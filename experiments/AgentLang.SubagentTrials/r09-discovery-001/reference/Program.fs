namespace AgentLang.R09Reference

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text.Json
open AgentLang.Business.Domain

exception ReferenceFailure of code: string * message: string

type ConstructorFailure = {
    stage: string
    code: string
    message: string
}

exception ConstructorBlocked of failures: ConstructorFailure list * code: string * message: string

type CountsProjection = {
    customers: int
    products: int
    subscriptions: int
    invoices: int
    payments: int
    pendingEmails: int
    sentEmails: int
}

type CustomerProjection = {
    id: string
    email: string
    kind: string
    balanceMinorUnits: string
    createdAt: string
}

type ProductProjection = {
    id: string
    name: string
    unitPriceMinorUnits: string
}

type SubscriptionProjection = {
    id: string
    customerId: string
    productId: string
    term: string
    startedAt: string
    expiresAt: string
    status: string
    cancelledAt: string
}

type InvoiceLineProjection = {
    productId: string
    description: string
    quantity: int
    unitPriceMinorUnits: string
    lineTotalMinorUnits: string
}

type InvoiceProjection = {
    id: string
    customerId: string
    lines: InvoiceLineProjection list
    totalMinorUnits: string
    createdAt: string
    status: string
}

type PaymentProjection = {
    id: string
    invoiceId: string
    amountMinorUnits: string
    providerReference: string
    paidAt: string
}

type EmailProjection = {
    toAddress: string
    subject: string
    body: string
}

type StoreProjection = {
    counts: CountsProjection
    countVector: int list
    customers: CustomerProjection list
    products: ProductProjection list
    subscriptions: SubscriptionProjection list
    invoices: InvoiceProjection list
    payments: PaymentProjection list
    pendingEmails: EmailProjection list
    sentEmails: EmailProjection list
    complete: bool
}

type Outcome = {
    status: string
    value: string
    errorCode: string
}

type CustomerTotal = {
    customerId: string
    outcome: Outcome
}

type PaymentSequenceEntry = {
    paymentId: string
    invoiceId: string
    amountMinorUnits: string
}

type ReferenceCalculation = {
    direct: Outcome
    account: Outcome
    accountCustomer: CustomerProjection
    accountCustomerFound: bool
    customerPaidTotals: CustomerTotal list
    metrics: Outcome
    metricsCounts: int list
    recipeCounts: int list
    paidInvoiceRecipeOrder: string list
    serializedPaymentIds: string list
    paymentSequence: PaymentSequenceEntry list
    paymentProjectionBasis: string
}

type ComparisonChecks = {
    directMatchesOracle: bool
    accountMatchesOracle: bool
    metricsMatchesOracle: bool
    metricsCountsMatchOracle: bool
    metricsCountsMatchRecipe: bool
    accountCustomerPreserved: bool
    storeProjectionUnchanged: bool
    projectionContainsEveryCountedValue: bool
    paymentProjectionOrderMatchesRecipe: bool
    stickyOverflowAmountOrderVerified: bool
}

type CaseOutput = {
    id: string
    status: string
    oracleExpected: JsonElement
    constructorFailures: ConstructorFailure list
    storeBefore: StoreProjection
    storeAfter: StoreProjection
    reference: ReferenceCalculation
    checks: ComparisonChecks
    failure: string
}

type FixtureSummary = {
    oracleCaseCount: int
    acceptedCaseCount: int
    blockedCaseCount: int
    mismatchedCaseCount: int
}

type FixtureOutput = {
    schemaVersion: int
    status: string
    oracleSha256Before: string
    oracleSha256After: string
    inputUnchanged: bool
    caseSummary: FixtureSummary
    cases: CaseOutput list
}

module Program =
    let private options = JsonSerializerOptions(WriteIndented = true)
    let private invariant = CultureInfo.InvariantCulture

    let private fail (code: string) (message: string) = raise (ReferenceFailure(code, message))

    let private property (element: JsonElement) (name: string) =
        match element.TryGetProperty(name) with
        | true, value -> value
        | false, _ -> fail "INVALID_ORACLE" $"Missing property '{name}'."

    let private tryProperty (element: JsonElement) (name: string) =
        match element.TryGetProperty(name) with
        | true, value -> Some value
        | false, _ -> None

    let private stringValue (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.String then fail "INVALID_ORACLE" "Expected a JSON string."
        element.GetString()

    let private stringProperty (element: JsonElement) (name: string) = property element name |> stringValue

    let private intProperty (element: JsonElement) (name: string) =
        let value = property element name
        match Int32.TryParse(value.GetRawText(), NumberStyles.Integer, invariant) with
        | true, parsed -> parsed
        | false, _ -> fail "INVALID_ORACLE" $"Property '{name}' must be an Int32."

    let private boolProperty (element: JsonElement) (name: string) =
        let value = property element name
        if value.ValueKind = JsonValueKind.True then true
        elif value.ValueKind = JsonValueKind.False then false
        else fail "INVALID_ORACLE" $"Property '{name}' must be a boolean."

    let private arrayItems (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Array then fail "INVALID_ORACLE" "Expected a JSON array."
        [ for index in 0 .. element.GetArrayLength() - 1 -> element[index] ]

    let private optionalStringProperty (element: JsonElement) (name: string) (fallback: string) =
        match tryProperty element name with
        | None -> fallback
        | Some value when value.ValueKind = JsonValueKind.Null -> fallback
        | Some value -> stringValue value

    let private parseMinorUnits (value: string) =
        match Int64.TryParse(value, NumberStyles.Integer, invariant) with
        | true, parsed -> parsed
        | false, _ -> fail "INVALID_ORACLE" $"Invalid Int64 minor-unit amount '{value}'."

    let private minorUnitsText (value: int64) = value.ToString(invariant)

    let private utcText (value: DateTimeOffset) =
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", invariant)

    let private parseDate (value: string) =
        DateTimeOffset.Parse(value, invariant, DateTimeStyles.RoundtripKind).ToUniversalTime()

    let private guidText (group: string) (number: int) =
        if number < 1 then fail "INVALID_ORACLE" "Symbolic identifier numbers must be positive."
        $"{group}-0000-0000-0000-{number:D12}"

    let private symbolNumber (prefix: string) (symbol: string) =
        if not (symbol.StartsWith(prefix, StringComparison.Ordinal)) then
            fail "INVALID_ORACLE" $"Symbol '{symbol}' does not use the expected '{prefix}' prefix."
        let suffix = symbol.Substring(prefix.Length)
        match Int32.TryParse(suffix, NumberStyles.None, invariant) with
        | true, value when value > 0 -> value
        | _ -> fail "INVALID_ORACLE" $"Symbol '{symbol}' must end in a positive decimal number."

    let private customerGuid symbol = guidText "10000000" (symbolNumber "c" symbol)
    let private invoiceGuid symbol = guidText "20000000" (symbolNumber "i" symbol)
    let private paymentGuid number = guidText "30000000" number
    let private productGuid number = guidText "40000000" number
    let private subscriptionGuid number = guidText "50000000" number

    let private domainValue (stage: string) (result: Result<'T, DomainError>) =
        match result with
        | Ok value -> value
        | Error error ->
            let code = DomainError.code error
            let message = DomainError.message error
            raise (ReferenceFailure(code, $"{stage}: {message}"))

    let private recordFailure (failures: ResizeArray<ConstructorFailure>) (stage: string) (result: Result<'T, DomainError>) =
        match result with
        | Ok value -> value
        | Error error ->
            let code = DomainError.code error
            let message = DomainError.message error
            failures.Add({ stage = stage; code = code; message = message })
            raise (ConstructorBlocked(List.ofSeq failures, code, $"{stage}: {message}"))

    let private customerId (symbol: string) =
        CustomerId.parse (customerGuid symbol)
        |> domainValue $"parse customer {symbol}"

    let private invoiceId (symbol: string) =
        InvoiceId.parse (invoiceGuid symbol)
        |> domainValue $"parse invoice {symbol}"

    let private customerProjection (value: Customer) = {
        id = Customer.id value |> CustomerId.toString
        email = Customer.email value |> Email.value
        kind = Customer.kind value
        balanceMinorUnits = Customer.balance value |> Money.minorUnits |> minorUnitsText
        createdAt = Customer.createdAt value |> utcText
    }

    let private productProjection (value: Product) = {
        id = Product.id value |> ProductId.toString
        name = Product.name value
        unitPriceMinorUnits = Product.unitPrice value |> Money.minorUnits |> minorUnitsText
    }

    let private subscriptionProjection (value: Subscription) = {
        id = Subscription.id value |> SubscriptionId.toString
        customerId = Subscription.customerId value |> CustomerId.toString
        productId = Subscription.productId value |> ProductId.toString
        term = Subscription.term value
        startedAt = Subscription.startedAt value |> utcText
        expiresAt = Subscription.expiresAt value |> utcText
        status =
            match Subscription.status value with
            | Active -> "active"
            | Cancelled -> "cancelled"
        cancelledAt =
            match Subscription.cancelledAt value with
            | Some date -> utcText date
            | None -> null
    }

    let private invoiceProjection (value: Invoice) = {
        id = Invoice.id value |> InvoiceId.toString
        customerId = Invoice.customerId value |> CustomerId.toString
        lines =
            Invoice.lines value
            |> List.map (fun line -> {
                productId = InvoiceLine.productId line |> ProductId.toString
                description = InvoiceLine.description line
                quantity = InvoiceLine.quantity line
                unitPriceMinorUnits = InvoiceLine.unitPrice line |> Money.minorUnits |> minorUnitsText
                lineTotalMinorUnits = InvoiceLine.lineTotal line |> Money.minorUnits |> minorUnitsText
            })
        totalMinorUnits = Invoice.total value |> Money.minorUnits |> minorUnitsText
        createdAt = Invoice.createdAt value |> utcText
        status =
            match Invoice.status value with
            | Open -> "open"
            | Paid -> "paid"
    }

    let private paymentProjection (value: Payment) = {
        id = Payment.id value |> PaymentId.toString
        invoiceId = Payment.invoiceId value |> InvoiceId.toString
        amountMinorUnits = Payment.amount value |> Money.minorUnits |> minorUnitsText
        providerReference = Payment.providerReference value
        paidAt = Payment.paidAt value |> utcText
    }

    let private emailProjection (value: EmailMessage) = {
        toAddress = EmailMessage.toAddress value |> Email.value
        subject = EmailMessage.subject value
        body = EmailMessage.body value
    }

    let private lookup (stage: string) (lookupResult: 'T option) =
        match lookupResult with
        | Some value -> value
        | None -> fail "PROJECTION_LOOKUP_MISSING" $"Expected generated entity was missing during {stage}."

    let private projectStore store customerIds productIds subscriptionIds invoiceIds paymentIds =
        let summary = Store.summary store
        let counts = {
            customers = summary.Customers
            products = summary.Products
            subscriptions = summary.Subscriptions
            invoices = summary.Invoices
            payments = summary.Payments
            pendingEmails = summary.PendingEmails
            sentEmails = summary.SentEmails
        }
        let countVector = [
            counts.customers
            counts.products
            counts.subscriptions
            counts.invoices
            counts.payments
            counts.pendingEmails
            counts.sentEmails
        ]
        let customerRows =
            customerIds
            |> List.map (fun id -> Store.customer id store |> lookup "customer projection" |> customerProjection)
        let productRows =
            productIds
            |> List.map (fun id -> Store.product id store |> lookup "product projection" |> productProjection)
        let subscriptionRows =
            subscriptionIds
            |> List.map (fun id -> Store.subscription id store |> lookup "subscription projection" |> subscriptionProjection)
        let invoiceRows =
            invoiceIds
            |> List.map (fun id -> Store.invoice id store |> lookup "invoice projection" |> invoiceProjection)
        let paymentRows =
            paymentIds
            |> List.sortBy PaymentId.toString
            |> List.map (fun id -> Store.payment id store |> lookup "payment projection" |> paymentProjection)
        let pendingRows = Store.pendingEmails store |> List.map emailProjection
        let sentRows = Store.sentEmails store |> List.map emailProjection
        let complete =
            counts.customers = customerRows.Length
            && counts.products = productRows.Length
            && counts.subscriptions = subscriptionRows.Length
            && counts.invoices = invoiceRows.Length
            && counts.payments = paymentRows.Length
            && counts.pendingEmails = pendingRows.Length
            && counts.sentEmails = sentRows.Length
        {
            counts = counts
            countVector = countVector
            customers = customerRows
            products = productRows
            subscriptions = subscriptionRows
            invoices = invoiceRows
            payments = paymentRows
            pendingEmails = pendingRows
            sentEmails = sentRows
            complete = complete
        }

    let private outcomeOk (value: string) = { status = "ok"; value = value; errorCode = null }
    let private outcomeError (code: string) = { status = "error"; value = null; errorCode = code }

    let private checkedSum (values: int64 list) =
        values
        |> List.fold (fun state value ->
            match state with
            | Error code -> Error code
            | Ok total ->
                try Ok(Checked.(+) total value)
                with :? OverflowException -> Error "MONEY_OVERFLOW") (Ok 0L)

    let private sameOutcome (actual: Outcome) (expected: JsonElement) =
        match tryProperty expected "ok", tryProperty expected "error" with
        | Some expectedValue, _ when expectedValue.ValueKind = JsonValueKind.String ->
            actual.status = "ok" && actual.value = stringValue expectedValue
        | _, Some expectedError when expectedError.ValueKind = JsonValueKind.String ->
            actual.status = "error" && actual.errorCode = stringValue expectedError
        | _ -> false

    let private expectedCountsMatch (actual: int list) (expectedMetrics: JsonElement) =
        match tryProperty expectedMetrics "counts" with
        | None -> true
        | Some expectedCounts ->
            let values = arrayItems expectedCounts |> List.map (fun value -> Int32.Parse(value.GetRawText(), invariant))
            actual = values

    let private buildAndCalculate (oracleCase: JsonElement) =
        let failures = ResizeArray<ConstructorFailure>()
        let caseId = stringProperty oracleCase "id"
        let customerSymbols = property oracleCase "customers" |> arrayItems |> List.map stringValue
        let querySymbol = stringProperty oracleCase "query"
        let invoiceRecipes = property oracleCase "invoices" |> arrayItems
        let overrides = tryProperty oracleCase "customerOverrides"
        let overrideFor symbol =
            match overrides with
            | Some value -> tryProperty value symbol
            | None -> None

        let mutable store = Store.empty
        let customerIds = ResizeArray<CustomerId>()
        let productIds = ResizeArray<ProductId>()
        let invoiceIds = ResizeArray<InvoiceId>()
        let paymentIds = ResizeArray<PaymentId>()
        let paidInvoiceIds = ResizeArray<InvoiceId>()
        let subscriptionIds = ResizeArray<SubscriptionId>()
        let customerSymbolsById = ResizeArray<string>()

        for symbol in customerSymbols do
            let id = customerId symbol
            let defaults = overrideFor symbol
            let emailText = defaults |> Option.map (fun value -> optionalStringProperty value "email" $"{symbol}@example.test") |> Option.defaultValue $"{symbol}@example.test"
            let kind = defaults |> Option.map (fun value -> optionalStringProperty value "kind" "standard") |> Option.defaultValue "standard"
            let balanceText = defaults |> Option.map (fun value -> optionalStringProperty value "balance" "0") |> Option.defaultValue "0"
            let createdAtText = defaults |> Option.map (fun value -> optionalStringProperty value "createdAt" "2000-01-01T00:00:00.0000000Z") |> Option.defaultValue "2000-01-01T00:00:00.0000000Z"
            let email = Email.create emailText |> recordFailure failures $"Customer.create email {symbol}"
            let customer =
                Customer.create id email kind (Money.ofMinorUnits(parseMinorUnits balanceText)) (parseDate createdAtText)
                |> recordFailure failures $"Customer.create {symbol}"
            store <- Store.addCustomer customer store |> recordFailure failures $"Store.addCustomer {symbol}"
            customerIds.Add(id)
            customerSymbolsById.Add(symbol)

        for index, recipe in invoiceRecipes |> List.indexed do
            let invoiceSymbol = stringProperty recipe "id"
            let ownerSymbol = stringProperty recipe "owner"
            let amount = stringProperty recipe "amount" |> parseMinorUnits
            let paid = boolProperty recipe "paid"
            let owner = customerId ownerSymbol
            let productId = ProductId.parse (productGuid (index + 1)) |> domainValue $"parse product p{index + 1}"
            let productName = $"Reference product {index + 1}"
            let product = Product.create productId productName (Money.ofMinorUnits amount) |> recordFailure failures $"Product.create p{index + 1}"
            store <- Store.addProduct product store |> recordFailure failures $"Store.addProduct p{index + 1}"
            productIds.Add(productId)
            let invoiceId = invoiceId invoiceSymbol
            let updated, _ =
                Invoice.create store invoiceId owner [ productId, 1 ] (parseDate "2001-02-03T04:05:06.0000000Z")
                |> recordFailure failures $"Invoice.create {invoiceSymbol}"
            store <- updated
            invoiceIds.Add(invoiceId)
            if paid then
                let paymentNumber = paymentIds.Count + 1
                let paymentId = PaymentId.parse (paymentGuid paymentNumber) |> domainValue $"parse payment pay{paymentNumber}"
                let paymentAmount = Money.ofMinorUnits amount
                let provider = PaymentProvider.deterministic $"r09-reference-pay{paymentNumber:D3}"
                let updated, _ =
                    Payment.submit store provider paymentId invoiceId paymentAmount (parseDate "2001-02-03T04:05:06.0000000Z")
                    |> recordFailure failures $"Payment.submit pay{paymentNumber} for {invoiceSymbol}"
                store <- updated
                paymentIds.Add(paymentId)
                paidInvoiceIds.Add(invoiceId)

        let extra = tryProperty oracleCase "extraState"
        let extraCount (field: string) =
            match extra with
            | Some value ->
                match tryProperty value field with
                | Some count -> Int32.Parse(count.GetRawText(), invariant)
                | None -> 0
            | None -> 0
        let subscriptionCount = extraCount "subscriptions"
        let pendingEmailCount = extraCount "pendingEmails"
        let sentEmailCount = extraCount "sentEmails"
        if subscriptionCount < 0 || pendingEmailCount < 0 || sentEmailCount < 0 then
            fail "FIXTURE_RECIPE_INVALID" "Extra-state counts must be nonnegative."
        if subscriptionCount + pendingEmailCount + sentEmailCount > 0 then
            if customerIds.Count = 0 then
                fail "FIXTURE_UNSUPPORTED_EXTRA_STATE" "Extra state requires at least one customer."
            if productIds.Count = 0 && subscriptionCount > 0 then
                fail "FIXTURE_UNSUPPORTED_EXTRA_STATE" "The subscription recipe requires a product from an invoice recipe."

        for number in 1 .. subscriptionCount do
            let subId = SubscriptionId.parse (subscriptionGuid number) |> domainValue $"parse subscription s{number}"
            let start = parseDate "2001-02-03T00:00:00.0000000Z" |> fun date -> date.AddYears(number - 1)
            let expiry = start.AddYears(1)
            let updated, _ =
                Subscription.start store subId customerIds[0] productIds[0] "annual" start expiry
                |> recordFailure failures $"Subscription.start s{number}"
            store <- updated
            subscriptionIds.Add(subId)

        let emailCount = pendingEmailCount + sentEmailCount
        for number in 1 .. emailCount do
            let subject = $"R09 reference message {number:D3}"
            let body = $"Reference fixture message {number:D3}."
            let updated, _ = EmailOutbox.queue store customerIds[0] subject body |> recordFailure failures $"EmailOutbox.queue message {number:D3}"
            store <- updated
        for number in 1 .. sentEmailCount do
            store <- EmailOutbox.deliverNext store EmailProvider.deterministic |> recordFailure failures $"EmailOutbox.deliverNext {number}"

        let customerIdsList = customerIds |> Seq.toList
        let productIdsList = productIds |> Seq.toList
        let invoiceIdsList = invoiceIds |> Seq.toList
        let paymentIdsInRecipeOrder = paymentIds |> Seq.toList
        let subscriptionIdsList = subscriptionIds |> Seq.toList
        let storeBefore = projectStore store customerIdsList productIdsList subscriptionIdsList invoiceIdsList paymentIdsInRecipeOrder
        if not storeBefore.complete then
            fail "PROJECTION_COUNT_MISMATCH" "The public Store.summary counts do not match all recipe-created projected values."
        let recipeCountVector = [
            customerSymbols.Length
            invoiceRecipes.Length
            subscriptionCount
            invoiceRecipes.Length
            paymentIdsInRecipeOrder.Length
            pendingEmailCount
            sentEmailCount
        ]

        let serializedPaymentIds = storeBefore.payments |> List.map (fun payment -> payment.id)
        let paymentProjectionOrderMatchesRecipe =
            let projectedInvoiceIds = storeBefore.payments |> List.map (fun payment -> payment.invoiceId)
            let recipeInvoiceIds = paidInvoiceIds |> Seq.map InvoiceId.toString |> Seq.toList
            projectedInvoiceIds = recipeInvoiceIds
        let paymentSequence =
            storeBefore.payments
            |> List.map (fun payment -> {
                paymentId = payment.id
                invoiceId = payment.invoiceId
                amountMinorUnits = payment.amountMinorUnits
            })

        let paymentAmountsFor owner =
            paymentIdsInRecipeOrder
            |> List.choose (fun id ->
                let payment = Store.payment id store |> lookup "reference payment total"
                let invoice = Store.invoice (Payment.invoiceId payment) store |> lookup "reference payment invoice owner"
                if Invoice.customerId invoice = owner then Some(Payment.amount payment |> Money.minorUnits) else None)

        let totalFor owner =
            match Store.customer owner store with
            | None -> outcomeError "CUSTOMER_NOT_FOUND"
            | Some _ ->
                match checkedSum (paymentAmountsFor owner) with
                | Ok total -> outcomeOk (minorUnitsText total)
                | Error code -> outcomeError code

        let directId = customerId querySymbol
        let direct = totalFor directId
        let accountCustomerValue = Store.customer directId store
        let accountCustomerFound = accountCustomerValue.IsSome
        let emptyCustomerProjection = { id = ""; email = ""; kind = ""; balanceMinorUnits = ""; createdAt = "" }
        let accountCustomer = accountCustomerValue |> Option.map customerProjection |> Option.defaultValue emptyCustomerProjection
        let account =
            match accountCustomerValue with
            | None -> outcomeError "CUSTOMER_NOT_FOUND"
            | Some _ -> direct
        let customerPaidTotals =
            customerIdsList
            |> List.mapi (fun index id -> {
                customerId = customerSymbolsById[index]
                outcome = totalFor id
            })
        let dashboardTotal =
            customerPaidTotals
            |> List.fold (fun state total ->
                match state, total.outcome with
                | Error code, _ -> Error code
                | Ok _, { status = "error" } -> Error total.outcome.errorCode
                | Ok accumulated, { status = "ok" } ->
                    match checkedSum [ accumulated; Int64.Parse(total.outcome.value, invariant) ] with
                    | Ok sum -> Ok sum
                    | Error code -> Error code
                | Ok _, _ -> Error "REFERENCE_CALCULATION_ERROR") (Ok 0L)
        let metrics =
            match dashboardTotal with
            | Ok total -> outcomeOk (minorUnitsText total)
            | Error code -> outcomeError code
        let storeAfter = projectStore store customerIdsList productIdsList subscriptionIdsList invoiceIdsList paymentIdsInRecipeOrder
        let expected = property oracleCase "expected"
        let expectedMetrics = property expected "metrics"
        let directMatches = sameOutcome direct (property expected "direct")
        let accountMatches = sameOutcome account (property expected "account")
        let metricsMatches = sameOutcome metrics expectedMetrics
        let countsMatch = expectedCountsMatch storeBefore.countVector expectedMetrics
        let recipeCountsMatch = storeBefore.countVector = recipeCountVector
        let expectedCustomerPreserved =
            match accountCustomerValue with
            | None -> not accountCustomerFound
            | Some customer -> accountCustomer = customerProjection customer
        let stickyOrderVerified =
            if caseId = "overflow-remains-sticky" then
                let amounts = storeBefore.payments |> List.map (fun payment -> payment.amountMinorUnits)
                amounts = [ "9223372036854775807"; "1"; "10" ]
            else true
        let checks = {
            directMatchesOracle = directMatches
            accountMatchesOracle = accountMatches
            metricsMatchesOracle = metricsMatches
            metricsCountsMatchOracle = countsMatch
            metricsCountsMatchRecipe = recipeCountsMatch
            accountCustomerPreserved = expectedCustomerPreserved
            storeProjectionUnchanged = storeBefore = storeAfter
            projectionContainsEveryCountedValue = storeBefore.complete && storeAfter.complete
            paymentProjectionOrderMatchesRecipe = paymentProjectionOrderMatchesRecipe
            stickyOverflowAmountOrderVerified = stickyOrderVerified
        }
        let reference = {
            direct = direct
            account = account
            accountCustomer = accountCustomer
            accountCustomerFound = accountCustomerFound
            customerPaidTotals = customerPaidTotals
            metrics = metrics
            metricsCounts = storeBefore.countVector
            recipeCounts = recipeCountVector
            paidInvoiceRecipeOrder = paidInvoiceIds |> Seq.map InvoiceId.toString |> Seq.toList
            serializedPaymentIds = serializedPaymentIds
            paymentSequence = paymentSequence
            paymentProjectionBasis = "Public Store.payment lookups for every generated key, sorted by canonical GUID; the current F# Domain exposes no payment collection enumerator."
        }
        let checksAllPass =
            checks.directMatchesOracle
            && checks.accountMatchesOracle
            && checks.metricsMatchesOracle
            && checks.metricsCountsMatchOracle
            && checks.metricsCountsMatchRecipe
            && checks.accountCustomerPreserved
            && checks.storeProjectionUnchanged
            && checks.projectionContainsEveryCountedValue
            && checks.paymentProjectionOrderMatchesRecipe
            && checks.stickyOverflowAmountOrderVerified
        let output = {
            id = caseId
            status = if checksAllPass then "accepted-reference" else "oracle-mismatch"
            oracleExpected = property oracleCase "expected" |> fun value -> value.Clone()
            constructorFailures = List.ofSeq failures
            storeBefore = storeBefore
            storeAfter = storeAfter
            reference = reference
            checks = checks
            failure = null
        }
        output

    let private blockedCase (oracleCase: JsonElement) (failures: ConstructorFailure list) code message = {
        id = optionalStringProperty oracleCase "id" "(missing-id)"
        status = "blocked-constructor"
        oracleExpected =
            match tryProperty oracleCase "expected" with
            | Some value -> value.Clone()
            | None -> Unchecked.defaultof<JsonElement>
        constructorFailures = failures
        storeBefore = Unchecked.defaultof<StoreProjection>
        storeAfter = Unchecked.defaultof<StoreProjection>
        reference = Unchecked.defaultof<ReferenceCalculation>
        checks = Unchecked.defaultof<ComparisonChecks>
        failure = $"{code}: {message}"
    }

    let private blockedReferenceCase (oracleCase: JsonElement) code message = {
        id = optionalStringProperty oracleCase "id" "(missing-id)"
        status = "blocked-constructor"
        oracleExpected =
            match tryProperty oracleCase "expected" with
            | Some value -> value.Clone()
            | None -> Unchecked.defaultof<JsonElement>
        constructorFailures = []
        storeBefore = Unchecked.defaultof<StoreProjection>
        storeAfter = Unchecked.defaultof<StoreProjection>
        reference = Unchecked.defaultof<ReferenceCalculation>
        checks = Unchecked.defaultof<ComparisonChecks>
        failure = $"{code}: {message}"
    }

    let private sha256 (bytes: byte array) = SHA256.HashData(bytes) |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

    let private run (oraclePath: string) (outputPath: string) =
        let oraclePath = Path.GetFullPath(oraclePath)
        let outputPath = Path.GetFullPath(outputPath)
        if String.Equals(oraclePath, outputPath, StringComparison.OrdinalIgnoreCase) then
            fail "INVALID_ARGUMENTS" "The output path must not overwrite oracle.json."
        let oracleBytesBefore = File.ReadAllBytes(oraclePath)
        let oracleHashBefore = sha256 oracleBytesBefore
        use document = JsonDocument.Parse(oracleBytesBefore)
        let cases = property document.RootElement "cases" |> arrayItems
        let outputs =
            cases
            |> List.map (fun oracleCase ->
                try buildAndCalculate oracleCase
                with
                | ConstructorBlocked(failures, code, message) -> blockedCase oracleCase failures code message
                | ReferenceFailure(code, message) -> blockedReferenceCase oracleCase code message
                | error -> blockedReferenceCase oracleCase "ADAPTER_ERROR" (error.ToString()))
        let oracleBytesAfter = File.ReadAllBytes(oraclePath)
        let oracleHashAfter = sha256 oracleBytesAfter
        let inputUnchanged = oracleHashBefore = oracleHashAfter
        let acceptedCount = outputs |> List.filter (fun item -> item.status = "accepted-reference") |> List.length
        let blockedCount = outputs |> List.filter (fun item -> item.status = "blocked-constructor") |> List.length
        let mismatchCount = outputs |> List.filter (fun item -> item.status = "oracle-mismatch") |> List.length
        let status =
            if not inputUnchanged then "input-changed-during-run"
            elif blockedCount > 0 then "blocked-constructor"
            elif mismatchCount > 0 then "oracle-mismatch"
            else "all-reference-cases-match"
        let output = {
            schemaVersion = 1
            status = status
            oracleSha256Before = oracleHashBefore
            oracleSha256After = oracleHashAfter
            inputUnchanged = inputUnchanged
            caseSummary = {
                oracleCaseCount = cases.Length
                acceptedCaseCount = acceptedCount
                blockedCaseCount = blockedCount
                mismatchedCaseCount = mismatchCount
            }
            cases = outputs
        }
        let outputText = JsonSerializer.Serialize(output, options) + Environment.NewLine
        let outputParent = Path.GetDirectoryName(outputPath)
        if not (String.IsNullOrEmpty outputParent) then Directory.CreateDirectory(outputParent) |> ignore
        File.WriteAllText(outputPath, outputText, System.Text.UTF8Encoding(false))
        Console.WriteLine($"status={status}; cases={cases.Length}; accepted={acceptedCount}; blocked={blockedCount}; mismatched={mismatchCount}")
        match status with
        | "all-reference-cases-match" -> 0
        | "blocked-constructor" -> 2
        | "oracle-mismatch" -> 3
        | _ -> 4

    [<EntryPoint>]
    let main arguments =
        if arguments.Length <> 2 then
            Console.Error.WriteLine("Usage: ReferenceFixture <oracle.json path> <output.json path>")
            64
        else
            try run arguments[0] arguments[1]
            with
            | ReferenceFailure(code, message) ->
                Console.Error.WriteLine($"{code}: {message}")
                64
            | error ->
                Console.Error.WriteLine(error.ToString())
                70
