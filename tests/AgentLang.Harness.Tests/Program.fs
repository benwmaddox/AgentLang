namespace AgentLang.Harness.Tests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
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
          Model = "scripted-test-model"
          ProjectDirectory = projectDir
          RunDirectory = runDir
          SeedDictionarySource = None
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
        check result.Success "offline run passed its oracle"
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
        check (secondRequest["tools"].AsArray().Count <= 6) "provider receives a small runtime tool set"

        let report = JsonNode.Parse(File.ReadAllText(reportPath settings)).AsObject()
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

    let private testInitialPromptIncludesLanguagePrimer () =
        let task =
            TaskFile.parse """{"id":"prompt-primer","goal":"Create a tested word.","systemPrompt":"Prefer existing vocabulary.","oracle":[{"operation":"test-all"}]}"""
        check (task.SystemPrompt.Contains("word name : Input -> Output", StringComparison.Ordinal)) "custom task guidance always includes the definition grammar"
        check (task.SystemPrompt.Contains("=> expected-literal", StringComparison.Ordinal)) "custom task guidance includes the test grammar"
        check (task.SystemPrompt.Contains("Task-specific guidance: Prefer existing vocabulary.", StringComparison.Ordinal)) "task-specific system guidance is preserved after the language primer"

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
        let engine = AgentLang.Runtime.Engine(settings.ProjectDirectory, Set.empty, "2000-01-01T00:00:00Z")
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
        let source = """word reused.increment : Int -> Int
    effects none
    1 add
end

test reused.increment/basic
    41 reused.increment
    => 42
end
"""
        let growingOne = config root Growing "growing-one" [ step "test-all" (JsonObject()) None None (Some 1) ]
        let defineCall = functionCall "define" "agentlang_define" (System.Text.Json.JsonSerializer.Serialize({| source = source; lifetime = "candidate" |}))
        let firstResponses = JsonArray()
        firstResponses.Add(scriptedResponse "completed" [ defineCall ] "" None)
        firstResponses.Add(scriptedResponse "completed" [ outputMessage "I added and tested the reusable word." ] "I added and tested the reusable word." None)
        let first = runWith growingOne (provider firstResponses)
        check first.Success "first Growing run commits a tested abstraction"

        let existingArgs = JsonObject()
        existingArgs["word"] <- JsonValue.Create("reused.increment")
        let growingTwo = config root Growing "growing-two" [ step "source" existingArgs (Some "word reused.increment") None None ]
        let secondResponses = JsonArray()
        secondResponses.Add(scriptedResponse "completed" [ outputMessage "The retained source is available." ] "The retained source is available." None)
        let second = runWith growingTwo (provider secondResponses)
        check second.Success "later Growing run discovers the committed word"

        let flatOne = config root Flat "flat-one" [ step "test-all" (JsonObject()) None None (Some 1) ]
        let flatCall = functionCall "define-flat" "agentlang_define" (System.Text.Json.JsonSerializer.Serialize({| source = source; lifetime = "candidate" |}))
        let flatResponses = JsonArray()
        flatResponses.Add(scriptedResponse "completed" [ flatCall ] "" None)
        flatResponses.Add(scriptedResponse "completed" [ outputMessage "Added in this flat run." ] "Added in this flat run." None)
        let flatResult = runWith flatOne (provider flatResponses)
        check flatResult.Success "Flat run can create and commit its own tested word"

        let missingArgs = JsonObject()
        missingArgs["word"] <- JsonValue.Create("reused.increment")
        let flatTwo = config root Flat "flat-two" [ step "source" missingArgs None None None ]
        let noReuseTask = { flatTwo.Task with Oracle = [ { flatTwo.Task.Oracle.Head with ExpectedOk = false } ] }
        let flatTwo = { flatTwo with Task = noReuseTask }
        let missingResponses = JsonArray()
        missingResponses.Add(scriptedResponse "completed" [ outputMessage "The isolated dictionary has no earlier word." ] "The isolated dictionary has no earlier word." None)
        let missing = runWith flatTwo (provider missingResponses)
        check missing.Success "separate Flat run starts without definitions from another Flat run"

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
        check (inspectTool["strict"].GetValue<bool>()) "function tool schema uses strict mode"
        let parameters = inspectTool["parameters"].AsObject()
        check (parameters["additionalProperties"].GetValue<bool>() = false) "function schema rejects unspecified arguments"
        check (parameters["required"].AsArray().Count = 2) "strict tool schema requires all properties"
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
            testInitialPromptIncludesLanguagePrimer ()
            testToolLimitRollsBackCandidates root
            testHistorySymlinkAndSizeGuards root
            testLibraryCoverageThroughTools root
            testFlatRequiresRunLocalProject root
            testEngineInitializationFailureIsReported root
            testContextCapAndIncompleteResponse root
            testGrowingAndFlatRetention root
            testTemporaryDefinitionIsTaskScoped root
            testOpenAiWireAndCredentialIsolation ()
            testOpenAiErrorBodyDoesNotLeakCredential root
            printfn "PASS: %d harness assertions" assertions
            0
        finally
            removeTempRoot root
