namespace AgentLang.Llvm

open System
open System.Collections.Generic
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open AgentLang

[<Struct>]
type OwningStackFieldLayout =
    { FieldName: string
      FieldType: IrType
      OffsetBytes: int
      PayloadBytes: int
      ExtentBytes: int }

type OwningStackTypeLayout =
    { Type: IrType
      TypeName: string
      PayloadBytes: int
      ExtentBytes: int
      Fields: OwningStackFieldLayout list }

type OwningStackLayoutEvent =
    { Kind: string
      TypeId: uint32
      OffsetBytes: int
      ExtentBytes: int
      PayloadBytes: int
      SourceOffsetBytes: int option
      SourceExtentBytes: int option
      Checksum: uint64 option }

type OwningStackMetrics =
    { StackCapacityBytes: int
      RetainedCapacityBytes: int
      InputBytes: int
      InputCopyBytes: uint64
      ReservedStackBytes: int
      PeakLiveStackBytes: int
      ReservedLocalBytes: int
      PeakLiveLocalBytes: int
      DeepCopyBytes: uint64
      MoveBytes: uint64
      RetainedCopyBytes: uint64
      CursorInvariantChecks: int
      FrameReturnCount: int
      DuplicateDisjointChecks: int
      DropSurvivorChecks: int
      PoisonReuseChecks: int
      TraceEventCount: int
      TraceEventCapacity: int
      TraceTruncated: bool
      InstrumentationReservedBytes: int64
      HostInputStagingBytes: int
      HostEncodedInputBytes: int
      HostRetainedStagingBytes: int
      HostRetainedCommitBytes: int
      FinalCursorBytes: int
      FinalLiveStackBytes: int }

type OwningStackExecutionResult =
    { Values: Value list
      Metrics: OwningStackMetrics
      RetainedBytesWritten: int
      RetainedOutputBytes: byte array
      Layouts: OwningStackTypeLayout list
      LayoutEvents: OwningStackLayoutEvent list }

[<Sealed>]
type OwningStackCapacityException internal
    (code: string, boundary: string, requiredBytes: int64, availableBytes: int64, metrics: OwningStackMetrics) =
    inherit InvalidOperationException(
        $"{code}: owning stack {boundary} requires {requiredBytes} bytes; capacity is {availableBytes} bytes.")

    member _.Code = code
    member _.Boundary = boundary
    member _.RequiredBytes = requiredBytes
    member _.AvailableBytes = availableBytes
    member _.Metrics = metrics

[<Sealed>]
type OwningStackExecutionException internal (diagnostic: Diagnostic, metrics: OwningStackMetrics) =
    inherit Exception(diagnostic.Message)

    member _.Diagnostic = diagnostic
    member _.Metrics = metrics

[<Struct; StructLayout(LayoutKind.Sequential, Pack = 8)>]
type private NativeOwningEvent =
    val mutable Kind: uint32
    val mutable TypeId: uint32
    val mutable Offset: uint32
    val mutable ExtentBytes: uint32
    val mutable PayloadBytes: uint32
    val mutable SourceOffset: uint32
    val mutable SourceExtentBytes: uint32
    val mutable Flags: uint32
    val mutable Checksum: uint64

[<Struct; StructLayout(LayoutKind.Sequential, Pack = 8)>]
type internal NativeOwningContext =
    val mutable AbiVersion: uint32
    val mutable StackCapacityBytes: uint32
    val mutable CursorBytes: uint32
    val mutable PeakCursorBytes: uint32
    val mutable LivePayloadBytes: uint32
    val mutable PeakLivePayloadBytes: uint32
    val mutable ActiveLocalReservedBytes: uint32
    val mutable PeakLocalReservedBytes: uint32
    val mutable LiveLocalPayloadBytes: uint32
    val mutable PeakLiveLocalPayloadBytes: uint32
    val mutable StepsConsumed: uint32
    val mutable TraceEventCount: uint32
    val mutable TraceEventCapacity: uint32
    val mutable TraceTruncated: uint32
    val mutable DuplicateDisjointChecks: uint32
    val mutable DropSurvivorChecks: uint32
    val mutable PoisonReuseChecks: uint32
    val mutable CursorInvariantChecks: uint32
    val mutable FrameReturnCount: uint32
    val mutable Status: uint32
    val mutable ErrorId: uint32
    val mutable RequiredBytes: uint32
    val mutable AvailableBytes: uint32
    val mutable InitBitmapBytes: uint32
    val mutable CallDepth: uint32
    val mutable StackData: nativeint
    val mutable InitBitmap: nativeint
    val mutable PoisonBitmap: nativeint
    val mutable TraceEvents: nativeint
    val mutable DeepCopyBytes: uint64
    val mutable MoveBytes: uint64
    val mutable InputCopyBytes: uint64
    val mutable RetainedCopyBytes: uint64

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private OwningExecuteDelegate = delegate of nativeint * nativeint * uint32 * nativeint * uint32 -> int32

type internal OwningTypeInfo =
    { Type: IrType
      TypeId: uint32
      Name: string
      PayloadBytes: int
      ExtentBytes: int
      Fields: (string * IrType * int) list }

type internal OwningProgramInfo =
    { Program: IrProgram
      Body: IrExecutableBody
      ReachableFunctions: IrFunction list
      TypeIds: Map<IrType, uint32>
      TypeInfos: Map<IrType, OwningTypeInfo>
      Layouts: OwningStackTypeLayout list }

type internal OwningDiagnosticInfo =
    { Diagnostic: Diagnostic }

type private OwningLocalSlotPlan =
    { OffsetBytes: int
      ReservedBytes: int
      FlagId: int option }

type private OwningLocalEnvPlan =
    { BaseOffsetBytes: int
      OwnBytes: int
      TotalBytes: int
      Slots: Map<LocalSlot, OwningLocalSlotPlan>
      Scopes: Map<SourceSiteId, OwningLocalEnvPlan> }

type private OwningFunctionPlan =
    { LocalBytes: int
      RootEnvironment: OwningLocalEnvPlan
      Flags: int list }

type private OwningLocalBinding =
    { Slot: LocalSlot
      OffsetBytes: int
      ReservedBytes: int
      ActiveFlag: int option
      Fallback: OwningLocalBinding option }

type private OwningStackEntry =
    { Type: IrType
      RelativeOffset: int }

type private OwningLlvmWriter() =
    let output = StringBuilder()
    let mutable serial = 0
    member _.Line(value: string) = output.AppendLine(value) |> ignore
    member _.Inst(value: string) = output.Append("  ").AppendLine(value) |> ignore
    member _.Fresh(prefix: string) =
        let value = $"%%{prefix}.{serial}"
        serial <- serial + 1
        value
    member _.Label(prefix: string) =
        let value = $"{prefix}.{serial}"
        serial <- serial + 1
        value
    member _.Text = output.ToString()

[<Sealed>]
type OwningStackCompiledProgram internal
    (libraryPath: string,
     programInfo: OwningProgramInfo,
     diagnostics: OwningDiagnosticInfo array,
     llvmIr: string,
     encodeValues: OwningProgramInfo -> IrType list -> Value list -> byte array,
     decodeValues: OwningProgramInfo -> IrType list -> byte array -> Value list,
     readEvents: nativeint -> uint32 -> OwningStackLayoutEvent list,
     readMetrics: int -> int -> int -> int -> int -> int -> int -> int -> NativeOwningContext -> OwningStackMetrics,
     diagnosticForError: OwningDiagnosticInfo array -> uint32 -> Diagnostic) as this =
    let mutable libraryHandle = IntPtr.Zero
    let mutable executeDelegate: OwningExecuteDelegate option = None
    let lifetimeGate = obj ()

    do
        let handle = NativeLibrary.Load libraryPath
        try
            let address = NativeLibrary.GetExport(handle, "agentlang_owning_execute")
            executeDelegate <- Some(Marshal.GetDelegateForFunctionPointer<OwningExecuteDelegate>(address))
            libraryHandle <- handle
        with _ ->
            NativeLibrary.Free handle
            reraise ()

    member _.LibraryPath = libraryPath
    member _.LlvmIr = llvmIr
    member _.InputTypes = programInfo.Body.BodyInputTypes
    member _.OutputTypes = programInfo.Body.BodyOutputTypes
    member _.Layouts = programInfo.Layouts

    member private _.ExecuteIntoCore(inputs: Value list, stackByteCapacity: int, retainedOutput: byte array, retainedCapacityBytes: int) =
        if libraryHandle = IntPtr.Zero then raise (ObjectDisposedException(nameof OwningStackCompiledProgram))
        if stackByteCapacity < 0 then invalidArg (nameof stackByteCapacity) "Owning stack capacity cannot be negative."
        if retainedCapacityBytes < 0 || retainedCapacityBytes > retainedOutput.Length then
            invalidArg (nameof retainedCapacityBytes) "Retained capacity must be between zero and the provided byte-array length."
        if inputs.Length <> programInfo.Body.BodyInputTypes.Length then
            invalidArg (nameof inputs) $"Expected {programInfo.Body.BodyInputTypes.Length} input value(s), received {inputs.Length}."

        let inputBytes = encodeValues programInfo programInfo.Body.BodyInputTypes inputs
        let inputLength = inputBytes.Length
        let stackBitBytes = stackByteCapacity / 8 + (if stackByteCapacity % 8 = 0 then 0 else 1)
        let traceCapacity = 8192
        let stackAllocationBytes = max 1 stackByteCapacity
        let bitmapAllocationBytes = max 1 stackBitBytes
        let eventAllocationBytes = traceCapacity * Marshal.SizeOf<NativeOwningEvent>()
        let inputAllocationBytes = max 1 inputLength
        let contextBytes = Marshal.SizeOf<NativeOwningContext>()
        if IntPtr.Size <> 8 || contextBytes <> 168 || Marshal.SizeOf<NativeOwningEvent>() <> 40 then
            invalidOp "Owning-stack ABI1 requires the verified x64 context/event layout."

        let allocate (bytes: int) = Marshal.AllocHGlobal bytes
        let mutable contextPointer = IntPtr.Zero
        let mutable stackPointer = IntPtr.Zero
        let mutable initBitmapPointer = IntPtr.Zero
        let mutable poisonBitmapPointer = IntPtr.Zero
        let mutable tracePointer = IntPtr.Zero
        let mutable inputPointer = IntPtr.Zero
        let retainedStagingBytes = max 1 retainedCapacityBytes
        let mutable retainedStagePointer = IntPtr.Zero
        try
            contextPointer <- allocate contextBytes
            stackPointer <- allocate stackAllocationBytes
            initBitmapPointer <- allocate bitmapAllocationBytes
            poisonBitmapPointer <- allocate bitmapAllocationBytes
            tracePointer <- allocate eventAllocationBytes
            inputPointer <- allocate inputAllocationBytes
            retainedStagePointer <- allocate retainedStagingBytes
            if inputLength > 0 then Marshal.Copy(inputBytes, 0, inputPointer, inputLength)

            let retainedPointer = retainedStagePointer
            let mutable context = Unchecked.defaultof<NativeOwningContext>
            context.AbiVersion <- 1u
            context.StackCapacityBytes <- uint32 stackByteCapacity
            context.TraceEventCapacity <- uint32 traceCapacity
            context.InitBitmapBytes <- uint32 stackBitBytes
            context.StackData <- stackPointer
            context.InitBitmap <- initBitmapPointer
            context.PoisonBitmap <- poisonBitmapPointer
            context.TraceEvents <- tracePointer
            Marshal.StructureToPtr(context, contextPointer, false)

            let status =
                lock lifetimeGate (fun () ->
                    if libraryHandle = IntPtr.Zero then raise (ObjectDisposedException(nameof OwningStackCompiledProgram))
                    match executeDelegate with
                    | Some native -> native.Invoke(contextPointer, inputPointer, uint32 inputLength, retainedPointer, uint32 retainedCapacityBytes)
                    | None -> raise (ObjectDisposedException(nameof OwningStackCompiledProgram)))
            context <- Marshal.PtrToStructure<NativeOwningContext>(contextPointer)
            let metrics = readMetrics stackByteCapacity retainedCapacityBytes inputLength traceCapacity eventAllocationBytes stackBitBytes retainedStagingBytes 0 context
            let events = readEvents tracePointer context.TraceEventCount

            if status <> 0 || context.Status <> 0u then
                match context.Status with
                | 2u ->
                    raise (OwningStackCapacityException(
                        "OWNING_STACK_CAPACITY", "program-data-stack",
                        int64 context.RequiredBytes, int64 context.AvailableBytes, metrics))
                | 3u ->
                    raise (OwningStackCapacityException(
                        "OWNING_RETAINED_CAPACITY", "retained-output",
                        int64 context.RequiredBytes, int64 context.AvailableBytes, metrics))
                | 1u ->
                    let diagnostic = diagnosticForError diagnostics context.ErrorId
                    raise (OwningStackExecutionException(diagnostic, metrics))
                | _ ->
                    let diagnostic =
                        { Code = "OWNING_STACK_INTERNAL"
                          Message = "The owning-stack runtime rejected an invalid or inconsistent native request."
                          Word = Some programInfo.Body.BodyName
                          Span = None
                          Expected = [ "valid bounded owning-stack context" ]
                          Actual = [ sprintf "status=%u; error=%u" context.Status context.ErrorId ] }
                    raise (OwningStackExecutionException(diagnostic, metrics))

            let writtenBytes = int context.RetainedCopyBytes
            if writtenBytes > retainedCapacityBytes || writtenBytes > retainedOutput.Length then
                raise (InvalidDataException("Native owning-stack output exceeded its bounded staging or caller buffer."))
            let resultBytes = Array.zeroCreate<byte> writtenBytes
            if writtenBytes > 0 then Marshal.Copy(retainedStagePointer, resultBytes, 0, writtenBytes)
            let values = decodeValues programInfo programInfo.Body.BodyOutputTypes resultBytes
            if writtenBytes > 0 then Array.Copy(resultBytes, 0, retainedOutput, 0, writtenBytes)
            let committedMetrics = { metrics with HostRetainedCommitBytes = writtenBytes }
            { Values = values
              Metrics = committedMetrics
              RetainedBytesWritten = writtenBytes
              RetainedOutputBytes = resultBytes
              Layouts = programInfo.Layouts
              LayoutEvents = events }
        finally
            if retainedStagePointer <> IntPtr.Zero then Marshal.FreeHGlobal retainedStagePointer
            if inputPointer <> IntPtr.Zero then Marshal.FreeHGlobal inputPointer
            if tracePointer <> IntPtr.Zero then Marshal.FreeHGlobal tracePointer
            if poisonBitmapPointer <> IntPtr.Zero then Marshal.FreeHGlobal poisonBitmapPointer
            if initBitmapPointer <> IntPtr.Zero then Marshal.FreeHGlobal initBitmapPointer
            if stackPointer <> IntPtr.Zero then Marshal.FreeHGlobal stackPointer
            if contextPointer <> IntPtr.Zero then Marshal.FreeHGlobal contextPointer

    member this.ExecuteInto(inputs: Value list, stackByteCapacity: int, retainedOutput: byte array) =
        this.ExecuteIntoCore(inputs, stackByteCapacity, retainedOutput, retainedOutput.Length)

    member this.ExecuteInto(inputs: Value list, stackByteCapacity: int, retainedOutput: byte array, retainedCapacityBytes: int) =
        this.ExecuteIntoCore(inputs, stackByteCapacity, retainedOutput, retainedCapacityBytes)

    member this.Execute(inputs: Value list, stackByteCapacity: int, retainedByteCapacity: int) =
        if retainedByteCapacity < 0 then invalidArg (nameof retainedByteCapacity) "Retained capacity cannot be negative."
        this.ExecuteInto(inputs, stackByteCapacity, Array.zeroCreate<byte> retainedByteCapacity)

    member private _.DisposeCore() =
        lock lifetimeGate (fun () ->
            if libraryHandle <> IntPtr.Zero then
                NativeLibrary.Free libraryHandle
                libraryHandle <- IntPtr.Zero
                executeDelegate <- None)

    interface IDisposable with
        member _.Dispose() = this.DisposeCore()

[<RequireQualifiedAccess>]
module OwningStackAot =
    let private unsupported code message owner actual =
        Diagnostics.raiseError code message (Some owner) None [ "Int | Bool | Unit | acyclic inline record" ] [ actual ]

    let private typeIdFor (ids: Map<IrType, uint32>) ty =
        ids.TryFind ty |> Option.defaultWith (fun () -> invalidOp $"Owning-stack type id missing for {IrTypes.format ty}.")

    let private typeName program ty =
        match ty with
        | IrInt -> "Int"
        | IrBool -> "Bool"
        | IrUnit -> "Unit"
        | IrNominal key ->
            match program.NominalTypesByKey.TryFind key with
            | Some(IrRecordDefinition record) -> record.TypeName
            | Some(IrScalarDefinition scalar) -> scalar.TypeName
            | Some(IrEnumDefinition enumDefinition) -> enumDefinition.TypeName
            | None -> sprintf "%A" key
        | other -> IrTypes.format other

    let private extent payload = max 8 payload

    let private makeProgramInfo (verifiedBody: VerifiedIrBody) =
        let checkedBody =
            let sourceProgram = VerifiedIrBody.program verifiedBody
            VerifiedIrProgram.requireBackendRegistry Compiler.primitiveIrCatalog sourceProgram
            IrVerifier.verifyBody sourceProgram (VerifiedIrBody.inspect verifiedBody)
        let program = VerifiedIrProgram.inspect (VerifiedIrBody.program checkedBody)
        let body = VerifiedIrBody.inspect checkedBody

        let requireEffectFree owner effects =
            if not (Set.isEmpty effects) then
                Diagnostics.raiseError "IR_OWNING_STACK_EFFECT_UNSUPPORTED"
                    "The owning value-stack backend accepts only effect-free verified code."
                    (Some owner) None [] (IrEffects.names effects)

        let typeIds =
            ([ IrInt, 1u; IrBool, 2u; IrUnit, 3u ]
             @ (program.NominalTypesByKey
                |> Map.toList
                |> List.mapi (fun index (key, _) -> IrNominal key, uint32 (index + 4))))
            |> Map.ofList
        let mutable typeInfos = Map.empty<IrType, OwningTypeInfo>
        let active = HashSet<IrType>()
        let rec buildType owner ty =
            match typeInfos.TryFind ty with
            | Some value -> value
            | None ->
                match ty with
                | IrInt | IrBool | IrUnit ->
                    let value =
                        { Type = ty
                          TypeId = typeIdFor typeIds ty
                          Name = IrTypes.format ty
                          PayloadBytes = 8
                          ExtentBytes = 8
                          Fields = [] }
                    typeInfos <- Map.add ty value typeInfos
                    value
                | IrNominal key ->
                    if not (active.Add ty) then
                        unsupported "IR_OWNING_STACK_RECURSIVE_RECORD" "Acyclic inline records cannot contain recursive type cycles." owner (IrTypes.format ty)
                    try
                        match program.NominalTypesByKey.TryFind key with
                        | Some(IrRecordDefinition record) ->
                            match record.ValidatorCall with
                            | Some validator ->
                                Diagnostics.raiseError "IR_OWNING_STACK_RECORD_VALIDATOR_UNSUPPORTED"
                                    "Record validators are outside the fixed-layout owning-stack slice."
                                    (Some owner) None [] [ validator.ResolvedName ]
                            | None -> ()
                            let children =
                                record.RecordFields
                                |> List.sortBy (fun field -> field.FieldIndex)
                                |> List.map (fun field -> field.FieldName, field.FieldType, (buildType owner field.FieldType).PayloadBytes)
                            let fields, payload =
                                let mutable offset = 0L
                                let rows =
                                    children
                                    |> List.map (fun (name, fieldType, fieldPayload) ->
                                        if fieldPayload < 0 then invalidOp "Verified type layout produced a negative field size."
                                        if offset > int64 Int32.MaxValue then
                                            unsupported "IR_OWNING_STACK_LAYOUT_TOO_LARGE" "Inline record layout exceeds the bounded runtime range." owner (IrTypes.format ty)
                                        let row = name, fieldType, int offset
                                        offset <- Checked.(+) offset (int64 fieldPayload)
                                        row)
                                if offset > int64 Int32.MaxValue then
                                    unsupported "IR_OWNING_STACK_LAYOUT_TOO_LARGE" "Inline record layout exceeds the bounded runtime range." owner (IrTypes.format ty)
                                rows, int offset
                            let value =
                                { Type = ty
                                  TypeId = typeIdFor typeIds ty
                                  Name = record.TypeName
                                  PayloadBytes = payload
                                  ExtentBytes = extent payload
                                  Fields = fields }
                            typeInfos <- Map.add ty value typeInfos
                            value
                        | Some(IrScalarDefinition scalar) ->
                            unsupported "IR_OWNING_STACK_TYPE_UNSUPPORTED" "Nominal scalar wrappers are not part of this record-only slice." owner scalar.TypeName
                        | Some(IrEnumDefinition enumDefinition) ->
                            unsupported "IR_OWNING_STACK_TYPE_UNSUPPORTED" "Nominal enums are not part of this record-only slice." owner enumDefinition.TypeName
                        | None -> unsupported "IR_OWNING_STACK_TYPE_UNKNOWN" "Owning-stack type is absent from the verified nominal table." owner (IrTypes.format ty)
                    finally
                        active.Remove ty |> ignore
                | other -> unsupported "IR_OWNING_STACK_TYPE_UNSUPPORTED" "Owning-stack backend does not support this verified value type." owner (IrTypes.format other)

        let rec checkType owner ty = buildType owner ty |> ignore
        let supportedPrimitives =
            set [ "add"; "subtract"; "multiply"; "divide"
                  "int.less-than"; "int.greater-than"; "int.less-or-equal"; "int.greater-or-equal"
                  "equals"; "bool.and"; "bool.or"; "bool.not"; "dup"; "drop"; "swap" ]

        let validatePrimitive owner span (call: IrResolvedCall) operation =
            requireEffectFree owner call.ResolvedDeclaredEffects
            requireEffectFree owner call.ResolvedEffects
            if not (supportedPrimitives.Contains operation) then
                Diagnostics.raiseError "IR_OWNING_STACK_PRIMITIVE_UNSUPPORTED"
                    "Owning-stack backend has no native implementation for this canonical primitive."
                    (Some owner) span (supportedPrimitives |> Set.toList) [ operation ]
            call.InputTypes @ call.OutputTypes |> List.iter (checkType owner)
            let valid =
                match operation, call.InputTypes, call.OutputTypes with
                | ("add" | "subtract" | "multiply" | "divide"), [ IrInt; IrInt ], [ IrInt ] -> true
                | ("int.less-than" | "int.greater-than" | "int.less-or-equal" | "int.greater-or-equal"), [ IrInt; IrInt ], [ IrBool ] -> true
                | "equals", [ left; right ], [ IrBool ] -> left = right
                | "bool.and", [ IrBool; IrBool ], [ IrBool ] -> true
                | "bool.or", [ IrBool; IrBool ], [ IrBool ] -> true
                | "bool.not", [ IrBool ], [ IrBool ] -> true
                | "dup", [ value ], [ first; second ] -> first = value && second = value
                | "drop", [ _ ], [] -> true
                | "swap", [ first; second ], [ secondResult; firstResult ] -> second = secondResult && first = firstResult
                | _ -> false
            if not valid then
                Diagnostics.raiseError "IR_OWNING_STACK_PRIMITIVE_SIGNATURE"
                    "The concrete primitive specialization does not match the owning-stack implementation."
                    (Some owner) span [ $"supported {operation} signature" ]
                    [ String.concat " " (call.InputTypes |> List.map IrTypes.format) + " -> " + String.concat " " (call.OutputTypes |> List.map IrTypes.format) ]

        let sourceMap = Map.fold (fun merged site source -> Map.add site source merged)
                            (VerifiedIrProgram.inspect (VerifiedIrBody.program checkedBody)).SourceMap body.BodySourceMap
        let rec validateBlock owner (block: IrBlock) =
            block.EntryShape.StackTypes @ (block.EntryShape.LocalTypes |> Map.toList |> List.map snd) @ block.ExitShape.StackTypes @ (block.ExitShape.LocalTypes |> Map.toList |> List.map snd)
            |> List.iter (checkType owner)
            for instruction in block.Code do
                let span = sourceMap.TryFind instruction.Site |> Option.map (fun source -> source.SiteSpan)
                match instruction.Operation with
                | IrOperation.Constant(LInt _, IrInt)
                | IrOperation.Constant(LBool _, IrBool)
                | IrOperation.Constant(LUnit, IrUnit) -> ()
                | IrOperation.Constant(literal, ty) ->
                    Diagnostics.raiseError "IR_OWNING_STACK_CONSTANT_UNSUPPORTED"
                        "Owning-stack backend supports Int, Bool, and Unit constants only."
                        (Some owner) span [ "Int"; "Bool"; "Unit" ] [ sprintf "%A : %s" literal (IrTypes.format ty) ]
                | IrOperation.Call call ->
                    match call.ResolvedTarget with
                    | PrimitiveTarget(PrimitiveId operation) -> validatePrimitive owner span call operation
                    | UserWordTarget _ ->
                        requireEffectFree owner call.ResolvedDeclaredEffects
                        requireEffectFree owner call.ResolvedEffects
                        call.InputTypes @ call.OutputTypes |> List.iter (checkType owner)
                    | GeneratedWordTarget _ ->
                        Diagnostics.raiseError "IR_OWNING_STACK_GENERATED_CALL_UNSUPPORTED"
                            "Generated calls are supported only through the verified MakeRecord/GetRecordField operations."
                            (Some owner) span [] [ call.ResolvedName ]
                | IrOperation.MakeRecord(call, key, validator) ->
                    checkType owner (IrNominal key)
                    validateRecordCall owner span call key
                    if validator.IsSome then
                        Diagnostics.raiseError "IR_OWNING_STACK_RECORD_VALIDATOR_UNSUPPORTED"
                            "Record validators are outside the fixed-layout owning-stack slice."
                            (Some owner) span [] [ call.ResolvedName ]
                | IrOperation.GetRecordField(call, key, index) ->
                    checkType owner (IrNominal key)
                    validateAccessorCall owner span call key index
                | IrOperation.StoreLocal _ | IrOperation.LoadLocal _ -> ()
                | IrOperation.Scope inner -> validateBlock owner inner
                | IrOperation.If(thenBlock, elseBlock) -> validateBlock owner thenBlock; validateBlock owner elseBlock
                | operation ->
                    Diagnostics.raiseError "IR_OWNING_STACK_OPERATION_UNSUPPORTED"
                        "Owning-stack backend supports constants, calls, records, locals, Scope, and If only."
                        (Some owner) span [ "Constant"; "Call"; "MakeRecord"; "GetRecordField"; "StoreLocal"; "LoadLocal"; "Scope"; "If" ]
                        [ sprintf "%A" operation ]

        and validateRecordCall owner span (call: IrResolvedCall) key =
            match call.ResolvedTarget with
            | GeneratedWordTarget(id, revision) ->
                match program.GeneratedTargetsById.TryFind id with
                | Some target when target.TargetRevision = revision && target.Operation = MakeRecordOperation key ->
                    requireEffectFree owner target.TargetDeclaredEffects
                    requireEffectFree owner target.TargetEffects
                | _ -> Diagnostics.raiseError "IR_OWNING_STACK_RECORD_TARGET" "MakeRecord is not bound to its verified matching record constructor." (Some owner) span [ sprintf "%A" (MakeRecordOperation key) ] [ call.ResolvedName ]
            | _ -> Diagnostics.raiseError "IR_OWNING_STACK_RECORD_TARGET" "MakeRecord requires its verified generated record constructor target." (Some owner) span [ "generated record constructor" ] [ call.ResolvedName ]

        and validateAccessorCall owner span (call: IrResolvedCall) key fieldIndex =
            match call.ResolvedTarget with
            | GeneratedWordTarget(id, revision) ->
                match program.GeneratedTargetsById.TryFind id with
                | Some target when target.TargetRevision = revision && target.Operation = GetRecordFieldOperation(key, fieldIndex) ->
                    requireEffectFree owner target.TargetDeclaredEffects
                    requireEffectFree owner target.TargetEffects
                | _ -> Diagnostics.raiseError "IR_OWNING_STACK_FIELD_TARGET" "GetRecordField is not bound to its verified matching record accessor." (Some owner) span [ sprintf "%A" (GetRecordFieldOperation(key, fieldIndex)) ] [ call.ResolvedName ]
            | _ -> Diagnostics.raiseError "IR_OWNING_STACK_FIELD_TARGET" "GetRecordField requires its verified generated record accessor target." (Some owner) span [ "generated record accessor" ] [ call.ResolvedName ]

        requireEffectFree body.BodyName body.BodyDeclaredEffects
        requireEffectFree body.BodyName body.BodyInferredEffects
        body.BodyInputTypes @ body.BodyOutputTypes |> List.iter (checkType body.BodyName)
        validateBlock body.BodyName body.BodyBlock

        let reachable = HashSet<WordId>()
        let pending = Queue<IrFunction>()
        let rec inspectCalls owner (block: IrBlock) =
            for instruction in block.Code do
                match instruction.Operation with
                | IrOperation.Call call ->
                    match call.ResolvedTarget with
                    | UserWordTarget(id, revision) ->
                        match program.FunctionsById.TryFind id with
                        | Some fn when fn.FunctionRevision = revision && reachable.Add id -> pending.Enqueue fn
                        | Some fn when fn.FunctionRevision = revision -> ()
                        | Some fn -> Diagnostics.raiseError "IR_OWNING_STACK_TARGET_REVISION" "Owning-stack call target revision does not match the verified snapshot." (Some owner) None [ string revision ] [ string fn.FunctionRevision ]
                        | None -> Diagnostics.raiseError "IR_OWNING_STACK_TARGET_MISSING" "Owning-stack call target is absent from the verified snapshot." (Some owner) None [ "reachable user word" ] [ sprintf "%A" id ]
                    | _ -> ()
                | IrOperation.Scope inner -> inspectCalls owner inner
                | IrOperation.If(left, right) -> inspectCalls owner left; inspectCalls owner right
                | _ -> ()
        and checkFunction (fn: IrFunction) =
            requireEffectFree fn.FunctionName fn.FunctionDeclaredEffects
            requireEffectFree fn.FunctionName fn.FunctionInferredEffects
            fn.InputTypes @ fn.OutputTypes |> List.iter (checkType fn.FunctionName)
            validateBlock fn.FunctionName fn.FunctionBody
            inspectCalls fn.FunctionName fn.FunctionBody
        inspectCalls body.BodyName body.BodyBlock
        while pending.Count > 0 do checkFunction (pending.Dequeue())
        let typeInfos = typeInfos
        let typeLayouts =
            typeInfos
            |> Map.toList
            |> List.map (fun (_, info) ->
                { Type = info.Type
                  TypeName = info.Name
                  PayloadBytes = info.PayloadBytes
                  ExtentBytes = info.ExtentBytes
                  Fields =
                    info.Fields
                    |> List.map (fun (fieldName, fieldType, offset) ->
                        let field = typeInfos[fieldType]
                        { FieldName = fieldName
                          FieldType = fieldType
                          OffsetBytes = offset
                          PayloadBytes = field.PayloadBytes
                          ExtentBytes = if field.PayloadBytes = 0 then 0 else field.ExtentBytes }) })
            |> List.sortBy (fun layout -> typeIdFor typeIds layout.Type)
        let reachableFunctions =
            program.FunctionsById
            |> Map.toList
            |> List.choose (fun (id, fn) -> if reachable.Contains id then Some fn else None)
        { Program = program
          Body = body
          ReachableFunctions = reachableFunctions
          TypeIds = typeIds
          TypeInfos = typeInfos
          Layouts = typeLayouts }

    let private infoFor (info: OwningProgramInfo) ty =
        info.TypeInfos.TryFind ty
        |> Option.defaultWith (fun () -> invalidOp $"Owning-stack layout was not built for {IrTypes.format ty}.")

    let private writeInt64 (bytes: byte array) offset (value: int64) =
        let bits = uint64 value
        for index in 0 .. 7 do bytes[offset + index] <- byte (bits >>> (index * 8))

    let private addBoundedByteOffset offset relative =
        let result = Checked.(+) (int64 offset) (int64 relative)
        if result < 0L || result > int64 Int32.MaxValue then
            invalidArg (nameof relative) "Owning-stack byte offset exceeds the bounded runtime range."
        int result

    let private readInt64 (bytes: byte array) offset =
        let mutable bits = 0UL
        for index in 0 .. 7 do bits <- bits ||| (uint64 bytes[offset + index] <<< (index * 8))
        int64 bits

    let private encodeValues (info: OwningProgramInfo) (types: IrType list) (values: Value list) =
        if types.Length <> values.Length then invalidArg (nameof values) "Input value count differs from verified signature."
        let totalBytes =
            types
            |> List.fold (fun total ty ->
                let size = int64 (infoFor info ty).ExtentBytes
                let next = Checked.(+) (int64 total) size
                if next > int64 Int32.MaxValue then invalidArg (nameof types) "Encoded owning-stack values exceed the supported bounded input range."
                int next) 0
        let bytes = Array.zeroCreate<byte> totalBytes
        let rec encode offset ty value =
            let layout = infoFor info ty
            match ty, value with
            | IrInt, IntValue number -> writeInt64 bytes offset number
            | IrBool, BoolValue flag -> writeInt64 bytes offset (if flag then 1L else 0L)
            | IrUnit, UnitValue -> writeInt64 bytes offset 0L
            | IrNominal _, RecordValue(name, fields) when name = layout.Name ->
                let expectedNames = layout.Fields |> List.map (fun (fieldName, _, _) -> fieldName) |> Set.ofList
                if fields |> Map.toSeq |> Seq.map fst |> Set.ofSeq <> expectedNames then
                    invalidArg (nameof values) $"Record input '{name}' has fields that differ from its verified layout."
                for fieldName, fieldType, fieldOffset in layout.Fields do
                    encode (addBoundedByteOffset offset fieldOffset) fieldType fields[fieldName]
            | _ ->
                invalidArg (nameof values) $"Input value {Types.formatValue value} does not match verified type {IrTypes.format ty}."
        let mutable offset = 0
        for ty, value in List.zip types values do
            encode offset ty value
            offset <- Checked.(+) offset (infoFor info ty).ExtentBytes
        bytes

    let private decodeValues (info: OwningProgramInfo) (types: IrType list) (bytes: byte array) =
        let expectedBytes =
            types
            |> List.fold (fun total ty ->
                let next = Checked.(+) (int64 total) (int64 (infoFor info ty).ExtentBytes)
                if next > int64 Int32.MaxValue then invalidArg (nameof types) "Decoded owning-stack values exceed the supported bounded output range."
                int next) 0
        if bytes.Length < expectedBytes then invalidArg (nameof bytes) "Retained bytes are shorter than the verified output signature."
        let rec decode offset ty =
            let layout = infoFor info ty
            match ty with
            | IrInt -> IntValue(readInt64 bytes offset)
            | IrBool ->
                match readInt64 bytes offset with
                | 0L -> BoolValue false
                | 1L -> BoolValue true
                | value -> raise (InvalidDataException($"Owning-stack Bool was not encoded as 0 or 1: {value}."))
            | IrUnit ->
                if readInt64 bytes offset <> 0L then raise (InvalidDataException("Owning-stack Unit token was not zeroed."))
                UnitValue
            | IrNominal _ ->
                let fields =
                    layout.Fields
                    |> List.map (fun (name, fieldType, fieldOffset) -> name, decode (addBoundedByteOffset offset fieldOffset) fieldType)
                    |> Map.ofList
                RecordValue(layout.Name, fields)
            | unsupported -> invalidOp $"Unsupported output type reached owning-stack decode: {IrTypes.format unsupported}."
        let mutable offset = 0
        types
        |> List.map (fun ty ->
            let value = decode offset ty
            offset <- Checked.(+) offset (infoFor info ty).ExtentBytes
            value)

    let private eventKind = function
        | 1u -> "allocate"
        | 2u -> "duplicate"
        | 3u -> "drop"
        | 4u -> "local-store"
        | 5u -> "local-load"
        | 6u -> "record-build"
        | 7u -> "field-extract"
        | 8u -> "call-input-move"
        | 9u -> "call-return-move"
        | 10u -> "retained-copy"
        | 11u -> "frame-enter"
        | 12u -> "frame-return"
        | 13u -> "scope-clear"
        | other -> $"event-{other}"

    let private readEvents tracePointer eventCount =
        Array.init (int eventCount) (fun index ->
            let nativeEvent = Marshal.PtrToStructure<NativeOwningEvent>(IntPtr.Add(tracePointer, index * Marshal.SizeOf<NativeOwningEvent>()))
            { Kind = eventKind nativeEvent.Kind
              TypeId = nativeEvent.TypeId
              OffsetBytes = int nativeEvent.Offset
              ExtentBytes = int nativeEvent.ExtentBytes
              PayloadBytes = int nativeEvent.PayloadBytes
              SourceOffsetBytes = if nativeEvent.SourceExtentBytes = 0u then None else Some(int nativeEvent.SourceOffset)
              SourceExtentBytes = if nativeEvent.SourceExtentBytes = 0u then None else Some(int nativeEvent.SourceExtentBytes)
              Checksum = if nativeEvent.Checksum = 0UL then None else Some nativeEvent.Checksum })
        |> Array.toList

    let private readMetrics stackCapacity retainedCapacity inputBytes traceCapacity traceBytes bitmapBytes hostStageBytes hostCommitBytes (context: NativeOwningContext) =
        { StackCapacityBytes = stackCapacity
          RetainedCapacityBytes = retainedCapacity
          InputBytes = inputBytes
          InputCopyBytes = context.InputCopyBytes
          ReservedStackBytes = int context.PeakCursorBytes
          PeakLiveStackBytes = int context.PeakLivePayloadBytes
          ReservedLocalBytes = int context.PeakLocalReservedBytes
          PeakLiveLocalBytes = int context.PeakLiveLocalPayloadBytes
          DeepCopyBytes = context.DeepCopyBytes
          MoveBytes = context.MoveBytes
          RetainedCopyBytes = context.RetainedCopyBytes
          CursorInvariantChecks = int context.CursorInvariantChecks
          FrameReturnCount = int context.FrameReturnCount
          DuplicateDisjointChecks = int context.DuplicateDisjointChecks
          DropSurvivorChecks = int context.DropSurvivorChecks
          PoisonReuseChecks = int context.PoisonReuseChecks
          TraceEventCount = int context.TraceEventCount
          TraceEventCapacity = traceCapacity
          TraceTruncated = context.TraceTruncated <> 0u
          InstrumentationReservedBytes = int64 (Marshal.SizeOf<NativeOwningContext>()) + int64 bitmapBytes * 2L + int64 traceBytes
          HostInputStagingBytes = max 1 inputBytes
          HostEncodedInputBytes = inputBytes
          HostRetainedStagingBytes = hostStageBytes
          HostRetainedCommitBytes = hostCommitBytes
          FinalCursorBytes = int context.CursorBytes
          FinalLiveStackBytes = int context.LivePayloadBytes + int context.LiveLocalPayloadBytes }

    let private diagnosticForError (diagnostics: OwningDiagnosticInfo array) errorId =
        if errorId = 0u || int errorId > diagnostics.Length then
            { Code = "OWNING_STACK_DIAGNOSTIC_UNKNOWN"
              Message = "Owning-stack execution reported an unmapped diagnostic id."
              Word = None
              Span = None
              Expected = [ "mapped owning-stack runtime diagnostic" ]
              Actual = [ string errorId ] }
        else diagnostics[int errorId - 1].Diagnostic

    let private slotValue (LocalSlot value) = value

    let private blockStores (block: IrBlock) =
        let values = Dictionary<LocalSlot, ResizeArray<IrType>>()
        let rec visit (current: IrBlock) =
            let mutable stack = current.EntryShape.StackTypes
            let mutable locals = current.EntryShape.LocalTypes
            let pop count =
                if count = 0 then [], stack
                elif stack.Length < count then invalidOp "Verified block has a stack underflow while planning local storage."
                else stack |> List.skip (stack.Length - count), stack |> List.take (stack.Length - count)
            for instruction in current.Code do
                match instruction.Operation with
                | IrOperation.Constant(_, ty) -> stack <- stack @ [ ty ]
                | IrOperation.Call call ->
                    let _, prefix = pop call.InputTypes.Length
                    stack <- prefix @ call.OutputTypes
                | IrOperation.MakeRecord(call, _, _) ->
                    let _, prefix = pop call.InputTypes.Length
                    stack <- prefix @ call.OutputTypes
                | IrOperation.GetRecordField(call, _, _) ->
                    let _, prefix = pop call.InputTypes.Length
                    stack <- prefix @ call.OutputTypes
                | IrOperation.StoreLocal slot ->
                    match List.rev stack with
                    | ty :: rest ->
                        match values.TryGetValue slot with
                        | true, seen -> seen.Add ty
                        | false, _ -> values.Add(slot, ResizeArray([ ty ]))
                        stack <- List.rev rest
                        locals <- Map.add slot ty locals
                    | [] -> invalidOp "Verified StoreLocal has no stack operand during local planning."
                | IrOperation.LoadLocal slot ->
                    let ty = locals.TryFind slot |> Option.defaultWith (fun () -> invalidOp "Verified LoadLocal lacks its local type during local planning.")
                    stack <- stack @ [ ty ]
                | IrOperation.Scope inner ->
                    let localsBeforeScope = locals
                    stack <- inner.ExitShape.StackTypes
                    locals <- localsBeforeScope
                | IrOperation.If(left, right) ->
                    let _ , _ = pop 1
                    visit left
                    visit right
                    stack <- left.ExitShape.StackTypes
                    locals <- left.ExitShape.LocalTypes
                    if stack <> right.ExitShape.StackTypes || locals <> right.ExitShape.LocalTypes then
                        invalidOp "Verified If branches diverged while planning local storage."
                | _ -> invalidOp "Unsupported operation reached owning local-storage planning after validation."
        visit block
        values
        |> Seq.map (fun pair -> pair.Key, List.ofSeq pair.Value)
        |> Map.ofSeq

    let private directScopes (block: IrBlock) =
        let found = ResizeArray<SourceSiteId * IrBlock>()
        let rec visit (current: IrBlock) =
            for instruction in current.Code do
                match instruction.Operation with
                | IrOperation.Scope inner -> found.Add(instruction.Site, inner)
                | IrOperation.If(left, right) -> visit left; visit right
                | _ -> ()
        visit block
        List.ofSeq found

    let private buildFunctionPlan (info: OwningProgramInfo) (block: IrBlock) =
        let mutable nextFlag = 0
        let rec plan (isRoot: bool) (baseOffset: int) (current: IrBlock) =
            let stores = blockStores current
            let mutable offset = baseOffset
            let slots =
                stores
                |> Map.toList
                |> List.sortBy (fst >> slotValue)
                |> List.map (fun (slot, types) ->
                    let reserved = types |> List.map (fun ty -> (infoFor info ty).ExtentBytes) |> List.max
                    let id = nextFlag
                    nextFlag <- nextFlag + 1
                    let flag = Some id
                    let value = slot, { OffsetBytes = offset; ReservedBytes = reserved; FlagId = flag }
                    let next = Checked.(+) (int64 offset) (int64 reserved)
                    if next > int64 Int32.MaxValue then invalidOp "Owning local frame exceeds the bounded runtime range."
                    offset <- int next
                    value)
                |> Map.ofList
            let ownBytes = offset - baseOffset
            let childPlans =
                directScopes current
                |> List.map (fun (site, inner) ->
                    let childBase = Checked.(+) (int64 baseOffset) (int64 ownBytes)
                    if childBase > int64 Int32.MaxValue then invalidOp "Owning local frame exceeds the bounded runtime range."
                    site, plan false (int childBase) inner)
            let childBytes = childPlans |> List.map (snd >> fst >> fun child -> child.TotalBytes) |> List.fold max 0
            let scopes = childPlans |> List.map (fun (site, (child, _)) -> site, child) |> Map.ofList
            let totalBytes = Checked.(+) (int64 ownBytes) (int64 childBytes)
            if totalBytes > int64 Int32.MaxValue then invalidOp "Owning local frame exceeds the bounded runtime range."
            let env =
                { BaseOffsetBytes = baseOffset
                  OwnBytes = ownBytes
                  TotalBytes = int totalBytes
                  Slots = slots
                  Scopes = scopes }
            let childFlags = childPlans |> List.collect (snd >> snd)
            let ownFlags = slots |> Map.toList |> List.choose (fun (_, slotPlan) -> slotPlan.FlagId)
            env, (ownFlags @ childFlags)
        let root, flags = plan true 0 block
        { LocalBytes = root.TotalBytes
          RootEnvironment = root
          Flags = flags |> List.distinct |> List.sort }

    let private checkedBoundedAdd operation left right =
        let value = Checked.(+) (int64 left) (int64 right)
        if value < 0L || value > int64 Int32.MaxValue then
            invalidOp $"Owning-stack {operation} exceeds the bounded x64 runtime range."
        int value

    let private stackBytes (info: OwningProgramInfo) (entries: OwningStackEntry list) =
        entries
        |> List.fold (fun total entry -> checkedBoundedAdd "operand stack" total (infoFor info entry.Type).ExtentBytes) 0

    let private emitModule (info: OwningProgramInfo) =
        let body = info.Body
        let program = info.Program
        let sourceMap =
            Map.fold (fun merged site source -> Map.add site source merged) program.SourceMap body.BodySourceMap
        let diagnostics = ResizeArray<OwningDiagnosticInfo>()
        let addDiagnostic code message word span expected actual =
            let item =
                { Code = code
                  Message = message
                  Word = Some word
                  Span = span
                  Expected = expected
                  Actual = actual }
            diagnostics.Add { Diagnostic = item }
            uint32 diagnostics.Count
        let spanFor site = sourceMap.TryFind site |> Option.map (fun source -> source.SiteSpan)
        let functionSymbols =
            info.ReachableFunctions
            |> List.sortBy (fun fn -> fn.FunctionName, fn.FunctionRevision)
            |> List.mapi (fun index fn -> fn.FunctionId, $"@agentlang_word_{index:D4}")
            |> Map.ofList
        let symbolFor id =
            let idText = sprintf "%A" id
            functionSymbols.TryFind id
            |> Option.defaultWith (fun () -> invalidOp $"Validated owning-stack call target {idText} has no LLVM symbol.")
        let writer = OwningLlvmWriter()
        let emitContextFieldPointer (w: OwningLlvmWriter) (context: string) fieldIndex =
            let pointer = w.Fresh "context.field"
            w.Inst($"{pointer} = getelementptr inbounds %%AlOwningContext, ptr {context}, i32 0, i32 {fieldIndex}")
            pointer
        let emitContextLoad (w: OwningLlvmWriter) context fieldIndex =
            let pointer = emitContextFieldPointer w context fieldIndex
            let value = w.Fresh "context.value"
            w.Inst($"{value} = load i32, ptr {pointer}, align 4")
            value
        let emitContextStore (w: OwningLlvmWriter) context fieldIndex value =
            let pointer = emitContextFieldPointer w context fieldIndex
            w.Inst($"store i32 {value}, ptr {pointer}, align 4")
        let emitOffset (w: OwningLlvmWriter) (baseValue: string) relative =
            // Context cursors and previously checked live offsets are at most the configured
            // Int32 stack capacity; every planned relative is also Int32-bounded. Their sum
            // therefore fits uint32, and any new end is reserved before it can be dereferenced.
            if relative = 0 then baseValue
            else
                let offset = w.Fresh "stack.offset"
                w.Inst($"{offset} = add i32 {baseValue}, {relative}")
                offset
        let emitPointerOffset (w: OwningLlvmWriter) (baseValue: string) relative =
            if relative = 0 then baseValue
            else
                let offset = w.Fresh "input.pointer"
                w.Inst($"{offset} = getelementptr inbounds i8, ptr {baseValue}, i32 {relative}")
                offset
        let emitRuntimeStatus (w: OwningLlvmWriter) (context: string) (failureLabel: string) =
            let status = emitContextLoad w context 19
            let ok = w.Fresh "runtime.ok"
            let next = w.Label "runtime.continue"
            w.Inst($"{ok} = icmp eq i32 {status}, 0")
            w.Inst($"br i1 {ok}, label %%{next}, label %%{failureLabel}")
            w.Line($"{next}:")
        let emitCursorCheck (w: OwningLlvmWriter) context expected failureLabel =
            let result = w.Fresh "cursor.check"
            w.Inst($"{result} = call i32 @al_owning_check_cursor(ptr {context}, i32 {expected})")
            let succeeded = w.Fresh "cursor.ok"
            let next = w.Label "cursor.continue"
            w.Inst($"{succeeded} = icmp eq i32 {result}, 0")
            w.Inst($"br i1 {succeeded}, label %%{next}, label %%{failureLabel}")
            w.Line($"{next}:")
        let emitReserve (w: OwningLlvmWriter) context newCursor errorId failureLabel =
            let status = w.Fresh "reserve.status"
            w.Inst($"{status} = call i32 @al_owning_reserve_to(ptr {context}, i32 {newCursor}, i32 {errorId})")
            let succeeded = w.Fresh "reserve.ok"
            let next = w.Label "reserve.continue"
            w.Inst($"{succeeded} = icmp eq i32 {status}, 0")
            w.Inst($"br i1 {succeeded}, label %%{next}, label %%{failureLabel}")
            w.Line($"{next}:")
        let emitCallAndCheck (w: OwningLlvmWriter) context callText failureLabel =
            w.Inst callText
            emitRuntimeStatus w context failureLabel
        let typeId ty = typeIdFor info.TypeIds ty
        let payload ty = (infoFor info ty).PayloadBytes
        let extentOf ty = (infoFor info ty).ExtentBytes
        let rec makeBindings (env: OwningLocalEnvPlan) (outer: Map<LocalSlot, OwningLocalBinding>) =
            env.Slots
            |> Map.fold (fun bindings slot slotPlan ->
                let flag = slotPlan.FlagId |> Option.defaultWith (fun () -> invalidOp "Owning local slot has no active flag.")
                Map.add slot
                    { Slot = slot
                      OffsetBytes = slotPlan.OffsetBytes
                      ReservedBytes = slotPlan.ReservedBytes
                      ActiveFlag = Some flag
                      Fallback = outer.TryFind slot }
                    bindings) outer

        let emitFrameFunction (symbol: string) (owner: string) (inputTypes: IrType list) (outputTypes: IrType list)
                             (block: IrBlock) (plan: OwningFunctionPlan) (isEntry: bool) =
            let w = OwningLlvmWriter()
            let failBody = w.Label "frame.failure"
            let failBeforeLocals = w.Label "frame.reserve.failure"
            let failedEnter = w.Label "frame.enter.failure"
            w.Line($"define internal i32 {symbol}(ptr %%ctx, i32 %%argument.source, i32 %%result.destination, i32 %%depth.error) {{")
            w.Line("entry:")

            let flagPointers = Dictionary<int, string>()
            let payloadPointers = Dictionary<int, string>()
            let typePointers = Dictionary<int, string>()
            for flagId in plan.Flags do
                let flagPointer = w.Fresh "local.active"
                let payloadPointer = w.Fresh "local.payload"
                let typePointer = w.Fresh "local.type"
                flagPointers.Add(flagId, flagPointer)
                payloadPointers.Add(flagId, payloadPointer)
                typePointers.Add(flagId, typePointer)
                w.Inst($"{flagPointer} = alloca i1, align 1")
                w.Inst($"{payloadPointer} = alloca i32, align 4")
                w.Inst($"{typePointer} = alloca i32, align 4")
                w.Inst($"store i1 false, ptr {flagPointer}, align 1")
                w.Inst($"store i32 0, ptr {payloadPointer}, align 4")
                w.Inst($"store i32 0, ptr {typePointer}, align 4")

            let baselineCursor = emitContextLoad w "%ctx" 2
            let baselineOperands = emitContextLoad w "%ctx" 4
            let baselineLocals = emitContextLoad w "%ctx" 8
            let baselineReservations = emitContextLoad w "%ctx" 6
            let entered = w.Fresh "frame.entered"
            w.Inst($"{entered} = call i32 @al_owning_enter_frame(ptr %%ctx, i32 %%depth.error)")
            let enteredOk = w.Fresh "frame.entered.ok"
            let enterContinue = w.Label "frame.entered.continue"
            w.Inst($"{enteredOk} = icmp eq i32 {entered}, 0")
            w.Inst($"br i1 {enteredOk}, label %%{enterContinue}, label %%{failedEnter}")
            w.Line($"{failedEnter}:")
            w.Inst("ret i32 1")
            w.Line($"{enterContinue}:")

            let frameBase = emitContextLoad w "%ctx" 2
            let operandBase = emitOffset w frameBase plan.LocalBytes
            let inputBytes = inputTypes |> List.fold (fun sum ty -> checkedBoundedAdd "function inputs" sum (extentOf ty)) 0
            let reservedFrameBytes = checkedBoundedAdd "function frame" plan.LocalBytes inputBytes
            let frameEnd = emitOffset w frameBase reservedFrameBytes
            let reserveError = addDiagnostic "OWNING_STACK_INTERNAL" "Unable to reserve a verified owning function frame." owner None [] []
            emitReserve w "%ctx" frameEnd reserveError failBeforeLocals
            w.Inst($"call void @al_owning_local_reserve(ptr %%ctx, i32 {plan.LocalBytes})")
            emitRuntimeStatus w "%ctx" failBody

            let inputMoves =
                let mutable sourceOffset = 0
                let mutable relativeOffset = 0
                inputTypes
                |> List.map (fun ty ->
                    let currentSource = emitOffset w "%argument.source" sourceOffset
                    let destination = emitOffset w operandBase relativeOffset
                    let callText =
                        $"call void @al_owning_move_range(ptr %%ctx, i32 {destination}, i32 {currentSource}, i32 {extentOf ty}, i32 {payload ty}, i32 {typeId ty}, i32 8)"
                    sourceOffset <- checkedBoundedAdd "argument offset" sourceOffset (extentOf ty)
                    relativeOffset <- checkedBoundedAdd "argument offset" relativeOffset (extentOf ty)
                    callText)
            for callText in inputMoves do emitCallAndCheck w "%ctx" callText failBody
            let initialStack =
                let mutable offset = 0
                inputTypes
                |> List.map (fun ty ->
                    let result = { Type = ty; RelativeOffset = offset }
                    offset <- checkedBoundedAdd "argument offset" offset (extentOf ty)
                    result)
            let rootBindings = makeBindings plan.RootEnvironment Map.empty

            let pointerFor (table: Dictionary<int, string>) id =
                match table.TryGetValue id with
                | true, value -> value
                | _ -> invalidOp $"Owning local active id {id} has no emitted control slot."
            let emitFlagLoad flagId =
                let value = w.Fresh "local.is.active"
                w.Inst($"{value} = load i1, ptr {pointerFor flagPointers flagId}, align 1")
                value
            let emitTagLoad table prefix flagId =
                let value = w.Fresh prefix
                w.Inst($"{value} = load i32, ptr {pointerFor table flagId}, align 4")
                value
            let rec resolveLocalOffset (binding: OwningLocalBinding) =
                let ownOffset = emitOffset w frameBase binding.OffsetBytes
                match binding.ActiveFlag, binding.Fallback with
                | Some flagId, Some fallback ->
                    let active = emitFlagLoad flagId
                    let fallbackOffset = resolveLocalOffset fallback
                    let selected = w.Fresh "local.source.offset"
                    w.Inst($"{selected} = select i1 {active}, i32 {ownOffset}, i32 {fallbackOffset}")
                    selected
                | _ -> ownOffset

            let emitClearEnvironment (env: OwningLocalEnvPlan) =
                for (slot, slotPlan) in env.Slots |> Map.toList |> List.sortBy (fst >> slotValue) do
                    let flagId = slotPlan.FlagId |> Option.defaultWith (fun () -> invalidOp "Owning local slot has no active flag.")
                    let active = emitFlagLoad flagId
                    let storedPayload = emitTagLoad payloadPointers "local.clear.payload" flagId
                    let storedType = emitTagLoad typePointers "local.clear.type" flagId
                    let livePayload = w.Fresh "local.clear.live.payload"
                    let liveType = w.Fresh "local.clear.live.type"
                    w.Inst($"{livePayload} = select i1 {active}, i32 {storedPayload}, i32 0")
                    w.Inst($"{liveType} = select i1 {active}, i32 {storedType}, i32 0")
                    let offset = emitOffset w frameBase slotPlan.OffsetBytes
                    let callText =
                        $"call void @al_owning_clear_local(ptr %%ctx, i32 {offset}, i32 {slotPlan.ReservedBytes}, i32 {livePayload}, i32 {liveType})"
                    emitCallAndCheck w "%ctx" callText failBody
                    w.Inst($"store i1 false, ptr {pointerFor flagPointers flagId}, align 1")
                    w.Inst($"store i32 0, ptr {pointerFor payloadPointers flagId}, align 4")
                    w.Inst($"store i32 0, ptr {pointerFor typePointers flagId}, align 4")

            let rec emitBlock (currentOwner: string) (env: OwningLocalEnvPlan)
                             (currentStack: OwningStackEntry list)
                             (currentLocals: Map<LocalSlot, OwningLocalBinding>)
                             (current: IrBlock) =
                if (currentStack |> List.map (fun item -> item.Type)) <> current.EntryShape.StackTypes then
                    invalidOp $"Validated stack shape changed during owning emission in '{currentOwner}'."
                let mutable stack = currentStack
                let mutable locals = currentLocals
                let mutable localTypes = current.EntryShape.LocalTypes
                let pop count =
                    if count = 0 then [], stack
                    elif stack.Length < count then invalidOp "Verified owning block stack underflow during emission."
                    else
                        let split = stack.Length - count
                        stack |> List.skip split, stack |> List.take split
                let pushTypes prefix types =
                    let mutable offset = stackBytes info prefix
                    prefix @ (types |> List.map (fun ty ->
                        let item = { Type = ty; RelativeOffset = offset }
                        offset <- checkedBoundedAdd "operand stack" offset (extentOf ty)
                        item))
                let expectedCursor entries = emitOffset w operandBase (stackBytes info entries)
                let emitDropEntry (entry: OwningStackEntry) =
                    let start = emitOffset w operandBase entry.RelativeOffset
                    let text = $"call void @al_owning_drop(ptr %%ctx, i32 {start}, i32 {extentOf entry.Type}, i32 {payload entry.Type}, i32 {typeId entry.Type})"
                    emitCallAndCheck w "%ctx" text failBody
                let emitUpdateLive operandDelta =
                    if operandDelta <> 0 then
                        w.Inst($"call void @al_owning_update_live(ptr %%ctx, i32 {operandDelta}, i32 0)")
                        emitRuntimeStatus w "%ctx" failBody
                for instruction in current.Code do
                    let instructionSpan = spanFor instruction.Site
                    let stepId = addDiagnostic "RUNTIME_STEP_LIMIT" "Execution exceeded the 10,000 instruction limit." currentOwner instructionSpan [] []
                    let stepStatus = w.Fresh "step.status"
                    w.Inst($"{stepStatus} = call i32 @al_owning_charge_step(ptr %%ctx, i32 {stepId})")
                    let stepOk = w.Fresh "step.ok"
                    let stepNext = w.Label "step.continue"
                    w.Inst($"{stepOk} = icmp eq i32 {stepStatus}, 0")
                    w.Inst($"br i1 {stepOk}, label %%{stepNext}, label %%{failBody}")
                    w.Line($"{stepNext}:")
                    match instruction.Operation with
                    | IrOperation.Constant(literal, ty)
                        when ty = IrInt || ty = IrBool || ty = IrUnit ->
                        let bits =
                            match literal, ty with
                            | LInt value, IrInt -> value
                            | LBool flag, IrBool -> if flag then 1L else 0L
                            | LUnit, IrUnit -> 0L
                            | _ -> invalidOp "Owning validation accepted a mismatched scalar constant."
                        let destination = emitOffset w operandBase (stackBytes info stack)
                        let next = emitOffset w destination 8
                        let reserveId = addDiagnostic "OWNING_STACK_INTERNAL" "Unable to reserve a verified scalar constant." currentOwner instructionSpan [] []
                        emitReserve w "%ctx" next reserveId failBody
                        let callText = $"call void @al_owning_store_i64(ptr %%ctx, i32 {destination}, i64 {bits}, i32 {typeId ty})"
                        emitCallAndCheck w "%ctx" callText failBody
                        emitUpdateLive 8
                        stack <- pushTypes stack [ ty ]
                    | IrOperation.Constant _ -> invalidOp "Owning validation missed an unsupported literal."
                    | IrOperation.StoreLocal slot ->
                        let values, prefix = pop 1
                        let source = List.head values
                        let binding = locals.TryFind slot |> Option.defaultWith (fun () -> invalidOp $"Verified local slot {slotValue slot} has no owning location.")
                        let flagId = binding.ActiveFlag |> Option.defaultWith (fun () -> invalidOp "Owning local binding lacks an active flag.")
                        let active = emitFlagLoad flagId
                        let previousPayload = emitTagLoad payloadPointers "local.previous.payload" flagId
                        let oldPayload = w.Fresh "local.old.payload"
                        w.Inst($"{oldPayload} = select i1 {active}, i32 {previousPayload}, i32 0")
                        let destination = emitOffset w frameBase binding.OffsetBytes
                        let sourceOffset = emitOffset w operandBase source.RelativeOffset
                        let text =
                            $"call void @al_owning_store_local(ptr %%ctx, i32 {destination}, i32 {sourceOffset}, i32 {binding.ReservedBytes}, i32 {extentOf source.Type}, i32 {payload source.Type}, i32 {oldPayload}, i32 {typeId source.Type})"
                        emitCallAndCheck w "%ctx" text failBody
                        w.Inst($"store i1 true, ptr {pointerFor flagPointers flagId}, align 1")
                        w.Inst($"store i32 {payload source.Type}, ptr {pointerFor payloadPointers flagId}, align 4")
                        w.Inst($"store i32 {typeId source.Type}, ptr {pointerFor typePointers flagId}, align 4")
                        emitDropEntry source
                        stack <- prefix
                        localTypes <- Map.add slot source.Type localTypes
                    | IrOperation.LoadLocal slot ->
                        let binding = locals.TryFind slot |> Option.defaultWith (fun () -> invalidOp $"Verified LoadLocal {slotValue slot} lacks an owning location.")
                        let ty = localTypes.TryFind slot |> Option.defaultWith (fun () -> invalidOp "Verified local type missing during owning load.")
                        let destination = emitOffset w operandBase (stackBytes info stack)
                        let next = emitOffset w destination (extentOf ty)
                        let reserveId = addDiagnostic "OWNING_STACK_INTERNAL" "Unable to reserve a verified local copy." currentOwner instructionSpan [] []
                        emitReserve w "%ctx" next reserveId failBody
                        let sourceOffset = resolveLocalOffset binding
                        let text = $"call void @al_owning_load_local(ptr %%ctx, i32 {destination}, i32 {sourceOffset}, i32 {extentOf ty}, i32 {payload ty}, i32 {typeId ty})"
                        emitCallAndCheck w "%ctx" text failBody
                        stack <- pushTypes stack [ ty ]
                    | IrOperation.Scope inner ->
                        let childEnv = env.Scopes.TryFind instruction.Site |> Option.defaultWith (fun () -> invalidOp "Verified Scope lacks a planned bounded local region.")
                        for _, slotPlan in childEnv.Slots |> Map.toList do
                            let flagId = slotPlan.FlagId.Value
                            w.Inst($"store i1 false, ptr {pointerFor flagPointers flagId}, align 1")
                            w.Inst($"store i32 0, ptr {pointerFor payloadPointers flagId}, align 4")
                            w.Inst($"store i32 0, ptr {pointerFor typePointers flagId}, align 4")
                        let childLocals = makeBindings childEnv locals
                        let innerStack = emitBlock currentOwner childEnv stack childLocals inner
                        emitClearEnvironment childEnv
                        if (innerStack |> List.map (fun item -> item.Type)) <> inner.ExitShape.StackTypes then
                            invalidOp "Scope output shape differed from its verified exit shape."
                        stack <- innerStack
                        locals <- currentLocals
                    | IrOperation.If(thenBlock, elseBlock) ->
                        let conditionItems, prefix = pop 1
                        let condition = List.head conditionItems
                        let conditionOffset = emitOffset w operandBase condition.RelativeOffset
                        let rawCondition = w.Fresh "if.condition.raw"
                        w.Inst($"{rawCondition} = call i64 @al_owning_load_i64(ptr %%ctx, i32 {conditionOffset})")
                        emitRuntimeStatus w "%ctx" failBody
                        let truth = w.Fresh "if.condition"
                        w.Inst($"{truth} = icmp ne i64 {rawCondition}, 0")
                        emitDropEntry condition
                        stack <- prefix
                        let thenLabel = w.Label "if.then"
                        let elseLabel = w.Label "if.else"
                        let joinLabel = w.Label "if.join"
                        w.Inst($"br i1 {truth}, label %%{thenLabel}, label %%{elseLabel}")
                        w.Line($"{thenLabel}:")
                        let thenStack = emitBlock currentOwner env prefix locals thenBlock
                        w.Inst($"br label %%{joinLabel}")
                        w.Line($"{elseLabel}:")
                        let elseStack = emitBlock currentOwner env prefix locals elseBlock
                        w.Inst($"br label %%{joinLabel}")
                        w.Line($"{joinLabel}:")
                        if (thenStack |> List.map (fun item -> item.Type)) <> (elseStack |> List.map (fun item -> item.Type)) then
                            invalidOp "Verified If branch stacks diverged during owning emission."
                        if thenBlock.ExitShape.LocalTypes <> elseBlock.ExitShape.LocalTypes then
                            invalidOp "Verified If branch locals diverged during owning emission."
                        stack <- thenStack
                        locals <- currentLocals
                        localTypes <- thenBlock.ExitShape.LocalTypes
                    | IrOperation.MakeRecord(call, key, _) ->
                        let fields, prefix = pop call.InputTypes.Length
                        let ty = IrNominal key
                        let layout = infoFor info ty
                        let recordStart = emitOffset w operandBase (stackBytes info prefix)
                        for ((_, fieldType, fieldOffset), fieldValue) in List.zip layout.Fields fields do
                            let fieldInfo = infoFor info fieldType
                            if fieldInfo.PayloadBytes > 0 then
                                let destination = emitOffset w recordStart fieldOffset
                                let source = emitOffset w operandBase fieldValue.RelativeOffset
                                let text =
                                    $"call void @al_owning_move_range(ptr %%ctx, i32 {destination}, i32 {source}, i32 {fieldInfo.PayloadBytes}, i32 {fieldInfo.PayloadBytes}, i32 {typeId fieldType}, i32 6)"
                                emitCallAndCheck w "%ctx" text failBody
                        let next = emitOffset w recordStart layout.ExtentBytes
                        let currentEnd = emitOffset w operandBase (stackBytes info stack)
                        let reserveId = addDiagnostic "OWNING_STACK_INTERNAL" "Unable to reserve an empty inline record token." currentOwner instructionSpan [] []
                        let isGrowing = w.Fresh "record.grows"
                        w.Inst($"{isGrowing} = icmp ugt i32 {next}, {currentEnd}")
                        let reservePath = w.Label "record.reserve"
                        let releasePath = w.Label "record.release"
                        let recordReady = w.Label "record.ready"
                        w.Inst($"br i1 {isGrowing}, label %%{reservePath}, label %%{releasePath}")
                        w.Line($"{reservePath}:")
                        emitReserve w "%ctx" next reserveId failBody
                        w.Inst($"br label %%{recordReady}")
                        w.Line($"{releasePath}:")
                        w.Inst($"call void @al_owning_release_to(ptr %%ctx, i32 {next}, i32 0, i32 {typeId ty}, i32 {layout.PayloadBytes})")
                        emitRuntimeStatus w "%ctx" failBody
                        w.Inst($"br label %%{recordReady}")
                        w.Line($"{recordReady}:")
                        if layout.PayloadBytes = 0 then
                            emitCallAndCheck w "%ctx" $"call void @al_owning_store_token(ptr %%ctx, i32 {recordStart}, i32 {typeId ty})" failBody
                        let eventText = $"call void @al_owning_record_layout(ptr %%ctx, i32 6, i32 {typeId ty}, i32 {recordStart}, i32 {layout.ExtentBytes}, i32 {layout.PayloadBytes}, i32 0, i32 0)"
                        emitCallAndCheck w "%ctx" eventText failBody
                        stack <- pushTypes prefix [ ty ]
                    | IrOperation.GetRecordField(_, key, fieldIndex) ->
                        let values, prefix = pop 1
                        let parent = List.head values
                        let parentInfo = infoFor info (IrNominal key)
                        let fieldName, fieldType, fieldOffset = parentInfo.Fields |> List.item fieldIndex
                        ignore fieldName
                        let fieldInfo = infoFor info fieldType
                        let source = emitOffset w operandBase parent.RelativeOffset
                        let destination = source
                        if fieldInfo.PayloadBytes > 0 then
                            let fieldSource = emitOffset w source fieldOffset
                            let text =
                                $"call void @al_owning_move_range(ptr %%ctx, i32 {destination}, i32 {fieldSource}, i32 {fieldInfo.PayloadBytes}, i32 {fieldInfo.PayloadBytes}, i32 {typeId fieldType}, i32 7)"
                            emitCallAndCheck w "%ctx" text failBody
                        let next = emitOffset w destination fieldInfo.ExtentBytes
                        let currentEnd = emitOffset w operandBase (stackBytes info stack)
                        let grows = w.Fresh "field.grows"
                        w.Inst($"{grows} = icmp ugt i32 {next}, {currentEnd}")
                        let reservePath = w.Label "field.reserve"
                        let releasePath = w.Label "field.release"
                        let fieldReady = w.Label "field.ready"
                        let reserveId = addDiagnostic "OWNING_STACK_INTERNAL" "Unable to reserve an empty extracted field token." currentOwner instructionSpan [] []
                        w.Inst($"br i1 {grows}, label %%{reservePath}, label %%{releasePath}")
                        w.Line($"{reservePath}:")
                        emitReserve w "%ctx" next reserveId failBody
                        w.Inst($"br label %%{fieldReady}")
                        w.Line($"{releasePath}:")
                        w.Inst($"call void @al_owning_release_to(ptr %%ctx, i32 {next}, i32 0, i32 {typeId fieldType}, i32 {fieldInfo.PayloadBytes})")
                        emitRuntimeStatus w "%ctx" failBody
                        w.Inst($"br label %%{fieldReady}")
                        w.Line($"{fieldReady}:")
                        if fieldInfo.PayloadBytes = 0 then
                            emitCallAndCheck w "%ctx" $"call void @al_owning_store_token(ptr %%ctx, i32 {destination}, i32 {typeId fieldType})" failBody
                            let eventText = $"call void @al_owning_record_layout(ptr %%ctx, i32 7, i32 {typeId fieldType}, i32 {destination}, i32 {fieldInfo.ExtentBytes}, i32 0, i32 {emitOffset w source fieldOffset}, i32 0)"
                            emitCallAndCheck w "%ctx" eventText failBody
                        let liveDelta = fieldInfo.PayloadBytes - parentInfo.PayloadBytes
                        if liveDelta <> 0 then emitUpdateLive liveDelta
                        stack <- pushTypes prefix [ fieldType ]
                    | IrOperation.Call call ->
                        match call.ResolvedTarget with
                        | PrimitiveTarget(PrimitiveId primitive) ->
                            let values, prefix = pop call.InputTypes.Length
                            match primitive, values with
                            | "dup", [ value ] ->
                                let destination = emitOffset w operandBase (stackBytes info stack)
                                let next = emitOffset w destination (extentOf value.Type)
                                let reserveId = addDiagnostic "OWNING_STACK_INTERNAL" "Unable to reserve an independent duplicate." currentOwner instructionSpan [] []
                                emitReserve w "%ctx" next reserveId failBody
                                let source = emitOffset w operandBase value.RelativeOffset
                                let text = $"call void @al_owning_duplicate(ptr %%ctx, i32 {destination}, i32 {source}, i32 {extentOf value.Type}, i32 {payload value.Type}, i32 {typeId value.Type})"
                                emitCallAndCheck w "%ctx" text failBody
                                stack <- pushTypes stack [ value.Type ]
                            | "drop", [ value ] -> emitDropEntry value; stack <- prefix
                            | "swap", [ left; right ] ->
                                let start = emitOffset w operandBase left.RelativeOffset
                                let text = $"call void @al_owning_swap(ptr %%ctx, i32 {start}, i32 {extentOf left.Type}, i32 {extentOf right.Type}, i32 {typeId left.Type}, i32 {typeId right.Type})"
                                emitCallAndCheck w "%ctx" text failBody
                                stack <- pushTypes prefix [ right.Type; left.Type ]
                            | "equals", [ left; right ] ->
                                let leftOffset = emitOffset w operandBase left.RelativeOffset
                                let rightOffset = emitOffset w operandBase right.RelativeOffset
                                let compare = w.Fresh "equals.result"
                                w.Inst($"{compare} = call i32 @al_owning_equal(ptr %%ctx, i32 {leftOffset}, i32 {rightOffset}, i32 {extentOf left.Type})")
                                emitRuntimeStatus w "%ctx" failBody
                                let raw = w.Fresh "equals.bool"
                                w.Inst($"{raw} = icmp ne i32 {compare}, 0")
                                let primitiveResult = w.Fresh "equals.value"
                                w.Inst($"{primitiveResult} = zext i1 {raw} to i64")
                                for value in List.rev values do emitDropEntry value
                                let resultStart = emitOffset w operandBase (stackBytes info prefix)
                                let next = emitOffset w resultStart 8
                                let reserveId = addDiagnostic "OWNING_STACK_INTERNAL" "Unable to reserve an equality result." currentOwner instructionSpan [] []
                                emitReserve w "%ctx" next reserveId failBody
                                emitCallAndCheck w "%ctx" $"call void @al_owning_store_i64(ptr %%ctx, i32 {resultStart}, i64 {primitiveResult}, i32 {typeId IrBool})" failBody
                                emitUpdateLive 8
                                stack <- pushTypes prefix [ IrBool ]
                            | "bool.not", [ value ] ->
                                let source = emitOffset w operandBase value.RelativeOffset
                                let raw = w.Fresh "bool.input"
                                w.Inst($"{raw} = call i64 @al_owning_load_i64(ptr %%ctx, i32 {source})")
                                emitRuntimeStatus w "%ctx" failBody
                                let result = w.Fresh "bool.not"
                                w.Inst($"{result} = xor i64 {raw}, 1")
                                // Defer result construction until after the input is dropped.
                                let resultStart = emitOffset w operandBase (stackBytes info prefix)
                                for item in List.rev values do emitDropEntry item
                                let next = emitOffset w resultStart 8
                                let reserveId = addDiagnostic "OWNING_STACK_INTERNAL" "Unable to reserve a Bool result." currentOwner instructionSpan [] []
                                emitReserve w "%ctx" next reserveId failBody
                                emitCallAndCheck w "%ctx" $"call void @al_owning_store_i64(ptr %%ctx, i32 {resultStart}, i64 {result}, i32 {typeId IrBool})" failBody
                                emitUpdateLive 8
                                stack <- pushTypes prefix [ IrBool ]
                            | ("bool.and" | "bool.or"), [ left; right ] ->
                                let aOffset = emitOffset w operandBase left.RelativeOffset
                                let bOffset = emitOffset w operandBase right.RelativeOffset
                                let a = w.Fresh "bool.left"
                                let b = w.Fresh "bool.right"
                                w.Inst($"{a} = call i64 @al_owning_load_i64(ptr %%ctx, i32 {aOffset})")
                                emitRuntimeStatus w "%ctx" failBody
                                w.Inst($"{b} = call i64 @al_owning_load_i64(ptr %%ctx, i32 {bOffset})")
                                emitRuntimeStatus w "%ctx" failBody
                                let result = w.Fresh "bool.binary"
                                let opcode = if primitive = "bool.and" then "and" else "or"
                                w.Inst($"{result} = {opcode} i64 {a}, {b}")
                                let resultStart = emitOffset w operandBase (stackBytes info prefix)
                                for item in List.rev values do emitDropEntry item
                                let next = emitOffset w resultStart 8
                                let reserveId = addDiagnostic "OWNING_STACK_INTERNAL" "Unable to reserve a Bool result." currentOwner instructionSpan [] []
                                emitReserve w "%ctx" next reserveId failBody
                                emitCallAndCheck w "%ctx" $"call void @al_owning_store_i64(ptr %%ctx, i32 {resultStart}, i64 {result}, i32 {typeId IrBool})" failBody
                                emitUpdateLive 8
                                stack <- pushTypes prefix [ IrBool ]
                            | ("add" | "subtract" | "multiply"), [ left; right ] ->
                                let leftOffset = emitOffset w operandBase left.RelativeOffset
                                let rightOffset = emitOffset w operandBase right.RelativeOffset
                                let a = w.Fresh "integer.left"
                                let b = w.Fresh "integer.right"
                                w.Inst($"{a} = call i64 @al_owning_load_i64(ptr %%ctx, i32 {leftOffset})")
                                emitRuntimeStatus w "%ctx" failBody
                                w.Inst($"{b} = call i64 @al_owning_load_i64(ptr %%ctx, i32 {rightOffset})")
                                emitRuntimeStatus w "%ctx" failBody
                                let operation = match primitive with "add" -> "sadd" | "subtract" -> "ssub" | _ -> "smul"
                                let tuple = w.Fresh "integer.checked"
                                w.Inst($"{tuple} = call {{ i64, i1 }} @llvm.{operation}.with.overflow.i64(i64 {a}, i64 {b})")
                                let result = w.Fresh "integer.result"
                                let overflow = w.Fresh "integer.overflow"
                                w.Inst($"{result} = extractvalue {{ i64, i1 }} {tuple}, 0")
                                w.Inst($"{overflow} = extractvalue {{ i64, i1 }} {tuple}, 1")
                                let overflowId = addDiagnostic "RUNTIME_OVERFLOW" $"'{primitive}' overflowed its Int64 result." primitive instructionSpan [] []
                                let noOverflow = w.Label "integer.nooverflow"
                                let failedOverflow = w.Label "integer.overflow.failure"
                                w.Inst($"br i1 {overflow}, label %%{failedOverflow}, label %%{noOverflow}")
                                w.Line($"{failedOverflow}:")
                                w.Inst($"call void @al_owning_set_failure(ptr %%ctx, i32 1, i32 {overflowId}, i32 0, i32 0)")
                                w.Inst($"br label %%{failBody}")
                                w.Line($"{noOverflow}:")
                                let resultStart = emitOffset w operandBase (stackBytes info prefix)
                                for item in List.rev values do emitDropEntry item
                                let next = emitOffset w resultStart 8
                                let reserveId = addDiagnostic "OWNING_STACK_INTERNAL" "Unable to reserve a checked integer result." currentOwner instructionSpan [] []
                                emitReserve w "%ctx" next reserveId failBody
                                emitCallAndCheck w "%ctx" $"call void @al_owning_store_i64(ptr %%ctx, i32 {resultStart}, i64 {result}, i32 {typeId IrInt})" failBody
                                emitUpdateLive 8
                                stack <- pushTypes prefix [ IrInt ]
                            | "divide", [ left; right ] ->
                                let leftOffset = emitOffset w operandBase left.RelativeOffset
                                let rightOffset = emitOffset w operandBase right.RelativeOffset
                                let a = w.Fresh "divide.left"
                                let b = w.Fresh "divide.right"
                                w.Inst($"{a} = call i64 @al_owning_load_i64(ptr %%ctx, i32 {leftOffset})")
                                emitRuntimeStatus w "%ctx" failBody
                                w.Inst($"{b} = call i64 @al_owning_load_i64(ptr %%ctx, i32 {rightOffset})")
                                emitRuntimeStatus w "%ctx" failBody
                                let zero = w.Fresh "divide.zero"
                                w.Inst($"{zero} = icmp eq i64 {b}, 0")
                                let zeroId = addDiagnostic "RUNTIME_DIVIDE_BY_ZERO" "Integer division by zero." primitive None [] []
                                let nonZero = w.Label "divide.nonzero"
                                let zeroFail = w.Label "divide.zero.failure"
                                w.Inst($"br i1 {zero}, label %%{zeroFail}, label %%{nonZero}")
                                w.Line($"{zeroFail}:")
                                w.Inst($"call void @al_owning_set_failure(ptr %%ctx, i32 1, i32 {zeroId}, i32 0, i32 0)")
                                w.Inst($"br label %%{failBody}")
                                w.Line($"{nonZero}:")
                                let isMin = w.Fresh "divide.min"
                                let isNegativeOne = w.Fresh "divide.negative.one"
                                let overflow = w.Fresh "divide.overflow"
                                w.Inst($"{isMin} = icmp eq i64 {a}, -9223372036854775808")
                                w.Inst($"{isNegativeOne} = icmp eq i64 {b}, -1")
                                w.Inst($"{overflow} = and i1 {isMin}, {isNegativeOne}")
                                let valid = w.Label "divide.valid"
                                let overflowFail = w.Label "divide.overflow.failure"
                                let overflowId = addDiagnostic "RUNTIME_OVERFLOW" "Integer division overflow." primitive None [] []
                                w.Inst($"br i1 {overflow}, label %%{overflowFail}, label %%{valid}")
                                w.Line($"{overflowFail}:")
                                w.Inst($"call void @al_owning_set_failure(ptr %%ctx, i32 1, i32 {overflowId}, i32 0, i32 0)")
                                w.Inst($"br label %%{failBody}")
                                w.Line($"{valid}:")
                                let result = w.Fresh "divide.result"
                                w.Inst($"{result} = sdiv i64 {a}, {b}")
                                let resultStart = emitOffset w operandBase (stackBytes info prefix)
                                for item in List.rev values do emitDropEntry item
                                let next = emitOffset w resultStart 8
                                let reserveId = addDiagnostic "OWNING_STACK_INTERNAL" "Unable to reserve a division result." currentOwner instructionSpan [] []
                                emitReserve w "%ctx" next reserveId failBody
                                emitCallAndCheck w "%ctx" $"call void @al_owning_store_i64(ptr %%ctx, i32 {resultStart}, i64 {result}, i32 {typeId IrInt})" failBody
                                emitUpdateLive 8
                                stack <- pushTypes prefix [ IrInt ]
                            | ("int.less-than" | "int.greater-than" | "int.less-or-equal" | "int.greater-or-equal"), [ left; right ] ->
                                let leftOffset = emitOffset w operandBase left.RelativeOffset
                                let rightOffset = emitOffset w operandBase right.RelativeOffset
                                let a = w.Fresh "comparison.left"
                                let b = w.Fresh "comparison.right"
                                w.Inst($"{a} = call i64 @al_owning_load_i64(ptr %%ctx, i32 {leftOffset})")
                                emitRuntimeStatus w "%ctx" failBody
                                w.Inst($"{b} = call i64 @al_owning_load_i64(ptr %%ctx, i32 {rightOffset})")
                                emitRuntimeStatus w "%ctx" failBody
                                let predicate = match primitive with "int.less-than" -> "slt" | "int.greater-than" -> "sgt" | "int.less-or-equal" -> "sle" | _ -> "sge"
                                let cmp = w.Fresh "comparison.result"
                                w.Inst($"{cmp} = icmp {predicate} i64 {a}, {b}")
                                let result = w.Fresh "comparison.bool"
                                w.Inst($"{result} = zext i1 {cmp} to i64")
                                let resultStart = emitOffset w operandBase (stackBytes info prefix)
                                for item in List.rev values do emitDropEntry item
                                let next = emitOffset w resultStart 8
                                let reserveId = addDiagnostic "OWNING_STACK_INTERNAL" "Unable to reserve a comparison result." currentOwner instructionSpan [] []
                                emitReserve w "%ctx" next reserveId failBody
                                emitCallAndCheck w "%ctx" $"call void @al_owning_store_i64(ptr %%ctx, i32 {resultStart}, i64 {result}, i32 {typeId IrBool})" failBody
                                emitUpdateLive 8
                                stack <- pushTypes prefix [ IrBool ]
                            | _ -> invalidOp $"Owning validator did not normalize primitive '{primitive}'."
                        | UserWordTarget(id, revision) ->
                            let values, prefix = pop call.InputTypes.Length
                            let argumentBytes = values |> List.fold (fun sum entry -> checkedBoundedAdd "call inputs" sum (extentOf entry.Type)) 0
                            let outputBytes = call.OutputTypes |> List.fold (fun sum ty -> checkedBoundedAdd "call outputs" sum (extentOf ty)) 0
                            let stageBytes = max argumentBytes outputBytes
                            let stageOffset = emitOffset w operandBase (stackBytes info prefix)
                            let stageEnd = emitOffset w stageOffset stageBytes
                            let reserveId = addDiagnostic "OWNING_STACK_INTERNAL" "Unable to reserve caller-owned function result space." currentOwner instructionSpan [] []
                            emitReserve w "%ctx" stageEnd reserveId failBody
                            let callError = addDiagnostic "RUNTIME_CALL_DEPTH" "Execution exceeded the 64 word call-depth limit." call.ResolvedName instructionSpan [] []
                            let argumentStart = if List.isEmpty values then stageOffset else emitOffset w operandBase (List.head values).RelativeOffset
                            let callStatus = w.Fresh "callee.status"
                            w.Inst($"{callStatus} = call i32 {symbolFor id}(ptr %%ctx, i32 {argumentStart}, i32 {stageOffset}, i32 {callError})")
                            let succeeded = w.Fresh "callee.ok"
                            let callNext = w.Label "callee.returned"
                            w.Inst($"{succeeded} = icmp eq i32 {callStatus}, 0")
                            w.Inst($"br i1 {succeeded}, label %%{callNext}, label %%{failBody}")
                            w.Line($"{callNext}:")
                            let finalEnd = emitOffset w stageOffset outputBytes
                            w.Inst($"call void @al_owning_release_to(ptr %%ctx, i32 {finalEnd}, i32 0, i32 0, i32 0)")
                            emitRuntimeStatus w "%ctx" failBody
                            let outputEntries =
                                let mutable offset = stackBytes info prefix
                                call.OutputTypes
                                |> List.map (fun ty ->
                                    let entry = { Type = ty; RelativeOffset = offset }
                                    offset <- checkedBoundedAdd "call output offset" offset (extentOf ty)
                                    entry)
                            stack <- prefix @ outputEntries
                        | _ -> invalidOp "Validated owning call target is not a primitive or user word."
                    | _ -> invalidOp "Owning validation missed an unsupported operation."

                    let expected = expectedCursor stack
                    emitCursorCheck w "%ctx" expected failBody
                stack

            let emittedStack = emitBlock owner plan.RootEnvironment initialStack rootBindings block
            if (emittedStack |> List.map (fun item -> item.Type)) <> outputTypes then
                invalidOp $"Validated owning function '{owner}' output shape changed during emission."
            let outputBytes = outputTypes |> List.fold (fun sum ty -> checkedBoundedAdd "function outputs" sum (extentOf ty)) 0
            for value in emittedStack do
                let destination = emitOffset w "%result.destination" value.RelativeOffset
                let source = emitOffset w operandBase value.RelativeOffset
                let text =
                    $"call void @al_owning_move_range(ptr %%ctx, i32 {destination}, i32 {source}, i32 {extentOf value.Type}, i32 {payload value.Type}, i32 {typeId value.Type}, i32 9)"
                emitCallAndCheck w "%ctx" text failBody
            emitClearEnvironment plan.RootEnvironment
            w.Inst($"call void @al_owning_local_release(ptr %%ctx, i32 {plan.LocalBytes})")
            w.Inst($"call void @al_owning_release_to(ptr %%ctx, i32 {frameBase}, i32 0, i32 0, i32 0)")
            w.Inst("call void @al_owning_leave_frame(ptr %ctx)")
            w.Inst("ret i32 0")

            w.Line($"{failBeforeLocals}:")
            w.Inst($"call void @al_owning_release_to(ptr %%ctx, i32 {baselineCursor}, i32 0, i32 0, i32 0)")
            w.Inst("call void @al_owning_leave_frame(ptr %ctx)")
            w.Inst("ret i32 1")

            w.Line($"{failBody}:")
            w.Inst($"call void @al_owning_release_to(ptr %%ctx, i32 {baselineCursor}, i32 0, i32 0, i32 0)")
            let currentOperandLive = emitContextLoad w "%ctx" 4
            let currentLocalLive = emitContextLoad w "%ctx" 8
            let currentReserved = emitContextLoad w "%ctx" 6
            let baselineTotal = w.Fresh "baseline.live.total"
            let currentTotal = w.Fresh "current.live.total"
            let liveDelta = w.Fresh "cleanup.live.delta"
            let localDelta = w.Fresh "cleanup.local.delta"
            let reservedDelta = w.Fresh "cleanup.reserved.delta"
            w.Inst($"{baselineTotal} = add i32 {baselineOperands}, {baselineLocals}")
            w.Inst($"{currentTotal} = add i32 {currentOperandLive}, {currentLocalLive}")
            w.Inst($"{liveDelta} = sub i32 {baselineTotal}, {currentTotal}")
            w.Inst($"{localDelta} = sub i32 {baselineLocals}, {currentLocalLive}")
            w.Inst($"{reservedDelta} = sub i32 {currentReserved}, {baselineReservations}")
            w.Inst($"call void @al_owning_update_live(ptr %%ctx, i32 {liveDelta}, i32 {localDelta})")
            w.Inst($"call void @al_owning_local_release(ptr %%ctx, i32 {reservedDelta})")
            w.Inst("call void @al_owning_leave_frame(ptr %ctx)")
            w.Inst("ret i32 1")
            w.Line("}")
            w.Line("")
            ignore isEntry
            ignore outputBytes
            w.Text

        writer.Line("%AlOwningContext = type { i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, ptr, ptr, ptr, ptr, i64, i64, i64, i64 }")
        writer.Line("declare void @al_owning_begin(ptr)")
        writer.Line("declare i32 @al_owning_enter_frame(ptr, i32)")
        writer.Line("declare void @al_owning_leave_frame(ptr)")
        writer.Line("declare i32 @al_owning_reserve_to(ptr, i32, i32)")
        writer.Line("declare void @al_owning_release_to(ptr, i32, i32, i32, i32)")
        writer.Line("declare i64 @al_owning_load_i64(ptr, i32)")
        writer.Line("declare void @al_owning_store_i64(ptr, i32, i64, i32)")
        writer.Line("declare void @al_owning_store_token(ptr, i32, i32)")
        writer.Line("declare void @al_owning_copy_external(ptr, i32, ptr, i32, i32, i32)")
        writer.Line("declare void @al_owning_move_range(ptr, i32, i32, i32, i32, i32, i32)")
        writer.Line("declare void @al_owning_duplicate(ptr, i32, i32, i32, i32, i32)")
        writer.Line("declare void @al_owning_drop(ptr, i32, i32, i32, i32)")
        writer.Line("declare void @al_owning_store_local(ptr, i32, i32, i32, i32, i32, i32, i32)")
        writer.Line("declare void @al_owning_load_local(ptr, i32, i32, i32, i32, i32)")
        writer.Line("declare void @al_owning_clear_local(ptr, i32, i32, i32, i32)")
        writer.Line("declare void @al_owning_local_reserve(ptr, i32)")
        writer.Line("declare void @al_owning_local_release(ptr, i32)")
        writer.Line("declare void @al_owning_update_live(ptr, i32, i32)")
        writer.Line("declare void @al_owning_record_layout(ptr, i32, i32, i32, i32, i32, i32, i32)")
        writer.Line("declare void @al_owning_swap(ptr, i32, i32, i32, i32, i32)")
        writer.Line("declare i32 @al_owning_equal(ptr, i32, i32, i32)")
        writer.Line("declare void @al_owning_publish(ptr, ptr, i32, i32, i32, i32)")
        writer.Line("declare i32 @al_owning_check_cursor(ptr, i32)")
        writer.Line("declare i32 @al_owning_charge_step(ptr, i32)")
        writer.Line("declare void @al_owning_set_failure(ptr, i32, i32, i32, i32)")
        writer.Line("declare { i64, i1 } @llvm.sadd.with.overflow.i64(i64, i64)")
        writer.Line("declare { i64, i1 } @llvm.ssub.with.overflow.i64(i64, i64)")
        writer.Line("declare { i64, i1 } @llvm.smul.with.overflow.i64(i64, i64)")
        writer.Line("")

        let bodyPlan = buildFunctionPlan info body.BodyBlock
        let entryFrame = emitFrameFunction "@agentlang_entry_frame" body.BodyName body.BodyInputTypes body.BodyOutputTypes body.BodyBlock bodyPlan true
        writer.Line(entryFrame)
        for fn in info.ReachableFunctions |> List.sortBy (fun fn -> fn.FunctionName, fn.FunctionRevision) do
            let symbol = symbolFor fn.FunctionId
            let plan = buildFunctionPlan info fn.FunctionBody
            writer.Line(emitFrameFunction symbol fn.FunctionName fn.InputTypes fn.OutputTypes fn.FunctionBody plan false)

        let entryInputBytes = body.BodyInputTypes |> List.fold (fun sum ty -> checkedBoundedAdd "entry inputs" sum (extentOf ty)) 0
        let entryOutputBytes = body.BodyOutputTypes |> List.fold (fun sum ty -> checkedBoundedAdd "entry outputs" sum (extentOf ty)) 0
        let entryInputPayload = body.BodyInputTypes |> List.fold (fun sum ty -> checkedBoundedAdd "entry input payload" sum (payload ty)) 0
        let entryOutputPayload = body.BodyOutputTypes |> List.fold (fun sum ty -> checkedBoundedAdd "entry output payload" sum (payload ty)) 0
        let retainedTypeId = if body.BodyOutputTypes.Length = 1 then typeId (List.head body.BodyOutputTypes) else 0u
        let wrapper = OwningLlvmWriter()
        let wrapperFailure = wrapper.Label "entry.failure"
        let wrapperBodyFailure = wrapper.Label "entry.body.failure"
        let wrapperSuccess = wrapper.Label "entry.success"
        wrapper.Line("define dllexport i32 @agentlang_owning_execute(ptr %ctx, ptr %input, i32 %input.bytes, ptr %retained, i32 %retained.capacity) {")
        wrapper.Line("entry:")
        wrapper.Inst("call void @al_owning_begin(ptr %ctx)")
        let contextStatus = emitContextLoad wrapper "%ctx" 19
        let contextOkay = wrapper.Fresh "entry.context.ok"
        let beginOkay = wrapper.Label "entry.begin.ok"
        wrapper.Inst($"{contextOkay} = icmp eq i32 {contextStatus}, 0")
        wrapper.Inst($"br i1 {contextOkay}, label %%{beginOkay}, label %%{wrapperFailure}")
        wrapper.Line($"{beginOkay}:")
        let inputLengthOkay = wrapper.Fresh "entry.input.length.ok"
        wrapper.Inst($"{inputLengthOkay} = icmp eq i32 %%input.bytes, {entryInputBytes}")
        let inputValid = wrapper.Label "entry.input.valid"
        let inputInvalid = wrapper.Label "entry.input.invalid"
        wrapper.Inst($"br i1 {inputLengthOkay}, label %%{inputValid}, label %%{inputInvalid}")
        wrapper.Line($"{inputInvalid}:")
        wrapper.Inst($"call void @al_owning_set_failure(ptr %%ctx, i32 4, i32 0, i32 {entryInputBytes}, i32 %%input.bytes)")
        wrapper.Inst($"br label %%{wrapperFailure}")
        wrapper.Line($"{inputValid}:")
        if entryInputBytes > 0 then
            let inputReserveId = addDiagnostic "OWNING_STACK_INTERNAL" "Unable to reserve entry input bytes." body.BodyName None [] []
            emitReserve wrapper "%ctx" (string entryInputBytes) inputReserveId wrapperFailure
            let mutable offset = 0
            for ty in body.BodyInputTypes do
                let source = emitPointerOffset wrapper "%input" offset
                let text = $"call void @al_owning_copy_external(ptr %%ctx, i32 {offset}, ptr {source}, i32 {payload ty}, i32 {extentOf ty}, i32 {typeId ty})"
                emitCallAndCheck wrapper "%ctx" text wrapperFailure
                offset <- checkedBoundedAdd "entry input offset" offset (extentOf ty)
        let stageBytes = max entryInputBytes entryOutputBytes
        if stageBytes > entryInputBytes then
            let stageReserveId = addDiagnostic "OWNING_STACK_INTERNAL" "Unable to reserve entry result staging bytes." body.BodyName None [] []
            emitReserve wrapper "%ctx" (string stageBytes) stageReserveId wrapperFailure
        let bodyCall = wrapper.Fresh "entry.body.status"
        wrapper.Inst($"{bodyCall} = call i32 @agentlang_entry_frame(ptr %%ctx, i32 0, i32 0, i32 0)")
        let bodyOk = wrapper.Fresh "entry.body.ok"
        wrapper.Inst($"{bodyOk} = icmp eq i32 {bodyCall}, 0")
        wrapper.Inst($"br i1 {bodyOk}, label %%{wrapperSuccess}, label %%{wrapperBodyFailure}")
        wrapper.Line($"{wrapperBodyFailure}:")
        let failureCursor = emitContextLoad wrapper "%ctx" 2
        let failureOperandLive = emitContextLoad wrapper "%ctx" 4
        let failureLocalLive = emitContextLoad wrapper "%ctx" 8
        let failureReserved = emitContextLoad wrapper "%ctx" 6
        wrapper.Inst("call void @al_owning_release_to(ptr %ctx, i32 0, i32 0, i32 0, i32 0)")
        let failureTotalLive = wrapper.Fresh "entry.failure.live"
        let failureLiveDelta = wrapper.Fresh "entry.failure.delta"
        let failureLocalDelta = wrapper.Fresh "entry.failure.local.delta"
        wrapper.Inst($"{failureTotalLive} = add i32 {failureOperandLive}, {failureLocalLive}")
        wrapper.Inst($"{failureLiveDelta} = sub i32 0, {failureTotalLive}")
        wrapper.Inst($"{failureLocalDelta} = sub i32 0, {failureLocalLive}")
        wrapper.Inst($"call void @al_owning_update_live(ptr %%ctx, i32 {failureLiveDelta}, i32 {failureLocalDelta})")
        wrapper.Inst($"call void @al_owning_local_release(ptr %%ctx, i32 {failureReserved})")
        wrapper.Inst("br label %entry.return.status")
        wrapper.Line($"{wrapperSuccess}:")
        let publishType = if entryOutputPayload = 0 then 0u else retainedTypeId
        wrapper.Inst($"call void @al_owning_publish(ptr %%ctx, ptr %%retained, i32 %%retained.capacity, i32 0, i32 {entryOutputBytes}, i32 {publishType})")
        emitRuntimeStatus wrapper "%ctx" wrapperFailure
        if entryOutputPayload <> 0 then
            wrapper.Inst($"call void @al_owning_update_live(ptr %%ctx, i32 -{entryOutputPayload}, i32 0)")
            emitRuntimeStatus wrapper "%ctx" wrapperFailure
        wrapper.Inst("call void @al_owning_release_to(ptr %ctx, i32 0, i32 0, i32 0, i32 0)")
        wrapper.Inst("br label %entry.return.status")
        wrapper.Line($"{wrapperFailure}:")
        let currentCursor = emitContextLoad wrapper "%ctx" 2
        let currentOperands = emitContextLoad wrapper "%ctx" 4
        let currentLocals = emitContextLoad wrapper "%ctx" 8
        let currentReservations = emitContextLoad wrapper "%ctx" 6
        let totalLive = wrapper.Fresh "entry.cleanup.total.live"
        let liveDelta = wrapper.Fresh "entry.cleanup.live.delta"
        let localDelta = wrapper.Fresh "entry.cleanup.local.delta"
        wrapper.Inst($"{totalLive} = add i32 {currentOperands}, {currentLocals}")
        wrapper.Inst($"{liveDelta} = sub i32 0, {totalLive}")
        wrapper.Inst($"{localDelta} = sub i32 0, {currentLocals}")
        wrapper.Inst("call void @al_owning_release_to(ptr %ctx, i32 0, i32 0, i32 0, i32 0)")
        wrapper.Inst($"call void @al_owning_update_live(ptr %%ctx, i32 {liveDelta}, i32 {localDelta})")
        wrapper.Inst($"call void @al_owning_local_release(ptr %%ctx, i32 {currentReservations})")
        wrapper.Inst("br label %entry.return.status")
        wrapper.Line("entry.return.status:")
        let finalStatus = emitContextLoad wrapper "%ctx" 19
        wrapper.Inst($"ret i32 {finalStatus}")
        wrapper.Line("}")
        writer.Line(wrapper.Text)

        writer.Text, diagnostics.ToArray()

    let private writeEmbeddedResource (assembly: Reflection.Assembly) resourceName outputPath =
        use source = assembly.GetManifestResourceStream resourceName
        if isNull source then invalidOp $"Embedded native runtime resource '{resourceName}' was not found."
        use destination = File.Create outputPath
        source.CopyTo destination

    /// Compile verified fixed-layout owning IR as a fresh x64 LLVM library.
    let compile (toolchain: LlvmToolchain) optimization outputDirectory (verifiedBody: VerifiedIrBody) =
        if String.IsNullOrWhiteSpace outputDirectory then invalidArg (nameof outputDirectory) "Output directory must be nonempty."
        let programInfo = makeProgramInfo verifiedBody
        let llvmIr, diagnostics = emitModule programInfo
        let fullDirectory = Path.GetFullPath outputDirectory
        Directory.CreateDirectory fullDirectory |> ignore
        let llvmIrPath = Path.Combine(fullDirectory, "owning-stack-native.ll")
        let libraryPath = Path.Combine(fullDirectory, "owning-stack-native.dll")
        File.WriteAllText(llvmIrPath, llvmIr, UTF8Encoding(false))
        let runtimeDirectory = Path.Combine(fullDirectory, "native-runtime")
        Directory.CreateDirectory runtimeDirectory |> ignore
        let assembly = typeof<OwningStackCompiledProgram>.Assembly
        let runtimeHeaderPath = Path.Combine(runtimeDirectory, "owning_stack_runtime.h")
        let runtimeSourcePath = Path.Combine(runtimeDirectory, "owning_stack_runtime.c")
        writeEmbeddedResource assembly "AgentLang.Llvm.native.owning_stack_runtime.h" runtimeHeaderPath
        writeEmbeddedResource assembly "AgentLang.Llvm.native.owning_stack_runtime.c" runtimeSourcePath
        let compiledPath =
            LlvmToolchain.compileLibraryWithRuntime toolchain optimization llvmIrPath runtimeSourcePath runtimeDirectory libraryPath
        new OwningStackCompiledProgram(
            compiledPath, programInfo, diagnostics, llvmIr,
            encodeValues, decodeValues, readEvents, readMetrics, diagnosticForError)
