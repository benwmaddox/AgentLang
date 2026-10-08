namespace AgentLang.Business.Transitions.Tests

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Text.Json.Nodes
open AgentLang
open AgentLang.Business
open AgentLang.Business.Domain
open AgentLang.Business.Contracts

module Program =
    type private OracleState =
        { Store: Domain.Store
          Known: KnownEntityIds }

    let mutable private assertions = 0
    let mutable private groups = 0
    let mutable private totalWords = 0
    let mutable private totalTypes = 0
    let mutable private totalTests = 0
    let mutable private totalExamples = 0
    let mutable private reloadChecks = 0
    let mutable private populatedDeliveryWords = 0
    let mutable private populatedDeliveryTypes = 0
    let mutable private populatedDeliveryTests = 0
    let mutable private populatedDeliveryExamples = 0
    let mutable private populatedDeliveryReloadChecks = 0
    let mutable private temporaryNames = Set.empty<string>

    let private check condition message =
        assertions <- assertions + 1
        if not condition then failwith message

    let private equal expected actual message =
        assertions <- assertions + 1
        if expected <> actual then failwith $"{message}: expected {expected}, got {actual}"

    let private jstr (value: string) = JsonValue.Create(value) :> JsonNode
    let private jbool (value: bool) = JsonValue.Create(value) :> JsonNode
    let private stringValue (node: JsonNode) = node.GetValue<string>()
    let private boolValue (node: JsonNode) = node.GetValue<bool>()

    let private arguments (fields: (string * JsonNode) list) =
        let result = JsonObject()
        for name, value in fields do
            result[name] <- if isNull value then null else value.DeepClone()
        result

    let private dispatch (engine: Runtime.Engine) operation fields =
        engine.Dispatch(operation, arguments fields)

    let private responseOk (response: JsonObject) = boolValue response.["ok"]

    let private expectOk label (response: JsonObject) =
        if not (responseOk response) then
            let code =
                try stringValue response.["error"].["code"]
                with _ -> "unknown"
            let detail =
                try stringValue response.["error"].["message"]
                with _ -> response.ToJsonString()
            failwith $"{label}: expected success, got {code}: {detail}"
        response

    let private expectError label (response: JsonObject) =
        check (not (responseOk response)) $"{label}: expected failure, got {response.ToJsonString()}"
        response

    let private eval (engine: Runtime.Engine) code =
        dispatch engine "eval"
            [ "frontend", jstr "flow"
              "code", jstr code
              "structured", jbool true ]
        |> expectOk $"evaluate Flow: {code}"

    let private evalFailure (engine: Runtime.Engine) code =
        dispatch engine "eval"
            [ "frontend", jstr "flow"
              "code", jstr code ]
        |> expectError $"reject Flow: {code}"

    let private outputValue (response: JsonObject) =
        response.["data"].["structuredStack"].["values"].AsArray().[0]

    let private kind (node: JsonNode) = stringValue node.["kind"]

    let private scalarPayload (node: JsonNode) =
        equal "scalar" (kind node) "nominal scalar representation"
        node.["value"]

    let private scalarString (node: JsonNode) =
        let payload = scalarPayload node
        equal "string" (kind payload) "string-backed nominal payload"
        stringValue payload.["value"]

    let private scalarInt64 (node: JsonNode) =
        let payload = scalarPayload node
        equal "int" (kind payload) "integer-backed nominal payload"
        Int64.Parse(stringValue payload.["value"], CultureInfo.InvariantCulture)

    let private field (node: JsonNode) name =
        equal "record" (kind node) $"{name} is in a record"
        node.["fields"].AsArray()
        |> Seq.find (fun entry -> stringValue entry.["name"] = name)
        |> fun entry -> entry.["value"]

    let private fieldString node name =
        let value = field node name
        equal "string" (kind value) $"{name} is String"
        stringValue value.["value"]

    let private fieldInt64 node name =
        let value = field node name
        equal "int" (kind value) $"{name} is Int"
        Int64.Parse(stringValue value.["value"], CultureInfo.InvariantCulture)

    let private fieldMoney node name = field node name |> scalarInt64
    let private fieldScalar node name = field node name |> scalarString

    let private listItems (value: JsonNode) =
        equal "list" (kind value) "Store collections are typed lists"
        value.["items"].AsArray() |> Seq.toList

    let private optionValue project (value: JsonNode) =
        equal "option" (kind value) "optional field retains Option type"
        match stringValue value.["case"] with
        | "none" -> None
        | "some" -> Some(project value.["value"])
        | other -> failwith $"Unknown option case {other}"

    let private resultCase (value: JsonNode) =
        equal "result" (kind value) "transition returns a typed Result"
        stringValue value.["case"]

    let private resultPayload (value: JsonNode) = value.["value"]

    let private resultErrorCode value =
        equal "error" (resultCase value) "transition error case"
        fieldString (resultPayload value) "code"

    let private resultErrorMessage value =
        equal "error" (resultCase value) "transition error case"
        fieldString (resultPayload value) "message"

    let private flowString (value: string) =
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\""

    let private runGroup name action =
        action ()
        groups <- groups + 1
        printfn "PASS %s" name

    let private runResultTests (engine: Runtime.Engine) expectedCount =
        let response = dispatch engine "test-all" [] |> expectOk "run all attached business transition tests"
        let rows = response.["data"].["results"].AsArray()
        equal expectedCount rows.Count "attached test inventory"
        for row in rows do
            let name = stringValue row.["name"]
            check (boolValue row.["passed"]) $"attached test passes: {name} ({row.ToJsonString()})"
        totalTests <- rows.Count
        response

    let private runExamplesForWord (engine: Runtime.Engine) word =
        let listed =
            dispatch engine "examples" [ "word", jstr word ]
            |> expectOk $"list examples for {word}"
            |> fun response -> response.["data"].AsArray()
        let response = dispatch engine "example" [ "word", jstr word ] |> expectOk $"run examples for {word}"
        let rows = response.["data"].["results"].AsArray()
        equal listed.Count rows.Count $"example result inventory for {word}"
        for row in rows do check (boolValue row.["passed"]) $"example passes for {word}: {row.ToJsonString()}"
        rows.Count

    let private oracleOk label = function
        | Ok value -> value
        | Error error -> failwith $"{label}: {Domain.DomainError.code error}: {Domain.DomainError.message error}"

    let private contractOk label = function
        | Ok value -> value
        | Error error -> failwith $"{label}: {error.Code} at {error.Path}: {error.Message}"

    let private guidId parser label value = parser value |> oracleOk label

    let private customerId value = guidId Domain.CustomerId.parse "CustomerId" value
    let private subscriptionId value = guidId Domain.SubscriptionId.parse "SubscriptionId" value
    let private invoiceId value = guidId Domain.InvoiceId.parse "InvoiceId" value
    let private productId value = guidId Domain.ProductId.parse "ProductId" value
    let private paymentId value = guidId Domain.PaymentId.parse "PaymentId" value

    let private instant (value: string) =
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)

    let private parseStatus label parse value =
        match value with
        | "active" -> parse true
        | "cancelled" -> parse false
        | other -> failwith $"{label} has unknown status {other}"

    let private parseInvoiceStatus value =
        match value with
        | "open" -> Domain.InvoiceStatus.Open
        | "paid" -> Domain.InvoiceStatus.Paid
        | other -> failwith $"Invoice has unknown status {other}"

    let private readCustomer node : CustomerRecord =
        { Id = scalarString (field node "id") |> customerId
          Email = scalarString (field node "email") |> fun value -> Domain.Email.create value |> oracleOk "Email"
          Kind = fieldString node "kind"
          Balance = field node "balance" |> scalarInt64 |> Domain.Money.ofMinorUnits
          CreatedAt = field node "created-at" |> scalarString |> instant }

    let private readProduct node : ProductRecord =
        { Id = scalarString (field node "id") |> productId
          Name = fieldString node "name"
          UnitPrice = field node "unit-price" |> scalarInt64 |> Domain.Money.ofMinorUnits }

    let private readSubscription node : SubscriptionRecord =
        { Id = scalarString (field node "id") |> subscriptionId
          CustomerId = scalarString (field node "customer-id") |> customerId
          ProductId = scalarString (field node "product-id") |> productId
          Term = fieldString node "term"
          StartedAt = field node "started-at" |> scalarString |> instant
          ExpiresAt = field node "expires-at" |> scalarString |> instant
          Status = field node "status" |> scalarString |> parseStatus "Subscription" (fun active -> if active then Domain.SubscriptionStatus.Active else Domain.SubscriptionStatus.Cancelled)
          CancelledAt = field node "cancelled-at" |> optionValue (scalarString >> instant) }

    let private readInvoiceLine node : InvoiceLineRecord =
        { ProductId = scalarString (field node "product-id") |> productId
          Description = fieldString node "description"
          Quantity = fieldInt64 node "quantity" |> int32
          UnitPrice = field node "unit-price" |> scalarInt64 |> Domain.Money.ofMinorUnits
          LineTotal = field node "line-total" |> scalarInt64 |> Domain.Money.ofMinorUnits }

    let private readInvoice node : InvoiceRecord =
        { Id = scalarString (field node "id") |> invoiceId
          CustomerId = scalarString (field node "customer-id") |> customerId
          Lines = field node "lines" |> listItems |> List.map readInvoiceLine
          Total = field node "total" |> scalarInt64 |> Domain.Money.ofMinorUnits
          CreatedAt = field node "created-at" |> scalarString |> instant
          Status = field node "status" |> scalarString |> parseInvoiceStatus }

    let private readPayment node : PaymentRecord =
        { Id = scalarString (field node "id") |> paymentId
          InvoiceId = scalarString (field node "invoice-id") |> invoiceId
          Amount = field node "amount" |> scalarInt64 |> Domain.Money.ofMinorUnits
          ProviderReference = fieldString node "provider-reference"
          PaidAt = field node "paid-at" |> scalarString |> instant }

    let private readEmail node : EmailRecord =
        { To = scalarString (field node "to") |> fun value -> Domain.Email.create value |> oracleOk "email recipient"
          Subject = fieldString node "subject"
          Body = fieldString node "body" }

    let private readLanguageStore (value: JsonNode) : FixtureDocument =
        equal "Store" (stringValue value.["name"]) "projected value has the Store nominal type"
        { Customers = field value "customers" |> listItems |> List.map readCustomer
          Products = field value "products" |> listItems |> List.map readProduct
          Subscriptions = field value "subscriptions" |> listItems |> List.map readSubscription
          Invoices = field value "invoices" |> listItems |> List.map readInvoice
          Payments = field value "payments" |> listItems |> List.map readPayment
          PendingEmails = field value "email-outbox" |> listItems |> List.map readEmail
          SentEmails = field value "sent-emails" |> listItems |> List.map readEmail
          ProviderOutcomes = [] }
        |> Contract.serializeCanonical
        |> contractOk "canonicalize AgentLang Store projection"
        |> Contract.parse
        |> contractOk "validate AgentLang Store projection"

    let private oracleProjection state =
        Contract.Projection.store state.Store state.Known |> contractOk "project conventional immutable Store"

    let private seedOracle () =
        let seed = Contract.Factory.baseline () |> contractOk "construct conventional baseline"
        { Store = seed.Store; Known = seed.KnownIds }, seed.Projection

    let private addKnownSubscription (id: Domain.SubscriptionId) (known: KnownEntityIds) = { known with Subscriptions = known.Subscriptions @ [ id ] }
    let private addKnownInvoice (id: Domain.InvoiceId) (known: KnownEntityIds) = { known with Invoices = known.Invoices @ [ id ] }
    let private addKnownPayment (id: Domain.PaymentId) (known: KnownEntityIds) = { known with Payments = known.Payments @ [ id ] }

    let private compareStoreResult label (languageResult: JsonNode) (oracleResult: Result<Domain.Store, Domain.DomainError>) knownOnSuccess =
        match resultCase languageResult, oracleResult with
        | "error", Error oracleError ->
            let expectedCode = Domain.DomainError.code oracleError
            equal expectedCode (resultErrorCode languageResult) $"{label} error code matches oracle"
            check (not (String.IsNullOrWhiteSpace(resultErrorMessage languageResult))) $"{label} error has a structured message"
            None
        | "ok", Ok oracleStore ->
            let langProjection = resultPayload languageResult |> readLanguageStore
            let expected = Contract.Projection.store oracleStore knownOnSuccess |> contractOk $"{label} oracle projection"
            equal expected langProjection $"{label} full Store projection matches oracle"
            Some { Store = oracleStore; Known = knownOnSuccess }
        | "error", Ok _ -> failwith $"{label}: AgentLang rejected a transition accepted by F# oracle"
        | "ok", Error problem -> failwith $"{label}: AgentLang accepted transition rejected by {Domain.DomainError.code problem}"
        | other, _ -> failwith $"{label}: unknown Result case {other}"

    let private compareFailure label (languageResult: JsonNode) oracleResult =
        match oracleResult with
        | Error problem ->
            equal (Domain.DomainError.code problem) (resultErrorCode languageResult) $"{label} error code matches F# oracle"
            check (not (String.IsNullOrWhiteSpace(resultErrorMessage languageResult))) $"{label} reports structured error detail"
        | Ok _ -> failwith $"{label}: reference unexpectedly accepted the invalid input"

    let private expectResultError label (languageResult: JsonNode) expectedCode =
        equal expectedCode (resultErrorCode languageResult) $"{label} error code"

    let private defineTemporary (engine: Runtime.Engine) label source =
        let _response =
            dispatch engine "define"
                [ "frontend", jstr "flow"
                  "source", jstr source
                  "temporary", jbool true ]
            |> expectOk label
        let names =
            FlowParser.parseDocument "<temporary-probes>" source
            |> Result.defaultWith (fun diagnostic -> failwith (Diagnostics.render diagnostic))
            |> fun document -> document.Words |> List.map _.Name |> Set.ofList
        temporaryNames <- Set.union temporaryNames names
        _response

    let private projectLangFromExpression engine code = eval engine code |> outputValue |> readLanguageStore

    let private projectOracle state = oracleProjection state

    let private freshOracle () = seedOracle () |> fst

    let private wordIds (engine: Runtime.Engine) names =
        let words = dispatch engine "words" [] |> expectOk "list dictionary words" |> fun response -> response.["data"].["words"].AsArray()
        names
        |> List.map (fun name ->
            let entry = words |> Seq.find (fun row -> stringValue row.["name"] = name)
            name, stringValue entry.["id"])

    let private sourceForWord (engine: Runtime.Engine) name =
        dispatch engine "source" [ "word", jstr name ] |> expectOk $"read source for {name}" |> fun response -> stringValue response.["data"]

    let private sourceForType (engine: Runtime.Engine) name =
        dispatch engine "source" [ "type", jstr name ] |> expectOk $"read source for type {name}" |> fun response -> stringValue response.["data"]

    let private jsonStrings (node: JsonNode) = node.AsArray() |> Seq.map stringValue |> Seq.toList

    let private customerIdExpr value = $"CustomerId::new({flowString value})"
    let private subscriptionIdExpr value = $"SubscriptionId::new({flowString value})"
    let private productIdExpr value = $"ProductId::new({flowString value})"
    let private invoiceIdExpr value = $"InvoiceId::new({flowString value})"
    let private paymentIdExpr value = $"PaymentId::new({flowString value})"
    let private instantExpr value = $"Instant::new({flowString value})"
    let private moneyExpr (value: int64) = $"Money::new({value.ToString(CultureInfo.InvariantCulture)})"

    let private cartLineExpr product (quantity: int64) =
        $"cartLine::new(product-id = {productIdExpr product}, quantity = {quantity.ToString(CultureInfo.InvariantCulture)})"

    let private expectedLine productName productKey quantity price =
        let unitPrice = Domain.Money.ofMinorUnits price
        let lineTotal = Domain.Money.multiplyByQuantity unitPrice quantity |> oracleOk "reference line multiplication"
        { ProductId = productId productKey
          Description = productName
          Quantity = quantity
          UnitPrice = unitPrice
          LineTotal = lineTotal }

    let private compareLine label (languageResult: JsonNode) (expected: Result<InvoiceLineRecord, Domain.DomainError>) =
        match resultCase languageResult, expected with
        | "error", Error error ->
            equal (Domain.DomainError.code error) (resultErrorCode languageResult) $"{label} error code matches oracle"
            check (not (String.IsNullOrWhiteSpace(resultErrorMessage languageResult))) $"{label} returns structured error detail"
        | "ok", Ok expectedLine ->
            let actual = resultPayload languageResult
            let actualLine = readInvoiceLine actual
            equal expectedLine actualLine $"{label} line fields match oracle"
        | "error", Ok _ -> failwith $"{label}: AgentLang rejected a line accepted by oracle"
        | "ok", Error problem -> failwith $"{label}: AgentLang accepted a line rejected by {Domain.DomainError.code problem}"
        | other, _ -> failwith $"{label}: unknown Result case {other}"

    let private compareMoneyResult label (languageResult: JsonNode) (expected: Result<Domain.Money, Domain.DomainError>) =
        match resultCase languageResult, expected with
        | "error", Error error ->
            equal (Domain.DomainError.code error) (resultErrorCode languageResult) $"{label} error code matches oracle"
            check (not (String.IsNullOrWhiteSpace(resultErrorMessage languageResult))) $"{label} returns structured error detail"
        | "ok", Ok expectedMoney ->
            equal (Domain.Money.minorUnits expectedMoney) (resultPayload languageResult |> scalarInt64) $"{label} exact minor units match oracle"
        | "error", Ok _ -> failwith $"{label}: AgentLang rejected a money result accepted by oracle"
        | "ok", Error problem -> failwith $"{label}: AgentLang accepted money result rejected by {Domain.DomainError.code problem}"
        | other, _ -> failwith $"{label}: unknown Result case {other}"

    let private testSeedAndTypedBoundaries (engine: Runtime.Engine) =
        let oracle, expected = seedOracle ()
        let actual = projectLangFromExpression engine "business::seed(unit)"
        equal expected actual "business.seed exactly matches Factory.baseline across all seven collections"
        equal expected (projectOracle oracle) "Contract projection independently confirms the F# seed"
        let wrongSubscriptionKey = "60000000-0000-0000-0000-000000000001"
        let wrongCustomerKey = "10000000-0000-0000-0000-000000000001"
        let knownProductKey = "20000000-0000-0000-0000-000000000001"
        let startsAt = "2026-02-01T12:00:00.0000000+00:00"
        let expiresAt = "2026-03-01T12:00:00.0000000+00:00"
        let wrongNominal =
            evalFailure engine
                $"subscription::start(business::seed(unit), {subscriptionIdExpr wrongSubscriptionKey}, {productIdExpr wrongCustomerKey}, {productIdExpr knownProductKey}, \"monthly\", {instantExpr startsAt}, {instantExpr expiresAt})"
        let detail = wrongNominal.["error"].ToJsonString()
        check (detail.Contains("CustomerId", StringComparison.Ordinal) && detail.Contains("ProductId", StringComparison.Ordinal)) "nominal CustomerId cannot be substituted with ProductId"
        let floatingMoney = evalFailure engine "money::add(Money::new(1), 1.0)"
        let floatDetail = floatingMoney.["error"].ToJsonString()
        check (floatDetail.Contains("Money", StringComparison.Ordinal) && floatDetail.Contains("Float", StringComparison.Ordinal)) "Float cannot enter nominal Money arithmetic"

    let private testSubscriptions (engine: Runtime.Engine) =
        let oracle, _ = seedOracle ()
        let baseSubscription = oracle.Known.Subscriptions.Head
        let customer = oracle.Known.Customers.Head
        let product = oracle.Known.Products.Head
        let newIdText = "30000000-0000-0000-0000-000000000002"
        let newId = subscriptionId newIdText
        let startText = "2026-02-02T12:00:00.0000000+00:00"
        let expiryText = "2026-03-02T12:00:00.0000000+00:00"
        let startAt = instant startText
        let expiryAt = instant expiryText
        let successCall =
            $"subscription::start(business::seed(unit), {subscriptionIdExpr newIdText}, {customerIdExpr (Domain.CustomerId.toString customer)}, {productIdExpr (Domain.ProductId.toString product)}, \"  quarterly  \", {instantExpr startText}, {instantExpr expiryText})"
        let expectedStart =
            Domain.Subscription.start oracle.Store newId customer product "  quarterly  " startAt expiryAt
            |> Result.map fst
        let newKnown = addKnownSubscription newId oracle.Known
        compareStoreResult "subscription.start success" (eval engine successCall |> outputValue) expectedStart newKnown |> ignore

        let duplicateCall =
            $"subscription::start(business::seed(unit), {subscriptionIdExpr (Domain.SubscriptionId.toString baseSubscription)}, {customerIdExpr (Domain.CustomerId.toString customer)}, {productIdExpr (Domain.ProductId.toString product)}, \" \", {instantExpr startText}, {instantExpr startText})"
        let duplicateExpected = Domain.Subscription.start oracle.Store baseSubscription customer product " " startAt startAt |> Result.map fst
        compareStoreResult "subscription.start duplicate precedence" (eval engine duplicateCall |> outputValue) duplicateExpected oracle.Known |> ignore

        let invalidTermCall =
            $"subscription::start(business::seed(unit), {subscriptionIdExpr newIdText}, {customerIdExpr (Domain.CustomerId.toString customer)}, {productIdExpr (Domain.ProductId.toString product)}, \"  \", {instantExpr startText}, {instantExpr expiryText})"
        let invalidTermExpected = Domain.Subscription.start oracle.Store newId customer product "  " startAt expiryAt |> Result.map fst
        compareStoreResult "subscription.start term validation" (eval engine invalidTermCall |> outputValue) invalidTermExpected oracle.Known |> ignore

        let invalidExpiryCall =
            $"subscription::start(business::seed(unit), {subscriptionIdExpr newIdText}, {customerIdExpr (Domain.CustomerId.toString customer)}, {productIdExpr (Domain.ProductId.toString product)}, \"quarterly\", {instantExpr startText}, {instantExpr startText})"
        let invalidExpiryExpected = Domain.Subscription.start oracle.Store newId customer product "quarterly" startAt startAt |> Result.map fst
        compareStoreResult "subscription.start expiry equality" (eval engine invalidExpiryCall |> outputValue) invalidExpiryExpected oracle.Known |> ignore

        let missingCustomer = customerId "90000000-0000-0000-0000-000000000001"
        let missingProduct = productId "90000000-0000-0000-0000-000000000002"
        let bothMissingCall =
            $"subscription::start(business::seed(unit), {subscriptionIdExpr newIdText}, {customerIdExpr (Domain.CustomerId.toString missingCustomer)}, {productIdExpr (Domain.ProductId.toString missingProduct)}, \"quarterly\", {instantExpr startText}, {instantExpr expiryText})"
        let bothMissingExpected = Domain.Subscription.start oracle.Store newId missingCustomer missingProduct "quarterly" startAt expiryAt |> Result.map fst
        compareStoreResult "subscription.start customer-before-product lookup" (eval engine bothMissingCall |> outputValue) bothMissingExpected oracle.Known |> ignore

        let missingProductCall =
            $"subscription::start(business::seed(unit), {subscriptionIdExpr newIdText}, {customerIdExpr (Domain.CustomerId.toString customer)}, {productIdExpr (Domain.ProductId.toString missingProduct)}, \"quarterly\", {instantExpr startText}, {instantExpr expiryText})"
        let missingProductExpected = Domain.Subscription.start oracle.Store newId customer missingProduct "quarterly" startAt expiryAt |> Result.map fst
        compareStoreResult "subscription.start product lookup" (eval engine missingProductCall |> outputValue) missingProductExpected oracle.Known |> ignore

        let cancelAtText = "2026-01-02T12:00:00.0000000+00:00"
        let cancelAt = instant cancelAtText
        let cancelCall =
            $"subscription::cancel(business::seed(unit), {subscriptionIdExpr (Domain.SubscriptionId.toString baseSubscription)}, {instantExpr cancelAtText})"
        let cancelExpected = Domain.Subscription.cancel oracle.Store baseSubscription cancelAt |> Result.map fst
        let cancelledState = compareStoreResult "subscription.cancel equality boundary" (eval engine cancelCall |> outputValue) cancelExpected oracle.Known
        check cancelledState.IsSome "cancelling at exact start time succeeds"

        let missingSubscription = subscriptionId "90000000-0000-0000-0000-000000000003"
        let missingCancelCall = $"subscription::cancel(business::seed(unit), {subscriptionIdExpr (Domain.SubscriptionId.toString missingSubscription)}, {instantExpr cancelAtText})"
        let missingCancelExpected = Domain.Subscription.cancel oracle.Store missingSubscription cancelAt |> Result.map fst
        compareStoreResult "subscription.cancel missing lookup" (eval engine missingCancelCall |> outputValue) missingCancelExpected oracle.Known |> ignore

        let beforeStartText = "2026-01-02T11:59:59.9999999+00:00"
        let beforeStartCall =
            $"subscription::cancel(business::seed(unit), {subscriptionIdExpr (Domain.SubscriptionId.toString baseSubscription)}, {instantExpr beforeStartText})"
        let beforeStartExpected = Domain.Subscription.cancel oracle.Store baseSubscription (instant beforeStartText) |> Result.map fst
        compareStoreResult "subscription.cancel before-start rejection" (eval engine beforeStartCall |> outputValue) beforeStartExpected oracle.Known |> ignore

        let secondCancelText = "2026-01-03T12:00:00.0000000+00:00"
        let cancelledTwice =
            "word conformance.cancel-twice(seed: Store) -> Result<Store, BusinessError> {\n"
            + "  effects none\n"
            + $"  let first = subscription::cancel(seed, {subscriptionIdExpr (Domain.SubscriptionId.toString baseSubscription)}, {instantExpr cancelAtText});\n"
            + $"  match first {{ ok cancelled => {{ subscription::cancel(cancelled, {subscriptionIdExpr (Domain.SubscriptionId.toString baseSubscription)}, {instantExpr secondCancelText}) }} error problem => {{ result::error<Store, BusinessError>(problem) }} }}\n"
            + "}"
        defineTemporary engine "define temporary repeated-cancel probe" cancelledTwice |> ignore
        let secondCancelExpected =
            match Domain.Subscription.cancel oracle.Store baseSubscription cancelAt with
            | Error problem -> Error problem
            | Ok(cancelledStore, _) -> Domain.Subscription.cancel cancelledStore baseSubscription (instant secondCancelText) |> Result.map fst
        compareStoreResult "subscription.cancel already-cancelled precedence" (eval engine "conformance::cancel-twice(business::seed(unit))" |> outputValue) secondCancelExpected oracle.Known |> ignore

    let private invoiceLineExpr product description (quantity: int64) (unitPrice: int64) (lineTotal: int64) =
        $"invoiceLine::new(product-id = {productIdExpr product}, description = {flowString description}, quantity = {quantity.ToString(CultureInfo.InvariantCulture)}, unit-price = {moneyExpr unitPrice}, line-total = {moneyExpr lineTotal})"

    let private testInvoiceTransitions (engine: Runtime.Engine) =
        let oracle, _ = seedOracle ()
        let customer = oracle.Known.Customers.Head
        let product = oracle.Known.Products.Head
        let customerKey = Domain.CustomerId.toString customer
        let productKey = Domain.ProductId.toString product
        let productValue = Domain.Store.product product oracle.Store |> Option.get
        let productName = Domain.Product.name productValue
        let price = Domain.Money.minorUnits (Domain.Product.unitPrice productValue)
        let invoiceIdText = "40000000-0000-0000-0000-000000000001"
        let createdAtText = "2026-02-03T15:04:05.0000000+00:00"
        let createdAt = instant createdAtText

        let lineCall = $"invoice::line(business::seed(unit), {cartLineExpr productKey 2})"
        let lineExpected = Ok(expectedLine productName productKey 2 price)
        compareLine "invoice.line success" (eval engine lineCall |> outputValue) lineExpected

        let invalidQuantityCall = $"invoice::line(business::seed(unit), {cartLineExpr productKey 0})"
        let invalidQuantityExpected =
            Domain.Invoice.create oracle.Store (invoiceId invoiceIdText) customer [ product, 0 ] createdAt
            |> Result.map (fun (_, invoice) -> Domain.Invoice.lines invoice |> List.head |> fun line ->
                { ProductId = Domain.InvoiceLine.productId line
                  Description = Domain.InvoiceLine.description line
                  Quantity = Domain.InvoiceLine.quantity line
                  UnitPrice = Domain.InvoiceLine.unitPrice line
                  LineTotal = Domain.InvoiceLine.lineTotal line })
        compareLine "invoice.line zero quantity" (eval engine invalidQuantityCall |> outputValue) invalidQuantityExpected

        let missingProduct = productId "90000000-0000-0000-0000-000000000004"
        let missingProductKey = Domain.ProductId.toString missingProduct
        let invalidAndMissingCall = $"invoice::line(business::seed(unit), {cartLineExpr missingProductKey 0})"
        let invalidAndMissingExpected = Domain.Invoice.create oracle.Store (invoiceId invoiceIdText) customer [ missingProduct, 0 ] createdAt |> Result.map (fun _ -> Unchecked.defaultof<InvoiceLineRecord>)
        compareLine "invoice.line quantity-before-product precedence" (eval engine invalidAndMissingCall |> outputValue) invalidAndMissingExpected

        let missingProductCall = $"invoice::line(business::seed(unit), {cartLineExpr missingProductKey 1})"
        let missingProductExpected = Domain.Invoice.create oracle.Store (invoiceId invoiceIdText) customer [ missingProduct, 1 ] createdAt |> Result.map (fun _ -> Unchecked.defaultof<InvoiceLineRecord>)
        compareLine "invoice.line missing product" (eval engine missingProductCall |> outputValue) missingProductExpected

        let maximumQuantityCall = $"invoice::line(business::seed(unit), {cartLineExpr productKey (int64 Int32.MaxValue)})"
        let maximumQuantityExpected =
            Domain.Invoice.create oracle.Store (invoiceId invoiceIdText) customer [ product, Int32.MaxValue ] createdAt
            |> Result.map (fun (_, invoice) ->
                let line = Domain.Invoice.lines invoice |> List.head
                { ProductId = Domain.InvoiceLine.productId line
                  Description = Domain.InvoiceLine.description line
                  Quantity = Domain.InvoiceLine.quantity line
                  UnitPrice = Domain.InvoiceLine.unitPrice line
                  LineTotal = Domain.InvoiceLine.lineTotal line })
        compareLine "invoice.line accepts the maximum in-range Int32 quantity when multiplication fits" (eval engine maximumQuantityCall |> outputValue) maximumQuantityExpected

        let overflowProductKey = "20000000-0000-0000-0000-000000000013"
        let overflowProductId = productId overflowProductKey
        let maximumPrice = Domain.Money.ofMinorUnits Int64.MaxValue
        let overflowProduct = Domain.Product.create overflowProductId "Overflow plan" maximumPrice |> oracleOk "construct a valid maximum-price product"
        let overflowStore = Domain.Store.addProduct overflowProduct oracle.Store |> oracleOk "add maximum-price product for overflow oracle"
        let overflowLineExpected =
            Domain.Invoice.create overflowStore (invoiceId invoiceIdText) customer [ overflowProductId, 2 ] createdAt
            |> Result.map (fun (_, invoice) ->
                let line = Domain.Invoice.lines invoice |> List.head
                { ProductId = Domain.InvoiceLine.productId line
                  Description = Domain.InvoiceLine.description line
                  Quantity = Domain.InvoiceLine.quantity line
                  UnitPrice = Domain.InvoiceLine.unitPrice line
                  LineTotal = Domain.InvoiceLine.lineTotal line })
        let overflowProductSource =
            "word conformance.overflow-price-line(seed: Store) -> Result<InvoiceLine, BusinessError> {\n"
            + "  effects none\n"
            + $"  let product = product::new(id = {productIdExpr overflowProductKey}, name = \"Overflow plan\", unit-price = {moneyExpr Int64.MaxValue});\n"
            + "  let augmented = store::new(customers = seed.customers(), products = list::append(seed.products(), product), subscriptions = seed.subscriptions(), invoices = seed.invoices(), payments = seed.payments(), email-outbox = seed.email-outbox(), sent-emails = seed.sent-emails());\n"
            + $"  invoice::line(augmented, {cartLineExpr overflowProductKey 2L})\n"
            + "}"
        defineTemporary engine "define maximum-price multiplication overflow probe" overflowProductSource |> ignore
        compareLine
            "invoice.line checked multiplication overflow"
            (eval engine "conformance::overflow-price-line(business::seed(unit))" |> outputValue)
            overflowLineExpected

        let outOfRangeLine = eval engine $"invoice::line(business::seed(unit), {cartLineExpr productKey (int64 Int32.MaxValue + 1L)})" |> outputValue
        expectResultError "invoice.line signed Int32 boundary" outOfRangeLine "INVALID_QUANTITY"

        let emptyTotal = eval engine "invoice::sum-lines(list::empty<InvoiceLine>())" |> outputValue
        compareMoneyResult "invoice.sum-lines empty seed" emptyTotal (Domain.Money.sum [])

        let smallLine1 = invoiceLineExpr productKey productName 1 price price
        let smallLine2 = invoiceLineExpr productKey productName 2 price (price * 2L)
        let totals = eval engine $"invoice::sum-lines(list::append(list::singleton<InvoiceLine>({smallLine1}), {smallLine2}))" |> outputValue
        compareMoneyResult "invoice.sum-lines ordered nonempty lines" totals (Domain.Money.sum [ Domain.Money.ofMinorUnits price; Domain.Money.ofMinorUnits (price * 2L) ])

        let maxLine = invoiceLineExpr productKey productName 1 Int64.MaxValue Int64.MaxValue
        let overflowLine = invoiceLineExpr productKey productName 1 1L 1L
        let overflowTotal = eval engine $"invoice::sum-lines(list::append(list::singleton<InvoiceLine>({maxLine}), {overflowLine}))" |> outputValue
        compareMoneyResult "invoice.sum-lines checked overflow" overflowTotal (Domain.Money.sum [ Domain.Money.ofMinorUnits Int64.MaxValue; Domain.Money.ofMinorUnits 1L ])

        let createCall =
            $"invoice::create(business::seed(unit), {invoiceIdExpr invoiceIdText}, {customerIdExpr customerKey}, list::append(list::singleton<CartLine>({cartLineExpr productKey 1}), {cartLineExpr productKey 2}), {instantExpr createdAtText})"
        let createExpected = Domain.Invoice.create oracle.Store (invoiceId invoiceIdText) customer [ product, 1; product, 2 ] createdAt |> Result.map fst
        let createdKnown = addKnownInvoice (invoiceId invoiceIdText) oracle.Known
        let createdState = compareStoreResult "invoice.create preserves cart line order" (eval engine createCall |> outputValue) createExpected createdKnown
        check createdState.IsSome "invoice creation succeeds"

        let duplicateStoreExpected = createExpected
        let duplicateCall =
            $"invoice::create(store::with-invoices(business::seed(unit), list::singleton<Invoice>(invoice::new(id = {invoiceIdExpr invoiceIdText}, customer-id = {customerIdExpr customerKey}, lines = list::empty<InvoiceLine>(), total = {moneyExpr 0L}, created-at = {instantExpr createdAtText}, status = InvoiceStatus::new(\"open\")))), {invoiceIdExpr invoiceIdText}, {customerIdExpr customerKey}, list::empty<CartLine>(), {instantExpr createdAtText})"
        let duplicateOracle =
            match duplicateStoreExpected with
            | Error problem -> Error problem
            | Ok populated ->
                Domain.Invoice.create populated (invoiceId invoiceIdText) customer [] createdAt |> Result.map fst
        let duplicateKnown =
            match duplicateStoreExpected with
            | Ok _ -> createdKnown
            | Error _ -> oracle.Known
        compareStoreResult "invoice.create duplicate precedes empty-cart validation" (eval engine duplicateCall |> outputValue) duplicateOracle duplicateKnown |> ignore

        let missingCustomer = customerId "90000000-0000-0000-0000-000000000005"
        let missingCustomerInvoiceKey = "40000000-0000-0000-0000-000000000002"
        let missingCustomerCall =
            $"invoice::create(business::seed(unit), {invoiceIdExpr missingCustomerInvoiceKey}, {customerIdExpr (Domain.CustomerId.toString missingCustomer)}, list::empty<CartLine>(), {instantExpr createdAtText})"
        let missingCustomerExpected = Domain.Invoice.create oracle.Store (invoiceId missingCustomerInvoiceKey) missingCustomer [] createdAt |> Result.map fst
        compareStoreResult "invoice.create customer-before-empty-cart" (eval engine missingCustomerCall |> outputValue) missingCustomerExpected oracle.Known |> ignore

        let emptyCartInvoiceKey = "40000000-0000-0000-0000-000000000003"
        let emptyCartCall =
            $"invoice::create(business::seed(unit), {invoiceIdExpr emptyCartInvoiceKey}, {customerIdExpr customerKey}, list::empty<CartLine>(), {instantExpr createdAtText})"
        let emptyCartExpected = Domain.Invoice.create oracle.Store (invoiceId emptyCartInvoiceKey) customer [] createdAt |> Result.map fst
        compareStoreResult "invoice.create rejects empty cart" (eval engine emptyCartCall |> outputValue) emptyCartExpected oracle.Known |> ignore

        let laterLineFailureInvoiceKey = "40000000-0000-0000-0000-000000000004"
        let largeProduct1Key = "20000000-0000-0000-0000-000000000011"
        let largeProduct2Key = "20000000-0000-0000-0000-000000000012"
        let largeProduct1 = Domain.Product.create (productId largeProduct1Key) "Large one" (Domain.Money.ofMinorUnits Int64.MaxValue) |> oracleOk "construct first maximum-price product"
        let largeProduct2 = Domain.Product.create (productId largeProduct2Key) "Large two" (Domain.Money.ofMinorUnits Int64.MaxValue) |> oracleOk "construct second maximum-price product"
        let oracleWithLargeProducts =
            Domain.Store.addProduct largeProduct1 oracle.Store
            |> Result.bind (Domain.Store.addProduct largeProduct2)
            |> oracleOk "add individually valid maximum-price products"
        let overflowWouldOccur = Domain.Money.sum [ Domain.Money.ofMinorUnits Int64.MaxValue; Domain.Money.ofMinorUnits Int64.MaxValue ]
        check (match overflowWouldOccur with | Error _ -> true | Ok _ -> false) "the first two individually valid line totals overflow when summed"

        let lateLineProbe =
            "word conformance.late-line-error(store: Store) -> Result<Store, BusinessError> {\n"
            + "  effects none\n"
            + $"  let first = product::new(id = {productIdExpr largeProduct1Key}, name = \"Large one\", unit-price = {moneyExpr Int64.MaxValue});\n"
            + $"  let second = product::new(id = {productIdExpr largeProduct2Key}, name = \"Large two\", unit-price = {moneyExpr Int64.MaxValue});\n"
            + "  let augmented = store::new(customers = store.customers(), products = list::append(list::append(store.products(), first), second), subscriptions = store.subscriptions(), invoices = store.invoices(), payments = store.payments(), email-outbox = store.email-outbox(), sent-emails = store.sent-emails());\n"
            + $"  invoice::create(augmented, {invoiceIdExpr laterLineFailureInvoiceKey}, {customerIdExpr customerKey}, list::append(list::append(list::singleton<CartLine>({cartLineExpr largeProduct1Key 1L}), {cartLineExpr largeProduct2Key 1L}), {cartLineExpr missingProductKey 1L}), {instantExpr createdAtText})\n"
            + "}"
        defineTemporary engine "define later-line error precedence probe" lateLineProbe |> ignore
        let lineFailureExpected =
            Domain.Invoice.create oracleWithLargeProducts (invoiceId laterLineFailureInvoiceKey) customer [ productId largeProduct1Key, 1; productId largeProduct2Key, 1; missingProduct, 1 ] createdAt
            |> Result.map fst
        compareStoreResult
            "invoice.create reports a later line error before the overflowing line-total sum"
            (eval engine $"conformance::late-line-error(business::seed(unit))" |> outputValue)
            lineFailureExpected
            oracle.Known
        |> ignore

        let invalidProductSource =
            "word conformance.negative-product-line(seed: Store) -> Result<InvoiceLine, BusinessError> {\n"
            + "  effects none\n"
            + $"  let invalid-product = product::new(id = {productIdExpr productKey}, name = \"Invalid price\", unit-price = {moneyExpr -1L});\n"
            + "  let invalid-store = store::new(customers = seed.customers(), products = list::singleton<Product>(invalid-product), subscriptions = seed.subscriptions(), invoices = seed.invoices(), payments = seed.payments(), email-outbox = seed.email-outbox(), sent-emails = seed.sent-emails());\n"
            + $"  invoice::line(invalid-store, {cartLineExpr productKey 1})\n"
            + "}"
        defineTemporary engine "define public negative-product boundary probe" invalidProductSource |> ignore
        let negativePrice = eval engine "conformance::negative-product-line(business::seed(unit))" |> outputValue
        expectResultError "invoice.line rejects public negative-price Product" negativePrice "NEGATIVE_PRODUCT_PRICE"

    let private openInvoiceCode seedExpression invoiceKey quantity createdAtText =
        let productKey = "20000000-0000-0000-0000-000000000001"
        let customerKey = "10000000-0000-0000-0000-000000000001"
        let unitPrice = 199L
        let lineTotal = unitPrice * int64 quantity
        let line = invoiceLineExpr productKey "Monthly plan" quantity unitPrice lineTotal
        let value =
            $"invoice::new(id = {invoiceIdExpr invoiceKey}, customer-id = {customerIdExpr customerKey}, lines = list::singleton<InvoiceLine>({line}), total = {moneyExpr lineTotal}, created-at = {instantExpr createdAtText}, status = InvoiceStatus::new(\"open\"))"
        $"store::with-invoices({seedExpression}, list::singleton<Invoice>({value}))"

    let private paymentStoreOracle (state: OracleState) invoiceKey quantity createdAt =
        let invoiceKeyValue = invoiceId invoiceKey
        let customer = state.Known.Customers.Head
        let product = state.Known.Products.Head
        let created =
            Domain.Invoice.create state.Store invoiceKeyValue customer [ product, quantity ] createdAt
            |> Result.map fst
        let known = addKnownInvoice invoiceKeyValue state.Known
        created, known

    let private paymentOracle store paymentKey invoiceKey amount requestedAt outcome =
        let provider: Domain.PaymentProvider = { Charge = fun _ -> outcome }
        Domain.Payment.submit store provider paymentKey invoiceKey amount requestedAt |> Result.map fst

    let private paidInvoiceStoreCode seedExpression invoiceKey paymentKey paymentAmount paidAtText =
        let productKey = "20000000-0000-0000-0000-000000000001"
        let customerKey = "10000000-0000-0000-0000-000000000001"
        let paymentReference = "receipt-010"
        let invoiceCreatedAtText = "2026-02-04T12:00:00.0000000+00:00"
        let line = invoiceLineExpr productKey "Monthly plan" 2L 199L 398L
        let invoice =
            $"invoice::new(id = {invoiceIdExpr invoiceKey}, customer-id = {customerIdExpr customerKey}, lines = list::singleton<InvoiceLine>({line}), total = {moneyExpr 398L}, created-at = {instantExpr invoiceCreatedAtText}, status = InvoiceStatus::new(\"paid\"))"
        let payment =
            $"payment::new(id = {paymentIdExpr paymentKey}, invoice-id = {invoiceIdExpr invoiceKey}, amount = {moneyExpr paymentAmount}, provider-reference = {flowString paymentReference}, paid-at = {instantExpr paidAtText})"
        $"store::with-payments(store::with-invoices({seedExpression}, list::singleton<Invoice>({invoice})), list::singleton<Payment>({payment}))"

    let private testPayments (engine: Runtime.Engine) =
        let baseline, _ = seedOracle ()
        let invoiceKey = "40000000-0000-0000-0000-000000000010"
        let invoiceAtText = "2026-02-04T12:00:00.0000000+00:00"
        let invoiceAt = instant invoiceAtText
        let openStoreCode = openInvoiceCode "business::seed(unit)" invoiceKey 2 invoiceAtText
        let openStoreResult, knownWithInvoice = paymentStoreOracle baseline invoiceKey 2 invoiceAt
        let openStore =
            match openStoreResult with
            | Ok store -> { Store = store; Known = knownWithInvoice }
            | Error problem -> failwith $"oracle open invoice: {Domain.DomainError.code problem}"
        let paymentKeyText = "50000000-0000-0000-0000-000000000010"
        let paymentKey = paymentId paymentKeyText
        let invoiceKeyValue = invoiceId invoiceKey
        let amount = Domain.Money.ofMinorUnits 398L
        let requestedAtText = "2026-02-04T13:00:00.0000000+00:00"
        let requestedAt = instant requestedAtText
        let validReceipt = Domain.PaymentReceipt.create " receipt-010 " amount |> oracleOk "reference payment receipt"
        let accepted = Ok validReceipt
        let duplicatePaymentKeyText = "50000000-0000-0000-0000-000000000011"
        let mismatchedReceiptPaymentKeyText = "50000000-0000-0000-0000-000000000012"
        let negativePaymentKeyText = "50000000-0000-0000-0000-000000000013"
        let missingInvoicePaymentKeyText = "50000000-0000-0000-0000-000000000014"
        let mismatchPaymentKeyText = "50000000-0000-0000-0000-000000000015"
        let alreadyPaidPaymentKeyText = "50000000-0000-0000-0000-000000000016"
        let blankReceiptPaymentKeyText = "50000000-0000-0000-0000-000000000017"
        let missingInvoiceKeyText = "90000000-0000-0000-0000-000000000006"
        let successExpression =
            $"payment::apply-result({openStoreCode}, {paymentIdExpr paymentKeyText}, {invoiceIdExpr invoiceKey}, {moneyExpr 398L}, {instantExpr requestedAtText}, result::ok<PaymentReceipt, BusinessError>(paymentReceipt::new(reference = \" receipt-010 \", amount = {moneyExpr 398L})))"
        let successExpected = paymentOracle openStore.Store paymentKey invoiceKeyValue amount requestedAt accepted
        let acceptedStore =
            match successExpected with
            | Ok store -> store
            | Error problem -> failwith $"oracle payment success: {Domain.DomainError.code problem}"
        let acceptedInputCode =
            paidInvoiceStoreCode "business::seed(unit)" invoiceKey paymentKeyText 398L requestedAtText
        let knownWithPayment = addKnownPayment paymentKey openStore.Known
        let paid = compareStoreResult "payment.apply-result accepted receipt" (eval engine successExpression |> outputValue) successExpected knownWithPayment
        check paid.IsSome "accepted payment commits invoice and payment together"

        let providerFailureExpression =
            $"payment::apply-result({openStoreCode}, {paymentIdExpr duplicatePaymentKeyText}, {invoiceIdExpr invoiceKey}, {moneyExpr 398L}, {instantExpr requestedAtText}, result::error<PaymentReceipt, BusinessError>(businessError::new(code = \"DECLINED\", message = \"processor offline\")))"
        let providerFailureExpected = paymentOracle openStore.Store (paymentId duplicatePaymentKeyText) invoiceKeyValue amount requestedAt (Error "processor offline")
        compareStoreResult "payment provider error leaves Store unchanged" (eval engine providerFailureExpression |> outputValue) providerFailureExpected openStore.Known |> ignore

        let badReceiptAmount = Domain.Money.ofMinorUnits 397L
        let invalidReceipt = Domain.PaymentReceipt.create "provider-010" badReceiptAmount |> oracleOk "mismatched amount receipt"
        let mismatchedReceiptExpression =
            $"payment::apply-result({openStoreCode}, {paymentIdExpr mismatchedReceiptPaymentKeyText}, {invoiceIdExpr invoiceKey}, {moneyExpr 398L}, {instantExpr requestedAtText}, result::ok<PaymentReceipt, BusinessError>(paymentReceipt::new(reference = \"provider-010\", amount = {moneyExpr 397L})))"
        let mismatchedReceiptExpected =
            paymentOracle openStore.Store (paymentId mismatchedReceiptPaymentKeyText) invoiceKeyValue amount requestedAt (Ok invalidReceipt)
        compareStoreResult "payment receipt amount must match request" (eval engine mismatchedReceiptExpression |> outputValue) mismatchedReceiptExpected openStore.Known |> ignore

        let duplicateCall =
            $"payment::apply-result({acceptedInputCode}, {paymentIdExpr paymentKeyText}, {invoiceIdExpr invoiceKey}, {moneyExpr -1L}, {instantExpr requestedAtText}, result::error<PaymentReceipt, BusinessError>(businessError::new(code = \"DECLINED\", message = \"unused\")))"
        let duplicateExpected = paymentOracle acceptedStore paymentKey invoiceKeyValue (Domain.Money.ofMinorUnits -1L) requestedAt (Error "unused")
        compareStoreResult "payment duplicate ID precedes later validation" (eval engine duplicateCall |> outputValue) duplicateExpected knownWithPayment |> ignore

        let negativeMissingInvoice =
            $"payment::apply-result({openStoreCode}, {paymentIdExpr negativePaymentKeyText}, {invoiceIdExpr missingInvoiceKeyText}, {moneyExpr -1L}, {instantExpr requestedAtText}, result::ok<PaymentReceipt, BusinessError>(paymentReceipt::new(reference = \"ref\", amount = {moneyExpr -1L})))"
        let negativeExpected = paymentOracle openStore.Store (paymentId negativePaymentKeyText) (invoiceId missingInvoiceKeyText) (Domain.Money.ofMinorUnits -1L) requestedAt (Ok(Domain.PaymentReceipt.create "ref" (Domain.Money.ofMinorUnits -1L) |> oracleOk "negative receipt"))
        compareStoreResult "payment amount positivity precedes invoice lookup" (eval engine negativeMissingInvoice |> outputValue) negativeExpected openStore.Known |> ignore

        let missingInvoice =
            $"payment::apply-result({openStoreCode}, {paymentIdExpr missingInvoicePaymentKeyText}, {invoiceIdExpr missingInvoiceKeyText}, {moneyExpr 1L}, {instantExpr requestedAtText}, result::ok<PaymentReceipt, BusinessError>(paymentReceipt::new(reference = \"ref\", amount = {moneyExpr 1L})))"
        let missingInvoiceExpected = paymentOracle openStore.Store (paymentId missingInvoicePaymentKeyText) (invoiceId missingInvoiceKeyText) (Domain.Money.ofMinorUnits 1L) requestedAt (Ok(Domain.PaymentReceipt.create "ref" (Domain.Money.ofMinorUnits 1L) |> oracleOk "missing-invoice receipt"))
        compareStoreResult "payment missing invoice" (eval engine missingInvoice |> outputValue) missingInvoiceExpected openStore.Known |> ignore

        let mismatch =
            $"payment::apply-result({openStoreCode}, {paymentIdExpr mismatchPaymentKeyText}, {invoiceIdExpr invoiceKey}, {moneyExpr 397L}, {instantExpr requestedAtText}, result::ok<PaymentReceipt, BusinessError>(paymentReceipt::new(reference = \"ref\", amount = {moneyExpr 397L})))"
        let mismatchExpected = paymentOracle openStore.Store (paymentId mismatchPaymentKeyText) invoiceKeyValue (Domain.Money.ofMinorUnits 397L) requestedAt (Ok(Domain.PaymentReceipt.create "ref" (Domain.Money.ofMinorUnits 397L) |> oracleOk "mismatched-request receipt"))
        compareStoreResult "payment amount must match full invoice balance" (eval engine mismatch |> outputValue) mismatchExpected openStore.Known |> ignore

        let alreadyPaid =
            $"payment::apply-result({acceptedInputCode}, {paymentIdExpr alreadyPaidPaymentKeyText}, {invoiceIdExpr invoiceKey}, {moneyExpr 397L}, {instantExpr requestedAtText}, result::ok<PaymentReceipt, BusinessError>(paymentReceipt::new(reference = \"ref\", amount = {moneyExpr 397L})))"
        let alreadyPaidOracle = paymentOracle acceptedStore (paymentId alreadyPaidPaymentKeyText) invoiceKeyValue (Domain.Money.ofMinorUnits 397L) requestedAt (Ok(Domain.PaymentReceipt.create "ref" (Domain.Money.ofMinorUnits 397L) |> oracleOk "already-paid receipt"))
        compareStoreResult "payment already-paid check precedes amount mismatch" (eval engine alreadyPaid |> outputValue) alreadyPaidOracle knownWithPayment |> ignore

        let blankReceiptExpression =
            $"payment::apply-result({openStoreCode}, {paymentIdExpr blankReceiptPaymentKeyText}, {invoiceIdExpr invoiceKey}, {moneyExpr 398L}, {instantExpr requestedAtText}, result::ok<PaymentReceipt, BusinessError>(paymentReceipt::new(reference = \"   \", amount = {moneyExpr 398L})))"
        let blankReceipt = eval engine blankReceiptExpression |> outputValue
        expectResultError "payment boundary revalidates a publicly constructible blank receipt" blankReceipt "INVALID_PROVIDER_RECEIPT"

    let private queuedStoreCode seedExpression firstSubject secondSubject =
        let first =
            $"emailMessage::new(to = Email::new(\"ada@example.test\"), subject = {flowString firstSubject}, body = \"first body\")"
        let second =
            $"emailMessage::new(to = Email::new(\"ada@example.test\"), subject = {flowString secondSubject}, body = \"second body\")"
        $"store::with-email-state({seedExpression}, list::append(list::singleton<EmailMessage>({first}), {second}), list::empty<EmailMessage>())"

    let private testEmailTransitions (engine: Runtime.Engine) =
        let oracle, _ = seedOracle ()
        let customer = oracle.Known.Customers.Head
        let customerKey = Domain.CustomerId.toString customer
        let queueCall = $"email::queue(business::seed(unit), {customerIdExpr customerKey}, \"  Welcome  \", \"  Hello Ada  \")"
        let queueExpected = Domain.EmailOutbox.queue oracle.Store customer "  Welcome  " "  Hello Ada  " |> Result.map fst
        let queued = compareStoreResult "email.queue trims and appends a message" (eval engine queueCall |> outputValue) queueExpected oracle.Known
        check queued.IsSome "email queue succeeds"

        let missingCustomer = customerId "90000000-0000-0000-0000-000000000007"
        let missingCall = $"email::queue(business::seed(unit), {customerIdExpr (Domain.CustomerId.toString missingCustomer)}, \"Subject\", \"Body\")"
        let missingExpected = Domain.EmailOutbox.queue oracle.Store missingCustomer "Subject" "Body" |> Result.map fst
        compareStoreResult "email.queue unknown customer" (eval engine missingCall |> outputValue) missingExpected oracle.Known |> ignore

        let blankCall = $"email::queue(business::seed(unit), {customerIdExpr customerKey}, \"  \", \"Body\")"
        let blankExpected = Domain.EmailOutbox.queue oracle.Store customer "  " "Body" |> Result.map fst
        compareStoreResult "email.queue rejects blank subject" (eval engine blankCall |> outputValue) blankExpected oracle.Known |> ignore

        let noPendingFailureCall =
            "email::apply-delivery-result(business::seed(unit), result::error<Unit, BusinessError>(businessError::new(code = \"TEMP\", message = \"offline\")))"
        let noPendingExpected =
            Domain.EmailOutbox.deliverNext oracle.Store { Send = fun _ -> Error "offline" }
        compareStoreResult "email delivery checks empty FIFO before provider result" (eval engine noPendingFailureCall |> outputValue) noPendingExpected oracle.Known |> ignore

        let queuedCode = queuedStoreCode "business::seed(unit)" "First" "Second"
        let queuedOracleResult =
            Domain.EmailOutbox.queue oracle.Store customer "First" "first body"
            |> Result.bind (fun (firstStore, _) -> Domain.EmailOutbox.queue firstStore customer "Second" "second body" |> Result.map fst)
        let queuedOracle =
            match queuedOracleResult with
            | Ok state -> { oracle with Store = state }
            | Error problem -> failwith $"oracle queued emails: {Domain.DomainError.code problem}"
        let queuedProjection = projectLangFromExpression engine queuedCode
        equal (projectOracle queuedOracle) queuedProjection "seeded two-message FIFO state matches sequential F# queueing"

        let deliveryFailure =
            $"email::apply-delivery-result({queuedCode}, result::error<Unit, BusinessError>(businessError::new(code = \"SMTP\", message = \"temporary mail outage\")))"
        let deliveryFailureExpected =
            Domain.EmailOutbox.deliverNext queuedOracle.Store { Send = fun _ -> Error "temporary mail outage" }
        compareStoreResult "email delivery error keeps head queued" (eval engine deliveryFailure |> outputValue) deliveryFailureExpected queuedOracle.Known |> ignore
        equal (projectOracle queuedOracle) (projectLangFromExpression engine queuedCode) "failed delivery leaves immutable input state and FIFO order unchanged"

        let deliverySuccess =
            $"email::apply-delivery-result({queuedCode}, result::ok<Unit, BusinessError>(unit))"
        let deliverySuccessExpected =
            Domain.EmailOutbox.deliverNext queuedOracle.Store { Send = fun _ -> Ok() }
        let firstDelivery = compareStoreResult "email delivery moves only the FIFO head" (eval engine deliverySuccess |> outputValue) deliverySuccessExpected queuedOracle.Known
        check firstDelivery.IsSome "email first delivery succeeds"
        let firstEmail =
            "emailMessage::new(to = Email::new(\"ada@example.test\"), subject = \"First\", body = \"first body\")"
        let secondEmail =
            "emailMessage::new(to = Email::new(\"ada@example.test\"), subject = \"Second\", body = \"second body\")"
        let afterFirstDeliveryCode =
            $"store::with-email-state(business::seed(unit), list::singleton<EmailMessage>({secondEmail}), list::singleton<EmailMessage>({firstEmail}))"
        let afterFirstDeliveryExpected =
            match deliverySuccessExpected with
            | Error problem -> Error problem
            | Ok firstStore -> Domain.EmailOutbox.deliverNext firstStore { Send = fun _ -> Ok() }
        let afterFirstDeliveryKnown = queuedOracle.Known
        compareStoreResult
            "email second delivery preserves FIFO order"
            (eval engine $"email::apply-delivery-result({afterFirstDeliveryCode}, result::ok<Unit, BusinessError>(unit))" |> outputValue)
            afterFirstDeliveryExpected
            afterFirstDeliveryKnown
        |> ignore

    let private transitionProbeSource =
        """
word conformance.preloaded-seed() -> Store {
    effects none
    let initial = business::seed(unit);
    let pending = emailMessage::new(to = Email::new("ada@example.test"), subject = "Preloaded pending", body = "pending body");
    let sent = emailMessage::new(to = Email::new("ada@example.test"), subject = "Preloaded sent", body = "sent body");
    store::with-email-state(initial, list::singleton<EmailMessage>(pending), list::singleton<EmailMessage>(sent))
}

word conformance.payment-email-flow(seed: Store) -> Result<Store, BusinessError> {
    effects none
    match subscription::start(
        seed,
        SubscriptionId::new("30000000-0000-0000-0000-000000000020"),
        CustomerId::new("10000000-0000-0000-0000-000000000001"),
        ProductId::new("20000000-0000-0000-0000-000000000001"),
        "weekly",
        Instant::new("2026-02-05T12:00:00.0000000+00:00"),
        Instant::new("2026-03-05T12:00:00.0000000+00:00")) {
        ok started => {
            match subscription::cancel(
                started,
                SubscriptionId::new("30000000-0000-0000-0000-000000000020"),
                Instant::new("2026-02-06T12:00:00.0000000+00:00")) {
                ok cancelled => {
                    match invoice::create(
                        cancelled,
                        InvoiceId::new("40000000-0000-0000-0000-000000000020"),
                        CustomerId::new("10000000-0000-0000-0000-000000000001"),
                        list::singleton<CartLine>(cartLine::new(product-id = ProductId::new("20000000-0000-0000-0000-000000000001"), quantity = 2)),
                        Instant::new("2026-02-07T12:00:00.0000000+00:00")) {
                        ok first-invoice => {
                            match invoice::create(
                                first-invoice,
                                InvoiceId::new("40000000-0000-0000-0000-000000000021"),
                                CustomerId::new("10000000-0000-0000-0000-000000000001"),
                                list::singleton<CartLine>(cartLine::new(product-id = ProductId::new("20000000-0000-0000-0000-000000000001"), quantity = 1)),
                                Instant::new("2026-02-08T12:00:00.0000000+00:00")) {
                                ok second-invoice => {
                                    match payment::apply-result(
                                        second-invoice,
                                        PaymentId::new("50000000-0000-0000-0000-000000000020"),
                                        InvoiceId::new("40000000-0000-0000-0000-000000000020"),
                                        Money::new(398),
                                        Instant::new("2026-02-09T12:00:00.0000000+00:00"),
                                        result::ok<PaymentReceipt, BusinessError>(paymentReceipt::new(reference = "provider-020", amount = Money::new(398)))) {
                                        ok first-paid => {
                                            match payment::apply-result(
                                                first-paid,
                                                PaymentId::new("50000000-0000-0000-0000-000000000021"),
                                                InvoiceId::new("40000000-0000-0000-0000-000000000021"),
                                                Money::new(199),
                                                Instant::new("2026-02-10T12:00:00.0000000+00:00"),
                                                result::ok<PaymentReceipt, BusinessError>(paymentReceipt::new(reference = "provider-021", amount = Money::new(199)))) {
                                                ok second-paid => {
                                                    match email::queue(second-paid, CustomerId::new("10000000-0000-0000-0000-000000000001"), "Invoice 020", "First notice") {
                                                        ok first-queued => { email::queue(first-queued, CustomerId::new("10000000-0000-0000-0000-000000000001"), "Invoice 021", "Second notice") }
                                                        error problem => { result::error<Store, BusinessError>(problem) }
                                                    }
                                                }
                                                error problem => { result::error<Store, BusinessError>(problem) }
                                            }
                                        }
                                        error problem => { result::error<Store, BusinessError>(problem) }
                                    }
                                }
                                error problem => { result::error<Store, BusinessError>(problem) }
                            }
                        }
                        error problem => { result::error<Store, BusinessError>(problem) }
                    }
                }
                error problem => { result::error<Store, BusinessError>(problem) }
            }
        }
        error problem => { result::error<Store, BusinessError>(problem) }
    }
}

word conformance.delivery-failure(seed: Store) -> Result<Store, BusinessError> {
    effects none
    match conformance::payment-email-flow(seed) {
        ok queued => { email::apply-delivery-result(queued, result::error<Unit, BusinessError>(businessError::new(code = "MAIL_DOWN", message = "temporary mail outage"))) }
        error problem => { result::error<Store, BusinessError>(problem) }
    }
}

word conformance.first-delivery-success(seed: Store) -> Result<Store, BusinessError> {
    effects none
    match conformance::payment-email-flow(seed) {
        ok queued => { email::apply-delivery-result(queued, result::ok<Unit, BusinessError>(unit)) }
        error problem => { result::error<Store, BusinessError>(problem) }
    }
}

word conformance.second-delivery-success(seed: Store) -> Result<Store, BusinessError> {
    effects none
    match conformance::first-delivery-success(seed) {
        ok first-sent => { email::apply-delivery-result(first-sent, result::ok<Unit, BusinessError>(unit)) }
        error problem => { result::error<Store, BusinessError>(problem) }
    }
}

word conformance.failed-delivery-input(seed: Store) -> Store {
    effects none
    match conformance::payment-email-flow(seed) {
        ok queued => {
            let ignored-result = email::apply-delivery-result(queued, result::error<Unit, BusinessError>(businessError::new(code = "MAIL_DOWN", message = "temporary mail outage")));
            queued
        }
        error problem => { seed }
    }
}
"""

    let private defineTransitionProbes (engine: Runtime.Engine) =
        defineTemporary engine "define temporary transition and state probes" transitionProbeSource

    let private testEndToEndState (engine: Runtime.Engine) =
        defineTransitionProbes engine |> ignore
        let oracle, _ = seedOracle ()
        let customer = oracle.Known.Customers.Head
        let product = oracle.Known.Products.Head
        let subId = subscriptionId "30000000-0000-0000-0000-000000000020"
        let firstInvoiceId = invoiceId "40000000-0000-0000-0000-000000000020"
        let secondInvoiceId = invoiceId "40000000-0000-0000-0000-000000000021"
        let firstPaymentId = paymentId "50000000-0000-0000-0000-000000000020"
        let secondPaymentId = paymentId "50000000-0000-0000-0000-000000000021"
        let oracleStore = oracle.Store
        let queueToSend = Domain.EmailOutbox.queue oracleStore customer "Preloaded sent" "sent body" |> oracleOk "queue seed sent message"
        let oracleStore, _ = queueToSend
        let deliverSeed =
            Domain.EmailOutbox.deliverNext oracleStore Domain.EmailProvider.deterministic
            |> oracleOk "pre-deliver seed message"
        let oracleStore, _ = Domain.EmailOutbox.queue deliverSeed customer "Preloaded pending" "pending body" |> oracleOk "queue seed pending message"
        let oracleStateWithMail = { oracle with Store = oracleStore }
        let initialLanguage = projectLangFromExpression engine "conformance::preloaded-seed()"
        equal (projectOracle oracleStateWithMail) initialLanguage "preloaded seed has both nonempty email lists and exact F# projection"

        let startStore, _ =
            Domain.Subscription.start
                oracleStateWithMail.Store
                subId
                customer
                product
                "weekly"
                (instant "2026-02-05T12:00:00.0000000+00:00")
                (instant "2026-03-05T12:00:00.0000000+00:00")
            |> oracleOk "start sequence subscription"
        let startKnown = addKnownSubscription subId oracleStateWithMail.Known
        let cancelStore, _ =
            Domain.Subscription.cancel startStore subId (instant "2026-02-06T12:00:00.0000000+00:00")
            |> oracleOk "cancel sequence subscription"
        let firstInvoiceStore, _ =
            Domain.Invoice.create cancelStore firstInvoiceId customer [ product, 2 ] (instant "2026-02-07T12:00:00.0000000+00:00")
            |> oracleOk "create first sequence invoice"
        let secondInvoiceStore, _ =
            Domain.Invoice.create firstInvoiceStore secondInvoiceId customer [ product, 1 ] (instant "2026-02-08T12:00:00.0000000+00:00")
            |> oracleOk "create second sequence invoice"
        let firstReceipt = Domain.PaymentReceipt.create "provider-020" (Domain.Money.ofMinorUnits 398L) |> oracleOk "first provider receipt"
        let firstPaidStore, _ =
            Domain.Payment.submit secondInvoiceStore (Domain.PaymentProvider.deterministic "provider-020") firstPaymentId firstInvoiceId (Domain.Money.ofMinorUnits 398L) (instant "2026-02-09T12:00:00.0000000+00:00")
            |> oracleOk "pay first sequence invoice"
        let secondPaidStore, _ =
            Domain.Payment.submit firstPaidStore (Domain.PaymentProvider.deterministic "provider-021") secondPaymentId secondInvoiceId (Domain.Money.ofMinorUnits 199L) (instant "2026-02-10T12:00:00.0000000+00:00")
            |> oracleOk "pay second sequence invoice"
        let firstQueuedStore, _ = Domain.EmailOutbox.queue secondPaidStore customer "Invoice 020" "First notice" |> oracleOk "queue first sequence notification"
        let queuedStore, _ = Domain.EmailOutbox.queue firstQueuedStore customer "Invoice 021" "Second notice" |> oracleOk "queue second sequence notification"
        let known =
            oracleStateWithMail.Known
            |> addKnownSubscription subId
            |> addKnownInvoice firstInvoiceId
            |> addKnownInvoice secondInvoiceId
            |> addKnownPayment firstPaymentId
            |> addKnownPayment secondPaymentId
        let sequenceExpected = Ok queuedStore
        let built = eval engine "conformance::payment-email-flow(conformance::preloaded-seed())" |> outputValue
        let flowState = compareStoreResult "end-to-end subscription, invoice, payment, and email queue" built sequenceExpected known
        check flowState.IsSome "all staged end-to-end transitions succeed"

        let failed = eval engine "conformance::delivery-failure(conformance::preloaded-seed())" |> outputValue
        let failedExpected =
            Domain.EmailOutbox.deliverNext queuedStore { Send = fun _ -> Error "temporary mail outage" }
        compareStoreResult "provider failure after stateful sequence is retryable" failed failedExpected known |> ignore

        let returnedInput = projectLangFromExpression engine "conformance::failed-delivery-input(conformance::preloaded-seed())"
        equal (projectOracle { Store = queuedStore; Known = known }) returnedInput "failed delivery leaves the exact input Store unchanged"

        let firstSuccess = eval engine "conformance::first-delivery-success(conformance::preloaded-seed())" |> outputValue
        let firstSuccessExpected = Domain.EmailOutbox.deliverNext queuedStore Domain.EmailProvider.deterministic
        let firstSent = compareStoreResult "retry success sends only the first queued message" firstSuccess firstSuccessExpected known
        check firstSent.IsSome "first retry succeeds"

        let secondSuccess = eval engine "conformance::second-delivery-success(conformance::preloaded-seed())" |> outputValue
        let secondSuccessExpected =
            match firstSuccessExpected with
            | Error problem -> Error problem
            | Ok firstSentStore -> Domain.EmailOutbox.deliverNext firstSentStore Domain.EmailProvider.deterministic
        let fullySent = compareStoreResult "second successful delivery retains FIFO and prior sent entries" secondSuccess secondSuccessExpected known
        check fullySent.IsSome "second retry succeeds"

    let private testMetadataGraphAndEffects (engine: Runtime.Engine) =
        let words = [ "invoice.build-lines"; "invoice.sum-lines"; "subscription.replace-list" ]
        for word in words do
            let direct =
                dispatch engine "dependencies" [ "word", jstr word ]
                |> expectOk $"query dependencies of {word}"
                |> fun response -> response.["data"].["dependencies"] |> jsonStrings
            let callbacks = direct |> List.filter (fun dependency -> dependency.Contains("step", StringComparison.Ordinal))
            check (not (List.isEmpty callbacks)) $"{word} declares its static fold-step dependency"
            for callback in callbacks do
                let callers = dispatch engine "callers" [ "word", jstr callback ] |> expectOk $"query callers of {callback}" |> fun response -> response.["data"] |> jsonStrings
                check (callers |> List.contains word) $"{callback} caller graph includes {word}"
            let effects = dispatch engine "effects" [ "word", jstr word ] |> expectOk $"query effects of {word}" |> fun response -> response.["data"] |> jsonStrings
            equal [] effects $"{word} is a pure Store transformation"
        for word in [ "subscription.cancel"; "invoice.create"; "payment.apply-result"; "email.apply-delivery-result" ] do
            let effects = dispatch engine "effects" [ "word", jstr word ] |> expectOk $"query transition effects of {word}" |> fun response -> response.["data"] |> jsonStrings
            equal [] effects $"{word} models provider outcomes as pure values with no host effects"

    let private testLibraryPersistence (engine: Runtime.Engine) projectPath (document: FlowProjectDocument) =
        let names = document.Words |> List.map _.Name |> List.sort
        let typeNames = (document.Records |> List.map _.Name) @ (document.Scalars |> List.map _.Name) |> List.sort
        totalWords <- names.Length
        totalTypes <- typeNames.Length

        let mutable idsBefore = wordIds engine names
        let wordSourcesBefore = names |> List.map (fun name -> name, sourceForWord engine name)
        let typeSourcesBefore = typeNames |> List.map (fun name -> name, sourceForType engine name)
        let testsBefore =
            names
            |> List.map (fun name ->
                let cases =
                    dispatch engine "tests" [ "word", jstr name ]
                    |> expectOk $"query attached tests for {name}"
                    |> fun response -> jsonStrings response.["data"]
                check (not (List.isEmpty cases)) $"{name} has first-class library tests"
                name, cases)
        let examplesBefore =
            names
            |> List.map (fun name -> name, (dispatch engine "examples" [ "word", jstr name ] |> expectOk $"query examples for {name}" |> fun response -> jsonStrings response.["data"]))

        let examplesRun = names |> List.sumBy (runExamplesForWord engine)
        equal document.Examples.Length examplesRun "all attached examples execute before commit"
        totalExamples <- examplesRun
        runResultTests engine document.Tests.Length |> ignore

        let projectOnlyWords = Set.ofList [ "email.delivery-fold-step"; "email.apply-delivery-result" ]
        let authoredNames = Set.ofList names
        let finiteAudit =
            names
            |> List.map (fun name ->
                let finite =
                    dispatch engine "describe" [ "word", jstr name ]
                    |> expectOk $"describe finite coverage for {name} after test-all"
                    |> fun response -> response.["data"].["coverage"].["finiteCoverage"]
                let missing = jsonStrings finite.["missing"]
                let unsupported = jsonStrings finite.["unsupported"]
                let complete = boolValue finite.["complete"]
                name, complete, missing, unsupported)
        equal names.Length finiteAudit.Length "finite coverage was audited for every authored transition function"
        let incompleteFinite = finiteAudit |> List.filter (fun (_, complete, _, unsupported) -> not complete || not (List.isEmpty unsupported))
        for name, _, missing, unsupported in incompleteFinite do
            printfn "FINITE AUDIT INCOMPLETE %s missing=[%s] unsupported=[%s]" name (String.concat "; " missing) (String.concat "; " unsupported)
        for name, complete, missing, unsupported in finiteAudit do
            if name = "email.delivery-fold-step" then
                equal false complete "the delivery fold is deliberately not library-qualified"
                equal [ "return[0] EmailDeliveryPlan: $.first: none" ] missing
                    "the delivery fold's only finite gap is the unreachable none state"
                equal [] unsupported "the delivery fold has no unsupported finite domain"
            else
                equal true complete $"{name} has complete finite observations"
                equal [] missing $"{name} has no missing finite observations"
                equal [] unsupported $"{name} has no unsupported finite domains"

        let dependencyMap =
            names
            |> List.map (fun name ->
                let direct =
                    dispatch engine "dependencies" [ "word", jstr name ]
                    |> expectOk $"query direct dependencies of {name} before maturity promotion"
                    |> fun response -> jsonStrings response.["data"].["dependencies"]
                name, Set.ofList direct)
            |> Map.ofList
        let mutable projectClosure = Set.singleton "email.delivery-fold-step"
        let mutable projectClosureChanged = true
        while projectClosureChanged do
            let before = projectClosure
            for name in names do
                if not (projectClosure.Contains name)
                   && not (Set.isEmpty (Set.intersect dependencyMap[name] projectClosure)) then
                    projectClosure <- Set.add name projectClosure
            projectClosureChanged <- before <> projectClosure
        equal projectOnlyWords projectClosure "only the delivery fold and its authored callers remain project maturity"

        let callerResponse =
            dispatch engine "transitive-callers" [ "word", jstr "email.delivery-fold-step" ]
            |> expectOk "inspect delivery-fold-step callers before maturity promotion"
        let authoredDeliveryCallers =
            jsonStrings callerResponse.["data"].["callers"]
            |> List.filter authoredNames.Contains
            |> List.sort
        equal [ "email.apply-delivery-result" ] authoredDeliveryCallers
            "delivery-fold-step has exactly one authored transitive caller"

        let statusBeforeRejectedPromotion =
            dispatch engine "storage.status" []
            |> expectOk "capture durable status before finite-coverage rejection"
            |> fun response -> response.["data"].ToJsonString()
        let rejectedPromotion =
            dispatch engine "commit" [ "word", jstr "email.delivery-fold-step"; "library", jbool true ]
            |> expectError "reject the delivery fold's unreachable empty-first return"
        equal "LIBRARY_FINITE_COVERAGE_INCOMPLETE" (stringValue rejectedPromotion.["error"].["code"])
            "email.delivery-fold-step remains project maturity because its declared output is wider than its reachable range"
        equal "email.delivery-fold-step" (stringValue rejectedPromotion.["error"].["word"])
            "finite coverage rejection identifies the exact helper"
        equal [ "return[0] EmailDeliveryPlan: $.first: none" ] (jsonStrings rejectedPromotion.["error"].["expected"])
            "finite coverage rejection names the single unreachable Option case"
        let statusAfterRejectedPromotion =
            dispatch engine "storage.status" []
            |> expectOk "inspect durable status after finite-coverage rejection"
            |> fun response -> response.["data"].ToJsonString()
        equal statusBeforeRejectedPromotion statusAfterRejectedPromotion
            "rejected finite coverage promotion leaves durable storage unchanged"

        let validatorWords = document.Scalars |> List.choose _.Validator |> Set.ofList
        check (Set.isSubset validatorWords authoredNames) "all scalar validators are included in the authored word inventory"

        let projectWords =
            document.Words
            |> List.filter (fun word -> projectOnlyWords.Contains word.Name)
        let projectTestSources =
            document.Tests
            |> List.filter (fun test -> projectOnlyWords.Contains test.Word)
            |> List.map _.SourceText
        let projectExampleSources =
            document.Examples
            |> List.filter (fun example -> projectOnlyWords.Contains example.Word)
            |> List.map _.SourceText
        let projectSource =
            [ projectWords |> List.map _.SourceText
              projectTestSources
              projectExampleSources ]
            |> List.concat
            |> String.concat (Environment.NewLine + Environment.NewLine)

        let mutable pendingTemporaryWords = temporaryNames
        while not pendingTemporaryWords.IsEmpty do
            let removable =
                pendingTemporaryWords
                |> Set.filter (fun name ->
                    let callers =
                        dispatch engine "callers" [ "word", jstr name ]
                        |> expectOk $"inspect temporary callers of {name} before cleanup"
                        |> fun response -> jsonStrings response.["data"]
                    callers |> List.forall (pendingTemporaryWords.Contains >> not))
            if removable.IsEmpty then
                let cycleNames = pendingTemporaryWords |> Set.toList |> String.concat ", "
                failwith $"Temporary probe dependency cycle prevents cleanup: {cycleNames}"
            for name in removable do
                dispatch engine "discard" [ "word", jstr name ]
                |> expectOk $"discard completed temporary probe {name} before library qualification"
                |> ignore
                pendingTemporaryWords <- Set.remove name pendingTemporaryWords

        for name in [ "email.apply-delivery-result"; "email.delivery-fold-step" ] do
            dispatch engine "discard" [ "word", jstr name ]
            |> expectOk $"discard {name} temporarily to isolate its test metadata from library qualification"
            |> ignore

        let beforeLibraryCommit =
            dispatch engine "words" []
            |> expectOk "inspect authored candidates after removing the project-only delivery path"
            |> fun response -> response.["data"].["words"].AsArray()
        for name in names do
            if projectOnlyWords.Contains name then
                check (beforeLibraryCommit |> Seq.forall (fun row -> stringValue row.["name"] <> name))
                    $"{name} is absent while all library-ready candidates are committed"
            else
                let entry = beforeLibraryCommit |> Seq.find (fun row -> stringValue row.["name"] = name)
                equal "candidate" (stringValue entry.["status"]) $"{name} remains a candidate for global library qualification"

        // A named commit also pulls in candidate dependencies and test metadata
        // at project maturity. Remove this path and its owned metadata so one
        // aggregate commit can qualify every other candidate as library code.
        dispatch engine "commit" [ "library", jbool true ]
        |> expectOk "commit all remaining business words and types at library maturity"
        |> ignore

        let libraryInventory =
            dispatch engine "words" []
            |> expectOk "inspect the library-qualified vocabulary before restoring the project-only delivery path"
            |> fun response -> response.["data"].["words"].AsArray()
        for name in names |> List.filter (fun item -> not (projectOnlyWords.Contains item)) do
            let entry = libraryInventory |> Seq.find (fun row -> stringValue row.["name"] = name)
            equal "persistent" (stringValue entry.["status"]) $"{name} is persistent after global library qualification"
            equal "library" (stringValue entry.["maturity"]) $"{name} has library maturity after global qualification"
        for name in projectOnlyWords do
            check (libraryInventory |> Seq.forall (fun row -> stringValue row.["name"] <> name))
                $"{name} was withheld from the aggregate library commit"

        let restoredProjectPath =
            dispatch engine "define" [ "frontend", jstr "flow"; "source", jstr projectSource ]
            |> expectOk "restore exact source and attachments for the two project-maturity delivery words"
        equal projectWords.Length (restoredProjectPath.["data"].["words"].AsArray().Count)
            "both project-only word definitions are restored"
        let restoredNames = wordIds engine names
        idsBefore <- restoredNames
        equal names.Length restoredNames.Length "all original function names are restored before project commit"
        for name, source in wordSourcesBefore do
            if projectOnlyWords.Contains name then
                equal source (sourceForWord engine name) $"exact source is restored for {name}"
        for name, expectedCases in testsBefore do
            if projectOnlyWords.Contains name then
                let actualCases = dispatch engine "tests" [ "word", jstr name ] |> expectOk $"query restored tests for {name}" |> fun response -> jsonStrings response.["data"]
                equal expectedCases actualCases $"test identities are restored for {name}"
        for name, expectedCases in examplesBefore do
            if projectOnlyWords.Contains name then
                let actualCases = dispatch engine "examples" [ "word", jstr name ] |> expectOk $"query restored examples for {name}" |> fun response -> jsonStrings response.["data"]
                equal expectedCases actualCases $"example identities are restored for {name}"
        runResultTests engine document.Tests.Length |> ignore
        let restoredExamples = names |> List.sumBy (runExamplesForWord engine)
        equal document.Examples.Length restoredExamples "all exact examples pass after project-only source restoration"
        totalExamples <- restoredExamples

        let committedProjectPath =
            dispatch engine "commit" [ "word", jstr "email.apply-delivery-result" ]
            |> expectOk "persist the delivery path and its finite-incomplete fold helper at project maturity"
        check (committedProjectPath.ContainsKey "data") "project-maturity transition commit returns confirmation"
        runResultTests engine document.Tests.Length |> ignore

        let publishedWords = dispatch engine "words" [] |> expectOk "inspect library words" |> fun response -> response.["data"].["words"].AsArray()
        for name in names do
            let entry = publishedWords |> Seq.find (fun row -> stringValue row.["name"] = name)
            equal "persistent" (stringValue entry.["status"]) $"{name} persisted"
            let expectedMaturity = if projectOnlyWords.Contains name then "project" else "library"
            equal expectedMaturity (stringValue entry.["maturity"]) $"{name} has its declared maturity"
            let description = dispatch engine "describe" [ "word", jstr name ] |> expectOk $"describe committed word {name}"
            let coverage = description.["data"].["coverage"]
            equal "current" (stringValue coverage.["status"]) $"{name} has current test coverage"
            equal 0 (coverage.["uncoveredInstructions"].AsArray().Count) $"{name} has no uncovered instructions"
            equal 0 (coverage.["uncoveredBranchOutcomes"].AsArray().Count) $"{name} has no uncovered branch outcomes"

        let fresh = Runtime.Engine(projectPath, Set.empty, "2026-01-01T00:00:00.0000000+00:00")
        let idsAfter = wordIds fresh names
        equal idsBefore idsAfter "all committed transition words keep stable IDs after reload"
        for name, source in wordSourcesBefore do equal source (sourceForWord fresh name) $"exact source reloads for {name}"
        for name, source in typeSourcesBefore do equal source (sourceForType fresh name) $"exact type source reloads for {name}"
        for name, expectedCases in testsBefore do
            let actualCases = dispatch fresh "tests" [ "word", jstr name ] |> expectOk $"query reloaded tests for {name}" |> fun response -> jsonStrings response.["data"]
            equal expectedCases actualCases $"test names reload for {name}"
        for name, expectedCases in examplesBefore do
            let actualCases = dispatch fresh "examples" [ "word", jstr name ] |> expectOk $"query reloaded examples for {name}" |> fun response -> jsonStrings response.["data"]
            equal expectedCases actualCases $"example names reload for {name}"

        let freshWordEntries = dispatch fresh "words" [] |> expectOk "inspect fresh persistent dictionary" |> fun response -> response.["data"].["words"].AsArray()
        for name in names do
            let entry = freshWordEntries |> Seq.find (fun row -> stringValue row.["name"] = name)
            let expectedMaturity = if projectOnlyWords.Contains name then "project" else "library"
            equal expectedMaturity (stringValue entry.["maturity"]) $"{name} retains its declared maturity after reload"
        for name in temporaryNames do
            check (freshWordEntries |> Seq.forall (fun row -> stringValue row.["name"] <> name)) $"temporary probe {name} does not persist"
            let history = dispatch fresh "history" [ "word", jstr name ] |> expectError $"query history for temporary probe {name}"
            equal "HISTORY_WORD_UNKNOWN" (stringValue history.["error"].["code"]) $"temporary probe {name} has no durable revision"

        let freshTests = runResultTests fresh document.Tests.Length
        let freshExamplesRun = names |> List.sumBy (runExamplesForWord fresh)
        equal document.Examples.Length freshExamplesRun "all attached examples pass after a new Engine reload"
        equal (projectOracle (freshOracle ())) (projectLangFromExpression fresh "business::seed(unit)") "business.seed preserves the exact fixed baseline after reload"
        let reloadSubscriptionId = subscriptionId "60000000-0000-0000-0000-000000000021"
        let reloadCustomerId = freshOracle () |> fun state -> state.Known.Customers.Head
        let reloadProductId = freshOracle () |> fun state -> state.Known.Products.Head
        let reloadStartText = "2027-01-02T12:00:00.0000000+00:00"
        let reloadExpiryText = "2027-02-02T12:00:00.0000000+00:00"
        let reloadStart = instant reloadStartText
        let reloadExpiry = instant reloadExpiryText
        let reloadTransitionExpected =
            freshOracle ()
            |> fun state -> Domain.Subscription.start state.Store reloadSubscriptionId reloadCustomerId reloadProductId "annual" reloadStart reloadExpiry
            |> Result.map fst
        let reloadTransitionKnown = addKnownSubscription reloadSubscriptionId (freshOracle ()).Known
        let reloadTransition =
            $"subscription::start(business::seed(unit), {subscriptionIdExpr (Domain.SubscriptionId.toString reloadSubscriptionId)}, {customerIdExpr (Domain.CustomerId.toString reloadCustomerId)}, {productIdExpr (Domain.ProductId.toString reloadProductId)}, \"annual\", {instantExpr reloadStartText}, {instantExpr reloadExpiryText})"
        let transitioned = compareStoreResult "reloaded library executes a new subscription transition" (eval fresh reloadTransition |> outputValue) reloadTransitionExpected reloadTransitionKnown
        check transitioned.IsSome "reloaded authored transition succeeds against the oracle"
        reloadChecks <- idsAfter.Length + wordSourcesBefore.Length + typeSourcesBefore.Length + testsBefore.Length + examplesBefore.Length + temporaryNames.Count + 2
        let rows = freshTests.["data"].["results"].AsArray()
        equal document.Tests.Length rows.Count "fresh test result inventory is complete"
        fresh

    let private testPopulatedDeliveryExtension (engine: Runtime.Engine) projectPath root =
        let extensionPath = Path.Combine(root, "examples", "business-populated-delivery.agent")
        let source = File.ReadAllText extensionPath
        let document =
            FlowParser.parseDocumentWithVersion 2 "<business-populated-delivery>" source
            |> Result.defaultWith (fun diagnostic -> failwith (Diagnostics.render diagnostic))
        let names = document.Words |> List.map _.Name |> List.sort
        let typeNames = document.Records |> List.map _.Name |> List.sort
        let oldWordNames = [ "email.apply-delivery-result"; "email.delivery-fold-step" ]
        let oldWordIds = wordIds engine oldWordNames
        let oldWordSources = oldWordNames |> List.map (fun name -> name, sourceForWord engine name)
        let oldPlanSource = sourceForType engine "EmailDeliveryPlan"

        equal [ "email.apply-delivery-result-populated"; "email.populated-delivery-plan" ] names
            "the isolated Flow/2 extension declares only the populated planner and transition"
        equal [ "PopulatedEmailDeliveryPlan" ] typeNames
            "the isolated Flow/2 extension adds one required-head plan type"
        equal 7 document.Tests.Length "the populated delivery extension attaches seven focused cases"
        equal 3 document.Examples.Length "the populated delivery extension attaches three examples"
        populatedDeliveryWords <- names.Length
        populatedDeliveryTypes <- typeNames.Length
        populatedDeliveryTests <- document.Tests.Length
        populatedDeliveryExamples <- document.Examples.Length

        let defined =
            dispatch engine "define"
                [ "frontend", jstr "flow"
                  "syntaxVersion", JsonValue.Create(2) :> JsonNode
                  "source", jstr source ]
            |> expectOk "define the populated email delivery extension after baseline persistence"
        equal names
            (defined.["data"].["words"].AsArray() |> Seq.map (fun row -> stringValue row.["name"]) |> Seq.toList |> List.sort)
            "the extension stages exactly its two words"
        equal typeNames (jsonStrings defined.["data"].["types"] |> List.sort)
            "the extension stages its required-head record"
        let rejectedOptionalHead =
            dispatch engine "eval"
                [ "frontend", jstr "flow"
                  "syntaxVersion", JsonValue.Create(2) :> JsonNode
                  "code", jstr "populatedEmailDeliveryPlan::new(first = option::none<EmailMessage>(), remaining = list::empty<EmailMessage>())" ]
            |> expectError "reject Option<EmailMessage> where the populated plan requires an EmailMessage head"
        equal "FLOW_ARGUMENT_TYPE" (stringValue rejectedOptionalHead.["error"].["code"])
            "the generated plan constructor rejects the optional head as an argument-type error"
        equal [ "EmailMessage" ] (jsonStrings rejectedOptionalHead.["error"].["expected"])
            "the generated plan constructor requires one concrete email head"
        equal [ "Option<EmailMessage>" ] (jsonStrings rejectedOptionalHead.["error"].["actual"])
            "the rejected value is specifically an empty-capable email Option"
        let inputProbeName = "email.populated-delivery-input-probe"
        let inputProbeSource =
            $"fn {inputProbeName}(store: Store, outcome: Result<Unit, BusinessError>) -> Store {{\n"
            + "    let initial = store;\n"
            + "    let ignored = email::apply-delivery-result-populated(initial, outcome);\n"
            + "    initial\n"
            + "}"
        dispatch engine "define"
            [ "frontend", jstr "flow"
              "syntaxVersion", JsonValue.Create(2) :> JsonNode
              "temporary", jbool true
              "source", jstr inputProbeSource ]
        |> expectOk "define an isolated temporary probe to observe the same input binding after application"
        |> ignore

        let testNames (target: Runtime.Engine) word =
            dispatch target "tests" [ "word", jstr word ]
            |> expectOk $"query attached tests for {word}"
            |> fun response -> jsonStrings response.["data"]
        let exampleNames (target: Runtime.Engine) word =
            dispatch target "examples" [ "word", jstr word ]
            |> expectOk $"query attached examples for {word}"
            |> fun response -> jsonStrings response.["data"]
        let testsBefore = names |> List.map (fun word -> word, testNames engine word)
        let examplesBefore = names |> List.map (fun word -> word, exampleNames engine word)
        equal document.Tests.Length (testsBefore |> List.sumBy (snd >> List.length))
            "all seven new tests attach to the two extension words"
        equal document.Examples.Length (examplesBefore |> List.sumBy (snd >> List.length))
            "all three examples attach to the two extension words"

        let runAttachedTests (target: Runtime.Engine) =
            let mutable total = 0
            for word in names do
                let expectedCases =
                    document.Tests
                    |> List.filter (fun test -> test.Word = word)
                    |> List.map _.CaseName
                    |> List.sort
                let response = dispatch target "test" [ "word", jstr word ] |> expectOk $"run tests for {word}"
                let rows = response.["data"].["results"].AsArray()
                equal expectedCases.Length rows.Count $"all attached cases run for {word}"
                equal expectedCases
                    (rows |> Seq.map (fun row -> stringValue row.["name"]) |> Seq.toList |> List.sort)
                    $"attached test names match the extension source for {word}"
                for row in rows do
                    check (boolValue row.["passed"]) $"extension test passed for {word}: {row.ToJsonString()}"
                let described = dispatch target "describe" [ "word", jstr word ] |> expectOk $"describe extension coverage for {word}"
                let coverage = described.["data"].["coverage"]
                equal "current" (stringValue coverage.["status"]) $"{word} has current structural coverage"
                equal 0 (coverage.["uncoveredInstructions"].AsArray().Count) $"{word} has no uncovered instructions"
                equal 0 (coverage.["uncoveredBranchOutcomes"].AsArray().Count) $"{word} has no uncovered branches"
                let finite = coverage.["finiteCoverage"]
                equal true (boolValue finite.["complete"]) $"{word} has complete finite input/output observations"
                equal [] (jsonStrings finite.["missing"]) $"{word} has no missing finite outcomes"
                equal [] (jsonStrings finite.["unsupported"]) $"{word} has no unsupported finite domain"
                total <- total + rows.Count
            equal document.Tests.Length total "all attached extension tests ran"
            total

        let runExamples (target: Runtime.Engine) =
            let total = names |> List.sumBy (runExamplesForWord target)
            equal document.Examples.Length total "all extension examples ran"
            total

        let messageCode address subject body =
            $"emailMessage::new(to = Email::new({flowString address}), subject = {flowString subject}, body = {flowString body})"
        let emailListCode values =
            match values with
            | [] -> "list::empty<EmailMessage>()"
            | head :: tail ->
                tail
                |> List.fold (fun prior next -> $"list::append({prior}, {next})")
                    $"list::singleton<EmailMessage>({head})"
        let storeCode pending sent =
            $"store::with-email-state(business::seed(unit), {emailListCode pending}, {emailListCode sent})"
        let applyCode store outcome =
            $"email::apply-delivery-result-populated({store}, {outcome})"
        let errorOutcome code message =
            $"result::error<Unit, BusinessError>(businessError::new(code = {flowString code}, message = {flowString message}))"
        let successOutcome = "result::ok<Unit, BusinessError>(unit)"

        let baseline = freshOracle ()
        let customer = baseline.Known.Customers.Head
        let queue label state subject body =
            let updated, _ = Domain.EmailOutbox.queue state.Store customer subject body |> oracleOk label
            { state with Store = updated }
        let oracleDeliver state outcome =
            Domain.EmailOutbox.deliverNext state.Store { Send = fun _ -> outcome }

        let compareDelivery label store outcome expected known expectedLanguageErrorMessage =
            let languageResult = eval engine (applyCode store outcome) |> outputValue
            match expected, expectedLanguageErrorMessage with
            | Error _, Some expectedMessage ->
                // Domain.EmailOutbox prefixes provider failures; Flow preserves the original raw provider detail.
                equal expectedMessage (resultErrorMessage languageResult) $"{label} language-side detail preserves the contract"
            | Ok _, None -> ()
            | Error _, None -> failwith $"{label}: missing expected language error detail"
            | Ok _, Some _ -> failwith $"{label}: success cannot have a language error detail"
            compareStoreResult label languageResult expected known |> ignore
            let inputProjection = projectLangFromExpression engine store
            let inputProbeCall = $"email::populated-delivery-input-probe({store}, {outcome})"
            let returnedInitial = eval engine inputProbeCall |> outputValue |> readLanguageStore
            equal inputProjection returnedInitial $"{label} leaves the same bound input Store unchanged after application"

        let emptyStore = storeCode [] []
        let emptyOracleResult = oracleDeliver baseline (Error "offline")
        compareDelivery "populated delivery prioritizes empty outbox over provider error"
            emptyStore (errorOutcome "TEMP" "offline") emptyOracleResult baseline.Known (Some "There is no queued email to deliver.")
            |> ignore

        let adaAddress = "ada@example.test"
        let priorCode = messageCode adaAddress "Prior sent" "prior body"
        let priorQueued = queue "oracle prior message" baseline "Prior sent" "prior body"
        let priorSentStore = oracleDeliver priorQueued (Ok ()) |> oracleOk "oracle prior sent message"
        let priorSent = { priorQueued with Store = priorSentStore }

        let singletonCode = messageCode adaAddress "Singleton" "singleton body"
        let singletonOracle = queue "oracle singleton pending message" priorSent "Singleton" "singleton body"
        let singletonStoreCode = storeCode [ singletonCode ] [ priorCode ]
        let singletonInput = projectLangFromExpression engine singletonStoreCode
        equal (projectOracle singletonOracle) singletonInput "singleton language input matches the independent F# Store"
        let singletonExpected = oracleDeliver singletonOracle (Ok ())
        compareDelivery "populated delivery transfers the sole message"
            singletonStoreCode successOutcome singletonExpected singletonOracle.Known None
            |> ignore

        let duplicateCode = messageCode adaAddress "Repeated" "same body"
        let laterCode = messageCode adaAddress "Later" "later body"
        let duplicateFirst = queue "oracle first duplicate" priorSent "Repeated" "same body"
        let duplicateSecond = queue "oracle second duplicate" duplicateFirst "Repeated" "same body"
        let threePending = queue "oracle third pending message" duplicateSecond "Later" "later body"
        let threeStoreCode = storeCode [ duplicateCode; duplicateCode; laterCode ] [ priorCode ]
        let threeInput = projectLangFromExpression engine threeStoreCode
        equal (projectOracle threePending) threeInput "three-message duplicate language input matches F# order and positions"
        let providerDetail = "mail gateway unavailable"
        let providerFailure = oracleDeliver threePending (Error providerDetail)
        compareDelivery "populated provider failure keeps all FIFO positions"
            threeStoreCode (errorOutcome "TEMPORARY" providerDetail) providerFailure threePending.Known (Some providerDetail)
            |> ignore

        let threeSuccess = oracleDeliver threePending (Ok ())
        compareDelivery "populated success removes exactly the first duplicate and preserves every Store field"
            threeStoreCode successOutcome threeSuccess threePending.Known None
            |> ignore

        dispatch engine "discard" [ "word", jstr inputProbeName ]
        |> expectOk "discard the temporary immutability probe before the aggregate library commit"
        |> ignore
        let wordsBeforeCommit = dispatch engine "words" [] |> expectOk "verify extension candidates before library commit" |> fun response -> response.["data"].["words"].AsArray()
        check (wordsBeforeCommit |> Seq.forall (fun row -> stringValue row.["name"] <> inputProbeName))
            "the temporary input probe is absent before library qualification"

        let dependencyResponse =
            dispatch engine "dependencies" [ "word", jstr "email.apply-delivery-result-populated" ]
            |> expectOk "inspect the populated transition's direct dependencies"
        check (jsonStrings dependencyResponse.["data"].["dependencies"] |> List.contains "email.populated-delivery-plan")
            "the populated transition calls its plan builder directly"
        let callerResponse =
            dispatch engine "callers" [ "word", jstr "email.populated-delivery-plan" ]
            |> expectOk "inspect callers of the populated plan builder"
        check (jsonStrings callerResponse.["data"] |> List.contains "email.apply-delivery-result-populated")
            "the populated transition is the plan builder's authored caller"
        for word in names do
            let effects = dispatch engine "effects" [ "word", jstr word ] |> expectOk $"inspect effects for {word}" |> fun response -> jsonStrings response.["data"]
            equal [] effects $"{word} remains a pure transition"

        runAttachedTests engine |> ignore
        runExamples engine |> ignore

        let newWordIds = wordIds engine names
        let newWordSources = names |> List.map (fun name -> name, sourceForWord engine name)
        let newTypeSources = typeNames |> List.map (fun name -> name, sourceForType engine name)
        let newWordTests = names |> List.map (fun name -> name, testNames engine name)
        let newWordExamples = names |> List.map (fun name -> name, exampleNames engine name)

        dispatch engine "commit" [ "library", jbool true ]
        |> expectOk "commit the tested populated delivery extension as library vocabulary"
        |> ignore

        let committedWords = dispatch engine "words" [] |> expectOk "inspect populated delivery library words" |> fun response -> response.["data"].["words"].AsArray()
        for name in names do
            let row = committedWords |> Seq.find (fun item -> stringValue item.["name"] = name)
            equal "persistent" (stringValue row.["status"]) $"{name} persists after library qualification"
            equal "library" (stringValue row.["maturity"]) $"{name} qualifies as a library word"
        let oldAfterCommit = dispatch engine "words" [] |> expectOk "verify the original delivery maturity after extension commit" |> fun response -> response.["data"].["words"].AsArray()
        for name in oldWordNames do
            let row = oldAfterCommit |> Seq.find (fun item -> stringValue item.["name"] = name)
            equal "project" (stringValue row.["maturity"]) $"original {name} remains project maturity"
        equal oldWordIds (wordIds engine oldWordNames) "original delivery word IDs remain unchanged"
        for name, oldSource in oldWordSources do
            equal oldSource (sourceForWord engine name) $"original source remains unchanged for {name}"
        equal oldPlanSource (sourceForType engine "EmailDeliveryPlan") "the original Option plan type remains unchanged"

        let fresh = Runtime.Engine(projectPath, Set.empty, "2026-01-01T00:00:00.0000000+00:00")
        equal newWordIds (wordIds fresh names) "both extension word identities persist across reload"
        equal oldWordIds (wordIds fresh oldWordNames) "both original word identities persist across extension reload"
        for name, expectedSource in newWordSources do
            equal expectedSource (sourceForWord fresh name) $"populated delivery source reloads exactly for {name}"
        for name, expectedSource in newTypeSources do
            equal expectedSource (sourceForType fresh name) $"populated delivery type reloads exactly for {name}"
        for name, expectedSource in oldWordSources do
            equal expectedSource (sourceForWord fresh name) $"original source reloads exactly for {name}"
        equal oldPlanSource (sourceForType fresh "EmailDeliveryPlan") "original plan type reloads unchanged"
        for name, expectedCases in newWordTests do
            equal expectedCases (testNames fresh name) $"attached test names reload exactly for {name}"
        for name, expectedCases in newWordExamples do
            equal expectedCases (exampleNames fresh name) $"attached example names reload exactly for {name}"

        let reloadedWords = dispatch fresh "words" [] |> expectOk "inspect extension maturity after reload" |> fun response -> response.["data"].["words"].AsArray()
        check (reloadedWords |> Seq.forall (fun row -> stringValue row.["name"] <> inputProbeName))
            "the discarded temporary input probe is absent after fresh reload"
        for name in names do
            let row = reloadedWords |> Seq.find (fun item -> stringValue item.["name"] = name)
            equal "library" (stringValue row.["maturity"]) $"{name} remains library-qualified after reload"
        for name in oldWordNames do
            let row = reloadedWords |> Seq.find (fun item -> stringValue item.["name"] = name)
            equal "project" (stringValue row.["maturity"]) $"original {name} remains project maturity after reload"

        runAttachedTests fresh |> ignore
        runExamples fresh |> ignore

        populatedDeliveryReloadChecks <- newWordIds.Length + newWordSources.Length + newTypeSources.Length + newWordTests.Length + newWordExamples.Length + oldWordSources.Length + 4
        fresh

    let private gitValue arguments =
        try
            let start = ProcessStartInfo("git")
            start.UseShellExecute <- false
            start.RedirectStandardOutput <- true
            start.RedirectStandardError <- true
            start.WorkingDirectory <- Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
            for argument in arguments do start.ArgumentList.Add argument
            use child = Process.Start start
            if isNull child then "unavailable"
            else
                let output = child.StandardOutput.ReadToEnd().Trim()
                child.WaitForExit()
                if child.ExitCode = 0 then output else "unavailable"
        with _ -> "unavailable"

    let private writeEvidence path command status failure =
        let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
        let target = Path.GetFullPath(path, root)
        Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
        let report = JsonObject()
        report.["name"] <- jstr "focused-business-transitions"
        report.["status"] <- jstr status
        report.["revision"] <- jstr (gitValue [ "rev-parse"; "HEAD" ])
        report.["dirty"] <- jbool (not (String.IsNullOrWhiteSpace(gitValue [ "status"; "--porcelain" ])))
        report.["command"] <- jstr command
        report.["result"] <- jstr (failure |> Option.defaultValue $"passed {groups} groups and {assertions} assertions")
        report.["assertions"] <- System.Text.Json.JsonSerializer.SerializeToNode assertions
        report.["groups"] <- System.Text.Json.JsonSerializer.SerializeToNode groups
        report.["authoredWords"] <- System.Text.Json.JsonSerializer.SerializeToNode totalWords
        report.["authoredTypes"] <- System.Text.Json.JsonSerializer.SerializeToNode totalTypes
        report.["attachedTests"] <- System.Text.Json.JsonSerializer.SerializeToNode totalTests
        report.["examplesPerRun"] <- System.Text.Json.JsonSerializer.SerializeToNode totalExamples
        report.["reloadChecks"] <- System.Text.Json.JsonSerializer.SerializeToNode reloadChecks
        let extension = JsonObject()
        extension.["authoredWords"] <- System.Text.Json.JsonSerializer.SerializeToNode populatedDeliveryWords
        extension.["authoredTypes"] <- System.Text.Json.JsonSerializer.SerializeToNode populatedDeliveryTypes
        extension.["attachedTests"] <- System.Text.Json.JsonSerializer.SerializeToNode populatedDeliveryTests
        extension.["examplesPerRun"] <- System.Text.Json.JsonSerializer.SerializeToNode populatedDeliveryExamples
        extension.["reloadChecks"] <- System.Text.Json.JsonSerializer.SerializeToNode populatedDeliveryReloadChecks
        extension.["coverageScope"] <- jstr "Coverage is checked immediately after each word's attached test batch; later runs reset the latest-batch evidence."
        report.["populatedDeliveryExtension"] <- extension
        report.["fidelityBoundary"] <- jstr "Provider results are explicit pure values; no external payment or email provider is invoked. Error codes and Store projections are compared to the F# oracle; Flow preserves raw provider error text while the F# DomainError formatter prefixes it. AgentLang record constructors are public, so supplemental invalid Product and PaymentReceipt boundary cases cannot be represented as valid F# domain records."
        File.WriteAllText(target, report.ToJsonString(System.Text.Json.JsonSerializerOptions(WriteIndented = true)) + Environment.NewLine, System.Text.UTF8Encoding(false))

    [<EntryPoint>]
    let main argv =
        let evidencePath, command =
            match argv |> Array.toList with
            | [] -> None, "dotnet run --project tests/AgentLang.Business.Transitions.Tests/AgentLang.Business.Transitions.Tests.fsproj --configuration Debug"
            | [ "--evidence"; path ] ->
                Some path,
                $"dotnet run --project tests/AgentLang.Business.Transitions.Tests/AgentLang.Business.Transitions.Tests.fsproj --configuration Debug -- --evidence {path}"
            | _ -> failwith "Usage: AgentLang.Business.Transitions.Tests [--evidence <relative-or-absolute-path>]"

        let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
        let fixtureFiles =
            [ "business-values.agent"
              "business-store.agent"
              "business-state.agent"
              "business-subscriptions.agent"
              "business-invoices.agent"
              "business-payments-email.agent" ]
        let source =
            fixtureFiles
            |> List.map (fun name -> Path.Combine(root, "examples", name) |> File.ReadAllText)
            |> String.concat Environment.NewLine
        let document =
            FlowParser.parseDocument "<business-transitions>" source
            |> Result.defaultWith (fun diagnostic -> failwith (Diagnostics.render diagnostic))
        check (not (List.isEmpty document.Words)) "joined fixture declares business words"
        check (not (List.isEmpty document.Tests)) "joined fixture includes attached tests"
        check (not (List.isEmpty document.Examples)) "joined fixture includes examples"
        equal 53 document.Words.Length "all expected authored business words are present"
        equal 31 (document.Records.Length + document.Scalars.Length) "all expected nominal business types are present"
        equal 154 document.Tests.Length "all expected attached business test cases are present"
        equal 44 document.Examples.Length "all expected business examples are present"

        let projectPath = Path.Combine(Path.GetTempPath(), $"agentlang-business-transitions-{Guid.NewGuid():N}")
        Directory.CreateDirectory projectPath |> ignore
        let engine = Runtime.Engine(projectPath, Set.empty, "2026-01-01T00:00:00.0000000+00:00")
        let resolvedProjectPath = Path.GetFullPath projectPath
        let resolvedTempRoot =
            Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + string Path.DirectorySeparatorChar
        let mutable baselineReloadedEngine: Runtime.Engine option = None
        try
          try
            let definition = dispatch engine "define" [ "frontend", jstr "flow"; "source", jstr source ] |> expectOk "define all six business fixture files in one Engine request"
            let definedWords = definition.["data"].["words"].AsArray()
            let definedTypes = definition.["data"].["types"].AsArray()
            equal document.Words.Length definedWords.Count "one atomic definition stages every authored word"
            equal (document.Records.Length + document.Scalars.Length) definedTypes.Count "one atomic definition stages every nominal type"

            runGroup "fixture tests and examples" (fun () ->
                runResultTests engine document.Tests.Length |> ignore
                let count = document.Words |> List.sumBy (fun word -> runExamplesForWord engine word.Name)
                equal document.Examples.Length count "every authored example executes"
                totalExamples <- count)
            runGroup "deterministic seed, full-state projection, and strong types" (fun () -> testSeedAndTypedBoundaries engine)
            runGroup "subscription start, cancel, and error precedence" (fun () -> testSubscriptions engine)
            runGroup "invoice lines, totals, and creation" (fun () -> testInvoiceTransitions engine)
            runGroup "payment and email transition contracts" (fun () -> testPayments engine; testEmailTransitions engine)
            runGroup "stateful renewal, billing, payment, and FIFO retry" (fun () -> testEndToEndState engine)
            runGroup "dependency graph, effects, library coverage, and reload" (fun () ->
                testMetadataGraphAndEffects engine
                baselineReloadedEngine <- Some(testLibraryPersistence engine projectPath document))
            runGroup "Flow/2 populated delivery extension and independent FIFO oracle" (fun () ->
                let reloaded = baselineReloadedEngine |> Option.defaultWith (fun () -> failwith "baseline persistence did not return its fresh Engine")
                testPopulatedDeliveryExtension reloaded projectPath root |> ignore)

            printfn "PASS %d groups, %d assertions; baseline: %d tests and %d examples per run, %d words and %d types; populated delivery extension: %d tests, %d examples, %d words, and %d types." groups assertions totalTests totalExamples totalWords totalTypes populatedDeliveryTests populatedDeliveryExamples populatedDeliveryWords populatedDeliveryTypes
            evidencePath |> Option.iter (fun path -> writeEvidence path command "passed" None)
            0
          with error ->
            eprintfn "FAIL after %d groups and %d assertions: %s" groups assertions (error.ToString())
            evidencePath |> Option.iter (fun path -> writeEvidence path command "failed" (Some(error.ToString())))
            1
        finally
            if Directory.Exists resolvedProjectPath then
                let isContained = resolvedProjectPath.StartsWith(resolvedTempRoot, StringComparison.OrdinalIgnoreCase)
                let hasExpectedPrefix = Path.GetFileName(resolvedProjectPath).StartsWith("agentlang-business-transitions-", StringComparison.Ordinal)
                if not (isContained && hasExpectedPrefix) then
                    failwith $"Refusing to recursively delete unexpected temporary project path: {resolvedProjectPath}"
                Directory.Delete(resolvedProjectPath, true)
