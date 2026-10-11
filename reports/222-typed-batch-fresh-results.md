# 222 — Fresh typed batch maintenance results

Status: closed; both corrected-protocol participants independently checked.
This is one fresh, sequential Luna/max pair from the corrected
[221 protocol](221-typed-batch-fresh-comparison.md), following the same migration
task on equivalent documented, tested renewal vocabularies. It does not estimate
a reliability rate or isolate a causal retention effect.

Both implementations are behaviorally correct. Only the F# edit passes the full
preservation requirement. This pair provides no evidence of a comparative
reliability advantage for AgentLang. It supports the narrower conclusion that a
fresh agent can discover and compose its typed renewal vocabulary to perform the
migration, while revealing a preservation failure and an experiment checker defect.

| Outcome | Retained AgentLang | Conventional F# |
|---|---:|---:|
| Independent full-result cases | 9/9 | 9/9 |
| Inherited edit preservation | Rejected: one fixture ID changed | Accepted |
| Library qualification / fixed local validation | Passed | Passed |
| Normal broker finalization | Passed | Passed |
| Full edit acceptance | Rejected | Accepted |
| Broker exchanges | 58 | 28 |
| Request wire bytes | 38398 | 15388 |
| Response wire bytes | 134056 | 40212 |
| Runtime session duration | 6m27s | 7m09s |

The language also passes 66/66 creator regressions, 152/152 persisted tests and
39 example owners. F# validation preserves its inherited scenario checks and
executes its added regression; its existing console text is not an exact new
suite-size counter. These unlike counts are not directly comparable.

## AgentLang result

The saved implementation passes all nine complete-result cases and all 66 creator
regressions. Independent execution passes 152/152 persisted tests and all 39
example owners. The batch and step remain library-qualified with stable IDs;
their implementation composes the existing monthly/annual renewal functions.
Normal `host.close` finalization passes the termination audit, with host/runtime
exits 0/0. The actor, baseline and scoring copy remain unchanged during scoring.

Full edit acceptance is rejected. Independent source review finds one inherited
helper fixture changed outside the permitted result-wrapper/state adaptation:
`renew-batch-step/errors` changes `later-change.old-id` from
`30000000-0000-0000-0000-000000000001` to
`33000000-0000-0000-0000-000000000001`. The former exists in the fixture Store;
the latter is absent. The task expressly required preserving inherited fixtures.
Other inherited scenarios, assertions, APIs, IDs and examples are preserved.
Passing coverage and behavioral probes therefore do not establish a preserved edit.
No endpoint was repaired after the participant finished.

The raw automated score also fails its textual nominal-record gate. The frozen
checker includes legal trailing field semicolons in type names (`Int;`,
`SubscriptionId;`, `BusinessError;`). Flow/2 accepts those separators. An isolated
reproduction confirms that the equivalent newline form passes while the semicolon
form fails this regex. Separately, the same frozen scorer validates the exact
runtime Int, nominal SubscriptionId and nominal BusinessError metadata in all five
error fixtures. This is a source-format checker defect, preserved as such; the
oracle and candidate were not modified or rerun with relaxed gates. The fixture
preservation rejection remains independent of that defect.

The participant's "311/311 full suite" claim was a cumulative-count mistake:
5+2 targeted executions, a 152-test full suite, and another 152 executions from
`failed-tests` total 311 at that task-status query. Publication runs seven more
tests, producing 318 cumulative executions. There are 152 distinct persisted
tests, up from 151, with one new later-item regression and strengthened typed
failure assertions in inherited tests. Cumulative invocations are not suite size.

The complete broker trace contains 58 exchanges, 38398 request wire bytes and
134056 response wire bytes. Runtime session duration is approximately 6m27s,
including discovery, edits, local checks and closure. This excludes the prior
shell setup failure and parent preparation/scoring. Wire bytes are not LLM tokens.
The participant reports packet reading and launcher invocation as its only
non-broker operations; no task hints or control solutions were provided.

## Conventional F# result

Independent grading builds only copied implementation sources with participant
self-tests excluded. All nine cases execute and pass, including exact nominal
failure fields, full Store/input membership and precise instants. Original source
and copied source hashes are checked unchanged after build and execution. Fixed
project validation passes, and the termination audit confirms 28 exchanges and
host/runtime exit 0/0 with normal `host.close` finalization.

Independent source review accepts the edit. Only the three permitted files change:
Domain adds the nominal failure record; Operations adapts batch/state/step; SelfTests
adapts typed expectations and adds a later-item regression. Existing error mappings,
lower renewal APIs, input record, enum and all inherited fixtures/assertions remain.
Fixtures, Program and project/dependency files are byte-identical. The inherited
competing-first-error scenario gains localization checks, and the added case checks
the missing old ID after a successful prefix.

The implementation composes the same existing monthly/annual renewal vocabulary.
It initializes index zero, increments only after a successful renewal, stores the
original cause and returns an already failed state unchanged. Thus list folding
may continue but domain processing stops on the first error. No partial Store is
returned as success. The participant reports only packet reading/launcher invocation
outside the broker; that self-report is not an OS confinement proof.

## Interpretation and next decision

This arm shows discoverable typed vocabulary can support a correct contract
migration and recover from stale-source diagnostics. It also shows strict library
coverage does not prevent an unintended edit to inherited evidence. Preserve that
negative finding rather than redefining success around behavioral correctness.

F# uses fewer broker exchanges and wire bytes in this pair. AgentLang's slightly
shorter session is not a cheaper-correct-change result because its full edit is
rejected. Session time includes agent/tool latency, is not a native performance
measurement and excludes preparation/scoring. No statistical advantage follows
from these two sessions. Both use retained, documented helpers, so this comparison
does not isolate retention from programming-language or interface differences.

The first legacy-shell launch failure remains separately archived as instrumentation
failure; the corrected pair uses fresh contexts and the original sampled order.
No solution, oracle, outcome feedback or parent repair reaches either participant.
F# map ordering is reconstructed from fixtures/request IDs rather than measured as
native list ordering. Model token/context metrics and native memory/throughput are
not measured. Strict preservation failure is not a production behavior defect, but
it is an incorrect edit against the assigned acceptance criteria.

Next address the concrete authoring/measurement problems before another nominal
contract trial: use semantic type metadata rather than source punctuation for
record-contract checks, and make cumulative test invocation counts distinct from
suite size. Keep explicit inherited-fixture preservation checks and source review;
coverage alone cannot establish them. Continue compact-core/native work without
claiming that this pair proves vocabulary benefit. A later held-out sequence is
still needed to determine whether accumulated vocabulary improves reliable edits.

The [evidence index](evidence/222-typed-batch-fresh-results/index.json) seals raw
traces, scoring outputs/copies, endpoint source, independent reviews and summary
receipts. Every member is checked by byte length and SHA-256. Frozen inputs and the
earlier setup failure remain in report 221's archives; neither oracle nor endpoint
was repaired to turn a failed acceptance gate green.
