# AgentLang small-business fixture plan

Status: full business fixture design, with its trusted value prerequisites now implemented. No full language-domain implementation or behavioral equivalence is claimed by this document. The conventional reference is documented in [BUSINESS.md](BUSINESS.md). Closed containers, static callbacks/cases, record fields and their persistence have integrated acceptance evidence. Typed fold is implemented and validated in [milestone 055](../reports/055-early-evaluation-preparation.md). The nine trusted value helpers below are implemented; their focused and integrated verification is recorded in milestone 073.

## Recommendation

The first implementation slice is in
[`business-values.agent`](../examples/business-values.agent) and
[`business-store.agent`](../examples/business-store.agent). Both use Flow/1
syntax. The foundation acceptance runner joins them in that order and stages
one atomic document. The schematic Stack excerpts below describe the intended
schema; the Flow files are the executable representation. Full transitions,
deterministic seed and matched benchmark adapters are still pending.

Represent the same immutable business state in AgentLang as records containing typed lists. Use the existing closed higher-order `list.fold` construct for lookup, replacement, and invoice totals. Its callback is a statically named word with signature `Accumulator T -> Accumulator`; the fold itself has signature `List<T> Accumulator -> Accumulator`, visits elements from left to right, and returns the initial accumulator for an empty list. A callback cannot capture caller locals, so the accumulator record must carry search keys, the original store, and any values needed during the fold. This keeps the language's no-closure rule visible and avoids a business-specific host escape hatch.

Keep business state transitions pure. The current runtime provides a virtual filesystem and fixed clock, but no injectable database, payment, or email providers. Model payment authorization and email delivery as explicit `Result` inputs to pure transition words: the same deterministic fake used by the F# oracle supplies an `Ok` receipt or an `Error`, and the AgentLang word either returns a new Store or a structured error. Do not label this as a real network effect. A later provider API can replace the input boundary only after host isolation and capability behavior are designed and tested.

## Current language surface

Current Core represents nested `List<T>`, `Option<T>`, and `Result<T,E>` values with their closed types retained at runtime. The source supports explicit constructors such as `list.empty<Customer>`, `option.none<Customer>`, `result.ok<Invoice, BusinessError>`, and `result.error<Invoice, BusinessError>`. `match-option` and `match-result` require both cases and isolate payload locals. `list.map <word>`, `list.filter <word>`, and `list.each <word>` invoke static one-argument callbacks and include callback effects/dependencies in checking. Runtime supports closed collection types in record fields, with persistence/reload covered by existing container and storage acceptance. This does not establish equivalence of the proposed full Store fixture.

The remaining fixture work includes:

- Build and verify domain lookup, total and replacement words using the implemented fold. Fold availability does not establish parity of the full Store fixture.
- Record declarations currently provide nominal fields, while F# Store uses keyed maps and payment/email providers are callable interfaces. AgentLang should deliberately use lists and pure outcome data instead of simulating a hidden map or invoking .NET.

The current `Compiler.primitives` list contains 58 trusted words, versus 49 before this value-prerequisite milestone. Container constructors and branch/mapping forms are separately described syntax, not newly registered dictionary words. `list.fold` is likewise typed syntax with a closed static callback. The nine helpers below keep the dictionary inside the PRD's 50–100 range.

## Minimal trusted additions

| Addition | Proposed signature | Purpose and boundary |
| --- | --- | --- |
| `string.guid-canonical?` | `String -> Bool` | Validate canonical lower-case GUID `D` storage text for nominal ID constructors. |
| `string.guid-normalize` | `String -> Result<String, String>` | Accept the reference's valid `Guid.TryParse` input forms and return lower-case `D` storage text before nominal construction. Do not narrow the accepted fixture inputs to claim parity. |
| `string.email-address-valid?` | `String -> Bool` | Apply the exact documented ASCII address policy from `BUSINESS.md`; keep it pure and bounded to the existing 254-character limit. The current demo's looser `email.valid?` word is not equivalent. |
| `int.add-checked` | `Int Int -> Result<Int, String>` | Surface Int64 overflow as a value so a domain word can return the same `MONEY_OVERFLOW` code instead of leaking a runtime diagnostic. |
| `int.multiply-checked` | `Int Int -> Result<Int, String>` | Checked line-price times quantity with no Float conversion. |
| `instant.parse-utc` | `String -> Result<String, String>` | Match the reference's explicit-zone ISO grammar and invariant parsing, then normalize to UTC `O` form, matching `DateTimeOffset.ToUniversalTime`. |
| `instant.is-canonical-utc?` | `String -> Bool` | Validate the exact normalized representation accepted by the `Instant` wrapper. |
| `instant.before?` | `String String -> Bool` | Compare the normalized UTC instants, including equality boundaries used by cancellation and renewal tasks. |
| `instant.add-days` | `String Int -> Result<String, String>` | Support deterministic expiry windows and reminder tasks with checked date-range errors. |

These nine helpers are implemented host API. Host-level null inputs are rejected by the pure helper functions; this does not add null to language values. Focused tests cover malformed inputs, boundaries, overflow, equality, offsets and UTC normalization, plus typed-IR execution and reference conformance. Preserve the immutable reference contracts and normalize valid alternate input forms at the explicit constructor boundary. The foundation files now compose these helpers; the full business transitions and benchmark fixture remain unfinished.

Low-level errors are `INVALID_GUID`, `INT_OVERFLOW`, `INVALID_INSTANT` and
`INSTANT_RANGE`. `instant.before?` requires two canonical UTC strings and raises
structured `RUNTIME_INVALID_INSTANT` for invalid operands; `instant.add-days`
requires a canonical UTC string and returns typed errors. Call `instant.parse-utc`
explicitly before constructing Instant values. Existing `add`/`multiply`
overflow diagnostics remain unchanged; these additions do not redefine them.

`int.add-checked` and `int.multiply-checked` return a typed Result with an error code string. User words can map that low-level error to `BusinessError` using `match-result`; the primitives contain no billing rule. Generic Result support already exists. Avoid a primitive such as `invoice.total`, `customer.premium?`, `subscription.renewable?`, or a general .NET/regex invocation.

## Proposed matching types

Use `String` wrappers for opaque IDs and Email, `Int` for signed minor-unit Money, and a validated ISO instant wrapper. An `Instant` validator should accept exactly the strings emitted by `instant.parse-utc`; callers normalize before constructing values. Money remains signed for intermediate arithmetic, while product and payment words reject negative or zero amounts according to `BUSINESS.md`.

```text
word business.id-valid? : String -> Bool
    effects none
    string.guid-canonical?
end

word business.email-valid? : String -> Bool
    effects none
    string.email-address-valid?
end

word business.instant-valid? : String -> Bool
    effects none
    instant.is-canonical-utc?
end

type CustomerId : String
    validate business.id-valid?
end

type SubscriptionId : String
    validate business.id-valid?
end

type InvoiceId : String
    validate business.id-valid?
end

type ProductId : String
    validate business.id-valid?
end

type PaymentId : String
    validate business.id-valid?
end

type Email : String
    validate business.email-valid?
end

type Money : Int
end

type Instant : String
    validate business.instant-valid?
end

word subscription.status-valid? : String -> Bool
    effects none
    let status
    $status "active" equals
    $status "cancelled" equals
    bool.or
end

type SubscriptionStatus : String
    validate subscription.status-valid?
end

word invoice.status-valid? : String -> Bool
    effects none
    let status
    $status "open" equals
    $status "paid" equals
    bool.or
end

type InvoiceStatus : String
    validate invoice.status-valid?
end

record BusinessError
    field code String
    field message String
end
```

`instant.is-canonical-utc?` and the other tabled operations are implemented primitives. Use the predicate for the generated `Instant.new` validator and normalize alternate input forms through `instant.parse-utc` before construction. `DateTimeOffset.ToUniversalTime().ToString("O")` emits a round-trip string with a UTC `+00:00` offset; normalization and validation agree on that exact representation.

Use nominal status wrappers because the conventional implementation uses discriminated unions. Their validators accept only the corresponding literal set; expose small constants or constructors only after the status type has been validated. Keep task fields raw where policy is not yet defined: `Customer.kind` is `String`, and `Subscription.term` is `String`. In particular, do not add premium or annual behavior to those types.

```text
record Customer
    field id CustomerId
    field email Email
    field kind String
    field balance Money
    field created-at Instant
end

record Product
    field id ProductId
    field name String
    field unit-price Money
end

record Subscription
    field id SubscriptionId
    field customer-id CustomerId
    field product-id ProductId
    field term String
    field started-at Instant
    field expires-at Instant
    field status SubscriptionStatus
    field cancelled-at Option<Instant>
end

record InvoiceLine
    field product-id ProductId
    field description String
    field quantity Int
    field unit-price Money
    field line-total Money
end

record CartLine
    field product-id ProductId
    field quantity Int
end

record Invoice
    field id InvoiceId
    field customer-id CustomerId
    field lines List<InvoiceLine>
    field total Money
    field created-at Instant
    field status InvoiceStatus
end

record PaymentReceipt
    field reference String
    field amount Money
end

record Payment
    field id PaymentId
    field invoice-id InvoiceId
    field amount Money
    field provider-reference String
    field paid-at Instant
end

record EmailMessage
    field to Email
    field subject String
    field body String
end

record Store
    field customers List<Customer>
    field products List<Product>
    field subscriptions List<Subscription>
    field invoices List<Invoice>
    field payments List<Payment>
    field email-outbox List<EmailMessage>
    field sent-emails List<EmailMessage>
end
```

The conventional F# fixture has more ID/value types than its roughly ten named entities. Do not count generated record constructors/accessors as trusted primitives or as separately authored domain algorithms. Count the 40–60 target as documented, user-authored reusable words; publish a deterministic word inventory before benchmarking.

## Word groups and state boundaries

Keep each public operation typed and return `Result<..., BusinessError>` for domain rejection. `BusinessError` should be a small record with stable `code` and `message` fields. The F# oracle already exposes stable error codes; compare those codes and resulting state, and document if detailed error payloads differ.

Suggested foundation vocabulary (grouped words, not a claim that all words already exist):

- Value policy: `money.add`, `money.multiply-by-quantity`, `money.nonnegative?`, `instant.normalize`, and status/Email constructors. Checked integer primitives back money calculations; no Float word participates in billing.
- Store: `store.empty`, `store.customer`, `store.product`, `store.invoice`, `store.add-customer`, `store.add-product`, and typed add/find/update-step callbacks. Store collections are immutable lists.
- Subscription: `subscription.start`, `subscription.cancel`, and lookup/update words. These enforce foreign-key existence, duplicate IDs, nonblank term, expiry after start, active-to-cancelled transition, and cancellation time not before start. They do not decide renewal eligibility.
- Invoice: `invoice.create`, `invoice.line`, `invoice.total`, and supporting fold callbacks. Input cart is `List<CartLine>`; creation rejects an empty cart, unknown product, nonpositive quantity, negative price, and any checked overflow. Product descriptions and prices are copied into lines.
- Payment: `payment.apply-result` takes an explicit `Result<PaymentReceipt, BusinessError>` alongside the pure Store/request data. An Error leaves the Store unchanged; an Ok receipt must match a positive full invoice balance before recording Payment and setting status Paid.
- Email: `email.queue` appends a validated message. `email.apply-delivery-result` takes an explicit `Result<Unit, BusinessError>`; error retains the head of the outbox, success moves it to sent messages. No SMTP or network word is present.

For lookup, use a typed fold state containing the target ID and `Option<Entity>`. For an update, the state also carries the replacement entity and the accumulating output list. For an invoice total, the accumulator carries the partial Money result; `match-result` preserves an earlier error without executing unchecked addition. This is verbose but visible and inspectable. It also makes the inability of map/filter callbacks to capture `Store`, ID, or partial total explicit.

Effect declarations for these foundation words are `none`; they transform passed immutable values only. Test fixtures should construct the payment/email provider result value in each case. If later work adds real providers, it needs an explicit Engine provider interface, per-test fresh mock state, effect preflight before callback invocation, and no capability grant inherited from test mode. The existing `--allow` capability set alone does not implement a pluggable provider.

## Fidelity issues to resolve before calling the fixtures equivalent

| Conventional contract | Current AgentLang gap or difference | Required decision/evidence |
| --- | --- | --- |
| Opaque IDs use `Guid.TryParse` and Guid equality | Normalization and canonical validation primitives exist; domain construction must wire both together | Normalize accepted input forms before nominal construction; generated `.new` must reject noncanonical text. Test uppercase, braces, `N`, `B`, `P`, malformed text, and equality. |
| Email uses the exact bounded ASCII regex in `BUSINESS.md` | Existing demo `email.valid?` only checks a few string shapes and admits extra cases | Use one trusted, documented validator with the same accepted/rejected corpus, or revise both contracts and rerun the F# oracle. |
| Money is signed Int64 minor units; add/multiply are checked Results | Checked integer Result primitives exist; domain wrappers must retain nominal identity and map errors | Map checked operation errors into `BusinessError`; test Int64 edges without Float. |
| Invoice quantity is F# `int` (Int32) | AgentLang `Int` is Int64 | Prefer making F# quantity Int64, or add a documented Int32 range check to AgentLang. Test the boundary on both sides. |
| F# timestamps accept DateTimeOffset and normalize to UTC | AgentLang stores only primitive String/Int/Float; `clock.now` returns a configured string | Normalize and compare exact instants with pure date helpers and pin the input format. Require canonical UTC `+00:00` strings inside `Instant` so alternate offset strings cannot compare unequal after F# normalization. Test offsets, equality, malformed input, leap day, and date-range overflow. |
| F# status types are discriminated unions | AgentLang has nominal scalar wrappers, not user-defined sum types | Use validated status string wrappers and test that constructors reject every other literal. Treat representation as an explicit limitation. |
| F# errors are a discriminated union with a `code` projection | AgentLang has no declared union type | Compare stable codes via `BusinessError` and record any detail mismatch; do not call that full error-structure parity. |
| F# Store uses Map; transition methods call injected providers | AgentLang supports List but not Map/closures, and Engine exposes only virtual file/clock providers | Use list folds and explicit provider outcome values for pure business-state comparison. Keep actual provider-call semantics out of the claimed parity surface. |
| F# can construct nullable strings at its public boundary | AgentLang string literals cannot encode null | Test blank/malformed values as language inputs and report null-only F# cases separately rather than inventing a language null. |

## Source layout and acceptance

Add one readable fixture such as `examples/business.agent`, split into separate files only if the loader's existing persistence model supports that without making agent discovery harder. Keep types and initial seed data reusable, but keep benchmark tasks and hidden acceptance checks outside the starting dictionary. The seed should use fixed IDs and normalized timestamps; it must be a pure `business.seed : Unit -> Store` word so Flat and Growing runs begin identically.

Reuse the validated `list.fold` implementation and its Core tests: empty/nonempty order, wrong callback signatures, callback dependency/caller graph, effect preflight even for empty input, instruction limits, type joins, and project reload. Add domain tests for accumulators that carry an ID and partial Result; do not test only numeric sum.

The business-language acceptance should then verify:

1. Every entity/value signature and generated field accessor is nominal: `Email` is not `String`, Money is not Int, and different ID wrappers cannot cross.
2. Valid/invalid GUID and Email corpora match the reference. Money tests use exact integer minor units, signed intermediate values, negative-charge rejection, and Int64 overflow. No Float reaches an invoice path.
3. Store lookup/add/replacement, subscription start/cancel, invoice construction/total, payment result application, and outbox delivery match F# error codes and final states. Failed transitions return an Error without changing the input Store.
4. The callback graph contains fold-step words as dependencies, callers, and inherited effects. Capability denial happens before callback invocation. Empty folds return their seed.
5. Every reusable library word has attached tests for every `if`, option/result arm, and fold empty/nonempty path it owns. Target approximately 50–100 language tests; keep independent conventional oracles hidden from the agent.
6. `test-all` passes, then commit the fixture words. Start a fresh Engine from the project directory and verify types, words, metadata, tests, examples, and `business.seed` reload identically. The seed must not depend on process randomness or a real provider.
7. Only after the F# and language oracle corpora agree should the domain be described as equivalent. Publish observed mismatches and provider/data-boundary limits in a milestone report; do not infer benchmark gains from these conformance checks.

The intended initial vocabulary remains 40–60 authored business words and 50–100 source tests, with trusted primitives kept within the PRD range. Recount after implementation, because records generate accessors automatically and coverage rules can require more test cases than the current seven-group F# smoke suite.
