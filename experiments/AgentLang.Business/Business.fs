namespace AgentLang.Business

module Domain =
    open System
    open System.Text.RegularExpressions

    [<Struct; StructuralEquality; StructuralComparison>]
    type CustomerId = private CustomerId of Guid

    [<Struct; StructuralEquality; StructuralComparison>]
    type SubscriptionId = private SubscriptionId of Guid

    [<Struct; StructuralEquality; StructuralComparison>]
    type InvoiceId = private InvoiceId of Guid

    [<Struct; StructuralEquality; StructuralComparison>]
    type ProductId = private ProductId of Guid

    [<Struct; StructuralEquality; StructuralComparison>]
    type PaymentId = private PaymentId of Guid

    [<Struct; StructuralEquality; StructuralComparison>]
    type Email = private Email of string

    /// Money is a signed count of the smallest currency unit. Billing
    /// operations separately require nonnegative prices and positive charges.
    [<Struct; StructuralEquality; StructuralComparison>]
    type Money = private Money of int64

    type DomainError =
        | InvalidIdentifier of kind: string * value: string
        | InvalidEmail of value: string
        | InvalidName of kind: string
        | InvalidSubscriptionTerm
        | SubscriptionExpiryMustFollowStart
        | NegativeProductPrice of Money
        | DuplicateCustomer of CustomerId
        | DuplicateProduct of ProductId
        | DuplicateSubscription of SubscriptionId
        | DuplicateInvoice of InvoiceId
        | DuplicatePayment of PaymentId
        | CustomerNotFound of CustomerId
        | ProductNotFound of ProductId
        | SubscriptionNotFound of SubscriptionId
        | InvoiceNotFound of InvoiceId
        | InvalidQuantity of int
        | EmptyInvoice
        | MoneyOverflow
        | SubscriptionAlreadyCancelled of SubscriptionId
        | CancelBeforeSubscriptionStart of SubscriptionId
        | InvoiceAlreadyPaid of InvoiceId
        | PaymentAmountMustBePositive of Money
        | PaymentAmountMismatch of expected: Money * actual: Money
        | PaymentProviderFailure of message: string
        | InvalidProviderReceipt of message: string
        | NoPendingEmail
        | EmailProviderFailure of message: string
        | InvalidEmailMessage

    module DomainError =
        let code = function
            | InvalidIdentifier _ -> "INVALID_IDENTIFIER"
            | InvalidEmail _ -> "INVALID_EMAIL"
            | InvalidName _ -> "INVALID_NAME"
            | InvalidSubscriptionTerm -> "INVALID_SUBSCRIPTION_TERM"
            | SubscriptionExpiryMustFollowStart -> "SUBSCRIPTION_EXPIRY_MUST_FOLLOW_START"
            | NegativeProductPrice _ -> "NEGATIVE_PRODUCT_PRICE"
            | DuplicateCustomer _ -> "DUPLICATE_CUSTOMER"
            | DuplicateProduct _ -> "DUPLICATE_PRODUCT"
            | DuplicateSubscription _ -> "DUPLICATE_SUBSCRIPTION"
            | DuplicateInvoice _ -> "DUPLICATE_INVOICE"
            | DuplicatePayment _ -> "DUPLICATE_PAYMENT"
            | CustomerNotFound _ -> "CUSTOMER_NOT_FOUND"
            | ProductNotFound _ -> "PRODUCT_NOT_FOUND"
            | SubscriptionNotFound _ -> "SUBSCRIPTION_NOT_FOUND"
            | InvoiceNotFound _ -> "INVOICE_NOT_FOUND"
            | InvalidQuantity _ -> "INVALID_QUANTITY"
            | EmptyInvoice -> "EMPTY_INVOICE"
            | MoneyOverflow -> "MONEY_OVERFLOW"
            | SubscriptionAlreadyCancelled _ -> "SUBSCRIPTION_ALREADY_CANCELLED"
            | CancelBeforeSubscriptionStart _ -> "CANCEL_BEFORE_START"
            | InvoiceAlreadyPaid _ -> "INVOICE_ALREADY_PAID"
            | PaymentAmountMustBePositive _ -> "PAYMENT_AMOUNT_MUST_BE_POSITIVE"
            | PaymentAmountMismatch _ -> "PAYMENT_AMOUNT_MISMATCH"
            | PaymentProviderFailure _ -> "PAYMENT_PROVIDER_FAILURE"
            | InvalidProviderReceipt _ -> "INVALID_PROVIDER_RECEIPT"
            | NoPendingEmail -> "NO_PENDING_EMAIL"
            | EmailProviderFailure _ -> "EMAIL_PROVIDER_FAILURE"
            | InvalidEmailMessage -> "INVALID_EMAIL_MESSAGE"

        let message = function
            | InvalidIdentifier (kind, value) -> $"{kind} must be a GUID; received '{value}'."
            | InvalidEmail value -> $"Email '{value}' does not match the documented address policy."
            | InvalidName kind -> $"{kind} must contain non-whitespace text."
            | InvalidSubscriptionTerm -> "Subscription term must contain non-whitespace text."
            | SubscriptionExpiryMustFollowStart -> "Subscription expiry must be later than its start."
            | NegativeProductPrice _ -> "Product prices must be nonnegative."
            | DuplicateCustomer _ -> "A customer with this identifier already exists."
            | DuplicateProduct _ -> "A product with this identifier already exists."
            | DuplicateSubscription _ -> "A subscription with this identifier already exists."
            | DuplicateInvoice _ -> "An invoice with this identifier already exists."
            | DuplicatePayment _ -> "A payment with this identifier already exists."
            | CustomerNotFound _ -> "The customer does not exist."
            | ProductNotFound _ -> "The product does not exist."
            | SubscriptionNotFound _ -> "The subscription does not exist."
            | InvoiceNotFound _ -> "The invoice does not exist."
            | InvalidQuantity quantity -> $"Quantity must be positive; received {quantity}."
            | EmptyInvoice -> "An invoice must contain at least one line."
            | MoneyOverflow -> "The money operation exceeded the Int64 minor-unit range."
            | SubscriptionAlreadyCancelled _ -> "The subscription is already cancelled."
            | CancelBeforeSubscriptionStart _ -> "A subscription cannot be cancelled before it starts."
            | InvoiceAlreadyPaid _ -> "The invoice is already paid."
            | PaymentAmountMustBePositive _ -> "A payment amount must be positive."
            | PaymentAmountMismatch _ -> "A payment must equal the full open invoice balance."
            | PaymentProviderFailure detail -> $"Payment provider failed: {detail}"
            | InvalidProviderReceipt detail -> $"Payment provider returned an invalid receipt: {detail}"
            | NoPendingEmail -> "There is no queued email to deliver."
            | EmailProviderFailure detail -> $"Email provider failed: {detail}"
            | InvalidEmailMessage -> "Email subject and body must contain non-whitespace text."

    module private Identifier =
        let parse (kind: string) (make: Guid -> 'id) (value: string) =
            match Guid.TryParse value with
            | true, id -> Ok(make id)
            | false, _ -> Error(InvalidIdentifier(kind, value))

        let format (id: Guid) = id.ToString("D")

    module CustomerId =
        let parse (value: string) = Identifier.parse "CustomerId" CustomerId value
        let toString (CustomerId id) = Identifier.format id

    module SubscriptionId =
        let parse (value: string) = Identifier.parse "SubscriptionId" SubscriptionId value
        let toString (SubscriptionId id) = Identifier.format id

    module InvoiceId =
        let parse (value: string) = Identifier.parse "InvoiceId" InvoiceId value
        let toString (InvoiceId id) = Identifier.format id

    module ProductId =
        let parse (value: string) = Identifier.parse "ProductId" ProductId value
        let toString (ProductId id) = Identifier.format id

    module PaymentId =
        let parse (value: string) = Identifier.parse "PaymentId" PaymentId value
        let toString (PaymentId id) = Identifier.format id

    module Email =
        // Deliberately modest policy: ASCII local/domain characters, one @,
        // at least two domain labels, and no whitespace or empty dot segments.
        // This is not a complete implementation of the Internet mail RFCs.
        let private addressPattern =
            Regex(
                "\\A[A-Za-z0-9_%+-]+(?:\\.[A-Za-z0-9_%+-]+)*@[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\\.[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)+\\z",
                RegexOptions.CultureInvariant)

        let create (value: string) =
            if not (isNull value) && value.Length <= 254 && addressPattern.IsMatch(value) then
                Ok(Email value)
            else
                Error(InvalidEmail value)

        let value (Email value) = value

    module Money =
        let ofMinorUnits value = Money value
        let minorUnits (Money value) = value
        let zero = Money 0L
        let isNegative (Money value) = value < 0L
        let isPositive (Money value) = value > 0L

        let add (Money left) (Money right) =
            try Ok(Money(Checked.(+) left right))
            with :? OverflowException -> Error MoneyOverflow

        let multiplyByQuantity (Money amount) quantity =
            try Ok(Money(Checked.(*) amount (int64 quantity)))
            with :? OverflowException -> Error MoneyOverflow

        let sum amounts =
            amounts
            |> List.fold (fun state value -> state |> Result.bind (fun total -> add total value)) (Ok zero)

    type Customer = private {
        Id: CustomerId
        Email: Email
        Kind: string
        Balance: Money
        CreatedAt: DateTimeOffset
    }

    type Product = private {
        Id: ProductId
        Name: string
        UnitPrice: Money
    }

    type SubscriptionStatus =
        | Active
        | Cancelled

    type Subscription = private {
        Id: SubscriptionId
        CustomerId: CustomerId
        ProductId: ProductId
        Term: string
        StartedAt: DateTimeOffset
        ExpiresAt: DateTimeOffset
        Status: SubscriptionStatus
        CancelledAt: DateTimeOffset option
    }

    type InvoiceLine = private {
        ProductId: ProductId
        Description: string
        Quantity: int
        UnitPrice: Money
        LineTotal: Money
    }

    type InvoiceStatus =
        | Open
        | Paid

    type Invoice = private {
        Id: InvoiceId
        CustomerId: CustomerId
        Lines: InvoiceLine list
        Total: Money
        CreatedAt: DateTimeOffset
        Status: InvoiceStatus
    }

    type PaymentRequest = {
        PaymentId: PaymentId
        InvoiceId: InvoiceId
        Amount: Money
        RequestedAt: DateTimeOffset
    }

    type PaymentReceipt = private {
        Reference: string
        Amount: Money
    }

    type Payment = private {
        Id: PaymentId
        InvoiceId: InvoiceId
        Amount: Money
        ProviderReference: string
        PaidAt: DateTimeOffset
    }

    type EmailMessage = private {
        To: Email
        Subject: string
        Body: string
    }

    type PaymentProvider = {
        Charge: PaymentRequest -> Result<PaymentReceipt, string>
    }

    type EmailProvider = {
        Send: EmailMessage -> Result<unit, string>
    }

    type StoreSummary = {
        Customers: int
        Products: int
        Subscriptions: int
        Invoices: int
        Payments: int
        PendingEmails: int
        SentEmails: int
    }

    type Store = private {
        CustomersById: Map<CustomerId, Customer>
        ProductsById: Map<ProductId, Product>
        SubscriptionsById: Map<SubscriptionId, Subscription>
        InvoicesById: Map<InvoiceId, Invoice>
        PaymentsById: Map<PaymentId, Payment>
        EmailOutbox: EmailMessage list
        SentEmailMessages: EmailMessage list
    }

    module Customer =
        let create (id: CustomerId) (email: Email) (kind: string) (balance: Money) (createdAt: DateTimeOffset) =
            if String.IsNullOrWhiteSpace kind then
                Error(InvalidName "Customer kind")
            else
                Ok {
                    Id = id
                    Email = email
                    Kind = kind.Trim()
                    Balance = balance
                    CreatedAt = createdAt.ToUniversalTime()
                }

        let id (customer: Customer) = customer.Id
        let email (customer: Customer) = customer.Email
        let kind (customer: Customer) = customer.Kind
        let balance (customer: Customer) = customer.Balance
        let createdAt (customer: Customer) = customer.CreatedAt

    module Product =
        let create (id: ProductId) (name: string) (unitPrice: Money) =
            if String.IsNullOrWhiteSpace name then
                Error(InvalidName "Product name")
            elif Money.isNegative unitPrice then
                Error(NegativeProductPrice unitPrice)
            else
                Ok { Id = id; Name = name.Trim(); UnitPrice = unitPrice }

        let id (product: Product) = product.Id
        let name (product: Product) = product.Name
        let unitPrice (product: Product) = product.UnitPrice

    module PaymentReceipt =
        let create (reference: string) (amount: Money) =
            if String.IsNullOrWhiteSpace reference then
                Error(InvalidProviderReceipt "reference must not be empty")
            else
                Ok { Reference = reference.Trim(); Amount = amount }

        let reference (receipt: PaymentReceipt) = receipt.Reference
        let amount (receipt: PaymentReceipt) = receipt.Amount

    module private ProviderHelpers =
        let paymentResult (reference: string) (request: PaymentRequest) =
            PaymentReceipt.create reference request.Amount
            |> Result.mapError DomainError.message

    module PaymentProvider =
        let deterministic (reference: string) = { Charge = ProviderHelpers.paymentResult reference }

    module EmailProvider =
        let deterministic = { Send = fun _ -> Ok() }

    module private StoreAccess =
        let empty: Store = {
            CustomersById = Map.empty
            ProductsById = Map.empty
            SubscriptionsById = Map.empty
            InvoicesById = Map.empty
            PaymentsById = Map.empty
            EmailOutbox = []
            SentEmailMessages = []
        }

    module Store =
        let empty: Store = StoreAccess.empty

        let summary (store: Store) = {
            Customers = store.CustomersById.Count
            Products = store.ProductsById.Count
            Subscriptions = store.SubscriptionsById.Count
            Invoices = store.InvoicesById.Count
            Payments = store.PaymentsById.Count
            PendingEmails = store.EmailOutbox.Length
            SentEmails = store.SentEmailMessages.Length
        }

        let customer (id: CustomerId) (store: Store) = Map.tryFind id store.CustomersById
        let product (id: ProductId) (store: Store) = Map.tryFind id store.ProductsById
        let subscription (id: SubscriptionId) (store: Store) = Map.tryFind id store.SubscriptionsById
        let invoice (id: InvoiceId) (store: Store) = Map.tryFind id store.InvoicesById
        let payment (id: PaymentId) (store: Store) = Map.tryFind id store.PaymentsById
        let pendingEmails (store: Store) = store.EmailOutbox
        let sentEmails (store: Store) = store.SentEmailMessages

        let addCustomer (customer: Customer) (store: Store) =
            if Map.containsKey customer.Id store.CustomersById then
                Error(DuplicateCustomer customer.Id)
            else
                Ok { store with CustomersById = Map.add customer.Id customer store.CustomersById }

        let addProduct (product: Product) (store: Store) =
            if Map.containsKey product.Id store.ProductsById then
                Error(DuplicateProduct product.Id)
            else
                Ok { store with ProductsById = Map.add product.Id product store.ProductsById }

    module Subscription =
        let id (subscription: Subscription) = subscription.Id
        let customerId (subscription: Subscription) = subscription.CustomerId
        let productId (subscription: Subscription) = subscription.ProductId
        let term (subscription: Subscription) = subscription.Term
        let startedAt (subscription: Subscription) = subscription.StartedAt
        let expiresAt (subscription: Subscription) = subscription.ExpiresAt
        let status (subscription: Subscription) = subscription.Status
        let cancelledAt (subscription: Subscription) = subscription.CancelledAt

        let start (store: Store) (id: SubscriptionId) (customerId: CustomerId) (productId: ProductId) (term: string) (startedAt: DateTimeOffset) (expiresAt: DateTimeOffset) =
            if Map.containsKey id store.SubscriptionsById then
                Error(DuplicateSubscription id)
            elif String.IsNullOrWhiteSpace term then
                Error InvalidSubscriptionTerm
            elif expiresAt.ToUniversalTime() <= startedAt.ToUniversalTime() then
                Error SubscriptionExpiryMustFollowStart
            elif not (Map.containsKey customerId store.CustomersById) then
                Error(CustomerNotFound customerId)
            elif not (Map.containsKey productId store.ProductsById) then
                Error(ProductNotFound productId)
            else
                let subscription = {
                    Id = id
                    CustomerId = customerId
                    ProductId = productId
                    Term = term.Trim()
                    StartedAt = startedAt.ToUniversalTime()
                    ExpiresAt = expiresAt.ToUniversalTime()
                    Status = Active
                    CancelledAt = None
                }
                let updated = { store with SubscriptionsById = Map.add id subscription store.SubscriptionsById }
                Ok(updated, subscription)

        let cancel (store: Store) (id: SubscriptionId) (cancelledAt: DateTimeOffset) =
            match Map.tryFind id store.SubscriptionsById with
            | None -> Error(SubscriptionNotFound id)
            | Some subscription when subscription.Status = Cancelled -> Error(SubscriptionAlreadyCancelled id)
            | Some subscription ->
                let cancelledAt = cancelledAt.ToUniversalTime()
                if cancelledAt < subscription.StartedAt then
                    Error(CancelBeforeSubscriptionStart id)
                else
                    let updatedSubscription = { subscription with Status = Cancelled; CancelledAt = Some cancelledAt }
                    let updated = { store with SubscriptionsById = Map.add id updatedSubscription store.SubscriptionsById }
                    Ok(updated, updatedSubscription)

    module Invoice =
        let id (invoice: Invoice) = invoice.Id
        let customerId (invoice: Invoice) = invoice.CustomerId
        let lines (invoice: Invoice) = invoice.Lines
        let total (invoice: Invoice) = invoice.Total
        let createdAt (invoice: Invoice) = invoice.CreatedAt
        let status (invoice: Invoice) = invoice.Status

        let private makeLine (store: Store) ((productId, quantity): ProductId * int) =
            if quantity <= 0 then
                Error(InvalidQuantity quantity)
            else
                match Map.tryFind productId store.ProductsById with
                | None -> Error(ProductNotFound productId)
                | Some product ->
                    Money.multiplyByQuantity product.UnitPrice quantity
                    |> Result.map (fun lineTotal -> {
                        ProductId = product.Id
                        Description = product.Name
                        Quantity = quantity
                        UnitPrice = product.UnitPrice
                        LineTotal = lineTotal
                    })

        let create (store: Store) (id: InvoiceId) (customerId: CustomerId) (cart: (ProductId * int) list) (createdAt: DateTimeOffset) =
            if Map.containsKey id store.InvoicesById then
                Error(DuplicateInvoice id)
            elif not (Map.containsKey customerId store.CustomersById) then
                Error(CustomerNotFound customerId)
            elif List.isEmpty cart then
                Error EmptyInvoice
            else
                cart
                |> List.map (makeLine store)
                |> List.fold (fun state line -> state |> Result.bind (fun lines -> line |> Result.map (fun value -> value :: lines))) (Ok [])
                |> Result.map List.rev
                |> Result.bind (fun lines ->
                    lines
                    |> List.map (fun line -> line.LineTotal)
                    |> Money.sum
                    |> Result.map (fun total ->
                        let invoice = {
                            Id = id
                            CustomerId = customerId
                            Lines = lines
                            Total = total
                            CreatedAt = createdAt.ToUniversalTime()
                            Status = Open
                        }
                        let updated = { store with InvoicesById = Map.add id invoice store.InvoicesById }
                        updated, invoice))

    module InvoiceLine =
        let productId (line: InvoiceLine) = line.ProductId
        let description (line: InvoiceLine) = line.Description
        let quantity (line: InvoiceLine) = line.Quantity
        let unitPrice (line: InvoiceLine) = line.UnitPrice
        let lineTotal (line: InvoiceLine) = line.LineTotal

    module Payment =
        let id (payment: Payment) = payment.Id
        let invoiceId (payment: Payment) = payment.InvoiceId
        let amount (payment: Payment) = payment.Amount
        let providerReference (payment: Payment) = payment.ProviderReference
        let paidAt (payment: Payment) = payment.PaidAt

        let submit (store: Store) (provider: PaymentProvider) (id: PaymentId) (invoiceId: InvoiceId) (amount: Money) (requestedAt: DateTimeOffset) =
            if Map.containsKey id store.PaymentsById then
                Error(DuplicatePayment id)
            elif Money.isNegative amount || amount = Money.zero then
                Error(PaymentAmountMustBePositive amount)
            else
                match Map.tryFind invoiceId store.InvoicesById with
                | None -> Error(InvoiceNotFound invoiceId)
                | Some invoice when invoice.Status = Paid -> Error(InvoiceAlreadyPaid invoiceId)
                | Some invoice when invoice.Total <> amount -> Error(PaymentAmountMismatch(invoice.Total, amount))
                | Some invoice ->
                    let request = {
                        PaymentId = id
                        InvoiceId = invoiceId
                        Amount = amount
                        RequestedAt = requestedAt.ToUniversalTime()
                    }
                    match provider.Charge request with
                    | Error detail -> Error(PaymentProviderFailure detail)
                    | Ok receipt when receipt.Amount <> amount ->
                        Error(InvalidProviderReceipt "receipt amount does not match the requested amount")
                    | Ok receipt ->
                        let payment = {
                            Id = id
                            InvoiceId = invoiceId
                            Amount = amount
                            ProviderReference = receipt.Reference
                            PaidAt = requestedAt.ToUniversalTime()
                        }
                        let paidInvoice = { invoice with Status = Paid }
                        let updated = {
                            store with
                                InvoicesById = Map.add invoiceId paidInvoice store.InvoicesById
                                PaymentsById = Map.add id payment store.PaymentsById
                        }
                        Ok(updated, payment)

    module EmailMessage =
        let create (toAddress: Email) (subject: string) (body: string) =
            if String.IsNullOrWhiteSpace subject || String.IsNullOrWhiteSpace body then
                Error InvalidEmailMessage
            else
                Ok { To = toAddress; Subject = subject.Trim(); Body = body.Trim() }

        let toAddress (message: EmailMessage) = message.To
        let subject (message: EmailMessage) = message.Subject
        let body (message: EmailMessage) = message.Body

    module EmailOutbox =
        let queue (store: Store) (customerId: CustomerId) (subject: string) (body: string) =
            match Map.tryFind customerId store.CustomersById with
            | None -> Error(CustomerNotFound customerId)
            | Some customer ->
                EmailMessage.create customer.Email subject body
                |> Result.map (fun message ->
                    { store with EmailOutbox = store.EmailOutbox @ [ message ] }, message)

        let deliverNext (store: Store) (provider: EmailProvider) =
            match store.EmailOutbox with
            | [] -> Error NoPendingEmail
            | message :: remaining ->
                match provider.Send message with
                | Error detail -> Error(EmailProviderFailure detail)
                | Ok () ->
                    Ok {
                        store with
                            EmailOutbox = remaining
                            SentEmailMessages = store.SentEmailMessages @ [ message ]
                    }
