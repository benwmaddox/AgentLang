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

    let private usage =
        """AgentLang CLI

Usage:
  agentlang [--project DIR] [--allow effect,...] [--clock ISO]
  agentlang [--project DIR] [--allow effect,...] --jsonl
  agentlang [--project DIR] [--eval CODE]
  agentlang [--project DIR] [--request JSON]

Human REPL commands:
  :define FILE       Stage declarations from a .agent file
  :words             List available words
  :describe WORD     Show a word's metadata
  :test [WORD]       Run attached tests
  :test-all          Run every attached test
  :commit WORD       Commit candidates after their tests pass
  :commit WORD --library  Also require full instruction and branch coverage
  :help              Show this help
  :quit              Exit
"""

    let private jsonString (value: string) = JsonValue.Create(value) :> JsonNode

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

    let private humanRequest (engine: Runtime.Engine) (line: string) =
        let trimmed = line.Trim()
        if not (trimmed.StartsWith(":", StringComparison.Ordinal)) then
            let args = newArgs ()
            args["code"] <- jsonString line
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
                args["source"] <- jsonString (File.ReadAllText(Path.GetFullPath(rest)))
                engine.Dispatch("define", args)
            | "eval" ->
                args["code"] <- jsonString rest
                engine.Dispatch("eval", args)
            | "search" ->
                args["query"] <- jsonString rest
                engine.Dispatch("search", args)
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
            | "describe" | "source" | "dependencies" | "callers" | "effects" | "ir" | "tests" | "examples" | "test" | "history" | "diff" | "promote" | "discard" ->
                if rest <> "" then args["word"] <- jsonString rest
                engine.Dispatch(command, args)
            | "words" | "test-all" | "failed-tests" | "stack" | "task.status" | "task.commit" | "task.abort" | "task.log" ->
                engine.Dispatch(command, args)
            | _ ->
                let message = $"Unknown REPL command ':{command}'. Use :help for available commands."
                let response = JsonObject()
                response["ok"] <- JsonValue.Create(false)
                response["kind"] <- jsonString "error"
                response["text"] <- jsonString message
                response

    let private readSubmission (firstLine: string) =
        let start = firstLine.Trim()
        let declaration =
            [ "record "; "type "; "word "; "temp word "; "test "; "example " ]
            |> List.exists (fun prefix -> start.StartsWith(prefix, StringComparison.Ordinal))
        let expressionBlock = start = "if" || start = "match-option" || start = "match-result"
        if not declaration && not expressionBlock then firstLine, false
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
            source.ToString(), declaration

    let private runRepl (engine: Runtime.Engine) =
        let mutable running = true
        while running do
            Console.Write("agentlang> ")
            let line = Console.ReadLine()
            if isNull line then
                running <- false
            else
                let trimmed = line.Trim()
                match trimmed with
                | ":quit" | ":exit" -> running <- false
                | ":help" -> Console.WriteLine(usage)
                | "" -> ()
                | _ ->
                    try
                        let source, isDefinition = readSubmission line
                        let response =
                            if isDefinition then
                                let args = newArgs ()
                                let temporary = source.TrimStart().StartsWith("temp word ", StringComparison.Ordinal)
                                let definitionSource =
                                    if temporary then
                                        source.Remove(source.IndexOf("temp word ", StringComparison.Ordinal), "temp ".Length)
                                    else source
                                args["source"] <- jsonString definitionSource
                                if temporary then args["temporary"] <- JsonValue.Create(true)
                                engine.Dispatch("define", args)
                            else
                                humanRequest engine source
                        renderHuman response
                    with ex ->
                        Console.WriteLine($"error: {ex.Message}")

    [<EntryPoint>]
    let main argv =
        let mutable projectDirectory = Environment.CurrentDirectory
        let mutable capabilities = Set.empty<string>
        let mutable clockValue: string option = None
        let mutable jsonLines = false
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
                    let response = engine.Dispatch("eval", args)
                    renderHuman response
                    if response["ok"].GetValue<bool>() then 0 else 1
                | Some(Request request) ->
                    let response = Protocol.dispatchLine engine request
                    renderHuman response
                    if response["ok"].GetValue<bool>() then 0 else 1
                | None ->
                    runRepl engine
                    0
