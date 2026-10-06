namespace AgentLang.Cli

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open AgentLang

module Program =
    type private Action =
        | Evaluate of string
        | Request of string

    type private Submission =
        { Source: string
          IsDefinition: bool
          PendingLine: string option }

    let private usage =
        """AgentLang CLI

Usage:
  agentlang [--project DIR] [--allow effect,...] [--clock ISO] [--frontend flow|stack]
  agentlang [--project DIR] [--allow effect,...] --jsonl
  agentlang [--project DIR] [--frontend flow|stack] --eval CODE
  agentlang [--project DIR] [--request JSON]

The human REPL and --eval default to Flow. Select --frontend stack to use the
legacy Stack syntax. --request and --jsonl honor request-level frontend fields
and preserve the runtime default when a request omits one.

Human REPL commands:
  :define FILE [--replace --expected-revision N]
                     Stage declarations from a .agent file (quoted or unquoted paths)
  :words             List available words
  :describe WORD     Show a word's metadata
  :source --type TYPE Show the exact authored type declaration
  :type-of WORD      Show the same type and declaration metadata
  :search-type TYPE  Find words whose input or output contains TYPE
  :search-output TYPE Find words whose output contains TYPE
  :search-effect EFFECT Find words declaring EFFECT
  :search-dependency WORD Find direct callers of WORD
  :transitive-dependencies WORD
                     List the full dependency closure
  :transitive-callers WORD
                     List the full caller closure
  :graph WORD [--max-depth N] [--max-nodes N]
                     Render a bounded dependency graph
  :context WORD [--max-depth N] [--max-words N] [--max-utf8-bytes N]
                     Return a bounded compact JSON context
  :test [WORD]       Run attached tests
  :test-all          Run every attached test
  :commit WORD       Commit candidates after their tests pass
  :commit WORD --library  Also require full instruction and branch coverage
  :replace-word WORD Commit a staged replacement after caller tests pass
  :rename OLD NEW    Rename a word and rewrite its semantic references
  :deprecate WORD    Mark a committed word as deprecated
  :snapshot.save NAME  Save committed project and provider state
  :snapshot.load NAME  Restore a named committed snapshot
  :storage.status    Show durable authority and export status
  :help              Show this help
  :quit              Exit
"""

    let private jsonString (value: string) = JsonValue.Create(value) :> JsonNode

    let private jsonIntegerOrString (value: string) =
        match Int32.TryParse value with
        | true, parsed -> JsonValue.Create(parsed) :> JsonNode
        | _ -> jsonString value

    let private newArgs () = JsonObject()

    let private responseText (response: JsonObject) =
        try response["text"].GetValue<string>()
        with _ -> response.ToJsonString()

    let private renderHuman (response: JsonObject) =
        Console.WriteLine(responseText response)
        if not (response["ok"].GetValue<bool>()) && not (isNull response["error"]) then
            let code =
                try (response["error"]["code"]).GetValue<string>()
                with _ -> "error"
            Console.WriteLine($"  [{code}]")
        if not (isNull response["data"]) then
            let options = JsonSerializerOptions(WriteIndented = true)
            Console.WriteLine(response["data"].ToJsonString(options))

    let private commandError code message =
        let response = JsonObject()
        response["ok"] <- JsonValue.Create(false)
        response["kind"] <- jsonString "error"
        response["text"] <- jsonString message
        let problem = JsonObject()
        problem["code"] <- jsonString code
        problem["message"] <- jsonString message
        response["error"] <- problem
        response

    let private commandWords (input: string) =
        let words = ResizeArray<string>()
        let current = StringBuilder()
        let mutable quote = '\000'
        let mutable index = 0
        let mutable started = false
        let flush () =
            if started then
                words.Add(current.ToString())
                current.Clear() |> ignore
                started <- false
        while index < input.Length do
            let ch = input[index]
            if quote <> '\000' && ch = '\\' && index + 1 < input.Length && input[index + 1] = quote then
                current.Append(quote) |> ignore
                index <- index + 2
                started <- true
            elif quote <> '\000' && ch = quote then
                quote <- '\000'
                started <- true
                index <- index + 1
            elif quote = '\000' && (ch = '\'' || ch = '"') then
                quote <- ch
                started <- true
                index <- index + 1
            elif quote = '\000' && (ch = ' ' || ch = '\t') then
                flush ()
                index <- index + 1
            else
                current.Append(ch) |> ignore
                started <- true
                index <- index + 1
        if quote <> '\000' then Error "A quoted command argument is missing its closing quote."
        else
            flush ()
            Ok(List.ofSeq words)

    let private parseDefineArguments (rest: string) =
        match commandWords rest with
        | Error message -> Error message
        | Ok parts ->
            let optionStart =
                parts
                |> List.tryFindIndex (fun value -> value.StartsWith("--", StringComparison.Ordinal))
                |> Option.defaultValue parts.Length
            let path = parts |> List.take optionStart |> String.concat " "
            if String.IsNullOrWhiteSpace path then Error ":define requires a file path."
            else
                let rec parseOptions remaining replace expectedRevision =
                    match remaining with
                    | [] ->
                        match replace, expectedRevision with
                        | false, None -> Ok(path, false, None)
                        | true, Some revision -> Ok(path, true, Some revision)
                        | true, None -> Error "--replace requires --expected-revision N."
                        | false, Some _ -> Error "--expected-revision requires --replace."
                    | "--replace" :: tail when not replace -> parseOptions tail true expectedRevision
                    | "--replace" :: _ -> Error "--replace may only be specified once."
                    | "--expected-revision" :: value :: tail when expectedRevision.IsNone ->
                        match Int32.TryParse(value, Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture) with
                        | true, revision when revision >= 0 -> parseOptions tail replace (Some revision)
                        | _ -> Error "--expected-revision requires a nonnegative integer."
                    | "--expected-revision" :: [] -> Error "--expected-revision requires a value."
                    | "--expected-revision" :: _ -> Error "--expected-revision may only be specified once."
                    | unknown :: _ -> Error $"Unknown :define option '{unknown}'."
                parseOptions (parts |> List.skip optionStart) false None

    let private humanRequest (engine: Runtime.Engine) (frontend: string) (line: string) =
        let trimmed = line.Trim()
        if not (trimmed.StartsWith(":", StringComparison.Ordinal)) then
            let args = newArgs ()
            args["code"] <- jsonString line
            args["frontend"] <- jsonString frontend
            engine.Dispatch("eval", args)
        else
            let commandLine = trimmed.Substring(1).Trim()
            let splitAt = commandLine.IndexOfAny([| ' '; '\t' |])
            let command, rest =
                if splitAt < 0 then commandLine, ""
                else commandLine.Substring(0, splitAt), commandLine.Substring(splitAt + 1).Trim()
            let args = newArgs ()
            match command with
            | "define" ->
                match parseDefineArguments rest with
                | Error message -> commandError "CLI_INVALID_COMMAND" message
                | Ok(_, true, _) when frontend <> "flow" ->
                    commandError "CLI_INVALID_COMMAND" ":define --replace is supported for Flow sources; Stack replacements use :replace-word."
                | Ok(path, replace, expectedRevision) ->
                    args["source"] <- jsonString (File.ReadAllText(Path.GetFullPath(path)))
                    args["frontend"] <- jsonString frontend
                    if replace then
                        args["replace"] <- JsonValue.Create(true)
                        args["expectedRevision"] <- JsonValue.Create(expectedRevision.Value)
                    engine.Dispatch("define", args)
            | "eval" ->
                args["code"] <- jsonString rest
                args["frontend"] <- jsonString frontend
                engine.Dispatch("eval", args)
            | "search" ->
                args["query"] <- jsonString rest
                engine.Dispatch("search", args)
            | "type-of" | "search-dependency" | "transitive-dependencies" | "transitive-callers" ->
                args["word"] <- jsonString rest
                engine.Dispatch(command, args)
            | "search-type" | "search-output" ->
                args["type"] <- jsonString rest
                engine.Dispatch(command, args)
            | "search-effect" ->
                args["effect"] <- jsonString rest
                engine.Dispatch(command, args)
            | "graph" | "context" ->
                let parts = rest.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
                let mutable position = 0
                let mutable invalidOption: string option = None
                if parts.Length > 0 && not (parts[0].StartsWith("--", StringComparison.Ordinal)) then
                    args["word"] <- jsonString parts[0]
                    position <- 1
                while position < parts.Length && invalidOption.IsNone do
                    let key =
                        match command, parts[position] with
                        | "graph", ("--max-depth" | "--maxDepth") -> Some "maxDepth"
                        | "graph", ("--max-nodes" | "--maxNodes") -> Some "maxNodes"
                        | "context", ("--max-depth" | "--maxDepth") -> Some "maxDepth"
                        | "context", ("--max-words" | "--maxWords") -> Some "maxWords"
                        | "context", ("--max-utf8-bytes" | "--maxUtf8Bytes") -> Some "maxUtf8Bytes"
                        | _ -> None
                    match key with
                    | None -> invalidOption <- Some parts[position]
                    | Some name when position + 1 >= parts.Length ->
                        args[name] <- jsonString ""
                        position <- parts.Length
                    | Some name ->
                        args[name] <- jsonIntegerOrString parts[position + 1]
                        position <- position + 2
                match invalidOption with
                | Some optionName ->
                    let response = JsonObject()
                    response["ok"] <- JsonValue.Create(false)
                    response["kind"] <- jsonString "error"
                    let message = $"Unknown {command} option '{optionName}'."
                    response["text"] <- jsonString message
                    let problem = JsonObject()
                    problem["code"] <- jsonString "DISCOVERY_INVALID_ARGUMENT"
                    problem["message"] <- jsonString message
                    response["error"] <- problem
                    response
                | None -> engine.Dispatch(command, args)
            | "task.begin" ->
                args["goal"] <- jsonString rest
                engine.Dispatch("task.begin", args)
            | "commit" | "commit-word" ->
                let parts = rest.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
                if parts |> Array.contains "--library" then args["library"] <- JsonValue.Create(true)
                match parts |> Array.tryFind (fun value -> value <> "--library") with
                | Some word -> args["word"] <- jsonString word
                | None -> ()
                engine.Dispatch("commit", args)
            | "replace-word" ->
                let parts = rest.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
                match parts |> Array.tryHead with Some word -> args["word"] <- jsonString word | None -> ()
                if parts |> Array.contains "--library" then args["library"] <- JsonValue.Create(true)
                engine.Dispatch("replace-word", args)
            | "rename" ->
                match rest.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries) with
                | [| oldName; newName |] -> args["word"] <- jsonString oldName; args["to"] <- jsonString newName
                | _ -> ()
                engine.Dispatch("rename", args)
            | "snapshot.save" | "snapshot.load" ->
                if rest <> "" then args["name"] <- jsonString rest
                engine.Dispatch(command, args)
            | "describe" | "source" | "dependencies" | "callers" | "effects" | "ir" | "tests" | "examples" | "test" | "history" | "diff" | "promote" | "discard" | "deprecate" ->
                if command = "source" && rest.StartsWith("--type", StringComparison.Ordinal) then
                    match commandWords rest with
                    | Ok [ "--type"; typeName ] ->
                        args["type"] <- jsonString typeName
                        engine.Dispatch(command, args)
                    | Ok _ -> commandError "CLI_INVALID_COMMAND" ":source --type requires exactly one type name."
                    | Error message -> commandError "CLI_INVALID_COMMAND" message
                else
                    if rest <> "" then args["word"] <- jsonString rest
                    engine.Dispatch(command, args)
            | "words" | "test-all" | "failed-tests" | "stack" | "task.status" | "task.commit" | "task.abort" | "task.log" | "storage.status" ->
                engine.Dispatch(command, args)
            | _ ->
                let message = $"Unknown REPL command ':{command}'. Use :help for available commands."
                commandError "CLI_UNKNOWN_COMMAND" message

    let private firstFlowSignificantLine (source: string) =
        let mutable offset = 0
        let mutable significant: (string * int) option = None
        while offset <= source.Length && significant.IsNone do
            let newline = source.IndexOfAny([| '\r'; '\n' |], offset)
            let lineEnd = if newline < 0 then source.Length else newline
            let line = source.Substring(offset, lineEnd - offset)
            let trimmed = line.Trim()
            if trimmed <> "" && not (trimmed.StartsWith("//", StringComparison.Ordinal)) && not (trimmed.StartsWith("#", StringComparison.Ordinal)) then
                significant <- Some(trimmed, offset + line.IndexOf(trimmed, StringComparison.Ordinal))
            if newline < 0 then offset <- source.Length + 1
            elif source[newline] = '\r' && newline + 1 < source.Length && source[newline + 1] = '\n' then offset <- newline + 2
            else offset <- newline + 1
        significant

    let private flowDefinitionSource (source: string) =
        match firstFlowSignificantLine source with
        | Some(line, marker) when line.StartsWith("temp word ", StringComparison.Ordinal) -> source.Remove(marker, "temp ".Length), true
        | _ -> source, false

    let private flowStartsWithDeclaration (source: string) =
        match firstFlowSignificantLine source |> Option.map fst with
        | None -> false
        | Some line ->
            [ "record "; "type "; "word "; "test "; "example "; "temp word " ]
            |> List.exists (fun prefix -> line.StartsWith(prefix, StringComparison.Ordinal))

    let private readFlowSubmission (firstLine: string) =
        // Keep this in sync with FlowParser's hard source-size guard. Truncating
        // to one unit beyond the parser limit makes it return FLOW_SOURCE_LIMIT
        // without letting a continuation grow an unbounded in-memory buffer.
        let maxFlowSourceLength = 1_000_000
        let source = StringBuilder()
        let mutable pendingLine: string option = None
        let mutable reachedEof = false
        let mutable doneReading = false
        let mutable isDefinition = false
        let appendBounded (line: string) =
            if source.Length < maxFlowSourceLength + 1 then
                let capacity = maxFlowSourceLength + 1 - source.Length
                if line.Length >= capacity then
                    source.Append(line, 0, capacity) |> ignore
                    doneReading <- true
                else
                    source.Append(line) |> ignore
                    let separator = Environment.NewLine
                    let separatorLength = min separator.Length (maxFlowSourceLength + 1 - source.Length)
                    if separatorLength > 0 then source.Append(separator, 0, separatorLength) |> ignore
                    if source.Length >= maxFlowSourceLength + 1 then doneReading <- true
        appendBounded firstLine
        while not doneReading && not reachedEof do
            let currentSource = source.ToString()
            let text, _ = flowDefinitionSource currentSource
            isDefinition <- flowStartsWithDeclaration currentSource
            let incomplete =
                if isDefinition then
                    match FlowParser.parseDocument "<repl>" text with
                    | Error diagnostic -> diagnostic.Code = "FLOW_INCOMPLETE_INPUT"
                    | Ok _ -> false
                else
                    match FlowParser.parseExpression "<repl>" text with
                    | Error diagnostic -> diagnostic.Code = "FLOW_INCOMPLETE_INPUT"
                    | Ok _ -> false
            if incomplete then
                Console.Write("....> ")
                let nextLine = Console.ReadLine()
                if isNull nextLine then reachedEof <- true
                elif nextLine.TrimStart().StartsWith(":", StringComparison.Ordinal) then
                    pendingLine <- Some nextLine
                    doneReading <- true
                else appendBounded nextLine
            else doneReading <- true
        let sourceText, _ = flowDefinitionSource (source.ToString())
        { Source = source.ToString()
          IsDefinition = isDefinition || flowStartsWithDeclaration sourceText
          PendingLine = pendingLine }

    let private readStackSubmission (firstLine: string) =
        let start = firstLine.Trim()
        let declaration =
            [ "record "; "type "; "word "; "temp word "; "test "; "example " ]
            |> List.exists (fun prefix -> start.StartsWith(prefix, StringComparison.Ordinal))
        let expressionBlock = start = "if" || start = "match-option" || start = "match-result"
        if not declaration && not expressionBlock then
            { Source = firstLine; IsDefinition = false; PendingLine = None }
        else
            let source = StringBuilder()
            source.AppendLine(firstLine) |> ignore
            let mutable depth = 1
            let mutable reachedEof = false
            while depth > 0 && not reachedEof do
                Console.Write("....> ")
                let line = Console.ReadLine()
                if isNull line then
                    reachedEof <- true
                else
                    source.AppendLine(line) |> ignore
                    match line.Trim() with
                    | "if" | "match-option" | "match-result" -> depth <- depth + 1
                    | "end" -> depth <- depth - 1
                    | _ -> ()
            { Source = source.ToString(); IsDefinition = declaration; PendingLine = None }

    let private readSubmission frontend firstLine =
        if frontend = "flow" then readFlowSubmission firstLine
        else readStackSubmission firstLine

    let private runRepl (engine: Runtime.Engine) (frontend: string) =
        let mutable running = true
        let mutable pendingLine: string option = None
        while running do
            Console.Write("agentlang> ")
            let nextLine =
                match pendingLine with
                | Some line -> pendingLine <- None; line
                | None -> Console.ReadLine()
            if isNull nextLine then
                running <- false
            else
                let trimmed = nextLine.Trim()
                match trimmed with
                | ":quit" | ":exit" -> running <- false
                | ":help" -> Console.WriteLine(usage)
                | "" -> ()
                | _ ->
                    try
                        let submission = readSubmission frontend nextLine
                        pendingLine <- submission.PendingLine
                        let response =
                            if submission.IsDefinition then
                                let args = newArgs ()
                                let definitionSource, temporary = flowDefinitionSource submission.Source
                                args["source"] <- jsonString definitionSource
                                args["frontend"] <- jsonString frontend
                                if temporary then args["temporary"] <- JsonValue.Create(true)
                                engine.Dispatch("define", args)
                            else
                                humanRequest engine frontend submission.Source
                        renderHuman response
                    with ex ->
                        Console.WriteLine($"error: {ex.Message}")

    [<EntryPoint>]
    let main argv =
        let mutable projectDirectory = Environment.CurrentDirectory
        let mutable capabilities = Set.empty<string>
        let mutable clockValue: string option = None
        let mutable jsonLines = false
        let mutable frontend = "flow"
        let mutable frontendSpecified = false
        let mutable action: Action option = None
        let mutable help = false
        let mutable problem: string option = None
        let mutable index = 0

        let requireValue flag =
            if index + 1 >= argv.Length then
                problem <- Some($"{flag} requires a value.")
                None
            else
                index <- index + 1
                Some argv[index]

        while index < argv.Length && problem.IsNone && not help do
            match argv[index] with
            | "--help" | "-h" -> help <- true
            | "--project" ->
                match requireValue "--project" with Some value -> projectDirectory <- value | None -> ()
            | "--allow" ->
                match requireValue "--allow" with
                | Some value ->
                    let granted =
                        value.Split([| ',' |], StringSplitOptions.RemoveEmptyEntries)
                        |> Array.map (fun item -> item.Trim())
                        |> Array.filter (String.IsNullOrWhiteSpace >> not)
                        |> Set.ofArray
                    capabilities <- Set.union capabilities granted
                | None -> ()
            | "--clock" -> clockValue <- requireValue "--clock"
            | "--frontend" ->
                if frontendSpecified then problem <- Some("--frontend may only be specified once.")
                else
                    frontendSpecified <- true
                    match requireValue "--frontend" with
                    | Some value when value = "flow" || value = "stack" -> frontend <- value
                    | Some value -> problem <- Some($"--frontend must be 'flow' or 'stack', not '{value}'.")
                    | None -> ()
            | "--jsonl" -> jsonLines <- true
            | "--eval" ->
                match requireValue "--eval" with
                | Some value when action.IsNone -> action <- Some(Evaluate value)
                | Some _ -> problem <- Some("Only one of --eval and --request can be used.")
                | None -> ()
            | "--request" ->
                match requireValue "--request" with
                | Some value when action.IsNone -> action <- Some(Request value)
                | Some _ -> problem <- Some("Only one of --eval and --request can be used.")
                | None -> ()
            | flag -> problem <- Some($"Unknown option '{flag}'. Use --help for usage.")
            index <- index + 1

        match problem with
        | Some message ->
            Console.Error.WriteLine(message)
            Console.Error.WriteLine(usage)
            2
        | None when help ->
            Console.WriteLine(usage)
            0
        | None when jsonLines && action.IsSome ->
            Console.Error.WriteLine("--jsonl reads requests from standard input; remove --eval or --request.")
            2
        | None when frontendSpecified && jsonLines ->
            Console.Error.WriteLine("--frontend selects human REPL and --eval syntax; JSONL requests must select their own frontend.")
            2
        | None when frontendSpecified && (match action with Some(Request _) -> true | _ -> false) ->
            Console.Error.WriteLine("--frontend cannot be combined with --request; the request must select its own frontend.")
            2
        | None ->
            let engine = Runtime.Engine(projectDirectory, capabilities, ?clockValue = clockValue)
            if jsonLines then
                Protocol.serveJsonLines engine Console.In Console.Out
                0
            else
                match action with
                | Some(Evaluate code) ->
                    let args = newArgs ()
                    args["code"] <- jsonString code
                    args["frontend"] <- jsonString frontend
                    let response = engine.Dispatch("eval", args)
                    renderHuman response
                    if response["ok"].GetValue<bool>() then 0 else 1
                | Some(Request request) ->
                    let response = Protocol.dispatchLine engine request
                    renderHuman response
                    if response["ok"].GetValue<bool>() then 0 else 1
                | None ->
                    runRepl engine frontend
                    0
