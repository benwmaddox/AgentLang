module AgentLang.NativeValueStack.Program

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open AgentLang
open AgentLang.Llvm

type private EntryBodies =
    { Program: VerifiedIrProgram
      CompilerContext: Compiler.IrLoweringContext
      Initialize: VerifiedIrBody
      TurnOne: VerifiedIrBody
      TurnEight: VerifiedIrBody
      Branch: VerifiedIrBody
      UnitValue: VerifiedIrBody
      UnitDrop: VerifiedIrBody
      EmptyValue: VerifiedIrBody
      ScopedWidth: VerifiedIrBody
      ScopeShadow: VerifiedIrBody
      Depth64: VerifiedIrBody
      Depth65: VerifiedIrBody
      DirectDupDrop: VerifiedIrBody
      FailAfterAllocation: VerifiedIrBody }

let private span file column =
    { File = file
      Line = 1
      Column = column
      Length = 1 }

let private lowerFirst (name: string) =
    if String.IsNullOrEmpty name then name
    else string (Char.ToLowerInvariant name[0]) + name.Substring(1)

let private generatedRecordEntries (records: Map<string, RecordDefinition>) =
    records
    |> Map.toList
    |> List.collect (fun (name, record) ->
        let prefix = lowerFirst name
        let definition wordName inputs outputs =
            { Name = wordName
              Inputs = inputs
              Outputs = outputs
              Effects = Set.empty
              Maturity = LibraryWord
              Revision = 1
              Documentation = "Generated record operation."
              Body = []
              SourceText = ""
              Span = span "<generated-native-value-stack-record>" 1 }
        let entry builtin wordName inputs outputs =
            { Definition = definition wordName inputs outputs
              Builtin = Some builtin
              Status = Persistent
              Maturity = LibraryWord
              Revision = 1 }
        let constructor =
            entry (RecordConstructor name) (prefix + ".new") (record.Fields |> List.map (fun field -> field.Type)) [ TNamed name ]
        let accessors =
            record.Fields
            |> List.map (fun field ->
                entry (RecordAccessor(name, field.Name)) (prefix + "." + field.Name) [ TNamed name ] [ field.Type ])
        constructor :: accessors)

let private turnSource repetition =
    let repeatedCalls =
        [ 1 .. repetition ]
        |> List.map (fun _ -> "    mailbox::exercise(value);")
        |> String.concat "\n"
    $"""fn mailbox.turn-{repetition}(state: State, value: Int) -> State {{
    effects none
    let returned = mailbox::make-envelope(value, add(value, 1000));
{repeatedCalls}
    let observed = returned.leaf.value;
    let increment = if equals(observed, value) {{ 1 }} else {{ 2 }};
    state::new(count = add(state.count, increment), last = returned)
}}"""

let private depthChainSource maxDepth =
    [ 0 .. maxDepth ]
    |> List.map (fun depth ->
        let body = if depth = 0 then "value" else $"mailbox::depth-{depth - 1}(value)"
        $"""fn mailbox.depth-{depth}(value: Int) -> Int {{
    effects none
    {body}
}}""")
    |> String.concat "\n\n"

let private compileEntries (source: string) =
    let document =
        match FlowParser.parseDocumentWithVersion 2 "value-stack.flow" source with
        | Ok parsed -> parsed
        | Error diagnostic -> failwith (Diagnostics.render diagnostic)
    let records =
        document.Records
        |> List.map (fun record -> record.Name, record)
        |> Map.ofList
        |> Map.add "Empty"
            { Name = "Empty"
              Fields = []
              Validator = None
              SourceText = "host-built conformance record; Flow source syntax rejects empty records"
              Span = span "<native-value-stack-empty-record>" 1 }
    let words =
        (generatedRecordEntries records)
        |> List.fold (fun current entry -> Map.add entry.Definition.Name entry current) Compiler.primitives
    let wordIds =
        words
        |> Map.toList
        |> List.map (fun (name, entry) ->
            let prefix =
                match entry.Builtin with
                | Some(BuiltinOp _) -> "primitive-"
                | Some _ -> "generated-"
                | None -> "user-"
            name, WordId(prefix + name))
        |> Map.ofList
    let compilerContext: Compiler.IrLoweringContext =
        { Words = words
          Records = records
          Scalars = Map.empty
          Enums = Map.empty
          WordIds = wordIds }
    let context: FlowLowering.Context =
        { CompilerContext = compilerContext
          ParameterNames = Map.empty
          Flow2OwnerIds = Set.empty
          SourceOrigins = Map.empty }
    let changes: FlowLowering.FlowWordChange list =
        document.Words
        |> List.map (fun definition ->
            { FlowLowering.FlowWordChange.Definition = definition
              RevisionIntent = FlowLowering.FlowWordRevisionIntent.Add(WordId("native-value-stack-source-" + definition.Name), 1) })
    let compiled = FlowLowering.compileBatchWords context changes
    let compileEntry entryName target inputTypes =
        Compiler.compileIrBodyAgainstProgramWithSourceOrigins
            compiled.Context.CompilerContext
            compiled.Program
            entryName
            inputTypes
            [ Call(target, span ("<" + entryName + ">") 1) ]
            compiled.Context.SourceOrigins
    let entries =
        { Program = compiled.Program
          CompilerContext = compiled.Context.CompilerContext
          Initialize = compileEntry "mailbox.initialize.entry" "mailbox.initialize" []
          TurnOne = compileEntry "mailbox.turn-1.entry" "mailbox.turn-1" [ TNamed "State"; TInt ]
          TurnEight = compileEntry "mailbox.turn-8.entry" "mailbox.turn-8" [ TNamed "State"; TInt ]
          Branch = compileEntry "mailbox.branch.entry" "mailbox.branch" [ TInt; TBool ]
          UnitValue = compileEntry "mailbox.unit-value.entry" "mailbox.unit-value" []
          UnitDrop =
            Compiler.compileIrBodyAgainstProgramWithSourceOrigins
                compiled.Context.CompilerContext
                compiled.Program
                "native-value-stack-unit-drop"
                []
                [ Push(LUnit, span "<native-value-stack-unit-drop>" 1)
                  Call("drop", span "<native-value-stack-unit-drop>" 2)
                  Push(LInt 123L, span "<native-value-stack-unit-drop>" 3) ]
                compiled.Context.SourceOrigins
          EmptyValue = compileEntry "mailbox.empty-value.entry" "mailbox.empty-value" []
          ScopedWidth = compileEntry "mailbox.scoped-width.entry" "mailbox.scoped-width" [ TInt ]
          Depth64 = compileEntry "mailbox.depth-64.entry" "mailbox.depth-64" [ TInt ]
          Depth65 = compileEntry "mailbox.depth-65.entry" "mailbox.depth-65" [ TInt ]
          ScopeShadow =
            Compiler.compileIrBodyAgainstProgramWithSourceOrigins
                compiled.Context.CompilerContext
                compiled.Program
                "native-value-stack-scope-local-shadow"
                []
                [ Push(LInt 17L, span "<native-value-stack-scope-local-shadow>" 1)
                  Push(LInt 700L, span "<native-value-stack-local-shadow>" 2)
                  Call("mailbox.make-envelope", span "<native-value-stack-local-shadow>" 3)
                  Let("shadow", span "<native-value-stack-local-shadow>" 4)
                  Scope(
                    [ Push(LInt 99L, span "<native-value-stack-local-shadow>" 5)
                      Call("leaf.new", span "<native-value-stack-local-shadow>" 6)
                      Let("shadow", span "<native-value-stack-local-shadow>" 7)
                      Load("shadow", span "<native-value-stack-local-shadow>" 8)
                      Call("leaf.value", span "<native-value-stack-local-shadow>" 9)
                      Call("drop", span "<native-value-stack-local-shadow>" 10) ],
                    span "<native-value-stack-local-shadow>" 11)
                  Load("shadow", span "<native-value-stack-local-shadow>" 12) ]
                compiled.Context.SourceOrigins
          DirectDupDrop =
            Compiler.compileIrBodyAgainstProgramWithSourceOrigins
                compiled.Context.CompilerContext
                compiled.Program
                "native-value-stack-direct-dup-drop"
                [ TInt; TInt ]
                [ Call("mailbox.make-envelope", span "<native-value-stack-direct-dup-drop>" 1)
                  Call("dup", span "<native-value-stack-direct-dup-drop>" 2)
                  Call("drop", span "<native-value-stack-direct-dup-drop>" 3) ]
                compiled.Context.SourceOrigins
          FailAfterAllocation = compileEntry "mailbox.fail-after-allocation.entry" "mailbox.fail-after-allocation" [ TNamed "State"; TInt ] }
    let bodies =
        [ entries.Initialize; entries.TurnOne; entries.TurnEight; entries.Branch; entries.UnitValue; entries.UnitDrop; entries.EmptyValue; entries.ScopedWidth; entries.Depth64; entries.Depth65; entries.ScopeShadow; entries.DirectDupDrop; entries.FailAfterAllocation ]
    if not (VerifiedIrProgram.isBackendExecutable entries.Program) then
        invalidOp "Flow lowering did not produce a backend-authorized VerifiedIrProgram."
    if bodies |> List.exists (fun body -> not (Object.ReferenceEquals(VerifiedIrBody.program body, entries.Program))) then
        invalidOp "Every comparison body must share the exact VerifiedIrProgram instance."
    entries

let private noOpHost (sources: NativeDiagnosticSources) : IrInterpreterHost =
    { PreflightEffects = fun _ _ _ -> ()
      ChargeInstruction = fun _ _ -> ()
      RecordBranchOutcome = fun _ _ _ -> ()
      RecordUse = ignore
      InvokeEffect = fun _ -> EffectUnit
      EnterUserFunction = fun _ _ _ -> fun () -> ()
      ReturnUserFunction = fun _ _ _ -> ()
      WordDefinitionSpan = fun word -> sources.WordDefinitionSpans.TryFind word
      PrimitiveDefinitionSpan = fun word -> sources.PrimitiveDefinitionSpans.TryFind word }

let private hashBytes (bytes: byte array) =
    SHA256.HashData(bytes) |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

let private hashText (value: string) =
    hashBytes (Encoding.UTF8.GetBytes value)

let private hashFile (path: string) =
    File.ReadAllBytes path |> hashBytes

let private getProperty (target: obj) (name: string) =
    if isNull target then null
    else
        let property = target.GetType().GetProperty(name)
        if isNull property then null else property.GetValue target

let private int64Property target name =
    let value = getProperty target name
    if isNull value then invalidOp $"Expected metric property '{name}' on {target.GetType().FullName}."
    Convert.ToInt64(value, CultureInfo.InvariantCulture)

let private jsonNode (options: JsonSerializerOptions) (value: obj) =
    JsonNode.Parse(JsonSerializer.Serialize(value, options))

let private jsonObject properties =
    let result = Dictionary<string, obj>(StringComparer.Ordinal)
    for name, value in properties do result.Add(name, value)
    result

let private valueInt (element: JsonElement) (name: string) =
    element.GetProperty(name).GetInt64() |> IntValue

let private bytesFromInt64s (values: int64 list) =
    values
    |> List.collect (fun value -> BitConverter.GetBytes(value) |> Array.toList)
    |> List.toArray

let private bytesHex (bytes: byte array) = Convert.ToHexString(bytes).ToLowerInvariant()

let private bytesFromHex (value: string) = Convert.FromHexString value

let private layoutTypeNames = [ "Leaf"; "Envelope"; "State"; "Empty" ]

let private validateTypeLayouts (fixture: JsonElement) (typeNames: string list) (layouts: OwningStackTypeLayout list) =
    let checks = ResizeArray<obj>()
    let failures = ResizeArray<string>()
    let layoutOracle = fixture.GetProperty("layouts")
    for typeName in layoutTypeNames |> List.filter (fun typeName -> List.contains typeName typeNames) do
        match layouts |> List.tryFind (fun layout -> String.Equals(layout.TypeName, typeName, StringComparison.Ordinal)) with
        | None -> failures.Add($"Missing type layout for {typeName}.")
        | Some layout ->
            let expectedPayload = layoutOracle.GetProperty("payloadBytes").GetProperty(typeName).GetInt32()
            let expectedExtent = layoutOracle.GetProperty("stackExtentBytes").GetProperty(typeName).GetInt32()
            let fields =
                layoutOracle.GetProperty("declarationOrderFields").GetProperty(typeName).EnumerateArray()
                |> Seq.map (fun field -> field.GetString())
                |> Seq.toList
            checks.Add(box (jsonObject [ "type", box typeName; "expectedPayloadBytes", box expectedPayload; "actualPayloadBytes", box layout.PayloadBytes; "expectedStackExtentBytes", box expectedExtent; "actualStackExtentBytes", box layout.ExtentBytes ]))
            if layout.PayloadBytes <> expectedPayload || layout.ExtentBytes <> expectedExtent then failures.Add($"{typeName} expected payload and stack extent {expectedPayload}/{expectedExtent}, got {layout.PayloadBytes}/{layout.ExtentBytes}.")
            if layout.Fields.Length <> fields.Length then failures.Add($"{typeName} expected {fields.Length} declaration-order fields, got {layout.Fields.Length}.")
            for fieldName in fields do
                let fieldKey = $"{typeName}.{fieldName}"
                let expectedOffset = layoutOracle.GetProperty("fieldOffsets").GetProperty(fieldKey).GetInt32()
                let expectedFieldPayload = layoutOracle.GetProperty("fieldPayloadBytes").GetProperty(fieldKey).GetInt32()
                let expectedFieldExtent = layoutOracle.GetProperty("fieldExtentBytes").GetProperty(fieldKey).GetInt32()
                match layout.Fields |> List.tryFind (fun field -> String.Equals(field.FieldName, fieldName, StringComparison.Ordinal)) with
                | None ->
                    checks.Add(box (jsonObject [ "type", box typeName; "field", box fieldName; "present", box false ]))
                    failures.Add($"{typeName}.{fieldName} is absent from the physical layout.")
                | Some field ->
                    let contained = field.OffsetBytes >= 0 && field.ExtentBytes >= field.PayloadBytes && field.OffsetBytes + field.ExtentBytes <= layout.PayloadBytes
                    let passed = field.OffsetBytes = expectedOffset && field.PayloadBytes = expectedFieldPayload && field.ExtentBytes = expectedFieldExtent && contained
                    checks.Add(box (jsonObject [
                        "type", box typeName
                        "field", box fieldName
                        "expectedOffset", box expectedOffset
                        "actualOffset", box field.OffsetBytes
                        "expectedPayloadBytes", box expectedFieldPayload
                        "actualPayloadBytes", box field.PayloadBytes
                        "expectedExtentBytes", box expectedFieldExtent
                        "actualExtentBytes", box field.ExtentBytes
                        "containedInOwnerPayload", box contained ]))
                    if field.OffsetBytes <> expectedOffset then failures.Add($"{typeName}.{fieldName} expected offset {expectedOffset}, got {field.OffsetBytes}.")
                    if field.PayloadBytes <> expectedFieldPayload || field.ExtentBytes <> expectedFieldExtent then failures.Add($"{typeName}.{fieldName} expected payload/extent {expectedFieldPayload}/{expectedFieldExtent}, got {field.PayloadBytes}/{field.ExtentBytes}.")
                    if not contained then failures.Add($"{typeName}.{fieldName} extends outside its owning {typeName} payload.")
            let noInterFieldGaps =
                layout.Fields
                |> List.map (fun field -> int64 field.OffsetBytes, int64 field.ExtentBytes)
                |> List.pairwise
                |> List.forall (fun ((leftOffset, leftExtent), (rightOffset, _)) -> leftOffset + leftExtent = rightOffset)
            checks.Add(box (jsonObject [ "type", box typeName; "interFieldGapPresent", box (not noInterFieldGaps) ]))
            if not noInterFieldGaps then failures.Add($"{typeName} contains a gap between declaration-order fields.")
    let summary =
        layouts
        |> List.filter (fun layout -> typeNames |> List.contains layout.TypeName)
        |> List.map (fun layout ->
            box (jsonObject [
                "type", box layout.TypeName
                "payloadBytes", box layout.PayloadBytes
                "extentBytes", box layout.ExtentBytes
                "fields", box (layout.Fields |> List.map (fun field -> jsonObject [ "name", box field.FieldName; "offsetBytes", box field.OffsetBytes; "payloadBytes", box field.PayloadBytes; "extentBytes", box field.ExtentBytes ])) ]))
        |> List.toArray
    checks.ToArray(), failures.ToArray(), summary

let private layoutEventKind (event: OwningStackLayoutEvent) =
    if event.Kind = "duplicate" then 2L
    elif event.Kind = "drop" then 3L
    elif event.Kind = "call-input-move" then 8L
    elif event.Kind = "call-return-move" then 9L
    elif event.Kind = "retained-copy" then 10L
    elif event.Kind = "allocate" then 1L
    else -1L

let private layoutEventRanges (events: OwningStackLayoutEvent list) =
    events
    |> List.map (fun event ->
        {| Kind = layoutEventKind event
           Offset = int64 event.OffsetBytes
           Extent = int64 event.ExtentBytes
           Payload = int64 event.PayloadBytes
           SourceOffset = event.SourceOffsetBytes |> Option.map int64 |> Option.defaultValue -1L
           SourceExtent = event.SourceExtentBytes |> Option.map int64 |> Option.defaultValue -1L |})
    |> List.toArray

let private envelopeFromJson (element: JsonElement) =
    let leaf = element.GetProperty("leaf")
    RecordValue("Envelope", Map.ofList [ "leaf", RecordValue("Leaf", Map.ofList [ "value", valueInt leaf "value" ]); "tag", valueInt element "tag" ])

let private stateFromJson (element: JsonElement) =
    RecordValue("State", Map.ofList [ "count", valueInt element "count"; "last", envelopeFromJson (element.GetProperty("last")) ])

let private expectedValues (element: JsonElement) =
    let kind = element.GetProperty("type").GetString()
    match kind with
    | "State" -> [ stateFromJson element ]
    | "Envelope" -> [ envelopeFromJson element ]
    | "Unit" -> [ UnitValue ]
    | "Int" -> [ valueInt element "value" ]
    | "Empty" -> [ RecordValue("Empty", Map.empty) ]
    | other -> invalidOp $"Unsupported fixture result type '{other}'."

let private inspectScopeShadow (program: VerifiedIrProgram) (verifiedBody: VerifiedIrBody) =
    let programData = VerifiedIrProgram.inspect program
    let body = VerifiedIrBody.inspect verifiedBody
    let slot =
        body.BodyLocalNames
        |> Map.toSeq
        |> Seq.tryPick (fun (slot, name) -> if name = "shadow" then Some slot else None)
    let typeKey typeName =
        programData.NominalTypesByKey
        |> Map.toSeq
        |> Seq.tryPick (fun (key, definition) ->
            match definition with
            | IrRecordDefinition record when record.TypeName = typeName -> Some key
            | _ -> None)
    let typeName = function
        | Some(IrNominal key) ->
            match programData.NominalTypesByKey.TryFind key with
            | Some(IrRecordDefinition record) -> record.TypeName
            | _ -> "<unknown-nominal>"
        | Some other -> IrTypes.format other
        | None -> "<missing>"
    match slot with
    | None -> false, jsonObject [ "slotFound", box false ]
    | Some shadowSlot ->
        let scopeBlock =
            body.BodyBlock.Code
            |> List.tryPick (fun instruction -> match instruction.Operation with | IrOperation.Scope inner -> Some inner | _ -> None)
        match scopeBlock, typeKey "Envelope", typeKey "Leaf" with
        | Some inner, Some envelopeKey, Some leafKey ->
            let entryType = inner.EntryShape.LocalTypes.TryFind shadowSlot
            let innerType = inner.ExitShape.LocalTypes.TryFind shadowSlot
            let restoredType = body.BodyBlock.ExitShape.LocalTypes.TryFind shadowSlot
            let innerStoreUsesSlot = inner.Code |> List.exists (fun instruction -> match instruction.Operation with | IrOperation.StoreLocal slot -> slot = shadowSlot | _ -> false)
            let outerStoreUsesSlot = body.BodyBlock.Code |> List.exists (fun instruction -> match instruction.Operation with | IrOperation.StoreLocal slot -> slot = shadowSlot | _ -> false)
            let passed =
                innerStoreUsesSlot
                && outerStoreUsesSlot
                && entryType = Some(IrNominal envelopeKey)
                && innerType = Some(IrNominal leafKey)
                && restoredType = Some(IrNominal envelopeKey)
            passed, jsonObject [
                "slotFound", box true
                "slot", box (sprintf "%A" shadowSlot)
                "outerStoreUsesSameSlot", box outerStoreUsesSlot
                "innerStoreUsesSameSlot", box innerStoreUsesSlot
                "scopeEntryType", box (typeName entryType)
                "scopeInnerExitType", box (typeName innerType)
                "afterScopeType", box (typeName restoredType) ]
        | _ -> false, jsonObject [ "slotFound", box true; "scopeBlockFound", box scopeBlock.IsSome; "expectedTypeKeysFound", box (typeKey "Envelope" |> Option.isSome && typeKey "Leaf" |> Option.isSome) ]

let private diagnosticCode (error: exn) =
    match error with
    | LanguageException diagnostic -> diagnostic.Code
    | _ ->
        match getProperty error "Diagnostic" with
        | null -> ""
        | diagnostic -> getProperty diagnostic "Code" |> string

let private resourceExceptionDetails (error: exn) =
    jsonObject [
        "type", box (error.GetType().Name)
        "code", box (getProperty error "Code" |> string)
        "boundary", box (getProperty error "Boundary" |> string)
        "arena", box (getProperty error "Arena" |> string)
        "requiredBytes", box (getProperty error "RequiredBytes")
        "availableBytes", box (getProperty error "AvailableBytes")
        "requiredNodes", box (getProperty error "RequiredNodes")
        "availableNodes", box (getProperty error "AvailableNodes") ]

let private recordCheck (checks: ResizeArray<obj>) (failures: ResizeArray<string>) name passed details =
    checks.Add(box (jsonObject [ "name", box name; "passed", box passed; "details", box details ]))
    if not passed then failures.Add($"{name}: {details}")

let private addCommonEntryReports (report: Dictionary<string, obj>) sourceHash expandedSource turnHashes =
    report["source"] <- box (jsonObject [
        "flowFixtureSha256", box sourceHash
        "expandedSourceSha256", box (hashText expandedSource)
        "turnBodyHashes", box turnHashes ])

let private runInterpreterChain
    (checks: ResizeArray<obj>)
    (failures: ResizeArray<string>)
    (options: JsonSerializerOptions)
    (fixture: JsonElement)
    (entries: EntryBodies)
    (repetition: int)
    (turnBody: VerifiedIrBody) =
    let expectedInitial = expectedValues (fixture.GetProperty("initialState"))
    let turns = fixture.GetProperty("turns").EnumerateArray() |> Seq.toArray
    let interpreterHost = noOpHost (NativeDiagnosticSources.fromLoweringContext entries.CompilerContext)
    use initial = IrInterpreter.executeBodyWithInputs interpreterHost "mailbox.initialize" entries.Initialize None []
    recordCheck checks failures $"interpreter/N={repetition}/initialize" (initial.Decode() = expectedInitial) (ValueInspection.toJson entries.Program (initial.Decode()))
    let firstInput = turns[0].GetProperty("input").GetInt64()
    use first = IrInterpreter.executeBodyWithInputs interpreterHost "mailbox.turn" turnBody (Some initial) [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.IntArgument firstInput ]
    let firstValues = first.Decode()
    let expectedFirst = expectedValues (turns[0].GetProperty("expectedState"))
    recordCheck checks failures $"interpreter/N={repetition}/first-turn" (firstValues = expectedFirst) (ValueInspection.toJson entries.Program firstValues)
    let secondInput = turns[1].GetProperty("input").GetInt64()
    use second = IrInterpreter.executeBodyWithInputs interpreterHost "mailbox.turn" turnBody (Some first) [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.IntArgument secondInput ]
    let secondValues = second.Decode()
    let expectedSecond = expectedValues (turns[1].GetProperty("expectedState"))
    recordCheck checks failures $"interpreter/N={repetition}/second-turn" (secondValues = expectedSecond) (ValueInspection.toJson entries.Program secondValues)
    for (index, branch) in fixture.GetProperty("branches").EnumerateArray() |> Seq.indexed do
        let input = branch.GetProperty("value").GetInt64()
        let chooseLeft = branch.GetProperty("chooseLeft").GetBoolean()
        use actual = IrInterpreter.executeBodyWithInputs interpreterHost "mailbox.branch" entries.Branch None [ IrEntryArgument.IntArgument input; IrEntryArgument.BoolArgument chooseLeft ]
        let values = actual.Decode()
        let expected = expectedValues (branch.GetProperty("expected"))
        recordCheck checks failures $"interpreter/N={repetition}/branch-{index}" (values = expected) (ValueInspection.toJson entries.Program values)
    let directExpected = [ RecordValue("Envelope", Map.ofList [ "leaf", RecordValue("Leaf", Map.ofList [ "value", IntValue 17L ]); "tag", IntValue 1017L ]) ]
    use direct = IrInterpreter.executeBodyWithInputs interpreterHost "native-value-stack-direct-dup-drop" entries.DirectDupDrop None [ IrEntryArgument.IntArgument 17L; IrEntryArgument.IntArgument 1017L ]
    recordCheck checks failures $"interpreter/N={repetition}/direct-top-dup-drop" (direct.Decode() = directExpected) (ValueInspection.toJson entries.Program (direct.Decode()))
    let failureCode =
        try
            use _unexpected = IrInterpreter.executeBodyWithInputs interpreterHost "mailbox.fail-after-allocation" entries.FailAfterAllocation (Some initial) [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.IntArgument firstInput ]
            "unexpected-success"
        with error -> diagnosticCode error
    recordCheck checks failures $"interpreter/N={repetition}/error-after-allocation" (failureCode = "RUNTIME_DIVIDE_BY_ZERO" && initial.Decode() = expectedInitial) failureCode
    let fixedCaseReports = ResizeArray<obj>()
    if repetition = 1 then
        let fixedCases = fixture.GetProperty("fixedLayoutCases")
        let unitExpected = expectedValues (fixedCases.GetProperty("unit").GetProperty("expected"))
        use unitResult = IrInterpreter.executeBodyWithInputs interpreterHost "mailbox.unit-value" entries.UnitValue None []
        let unitValues = unitResult.Decode()
        recordCheck checks failures "interpreter/unit-fixed-layout-value" (unitValues = unitExpected) (ValueInspection.toJson entries.Program unitValues)
        fixedCaseReports.Add(box (jsonObject [ "case", box "unit"; "values", jsonNode options (ValueInspection.toData entries.Program unitValues) ]))
        let unitDropExpected = expectedValues (fixedCases.GetProperty("unitDrop").GetProperty("expected"))
        use unitDropResult = IrInterpreter.executeBodyWithInputs interpreterHost "native-value-stack-unit-drop" entries.UnitDrop None []
        let unitDropValues = unitDropResult.Decode()
        recordCheck checks failures "interpreter/unit-drop-releases-token-and-continues" (unitDropValues = unitDropExpected) (ValueInspection.toJson entries.Program unitDropValues)
        fixedCaseReports.Add(box (jsonObject [ "case", box "unit-drop"; "values", jsonNode options (ValueInspection.toData entries.Program unitDropValues) ]))
        let emptyExpected = expectedValues (fixedCases.GetProperty("emptyRecord").GetProperty("expected"))
        use emptyResult = IrInterpreter.executeBodyWithInputs interpreterHost "mailbox.empty-value" entries.EmptyValue None []
        let emptyValues = emptyResult.Decode()
        recordCheck checks failures "interpreter/empty-record-zero-field-value" (emptyValues = emptyExpected) (ValueInspection.toJson entries.Program emptyValues)
        fixedCaseReports.Add(box (jsonObject [ "case", box "empty-record"; "values", jsonNode options (ValueInspection.toData entries.Program emptyValues) ]))
        let scopedCases = fixture.GetProperty("scopedWidthCases").EnumerateArray() |> Seq.toArray
        for index, scopedCase in scopedCases |> Array.indexed do
            let input = scopedCase.GetProperty("input").GetInt64()
            let expected = expectedValues (scopedCase.GetProperty("expected"))
            use scoped = IrInterpreter.executeBodyWithInputs interpreterHost "mailbox.scoped-width" entries.ScopedWidth None [ IrEntryArgument.IntArgument input ]
            let values = scoped.Decode()
            recordCheck checks failures $"interpreter/scoped-width-{index}" (values = expected) (ValueInspection.toJson entries.Program values)
            fixedCaseReports.Add(box (jsonObject [ "input", box input; "values", jsonNode options (ValueInspection.toData entries.Program values) ]))
        let shadowCase = fixedCases.GetProperty("scopeShadow")
        let shadowExpected = expectedValues (shadowCase.GetProperty("expected"))
        use shadow = IrInterpreter.executeBodyWithInputs interpreterHost "native-value-stack-scope-local-shadow" entries.ScopeShadow None []
        let shadowValues = shadow.Decode()
        recordCheck checks failures "interpreter/scope-shadow-restores-outer-width" (shadowValues = shadowExpected) (ValueInspection.toJson entries.Program shadowValues)
        fixedCaseReports.Add(box (jsonObject [ "case", box "scope-shadow"; "values", jsonNode options (ValueInspection.toData entries.Program shadowValues) ]))
        let depthExpected = [ IntValue 42L ]
        use depth64 = IrInterpreter.executeBodyWithInputs interpreterHost "mailbox.depth-64" entries.Depth64 None [ IrEntryArgument.IntArgument 42L ]
        let depth64Values = depth64.Decode()
        recordCheck checks failures "interpreter/call-depth-64-succeeds" (depth64Values = depthExpected) (ValueInspection.toJson entries.Program depth64Values)
        let depth65Code =
            try
                use _unexpected = IrInterpreter.executeBodyWithInputs interpreterHost "mailbox.depth-65" entries.Depth65 None [ IrEntryArgument.IntArgument 42L ]
                "unexpected-success"
            with error -> diagnosticCode error
        recordCheck checks failures "interpreter/call-depth-65-diagnostic" (depth65Code = "RUNTIME_CALL_DEPTH") depth65Code
    jsonObject [
        "backend", box "interpreter"
        "repetitions", box repetition
        "afterTurn1", jsonNode options (ValueInspection.toData entries.Program firstValues)
        "afterTurn2", jsonNode options (ValueInspection.toData entries.Program secondValues)
        "failureAfterAllocation", box failureCode
        "fixedLayoutCases", box (fixedCaseReports.ToArray()) ]

let private nativeOptions scratchBytes scratchNodes retainedBytes retainedNodes =
    { NativeExecutionOptions.defaults with
        ScratchByteCapacity = Some scratchBytes
        ScratchNodeCapacity = Some scratchNodes
        RetainedByteCapacity = Some retainedBytes
        RetainedNodeCapacity = Some retainedNodes }

let private controlScratchBytes repetition = 24 * repetition + 80
let private controlScratchNodes repetition = 2 * repetition + 6

let private runNativeChain
    (checks: ResizeArray<obj>)
    (failures: ResizeArray<string>)
    (options: JsonSerializerOptions)
    (artifactRoot: string)
    (optimization: LlvmOptimization)
    (fixture: JsonElement)
    (entries: EntryBodies)
    (repetition: int)
    (turnBody: VerifiedIrBody) =
    let nativeSources = NativeDiagnosticSources.fromLoweringContext entries.CompilerContext
    let toolchain = LlvmToolchain.discover ()
    let compile name body =
        LlvmAot.compile toolchain optimization (Path.Combine(artifactRoot, "abi3", string optimization, name)) nativeSources body
    use initialProgram = compile "initialize" entries.Initialize
    use turnProgram = compile $"turn-{repetition}" turnBody
    use branchProgram = compile "branch" entries.Branch
    use directProgram = compile "direct-dup-drop" entries.DirectDupDrop
    use failProgram = compile "fail-after-allocation" entries.FailAfterAllocation
    let expectedInitial = expectedValues (fixture.GetProperty("initialState"))
    let scratchBytes = controlScratchBytes repetition
    let scratchNodes = controlScratchNodes repetition
    let retainedBytes = 40
    let retainedNodes = 3
    use initial = initialProgram.ExecuteRetained("mailbox.initialize", options = nativeOptions 40 3 retainedBytes retainedNodes)
    let initialValues = initial.Decode()
    recordCheck checks failures $"abi3/{optimization}/N={repetition}/initialize" (initialValues = expectedInitial) (ValueInspection.toJson entries.Program initialValues)
    let turns = fixture.GetProperty("turns").EnumerateArray() |> Seq.toArray
    let firstInput = turns[0].GetProperty("input").GetInt64()
    use first = turnProgram.ExecuteRetainedWithInputs(
        "mailbox.turn",
        Some initial,
        [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.IntArgument firstInput ],
        options = nativeOptions scratchBytes scratchNodes retainedBytes retainedNodes)
    let firstValues = first.Decode()
    let expectedFirst = expectedValues (turns[0].GetProperty("expectedState"))
    recordCheck checks failures $"abi3/{optimization}/N={repetition}/turn-1" (firstValues = expectedFirst) (ValueInspection.toJson entries.Program firstValues)
    let secondInput = turns[1].GetProperty("input").GetInt64()
    use second = turnProgram.ExecuteRetainedWithInputs(
        "mailbox.turn",
        Some first,
        [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.IntArgument secondInput ],
        options = nativeOptions scratchBytes scratchNodes retainedBytes retainedNodes)
    let secondValues = second.Decode()
    let expectedSecond = expectedValues (turns[1].GetProperty("expectedState"))
    recordCheck checks failures $"abi3/{optimization}/N={repetition}/turn-2" (secondValues = expectedSecond) (ValueInspection.toJson entries.Program secondValues)
    let failedScratch =
        try
            use _unexpected = turnProgram.ExecuteRetainedWithInputs(
                "mailbox.turn-capacity",
                Some initial,
                [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.IntArgument firstInput ],
                options = nativeOptions (scratchBytes - 1) scratchNodes retainedBytes retainedNodes)
            None
        with :? NativeResourceLimitException as error -> Some error
    let priorUnchanged = initial.Decode() = expectedInitial
    match failedScratch with
    | Some error ->
        let capacityPassed = error.Code = "NATIVE_SCRATCH_CAPACITY" && error.Arena = "scratch" && error.RequiredBytes = int64 scratchBytes && error.AvailableBytes = scratchBytes - 1
        recordCheck checks failures $"abi3/{optimization}/N={repetition}/scratch-capacity-boundary" capacityPassed (resourceExceptionDetails error)
        recordCheck checks failures $"abi3/{optimization}/N={repetition}/scratch-failure-preserves-owner" priorUnchanged (ValueInspection.toJson entries.Program (initial.Decode()))
    | None ->
        recordCheck checks failures $"abi3/{optimization}/N={repetition}/scratch-capacity-boundary" false "Scratch capacity one byte below the independent threshold unexpectedly succeeded."
    let failCode =
        try
            use _unexpected = failProgram.ExecuteRetainedWithInputs(
                "mailbox.fail-after-allocation",
                Some initial,
                [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.IntArgument firstInput ],
                options = nativeOptions 64 5 retainedBytes retainedNodes)
            "unexpected-success"
        with error -> diagnosticCode error
    recordCheck checks failures $"abi3/{optimization}/N={repetition}/error-after-allocation" (failCode = "RUNTIME_DIVIDE_BY_ZERO" && initial.Decode() = expectedInitial) failCode
    let directExpected = [ RecordValue("Envelope", Map.ofList [ "leaf", RecordValue("Leaf", Map.ofList [ "value", IntValue 17L ]); "tag", IntValue 1017L ]) ]
    use direct = directProgram.ExecuteRetainedWithInputs(
        "native-value-stack-direct-dup-drop",
        None,
        [ IrEntryArgument.IntArgument 17L; IrEntryArgument.IntArgument 1017L ],
        options = nativeOptions 24 2 24 2)
    let directValues = direct.Decode()
    recordCheck checks failures $"abi3/{optimization}/N={repetition}/direct-top-dup-drop" (directValues = directExpected) (ValueInspection.toJson entries.Program directValues)
    let branchReports =
        fixture.GetProperty("branches").EnumerateArray()
        |> Seq.mapi (fun index branch ->
            let input = branch.GetProperty("value").GetInt64()
            let chooseLeft = branch.GetProperty("chooseLeft").GetBoolean()
            let expected = expectedValues (branch.GetProperty("expected"))
            use result = branchProgram.ExecuteRetainedWithInputs(
                "mailbox.branch",
                None,
                [ IrEntryArgument.IntArgument input; IrEntryArgument.BoolArgument chooseLeft ],
                options = nativeOptions 24 2 24 2)
            let values = result.Decode()
            recordCheck checks failures $"abi3/{optimization}/N={repetition}/branch-{index}" (values = expected) (ValueInspection.toJson entries.Program values)
            box (jsonObject [ "chooseLeft", box chooseLeft; "values", jsonNode options (ValueInspection.toData entries.Program values) ]))
        |> Seq.toArray
    let fixedCaseReports = ResizeArray<obj>()
    if repetition = 1 then
        let fixedCases = fixture.GetProperty("fixedLayoutCases")
        use unitProgram = compile "unit-value" entries.UnitValue
        use unitDropProgram = compile "unit-drop" entries.UnitDrop
        use emptyProgram = compile "empty-value" entries.EmptyValue
        use scopedProgram = compile "scoped-width" entries.ScopedWidth
        use scopeShadowProgram = compile "scope-shadow" entries.ScopeShadow
        use depth64Program = compile "depth-64" entries.Depth64
        use depth65Program = compile "depth-65" entries.Depth65
        use unitResult = unitProgram.ExecuteRetained("mailbox.unit-value", options = nativeOptions 8 1 8 1)
        let unitValues = unitResult.Decode()
        let unitExpected = expectedValues (fixedCases.GetProperty("unit").GetProperty("expected"))
        recordCheck checks failures $"abi3/{optimization}/unit-fixed-layout-value" (unitValues = unitExpected) (ValueInspection.toJson entries.Program unitValues)
        fixedCaseReports.Add(box (jsonObject [ "case", box "unit"; "values", jsonNode options (ValueInspection.toData entries.Program unitValues); "abi3RetainedBytes", box unitResult.RetainedByteCount; "abi3RetainedNodes", box unitResult.RetainedNodeCount ]))
        use unitDropResult = unitDropProgram.ExecuteRetained("native-value-stack-unit-drop", options = nativeOptions 16 2 8 1)
        let unitDropValues = unitDropResult.Decode()
        let unitDropExpected = expectedValues (fixedCases.GetProperty("unitDrop").GetProperty("expected"))
        recordCheck checks failures $"abi3/{optimization}/unit-drop-releases-token-and-continues" (unitDropValues = unitDropExpected) (ValueInspection.toJson entries.Program unitDropValues)
        fixedCaseReports.Add(box (jsonObject [ "case", box "unit-drop"; "values", jsonNode options (ValueInspection.toData entries.Program unitDropValues); "abi3RetainedBytes", box unitDropResult.RetainedByteCount; "abi3RetainedNodes", box unitDropResult.RetainedNodeCount ]))
        use emptyResult = emptyProgram.ExecuteRetained("mailbox.empty-value", options = nativeOptions 8 1 8 1)
        let emptyValues = emptyResult.Decode()
        let emptyExpected = expectedValues (fixedCases.GetProperty("emptyRecord").GetProperty("expected"))
        recordCheck checks failures $"abi3/{optimization}/empty-record-zero-field-value" (emptyValues = emptyExpected) (ValueInspection.toJson entries.Program emptyValues)
        fixedCaseReports.Add(box (jsonObject [ "case", box "empty-record"; "values", jsonNode options (ValueInspection.toData entries.Program emptyValues); "abi3RetainedBytes", box emptyResult.RetainedByteCount; "abi3RetainedNodes", box emptyResult.RetainedNodeCount ]))
        for index, scopedCase in fixture.GetProperty("scopedWidthCases").EnumerateArray() |> Seq.indexed do
            let input = scopedCase.GetProperty("input").GetInt64()
            let expected = expectedValues (scopedCase.GetProperty("expected"))
            use scopedResult = scopedProgram.ExecuteRetainedWithInputs(
                "mailbox.scoped-width",
                None,
                [ IrEntryArgument.IntArgument input ],
                options = nativeOptions 128 16 24 2)
            let values = scopedResult.Decode()
            recordCheck checks failures $"abi3/{optimization}/scoped-width-{index}" (values = expected) (ValueInspection.toJson entries.Program values)
            fixedCaseReports.Add(box (jsonObject [ "case", box "scoped-width"; "input", box input; "values", jsonNode options (ValueInspection.toData entries.Program values); "abi3RetainedBytes", box scopedResult.RetainedByteCount; "abi3RetainedNodes", box scopedResult.RetainedNodeCount ]))
        let shadowCase = fixedCases.GetProperty("scopeShadow")
        let shadowExpected = expectedValues (shadowCase.GetProperty("expected"))
        use shadowResult = scopeShadowProgram.ExecuteRetained("native-value-stack-scope-local-shadow", options = nativeOptions 256 32 24 2)
        let shadowValues = shadowResult.Decode()
        recordCheck checks failures $"abi3/{optimization}/scope-shadow-restores-outer-width" (shadowValues = shadowExpected) (ValueInspection.toJson entries.Program shadowValues)
        fixedCaseReports.Add(box (jsonObject [ "case", box "scope-shadow"; "values", jsonNode options (ValueInspection.toData entries.Program shadowValues); "abi3RetainedBytes", box shadowResult.RetainedByteCount; "abi3RetainedNodes", box shadowResult.RetainedNodeCount ]))
        use depth64Result = depth64Program.ExecuteRetainedWithInputs("mailbox.depth-64", None, [ IrEntryArgument.IntArgument 42L ], options = nativeOptions 65536 4096 8 1)
        let depth64Values = depth64Result.Decode()
        recordCheck checks failures $"abi3/{optimization}/call-depth-64-succeeds" (depth64Values = [ IntValue 42L ]) (ValueInspection.toJson entries.Program depth64Values)
        let depth65Code =
            try
                use _unexpected = depth65Program.ExecuteRetainedWithInputs("mailbox.depth-65", None, [ IrEntryArgument.IntArgument 42L ], options = nativeOptions 65536 4096 8 1)
                "unexpected-success"
            with error -> diagnosticCode error
        recordCheck checks failures $"abi3/{optimization}/call-depth-65-diagnostic" (depth65Code = "RUNTIME_CALL_DEPTH") depth65Code
    jsonObject [
        "backend", box $"abi3-sharedgraph/{optimization}"
        "repetitions", box repetition
        "minimumScratchBytes", box scratchBytes
        "minimumScratchNodes", box scratchNodes
        "retainedBytesAfterTurn1", box first.RetainedByteCount
        "retainedNodesAfterTurn1", box first.RetainedNodeCount
        "afterTurn1", jsonNode options (ValueInspection.toData entries.Program firstValues)
        "afterTurn2", jsonNode options (ValueInspection.toData entries.Program secondValues)
        "directDupDrop", jsonNode options (ValueInspection.toData entries.Program directValues)
        "branchRuns", box branchReports
        "failureAfterAllocation", box failCode
        "fixedLayoutCases", box (fixedCaseReports.ToArray()) ]

let private stackMetrics (result: obj) = getProperty result "Metrics"

let private metricSummary (metrics: obj) =
    let boolean name =
        let value = getProperty metrics name
        if isNull value then invalidOp $"Expected metric property '{name}'."
        Convert.ToBoolean(value, CultureInfo.InvariantCulture)
    jsonObject [
        "stackCapacityBytes", box (int64Property metrics "StackCapacityBytes")
        "peakLiveStackBytes", box (int64Property metrics "PeakLiveStackBytes")
        "reservedStackBytes", box (int64Property metrics "ReservedStackBytes")
        "peakLiveLocalBytes", box (int64Property metrics "PeakLiveLocalBytes")
        "reservedLocalBytes", box (int64Property metrics "ReservedLocalBytes")
        "inputBytes", box (int64Property metrics "InputBytes")
        "inputCopyBytes", box (int64Property metrics "InputCopyBytes")
        "retainedCapacityBytes", box (int64Property metrics "RetainedCapacityBytes")
        "hostRetainedStagingBytes", box (int64Property metrics "HostRetainedStagingBytes")
        "hostRetainedCommitBytes", box (int64Property metrics "HostRetainedCommitBytes")
        "deepCopyBytes", box (int64Property metrics "DeepCopyBytes")
        "moveBytes", box (int64Property metrics "MoveBytes")
        "retainedCopyBytes", box (int64Property metrics "RetainedCopyBytes")
        "cursorInvariantChecks", box (int64Property metrics "CursorInvariantChecks")
        "frameReturnCount", box (int64Property metrics "FrameReturnCount")
        "duplicateDisjointChecks", box (int64Property metrics "DuplicateDisjointChecks")
        "dropSurvivorChecks", box (int64Property metrics "DropSurvivorChecks")
        "poisonReuseChecks", box (int64Property metrics "PoisonReuseChecks")
        "traceEventCount", box (int64Property metrics "TraceEventCount")
        "traceEventCapacity", box (int64Property metrics "TraceEventCapacity")
        "traceEventTruncated", box (boolean "TraceTruncated")
        "instrumentationReservedBytes", box (int64Property metrics "InstrumentationReservedBytes")
        "finalCursorBytes", box (int64Property metrics "FinalCursorBytes")
        "finalLiveStackBytes", box (int64Property metrics "FinalLiveStackBytes") ]

let private checkTraceUsable (checks: ResizeArray<obj>) (failures: ResizeArray<string>) name (metrics: obj) (events: OwningStackLayoutEvent list) =
    let eventCount = events.Length
    let traceCount = int64Property metrics "TraceEventCount"
    let traceCapacity = int64Property metrics "TraceEventCapacity"
    let traceTruncated = Convert.ToBoolean(getProperty metrics "TraceTruncated", CultureInfo.InvariantCulture)
    let passed = not traceTruncated && eventCount >= 0 && int64 eventCount = traceCount && traceCount <= traceCapacity
    recordCheck checks failures name passed (jsonObject [
        "eventArrayCount", box eventCount
        "traceEventCount", box traceCount
        "traceEventCapacity", box traceCapacity
        "traceEventTruncated", box traceTruncated ])
    passed

let private stateBytesFromJson (state: JsonElement) =
    let last = state.GetProperty("last")
    let leafValue = last.GetProperty("leaf").GetProperty("value").GetInt64()
    bytesFromInt64s [ state.GetProperty("count").GetInt64(); leafValue; last.GetProperty("tag").GetInt64() ]

let private int64FromBytesLittleEndian (bytes: byte array) offset =
    if offset < 0 || offset + 8 > bytes.Length then invalidArg (nameof bytes) "A fixed scalar read exceeds the retained byte buffer."
    let mutable bits = 0UL
    for index in 0 .. 7 do bits <- bits ||| (uint64 bytes[offset + index] <<< (index * 8))
    int64 bits

let private stateFromRetainedBytes (bytes: byte array) =
    if bytes.Length <> 24 then invalidArg (nameof bytes) "State fixture bytes must have the pinned 24-byte layout."
    RecordValue("State", Map.ofList [
        "count", IntValue(int64FromBytesLittleEndian bytes 0)
        "last", RecordValue("Envelope", Map.ofList [
            "leaf", RecordValue("Leaf", Map.ofList [ "value", IntValue(int64FromBytesLittleEndian bytes 8) ])
            "tag", IntValue(int64FromBytesLittleEndian bytes 16) ]) ])

let private envelopeBytesFromJson (envelope: JsonElement) =
    bytesFromInt64s [ envelope.GetProperty("leaf").GetProperty("value").GetInt64(); envelope.GetProperty("tag").GetInt64() ]

let private bufferCheckDetails (expected: byte array) (actual: byte array) =
    jsonObject [ "unchanged", box (expected = actual); "expectedHex", box (bytesHex expected); "actualHex", box (bytesHex actual); "lengthBytes", box actual.Length ]

let private runOwningStackChain
    (checks: ResizeArray<obj>)
    (failures: ResizeArray<string>)
    (options: JsonSerializerOptions)
    (artifactRoot: string)
    (optimization: LlvmOptimization)
    (fixture: JsonElement)
    (entries: EntryBodies)
    (repetition: int)
    (turnBody: VerifiedIrBody)
    (candidateSummaries: Dictionary<string, obj>) =
    let toolchain = LlvmToolchain.discover ()
    let compile name body =
        OwningStackAot.compile toolchain optimization (Path.Combine(artifactRoot, "owning-stack", string optimization, name)) body
    use initialProgram = compile "initialize" entries.Initialize
    use turnProgram = compile $"turn-{repetition}" turnBody
    use branchProgram = compile "branch" entries.Branch
    use directProgram = compile "direct-dup-drop" entries.DirectDupDrop
    use failProgram = compile "fail-after-allocation" entries.FailAfterAllocation
    let stackCapacity = 512
    let stateCapacity = 24
    let initialElement = fixture.GetProperty("initialState")
    let expectedInitial = expectedValues initialElement
    let oldRetainedState = bytesFromHex (fixture.GetProperty("initialRetainedBytesHex").GetString())
    recordCheck checks failures $"fixture/N={repetition}/initial-little-endian-layout-bytes" (stateBytesFromJson initialElement = oldRetainedState) (jsonObject [
        "fixtureHex", box (bytesHex oldRetainedState)
        "independentValueEncodingHex", box (bytesHex (stateBytesFromJson initialElement)) ])
    if not BitConverter.IsLittleEndian then invalidOp "The candidate ABI fixture currently pins little-endian Int64 bytes."
    let initialOutput = Array.create stateCapacity 0xA5uy
    let initialResult = initialProgram.ExecuteInto([], stackCapacity, initialOutput)
    let initialValues = getProperty (box initialResult) "Values" :?> Value list
    let initialMetrics = stackMetrics (box initialResult)
    recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/initialize" (initialValues = expectedInitial) (ValueInspection.toJson entries.Program initialValues)
    recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/initialize-retained-bytes" (initialOutput = oldRetainedState) (bufferCheckDetails oldRetainedState initialOutput)
    let turns = fixture.GetProperty("turns").EnumerateArray() |> Seq.toArray
    let firstInput = turns[0].GetProperty("input").GetInt64()
    let firstOutput = Array.create stateCapacity 0xA5uy
    let firstResult = turnProgram.ExecuteInto(initialValues @ [ IntValue firstInput ], stackCapacity, firstOutput)
    let firstValues = getProperty (box firstResult) "Values" :?> Value list
    let firstMetrics = stackMetrics (box firstResult)
    let expectedFirstElement = turns[0].GetProperty("expectedState")
    let expectedFirst = expectedValues expectedFirstElement
    let expectedFirstBytes = bytesFromHex (turns[0].GetProperty("retainedBytesHex").GetString())
    recordCheck checks failures $"fixture/N={repetition}/turn-1-little-endian-layout-bytes" (stateBytesFromJson expectedFirstElement = expectedFirstBytes) (jsonObject [
        "fixtureHex", box (bytesHex expectedFirstBytes)
        "independentValueEncodingHex", box (bytesHex (stateBytesFromJson expectedFirstElement)) ])
    recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/turn-1" (firstValues = expectedFirst) (ValueInspection.toJson entries.Program firstValues)
    recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/turn-1-retained-bytes" (firstOutput = expectedFirstBytes) (bufferCheckDetails expectedFirstBytes firstOutput)
    let firstPublishedState = stateFromRetainedBytes firstOutput
    recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/turn-1-raw-publication-decodes-to-next-input" (firstValues = [ firstPublishedState ]) (jsonObject [
        "decodedFromRawRetainedHex", box (bytesHex firstOutput)
        "decodedValue", ValueInspection.toJson entries.Program [ firstPublishedState ] ])
    let secondInput = turns[1].GetProperty("input").GetInt64()
    let secondOutput = Array.create stateCapacity 0xA5uy
    let secondResult = turnProgram.ExecuteInto([ firstPublishedState; IntValue secondInput ], stackCapacity, secondOutput)
    let secondValues = getProperty (box secondResult) "Values" :?> Value list
    let secondMetrics = stackMetrics (box secondResult)
    let expectedSecondElement = turns[1].GetProperty("expectedState")
    let expectedSecond = expectedValues expectedSecondElement
    let expectedSecondBytes = bytesFromHex (turns[1].GetProperty("retainedBytesHex").GetString())
    recordCheck checks failures $"fixture/N={repetition}/turn-2-little-endian-layout-bytes" (stateBytesFromJson expectedSecondElement = expectedSecondBytes) (jsonObject [
        "fixtureHex", box (bytesHex expectedSecondBytes)
        "independentValueEncodingHex", box (bytesHex (stateBytesFromJson expectedSecondElement)) ])
    recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/turn-2" (secondValues = expectedSecond) (ValueInspection.toJson entries.Program secondValues)
    recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/turn-2-retained-bytes" (secondOutput = expectedSecondBytes) (bufferCheckDetails expectedSecondBytes secondOutput)
    let retainedBefore = Array.copy oldRetainedState
    let retainedBuffer = Array.copy retainedBefore
    let retainedCapacityFailure =
        try
            turnProgram.ExecuteInto(initialValues @ [ IntValue firstInput ], stackCapacity, retainedBuffer, 23) |> ignore
            None
        with error -> Some error
    match retainedCapacityFailure with
    | Some error ->
        let code = getProperty error "Code" |> string
        let boundary = getProperty error "Boundary" |> string
        let required = int64Property error "RequiredBytes"
        let available = int64Property error "AvailableBytes"
        let metrics = getProperty error "Metrics"
        let finalLive = int64Property metrics "FinalLiveStackBytes"
        let finalCursor = int64Property metrics "FinalCursorBytes"
        let unchanged = retainedBuffer = retainedBefore
        let passed = code = "OWNING_RETAINED_CAPACITY" && boundary = "retained-output" && required = 24L && available = 23L && finalLive = 0L && finalCursor = 0L && unchanged
        recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/retained-capacity-buffer-atomicity" passed (jsonObject [
            "exception", box (resourceExceptionDetails error)
            "finalLiveStackBytes", box finalLive
            "finalCursorBytes", box finalCursor
            "callerBuffer", bufferCheckDetails retainedBefore retainedBuffer ])
    | None -> recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/retained-capacity-buffer-atomicity" false "Retained output capacity one byte below State's inline payload unexpectedly succeeded."
    let stackFailureBuffer = Array.copy oldRetainedState
    let stackFailureBefore = Array.copy stackFailureBuffer
    let stackCapacityFailure =
        try
            turnProgram.ExecuteInto(initialValues @ [ IntValue firstInput ], 1, stackFailureBuffer) |> ignore
            None
        with error -> Some error
    match stackCapacityFailure with
    | Some error ->
        let code = getProperty error "Code" |> string
        let boundary = getProperty error "Boundary" |> string
        let finalLive = int64Property (getProperty error "Metrics") "FinalLiveStackBytes"
        let finalCursor = int64Property (getProperty error "Metrics") "FinalCursorBytes"
        let unchanged = stackFailureBuffer = stackFailureBefore
        let passed = code = "OWNING_STACK_CAPACITY" && boundary = "program-data-stack" && finalLive = 0L && finalCursor = 0L && unchanged
        recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/stack-capacity-buffer-atomicity" passed (jsonObject [
            "exception", box (resourceExceptionDetails error)
            "finalLiveStackBytes", box finalLive
            "finalCursorBytes", box finalCursor
            "callerBuffer", bufferCheckDetails stackFailureBefore stackFailureBuffer ])
    | None -> recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/stack-capacity-buffer-atomicity" false "One-byte program stack capacity unexpectedly succeeded."
    let diagnosticBuffer = Array.copy oldRetainedState
    let diagnosticBefore = Array.copy diagnosticBuffer
    let diagnostic, diagnosticMetrics =
        try
            failProgram.ExecuteInto(initialValues @ [ IntValue firstInput ], stackCapacity, diagnosticBuffer) |> ignore
            "unexpected-success", null
        with error -> diagnosticCode error, getProperty error "Metrics"
    let diagnosticFinalLive = if isNull diagnosticMetrics then -1L else int64Property diagnosticMetrics "FinalLiveStackBytes"
    let diagnosticFinalCursor = if isNull diagnosticMetrics then -1L else int64Property diagnosticMetrics "FinalCursorBytes"
    let diagnosticBufferUnchanged = diagnosticBuffer = diagnosticBefore
    recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/error-after-allocation-buffer-atomicity" (diagnostic = "RUNTIME_DIVIDE_BY_ZERO" && diagnosticFinalLive = 0L && diagnosticFinalCursor = 0L && diagnosticBufferUnchanged) (jsonObject [
        "code", box diagnostic
        "finalLiveStackBytes", box diagnosticFinalLive
        "finalCursorBytes", box diagnosticFinalCursor
        "callerBuffer", bufferCheckDetails diagnosticBefore diagnosticBuffer ])
    let directOutput = Array.create 16 0xA5uy
    let directResult = directProgram.ExecuteInto([ IntValue 17L; IntValue 1017L ], stackCapacity, directOutput)
    let directValues = getProperty (box directResult) "Values" :?> Value list
    let directExpected = [ RecordValue("Envelope", Map.ofList [ "leaf", RecordValue("Leaf", Map.ofList [ "value", IntValue 17L ]); "tag", IntValue 1017L ]) ]
    let directMetrics = stackMetrics (box directResult)
    let directEvents = getProperty (box directResult) "LayoutEvents" :?> OwningStackLayoutEvent list
    let directRanges = layoutEventRanges directEvents
    let duplicateEvents = directRanges |> Array.filter (fun event -> event.Kind = 2L)
    let dropEvents = directRanges |> Array.filter (fun event -> event.Kind = 3L)
    let callReturnEvents = directRanges |> Array.filter (fun event -> event.Kind = 9L)
    let retainedEvents = directRanges |> Array.filter (fun event -> event.Kind = 10L)
    let droppedDuplicates =
        duplicateEvents
        |> Array.collect (fun duplicate ->
            dropEvents
            |> Array.filter (fun dropped -> dropped.Offset = duplicate.Offset && dropped.Extent = duplicate.Extent && dropped.Payload = duplicate.Payload))
    let survivorReturnEvents =
        match duplicateEvents, retainedEvents with
        | [| duplicate |], [| retained |] ->
            callReturnEvents
            |> Array.filter (fun transfer ->
                transfer.Offset = retained.SourceOffset
                && transfer.Extent = duplicate.SourceExtent
                && transfer.Payload = duplicate.Payload
                && transfer.SourceOffset = duplicate.SourceOffset
                && transfer.SourceExtent = duplicate.SourceExtent)
        | _ -> [||]
    let duplicateIndex =
        directRanges |> Array.tryFindIndex (fun event -> event.Kind = 2L) |> Option.defaultValue -1
    let droppedDuplicateIndex =
        directRanges
        |> Array.tryFindIndex (fun event ->
            duplicateEvents.Length = 1
            && event.Kind = 3L
            && event.Offset = duplicateEvents[0].Offset
            && event.Extent = duplicateEvents[0].Extent)
        |> Option.defaultValue -1
    let survivorReturnIndex =
        directRanges
        |> Array.tryFindIndex (fun event -> event.Kind = 9L && Array.contains event survivorReturnEvents)
        |> Option.defaultValue -1
    let retainedCopyIndex = directRanges |> Array.tryFindIndex (fun event -> event.Kind = 10L) |> Option.defaultValue -1
    let directDuplicateRangesPass =
        duplicateEvents.Length = 1
        && droppedDuplicates.Length = 1
        && retainedEvents.Length = 1
        && duplicateIndex >= 0
        && duplicateEvents[0].Extent = 16L
        && duplicateEvents[0].Payload = 16L
        && duplicateEvents[0].SourceExtent = 16L
        && (duplicateEvents[0].Offset + duplicateEvents[0].Extent <= duplicateEvents[0].SourceOffset
            || duplicateEvents[0].SourceOffset + duplicateEvents[0].SourceExtent <= duplicateEvents[0].Offset)
        && droppedDuplicates[0].Offset = duplicateEvents[0].Offset
        && droppedDuplicates[0].Extent = duplicateEvents[0].Extent
        && duplicateIndex < droppedDuplicateIndex
        && survivorReturnEvents.Length = 1
        && survivorReturnIndex > droppedDuplicateIndex
        && retainedCopyIndex > survivorReturnIndex
        && survivorReturnEvents[0].Offset = retainedEvents[0].SourceOffset
        && survivorReturnEvents[0].SourceOffset = duplicateEvents[0].SourceOffset
        && survivorReturnEvents[0].SourceExtent = duplicateEvents[0].SourceExtent
        && retainedEvents[0].SourceOffset = survivorReturnEvents[0].Offset
        && retainedEvents[0].SourceExtent = duplicateEvents[0].SourceExtent
        && retainedEvents[0].Payload = 16L
    let directLayouts = getProperty (box directResult) "Layouts" :?> OwningStackTypeLayout list
    recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/direct-top-dup-drop" (directValues = directExpected) (ValueInspection.toJson entries.Program directValues)
    let directExpectedBytes = bytesFromHex (fixture.GetProperty("directTopDupDrop").GetProperty("retainedBytesHex").GetString())
    recordCheck checks failures $"fixture/N={repetition}/direct-Envelope-little-endian-layout-bytes" (directExpectedBytes = bytesFromInt64s [ 17L; 1017L ]) (box (bytesHex directExpectedBytes))
    recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/direct-retained-bytes" (directOutput = directExpectedBytes) (bufferCheckDetails directExpectedBytes directOutput)
    let directTraceComplete = checkTraceUsable checks failures $"owning-stack/{optimization}/N={repetition}/direct-trace-complete" directMetrics directEvents
    recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/direct-physical-duplicate-drop-return-ranges" (directDuplicateRangesPass && directTraceComplete) (jsonObject [
        "duplicateEvents", box duplicateEvents
        "dropEvents", box dropEvents
        "matchingDroppedDuplicateEvents", box droppedDuplicates
        "survivorReturnMoveEvents", box survivorReturnEvents
        "retainedCopyEvents", box retainedEvents ])
    let allocatedScalars = directRanges |> Array.filter (fun event -> event.Kind = 1L && event.Extent = 8L && event.Payload = 8L) |> Array.sortBy (fun event -> event.Offset)
    let adjacentScalars = allocatedScalars |> Array.pairwise |> Array.exists (fun (left, right) -> left.Offset + left.Extent = right.Offset)
    recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/adjacent-inline-scalar-ranges" adjacentScalars (box allocatedScalars)
    let directDeepCopyBytes = int64Property directMetrics "DeepCopyBytes"
    recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/explicit-Envelope-dup-copy-bytes" (directDeepCopyBytes >= 16L) (jsonObject [ "expectedExplicitCopyBytes", box 16; "measuredDeepCopyBytes", box directDeepCopyBytes ])
    let directReserved = int64Property directMetrics "ReservedStackBytes"
    let directLive = int64Property directMetrics "PeakLiveStackBytes"
    let directReservedLocal = int64Property directMetrics "ReservedLocalBytes"
    let directLiveLocal = int64Property directMetrics "PeakLiveLocalBytes"
    recordCheck checks failures $"owning-stack/{optimization}/direct-live-reserved-byte-categories" (directReserved >= directLive && directReservedLocal >= directLiveLocal) (jsonObject [
        "addressSpanBytes", box (directReserved + directReservedLocal)
        "livePayloadBytes", box (directLive + directLiveLocal)
        "stackReservationSlackBytes", box (directReserved - directLive)
        "localReservationSlackBytes", box (directReservedLocal - directLiveLocal)
        "metrics", box (metricSummary directMetrics) ])
    let afterAllocationCapacity = max 0L (directReserved - 1L) |> int
    let afterAllocationBuffer = Array.copy oldRetainedState
    let afterAllocationBefore = Array.copy afterAllocationBuffer
    let afterAllocationFailure =
        try
            directProgram.ExecuteInto([ IntValue 17L; IntValue 1017L ], afterAllocationCapacity, afterAllocationBuffer, stateCapacity) |> ignore
            None
        with error -> Some error
    match afterAllocationFailure with
    | Some error ->
        let metrics = getProperty error "Metrics"
        let frameReturns = int64Property metrics "FrameReturnCount"
        let finalLive = int64Property metrics "FinalLiveStackBytes"
        let finalCursor = int64Property metrics "FinalCursorBytes"
        let peakLive = int64Property metrics "PeakLiveStackBytes"
        let inputCopyBytes = int64Property metrics "InputCopyBytes"
        let retainedCopyBytes = int64Property metrics "RetainedCopyBytes"
        let bufferUnchanged = afterAllocationBuffer = afterAllocationBefore
        let code = getProperty error "Code" |> string
        let boundary = getProperty error "Boundary" |> string
        let passed = code = "OWNING_STACK_CAPACITY" && boundary = "program-data-stack" && frameReturns > 0L && peakLive >= 16L && finalLive = 0L && finalCursor = 0L && inputCopyBytes = 16L && retainedCopyBytes = 0L && bufferUnchanged
        recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/post-allocation-post-frame-capacity-unwind" passed (jsonObject [
            "exception", box (resourceExceptionDetails error)
            "configuredStackCapacityBytes", box afterAllocationCapacity
            "frameReturnsBeforeFailure", box frameReturns
            "inputCopyBytes", box inputCopyBytes
            "peakLiveStackBytesBeforeFailure", box peakLive
            "retainedCopyBytes", box retainedCopyBytes
            "finalLiveStackBytes", box finalLive
            "finalCursorBytes", box finalCursor
            "callerBuffer", bufferCheckDetails afterAllocationBefore afterAllocationBuffer ])
    | None -> recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/post-allocation-post-frame-capacity-unwind" false "Capacity one byte below the successful program high-water unexpectedly succeeded."
    let directLayoutChecks, directLayoutFailures, directLayoutElement = validateTypeLayouts fixture [ "Leaf"; "Envelope" ] directLayouts
    for details in directLayoutChecks do checks.Add details
    for failure in directLayoutFailures do failures.Add($"owning-stack/{optimization}/N={repetition}/direct-layout: {failure}")
    recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/direct-layout-contract" (directLayoutFailures.Length = 0) (jsonNode options directLayoutElement)
    let fixedCaseReports = ResizeArray<obj>()
    if repetition = 1 then
        let fixedCases = fixture.GetProperty("fixedLayoutCases")
        use unitProgram = compile "unit-value" entries.UnitValue
        use unitDropProgram = compile "unit-drop" entries.UnitDrop
        use emptyProgram = compile "empty-value" entries.EmptyValue
        use scopedProgram = compile "scoped-width" entries.ScopedWidth
        use scopeShadowProgram = compile "scope-shadow" entries.ScopeShadow
        use depth64Program = compile "depth-64" entries.Depth64
        use depth65Program = compile "depth-65" entries.Depth65
        let unitExpected = expectedValues (fixedCases.GetProperty("unit").GetProperty("expected"))
        let unitExpectedBytes = bytesFromHex (fixedCases.GetProperty("unit").GetProperty("retainedBytesHex").GetString())
        let unitOutput = Array.create unitExpectedBytes.Length 0xA5uy
        let unitResult = unitProgram.ExecuteInto([], stackCapacity, unitOutput)
        let unitLayout = unitResult.Layouts |> List.tryFind (fun layout -> layout.TypeName = "Unit")
        let unitLayoutEvents = layoutEventRanges unitResult.LayoutEvents
        let unitAllocationEvents = unitLayoutEvents |> Array.filter (fun event -> event.Kind = 1L && event.Extent = 8L)
        let unitLayoutPassed =
            match unitLayout with
            | Some layout ->
                layout.PayloadBytes = fixedCases.GetProperty("unit").GetProperty("payloadBytes").GetInt32()
                && layout.ExtentBytes = fixedCases.GetProperty("unit").GetProperty("stackExtentBytes").GetInt32()
            | None -> false
        let unitAllocationPassed = unitAllocationEvents.Length = 1 && unitAllocationEvents[0].Payload = 8L
        let unitLayoutDetails =
            match unitLayout with
            | Some layout -> jsonObject [ "typeName", box layout.TypeName; "payloadBytes", box layout.PayloadBytes; "extentBytes", box layout.ExtentBytes; "fields", box (layout.Fields |> List.map (fun field -> jsonObject [ "name", box field.FieldName; "offsetBytes", box field.OffsetBytes; "payloadBytes", box field.PayloadBytes; "extentBytes", box field.ExtentBytes ])) ]
            | None -> null
        recordCheck checks failures $"owning-stack/{optimization}/unit-fixed-layout-value" (unitResult.Values = unitExpected) (ValueInspection.toJson entries.Program unitResult.Values)
        recordCheck checks failures $"owning-stack/{optimization}/unit-scalar-layout-and-retained-bytes" (unitLayoutPassed && unitAllocationPassed && unitResult.RetainedBytesWritten = 8 && unitOutput = unitExpectedBytes) (jsonObject [
            "layout", box unitLayoutDetails
            "allocateEvents", box unitAllocationEvents
            "retainedBytesWritten", box unitResult.RetainedBytesWritten
            "retainedBytes", box (bytesHex unitOutput)
            "metrics", jsonNode options (metricSummary (box unitResult.Metrics)) ])
        fixedCaseReports.Add(box (jsonObject [ "case", box "unit"; "values", jsonNode options (ValueInspection.toData entries.Program unitResult.Values); "retainedBytes", box (bytesHex unitOutput); "metrics", jsonNode options (metricSummary (box unitResult.Metrics)) ]))
        let unitDropExpected = expectedValues (fixedCases.GetProperty("unitDrop").GetProperty("expected"))
        let unitDropExpectedBytes = bytesFromHex (fixedCases.GetProperty("unitDrop").GetProperty("retainedBytesHex").GetString())
        let unitDropOutput = Array.create unitDropExpectedBytes.Length 0xA5uy
        let unitDropResult = unitDropProgram.ExecuteInto([], stackCapacity, unitDropOutput)
        let unitDropEvents = unitDropResult.LayoutEvents |> List.filter (fun event -> event.Kind = "drop" && event.PayloadBytes = 8 && event.ExtentBytes = 8)
        let unitDropMetrics = box unitDropResult.Metrics
        let unitDropPassed =
            unitDropResult.Values = unitDropExpected
            && unitDropOutput = unitDropExpectedBytes
            && unitDropResult.RetainedBytesWritten = unitDropExpectedBytes.Length
            && unitDropEvents.Length >= 1
            && int64Property unitDropMetrics "PeakLiveStackBytes" >= 8L
            && int64Property unitDropMetrics "FinalLiveStackBytes" = 0L
        recordCheck checks failures $"owning-stack/{optimization}/unit-drop-releases-eight-byte-token-and-continues" unitDropPassed (jsonObject [
            "values", box (ValueInspection.toJson entries.Program unitDropResult.Values)
            "retainedBytes", box (bytesHex unitDropOutput)
            "unitDropEvents", box (layoutEventRanges unitDropEvents)
            "metrics", box (jsonNode options (metricSummary unitDropMetrics)) ])
        checkTraceUsable checks failures $"owning-stack/{optimization}/unit-drop-trace-complete" unitDropResult.Metrics unitDropResult.LayoutEvents |> ignore
        fixedCaseReports.Add(box (jsonObject [ "case", box "unit-drop"; "values", jsonNode options (ValueInspection.toData entries.Program unitDropResult.Values); "retainedBytes", box (bytesHex unitDropOutput); "unitDropEvents", box (layoutEventRanges unitDropEvents); "metrics", jsonNode options (metricSummary unitDropMetrics) ]))
        let emptyExpected = expectedValues (fixedCases.GetProperty("emptyRecord").GetProperty("expected"))
        let emptyExpectedBytes = bytesFromHex (fixedCases.GetProperty("emptyRecord").GetProperty("retainedBytesHex").GetString())
        let emptyOutput = Array.create emptyExpectedBytes.Length 0xA5uy
        let emptyResult = emptyProgram.ExecuteInto([], stackCapacity, emptyOutput)
        let emptyLayoutChecks, emptyLayoutFailures, emptyLayoutSummary = validateTypeLayouts fixture [ "Empty" ] emptyResult.Layouts
        let emptyAllocationEvents = layoutEventRanges emptyResult.LayoutEvents |> Array.filter (fun event -> event.Kind = 1L && event.Extent = 8L)
        let emptyTokenAllocationPassed = emptyAllocationEvents.Length = 1 && emptyAllocationEvents[0].Payload = 0L
        for details in emptyLayoutChecks do checks.Add details
        for failure in emptyLayoutFailures do failures.Add($"owning-stack/{optimization}/empty-record-layout: {failure}")
        recordCheck checks failures $"owning-stack/{optimization}/empty-record-zero-field-value" (emptyResult.Values = emptyExpected) (ValueInspection.toJson entries.Program emptyResult.Values)
        recordCheck checks failures $"owning-stack/{optimization}/empty-record-token-extent-and-bytes" (emptyLayoutFailures.Length = 0 && emptyTokenAllocationPassed && emptyResult.RetainedBytesWritten = 8 && emptyOutput = emptyExpectedBytes) (jsonObject [
            "layout", box (jsonNode options emptyLayoutSummary)
            "allocateEvents", box emptyAllocationEvents
            "retainedBytesWritten", box emptyResult.RetainedBytesWritten
            "retainedBytes", box (bytesHex emptyOutput)
            "metrics", box (jsonNode options (metricSummary (box emptyResult.Metrics))) ])
        fixedCaseReports.Add(box (jsonObject [ "case", box "empty-record"; "values", jsonNode options (ValueInspection.toData entries.Program emptyResult.Values); "retainedBytes", box (bytesHex emptyOutput); "metrics", jsonNode options (metricSummary (box emptyResult.Metrics)) ]))
        checkTraceUsable checks failures $"owning-stack/{optimization}/unit-trace-complete" unitResult.Metrics unitResult.LayoutEvents |> ignore
        checkTraceUsable checks failures $"owning-stack/{optimization}/empty-record-trace-complete" emptyResult.Metrics emptyResult.LayoutEvents |> ignore
        for index, scopedCase in fixture.GetProperty("scopedWidthCases").EnumerateArray() |> Seq.indexed do
            let input = scopedCase.GetProperty("input").GetInt64()
            let expected = expectedValues (scopedCase.GetProperty("expected"))
            let expectedBytes = bytesFromHex (scopedCase.GetProperty("retainedBytesHex").GetString())
            let output = Array.create expectedBytes.Length 0xA5uy
            let result = scopedProgram.ExecuteInto([ IntValue input ], stackCapacity, output)
            let layoutChecks, layoutFailures, layoutSummary = validateTypeLayouts fixture [ "Leaf"; "Envelope"; "State" ] result.Layouts
            for details in layoutChecks do checks.Add details
            for failure in layoutFailures do failures.Add($"owning-stack/{optimization}/scoped-width-{index}-layout: {failure}")
            recordCheck checks failures $"owning-stack/{optimization}/scoped-width-{index}-restores-outer-value" (result.Values = expected) (ValueInspection.toJson entries.Program result.Values)
            recordCheck checks failures $"owning-stack/{optimization}/scoped-width-{index}-retained-layout-bytes" (layoutFailures.Length = 0 && result.RetainedBytesWritten = expectedBytes.Length && output = expectedBytes) (jsonObject [
                "layout", box (jsonNode options layoutSummary)
                "retainedBytes", box (bytesHex output)
                "metrics", box (jsonNode options (metricSummary (box result.Metrics))) ])
            let localReservationPassed = int64Property (box result.Metrics) "ReservedLocalBytes" >= int64Property (box result.Metrics) "PeakLiveLocalBytes" && int64Property (box result.Metrics) "FinalLiveStackBytes" = 0L
            recordCheck checks failures $"owning-stack/{optimization}/scoped-width-{index}-local-frame-cleanup" localReservationPassed (jsonNode options (metricSummary (box result.Metrics)))
            checkTraceUsable checks failures $"owning-stack/{optimization}/scoped-width-{index}-trace-complete" result.Metrics result.LayoutEvents |> ignore
            fixedCaseReports.Add(box (jsonObject [ "case", box "scoped-width"; "input", box input; "values", jsonNode options (ValueInspection.toData entries.Program result.Values); "retainedBytes", box (bytesHex output); "metrics", jsonNode options (metricSummary (box result.Metrics)) ]))
        let shadowCase = fixedCases.GetProperty("scopeShadow")
        let shadowExpected = expectedValues (shadowCase.GetProperty("expected"))
        let shadowExpectedBytes = bytesFromHex (shadowCase.GetProperty("retainedBytesHex").GetString())
        let shadowOutput = Array.create shadowExpectedBytes.Length 0xA5uy
        let shadowResult = scopeShadowProgram.ExecuteInto([], stackCapacity, shadowOutput)
        let shadowLayoutChecks, shadowLayoutFailures, shadowLayoutSummary = validateTypeLayouts fixture [ "Leaf"; "Envelope" ] shadowResult.Layouts
        for details in shadowLayoutChecks do checks.Add details
        for failure in shadowLayoutFailures do failures.Add($"owning-stack/{optimization}/scope-shadow-layout: {failure}")
        let shadowMetrics = box shadowResult.Metrics
        let shadowScopePassed =
            shadowResult.Values = shadowExpected
            && shadowOutput = shadowExpectedBytes
            && shadowLayoutFailures.Length = 0
            && int64Property shadowMetrics "ReservedLocalBytes" >= 16L
            && int64Property shadowMetrics "FinalLiveStackBytes" = 0L
        recordCheck checks failures $"owning-stack/{optimization}/scope-shadow-same-slot-restores-envelope" shadowScopePassed (jsonObject [
            "values", box (ValueInspection.toJson entries.Program shadowResult.Values)
            "retainedBytes", box (bytesHex shadowOutput)
            "layout", box (jsonNode options shadowLayoutSummary)
            "metrics", box (jsonNode options (metricSummary shadowMetrics)) ])
        checkTraceUsable checks failures $"owning-stack/{optimization}/scope-shadow-trace-complete" shadowResult.Metrics shadowResult.LayoutEvents |> ignore
        fixedCaseReports.Add(box (jsonObject [ "case", box "scope-shadow"; "values", jsonNode options (ValueInspection.toData entries.Program shadowResult.Values); "retainedBytes", box (bytesHex shadowOutput); "metrics", jsonNode options (metricSummary shadowMetrics) ]))
        let depthOutput = Array.create 8 0xA5uy
        let depthResult = depth64Program.ExecuteInto([ IntValue 42L ], 32768, depthOutput)
        recordCheck checks failures $"owning-stack/{optimization}/call-depth-64-succeeds" (depthResult.Values = [ IntValue 42L ] && depthOutput = bytesFromInt64s [ 42L ]) (jsonObject [
            "values", box (ValueInspection.toJson entries.Program depthResult.Values)
            "retainedBytes", box (bytesHex depthOutput)
            "metrics", box (jsonNode options (metricSummary (box depthResult.Metrics))) ])
        let depthFailureBuffer = Array.create 8 0xA5uy
        let depthFailureBefore = Array.copy depthFailureBuffer
        let depthFailure, depthFailureMetrics =
            try
                depth65Program.ExecuteInto([ IntValue 42L ], 32768, depthFailureBuffer) |> ignore
                "unexpected-success", null
            with error -> diagnosticCode error, getProperty error "Metrics"
        let depthFinalLive = if isNull depthFailureMetrics then -1L else int64Property depthFailureMetrics "FinalLiveStackBytes"
        let depthFinalCursor = if isNull depthFailureMetrics then -1L else int64Property depthFailureMetrics "FinalCursorBytes"
        recordCheck checks failures $"owning-stack/{optimization}/call-depth-65-diagnostic-unwinds" (depthFailure = "RUNTIME_CALL_DEPTH" && depthFinalLive = 0L && depthFinalCursor = 0L && depthFailureBuffer = depthFailureBefore) (jsonObject [
            "diagnosticCode", box depthFailure
            "finalLiveStackBytes", box depthFinalLive
            "finalCursorBytes", box depthFinalCursor
            "callerBuffer", box (bufferCheckDetails depthFailureBefore depthFailureBuffer)
            "metrics", (if isNull depthFailureMetrics then null else jsonNode options (metricSummary depthFailureMetrics)) ])
    let branches = fixture.GetProperty("branches").EnumerateArray() |> Seq.toArray
    let branchReports =
        branches
        |> Array.mapi (fun index branch ->
            let value = branch.GetProperty("value").GetInt64()
            let chooseLeft = branch.GetProperty("chooseLeft").GetBoolean()
            let expectedElement = branch.GetProperty("expected")
            let expected = expectedValues expectedElement
            let branchOutput = Array.create 16 0xA5uy
            let branchResult = branchProgram.ExecuteInto([ IntValue value; BoolValue chooseLeft ], stackCapacity, branchOutput)
            let actual = getProperty (box branchResult) "Values" :?> Value list
            let branchMetrics = stackMetrics (box branchResult)
            let branchLayouts = getProperty (box branchResult) "Layouts" :?> OwningStackTypeLayout list
            let layoutChecks, layoutFailures, layoutSummary = validateTypeLayouts fixture [ "Leaf"; "Envelope" ] branchLayouts
            for details in layoutChecks do checks.Add details
            for failure in layoutFailures do failures.Add($"owning-stack/{optimization}/N={repetition}/branch-{index}-layout: {failure}")
            let expectedBytes = bytesFromHex (branch.GetProperty("retainedBytesHex").GetString())
            recordCheck checks failures $"fixture/N={repetition}/branch-{index}-little-endian-layout-bytes" (envelopeBytesFromJson expectedElement = expectedBytes) (jsonObject [
                "fixtureHex", box (bytesHex expectedBytes)
                "independentValueEncodingHex", box (bytesHex (envelopeBytesFromJson expectedElement)) ])
            recordCheck checks failures $"owning-stack/{optimization}/branch-{index}" (actual = expected) (ValueInspection.toJson entries.Program actual)
            recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/branch-{index}-retained-bytes" (branchOutput = expectedBytes) (bufferCheckDetails expectedBytes branchOutput)
            box (jsonObject [
                "chooseLeft", box chooseLeft
                "values", jsonNode options (ValueInspection.toData entries.Program actual)
                "retainedBytes", box (bytesHex branchOutput)
                "metrics", jsonNode options (metricSummary branchMetrics)
                "typeLayouts", jsonNode options layoutSummary
                "layoutEvents", jsonNode options (layoutEventRanges (getProperty (box branchResult) "LayoutEvents" :?> OwningStackLayoutEvent list)) ]))
    for (turnName, metrics) in [ "turn1", firstMetrics; "turn2", secondMetrics ] do
        let summary = metricSummary metrics
        candidateSummaries.Add($"{optimization}/N={repetition}/{turnName}", summary)
        let inputBytes = int64Property metrics "InputBytes"
        let inputCopyBytes = int64Property metrics "InputCopyBytes"
        let retainedCopyBytes = int64Property metrics "RetainedCopyBytes"
        let retainedCapacityBytes = int64Property metrics "RetainedCapacityBytes"
        let hostStagingBytes = int64Property metrics "HostRetainedStagingBytes"
        let hostCommitBytes = int64Property metrics "HostRetainedCommitBytes"
        let measuredCopySizesPass = inputBytes = 32L && inputCopyBytes = 32L && retainedCopyBytes = 24L && retainedCapacityBytes = 24L && hostStagingBytes = 24L && hostCommitBytes = 24L
        recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/{turnName}/input-retained-copy-categories" measuredCopySizesPass (jsonObject [
            "expectedInputBytes", box 32
            "actualInputBytes", box inputBytes
            "expectedInputCopyBytes", box 32
            "actualInputCopyBytes", box inputCopyBytes
            "expectedRetainedCopyBytes", box 24
            "actualRetainedCopyBytes", box retainedCopyBytes
            "retainedCapacityBytes", box retainedCapacityBytes
            "hostRetainedStagingBytes", box hostStagingBytes
            "hostRetainedCommitBytes", box hostCommitBytes ])
        let reserved = int64Property metrics "ReservedStackBytes"
        let live = int64Property metrics "PeakLiveStackBytes"
        let reservedLocal = int64Property metrics "ReservedLocalBytes"
        let liveLocal = int64Property metrics "PeakLiveLocalBytes"
        recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/{turnName}/live-reserved-span" (reserved >= live && reservedLocal >= liveLocal) (jsonObject [
            "addressSpanBytes", box (reserved + reservedLocal)
            "livePayloadBytes", box (live + liveLocal)
            "stackReservationSlackBytes", box (reserved - live)
            "localReservationSlackBytes", box (reservedLocal - liveLocal)
            "metrics", box summary ])
    let stateLayoutChecks, stateLayoutFailures, stateLayoutElement =
        validateTypeLayouts fixture [ "Leaf"; "Envelope"; "State" ] (getProperty (box firstResult) "Layouts" :?> OwningStackTypeLayout list)
    for details in stateLayoutChecks do checks.Add details
    for failure in stateLayoutFailures do failures.Add($"owning-stack/{optimization}/N={repetition}/State-layout: {failure}")
    recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/full-nested-layout-contract" (stateLayoutFailures.Length = 0) (jsonNode options stateLayoutElement)
    checkTraceUsable checks failures $"owning-stack/{optimization}/N={repetition}/direct-trace-complete" directMetrics directEvents |> ignore
    checkTraceUsable checks failures $"owning-stack/{optimization}/N={repetition}/turn1-trace-complete" firstMetrics (getProperty (box firstResult) "LayoutEvents" :?> OwningStackLayoutEvent list) |> ignore
    checkTraceUsable checks failures $"owning-stack/{optimization}/N={repetition}/turn2-trace-complete" secondMetrics (getProperty (box secondResult) "LayoutEvents" :?> OwningStackLayoutEvent list) |> ignore
    jsonObject [
        "backend", box $"inline-owning-stack/{optimization}"
        "repetitions", box repetition
        "afterTurn1", jsonNode options (ValueInspection.toData entries.Program firstValues)
        "afterTurn2", jsonNode options (ValueInspection.toData entries.Program secondValues)
        "retainedBytesAfterTurn1", box (bytesHex firstOutput)
        "retainedBytesAfterTurn2", box (bytesHex secondOutput)
        "initializeMetrics", jsonNode options (metricSummary initialMetrics)
        "turn1Metrics", jsonNode options (metricSummary firstMetrics)
        "turn2Metrics", jsonNode options (metricSummary secondMetrics)
        "stateLayoutContract", jsonNode options stateLayoutElement
        "directDupDropMetrics", jsonNode options (metricSummary directMetrics)
        "directDupDropLayouts", jsonNode options directLayoutElement
        "directDupDropLayoutContract", jsonNode options directLayoutElement
        "directDupDropLayoutEvents", jsonNode options directRanges
        "fixedLayoutCases", box (fixedCaseReports.ToArray())
        "branchRuns", box branchReports
        "failureAfterAllocation", box diagnostic
        "failureAfterAllocationMetrics", (if isNull diagnosticMetrics then null else jsonNode options (metricSummary diagnosticMetrics)) ]

[<EntryPoint>]
let main argv =
    let reportPath =
        if argv.Length >= 3 then Path.GetFullPath argv[2]
        else Path.Combine(Environment.CurrentDirectory, "native-value-stack-evidence.json")
    let artifactsRoot = if argv.Length >= 2 then Path.GetFullPath argv[1] else Path.Combine(Environment.CurrentDirectory, "native-value-stack-artifacts")
    let checks = ResizeArray<obj>()
    let failures = ResizeArray<string>()
    let options = JsonSerializerOptions(WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
    let report = Dictionary<string, obj>(StringComparer.Ordinal)
    let mutable exceptionText = ""
    try
        if argv.Length <> 3 then invalidArg "arguments" "Usage: AgentLang.NativeValueStack <fixture.json> <artifact-directory> <evidence.json>"
        let fixturePath = Path.GetFullPath argv[0]
        let fixturePathFromRepo = fixturePath
        let sourcePath = Path.Combine(AppContext.BaseDirectory, "value-stack.flow")
        let source = File.ReadAllText sourcePath
        let sourceHash = hashFile sourcePath
        use fixtureDocument = JsonDocument.Parse(File.ReadAllText fixturePath)
        let fixture = fixtureDocument.RootElement
        let expectedSourceHash = fixture.GetProperty("source").GetProperty("sha256").GetString()
        recordCheck checks failures "fixture/source-hash" (String.Equals(sourceHash, expectedSourceHash, StringComparison.Ordinal)) (jsonObject [ "expected", box expectedSourceHash; "actual", box sourceHash ])
        let repetitions = fixture.GetProperty("repetitions").EnumerateArray() |> Seq.map (fun item -> item.GetInt32()) |> Seq.toArray
        if repetitions <> [| 1; 8 |] then invalidOp "The independent fixture must retain repetition counts 1 and 8."
        let generatedTurns = repetitions |> Array.map turnSource
        let depthSource = depthChainSource 65
        let expandedSource = String.concat "\n\n" [ source; String.concat "\n\n" generatedTurns; depthSource ]
        Directory.CreateDirectory(Path.GetDirectoryName reportPath) |> ignore
        Directory.CreateDirectory artifactsRoot |> ignore
        File.WriteAllText(Path.Combine(Path.GetDirectoryName reportPath, "expanded-source.flow"), expandedSource, UTF8Encoding(false))
        let turnHashes =
            repetitions
            |> Array.mapi (fun index count -> box (jsonObject [ "repetitions", box count; "sha256", box (hashText generatedTurns[index]) ]))
        addCommonEntryReports report sourceHash expandedSource turnHashes
        report["depthChain"] <- box (jsonObject [ "maxDepth", box 65; "sourceSha256", box (hashText depthSource); "shape", box "Static acyclic function chain; chain-64 enters 65 user bodies (depth 0 through 64), chain-65 enters 66 and crosses the language boundary." ])
        report["fixturePath"] <- box fixturePathFromRepo
        let entries = compileEntries expandedSource
        let scopeShadowPassed, scopeShadowDetails = inspectScopeShadow entries.Program entries.ScopeShadow
        recordCheck checks failures "verified-ir/scope-shadow-same-local-slot-width-restore" scopeShadowPassed scopeShadowDetails
        report["scopeShadowIr"] <- scopeShadowDetails
        report["sameVerifiedProgramInstance"] <- box true
        report["backendScope"] <- box "The same VerifiedIrBody objects are executed by the interpreter, ABI3 control, and inline owning-stack candidate."
        report["turnBoundaryScope"] <- box "Each owning-stack invocation publishes fixed State bytes into a caller-owned output buffer. Turn 2 is fed from an independent decode of turn 1's raw retained bytes through the Value input API; this validates publication and round-trip semantics across the host boundary, not a persistent native controller."
        report["limitations"] <- fixture.GetProperty("limits").EnumerateArray() |> Seq.map (fun value -> box (value.GetString())) |> Seq.toArray
        let optimizationPairs = [| LlvmOptimization.O0, "O0"; LlvmOptimization.O2, "O2" |]
        let runReports = ResizeArray<obj>()
        let candidateSummaries = Dictionary<string, obj>(StringComparer.Ordinal)
        for repetition in repetitions do
            let body = if repetition = 1 then entries.TurnOne else entries.TurnEight
            runReports.Add(box (runInterpreterChain checks failures options fixture entries repetition body))
            for optimization, _ in optimizationPairs do
                runReports.Add(box (runNativeChain checks failures options artifactsRoot optimization fixture entries repetition body))
                runReports.Add(box (runOwningStackChain checks failures options artifactsRoot optimization fixture entries repetition body candidateSummaries))
        let readSummary (key: string) : Dictionary<string, obj> = candidateSummaries[key] :?> Dictionary<string, obj>
        let metricInt (summary: Dictionary<string, obj>) (name: string) = Convert.ToInt64(summary[name], CultureInfo.InvariantCulture)
        let repetitionComparisons = ResizeArray<obj>()
        for (_, optimizationName) in optimizationPairs do
            for turnName in [ "turn1"; "turn2" ] do
                let n1 = readSummary $"{optimizationName}/N=1/{turnName}"
                let n8 = readSummary $"{optimizationName}/N=8/{turnName}"
                let invariantFields = [ "peakLiveStackBytes"; "reservedStackBytes"; "peakLiveLocalBytes"; "reservedLocalBytes"; "inputBytes"; "inputCopyBytes"; "retainedCopyBytes"; "hostRetainedStagingBytes"; "hostRetainedCommitBytes" ]
                let invariantResults =
                    invariantFields
                    |> List.map (fun field -> field, metricInt n1 field, metricInt n8 field)
                let invariantPassed = invariantResults |> List.forall (fun (_, left, right) -> left = right)
                recordCheck checks failures $"owning-stack/{optimizationName}/{turnName}/same-maximum-depth-for-N1-and-N8" invariantPassed (jsonObject [
                    "independentExpectation", box "The unrolled repeated helper calls have the same maximum call depth, and the helper's scoped local frame is released on each return."
                    "categories", box (invariantResults |> List.map (fun (name, n1Value, n8Value) -> jsonObject [ "category", box name; "N1", box n1Value; "N8", box n8Value ])) ])
                let copyDelta = metricInt n8 "deepCopyBytes" - metricInt n1 "deepCopyBytes"
                let frameDelta = metricInt n8 "frameReturnCount" - metricInt n1 "frameReturnCount"
                let repetitionPassed = copyDelta >= 7L * 16L && frameDelta >= 7L
                recordCheck checks failures $"owning-stack/{optimizationName}/{turnName}/repetition-copy-and-frame-growth" repetitionPassed (jsonObject [
                    "minimumExplicitDuplicateGrowthBytes", box (7 * 16)
                    "actualDeepCopyGrowthBytes", box copyDelta
                    "minimumAdditionalHelperReturns", box 7
                    "actualFrameReturnGrowth", box frameDelta ])
                repetitionComparisons.Add(box (jsonObject [
                    "optimization", box optimizationName
                    "turn", box turnName
                    "categories", box (invariantResults |> List.map (fun (name, n1Value, n8Value) -> jsonObject [ "category", box name; "N1", box n1Value; "N8", box n8Value ]))
                    "deepCopyGrowthBytes", box copyDelta
                    "frameReturnGrowth", box frameDelta ]))
        report["runs"] <- runReports.ToArray()
        report["candidateRepetitionComparisons"] <- repetitionComparisons.ToArray()
    with error ->
        let exceptionDetails =
            [ "Diagnostic"; "Metrics"; "RequiredBytes"; "AvailableBytes"; "Boundary" ]
            |> List.choose (fun name ->
                let value = getProperty error name
                if isNull value then None else Some($"{name}: {value}"))
            |> String.concat "\n"
        exceptionText <-
            if String.IsNullOrWhiteSpace exceptionDetails then error.ToString()
            else error.ToString() + "\n" + exceptionDetails
        failures.Add(exceptionText)
    report["passed"] <- box (String.IsNullOrEmpty exceptionText && failures.Count = 0 && checks.Count > 0)
    report["checks"] <- checks.ToArray()
    report["failure"] <- box exceptionText
    report["failureCount"] <- box failures.Count
    report["failureDetails"] <- box (failures.ToArray())
    Directory.CreateDirectory(Path.GetDirectoryName reportPath) |> ignore
    File.WriteAllText(reportPath, JsonSerializer.Serialize(report, options), UTF8Encoding(false))
    let outcome = if failures.Count = 0 then "passed" else "failed"
    Console.WriteLine($"Native owning-value-stack experiment: {outcome}; evidence={reportPath}")
    if failures.Count = 0 then 0 else 1
