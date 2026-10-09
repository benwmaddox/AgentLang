namespace AgentLang.Flow.Runtime.Tests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
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

    let private jstr (value: string) = JsonValue.Create(value) :> JsonNode
    let private jint (value: int) = JsonValue.Create(value) :> JsonNode
    let private jbool (value: bool) = JsonValue.Create(value) :> JsonNode
    let private stringValue (node: JsonNode) = node.GetValue<string>()
    let private boolValue (node: JsonNode) = node.GetValue<bool>()

    let private strings values =
        let result = JsonArray()
        for value in values do result.Add(jstr value)
        result :> JsonNode

    let private digest (text: string) =
        UTF8Encoding(false, true).GetBytes text
        |> SHA256.HashData
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    let private arguments (values: (string * JsonNode) list) =
        let result = JsonObject()
        for name, value in values do
            result.[name] <- if isNull value then null else value.DeepClone()
        result

    let private dispatch (engine: Runtime.Engine) operation values =
        engine.Dispatch(operation, arguments values)

    let private succeeded (response: JsonObject) = boolValue (response.["ok"])

    let private expectOk label (response: JsonObject) =
        if not (succeeded response) then
            let code =
                try stringValue (response.["error"].["code"])
                with _ -> "unknown"
            let message =
                try stringValue (response.["error"].["message"])
                with _ -> response.ToJsonString()
            failwith $"{label}: expected success, got {code}: {message}"
        response

    let private expectError expectedCode (response: JsonObject) =
        check (not (succeeded response)) $"expected {expectedCode}, got success: {response.ToJsonString()}"
        let actualCode = stringValue (response.["error"].["code"])
        equal expectedCode actualCode "runtime diagnostic code"
        response

    let private errorCode (response: JsonObject) = stringValue (response.["error"].["code"])

    let private newRoot () =
        let path = Path.Combine(Path.GetTempPath(), $"agentlang-flow-runtime-{Guid.NewGuid():N}")
        Directory.CreateDirectory path |> ignore
        path

    let private jsonArrayStrings (node: JsonNode) =
        node.AsArray() |> Seq.map stringValue |> Seq.toList

    let private flowWordSource =
        "word durable.increment(value: Int) -> Int {\n"
        + "    effects none\n"
        + "    add(value, 1)\n"
        + "}"

    let private flowTestSource =
        "test durable.increment/basic {\n"
        + "    durable::increment(41)\n"
        + "    => value durable::increment(41)\n"
        + "}"

    let private flowExampleSource =
        "example durable.increment/one {\n"
        + "    durable::increment(41)\n"
        + "    => 42\n"
        + "}"

    let private defineFlow (engine: Runtime.Engine) source tests examples extra =
        let fields =
            [ "frontend", jstr "flow"
              "source", jstr source ]
            @ (if List.isEmpty tests then [] else [ "tests", strings tests ])
            @ (if List.isEmpty examples then [] else [ "examples", strings examples ])
            @ extra
        dispatch engine "define" fields

    let private defineFlowProject (engine: Runtime.Engine) source extra =
        dispatch engine "define" ([ "frontend", jstr "flow"; "source", jstr source ] @ extra)

    let private defineStack (engine: Runtime.Engine) source extra =
        dispatch engine "define" ([ "frontend", jstr "stack"; "source", jstr source ] @ extra)

    let private commit (engine: Runtime.Engine) operation name extra =
        dispatch engine operation ([ "word", jstr name ] @ extra)

    let private evalFlow (engine: Runtime.Engine) code =
        dispatch engine "eval" [ "frontend", jstr "flow"; "code", jstr code ]

    let private evalStack (engine: Runtime.Engine) code =
        dispatch engine "eval" [ "frontend", jstr "stack"; "code", jstr code ]

    let private cliEval project code =
        let outputDirectory = DirectoryInfo(AppContext.BaseDirectory.TrimEnd([| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |]))
        let configuration = outputDirectory.Parent.Name
        let repositoryRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
        let configuredCli = Environment.GetEnvironmentVariable("AGENTLANG_TEST_CLI")
        let cliAssembly =
            if String.IsNullOrWhiteSpace configuredCli then
                Path.Combine(repositoryRoot, "src", "AgentLang.Cli", "bin", configuration, "net9.0", "AgentLang.Cli.dll")
            else
                Path.GetFullPath configuredCli
        check (File.Exists cliAssembly) $"CLI assembly exists for the {configuration} integration-test build"
        let startInfo = ProcessStartInfo("dotnet")
        startInfo.UseShellExecute <- false
        startInfo.RedirectStandardInput <- true
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.ArgumentList.Add cliAssembly
        startInfo.ArgumentList.Add "--project"
        startInfo.ArgumentList.Add project
        startInfo.ArgumentList.Add "--jsonl"
        use childProcess = new Process(StartInfo = startInfo)
        if not (childProcess.Start()) then failwith "Could not start the AgentLang CLI integration process."
        let request = JsonObject()
        request.["op"] <- jstr "eval"
        request.["frontend"] <- jstr "flow"
        request.["code"] <- jstr code
        childProcess.StandardInput.WriteLine(request.ToJsonString())
        childProcess.StandardInput.Close()
        if not (childProcess.WaitForExit 30000) then
            childProcess.Kill(true)
            failwith "The AgentLang CLI did not exit after its JSON-lines input closed."
        let output = childProcess.StandardOutput.ReadToEnd()
        let error = childProcess.StandardError.ReadToEnd()
        check (childProcess.ExitCode = 0) $"fresh-process Flow CLI exits successfully: {error}"
        let line =
            output.Split([| "\r\n"; "\n" |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.tryHead
            |> Option.defaultWith (fun () -> failwith $"The AgentLang CLI returned no JSON response. stderr: {error}")
        JsonNode.Parse(line).AsObject()

    let private findWord (response: JsonObject) name =
        (response.["data"].["words"]).AsArray()
        |> Seq.find (fun entry -> stringValue (entry.["name"]) = name)

    let private getWordId (engine: Runtime.Engine) name =
        let words = dispatch engine "words" [] |> expectOk "words"
        let word = findWord words name
        stringValue (word.["id"])

    let private assertAllPassed expectedCount (response: JsonObject) =
        let results = (response.["data"].["results"]).AsArray()
        equal expectedCount results.Count "test result count"
        for item in results do
            let name = stringValue (item.["name"])
            check (boolValue (item.["passed"])) $"test {name} passed"

    let private testAuthoringHelpAndCanonicalLibrarySource root =
        let project = Path.Combine(root, "authoring-help-canonical")
        let engine = Runtime.Engine(project, Set.empty, "2041-02-03T04:05:06Z")
        let index = dispatch engine "help" [] |> expectOk "read the default authoring-help index"
        equal "help" (stringValue index.["kind"]) "help is a JSONL operation"
        let indexData = index.["data"]
        equal 1 (indexData.["schemaVersion"].GetValue<int>()) "help schema version"
        equal [ "authoring"; "define"; "replacement"; "examples" ] (jsonArrayStrings indexData.["topics"]) "help topic order is stable"
        equal "authoring" (stringValue indexData.["topic"]) "omitted help topic selects authoring index"
        let originalIndexJson = index.ToJsonString()
        let repeatedIndex = dispatch engine "help" [] |> expectOk "repeat default authoring help"
        equal originalIndexJson (repeatedIndex.ToJsonString()) "help payload is deterministic across requests"
        (indexData.AsObject()).["documentation"] <- jstr "caller mutation"
        let freshIndex = dispatch engine "help" [] |> expectOk "read fresh default help after caller mutation"
        check (stringValue freshIndex.["data"].["documentation"] <> "caller mutation") "one response cannot mutate later help payloads"
        let discoveryGuidance = stringValue freshIndex.["data"].["documentation"]
        for guidance in [ "compact=true"; "search"; "describe"; "flowReference"; "context"; "transitive-dependencies"; "dictionary names with dots" ] do
            check (discoveryGuidance.Contains(guidance, StringComparison.Ordinal)) $"default help explains discovery with {guidance}"

        for name, stackSyntax, flow2Syntax in
            [ "list.map", "list.map <word>", "items.map(callback)"
              "list.filter", "list.filter <word>", "items.filter(callback)"
              "list.each", "list.each <word>", "items.each(callback)"
              "list.fold", "list.fold <word>", "items.fold(seed, callback)" ] do
            let descriptor =
                dispatch engine "describe" [ "word", jstr name ]
                |> expectOk $"describe {name} syntax metadata"
                |> fun response -> response.["data"]
            equal stackSyntax (stringValue descriptor.["syntax"]) $"{name} keeps its Stack syntax spelling"
            equal flow2Syntax (stringValue descriptor.["flow2Syntax"]) $"{name} exposes its Flow/2 receiver spelling"
            let flow2Name = flow2Syntax.Split('(')[0]
            let search =
                dispatch engine "search" [ "query", jstr flow2Name ]
                |> expectOk $"search Flow/2 syntax for {name}"
            check (jsonArrayStrings search.["data"] |> List.contains name) $"search finds the Flow/2 form for {name}"

        equal [ "authoring"; "define"; "replacement"; "examples" ]
            (freshIndex.["data"].["topicInstructions"].AsArray() |> Seq.map (fun item -> stringValue item.["topic"]) |> Seq.toList)
            "index includes one instruction per help topic"

        let discoveryExampleNames = [ "compact-words"; "search-add"; "context-add" ]
        let findRequestExample (helpData: JsonNode) name =
            helpData.["requestExamples"].AsArray()
            |> Seq.find (fun item -> stringValue item.["name"] = name)
            |> fun item -> item.["request"]
        for versionLabel, helpData in
            [ "Flow/1", freshIndex.["data"]
              "Flow/2", (dispatch engine "help" [ "syntaxVersion", jint 2 ] |> expectOk "read Flow/2 discovery help").["data"] ] do
            let documentation = stringValue helpData.["documentation"]
            for guidance in [ "compact=true"; "search"; "context"; "transitive-dependencies" ] do
                check (documentation.Contains(guidance, StringComparison.Ordinal)) $"{versionLabel} help exposes discovery guidance for {guidance}"
            for exampleName in discoveryExampleNames do
                check
                    (helpData.["requestExamples"].AsArray()
                     |> Seq.exists (fun item -> stringValue item.["name"] = exampleName))
                    $"{versionLabel} help exposes the {exampleName} request"

            let dispatchHelpRequest name =
                Protocol.dispatchLine engine ((findRequestExample helpData name).ToJsonString())
                |> expectOk $"execute {versionLabel} help example {name} through JSONL protocol"
            let compactWords = dispatchHelpRequest "compact-words"
            equal "words" (stringValue compactWords.["kind"]) $"{versionLabel} compact inventory request kind"
            check (boolValue compactWords.["data"].["compact"]) $"{versionLabel} compact inventory request selects names-only shape"
            check
                (jsonArrayStrings compactWords.["data"].["words"] |> List.contains "add")
                $"{versionLabel} compact inventory contains the add primitive"
            check (compactWords.["data"].["constructs"].AsArray().Count > 0) $"{versionLabel} compact inventory includes syntax constructs"

            let search = dispatchHelpRequest "search-add"
            equal "search" (stringValue search.["kind"]) $"{versionLabel} focused search request kind"
            check (jsonArrayStrings search.["data"] |> List.contains "add") $"{versionLabel} focused search finds add"

            let context = dispatchHelpRequest "context-add"
            equal "context" (stringValue context.["kind"]) $"{versionLabel} context request kind"
            let contextData = context.["data"]
            let contextWords = contextData.["words"].AsArray()
            equal "add" (stringValue contextWords[0].["name"]) $"{versionLabel} context keeps its requested root first"
            check (contextWords.Count <= 6) $"{versionLabel} context respects its word limit"
            check (contextData["utf8Bytes"].GetValue<int>() <= 4096) $"{versionLabel} context respects its byte limit"

        let defineHelp = dispatch engine "help" [ "topic", jstr "define" ] |> expectOk "read Flow define help"
        let defineData = defineHelp.["data"]
        equal "define" (stringValue defineData.["topic"]) "define help selects its requested topic"
        let fieldNames =
            defineData.["allowedFlowDefineFields"].AsArray()
            |> Seq.map (fun field -> stringValue field.["name"])
            |> Seq.toList
        equal (AuthoringHelp.flowDefineFields |> List.map (fun field -> field.Name)) fieldNames "Flow define help and runtime allowlist share one contract"
        check (not (fieldNames |> List.contains "code")) "Flow define help does not advertise a code alias"
        check ((stringValue defineData.["documentation"]).Contains("doc", StringComparison.Ordinal)) "define help explains inline word documentation"
        check ((stringValue defineData.["documentation"]).Contains("effects", StringComparison.Ordinal)) "define help explains effect declarations"
        check ((stringValue defineData.["documentation"]).Contains("authored dependencies committed as library words", StringComparison.Ordinal)) "define help explains qualified library dependencies"
        equal [ "tutorial-sign" ]
            (defineData.["sourceExamples"].AsArray() |> Seq.map (fun item -> stringValue item.["name"]) |> Seq.toList)
            "default Flow/1 define help preserves its existing source-example inventory"
        check
            (defineData.["requestExamples"].AsArray()
             |> Seq.forall (fun item -> stringValue item.["name"] <> "define-tutorial-span-validator"))
            "default Flow/1 define help does not advertise Flow/2 record source"

        let flow2HelpEngine = Runtime.Engine(Path.Combine(root, "authoring-help-flow2"), Set.empty, "2041-02-03T04:05:06Z")
        let defineHelpV2 =
            dispatch flow2HelpEngine "help" [ "topic", jstr "define"; "syntaxVersion", jint 2 ]
            |> expectOk "read Flow/2 define help"
        let defineHelpDataV2 = defineHelpV2.["data"]
        equal 2 ((defineHelpDataV2.["syntaxVersion"]).GetValue<int>()) "help response records selected syntaxVersion"
        check ((stringValue (defineHelpDataV2.["documentation"])).Contains("Flow/2", StringComparison.Ordinal)) "Flow/2 help identifies its selected syntax"
        for guidance in [ "eval"; "`code`"; "define uses `source`"; "`word`"; "`type`"; "unchecked construction candidate"; "completed false return"; "caller-owned tests do not qualify the callee"; "generated constructors cannot own authored Flow tests"; "authored dependencies committed as library words" ] do
            check ((stringValue defineHelpDataV2.["documentation"]).Contains(guidance, StringComparison.Ordinal)) $"Flow/2 Define help explains {guidance}"
        equal [ "tutorial-sign"; "tutorial-span-validator"; "tutorial-list-fold"; "tutorial-enum-tests" ]
            (defineHelpDataV2.["sourceExamples"].AsArray() |> Seq.map (fun item -> stringValue item.["name"]) |> Seq.toList)
            "Flow/2 help retains prior source examples and adds the enum tests"
        let sourceExampleV2 = (defineHelpDataV2.["sourceExamples"]).AsArray() |> Seq.head |> fun item -> stringValue (item.["source"])
        check (sourceExampleV2.StartsWith("fn tutorial.sign", StringComparison.Ordinal)) "Flow/2 help returns an fn source example"
        check (not (sourceExampleV2.Contains("effects ", StringComparison.Ordinal))) "Flow/2 help preserves omitted effects metadata"
        let requestExampleV2 name =
            defineHelpDataV2.["requestExamples"].AsArray()
            |> Seq.find (fun item -> stringValue item.["name"] = name)
            |> fun item -> item.["request"]
        let formatRequestV2 =
            requestExampleV2 "format-tutorial-sign"
        equal 2 ((formatRequestV2.["syntaxVersion"]).GetValue<int>()) "Flow/2 format example selects syntax version 2"
        let formattedHelpSource = Protocol.dispatchLine flow2HelpEngine (formatRequestV2.ToJsonString()) |> expectOk "format the Flow/2 help example"
        let stagedBeforeDefine = dispatch flow2HelpEngine "words" [] |> expectOk "check help example before explicit define"
        check (not ((stagedBeforeDefine.["data"].["words"]).AsArray() |> Seq.exists (fun item -> stringValue (item.["name"]) = "tutorial.sign"))) "Flow/2 formatter does not stage the help example"
        let defineRequestV2 =
            (defineHelpDataV2.["requestExamples"]).AsArray()
            |> Seq.find (fun item -> stringValue item.["name"] = "define-tutorial-sign")
            |> fun item -> item.["request"]
        equal 2 ((defineRequestV2.["syntaxVersion"]).GetValue<int>()) "Flow/2 define example selects syntax version 2"
        let explicitDefineRequest = defineRequestV2.AsObject()
        let formattedHelpData = formattedHelpSource.["data"]
        explicitDefineRequest.["source"] <- jstr (stringValue (formattedHelpData.["source"]))
        Protocol.dispatchLine flow2HelpEngine (explicitDefineRequest.ToJsonString()) |> expectOk "stage the Flow/2 help example through explicit define" |> ignore
        assertAllPassed 3 (dispatch flow2HelpEngine "test" [ "word", jstr "tutorial.sign" ] |> expectOk "run Flow/2 help tests")

        let evalRequestV2 = requestExampleV2 "eval-tutorial-sign-v2"
        equal "eval" (stringValue evalRequestV2.["op"]) "compact Flow/2 example uses eval"
        equal "tutorial.sign(-2)" (stringValue evalRequestV2.["code"]) "compact Flow/2 eval example uses canonical dotted qualification"
        check (not ((evalRequestV2.AsObject()).ContainsKey("source"))) "compact eval example does not use define's source field"
        equal 2 ((evalRequestV2.["syntaxVersion"]).GetValue<int>()) "compact eval example selects Flow/2"
        let evaluatedHelpCode = Protocol.dispatchLine flow2HelpEngine (evalRequestV2.ToJsonString()) |> expectOk "execute the Flow/2 compact eval example"
        equal "-1" (stringValue evaluatedHelpCode.["data"].["stack"].[0]) "compact Flow/2 eval example returns the documented value"

        let tutorialSpanSourceExample =
            defineHelpDataV2.["sourceExamples"].AsArray()
            |> Seq.find (fun item -> stringValue item.["name"] = "tutorial-span-validator")
            |> fun item -> stringValue item.["source"]
        let tutorialSpanDefineRequest = requestExampleV2 "define-tutorial-span-validator"
        equal tutorialSpanSourceExample (stringValue tutorialSpanDefineRequest.["source"]) "record source example exactly matches its define request"
        equal 2 ((tutorialSpanDefineRequest.["syntaxVersion"]).GetValue<int>()) "record define request explicitly selects Flow/2"
        Protocol.dispatchLine flow2HelpEngine (tutorialSpanDefineRequest.ToJsonString())
        |> expectOk "execute the Flow/2 TutorialSpan source example through its returned define request"
        |> ignore

        let tutorialSpanTestRequest = requestExampleV2 "test-tutorial-span-validator"
        equal "tutorialSpan.valid?" (stringValue tutorialSpanTestRequest.["word"]) "record tests are owned by the predicate word"
        let tutorialSpanTests = Protocol.dispatchLine flow2HelpEngine (tutorialSpanTestRequest.ToJsonString()) |> expectOk "execute the returned TutorialSpan test request"
        assertAllPassed 3 tutorialSpanTests
        let tutorialSpanCoverage =
            dispatch flow2HelpEngine "describe" [ "word", jstr "tutorialSpan.valid?" ]
            |> expectOk "inspect predicate-owned finite return evidence"
            |> fun response -> response.["data"].["coverage"].["finiteCoverage"]
        check (tutorialSpanCoverage.["complete"].GetValue<bool>()) "predicate-owned tests cover both finite Bool returns"
        let tutorialSpanObservedReturns = jsonArrayStrings tutorialSpanCoverage.["returns"].[0].["observed"]
        check (tutorialSpanObservedReturns |> List.contains "true") "ordered/equal constructor tests observe the predicate's true return"
        check (tutorialSpanObservedReturns |> List.contains "false") "predicate-owned expected-error constructor test retains its completed false return"

        let foldSourceExampleV2 =
            defineHelpDataV2.["sourceExamples"].AsArray()
            |> Seq.find (fun item -> stringValue item.["name"] = "tutorial-list-fold")
            |> fun item -> stringValue item.["source"]
        check (foldSourceExampleV2.StartsWith("fn tutorial.fold-step", StringComparison.Ordinal)) "Flow/2 fold help uses fn declarations"
        check (foldSourceExampleV2.Contains("items.fold(0, tutorial.fold-step)", StringComparison.Ordinal)) "Flow/2 fold help uses a named receiver callback"
        check (foldSourceExampleV2.Contains("test tutorial.fold-sum/empty", StringComparison.Ordinal)) "Flow/2 fold source includes attached executable tests"
        check (foldSourceExampleV2.Contains("example tutorial.fold-sum/multiple", StringComparison.Ordinal)) "Flow/2 fold source includes an executable example"

        let foldDefineRequest = requestExampleV2 "define-tutorial-list-fold"
        equal foldSourceExampleV2 (stringValue foldDefineRequest.["source"]) "Flow/2 fold source exactly matches its define request"
        equal 2 (foldDefineRequest.["syntaxVersion"].GetValue<int>()) "Flow/2 fold define request selects syntax version 2"
        Protocol.dispatchLine flow2HelpEngine (foldDefineRequest.ToJsonString())
        |> expectOk "define the help-returned named callback fold source"
        |> ignore
        let foldStepTests =
            requestExampleV2 "test-tutorial-fold-step"
            |> fun request -> Protocol.dispatchLine flow2HelpEngine (request.ToJsonString())
            |> expectOk "run the help-returned fold callback test request"
        assertAllPassed 1 foldStepTests
        let foldSumTests =
            requestExampleV2 "test-tutorial-fold-sum"
            |> fun request -> Protocol.dispatchLine flow2HelpEngine (request.ToJsonString())
            |> expectOk "run the help-returned fold owner test request"
        assertAllPassed 2 foldSumTests

        let examplesHelpV2 =
            dispatch flow2HelpEngine "help" [ "topic", jstr "examples"; "syntaxVersion", jint 2 ]
            |> expectOk "read Flow/2 examples help"
            |> fun response -> response.["data"]
        check
            ((stringValue examplesHelpV2.["documentation"]).Contains("tutorial-list-fold", StringComparison.Ordinal))
            "Flow/2 examples help explains how to define its fold example"
        let foldSourceExampleInExamplesV2 =
            examplesHelpV2.["sourceExamples"].AsArray()
            |> Seq.find (fun item -> stringValue item.["name"] = "tutorial-list-fold")
            |> fun item -> stringValue item.["source"]
        equal foldSourceExampleV2 foldSourceExampleInExamplesV2 "Flow/2 examples help also exposes the executable named callback source"
        let foldCaseExampleV2 =
            examplesHelpV2.["sourceExamples"].AsArray()
            |> Seq.find (fun item -> stringValue item.["name"] = "tutorial-list-fold-example")
            |> fun item -> stringValue item.["source"]
        check (foldCaseExampleV2.Contains("tutorial.fold-sum", StringComparison.Ordinal)) "Flow/2 examples help includes the executable fold case"
        let exampleRequest =
            examplesHelpV2.["requestExamples"].AsArray()
            |> Seq.find (fun item -> stringValue item.["name"] = "run-tutorial-list-fold-example")
            |> fun item -> item.["request"]
        let exampleResult = Protocol.dispatchLine flow2HelpEngine (exampleRequest.ToJsonString()) |> expectOk "run the help-returned fold example request"
        equal "1/1 example(s) passed." (stringValue exampleResult.["text"]) "the help-returned fold example passes through the runtime"
        let evalRequest =
            examplesHelpV2.["requestExamples"].AsArray()
            |> Seq.find (fun item -> stringValue item.["name"] = "eval-tutorial-list-fold")
            |> fun item -> item.["request"]
        let evaluatedFold = Protocol.dispatchLine flow2HelpEngine (evalRequest.ToJsonString()) |> expectOk "evaluate the help-returned Flow/2 fold request"
        equal "6" (stringValue evaluatedFold.["data"].["stack"].[0]) "the help-returned Flow/2 fold evaluates to the sum"

        let enumSource =
            defineHelpDataV2.["sourceExamples"].AsArray()
            |> Seq.find (fun item -> stringValue item.["name"] = "tutorial-enum-tests")
            |> fun item -> stringValue item.["source"]
        let enumRequests = examplesHelpV2.["requestExamples"].AsArray()
        let enumRequest name =
            enumRequests |> Seq.find (fun item -> stringValue item.["name"] = name)
            |> fun item -> item.["request"]
        equal enumSource (stringValue ((enumRequest "define-tutorial-enum-tests").["source"])) "enum help source and define request agree"
        for name in [ "define-tutorial-enum-tests"; "test-tutorial-classify"; "commit-tutorial-classify-as-library" ] do
            Protocol.dispatchLine flow2HelpEngine ((enumRequest name).ToJsonString())
            |> expectOk $"execute enum help request {name}"
            |> ignore
        let classified = dispatch flow2HelpEngine "describe" [ "word", jstr "tutorial.classify" ] |> expectOk "inspect help-defined enum classifier"
        equal "library" (stringValue classified.["data"].["maturity"]) "enum help classifier qualifies as library"
        let enumTests = dispatch flow2HelpEngine "test" [ "word", jstr "tutorial.classify" ] |> expectOk "rerun committed enum help tests"
        equal "3/3 test(s) passed." (stringValue enumTests.["text"]) "enum constructor expectations pass after publication"
        let reloadEnum = Runtime.Engine(Path.Combine(root, "authoring-help-flow2"), Set.empty, "2041-02-03T04:05:06Z")
        dispatch reloadEnum "test" [ "word", jstr "tutorial.classify" ] |> expectOk "reload published enum help tests" |> ignore

        let tutorialSpanCommitRequest = requestExampleV2 "commit-tutorial-span-validator-as-library"
        equal "commit" (stringValue tutorialSpanCommitRequest.["op"]) "predicate library example uses commit"
        equal "tutorialSpan.valid?" (stringValue tutorialSpanCommitRequest.["word"]) "library example commits the predicate, not its caller"
        equal true (boolValue tutorialSpanCommitRequest.["library"]) "predicate library example explicitly requests library maturity"
        Protocol.dispatchLine flow2HelpEngine (tutorialSpanCommitRequest.ToJsonString())
        |> expectOk "execute the returned predicate library-commit request"
        |> ignore

        let tutorialSpanEffects =
            dispatch flow2HelpEngine "effects" [ "word", jstr "tutorialSpan.valid?" ]
            |> expectOk "inspect predicate effects"
            |> fun response -> jsonArrayStrings response.["data"]
        equal [] tutorialSpanEffects "TutorialSpan predicate has an empty effect set"

        let tutorialSpanReloaded = Runtime.Engine(Path.Combine(root, "authoring-help-flow2"), Set.empty, "2041-02-03T04:05:06Z")
        let tutorialSpanAfterReload =
            dispatch tutorialSpanReloaded "describe" [ "word", jstr "tutorialSpan.valid?" ]
            |> expectOk "inspect predicate after fresh reload"
        equal "library" (stringValue tutorialSpanAfterReload.["data"].["maturity"]) "predicate library maturity survives fresh reload"
        assertAllPassed 3 (dispatch tutorialSpanReloaded "test" [ "word", jstr "tutorialSpan.valid?" ] |> expectOk "run predicate-owned cases after fresh reload")
        let tutorialSpanTypeRequest = requestExampleV2 "source-tutorial-span-type"
        equal "{\"op\":\"source\",\"type\":\"TutorialSpan\"}" (tutorialSpanTypeRequest.ToJsonString()) "type-source help request has the exact source(type) shape"
        let tutorialSpanTypeSource =
            "record TutorialSpan {\n"
            + "    field start: Int\n"
            + "    field finish: Int\n"
            + "    validate tutorialSpan.valid?\n"
            + "}"
        equal tutorialSpanTypeSource
            (Protocol.dispatchLine tutorialSpanReloaded (tutorialSpanTypeRequest.ToJsonString())
             |> expectOk "execute the returned source(type) request after reload"
             |> fun response -> stringValue response.["data"])
            "source(type) returns the exact authored TutorialSpan declaration after reload"

        let tutorialSpanWordRequest = requestExampleV2 "source-tutorial-span-predicate"
        equal "{\"op\":\"source\",\"word\":\"tutorialSpan.valid?\"}" (tutorialSpanWordRequest.ToJsonString()) "word-source help request uses the word selector"
        let tutorialSpanPredicateSource =
            "fn tutorialSpan.valid?(value: TutorialSpan) -> Bool {\n"
            + "    doc \"A span is ordered when its finish is not before its start.\"\n"
            + "\n"
            + "    int.less-or-equal(value.start, value.finish)\n"
            + "}"
        equal tutorialSpanPredicateSource
            (Protocol.dispatchLine tutorialSpanReloaded (tutorialSpanWordRequest.ToJsonString())
             |> expectOk "execute the returned source(word) request after reload"
             |> fun response -> stringValue response.["data"])
            "source(word) returns the exact authored TutorialSpan predicate"

        for word in [ "tutorialSpan.valid?"; "tutorialSpan.new" ] do
            equal []
                (dispatch tutorialSpanReloaded "effects" [ "word", jstr word ]
                 |> expectOk $"inspect {word} effects after reload"
                 |> fun response -> jsonArrayStrings response.["data"])
                $"{word} preserves an empty effect closure after reload"
        dispatch tutorialSpanReloaded "eval" [ "frontend", jstr "flow"; "syntaxVersion", jint 2; "code", jstr "tutorialSpan.new(start = 3, finish = 1)" ]
        |> expectError "RECORD_VALIDATION_FAILED"
        |> ignore

        let sourceExample = defineData.["sourceExamples"].AsArray() |> Seq.head
        let canonicalSource = stringValue sourceExample.["source"]
        let defineRequestExample =
            defineData.["requestExamples"].AsArray()
            |> Seq.find (fun item -> stringValue item.["name"] = "define-tutorial-sign")
        let defineRequest = defineRequestExample.["request"]
        equal canonicalSource (stringValue defineRequest.["source"]) "define request example uses the canonical documented source"
        let defined = Protocol.dispatchLine engine (defineRequest.ToJsonString()) |> expectOk "execute the Flow source returned by help"
        equal "flow" (stringValue defined.["data"].["frontend"]) "help's source request uses the default Flow frontend"
        assertAllPassed 3 (dispatch engine "test" [ "word", jstr "tutorial.sign" ] |> expectOk "run all canonical tutorial tests")
        let examples = dispatch engine "example" [ "word", jstr "tutorial.sign" ] |> expectOk "run the canonical tutorial example"
        check (boolValue examples.["data"].["results"].[0].["passed"]) "canonical tutorial example passes"

        let examplesHelp = dispatch engine "help" [ "topic", jstr "examples" ] |> expectOk "read test and example help"
        let examplesDocumentation = stringValue examplesHelp.["data"].["documentation"]
        for guidance in [ "=> <literal>"; "=> value <expression>"; "=> error CODE"; "test-all"; "nominal"; "literal expectations only" ] do
            check (examplesDocumentation.Contains(guidance, StringComparison.Ordinal)) $"examples help explains {guidance}"
        check (not (examplesDocumentation.Contains("effect-count assertion", StringComparison.Ordinal))) "Flow/1 examples help does not advertise the Flow/2-only suffix"
        check
            (examplesHelp.["data"].["sourceExamples"].AsArray()
             |> Seq.forall (fun item -> stringValue item.["name"] <> "effect-count-test-v2"))
            "Flow/1 examples help has no Flow/2 effect-count source example"
        let valueExpectation =
            examplesHelp.["data"].["sourceExamples"].AsArray()
            |> Seq.find (fun item -> stringValue item.["name"] = "value-expression-test-expectation")
        let valueExpectationSource = stringValue valueExpectation.["source"]
        let runtimeErrorExpectation =
            examplesHelp.["data"].["sourceExamples"].AsArray()
            |> Seq.find (fun item -> stringValue item.["name"] = "runtime-error-test-expectation")
        let runtimeErrorExpectationSource = stringValue runtimeErrorExpectation.["source"]
        let attachHelpCase source =
            let request = JsonObject()
            request["op"] <- jstr "define"
            request["source"] <- jstr source
            Protocol.dispatchLine engine (request.ToJsonString())
            |> expectOk "execute a test source returned by help"
        attachHelpCase valueExpectationSource |> ignore
        attachHelpCase runtimeErrorExpectationSource |> ignore
        let canonicalTests = dispatch engine "test" [ "word", jstr "tutorial.sign" ] |> expectOk "run canonical and help-provided tests"
        assertAllPassed 5 canonicalTests
        let testResults = canonicalTests.["data"].["results"].AsArray()
        let valueResult = testResults |> Seq.find (fun item -> stringValue item.["word"] = "tutorial.sign" && stringValue item.["name"] = "value-expression")
        equal "value-expression" (stringValue valueResult.["expectedKind"]) "help value expectation is compiled and executed"
        equal "1" (stringValue valueResult.["actual"].[0]) "help value expectation runs its actual word body"
        let runtimeErrorResult = testResults |> Seq.find (fun item -> stringValue item.["word"] = "tutorial.sign" && stringValue item.["name"] = "divide-by-zero")
        equal "RUNTIME_DIVIDE_BY_ZERO" (stringValue runtimeErrorResult.["expectedErrorCode"]) "help runtime-error expectation executes the real division error"
        check (boolValue runtimeErrorResult.["passed"]) "help runtime-error expectation passes only when the runtime error matches"
        let examplesHelpV2 =
            dispatch flow2HelpEngine "help" [ "topic", jstr "examples"; "syntaxVersion", jint 2 ]
            |> expectOk "read Flow/2 effect-count testing help"
        let examplesDocumentationV2 = stringValue examplesHelpV2.["data"].["documentation"]
        for guidance in [ "Flow/2 tests"; "omitted categories are zero"; "fs.read"; "console.write"; "nested helpers" ] do
            check (examplesDocumentationV2.Contains(guidance, StringComparison.Ordinal)) $"Flow/2 examples help explains {guidance}"
        let effectCountHelp =
            examplesHelpV2.["data"].["sourceExamples"].AsArray()
            |> Seq.find (fun item -> stringValue item.["name"] = "effect-count-test-v2")
        check
            ((stringValue effectCountHelp.["source"]).Contains("effects {\n        fs.read: 2\n        fs.write: 0\n    }", StringComparison.Ordinal))
            "Flow/2 examples help includes valid count assertion syntax"
        let afterHelpCases = dispatch engine "describe" [ "word", jstr "tutorial.sign" ] |> expectOk "inspect help-provided test attachments"
        equal [ "divide-by-zero"; "negative"; "positive"; "value-expression"; "zero" ] (jsonArrayStrings afterHelpCases.["data"].["tests"]) "help examples attach to their existing tutorial word"

        let libraryCommitExample =
            defineData.["requestExamples"].AsArray()
            |> Seq.find (fun item -> stringValue item.["name"] = "commit-tutorial-sign-as-library")
        let committed = Protocol.dispatchLine engine (libraryCommitExample.["request"].ToJsonString()) |> expectOk "execute the help's alternative library commit request"
        check (not (isNull committed.["data"])) "library commit request example completes"
        let beforeReload = dispatch engine "describe" [ "word", jstr "tutorial.sign" ] |> expectOk "inspect committed authoring metadata"
        equal "library" (stringValue beforeReload.["data"].["maturity"]) "library commit records library maturity"
        equal "Returns -1 for negative integers and 1 for zero or positive integers." (stringValue beforeReload.["data"].["documentation"]) "inline Flow doc is available as describe metadata"
        equal [ "divide-by-zero"; "negative"; "positive"; "value-expression"; "zero" ] (jsonArrayStrings beforeReload.["data"].["tests"]) "inline and help-provided Flow tests remain inspectable"
        equal [ "negative" ] (jsonArrayStrings beforeReload.["data"].["examples"]) "inline Flow examples remain inspectable"

        let reloaded = Runtime.Engine(project, Set.empty, "2041-02-03T04:05:06Z")
        let reloadedBeforeTests = dispatch reloaded "describe" [ "word", jstr "tutorial.sign" ] |> expectOk "describe word after fresh Engine reload"
        equal "library" (stringValue reloadedBeforeTests.["data"].["maturity"]) "library maturity survives reload"
        equal "not-run" (stringValue reloadedBeforeTests.["data"].["coverage"].["status"]) "fresh reload has no current coverage observations"
        equal "Returns -1 for negative integers and 1 for zero or positive integers." (stringValue reloadedBeforeTests.["data"].["documentation"]) "documentation survives reload"
        equal [ "divide-by-zero"; "negative"; "positive"; "value-expression"; "zero" ] (jsonArrayStrings reloadedBeforeTests.["data"].["tests"]) "tests survive reload"
        equal [ "negative" ] (jsonArrayStrings reloadedBeforeTests.["data"].["examples"]) "examples survive reload"
        assertAllPassed 5 (dispatch reloaded "test-all" [] |> expectOk "run test-all after reload")
        let afterTests = dispatch reloaded "describe" [ "word", jstr "tutorial.sign" ] |> expectOk "inspect latest coverage after test-all"
        equal "current" (stringValue afterTests.["data"].["coverage"].["status"]) "describe shows the most recent test batch"
        equal
            (afterTests.["data"].["coverage"].["instructionsTotal"].GetValue<int>())
            (afterTests.["data"].["coverage"].["instructionsCovered"].GetValue<int>())
            "canonical tests cover each own instruction for library publication"

        let unknownTopic = dispatch engine "help" [ "topic", jstr "invented" ] |> expectError "HELP_UNKNOWN_TOPIC"
        equal [ "authoring"; "define"; "replacement"; "examples" ] (jsonArrayStrings unknownTopic.["error"].["expected"]) "unknown help topic reports known topics"
        equal [ "invented" ] (jsonArrayStrings unknownTopic.["error"].["actual"]) "unknown help topic reports requested name"
        let invalidType = dispatch engine "help" [ "topic", jbool true ] |> expectError "HELP_INVALID_ARGUMENT"
        equal [ "string" ] (jsonArrayStrings invalidType.["error"].["expected"]) "invalid help topic type expects a string"
        equal [ "boolean" ] (jsonArrayStrings invalidType.["error"].["actual"]) "invalid help topic type reports actual JSON kind"
        let invalidField = dispatch engine "help" [ "extra", jstr "ignored" ] |> expectError "HELP_INVALID_ARGUMENT"
        equal [ "syntaxVersion"; "topic" ] (jsonArrayStrings invalidField.["error"].["expected"]) "help documents its topic and syntax-version selectors"
        equal [ "extra" ] (jsonArrayStrings invalidField.["error"].["actual"]) "help invalid field error identifies unknown key"
        dispatch engine "help" [ "syntaxVersion", jstr "2" ] |> expectError "HELP_INVALID_ARGUMENT" |> ignore
        dispatch engine "help" [ "syntaxVersion", jint 3 ] |> expectError "HELP_SOURCE_VERSION_UNSUPPORTED" |> ignore
        let recoveredHelp = dispatch engine "help" [] |> expectOk "valid help follows invalid topic requests"
        equal [ "authoring"; "define"; "replacement"; "examples" ] (jsonArrayStrings recoveredHelp.["data"].["topics"]) "help recovers after invalid requests"

    let private testCandidateCasThenNormalCommit root =
        let engine = Runtime.Engine(Path.Combine(root, "authoring-help-candidate-cas"), Set.empty)
        let firstSource =
            "word draft.bump(value: Int) -> Int {\n"
            + "    effects none\n"
            + "    add(value, 1)\n"
            + "}"
        let firstTest = "test draft.bump/basic { draft::bump(1) => 2 }"
        defineFlow engine firstSource [ firstTest ] [] [] |> expectOk "stage the original candidate" |> ignore
        let originalDescription = dispatch engine "describe" [ "word", jstr "draft.bump" ] |> expectOk "inspect original candidate revision"
        equal 1 (originalDescription.["data"].["revision"].GetValue<int>()) "candidate begins at revision one"

        let replacementSource =
            "word draft.bump(value: Int) -> Int {\n"
            + "    effects none\n"
            + "    add(value, 2)\n"
            + "}"
        let replacementTest = "test draft.bump/basic { draft::bump(1) => 3 }"
        let stale =
            defineFlow engine replacementSource [ replacementTest ] []
                [ "replace", jbool true; "expectedRevision", jint 0 ]
        expectError "FLOW_BATCH_STALE_REVISION" stale |> ignore
        let afterStale = dispatch engine "describe" [ "word", jstr "draft.bump" ] |> expectOk "inspect candidate after stale CAS"
        equal 1 (afterStale.["data"].["revision"].GetValue<int>()) "stale CAS does not advance candidate revision"
        defineFlow engine replacementSource [ replacementTest ] []
            [ "replace", jbool true; "expectedRevision", jint 1 ]
        |> expectOk "stage the current-revision candidate replacement"
        |> ignore
        equal 2 ((dispatch engine "describe" [ "word", jstr "draft.bump" ] |> expectOk "inspect staged candidate revision").["data"].["revision"].GetValue<int>()) "candidate replacement advances its local revision"
        assertAllPassed 1 (dispatch engine "test" [ "word", jstr "draft.bump" ] |> expectOk "test staged candidate replacement")
        let notStaged = commit engine "replace-word" "draft.bump" [] |> expectError "REPLACE_NOT_STAGED"
        check ((stringValue notStaged.["error"].["message"]).Contains("expectedRevision", StringComparison.Ordinal)) "replace-word guidance points to the define CAS request"
        commit engine "commit" "draft.bump" [] |> expectOk "publish candidate replacement with ordinary commit" |> ignore
        let committed = dispatch engine "describe" [ "word", jstr "draft.bump" ] |> expectOk "inspect committed candidate replacement"
        equal "persistent" (stringValue committed.["data"].["status"]) "ordinary commit publishes a candidate replacement"
        equal 2 (committed.["data"].["revision"].GetValue<int>()) "commit preserves the staged replacement revision"

    let private testCommittedReplacementCallerGate root =
        let project = Path.Combine(root, "authoring-help-caller-gate")
        let engine = Runtime.Engine(project, Set.empty)
        let bumpSource =
            "word durable.bump(value: Int) -> Int {\n"
            + "    effects none\n"
            + "    add(value, 1)\n"
            + "}"
        let bumpTest = "test durable.bump/basic { durable::bump(1) => 2 }"
        defineFlow engine bumpSource [ bumpTest ] [] [] |> expectOk "define durable replacement owner" |> ignore
        commit engine "commit" "durable.bump" [] |> expectOk "commit durable replacement owner" |> ignore

        let callerSource =
            "word durable.forward(value: Int) -> Int {\n"
            + "    effects none\n"
            + "    durable::bump(value)\n"
            + "}"
        let callerTest = "test durable.forward/basic { durable::forward(5) => 6 }"
        defineFlow engine callerSource [ callerTest ] [] [] |> expectOk "define persistent caller" |> ignore
        commit engine "commit" "durable.forward" [] |> expectOk "commit persistent caller" |> ignore
        let ownerId = getWordId engine "durable.bump"
        let store = Storage.create project
        let before = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        expectError "REPLACE_NOT_STAGED" (commit engine "replace-word" "durable.bump" []) |> ignore

        let incompatibleSource =
            "word durable.bump(value: Int) -> Int {\n"
            + "    effects none\n"
            + "    if int::less-than(value, 5) { add(value, 1) } else { add(value, 2) }\n"
            + "}"
        defineFlow engine incompatibleSource [ bumpTest ] [] [ "replace", jbool true; "expectedRevision", jint 1 ]
        |> expectOk "stage a replacement that preserves owner tests but changes the persistent caller result"
        |> ignore
        let rejected = commit engine "replace-word" "durable.bump" [] |> expectError "COMMIT_TESTS_FAILED"
        let failedCases = jsonArrayStrings rejected.["error"].["actual"]
        check (failedCases |> List.contains "durable.forward/basic") "persistent replacement gate reports the failing caller case"
        let afterRejected = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        equal before.ManifestHash afterRejected.ManifestHash "failing caller gate leaves durable authority unchanged"
        let freshAfterRejected = Runtime.Engine(project, Set.empty)
        equal "6" (evalFlow freshAfterRejected "durable::bump(5)" |> expectOk "evaluate durable owner after caller gate rejection" |> fun response -> stringValue response.["data"].["stack"].[0]) "rejected persistent replacement leaves the committed body executable"
        equal ownerId (getWordId engine "durable.bump") "staged replacement preserves the owner's stable identity"
        dispatch engine "discard" [ "word", jstr "durable.bump" ] |> expectOk "discard rejected replacement candidate" |> ignore

        let compatibleReplacement =
            "word durable.bump(value: Int) -> Int {\n"
            + "    effects none\n"
            + "    doc \"Increment an integer by one.\"\n"
            + "    add(value, 1)\n"
            + "}"
        defineFlow engine compatibleReplacement [] [] [ "replace", jbool true; "expectedRevision", jint 1 ]
        |> expectOk "stage a compatible committed replacement"
        |> ignore
        let published = commit engine "replace-word" "durable.bump" [] |> expectOk "publish persistent replacement after caller regression tests pass"
        check (jsonArrayStrings published.["data"] |> List.contains "durable.forward/basic") "successful replacement result includes persistent caller tests"
        equal "Increment an integer by one." (stringValue (dispatch engine "describe" [ "word", jstr "durable.bump" ] |> expectOk "inspect published replacement documentation").["data"].["documentation"]) "compatible replacement publishes new inline documentation"

    let private testFlowUnknownArgumentsAndExpectationGuidance root =
        let project = Path.Combine(root, "authoring-help-unknown-fields")
        let engine = Runtime.Engine(project, Set.ofList [ "fs.read"; "fs.write" ])
        let canonicalSource =
            "word tutorial.sign(value: Int) -> Int {\n"
            + "    effects none\n"
            + "    doc \"Returns -1 for negative integers and 1 for zero or positive integers.\"\n"
            + "    if int::less-than(value, 0) { -1 } else { 1 }\n"
            + "}\n\n"
            + "test tutorial.sign/negative { tutorial::sign(-2) => -1 }\n"
            + "test tutorial.sign/zero { tutorial::sign(0) => 1 }\n"
            + "test tutorial.sign/positive { tutorial::sign(2) => 1 }\n\n"
            + "example tutorial.sign/negative { tutorial::sign(-2) => -1 }"
        defineFlowProject engine canonicalSource [] |> expectOk "stage tutorial word before unknown-field checks" |> ignore
        commit engine "commit" "tutorial.sign" [ "library", jbool true ] |> expectOk "commit tutorial word before unknown-field checks" |> ignore
        let baselineDescription = dispatch engine "describe" [ "word", jstr "tutorial.sign" ] |> expectOk "capture the current word before unknown Flow fields"
        let baselineSource = dispatch engine "source" [ "word", jstr "tutorial.sign" ] |> expectOk "capture authored source before unknown Flow fields"
        evalStack engine "\"authoring-help-sentinel\" \"stable\" file.write" |> expectOk "seed virtual file provider before unknown Flow fields" |> ignore
        let providerBefore = evalStack engine "\"authoring-help-sentinel\" file.read" |> expectOk "read virtual file provider sentinel before task"
        equal "\"stable\"" (stringValue providerBefore.["data"].["stack"].[0]) "virtual file provider sentinel is seeded"
        dispatch engine "task.begin" [ "goal", jstr "verify unknown Flow fields are rejected atomically" ] |> expectOk "begin unknown-field atomicity task" |> ignore

        let beforeWords = dispatch engine "words" [ "compact", jbool true ] |> expectOk "capture dictionary before unknown Flow fields"
        let beforeStorage = dispatch engine "storage.status" [] |> expectOk "capture provider status before unknown Flow fields"
        let beforeTask = dispatch engine "task.status" [] |> expectOk "capture task state before unknown Flow fields"
        let unknownTopLevel =
            dispatch engine "define"
                [ "frontend", jstr "flow"
                  "source", jstr "word malformed"
                  "code", jstr "not a Flow code alias"
                  "documentation", jstr "metadata is inline"
                  "extra", jbool true ]
            |> expectError "FLOW_RUNTIME_UNKNOWN_ARGUMENT"
        equal [ "examples"; "expectedRevision"; "frontend"; "removeAttachments"; "replace"; "source"; "syntaxVersion"; "temporary"; "tests" ]
            (jsonArrayStrings unknownTopLevel.["error"].["expected"]) "unknown Flow field error returns the allowed field set"
        equal [ "code"; "documentation"; "extra" ] (jsonArrayStrings unknownTopLevel.["error"].["actual"]) "unknown Flow fields are reported in sorted order"
        let unknownMessage = stringValue unknownTopLevel.["error"].["message"]
        check (unknownMessage.Contains("doc", StringComparison.Ordinal)) "unknown metadata field guidance points to inline doc source"
        check (unknownMessage.Contains("help topic `define`", StringComparison.Ordinal)) "unknown Flow field guidance points to define help"

        let testSource = "test tutorial.sign/negative { tutorial::sign(-2) => -1 }"
        let removal = JsonObject()
        removal["kind"] <- jstr "test"
        removal["caseName"] <- jstr "negative"
        removal["expectedSourceHash"] <- jstr (digest testSource)
        removal["extra"] <- jstr "ignored by the old permissive reader"
        let removals = JsonArray()
        removals.Add removal
        let replacementWordSource =
            "word tutorial.sign(value: Int) -> Int {\n"
            + "    effects none\n"
            + "    doc \"Returns -1 for negative integers and 1 for zero or positive integers.\"\n"
            + "    if int::less-than(value, 0) { -1 } else { 1 }\n"
            + "}"
        let unknownNested =
            dispatch engine "define"
                [ "frontend", jstr "flow"
                  "source", jstr replacementWordSource
                  "replace", jbool true
                  "expectedRevision", jint 1
                  "removeAttachments", removals ]
            |> expectError "FLOW_RUNTIME_UNKNOWN_ARGUMENT"
        equal [ "caseName"; "expectedSourceHash"; "kind" ] (jsonArrayStrings unknownNested.["error"].["expected"]) "unknown removal row error returns the nested allowed fields"
        equal [ "extra" ] (jsonArrayStrings unknownNested.["error"].["actual"]) "unknown removal row error identifies the extra key"
        equal (beforeWords.["data"].ToJsonString()) (dispatch engine "words" [ "compact", jbool true ] |> expectOk "read dictionary after unknown Flow fields" |> fun response -> response.["data"].ToJsonString()) "unknown Flow fields do not stage a definition"
        equal (beforeStorage.["data"].ToJsonString()) (dispatch engine "storage.status" [] |> expectOk "read durable status after unknown Flow fields" |> fun response -> response.["data"].ToJsonString()) "unknown Flow fields do not change durable project authority"
        let afterTask = dispatch engine "task.status" [] |> expectOk "inspect task after unknown Flow field diagnostics"
        for property in [ "active"; "wordsInspected"; "wordsUsed"; "wordsCreated"; "testsRun"; "testsFailed"; "effects" ] do
            equal (beforeTask.["data"].[property].ToJsonString()) (afterTask.["data"].[property].ToJsonString()) $"unknown Flow fields leave task {property} unchanged"
        equal [ "FLOW_RUNTIME_UNKNOWN_ARGUMENT"; "FLOW_RUNTIME_UNKNOWN_ARGUMENT" ]
            (jsonArrayStrings afterTask.["data"].["errors"]) "only logged diagnostics change task state"
        let afterDescription = dispatch engine "describe" [ "word", jstr "tutorial.sign" ] |> expectOk "inspect owner after rejected attachment row"
        equal (baselineDescription.["data"].["id"].ToJsonString()) (afterDescription.["data"].["id"].ToJsonString()) "unknown Flow fields leave the existing word identity unchanged"
        equal (baselineDescription.["data"].["revision"].ToJsonString()) (afterDescription.["data"].["revision"].ToJsonString()) "unknown Flow fields leave the existing word revision unchanged"
        equal (baselineDescription.["data"].["documentation"].ToJsonString()) (afterDescription.["data"].["documentation"].ToJsonString()) "unknown Flow fields leave the existing word metadata unchanged"
        let afterSource = dispatch engine "source" [ "word", jstr "tutorial.sign" ] |> expectOk "read owner source after rejected attachment row"
        equal (baselineSource.["data"].ToJsonString()) (afterSource.["data"].ToJsonString()) "unknown Flow fields leave existing authored source unchanged"
        equal 1 (afterDescription.["data"].["revision"].GetValue<int>()) "unknown nested removal does not stage an owner revision"
        equal [ "negative"; "positive"; "zero" ] (jsonArrayStrings afterDescription.["data"].["tests"]) "unknown nested removal does not remove attached tests"
        let providerAfter = evalStack engine "\"authoring-help-sentinel\" file.read" |> expectOk "read virtual file provider sentinel after unknown Flow fields"
        equal "\"stable\"" (stringValue providerAfter.["data"].["stack"].[0]) "unknown Flow fields leave virtual file provider state unchanged"

        let invalidExpectationEngine = Runtime.Engine("", Set.empty)
        let invalidExpectationSource =
            "word tutorial.echo(value: Int) -> Int {\n    effects none\n    value\n}\n"
            + "test tutorial.echo/nominal { tutorial::echo(1) => Money::new(1) }"
        let invalidExpectation = defineFlow invalidExpectationEngine invalidExpectationSource [] [] [] |> expectError "FLOW_EXPECTATION_LITERAL_REQUIRED"
        let guidance = stringValue invalidExpectation.["error"].["message"]
        check (guidance.Contains("=> value <expression>", StringComparison.Ordinal)) "bare constructor expectation points to explicit value-expression syntax"
        check (guidance.Contains("=> error CODE", StringComparison.Ordinal)) "test expectation diagnostic lists runtime-error syntax"
        let invalidExample =
            dispatch invalidExpectationEngine "define"
                [ "source", jstr "word tutorial.echo(value: Int) -> Int {\n    effects none\n    value\n}\nexample tutorial.echo/nominal { tutorial::echo(1) => value Money::new(1) }" ]
            |> expectError "FLOW_EXAMPLE_EXPECTATION_KIND"
        check ((stringValue invalidExample.["error"].["message"]).Contains("literal expectations only", StringComparison.Ordinal)) "example diagnostics keep examples literal-only"

    let private testExplicitFrontendAndDurableReload root =
        let project = Path.Combine(root, "durable-reload")
        let engine = Runtime.Engine(project, Set.empty, "2030-01-02T03:04:05Z")
        let defined = defineFlow engine flowWordSource [ flowTestSource ] [ flowExampleSource ] []
        expectOk "define an explicit Flow word with authored attachments" defined |> ignore
        equal "flow" (stringValue (defined.["data"].["frontend"])) "definition reports selected frontend"
        equal [ "basic" ] (jsonArrayStrings defined.["data"].["tests"]) "definition reports test case names"
        equal [ "one" ] (jsonArrayStrings defined.["data"].["examples"]) "definition reports example case names"

        let wordId = getWordId engine "durable.increment"
        let committed = commit engine "commit" "durable.increment" [] |> expectOk "commit Flow candidate"
        check (not (isNull committed.["data"])) "Flow commit returns its ordinary commit result"

        let stackWrapperSource =
            "word durable.stack_wrapper : Int -> Int\n"
            + "    effects none\n"
            + "    durable.increment\n"
            + "end\n"
            + "\n"
            + "test durable.stack_wrapper/basic\n"
            + "    9 durable.stack_wrapper\n"
            + "    expect 10\n"
            + "end\n"
            + "\n"
            + "example durable.stack_wrapper/basic\n"
            + "    4 durable.stack_wrapper\n"
            + "    => 5\n"
            + "end\n"
        defineStack engine stackWrapperSource []
        |> expectOk "define a Stack wrapper and cases after committing Flow"
        |> ignore
        let stackWrapperId = getWordId engine "durable.stack_wrapper"
        commit engine "commit" "durable.stack_wrapper" []
        |> expectOk "commit a Stack caller of the Flow owner"
        |> ignore
        assertAllPassed 1 (dispatch engine "test" [ "word", jstr "durable.stack_wrapper" ] |> expectOk "run Stack case calling Flow")
        let stackExampleResult = dispatch engine "example" [ "word", jstr "durable.stack_wrapper"; "caseName", jstr "basic" ] |> expectOk "run Stack example calling Flow"
        check (boolValue (stackExampleResult.["data"].["results"].[0].["passed"])) "Stack example calling Flow passes before reload"

        let store = Storage.create project
        let loaded = Storage.load store
        let manifest =
            match loaded with
            | Ok snapshot -> snapshot.Manifest |> Option.defaultWith (fun () -> failwith "committed Flow manifest is missing")
            | Error problem -> failwith $"load Flow manifest: {problem.Code}: {problem.Message}"
        equal 2 manifest.FormatVersion "Flow publication writes manifest v2"
        let revision = manifest.Revisions |> List.find (fun item -> item.WordId = wordId)
        equal { Frontend = SourceFrontend.Flow; Version = 1 } revision.SourceFormat "revision records Flow/1"
        equal 1 revision.Revision "initial Flow revision is revision one"
        equal (digest flowWordSource) revision.Definition.Hash "definition SourceRef hashes the exact authored UTF-8 bytes"
        equal [ digest flowTestSource ] (revision.Tests |> List.map _.Hash) "test SourceRef hashes its exact authored UTF-8 bytes"
        equal [ digest flowExampleSource ] (revision.Examples |> List.map _.Hash) "example SourceRef hashes its exact authored UTF-8 bytes"
        equal 1 (revision.CallBindings |> List.filter (fun binding -> binding.BodyRole = StoredCallBodyRole.Definition) |> List.length) "word body call bindings use the Definition role"
        equal 2 (revision.CallBindings |> List.filter (fun binding -> binding.BodyRole = StoredCallBodyRole.Actual) |> List.length) "actual test/example bodies use the Actual role"
        equal 1 (revision.CallBindings |> List.filter (fun binding -> binding.BodyRole = StoredCallBodyRole.ExpectedExpression) |> List.length) "pure expectation calls use the ExpectedExpression role"
        equal flowWordSource (Storage.readSource store revision.Definition |> Result.defaultWith (fun problem -> failwith problem.Message)) "authored definition object survives exactly"
        equal [ flowTestSource ] (revision.Tests |> List.map (fun reference -> Storage.readSource store reference |> Result.defaultWith (fun problem -> failwith problem.Message))) "authored test object survives exactly"
        equal [ flowExampleSource ] (revision.Examples |> List.map (fun reference -> Storage.readSource store reference |> Result.defaultWith (fun problem -> failwith problem.Message))) "authored example object survives exactly"
        equal 4 revision.CallBindings.Length "definition, actual, expected and example call sites are all persisted"
        check (revision.CallBindings |> List.exists (fun binding -> binding.BodyRole = StoredCallBodyRole.ExpectedExpression)) "expected-expression call binding is persisted"
        let stackWrapperRevision = manifest.Revisions |> List.find (fun item -> item.WordId = stackWrapperId)
        equal { Frontend = SourceFrontend.Stack; Version = 1 } stackWrapperRevision.SourceFormat "Stack caller remains Stack/1 beside Flow"
        check (File.ReadAllText(Path.Combine(project, "dictionary.agent")).Contains("// frontend: flow/1", StringComparison.Ordinal)) "mixed-language export marks the Flow source block"
        check (File.ReadAllText(Path.Combine(project, "dictionary.agent")).Contains(flowWordSource, StringComparison.Ordinal)) "export contains authored Flow instead of lowered RPN"

        let reloaded = Runtime.Engine(project, Set.empty, "2030-01-02T03:04:05Z")
        equal wordId (getWordId reloaded "durable.increment") "fresh Engine reload preserves WordId"
        let source = dispatch reloaded "source" [ "word", jstr "durable.increment" ] |> expectOk "source after reload"
        equal flowWordSource (stringValue (source.["data"])) "source query exposes exact authored Flow definition text"
        let described = dispatch reloaded "describe" [ "word", jstr "durable.increment" ] |> expectOk "describe after reload"
        equal "value" (stringValue (described.["data"].["parameters"].[0].["name"])) "describe exposes authored parameter names"
        equal "Int" (stringValue (described.["data"].["parameters"].[0].["type"])) "describe exposes authored parameter type"
        equal [ "basic" ] (jsonArrayStrings (dispatch reloaded "tests" [ "word", jstr "durable.increment" ] |> expectOk "tests after reload" |> fun result -> result.["data"])) "tests remain attached after reload"
        equal [ "one" ] (jsonArrayStrings (dispatch reloaded "examples" [ "word", jstr "durable.increment" ] |> expectOk "examples after reload" |> fun result -> result.["data"])) "examples remain attached after reload"
        assertAllPassed 1 (dispatch reloaded "test" [ "word", jstr "durable.increment" ] |> expectOk "run reloaded Flow test")
        let evaluated = evalFlow reloaded "durable::increment(9)" |> expectOk "evaluate a reloaded Flow word"
        equal "10" (stringValue (evaluated.["data"].["stack"].[0])) "Flow eval invokes the verified durable body"
        let exampleResult = dispatch reloaded "example" [ "word", jstr "durable.increment"; "caseName", jstr "one" ] |> expectOk "run a reloaded Flow example"
        check (boolValue (exampleResult.["data"].["results"].[0].["passed"])) "reloaded example matches its expectation"
        equal flowExampleSource (stringValue (exampleResult.["data"].["results"].[0].["source"])) "example response preserves exact authored case source"
        let cliResponse = cliEval project "durable::increment(9)" |> expectOk "fresh-process Flow CLI evaluation"
        equal "10" (stringValue (cliResponse.["data"].["stack"].[0])) "fresh-process CLI loads the authored Flow program"
        assertAllPassed 1 (dispatch reloaded "test" [ "word", jstr "durable.stack_wrapper" ] |> expectOk "run reloaded Stack case calling Flow")
        let reloadedStackExample = dispatch reloaded "example" [ "word", jstr "durable.stack_wrapper"; "caseName", jstr "basic" ] |> expectOk "run reloaded Stack example calling Flow"
        check (boolValue (reloadedStackExample.["data"].["results"].[0].["passed"])) "reloaded Stack example calling Flow passes"
        let stackWrapperValue = evalStack reloaded "9 durable.stack_wrapper" |> expectOk "evaluate reloaded Stack caller"
        equal "10" (stringValue (stackWrapperValue.["data"].["stack"].[0])) "fresh Engine reload executes the Stack caller against Flow"

    let private testExplicitFrontendCannotFallBack root =
        let engine = Runtime.Engine(Path.Combine(root, "frontend-selector"), Set.empty)
        let flow = dispatch engine "eval" [ "code", jstr "add(10, 20)" ] |> expectOk "omitted frontend selects Flow evaluation"
        equal "30" (stringValue (flow.["data"].["stack"].[0])) "Flow is the default expression frontend"
        let stack = evalStack engine "10 20 add" |> expectOk "explicit Stack frontend keeps RPN evaluation"
        equal "30" (stringValue (stack.["data"].["stack"].[0])) "default Stack expression remains supported"
        let defaultDefinition =
            "word default.increment(value: Int) -> Int {\n"
            + "    effects none\n"
            + "    add(value, 1)\n"
            + "}\n\n"
            + "test default.increment/basic {\n"
            + "    default::increment(9)\n"
            + "    => 10\n"
            + "}"
        let defaultDefined = dispatch engine "define" [ "source", jstr defaultDefinition ] |> expectOk "omitted frontend selects Flow definition"
        equal "flow" (stringValue (defaultDefined.["data"].["frontend"])) "default definition reports Flow"
        equal "10" (stringValue (dispatch engine "eval" [ "code", jstr "default::increment(9)" ] |> expectOk "evaluate omitted-frontend Flow word" |> fun response -> response.["data"].["stack"].[0])) "omitted-frontend Flow word is executable"
        let freshFlowEngine = Runtime.Engine(Path.Combine(root, "flow-expression-without-words"), Set.empty)
        let primitiveOnly = evalFlow freshFlowEngine "add(1, 2)" |> expectOk "evaluate a Flow primitive with no user Flow words"
        equal "3" (stringValue (primitiveOnly.["data"].["stack"].[0])) "explicit Flow expression resolves primitives in a fresh empty Engine"
        let unknown = dispatch engine "eval" [ "frontend", jstr "flow2"; "code", jstr "10 20 add" ]
        expectError "RUNTIME_FRONTEND_UNSUPPORTED" unknown |> ignore
        let invalidFrontendValues: JsonNode list = [ null; jbool true; JsonObject() :> JsonNode ]
        for value in invalidFrontendValues do
            dispatch engine "eval" [ "frontend", value; "code", jstr "10 20 add" ]
            |> expectError "RUNTIME_FRONTEND_INVALID"
            |> ignore
        let malformedEval = evalFlow engine "word broken"
        check (not (succeeded malformedEval)) "malformed explicit Flow eval is rejected"
        check ((errorCode malformedEval).StartsWith("FLOW_", StringComparison.Ordinal)) "malformed explicit Flow eval keeps its Flow diagnostic"
        let malformedDefaultEval = dispatch engine "eval" [ "code", jstr "word broken" ]
        check (not (succeeded malformedDefaultEval)) "malformed omitted-frontend eval is rejected"
        check ((errorCode malformedDefaultEval).StartsWith("FLOW_", StringComparison.Ordinal)) "malformed omitted-frontend eval keeps its Flow diagnostic"
        let malformedDefine = defineFlow engine "word malformed" [] [] []
        check (not (succeeded malformedDefine)) "malformed explicit Flow definition is rejected"
        check ((errorCode malformedDefine).StartsWith("FLOW_", StringComparison.Ordinal)) "malformed Flow definition is not passed to the Stack parser"
        let malformedDefaultDefine = dispatch engine "define" [ "source", jstr "word malformed" ]
        check (not (succeeded malformedDefaultDefine)) "malformed omitted-frontend definition is rejected"
        check ((errorCode malformedDefaultDefine).StartsWith("FLOW_", StringComparison.Ordinal)) "malformed omitted-frontend definition is not passed to the Stack parser"

    let private testFlow2FormatDefinePersistReloadAndRewrite root =
        let project = Path.Combine(root, "flow2-persistence")
        let engine = Runtime.Engine(project, Set.empty, "2042-03-04T05:06:07Z")
        let source =
            "record Customer { field email: String; }\n\n"
            + "fn customer.has-email(value: Customer) -> Bool {\n"
            + "    value.email == \"a@example.com\"\n"
            + "}\n\n"
            + "test customer.has-email/matching {\n"
            + "    customer::has-email(customer::new(email = \"a@example.com\"))\n"
            + "    => true\n"
            + "}"
        let canonicalDocument =
            FlowParser.parseDocumentWithVersion 2 "<flow2-runtime-test>" source
            |> Result.defaultWith (fun diagnostic -> failwith (Diagnostics.render diagnostic))
        let expectedCanonical = FlowSource.renderDocument canonicalDocument
        let canonicalWordSource =
            FlowParser.parseDocumentWithVersion 2 "<flow2-canonical-word>" expectedCanonical
            |> Result.defaultWith (fun diagnostic -> failwith (Diagnostics.render diagnostic))
            |> fun document -> document.Words |> List.exactlyOne |> fun definition -> definition.SourceText
        let canonicalTestSource =
            FlowParser.parseDocumentWithVersion 2 "<flow2-canonical-test>" expectedCanonical
            |> Result.defaultWith (fun diagnostic -> failwith (Diagnostics.render diagnostic))
            |> fun document -> document.Tests |> List.exactlyOne |> fun definition -> definition.SourceText
        let beforeGeneration = Storage.load (Storage.create project) |> Result.defaultWith (fun problem -> failwith problem.Message)
        let beforeWords = dispatch engine "words" [] |> expectOk "capture vocabulary before formatting" |> fun response -> response["data"].ToJsonString()
        let formatRequest = JsonObject()
        formatRequest["op"] <- jstr "format"
        let formatArgs = JsonObject()
        formatArgs["source"] <- jstr source
        formatArgs["frontend"] <- jstr "flow"
        formatRequest["args"] <- formatArgs
        formatRequest["syntaxVersion"] <- jint 2
        let formatted = Protocol.dispatchLine engine (formatRequest.ToJsonString()) |> expectOk "format Flow/2 through the JSON-lines protocol"
        let formattedData = formatted["data"]
        equal 2 ((formattedData["syntaxVersion"]).GetValue<int>()) "format response reports its selected syntax version"
        equal expectedCanonical (stringValue (formattedData["source"])) "format response contains the canonical Flow/2 source"
        let afterFormat = Storage.load (Storage.create project) |> Result.defaultWith (fun problem -> failwith problem.Message)
        equal beforeGeneration.Generation afterFormat.Generation "format does not advance persisted storage generation"
        equal beforeGeneration.ManifestHash afterFormat.ManifestHash "format does not change persisted manifest authority"
        equal beforeWords (dispatch engine "words" [] |> expectOk "check vocabulary after formatting" |> fun response -> response["data"].ToJsonString()) "format does not stage words or types"

        dispatch engine "format" [ "frontend", jstr "stack"; "syntaxVersion", jint 2; "source", jstr source ]
        |> expectError "RUNTIME_SOURCE_VERSION_UNSUPPORTED"
        |> ignore
        dispatch engine "format" [ "syntaxVersion", jstr "2"; "source", jstr source ]
        |> expectError "RUNTIME_SOURCE_VERSION_INVALID"
        |> ignore
        dispatch engine "format" [ "frontend", jstr "stack"; "source", jstr source ]
        |> expectError "SOURCE_FORMAT_UNSUPPORTED"
        |> ignore

        defineFlowProject engine expectedCanonical [ "syntaxVersion", jint 2 ]
        |> expectOk "explicitly define the returned Flow/2 source"
        |> ignore
        equal 1 (dispatch engine "test" [ "word", jstr "customer.has-email" ] |> expectOk "run the staged Flow/2 test" |> fun response -> (response["data"]["results"]).AsArray().Count) "Flow/2 test runs before commit"
        dispatch engine "eval" [ "frontend", jstr "flow"; "syntaxVersion", jint 2; "code", jstr "\"a@example.com\" == \"a@example.com\"" ]
        |> expectOk "evaluate a versioned Flow/2 equality expression"
        |> fun response -> equal "true" (stringValue ((response["data"]["stack"]).[0])) "versioned expression lowering preserves equality"

        commit engine "commit" "Customer" [] |> expectOk "commit the Flow/2 record source" |> ignore
        commit engine "commit" "customer.has-email" [] |> expectOk "commit the Flow/2 function and attached test" |> ignore
        assertAllPassed 1 (dispatch engine "test" [ "word", jstr "customer.has-email" ] |> expectOk "run the committed Flow/2 case")
        let store = Storage.create project
        let committed = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        let manifest = committed.Manifest |> Option.defaultWith (fun () -> failwith "Flow/2 project did not publish a manifest")
        let recordMetadata = manifest.Types |> List.find (fun item -> item.Name = "Customer")
        equal { Frontend = SourceFrontend.Flow; Version = 2 } recordMetadata.SourceFormat "Flow/2 record source version persists"
        let head = manifest.Words |> List.find (fun item -> item.CurrentName = "customer.has-email")
        let revision = manifest.Revisions |> List.find (fun item -> item.WordId = head.WordId && item.Revision = head.CurrentRevision)
        equal { Frontend = SourceFrontend.Flow; Version = 2 } revision.SourceFormat "Flow/2 function source version persists"
        equal (digest canonicalWordSource) revision.Definition.Hash "word source hash uses exact canonical source bytes"
        let propertyBinding = revision.CallBindings |> List.find (fun binding -> binding.Form = StoredCallForm.PropertyAccess "email")
        equal (FlowAstPath.FlowAstPath [ FlowAstPathSegment.BlockStatement 0; FlowAstPathSegment.EvaluateExpression; FlowAstPathSegment.EqualityLeft ])
            propertyBinding.Path
            "property accessor binding path identifies the property expression within equality-left"
        check (File.ReadAllText(Path.Combine(project, "dictionary.agent")).Contains("// frontend: flow/2", StringComparison.Ordinal)) "aggregate export identifies Flow/2 source blocks"
        let retainedDefinition = Storage.readSource store revision.Definition |> Result.defaultWith (fun problem -> failwith problem.Message)
        check (retainedDefinition.StartsWith("fn customer.has-email", StringComparison.Ordinal)) "durable definition keeps the Flow/2 fn declaration"
        check (not (retainedDefinition.Contains("effects ", StringComparison.Ordinal))) "durable definition preserves omitted effects metadata"
        equal [ canonicalTestSource ]
            (revision.Tests |> List.map (Storage.readSource store >> Result.defaultWith (fun problem -> failwith problem.Message)))
            "Flow/2 test source bytes persist exactly"

        let v1Replacement =
            "word customer.has-email(value: Customer) -> Bool {\n"
            + "    effects none\n"
            + "    true\n"
            + "}"
        dispatch engine "define"
            [ "frontend", jstr "flow"
              "source", jstr v1Replacement
              "replace", jbool true
              "expectedRevision", jint revision.Revision ]
        |> expectError "FLOW_SOURCE_VERSION_CHANGE_REQUIRES_SELECTION"
        |> ignore
        equal committed.ManifestHash (Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)).ManifestHash "implicit Flow/2 downgrade refusal leaves persisted authority unchanged"

        let renamed = dispatch engine "rename" [ "word", jstr "customer.has-email"; "to", jstr "customer.matches-email" ] |> expectOk "rename a persisted Flow/2 function and rewrite its case"
        let renamedData = renamed["data"]
        equal "customer.matches-email" (stringValue (renamedData["to"])) "rename publishes the requested Flow/2 owner"
        let renamedSnapshot = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        let renamedManifest = renamedSnapshot.Manifest |> Option.defaultWith (fun () -> failwith "Flow/2 rename did not retain a manifest")
        let renamedHead = renamedManifest.Words |> List.find (fun item -> item.CurrentName = "customer.matches-email")
        let renamedRevision = renamedManifest.Revisions |> List.find (fun item -> item.WordId = renamedHead.WordId && item.Revision = renamedHead.CurrentRevision)
        equal { Frontend = SourceFrontend.Flow; Version = 2 } renamedRevision.SourceFormat "rename retains Flow/2 source metadata"
        let renamedText = Storage.readSource store renamedRevision.Definition |> Result.defaultWith (fun problem -> failwith problem.Message)
        check (renamedText.StartsWith("fn customer.matches-email", StringComparison.Ordinal)) "rename rewrites the Flow/2 fn owner and preserves source syntax"
        check (not (renamedText.Contains("effects ", StringComparison.Ordinal))) "rename preserves omitted Flow/2 effect declaration"
        let reloaded = Runtime.Engine(project, Set.empty, "2042-03-04T05:06:07Z")
        assertAllPassed 1 (dispatch reloaded "test" [ "word", jstr "customer.matches-email" ] |> expectOk "run the rewritten Flow/2 case after fresh reload")
        let reloadedValue = evalFlow reloaded "customer::matches-email(customer::new(email = \"a@example.com\"))" |> expectOk "evaluate the rewritten Flow/2 word after fresh reload"
        let reloadedStack = (reloadedValue["data"]["stack"]).AsArray()
        equal "true" (stringValue reloadedStack.[0]) "fresh reload compiles and executes Flow/2 property access"

    let private testFlow2EnumsPersistReloadAndBindings root =
        let fixturePath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "examples", "closed-renewal-state.agent"))
        let source = File.ReadAllText fixturePath
        let parsed =
            FlowParser.parseDocumentWithVersion 2 fixturePath source
            |> Result.defaultWith (fun diagnostic -> failwith (Diagnostics.render diagnostic))
        let canonical = FlowSource.renderDocument parsed
        equal (source.TrimEnd([| '\r'; '\n' |])) canonical "new enum fixture is already in canonical Flow/2 format"
        let enumDefinition = parsed.Enums |> List.exactlyOne
        equal [ "pending"; "renewed"; "cancelled" ] enumDefinition.Cases "fixture keeps a frozen declaration-order case table"

        let project = Path.Combine(root, "flow2-enum-durable")
        let engine = Runtime.Engine(project, Set.empty, "2042-03-04T05:06:07Z")
        let staged = defineFlowProject engine canonical [ "syntaxVersion", jint 2 ] |> expectOk "stage the payload-free Flow/2 enum project"
        equal [ "RenewalState" ] (jsonArrayStrings staged.["data"].["types"]) "project introspection exposes the enum type"
        assertAllPassed 4 (dispatch engine "test-all" [] |> expectOk "run enum project cases before commit")

        commit engine "commit" "RenewalState" [] |> expectOk "commit the closed enum type" |> ignore
        commit engine "commit" "renewal.echo" [] |> expectOk "commit the enum identity helper" |> ignore
        commit engine "commit" "renewal.describe" [] |> expectOk "commit the exhaustive enum matcher" |> ignore

        let store = Storage.create project
        let persisted = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        let manifest = persisted.Manifest |> Option.defaultWith (fun () -> failwith "enum project did not publish a manifest")
        equal 3 manifest.FormatVersion "enum project uses the existing manifest schema v3"
        let typeMetadata = manifest.Types |> List.find (fun item -> item.Name = "RenewalState")
        equal { Frontend = SourceFrontend.Flow; Version = 2 } typeMetadata.SourceFormat "enum type source persists as Flow/2"
        equal (FlowSource.renderEnum enumDefinition)
            (Storage.readSource store typeMetadata.Definition |> Result.defaultWith (fun problem -> failwith problem.Message))
            "enum declaration bytes survive durable commit"

        let originalHead = manifest.Words |> List.find (fun item -> item.CurrentName = "renewal.describe")
        let originalRevision = manifest.Revisions |> List.find (fun item -> item.WordId = originalHead.WordId && item.Revision = originalHead.CurrentRevision)
        equal { Frontend = SourceFrontend.Flow; Version = 2 } originalRevision.SourceFormat "enum match word source persists as Flow/2"
        let definitionBindings = originalRevision.CallBindings |> List.filter (fun binding -> binding.BodyRole = StoredCallBodyRole.Definition)
        let scrutineeCall = definitionBindings |> List.find (fun binding -> binding.RequestedName = "renewal.echo" && (match binding.Path with FlowAstPath.FlowAstPath path -> List.contains FlowAstPathSegment.EnumScrutinee path))
        check (match scrutineeCall.Path with FlowAstPath.FlowAstPath path -> List.contains FlowAstPathSegment.EnumScrutinee path) "call in the enum scrutinee keeps its structural binding path"
        let armHelperCall = definitionBindings |> List.find (fun binding -> binding.RequestedName = "renewal.echo" && (match binding.Path with FlowAstPath.FlowAstPath path -> List.exists (function | FlowAstPathSegment.EnumCaseStatement _ -> true | _ -> false) path))
        check (match armHelperCall.Path with FlowAstPath.FlowAstPath path -> List.contains (FlowAstPathSegment.EnumCaseStatement(0, 0)) path) "ordinary call inside an enum arm keeps its authored case index"
        let armConstructor = definitionBindings |> List.find (fun binding -> binding.RequestedName = "RenewalState.pending" && (match binding.Path with FlowAstPath.FlowAstPath path -> List.exists (function | FlowAstPathSegment.EnumCaseStatement _ -> true | _ -> false) path))
        check (armConstructor.Target |> function | StoredCallTarget.GeneratedWord _ -> true | _ -> false) "enum constructor binding retains its generated target identity"
        check (match armConstructor.Path with FlowAstPath.FlowAstPath path -> List.contains (FlowAstPathSegment.EnumCaseStatement(0, 0)) path && List.contains (FlowAstPathSegment.CallArgument 0) path) "nested constructor argument keeps both enum-arm and argument path segments"

        let evalEnum target code structured =
            dispatch target "eval"
                [ "frontend", jstr "flow"
                  "syntaxVersion", jint 2
                  "structured", jbool structured
                  "code", jstr code ]
        let enumValue = evalEnum engine "RenewalState::pending()" true |> expectOk "evaluate a generated enum constructor with structured values"
        let structuredValue = enumValue.["data"].["structuredStack"].["values"] |> fun node -> node.AsArray() |> Seq.head
        equal "enum" (stringValue structuredValue.["kind"]) "structured runtime value has a dedicated enum tag"
        equal "RenewalState" (stringValue structuredValue.["name"]) "structured runtime value retains nominal enum identity"
        equal "pending" (stringValue structuredValue.["case"]) "structured runtime value retains the selected case"

        let sourceType target name = dispatch target "source" [ "type", jstr name ]
        equal (FlowSource.renderEnum enumDefinition)
            (sourceType engine "RenewalState" |> expectOk "read committed enum source" |> fun response -> stringValue response["data"])
            "source(type) exposes the committed enum declaration"
        equal "\"Pending\""
            (evalEnum engine "renewal::describe(RenewalState::pending())" false
             |> expectOk "execute exhaustive enum match before rename"
             |> fun response -> response.["data"].["stack"] |> fun node -> node.AsArray() |> Seq.head |> stringValue)
            "interpreter executes the matching authored enum arm"

        dispatch engine "rename" [ "word", jstr "renewal.echo"; "to", jstr "renewal.echo-state" ]
        |> expectOk "rename a helper referenced by an enum scrutinee and match arms"
        |> ignore
        let afterRename = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        let renamedManifest = afterRename.Manifest |> Option.defaultWith (fun () -> failwith "renamed enum project lost its manifest")
        let renamedHead = renamedManifest.Words |> List.find (fun item -> item.CurrentName = "renewal.describe")
        let renamedRevision = renamedManifest.Revisions |> List.find (fun item -> item.WordId = renamedHead.WordId && item.Revision = renamedHead.CurrentRevision)
        let renamedHelperBindings = renamedRevision.CallBindings |> List.filter (fun binding -> binding.BodyRole = StoredCallBodyRole.Definition && binding.RequestedName = "renewal.echo-state")
        check (renamedHelperBindings |> List.exists (fun binding -> match binding.Path with FlowAstPath.FlowAstPath path -> List.contains FlowAstPathSegment.EnumScrutinee path)) "rename rewrites the enum scrutinee binding without flattening its path"
        check (renamedHelperBindings |> List.exists (fun binding -> match binding.Path with FlowAstPath.FlowAstPath path -> List.exists (function | FlowAstPathSegment.EnumCaseStatement _ -> true | _ -> false) path)) "rename rewrites helper calls nested in enum arms"

        let reloaded = Runtime.Engine(project, Set.empty, "2042-03-04T05:06:07Z")
        assertAllPassed 4 (dispatch reloaded "test-all" [] |> expectOk "run enum tests after fresh Engine reload and rename")
        equal "\"Renewed\""
            (evalEnum reloaded "renewal::describe(RenewalState::renewed())" false
             |> expectOk "execute reloaded enum match"
             |> fun response -> response.["data"].["stack"] |> fun node -> node.AsArray() |> Seq.head |> stringValue)
            "fresh reload compiles and executes the renamed enum helper binding"
        equal (FlowSource.renderEnum enumDefinition)
            (sourceType reloaded "RenewalState" |> expectOk "read enum source after fresh reload" |> fun response -> stringValue response["data"])
            "fresh reload retains exact enum source bytes"

        let sourceReferences =
            [ renamedManifest.ProjectSource ]
            @ (renamedManifest.Types |> List.map _.Definition)
            @ (renamedManifest.Revisions |> List.collect (fun revision -> revision.Definition :: revision.Tests @ revision.Examples))
            |> List.distinct
        let sourceObjects =
            sourceReferences
            |> List.map (fun reference ->
                let content = Storage.readSource store reference |> Result.defaultWith (fun problem -> failwith problem.Message)
                { Reference = reference; Content = content })
        let exportText = File.ReadAllText(Path.Combine(project, "dictionary.agent"))
        let expectTamperRejected label mutateBinding =
            let candidateProject = Path.Combine(root, "enum-binding-tamper-" + label)
            let candidateStore = Storage.create candidateProject
            let targetHead = renamedManifest.Words |> List.find (fun item -> item.CurrentName = "renewal.describe")
            let targetRevision = renamedManifest.Revisions |> List.find (fun item -> item.WordId = targetHead.WordId && item.Revision = targetHead.CurrentRevision)
            let targetBinding = targetRevision.CallBindings |> List.find (fun binding -> binding.BodyRole = StoredCallBodyRole.Definition && binding.RequestedName = "RenewalState.pending")
            let forgedRevision =
                { targetRevision with
                    CallBindings = targetRevision.CallBindings |> List.map (fun binding -> if binding = targetBinding then mutateBinding binding else binding) }
            let forgedManifest =
                { renamedManifest with
                    Revisions = renamedManifest.Revisions |> List.map (fun revision -> if revision.WordId = targetHead.WordId && revision.Revision = targetRevision.Revision then forgedRevision else revision) }
            Storage.commit candidateStore 0L forgedManifest sourceObjects exportText
            |> Result.defaultWith (fun problem -> failwith $"well-shaped enum binding tamper should store for runtime attestation: {problem.Code}: {problem.Message}")
            |> ignore
            try
                Runtime.Engine(candidateProject, Set.empty) |> ignore
                failwith $"fresh Engine trusted a forged enum binding {label}"
            with
            | LanguageException diagnostic -> equal "FLOW_RUNTIME_BINDING_MISMATCH" diagnostic.Code $"fresh Engine rejects enum binding {label} tamper"
        expectTamperRejected "target" (fun binding -> { binding with Target = StoredCallTarget.GeneratedWord "generated-forged-enum-target" })
        expectTamperRejected "path" (fun binding ->
            match binding.Path with
            | FlowAstPath.FlowAstPath path ->
                { binding with Path = FlowAstPath.FlowAstPath(path |> List.map (function | FlowAstPathSegment.EnumCaseStatement(0, statement) -> FlowAstPathSegment.EnumCaseStatement(0, statement + 1) | segment -> segment)) })

    let private testLibraryDependencyQualification root =
        let directProject = Path.Combine(root, "library-dependency-direct")
        let directEngine = Runtime.Engine(directProject, Set.empty)
        let directSource =
            "fn direct.helper(value: Int) -> Int { add(value, 1) }\n\n"
            + "fn direct.wrapper(value: Int) -> Int { direct::helper(value) }\n\n"
            + "test direct.helper/basic { direct::helper(1) => 2 }\n\n"
            + "test direct.wrapper/basic { direct::wrapper(1) => 2 }"
        defineFlowProject directEngine directSource [ "syntaxVersion", jint 2 ] |> expectOk "stage an ordinary helper and its wrapper" |> ignore
        commit directEngine "commit" "direct.helper" [] |> expectOk "persist the ordinary direct helper" |> ignore
        let directStore = Storage.create directProject
        let beforeDirectReject = Storage.load directStore |> Result.defaultWith (fun problem -> failwith problem.Message)
        let directRejected = commit directEngine "commit" "direct.wrapper" [ "library", jbool true ] |> expectError "LIBRARY_DEPENDENCY_NOT_QUALIFIED"
        let directDiagnostic = directRejected.["error"]
        equal "direct.wrapper" (stringValue directDiagnostic.["word"]) "direct dependency diagnostic names the library target"
        check ((stringValue directDiagnostic.["message"]).Contains("direct.helper", StringComparison.Ordinal)) "direct dependency diagnostic names the ordinary helper"
        equal beforeDirectReject.ManifestHash (Storage.load directStore |> Result.defaultWith (fun problem -> failwith problem.Message) |> fun loaded -> loaded.ManifestHash) "direct dependency rejection leaves the manifest unchanged"
        equal "candidate" (stringValue ((findWord (dispatch directEngine "words" [] |> expectOk "inspect direct rejection state") "direct.wrapper").["status"])) "direct dependency rejection leaves the wrapper staged"

        let callbackProject = Path.Combine(root, "library-dependency-callback")
        let callbackEngine = Runtime.Engine(callbackProject, Set.empty)
        let callbackSource =
            "fn callback.helper(value: Int) -> Int { add(value, 1) }\n\n"
            + "fn callback.owner(values: List<Int>) -> Int { list::count(values.map(callback::helper)) }\n\n"
            + "test callback.helper/basic { callback::helper(1) => 2 }\n\n"
            + "test callback.owner/basic { callback::owner(list::singleton<Int>(1)) => 1 }"
        defineFlowProject callbackEngine callbackSource [ "syntaxVersion", jint 2 ] |> expectOk "stage a static collection callback caller" |> ignore
        let callbackDependencies = dispatch callbackEngine "dependencies" [ "word", jstr "callback.owner" ] |> expectOk "inspect the static callback dependency"
        check (jsonArrayStrings callbackDependencies.["data"].["dependencies"] |> List.contains "callback.helper") "the collection callback is present in compiler dependencies"
        commit callbackEngine "commit" "callback.helper" [] |> expectOk "persist the ordinary callback helper" |> ignore
        let beforeCallbackReject = Storage.load (Storage.create callbackProject) |> Result.defaultWith (fun problem -> failwith problem.Message)
        commit callbackEngine "commit" "callback.owner" [ "library", jbool true ]
        |> expectError "LIBRARY_DEPENDENCY_NOT_QUALIFIED"
        |> ignore
        equal beforeCallbackReject.ManifestHash (Storage.load (Storage.create callbackProject) |> Result.defaultWith (fun problem -> failwith problem.Message) |> fun loaded -> loaded.ManifestHash) "callback dependency rejection leaves the manifest unchanged"

        let ordinaryProject = Path.Combine(root, "library-dependency-ordinary-composition")
        let ordinaryEngine = Runtime.Engine(ordinaryProject, Set.empty)
        let ordinarySource =
            "fn z.leaf(value: Int) -> Int { add(value, 1) }\n\n"
            + "fn z.middle(value: Int) -> Int { z::leaf(value) }\n\n"
            + "fn a.root(value: Int) -> Int { z::middle(value) }\n\n"
            + "test z.leaf/basic { z::leaf(1) => 2 }\n\n"
            + "test z.middle/basic { z::middle(1) => 2 }\n\n"
            + "test a.root/basic { a::root(1) => 2 }"
        defineFlowProject ordinaryEngine ordinarySource [ "syntaxVersion", jint 2 ] |> expectOk "stage a transitive ordinary composition" |> ignore
        commit ordinaryEngine "commit" "" [] |> expectOk "commit the ordinary composition together" |> ignore
        equal "2" (stringValue (evalFlow ordinaryEngine "a::root(1)" |> expectOk "evaluate ordinary-to-ordinary composition" |> fun response -> response.["data"].["stack"].[0])) "ordinary words may compose through ordinary dependencies"
        for name in [ "a.root"; "z.middle"; "z.leaf" ] do
            equal "project" (stringValue (dispatch ordinaryEngine "describe" [ "word", jstr name ] |> expectOk "inspect ordinary composition maturity" |> fun response -> response.["data"].["maturity"])) $"ordinary composition keeps {name} at project maturity"

        let ordinaryStore = Storage.create ordinaryProject
        let ordinaryManifest = (Storage.load ordinaryStore |> Result.defaultWith (fun problem -> failwith problem.Message)).Manifest |> Option.defaultWith (fun () -> failwith "ordinary composition did not persist")
        let libraryNames = Set.ofList [ "a.root"; "z.middle" ]
        let forgedManifest =
            { ordinaryManifest with
                Revisions = ordinaryManifest.Revisions |> List.map (fun revision -> if libraryNames.Contains revision.Name then { revision with Maturity = LibraryWord } else revision) }
        let sourceReferences =
            [ ordinaryManifest.ProjectSource ]
            @ (ordinaryManifest.Types |> List.map (fun value -> value.Definition))
            @ (ordinaryManifest.Revisions |> List.collect (fun revision -> [ revision.Definition ] @ revision.Tests @ revision.Examples))
            |> List.distinct
        let sourceObjects =
            sourceReferences
            |> List.map (fun reference ->
                { Reference = reference
                  Content = Storage.readSource ordinaryStore reference |> Result.defaultWith (fun problem -> failwith problem.Message) })
        let forgedProject = Path.Combine(root, "library-dependency-forged-transitive")
        Storage.commit (Storage.create forgedProject) 0L forgedManifest sourceObjects (File.ReadAllText(Path.Combine(ordinaryProject, "dictionary.agent")))
        |> Result.defaultWith (fun problem -> failwith $"could not persist the exact-source transitive qualification fixture: {problem.Code}: {problem.Message}")
        |> ignore
        let transitiveDiagnostic =
            try
                Runtime.Engine(forgedProject, Set.empty) |> ignore
                failwith "fresh Engine accepted a persisted transitive library dependency on a project word"
            with
            | LanguageException diagnostic -> diagnostic
        equal "LIBRARY_DEPENDENCY_NOT_QUALIFIED" transitiveDiagnostic.Code "reload requalification uses the library dependency diagnostic"
        equal (Some "a.root") transitiveDiagnostic.Word "transitive dependency diagnostic names the outer library target"
        equal [ "z.leaf" ] transitiveDiagnostic.Actual "transitive dependency diagnostic names the first unqualified leaf"

        let groupProject = Path.Combine(root, "library-dependency-group")
        let groupEngine = Runtime.Engine(groupProject, Set.empty)
        let groupSource =
            "enum GroupState { case ready; }\n\n"
            + "fn group.helper(value: Int) -> Int { add(value, 1) }\n\n"
            + "fn group.wrapper(value: Int) -> Int { group::helper(value) }\n\n"
            + "fn group.generated(value: Int) -> Int { GroupState::ready(); add(value, 1) }\n\n"
            + "test group.helper/basic { group::helper(1) => 2 }\n\n"
            + "test group.wrapper/basic { group::wrapper(1) => 2 }\n\n"
            + "test group.generated/basic { group::generated(1) => 2 }"
        defineFlowProject groupEngine groupSource [ "syntaxVersion", jint 2 ] |> expectOk "stage a mutually dependent library candidate group" |> ignore
        commit groupEngine "commit" "" [ "library", jbool true ] |> expectOk "qualify the selected acyclic library group atomically" |> ignore
        for name in [ "group.helper"; "group.wrapper"; "group.generated" ] do
            equal "library" (stringValue (dispatch groupEngine "describe" [ "word", jstr name ] |> expectOk "inspect group library maturity" |> fun response -> response.["data"].["maturity"])) $"group commit qualifies {name}"
        let groupReload = Runtime.Engine(groupProject, Set.empty)
        assertAllPassed 1 (dispatch groupReload "test" [ "word", jstr "group.wrapper" ] |> expectOk "reload the qualified group")
        equal "2" (stringValue (evalFlow groupReload "group::wrapper(1)" |> expectOk "execute reloaded qualified composition" |> fun response -> response.["data"].["stack"].[0])) "qualified helper composition survives reload"

        let groupRevision name =
            dispatch groupEngine "describe" [ "word", jstr name ]
            |> expectOk $"inspect {name} before grouped replacement"
            |> fun response -> response.["data"].["revision"].GetValue<int>()
        let stageGroupReplacement name source testSource expectedRevision =
            defineFlow groupEngine source [ testSource ] [] [ "replace", jbool true; "expectedRevision", jint expectedRevision; "syntaxVersion", jint 2 ]
            |> expectOk $"stage grouped replacement for {name}"
            |> ignore
        let helperRevision = groupRevision "group.helper"
        let wrapperRevision = groupRevision "group.wrapper"
        stageGroupReplacement "group.helper" "fn group.helper(value: Int) -> Int { add(value, 2) }" "test group.helper/basic { group::helper(1) => 3 }" helperRevision
        stageGroupReplacement "group.wrapper" "fn group.wrapper(value: Int) -> Int { group::helper(value) }" "test group.wrapper/basic { group::wrapper(1) => 3 }" wrapperRevision
        commit groupEngine "commit" "" [ "library", jbool true ] |> expectOk "publish staged replacements as a fully gated library group" |> ignore
        equal "3" (stringValue (evalFlow groupEngine "group::wrapper(1)" |> expectOk "evaluate the replaced qualified group" |> fun response -> response.["data"].["stack"].[0])) "staged library candidates can be qualified together after all own gates pass"

        let replacementProject = Path.Combine(root, "library-dependency-replacement")
        let mutable replacementEngine = Runtime.Engine(replacementProject, Set.empty)
        let replacementSource =
            "fn replace.helper(value: Int) -> Int { add(value, 1) }\n\n"
            + "fn replace.caller(value: Int) -> Int { replace::helper(value) }\n\n"
            + "test replace.helper/basic { replace::helper(1) => 2 }\n\n"
            + "test replace.caller/basic { replace::caller(1) => 2 }"
        defineFlowProject replacementEngine replacementSource [ "syntaxVersion", jint 2 ] |> expectOk "stage a library helper and affected caller" |> ignore
        commit replacementEngine "commit" "replace.helper" [ "library", jbool true ] |> expectOk "qualify the replacement helper baseline" |> ignore
        commit replacementEngine "commit" "replace.caller" [ "library", jbool true ] |> expectOk "qualify the persistent affected caller baseline" |> ignore
        defineFlow replacementEngine "fn replace.ordinary(value: Int) -> Int { add(value, 9) }" [ "test replace.ordinary/basic { replace::ordinary(1) => 10 }" ] [] [ "syntaxVersion", jint 2 ]
        |> expectOk "stage an ordinary helper for the attempted replacement"
        |> ignore
        commit replacementEngine "commit" "replace.ordinary" [] |> expectOk "persist the ordinary helper used by the unsafe replacement" |> ignore
        let replacementStore = Storage.create replacementProject
        let beforeUnsafeReplacement = Storage.load replacementStore |> Result.defaultWith (fun problem -> failwith problem.Message)
        let callerRevision =
            dispatch replacementEngine "describe" [ "word", jstr "replace.caller" ]
            |> expectOk "inspect the affected caller before its staged replacement"
            |> fun response -> response.["data"].["revision"].GetValue<int>()
        let helperRevisionForCallerCheck =
            dispatch replacementEngine "describe" [ "word", jstr "replace.helper" ]
            |> expectOk "inspect the helper before its staged replacement"
            |> fun response -> response.["data"].["revision"].GetValue<int>()
        defineFlow replacementEngine "fn replace.caller(value: Int) -> Int { add(value, 5) }" [ "test replace.caller/basic { replace::caller(1) => 6 }" ] [] [ "replace", jbool true; "expectedRevision", jint callerRevision; "syntaxVersion", jint 2 ]
        |> expectOk "stage a passing caller replacement that removes its helper dependency"
        |> ignore
        defineFlow replacementEngine "fn replace.helper(value: Int) -> Int { add(value, 2) }" [ "test replace.helper/basic { replace::helper(1) => 3 }" ] [] [ "replace", jbool true; "expectedRevision", jint helperRevisionForCallerCheck; "syntaxVersion", jint 2 ]
        |> expectOk "stage a helper replacement behind the affected caller"
        |> ignore
        assertAllPassed 1 (dispatch replacementEngine "test" [ "word", jstr "replace.caller" ] |> expectOk "run the passing staged caller replacement")
        let rejectedAffectedCaller = commit replacementEngine "commit" "replace.helper" [] |> expectError "COMMIT_TESTS_FAILED"
        check (jsonArrayStrings rejectedAffectedCaller.["error"].["actual"] |> List.contains "replace.caller/basic") "publishing the helper alone tests the durable caller even when its passing staged replacement removes that edge"
        equal beforeUnsafeReplacement.ManifestHash (Storage.load replacementStore |> Result.defaultWith (fun problem -> failwith problem.Message) |> fun loaded -> loaded.ManifestHash) "failed affected-caller replacement leaves the old manifest authoritative"
        replacementEngine <- Runtime.Engine(replacementProject, Set.empty)
        let durableCallerSource = dispatch replacementEngine "source" [ "word", jstr "replace.caller" ] |> expectOk "read the caller restored from durable state" |> fun response -> stringValue response.["data"]
        check (durableCallerSource.Contains("replace::helper", StringComparison.Ordinal)) "fresh reload retains the durable caller edge after failed staged replacements"
        let helperRevision =
            dispatch replacementEngine "describe" [ "word", jstr "replace.helper" ]
            |> expectOk "inspect library helper before replacement"
            |> fun response -> response.["data"].["revision"].GetValue<int>()
        defineFlow replacementEngine "fn replace.helper(value: Int) -> Int { replace::ordinary(value) }" [ "test replace.helper/basic { replace::helper(1) => 10 }" ] [] [ "replace", jbool true; "expectedRevision", jint helperRevision; "syntaxVersion", jint 2 ]
        |> expectError "LIBRARY_DEPENDENCY_NOT_QUALIFIED"
        |> ignore
        equal beforeUnsafeReplacement.ManifestHash (Storage.load replacementStore |> Result.defaultWith (fun problem -> failwith problem.Message) |> fun loaded -> loaded.ManifestHash) "unsafe library helper replacement leaves the old manifest authoritative"
        let replacementReload = Runtime.Engine(replacementProject, Set.empty)
        assertAllPassed 1 (dispatch replacementReload "test" [ "word", jstr "replace.caller" ] |> expectOk "requalify the preserved affected caller on reload")
        equal "2" (stringValue (evalFlow replacementReload "replace::caller(1)" |> expectOk "evaluate preserved caller after rejected helper replacement" |> fun response -> response.["data"].["stack"].[0])) "fresh reload retains the previous qualified helper and caller"

    let private testEnumFiniteLibraryCoverage root =
        let enumAndHelpers =
            "enum RenewalState { case pending; case renewed; case cancelled; }\n\n"
            + "record StateEnvelope { field maybe: Option<List<RenewalState>>; }\n\n"
            + "fn internal.match-state(state: RenewalState) -> Int {\n"
            + "    match state { pending => { 1 } renewed => { 2 } cancelled => { 3 } }\n"
            + "}\n\n"
            + "fn internal.record-state(value: StateEnvelope) -> Int {\n"
            + "    0\n"
            + "}\n\n"
            + "fn public.delegate(state: RenewalState) -> Int {\n"
            + "    internal::match-state(state)\n"
            + "}\n\n"
            + "fn public.container-use(value: Int) -> Int {\n"
            + "    option::some<List<RenewalState>>(list::empty<RenewalState>());\n"
            + "    value\n"
            + "}\n\n"
            + "fn public.record-use(value: Int) -> Int {\n"
            + "    internal::record-state(stateEnvelope::new(option::some<List<RenewalState>>(list::empty<RenewalState>())));\n"
            + "    value\n"
            + "}\n\n"
            + "fn public.construct-use(value: Int) -> Int {\n"
            + "    RenewalState::pending();\n"
            + "    value\n"
            + "}\n\n"
            + "fn public.pure(value: Int) -> Int {\n"
            + "    value\n"
            + "}\n\n"
            + "test internal.record-state/basic {\n"
            + "    internal::record-state(stateEnvelope::new(option::none<List<RenewalState>>()))\n"
            + "    => 0\n"
            + "}\n\n"
            + "test public.pure/basic {\n"
            + "    public::pure(7)\n"
            + "    => 7\n"
            + "}\n\n"
            + "test internal.match-state/pending { internal::match-state(RenewalState::pending()) => 1 }\n"
            + "test internal.match-state/renewed { internal::match-state(RenewalState::renewed()) => 2 }\n"
            + "test internal.match-state/cancelled { internal::match-state(RenewalState::cancelled()) => 3 }\n"
            + "test public.delegate/pending { public::delegate(RenewalState::pending()) => 1 }\n"
            + "test public.delegate/renewed { public::delegate(RenewalState::renewed()) => 2 }\n"
            + "test public.delegate/cancelled { public::delegate(RenewalState::cancelled()) => 3 }\n"
            + "test public.container-use/basic { public::container-use(8) => 8 }\n"
            + "test public.construct-use/basic { public::construct-use(9) => 9 }"
        let project = Path.Combine(root, "enum-library-qualification")
        let engine = Runtime.Engine(project, Set.empty)
        defineFlowProject engine enumAndHelpers [ "syntaxVersion", jint 2 ] |> expectOk "stage enum-bearing helper and primitive-signature library candidates" |> ignore
        commit engine "commit" "internal.match-state" [ "library", jbool true ]
        |> expectOk "qualify an enum-taking match after tests cover every input and match arm"
        |> ignore
        commit engine "commit" "public.delegate" [ "library", jbool true ]
        |> expectOk "qualify an enum caller after its independently qualified helper"
        |> ignore
        commit engine "commit" "public.container-use" [ "library", jbool true ]
        |> expectOk "qualify a function whose local generic type mentions an enum"
        |> ignore
        commit engine "commit" "public.construct-use" [ "library", jbool true ]
        |> expectOk "qualify a function that constructs and discards an enum value"
        |> ignore
        commit engine "commit" "internal.record-state" []
        |> expectOk "commit an ordinary helper that accepts a generated record"
        |> ignore
        commit engine "commit" "public.record-use" [ "library", jbool true ]
        |> expectError "LIBRARY_DEPENDENCY_NOT_QUALIFIED"
        |> fun response ->
            let diagnostic = response.["error"]
            equal "public.record-use" (stringValue diagnostic.["word"]) "library dependency diagnostic names the rejected target"
            let message = stringValue diagnostic.["message"]
            check (message.Contains("internal.record-state", StringComparison.Ordinal)) "library dependency diagnostic names the unqualified helper"
            check (message.Contains("own passing tests and complete coverage", StringComparison.Ordinal)) "library dependency diagnostic explains how to qualify the helper"
            response
        |> ignore

        commit engine "commit" "public.pure" [ "library", jbool true ]
        |> expectOk "qualify an enum-free word beside an unrelated enum declaration"
        |> ignore
        equal "7" (evalFlow engine "public::pure(7)" |> expectOk "evaluate the independently qualified enum-free word" |> fun response -> stringValue response.["data"].["stack"].[0]) "unrelated enum declarations do not block an enum-free library word"

        let store = Storage.load (Storage.create project) |> Result.defaultWith (fun problem -> failwith problem.Message)
        let beforeReplacement = store.Manifest |> Option.defaultWith (fun () -> failwith "enum-free qualification should publish a manifest")
        let beforeReplacementHash = store.ManifestHash
        equal [ "internal.match-state"; "internal.record-state"; "public.construct-use"; "public.container-use"; "public.delegate"; "public.pure" ] (beforeReplacement.Words |> List.map (fun item -> item.CurrentName) |> List.sort) "qualified enum words and the ordinary helper persist while its library caller does not"
        for name in [ "public.record-use" ] do
            check ((findWord (dispatch engine "words" [] |> expectOk "inspect candidates after enum library rejection") name).["status"].GetValue<string>() = "candidate") $"rejected library candidate {name} remains staged atomically"

        let currentRevision =
            dispatch engine "describe" [ "word", jstr "public.pure" ]
            |> expectOk "inspect qualified enum-free word before replacement"
            |> fun response -> response.["data"].["revision"].GetValue<int>()
        let enumReplacement =
            "fn public.pure(value: Int) -> Int {\n"
            + "    match RenewalState::pending() { pending => { value } renewed => { value } cancelled => { value } }\n"
            + "}"
        let enumReplacementTest = "test public.pure/basic { public::pure(7) => 7 }"
        defineFlow engine enumReplacement [ enumReplacementTest ] [] [ "replace", jbool true; "expectedRevision", jint currentRevision; "syntaxVersion", jint 2 ]
        |> expectOk "stage a direct enum match replacement before evaluating its test evidence"
        |> ignore
        commit engine "replace-word" "public.pure" []
        |> expectError "LIBRARY_COVERAGE_INCOMPLETE"
        |> ignore
        equal beforeReplacementHash (Storage.load (Storage.create project) |> Result.defaultWith (fun problem -> failwith problem.Message)).ManifestHash "incompletely covered enum replacement leaves the committed library manifest unchanged"
        let durableAfterReplacement = Storage.load (Storage.create project) |> Result.defaultWith (fun problem -> failwith problem.Message)
        let durablePure = durableAfterReplacement.Manifest |> Option.defaultWith (fun () -> failwith "library manifest is missing") |> _.Words |> List.find (fun item -> item.CurrentName = "public.pure")
        equal currentRevision durablePure.CurrentRevision "failed enum replacement does not advance the durable library revision"

        let discardProject = Path.Combine(root, "enum-discard-dependency")
        let discardEngine = Runtime.Engine(discardProject, Set.empty)
        let dependentSource =
            """enum RenewalState {
    case pending;
    case renewed;
    case cancelled;
}

fn renewal.dependent(state: RenewalState) -> String {
    match state {
        pending => { "Pending" }
        renewed => { "Renewed" }
        cancelled => { "Cancelled" }
    }
}"""
        defineFlowProject discardEngine dependentSource [ "syntaxVersion", jint 2 ] |> expectOk "stage a candidate enum and dependent match" |> ignore
        let rejectedDiscard = dispatch discardEngine "discard" [ "word", jstr "RenewalState" ]
        check (not (succeeded rejectedDiscard)) "discarding an enum with a live candidate dependency is rejected"
        check (succeeded (dispatch discardEngine "source" [ "type", jstr "RenewalState" ])) "failed enum discard leaves the authored type available"
        dispatch discardEngine "discard" [ "word", jstr "renewal.dependent" ] |> expectOk "discard the dependent word first" |> ignore
        dispatch discardEngine "discard" [ "word", jstr "RenewalState" ] |> expectOk "discard the enum after its dependent word" |> ignore

    let private testDescribeFlowReferences root =
        let engine = Runtime.Engine(Path.Combine(root, "describe-flow-references"), Set.empty)
        let stackSource =
            "record Cart\n"
            + "    field value Int\n"
            + "end\n\n"
            + "word advance : Int -> Int\n    effects none\n    100 add\nend\n\n"
            + "word math.advance : Int -> Int\n    effects none\n    1 add\nend\n\n"
            + "word tools.math.advance : Int -> Int\n    effects none\n    10 add\nend\n\n"
            + "word if.target : Int -> Int\n    effects none\n    dup\n    drop\nend\n\n"
            + "word match.target : Int -> Int\n    effects none\n    dup\n    drop\nend\n\n"
            + "word true.target : Int -> Int\n    effects none\n    dup\n    drop\nend\n\n"
            + "word false.target : Int -> Int\n    effects none\n    dup\n    drop\nend\n\n"
            + "word unit.target : Int -> Int\n    effects none\n    dup\n    drop\nend\n"
        defineStack engine stackSource [] |> expectOk "define exact root, nested names, record, and protected-prefix words" |> ignore

        let describe name =
            dispatch engine "describe" [ "word", jstr name ] |> expectOk $"describe {name}" |> fun response -> response.["data"]
        let flowReference name =
            let data = describe name
            check (not (isNull data.["flowReference"])) $"{name} has a Flow reference"
            equal 2 (data.["flowReferenceSyntaxVersion"].GetValue<int>()) $"{name} reference is explicitly versioned as Flow/2"
            stringValue data.["flowReference"]
        let evalFlow2 code =
            dispatch engine "eval" [ "frontend", jstr "flow"; "syntaxVersion", jint 2; "code", jstr code ]

        let advanceReference = flowReference "advance"
        equal ".advance" advanceReference "unqualified dictionary keys use exact-root Flow/2 references"
        let namespaceReference = flowReference "tools.math.advance"
        equal ".tools.math.advance" namespaceReference "multi-segment dictionary keys preserve every exact namespace segment"
        equal "102" (stringValue (evalFlow2 $"{advanceReference}(2)" |> expectOk "evaluate described exact-root reference" |> fun response -> response.["data"].["stack"].[0])) "described root reference resolves to the exact root word"
        equal "11" (stringValue (evalFlow2 $"{namespaceReference}(1)" |> expectOk "evaluate described multi-segment reference" |> fun response -> response.["data"].["stack"].[0])) "described multi-segment reference survives root and suffix collisions"
        let mathReference = flowReference "math.advance"
        equal ".math.advance" mathReference "shorter namespace references keep their full exact key"
        equal "2" (stringValue (evalFlow2 $"{mathReference}(1)" |> expectOk "evaluate described suffix namespace reference" |> fun response -> response.["data"].["stack"].[0])) "the shorter namespace reference resolves to its exact dictionary key"

        let floatReference = flowReference "float.add"
        equal ".float.add" floatReference "primitive Float references use exact dotted qualification"
        let floatResult = evalFlow2 $"{floatReference}(1.5, 2.25)" |> expectOk "evaluate described primitive Float reference"
        equal "Float" (stringValue floatResult.["data"].["stackTypes"].[0]) "described primitive Float reference keeps its Float type"
        equal "3.75" (stringValue floatResult.["data"].["stack"].[0]) "described primitive Float reference computes its value"

        let tailDescription = describe "list.tail"
        equal "primitive" (stringValue tailDescription.["kind"]) "list.tail is exposed as a dictionary primitive"
        equal [ "List<a>" ] (jsonArrayStrings tailDescription.["inputs"]) "list.tail discovery reports its generic list input"
        equal [ "List<a>" ] (jsonArrayStrings tailDescription.["outputs"]) "list.tail discovery reports the same generic list output"
        check ((stringValue tailDescription.["documentation"]).Contains("empty lists remain empty", StringComparison.Ordinal))
            "list.tail discovery documents its empty-list behavior"
        let tailReference = flowReference "list.tail"
        equal ".list.tail" tailReference "list.tail has a deterministic Flow/2 reference"
        let tailEmpty = evalFlow2 $"{tailReference}(list.empty<Int>())" |> expectOk "evaluate typed empty-list tail"
        equal "List<Int>" (stringValue tailEmpty.["data"].["stackTypes"].[0]) "list.tail preserves the element type of an empty list"
        equal "[]" (stringValue tailEmpty.["data"].["stack"].[0]) "list.tail returns an empty list for an empty input"

        let scalarSource =
            "type Email : String { }\n\n"
            + "fn local.shadow(advance: Int) -> Int {\n"
            + "    effects none\n"
            + $"    {advanceReference}(5)\n"
            + "}\n\n"
            + "fn callbacks.map-values(values: List<Int>) -> List<Int> {\n"
            + "    effects none\n"
            + $"    values.map({advanceReference})\n"
            + "}"
        defineFlowProject engine scalarSource [ "syntaxVersion", jint 2 ] |> expectOk "define Flow scalar and Flow/2 root-shadowing caller" |> ignore
        let recordReference = flowReference "cart.new"
        equal ".cart.new" recordReference "generated record constructors expose their exact Flow/2 call spelling"
        let recordResult = evalFlow2 $"{recordReference}(value = 7)" |> expectOk "evaluate described generated record reference"
        equal "Cart" (stringValue recordResult.["data"].["stackTypes"].[0]) "described record constructor returns the exact nominal record"
        let scalarReference = flowReference "Email.new"
        equal ".Email.new" scalarReference "generated scalar constructors expose their exact Flow/2 call spelling"
        let scalarResult = evalFlow2 $"{scalarReference}(\"contact@example.com\")" |> expectOk "evaluate described generated scalar reference"
        equal "Email" (stringValue scalarResult.["data"].["stackTypes"].[0]) "described scalar constructor returns the exact nominal scalar"
        equal "105" (stringValue (evalFlow2 "local::shadow(0)" |> expectOk "call root target while a same-named local exists" |> fun response -> response.["data"].["stack"].[0])) "legacy namespace-qualified reference bypasses a same-named local"
        let callbackResult = evalFlow2 "callbacks.map-values(list.singleton<Int>(1))" |> expectOk "evaluate metadata-derived callback reference through map"
        equal "[101]" (stringValue callbackResult.["data"].["stack"].[0]) "static map callback uses the exact root word despite suffix collisions"
        let callbackDescription = describe "callbacks.map-values"
        let callbackDependencies = jsonArrayStrings callbackDescription.["dependencies"]
        check (callbackDependencies |> List.contains "advance") "Flow description retains the exact static callback dependency"

        for prefix in [ "if"; "match"; "true"; "false"; "unit" ] do
            let name = prefix + ".target"
            let exactReference = flowReference name
            equal ("." + name) exactReference $"{prefix} prefix has a shadow-safe Flow/2 exact reference"
            equal "1" (stringValue (evalFlow2 $"{exactReference}(1)" |> expectOk $"evaluate exact reference for {name}" |> fun response -> response.["data"].["stack"].[0]))
                $"{prefix} exact reference bypasses its keyword-like namespace prefix"

        let syntax = describe "list.empty"
        equal "syntax" (stringValue syntax.["kind"]) "container constructors remain syntax descriptors"
        check (not (syntax.AsObject().ContainsKey("flowReference"))) "syntax descriptions do not expose a word Flow reference"
        check (not (syntax.AsObject().ContainsKey("flowReferenceUnavailableReason"))) "syntax descriptions do not expose a word-reference failure reason"

    let private testGeneratedRecordCasesPersistBesideFlow root =
        let project = Path.Combine(root, "flow-generated-record-cases")
        let engine = Runtime.Engine(project, Set.empty, "2030-01-02T03:04:05Z")
        defineFlow engine flowWordSource [ flowTestSource ] [ flowExampleSource ] []
        |> expectOk "define Flow authority beside a generated record type"
        |> ignore
        commit engine "commit" "durable.increment" []
        |> expectOk "commit Flow authority before adding a Stack record"
        |> ignore

        let stackSource =
            "record Receipt\n"
            + "    field amount Int\n"
            + "end\n"
            + "\n"
            + "test receipt.amount/read\n"
            + "    7 receipt.new receipt.amount\n"
            + "    => 7\n"
            + "end\n"
            + "\n"
            + "example receipt.amount/read\n"
            + "    9 receipt.new receipt.amount\n"
            + "    => 9\n"
            + "end\n"
        let parsed =
            match Parser.parse "<runtime-generated-record>" stackSource with
            | Ok value -> value
            | Error diagnostic -> failwith (Diagnostics.render diagnostic)
        let canonicalRecord = parsed.Records |> List.exactlyOne |> Source.renderRecord
        let canonicalTest = parsed.Tests |> List.exactlyOne |> Source.renderTest
        let canonicalExample = parsed.Examples |> List.exactlyOne |> Source.renderExample

        defineStack engine stackSource []
        |> expectOk "define a record and generated accessor test/example"
        |> ignore
        equal [ "read" ]
            (dispatch engine "tests" [ "word", jstr "receipt.amount" ] |> expectOk "inspect staged generated accessor tests" |> fun response -> jsonArrayStrings response.["data"])
            "generated accessor test is attached before type publication"
        equal [ "read" ]
            (dispatch engine "examples" [ "word", jstr "receipt.amount" ] |> expectOk "inspect staged generated accessor examples" |> fun response -> jsonArrayStrings response.["data"])
            "generated accessor example is attached before type publication"

        commit engine "commit" "Receipt" []
        |> expectOk "commit record type with generated accessor cases"
        |> ignore
        assertAllPassed 1 (dispatch engine "test" [ "word", jstr "receipt.amount" ] |> expectOk "run committed generated accessor test")
        let stagedExample = dispatch engine "example" [ "word", jstr "receipt.amount"; "caseName", jstr "read" ] |> expectOk "run committed generated accessor example"
        check (boolValue (stagedExample.["data"].["results"].[0].["passed"])) "generated accessor example passes before reload"

        let store = Storage.create project
        let loaded = Storage.load store |> Result.defaultWith (fun problem -> failwith $"load mixed Flow and generated record manifest: {problem.Code}: {problem.Message}")
        let manifest = loaded.Manifest |> Option.defaultWith (fun () -> failwith "mixed Flow/generated-record manifest is missing")
        check (manifest.Types |> List.exists (fun item -> item.Name = "Receipt")) "type-only commit persists the record definition"
        let export = loaded.ProjectSource |> Option.defaultWith (fun () -> failwith "mixed project export is missing")
        equal (File.ReadAllText(Path.Combine(project, "dictionary.agent"))) export "aggregate export bytes match the authoritative ProjectSource"
        check (export.Contains("// frontend: flow/1", StringComparison.Ordinal)) "mixed aggregate export preserves its Flow source marker"
        check (export.Contains(canonicalRecord, StringComparison.Ordinal)) "aggregate export contains the canonical record declaration"
        check (export.Contains(canonicalTest, StringComparison.Ordinal)) "aggregate export contains the canonical generated accessor test"
        check (export.Contains(canonicalExample, StringComparison.Ordinal)) "aggregate export contains the canonical generated accessor example"

        let reloaded = Runtime.Engine(project, Set.empty, "2030-01-02T03:04:05Z")
        equal [ "read" ]
            (dispatch reloaded "tests" [ "word", jstr "receipt.amount" ] |> expectOk "inspect generated accessor tests after reload" |> fun response -> jsonArrayStrings response.["data"])
            "generated accessor test survives fresh Engine reload"
        equal [ "read" ]
            (dispatch reloaded "examples" [ "word", jstr "receipt.amount" ] |> expectOk "inspect generated accessor examples after reload" |> fun response -> jsonArrayStrings response.["data"])
            "generated accessor example survives fresh Engine reload"
        assertAllPassed 1 (dispatch reloaded "test" [ "word", jstr "receipt.amount" ] |> expectOk "run generated accessor test after reload")
        let reloadedExample = dispatch reloaded "example" [ "word", jstr "receipt.amount"; "caseName", jstr "read" ] |> expectOk "run generated accessor example after reload"
        check (boolValue (reloadedExample.["data"].["results"].[0].["passed"])) "generated accessor example passes after reload"
        let value = evalStack reloaded "7 receipt.new receipt.amount" |> expectOk "evaluate generated accessor after reload"
        equal "7" (stringValue (value.["data"].["stack"].[0])) "generated accessor body remains executable after reload"

    let private testStackGeneratedCasesSurviveV1Manifest root =
        let project = Path.Combine(root, "stack-generated-cases-v1")
        let engine = Runtime.Engine(project, Set.empty, "2030-01-02T03:04:05Z")
        let source =
            "record Receipt\n"
            + "    field amount Int\n"
            + "end\n"
            + "\n"
            + "word receipt.double : Receipt -> Int\n"
            + "    effects none\n"
            + "    receipt.amount\n"
            + "end\n"
            + "\n"
            + "test receipt.double/basic\n"
            + "    9 receipt.new receipt.double\n"
            + "    => 9\n"
            + "end\n"
            + "\n"
            + "test receipt.amount/read\n"
            + "    7 receipt.new receipt.amount\n"
            + "    => 7\n"
            + "end\n"
            + "\n"
            + "example receipt.amount/read\n"
            + "    11 receipt.new receipt.amount\n"
            + "    => 11\n"
            + "end\n"
        defineStack engine source []
        |> expectOk "define a Stack record, word, and generated accessor cases"
        |> ignore
        commit engine "commit" "Receipt" []
        |> expectOk "commit Stack record and generated accessor cases"
        |> ignore
        commit engine "commit" "receipt.double" []
        |> expectOk "commit a Stack word using the generated accessor"
        |> ignore

        let store = Storage.create project
        let loaded = Storage.load store |> Result.defaultWith (fun problem -> failwith $"load Stack-only manifest: {problem.Code}: {problem.Message}")
        let manifest = loaded.Manifest |> Option.defaultWith (fun () -> failwith "Stack-only manifest is missing")
        equal 2 manifest.FormatVersion "Runtime initially publishes the Stack project as manifest v2"
        check (not (List.isEmpty manifest.Revisions)) "Stack-only fixture has a real word revision"
        check (manifest.Revisions |> List.forall (fun revision -> revision.SourceFormat = { Frontend = SourceFrontend.Stack; Version = 1 })) "every revision uses Stack/1"
        check (manifest.Revisions |> List.forall (fun revision -> List.isEmpty revision.CallBindings)) "Stack/1 revisions retain default empty call-binding metadata"

        let export = loaded.ProjectSource |> Option.defaultWith (fun () -> failwith "Stack-only project export is missing")
        let sourceReferences =
            [ manifest.ProjectSource ]
            @ (manifest.Types |> List.map (fun item -> item.Definition))
            @ (manifest.Revisions |> List.collect (fun revision -> revision.Definition :: revision.Tests @ revision.Examples))
            |> List.distinct
        let sourceObjects =
            sourceReferences
            |> List.map (fun reference ->
                let content = Storage.readSource store reference |> Result.defaultWith (fun problem -> failwith problem.Message)
                { Reference = reference; Content = content })
        let legacyManifest = { manifest with FormatVersion = 1 }
        match Storage.commit store loaded.Generation legacyManifest sourceObjects export with
        | Ok _ -> ()
        | Error problem -> failwith $"recommit the same Stack objects as a v1 manifest: {problem.Code}: {problem.Message}"

        let converted = Storage.load store |> Result.defaultWith (fun problem -> failwith $"load v1 Stack manifest: {problem.Code}: {problem.Message}")
        let convertedManifest = converted.Manifest |> Option.defaultWith (fun () -> failwith "converted v1 manifest is missing")
        equal 1 convertedManifest.FormatVersion "the compatibility step publishes v1 authority"
        equal manifest.Types convertedManifest.Types "v1 conversion keeps the exact type source references"
        equal manifest.Revisions convertedManifest.Revisions "v1 conversion keeps the exact Stack revisions and empty binding metadata"
        equal export (converted.ProjectSource |> Option.defaultWith (fun () -> failwith "converted project export is missing")) "v1 conversion keeps the identical aggregate source"

        let reloaded = Runtime.Engine(project, Set.empty, "2030-01-02T03:04:05Z")
        equal [ "read" ]
            (dispatch reloaded "tests" [ "word", jstr "receipt.amount" ] |> expectOk "inspect generated accessor tests from v1" |> fun response -> jsonArrayStrings response.["data"])
            "v1 aggregate loading retains the generated accessor test"
        equal [ "read" ]
            (dispatch reloaded "examples" [ "word", jstr "receipt.amount" ] |> expectOk "inspect generated accessor examples from v1" |> fun response -> jsonArrayStrings response.["data"])
            "v1 aggregate loading retains the generated accessor example"
        assertAllPassed 1 (dispatch reloaded "test" [ "word", jstr "receipt.amount" ] |> expectOk "run generated accessor test from v1")
        let example = dispatch reloaded "example" [ "word", jstr "receipt.amount"; "caseName", jstr "read" ] |> expectOk "run generated accessor example from v1"
        check (boolValue (example.["data"].["results"].[0].["passed"])) "generated accessor example passes after v1 migration"
        let value = evalStack reloaded "9 receipt.new receipt.double" |> expectOk "evaluate Stack word from v1"
        equal "9" (stringValue (value.["data"].["stack"].[0])) "Stack word remains executable after v1 migration"

    let private testStackOwnerMigrationToFlow root =
        let project = Path.Combine(root, "stack-to-flow-migration")
        let engine = Runtime.Engine(project, Set.empty, "2030-01-02T03:04:05Z")
        let stackDefinition =
            "word migration.increment : Int -> Int\n"
            + "    effects none\n"
            + "    1 add\n"
            + "end\n"
        let stackTest =
            "test migration.increment/basic\n"
            + "    41 migration.increment\n"
            + "    expect 42\n"
            + "end\n"
        let stackExample =
            "example migration.increment/basic\n"
            + "    1 migration.increment\n"
            + "    => 2\n"
            + "end\n"
        let completeStackSource = stackDefinition + "\n" + stackTest + "\n" + stackExample
        defineStack engine completeStackSource []
        |> expectOk "define a Stack owner with authored cases"
        |> ignore
        let wordId = getWordId engine "migration.increment"
        commit engine "commit" "migration.increment" [] |> expectOk "commit Stack owner before Flow migration" |> ignore

        let store = Storage.create project
        let stackSnapshot = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        let stackManifest = stackSnapshot.Manifest |> Option.defaultWith (fun () -> failwith "Stack migration baseline is missing")
        let stackRevision = stackManifest.Revisions |> List.find (fun item -> item.WordId = wordId)
        equal { Frontend = SourceFrontend.Stack; Version = 1 } stackRevision.SourceFormat "migration baseline is explicitly Stack/1"
        equal 1 stackRevision.Tests.Length "migration baseline has one Stack test"
        equal 1 stackRevision.Examples.Length "migration baseline has one Stack example"
        let originalTestReference = stackRevision.Tests.Head
        let originalExampleReference = stackRevision.Examples.Head
        let originalStoredTest = Storage.readSource store originalTestReference |> Result.defaultWith (fun problem -> failwith problem.Message)
        let originalStoredExample = Storage.readSource store originalExampleReference |> Result.defaultWith (fun problem -> failwith problem.Message)
        check (originalStoredTest.Contains("test migration.increment/basic", StringComparison.Ordinal)) "baseline Stack test object contains its declared case"
        check (originalStoredExample.Contains("example migration.increment/basic", StringComparison.Ordinal)) "baseline Stack example object contains its declared case"

        let flowDefinition =
            "word migration.increment(value: Int) -> Int {\n"
            + "    effects none\n"
            + "    add(value, 1)\n"
            + "}"
        let flowTest =
            "test migration.increment/basic {\n"
            + "    migration::increment(41)\n"
            + "    => value migration::increment(41)\n"
            + "}"
        let flowExample =
            "example migration.increment/basic {\n"
            + "    migration::increment(1)\n"
            + "    => 2\n"
            + "}"

        // Omitting case arrays requests retention. A Flow revision cannot inherit
        // Stack case objects, so either staging or publication must reject this.
        let mixedStage =
            defineFlow engine flowDefinition [] [] [ "replace", jbool true; "expectedRevision", jint 1 ]
        let mixedFailure =
            if not (succeeded mixedStage) then mixedStage
            else commit engine "replace-word" "migration.increment" []
        check (not (succeeded mixedFailure)) "Flow owner replacement rejects retained Stack test/example objects"
        check (not (String.IsNullOrWhiteSpace(errorCode mixedFailure))) "mixed-frontend rejection is structured"
        let afterMixedAttempt = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        equal stackSnapshot.ManifestHash afterMixedAttempt.ManifestHash "rejected mixed-frontend migration leaves CURRENT unchanged"
        let afterMixedManifest = afterMixedAttempt.Manifest |> Option.defaultWith (fun () -> failwith "Stack owner disappeared after rejected migration")
        let afterMixedRevision = afterMixedManifest.Revisions |> List.find (fun item -> item.WordId = wordId && item.Revision = 1)
        equal stackRevision.Tests afterMixedRevision.Tests "rejected mixed migration retains the exact old Stack test reference"
        equal stackRevision.Examples afterMixedRevision.Examples "rejected mixed migration retains the exact old Stack example reference"
        if succeeded mixedStage then
            dispatch engine "discard" [ "word", jstr "migration.increment" ]
            |> expectOk "discard rejected mixed-frontend Flow candidate"
            |> ignore
        let stillStack = Runtime.Engine(project, Set.empty, "2030-01-02T03:04:05Z")
        let oldEvaluation = evalStack stillStack "1 migration.increment" |> expectOk "reload Stack owner after rejected migration"
        equal "2" (stringValue (oldEvaluation.["data"].["stack"].[0])) "failed migration leaves the Stack implementation executable"
        assertAllPassed 1 (dispatch stillStack "test" [ "word", jstr "migration.increment" ] |> expectOk "rerun Stack case after rejected migration")

        defineFlow engine flowDefinition [ flowTest ] [ flowExample ]
            [ "replace", jbool true
              "expectedRevision", jint 1 ]
        |> expectOk "replace every Stack case with Flow-authored cases"
        |> ignore
        commit engine "replace-word" "migration.increment" []
        |> expectOk "publish complete Stack-to-Flow migration"
        |> ignore
        equal wordId (getWordId engine "migration.increment") "frontend migration preserves the stable owner ID"

        let migrated = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        let migratedManifest = migrated.Manifest |> Option.defaultWith (fun () -> failwith "Flow migration did not publish")
        let revisions = migratedManifest.Revisions |> List.filter (fun item -> item.WordId = wordId) |> List.sortBy _.Revision
        equal [ 1; 2 ] (revisions |> List.map _.Revision) "migration retains both immutable owner revisions"
        let oldRevision = revisions.Head
        let flowRevision = List.item 1 revisions
        equal { Frontend = SourceFrontend.Stack; Version = 1 } oldRevision.SourceFormat "historical revision remains Stack/1"
        equal [ originalTestReference ] oldRevision.Tests "historical Stack test reference is unchanged"
        equal [ originalExampleReference ] oldRevision.Examples "historical Stack example reference is unchanged"
        equal originalStoredTest (Storage.readSource store oldRevision.Tests.Head |> Result.defaultWith (fun problem -> failwith problem.Message)) "historical Stack test bytes remain available unchanged"
        equal originalStoredExample (Storage.readSource store oldRevision.Examples.Head |> Result.defaultWith (fun problem -> failwith problem.Message)) "historical Stack example bytes remain available unchanged"
        equal { Frontend = SourceFrontend.Flow; Version = 1 } flowRevision.SourceFormat "current revision is Flow/1"
        equal flowDefinition (Storage.readSource store flowRevision.Definition |> Result.defaultWith (fun problem -> failwith problem.Message)) "migrated definition preserves authored Flow source"
        equal [ flowTest ] (flowRevision.Tests |> List.map (fun reference -> Storage.readSource store reference |> Result.defaultWith (fun problem -> failwith problem.Message))) "all current tests are Flow-authored"
        equal [ flowExample ] (flowRevision.Examples |> List.map (fun reference -> Storage.readSource store reference |> Result.defaultWith (fun problem -> failwith problem.Message))) "all current examples are Flow-authored"

        let reloaded = Runtime.Engine(project, Set.empty, "2030-01-02T03:04:05Z")
        equal flowDefinition (dispatch reloaded "source" [ "word", jstr "migration.increment" ] |> expectOk "read migrated source after reload" |> fun response -> stringValue (response.["data"])) "fresh Engine loads the Flow owner"
        assertAllPassed 1 (dispatch reloaded "test" [ "word", jstr "migration.increment" ] |> expectOk "run migrated Flow test after reload")
        let example = dispatch reloaded "example" [ "word", jstr "migration.increment"; "caseName", jstr "basic" ] |> expectOk "run migrated Flow example after reload"
        check (boolValue (example.["data"].["results"].[0].["passed"])) "migrated Flow example passes after reload"
        equal flowExample (stringValue (example.["data"].["results"].[0].["source"])) "migrated Flow example source survives reload"

    let private testReplacementCasAndRollback root =
        let project = Path.Combine(root, "replacement-cas")
        let engine = Runtime.Engine(project, Set.empty, "2030-01-02T03:04:05Z")
        defineFlow engine flowWordSource [ flowTestSource ] [ flowExampleSource ] []
        |> expectOk "define initial replacement target"
        |> ignore
        let originalId = getWordId engine "durable.increment"
        commit engine "commit" "durable.increment" [] |> expectOk "commit initial replacement target" |> ignore

        let store = Storage.create project
        let before = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        let originalManifestHash = before.ManifestHash

        let compatibleReplacement =
            "word durable.increment(value: Int) -> Int {\n"
            + "    effects none\n"
            + "    add(1, value)\n"
            + "}"
        defineFlow engine compatibleReplacement [] [] [ "replace", jbool true; "expectedRevision", jint 1 ]
        |> expectOk "stage same-ID Flow replacement"
        |> ignore
        commit engine "replace-word" "durable.increment" []
        |> expectOk "commit same-ID Flow replacement"
        |> ignore
        equal originalId (getWordId engine "durable.increment") "Flow replacement keeps the stable word identity"
        let replaced = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        let replacedManifest = replaced.Manifest.Value
        let revisions = replacedManifest.Revisions |> List.filter (fun item -> item.WordId = originalId) |> List.sortBy _.Revision
        equal [ 1; 2 ] (revisions |> List.map _.Revision) "Flow replacement appends a second durable revision"
        check originalManifestHash.IsSome "initial replacement target was durably published"
        equal compatibleReplacement (Storage.readSource store (List.item 1 revisions).Definition |> Result.defaultWith (fun problem -> failwith problem.Message)) "replacement stores its authored Flow source"
        equal ProjectWord (List.item 1 revisions).Maturity "same-ID replacement preserves project maturity"
        let afterValue = evalFlow engine "durable::increment(9)" |> expectOk "evaluate compatible replacement"
        equal "10" (stringValue (afterValue.["data"].["stack"].[0])) "same-ID replacement updates the executable snapshot"
        let committedHistory = dispatch engine "history" [ "word", jstr "durable.increment" ] |> expectOk "read history after Flow replacement"
        equal flowWordSource (stringValue (committedHistory.["data"].[0].["source"])) "replacement retains the original authored revision"

        let failedReplacement =
            "word durable.increment(value: Int) -> Int {\n"
            + "    effects none\n"
            + "    add(value, 2)\n"
            + "}"
        let wrongTest =
            "test durable.increment/wrong_expectation {\n"
            + "    durable::increment(41)\n"
            + "    => 42\n"
            + "}"
        defineFlow engine failedReplacement [ wrongTest ] [] [ "replace", jbool true; "expectedRevision", jint 2 ]
        |> expectOk "stage replacement whose new attached test fails"
        |> ignore
        let failingCommit = commit engine "replace-word" "durable.increment" []
        check (not (succeeded failingCommit)) "a replacement with a failing authored case is rejected"
        let afterFailure = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        equal replaced.ManifestHash afterFailure.ManifestHash "failed replacement leaves the manifest authority unchanged"
        let freshAfterFailure = Runtime.Engine(project, Set.empty, "2030-01-02T03:04:05Z")
        equal "10" (evalFlow freshAfterFailure "durable::increment(9)" |> expectOk "evaluate durable word after rejected replacement" |> fun response -> stringValue (response.["data"].["stack"].[0])) "failed replacement is not reachable after a fresh Engine reload"
        dispatch engine "discard" [ "word", jstr "durable.increment" ] |> expectOk "discard failed replacement candidate" |> ignore
        equal "10" (evalFlow engine "durable::increment(9)" |> expectOk "evaluate after discarding rejected replacement" |> fun response -> stringValue (response.["data"].["stack"].[0])) "discard restores the live pre-replacement body"

        let staleRemove =
            let removal = JsonObject()
            removal.["kind"] <- jstr "example"
            removal.["caseName"] <- jstr "one"
            removal.["expectedSourceHash"] <- jstr (String.replicate 64 "0")
            let removals = JsonArray()
            removals.Add removal
            removals :> JsonNode
        let staleArgs =
            [ "frontend", jstr "flow"
              "source", jstr compatibleReplacement
              "replace", jbool true
              "expectedRevision", jint 2
              "removeAttachments", staleRemove ]
        let stale = dispatch engine "define" staleArgs
        expectError "FLOW_ATTACHMENT_STALE_SOURCE" stale |> ignore
        let afterStale = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        equal replaced.ManifestHash afterStale.ManifestHash "stale attachment removal CAS does not publish"

        let updatedBasicTest =
            "test durable.increment/basic {\n"
            + "    durable::increment(40)\n"
            + "    => value durable::increment(40)\n"
            + "}"
        let extraTest =
            "test durable.increment/extra {\n"
            + "    durable::increment(5)\n"
            + "    => value durable::increment(5)\n"
            + "}"
        defineFlow engine compatibleReplacement [ updatedBasicTest; extraTest ] []
            [ "replace", jbool true
              "expectedRevision", jint 2 ]
        |> expectOk "replace an attached Flow test and add another case"
        |> ignore
        commit engine "replace-word" "durable.increment" [] |> expectOk "publish Flow test replacement and addition" |> ignore
        let afterTestEdit = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        let editedRevision = afterTestEdit.Manifest.Value.Revisions |> List.find (fun item -> item.WordId = originalId && item.Revision = 3)
        let editedTestSources = editedRevision.Tests |> List.map (fun reference -> Storage.readSource store reference |> Result.defaultWith (fun problem -> failwith problem.Message)) |> Set.ofList
        equal (Set.ofList [ updatedBasicTest; extraTest ]) editedTestSources "attachment edit replaces one exact case and adds another"
        equal 1 editedRevision.Examples.Length "omitted examples retain the original example source"
        assertAllPassed 2 (dispatch engine "test" [ "word", jstr "durable.increment" ] |> expectOk "run retained and updated Flow tests")

        let currentRevision = editedRevision
        let exampleHash = currentRevision.Examples.Head.Hash
        let removeExample = JsonObject()
        removeExample.["kind"] <- jstr "example"
        removeExample.["caseName"] <- jstr "one"
        removeExample.["expectedSourceHash"] <- jstr exampleHash
        let validRemovals = JsonArray()
        validRemovals.Add removeExample
        defineFlow engine compatibleReplacement [] []
            [ "replace", jbool true
              "expectedRevision", jint 3
              "removeAttachments", (validRemovals :> JsonNode) ]
        |> expectOk "stage attachment removal with its exact current source hash"
        |> ignore
        commit engine "replace-word" "durable.increment" []
        |> expectOk "publish source-bound example removal"
        |> ignore
        let afterRemoval = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        let removalRevision = afterRemoval.Manifest.Value.Revisions |> List.find (fun item -> item.WordId = originalId && item.Revision = 4)
        equal [] removalRevision.Examples "successful CAS removes the requested authored example"
        equal [ "basic"; "extra" ] (jsonArrayStrings (dispatch engine "tests" [ "word", jstr "durable.increment" ] |> expectOk "test remains after example removal" |> fun response -> response.["data"])) "unmentioned attachments remain intact"

    let private testFlowAttachmentOnlyDocuments root =
        let project = Path.Combine(root, "flow-attachment-only-document")
        let engine = Runtime.Engine(project, Set.empty, "2032-03-04T05:06:07Z")
        let store = Storage.create project
        let recordSource =
            "record CaseItem {\n"
            + "    field text: String;\n"
            + "}"
        let describeSource =
            "word caseitem.describe(value: CaseItem) -> String {\n"
            + "    effects none\n"
            + "    value.text()\n"
            + "}"
        let forwardSource =
            "word caseitem.forward(value: CaseItem) -> String {\n"
            + "    effects none\n"
            + "    caseitem::describe(value)\n"
            + "}"
        let basicTest =
            "test caseitem.describe/basic {\n"
            + "    caseitem::describe(caseItem::new(text = \"original\"))\n"
            + "    => \"original\"\n"
            + "}"
        let forwardTest =
            "test caseitem.forward/basic {\n"
            + "    caseitem::forward(caseItem::new(text = \"caller\"))\n"
            + "    => \"caller\"\n"
            + "}"
        let basicExample =
            "example caseitem.describe/basic {\n"
            + "    caseitem::describe(caseItem::new(text = \"example\"))\n"
            + "    => \"example\"\n"
            + "}"
        let projectDocument =
            [ recordSource; describeSource; forwardSource; basicTest; forwardTest; basicExample ]
            |> String.concat "\n\n"
        defineFlowProject engine projectDocument []
        |> expectOk "define Flow owner and caller with generated record accessors"
        |> ignore
        let ownerId = getWordId engine "caseitem.describe"
        commit engine "commit" "caseitem.describe" [] |> expectOk "commit typed Flow attachment owner" |> ignore
        commit engine "commit" "caseitem.forward" [] |> expectOk "commit retained Flow caller" |> ignore

        let originalSource = stringValue (dispatch engine "source" [ "word", jstr "caseitem.describe" ] |> expectOk "read authored owner source before case-only edit" |> fun response -> response.["data"])
        let before = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        let beforeManifest = before.Manifest |> Option.defaultWith (fun () -> failwith "Flow owner was not committed")
        let originalRevision = beforeManifest.Revisions |> List.find (fun item -> item.WordId = ownerId && item.Revision = 1)
        let originalTestReference = originalRevision.Tests |> List.exactlyOne
        let originalExampleReference = originalRevision.Examples |> List.exactlyOne

        let extraTest =
            "test caseitem.describe/extra {\n"
            + "    caseitem::describe(caseItem::new(text = \"extra\"))\n"
            + "    => \"extra\"\n"
            + "}"
        let extraExample =
            "example caseitem.describe/extra {\n"
            + "    caseitem::describe(caseItem::new(text = \"extra example\"))\n"
            + "    => \"extra example\"\n"
            + "}"
        let addedDocument = String.concat "\n\n" [ extraTest; extraExample ]
        let staleRemoval =
            let row = JsonObject()
            row["kind"] <- jstr "example"
            row["caseName"] <- jstr "basic"
            row["expectedSourceHash"] <- jstr (String.replicate 64 "0")
            let rows = JsonArray()
            rows.Add row
            rows :> JsonNode
        let staleRemovalResponse =
            dispatch engine "define"
                [ "source", jstr addedDocument
                  "replace", jbool true
                  "expectedRevision", jint 1
                  "removeAttachments", staleRemoval ]
        expectError "FLOW_ATTACHMENT_STALE_SOURCE" staleRemovalResponse |> ignore
        equal before.ManifestHash (Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)).ManifestHash "stale source-hash removal through an attachment-only document does not publish"
        expectError "FLOW_RUNTIME_INVALID_ARGUMENT" (dispatch engine "define" [ "source", jstr addedDocument; "temporary", jbool true ])
        |> ignore
        equal before.ManifestHash (Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)).ManifestHash "a persistent owner cannot be made temporary through an attachment-only edit"
        let ambiguous = dispatch engine "define" [ "source", jstr addedDocument; "tests", strings [ extraTest ] ]
        expectError "FLOW_PROJECT_REQUEST_SHAPE" ambiguous |> ignore
        equal before.ManifestHash (Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)).ManifestHash "ambiguous external attachments do not change durable authority"
        equal originalSource (stringValue (dispatch engine "source" [ "word", jstr "caseitem.describe" ] |> expectOk "owner source remains unchanged after ambiguous attachments" |> fun response -> response.["data"])) "ambiguous external attachments do not replace the owner body"

        let staged = dispatch engine "define" [ "source", jstr addedDocument ] |> expectOk "add cases with an omitted frontend and no repeated word definition"
        equal "flow" (stringValue (staged.["data"].["frontend"])) "attachment-only source selects Flow by default"
        equal ownerId (stringValue (staged.["data"].["id"])) "case-only edit retains the existing owner identity"
        equal 2 ((staged.["data"].["revision"]).GetValue<int>()) "case-only addition stages the next immutable owner revision"
        equal [ "basic"; "extra" ] (jsonArrayStrings staged.["data"].["tests"]) "add-only case document preserves existing tests"
        equal [ "basic"; "extra" ] (jsonArrayStrings staged.["data"].["examples"]) "add-only case document preserves existing examples"
        equal originalSource (stringValue (dispatch engine "source" [ "word", jstr "caseitem.describe" ] |> expectOk "read unchanged authored body after case-only edit" |> fun response -> response.["data"])) "case-only edit preserves exact word source bytes"
        equal ownerId (getWordId engine "caseitem.describe") "case-only candidate revision retains stable owner identity"
        assertAllPassed 2 (dispatch engine "test" [ "word", jstr "caseitem.describe" ] |> expectOk "run original and new generated-record tests before publication")
        for caseName in [ "basic"; "extra" ] do
            let result = dispatch engine "example" [ "word", jstr "caseitem.describe"; "caseName", jstr caseName ] |> expectOk $"run {caseName} generated-record example"
            check (boolValue (result.["data"].["results"].[0].["passed"])) $"case-only example {caseName} passes"

        let stagedInventory = dispatch engine "words" [] |> expectOk "inspect candidate status after Flow case-only edit"
        equal "candidate" (stringValue ((findWord stagedInventory "caseitem.describe").["status"])) "persistent owner becomes a candidate while its replacement is staged"
        equal "project" (stringValue ((findWord stagedInventory "caseitem.describe").["maturity"])) "case-only revision preserves project maturity"
        let mixedOwners =
            String.concat "\n\n"
                [ "test caseitem.describe/mixed {\n    caseitem::describe(caseItem::new(text = \"a\"))\n    => \"a\"\n}"
                  "example caseitem.forward/mixed {\n    caseitem::forward(caseItem::new(text = \"b\"))\n    => \"b\"\n}" ]
        expectError "FLOW_ATTACHMENT_OWNER_MISMATCH" (dispatch engine "define" [ "source", jstr mixedOwners ]) |> ignore

        let stackOwner =
            "word caseitem.stack_label : Int -> Int\n"
            + "    effects none\n"
            + "    1\n"
            + "    add\n"
            + "end\n\n"
            + "test caseitem.stack_label/basic\n"
            + "    1 caseitem.stack_label\n"
            + "    expect 2\n"
            + "end"
        defineStack engine stackOwner [] |> expectOk "define a Stack owner beside Flow-authored words" |> ignore
        let stackOwnerCase =
            "test caseitem.stack_label/flow_case {\n"
            + "    caseitem::stack_label(1)\n"
            + "    => 1\n"
            + "}"
        expectError "FLOW_ATTACHMENT_OWNER_NOT_FLOW_WORD" (dispatch engine "define" [ "source", jstr stackOwnerCase ]) |> ignore
        let generatedOwnerCase =
            "test caseItem.text/flow_case {\n"
            + "    caseItem::text(caseItem::new(text = \"generated\"))\n"
            + "    => \"generated\"\n"
            + "}"
        expectError "FLOW_ATTACHMENT_OWNER_NOT_FLOW_WORD" (dispatch engine "define" [ "source", jstr generatedOwnerCase ]) |> ignore
        let committed = commit engine "replace-word" "caseitem.describe" [] |> expectOk "publish case-only edit through owner and caller gates"
        check (jsonArrayStrings committed.["data"] |> List.contains "caseitem.forward/basic") "persistent caller tests run during case-only publication"
        let after = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        let afterManifest = after.Manifest |> Option.defaultWith (fun () -> failwith "case-only revision did not publish")
        let revised = afterManifest.Revisions |> List.find (fun item -> item.WordId = ownerId && item.Revision = 2)
        equal originalRevision.Definition revised.Definition "case-only publication keeps the exact immutable word source reference"
        check (List.contains originalTestReference revised.Tests) "case-only publication retains the original test source reference"
        check (List.contains originalExampleReference revised.Examples) "case-only publication retains the original example source reference"
        let revisedTestSources = revised.Tests |> List.map (fun reference -> Storage.readSource store reference |> Result.defaultWith (fun problem -> failwith problem.Message)) |> Set.ofList
        equal (Set.ofList [ basicTest; extraTest ]) revisedTestSources "case-only publication preserves original and newly added test source bytes"
        let revisedExampleSources = revised.Examples |> List.map (fun reference -> Storage.readSource store reference |> Result.defaultWith (fun problem -> failwith problem.Message)) |> Set.ofList
        equal (Set.ofList [ basicExample; extraExample ]) revisedExampleSources "case-only publication preserves original and newly added example source bytes"

        let collisionSource =
            "test caseitem.describe/extra {\n"
            + "    caseitem::describe(caseItem::new(text = \"replacement\"))\n"
            + "    => \"replacement\"\n"
            + "}"
        let collision = dispatch engine "define" [ "source", jstr collisionSource ]
        expectError "FLOW_ATTACHMENT_CAS_REQUIRED" collision |> ignore
        equal after.ManifestHash (Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)).ManifestHash "case-name collision without explicit CAS does not publish"
        equal [ "basic"; "extra" ] (jsonArrayStrings (dispatch engine "tests" [ "word", jstr "caseitem.describe" ] |> expectOk "inspect cases after rejected implicit replacement" |> fun response -> response.["data"])) "case-name collision does not activate an implicit replacement"

        let stale = dispatch engine "define" [ "source", jstr collisionSource; "replace", jbool true; "expectedRevision", jint 1 ]
        expectError "FLOW_BATCH_STALE_REVISION" stale |> ignore
        equal after.ManifestHash (Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)).ManifestHash "stale case replacement CAS does not publish"
        equal ownerId (getWordId engine "caseitem.describe") "stale case replacement does not change owner identity"
        let replaced = dispatch engine "define" [ "source", jstr collisionSource; "replace", jbool true; "expectedRevision", jint 2 ] |> expectOk "replace one case with an explicit current owner revision"
        equal 3 ((replaced.["data"].["revision"]).GetValue<int>()) "explicit case replacement advances the owner revision"
        equal originalSource (stringValue (dispatch engine "source" [ "word", jstr "caseitem.describe" ] |> expectOk "read body after explicit case replacement" |> fun response -> response.["data"])) "explicit case replacement still preserves word body bytes"
        commit engine "replace-word" "caseitem.describe" [] |> expectOk "publish explicit case replacement with caller regression tests" |> ignore
        let afterReplacement = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        let replacementRevision = afterReplacement.Manifest.Value.Revisions |> List.find (fun item -> item.WordId = ownerId && item.Revision = 3)
        equal originalRevision.Definition replacementRevision.Definition "case replacement preserves the same word source reference"
        assertAllPassed 2 (dispatch engine "test" [ "word", jstr "caseitem.describe" ] |> expectOk "run retained and replaced owner cases")
        assertAllPassed 1 (dispatch engine "test" [ "word", jstr "caseitem.forward" ] |> expectOk "run persistent Flow caller after case replacement")

        let beforeTask = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        let beforeTaskTests = dispatch engine "tests" [ "word", jstr "caseitem.describe" ] |> expectOk "capture owner cases before task abort"
        dispatch engine "task.begin" [ "goal", jstr "add and roll back a Flow attachment-only case" ] |> expectOk "begin task for Flow case-only rollback" |> ignore
        let taskTest =
            "test caseitem.describe/task_only {\n"
            + "    caseitem::describe(caseItem::new(text = \"temporary\"))\n"
            + "    => \"temporary\"\n"
            + "}"
        dispatch engine "define" [ "source", jstr taskTest ] |> expectOk "stage a task-local attachment-only Flow document" |> ignore
        commit engine "replace-word" "caseitem.describe" [] |> expectOk "publish the case-only revision inside the task" |> ignore
        dispatch engine "task.abort" [] |> expectOk "abort the committed attachment-only change" |> ignore
        let afterTask = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        equal beforeTask.ManifestHash afterTask.ManifestHash "task abort restores exact authority after a case-only revision"
        equal ((beforeTaskTests.["data"]).ToJsonString()) (((dispatch engine "tests" [ "word", jstr "caseitem.describe" ] |> expectOk "read restored cases after task abort").["data"]).ToJsonString()) "task abort restores exact pre-edit test sources and names"
        equal originalSource (stringValue (dispatch engine "source" [ "word", jstr "caseitem.describe" ] |> expectOk "read restored body after task abort" |> fun response -> response.["data"])) "task abort restores the exact Flow definition bytes"
        equal ownerId (getWordId (Runtime.Engine(project, Set.empty, "2032-03-04T05:06:07Z")) "caseitem.describe") "fresh Engine after abort reloads the same owner identity"

        let candidateProject = Path.Combine(root, "flow-attachment-only-candidate-gate")
        let candidateEngine = Runtime.Engine(candidateProject, Set.empty)
        let candidateWord =
            "word candidate.bump(value: Int) -> Int {\n"
            + "    effects none\n"
            + "    add(value, 1)\n"
            + "}"
        let candidateTest =
            "test candidate.bump/basic {\n"
            + "    candidate::bump(1)\n"
            + "    => 2\n"
            + "}"
        defineFlow candidateEngine candidateWord [ candidateTest ] [] [] |> expectOk "define candidate for attachment test gate" |> ignore
        let failingCandidateTest =
            "test candidate.bump/basic {\n"
            + "    candidate::bump(1)\n"
            + "    => 99\n"
            + "}"
        dispatch candidateEngine "define" [ "source", jstr failingCandidateTest; "replace", jbool true; "expectedRevision", jint 1 ]
        |> expectOk "stage a failing case-only candidate edit"
        |> ignore
        expectError "COMMIT_TESTS_FAILED" (commit candidateEngine "commit" "candidate.bump" []) |> ignore
        check ((Storage.load (Storage.create candidateProject) |> Result.defaultWith (fun problem -> failwith problem.Message)).Manifest.IsNone) "a failing candidate case-only edit cannot create durable authority"

        let libraryProject = Path.Combine(root, "flow-attachment-only-library-gate")
        let libraryEngine = Runtime.Engine(libraryProject, Set.empty)
        let libraryWord =
            "word library.case_only(value: Bool) -> Int {\n"
            + "    effects none\n"
            + "    if value { 1 } else { 2 }\n"
            + "}"
        let trueTest =
            "test library.case_only/true {\n"
            + "    library::case_only(true)\n"
            + "    => 1\n"
            + "}"
        let falseTest =
            "test library.case_only/false {\n"
            + "    library::case_only(false)\n"
            + "    => 2\n"
            + "}"
        defineFlow libraryEngine libraryWord [ trueTest; falseTest ] [] [] |> expectOk "define library branch coverage baseline" |> ignore
        commit libraryEngine "commit" "library.case_only" [ "library", jbool true ]
        |> expectOk "commit library after both real branches are exercised"
        |> ignore
        let libraryBefore = Storage.load (Storage.create libraryProject) |> Result.defaultWith (fun problem -> failwith problem.Message)
        let libraryFalseReplacement =
            "test library.case_only/false {\n"
            + "    library::case_only(true)\n"
            + "    => 1\n"
            + "}"
        dispatch libraryEngine "define" [ "source", jstr libraryFalseReplacement; "replace", jbool true; "expectedRevision", jint 1 ]
        |> expectOk "stage a passing library case edit that omits one actual branch"
        |> ignore
        assertAllPassed 2 (dispatch libraryEngine "test" [ "word", jstr "library.case_only" ] |> expectOk "verify all library case expectations still pass")
        expectError "LIBRARY_COVERAGE_INCOMPLETE" (commit libraryEngine "replace-word" "library.case_only" []) |> ignore
        equal libraryBefore.ManifestHash (Storage.load (Storage.create libraryProject) |> Result.defaultWith (fun problem -> failwith problem.Message)).ManifestHash "library branch coverage gate rejects case-only publication without changing authority"
        dispatch libraryEngine "discard" [ "word", jstr "library.case_only" ] |> expectOk "discard library case revision rejected by the actual-coverage gate" |> ignore

    let private testFlowAttachmentOnlyPreservesTemporaryLifetime root =
        let project = Path.Combine(root, "flow-attachment-only-temporary")
        let engine = Runtime.Engine(project, Set.empty)
        let temporaryWord =
            "word temporary.echo(value: Int) -> Int {\n"
            + "    effects none\n"
            + "    value\n"
            + "}"
        let firstTest =
            "test temporary.echo/first {\n"
            + "    temporary::echo(1)\n"
            + "    => 1\n"
            + "}"
        defineFlow engine temporaryWord [ firstTest ] [] [ "temporary", jbool true ]
        |> expectOk "define a temporary Flow attachment owner"
        |> ignore
        let firstId = getWordId engine "temporary.echo"
        let secondTest =
            "test temporary.echo/second {\n"
            + "    temporary::echo(2)\n"
            + "    => 2\n"
            + "}"
        dispatch engine "define" [ "source", jstr secondTest ]
        |> expectOk "add a case without changing a temporary owner's lifetime"
        |> ignore
        equal firstId (getWordId engine "temporary.echo") "temporary case-only edit preserves the owner identity"
        let temporaryInventory = dispatch engine "words" [] |> expectOk "inspect temporary status after case-only edit"
        equal "temporary" (stringValue ((findWord temporaryInventory "temporary.echo").["status"])) "case-only edit keeps a temporary owner temporary"
        assertAllPassed 2 (dispatch engine "test" [ "word", jstr "temporary.echo" ] |> expectOk "run both temporary Flow cases")
        dispatch engine "discard" [ "word", jstr "temporary.echo" ] |> expectOk "discard temporary owner after case-only edit" |> ignore
        expectError "NAME_UNKNOWN_WORD" (dispatch engine "describe" [ "word", jstr "temporary.echo" ]) |> ignore

        dispatch engine "task.begin" [ "goal", jstr "clean task-local Flow case-only words" ] |> expectOk "begin a task before defining temporary Flow owner" |> ignore
        defineFlow engine temporaryWord [ firstTest ] [] [ "temporary", jbool true ]
        |> expectOk "define a task-local temporary Flow owner"
        |> ignore
        dispatch engine "define" [ "source", jstr secondTest ]
        |> expectOk "add a task-local case without promoting its owner"
        |> ignore
        equal "temporary" (stringValue ((findWord (dispatch engine "words" [] |> expectOk "inspect task-local temporary owner") "temporary.echo").["status"])) "task-local case-only edit preserves temporary status"
        dispatch engine "task.commit" [] |> expectOk "task commit cleans up a temporary owner edited through a case-only document" |> ignore
        expectError "NAME_UNKNOWN_WORD" (dispatch engine "describe" [ "word", jstr "temporary.echo" ]) |> ignore
        check ((Storage.load (Storage.create project) |> Result.defaultWith (fun problem -> failwith problem.Message)).Manifest.IsNone) "temporary case-only edits leave no durable project authority"

    let private testTemporaryPromotionAndTaskAbort root =
        let project = Path.Combine(root, "temporary-promotion")
        let engine = Runtime.Engine(project, Set.empty, "2030-01-02T03:04:05Z")
        let discardedSource =
            "word temporary.discarded(value: Int) -> Int {\n    effects none\n    add(value, 1)\n}"
        let discardedTest =
            "test temporary.discarded/basic {\n    temporary::discarded(1)\n    => value temporary::discarded(1)\n}"
        defineFlow engine discardedSource [ discardedTest ] [] [ "temporary", jbool true ]
        |> expectOk "define temporary Flow word"
        |> ignore
        let discardedId = getWordId engine "temporary.discarded"
        let emptyStore = Storage.load (Storage.create project) |> Result.defaultWith (fun problem -> failwith problem.Message)
        check emptyStore.Manifest.IsNone "temporary Flow definition does not enter durable Storage"
        dispatch engine "discard" [ "word", jstr "temporary.discarded" ] |> expectOk "discard temporary Flow word" |> ignore
        let missingAfterDiscard = dispatch engine "describe" [ "word", jstr "temporary.discarded" ]
        expectError "NAME_UNKNOWN_WORD" missingAfterDiscard |> ignore

        let promotedSource =
            "word temporary.promoted(value: Int) -> Int {\n    effects none\n    add(value, 1)\n}"
        let promotedTest =
            "test temporary.promoted/basic {\n    temporary::promoted(1)\n    => value temporary::promoted(1)\n}"
        defineFlow engine promotedSource [ promotedTest ] [] [ "temporary", jbool true ]
        |> expectOk "define Flow word for promotion"
        |> ignore
        let promotedId = getWordId engine "temporary.promoted"
        dispatch engine "promote" [ "word", jstr "temporary.promoted" ] |> expectOk "promote temporary Flow word" |> ignore
        commit engine "commit" "temporary.promoted" [] |> expectOk "commit promoted Flow word" |> ignore
        equal promotedId (getWordId engine "temporary.promoted") "promotion preserves the in-memory Flow word ID"
        let promotedManifest = (Storage.load (Storage.create project) |> Result.defaultWith (fun problem -> failwith problem.Message)).Manifest.Value
        let promotedRevision = promotedManifest.Revisions |> List.find (fun item -> item.WordId = promotedId)
        equal { Frontend = SourceFrontend.Flow; Version = 1 } promotedRevision.SourceFormat "promoted definition persists as Flow/1"
        check (discardedId <> promotedId) "distinct temporary definitions receive distinct IDs"

        let cleanupProject = Path.Combine(root, "task-commit-flow-temporaries")
        let cleanupEngine = Runtime.Engine(cleanupProject, Set.empty, "2030-01-02T03:04:05Z")
        let priorSource =
            "word temporary.prior(value: Int) -> Int {\n    effects none\n    add(value, 1)\n}"
        let priorTest =
            "test temporary.prior/basic {\n    temporary::prior(1)\n    => value temporary::prior(1)\n}"
        defineFlow cleanupEngine priorSource [ priorTest ] [] [ "temporary", jbool true ]
        |> expectOk "define Flow temporary before a task"
        |> ignore
        let priorId = getWordId cleanupEngine "temporary.prior"
        let priorDescribe = dispatch cleanupEngine "describe" [ "word", jstr "temporary.prior" ] |> expectOk "capture prior temporary metadata"
        let priorTests = dispatch cleanupEngine "tests" [ "word", jstr "temporary.prior" ] |> expectOk "capture prior temporary cases"
        let priorExamples = dispatch cleanupEngine "examples" [ "word", jstr "temporary.prior" ] |> expectOk "capture prior temporary examples"
        dispatch cleanupEngine "task.begin" [ "goal", jstr "clear only task-local Flow words" ]
        |> expectOk "begin Flow temporary cleanup task"
        |> ignore
        let taskOnlySource =
            "word temporary.task_only(value: Int) -> Int {\n    effects none\n    add(value, 2)\n}"
        let taskOnlyTest =
            "test temporary.task_only/basic {\n    temporary::task_only(1)\n    => value temporary::task_only(1)\n}"
        defineFlow cleanupEngine taskOnlySource [ taskOnlyTest ] [] [ "temporary", jbool true ]
        |> expectOk "define task-local Flow temporary"
        |> ignore
        let taskOnlyId = getWordId cleanupEngine "temporary.task_only"
        dispatch cleanupEngine "task.commit" [] |> expectOk "commit Flow temporary cleanup task" |> ignore

        equal priorId (getWordId cleanupEngine "temporary.prior") "task.commit preserves the exact pre-task temporary identity"
        equal ((priorDescribe.["data"]).ToJsonString()) (((dispatch cleanupEngine "describe" [ "word", jstr "temporary.prior" ] |> expectOk "read prior metadata after task.commit").["data"]).ToJsonString()) "task.commit preserves pre-task Flow metadata exactly"
        equal (stringValue ((dispatch cleanupEngine "source" [ "word", jstr "temporary.prior" ] |> expectOk "read prior Flow source after task.commit").["data"])) priorSource "task.commit preserves pre-task Flow source bytes"
        equal ((priorTests.["data"]).ToJsonString()) (((dispatch cleanupEngine "tests" [ "word", jstr "temporary.prior" ] |> expectOk "read prior Flow cases after task.commit").["data"]).ToJsonString()) "task.commit preserves pre-task Flow test metadata exactly"
        equal ((priorExamples.["data"]).ToJsonString()) (((dispatch cleanupEngine "examples" [ "word", jstr "temporary.prior" ] |> expectOk "read prior Flow examples after task.commit").["data"]).ToJsonString()) "task.commit preserves pre-task Flow example metadata exactly"
        equal "2" (evalFlow cleanupEngine "temporary::prior(1)" |> expectOk "execute pre-task Flow temporary after task.commit" |> fun response -> stringValue (response.["data"].["stack"].[0])) "pre-task Flow temporary remains executable"
        check (taskOnlyId <> priorId) "task-local and pre-task temporaries have distinct identities"
        expectError "NAME_UNKNOWN_WORD" (dispatch cleanupEngine "source" [ "word", jstr "temporary.task_only" ]) |> ignore
        let cleanupStore = Storage.create cleanupProject
        let afterTaskCommit = Storage.load cleanupStore |> Result.defaultWith (fun problem -> failwith problem.Message)
        check afterTaskCommit.Manifest.IsNone "task.commit does not persist session-only Flow words"
        let objectRoot = Path.Combine(cleanupProject, ".agentlang", "store", "objects")
        let objectCount = if Directory.Exists objectRoot then Directory.GetFiles(objectRoot, "*", SearchOption.AllDirectories).Length else 0
        equal 0 objectCount "task.commit leaves no durable orphan source objects for Flow temporaries"
        let freshCleanupEngine = Runtime.Engine(cleanupProject, Set.empty, "2030-01-02T03:04:05Z")
        expectError "NAME_UNKNOWN_WORD" (dispatch freshCleanupEngine "source" [ "word", jstr "temporary.prior" ]) |> ignore
        expectError "NAME_UNKNOWN_WORD" (dispatch freshCleanupEngine "source" [ "word", jstr "temporary.task_only" ]) |> ignore

        let emptyTaskProject = Path.Combine(root, "task-abort-empty-authority")
        let emptyTaskEngine = Runtime.Engine(emptyTaskProject, Set.empty, "2030-01-02T03:04:05Z")
        let emptyTaskStore = Storage.create emptyTaskProject
        let emptyBefore = Storage.load emptyTaskStore |> Result.defaultWith (fun problem -> failwith problem.Message)
        check emptyBefore.Manifest.IsNone "empty task rollback begins without a manifest"
        expectError "STORAGE_SNAPSHOT_REQUIRES_MANIFEST" (dispatch emptyTaskEngine "snapshot.save" [ "name", jstr "not-allowed" ]) |> ignore
        let emptyBaselineWords = dispatch emptyTaskEngine "words" [] |> expectOk "capture empty-authority builtin inventory"
        let emptyBaselineInventory = (emptyBaselineWords.["data"].["words"]).ToJsonString()
        let emptyExportPath = Path.Combine(emptyTaskProject, "dictionary.agent")
        let emptyExport = if File.Exists emptyExportPath then Some(File.ReadAllBytes emptyExportPath) else None
        dispatch emptyTaskEngine "task.begin" [ "goal", jstr "commit and roll back from empty authority" ]
        |> expectOk "begin task from empty storage authority"
        |> ignore
        let committedDuringTask =
            "word task.empty_abort(value: Int) -> Int {\n"
            + "    effects none\n"
            + "    add(value, 1)\n"
            + "}"
        let committedDuringTaskTest =
            "test task.empty_abort/basic {\n"
            + "    task::empty_abort(1)\n"
            + "    => value task::empty_abort(1)\n"
            + "}"
        defineFlow emptyTaskEngine committedDuringTask [ committedDuringTaskTest ] [] []
        |> expectOk "define Flow word inside empty-authority task"
        |> ignore
        let committedTaskId = getWordId emptyTaskEngine "task.empty_abort"
        commit emptyTaskEngine "commit" "task.empty_abort" [] |> expectOk "commit Flow into empty-authority task" |> ignore
        let duringEmptyTask = Storage.load emptyTaskStore |> Result.defaultWith (fun problem -> failwith problem.Message)
        check duringEmptyTask.Manifest.IsSome "Flow commit creates the task-local manifest authority"
        equal "2" (evalFlow emptyTaskEngine "task::empty_abort(1)" |> expectOk "evaluate committed Flow in empty-authority task" |> fun response -> stringValue (response.["data"].["stack"].[0])) "Flow word is active before empty-authority rollback"
        dispatch emptyTaskEngine "task.abort" [] |> expectOk "abort Flow commit to empty authority" |> ignore
        let restoredEmpty = Storage.load emptyTaskStore |> Result.defaultWith (fun problem -> failwith problem.Message)
        check restoredEmpty.Manifest.IsNone "task.abort restores the original EmptyAuthority"
        let restoredInventory = dispatch emptyTaskEngine "words" [] |> expectOk "inspect in-memory words after empty-authority abort"
        equal emptyBaselineInventory ((restoredInventory.["data"].["words"]).ToJsonString()) "task.abort restores the exact pre-task names, IDs, and word metadata"
        expectError "NAME_UNKNOWN_WORD" (dispatch emptyTaskEngine "source" [ "word", jstr "task.empty_abort" ]) |> ignore
        match emptyExport with
        | Some expected ->
            check (File.Exists emptyExportPath) "empty-authority abort restores its original export"
            check (expected.AsSpan().SequenceEqual(File.ReadAllBytes(emptyExportPath).AsSpan())) "empty-authority abort restores exact export bytes"
        | None -> check (not (File.Exists emptyExportPath)) "empty-authority abort restores absence of a prior export"
        check (not (String.IsNullOrWhiteSpace committedTaskId)) "the Flow owner had a concrete ID before rollback"
        let freshEmptyTaskEngine = Runtime.Engine(emptyTaskProject, Set.empty, "2030-01-02T03:04:05Z")
        let freshEmptyInventory = dispatch freshEmptyTaskEngine "words" [] |> expectOk "fresh-load empty task authority"
        equal emptyBaselineInventory ((freshEmptyInventory.["data"].["words"]).ToJsonString()) "fresh Engine exposes the same restored builtin word inventory and IDs"
        expectError "NAME_UNKNOWN_WORD" (dispatch freshEmptyTaskEngine "source" [ "word", jstr "task.empty_abort" ]) |> ignore

        let rollbackProject = Path.Combine(root, "task-abort-flow")
        let rollbackEngine = Runtime.Engine(rollbackProject, Set.empty, "2030-01-02T03:04:05Z")
        defineFlow rollbackEngine flowWordSource [ flowTestSource ] [ flowExampleSource ] []
        |> expectOk "define Flow baseline for task rollback"
        |> ignore
        commit rollbackEngine "commit" "durable.increment" [] |> expectOk "commit Flow baseline for task rollback" |> ignore
        let rollbackStore = Storage.create rollbackProject
        let before = Storage.load rollbackStore |> Result.defaultWith (fun problem -> failwith problem.Message)
        let beforeExport = File.ReadAllBytes(Path.Combine(rollbackProject, "dictionary.agent"))
        let task = dispatch rollbackEngine "task.begin" [ "goal", jstr "replace a Flow revision and roll it back" ] |> expectOk "begin Flow rollback task"
        check (not (isNull task.["data"])) "task starts with a committed Flow snapshot"
        let replacement = "word durable.increment(value: Int) -> Int {\n    effects none\n    add(value, 2)\n}"
        defineFlow rollbackEngine replacement [] [] [ "replace", jbool true; "expectedRevision", jint 1 ]
        |> expectOk "stage Flow replacement inside task"
        |> ignore
        commit rollbackEngine "replace-word" "durable.increment" [] |> expectOk "publish Flow replacement inside task" |> ignore
        let duringTask = Storage.load rollbackStore |> Result.defaultWith (fun problem -> failwith problem.Message)
        check (duringTask.ManifestHash <> before.ManifestHash) "Flow replacement becomes durable inside task before abort"
        dispatch rollbackEngine "task.abort" [] |> expectOk "abort Flow replacement task" |> ignore
        let restored = Storage.load rollbackStore |> Result.defaultWith (fun problem -> failwith problem.Message)
        equal before.ManifestHash restored.ManifestHash "task abort restores the original Flow manifest authority"
        check (restored.Generation > duringTask.Generation) "task abort advances Storage generation while restoring authority"
        check (beforeExport.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(rollbackProject, "dictionary.agent")).AsSpan())) "task abort restores exact Flow export bytes"
        let history = dispatch rollbackEngine "history" [ "word", jstr "durable.increment" ] |> expectOk "inspect restored Flow history"
        equal 1 ((history.["data"]).AsArray().Count) "task abort removes the rolled-back Flow revision from current history"
        equal flowWordSource (stringValue (history.["data"].[0].["source"])) "task abort restores the original authored Flow source"
        equal "10" (evalFlow rollbackEngine "durable::increment(9)" |> expectOk "evaluate restored Flow body" |> fun response -> stringValue (response.["data"].["stack"].[0])) "task abort restores the verified executable body"

    let private testNamedSnapshotRestoresFlowAndProviders root =
        let project = Path.Combine(root, "snapshot-flow")
        let savedClock = "2031-02-03T04:05:06Z"
        let capabilities = Set.ofList [ "fs.read"; "fs.write" ]
        let engine = Runtime.Engine(project, capabilities, savedClock)
        defineFlow engine flowWordSource [ flowTestSource ] [ flowExampleSource ] []
        |> expectOk "define snapshot Flow word"
        |> ignore
        commit engine "commit" "durable.increment" [] |> expectOk "commit snapshot Flow word" |> ignore
        evalStack engine "\"snapshot-file\" \"saved-value\" file.write"
        |> expectOk "write virtual provider state before snapshot"
        |> ignore
        dispatch engine "snapshot.save" [ "name", jstr "flow-baseline" ] |> expectOk "save Flow/provider snapshot" |> ignore
        let baseline = Storage.load (Storage.create project) |> Result.defaultWith (fun problem -> failwith problem.Message)

        let replacement = "word durable.increment(value: Int) -> Int {\n    effects none\n    add(value, 2)\n}"
        defineFlow engine replacement [] [] [ "replace", jbool true; "expectedRevision", jint 1 ]
        |> expectOk "stage later Flow revision after named snapshot"
        |> ignore
        commit engine "replace-word" "durable.increment" [] |> expectOk "commit later Flow revision after named snapshot" |> ignore
        evalStack engine "\"snapshot-file\" \"later-value\" file.write"
        |> expectOk "mutate virtual provider state after snapshot"
        |> ignore

        let reloaded = Runtime.Engine(project, capabilities, "2040-01-01T00:00:00Z")
        let loaded = dispatch reloaded "snapshot.load" [ "name", jstr "flow-baseline" ] |> expectOk "load named Flow/provider snapshot"
        equal savedClock (stringValue (loaded.["data"].["clockValue"])) "named snapshot restores its saved clock value"
        let restored = Storage.load (Storage.create project) |> Result.defaultWith (fun problem -> failwith problem.Message)
        equal baseline.ManifestHash restored.ManifestHash "named snapshot restores the original Flow manifest"
        let fileValue = evalStack reloaded "\"snapshot-file\" file.read" |> expectOk "read restored virtual provider file"
        equal "\"saved-value\"" (stringValue (fileValue.["data"].["stack"].[0])) "named snapshot restores exact virtual-file state"
        equal "2" (evalFlow reloaded "durable::increment(1)" |> expectOk "evaluate restored Flow revision" |> fun response -> stringValue (response.["data"].["stack"].[0])) "named snapshot rehydrates the earlier executable Flow revision"
        assertAllPassed 1 (dispatch reloaded "test" [ "word", jstr "durable.increment" ] |> expectOk "run restored Flow attachment after snapshot load")

    let private testPersistedBindingsAreVerified root =
        let project = Path.Combine(root, "persisted-binding-tamper")
        let engine = Runtime.Engine(project, Set.empty, "2030-01-02T03:04:05Z")
        defineFlow engine flowWordSource [ flowTestSource ] [ flowExampleSource ] []
        |> expectOk "define Flow source before persisted binding tamper"
        |> ignore
        commit engine "commit" "durable.increment" [] |> expectOk "commit source before persisted binding tamper" |> ignore

        let store = Storage.create project
        let snapshot = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        let manifest = snapshot.Manifest.Value
        let ownerId = getWordId engine "durable.increment"
        let ownerRevision = manifest.Revisions |> List.find (fun item -> item.WordId = ownerId)
        let targetBinding =
            ownerRevision.CallBindings
            |> List.find (fun binding -> binding.BodyRole = StoredCallBodyRole.Actual && binding.CaseName = Some "basic")
        let forgedRevision =
            { ownerRevision with
                CallBindings =
                    ownerRevision.CallBindings
                    |> List.map (fun binding ->
                        if binding = targetBinding then
                            { binding with Target = StoredCallTarget.UserWord "user-forged-runtime-target" }
                        else binding) }
        let forgedManifest =
            { manifest with
                Revisions = manifest.Revisions |> List.map (fun revision -> if revision.WordId = ownerId then forgedRevision else revision) }
        let sourceReferences =
            [ manifest.ProjectSource ]
            @ (manifest.Types |> List.map _.Definition)
            @ (manifest.Revisions |> List.collect (fun revision -> revision.Definition :: revision.Tests @ revision.Examples))
            |> List.distinct
        let sourceObjects =
            sourceReferences
            |> List.map (fun reference ->
                let content = Storage.readSource store reference |> Result.defaultWith (fun problem -> failwith problem.Message)
                { Reference = reference; Content = content })
        let exportText = File.ReadAllText(Path.Combine(project, "dictionary.agent"))
        let tamperCommit = Storage.commit store snapshot.Generation forgedManifest sourceObjects exportText
        match tamperCommit with
        | Ok _ -> ()
        | Error problem -> failwith $"Storage should accept the structurally well-shaped binding edit: {problem.Code}: {problem.Message}"

        let diagnostic =
            try
                Runtime.Engine(project, Set.empty) |> ignore
                None
            with
            | LanguageException problem -> Some problem
        let diagnostic = diagnostic |> Option.defaultWith (fun () -> failwith "A fresh Engine trusted a forged persisted Flow call target.")
        equal "FLOW_RUNTIME_BINDING_MISMATCH" diagnostic.Code "fresh Engine rejects mismatched persisted Flow metadata"
        equal (Some "durable.increment/basic") diagnostic.Word "binding mismatch identifies its owner and case"
        check diagnostic.Span.IsSome "binding mismatch identifies the authored call site"

    let private testRetainedDotBindingAcrossReplacement root =
        let project = Path.Combine(root, "retained-dot-binding")
        let engine = Runtime.Engine(project, Set.empty, "2030-01-02T03:04:05Z")
        let bumpSource =
            "word math.bump(value: Int, amount: Int) -> Int {\n"
            + "    effects none\n"
            + "    add(value, amount)\n"
            + "}"
        let bumpTest =
            "test math.bump/basic {\n"
            + "    math::bump(5, 1)\n"
            + "    => value math::bump(5, 1)\n"
            + "}"
        defineFlow engine bumpSource [ bumpTest ] [] [] |> expectOk "define a Flow dot-stage target" |> ignore
        let bumpId = getWordId engine "math.bump"
        commit engine "commit" "math.bump" [] |> expectOk "commit a Flow dot-stage target" |> ignore

        let callerSource =
            "word client.dot(value: Int) -> Int {\n"
            + "    effects none\n"
            + "    value.bump(amount = 1)\n"
            + "}"
        let callerTest =
            "test client.dot/basic {\n"
            + "    client::dot(5)\n"
            + "    => value client::dot(5)\n"
            + "}"
        defineFlow engine callerSource [ callerTest ] [] [] |> expectOk "define a retained named-argument dot caller" |> ignore
        commit engine "commit" "client.dot" [] |> expectOk "commit a retained named-argument dot caller" |> ignore
        equal "6" (evalFlow engine "client::dot(5)" |> expectOk "execute retained dot caller before replacement" |> fun response -> stringValue (response.["data"].["stack"].[0])) "dot caller binds to the original target"

        let replacementSource =
            "word math.bump(value: Int, amount: Int) -> Int {\n"
            + "    effects none\n"
            + "    add(amount, value)\n"
            + "}"
        defineFlow engine replacementSource [] [] [ "replace", jbool true; "expectedRevision", jint 1 ]
        |> expectOk "stage same-ID target replacement for retained dot caller"
        |> ignore
        commit engine "replace-word" "math.bump" []
        |> expectOk "commit replacement while validating retained dot caller"
        |> ignore
        equal bumpId (getWordId engine "math.bump") "compatible Flow replacement keeps the dot target ID"
        let history = dispatch engine "history" [ "word", jstr "math.bump" ] |> expectOk "inspect retained dot target history"
        equal 2 ((history.["data"]).AsArray().Count) "compatible Flow replacement advances target revision"
        equal "6" (evalFlow engine "client::dot(5)" |> expectOk "execute retained dot caller after target replacement" |> fun response -> stringValue (response.["data"].["stack"].[0])) "retained dot caller follows the same stable target ID"

        let store = Storage.create project
        let beforeAmbiguousAdd = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        let competingSource =
            "word other.bump(value: Int, amount: Int) -> Int {\n"
            + "    effects none\n"
            + "    add(value, amount)\n"
            + "}"
        let competingTest =
            "test other.bump/basic {\n"
            + "    other::bump(5, 1)\n"
            + "    => value other::bump(5, 1)\n"
            + "}"
        let ambiguous = defineFlow engine competingSource [ competingTest ] [] []
        check (not (succeeded ambiguous)) "adding a second compatible dot stage is rejected while the retained source becomes ambiguous"
        check ((errorCode ambiguous).StartsWith("FLOW_", StringComparison.Ordinal)) "retained dot ambiguity keeps a Flow diagnostic"
        let ambiguousOwner =
            try stringValue (ambiguous.["error"].["word"])
            with _ -> ""
        equal "client.dot" ambiguousOwner "retained source rebinding diagnostic identifies the original caller"
        check (not (isNull ambiguous.["error"].["span"])) "retained source rebinding diagnostic includes its call site"
        let afterAmbiguousAdd = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        equal beforeAmbiguousAdd.ManifestHash afterAmbiguousAdd.ManifestHash "failed retained-call rebind leaves current manifest unchanged"
        equal "6" (evalFlow engine "client::dot(5)" |> expectOk "evaluate retained caller after rejected dot-stage addition" |> fun response -> stringValue (response.["data"].["stack"].[0])) "failed source rebind leaves the previous executable snapshot active"

    let private testExpectationCoverageIsNotActualCoverage root =
        let word =
            "word coverage.same(value: Bool) -> Int {\n"
            + "    effects none\n"
            + "    if value { 7 } else { 7 }\n"
            + "}"
        let oppositeExpectation =
            "test coverage.same/true_actual {\n"
            + "    coverage::same(true)\n"
            + "    => value coverage::same(false)\n"
            + "}"
        let bothActualBranches =
            [ oppositeExpectation
              "test coverage.same/false_actual {\n"
              + "    coverage::same(false)\n"
              + "    => value coverage::same(true)\n"
              + "}" ]

        let missingBranchProject = Path.Combine(root, "expected-branch-not-coverage")
        let missingBranchEngine = Runtime.Engine(missingBranchProject, Set.empty, "2030-01-02T03:04:05Z")
        defineFlow missingBranchEngine word [ oppositeExpectation ] [] []
        |> expectOk "define library Flow word with opposite pure expectation"
        |> ignore
        let rejected = commit missingBranchEngine "commit" "coverage.same" [ "library", jbool true ]
        expectError "LIBRARY_COVERAGE_INCOMPLETE" rejected |> ignore
        let unchanged = Storage.load (Storage.create missingBranchProject) |> Result.defaultWith (fun problem -> failwith problem.Message)
        check unchanged.Manifest.IsNone "expectation-only branch trace does not publish the Flow library"
        dispatch missingBranchEngine "discard" [ "word", jstr "coverage.same" ] |> expectOk "discard incomplete library candidate" |> ignore

        let completeProject = Path.Combine(root, "actual-both-branches")
        let completeEngine = Runtime.Engine(completeProject, Set.empty, "2030-01-02T03:04:05Z")
        defineFlow completeEngine word bothActualBranches [] []
        |> expectOk "define library Flow word with tests that execute both actual branches"
        |> ignore
        let committed = commit completeEngine "commit" "coverage.same" [ "library", jbool true ]
        expectOk "commit library after both actual branches are covered" committed |> ignore
        let complete = Storage.load (Storage.create completeProject) |> Result.defaultWith (fun problem -> failwith problem.Message)
        check complete.Manifest.IsSome "actual execution of both branches permits the Flow library commit"

    let private testFiniteCoverageInvocationAndNormalReturns root =
        let identitySource =
            "word coverage.identity : Int -> Int\n"
            + "    effects none\n"
            + "end\n\n"
            + "test coverage.identity/unrelated\n"
            + "    5\n"
            + "    => 5\n"
            + "end"
        let unrelatedProject = Path.Combine(root, "finite-coverage-unrelated-test")
        let unrelatedEngine = Runtime.Engine(unrelatedProject, Set.empty)
        defineStack unrelatedEngine identitySource [] |> expectOk "define a zero-instruction Int identity and an unrelated passing owner test" |> ignore
        let uninvoked = commit unrelatedEngine "commit" "coverage.identity" [ "library", jbool true ] |> expectError "LIBRARY_FINITE_COVERAGE_INCOMPLETE"
        equal [ "targetInvocations=0" ] (jsonArrayStrings uninvoked.["error"].["actual"]) "a branchless open-domain identity still requires an actual target invocation"

        let invokedProject = Path.Combine(root, "finite-coverage-identity-invoked")
        let invokedEngine = Runtime.Engine(invokedProject, Set.empty)
        let invokedSource =
            "word coverage.identity : Int -> Int\n"
            + "    effects none\n"
            + "end\n\n"
            + "test coverage.identity/identity\n"
            + "    5 coverage.identity\n"
            + "    => 5\n"
            + "end"
        defineStack invokedEngine invokedSource [] |> expectOk "define the identity with a passing actual target call" |> ignore
        commit invokedEngine "commit" "coverage.identity" [ "library", jbool true ]
        |> expectOk "qualify a no-obligation identity after its exact revision was invoked"
        |> ignore

        let boolProject = Path.Combine(root, "finite-coverage-error-return")
        let boolEngine = Runtime.Engine(boolProject, Set.empty)
        let boolWord =
            "fn coverage.bool-result(value: Bool) -> Bool {\n"
            + "    if value { true } else { false }\n"
            + "}"
        let boolTests =
            [ "test coverage.bool-result/true { coverage::bool-result(true) => true }"
              "test coverage.bool-result/error-after-normal { coverage::bool-result(false); ::divide(1, 0) => error RUNTIME_DIVIDE_BY_ZERO }" ]
        let boolSource = boolWord + "\n\n" + String.concat "\n" boolTests
        defineFlowProject boolEngine boolSource [ "syntaxVersion", jint 2 ] |> expectOk "define Bool function with normal and expected-error test paths" |> ignore
        assertAllPassed 2 (dispatch boolEngine "test" [ "word", jstr "coverage.bool-result" ] |> expectOk "run normal-return and expected-error Bool cases")
        let boolCoverage =
            dispatch boolEngine "describe" [ "word", jstr "coverage.bool-result" ]
            |> expectOk "inspect actual Bool returns from passing expected-error tests"
            |> fun response -> response.["data"].["coverage"].["finiteCoverage"]
        equal 2 (boolCoverage.["targetInvocations"].GetValue<int>()) "both actual function invocations are retained"
        equal true (boolValue boolCoverage.["complete"]) "a completed false return before a later expected error contributes finite evidence"
        commit boolEngine "commit" "coverage.bool-result" [ "library", jbool true ]
        |> expectOk "qualify Bool function after retaining its completed return before the expected error"
        |> ignore

        let throwingProject = Path.Combine(root, "finite-coverage-throw-before-return")
        let throwingEngine = Runtime.Engine(throwingProject, Set.empty)
        let throwingSource =
            "fn coverage.bool-before-throw(value: Bool) -> Bool {\n"
            + "    if value { ::divide(1, 0); true } else { false }\n"
            + "}\n\n"
            + "test coverage.bool-before-throw/false { coverage::bool-before-throw(false) => false }\n"
            + "test coverage.bool-before-throw/throw { coverage::bool-before-throw(true) => error RUNTIME_DIVIDE_BY_ZERO }"
        defineFlowProject throwingEngine throwingSource [ "syntaxVersion", jint 2 ]
        |> expectOk "define a target that can throw before producing a return"
        |> ignore
        assertAllPassed 2 (dispatch throwingEngine "test" [ "word", jstr "coverage.bool-before-throw" ] |> expectOk "run a normal false return and a throw-before-return case")
        let throwingCoverage =
            dispatch throwingEngine "describe" [ "word", jstr "coverage.bool-before-throw" ]
            |> expectOk "inspect finite returns when the target throws before returning"
            |> fun response -> response.["data"].["coverage"].["finiteCoverage"]
        check
            (jsonArrayStrings throwingCoverage.["returns"].[0].["missing"] |> List.contains "true")
            "a target that throws before return does not receive a fabricated true observation"
        let throwGap = commit throwingEngine "commit" "coverage.bool-before-throw" [ "library", jbool true ] |> expectError "LIBRARY_COVERAGE_INCOMPLETE"
        equal "LIBRARY_COVERAGE_INCOMPLETE" (stringValue throwGap.["error"].["code"])
            "the real missing instruction or branch still blocks publication"

        let failedProject = Path.Combine(root, "finite-coverage-failed-assertion-control")
        let failedEngine = Runtime.Engine(failedProject, Set.empty)
        let failedSource =
            "fn coverage.failed-assertion(value: Bool) -> Bool { if value { true } else { false } }\n\n"
            + "test coverage.failed-assertion/wrong-value { coverage::failed-assertion(false) => true }\n"
            + "test coverage.failed-assertion/wrong-error-code { coverage::failed-assertion(false); ::divide(1, 0) => error RUNTIME_NOT_FOUND }"
        defineFlowProject failedEngine failedSource [ "syntaxVersion", jint 2 ]
        |> expectOk "define a target with a failing expected-value assertion"
        |> ignore
        let failedRows =
            dispatch failedEngine "test" [ "word", jstr "coverage.failed-assertion" ]
            |> expectOk "run the deliberately failing assertion control"
            |> fun response -> response.["data"].["results"].AsArray()
        equal 2 failedRows.Count "failed assertion controls both run"
        let rowFor name = failedRows |> Seq.find (fun row -> stringValue row.["name"] = name)
        equal false (boolValue (rowFor "wrong-value").["passed"]) "wrong expected value leaves the test failed"
        equal false (boolValue (rowFor "wrong-error-code").["passed"]) "wrong expected error code leaves the test failed"
        let failedCoverage =
            dispatch failedEngine "describe" [ "word", jstr "coverage.failed-assertion" ]
            |> expectOk "inspect finite evidence after the failing assertion control"
            |> fun response -> response.["data"].["coverage"].["finiteCoverage"]
        equal 0 (failedCoverage.["targetInvocations"].GetValue<int>()) "failed assertions contribute no finite return observations"
        commit failedEngine "commit" "coverage.failed-assertion" [ "library", jbool true ]
        |> expectError "COMMIT_TESTS_FAILED"
        |> ignore

        let refinedProject = Path.Combine(root, "finite-coverage-refined-string-result")
        let refinedEngine = Runtime.Engine(refinedProject, Set.empty)
        let refinedSource =
            "type Email : String { validate email::valid?; }\n\n"
            + "fn email.valid?(value: String) -> Bool { string::contains(value, \"@\") }\n\n"
            + "fn email.parse(value: String) -> Result<Email, String> {\n"
            + "    if string::contains(value, \"@\") {\n"
            + "        result::ok<Email, String>(Email::new(value))\n"
            + "    } else {\n"
            + "        result::error<Email, String>(\"invalid\")\n"
            + "    }\n"
            + "}\n\n"
            + "test email.valid?/valid { email::valid?(\"a@b\") => true }\n"
            + "test email.valid?/invalid { email::valid?(\"missing\") => false }\n"
            + "test email.parse/ok { email::parse(\"a@b\") => value result::ok<Email, String>(Email::new(\"a@b\")) }\n"
            + "test email.parse/error { email::parse(\"missing\") => value result::error<Email, String>(\"invalid\") }"
        defineFlowProject refinedEngine refinedSource [ "syntaxVersion", jint 2 ]
        |> expectOk "stage refined String Result parser and its valid/error cases"
        |> ignore
        commit refinedEngine "commit" "email.valid?" [ "library", jbool true ]
        |> expectOk "qualify the Bool validator with both return values"
        |> ignore
        commit refinedEngine "commit" "Email" [] |> expectOk "commit refined String scalar and validator closure" |> ignore
        commit refinedEngine "commit" "email.parse" [ "library", jbool true ]
        |> expectOk "qualify refined String Result parser after observing both Result tags"
        |> ignore
        let finiteResult =
            dispatch refinedEngine "describe" [ "word", jstr "email.parse" ]
            |> expectOk "inspect refined String Result coverage"
            |> fun response -> response.["data"].["coverage"].["finiteCoverage"]
        check (boolValue finiteResult.["complete"]) "refined String Result library reports complete finite coverage"
        equal [ "error"; "ok" ] (jsonArrayStrings finiteResult.["returns"].[0].["required"]) "open refined String payload preserves both finite Result variant obligations"

    let private testFlowMaintenanceRenameDeprecateAndRestore root =
        let project = Path.Combine(root, "flow-maintenance-mixed-rename")
        let capabilities = Set.ofList [ "console.write" ]
        let engine = Runtime.Engine(project, capabilities, "2034-05-06T07:08:09Z")
        let store = Storage.create project
        let defineAndCommitFlow name source tests examples =
            defineFlow engine source tests examples []
            |> expectOk $"define Flow maintenance fixture {name}"
            |> ignore
            commit engine "commit" name []
            |> expectOk $"commit Flow maintenance fixture {name}"
            |> ignore
        let sourceOf reference =
            Storage.readSource store reference |> Result.defaultWith (fun problem -> failwith problem.Message)
        let loadSnapshot () = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        let requiredManifest () =
            (loadSnapshot ()).Manifest |> Option.defaultWith (fun () -> failwith "Flow maintenance manifest is missing")
        let revision (manifest: ProjectManifest) name =
            let head = manifest.Words |> List.find (fun item -> item.CurrentName = name)
            manifest.Revisions
            |> List.find (fun item -> item.WordId = head.WordId && item.Revision = head.CurrentRevision)
        let history engine name =
            dispatch engine "history" [ "word", jstr name ]
            |> expectOk $"read maintenance history for {name}"
            |> fun response -> response.["data"].AsArray()

        let bumpSource =
            "word bump(value: Int, first: Int, second: Int) -> Int {\n"
            + "    effects none\n"
            + "    add(add(multiply(value, 100), multiply(first, 10)), second)\n"
            + "}"
        let bumpTest =
            "test bump/basic {\n"
            + "    bump(5, 1, 2)\n"
            + "    => value bump(5, 1, 2)\n"
            + "}"
        let bumpExample =
            "example bump/basic {\n"
            + "    bump(5, 1, 2)\n"
            + "    => 512\n"
            + "}"
        defineAndCommitFlow "bump" bumpSource [ bumpTest ] [ bumpExample ]
        let bumpId = getWordId engine "bump"

        let competitorSource =
            "word math.advance(value: Int, first: Int, second: Int) -> Int {\n"
            + "    effects none\n"
            + "    add(add(multiply(value, 100), multiply(first, 10)), second)\n"
            + "}"
        let competitorTest =
            "test math.advance/basic {\n"
            + "    math::advance(5, 1, 2)\n"
            + "    => 512\n"
            + "}"
        defineAndCommitFlow "math.advance" competitorSource [ competitorTest ] []
        let stepSource =
            "word step(value: Int) -> Int {\n"
            + "    effects none\n"
            + "    add(value, 1)\n"
            + "}"
        let stepTest =
            "test step/basic {\n"
            + "    step(4)\n"
            + "    => value step(4)\n"
            + "}"
        let stepExample =
            "example step/basic {\n"
            + "    step(4)\n"
            + "    => 5\n"
            + "}"
        defineAndCommitFlow "step" stepSource [ stepTest ] [ stepExample ]
        let stepId = getWordId engine "step"

        let stepCompetitorSource =
            "word math.stepped(value: Int) -> Int {\n"
            + "    effects none\n"
            + "    add(value, 9)\n"
            + "}"
        let stepCompetitorTest =
            "test math.stepped/basic {\n"
            + "    math::stepped(4)\n"
            + "    => 13\n"
            + "}"
        defineAndCommitFlow "math.stepped" stepCompetitorSource [ stepCompetitorTest ] []
        let effectWord name label value =
            "word probe." + name + "() -> Int {\n"
            + "    effects console.write\n"
            + "    console::write(\"" + label + "\");\n"
            + "    " + string value + "\n"
            + "}"
        let effectTest name value =
            "test probe." + name + "/basic {\n"
            + "    probe::" + name + "()\n"
            + "    => " + string value + "\n"
            + "}"
        defineAndCommitFlow "probe.receiver" (effectWord "receiver" "receiver" 5) [ effectTest "receiver" 5 ] []
        defineAndCommitFlow "probe.right" (effectWord "right" "second" 2) [ effectTest "right" 2 ] []
        defineAndCommitFlow "probe.left" (effectWord "left" "first" 1) [ effectTest "left" 1 ] []

        let dotSource =
            "word client.dot() -> Int {\n"
            + "    effects console.write\n"
            + "    probe::receiver().bump(second = probe::right(), first = probe::left())\n"
            + "}"
        let dotTest =
            "test client.dot/basic {\n"
            + "    client::dot()\n"
            + "    => 512\n"
            + "}"
        let dotExample =
            "example client.dot/basic {\n"
            + "    client::dot()\n"
            + "    => 512\n"
            + "}"
        defineAndCommitFlow "client.dot" dotSource [ dotTest ] [ dotExample ]
        let directSource =
            "word client.direct(value: Int) -> Int {\n"
            + "    effects none\n"
            + "    bump(value, 1, 2)\n"
            + "}"
        let directTest =
            "test client.direct/basic {\n"
            + "    client::direct(5)\n"
            + "    => value bump(5, 1, 2)\n"
            + "}"
        let directExample =
            "example client.direct/basic {\n"
            + "    client::direct(5)\n"
            + "    => 512\n"
            + "}"
        defineAndCommitFlow "client.direct" directSource [ directTest ] [ directExample ]
        let absoluteSource =
            "word client.absolute(value: Int) -> Int {\n"
            + "    effects none\n"
            + "    ::bump(value, 1, 2)\n"
            + "}"
        let absoluteTest =
            "test client.absolute/basic {\n"
            + "    client::absolute(5)\n"
            + "    => value ::bump(5, 1, 2)\n"
            + "}"
        let absoluteExample =
            "example client.absolute/basic {\n"
            + "    client::absolute(5)\n"
            + "    => 512\n"
            + "}"
        defineAndCommitFlow "client.absolute" absoluteSource [ absoluteTest ] [ absoluteExample ]
        let expectedOnlySource =
            "word client.expected_only() -> Int {\n"
            + "    effects none\n"
            + "    512\n"
            + "}"
        let expectedOnlyTest =
            "test client.expected_only/target_expectation {\n"
            + "    client::expected_only()\n"
            + "    => value bump(5, 1, 2)\n"
            + "}"
        let expectedOnlyExample =
            "example client.expected_only/basic {\n"
            + "    client::expected_only()\n"
            + "    => 512\n"
            + "}"
        defineAndCommitFlow "client.expected_only" expectedOnlySource [ expectedOnlyTest ] [ expectedOnlyExample ]
        let directStepSource =
            "word client.step_direct(value: Int) -> Int {\n"
            + "    effects none\n"
            + "    step(value)\n"
            + "}"
        let directStepTest =
            "test client.step_direct/basic {\n"
            + "    client::step_direct(4)\n"
            + "    => value step(4)\n"
            + "}"
        let directStepExample =
            "example client.step_direct/basic {\n"
            + "    client::step_direct(4)\n"
            + "    => 5\n"
            + "}"
        defineAndCommitFlow "client.step_direct" directStepSource [ directStepTest ] [ directStepExample ]
        let absoluteStepSource =
            "word client.step_absolute(value: Int) -> Int {\n"
            + "    effects none\n"
            + "    ::step(value)\n"
            + "}"
        let absoluteStepTest =
            "test client.step_absolute/basic {\n"
            + "    client::step_absolute(4)\n"
            + "    => value ::step(4)\n"
            + "}"
        let absoluteStepExample =
            "example client.step_absolute/basic {\n"
            + "    client::step_absolute(4)\n"
            + "    => 5\n"
            + "}"
        defineAndCommitFlow "client.step_absolute" absoluteStepSource [ absoluteStepTest ] [ absoluteStepExample ]
        let mapSource =
            "word client.map(value: Int) -> List<Int> {\n"
            + "    effects none\n"
            + "    list::singleton<Int>(value).map(word step)\n"
            + "}"
        let mapTest =
            "test client.map/basic {\n"
            + "    client::map(4)\n"
            + "    => value list::singleton<Int>(5)\n"
            + "}"
        let mapExample =
            "example client.map/static_callback {\n"
            + "    list::count(list::singleton<Int>(4).map(word step))\n"
            + "    => 1\n"
            + "}"
        defineAndCommitFlow "client.map" mapSource [ mapTest ] [ mapExample ]
        let stackBumpSource =
            "word client.stack : Int -> Int\n"
            + "    effects none\n"
            + "    1\n"
            + "    2\n"
            + "    bump\n"
            + "end\n"
            + "\n"
            + "test client.stack/basic\n"
            + "    5 client.stack\n"
            + "    => 512\n"
            + "end\n"
            + "\n"
            + "example client.stack/basic\n"
            + "    5 client.stack\n"
            + "    => 512\n"
            + "end\n"
        defineStack engine stackBumpSource []
        |> expectOk "define a Stack caller of the Flow rename target"
        |> ignore
        commit engine "commit" "client.stack" [] |> expectOk "commit Stack caller of the Flow rename target" |> ignore
        let legacySource =
            "word legacy.bump : Int -> Int\n"
            + "    effects none\n"
            + "    1 add\n"
            + "end\n"
            + "\n"
            + "test legacy.bump/basic\n"
            + "    5 legacy.bump\n"
            + "    => 6\n"
            + "end\n"
            + "\n"
            + "example legacy.bump/basic\n"
            + "    5 legacy.bump\n"
            + "    => 6\n"
            + "end\n"
        defineStack engine legacySource []
        |> expectOk "define a Stack target for Flow caller rename coverage"
        |> ignore
        commit engine "commit" "legacy.bump" [] |> expectOk "commit Stack target for Flow caller rename coverage" |> ignore
        let legacyId = getWordId engine "legacy.bump"

        let legacyCallerSource =
            "word client.legacy(value: Int) -> Int {\n"
            + "    effects none\n"
            + "    legacy::bump(value)\n"
            + "}"
        let legacyCallerTest =
            "test client.legacy/basic {\n"
            + "    client::legacy(5)\n"
            + "    => value legacy::bump(5)\n"
            + "}"
        let legacyCallerExample =
            "example client.legacy/basic {\n"
            + "    client::legacy(5)\n"
            + "    => 6\n"
            + "}"
        defineAndCommitFlow "client.legacy" legacyCallerSource [ legacyCallerTest ] [ legacyCallerExample ]
        let legacyCallerId = getWordId engine "client.legacy"

        let beforeInvalidRenames = loadSnapshot ()
        let beforeInvalidExport = File.ReadAllBytes(Path.Combine(project, "dictionary.agent"))
        expectError "RENAME_COLLISION" (dispatch engine "rename" [ "word", jstr "bump"; "to", jstr "math.advance"; "actor", jstr "client" ])
        |> ignore
        let malformedName = dispatch engine "rename" [ "word", jstr "bump"; "to", jstr "bad..name"; "actor", jstr "client" ]
        check (not (succeeded malformedName)) "rename rejects a destination with an empty dotted name segment"
        check ((errorCode malformedName).StartsWith("FLOW_", StringComparison.Ordinal) || (errorCode malformedName).StartsWith("RENAME_", StringComparison.Ordinal)) "malformed Flow destination returns a structured source/maintenance diagnostic"
        equal beforeInvalidRenames.ManifestHash (loadSnapshot ()).ManifestHash "collision and malformed Flow destination failures preserve manifest authority"
        check (beforeInvalidExport.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(project, "dictionary.agent")).AsSpan())) "collision and malformed Flow destination failures preserve export bytes"
        equal bumpId (getWordId engine "bump") "invalid rename attempts preserve the original stable target"

        let beforeSnapshot = loadSnapshot ()
        let beforeManifest = beforeSnapshot.Manifest |> Option.defaultWith (fun () -> failwith "baseline maintenance manifest is missing")
        let beforeExportPath = Path.Combine(project, "dictionary.agent")
        let beforeExport = File.ReadAllBytes beforeExportPath
        let beforeBump = revision beforeManifest "bump"
        let beforeDot = revision beforeManifest "client.dot"
        let beforeDirect = revision beforeManifest "client.direct"
        let beforeAbsolute = revision beforeManifest "client.absolute"
        let beforeExpectedOnly = revision beforeManifest "client.expected_only"
        let beforeStack = revision beforeManifest "client.stack"
        let beforeStep = revision beforeManifest "step"
        let beforeMap = revision beforeManifest "client.map"
        let beforeLegacy = revision beforeManifest "legacy.bump"
        let beforeLegacyCaller = revision beforeManifest "client.legacy"
        let beforeCompetitor = revision beforeManifest "math.advance"
        let beforeStepCompetitor = revision beforeManifest "math.stepped"
        let oldBumpText = sourceOf beforeBump.Definition
        let oldBumpTestText = sourceOf beforeBump.Tests.Head
        let oldBumpExampleText = sourceOf beforeBump.Examples.Head
        let oldDotText = sourceOf beforeDot.Definition
        let oldDotExampleText = sourceOf beforeDot.Examples.Head
        let oldExpectedTestText = sourceOf beforeExpectedOnly.Tests.Head
        let oldStackText = sourceOf beforeStack.Definition
        let oldLegacyCallerText = sourceOf beforeLegacyCaller.Definition
        let oldLegacyCallerExampleText = sourceOf beforeLegacyCaller.Examples.Head
        let oldLegacyTestText = sourceOf beforeLegacy.Tests.Head
        let oldStepExampleText = sourceOf beforeStep.Examples.Head
        let oldStepTestText = sourceOf beforeStep.Tests.Head
        let oldMapExampleText = sourceOf beforeMap.Examples.Head
        let oldLegacyExampleText = sourceOf beforeLegacy.Examples.Head
        let oldBumpHistory = history engine "bump" |> fun rows -> rows.ToJsonString()
        let oldDotHistory = history engine "client.dot" |> fun rows -> rows.ToJsonString()
        dispatch engine "snapshot.save" [ "name", jstr "before-flow-maintenance" ]
        |> expectOk "save the pre-maintenance durable snapshot"
        |> ignore

        let task = dispatch engine "task.begin" [ "goal", jstr "rename and deprecate Flow-bound words" ] |> expectOk "begin maintenance rollback task"
        check (not (isNull task.["data"])) "maintenance task begins from a committed manifest"
        dispatch engine "rename" [ "word", jstr "bump"; "to", jstr "advance"; "actor", jstr "client" ]
        |> expectOk "rename Flow target inside task"
        |> ignore
        dispatch engine "rename" [ "word", jstr "step"; "to", jstr "stepped"; "actor", jstr "client" ]
        |> expectOk "rename callback target inside task"
        |> ignore
        dispatch engine "rename" [ "word", jstr "legacy.bump"; "to", jstr "legacy.advance"; "actor", jstr "client" ]
        |> expectOk "rename Stack target with Flow callers inside task"
        |> ignore
        dispatch engine "deprecate" [ "word", jstr "stepped"; "actor", jstr "client" ]
        |> expectOk "deprecate Flow target inside task"
        |> ignore
        dispatch engine "task.abort" [] |> expectOk "abort successful Flow maintenance transaction" |> ignore
        let afterAbort = loadSnapshot ()
        equal beforeSnapshot.ManifestHash afterAbort.ManifestHash "task abort restores the exact pre-maintenance manifest authority"
        check (beforeExport.AsSpan().SequenceEqual(File.ReadAllBytes(beforeExportPath).AsSpan())) "task abort restores exact pre-maintenance export bytes"
        equal oldBumpHistory ((history engine "bump").ToJsonString()) "task abort restores original Flow target history"
        equal oldDotHistory ((history engine "client.dot").ToJsonString()) "task abort restores original Flow caller history"
        equal oldBumpText (sourceOf beforeBump.Definition) "task abort retains the original content-addressed target bytes"
        equal oldDotText (sourceOf beforeDot.Definition) "task abort retains the original content-addressed caller bytes"
        equal "512" (evalFlow engine "::bump(5, 1, 2)" |> expectOk "evaluate target after task abort" |> fun response -> stringValue (response.["data"].["stack"].[0])) "task abort restores the original target name and verified body"
        expectError "NAME_UNKNOWN_WORD" (dispatch engine "describe" [ "word", jstr "advance" ]) |> ignore
        check (not (boolValue ((dispatch engine "describe" [ "word", jstr "step" ] |> expectOk "inspect callback target after task abort").["data"].["deprecated"]))) "task abort restores the Flow target's deprecation metadata"

        dispatch engine "rename" [ "word", jstr "bump"; "to", jstr "advance"; "actor", jstr "client" ]
        |> expectOk "rename Flow target and its bound references"
        |> ignore
        let afterBumpSnapshot = loadSnapshot ()
        let afterBumpManifest = afterBumpSnapshot.Manifest.Value
        let currentRevision name = revision afterBumpManifest name
        let assertAdvancedFromBaseline (oldRevision: WordRevision) newName label =
            let updated = currentRevision newName
            equal oldRevision.WordId updated.WordId $"{label} preserves stable WordId"
            equal (oldRevision.Revision + 1) updated.Revision $"{label} advances its owner revision exactly once"
        assertAdvancedFromBaseline beforeBump "advance" "renamed Flow target"
        assertAdvancedFromBaseline beforeDot "client.dot" "dot caller"
        assertAdvancedFromBaseline beforeDirect "client.direct" "direct caller"
        assertAdvancedFromBaseline beforeAbsolute "client.absolute" "absolute-root caller"
        assertAdvancedFromBaseline beforeExpectedOnly "client.expected_only" "expected-expression-only case owner"
        assertAdvancedFromBaseline beforeStack "client.stack" "Stack caller"
        equal legacyCallerId beforeLegacyCaller.WordId "unrelated Flow caller keeps its WordId"
        equal beforeLegacyCaller (currentRevision "client.legacy") "unrelated Flow caller revision and all source metadata remain unchanged"
        let directAfter = currentRevision "client.direct"
        equal { Frontend = SourceFrontend.Flow; Version = 1 } directAfter.SourceFormat "rewritten direct caller remains Flow/1"
        check (directAfter.CallBindings |> List.exists (fun binding -> binding.BodyRole = StoredCallBodyRole.Definition && binding.Target = StoredCallTarget.UserWord bumpId)) "direct caller definition remains bound to the original target ID"
        check (directAfter.CallBindings |> List.exists (fun binding -> binding.BodyRole = StoredCallBodyRole.ExpectedExpression && binding.Target = StoredCallTarget.UserWord bumpId)) "direct caller's pure expected expression is rewritten by stable target ID"
        equal { Frontend = SourceFrontend.Stack; Version = 1 } (currentRevision "client.stack").SourceFormat "rewritten Stack caller remains Stack/1"
        equal [] (currentRevision "client.stack").CallBindings "rewritten Stack caller does not acquire Flow call metadata"
        let dotAfter = currentRevision "client.dot"
        let dotDefinition = sourceOf dotAfter.Definition
        check (dotDefinition.Contains("::advance", StringComparison.Ordinal)) "dot rename writes an explicit root-qualified target after a same-signature suffix competitor appears"
        let dotBinding = dotAfter.CallBindings |> List.find (fun binding -> binding.BodyRole = StoredCallBodyRole.Definition && binding.Target = StoredCallTarget.UserWord bumpId)
        equal StoredCallForm.AbsoluteRoot dotBinding.Form "rewritten dot call is stored as an absolute-root call"
        let expectedOnlyAfter = currentRevision "client.expected_only"
        equal beforeExpectedOnly.Definition expectedOnlyAfter.Definition "expected-only rewrite leaves the owner's executable definition object unchanged"
        check (beforeExpectedOnly.Tests <> expectedOnlyAfter.Tests) "expected-only rewrite replaces the authored test object"
        let expectedOnlyBinding =
            expectedOnlyAfter.CallBindings
            |> List.find (fun binding -> binding.BodyRole = StoredCallBodyRole.ExpectedExpression && binding.Target = StoredCallTarget.UserWord bumpId)
        check (expectedOnlyBinding.Source = expectedOnlyAfter.Tests.Head) "expected-expression call metadata points to the rewritten test source object"
        let expectedOnlyTestAfter = sourceOf expectedOnlyAfter.Tests.Head
        check (expectedOnlyTestAfter.Contains("::advance", StringComparison.Ordinal)) "expected-only case source rewrites its expression call"
        check (not (expectedOnlyTestAfter.Contains("bump(", StringComparison.Ordinal))) "expected-only case has no stale call to the previous name"
        equal beforeCompetitor (currentRevision "math.advance") "same-signature namespace competitor remains byte-for-byte and revision-for-revision unchanged"
        equal oldBumpText (sourceOf beforeBump.Definition) "Flow rename retains historical definition bytes"
        equal oldBumpTestText (sourceOf beforeBump.Tests.Head) "Flow rename retains historical target test bytes"
        equal oldBumpExampleText (sourceOf beforeBump.Examples.Head) "Flow rename retains historical example bytes"
        equal oldDotText (sourceOf beforeDot.Definition) "Flow rename retains historical caller bytes"
        equal oldDotExampleText (sourceOf beforeDot.Examples.Head) "Flow rename retains historical dot example bytes"
        equal oldExpectedTestText (sourceOf beforeExpectedOnly.Tests.Head) "Flow rename retains historical expected-expression test bytes"
        equal oldStackText (sourceOf beforeStack.Definition) "Flow rename retains historical Stack caller bytes"
        let bumpHistoryAfterRename = history engine "advance"
        equal oldBumpText (stringValue (bumpHistoryAfterRename.[0].["source"])) "rename history exposes the exact previous Flow source"
        let stackAfterBumpText = sourceOf (currentRevision "client.stack").Definition
        check (stackAfterBumpText.Contains("advance", StringComparison.Ordinal)) "Stack source caller rewrites the Flow target name"
        check (not (stackAfterBumpText.Contains("bump", StringComparison.Ordinal)) ) "Stack source caller no longer references the old Flow name"
        let flowEvalBeforeStepRename = evalFlow engine "client::dot()" |> expectOk "run effectful named-argument dot call after Flow rename"
        equal "512" (stringValue (flowEvalBeforeStepRename.["data"].["stack"].[0])) "renamed dot call preserves its result"
        equal [ "receiver"; "second"; "first" ] (jsonArrayStrings flowEvalBeforeStepRename.["data"].["console"]) "dot receiver evaluates once before explicit named arguments in written order"
        assertAllPassed 1 (dispatch engine "test" [ "word", jstr "advance" ] |> expectOk "run renamed target's Flow actual and expected call sites")
        let renamedBumpExample = dispatch engine "example" [ "word", jstr "advance"; "caseName", jstr "basic" ] |> expectOk "run renamed target example"
        check (boolValue (renamedBumpExample.["data"].["results"].[0].["passed"])) "Flow target example actual call is rewritten and still passes"
        let renamedDotExample = dispatch engine "example" [ "word", jstr "client.dot"; "caseName", jstr "basic" ] |> expectOk "run rewritten dot caller example"
        check (boolValue (renamedDotExample.["data"].["results"].[0].["passed"])) "Flow dot caller example remains attached and executable"
        assertAllPassed 1 (dispatch engine "test" [ "word", jstr "client.expected_only" ] |> expectOk "run expected-expression-only owner after rename")
        assertAllPassed 1 (dispatch engine "test" [ "word", jstr "client.stack" ] |> expectOk "run Stack caller after Flow target rename")

        dispatch engine "rename" [ "word", jstr "step"; "to", jstr "stepped"; "actor", jstr "client" ]
        |> expectOk "rename Flow callback target and rewrite direct, root, callback, and attachment sites"
        |> ignore
        let afterStepSnapshot = loadSnapshot ()
        let afterStepManifest = afterStepSnapshot.Manifest.Value
        let stepRevision name = revision afterStepManifest name
        let assertStepAdvanced (oldRevision: WordRevision) newName label =
            let updated = stepRevision newName
            equal oldRevision.WordId updated.WordId $"{label} preserves stable WordId"
            equal (oldRevision.Revision + 1) updated.Revision $"{label} advances once for callback target rename"
        assertStepAdvanced beforeStep "stepped" "renamed callback target"
        assertStepAdvanced beforeMap "client.map" "static callback owner"
        assertStepAdvanced (revision beforeManifest "client.step_direct") "client.step_direct" "direct short-name owner"
        assertStepAdvanced (revision beforeManifest "client.step_absolute") "client.step_absolute" "root-call owner"
        equal beforeStepCompetitor (stepRevision "math.stepped") "callback suffix competitor remains unchanged"
        let mapAfter = stepRevision "client.map"
        let callbackBinding =
            mapAfter.CallBindings
            |> List.find (fun binding -> binding.BodyRole = StoredCallBodyRole.Definition && binding.Target = StoredCallTarget.UserWord stepId)
        match callbackBinding.Form with
        | StoredCallForm.StaticCallback(_, FlowWordReferenceQualification.AbsoluteRoot) -> ()
        | form -> failwith $"static callback rewrite must use absolute-root qualification after suffix collision, got {form}"
        check ((sourceOf mapAfter.Definition).Contains("::stepped", StringComparison.Ordinal)) "static callback source is explicitly root-qualified"
        equal oldStepExampleText (sourceOf beforeStep.Examples.Head) "callback rename retains the target's exact historical example bytes"
        equal oldStepTestText (sourceOf beforeStep.Tests.Head) "callback rename retains the target's exact historical test bytes"
        equal oldMapExampleText (sourceOf beforeMap.Examples.Head) "callback rename retains the caller example's exact historical source bytes"
        let directStepAfter = stepRevision "client.step_direct"
        check ((sourceOf directStepAfter.Definition).Contains("::stepped(", StringComparison.Ordinal)) "short direct call is made unambiguous by explicit root qualification"
        equal oldBumpText (sourceOf beforeBump.Definition) "second rename does not change prior historical Flow source"
        equal [ "receiver"; "second"; "first" ] (jsonArrayStrings ((evalFlow engine "client::dot()" |> expectOk "recheck effectful dot call after callback rename").["data"].["console"])) "dot-call receiver and named arguments keep their order through subsequent maintenance"
        assertAllPassed 1 (dispatch engine "test" [ "word", jstr "client.map" ] |> expectOk "run static callback case after target rename")
        let renamedCallbackExample = dispatch engine "example" [ "word", jstr "client.map"; "caseName", jstr "static_callback" ] |> expectOk "run rewritten callback from an example actual body"
        check (boolValue (renamedCallbackExample.["data"].["results"].[0].["passed"])) "example actual call through a renamed static callback still passes"
        let renamedStepExample = dispatch engine "example" [ "word", jstr "stepped"; "caseName", jstr "basic" ] |> expectOk "run renamed callback target example"
        check (boolValue (renamedStepExample.["data"].["results"].[0].["passed"])) "callback target example call is rewritten and still passes"

        dispatch engine "rename" [ "word", jstr "legacy.bump"; "to", jstr "legacy.advance"; "actor", jstr "client" ]
        |> expectOk "rename Stack target selected by Flow identities"
        |> ignore
        let afterLegacySnapshot = loadSnapshot ()
        let afterLegacyManifest = afterLegacySnapshot.Manifest.Value
        let legacyAfter = revision afterLegacyManifest "legacy.advance"
        let legacyCallerAfter = revision afterLegacyManifest "client.legacy"
        equal legacyId legacyAfter.WordId "Stack target rename preserves its stable identity"
        equal (beforeLegacy.Revision + 1) legacyAfter.Revision "Stack target rename advances its revision once"
        equal legacyCallerId legacyCallerAfter.WordId "Flow caller of Stack target keeps its identity"
        equal (beforeLegacyCaller.Revision + 1) legacyCallerAfter.Revision "Flow definition, actual, expected, and example owner advances once"
        equal { Frontend = SourceFrontend.Flow; Version = 1 } legacyCallerAfter.SourceFormat "Stack target rewrite retains Flow caller format"
        let legacyBinding = legacyCallerAfter.CallBindings |> List.find (fun binding -> binding.Target = StoredCallTarget.UserWord legacyId)
        equal "legacy.advance" legacyBinding.RequestedName "Flow binding resolves the renamed Stack target by stable ID"
        check ((sourceOf legacyCallerAfter.Definition).Contains("legacy::advance", StringComparison.Ordinal)) "Flow caller source is rewritten for a Stack target rename"
        equal oldLegacyCallerText (sourceOf beforeLegacyCaller.Definition) "Stack-target rename retains prior Flow caller bytes in history"
        equal oldLegacyCallerExampleText (sourceOf beforeLegacyCaller.Examples.Head) "Stack-target rename retains the prior Flow caller example bytes"
        equal oldLegacyTestText (sourceOf beforeLegacy.Tests.Head) "Stack-target rename retains the prior Stack target test bytes"
        equal oldLegacyExampleText (sourceOf beforeLegacy.Examples.Head) "Stack-target rename retains the prior Stack target example bytes"
        let legacyHistory = history engine "legacy.advance"
        equal (sourceOf beforeLegacy.Definition) (stringValue (legacyHistory.[0].["source"])) "Stack target rename preserves its exact historical definition text"
        let freshCurrent = Runtime.Engine(project, capabilities, "2034-05-06T07:08:09Z")
        equal bumpId (getWordId freshCurrent "advance") "fresh Engine reload preserves renamed Flow target identity"
        equal stepId (getWordId freshCurrent "stepped") "fresh Engine reload preserves renamed callback identity"
        equal legacyId (getWordId freshCurrent "legacy.advance") "fresh Engine reload preserves renamed Stack target identity"
        equal "512" (stringValue (evalFlow freshCurrent "::advance(5, 1, 2)" |> expectOk "invoke renamed root target after reload" |> fun response -> response.["data"].["stack"].[0])) "fresh Engine reload executes renamed Flow source"
        equal "6" (stringValue (evalFlow freshCurrent "client::legacy(5)" |> expectOk "invoke Flow caller of renamed Stack target" |> fun response -> response.["data"].["stack"].[0])) "fresh Engine resolves the Flow caller against the renamed Stack WordId"
        let cliResponse = cliEval project "::advance(5, 1, 2)" |> expectOk "fresh-process CLI loads Flow maintenance result"
        equal "512" (stringValue (cliResponse.["data"].["stack"].[0])) "fresh CLI executes the renamed Flow target"
        let oldCliName = cliEval project "::bump(5, 1, 2)"
        check (not (succeeded oldCliName)) "fresh CLI does not retain the old Flow target spelling"
        assertAllPassed 1 (dispatch freshCurrent "test" [ "word", jstr "legacy.advance" ] |> expectOk "run renamed Stack target case after reload")
        assertAllPassed 1 (dispatch freshCurrent "test" [ "word", jstr "client.legacy" ] |> expectOk "run Flow caller case after Stack rename and reload")
        let legacyExample = dispatch freshCurrent "example" [ "word", jstr "legacy.advance"; "caseName", jstr "basic" ] |> expectOk "run renamed Stack target example after reload"
        check (boolValue (legacyExample.["data"].["results"].[0].["passed"])) "Stack target example remains attached after Flow caller binding rewrite"
        let legacyCallerExample = dispatch freshCurrent "example" [ "word", jstr "client.legacy"; "caseName", jstr "basic" ] |> expectOk "run Flow caller example after Stack target rename"
        check (boolValue (legacyCallerExample.["data"].["results"].[0].["passed"])) "Flow caller example invokes the renamed Stack target"

        let beforeDeprecation = requiredManifest () |> fun manifest -> revision manifest "stepped"
        let beforeDeprecationSource = sourceOf beforeDeprecation.Definition
        let beforeDeprecationBindings = beforeDeprecation.CallBindings
        let beforeDeprecationTests = beforeDeprecation.Tests
        let beforeDeprecationExamples = beforeDeprecation.Examples
        let deprecateResponse =
            dispatch engine "deprecate" [ "word", jstr "stepped"; "actor", jstr "client" ]
            |> expectOk "deprecate a Flow-authored word without rewriting its source"
        equal stepId (stringValue (deprecateResponse.["data"].["id"])) "Flow deprecation preserves stable identity"
        equal (beforeDeprecation.Revision + 1) ((deprecateResponse.["data"].["revision"]).GetValue<int>()) "Flow deprecation advances metadata revision once"
        let afterDeprecation = requiredManifest () |> fun manifest -> revision manifest "stepped"
        equal (beforeDeprecation.Revision + 1) afterDeprecation.Revision "durable Flow deprecation has one new revision"
        check afterDeprecation.Deprecated "Flow deprecation metadata is durable"
        equal beforeDeprecation.Definition afterDeprecation.Definition "deprecation leaves the authored definition SourceRef unchanged"
        equal beforeDeprecationTests afterDeprecation.Tests "deprecation leaves authored test SourceRefs unchanged"
        equal beforeDeprecationExamples afterDeprecation.Examples "deprecation leaves authored example SourceRefs unchanged"
        equal beforeDeprecationBindings afterDeprecation.CallBindings "deprecation leaves stored call bindings unchanged"
        equal beforeDeprecationSource (sourceOf afterDeprecation.Definition) "deprecation keeps exact authored Flow bytes"
        let historyAfterDeprecation = history engine "stepped"
        equal 3 historyAfterDeprecation.Count "deprecation appends exactly one metadata revision after rename history"
        equal beforeDeprecationSource (stringValue (historyAfterDeprecation.[2].["source"])) "deprecation history repeats exact authored Flow source"
        assertAllPassed 1 (dispatch engine "test" [ "word", jstr "stepped" ] |> expectOk "Flow deprecation requires and preserves passing tests")
        equal "5" (stringValue (evalFlow engine "::stepped(4)" |> expectOk "deprecated Flow word remains callable" |> fun response -> response.["data"].["stack"].[0])) "deprecated Flow word remains callable"
        equal "[5]" (stringValue (evalFlow engine "client::map(4)" |> expectOk "deprecated callback remains callable through a Flow caller" |> fun response -> response.["data"].["stack"].[0])) "Flow static callback still returns the deprecated target's mapped value"
        let beforeIdempotentDeprecation = (loadSnapshot ()).ManifestHash
        dispatch engine "deprecate" [ "word", jstr "stepped"; "actor", jstr "client" ]
        |> expectOk "repeat Flow deprecation idempotently"
        |> ignore
        equal beforeIdempotentDeprecation (loadSnapshot ()).ManifestHash "repeated Flow deprecation does not create another revision"
        equal 3 (history engine "stepped").Count "repeated Flow deprecation leaves history length unchanged"
        let freshDeprecated = Runtime.Engine(project, capabilities, "2040-02-02T00:00:00Z")
        check (boolValue ((dispatch freshDeprecated "describe" [ "word", jstr "stepped" ] |> expectOk "reload deprecated Flow metadata").["data"].["deprecated"])) "Flow deprecation metadata reloads from durable authority"
        equal "5" (stringValue (evalFlow freshDeprecated "::stepped(4)" |> expectOk "invoke deprecated word after fresh reload" |> fun response -> response.["data"].["stack"].[0])) "fresh Engine keeps the deprecated Flow word callable"

        let restoredEngine = Runtime.Engine(project, capabilities, "2044-01-01T00:00:00Z")
        dispatch restoredEngine "snapshot.load" [ "name", jstr "before-flow-maintenance" ]
        |> expectOk "restore the exact pre-maintenance named snapshot"
        |> ignore
        let afterSnapshotRestore = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        equal beforeSnapshot.ManifestHash afterSnapshotRestore.ManifestHash "named snapshot restores the exact pre-maintenance authority"
        check (beforeExport.AsSpan().SequenceEqual(File.ReadAllBytes(beforeExportPath).AsSpan())) "named snapshot restores exact pre-maintenance export bytes"
        equal bumpId (getWordId restoredEngine "bump") "named snapshot restores the original Flow target ID and name"
        equal stepId (getWordId restoredEngine "step") "named snapshot restores the original callback target ID and name"
        equal legacyId (getWordId restoredEngine "legacy.bump") "named snapshot restores the original Stack target ID and name"
        expectError "NAME_UNKNOWN_WORD" (dispatch restoredEngine "describe" [ "word", jstr "advance" ]) |> ignore
        check (not (boolValue ((dispatch restoredEngine "describe" [ "word", jstr "step" ] |> expectOk "inspect snapshot-restored callback").["data"].["deprecated"]))) "named snapshot restores pre-deprecation metadata"
        equal oldBumpHistory ((history restoredEngine "bump").ToJsonString()) "named snapshot restores exact Flow target history"
        equal oldDotHistory ((history restoredEngine "client.dot").ToJsonString()) "named snapshot restores exact mixed-caller history"
        equal oldBumpText (sourceOf beforeBump.Definition) "named snapshot leaves original target object bytes available"
        equal "512" (stringValue (evalFlow restoredEngine "::bump(5, 1, 2)" |> expectOk "evaluate restored snapshot Flow target" |> fun response -> response.["data"].["stack"].[0])) "named snapshot reactivates the original verified Flow program"

    let private testFlowMaintenanceRejectsUntouchedRebind root =
        let project = Path.Combine(root, "flow-maintenance-untouched-rebind")
        let engine = Runtime.Engine(project, Set.empty, "2034-05-06T07:08:09Z")
        let store = Storage.create project
        let bumpSource = "word bump(value: Int, first: Int, second: Int) -> Int {\n    effects none\n    add(add(value, first), second)\n}"
        let bumpTest = "test bump/basic {\n    bump(5, 1, 2)\n    => 8\n}"
        defineFlow engine bumpSource [ bumpTest ] [] [] |> expectOk "define target for untouched-call rebind guard" |> ignore
        commit engine "commit" "bump" [] |> expectOk "commit target for untouched-call rebind guard" |> ignore
        let bumpId = getWordId engine "bump"
        let competitor = "word math.advance(value: Int, first: Int, second: Int) -> Int {\n    effects none\n    add(add(value, first), second)\n}"
        let competitorTest = "test math.advance/basic {\n    math::advance(5, 1, 2)\n    => 8\n}"
        defineFlow engine competitor [ competitorTest ] [] [] |> expectOk "define pre-existing suffix competitor" |> ignore
        commit engine "commit" "math.advance" [] |> expectOk "commit pre-existing suffix competitor" |> ignore
        let competitorId = getWordId engine "math.advance"
        let caller =
            "word client.paired(value: Int) -> Int {\n"
            + "    effects none\n"
            + "    let renamed = bump(value, 1, 2);\n"
            + "    let untouched = advance(value, 1, 2);\n"
            + "    add(renamed, untouched)\n"
            + "}"
        let callerTest =
            "test client.paired/basic {\n"
            + "    client::paired(5)\n"
            + "    => value client::paired(5)\n"
            + "}"
        defineFlow engine caller [ callerTest ] [] [] |> expectOk "define one document with a renamed site and an untouched competitor call" |> ignore
        commit engine "commit" "client.paired" [] |> expectOk "commit caller before target rename" |> ignore
        let pairedId = getWordId engine "client.paired"
        let before = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        let beforeExport = File.ReadAllBytes(Path.Combine(project, "dictionary.agent"))
        let oldBindingTargets =
            before.Manifest.Value.Revisions
            |> List.find (fun item -> item.Name = "client.paired")
            |> fun item -> item.CallBindings |> List.map _.Target
        check (oldBindingTargets |> List.contains (StoredCallTarget.UserWord bumpId)) "fixture has a distinct binding to the requested rename target"
        check (oldBindingTargets |> List.contains (StoredCallTarget.UserWord competitorId)) "fixture has a distinct untouched call to the suffix competitor"
        let failed = dispatch engine "rename" [ "word", jstr "bump"; "to", jstr "advance"; "actor", jstr "client" ]
        check (not (succeeded failed)) "rename refuses to publish when an untouched source call would resolve to a different target"
        equal "FLOW_AMBIGUOUS_CALL" (errorCode failed) "rename rejects the newly ambiguous untouched short call during final source resolution"
        let after = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        equal before.ManifestHash after.ManifestHash "rejected untouched-call rebind leaves exact manifest authority unchanged"
        check (beforeExport.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(project, "dictionary.agent")).AsSpan())) "rejected untouched-call rebind leaves export bytes unchanged"
        equal bumpId (getWordId engine "bump") "rejected rebind keeps the old Flow target active"
        equal pairedId (getWordId engine "client.paired") "rejected rebind preserves caller identity"
        equal "16" (stringValue (evalFlow engine "client::paired(5)" |> expectOk "run caller after refused rebind" |> fun response -> response.["data"].["stack"].[0])) "rejected rebind leaves the prior live caller executable"
        expectError "NAME_UNKNOWN_WORD" (dispatch engine "describe" [ "word", jstr "advance" ]) |> ignore

    let private testFlowMaintenanceFailureAndLibraryCoverage root =
        let failingProject = Path.Combine(root, "flow-maintenance-failed-deprecate")
        let capabilities = Set.singleton "clock.read"
        let savedClock = "2034-05-06T07:08:09Z"
        let changedClock = "2040-01-02T03:04:05Z"
        let failingEngine = Runtime.Engine(failingProject, capabilities, savedClock)
        let guardedSource =
            "word guarded.answer() -> String {\n"
            + "    effects clock.read\n"
            + "    clock::now()\n"
            + "}"
        let guardedTest =
            "test guarded.answer/provider_guard {\n"
            + "    guarded::answer()\n"
            + "    => \"" + savedClock + "\"\n"
            + "}"
        defineFlow failingEngine guardedSource [ guardedTest ] [] []
        |> expectOk "define Flow word with a provider-backed passing guard test"
        |> ignore
        commit failingEngine "commit" "guarded.answer" [] |> expectOk "commit Flow word with passing guard test" |> ignore
        let failingStore = Storage.create failingProject
        let beforeFailure = Storage.load failingStore |> Result.defaultWith (fun problem -> failwith problem.Message)
        let beforeGuardRevision = beforeFailure.Manifest.Value.Revisions |> List.find (fun item -> item.Name = "guarded.answer")
        let beforeGuardHistory = dispatch failingEngine "history" [ "word", jstr "guarded.answer" ] |> expectOk "capture guard history before deprecation"
        let changedClockEngine = Runtime.Engine(failingProject, capabilities, changedClock)
        expectError "DEPRECATE_TESTS_FAILED" (dispatch changedClockEngine "deprecate" [ "word", jstr "guarded.answer"; "actor", jstr "client" ])
        |> ignore
        let afterFailure = Storage.load failingStore |> Result.defaultWith (fun problem -> failwith problem.Message)
        equal beforeFailure.ManifestHash afterFailure.ManifestHash "failed deprecation test leaves durable authority unchanged"
        let afterGuardRevision = afterFailure.Manifest.Value.Revisions |> List.find (fun item -> item.Name = "guarded.answer")
        equal beforeGuardRevision afterGuardRevision "failed deprecation leaves word revision and source references unchanged"
        equal ((beforeGuardHistory.["data"]).ToJsonString()) (((dispatch changedClockEngine "history" [ "word", jstr "guarded.answer" ] |> expectOk "read history after failed deprecation").["data"]).ToJsonString()) "failed deprecation adds no history entry"
        check (not (boolValue ((dispatch changedClockEngine "describe" [ "word", jstr "guarded.answer" ] |> expectOk "inspect live word after failed deprecation").["data"].["deprecated"]))) "failed deprecation leaves the word non-deprecated after reopening with a different clock"
        equal ("\"" + changedClock + "\"") (stringValue (evalFlow changedClockEngine "guarded::answer()" |> expectOk "execute word after failed deprecation" |> fun response -> response.["data"].["stack"].[0])) "failed deprecation leaves the persisted implementation callable under the new clock"

        let libraryProject = Path.Combine(root, "flow-maintenance-library-coverage")
        let libraryEngine = Runtime.Engine(libraryProject, capabilities, savedClock)
        let librarySource =
            "word coverage.branch(value: String) -> Int {\n"
            + "    effects clock.read\n"
            + "    if equals(clock::now(), value) { 1 } else { 1 }\n"
            + "}"
        let matchingClockTest =
            "test coverage.branch/current_clock {\n"
            + "    coverage::branch(\"" + savedClock + "\")\n"
            + "    => 1\n"
            + "}"
        let nonmatchingTest =
            "test coverage.branch/nonmatching {\n"
            + "    coverage::branch(\"not-the-clock\")\n"
            + "    => 1\n"
            + "}"
        defineFlow libraryEngine librarySource [ matchingClockTest; nonmatchingTest ] [] []
        |> expectOk "define library with owner tests for both fixed-clock outcomes"
        |> ignore
        commit libraryEngine "commit" "coverage.branch" [ "library", jbool true ]
        |> expectOk "commit Flow library with actual own-site coverage of both clock comparison branches"
        |> ignore
        assertAllPassed 2 (dispatch libraryEngine "test" [ "word", jstr "coverage.branch" ] |> expectOk "verify both coverage fixture tests pass before mutation")
        let libraryStore = Storage.create libraryProject
        let beforeChangedClockReload = Storage.load libraryStore |> Result.defaultWith (fun problem -> failwith problem.Message)
        let beforeLibraryExport = File.ReadAllBytes(Path.Combine(libraryProject, "dictionary.agent"))
        try
            Runtime.Engine(libraryProject, capabilities, changedClock) |> ignore
            failwith "durable library with stale finite branch evidence loaded under a changed clock"
        with
        | LanguageException diagnostic -> equal "LIBRARY_COVERAGE_INCOMPLETE" diagnostic.Code "durable library reload requalifies actual branch coverage under the current provider"
        let afterChangedClockReload = Storage.load libraryStore |> Result.defaultWith (fun problem -> failwith problem.Message)
        equal beforeChangedClockReload.ManifestHash afterChangedClockReload.ManifestHash "failed durable library requalification leaves storage authority unchanged"
        check (beforeLibraryExport.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(libraryProject, "dictionary.agent")).AsSpan())) "failed durable library requalification leaves export bytes unchanged"

    let private testFlowStaticListFold root =
        let project = Path.Combine(root, "flow-static-list-fold")
        let capabilities = Set.ofList [ "fs.read"; "fs.write" ]
        let engine = Runtime.Engine(project, capabilities, "2034-05-06T07:08:09Z")
        let foldOwnerSource =
            "word domain.fold-number(items: List<Int>) -> Int {\n"
            + "    effects none\n"
            + "    items.fold(0, domain::fold-step)\n"
            + "}"
        let source =
            "type Email : String { }\n\n"
            + "record Customer { field email: Email; }\n\n"
            + "word domain.fold-step(acc: Int, item: Int) -> Int {\n"
            + "    effects none\n"
            + "    if int::greater-than(item, 0) { add(multiply(acc, 10), item) } else { multiply(acc, 10) }\n"
            + "}\n\n"
            + foldOwnerSource + "\n\n"
            + "word domain.keep-email(acc: Email, item: Int) -> Email {\n"
            + "    effects none\n"
            + "    acc\n"
            + "}\n\n"
            + "word domain.fold-email(items: List<Int>) -> Email {\n"
            + "    effects none\n"
            + "    items.fold(Email::new(\"seed@example.com\"), domain::keep-email)\n"
            + "}\n\n"
            + "word domain.keep-customer(acc: Customer, item: Int) -> Customer {\n"
            + "    effects none\n"
            + "    acc\n"
            + "}\n\n"
            + "word domain.fold-customer(items: List<Int>) -> Customer {\n"
            + "    effects none\n"
            + "    items.fold(customer::new(email = Email::new(\"seed@example.com\")), domain::keep-customer)\n"
            + "}\n\n"
            + "word io.read-step(acc: String, item: Int) -> String {\n"
            + "    effects fs.read\n"
            + "    file::read(acc)\n"
            + "}\n\n"
            + "word io.fold-path(items: List<Int>) -> String {\n"
            + "    effects fs.read\n"
            + "    items.fold(\"/fold-seed\", io::read-step)\n"
            + "}\n\n"
            + "test domain.fold-step/positive { domain::fold-step(0, 1) => 1 }\n\n"
            + "test domain.fold-number/empty { domain::fold-number(list::empty<Int>()) => 0 }\n\n"
            + "test domain.fold-number/nonempty { domain::fold-number(list::append(list::append(list::singleton<Int>(1), 2), 3)) => 123 }\n\n"
            + "test domain.keep-email/identity { equals(domain::keep-email(Email::new(\"seed@example.com\"), 1), Email::new(\"seed@example.com\")) => true }\n\n"
            + "test domain.fold-email/empty { equals(domain::fold-email(list::empty<Int>()), Email::new(\"seed@example.com\")) => true }\n\n"
            + "test domain.keep-customer/identity { equals(domain::keep-customer(customer::new(email = Email::new(\"seed@example.com\")), 1), customer::new(email = Email::new(\"seed@example.com\"))) => true }\n\n"
            + "test domain.fold-customer/empty { equals(domain::fold-customer(list::empty<Int>()), customer::new(email = Email::new(\"seed@example.com\"))) => true }\n\n"
            + "test io.fold-path/empty { io::fold-path(list::empty<Int>()) => \"/fold-seed\" }"
        defineFlowProject engine source [] |> expectOk "define Flow fold words with an integer branch, nominal accumulators, and an effectful callback" |> ignore
        evalFlow engine "file::write(\"/fold-seed\", \"callback-ran\")" |> expectOk "seed the virtual file used to observe accidental empty-fold callback execution" |> ignore
        equal "\"/fold-seed\"" (stringValue (evalFlow engine "io::fold-path(list::empty<Int>())" |> expectOk "evaluate an empty effectful fold" |> fun response -> response["data"].["stack"].[0])) "empty fold returns its seed without invoking the read callback"
        expectError "FLOW_CALLBACK_INPUT_TYPE"
            (defineFlowProject engine
                "word domain.invalid-email-fold(items: List<Int>) -> Email {\n effects none\n items.fold(Email::new(\"seed@example.com\"), domain::string-step)\n }\n\nword domain.string-step(acc: String, item: Int) -> String {\n effects none\n acc\n }"
                [])
        |> ignore
        equal "Email" (stringValue (evalFlow engine "domain::fold-email(list::empty<Int>())" |> expectOk "fold with a nominal scalar seed" |> fun response -> response["data"].["stackTypes"].[0])) "Flow fold keeps a nominal scalar accumulator type"
        equal "Customer" (stringValue (evalFlow engine "domain::fold-customer(list::empty<Int>())" |> expectOk "fold with a nominal record seed" |> fun response -> response["data"].["stackTypes"].[0])) "Flow fold keeps a nominal record accumulator type"
        let dependencies = dispatch engine "dependencies" [ "word", jstr "domain.fold-number" ] |> expectOk "inspect fold callback dependency"
        check (jsonArrayStrings (dependencies["data"].["dependencies"]) |> List.contains "domain.fold-step") "fold callback is a direct dependency"
        let callers = dispatch engine "callers" [ "word", jstr "domain.fold-step" ] |> expectOk "inspect fold callback callers"
        check (jsonArrayStrings callers["data"] |> List.contains "domain.fold-number") "fold owner appears as a callback caller"
        let effects = dispatch engine "effects" [ "word", jstr "io.fold-path" ] |> expectOk "inspect fold effect closure"
        equal [ "fs.read" ] (jsonArrayStrings effects["data"]) "fold inherits its static callback's declared effect"
        assertAllPassed 2 (dispatch engine "test" [ "word", jstr "domain.fold-number" ] |> expectOk "run owner tests covering both fold branches")

        let missingCallbackBranch = commit engine "commit" "domain.fold-step" [ "library", jbool true ] |> expectError "LIBRARY_COVERAGE_INCOMPLETE"
        check (missingCallbackBranch["error"].["actual"].ToJsonString().Length > 0) "library gate reports the callback's own uncovered branch"
        let negativeCallbackTest = "test domain.fold-step/nonpositive { domain::fold-step(12, -1) => 120 }"
        defineFlowProject engine negativeCallbackTest [] |> expectOk "attach the fold callback's own negative branch test" |> ignore
        assertAllPassed 2 (dispatch engine "test" [ "word", jstr "domain.fold-step" ] |> expectOk "run the callback's independent branch tests")
        commit engine "commit" "domain.fold-step" [ "library", jbool true ] |> expectOk "publish callback after its own branches are covered" |> ignore
        commit engine "commit" "domain.fold-number" [ "library", jbool true ] |> expectOk "publish fold owner after empty/nonempty branch coverage" |> ignore

        let store = Storage.create project
        let snapshot = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        let manifest = snapshot.Manifest |> Option.defaultWith (fun () -> failwith "Fold commit did not persist a manifest.")
        let ownerId = getWordId engine "domain.fold-number"
        let callbackId = getWordId engine "domain.fold-step"
        let ownerRevision = manifest.Revisions |> List.find (fun item -> item.WordId = ownerId && item.Revision = (manifest.Words |> List.find (fun head -> head.WordId = ownerId)).CurrentRevision)
        let exactOwnerSource = Storage.readSource store ownerRevision.Definition |> Result.defaultWith (fun problem -> failwith problem.Message)
        equal foldOwnerSource exactOwnerSource "persisted definition retains the exact authored fold source"
        let callbackBinding = ownerRevision.CallBindings |> List.find (fun binding -> binding.Form = StoredCallForm.StaticCallback("fold", FlowWordReferenceQualification.NamespaceQualified))
        equal (StoredCallTarget.UserWord callbackId) callbackBinding.Target "persisted fold binding targets the callback's stable identity"
        match callbackBinding.Path with
        | FlowAstPath.FlowAstPath segments ->
            check (segments |> List.contains (FlowAstPathSegment.DotArgument 1)) "persisted callback binding points to DotArgument 1"
            check (not (segments |> List.contains (FlowAstPathSegment.DotArgument 0))) "persisted seed expression remains distinct from the callback"
        let reloaded = Runtime.Engine(project, capabilities, "2034-05-06T07:08:09Z")
        equal ownerId (getWordId reloaded "domain.fold-number") "fresh Engine reload preserves the fold owner identity"
        equal callbackId (getWordId reloaded "domain.fold-step") "fresh Engine reload preserves the callback identity"
        assertAllPassed 2 (dispatch reloaded "test" [ "word", jstr "domain.fold-number" ] |> expectOk "run persisted empty/nonempty fold tests")
        equal "123" (stringValue (evalFlow reloaded "domain::fold-number(list::append(list::append(list::singleton<Int>(1), 2), 3))" |> expectOk "execute reloaded ordered fold" |> fun response -> response["data"].["stack"].[0])) "reloaded fold still visits items in order"

        dispatch reloaded "rename" [ "word", jstr "domain.fold-step"; "to", jstr "domain.append-number"; "actor", jstr "client" ]
        |> expectOk "semantically rename a fold callback and rewrite its caller"
        |> ignore
        equal callbackId (getWordId reloaded "domain.append-number") "callback rename preserves its stable identity"
        let renamedSource = stringValue (dispatch reloaded "source" [ "word", jstr "domain.fold-number" ] |> expectOk "read rewritten fold source" |> fun response -> response["data"])
        check (renamedSource.Contains("items.fold(0, domain::append-number)", StringComparison.Ordinal)) "rename rewrites only the callback reference while retaining Flow/1 source form"
        let renamedOwner = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message) |> fun value -> value.Manifest.Value.Revisions |> List.find (fun item -> item.WordId = ownerId && item.Revision = 2)
        let renamedBinding = renamedOwner.CallBindings |> List.find (fun binding -> binding.Form = StoredCallForm.StaticCallback("fold", FlowWordReferenceQualification.NamespaceQualified))
        equal (StoredCallTarget.UserWord callbackId) renamedBinding.Target "renamed fold binding remains attached to the same callback identity"
        match renamedBinding.Path with
        | FlowAstPath.FlowAstPath segments -> check (segments |> List.contains (FlowAstPathSegment.DotArgument 1)) "renamed callback remains at DotArgument 1"

        let replacement =
            "word domain.append-number(acc: Int, item: Int) -> Int {\n"
            + "    effects none\n"
            + "    if int::greater-than(item, 0) { add(multiply(acc, 10), item) } else { multiply(acc, 10) }\n"
            + "}"
        let currentCallbackRevision = (Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)).Manifest.Value.Words |> List.find (fun head -> head.WordId = callbackId) |> fun head -> head.CurrentRevision
        defineFlow reloaded replacement [] [] [ "replace", jbool true; "expectedRevision", jint currentCallbackRevision ]
        |> expectOk "stage a compatible fold callback replacement"
        |> ignore
        commit reloaded "replace-word" "domain.append-number" [] |> expectOk "publish the replacement behind the stable callback identity" |> ignore
        equal callbackId (getWordId reloaded "domain.append-number") "fold callback replacement preserves its stable identity"
        assertAllPassed 2 (dispatch reloaded "test" [ "word", jstr "domain.fold-number" ] |> expectOk "run the retained fold caller tests after callback replacement")
        equal "123" (stringValue (evalFlow reloaded "domain::fold-number(list::append(list::append(list::singleton<Int>(1), 2), 3))" |> expectOk "execute fold after callback replacement" |> fun response -> response["data"].["stack"].[0])) "replacement preserves fold behavior"

        let deniedProject = Path.Combine(root, "flow-fold-effect-denial")
        let denied = Runtime.Engine(deniedProject, Set.empty, "2034-05-06T07:08:09Z")
        let effectSource =
            "word io.read-step(acc: String, item: Int) -> String {\n effects fs.read\n file::read(acc)\n }\n\n"
            + "word io.fold-path(items: List<Int>) -> String {\n effects fs.read\n items.fold(\"/missing\", io::read-step)\n }"
        defineFlowProject denied effectSource [] |> expectOk "define denied effectful fold without granting filesystem access" |> ignore
        expectError "CAPABILITY_DENIED" (evalFlow denied "io::fold-path(list::empty<Int>())")
        |> ignore

    let private testFlow2StaticListCallbacks root =
        let project = Path.Combine(root, "flow2-static-list-callbacks")
        let engine = Runtime.Engine(project, Set.empty, "2042-03-04T05:06:07Z")
        let source =
            "fn callbacks.increment(value: Int) -> Int { add(value, 1) }\n\n"
            + "fn callbacks.is-positive(value: Int) -> Bool { int::greater-than(value, 0) }\n\n"
            + "fn callbacks.visit-item(value: Int) -> Unit { unit }\n\n"
            + "fn identity(value: Int) -> Int { value }\n\n"
            + "fn callbacks.map-values(items: List<Int>) -> List<Int> { items.map(callbacks::increment) }\n\n"
            + "fn callbacks.map-values-dotted(items: List<Int>) -> List<Int> { items.map(callbacks.increment) }\n\n"
            + "fn callbacks.map-identity-root(items: List<Int>) -> List<Int> { items.map(.identity) }\n\n"
            + "fn callbacks.filter-positive(items: List<Int>) -> List<Int> { items.filter(callbacks::is-positive) }\n\n"
            + "fn callbacks.visit-all(items: List<Int>) -> Unit { items.each(callbacks::visit-item) }\n\n"
            + "test callbacks.map-values/single { callbacks::map-values(list::singleton<Int>(4)) => value list::singleton<Int>(5) }\n\n"
            + "test callbacks.map-values-dotted/single { callbacks.map-values-dotted(list.singleton<Int>(4)) => value list.singleton<Int>(5) }\n\n"
            + "test callbacks.map-identity-root/single { callbacks.map-identity-root(list.singleton<Int>(4)) => value list.singleton<Int>(4) }\n\n"
            + "test callbacks.filter-positive/mixed { callbacks::filter-positive(list::append(list::singleton<Int>(-1), 2)) => value list::singleton<Int>(2) }\n\n"
            + "test callbacks.visit-all/populated { callbacks::visit-all(list::append(list::singleton<Int>(1), 2)) => unit }"
        defineFlowProject engine source [ "syntaxVersion", jint 2 ]
        |> expectOk "define and typecheck Flow/2 map, filter, and each receiver callbacks"
        |> ignore
        assertAllPassed 1 (dispatch engine "test" [ "word", jstr "callbacks.map-values" ] |> expectOk "run Flow/2 map receiver callback")
        assertAllPassed 1 (dispatch engine "test" [ "word", jstr "callbacks.map-values-dotted" ] |> expectOk "run Flow/2 dotted map callback")
        assertAllPassed 1 (dispatch engine "test" [ "word", jstr "callbacks.map-identity-root" ] |> expectOk "run Flow/2 leading-dot root callback")
        assertAllPassed 1 (dispatch engine "test" [ "word", jstr "callbacks.filter-positive" ] |> expectOk "run Flow/2 filter receiver callback")
        assertAllPassed 1 (dispatch engine "test" [ "word", jstr "callbacks.visit-all" ] |> expectOk "run Flow/2 each receiver callback")

    let private testFlow2DottedCallsAndRename root =
        let project = Path.Combine(root, "flow2-dotted-calls")
        let engine = Runtime.Engine(project, Set.ofList [ "fs.read"; "fs.write" ], "2042-03-04T05:06:07Z")
        let source =
            """record Customer {
    field email: String
    validate customer.valid?
}

enum State {
    case ready
    case waiting
}

fn identity(value: Int) -> Int { value }

fn customer.balance(value: Int) -> Int { add(value, 1) }

fn customer.active?(value: Int) -> Bool { int.greater-than(value, 0) }

fn customer.valid?(value: Customer) -> Bool { string.contains(value.email, "@") }

fn owner.local(customer: Int) -> Int { customer.balance() }

fn owner.exact(customer: Int) -> Int { .customer.balance(7) }

fn owner.legacy(customer: Int) -> Int { customer::balance(7) }

fn owner.root(identity: Int) -> Int { .identity(identity) }

fn owner.map(values: List<Int>) -> List<Bool> { values.map(customer.active?) }

fn owner.map-shadow(customer: List<Int>) -> List<Bool> { customer.map(customer.active?) }

fn owner.map-root(values: List<Int>) -> List<Int> { values.map(.identity) }

fn owner.option(value: Int) -> Option<Int> { option.some<Int>(value) }

fn owner.result(value: Int) -> Result<Int, String> { result.ok<Int, String>(value) }

fn owner.state() -> State { State.ready() }

fn owner.valid?(email: String) -> Bool { customer.valid?(customer.new(email = email)) }

fn owner.read-path(path: String) -> String {
    effects fs.read
    file.read(path)
}

fn owner.read-bound(file: String) -> String {
    effects fs.read
    file.read()
}

test owner.local/receiver { owner.local(9) => 10 }

test customer.balance/increment { customer.balance(7) => 8 }

test owner.exact/root-qualified { owner.exact(99) => 8 }

test owner.legacy/double-colon { owner.legacy(99) => 8 }

test owner.root/unqualified { owner.root(99) => 99 }

test owner.map/dotted-callback {
    owner.map(list.append(list.singleton<Int>(-1), 2))
    => value list.append(list.singleton<Bool>(false), true)
}

test owner.map-shadow/local-root-shadow {
    owner.map-shadow(list.append(list.singleton<Int>(-1), 2))
    => value list.append(list.singleton<Bool>(false), true)
}

test owner.map-root/root-callback {
    owner.map-root(list.singleton<Int>(4))
    => value list.singleton<Int>(4)
}

test owner.option/dotted-constructor { owner.option(3) => value option.some<Int>(3) }

test owner.result/dotted-constructor { owner.result(5) => value result.ok<Int, String>(5) }

test owner.state/dotted-enum-constructor { owner.state() => value State.ready() }

test owner.valid?/dotted-validator { owner.valid?("person@example.com") => true }

test owner.read-path/dotted-primitive {
    file.write("dotted-read", "ready")
    owner.read-path("dotted-read")
    => "ready" effects {
        fs.read: 1
    }
}

test owner.read-bound/local-receiver {
    file.write("dotted-bound-read", "ready")
    owner.read-bound("dotted-bound-read")
    => "ready" effects {
        fs.read: 1
    }
}"""
        defineFlowProject engine source [ "syntaxVersion", jint 2 ]
        |> expectOk "stage Flow/2 dotted calls, constructors, callbacks, validators, and newline-separated cases"
        |> ignore

        for owner in
            [ "owner.local"; "owner.exact"; "owner.legacy"; "owner.root"; "owner.map"; "owner.map-root"
              "owner.map-shadow"; "owner.option"; "owner.result"; "owner.state"; "owner.valid?"; "owner.read-path"; "owner.read-bound" ] do
            assertAllPassed 1 (dispatch engine "test" [ "word", jstr owner ] |> expectOk ("run dotted-call case for " + owner))

        let balanceId = getWordId engine "customer.balance"
        commit engine "commit" "customer.balance" [] |> expectOk "persist the exact dotted target" |> ignore
        commit engine "commit" "owner.local" [] |> expectOk "persist the lexical receiver caller" |> ignore
        commit engine "commit" "owner.exact" [] |> expectOk "persist the leading-dot exact caller" |> ignore
        commit engine "commit" "owner.legacy" [] |> expectOk "persist the legacy namespace-qualified caller" |> ignore

        let store = Storage.create project
        let before = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        let beforeManifest = before.Manifest |> Option.defaultWith (fun () -> failwith "dotted-call rename fixture did not persist")
        let revision name =
            let head = beforeManifest.Words |> List.find (fun item -> item.CurrentName = name)
            beforeManifest.Revisions |> List.find (fun item -> item.WordId = head.WordId && item.Revision = head.CurrentRevision)
        let exactBefore = revision "owner.exact"
        let exactBinding = exactBefore.CallBindings |> List.find (fun binding -> binding.Target = StoredCallTarget.UserWord balanceId)
        equal (StoredCallTarget.UserWord balanceId) exactBinding.Target "leading-dot call persists the exact dictionary target identity"
        let localBefore = revision "owner.local"
        let localBinding = localBefore.CallBindings |> List.find (fun binding -> binding.Target = StoredCallTarget.UserWord balanceId)
        equal (StoredCallTarget.UserWord balanceId) localBinding.Target "lexical receiver persists its resolved method target identity"
        let legacyBefore = revision "owner.legacy"
        let legacyBeforeSource = Storage.readSource store legacyBefore.Definition |> Result.defaultWith (fun problem -> failwith problem.Message)
        check (legacyBeforeSource.Contains("customer::balance(7)", StringComparison.Ordinal)) "the committed Flow/2 legacy caller retains its authored double-colon bytes"
        let legacyReloaded = Runtime.Engine(project, Set.ofList [ "fs.read"; "fs.write" ], "2042-03-04T05:06:07Z")
        let legacyReloadedSource =
            dispatch legacyReloaded "source" [ "word", jstr "owner.legacy" ]
            |> expectOk "read persisted Flow/2 legacy caller after a fresh Engine reload"
            |> fun response -> stringValue response.["data"]
        equal legacyBeforeSource legacyReloadedSource "fresh reload preserves the legacy caller source bytes before rename"
        assertAllPassed 1 (dispatch legacyReloaded "test" [ "word", jstr "owner.legacy" ] |> expectOk "run persisted Flow/2 legacy caller before rename")

        dispatch legacyReloaded "rename" [ "word", jstr "customer.balance"; "to", jstr "account.balance" ]
        |> expectOk "rename a dotted call target and rewrite its callers"
        |> ignore
        equal balanceId (getWordId legacyReloaded "account.balance") "dotted target rename retains its stable identity"

        let after = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        let afterManifest = after.Manifest |> Option.defaultWith (fun () -> failwith "renamed dotted-call manifest is missing")
        let afterRevision name =
            let head = afterManifest.Words |> List.find (fun item -> item.CurrentName = name)
            afterManifest.Revisions |> List.find (fun item -> item.WordId = head.WordId && item.Revision = head.CurrentRevision)
        let exactAfter = afterRevision "owner.exact"
        let exactAfterBinding = exactAfter.CallBindings |> List.find (fun binding -> binding.Target = StoredCallTarget.UserWord balanceId)
        equal (StoredCallTarget.UserWord balanceId) exactAfterBinding.Target "renamed leading-dot call keeps its exact target identity"
        let exactAfterSource = Storage.readSource store exactAfter.Definition |> Result.defaultWith (fun problem -> failwith problem.Message)
        check (exactAfterSource.Contains("account.balance(7)", StringComparison.Ordinal)) "rename rewrites the exact dotted source to its unshadowed namespace"
        check (not (exactAfterSource.Contains(".account.balance(7)", StringComparison.Ordinal))) "canonical formatting omits an unnecessary leading-root marker after rename"
        let localAfter = afterRevision "owner.local"
        let localAfterBinding = localAfter.CallBindings |> List.find (fun binding -> binding.Target = StoredCallTarget.UserWord balanceId)
        equal (StoredCallTarget.UserWord balanceId) localAfterBinding.Target "renamed receiver call retains its exact target identity"

        let reloaded = Runtime.Engine(project, Set.ofList [ "fs.read"; "fs.write" ], "2042-03-04T05:06:07Z")
        assertAllPassed 1 (dispatch reloaded "test" [ "word", jstr "owner.local" ] |> expectOk "run the renamed receiver call after reload")
        assertAllPassed 1 (dispatch reloaded "test" [ "word", jstr "owner.exact" ] |> expectOk "run the renamed exact call after reload")
        assertAllPassed 1 (dispatch reloaded "test" [ "word", jstr "owner.legacy" ] |> expectOk "run the rewritten legacy caller after reload")

    let private testFlowValidatorCannotBeRenamedAfterTypeCommit root =
        let project = Path.Combine(root, "flow-validator-frozen")
        let engine = Runtime.Engine(project, Set.empty, "2034-05-06T07:08:09Z")
        let store = Storage.create project
        let validatorSource =
            "word email.valid?(value: String) -> Bool {\n"
            + "    effects none\n"
            + "    string::contains(value, \"@\")\n"
            + "}"
        let validTest =
            "test email.valid?/valid {\n"
            + "    email::valid?(\"a@b\")\n"
            + "    => true\n"
            + "}"
        let invalidTest =
            "test email.valid?/invalid {\n"
            + "    email::valid?(\"missing\")\n"
            + "    => false\n"
            + "}"
        defineFlow engine validatorSource [ validTest; invalidTest ] [] []
        |> expectOk "define Flow-authored scalar validator with passing cases"
        |> ignore
        commit engine "commit" "email.valid?" [] |> expectOk "commit Flow-authored scalar validator" |> ignore
        let validatorId = getWordId engine "email.valid?"

        let emailTypeSource =
            "type Email : String\n"
            + "    validate email.valid?\n"
            + "end\n"
            + "\n"
            + "word email.roundtrip : String -> String\n"
            + "    effects none\n"
            + "    Email.new Email.value\n"
            + "end\n"
            + "\n"
            + "test email.roundtrip/valid\n"
            + "    \"a@b\" email.roundtrip\n"
            + "    => \"a@b\"\n"
            + "end\n"
        defineStack engine emailTypeSource []
        |> expectOk "stage a Stack scalar type whose validator is Flow-authored"
        |> ignore
        dispatch engine "commit" []
        |> expectOk "commit the Flow validator closure and tested nominal constructor caller"
        |> ignore
        assertAllPassed 1 (dispatch engine "test" [ "word", jstr "email.roundtrip" ] |> expectOk "run committed nominal caller through Flow validator")

        let before = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        let beforeManifest = before.Manifest.Value
        let validatorHead = beforeManifest.Words |> List.find (fun head -> head.WordId = validatorId)
        let beforeRevision = beforeManifest.Revisions |> List.find (fun item -> item.WordId = validatorId && item.Revision = validatorHead.CurrentRevision)
        let beforeExport = File.ReadAllBytes(Path.Combine(project, "dictionary.agent"))
        let beforeHistory = dispatch engine "history" [ "word", jstr "email.valid?" ] |> expectOk "capture Flow validator history before frozen-closure guard"
        expectError "TYPE_VALIDATOR_FROZEN" (dispatch engine "rename" [ "word", jstr "email.valid?"; "to", jstr "email.accepts?"; "actor", jstr "client" ])
        |> ignore
        let after = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        equal before.ManifestHash after.ManifestHash "frozen Flow validator rename leaves exact durable authority unchanged"
        check (beforeExport.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(project, "dictionary.agent")).AsSpan())) "frozen Flow validator rename leaves exact export bytes unchanged"
        let afterManifest = after.Manifest.Value
        let afterRevision = afterManifest.Revisions |> List.find (fun item -> item.WordId = validatorId && item.Revision = beforeRevision.Revision)
        equal beforeRevision afterRevision "frozen Flow validator retains exact revision and authored references"
        equal validatorId (getWordId engine "email.valid?") "frozen Flow validator retains stable identity and name"
        expectError "NAME_UNKNOWN_WORD" (dispatch engine "describe" [ "word", jstr "email.accepts?" ]) |> ignore
        equal ((beforeHistory.["data"]).ToJsonString()) (((dispatch engine "history" [ "word", jstr "email.valid?" ] |> expectOk "read Flow validator history after refused rename").["data"]).ToJsonString()) "frozen validator refusal adds no history entry"
        assertAllPassed 2 (dispatch engine "test" [ "word", jstr "email.valid?" ] |> expectOk "Flow validator cases remain active after refused rename")
        let frozenAttachment =
            "test email.valid?/late_case {\n"
            + "    email::valid?(\"late\")\n"
            + "    => false\n"
            + "}"
        expectError "TYPE_VALIDATOR_FROZEN" (dispatch engine "define" [ "source", jstr frozenAttachment ])
        |> ignore
        equal before.ManifestHash (Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)).ManifestHash "case-only edit cannot bypass the frozen validator replacement guard"
        equal [ "invalid"; "valid" ] (jsonArrayStrings (dispatch engine "tests" [ "word", jstr "email.valid?" ] |> expectOk "inspect frozen validator cases after rejected addition" |> fun response -> response.["data"])) "frozen validator refusal does not activate an attachment edit"
        equal "\"a@b\"" (stringValue (evalStack engine "\"a@b\" Email.new Email.value" |> expectOk "construct nominal value through retained Flow validator" |> fun response -> response.["data"].["stack"].[0])) "committed Stack nominal type keeps its Flow validator callable"

    let private testFlowProjectDocumentTypesCommitAndReload root =
        let project = Path.Combine(root, "flow-project-document")
        let engine = Runtime.Engine(project, Set.empty, "2038-04-05T06:07:08Z")
        let store = Storage.create project
        let recordSource =
            "record Customer {\n"
            + "    field email: Email;\n"
            + "}"
        let emailTypeSource =
            "type Email : String {\n"
            + "    validate email::valid?;\n"
            + "}"
        let speedTypeSource = "type MetersPerSecond : Float { }"
        let validatorSource =
            "word email.valid?(value: String) -> Bool {\n"
            + "    effects none\n"
            + "    string::contains(value, \"@\")\n"
            + "}"
        let customerWordSource =
            "word customer.accepts-email(value: Customer) -> Bool {\n"
            + "    effects none\n"
            + "    email::valid?(Email::value(value.email()))\n"
            + "}"
        let speedWordSource =
            "word speed.roundtrip(value: MetersPerSecond) -> MetersPerSecond {\n"
            + "    effects none\n"
            + "    MetersPerSecond::new(MetersPerSecond::value(value))\n"
            + "}"
        let validatorValidTest =
            "test email.valid?/valid {\n"
            + "    email::valid?(\"a@b\")\n"
            + "    => true\n"
            + "}"
        let validatorInvalidTest =
            "test email.valid?/invalid {\n"
            + "    email::valid?(\"missing\")\n"
            + "    => false\n"
            + "}"
        let customerValidTest =
            "test customer.accepts-email/valid {\n"
            + "    customer::accepts-email(customer::new(email = Email::new(\"a@b\")))\n"
            + "    => true\n"
            + "}"
        let customerInvalidTest =
            "test customer.accepts-email/invalid {\n"
            + "    customer::accepts-email(customer::new(email = Email::new(\"missing\")))\n"
            + "    => error REFINEMENT_FAILED\n"
            + "}"
        let speedTest =
            "test speed.roundtrip/unit {\n"
            + "    speed::roundtrip(MetersPerSecond::new(1.0))\n"
            + "    => value MetersPerSecond::new(1.0)\n"
            + "}"
        let customerExample =
            "example customer.accepts-email/valid {\n"
            + "    customer::accepts-email(customer::new(email = Email::new(\"a@b\")))\n"
            + "    => true\n"
            + "}"
        let document =
            [ recordSource
              emailTypeSource
              speedTypeSource
              validatorSource
              customerWordSource
              speedWordSource
              validatorValidTest
              validatorInvalidTest
              customerValidTest
              customerInvalidTest
              speedTest
              customerExample ]
            |> String.concat "\n\n"

        let sourceType (target: Runtime.Engine) name =
            dispatch target "source" [ "type", jstr name ]
        let wordInventory (target: Runtime.Engine) =
            dispatch target "words" []
            |> expectOk "capture project word inventory"
            |> fun response -> response.["data"].["words"].ToJsonString()
        let assertStructuredFailure label (response: JsonObject) =
            check (not (succeeded response)) $"{label} is rejected without fallback: {response.ToJsonString()}"
            check (not (String.IsNullOrWhiteSpace(errorCode response))) $"{label} keeps a structured diagnostic"

        let defined = defineFlowProject engine document [] |> expectOk "stage one complete Flow project document"
        equal "flow" (stringValue (defined.["data"].["frontend"])) "project reports the explicit Flow frontend"
        let declaredWords =
            defined.["data"].["words"].AsArray()
            |> Seq.map (fun item -> stringValue (item.["name"]))
            |> Seq.sort
            |> Seq.toList
        equal [ "customer.accepts-email"; "email.valid?"; "speed.roundtrip" ] declaredWords "project response identifies all authored words"
        let declaredTypes =
            defined.["data"].["types"].AsArray()
            |> Seq.map stringValue
            |> Seq.sort
            |> Seq.toList
        equal [ "Customer"; "Email"; "MetersPerSecond" ] declaredTypes "project response identifies all authored types"

        // Existing names reject the whole incoming document, including its new members.
        let wordsBeforeTypeCollision = wordInventory engine
        let typeCollision =
            "type Customer : String { }\n\n"
            + "word collision.never-staged(value: Int) -> Int {\n"
            + "    effects none\n"
            + "    add(value, 1)\n"
            + "}"
        assertStructuredFailure "a project colliding with an existing type" (defineFlowProject engine typeCollision [])
        equal wordsBeforeTypeCollision (wordInventory engine) "a type collision does not partially stage new generated words or definitions"
        let wordsBeforeWordCollision = wordInventory engine
        let wordCollision =
            "type FreshType : String { }\n\n"
            + "word email.valid?(value: String) -> Bool {\n"
            + "    effects none\n"
            + "    true\n"
            + "}\n\n"
            + "test email.valid?/collision {\n"
            + "    email::valid?(\"x\")\n"
            + "    => true\n"
            + "}"
        assertStructuredFailure "a project colliding with an existing word" (defineFlowProject engine wordCollision [])
        equal wordsBeforeWordCollision (wordInventory engine) "a word collision does not partially stage a new project type"

        for name, count in [ "email.valid?", 2; "customer.accepts-email", 2; "speed.roundtrip", 1 ] do
            assertAllPassed count (dispatch engine "test" [ "word", jstr name ] |> expectOk $"run staged project cases for {name}")

        // Type selection uses the existing commit closure. An unrelated validator,
        // record and user word remain candidates when only the Float wrapper is selected.
        commit engine "commit" "MetersPerSecond" [] |> expectOk "commit the selected nominal Float type" |> ignore
        let firstManifest = Storage.load store |> Result.defaultWith (fun problem -> failwith $"load type-only manifest: {problem.Code}: {problem.Message}")
        let firstAuthority = firstManifest.Manifest |> Option.defaultWith (fun () -> failwith "type-only commit did not create a manifest")
        equal 3 firstAuthority.FormatVersion "Flow type publication selects manifest v3"
        equal [ "MetersPerSecond" ] (firstAuthority.Types |> List.map _.Name) "type-only commit publishes only its selected type closure"
        equal [] firstAuthority.Revisions "type-only commit does not fabricate word revisions"
        check ((findWord (dispatch engine "words" [] |> expectOk "inspect remaining candidates") "email.valid?").["status"].GetValue<string>() = "candidate") "unrelated Flow validator remains a candidate after selected type commit"

        commit engine "commit" "Email" [] |> expectOk "commit refined Email and its validator closure" |> ignore
        commit engine "commit" "Customer" [] |> expectOk "commit Customer and its referenced Email type" |> ignore
        commit engine "commit" "customer.accepts-email" [] |> expectOk "commit the dependent record word with its cases" |> ignore
        commit engine "commit" "speed.roundtrip" [] |> expectOk "commit the nominal wrapper word with its case" |> ignore

        assertAllPassed 2 (dispatch engine "test" [ "word", jstr "email.valid?" ] |> expectOk "run committed scalar validator cases")
        assertAllPassed 2 (dispatch engine "test" [ "word", jstr "customer.accepts-email" ] |> expectOk "run committed record word cases")
        assertAllPassed 1 (dispatch engine "test" [ "word", jstr "speed.roundtrip" ] |> expectOk "run committed nominal wrapper case")
        let example = dispatch engine "example" [ "word", jstr "customer.accepts-email"; "caseName", jstr "valid" ] |> expectOk "run inline project example"
        check (boolValue (example.["data"].["results"].[0].["passed"])) "inline project example passes before reload"

        let committed = Storage.load store |> Result.defaultWith (fun problem -> failwith $"load Flow type manifest: {problem.Code}: {problem.Message}")
        let manifest = committed.Manifest |> Option.defaultWith (fun () -> failwith "Flow project manifest is missing")
        equal 3 manifest.FormatVersion "mixed Flow project remains in manifest v3"
        equal [ "Customer"; "Email"; "MetersPerSecond" ] (manifest.Types |> List.map _.Name |> List.sort) "selected type commits persist all required type sources"
        let emailType = manifest.Types |> List.find (fun item -> item.Name = "Email")
        let customerType = manifest.Types |> List.find (fun item -> item.Name = "Customer")
        let speedType = manifest.Types |> List.find (fun item -> item.Name = "MetersPerSecond")
        equal { Frontend = SourceFrontend.Flow; Version = 1 } emailType.SourceFormat "Email source is tagged Flow/1"
        equal { Frontend = SourceFrontend.Flow; Version = 1 } customerType.SourceFormat "record source is tagged Flow/1"
        equal { Frontend = SourceFrontend.Flow; Version = 1 } speedType.SourceFormat "nominal wrapper source is tagged Flow/1"
        equal (digest emailTypeSource) emailType.Definition.Hash "Email type object hash covers exact authored declaration bytes"
        equal (digest recordSource) customerType.Definition.Hash "record type object hash covers exact authored declaration bytes"
        equal emailTypeSource (Storage.readSource store emailType.Definition |> Result.defaultWith (fun problem -> failwith problem.Message)) "Email type source object preserves its exact authored bytes"
        equal recordSource (Storage.readSource store customerType.Definition |> Result.defaultWith (fun problem -> failwith problem.Message)) "record type source object preserves its exact authored bytes"
        let validatorId = getWordId engine "email.valid?"
        equal (Some(StoredCallTarget.UserWord validatorId)) emailType.ValidatorTarget "scalar validator target metadata binds the stable user word ID"
        equal None customerType.ValidatorTarget "record type source carries no scalar validator identity"
        equal None speedType.ValidatorTarget "unvalidated scalar source carries no validator identity"
        check (manifest.Revisions |> List.exists (fun revision -> revision.WordId = validatorId && revision.Name = "email.valid?")) "the validator is persisted as its own authored Flow word revision"
        check (manifest.Revisions |> List.forall (fun revision -> not ([ "Customer"; "Email"; "MetersPerSecond" ] |> List.contains revision.Name))) "types remain immutable sources rather than fabricated word revisions"

        let aggregate = committed.ProjectSource |> Option.defaultWith (fun () -> failwith "Flow project aggregate source is missing")
        check (aggregate.Contains("// frontend: flow/1", StringComparison.Ordinal)) "canonical aggregate export identifies Flow declarations"
        check (aggregate.Contains(FlowSource.renderScalar (FlowParser.parseDocument "<type>" emailTypeSource |> Result.defaultWith (fun diagnostic -> failwith (Diagnostics.render diagnostic))).Scalars.Head, StringComparison.Ordinal)) "canonical aggregate export renders the Flow scalar declaration"
        check (aggregate <> document) "canonical aggregate export is separate from the original whole-document input"

        let reloaded = Runtime.Engine(project, Set.empty, "2038-04-05T06:07:08Z")
        equal validatorId (getWordId reloaded "email.valid?") "reload preserves scalar validator stable identity"
        equal emailTypeSource (stringValue (sourceType reloaded "Email" |> expectOk "read authored Flow scalar source after reload" |> fun response -> response.["data"])) "source(type) returns the exact authored scalar declaration"
        equal recordSource (stringValue (sourceType reloaded "Customer" |> expectOk "read authored Flow record source after reload" |> fun response -> response.["data"])) "source(type) returns the exact authored record declaration"
        equal "MetersPerSecond" (stringValue (evalFlow reloaded "speed::roundtrip(MetersPerSecond::new(1.0))" |> expectOk "evaluate a reloaded nominal Float wrapper" |> fun response -> response.["data"].["stackTypes"].[0])) "nominal wrapper remains distinct from its Float base after reload"
        equal "1" (stringValue (evalFlow reloaded "MetersPerSecond::value(MetersPerSecond::new(1.0))" |> expectOk "construct and explicitly unwrap the reloaded nominal wrapper" |> fun response -> response.["data"].["stack"].[0])) "nominal payload is available only through the generated accessor"
        assertAllPassed 2 (dispatch reloaded "test" [ "word", jstr "customer.accepts-email" ] |> expectOk "run reloaded dependent record cases")
        expectError "REFINEMENT_FAILED"
            (evalFlow reloaded "customer::accepts-email(customer::new(email = Email::new(\"missing\")))")
        |> ignore
        let reloadedExample = dispatch reloaded "example" [ "word", jstr "customer.accepts-email"; "caseName", jstr "valid" ] |> expectOk "run reloaded project example"
        check (boolValue (reloadedExample.["data"].["results"].[0].["passed"])) "reloaded example still executes against the nominal record"
        let cli = cliEval project "customer::accepts-email(customer::new(email = Email::new(\"a@b\")))" |> expectOk "fresh-process CLI loads the Flow project"
        equal "true" (stringValue (cli.["data"].["stack"].[0])) "fresh-process CLI resolves the Flow validator and generated record vocabulary"

        // Failed later declarations, nominal payload mismatches, invalid validators,
        // and temporary typed documents all leave the candidate vocabulary intact.
        let assertRejectedWithoutPartialStage label (target: Runtime.Engine) source extra =
            let before = wordInventory target
            let response = defineFlowProject target source extra
            assertStructuredFailure label response
            equal before (wordInventory target) $"{label} leaves no partial words, generated words, or type vocabulary"
            response

        let lateFailureEngine = Runtime.Engine(Path.Combine(root, "flow-project-late-failure"), Set.empty)
        let lateFailureDocument =
            "type EarlyType : String { }\n\n"
            + "word early.good(value: Int) -> Int {\n"
            + "    effects none\n"
            + "    add(value, 1)\n"
            + "}\n\n"
            + "test early.good/basic {\n"
            + "    early::good(1)\n"
            + "    => value early::good(1)\n"
            + "}\n\n"
            + "word later.bad(value: Int) -> Int {\n"
            + "    effects none\n"
            + "    missing::word(value)\n"
            + "}"
        assertRejectedWithoutPartialStage "a later invalid project word" lateFailureEngine lateFailureDocument [] |> ignore

        let wrongPayloadEngine = Runtime.Engine(Path.Combine(root, "flow-project-wrong-nominal-payload"), Set.empty)
        let wrongPayloadDocument =
            "type MetersPerSecond : Float { }\n\n"
            + "word speed.invalid(value: Int) -> MetersPerSecond {\n"
            + "    effects none\n"
            + "    MetersPerSecond::new(value)\n"
            + "}"
        let wrongPayload = assertRejectedWithoutPartialStage "a primitive payload passed to the nominal constructor" wrongPayloadEngine wrongPayloadDocument []
        equal "FLOW_ARGUMENT_TYPE" (errorCode wrongPayload) "nominal constructor rejects Int where its Float payload is required"

        let badSignatureEngine = Runtime.Engine(Path.Combine(root, "flow-project-bad-validator-signature"), Set.empty)
        let badSignatureDocument =
            "type Email : String { validate email::valid?; }\n\n"
            + "word email.valid?(value: String) -> Int {\n"
            + "    effects none\n"
            + "    1\n"
            + "}"
        let badSignature = assertRejectedWithoutPartialStage "a non-Bool scalar validator" badSignatureEngine badSignatureDocument []
        equal "TYPE_VALIDATOR_SIGNATURE" (errorCode badSignature) "validator must have the base-to-Bool signature"

        let effectfulValidatorEngine = Runtime.Engine(Path.Combine(root, "flow-project-effectful-validator"), Set.empty, "2038-04-05T06:07:08Z")
        let effectfulValidatorDocument =
            "type Email : String { validate email::valid?; }\n\n"
            + "word email.valid?(value: String) -> Bool {\n"
            + "    effects clock.read\n"
            + "    string::contains(clock::now(), value)\n"
            + "}"
        let effectfulValidator = assertRejectedWithoutPartialStage "an effectful scalar validator" effectfulValidatorEngine effectfulValidatorDocument []
        equal "TYPE_VALIDATOR_EFFECT" (errorCode effectfulValidator) "effectful scalar validator is rejected by the purity guard"

        let temporaryEngine = Runtime.Engine(Path.Combine(root, "flow-project-temporary-types"), Set.empty)
        let temporaryDocument = "type TemporaryEmail : String { }"
        assertRejectedWithoutPartialStage "a temporary project containing types" temporaryEngine temporaryDocument [ "temporary", jbool true ] |> ignore

        let discardProject = Path.Combine(root, "flow-project-discard")
        let discardEngine = Runtime.Engine(discardProject, Set.empty)
        let discardDocument =
            "type Ephemeral : String { }\n\n"
            + "word ephemeral.echo(value: Ephemeral) -> Ephemeral {\n"
            + "    effects none\n"
            + "    Ephemeral::new(Ephemeral::value(value))\n"
            + "}\n\n"
            + "test ephemeral.echo/basic {\n"
            + "    ephemeral::echo(Ephemeral::new(\"ok\"))\n"
            + "    => value Ephemeral::new(\"ok\")\n"
            + "}"
        defineFlowProject discardEngine discardDocument [] |> expectOk "stage a typed document for discard" |> ignore
        dispatch discardEngine "discard" [ "word", jstr "ephemeral.echo" ] |> expectOk "discard the dependent Flow word before its type" |> ignore
        dispatch discardEngine "discard" [ "word", jstr "Ephemeral" ] |> expectOk "discard the candidate scalar type" |> ignore
        assertStructuredFailure "discarded Flow type source" (sourceType discardEngine "Ephemeral")
        let discardReload = Runtime.Engine(discardProject, Set.empty)
        assertStructuredFailure "discarded Flow type after fresh reload" (sourceType discardReload "Ephemeral")

        let beforeTask = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        let beforeTaskManifest = beforeTask.Manifest |> Option.defaultWith (fun () -> failwith "committed Flow type authority is missing")
        dispatch reloaded "task.begin" [ "goal", jstr "rollback a committed Flow type" ] |> expectOk "begin type-source rollback task" |> ignore
        defineFlowProject reloaded "type TaskOnly : Float { }" [] |> expectOk "stage a new Flow type inside a task" |> ignore
        commit reloaded "commit" "TaskOnly" [] |> expectOk "commit a Flow type inside a task" |> ignore
        dispatch reloaded "task.abort" [] |> expectOk "abort the committed Flow type task" |> ignore
        let afterTask = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        equal beforeTask.ManifestHash afterTask.ManifestHash "task abort restores the exact Flow type manifest authority"
        equal beforeTaskManifest.Types afterTask.Manifest.Value.Types "task abort restores exact type SourceRefs and stable validator metadata"
        assertStructuredFailure "task-aborted Flow type source" (sourceType reloaded "TaskOnly")

        dispatch reloaded "snapshot.save" [ "name", jstr "flow-type-baseline" ] |> expectOk "save committed Flow type sources" |> ignore
        let snapshotBaseline = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        defineFlowProject reloaded "type SnapshotOnly : String { }" [] |> expectOk "stage a Flow type after snapshot" |> ignore
        commit reloaded "commit" "SnapshotOnly" [] |> expectOk "commit a Flow type after snapshot" |> ignore
        let snapshotChanged = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        check (snapshotChanged.ManifestHash <> snapshotBaseline.ManifestHash) "committing another Flow type advances durable authority after snapshot"
        let snapshotReload = Runtime.Engine(project, Set.empty, "2040-01-01T00:00:00Z")
        dispatch snapshotReload "snapshot.load" [ "name", jstr "flow-type-baseline" ] |> expectOk "restore named Flow type snapshot" |> ignore
        let afterSnapshot = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        equal snapshotBaseline.ManifestHash afterSnapshot.ManifestHash "snapshot restore reinstates exact Flow type authority"
        equal emailTypeSource (stringValue (sourceType snapshotReload "Email" |> expectOk "read Flow scalar source after snapshot restore" |> fun response -> response.["data"])) "snapshot restore retains exact authored Flow type bytes"
        assertStructuredFailure "snapshot-removed Flow type source" (sourceType snapshotReload "SnapshotOnly")

    let private testRecordValidatorRuntimeAndPersistence root =
        let project = Path.Combine(root, "flow-record-validator-runtime")
        let engine = Runtime.Engine(project, Set.empty, "2042-03-04T05:06:07Z")
        let store = Storage.create project
        let targetId = "10000000-0000-0000-0000-000000000031"
        let matchingCustomer =
            "customer::new(id = CustomerId::new(\"10000000-0000-0000-0000-000000000031\"))"
        let mismatchCustomer =
            "customer::new(id = CustomerId::new(\"20000000-0000-0000-0000-000000000031\"))"
        let noneLookup =
            $"customer::lookup-valid?(validatedCustomerLookup::new(target = CustomerId::new(\"{targetId}\"), found = option::none<Customer>()))"
        let matchingLookup =
            $"customer::lookup-valid?(validatedCustomerLookup::new(target = CustomerId::new(\"{targetId}\"), found = option::some<Customer>({matchingCustomer})))"
        let invalidConstructor =
            $"validatedCustomerLookup::new(target = CustomerId::new(\"{targetId}\"), found = option::some<Customer>({mismatchCustomer}))"
        let recordSource =
            "record ValidatedCustomerLookup {\n"
            + "    field target: CustomerId;\n"
            + "    field found: Option<Customer>;\n"
            + "    validate customer::lookup-valid?;\n"
            + "}"
        let validatorSource =
            "fn customer.lookup-valid?(value: ValidatedCustomerLookup) -> Bool {\n"
            + "    doc \"A found customer must have the requested ID; no match is a valid lookup result.\"\n"
            + "    match value.found {\n"
            + "        some customer => { customer.id == value.target }\n"
            + "        none => { true }\n"
            + "    }\n"
            + "}"
        let tests =
            [ "test customer.lookup-valid?/none {\n    " + noneLookup + "\n    => true\n}"
              "test customer.lookup-valid?/matching {\n    " + matchingLookup + "\n    => true\n}"
              "test customer.lookup-valid?/mismatch {\n    " + invalidConstructor + "\n    => error RECORD_VALIDATION_FAILED\n}" ]
        let supportTypes =
            "type CustomerId : String { }\n\n"
            + "record Customer { field id: CustomerId; }"
        let source = String.concat "\n\n" ([ supportTypes; recordSource; validatorSource ] @ tests)
        defineFlowProject engine source [ "syntaxVersion", jint 2 ]
        |> expectOk "define the Flow/2 complete-record CustomerLookup invariant"
        |> ignore

        assertAllPassed 3 (dispatch engine "test" [ "word", jstr "customer.lookup-valid?" ] |> expectOk "run none, matching, and rejecting record-validator cases")
        let coverage =
            dispatch engine "describe" [ "word", jstr "customer.lookup-valid?" ]
            |> expectOk "inspect record-validator finite return coverage"
            |> fun response -> response.["data"].["coverage"].["finiteCoverage"]
        check (coverage.["targetInvocations"].GetValue<int>() > 0) "the validator is invoked by its generated record constructor"
        equal true (boolValue coverage.["complete"]) "the validator's true and false Bool returns are observed"
        let observedReturns = jsonArrayStrings coverage.["returns"].[0].["observed"]
        check (observedReturns |> List.contains "true") "a valid none/matching lookup observes the validator's true return"
        check (observedReturns |> List.contains "false") "the expected constructor rejection retains the validator's completed false return"

        commit engine "commit" "customer.lookup-valid?" [ "library", jbool true ]
        |> expectOk "qualify the predicate itself from its true and expected rejected-constructor tests"
        |> ignore
        let qualifiedValidator = dispatch engine "describe" [ "word", jstr "customer.lookup-valid?" ] |> expectOk "inspect predicate maturity after qualification"
        equal "library" (stringValue qualifiedValidator.["data"].["maturity"]) "the complete-record predicate itself reaches library maturity"
        let manifest = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message) |> fun loaded -> loaded.Manifest.Value
        let typeSource = manifest.Types |> List.find (fun item -> item.Name = "ValidatedCustomerLookup")
        let validatorId = getWordId engine "customer.lookup-valid?"
        equal (Some(StoredCallTarget.UserWord validatorId)) typeSource.ValidatorTarget "record type persists the predicate's stable WordId target"
        equal { Frontend = SourceFrontend.Flow; Version = 2 } typeSource.SourceFormat "complete-record source remains Flow/2"
        equal recordSource (Storage.readSource store typeSource.Definition |> Result.defaultWith (fun problem -> failwith problem.Message)) "validated record source is retained byte-for-byte"
        let predicateRevision = manifest.Revisions |> List.find (fun revision -> revision.Name = "customer.lookup-valid?")
        let mismatchBindings =
            predicateRevision.CallBindings
            |> List.filter (fun binding -> binding.CaseName = Some "mismatch" && binding.BodyRole = StoredCallBodyRole.Actual)
        let recordConstructorBindings =
            mismatchBindings
            |> List.filter (fun binding ->
                match binding.Target with
                | StoredCallTarget.GeneratedWord identity -> identity.EndsWith("validatedCustomerLookup.new", StringComparison.Ordinal)
                | _ -> false)
        equal 1 recordConstructorBindings.Length "the rejection test persists its authored generated record-constructor call"
        check
            (recordConstructorBindings.Head.RequestedName.Contains("validatedCustomerLookup", StringComparison.Ordinal))
            "the authored mismatch-test call binding retains the constructor request"
        check
            (mismatchBindings |> List.forall (fun binding -> binding.Target <> StoredCallTarget.UserWord validatorId))
            "the implicit predicate invocation does not become an authored Flow binding"

        let dependentConstructors =
            dispatch engine "search-dependency" [ "word", jstr "customer.lookup-valid?" ]
            |> expectOk "search generated constructors that depend on the predicate"
            |> fun response -> jsonArrayStrings response.["data"].["words"]
        check (dependentConstructors |> List.contains "validatedCustomerLookup.new") "Discovery exposes the generated constructor's validator edge"
        let transitiveCallers =
            dispatch engine "transitive-callers" [ "word", jstr "customer.lookup-valid?" ]
            |> expectOk "inspect callers of the persisted predicate"
            |> fun response -> jsonArrayStrings response.["data"].["callers"]
        check (transitiveCallers |> List.contains "validatedCustomerLookup.new") "Discovery exposes the generated constructor as a validator caller"

        let reloaded = Runtime.Engine(project, Set.empty, "2042-03-04T05:06:07Z")
        equal validatorId (getWordId reloaded "customer.lookup-valid?") "record validator target WordId survives a fresh Engine load"
        equal recordSource (stringValue (dispatch reloaded "source" [ "type", jstr "ValidatedCustomerLookup" ] |> expectOk "read validated record source after reload" |> fun response -> response.["data"]))
            "fresh load retains the exact validated record source"
        equal "library" (stringValue (dispatch reloaded "describe" [ "word", jstr "customer.lookup-valid?" ] |> expectOk "inspect predicate maturity after reload" |> fun response -> response.["data"].["maturity"]))
            "fresh load retains the validator's library maturity"
        assertAllPassed 3 (dispatch reloaded "test" [ "word", jstr "customer.lookup-valid?" ] |> expectOk "rerun record-validator tests after reload")
        let invalidAfterReload = evalFlow reloaded invalidConstructor |> expectError "RECORD_VALIDATION_FAILED"
        equal [ "validator returns true" ] (jsonArrayStrings invalidAfterReload.["error"].["expected"]) "record validation failure states the required predicate result"
        equal [ "false" ] (jsonArrayStrings invalidAfterReload.["error"].["actual"]) "record validation failure reports the actual false result"

        let beforeReplacement = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message) |> fun loaded -> loaded.ManifestHash
        let replacementSource =
            "fn customer.lookup-valid?(value: ValidatedCustomerLookup) -> Bool { true }\n\n"
            + $"test customer.lookup-valid?/replacement {{ {noneLookup} => true }}"
        defineFlowProject engine replacementSource
            [ "syntaxVersion", jint 2; "replace", jbool true; "expectedRevision", jint 1 ]
        |> expectError "TYPE_VALIDATOR_FROZEN"
        |> ignore
        equal beforeReplacement (Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message) |> fun loaded -> loaded.ManifestHash)
            "rejected predicate replacement leaves the durable validator binding unchanged"
        dispatch engine "rename" [ "word", jstr "customer.lookup-valid?"; "to", jstr "customer.lookup-valid-renamed"; "actor", jstr "client" ]
        |> expectError "TYPE_VALIDATOR_FROZEN"
        |> ignore

        let rejectedFlow label expectedCode document : JsonObject =
            let invalidEngine = Runtime.Engine(Path.Combine(root, "flow-record-validator-" + label), Set.empty)
            let response = defineFlowProject invalidEngine document [ "syntaxVersion", jint 2 ]
            equal expectedCode (errorCode response) $"{label} record validator is rejected"
            response
        rejectedFlow "unknown" "TYPE_VALIDATOR_UNKNOWN_WORD"
            "record Lookup { field value: Int; validate absent::predicate?; }"
        |> ignore
        rejectedFlow "signature" "TYPE_RECORD_VALIDATOR_SIGNATURE"
            ("record Lookup { field value: Int; validate lookup::valid?; }\n\n"
             + "fn lookup.valid?(value: Int) -> Bool { true }")
        |> ignore
        rejectedFlow "effect" "TYPE_RECORD_VALIDATOR_EFFECT"
            ("record Lookup { field value: Int; validate lookup::valid?; }\n\n"
             + "fn lookup.valid?(value: Lookup) -> Bool {\n    effects fs.read\n    file::exists?(\"missing\")\n}")
        |> ignore
        rejectedFlow "duplicate" "FLOW_RECORD_VALIDATOR_DUPLICATE"
            "record Lookup { field value: Int; validate lookup::first?; validate lookup::second?; }"
        |> ignore

        let directCycle =
            "record RecursiveLookup { field value: Int; validate recursive::valid?; }\n\n"
            + "fn recursive.valid?(value: RecursiveLookup) -> Bool { recursiveLookup::new(value = value.value) == value }"
        let assertCycleContext label (response: JsonObject) =
            let paths = jsonArrayStrings response.["error"].["actual"]
            let closesCycle (path: string) =
                let nodes = path.Split([| " -> " |], StringSplitOptions.None) |> Array.toList
                nodes.Length >= 2 && nodes.Head = List.last nodes && (nodes |> List.forall (fun node -> node.StartsWith("word:", StringComparison.Ordinal)))
            check
                (paths |> List.exists closesCycle)
                $"{label} recursive-call diagnostic includes a closed word cycle path"
        let directCycleResponse = rejectedFlow "direct-cycle" "IR_RECURSIVE_CALL_GRAPH" directCycle
        assertCycleContext "direct" directCycleResponse
        let indirectCycle =
            "record RecursiveLookup { field value: Int; validate recursive::valid?; }\n\n"
            + "fn recursive.build(value: Int) -> RecursiveLookup { recursiveLookup::new(value = value) }\n\n"
            + "fn recursive.valid?(value: RecursiveLookup) -> Bool { recursive::build(value.value) == value }"
        let indirectCycleResponse = rejectedFlow "indirect-cycle" "IR_RECURSIVE_CALL_GRAPH" indirectCycle
        assertCycleContext "indirect" indirectCycleResponse

        let stackProject = Path.Combine(root, "stack-record-validator-target")
        let stackEngine = Runtime.Engine(stackProject, Set.empty, "2042-03-04T05:06:07Z")
        let stackSource =
            "record CheckedNumber\n"
            + "    validate checkedNumber.valid?\n"
            + "    field value Int\n"
            + "end\n\n"
            + "word checkedNumber.valid? : CheckedNumber -> Bool\n"
            + "    effects none\n"
            + "    drop true\n"
            + "end\n\n"
            + "test checkedNumber.valid?/accept\n"
            + "    1 checkedNumber.new checkedNumber.valid?\n"
            + "    expect true\n"
            + "end"
        defineStack stackEngine stackSource [] |> expectOk "define a Stack/1 validated record and its predicate" |> ignore
        commit stackEngine "commit" "CheckedNumber" [ "library", jbool true ]
        |> expectOk "commit Stack/1 record validator with stable target metadata"
        |> ignore
        let stackStore = Storage.create stackProject
        let stackLoaded = Storage.load stackStore |> Result.defaultWith (fun problem -> failwith problem.Message)
        let stackManifest = stackLoaded.Manifest.Value
        let stackType = stackManifest.Types |> List.find (fun item -> item.Name = "CheckedNumber")
        let stackValidatorId = getWordId stackEngine "checkedNumber.valid?"
        equal { Frontend = SourceFrontend.Stack; Version = 1 } stackType.SourceFormat "validated Stack record persists its frontend and version"
        equal (Some(StoredCallTarget.UserWord stackValidatorId)) stackType.ValidatorTarget "validated Stack record persists its exact stable validator target"
        assertAllPassed 1 (dispatch stackEngine "test" [ "word", jstr "checkedNumber.valid?" ] |> expectOk "run Stack record validator after commit")

        let stackReferences =
            [ stackManifest.ProjectSource ]
            @ (stackManifest.Types |> List.map _.Definition)
            @ (stackManifest.Revisions |> List.collect (fun revision -> revision.Definition :: revision.Tests @ revision.Examples))
            |> List.distinct
        let stackObjects =
            stackReferences
            |> List.map (fun reference ->
                let content = Storage.readSource stackStore reference |> Result.defaultWith (fun problem -> failwith problem.Message)
                Storage.sourceObject reference.Kind content)
        let export = stackLoaded.ProjectSource.Value
        let rejectStackMetadata label expectedCode replacement =
            let candidateProject = Path.Combine(root, "stack-record-validator-" + label)
            let candidateStore = Storage.create candidateProject
            let forged =
                { stackManifest with
                    Types = stackManifest.Types |> List.map (fun item -> if item.Name = "CheckedNumber" then { item with ValidatorTarget = replacement } else item) }
            Storage.commit candidateStore 0L forged stackObjects export
            |> Result.defaultWith (fun problem -> failwith $"store well-shaped {label} target metadata: {problem.Code}: {problem.Message}")
            |> ignore
            try
                Runtime.Engine(candidateProject, Set.empty, "2042-03-04T05:06:07Z") |> ignore
                failwith $"fresh Engine accepted {label} record-validator metadata"
            with
            | LanguageException diagnostic -> equal expectedCode diagnostic.Code $"fresh Engine rejects {label} record-validator metadata"
        rejectStackMetadata "missing" "TYPE_VALIDATOR_TARGET_MISSING" None
        rejectStackMetadata "mismatched" "TYPE_VALIDATOR_TARGET_MISMATCH" (Some(StoredCallTarget.GeneratedWord "generated-forged-validator-target"))

        let intervalProject = Path.Combine(root, "flow-record-validator-interval-smoke")
        let intervalEngine = Runtime.Engine(intervalProject, Set.empty, "2042-03-04T05:06:07Z")
        let intervalSource =
            "record Interval { field start: Int; field finish: Int; validate interval::valid?; }\n\n"
            + "fn interval.valid?(value: Interval) -> Bool {\n"
            + "    doc \"An interval finishes at or after its start.\"\n"
            + "    int::less-or-equal(value.start, value.finish)\n"
            + "}\n\n"
            + "test interval.valid?/ordered { interval::valid?(interval::new(start = 1, finish = 2)) => true }\n"
            + "test interval.valid?/reversed { interval::new(start = 2, finish = 1) => error RECORD_VALIDATION_FAILED }"
        defineFlowProject intervalEngine intervalSource [ "syntaxVersion", jint 2 ]
        |> expectOk "run the documented Interval record-validator example"
        |> ignore
        assertAllPassed 2 (dispatch intervalEngine "test" [ "word", jstr "interval.valid?" ] |> expectOk "run Interval valid and expected-error tests")

    let private testEffectCountAssertions root =
        let project = Path.Combine(root, "effect-count-assertions")
        let engine = Runtime.Engine(project, Set.empty, "2041-02-03T04:05:06Z")
        let source =
            """fn marker.ensure(path: String) -> String {
    effects fs.read, fs.write
    if file::exists?(path) { file::read(path) } else { file::write(path, "queued"); "queued" }
}

fn marker.read(path: String) -> String {
    effects fs.read, fs.write
    marker::ensure(path)
}

fn marker.required(path: String) -> String {
    effects fs.read
    file::read(path)
}

fn marker.mutant(path: String) -> String {
    effects fs.read, fs.write
    file::write("extra", "noise")
    file::read(path)
}

test marker.read/existing {
    file::write("existing", "held")
    marker::read("existing")
    => "held" effects { fs.read: 2; fs.write: 0; }
}

test marker.read/missing {
    marker::read("new")
    => "queued" effects { fs.read: 1; fs.write: 1; }
}

test marker.read/repeated {
    file::write("repeated", "held")
    let first = marker::read("repeated")
    marker::read("repeated")
    => "held" effects { fs.read: 4; fs.write: 0; }
}

test marker.read/not-invoked {
    "idle"
    => "idle" effects {}
}

test marker.required/fault-counted {
    marker::required("missing")
    => error EFFECT_FILE_NOT_FOUND effects { fs.read: 1; }
}

test marker.required/error-does-not-hide-mismatch {
    marker::required("missing")
    => error EFFECT_FILE_NOT_FOUND effects {}
}

test marker.required/legacy-shape {
    file::write("legacy", "held")
    marker::required("legacy")
    => "held"
}

test marker.mutant/value-and-effect-failures {
    file::write("mutant", "held")
    marker::mutant("mutant")
    => "wrong" effects { fs.read: 1; fs.write: 0; }
}"""
        defineFlowProject engine source [ "syntaxVersion", jint 2 ]
        |> expectOk "stage Flow/2 words and effect-count tests"
        |> ignore

        let testResult (runtime: Runtime.Engine) owner caseName =
            let response = dispatch runtime "test" [ "word", jstr owner ] |> expectOk ("run " + owner + " effect-count tests")
            response.["data"].["results"].AsArray()
            |> Seq.find (fun item -> stringValue item.["name"] = caseName)
            |> fun item -> item.AsObject()

        let existing = testResult engine "marker.read" "existing"
        check (boolValue existing.["passed"]) "existing marker assertion excludes setup write and includes nested helper reads"
        let existingEffects = existing.["effectAssertion"]
        equal 2 (existingEffects.["actual"].["fs.read"].GetValue<int>()) "existing marker read count includes exists? plus read"
        equal 0 (existingEffects.["actual"].["fs.write"].GetValue<int>()) "setup write is outside the target scope"
        equal 1 (existingEffects.["targetInvocationCount"].GetValue<int>()) "target invocation count is recorded"
        check
            (not (String.IsNullOrWhiteSpace(stringValue existingEffects.["span"].["file"]))
             && existingEffects.["span"].["line"].GetValue<int>() > 0)
            "effect assertion annotation span is structured"

        let missing = testResult engine "marker.read" "missing"
        check (boolValue missing.["passed"]) "missing marker assertion counts the attempted write and existence read"
        equal 1 (missing.["effectAssertion"].["actual"].["fs.read"].GetValue<int>()) "missing marker performs one read-category provider call"
        equal 1 (missing.["effectAssertion"].["actual"].["fs.write"].GetValue<int>()) "missing marker performs one write-category provider call"

        let repeated = testResult engine "marker.read" "repeated"
        check (boolValue repeated.["passed"]) "repeated target calls aggregate exact effects"
        equal 4 (repeated.["effectAssertion"].["actual"].["fs.read"].GetValue<int>()) "two target calls each include nested helper exists/read pair"
        equal 2 (repeated.["effectAssertion"].["targetInvocationCount"].GetValue<int>()) "target invocation count aggregates repeated calls"

        let notInvoked = testResult engine "marker.read" "not-invoked"
        check (not (boolValue notInvoked.["passed"])) "an empty exact map still requires the target to run"
        equal "TEST_EFFECT_ASSERTION_FAILED" (stringValue notInvoked.["errorCode"]) "zero target invocations fail with the stable diagnostic"
        equal 0 (notInvoked.["effectAssertion"].["targetInvocationCount"].GetValue<int>()) "zero-invocation result is observable"

        let faultCounted = testResult engine "marker.required" "fault-counted"
        check (boolValue faultCounted.["passed"]) "matching expected provider error passes with its attempted call counted"
        equal 1 (faultCounted.["effectAssertion"].["actual"].["fs.read"].GetValue<int>()) "provider calls count before a missing-file error"

        let errorMismatch = testResult engine "marker.required" "error-does-not-hide-mismatch"
        check (not (boolValue errorMismatch.["passed"])) "an expected runtime error cannot swallow an effect-count mismatch"
        equal "TEST_EFFECT_ASSERTION_FAILED" (stringValue errorMismatch.["errorCode"]) "effect mismatch stays primary when the expected error matched"
        equal "TEST_EFFECT_ASSERTION_FAILED" (stringValue errorMismatch.["effectAssertion"].["error"].["code"]) "effect mismatch remains structured"
        equal 1 (errorMismatch.["effectAssertion"].["actual"].["fs.read"].GetValue<int>()) "faulted call remains in the actual count map"

        let legacy = testResult engine "marker.required" "legacy-shape"
        check (boolValue legacy.["passed"]) "Flow/2 tests without a suffix retain return-value behavior"
        check (not (legacy.ContainsKey "effectAssertion")) "tests without a suffix retain the legacy JSON shape"

        let bothFailures = testResult engine "marker.mutant" "value-and-effect-failures"
        check (not (boolValue bothFailures.["passed"])) "wrong return and extra write both fail"
        equal "TEST_ASSERTION_FAILED" (stringValue bothFailures.["errorCode"]) "existing value diagnostic remains primary"
        equal "TEST_EFFECT_ASSERTION_FAILED" (stringValue bothFailures.["effectAssertion"].["error"].["code"]) "effect failure is preserved beside the value diagnostic"
        equal 1 (bothFailures.["effectAssertion"].["actual"].["fs.write"].GetValue<int>()) "extra write appears in structured actual counts"

        let persistenceProject = Path.Combine(root, "effect-count-persist-rewrite")
        let persistenceEngine = Runtime.Engine(persistenceProject, Set.empty, "2041-02-03T04:05:06Z")
        let persistenceSource =
            """fn persist.read(path: String) -> String {
    effects fs.read
    file::read(path)
}

test persist.read/exact-count {
    file::write("persistent", "held")
    persist::read("persistent")
    => "held" effects { fs.read: 1; fs.write: 0; }
}"""
        defineFlowProject persistenceEngine persistenceSource [ "syntaxVersion", jint 2 ]
        |> expectOk "stage durable Flow/2 effect assertion"
        |> ignore
        commit persistenceEngine "commit" "persist.read" [ "library", jbool true ]
        |> expectOk "commit the annotated Flow/2 function"
        |> ignore
        let persistenceStore = Storage.create persistenceProject
        let beforeRename = Storage.load persistenceStore |> Result.defaultWith (fun problem -> failwith problem.Message)
        let beforeManifest = beforeRename.Manifest |> Option.defaultWith (fun () -> failwith "effect assertion fixture did not create a manifest")
        let beforeHead = beforeManifest.Words |> List.find (fun item -> item.CurrentName = "persist.read")
        let beforeRevision = beforeManifest.Revisions |> List.find (fun item -> item.WordId = beforeHead.WordId && item.Revision = beforeHead.CurrentRevision)
        let beforeTest = beforeRevision.Tests |> List.map (Storage.readSource persistenceStore >> Result.defaultWith (fun problem -> failwith problem.Message)) |> List.exactlyOne
        check (beforeTest.Contains("effects { fs.read: 1; fs.write: 0; }", StringComparison.Ordinal)) "durable test source retains the exact assertion suffix"
        let persistentId = getWordId persistenceEngine "persist.read"
        dispatch persistenceEngine "rename" [ "word", jstr "persist.read"; "to", jstr "persist.load" ]
        |> expectOk "rename the annotated Flow/2 owner"
        |> ignore
        equal persistentId (getWordId persistenceEngine "persist.load") "rename retains the stable owner identity"
        let afterRename = Storage.load persistenceStore |> Result.defaultWith (fun problem -> failwith problem.Message)
        let afterManifest = afterRename.Manifest |> Option.defaultWith (fun () -> failwith "renamed effect assertion manifest is missing")
        let afterHead = afterManifest.Words |> List.find (fun item -> item.CurrentName = "persist.load")
        let afterRevision = afterManifest.Revisions |> List.find (fun item -> item.WordId = afterHead.WordId && item.Revision = afterHead.CurrentRevision)
        let rewrittenTest = afterRevision.Tests |> List.map (Storage.readSource persistenceStore >> Result.defaultWith (fun problem -> failwith problem.Message)) |> List.exactlyOne
        check (rewrittenTest.Contains("effects {\n        fs.read: 1\n        fs.write: 0\n    }", StringComparison.Ordinal)) "rename preserves exact effect counts in canonical newline syntax"
        let reloaded = Runtime.Engine(persistenceProject, Set.empty, "2041-02-03T04:05:06Z")
        let reloadedPersistent = testResult reloaded "persist.load" "exact-count"
        check (boolValue reloadedPersistent.["passed"]) "fresh reload reparses the renamed effect-count test"
        equal 1 (reloadedPersistent.["effectAssertion"].["actual"].["fs.read"].GetValue<int>()) "renamed reload retains exact count behavior"

        let gateSource suffix writeExtra =
            let effects = if writeExtra then "fs.read, fs.write" else "fs.read"
            let targetBody =
                if writeExtra then "    file::write(\"extra\", \"noise\")\n    file::read(\"gate\")"
                else "    file::read(\"gate\")"
            "fn gate.read() -> String {\n"
            + "    effects " + effects + "\n"
            + targetBody + "\n}\n\n"
            + "test gate.read/basic {\n"
            + "    file::write(\"gate\", \"held\")\n"
            + "    gate::read()\n"
            + "    => \"held\"" + suffix + "\n}"

        let runReplacementControl projectName withAssertion =
            let replacementProject = Path.Combine(root, projectName)
            let runtime = Runtime.Engine(replacementProject, Set.empty, "2041-02-03T04:05:06Z")
            let suffix = if withAssertion then " effects { fs.read: 1; fs.write: 0; }" else ""
            defineFlowProject runtime (gateSource suffix false) [ "syntaxVersion", jint 2 ]
            |> expectOk "stage baseline Flow/2 library test"
            |> ignore
            commit runtime "commit" "gate.read" [ "library", jbool true ]
            |> expectOk "publish baseline as a library word"
            |> ignore
            let before = Storage.load (Storage.create replacementProject) |> Result.defaultWith (fun problem -> failwith problem.Message)
            defineFlowProject runtime (gateSource suffix true)
                [ "syntaxVersion", jint 2; "replace", jbool true; "expectedRevision", jint 1 ]
            |> expectOk "stage the extra-write replacement with its test source"
            |> ignore
            runtime, replacementProject, before

        let control, controlProject, controlBefore = runReplacementControl "effect-count-old-control" false
        let controlPublished = commit control "replace-word" "gate.read" [] |> expectOk "old return-only test permits the extra-write replacement"
        check (jsonArrayStrings controlPublished.["data"] |> List.contains "gate.read/basic") "return-only replacement runs its attached test"
        let controlAfter = Storage.load (Storage.create controlProject) |> Result.defaultWith (fun problem -> failwith problem.Message)
        check (controlBefore.ManifestHash <> controlAfter.ManifestHash) "return-only control publishes the replacement despite the extra write"

        let guarded, guardedProject, guardedBefore = runReplacementControl "effect-count-library-gate" true
        let blocked = commit guarded "replace-word" "gate.read" [] |> expectError "COMMIT_TESTS_FAILED"
        check (jsonArrayStrings blocked.["error"].["actual"] |> List.contains "gate.read/basic") "effect assertion failure blocks library replacement"
        let guardedAfter = Storage.load (Storage.create guardedProject) |> Result.defaultWith (fun problem -> failwith problem.Message)
        equal guardedBefore.ManifestHash guardedAfter.ManifestHash "failed effect assertion leaves library manifest unchanged"

    [<EntryPoint>]
    let main _ =
        let root = newRoot ()
        try
            testAuthoringHelpAndCanonicalLibrarySource root
            testCandidateCasThenNormalCommit root
            testCommittedReplacementCallerGate root
            testFlowUnknownArgumentsAndExpectationGuidance root
            testExplicitFrontendAndDurableReload root
            testExplicitFrontendCannotFallBack root
            testFlow2FormatDefinePersistReloadAndRewrite root
            testFlow2EnumsPersistReloadAndBindings root
            testLibraryDependencyQualification root
            testEnumFiniteLibraryCoverage root
            testDescribeFlowReferences root
            testGeneratedRecordCasesPersistBesideFlow root
            testStackGeneratedCasesSurviveV1Manifest root
            testStackOwnerMigrationToFlow root
            testReplacementCasAndRollback root
            testFlowAttachmentOnlyDocuments root
            testFlowAttachmentOnlyPreservesTemporaryLifetime root
            testTemporaryPromotionAndTaskAbort root
            testNamedSnapshotRestoresFlowAndProviders root
            testPersistedBindingsAreVerified root
            testRetainedDotBindingAcrossReplacement root
            testExpectationCoverageIsNotActualCoverage root
            testFiniteCoverageInvocationAndNormalReturns root
            testFlowMaintenanceRenameDeprecateAndRestore root
            testFlowMaintenanceRejectsUntouchedRebind root
            testFlowMaintenanceFailureAndLibraryCoverage root
            testFlowStaticListFold root
            testFlow2StaticListCallbacks root
            testFlow2DottedCallsAndRename root
            testFlowValidatorCannotBeRenamedAfterTypeCommit root
            testFlowProjectDocumentTypesCommitAndReload root
            testRecordValidatorRuntimeAndPersistence root
            testEffectCountAssertions root
            printfn $"Flow Runtime tests passed: 33 groups, {assertions} assertions."
            0
        finally
            if Directory.Exists root then Directory.Delete(root, true)
