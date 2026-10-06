# 062 — Repeated external-agent comparison: annual eligibility

Status: partial repeated sequence, 2026-10-06. Task 3 adds an annual-renewal
predicate: exact term `annual` and renewable true. The frozen `dc18d6e` runtime,
schema, public tasks and independent 20-vector oracle remain unchanged. Fresh
zero-history GPT-6 Luna/max agents run serially in Flat, Conventional, Growing
order. Flat resets; the other arms retain their accepted task-2 state.

## Outcomes and interpretation

| Arm | Independent checks | New own tests | Exchanges | Request bytes | Response bytes | Runtime errors |
|---|---:|---:|---:|---:|---:|---:|
| Flat | 48 passed | 4 | 13 | 2,264 | 8,597 | 1 |
| Conventional | 43 passed | 6 | 19 | 2,584 | 19,752 | 1 |
| Growing | 48 passed | 4 | 14 | 1,578 | 10,216 | 0 |

All three arms passed: 60 behavioral evaluations and 139 independent checks
for task 3. Nine of fifteen repeated trials are accepted; six remain.

Flat committed one pure library word with 4/4 own tests and 7/7 instructions
covered (0/0 explicit branch outcomes). Its conjunction uses `bool::and`.
The initial attachment calls used `::subscription.annual-renewable?(...)` and
`::subscription.new(...)`; these mix absolute-root prefix and dotted dictionary
names. Validation rejected the document with `FLOW_ROOT_CALL_REQUIRES_ARGUMENTS`.
The agent corrected calls to `subscription::annual-renewable?(...)` and
`subscription::new(...)`, then passed tests, library/task commit and all 48
independent checks. Both original type entries are unchanged.

Conventional passed six new cases and preserved all twelve earlier cases. Its
only Domain.fs change implements `Subscription.annualRenewable` with exact term
comparison and `&&`. The project file and customer helpers remain unchanged.
Public validation passed all eighteen self-test cases, followed by 43 independent
checks. One `HASH_INVALID` patch rejection led to additional reads before a
successful edit; the raw trace contains nineteen exchanges. This is protocol
recovery cost, not a compiler or behavioral failure.

The conventional broker requires explicit hash-guarded edits and returns whole
files for reads. These differ from ordinary unrestricted repository tooling and
from word-level language operations. Repeated reads and hash-copy friction limit
causal interpretation of byte/exchange differences; this study compares these
specific interfaces, not every possible F# development environment.

Growing committed a third pure library word, with 4/4 tests and 7/7 instructions
covered (0/0 branches). Both earlier word IDs, current heads, complete revisions,
definition/test objects and schema entries are unchanged. Its trace includes
reading the existing customer-word sources before definition. This is observed
source retrieval, not direct customer-word reuse by the new predicate. No
runtime errors occurred. Unrelated vocabulary did not prevent completion in
this small case; that is not a general pollution or scalability result.

The three-arm results remain mixed. On task 3, Flat used thirteen exchanges,
Growing fourteen, and Conventional nineteen with hash recovery. Exact model
cost and controlled latency are unavailable; no overall efficiency advantage,
small-context success or full PRD completion is established. The remaining
renewal and savings tasks test composition of multiple retained helpers.

Task 3 does not need the earlier customer helpers. It tests adding another
reusable concept while preserving prior project knowledge. Tasks 4 and 5 will
provide the later opportunities to compose these separate helpers.

Instruction and branch coverage describe each word's own IR. A Boolean primitive
can implement conjunction without an explicit branch, while an `if` implementation
can have branch outcomes. Reported coverage is therefore not interchangeable
with semantic case coverage or proof of correctness. Attached tests and independent
behavioral acceptance remain separate evidence.

## Evidence

The [repeat artifacts](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/)
retain prelaunch intended prompts, starting-state hashes, source/executable pins,
raw traces, acceptance, metrics and final source/storage. Actual provider prompt
bytes, exact model tokens/turns and controlled duration remain unavailable.
Coordinator teardown uses existing sessions and is separate from agent requests.
This milestone changes evidence and reports, not runtime semantics.

Coordinator audit passed 98 wire/provenance checks across 46 exchanges and pins
74 task-3 artifact files. All hosts stopped with exit code 0 through separate
teardown follow-ups, without extra JSONL requests. Independent acceptance,
preservation audits, exact staged-byte checks and Git diff checks precede
publication. No runtime source changed.

The preceding publication has clean committed-source evidence:
[CI identity](evidence/061-main-ci.json) and [validation](evidence/061-main-validation.json)
show run 37504069872 passed all 32 required checks on clean
`c66797db8a860f72b12ac320ad9ce81ff359470f`.
