# 179 — Preview repair and typed result migration

2026-10-10. Completed six participant sequences, with two stages each.

The question is whether agents can repair a tested defect and then change a
typed API while preserving unrelated behavior and assertions. All six
sequences succeed in all three conditions. They establish no
comparative reliability advantage for AgentLang.

## Design

Two fresh external GPT-6-Luna/max participants per condition complete two stages
on the same project and conversation, with 100 broker exchanges per stage.
The fixed order is retained-1, reset-1, F#-1, F#-2, reset-2, retained-2.
Independent scores and preservation feedback are withheld between stages.
See the [frozen protocol](../experiments/AgentLang.SubagentTrials/preview-sequence-179/README.md).

Stage one removes a zero-duration preview shortcut that incorrectly returns
success before validation. Cancellation must precede replacement creation;
successful preview returns the complete unchanged input Store. Only the
deliberately wrong zero-duration expectation may change.

Stage two returns a typed `ReplacementPreview` with exactly `original` and
`proposed` Store fields (`Original`/`Proposed` in F#). Original equals the input;
proposed is the validated cancellation/replacement state. Existing success
assertions may select original; existing errors and unrelated evidence must
remain intact. A new complete proposed-state assertion is required.

Retained/reset compares entire checkpoint-171 project packages, including
code, tests, documentation and history. Reset is a rich domain environment,
not primitives alone. F# is a workflow reference with unequal provenance and
suites, using the checkpoint-178 preparation derived from 166. This is not a
matched language-only comparison.

## Results

Each projection uses the same 37 frozen cases: 30 valid-reference cases and
seven orphan-reference cases. Thirty-three are historical fixtures; four add
zero-duration precedence combinations. The oracle compares complete input and
result state plus error codes. Exact error messages require source review.

| Participant | Repair | Migration: original | Migration: proposed | Broker exchanges |
| --- | ---: | ---: | ---: | ---: |
| retained-1 | 37/37 | 37/37 | 37/37 | 22 + 30 |
| reset-1 | 37/37 | 37/37 | 37/37 | 19 + 32 |
| F#-1 | 37/37 | 37/37 | 37/37 | 14 + 9 |
| F#-2 | 37/37 | 37/37 | 37/37 | 18 + 9 |
| reset-2 | 37/37 | 37/37 | 37/37 | 17 + 15 |
| retained-2 | 37/37 | 37/37 | 37/37 | 20 + 18 |

All saved results have zero setup failures. Preservation and finalization are
separate endpoints, checked through manifest/file inventories, source diffs,
test adaptations, task commits where applicable, and terminal broker records.
All participants preserve unrelated definitions and inherited tests. All eight
AgentLang stages publish with library maturity preserved and commit their tasks;
all four F# stages finish local validation with exit 0. All 12 brokers close
normally with host/runtime exit 0.

| Condition (two participants, four stages) | Exchanges | Request payload bytes | Response payload bytes | Broker seconds |
| --- | ---: | ---: | ---: | ---: |
| Retained | 90 | 20,072 | 345,638 | 870.0 |
| Reset-rich | 83 | 25,882 | 310,522 | 1,149.9 |
| F# | 50 | 16,778 | 264,718 | 1,153.7 |

F# uses fewer exchanges here; retained has the shortest summed broker duration.
These interfaces have different operation granularity, and durations include
participant pauses and validation. Neither observation measures service speed,
total agent wall time or model cost. The two shortened retained allowlists are
additional workflow confounds.

Final AgentLang suite totals by stage are retained-1 146/146 then 147/147,
retained-2 146/146 then 146/146, reset-1 142/142 then 143/143, and reset-2
142/142 then 142/142. Unchanged counts can include new assertions in an existing
case. These totals are not interchangeable with independent behavioral scores
or cross-language coverage rates; F# has no AgentLang library gate.

Both retained participants shortened their first-stage launch allowlists to
subsets, had rejected queries, and recovered from stale revisions. These
deviations confound workflow comparisons; they did not expand permissions.
Reset-1 recovered from a
branch type mismatch during migration; its zero-duration test accepts any
error, a weaker assertion than the exact code checked by retained-1 and F#.
Its explicit missing-subscription error preserves the existing code and message.
F#-2 recovered from a malformed hash before applying its repair.
F#-1's proposed-state test uses the existing handoff as its expected result
and separately verifies unrelated state; the independent oracle supplies a
separate model rather than relying on this shared dependency.
F#-2 derives its expected proposed state from cancellation and creation.
Reset-2 retains the original comparison and adds proposed equality in the same
success case; independent review confirms this satisfies the frozen scope.
Its fixture includes unrelated customer/product state. The expected proposed
state also uses cancellation/creation but falls back to the original on error,
a test robustness weakness that independent model scoring does not share.
Retained-2 similarly adds proposed equality to its existing success case,
explicitly compares unrelated Store collections, and derives the expectation
from handoff with an error-to-input fallback. Its original assertion remains.
Both second AgentLang participants recover from a generated-constructor casing
error. Reset-2 also has an interim 4/5 target test run before its final repair.

## Controls and provenance

[Report 178](178-preview-defect-control-calibration.md) established that the
tested faulty shortcut scores 29/37 while correct controls score 37/37.
Stage-two correct controls pass both projections in retained, reset and F#.
The saved swapped-field and proposed-equals-input negative controls qualify
under deliberately matching tests yet each score 27/37 on the affected field.
Coverage, behavior and preservation are distinct measurements.

Calibration also exposes a usability pressure: an unexercisable nested success
branch blocks strict library qualification, and the corrected reset control
writes the established missing-subscription error explicitly instead. Its code
and message are preserved, but this can encourage duplicated validation
knowledge. Keep the gate strict for this study; later examine reusable typed
error construction or compiler-proven unreachable paths without weakening
checks for reachable branches.

Failed calibration attempts remain evidence: generic Result typing, an
unexercisable nested branch rejected by the library gate, a missing dependency
in the first disposable F# scorer build, and a Windows path-length failure.
Corrected controls pass before participant dispatch; these are coordinator
setup failures, not participant behavior.

The conventional broker is freshly built locally; unchanged AgentLang runtime
and foundation hashes match checkpoint 178. Each saved candidate is captured
only after terminal status, then scored from a disposable copy with before/after
hashes. A review found missing mandatory capture linkage in the initial scorer;
the amendment adds snapshot-to-capture and capture-to-trace checks without
changing prompts or semantics. The first saved score's linkage was independently
verified, and the corrected guard was reviewed again. The original wrapper's
source bytes were not retained, only its hash receipt and result. A supplemental
verification from the same captured candidate with the corrected guards again
passes 37/37; its executed wrapper source and result are archived. This is a
verification run, not another participant outcome.

## Interpretation and limits

Two participants per condition in one fixed-order sequence cannot estimate
population reliability or establish a causal language benefit. The corpus is
partly reused. No authoritative provider tokens or effective context limits
are available; broker exchanges, payload bytes and duration describe protocol
activity, not total model cost or agent wall time.

The local trials use isolated projects and existing brokers, with pure/virtual
application effects and no network/mail operations. No product runtime source,
allocator, native ABI or host capability changes here. Package audit is disabled
in correctness builds; this report makes no dependency-security or cybersecurity
certification claim. The latest complete product Release gate remains report
176 because product source is unchanged.

The bounded result is reliable repair and typed migration in all three
conditions, with discovery/reuse demonstrated and no observed correctness lead.
Do not add more easy composition trials to establish basic feasibility. Resume
native LLVM/arena conformance as the secondary delivery track, and retain
behavioral and collateral-preservation checks for future maintenance research.

The [archive index](evidence/179-preview-repair-and-record-migration/archive.json)
pins the [evidence ZIP](evidence/179-preview-repair-and-record-migration/evidence.zip):
frozen prompts/seeds, controls and failed attempts, captured candidates, raw
protocol traces, scoring sources/results, metrics and per-stage source reviews.
