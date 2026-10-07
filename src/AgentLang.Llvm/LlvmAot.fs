namespace AgentLang.Llvm

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open AgentLang

type NativeExecutionResult =
    { Values: Value list
      StepsConsumed: int }

/// Definition spans frozen from the same word snapshot used to create a
/// verified body. Primitive overflow diagnostics use PrimitiveDefinitionSpans;
/// call-depth diagnostics use WordDefinitionSpans.
type NativeDiagnosticSources =
    { WordDefinitionSpans: Map<string, SourceSpan>
      PrimitiveDefinitionSpans: Map<string, SourceSpan> }

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
      HasOverflowOperands: bool }

type private EmittedValue =
    { Type: IrType
      Operand: string }

type internal NativeScalarType =
    { TypeName: string
      BaseType: IrType }

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
    (libraryPath: string, outputTypes: IrType list, outputCapacity: int, scalarTypes: Map<ProgramTypeKey, NativeScalarType>, metadata: ErrorMetadata array) =
    let mutable libraryHandle = IntPtr.Zero
    let mutable executeDelegate: ExecuteDelegate option = None

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
    member _.OutputCount = outputTypes.Length
    /// Required i64 slots in the caller-owned scratch buffer shared by the
    /// entry body and its reachable user-word calls.
    member _.OutputCapacity = outputCapacity

    /// Separate Execute calls use separate buffers and can run concurrently.
    /// Dispose must not race an Execute call because it unloads the native DLL.
    member _.Execute(executionName: string) =
        if String.IsNullOrWhiteSpace executionName then invalidArg (nameof executionName) "Execution name must be nonempty."
        if libraryHandle = IntPtr.Zero then raise (ObjectDisposedException(nameof NativeCompiledProgram))
        let outputCount = outputTypes.Length
        let mutable contextPointer = IntPtr.Zero
        let mutable outputPointer = IntPtr.Zero
        let mutable statusPointer = IntPtr.Zero
        try
            contextPointer <- Marshal.AllocHGlobal NativeAbi.ContextSize
            outputPointer <-
                if outputCapacity = 0 then IntPtr.Zero
                else Marshal.AllocHGlobal(outputCapacity * NativeAbi.SlotSize)
            statusPointer <- Marshal.AllocHGlobal sizeof<int32>
            Marshal.WriteInt32(contextPointer, NativeAbi.ContextAbiVersionOffset, int NativeAbi.Version)
            Marshal.WriteInt32(contextPointer, NativeAbi.ContextStepsConsumedOffset, 0)
            Marshal.WriteInt32(contextPointer, NativeAbi.ContextErrorMetadataIdOffset, -1)
            Marshal.WriteInt32(contextPointer, NativeAbi.ContextReservedOffset, 0)
            Marshal.WriteInt64(contextPointer, NativeAbi.ContextErrorArgument0Offset, 0L)
            Marshal.WriteInt64(contextPointer, NativeAbi.ContextErrorArgument1Offset, 0L)
            Marshal.WriteInt32(statusPointer, NativeAbi.StatusInvalidRequest)
            match executeDelegate with
            | None -> raise (ObjectDisposedException(nameof NativeCompiledProgram))
            | Some native -> native.Invoke(nativeint contextPointer, nativeint outputPointer, outputCapacity, nativeint statusPointer)
            let status = Marshal.ReadInt32 statusPointer
            let steps = Marshal.ReadInt32(contextPointer, NativeAbi.ContextStepsConsumedOffset)
            match status with
            | NativeAbi.StatusSuccess ->
                let values =
                    outputTypes
                    |> List.mapi (fun index ty ->
                        let raw = Marshal.ReadInt64(outputPointer, index * NativeAbi.SlotSize)
                        let rec decode valueType value =
                            match valueType with
                            | IrInt -> IntValue value
                            | IrBool when value = 0L -> BoolValue false
                            | IrBool when value = 1L -> BoolValue true
                            | IrBool -> raise (InvalidDataException($"Native Bool output slot {index} was not encoded as 0 or 1: {value}."))
                            | IrUnit when value = 0L -> UnitValue
                            | IrUnit -> raise (InvalidDataException($"Native Unit output slot {index} was not encoded as 0: {value}."))
                            | IrNominal key ->
                                match scalarTypes.TryFind key with
                                | Some scalar -> NamedValue(scalar.TypeName, decode scalar.BaseType value)
                                | None -> raise (InvalidDataException($"Native artifact returned unsupported ABI output type {IrTypes.format valueType}."))
                            | unsupported ->
                                raise (InvalidDataException($"Native artifact returned unsupported ABI output type {IrTypes.format unsupported}."))
                        decode ty raw)
                { Values = values; StepsConsumed = steps }
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
                    if item.HasOverflowOperands then
                        [ Marshal.ReadInt64(contextPointer, NativeAbi.ContextErrorArgument0Offset).ToString(CultureInfo.InvariantCulture)
                          Marshal.ReadInt64(contextPointer, NativeAbi.ContextErrorArgument1Offset).ToString(CultureInfo.InvariantCulture) ]
                    else item.Diagnostic.Actual
                raise (LanguageException { item.Diagnostic with Word = word; Actual = actual })
            | NativeAbi.StatusInvalidRequest ->
                raise (InvalidOperationException("The native LLVM entry rejected the ABI version, output capacity, or output pointer."))
            | other -> raise (InvalidDataException($"Native LLVM entry returned unknown status {other}."))
        finally
            if statusPointer <> IntPtr.Zero then Marshal.FreeHGlobal statusPointer
            if outputPointer <> IntPtr.Zero then Marshal.FreeHGlobal outputPointer
            if contextPointer <> IntPtr.Zero then Marshal.FreeHGlobal contextPointer

    interface IDisposable with
        member _.Dispose() =
            if libraryHandle <> IntPtr.Zero then
                NativeLibrary.Free libraryHandle
                libraryHandle <- IntPtr.Zero
                executeDelegate <- None

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

    let private ensureScalarType (program: IrProgram) (owner: string) (span: SourceSpan option) (ty: IrType) =
        match ty with
        | IrInt | IrBool | IrUnit -> ()
        | IrNominal key ->
            match program.NominalTypesByKey.TryFind key with
            | Some(IrScalarDefinition scalar) ->
                match scalar.BaseType with
                | IrInt | IrBool -> ()
                | unsupported ->
                    callDiagnostic "IR_LLVM_UNSUPPORTED_TYPE" "The LLVM scalar backend supports nominal scalars only when their base is Int or Bool."
                        owner span [ "nominal scalar based on Int or Bool" ] [ $"{scalar.TypeName}: {IrTypes.format unsupported}" ]
            | Some(IrRecordDefinition record) ->
                callDiagnostic "IR_LLVM_UNSUPPORTED_TYPE" "The LLVM scalar backend does not support nominal records."
                    owner span [ "Int, Bool, Unit, or supported nominal scalar" ] [ record.TypeName ]
            | None ->
                callDiagnostic "IR_LLVM_UNSUPPORTED_TYPE" "The LLVM scalar backend cannot resolve an unknown nominal type."
                    owner span [ "known nominal scalar" ] [ IrTypes.format ty ]
        | unsupported ->
            callDiagnostic "IR_LLVM_UNSUPPORTED_TYPE" "The LLVM scalar backend supports only Int, Bool, Unit, and nominal Int/Bool scalar values."
                owner span [ "Int"; "Bool"; "Unit" ] [ IrTypes.format unsupported ]

    let private callsInBlock (program: IrProgram) (block: IrBlock) =
        let found = ResizeArray<IrResolvedCall * SourceSiteId>()
        let rec visitBlock (block: IrBlock) =
            for instruction in block.Code do
                let add call = found.Add(call, instruction.Site)
                let addConstructorValidator key =
                    match program.NominalTypesByKey.TryFind key with
                    | Some(IrScalarDefinition scalar) -> scalar.ValidatorCall |> Option.iter add
                    | _ -> ()
                match instruction.Operation with
                | IrOperation.Call call ->
                    add call
                    match call.ResolvedTarget with
                    | GeneratedWordTarget(id, _) ->
                        match program.GeneratedTargetsById.TryFind id with
                        | Some { Operation = WrapScalarOperation key } -> addConstructorValidator key
                        | _ -> ()
                    | _ -> ()
                | IrOperation.ListMap(call, _, _)
                | IrOperation.ListFilter(call, _)
                | IrOperation.ListEach(call, _)
                | IrOperation.ListFold(call, _, _)
                | IrOperation.MakeRecord(call, _)
                | IrOperation.GetRecordField(call, _, _)
                | IrOperation.UnwrapScalar(call, _) -> add call
                | IrOperation.WrapScalar(call, key, _) ->
                    add call
                    addConstructorValidator key
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
                    | UnwrapScalarOperation key -> ensureScalarType program owner span (IrNominal key)
                    | _ ->
                        callDiagnostic "IR_LLVM_UNSUPPORTED_TARGET" "Generated record calls are outside the LLVM scalar slice."
                            owner span [ "scalar constructor or accessor" ] [ call.ResolvedName ]

        for call, site in callsInBlock program body.BodyBlock do inspectCall body.BodyName site call
        while pending.Count > 0 do
            let functionValue = pending.Dequeue()
            rejectEffects functionValue.FunctionName None functionValue.FunctionDeclaredEffects
            rejectEffects functionValue.FunctionName None functionValue.FunctionInferredEffects
            for call, site in callsInBlock program functionValue.FunctionBody do
                inspectCall functionValue.FunctionName site call
        reachable

    let rec private validateCall (program: IrProgram) (owner: string) (span: SourceSpan option) (call: IrResolvedCall) =
        for ty in call.InputTypes @ call.OutputTypes do ensureScalarType program owner span ty
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
            | _ ->
                callDiagnostic "IR_LLVM_UNSUPPORTED_TARGET" "Generated record calls are outside the LLVM scalar slice."
                    owner span [ "scalar constructor or accessor" ] [ call.ResolvedName ]
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
            shape.StackTypes |> List.iter (ensureScalarType program owner None)
            shape.LocalTypes |> Map.iter (fun _ ty -> ensureScalarType program owner None ty)
        validateShape block.EntryShape
        validateShape block.ExitShape
        for instruction in block.Code do
            let span = sourceSpan sourceMap instruction.Site
            match instruction.Operation with
            | IrOperation.Constant(literal, ty) ->
                ensureScalarType program owner span ty
                match literal with
                | LInt _ when ty = IrInt -> ()
                | LBool _ when ty = IrBool -> ()
                | LUnit when ty = IrUnit -> ()
                | _ ->
                    callDiagnostic "IR_LLVM_UNSUPPORTED_CONSTANT"
                        "The LLVM scalar backend supports Int, Bool, and Unit constants only."
                        owner span [ "Int"; "Bool"; "Unit" ] [ sprintf "%A : %s" literal (IrTypes.format ty) ]
            | IrOperation.Call call -> validateCall program owner span call
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

    let private metadataFor (metadata: ResizeArray<ErrorMetadata>) (diagnostic: Diagnostic) (wordSource: ErrorWord) (hasOverflowOperands: bool) =
        let id = metadata.Count
        metadata.Add
            { Diagnostic = diagnostic
              WordSource = wordSource
              HasOverflowOperands = hasOverflowOperands }
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

    let private emitModule (diagnosticSources: NativeDiagnosticSources) (verifiedBody: VerifiedIrBody) =
        let verifiedProgram = VerifiedIrBody.program verifiedBody
        validateProgram verifiedProgram
        let program = VerifiedIrProgram.inspect verifiedProgram
        let body = VerifiedIrBody.inspect verifiedBody
        if not (List.isEmpty body.BodyInputTypes) then
            callDiagnostic "IR_LLVM_BODY_INPUT_UNSUPPORTED"
                "The LLVM ABI-v1 entry accepts only verified bodies with an empty initial stack."
                body.BodyName None [] (body.BodyInputTypes |> List.map IrTypes.format)
        let sourceMap = sourceMapFor verifiedBody
        let reachable = reachableFunctions verifiedBody sourceMap
        body.BodyInputTypes @ body.BodyOutputTypes |> List.iter (ensureScalarType program body.BodyName None)
        validateBlock program body.BodyName sourceMap body.BodyBlock
        let functions =
            program.FunctionsById
            |> Map.toList
            |> List.choose (fun (id, functionValue) -> if reachable.Contains id then Some(id, functionValue) else None)
        for _, functionValue in functions do
            functionValue.InputTypes @ functionValue.OutputTypes |> List.iter (ensureScalarType program functionValue.FunctionName None)
            validateBlock program functionValue.FunctionName program.SourceMap functionValue.FunctionBody

        let nativeScalarTypes =
            program.NominalTypesByKey
            |> Map.toList
            |> List.choose (fun (key, definition) ->
                match definition with
                | IrScalarDefinition scalar ->
                    Some(key, { TypeName = scalar.TypeName; BaseType = scalar.BaseType })
                | IrRecordDefinition _ -> None)
            |> Map.ofList

        let metadata = ResizeArray<ErrorMetadata>()
        let output = StringBuilder()
        output.AppendLine("target triple = " + "\"" + "x86_64-pc-windows-msvc" + "\"") |> ignore
        output.AppendLine("%NativeExecutionContext = type { i32, i32, i32, i32, i64, i64 }") |> ignore
        output.AppendLine("%NativeExecutionContextAlignmentProbe = type { i8, %NativeExecutionContext }") |> ignore
        output.AppendLine("declare { i64, i1 } @llvm.sadd.with.overflow.i64(i64, i64)") |> ignore
        output.AppendLine("declare { i64, i1 } @llvm.ssub.with.overflow.i64(i64, i64)") |> ignore
        output.AppendLine("declare { i64, i1 } @llvm.smul.with.overflow.i64(i64, i64)") |> ignore
        output.AppendLine() |> ignore

        let emitFieldPointer (builder: CodeBuilder) (field: int) =
            let pointer = builder.Fresh "ctx.field"
            builder.Emit($"{pointer} = getelementptr %%NativeExecutionContext, ptr %%ctx, i32 0, i32 {field}")
            pointer

        let emitFailure (builder: CodeBuilder) (diagnostic: Diagnostic) wordSource =
            let metadataId = metadataFor metadata diagnostic wordSource false
            let metadataPointer = emitFieldPointer builder 2
            builder.Emit($"store i32 {metadataId}, ptr {metadataPointer}, align 4")
            let statusPointer = builder.Fresh "status.diagnostic"
            builder.Emit($"{statusPointer} = getelementptr inbounds i32, ptr %%status, i64 0")
            builder.Emit($"store i32 {NativeAbi.StatusDiagnostic}, ptr {statusPointer}, align 4")
            builder.Emit("ret void")

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
            let metadataId = metadataFor metadata diagnostic (FixedWord operation) true
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
                    ensureScalarType program functionName None ty
                    { Type = ty; Operand = $"%%arg{index}" })
            let rec emitBlock (currentWord: string) (wordSource: ErrorWord) (block: IrBlock) (startStack: EmittedValue list) (startLocals: Map<LocalSlot, EmittedValue>) =
                let mutable stack = startStack
                let mutable localValues = startLocals
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
                let rec emitResolvedCall (call: IrResolvedCall) (arguments: EmittedValue list) (depthValue: string) =
                    emitDepthGuardAt builder depthValue call
                    match call.ResolvedTarget with
                    | PrimitiveTarget(PrimitiveId operation) -> emitPrimitive builder call operation arguments
                    | UserWordTarget(id, revision) -> emitUserCall call id revision arguments depthValue
                    | GeneratedWordTarget(id, revision) ->
                        match program.GeneratedTargetsById.TryFind id with
                        | Some target when target.TargetRevision = revision -> emitGeneratedScalarCall call target arguments depthValue
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
                and emitGeneratedScalarCall (call: IrResolvedCall) (target: IrGeneratedTarget) (arguments: EmittedValue list) (depthValue: string) =
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
                                match emitResolvedCall validator [ value ] validatorDepth with
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
                    | IrOperation.Call call
                    | IrOperation.WrapScalar(call, _, _)
                    | IrOperation.UnwrapScalar(call, _) ->
                        let arguments = popArguments call.InputTypes.Length
                        stack <- stack @ emitResolvedCall call arguments "%depth"
                    | _ -> invalidOp "LLVM validation missed an unsupported operation."
                stack, localValues
            let resultStack =
                let source = if isEntry then EntryExecutionName else FixedWord functionName
                emitBlock functionName source block entryValues Map.empty |> fst
            if resultStack.Length <> functionOutputs.Length then
                invalidOp "Verified LLVM body exit stack differed from its declared outputs."
            for (index, (value, expected)) in List.indexed (List.zip resultStack functionOutputs) do
                if value.Type <> expected then invalidOp "Verified LLVM output type differed from its declared signature."
                let pointer = builder.Fresh "output.ptr"
                builder.Emit($"{pointer} = getelementptr inbounds i64, ptr %%outputs, i64 {index}")
                builder.Emit($"store i64 {value.Operand}, ptr {pointer}, align 8")
            builder.Emit("ret void")
            output.Append(builder.Text) |> ignore
            output.AppendLine("}") |> ignore
            output.AppendLine() |> ignore

        emitFunction body.BodyName "@agentlang_body" [] body.BodyOutputTypes body.BodyLocalNames body.BodyBlock true
        for _, functionValue in functions do
            emitFunction functionValue.FunctionName (functionSymbol functionValue.FunctionId functionValue.FunctionRevision)
                functionValue.InputTypes functionValue.OutputTypes functionValue.LocalNames functionValue.FunctionBody false
        let outputCapacity =
            body.BodyOutputTypes.Length
            :: (functions |> List.map (fun (_, functionValue) -> functionValue.OutputTypes.Length))
            |> List.max

        output.AppendLine($"define dllexport i32 @agentlang_output_capacity() {{ ret i32 {outputCapacity} }}") |> ignore
        output.AppendLine() |> ignore
        output.AppendLine("define dllexport void @agentlang_abi_layout(ptr %output) {") |> ignore
        output.AppendLine("entry:") |> ignore
        output.AppendLine("  %size.ptr = getelementptr %NativeExecutionContext, ptr null, i32 1") |> ignore
        output.AppendLine("  %size = ptrtoint ptr %size.ptr to i64") |> ignore
        output.AppendLine("  %align.ptr = getelementptr %NativeExecutionContextAlignmentProbe, ptr null, i32 0, i32 1") |> ignore
        output.AppendLine("  %alignment = ptrtoint ptr %align.ptr to i64") |> ignore
        for index in 0 .. 5 do
            output.AppendLine($"  %%field{index}.ptr = getelementptr %%NativeExecutionContext, ptr null, i32 0, i32 {index}") |> ignore
            output.AppendLine($"  %%field{index} = ptrtoint ptr %%field{index}.ptr to i64") |> ignore
        output.AppendLine("  %items = alloca [8 x i64], align 8") |> ignore
        for index in 0 .. 7 do
            let valueName = if index = 0 then "%size" elif index = 1 then "%alignment" else $"%%field{index - 2}"
            output.AppendLine($"  %%out{index} = getelementptr inbounds [8 x i64], ptr %%items, i64 0, i64 {index}") |> ignore
            output.AppendLine($"  store i64 {valueName}, ptr %%out{index}, align 8") |> ignore
        for index in 0 .. 7 do
            output.AppendLine($"  %%item{index} = getelementptr inbounds [8 x i64], ptr %%items, i64 0, i64 {index}") |> ignore
            output.AppendLine($"  %%value{index} = load i64, ptr %%item{index}, align 8") |> ignore
            output.AppendLine($"  %%dest{index} = getelementptr inbounds i64, ptr %%output, i64 {index}") |> ignore
            output.AppendLine($"  store i64 %%value{index}, ptr %%dest{index}, align 8") |> ignore
        output.AppendLine("  ret void") |> ignore
        output.AppendLine("}") |> ignore
        output.AppendLine() |> ignore

        output.AppendLine("define dllexport void @agentlang_execute(ptr %ctx, ptr %outputs, i32 %capacity, ptr %status) {") |> ignore
        output.AppendLine("entry:") |> ignore
        output.AppendLine("  %version.ptr = getelementptr %NativeExecutionContext, ptr %ctx, i32 0, i32 0") |> ignore
        output.AppendLine("  %version = load i32, ptr %version.ptr, align 4") |> ignore
        output.AppendLine("  %version.ok = icmp eq i32 %version, 1") |> ignore
        output.AppendLine("  br i1 %version.ok, label %capacity.check, label %invalid.version") |> ignore
        output.AppendLine("invalid.version:") |> ignore
        output.AppendLine($"  store i32 {NativeAbi.StatusInvalidRequest}, ptr %%status, align 4") |> ignore
        output.AppendLine("  ret void") |> ignore
        output.AppendLine("capacity.check:") |> ignore
        output.AppendLine($"  %%capacity.ok = icmp sge i32 %%capacity, {outputCapacity}") |> ignore
        output.AppendLine("  %outputs.null = icmp eq ptr %outputs, null") |> ignore
        output.AppendLine("  %outputs.required = icmp ne i32 %capacity, 0") |> ignore
        output.AppendLine("  %null.invalid = and i1 %outputs.null, %outputs.required") |> ignore
        output.AppendLine("  %capacity.invalid = xor i1 %capacity.ok, true") |> ignore
        output.AppendLine("  %request.invalid = or i1 %capacity.invalid, %null.invalid") |> ignore
        output.AppendLine("  br i1 %request.invalid, label %invalid.request, label %request.valid") |> ignore
        output.AppendLine("invalid.request:") |> ignore
        output.AppendLine($"  store i32 {NativeAbi.StatusInvalidRequest}, ptr %%status, align 4") |> ignore
        output.AppendLine("  ret void") |> ignore
        output.AppendLine("request.valid:") |> ignore
        output.AppendLine("  store i32 0, ptr %status, align 4") |> ignore
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
        output.AppendLine("  call void @agentlang_body(ptr %ctx, ptr %outputs, ptr %status, i32 0)") |> ignore
        output.AppendLine("  ret void") |> ignore
        output.AppendLine("}") |> ignore

        output.ToString(), metadata.ToArray(), body.BodyOutputTypes, outputCapacity, nativeScalarTypes

    /// Emit deterministic LLVM IR from the exact compiler-verified body and its
    /// reachable verified user-word closure. No source is reparsed or relowered.
    let emit (verifiedBody: VerifiedIrBody) =
        emitModule NativeDiagnosticSources.empty verifiedBody |> fun (llvmIr, _, _, _, _) -> llvmIr

    /// Compile one effect-free scalar body to a dependency-free Windows x64
    /// DLL. The output directory retains the LLVM IR/object/DLL for inspection.
    let compile (toolchain: LlvmToolchain) optimization outputDirectory (diagnosticSources: NativeDiagnosticSources) (verifiedBody: VerifiedIrBody) =
        if String.IsNullOrWhiteSpace outputDirectory then invalidArg (nameof outputDirectory) "Output directory must be nonempty."
        let llvmIr, metadata, outputTypes, outputCapacity, scalarTypes = emitModule diagnosticSources verifiedBody
        let fullDirectory = Path.GetFullPath outputDirectory
        Directory.CreateDirectory fullDirectory |> ignore
        let llvmIrPath = Path.Combine(fullDirectory, "agentlang-native.ll")
        let libraryPath = Path.Combine(fullDirectory, "agentlang-native.dll")
        File.WriteAllText(llvmIrPath, llvmIr, UTF8Encoding(false))
        let compiledPath = LlvmToolchain.compileLibrary toolchain optimization llvmIrPath libraryPath
        new NativeCompiledProgram(compiledPath, outputTypes, outputCapacity, scalarTypes, metadata)
