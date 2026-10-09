namespace AgentLang.Conventional.Cli

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open AgentLang.Conventional

module Program =
    let private maximumRequestCharacters = 1_048_576
    let private maximumResponseCharacters = 8 * 1_048_576
    let private defaultMaximumRequests = 100
    let private maximumRequestsLimit = 100
    let private compactJson = JsonSerializerOptions(WriteIndented = false)

    let private text (value: string) = JsonValue.Create(value) :> JsonNode
    let private bool (value: bool) = JsonValue.Create(value) :> JsonNode

    let private errorResponse code message =
        let detail = JsonObject()
        detail["code"] <- text code
        detail["message"] <- text message
        let response = JsonObject()
        response["ok"] <- bool false
        response["kind"] <- text "error"
        response["text"] <- text message
        response["error"] <- detail
        response

    let private readBoundedLine (reader: TextReader) =
        let buffer = StringBuilder()
        let mutable consumed = false
        let mutable oversized = false
        let mutable next = reader.Read()

        while next >= 0 && next <> int '\n' do
            consumed <- true
            if buffer.Length < maximumRequestCharacters then
                buffer.Append(char next) |> ignore
            else
                oversized <- true
            next <- reader.Read()

        if not consumed && next < 0 then None
        elif oversized then Some(Error("PROTOCOL_REQUEST_TOO_LARGE", $"A JSON-lines request cannot exceed {maximumRequestCharacters} characters."))
        else
            let line = buffer.ToString().TrimEnd('\r')
            Some(Ok line)

    let private copyProperties (target: JsonObject) (source: JsonObject) =
        for KeyValue(key, value) in source do
            target[key] <- if isNull value then null else value.DeepClone()

    let private parseRequest (line: string) =
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
                | Some name when String.IsNullOrWhiteSpace name ->
                    Error("PROTOCOL_INVALID_REQUEST", "Request operation cannot be empty.")
                | Some name ->
                    let arguments = JsonObject()
                    match request["args"] with
                    | :? JsonObject as nested -> copyProperties arguments nested
                    | _ -> ()

                    for KeyValue(key, value) in request do
                        if key <> "op" && key <> "args" then
                            arguments[key] <- if isNull value then null else value.DeepClone()
                    Ok(name, arguments)
            | _ -> Error("PROTOCOL_INVALID_REQUEST", "Each JSON-lines request must be a JSON object.")
        with
        | :? JsonException -> Error("PROTOCOL_INVALID_JSON", "The request is not valid JSON.")
        | _ -> Error("PROTOCOL_INVALID_REQUEST", "The request could not be read.")

    let private dispatchLine (dispatcher: ConventionalDispatcher) (line: string) =
        match parseRequest line with
        | Ok(operation, arguments) -> dispatcher.Dispatch(operation, arguments)
        | Error(code, message) -> errorResponse code message

    let private serializeResponse (response: JsonObject) =
        let serialized = response.ToJsonString(compactJson)
        if serialized.Length > maximumResponseCharacters then
            errorResponse "PROTOCOL_RESPONSE_TOO_LARGE" $"The response exceeded the {maximumResponseCharacters}-character limit."
            |> fun fallback -> fallback.ToJsonString(compactJson)
        else
            serialized

    let private serveJsonLines (dispatcher: ConventionalDispatcher) maximumRequests =
        let mutable keepReading = true
        let mutable requestCount = 0
        let mutable limitExceeded = false
        while keepReading do
            match readBoundedLine Console.In with
            | None -> keepReading <- false
            | Some _ when requestCount >= maximumRequests ->
                Console.Out.WriteLine(
                    serializeResponse
                        (errorResponse "PROTOCOL_REQUEST_LIMIT" $"This session accepts at most {maximumRequests} request(s)."))
                limitExceeded <- true
                keepReading <- false
            | Some(Error(code, message)) ->
                requestCount <- requestCount + 1
                Console.Out.WriteLine(serializeResponse (errorResponse code message))
            | Some(Ok line) ->
                requestCount <- requestCount + 1
                Console.Out.WriteLine(dispatchLine dispatcher line |> serializeResponse)
        limitExceeded

    let private usage =
        String.concat Environment.NewLine
            [ "Usage: agentlang-conventional --project DIR --jsonl --validation-project RELATIVE.fsproj [--max-requests 1..100]"
              "--max-requests counts input lines per process, including malformed requests; it defaults to 100 and cannot exceed 100."
              "After the limit is reached, the next line receives PROTOCOL_REQUEST_LIMIT and the process exits with code 2. EOF at the limit exits normally."
              "Host validation skips NuGet's remote vulnerability feed with -p:NuGetAudit=false; normal package restore still runs, so validation is not fully offline." ]

    let private parseArguments (arguments: string array) =
        let mutable index = 0
        let mutable project: string option = None
        let mutable validationProject: string option = None
        let mutable maximumRequests: int option = None
        let mutable jsonLines = false
        let mutable help = false

        let setOnce (name: string) (current: string option) (value: string) : string option =
            if current.IsSome then invalidArg name $"Option {name} may be supplied only once."
            Some value

        let requiredOptionValue (option: string) =
            if index + 1 >= arguments.Length || arguments[index + 1].StartsWith("--", StringComparison.Ordinal) then
                invalidArg option $"{option} requires a value."
            let value = arguments[index + 1]
            if String.IsNullOrWhiteSpace value then invalidArg option $"{option} requires a non-empty value."
            value

        while index < arguments.Length do
            match arguments[index] with
            | "--help" | "-h" ->
                if help then invalidArg "--help" "Help may be requested only once."
                help <- true
                index <- index + 1
            | "--jsonl" ->
                if jsonLines then invalidArg "--jsonl" "Option --jsonl may be supplied only once."
                jsonLines <- true
                index <- index + 1
            | option when option = "--project" || option = "--validation-project" ->
                let value = requiredOptionValue option
                if option = "--project" then project <- setOnce option project value
                else validationProject <- setOnce option validationProject value
                index <- index + 2
            | "--max-requests" ->
                if maximumRequests.IsSome then invalidArg "--max-requests" "Option --max-requests may be supplied only once."
                let value = requiredOptionValue "--max-requests"
                let mutable parsed = 0
                if not (Int32.TryParse(value, Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture, &parsed)) then
                    invalidArg "--max-requests" "Option --max-requests must be an integer from 1 through 100."
                if parsed < 1 || parsed > maximumRequestsLimit then
                    invalidArg "--max-requests" $"Option --max-requests must be between 1 and {maximumRequestsLimit}."
                maximumRequests <- Some parsed
                index <- index + 2
            | option -> invalidArg "args" $"Unknown option '{option}'."

        if help then None
        else
            if not jsonLines then invalidArg "--jsonl" "This host requires --jsonl."
            let projectRoot = project |> Option.defaultWith (fun () -> invalidArg "--project" "Option --project is required.")
            let validationPath = validationProject |> Option.defaultWith (fun () -> invalidArg "--validation-project" "Option --validation-project is required.")
            Some(Path.GetFullPath(projectRoot), validationPath, maximumRequests |> Option.defaultValue defaultMaximumRequests)

    let run arguments =
        try
            match parseArguments arguments with
            | None -> Console.WriteLine(usage); 0
            | Some(projectRoot, validationProject, requestLimit) ->
                // Validation is a host-selected `dotnet run`; requests can only invoke
                // the Dispatcher's fixed `validate` operation, never choose a command.
                let validation =
                    { Action = ValidationAction.Run
                      ProjectFile = validationProject
                      TimeoutMilliseconds = 120_000
                      MaximumOutputCharactersPerStream = 200_000 }
                let dispatcher = ConventionalDispatcher(projectRoot, validation)
                if serveJsonLines dispatcher requestLimit then 2 else 0
        with ex ->
            Console.Error.WriteLine($"Host configuration error: {ex.Message}")
            Console.Error.WriteLine(usage)
            64

    [<EntryPoint>]
    let main arguments =
        Console.InputEncoding <- UTF8Encoding(false)
        Console.OutputEncoding <- UTF8Encoding(false)
        run arguments
