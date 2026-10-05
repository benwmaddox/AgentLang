namespace AgentLang.Business.Contracts

open System
open System.Buffers
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.RegularExpressions

module B = AgentLang.Business.Domain

/// Stable diagnostic returned by strict fixture parsing and projection.
type ContractDiagnostic =
    { Code: string
      Path: string
      Message: string }

/// A typed, immutable projection of the business fixture's persisted customer.
type CustomerRecord =
    { Id: B.CustomerId
      Email: B.Email
      Kind: string
      Balance: B.Money
      CreatedAt: DateTimeOffset }

type ProductRecord =
    { Id: B.ProductId
      Name: string
      UnitPrice: B.Money }

type SubscriptionRecord =
    { Id: B.SubscriptionId
      CustomerId: B.CustomerId
      ProductId: B.ProductId
      Term: string
      StartedAt: DateTimeOffset
      ExpiresAt: DateTimeOffset
      Status: B.SubscriptionStatus
      CancelledAt: DateTimeOffset option }

type InvoiceLineRecord =
    { ProductId: B.ProductId
      Description: string
      Quantity: int
      UnitPrice: B.Money
      LineTotal: B.Money }

type InvoiceRecord =
    { Id: B.InvoiceId
      CustomerId: B.CustomerId
      Lines: InvoiceLineRecord list
      Total: B.Money
      CreatedAt: DateTimeOffset
      Status: B.InvoiceStatus }

type PaymentRecord =
    { Id: B.PaymentId
      InvoiceId: B.InvoiceId
      Amount: B.Money
      ProviderReference: string
      PaidAt: DateTimeOffset }

type EmailRecord =
    { To: B.Email
      Subject: string
      Body: string }

/// Provider results are values in the fixture contract. They do not represent
/// invocation of a provider or grant an effect capability.
type ProviderOutcome =
    | PaymentAccepted of reference: string * amount: B.Money
    | PaymentDeclined of message: string
    | EmailAccepted
    | EmailFailed of message: string

/// Full version-1 business fixture. Empty collections are explicit in JSON.
type FixtureDocument =
    { Customers: CustomerRecord list
      Products: ProductRecord list
      Subscriptions: SubscriptionRecord list
      Invoices: InvoiceRecord list
      Payments: PaymentRecord list
      PendingEmails: EmailRecord list
      SentEmails: EmailRecord list
      ProviderOutcomes: ProviderOutcome list }

type KnownEntityIds =
    { Customers: B.CustomerId list
      Products: B.ProductId list
      Subscriptions: B.SubscriptionId list
      Invoices: B.InvoiceId list
      Payments: B.PaymentId list }

type DomainSeed =
    { Store: B.Store
      KnownIds: KnownEntityIds
      Projection: FixtureDocument }

type DefaultApplied =
    { Path: string
      Value: string }

type ExpansionProvenance =
    { SourceSha256: string
      DefaultsApplied: DefaultApplied list }

type ExpandedFragment =
    { Document: FixtureDocument
      Provenance: ExpansionProvenance }

module Contract =
    [<Literal>]
    let Schema = "agentlang.business.fixture"

    [<Literal>]
    let Version = 1

    [<Literal>]
    let MaximumDocumentBytes = 4 * 1024 * 1024

    [<Literal>]
    let MaximumCollectionItems = 10_000

    exception private ContractFailure of ContractDiagnostic

    let private diagnostic code path message =
        { Code = code; Path = path; Message = message }

    let private fail code path message =
        raise (ContractFailure(diagnostic code path message))

    let private jsonOptions =
        JsonDocumentOptions(MaxDepth = 64, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow)

    let private pathProperty (path: string) (name: string) =
        let escaped = name.Replace("~", "~0").Replace("/", "~1")
        $"{path}/{escaped}"

    let private normalize (document: FixtureDocument) =
        { document with
            Customers = document.Customers |> List.sortBy (fun item -> B.CustomerId.toString item.Id)
            Products = document.Products |> List.sortBy (fun item -> B.ProductId.toString item.Id)
            Subscriptions = document.Subscriptions |> List.sortBy (fun item -> B.SubscriptionId.toString item.Id)
            Invoices = document.Invoices |> List.sortBy (fun item -> B.InvoiceId.toString item.Id)
            Payments = document.Payments |> List.sortBy (fun item -> B.PaymentId.toString item.Id) }

    let empty =
        { Customers = []
          Products = []
          Subscriptions = []
          Invoices = []
          Payments = []
          PendingEmails = []
          SentEmails = []
          ProviderOutcomes = [] }

    let private isLowerSha256 (value: string) =
        value.Length = 64
        && (value
            |> Seq.forall (fun character ->
                (character >= '0' && character <= '9')
                || (character >= 'a' && character <= 'f')))

    let private objectFields (path: string) (element: JsonElement) (allowed: string list) (required: string list) =
        if element.ValueKind <> JsonValueKind.Object then
            fail "TYPE_MISMATCH" path "Expected a JSON object."

        let allowedSet = HashSet<string>(allowed, StringComparer.Ordinal)
        let fields = Dictionary<string, JsonElement>(StringComparer.Ordinal)

        for property in element.EnumerateObject() do
            let propertyPath = pathProperty path property.Name
            if not (allowedSet.Contains property.Name) then
                fail "UNKNOWN_FIELD" propertyPath "Field is not part of this fixture schema version."
            if not (fields.TryAdd(property.Name, property.Value)) then
                fail "DUPLICATE_FIELD" propertyPath "A JSON object cannot repeat a field."

        for name in required do
            if not (fields.ContainsKey name) then
                fail "MISSING_FIELD" (pathProperty path name) "Required field is missing."

        fields

    let private requiredElement path (fields: Dictionary<string, JsonElement>) name =
        match fields.TryGetValue name with
        | true, value -> value
        | false, _ -> fail "MISSING_FIELD" (pathProperty path name) "Required field is missing."

    let private requiredString path fields name =
        let value = requiredElement path fields name
        if value.ValueKind <> JsonValueKind.String then
            fail "TYPE_MISMATCH" (pathProperty path name) "Expected a JSON string."
        match value.GetString() with
        | null -> fail "TYPE_MISMATCH" (pathProperty path name) "Expected a non-null JSON string."
        | text -> text

    let private requiredNonBlankText path fields name =
        let value = requiredString path fields name |> fun text -> text.Trim()
        if String.IsNullOrWhiteSpace value then
            fail "INVALID_TEXT" (pathProperty path name) "Text must contain at least one non-whitespace character."
        value

    let private optionalNullableString path fields name =
        let element = requiredElement path fields name
        match element.ValueKind with
        | JsonValueKind.Null -> None
        | JsonValueKind.String ->
            match element.GetString() with
            | null -> None
            | text -> Some text
        | _ -> fail "TYPE_MISMATCH" (pathProperty path name) "Expected a string or null."

    let private parseIntegerToken path (element: JsonElement) (minValue: int64) (maxValue: int64) =
        if element.ValueKind <> JsonValueKind.Number then
            fail "TYPE_MISMATCH" path "Expected an integer JSON number."
        let raw = element.GetRawText()
        if not (Regex.IsMatch(raw, "\\A-?(?:0|[1-9][0-9]*)\\z", RegexOptions.CultureInvariant)) then
            fail "TYPE_MISMATCH" path "Floating-point, exponent, and non-integer numeric forms are not accepted."
        match Int64.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
        | true, value when value >= minValue && value <= maxValue -> value
        | _ -> fail "INTEGER_OUT_OF_RANGE" path "Integer is outside the declared range."

    let private requiredInt64 path fields name =
        parseIntegerToken (pathProperty path name) (requiredElement path fields name) Int64.MinValue Int64.MaxValue

    let private requiredInt32 path fields name =
        parseIntegerToken (pathProperty path name) (requiredElement path fields name) (int64 Int32.MinValue) (int64 Int32.MaxValue)
        |> int

    let private arrayItems path (element: JsonElement) parseItem =
        if element.ValueKind <> JsonValueKind.Array then
            fail "TYPE_MISMATCH" path "Expected a JSON array."
        let length = element.GetArrayLength()
        if length > MaximumCollectionItems then
            fail "COLLECTION_TOO_LARGE" path $"Collection exceeds the {MaximumCollectionItems}-item limit."
        element.EnumerateArray()
        |> Seq.mapi (fun index item -> parseItem $"{path}/{index}" item)
        |> Seq.toList

    let private requiredArray path fields name parseItem =
        arrayItems (pathProperty path name) (requiredElement path fields name) parseItem

    let private parseGuid path parse (text: string) =
        match parse text with
        | Ok value -> value
        | Error error -> fail (B.DomainError.code error) path (B.DomainError.message error)

    let private parseInstant path (text: string) =
        let hasIsoZone = Regex.IsMatch(text, "(?:[Zz]|[+-][0-9]{2}:[0-9]{2})\\z", RegexOptions.CultureInvariant)
        let hasIsoShape = Regex.IsMatch(text, "\\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\\.[0-9]{1,7})?(?:[Zz]|[+-][0-9]{2}:[0-9]{2})\\z", RegexOptions.CultureInvariant)
        if not hasIsoZone || not hasIsoShape then
            fail "INVALID_INSTANT" path "Expected an ISO 8601 date-time with an explicit UTC marker or numeric offset."
        match DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None) with
        | false, _ -> fail "INVALID_INSTANT" path "Date-time is invalid or outside the DateTimeOffset range."
        | true, value ->
            try value.ToUniversalTime()
            with :? ArgumentOutOfRangeException -> fail "INVALID_INSTANT" path "UTC normalization is outside the DateTimeOffset range."

    let private parseEmail path text =
        match B.Email.create text with
        | Ok value -> value
        | Error error -> fail (B.DomainError.code error) path (B.DomainError.message error)

    let private parseCustomerId path text = parseGuid path B.CustomerId.parse text
    let private parseSubscriptionId path text = parseGuid path B.SubscriptionId.parse text
    let private parseProductId path text = parseGuid path B.ProductId.parse text
    let private parseInvoiceId path text = parseGuid path B.InvoiceId.parse text
    let private parsePaymentId path text = parseGuid path B.PaymentId.parse text

    let private parseCustomer path element =
        let fields = objectFields path element [ "id"; "email"; "kind"; "balanceMinorUnits"; "createdAt" ] [ "id"; "email"; "kind"; "balanceMinorUnits"; "createdAt" ]
        { Id = requiredString path fields "id" |> parseCustomerId (pathProperty path "id")
          Email = requiredString path fields "email" |> parseEmail (pathProperty path "email")
          Kind = requiredNonBlankText path fields "kind"
          Balance = requiredInt64 path fields "balanceMinorUnits" |> B.Money.ofMinorUnits
          CreatedAt = requiredString path fields "createdAt" |> parseInstant (pathProperty path "createdAt") }

    let private parseProduct path element =
        let fields = objectFields path element [ "id"; "name"; "unitPriceMinorUnits" ] [ "id"; "name"; "unitPriceMinorUnits" ]
        let price = requiredInt64 path fields "unitPriceMinorUnits" |> B.Money.ofMinorUnits
        match B.Product.create (requiredString path fields "id" |> parseProductId (pathProperty path "id")) (requiredNonBlankText path fields "name") price with
        | Ok product ->
            { Id = B.Product.id product
              Name = B.Product.name product
              UnitPrice = B.Product.unitPrice product }
        | Error error -> fail (B.DomainError.code error) (pathProperty path "unitPriceMinorUnits") (B.DomainError.message error)

    let private parseSubscriptionStatus path text =
        match text with
        | "active" -> B.Active
        | "cancelled" -> B.Cancelled
        | _ -> fail "INVALID_SUBSCRIPTION_STATUS" path "Status must be 'active' or 'cancelled'."

    let private parseInvoiceStatus path text =
        match text with
        | "open" -> B.Open
        | "paid" -> B.Paid
        | _ -> fail "INVALID_INVOICE_STATUS" path "Status must be 'open' or 'paid'."

    let private parseSubscription path element =
        let fields = objectFields path element [ "id"; "customerId"; "productId"; "term"; "startedAt"; "expiresAt"; "status"; "cancelledAt" ] [ "id"; "customerId"; "productId"; "term"; "startedAt"; "expiresAt"; "status"; "cancelledAt" ]
        { Id = requiredString path fields "id" |> parseSubscriptionId (pathProperty path "id")
          CustomerId = requiredString path fields "customerId" |> parseCustomerId (pathProperty path "customerId")
          ProductId = requiredString path fields "productId" |> parseProductId (pathProperty path "productId")
          Term = requiredNonBlankText path fields "term"
          StartedAt = requiredString path fields "startedAt" |> parseInstant (pathProperty path "startedAt")
          ExpiresAt = requiredString path fields "expiresAt" |> parseInstant (pathProperty path "expiresAt")
          Status = requiredString path fields "status" |> parseSubscriptionStatus (pathProperty path "status")
          CancelledAt = optionalNullableString path fields "cancelledAt" |> Option.map (parseInstant (pathProperty path "cancelledAt")) }

    let private parseInvoiceLine path element =
        let fields = objectFields path element [ "productId"; "description"; "quantity"; "unitPriceMinorUnits"; "lineTotalMinorUnits" ] [ "productId"; "description"; "quantity"; "unitPriceMinorUnits"; "lineTotalMinorUnits" ]
        { ProductId = requiredString path fields "productId" |> parseProductId (pathProperty path "productId")
          Description = requiredNonBlankText path fields "description"
          Quantity = requiredInt32 path fields "quantity"
          UnitPrice = requiredInt64 path fields "unitPriceMinorUnits" |> B.Money.ofMinorUnits
          LineTotal = requiredInt64 path fields "lineTotalMinorUnits" |> B.Money.ofMinorUnits }

    let private parseInvoice path element =
        let fields = objectFields path element [ "id"; "customerId"; "lines"; "totalMinorUnits"; "createdAt"; "status" ] [ "id"; "customerId"; "lines"; "totalMinorUnits"; "createdAt"; "status" ]
        { Id = requiredString path fields "id" |> parseInvoiceId (pathProperty path "id")
          CustomerId = requiredString path fields "customerId" |> parseCustomerId (pathProperty path "customerId")
          Lines = requiredArray path fields "lines" parseInvoiceLine
          Total = requiredInt64 path fields "totalMinorUnits" |> B.Money.ofMinorUnits
          CreatedAt = requiredString path fields "createdAt" |> parseInstant (pathProperty path "createdAt")
          Status = requiredString path fields "status" |> parseInvoiceStatus (pathProperty path "status") }

    let private parsePayment path element =
        let fields = objectFields path element [ "id"; "invoiceId"; "amountMinorUnits"; "providerReference"; "paidAt" ] [ "id"; "invoiceId"; "amountMinorUnits"; "providerReference"; "paidAt" ]
        { Id = requiredString path fields "id" |> parsePaymentId (pathProperty path "id")
          InvoiceId = requiredString path fields "invoiceId" |> parseInvoiceId (pathProperty path "invoiceId")
          Amount = requiredInt64 path fields "amountMinorUnits" |> B.Money.ofMinorUnits
          ProviderReference = requiredNonBlankText path fields "providerReference"
          PaidAt = requiredString path fields "paidAt" |> parseInstant (pathProperty path "paidAt") }

    let private parseEmailRecord path element =
        let fields = objectFields path element [ "to"; "subject"; "body" ] [ "to"; "subject"; "body" ]
        { To = requiredString path fields "to" |> parseEmail (pathProperty path "to")
          Subject = requiredNonBlankText path fields "subject"
          Body = requiredNonBlankText path fields "body" }

    let private parseProviderOutcome (path: string) (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            fail "TYPE_MISMATCH" path "Expected a provider outcome object."
        let kind =
            element.TryGetProperty("kind")
            |> function
                | true, value when value.ValueKind = JsonValueKind.String -> value.GetString()
                | _ -> fail "MISSING_FIELD" (pathProperty path "kind") "A string outcome kind is required."
        match kind with
        | "payment-accepted" ->
            let fields = objectFields path element [ "kind"; "reference"; "amountMinorUnits" ] [ "kind"; "reference"; "amountMinorUnits" ]
            PaymentAccepted(requiredNonBlankText path fields "reference", requiredInt64 path fields "amountMinorUnits" |> B.Money.ofMinorUnits)
        | "payment-declined" ->
            let fields = objectFields path element [ "kind"; "message" ] [ "kind"; "message" ]
            PaymentDeclined(requiredString path fields "message")
        | "email-accepted" ->
            let _ = objectFields path element [ "kind" ] [ "kind" ]
            EmailAccepted
        | "email-failed" ->
            let fields = objectFields path element [ "kind"; "message" ] [ "kind"; "message" ]
            EmailFailed(requiredString path fields "message")
        | _ -> fail "INVALID_PROVIDER_OUTCOME" (pathProperty path "kind") "Unknown provider outcome kind."

    let private uniqueValues path values =
        let seen = HashSet<string>(StringComparer.Ordinal)
        values
        |> List.iter (fun (valuePath, value) ->
            if not (seen.Add value) then fail "DUPLICATE_IDENTIFIER" valuePath $"Identifier '{value}' occurs more than once.")

    let private requireUtcRepresentable path (value: DateTimeOffset) =
        try value.ToUniversalTime() |> ignore
        with :? ArgumentOutOfRangeException ->
            fail "INVALID_INSTANT" path "Date-time cannot be represented as a UTC instant."

    let private validateDocumentCore (document: FixtureDocument) =
        let checkUnique (path: string) (identifiers: string list) =
            if identifiers.Length > MaximumCollectionItems then
                fail "COLLECTION_TOO_LARGE" ("/" + path) $"Collection exceeds the {MaximumCollectionItems}-item limit."
            identifiers
            |> List.mapi (fun index id -> $"/{path}/{index}/id", id)
            |> uniqueValues ("/" + path)

        checkUnique "customers" (document.Customers |> List.map (fun item -> B.CustomerId.toString item.Id))
        checkUnique "products" (document.Products |> List.map (fun item -> B.ProductId.toString item.Id))
        checkUnique "subscriptions" (document.Subscriptions |> List.map (fun item -> B.SubscriptionId.toString item.Id))
        checkUnique "invoices" (document.Invoices |> List.map (fun item -> B.InvoiceId.toString item.Id))
        checkUnique "payments" (document.Payments |> List.map (fun item -> B.PaymentId.toString item.Id))

        for name, count in [
            "pendingEmails", document.PendingEmails.Length
            "sentEmails", document.SentEmails.Length
            "providerOutcomes", document.ProviderOutcomes.Length
        ] do
            if count > MaximumCollectionItems then
                fail "COLLECTION_TOO_LARGE" ("/" + name) $"Collection exceeds the {MaximumCollectionItems}-item limit."

        let customers = document.Customers |> List.map (fun item -> B.CustomerId.toString item.Id) |> Set.ofList
        let products = document.Products |> List.map (fun item -> B.ProductId.toString item.Id) |> Set.ofList
        let invoices = document.Invoices |> List.map (fun item -> item.Id, item) |> Map.ofList
        let failRef collection index field id =
            fail "MISSING_REFERENCE" $"/{collection}/{index}/{field}" $"Referenced identifier '{id}' does not exist."

        document.Customers
        |> List.iteri (fun index customer ->
            let email = B.Email.value customer.Email
            match B.Email.create email with
            | Error _ -> fail "INVALID_EMAIL" $"/customers/{index}/email" "Email must satisfy the documented reference policy."
            | Ok _ -> ()
            if String.IsNullOrWhiteSpace customer.Kind then
                fail "INVALID_TEXT" $"/customers/{index}/kind" "Customer kind is required."
            requireUtcRepresentable $"/customers/{index}/createdAt" customer.CreatedAt
            if String.IsNullOrWhiteSpace email then
                fail "INVALID_EMAIL" $"/customers/{index}/email" "Email must be a valid non-null address."
        )

        document.Products
        |> List.iteri (fun index product ->
            if String.IsNullOrWhiteSpace product.Name then fail "INVALID_TEXT" $"/products/{index}/name" "Product name is required."
            if B.Money.isNegative product.UnitPrice then fail "NEGATIVE_PRODUCT_PRICE" $"/products/{index}/unitPriceMinorUnits" "Product prices must be nonnegative.")

        document.Subscriptions
        |> List.iteri (fun index subscription ->
            let customerId = B.CustomerId.toString subscription.CustomerId
            let productId = B.ProductId.toString subscription.ProductId
            if not (Set.contains customerId customers) then failRef "subscriptions" index "customerId" customerId
            if not (Set.contains productId products) then failRef "subscriptions" index "productId" productId
            if String.IsNullOrWhiteSpace subscription.Term then fail "INVALID_SUBSCRIPTION_TERM" $"/subscriptions/{index}/term" "Subscription term is required."
            requireUtcRepresentable $"/subscriptions/{index}/startedAt" subscription.StartedAt
            requireUtcRepresentable $"/subscriptions/{index}/expiresAt" subscription.ExpiresAt
            if subscription.ExpiresAt <= subscription.StartedAt then fail "SUBSCRIPTION_EXPIRY_MUST_FOLLOW_START" $"/subscriptions/{index}/expiresAt" "Subscription expiry must be later than start."
            match subscription.Status, subscription.CancelledAt with
            | B.Active, None -> ()
            | B.Active, Some _ -> fail "INCONSISTENT_SUBSCRIPTION_STATUS" $"/subscriptions/{index}/cancelledAt" "An active subscription cannot have a cancellation instant."
            | B.Cancelled, None -> fail "INCONSISTENT_SUBSCRIPTION_STATUS" $"/subscriptions/{index}/cancelledAt" "A cancelled subscription requires a cancellation instant."
            | B.Cancelled, Some cancelledAt when cancelledAt < subscription.StartedAt -> fail "CANCEL_BEFORE_START" $"/subscriptions/{index}/cancelledAt" "Cancellation cannot precede the subscription start."
            | B.Cancelled, Some cancelledAt -> requireUtcRepresentable $"/subscriptions/{index}/cancelledAt" cancelledAt)

        document.Invoices
        |> List.iteri (fun index invoice ->
            if invoice.Lines.Length > MaximumCollectionItems then
                fail "COLLECTION_TOO_LARGE" $"/invoices/{index}/lines" $"Collection exceeds the {MaximumCollectionItems}-item limit."
            let customerId = B.CustomerId.toString invoice.CustomerId
            if not (Set.contains customerId customers) then failRef "invoices" index "customerId" customerId
            requireUtcRepresentable $"/invoices/{index}/createdAt" invoice.CreatedAt
            if List.isEmpty invoice.Lines then fail "EMPTY_INVOICE" $"/invoices/{index}/lines" "An invoice requires at least one line."
            let totals =
                invoice.Lines
                |> List.mapi (fun lineIndex line ->
                    let productId = B.ProductId.toString line.ProductId
                    if not (Set.contains productId products) then failRef $"invoices/{index}/lines" lineIndex "productId" productId
                    if line.Quantity <= 0 then fail "INVALID_QUANTITY" $"/invoices/{index}/lines/{lineIndex}/quantity" "Invoice quantities must be positive Int32 values."
                    if B.Money.isNegative line.UnitPrice then fail "NEGATIVE_PRODUCT_PRICE" $"/invoices/{index}/lines/{lineIndex}/unitPriceMinorUnits" "Invoice line prices must be nonnegative."
                    if String.IsNullOrWhiteSpace line.Description then fail "INVALID_TEXT" $"/invoices/{index}/lines/{lineIndex}/description" "Invoice line description is required."
                    match B.Money.multiplyByQuantity line.UnitPrice line.Quantity with
                    | Error error -> fail (B.DomainError.code error) $"/invoices/{index}/lines/{lineIndex}/lineTotalMinorUnits" (B.DomainError.message error)
                    | Ok expected when expected <> line.LineTotal -> fail "INVOICE_LINE_TOTAL_MISMATCH" $"/invoices/{index}/lines/{lineIndex}/lineTotalMinorUnits" "Line total does not equal exact minor-unit multiplication."
                    | Ok _ -> line.LineTotal)
            match B.Money.sum totals with
            | Error error -> fail (B.DomainError.code error) $"/invoices/{index}/totalMinorUnits" (B.DomainError.message error)
            | Ok total when total <> invoice.Total -> fail "INVOICE_TOTAL_MISMATCH" $"/invoices/{index}/totalMinorUnits" "Invoice total does not equal the exact sum of its lines."
            | Ok _ -> ())

        uniqueValues "/payments" (document.Payments |> List.mapi (fun index payment -> $"/payments/{index}/invoiceId", B.InvoiceId.toString payment.InvoiceId))
        let paidInvoiceIds = HashSet<string>(StringComparer.Ordinal)
        document.Payments
        |> List.iteri (fun index payment ->
            requireUtcRepresentable $"/payments/{index}/paidAt" payment.PaidAt
            let invoiceId = B.InvoiceId.toString payment.InvoiceId
            match Map.tryFind payment.InvoiceId invoices with
            | None -> failRef "payments" index "invoiceId" invoiceId
            | Some invoice when invoice.Status <> B.Paid -> fail "PAYMENT_INVOICE_NOT_PAID" $"/payments/{index}/invoiceId" "A recorded payment must refer to a paid invoice."
            | Some invoice when payment.Amount <> invoice.Total -> fail "PAYMENT_AMOUNT_MISMATCH" $"/payments/{index}/amountMinorUnits" "A recorded payment must equal the invoice total."
            | Some _ ->
                if B.Money.isNegative payment.Amount || payment.Amount = B.Money.zero then fail "PAYMENT_AMOUNT_MUST_BE_POSITIVE" $"/payments/{index}/amountMinorUnits" "Payment amount must be positive."
                if String.IsNullOrWhiteSpace payment.ProviderReference then fail "INVALID_PROVIDER_RECEIPT" $"/payments/{index}/providerReference" "Provider reference must contain text."
                paidInvoiceIds.Add invoiceId |> ignore)

        document.Invoices
        |> List.iteri (fun index invoice ->
            let invoiceId = B.InvoiceId.toString invoice.Id
            let hasPayment = paidInvoiceIds.Contains invoiceId
            match invoice.Status, hasPayment with
            | B.Open, false
            | B.Paid, true -> ()
            | B.Open, true -> fail "INCONSISTENT_INVOICE_STATUS" $"/invoices/{index}/status" "Open invoices cannot have recorded payments."
            | B.Paid, false -> fail "INCONSISTENT_INVOICE_STATUS" $"/invoices/{index}/status" "Paid invoices require a recorded payment.")

        let validateEmails collection messages =
            messages
            |> List.iteri (fun index message ->
                match B.Email.create (B.Email.value message.To) with
                | Error _ -> fail "INVALID_EMAIL" $"/{collection}/{index}/to" "Email must satisfy the documented reference policy."
                | Ok _ -> ()
                if String.IsNullOrWhiteSpace message.Subject then fail "INVALID_EMAIL_MESSAGE" $"/{collection}/{index}/subject" "Email subject is required."
                if String.IsNullOrWhiteSpace message.Body then fail "INVALID_EMAIL_MESSAGE" $"/{collection}/{index}/body" "Email body is required.")
        validateEmails "pendingEmails" document.PendingEmails
        validateEmails "sentEmails" document.SentEmails
        document.ProviderOutcomes
        |> List.iteri (fun index outcome ->
            match outcome with
            | PaymentAccepted(reference, amount) ->
                if String.IsNullOrWhiteSpace reference then fail "INVALID_PROVIDER_RECEIPT" $"/providerOutcomes/{index}/reference" "Provider reference must contain text."
                if not (B.Money.isPositive amount) then fail "PAYMENT_AMOUNT_MUST_BE_POSITIVE" $"/providerOutcomes/{index}/amountMinorUnits" "Accepted payment amount must be positive."
            | PaymentDeclined message
            | EmailFailed message ->
                if String.IsNullOrWhiteSpace message then fail "INVALID_PROVIDER_OUTCOME" $"/providerOutcomes/{index}/message" "Provider outcome message must contain text."
            | EmailAccepted -> ())

    let private parseDocumentCore (root: JsonElement) =
        let rootPath = "$"
        let rootFields =
            objectFields
                rootPath
                root
                [ "schema"; "version"; "customers"; "products"; "subscriptions"; "invoices"; "payments"; "pendingEmails"; "sentEmails"; "providerOutcomes" ]
                [ "schema"; "version"; "customers"; "products"; "subscriptions"; "invoices"; "payments"; "pendingEmails"; "sentEmails"; "providerOutcomes" ]
        let schema = requiredString rootPath rootFields "schema"
        if schema <> Schema then fail "SCHEMA_MISMATCH" "/schema" $"Expected schema '{Schema}'."
        let version = requiredInt32 rootPath rootFields "version"
        if version <> Version then fail "UNSUPPORTED_VERSION" "/version" $"Only schema version {Version} is supported."
        let document =
            { Customers = requiredArray rootPath rootFields "customers" parseCustomer
              Products = requiredArray rootPath rootFields "products" parseProduct
              Subscriptions = requiredArray rootPath rootFields "subscriptions" parseSubscription
              Invoices = requiredArray rootPath rootFields "invoices" parseInvoice
              Payments = requiredArray rootPath rootFields "payments" parsePayment
              PendingEmails = requiredArray rootPath rootFields "pendingEmails" parseEmailRecord
              SentEmails = requiredArray rootPath rootFields "sentEmails" parseEmailRecord
              ProviderOutcomes = requiredArray rootPath rootFields "providerOutcomes" parseProviderOutcome }
        validateDocumentCore document
        normalize document

    /// Parse a complete versioned fixture. This strict path never fills missing
    /// fields; callers wanting deterministic defaults must use `Fragment`.
    let parse (json: string) =
        try
            if isNull json then fail "INVALID_JSON" "$" "Fixture JSON cannot be null."
            let byteCount = Encoding.UTF8.GetByteCount json
            if byteCount > MaximumDocumentBytes then fail "DOCUMENT_TOO_LARGE" "$" $"Fixture exceeds the {MaximumDocumentBytes}-byte limit."
            use parsed = JsonDocument.Parse(json, jsonOptions)
            Ok(parseDocumentCore parsed.RootElement)
        with
        | ContractFailure error -> Error error
        | :? JsonException as error -> Error(diagnostic "INVALID_JSON" "$" error.Message)
        | :? ArgumentException as error -> Error(diagnostic "INVALID_JSON" "$" error.Message)

    let private writeInstant (writer: Utf8JsonWriter) (value: DateTimeOffset) =
        writer.WriteStringValue(value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))

    let private writeCustomer (writer: Utf8JsonWriter) (value: CustomerRecord) =
        writer.WriteStartObject()
        writer.WriteString("id", B.CustomerId.toString value.Id)
        writer.WriteString("email", B.Email.value value.Email)
        writer.WriteString("kind", value.Kind.Trim())
        writer.WriteNumber("balanceMinorUnits", B.Money.minorUnits value.Balance)
        writer.WritePropertyName("createdAt")
        writeInstant writer value.CreatedAt
        writer.WriteEndObject()

    let private writeProduct (writer: Utf8JsonWriter) (value: ProductRecord) =
        writer.WriteStartObject()
        writer.WriteString("id", B.ProductId.toString value.Id)
        writer.WriteString("name", value.Name.Trim())
        writer.WriteNumber("unitPriceMinorUnits", B.Money.minorUnits value.UnitPrice)
        writer.WriteEndObject()

    let private writeSubscription (writer: Utf8JsonWriter) (value: SubscriptionRecord) =
        writer.WriteStartObject()
        writer.WriteString("id", B.SubscriptionId.toString value.Id)
        writer.WriteString("customerId", B.CustomerId.toString value.CustomerId)
        writer.WriteString("productId", B.ProductId.toString value.ProductId)
        writer.WriteString("term", value.Term.Trim())
        writer.WriteString("startedAt", value.StartedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
        writer.WriteString("expiresAt", value.ExpiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
        writer.WriteString("status", if value.Status = B.Active then "active" else "cancelled")
        writer.WritePropertyName("cancelledAt")
        match value.CancelledAt with
        | Some cancelledAt -> writeInstant writer cancelledAt
        | None -> writer.WriteNullValue()
        writer.WriteEndObject()

    let private writeInvoiceLine (writer: Utf8JsonWriter) (value: InvoiceLineRecord) =
        writer.WriteStartObject()
        writer.WriteString("productId", B.ProductId.toString value.ProductId)
        writer.WriteString("description", value.Description.Trim())
        writer.WriteNumber("quantity", value.Quantity)
        writer.WriteNumber("unitPriceMinorUnits", B.Money.minorUnits value.UnitPrice)
        writer.WriteNumber("lineTotalMinorUnits", B.Money.minorUnits value.LineTotal)
        writer.WriteEndObject()

    let private writeInvoice (writer: Utf8JsonWriter) (value: InvoiceRecord) =
        writer.WriteStartObject()
        writer.WriteString("id", B.InvoiceId.toString value.Id)
        writer.WriteString("customerId", B.CustomerId.toString value.CustomerId)
        writer.WritePropertyName("lines")
        writer.WriteStartArray()
        value.Lines |> List.iter (writeInvoiceLine writer)
        writer.WriteEndArray()
        writer.WriteNumber("totalMinorUnits", B.Money.minorUnits value.Total)
        writer.WriteString("createdAt", value.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
        writer.WriteString("status", if value.Status = B.Open then "open" else "paid")
        writer.WriteEndObject()

    let private writePayment (writer: Utf8JsonWriter) (value: PaymentRecord) =
        writer.WriteStartObject()
        writer.WriteString("id", B.PaymentId.toString value.Id)
        writer.WriteString("invoiceId", B.InvoiceId.toString value.InvoiceId)
        writer.WriteNumber("amountMinorUnits", B.Money.minorUnits value.Amount)
        writer.WriteString("providerReference", value.ProviderReference.Trim())
        writer.WriteString("paidAt", value.PaidAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
        writer.WriteEndObject()

    let private writeEmail (writer: Utf8JsonWriter) (value: EmailRecord) =
        writer.WriteStartObject()
        writer.WriteString("to", B.Email.value value.To)
        writer.WriteString("subject", value.Subject.Trim())
        writer.WriteString("body", value.Body.Trim())
        writer.WriteEndObject()

    let private writeOutcome (writer: Utf8JsonWriter) (value: ProviderOutcome) =
        writer.WriteStartObject()
        match value with
        | PaymentAccepted(reference, amount) ->
            writer.WriteString("kind", "payment-accepted")
            writer.WriteString("reference", reference.Trim())
            writer.WriteNumber("amountMinorUnits", B.Money.minorUnits amount)
        | PaymentDeclined message ->
            writer.WriteString("kind", "payment-declined")
            writer.WriteString("message", message)
        | EmailAccepted -> writer.WriteString("kind", "email-accepted")
        | EmailFailed message ->
            writer.WriteString("kind", "email-failed")
            writer.WriteString("message", message)
        writer.WriteEndObject()

    let private writeDocument (document: FixtureDocument) =
        let buffer = ArrayBufferWriter<byte>()
        use writer = new Utf8JsonWriter(buffer, JsonWriterOptions(Indented = false, Encoder = JavaScriptEncoder.Default))
        writer.WriteStartObject()
        writer.WriteString("schema", Schema)
        writer.WriteNumber("version", Version)
        writer.WritePropertyName("customers")
        writer.WriteStartArray()
        document.Customers |> List.iter (writeCustomer writer)
        writer.WriteEndArray()
        writer.WritePropertyName("products")
        writer.WriteStartArray()
        document.Products |> List.iter (writeProduct writer)
        writer.WriteEndArray()
        writer.WritePropertyName("subscriptions")
        writer.WriteStartArray()
        document.Subscriptions |> List.iter (writeSubscription writer)
        writer.WriteEndArray()
        writer.WritePropertyName("invoices")
        writer.WriteStartArray()
        document.Invoices |> List.iter (writeInvoice writer)
        writer.WriteEndArray()
        writer.WritePropertyName("payments")
        writer.WriteStartArray()
        document.Payments |> List.iter (writePayment writer)
        writer.WriteEndArray()
        writer.WritePropertyName("pendingEmails")
        writer.WriteStartArray()
        document.PendingEmails |> List.iter (writeEmail writer)
        writer.WriteEndArray()
        writer.WritePropertyName("sentEmails")
        writer.WriteStartArray()
        document.SentEmails |> List.iter (writeEmail writer)
        writer.WriteEndArray()
        writer.WritePropertyName("providerOutcomes")
        writer.WriteStartArray()
        document.ProviderOutcomes |> List.iter (writeOutcome writer)
        writer.WriteEndArray()
        writer.WriteEndObject()
        writer.Flush()
        Encoding.UTF8.GetString(buffer.WrittenSpan)

    /// Emit deterministic compact JSON with stable property order, sorted map-like
    /// entity collections, normalized GUIDs/instants, and exact integer money.
    let serializeCanonical document =
        try
            validateDocumentCore document
            let json = document |> normalize |> writeDocument
            if Encoding.UTF8.GetByteCount json > MaximumDocumentBytes then
                fail "DOCUMENT_TOO_LARGE" "$" $"Fixture exceeds the {MaximumDocumentBytes}-byte limit."
            Ok json
        with
        | ContractFailure error -> Error error

    let sha256Canonical document =
        serializeCanonical document
        |> Result.map (Encoding.UTF8.GetBytes >> SHA256.HashData >> Convert.ToHexString >> fun value -> value.ToLowerInvariant())

    /// Stable JSON-neutral projection for a domain error. Message text is the
    /// reference fixture's canonical message; `Code` is the comparison key.
    let domainError (error: B.DomainError) =
        { Code = B.DomainError.code error
          Path = ""
          Message = B.DomainError.message error }

    let private resultOrFail path = function
        | Ok value -> value
        | Error error -> fail (B.DomainError.code error) path (B.DomainError.message error)

    module Projection =
        let customer (value: B.Customer) =
            { Id = B.Customer.id value
              Email = B.Customer.email value
              Kind = B.Customer.kind value
              Balance = B.Customer.balance value
              CreatedAt = B.Customer.createdAt value }

        let product (value: B.Product) =
            { Id = B.Product.id value
              Name = B.Product.name value
              UnitPrice = B.Product.unitPrice value }

        let subscription (value: B.Subscription) =
            { Id = B.Subscription.id value
              CustomerId = B.Subscription.customerId value
              ProductId = B.Subscription.productId value
              Term = B.Subscription.term value
              StartedAt = B.Subscription.startedAt value
              ExpiresAt = B.Subscription.expiresAt value
              Status = B.Subscription.status value
              CancelledAt = B.Subscription.cancelledAt value }

        let private invoiceLine (value: B.InvoiceLine) =
            { ProductId = B.InvoiceLine.productId value
              Description = B.InvoiceLine.description value
              Quantity = B.InvoiceLine.quantity value
              UnitPrice = B.InvoiceLine.unitPrice value
              LineTotal = B.InvoiceLine.lineTotal value }

        let invoice (value: B.Invoice) =
            { Id = B.Invoice.id value
              CustomerId = B.Invoice.customerId value
              Lines = B.Invoice.lines value |> List.map invoiceLine
              Total = B.Invoice.total value
              CreatedAt = B.Invoice.createdAt value
              Status = B.Invoice.status value }

        let payment (value: B.Payment) =
            { Id = B.Payment.id value
              InvoiceId = B.Payment.invoiceId value
              Amount = B.Payment.amount value
              ProviderReference = B.Payment.providerReference value
              PaidAt = B.Payment.paidAt value }

        let email (value: B.EmailMessage) =
            { To = B.EmailMessage.toAddress value
              Subject = B.EmailMessage.subject value
              Body = B.EmailMessage.body value }

        let store (store: B.Store) (known: KnownEntityIds) =
            try
                let unique path ids format =
                    ids
                    |> List.mapi (fun index id -> $"/{path}/{index}", format id)
                    |> uniqueValues ("/" + path)
                unique "customers" known.Customers B.CustomerId.toString
                unique "products" known.Products B.ProductId.toString
                unique "subscriptions" known.Subscriptions B.SubscriptionId.toString
                unique "invoices" known.Invoices B.InvoiceId.toString
                unique "payments" known.Payments B.PaymentId.toString
                let find path getter ids project =
                    ids
                    |> List.mapi (fun index id ->
                        match getter id store with
                        | Some value -> project value
                        | None -> fail "STORE_LEDGER_ID_MISSING" $"/{path}/{index}" "Identity ledger references an entity absent from the Store.")
                let customers = find "customers" B.Store.customer known.Customers customer
                let products = find "products" B.Store.product known.Products product
                let subscriptions = find "subscriptions" B.Store.subscription known.Subscriptions subscription
                let invoices = find "invoices" B.Store.invoice known.Invoices invoice
                let payments = find "payments" B.Store.payment known.Payments payment
                let summary = B.Store.summary store
                if summary.Customers <> customers.Length
                   || summary.Products <> products.Length
                   || summary.Subscriptions <> subscriptions.Length
                   || summary.Invoices <> invoices.Length
                   || summary.Payments <> payments.Length then
                    fail "STORE_LEDGER_COUNT_MISMATCH" "/identityLedger" "Known identifiers do not cover every entity reported by the Store summary."
                let document =
                    { Customers = customers
                      Products = products
                      Subscriptions = subscriptions
                      Invoices = invoices
                      Payments = payments
                      PendingEmails = B.Store.pendingEmails store |> List.map email
                      SentEmails = B.Store.sentEmails store |> List.map email
                      ProviderOutcomes = [] }
                if summary.PendingEmails <> document.PendingEmails.Length then
                    fail "STORE_LEDGER_COUNT_MISMATCH" "/pendingEmails" "Projected pending email count differs from the Store summary."
                if summary.SentEmails <> document.SentEmails.Length then
                    fail "STORE_LEDGER_COUNT_MISMATCH" "/sentEmails" "Projected sent email count differs from the Store summary."
                validateDocumentCore document
                Ok(normalize document)
            with
            | ContractFailure error -> Error error

    module Factory =
        let private instant year month day hour minute = DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero)

        /// Fixed, policy-neutral seed data used for repeatable fixture tests.
        /// Raw `Kind` and `Term` values are input data, not predicates or rules.
        let baseline () =
            try
                let customerId = B.CustomerId.parse "10000000-0000-0000-0000-000000000001" |> resultOrFail "/customers/0/id"
                let productId = B.ProductId.parse "20000000-0000-0000-0000-000000000001" |> resultOrFail "/products/0/id"
                let subscriptionId = B.SubscriptionId.parse "30000000-0000-0000-0000-000000000001" |> resultOrFail "/subscriptions/0/id"
                let email = B.Email.create "ada@example.test" |> resultOrFail "/customers/0/email"
                let createdAt = instant 2026 1 1 12 0
                let customer = B.Customer.create customerId email "regular" (B.Money.ofMinorUnits 1250L) createdAt |> resultOrFail "/customers/0"
                let product = B.Product.create productId "Monthly plan" (B.Money.ofMinorUnits 199L) |> resultOrFail "/products/0"
                let start = instant 2026 1 2 12 0
                let expiry = instant 2026 2 1 12 0
                let store =
                    B.Store.empty
                    |> B.Store.addCustomer customer
                    |> resultOrFail "/customers/0"
                    |> B.Store.addProduct product
                    |> resultOrFail "/products/0"
                let store, _ = B.Subscription.start store subscriptionId customerId productId "monthly" start expiry |> resultOrFail "/subscriptions/0"
                let knownIds =
                    { Customers = [ customerId ]
                      Products = [ productId ]
                      Subscriptions = [ subscriptionId ]
                      Invoices = []
                      Payments = [] }
                match Projection.store store knownIds with
                | Ok projection -> Ok { Store = store; KnownIds = knownIds; Projection = projection }
                | Error error -> Error error
            with
            | ContractFailure error -> Error error

    module Fragment =
        type CustomerFragment =
            { Id: B.CustomerId option
              Email: B.Email option
              Kind: string option
              Balance: B.Money option
              CreatedAt: DateTimeOffset option }

        type ParsedCustomerFragment =
            { SourceSha256: string
              Customers: CustomerFragment list }

        let private optionalFragmentString (path: string) (fields: Dictionary<string, JsonElement>) (name: string) (parseValue: string -> string -> 'value) =
            match fields.TryGetValue name with
            | false, _ -> None
            | true, value when value.ValueKind = JsonValueKind.String ->
                let text = value.GetString()
                if isNull text then fail "TYPE_MISMATCH" (pathProperty path name) "Expected a non-null string."
                Some(parseValue (pathProperty path name) text)
            | true, _ -> fail "TYPE_MISMATCH" (pathProperty path name) "Expected a JSON string."

        let private optionalInteger (path: string) (fields: Dictionary<string, JsonElement>) (name: string) =
            match fields.TryGetValue name with
            | false, _ -> None
            | true, value -> Some(parseIntegerToken (pathProperty path name) value Int64.MinValue Int64.MaxValue |> B.Money.ofMinorUnits)

        let private optionalInstant path fields name =
            optionalFragmentString path fields name parseInstant

        let parseCustomerFragments (json: string) =
            try
                if isNull json then fail "INVALID_JSON" "$" "Fragment JSON cannot be null."
                let bytes = Encoding.UTF8.GetBytes json
                if bytes.Length > MaximumDocumentBytes then fail "DOCUMENT_TOO_LARGE" "$" $"Fragment exceeds the {MaximumDocumentBytes}-byte limit."
                use parsed = JsonDocument.Parse(json, jsonOptions)
                let rootFields = objectFields "$" parsed.RootElement [ "schema"; "version"; "customers" ] [ "schema"; "version"; "customers" ]
                let schema = requiredString "$" rootFields "schema"
                if schema <> Schema then fail "SCHEMA_MISMATCH" "/schema" $"Expected schema '{Schema}'."
                let version = requiredInt32 "$" rootFields "version"
                if version <> Version then fail "UNSUPPORTED_VERSION" "/version" $"Only schema version {Version} is supported."
                let parseCustomerFragment path element =
                    let fields = objectFields path element [ "id"; "email"; "kind"; "balanceMinorUnits"; "createdAt" ] []
                    { Id = optionalFragmentString path fields "id" parseCustomerId
                      Email = optionalFragmentString path fields "email" parseEmail
                      Kind = optionalFragmentString path fields "kind" (fun fieldPath value ->
                          let trimmed = value.Trim()
                          if String.IsNullOrWhiteSpace trimmed then fail "INVALID_TEXT" fieldPath "Customer kind must contain text."
                          trimmed)
                      Balance = optionalInteger path fields "balanceMinorUnits"
                      CreatedAt = optionalInstant path fields "createdAt" }
                let customers = requiredArray "$" rootFields "customers" parseCustomerFragment
                let sourceSha256 = SHA256.HashData bytes |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
                Ok { SourceSha256 = sourceSha256; Customers = customers }
            with
            | ContractFailure error -> Error error
            | :? JsonException as error -> Error(diagnostic "INVALID_JSON" "$" error.Message)

        let expand (fragment: ParsedCustomerFragment) =
            try
                let defaults = ResizeArray<DefaultApplied>()
                let fallbackGuid (ordinal: int) =
                    let suffix = ordinal.ToString("D12", CultureInfo.InvariantCulture)
                    $"11000000-0000-0000-0000-{suffix}"
                let fixedInstant = DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)
                let customers =
                    fragment.Customers
                    |> List.mapi (fun index part ->
                        let basePath = $"/customers/{index}"
                        let useDefault field optionValue defaultValue format =
                            match optionValue with
                            | Some value -> value
                            | None ->
                                defaults.Add({ Path = $"{basePath}/{field}"; Value = format defaultValue })
                                defaultValue
                        let idDefault = fallbackGuid (index + 1)
                        let id = useDefault "id" part.Id (parseCustomerId $"{basePath}/id" idDefault) B.CustomerId.toString
                        let emailDefault = parseEmail $"{basePath}/email" $"customer-{index + 1}@example.test"
                        let address = useDefault "email" part.Email emailDefault B.Email.value
                        let kind = useDefault "kind" part.Kind "standard" (fun value -> value)
                        let balance = useDefault "balanceMinorUnits" part.Balance B.Money.zero (B.Money.minorUnits >> string)
                        let createdAt = useDefault "createdAt" part.CreatedAt fixedInstant (fun value -> value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
                        let customer = B.Customer.create id address kind balance createdAt |> resultOrFail basePath
                        Projection.customer customer)
                let document = { empty with Customers = customers } |> normalize
                validateDocumentCore document
                Ok
                    { Document = document
                      Provenance =
                        { SourceSha256 = fragment.SourceSha256
                          DefaultsApplied = List.ofSeq defaults } }
            with
            | ContractFailure error -> Error error
