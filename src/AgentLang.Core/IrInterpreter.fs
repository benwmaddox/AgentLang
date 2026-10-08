namespace AgentLang

open System
open System.Collections.Generic
open System.Globalization
open System.Runtime.CompilerServices
open System.Text

/// Operations that cross from the interpreter into Engine-owned virtual providers.
/// Payloads deliberately contain only primitive values; nominal values never cross
/// this boundary.
type IrEffectCommand =
    | ReadVirtualFile of operation: string * path: string
    | VirtualFileExists of operation: string * path: string
    | WriteVirtualFile of operation: string * path: string * contents: string
    | ReadFixedClock of operation: string
    | WriteVirtualConsole of operation: string * contents: string

type IrEffectResult =
    | EffectString of string
    | EffectBool of bool
    | EffectUnit

/// Engine hooks for policy, trace, and virtual effects. The interpreter owns the
/// executable semantics and calls these hooks only after verified-program checks.
type IrInterpreterHost =
    { PreflightEffects: Set<IrEffect> -> string option -> SourceSiteId option -> unit
      ChargeInstruction: string -> SourceSiteId -> unit
      RecordBranchOutcome: string -> SourceSiteId -> string -> unit
      RecordUse: string -> unit
      InvokeEffect: IrEffectCommand -> IrEffectResult
      /// Called at verified user-function entry with a deferred snapshot of the
      /// actual input values. The host may ignore the decoder. The returned scope
      /// exit action is always called from a finally block.
      EnterUserFunction: WordId -> int -> (unit -> Value list) -> (unit -> unit)
      /// Called only after a verified user function returns normally. Output
      /// decoding is deferred so hosts without observation do not allocate it.
      ReturnUserFunction: WordId -> int -> (unit -> Value list) -> unit
      WordDefinitionSpan: string -> SourceSpan option
      PrimitiveDefinitionSpan: string -> SourceSpan option }

[<RequireQualifiedAccess>]
type IrEntryArgument =
    /// Select an existing root from the one retained input owner.
    | RetainedRoot of int
    /// Supply a primitive message value. These cannot stand in for nominal types.
    | IntArgument of int64
    | BoolArgument of bool
    | UnitArgument

/// Internal semantic values shared by the interpreter and its opaque retained
/// result. They are never exposed through the public entry API.
type internal IrInterpreterRuntimeValue =
    | RuntimeInt of int64
    | RuntimeFloat of double
    | RuntimeBool of bool
    | RuntimeString of string
    | RuntimeUnit
    | RuntimeList of IrType * IrInterpreterRuntimeValue list
    | RuntimeOption of IrType * IrInterpreterRuntimeValue option
    | RuntimeResult of IrType * IrType * Result<IrInterpreterRuntimeValue, IrInterpreterRuntimeValue>
    | RuntimeRecord of ProgramTypeKey * IrInterpreterRuntimeValue list
    | RuntimeScalar of ProgramTypeKey * IrInterpreterRuntimeValue
    | RuntimeEnum of ProgramTypeKey * int

/// Opaque interpreter-owned state that can be passed to a later body compiled
/// against the same verified-program object. Runtime values remain private; the
/// public conversion is only for final observation.
[<Sealed>]
type IrInterpreterResult internal
    (programIdentity: VerifiedIrProgram, roots: IrInterpreterRuntimeValue array, decodeRoot: IrInterpreterRuntimeValue -> Value) =
    let gate = obj ()
    let mutable disposed = false
    let mutable currentRoots = Array.copy roots

    member _.Decode() =
        lock gate (fun () ->
            if disposed then raise (ObjectDisposedException("IrInterpreterResult"))
            currentRoots |> Array.map decodeRoot |> Array.toList)

    /// A shallow root snapshot is safe because RuntimeValue graphs are immutable.
    /// It keeps active execution valid if the input owner is disposed meanwhile.
    member internal _.AcquireRootsFor(expectedProgram: VerifiedIrProgram, executionName: string) =
        lock gate (fun () ->
            if disposed then
                Diagnostics.raiseError "IR_BACKEND_ENTRY_OWNER_DISPOSED" "The retained input owner has been disposed." (Some executionName) None
                    [ "live retained input owner" ] [ "disposed" ]
            if not (Object.ReferenceEquals(programIdentity, expectedProgram)) then
                Diagnostics.raiseError "IR_BACKEND_ENTRY_PROGRAM_MISMATCH" "The retained input belongs to a different verified-program instance." (Some executionName) None
                    [ "same VerifiedIrProgram instance" ] [ "different program instance" ]
            Array.copy currentRoots)

    member _.Dispose() =
        lock gate (fun () ->
            if not disposed then
                Array.Clear(currentRoots, 0, currentRoots.Length)
                currentRoots <- [||]
                disposed <- true)

    interface IDisposable with
        member this.Dispose() = this.Dispose()

module IrInterpreter =
    type private TypeFormatTask = FormatType of IrType | FormatText of string
    type private RuntimeValue = IrInterpreterRuntimeValue

    type private RuntimeValueMetrics =
        { ExpandedNodes: int64
          Depth: int
          EstimatedOutputBytes: int64 }

    let private maxSteps = 10000
    let private maxCallDepth = 64
    let private maxCollectionLength = 10000
    let private maxRuntimeValueDepth = 256
    let private maxRuntimeValueNodes = 100000L
    // Six bytes per UTF-16 code unit bounds JSON escaping (including surrogate
    // pairs). The node budget separately accounts for structural punctuation.
    let private maxRuntimeValueOutputBytes = 8000000L

    // This is intentionally a closed backend table. A compiler catalog entry by
    // itself cannot create an implementation or authorize dispatch to one.
    let private implementedPrimitiveOperations =
        set [
            "add"; "subtract"; "multiply"; "divide"
            "float.add"; "float.subtract"; "float.multiply"; "float.divide"
            "int.less-than"; "int.greater-than"; "int.less-or-equal"; "int.greater-or-equal"
            "float.less-than"; "float.greater-than"; "float.less-or-equal"; "float.greater-or-equal"
            "equals"; "bool.and"; "bool.or"; "bool.not"
            "string.guid-canonical?"; "string.guid-normalize"; "string.email-address-valid?"
            "int.add-checked"; "int.multiply-checked"; "int.scale-ratio-toward-zero"
            "instant.parse-utc"; "instant.is-canonical-utc?"; "instant.before?"; "instant.add-days"
            "string.concat"; "string.contains"; "string.starts-with"; "string.ends-with"
            "string.length"; "string.trim"; "string.to-lower"; "string.to-upper"
            "int.abs"; "int.min"; "int.max"; "int.to-float"; "float.to-int"; "float.round"
            "int.to-string"; "float.to-string"
            "list.count"; "list.tail"; "list.append"; "list.concat"; "list.get"; "list.is-empty?"
            "dup"; "drop"; "swap"
            "file.read"; "file.write"; "file.exists?"; "clock.now"; "console.write"
        ]

    let private implementedPrimitiveIds =
        implementedPrimitiveOperations |> Set.map PrimitiveId

    let private fail code message word span expected actual =
        Diagnostics.raiseError code message word span expected actual

    let private runtimeValueType = function
        | RuntimeInt _ -> IrInt
        | RuntimeFloat _ -> IrFloat
        | RuntimeBool _ -> IrBool
        | RuntimeString _ -> IrString
        | RuntimeUnit -> IrUnit
        | RuntimeList(itemType, _) -> IrList itemType
        | RuntimeOption(itemType, _) -> IrOption itemType
        | RuntimeResult(okType, errorType, _) -> IrResult(okType, errorType)
        | RuntimeRecord(key, _) | RuntimeScalar(key, _) | RuntimeEnum(key, _) -> IrNominal key

    let private typeName (program: IrProgram) key =
        match program.NominalTypesByKey.TryFind key with
        | Some(IrRecordDefinition record) -> record.TypeName
        | Some(IrScalarDefinition scalar) -> scalar.TypeName
        | Some(IrEnumDefinition enumDefinition) -> enumDefinition.TypeName
        | None -> fail "IR_BACKEND_NOMINAL_UNKNOWN" "Executable value refers to a nominal type absent from its verified program." None None [] [ sprintf "%A" key ]

    let private formatTypeOutputLimit = 8000000L

    let private formatType (program: IrProgram) rootType =
        let pending = Stack<TypeFormatTask>()
        let output = StringBuilder()
        let append (text: string) =
            if int64 output.Length + int64 text.Length > formatTypeOutputLimit then
                fail "RUNTIME_VALUE_LIMIT" "Type formatting exceeded the bounded output limit." None None
                    [ $"formatted type length <= {formatTypeOutputLimit}" ] [ string (int64 output.Length + int64 text.Length) ]
            output.Append(text) |> ignore
        pending.Push(FormatType rootType)
        while pending.Count > 0 do
            match pending.Pop() with
            | FormatText text -> append text
            | FormatType current ->
                match current with
                | IrInt -> append "Int"
                | IrFloat -> append "Float"
                | IrBool -> append "Bool"
                | IrString -> append "String"
                | IrUnit -> append "Unit"
                | IrNominal key -> append (typeName program key)
                | IrList item ->
                    pending.Push(FormatText ">")
                    pending.Push(FormatType item)
                    pending.Push(FormatText "List<")
                | IrOption item ->
                    pending.Push(FormatText ">")
                    pending.Push(FormatType item)
                    pending.Push(FormatText "Option<")
                | IrResult(okType, errorType) ->
                    pending.Push(FormatText ">")
                    pending.Push(FormatType errorType)
                    pending.Push(FormatText ", ")
                    pending.Push(FormatType okType)
                    pending.Push(FormatText "Result<")
        output.ToString()

    let private runtimeTypeNames (program: IrProgram) values =
        values |> List.map (runtimeValueType >> formatType program)

    let private decodeRuntimeValue (program: IrProgram) =
        let rec fromRuntimeValue = function
            | RuntimeInt value -> IntValue value
            | RuntimeFloat value -> FloatValue value
            | RuntimeBool value -> BoolValue value
            | RuntimeString value -> StringValue value
            | RuntimeUnit -> UnitValue
            | RuntimeList(itemType, values) -> ListValue(toLangType itemType, List.map fromRuntimeValue values)
            | RuntimeOption(itemType, value) -> OptionValue(toLangType itemType, Option.map fromRuntimeValue value)
            | RuntimeResult(okType, errorType, value) ->
                ResultValue(toLangType okType, toLangType errorType, Result.map fromRuntimeValue value |> Result.mapError fromRuntimeValue)
            | RuntimeRecord(key, values) ->
                match program.NominalTypesByKey.TryFind key with
                | Some(IrRecordDefinition definition) ->
                    let fields = definition.RecordFields |> List.sortBy (fun field -> field.FieldIndex)
                    if fields.Length <> values.Length then
                        fail "IR_BACKEND_RECORD_LAYOUT" "Runtime record field count differs from its verified nominal layout." (Some definition.TypeName) None [ string fields.Length ] [ string values.Length ]
                    RecordValue(definition.TypeName, List.zip fields values |> List.map (fun (field, value) -> field.FieldName, fromRuntimeValue value) |> Map.ofList)
                | _ -> fail "IR_BACKEND_RECORD_LAYOUT" "Record value refers to a non-record nominal type." None None [ "record" ] [ typeName program key ]
            | RuntimeScalar(key, value) ->
                match program.NominalTypesByKey.TryFind key with
                | Some(IrScalarDefinition definition) -> NamedValue(definition.TypeName, fromRuntimeValue value)
                | _ -> fail "IR_BACKEND_SCALAR_LAYOUT" "Scalar value refers to a non-scalar nominal type." None None [ "scalar" ] [ typeName program key ]
            | RuntimeEnum(key, caseIndex) ->
                match program.NominalTypesByKey.TryFind key with
                | Some(IrEnumDefinition definition) when caseIndex >= 0 && caseIndex < definition.Cases.Length -> EnumValue(definition.TypeName, definition.Cases[caseIndex])
                | _ -> fail "IR_BACKEND_ENUM_LAYOUT" "Enum value refers to a non-enum nominal type or case index." None None [ "valid enum type and case" ] [ typeName program key; string caseIndex ]
        and toLangType = function
            | IrInt -> TInt
            | IrFloat -> TFloat
            | IrBool -> TBool
            | IrString -> TString
            | IrUnit -> TUnit
            | IrList item -> TList(toLangType item)
            | IrOption item -> TOption(toLangType item)
            | IrResult(okType, errorType) -> TResult(toLangType okType, toLangType errorType)
            | IrNominal key -> TNamed(typeName program key)
        fromRuntimeValue

    let private requireBackendRegistry (verified: VerifiedIrProgram) =
        VerifiedIrProgram.requireBackendRegistry Compiler.primitiveIrCatalog verified
        let catalogIds = Compiler.primitiveIrCatalog |> Map.toSeq |> Seq.map fst |> Set.ofSeq
        if catalogIds <> implementedPrimitiveIds then
            fail "IR_BACKEND_IMPLEMENTATION_MISMATCH" "The trusted compiler primitive catalog and interpreter implementation table differ." None None
                (implementedPrimitiveIds |> Set.toList |> List.map (sprintf "%A"))
                (catalogIds |> Set.toList |> List.map (sprintf "%A"))

    /// Check a program handle against this backend's fixed implementation
    /// registry without executing a body or invoking any host hooks.
    let validateProgram (verified: VerifiedIrProgram) =
        requireBackendRegistry verified

    let private sourceAt (sourceMap: Map<SourceSiteId, IrSourceSite>) site =
        sourceMap.TryFind site |> Option.map (fun entry -> entry.SiteSpan)

    let private checkedIntegerOperation (operation: string) (span: SourceSpan option) (functionValue: int64 -> int64 -> int64) left right =
        try RuntimeInt(functionValue left right)
        with :? OverflowException ->
            fail "RUNTIME_OVERFLOW" $"'{operation}' overflowed its Int64 result." (Some operation) span [] [ string left; string right ]

    let executeBodyWithInputs
        (host: IrInterpreterHost)
        (executionName: string)
        (verifiedBody: VerifiedIrBody)
        (inputOwner: IrInterpreterResult option)
        (arguments: IrEntryArgument list) : IrInterpreterResult =
        let verifiedProgram = VerifiedIrBody.program verifiedBody
        requireBackendRegistry verifiedProgram
        let program = VerifiedIrProgram.inspect verifiedProgram
        let body = VerifiedIrBody.inspect verifiedBody

        // Check owner provenance and liveness before argument shape, matching the
        // native entry contract. Root values are immutable so this shallow copy
        // remains valid even if the caller disposes the input owner concurrently.
        let retainedRoots =
            match inputOwner with
            | Some owner -> owner.AcquireRootsFor(verifiedProgram, executionName)
            | None -> [||]
        if arguments.Length <> body.BodyInputTypes.Length then
            fail "IR_BACKEND_ENTRY_ARGUMENT_COUNT" "Entry argument count does not match the verified body input signature." (Some executionName) None
                [ string body.BodyInputTypes.Length ] [ string arguments.Length ]

        let initialStack =
            arguments
            |> List.map (function
                | IrEntryArgument.RetainedRoot index ->
                    if index < 0 || index >= retainedRoots.Length then
                        fail "IR_BACKEND_ENTRY_ROOT_INDEX" "Entry argument selects a root outside the retained input owner." (Some executionName) None
                            [ $"root index in [0, {retainedRoots.Length})" ] [ string index ]
                    retainedRoots[index]
                | IrEntryArgument.IntArgument value -> RuntimeInt value
                | IrEntryArgument.BoolArgument value -> RuntimeBool value
                | IrEntryArgument.UnitArgument -> RuntimeUnit)
        let actualInputTypes = initialStack |> List.map runtimeValueType
        if actualInputTypes <> body.BodyInputTypes then
            fail "IR_BACKEND_ENTRY_ARGUMENT_TYPE" "Entry argument types do not match the verified body input signature." (Some executionName) None
                (body.BodyInputTypes |> List.map (formatType program)) (runtimeTypeNames program initialStack)

        let sourceMap = Map.fold (fun found site source -> Map.add site source found) program.SourceMap body.BodySourceMap

        let sourceSpan site = sourceAt sourceMap site
        let failValueLimit currentWord site dimension expected actual =
            fail "RUNTIME_VALUE_LIMIT" $"Runtime value exceeds the {dimension} safety limit." (Some currentWord) (site |> Option.bind sourceSpan) [ expected ] [ actual ]

        let saturatingAdd limit left right =
            if left > limit || right > limit || left > limit - right then limit + 1L
            else left + right

        let checkValueMetrics currentWord site metrics =
            if metrics.Depth > maxRuntimeValueDepth then
                failValueLimit currentWord site "depth" $"depth <= {maxRuntimeValueDepth}" (string metrics.Depth)
            if metrics.ExpandedNodes > maxRuntimeValueNodes then
                failValueLimit currentWord site "expanded-node" $"expanded nodes <= {maxRuntimeValueNodes}" (string metrics.ExpandedNodes)
            if metrics.EstimatedOutputBytes > maxRuntimeValueOutputBytes then
                failValueLimit currentWord site "output-size" $"estimated UTF-8 output bytes <= {maxRuntimeValueOutputBytes}" (string metrics.EstimatedOutputBytes)

        let typeFootprint currentWord site rootType =
            let pending = Stack<IrType * int>()
            pending.Push((rootType, 1))
            let mutable nodes = 0L
            let mutable depth = 0
            let mutable outputBytes = 0L
            while pending.Count > 0 do
                let current, currentDepth = pending.Pop()
                nodes <- saturatingAdd maxRuntimeValueNodes nodes 1L
                if currentDepth > maxRuntimeValueDepth then
                    failValueLimit currentWord site "type-depth" $"type depth <= {maxRuntimeValueDepth}" (string currentDepth)
                if nodes > maxRuntimeValueNodes then
                    failValueLimit currentWord site "type-description" $"type nodes <= {maxRuntimeValueNodes}" (string nodes)
                depth <- max depth currentDepth
                outputBytes <- saturatingAdd maxRuntimeValueOutputBytes outputBytes 32L
                let push child = pending.Push((child, currentDepth + 1))
                match current with
                | IrList item | IrOption item -> push item
                | IrResult(okType, errorType) -> push errorType; push okType
                | IrNominal key ->
                    let name = typeName program key
                    outputBytes <- saturatingAdd maxRuntimeValueOutputBytes outputBytes (int64 name.Length * 6L)
                | IrInt | IrFloat | IrBool | IrString | IrUnit -> ()
                if outputBytes > maxRuntimeValueOutputBytes then
                    failValueLimit currentWord site "type-description output" $"estimated UTF-8 output bytes <= {maxRuntimeValueOutputBytes}" (string outputBytes)
            depth, outputBytes

        let valueMetricsCache = ConditionalWeakTable<RuntimeValue, RuntimeValueMetrics>()

        let tryCachedMetrics value =
            let mutable cached = Unchecked.defaultof<RuntimeValueMetrics>
            if valueMetricsCache.TryGetValue(value, &cached) then Some cached else None

        let valueChildren = function
            | RuntimeList(_, values) | RuntimeRecord(_, values) -> values
            | RuntimeOption(_, Some value) -> [ value ]
            | RuntimeResult(_, _, Ok value) | RuntimeResult(_, _, Error value) -> [ value ]
            | RuntimeScalar(_, value) -> [ value ]
            | RuntimeInt _ | RuntimeFloat _ | RuntimeBool _ | RuntimeString _ | RuntimeUnit
            | RuntimeOption(_, None) | RuntimeEnum _ -> []

        let runtimeValueOwnOutputBytes currentWord site value =
            let typeDepth, typeBytes = typeFootprint currentWord site (runtimeValueType value)
            let valueBytes =
                match value with
                | RuntimeString text -> 64L + int64 text.Length * 6L
                | RuntimeList(_, values) -> 64L + int64 values.Length * 2L
                | RuntimeRecord(key, _) ->
                    match program.NominalTypesByKey.TryFind key with
                    | Some(IrRecordDefinition record) ->
                        64L + (int64 record.TypeName.Length + (record.RecordFields |> List.sumBy (fun field -> int64 field.FieldName.Length))) * 6L
                    | _ -> fail "IR_BACKEND_RECORD_LAYOUT" "Record value refers to a non-record nominal type." None None [ "record" ] [ typeName program key ]
                | RuntimeScalar(key, _) ->
                    match program.NominalTypesByKey.TryFind key with
                    | Some(IrScalarDefinition scalar) -> 64L + int64 scalar.TypeName.Length * 6L
                    | _ -> fail "IR_BACKEND_SCALAR_LAYOUT" "Scalar value refers to a non-scalar nominal type." None None [ "scalar" ] [ typeName program key ]
                | RuntimeEnum(key, caseIndex) ->
                    match program.NominalTypesByKey.TryFind key with
                    | Some(IrEnumDefinition enumDefinition) when caseIndex >= 0 && caseIndex < enumDefinition.Cases.Length ->
                        64L + int64 (enumDefinition.TypeName.Length + enumDefinition.Cases[caseIndex].Length) * 6L
                    | _ -> fail "IR_BACKEND_ENUM_LAYOUT" "Enum value refers to a missing type or case in its frozen nominal table." None None [ "valid enum type and case" ] [ typeName program key; string caseIndex ]
                | RuntimeInt _ | RuntimeFloat _ | RuntimeBool _ | RuntimeUnit
                | RuntimeOption _ | RuntimeResult _ -> 64L
            max typeDepth 1, saturatingAdd maxRuntimeValueOutputBytes valueBytes typeBytes

        let runtimeMetrics currentWord site root =
            match tryCachedMetrics root with
            | Some cached -> cached
            | None ->
                let pending = Stack<RuntimeValue * bool>()
                pending.Push((root, false))
                while pending.Count > 0 do
                    let value, expanded = pending.Pop()
                    if tryCachedMetrics value |> Option.isNone then
                        let children = valueChildren value
                        let ownDepth, ownBytes = runtimeValueOwnOutputBytes currentWord site value
                        if expanded then
                            let mutable nodes = 1L
                            let mutable depth = ownDepth
                            let mutable outputBytes = ownBytes
                            for child in children do
                                let childMetrics = tryCachedMetrics child |> Option.defaultWith (fun () -> fail "RUNTIME_VALUE_ACCOUNTING" "Value graph metrics are unavailable for a child node." (Some currentWord) (site |> Option.bind sourceSpan) [] [])
                                nodes <- saturatingAdd maxRuntimeValueNodes nodes childMetrics.ExpandedNodes
                                depth <- max depth (childMetrics.Depth + 1)
                                outputBytes <- saturatingAdd maxRuntimeValueOutputBytes outputBytes childMetrics.EstimatedOutputBytes
                            let metrics =
                                { ExpandedNodes = nodes
                                  Depth = depth
                                  EstimatedOutputBytes = outputBytes }
                            checkValueMetrics currentWord site metrics
                            valueMetricsCache.Add(value, metrics)
                        elif List.isEmpty children then
                            let metrics =
                                { ExpandedNodes = 1L
                                  Depth = ownDepth
                                  EstimatedOutputBytes = ownBytes }
                            checkValueMetrics currentWord site metrics
                            valueMetricsCache.Add(value, metrics)
                        else
                            pending.Push((value, true))
                            for child in children do
                                if tryCachedMetrics child |> Option.isNone then pending.Push((child, false))
                tryCachedMetrics root |> Option.defaultWith (fun () -> fail "RUNTIME_VALUE_ACCOUNTING" "Value graph metrics were not produced for a root node." (Some currentWord) (site |> Option.bind sourceSpan) [] [])

        let aggregateRuntimeValues currentWord site (values: seq<RuntimeValue>) =
            let mutable nodes = 0L
            let mutable depth = 0
            let mutable outputBytes = 0L
            for value in values do
                let metrics = runtimeMetrics currentWord site value
                nodes <- saturatingAdd maxRuntimeValueNodes nodes metrics.ExpandedNodes
                depth <- max depth metrics.Depth
                outputBytes <- saturatingAdd maxRuntimeValueOutputBytes outputBytes metrics.EstimatedOutputBytes
                checkValueMetrics currentWord site
                    { ExpandedNodes = nodes
                      Depth = depth
                      EstimatedOutputBytes = outputBytes }
            { ExpandedNodes = nodes
              Depth = depth
              EstimatedOutputBytes = outputBytes }

        let checkRuntimeValueRoots currentWord site values =
            aggregateRuntimeValues currentWord site values |> ignore

        let mutable chargedSteps = 0
        let chargeInstruction currentWord site =
            chargedSteps <- chargedSteps + 1
            host.ChargeInstruction currentWord site
            if chargedSteps > maxSteps then
                fail "RUNTIME_STEP_LIMIT" "Execution exceeded the 10,000 instruction limit." (Some currentWord) (sourceSpan site) [] []

        let decodeValue = decodeRuntimeValue program

        let rec invokeResolved (depth: int) (call: IrResolvedCall) (arguments: RuntimeValue list) (site: SourceSiteId option) =
            if depth > maxCallDepth then
                fail "RUNTIME_CALL_DEPTH" "Execution exceeded the 64 word call-depth limit." (Some call.ResolvedName) (host.WordDefinitionSpan call.ResolvedName) [] []
            host.PreflightEffects call.ResolvedEffects (Some call.ResolvedName) site
            match call.ResolvedTarget with
            | UserWordTarget(wordId, revision) ->
                match program.FunctionsById.TryFind wordId with
                | None -> fail "IR_BACKEND_TARGET_MISSING" "Verified call refers to a user word absent from its bound program." (Some call.ResolvedName) (site |> Option.bind sourceSpan) [] [ sprintf "%A" wordId ]
                | Some functionValue when functionValue.FunctionRevision <> revision ->
                    fail "IR_BACKEND_TARGET_REVISION" "Verified call revision differs from the function revision in its bound program." (Some call.ResolvedName) (site |> Option.bind sourceSpan) [ string revision ] [ string functionValue.FunctionRevision ]
                | Some functionValue ->
                    host.RecordUse functionValue.FunctionName
                    executeFunction depth functionValue arguments
            | GeneratedWordTarget(wordId, revision) ->
                match program.GeneratedTargetsById.TryFind wordId with
                | None -> fail "IR_BACKEND_TARGET_MISSING" "Verified call refers to a generated word absent from its bound program." (Some call.ResolvedName) (site |> Option.bind sourceSpan) [] [ sprintf "%A" wordId ]
                | Some target when target.TargetRevision <> revision ->
                    fail "IR_BACKEND_TARGET_REVISION" "Verified call revision differs from its generated target revision." (Some call.ResolvedName) (site |> Option.bind sourceSpan) [ string revision ] [ string target.TargetRevision ]
                | Some target ->
                    host.RecordUse target.TargetName
                    executeGenerated depth target call.ResolvedName arguments site
            | PrimitiveTarget(PrimitiveId operation) ->
                if not (implementedPrimitiveOperations.Contains operation) then
                    fail "IR_BACKEND_PRIMITIVE_UNIMPLEMENTED" "Verified program refers to a primitive with no fixed interpreter implementation." (Some call.ResolvedName) (site |> Option.bind sourceSpan) [] [ operation ]
                host.RecordUse call.ResolvedName
                executePrimitive operation call arguments site

        and executeFunction (callerDepth: int) (functionValue: IrFunction) (arguments: RuntimeValue list) =
            let entryDepth = callerDepth + 1
            if arguments.Length <> functionValue.InputTypes.Length then
                fail "RUNTIME_INTERNAL_TYPE" $"'{functionValue.FunctionName}' received values outside its verified signature." (Some functionValue.FunctionName) None
                    (functionValue.InputTypes |> List.map (formatType program)) (runtimeTypeNames program arguments)
            let exitUserFunction =
                host.EnterUserFunction
                    functionValue.FunctionId
                    functionValue.FunctionRevision
                    (fun () -> arguments |> List.map decodeValue)
            try
                let locals = Map.empty
                let stack, _ = executeBlock entryDepth functionValue.FunctionName functionValue.LocalNames functionValue.FunctionBody arguments locals
                host.ReturnUserFunction functionValue.FunctionId functionValue.FunctionRevision (fun () -> stack |> List.map decodeValue)
                stack
            finally
                exitUserFunction ()

        and executeGenerated (depth: int) (target: IrGeneratedTarget) (resolvedName: string) (arguments: RuntimeValue list) (site: SourceSiteId option) =
            if depth > maxCallDepth then
                fail "RUNTIME_CALL_DEPTH" "Execution exceeded the 64 word call-depth limit." (Some resolvedName) (host.WordDefinitionSpan resolvedName) [] []
            match target.Operation, arguments with
            | MakeRecordOperation key, values ->
                let expectedFields =
                    match program.NominalTypesByKey.TryFind key with
                    | Some(IrRecordDefinition definition) -> definition.RecordFields |> List.sortBy (fun field -> field.FieldIndex)
                    | _ -> fail "IR_BACKEND_RECORD_LAYOUT" "Generated record constructor refers to a non-record type." (Some resolvedName) (host.WordDefinitionSpan resolvedName) [ "record" ] []
                if values.Length <> expectedFields.Length then
                    fail "RUNTIME_INTERNAL_TYPE" "Record constructor received an invalid field count." (Some resolvedName) (host.WordDefinitionSpan resolvedName)
                        [ string expectedFields.Length ] [ string values.Length ]
                let recordValue = RuntimeRecord(key, values)
                let validator =
                    match program.NominalTypesByKey.TryFind key with
                    | Some(IrRecordDefinition definition) -> definition.ValidatorCall
                    | _ -> None
                match validator with
                | None -> [ recordValue ]
                | Some checkedCall ->
                    match invokeResolved (depth + 1) checkedCall [ recordValue ] site with
                    | [ RuntimeBool true ] -> [ recordValue ]
                    | [ RuntimeBool false ] ->
                        let typeName = typeName program key
                        fail "RECORD_VALIDATION_FAILED" $"Value does not satisfy {typeName}'s validation predicate." (Some resolvedName) (site |> Option.bind sourceSpan) [ "validator returns true" ] [ "false" ]
                    | checkedResult ->
                        fail "RUNTIME_VALIDATOR_RESULT" "Record validator did not return one Bool." (Some checkedCall.ResolvedName) (site |> Option.bind sourceSpan) [ "Bool" ] (runtimeTypeNames program checkedResult)
            | GetRecordFieldOperation(key, fieldIndex), [ RuntimeRecord(actualKey, values) ] when actualKey = key ->
                match program.NominalTypesByKey.TryFind key with
                | Some(IrRecordDefinition definition) ->
                    match definition.RecordFields |> List.tryFind (fun field -> field.FieldIndex = fieldIndex) with
                    | Some _ when fieldIndex >= 0 && fieldIndex < values.Length -> [ values[fieldIndex] ]
                    | _ -> fail "IR_BACKEND_RECORD_LAYOUT" "Generated record accessor refers to an absent verified field." (Some resolvedName) (host.WordDefinitionSpan resolvedName) [] [ string fieldIndex ]
                | _ -> fail "IR_BACKEND_RECORD_LAYOUT" "Generated record accessor refers to a non-record type." (Some resolvedName) (host.WordDefinitionSpan resolvedName) [ "record" ] []
            | GetRecordFieldOperation(key, _), _ ->
                fail "RUNTIME_INTERNAL_TYPE" "Record accessor received an invalid record value." (Some resolvedName) None [ typeName program key ] (runtimeTypeNames program arguments)
            | WrapScalarOperation key, [ value ] ->
                let scalar =
                    match program.NominalTypesByKey.TryFind key with
                    | Some(IrScalarDefinition definition) -> definition
                    | _ -> fail "IR_BACKEND_SCALAR_LAYOUT" "Generated scalar constructor refers to a non-scalar type." (Some resolvedName) (host.WordDefinitionSpan resolvedName) [ "scalar" ] []
                match scalar.ValidatorCall with
                | None -> [ RuntimeScalar(key, value) ]
                | Some validator ->
                    match invokeResolved (depth + 1) validator [ value ] site with
                    | [ RuntimeBool true ] -> [ RuntimeScalar(key, value) ]
                    | [ RuntimeBool false ] ->
                        fail "REFINEMENT_FAILED" $"Value does not satisfy {scalar.TypeName}'s refinement validator." (Some resolvedName) (host.WordDefinitionSpan resolvedName) [ "validator returns true" ] [ "false" ]
                    | checkedResult ->
                        fail "RUNTIME_VALIDATOR_RESULT" "Scalar validator did not return one Bool." (Some validator.ResolvedName) None [ "Bool" ] (runtimeTypeNames program checkedResult)
            | WrapScalarOperation key, _ ->
                match program.NominalTypesByKey.TryFind key with
                | Some(IrScalarDefinition scalar) ->
                    fail "RUNTIME_INTERNAL_TYPE" "Scalar constructor received an invalid value." (Some resolvedName) (host.WordDefinitionSpan resolvedName)
                        [ formatType program scalar.BaseType ] (runtimeTypeNames program arguments)
                | _ -> fail "IR_BACKEND_SCALAR_LAYOUT" "Generated scalar constructor refers to a non-scalar type." (Some resolvedName) (host.WordDefinitionSpan resolvedName) [ "scalar" ] []
            | UnwrapScalarOperation key, [ RuntimeScalar(actualKey, value) ] when actualKey = key -> [ value ]
            | UnwrapScalarOperation key, _ ->
                fail "RUNTIME_INTERNAL_TYPE" "Scalar unwrapping received an invalid nominal value." (Some resolvedName) None [ typeName program key ] (runtimeTypeNames program arguments)
            | MakeEnumCaseOperation(key, caseIndex), [] ->
                match program.NominalTypesByKey.TryFind key with
                | Some(IrEnumDefinition enumDefinition) when caseIndex >= 0 && caseIndex < enumDefinition.Cases.Length -> [ RuntimeEnum(key, caseIndex) ]
                | _ -> fail "IR_BACKEND_ENUM_LAYOUT" "Generated enum constructor refers to a missing type or case in its frozen nominal table." (Some resolvedName) (host.WordDefinitionSpan resolvedName) [ "valid enum type and case" ] [ typeName program key; string caseIndex ]
            | MakeEnumCaseOperation(key, _), _ ->
                fail "RUNTIME_INTERNAL_TYPE" "Enum case constructor received an invalid argument count." (Some resolvedName) (host.WordDefinitionSpan resolvedName) [ "0" ] [ string arguments.Length ]

        and executePrimitive (operation: string) (call: IrResolvedCall) (arguments: RuntimeValue list) (site: SourceSiteId option) =
            let currentWord = call.ResolvedName
            let primitiveDefinitionSpan = host.PrimitiveDefinitionSpan call.ResolvedName
            let typedError () =
                fail "RUNTIME_INTERNAL_TYPE" $"Builtin '{operation}' received a value outside its checked signature." (Some operation) None [] (runtimeTypeNames program arguments)
            let finiteFloat value =
                if not (Double.IsFinite value) then
                    fail "RUNTIME_NONFINITE_FLOAT" "Float operation produced a nonfinite value." (Some operation) None [ "finite Float" ] [ string value ]
                RuntimeFloat value
            match operation, arguments with
            | "dup", [ value ] -> [ value; value ]
            | "drop", [ _ ] -> []
            | "swap", [ first; second ] -> [ second; first ]
            | "add", [ RuntimeInt left; RuntimeInt right ] -> [ checkedIntegerOperation operation primitiveDefinitionSpan Checked.(+) left right ]
            | "subtract", [ RuntimeInt left; RuntimeInt right ] -> [ checkedIntegerOperation operation primitiveDefinitionSpan Checked.(-) left right ]
            | "multiply", [ RuntimeInt left; RuntimeInt right ] -> [ checkedIntegerOperation operation primitiveDefinitionSpan Checked.(*) left right ]
            | "divide", [ RuntimeInt _; RuntimeInt 0L ] -> fail "RUNTIME_DIVIDE_BY_ZERO" "Integer division by zero." (Some operation) None [] []
            | "divide", [ RuntimeInt left; RuntimeInt right ] when left = Int64.MinValue && right = -1L -> fail "RUNTIME_OVERFLOW" "Integer division overflow." (Some operation) None [] []
            | "divide", [ RuntimeInt left; RuntimeInt right ] -> [ RuntimeInt(left / right) ]
            | "int.add-checked", [ RuntimeInt left; RuntimeInt right ] ->
                [ RuntimeResult(IrInt, IrString, TrustedValues.addChecked left right |> Result.map RuntimeInt |> Result.mapError RuntimeString) ]
            | "int.multiply-checked", [ RuntimeInt left; RuntimeInt right ] ->
                [ RuntimeResult(IrInt, IrString, TrustedValues.multiplyChecked left right |> Result.map RuntimeInt |> Result.mapError RuntimeString) ]
            | "int.scale-ratio-toward-zero", [ RuntimeInt value; RuntimeInt numerator; RuntimeInt denominator ] ->
                [ RuntimeResult(IrInt, IrString, TrustedValues.scaleRatioTowardZero value numerator denominator |> Result.map RuntimeInt |> Result.mapError RuntimeString) ]
            | "float.add", [ RuntimeFloat left; RuntimeFloat right ] -> [ finiteFloat (left + right) ]
            | "float.subtract", [ RuntimeFloat left; RuntimeFloat right ] -> [ finiteFloat (left - right) ]
            | "float.multiply", [ RuntimeFloat left; RuntimeFloat right ] -> [ finiteFloat (left * right) ]
            | "float.divide", [ RuntimeFloat _; RuntimeFloat right ] when right = 0.0 -> fail "RUNTIME_DIVIDE_BY_ZERO" "Float division by zero." (Some operation) None [] []
            | "float.divide", [ RuntimeFloat left; RuntimeFloat right ] -> [ finiteFloat (left / right) ]
            | "int.less-than", [ RuntimeInt left; RuntimeInt right ] -> [ RuntimeBool(left < right) ]
            | "int.greater-than", [ RuntimeInt left; RuntimeInt right ] -> [ RuntimeBool(left > right) ]
            | "int.less-or-equal", [ RuntimeInt left; RuntimeInt right ] -> [ RuntimeBool(left <= right) ]
            | "int.greater-or-equal", [ RuntimeInt left; RuntimeInt right ] -> [ RuntimeBool(left >= right) ]
            | "float.less-than", [ RuntimeFloat left; RuntimeFloat right ] -> [ RuntimeBool(left < right) ]
            | "float.greater-than", [ RuntimeFloat left; RuntimeFloat right ] -> [ RuntimeBool(left > right) ]
            | "float.less-or-equal", [ RuntimeFloat left; RuntimeFloat right ] -> [ RuntimeBool(left <= right) ]
            | "float.greater-or-equal", [ RuntimeFloat left; RuntimeFloat right ] -> [ RuntimeBool(left >= right) ]
            | "equals", [ left; right ] -> [ RuntimeBool(left = right) ]
            | "bool.and", [ RuntimeBool left; RuntimeBool right ] -> [ RuntimeBool(left && right) ]
            | "bool.or", [ RuntimeBool left; RuntimeBool right ] -> [ RuntimeBool(left || right) ]
            | "bool.not", [ RuntimeBool value ] -> [ RuntimeBool(not value) ]
            | "string.guid-canonical?", [ RuntimeString value ] -> [ RuntimeBool(TrustedValues.guidCanonical value) ]
            | "string.guid-normalize", [ RuntimeString value ] ->
                [ RuntimeResult(IrString, IrString, TrustedValues.guidNormalize value |> Result.map RuntimeString |> Result.mapError RuntimeString) ]
            | "string.email-address-valid?", [ RuntimeString value ] -> [ RuntimeBool(TrustedValues.emailAddressValid value) ]
            | "instant.parse-utc", [ RuntimeString value ] ->
                [ RuntimeResult(IrString, IrString, TrustedValues.parseUtc value |> Result.map RuntimeString |> Result.mapError RuntimeString) ]
            | "instant.is-canonical-utc?", [ RuntimeString value ] -> [ RuntimeBool(TrustedValues.instantIsCanonicalUtc value) ]
            | "instant.before?", [ RuntimeString left; RuntimeString right ] ->
                match TrustedValues.instantBefore left right with
                | Ok before -> [ RuntimeBool before ]
                | Error _ ->
                    fail "RUNTIME_INVALID_INSTANT" "instant.before? requires two canonical UTC O-format instants." (Some currentWord) (site |> Option.bind sourceSpan)
                        [ "canonical UTC instant"; "canonical UTC instant" ] [ left; right ]
            | "instant.add-days", [ RuntimeString value; RuntimeInt days ] ->
                [ RuntimeResult(IrString, IrString, TrustedValues.instantAddDays value days |> Result.map RuntimeString |> Result.mapError RuntimeString) ]
            | "string.concat", [ RuntimeString left; RuntimeString right ] ->
                let typeDepth, typeBytes = typeFootprint currentWord site IrString
                let estimatedBytes = saturatingAdd maxRuntimeValueOutputBytes (64L + typeBytes) (int64 left.Length * 6L + int64 right.Length * 6L)
                checkValueMetrics currentWord site
                    { ExpandedNodes = 1L
                      Depth = max 1 typeDepth
                      EstimatedOutputBytes = estimatedBytes }
                [ RuntimeString(left + right) ]
            | "string.contains", [ RuntimeString value; RuntimeString sub ] -> [ RuntimeBool(value.Contains(sub, StringComparison.Ordinal)) ]
            | "string.starts-with", [ RuntimeString value; RuntimeString sub ] -> [ RuntimeBool(value.StartsWith(sub, StringComparison.Ordinal)) ]
            | "string.ends-with", [ RuntimeString value; RuntimeString sub ] -> [ RuntimeBool(value.EndsWith(sub, StringComparison.Ordinal)) ]
            | "string.length", [ RuntimeString value ] -> [ RuntimeInt(int64 value.Length) ]
            | "string.trim", [ RuntimeString value ] -> [ RuntimeString(value.Trim()) ]
            | "string.to-lower", [ RuntimeString value ] -> [ RuntimeString(value.ToLowerInvariant()) ]
            | "string.to-upper", [ RuntimeString value ] -> [ RuntimeString(value.ToUpperInvariant()) ]
            | "int.abs", [ RuntimeInt Int64.MinValue ] -> fail "RUNTIME_OVERFLOW" "Absolute value of Int64.MinValue overflows." (Some operation) None [] []
            | "int.abs", [ RuntimeInt value ] -> [ RuntimeInt(abs value) ]
            | "int.min", [ RuntimeInt left; RuntimeInt right ] -> [ RuntimeInt(min left right) ]
            | "int.max", [ RuntimeInt left; RuntimeInt right ] -> [ RuntimeInt(max left right) ]
            | "int.to-float", [ RuntimeInt value ] -> [ RuntimeFloat(float value) ]
            | "float.to-int", [ RuntimeFloat value ] when not (Double.IsFinite value) || value >= 9223372036854775808.0 || value < -9223372036854775808.0 ->
                fail "RUNTIME_RANGE" "Float value is outside the Int64 range." (Some operation) None [ "finite Int64 range" ] [ string value ]
            | "float.to-int", [ RuntimeFloat value ] -> [ RuntimeInt(int64 value) ]
            | "float.round", [ RuntimeFloat value ] when not (Double.IsFinite value) -> fail "RUNTIME_RANGE" "Float value is outside the Int64 range." (Some operation) None [ "finite Int64 range" ] [ string value ]
            | "float.round", [ RuntimeFloat value ] ->
                let rounded = Math.Round(value, MidpointRounding.AwayFromZero)
                if rounded >= 9223372036854775808.0 || rounded < -9223372036854775808.0 then
                    fail "RUNTIME_RANGE" "Rounded Float value is outside the Int64 range." (Some operation) None [ "finite Int64 range" ] [ string value ]
                [ RuntimeInt(int64 rounded) ]
            | "int.to-string", [ RuntimeInt value ] -> [ RuntimeString(string value) ]
            | "float.to-string", [ RuntimeFloat value ] -> [ RuntimeString(value.ToString("G", CultureInfo.InvariantCulture)) ]
            | "list.count", [ RuntimeList(_, values) ] -> [ RuntimeInt(int64 values.Length) ]
            | "list.tail", [ RuntimeList(itemType, []) ] -> [ RuntimeList(itemType, []) ]
            | "list.tail", [ RuntimeList(itemType, _ :: values) ] -> [ RuntimeList(itemType, values) ]
            | "list.append", [ RuntimeList(_, values); _ ] when values.Length >= maxCollectionLength ->
                fail "RUNTIME_VALUE_LIMIT" $"Lists cannot contain more than {maxCollectionLength} values." (Some operation) None [ string maxCollectionLength ] [ string values.Length ]
            | "list.append", [ RuntimeList(itemType, values); value ] when runtimeValueType value = itemType ->
                [ RuntimeList(itemType, values @ [ value ]) ]
            | "list.concat", [ RuntimeList(_, left); RuntimeList(_, right) ] when int64 left.Length + int64 right.Length > int64 maxCollectionLength ->
                fail "RUNTIME_VALUE_LIMIT" $"Lists cannot contain more than {maxCollectionLength} values." (Some operation) None [ string maxCollectionLength ] [ string (left.Length + right.Length) ]
            | "list.concat", [ RuntimeList(itemType, left); RuntimeList(otherType, right) ] when itemType = otherType -> [ RuntimeList(itemType, left @ right) ]
            | "list.get", [ RuntimeList(itemType, values); RuntimeInt index ] when index >= 0L && index < int64 values.Length -> [ RuntimeOption(itemType, Some values[int index]) ]
            | "list.get", [ RuntimeList(itemType, _); RuntimeInt _ ] -> [ RuntimeOption(itemType, None) ]
            | "list.is-empty?", [ RuntimeList(_, values) ] -> [ RuntimeBool(List.isEmpty values) ]
            | "file.read", [ RuntimeString path ] ->
                match host.InvokeEffect(ReadVirtualFile(operation, path)) with
                | EffectString contents -> [ RuntimeString contents ]
                | _ -> fail "IR_BACKEND_EFFECT_RESULT" "Virtual file read returned an invalid primitive result." (Some operation) None [ "String" ] []
            | "file.exists?", [ RuntimeString path ] ->
                match host.InvokeEffect(VirtualFileExists(operation, path)) with
                | EffectBool exists -> [ RuntimeBool exists ]
                | _ -> fail "IR_BACKEND_EFFECT_RESULT" "Virtual file existence check returned an invalid primitive result." (Some operation) None [ "Bool" ] []
            | "file.write", [ RuntimeString path; RuntimeString contents ] ->
                match host.InvokeEffect(WriteVirtualFile(operation, path, contents)) with
                | EffectUnit -> [ RuntimeUnit ]
                | _ -> fail "IR_BACKEND_EFFECT_RESULT" "Virtual file write returned an invalid primitive result." (Some operation) None [ "Unit" ] []
            | "clock.now", [] ->
                match host.InvokeEffect(ReadFixedClock operation) with
                | EffectString value -> [ RuntimeString value ]
                | _ -> fail "IR_BACKEND_EFFECT_RESULT" "Clock read returned an invalid primitive result." (Some operation) None [ "String" ] []
            | "console.write", [ RuntimeString contents ] ->
                match host.InvokeEffect(WriteVirtualConsole(operation, contents)) with
                | EffectUnit -> [ RuntimeUnit ]
                | _ -> fail "IR_BACKEND_EFFECT_RESULT" "Console write returned an invalid primitive result." (Some operation) None [ "Unit" ] []
            | _ -> typedError ()

        and executeBlock (depth: int) (currentWord: string) (localNames: Map<LocalSlot, string>) (block: IrBlock) (initialStack: RuntimeValue list) (initialLocals: Map<LocalSlot, RuntimeValue>) =
            let mutable stack = initialStack
            let mutable locals = initialLocals
            checkRuntimeValueRoots currentWord None (Seq.append stack (locals |> Map.toSeq |> Seq.map snd))
            let popArguments name (inputTypes: IrType list) =
                if stack.Length < inputTypes.Length then
                    fail "RUNTIME_STACK_UNDERFLOW" $"'{name}' requires {inputTypes.Length} value(s)." (Some name) None
                        (inputTypes |> List.map (formatType program)) (runtimeTypeNames program stack)
                let prefix = stack |> List.take (stack.Length - inputTypes.Length)
                let arguments = stack |> List.skip (stack.Length - inputTypes.Length)
                prefix, arguments
            let popOne message word site expected =
                match stack with
                | [] -> fail "RUNTIME_STACK_UNDERFLOW" message (Some word) (sourceSpan site) expected []
                | values -> values |> List.take (values.Length - 1), List.last values
            for instruction in block.Code do
                chargeInstruction currentWord instruction.Site
                let instructionSpan = sourceSpan instruction.Site
                match instruction.Operation with
                | IrOperation.Constant(literal, _) ->
                    let value =
                        match literal with
                        | LInt value -> RuntimeInt value
                        | LFloat value -> RuntimeFloat value
                        | LBool value -> RuntimeBool value
                        | LString value -> RuntimeString value
                        | LUnit -> RuntimeUnit
                    stack <- stack @ [ value ]
                | IrOperation.Call call ->
                    let prefix, arguments = popArguments call.ResolvedName call.InputTypes
                    let result = invokeResolved depth call arguments (Some instruction.Site)
                    stack <- prefix @ result
                | IrOperation.ListEmpty itemType -> stack <- stack @ [ RuntimeList(itemType, []) ]
                | IrOperation.ListSingleton itemType ->
                    let prefix, value = popOne "Typed container constructor requires one payload value." currentWord instruction.Site [ formatType program itemType ]
                    stack <- prefix @ [ RuntimeList(itemType, [ value ]) ]
                | IrOperation.OptionNone itemType -> stack <- stack @ [ RuntimeOption(itemType, None) ]
                | IrOperation.OptionSome itemType ->
                    let prefix, value = popOne "Typed container constructor requires one payload value." currentWord instruction.Site [ formatType program itemType ]
                    stack <- prefix @ [ RuntimeOption(itemType, Some value) ]
                | IrOperation.ResultOk(okType, errorType) ->
                    let prefix, value = popOne "Typed result constructor requires one payload value." currentWord instruction.Site [ formatType program okType ]
                    stack <- prefix @ [ RuntimeResult(okType, errorType, Ok value) ]
                | IrOperation.ResultError(okType, errorType) ->
                    let prefix, value = popOne "Typed result constructor requires one payload value." currentWord instruction.Site [ formatType program errorType ]
                    stack <- prefix @ [ RuntimeResult(okType, errorType, Error value) ]
                | IrOperation.StoreLocal slot ->
                    match stack with
                    | [] -> fail "RUNTIME_STACK_UNDERFLOW" "Local binding requires a stack value." (Some currentWord) instructionSpan [] []
                    | values ->
                        locals <- Map.add slot (List.last values) locals
                        stack <- values |> List.take (values.Length - 1)
                | IrOperation.LoadLocal slot ->
                    match locals.TryFind slot with
                    | Some value -> stack <- stack @ [ value ]
                    | None ->
                        let name = localNames.TryFind slot |> Option.defaultValue (sprintf "%A" slot)
                        fail "RUNTIME_UNKNOWN_LOCAL" $"Local '${name}' has not been bound." (Some currentWord) instructionSpan [] [ name ]
                | IrOperation.ListMap(callback, itemType, outputType) ->
                    let prefix, input = popOne "List higher-order operation requires a list." currentWord instruction.Site []
                    let values =
                        match input with
                        | RuntimeList(actualType, values) when actualType = itemType -> values
                        | actual -> fail "RUNTIME_INTERNAL_TYPE" "List operation received a non-list after type checking." (Some currentWord) instructionSpan [ formatType program (IrList itemType) ] [ formatType program (runtimeValueType actual) ]
                    host.PreflightEffects callback.ResolvedEffects (Some callback.ResolvedName) (Some instruction.Site)
                    host.RecordUse "list.map"
                    host.RecordBranchOutcome currentWord instruction.Site (if List.isEmpty values then "empty" else "nonempty")
                    let outputValues = ResizeArray<RuntimeValue>()
                    let prefixMetrics = aggregateRuntimeValues currentWord (Some instruction.Site) prefix
                    let inputMetrics = runtimeMetrics currentWord (Some instruction.Site) input
                    let outputListBase = runtimeMetrics currentWord (Some instruction.Site) (RuntimeList(outputType, []))
                    let mutable outputNodes =
                        saturatingAdd maxRuntimeValueNodes
                            (saturatingAdd maxRuntimeValueNodes prefixMetrics.ExpandedNodes inputMetrics.ExpandedNodes)
                            outputListBase.ExpandedNodes
                    let mutable outputDepth = max prefixMetrics.Depth (max inputMetrics.Depth outputListBase.Depth)
                    let mutable outputBytes =
                        saturatingAdd maxRuntimeValueOutputBytes
                            (saturatingAdd maxRuntimeValueOutputBytes prefixMetrics.EstimatedOutputBytes inputMetrics.EstimatedOutputBytes)
                            outputListBase.EstimatedOutputBytes
                    checkValueMetrics currentWord (Some instruction.Site)
                        { ExpandedNodes = outputNodes
                          Depth = outputDepth
                          EstimatedOutputBytes = outputBytes }
                    for value in values do
                        chargeInstruction currentWord instruction.Site
                        match invokeResolved (depth + 1) callback [ value ] (Some instruction.Site) with
                        | [ mapped ] ->
                            let mappedMetrics = runtimeMetrics currentWord (Some instruction.Site) mapped
                            outputNodes <- saturatingAdd maxRuntimeValueNodes outputNodes mappedMetrics.ExpandedNodes
                            outputDepth <- max outputDepth (mappedMetrics.Depth + 1)
                            outputBytes <- saturatingAdd maxRuntimeValueOutputBytes outputBytes mappedMetrics.EstimatedOutputBytes
                            checkValueMetrics currentWord (Some instruction.Site)
                                { ExpandedNodes = outputNodes
                                  Depth = outputDepth
                                  EstimatedOutputBytes = outputBytes }
                            outputValues.Add mapped
                        | result -> fail "RUNTIME_INTERNAL_TYPE" $"List callback '{callback.ResolvedName}' returned values outside its checked signature." (Some currentWord) instructionSpan [] (runtimeTypeNames program result)
                    stack <- prefix @ [ RuntimeList(outputType, List.ofSeq outputValues) ]
                | IrOperation.ListFilter(callback, itemType) ->
                    let prefix, input = popOne "List higher-order operation requires a list." currentWord instruction.Site []
                    let values =
                        match input with
                        | RuntimeList(actualType, values) when actualType = itemType -> values
                        | actual -> fail "RUNTIME_INTERNAL_TYPE" "List operation received a non-list after type checking." (Some currentWord) instructionSpan [ formatType program (IrList itemType) ] [ formatType program (runtimeValueType actual) ]
                    host.PreflightEffects callback.ResolvedEffects (Some callback.ResolvedName) (Some instruction.Site)
                    host.RecordUse "list.filter"
                    host.RecordBranchOutcome currentWord instruction.Site (if List.isEmpty values then "empty" else "nonempty")
                    let outputValues = ResizeArray<RuntimeValue>()
                    for value in values do
                        chargeInstruction currentWord instruction.Site
                        match invokeResolved (depth + 1) callback [ value ] (Some instruction.Site) with
                        | [ RuntimeBool true ] -> outputValues.Add value; host.RecordBranchOutcome currentWord instruction.Site "keep"
                        | [ RuntimeBool false ] -> host.RecordBranchOutcome currentWord instruction.Site "drop"
                        | result -> fail "RUNTIME_INTERNAL_TYPE" $"List callback '{callback.ResolvedName}' returned values outside its checked signature." (Some currentWord) instructionSpan [] (runtimeTypeNames program result)
                    stack <- prefix @ [ RuntimeList(itemType, List.ofSeq outputValues) ]
                | IrOperation.ListEach(callback, itemType) ->
                    let prefix, input = popOne "List higher-order operation requires a list." currentWord instruction.Site []
                    let values =
                        match input with
                        | RuntimeList(actualType, values) when actualType = itemType -> values
                        | actual -> fail "RUNTIME_INTERNAL_TYPE" "List operation received a non-list after type checking." (Some currentWord) instructionSpan [ formatType program (IrList itemType) ] [ formatType program (runtimeValueType actual) ]
                    host.PreflightEffects callback.ResolvedEffects (Some callback.ResolvedName) (Some instruction.Site)
                    host.RecordUse "list.each"
                    host.RecordBranchOutcome currentWord instruction.Site (if List.isEmpty values then "empty" else "nonempty")
                    for value in values do
                        chargeInstruction currentWord instruction.Site
                        match invokeResolved (depth + 1) callback [ value ] (Some instruction.Site) with
                        | [ RuntimeUnit ] -> ()
                        | result -> fail "RUNTIME_INTERNAL_TYPE" $"List callback '{callback.ResolvedName}' returned values outside its checked signature." (Some currentWord) instructionSpan [] (runtimeTypeNames program result)
                    stack <- prefix @ [ RuntimeUnit ]
                | IrOperation.ListFold(callback, itemType, accumulatorType) ->
                    let prefix, inputs = popArguments "list.fold" [ IrList itemType; accumulatorType ]
                    let input, initialAccumulator =
                        match inputs with
                        | [ listValue; accumulatorValue ] -> listValue, accumulatorValue
                        | _ -> fail "RUNTIME_INTERNAL_TYPE" "List fold received values outside its verified stack shape." (Some currentWord) instructionSpan
                                   [ formatType program (IrList itemType); formatType program accumulatorType ] (runtimeTypeNames program inputs)
                    let values =
                        match input with
                        | RuntimeList(actualType, values) when actualType = itemType -> values
                        | actual -> fail "RUNTIME_INTERNAL_TYPE" "List fold received a non-list after type checking." (Some currentWord) instructionSpan
                                        [ formatType program (IrList itemType) ] [ formatType program (runtimeValueType actual) ]
                    if runtimeValueType initialAccumulator <> accumulatorType then
                        fail "RUNTIME_INTERNAL_TYPE" "List fold seed does not match its verified accumulator type." (Some currentWord) instructionSpan
                            [ formatType program accumulatorType ] [ formatType program (runtimeValueType initialAccumulator) ]
                    // Effect declarations apply to the whole fold, including an
                    // empty list. Actual callback use is recorded only when the
                    // callback is invoked below.
                    host.PreflightEffects callback.ResolvedEffects (Some callback.ResolvedName) (Some instruction.Site)
                    host.RecordUse "list.fold"
                    host.RecordBranchOutcome currentWord instruction.Site (if List.isEmpty values then "empty" else "nonempty")
                    let mutable accumulator = initialAccumulator
                    for value in values do
                        chargeInstruction currentWord instruction.Site
                        match invokeResolved (depth + 1) callback [ accumulator; value ] (Some instruction.Site) with
                        | [ next ] when runtimeValueType next = accumulatorType -> accumulator <- next
                        | result -> fail "RUNTIME_INTERNAL_TYPE" $"List fold callback '{callback.ResolvedName}' returned values outside its checked accumulator signature." (Some currentWord) instructionSpan
                                        [ formatType program accumulatorType ] (runtimeTypeNames program result)
                    stack <- prefix @ [ accumulator ]
                | IrOperation.If(thenBlock, elseBlock) ->
                    match stack with
                    | [] -> fail "RUNTIME_IF_REQUIRES_BOOL" "'if' requires a Bool at the top of the stack." (Some currentWord) instructionSpan [ "Bool" ] []
                    | values ->
                        match List.last values with
                        | RuntimeBool condition ->
                            let prefix = values |> List.take (values.Length - 1)
                            host.RecordBranchOutcome currentWord instruction.Site (if condition then "true" else "false")
                            let branch = if condition then thenBlock else elseBlock
                            let branchStack, branchLocals = executeBlock depth currentWord localNames branch prefix locals
                            stack <- branchStack
                            locals <- branchLocals
                        | actual -> fail "RUNTIME_IF_REQUIRES_BOOL" "'if' requires a Bool at the top of the stack." (Some currentWord) instructionSpan [ "Bool" ] [ formatType program (runtimeValueType actual) ]
                | IrOperation.Scope innerBlock ->
                    let scopedStack, _ = executeBlock depth currentWord localNames innerBlock stack locals
                    stack <- scopedStack
                | IrOperation.MatchOption(someLocal, someBlock, noneBlock) ->
                    let prefix, input = popOne "match-option requires an Option<T>." currentWord instruction.Site []
                    match input with
                    | RuntimeOption(_, Some value) ->
                        host.RecordBranchOutcome currentWord instruction.Site "some"
                        let branchStack, branchLocals = executeBlock depth currentWord localNames someBlock prefix (Map.add someLocal value locals)
                        stack <- branchStack
                        locals <- Map.remove someLocal branchLocals
                    | RuntimeOption(_, None) ->
                        host.RecordBranchOutcome currentWord instruction.Site "none"
                        let branchStack, branchLocals = executeBlock depth currentWord localNames noneBlock prefix locals
                        stack <- branchStack
                        locals <- branchLocals
                    | actual -> fail "RUNTIME_INTERNAL_TYPE" "match-option received a non-option after type checking." (Some currentWord) instructionSpan [ "Option<T>" ] [ formatType program (runtimeValueType actual) ]
                | IrOperation.MatchResult(okLocal, errorLocal, okBlock, errorBlock) ->
                    let prefix, input = popOne "match-result requires a Result<T, E>." currentWord instruction.Site []
                    match input with
                    | RuntimeResult(_, _, Ok value) ->
                        host.RecordBranchOutcome currentWord instruction.Site "ok"
                        let branchStack, branchLocals = executeBlock depth currentWord localNames okBlock prefix (Map.add okLocal value locals)
                        stack <- branchStack
                        locals <- Map.remove okLocal branchLocals
                    | RuntimeResult(_, _, Error value) ->
                        host.RecordBranchOutcome currentWord instruction.Site "error"
                        let branchStack, branchLocals = executeBlock depth currentWord localNames errorBlock prefix (Map.add errorLocal value locals)
                        stack <- branchStack
                        locals <- Map.remove errorLocal branchLocals
                    | actual -> fail "RUNTIME_INTERNAL_TYPE" "match-result received a non-result after type checking." (Some currentWord) instructionSpan [ "Result<T, E>" ] [ formatType program (runtimeValueType actual) ]
                | IrOperation.MatchEnum(typeKey, cases) ->
                    let prefix, input = popOne "match-enum requires a declared enum value." currentWord instruction.Site []
                    match input with
                    | RuntimeEnum(actualKey, caseIndex) when actualKey = typeKey ->
                        match program.NominalTypesByKey.TryFind typeKey with
                        | Some(IrEnumDefinition enumDefinition) when caseIndex >= 0 && caseIndex < enumDefinition.Cases.Length ->
                            match cases |> List.tryPick (fun (index, branch) -> if index = caseIndex then Some branch else None) with
                            | Some branch ->
                                host.RecordBranchOutcome currentWord instruction.Site enumDefinition.Cases[caseIndex]
                                let branchStack, branchLocals = executeBlock depth currentWord localNames branch prefix locals
                                stack <- branchStack
                                locals <- branchLocals
                            | None -> fail "IR_ENUM_MATCH_CASE_SET" "Verified enum match has no arm for the selected case." (Some currentWord) instructionSpan enumDefinition.Cases [ string caseIndex ]
                        | _ -> fail "IR_BACKEND_ENUM_LAYOUT" "Enum value refers to a missing type or case in its frozen nominal table." (Some currentWord) instructionSpan [ "valid enum type and case" ] [ typeName program actualKey; string caseIndex ]
                    | RuntimeEnum(actualKey, _) -> fail "RUNTIME_INTERNAL_TYPE" "Enum match received a value of another enum type." (Some currentWord) instructionSpan [ typeName program typeKey ] [ typeName program actualKey ]
                    | actual -> fail "RUNTIME_INTERNAL_TYPE" "Enum match received a non-enum value after type checking." (Some currentWord) instructionSpan [ typeName program typeKey ] [ formatType program (runtimeValueType actual) ]
                | IrOperation.MakeRecord(call, _, _) | IrOperation.GetRecordField(call, _, _) | IrOperation.WrapScalar(call, _, _) | IrOperation.UnwrapScalar(call, _) | IrOperation.MakeEnumCase(call, _, _) ->
                    let prefix, arguments = popArguments call.ResolvedName call.InputTypes
                    let result = invokeResolved depth call arguments (Some instruction.Site)
                    stack <- prefix @ result
                checkRuntimeValueRoots currentWord (Some instruction.Site) (Seq.append stack (locals |> Map.toSeq |> Seq.map snd))
            stack, locals

        host.PreflightEffects body.BodyInferredEffects None None
        let result, _ = executeBlock 0 executionName body.BodyLocalNames body.BodyBlock initialStack Map.empty
        checkRuntimeValueRoots executionName None result

        new IrInterpreterResult(
            verifiedProgram,
            result |> List.toArray,
            decodeValue)

    /// Compatibility entry point for callers that only execute zero-input
    /// bodies and immediately observe public Values.
    let executeBody (host: IrInterpreterHost) (executionName: string) (verifiedBody: VerifiedIrBody) : Value list =
        let verifiedProgram = VerifiedIrBody.program verifiedBody
        requireBackendRegistry verifiedProgram
        let program = VerifiedIrProgram.inspect verifiedProgram
        let body = VerifiedIrBody.inspect verifiedBody
        if not (List.isEmpty body.BodyInputTypes) then
            fail "IR_BACKEND_BODY_INPUT_UNSUPPORTED" "The interpreter entry point accepts only bodies with an empty initial stack." (Some executionName) None [] (body.BodyInputTypes |> List.map (formatType program))
        use result = executeBodyWithInputs host executionName verifiedBody None []
        result.Decode()
