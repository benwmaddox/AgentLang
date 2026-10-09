namespace AgentLang.SubagentTrials.IoMaintenance156.Fixture

open System
open System.Text.Json
open System.Text.Json.Nodes

module Program =
    let private requiredString (node: JsonNode) (name: string) =
        if isNull node then
            invalidArg name (sprintf "Missing required string '%s'." name)

        node.GetValue<string>()

    let private optionalString (values: JsonObject) (name: string) (defaultValue: string) =
        if values.ContainsKey(name) then
            requiredString values[name] name
        else
            defaultValue

    let private optionalInt (values: JsonObject) (name: string) (defaultValue: int) =
        if values.ContainsKey(name) then
            values[name].GetValue<int>()
        else
            defaultValue

    let private writeScenarioResult
        (outcomes: ResizeArray<string>)
        (files: VirtualFiles option)
        (error: string option)
        =
        let result = JsonObject()
        let outcomeNodes = JsonArray()

        for outcome in outcomes do
            outcomeNodes.Add(JsonValue.Create(outcome))

        result["outcomes"] <- outcomeNodes

        let fileNodes = JsonObject()

        match files with
        | Some virtualFiles ->
            for KeyValue(path, contents) in virtualFiles.Snapshot() do
                fileNodes[path] <- JsonValue.Create(contents)
        | None -> ()

        result["files"] <- fileNodes
        result["reads"] <- JsonValue.Create(files |> Option.map (fun value -> value.ReadCount) |> Option.defaultValue 0)
        result["writes"] <- JsonValue.Create(files |> Option.map (fun value -> value.WriteCount) |> Option.defaultValue 0)

        match error with
        | Some message -> result["error"] <- JsonValue.Create(message)
        | None -> result["error"] <- null

        Console.WriteLine(result.ToJsonString(JsonSerializerOptions(WriteIndented = false)))

    let private runScenario () =
        let outcomes = ResizeArray<string>()
        let mutable virtualFiles: VirtualFiles option = None
        let mutable error: string option = None

        try
            let input = Console.In.ReadToEnd()
            let rootNode = JsonNode.Parse(input)

            if isNull rootNode then
                invalidArg "stdin" "Expected a JSON object on standard input."

            let root = rootNode.AsObject()
            let source = requiredString root["source"] "source"
            let destination = requiredString root["destination"] "destination"
            let repeat = optionalInt root "repeat" 1
            let operation = optionalString root "operation" "safe"

            let initialFiles =
                root["files"].AsObject()
                |> Seq.map (fun entry -> entry.Key, requiredString entry.Value entry.Key)

            let files = VirtualFiles(initialFiles)
            virtualFiles <- Some files

            for _ in 1 .. repeat do
                match operation with
                | "safe" ->
                    let result = ConfigurationOperations.publishSafely(files, source, destination)
                    outcomes.Add(string result)
                | "publish" ->
                    Configuration.publish(files, source, destination)
                    outcomes.Add("Unit")
                | "refresh" ->
                    Configuration.refresh(files, source, destination)
                    outcomes.Add("Unit")
                | unsupported -> invalidArg "operation" (sprintf "Unsupported operation '%s'." unsupported)
        with ex ->
            error <- Some ex.Message

        writeScenarioResult outcomes virtualFiles error

    [<EntryPoint>]
    let main arguments =
        if arguments |> Array.contains "--scenario" then
            runScenario ()
        else
            SelfTests.run ()
            Console.WriteLine("SelfTests passed.")

        0
