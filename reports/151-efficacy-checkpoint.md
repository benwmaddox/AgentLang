# 151 — Efficacy checkpoint: typed vocabulary and reliable maintenance

Status: evidence synthesis through report 150, 2026-10-09. This assesses the
prototype; it does not establish PRD completion or comparative superiority.
The subsequent sections record later evidence through report 166 without
changing the original trials or their frozen outcomes.

## What is working

External coding agents can discover functions through the dictionary, compose
them, attach tests, satisfy enforced library gates, and leave reusable project
vocabulary. The practical mechanism is demonstrated. Its reliability advantage
over a conventional strongly typed repository is not.

The most useful recent finding is that API structure matters. A checked
`BillingWindow` lets downstream calculations consume a valid value and return
an integer, instead of repeating invalid-window Result branches. In
[report 149](149-validated-window-reuse.md), both fresh agents discovered and
reused that API and passed 18/18 hidden cases. AgentLang retained its full library
gates. F# also succeeded. The API was coordinator-designed, so this does not
show that an unaided agent would invent it.

## Evidence that constrains the hypothesis

| Evidence | Observed result | What it means |
| --- | --- | --- |
| [Shared payment maintenance, 141](141-paid-invoice-maintenance.md) | All four submissions pass behavior and preserve shared structure; three rely on inherited coverage where the frozen task required new regression tests | Test adequacy and literal task compliance are different; preserve both outcomes |
| [Subscription overlap, 143](143-subscription-overlap-comparison.md) | One AgentLang submission passes its suite and full coverage but fails an empty-interval case; the other three pass | Coverage does not establish relational business correctness |
| [Nonempty interval, 144](144-nonempty-interval-prototype.md) | Existing validated records/Option express the invariant; both languages pass 125 grid cases | A useful domain abstraction can be added without expanding the trusted core |
| [Retained interval discovery, 145](145-rental-vocabulary-reuse.md) | All four agents pass 17 cases; neither retained agent uses the interval helper | Retention alone does not ensure usefulness or adoption |
| [Batch discovery, 147](147-agent-authored-batch-reuse.md) and [guidance repair, 148](148-flow-list-discovery.md) | Incorrect fold guidance contributes to two abandoned runs; corrected guidance enables a fresh passing run | Introspection is part of the product contract and must be executable; 147's participant communication also prevents an independence claim |
| [Validated-window reuse, 149](149-validated-window-reuse.md) | Both agents pass 18 cases and reuse; AgentLang 21 exchanges/two syntax errors, F# 11/zero | Compatible with better composition through validated inputs; no language reliability lead or causal cost estimate |

These tasks, protocols and denominators differ. Do not combine them into one
success percentage or treat broker exchanges as model turns/tokens. No controlled
small-context result or authoritative provider-token comparison is available.

Report 148 is especially instructive: its agent initially used the retained
Result-returning helper, then replaced it with duplicate calculation after an
outer guard left a later error branch uncovered. Report 149 shows a type/API
shape that avoids that redundant error state while preserving the gates. This
supports improving the abstraction before adding exceptions to coverage rules;
it does not prove the agent's private reasoning or a causal effect.

[Report 150](150-shared-rule-maintenance.md) then tests a later policy change on
the resulting projects. Both fresh agents pass all 48 hidden cases across the
shared helper and its two consumers, preserve the delegation paths, and finish
normally. AgentLang retains all 47 prior test names, adds 12, and preserves full
library qualification. F# adds 11 checks. This demonstrates one bounded successful
shared-maintenance edit in each
environment, without establishing a language advantage.
The earlier and later success is a useful sequence observation, not a controlled
estimate of retention benefit.

## What to keep, and what not to assume

Keep the inspectable dictionary, explicit effects, strong nominal/validated
types, exhaustive matches, tests attached to functions, and checked library
dependency closure. Those are concrete development mechanisms with useful local
checks. Validated records enforce predicates at construction; they are not
compile-time proofs of arbitrary domain logic. A wrong predicate can still
encode the wrong policy.

Keep independent acceptance and domain-boundary tests alongside coverage.
[Report 142](142-library-dependency-qualification.md) shows enforceable dependency
qualification; report 143 shows that qualified code can still be wrong. Neither
100% branches nor one successful mutation test means every field relationship
was tested. Library status should mean the declared checks passed, not that the
implementation is proven correct.

The current examples also expose composition cost: named static callbacks may
require an accumulator record where F# uses a closure. That tradeoff may simplify
runtime semantics, but it should be measured rather than advertised as naturally
simpler for an agent. Retained helpers need signatures that actually fit later
work; a large dictionary alone is not accumulated understanding.

## Native runtime is a separate result

[Report 140](140-matched-mailbox-load.md) demonstrates bounded native mailbox
execution and lower process memory on its measured fixture. The high native
short-run throughput point fails longer confirmation, and KEEP throughput is
sensitive to available arena slots. This supports continued engineering, not a
general native throughput advantage or a final KEEP/RETURN choice.

There is no need to decide on a full actor framework to preserve this work.
Typed state/message boundaries and specialized mailboxes remain compatible with
a dataflow-oriented application model. Scheduling, retained state, I/O lifetime,
and arena reuse still need explicit contracts. The interpreter/native/JIT/AOT
product target is not complete; JIT and general native release support remain
future work.

## Recommended next work

Close this billing sequence with report 150. Stop adding similar easy tasks
merely to collect more successful runs; both recent pairs reached equal behavior
acceptance, and report 150 exposed no new unresolved final defect.

Use the demonstrated empty-occupancy defect to improve the actual domain API:
represent absence of occupancy separately from a validated nonempty period,
and route overlap decisions through it using the existing core. Validate with
independent occupancy expectations and retain coverage/dependency gates. This
is a product correction motivated by an observed failure, not another claim of
language superiority.

Continue native conformance and memory work as a secondary track, preserving
both arena policies and avoiding an actor-framework expansion. The next efficacy
comparison should accompany a materially different real maintenance problem,
with collateral-regression checks, rather than another arithmetic variant.

The [independent synthesis notes](evidence/151-efficacy-checkpoint/review-notes.md)
and [final review](evidence/151-efficacy-checkpoint/final-review.md) are preserved.
The final review's three wording/link corrections are incorporated here; execution
evidence remains in the individual trial reports and their verified archives.

## Subsequent I/O maintenance evidence

[Report 156](156-state-sensitive-io-maintenance.md) adds a materially different
state-sensitive I/O task with collateral checks. Both fresh participants pass
nine independent cases. AgentLang publishes a function meeting all own-body
library coverage requirements, but neither participant reuses the existing small
publisher helper. AgentLang uses 37 broker exchanges versus 19 for F#; F#'s two
validation attempts are environmentally blocked and coordinator validation is
reported separately. This supports feasibility without changing the checkpoint's
conclusion: comparative reliability and accumulated-vocabulary benefits remain
unproven. Fix offline validation and enum/test-constructor discovery friction
before another comparison; do not expand the deferred retention study merely
to collect more easy successes.

[Report 159](159-retained-io-vocabulary-follow-on.md) follows the retained safe
publisher with mirroring, after the validation/transport fixes. Both fresh
participants pass twelve independent scenarios. F# calls the helper; AgentLang
discovers, reads and tests it but duplicates the logic. Its new library passes
all own-body and finite-return gates. This is a negative reuse observation:
discoverability and coverage do not guarantee composition. AgentLang uses 23
broker exchanges versus 10 for F# in this pair, with no transport or audit-feed
failure. Four frozen control-output reports were replaced with compact copies
after dispatch; unchanged code pins and passing reruns remain, but full original
evidence provenance cannot be claimed. Close this I/O microsequence and continue
the separate native conformance track instead of collecting similar successes.

## Subsequent transition reuse and signature maintenance

[Report 163](163-atomic-subscription-handoff.md) tests a different domain contract:
an atomic handoff composed from retained cancellation and creation operations.
Both fresh agents reuse those operations and pass all 33 independent cases.
This is positive voluntary-reuse evidence in both environments; it does not
erase the negative language-side reuse finding from report 159.

[Report 166](166-handoff-signature-maintenance-agent-pair.md) then changes those
actual saved implementations to add dry-run behavior and updates two disclosed
coordinator-created callers. Both fresh agents pass 66 target and 66 caller
checks. The language agent preserves stable identities and library qualification
and passes all 149 attached tests after reload. However, it changes a prior
multi-error input and drops explicit pre-state assertions from a prior success
test. F# preserves its prior assertions. Independent behavioral acceptance and
source-test preservation therefore yield different conclusions for the language
submission. Coverage did not prevent the loss of earlier evidence.

The engineering mechanism supports coordinated signature changes while checking
the complete proposed dictionary before candidate activation; see
[report 165](165-atomic-function-replacement-staging.md). Its full 37-check local
gate passes. That mechanism and the maintenance pair establish feasibility,
not comparative reliability. No fixed token context limit or authoritative LLM
token comparison was used, and concurrent validation may confound latency.

[Report 167](167-test-source-inspection.md) implements current test-body
inspection, including the enclosing setup for test-file dependency replacements.
This addresses observed discovery friction without rewriting the participants'
saved outcomes. A fresh experiment is required before attributing any improved
agent behavior to it.

[Report 168](168-test-source-inspection-agent-probe.md) completes one fresh
language follow-up. The agent discovers test-source inspection before editing,
passes all 132 independent checks and 150 attached tests, and retains the
competing-error scenario. It still removes two prior success-test pre-state
assertions. Discovery is feasible; complete preservation and a comparative
reliability advantage remain unproven. Close this bounded follow-up and continue
native conformance as a separate engineering track.
