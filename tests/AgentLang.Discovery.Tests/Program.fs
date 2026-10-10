namespace AgentLang.DiscoveryTests

open System
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open AgentLang

module Program =
    let mutable private assertions = 0

    let private check condition message =
        assertions <- assertions + 1
        if not condition then failwith message

    let private equal expected actual message =
        assertions <- assertions + 1
        if expected <> actual then failwith $"{message}: expected {expected}, got {actual}"

    let private span =
        { File = "discovery-test.agent"
          Line = 1
          Column = 1
          Length = 1 }

    let private word name inputs outputs effects body documentation builtin =
        let definition =
            { Name = name
              Inputs = inputs
              Outputs = outputs
              Effects = effects
              Maturity = LibraryWord
              Revision = 1
              Documentation = documentation
              Body = body
              SourceText = ""
              Span = span }
        { Definition = definition
          Builtin = builtin
          Status = if builtin.IsSome then Primitive else Persistent
          Maturity = definition.Maturity
          Revision = definition.Revision }

    let private call name = Call(name, span)

    let private record name (fields: RecordField list) : RecordEntry =
        { Definition =
            { Name = name
              Fields = fields
              Validator = None
              SourceText = ""
              Span = span }
          Status = Persistent }

    let private scalar name baseType validator : ScalarEntry =
        { Definition =
            { Name = name
              BaseType = baseType
              Validator = validator
              SourceText = ""
              Span = span }
          Status = Persistent }

    let private makeIndex () =
        let words =
            [ word "root"
                  [ TNamed "Customer" ]
                  [ TResult(TList(TOption(TNamed "Email")), TNamed "CustomerId") ]
                  (Set.singleton "db.read")
                  [ call "a"; call "b"; call "email.new"; MapList("list.callback", span) ]
                  ("Root context with multibyte text: café 😀 — 東京 <tag>&.\n\n" + String.replicate 8192 "z")
                  None
              word "a" [ TList(TNamed "Email") ] [ TBool ] Set.empty [ call "c" ] "Left branch." None
              word "b" [ TString ] [ TBool ] Set.empty [ call "c" ] "Right branch." None
              word "c" [ TBool ] [ TOption(TNamed "CustomerId") ] Set.empty [ call "root" ] "Closes the cycle." None
              word "email.new" [ TString ] [ TNamed "Email" ] Set.empty [] "Constructs a validated Email." (Some(ScalarConstructor "Email"))
              word "email.validate" [ TString ] [ TBool ] Set.empty [ call "string.check" ] "Checks the Email base value." None
              word "string.check" [ TString ] [ TBool ] Set.empty [] "Pure primitive check." (Some(BuiltinOp "string.contains"))
              word "list.callback" [ TNamed "Email" ] [ TBool ] Set.empty [ call "string.check" ] "Callback used by a list operation." None
              word "list.owner" [ TList(TNamed "Email") ] [ TList TBool ] Set.empty [ MapList("list.callback", span) ] "Uses a static callback." None
              word "customer.email-text" [ TNamed "Customer" ] [ TString ] Set.empty [] "Returns the raw representation." None
              word "observed.read" [] [ TUnit ] (Set.singleton "network.read") [] "Conservative effect declaration." None ]
            |> List.map (fun entry -> entry.Definition.Name, entry)
            |> Map.ofList

        let records =
            [ record "Customer"
                  [ { Name = "id"; Type = TNamed "CustomerId" }
                    { Name = "email"; Type = TNamed "Email" }
                    { Name = "address"; Type = TNamed "Address" } ]
              record "Address" [ { Name = "region"; Type = TNamed "Region" }; { Name = "line"; Type = TString } ] ]
            |> List.map (fun entry -> entry.Definition.Name, entry)
            |> Map.ofList

        let scalars =
            [ scalar "Email" TString (Some "email.validate")
              scalar "CustomerId" TString None
              scalar "Region" TString None ]
            |> List.map (fun entry -> entry.Definition.Name, entry)
            |> Map.ofList

        Discovery.build words records scalars Map.empty

    let private expectDiagnostic expectedCode action =
        try
            action () |> ignore
            failwith $"expected diagnostic {expectedCode}"
        with
        | LanguageException diagnostic when diagnostic.Code = expectedCode ->
            assertions <- assertions + 1
        | _ -> reraise ()

    let private protocolDataJson (content: string) =
        let response = JsonObject()
        response["ok"] <- JsonValue.Create true
        response["data"] <- JsonNode.Parse content
        let serializedResponse = Protocol.serializeResponse response
        use parsedResponse = JsonDocument.Parse serializedResponse
        parsedResponse.RootElement.GetProperty("data").GetRawText()

    let private testSearchAndDeclaredEffects index =
        equal
            [ "a"; "email.new"; "list.callback"; "list.owner"; "root" ]
            (Discovery.searchType index (TNamed "Email"))
            "nested nominal search includes exact Email occurrences"
        equal
            [ "b"; "customer.email-text"; "email.new"; "email.validate"; "string.check" ]
            (Discovery.searchType index TString)
            "String search is distinct from nominal scalar search"
        equal
            [ "customer.email-text" ]
            (Discovery.searchOutput index TString)
            "output search examines only output subtrees"
        equal
            [ "email.new"; "root" ]
            (Discovery.searchOutput index (TNamed "Email"))
            "nominal output search is exact"
        equal [ "observed.read" ] (Discovery.searchEffect index "network.read") "effect search uses declared effects"
        equal [ "root" ] (Discovery.searchEffect index "db.read") "effect query preserves declaration"
        equal [ "a"; "b" ] (Discovery.searchDependency index "c") "dependency search returns direct callers"
        equal [ "list.owner"; "root" ] (Discovery.searchDependency index "list.callback") "callback references are direct dependencies"

    let private testGraphAndCycles index =
        equal [ "a"; "b"; "email.new"; "list.callback" ] (Discovery.dependencies index "root") "direct dependencies sort deterministically"
        equal
            [ "a"; "b"; "c"; "email.new"; "email.validate"; "list.callback"; "string.check" ]
            (Discovery.transitiveDependencies index "root")
            "transitive dependencies are finite and exclude the root"
        equal [ "a"; "b"; "c" ] (Discovery.transitiveCallers index "root") "transitive callers are finite and exclude the root"
        equal [ "email.validate"; "string.check" ] (Discovery.transitiveDependencies index "email.new") "generated scalar constructor reaches validator closure"

        let full = Discovery.graphText index "root" 20 100
        check (full.Text.Contains("[cycle]", StringComparison.Ordinal)) "graph renders explicit cycle markers"
        check (full.Text.Contains("[reused]", StringComparison.Ordinal)) "graph renders shared dependency markers"
        equal full.ExpandedWords (Discovery.graphText index "root" 20 100).ExpandedWords "graph expansion order is deterministic"

        let depthLimited = Discovery.graphText index "root" 0 100
        check depthLimited.Truncated "depth-limited graph marks truncation"
        check (depthLimited.Text.Contains("[depth limit]", StringComparison.Ordinal)) "graph reports depth limit"
        equal 7 depthLimited.OmittedWords "depth limit reports omitted unique words"

        let nodeLimited = Discovery.graphText index "root" 20 2
        check nodeLimited.Truncated "node-limited graph marks truncation"
        check (nodeLimited.Text.Contains("[node limit]", StringComparison.Ordinal)) "graph reports node limit"
        equal 6 nodeLimited.OmittedWords "node limit reports omitted unique words"

    let private testValidation index =
        expectDiagnostic "DISCOVERY_UNKNOWN_ROOT" (fun () -> Discovery.dependencies index "missing")
        expectDiagnostic "DISCOVERY_INVALID_QUERY" (fun () -> Discovery.searchEffect index " ")
        expectDiagnostic "DISCOVERY_INVALID_BUDGET" (fun () -> Discovery.context index "root" -1 10 10000)
        expectDiagnostic "DISCOVERY_INVALID_BUDGET" (fun () -> Discovery.context index "root" 1 0 10000)

        let badDefinition =
            word "bad"
                [ TNamed "MissingType" ]
                [ TUnit ]
                Set.empty
                []
                ""
                None
        let badWords = Map.ofList [ "bad", badDefinition ]
        expectDiagnostic "DISCOVERY_UNKNOWN_TYPE" (fun () -> Discovery.build badWords Map.empty Map.empty Map.empty)

        let badCall = word "bad.call" [] [ TUnit ] Set.empty [ call "missing.word" ] "" None
        expectDiagnostic "DISCOVERY_UNKNOWN_WORD" (fun () -> Discovery.build (Map.ofList [ "bad.call", badCall ]) Map.empty Map.empty Map.empty)

        let missingValidator = scalar "Validated" TString (Some "missing.validator")
        expectDiagnostic "DISCOVERY_UNKNOWN_VALIDATOR" (fun () -> Discovery.build Map.empty Map.empty (Map.ofList [ "Validated", missingValidator ]) Map.empty)

        let missingConstructor = word "bad.constructor" [ TString ] [ TNamed "MissingScalar" ] Set.empty [] "" (Some(ScalarConstructor "MissingScalar"))
        expectDiagnostic "DISCOVERY_UNKNOWN_SCALAR" (fun () -> Discovery.build (Map.ofList [ "bad.constructor", missingConstructor ]) Map.empty Map.empty Map.empty)

        expectDiagnostic "DISCOVERY_INVALID_BUDGET" (fun () -> Discovery.graphText index "root" 1 0)

    let private testContextBudgets index =
        let complete = Discovery.context index "root" 20 100 100000
        equal complete (Discovery.context index "root" 20 100 100000) "context serialization is deterministic"
        equal (Encoding.UTF8.GetByteCount complete.Content) complete.Utf8Bytes "reported count measures complete UTF-8 JSON"
        let parsed = JsonNode.Parse complete.Content
        equal complete.Utf8Bytes (parsed["utf8Bytes"].GetValue<int>()) "serialized metadata includes its exact byte count"
        check (complete.Content.Contains("\\u003C", StringComparison.Ordinal) && complete.Content.Contains("\\u0026", StringComparison.Ordinal)) "compact JSON escapes HTML-special documentation and type text"
        let transportedComplete = protocolDataJson complete.Content
        equal complete.Content transportedComplete "Protocol preserves the complete Discovery JSON representation"
        equal complete.Utf8Bytes (Encoding.UTF8.GetByteCount transportedComplete) "Protocol payload byte count matches Discovery metadata"
        let rootNode = parsed["words"].AsArray() |> Seq.find (fun node -> node["name"].GetValue<string>() = "root")
        check (rootNode["documentationTruncated"].GetValue<bool>()) "context flags omitted documentation"
        check (rootNode["documentation"].GetValue<string>().Length < 256) "long documentation is reduced to its first paragraph"
        check (rootNode["documentation"].GetValue<string>().Contains("café 😀 — 東京", StringComparison.Ordinal)) "JSON preserves decoded multibyte documentation text"
        let compactRoot = Discovery.context index "root" 0 1 2048
        check (compactRoot.Utf8Bytes <= 2048) "long source documentation does not prevent a compact root context"
        equal [ "root"; "a"; "b"; "email.new"; "list.callback"; "c"; "email.validate"; "string.check" ] complete.WordsIncluded "context word order is breadth-first and stable"
        equal [ "Address"; "Customer"; "CustomerId"; "Email"; "Region" ] complete.TypesIncluded "context follows nested record and scalar type closure"

        let depthBounded = Discovery.context index "root" 0 100 100000
        equal [ "root" ] depthBounded.WordsIncluded "depth limit keeps root"
        check (List.contains "maxDepth" depthBounded.TruncationReasons) "context reports depth omissions"

        let wordBounded = Discovery.context index "root" 20 2 100000
        equal [ "root"; "a" ] wordBounded.WordsIncluded "word limit preserves BFS prefix"
        check (List.contains "maxWords" wordBounded.TruncationReasons) "context reports word omissions"

        let rootOnly = Discovery.context index "root" 20 1 100000
        let mutable noEntryFit: (int * DiscoveryContextResult) option = None
        let mutable budget = rootOnly.Utf8Bytes
        while noEntryFit.IsNone && budget <= complete.Utf8Bytes do
            try
                let result = Discovery.context index "root" 20 100 budget
                if result.WordsIncluded = [ "root" ] && List.contains "maxUtf8Bytes" result.TruncationReasons then
                    noEntryFit <- Some(budget, result)
            with
            | LanguageException diagnostic when diagnostic.Code = "DISCOVERY_CONTEXT_BUDGET_TOO_SMALL" -> ()
            budget <- budget + 1
        match noEntryFit with
        | None -> failwith "expected a budget that fits the root report but no whole dependency word"
        | Some(limit, result) ->
            check (result.Utf8Bytes <= limit) "no-entry-fits report respects the complete byte budget"
            equal (Encoding.UTF8.GetByteCount result.Content) result.Utf8Bytes "no-entry-fits report count matches emitted JSON"
            equal 7 result.WordsOmitted "no-entry-fits report counts omitted words"
            let transported = protocolDataJson result.Content
            equal result.Content transported "tight-budget context survives actual Protocol response serialization"
            equal result.Utf8Bytes (Encoding.UTF8.GetByteCount transported) "tight-budget transport keeps its byte count exact"
            check (Encoding.UTF8.GetByteCount transported <= limit) "tight-budget transport remains under the caller's byte budget"

        expectDiagnostic "DISCOVERY_CONTEXT_BUDGET_TOO_SMALL" (fun () -> Discovery.context index "root" 20 100 1)

    let private testContextFlowReferences () =
        let referenceWords =
            [ word "root" [] [ TInt ] Set.empty [] "Unqualified dictionary key." None
              word "advance" [] [ TInt ] Set.empty [] "Root name collision." None
              word "math.advance" [] [ TInt ] Set.empty [] "Dotted name collision." None
              word "tools.math.advance" [] [ TInt ] Set.empty [] "Qualified name collision." None
              word "Advance" [] [ TInt ] Set.empty [] "Case-preserved root name." None
              word "Math.Advance" [] [ TInt ] Set.empty [] "Case-preserved dotted name." None
              word "if.target" [] [ TInt ] Set.empty [] "Reserved syntax prefix." None
              word "match.target" [] [ TInt ] Set.empty [] "Reserved syntax prefix." None
              word "true.target" [] [ TInt ] Set.empty [] "Reserved syntax prefix." None
              word "false.target" [] [ TInt ] Set.empty [] "Reserved syntax prefix." None
              word "unit.target" [] [ TInt ] Set.empty [] "Reserved syntax prefix." None
              word "list.empty" [] [ TList TInt ] Set.empty [] "Generic constructor without type arguments." None
              word "list.empty<Int>" [] [ TList TInt ] Set.empty [] "Legacy key intercepted by container syntax." None
              word "Email.new" [ TString ] [ TNamed "Email" ] Set.empty [] "Capitalized scalar constructor." (Some(ScalarConstructor "Email"))
              word "email.new" [ TString ] [ TNamed "Email" ] Set.empty [] "Lowercase scalar constructor." (Some(ScalarConstructor "Email")) ]
            |> List.map (fun entry -> entry.Definition.Name, entry)
            |> Map.ofList
        let referenceIndex =
            Discovery.build
                referenceWords
                Map.empty
                (Map.ofList [ "Email", scalar "Email" TString None ])
                Map.empty

        let contextWord name =
            let context = Discovery.context referenceIndex name 0 1 100000
            let document = JsonNode.Parse(context.Content)
            document["words"].AsArray() |> Seq.exactlyOne

        let expectReference name expected =
            let node = contextWord name
            equal name (node["name"].GetValue<string>()) $"context preserves dictionary key {name}"
            equal expected (node["flowReference"].GetValue<string>()) $"context exposes exact Flow call reference for {name}"
            equal 2 (node["flowReferenceSyntaxVersion"].GetValue<int>()) $"context reports Flow/2 call reference syntax for {name}"
            check (isNull node["flowReferenceUnavailableReason"]) $"callable context entry {name} has no unavailable reason"
            equal (Some expected, None) (FlowParser.describeCallReference name) $"shared resolver accepts {name}"
            let parsed =
                FlowParser.parseExpressionWithVersion 2 "<discovery-flow-reference>" (expected + "()")
                |> Result.defaultWith (Diagnostics.render >> failwith)
            let parsedTarget =
                match parsed with
                | FlowExpression.Call(target, _, _) -> target
                | FlowExpression.RootCall(target, _, _) -> target.Name
                | _ -> failwith $"Flow reference for {name} did not parse as an exact call."
            equal name parsedTarget $"Flow/2 exact reference parses to dictionary key {name}"

        expectReference "root" ".root"
        expectReference "advance" ".advance"
        expectReference "math.advance" ".math.advance"
        expectReference "tools.math.advance" ".tools.math.advance"
        expectReference "Advance" ".Advance"
        expectReference "Math.Advance" ".Math.Advance"
        expectReference "Email.new" ".Email.new"
        expectReference "email.new" ".email.new"

        for prefix in [ "if"; "match"; "true"; "false"; "unit" ] do
            let name = prefix + ".target"
            expectReference name ("." + name)

        expectReference "list.empty" ".list.empty"

        let intercepted = contextWord "list.empty<Int>"
        check (isNull intercepted["flowReference"]) "context omits a typed constructor key intercepted by container syntax"
        equal
            "The candidate is intercepted by Flow syntax instead of an ordinary call."
            (intercepted["flowReferenceUnavailableReason"].GetValue<string>())
            "context explains a typed suffix that cannot be called as a Flow dictionary reference"
        equal
            (None, Some "The candidate is intercepted by Flow syntax instead of an ordinary call.")
            (FlowParser.describeCallReference "list.empty<Int>")
            "shared resolver preserves parser-proof rejection for typed malformed references"

    [<EntryPoint>]
    let main _ =
        let index = makeIndex ()
        testSearchAndDeclaredEffects index
        testGraphAndCycles index
        testValidation index
        testContextBudgets index
        testContextFlowReferences ()
        printfn $"AgentLang.Discovery.Tests: {assertions} assertions passed."
        0
