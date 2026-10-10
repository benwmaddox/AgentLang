namespace AgentLang.SubscriptionHandoff.Control

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes
open AgentLang.Business.Domain

module Scorer =
    module BusinessStore = AgentLang.Business.Domain.Store
    module BusinessSubscription = AgentLang.Business.Domain.Subscription

    type Handoff = Store -> SubscriptionId -> SubscriptionId -> string -> DateTimeOffset -> DateTimeOffset -> bool -> Result<Store, DomainError>
    type Renewal = Store -> SubscriptionId -> SubscriptionId -> DateTimeOffset -> DateTimeOffset -> Result<Store, DomainError>
    type Invocation = Store -> SubscriptionId -> SubscriptionId -> string -> DateTimeOffset -> DateTimeOffset -> Result<Store, DomainError>

    let private epoch = DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)

    let private customerIds =
        [ "alice", "10000000-0000-0000-0000-000000000001"
          "bob", "10000000-0000-0000-0000-000000000002"
          "ghost", "10000000-0000-0000-0000-000000000003" ]
        |> Map.ofList

    let private productIds =
        [ "basic", "20000000-0000-0000-0000-000000000001"
          "other", "20000000-0000-0000-0000-000000000002"
          "ghost-product", "20000000-0000-0000-0000-000000000003"
          "ghost", "20000000-0000-0000-0000-000000000004" ]
        |> Map.ofList

    let private subscriptionIds =
        [ "old", "30000000-0000-0000-0000-000000000001"
          "new", "30000000-0000-0000-0000-000000000002"
          "third", "30000000-0000-0000-0000-000000000003"
          "missing", "30000000-0000-0000-0000-000000000004" ]
        |> Map.ofList

    let private invoiceIdText = "40000000-0000-0000-0000-000000000001"
    let private paymentIdText = "50000000-0000-0000-0000-000000000001"

    let private requiredString (element: JsonElement) (name: string) =
        let value = element.GetProperty(name).GetString()
        if isNull value then failwithf "Expected a string at property %s" name else value

    let private requiredInt (element: JsonElement) (name: string) = element.GetProperty(name).GetInt32()

    let private strings (element: JsonElement) (name: string) =
        element.GetProperty(name).EnumerateArray()
        |> Seq.map (fun item -> item.GetString() |> Option.ofObj |> Option.defaultWith (fun () -> failwithf "Expected strings in %s" name))
        |> Seq.toList

    let private parseWith (label: string) (parser: string -> Result<'value, DomainError>) (value: string) : 'value =
        match parser value with
        | Ok parsed -> parsed
        | Error error -> failwithf "%s: %s" label (DomainError.code error)

    let private customerId (label: string) =
        parseWith "fixture customer id" CustomerId.parse (Map.find label customerIds)

    let private productId (label: string) =
        parseWith "fixture product id" ProductId.parse (Map.find label productIds)

    let private subscriptionId (label: string) =
        parseWith "fixture subscription id" SubscriptionId.parse (Map.find label subscriptionIds)

    let private invoiceId = parseWith "fixture invoice id" InvoiceId.parse invoiceIdText
    let private paymentId = parseWith "fixture payment id" PaymentId.parse paymentIdText

    let private unwrap label = function
        | Ok value -> value
        | Error error -> failwithf "%s: %s" label (DomainError.code error)

    let private instant cell = epoch.AddDays(float cell)

    let private addCustomer label store =
        let email = Email.create (label + "@example.test") |> unwrap "fixture email"
        let customer =
            Customer.create (customerId label) email "regular" (Money.ofMinorUnits 1250L) epoch
            |> unwrap "fixture customer"
        BusinessStore.addCustomer customer store |> unwrap "fixture store customer"

    let private addProduct label store =
        let product =
            Product.create (productId label) ("Plan " + label) (Money.ofMinorUnits 199L)
            |> unwrap "fixture product"
        BusinessStore.addProduct product store |> unwrap "fixture store product"

    let private createCollateral store =
        let createdInvoice =
            Invoice.create store invoiceId (customerId "alice") [ productId "basic", 1 ] epoch
            |> Result.map fst
            |> unwrap "fixture invoice"
        let paidInvoice =
            Payment.submit createdInvoice (PaymentProvider.deterministic "payment-marker") paymentId invoiceId
                (Money.ofMinorUnits 199L) epoch
            |> Result.map fst
            |> unwrap "fixture payment"
        let queuedSent =
            EmailOutbox.queue paidInvoice (customerId "alice") "sent-marker" "sent marker body"
            |> Result.map fst
            |> unwrap "fixture sent email"
        let queuedOutbox =
            EmailOutbox.queue queuedSent (customerId "alice") "outbox-marker" "outbox marker body"
            |> Result.map fst
            |> unwrap "fixture outbox email"
        EmailOutbox.deliverNext queuedOutbox EmailProvider.deterministic
        |> unwrap "fixture email delivery"

    let private addSubscriptionRow store (row: JsonElement) =
        let identifier = requiredString row "id"
        let customer = requiredString row "customer"
        let product = requiredString row "product"
        let term = requiredString row "term"
        let started = requiredInt row "start"
        let finishes = requiredInt row "finish"
        let created =
            BusinessSubscription.start store (subscriptionId identifier) (customerId customer) (productId product)
                term (instant started) (instant finishes)
            |> unwrap ("fixture subscription " + identifier)
            |> fst
        match requiredString row "status", row.GetProperty("cancelled").ValueKind with
        | "cancelled", JsonValueKind.Number ->
            let cancelled = row.GetProperty("cancelled").GetInt32()
            BusinessSubscription.cancel created (subscriptionId identifier) (instant cancelled)
            |> Result.map fst
            |> unwrap ("fixture cancellation " + identifier)
        | "active", JsonValueKind.Null -> created
        | state, _ -> failwithf "Unsupported fixture subscription state %s" state

    let private buildStore (case: JsonElement) =
        let source = case.GetProperty("store")
        let customerNames = strings source "customers"
        let productNames = strings source "products"
        let rows = source.GetProperty("subscriptions").EnumerateArray() |> Seq.toList
        let store =
            BusinessStore.empty
            |> addCustomer "alice"
            |> addCustomer "bob"
            |> addCustomer "ghost"
            |> addProduct "basic"
            |> addProduct "other"
            |> addProduct "ghost-product"
            |> addProduct "ghost"
            |> createCollateral
        let orderedRows =
            rows
            |> List.sortBy (fun row -> if requiredString row "status" = "cancelled" then 0 else 1)
        let withSubscriptions = orderedRows |> List.fold addSubscriptionRow store
        let withoutGhostCustomer =
            if List.contains "ghost" customerNames then withSubscriptions
            else StoreReflection.removeCustomer (customerId "ghost") withSubscriptions
        let withoutGhostProduct =
            [ "ghost-product"; "ghost" ]
            |> List.fold (fun current label ->
                if List.contains label productNames then current
                else StoreReflection.removeProduct (productId label) current) withoutGhostCustomer
        withoutGhostProduct

    let private cell (value: DateTimeOffset) =
        int ((value.ToUniversalTime() - epoch).TotalDays)

    let private objectNode (fields: (string * JsonNode) list) =
        let node = JsonObject()
        fields |> List.iter (fun (key, value) -> node.[key] <- value)
        node :> JsonNode

    let private arrayNode (values: JsonNode list) =
        let node = JsonArray()
        values |> List.iter node.Add
        node :> JsonNode

    let private stringNode (value: string) = JsonValue.Create(value) :> JsonNode
    let private boolNode (value: bool) = JsonValue.Create(value) :> JsonNode

    let private customerLabel id =
        let text = CustomerId.toString id
        customerIds |> Map.toSeq |> Seq.find (fun (_, value) -> value = text) |> fst

    let private productLabel id =
        let text = ProductId.toString id
        productIds |> Map.toSeq |> Seq.find (fun (_, value) -> value = text) |> fst

    let private projectSubscription (label: string) (subscription: Subscription) =
        let cancelled: JsonNode =
            match BusinessSubscription.cancelledAt subscription with
            | Some value -> JsonValue.Create(cell value) :> JsonNode
            | None -> null
        objectNode
            [ "id", stringNode label
              "customer", stringNode (customerLabel (BusinessSubscription.customerId subscription))
              "product", stringNode (productLabel (BusinessSubscription.productId subscription))
              "start", JsonValue.Create(cell (BusinessSubscription.startedAt subscription)) :> JsonNode
              "finish", JsonValue.Create(cell (BusinessSubscription.expiresAt subscription)) :> JsonNode
              "status", stringNode (if BusinessSubscription.status subscription = Active then "active" else "cancelled")
              "cancelled", cancelled
              "term", stringNode (BusinessSubscription.term subscription) ]

    let private projectStore (case: JsonElement) (store: Store) =
        let source = case.GetProperty("store")
        let customerNames =
            strings source "customers"
            |> List.filter (fun label -> BusinessStore.customer (customerId label) store |> Option.isSome)
        let productNames =
            strings source "products"
            |> List.filter (fun label -> BusinessStore.product (productId label) store |> Option.isSome)
        let inputRows = source.GetProperty("subscriptions").EnumerateArray() |> Seq.toList
        let inputIds = inputRows |> List.map (fun row -> requiredString row "id")
        let request = case.GetProperty("request")
        let newLabel = requiredString request "newId"
        let subscriptionNames =
            if List.contains newLabel inputIds then inputIds
            else inputIds @ [ newLabel ]
        let subscriptions =
            subscriptionNames
            |> List.choose (fun label ->
                match BusinessStore.subscription (subscriptionId label) store with
                | Some value -> Some(projectSubscription label value)
                | None -> None)
        let invoices =
            if BusinessStore.invoice invoiceId store |> Option.isSome then [ stringNode "invoice-marker" ] else []
        let payments =
            if BusinessStore.payment paymentId store |> Option.isSome then [ stringNode "payment-marker" ] else []
        let outbox = BusinessStore.pendingEmails store |> List.map (EmailMessage.subject >> stringNode)
        let sent = BusinessStore.sentEmails store |> List.map (EmailMessage.subject >> stringNode)
        objectNode
            [ "customers", customerNames |> List.map stringNode |> arrayNode
              "products", productNames |> List.map stringNode |> arrayNode
              "subscriptions", subscriptions |> arrayNode
              "invoices", invoices |> arrayNode
              "payments", payments |> arrayNode
              "outbox", outbox |> arrayNode
              "sent", sent |> arrayNode ]

    let private otherEntitySnapshot (store: Store) =
        let customers =
            [ "alice"; "bob"; "ghost" ]
            |> List.map (fun label ->
                match BusinessStore.customer (customerId label) store with
                | None -> label + "=missing"
                | Some value ->
                    String.concat "|"
                        [ label
                          CustomerId.toString (Customer.id value)
                          Email.value (Customer.email value)
                          Customer.kind value
                          string (Money.minorUnits (Customer.balance value))
                          (Customer.createdAt value).ToString("O", CultureInfo.InvariantCulture) ])
        let products =
            [ "basic"; "other"; "ghost-product"; "ghost" ]
            |> List.map (fun label ->
                match BusinessStore.product (productId label) store with
                | None -> label + "=missing"
                | Some value ->
                    String.concat "|"
                        [ label
                          ProductId.toString (Product.id value)
                          Product.name value
                          string (Money.minorUnits (Product.unitPrice value)) ])
        let invoice =
            match BusinessStore.invoice invoiceId store with
            | None -> [ "invoice=missing" ]
            | Some value ->
                let lines =
                    Invoice.lines value
                    |> List.map (fun line ->
                        String.concat ":"
                            [ ProductId.toString (InvoiceLine.productId line)
                              InvoiceLine.description line
                              string (InvoiceLine.quantity line)
                              string (Money.minorUnits (InvoiceLine.unitPrice line))
                              string (Money.minorUnits (InvoiceLine.lineTotal line)) ])
                [ String.concat "|"
                    [ InvoiceId.toString (Invoice.id value)
                      CustomerId.toString (Invoice.customerId value)
                      string (Money.minorUnits (Invoice.total value))
                      string (Invoice.status value)
                      (Invoice.createdAt value).ToString("O", CultureInfo.InvariantCulture) ]
                  yield! lines ]
        let payment =
            match BusinessStore.payment paymentId store with
            | None -> [ "payment=missing" ]
            | Some value ->
                [ String.concat "|"
                    [ PaymentId.toString (Payment.id value)
                      InvoiceId.toString (Payment.invoiceId value)
                      string (Money.minorUnits (Payment.amount value))
                      Payment.providerReference value
                      (Payment.paidAt value).ToString("O", CultureInfo.InvariantCulture) ] ]
        let emailSnapshot messages =
            messages
            |> List.map (fun value ->
                String.concat "|"
                    [ Email.value (EmailMessage.toAddress value)
                      EmailMessage.subject value
                      EmailMessage.body value ])
        String.concat "\n"
            (customers @ products @ invoice @ payment @ emailSnapshot (BusinessStore.pendingEmails store) @ emailSnapshot (BusinessStore.sentEmails store))

    let private parseControl = function
        | "composed" -> Composed
        | "skip-creation-validation-dry-run" -> SkipCreationValidationOnDryRun
        | "return-modified-dry-run" -> ReturnModifiedStoreOnDryRun
        | name -> failwithf "Unknown control: %s" name

    let private runOne (invoke: Invocation) (case: JsonElement) =
        let caseId = requiredString case "id"
        try
            let initialStore = buildStore case
            let inputProjection = projectStore case initialStore
            let collateralBefore = otherEntitySnapshot initialStore
            let preservedBefore = JsonSerializer.Serialize(inputProjection)
            let request = case.GetProperty("request")
            let oldId = subscriptionId (requiredString request "oldId")
            let newId = subscriptionId (requiredString request "newId")
            let term = requiredString request "term"
            let at = instant (requiredInt request "at")
            let expiry = instant (requiredInt request "expiry")
            let result = invoke initialStore oldId newId term at expiry
            let inputPreserved = preservedBefore = JsonSerializer.Serialize(projectStore case initialStore)
            let actual, collateralPreserved =
                match result with
                | Error error ->
                    objectNode
                        [ "tag", stringNode "error"
                          "code", stringNode (DomainError.code error)
                          "input", inputProjection ],
                    collateralBefore = otherEntitySnapshot initialStore
                | Ok finalStore ->
                    objectNode
                        [ "tag", stringNode "ok"
                          "store", projectStore case finalStore
                          "input", inputProjection ],
                    collateralBefore = otherEntitySnapshot finalStore
            objectNode
                [ "id", stringNode caseId
                  "actual", actual
                  "inputPreserved", boolNode inputPreserved
                  "collateralPreserved", boolNode collateralPreserved ]
        with error ->
            objectNode
                [ "id", stringNode caseId
                  "setupFailure", stringNode (error.ToString()) ]

    let runFileWith (handoff: Handoff) dryRun path =
        let invoke store oldId newId term at expiry = handoff store oldId newId term at expiry dryRun
        use document = JsonDocument.Parse(System.IO.File.ReadAllText(path))
        document.RootElement.EnumerateArray()
        |> Seq.map (runOne invoke)
        |> Seq.iter (fun output -> Console.Out.WriteLine(JsonSerializer.Serialize(output)))

    let runRenewalFileWith (renewal: Renewal) path =
        let invoke store oldId newId _term at expiry = renewal store oldId newId at expiry
        use document = JsonDocument.Parse(System.IO.File.ReadAllText(path))
        document.RootElement.EnumerateArray()
        |> Seq.map (runOne invoke)
        |> Seq.iter (fun output -> Console.Out.WriteLine(JsonSerializer.Serialize(output)))

    let runFile controlName dryRun path =
        let handoff = SubscriptionHandoff.run (parseControl controlName)
        runFileWith handoff dryRun path
