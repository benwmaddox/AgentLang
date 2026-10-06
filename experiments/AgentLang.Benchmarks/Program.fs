namespace AgentLang.Benchmarks

open System
open System.IO
open System.Text.Json.Nodes
open AgentLang
open AgentLang.Benchmarks

module Program =
    let private usage () =
        eprintfn "Usage: AgentLang.Benchmarks run --task task.json --provider scripted|openai [--frontend flow|stack] [--model model] [--mode flat|growing] [--baseline domain-seeded-control|primitive-only] [--project-root path] [--run-dir path] [--seed-source file] [--script responses.json] [--max-turns n] [--max-tool-calls n] [--max-output-tokens n] [--context-budget-bytes n]"

    let private arguments (values: string array) =
        if values.Length % 2 <> 0 then invalidArg "args" "Every option must have one value."
        values
        |> Array.chunkBySize 2
        |> Array.map (fun pair ->
            if not (pair[0].StartsWith("--", StringComparison.Ordinal)) then invalidArg "args" $"Expected an option, got '{pair[0]}'."
            pair[0].Substring(2), pair[1])
        |> Map.ofArray

    let private option values name = Map.tryFind name values

    let private required values name =
        option values name
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.defaultWith (fun () -> invalidArg name $"Required option --{name} is missing.")

    let private number (values: Map<string, string>) (name: string) (fallback: int) =
        match option values name with
        | None -> fallback
        | Some value ->
            match Int32.TryParse value with
            | true, parsed -> parsed
            | _ -> invalidArg name $"Option --{name} must be an integer."

    let private scriptResponses path =
        let root = JsonNode.Parse(File.ReadAllText path)
        match root with
        | :? JsonArray as array -> array
        | :? JsonObject as obj ->
            match obj["responses"] with
            | :? JsonArray as array -> array
            | _ -> invalidArg "script" "Script JSON object must contain a responses array."
        | _ -> invalidArg "script" "Script must be a JSON array or an object with a responses array."

    let private run values =
        let taskPath = Path.GetFullPath(required values "task")
        let frontend =
            match option values "frontend" |> Option.defaultValue "flow" with
            | "flow" -> SourceFrontend.Flow
            | "stack" -> SourceFrontend.Stack
            | _ -> invalidArg "frontend" "Option --frontend must be 'flow' or 'stack'."
        let task = TaskFile.loadWithFrontend frontend taskPath
        let providerName = required values "provider"
        let model =
            match providerName, option values "model" with
            | "openai", Some name when not (String.IsNullOrWhiteSpace name) -> name
            | "openai", _ -> invalidArg "model" "Live OpenAI runs require an explicit --model."
            | "scripted", Some name -> name
            | "scripted", None -> "scripted-fixture-v1"
            | _ -> invalidArg "provider" "--provider must be 'scripted' or 'openai'."

        let mode =
            match option values "mode" |> Option.defaultValue "flat" with
            | "flat" -> Flat
            | "growing" -> Growing
            | _ -> invalidArg "mode" "--mode must be 'flat' or 'growing'."

        let baselineProfile =
            match option values "baseline" |> Option.defaultValue "domain-seeded-control" with
            | "domain-seeded-control" -> BaselineProfile.DomainSeededControl
            | "primitive-only" -> BaselineProfile.PrimitiveOnly
            | _ -> invalidArg "baseline" "--baseline must be 'domain-seeded-control' or 'primitive-only'."

        let defaultRunDirectory =
            let stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ")
            let shortId = Guid.NewGuid().ToString("N").Substring(0, 8)
            Path.Combine(Environment.CurrentDirectory, ".agentlang", "runs", $"{task.Id}-{stamp}-{shortId}")
        let runDirectory = Path.GetFullPath(option values "run-dir" |> Option.defaultValue defaultRunDirectory)
        let projectDirectory =
            match mode with
            | Flat -> Path.Combine(runDirectory, "project")
            | Growing ->
                option values "project-root"
                |> Option.map Path.GetFullPath
                |> Option.defaultValue (Path.Combine(Environment.CurrentDirectory, ".agentlang", "growing-project"))
        let seedSource = option values "seed-source" |> Option.map Path.GetFullPath
        let config =
            { Task = task
              Mode = mode
              Frontend = frontend
              Model = model
              ProjectDirectory = projectDirectory
              RunDirectory = runDirectory
              BaselineProfile = baselineProfile
              SeedDictionarySource = seedSource
              TestFailurePoint = None
              MaxTurns = number values "max-turns" 12
              MaxToolCalls = number values "max-tool-calls" 40
              MaxOutputTokens = number values "max-output-tokens" 4096
              ContextBudgetBytes = number values "context-budget-bytes" 262144 }

        let provider: IAgentProvider * IDisposable option =
            match providerName with
            | "scripted" ->
                let scriptPath = Path.GetFullPath(required values "script")
                (new ScriptedProvider(scriptResponses scriptPath) :> IAgentProvider), None
            | "openai" ->
                let key = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                if String.IsNullOrWhiteSpace key then invalidOp "OPENAI_API_KEY is not set; configure it in the process environment for a live run."
                let client = new OpenAiResponsesProvider(key)
                (client :> IAgentProvider), Some(client :> IDisposable)
            | _ -> invalidArg "provider" "--provider must be 'scripted' or 'openai'."
        let providerValue, owned = provider
        try
            let report = Runner.run config providerValue |> fun pending -> pending.GetAwaiter().GetResult()
            printfn "%s: %s (%d turns, %d runtime calls)" report.TaskId report.Status report.Turns report.ToolCalls
            printfn "Report: %s" (Path.Combine(runDirectory, "report.json"))
            if report.Success then 0 else 2
        finally
            owned |> Option.iter (fun value -> value.Dispose())

    [<EntryPoint>]
    let main args =
        try
            if args.Length >= 1 && args[0] = "run" then run (arguments args[1..])
            else
                usage ()
                64
        with ex ->
            eprintfn "Harness error: %s" ex.Message
            64
