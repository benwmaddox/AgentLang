# 134 — R09-inspired vocabulary discovery study

Status: agent-created seed functionally accepted; comparison pending. This is an exploratory
agent-reliability study, not a native performance result. Starting revision:
`6aa035a`.

Report 125 observed correct edits and vocabulary reuse in all three conditions,
without establishing a reliability advantage over conventional F#. The next
comparison asks agents to consolidate two initially correct but duplicated
business summaries around a shared customer payment-total operation. It measures
discovery, reuse, preservation and correct shared structure rather than repair
of an initially incorrect output.

Before that comparison, a separate fresh agent must create the reusable helper.
Its actual accepted output will become retained vocabulary. A coordinator-written
control cannot substitute for that agent-created seed. The seed participant
receives the public contract and runtime discovery tools, but not the hidden
oracle, control implementation or subsequent refactoring task.

## Checks before the seed trial

The inherited language project has 53 authored functions, 31 types and 154
passing tests after fresh reload. The foundation includes 51 library functions
and two existing project functions; this study does not weaken their gates or
promote them merely to simplify the experiment.

Thirteen independently enumerated cases cover customer ownership, missing
customers, no payments, unpaid invoices, exact Int64 limits, customer-local
overflow, dashboard aggregate overflow and preservation of an earlier failure.
The F# reference adapter constructs valid states through the existing business
domain API. Multiple payments for one customer use separate fully paid invoices.
This is R09-inspired work, not a claim to have completed the task bank's original
R09 contract or its invalid partial-payment example.

The seed scorer reconstructs and checks all thirteen complete typed Store
values before invoking the helper. Money amounts remain decimal strings through
the adapters. The only date normalization changes the UTC suffix between `Z`
and `+00:00`. Candidate tests, library maturity and independent behavior are
reported separately; seed acceptance requires all of them plus inherited
definition preservation and explicit task finalization.

| Scripted control | Independent direct cases | Its own tests | Outcome |
| --- | ---: | ---: | --- |
| Correct library helper | 13/13 | 163/163 | Pass |
| Missing helper, unchanged foundation | No direct calls | 154/154 | Rejected |
| Wrong customer ownership | 5/13 | 156/156 | Rejected |
| Unchecked arithmetic | 11/13 | 157/157 | Rejected |

Both faulty helpers pass their own attached tests. The independent cases detect
the wrong-owner totals and the unchecked helper's runtime overflow instead of
the required `MONEY_OVERFLOW` result. These are scorer controls, not agent trials
or evidence of an efficacy advantage. The mutants have project maturity and do
not establish a way around library qualification.

Review found that the draft scorer recorded process outcomes without gating
them and could ignore failed nonbehavior checks. Before any participant dispatch,
the scorer was hardened to require successful process exit, bounded output
draining, exact request/response counts, no parse errors and no failed checks.
The control results above were reproduced with the hardened scorer. Earlier
failed and superseded scoring attempts remain available in local evidence.

The F# reference adapter's first two failed build logs were overwritten during
preparation. Its final successful build and fixture output are retained, along
with a disclosure of that provenance gap; this report does not claim a complete
raw build-attempt history.

## Remaining acceptance

The seed participant has finished. Fresh-copy scoring passes all thirteen hidden
direct cases and all thirteen typed Store round-trips. Its complete attached
suite passes 163/163 tests; the public helper is persistent, pure and
library-qualified. The trace records 56 broker exchanges, a successful
`task.commit`, `host.close`, and host/runtime exit zero. The first commit of its
supporting function was rejected with `COMMIT_TEST_REQUIRED`; the participant
added tests and recovered without coordinator solution guidance. Scorer inputs
remained unchanged. Both new functions are persistent, pure library functions.
All 295 baseline files remain, all 284 inherited content-addressed objects and
six fixtures are unchanged, and all 3,941 original dictionary lines remain in
order. The changed dictionary and current-manifest pointer incorporate the new
definitions. Source review found generic customer lookup, invoice ownership,
checked addition and sticky errors, with no fixture-specific branching.

The seed is accepted for functional reuse. Exact prompt adherence is not claimed:
in addition to the allowlist omission described below, the agent used accessor
calls such as `state.outcome()` instead of the requested plain-property style.
The agent recovered from invalid constructor/Unit syntax, two rejected definition
attempts and the missing-test commit refusal. This demonstrates a successful
uncoached tested abstraction in this environment, not comparative reliability
superiority or proof that every future customer-total case is correct.

The seed prompt, foundation, runtime, broker, oracle, reference fixtures and
scorer were frozen before dispatch. The isolated starting copy matches all 295
foundation files; all 22 runtime files and 25 pinned runtime-source hashes were
verified. One fresh Luna/max participant was dispatched without inherited
conversation. The immutable freeze retains its predispatch status; a separate
[dispatch record](../experiments/AgentLang.SubagentTrials/r09-discovery-001/seed-dispatch/dispatch.json)
records the actor and prompt/freeze hashes. Dispatch is not evidence of successful
execution or acceptance.

The coordinator disclosed one dispatch deviation: manual transcription omitted
the read-only `transitive-callers` operation from the participant's broker
allowlist, although the frozen prompt includes it. The participant continues
without coaching or restart. This run must not be described as exact frozen-prompt
execution; actual prompt/allowlist evidence and the deviation will accompany its
outcome. The seed's behavior and library qualification remain independently
testable. Subsequent comparison dispatch must use exact prompt contents rather
than manual transcription.

Next, finalize the three matched refactoring starts: retained vocabulary, the same
foundation without the seed, and conventional F# with an equivalent helper.
Independent duplicated-summary scaffolds are being prepared on isolated copies
and the accepted seed is now available for integration. The conventional
copy needs two pure collection accessors while preserving its private Store and
nominal types. Hidden acceptance fixtures remain outside actor-visible projects.
The main comparison accepts any correctly shared helper name and reports reuse
of the retained seed's actual identity separately. No main comparison participant
has run, and comparative reliability remains unproven.

The detailed design is in
[the study plan](../experiments/AgentLang.SubagentTrials/r09-discovery-001/plan.md).
Local working evidence is under `.agentlang/r09-discovery-001/`; an immutable
seed milestone [archive](evidence/134-r09-discovery-seed/134-r09-discovery-seed.zip)
contains 7,316 payload files plus its manifest. All entries were verified after
compression, and all 351 checked frozen-input hashes matched before and after.
It includes the accepted project, trace, actual/frozen prompts, controls, failed
attempts and reproducible source snapshots; compiled binaries and active comparison
preparation are excluded. The two missing early reference-build logs are disclosed.
See the [storage index](evidence/134-r09-discovery-seed/storage-index.json).
Archive SHA-256: `7129e1adf07340fd312db802df75d5c2f086c2d6b38ac6ae46940243bee75283`.
