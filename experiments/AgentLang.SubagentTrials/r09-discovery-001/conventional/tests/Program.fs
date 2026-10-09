namespace AgentLang.Business.Tests

open System
open AgentLang.Business.Domain

module Program =
    let mutable private assertions = 0
    let mutable private groups = 0

    let private check condition message =
        assertions <- assertions + 1
        if not condition then failwith message

    let private equal expected actual message =
        assertions <- assertions + 1
        if expected <> actual then
            failwith $"{message}: expected {expected}, got {actual}"

    let private ok label = function
        | Ok value -> value
        | Error error -> failwith $"{label}: unexpected {DomainError.code error}: {DomainError.message error}"

    let private errorCode expected label = function
        | Error error ->
            assertions <- assertions + 1
            let actual = DomainError.code error
            if actual <> expected then
                failwith $"{label}: expected {expected}, got {actual}: {DomainError.message error}"
        | Ok _ -> failwith $"{label}: expected error {expected}, got success"

    let private group name action =
        action ()
        groups <- groups + 1
        printfn "PASS %s" name

    let private timestamp day = DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero).AddDays(float (day - 1))

    let private customerId value = CustomerId.parse value |> ok "customer id"
    let private subscriptionId value = SubscriptionId.parse value |> ok "subscription id"
    let private invoiceId value = InvoiceId.parse value |> ok "invoice id"
    let private productId value = ProductId.parse value |> ok "product id"
    let private paymentId value = PaymentId.parse value |> ok "payment id"

    let private primaryCustomerId = customerId "10000000-0000-0000-0000-000000000001"
    let private primaryProductId = productId "20000000-0000-0000-0000-000000000001"

    let private baseStore () =
        let address = Email.create "ada@example.test" |> ok "valid email"
        let customer = Customer.create primaryCustomerId address "regular" (Money.ofMinorUnits 1250L) (timestamp 1) |> ok "valid customer"
        let product = Product.create primaryProductId "Monthly plan" (Money.ofMinorUnits 199L) |> ok "valid product"
        let store =
            Store.empty
            |> Store.addCustomer customer
            |> ok "add customer"
            |> Store.addProduct product
            |> ok "add product"
        store

    let private testStrongIdentifiersAndEmailPolicy () =
        let expectedIds = [
            "10000000-0000-0000-0000-000000000001"
            "20000000-0000-0000-0000-000000000001"
        ]
        equal (Some primaryCustomerId) (CustomerId.parse expectedIds[0] |> Result.toOption) "CustomerId parses GUID"
        equal (Some primaryProductId) (ProductId.parse expectedIds[1] |> Result.toOption) "ProductId is a distinct nominal identifier"
        equal expectedIds[0] (CustomerId.toString primaryCustomerId) "CustomerId format is stable"
        errorCode "INVALID_IDENTIFIER" "bad identifier" (CustomerId.parse "customer-1")

        let accepted = [
            "ada@example.test"
            "first.last+tag@sub.example.com"
            "A_1%tag@example-domain.test"
        ]
        for value in accepted do
            check (Email.create value |> Result.isOk) $"accepted email {value}"

        let rejected = [
            ""
            "not-an-address"
            "a@b"
            "@example.test"
            "a..b@example.test"
            "a@example..test"
            "a b@example.test"
            "a@@example.test"
            "a@-example.test"
            "a@example-.test"
        ]
        for value in rejected do
            errorCode "INVALID_EMAIL" $"rejected email '{value}'" (Email.create value)
        errorCode "INVALID_EMAIL" "null email" (Email.create null)

        let address = Email.create "ada@example.test" |> ok "email"
        errorCode "INVALID_NAME" "blank customer kind" (Customer.create primaryCustomerId address "  " Money.zero (timestamp 1))

    let private testMoneyArithmeticAndPriceInvariants () =
        let total = Money.add (Money.ofMinorUnits 125L) (Money.ofMinorUnits 75L) |> ok "minor-unit addition"
        equal 200L (Money.minorUnits total) "integer arithmetic preserves exact minor units"
        let signed = Money.add (Money.ofMinorUnits -125L) total |> ok "signed money arithmetic"
        equal 75L (Money.minorUnits signed) "Money explicitly permits signed intermediate values"
        equal Money.zero (Money.sum [] |> ok "empty sum") "empty sum is exact zero"
        errorCode "MONEY_OVERFLOW" "checked addition" (Money.add (Money.ofMinorUnits Int64.MaxValue) (Money.ofMinorUnits 1L))
        errorCode "MONEY_OVERFLOW" "checked multiplication" (Money.multiplyByQuantity (Money.ofMinorUnits Int64.MaxValue) 2)
        errorCode "NEGATIVE_PRODUCT_PRICE" "negative product price" (Product.create (productId "20000000-0000-0000-0000-000000000099") "invalid" (Money.ofMinorUnits -1L))

    let private testImmutableStoreAndCustomerLookup () =
        let initial = Store.empty
        let email = Email.create "ada@example.test" |> ok "email"
        let customer = Customer.create primaryCustomerId email "premium" (Money.ofMinorUnits 5000L) (timestamp 1) |> ok "customer"
        let withCustomer = Store.addCustomer customer initial |> ok "insert customer"
        equal 0 (Store.summary initial).Customers "insert leaves prior immutable store unchanged"
        equal 1 (Store.summary withCustomer).Customers "insert creates new store state"
        equal (Some customer) (Store.customer primaryCustomerId withCustomer) "customer can be found by nominal id"
        equal primaryCustomerId (Customer.id customer) "Customer accessor preserves CustomerId"
        equal "ada@example.test" (Customer.email customer |> Email.value) "Customer accessor preserves Email"
        equal "premium" (Store.customer primaryCustomerId withCustomer |> Option.map Customer.kind |> Option.defaultValue "") "customer kind remains raw task input"
        equal 5000L (Store.customer primaryCustomerId withCustomer |> Option.map (Customer.balance >> Money.minorUnits) |> Option.defaultValue 0L) "customer balance remains integer minor-unit input"
        equal (timestamp 1) (Customer.createdAt customer) "Customer timestamp accessor has its entity type"

        let product = Product.create primaryProductId "Plan" (Money.ofMinorUnits 999L) |> ok "product"
        equal primaryProductId (Product.id product) "Product accessor preserves ProductId"
        equal "Plan" (Product.name product) "Product name accessor"
        equal 999L (Product.unitPrice product |> Money.minorUnits) "Product price accessor preserves Money"
        errorCode "DUPLICATE_CUSTOMER" "duplicate customer id" (Store.addCustomer customer withCustomer)
        equal 1 (Store.summary withCustomer).Customers "rejected duplicate does not mutate store"

    let private testSubscriptionLifecycle () =
        let store = baseStore ()
        let sid = subscriptionId "30000000-0000-0000-0000-000000000001"
        let missingCustomer = customerId "10000000-0000-0000-0000-000000000099"
        errorCode "CUSTOMER_NOT_FOUND" "subscription customer reference" (Subscription.start store sid missingCustomer primaryProductId "monthly" (timestamp 2) (timestamp 32))
        errorCode "PRODUCT_NOT_FOUND" "subscription product reference" (Subscription.start store sid primaryCustomerId (productId "20000000-0000-0000-0000-000000000099") "monthly" (timestamp 2) (timestamp 32))
        errorCode "INVALID_SUBSCRIPTION_TERM" "blank term" (Subscription.start store sid primaryCustomerId primaryProductId " " (timestamp 2) (timestamp 32))
        errorCode "SUBSCRIPTION_EXPIRY_MUST_FOLLOW_START" "expiry before start" (Subscription.start store sid primaryCustomerId primaryProductId "annual" (timestamp 32) (timestamp 2))

        let activeStore, active = Subscription.start store sid primaryCustomerId primaryProductId "annual" (timestamp 2) (timestamp 32) |> ok "start subscription"
        equal sid (Subscription.id active) "Subscription accessor preserves SubscriptionId"
        equal primaryCustomerId (Subscription.customerId active) "Subscription accessor preserves CustomerId"
        equal primaryProductId (Subscription.productId active) "Subscription accessor preserves ProductId"
        equal Active (Subscription.status active) "new subscription starts active"
        equal "annual" (Subscription.term active) "subscription term remains raw task input"
        equal (timestamp 2) (Subscription.startedAt active) "subscription start accessor"
        equal (timestamp 32) (Subscription.expiresAt active) "subscription expiry remains explicit task input"
        equal 0 (Store.summary store).Subscriptions "start leaves the input store unchanged"
        equal (Some Active) (Store.subscription sid activeStore |> Option.map Subscription.status) "active subscription is persisted"
        errorCode "DUPLICATE_SUBSCRIPTION" "duplicate subscription id" (Subscription.start activeStore sid primaryCustomerId primaryProductId "annual" (timestamp 3) (timestamp 33))
        errorCode "CANCEL_BEFORE_START" "cancellation precedes start" (Subscription.cancel activeStore sid (timestamp 1))

        let cancelledStore, cancelled = Subscription.cancel activeStore sid (timestamp 4) |> ok "cancel active subscription"
        equal Cancelled (Subscription.status cancelled) "cancellation changes lifecycle state"
        equal (Some(timestamp 4)) (Subscription.cancelledAt cancelled) "cancellation timestamp is recorded"
        errorCode "SUBSCRIPTION_ALREADY_CANCELLED" "cannot cancel twice" (Subscription.cancel cancelledStore sid (timestamp 5))
        equal Cancelled (Store.subscription sid cancelledStore |> Option.map Subscription.status |> Option.defaultValue Active) "stored lifecycle state remains cancelled"

    let private testInvoiceTotalsAndValidation () =
        let store = baseStore ()
        let secondProductId = productId "20000000-0000-0000-0000-000000000002"
        let secondProduct = Product.create secondProductId "Setup" (Money.ofMinorUnits 225L) |> ok "second product"
        let store = Store.addProduct secondProduct store |> ok "add second product"
        let iid = invoiceId "40000000-0000-0000-0000-000000000001"
        let cart = [ primaryProductId, 3; secondProductId, 2 ]
        let invoiced, invoice = Invoice.create store iid primaryCustomerId cart (timestamp 5) |> ok "create invoice"
        equal iid (Invoice.id invoice) "Invoice accessor preserves InvoiceId"
        equal primaryCustomerId (Invoice.customerId invoice) "Invoice accessor preserves CustomerId"
        equal 1047L (Money.minorUnits (Invoice.total invoice)) "invoice sums integer minor-unit line totals"
        let invoiceLines = Invoice.lines invoice
        equal [ 3; 2 ] (invoiceLines |> List.map InvoiceLine.quantity) "invoice snapshots the requested quantities"
        equal primaryProductId (InvoiceLine.productId invoiceLines.Head) "line accessor preserves ProductId"
        equal "Monthly plan" (InvoiceLine.description invoiceLines.Head) "line description is snapshotted"
        equal 199L (InvoiceLine.unitPrice invoiceLines.Head |> Money.minorUnits) "line accessor preserves Money"
        equal 597L (InvoiceLine.lineTotal invoiceLines.Head |> Money.minorUnits) "line total is exact minor-unit multiplication"
        equal Open (Invoice.status invoice) "new invoice is open"
        equal (timestamp 5) (Invoice.createdAt invoice) "invoice timestamp accessor"
        equal 0 (Store.summary store).Invoices "invoice creation is an immutable transition"
        equal 1 (Store.summary invoiced).Invoices "invoice is added to new store"

        errorCode "EMPTY_INVOICE" "empty cart" (Invoice.create store iid primaryCustomerId [] (timestamp 5))
        errorCode "INVALID_QUANTITY" "zero quantity" (Invoice.create store iid primaryCustomerId [ primaryProductId, 0 ] (timestamp 5))
        errorCode "INVALID_QUANTITY" "negative quantity" (Invoice.create store iid primaryCustomerId [ primaryProductId, -2 ] (timestamp 5))
        errorCode "PRODUCT_NOT_FOUND" "unknown cart product" (Invoice.create store iid primaryCustomerId [ productId "20000000-0000-0000-0000-000000000099", 1 ] (timestamp 5))

        let maxProductId = productId "20000000-0000-0000-0000-000000000003"
        let maxProduct = Product.create maxProductId "Large amount" (Money.ofMinorUnits Int64.MaxValue) |> ok "maximum product"
        let overflowStore = Store.addProduct maxProduct store |> ok "add maximum product"
        errorCode "MONEY_OVERFLOW" "cart line multiplication overflow" (Invoice.create overflowStore iid primaryCustomerId [ maxProductId, 2 ] (timestamp 5))

    let private testPaymentProviderBoundaryAndReceipt () =
        let store = baseStore ()
        let iid = invoiceId "40000000-0000-0000-0000-000000000002"
        let store, invoice = Invoice.create store iid primaryCustomerId [ primaryProductId, 3 ] (timestamp 6) |> ok "invoice for payment"
        let pid = paymentId "50000000-0000-0000-0000-000000000001"
        let total = Invoice.total invoice
        let receipt = PaymentReceipt.create "standalone-receipt" total |> ok "receipt value"
        equal "standalone-receipt" (PaymentReceipt.reference receipt) "receipt reference accessor"
        equal total (PaymentReceipt.amount receipt) "receipt amount accessor preserves Money"
        let mutable providerCalls = 0
        let successfulProvider = {
            Charge = fun request ->
                providerCalls <- providerCalls + 1
                PaymentReceipt.create "charge-0001" request.Amount
                |> Result.mapError DomainError.message
        }
        let mismatchedProvider = {
            Charge = fun _ ->
                providerCalls <- providerCalls + 1
                PaymentReceipt.create "charge-wrong" (Money.ofMinorUnits 1L)
                |> Result.mapError DomainError.message
        }
        let failingProvider = {
            Charge = fun _ ->
                providerCalls <- providerCalls + 1
                Error "simulated decline"
        }

        errorCode "PAYMENT_AMOUNT_MUST_BE_POSITIVE" "zero payment" (Payment.submit store successfulProvider pid iid Money.zero (timestamp 7))
        errorCode "PAYMENT_AMOUNT_MUST_BE_POSITIVE" "negative payment" (Payment.submit store successfulProvider pid iid (Money.ofMinorUnits -1L) (timestamp 7))
        errorCode "PAYMENT_AMOUNT_MISMATCH" "partial payment is not accepted" (Payment.submit store successfulProvider pid iid (Money.ofMinorUnits 596L) (timestamp 7))
        equal 0 providerCalls "invalid payment requests never reach the provider"

        errorCode "PAYMENT_PROVIDER_FAILURE" "provider decline" (Payment.submit store failingProvider pid iid total (timestamp 7))
        equal 1 providerCalls "provider is called only after domain validation"
        equal 0 (Store.summary store).Payments "failed provider call leaves input store unchanged"

        errorCode "INVALID_PROVIDER_RECEIPT" "provider receipt amount mismatch" (Payment.submit store mismatchedProvider pid iid total (timestamp 7))
        equal 2 providerCalls "mismatched receipt was returned by the injected provider"
        equal Open (Store.invoice iid store |> Option.map Invoice.status |> Option.defaultValue Paid) "rejected receipt leaves invoice open"

        let paidStore, payment = Payment.submit store successfulProvider pid iid total (timestamp 7) |> ok "settle invoice"
        equal 3 providerCalls "one provider call for accepted charge"
        equal total (Payment.amount payment) "payment records exact amount"
        equal "charge-0001" (Payment.providerReference payment) "provider receipt reference is retained"
        equal Paid (Store.invoice iid paidStore |> Option.map Invoice.status |> Option.defaultValue Open) "successful receipt settles invoice"
        equal 1 (Store.summary paidStore).Payments "successful payment is recorded"
        equal (Some pid) (Store.payment pid paidStore |> Option.map Payment.id) "payment can be found using nominal PaymentId"
        equal iid (Payment.invoiceId payment) "Payment accessor preserves InvoiceId"
        equal (timestamp 7) (Payment.paidAt payment) "payment timestamp accessor"
        equal Open (Store.invoice iid store |> Option.map Invoice.status |> Option.defaultValue Paid) "successful transition leaves the old store unchanged"

        let secondId = paymentId "50000000-0000-0000-0000-000000000002"
        errorCode "INVOICE_ALREADY_PAID" "cannot charge a paid invoice again" (Payment.submit paidStore successfulProvider secondId iid total (timestamp 8))
        equal 3 providerCalls "already-paid invoice is rejected before provider call"

    let private testEmailOutboxAndProviderIsolation () =
        let store = baseStore ()
        let queued, message = EmailOutbox.queue store primaryCustomerId " Renewal notice " " Your invoice is ready. " |> ok "queue email"
        equal 0 (Store.summary store).PendingEmails "queueing leaves prior state unchanged"
        equal 1 (Store.summary queued).PendingEmails "message is placed in outbox"
        equal "Renewal notice" (EmailMessage.subject message) "message subject is normalized"
        equal "ada@example.test" (EmailMessage.toAddress message |> Email.value) "queued message uses validated customer address"
        errorCode "CUSTOMER_NOT_FOUND" "queue email for missing customer" (EmailOutbox.queue store (customerId "10000000-0000-0000-0000-000000000099") "subject" "body")
        errorCode "INVALID_EMAIL_MESSAGE" "empty email body" (EmailMessage.create (Email.create "ada@example.test" |> ok "email") "subject" " ")
        errorCode "INVALID_EMAIL_MESSAGE" "null email subject" (EmailMessage.create (Email.create "ada@example.test" |> ok "email") null "body")
        errorCode "INVALID_EMAIL_MESSAGE" "null email body" (EmailMessage.create (Email.create "ada@example.test" |> ok "email") "subject" null)

        let mutable sends = 0
        let offline = {
            Send = fun _ ->
                sends <- sends + 1
                Error "offline"
        }
        errorCode "EMAIL_PROVIDER_FAILURE" "provider failure retains queued message" (EmailOutbox.deliverNext queued offline)
        equal 1 sends "delivery calls only the injected provider"
        equal 1 (Store.summary queued).PendingEmails "failed delivery leaves original outbox intact"

        let delivered = EmailOutbox.deliverNext queued EmailProvider.deterministic |> ok "deliver queued email"
        equal 0 (Store.summary delivered).PendingEmails "successful delivery removes message from outbox"
        equal 1 (Store.summary delivered).SentEmails "successful delivery records message"
        equal "Your invoice is ready." (Store.sentEmails delivered |> List.head |> EmailMessage.body) "sent message remains inspectable"
        equal 1 (Store.summary queued).PendingEmails "delivery is an immutable store transition"
        errorCode "NO_PENDING_EMAIL" "empty outbox" (EmailOutbox.deliverNext delivered EmailProvider.deterministic)

    let private testR09InitialPublicSummarySmoke () =
        let c1 = customerId "10000000-0000-0000-0000-000000000001"
        let c2 = customerId "10000000-0000-0000-0000-000000000002"
        let c9 = customerId "10000000-0000-0000-0000-000000000009"
        let p1 = productId "40000000-0000-0000-0000-000000000001"
        let p2 = productId "40000000-0000-0000-0000-000000000002"
        let p3 = productId "40000000-0000-0000-0000-000000000003"
        let address value = Email.create value |> ok "summary smoke email"
        let makeCustomer id email kind balance =
            Customer.create id (address email) kind (Money.ofMinorUnits balance) (timestamp 1)
            |> ok "summary smoke customer"
        let makeProduct id name amount =
            Product.create id name (Money.ofMinorUnits amount) |> ok "summary smoke product"
        let addPaidInvoice owner invoiceKey productKey paymentKey reference store =
            let invoiceKey = invoiceId invoiceKey
            let paymentKey = paymentId paymentKey
            let invoiced, invoice =
                Invoice.create store invoiceKey owner [ productKey, 1 ] (timestamp 2)
                |> ok "summary smoke invoice"
            Payment.submit invoiced (PaymentProvider.deterministic reference) paymentKey invoiceKey (Invoice.total invoice) (timestamp 3)
            |> ok "summary smoke payment"
            |> fst

        let customer1 = makeCustomer c1 "c1@example.test" "premium" 77L
        let customer2 = makeCustomer c2 "c2@example.test" "standard" 88L
        let source =
            Store.empty
            |> Store.addCustomer customer1 |> ok "add summary smoke c1"
            |> Store.addCustomer customer2 |> ok "add summary smoke c2"
            |> Store.addProduct (makeProduct p1 "p1" 250L) |> ok "add summary smoke p1"
            |> Store.addProduct (makeProduct p2 "p2" 900L) |> ok "add summary smoke p2"
            |> Store.addProduct (makeProduct p3 "p3" 125L) |> ok "add summary smoke p3"
        let paid =
            source
            |> addPaidInvoice c1 "20000000-0000-0000-0000-000000000001" p1 "30000000-0000-0000-0000-000000000001" "smoke-1"
            |> addPaidInvoice c2 "20000000-0000-0000-0000-000000000002" p2 "30000000-0000-0000-0000-000000000002" "smoke-2"
            |> addPaidInvoice c1 "20000000-0000-0000-0000-000000000003" p3 "30000000-0000-0000-0000-000000000003" "smoke-3"

        let account = Customer.accountSummary paid c1 |> ok "owned customer summary"
        equal customer1 account.Customer "account summary carries the full original Customer"
        equal 375L (Money.minorUnits account.PaidTotal) "account summary excludes foreign payments and sums only owned invoices"
        let metrics = Store.customerMetrics paid |> ok "dashboard customer metrics"
        equal 2 metrics.Customers "dashboard customer count"
        equal 3 metrics.Products "dashboard product count"
        equal 0 metrics.Subscriptions "dashboard subscription count"
        equal 3 metrics.Invoices "dashboard invoice count"
        equal 3 metrics.Payments "dashboard payment count"
        equal 0 metrics.PendingEmails "dashboard pending email count"
        equal 0 metrics.SentEmails "dashboard sent email count"
        equal 1275L (Money.minorUnits metrics.PaidTotal) "dashboard sums each customer's owned payments"
        equal 0 (Store.summary source).Payments "summary reads leave the original store unchanged"

        let unpaidCustomer = makeCustomer c9 "c9@example.test" "standard" 0L
        let noPayments = Store.empty |> Store.addCustomer unpaidCustomer |> ok "add known customer without payments"
        let noPaymentSummary = Customer.accountSummary noPayments c9 |> ok "known customer without payments"
        equal unpaidCustomer noPaymentSummary.Customer "zero summary retains its complete customer"
        equal 0L (Money.minorUnits noPaymentSummary.PaidTotal) "known customer without payments is zero"
        errorCode "CUSTOMER_NOT_FOUND" "unknown customer summary" (Customer.accountSummary noPayments c1)

        let maxProduct = makeProduct p1 "max" Int64.MaxValue
        let oneProduct = makeProduct p2 "one" 1L
        let tenProduct = makeProduct p3 "ten" 10L
        let overflowBase =
            Store.empty
            |> Store.addCustomer customer1 |> ok "add overflow smoke c1"
            |> Store.addCustomer customer2 |> ok "add overflow smoke c2"
            |> Store.addProduct maxProduct |> ok "add overflow smoke max product"
            |> Store.addProduct oneProduct |> ok "add overflow smoke one product"
            |> Store.addProduct tenProduct |> ok "add overflow smoke ten product"
        let overflowStore =
            overflowBase
            |> addPaidInvoice c2 "20000000-0000-0000-0000-000000000011" p1 "30000000-0000-0000-0000-000000000011" "overflow-max"
            |> addPaidInvoice c2 "20000000-0000-0000-0000-000000000012" p2 "30000000-0000-0000-0000-000000000012" "overflow-one"
            |> addPaidInvoice c2 "20000000-0000-0000-0000-000000000013" p3 "30000000-0000-0000-0000-000000000013" "overflow-ten"
        equal 0L (Money.minorUnits (Customer.accountSummary overflowStore c1 |> ok "foreign overflow does not affect account").PaidTotal) "another customer's overflow is isolated"
        errorCode "MONEY_OVERFLOW" "customer overflow remains sticky after max, one, ten" (Customer.accountSummary overflowStore c2)
        errorCode "CUSTOMER_NOT_FOUND" "unknown customer takes precedence over foreign overflow" (Customer.accountSummary overflowStore c9)
        errorCode "MONEY_OVERFLOW" "dashboard includes foreign customer overflow" (Store.customerMetrics overflowStore)

        let crossOwnerStore =
            Store.empty
            |> Store.addCustomer customer1 |> ok "add cross-owner c1"
            |> Store.addCustomer customer2 |> ok "add cross-owner c2"
            |> Store.addProduct maxProduct |> ok "add cross-owner max product"
            |> Store.addProduct oneProduct |> ok "add cross-owner one product"
        let crossOwnerPaid =
            crossOwnerStore
            |> addPaidInvoice c1 "20000000-0000-0000-0000-000000000021" p1 "30000000-0000-0000-0000-000000000021" "cross-max"
            |> addPaidInvoice c2 "20000000-0000-0000-0000-000000000022" p2 "30000000-0000-0000-0000-000000000022" "cross-one"
        equal Int64.MaxValue (Money.minorUnits (Customer.accountSummary crossOwnerPaid c1 |> ok "max customer total fits").PaidTotal) "individual max total is representable"
        equal 1L (Money.minorUnits (Customer.accountSummary crossOwnerPaid c2 |> ok "one customer total fits").PaidTotal) "individual one-unit total is representable"
        errorCode "MONEY_OVERFLOW" "dashboard checked sum of customers overflows" (Store.customerMetrics crossOwnerPaid)

    let private helperTestCustomer id address =
        let email = Email.create address |> ok "paid-total helper email"
        Customer.create id email "standard" Money.zero (timestamp 1) |> ok "paid-total helper customer"

    let private helperTestProduct id amount =
        Product.create id "Paid-total helper product" (Money.ofMinorUnits amount)
        |> ok "paid-total helper product"

    let private helperTestStore customers products =
        let withCustomers =
            customers
            |> List.fold (fun store customer -> Store.addCustomer customer store |> ok "add paid-total helper customer") Store.empty
        products
        |> List.fold (fun store product -> Store.addProduct product store |> ok "add paid-total helper product") withCustomers

    let private helperTestPaidInvoice store owner invoiceKey product paymentKey =
        let invoiceKey = invoiceId invoiceKey
        let paymentKey = paymentId paymentKey
        let invoiced, invoice =
            Invoice.create store invoiceKey owner [ product, 1 ] (timestamp 2)
            |> ok "create paid-total helper invoice"
        let paid, payment =
            Payment.submit
                invoiced
                (PaymentProvider.deterministic (PaymentId.toString paymentKey))
                paymentKey
                invoiceKey
                (Invoice.total invoice)
                (timestamp 3)
            |> ok "pay paid-total helper invoice"
        paid, payment

    let private testCustomerPaidTotalFiltersOwnedPayments () =
        let c1 = customerId "10000000-0000-0000-0000-000000000001"
        let c2 = customerId "10000000-0000-0000-0000-000000000002"
        let p1 = productId "40000000-0000-0000-0000-000000000001"
        let p2 = productId "40000000-0000-0000-0000-000000000002"
        let p3 = productId "40000000-0000-0000-0000-000000000003"
        let c1Value = helperTestCustomer c1 "paid-total-c1@example.test"
        let c2Value = helperTestCustomer c2 "paid-total-c2@example.test"
        let source =
            helperTestStore [ c1Value; c2Value ] [
                helperTestProduct p1 10L
                helperTestProduct p2 Int64.MaxValue
                helperTestProduct p3 500L
            ]
        let withOwned, _ =
            helperTestPaidInvoice source c1 "20000000-0000-0000-0000-000000000001" p1 "30000000-0000-0000-0000-000000000001"
        let withForeign, _ =
            helperTestPaidInvoice withOwned c2 "20000000-0000-0000-0000-000000000002" p2 "30000000-0000-0000-0000-000000000002"
        let withOpenInvoice, _ =
            Invoice.create withForeign (invoiceId "20000000-0000-0000-0000-000000000003") c1 [ p3, 1 ] (timestamp 4)
            |> ok "create open paid-total helper invoice"
        equal 10L
            (Store.customerPaidTotal withOpenInvoice c1 |> ok "owned paid total" |> Money.minorUnits)
            "helper counts only paid invoices owned by the requested customer"

    let private testCustomerPaidTotalKnownCustomerWithoutPayments () =
        let c1 = customerId "10000000-0000-0000-0000-000000000001"
        let p1 = productId "40000000-0000-0000-0000-000000000001"
        let customer = helperTestCustomer c1 "paid-total-empty@example.test"
        let product = helperTestProduct p1 950L
        let store = helperTestStore [ customer ] [ product ]
        let withOpenInvoice, _ =
            Invoice.create store (invoiceId "20000000-0000-0000-0000-000000000001") c1 [ p1, 1 ] (timestamp 2)
            |> ok "create unpaid open invoice"
        equal 0L
            (Store.customerPaidTotal withOpenInvoice c1 |> ok "known customer paid total" |> Money.minorUnits)
            "open invoice total is not a payment"

    let private testCustomerPaidTotalOverflowRemainsSticky () =
        let c1 = customerId "10000000-0000-0000-0000-000000000001"
        let p1 = productId "40000000-0000-0000-0000-000000000001"
        let p2 = productId "40000000-0000-0000-0000-000000000002"
        let p3 = productId "40000000-0000-0000-0000-000000000003"
        let customer = helperTestCustomer c1 "paid-total-overflow@example.test"
        let store =
            helperTestStore [ customer ] [
                helperTestProduct p1 Int64.MaxValue
                helperTestProduct p2 1L
                helperTestProduct p3 2L
            ]
        let withFirst, _ =
            helperTestPaidInvoice store c1 "20000000-0000-0000-0000-000000000011" p1 "30000000-0000-0000-0000-000000000011"
        let withSecond, _ =
            helperTestPaidInvoice withFirst c1 "20000000-0000-0000-0000-000000000012" p2 "30000000-0000-0000-0000-000000000012"
        let withThird, _ =
            helperTestPaidInvoice withSecond c1 "20000000-0000-0000-0000-000000000013" p3 "30000000-0000-0000-0000-000000000013"
        errorCode "MONEY_OVERFLOW" "public helper preserves overflow through a later payment" (Store.customerPaidTotal withThird c1)

    let private testCustomerPaidTotalUnknownCustomer () =
        let unknown = customerId "10000000-0000-0000-0000-000000000099"
        errorCode "CUSTOMER_NOT_FOUND" "public helper checks customer existence before folding" (Store.customerPaidTotal Store.empty unknown)

    let private testCustomerPaidTotalStepAddsMatchingPayment () =
        let c1 = customerId "10000000-0000-0000-0000-000000000001"
        let p1 = productId "40000000-0000-0000-0000-000000000001"
        let customer = helperTestCustomer c1 "paid-total-step-match@example.test"
        let store = helperTestStore [ customer ] [ helperTestProduct p1 7L ]
        let paid, payment =
            helperTestPaidInvoice store c1 "20000000-0000-0000-0000-000000000021" p1 "30000000-0000-0000-0000-000000000021"
        let state = { CustomerId = c1; Store = paid; Outcome = Ok(Money.ofMinorUnits 4L) }
        let updated = Store.customerPaidTotalStep state payment
        equal 11L (updated.Outcome |> ok "matching step total" |> Money.minorUnits) "matching invoice owner adds payment amount"

    let private testCustomerPaidTotalStepMissingInvoiceIsDefensive () =
        let c1 = customerId "10000000-0000-0000-0000-000000000001"
        let p1 = productId "40000000-0000-0000-0000-000000000001"
        let customer = helperTestCustomer c1 "paid-total-step-missing@example.test"
        let store = helperTestStore [ customer ] [ helperTestProduct p1 1L ]
        let targetInvoice = invoiceId "20000000-0000-0000-0000-000000000024"
        let invoiceStore, payment =
            helperTestPaidInvoice store c1 (InvoiceId.toString targetInvoice) p1 "30000000-0000-0000-0000-000000000024"
        let state = { CustomerId = c1; Store = store; Outcome = Ok(Money.ofMinorUnits 9L) }
        let updated = Store.customerPaidTotalStep state payment
        equal 9L (updated.Outcome |> ok "missing-invoice defensive total" |> Money.minorUnits) "standalone defensive step ignores a payment with no invoice in its state Store"
        check (Store.invoice targetInvoice store |> Option.isNone) "standalone step Store has no linked invoice"
        check (Store.invoice targetInvoice invoiceStore |> Option.isSome) "the Payment was created through the public operation in its original Store"

    let private testCustomerPaidTotalStepIgnoresOtherOwner () =
        let c1 = customerId "10000000-0000-0000-0000-000000000001"
        let c2 = customerId "10000000-0000-0000-0000-000000000002"
        let p1 = productId "40000000-0000-0000-0000-000000000001"
        let c1Value = helperTestCustomer c1 "paid-total-step-c1@example.test"
        let c2Value = helperTestCustomer c2 "paid-total-step-c2@example.test"
        let store = helperTestStore [ c1Value; c2Value ] [ helperTestProduct p1 Int64.MaxValue ]
        let paid, payment =
            helperTestPaidInvoice store c2 "20000000-0000-0000-0000-000000000022" p1 "30000000-0000-0000-0000-000000000022"
        let state = { CustomerId = c1; Store = paid; Outcome = Ok(Money.ofMinorUnits 10L) }
        let updated = Store.customerPaidTotalStep state payment
        equal 10L (updated.Outcome |> ok "foreign step total" |> Money.minorUnits) "foreign invoice is ignored even when adding it would overflow"

    let private testCustomerPaidTotalStepRetainsPriorError () =
        let c1 = customerId "10000000-0000-0000-0000-000000000001"
        let p1 = productId "40000000-0000-0000-0000-000000000001"
        let customer = helperTestCustomer c1 "paid-total-step-error@example.test"
        let store = helperTestStore [ customer ] [ helperTestProduct p1 1L ]
        let paid, payment =
            helperTestPaidInvoice store c1 "20000000-0000-0000-0000-000000000023" p1 "30000000-0000-0000-0000-000000000023"
        let earlierError = PaymentProviderFailure "Earlier accumulation failed."
        let state = { CustomerId = c1; Store = paid; Outcome = Error earlierError }
        let updated = Store.customerPaidTotalStep state payment
        equal (Error earlierError) updated.Outcome "standalone pre-existing error state is returned unchanged"

    let private testCustomerPaidTotalStepReturnsOverflow () =
        let c1 = customerId "10000000-0000-0000-0000-000000000001"
        let p1 = productId "40000000-0000-0000-0000-000000000001"
        let customer = helperTestCustomer c1 "paid-total-step-overflow@example.test"
        let store = helperTestStore [ customer ] [ helperTestProduct p1 1L ]
        let paid, payment =
            helperTestPaidInvoice store c1 "20000000-0000-0000-0000-000000000025" p1 "30000000-0000-0000-0000-000000000025"
        let state = { CustomerId = c1; Store = paid; Outcome = Ok(Money.ofMinorUnits Int64.MaxValue) }
        let updated = Store.customerPaidTotalStep state payment
        errorCode "MONEY_OVERFLOW" "matching step uses checked Money.add" updated.Outcome

    [<EntryPoint>]
    let main _ =
        try
            group "nominal identifiers and Email policy" testStrongIdentifiersAndEmailPolicy
            group "checked minor-unit arithmetic and price invariants" testMoneyArithmeticAndPriceInvariants
            group "immutable store and lookup" testImmutableStoreAndCustomerLookup
            group "subscription lifecycle" testSubscriptionLifecycle
            group "invoice totals and validation" testInvoiceTotalsAndValidation
            group "payment provider boundary and receipt" testPaymentProviderBoundaryAndReceipt
            group "email outbox and provider isolation" testEmailOutboxAndProviderIsolation
            group "initial public customer summary smoke" testR09InitialPublicSummarySmoke
            group "helper total filters by invoice owner and paid payment" testCustomerPaidTotalFiltersOwnedPayments
            group "helper total for known customer without payments" testCustomerPaidTotalKnownCustomerWithoutPayments
            group "helper public overflow remains sticky" testCustomerPaidTotalOverflowRemainsSticky
            group "helper public unknown customer" testCustomerPaidTotalUnknownCustomer
            group "helper step adds matching payment" testCustomerPaidTotalStepAddsMatchingPayment
            group "defensive standalone step ignores missing invoice" testCustomerPaidTotalStepMissingInvoiceIsDefensive
            group "helper step ignores another customer's payment" testCustomerPaidTotalStepIgnoresOtherOwner
            group "standalone helper step retains prior error" testCustomerPaidTotalStepRetainsPriorError
            group "helper step reports checked overflow" testCustomerPaidTotalStepReturnsOverflow
            printfn "Business reference: %d groups, %d assertions" groups assertions
            0
        with error ->
            eprintfn "FAIL after %d assertions: %s" assertions error.Message
            1
