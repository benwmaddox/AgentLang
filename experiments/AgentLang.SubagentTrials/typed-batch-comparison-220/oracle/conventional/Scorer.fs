namespace AgentLang.SubscriptionHandoff.Control

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes
open Microsoft.FSharp.Reflection
open AgentLang.Business.Domain
open AgentLang.SubscriptionHandoff

module Scorer =
    module BusinessStore = AgentLang.Business.Domain.Store
    module BusinessSubscription = AgentLang.Business.Domain.Subscription

    type BatchInvocation<'failure> =
        Store -> SubscriptionRenewal list -> DateTimeOffset
            -> Result<Store, 'failure>

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
          "missing", "30000000-0000-0000-0000-000000000004"
          "new2", "30000000-0000-0000-0000-000000000005"
          "old2", "30000000-0000-0000-0000-000000000006" ]
        |> Map.ofList

    let private invoiceIdText = "40000000-0000-0000-0000-000000000001"
    let private paymentIdText = "50000000-0000-0000-0000-000000000001"

    let private requiredString (element: JsonElement) (name: string) =
        let value = element.GetProperty(name).GetString()
        if isNull value then failwithf "Expected a string at property %s" name else value

    let private requiredInt (element: JsonElement) (name: string) =
        element.GetProperty(name).GetInt32()

    let private strings (element: JsonElement) (name: string) =
        element.GetProperty(name).EnumerateArray()
        |> Seq.map (fun item ->
            item.GetString()
            |> Option.ofObj
            |> Option.defaultWith (fun () -> failwithf "Expected strings in %s" name))
        |> Seq.toList

    let private parseWith (label: string) (parser: string -> Result<'value, AgentLang.Business.Domain.DomainError>) value =
        match parser value with
        | Ok parsed -> parsed
        | Error error ->
            failwithf "%s: %s" label (AgentLang.Business.Domain.DomainError.code error)

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
        | Error error ->
            failwithf "%s: %s" label (AgentLang.Business.Domain.DomainError.code error)

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
            BusinessSubscription.cancel created (subscriptionId identifier) (instant (requiredInt row "cancelled"))
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
        [ "ghost-product"; "ghost" ]
        |> List.fold (fun current label ->
            if List.contains label productNames then current
            else StoreReflection.removeProduct (productId label) current) withoutGhostCustomer

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

    let private recordField name (value: obj) =
        let recordType = value.GetType()
        if not (FSharpType.IsRecord recordType) then
            failwithf "Expected a record while reading %s" name
        let fields = FSharpType.GetRecordFields recordType
        let values = FSharpValue.GetRecordFields value
        let index = fields |> Array.tryFindIndex (fun field -> field.Name = name)
        match index with
        | Some position -> values.[position]
        | None -> failwithf "Typed failure is missing the %s field" name

    let private customerLabel id =
        let text = CustomerId.toString id
        customerIds |> Map.toSeq |> Seq.find (fun (_, value) -> value = text) |> fst

    let private productLabel id =
        let text = ProductId.toString id
        productIds |> Map.toSeq |> Seq.find (fun (_, value) -> value = text) |> fst

    let private subscriptionLabel id =
        let text = SubscriptionId.toString id
        subscriptionIds
        |> Map.toSeq
        |> Seq.tryFind (fun (_, value) -> value = text)
        |> Option.map fst
        |> Option.defaultWith (fun () -> failwithf "Unrecognized subscription id %s" text)

    let private projectFailure (failure: 'failure) =
        let failureType = failure.GetType()
        if failureType.FullName <> "AgentLang.SubscriptionHandoff.SubscriptionRenewalFailure" then
            failwithf "Expected nominal SubscriptionRenewalFailure, received %s" failureType.FullName
        let fields = FSharpType.GetRecordFields failureType
        if fields.Length <> 3 then
            failwith "SubscriptionRenewalFailure must have exactly Index, OldId, and Cause fields"
        let requireField name expectedType =
            match fields |> Array.tryFind (fun field -> field.Name = name) with
            | Some field when field.PropertyType = expectedType -> ()
            | Some field -> failwithf "SubscriptionRenewalFailure.%s must be %s" name expectedType.FullName
            | None -> failwithf "SubscriptionRenewalFailure is missing %s" name
        requireField "Index" typeof<int>
        requireField "OldId" typeof<SubscriptionId>
        requireField "Cause" typeof<AgentLang.SubscriptionHandoff.DomainError>
        let index = recordField "Index" (box failure) :?> int
        let oldId = recordField "OldId" (box failure) :?> SubscriptionId
        let cause = recordField "Cause" (box failure)
        let code = recordField "Code" cause :?> string
        let message = recordField "Message" cause :?> string
        objectNode
            [ "index", JsonValue.Create(index) :> JsonNode
              "old-id", stringNode (subscriptionLabel oldId)
              "cause", objectNode [ "code", stringNode code; "message", stringNode message ] ]

    let private labelFor (knownIds: Map<string, string>) (identifier: string) =
        knownIds
        |> Map.toSeq
        |> Seq.tryFind (fun (_, knownIdentifier) -> knownIdentifier = identifier)
        |> Option.map fst

    let private membershipMatches (expected: JsonElement) (store: Store) =
        let expectedCustomers = strings expected "customers"
        let actualCustomers = StoreReflection.customerIds store |> List.map CustomerId.toString |> List.choose (labelFor customerIds)
        let expectedProducts = strings expected "products"
        let actualProducts = StoreReflection.productIds store |> List.map ProductId.toString |> List.choose (labelFor productIds)
        let expectedSubscriptions =
            expected.GetProperty("subscriptions").EnumerateArray()
            |> Seq.map (fun row -> requiredString row "id")
            |> Seq.toList
        let actualSubscriptions = StoreReflection.subscriptionIds store |> List.map SubscriptionId.toString |> List.choose (labelFor subscriptionIds)
        let expectedInvoices =
            if List.contains "invoice-marker" (strings expected "invoices") then [ invoiceIdText ] else []
        let actualInvoices = StoreReflection.invoiceIds store |> List.map InvoiceId.toString
        let expectedPayments =
            if List.contains "payment-marker" (strings expected "payments") then [ paymentIdText ] else []
        let actualPayments = StoreReflection.paymentIds store |> List.map PaymentId.toString
        let sameIds (expectedIds: string list) (actualIds: string list) =
            expectedIds.Length = (expectedIds |> Set.ofList |> Set.count)
            && expectedIds.Length = actualIds.Length
            && (Set.ofList expectedIds) = (Set.ofList actualIds)
        let allKnown (ids: Map<string, string>) (actual: string list) =
            actual |> List.forall (fun identifier -> labelFor ids identifier |> Option.isSome)
        allKnown customerIds (StoreReflection.customerIds store |> List.map CustomerId.toString)
        && allKnown productIds (StoreReflection.productIds store |> List.map ProductId.toString)
        && allKnown subscriptionIds (StoreReflection.subscriptionIds store |> List.map SubscriptionId.toString)
        && sameIds expectedCustomers actualCustomers
        && sameIds expectedProducts actualProducts
        && sameIds expectedSubscriptions actualSubscriptions
        && sameIds expectedInvoices actualInvoices
        && sameIds expectedPayments actualPayments

    let private exactSubscriptionTimesMatch (expected: JsonElement) (store: Store) =
        expected.GetProperty("subscriptions").EnumerateArray()
        |> Seq.forall (fun row ->
            let label = requiredString row "id"
            match BusinessStore.subscription (subscriptionId label) store with
            | None -> false
            | Some subscription ->
                let expectedCancelled =
                    match row.GetProperty("cancelled").ValueKind with
                    | JsonValueKind.Number -> Some(instant (requiredInt row "cancelled"))
                    | JsonValueKind.Null -> None
                    | _ -> failwith "Invalid expected cancellation timestamp"
                BusinessSubscription.startedAt subscription = instant (requiredInt row "start")
                && BusinessSubscription.expiresAt subscription = instant (requiredInt row "finish")
                && BusinessSubscription.cancelledAt subscription = expectedCancelled)

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
        let requestedNewIds =
            case.GetProperty("request").GetProperty("changes").EnumerateArray()
            |> Seq.map (fun change -> requiredString change "newId")
            |> Seq.toList
        // Store subscriptions are held in a map. Reconstruct the fixture contract's
        // list order from input rows, then append successful request IDs in request order.
        let subscriptionNames =
            requestedNewIds
            |> List.fold (fun names label ->
                if List.contains label names then names else names @ [ label ]) inputIds
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
            (customers @ products @ invoice @ payment @ emailSnapshot (BusinessStore.pendingEmails store)
             @ emailSnapshot (BusinessStore.sentEmails store))

    let private parseChange (change: JsonElement) =
        let plan =
            match requiredString change "plan" with
            | "monthly" -> Monthly
            | "annual" -> Annual
            | other -> failwithf "Unsupported plan %s" other
        { OldId = subscriptionId (requiredString change "oldId")
          NewId = subscriptionId (requiredString change "newId")
          Plan = plan
          ExpiresAt = instant (requiredInt change "expiry") }

    let private runOne (invoke: BatchInvocation<'failure>) (case: JsonElement) =
        let caseId = requiredString case "id"
        try
            let initialStore = buildStore case
            let inputProjection = projectStore case initialStore
            let collateralBefore = otherEntitySnapshot initialStore
            let preservedBefore = JsonSerializer.Serialize(inputProjection)
            let inputReference = case.GetProperty("store")
            let request = case.GetProperty("request")
            let changes =
                request.GetProperty("changes").EnumerateArray()
                |> Seq.map parseChange
                |> Seq.toList
            let at = instant (requiredInt request "at")
            let result = invoke initialStore changes at
            let inputPreserved =
                preservedBefore = JsonSerializer.Serialize(projectStore case initialStore)
                && membershipMatches inputReference initialStore
                && exactSubscriptionTimesMatch inputReference initialStore
            let actual, collateralPreserved =
                match result with
                | Error failure ->
                    objectNode
                        [ "tag", stringNode "error"
                          "failure", projectFailure failure
                          "input", inputProjection ],
                    collateralBefore = otherEntitySnapshot initialStore
                | Ok finalStore ->
                    objectNode
                        [ "tag", stringNode "ok"
                          "store", projectStore case finalStore
                          "input", inputProjection ],
                    collateralBefore = otherEntitySnapshot finalStore
            let expectedResult = case.GetProperty("expected")
            let expectedStore =
                if requiredString expectedResult "tag" = "error" then expectedResult.GetProperty("input")
                else expectedResult.GetProperty("store")
            let resultStore =
                match result with
                | Error _ -> initialStore
                | Ok finalStore -> finalStore
            let entityMembershipPreserved = membershipMatches expectedStore resultStore
            let instantPrecisionPreserved = exactSubscriptionTimesMatch expectedStore resultStore
            objectNode
                [ "id", stringNode caseId
                  "actual", actual
                  "inputPreserved", boolNode inputPreserved
                  "entityMembershipPreserved", boolNode entityMembershipPreserved
                  "instantPrecisionPreserved", boolNode instantPrecisionPreserved
                  "collateralPreserved", boolNode collateralPreserved ]
        with error ->
            objectNode
                [ "id", stringNode caseId
                  "setupFailure", stringNode (error.ToString()) ]

    let runBatchFileWith (batch: BatchInvocation<'failure>) path =
        use document = JsonDocument.Parse(System.IO.File.ReadAllText(path))
        document.RootElement.EnumerateArray()
        |> Seq.map (runOne batch)
        |> Seq.iter (fun output -> Console.Out.WriteLine(JsonSerializer.Serialize(output)))
