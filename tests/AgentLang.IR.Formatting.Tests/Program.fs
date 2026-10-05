module AgentLang.IR.Formatting.Tests

open System
open System.Text.Json
open System.Text.Json.Nodes
open AgentLang

let mutable private assertions = 0

let private check name condition =
    assertions <- assertions + 1
    if not condition then failwith $"{name}: assertion failed"

let private expectDiagnostic name code action =
    try
        action ()
        failwith $"{name}: expected diagnostic {code}"
    with
    | LanguageException diagnostic when diagnostic.Code = code -> assertions <- assertions + 1
    | LanguageException diagnostic ->
        failwith $"{name}: expected {code}, got {diagnostic.Code}: {diagnostic.Message}"

let private span line =
    { File = "formatting.agent"
      Line = line
      Column = 1
      Length = 8 }

let private wordEntry name inputs outputs effects body revision status =
    let definition =
        { Name = name
          Inputs = inputs
          Outputs = outputs
          Effects = effects
          Maturity = LibraryWord
          Revision = revision
          Documentation = "formatter fixture"
          Body = body
          SourceText = "fixture source"
          Span = span 1 }
    { Definition = definition
      Builtin = None
      Status = status
      Maturity = LibraryWord
      Revision = revision }

let private generatedEntry name builtin inputs outputs line =
    let definition =
        { Name = name
          Inputs = inputs
          Outputs = outputs
          Effects = Set.empty
          Maturity = LibraryWord
          Revision = 1
          Documentation = "generated formatter fixture"
          Body = []
          SourceText = "generated fixture"
          Span = span line }
    { Definition = definition
      Builtin = Some builtin
      Status = Persistent
      Maturity = LibraryWord
      Revision = 1 }

let private contextAndProgram () =
    let record =
        { Name = "Customer"
          Fields = [ { Name = "email"; Type = TNamed "Email" }; { Name = "active"; Type = TBool } ]
          SourceText = "record Customer"
          Span = span 2 }
    let scalar =
        { Name = "Email"
          BaseType = TString
          Validator = None
          SourceText = "type Email = String"
          Span = span 3 }
    let generated =
        [ "customer.new", generatedEntry "customer.new" (RecordConstructor "Customer") [ TNamed "Email"; TBool ] [ TNamed "Customer" ] 4
          "customer.email", generatedEntry "customer.email" (RecordAccessor("Customer", "email")) [ TNamed "Customer" ] [ TNamed "Email" ] 5
          "customer.active", generatedEntry "customer.active" (RecordAccessor("Customer", "active")) [ TNamed "Customer" ] [ TBool ] 6
          "Email.new", generatedEntry "Email.new" (ScalarConstructor "Email") [ TString ] [ TNamed "Email" ] 7
          "Email.value", generatedEntry "Email.value" (ScalarAccessor "Email") [ TNamed "Email" ] [ TString ] 8 ]
        |> Map.ofList
    let noEffects = Set.empty<string>
    let words =
        [ "customer.active?",
          wordEntry "customer.active?" [ TNamed "Customer" ] [ TBool ] noEffects
              [ Call("customer.active", span 10)
                If([ Push(LBool true, span 11) ], [ Push(LBool false, span 12) ], span 13) ] 2 Candidate
          "customer.has-email?",
          wordEntry "customer.has-email?" [ TNamed "Customer" ] [ TBool ] noEffects
              [ Call("customer.email", span 14)
                Call("Email.value", span 15)
                Push(LString "person@example.test", span 16)
                Call("equals", span 17) ] 3 Candidate
          "customers.active",
          wordEntry "customers.active" [ TList(TNamed "Customer") ] [ TList(TNamed "Customer") ] noEffects
              [ FilterList("customer.active?", span 18) ] 4 Candidate
          "customer.copy",
          wordEntry "customer.copy" [ TNamed "Customer" ] [ TNamed "Customer" ] noEffects
              [ Let("copy", span 19); Load("copy", span 20) ] 5 Candidate ]
        |> Map.ofList
    let allWords =
        Map.fold (fun acc name entry -> Map.add name entry acc) Compiler.primitives generated
        |> fun primitiveAndGenerated -> Map.fold (fun acc name entry -> Map.add name entry acc) primitiveAndGenerated words
    let wordIds =
        allWords
        |> Map.toList
        |> List.map (fun (name, entry) ->
            let prefix =
                match entry.Builtin with
                | Some(BuiltinOp _) -> "primitive-"
                | Some _ -> "generated-"
                | None -> "user-"
            name, WordId(prefix + name))
        |> Map.ofList
    let context: Compiler.IrLoweringContext =
        { Words = allWords
          Records = Map.ofList [ "Customer", record ]
          Scalars = Map.ofList [ "Email", scalar ]
          WordIds = wordIds }
    context, Compiler.compileIrProgram context

let private prop (key: string) (node: JsonNode) = node[key]
let private at (index: int) (node: JsonNode) = node.AsArray()[index]
let private stringValue (node: JsonNode) = node.GetValue<string>()
let private intValue (node: JsonNode) = node.GetValue<int>()
let private stringField key node = node |> prop key |> stringValue
let private intField key node = node |> prop key |> intValue

let private testUserFunctionJson () =
    let context, verified = contextAndProgram ()
    let document = IrFormatting.toData verified (IrFormatTarget.UserWordName "customer.active?")
    check "user target serializes as function DTO" (stringField "kind" document = "function")
    check "scope-capable formatter uses schema version 2 even for a scope-free function" (intField "formatVersion" document = 2)
    let (WordId activeId) = context.WordIds["customer.active?"]
    check "function retains stable ID and revision" (stringField "wordId" document = activeId && intField "revision" document = 2)
    check "function signature uses nominal display name" (document |> prop "inputs" |> at 0 |> stringField "display" = "Customer")

    let instructions = document |> prop "body" |> prop "instructions" |> fun node -> node.AsArray()
    let ifInstruction =
        instructions
        |> Seq.cast<JsonNode>
        |> Seq.find (fun instruction -> instruction |> prop "operation" |> stringField "kind" = "if")
    let operation = ifInstruction |> prop "operation"
    check "if operation includes both nested blocks" (prop "then" operation <> null && prop "else" operation <> null)
    check "then/else block DTOs preserve typed stack joins"
        (operation |> prop "then" |> prop "exit" |> prop "stack" |> at 0 |> stringField "display" = "Bool"
         && operation |> prop "else" |> prop "exit" |> prop "stack" |> at 0 |> stringField "display" = "Bool")

    let coverage = document |> prop "coverageObligations"
    check "coverage is explicitly obligations, not run traces" ((coverage |> stringField "meaning").Contains("not observed execution traces", StringComparison.Ordinal))
    let outcomes = coverage |> prop "branchOutcomes" |> at 0 |> prop "outcomes" |> fun node -> node.AsArray() |> Seq.cast<JsonNode> |> Seq.map stringValue |> Set.ofSeq
    check "if coverage lists both required outcomes" (outcomes = Set.ofList [ "true"; "false" ])

    let sourceSites = document |> prop "sourceSites" |> fun node -> node.AsArray()
    let firstSpan = sourceSites[0] |> prop "span"
    check "source map includes exact file and source line" (stringField "file" firstSpan = "formatting.agent" && intField "line" firstSpan >= 10)

    let text = IrFormatting.toText verified (IrFormatTarget.UserWordName "customer.active?")
    check "human text renders function and nested branch" (text.Contains("word customer.active?", StringComparison.Ordinal) && text.Contains("then:", StringComparison.Ordinal) && text.Contains("else:", StringComparison.Ordinal))

    let scoped =
        wordEntry "customer.scoped" [ TNamed "Customer" ] [ TBool ] Set.empty
            [ Scope([ Call("customer.active", span 30) ], span 31) ] 6 Candidate
    let scopedContext =
        { context with
            Words = Map.add "customer.scoped" scoped context.Words
            WordIds = Map.add "customer.scoped" (WordId "user-customer.scoped") context.WordIds }
    let scopedProgram = Compiler.compileIrProgram scopedContext
    let scopedDocument = IrFormatting.toData scopedProgram (IrFormatTarget.UserWordName "customer.scoped")
    check "Scope operation is emitted under the explicit version 2 schema"
        (intField "formatVersion" scopedDocument = 2
         && (scopedDocument |> prop "body" |> prop "instructions" |> at 0 |> prop "operation" |> stringField "kind") = "scope")

let private testCallbacksLocalsAndNominalClosure () =
    let context, verified = contextAndProgram ()
    let filter = IrFormatting.toData verified (IrFormatTarget.UserWordId context.WordIds["customers.active"])
    check "callback consumer is a function" (stringField "kind" filter = "function")
    let filterOperation = filter |> prop "body" |> prop "instructions" |> at 0 |> prop "operation"
    check "filter result preserves List<Customer>" (filterOperation |> prop "resultType" |> stringField "display" = "List<Customer>")
    let callback = filterOperation |> prop "callback"
    let callbackTarget = callback |> prop "target"
    check "callback links resolved authored ID and revision" (stringField "kind" callbackTarget = "user-word" && intField "revision" callbackTarget = 2 && stringField "name" callback = "customer.active?")
    check "callback contract is concrete" (callback |> prop "inputs" |> at 0 |> stringField "display" = "Customer" && callback |> prop "outputs" |> at 0 |> stringField "display" = "Bool")
    let branchLabels = filter |> prop "coverageObligations" |> prop "branchOutcomes" |> at 0 |> prop "outcomes" |> fun node -> node.AsArray() |> Seq.cast<JsonNode> |> Seq.map stringValue |> Set.ofSeq
    check "filter reports every coverage obligation" (branchLabels = Set.ofList [ "empty"; "nonempty"; "keep"; "drop" ])

    let email = IrFormatting.toData verified (IrFormatTarget.UserWordName "customer.has-email?")
    let nominalTypes = email |> prop "nominalTypes" |> fun node -> node.AsArray() |> Seq.cast<JsonNode> |> Seq.map (stringField "name") |> Set.ofSeq
    check "nominal type table contains only referenced closure" (nominalTypes = Set.ofList [ "Customer"; "Email" ])
    let nominalDtos = email |> prop "nominalTypes" |> fun node -> node.AsArray() |> Seq.cast<JsonNode> |> Seq.toList
    check "nominal keys are paired with readable names" (nominalDtos |> List.forall (fun value -> intField "typeKey" value >= 0 && not (String.IsNullOrWhiteSpace(stringField "name" value))))
    let customerDto = nominalDtos |> List.find (fun value -> stringField "name" value = "Customer")
    check "record fields retain nominal Email type" (customerDto |> prop "fields" |> at 0 |> prop "type" |> stringField "display" = "Email")

    let copy = IrFormatting.toData verified (IrFormatTarget.UserWordName "customer.copy")
    let exitLocals = copy |> prop "body" |> prop "exit" |> prop "locals" |> fun node -> node.AsArray()
    check "block shape exposes local slot, name, and closed type" (exitLocals.Count = 1 && stringField "name" exitLocals[0] = "copy" && exitLocals[0] |> prop "type" |> stringField "display" = "Customer")

let private testGeneratedAndPrimitiveDocuments () =
    let _, verified = contextAndProgram ()
    let generated = IrFormatting.toData verified (IrFormatTarget.GeneratedWordName "customer.email")
    check "generated target has explicit generated kind" (stringField "kind" generated = "generated-word")
    check "generated target retains stable ID and revision" (stringField "wordId" generated = "generated-customer.email" && intField "revision" generated = 1)
    let operation = generated |> prop "operation"
    check "generated field operation identifies nominal type and field index" (stringField "kind" operation = "get-record-field" && intField "fieldIndex" operation = 0)
    let sourceSpanNode = generated |> prop "source" |> prop "span"
    check "generated target includes source declaration location" (stringField "file" sourceSpanNode = "formatting.agent" && intField "line" sourceSpanNode = 2)

    let primitive = IrFormatting.toData verified (IrFormatTarget.PrimitiveContract(PrimitiveId "equals"))
    check "primitive contracts use the same current top-level schema version" (intField "formatVersion" primitive = 2)
    check "primitive output is explicitly a contract, not executable code" (stringField "kind" primitive = "primitive-contract" && prop "body" primitive = null)
    let inputPatterns = primitive |> prop "inputs" |> fun node -> node.AsArray()
    check "primitive contract retains generic type variable pattern" (stringField "kind" inputPatterns[0] = "variable" && intField "variableIndex" inputPatterns[0] = 0 && intField "variableIndex" inputPatterns[1] = 0)
    check "primitive aliases are listed separately from contract identity" (primitive |> prop "aliases" |> fun node -> node.AsArray() |> Seq.cast<JsonNode> |> Seq.exists (fun alias -> stringValue alias = "equals"))
    let primitiveText = IrFormatting.toText verified (IrFormatTarget.PrimitiveContract(PrimitiveId "equals"))
    check "primitive contract text labels the generic contract" (primitiveText.Contains("no executable function body", StringComparison.Ordinal))

let private testStructuredLookupErrors () =
    let _, verified = contextAndProgram ()
    expectDiagnostic "missing word name returns structured diagnostic" "IR_FORMAT_TARGET_NOT_FOUND" (fun () -> IrFormatting.toData verified (IrFormatTarget.UserWordName "missing") |> ignore)
    expectDiagnostic "missing generated ID returns structured diagnostic" "IR_FORMAT_TARGET_NOT_FOUND" (fun () -> IrFormatting.toData verified (IrFormatTarget.GeneratedWordId(WordId "missing")) |> ignore)
    expectDiagnostic "unknown primitive returns structured diagnostic" "IR_FORMAT_PRIMITIVE_NOT_FOUND" (fun () -> IrFormatting.toData verified (IrFormatTarget.PrimitiveContract(PrimitiveId "not-a-primitive")) |> ignore)
    let modelOnly = IrVerifier.verify Compiler.primitiveIrCatalog (VerifiedIrProgram.inspect verified)
    expectDiagnostic "model-only verifier handle is not exposed as executable formatting" "IR_BACKEND_UNTRUSTED_PROGRAM" (fun () -> IrFormatting.toData modelOnly (IrFormatTarget.UserWordName "customer.active?") |> ignore)

let private testProtocolDepthGuard () =
    let baseContext, _ = contextAndProgram ()
    let addCandidate name inputs outputs body revision =
        let entry = wordEntry name inputs outputs Set.empty body revision Candidate
        let updated: Compiler.IrLoweringContext =
            { baseContext with
                Words = Map.add name entry baseContext.Words
                WordIds = Map.add name (WordId("user-" + name)) baseContext.WordIds }
        updated, Compiler.compileIrProgram updated
    let envelopeJson node =
        let response = JsonObject()
        response["ok"] <- JsonValue.Create(true) :> JsonNode
        response["kind"] <- JsonValue.Create("inspect") :> JsonNode
        response["text"] <- JsonValue.Create("verified IR") :> JsonNode
        response["data"] <- node
        let serialized = Protocol.serializeResponse response
        use _document = JsonDocument.Parse(serialized)
        serialized

    let nestedList depth = [ 1 .. depth ] |> List.fold (fun ty _ -> TList ty) TInt
    let _, withinTypeProgram = addCandidate "deep-type-ok" [ nestedList 10 ] [ nestedList 10 ] [] 20
    let withinType = IrFormatting.toData withinTypeProgram (IrFormatTarget.UserWordName "deep-type-ok")
    check "deep but supported type DTO serializes through the default Protocol envelope" (envelopeJson withinType |> fun json -> json.Contains("deep-type-ok", StringComparison.Ordinal))

    let _, overTypeProgram = addCandidate "deep-type-over" [ nestedList 28 ] [ nestedList 28 ] [] 21
    expectDiagnostic "deep type exceeding protocol budget is a structured formatter limit" "IR_FORMAT_LIMIT_EXCEEDED" (fun () -> IrFormatting.toData overTypeProgram (IrFormatTarget.UserWordName "deep-type-over") |> ignore)
    expectDiagnostic "over-depth JSON formatter reports the same structured limit" "IR_FORMAT_LIMIT_EXCEEDED" (fun () -> IrFormatting.toJson overTypeProgram (IrFormatTarget.UserWordName "deep-type-over") |> ignore)
    expectDiagnostic "over-depth text formatter reports the same structured limit" "IR_FORMAT_LIMIT_EXCEEDED" (fun () -> IrFormatting.toText overTypeProgram (IrFormatTarget.UserWordName "deep-type-over") |> ignore)

    let rec conditionalBody depth lineNumber =
        if depth <= 1 then [ Push(LBool true, span lineNumber) ]
        else
            [ Push(LBool true, span lineNumber)
              If(conditionalBody (depth - 1) (lineNumber + 1), [ Push(LBool false, span (lineNumber + 100)) ], span (lineNumber + 200)) ]
    let _, withinBranchesProgram = addCandidate "deep-branches-ok" [] [ TBool ] (conditionalBody 8 30) 22
    let withinBranches = IrFormatting.toData withinBranchesProgram (IrFormatTarget.UserWordName "deep-branches-ok")
    check "nested branches within the bound serialize through Protocol" (envelopeJson withinBranches |> fun json -> json.Contains("deep-branches-ok", StringComparison.Ordinal))

    let _, overBranchesProgram = addCandidate "deep-branches-over" [] [ TBool ] (conditionalBody 16 60) 23
    expectDiagnostic "nested branches exceeding protocol budget are a structured formatter limit" "IR_FORMAT_LIMIT_EXCEEDED" (fun () -> IrFormatting.toData overBranchesProgram (IrFormatTarget.UserWordName "deep-branches-over") |> ignore)

let private tests =
    [ "verified authored function and branches", testUserFunctionJson
      "callbacks locals and nominal closure", testCallbacksLocalsAndNominalClosure
      "generated and primitive documents", testGeneratedAndPrimitiveDocuments
      "structured lookup errors", testStructuredLookupErrors
      "protocol depth guard", testProtocolDepthGuard ]

[<EntryPoint>]
let main _ =
    try
        for name, test in tests do
            test ()
            printfn "PASS %s" name
        printfn "AgentLang.IR.Formatting.Tests: %d assertions passed." assertions
        0
    with error ->
        eprintfn "%s" error.Message
        1
