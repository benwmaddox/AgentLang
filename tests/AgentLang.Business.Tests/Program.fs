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

    let private testOccupiedPeriodsAndSubscriptionOverlap () =
        let period start finish label =
            match OccupiedPeriod.tryCreate start finish with
            | Some value -> value
            | None -> failwith $"{label}: expected a nonempty occupied period"

        let baseTime = timestamp 1
        let fromDay day = baseTime.AddDays(float day)
        let fromTick tick = baseTime.AddTicks(int64 tick)

        check (OccupiedPeriod.tryCreate baseTime baseTime |> Option.isNone) "occupied period rejects equal endpoints"
        check (OccupiedPeriod.tryCreate (baseTime.AddTicks 1L) baseTime |> Option.isNone) "occupied period rejects reversed endpoints"

        let offsetStart = DateTimeOffset(2026, 1, 1, 7, 0, 0, TimeSpan.FromHours(-5.0))
        let offsetFinish = DateTimeOffset(2026, 1, 1, 18, 0, 0, TimeSpan.FromHours(4.0))
        let offsetPeriod = period offsetStart offsetFinish "offset period"
        equal baseTime (OccupiedPeriod.start offsetPeriod) "occupied period normalizes its start to UTC"
        equal (baseTime.AddHours 2.0) (OccupiedPeriod.finish offsetPeriod) "occupied period normalizes its finish to UTC"
        equal TimeSpan.Zero (OccupiedPeriod.start offsetPeriod).Offset "occupied start exposes a zero offset"
        equal TimeSpan.Zero (OccupiedPeriod.finish offsetPeriod).Offset "occupied finish exposes a zero offset"

        // A finite discrete-day oracle independently checks every pair of
        // intervals whose boundaries lie in this bounded day range.
        for leftStart in 0 .. 4 do
            for leftFinish in leftStart + 1 .. 5 do
                let left = period (fromDay leftStart) (fromDay leftFinish) "day interval"
                let leftDays = [ leftStart .. leftFinish - 1 ] |> Set.ofList
                for rightStart in 0 .. 4 do
                    for rightFinish in rightStart + 1 .. 5 do
                        let right = period (fromDay rightStart) (fromDay rightFinish) "day interval"
                        let rightDays = [ rightStart .. rightFinish - 1 ] |> Set.ofList
                        let expected = not (Set.intersect leftDays rightDays |> Set.isEmpty)
                        equal expected (OccupiedPeriod.overlaps left right) "day-set oracle matches half-open overlap"

        // The same oracle at 100 ns precision covers single-tick intervals
        // and boundaries that differ by exactly one tick.
        for leftStart in 0 .. 5 do
            for leftFinish in leftStart + 1 .. 6 do
                let left = period (fromTick leftStart) (fromTick leftFinish) "tick interval"
                let leftTicks = [ leftStart .. leftFinish - 1 ] |> Set.ofList
                for rightStart in 0 .. 5 do
                    for rightFinish in rightStart + 1 .. 6 do
                        let right = period (fromTick rightStart) (fromTick rightFinish) "tick interval"
                        let rightTicks = [ rightStart .. rightFinish - 1 ] |> Set.ofList
                        let expected = not (Set.intersect leftTicks rightTicks |> Set.isEmpty)
                        equal expected (OccupiedPeriod.overlaps left right) "tick-set oracle matches half-open overlap"

        let store = baseStore ()
        let firstId = subscriptionId "30000000-0000-0000-0000-000000000010"
        let firstStart = timestamp 2
        let firstFinish = timestamp 4
        let occupiedStore, active =
            Subscription.start store firstId primaryCustomerId primaryProductId "monthly" firstStart firstFinish
            |> ok "start first occupied subscription"
        let activePeriod = Subscription.occupiedPeriod active |> Option.defaultWith (fun () -> failwith "active subscription has no occupied period")
        equal firstStart (OccupiedPeriod.start activePeriod) "active subscription occupies its start"
        equal firstFinish (OccupiedPeriod.finish activePeriod) "active subscription occupies through expiry"

        let offsetOverlapStart = firstStart.ToOffset(TimeSpan.FromHours(-5.0))
        let offsetOverlapFinish = firstFinish.ToOffset(TimeSpan.FromHours(4.0))
        errorCode "SUBSCRIPTION_OVERLAP" "offset-equivalent interval overlaps" (Subscription.start occupiedStore (subscriptionId "30000000-0000-0000-0000-000000000011") primaryCustomerId primaryProductId "monthly" offsetOverlapStart offsetOverlapFinish)
        errorCode "SUBSCRIPTION_OVERLAP" "interior interval overlaps" (Subscription.start occupiedStore (subscriptionId "30000000-0000-0000-0000-000000000012") primaryCustomerId primaryProductId "monthly" (timestamp 3) (timestamp 5))
        errorCode "SUBSCRIPTION_OVERLAP" "interval beginning one tick before finish overlaps" (Subscription.start occupiedStore (subscriptionId "30000000-0000-0000-0000-000000000013") primaryCustomerId primaryProductId "monthly" (firstFinish.AddTicks(-1L)) (timestamp 5))
        equal 1 (Store.summary occupiedStore).Subscriptions "rejected overlap leaves the input store unchanged"

        let _, adjacent =
            Subscription.start occupiedStore (subscriptionId "30000000-0000-0000-0000-000000000014") primaryCustomerId primaryProductId "monthly" firstFinish (timestamp 6)
            |> ok "adjacent subscription"
        equal firstFinish (Subscription.occupiedPeriod adjacent |> Option.map OccupiedPeriod.start |> Option.defaultValue baseTime) "adjacent subscription starts at the prior exclusive finish"

        errorCode "DUPLICATE_SUBSCRIPTION" "duplicate id remains first on overlap" (Subscription.start occupiedStore firstId primaryCustomerId primaryProductId "monthly" (timestamp 3) (timestamp 5))
        errorCode "INVALID_SUBSCRIPTION_TERM" "term validation precedes overlap" (Subscription.start occupiedStore (subscriptionId "30000000-0000-0000-0000-000000000015") primaryCustomerId primaryProductId " " (timestamp 3) (timestamp 5))
        errorCode "SUBSCRIPTION_EXPIRY_MUST_FOLLOW_START" "date validation precedes overlap" (Subscription.start occupiedStore (subscriptionId "30000000-0000-0000-0000-000000000016") primaryCustomerId primaryProductId "monthly" (timestamp 3) (timestamp 3))

        let cancelledNextDayStore, cancelledNextDay = Subscription.cancel occupiedStore firstId (timestamp 3) |> ok "cancel on next day"
        let truncated = Subscription.occupiedPeriod cancelledNextDay |> Option.defaultWith (fun () -> failwith "next-day cancellation has no occupied period")
        equal (timestamp 3) (OccupiedPeriod.finish truncated) "cancellation truncates occupancy at cancellation time"
        check (Subscription.occupiedPeriod active |> Option.isSome) "cancelling a stored copy leaves the original active value unchanged"
        let _, afterNextDay =
            Subscription.start cancelledNextDayStore (subscriptionId "30000000-0000-0000-0000-000000000017") primaryCustomerId primaryProductId "monthly" (timestamp 3) (timestamp 5)
            |> ok "subscription adjacent to next-day cancellation"
        equal (timestamp 5) (Subscription.occupiedPeriod afterNextDay |> Option.map OccupiedPeriod.finish |> Option.defaultValue baseTime) "subscription after cancellation keeps its requested expiry"

        let atStartId = subscriptionId "30000000-0000-0000-0000-000000000018"
        let atStartStore, atStart = Subscription.start store atStartId primaryCustomerId primaryProductId "monthly" (timestamp 8) (timestamp 10) |> ok "start cancellation-at-start subscription"
        let atStartCancelledStore, atStartCancelled = Subscription.cancel atStartStore atStartId (timestamp 8) |> ok "cancel exactly at start"
        check (Subscription.occupiedPeriod atStartCancelled |> Option.isNone) "cancellation at subscription start produces no occupied period"
        let _, startsAtSameInstant =
            Subscription.start atStartCancelledStore (subscriptionId "30000000-0000-0000-0000-000000000019") primaryCustomerId primaryProductId "monthly" (timestamp 8) (timestamp 10)
            |> ok "start after zero-length cancellation"
        equal (Some(timestamp 8)) (Subscription.occupiedPeriod startsAtSameInstant |> Option.map OccupiedPeriod.start) "zero-length cancellation does not block a new subscription"

        let afterExpiryId = subscriptionId "30000000-0000-0000-0000-000000000020"
        let afterExpiryStore, _ = Subscription.start store afterExpiryId primaryCustomerId primaryProductId "monthly" (timestamp 12) (timestamp 14) |> ok "start subscription for late cancellation"
        let afterExpiryStore, afterExpiry = Subscription.cancel afterExpiryStore afterExpiryId (timestamp 20) |> ok "cancel after expiry"
        let clippedToExpiry = Subscription.occupiedPeriod afterExpiry |> Option.defaultWith (fun () -> failwith "late cancellation has no occupied period")
        equal (timestamp 14) (OccupiedPeriod.finish clippedToExpiry) "cancellation after expiry leaves the original expiry"
        let _, afterExpiryAdjacent =
            Subscription.start afterExpiryStore (subscriptionId "30000000-0000-0000-0000-000000000021") primaryCustomerId primaryProductId "monthly" (timestamp 14) (timestamp 16)
            |> ok "start adjacent to expiry after late cancellation"
        equal (timestamp 14) (Subscription.occupiedPeriod afterExpiryAdjacent |> Option.map OccupiedPeriod.start |> Option.defaultValue baseTime) "late cancellation period ends at expiry"

        let secondProductId = productId "20000000-0000-0000-0000-000000000002"
        let secondProduct = Product.create secondProductId "Another plan" (Money.ofMinorUnits 100L) |> ok "another product"
        let withSecondProduct = Store.addProduct secondProduct occupiedStore |> ok "add another product"
        let _, otherProductSubscription =
            Subscription.start withSecondProduct (subscriptionId "30000000-0000-0000-0000-000000000022") primaryCustomerId secondProductId "monthly" (timestamp 3) (timestamp 5)
            |> ok "same customer can overlap on another product"
        equal secondProductId (Subscription.productId otherProductSubscription) "overlap scope includes the product"

        let secondCustomerId = customerId "10000000-0000-0000-0000-000000000002"
        let secondEmail = Email.create "grace@example.test" |> ok "second customer email"
        let secondCustomer = Customer.create secondCustomerId secondEmail "regular" Money.zero (timestamp 1) |> ok "second customer"
        let withSecondCustomer = Store.addCustomer secondCustomer occupiedStore |> ok "add another customer"
        let _, otherCustomerSubscription =
            Subscription.start withSecondCustomer (subscriptionId "30000000-0000-0000-0000-000000000023") secondCustomerId primaryProductId "monthly" (timestamp 3) (timestamp 5)
            |> ok "another customer can overlap on the same product"
        equal secondCustomerId (Subscription.customerId otherCustomerSubscription) "overlap scope includes the customer"

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

    [<EntryPoint>]
    let main _ =
        try
            group "nominal identifiers and Email policy" testStrongIdentifiersAndEmailPolicy
            group "checked minor-unit arithmetic and price invariants" testMoneyArithmeticAndPriceInvariants
            group "immutable store and lookup" testImmutableStoreAndCustomerLookup
            group "subscription lifecycle" testSubscriptionLifecycle
            group "occupied periods and subscription overlap" testOccupiedPeriodsAndSubscriptionOverlap
            group "invoice totals and validation" testInvoiceTotalsAndValidation
            group "payment provider boundary and receipt" testPaymentProviderBoundaryAndReceipt
            group "email outbox and provider isolation" testEmailOutboxAndProviderIsolation
            printfn "Business reference: %d groups, %d assertions" groups assertions
            0
        with error ->
            eprintfn "FAIL after %d assertions: %s" assertions error.Message
            1
