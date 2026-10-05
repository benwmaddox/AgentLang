namespace AgentLang.Benchmarks

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open AgentLang

type RunMode =
    | Flat
    | Growing

type OracleStep =
    { Operation: string
      Arguments: JsonObject
      ExpectedOk: bool
      ExpectedTextContains: string option
      ExpectedData: JsonNode option
      MinimumPassingTests: int option }

type ExperimentTask =
    { Id: string
      Goal: string
      SystemPrompt: string
      InitialContext: string
      Oracle: OracleStep list }

type RunConfig =
    { Task: ExperimentTask
      Mode: RunMode
      Model: string
      ProjectDirectory: string
      RunDirectory: string
      SeedDictionarySource: string option
      MaxTurns: int
      MaxToolCalls: int
      MaxOutputTokens: int
      ContextBudgetBytes: int }

type OracleOutcome =
    { Operation: string
      Passed: bool
      Reason: string
      Response: JsonObject }

type RunReport =
    { RunId: string
      TaskId: string
      Mode: string
      Provider: string
      Model: string
      Success: bool
      Status: string
      FailureCode: string option
      FailureMessage: string option
      StartedAt: string
      FinishedAt: string
      DurationMilliseconds: int64
      Turns: int
      ToolCalls: int
      RequestCount: int
      TotalRequestBytes: int64
      PreparedRequestBytes: int64
      PeakRequestBytes: int64
      ContextBudgetBytes: int
      EstimatedRequestTokens: int64
      EstimateMethod: string
      InputTokens: int option
      OutputTokens: int option
      TotalTokens: int option
      Oracle: OracleOutcome list
      ProjectDirectory: string }

module TaskFile =
    let private requiredString root key =
        Json.tryProperty root key
        |> Option.bind Json.tryString
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.defaultWith (fun () -> invalidOp $"Task file requires non-empty string '{key}'.")

    let private readOracleStep node =
        let operation = requiredString node "operation"
        let args =
            match Json.tryProperty node "args" with
            | None -> JsonObject()
            | Some value -> Json.asObject value |> Option.defaultWith (fun () -> invalidOp "Oracle args must be a JSON object.")
        let expectedOk =
            match Json.tryProperty node "expectOk" with
            | Some value -> try value.GetValue<bool>() with _ -> invalidOp "Oracle expectOk must be a boolean."
            | None -> true
        let expectedText = Json.tryProperty node "textContains" |> Option.bind Json.tryString
        let expectedData = Json.tryProperty node "data" |> Option.map (fun value -> value.DeepClone())
        let minimumPassing = Json.propertyInt node "minimumPassingTests"
        if minimumPassing |> Option.exists (fun value -> value < 0) then invalidOp "minimumPassingTests cannot be negative."
        { Operation = operation
          Arguments = args
          ExpectedOk = expectedOk
          ExpectedTextContains = expectedText
          ExpectedData = expectedData
          MinimumPassingTests = minimumPassing }

    let parse (text: string) =
        let root =
            match JsonNode.Parse(text) |> Json.asObject with
            | Some value -> value
            | None -> invalidOp "Task file must be a JSON object."
        let oracle =
            match Json.tryProperty root "oracle" |> Option.bind Json.asArray with
            | Some items -> items |> Seq.map readOracleStep |> Seq.toList
            | None -> invalidOp "Task file requires an oracle array."
        if List.isEmpty oracle then invalidOp "Task file oracle must contain at least one check."
        let languagePrimer =
            "You work inside a small, strongly typed concatenative language. Inspect words before use. Define with `word name : Input -> Output`, then `effects none` or declared effects, a body, and `end`. Locals use `let name` to bind the top value and `$name` to read it. `if` has `else` and `end`. A test is `test word/case`, a body, `=> expected-literal`, `end`; attach tests in the definition source. The define tool requires lifetime `candidate` or `temporary`; temporary words disappear when the task ends. Test your changes. A project commit requires passing tests; a library commit additionally requires tests to execute every instruction and every control-flow outcome, including both `if` branches. The task tool's `quality` is `project` or `library`. Use only supplied runtime tools and finish with a concise result."
        let taskSpecificPrompt = Json.propertyString root "systemPrompt" ""
        { Id = requiredString root "id"
          Goal = requiredString root "goal"
          SystemPrompt = if String.IsNullOrWhiteSpace taskSpecificPrompt then languagePrimer else $"{languagePrimer}\nTask-specific guidance: {taskSpecificPrompt}"
          InitialContext = Json.propertyString root "initialContext" ""
          Oracle = oracle }

    let load path = File.ReadAllText(path) |> parse

module Runner =
    let private serializer = JsonSerializerOptions(WriteIndented = true)

    let private nodeOption (value: JsonNode option) =
        match value with
        | Some node -> node.DeepClone()
        | None -> null

    let private responseOk (response: JsonObject) =
        Json.tryProperty response "ok"
        |> Option.bind (fun value -> try value.GetValue<bool>() |> Some with _ -> None)
        |> Option.defaultValue false

    let private deepSubset (expected: JsonNode) (actual: JsonNode) =
        let rec matches (left: JsonNode) (right: JsonNode) =
            match left, right with
            | null, null -> true
            | null, _ | _, null -> false
            | (:? JsonObject as leftObject), (:? JsonObject as rightObject) ->
                leftObject
                |> Seq.forall (fun pair ->
                    match Json.tryProperty rightObject pair.Key with
                    | Some value -> matches pair.Value value
                    | None -> false)
            | (:? JsonArray as leftArray), (:? JsonArray as rightArray) ->
                leftArray.Count = rightArray.Count
                && Seq.forall2 matches leftArray rightArray
            | _ -> Json.compact left = Json.compact right
        matches expected actual

    let private addKnown (current: int option) (next: int option) =
        match current, next with
        | Some left, Some right -> Some(left + right)
        | _ -> None

    let private optionNumber (value: int option) : JsonNode =
        match value with
        | Some number -> Json.integer number
        | None -> null

    let private setTaskCall (engine: Runtime.Engine) (operation: string) (arguments: JsonObject) =
        let args = JsonObject()
        for KeyValue(key, value) in arguments do args[key] <- if isNull value then null else value.DeepClone()
        engine.Dispatch(operation, args)

    let private stateNode (projectDirectory: string) =
        let state = JsonObject()
        let dictionaryPath = Path.Combine(projectDirectory, "dictionary.agent")
        state["dictionary"] <- if File.Exists dictionaryPath then Json.text (File.ReadAllText dictionaryPath) else null
        let history = JsonArray()
        let historyPath = Path.Combine(projectDirectory, "history")
        if Directory.Exists historyPath then
            Directory.GetFiles(historyPath, "task-*.json")
            |> Array.sort
            |> Array.iter (fun path ->
                let item = JsonObject()
                item["path"] <- Json.text ($"history/{Path.GetFileName path}")
                item["content"] <- Json.text (File.ReadAllText path)
                history.Add item)
        state["history"] <- history
        state

    let private restoreState (projectDirectory: string) (state: JsonObject) =
        Directory.CreateDirectory(projectDirectory) |> ignore
        let dictionaryPath = Path.Combine(projectDirectory, "dictionary.agent")
        match Json.tryProperty state "dictionary" |> Option.bind Json.tryString with
        | Some contents -> File.WriteAllText(dictionaryPath, contents, UTF8Encoding(false))
        | None when File.Exists dictionaryPath -> File.Delete dictionaryPath
        | None -> ()

        let historyPath = Path.Combine(projectDirectory, "history")
        if Directory.Exists historyPath then
            Directory.GetFiles(historyPath, "task-*.json") |> Array.iter File.Delete
        match Json.tryProperty state "history" |> Option.bind Json.asArray with
        | Some entries ->
            Directory.CreateDirectory(historyPath) |> ignore
            for entry in entries do
                let relative = Json.propertyString entry "path" ""
                let fileName = Path.GetFileName(relative)
                if fileName.StartsWith("task-", StringComparison.Ordinal) && fileName.EndsWith(".json", StringComparison.Ordinal) then
                    let content = Json.propertyString entry "content" ""
                    File.WriteAllText(Path.Combine(historyPath, fileName), content, UTF8Encoding(false))
        | None -> ()

    let private writeJson (path: string) (node: JsonNode) =
        File.WriteAllText(path, node.ToJsonString(serializer), UTF8Encoding(false))

    let private errorResponse code message =
        let detail = JsonObject()
        detail["code"] <- Json.text code
        detail["message"] <- Json.text message
        let result = JsonObject()
        result["ok"] <- Json.bool false
        result["kind"] <- Json.text "error"
        result["text"] <- Json.text message
        result["error"] <- detail
        result

    let private commandError code message = Json.compact (errorResponse code message)

    let private toolCallId (item: JsonObject) = Json.propertyString item "call_id" ""

    let private toolCalls (output: JsonArray) =
        output
        |> Seq.choose (fun item ->
            match Json.asObject item with
            | Some value when Json.propertyString value "type" "" = "function_call" -> Some value
            | _ -> None)
        |> Seq.toList

    let private toolOutput callId output =
        let item = JsonObject()
        item["type"] <- Json.text "function_call_output"
        item["call_id"] <- Json.text callId
        item["output"] <- Json.text output
        item :> JsonNode

    let private inputItems initialContext goal =
        let user = JsonObject()
        user["role"] <- Json.text "user"
        let content =
            if String.IsNullOrWhiteSpace initialContext then goal
            else $"{initialContext.Trim()}\n\nTask:\n{goal}"
        user["content"] <- Json.text content
        let items = JsonArray()
        items.Add user
        items

    let private oracleOutcome (engine: Runtime.Engine) (step: OracleStep) =
        let response = setTaskCall engine step.Operation step.Arguments
        let mutable passed = responseOk response = step.ExpectedOk
        let reasons = ResizeArray<string>()
        if not passed then reasons.Add $"expected ok={step.ExpectedOk}"
        let text = Json.propertyString response "text" ""
        match step.ExpectedTextContains with
        | Some fragment when not (text.Contains(fragment, StringComparison.Ordinal)) ->
            passed <- false
            reasons.Add $"response text does not contain {fragment}"
        | _ -> ()
        match step.ExpectedData with
        | Some expected ->
            let actual = Json.tryProperty response "data" |> Option.defaultValue null
            if not (deepSubset expected actual) then
                passed <- false
                reasons.Add "response data does not contain the expected structure"
        | None -> ()

        let isTest = step.Operation = "test" || step.Operation = "test-all" || step.Operation = "failed-tests"
        let resultNodes =
            Json.tryProperty response "data"
            |> Option.bind (fun node -> Json.tryProperty node "results")
            |> Option.bind Json.asArray
            |> Option.defaultValue (JsonArray())
        let passingTests =
            resultNodes
            |> Seq.filter (fun result -> Json.tryProperty result "passed" |> Option.bind (fun value -> try value.GetValue<bool>() |> Some with _ -> None) = Some true)
            |> Seq.length
        if step.MinimumPassingTests |> Option.exists (fun minimum -> passingTests < minimum) then
            passed <- false
            reasons.Add $"expected at least {step.MinimumPassingTests.Value} passing tests, got {passingTests}"
        if isTest && step.Operation = "failed-tests" && resultNodes.Count <> 0 then
            passed <- false
            reasons.Add $"{resultNodes.Count} failing tests remain"
        elif isTest && step.Operation <> "failed-tests" then
            if resultNodes.Count = 0 then
                passed <- false
                reasons.Add "test oracle ran no tests"
            elif passingTests <> resultNodes.Count then
                passed <- false
                reasons.Add $"{resultNodes.Count - passingTests} test(s) failed"
        let reason = if reasons.Count = 0 then "oracle passed" else String.concat "; " reasons
        { Operation = step.Operation; Passed = passed; Reason = reason; Response = response }

    let private oracleJson (outcome: OracleOutcome) =
        let value = JsonObject()
        value["operation"] <- Json.text outcome.Operation
        value["passed"] <- Json.bool outcome.Passed
        value["reason"] <- Json.text outcome.Reason
        value["response"] <- outcome.Response.DeepClone()
        value

    let private makeReportNode report =
        let node = JsonObject()
        node["runId"] <- Json.text report.RunId
        node["taskId"] <- Json.text report.TaskId
        node["mode"] <- Json.text report.Mode
        node["provider"] <- Json.text report.Provider
        node["model"] <- Json.text report.Model
        node["success"] <- Json.bool report.Success
        node["status"] <- Json.text report.Status
        node["failureCode"] <- report.FailureCode |> Option.map Json.text |> nodeOption
        node["failureMessage"] <- report.FailureMessage |> Option.map Json.text |> nodeOption
        node["startedAt"] <- Json.text report.StartedAt
        node["finishedAt"] <- Json.text report.FinishedAt
        node["durationMilliseconds"] <- JsonValue.Create(report.DurationMilliseconds)
        node["turns"] <- Json.integer report.Turns
        node["toolCalls"] <- Json.integer report.ToolCalls
        node["requestCount"] <- Json.integer report.RequestCount
        let context = JsonObject()
        context["totalRequestBytes"] <- JsonValue.Create(report.TotalRequestBytes)
        context["preparedRequestBytes"] <- JsonValue.Create(report.PreparedRequestBytes)
        context["peakRequestBytes"] <- JsonValue.Create(report.PeakRequestBytes)
        context["budgetBytesPerRequest"] <- Json.integer report.ContextBudgetBytes
        context["estimatedRequestTokens"] <- JsonValue.Create(report.EstimatedRequestTokens)
        context["estimateMethod"] <- Json.text report.EstimateMethod
        node["context"] <- context
        let tokens = JsonObject()
        tokens["inputTokens"] <- optionNumber report.InputTokens
        tokens["outputTokens"] <- optionNumber report.OutputTokens
        tokens["totalTokens"] <- optionNumber report.TotalTokens
        node["tokenUsage"] <- tokens
        let oracle = JsonArray()
        report.Oracle |> List.iter (oracleJson >> oracle.Add)
        node["oracle"] <- oracle
        node["projectDirectory"] <- Json.text report.ProjectDirectory
        node

    let private stateDigest (state: JsonObject) =
        let bytes = Encoding.UTF8.GetBytes(Json.compact state)
        SHA256.HashData(bytes) |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

    let private stateManifest (state: JsonObject) =
        let node = JsonObject()
        node["sha256"] <- Json.text (stateDigest state)
        node["state"] <- state.DeepClone()
        node

    let private logFile (writer: StreamWriter) (eventName: string) (fields: (string * JsonNode) list) =
        let item = JsonObject()
        item["at"] <- Json.text (DateTimeOffset.UtcNow.ToString("O"))
        item["event"] <- Json.text eventName
        for key, value in fields do item[key] <- if isNull value then null else value.DeepClone()
        writer.WriteLine(item.ToJsonString(JsonSerializerOptions(WriteIndented = false)))
        writer.Flush()

    let run (config: RunConfig) (provider: IAgentProvider) =
        task {
            if String.IsNullOrWhiteSpace config.Model then invalidArg "config.Model" "A model name is required, including for scripted runs."
            if config.MaxTurns <= 0 then invalidArg "config.MaxTurns" "maxTurns must be positive."
            if config.MaxToolCalls <= 0 then invalidArg "config.MaxToolCalls" "maxToolCalls must be positive."
            if config.MaxOutputTokens <= 0 then invalidArg "config.MaxOutputTokens" "maxOutputTokens must be positive."
            if config.ContextBudgetBytes <= 0 then invalidArg "config.ContextBudgetBytes" "contextBudgetBytes must be positive."
            if Directory.Exists config.RunDirectory then invalidOp $"Run directory already exists: {config.RunDirectory}"
            if config.Mode = Flat then
                let expectedProject = Path.GetFullPath(Path.Combine(config.RunDirectory, "project"))
                if not (String.Equals(Path.GetFullPath(config.ProjectDirectory), expectedProject, StringComparison.OrdinalIgnoreCase)) then
                    invalidArg "config.ProjectDirectory" "Flat mode requires a new project directory at <run-directory>/project."
            Directory.CreateDirectory(config.RunDirectory) |> ignore
            Directory.CreateDirectory(config.ProjectDirectory) |> ignore

            let started = DateTimeOffset.UtcNow
            let timer = Diagnostics.Stopwatch.StartNew()
            let runId = $"{config.Task.Id}-{started:yyyyMMddTHHmmssfffZ}"
            let tracePath = Path.Combine(config.RunDirectory, "trace.jsonl")
            let runtimeLogPath = Path.Combine(config.RunDirectory, "runtime.log")
            let reportPath = Path.Combine(config.RunDirectory, "report.json")
            let initialStatePath = Path.Combine(config.RunDirectory, "initial-state.json")
            let finalStatePath = Path.Combine(config.RunDirectory, "final-state.json")
            let promptPath = Path.Combine(config.RunDirectory, "prompt.txt")
            let fullPrompt =
                if String.IsNullOrWhiteSpace config.Task.InitialContext then config.Task.Goal
                else $"{config.Task.InitialContext.Trim()}\n\nTask:\n{config.Task.Goal}"
            File.WriteAllText(promptPath, $"SYSTEM\n{config.Task.SystemPrompt}\n\nUSER\n{fullPrompt}\n", UTF8Encoding(false))

            use traceWriter = new StreamWriter(tracePath, false, UTF8Encoding(false))
            let log event fields = logFile traceWriter event fields
            let dictionaryPath = Path.Combine(config.ProjectDirectory, "dictionary.agent")
            let mutable engineInstance: Runtime.Engine option = None
            let mutable failure: (string * string) option = None
            let mutable taskBegan = false
            let mutable initialStateCaptured = false
            let mutable initialStateSnapshot: JsonObject option = None
            let mutable turns = 0
            let mutable calls = 0
            let mutable requestsSent = 0
            let mutable totalRequestBytes = 0L
            let mutable preparedRequestBytes = 0L
            let mutable peakRequestBytes = 0L
            let mutable inputTokens: int option = Some 0
            let mutable outputTokens: int option = Some 0
            let mutable totalTokens: int option = Some 0
            let mutable oracleOutcomes: OracleOutcome list = []
            let mutable runtimeLog: JsonNode = null

            try
                let preSeedState = stateNode config.ProjectDirectory
                initialStateSnapshot <- Some preSeedState
                writeJson initialStatePath (stateManifest preSeedState)
                initialStateCaptured <- true
                let engine = Runtime.Engine(config.ProjectDirectory, Set.empty, "2000-01-01T00:00:00Z")
                engineInstance <- Some engine
                if not (File.Exists dictionaryPath) then
                    match config.SeedDictionarySource with
                    | Some sourcePath when File.Exists sourcePath ->
                        let defineArgs = JsonObject()
                        defineArgs["source"] <- Json.text (File.ReadAllText sourcePath)
                        let defined = engine.Dispatch("define", defineArgs)
                        log "seed-define" [ "response", defined ]
                        let defineError = Json.propertyString defined "text" "unknown error"
                        if not (responseOk defined) then failwith $"Could not load seed dictionary: {defineError}"
                        let committed = engine.Dispatch("commit", JsonObject())
                        log "seed-commit" [ "response", committed ]
                        let commitError = Json.propertyString committed "text" "unknown error"
                        if not (responseOk committed) then failwith $"Could not commit seed dictionary: {commitError}"
                    | Some path -> failwith $"Seed dictionary source does not exist: {path}"
                    | None -> ()

                let initialState = stateNode config.ProjectDirectory
                initialStateSnapshot <- Some initialState
                writeJson initialStatePath (stateManifest initialState)
                let beginArgs = JsonObject()
                beginArgs["goal"] <- Json.text config.Task.Goal
                let begun = engine.Dispatch("task.begin", beginArgs)
                log "task-begin" [ "response", begun ]
                let beginError = Json.propertyString begun "text" "unknown error"
                if not (responseOk begun) then failwith $"Could not begin task: {beginError}"
                taskBegan <- true

                let tools = AgentTools.definitions ()
                let history = inputItems config.Task.InitialContext config.Task.Goal
                let mutable isFinished = false
                while not isFinished && failure.IsNone do
                    turns <- turns + 1
                    let request =
                        { Model = config.Model
                          Instructions = config.Task.SystemPrompt
                          Input = history
                          Tools = tools
                          MaxOutputTokens = config.MaxOutputTokens }
                    let wire = RequestWire.serialize request
                    let bytes = int64 (Encoding.UTF8.GetByteCount wire)
                    preparedRequestBytes <- preparedRequestBytes + bytes
                    peakRequestBytes <- max peakRequestBytes bytes
                    log "provider-request-prepared" [ "turn", Json.integer turns; "bytes", JsonValue.Create(bytes); "body", Json.text wire ]
                    if bytes > int64 config.ContextBudgetBytes then
                        failure <- Some("CONTEXT_BUDGET_EXCEEDED", $"Request is {bytes} UTF-8 bytes, above the {config.ContextBudgetBytes}-byte per-request budget.")
                    elif turns > config.MaxTurns then
                        failure <- Some("TURN_LIMIT", $"The run exceeded its {config.MaxTurns}-turn model-call limit.")
                    else
                        requestsSent <- requestsSent + 1
                        totalRequestBytes <- totalRequestBytes + bytes
                        log "provider-request" [ "turn", Json.integer turns; "bytes", JsonValue.Create(bytes); "body", Json.text wire ]
                        let! response = provider.Complete request
                        log "provider-response" [ "turn", Json.integer turns; "status", Json.text response.Status; "raw", Json.text response.RawResponse ]
                        inputTokens <- addKnown inputTokens (response.Usage |> Option.bind (fun usage -> usage.InputTokens))
                        outputTokens <- addKnown outputTokens (response.Usage |> Option.bind (fun usage -> usage.OutputTokens))
                        totalTokens <- addKnown totalTokens (response.Usage |> Option.bind (fun usage -> usage.TotalTokens))
                        if response.Status <> "completed" then
                            let message = if response.Status = "incomplete" then "Provider response was incomplete (often caused by an output-token limit)." else $"Provider response status was '{response.Status}'."
                            failure <- Some("PROVIDER_NOT_COMPLETED", message)
                        else
                            let returnedCalls = toolCalls response.OutputItems
                            if returnedCalls.IsEmpty then
                                if String.IsNullOrWhiteSpace response.OutputText then
                                    failure <- Some("AGENT_EMPTY_FINAL", "Provider completed without a final text answer or runtime tool call.")
                                else
                                    log "agent-final" [ "text", Json.text response.OutputText ]
                                    isFinished <- true
                            elif turns >= config.MaxTurns then
                                failure <- Some("TURN_LIMIT", $"The agent requested tools on its final allowed turn ({config.MaxTurns}); no calls were executed without room for a follow-up response.")
                            elif calls + returnedCalls.Length > config.MaxToolCalls then
                                failure <- Some("TOOL_LIMIT", $"The agent requested more than the {config.MaxToolCalls}-call tool limit; none of the calls in this response were executed.")
                            else
                                for outputItem in response.OutputItems do history.Add(outputItem.DeepClone())
                                calls <- calls + returnedCalls.Length
                                for call in returnedCalls do
                                    let callId = toolCallId call
                                    if String.IsNullOrWhiteSpace callId then
                                        failure <- Some("MALFORMED_TOOL_CALL", "Function call item is missing call_id; the run cannot be continued safely.")
                                    else
                                        let resultText =
                                            match AgentTools.decode call with
                                            | Error message -> commandError "AGENT_TOOL_ARGUMENTS" message
                                            | Ok command ->
                                                try engine |> fun value -> AgentTools.dispatch value command |> Json.compact
                                                with ex -> commandError "AGENT_TOOL_FAILURE" ex.Message
                                        let resultNode =
                                            try JsonNode.Parse(resultText) with _ -> errorResponse "AGENT_TOOL_FAILURE" resultText
                                        log "runtime-tool" [ "callId", Json.text callId; "call", call; "result", resultNode ]
                                        history.Add(toolOutput callId resultText)

                if failure.IsNone && isFinished then
                    for step in config.Task.Oracle do
                        if failure.IsNone then
                            let outcome = oracleOutcome engine step
                            oracleOutcomes <- oracleOutcomes @ [ outcome ]
                            log "oracle" [ "operation", Json.text outcome.Operation; "passed", Json.bool outcome.Passed; "reason", Json.text outcome.Reason; "response", outcome.Response ]
                            if not outcome.Passed then
                                failure <- Some("ORACLE_FAILED", outcome.Reason)

                    if failure.IsNone then
                        let status = engine.Dispatch("task.status", JsonObject())
                        let taskActive =
                            Json.tryProperty status "data"
                            |> Option.bind (fun node -> Json.tryProperty node "active")
                            |> Option.bind (fun value -> try value.GetValue<bool>() |> Some with _ -> None)
                            |> Option.defaultValue false
                        if taskActive then
                            let committed = engine.Dispatch("task.commit", JsonObject())
                            log "task-commit" [ "response", committed ]
                            if not (responseOk committed) then failure <- Some("TASK_COMMIT_FAILED", Json.propertyString committed "text" "Task commit failed.")
                if failure.IsNone then
                    taskBegan <- false
                let finalStatus = engine.Dispatch("task.log", JsonObject())
                runtimeLog <- finalStatus.DeepClone()
                log "runtime-log" [ "response", finalStatus ]
            with
            | :? ProviderFailure as ex ->
                failure <- Some(ex.Code, ex.Message)
                inputTokens <- None
                outputTokens <- None
                totalTokens <- None
            | ex ->
                if failure.IsNone then failure <- Some("HARNESS_FAILURE", ex.Message)
                if requestsSent > 0 then
                    inputTokens <- None
                    outputTokens <- None
                    totalTokens <- None

            if failure.IsSome then
                if taskBegan then
                    try
                        let aborted = engineInstance.Value.Dispatch("task.abort", JsonObject())
                        log "task-abort" [ "response", aborted ]
                        if not (responseOk aborted) then
                            let message = Json.propertyString aborted "text" "Task abort was rejected."
                            failure <- Some("ROLLBACK_FAILED", $"Task abort was rejected: {message}")
                        runtimeLog <- engineInstance.Value.Dispatch("task.log", JsonObject())
                    with ex ->
                        log "task-abort-failure" [ "message", Json.text ex.Message ]
                        failure <- Some("ROLLBACK_FAILED", $"Task abort failed: {ex.Message}")
                if initialStateCaptured then
                    try
                        match initialStateSnapshot with
                        | Some initial ->
                            restoreState config.ProjectDirectory initial
                            let restored = stateNode config.ProjectDirectory
                            if Json.compact restored <> Json.compact initial then
                                failure <- Some("ROLLBACK_FAILED", "Project state did not match its pre-task snapshot after rollback.")
                        | None -> ()
                    with ex -> failure <- Some("ROLLBACK_FAILED", $"Could not restore project state: {ex.Message}")

            if requestsSent = 0 then
                inputTokens <- None
                outputTokens <- None
                totalTokens <- None

            let finalState = stateNode config.ProjectDirectory
            writeJson finalStatePath (stateManifest finalState)
            let savedRuntimeLog: JsonNode = if isNull runtimeLog then JsonObject() :> JsonNode else runtimeLog
            writeJson runtimeLogPath savedRuntimeLog
            timer.Stop()
            let finished = DateTimeOffset.UtcNow
            let success = failure.IsNone && (oracleOutcomes |> List.forall (fun outcome -> outcome.Passed))
            let report =
                { RunId = runId
                  TaskId = config.Task.Id
                  Mode = if config.Mode = Flat then "flat" else "growing"
                  Provider = provider.Name
                  Model = config.Model
                  Success = success
                  Status = if success then "completed" else "failed"
                  FailureCode = failure |> Option.map fst
                  FailureMessage = failure |> Option.map snd
                  StartedAt = started.ToString("O")
                  FinishedAt = finished.ToString("O")
                  DurationMilliseconds = timer.ElapsedMilliseconds
                  Turns = turns
                  ToolCalls = calls
                  RequestCount = requestsSent
                  TotalRequestBytes = totalRequestBytes
                  PreparedRequestBytes = preparedRequestBytes
                  PeakRequestBytes = peakRequestBytes
                  ContextBudgetBytes = config.ContextBudgetBytes
                  EstimatedRequestTokens = (totalRequestBytes + 3L) / 4L
                  EstimateMethod = "Approximate ceil(UTF-8 request-body bytes / 4); not a tokenizer or exact token usage."
                  InputTokens = inputTokens
                  OutputTokens = outputTokens
                  TotalTokens = totalTokens
                  Oracle = oracleOutcomes
                  ProjectDirectory = config.ProjectDirectory }
            writeJson reportPath (makeReportNode report)
            return report
        }
