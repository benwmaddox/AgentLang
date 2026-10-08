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
      EmptyIdentity: VerifiedIrBody
      ScopedWidth: VerifiedIrBody
      ScopeShadow: VerifiedIrBody
      Depth64: VerifiedIrBody
      Depth65: VerifiedIrBody
      DirectDupDrop: VerifiedIrBody
      FailAfterAllocation: VerifiedIrBody }

type private StringEntryBodies =
    { Program: VerifiedIrProgram
      CompilerContext: Compiler.IrLoweringContext
      SourceOrigins: Map<SourceSpan, SourceSpan>
      TurnText: VerifiedIrBody
      IdentityString: VerifiedIrBody
      JoinStrings: VerifiedIrBody
      DirectConcat: VerifiedIrBody
      MixedConstruct: VerifiedIrBody
      MixedProjectTextAndSentinel: VerifiedIrBody
      MixedProjectEmptyMarker: VerifiedIrBody
      DynamicBranch: VerifiedIrBody
      FailAfterTextAllocation: VerifiedIrBody
      DirectDupDrop: VerifiedIrBody
      ScopeShadow: VerifiedIrBody
      ZeroOutputUserCall: VerifiedIrBody }

type private LayoutDepthCase =
    { Program: VerifiedIrProgram
      Body: VerifiedIrBody
      RecordLevels: int
      LayoutLevels: int
      TopTypeName: string }

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

let private compileFlowProgram (source: string) =
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
    compiled

let private compileEntries (source: string) =
    let compiled = compileFlowProgram source
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
          EmptyIdentity =
            Compiler.compileIrBodyAgainstProgramWithSourceOrigins
                compiled.Context.CompilerContext
                compiled.Program
                "native-value-stack-empty-record-identity"
                [ TNamed "Empty" ]
                []
                compiled.Context.SourceOrigins
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
        [ entries.Initialize; entries.TurnOne; entries.TurnEight; entries.Branch; entries.UnitValue; entries.UnitDrop; entries.EmptyValue; entries.EmptyIdentity; entries.ScopedWidth; entries.Depth64; entries.Depth65; entries.ScopeShadow; entries.DirectDupDrop; entries.FailAfterAllocation ]
    if not (VerifiedIrProgram.isBackendExecutable entries.Program) then
        invalidOp "Flow lowering did not produce a backend-authorized VerifiedIrProgram."
    if bodies |> List.exists (fun body -> not (Object.ReferenceEquals(VerifiedIrBody.program body, entries.Program))) then
        invalidOp "Every comparison body must share the exact VerifiedIrProgram instance."
    entries

let private compileStringEntries (source: string) =
    let flowCompiled = compileFlowProgram source
    let helperSpan = span "<native-value-stack-zero-output-helper>" 1
    let discardDefinition: WordDefinition =
        { Name = "mailbox.discard-text-envelope"
          Inputs = [ TNamed "TextEnvelope" ]
          Outputs = []
          Effects = Set.empty
          Maturity = LibraryWord
          Revision = 1
          Documentation = "Compiler-minted no-output helper used by the owning-stack return conformance case."
          Body = [ Call("drop", helperSpan) ]
          SourceText = "compiler-minted zero-output helper"
          Span = helperSpan }
    let discardEntry: WordEntry =
        { Definition = discardDefinition
          Builtin = None
          Status = Persistent
          Maturity = LibraryWord
          Revision = 1 }
    let discardName = discardDefinition.Name
    let compilerContext =
        { flowCompiled.Context.CompilerContext with
            Words = Map.add discardName discardEntry flowCompiled.Context.CompilerContext.Words
            WordIds = Map.add discardName (WordId "native-value-stack-string-discard-text-envelope") flowCompiled.Context.CompilerContext.WordIds }
    let verifiedProgram = Compiler.compileIrProgramWithSourceOrigins compilerContext flowCompiled.Context.SourceOrigins
    let compiled =
        { flowCompiled with
            Program = verifiedProgram
            Context = { flowCompiled.Context with CompilerContext = compilerContext } }
    let compileEntry entryName target inputTypes =
        Compiler.compileIrBodyAgainstProgramWithSourceOrigins
            compiled.Context.CompilerContext
            compiled.Program
            entryName
            inputTypes
            [ Call(target, span ("<" + entryName + ">") 1) ]
            compiled.Context.SourceOrigins
    let stringSpan name column = span ("<native-value-stack-string-" + name + ">") column
    let entries =
        { Program = compiled.Program
          CompilerContext = compiled.Context.CompilerContext
          SourceOrigins = compiled.Context.SourceOrigins
          TurnText = compileEntry "mailbox.turn-text.entry" "mailbox.turn-text" [ TNamed "TextState" ]
          IdentityString = compileEntry "mailbox.identity-string.entry" "mailbox.identity-string" [ TString ]
          JoinStrings = compileEntry "mailbox.join-strings.entry" "mailbox.join-strings" [ TString; TString ]
          DirectConcat =
            Compiler.compileIrBodyAgainstProgramWithSourceOrigins
                compiled.Context.CompilerContext
                compiled.Program
                "native-value-stack-direct-string-concat-capacity"
                [ TString; TString ]
                [ Call("string.concat", stringSpan "direct-string-concat-capacity" 1) ]
                compiled.Context.SourceOrigins
          MixedConstruct = compileEntry "mailbox.make-text-mixed.entry" "mailbox.make-text-mixed" [ TString; TInt ]
          MixedProjectTextAndSentinel = compileEntry "mailbox.project-text-mixed.entry" "mailbox.project-text-mixed" [ TNamed "TextMixed" ]
          MixedProjectEmptyMarker = compileEntry "mailbox.project-text-mixed-marker.entry" "mailbox.project-text-mixed-marker" [ TNamed "TextMixed" ]
          DynamicBranch = compileEntry "mailbox.choose-text-envelope.entry" "mailbox.choose-text-envelope" [ TNamed "TextEnvelope"; TNamed "TextEnvelope"; TBool ]
          FailAfterTextAllocation = compileEntry "mailbox.fail-after-text-allocation.entry" "mailbox.fail-after-text-allocation" [ TNamed "TextState" ]
          DirectDupDrop =
            Compiler.compileIrBodyAgainstProgramWithSourceOrigins
                compiled.Context.CompilerContext
                compiled.Program
                "native-value-stack-string-direct-dup-drop"
                [ TNamed "TextEnvelope" ]
                [ Call("dup", stringSpan "direct-dup-drop" 1)
                  Call("drop", stringSpan "direct-dup-drop" 2) ]
                compiled.Context.SourceOrigins
          ScopeShadow =
            Compiler.compileIrBodyAgainstProgramWithSourceOrigins
                compiled.Context.CompilerContext
                compiled.Program
                "native-value-stack-string-scope-shadow"
                [ TNamed "TextEnvelope"; TNamed "TextEnvelope" ]
                [ Call("swap", stringSpan "scope-shadow" 1)
                  Let("shadow", stringSpan "scope-shadow" 2)
                  Scope(
                    [ Let("shadow", stringSpan "scope-shadow" 3) ],
                    stringSpan "scope-shadow" 4)
                  Load("shadow", stringSpan "scope-shadow" 5) ]
                compiled.Context.SourceOrigins
          ZeroOutputUserCall =
            Compiler.compileIrBodyAgainstProgramWithSourceOrigins
                compiled.Context.CompilerContext
                compiled.Program
                "native-value-stack-string-zero-output-user-call"
                [ TNamed "TextEnvelope" ]
                [ Call("mailbox.discard-text-envelope", stringSpan "zero-output-user-call" 1) ]
                compiled.Context.SourceOrigins }
    let bodies =
        [ entries.TurnText
          entries.IdentityString
          entries.JoinStrings
          entries.DirectConcat
          entries.MixedConstruct
          entries.MixedProjectTextAndSentinel
          entries.MixedProjectEmptyMarker
          entries.DynamicBranch
          entries.FailAfterTextAllocation
          entries.DirectDupDrop
          entries.ScopeShadow
          entries.ZeroOutputUserCall ]
    if not (VerifiedIrProgram.isBackendExecutable entries.Program) then
        invalidOp "Flow lowering did not produce a backend-authorized String VerifiedIrProgram."
    if bodies |> List.exists (fun body -> not (Object.ReferenceEquals(VerifiedIrBody.program body, entries.Program))) then
        invalidOp "Every String comparison body must share the exact VerifiedIrProgram instance."
    entries

let private compileTextStateFactory (entries: StringEntryBodies) (caseName: string) (value: Value) =
    let count, tag, codeUnits, text =
        match value with
        | RecordValue("TextState", state) ->
            match state["count"], state["last"] with
            | IntValue count, RecordValue("TextEnvelope", envelope) ->
                match envelope["tag"], envelope["leaf"] with
                | IntValue tag, RecordValue("TextLeaf", leaf) ->
                    match leaf["text"], leaf["codeUnits"] with
                    | StringValue text, IntValue codeUnits -> count, tag, codeUnits, text
                    | _ -> invalidOp "TextState fixture factory requires StringValue and IntValue leaf fields."
                | _ -> invalidOp "TextState fixture factory requires an Int tag and TextLeaf."
            | _ -> invalidOp "TextState fixture factory requires an Int count and TextEnvelope."
        | _ -> invalidOp "TextState fixture factory requires a TextState Value."
    let site name column = span ($"<native-value-stack-string-{caseName}-{name}>") column
    Compiler.compileIrBodyAgainstProgramWithSourceOrigins
        entries.CompilerContext
        entries.Program
        ($"native-value-stack-string-input-{caseName}")
        []
        [ Push(LInt count, site "input-state-count" 1)
          Push(LString text, site "input-string" 2)
          Push(LInt codeUnits, site "input-code-units" 3)
          Call("textLeaf.new", site "input-leaf" 4)
          Push(LInt tag, site "input-tag" 5)
          Call("textEnvelope.new", site "input-envelope" 6)
          Call("textState.new", site "input-state" 7) ]
        entries.SourceOrigins

let private compileTextEnvelopePairFactory (entries: StringEntryBodies) (caseName: string) (first: Value) (second: Value) =
    let envelopeParts = function
        | RecordValue("TextEnvelope", envelope) ->
            match envelope["tag"], envelope["leaf"] with
            | IntValue tag, RecordValue("TextLeaf", leaf) ->
                match leaf["text"], leaf["codeUnits"] with
                | StringValue text, IntValue codeUnits -> tag, codeUnits, text
                | _ -> invalidOp "TextEnvelope fixture factory requires StringValue and IntValue leaf fields."
            | _ -> invalidOp "TextEnvelope fixture factory requires an Int tag and TextLeaf."
        | _ -> invalidOp "TextEnvelope fixture factory requires TextEnvelope Values."
    let firstTag, firstLength, firstText = envelopeParts first
    let secondTag, secondLength, secondText = envelopeParts second
    let site name column = span ($"<native-value-stack-string-{caseName}-{name}>") column
    let expressions =
        [ Push(LString firstText, site "first-text" 1)
          Push(LInt firstLength, site "first-length" 2)
          Call("textLeaf.new", site "first-leaf" 3)
          Push(LInt firstTag, site "first-tag" 4)
          Call("textEnvelope.new", site "first-envelope" 5)
          Push(LString secondText, site "second-text" 6)
          Push(LInt secondLength, site "second-length" 7)
          Call("textLeaf.new", site "second-leaf" 8)
          Push(LInt secondTag, site "second-tag" 9)
          Call("textEnvelope.new", site "second-envelope" 10) ]
    Compiler.compileIrBodyAgainstProgramWithSourceOrigins
        entries.CompilerContext
        entries.Program
        ($"native-value-stack-string-envelope-pair-{caseName}")
        []
        expressions
        entries.SourceOrigins

let private compileStringPairFactory (entries: StringEntryBodies) (caseName: string) (left: string) (right: string) =
    let site name column = span ($"<native-value-stack-string-{caseName}-{name}>") column
    Compiler.compileIrBodyAgainstProgramWithSourceOrigins
        entries.CompilerContext
        entries.Program
        ($"native-value-stack-string-pair-{caseName}")
        []
        [ Push(LString left, site "left" 1); Push(LString right, site "right" 2) ]
        entries.SourceOrigins

let private compileStringFactory (entries: StringEntryBodies) (caseName: string) (value: string) =
    let site = span ($"<native-value-stack-string-{caseName}-literal>") 1
    Compiler.compileIrBodyAgainstProgramWithSourceOrigins
        entries.CompilerContext
        entries.Program
        ($"native-value-stack-string-value-{caseName}")
        []
        [ Push(LString value, site) ]
        entries.SourceOrigins

let private compileTextMixedInputFactory (entries: StringEntryBodies) (caseName: string) (text: string) (sentinel: int64) =
    let site name column = span ($"<native-value-stack-mixed-{caseName}-{name}>") column
    Compiler.compileIrBodyAgainstProgramWithSourceOrigins
        entries.CompilerContext
        entries.Program
        ($"native-value-stack-mixed-input-{caseName}")
        []
        [ Push(LString text, site "text" 1); Push(LInt sentinel, site "sentinel" 2) ]
        entries.SourceOrigins

let private compileLayoutDepthCase recordLevels =
    let recordSource =
        [ 0 .. recordLevels - 1 ]
        |> List.map (fun depth ->
            let name = $"DepthNode{depth}"
            let field = if depth = 0 then "field value: Int;" else $"field next: DepthNode{depth - 1};"
            $"record {name} {{ {field} }}")
        |> String.concat "\n"
    let topType = $"DepthNode{recordLevels - 1}"
    let functionSource = $"""fn mailbox.layout-depth(value: {topType}) -> {topType} {{
    effects none
    value
}}"""
    let compiled = compileFlowProgram (String.concat "\n\n" [ recordSource; functionSource ])
    let body =
        Compiler.compileIrBodyAgainstProgramWithSourceOrigins
            compiled.Context.CompilerContext
            compiled.Program
            $"native-value-stack-layout-depth-{recordLevels}"
            [ TNamed topType ]
            [ Call("mailbox.layout-depth", span ($"<layout-depth-{recordLevels}>") 1) ]
            compiled.Context.SourceOrigins
    { Program = compiled.Program
      Body = body
      RecordLevels = recordLevels
      LayoutLevels = recordLevels + 1
      TopTypeName = topType }

let private layoutDepthValue (layoutCase: LayoutDepthCase) =
    let mutable value = RecordValue("DepthNode0", Map.ofList [ "value", IntValue 42L ])
    for depth in 1 .. layoutCase.RecordLevels - 1 do
        value <- RecordValue($"DepthNode{depth}", Map.ofList [ "next", value ])
    value

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

let private optionalInt64 target name =
    let value = getProperty target name
    if isNull value then -1L else Convert.ToInt64(value, CultureInfo.InvariantCulture)

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

let private codeUnitsHexFromString (value: string) =
    let bytes = Array.zeroCreate<byte> (value.Length * 2)
    for index in 0 .. value.Length - 1 do
        let codeUnit = uint16 value[index]
        bytes[index * 2] <- byte (codeUnit &&& 0x00ffus)
        bytes[index * 2 + 1] <- byte (codeUnit >>> 8)
    bytesHex bytes

let rec private codeUnitSafeValueData (value: Value) : obj =
    let nested = codeUnitSafeValueData
    match value with
    | IntValue number -> jsonObject [ "type", box "Int"; "value", box number ]
    | FloatValue number -> jsonObject [ "type", box "Float"; "value", box number ]
    | BoolValue boolean -> jsonObject [ "type", box "Bool"; "value", box boolean ]
    | StringValue text ->
        jsonObject [
            "type", box "String"
            "codeUnitCount", box text.Length
            "codeUnitsHex", box (codeUnitsHexFromString text) ]
    | UnitValue -> jsonObject [ "type", box "Unit" ]
    | ListValue(elementType, items) ->
        jsonObject [
            "type", box "List"
            "elementType", box (sprintf "%A" elementType)
            "items", box (items |> List.map nested) ]
    | OptionValue(elementType, item) ->
        jsonObject [
            "type", box "Option"
            "elementType", box (sprintf "%A" elementType)
            "value", box (item |> Option.map nested) ]
    | ResultValue(okType, errorType, result) ->
        match result with
        | Ok item -> jsonObject [ "type", box "Result"; "okType", box (sprintf "%A" okType); "errorType", box (sprintf "%A" errorType); "ok", box (nested item) ]
        | Error item -> jsonObject [ "type", box "Result"; "okType", box (sprintf "%A" okType); "errorType", box (sprintf "%A" errorType); "error", box (nested item) ]
    | RecordValue(name, fields) ->
        jsonObject [
            "type", box "Record"
            "name", box name
            "fields", box (fields |> Map.toList |> List.map (fun (fieldName, fieldValue) -> jsonObject [ "name", box fieldName; "value", nested fieldValue ])) ]
    | EnumValue(name, variant) -> jsonObject [ "type", box "Enum"; "name", box name; "variant", box variant ]
    | NamedValue(name, inner) -> jsonObject [ "type", box "Named"; "name", box name; "value", nested inner ]

let private codeUnitSafeValuesData values = values |> List.map codeUnitSafeValueData

let private codeUnitSafeValuesJson values = JsonSerializer.Serialize(codeUnitSafeValuesData values)

let private codeUnitsFromHex (hex: string) =
    let bytes = bytesFromHex hex
    if bytes.Length % 2 <> 0 then invalidArg (nameof hex) "UTF-16 code-unit hex must contain complete little-endian code units."
    Array.init (bytes.Length / 2) (fun index -> char (uint16 bytes[index * 2] ||| (uint16 bytes[index * 2 + 1] <<< 8)))

let private stringFromCodeUnitsHex (hex: string) =
    String(codeUnitsFromHex hex)

let private stringBytesFromCodeUnitsHex (hex: string) =
    let data = bytesFromHex hex
    if data.Length % 2 <> 0 then invalidArg (nameof hex) "UTF-16 code-unit hex must contain complete little-endian code units."
    let count = uint32 (data.Length / 2)
    let payloadBytes = 8 + data.Length
    let extentBytes = (payloadBytes + 7) / 8 * 8
    let result = Array.zeroCreate<byte> extentBytes
    let countBytes = BitConverter.GetBytes(count)
    Array.Copy(countBytes, 0, result, 0, 4)
    Array.Copy(data, 0, result, 8, data.Length)
    result

let private textStateBytes count tag codeUnits stringCodeUnitsHex =
    Array.concat [
        bytesFromInt64s [ int64 count ]
        stringBytesFromCodeUnitsHex stringCodeUnitsHex
        bytesFromInt64s [ int64 codeUnits; int64 tag ] ]

let private textStateValue count tag codeUnits text =
    RecordValue("TextState", Map.ofList [
        "count", IntValue(int64 count)
        "last", RecordValue("TextEnvelope", Map.ofList [
            "leaf", RecordValue("TextLeaf", Map.ofList [
                "text", StringValue text
                "codeUnits", IntValue(int64 codeUnits) ])
            "tag", IntValue(int64 tag) ]) ])

let private textEnvelopeValue tag text codeUnits =
    RecordValue("TextEnvelope", Map.ofList [
        "leaf", RecordValue("TextLeaf", Map.ofList [
            "text", StringValue text
            "codeUnits", IntValue(int64 codeUnits) ])
        "tag", IntValue(int64 tag) ])

let private textStateFromCase (caseElement: JsonElement) =
    let mutable inputProperty = Unchecked.defaultof<JsonElement>
    let input = if caseElement.TryGetProperty("input", &inputProperty) then inputProperty else caseElement
    let codeUnitsHex = caseElement.GetProperty("inputCodeUnitsHex").GetString()
    let mutable countProperty = Unchecked.defaultof<JsonElement>
    let mutable tagProperty = Unchecked.defaultof<JsonElement>
    let mutable codeUnitsProperty = Unchecked.defaultof<JsonElement>
    let count = if input.TryGetProperty("count", &countProperty) then countProperty.GetInt64() else 10L
    let tag = if input.TryGetProperty("tag", &tagProperty) then tagProperty.GetInt64() else 70L
    let codeUnits = if input.TryGetProperty("codeUnits", &codeUnitsProperty) then codeUnitsProperty.GetInt64() else caseElement.GetProperty("inputCodeUnitCount").GetInt64()
    textStateValue count tag codeUnits (stringFromCodeUnitsHex codeUnitsHex)

let private textExpectedOutputs (caseElement: JsonElement) =
    let expected = caseElement.GetProperty("expectedOutputs").EnumerateArray() |> Seq.toArray
    let state = expected[0]
    let codeUnitsHex = caseElement.GetProperty("outputCodeUnitsHex").GetString()
    [ textStateValue
          (state.GetProperty("count").GetInt64())
          (state.GetProperty("tag").GetInt64())
          (state.GetProperty("codeUnits").GetInt64())
          (stringFromCodeUnitsHex codeUnitsHex)
      IntValue(expected[1].GetProperty("value").GetInt64()) ]

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

let private validateDynamicTypeLayouts (typeNames: string list) (fixture: JsonElement) (layouts: OwningStackTypeLayout list) =
    let oracle = fixture.GetProperty("stringWorkload").GetProperty("dynamicLayoutOracle")
    let checks = ResizeArray<obj>()
    let failures = ResizeArray<string>()
    for typeName in typeNames do
        let expectedPayload = oracle.GetProperty("minimumPayloadBytes").GetProperty(typeName).GetInt32()
        let expectedExtent = oracle.GetProperty("minimumExtentBytes").GetProperty(typeName).GetInt32()
        match layouts |> List.tryFind (fun layout -> String.Equals(layout.TypeName, typeName, StringComparison.Ordinal)) with
        | None ->
            checks.Add(box (jsonObject [ "type", box typeName; "present", box false ]))
            failures.Add($"Missing dynamic type layout for {typeName}.")
        | Some layout ->
            let dynamic = Convert.ToBoolean(getProperty (box layout) "IsDynamic", CultureInfo.InvariantCulture)
            let minimumPayload = int64Property (box layout) "MinimumPayloadBytes"
            let minimumExtent = int64Property (box layout) "MinimumExtentBytes"
            let payload = int64 layout.PayloadBytes
            let extent = int64 layout.ExtentBytes
            let passed = dynamic && payload = -1L && extent = -1L && minimumPayload = int64 expectedPayload && minimumExtent = int64 expectedExtent
            checks.Add(box (jsonObject [
                "type", box typeName
                "isDynamic", box dynamic
                "payloadBytes", box payload
                "extentBytes", box extent
                "expectedMinimumPayloadBytes", box expectedPayload
                "actualMinimumPayloadBytes", box minimumPayload
                "expectedMinimumExtentBytes", box expectedExtent
                "actualMinimumExtentBytes", box minimumExtent ]))
            if not passed then failures.Add($"{typeName} must expose dynamic sizes (-1) and minimum payload/extent {expectedPayload}/{expectedExtent}.")
    let expectedFields =
        oracle.GetProperty("fields").EnumerateObject()
        |> Seq.filter (fun property ->
            let separator = property.Name.IndexOf('.')
            separator > 0 && List.contains (property.Name.Substring(0, separator)) typeNames)
    for property in expectedFields do
        let key = property.Name
        let separator = key.IndexOf('.')
        let typeName = key.Substring(0, separator)
        let fieldName = key.Substring(separator + 1)
        let expected = property.Value
        match layouts |> List.tryFind (fun layout -> layout.TypeName = typeName) with
        | None -> failures.Add($"Field oracle owner {typeName} has no type layout.")
        | Some owner ->
            match owner.Fields |> List.tryFind (fun field -> field.FieldName = fieldName) with
            | None -> failures.Add($"Missing dynamic field layout for {key}.")
            | Some field ->
                let expectedDynamic = expected.GetProperty("isDynamic").GetBoolean()
                let expectedOffsetDynamic = expected.GetProperty("isOffsetDynamic").GetBoolean()
                let expectedMinimumOffset =
                    if expectedOffsetDynamic then expected.GetProperty("minimumOffsetBytes").GetInt32()
                    else
                        let mutable minimumOffset = Unchecked.defaultof<JsonElement>
                        if expected.TryGetProperty("minimumOffsetBytes", &minimumOffset) then minimumOffset.GetInt32()
                        else expected.GetProperty("offsetBytes").GetInt32()
                let expectedOffset = if expectedOffsetDynamic then -1 else expectedMinimumOffset
                let expectedMinimumPayload =
                    if expectedDynamic then expected.GetProperty("minimumPayloadBytes").GetInt32()
                    else expected.GetProperty("payloadBytes").GetInt32()
                let expectedMinimumExtent =
                    if expectedDynamic then expected.GetProperty("minimumExtentBytes").GetInt32()
                    else expected.GetProperty("extentBytes").GetInt32()
                let dynamic = Convert.ToBoolean(getProperty (box field) "IsDynamic", CultureInfo.InvariantCulture)
                let offsetDynamic = Convert.ToBoolean(getProperty (box field) "IsOffsetDynamic", CultureInfo.InvariantCulture)
                let minimumPayload = int64Property (box field) "MinimumPayloadBytes"
                let minimumExtent = int64Property (box field) "MinimumExtentBytes"
                let expectedPayload = if expectedDynamic then -1 else expected.GetProperty("payloadBytes").GetInt32()
                let expectedExtent = if expectedDynamic then -1 else expected.GetProperty("extentBytes").GetInt32()
                let passed =
                    dynamic = expectedDynamic
                    && offsetDynamic = expectedOffsetDynamic
                    && field.OffsetBytes = expectedOffset
                    && field.PayloadBytes = expectedPayload
                    && field.ExtentBytes = expectedExtent
                    && minimumPayload = int64 expectedMinimumPayload
                    && minimumExtent = int64 expectedMinimumExtent
                checks.Add(box (jsonObject [
                    "field", box key
                    "isDynamic", box dynamic
                    "expectedIsDynamic", box expectedDynamic
                    "isOffsetDynamic", box offsetDynamic
                    "expectedIsOffsetDynamic", box expectedOffsetDynamic
                    "offsetBytes", box field.OffsetBytes
                    "expectedOffsetBytes", box expectedOffset
                    "payloadBytes", box field.PayloadBytes
                    "extentBytes", box field.ExtentBytes
                    "minimumPayloadBytes", box minimumPayload
                    "minimumExtentBytes", box minimumExtent ]))
                if not passed then failures.Add($"{key} dynamic metadata does not match the independent layout oracle.")
    let summary =
        layouts
        |> List.filter (fun layout -> List.contains layout.TypeName typeNames)
        |> List.map (fun layout ->
            box (jsonObject [
                "type", box layout.TypeName
                "isDynamic", box (getProperty (box layout) "IsDynamic")
                "payloadBytes", box layout.PayloadBytes
                "extentBytes", box layout.ExtentBytes
                "minimumPayloadBytes", box (getProperty (box layout) "MinimumPayloadBytes")
                "minimumExtentBytes", box (getProperty (box layout) "MinimumExtentBytes")
                "fields", box (layout.Fields |> List.map (fun field -> jsonObject [
                    "name", box field.FieldName
                    "isDynamic", box (getProperty (box field) "IsDynamic")
                    "isOffsetDynamic", box (getProperty (box field) "IsOffsetDynamic")
                    "offsetBytes", box field.OffsetBytes
                    "payloadBytes", box field.PayloadBytes
                    "extentBytes", box field.ExtentBytes
                    "minimumPayloadBytes", box (getProperty (box field) "MinimumPayloadBytes")
                    "minimumExtentBytes", box (getProperty (box field) "MinimumExtentBytes") ])) ]))
        |> List.toArray
    checks.ToArray(), failures.ToArray(), summary

let private layoutEventKind (event: OwningStackLayoutEvent) =
    if event.Kind = "duplicate" then 2L
    elif event.Kind = "drop" then 3L
    elif event.Kind = "call-input-move" then 8L
    elif event.Kind = "call-return-move" then 9L
    elif event.Kind = "retained-copy" then 10L
    elif event.Kind = "local-compact" then 14L
    elif event.Kind = "string-concat-left" then 15L
    elif event.Kind = "string-concat-right" then 16L
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

let private layoutEventDetails (events: OwningStackLayoutEvent list) =
    events
    |> List.map (fun event ->
        jsonObject [
            "kind", box event.Kind
            "kindCode", box (layoutEventKind event)
            "typeId", box event.TypeId
            "offsetBytes", box event.OffsetBytes
            "extentBytes", box event.ExtentBytes
            "payloadBytes", box event.PayloadBytes
            "sourceOffsetBytes", box (event.SourceOffsetBytes |> Option.map box |> Option.defaultValue null)
            "sourceExtentBytes", box (event.SourceExtentBytes |> Option.map box |> Option.defaultValue null)
            "checksum", box (event.Checksum |> Option.map box |> Option.defaultValue null) ])
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

let private inspectDynamicScopeShadow (program: VerifiedIrProgram) (verifiedBody: VerifiedIrBody) =
    let programData = VerifiedIrProgram.inspect program
    let body = VerifiedIrBody.inspect verifiedBody
    let slot =
        body.BodyLocalNames
        |> Map.toSeq
        |> Seq.tryPick (fun (slot, name) -> if name = "shadow" then Some slot else None)
    let envelopeKey =
        programData.NominalTypesByKey
        |> Map.toSeq
        |> Seq.tryPick (fun (key, definition) ->
            match definition with
            | IrRecordDefinition record when record.TypeName = "TextEnvelope" -> Some key
            | _ -> None)
    match slot, envelopeKey with
    | Some shadowSlot, Some key ->
        let scopeBlock =
            body.BodyBlock.Code
            |> List.tryPick (fun instruction -> match instruction.Operation with | IrOperation.Scope inner -> Some inner | _ -> None)
        match scopeBlock with
        | Some inner ->
            let entryType = inner.EntryShape.LocalTypes.TryFind shadowSlot
            let innerType = inner.ExitShape.LocalTypes.TryFind shadowSlot
            let restoredType = body.BodyBlock.ExitShape.LocalTypes.TryFind shadowSlot
            let innerStoreUsesSlot = inner.Code |> List.exists (fun instruction -> match instruction.Operation with | IrOperation.StoreLocal slot -> slot = shadowSlot | _ -> false)
            let outerStoreUsesSlot = body.BodyBlock.Code |> List.exists (fun instruction -> match instruction.Operation with | IrOperation.StoreLocal slot -> slot = shadowSlot | _ -> false)
            let expectedType = Some(IrNominal key)
            let passed =
                innerStoreUsesSlot
                && outerStoreUsesSlot
                && entryType = expectedType
                && innerType = expectedType
                && restoredType = expectedType
            passed, jsonObject [
                "slotFound", box true
                "slot", box (sprintf "%A" shadowSlot)
                "outerStoreUsesSameSlot", box outerStoreUsesSlot
                "innerStoreUsesSameSlot", box innerStoreUsesSlot
                "scopeEntryType", box "TextEnvelope"
                "scopeInnerExitType", box "TextEnvelope"
                "afterScopeType", box "TextEnvelope"
                "sameLogicalSlot", box (innerStoreUsesSlot && outerStoreUsesSlot)
                "widthsAreRuntimeDependent", box true ]
        | None -> false, jsonObject [ "slotFound", box true; "scopeBlockFound", box false ]
    | _ -> false, jsonObject [ "slotFound", box slot.IsSome; "textEnvelopeTypeFound", box envelopeKey.IsSome ]

let private diagnosticCode (error: exn) =
    match error with
    | LanguageException diagnostic -> diagnostic.Code
    | _ ->
        match getProperty error "Diagnostic" with
        | null -> ""
        | diagnostic -> getProperty diagnostic "Code" |> string

let private exceptionCode (error: exn) =
    let diagnostic = diagnosticCode error
    if not (String.IsNullOrWhiteSpace diagnostic) then diagnostic
    else
        match getProperty error "Code" with
        | null -> ""
        | code -> string code

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
        use emptyIdentity = IrInterpreter.executeBodyWithInputs interpreterHost "native-value-stack-empty-record-identity" entries.EmptyIdentity (Some emptyResult) [ IrEntryArgument.RetainedRoot 0 ]
        recordCheck checks failures "interpreter/empty-record-zero-token-identity" (emptyIdentity.Decode() = emptyExpected) (ValueInspection.toJson entries.Program (emptyIdentity.Decode()))
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
        use emptyIdentityProgram = compile "empty-identity" entries.EmptyIdentity
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
        use emptyIdentityResult = emptyIdentityProgram.ExecuteRetainedWithInputs("native-value-stack-empty-record-identity", Some emptyResult, [ IrEntryArgument.RetainedRoot 0 ], options = nativeOptions 8 1 8 1)
        let emptyIdentityValues = emptyIdentityResult.Decode()
        let abi3EmptyBytes = fixedCases.GetProperty("emptyRecord").GetProperty("abi3RetainedBytes").GetInt32()
        let abi3EmptyNodes = fixedCases.GetProperty("emptyRecord").GetProperty("abi3RetainedNodes").GetInt32()
        recordCheck checks failures $"abi3/{optimization}/empty-record-zero-width-identity" (emptyIdentityValues = emptyExpected && emptyIdentityResult.RetainedByteCount = abi3EmptyBytes && emptyIdentityResult.RetainedNodeCount = abi3EmptyNodes) (jsonObject [
            "values", box (ValueInspection.toJson entries.Program emptyIdentityValues)
            "retainedBytes", box emptyIdentityResult.RetainedByteCount
            "retainedNodes", box emptyIdentityResult.RetainedNodeCount
            "note", box "ABI3 encodes a zero-width Empty value as one retained node and zero retained bytes; the owning candidate separately uses an eight-byte token extent." ])
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
        "hostInputStagingBytes", box (int64Property metrics "HostInputStagingBytes")
        "hostEncodedInputBytes", box (int64Property metrics "HostEncodedInputBytes")
        "hostInputExtentTableBytes", box (int64Property metrics "HostInputExtentTableBytes")
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
        "backendMetadataPerFrameBytes", box (int64Property metrics "BackendMetadataPerFrameBytes")
        "backendMetadataPeakBoundBytes", box (int64Property metrics "BackendMetadataPeakBoundBytes")
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
        let required = int64Property error "RequiredBytes"
        let available = int64Property error "AvailableBytes"
        let passed = code = "OWNING_STACK_CAPACITY" && boundary = "host-input-encoding" && required = 32L && available = 1L && finalLive = 0L && finalCursor = 0L && unchanged
        recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/host-input-encoding-capacity-buffer-atomicity" passed (jsonObject [
            "exception", box (resourceExceptionDetails error)
            "requiredInputBytes", box required
            "availableStackCapacityBytes", box available
            "finalLiveStackBytes", box finalLive
            "finalCursorBytes", box finalCursor
            "callerBuffer", bufferCheckDetails stackFailureBefore stackFailureBuffer ])
    | None -> recordCheck checks failures $"owning-stack/{optimization}/N={repetition}/host-input-encoding-capacity-buffer-atomicity" false "One-byte stack capacity unexpectedly accepted the 32-byte host input encoding."
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
        use emptyIdentityProgram = compile "empty-identity" entries.EmptyIdentity
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
        let emptyIdentityOutput = Array.create 8 0xA5uy
        let emptyIdentityResult = emptyIdentityProgram.ExecuteInto([ RecordValue("Empty", Map.empty) ], stackCapacity, emptyIdentityOutput)
        let emptyIdentityMetrics = box emptyIdentityResult.Metrics
        let emptyIdentityPassed =
            emptyIdentityResult.Values = emptyExpected
            && emptyIdentityOutput = emptyExpectedBytes
            && emptyIdentityResult.RetainedBytesWritten = 8
            && int64Property emptyIdentityMetrics "InputBytes" = 8L
            && int64Property emptyIdentityMetrics "InputCopyBytes" = 8L
        recordCheck checks failures $"owning-stack/{optimization}/empty-record-value-input-and-zero-token-decode" emptyIdentityPassed (jsonObject [
            "values", box (ValueInspection.toJson entries.Program emptyIdentityResult.Values)
            "inputBytes", box (int64Property emptyIdentityMetrics "InputBytes")
            "inputCopyBytes", box (int64Property emptyIdentityMetrics "InputCopyBytes")
            "retainedBytes", box (bytesHex emptyIdentityOutput)
            "metrics", box (jsonNode options (metricSummary emptyIdentityMetrics)) ])
        checkTraceUsable checks failures $"owning-stack/{optimization}/empty-record-identity-trace-complete" emptyIdentityResult.Metrics emptyIdentityResult.LayoutEvents |> ignore
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

let private textStateMetadata (value: Value) =
    match value with
    | RecordValue("TextState", state) ->
        match state["count"], state["last"] with
        | IntValue count, RecordValue("TextEnvelope", envelope) ->
            match envelope["tag"], envelope["leaf"] with
            | IntValue tag, RecordValue("TextLeaf", leaf) ->
                match leaf["text"], leaf["codeUnits"] with
                | StringValue text, IntValue codeUnits -> count, tag, codeUnits, text
                | _ -> invalidOp "TextState value contains invalid TextLeaf field values."
            | _ -> invalidOp "TextState value contains an invalid TextEnvelope."
        | _ -> invalidOp "TextState value contains invalid count or last fields."
    | _ -> invalidOp "Expected a TextState value."

let private textEnvelopeFromState (value: Value) =
    match value with
    | RecordValue("TextState", state) -> state["last"]
    | _ -> invalidOp "Expected a TextState value."

let private textEnvelopeBytes (tag: int64) (codeUnits: int64) stringCodeUnitsHex =
    Array.concat [ stringBytesFromCodeUnitsHex stringCodeUnitsHex; bytesFromInt64s [ codeUnits; tag ] ]

let private runStringWorkload
    (checks: ResizeArray<obj>)
    (failures: ResizeArray<string>)
    (options: JsonSerializerOptions)
    (fixture: JsonElement)
    (entries: StringEntryBodies)
    (artifactRoot: string)
    (optimization: LlvmOptimization)
    (optimizationName: string) =
    let workload = fixture.GetProperty("stringWorkload")
    let eventTypeIdOracle = workload.GetProperty("dynamicLayoutOracle").GetProperty("eventTypeIds")
    let nominalTypeIds =
        VerifiedIrProgram.inspect entries.Program
        |> fun program -> program.NominalTypesByKey
        |> Map.toList
        |> List.mapi (fun index (_, definition) ->
            let typeName =
                match definition with
                | IrRecordDefinition record -> record.TypeName
                | IrScalarDefinition scalar -> scalar.TypeName
                | IrEnumDefinition enum -> enum.TypeName
            typeName, uint32 (index + 4))
        |> Map.ofList
    let stringTypeId = uint32 (nominalTypeIds.Count + 4)
    let textEnvelopeTypeId =
        nominalTypeIds.TryFind "TextEnvelope"
        |> Option.defaultWith (fun () -> invalidOp "Verified String program is missing TextEnvelope's nominal type ID.")
    let emptyTypeId =
        nominalTypeIds.TryFind "Empty"
        |> Option.defaultWith (fun () -> invalidOp "Verified String program is missing Empty's nominal type ID.")
    let textMixedTypeId =
        nominalTypeIds.TryFind "TextMixed"
        |> Option.defaultWith (fun () -> invalidOp "Verified String program is missing TextMixed's nominal type ID.")
    let intTypeId = 1u
    let expectedStringTypeId = eventTypeIdOracle.GetProperty("String").GetUInt32()
    let expectedTextEnvelopeTypeId = eventTypeIdOracle.GetProperty("TextEnvelope").GetUInt32()
    let expectedEmptyTypeId = eventTypeIdOracle.GetProperty("Empty").GetUInt32()
    let expectedTextMixedTypeId = eventTypeIdOracle.GetProperty("TextMixed").GetUInt32()
    let expectedIntTypeId = eventTypeIdOracle.GetProperty("Int").GetUInt32()
    let eventTypeIdsMatch =
        stringTypeId = expectedStringTypeId
        && textEnvelopeTypeId = expectedTextEnvelopeTypeId
        && emptyTypeId = expectedEmptyTypeId
        && textMixedTypeId = expectedTextMixedTypeId
        && intTypeId = expectedIntTypeId
    recordCheck checks failures "oracle/String/runtime-event-type-ids" eventTypeIdsMatch (jsonObject [
        "verifiedProgramNominalTypeCount", box nominalTypeIds.Count
        "expectedStringTypeId", box expectedStringTypeId
        "actualStringTypeId", box stringTypeId
        "expectedTextEnvelopeTypeId", box expectedTextEnvelopeTypeId
        "actualTextEnvelopeTypeId", box textEnvelopeTypeId
        "expectedEmptyTypeId", box expectedEmptyTypeId
        "actualEmptyTypeId", box emptyTypeId
        "expectedTextMixedTypeId", box expectedTextMixedTypeId
        "actualTextMixedTypeId", box textMixedTypeId
        "expectedIntTypeId", box expectedIntTypeId
        "actualIntTypeId", box intTypeId ])
    let mainCases = workload.GetProperty("mainCases").EnumerateArray() |> Seq.toArray
    let edgeCases = workload.GetProperty("nestedEdgeCases").EnumerateArray() |> Seq.toArray
    let cases = Array.append mainCases edgeCases
    let interpreterHost = noOpHost (NativeDiagnosticSources.fromLoweringContext entries.CompilerContext)
    let compiledInputBodies =
        cases
        |> Array.map (fun caseElement ->
            let name = caseElement.GetProperty("name").GetString()
            let inputValue = textStateFromCase caseElement
            name, (inputValue, compileTextStateFactory entries name inputValue))
        |> Map.ofArray
    let interpreterReports = ResizeArray<obj>()
    for caseElement in cases do
        let name = caseElement.GetProperty("name").GetString()
        let inputValue, inputFactory =
            match compiledInputBodies[name] with
            | value, body -> value, body
        let expectedValues = textExpectedOutputs caseElement
        use inputOwner = IrInterpreter.executeBodyWithInputs interpreterHost ("string-input-" + name) inputFactory None []
        use actual = IrInterpreter.executeBodyWithInputs interpreterHost "mailbox.turn-text" entries.TurnText (Some inputOwner) [ IrEntryArgument.RetainedRoot 0 ]
        let inputCount, inputTag, inputCodeUnits, _ = textStateMetadata inputValue
        let inputHex = caseElement.GetProperty("inputCodeUnitsHex").GetString()
        let output = expectedValues[0]
        let outputCount, outputTag, outputCodeUnits, _ = textStateMetadata output
        let outputHex = caseElement.GetProperty("outputCodeUnitsHex").GetString()
        let inputOracle = textStateBytes inputCount inputTag inputCodeUnits inputHex
        let outputStateOracle = textStateBytes outputCount outputTag outputCodeUnits outputHex
        let expectedOutputLength = match expectedValues[1] with | IntValue value -> value | _ -> invalidOp "String fixture length output must be Int."
        let expectedRetained = Array.concat [ outputStateOracle; bytesFromInt64s [ expectedOutputLength ] ]
        let fixtureInputBytes = bytesFromHex (caseElement.GetProperty("inputStateBytesHex").GetString())
        let fixtureOutputBytes = bytesFromHex (caseElement.GetProperty("retainedBytesHex").GetString())
        let values = actual.Decode()
        recordCheck checks failures $"interpreter/String/{name}/same-verified-body-output" (values = expectedValues) (codeUnitSafeValuesJson values)
        recordCheck checks failures $"oracle/String/{name}/input-inline-UTF16-bytes" (inputOracle = fixtureInputBytes) (jsonObject [
            "oracleHex", box (bytesHex inputOracle)
            "fixtureHex", box (bytesHex fixtureInputBytes)
            "codeUnitCount", box inputCodeUnits ])
        recordCheck checks failures $"oracle/String/{name}/output-inline-UTF16-bytes" (expectedRetained = fixtureOutputBytes) (jsonObject [
            "oracleHex", box (bytesHex expectedRetained)
            "fixtureHex", box (bytesHex fixtureOutputBytes)
            "outputCodeUnitCount", box (caseElement.GetProperty("outputCodeUnitCount").GetInt32()) ])
        interpreterReports.Add(box (jsonObject [
            "case", box name
            "inputValue", jsonNode options (codeUnitSafeValuesData [ inputValue ])
            "values", jsonNode options (codeUnitSafeValuesData values)
            "inputStateBytes", box (bytesHex fixtureInputBytes)
            "retainedBytes", box (bytesHex fixtureOutputBytes) ]))

    let toolchain = LlvmToolchain.discover ()
    let compile name body =
        OwningStackAot.compile toolchain optimization (Path.Combine(artifactRoot, "owning-stack", "strings", optimizationName, name)) body
    use turnProgram = compile "turn-text" entries.TurnText
    use identityProgram = compile "identity-string" entries.IdentityString
    use joinProgram = compile "join-strings" entries.JoinStrings
    use directConcatProgram = compile "direct-concat-capacity" entries.DirectConcat
    use mixedConstructProgram = compile "mixed-record-construct" entries.MixedConstruct
    use mixedProjectProgram = compile "mixed-record-project-text-and-sentinel" entries.MixedProjectTextAndSentinel
    use mixedProjectEmptyProgram = compile "mixed-record-project-empty-marker" entries.MixedProjectEmptyMarker
    use dynamicBranchProgram = compile "dynamic-record-branch-join" entries.DynamicBranch
    use duplicateProgram = compile "text-envelope-dup-drop" entries.DirectDupDrop
    use scopeProgram = compile "text-envelope-scope-shadow" entries.ScopeShadow
    use zeroOutputProgram = compile "zero-output-user-call" entries.ZeroOutputUserCall
    use failProgram = compile "fail-after-text-allocation" entries.FailAfterTextAllocation
    let owningReports = ResizeArray<obj>()
    let metricsByCase = Dictionary<string, OwningStackMetrics>(StringComparer.Ordinal)
    let mutable dynamicLayouts = []
    let mutable layoutSchemaVersion = -1L
    let stateCapacity = 4096
    for caseElement in cases do
        let name = caseElement.GetProperty("name").GetString()
        let inputValue, _ = compiledInputBodies[name]
        let expectedValues = textExpectedOutputs caseElement
        let outputBytes = bytesFromHex (caseElement.GetProperty("retainedBytesHex").GetString())
        let callerOutput = Array.create outputBytes.Length 0xA5uy
        let result = turnProgram.ExecuteInto([ inputValue ], stateCapacity, callerOutput)
        let actualValues = getProperty (box result) "Values" :?> Value list
        let metrics = stackMetrics (box result) :?> OwningStackMetrics
        let events = getProperty (box result) "LayoutEvents" :?> OwningStackLayoutEvent list
        if name = mainCases[0].GetProperty("name").GetString() then
            dynamicLayouts <- getProperty (box result) "Layouts" :?> OwningStackTypeLayout list
            layoutSchemaVersion <- int64Property (box result) "LayoutSchemaVersion"
        metricsByCase.Add(name, metrics)
        recordCheck checks failures $"owning-stack/{optimizationName}/String/{name}/same-verified-body-output" (actualValues = expectedValues) (codeUnitSafeValuesJson actualValues)
        recordCheck checks failures $"owning-stack/{optimizationName}/String/{name}/caller-retained-bytes" (callerOutput = outputBytes && (getProperty (box result) "RetainedOutputBytes" :?> byte array) = outputBytes) (bufferCheckDetails outputBytes callerOutput)
        let inputStateExtent = caseElement.GetProperty("inputStateBytes").GetInt32()
        let outputStateExtent = outputBytes.Length - 8
        let returnedLength = int64FromBytesLittleEndian outputBytes outputStateExtent
        let expectedLength = match expectedValues[1] with | IntValue value -> value | _ -> invalidOp "String fixture length output must be Int."
        let multiOutputPass = result.RetainedBytesWritten = outputBytes.Length && returnedLength = expectedLength && outputStateExtent + 8 = outputBytes.Length && (name <> "long-bmp-astral" || outputStateExtent > inputStateExtent)
        recordCheck checks failures $"owning-stack/{optimizationName}/String/{name}/multi-output-range-and-growth" multiOutputPass (jsonObject [
            "inputStateExtentBytes", box inputStateExtent
            "outputStateExtentBytes", box outputStateExtent
            "retainedBytesWritten", box result.RetainedBytesWritten
            "secondOutputAtByteOffset", box outputStateExtent
            "secondOutputValue", box returnedLength ])
        let inputValueBytes = bytesFromHex (caseElement.GetProperty("inputStateBytesHex").GetString())
        let inputBytes = int64Property metrics "InputBytes"
        let inputCopyBytes = int64Property metrics "InputCopyBytes"
        let hostEncodedInputBytes = int64Property metrics "HostEncodedInputBytes"
        let exactInputCopy = inputBytes = int64 inputValueBytes.Length && inputCopyBytes = int64 inputValueBytes.Length && hostEncodedInputBytes = int64 inputValueBytes.Length
        recordCheck checks failures $"owning-stack/{optimizationName}/String/{name}/dynamic-value-input-marshaling" exactInputCopy (jsonObject [
            "oracleInputBytes", box inputValueBytes.Length
            "inputBytes", box inputBytes
            "inputCopyBytes", box inputCopyBytes
            "hostEncodedInputBytes", box hostEncodedInputBytes ])
        let inputCount = caseElement.GetProperty("inputCodeUnitCount").GetInt32()
        let outputCount = caseElement.GetProperty("outputCodeUnitCount").GetInt32()
        let inputStringExtent = caseElement.GetProperty("inputStringExtentBytes").GetInt32()
        let outputStringExtent = caseElement.GetProperty("outputStringExtentBytes").GetInt32()
        let fieldExtractEvents = events |> List.filter (fun event -> event.Kind = "field-extract")
        let stringFieldExtractEvents = fieldExtractEvents |> List.filter (fun event -> event.TypeId = stringTypeId)
        let inputStringPayload = 8 + 2 * inputCount
        let outputStringPayload = 8 + 2 * outputCount
        let align8 bytes = ((bytes + 7) / 8) * 8
        let inputStringExtentFromOracle = align8 inputStringPayload
        let outputStringExtentFromOracle = align8 outputStringPayload
        let inputStringEvent =
            inputStringExtent = inputStringExtentFromOracle
            && (stringFieldExtractEvents |> List.exists (fun event -> event.PayloadBytes = inputStringPayload && event.ExtentBytes = inputStringExtent))
        let outputStringEvent =
            outputStringExtent = outputStringExtentFromOracle
            && (stringFieldExtractEvents |> List.exists (fun event -> event.PayloadBytes = outputStringPayload && event.ExtentBytes = outputStringExtent))
        recordCheck checks failures $"owning-stack/{optimizationName}/String/{name}/runtime-string-field-extents" (inputStringEvent && outputStringEvent) (jsonObject [
            "stringTypeId", box stringTypeId
            "inputStringPayloadBytes", box inputStringPayload
            "inputStringExtentBytes", box inputStringExtent
            "inputStringExtentFromIndependentFormula", box inputStringExtentFromOracle
            "inputInstanceFieldEventFound", box inputStringEvent
            "outputStringPayloadBytes", box outputStringPayload
            "outputStringExtentBytes", box outputStringExtent
            "outputStringExtentFromIndependentFormula", box outputStringExtentFromOracle
            "outputInstanceFieldEventFound", box outputStringEvent
            "stringFieldExtractEvents", box (layoutEventDetails stringFieldExtractEvents) ])
        let concatEventPairs =
            let indexedEvents = events |> List.indexed |> List.toArray
            if indexedEvents.Length < 2 then
                [||]
            else
                [| for index in 0 .. indexedEvents.Length - 2 do
                       let _, left = indexedEvents[index]
                       let _, right = indexedEvents[index + 1]
                       if left.Kind = "string-concat-left"
                          && right.Kind = "string-concat-right"
                          && left.TypeId = stringTypeId
                          && right.TypeId = stringTypeId then
                           yield left, right |]
        let outputStringDataBytes = 2 * outputCount
        let outputConcatSpansMatch =
            concatEventPairs
            |> Array.exists (fun (left, right) ->
                left.OffsetBytes >= 8
                && left.ExtentBytes > 0
                && right.ExtentBytes > 0
                && left.PayloadBytes = left.ExtentBytes
                && right.PayloadBytes = right.ExtentBytes
                && left.SourceExtentBytes = Some left.ExtentBytes
                && right.SourceExtentBytes = Some right.ExtentBytes
                && right.OffsetBytes = left.OffsetBytes + left.ExtentBytes
                && left.ExtentBytes + right.ExtentBytes = outputStringDataBytes)
        recordCheck checks failures $"owning-stack/{optimizationName}/String/{name}/runtime-string-concat-output-spans" outputConcatSpansMatch (jsonObject [
            "stringTypeId", box stringTypeId
            "expectedOutputCodeUnitDataBytes", box outputStringDataBytes
            "concatSourceSpans", box (concatEventPairs |> Array.collect (fun (left, right) -> [| left; right |]) |> Array.toList |> layoutEventDetails) ])
        let textLeafExtent = inputStringExtent + 8
        let expectedTagOffsetWithinEnvelope = caseElement.GetProperty("inputTagOffsetWithinEnvelopeBytes").GetInt32()
        let tagOffsetOracleMatches = textLeafExtent = expectedTagOffsetWithinEnvelope
        let expectedEnvelopePayload = inputStringPayload + 16
        let expectedEnvelopeExtent = align8 expectedEnvelopePayload
        let indexedEvents = events |> List.indexed |> List.toArray
        let rangeOverlaps leftStart leftExtent rightStart rightExtent =
            leftExtent > 0
            && rightExtent > 0
            && int64 leftStart < int64 rightStart + int64 rightExtent
            && int64 rightStart < int64 leftStart + int64 leftExtent
        let mutationRanges event =
            let destination = [ event.OffsetBytes, event.ExtentBytes ]
            let source =
                match event.SourceOffsetBytes, event.SourceExtentBytes with
                | Some offset, Some extent -> [ offset, extent ]
                | _ -> []
            match event.Kind with
            | "allocate" | "duplicate" | "local-load" | "drop" | "scope-clear" | "string-concat-left" | "string-concat-right" -> destination
            | "local-store" | "record-build" | "field-extract" | "call-input-move" | "call-return-move" | "local-compact" -> destination @ source
            | _ -> []
        let tagCandidates =
            indexedEvents
            |> Array.choose (fun (tagIndex, event) ->
                let isTagField =
                    event.Kind = "field-extract"
                    && event.TypeId = intTypeId
                    && event.ExtentBytes = 8
                    && event.PayloadBytes = 8
                    && event.SourceExtentBytes = Some 8
                    && event.SourceOffsetBytes = Some(event.OffsetBytes + expectedTagOffsetWithinEnvelope)
                if not isTagField then
                    None
                else
                    let parentLoads =
                        indexedEvents
                        |> Array.filter (fun (loadIndex, load) ->
                            loadIndex < tagIndex
                            && load.Kind = "local-load"
                            && load.TypeId = textEnvelopeTypeId
                            && load.OffsetBytes = event.OffsetBytes
                            && load.ExtentBytes = expectedEnvelopeExtent
                            && load.PayloadBytes = expectedEnvelopePayload)
                    if parentLoads.Length = 0 then
                        None
                    else
                        let parentLoadIndex, parentLoad = parentLoads[parentLoads.Length - 1]
                        let invalidatingEvents =
                            indexedEvents
                            |> Array.choose (fun (index, intervening) ->
                                if index <= parentLoadIndex || index >= tagIndex then
                                    None
                                else
                                    let mutatesOwner =
                                        mutationRanges intervening
                                        |> List.exists (fun (offset, extent) -> rangeOverlaps offset extent event.OffsetBytes expectedEnvelopeExtent)
                                    if mutatesOwner then Some(index, intervening) else None)
                        Some(tagIndex, event, parentLoadIndex, parentLoad, invalidatingEvents))
        let validTagCandidates = tagCandidates |> Array.filter (fun (_, _, _, _, invalidating) -> invalidating.Length = 0)
        let dynamicTagFound = tagOffsetOracleMatches && validTagCandidates.Length > 0
        let tagEvidence =
            validTagCandidates
            |> Array.map (fun (tagIndex, tagEvent, loadIndex, loadEvent, _) ->
                jsonObject [
                    "tagExtractionIndex", box tagIndex
                    "parentLoadIndex", box loadIndex
                    "parentLoad", box (layoutEventDetails [ loadEvent ])
                    "tagExtraction", box (layoutEventDetails [ tagEvent ]) ])
        let rejectedTagEvidence =
            tagCandidates
            |> Array.filter (fun (_, _, _, _, invalidating) -> invalidating.Length > 0)
            |> Array.map (fun (_, _, _, _, invalidating) ->
                invalidating
                |> Array.map (fun (index, event) -> jsonObject [ "index", box index; "event", box (layoutEventDetails [ event ]) ]))
        recordCheck checks failures $"owning-stack/{optimizationName}/String/{name}/runtime-nested-dynamic-tag-offset" dynamicTagFound (jsonObject [
            "textEnvelopeTypeId", box textEnvelopeTypeId
            "intTypeId", box intTypeId
            "stringExtentBytes", box inputStringExtent
            "expectedTextLeafExtentBytes", box textLeafExtent
            "expectedTagOffsetWithinEnvelopeBytes", box expectedTagOffsetWithinEnvelope
            "tagOffsetOracleMatchesIndependentLiteral", box tagOffsetOracleMatches
            "expectedEnvelopePayloadBytes", box expectedEnvelopePayload
            "expectedEnvelopeExtentBytes", box expectedEnvelopeExtent
            "correlatedParentAndTagEvents", box tagEvidence
            "tagCandidatesRejectedForInterveningOwnerMutation", box rejectedTagEvidence ])
        checkTraceUsable checks failures $"owning-stack/{optimizationName}/String/{name}/trace-complete" metrics events |> ignore
        owningReports.Add(box (jsonObject [
            "case", box name
            "values", jsonNode options (codeUnitSafeValuesData actualValues)
            "inputBytes", box (bytesHex inputValueBytes)
            "retainedBytes", box (bytesHex outputBytes)
            "metrics", jsonNode options (metricSummary metrics)
            "layoutEvents", jsonNode options (layoutEventDetails events) ]))

    recordCheck checks failures $"owning-stack/{optimizationName}/String/layout-schema-v2" (layoutSchemaVersion = 2L) (jsonObject [ "expected", box 2; "actual", box layoutSchemaVersion ])
    let dynamicLayoutChecks, dynamicLayoutFailures, dynamicLayoutSummary =
        validateDynamicTypeLayouts [ "String"; "TextLeaf"; "TextEnvelope"; "TextState" ] fixture dynamicLayouts
    for details in dynamicLayoutChecks do checks.Add details
    for failure in dynamicLayoutFailures do failures.Add($"owning-stack/{optimizationName}/String/dynamic-layout: {failure}")
    recordCheck checks failures $"owning-stack/{optimizationName}/String/dynamic-layout-contract" (dynamicLayoutFailures.Length = 0) (jsonNode options dynamicLayoutSummary)

    let roundTripReports = ResizeArray<obj>()
    for roundTrip in workload.GetProperty("directValueStringRoundTrips").EnumerateArray() do
        let name = roundTrip.GetProperty("name").GetString()
        let codeUnitsHex = roundTrip.GetProperty("inputCodeUnitsHex").GetString()
        let value = StringValue(stringFromCodeUnitsHex codeUnitsHex)
        let expectedBytes = bytesFromHex (roundTrip.GetProperty("stringBytesHex").GetString())
        let independentBytes = stringBytesFromCodeUnitsHex codeUnitsHex
        recordCheck checks failures $"oracle/String/{name}/direct-value-string-buffer" (independentBytes = expectedBytes && expectedBytes.Length = roundTrip.GetProperty("extentBytes").GetInt32()) (jsonObject [
            "codeUnitCount", box (roundTrip.GetProperty("inputCodeUnitCount").GetInt32())
            "oracleHex", box (bytesHex independentBytes)
            "fixtureHex", box (bytesHex expectedBytes)
            "extentBytes", box expectedBytes.Length ])
        let factory = compileStringFactory entries ("roundtrip-" + name) (stringFromCodeUnitsHex codeUnitsHex)
        use interpreterOwner = IrInterpreter.executeBodyWithInputs interpreterHost ("string-roundtrip-input-" + name) factory None []
        use interpreterResult = IrInterpreter.executeBodyWithInputs interpreterHost "mailbox.identity-string" entries.IdentityString (Some interpreterOwner) [ IrEntryArgument.RetainedRoot 0 ]
        let interpreterValues = interpreterResult.Decode()
        recordCheck checks failures $"interpreter/String/{name}/direct-value-roundtrip" (interpreterValues = [ value ]) (codeUnitSafeValuesJson interpreterValues)
        let callerOutput = Array.create expectedBytes.Length 0xA5uy
        let result = identityProgram.ExecuteInto([ value ], stateCapacity, callerOutput)
        let resultValues = getProperty (box result) "Values" :?> Value list
        let metrics = stackMetrics (box result)
        let inputBytes = int64Property metrics "InputBytes"
        let encodedBytes = int64Property metrics "HostEncodedInputBytes"
        let passed =
            resultValues = [ value ]
            && callerOutput = expectedBytes
            && (getProperty (box result) "RetainedOutputBytes" :?> byte array) = expectedBytes
            && int64Property metrics "RetainedCopyBytes" = int64 expectedBytes.Length
            && inputBytes = int64 expectedBytes.Length
            && encodedBytes = int64 expectedBytes.Length
        recordCheck checks failures $"owning-stack/{optimizationName}/String/{name}/direct-value-roundtrip" passed (jsonObject [
            "values", box (codeUnitSafeValuesJson resultValues)
            "retainedBytes", box (bytesHex callerOutput)
            "inputBytes", box inputBytes
            "hostEncodedInputBytes", box encodedBytes
            "metrics", box (jsonNode options (metricSummary metrics)) ])
        checkTraceUsable checks failures $"owning-stack/{optimizationName}/String/{name}/direct-roundtrip-trace-complete" metrics result.LayoutEvents |> ignore
        roundTripReports.Add(box (jsonObject [ "case", box name; "bytes", box (bytesHex callerOutput); "metrics", box (jsonNode options (metricSummary metrics)) ]))

    let mixedOracle = workload.GetProperty("mixedDynamicRecord")
    let mixedCodeUnitsHex = mixedOracle.GetProperty("inputCodeUnitsHex").GetString()
    let mixedText = stringFromCodeUnitsHex mixedCodeUnitsHex
    let mixedSentinel = mixedOracle.GetProperty("sentinel").GetInt64()
    let mixedEmpty = RecordValue("Empty", Map.empty)
    let mixedValue = RecordValue("TextMixed", Map.ofList [
        "marker", mixedEmpty
        "text", StringValue mixedText
        "sentinel", IntValue mixedSentinel ])
    let mixedCodeUnitCount = mixedOracle.GetProperty("inputCodeUnitCount").GetInt32()
    let mixedStringBytes = stringBytesFromCodeUnitsHex mixedCodeUnitsHex
    let mixedStringPayloadBytes = 8 + 2 * mixedCodeUnitCount
    let mixedStringExtentBytes = ((mixedStringPayloadBytes + 7) / 8) * 8
    let mixedRecordPayloadBytes = mixedStringPayloadBytes + 8
    let mixedExpectedRecordPayloadBytes = 16 + 2 * mixedCodeUnitCount
    let mixedExpectedRecordExtentBytes = mixedStringExtentBytes + 8
    let mixedRecordBytes = Array.concat [ mixedStringBytes; bytesFromInt64s [ mixedSentinel ] ]
    let mixedFixtureStringBytes = bytesFromHex (mixedOracle.GetProperty("inputStringBytesHex").GetString())
    let mixedFixtureRecordBytes = bytesFromHex (mixedOracle.GetProperty("recordBytesHex").GetString())
    let mixedFixtureTextAndSentinelBytes = bytesFromHex (mixedOracle.GetProperty("projectedTextAndSentinelBytesHex").GetString())
    let mixedEmptyTokenBytes = bytesFromHex (mixedOracle.GetProperty("projectedEmptyTokenBytesHex").GetString())
    let mixedOraclePassed =
        mixedStringBytes = mixedFixtureStringBytes
        && mixedRecordBytes = mixedFixtureRecordBytes
        && mixedFixtureRecordBytes = mixedFixtureTextAndSentinelBytes
        && mixedStringBytes.Length = mixedOracle.GetProperty("inputStringExtentBytes").GetInt32()
        && mixedStringExtentBytes = mixedStringBytes.Length
        && mixedRecordPayloadBytes = mixedExpectedRecordPayloadBytes
        && mixedOracle.GetProperty("recordPayloadBytes").GetInt32() = mixedExpectedRecordPayloadBytes
        && mixedRecordBytes.Length = mixedExpectedRecordExtentBytes
        && mixedOracle.GetProperty("recordExtentBytes").GetInt32() = mixedExpectedRecordExtentBytes
        && mixedOracle.GetProperty("textOffsetBytes").GetInt32() = 0
        && mixedOracle.GetProperty("sentinelOffsetBytes").GetInt32() = mixedStringExtentBytes
        && int64 mixedRecordBytes.Length = int64 (mixedOracle.GetProperty("nestedEmptyExtentBytes").GetInt32() + mixedStringBytes.Length + 8)
        && mixedEmptyTokenBytes = Array.zeroCreate<byte> 8
    recordCheck checks failures "oracle/String/mixed-empty-string-int-record/independent-inline-bytes" mixedOraclePassed (jsonObject [
        "inputStringBytes", box (bytesHex mixedStringBytes)
        "fixtureStringBytes", box (bytesHex mixedFixtureStringBytes)
        "nestedEmptyBytes", box (mixedOracle.GetProperty("nestedEmptyExtentBytes").GetInt32())
        "recordPayloadBytes", box mixedExpectedRecordPayloadBytes
        "fixtureRecordPayloadBytes", box (mixedOracle.GetProperty("recordPayloadBytes").GetInt32())
        "recordExtentBytes", box mixedExpectedRecordExtentBytes
        "stringBytes", box mixedStringBytes.Length
        "sentinelOffsetBytes", box mixedStringBytes.Length
        "sentinelBytes", box (bytesHex (bytesFromInt64s [ mixedSentinel ]))
        "recordBytes", box (bytesHex mixedRecordBytes)
        "fixtureRecordBytes", box (bytesHex mixedFixtureRecordBytes)
        "projectedEmptyTokenBytes", box (bytesHex mixedEmptyTokenBytes) ])
    let mixedInputFactory = compileTextMixedInputFactory entries "empty-string-int" mixedText mixedSentinel
    use mixedInterpreterInput = IrInterpreter.executeBodyWithInputs interpreterHost "mixed-empty-string-int-inputs" mixedInputFactory None []
    use mixedInterpreterConstruct =
        IrInterpreter.executeBodyWithInputs
            interpreterHost
            "mailbox.make-text-mixed"
            entries.MixedConstruct
            (Some mixedInterpreterInput)
            [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.RetainedRoot 1 ]
    let mixedConstructInterpreterValues = mixedInterpreterConstruct.Decode()
    recordCheck checks failures "interpreter/String/mixed-empty-string-int-record/construction" (mixedConstructInterpreterValues = [ mixedValue ]) (codeUnitSafeValuesJson mixedConstructInterpreterValues)
    use mixedInterpreterProjection =
        IrInterpreter.executeBodyWithInputs
            interpreterHost
            "mailbox.project-text-mixed"
            entries.MixedProjectTextAndSentinel
            (Some mixedInterpreterConstruct)
            [ IrEntryArgument.RetainedRoot 0 ]
    use mixedInterpreterEmptyProjection =
        IrInterpreter.executeBodyWithInputs
            interpreterHost
            "mailbox.project-text-mixed-marker"
            entries.MixedProjectEmptyMarker
            (Some mixedInterpreterConstruct)
            [ IrEntryArgument.RetainedRoot 0 ]
    let mixedProjectionValues = mixedInterpreterProjection.Decode()
    let mixedEmptyProjectionValues = mixedInterpreterEmptyProjection.Decode()
    recordCheck checks failures "interpreter/String/mixed-empty-string-int-record/projection-order" (mixedProjectionValues = [ StringValue mixedText; IntValue mixedSentinel ]) (codeUnitSafeValuesJson mixedProjectionValues)
    recordCheck checks failures "interpreter/String/mixed-empty-string-int-record/empty-projection" (mixedEmptyProjectionValues = [ mixedEmpty ]) (codeUnitSafeValuesJson mixedEmptyProjectionValues)

    let mixedRecordOutput = Array.create mixedRecordBytes.Length 0xA5uy
    let mixedConstructResult = mixedConstructProgram.ExecuteInto([ StringValue mixedText; IntValue mixedSentinel ], stateCapacity, mixedRecordOutput)
    let mixedConstructMetrics = stackMetrics (box mixedConstructResult)
    let mixedConstructEvents = mixedConstructResult.LayoutEvents
    let mixedAllocateEvents = mixedConstructEvents |> List.filter (fun event -> event.Kind = "allocate")
    let mixedRecordBuildEvents = mixedConstructEvents |> List.filter (fun event -> event.Kind = "record-build")
    let mixedRecordBuild = mixedRecordBuildEvents |> List.exists (fun event -> event.TypeId = textMixedTypeId && event.PayloadBytes = mixedExpectedRecordPayloadBytes && event.ExtentBytes = mixedExpectedRecordExtentBytes)
    let mixedStringAllocate = mixedAllocateEvents |> List.exists (fun event -> event.TypeId = stringTypeId && event.PayloadBytes = mixedStringPayloadBytes && event.ExtentBytes = mixedStringExtentBytes)
    let mixedEmptyTokenAllocate = mixedAllocateEvents |> List.exists (fun event -> event.TypeId = emptyTypeId && event.PayloadBytes = 0 && event.ExtentBytes = 8)
    let mixedConstructPassed =
        mixedConstructResult.Values = [ mixedValue ]
        && mixedRecordOutput = mixedRecordBytes
        && mixedConstructResult.RetainedBytesWritten = mixedRecordBytes.Length
        && int64Property mixedConstructMetrics "HostEncodedInputBytes" = int64 (mixedStringBytes.Length + 8)
        && mixedRecordBuild
        && mixedStringAllocate
        && mixedEmptyTokenAllocate
    recordCheck checks failures $"owning-stack/{optimizationName}/String/mixed-empty-string-int-record/construction-and-inline-nesting" mixedConstructPassed (jsonObject [
        "values", box (codeUnitSafeValuesJson mixedConstructResult.Values)
        "expectedRecordBytes", box (bytesHex mixedRecordBytes)
        "actualRecordBytes", box (bytesHex mixedRecordOutput)
        "retainedBytesWritten", box mixedConstructResult.RetainedBytesWritten
        "expectedRecordPayloadBytes", box mixedExpectedRecordPayloadBytes
        "expectedRecordExtentBytes", box mixedExpectedRecordExtentBytes
        "stringAllocateFound", box mixedStringAllocate
        "mixedRecordBuildFound", box mixedRecordBuild
        "mixedRecordBuildEvents", box (layoutEventDetails mixedRecordBuildEvents)
        "standaloneEmptyTokenAllocateFound", box mixedEmptyTokenAllocate
        "metrics", box (jsonNode options (metricSummary mixedConstructMetrics))
        "events", box (layoutEventDetails mixedConstructEvents) ])

    let mixedLayouts = mixedConstructResult.Layouts
    let mixedDynamicLayoutChecks, mixedDynamicLayoutFailures, mixedDynamicLayoutSummary =
        validateDynamicTypeLayouts [ "TextMixed" ] fixture mixedLayouts
    for details in mixedDynamicLayoutChecks do checks.Add details
    for failure in mixedDynamicLayoutFailures do failures.Add($"owning-stack/{optimizationName}/String/mixed-empty-string-int-record/dynamic-layout: {failure}")
    recordCheck checks failures $"owning-stack/{optimizationName}/String/mixed-empty-string-int-record/dynamic-layout-contract" (mixedDynamicLayoutFailures.Length = 0) (jsonNode options mixedDynamicLayoutSummary)
    let mixedLayoutCheck =
        match mixedLayouts |> List.tryFind (fun layout -> layout.TypeName = "TextMixed") with
        | None -> false, jsonObject [ "found", box false; "type", box "TextMixed" ]
        | Some layout ->
            let findField name = layout.Fields |> List.tryFind (fun field -> field.FieldName = name)
            let markerField = findField "marker"
            let textField = findField "text"
            let sentinelField = findField "sentinel"
            let emptyLayout = mixedLayouts |> List.tryFind (fun item -> item.TypeName = "Empty")
            let mixedLayoutOracle = workload.GetProperty("dynamicLayoutOracle")
            let passed =
                layout.IsDynamic
                && layout.PayloadBytes = -1
                && layout.ExtentBytes = -1
                && layout.MinimumPayloadBytes = mixedLayoutOracle.GetProperty("minimumPayloadBytes").GetProperty("TextMixed").GetInt32()
                && layout.MinimumExtentBytes = mixedLayoutOracle.GetProperty("minimumExtentBytes").GetProperty("TextMixed").GetInt32()
                && (match markerField with Some field -> not field.IsDynamic && not field.IsOffsetDynamic && field.OffsetBytes = 0 && field.PayloadBytes = 0 && field.ExtentBytes = 0 | None -> false)
                && (match textField with Some field -> field.IsDynamic && not field.IsOffsetDynamic && field.OffsetBytes = 0 && field.MinimumPayloadBytes = 8 && field.MinimumExtentBytes = 8 | None -> false)
                && (match sentinelField with Some field -> not field.IsDynamic && field.IsOffsetDynamic && field.OffsetBytes = -1 && field.MinimumPayloadBytes = 8 && field.MinimumExtentBytes = 8 && field.PayloadBytes = 8 && field.ExtentBytes = 8 | None -> false)
                && (match emptyLayout with Some empty -> not empty.IsDynamic && empty.PayloadBytes = 0 && empty.ExtentBytes = 8 && empty.MinimumPayloadBytes = 0 && empty.MinimumExtentBytes = 8 | None -> false)
            passed, jsonObject [
                "found", box true
                "isDynamic", box layout.IsDynamic
                "payloadBytes", box layout.PayloadBytes
                "extentBytes", box layout.ExtentBytes
                "minimumPayloadBytes", box layout.MinimumPayloadBytes
                "minimumExtentBytes", box layout.MinimumExtentBytes
                "fields", box (layout.Fields |> List.map (fun field -> jsonObject [ "name", box field.FieldName; "offsetBytes", box field.OffsetBytes; "isOffsetDynamic", box field.IsOffsetDynamic; "isDynamic", box field.IsDynamic; "payloadBytes", box field.PayloadBytes; "extentBytes", box field.ExtentBytes; "minimumPayloadBytes", box field.MinimumPayloadBytes; "minimumExtentBytes", box field.MinimumExtentBytes ]))
                "emptyLayout", box (match emptyLayout with Some empty -> jsonObject [ "payloadBytes", box empty.PayloadBytes; "extentBytes", box empty.ExtentBytes ] | None -> null) ]
    let mixedLayoutPassed, mixedLayoutDetails = mixedLayoutCheck
    recordCheck checks failures $"owning-stack/{optimizationName}/String/mixed-empty-string-int-record/dynamic-layout-with-zero-width-field" mixedLayoutPassed mixedLayoutDetails

    let mixedProjectionOutput = Array.create mixedFixtureTextAndSentinelBytes.Length 0xA5uy
    let mixedProjectionResult = mixedProjectProgram.ExecuteInto([ mixedValue ], stateCapacity, mixedProjectionOutput)
    let mixedProjectionMetrics = stackMetrics (box mixedProjectionResult)
    let mixedProjectionPassed =
        mixedProjectionResult.Values = [ StringValue mixedText; IntValue mixedSentinel ]
        && mixedProjectionOutput = mixedFixtureTextAndSentinelBytes
        && mixedProjectionResult.RetainedBytesWritten = mixedFixtureTextAndSentinelBytes.Length
    recordCheck checks failures $"owning-stack/{optimizationName}/String/mixed-empty-string-int-record/project-string-and-following-sentinel" mixedProjectionPassed (jsonObject [
        "values", box (codeUnitSafeValuesJson mixedProjectionResult.Values)
        "expectedProjectionBytes", box (bytesHex mixedFixtureTextAndSentinelBytes)
        "actualProjectionBytes", box (bytesHex mixedProjectionOutput)
        "sentinelExpectedOffsetBytes", box (mixedStringBytes.Length)
        "sentinelExpectedValue", box mixedSentinel
        "metrics", box (jsonNode options (metricSummary mixedProjectionMetrics))
        "events", box (layoutEventDetails mixedProjectionResult.LayoutEvents) ])
    let mixedEmptyProjectionOutput = Array.create mixedEmptyTokenBytes.Length 0xA5uy
    let mixedEmptyProjectionResult = mixedProjectEmptyProgram.ExecuteInto([ mixedValue ], stateCapacity, mixedEmptyProjectionOutput)
    let mixedEmptyProjectionPassed =
        mixedEmptyProjectionResult.Values = [ mixedEmpty ]
        && mixedEmptyProjectionOutput = mixedEmptyTokenBytes
        && mixedEmptyProjectionResult.RetainedBytesWritten = 8
    recordCheck checks failures $"owning-stack/{optimizationName}/String/mixed-empty-string-int-record/project-empty-as-standalone-token" mixedEmptyProjectionPassed (jsonObject [
        "values", box (codeUnitSafeValuesJson mixedEmptyProjectionResult.Values)
        "expectedTokenBytes", box (bytesHex mixedEmptyTokenBytes)
        "actualTokenBytes", box (bytesHex mixedEmptyProjectionOutput)
        "retainedBytesWritten", box mixedEmptyProjectionResult.RetainedBytesWritten
        "metrics", box (jsonNode options (metricSummary (box mixedEmptyProjectionResult.Metrics)))
        "events", box (layoutEventDetails mixedEmptyProjectionResult.LayoutEvents) ])

    let shortBranchState, _ = compiledInputBodies["short-ascii"]
    let longBranchState, _ = compiledInputBodies["long-bmp-astral"]
    let shortBranchEnvelope = textEnvelopeFromState shortBranchState
    let longBranchEnvelope = textEnvelopeFromState longBranchState
    let branchInputFactory = compileTextEnvelopePairFactory entries "dynamic-branch-join" shortBranchEnvelope longBranchEnvelope
    use branchInputOwner = IrInterpreter.executeBodyWithInputs interpreterHost "dynamic-branch-join-inputs" branchInputFactory None []
    let dynamicBranchReports = ResizeArray<obj>()
    for branchCase in workload.GetProperty("dynamicBranchCases").EnumerateArray() do
        let branchName = branchCase.GetProperty("name").GetString()
        let chooseLeft = branchCase.GetProperty("chooseLeft").GetBoolean()
        let selectedCaseName = branchCase.GetProperty("selectedMainCase").GetString()
        let selectedEnvelope = if chooseLeft then shortBranchEnvelope else longBranchEnvelope
        let selectedCase = mainCases |> Array.find (fun item -> item.GetProperty("name").GetString() = selectedCaseName)
        let selectedCodeUnitsHex = selectedCase.GetProperty("inputCodeUnitsHex").GetString()
        let selectedCodeUnitCount = selectedCase.GetProperty("inputCodeUnitCount").GetInt32()
        let selectedInput = selectedCase.GetProperty("input")
        let selectedTag = selectedInput.GetProperty("tag").GetInt64()
        let expectedLength = branchCase.GetProperty("expectedLengthOutput").GetInt64()
        let expectedEnvelopeBytes = textEnvelopeBytes selectedTag (int64 selectedCodeUnitCount) selectedCodeUnitsHex
        let expectedBranchBytes = Array.concat [ expectedEnvelopeBytes; bytesFromInt64s [ expectedLength ] ]
        let fixtureBranchBytes = bytesFromHex (branchCase.GetProperty("expectedRetainedBytesHex").GetString())
        let expectedEnvelopeExtent = branchCase.GetProperty("expectedSelectedEnvelopeExtentBytes").GetInt32()
        let fixtureOraclePassed =
            expectedEnvelopeBytes.Length = expectedEnvelopeExtent
            && expectedBranchBytes = fixtureBranchBytes
            && fixtureBranchBytes.Length = branchCase.GetProperty("expectedRetainedBytes").GetInt32()
            && expectedLength = int64 selectedCodeUnitCount
        recordCheck checks failures $"oracle/String/{branchName}/dynamic-branch-retained-bytes" fixtureOraclePassed (jsonObject [
            "selectedEnvelopeBytes", box (bytesHex expectedEnvelopeBytes)
            "selectedEnvelopeExtentBytes", box expectedEnvelopeBytes.Length
            "lengthOutput", box expectedLength
            "oracleRetainedBytes", box (bytesHex expectedBranchBytes)
            "fixtureRetainedBytes", box (bytesHex fixtureBranchBytes) ])
        use branchInterpreterResult =
            IrInterpreter.executeBodyWithInputs
                interpreterHost
                "mailbox.choose-text-envelope"
                entries.DynamicBranch
                (Some branchInputOwner)
                [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.RetainedRoot 1; IrEntryArgument.BoolArgument chooseLeft ]
        let branchInterpreterValues = branchInterpreterResult.Decode()
        let expectedBranchValues = [ selectedEnvelope; IntValue expectedLength ]
        recordCheck checks failures $"interpreter/String/{branchName}/phi-value-and-following-read" (branchInterpreterValues = expectedBranchValues) (codeUnitSafeValuesJson branchInterpreterValues)
        let branchOutput = Array.create fixtureBranchBytes.Length 0xA5uy
        let branchResult = dynamicBranchProgram.ExecuteInto([ shortBranchEnvelope; longBranchEnvelope; BoolValue chooseLeft ], stateCapacity, branchOutput)
        let branchValues = branchResult.Values
        let branchMetrics = stackMetrics (box branchResult)
        let returnedSecondOutput = int64FromBytesLittleEndian branchOutput expectedEnvelopeExtent
        let branchPassed =
            branchValues = expectedBranchValues
            && branchOutput = fixtureBranchBytes
            && branchResult.RetainedBytesWritten = fixtureBranchBytes.Length
            && returnedSecondOutput = expectedLength
            && fixtureBranchBytes.Length - expectedEnvelopeExtent = 8
        recordCheck checks failures $"owning-stack/{optimizationName}/String/{branchName}/phi-layout-and-multi-output-return" branchPassed (jsonObject [
            "chooseLeft", box chooseLeft
            "selectedCase", box selectedCaseName
            "values", box (codeUnitSafeValuesJson branchValues)
            "expectedEnvelopeExtentBytes", box expectedEnvelopeExtent
            "secondOutputOffsetBytes", box expectedEnvelopeExtent
            "secondOutputValue", box returnedSecondOutput
            "expectedRetainedBytes", box (bytesHex fixtureBranchBytes)
            "actualRetainedBytes", box (bytesHex branchOutput)
            "retainedBytesWritten", box branchResult.RetainedBytesWritten
            "metrics", box (jsonNode options (metricSummary branchMetrics))
            "events", box (layoutEventDetails branchResult.LayoutEvents) ])
        checkTraceUsable checks failures $"owning-stack/{optimizationName}/String/{branchName}/trace-complete" branchMetrics branchResult.LayoutEvents |> ignore
        dynamicBranchReports.Add(box (jsonObject [
            "case", box branchName
            "selectedCase", box selectedCaseName
            "expectedBytes", box (bytesHex fixtureBranchBytes)
            "actualBytes", box (bytesHex branchOutput)
            "metrics", box (jsonNode options (metricSummary branchMetrics))
            "events", box (layoutEventDetails branchResult.LayoutEvents) ]))

    let joined = workload.GetProperty("joinedSurrogatePair")
    let leftHex = joined.GetProperty("leftCodeUnitHex").GetString()
    let rightHex = joined.GetProperty("rightCodeUnitHex").GetString()
    let joinedHex = joined.GetProperty("expectedCodeUnitsHex").GetString()
    let leftValue = stringFromCodeUnitsHex leftHex
    let rightValue = stringFromCodeUnitsHex rightHex
    let joinedValue = StringValue(stringFromCodeUnitsHex joinedHex)
    let joinedBytes = bytesFromHex (joined.GetProperty("expectedStringBytesHex").GetString())
    let joinedOracle = stringBytesFromCodeUnitsHex joinedHex
    recordCheck checks failures $"oracle/String/joined-surrogate-pair/raw-code-units" (joinedOracle = joinedBytes && joinedBytes.Length = joined.GetProperty("expectedExtentBytes").GetInt32()) (jsonObject [
        "oracleHex", box (bytesHex joinedOracle)
        "fixtureHex", box (bytesHex joinedBytes)
        "codeUnitCount", box (joined.GetProperty("expectedCodeUnitCount").GetInt32()) ])
    let pairFactory = compileStringPairFactory entries "joined-surrogate-pair" leftValue rightValue
    use pairOwner = IrInterpreter.executeBodyWithInputs interpreterHost "joined-surrogate-pair-input" pairFactory None []
    use joinedInterpreterResult = IrInterpreter.executeBodyWithInputs interpreterHost "mailbox.join-strings" entries.JoinStrings (Some pairOwner) [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.RetainedRoot 1 ]
    use directConcatInterpreterResult = IrInterpreter.executeBodyWithInputs interpreterHost "native-value-stack-direct-string-concat-capacity" entries.DirectConcat (Some pairOwner) [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.RetainedRoot 1 ]
    let joinedInterpreterValues = joinedInterpreterResult.Decode()
    let directConcatInterpreterValues = directConcatInterpreterResult.Decode()
    recordCheck checks failures $"interpreter/String/joined-surrogate-pair/concat-preserves-code-units" (joinedInterpreterValues = [ joinedValue ]) (codeUnitSafeValuesJson joinedInterpreterValues)
    recordCheck checks failures $"interpreter/String/direct-concat-capacity/same-verified-body" (directConcatInterpreterValues = [ joinedValue ]) (codeUnitSafeValuesJson directConcatInterpreterValues)
    let joinedOutput = Array.create joinedBytes.Length 0xA5uy
    let joinedResult = joinProgram.ExecuteInto([ StringValue leftValue; StringValue rightValue ], stateCapacity, joinedOutput)
    let joinedValues = getProperty (box joinedResult) "Values" :?> Value list
    let joinedMetrics = stackMetrics (box joinedResult)
    recordCheck checks failures $"owning-stack/{optimizationName}/String/joined-surrogate-pair/two-dynamic-inputs" (joinedValues = [ joinedValue ] && joinedOutput = joinedBytes && (getProperty (box joinedResult) "RetainedOutputBytes" :?> byte array) = joinedBytes && int64Property joinedMetrics "InputBytes" = 32L) (jsonObject [
        "values", box (codeUnitSafeValuesJson joinedValues)
        "leftCodeUnitHex", box leftHex
        "rightCodeUnitHex", box rightHex
        "resultBytes", box (bytesHex joinedOutput)
        "metrics", box (jsonNode options (metricSummary joinedMetrics))
        "layoutEvents", box (layoutEventDetails joinedResult.LayoutEvents) ])
    let concatEvents = joinedResult.LayoutEvents |> List.filter (fun event -> event.Kind = "string-concat-left" || event.Kind = "string-concat-right")
    let concatRangesPass =
        concatEvents.Length = 2
        && (concatEvents |> List.forall (fun event -> event.ExtentBytes = 2 && event.PayloadBytes = 2 && event.SourceExtentBytes = Some 2))
        && (concatEvents |> List.map (fun event -> event.OffsetBytes) |> Set.ofList |> Set.count = 2)
    recordCheck checks failures $"owning-stack/{optimizationName}/String/joined-surrogate-pair/concat-source-ranges" concatRangesPass (jsonObject [
        "expectedCopiedCodeUnitBytesPerInput", box 2
        "events", box (layoutEventDetails concatEvents) ])
    checkTraceUsable checks failures $"owning-stack/{optimizationName}/String/joined-surrogate-pair/trace-complete" joinedMetrics joinedResult.LayoutEvents |> ignore

    let independentCapacityOracle = workload.GetProperty("capacityCases").GetProperty("programStack").GetProperty("independentDynamicConcat")
    let scheduledExtents = independentCapacityOracle.GetProperty("inputStringExtentsBytes").EnumerateArray() |> Seq.map (fun item -> item.GetInt32()) |> Seq.toList
    let scheduledInputBytes = scheduledExtents |> List.sum
    let scheduledEntryFrameBytes = independentCapacityOracle.GetProperty("entryFrameInputBytes").GetInt32()
    let stagedConcatExtent = independentCapacityOracle.GetProperty("concatOutputExtentBytes").GetInt32()
    let requiredStackBytes = independentCapacityOracle.GetProperty("requiredStackCapacityBytes").GetInt32()
    let oneByteBelowCapacity = independentCapacityOracle.GetProperty("oneByteBelowAvailableBytes").GetInt32()
    let wrappedJoinArgumentCopyBytes = scheduledInputBytes
    let wrappedJoinExpectedPeakBytes = scheduledInputBytes + 3 * scheduledEntryFrameBytes + stagedConcatExtent
    let wrappedJoinFixturePeakBytes = independentCapacityOracle.GetProperty("wrappedUserCallExpectedPeakBytes").GetInt32()
    let wrappedScheduleOwners = [
        "wrapper-left-input"; "wrapper-right-input"
        "entry-frame-left-input-copy"; "entry-frame-right-input-copy"
        "user-call-left-input-copy"; "user-call-right-input-copy"
        "left-LoadLocal-operand-copy"; "right-LoadLocal-operand-copy"
        "concat-result-scratch" ]
    let wrappedScheduleExtents = [
        scheduledExtents[0]; scheduledExtents[1]
        scheduledExtents[0]; scheduledExtents[1]
        scheduledExtents[0]; scheduledExtents[1]
        scheduledExtents[0]; scheduledExtents[1]
        stagedConcatExtent ]
    let wrappedScheduleStarts = wrappedScheduleExtents |> List.scan (+) 0 |> List.take wrappedScheduleExtents.Length
    let expectedWrappedSchedule = List.map3 (fun owner start extent -> owner, start, extent) wrappedScheduleOwners wrappedScheduleStarts wrappedScheduleExtents
    let fixtureWrappedSchedule =
        independentCapacityOracle.GetProperty("wrappedUserCallSchedule").EnumerateArray()
        |> Seq.map (fun item -> item.GetProperty("owner").GetString(), item.GetProperty("startBytes").GetInt32(), item.GetProperty("extentBytes").GetInt32())
        |> Seq.toList
    let wrappedSchedulePeakBytes = List.last wrappedScheduleStarts + List.last wrappedScheduleExtents
    let wrappedSchedulePass = expectedWrappedSchedule = fixtureWrappedSchedule && wrappedSchedulePeakBytes = wrappedJoinExpectedPeakBytes
    let wrappedJoinObservedPeakBytes = int64Property joinedMetrics "ReservedStackBytes"
    recordCheck checks failures $"owning-stack/{optimizationName}/String/wrapped-user-call-observed-stack-span" (wrappedSchedulePass && wrappedJoinExpectedPeakBytes = wrappedJoinFixturePeakBytes && wrappedJoinObservedPeakBytes = int64 wrappedJoinFixturePeakBytes) (jsonObject [
        "hostInputBytes", box scheduledInputBytes
        "entryFrameCopyBytes", box scheduledEntryFrameBytes
        "userCallFrameCopyBytes", box scheduledEntryFrameBytes
        "userCallLocalArgumentCopyBytes", box wrappedJoinArgumentCopyBytes
        "concatScratchBytes", box stagedConcatExtent
        "fixtureWrappedSchedule", box (JsonNode.Parse(independentCapacityOracle.GetProperty("wrappedUserCallSchedule").GetRawText()))
        "expectedStackSpanBytes", box wrappedJoinExpectedPeakBytes
        "fixtureExpectedStackSpanBytes", box wrappedJoinFixturePeakBytes
        "observedReservedStackBytes", box wrappedJoinObservedPeakBytes ])
    let independentSchedulePass =
        scheduledExtents = [ 16; 16 ]
        && scheduledInputBytes = independentCapacityOracle.GetProperty("inputStackBytes").GetInt32()
        && scheduledEntryFrameBytes = scheduledInputBytes
        && requiredStackBytes = scheduledInputBytes + scheduledEntryFrameBytes + stagedConcatExtent
        && oneByteBelowCapacity = requiredStackBytes - 1
        && joinedBytes.Length = stagedConcatExtent
        && int64Property joinedMetrics "InputBytes" = int64 scheduledInputBytes
    recordCheck checks failures $"owning-stack/{optimizationName}/String/independent-concat-capacity-schedule" independentSchedulePass (jsonObject [
        "leftInputExtentBytes", box scheduledExtents[0]
        "rightInputExtentBytes", box scheduledExtents[1]
        "inputOwnerBytes", box scheduledInputBytes
        "entryFrameInputCopyBytes", box scheduledEntryFrameBytes
        "concatStagingExtentBytes", box stagedConcatExtent
        "independentlyRequiredStackCapacityBytes", box requiredStackBytes
        "oneByteBelowAvailableBytes", box oneByteBelowCapacity
        "schedule", box (JsonNode.Parse(independentCapacityOracle.GetProperty("schedule").GetRawText())) ])
    let exactCapacityFailureOutput = Array.create joinedBytes.Length 0xA5uy
    let exactCapacityFailureBefore = Array.copy exactCapacityFailureOutput
    let exactCapacityFailureCode, exactCapacityFailureMetrics, exactCapacityFailureRequired, exactCapacityFailureAvailable, exactCapacityFailureBoundary =
        try
            directConcatProgram.ExecuteInto([ StringValue leftValue; StringValue rightValue ], oneByteBelowCapacity, exactCapacityFailureOutput) |> ignore
            "unexpected-success", null, -1L, -1L, ""
        with error ->
            exceptionCode error,
            getProperty error "Metrics",
            optionalInt64 error "RequiredBytes",
            optionalInt64 error "AvailableBytes",
            (getProperty error "Boundary" |> string)
    let exactCapacityFailurePassed =
        exactCapacityFailureCode = "OWNING_STACK_CAPACITY"
        && exactCapacityFailureBoundary = "program-data-stack"
        && exactCapacityFailureRequired = int64 requiredStackBytes
        && exactCapacityFailureAvailable = int64 oneByteBelowCapacity
        && not (isNull exactCapacityFailureMetrics)
        && int64Property exactCapacityFailureMetrics "InputBytes" = int64 scheduledInputBytes
        && int64Property exactCapacityFailureMetrics "InputCopyBytes" = int64 scheduledInputBytes
        && int64Property exactCapacityFailureMetrics "TraceEventCount" > 0L
        && not (Convert.ToBoolean(getProperty exactCapacityFailureMetrics "TraceTruncated", CultureInfo.InvariantCulture))
        && int64Property exactCapacityFailureMetrics "FinalCursorBytes" = 0L
        && int64Property exactCapacityFailureMetrics "FinalLiveStackBytes" = 0L
        && exactCapacityFailureOutput = exactCapacityFailureBefore
    recordCheck checks failures $"owning-stack/{optimizationName}/String/independent-concat-capacity-one-byte-short" exactCapacityFailurePassed (jsonObject [
        "code", box exactCapacityFailureCode
        "boundary", box exactCapacityFailureBoundary
        "independentlyExpectedRequiredBytes", box requiredStackBytes
        "actualRequiredBytes", box exactCapacityFailureRequired
        "independentlyExpectedAvailableBytes", box oneByteBelowCapacity
        "actualAvailableBytes", box exactCapacityFailureAvailable
        "inputCopyBytesBeforeFailure", box (if isNull exactCapacityFailureMetrics then -1L else int64Property exactCapacityFailureMetrics "InputCopyBytes")
        "callerBufferUnchanged", box (bufferCheckDetails exactCapacityFailureBefore exactCapacityFailureOutput)
        "metrics", if isNull exactCapacityFailureMetrics then null else jsonNode options (metricSummary exactCapacityFailureMetrics) ])
    let exactCapacityOutput = Array.create joinedBytes.Length 0xA5uy
    let exactCapacityResult = directConcatProgram.ExecuteInto([ StringValue leftValue; StringValue rightValue ], requiredStackBytes, exactCapacityOutput)
    let exactCapacityResultMetrics = stackMetrics (box exactCapacityResult)
    let exactCapacitySuccessPassed =
        exactCapacityResult.Values = [ joinedValue ]
        && exactCapacityOutput = joinedBytes
        && exactCapacityResult.RetainedBytesWritten = joinedBytes.Length
        && int64Property exactCapacityResultMetrics "InputCopyBytes" = int64 scheduledInputBytes
        && int64Property exactCapacityResultMetrics "ReservedStackBytes" = int64 requiredStackBytes
    recordCheck checks failures $"owning-stack/{optimizationName}/String/independent-concat-capacity-exact-boundary-succeeds" exactCapacitySuccessPassed (jsonObject [
        "capacityBytes", box requiredStackBytes
        "outputBytes", box (bytesHex exactCapacityOutput)
        "reservedStackBytes", box (int64Property exactCapacityResultMetrics "ReservedStackBytes")
        "metrics", jsonNode options (metricSummary exactCapacityResultMetrics) ])

    let shortCase = mainCases |> Array.find (fun element -> element.GetProperty("name").GetString() = "short-ascii")
    let longCase = mainCases |> Array.find (fun element -> element.GetProperty("name").GetString() = "long-bmp-astral")
    let shortState, _ = compiledInputBodies["short-ascii"]
    let longState, _ = compiledInputBodies["long-bmp-astral"]
    let shortEnvelope = textEnvelopeFromState shortState
    let longEnvelope = textEnvelopeFromState longState
    let shortEnvelopeHex = shortCase.GetProperty("inputCodeUnitsHex").GetString()
    let longEnvelopeHex = longCase.GetProperty("inputCodeUnitsHex").GetString()
    let envelopeParts = function
        | RecordValue("TextEnvelope", fields) ->
            match fields["tag"], fields["leaf"] with
            | IntValue tag, RecordValue("TextLeaf", leaf) ->
                match leaf["text"], leaf["codeUnits"] with
                | StringValue text, IntValue codeUnits -> tag, codeUnits, text
                | _ -> invalidOp "TextEnvelope requires StringValue/IntValue leaf fields."
            | _ -> invalidOp "TextEnvelope requires an Int tag and TextLeaf."
        | _ -> invalidOp "Expected TextEnvelope."
    let shortTag, shortCodeUnits, shortText = envelopeParts shortEnvelope
    let longTag, longCodeUnits, longText = envelopeParts longEnvelope
    let scopePairs = [
        "short-outer-long-inner", shortEnvelope, longEnvelope, shortEnvelopeHex, shortTag, shortCodeUnits, shortText
        "long-outer-short-inner", longEnvelope, shortEnvelope, longEnvelopeHex, longTag, longCodeUnits, longText ]
    let scopeReports = ResizeArray<obj>()
    for caseName, outerValue, innerValue, outerHex, outerTag, outerCodeUnits, outerText in scopePairs do
        let pairInputFactory = compileTextEnvelopePairFactory entries caseName outerValue innerValue
        use scopeInputOwner = IrInterpreter.executeBodyWithInputs interpreterHost ("scope-input-" + caseName) pairInputFactory None []
        use scopeInterpreterResult = IrInterpreter.executeBodyWithInputs interpreterHost "native-value-stack-string-scope-shadow" entries.ScopeShadow (Some scopeInputOwner) [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.RetainedRoot 1 ]
        recordCheck checks failures $"interpreter/String/scope-shadow/{caseName}" (scopeInterpreterResult.Decode() = [ outerValue ]) (codeUnitSafeValuesJson (scopeInterpreterResult.Decode()))
        let expectedEnvelopeBytes = textEnvelopeBytes outerTag outerCodeUnits outerHex
        let scopeOutput = Array.create expectedEnvelopeBytes.Length 0xA5uy
        let scopeResult = scopeProgram.ExecuteInto([ outerValue; innerValue ], stateCapacity, scopeOutput)
        let scopeMetrics = stackMetrics (box scopeResult)
        let scopeValuePass = scopeResult.Values = [ outerValue ] && scopeOutput = expectedEnvelopeBytes
        let cleanupPass = int64Property scopeMetrics "FinalLiveStackBytes" = 0L && int64Property scopeMetrics "FinalCursorBytes" = 0L
        recordCheck checks failures $"owning-stack/{optimizationName}/String/scope-shadow/{caseName}/outer-restore" scopeValuePass (jsonObject [
            "values", box (codeUnitSafeValuesJson scopeResult.Values)
            "expectedOuterBytes", box (bytesHex expectedEnvelopeBytes)
            "actualOuterBytes", box (bytesHex scopeOutput) ])
        recordCheck checks failures $"owning-stack/{optimizationName}/String/scope-shadow/{caseName}/cleanup-and-local-categories" (cleanupPass && int64Property scopeMetrics "ReservedLocalBytes" >= int64Property scopeMetrics "PeakLiveLocalBytes") (jsonObject [
            "reservedLocalBytes", box (int64Property scopeMetrics "ReservedLocalBytes")
            "peakLiveLocalBytes", box (int64Property scopeMetrics "PeakLiveLocalBytes")
            "backendMetadataPerFrameBytes", box (int64Property scopeMetrics "BackendMetadataPerFrameBytes")
            "backendMetadataPeakBoundBytes", box (int64Property scopeMetrics "BackendMetadataPeakBoundBytes")
            "finalCursorBytes", box (int64Property scopeMetrics "FinalCursorBytes")
            "finalLiveStackBytes", box (int64Property scopeMetrics "FinalLiveStackBytes")
            "metrics", box (jsonNode options (metricSummary scopeMetrics)) ])
        checkTraceUsable checks failures $"owning-stack/{optimizationName}/String/scope-shadow/{caseName}/trace-complete" scopeMetrics scopeResult.LayoutEvents |> ignore
        scopeReports.Add(box (jsonObject [
            "case", box caseName
            "outerCodeUnits", box (outerText.Length)
            "outerValueBytes", box (bytesHex expectedEnvelopeBytes)
            "metrics", box (jsonNode options (metricSummary scopeMetrics))
            "events", box (layoutEventDetails scopeResult.LayoutEvents) ]))

    let duplicateInputFactory = compileTextEnvelopePairFactory entries "duplicate-long" longEnvelope longEnvelope
    use duplicateInputOwner = IrInterpreter.executeBodyWithInputs interpreterHost "duplicate-long-input" duplicateInputFactory None []
    use duplicateInterpreterResult = IrInterpreter.executeBodyWithInputs interpreterHost "native-value-stack-string-direct-dup-drop" entries.DirectDupDrop (Some duplicateInputOwner) [ IrEntryArgument.RetainedRoot 0 ]
    recordCheck checks failures "interpreter/String/direct-dup-drop-preserves-survivor" (duplicateInterpreterResult.Decode() = [ longEnvelope ]) (codeUnitSafeValuesJson (duplicateInterpreterResult.Decode()))
    let duplicateExpectedBytes = textEnvelopeBytes longTag longCodeUnits longEnvelopeHex
    let duplicateOutput = Array.create duplicateExpectedBytes.Length 0xA5uy
    let duplicateResult = duplicateProgram.ExecuteInto([ longEnvelope ], stateCapacity, duplicateOutput)
    let duplicateEvents = duplicateResult.LayoutEvents
    let duplicateCopyEvents = duplicateEvents |> List.filter (fun event -> event.Kind = "duplicate")
    let duplicateDropEvents = duplicateEvents |> List.filter (fun event -> event.Kind = "drop")
    let duplicatePhysicalPass =
        duplicateCopyEvents.Length = 1
        && (duplicateCopyEvents |> List.forall (fun event ->
            match event.SourceOffsetBytes with
            | Some source ->
                let sourceExtent = defaultArg event.SourceExtentBytes event.ExtentBytes
                let destinationStart, destinationEnd = int64 event.OffsetBytes, int64 event.OffsetBytes + int64 event.ExtentBytes
                let sourceStart, sourceEnd = int64 source, int64 source + int64 sourceExtent
                destinationEnd <= sourceStart || sourceEnd <= destinationStart
            | None -> false))
        && (duplicateDropEvents |> List.exists (fun drop -> duplicateCopyEvents |> List.exists (fun duplicate -> drop.OffsetBytes = duplicate.OffsetBytes && drop.ExtentBytes = duplicate.ExtentBytes)))
        && duplicateResult.Values = [ longEnvelope ]
        && duplicateOutput = duplicateExpectedBytes
    recordCheck checks failures $"owning-stack/{optimizationName}/String/direct-dup-drop-disjoint-and-survivor" duplicatePhysicalPass (jsonObject [
        "expectedEnvelopeBytes", box (bytesHex duplicateExpectedBytes)
        "actualEnvelopeBytes", box (bytesHex duplicateOutput)
        "copyEvents", box (layoutEventDetails duplicateCopyEvents)
        "dropEvents", box (layoutEventDetails duplicateDropEvents)
        "metrics", box (jsonNode options (metricSummary (box duplicateResult.Metrics))) ])
    checkTraceUsable checks failures $"owning-stack/{optimizationName}/String/direct-dup-drop/trace-complete" duplicateResult.Metrics duplicateEvents |> ignore

    let zeroEnvelope = shortEnvelope
    let zeroFactory = compileTextEnvelopePairFactory entries "zero-output" zeroEnvelope zeroEnvelope
    use zeroInputOwner = IrInterpreter.executeBodyWithInputs interpreterHost "zero-output-input" zeroFactory None []
    use zeroInterpreterResult = IrInterpreter.executeBodyWithInputs interpreterHost "native-value-stack-string-zero-output-user-call" entries.ZeroOutputUserCall (Some zeroInputOwner) [ IrEntryArgument.RetainedRoot 0 ]
    recordCheck checks failures "interpreter/String/zero-output-user-call" (zeroInterpreterResult.Decode() = []) (codeUnitSafeValuesJson (zeroInterpreterResult.Decode()))
    let zeroBuffer = Array.create 32 0xA5uy
    let zeroBefore = Array.copy zeroBuffer
    let zeroResult = zeroOutputProgram.ExecuteInto([ zeroEnvelope ], stateCapacity, zeroBuffer)
    let zeroMetrics = box zeroResult.Metrics
    let zeroPassed = zeroResult.Values = [] && zeroResult.RetainedBytesWritten = 0 && zeroBuffer = zeroBefore && int64Property zeroMetrics "FinalCursorBytes" = 0L && int64Property zeroMetrics "FinalLiveStackBytes" = 0L
    recordCheck checks failures $"owning-stack/{optimizationName}/String/zero-output-user-call-return" zeroPassed (jsonObject [
        "values", box []
        "retainedBytesWritten", box zeroResult.RetainedBytesWritten
        "callerBufferUnchanged", box (bufferCheckDetails zeroBefore zeroBuffer)
        "metrics", box (jsonNode options (metricSummary zeroMetrics))
        "events", box (layoutEventDetails zeroResult.LayoutEvents) ])
    checkTraceUsable checks failures $"owning-stack/{optimizationName}/String/zero-output-user-call/trace-complete" zeroResult.Metrics zeroResult.LayoutEvents |> ignore

    let inputOwnerForFailure = compileTextStateFactory entries "failure-after-allocation" longState
    use failureInputOwner = IrInterpreter.executeBodyWithInputs interpreterHost "text-failure-input" inputOwnerForFailure None []
    let interpreterFailureCode =
        try
            use _unexpected = IrInterpreter.executeBodyWithInputs interpreterHost "mailbox.fail-after-text-allocation" entries.FailAfterTextAllocation (Some failureInputOwner) [ IrEntryArgument.RetainedRoot 0 ]
            "unexpected-success"
        with error -> diagnosticCode error
    recordCheck checks failures "interpreter/String/failure-after-concat" (interpreterFailureCode = "RUNTIME_DIVIDE_BY_ZERO" && failureInputOwner.Decode() = [ longState ]) interpreterFailureCode
    let failureBuffer = Array.create 80 0xA5uy
    let failureBefore = Array.copy failureBuffer
    let nativeFailure, nativeFailureMetrics =
        try
            failProgram.ExecuteInto([ longState ], stateCapacity, failureBuffer) |> ignore
            "unexpected-success", null
        with error -> diagnosticCode error, getProperty error "Metrics"
    let nativeFailurePassed =
        nativeFailure = "RUNTIME_DIVIDE_BY_ZERO"
        && not (isNull nativeFailureMetrics)
        && int64Property nativeFailureMetrics "DeepCopyBytes" > 0L
        && int64Property nativeFailureMetrics "FinalCursorBytes" = 0L
        && int64Property nativeFailureMetrics "FinalLiveStackBytes" = 0L
        && failureBuffer = failureBefore
    recordCheck checks failures $"owning-stack/{optimizationName}/String/failure-after-concat-unwinds-and-does-not-publish" nativeFailurePassed (jsonObject [
        "diagnosticCode", box nativeFailure
        "callerBufferUnchanged", box (bufferCheckDetails failureBefore failureBuffer)
        "metrics", if isNull nativeFailureMetrics then null else jsonNode options (metricSummary nativeFailureMetrics) ])

    let retainedFailureReports = ResizeArray<obj>()
    for caseElement in mainCases do
        let name = caseElement.GetProperty("name").GetString()
        let inputValue, _ = compiledInputBodies[name]
        let expectedBytes = bytesFromHex (caseElement.GetProperty("retainedBytesHex").GetString())
        let sentinel = Array.create expectedBytes.Length 0xA5uy
        let old = Array.copy sentinel
        let available = expectedBytes.Length - 1
        let failureCode, failureMetrics, required, actualAvailable, boundary =
            try
                turnProgram.ExecuteInto([ inputValue ], stateCapacity, sentinel, available) |> ignore
                "unexpected-success", null, -1L, -1L, ""
            with error ->
                exceptionCode error,
                getProperty error "Metrics",
                optionalInt64 error "RequiredBytes",
                optionalInt64 error "AvailableBytes",
                (getProperty error "Boundary" |> string)
        let passed = failureCode = "OWNING_RETAINED_CAPACITY" && boundary = "retained-output" && required = int64 expectedBytes.Length && actualAvailable = int64 available && sentinel = old && not (isNull failureMetrics) && int64Property failureMetrics "FinalCursorBytes" = 0L && int64Property failureMetrics "FinalLiveStackBytes" = 0L
        recordCheck checks failures $"owning-stack/{optimizationName}/String/{name}/retained-capacity-atomicity" passed (jsonObject [
            "code", box failureCode
            "boundary", box boundary
            "requiredBytes", box required
            "availableBytes", box actualAvailable
            "oldCallerBytesUnchanged", box (sentinel = old)
            "metrics", if isNull failureMetrics then null else jsonNode options (metricSummary failureMetrics) ])
        retainedFailureReports.Add(box (jsonObject [ "case", box name; "requiredBytes", box required; "availableBytes", box actualAvailable; "bufferUnchanged", box (sentinel = old) ]))

    let longMetrics = metricsByCase["long-bmp-astral"]
    let highWater = int64Property (box longMetrics) "ReservedStackBytes" + int64Property (box longMetrics) "ReservedLocalBytes"
    let upperCapacity = int (max 0L (highWater - 1L))
    let mutable selectedStackFailure = None
    for available in upperCapacity .. -1 .. 0 do
        if selectedStackFailure.IsNone then
            let attemptOutput = Array.create 80 0xA5uy
            let attemptBefore = Array.copy attemptOutput
            try
                turnProgram.ExecuteInto([ longState ], available, attemptOutput) |> ignore
            with error ->
                if exceptionCode error = "OWNING_STACK_CAPACITY" then
                    let metrics = getProperty error "Metrics"
                    let deepCopy = if isNull metrics then 0L else int64Property metrics "DeepCopyBytes"
                    let frameReturns = if isNull metrics then 0L else int64Property metrics "FrameReturnCount"
                    let traceCount = if isNull metrics then 0L else int64Property metrics "TraceEventCount"
                    if deepCopy > 0L && frameReturns > 0L && traceCount > 0L then
                        selectedStackFailure <- Some(error, metrics, available, deepCopy, frameReturns, traceCount, attemptOutput, attemptBefore)
    match selectedStackFailure with
    | None -> recordCheck checks failures $"owning-stack/{optimizationName}/String/program-stack-capacity-after-concat" false (jsonObject [
        "searchUpperCapacityBytes", box upperCapacity
        "successfulStackAndLocalHighWaterBytes", box highWater
        "reason", box "No one-byte-under or lower stack capacity reached a capacity error after concat/copy events." ])
    | Some(error, metrics, available, deepCopy, frameReturns, traceCount, attemptOutput, attemptBefore) ->
        let capacityPassed =
            exceptionCode error = "OWNING_STACK_CAPACITY"
            && (getProperty error "Boundary" |> string) = "program-data-stack"
            && deepCopy > 0L
            && frameReturns > 0L
            && traceCount > 0L
            && int64Property metrics "FinalCursorBytes" = 0L
            && int64Property metrics "FinalLiveStackBytes" = 0L
            && attemptOutput = attemptBefore
        recordCheck checks failures $"owning-stack/{optimizationName}/String/program-stack-capacity-after-concat" capacityPassed (jsonObject [
            "capacityBytes", box available
            "requiredBytes", box (getProperty error "RequiredBytes")
            "availableBytes", box (getProperty error "AvailableBytes")
            "deepCopyBytesBeforeFailure", box deepCopy
            "helperFrameReturnsBeforeFailure", box frameReturns
            "traceEventCountBeforeFailure", box traceCount
            "callerBufferUnchanged", box (bufferCheckDetails attemptBefore attemptOutput)
            "metrics", box (jsonNode options (metricSummary metrics)) ])

    let stackFailureSummary: obj =
        match selectedStackFailure with
        | Some(error, metrics, available, deepCopy, frameReturns, traceCount, attemptOutput, attemptBefore) ->
            box (jsonObject [
                "availableBytes", box available
                "requiredBytes", box (getProperty error "RequiredBytes")
                "deepCopyBytesBeforeFailure", box deepCopy
                "helperFrameReturnsBeforeFailure", box frameReturns
                "traceEventCountBeforeFailure", box traceCount
                "callerBufferUnchanged", box (attemptOutput = attemptBefore)
                "metrics", jsonNode options (metricSummary metrics) ])
        | None -> null
    jsonObject [
        "backend", box $"inline-owning-stack-strings/{optimizationName}"
        "sameVerifiedProgramInstance", box true
        "layoutSchemaVersion", box layoutSchemaVersion
        "dynamicTypeLayouts", jsonNode options dynamicLayoutSummary
        "interpreterCases", box (interpreterReports.ToArray())
        "owningCases", box (owningReports.ToArray())
        "directValueStringRoundTrips", box (roundTripReports.ToArray())
        "joinedSurrogatePair", jsonObject [
            "expectedBytes", box (bytesHex joinedBytes)
            "actualBytes", box (bytesHex joinedOutput)
            "metrics", jsonNode options (metricSummary joinedMetrics)
            "concatSourceEvents", jsonNode options (layoutEventDetails concatEvents) ]
        "mixedEmptyStringIntRecord", jsonObject [
            "expectedRecordBytes", box (bytesHex mixedRecordBytes)
            "actualRecordBytes", box (bytesHex mixedRecordOutput)
            "projectedStringAndSentinelBytes", box (bytesHex mixedProjectionOutput)
            "projectedEmptyTokenBytes", box (bytesHex mixedEmptyProjectionOutput)
            "layout", box mixedLayoutDetails
            "constructMetrics", box (jsonNode options (metricSummary mixedConstructMetrics))
            "projectionMetrics", box (jsonNode options (metricSummary mixedProjectionMetrics))
            "emptyProjectionMetrics", box (jsonNode options (metricSummary (box mixedEmptyProjectionResult.Metrics)))
            "constructEvents", box (jsonNode options (layoutEventDetails mixedConstructEvents))
            "projectionEvents", box (jsonNode options (layoutEventDetails mixedProjectionResult.LayoutEvents))
            "emptyProjectionEvents", box (jsonNode options (layoutEventDetails mixedEmptyProjectionResult.LayoutEvents)) ]
        "dynamicBranchJoins", box (dynamicBranchReports.ToArray())
        "independentDynamicConcatCapacity", jsonObject [
            "requiredStackCapacityBytes", box requiredStackBytes
            "oneByteBelowAvailableBytes", box oneByteBelowCapacity
            "failureCode", box exactCapacityFailureCode
            "failureRequiredBytes", box exactCapacityFailureRequired
            "failureAvailableBytes", box exactCapacityFailureAvailable
            "failureCallerBufferUnchanged", box (exactCapacityFailureOutput = exactCapacityFailureBefore)
            "exactBoundarySucceeded", box exactCapacitySuccessPassed
            "exactBoundaryOutput", box (bytesHex exactCapacityOutput)
            "failureMetrics", if isNull exactCapacityFailureMetrics then null else jsonNode options (metricSummary exactCapacityFailureMetrics)
            "successMetrics", jsonNode options (metricSummary exactCapacityResultMetrics) ]
        "directDupDrop", jsonObject [
            "values", box (jsonNode options (codeUnitSafeValuesData duplicateResult.Values))
            "bytes", box (bytesHex duplicateOutput)
            "copyEvents", box (jsonNode options (layoutEventDetails duplicateCopyEvents))
            "dropEvents", box (jsonNode options (layoutEventDetails duplicateDropEvents))
            "metrics", box (jsonNode options (metricSummary (box duplicateResult.Metrics))) ]
        "scopeShadowCases", box (scopeReports.ToArray())
        "zeroOutputUserCall", jsonObject [
            "values", box (codeUnitSafeValuesJson zeroResult.Values)
            "retainedBytesWritten", box zeroResult.RetainedBytesWritten
            "metrics", jsonNode options (metricSummary zeroMetrics) ]
        "failureAfterConcat", jsonObject [
            "interpreterDiagnostic", box interpreterFailureCode
            "owningDiagnostic", box nativeFailure
            "metrics", if isNull nativeFailureMetrics then null else jsonNode options (metricSummary nativeFailureMetrics) ]
        "retainedCapacityFailures", box (retainedFailureReports.ToArray())
        "programStackCapacityFailure", stackFailureSummary ]

let private runLayoutDepthCases
    (checks: ResizeArray<obj>)
    (failures: ResizeArray<string>)
    (options: JsonSerializerOptions)
    (fixture: JsonElement)
    (artifactRoot: string)
    (optimization: LlvmOptimization)
    (optimizationName: string) =
    let boundary = fixture.GetProperty("stringWorkload").GetProperty("layoutDepthBoundary")
    let acceptedLayoutLevels = boundary.GetProperty("maximumAcceptedLayoutLevels").GetInt32()
    let acceptedRecordLevels = boundary.GetProperty("acceptedAcyclicRecordLevels").GetInt32()
    let acceptedTopType = boundary.GetProperty("acceptedTopType").GetString()
    let rejectedLayoutLevels = boundary.GetProperty("rejectedLayoutLevels").GetInt32()
    let rejectedRecordLevels = boundary.GetProperty("rejectedAcyclicRecordLevels").GetInt32()
    let rejectedTopType = boundary.GetProperty("rejectedTopType").GetString()
    let expectedDiagnostic = boundary.GetProperty("diagnosticCode").GetString()
    let supported = compileLayoutDepthCase acceptedRecordLevels
    let tooDeep = compileLayoutDepthCase rejectedRecordLevels
    let fixtureGeometryPassed =
        acceptedLayoutLevels = supported.LayoutLevels
        && rejectedLayoutLevels = tooDeep.LayoutLevels
        && supported.TopTypeName = acceptedTopType
        && tooDeep.TopTypeName = rejectedTopType
        && boundary.GetProperty("rootLevelIs").GetInt32() = 1
    let toolchain = LlvmToolchain.discover ()
    use supportedProgram = OwningStackAot.compile toolchain optimization (Path.Combine(artifactRoot, "owning-stack", "layout-depth", optimizationName, $"layout-{acceptedLayoutLevels}")) supported.Body
    let inputValue = layoutDepthValue supported
    let output = Array.create 8 0xA5uy
    let result = supportedProgram.ExecuteInto([ inputValue ], 4096, output)
    let expected = bytesFromInt64s [ 42L ]
    let layoutCount = result.Layouts |> List.length
    let supportedPassed = fixtureGeometryPassed && result.Values = [ inputValue ] && output = expected && result.RetainedOutputBytes = expected
    recordCheck checks failures $"owning-stack/{optimizationName}/layout-depth/{acceptedLayoutLevels}-layout-levels-accepted" supportedPassed (jsonObject [
        "rootLevelIsOne", box (boundary.GetProperty("rootLevelIs").GetInt32() = 1)
        "recordLevels", box supported.RecordLevels
        "layoutLevelsIncludingScalarLeaf", box supported.LayoutLevels
        "topType", box supported.TopTypeName
        "typeLayoutCount", box layoutCount
        "fixtureGeometryPassed", box fixtureGeometryPassed
        "outputValueCount", box result.Values.Length
        "outputMatchesDeepInput", box (result.Values = [ inputValue ])
        "expectedRawBytes", box (bytesHex expected)
        "actualRawBytes", box (bytesHex output)
        "metrics", jsonNode options (metricSummary (box result.Metrics)) ])
    checkTraceUsable checks failures $"owning-stack/{optimizationName}/layout-depth/{acceptedLayoutLevels}-level-trace-complete" result.Metrics result.LayoutEvents |> ignore
    let rejectedCode, rejectedException =
        try
            use _unexpected = OwningStackAot.compile toolchain optimization (Path.Combine(artifactRoot, "owning-stack", "layout-depth", optimizationName, $"layout-{rejectedLayoutLevels}")) tooDeep.Body
            "unexpected-success", null
        with error -> diagnosticCode error, error
    let rejectedPassed = fixtureGeometryPassed && rejectedCode = expectedDiagnostic
    recordCheck checks failures $"owning-stack/{optimizationName}/layout-depth/{rejectedLayoutLevels}-layout-levels-rejected-before-native-entry" rejectedPassed (jsonObject [
        "recordLevels", box tooDeep.RecordLevels
        "layoutLevelsIncludingScalarLeaf", box tooDeep.LayoutLevels
        "topType", box tooDeep.TopTypeName
        "diagnosticCode", box rejectedCode
        "expectedDiagnosticCode", box expectedDiagnostic
        "fixtureGeometryPassed", box fixtureGeometryPassed
        "diagnostic", if isNull rejectedException then null else box (rejectedException.ToString()) ])
    jsonObject [
        "optimization", box optimizationName
        "acceptedLayoutLevels", box acceptedLayoutLevels
        "acceptedRecordLevels", box acceptedRecordLevels
        "acceptedTypeLayoutCount", box layoutCount
        "acceptedMetrics", jsonNode options (metricSummary (box result.Metrics))
        "rejectedLayoutLevels", box rejectedLayoutLevels
        "rejectedRecordLevels", box rejectedRecordLevels
        "rejectedDiagnosticCode", box rejectedCode ]

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
        if fixture.GetProperty("schemaVersion").GetInt32() <> 2 then
            invalidOp "The current native-value-stack fixture must use schemaVersion 2."
        let expectedSourceHash = fixture.GetProperty("source").GetProperty("sha256").GetString()
        recordCheck checks failures "fixture/source-hash" (String.Equals(sourceHash, expectedSourceHash, StringComparison.Ordinal)) (jsonObject [ "expected", box expectedSourceHash; "actual", box sourceHash ])
        let repetitions = fixture.GetProperty("repetitions").EnumerateArray() |> Seq.map (fun item -> item.GetInt32()) |> Seq.toArray
        if repetitions <> [| 1; 8 |] then invalidOp "The independent fixture must retain repetition counts 1 and 8."
        let generatedTurns = repetitions |> Array.map turnSource
        let depthSource = depthChainSource 65
        let stringRecordMarker = "record TextLeaf {"
        let stringRecordStart = source.IndexOf(stringRecordMarker, StringComparison.Ordinal)
        if stringRecordStart < 0 then invalidOp "The source fixture no longer contains the String workload boundary marker."
        let fixedSource = source.Substring(0, stringRecordStart)
        let expandedFixedSource = String.concat "\n\n" [ fixedSource; String.concat "\n\n" generatedTurns; depthSource ]
        let expandedStringSource = String.concat "\n\n" [ source; String.concat "\n\n" generatedTurns; depthSource ]
        Directory.CreateDirectory(Path.GetDirectoryName reportPath) |> ignore
        Directory.CreateDirectory artifactsRoot |> ignore
        File.WriteAllText(Path.Combine(Path.GetDirectoryName reportPath, "expanded-fixed-source.flow"), expandedFixedSource, UTF8Encoding(false))
        File.WriteAllText(Path.Combine(Path.GetDirectoryName reportPath, "expanded-string-source.flow"), expandedStringSource, UTF8Encoding(false))
        let turnHashes =
            repetitions
            |> Array.mapi (fun index count -> box (jsonObject [ "repetitions", box count; "sha256", box (hashText generatedTurns[index]) ]))
        addCommonEntryReports report sourceHash expandedFixedSource turnHashes
        report["stringExpandedSourceSha256"] <- box (hashText expandedStringSource)
        report["depthChain"] <- box (jsonObject [ "maxDepth", box 65; "sourceSha256", box (hashText depthSource); "shape", box "Static acyclic function chain; chain-64 enters 65 user bodies (depth 0 through 64), chain-65 enters 66 and crosses the language boundary." ])
        report["fixturePath"] <- box fixturePathFromRepo
        let entries = compileEntries expandedFixedSource
        let scopeShadowPassed, scopeShadowDetails = inspectScopeShadow entries.Program entries.ScopeShadow
        recordCheck checks failures "verified-ir/scope-shadow-same-local-slot-width-restore" scopeShadowPassed scopeShadowDetails
        report["scopeShadowIr"] <- scopeShadowDetails
        report["sameVerifiedProgramInstance"] <- box true
        report["fixedControlVerifiedProgramInstance"] <- box true
        report["backendScope"] <- box "Within fixed controls, the same VerifiedIrBody objects are executed by interpreter, existing ABI3 LLVM sharedgraph, and inline owning-stack LLVM. ABI3 receives only the fixed nominal types."
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
        let stringEntries = compileStringEntries expandedStringSource
        let dynamicScopeShadowPassed, dynamicScopeShadowDetails = inspectDynamicScopeShadow stringEntries.Program stringEntries.ScopeShadow
        recordCheck checks failures "verified-ir/dynamic-scope-shadow-same-local-slot" dynamicScopeShadowPassed dynamicScopeShadowDetails
        report["stringScopeShadowIr"] <- dynamicScopeShadowDetails
        report["stringVerifiedProgramInstance"] <- box true
        report["stringBackendScope"] <- box "String cases share one compiler-authorized VerifiedIrProgram and exact VerifiedIrBody between interpreter and owning LLVM O0/O2. ABI3 String parity is unavailable because its closed native type registry rejects String and string.concat; fixed controls remain the three-way comparison. Interpreter fixture inputs are produced by compiler-verified literal factory bodies because its entry API accepts only Int/Bool/Unit arguments, while owning execution receives each varying nested TextState as a runtime Value input."
        let stringRuns = ResizeArray<obj>()
        let layoutDepthRuns = ResizeArray<obj>()
        for optimization, optimizationName in optimizationPairs do
            stringRuns.Add(box (runStringWorkload checks failures options fixture stringEntries artifactsRoot optimization optimizationName))
            layoutDepthRuns.Add(box (runLayoutDepthCases checks failures options fixture artifactsRoot optimization optimizationName))
        report["stringRuns"] <- stringRuns.ToArray()
        report["layoutDepthRuns"] <- layoutDepthRuns.ToArray()
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
