namespace AgentLang

open System
open System.Collections.Generic
open System.Globalization
open System.Runtime.CompilerServices
open System.Text

/// Operations that cross from the interpreter into Engine-owned effect providers.
/// Payloads deliberately contain only primitive values; nominal values never cross
/// this boundary.
type IrEffectCommand =
    | ReadFile of operation: string * path: string
    | FileExists of operation: string * path: string
    | WriteFile of operation: string * path: string * contents: string
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
      /// Closed application operations are dispatched only from verified IR
      /// contracts after the normal effect preflight has succeeded.
      InvokeHostOperation: HostOperationId -> Value list -> Result<Value list, HostOperationFailure>
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
    | FloatArgument of double
    | BoolArgument of bool
    | UnitArgument

/// Internal semantic values shared by the interpreter and its opaque retained
/// result. They are never exposed through the public entry API.
type internal IrInterpreterRuntimeValue =
    | RuntimeInt of int64
    | RuntimeFloat of double
    | RuntimeBool of bool
    | RuntimeString of string
    | RuntimeBytes of byte[]
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

    let private maxCallDepth = 64

    /// Installed by RefinedLiterals (end of this file): true when a function-body
    /// literal passed directly to a refined constructor is accepted by the type's
    /// pure validator. Such a construction is a retag in every backend.
    let mutable internal provenRefinedLiteral: VerifiedIrProgram -> SourceSiteId -> Literal -> IrType -> IrOperation -> bool =
        fun _ _ _ _ _ -> false

    /// A map callback that only unwraps a nominal scalar (no effects) changes the
    /// list's element type, not its bytes: nominal scalars share their base layout.
    /// Every backend maps it as a retag without calling the callback.
    let retagOnlyCallback (program: IrProgram) (callback: IrResolvedCall) =
        Set.isEmpty callback.ResolvedEffects
        && match callback.ResolvedTarget with
           | UserWordTarget(id, _) ->
               match program.FunctionsById.TryFind id with
               | Some fn ->
                   match fn.FunctionBody.Code |> List.map (fun instruction -> instruction.Operation) with
                   | [ IrOperation.UnwrapScalar _ ] -> true
                   | [ IrOperation.StoreLocal stored; IrOperation.LoadLocal loaded; IrOperation.UnwrapScalar _ ] -> stored = loaded
                   | _ -> false
               | None -> false
           | GeneratedWordTarget(id, _) ->
               match program.GeneratedTargetsById.TryFind id with
               | Some { Operation = UnwrapScalarOperation _ } -> true
               | _ -> false
           | _ -> false
    // Host root-value and record-field count bound. Lists have no length limit;
    // memory (the native arena capacity, or the value-size budget here) bounds them.
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
            "add"; "subtract"; "multiply"; "divide"; "int.remainder"
            "float.add"; "float.subtract"; "float.multiply"; "float.divide"
            "int.less-than"; "int.greater-than"; "int.less-or-equal"; "int.greater-or-equal"
            "float.less-than"; "float.greater-than"; "float.less-or-equal"; "float.greater-or-equal"
            "equals"; "bool.and"; "bool.or"; "bool.not"
            "string.guid-canonical?"; "string.guid-normalize"; "string.email-address-valid?"
            "int.add-checked"; "int.multiply-checked"; "int.scale-ratio-toward-zero"
            "instant.parse-utc"; "instant.is-canonical-utc?"; "instant.before?"; "instant.add-days"
            "string.concat"; "string.replace"; "string.join"; "string.html-escape"; "string.html-fragment?"; "string.contains"; "string.starts-with"; "string.ends-with"
            "string.length"; "string.byte-length"; "string.char-length"; "string.trim"; "string.to-lower"; "string.to-upper"
            "int.abs"; "int.min"; "int.max"; "int.to-float"; "float.to-int"; "float.round"
            "int.to-string"; "float.to-string"
            "list.count"; "list.tail"; "list.append"; "list.concat"; "list.get"; "list.is-empty?"
            "dup"; "drop"; "swap"
            "file.read"; "file.write"; "file.exists?"; "clock.now"; "console.write"
            "bytes.from-utf8"; "bytes.to-utf8"; "bytes.length"; "bytes.concat"; "bytes.slice"; "bytes.xor"; "bytes.equal-constant-time?"; "base64.encode"; "base64.decode"; "base64.url-encode"; "base64.url-decode"; "hex.encode"; "crypto.sha256"; "crypto.hmac-sha256"; "crypto.pbkdf2-sha256"; "crypto.random-bytes"; "string.split"; "string.index-of"; "string.slice"; "string.byte-at"; "int.parse"; "string.to-lower-ascii"
            "<expect>"; "<expect-equal>"
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
        | RuntimeBytes _ -> IrBytes
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
                | IrBytes -> append "Bytes"
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
            | RuntimeBytes value -> BytesValue(Array.copy value)
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
            | IrBytes -> TBytes
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

    let private ensureProgramContainsOriginalSnapshot (originalProgram: VerifiedIrProgram) (expandedProgram: VerifiedIrProgram) =
        let original = VerifiedIrProgram.inspect originalProgram
        let expanded = VerifiedIrProgram.inspect expandedProgram
        let requireSubset label (source: Map<'key, 'value>) (target: Map<'key, 'value>) =
            for KeyValue(key, value) in source do
                match target.TryFind key with
                | Some expandedValue when expandedValue = value -> ()
                | Some _ ->
                    fail "IR_TEST_DISPATCH_ORIGINAL_CHANGED" "A test-specific verified program changes an entry from the original production snapshot." None None
                        [ $"unchanged {label} entry" ] [ sprintf "%A" key ]
                | None ->
                    fail "IR_TEST_DISPATCH_ORIGINAL_MISSING" "A test-specific verified program omits an entry from the original production snapshot." None None
                        [ $"preserved {label} entry" ] [ sprintf "%A" key ]
        requireSubset "user function" original.FunctionsById expanded.FunctionsById
        requireSubset "generated target" original.GeneratedTargetsById expanded.GeneratedTargetsById
        requireSubset "nominal type" original.NominalTypesByKey expanded.NominalTypesByKey
        requireSubset "host-operation contract" (VerifiedIrProgram.hostOperationContracts originalProgram) (VerifiedIrProgram.hostOperationContracts expandedProgram)
        requireSubset "source map" original.SourceMap expanded.SourceMap
        requireSubset "coverage" original.CoverageByWord expanded.CoverageByWord

    type private TestDispatchTargetContract =
        | ConcreteTargetContract of IrType list * IrType list * Set<IrEffect>
        | PrimitiveTargetContract of IrTypePattern list * IrTypePattern list * Set<IrEffect>

    let private targetContract (program: IrProgram) (hostCatalog: IrHostOperationCatalog) (target: IrCallTarget) =
        match target with
        | UserWordTarget(wordId, revision) ->
            match program.FunctionsById.TryFind wordId with
            | Some functionValue when functionValue.FunctionRevision = revision ->
                Some(ConcreteTargetContract(functionValue.InputTypes, functionValue.OutputTypes, functionValue.FunctionDeclaredEffects))
            | _ -> None
        | PrimitiveTarget primitiveId ->
            Compiler.primitiveIrCatalog.TryFind primitiveId
            |> Option.map (fun contract -> PrimitiveTargetContract(contract.InputPatterns, contract.OutputPatterns, contract.PrimitiveEffects))
        | HostOperationTarget operationId ->
            hostCatalog.TryFind operationId
            |> Option.map (fun contract -> ConcreteTargetContract(contract.InputTypes, contract.OutputTypes, contract.OperationEffects))
        | GeneratedWordTarget _ -> None

    let private primitiveSignatureMatches (inputPatterns: IrTypePattern list) (outputPatterns: IrTypePattern list) (inputs: IrType list) (outputs: IrType list) =
        if inputPatterns.Length <> inputs.Length || outputPatterns.Length <> outputs.Length then false
        else
            let mutable substitutions = Map.empty<int, IrType>
            let rec matches pattern actual =
                match pattern, actual with
                | PatternInt, IrInt | PatternFloat, IrFloat | PatternBool, IrBool | PatternString, IrString | PatternUnit, IrUnit -> true
                | PatternList expected, IrList actual
                | PatternOption expected, IrOption actual -> matches expected actual
                | PatternResult(expectedOk, expectedError), IrResult(actualOk, actualError) ->
                    matches expectedOk actualOk && matches expectedError actualError
                | PatternVariable index, actual ->
                    match substitutions.TryFind index with
                    | Some prior -> prior = actual
                    | None -> substitutions <- Map.add index actual substitutions; true
                | _ -> false
            List.forall2 matches inputPatterns inputs && List.forall2 matches outputPatterns outputs

    let private primitiveContractIsClosed (inputPatterns: IrTypePattern list) (outputPatterns: IrTypePattern list) =
        let rec isClosed = function
            | PatternVariable _ -> false
            | PatternList item | PatternOption item -> isClosed item
            | PatternResult(okType, errorType) -> isClosed okType && isClosed errorType
            | PatternInt | PatternFloat | PatternBool | PatternString | PatternBytes | PatternUnit -> true
        List.forall isClosed (inputPatterns @ outputPatterns)

    let private validateTestDispatch (originalProgram: VerifiedIrProgram) (expandedProgram: VerifiedIrProgram) (dispatch: Map<IrCallTarget, IrCallTarget>) =
        requireBackendRegistry originalProgram
        requireBackendRegistry expandedProgram
        let original = VerifiedIrProgram.inspect originalProgram
        let expanded = VerifiedIrProgram.inspect expandedProgram
        ensureProgramContainsOriginalSnapshot originalProgram expandedProgram
        let originalHostCatalog = VerifiedIrProgram.hostOperationContracts originalProgram
        let expandedHostCatalog = VerifiedIrProgram.hostOperationContracts expandedProgram
        let fixtureTargets = ResizeArray<WordId>()
        for KeyValue(originalTarget, fixtureTarget) in dispatch do
            match targetContract original originalHostCatalog originalTarget with
            | None ->
                fail "IR_TEST_DISPATCH_ORIGINAL_TARGET" "Test dispatch supports only an authored user word, trusted primitive, or registered host operation at its active identity; generated targets are not replaceable." None None
                    [ "active authored user-word, primitive, or registered host-operation target" ] [ sprintf "%A" originalTarget ]
            | Some contract ->
                match contract with
                | PrimitiveTargetContract(inputPatterns, outputPatterns, _) when not (primitiveContractIsClosed inputPatterns outputPatterns) ->
                    fail "IR_TEST_DISPATCH_POLYMORPHIC_TARGET" "Test dispatch cannot replace a polymorphic primitive whose call sites may use different concrete signatures." None None
                        [ "closed primitive signature" ] [ sprintf "%A" originalTarget ]
                | _ -> ()
                match fixtureTarget with
                | UserWordTarget(fixtureId, fixtureRevision) ->
                    if (original.FunctionsById.ContainsKey fixtureId) then
                        fail "IR_TEST_DISPATCH_FIXTURE_ID_COLLISION" "A test fixture must use a user-function identity distinct from every production function." None None
                            [ "distinct synthetic fixture ID" ] [ sprintf "%A" fixtureId ]
                    match expanded.FunctionsById.TryFind fixtureId with
                    | None ->
                        fail "IR_TEST_DISPATCH_FIXTURE_MISSING" "Test dispatch refers to a fixture function absent from its exact verified program." None None
                            [ sprintf "%A" fixtureId ] [ sprintf "%A" fixtureTarget ]
                    | Some fixture when fixture.FunctionRevision <> fixtureRevision ->
                        fail "IR_TEST_DISPATCH_FIXTURE_REVISION" "Test dispatch revision differs from the fixture function in its exact verified program." (Some fixture.FunctionName) None
                            [ string fixtureRevision ] [ string fixture.FunctionRevision ]
                    | Some fixture ->
                        let isSyntheticFixtureId =
                            match fixtureId with
                            | WordId raw -> raw.StartsWith("flow-test-fixture-", StringComparison.Ordinal)
                        if fixture.FunctionId <> fixtureId
                           || not (fixture.FunctionName.StartsWith("$flow$test-fixture$", StringComparison.Ordinal))
                           || not isSyntheticFixtureId then
                            fail "IR_TEST_DISPATCH_FIXTURE_IDENTITY" "Test dispatch fixtures must use the lowerer's reserved ephemeral name and identity namespace." (Some fixture.FunctionName) None
                                [ "$flow$test-fixture$... / flow-test-fixture-..." ] [ sprintf "%A %s" fixtureId fixture.FunctionName ]
                        let signatureMatches =
                            match contract with
                            | ConcreteTargetContract(originalInputs, originalOutputs, _) ->
                                fixture.InputTypes = originalInputs && fixture.OutputTypes = originalOutputs
                            | PrimitiveTargetContract(inputPatterns, outputPatterns, _) ->
                                primitiveSignatureMatches inputPatterns outputPatterns fixture.InputTypes fixture.OutputTypes
                        let originalEffects =
                            match contract with
                            | ConcreteTargetContract(_, _, effects)
                            | PrimitiveTargetContract(_, _, effects) -> effects
                        if not signatureMatches then
                            let originalDescription =
                                match contract with
                                | ConcreteTargetContract(originalInputs, originalOutputs, _) ->
                                    (originalInputs |> List.map (formatType expanded)) @ [ "->" ] @ (originalOutputs |> List.map (formatType expanded))
                                | PrimitiveTargetContract(inputPatterns, outputPatterns, _) ->
                                    [ sprintf "%A" inputPatterns; "->"; sprintf "%A" outputPatterns ]
                            fail "IR_TEST_DISPATCH_SIGNATURE" "Test fixture signature is incompatible with the original target contract." (Some fixture.FunctionName) None
                                originalDescription
                                ((fixture.InputTypes |> List.map (formatType expanded)) @ [ "->" ] @ (fixture.OutputTypes |> List.map (formatType expanded)))
                        if not (Set.isSubset fixture.FunctionDeclaredEffects originalEffects) then
                            fail "IR_TEST_DISPATCH_EFFECTS" "Test fixture declares an effect outside the original target contract." (Some fixture.FunctionName) None
                                (originalEffects |> Set.toList |> List.map IrEffects.format)
                                (fixture.FunctionDeclaredEffects |> Set.toList |> List.map IrEffects.format)
                        if fixtureTargets.Contains fixtureId then
                            fail "IR_TEST_DISPATCH_FIXTURE_DUPLICATE" "Two original targets cannot share one synthetic fixture function." (Some fixture.FunctionName) None
                                [ "unique fixture target per override" ] [ sprintf "%A" fixtureId ]
                        fixtureTargets.Add fixtureId
                | _ ->
                    fail "IR_TEST_DISPATCH_FIXTURE_KIND" "Test dispatch values must identify synthetic verified user functions." None None
                        [ "UserWordTarget fixture" ] [ sprintf "%A" fixtureTarget ]
        let fixtureIdSet = fixtureTargets |> Set.ofSeq
        let keySet (entries: Map<'key, 'value>) = entries |> Map.toSeq |> Seq.map fst |> Set.ofSeq
        let requireExactKeys label (expected: Set<'key>) (actual: Set<'key>) =
            if expected <> actual then
                fail "IR_TEST_DISPATCH_PROGRAM_SHAPE" $"The test-specific verified program contains unexpected {label} identities." None None
                    (expected |> Set.toList |> List.map (sprintf "%A")) (actual |> Set.toList |> List.map (sprintf "%A"))
        requireExactKeys "user-function" (Set.union (keySet original.FunctionsById) fixtureIdSet) (keySet expanded.FunctionsById)
        requireExactKeys "generated-target" (keySet original.GeneratedTargetsById) (keySet expanded.GeneratedTargetsById)
        requireExactKeys "nominal-type" (keySet original.NominalTypesByKey) (keySet expanded.NominalTypesByKey)
        requireExactKeys "coverage" (Set.union (keySet original.CoverageByWord) fixtureIdSet) (keySet expanded.CoverageByWord)
        requireExactKeys "host-operation contract" (keySet originalHostCatalog) (keySet expandedHostCatalog)
        let unexpectedSourceSites =
            expanded.SourceMap
            |> Map.toSeq
            |> Seq.choose (fun (site, _) ->
                if original.SourceMap.ContainsKey site then None
                else
                    match site with
                    | SourceSiteId(Some owner, _) when fixtureIdSet.Contains owner -> None
                    | _ -> Some site)
            |> Seq.toList
        if not (List.isEmpty unexpectedSourceSites) then
            fail "IR_TEST_DISPATCH_PROGRAM_SHAPE" "The test-specific verified program contains source-map entries outside its original snapshot and mapped fixtures." None None
                [ "original or fixture-owned source sites" ] (unexpectedSourceSites |> List.map (sprintf "%A"))

    let private sourceAt (sourceMap: Map<SourceSiteId, IrSourceSite>) site =
        sourceMap.TryFind site |> Option.map (fun entry -> entry.SiteSpan)

    let private checkedIntegerOperation (operation: string) (span: SourceSpan option) (functionValue: int64 -> int64 -> int64) left right =
        try RuntimeInt(functionValue left right)
        with :? OverflowException ->
            fail "RUNTIME_OVERFLOW" $"'{operation}' overflowed its Int64 result." (Some operation) span [] [ string left; string right ]

    let private executeBodyWithInputsAndDispatch
        (host: IrInterpreterHost)
        (executionName: string)
        (verifiedBody: VerifiedIrBody)
        (testOriginalProgram: VerifiedIrProgram option)
        (testDispatch: Map<IrCallTarget, IrCallTarget>)
        (inputOwner: IrInterpreterResult option)
        (arguments: IrEntryArgument list)
        (valueArguments: Value list option) : IrInterpreterResult =
        let verifiedProgram = VerifiedIrBody.program verifiedBody
        requireBackendRegistry verifiedProgram
        match testOriginalProgram with
        | Some original when not (Object.ReferenceEquals(original, verifiedProgram)) ->
            validateTestDispatch original verifiedProgram testDispatch
        | Some _ when Map.isEmpty testDispatch -> ()
        | Some _ ->
            fail "IR_TEST_DISPATCH_PROGRAM_MISMATCH" "A test overlay must use a distinct verified program from its original production snapshot." None None
                [ "separate ephemeral overlay program" ] [ "same VerifiedIrProgram instance" ]
        | None when not (Map.isEmpty testDispatch) ->
            fail "IR_TEST_DISPATCH_ORIGINAL_REQUIRED" "Test dispatch requires the original production snapshot for target validation." None None
                [ "original VerifiedIrProgram" ] [ "none" ]
        | None -> ()
        let program = VerifiedIrProgram.inspect verifiedProgram
        let body = VerifiedIrBody.inspect verifiedBody

        // Check owner provenance and liveness before argument shape, matching the
        // native entry contract. Root values are immutable so this shallow copy
        // remains valid even if the caller disposes the input owner concurrently.
        let retainedRoots =
            match inputOwner with
            | Some owner -> owner.AcquireRootsFor(verifiedProgram, executionName)
            | None -> [||]
        match valueArguments with
        | Some values when isNull (box values) ->
            fail "IR_BACKEND_ENTRY_ARGUMENT_INVALID" "Flow entrypoint arguments must be a non-null value list." (Some executionName) None
                [ "value list" ] [ "null" ]
        | Some values when values.Length <> body.BodyInputTypes.Length ->
            fail "IR_BACKEND_ENTRY_ARGUMENT_COUNT" "Entry argument count does not match the verified body input signature." (Some executionName) None
                [ string body.BodyInputTypes.Length ] [ string values.Length ]
        | None when arguments.Length <> body.BodyInputTypes.Length ->
            fail "IR_BACKEND_ENTRY_ARGUMENT_COUNT" "Entry argument count does not match the verified body input signature." (Some executionName) None
                [ string body.BodyInputTypes.Length ] [ string arguments.Length ]
        | _ -> ()

        let primitiveInputStack =
            match valueArguments with
            | Some _ -> []
            | None ->
                arguments
                |> List.map (function
                    | IrEntryArgument.RetainedRoot index ->
                        if index < 0 || index >= retainedRoots.Length then
                            fail "IR_BACKEND_ENTRY_ROOT_INDEX" "Entry argument selects a root outside the retained input owner." (Some executionName) None
                                [ $"root index in [0, {retainedRoots.Length})" ] [ string index ]
                        retainedRoots[index]
                    | IrEntryArgument.IntArgument value -> RuntimeInt value
                    | IrEntryArgument.FloatArgument value -> RuntimeFloat value
                    | IrEntryArgument.BoolArgument value -> RuntimeBool value
                    | IrEntryArgument.UnitArgument -> RuntimeUnit)
        match valueArguments with
        | Some _ -> ()
        | None ->
            let actualInputTypes = primitiveInputStack |> List.map runtimeValueType
            if actualInputTypes <> body.BodyInputTypes then
                fail "IR_BACKEND_ENTRY_ARGUMENT_TYPE" "Entry argument types do not match the verified body input signature." (Some executionName) None
                    (body.BodyInputTypes |> List.map (formatType program)) (runtimeTypeNames program primitiveInputStack)
            for value in primitiveInputStack do
                match value with
                | RuntimeFloat number when not (Double.IsFinite number) ->
                    fail "IR_BACKEND_ENTRY_ARGUMENT_NONFINITE" "Float entry arguments must be finite." (Some executionName) None
                        [ "finite Float" ] [ string number ]
                | _ -> ()

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
                | IrInt | IrFloat | IrBool | IrString | IrBytes | IrUnit -> ()
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
            | RuntimeInt _ | RuntimeFloat _ | RuntimeBool _ | RuntimeString _ | RuntimeBytes _ | RuntimeUnit
            | RuntimeOption(_, None) | RuntimeEnum _ -> []

        let runtimeValueOwnOutputBytes currentWord site value =
            let typeDepth, typeBytes = typeFootprint currentWord site (runtimeValueType value)
            let valueBytes =
                match value with
                | RuntimeString text -> 64L + int64 text.Length * 6L
                | RuntimeBytes data -> 64L + int64 data.Length * 2L
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

        let validateHostTypeMetadata currentWord site rootType =
            let pending = Stack<LangType * int>()
            if isNull (box rootType) then
                fail "HOST_OPERATION_VALUE_INVALID" "Host value contains null container type metadata." (Some currentWord) (site |> Option.bind sourceSpan) [ "declared container type" ] [ "null" ]
            pending.Push((rootType, 1))
            let mutable nodes = 0L
            while pending.Count > 0 do
                let current, depth = pending.Pop()
                if isNull (box current) then
                    fail "HOST_OPERATION_VALUE_INVALID" "Host value contains null nested type metadata." (Some currentWord) (site |> Option.bind sourceSpan) [ "declared container type" ] [ "null" ]
                nodes <- saturatingAdd maxRuntimeValueNodes nodes 1L
                if depth > maxRuntimeValueDepth || nodes > maxRuntimeValueNodes then
                    fail "RUNTIME_VALUE_LIMIT" "Host value type metadata exceeds the bounded type traversal limit." (Some currentWord) (site |> Option.bind sourceSpan)
                        [ $"type depth <= {maxRuntimeValueDepth} and nodes <= {maxRuntimeValueNodes}" ] [ $"depth={depth};nodes={nodes}" ]
                match current with
                | TList item | TOption item -> pending.Push((item, depth + 1))
                | TResult(okType, errorType) -> pending.Push((errorType, depth + 1)); pending.Push((okType, depth + 1))
                | TNamed name when String.IsNullOrWhiteSpace name ->
                    fail "HOST_OPERATION_VALUE_INVALID" "Host value contains an empty nominal type name." (Some currentWord) (site |> Option.bind sourceSpan) [ "declared type name" ] [ "empty" ]
                | TInt | TFloat | TBool | TString | TBytes | TUnit | TNamed _ | TVar _ -> ()

        let validateHostValueBudget currentWord site (roots: Value list) =
            let invalid actual =
                fail "HOST_OPERATION_VALUE_INVALID" "Host operation returned a malformed value payload." (Some currentWord) (site |> Option.bind sourceSpan) [ "well-formed declared Flow value" ] [ actual ]
            if isNull (box roots) then invalid "null output collection"
            if roots.Length > maxCollectionLength then
                fail "RUNTIME_VALUE_LIMIT" "Host operation returned too many output values." (Some currentWord) (site |> Option.bind sourceSpan)
                    [ $"output count <= {maxCollectionLength}" ] [ string roots.Length ]
            let pending = Stack<Value * int>()
            for value in roots do pending.Push((value, 1))
            let mutable nodes = 0L
            let mutable outputBytes = 0L
            let addBytes count =
                outputBytes <- saturatingAdd maxRuntimeValueOutputBytes outputBytes count
                if outputBytes > maxRuntimeValueOutputBytes then
                    fail "RUNTIME_VALUE_LIMIT" "Host value payload exceeds the bounded output-size limit." (Some currentWord) (site |> Option.bind sourceSpan)
                        [ $"estimated output bytes <= {maxRuntimeValueOutputBytes}" ] [ string outputBytes ]
            while pending.Count > 0 do
                let value, depth = pending.Pop()
                if isNull (box value) then invalid "null nested value"
                nodes <- saturatingAdd maxRuntimeValueNodes nodes 1L
                if depth > maxRuntimeValueDepth || nodes > maxRuntimeValueNodes then
                    fail "RUNTIME_VALUE_LIMIT" "Host value payload exceeds the bounded depth or node limit." (Some currentWord) (site |> Option.bind sourceSpan)
                        [ $"depth <= {maxRuntimeValueDepth}; nodes <= {maxRuntimeValueNodes}" ] [ $"depth={depth};nodes={nodes}" ]
                addBytes 64L
                let push child = pending.Push((child, depth + 1))
                match value with
                | StringValue text ->
                    if isNull text then invalid "null string"
                    if not (TrustedValues.isWellFormedUtf16 text) then
                        fail "STRING_INVALID_UTF16" "A String contains an unpaired surrogate and has no UTF-8 form." (Some currentWord) (site |> Option.bind sourceSpan) [ "well-formed Unicode text" ] [ "unpaired surrogate" ]
                    addBytes (int64 text.Length * 6L)
                | BytesValue data ->
                    if isNull data then invalid "null bytes"
                    addBytes (int64 data.Length)
                | FloatValue number when not (Double.IsFinite number) -> invalid "non-finite float"
                | FloatValue _ -> ()
                | ListValue(itemType, values) ->
                    validateHostTypeMetadata currentWord site itemType
                    if isNull (box values) then invalid "null list payload"
                    addBytes (int64 values.Length * 2L)
                    values |> List.iter push
                | OptionValue(itemType, value) ->
                    validateHostTypeMetadata currentWord site itemType
                    value |> Option.iter push
                | ResultValue(okType, errorType, value) ->
                    validateHostTypeMetadata currentWord site okType
                    validateHostTypeMetadata currentWord site errorType
                    if isNull (box value) then invalid "null result arm"
                    match value with Ok item | Error item -> push item
                | RecordValue(typeName, fields) ->
                    if String.IsNullOrWhiteSpace typeName then invalid "empty record type name"
                    if isNull (box fields) then invalid "null record fields"
                    if fields.Count > maxCollectionLength then
                        fail "RUNTIME_VALUE_LIMIT" "Host record exceeds the bounded field limit." (Some currentWord) (site |> Option.bind sourceSpan)
                            [ $"record fields <= {maxCollectionLength}" ] [ string fields.Count ]
                    addBytes (int64 typeName.Length * 6L)
                    for KeyValue(fieldName, fieldValue) in fields do
                        if String.IsNullOrWhiteSpace fieldName then invalid "empty record field name"
                        addBytes (int64 fieldName.Length * 6L)
                        push fieldValue
                | NamedValue(typeName, inner) ->
                    if String.IsNullOrWhiteSpace typeName then invalid "empty scalar type name"
                    addBytes (int64 typeName.Length * 6L)
                    push inner
                | EnumValue(typeName, caseName) ->
                    if String.IsNullOrWhiteSpace typeName || String.IsNullOrWhiteSpace caseName then invalid "empty enum type or case name"
                    addBytes (int64 (typeName.Length + caseName.Length) * 6L)
                | IntValue _ | BoolValue _ | UnitValue -> ()

        // No instruction limit: verified programs have acyclic call graphs and
        // traverse only finite lists, so execution terminates. Hosts may still
        // count instructions through ChargeInstruction.
        let chargeInstruction currentWord site =
            host.ChargeInstruction currentWord site

        let decodeValue = decodeRuntimeValue program

        let rec invokeResolved (depth: int) (call: IrResolvedCall) (arguments: RuntimeValue list) (site: SourceSiteId option) =
            if depth > maxCallDepth then
                fail "RUNTIME_CALL_DEPTH" "Execution exceeded the 64 word call-depth limit." (Some call.ResolvedName) (host.WordDefinitionSpan call.ResolvedName) [] []
            host.PreflightEffects call.ResolvedEffects (Some call.ResolvedName) site
            match testDispatch.TryFind call.ResolvedTarget with
            | Some(UserWordTarget(fixtureId, fixtureRevision)) ->
                executeDispatchedFixture call fixtureId fixtureRevision arguments depth
            | Some other ->
                fail "IR_TEST_DISPATCH_FIXTURE_KIND" "Test dispatch values must identify synthetic verified user functions." (Some call.ResolvedName) (site |> Option.bind sourceSpan)
                    [ "UserWordTarget fixture" ] [ sprintf "%A" other ]
            | None ->
                invokeUnchanged depth call arguments site

        and executeDispatchedFixture (call: IrResolvedCall) (fixtureId: WordId) (fixtureRevision: int) (arguments: RuntimeValue list) (depth: int) =
            match program.FunctionsById.TryFind fixtureId with
            | Some fixture when fixture.FunctionRevision = fixtureRevision ->
                if call.InputTypes <> fixture.InputTypes || call.OutputTypes <> fixture.OutputTypes then
                    fail "IR_TEST_DISPATCH_CALL_SIGNATURE" "A resolved original call specialization does not match its test fixture signature." (Some call.ResolvedName) None
                        ((call.InputTypes |> List.map (formatType program)) @ [ "->" ] @ (call.OutputTypes |> List.map (formatType program)))
                        ((fixture.InputTypes |> List.map (formatType program)) @ [ "->" ] @ (fixture.OutputTypes |> List.map (formatType program)))
                if not (Set.isSubset fixture.FunctionDeclaredEffects call.ResolvedDeclaredEffects) then
                    fail "IR_TEST_DISPATCH_CALL_EFFECTS" "A test fixture declares effects outside this original call specialization's contract." (Some call.ResolvedName) None
                        (call.ResolvedDeclaredEffects |> Set.toList |> List.map IrEffects.format)
                        (fixture.FunctionDeclaredEffects |> Set.toList |> List.map IrEffects.format)
                host.RecordUse fixture.FunctionName
                executeFunction depth fixture arguments
            | _ -> fail "IR_TEST_DISPATCH_FIXTURE_MISSING" "Test dispatch fixture is absent or stale in its exact verified program." (Some call.ResolvedName) None
                     [ sprintf "%A" fixtureId; string fixtureRevision ] []

        and invokeUnchanged (depth: int) (call: IrResolvedCall) (arguments: RuntimeValue list) (site: SourceSiteId option) =
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
            | HostOperationTarget operationId ->
                let catalog = VerifiedIrProgram.hostOperationContracts verifiedProgram
                match catalog.TryFind operationId with
                | None ->
                    fail "IR_BACKEND_HOST_OPERATION_MISSING" "Verified call refers to a host operation absent from its retained program contract." (Some call.ResolvedName) (site |> Option.bind sourceSpan) [] [ sprintf "%A" operationId ]
                | Some contract ->
                    if contract.OperationId <> operationId
                       || contract.OperationName <> call.ResolvedName
                       || contract.InputTypes <> call.InputTypes
                       || contract.OutputTypes <> call.OutputTypes
                       || contract.OperationEffects <> call.ResolvedDeclaredEffects
                       || contract.OperationEffects <> call.ResolvedEffects then
                        fail "IR_BACKEND_HOST_OPERATION_CONTRACT" "Host call does not match the exact contract retained by its verified program." (Some call.ResolvedName) (site |> Option.bind sourceSpan)
                            [ contract.OperationName; (contract.InputTypes |> List.map (formatType program) |> String.concat ",") ]
                            [ call.ResolvedName; (call.InputTypes |> List.map (formatType program) |> String.concat ",") ]
                    if arguments.Length <> contract.InputTypes.Length then
                        fail "IR_BACKEND_HOST_OPERATION_ARITY" "Host call argument count differs from its retained contract." (Some call.ResolvedName) (site |> Option.bind sourceSpan)
                            [ string contract.InputTypes.Length ] [ string arguments.Length ]
                    checkRuntimeValueRoots call.ResolvedName site arguments
                    let checkedInputs =
                        List.zip contract.InputTypes arguments
                        |> List.map (fun (expectedType, argument) -> importHostValue depth call.ResolvedName site expectedType (decodeValue argument))
                    checkRuntimeValueRoots call.ResolvedName site checkedInputs
                    let publicInputs = checkedInputs |> List.map decodeValue
                    host.RecordUse call.ResolvedName
                    let providerResult = host.InvokeHostOperation operationId publicInputs
                    if isNull (box providerResult) then
                        fail "HOST_OPERATION_PROVIDER_INVALID" "The host operation adapter returned no result envelope." (Some call.ResolvedName) (site |> Option.bind sourceSpan) [ "provider result" ] [ "null" ]
                    match providerResult with
                    | Error HostOperationFailure.MissingHandler ->
                        fail "HOST_OPERATION_HANDLER_MISSING" "The registered host operation has no active provider handler." (Some call.ResolvedName) (site |> Option.bind sourceSpan) [ "registered provider handler" ] []
                    | Error HostOperationFailure.InvocationFailed ->
                        fail "HOST_OPERATION_FAILED" "The host operation provider failed without a language-level result." (Some call.ResolvedName) (site |> Option.bind sourceSpan) [ "successful provider invocation or declared domain result" ] []
                    | Error HostOperationFailure.ProviderContractMismatch ->
                        fail "HOST_OPERATION_PROVIDER_MISMATCH" "The active provider does not match the registered host-operation contract." (Some call.ResolvedName) (site |> Option.bind sourceSpan) [ sprintf "%A" operationId ] []
                    | Error HostOperationFailure.TestProviderUnavailable ->
                        fail "HOST_OPERATION_TEST_PROVIDER_UNAVAILABLE" "The test host-operation provider is unavailable; production providers are never used for tests." (Some call.ResolvedName) (site |> Option.bind sourceSpan) [ "test provider factory" ] []
                    | Ok publicOutputs ->
                        validateHostValueBudget call.ResolvedName site publicOutputs
                        if publicOutputs.Length <> contract.OutputTypes.Length then
                            fail "HOST_OPERATION_OUTPUT_ARITY" "Host operation output count differs from its retained contract." (Some call.ResolvedName) (site |> Option.bind sourceSpan)
                                [ string contract.OutputTypes.Length ] [ string publicOutputs.Length ]
                        let outputs =
                            List.zip contract.OutputTypes publicOutputs
                            |> List.map (fun (expectedType, value) -> importHostValue depth call.ResolvedName site expectedType value)
                        checkRuntimeValueRoots call.ResolvedName site outputs
                        outputs

        and importHostValue (depth: int) (currentWord: string) (site: SourceSiteId option) (expectedType: IrType) (value: Value) : RuntimeValue =
            let invalid actual =
                fail "HOST_OPERATION_VALUE_INVALID" "Host operation value does not satisfy its exact verified Flow type and refinements." (Some currentWord) (site |> Option.bind sourceSpan)
                    [ formatType program expectedType ] [ actual ]
            if isNull (box value) then invalid "null value"
            let rec toLangType = function
                | IrInt -> TInt
                | IrFloat -> TFloat
                | IrBool -> TBool
                | IrString -> TString
                | IrBytes -> TBytes
                | IrUnit -> TUnit
                | IrList item -> TList(toLangType item)
                | IrOption item -> TOption(toLangType item)
                | IrResult(okType, errorType) -> TResult(toLangType okType, toLangType errorType)
                | IrNominal key -> TNamed(typeName program key)
            let requireContainerType actual =
                validateHostTypeMetadata currentWord site actual
                if actual <> toLangType expectedType then invalid "container metadata does not match the exact contract"
            let runValidator validator value =
                match validator with
                | None -> value
                | Some checkedCall ->
                    match invokeResolved (depth + 1) checkedCall [ value ] site with
                    | [ RuntimeBool true ] -> value
                    | [ RuntimeBool false ] -> invalid "nominal refinement validator returned false"
                    | _ -> invalid "nominal refinement validator returned a non-Bool result"
            match expectedType, value with
            | IrInt, IntValue number -> RuntimeInt number
            | IrFloat, FloatValue number when Double.IsFinite number -> RuntimeFloat number
            | IrBool, BoolValue flag -> RuntimeBool flag
            | IrString, StringValue text when not (isNull text) && TrustedValues.isWellFormedUtf16 text -> RuntimeString text
            | IrBytes, BytesValue data when not (isNull data) -> RuntimeBytes(Array.copy data)
            | IrUnit, UnitValue -> RuntimeUnit
            | IrList itemType, ListValue(actualType, values) ->
                requireContainerType (TList actualType)
                RuntimeList(itemType, values |> List.map (importHostValue depth currentWord site itemType))
            | IrOption itemType, OptionValue(actualType, value) ->
                requireContainerType (TOption actualType)
                RuntimeOption(itemType, Option.map (importHostValue depth currentWord site itemType) value)
            | IrResult(okType, errorType), ResultValue(actualOk, actualError, value) ->
                requireContainerType (TResult(actualOk, actualError))
                RuntimeResult(okType, errorType, Result.map (importHostValue depth currentWord site okType) value
                              |> Result.mapError (importHostValue depth currentWord site errorType))
            | IrNominal key, RecordValue(actualName, fields) ->
                match program.NominalTypesByKey.TryFind key with
                | Some(IrRecordDefinition definition) when definition.TypeName = actualName && not (isNull (box fields)) ->
                    let declaredFields = definition.RecordFields |> List.sortBy (fun field -> field.FieldIndex)
                    if fields.Count <> declaredFields.Length || declaredFields |> List.exists (fun field -> not (fields.ContainsKey field.FieldName)) then
                        invalid "record field set differs from the verified layout"
                    let values = declaredFields |> List.map (fun field -> importHostValue depth currentWord site field.FieldType fields[field.FieldName])
                    runValidator definition.ValidatorCall (RuntimeRecord(key, values))
                | Some(IrRecordDefinition _) -> invalid "record name or payload differs from the verified nominal type"
                | _ -> invalid "expected nominal type is not a record"
            | IrNominal key, NamedValue(actualName, inner) ->
                match program.NominalTypesByKey.TryFind key with
                | Some(IrScalarDefinition definition) when definition.TypeName = actualName ->
                    let payload = importHostValue depth currentWord site definition.BaseType inner
                    runValidator definition.ValidatorCall payload |> ignore
                    RuntimeScalar(key, payload)
                | Some(IrScalarDefinition _) -> invalid "scalar name differs from the verified nominal type"
                | _ -> invalid "expected nominal type is not a scalar"
            | IrNominal key, EnumValue(actualName, caseName) ->
                match program.NominalTypesByKey.TryFind key with
                | Some(IrEnumDefinition definition) when definition.TypeName = actualName ->
                    match definition.Cases |> List.tryFindIndex ((=) caseName) with
                    | Some caseIndex -> RuntimeEnum(key, caseIndex)
                    | None -> invalid "enum case is not declared by the verified nominal type"
                | Some(IrEnumDefinition _) -> invalid "enum name differs from the verified nominal type"
                | _ -> invalid "expected nominal type is not an enum"
            | _ -> invalid "value kind differs from the verified contract"

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
            // C remainder: truncated toward zero, sign of the dividend; MinValue % -1 is 0 (.NET would throw).
            | "int.remainder", [ RuntimeInt _; RuntimeInt 0L ] -> fail "RUNTIME_DIVIDE_BY_ZERO" "Integer division by zero." (Some operation) None [] []
            | "int.remainder", [ RuntimeInt _; RuntimeInt -1L ] -> [ RuntimeInt 0L ]
            | "int.remainder", [ RuntimeInt left; RuntimeInt right ] -> [ RuntimeInt(left % right) ]
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
            | "<expect>", [ RuntimeBool true ] -> []
            | "<expect>", [ RuntimeBool false ] ->
                fail "TEST_ASSERTION_FAILED" "Test expectation evaluated to false." (Some currentWord) (site |> Option.bind sourceSpan) [ "true" ] [ "false" ]
            | "<expect-equal>", [ left; right ] when left = right -> []
            | "<expect-equal>", [ left; right ] ->
                let leftText = Types.formatValue (decodeValue left)
                let rightText = Types.formatValue (decodeValue right)
                fail "TEST_ASSERTION_FAILED" $"Test equality expectation failed: left {leftText} does not equal right {rightText}." (Some currentWord) (site |> Option.bind sourceSpan) [ leftText ] [ rightText ]
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
            | ("bytes.from-utf8" | "bytes.to-utf8" | "bytes.length" | "bytes.concat" | "bytes.slice" | "bytes.xor" | "bytes.equal-constant-time?" | "base64.encode" | "base64.decode" | "base64.url-encode" | "base64.url-decode" | "hex.encode" | "crypto.sha256" | "crypto.hmac-sha256" | "crypto.pbkdf2-sha256" | "string.split" | "string.index-of" | "string.slice" | "string.byte-at" | "int.parse" | "string.to-lower-ascii"), _ ->
                // Standard library slice 1 (report 272): pure byte, text and crypto primitives.
                let some itemType value = RuntimeOption(itemType, Some value)
                let none itemType = RuntimeOption(itemType, None)
                let utf8 = System.Text.UTF8Encoding(false, true)
                // Strict base64: exact alphabet, no whitespace, '=' only as final
                // padding, and zero unused tail bits, so every byte string has one text.
                let strictBase64 (urlSafe: bool) (paddingRequired: bool) (text: string) : byte[] option =
                    let value (ch: char) =
                        if ch >= 'A' && ch <= 'Z' then int ch - int 'A'
                        elif ch >= 'a' && ch <= 'z' then int ch - int 'a' + 26
                        elif ch >= '0' && ch <= '9' then int ch - int '0' + 52
                        elif ch = (if urlSafe then '-' else '+') then 62
                        elif ch = (if urlSafe then '_' else '/') then 63
                        else -1
                    let data = text.TrimEnd('=')
                    let padding = text.Length - data.Length
                    let remainder = data.Length % 4
                    let expectedPadding = if remainder = 0 then 0 else 4 - remainder
                    if remainder = 1 || padding > 2
                       || (padding > 0 && padding <> expectedPadding)
                       || (paddingRequired && padding <> expectedPadding)
                       || data |> Seq.exists (fun ch -> value ch < 0) then None
                    else
                        let values = data |> Seq.map value |> Array.ofSeq
                        let tailMask = match remainder with 2 -> 0x0F | 3 -> 0x03 | _ -> 0
                        if remainder <> 0 && (values[values.Length - 1] &&& tailMask) <> 0 then None
                        else
                            let output = Collections.Generic.List<byte>(data.Length * 3 / 4)
                            let mutable bits = 0
                            let mutable count = 0
                            for v in values do
                                bits <- (bits <<< 6) ||| v
                                count <- count + 6
                                if count >= 8 then
                                    count <- count - 8
                                    output.Add(byte ((bits >>> count) &&& 0xFF))
                            Some(output.ToArray())
                match operation, arguments with
                | "bytes.from-utf8", [ RuntimeString text ] -> [ RuntimeBytes(utf8.GetBytes text) ]
                | "bytes.to-utf8", [ RuntimeBytes data ] ->
                    [ (try some IrString (RuntimeString(utf8.GetString data)) with :? Text.DecoderFallbackException -> none IrString) ]
                | "bytes.length", [ RuntimeBytes data ] -> [ RuntimeInt(int64 data.Length) ]
                | "bytes.concat", [ RuntimeBytes left; RuntimeBytes right ] -> [ RuntimeBytes(Array.append left right) ]
                | "bytes.slice", [ RuntimeBytes data; RuntimeInt start; RuntimeInt count ] ->
                    if start < 0L || count < 0L || start > int64 data.Length || count > int64 data.Length - start then [ none IrBytes ]
                    else [ some IrBytes (RuntimeBytes(Array.sub data (int start) (int count))) ]
                | "bytes.xor", [ RuntimeBytes left; RuntimeBytes right ] ->
                    if left.Length <> right.Length then [ none IrBytes ]
                    else [ some IrBytes (RuntimeBytes(Array.map2 (^^^) left right)) ]
                | "bytes.equal-constant-time?", [ RuntimeBytes left; RuntimeBytes right ] ->
                    [ RuntimeBool(Security.Cryptography.CryptographicOperations.FixedTimeEquals(ReadOnlySpan left, ReadOnlySpan right)) ]
                | "base64.encode", [ RuntimeBytes data ] -> [ RuntimeString(Convert.ToBase64String data) ]
                | "base64.decode", [ RuntimeString text ] ->
                    [ (match strictBase64 false true text with Some data -> some IrBytes (RuntimeBytes data) | None -> none IrBytes) ]
                | "base64.url-encode", [ RuntimeBytes data ] ->
                    [ RuntimeString(Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_')) ]
                | "base64.url-decode", [ RuntimeString text ] ->
                    [ (match strictBase64 true false text with Some data -> some IrBytes (RuntimeBytes data) | None -> none IrBytes) ]
                | "hex.encode", [ RuntimeBytes data ] -> [ RuntimeString(Convert.ToHexString(data).ToLowerInvariant()) ]
                | "crypto.sha256", [ RuntimeBytes data ] -> [ RuntimeBytes(Security.Cryptography.SHA256.HashData data) ]
                | "crypto.hmac-sha256", [ RuntimeBytes key; RuntimeBytes message ] ->
                    [ RuntimeBytes(Security.Cryptography.HMACSHA256.HashData(key, message)) ]
                | "crypto.pbkdf2-sha256", [ RuntimeBytes password; RuntimeBytes salt; RuntimeInt iterations; RuntimeInt length ] ->
                    if iterations < 1L || iterations > 1000000L || length < 1L || length > 1024L then [ none IrBytes ]
                    else
                        let key = Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(password, salt, int iterations, Security.Cryptography.HashAlgorithmName.SHA256, int length)
                        [ some IrBytes (RuntimeBytes key) ]
                | "string.split", [ RuntimeString text; RuntimeString separator ] ->
                    let pieces =
                        if separator.Length = 0 then [ text ]
                        else
                            let textBytes = utf8.GetBytes text
                            let separatorBytes = utf8.GetBytes separator
                            let results = Collections.Generic.List<string>()
                            let mutable start = 0
                            let mutable index = 0
                            while index <= textBytes.Length - separatorBytes.Length do
                                if ReadOnlySpan(textBytes, index, separatorBytes.Length).SequenceEqual(ReadOnlySpan separatorBytes) then
                                    results.Add(utf8.GetString(textBytes, start, index - start))
                                    index <- index + separatorBytes.Length
                                    start <- index
                                else index <- index + 1
                            results.Add(utf8.GetString(textBytes, start, textBytes.Length - start))
                            List.ofSeq results
                    [ RuntimeList(IrString, pieces |> List.map RuntimeString) ]
                | "string.index-of", [ RuntimeString text; RuntimeString needle; RuntimeInt start ] ->
                    let textBytes = utf8.GetBytes text
                    let needleBytes = utf8.GetBytes needle
                    if start < 0L || start > int64 textBytes.Length then [ none IrInt ]
                    else
                        let found = ReadOnlySpan(textBytes, int start, textBytes.Length - int start).IndexOf(ReadOnlySpan needleBytes)
                        [ (if found < 0 then none IrInt else some IrInt (RuntimeInt(start + int64 found))) ]
                | "string.slice", [ RuntimeString text; RuntimeInt start; RuntimeInt count ] ->
                    let textBytes = utf8.GetBytes text
                    let isBoundary (index: int64) = index = int64 textBytes.Length || (textBytes[int index] &&& 0xC0uy) <> 0x80uy
                    if start < 0L || count < 0L || start > int64 textBytes.Length || count > int64 textBytes.Length - start
                       || not (isBoundary start) || not (isBoundary (start + count)) then [ none IrString ]
                    else [ some IrString (RuntimeString(utf8.GetString(textBytes, int start, int count))) ]
                | "string.byte-at", [ RuntimeString text; RuntimeInt index ] ->
                    let textBytes = utf8.GetBytes text
                    if index < 0L || index >= int64 textBytes.Length then [ none IrInt ]
                    else [ some IrInt (RuntimeInt(int64 textBytes[int index])) ]
                | "string.to-lower-ascii", [ RuntimeString text ] ->
                    // ASCII A-Z only: every other scalar value is unchanged.
                    [ RuntimeString(String(text.ToCharArray() |> Array.map (fun ch -> if ch >= 'A' && ch <= 'Z' then char (int ch + 32) else ch))) ]
                | "int.parse", [ RuntimeString text ] ->
                    let digits = if text.StartsWith "-" then text.Substring 1 else text
                    let mutable parsed = 0L
                    if digits.Length = 0 || not (digits |> Seq.forall Char.IsAsciiDigit)
                       || not (Int64.TryParse(text, Globalization.NumberStyles.AllowLeadingSign, Globalization.CultureInfo.InvariantCulture, &parsed))
                       || text.StartsWith "+" then [ none IrInt ]
                    else [ some IrInt (RuntimeInt parsed) ]
                | _ -> fail "IR_PRIMITIVE_ARGUMENTS" $"Primitive '{operation}' received unsupported arguments." (Some currentWord) (site |> Option.bind sourceSpan) [] []
            | "crypto.random-bytes", [ RuntimeInt count ] ->
                if count < 0L || count > 65536L then
                    fail "RUNTIME_RANDOM_BYTES_RANGE" "crypto.random-bytes takes a count from 0 to 65536." (Some currentWord) (site |> Option.bind sourceSpan) [ "0..65536" ] [ string count ]
                [ RuntimeBytes(Security.Cryptography.RandomNumberGenerator.GetBytes(int count)) ]
            | "string.replace", [ RuntimeString text; RuntimeString old; RuntimeString replacement ] ->
                if old.Length = 0 then
                    [ RuntimeString text ]
                else
                    // Matching is ordinal over UTF-8 bytes, left to right, non-overlapping, as the
                    // owning-stack backend does. UTF-8 is self-synchronizing, so the decoded result is exact.
                    let utf8 = System.Text.Encoding.UTF8
                    let textBytes = utf8.GetBytes text
                    let oldBytes = utf8.GetBytes old
                    let newBytes = utf8.GetBytes replacement
                    let typeDepth, typeBytes = typeFootprint currentWord site IrString
                    let maxMatches = int64 textBytes.Length / int64 oldBytes.Length
                    let estimatedBytes = saturatingAdd maxRuntimeValueOutputBytes (64L + typeBytes) (int64 textBytes.Length + maxMatches * int64 newBytes.Length)
                    checkValueMetrics currentWord site
                        { ExpandedNodes = 1L
                          Depth = max 1 typeDepth
                          EstimatedOutputBytes = estimatedBytes }
                    let output = System.Collections.Generic.List<byte>(textBytes.Length)
                    let matchesAt index =
                        index <= textBytes.Length - oldBytes.Length
                        && Array.forall id (Array.init oldBytes.Length (fun offset -> textBytes.[index + offset] = oldBytes.[offset]))
                    let mutable index = 0
                    while index < textBytes.Length do
                        if matchesAt index then
                            output.AddRange newBytes
                            index <- index + oldBytes.Length
                        else
                            output.Add textBytes.[index]
                            index <- index + 1
                    [ RuntimeString(utf8.GetString(output.ToArray())) ]
            | "string.join", [ RuntimeList(IrString, parts) ] ->
                let texts =
                    parts |> List.map (function
                        | RuntimeString text -> text
                        | _ -> fail "RUNTIME_INTERNAL_TYPE" "string.join received a non-String part." (Some currentWord) (site |> Option.bind sourceSpan) [ "String" ] [])
                let typeDepth, typeBytes = typeFootprint currentWord site IrString
                let units = texts |> List.sumBy (fun text -> int64 text.Length)
                checkValueMetrics currentWord site
                    { ExpandedNodes = 1L
                      Depth = max 1 typeDepth
                      EstimatedOutputBytes = saturatingAdd maxRuntimeValueOutputBytes (64L + typeBytes) (units * 6L) }
                [ RuntimeString(String.concat "" texts) ]
            | "string.html-escape", [ RuntimeString value ] ->
                let escaped = TrustedValues.htmlEscape value
                let typeDepth, typeBytes = typeFootprint currentWord site IrString
                checkValueMetrics currentWord site
                    { ExpandedNodes = 1L
                      Depth = max 1 typeDepth
                      EstimatedOutputBytes = saturatingAdd maxRuntimeValueOutputBytes (64L + typeBytes) (int64 escaped.Length * 6L) }
                [ RuntimeString escaped ]
            | "string.html-fragment?", [ RuntimeString value ] -> [ RuntimeBool(HtmlMarkup.isFragment value) ]
            | "string.contains", [ RuntimeString value; RuntimeString sub ] -> [ RuntimeBool(value.Contains(sub, StringComparison.Ordinal)) ]
            | "string.starts-with", [ RuntimeString value; RuntimeString sub ] -> [ RuntimeBool(value.StartsWith(sub, StringComparison.Ordinal)) ]
            | "string.ends-with", [ RuntimeString value; RuntimeString sub ] -> [ RuntimeBool(value.EndsWith(sub, StringComparison.Ordinal)) ]
            | ("string.length" | "string.byte-length"), [ RuntimeString value ] -> [ RuntimeInt(TrustedValues.utf8ByteLength value) ]
            | "string.char-length", [ RuntimeString value ] -> [ RuntimeInt(TrustedValues.characterLength value) ]
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
            | "float.to-string", [ RuntimeFloat value ] -> [ RuntimeString(FloatText.format value) ]
            | "list.count", [ RuntimeList(_, values) ] -> [ RuntimeInt(int64 values.Length) ]
            | "list.tail", [ RuntimeList(itemType, []) ] -> [ RuntimeList(itemType, []) ]
            | "list.tail", [ RuntimeList(itemType, _ :: values) ] -> [ RuntimeList(itemType, values) ]
            | "list.append", [ RuntimeList(itemType, values); value ] when runtimeValueType value = itemType ->
                [ RuntimeList(itemType, values @ [ value ]) ]
            | "list.concat", [ RuntimeList(itemType, left); RuntimeList(otherType, right) ] when itemType = otherType -> [ RuntimeList(itemType, left @ right) ]
            | "list.get", [ RuntimeList(itemType, values); RuntimeInt index ] when index >= 0L && index < int64 values.Length -> [ RuntimeOption(itemType, Some values[int index]) ]
            | "list.get", [ RuntimeList(itemType, _); RuntimeInt _ ] -> [ RuntimeOption(itemType, None) ]
            | "list.is-empty?", [ RuntimeList(_, values) ] -> [ RuntimeBool(List.isEmpty values) ]
            | "file.read", [ RuntimeString path ] ->
                match host.InvokeEffect(ReadFile(operation, path)) with
                | EffectString contents -> [ RuntimeString contents ]
                | _ -> fail "IR_BACKEND_EFFECT_RESULT" "File read returned an invalid primitive result." (Some operation) None [ "String" ] []
            | "file.exists?", [ RuntimeString path ] ->
                match host.InvokeEffect(FileExists(operation, path)) with
                | EffectBool exists -> [ RuntimeBool exists ]
                | _ -> fail "IR_BACKEND_EFFECT_RESULT" "File existence check returned an invalid primitive result." (Some operation) None [ "Bool" ] []
            | "file.write", [ RuntimeString path; RuntimeString contents ] ->
                match host.InvokeEffect(WriteFile(operation, path, contents)) with
                | EffectUnit -> [ RuntimeUnit ]
                | _ -> fail "IR_BACKEND_EFFECT_RESULT" "File write returned an invalid primitive result." (Some operation) None [ "Unit" ] []
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
            let mutable previousOperation: IrOperation option = None
            for instruction in block.Code do
                chargeInstruction currentWord instruction.Site
                let instructionSpan = sourceSpan instruction.Site
                let provenLiteralWrap =
                    match previousOperation, instruction.Operation, instruction.Site with
                    | Some(IrOperation.Constant(literal, literalType)), (IrOperation.WrapScalar(_, _, Some _) as wrap), SourceSiteId(Some _, _) ->
                        provenRefinedLiteral verifiedProgram instruction.Site literal literalType wrap
                    | _ -> false
                previousOperation <- Some instruction.Operation
                match instruction.Operation with
                | IrOperation.WrapScalar(call, key, _) when provenLiteralWrap ->
                    // Proven at compile time by the same pure validator: retag only.
                    let prefix, arguments = popArguments call.ResolvedName call.InputTypes
                    stack <- prefix @ [ RuntimeScalar(key, List.head arguments) ]
                | IrOperation.HtmlTemplate(key, parts) ->
                    // Html by construction (verified skeleton, escaped String holes,
                    // Html holes): no validator call.
                    let holeTypes =
                        parts |> List.choose (function
                            | HtmlTemplatePart.StringHole -> Some IrString
                            | HtmlTemplatePart.HtmlHole -> Some(IrNominal key)
                            | HtmlTemplatePart.HtmlListHole -> Some(IrList(IrNominal key))
                            | HtmlTemplatePart.Text _ -> None)
                    let prefix, holes = popArguments "html template" holeTypes
                    let builder = Text.StringBuilder()
                    let mutable remaining = holes
                    for part in parts do
                        match part with
                        | HtmlTemplatePart.Text text -> builder.Append(text) |> ignore
                        | HtmlTemplatePart.StringHole | HtmlTemplatePart.HtmlHole | HtmlTemplatePart.HtmlListHole ->
                            match remaining with
                            | RuntimeList(_, items) :: rest when part = HtmlTemplatePart.HtmlListHole ->
                                for item in items do
                                    match item with
                                    | RuntimeScalar(_, RuntimeString value) -> builder.Append(value) |> ignore
                                    | _ -> fail "RUNTIME_STACK_TYPE" "html template List<Html> hole holds a non-Html item." (Some currentWord) instructionSpan [] []
                                remaining <- rest
                            | RuntimeString value :: rest when part = HtmlTemplatePart.StringHole ->
                                builder.Append(TrustedValues.htmlEscape value) |> ignore
                                remaining <- rest
                            | RuntimeScalar(_, RuntimeString value) :: rest when part = HtmlTemplatePart.HtmlHole ->
                                builder.Append(value) |> ignore
                                remaining <- rest
                            | _ -> fail "RUNTIME_STACK_TYPE" "html template hole has the wrong runtime type." (Some currentWord) instructionSpan [] []
                    let markup = builder.ToString()
                    let typeDepth, typeBytes = typeFootprint currentWord (Some instruction.Site) (IrNominal key)
                    checkValueMetrics currentWord (Some instruction.Site)
                        { ExpandedNodes = 1L
                          Depth = max 1 typeDepth
                          EstimatedOutputBytes = saturatingAdd maxRuntimeValueOutputBytes (64L + typeBytes) (int64 markup.Length * 6L) }
                    stack <- prefix @ [ RuntimeScalar(key, RuntimeString markup) ]
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
                | IrOperation.ListOf(itemType, count) ->
                    if count < 1 || stack.Length < count then
                        fail "RUNTIME_STACK_UNDERFLOW" "List literal requires one value per element." (Some currentWord) (sourceSpan instruction.Site) [ string count ] [ string stack.Length ]
                    let prefix = stack |> List.take (stack.Length - count)
                    stack <- prefix @ [ RuntimeList(itemType, stack |> List.skip (stack.Length - count)) ]
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
                | IrOperation.ListMap(callback, itemType, outputType) when retagOnlyCallback program callback ->
                    // Retag: same elements under the base type; the callback is not called.
                    let prefix, input = popOne "List higher-order operation requires a list." currentWord instruction.Site []
                    match input with
                    | RuntimeList(actualType, values) when actualType = itemType ->
                        host.RecordUse "list.map"
                        host.RecordBranchOutcome currentWord instruction.Site (if List.isEmpty values then "empty" else "nonempty")
                        let unwrapped =
                            values |> List.map (function
                                | RuntimeScalar(_, value) -> value
                                | actual -> fail "RUNTIME_INTERNAL_TYPE" "Retag map received a non-scalar element after type checking." (Some currentWord) instructionSpan [] [ formatType program (runtimeValueType actual) ])
                        stack <- prefix @ [ RuntimeList(outputType, unwrapped) ]
                    | actual -> fail "RUNTIME_INTERNAL_TYPE" "List operation received a non-list after type checking." (Some currentWord) instructionSpan [ formatType program (IrList itemType) ] [ formatType program (runtimeValueType actual) ]
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
                | IrOperation.ListMapWithContext(callback, contextType, itemType, outputType) ->
                    let prefix, inputs = popArguments "list.map" [ IrList itemType; contextType ]
                    let input, context =
                        match inputs with
                        | [ listValue; contextValue ] -> listValue, contextValue
                        | _ -> fail "RUNTIME_INTERNAL_TYPE" "List map with context received values outside its verified stack shape." (Some currentWord) instructionSpan
                                   [ formatType program (IrList itemType); formatType program contextType ] (runtimeTypeNames program inputs)
                    let values =
                        match input with
                        | RuntimeList(actualType, values) when actualType = itemType -> values
                        | actual -> fail "RUNTIME_INTERNAL_TYPE" "List map with context received a non-list after type checking." (Some currentWord) instructionSpan
                                        [ formatType program (IrList itemType) ] [ formatType program (runtimeValueType actual) ]
                    if runtimeValueType context <> contextType then
                        fail "RUNTIME_INTERNAL_TYPE" "List map context does not match its verified context type." (Some currentWord) instructionSpan
                            [ formatType program contextType ] [ formatType program (runtimeValueType context) ]
                    host.PreflightEffects callback.ResolvedEffects (Some callback.ResolvedName) (Some instruction.Site)
                    host.RecordUse "list.map"
                    host.RecordBranchOutcome currentWord instruction.Site (if List.isEmpty values then "empty" else "nonempty")
                    let outputValues = ResizeArray<RuntimeValue>()
                    let prefixMetrics = aggregateRuntimeValues currentWord (Some instruction.Site) prefix
                    let contextMetrics = runtimeMetrics currentWord (Some instruction.Site) context
                    let inputMetrics = runtimeMetrics currentWord (Some instruction.Site) input
                    let outputListBase = runtimeMetrics currentWord (Some instruction.Site) (RuntimeList(outputType, []))
                    let mutable outputNodes =
                        saturatingAdd maxRuntimeValueNodes
                            (saturatingAdd maxRuntimeValueNodes
                                (saturatingAdd maxRuntimeValueNodes prefixMetrics.ExpandedNodes contextMetrics.ExpandedNodes)
                                inputMetrics.ExpandedNodes)
                            outputListBase.ExpandedNodes
                    let mutable outputDepth = max prefixMetrics.Depth (max contextMetrics.Depth (max inputMetrics.Depth outputListBase.Depth))
                    let mutable outputBytes =
                        saturatingAdd maxRuntimeValueOutputBytes
                            (saturatingAdd maxRuntimeValueOutputBytes
                                (saturatingAdd maxRuntimeValueOutputBytes prefixMetrics.EstimatedOutputBytes contextMetrics.EstimatedOutputBytes)
                                inputMetrics.EstimatedOutputBytes)
                            outputListBase.EstimatedOutputBytes
                    checkValueMetrics currentWord (Some instruction.Site)
                        { ExpandedNodes = outputNodes
                          Depth = outputDepth
                          EstimatedOutputBytes = outputBytes }
                    for value in values do
                        chargeInstruction currentWord instruction.Site
                        match invokeResolved (depth + 1) callback [ context; value ] (Some instruction.Site) with
                        | [ mapped ] when runtimeValueType mapped = outputType ->
                            let mappedMetrics = runtimeMetrics currentWord (Some instruction.Site) mapped
                            outputNodes <- saturatingAdd maxRuntimeValueNodes outputNodes mappedMetrics.ExpandedNodes
                            outputDepth <- max outputDepth (mappedMetrics.Depth + 1)
                            outputBytes <- saturatingAdd maxRuntimeValueOutputBytes outputBytes mappedMetrics.EstimatedOutputBytes
                            checkValueMetrics currentWord (Some instruction.Site)
                                { ExpandedNodes = outputNodes
                                  Depth = outputDepth
                                  EstimatedOutputBytes = outputBytes }
                            outputValues.Add mapped
                        | result -> fail "RUNTIME_INTERNAL_TYPE" $"List map callback '{callback.ResolvedName}' returned values outside its checked signature." (Some currentWord) instructionSpan
                                        [ formatType program outputType ] (runtimeTypeNames program result)
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
                | IrOperation.Scope innerBlock
                | IrOperation.Compact innerBlock ->
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

        let initialStack =
            match valueArguments with
            | None -> primitiveInputStack
            | Some values ->
                let contextualizeEntryValidation action =
                    try action ()
                    with
                    | LanguageException diagnostic when diagnostic.Code = "HOST_OPERATION_VALUE_INVALID" ->
                        raise (LanguageException
                            { diagnostic with
                                Code = "IR_BACKEND_ENTRY_ARGUMENT_INVALID"
                                Message = "Flow entrypoint argument does not satisfy its exact verified type and refinements." })
                    | LanguageException diagnostic when diagnostic.Code = "RUNTIME_VALUE_LIMIT" ->
                        raise (LanguageException
                            { diagnostic with
                                Message = "Flow entrypoint argument exceeds the bounded value limit." })
                contextualizeEntryValidation (fun () -> validateHostValueBudget executionName None values)
                let imported =
                    List.zip body.BodyInputTypes values
                    |> List.map (fun (expectedType, value) ->
                        contextualizeEntryValidation (fun () -> importHostValue 0 executionName None expectedType value))
                checkRuntimeValueRoots executionName None imported
                imported

        host.PreflightEffects body.BodyInferredEffects None None
        let result, _ = executeBlock 0 executionName body.BodyLocalNames body.BodyBlock initialStack Map.empty
        checkRuntimeValueRoots executionName None result

        new IrInterpreterResult(
            verifiedProgram,
            result |> List.toArray,
            decodeValue)

    /// Execute a detached test body with an immutable, test-scoped replacement
    /// map. The original production snapshot is used to validate exact targets;
    /// the body and all fixture functions must belong to the expanded program.
    let executeBodyWithTestDispatch
        (host: IrInterpreterHost)
        (executionName: string)
        (originalProgram: VerifiedIrProgram)
        (dispatch: Map<IrCallTarget, IrCallTarget>)
        (verifiedBody: VerifiedIrBody)
        (inputOwner: IrInterpreterResult option)
        (arguments: IrEntryArgument list) : IrInterpreterResult =
        executeBodyWithInputsAndDispatch host executionName verifiedBody (Some originalProgram) dispatch inputOwner arguments None

    /// Execute an ordinary detached body without test-scoped replacement routing.
    let executeBodyWithInputs
        (host: IrInterpreterHost)
        (executionName: string)
        (verifiedBody: VerifiedIrBody)
        (inputOwner: IrInterpreterResult option)
        (arguments: IrEntryArgument list) : IrInterpreterResult =
        executeBodyWithInputsAndDispatch host executionName verifiedBody None Map.empty inputOwner arguments None

    /// Execute a verified body with checked public Values as its initial stack.
    /// This stays internal to the Engine boundary; callers cannot forge IR or
    /// bypass the import/refinement and aggregate-budget checks above.
    let internal executeBodyWithValues
        (host: IrInterpreterHost)
        (executionName: string)
        (verifiedBody: VerifiedIrBody)
        (values: Value list) : IrInterpreterResult =
        executeBodyWithInputsAndDispatch host executionName verifiedBody None Map.empty None [] (Some values)

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

/// Compile-time evaluation of refined-type literals. Scalar validators are pure
/// (TYPE_VALIDATOR_EFFECT), so a literal that passes its validator once passes
/// it on every run: backends may then retag it without a runtime check.
module RefinedLiterals =
    exception private ValidatorEffect

    let private host: IrInterpreterHost =
        { PreflightEffects = fun effects _ _ -> if not (Set.isEmpty effects) then raise ValidatorEffect
          ChargeInstruction = fun _ _ -> ()
          RecordBranchOutcome = fun _ _ _ -> ()
          RecordUse = ignore
          InvokeEffect = fun _ -> raise ValidatorEffect
          InvokeHostOperation = fun _ _ -> raise ValidatorEffect
          EnterUserFunction = fun _ _ _ -> fun () -> ()
          ReturnUserFunction = fun _ _ _ -> ()
          WordDefinitionSpan = fun _ -> None
          PrimitiveDefinitionSpan = fun _ -> None }

    /// Outcome of running a refined constructor on a literal at compile time.
    type Outcome =
        | Accepted
        | Rejected of Diagnostic
        | Deferred

    /// Runs `literal` through the refined constructor `wrap` (an IrOperation.WrapScalar
    /// with a validator) against the verified program. `Rejected` carries the
    /// validator's diagnostic (REFINEMENT_FAILED or the validator's own failure).
    let evaluate (verifiedProgram: VerifiedIrProgram) (literal: Literal) (literalType: IrType) (wrap: IrOperation) =
        match wrap with
        | IrOperation.WrapScalar(call, _, Some _) ->
            let span = { File = "<refined-literal>"; Line = 1; Column = 1; Length = 1 }
            let site index = SourceSiteId(None, index)
            let source kind = { SiteOwner = None; SiteSpan = span; SourceKind = kind }
            let block =
                { EntryShape = { StackTypes = []; LocalTypes = Map.empty }
                  ExitShape = { StackTypes = call.OutputTypes; LocalTypes = Map.empty }
                  Code =
                    [ { Site = site 0; Operation = IrOperation.Constant(literal, literalType) }
                      { Site = site 1; Operation = wrap } ] }
            let sourceMap = Map.ofList [ site 0, source "constant"; site 1, source "call" ]
            let program = VerifiedIrProgram.inspect verifiedProgram
            let body =
                { BodyName = "<refined-literal>"
                  BodyInputTypes = []
                  BodyOutputTypes = call.OutputTypes
                  BodyDeclaredEffects = Set.empty
                  BodyInferredEffects = Set.empty
                  BodyLocalNames = Map.empty
                  BodyBlock = block
                  BodySourceMap = sourceMap
                  BodyCoverage = IrVerifier.coverageObligationsWithTypes program.NominalTypesByKey sourceMap block }
            // A verification failure is a compiler bug, not a rejected literal: let it propagate.
            let verified = IrVerifier.verifyBody verifiedProgram body
            try
                IrInterpreter.executeBody host "<refined-literal>" verified |> ignore
                Accepted
            with
            | ValidatorEffect -> Deferred
            | LanguageException diagnostic -> Rejected diagnostic
        | _ -> Deferred

    let private proofs = ConditionalWeakTable<VerifiedIrProgram, System.Collections.Concurrent.ConcurrentDictionary<SourceSiteId, bool>>()

    /// Cached per program and WrapScalar site: whether this function-body literal
    /// construction is proven. Shared by the interpreter and native backends so
    /// they skip exactly the same runtime checks.
    let proven (verifiedProgram: VerifiedIrProgram) (site: SourceSiteId) (literal: Literal) (literalType: IrType) (wrap: IrOperation) =
        match site with
        | SourceSiteId(Some _, _) ->
            proofs.GetValue(verifiedProgram, fun _ -> System.Collections.Concurrent.ConcurrentDictionary())
                .GetOrAdd(site, fun _ -> evaluate verifiedProgram literal literalType wrap = Accepted)
        | SourceSiteId(None, _) -> false

    do IrInterpreter.provenRefinedLiteral <- proven
