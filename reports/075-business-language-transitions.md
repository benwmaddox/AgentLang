# Pure business transitions and reproducible seed

2026-10-06. This checkpoint extends the strong business foundation with
subscription lifecycle, invoice construction, payment outcome handling, FIFO
email delivery, and a deterministic baseline. All operations use Flow source
and the existing typed semantic IR; there are no new host primitives.

The implementation is split into
[state reconstruction and seed](../examples/business-state.agent),
[subscriptions](../examples/business-subscriptions.agent),
[invoices](../examples/business-invoices.agent), and
[payments and email](../examples/business-payments-email.agent).
It composes with the two foundation documents in one staged project.

Store-changing operations return `Result<Store, BusinessError>`. Typed lookups
expose the resulting entity; the F# reference often returns a Store/entity pair.
Acceptance compares the observable state and stable error codes across that
representation difference. Payment and email outcomes are explicit Result data,
not calls to real providers or declared network effects.

Invoice creation validates lines before calculating the total, preserving the
reference's error precedence. Quantity must be positive and fit Int32; prices
and totals use exact checked Int64 minor units. Cancellation permits equality
with the subscription start, replaces only the target, and preserves list order.
Payment success records a payment and marks its invoice paid together. Delivery
moves only the first pending email to the end of the sent list.

`business.seed` matches `Contract.Factory.baseline` with fixed IDs, UTC instants,
one regular customer, one product and one active monthly subscription. It has
no invoice, payment, email, premium-discount or renewal solution.

## Verification

The independent
[transition suite](../tests/AgentLang.Business.Transitions.Tests/Program.fs)
uses the existing F# reference and strict fixture-contract projection. The
focused Release run passes seven groups and 3,501 assertions: all 151 attached
tests and 44 examples pass, and all 53 words and 31 types publish and reload.
It checks exact seed data, nominal/Float rejection, transition errors and their
precedence, full state after chained billing/payment/email operations, FIFO
failure/retry, dependency/caller metadata, and current own-site library coverage.
Every library word has zero uncovered instructions and branch outcomes.

The focused oracle exposed and corrected harness mistakes in JSON name/status
decoding and an arithmetic overflow expectation: `199 × Int32.MaxValue` is a
valid Int64 value. The final overflow case uses a valid maximum-price product
and the F# invoice oracle. These corrections did not require runtime changes.

The fresh full Release gate passes all 35 required checks, with zero build
warnings or errors. This includes the new transition suite, all existing
acceptance runners, process verifiers, and both unstaged/staged diff checks.
The [full gate evidence](evidence/075-local-validation.json) and its supporting
process evidence record the tested working tree (`47bc693`, `dirty: true`).

```powershell
./scripts/Validate.ps1 -Configuration Release -ReportPath reports/evidence/075-local-validation.json
```

Focused command:

```powershell
dotnet run --project tests/AgentLang.Business.Transitions.Tests/AgentLang.Business.Transitions.Tests.fsproj --configuration Release -- --evidence reports/evidence/075-focused-business-transitions.json
```

The [focused evidence](evidence/075-focused-business-transitions.json) records
the tested working tree on parent revision `47bc693`, with `dirty: true`.
The [source audit](evidence/075-transition-audit.json) hashes the six documents,
test project and gate registration and confirms no Core source changes.
Independent source review found no transition logic or state-preservation
defect; its temp-project cleanup finding was fixed with checked containment
and a `finally` block before final validation.

## Feedback and boundaries

The generated `invoice.total : Invoice -> Money` accessor already exists, so
the line-list calculation is named `invoice.sum-lines`. This preserves the
schema and the dictionary's collision rule without adding overloading.

The library gate requires empty/nonempty coverage at each fold site. A fold
behind a nonempty guard cannot exercise its empty path. Replacement and line
building are therefore separately reusable, unconditional fold words with own
empty/nonempty tests. This retains strict coverage while increasing helper
vocabulary; that cost must remain visible in the agent evaluation.

The six documents now contain 53 authored words, 151 attached tests, 44 examples
and 31 nominal types (10 scalars and 21 records). This fits the proposed
40–60-word domain size but exceeds the original test estimate. Full own-site
coverage provides a publication gate; independent behavioral oracles remain
necessary to catch an incorrect expectation or fully covered wrong behavior.

Public record constructors still permit invalid states unavailable through the
reference's private constructors. Negative product prices and blank receipt
references receive supplemental guards, tested separately from valid-reference
conformance. This does not add general record invariants or private construction.
Provider call counts, actual I/O, full error-payload representation, matched
benchmark adapters and controlled agent-cost results remain outside this slice.

This is coding-agent implementation and reference verification, not a measured
task trial. The next step is executable business-task adapters and paired fresh
subagents using the retained vocabulary. Memory and LLVM research remain deferred.

## Publication follow-up

Revision `3a7c0cc943924cac6403a35cab8992e3a60f1ab3` is published on `main`
and `prototype`. [Main CI](evidence/075-main-ci.json) completed successfully;
its [clean validation artifact](evidence/075-main-validation.json) confirms all
35 required checks passed on that exact revision with `dirty: false`.
