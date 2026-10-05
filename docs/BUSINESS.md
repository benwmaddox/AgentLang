# Conventional small-business reference fixture

This project is the ordinary F# comparison environment for the agent-language experiment. It gives the conventional mode a deterministic business backend with typed operations, immutable state transitions, and independent tests. The fixture intentionally supplies basic entities and operations only. Premium classification, discount policy, renewal eligibility, and reminder rules remain benchmark tasks for the agent to implement.

Build and run its checks with:

```powershell
dotnet build experiments/AgentLang.Business/AgentLang.Business.fsproj
dotnet run --project tests/AgentLang.Business.Tests
```

## Types and invariants

The `AgentLang.Business.Domain` module defines opaque nominal `CustomerId`, `SubscriptionId`, `InvoiceId`, `ProductId`, and `PaymentId` values. Each currently parses from a GUID string. `Email` can only be constructed through its validating function. `Money` stores signed `int64` minor units; its representation has no floating-point path. Signed money supports explicit arithmetic, while the billing operations enforce nonnegative product prices and strictly positive, full-balance payments. Addition and multiplication are checked and return `MONEY_OVERFLOW` on overflow.

The Email policy is intentionally small and deterministic: ASCII letters, digits, underscore, percent, plus, and hyphen are allowed in local segments; local segments may be separated by single dots; the domain must have at least two nonempty labels, with ASCII letters/digits and internal hyphens; whitespace, repeated dots, multiple `@` characters, and values longer than 254 characters are rejected. It is a project policy, not complete Internet mail-standard validation. Keep this policy aligned with the AgentLang Email validator when the language-side business fixture is added.

Entities are immutable: `Customer`, `Product`, `Subscription`, `Invoice`, `Payment`, and `EmailMessage`. A customer retains validated Email, a raw nonempty `Kind` string, and a signed Money `Balance`. A subscription retains a raw nonempty `Term` string, start and expiry instants, and lifecycle status. Values such as `premium` and `annual` are input attributes only: the fixture does not classify customers, compute discounts, decide renewal eligibility, or send reminders. These raw fields give later benchmark tasks data to work from without baking their answers into the starting environment.

`Store` holds immutable maps and FIFO email lists. Every accepted operation returns a new store; a failed operation returns a structured `DomainError` and does not change the input value. Entity constructors and accessors are grouped by nominal type under `Customer`, `Product`, `Subscription`, `Invoice`, `InvoiceLine`, `Payment`, and `EmailMessage` modules.

## Operation contracts

- Customer and product insertion reject duplicate IDs. Product creation trims its name and rejects blank names and negative prices.
- Subscription start requires an existing customer and product, a fresh ID, nonblank term text, and an expiry after the start. A new subscription is active. Cancellation is allowed once and cannot be dated before the start. The fixture treats term text as data and does not assign special meaning to `annual` or `monthly`.
- Invoice creation snapshots product names and prices into its lines, requires an existing customer, at least one line, existing products, and positive integer quantities. Each line multiplication and the invoice sum use checked `int64` minor-unit arithmetic. An invoice begins open.
- Payment accepts an injected `PaymentProvider` capability after validating a unique payment ID, existing open invoice, positive amount, and exact full invoice balance. It records the returned reference and changes the invoice to paid only when the provider returns a receipt for the exact requested amount. A decline or invalid receipt leaves the input store unchanged. The fixture contains no network/payment SDK implementation.
- Email queueing requires an existing customer and nonempty subject/body. It stores a message in the outbox. Delivery accepts an injected `EmailProvider`; a failure leaves the message queued, while success moves it to the sent list. The fixture contains no SMTP implementation.

Provider callbacks are explicit boundaries for deterministic fakes or a future host adapter. Tests use local closures and deterministic providers; they do not access a real filesystem, database, payment service, or mail server.

## Comparison discipline

This fixture is a candidate foundation for the Conventional mode in the three-way comparison: flat AgentLang, growing AgentLang, and ordinary F#. The matching AgentLang domain fixture and task exposure still need independent verification. Do not treat this code as evidence that the language and conventional environments are equivalent. Once both exist, compare exposed business rules, initial records, allowed effects, task prompts, and hidden acceptance oracles; do not give either mode the premium/renewal task answers in its starting vocabulary.

The executable tests are a visible fixture contract and smoke/oracle suite for this implementation. Benchmark acceptance tests must remain separate from agent-visible code, and eventual 60-task coverage is a separate deliverable.
