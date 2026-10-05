namespace AgentLang.Benchmarks

open System
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks

type TokenUsage =
    { InputTokens: int option
      OutputTokens: int option
      TotalTokens: int option }

type ProviderRequest =
    { Model: string
      Instructions: string
      Input: JsonArray
      Tools: JsonArray
      MaxOutputTokens: int }

type ProviderResponse =
    { Status: string
      OutputItems: JsonArray
      OutputText: string
      Usage: TokenUsage option
      RawResponse: string }

type IAgentProvider =
    abstract Name: string
    abstract Complete: ProviderRequest -> Task<ProviderResponse>

type ProviderFailure(code: string, message: string) =
    inherit Exception(message)
    member _.Code = code

module Json =
    let text (value: string) : JsonNode = JsonValue.Create(value) :> JsonNode
    let bool (value: bool) : JsonNode = JsonValue.Create(value) :> JsonNode
    let integer (value: int) : JsonNode = JsonValue.Create(value) :> JsonNode

    let tryProperty (node: JsonNode) (name: string) =
        match node with
        | :? JsonObject as value ->
            let mutable child = Unchecked.defaultof<JsonNode>
            if value.TryGetPropertyValue(name, &child) then Some child else None
        | _ -> None

    let tryString (node: JsonNode) =
        try Some(node.GetValue<string>()) with _ -> None

    let tryInt (node: JsonNode) =
        try Some(node.GetValue<int>()) with _ -> None

    let asObject (node: JsonNode) =
        match node with
        | :? JsonObject as value -> Some value
        | _ -> None

    let asArray (node: JsonNode) =
        match node with
        | :? JsonArray as value -> Some value
        | _ -> None

    let cloneArray (source: JsonArray) =
        match source.DeepClone() with
        | :? JsonArray as value -> value
        | _ -> JsonArray()

    let compact (node: JsonNode) = node.ToJsonString(JsonSerializerOptions(WriteIndented = false))

    let propertyString (node: JsonNode) (name: string) (defaultValue: string) =
        tryProperty node name
        |> Option.bind tryString
        |> Option.defaultValue defaultValue

    let propertyInt (node: JsonNode) (name: string) =
        tryProperty node name |> Option.bind tryInt

module RequestWire =
    /// The body used by both providers and byte-budget accounting. It contains no credentials.
    let serialize (request: ProviderRequest) =
        let body = JsonObject()
        body["model"] <- Json.text request.Model
        body["instructions"] <- Json.text request.Instructions
        body["input"] <- Json.cloneArray request.Input
        body["tools"] <- Json.cloneArray request.Tools
        body["max_output_tokens"] <- Json.integer request.MaxOutputTokens
        body["store"] <- Json.bool false
        body["parallel_tool_calls"] <- Json.bool false
        let includeItems = JsonArray()
        includeItems.Add(Json.text "reasoning.encrypted_content")
        body["include"] <- includeItems
        Json.compact body

module AgentTools =
    let private schemaString description =
        let schema = JsonObject()
        schema["type"] <- Json.text "string"
        schema["description"] <- Json.text description
        schema :> JsonNode

    let private schemaEnum description values =
        let schema = JsonObject()
        schema["type"] <- Json.text "string"
        schema["description"] <- Json.text description
        let choices = JsonArray()
        values |> List.iter (Json.text >> choices.Add)
        schema["enum"] <- choices
        schema :> JsonNode

    let private functionTool (name: string) (description: string) (fields: (string * string * string list option) list) =
        let properties = JsonObject()
        let required = JsonArray()
        for fieldName, fieldDescription, choices in fields do
            properties[fieldName] <-
                match choices with
                | Some values -> schemaEnum fieldDescription values
                | None -> schemaString fieldDescription
            required.Add(Json.text fieldName)

        let parameters = JsonObject()
        parameters["type"] <- Json.text "object"
        parameters["properties"] <- properties
        parameters["required"] <- required
        parameters["additionalProperties"] <- Json.bool false

        let tool = JsonObject()
        tool["type"] <- Json.text "function"
        tool["name"] <- Json.text name
        tool["description"] <- Json.text description
        tool["parameters"] <- parameters
        tool["strict"] <- Json.bool true
        tool :> JsonNode

    /// A deliberately small, closed command surface for the agent.
    let definitions () =
        let tools = JsonArray()
        tools.Add(functionTool "agentlang_eval" "Evaluate checked AgentLang code or a short expression." [ "code", "Expression or code to evaluate", None ])
        tools.Add(functionTool "agentlang_define" "Add a typed candidate or task-scoped temporary definition with attached tests." [ "source", "Complete AgentLang definition source", None; "lifetime", "Definition lifetime", Some [ "candidate"; "temporary" ] ])
        tools.Add(functionTool "agentlang_search" "Search word names, documentation, and types." [ "query", "Text or type name", None ])
        tools.Add(functionTool "agentlang_inspect" "Inspect words, signatures, source, dependencies, callers, effects, IR, examples, or tests." [ "operation", "Introspection command", Some [ "words"; "describe"; "source"; "dependencies"; "callers"; "effects"; "ir"; "tests"; "examples"; "task.status"; "task.log" ]; "name", "Word name, or empty for words", None ])
        tools.Add(functionTool "agentlang_test" "Run tests for one word or the current project." [ "operation", "Test command", Some [ "test"; "test-all"; "failed-tests" ]; "word", "Word name, or empty for all tests", None ])
        tools.Add(functionTool "agentlang_task" "Inspect task status, commit a tested word to project or library maturity, promote a temporary word, or discard a staged word. Set quality to project except when committing a library word." [ "action", "Task action", Some [ "status"; "commit-word"; "promote"; "discard" ]; "word", "Word name; empty for status", None; "quality", "Commit maturity; use project for other actions", Some [ "project"; "library" ] ])
        tools

    type Command =
        | Eval of string
        | Define of string * string
        | Search of string
        | Inspect of string * string
        | Test of string * string
        | Task of string * string * string

    let private requiredString (args: JsonObject) (name: string) =
        match Json.tryProperty args name |> Option.bind Json.tryString with
        | Some value -> Ok value
        | None -> Error $"argument '{name}' must be a string"

    let private ensureProperties (args: JsonObject) (required: string list) =
        let actual = args |> Seq.map (fun pair -> pair.Key) |> Set.ofSeq
        let expected = Set.ofList required
        if actual = expected then Ok()
        else
            let expectedText = String.concat ", " required
            let actualText = String.concat ", " (Set.toList actual)
            Error $"expected exactly {expectedText}; received {actualText}"

    let decode (item: JsonObject) =
        let name = Json.propertyString item "name" ""
        let rawArguments = Json.propertyString item "arguments" ""
        let callId = Json.propertyString item "call_id" ""
        if String.IsNullOrWhiteSpace callId then Error "function call is missing call_id"
        else
            try
                match JsonNode.Parse(rawArguments) |> Json.asObject with
                | None -> Error "function arguments must be a JSON object"
                | Some args ->
                    match name with
                    | "agentlang_eval" -> ensureProperties args [ "code" ] |> Result.bind (fun () -> requiredString args "code" |> Result.map Eval)
                    | "agentlang_define" ->
                        ensureProperties args [ "source"; "lifetime" ]
                        |> Result.bind (fun () ->
                            match requiredString args "source", requiredString args "lifetime" with
                            | Ok source, Ok lifetime when lifetime = "candidate" || lifetime = "temporary" -> Ok(Define(source, lifetime))
                            | Ok _, Ok lifetime -> Error $"definition lifetime '{lifetime}' is not available"
                            | Error message, _ | _, Error message -> Error message)
                    | "agentlang_search" -> ensureProperties args [ "query" ] |> Result.bind (fun () -> requiredString args "query" |> Result.map Search)
                    | "agentlang_inspect" ->
                        ensureProperties args [ "operation"; "name" ]
                        |> Result.bind (fun () ->
                            match requiredString args "operation", requiredString args "name" with
                            | Ok operation, Ok target when Set.contains operation (Set.ofList [ "words"; "describe"; "source"; "dependencies"; "callers"; "effects"; "ir"; "tests"; "examples"; "task.status"; "task.log" ]) -> Ok(Inspect(operation, target))
                            | Ok operation, Ok _ -> Error $"introspection operation '{operation}' is not available"
                            | Error message, _ | _, Error message -> Error message)
                    | "agentlang_test" ->
                        ensureProperties args [ "operation"; "word" ]
                        |> Result.bind (fun () ->
                            match requiredString args "operation", requiredString args "word" with
                            | Ok operation, Ok word when Set.contains operation (Set.ofList [ "test"; "test-all"; "failed-tests" ]) -> Ok(Test(operation, word))
                            | Ok operation, Ok _ -> Error $"test operation '{operation}' is not available"
                            | Error message, _ | _, Error message -> Error message)
                    | "agentlang_task" ->
                        ensureProperties args [ "action"; "word"; "quality" ]
                        |> Result.bind (fun () ->
                            match requiredString args "action", requiredString args "word", requiredString args "quality" with
                            | Ok action, Ok word, Ok quality when Set.contains action (Set.ofList [ "status"; "commit-word"; "promote"; "discard" ]) && Set.contains quality (Set.ofList [ "project"; "library" ]) -> Ok(Task(action, word, quality))
                            | Ok action, _, _ when not (Set.contains action (Set.ofList [ "status"; "commit-word"; "promote"; "discard" ])) -> Error $"task action '{action}' is not available"
                            | _, _, Ok quality when not (Set.contains quality (Set.ofList [ "project"; "library" ])) -> Error $"commit quality '{quality}' is not available"
                            | _, Error message, _ | Error message, _, _ | _, _, Error message -> Error message
                            | _ -> Error "invalid task command arguments")
                    | _ -> Error $"tool '{name}' is not available"
            with ex -> Error $"invalid function arguments: {ex.Message}"

    let dispatch (engine: AgentLang.Runtime.Engine) (command: Command) =
        let args = JsonObject()
        let operation =
            match command with
            | Eval code -> args["code"] <- Json.text code; "eval"
            | Define(source, lifetime) ->
                args["source"] <- Json.text source
                args["temporary"] <- Json.bool (lifetime = "temporary")
                "define"
            | Search query -> args["query"] <- Json.text query; "search"
            | Inspect(name, target) ->
                if name <> "words" && name <> "task.status" && name <> "task.log" then args["word"] <- Json.text target
                name
            | Test(name, word) ->
                if name <> "test-all" && name <> "failed-tests" then args["word"] <- Json.text word
                name
            | Task(action, word, quality) ->
                match action with
                | "status" -> "task.status"
                | "commit-word" ->
                    args["word"] <- Json.text word
                    args["library"] <- Json.bool (quality = "library")
                    "commit"
                | "promote" -> args["word"] <- Json.text word; "promote"
                | "discard" -> args["word"] <- Json.text word; "discard"
                | _ -> "harness.invalid-task-action"
        engine.Dispatch(operation, args)

module ResponseParsing =
    let textFromItems (items: JsonArray) =
        items
        |> Seq.choose (fun item ->
            if Json.propertyString item "type" "" <> "message" then None
            else
                match Json.tryProperty item "content" |> Option.bind Json.asArray with
                | None -> None
                | Some content ->
                    content
                    |> Seq.choose (fun part ->
                        if Json.propertyString part "type" "" = "output_text" then Json.tryProperty part "text" |> Option.bind Json.tryString
                        else None)
                    |> String.concat ""
                    |> Some)
        |> String.concat ""

    let usageFromNode (node: JsonNode) =
        Json.tryProperty node "usage"
        |> Option.bind Json.asObject
        |> Option.map (fun usage ->
            { InputTokens = Json.propertyInt usage "input_tokens"
              OutputTokens = Json.propertyInt usage "output_tokens"
              TotalTokens = Json.propertyInt usage "total_tokens" })

    let parse (raw: string) =
        try
            match JsonNode.Parse(raw) |> Json.asObject with
            | None -> Error(ProviderFailure("PROVIDER_INVALID_RESPONSE", "Responses API returned a non-object JSON value."))
            | Some response ->
                let status = Json.propertyString response "status" ""
                if String.IsNullOrWhiteSpace status then Error(ProviderFailure("PROVIDER_INVALID_RESPONSE", "Responses API response is missing status."))
                else
                    match Json.tryProperty response "output" |> Option.bind Json.asArray with
                    | None -> Error(ProviderFailure("PROVIDER_INVALID_RESPONSE", "Responses API response is missing output items."))
                    | Some output ->
                        let text = Json.propertyString response "output_text" ""
                        let outputText = if String.IsNullOrEmpty text then textFromItems output else text
                        Ok
                            { Status = status
                              OutputItems = Json.cloneArray output
                              OutputText = outputText
                              Usage = usageFromNode response
                              RawResponse = raw }
        with ex -> Error(ProviderFailure("PROVIDER_INVALID_RESPONSE", $"Could not parse Responses API result: {ex.Message}"))

type ScriptedProvider(responses: JsonArray) =
    let mutable next = 0
    let seenRequests = ResizeArray<string>()

    member _.RequestBodies = List.ofSeq seenRequests
    member _.ResponsesConsumed = next

    interface IAgentProvider with
        member _.Name = "scripted"
        member _.Complete (request: ProviderRequest) =
            task {
                seenRequests.Add(RequestWire.serialize request)
                if next >= responses.Count then
                    return raise (ProviderFailure("SCRIPT_EXHAUSTED", "Scripted provider ran out of responses."))
                else
                    let item = responses[next]
                    next <- next + 1
                    let raw = Json.compact item
                    let status = Json.propertyString item "status" "completed"
                    let output = Json.tryProperty item "output" |> Option.bind Json.asArray |> Option.map Json.cloneArray |> Option.defaultValue (JsonArray())
                    let explicitText = Json.propertyString item "output_text" ""
                    let outputText = if String.IsNullOrEmpty explicitText then ResponseParsing.textFromItems output else explicitText
                    return
                        { Status = status
                          OutputItems = output
                          OutputText = outputText
                          Usage = ResponseParsing.usageFromNode item
                          RawResponse = raw }
            }

type OpenAiResponsesProvider(apiKey: string, ?handler: HttpMessageHandler, ?endpoint: string) =
    let endpoint = defaultArg endpoint "https://api.openai.com/v1/responses"
    let httpClient =
        match handler with
        | Some value -> new HttpClient(value, false)
        | None -> new HttpClient()

    do
        if String.IsNullOrWhiteSpace apiKey then invalidArg (nameof apiKey) "An API key is required for the OpenAI provider."
        httpClient.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Bearer", apiKey)

    member _.Endpoint = endpoint

    interface IDisposable with
        member _.Dispose() = httpClient.Dispose()

    interface IAgentProvider with
        member _.Name = "openai-responses"
        member _.Complete (request: ProviderRequest) =
            task {
                let body = RequestWire.serialize request
                use content = new StringContent(body, Encoding.UTF8, "application/json")
                let! response = httpClient.PostAsync(endpoint, content)
                use response = response
                let! raw = response.Content.ReadAsStringAsync()
                if not response.IsSuccessStatusCode then
                    return raise (ProviderFailure("PROVIDER_HTTP_ERROR", $"Responses API returned HTTP {int response.StatusCode}."))
                else
                    match ResponseParsing.parse raw with
                    | Ok result -> return result
                    | Error failure -> return raise failure
            }
