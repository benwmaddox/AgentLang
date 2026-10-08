namespace AgentLang.BusinessPolicy

open System
open System.Globalization
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open AgentLang.Business

module Program =
    let private usage = "Usage: BusinessPolicy [--probe S01|S06|S07 cases.json]"

    // These private bindings freeze the receiver and result types at the probe boundary.
    let private s01Target: Customer -> bool = CustomerPolicies.isPremium
    let private s06Target: Customer -> int64 = CustomerPolicies.discountBasisPoints
    let private s07Target: Customer -> Domain.Money = CustomerPolicies.discountedBalance

    let private jsonString (value: string) = JsonValue.Create(value) :> JsonNode

    let private addProbeResult (results: JsonArray) (id: string) (resultType: string) (value: JsonNode) =
        let item = JsonObject()
        item["id"] <- jsonString id
        item["resultType"] <- jsonString resultType
        item["value"] <- value
        results.Add item

    let private parseBalanceMinor (value: string) =
        Int64.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)

    let private runProbe (taskId: string) (casesPath: string) =
        use document = JsonDocument.Parse(File.ReadAllText casesPath)
        if document.RootElement.ValueKind <> JsonValueKind.Array then
            invalidArg "casesPath" "Probe cases must be a JSON array."

        let results = JsonArray()
        for item in document.RootElement.EnumerateArray() do
            let id = item.GetProperty("id").GetString()
            let kind = item.GetProperty("kind").GetString()
            let balanceMinor = item.GetProperty("balanceMinor").GetString() |> parseBalanceMinor
            let customer = Fixtures.customer kind balanceMinor

            match taskId with
            | "S01" ->
                s01Target customer
                |> JsonValue.Create
                |> fun value -> addProbeResult results id "Bool" (value :> JsonNode)
            | "S06" ->
                s06Target customer
                |> string
                |> jsonString
                |> addProbeResult results id "Int"
            | "S07" ->
                s07Target customer
                |> Domain.Money.minorUnits
                |> string
                |> jsonString
                |> addProbeResult results id "Money"
            | _ -> invalidArg "taskId" $"Unknown task '{taskId}'."

        Console.Out.WriteLine(results.ToJsonString())

    [<EntryPoint>]
    let main arguments =
        try
            match arguments with
            | [||] ->
                SelfTests.runSeedTests ()
                SelfTests.runOwnTests ()
                SelfTests.runExamples ()
                0
            | [| "--probe"; taskId; casesPath |] ->
                if taskId <> "S01" && taskId <> "S06" && taskId <> "S07" then
                    invalidArg "taskId" $"Unknown task '{taskId}'."
                runProbe taskId casesPath
                0
            | _ ->
                Console.Error.WriteLine usage
                2
        with error ->
            Console.Error.WriteLine(error.Message)
            1
