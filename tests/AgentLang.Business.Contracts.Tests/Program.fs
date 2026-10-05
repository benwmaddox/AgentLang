namespace AgentLang.Business.Contracts.Tests

open System
open System.Globalization
open System.Text.Json
open AgentLang.Business.Domain
open AgentLang.Business.Contracts

module Program =
    let mutable private assertions = 0
    let mutable private groups = 0

    let private check condition message =
        assertions <- assertions + 1
        if not condition then failwith message

    let private equal expected actual message =
        assertions <- assertions + 1
        if expected <> actual then failwith $"{message}: expected {expected}, got {actual}"

    let private ok label = function
        | Ok value -> value
        | Error error -> failwith $"{label}: unexpected {error.Code} at {error.Path}: {error.Message}"

    let private errorCode expected label = function
        | Error error ->
            assertions <- assertions + 1
            if error.Code <> expected then
                failwith $"{label}: expected {expected}, got {error.Code} at {error.Path}: {error.Message}"
        | Ok _ -> failwith $"{label}: expected {expected}, got success"

    let private group name action =
        action ()
        groups <- groups + 1
        printfn "PASS %s" name

    let private customerId value = CustomerId.parse value |> Result.defaultWith (fun error -> failwith (DomainError.message error))
    let private productId value = ProductId.parse value |> Result.defaultWith (fun error -> failwith (DomainError.message error))
    let private invoiceId value = InvoiceId.parse value |> Result.defaultWith (fun error -> failwith (DomainError.message error))
    let private paymentId value = PaymentId.parse value |> Result.defaultWith (fun error -> failwith (DomainError.message error))

    let private baseline () = Contract.Factory.baseline () |> ok "baseline factory"

    let private baselineJson () =
        baseline().Projection |> Contract.serializeCanonical |> ok "serialize baseline"

    let private replaceOnce (before: string) (after: string) (text: string) =
        if not (text.Contains(before, StringComparison.Ordinal)) then failwith $"replacement source was not present: {before}"
        text.Replace(before, after, StringComparison.Ordinal)

    let private jsonStringToken (value: string) =
        JsonSerializer.Serialize(value).Trim('"')

    let private testDeterministicBaselineAndCanonicalHash () =
        let first = baseline ()
        let second = baseline ()
        equal (Some first.Projection) (Contract.parse (baselineJson ()) |> Result.toOption) "baseline serializes and parses"
        let firstJson = first.Projection |> Contract.serializeCanonical |> ok "first canonical JSON"
        let secondJson = second.Projection |> Contract.serializeCanonical |> ok "second canonical JSON"
        equal firstJson secondJson "factory output is byte deterministic"
        equal
            (first.Projection |> Contract.sha256Canonical |> ok "first fixture hash")
            (second.Projection |> Contract.sha256Canonical |> ok "second fixture hash")
            "factory hash is deterministic"
        equal "regular" first.Projection.Customers.Head.Kind "customer kind is raw seed data"
        equal "monthly" first.Projection.Subscriptions.Head.Term "subscription term is raw seed data"
        equal 1250L (first.Projection.Customers.Head.Balance |> Money.minorUnits) "money is signed Int64 minor units"

    let private testStrictDocumentParsing () =
        let json = baselineJson ()
        errorCode "MISSING_FIELD" "missing required collection" (Contract.parse (replaceOnce ",\"sentEmails\":[]" "" json))
        errorCode "UNKNOWN_FIELD" "unknown root field" (Contract.parse (json.Replace("\"schema\":", "\"unexpected\":true,\"schema\":", StringComparison.Ordinal)))
        errorCode "DUPLICATE_FIELD" "duplicate root field" (Contract.parse (json.Replace("\"schema\":", "\"schema\":\"agentlang.business.fixture\",\"schema\":", StringComparison.Ordinal)))
        errorCode "TYPE_MISMATCH" "wrong customer kind type" (Contract.parse (replaceOnce "\"kind\":\"regular\"" "\"kind\":true" json))
        errorCode "INVALID_JSON" "malformed JSON" (Contract.parse "{")
        errorCode "INVALID_JSON" "null input" (Contract.parse null)
        errorCode "UNSUPPORTED_VERSION" "unsupported version" (Contract.parse (replaceOnce "\"version\":1" "\"version\":2" json))

    let private testIdentifiersEmailAndCanonicalNormalization () =
        let json = baselineJson ()
        let canonicalCustomerId = "10000000-0000-0000-0000-00000000000a"
        let originalId = "10000000-0000-0000-0000-000000000001"
        let alternateId = "{10000000-0000-0000-0000-00000000000A}"
        let alternateGuidJson = json.Replace(originalId, alternateId, StringComparison.Ordinal)
        let parsedGuid = Contract.parse alternateGuidJson |> ok "alternate Guid form"
        let normalizedGuidJson = Contract.serializeCanonical parsedGuid |> ok "canonical Guid output"
        check (normalizedGuidJson.Contains(canonicalCustomerId, StringComparison.Ordinal)) "GUID canonicalizes to lower-case D format"
        check (not (normalizedGuidJson.Contains(alternateId, StringComparison.Ordinal))) "alternate GUID spelling is not preserved"

        let exactBoundary = String.replicate 64 "a" + "@b." + String.replicate 185 "c" + ".d"
        equal 254 exactBoundary.Length "email test fixture is exactly at policy maximum"
        check (Email.create exactBoundary |> Result.isOk) "reference Email accepts valid 254-character boundary"
        check (Email.create (exactBoundary + "x") |> Result.isError) "reference Email rejects 255 characters"
        check (Email.create null |> Result.isError) "reference Email handles null"
        for invalid in [ "a@b"; "a..b@example.test"; "a b@example.test"; "a@@example.test"; "@example.test" ] do
            check (Email.create invalid |> Result.isError) $"reference Email rejects '{invalid}'"
        for valid in [ "ada@example.test"; "first.last+tag@sub.example.com"; "A_1%tag@example-domain.test" ] do
            check (Email.create valid |> Result.isOk) $"reference Email accepts '{valid}'"

        let padded = replaceOnce "\"kind\":\"regular\"" "\"kind\":\"  regular  \"" json
        let normalized = Contract.parse padded |> ok "constructor trimming"
        equal "regular" normalized.Customers.Head.Kind "strict parse preserves Customer.create trimming"
        errorCode "INVALID_EMAIL" "strict document rejects invalid email" (Contract.parse (replaceOnce "ada@example.test" "a..b@example.test" json))
        let boundary = Email.create exactBoundary |> Result.defaultWith (fun error -> failwith (DomainError.message error))
        let seed = baseline ()
        let withBoundaryEmail = { seed.Projection with Customers = [ { seed.Projection.Customers.Head with Email = boundary } ] }
        let boundaryJson = Contract.serializeCanonical withBoundaryEmail |> ok "serialize boundary email"
        equal exactBoundary (Contract.parse boundaryJson |> ok "parse boundary email" |> fun document -> document.Customers.Head.Email |> Email.value) "strict parser accepts reference email at maximum length"

        let paddedNames =
            json
            |> replaceOnce "\"name\":\"Monthly plan\"" "\"name\":\"  Monthly plan  \""
            |> replaceOnce "\"term\":\"monthly\"" "\"term\":\"  monthly  \""
        let trimmedNames = Contract.parse paddedNames |> ok "name and term constructors"
        equal "Monthly plan" trimmedNames.Products.Head.Name "Product.create trimming is retained"
        equal "monthly" trimmedNames.Subscriptions.Head.Term "Subscription.start trimming is retained"

    let private testExactMoneyTimeAndQuantityRepresentations () =
        let json = baselineJson ()
        errorCode "TYPE_MISMATCH" "fractional minor units" (Contract.parse (replaceOnce "\"balanceMinorUnits\":1250" "\"balanceMinorUnits\":1250.0" json))
        errorCode "TYPE_MISMATCH" "exponent minor units" (Contract.parse (replaceOnce "\"balanceMinorUnits\":1250" "\"balanceMinorUnits\":1.25e3" json))
        errorCode "INTEGER_OUT_OF_RANGE" "Int64 overflow" (Contract.parse (replaceOnce "\"balanceMinorUnits\":1250" "\"balanceMinorUnits\":9223372036854775808" json))

        let canonicalInstant = baseline().Projection.Customers.Head.CreatedAt
        let utc = canonicalInstant.ToString("O", CultureInfo.InvariantCulture)
        let utcToken = jsonStringToken utc
        let equalOffsetInstant = canonicalInstant.ToOffset(TimeSpan.FromHours(-5.0))
        let equalOffsetToken = equalOffsetInstant.ToString("O", CultureInfo.InvariantCulture) |> jsonStringToken
        let equalOffset = replaceOnce utcToken equalOffsetToken json
        let normalizedUtc = Contract.parse equalOffset |> ok "UTC-equivalent offset"
        equal json (normalizedUtc |> Contract.serializeCanonical |> ok "UTC canonical output") "offset input canonicalizes to UTC"
        errorCode "INVALID_INSTANT" "missing explicit timezone" (Contract.parse (replaceOnce utcToken (jsonStringToken "2026-01-01T12:00:00") json))
        errorCode "INVALID_INSTANT" "invalid UTC normalization range" (Contract.parse (replaceOnce utcToken (jsonStringToken "0001-01-01T00:00:00+01:00") json))

        let seed = baseline ()
        let line =
            { ProductId = seed.Projection.Products.Head.Id
              Description = "Monthly plan"
              Quantity = 1
              UnitPrice = Money.ofMinorUnits 199L
              LineTotal = Money.ofMinorUnits 199L }
        let invoice =
            { Id = invoiceId "40000000-0000-0000-0000-000000000001"
              CustomerId = seed.Projection.Customers.Head.Id
              Lines = [ line ]
              Total = Money.ofMinorUnits 199L
              CreatedAt = DateTimeOffset(2026, 1, 3, 12, 0, 0, TimeSpan.Zero)
              Status = InvoiceStatus.Open }
        let withInvoice = { seed.Projection with Invoices = [ invoice ] }
        let invoiceJson = Contract.serializeCanonical withInvoice |> ok "serialize quantity fixture"
        errorCode "INTEGER_OUT_OF_RANGE" "quantity over Int32" (Contract.parse (replaceOnce "\"quantity\":1" "\"quantity\":2147483648" invoiceJson))
        errorCode "INVALID_QUANTITY" "zero quantity" (Contract.parse (replaceOnce "\"quantity\":1" "\"quantity\":0" invoiceJson))
        errorCode "INVOICE_LINE_TOTAL_MISMATCH" "exact line arithmetic" (Contract.serializeCanonical { withInvoice with Invoices = [ { invoice with Lines = [ { line with LineTotal = Money.ofMinorUnits 200L } ] } ] })
        errorCode "MONEY_OVERFLOW" "checked line multiplication" (Contract.serializeCanonical { withInvoice with Invoices = [ { invoice with Lines = [ { line with Quantity = Int32.MaxValue; UnitPrice = Money.ofMinorUnits Int64.MaxValue; LineTotal = Money.zero } ]; Total = Money.zero } ] })

        let activeWithCancellation = { seed.Projection.Subscriptions.Head with CancelledAt = Some(DateTimeOffset(2026, 1, 4, 12, 0, 0, TimeSpan.Zero)) }
        errorCode "INCONSISTENT_SUBSCRIPTION_STATUS" "active subscription cannot have cancellation time" (Contract.serializeCanonical { seed.Projection with Subscriptions = [ activeWithCancellation ] })
        let cancelledWithoutTime = { seed.Projection.Subscriptions.Head with Status = SubscriptionStatus.Cancelled; CancelledAt = None }
        errorCode "INCONSISTENT_SUBSCRIPTION_STATUS" "cancelled subscription requires cancellation time" (Contract.serializeCanonical { seed.Projection with Subscriptions = [ cancelledWithoutTime ] })

    let private testCompleteWireRoundTrip () =
        let seed = baseline ()
        let customer = seed.Projection.Customers.Head
        let product = seed.Projection.Products.Head
        let invoiceIdValue = invoiceId "40000000-0000-0000-0000-000000000001"
        let paymentIdValue = paymentId "50000000-0000-0000-0000-000000000001"
        let line =
            { ProductId = product.Id
              Description = product.Name
              Quantity = 2
              UnitPrice = product.UnitPrice
              LineTotal = Money.ofMinorUnits 398L }
        let invoice =
            { Id = invoiceIdValue
              CustomerId = customer.Id
              Lines = [ line ]
              Total = Money.ofMinorUnits 398L
              CreatedAt = DateTimeOffset(2026, 1, 3, 12, 0, 0, TimeSpan.Zero)
              Status = InvoiceStatus.Paid }
        let payment =
            { Id = paymentIdValue
              InvoiceId = invoiceIdValue
              Amount = Money.ofMinorUnits 398L
              ProviderReference = "  provider-1  "
              PaidAt = DateTimeOffset(2026, 1, 3, 13, 0, 0, TimeSpan.Zero) }
        let toAddress = Email.create "ada@example.test" |> Result.defaultWith (fun error -> failwith (DomainError.message error))
        let doc: FixtureDocument =
            { seed.Projection with
                Invoices = [ invoice ]
                Payments = [ payment ]
                PendingEmails = [ { To = toAddress; Subject = "  Receipt  "; Body = "  Paid  " } ]
                SentEmails = []
                ProviderOutcomes = [ PaymentAccepted("  provider-1  ", Money.ofMinorUnits 398L); EmailFailed "  offline  " ] }
        let canonical = Contract.serializeCanonical doc |> ok "canonical full fixture"
        let parsed = Contract.parse canonical |> ok "parse full fixture"
        equal canonical (Contract.serializeCanonical parsed |> ok "recanonicalize full fixture") "all six entity kinds round-trip canonically"
        equal "provider-1" parsed.Payments.Head.ProviderReference "payment receipt follows reference trim policy"
        equal "Receipt" parsed.PendingEmails.Head.Subject "email subject follows reference trim policy"
        equal "Paid" parsed.PendingEmails.Head.Body "email body follows reference trim policy"
        match parsed.ProviderOutcomes with
        | [ PaymentAccepted(reference, amount); EmailFailed message ] ->
            equal "provider-1" reference "provider reference canonicalized"
            equal 398L (Money.minorUnits amount) "provider amount stays exact minor units"
            equal "  offline  " message "provider failure detail is preserved verbatim"
        | _ -> failwith "provider outcomes did not round-trip"

        let badTotal = { doc with Invoices = [ { invoice with Total = Money.ofMinorUnits 397L } ] }
        errorCode "INVOICE_TOTAL_MISMATCH" "invoice total integrity" (Contract.serializeCanonical badTotal)
        errorCode "PAYMENT_AMOUNT_MISMATCH" "payment matches invoice total" (Contract.serializeCanonical { doc with Payments = [ { payment with Amount = Money.ofMinorUnits 397L } ] })

    let private testDomainStoreProjectionAndLedger () =
        let seed = baseline ()
        let product = seed.Projection.Products.Head
        let customer = seed.Projection.Customers.Head
        let invoiceIdValue = invoiceId "40000000-0000-0000-0000-000000000001"
        let paymentIdValue = paymentId "50000000-0000-0000-0000-000000000001"
        let createdAt = DateTimeOffset(2026, 1, 3, 12, 0, 0, TimeSpan.Zero)
        let store, _ = Invoice.create seed.Store invoiceIdValue customer.Id [ product.Id, 2 ] createdAt |> Result.defaultWith (fun error -> failwith (DomainError.message error))
        let store, _ = Payment.submit store (PaymentProvider.deterministic "provider-1") paymentIdValue invoiceIdValue (Money.ofMinorUnits 398L) createdAt |> Result.defaultWith (fun error -> failwith (DomainError.message error))
        let store, _ = EmailOutbox.queue store customer.Id "Receipt" "Payment accepted" |> Result.defaultWith (fun error -> failwith (DomainError.message error))
        let store = EmailOutbox.deliverNext store EmailProvider.deterministic |> Result.defaultWith (fun error -> failwith (DomainError.message error))
        let known: KnownEntityIds =
            { seed.KnownIds with
                Invoices = [ invoiceIdValue ]
                Payments = [ paymentIdValue ] }
        let projected: FixtureDocument = Contract.Projection.store store known |> ok "project actual immutable Store"
        equal 398L (projected.Invoices.Head.Total |> Money.minorUnits) "actual invoice projection preserves exact total"
        equal "provider-1" projected.Payments.Head.ProviderReference "actual payment projection reads opaque entity accessors"
        equal 0 projected.PendingEmails.Length "delivered message leaves pending projection"
        equal 1 projected.SentEmails.Length "sent message is projected in FIFO state"
        equal projected (Contract.parse (Contract.serializeCanonical projected |> ok "projected canonical") |> ok "projected parse") "actual projection is JSON-neutral"

        let omittedCustomer = { known with Customers = [] }
        errorCode "STORE_LEDGER_COUNT_MISMATCH" "ledger omission is detected" (Contract.Projection.store store omittedCustomer)
        let duplicatePayment = { known with Payments = [ paymentIdValue; paymentIdValue ] }
        errorCode "DUPLICATE_IDENTIFIER" "ledger duplicate is detected" (Contract.Projection.store store duplicatePayment)

    let private testFragmentExpansionAndUnsupportedFields () =
        let fragmentJson = """{"schema":"agentlang.business.fixture","version":1,"customers":[{"kind":"  premium  "},{"kind":"regular"}]}"""
        let parsed = Contract.Fragment.parseCustomerFragments fragmentJson |> ok "parse sparse customer fragment"
        let expanded = Contract.Fragment.expand parsed |> ok "expand sparse customer fragment"
        let repeated = Contract.Fragment.parseCustomerFragments fragmentJson |> ok "reparse same fragment" |> Contract.Fragment.expand |> ok "repeat fragment expansion"
        equal expanded.Document repeated.Document "fragment defaults are deterministic"
        equal parsed.SourceSha256 expanded.Provenance.SourceSha256 "fragment source hash retained"
        equal 8 expanded.Provenance.DefaultsApplied.Length "every default has explicit provenance"
        equal "premium" expanded.Document.Customers[0].Kind "fragment text matches constructor trimming"
        equal "customer-1@example.test" (expanded.Document.Customers[0].Email |> Email.value) "fixture email default is deterministic"
        check (CustomerId.toString expanded.Document.Customers[0].Id <> CustomerId.toString expanded.Document.Customers[1].Id) "default IDs differ by fragment ordinal"
        check (expanded.Provenance.DefaultsApplied |> List.exists (fun item -> item.Path = "/customers/0/email")) "email default provenance names the field"
        errorCode "UNKNOWN_FIELD" "unsupported status is never inferred" (Contract.Fragment.parseCustomerFragments (replaceOnce "\"kind\":\"regular\"" "\"kind\":\"regular\",\"status\":\"active\"" fragmentJson))
        errorCode "UNKNOWN_FIELD" "unsupported active field is never inferred" (Contract.Fragment.parseCustomerFragments (replaceOnce "\"kind\":\"regular\"" "\"kind\":\"regular\",\"active\":true" fragmentJson))

    [<EntryPoint>]
    let main _ =
        group "deterministic baseline and canonical hash" testDeterministicBaselineAndCanonicalHash
        group "strict versioned document parsing" testStrictDocumentParsing
        group "identifiers, email policy, and normalization" testIdentifiersEmailAndCanonicalNormalization
        group "exact money, time, and quantity representations" testExactMoneyTimeAndQuantityRepresentations
        group "complete wire round-trip" testCompleteWireRoundTrip
        group "immutable Store projection and identity ledger" testDomainStoreProjectionAndLedger
        group "fragment expansion and unsupported fields" testFragmentExpansionAndUnsupportedFields
        printfn "PASS %d groups, %d assertions" groups assertions
        0
