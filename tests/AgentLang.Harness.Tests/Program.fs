namespace AgentLang.Harness.Tests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open AgentLang
open AgentLang.Benchmarks

module Program =
    let mutable private assertions = 0

    let private check condition message =
        assertions <- assertions + 1
        if not condition then failwith message

    let private equal expected actual message =
        assertions <- assertions + 1
        if expected <> actual then failwith $"{message}: expected {expected}, got {actual}"

    let private json (text: string) = JsonNode.Parse(text)

    let private outputMessage (text: string) =
        let item = JsonObject()
        item["type"] <- JsonValue.Create("message")
        item["role"] <- JsonValue.Create("assistant")
        let part = JsonObject()
        part["type"] <- JsonValue.Create("output_text")
        part["text"] <- JsonValue.Create(text)
        let content = JsonArray()
        content.Add part
        item["content"] <- content
        item :> JsonNode

    let private functionCall (callId: string) (name: string) (arguments: string) =
        let item = JsonObject()
        item["type"] <- JsonValue.Create("function_call")
        item["id"] <- JsonValue.Create($"fc_{callId}")
        item["call_id"] <- JsonValue.Create(callId)
        item["name"] <- JsonValue.Create(name)
        item["arguments"] <- JsonValue.Create(arguments)
        item :> JsonNode

    let private scriptedResponse (status: string) (output: JsonNode list) (text: string) (usage: string option) =
        let response = JsonObject()
        response["status"] <- JsonValue.Create(status)
        let items = JsonArray()
        output |> List.iter items.Add
        response["output"] <- items
        response["output_text"] <- JsonValue.Create(text)
        match usage with
        | Some value -> response["usage"] <- json value
        | None -> ()
        response :> JsonNode

    let private provider (responses: JsonArray) = ScriptedProvider(responses) :> IAgentProvider

    let private step (operation: string) (args: JsonObject) (expectedText: string option) (expectedData: JsonNode option) (minimum: int option) =
        { Operation = operation
          Arguments = args
          ExpectedOk = true
          ExpectedTextContains = expectedText
          ExpectedData = expectedData
          MinimumPassingTests = minimum }

    let private evalOracle (code: string) (expectedStack: string) =
        let args = JsonObject()
        args["code"] <- JsonValue.Create(code)
        let data = JsonObject()
        let values = JsonArray()
        values.Add(JsonValue.Create(expectedStack))
        data["stack"] <- values
        step "eval" args None (Some(data :> JsonNode)) None

    let private makeTask (id: string) (oracle: OracleStep list) =
        { Id = id
          Goal = "Run the configured acceptance check."
          SystemPrompt = "Work only with runtime tools."
          InitialContext = "Core operations are introspectable."
          Oracle = oracle }

    let private config (root: string) (mode: RunMode) (taskName: string) (oracle: OracleStep list) =
        let runDir = Path.Combine(root, "runs", taskName)
        let projectDir =
            match mode with
            | Flat -> Path.Combine(runDir, "project")
            | Growing ->
                let projectName =
                    if taskName = "growing-one" || taskName = "growing-two" then "growing-retention"
                    else $"growing-{taskName}"
                Path.Combine(root, projectName)
        { Task = makeTask taskName oracle
          Mode = mode
          // Existing scripted protocol cases are historical Stack coverage.
          // Dedicated Flow cases opt in explicitly below.
          Frontend = SourceFrontend.Stack
          Model = "scripted-test-model"
          ProjectDirectory = projectDir
          RunDirectory = runDir
          BaselineProfile = BaselineProfile.DomainSeededControl
          SeedDictionarySource = None
          TestFailurePoint = None
          MaxTurns = 8
          MaxToolCalls = 10
          MaxOutputTokens = 512
          ContextBudgetBytes = 100_000 }

    let private runWith (settings: RunConfig) (agentProvider: IAgentProvider) =
        Runner.run settings agentProvider |> fun pending -> pending.GetAwaiter().GetResult()

    let private reportPath (settings: RunConfig) = Path.Combine(settings.RunDirectory, "report.json")

    let private makeTempRoot () =
        let root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "agentlang-harness-tests", Guid.NewGuid().ToString("N")))
        Directory.CreateDirectory(root) |> ignore
        root

    let private removeTempRoot root =
        let resolved = Path.GetFullPath root
        let allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "agentlang-harness-tests"))
        if resolved.StartsWith(allowed + string Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists resolved then
            Directory.Delete(resolved, true)

    let private removeDirectoryLink path =
        if Directory.Exists path then
            let attributes = File.GetAttributes(path)
            if (attributes &&& FileAttributes.ReparsePoint) <> enum<FileAttributes> 0 then
                Directory.Delete(path)

    let private testStatelessToolLoopAndLogs root =
        let settings = config root Growing "stateless-loop" [ evalOracle "10 20 add" "30" ]
        let reasoning = json "{\"type\":\"reasoning\",\"encrypted_content\":\"ciphertext-preserved\"}"
        let call = functionCall "call-1" "agentlang_eval" "{\"code\":\"10 20 add\"}"
        let responses = JsonArray()
        responses.Add(scriptedResponse "completed" [ reasoning; call ] "" None)
        responses.Add(scriptedResponse "completed" [ outputMessage "The expression evaluated to 30." ] "The expression evaluated to 30." None)
        let scripted = ScriptedProvider(responses)
        let result = runWith settings (scripted :> IAgentProvider)
        let failureCode = result.FailureCode |> Option.defaultValue "none"
        let failureMessage = result.FailureMessage |> Option.defaultValue "none"
        check result.Success $"offline run passed its oracle (failure: {failureCode}: {failureMessage})"
        equal 2 result.RequestCount "two provider turns were sent"
        equal 1 result.ToolCalls "one runtime call was performed"
        equal None result.InputTokens "unknown input usage stays null"
        equal None result.OutputTokens "unknown output usage stays null"
        check (File.Exists(Path.Combine(settings.RunDirectory, "prompt.txt"))) "prompt was saved"
        check (File.Exists(Path.Combine(settings.RunDirectory, "trace.jsonl"))) "request, response, and tool trace was saved"
        check (File.Exists(Path.Combine(settings.RunDirectory, "runtime.log"))) "runtime task log was saved"
        check (File.Exists(Path.Combine(settings.RunDirectory, "initial-state.json"))) "pre-run state was saved"
        check (File.Exists(Path.Combine(settings.RunDirectory, "final-state.json"))) "final state was saved"

        let requests = scripted.RequestBodies
        equal 2 requests.Length "script provider recorded each request body"
        let secondRequest = JsonNode.Parse(requests[1]).AsObject()
        check (isNull secondRequest["previous_response_id"]) "continuation does not hide history behind previous_response_id"
        let inputs = secondRequest["input"].AsArray()
        check (inputs |> Seq.exists (fun item -> Json.propertyString item "type" "" = "reasoning" && item["encrypted_content"].GetValue<string>() = "ciphertext-preserved")) "reasoning output item is resent verbatim"
        check (inputs |> Seq.exists (fun item -> Json.propertyString item "type" "" = "function_call" && item["call_id"].GetValue<string>() = "call-1")) "function call output item is preserved"
        check (inputs |> Seq.exists (fun item -> Json.propertyString item "type" "" = "function_call_output" && item["call_id"].GetValue<string>() = "call-1")) "function output points to the matching call_id"
        equal "false" (secondRequest["store"].GetValue<bool>().ToString().ToLowerInvariant()) "Responses request uses store=false"
        equal 6 (secondRequest["tools"].AsArray().Count) "provider receives exactly the closed six-tool runtime surface"

        let report = JsonNode.Parse(File.ReadAllText(reportPath settings)).AsObject()
        equal "stack" (report["frontend"].GetValue<string>()) "run metadata records the explicitly selected historical frontend"
        let tokenUsage = report["tokenUsage"].AsObject()
        let context = report["context"].AsObject()
        check (isNull tokenUsage["inputTokens"]) "missing token usage serializes as JSON null, not zero"
        check (context["estimatedRequestTokens"].GetValue<int64>() > 0L) "byte estimate is separately recorded"
        check (context["estimateMethod"].GetValue<string>().Contains("not a tokenizer")) "byte estimate is explicitly labeled approximate"

    let private testStrictRuntimeToolWhitelist root =
        let settings = config root Growing "tool-whitelist" [ evalOracle "7 8 add" "15" ]
        let rejected = functionCall "bad-1" "agentlang_inspect" "{\"operation\":\"task.abort\",\"name\":\"\"}"
        let malformed = functionCall "bad-2" "agentlang_inspect" "{"
        let responses = JsonArray()
        responses.Add(scriptedResponse "completed" [ rejected; malformed ] "" None)
        responses.Add(scriptedResponse "completed" [ outputMessage "The restricted calls were rejected." ] "The restricted calls were rejected." None)
        let result = runWith settings (provider responses)
        check result.Success "invalid introspection requests remain recoverable and do not end the task"
        equal 2 result.ToolCalls "attempted calls count against tool limits"
        let trace = File.ReadAllLines(Path.Combine(settings.RunDirectory, "trace.jsonl")) |> String.concat "\n"
        check (trace.Contains("introspection operation") && trace.Contains("not available")) $"mutating lifecycle operation is rejected by runtime decoder; trace was {trace}"
        check (trace.Contains("invalid function arguments")) "malformed JSON arguments become structured tool errors"
        check (Directory.Exists settings.ProjectDirectory) "rejected task mutation does not change the project"

    let private testHelpThroughInspectTool root =
        let expected = JsonObject()
        expected["topic"] <- JsonValue.Create("authoring")
        let settings =
            config root Flat "help-inspect-tool"
                [ step "help" (JsonObject()) None (Some(expected :> JsonNode)) None ]
        let defaultHelp = functionCall "help-authoring" "agentlang_inspect" "{\"operation\":\"help\",\"name\":\"\"}"
        let defineHelp = functionCall "help-define" "agentlang_inspect" "{\"operation\":\"help\",\"name\":\"define\"}"
        let responses = JsonArray()
        responses.Add(scriptedResponse "completed" [ defaultHelp; defineHelp ] "" None)
        responses.Add(scriptedResponse "completed" [ outputMessage "The authoring help explains the source form and define fields." ] "The authoring help explains the source form and define fields." None)
        let result = runWith settings (provider responses)
        check result.Success "the inspect help tool and default authoring oracle both succeed"
        equal 2 result.ToolCalls "both the default authoring index and define topic are dispatched"

        let trace =
            File.ReadAllLines(Path.Combine(settings.RunDirectory, "trace.jsonl"))
            |> Array.choose (fun line ->
                let node = JsonNode.Parse(line)
                if Json.propertyString node "event" "" = "runtime-tool" then Json.tryProperty node "result" else None)
        equal 2 trace.Length "both help calls are present in the runtime trace"
        let authoringData = trace[0]["data"]
        let defineData = trace[1]["data"]
        equal "authoring" (Json.propertyString authoringData "topic" "") "an empty inspect name omits the topic and selects default authoring help"
        equal "define" (Json.propertyString defineData "topic" "") "a nonempty inspect name is routed as the help topic"
        check (Json.tryProperty authoringData "word" |> Option.isNone) "help dispatch does not pass its name as a word"
        equal 1 (authoringData["schemaVersion"].GetValue<int>()) "help data declares its stable schema version"
        let topics = authoringData["topics"].AsArray() |> Seq.map (fun item -> item.GetValue<string>()) |> Seq.toList
        equal [ "authoring"; "define"; "replacement"; "examples" ] topics "help data lists the canonical topics in order"
        for field in [ "title"; "documentation"; "topicInstructions"; "allowedFlowDefineFields"; "sourceExamples"; "requestExamples" ] do
            check (Json.tryProperty authoringData field |> Option.isSome) $"authoring help data includes {field}"
        check (defineData["allowedFlowDefineFields"].AsArray().Count > 0) "define help describes supported Flow define fields"
        check (defineData["sourceExamples"].AsArray().Count > 0) "define help includes complete source examples"
        check (defineData["requestExamples"].AsArray().Count > 0) "define help includes request examples"

        let tools = AgentTools.definitions ()
        equal 6 tools.Count "adding help does not add a model tool"
        let inspect = tools |> Seq.find (fun item -> Json.propertyString item "name" "" = "agentlang_inspect")
        let inspectParameters = inspect["parameters"].AsObject()
        let inspectProperties = inspectParameters["properties"].AsObject()
        let inspectOperation = inspectProperties["operation"].AsObject()
        let inspectOperationChoices = inspectOperation["enum"].AsArray()
        let inspectOperations = inspectOperationChoices |> Seq.map (fun item -> item.GetValue<string>()) |> Set.ofSeq
        check (inspectOperations.Contains("help")) "help is part of the existing inspect operation enum"
        let define = tools |> Seq.find (fun item -> Json.propertyString item "name" "" = "agentlang_define")
        check ((Json.propertyString define "description" "").Contains("documentation, tests, and examples in the source", StringComparison.Ordinal)) "the define description points authors to source documentation and attachments"

    let private testInitialPromptIncludesLanguagePrimer () =
        let task =
            TaskFile.parse """{"id":"prompt-primer","goal":"Create a tested word.","systemPrompt":"Prefer existing vocabulary.","oracle":[{"operation":"test-all"}]}"""
        check (task.SystemPrompt.Contains("word name(input: Type) -> Output", StringComparison.Ordinal)) "the default task primer explains Flow definitions"
        check (task.SystemPrompt.Contains("test word/case", StringComparison.Ordinal)) "the default task primer includes the Flow test grammar"
        check (task.SystemPrompt.Contains("example word/case", StringComparison.Ordinal)) "the default task primer includes the Flow example grammar"
        check (task.SystemPrompt.Contains("doc \"...\"", StringComparison.Ordinal)) "the default task primer explains inline documentation syntax"
        check (task.SystemPrompt.Contains("Bare `=>` expectations use supported literals", StringComparison.Ordinal) && task.SystemPrompt.Contains("=> value expression", StringComparison.Ordinal) && task.SystemPrompt.Contains("=> error CODE", StringComparison.Ordinal)) "the default task primer distinguishes literal, value, and error test expectations"
        check (task.SystemPrompt.Contains("Examples use literal expectations", StringComparison.Ordinal) && task.SystemPrompt.Contains("Money::value(...)", StringComparison.Ordinal) && task.SystemPrompt.Contains("Int literal", StringComparison.Ordinal)) "the default task primer explains literal example expectations and nominal result unwrapping"
        check (task.SystemPrompt.Contains("agentlang_inspect", StringComparison.Ordinal) && task.SystemPrompt.Contains("authoring index", StringComparison.Ordinal)) "the default task primer makes live inspect help discoverable"
        check (task.SystemPrompt.Contains("External JSONL replacement", StringComparison.Ordinal) && task.SystemPrompt.Contains("replace=true", StringComparison.Ordinal) && task.SystemPrompt.Contains("expectedRevision", StringComparison.Ordinal) && task.SystemPrompt.Contains("from `describe`", StringComparison.Ordinal)) "the default task primer points to the external revision-checked replacement contract"
        check (task.SystemPrompt.Contains("no replacement adapter", StringComparison.Ordinal) && task.SystemPrompt.Contains("Use only supplied host operations", StringComparison.Ordinal)) "the default task primer limits mutations to the active harness tools"
        check (task.SystemPrompt.Contains("Task-specific guidance: Prefer existing vocabulary.", StringComparison.Ordinal)) "task-specific system guidance is preserved after the language primer"
        let stackTask =
            TaskFile.parseWithFrontend SourceFrontend.Stack """{"id":"stack-primer","goal":"Check a historical fixture.","oracle":[{"operation":"test-all"}]}"""
        check (stackTask.SystemPrompt.Contains("word name : Input -> Output", StringComparison.Ordinal)) "an explicit historical Stack run receives the Stack definition primer"

    let private testFlowFrontendSeedAndToolDispatch root =
        let flowSeed = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "fixtures", "flow1", "customer.agent"))
        let flowSettings =
            { config root Flat "flow-seed-inventory" [ step "test-all" (JsonObject()) None None (Some 4) ] with
                Frontend = SourceFrontend.Flow
                SeedDictionarySource = Some flowSeed }
        let seedResponses = JsonArray()
        seedResponses.Add(scriptedResponse "completed" [ outputMessage "The Flow-seeded vocabulary tests pass." ] "The Flow-seeded vocabulary tests pass." None)
        let seeded = runWith flowSettings (provider seedResponses)
        check seeded.Success "the default Flow frontend seeds and audits its exact project vocabulary"
        let seedReport = JsonNode.Parse(File.ReadAllText(reportPath flowSettings)).AsObject()
        equal "flow" (seedReport["frontend"].GetValue<string>()) "Flow selection is saved in report metadata"
        let seedInventory = ((seedReport["baselineAudit"])["taskStartInventory"]).AsObject()
        equal "word-case/1" (seedInventory["attachmentNameFormat"].GetValue<string>()) "new inventories declare the qualified attachment-name format"
        equal 2 (seedInventory["authoredWords"].AsArray().Count) "Flow inventory includes only current authored word heads"
        equal 1 (seedInventory["records"].AsArray().Count) "Flow inventory reads typed record sources"
        equal 4 (seedInventory["tests"].AsArray().Count) "Flow inventory reads current test source objects"
        equal 1 (seedInventory["examples"].AsArray().Count) "Flow inventory reads current example source objects"
        let seedTests = seedInventory["tests"].AsArray() |> Seq.map (fun item -> item.GetValue<string>()) |> Set.ofSeq
        check (seedTests.Contains("customer.premium?/premium") && seedTests.Contains("customer.discounted-balance/regular")) "Flow inventory identifies each test by owner and case"
        let seedExamples = seedInventory["examples"].AsArray()
        let seedExampleName = seedExamples[0].GetValue<string>()
        equal "customer.discounted-balance/premium" seedExampleName "Flow example inventory identifies its owning word"
        let flowSource =
            "word harness.increment(value: Int) -> Int {\n"
            + "    effects none\n"
            + "    add(value, 1)\n"
            + "}\n\n"
            + "test harness.increment/basic {\n"
            + "    harness::increment(41)\n"
            + "    => value harness::increment(41)\n"
            + "}"
        let flowArgs = System.Text.Json.JsonSerializer.Serialize({| source = flowSource; lifetime = "candidate" |})
        let define = functionCall "flow-define" "agentlang_define" flowArgs
        let commitArgs = JsonObject()
        commitArgs["action"] <- JsonValue.Create("commit-word")
        commitArgs["word"] <- JsonValue.Create("harness.increment")
        commitArgs["quality"] <- JsonValue.Create("project")
        let commit = functionCall "flow-commit" "agentlang_task" (commitArgs.ToJsonString())
        let flowOracle =
            let args = JsonObject()
            args["code"] <- JsonValue.Create("harness::increment(41)")
            { evalOracle "" "42" with Arguments = args }
        let explicitStackOracle =
            let args = JsonObject()
            args["code"] <- JsonValue.Create("41 1 add")
            args["frontend"] <- JsonValue.Create("stack")
            { evalOracle "" "42" with Arguments = args }
        let settings =
            { config root Flat "flow-tool-dispatch" [ step "test-all" (JsonObject()) None None (Some 1); flowOracle; explicitStackOracle ] with
                Frontend = SourceFrontend.Flow
                BaselineProfile = BaselineProfile.PrimitiveOnly }
        let responses = JsonArray()
        responses.Add(scriptedResponse "completed" [ define ] "" None)
        responses.Add(scriptedResponse "completed" [ commit ] "" None)
        responses.Add(scriptedResponse "completed" [ outputMessage "The Flow word is tested and committed." ] "The Flow word is tested and committed." None)
        let result = runWith settings (provider responses)
        check result.Success "configured Flow applies to authoring and default oracle calls while an explicit Stack oracle selector wins"
        equal 2 result.ToolCalls "Flow tool dispatch uses the existing compact tool set"
        let report = JsonNode.Parse(File.ReadAllText(reportPath settings)).AsObject()
        equal "flow" (report["frontend"].GetValue<string>()) "report records the configured Flow frontend"
        equal 3 (report["oracle"].AsArray().Count) "Flow and explicitly Stack-selected oracle checks are both recorded"
        check (report["oracle"].AsArray() |> Seq.forall (fun item -> item["passed"].GetValue<bool>())) "frontend-aware task oracles pass"
        let trace = File.ReadAllText(Path.Combine(settings.RunDirectory, "trace.jsonl"))
        check (trace.Contains("harness.increment", StringComparison.Ordinal)) "Flow-authored word is recorded by the runtime trace"

    let private testManifestInventoryUsesCurrentMixedFrontendHeads root =
        let settings = config root Growing "inventory-current-heads" [ step "test-all" (JsonObject()) None None (Some 3) ]
        let engine = Runtime.Engine(settings.ProjectDirectory, Set.empty, "2030-01-02T03:04:05Z", fileSystemMode = FileSystemMode.Virtual)
        let request (operation: string) (fields: (string * JsonNode) list) =
            let args = JsonObject()
            for name, value in fields do args[name] <- value
            let response = engine.Dispatch(operation, args)
            if not (response["ok"].GetValue<bool>()) then
                let message = response["text"].GetValue<string>()
                failwith $"Could not prepare mixed frontend inventory: {message}"
            response
        let define (frontend: string) (source: string) (extraArguments: (string * JsonNode) list) =
            request "define" ([ "frontend", JsonValue.Create(frontend) :> JsonNode; "source", JsonValue.Create(source) :> JsonNode ] @ extraArguments)
            |> ignore
        let commit (word: string) = request "commit" [ "word", JsonValue.Create(word) :> JsonNode ] |> ignore
        let readWordId (word: string) =
            let words = request "words" []
            ((words["data"])["words"]).AsArray()
            |> Seq.find (fun item -> item["name"].GetValue<string>() = word)
            |> fun word -> word["id"].GetValue<string>()

        let firstRevision =
            "word inventory.mixed : Int -> Int\n"
            + "    effects none\n"
            + "    1 add\n"
            + "end\n\n"
            + "test inventory.mixed/first\n"
            + "    1 inventory.mixed\n"
            + "    expect 2\n"
            + "end\n"
        define "stack" firstRevision []
        commit "inventory.mixed"
        let stableMixedId = readWordId "inventory.mixed"

        let currentRevision =
            "word inventory.mixed : Int -> Int\n"
            + "    effects none\n"
            + "    2 add\n"
            + "end\n\n"
            + "test inventory.mixed/first\n"
            + "    1 inventory.mixed\n"
            + "    expect 3\n"
            + "end\n\n"
            + "test inventory.mixed/current\n"
            + "    1 inventory.mixed\n"
            + "    expect 3\n"
            + "end\n"
        define "stack" currentRevision [ "replace", JsonValue.Create(true) :> JsonNode; "expectedRevision", JsonValue.Create(1) :> JsonNode ]
        commit "inventory.mixed"
        equal stableMixedId (readWordId "inventory.mixed") "a replacement retains its stable word identity"

        let flowSource =
            "word inventory.flow(value: Int) -> Int {\n"
            + "    effects none\n"
            + "    add(value, 2)\n"
            + "}\n\n"
            + "test inventory.flow/current {\n"
            + "    inventory::flow(1)\n"
            + "    => value inventory::flow(1)\n"
            + "}"
        define "flow" flowSource []
        commit "inventory.flow"
        let stableFlowId = readWordId "inventory.flow"

        let responses = JsonArray()
        responses.Add(scriptedResponse "completed" [ outputMessage "Both current Stack and Flow word tests pass." ] "Both current Stack and Flow word tests pass." None)
        let result = runWith { settings with Frontend = SourceFrontend.Flow } (provider responses)
        check result.Success "mixed historical Stack and current Flow words load through the Flow harness"
        let report = JsonNode.Parse(File.ReadAllText(reportPath settings)).AsObject()
        let inventory = ((report["baselineAudit"])["taskStartInventory"]).AsObject()
        equal "word-case/1" (inventory["attachmentNameFormat"].GetValue<string>()) "mixed frontend inventory declares qualified attachment names"
        let words = inventory["authoredWords"].AsArray()
        equal 2 words.Count "manifest inventory lists current word heads, not historical revisions"
        let currentMixed = words |> Seq.find (fun word -> word["name"].GetValue<string>() = "inventory.mixed")
        let currentFlow = words |> Seq.find (fun word -> word["name"].GetValue<string>() = "inventory.flow")
        equal 2 (currentMixed["revision"].GetValue<int>()) "Stack inventory uses the current revision metadata"
        equal stableMixedId (currentMixed["stableId"].GetValue<string>()) "Stack inventory reports the stable current-head ID"
        equal stableFlowId (currentFlow["stableId"].GetValue<string>()) "Flow inventory reports the stable current-head ID"
        let tests = inventory["tests"].AsArray() |> Seq.map (fun test -> test.GetValue<string>()) |> Set.ofSeq
        equal 3 tests.Count "inventory lists only the three tests attached to current word heads"
        check (tests.Contains("inventory.mixed/current") && tests.Contains("inventory.mixed/first") && tests.Contains("inventory.flow/current")) "inventory lists the refreshed Stack cases and current Flow case"

        let lineagePath = Path.Combine(settings.ProjectDirectory, ".agentlang-benchmark-lineage.json")
        let lineage = JsonNode.Parse(File.ReadAllText(lineagePath)).AsObject()
        let origin = lineage["originInventory"].AsObject()
        origin.Remove("attachmentNameFormat") |> ignore
        let legacyCaseNames (names: JsonArray) =
            let result = JsonArray()
            for item in names do
                let qualifiedName = item.GetValue<string>()
                let separator = qualifiedName.IndexOf('/')
                if separator < 0 || separator = qualifiedName.Length - 1 then
                    failwith $"Expected a qualified inventory attachment name, received '{qualifiedName}'."
                result.Add(JsonValue.Create(qualifiedName.Substring(separator + 1)))
            result
        origin["tests"] <- legacyCaseNames (origin["tests"].AsArray())
        origin["examples"] <- legacyCaseNames (origin["examples"].AsArray())
        let legacyOriginText = origin.ToJsonString()
        let legacyMarker = lineage.ToJsonString()
        File.WriteAllText(lineagePath, legacyMarker, UTF8Encoding(false))

        let legacySettings =
            { config root Growing "inventory-legacy-origin" [ step "test-all" (JsonObject()) None None (Some 3) ] with
                ProjectDirectory = settings.ProjectDirectory }
        let legacyResponses = JsonArray()
        legacyResponses.Add(scriptedResponse "completed" [ outputMessage "The legacy lineage marker remains readable." ] "The legacy lineage marker remains readable." None)
        let legacyResult = runWith legacySettings (provider legacyResponses)
        check legacyResult.Success "an untagged version-2 lineage origin remains readable"
        let preservedOrigin = legacyResult.BaselineAudit["originInventory"].AsObject()
        equal legacyOriginText (preservedOrigin.ToJsonString()) "continuing a legacy lineage preserves the complete origin inventory"
        check (not (preservedOrigin.ContainsKey("attachmentNameFormat"))) "reading a legacy origin does not rewrite it with a new name format"
        let preservedLegacyTests = preservedOrigin["tests"].AsArray() |> Seq.map (fun item -> item.GetValue<string>()) |> Set.ofSeq
        check (preservedLegacyTests.Contains("first") && preservedLegacyTests.Contains("current")) "legacy origin retains its case-only test names"
        let currentInventory = legacyResult.BaselineAudit["taskStartInventory"].AsObject()
        equal "word-case/1" (currentInventory["attachmentNameFormat"].GetValue<string>()) "continued tasks emit the new qualified inventory format"
        let acceptedLegacyMarker = File.ReadAllText(lineagePath)
        let savedMarker = JsonNode.Parse(acceptedLegacyMarker).AsObject()
        let savedOrigin = savedMarker["originInventory"].AsObject()
        equal legacyOriginText (savedOrigin.ToJsonString()) "continuing a legacy lineage preserves the saved origin inventory"

        let rejectInventoryMarker name mutate =
            let invalidMarker = JsonNode.Parse(acceptedLegacyMarker).AsObject()
            mutate (invalidMarker["originInventory"].AsObject())
            File.WriteAllText(lineagePath, invalidMarker.ToJsonString(), UTF8Encoding(false))
            let invalidSettings =
                { config root Growing name [ step "test-all" (JsonObject()) None None (Some 3) ] with
                    ProjectDirectory = settings.ProjectDirectory }
            let invalidResult = runWith invalidSettings (provider (JsonArray()))
            equal (Some "BASELINE_LINEAGE_INVALID") invalidResult.FailureCode $"invalid attachment inventory metadata is rejected ({name})"
            File.WriteAllText(lineagePath, acceptedLegacyMarker, UTF8Encoding(false))

        rejectInventoryMarker "inventory-unknown-name-format" (fun oldOrigin ->
            oldOrigin["attachmentNameFormat"] <- JsonValue.Create("word-case/2"))
        rejectInventoryMarker "inventory-malformed-name-format" (fun oldOrigin ->
            oldOrigin["attachmentNameFormat"] <- JsonValue.Create(true))
        rejectInventoryMarker "inventory-malformed-qualified-name" (fun oldOrigin ->
            oldOrigin["attachmentNameFormat"] <- JsonValue.Create("word-case/1")
            let oldTests = oldOrigin["tests"].AsArray()
            oldTests[0] <- JsonValue.Create("case-only"))

    let private testToolLimitRollsBackCandidates root =
        let settings = config root Growing "tool-limit" [ evalOracle "41 rollback.staged" "42" ]
        let candidate = """word rollback.staged : Int -> Int
    effects none
    1 add
end

test rollback.staged/basic
    41 rollback.staged
    => 42
end
"""
        let define = functionCall "define-1" "agentlang_define" (System.Text.Json.JsonSerializer.Serialize({| source = candidate; lifetime = "candidate" |}))
        let commit = functionCall "commit-2" "agentlang_task" (System.Text.Json.JsonSerializer.Serialize({| action = "commit-word"; word = "rollback.staged"; quality = "project" |}))
        let overLimit = functionCall "eval-3" "agentlang_eval" "{\"code\":\"41 rollback.staged\"}"
        let responses = JsonArray()
        responses.Add(scriptedResponse "completed" [ define ] "" None)
        responses.Add(scriptedResponse "completed" [ commit ] "" None)
        responses.Add(scriptedResponse "completed" [ overLimit ] "" None)
        let limited = { settings with MaxToolCalls = 2 }
        let result = runWith limited (provider responses)
        check (not result.Success) "tool overflow fails the run"
        equal (Some "TOOL_LIMIT") result.FailureCode "tool limit diagnostic code"
        equal 2 result.ToolCalls "the over-limit call did not execute"
        let dictionaryPath = Path.Combine(settings.ProjectDirectory, "dictionary.agent")
        check (not (File.Exists dictionaryPath) || not (File.ReadAllText(dictionaryPath).Contains("rollback.staged"))) "abort rolls back an earlier per-word project commit"
        let initialState = JsonNode.Parse(File.ReadAllText(Path.Combine(settings.RunDirectory, "initial-state.json")))
        let finalState = JsonNode.Parse(File.ReadAllText(Path.Combine(settings.RunDirectory, "final-state.json")))
        let finalDictionary = finalState["state"].AsObject()["dictionary"]
        check (isNull finalDictionary) "reported final state matches the pre-task empty dictionary"
        let initialDigest = initialState["sha256"].GetValue<string>()
        let finalDigest = finalState["sha256"].GetValue<string>()
        equal initialDigest finalDigest "semantic state digest matches after rollback"
        let initialStateBody = initialState["state"]
        let finalStateBody = finalState["state"]
        let initialGeneration = initialStateBody["generation"].GetValue<int64>()
        let finalGeneration = finalStateBody["generation"].GetValue<int64>()
        check (finalGeneration > initialGeneration) "rollback advances storage concurrency generation without changing semantic state"

    let private testHistorySymlinkAndSizeGuards root =
        let captureSettings = config root Growing "history-link-capture" [ evalOracle "1" "1" ]
        let captureHistory = Path.Combine(captureSettings.ProjectDirectory, "history")
        let captureExternal = Path.Combine(root, "external-history-capture")
        Directory.CreateDirectory(captureSettings.ProjectDirectory) |> ignore
        Directory.CreateDirectory(captureExternal) |> ignore
        let captureSentinel = Path.Combine(captureExternal, "task-sentinel.json")
        File.WriteAllText(captureSentinel, "external history must survive")

        let captureLinkCreated =
            try
                Directory.CreateSymbolicLink(captureHistory, captureExternal) |> ignore
                true
            with
            | :? UnauthorizedAccessException
            | :? PlatformNotSupportedException
            | :? IOException -> false

        if captureLinkCreated then
            try
                let emptyProvider = ScriptedProvider(JsonArray())
                let captured = runWith captureSettings (emptyProvider :> IAgentProvider)
                equal (Some "PROJECT_HISTORY_REPARSE_POINT") captured.FailureCode "initial history symlink is rejected"
                equal 0 emptyProvider.ResponsesConsumed "history path is checked before the provider is called"
                equal "external history must survive" (File.ReadAllText(captureSentinel)) "capture never follows an external history link"
                equal [ "task-sentinel.json" ] (Directory.GetFiles(captureExternal) |> Array.map (fun path -> Path.GetFileName(path)) |> Array.toList) "capture does not add or remove external task files"
            finally
                removeDirectoryLink captureHistory

            let restoreSettings = config root Growing "history-link-restore" [ evalOracle "1" "2" ]
            let restoreHistory = Path.Combine(restoreSettings.ProjectDirectory, "history")
            let restoreExternal = Path.Combine(root, "external-history-restore")
            Directory.CreateDirectory(restoreExternal) |> ignore
            let restoreSentinel = Path.Combine(restoreExternal, "task-sentinel.json")
            File.WriteAllText(restoreSentinel, "external task log must survive rollback")
            let mutable linkInstalled = false
            let linkSwappingProvider =
                { new IAgentProvider with
                    member _.Name = "history-link-swap"
                    member _.Complete(_: ProviderRequest) =
                        task {
                            if not linkInstalled then
                                linkInstalled <- true
                                Directory.CreateSymbolicLink(restoreHistory, restoreExternal) |> ignore
                            let output = JsonArray()
                            output.Add(outputMessage "The task is finished.")
                            return
                                { Status = "completed"
                                  OutputItems = output
                                  OutputText = "The task is finished."
                                  Usage = None
                                  RawResponse = "{}" }
                        } }
            try
                let rolledBack = runWith restoreSettings linkSwappingProvider
                equal (Some "ROLLBACK_FAILED") rolledBack.FailureCode "rollback refuses a history link installed after capture"
                check (rolledBack.FailureMessage |> Option.exists (fun message -> message.Contains("PROJECT_HISTORY_REPARSE_POINT", StringComparison.Ordinal))) "rollback exposes the structured history path error"
                let restoreTrace = File.ReadAllText(Path.Combine(restoreSettings.RunDirectory, "trace.jsonl"))
                check (restoreTrace.Contains("project-storage-restore-failure", StringComparison.Ordinal) && restoreTrace.Contains("PROJECT_HISTORY_REPARSE_POINT", StringComparison.Ordinal)) "rollback trace records a structured reparse error"
                check (File.Exists restoreSentinel) "external task history remains present after rollback"
                equal "external task log must survive rollback" (File.ReadAllText(restoreSentinel)) "rollback never deletes through an external history link"
                equal [ "task-sentinel.json" ] (Directory.GetFiles(restoreExternal) |> Array.map (fun path -> Path.GetFileName(path)) |> Array.toList) "rollback does not add or remove external task files"
            finally
                removeDirectoryLink restoreHistory
        else
            printfn "SKIP history symlink fixtures: this host does not permit creating links"

        let sizeSettings = config root Growing "history-size-limit" [ evalOracle "1" "1" ]
        let oversizedPath = Path.Combine(sizeSettings.ProjectDirectory, "history", "task-large.json")
        Directory.CreateDirectory(Path.GetDirectoryName oversizedPath) |> ignore
        do
            use oversized = new FileStream(oversizedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)
            oversized.SetLength(int64 AgentLang.StorageLimits.MaxMetadataBytes + 1L)
        let sizeProvider = ScriptedProvider(JsonArray())
        let sizeResult = runWith sizeSettings (sizeProvider :> IAgentProvider)
        equal (Some "PROJECT_HISTORY_LIMIT") sizeResult.FailureCode "oversized task history is rejected before loading"
        check (File.Exists oversizedPath) "history size rejection preserves the oversized file"

    let private testLibraryCoverageThroughTools root =
        let settings = config root Growing "library-coverage" [ step "test-all" (JsonObject()) None None (Some 2) ]
        let firstSource = """word library.flag : Bool -> Bool
    effects none
    if
        true
    else
        false
    end
end

test library.flag/true
    true library.flag
    => true
end
"""
        let define = functionCall "library-define" "agentlang_define" (System.Text.Json.JsonSerializer.Serialize({| source = firstSource; lifetime = "candidate" |}))
        let firstCommit = functionCall "library-commit-incomplete" "agentlang_task" (System.Text.Json.JsonSerializer.Serialize({| action = "commit-word"; word = "library.flag"; quality = "library" |}))
        let secondSource = """test library.flag/false
    false library.flag
    => false
end
"""
        let addMissingCase = functionCall "library-add-case" "agentlang_define" (System.Text.Json.JsonSerializer.Serialize({| source = secondSource; lifetime = "candidate" |}))
        let completeCommit = functionCall "library-commit-complete" "agentlang_task" (System.Text.Json.JsonSerializer.Serialize({| action = "commit-word"; word = "library.flag"; quality = "library" |}))
        let responses = JsonArray()
        responses.Add(scriptedResponse "completed" [ define ] "" None)
        responses.Add(scriptedResponse "completed" [ firstCommit ] "" None)
        responses.Add(scriptedResponse "completed" [ addMissingCase ] "" None)
        responses.Add(scriptedResponse "completed" [ completeCommit ] "" None)
        responses.Add(scriptedResponse "completed" [ outputMessage "Both conditional outcomes are covered and the library word is committed." ] "Both conditional outcomes are covered and the library word is committed." None)
        let result = runWith settings (provider responses)
        check result.Success "library definition passes the independent tests after full branch coverage"
        let describeArgs = JsonObject()
        describeArgs["word"] <- JsonValue.Create("library.flag")
        let engine = AgentLang.Runtime.Engine(settings.ProjectDirectory, Set.empty, "2000-01-01T00:00:00Z", fileSystemMode = FileSystemMode.Virtual)
        let described = engine.Dispatch("describe", describeArgs)
        equal "library" (Json.propertyString described["data"] "maturity" "") "tool quality selection retains library maturity"
        let firstCommitResult =
            File.ReadAllLines(Path.Combine(settings.RunDirectory, "trace.jsonl"))
            |> Array.map JsonNode.Parse
            |> Array.choose (fun item ->
                if Json.propertyString item "event" "" <> "runtime-tool" then None
                elif Json.propertyString item["call"] "call_id" "" <> "library-commit-incomplete" then None
                else Json.tryProperty item "result" |> Option.bind Json.asObject)
            |> Array.tryHead
        check (firstCommitResult |> Option.exists (fun value -> not (value["ok"].GetValue<bool>()) && (Json.compact value).Contains("LIBRARY_COVERAGE_INCOMPLETE"))) "an incomplete library commit is rejected but can be repaired within the same task"

    let private testFlatRequiresRunLocalProject root =
        let settings = config root Flat "flat-path-check" [ evalOracle "1 2 add" "3" ]
        let outside = { settings with ProjectDirectory = Path.Combine(root, "shared-project") }
        let agentProvider = ScriptedProvider(JsonArray())
        let rejected =
            try
                runWith outside (agentProvider :> IAgentProvider) |> ignore
                false
            with :? ArgumentException -> true
        check rejected "Flat mode rejects a project path outside the unique run directory"
        equal 0 agentProvider.ResponsesConsumed "invalid Flat path never reaches the provider"
        check (not (Directory.Exists settings.RunDirectory)) "invalid Flat path creates no run artifact directory"

    let private testEngineInitializationFailureIsReported root =
        let settings = config root Growing "engine-init-failure" [ evalOracle "1 2 add" "3" ]
        Directory.CreateDirectory(settings.ProjectDirectory) |> ignore
        let dictionaryPath = Path.Combine(settings.ProjectDirectory, "dictionary.agent")
        let invalidSource = """word invalid.dictionary : Int -> Int
    effects none
    1
end
"""
        File.WriteAllText(dictionaryPath, invalidSource)
        let initialDictionary = File.ReadAllText dictionaryPath
        let agentProvider = ScriptedProvider(JsonArray())
        let result = runWith settings (agentProvider :> IAgentProvider)
        check (not result.Success) "dictionary load failure fails the run"
        equal 0 result.RequestCount "engine initialization failure happens before a provider request"
        check (File.Exists(reportPath settings)) "engine initialization failure still writes a durable report"
        check (File.ReadAllText dictionaryPath = initialDictionary) "constructor failure preserves the pre-run project state"

    let private testContextCapAndIncompleteResponse root =
        let capSettings = config root Flat "context-cap" [ evalOracle "1 1 add" "2" ]
        let capped = { capSettings with ContextBudgetBytes = 16 }
        let neverCalled = ScriptedProvider(JsonArray())
        let capResult = runWith capped (neverCalled :> IAgentProvider)
        check (not capResult.Success) "oversized serialized request fails before provider call"
        equal (Some "CONTEXT_BUDGET_EXCEEDED") capResult.FailureCode "context cap diagnostic"
        equal 0 capResult.RequestCount "request rejected locally is not counted as sent"
        equal 0 neverCalled.ResponsesConsumed "request cap prevents provider interaction"
        let capReport = JsonNode.Parse(File.ReadAllText(reportPath capped)).AsObject()
        let capContext = capReport["context"].AsObject()
        let capUsage = capReport["tokenUsage"].AsObject()
        check (capContext["preparedRequestBytes"].GetValue<int64>() > 0L) "locally rejected request bytes are recorded as prepared"
        equal 0L (capContext["totalRequestBytes"].GetValue<int64>()) "locally rejected request bytes are excluded from bytes sent"
        check (isNull capUsage["inputTokens"]) "a run that sent no request has unknown usage, not zero"

        let incompleteSettings = config root Flat "incomplete" [ evalOracle "1 1 add" "2" ]
        let responses = JsonArray()
        responses.Add(scriptedResponse "incomplete" [] "partial output" None)
        let incomplete = runWith incompleteSettings (provider responses)
        check (not incomplete.Success) "incomplete model output cannot satisfy oracle"
        equal (Some "PROVIDER_NOT_COMPLETED") incomplete.FailureCode "incomplete response is explicitly diagnosed"
        check (not (File.Exists(Path.Combine(incompleteSettings.ProjectDirectory, "dictionary.agent")))) "incomplete run leaves the project unchanged"

    let private testGrowingAndFlatRetention root =
        let growingSeedPath = Path.Combine(root, "growing-schema.agent")
        File.WriteAllText(
            growingSeedPath,
            """type RetainedCustomerId : String
end

        record RetainedCustomer
    field id RetainedCustomerId
end
""",
            UTF8Encoding(false))
        let preMarkerFailureConfig =
            { config root Growing "growing-pre-marker-failure" [ evalOracle "10 20 add" "30" ] with
                BaselineProfile = BaselineProfile.PrimitiveOnly
                SeedDictionarySource = Some growingSeedPath
                TestFailurePoint = Some HarnessTestFailurePoint.BeforeInitialLineageMarkerWrite }
        let noProvider = ScriptedProvider(JsonArray())
        let preMarkerFailure = runWith preMarkerFailureConfig (noProvider :> IAgentProvider)
        equal (Some "HARNESS_FAILURE") preMarkerFailure.FailureCode "injected fresh-marker failure is reported"
        equal "initialization-failed" (preMarkerFailure.BaselineAudit["status"].GetValue<string>()) "failed marker initialization is not reported as an accepted origin"
        equal true (preMarkerFailure.BaselineAudit["seedSourceAppliedThisRun"].GetValue<bool>()) "failed attempt accurately records that seed bytes were applied before rollback"
        equal false (preMarkerFailure.BaselineAudit["seedSourceAppliedAtOrigin"].GetValue<bool>()) "failed attempt does not claim an uncommitted seed origin"
        equal 0 noProvider.ResponsesConsumed "initial marker failure occurs before any provider turn"
        check (not (File.Exists(Path.Combine(preMarkerFailureConfig.ProjectDirectory, ".agentlang-benchmark-lineage.json")))) "failed initial marker write leaves no lineage marker"
        match AgentLang.Storage.load (AgentLang.Storage.create preMarkerFailureConfig.ProjectDirectory) with
        | Ok loaded -> equal AgentLang.EmptyAuthority loaded.Authority "failed initial marker write restores the exact pre-seed empty authority"
        | Error error -> failwith $"Could not check pre-marker rollback: {error.Code}"

        let retryAfterMarkerFailure =
            { config root Growing "growing-pre-marker-retry" [ evalOracle "10 20 add" "30" ] with
                BaselineProfile = BaselineProfile.PrimitiveOnly
                ProjectDirectory = preMarkerFailureConfig.ProjectDirectory
                SeedDictionarySource = Some growingSeedPath }
        let retryResponses = JsonArray()
        retryResponses.Add(scriptedResponse "completed" [ outputMessage "The schema seed was applied once." ] "The schema seed was applied once." None)
        let retryResult = runWith retryAfterMarkerFailure (provider retryResponses)
        check retryResult.Success "fresh Growing retry succeeds after pre-marker rollback"
        equal true (retryResult.BaselineAudit["seedSourceAppliedThisRun"].GetValue<bool>()) "retry audit records that the seed was actually applied"
        equal true (retryResult.BaselineAudit["seedSourceAppliedAtOrigin"].GetValue<bool>()) "retry lineage preserves seed-applied origin provenance"
        let source = """word reused.increment : Int -> Int
    effects none
    1 add
end

test reused.increment/basic
    41 reused.increment
    => 42
end
"""
        let growingOne =
            { config root Growing "growing-one" [ step "test-all" (JsonObject()) None None (Some 1) ] with
                BaselineProfile = BaselineProfile.PrimitiveOnly
                SeedDictionarySource = Some growingSeedPath }
        let defineCall = functionCall "define" "agentlang_define" (System.Text.Json.JsonSerializer.Serialize({| source = source; lifetime = "candidate" |}))
        let firstResponses = JsonArray()
        firstResponses.Add(scriptedResponse "completed" [ defineCall ] "" None)
        firstResponses.Add(scriptedResponse "completed" [ outputMessage "I added and tested the reusable word." ] "I added and tested the reusable word." None)
        let first = runWith growingOne (provider firstResponses)
        check first.Success "first Growing run commits a tested abstraction"
        equal "primitive-only" (first.BaselineAudit["profile"].GetValue<string>()) "Growing report records the strict primitive-only profile"
        equal true (first.BaselineAudit["seedSourceAppliedThisRun"].GetValue<bool>()) "fresh Growing audit records that the supplied schema seed was applied on this run"
        equal true (first.BaselineAudit["seedSourceAppliedAtOrigin"].GetValue<bool>()) "fresh Growing lineage records its seed application provenance"
        let firstLineage = first.BaselineAudit["lineage"].AsObject()
        let firstOriginInventory = first.BaselineAudit["originInventory"].AsObject()
        let firstTaskStartInventory = first.BaselineAudit["taskStartInventory"].AsObject()
        equal "fresh" (firstLineage["status"].GetValue<string>()) "first Growing task establishes an audited fresh lineage"
        equal 0 (firstOriginInventory["authoredWords"].AsArray().Count) "primitive-only origin contains no authored algorithms"
        equal 1 (firstOriginInventory["records"].AsArray().Count) "primitive-only origin may retain a user-supplied type declaration"
        equal 0 (firstTaskStartInventory["authoredWords"].AsArray().Count) "first task began without seeded algorithms"
        check (File.Exists(Path.Combine(growingOne.ProjectDirectory, ".agentlang-benchmark-lineage.json"))) "Growing profile marker is persisted with the project"
        let firstManifest =
            match AgentLang.Storage.load (AgentLang.Storage.create growingOne.ProjectDirectory) with
            | Ok loaded -> loaded.Manifest |> Option.get
            | Error error -> failwith $"Could not read committed first-run manifest: {error.Code}"
        let originalHead = firstManifest.Words |> List.find (fun head -> head.CurrentName = "reused.increment")
        let advisoryExport = Path.Combine(growingOne.ProjectDirectory, "dictionary.agent")
        check (File.Exists advisoryExport) "successful commit created the human-readable export fixture"
        File.Delete advisoryExport

        let existingArgs = JsonObject()
        existingArgs["word"] <- JsonValue.Create("reused.increment")
        let growingTwo =
            { config root Growing "growing-two" [ step "source" existingArgs (Some "word reused.increment") None None ] with
                BaselineProfile = BaselineProfile.PrimitiveOnly
                ProjectDirectory = growingOne.ProjectDirectory
                SeedDictionarySource = Some growingSeedPath }
        let secondResponses = JsonArray()
        secondResponses.Add(scriptedResponse "completed" [ outputMessage "The retained source is available." ] "The retained source is available." None)
        let second = runWith growingTwo (provider secondResponses)
        check second.Success "later Growing run discovers the committed word"
        let secondLineage = second.BaselineAudit["lineage"].AsObject()
        let secondOriginInventory = second.BaselineAudit["originInventory"].AsObject()
        let secondTaskStartInventory = second.BaselineAudit["taskStartInventory"].AsObject()
        equal "continued" (secondLineage["status"].GetValue<string>()) "later Growing task proves canonical lineage continuity"
        equal false (second.BaselineAudit["seedSourceAppliedThisRun"].GetValue<bool>()) "existing manifest is not reseeded during continuation"
        equal true (second.BaselineAudit["seedSourceAppliedAtOrigin"].GetValue<bool>()) "continuation preserves the origin seed application provenance"
        equal 0 (secondOriginInventory["authoredWords"].AsArray().Count) "later report retains the original primitive-only origin inventory"
        equal 1 (secondTaskStartInventory["authoredWords"].AsArray().Count) "later task sees the vocabulary grown by the first task"
        let retainedWord = secondTaskStartInventory["authoredWords"].AsArray() |> Seq.find (fun item -> item["name"].GetValue<string>() = "reused.increment")
        equal originalHead.WordId (retainedWord["stableId"].GetValue<string>()) "continuation preserves the retained word identity when the export is absent"
        equal originalHead.CurrentRevision (retainedWord["revision"].GetValue<int>()) "continuation preserves the retained word revision when the export is absent"
        let secondTrace = File.ReadAllLines(Path.Combine(growingTwo.RunDirectory, "trace.jsonl")) |> Array.map JsonNode.Parse
        check (secondTrace |> Array.forall (fun item -> item["event"].GetValue<string>() <> "seed-define")) "a missing readable export never causes the supplied schema seed to be reapplied"

        File.WriteAllText(advisoryExport, "word export.poison : Int -> Int\n    effects none\n    99 add\nend\n", UTF8Encoding(false))

        let failedContinuation =
            { config root Growing "growing-rollback" [ evalOracle "1 2 add" "3" ] with
                BaselineProfile = BaselineProfile.PrimitiveOnly
                ProjectDirectory = growingOne.ProjectDirectory
                SeedDictionarySource = Some growingSeedPath }
        let incompleteResponses = JsonArray()
        incompleteResponses.Add(scriptedResponse "incomplete" [] "partial" None)
        let incompleteProvider = ScriptedProvider(incompleteResponses)
        let failedRun = runWith failedContinuation (incompleteProvider :> IAgentProvider)
        check (not failedRun.Success) "an incomplete task is rolled back"
        equal true (failedRun.BaselineAudit["rollbackContinuityVerified"].GetValue<bool>()) "rollback restores the task-start canonical durable state"
        let failedTaskStartInventory = failedRun.BaselineAudit["taskStartInventory"].AsObject()
        let auditedNames = failedTaskStartInventory["authoredWords"].AsArray() |> Seq.map (fun item -> item["name"].GetValue<string>()) |> Set.ofSeq
        check (auditedNames.Contains "reused.increment") "the manifest-backed retained word remains in the task-start audit with a corrupt export"
        check (not (auditedNames.Contains "export.poison")) "inventory ignores words forged only in the readable export"

        let afterRollback =
            { config root Growing "growing-after-rollback" [ step "source" existingArgs (Some "word reused.increment") None None ] with
                BaselineProfile = BaselineProfile.PrimitiveOnly
                ProjectDirectory = growingOne.ProjectDirectory
                SeedDictionarySource = Some growingSeedPath }
        let afterRollbackResponses = JsonArray()
        afterRollbackResponses.Add(scriptedResponse "completed" [ outputMessage "The retained word remains available after rollback." ] "The retained word remains available after rollback." None)
        let afterRollbackResult = runWith afterRollback (provider afterRollbackResponses)
        check afterRollbackResult.Success "a failed task leaves the Growing lineage usable"
        let afterRollbackLineage = afterRollbackResult.BaselineAudit["lineage"].AsObject()
        equal "continued" (afterRollbackLineage["status"].GetValue<string>()) "post-rollback run continues the same lineage"

        let postMarkerFailureConfig =
            { config root Growing "growing-post-marker-failure" [ evalOracle "1 2 add" "3" ] with
                BaselineProfile = BaselineProfile.PrimitiveOnly
                ProjectDirectory = growingOne.ProjectDirectory
                SeedDictionarySource = Some growingSeedPath
                TestFailurePoint = Some HarnessTestFailurePoint.AfterFinalLineageMarkerWrite }
        let postMarkerResponses = JsonArray()
        postMarkerResponses.Add(scriptedResponse "completed" [ outputMessage "The task reached its final marker checkpoint." ] "The task reached its final marker checkpoint." None)
        let postMarkerProvider = ScriptedProvider(postMarkerResponses)
        let postMarkerFailure = runWith postMarkerFailureConfig (postMarkerProvider :> IAgentProvider)
        equal (Some "HARNESS_FAILURE") postMarkerFailure.FailureCode "injected post-marker failure is reported"
        equal true (postMarkerFailure.BaselineAudit["rollbackContinuityVerified"].GetValue<bool>()) "post-marker failure restores the task-start durable state"
        equal true (postMarkerFailure.BaselineAudit["lineageReconciledAfterRollback"].GetValue<bool>()) "rollback reconciles the committed marker to its restored durable state"
        equal true (postMarkerFailure.BaselineAudit["rollbackLineageContinuityVerified"].GetValue<bool>()) "rollback confirms the repaired lineage marker"
        let rollbackHash = postMarkerFailure.BaselineAudit["rollbackCanonicalDurableStateSha256"].GetValue<string>()
        let finalHash = postMarkerFailure.BaselineAudit["finalCanonicalDurableStateSha256"].GetValue<string>()
        equal rollbackHash finalHash "reported final lineage hash matches the restored task-start hash"

        let verifyReconciledMarker =
            { config root Growing "growing-post-marker-recovery" [ step "source" existingArgs (Some "word reused.increment") None None ] with
                BaselineProfile = BaselineProfile.PrimitiveOnly
                ProjectDirectory = growingOne.ProjectDirectory
                SeedDictionarySource = Some growingSeedPath }
        let verifyMarkerResponses = JsonArray()
        verifyMarkerResponses.Add(scriptedResponse "completed" [ outputMessage "The reconciled lineage accepts the next task." ] "The reconciled lineage accepts the next task." None)
        let verifiedMarker = runWith verifyReconciledMarker (provider verifyMarkerResponses)
        check verifiedMarker.Success "the next Growing task accepts the reconciled marker after rollback"

        let lineageMarker = Path.Combine(growingOne.ProjectDirectory, ".agentlang-benchmark-lineage.json")
        let savedLineage = File.ReadAllText(lineageMarker)
        File.Delete lineageMarker
        let missingLineageSettings =
            { config root Growing "growing-missing-lineage" [ evalOracle "1 2 add" "3" ] with
                BaselineProfile = BaselineProfile.PrimitiveOnly
                ProjectDirectory = growingOne.ProjectDirectory }
        let missingLineageProvider = ScriptedProvider(JsonArray())
        let missingLineage = runWith missingLineageSettings (missingLineageProvider :> IAgentProvider)
        equal (Some "BASELINE_LINEAGE_MISSING") missingLineage.FailureCode "primitive-only Growing refuses unverified prior history without its lineage marker"
        equal 0 missingLineageProvider.ResponsesConsumed "missing-lineage refusal happens before provider invocation"
        File.WriteAllText(lineageMarker, savedLineage, UTF8Encoding(false))

        let malformedInventoryMarker = JsonNode.Parse(savedLineage).AsObject()
        malformedInventoryMarker["originInventory"].AsObject().Remove("records") |> ignore
        File.WriteAllText(lineageMarker, malformedInventoryMarker.ToJsonString(), UTF8Encoding(false))
        let malformedInventoryConfig =
            { config root Growing "growing-malformed-inventory-marker" [ evalOracle "1 2 add" "3" ] with
                BaselineProfile = BaselineProfile.PrimitiveOnly
                ProjectDirectory = growingOne.ProjectDirectory }
        let malformedInventoryProvider = ScriptedProvider(JsonArray())
        let malformedInventory = runWith malformedInventoryConfig (malformedInventoryProvider :> IAgentProvider)
        equal (Some "BASELINE_LINEAGE_INVALID") malformedInventory.FailureCode "lineage marker requires every origin inventory collection"
        equal 0 malformedInventoryProvider.ResponsesConsumed "malformed inventory is rejected before provider invocation"

        let malformedHashMarker = JsonNode.Parse(savedLineage).AsObject()
        malformedHashMarker["originCanonicalDurableStateSha256"] <- JsonValue.Create(String('A', 64))
        File.WriteAllText(lineageMarker, malformedHashMarker.ToJsonString(), UTF8Encoding(false))
        let malformedHashConfig =
            { config root Growing "growing-malformed-hash-marker" [ evalOracle "1 2 add" "3" ] with
                BaselineProfile = BaselineProfile.PrimitiveOnly
                ProjectDirectory = growingOne.ProjectDirectory }
        let malformedHashProvider = ScriptedProvider(JsonArray())
        let malformedHash = runWith malformedHashConfig (malformedHashProvider :> IAgentProvider)
        equal (Some "BASELINE_LINEAGE_INVALID") malformedHash.FailureCode "lineage marker requires lowercase hexadecimal SHA-256 hashes"
        equal 0 malformedHashProvider.ResponsesConsumed "malformed hash is rejected before provider invocation"

        let malformedSeedProvenanceMarker = JsonNode.Parse(savedLineage).AsObject()
        malformedSeedProvenanceMarker["seedSourceSha256"] <- null
        malformedSeedProvenanceMarker["seedSourceApplied"] <- JsonValue.Create(true)
        File.WriteAllText(lineageMarker, malformedSeedProvenanceMarker.ToJsonString(), UTF8Encoding(false))
        let malformedSeedProvenanceConfig =
            { config root Growing "growing-malformed-seed-provenance-marker" [ evalOracle "1 2 add" "3" ] with
                BaselineProfile = BaselineProfile.PrimitiveOnly
                ProjectDirectory = growingOne.ProjectDirectory }
        let malformedSeedProvenanceProvider = ScriptedProvider(JsonArray())
        let malformedSeedProvenance = runWith malformedSeedProvenanceConfig (malformedSeedProvenanceProvider :> IAgentProvider)
        equal (Some "BASELINE_LINEAGE_INVALID") malformedSeedProvenance.FailureCode "a lineage cannot claim a seed was applied without recording its source hash"
        equal 0 malformedSeedProvenanceProvider.ResponsesConsumed "invalid seed provenance is rejected before provider invocation"
        File.WriteAllText(lineageMarker, savedLineage, UTF8Encoding(false))

        let switchedProfile =
            { config root Growing "growing-profile-switch" [ evalOracle "1 2 add" "3" ] with
                BaselineProfile = BaselineProfile.DomainSeededControl
                ProjectDirectory = growingOne.ProjectDirectory }
        let switchedProvider = ScriptedProvider(JsonArray())
        let switched = runWith switchedProfile (switchedProvider :> IAgentProvider)
        let switchFailureMessage = switched.FailureMessage |> Option.defaultValue "none"
        equal (Some "BASELINE_PROFILE_MISMATCH") switched.FailureCode $"Growing lineage refuses a baseline profile switch ({switchFailureMessage})"
        equal 0 switchedProvider.ResponsesConsumed "profile-switch refusal happens before provider invocation"

        let external = AgentLang.Runtime.Engine(growingOne.ProjectDirectory, Set.empty, "2000-01-01T00:00:00Z", fileSystemMode = FileSystemMode.Virtual)
        let externalSource = """word external.change : Int -> Int
    effects none
    2 add
end

test external.change/basic
    1 external.change
    => 3
end
"""
        let externalDefine = JsonObject()
        externalDefine["source"] <- JsonValue.Create(externalSource)
        externalDefine["frontend"] <- JsonValue.Create("stack")
        let externalDefinitionResult = external.Dispatch("define", externalDefine)
        check (externalDefinitionResult["ok"].GetValue<bool>()) "test fixture stages an external durable change"
        let externalCommitResult = external.Dispatch("commit", JsonObject())
        check (externalCommitResult["ok"].GetValue<bool>()) "test fixture commits an external durable change"
        let mismatch =
            { config root Growing "growing-lineage-mismatch" [ evalOracle "1 2 add" "3" ] with
                BaselineProfile = BaselineProfile.PrimitiveOnly
                ProjectDirectory = growingOne.ProjectDirectory }
        let mismatchProvider = ScriptedProvider(JsonArray())
        let mismatchResult = runWith mismatch (mismatchProvider :> IAgentProvider)
        equal (Some "BASELINE_LINEAGE_MISMATCH") mismatchResult.FailureCode "untracked canonical durable edits invalidate Growing continuation"
        equal 0 mismatchProvider.ResponsesConsumed "lineage mismatch is rejected before provider invocation"
        let mismatchRetry =
            { config root Growing "growing-lineage-mismatch-retry" [ evalOracle "1 2 add" "3" ] with
                BaselineProfile = BaselineProfile.PrimitiveOnly
                ProjectDirectory = growingOne.ProjectDirectory }
        let mismatchRetryProvider = ScriptedProvider(JsonArray())
        let mismatchRetryResult = runWith mismatchRetry (mismatchRetryProvider :> IAgentProvider)
        equal (Some "BASELINE_LINEAGE_MISMATCH") mismatchRetryResult.FailureCode "rejected pre-task edits do not rewrite lineage to silently adopt them"
        equal 0 mismatchRetryProvider.ResponsesConsumed "mismatched lineage remains rejected before the provider on retry"

        let flatOne = config root Flat "flat-one" [ step "test-all" (JsonObject()) None None (Some 1) ]
        let flatCall = functionCall "define-flat" "agentlang_define" (System.Text.Json.JsonSerializer.Serialize({| source = source; lifetime = "candidate" |}))
        let flatResponses = JsonArray()
        flatResponses.Add(scriptedResponse "completed" [ flatCall ] "" None)
        flatResponses.Add(scriptedResponse "completed" [ outputMessage "Added in this flat run." ] "Added in this flat run." None)
        let flatResult = runWith flatOne (provider flatResponses)
        check flatResult.Success "Flat run can create and commit its own tested word"
        let flatTaskStartInventory = flatResult.BaselineAudit["taskStartInventory"].AsObject()
        equal 0 (flatTaskStartInventory["authoredWords"].AsArray().Count) "fresh Flat run reports an empty task-start word inventory"
        equal "domain-seeded-control" (flatResult.BaselineAudit["profile"].GetValue<string>()) "baseline profile is independent from Flat retention mode"

        let missingArgs = JsonObject()
        missingArgs["word"] <- JsonValue.Create("reused.increment")
        let flatTwo = config root Flat "flat-two" [ step "source" missingArgs None None None ]
        let noReuseTask = { flatTwo.Task with Oracle = [ { flatTwo.Task.Oracle.Head with ExpectedOk = false } ] }
        let flatTwo = { flatTwo with Task = noReuseTask }
        let missingResponses = JsonArray()
        missingResponses.Add(scriptedResponse "completed" [ outputMessage "The isolated dictionary has no earlier word." ] "The isolated dictionary has no earlier word." None)
        let missing = runWith flatTwo (provider missingResponses)
        check missing.Success "separate Flat run starts without definitions from another Flat run"

    let private testBaselineInventoryGuard root =
        let typeOnlyPath = Path.Combine(root, "type-only-baseline.agent")
        let typeOnlySource = """type TrackingCode : String
end

record PilotSchema
    field code TrackingCode
end
"""
        File.WriteAllText(typeOnlyPath, typeOnlySource, Encoding.UTF8)
        let typeOnlySettings =
            { config root Flat "primitive-type-only" [ evalOracle "10 20 add" "30" ] with
                BaselineProfile = BaselineProfile.PrimitiveOnly
                SeedDictionarySource = Some typeOnlyPath }
        let typeOnlyResponses = JsonArray()
        typeOnlyResponses.Add(scriptedResponse "completed" [ outputMessage "The type-only baseline is ready." ] "The type-only baseline is ready." None)
        let typeOnlyProvider = ScriptedProvider(typeOnlyResponses)
        let typeOnly = runWith typeOnlySettings (typeOnlyProvider :> IAgentProvider)
        check typeOnly.Success "primitive-only baseline accepts supplied record and nominal scalar declarations"
        equal 1 typeOnlyProvider.ResponsesConsumed "accepted type-only baseline reaches the provider"
        equal "primitive-only" (typeOnly.BaselineAudit["profile"].GetValue<string>()) "report names the primitive-only baseline profile"
        equal "user-supplied schema; source hash and exact declarations are recorded" (typeOnly.BaselineAudit["schemaProvenance"].GetValue<string>()) "report does not imply the user-supplied schema is an official matched fixture"
        equal "not established; this audit records declarations and source identity only" (typeOnly.BaselineAudit["contractEquivalence"].GetValue<string>()) "inventory hash does not claim semantic fixture equivalence"
        equal true (typeOnly.BaselineAudit["seedSourceAppliedThisRun"].GetValue<bool>()) "Flat type-only audit records that the seed was applied on this run"
        equal true (typeOnly.BaselineAudit["seedSourceAppliedAtOrigin"].GetValue<bool>()) "Flat type-only audit records the origin seed application"
        let expectedSeedHash =
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes typeOnlyPath)
            |> Convert.ToHexString
            |> fun value -> value.ToLowerInvariant()
        equal expectedSeedHash (typeOnly.BaselineAudit["seedSourceSha256"].GetValue<string>()) "audit preserves the exact raw seed file hash"
        let typeInventory = typeOnly.BaselineAudit["taskStartInventory"]
        equal 0 (typeInventory["authoredWords"].AsArray().Count) "type-only inventory contains no authored words"
        equal 1 (typeInventory["records"].AsArray().Count) "record declaration is captured in exact inventory"
        equal 1 (typeInventory["scalars"].AsArray().Count) "nominal scalar declaration is captured in exact inventory"
        equal 0 (typeInventory["tests"].AsArray().Count) "type-only baseline cannot conceal authored tests"
        equal 0 (typeInventory["examples"].AsArray().Count) "type-only baseline cannot conceal authored examples"
        let savedReport = JsonNode.Parse(File.ReadAllText(reportPath typeOnlySettings))
        let savedReportBaselineAudit = savedReport["baselineAudit"].AsObject()
        equal "primitive-only" (savedReportBaselineAudit["profile"].GetValue<string>()) "report.json persists the selected baseline profile"
        let savedInitialState = JsonNode.Parse(File.ReadAllText(Path.Combine(typeOnlySettings.RunDirectory, "initial-state.json")))
        let savedInitialStateBaselineAudit = savedInitialState["baselineAudit"].AsObject()
        equal "primitive-only" (savedInitialStateBaselineAudit["profile"].GetValue<string>()) "initial-state.json persists the audited profile"

        let businessSeed = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "examples", "legacy", "customer.agent"))
        let rejectedSettings =
            { config root Flat "primitive-rejects-domain-seed" [ evalOracle "1 2 add" "3" ] with
                BaselineProfile = BaselineProfile.PrimitiveOnly
                SeedDictionarySource = Some businessSeed }
        let rejectedProvider = ScriptedProvider(JsonArray())
        let rejected = runWith rejectedSettings (rejectedProvider :> IAgentProvider)
        check (not rejected.Success) "primitive-only baseline rejects an authored business algorithm seed"
        equal (Some "BASELINE_INVENTORY_MISMATCH") rejected.FailureCode "authored-seed refusal has a machine-readable diagnostic"
        equal 0 rejectedProvider.ResponsesConsumed "authored algorithms are rejected before the provider is called"
        check rejectedProvider.RequestBodies.IsEmpty "rejected authored seed produced no provider request"
        let rejectedTaskStartInventory = rejected.BaselineAudit["taskStartInventory"].AsObject()
        let observedWords = rejectedTaskStartInventory["authoredWords"].AsArray()
        check (observedWords |> Seq.exists (fun item -> item["name"].GetValue<string>() = "customer.premium?")) "rejection report publishes the exact observed authored word names"
        equal 4 (rejectedTaskStartInventory["tests"].AsArray().Count) "rejection report counts and identifies authored tests"
        equal 1 (rejectedTaskStartInventory["examples"].AsArray().Count) "rejection report identifies authored examples"
        equal "BASELINE_INVENTORY_MISMATCH" (rejected.BaselineAudit["rejectionCode"].GetValue<string>()) "rejection artifact records the guard diagnostic"

        let controlSettings =
            { config root Flat "domain-seeded-control-audit" [ step "test-all" (JsonObject()) None None (Some 4) ] with
                SeedDictionarySource = Some businessSeed }
        let controlResponses = JsonArray()
        controlResponses.Add(scriptedResponse "completed" [ outputMessage "The seeded control passes its tests." ] "The seeded control passes its tests." None)
        let control = runWith controlSettings (provider controlResponses)
        check control.Success "the default domain-seeded control remains available for legacy scripted experiments"
        equal "domain-seeded-control" (control.BaselineAudit["profile"].GetValue<string>()) "legacy seed runs are labeled as a separate control"
        let controlTaskStartInventory = control.BaselineAudit["taskStartInventory"].AsObject()
        let controlWords = controlTaskStartInventory["authoredWords"].AsArray()
        equal 2 controlWords.Count "domain-seeded control report records its exact authored word inventory"
        check (controlWords |> Seq.forall (fun item -> not (isNull item["stableId"]))) "manifest-backed authored words include their stable identities"
        let premiumWord = controlWords |> Seq.find (fun item -> item["name"].GetValue<string>() = "customer.premium?")
        let premiumInputs = premiumWord["inputs"].AsArray()
        equal "Customer" (premiumInputs[0].GetValue<string>()) "authored inventory records signatures"
        equal "project" (premiumWord["maturity"].GetValue<string>()) "authored inventory records maturity"
        equal 1 (premiumWord["revision"].GetValue<int>()) "authored inventory records current revision"

    let private testTemporaryDefinitionIsTaskScoped root =
        let settings = config root Growing "temporary-lifetime" [ evalOracle "41 task.local" "42" ]
        let source = """word task.local : Int -> Int
    effects none
    1 add
end
"""
        let defineCall = functionCall "temp-define" "agentlang_define" (System.Text.Json.JsonSerializer.Serialize({| source = source; lifetime = "temporary" |}))
        let evalCall = functionCall "temp-eval" "agentlang_eval" "{\"code\":\"41 task.local\"}"
        let responses = JsonArray()
        responses.Add(scriptedResponse "completed" [ defineCall ] "" None)
        responses.Add(scriptedResponse "completed" [ evalCall ] "" None)
        responses.Add(scriptedResponse "completed" [ outputMessage "The temporary word works for this task." ] "The temporary word works for this task." None)
        let result = runWith settings (provider responses)
        check result.Success "temporary definition runs and passes task oracle"
        let dictionaryPath = Path.Combine(settings.ProjectDirectory, "dictionary.agent")
        check (not (File.Exists dictionaryPath) || not (File.ReadAllText(dictionaryPath).Contains("task.local"))) "task commit does not persist a temporary word"

    type private CannedHandler(body: string, statusCode: HttpStatusCode, onRequest: HttpRequestMessage -> string -> unit) =
        inherit HttpMessageHandler()
        override _.SendAsync(request, cancellationToken) =
            task {
                let! requestBody = request.Content.ReadAsStringAsync(cancellationToken)
                onRequest request requestBody
                let response = new HttpResponseMessage(statusCode)
                response.Content <- new StringContent(body, Encoding.UTF8, "application/json")
                return response
            }

    let private testOpenAiWireAndCredentialIsolation () =
        let mutable observedAuth = ""
        let mutable observedBody = ""
        let raw = """{"id":"resp-1","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"done"}]}],"output_text":"done","usage":{"input_tokens":23,"output_tokens":4,"total_tokens":27}}"""
        let handler =
            new CannedHandler(raw, HttpStatusCode.OK, fun request body ->
                observedBody <- body
                observedAuth <- request.Headers.Authorization.ToString())
        use provider = new OpenAiResponsesProvider("test-secret-key", handler = handler, endpoint = "https://example.invalid/v1/responses")
        let request =
            { Model = "test-model"
              Instructions = "test instructions"
              Input = (let items = JsonArray() in items.Add(json "{\"role\":\"user\",\"content\":\"hello\"}"); items)
              Tools = AgentTools.definitions ()
              MaxOutputTokens = 321 }
        let response = (provider :> IAgentProvider).Complete request |> fun pending -> pending.GetAwaiter().GetResult()
        equal "Bearer test-secret-key" observedAuth "API key is sent only in authorization header"
        check (not (observedBody.Contains("test-secret-key"))) "API key is absent from serialized request body"
        check (observedBody.Contains("\"store\":false")) "OpenAI request body disables response storage"
        check (observedBody.Contains("\"max_output_tokens\":321")) "OpenAI request has an explicit output token ceiling"
        check (observedBody.Contains("reasoning.encrypted_content")) "OpenAI request asks for encrypted reasoning items"
        let wire = JsonNode.Parse(observedBody).AsObject()
        check (isNull wire["previous_response_id"]) "OpenAI requests do not use hidden remote response history"
        let inspectTool = wire["tools"].AsArray() |> Seq.find (fun item -> item["name"].GetValue<string>() = "agentlang_inspect")
        equal 6 (wire["tools"].AsArray().Count) "the serialized function surface remains exactly six tools"
        check (inspectTool["strict"].GetValue<bool>()) "function tool schema uses strict mode"
        let parameters = inspectTool["parameters"].AsObject()
        check (parameters["additionalProperties"].GetValue<bool>() = false) "function schema rejects unspecified arguments"
        check (parameters["required"].AsArray().Count = 2) "strict tool schema requires all properties"
        let properties = parameters["properties"].AsObject()
        let operation = properties["operation"].AsObject()
        let operationChoices = operation["enum"].AsArray()
        check (operationChoices |> Seq.exists (fun item -> item.GetValue<string>() = "help")) "the existing inspect enum exposes help without another tool"
        equal "completed" response.Status "OpenAI adapter parses response status"
        equal "done" response.OutputText "OpenAI adapter extracts final text"
        equal (Some 23) (response.Usage |> Option.bind (fun usage -> usage.InputTokens)) "known usage input count is preserved"

    let private testOpenAiErrorBodyDoesNotLeakCredential root =
        let secret = "error-secret-key"
        let mutable observedAuth = ""
        let handler =
            new CannedHandler($"{{\"error\":{{\"message\":\"invalid credential {secret}\"}}}}", HttpStatusCode.Unauthorized, fun request _ ->
                observedAuth <- request.Headers.Authorization.ToString())
        use agentProvider = new OpenAiResponsesProvider(secret, handler = handler, endpoint = "https://example.invalid/v1/responses")
        let settings = config root Flat "api-error-redaction" [ evalOracle "1 2 add" "3" ]
        let result = runWith settings (agentProvider :> IAgentProvider)
        check (not result.Success) "HTTP errors fail the live-provider harness run"
        equal (Some "PROVIDER_HTTP_ERROR") result.FailureCode "HTTP error uses sanitized provider failure code"
        equal "Bearer error-secret-key" observedAuth "credential is used in the authorization header"
        let report = JsonNode.Parse(File.ReadAllText(reportPath settings))
        let usage = report["tokenUsage"].AsObject()
        check (isNull usage["inputTokens"] && isNull usage["outputTokens"] && isNull usage["totalTokens"]) "usage stays unknown when the provider fails after send"
        let savedFiles = Directory.GetFiles(settings.RunDirectory, "*", SearchOption.AllDirectories)
        for path in savedFiles do
            if not (Directory.Exists path) then
                check (not (File.ReadAllText(path).Contains(secret, StringComparison.Ordinal))) $"credential is absent from saved artifact {Path.GetFileName path}"
        let failureMessage = result.FailureMessage |> Option.defaultValue ""
        check (not (failureMessage.Contains(secret, StringComparison.Ordinal))) "sanitized report message does not include the API error body"

    [<EntryPoint>]
    let main _ =
        let root = makeTempRoot ()
        try
            testStatelessToolLoopAndLogs root
            testStrictRuntimeToolWhitelist root
            testHelpThroughInspectTool root
            testInitialPromptIncludesLanguagePrimer ()
            testFlowFrontendSeedAndToolDispatch root
            testManifestInventoryUsesCurrentMixedFrontendHeads root
            testToolLimitRollsBackCandidates root
            testHistorySymlinkAndSizeGuards root
            testLibraryCoverageThroughTools root
            testFlatRequiresRunLocalProject root
            testEngineInitializationFailureIsReported root
            testContextCapAndIncompleteResponse root
            testGrowingAndFlatRetention root
            testBaselineInventoryGuard root
            testTemporaryDefinitionIsTaskScoped root
            testOpenAiWireAndCredentialIsolation ()
            testOpenAiErrorBodyDoesNotLeakCredential root
            printfn "PASS: %d harness assertions" assertions
            0
        finally
            removeTempRoot root
