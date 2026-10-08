namespace AgentLang.Llvm

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open System.Text.Json
open AgentLang

/// Definition spans frozen from the same word snapshot used to create a
/// verified body. Primitive overflow diagnostics use PrimitiveDefinitionSpans;
/// call-depth diagnostics use WordDefinitionSpans.
type NativeDiagnosticSources =
    { WordDefinitionSpans: Map<string, SourceSpan>
      PrimitiveDefinitionSpans: Map<string, SourceSpan> }

/// One named entry point in a native module. The body must belong to the same
/// backend-authorized VerifiedIrProgram instance as every other module entry.
type NativeModuleEntry =
    { Name: string
      Body: VerifiedIrBody }

/// File-only result for a native module build. It does not load or own the DLL.
type NativeModuleArtifact =
    { LibraryPath: string
      ManifestPath: string
      Fingerprint: string
      EntryIrPaths: string list
      MetadataSourcePath: string
      ObjectPaths: string list }

[<RequireQualifiedAccess>]
module NativeDiagnosticSources =
    let empty =
        { WordDefinitionSpans = Map.empty
          PrimitiveDefinitionSpans = Map.empty }

    /// Freeze the definition-span lookup that the interpreter host uses for
    /// this compiler snapshot. These maps are captured in native metadata at
    /// compile time and do not change when later words are edited.
    let fromLoweringContext (context: Compiler.IrLoweringContext) =
        let spans = context.Words |> Map.map (fun _ entry -> entry.Definition.Span)
        { WordDefinitionSpans = spans
          PrimitiveDefinitionSpans = spans }

type internal ErrorWord =
    | FixedWord of string
    | EntryExecutionName

type internal ErrorMetadata =
    { Diagnostic: Diagnostic
      WordSource: ErrorWord
      DynamicActualOperandCount: int }

type private ModuleEntrySymbols =
    { OutputCapacity: string
      AbiLayout: string
      Execute: string }

type private ModuleDiagnosticManifest =
    { Id: int
      Code: string
      Message: string
      Word: string
      File: string
      Line: int
      Column: int
      Length: int
      Expected: string list
      Actual: string list }

type private ModuleFieldManifest =
    { Name: string
      TypeId: uint32 }

type private ModuleTypeManifest =
    { Id: uint32
      Name: string
      Kind: uint32
      Fields: ModuleFieldManifest list }

type private ModuleEntryManifest =
    { Id: int
      Name: string
      InputTypeIds: uint32 list
      OutputTypeIds: uint32 list
      InputTypes: string list
      OutputTypes: string list
      WorkspaceCapacity: int
      RequiresRecordArenas: bool
      Diagnostics: ModuleDiagnosticManifest list
      IrSha256: string }

type private ModuleManifestSemantic =
    { ModuleAbiVersion: uint32
      RuntimeAbiVersion: uint32
      Target: string
      Types: ModuleTypeManifest list
      Entries: ModuleEntryManifest list }

type private EmittedModuleEntry =
    { Id: int
      Name: string
      LlvmIr: string
      Metadata: ErrorMetadata array
      InputTypes: IrType list
      OutputTypes: IrType list
      OutputCapacity: int
      WorkspaceCapacity: int
      RequiresRecordArenas: bool
      ValueMetadata: NativeProgramMetadata }

type private EmittedValue =
    { Type: IrType
      Operand: string }

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private ExecuteDelegate = delegate of nativeint * nativeint * int32 * nativeint -> unit

type private CodeBuilder() =
    let text = StringBuilder()
    let mutable serial = 0
    let mutable currentLabel = "entry"

    member _.Emit(line: string) =
        text.Append("  ").AppendLine(line) |> ignore

    member _.Fresh(prefix: string) =
        let name = $"%%{prefix}.{serial}"
        serial <- serial + 1
        name

    member _.FreshLabel(prefix: string) =
        let name = $"{prefix}.{serial}"
        serial <- serial + 1
        name

    member _.Switch(label: string) =
        text.Append(label).AppendLine(":") |> ignore
        currentLabel <- label

    member _.CurrentLabel = currentLabel
    member _.Text = text.ToString()

[<Sealed>]
type NativeCompiledProgram internal
    (libraryPath: string,
     outputTypeIds: uint32 array,
     inputTypes: IrType array,
     inputTypeIds: uint32 array,
     outputCapacity: int,
     workspaceCapacity: int,
     requiresRecordArenas: bool,
     programIdentity: VerifiedIrProgram,
     valueMetadata: NativeProgramMetadata,
     metadata: ErrorMetadata array) as this =
    let mutable libraryHandle = IntPtr.Zero
    let mutable executeDelegate: ExecuteDelegate option = None
    let lifetimeGate = obj ()

    let invokeNative contextPointer outputPointer capacity statusPointer =
        lock lifetimeGate (fun () ->
            if libraryHandle = IntPtr.Zero then raise (ObjectDisposedException(nameof NativeCompiledProgram))
            match executeDelegate with
            | None -> raise (ObjectDisposedException(nameof NativeCompiledProgram))
            | Some native -> native.Invoke(contextPointer, outputPointer, capacity, statusPointer))

    do
        let handle = NativeLibrary.Load libraryPath
        try
            let address = NativeLibrary.GetExport(handle, "agentlang_execute")
            let native = Marshal.GetDelegateForFunctionPointer<ExecuteDelegate>(address)
            libraryHandle <- handle
            executeDelegate <- Some native
        with _ ->
            NativeLibrary.Free handle
            reraise ()

    member _.LibraryPath = libraryPath
    /// Number of values returned by the verified entry body.
    member _.OutputCount = outputTypeIds.Length
    /// Number of verified entry arguments accepted by this artifact.
    member _.InputCount = inputTypes.Length
    /// Required public i64 slots for the returned roots.
    member _.OutputCapacity = outputCapacity
    /// Required i64 slots in the per-execution workspace shared by calls and
    /// native record helpers.
    member _.WorkspaceCapacity = workspaceCapacity

    member private _.ExecuteRetainedCore
        (executionName: string,
         selectedOptions: NativeExecutionOptions,
         inputOwnerPointer: IntPtr,
         inputRoots: int64 array,
         actualInputTypeIds: uint32 array) : NativeRetainedResult =
        if String.IsNullOrWhiteSpace executionName then invalidArg (nameof executionName) "Execution name must be nonempty."
        if libraryHandle = IntPtr.Zero then raise (ObjectDisposedException(nameof NativeCompiledProgram))
        if inputRoots.Length <> inputTypes.Length || actualInputTypeIds.Length <> inputTypes.Length then
            invalidOp "Validated native entry argument buffers do not match the verified body signature."
        if not (Array.forall2 (=) inputTypeIds actualInputTypeIds) then
            invalidOp "Validated native entry argument type IDs differ from the compiled body signature."
        let defaultArenaBytes, defaultArenaNodes =
            if requiresRecordArenas then 1024 * 1024, 10000
            else 0, 0
        let capacityValue name requested defaultValue =
            let value = defaultArg requested defaultValue
            if value < 0 then invalidArg name "Native arena capacities cannot be negative."
            value
        let selectedScratch =
            match selectedOptions.ScratchArena with
            | Some scratch -> scratch
            | None ->
                let byteCapacity = capacityValue "ScratchByteCapacity" selectedOptions.ScratchByteCapacity defaultArenaBytes
                let nodeCapacity = capacityValue "ScratchNodeCapacity" selectedOptions.ScratchNodeCapacity defaultArenaNodes
                new NativeScratchArena(byteCapacity, nodeCapacity)
        let ownsScratch = selectedOptions.ScratchArena.IsNone
        let scratchOwner = selectedScratch.Owner
        try
            let scratchBytes = capacityValue "ScratchByteCapacity" selectedOptions.ScratchByteCapacity scratchOwner.ByteCapacity
            let scratchNodes = capacityValue "ScratchNodeCapacity" selectedOptions.ScratchNodeCapacity scratchOwner.NodeCapacity
            if scratchBytes <> scratchOwner.ByteCapacity || scratchNodes <> scratchOwner.NodeCapacity then
                invalidArg (nameof selectedOptions) "Explicit scratch capacities must match the supplied NativeScratchArena."
            let retainedByteCapacity = capacityValue "RetainedByteCapacity" selectedOptions.RetainedByteCapacity defaultArenaBytes
            let retainedNodeCapacity = capacityValue "RetainedNodeCapacity" selectedOptions.RetainedNodeCapacity defaultArenaNodes
            let scratchGeneration = NativeGeneration.next ()
            let retainedGeneration = NativeGeneration.next ()
            if scratchGeneration = retainedGeneration then invalidOp "Native arena generations must be unique."
            let retainedOwner = new NativeArenaOwner(retainedByteCapacity, retainedNodeCapacity)
            let mutable transferRetainedOwner = false
            try
                lock scratchOwner.SyncRoot (fun () ->
                    scratchOwner.Initialize scratchGeneration
                    retainedOwner.Initialize retainedGeneration
                    let mutable contextPointer = IntPtr.Zero
                    let mutable outputPointer = IntPtr.Zero
                    let mutable workspacePointer = IntPtr.Zero
                    let mutable inputRootsPointer = IntPtr.Zero
                    let mutable inputTypeIdsPointer = IntPtr.Zero
                    let mutable statusPointer = IntPtr.Zero
                    try
                        contextPointer <- Marshal.AllocHGlobal NativeAbi.ContextSize
                        outputPointer <-
                            if outputCapacity = 0 then IntPtr.Zero
                            else Marshal.AllocHGlobal(outputCapacity * NativeAbi.SlotSize)
                        workspacePointer <-
                            if workspaceCapacity = 0 then IntPtr.Zero
                            else Marshal.AllocHGlobal(workspaceCapacity * NativeAbi.SlotSize)
                        if inputRoots.Length > 0 then
                            inputRootsPointer <- Marshal.AllocHGlobal(inputRoots.Length * NativeAbi.SlotSize)
                            inputTypeIdsPointer <- Marshal.AllocHGlobal(inputRoots.Length * sizeof<uint32>)
                            for index in 0 .. inputRoots.Length - 1 do
                                Marshal.WriteInt64(inputRootsPointer, index * NativeAbi.SlotSize, inputRoots[index])
                                Marshal.WriteInt32(inputTypeIdsPointer, index * sizeof<uint32>, int actualInputTypeIds[index])
                        statusPointer <- Marshal.AllocHGlobal sizeof<int32>
                        Marshal.WriteInt32(contextPointer, NativeAbi.ContextAbiVersionOffset, int NativeAbi.Version)
                        Marshal.WriteInt32(contextPointer, NativeAbi.ContextStepsConsumedOffset, 0)
                        Marshal.WriteInt32(contextPointer, NativeAbi.ContextErrorMetadataIdOffset, -1)
                        Marshal.WriteInt32(contextPointer, NativeAbi.ContextReservedOffset, 0)
                        Marshal.WriteInt64(contextPointer, NativeAbi.ContextErrorArgument0Offset, 0L)
                        Marshal.WriteInt64(contextPointer, NativeAbi.ContextErrorArgument1Offset, 0L)
                        Marshal.WriteIntPtr(contextPointer, NativeAbi.ContextScratchOffset, scratchOwner.DescriptorPointer)
                        Marshal.WriteIntPtr(contextPointer, NativeAbi.ContextRetainedOffset, retainedOwner.DescriptorPointer)
                        Marshal.WriteIntPtr(contextPointer, NativeAbi.ContextWorkspaceOffset, workspacePointer)
                        Marshal.WriteInt32(contextPointer, NativeAbi.ContextWorkspaceCapacityOffset, workspaceCapacity)
                        Marshal.WriteInt32(contextPointer, NativeAbi.ContextReservedTailOffset, 0)
                        Marshal.WriteIntPtr(contextPointer, NativeAbi.ContextInputOwnerOffset, inputOwnerPointer)
                        Marshal.WriteIntPtr(contextPointer, NativeAbi.ContextInputRootsOffset, inputRootsPointer)
                        Marshal.WriteIntPtr(contextPointer, NativeAbi.ContextInputRootTypeIdsOffset, inputTypeIdsPointer)
                        Marshal.WriteInt32(contextPointer, NativeAbi.ContextInputRootCountOffset, inputRoots.Length)
                        Marshal.WriteInt32(contextPointer, NativeAbi.ContextReservedV3Offset, 0)
                        Marshal.WriteInt32(statusPointer, NativeAbi.StatusInvalidRequest)
                        invokeNative (nativeint contextPointer) (nativeint outputPointer) outputCapacity (nativeint statusPointer)
                        let status = Marshal.ReadInt32 statusPointer
                        let steps = Marshal.ReadInt32(contextPointer, NativeAbi.ContextStepsConsumedOffset)
                        match status with
                        | NativeAbi.StatusSuccess ->
                            let rootValues =
                                Array.init outputTypeIds.Length (fun index -> Marshal.ReadInt64(outputPointer, index * NativeAbi.SlotSize))
                            let result = new NativeRetainedResult(programIdentity, steps, rootValues, outputTypeIds, retainedOwner, valueMetadata.Types)
                            transferRetainedOwner <- true
                            result
                        | NativeAbi.StatusDiagnostic ->
                            let metadataId = Marshal.ReadInt32(contextPointer, NativeAbi.ContextErrorMetadataIdOffset)
                            if metadataId < 0 || metadataId >= metadata.Length then
                                raise (InvalidDataException($"Native artifact returned unknown diagnostic metadata id {metadataId}."))
                            let item = metadata[metadataId]
                            let word =
                                match item.WordSource with
                                | FixedWord value -> Some value
                                | EntryExecutionName -> Some executionName
                            let actual =
                                if item.DynamicActualOperandCount = 0 then item.Diagnostic.Actual
                                else
                                    List.init item.DynamicActualOperandCount (fun index ->
                                        let offset = if index = 0 then NativeAbi.ContextErrorArgument0Offset else NativeAbi.ContextErrorArgument1Offset
                                        Marshal.ReadInt64(contextPointer, offset).ToString(CultureInfo.InvariantCulture))
                            raise (LanguageException { item.Diagnostic with Word = word; Actual = actual })
                        | NativeAbi.StatusScratchCapacity
                        | NativeAbi.StatusRetainedCapacity as capacityStatus ->
                            let isScratch = capacityStatus = NativeAbi.StatusScratchCapacity
                            let arenaName = if isScratch then "scratch" else "retained"
                            let code = if isScratch then "NATIVE_SCRATCH_CAPACITY" else "NATIVE_RETAINED_CAPACITY"
                            let arena = if isScratch then scratchOwner else retainedOwner
                            raise (NativeResourceLimitException(
                                code, arenaName,
                                Marshal.ReadInt64(contextPointer, NativeAbi.ContextErrorArgument0Offset),
                                Marshal.ReadInt64(contextPointer, NativeAbi.ContextErrorArgument1Offset),
                                arena.ByteCapacity, arena.NodeCapacity))
                        | NativeAbi.StatusInvalidRequest ->
                            raise (InvalidOperationException("The native LLVM entry rejected the ABI version, arena, workspace, output capacity, or pointer layout."))
                        | NativeAbi.StatusInvalidReference ->
                            raise (InvalidDataException("The native LLVM entry rejected a record handle or graph reference."))
                        | other -> raise (InvalidDataException($"Native LLVM entry returned unknown status {other}."))
                    finally
                        if statusPointer <> IntPtr.Zero then Marshal.FreeHGlobal statusPointer
                        if inputTypeIdsPointer <> IntPtr.Zero then Marshal.FreeHGlobal inputTypeIdsPointer
                        if inputRootsPointer <> IntPtr.Zero then Marshal.FreeHGlobal inputRootsPointer
                        if workspacePointer <> IntPtr.Zero then Marshal.FreeHGlobal workspacePointer
                        if outputPointer <> IntPtr.Zero then Marshal.FreeHGlobal outputPointer
                        if contextPointer <> IntPtr.Zero then Marshal.FreeHGlobal contextPointer)
            finally
                if not transferRetainedOwner then (retainedOwner :> IDisposable).Dispose()
        finally
            // Reusable scratch arenas are invalidated after both success
            // and failure. Execute holds the same lock while C is running.
            try
                lock scratchOwner.SyncRoot (fun () -> scratchOwner.Reset(false))
            with :? ObjectDisposedException -> ()
            if ownsScratch then (selectedScratch :> IDisposable).Dispose()

    /// Execute with one retained input owner and ordered typed entry arguments.
    /// The input result remains locked and its arena remains borrowed for the
    /// complete native call. Primitive messages cannot satisfy nominal inputs.
    member _.ExecuteRetainedWithInputs
        (executionName: string,
         inputOwner: NativeRetainedResult option,
         arguments: IrEntryArgument list,
         ?options: NativeExecutionOptions) : NativeRetainedResult =
        if String.IsNullOrWhiteSpace executionName then invalidArg (nameof executionName) "Execution name must be nonempty."
        let selectedOptions = defaultArg options NativeExecutionOptions.defaults
        let executeWithInputData (inputOwnerPointer: IntPtr) (inputRootValues: int64 array) (inputOwnerRootTypeIds: uint32 array) =
            if arguments.Length <> inputTypes.Length then
                Diagnostics.raiseError "IR_BACKEND_ENTRY_ARGUMENT_COUNT" "Entry argument count does not match the verified body input signature." (Some executionName) None
                    [ string inputTypes.Length ] [ string arguments.Length ]

            let rootSelections =
                arguments
                |> List.choose (function
                    | IrEntryArgument.RetainedRoot index -> Some index
                    | IrEntryArgument.IntArgument _
                    | IrEntryArgument.BoolArgument _
                    | IrEntryArgument.UnitArgument -> None)
            for index in rootSelections do
                if index < 0 || index >= inputOwnerRootTypeIds.Length then
                    Diagnostics.raiseError "IR_BACKEND_ENTRY_ROOT_INDEX" "Entry argument selects a root outside the retained input owner." (Some executionName) None
                        [ $"root index in [0, {inputOwnerRootTypeIds.Length})" ] [ string index ]

            let typeForId typeId =
                if uint64 typeId >= uint64 valueMetadata.Types.Length then
                    raise (InvalidDataException($"Native retained input references unknown type id {typeId}."))
                valueMetadata.Types[int typeId].ValueType

            let argumentTypes =
                arguments
                |> List.map (function
                    | IrEntryArgument.RetainedRoot index -> typeForId inputOwnerRootTypeIds[index]
                    | IrEntryArgument.IntArgument _ -> IrInt
                    | IrEntryArgument.BoolArgument _ -> IrBool
                    | IrEntryArgument.UnitArgument -> IrUnit)

            if argumentTypes <> Array.toList inputTypes then
                let formatType = function
                    | IrNominal key ->
                        let typeId = valueMetadata.TypeIdsByIrType[IrNominal key]
                        match valueMetadata.Types[int typeId].Definition with
                        | Some(NativeScalarMetadata(name, _))
                        | Some(NativeRecordMetadata(name, _)) -> name
                        | None -> IrTypes.format (IrNominal key)
                    | other -> IrTypes.format other
                Diagnostics.raiseError "IR_BACKEND_ENTRY_ARGUMENT_TYPE" "Entry argument types do not match the verified body input signature." (Some executionName) None
                    (inputTypes |> Array.map formatType |> Array.toList)
                    (argumentTypes |> List.map formatType)

            let actualRootTypeIds =
                arguments
                |> List.map (function
                    | IrEntryArgument.RetainedRoot index -> inputOwnerRootTypeIds[index]
                    | IrEntryArgument.IntArgument _ -> NativeAbi.TypeIdInt
                    | IrEntryArgument.BoolArgument _ -> NativeAbi.TypeIdBool
                    | IrEntryArgument.UnitArgument -> NativeAbi.TypeIdUnit)
                |> List.toArray
            let actualRootValues =
                arguments
                |> List.map (function
                    | IrEntryArgument.RetainedRoot index -> inputRootValues[index]
                    | IrEntryArgument.IntArgument value -> value
                    | IrEntryArgument.BoolArgument value -> if value then 1L else 0L
                    | IrEntryArgument.UnitArgument -> 0L)
                |> List.toArray
            this.ExecuteRetainedCore(executionName, selectedOptions, inputOwnerPointer, actualRootValues, actualRootTypeIds)

        match inputOwner with
        | Some owner ->
            owner.WithBorrow(
                programIdentity,
                executionName,
                fun nativeOwner rootValues rootTypeIds ->
                    executeWithInputData (nativeOwner.DescriptorPointer) rootValues rootTypeIds)
        | None -> executeWithInputData IntPtr.Zero [||] [||]

    /// Execute a zero-input body and return an owned native retained arena.
    /// The result remains decodable after this compiled program is disposed.
    member this.ExecuteRetained(executionName: string, ?options: NativeExecutionOptions) : NativeRetainedResult =
        this.ExecuteRetainedWithInputs(executionName, None, [], ?options = options)


    /// Execute without retaining the native result after it has been decoded.
    member _.Execute(executionName: string) =
        use retained = this.ExecuteRetained executionName
        { Values = retained.Decode()
          StepsConsumed = retained.StepsConsumed }

    interface IDisposable with
        member _.Dispose() =
            lock lifetimeGate (fun () ->
                if libraryHandle <> IntPtr.Zero then
                    NativeLibrary.Free libraryHandle
                    libraryHandle <- IntPtr.Zero
                    executeDelegate <- None)

[<RequireQualifiedAccess>]
module LlvmAot =
    let private supportedPrimitiveOperations =
        set [
            "add"; "subtract"; "multiply"; "divide"
            "int.less-than"; "int.greater-than"; "int.less-or-equal"; "int.greater-or-equal"
            "equals"; "bool.and"; "bool.or"; "bool.not"
            "dup"; "drop"; "swap"
        ]

    let private raiseDiagnostic (code: string) (message: string) (word: string option) (span: SourceSpan option) (expected: string list) (actual: string list) =
        Diagnostics.raiseError code message word span expected actual

    let private functionSymbol (id: WordId) revision =
        let (WordId stableId) = id
        let hash =
            SHA256.HashData(Encoding.UTF8.GetBytes stableId)
            |> Convert.ToHexString
            |> fun value -> value.ToLowerInvariant()
        $"@agentlang_fn_{hash}_r{revision}"

    let private callDiagnostic (code: string) (message: string) (owner: string) (span: SourceSpan option) (expected: string list) (actual: string list) =
        raiseDiagnostic code message (Some owner) span expected actual

    let private nominalTypeName (program: IrProgram) key =
        match program.NominalTypesByKey.TryFind key with
        | Some(IrRecordDefinition record) -> record.TypeName
        | Some(IrScalarDefinition scalar) -> scalar.TypeName
        | Some(IrEnumDefinition enumDefinition) -> enumDefinition.TypeName
        | None -> IrTypes.format (IrNominal key)

    let private rejectEnum (program: IrProgram) (owner: string) (span: SourceSpan option) (key: ProgramTypeKey) =
        callDiagnostic "IR_LLVM_ENUM_UNSUPPORTED"
            "The LLVM backend does not support payload-free enum values or exhaustive enum matches."
            owner span [ "native enum support" ] [ nominalTypeName program key ]

    let private validateRecordTypeGraph (program: IrProgram) =
        let state = Dictionary<ProgramTypeKey, int>()
        let recordChildren key =
            match program.NominalTypesByKey.TryFind key with
            | Some(IrRecordDefinition record) ->
                record.RecordFields
                |> List.sortBy (fun field -> field.FieldIndex)
                |> List.choose (fun field ->
                    match field.FieldType with
                    | IrNominal childKey ->
                        match program.NominalTypesByKey.TryFind childKey with
                        | Some(IrRecordDefinition _) -> Some childKey
                        | _ -> None
                    | _ -> None)
            | _ -> []
        for KeyValue(root, definition) in program.NominalTypesByKey do
            match definition with
            | IrRecordDefinition _ when not (state.ContainsKey root) ->
                let active = ResizeArray<ProgramTypeKey>()
                let frames = Stack<ProgramTypeKey * ProgramTypeKey list * int>()
                state[root] <- 1
                active.Add root
                frames.Push(root, recordChildren root, 0)
                while frames.Count > 0 do
                    let current, children, childIndex = frames.Pop()
                    if childIndex >= children.Length then
                        state[current] <- 2
                        active.RemoveAt(active.Count - 1)
                    else
                        frames.Push(current, children, childIndex + 1)
                        let child = children[childIndex]
                        match state.TryGetValue child with
                        | true, 1 ->
                            let activeIndex = active |> Seq.tryFindIndex ((=) child) |> Option.defaultValue 0
                            let cycle =
                                [ yield! active |> Seq.skip activeIndex |> Seq.map (nominalTypeName program)
                                  yield nominalTypeName program child ]
                            callDiagnostic "IR_LLVM_RECURSIVE_RECORD_TYPE"
                                "The LLVM native-value backend does not support recursive record type graphs."
                                (nominalTypeName program child) None [ "acyclic immutable record type graph" ] [ String.concat " -> " cycle ]
                        | true, 2 -> ()
                        | _ ->
                            state[child] <- 1
                            active.Add child
                            frames.Push(child, recordChildren child, 0)
            | _ -> ()

    let private ensureNativeType (program: IrProgram) (owner: string) (span: SourceSpan option) (ty: IrType) =
        let seen = HashSet<IrType>()
        let pending = Stack<bool * string * SourceSpan option * IrType>()
        pending.Push(false, owner, span, ty)
        while pending.Count > 0 do
            let inRecordField, currentOwner, currentSpan, currentType = pending.Pop()
            if seen.Add currentType then
                match currentType with
                | IrInt | IrBool | IrUnit -> ()
                | IrNominal key ->
                    match program.NominalTypesByKey.TryFind key with
                    | Some(IrScalarDefinition scalar) ->
                        match scalar.BaseType with
                        | IrInt | IrBool -> ()
                        | unsupported ->
                            callDiagnostic "IR_LLVM_UNSUPPORTED_TYPE" "The LLVM scalar backend supports nominal scalars only when their base is Int or Bool."
                                currentOwner currentSpan [ "nominal scalar based on Int or Bool" ] [ $"{scalar.TypeName}: {IrTypes.format unsupported}" ]
                    | Some(IrRecordDefinition record) ->
                        for field in record.RecordFields |> List.sortByDescending (fun item -> item.FieldIndex) do
                            pending.Push(true, record.TypeName, None, field.FieldType)
                    | Some(IrEnumDefinition _) -> rejectEnum program currentOwner currentSpan key
                    | None ->
                        callDiagnostic "IR_LLVM_UNSUPPORTED_TYPE" "The LLVM native-value backend cannot resolve an unknown nominal type."
                            currentOwner currentSpan [ "known nominal scalar or record" ] [ IrTypes.format currentType ]
                | unsupported when inRecordField ->
                    callDiagnostic "IR_LLVM_UNSUPPORTED_TYPE"
                        "Native records may contain only Int, Bool, Unit, nominal Int/Bool scalars, and other immutable records."
                        currentOwner currentSpan [ "Int"; "Bool"; "Unit"; "nominal Int/Bool"; "immutable record" ] [ IrTypes.format unsupported ]
                | unsupported ->
                    callDiagnostic "IR_LLVM_UNSUPPORTED_TYPE"
                        "The LLVM scalar backend supports only Int, Bool, Unit, nominal Int/Bool scalar values, and immutable records."
                        currentOwner currentSpan [ "Int"; "Bool"; "Unit"; "nominal Int/Bool scalar"; "immutable record" ] [ IrTypes.format unsupported ]

    let private callsInBlock (program: IrProgram) (block: IrBlock) =
        let found = ResizeArray<IrResolvedCall * SourceSiteId>()
        let rec visitBlock (block: IrBlock) =
            for instruction in block.Code do
                let add call = found.Add(call, instruction.Site)
                let addConstructorValidator operation =
                    let validator =
                        match operation with
                        | WrapScalarOperation key ->
                            match program.NominalTypesByKey.TryFind key with
                            | Some(IrScalarDefinition scalar) -> scalar.ValidatorCall
                            | _ -> None
                        | MakeRecordOperation key ->
                            match program.NominalTypesByKey.TryFind key with
                            | Some(IrRecordDefinition record) -> record.ValidatorCall
                            | _ -> None
                        | _ -> None
                    validator |> Option.iter add
                match instruction.Operation with
                | IrOperation.Call call ->
                    add call
                    match call.ResolvedTarget with
                    | GeneratedWordTarget(id, _) ->
                        match program.GeneratedTargetsById.TryFind id with
                        | Some target -> addConstructorValidator target.Operation
                        | _ -> ()
                    | _ -> ()
                | IrOperation.ListMap(call, _, _)
                | IrOperation.ListFilter(call, _)
                | IrOperation.ListEach(call, _)
                | IrOperation.ListFold(call, _, _) -> add call
                | IrOperation.MakeRecord(call, _, validator) ->
                    add call
                    validator |> Option.iter add
                | IrOperation.GetRecordField(call, _, _)
                | IrOperation.UnwrapScalar(call, _) -> add call
                | IrOperation.WrapScalar(call, key, _) ->
                    add call
                    addConstructorValidator (WrapScalarOperation key)
                | IrOperation.Scope inner -> visitBlock inner
                | IrOperation.If(thenBlock, elseBlock) ->
                    visitBlock thenBlock
                    visitBlock elseBlock
                | IrOperation.MatchOption(_, someBlock, noneBlock) ->
                    visitBlock someBlock
                    visitBlock noneBlock
                | IrOperation.MatchResult(_, _, okBlock, errorBlock) ->
                    visitBlock okBlock
                    visitBlock errorBlock
                | IrOperation.MatchEnum(_, cases) ->
                    cases |> List.iter (snd >> visitBlock)
                | IrOperation.MakeEnumCase(call, _, _) -> add call
                | IrOperation.Constant _
                | IrOperation.ListEmpty _
                | IrOperation.ListSingleton _
                | IrOperation.OptionNone _
                | IrOperation.OptionSome _
                | IrOperation.ResultOk _
                | IrOperation.ResultError _
                | IrOperation.StoreLocal _
                | IrOperation.LoadLocal _ -> ()
        visitBlock block
        List.ofSeq found

    let private sourceMapFor (verifiedBody: VerifiedIrBody) =
        let verifiedProgram = VerifiedIrBody.program verifiedBody
        let program = VerifiedIrProgram.inspect verifiedProgram
        let body = VerifiedIrBody.inspect verifiedBody
        Map.fold (fun found site source -> Map.add site source found) program.SourceMap body.BodySourceMap

    let private sourceSpan (sourceMap: Map<SourceSiteId, IrSourceSite>) (site: SourceSiteId) =
        sourceMap.TryFind site |> Option.map (fun source -> source.SiteSpan)

    let private reachableFunctions (verifiedBody: VerifiedIrBody) (sourceMap: Map<SourceSiteId, IrSourceSite>) =
        let verifiedProgram = VerifiedIrBody.program verifiedBody
        VerifiedIrProgram.requireBackendRegistry Compiler.primitiveIrCatalog verifiedProgram
        let program = VerifiedIrProgram.inspect verifiedProgram
        let body = VerifiedIrBody.inspect verifiedBody
        let reachable = HashSet<WordId>()
        let pending = Queue<IrFunction>()

        let rejectEffects (owner: string) (span: SourceSpan option) (effects: Set<IrEffect>) =
            if not (Set.isEmpty effects) then
                callDiagnostic "IR_LLVM_EFFECT_UNSUPPORTED"
                    "The LLVM scalar backend accepts only effect-free bodies and reachable functions."
                    owner span [] (IrEffects.names effects)

        rejectEffects body.BodyName None body.BodyDeclaredEffects
        rejectEffects body.BodyName None body.BodyInferredEffects

        let inspectCall owner site (call: IrResolvedCall) =
            let span = sourceSpan sourceMap site
            rejectEffects call.ResolvedName span call.ResolvedDeclaredEffects
            rejectEffects call.ResolvedName span call.ResolvedEffects
            match call.ResolvedTarget with
            | UserWordTarget(id, revision) ->
                match program.FunctionsById.TryFind id with
                | None ->
                    callDiagnostic "IR_LLVM_TARGET_MISSING" "A verified user call is absent from its bound program."
                        owner span [ "reachable user function" ] [ sprintf "%A" id ]
                | Some functionValue when functionValue.FunctionRevision <> revision ->
                    callDiagnostic "IR_LLVM_TARGET_REVISION" "A verified user call revision differs from its bound function."
                        owner span [ string functionValue.FunctionRevision ] [ string revision ]
                | Some functionValue ->
                    if reachable.Add id then pending.Enqueue functionValue
            | PrimitiveTarget id ->
                if not (Compiler.primitiveIrCatalog.ContainsKey id) then
                    callDiagnostic "IR_LLVM_CATALOG_MISMATCH" "A primitive call is absent from the canonical compiler catalog."
                        owner span [ "Compiler.primitiveIrCatalog entry" ] [ sprintf "%A" id ]
            | GeneratedWordTarget(id, revision) ->
                match program.GeneratedTargetsById.TryFind id with
                | None ->
                    callDiagnostic "IR_LLVM_TARGET_MISSING" "A verified generated call is absent from its bound program."
                        owner span [ "reachable scalar constructor or accessor" ] [ sprintf "%A" id ]
                | Some target when target.TargetRevision <> revision ->
                    callDiagnostic "IR_LLVM_TARGET_REVISION" "A verified generated call revision differs from its bound target."
                        owner span [ string target.TargetRevision ] [ string revision ]
                | Some target ->
                    rejectEffects owner span target.TargetDeclaredEffects
                    rejectEffects owner span target.TargetEffects
                    match target.Operation with
                    | WrapScalarOperation key
                    | UnwrapScalarOperation key
                    | MakeRecordOperation key -> ensureNativeType program owner span (IrNominal key)
                    | GetRecordFieldOperation(key, _) -> ensureNativeType program owner span (IrNominal key)
                    | MakeEnumCaseOperation(key, _) -> rejectEnum program owner span key

        for call, site in callsInBlock program body.BodyBlock do inspectCall body.BodyName site call
        while pending.Count > 0 do
            let functionValue = pending.Dequeue()
            rejectEffects functionValue.FunctionName None functionValue.FunctionDeclaredEffects
            rejectEffects functionValue.FunctionName None functionValue.FunctionInferredEffects
            for call, site in callsInBlock program functionValue.FunctionBody do
                inspectCall functionValue.FunctionName site call
        reachable

    let rec private validateCall (program: IrProgram) (owner: string) (span: SourceSpan option) (call: IrResolvedCall) =
        for ty in call.InputTypes @ call.OutputTypes do ensureNativeType program owner span ty
        match call.ResolvedTarget with
        | UserWordTarget _ -> ()
        | GeneratedWordTarget(id, revision) ->
            let target =
                match program.GeneratedTargetsById.TryFind id with
                | None ->
                    callDiagnostic "IR_LLVM_TARGET_MISSING" "A verified generated call is absent from its bound program."
                        owner span [ "reachable scalar constructor or accessor" ] [ sprintf "%A" id ]
                | Some target when target.TargetRevision <> revision ->
                    callDiagnostic "IR_LLVM_TARGET_REVISION" "A verified generated call revision differs from its bound target."
                        owner span [ string target.TargetRevision ] [ string revision ]
                | Some target -> target
            if target.InputTypes <> call.InputTypes || target.OutputTypes <> call.OutputTypes then
                callDiagnostic "IR_LLVM_TARGET_SIGNATURE" "Generated scalar call signature differs from its checked target."
                    owner span
                    [ String.concat " " (target.InputTypes |> List.map IrTypes.format) + " -> " + String.concat " " (target.OutputTypes |> List.map IrTypes.format) ]
                    [ String.concat " " (call.InputTypes |> List.map IrTypes.format) + " -> " + String.concat " " (call.OutputTypes |> List.map IrTypes.format) ]
            match target.Operation with
            | WrapScalarOperation key ->
                match program.NominalTypesByKey.TryFind key with
                | Some(IrScalarDefinition scalar) when target.InputTypes = [ scalar.BaseType ] && target.OutputTypes = [ IrNominal key ] ->
                    scalar.ValidatorCall |> Option.iter (validateCall program owner span)
                | _ ->
                    callDiagnostic "IR_LLVM_TARGET_SIGNATURE" "Generated scalar constructor does not match its frozen nominal type."
                        owner span [ "base type -> nominal scalar" ] (call.OutputTypes |> List.map IrTypes.format)
            | UnwrapScalarOperation key ->
                match program.NominalTypesByKey.TryFind key with
                | Some(IrScalarDefinition scalar) when target.InputTypes = [ IrNominal key ] && target.OutputTypes = [ scalar.BaseType ] -> ()
                | _ ->
                    callDiagnostic "IR_LLVM_TARGET_SIGNATURE" "Generated scalar accessor does not match its frozen nominal type."
                        owner span [ "nominal scalar -> base type" ] (call.OutputTypes |> List.map IrTypes.format)
            | MakeRecordOperation key ->
                match program.NominalTypesByKey.TryFind key with
                | Some(IrRecordDefinition record) ->
                    let fieldTypes = record.RecordFields |> List.sortBy (fun field -> field.FieldIndex) |> List.map (fun field -> field.FieldType)
                    if target.InputTypes <> fieldTypes || target.OutputTypes <> [ IrNominal key ] then
                        callDiagnostic "IR_LLVM_TARGET_SIGNATURE" "Generated record constructor does not match its frozen nominal type."
                            owner span
                            [ String.concat " " (fieldTypes |> List.map IrTypes.format) + " -> " + IrTypes.format (IrNominal key) ]
                            [ String.concat " " (target.InputTypes |> List.map IrTypes.format) + " -> " + String.concat " " (target.OutputTypes |> List.map IrTypes.format) ]
                    match record.ValidatorCall with
                    | Some validator ->
                        if validator.InputTypes <> [ IrNominal key ] || validator.OutputTypes <> [ IrBool ] then
                            callDiagnostic "IR_RECORD_VALIDATOR_SIGNATURE" "Record validator must have the exact Record -> Bool signature."
                                owner span [ IrTypes.format (IrNominal key) + " -> Bool" ]
                                [ String.concat " " (validator.InputTypes |> List.map IrTypes.format) + " -> " + String.concat " " (validator.OutputTypes |> List.map IrTypes.format) ]
                        if not (Set.isEmpty validator.ResolvedEffects) then
                            callDiagnostic "IR_RECORD_VALIDATOR_EFFECT" "Record validator must be pure."
                                owner span [] (IrEffects.names validator.ResolvedEffects)
                        validateCall program owner span validator
                    | None -> ()
                    ensureNativeType program owner span (IrNominal key)
                | _ ->
                    callDiagnostic "IR_LLVM_TARGET_SIGNATURE" "Generated record constructor refers to a non-record nominal type."
                        owner span [ "record fields -> nominal record" ] (call.OutputTypes |> List.map IrTypes.format)
            | GetRecordFieldOperation(key, fieldIndex) ->
                match program.NominalTypesByKey.TryFind key with
                | Some(IrRecordDefinition record) when fieldIndex >= 0 && fieldIndex < record.RecordFields.Length ->
                    let fieldTypes = record.RecordFields |> List.sortBy (fun field -> field.FieldIndex) |> List.map (fun field -> field.FieldType)
                    let fieldType = fieldTypes[fieldIndex]
                    if target.InputTypes <> [ IrNominal key ] || target.OutputTypes <> [ fieldType ] then
                        callDiagnostic "IR_LLVM_TARGET_SIGNATURE" "Generated record accessor does not match its frozen nominal type or field index."
                            owner span [ IrTypes.format (IrNominal key) + " -> " + IrTypes.format fieldType ]
                            [ String.concat " " (target.InputTypes |> List.map IrTypes.format) + " -> " + String.concat " " (target.OutputTypes |> List.map IrTypes.format) ]
                    ensureNativeType program owner span (IrNominal key)
                | _ ->
                    callDiagnostic "IR_LLVM_TARGET_SIGNATURE" "Generated record accessor refers to an absent record field."
                        owner span [ "record -> declared field" ] (call.OutputTypes |> List.map IrTypes.format)
            | MakeEnumCaseOperation(key, _) -> rejectEnum program owner span key
        | PrimitiveTarget(PrimitiveId operation) ->
            if not (supportedPrimitiveOperations.Contains operation) then
                callDiagnostic "IR_LLVM_UNSUPPORTED_PRIMITIVE"
                    "The LLVM scalar backend has no implementation for this canonical primitive."
                    owner span (supportedPrimitiveOperations |> Set.toList) [ operation ]
            let inputs = call.InputTypes
            let outputs = call.OutputTypes
            let signatureMatches =
                match operation, inputs, outputs with
                | ("add" | "subtract" | "multiply" | "divide"), [ IrInt; IrInt ], [ IrInt ] -> true
                | ("int.less-than" | "int.greater-than" | "int.less-or-equal" | "int.greater-or-equal"), [ IrInt; IrInt ], [ IrBool ] -> true
                | "equals", [ left; right ], [ IrBool ] -> left = right
                | "bool.and", [ IrBool; IrBool ], [ IrBool ] -> true
                | "bool.or", [ IrBool; IrBool ], [ IrBool ] -> true
                | "bool.not", [ IrBool ], [ IrBool ] -> true
                | "dup", [ value ], [ left; right ] -> left = value && right = value
                | "drop", [ _ ], [] -> true
                | "swap", [ first; second ], [ resultFirst; resultSecond ] -> resultFirst = second && resultSecond = first
                | _ -> false
            if not signatureMatches then
                callDiagnostic "IR_LLVM_PRIMITIVE_SIGNATURE"
                    "The concrete primitive call does not match the LLVM backend's scalar signature."
                    owner span [ $"supported {operation} signature" ]
                    [ String.concat " " (inputs |> List.map IrTypes.format) + " -> " + String.concat " " (outputs |> List.map IrTypes.format) ]

    let rec private validateBlock (program: IrProgram) (owner: string) (sourceMap: Map<SourceSiteId, IrSourceSite>) (block: IrBlock) =
        let validateShape shape =
            shape.StackTypes |> List.iter (ensureNativeType program owner None)
            shape.LocalTypes |> Map.iter (fun _ ty -> ensureNativeType program owner None ty)
        validateShape block.EntryShape
        validateShape block.ExitShape
        for instruction in block.Code do
            let span = sourceSpan sourceMap instruction.Site
            match instruction.Operation with
            | IrOperation.Constant(literal, ty) ->
                ensureNativeType program owner span ty
                match literal with
                | LInt _ when ty = IrInt -> ()
                | LBool _ when ty = IrBool -> ()
                | LUnit when ty = IrUnit -> ()
                | _ ->
                    callDiagnostic "IR_LLVM_UNSUPPORTED_CONSTANT"
                        "The LLVM scalar backend supports Int, Bool, and Unit constants only."
                        owner span [ "Int"; "Bool"; "Unit" ] [ sprintf "%A : %s" literal (IrTypes.format ty) ]
            | IrOperation.Call call -> validateCall program owner span call
            | IrOperation.MakeRecord(call, key, validator) ->
                validateCall program owner span call
                ensureNativeType program owner span (IrNominal key)
                let record =
                    match program.NominalTypesByKey.TryFind key with
                    | Some(IrRecordDefinition record) -> record
                    | _ ->
                        callDiagnostic "IR_LLVM_TARGET_OPERATION" "Record construction refers to a non-record nominal type."
                            owner span [ "record type" ] [ IrTypes.format (IrNominal key) ]
                validator
                |> Option.iter (fun checkedCall ->
                    if checkedCall.InputTypes <> [ IrNominal key ] || checkedCall.OutputTypes <> [ IrBool ] then
                        callDiagnostic "IR_RECORD_VALIDATOR_INVALID" "Operation-carried record validator must have the exact Record -> Bool signature."
                            owner span [ IrTypes.format (IrNominal key) + " -> Bool" ]
                            [ String.concat " " (checkedCall.InputTypes |> List.map IrTypes.format) + " -> " + String.concat " " (checkedCall.OutputTypes |> List.map IrTypes.format) ]
                    if not (Set.isEmpty checkedCall.ResolvedEffects) then
                        callDiagnostic "IR_RECORD_VALIDATOR_INVALID" "Operation-carried record validator must be pure."
                            owner span [] (IrEffects.names checkedCall.ResolvedEffects)
                    validateCall program owner span checkedCall)
                if validator <> record.ValidatorCall then
                    callDiagnostic "IR_RECORD_VALIDATOR_MISMATCH" "Record construction validator differs from the immutable type table."
                        owner span
                        (record.ValidatorCall |> Option.map (fun value -> [ value.ResolvedName ]) |> Option.defaultValue [ "no validator" ])
                        (validator |> Option.map (fun value -> [ value.ResolvedName ]) |> Option.defaultValue [ "no validator" ])
                match call.ResolvedTarget with
                | GeneratedWordTarget(id, _) ->
                    match program.GeneratedTargetsById.TryFind id with
                    | Some { Operation = MakeRecordOperation targetKey } when targetKey = key -> ()
                    | _ ->
                        callDiagnostic "IR_LLVM_TARGET_OPERATION" "Record construction does not target its matching generated constructor."
                            owner span [ sprintf "%A" (MakeRecordOperation key) ] [ call.ResolvedName ]
                | _ ->
                    callDiagnostic "IR_LLVM_TARGET_OPERATION" "Record construction requires a generated constructor target."
                        owner span [ "generated record constructor" ] [ call.ResolvedName ]
            | IrOperation.GetRecordField(call, key, fieldIndex) ->
                validateCall program owner span call
                ensureNativeType program owner span (IrNominal key)
                match call.ResolvedTarget with
                | GeneratedWordTarget(id, _) ->
                    match program.GeneratedTargetsById.TryFind id with
                    | Some { Operation = GetRecordFieldOperation(targetKey, targetIndex) } when targetKey = key && targetIndex = fieldIndex -> ()
                    | _ ->
                        callDiagnostic "IR_LLVM_TARGET_OPERATION" "Record access does not target its matching generated field accessor."
                            owner span [ sprintf "%A" (GetRecordFieldOperation(key, fieldIndex)) ] [ call.ResolvedName ]
                | _ ->
                    callDiagnostic "IR_LLVM_TARGET_OPERATION" "Record access requires a generated field accessor target."
                        owner span [ "generated record accessor" ] [ call.ResolvedName ]
            | IrOperation.WrapScalar(call, key, validator) ->
                validateCall program owner span call
                match call.ResolvedTarget with
                | GeneratedWordTarget(id, _) ->
                    match program.GeneratedTargetsById.TryFind id with
                    | Some { Operation = WrapScalarOperation targetKey } when targetKey = key -> ()
                    | _ ->
                        callDiagnostic "IR_LLVM_TARGET_OPERATION" "Scalar wrap instruction does not target the matching generated constructor."
                            owner span [ sprintf "%A" (WrapScalarOperation key) ] [ call.ResolvedName ]
                | _ ->
                    callDiagnostic "IR_LLVM_TARGET_OPERATION" "Scalar wrap instruction requires a generated constructor target."
                        owner span [ "generated scalar constructor" ] [ call.ResolvedName ]
                match program.NominalTypesByKey.TryFind key with
                | Some(IrScalarDefinition scalar) when scalar.ValidatorCall = validator -> ()
                | Some(IrScalarDefinition _) ->
                    callDiagnostic "IR_SCALAR_VALIDATOR_MISMATCH" "Scalar wrapping validator differs from the immutable type table."
                        owner span [] []
                | _ ->
                    callDiagnostic "IR_LLVM_TARGET_SIGNATURE" "Scalar wrap instruction refers to a non-scalar nominal type."
                        owner span [ "verified nominal scalar" ] [ IrTypes.format (IrNominal key) ]
            | IrOperation.UnwrapScalar(call, key) ->
                validateCall program owner span call
                match call.ResolvedTarget with
                | GeneratedWordTarget(id, _) ->
                    match program.GeneratedTargetsById.TryFind id with
                    | Some { Operation = UnwrapScalarOperation targetKey } when targetKey = key -> ()
                    | _ ->
                        callDiagnostic "IR_LLVM_TARGET_OPERATION" "Scalar unwrap instruction does not target the matching generated accessor."
                            owner span [ sprintf "%A" (UnwrapScalarOperation key) ] [ call.ResolvedName ]
                | _ ->
                    callDiagnostic "IR_LLVM_TARGET_OPERATION" "Scalar unwrap instruction requires a generated accessor target."
                        owner span [ "generated scalar accessor" ] [ call.ResolvedName ]
            | IrOperation.MatchEnum(key, _) -> rejectEnum program owner span key
            | IrOperation.MakeEnumCase(_, key, _) -> rejectEnum program owner span key
            | IrOperation.StoreLocal _
            | IrOperation.LoadLocal _ -> ()
            | IrOperation.Scope inner -> validateBlock program owner sourceMap inner
            | IrOperation.If(thenBlock, elseBlock) ->
                validateBlock program owner sourceMap thenBlock
                validateBlock program owner sourceMap elseBlock
            | operation ->
                callDiagnostic "IR_LLVM_UNSUPPORTED_OPERATION"
                    "The LLVM scalar backend supports constants, calls, locals, Scope, and If."
                    owner span
                    [ "Constant"; "Call"; "StoreLocal"; "LoadLocal"; "Scope"; "If" ]
                    [ sprintf "%A" operation ]

    let private metadataFor (metadata: ResizeArray<ErrorMetadata>) (diagnostic: Diagnostic) (wordSource: ErrorWord) (dynamicActualOperandCount: int) =
        let id = metadata.Count
        metadata.Add
            { Diagnostic = diagnostic
              WordSource = wordSource
              DynamicActualOperandCount = dynamicActualOperandCount }
        id

    let private operationDiagnostic (code: string) (message: string) (word: string) (span: SourceSpan option) (expected: string list) (actual: string list) =
        { Code = code
          Message = message
          Word = Some word
          Span = span
          Expected = expected
          Actual = actual }

    /// Check that a compiler-minted program is authorized by the exact
    /// primitive contract catalog used by Core.
    let validateProgram (verifiedProgram: VerifiedIrProgram) =
        VerifiedIrProgram.requireBackendRegistry Compiler.primitiveIrCatalog verifiedProgram

    let private emitModule (diagnosticSources: NativeDiagnosticSources) (symbols: ModuleEntrySymbols) (verifiedBody: VerifiedIrBody) =
        let verifiedProgram = VerifiedIrBody.program verifiedBody
        validateProgram verifiedProgram
        let program = VerifiedIrProgram.inspect verifiedProgram
        let body = VerifiedIrBody.inspect verifiedBody
        let sourceMap = sourceMapFor verifiedBody
        validateRecordTypeGraph program
        for KeyValue(key, definition) in program.NominalTypesByKey do
            match definition with
            | IrRecordDefinition record -> ensureNativeType program record.TypeName None (IrNominal key)
            | IrScalarDefinition scalar -> ensureNativeType program scalar.TypeName None (IrNominal key)
            | IrEnumDefinition _ -> rejectEnum program (nominalTypeName program key) None key
        let reachable = reachableFunctions verifiedBody sourceMap
        body.BodyInputTypes @ body.BodyOutputTypes |> List.iter (ensureNativeType program body.BodyName None)
        validateBlock program body.BodyName sourceMap body.BodyBlock
        let functions =
            program.FunctionsById
            |> Map.toList
            |> List.choose (fun (id, functionValue) -> if reachable.Contains id then Some(id, functionValue) else None)
        for _, functionValue in functions do
            functionValue.InputTypes @ functionValue.OutputTypes |> List.iter (ensureNativeType program functionValue.FunctionName None)
            validateBlock program functionValue.FunctionName program.SourceMap functionValue.FunctionBody

        let nativeValueMetadata = NativeProgramMetadata.create program

        let metadata = ResizeArray<ErrorMetadata>()
        let output = StringBuilder()
        output.AppendLine("target triple = " + "\"" + "x86_64-pc-windows-msvc" + "\"") |> ignore
        output.AppendLine("%NativeExecutionContext = type { i32, i32, i32, i32, i64, i64, ptr, ptr, ptr, i32, i32, ptr, ptr, ptr, i32, i32 }") |> ignore
        output.AppendLine("%NativeExecutionContextAlignmentProbe = type { i8, %NativeExecutionContext }") |> ignore
        output.AppendLine("%NativeTypeDescriptor = type { i32, i32, ptr }") |> ignore
        output.AppendLine("%NativeProgramDescriptor = type { ptr, i32, i32 }") |> ignore
        output.AppendLine("declare i32 @al_runtime_validate_request(ptr, ptr, i32, i32, ptr, i32)") |> ignore
        output.AppendLine("declare i32 @al_runtime_import_state(ptr, ptr, ptr, i32)") |> ignore
        output.AppendLine("declare i32 @al_runtime_make_record(ptr, ptr, i32, ptr, i32, ptr)") |> ignore
        output.AppendLine("declare i32 @al_runtime_get_field(ptr, ptr, i32, i64, i32, ptr)") |> ignore
        output.AppendLine("declare i32 @al_runtime_equal(ptr, ptr, i32, i64, i64, ptr)") |> ignore
        output.AppendLine("declare i32 @al_runtime_promote(ptr, ptr, ptr, ptr, i32, ptr, i32)") |> ignore
        output.AppendLine("declare { i64, i1 } @llvm.sadd.with.overflow.i64(i64, i64)") |> ignore
        output.AppendLine("declare { i64, i1 } @llvm.ssub.with.overflow.i64(i64, i64)") |> ignore
        output.AppendLine("declare { i64, i1 } @llvm.smul.with.overflow.i64(i64, i64)") |> ignore
        output.AppendLine() |> ignore

        for typeMetadata in nativeValueMetadata.Types do
            match typeMetadata.Definition with
            | Some(NativeRecordMetadata(_, fields)) when not (List.isEmpty fields) ->
                let fieldIds = fields |> List.map (fun (_, fieldTypeId) -> $"i32 {fieldTypeId}") |> String.concat ", "
                output.AppendLine($"@agentlang_fields_{typeMetadata.TypeId} = private constant [{fields.Length} x i32] [{fieldIds}]") |> ignore
            | _ -> ()
        let descriptorValues =
            nativeValueMetadata.Types
            |> Array.map (fun typeMetadata ->
                match typeMetadata.Definition with
                | Some(NativeRecordMetadata(_, fields)) when not (List.isEmpty fields) ->
                    let fieldPointer =
                        $"ptr getelementptr inbounds ([{fields.Length} x i32], ptr @agentlang_fields_{typeMetadata.TypeId}, i32 0, i32 0)"
                    $"%%NativeTypeDescriptor {{ i32 {typeMetadata.Kind}, i32 {fields.Length}, {fieldPointer} }}"
                | _ -> $"%%NativeTypeDescriptor {{ i32 {typeMetadata.Kind}, i32 0, ptr null }}")
            |> String.concat ", "
        output.AppendLine($"@agentlang_type_descriptors = private constant [{nativeValueMetadata.Types.Length} x %%NativeTypeDescriptor] [{descriptorValues}]") |> ignore
        output.AppendLine($"@agentlang_program = private constant %%NativeProgramDescriptor {{ ptr getelementptr inbounds ([{nativeValueMetadata.Types.Length} x %%NativeTypeDescriptor], ptr @agentlang_type_descriptors, i32 0, i32 0), i32 {nativeValueMetadata.Types.Length}, i32 0 }}") |> ignore
        let rootTypeIds =
            body.BodyOutputTypes
            |> List.map (fun ty -> nativeValueMetadata.TypeIdsByIrType[ty])
            |> List.toArray
        let rootTypeValues = rootTypeIds |> Array.map (fun typeId -> $"i32 {typeId}") |> String.concat ", "
        output.AppendLine($"@agentlang_root_type_ids = private constant [{rootTypeIds.Length} x i32] [{rootTypeValues}]") |> ignore
        let inputTypeIds = body.BodyInputTypes |> List.map (fun ty -> nativeValueMetadata.TypeIdsByIrType[ty]) |> List.toArray
        let inputTypeValues = inputTypeIds |> Array.map (fun typeId -> $"i32 {typeId}") |> String.concat ", "
        output.AppendLine($"@agentlang_input_type_ids = private constant [{inputTypeIds.Length} x i32] [{inputTypeValues}]") |> ignore
        output.AppendLine() |> ignore
        let valueMetrics = NativeProgramMetadata.valueMetrics nativeValueMetadata
        let outputCapacity = body.BodyOutputTypes.Length
        let functionOutputMax = functions |> List.map (fun (_, functionValue) -> functionValue.OutputTypes.Length) |> List.fold max 0
        let recordDefinitions =
            nativeValueMetadata.Types
            |> Array.choose (fun ty ->
                match ty.Definition with
                | Some(NativeRecordMetadata(_, fields)) -> Some fields.Length
                | _ -> None)
        let helperWorkspaceCapacity =
            if recordDefinitions.Length = 0 then 0
            else (recordDefinitions |> Array.max) + 1
        let workspaceCapacity = max body.BodyInputTypes.Length (max outputCapacity (max functionOutputMax helperWorkspaceCapacity))
        let requiresRecordArenas = recordDefinitions.Length > 0

        let emitFieldPointer (builder: CodeBuilder) (field: int) =
            let pointer = builder.Fresh "ctx.field"
            builder.Emit($"{pointer} = getelementptr %%NativeExecutionContext, ptr %%ctx, i32 0, i32 {field}")
            pointer

        let emitFailure (builder: CodeBuilder) (diagnostic: Diagnostic) wordSource =
            let metadataId = metadataFor metadata diagnostic wordSource 0
            let metadataPointer = emitFieldPointer builder 2
            builder.Emit($"store i32 {metadataId}, ptr {metadataPointer}, align 4")
            let statusPointer = builder.Fresh "status.diagnostic"
            builder.Emit($"{statusPointer} = getelementptr inbounds i32, ptr %%status, i64 0")
            builder.Emit($"store i32 {NativeAbi.StatusDiagnostic}, ptr {statusPointer}, align 4")
            builder.Emit("ret void")

        let emitFailureWithActualOperands (builder: CodeBuilder) (diagnostic: Diagnostic) wordSource (operands: string list) =
            if operands.Length > 2 then invalidOp "Native diagnostic metadata supports at most two dynamic actual operands."
            for index, operand in List.indexed operands do
                let field = if index = 0 then 4 else 5
                let pointer = emitFieldPointer builder field
                builder.Emit($"store i64 {operand}, ptr {pointer}, align 8")
            let metadataId = metadataFor metadata diagnostic wordSource operands.Length
            let metadataPointer = emitFieldPointer builder 2
            builder.Emit($"store i32 {metadataId}, ptr {metadataPointer}, align 4")
            let statusPointer = builder.Fresh "status.diagnostic"
            builder.Emit($"{statusPointer} = getelementptr inbounds i32, ptr %%status, i64 0")
            builder.Emit($"store i32 {NativeAbi.StatusDiagnostic}, ptr {statusPointer}, align 4")
            builder.Emit("ret void")

        let emitRuntimeResult (builder: CodeBuilder) (result: string) =
            let successLabel = builder.FreshLabel "runtime.success"
            let invalidRequestLabel = builder.FreshLabel "runtime.invalid.request"
            let invalidReferenceLabel = builder.FreshLabel "runtime.invalid.reference"
            let scratchCapacityLabel = builder.FreshLabel "runtime.scratch.capacity"
            let retainedCapacityLabel = builder.FreshLabel "runtime.retained.capacity"
            let invalidResultLabel = builder.FreshLabel "runtime.invalid.result"
            builder.Emit(
                $"switch i32 {result}, label %%{invalidResultLabel} [ " +
                $"i32 0, label %%{successLabel} i32 1, label %%{invalidRequestLabel} " +
                $"i32 2, label %%{invalidReferenceLabel} i32 3, label %%{scratchCapacityLabel} " +
                $"i32 4, label %%{retainedCapacityLabel} ]")
            let storeStatusAndReturn label status =
                builder.Switch label
                builder.Emit($"store i32 {status}, ptr %%status, align 4")
                builder.Emit("ret void")
            storeStatusAndReturn invalidRequestLabel NativeAbi.StatusInvalidRequest
            storeStatusAndReturn invalidReferenceLabel NativeAbi.StatusInvalidReference
            storeStatusAndReturn scratchCapacityLabel NativeAbi.StatusScratchCapacity
            storeStatusAndReturn retainedCapacityLabel NativeAbi.StatusRetainedCapacity
            storeStatusAndReturn invalidResultLabel NativeAbi.StatusInvalidRequest
            builder.Switch successLabel

        let emitValueLimitFailure (builder: CodeBuilder) currentWord wordSource span failure =
            let diagnostic =
                operationDiagnostic "RUNTIME_VALUE_LIMIT"
                    $"Runtime value exceeds the {failure.Dimension} safety limit."
                    currentWord span [ failure.Expected ] [ failure.Actual ]
            let failureLabel = builder.FreshLabel "value.limit.failure"
            let unreachableLabel = builder.FreshLabel "value.limit.continue"
            builder.Emit($"br label %%{failureLabel}")
            builder.Switch failureLabel
            emitFailure builder diagnostic wordSource
            builder.Switch unreachableLabel

        let emitValueLimitCheck (builder: CodeBuilder) currentWord wordSource span (stack: EmittedValue list) (locals: Map<LocalSlot, EmittedValue>) =
            let roots = (stack |> List.map (fun value -> value.Type)) @ (locals |> Map.toList |> List.map (fun (_, value) -> value.Type))
            valueMetrics.CheckRoots roots |> Option.iter (emitValueLimitFailure builder currentWord wordSource span)

        let emitErrorWhen (builder: CodeBuilder) (condition: string) (diagnostic: Diagnostic) wordSource =
            let failureLabel = builder.FreshLabel "failure"
            let continueLabel = builder.FreshLabel "continue"
            builder.Emit($"br i1 {condition}, label %%{failureLabel}, label %%{continueLabel}")
            builder.Switch failureLabel
            emitFailure builder diagnostic wordSource
            builder.Switch continueLabel

        let emitCharge (builder: CodeBuilder) (owner: string) wordSource site =
            let currentSpan = sourceSpan sourceMap site
            let diagnostic =
                operationDiagnostic "RUNTIME_STEP_LIMIT" "Execution exceeded the 10,000 instruction limit."
                    owner currentSpan [] []
            let field = emitFieldPointer builder 1
            let current = builder.Fresh "steps.current"
            let next = builder.Fresh "steps.next"
            let over = builder.Fresh "steps.over"
            builder.Emit($"{current} = load i32, ptr {field}, align 4")
            builder.Emit($"{next} = add i32 {current}, 1")
            builder.Emit($"store i32 {next}, ptr {field}, align 4")
            builder.Emit($"{over} = icmp ugt i32 {next}, 10000")
            emitErrorWhen builder over diagnostic wordSource

        let emitDepthGuardAt (builder: CodeBuilder) (depthValue: string) (call: IrResolvedCall) =
            let tooDeep = builder.Fresh "depth.exceeded"
            builder.Emit($"{tooDeep} = icmp ugt i32 {depthValue}, 64")
            let diagnostic =
                operationDiagnostic "RUNTIME_CALL_DEPTH" "Execution exceeded the 64 word call-depth limit."
                    call.ResolvedName (diagnosticSources.WordDefinitionSpans.TryFind call.ResolvedName) [] []
            emitErrorWhen builder tooDeep diagnostic (FixedWord call.ResolvedName)

        let emitBoolCast (builder: CodeBuilder) (value: string) =
            let result = builder.Fresh "bool.value"
            builder.Emit($"{result} = icmp ne i64 {value}, 0")
            result

        let emitZext (builder: CodeBuilder) (value: string) =
            let result = builder.Fresh "bool.slot"
            builder.Emit($"{result} = zext i1 {value} to i64")
            result

        let emitWorkspaceSlot (builder: CodeBuilder) (index: int) =
            let pointer = builder.Fresh "workspace.slot"
            builder.Emit($"{pointer} = getelementptr inbounds i64, ptr %%outputs, i64 {index}")
            pointer

        let emitOverflow (builder: CodeBuilder) operation resolvedName (left: string) (right: string) =
            let tuple = builder.Fresh "checked.tuple"
            let raw = builder.Fresh "checked.raw"
            let overflow = builder.Fresh "checked.overflow"
            let intrinsic =
                match operation with
                | "add" -> "llvm.sadd.with.overflow.i64"
                | "subtract" -> "llvm.ssub.with.overflow.i64"
                | "multiply" -> "llvm.smul.with.overflow.i64"
                | _ -> invalidOp $"No signed overflow intrinsic for {operation}."
            builder.Emit($"{tuple} = call {{ i64, i1 }} @{intrinsic}(i64 {left}, i64 {right})")
            builder.Emit($"{raw} = extractvalue {{ i64, i1 }} {tuple}, 0")
            builder.Emit($"{overflow} = extractvalue {{ i64, i1 }} {tuple}, 1")
            let diagnostic =
                operationDiagnostic "RUNTIME_OVERFLOW" $"'{operation}' overflowed its Int64 result."
                    operation (diagnosticSources.PrimitiveDefinitionSpans.TryFind resolvedName) [] []
            let failureLabel = builder.FreshLabel "overflow"
            let continueLabel = builder.FreshLabel "checked"
            builder.Emit($"br i1 {overflow}, label %%{failureLabel}, label %%{continueLabel}")
            builder.Switch failureLabel
            let arg0Pointer = emitFieldPointer builder 4
            let arg1Pointer = emitFieldPointer builder 5
            builder.Emit($"store i64 {left}, ptr {arg0Pointer}, align 8")
            builder.Emit($"store i64 {right}, ptr {arg1Pointer}, align 8")
            let metadataId = metadataFor metadata diagnostic (FixedWord operation) 2
            let metadataPointer = emitFieldPointer builder 2
            builder.Emit($"store i32 {metadataId}, ptr {metadataPointer}, align 4")
            let statusPointer = builder.Fresh "status.diagnostic"
            builder.Emit($"{statusPointer} = getelementptr inbounds i32, ptr %%status, i64 0")
            builder.Emit($"store i32 {NativeAbi.StatusDiagnostic}, ptr {statusPointer}, align 4")
            builder.Emit("ret void")
            builder.Switch continueLabel
            { Type = IrInt; Operand = raw }

        let emitPrimitive (builder: CodeBuilder) (call: IrResolvedCall) operation (arguments: EmittedValue list) =
            match operation, arguments with
            | "add", [ left; right ]
            | "subtract", [ left; right ]
            | "multiply", [ left; right ] ->
                [ emitOverflow builder operation call.ResolvedName left.Operand right.Operand ]
            | "divide", [ left; right ] ->
                let zero = builder.Fresh "divide.zero"
                builder.Emit($"{zero} = icmp eq i64 {right.Operand}, 0")
                let zeroDiagnostic =
                    operationDiagnostic "RUNTIME_DIVIDE_BY_ZERO" "Integer division by zero."
                        operation None [] []
                emitErrorWhen builder zero zeroDiagnostic (FixedWord operation)
                let min = builder.Fresh "divide.min"
                let negOne = builder.Fresh "divide.negative.one"
                let overflow = builder.Fresh "divide.overflow"
                builder.Emit($"{min} = icmp eq i64 {left.Operand}, -9223372036854775808")
                builder.Emit($"{negOne} = icmp eq i64 {right.Operand}, -1")
                builder.Emit($"{overflow} = and i1 {min}, {negOne}")
                let overflowDiagnostic =
                    operationDiagnostic "RUNTIME_OVERFLOW" "Integer division overflow."
                        operation None [] []
                emitErrorWhen builder overflow overflowDiagnostic (FixedWord operation)
                let result = builder.Fresh "divide.result"
                builder.Emit($"{result} = sdiv i64 {left.Operand}, {right.Operand}")
                [ { Type = IrInt; Operand = result } ]
            | "int.less-than", [ left; right ]
            | "int.greater-than", [ left; right ]
            | "int.less-or-equal", [ left; right ]
            | "int.greater-or-equal", [ left; right ] ->
                let predicate =
                    match operation with
                    | "int.less-than" -> "slt"
                    | "int.greater-than" -> "sgt"
                    | "int.less-or-equal" -> "sle"
                    | _ -> "sge"
                let comparison = builder.Fresh "int.compare"
                builder.Emit($"{comparison} = icmp {predicate} i64 {left.Operand}, {right.Operand}")
                [ { Type = IrBool; Operand = emitZext builder comparison } ]
            | "equals", [ left; right ] ->
                match left.Type with
                | IrNominal key ->
                    match program.NominalTypesByKey.TryFind key with
                    | Some(IrRecordDefinition _) ->
                        let typeId = nativeValueMetadata.TypeIdsByIrType[IrNominal key]
                        let outputPointer = emitWorkspaceSlot builder 0
                        let runtimeResult = builder.Fresh "record.equal.status"
                        builder.Emit($"{runtimeResult} = call i32 @al_runtime_equal(ptr %%ctx, ptr @agentlang_program, i32 {typeId}, i64 {left.Operand}, i64 {right.Operand}, ptr {outputPointer})")
                        emitRuntimeResult builder runtimeResult
                        let raw = builder.Fresh "record.equal.value"
                        builder.Emit($"{raw} = load i32, ptr {outputPointer}, align 4")
                        let result = builder.Fresh "record.equal.slot"
                        builder.Emit($"{result} = zext i32 {raw} to i64")
                        [ { Type = IrBool; Operand = result } ]
                    | _ ->
                        let comparison = builder.Fresh "scalar.equals"
                        builder.Emit($"{comparison} = icmp eq i64 {left.Operand}, {right.Operand}")
                        [ { Type = IrBool; Operand = emitZext builder comparison } ]
                | _ ->
                    let comparison = builder.Fresh "scalar.equals"
                    builder.Emit($"{comparison} = icmp eq i64 {left.Operand}, {right.Operand}")
                    [ { Type = IrBool; Operand = emitZext builder comparison } ]
            | "bool.and", [ left; right ]
            | "bool.or", [ left; right ] ->
                let leftBool = emitBoolCast builder left.Operand
                let rightBool = emitBoolCast builder right.Operand
                let result = builder.Fresh "bool.logic"
                let keyword = if operation = "bool.and" then "and" else "or"
                builder.Emit($"{result} = {keyword} i1 {leftBool}, {rightBool}")
                [ { Type = IrBool; Operand = emitZext builder result } ]
            | "bool.not", [ value ] ->
                let input = emitBoolCast builder value.Operand
                let result = builder.Fresh "bool.not"
                builder.Emit($"{result} = xor i1 {input}, true")
                [ { Type = IrBool; Operand = emitZext builder result } ]
            | "dup", [ value ] -> [ value; value ]
            | "drop", [ _ ] -> []
            | "swap", [ first; second ] -> [ second; first ]
            | _ -> invalidOp $"Validated primitive '{operation}' had an unsupported emitted signature."

        let emitFunction (functionName: string) (functionLabel: string) (functionInputs: IrType list) (functionOutputs: IrType list)
            (localNames: Map<LocalSlot, string>) (block: IrBlock) (isEntry: bool) =
            let builder = CodeBuilder()
            let inputParameters =
                functionInputs
                |> List.mapi (fun index _ -> $"i64 %%arg{index}")
                |> String.concat ", "
            let inputSuffix = if inputParameters.Length = 0 then "" else ", " + inputParameters
            output.AppendLine($"define internal void {functionLabel}(ptr %%ctx, ptr %%outputs, ptr %%status, i32 %%depth{inputSuffix}) {{") |> ignore
            output.AppendLine("entry:") |> ignore
            let entryValues =
                functionInputs
                |> List.mapi (fun index ty ->
                    ensureNativeType program functionName None ty
                    { Type = ty; Operand = $"%%arg{index}" })
            let rec emitBlock (currentWord: string) (wordSource: ErrorWord) (block: IrBlock) (startStack: EmittedValue list) (startLocals: Map<LocalSlot, EmittedValue>) =
                let mutable stack = startStack
                let mutable localValues = startLocals
                emitValueLimitCheck builder currentWord wordSource None stack localValues
                let popOne () =
                    match stack with
                    | [] -> invalidOp "Verified LLVM IR unexpectedly underflowed its operand stack."
                    | values ->
                        stack <- values |> List.take (values.Length - 1)
                        List.last values
                let popArguments count =
                    if stack.Length < count then invalidOp "Verified LLVM IR unexpectedly underflowed a call."
                    let prefix = stack |> List.take (stack.Length - count)
                    let arguments = stack |> List.skip (stack.Length - count)
                    stack <- prefix
                    arguments
                let rec emitResolvedCall
                    (call: IrResolvedCall)
                    (arguments: EmittedValue list)
                    (depthValue: string)
                    (callSiteSpan: SourceSpan option)
                    (recordValidator: IrResolvedCall option) =
                    emitDepthGuardAt builder depthValue call
                    match call.ResolvedTarget with
                    | PrimitiveTarget(PrimitiveId operation) -> emitPrimitive builder call operation arguments
                    | UserWordTarget(id, revision) -> emitUserCall call id revision arguments depthValue
                    | GeneratedWordTarget(id, revision) ->
                        match program.GeneratedTargetsById.TryFind id with
                        | Some target when target.TargetRevision = revision ->
                            emitGeneratedScalarCall call target arguments depthValue callSiteSpan recordValidator
                        | Some target -> invalidOp $"Validated generated target '{call.ResolvedName}' changed revision to {target.TargetRevision}."
                        | None -> invalidOp $"Validated generated target '{call.ResolvedName}' was absent from the bound program."
                and emitUserCall (call: IrResolvedCall) (id: WordId) (revision: int) (arguments: EmittedValue list) (callerDepth: string) =
                    let target = functionSymbol id revision
                    let nextDepth = builder.Fresh "call.depth"
                    builder.Emit($"{nextDepth} = add i32 {callerDepth}, 1")
                    let inputArguments =
                        arguments
                        |> List.map (fun argument -> "i64 " + argument.Operand)
                        |> String.concat ", "
                    let inputSuffix = if inputArguments.Length = 0 then "" else ", " + inputArguments
                    builder.Emit($"call void {target}(ptr %%ctx, ptr %%outputs, ptr %%status, i32 {nextDepth}{inputSuffix})")
                    let returnedStatus = builder.Fresh "callee.status"
                    let succeeded = builder.Fresh "callee.succeeded"
                    let failureLabel = builder.FreshLabel "callee.failure"
                    let continueLabel = builder.FreshLabel "callee.continue"
                    builder.Emit($"{returnedStatus} = load i32, ptr %%status, align 4")
                    builder.Emit($"{succeeded} = icmp eq i32 {returnedStatus}, 0")
                    builder.Emit($"br i1 {succeeded}, label %%{continueLabel}, label %%{failureLabel}")
                    builder.Switch failureLabel
                    builder.Emit("ret void")
                    builder.Switch continueLabel
                    call.OutputTypes
                    |> List.mapi (fun index ty ->
                        let pointer = builder.Fresh "call.output.ptr"
                        let value = builder.Fresh "call.output"
                        builder.Emit($"{pointer} = getelementptr inbounds i64, ptr %%outputs, i64 {index}")
                        builder.Emit($"{value} = load i64, ptr {pointer}, align 8")
                        { Type = ty; Operand = value })
                and emitGeneratedScalarCall
                    (call: IrResolvedCall)
                    (target: IrGeneratedTarget)
                    (arguments: EmittedValue list)
                    (depthValue: string)
                    (callSiteSpan: SourceSpan option)
                    (recordValidator: IrResolvedCall option) =
                    match target.Operation, arguments with
                    | WrapScalarOperation key, [ value ] ->
                        match program.NominalTypesByKey.TryFind key with
                        | Some(IrScalarDefinition scalar) ->
                            let nominalValue = { Type = IrNominal key; Operand = value.Operand }
                            match scalar.ValidatorCall with
                            | None -> [ nominalValue ]
                            | Some validator ->
                                // Generated construction itself consumes the caller's current depth.
                                // The validator call is one level deeper, with a user body one beyond that.
                                let validatorDepth = builder.Fresh "validator.depth"
                                builder.Emit($"{validatorDepth} = add i32 {depthValue}, 1")
                                match emitResolvedCall validator [ value ] validatorDepth None None with
                                | [ checkedValue ] ->
                                    let accepted = builder.Fresh "validator.accepted"
                                    builder.Emit($"{accepted} = icmp ne i64 {checkedValue.Operand}, 0")
                                    let diagnostic =
                                        operationDiagnostic "REFINEMENT_FAILED"
                                            $"Value does not satisfy {scalar.TypeName}'s refinement validator."
                                            call.ResolvedName (diagnosticSources.WordDefinitionSpans.TryFind call.ResolvedName)
                                            [ "validator returns true" ] [ "false" ]
                                    let failed = builder.Fresh "validator.failed"
                                    builder.Emit($"{failed} = xor i1 {accepted}, true")
                                    emitErrorWhen builder failed diagnostic (FixedWord call.ResolvedName)
                                    [ nominalValue ]
                                | _ -> invalidOp "Validated scalar validator did not return exactly one Bool."
                        | _ -> invalidOp "Validated scalar constructor referred to a non-scalar nominal type."
                    | UnwrapScalarOperation key, [ value ] ->
                        match program.NominalTypesByKey.TryFind key with
                        | Some(IrScalarDefinition scalar) -> [ { Type = scalar.BaseType; Operand = value.Operand } ]
                        | _ -> invalidOp "Validated scalar accessor referred to a non-scalar nominal type."
                    | MakeRecordOperation key, fields ->
                        match program.NominalTypesByKey.TryFind key with
                        | Some(IrRecordDefinition record) ->
                            let declaredFields = record.RecordFields |> List.sortBy (fun field -> field.FieldIndex)
                            if fields.Length <> declaredFields.Length then
                                invalidOp "Validated record constructor received a different field count."
                            let validator =
                                match recordValidator with
                                | Some supplied when Some supplied = record.ValidatorCall -> Some supplied
                                | Some _ -> invalidOp "Validated record-construction validator differs from its frozen type definition."
                                | None -> record.ValidatorCall
                            for index, value in List.indexed fields do
                                let fieldPointer = emitWorkspaceSlot builder index
                                builder.Emit($"store i64 {value.Operand}, ptr {fieldPointer}, align 8")
                            let outputPointer = emitWorkspaceSlot builder fields.Length
                            let typeId = nativeValueMetadata.TypeIdsByIrType[IrNominal key]
                            let runtimeResult = builder.Fresh "record.make.status"
                            builder.Emit($"{runtimeResult} = call i32 @al_runtime_make_record(ptr %%ctx, ptr @agentlang_program, i32 {typeId}, ptr %%outputs, i32 {fields.Length}, ptr {outputPointer})")
                            emitRuntimeResult builder runtimeResult
                            let handle = builder.Fresh "record.make.handle"
                            builder.Emit($"{handle} = load i64, ptr {outputPointer}, align 8")
                            let constructed = { Type = IrNominal key; Operand = handle }
                            match validator with
                            | None -> [ constructed ]
                            | Some checkedCall ->
                                let validatorDepth = builder.Fresh "record.validator.depth"
                                builder.Emit($"{validatorDepth} = add i32 {depthValue}, 1")
                                match emitResolvedCall checkedCall [ constructed ] validatorDepth callSiteSpan None with
                                | [ checkedValue ] ->
                                    let acceptedLabel = builder.FreshLabel "record.validator.accepted"
                                    let rejectedLabel = builder.FreshLabel "record.validator.rejected"
                                    let malformedLabel = builder.FreshLabel "record.validator.malformed"
                                    builder.Emit(
                                        $"switch i64 {checkedValue.Operand}, label %%{malformedLabel} [ " +
                                        $"i64 0, label %%{rejectedLabel} i64 1, label %%{acceptedLabel} ]")
                                    builder.Switch rejectedLabel
                                    let typeName = record.TypeName
                                    let diagnostic =
                                        operationDiagnostic "RECORD_VALIDATION_FAILED"
                                            $"Value does not satisfy {typeName}'s validation predicate."
                                            call.ResolvedName callSiteSpan [ "validator returns true" ] [ "false" ]
                                    emitFailure builder diagnostic (FixedWord call.ResolvedName)
                                    builder.Switch malformedLabel
                                    let malformedDiagnostic =
                                        operationDiagnostic "RUNTIME_VALIDATOR_RESULT" "Record validator did not return one Bool."
                                            checkedCall.ResolvedName callSiteSpan [ "Bool" ] []
                                    emitFailureWithActualOperands builder malformedDiagnostic (FixedWord checkedCall.ResolvedName) [ checkedValue.Operand ]
                                    builder.Switch acceptedLabel
                                    [ constructed ]
                                | _ -> invalidOp "Validated record validator did not return exactly one Bool."
                        | _ -> invalidOp "Validated generated record constructor referred to a non-record type."
                    | GetRecordFieldOperation(key, fieldIndex), [ recordValue ] ->
                        match program.NominalTypesByKey.TryFind key with
                        | Some(IrRecordDefinition record) when fieldIndex >= 0 && fieldIndex < record.RecordFields.Length ->
                            let outputPointer = emitWorkspaceSlot builder 0
                            let typeId = nativeValueMetadata.TypeIdsByIrType[IrNominal key]
                            let runtimeResult = builder.Fresh "record.field.status"
                            builder.Emit($"{runtimeResult} = call i32 @al_runtime_get_field(ptr %%ctx, ptr @agentlang_program, i32 {typeId}, i64 {recordValue.Operand}, i32 {fieldIndex}, ptr {outputPointer})")
                            emitRuntimeResult builder runtimeResult
                            let field = record.RecordFields |> List.sortBy (fun item -> item.FieldIndex) |> List.item fieldIndex
                            let raw = builder.Fresh "record.field.value"
                            builder.Emit($"{raw} = load i64, ptr {outputPointer}, align 8")
                            [ { Type = field.FieldType; Operand = raw } ]
                        | _ -> invalidOp "Validated generated record accessor referred to an absent record field."
                    | _ -> invalidOp "Validated generated target is not a supported scalar constructor or accessor."
                for instruction in block.Code do
                    emitCharge builder currentWord wordSource instruction.Site
                    let span = sourceSpan sourceMap instruction.Site
                    match instruction.Operation with
                    | IrOperation.Constant(LInt value, IrInt) ->
                        stack <- stack @ [ { Type = IrInt; Operand = value.ToString(CultureInfo.InvariantCulture) } ]
                    | IrOperation.Constant(LBool value, IrBool) ->
                        stack <- stack @ [ { Type = IrBool; Operand = if value then "1" else "0" } ]
                    | IrOperation.Constant(LUnit, IrUnit) ->
                        stack <- stack @ [ { Type = IrUnit; Operand = "0" } ]
                    | IrOperation.Constant _ -> invalidOp "LLVM validation missed an unsupported constant."
                    | IrOperation.StoreLocal slot ->
                        let value = popOne ()
                        localValues <- Map.add slot value localValues
                    | IrOperation.LoadLocal slot ->
                        match localValues.TryFind slot with
                        | Some value -> stack <- stack @ [ value ]
                        | None ->
                            let localName = localNames.TryFind slot |> Option.defaultValue (sprintf "%A" slot)
                            let diagnostic =
                                operationDiagnostic "RUNTIME_UNKNOWN_LOCAL" ("Local '$" + localName + "' has not been bound.")
                                    currentWord span [] [ localName ]
                            let failureLabel = builder.FreshLabel "unknown.local"
                            let okLabel = builder.FreshLabel "local.ok"
                            builder.Emit($"br label %%{failureLabel}")
                            builder.Switch failureLabel
                            emitFailure builder diagnostic wordSource
                            builder.Switch okLabel
                    | IrOperation.Scope innerBlock ->
                        let resultStack, _ = emitBlock currentWord wordSource innerBlock stack localValues
                        stack <- resultStack
                    | IrOperation.If(thenBlock, elseBlock) ->
                        let condition = popOne ()
                        let prefix = stack
                        let truth = builder.Fresh "if.condition"
                        let thenLabel = builder.FreshLabel "if.then"
                        let elseLabel = builder.FreshLabel "if.else"
                        let mergeLabel = builder.FreshLabel "if.merge"
                        builder.Emit($"{truth} = icmp ne i64 {condition.Operand}, 0")
                        builder.Emit($"br i1 {truth}, label %%{thenLabel}, label %%{elseLabel}")
                        builder.Switch thenLabel
                        let thenStack, thenLocals = emitBlock currentWord wordSource thenBlock prefix localValues
                        let thenEnd = builder.CurrentLabel
                        builder.Emit($"br label %%{mergeLabel}")
                        builder.Switch elseLabel
                        let elseStack, elseLocals = emitBlock currentWord wordSource elseBlock prefix localValues
                        let elseEnd = builder.CurrentLabel
                        builder.Emit($"br label %%{mergeLabel}")
                        builder.Switch mergeLabel
                        if thenStack.Length <> elseStack.Length then invalidOp "Verified LLVM IR branch stack shapes did not join."
                        stack <-
                            List.map2 (fun left right ->
                                if left.Type <> right.Type then invalidOp "Verified LLVM IR branch value types did not join."
                                let phi = builder.Fresh "stack.phi"
                                builder.Emit($"{phi} = phi i64 [ {left.Operand}, %%{thenEnd} ], [ {right.Operand}, %%{elseEnd} ]")
                                { Type = left.Type; Operand = phi })
                                thenStack elseStack
                        if (thenLocals |> Map.toList |> List.map fst) <> (elseLocals |> Map.toList |> List.map fst) then
                            invalidOp "Verified LLVM IR branch local shapes did not join."
                        localValues <-
                            Map.map (fun slot left ->
                                let right = elseLocals[slot]
                                if left.Type <> right.Type then invalidOp "Verified LLVM IR branch local types did not join."
                                let phi = builder.Fresh "local.phi"
                                builder.Emit($"{phi} = phi i64 [ {left.Operand}, %%{thenEnd} ], [ {right.Operand}, %%{elseEnd} ]")
                                { Type = left.Type; Operand = phi })
                                thenLocals
                    | IrOperation.MakeRecord(call, _, validator) ->
                        let arguments = popArguments call.InputTypes.Length
                        stack <- stack @ emitResolvedCall call arguments "%depth" span validator
                    | IrOperation.Call call
                    | IrOperation.GetRecordField(call, _, _)
                    | IrOperation.WrapScalar(call, _, _)
                    | IrOperation.UnwrapScalar(call, _) ->
                        let arguments = popArguments call.InputTypes.Length
                        stack <- stack @ emitResolvedCall call arguments "%depth" span None
                    | _ -> invalidOp "LLVM validation missed an unsupported operation."
                    emitValueLimitCheck builder currentWord wordSource span stack localValues
                stack, localValues
            let resultStack =
                let source = if isEntry then EntryExecutionName else FixedWord functionName
                emitBlock functionName source block entryValues Map.empty |> fst
            if resultStack.Length <> functionOutputs.Length then
                invalidOp "Verified LLVM body exit stack differed from its declared outputs."
            if isEntry then
                emitValueLimitCheck builder functionName EntryExecutionName None resultStack Map.empty
            for (index, (value, expected)) in List.indexed (List.zip resultStack functionOutputs) do
                if value.Type <> expected then invalidOp "Verified LLVM output type differed from its declared signature."
                let pointer = builder.Fresh "output.ptr"
                builder.Emit($"{pointer} = getelementptr inbounds i64, ptr %%outputs, i64 {index}")
                builder.Emit($"store i64 {value.Operand}, ptr {pointer}, align 8")
            builder.Emit("ret void")
            output.Append(builder.Text) |> ignore
            output.AppendLine("}") |> ignore
            output.AppendLine() |> ignore

        emitFunction body.BodyName "@agentlang_body" body.BodyInputTypes body.BodyOutputTypes body.BodyLocalNames body.BodyBlock true
        for _, functionValue in functions do
            emitFunction functionValue.FunctionName (functionSymbol functionValue.FunctionId functionValue.FunctionRevision)
                functionValue.InputTypes functionValue.OutputTypes functionValue.LocalNames functionValue.FunctionBody false

        output.AppendLine($"define dllexport i32 @{symbols.OutputCapacity}() {{ ret i32 {workspaceCapacity} }}") |> ignore
        output.AppendLine() |> ignore
        output.AppendLine($"define dllexport void @{symbols.AbiLayout}(ptr %%output) {{") |> ignore
        output.AppendLine("entry:") |> ignore
        output.AppendLine("  %size.ptr = getelementptr %NativeExecutionContext, ptr null, i32 1") |> ignore
        output.AppendLine("  %size = ptrtoint ptr %size.ptr to i64") |> ignore
        output.AppendLine("  %align.ptr = getelementptr %NativeExecutionContextAlignmentProbe, ptr null, i32 0, i32 1") |> ignore
        output.AppendLine("  %alignment = ptrtoint ptr %align.ptr to i64") |> ignore
        for index in 0 .. 15 do
            output.AppendLine($"  %%field{index}.ptr = getelementptr %%NativeExecutionContext, ptr null, i32 0, i32 {index}") |> ignore
            output.AppendLine($"  %%field{index} = ptrtoint ptr %%field{index}.ptr to i64") |> ignore
        output.AppendLine("  %items = alloca [18 x i64], align 8") |> ignore
        for index in 0 .. 17 do
            let valueName = if index = 0 then "%size" elif index = 1 then "%alignment" else $"%%field{index - 2}"
            output.AppendLine($"  %%out{index} = getelementptr inbounds [18 x i64], ptr %%items, i64 0, i64 {index}") |> ignore
            output.AppendLine($"  store i64 {valueName}, ptr %%out{index}, align 8") |> ignore
        for index in 0 .. 17 do
            output.AppendLine($"  %%item{index} = getelementptr inbounds [18 x i64], ptr %%items, i64 0, i64 {index}") |> ignore
            output.AppendLine($"  %%value{index} = load i64, ptr %%item{index}, align 8") |> ignore
            output.AppendLine($"  %%dest{index} = getelementptr inbounds i64, ptr %%output, i64 {index}") |> ignore
            output.AppendLine($"  store i64 %%value{index}, ptr %%dest{index}, align 8") |> ignore
        output.AppendLine("  ret void") |> ignore
        output.AppendLine("}") |> ignore
        output.AppendLine() |> ignore

        output.AppendLine($"define dllexport void @{symbols.Execute}(ptr %%ctx, ptr %%outputs, i32 %%capacity, ptr %%status) {{") |> ignore
        output.AppendLine("entry:") |> ignore
        output.AppendLine($"  %%validation.result = call i32 @al_runtime_validate_request(ptr %%ctx, ptr %%outputs, i32 {outputCapacity}, i32 %%capacity, ptr %%status, i32 {workspaceCapacity})") |> ignore
        output.AppendLine("  %request.valid = icmp eq i32 %validation.result, 0") |> ignore
        output.AppendLine("  br i1 %request.valid, label %request.accepted, label %request.rejected") |> ignore
        output.AppendLine("request.rejected:") |> ignore
        output.AppendLine("  ret void") |> ignore
        output.AppendLine("request.accepted:") |> ignore
        output.AppendLine("  %steps.ptr = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 1") |> ignore
        output.AppendLine("  store i32 0, ptr %steps.ptr, align 4") |> ignore
        output.AppendLine("  %error.ptr = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 2") |> ignore
        output.AppendLine("  store i32 -1, ptr %error.ptr, align 4") |> ignore
        output.AppendLine("  %reserved.ptr = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 3") |> ignore
        output.AppendLine("  store i32 0, ptr %reserved.ptr, align 4") |> ignore
        output.AppendLine("  %arg0.ptr = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 4") |> ignore
        output.AppendLine("  store i64 0, ptr %arg0.ptr, align 8") |> ignore
        output.AppendLine("  %arg1.ptr = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 5") |> ignore
        output.AppendLine("  store i64 0, ptr %arg1.ptr, align 8") |> ignore
        output.AppendLine("  %workspace.ptr = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 8") |> ignore
        output.AppendLine("  %workspace = load ptr, ptr %workspace.ptr, align 8") |> ignore
        let inputTypePointer = if inputTypeIds.Length = 0 then "ptr null" else "ptr @agentlang_input_type_ids"
        output.AppendLine($"  %%import.result = call i32 @al_runtime_import_state(ptr %%ctx, ptr @agentlang_program, {inputTypePointer}, i32 {inputTypeIds.Length})") |> ignore
        output.AppendLine("  switch i32 %import.result, label %import.invalid [ i32 0, label %import.succeeded i32 1, label %import.invalid.request i32 2, label %import.invalid.reference i32 3, label %import.scratch.capacity i32 4, label %import.retained.capacity ]") |> ignore
        output.AppendLine("import.invalid:") |> ignore
        output.AppendLine($"  store i32 {NativeAbi.StatusInvalidRequest}, ptr %%status, align 4") |> ignore
        output.AppendLine("  ret void") |> ignore
        output.AppendLine("import.invalid.request:") |> ignore
        output.AppendLine($"  store i32 {NativeAbi.StatusInvalidRequest}, ptr %%status, align 4") |> ignore
        output.AppendLine("  ret void") |> ignore
        output.AppendLine("import.invalid.reference:") |> ignore
        output.AppendLine($"  store i32 {NativeAbi.StatusInvalidReference}, ptr %%status, align 4") |> ignore
        output.AppendLine("  ret void") |> ignore
        output.AppendLine("import.scratch.capacity:") |> ignore
        output.AppendLine($"  store i32 {NativeAbi.StatusScratchCapacity}, ptr %%status, align 4") |> ignore
        output.AppendLine("  ret void") |> ignore
        output.AppendLine("import.retained.capacity:") |> ignore
        output.AppendLine($"  store i32 {NativeAbi.StatusRetainedCapacity}, ptr %%status, align 4") |> ignore
        output.AppendLine("  ret void") |> ignore
        output.AppendLine("import.succeeded:") |> ignore
        let entryArgumentOperands =
            body.BodyInputTypes
            |> List.mapi (fun index _ ->
                output.AppendLine($"  %%entry.arg{index}.ptr = getelementptr inbounds i64, ptr %%workspace, i64 {index}") |> ignore
                output.AppendLine($"  %%entry.arg{index} = load i64, ptr %%entry.arg{index}.ptr, align 8") |> ignore
                $"i64 %%entry.arg{index}")
        let entryArgumentSuffix = if List.isEmpty entryArgumentOperands then "" else ", " + String.concat ", " entryArgumentOperands
        output.AppendLine($"  call void @agentlang_body(ptr %%ctx, ptr %%workspace, ptr %%status, i32 0{entryArgumentSuffix})") |> ignore
        output.AppendLine("  %body.status = load i32, ptr %status, align 4") |> ignore
        output.AppendLine("  %body.succeeded = icmp eq i32 %body.status, 0") |> ignore
        output.AppendLine("  br i1 %body.succeeded, label %promote, label %body.failed") |> ignore
        output.AppendLine("body.failed:") |> ignore
        output.AppendLine("  ret void") |> ignore
        output.AppendLine("promote:") |> ignore
        output.AppendLine($"  %%promote.result = call i32 @al_runtime_promote(ptr %%ctx, ptr @agentlang_program, ptr %%workspace, ptr @agentlang_root_type_ids, i32 {rootTypeIds.Length}, ptr %%outputs, i32 %%capacity)") |> ignore
        output.AppendLine("  switch i32 %promote.result, label %promote.invalid [ i32 0, label %promote.succeeded i32 1, label %promote.invalid.request i32 2, label %promote.invalid.reference i32 3, label %promote.scratch.capacity i32 4, label %promote.retained.capacity ]") |> ignore
        output.AppendLine("promote.invalid:") |> ignore
        output.AppendLine($"  store i32 {NativeAbi.StatusInvalidRequest}, ptr %%status, align 4") |> ignore
        output.AppendLine("  ret void") |> ignore
        output.AppendLine("promote.invalid.request:") |> ignore
        output.AppendLine($"  store i32 {NativeAbi.StatusInvalidRequest}, ptr %%status, align 4") |> ignore
        output.AppendLine("  ret void") |> ignore
        output.AppendLine("promote.invalid.reference:") |> ignore
        output.AppendLine($"  store i32 {NativeAbi.StatusInvalidReference}, ptr %%status, align 4") |> ignore
        output.AppendLine("  ret void") |> ignore
        output.AppendLine("promote.scratch.capacity:") |> ignore
        output.AppendLine($"  store i32 {NativeAbi.StatusScratchCapacity}, ptr %%status, align 4") |> ignore
        output.AppendLine("  ret void") |> ignore
        output.AppendLine("promote.retained.capacity:") |> ignore
        output.AppendLine($"  store i32 {NativeAbi.StatusRetainedCapacity}, ptr %%status, align 4") |> ignore
        output.AppendLine("  ret void") |> ignore
        output.AppendLine("promote.succeeded:") |> ignore
        output.AppendLine("  ret void") |> ignore
        output.AppendLine("}") |> ignore

        output.ToString(), metadata.ToArray(), body.BodyInputTypes, body.BodyOutputTypes, outputCapacity, workspaceCapacity, requiresRecordArenas, nativeValueMetadata

    /// Emit deterministic LLVM IR from the exact compiler-verified body and its
    /// reachable verified user-word closure. No source is reparsed or relowered.
    let emit (verifiedBody: VerifiedIrBody) =
        let symbols =
            { OutputCapacity = "agentlang_output_capacity"
              AbiLayout = "agentlang_abi_layout"
              Execute = "agentlang_execute" }
        emitModule NativeDiagnosticSources.empty symbols verifiedBody |> fun (llvmIr, _, _, _, _, _, _, _) -> llvmIr

    let private writeEmbeddedResource (assembly: Reflection.Assembly) resourceName outputPath =
        use source = assembly.GetManifestResourceStream resourceName
        if isNull source then invalidOp $"Embedded native runtime resource '{resourceName}' was not found."
        use destination = File.Create outputPath
        source.CopyTo destination

    /// Compile one effect-free scalar body to a dependency-free Windows x64
    /// DLL. The output directory retains the LLVM IR/object/DLL for inspection.
    let compile (toolchain: LlvmToolchain) optimization outputDirectory (diagnosticSources: NativeDiagnosticSources) (verifiedBody: VerifiedIrBody) =
        if String.IsNullOrWhiteSpace outputDirectory then invalidArg (nameof outputDirectory) "Output directory must be nonempty."
        let llvmIr, metadata, inputTypes, outputTypes, outputCapacity, workspaceCapacity, requiresRecordArenas, valueMetadata =
            emitModule diagnosticSources
                { OutputCapacity = "agentlang_output_capacity"
                  AbiLayout = "agentlang_abi_layout"
                  Execute = "agentlang_execute" }
                verifiedBody
        let fullDirectory = Path.GetFullPath outputDirectory
        Directory.CreateDirectory fullDirectory |> ignore
        let llvmIrPath = Path.Combine(fullDirectory, "agentlang-native.ll")
        let libraryPath = Path.Combine(fullDirectory, "agentlang-native.dll")
        File.WriteAllText(llvmIrPath, llvmIr, UTF8Encoding(false))
        let runtimeDirectory = Path.Combine(fullDirectory, "native-runtime")
        Directory.CreateDirectory runtimeDirectory |> ignore
        let assembly = typeof<NativeCompiledProgram>.Assembly
        let runtimeHeaderPath = Path.Combine(runtimeDirectory, "arena_runtime.h")
        let runtimeSourcePath = Path.Combine(runtimeDirectory, "arena_runtime.c")
        writeEmbeddedResource assembly "AgentLang.Llvm.native.arena_runtime.h" runtimeHeaderPath
        writeEmbeddedResource assembly "AgentLang.Llvm.native.arena_runtime.c" runtimeSourcePath
        let compiledPath =
            LlvmToolchain.compileLibraryWithRuntime toolchain optimization llvmIrPath runtimeSourcePath runtimeDirectory libraryPath
        let outputTypeIds = outputTypes |> List.map (fun ty -> valueMetadata.TypeIdsByIrType[ty]) |> List.toArray
        new NativeCompiledProgram(
            compiledPath, outputTypeIds,
            inputTypes |> List.toArray,
            inputTypes |> List.map (fun ty -> valueMetadata.TypeIdsByIrType[ty]) |> List.toArray,
            outputCapacity, workspaceCapacity,
            requiresRecordArenas,
            VerifiedIrBody.program verifiedBody,
            valueMetadata, metadata)

    let private moduleEntrySymbols (index: int) =
        let suffix = index.ToString("D3", CultureInfo.InvariantCulture)
        { OutputCapacity = "agentlang_entry_" + suffix + "_output_capacity"
          AbiLayout = "agentlang_entry_" + suffix + "_abi_layout"
          Execute = "agentlang_entry_" + suffix + "_execute" }

    let private nativeTypeName (metadata: NativeValueTypeMetadata) =
        match metadata.Definition with
        | Some(NativeScalarMetadata(name, _))
        | Some(NativeRecordMetadata(name, _)) -> name
        | None -> IrTypes.format metadata.ValueType

    let private nativeTypeNameForIrType (metadata: NativeProgramMetadata) (valueType: IrType) =
        let typeId = metadata.TypeIdsByIrType[valueType]
        nativeTypeName metadata.Types[int typeId]

    let private manifestDiagnostic entryName id (error: ErrorMetadata) =
        let diagnostic = error.Diagnostic
        let word =
            match error.WordSource with
            | FixedWord value -> value
            | EntryExecutionName -> entryName
        let file, line, column, length =
            match diagnostic.Span with
            | Some source -> source.File, source.Line, source.Column, source.Length
            | None -> "", 0, 0, 0
        { Id = id
          Code = diagnostic.Code
          Message = diagnostic.Message
          Word = word
          File = file
          Line = line
          Column = column
          Length = length
          Expected = diagnostic.Expected
          Actual = diagnostic.Actual }

    let private moduleSemanticManifest (emitted: EmittedModuleEntry array) (valueMetadata: NativeProgramMetadata) =
        let types =
            valueMetadata.Types
            |> Array.map (fun item ->
                let fields =
                    match item.Definition with
                    | Some(NativeRecordMetadata(_, fields)) ->
                        fields |> List.map (fun (name, typeId) -> { Name = name; TypeId = typeId })
                    | _ -> []
                { Id = item.TypeId
                  Name = nativeTypeName item
                  Kind = item.Kind
                  Fields = fields })
            |> Array.toList
        let entries =
            emitted
            |> Array.map (fun item ->
                let inputTypeIds = item.InputTypes |> List.map (fun ty -> valueMetadata.TypeIdsByIrType[ty])
                let outputTypeIds = item.OutputTypes |> List.map (fun ty -> valueMetadata.TypeIdsByIrType[ty])
                { Id = item.Id
                  Name = item.Name
                  InputTypeIds = inputTypeIds
                  OutputTypeIds = outputTypeIds
                  InputTypes = item.InputTypes |> List.map (nativeTypeNameForIrType valueMetadata)
                  OutputTypes = item.OutputTypes |> List.map (nativeTypeNameForIrType valueMetadata)
                  WorkspaceCapacity = item.WorkspaceCapacity
                  RequiresRecordArenas = item.RequiresRecordArenas
                  Diagnostics = item.Metadata |> Array.mapi (manifestDiagnostic item.Name) |> Array.toList
                  IrSha256 =
                    item.LlvmIr
                    |> Encoding.UTF8.GetBytes
                    |> SHA256.HashData
                    |> Convert.ToHexString
                    |> fun hex -> hex.ToLowerInvariant() })
            |> Array.toList
        { ModuleAbiVersion = 1u
          RuntimeAbiVersion = NativeAbi.Version
          Target = "Windows x64"
          Types = types
          Entries = entries }

    let private writeStringArray (writer: Utf8JsonWriter) (name: string) (values: string list) =
        writer.WriteStartArray name
        for value in values do writer.WriteStringValue value
        writer.WriteEndArray()

    let private writeUIntArray (writer: Utf8JsonWriter) (name: string) (values: uint32 list) =
        writer.WriteStartArray name
        for value in values do writer.WriteNumberValue value
        writer.WriteEndArray()

    let private writeSemanticJson (writer: Utf8JsonWriter) (semantic: ModuleManifestSemantic) =
        writer.WriteStartObject()
        writer.WriteNumber("moduleAbiVersion", semantic.ModuleAbiVersion)
        writer.WriteNumber("runtimeAbiVersion", semantic.RuntimeAbiVersion)
        writer.WriteString("target", semantic.Target)
        writer.WriteStartArray("types")
        for item in semantic.Types do
            writer.WriteStartObject()
            writer.WriteNumber("id", item.Id)
            writer.WriteString("name", item.Name)
            writer.WriteNumber("kind", item.Kind)
            writer.WriteStartArray("fields")
            for field in item.Fields do
                writer.WriteStartObject()
                writer.WriteString("name", field.Name)
                writer.WriteNumber("typeId", field.TypeId)
                writer.WriteEndObject()
            writer.WriteEndArray()
            writer.WriteEndObject()
        writer.WriteEndArray()
        writer.WriteStartArray("entries")
        for item in semantic.Entries do
            writer.WriteStartObject()
            writer.WriteNumber("id", item.Id)
            writer.WriteString("name", item.Name)
            writeUIntArray writer "inputTypeIds" item.InputTypeIds
            writeUIntArray writer "outputTypeIds" item.OutputTypeIds
            writeStringArray writer "inputTypes" item.InputTypes
            writeStringArray writer "outputTypes" item.OutputTypes
            writer.WriteNumber("workspaceCapacity", item.WorkspaceCapacity)
            writer.WriteBoolean("requiresRecordArenas", item.RequiresRecordArenas)
            writer.WriteString("irSha256", item.IrSha256)
            writer.WriteStartArray("diagnostics")
            for diagnostic in item.Diagnostics do
                writer.WriteStartObject()
                writer.WriteNumber("id", diagnostic.Id)
                writer.WriteString("code", diagnostic.Code)
                writer.WriteString("message", diagnostic.Message)
                writer.WriteString("word", diagnostic.Word)
                writer.WriteString("file", diagnostic.File)
                writer.WriteNumber("line", diagnostic.Line)
                writer.WriteNumber("column", diagnostic.Column)
                writer.WriteNumber("length", diagnostic.Length)
                writeStringArray writer "expected" diagnostic.Expected
                writeStringArray writer "actual" diagnostic.Actual
                writer.WriteEndObject()
            writer.WriteEndArray()
            writer.WriteEndObject()
        writer.WriteEndArray()
        writer.WriteEndObject()

    let private serializeSemantic (semantic: ModuleManifestSemantic) writeIndented =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = writeIndented))
        writeSemanticJson writer semantic
        writer.Flush()
        stream.ToArray()

    let private manifestFingerprint (semantic: ModuleManifestSemantic) =
        serializeSemantic semantic false
        |> SHA256.HashData
        |> Convert.ToHexString
        |> fun hex -> hex.ToLowerInvariant()

    let private serializeManifest (fingerprint: string) (optimization: string) (sourceHashes: (string * string) list) (binaryHash: string) (semantic: ModuleManifestSemantic) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = true))
        writer.WriteStartObject()
        writer.WriteString("fingerprint", fingerprint)
        writer.WriteString("optimization", optimization)
        writer.WriteStartObject("sourceSha256")
        for name, hash in sourceHashes do writer.WriteString(name, hash)
        writer.WriteEndObject()
        writer.WriteString("binarySha256", binaryHash)
        writer.WritePropertyName("module")
        writeSemanticJson writer semantic
        writer.WriteEndObject()
        writer.Flush()
        UTF8Encoding(false).GetString(stream.ToArray())

    let private cStringLiteral (value: string) =
        if isNull value then invalidArg (nameof value) "Native module metadata strings cannot be null."
        if value.IndexOf('\000') >= 0 then invalidArg (nameof value) "Native module metadata strings cannot contain NUL."
        let literal = StringBuilder("\"")
        for valueByte in Encoding.UTF8.GetBytes value do
            match valueByte with
            | 0x22uy -> literal.Append("\\\"") |> ignore
            | 0x5Cuy -> literal.Append("\\\\") |> ignore
            | 0x0Auy -> literal.Append("\\n") |> ignore
            | 0x0Duy -> literal.Append("\\r") |> ignore
            | 0x09uy -> literal.Append("\\t") |> ignore
            | item when item >= 0x20uy && item <= 0x7Euy -> literal.Append(char item) |> ignore
            | item ->
                // Close and reopen after each hexadecimal byte so a following
                // hexadecimal character cannot extend the C escape.
                literal.Append("\\x").Append(item.ToString("X2", CultureInfo.InvariantCulture)).Append("\"\"") |> ignore
        literal.Append('\"').ToString()

    let private generateModuleMetadataSource (fingerprint: string) (semantic: ModuleManifestSemantic) (emitted: EmittedModuleEntry array) (valueMetadata: NativeProgramMetadata) =
        let output = StringBuilder()
        let appendLine (value: string) = output.AppendLine(value) |> ignore
        let cUInt (value: uint32) = value.ToString(CultureInfo.InvariantCulture) + "u"
        let cInt (value: int) = value.ToString(CultureInfo.InvariantCulture)
        let cList values = values |> List.map cUInt |> String.concat ", "
        let fingerprintBytes = Convert.FromHexString fingerprint
        appendLine "#include <stddef.h>"
        appendLine "#include <stdint.h>"
        appendLine "#include \"module_abi.h\""
        appendLine ""

        for entry in emitted do
            let symbol = (moduleEntrySymbols entry.Id).Execute
            appendLine $"extern void {symbol}(al_runtime_context *ctx, int64_t *outputs, uint32_t capacity, int32_t *status);"
        appendLine ""

        for item in valueMetadata.Types do
            match item.Definition with
            | Some(NativeRecordMetadata(_, fields)) when not (List.isEmpty fields) ->
                let fieldIds = fields |> List.map snd |> cList
                appendLine $"static const uint32_t module_type_fields_{item.TypeId}[] = {{ {fieldIds} }};"
            | _ -> ()
        appendLine $"static const al_type_desc module_types[{valueMetadata.Types.Length}] = {{"
        for item in valueMetadata.Types do
            let fieldCount, fieldPointer =
                match item.Definition with
                | Some(NativeRecordMetadata(_, fields)) when not (List.isEmpty fields) -> fields.Length, $"module_type_fields_{item.TypeId}"
                | _ -> 0, "NULL"
            appendLine $"  {{ {cUInt item.Kind}, {cUInt(uint32 fieldCount)}, {fieldPointer} }},"
        appendLine "};"
        appendLine $"static const al_program_desc module_program = {{ module_types, {cUInt(uint32 valueMetadata.Types.Length)}, 0u }};"
        appendLine ""

        appendLine $"static const char *const module_type_names[{semantic.Types.Length}] = {{"
        for item in semantic.Types do appendLine $"  {cStringLiteral item.Name},"
        appendLine "};"
        appendLine ""

        for entry in emitted do
            let suffix = entry.Id.ToString("D3", CultureInfo.InvariantCulture)
            let inputTypeIds = entry.InputTypes |> List.map (fun ty -> valueMetadata.TypeIdsByIrType[ty])
            let outputTypeIds = entry.OutputTypes |> List.map (fun ty -> valueMetadata.TypeIdsByIrType[ty])
            if not (List.isEmpty inputTypeIds) then
                appendLine $"static const uint32_t module_entry_{suffix}_input_type_ids[{inputTypeIds.Length}] = {{ {cList inputTypeIds} }};"
            if not (List.isEmpty outputTypeIds) then
                appendLine $"static const uint32_t module_entry_{suffix}_output_type_ids[{outputTypeIds.Length}] = {{ {cList outputTypeIds} }};"
            if entry.Metadata.Length > 0 then
                appendLine $"static const al_module_diagnostic module_entry_{suffix}_diagnostics[{entry.Metadata.Length}] = {{"
                for id, error in Array.indexed entry.Metadata do
                    let diagnostic = error.Diagnostic
                    let word =
                        match error.WordSource with
                        | FixedWord value -> value
                        | EntryExecutionName -> entry.Name
                    let file, line, column, length =
                        match diagnostic.Span with
                        | Some source -> source.File, source.Line, source.Column, source.Length
                        | None -> "", 0, 0, 0
                    appendLine (
                        $"  {{ {cInt id}, {cUInt(uint32 line)}, {cUInt(uint32 column)}, {cUInt(uint32 length)}, " +
                        $"{cStringLiteral diagnostic.Code}, {cStringLiteral word}, {cStringLiteral file} }},")
                appendLine "};"
            appendLine ""

        appendLine $"static const al_module_entry module_entries[{emitted.Length}] = {{"
        for entry in emitted do
            let suffix = entry.Id.ToString("D3", CultureInfo.InvariantCulture)
            let inputIds = if List.isEmpty entry.InputTypes then "NULL" else $"module_entry_{suffix}_input_type_ids"
            let outputIds = if List.isEmpty entry.OutputTypes then "NULL" else $"module_entry_{suffix}_output_type_ids"
            let diagnostics = if entry.Metadata.Length = 0 then "NULL" else $"module_entry_{suffix}_diagnostics"
            appendLine (
                $"  {{ sizeof(al_module_entry), {cUInt(uint32 entry.Id)}, {cStringLiteral entry.Name}, " +
                $"{(moduleEntrySymbols entry.Id).Execute}, {cUInt(uint32 entry.InputTypes.Length)}, " +
                $"{cUInt(uint32 entry.OutputTypes.Length)}, {cUInt(uint32 entry.WorkspaceCapacity)}, " +
                $"{cUInt(uint32 entry.Metadata.Length)}, {inputIds}, {outputIds}, {diagnostics} }},")
        appendLine "};"
        let fingerprintInitializer = fingerprintBytes |> Array.map (fun value -> $"0x{value:X2}u") |> String.concat ", "
        appendLine ""
        appendLine "static const al_module_desc module_descriptor = {"
        appendLine "  AL_MODULE_ABI_VERSION,"
        appendLine "  sizeof(al_module_desc),"
        appendLine "  AL_RUNTIME_ABI_VERSION,"
        appendLine $"  {cUInt(uint32 emitted.Length)},"
        appendLine $"  {{ {fingerprintInitializer} }},"
        appendLine "  &module_program,"
        appendLine "  module_entries,"
        appendLine "  module_type_names"
        appendLine "};"
        appendLine ""
        appendLine "#pragma clang diagnostic push"
        appendLine "#pragma clang diagnostic ignored \"-Wdll-attribute-on-redeclaration\""
        appendLine "__declspec(dllexport) const al_module_desc *agentlang_module_descriptor(void) {"
        appendLine "  return &module_descriptor;"
        appendLine "}"
        appendLine "#pragma clang diagnostic pop"
        output.ToString()

    /// Compile related verified entries into one freestanding DLL with an
    /// immutable ABI-v1 descriptor. Validation and IR emission finish before
    /// invoking clang or lld; the result contains paths only.
    let compileModule
        (toolchain: LlvmToolchain)
        optimization
        outputDirectory
        (diagnosticSources: NativeDiagnosticSources)
        (moduleEntries: NativeModuleEntry list) =
        if String.IsNullOrWhiteSpace outputDirectory then invalidArg (nameof outputDirectory) "Output directory must be nonempty."
        if List.isEmpty moduleEntries then invalidArg (nameof moduleEntries) "A native module must contain at least one entry."

        for entry in moduleEntries do
            if Object.ReferenceEquals(box entry, null) then invalidArg "moduleEntries" "Native module entries cannot be null."
            if isNull entry.Name || entry.Name.Length = 0 then invalidArg "moduleEntries" "Native module entry names must be nonempty."
            if entry.Name.IndexOf('\000') >= 0 then invalidArg "moduleEntries" "Native module entry names cannot contain NUL."
            if Object.ReferenceEquals(entry.Body, null) then invalidArg "moduleEntries" "Native module entry bodies cannot be null."
        let orderedEntries =
            moduleEntries
            |> List.toArray
            |> Array.sortWith (fun left right -> StringComparer.Ordinal.Compare(left.Name, right.Name))
        for index in 1 .. orderedEntries.Length - 1 do
            if StringComparer.Ordinal.Equals(orderedEntries[index - 1].Name, orderedEntries[index].Name) then
                invalidArg "moduleEntries" $"Native module entry name '{orderedEntries[index].Name}' is duplicated."

        let verifiedProgram = VerifiedIrBody.program orderedEntries[0].Body
        for entry in orderedEntries do
            if not (Object.ReferenceEquals(verifiedProgram, VerifiedIrBody.program entry.Body)) then
                invalidArg "moduleEntries" "All native module entries must share the exact same VerifiedIrProgram instance."
        validateProgram verifiedProgram

        let emitted =
            orderedEntries
            |> Array.mapi (fun index entry ->
                let llvmIr, metadata, inputTypes, outputTypes, outputCapacity, workspaceCapacity, requiresRecordArenas, valueMetadata =
                    emitModule diagnosticSources (moduleEntrySymbols index) entry.Body
                { Id = index
                  Name = entry.Name
                  LlvmIr = llvmIr
                  Metadata = metadata
                  InputTypes = inputTypes
                  OutputTypes = outputTypes
                  OutputCapacity = outputCapacity
                  WorkspaceCapacity = workspaceCapacity
                  RequiresRecordArenas = requiresRecordArenas
                  ValueMetadata = valueMetadata })
        let valueMetadata = emitted[0].ValueMetadata
        for entry in emitted do
            if entry.ValueMetadata <> valueMetadata then
                invalidOp "Native module entries produced different verified-program type tables."

        let semantic = moduleSemanticManifest emitted valueMetadata
        let fingerprint = manifestFingerprint semantic
        let optimizationName =
            match optimization with
            | LlvmOptimization.O0 -> "O0"
            | LlvmOptimization.O2 -> "O2"
        let metadataSource = generateModuleMetadataSource fingerprint semantic emitted valueMetadata

        let fullDirectory = Path.GetFullPath outputDirectory
        Directory.CreateDirectory fullDirectory |> ignore
        let entryIrPaths =
            emitted
            |> Array.map (fun entry ->
                let path = Path.Combine(fullDirectory, $"entry-{entry.Id:D3}.ll")
                File.WriteAllText(path, entry.LlvmIr, UTF8Encoding(false))
                path)
            |> Array.toList
        let metadataSourcePath = Path.Combine(fullDirectory, "agentlang-module-metadata.c")
        File.WriteAllText(metadataSourcePath, metadataSource, UTF8Encoding(false))

        let runtimeDirectory = Path.Combine(fullDirectory, "native-runtime")
        Directory.CreateDirectory runtimeDirectory |> ignore
        let assembly = typeof<NativeCompiledProgram>.Assembly
        let runtimeHeaderPath = Path.Combine(runtimeDirectory, "arena_runtime.h")
        let runtimeSourcePath = Path.Combine(runtimeDirectory, "arena_runtime.c")
        let moduleHeaderPath = Path.Combine(runtimeDirectory, "module_abi.h")
        writeEmbeddedResource assembly "AgentLang.Llvm.native.arena_runtime.h" runtimeHeaderPath
        writeEmbeddedResource assembly "AgentLang.Llvm.native.arena_runtime.c" runtimeSourcePath
        writeEmbeddedResource assembly "AgentLang.Llvm.native.module_abi.h" moduleHeaderPath
        let libraryPath = Path.Combine(fullDirectory, "agentlang-module.dll")
        let compiledPath, objectPaths =
            LlvmToolchain.compileModuleLibrary toolchain optimization entryIrPaths metadataSourcePath runtimeSourcePath runtimeDirectory libraryPath
        let hashFile path =
            File.ReadAllBytes path
            |> SHA256.HashData
            |> Convert.ToHexString
            |> fun hex -> hex.ToLowerInvariant()
        let sourceHashes =
            [ "metadataC", hashFile metadataSourcePath
              "arenaRuntimeC", hashFile runtimeSourcePath
              "arenaRuntimeHeader", hashFile runtimeHeaderPath
              "moduleAbiHeader", hashFile moduleHeaderPath ]
        let serializedManifest = serializeManifest fingerprint optimizationName sourceHashes (hashFile compiledPath) semantic
        let manifestPath = Path.Combine(fullDirectory, "agentlang-module-manifest.json")
        File.WriteAllText(manifestPath, serializedManifest, UTF8Encoding(false))
        { LibraryPath = compiledPath
          ManifestPath = manifestPath
          Fingerprint = fingerprint
          EntryIrPaths = entryIrPaths
          MetadataSourcePath = metadataSourcePath
          ObjectPaths = objectPaths }
