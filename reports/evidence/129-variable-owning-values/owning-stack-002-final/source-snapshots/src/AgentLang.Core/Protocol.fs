namespace AgentLang

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes

module Protocol =
    let private compactJson = JsonSerializerOptions(WriteIndented = false)

    let private stringValue (value: string) = JsonValue.Create(value) :> JsonNode
    let private boolValue (value: bool) = JsonValue.Create(value) :> JsonNode

    let private errorResponse code message =
        let problem = JsonObject()
        problem["code"] <- stringValue code
        problem["message"] <- stringValue message

        let response = JsonObject()
        response["ok"] <- boolValue false
        response["kind"] <- stringValue "error"
        response["text"] <- stringValue message
        response["error"] <- problem
        response

    let private copyProperties (target: JsonObject) (source: JsonObject) =
        for KeyValue(key, value) in source do
            target[key] <- if isNull value then null else value.DeepClone()

    /// Parse the protocol's operation and arguments from one JSON request object.
    /// Arguments may be top-level fields or grouped under an optional `args` object.
    let parseRequest (line: string) =
        try
            match JsonNode.Parse(line) with
            | :? JsonObject as request ->
                let operation =
                    try
                        if isNull request["op"] then None
                        else Some(request["op"].GetValue<string>())
                    with _ -> None

                match operation with
                | None -> Error("PROTOCOL_INVALID_REQUEST", "Request must contain a string 'op' field.")
                | Some operation when String.IsNullOrWhiteSpace operation ->
                    Error("PROTOCOL_INVALID_REQUEST", "Request operation cannot be empty.")
                | Some operation ->
                    let args = JsonObject()
                    match request["args"] with
                    | :? JsonObject as nested -> copyProperties args nested
                    | _ -> ()

                    for KeyValue(key, value) in request do
                        if key <> "op" && key <> "args" then
                            args[key] <- if isNull value then null else value.DeepClone()

                    Ok(operation, args)
            | _ -> Error("PROTOCOL_INVALID_REQUEST", "Each JSON-lines request must be a JSON object.")
        with
        | :? JsonException as ex -> Error("PROTOCOL_INVALID_JSON", ex.Message)
        | ex -> Error("PROTOCOL_INVALID_REQUEST", ex.Message)

    /// Dispatch one JSON request line and return exactly one compact JSON response line.
    let dispatchLine (engine: Runtime.Engine) (line: string) =
        match parseRequest line with
        | Ok(operation, args) -> engine.Dispatch(operation, args)
        | Error(code, message) -> errorResponse code message

    let serializeResponse (response: JsonObject) = response.ToJsonString(compactJson)

    /// Serve JSON-lines requests without writing prompts or diagnostics to standard output.
    let serveJsonLines (engine: Runtime.Engine) (input: TextReader) (output: TextWriter) =
        let mutable line = input.ReadLine()
        while not (isNull line) do
            output.WriteLine(dispatchLine engine line |> serializeResponse)
            line <- input.ReadLine()
