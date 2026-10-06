# Strong business vocabulary foundation

2026-10-06. The business foundation now runs in Flow source using
the existing typed semantic IR. This checkpoint covers strict values and the
immutable Store foundation; subscription, invoice, payment and email transitions,
deterministic benchmark seed and complete paired-task adapters remain unfinished.

The [values document](../examples/business-values.agent) declares distinct
CustomerId, SubscriptionId, InvoiceId, ProductId and PaymentId types, validated
Email and canonical UTC Instant, signed integer Money, and closed validated
status types. Parsing normalizes alternate GUID and timestamp forms before
strict nominal construction. Arithmetic retains integer minor units, maps
overflow into BusinessError, and bounds quantities to signed Int32. Invoice
quantity positivity is a separate rule for the later invoice transition.

The [Store document](../examples/business-store.agent) uses explicit typed
fold accumulators for five entity lookups. Lookups return the first matching
list entry. Add operations reject duplicate customer/product IDs and rebuild
all seven collections, changing only the intended list. All words are pure;
there are no new trusted primitives or host provider calls.

The documents contain 32 authored words, ten nominal scalars, sixteen records
(including five lookup accumulators), 83 attached tests and 27 examples.
Generated constructors/accessors are excluded from the authored-word count.

The [independent acceptance runner](../tests/AgentLang.Business.Language.Tests/Program.fs)
passes six groups and 1,368 assertions against the conventional F# reference,
including normalization, error codes, signed arithmetic, constructor precedence,
Store contents, duplicate rejection and nominal type errors. All 83 tests and
27 examples pass. Each of the 32 words commits at library maturity with current
own coverage and no uncovered instruction or branch outcome. All 26 types and
32 words reload in a fresh Engine, with 156 identity/source/metadata checks.
Temporary test probes are absent from that dictionary and have no durable history.

The [focused evidence](evidence/074-focused-business-language.json) records the
exact command and dirty working tree based on `3cdc7fc`. Evidence output is
opt-in; ordinary CI execution does not rewrite a tracked report. The full
required Release gate is recorded separately.

`./scripts/Validate.ps1 -Configuration Release -ReportPath
reports/evidence/074-local-validation.json` passes all 34 required checks,
including the newly required business-language suite. The fresh solution build
has zero warnings/errors. The [full evidence](evidence/074-local-validation.json)
identifies the dirty working tree based on `3cdc7fc`; clean committed-source CI
is a separate publication check. The [fixture audit](evidence/074-foundation-audit.json)
records working-file hashes and confirms no Core source or versioned IR/storage
layout changes. The fixed primitive dictionary remains at 58 words.

## Feedback and limits

Strict scalar construction enforces validity and nominal identity, but public
record constructors can bypass smart-constructor rules such as nonblank names
or nonnegative product prices. Store lists can likewise be constructed with
duplicate IDs; first-match behavior is deterministic, not a Map invariant.
These are explicit limits of this foundation, not encapsulation parity with
the reference's private records.

Some invalid-input messages are less detailed than the reference; conformance
checks use stable codes and normalized values rather than claiming identical
error text. Review also corrected qualified primitive and generated constructor
names, a duplicate Store definition, structured-JSON unwrapping, and test setup
that assumed persistent eval locals. Multi-step probes now use temporary words.
Store addition tests compare exact lists rather than counts alone.

Library coverage makes helper control flow auditable, at the cost of more
source tests and helper vocabulary. Independent reference checks remain
necessary: complete branch coverage alone cannot establish correct behavior.
This is implementation work by coding subagents, not a measured agent trial
or evidence of lower token cost. The next evaluation step needs executable
business tasks and matched acceptance adapters. LLVM and memory-model research
remain deferred behind that work.
