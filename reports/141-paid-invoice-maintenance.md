# 141 — Maintenance of shared payment vocabulary

Status: all four submissions independently scored; behavior passes in both arms.
Starting revision: `aff6a84`.

This study returns to the primary question of reliable later edits to discovered,
reusable vocabulary. It follows the runtime comparison in report 140 without
treating native performance as evidence about agent editing quality.

The comparison uses two fresh agents per environment, starting from the
accepted retained-language and F# second submissions in report 135. Both account
and dashboard summaries already share a customer payment-total operation. The
maintenance task adds a paid-invoice status requirement while preserving customer
ownership, missing-invoice handling, checked arithmetic, error precedence and
unrelated behavior. Independently score behavior, shared structure, collateral
changes, authored tests and task finalization.

The language's underlying helper was authored by an earlier agent; the original
F# equivalent was supplied by the coordinator. Both summary integrations were
agent edits. These histories and the different discovery/editing interfaces
prevent an identical-provenance or causal language-advantage claim. Four agents
on one change provide bounded observations, not a general superiority estimate.

The new rule affects defensive imported states. It does not repair a defect
reachable through the conventional application's normal payment API, which
already couples a successful payment with a paid invoice. The supplied builders
deliberately bypass those construction invariants for both arms. Keep that
limitation visible when interpreting the result; this is a small shared-rule
maintenance task, not a broad application-correctness benchmark.

## Participant results

All four terminal submissions have been independently scored. Each passes
all four frozen scenarios, evaluated at the direct helper and both summaries
(12 observations per participant). Fresh full-suite runs also pass. These are
observations from one small maintenance task, not evidence of a general language
reliability advantage.

| Participant | Independent scenarios | Fresh attached suite | Shared structure / preservation | New-test requirement | Broker exchanges / error responses |
| --- | --- | --- | --- | --- | --- |
| Language 1 | 4/4 | 195/195 tests | Pass / pass | Fail | 71 / 10 |
| Language 2 | 4/4 | 197/197 tests | Pass / pass | Fail | 98 / 12 |
| F# 1 | 4/4 | 19 groups / 174 assertions | Pass / pass | Pass | 36 / 7 |
| F# 2 | 4/4 | 19 groups / 163 assertions | Pass / pass | Fail | 25 / 3 |

The failing new-test dimension is literal compliance with the frozen task:
Both language participants and F# 2 added signed-payment, Open/missing-invoice and
summary regressions, but relied on inherited tests for paid overflow and unknown
customer precedence. The task explicitly required those behaviors in **new**
tests. Their combined suites and independent behavior checks pass; the omission
must not be described as an observed production correctness defect. Conversely,
passing behavior does not erase the separate frozen acceptance failure. F# 1
newly tests those cases as requested.

This requirement was unnecessarily prescriptive for ordinary maintenance, where
retaining suitable existing regressions is reasonable. Future studies should
assess adequacy of the resulting suite rather than demand duplicate assertions.
The present criterion is retained unchanged; it was not relaxed after results.

All four agents kept the shared aggregation path. F# submissions
modify `Store.customerPaidTotalStep`; both language submissions modify
`customer.paid-total-step`. The public helpers check customer existence first,
both summaries reuse them, signed additions remain checked, and earlier errors
remain sticky. Types, public contracts, fixture implementations and inherited
tests are preserved. Other language revisions attach tests and, in Language 2,
clarify the existing helper documentation; other executable behavior is unchanged.
Fresh reloads report no active task and current library coverage, including
38/38 instructions and 8/8 branch outcomes for each changed step. All four
broker sessions close with host/runtime exit 0. Both language tasks commit.

Exchange counts measure broker requests, not agent turns or LLM tokens. The
first language participant's 10 error responses include syntax/discovery and publication mistakes;
two additional failed draft-test attempts return successful protocol envelopes
and are counted separately. F# 1's seven include two failed build validations;
Language 2's 12 include discovery, refinement, syntax and publication probes;
its attached test runs pass. F# 2's three concern inspection arguments and patch hashes. Participants recover
before submission. Interfaces and payload granularity differ, so these counts
do not establish relative reasoning cost. No controlled latency or token claim
is made. Overall strict acceptance is 0/2 language and 1/2 F# because of the
new-test criterion; behavioral acceptance is 2/2 in each arm. Do not conflate
those outcomes or infer population success rates from these four agents.

The bounded conclusion is that agents can discover and maintain shared vocabulary
in both environments. The language's dictionary and library gates work in this
case, but do not establish better edits than conventional typed functions.
The language participants also needed more broker interactions here. The next
efficacy question should concern a normal reachable application change where
effects, module boundaries or reusable domain types can prevent a concrete
mistake, with resulting regression coverage assessed independently. More mailbox
or compiler infrastructure is not a prerequisite for that comparison.

## Preparation evidence

Fresh Release builds of AgentLang.Cli and AgentLang.Conventional.Cli pass with
zero warnings and errors. Independent copies of the accepted starting projects
pass 190/190 language tests and 18 F# groups / 157 assertions. Historical
submissions are unchanged. Local build and baseline logs are under
`.agentlang/maintenance-141/`.

All 388 archived non-build files in the two selected original projects match
the corresponding entries in report 135's published evidence ZIP. This verifies
the retained starts against the historical artifact, rather than trusting only
their current directory names or passing tests. The per-file check is saved as
`accepted-source-verification.json`; fresh build source and artifact hashes are
recorded in `build-provenance.json` in the same local evidence directory.

Before dispatch, we resolved a fixture-access mismatch: language records permit
defensive imported states, whereas the F# store/invoice/payment constructors are
private and its normal payment operation marks the invoice paid atomically.
Equivalent frozen test-only builders give both arms a way to author regression
tests for the new rule. Their purpose is public, and participants must preserve
their source and exclude them from production call paths. The language builder
has one attached registration test, increasing that starting suite from 190 to
191. Existing production construction rules remain unchanged.

The public task, independent oracle, starts, scorers and controls were checked
and frozen before participant dispatch. The freeze includes all four project
inventories, exact prompts/wrappers, tooling source and runtime artifact hashes.
Two fresh Luna/max agents per arm receive no prior conversation, a 100-exchange
broker limit and no access to private acceptance material. Dispatch is recorded
in the study's `dispatch/events.json`. No participant efficacy result is claimed
until the unchanged acceptance procedure has been executed.

The F# preflight now distinguishes the intended cases: the unchanged baseline
fails the two changed-rule cases and passes the two preserved-error cases; the
correct status guard passes all four; a plausible guard that also drops negative
payments fails the signed-payment case. Each invocation evaluates the direct
helper and both summaries. The correct control also passes the unchanged 18
groups / 157 assertions. Frozen inputs match before and after these checks.
This validates that arm's scorer, not agent efficacy. Evidence is in
`.agentlang/maintenance-141/fsharp-scoring/preflight-controls.json`.

Root independently reran the final language scorer: baseline 6/12 checks,
correct control 12/12, incorrect positive-only control 9/12, with zero runtime
errors. These are four scenarios evaluated at three call sites, not twelve
independent business scenarios. Both language control copies pass all 191
attached tests. The incorrect control therefore demonstrates again that passing
the existing tests is insufficient: it returns 100 and 113 where the independent
signed-payment case requires 70 and 83. This is coordinator control evidence,
not a participant outcome or a new comparative advantage claim.

Preparation encountered protocol and fixture errors before the freeze: appending
the dictionary text did not register a durable definition; registration needed
the correct syntax selector and an attached test; one self-test expected an Int
from a Bool body; and PowerShell's default JSON timestamp conversion corrupted
fixture literals. The final setup uses definition-level publication and preserves
timestamp strings. Some initial attempts survive only as tool-transcript
observations, not complete raw response files; do not represent them as fully
archived trials. No such attempt is counted as a participant result.

## Retained preflight evidence

The [preflight archive](evidence/141-paid-invoice-maintenance/preflight.zip)
contains the exact generated starts, control projects/results, raw retained
responses, build logs and 30 build-source snapshots. All 1,308 entries were
verified by size and SHA-256 against the
[index](evidence/141-paid-invoice-maintenance/preflight-index.json).
The archive is 6,433,611 bytes, SHA-256
`3cc65fd3cede92742656ad769049429be48ea18c6371252a03e0fc874c9cc18c`.
Participant projects and live traces are excluded from this preflight package.

The separate [results archive](evidence/141-paid-invoice-maintenance/results.zip)
contains final projects, terminal raw broker traces, source reviews, fresh test
logs, independent scorer outputs, the acceptance matrix and audit tools. Its
[index](evidence/141-paid-invoice-maintenance/results-index.json) records all 862
entries with byte lengths and SHA-256. The archive is 5,490,495 bytes, SHA-256
`9247137cd42c22e509777296ee8356ee59a1704d5fcc2cee377be1464582e01c`.
Build artifact directories are excluded. All 116 frozen-input, build-source,
runtime-artifact and post-grading project/trace checks pass; grading did not
change any participant source. Archive entries are verified against the index.

Coordinator metadata retries are retained separately: the first Language 2
reload passes 197 tests but sends five invalid `describe` requests using `name`
instead of `word`. Corrected descriptions in a separate process report coverage
as not-run because coverage is process-local. A final fresh process executes
the full suite and corrected descriptions together: 197/197 tests, five current
library coverage records with complete finite coverage, and no active task.
These coordinator errors are excluded from participant exchange/error counts.
