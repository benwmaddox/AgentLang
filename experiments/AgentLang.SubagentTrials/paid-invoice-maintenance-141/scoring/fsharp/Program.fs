namespace AgentLang.Maintenance141.FSharpScorer

open System
open System.Globalization
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open AgentLang.Business.Domain

module Program =
    let private fail (message: string) = invalidOp message

    let private stringProperty (element: JsonElement) (name: string) : string =
        let property = element.GetProperty(name)
        if property.ValueKind <> JsonValueKind.String then
            fail $"Oracle property '{name}' must be a string."
        property.GetString()

    let private requiredValue (context: string) (value: Result<'value, DomainError>) : 'value =
        match value with
        | Ok parsed -> parsed
        | Error error -> fail $"{context}: {DomainError.code error} ({DomainError.message error})"

    let private addJson (target: JsonObject) name (value: JsonNode) =
        target.Add(name, value)

    let private jsonString (value: string) = JsonValue.Create(value) :> JsonNode
    let private jsonInt (value: int) = JsonValue.Create(value) :> JsonNode
    let private jsonBool (value: bool) = JsonValue.Create(value) :> JsonNode

    let private okResult (amount: string) =
        let result = JsonObject()
        addJson result "ok" (jsonString amount)
        result :> JsonNode

    let private errorResult (errorCode: string) =
        let result = JsonObject()
        addJson result "error" (jsonString errorCode)
        result :> JsonNode

    let private resultNode (project: 'value -> string) (result: Result<'value, DomainError>) =
        match result with
        | Ok value -> okResult (project value)
        | Error error -> errorResult (DomainError.code error)

    let private moneyText (amount: Money) = amount |> Money.minorUnits |> fun minor -> minor.ToString(CultureInfo.InvariantCulture)

    let private universalTimestamp (value: DateTimeOffset) =
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture)

    let private caseActual (customerIds: Map<string, string>) (invoiceIds: Map<string, string>) (paymentIds: Map<string, string>) (case: JsonElement) =
        let customerId (alias: string) =
            Map.find alias customerIds
            |> CustomerId.parse
            |> requiredValue $"customer id {alias}"

        let invoiceId (alias: string) =
            Map.find alias invoiceIds
            |> InvoiceId.parse
            |> requiredValue $"invoice id {alias}"

        let paymentId (alias: string) =
            Map.find alias paymentIds
            |> PaymentId.parse
            |> requiredValue $"payment id {alias}"

        let queryAlias = stringProperty case "query"
        let customerOverrides = case.GetProperty("customerOverrides")

        let storeWithCustomers =
            case.GetProperty("customers").EnumerateArray()
            |> Seq.fold (fun store customerAliasElement ->
                let alias = customerAliasElement.GetString()
                let id = customerId alias
                let hasOverride, customerOverride = customerOverrides.TryGetProperty(alias)
                let email, kind, balance, createdAt =
                    if hasOverride then
                        (
                            stringProperty customerOverride "email",
                            stringProperty customerOverride "kind",
                            Int64.Parse(stringProperty customerOverride "balance", CultureInfo.InvariantCulture),
                            DateTimeOffset.Parse(stringProperty customerOverride "createdAt", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                        )
                    else
                        ($"{alias}@example.test", "standard", 0L, DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.Zero))

                let customer =
                    let address = Email.create email |> requiredValue $"customer email {alias}"
                    Customer.create id address kind (Money.ofMinorUnits balance) createdAt
                    |> requiredValue $"customer {alias}"

                Store.addCustomer customer store
                |> requiredValue $"add customer {alias}") Store.empty

        let invoiceRows =
            case.GetProperty("invoices").EnumerateArray()
            |> Seq.map (fun invoice ->
                let alias = stringProperty invoice "id"
                let owner = stringProperty invoice "owner"
                let status =
                    match stringProperty invoice "status" with
                    | "Paid" -> Paid
                    | "Open" -> Open
                    | value -> fail $"Unsupported invoice status '{value}' in oracle."
                alias, (owner, status))
            |> Map.ofSeq

        let storeWithPayments =
            case.GetProperty("payments").EnumerateArray()
            |> Seq.fold (fun store payment ->
                let invoiceAlias = stringProperty payment "invoice"
                let ownerAlias, status =
                    match Map.tryFind invoiceAlias invoiceRows with
                    | Some(owner, invoiceStatus) -> owner, Some invoiceStatus
                    | None -> queryAlias, None
                let amount = Int64.Parse(stringProperty payment "amount", CultureInfo.InvariantCulture)
                Store.Maintenance141Fixture.importPayment
                    store
                    (invoiceId invoiceAlias)
                    (customerId ownerAlias)
                    status
                    (paymentId (stringProperty payment "id"))
                    (Money.ofMinorUnits amount)) storeWithCustomers

        let query = customerId queryAlias
        let shared = Store.customerPaidTotal storeWithPayments query |> resultNode moneyText

        let account =
            match Customer.accountSummary storeWithPayments query with
            | Error error -> errorResult (DomainError.code error)
            | Ok summary ->
                let node = JsonObject()
                addJson node "ok" (jsonString (moneyText summary.PaidTotal))
                let customerNode = JsonObject()
                addJson customerNode "id" (jsonString (summary.Customer |> Customer.id |> CustomerId.toString))
                addJson customerNode "email" (jsonString (summary.Customer |> Customer.email |> Email.value))
                addJson customerNode "kind" (jsonString (Customer.kind summary.Customer))
                addJson customerNode "balance" (jsonString (summary.Customer |> Customer.balance |> moneyText))
                addJson customerNode "createdAt" (jsonString (summary.Customer |> Customer.createdAt |> universalTimestamp))
                addJson node "customer" (customerNode :> JsonNode)
                node :> JsonNode

        let metrics =
            match Store.customerMetrics storeWithPayments with
            | Error error -> errorResult (DomainError.code error)
            | Ok values ->
                let node = JsonObject()
                addJson node "ok" (jsonString (moneyText values.PaidTotal))
                let counts = JsonArray()
                [ values.Customers
                  values.Products
                  values.Subscriptions
                  values.Invoices
                  values.Payments
                  values.PendingEmails
                  values.SentEmails ]
                |> List.iter (fun count -> counts.Add(jsonInt count))
                addJson node "counts" (counts :> JsonNode)
                node :> JsonNode

        let actual = JsonObject()
        addJson actual "shared" shared
        addJson actual "account" account
        addJson actual "metrics" metrics
        actual :> JsonNode

    let private jsonFromElement (element: JsonElement) = JsonNode.Parse(element.GetRawText())

    let private scoreCase (customerIds: Map<string, string>) (invoiceIds: Map<string, string>) (paymentIds: Map<string, string>) (case: JsonElement) =
        let actual = caseActual customerIds invoiceIds paymentIds case
        let expectedElement = case.GetProperty("expected")
        let expected = jsonFromElement expectedElement
        let expectedAccountElement = expectedElement.GetProperty("account")
        let hasExpectedCustomer, expectedCustomerElement = expectedAccountElement.TryGetProperty("customer")
        if hasExpectedCustomer then
            // The oracle uses fixture aliases in its readable expected customer id. Resolve only
            // this known alias field through the frozen ID table; never normalize candidate output.
            let expectedCustomerAlias = stringProperty expectedCustomerElement "id"
            let expectedCustomerGuid = Map.find expectedCustomerAlias customerIds
            let customerNode = (expected["account"]["customer"]).AsObject()
            customerNode["id"] <- jsonString expectedCustomerGuid
        let actualShared = actual["shared"]
        let actualAccount = actual["account"]
        let actualMetrics = actual["metrics"]
        let expectedShared = expected["shared"]
        let expectedAccount = expected["account"]
        let expectedMetrics = expected["metrics"]
        let sharedPass = JsonNode.DeepEquals(actualShared, expectedShared)
        let accountPass = JsonNode.DeepEquals(actualAccount, expectedAccount)
        let metricsPass = JsonNode.DeepEquals(actualMetrics, expectedMetrics)
        let passed = sharedPass && accountPass && metricsPass

        let checks = JsonObject()
        addJson checks "shared" (jsonBool sharedPass)
        addJson checks "account" (jsonBool accountPass)
        addJson checks "metrics" (jsonBool metricsPass)

        let output = JsonObject()
        addJson output "id" (jsonString (stringProperty case "id"))
        addJson output "passed" (jsonBool passed)
        addJson output "checks" (checks :> JsonNode)
        addJson output "expected" expected
        addJson output "actual" actual
        output :> JsonNode, passed

    [<EntryPoint>]
    let main arguments =
        try
            let oracleArgument =
                arguments
                |> Array.toList
                |> List.tryFindIndex ((=) "--oracle")
                |> Option.bind (fun index -> if index + 1 < arguments.Length then Some arguments[index + 1] else None)
                |> Option.defaultWith (fun () -> fail "Usage: scorer --oracle <oracle.json>")

            use oracleDocument = JsonDocument.Parse(File.ReadAllText(oracleArgument))
            let root = oracleDocument.RootElement
            let fixtureIds = root.GetProperty("fixtures")
            let aliases (name: string) : Map<string, string> =
                fixtureIds.GetProperty(name).EnumerateObject()
                |> Seq.map (fun property -> property.Name, property.Value.GetString())
                |> Map.ofSeq

            let customerIds = aliases "customerIds"
            let invoiceIds = aliases "invoiceIds"
            let paymentIds = aliases "paymentIds"
            let cases = root.GetProperty("cases").EnumerateArray() |> Seq.toArray
            let scored = cases |> Array.map (scoreCase customerIds invoiceIds paymentIds)
            let results = JsonArray()
            scored |> Array.iter (fun (result, _) -> results.Add(result))
            let passed = scored |> Array.forall snd

            let output = JsonObject()
            addJson output "schemaVersion" (jsonInt 1)
            addJson output "operation" (jsonString "Store.customerPaidTotal")
            addJson output "responseCount" (jsonInt scored.Length)
            addJson output "passed" (jsonBool passed)
            addJson output "cases" (results :> JsonNode)
            Console.WriteLine("MAINTENANCE141_SCORE_JSON:" + output.ToJsonString())
            0
        with error ->
            eprintfn "SCORER_ERROR: %s" (error.ToString())
            2
