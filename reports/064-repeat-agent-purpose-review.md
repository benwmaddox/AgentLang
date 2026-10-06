# 064 — Repeated agent-purpose review: vocabulary reuse and observed interface cost

Status: complete repeat review, 2026-10-06. All 15 trials were accepted:
300 behavioral evaluations and 695 independent checks. Each arm passed all five
tasks. Aggregate protocol totals below were reconciled from the raw traces.
Fresh agents discovered and composed tested vocabulary, and the new language
definitions show task-specific compression. Overall cheaper development is not
established; this small repeat supports a targeted context study, not native or
performance claims.

The repeated sequence uses the frozen dc18d6e runtime, schema, public tasks,
20-vector independent oracle, and hash-guarded conventional patch interface.
Each task runs in three modes: Conventional retains tested F# helpers, Growing
retains published Flow words, and Flat starts from the schema. The repeat is
separate from the earlier e87931a pilot and its conventional whole-file
replacement apparatus; see [report 057](057-early-agent-purpose-review.md)
and [repeat preparation](059-repeat-comparison-preparation.md).

## Per-trial protocol observations

The byte figures are UTF-8 JSON payload bytes, excluding wire framing. Counts
below come from each saved raw JSONL trace; the adjacent metrics file is retained
as a cross-check. Error codes and exchange indices are read from response JSON
in the trace. “—” means no error response was recorded.

| Task | Arm | Result | Exchanges | Request / response bytes | Error responses in trace | Metrics and raw trace |
| --- | --- | --- | ---: | ---: | --- | --- |
| 1 — premium predicate | Conventional | Accepted | 15 | 5,908 / 6,251 | HASH_INVALID at 9, 10, 13 | [metrics](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/conventional-task-1/metrics.json) · [trace](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/conventional-task-1/trace.jsonl) |
|  | Growing | Accepted | 11 | 1,008 / 6,188 | — | [metrics](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/growing-task-1/metrics.json) · [trace](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/growing-task-1/trace.jsonl) |
|  | Flat | Accepted | 9 | 1,145 / 5,531 | — | [metrics](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/flat-task-1/metrics.json) · [trace](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/flat-task-1/trace.jsonl) |
| 2 — discounted balance | Conventional | Accepted | 9 | 1,798 / 7,681 | — | [metrics](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/conventional-task-2/metrics.json) · [trace](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/conventional-task-2/trace.jsonl) |
|  | Growing | Accepted | 14 | 1,442 / 8,837 | — | [metrics](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/growing-task-2/metrics.json) · [trace](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/growing-task-2/trace.jsonl) |
|  | Flat | Accepted | 17 | 2,690 / 12,483 | FLOW_UNKNOWN_LOCAL at 9; NAME_UNKNOWN_WORD at 10 | [metrics](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/flat-task-2/metrics.json) · [trace](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/flat-task-2/trace.jsonl) |
| 3 — annual eligibility | Conventional | Accepted | 19 | 2,584 / 19,752 | HASH_INVALID at 4 | [metrics](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/conventional-task-3/metrics.json) · [trace](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/conventional-task-3/trace.jsonl) |
|  | Growing | Accepted | 14 | 1,578 / 10,216 | — | [metrics](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/growing-task-3/metrics.json) · [trace](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/growing-task-3/trace.jsonl) |
|  | Flat | Accepted | 13 | 2,264 / 8,597 | FLOW_ROOT_CALL_REQUIRES_ARGUMENTS at 9 | [metrics](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/flat-task-3/metrics.json) · [trace](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/flat-task-3/trace.jsonl) |
| 4 — renewal balance | Conventional | Accepted | 14 | 8,428 / 13,631 | HASH_INVALID at 9, 11 | [metrics](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/conventional-task-4/metrics.json) · [trace](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/conventional-task-4/trace.jsonl) |
|  | Growing | Accepted | 19 | 2,899 / 16,167 | — | [metrics](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/growing-task-4/metrics.json) · [trace](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/growing-task-4/trace.jsonl) |
|  | Flat | Accepted | 14 | 2,347 / 10,178 | — | [metrics](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/flat-task-4/metrics.json) · [trace](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/flat-task-4/trace.jsonl) |
| 5 — renewal savings | Conventional | Accepted | 10 | 2,900 / 27,961 | — | [metrics](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/conventional-task-5/metrics.json) · [trace](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/conventional-task-5/trace.jsonl) |
|  | Growing | Accepted | 12 | 2,197 / 10,272 | — | [metrics](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/growing-task-5/metrics.json) · [trace](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/growing-task-5/trace.jsonl) |
|  | Flat | Accepted | 22 | 4,287 / 17,371 | NAME_UNKNOWN_WORD at 11; FLOW_EXPECTED_TOKEN at 13 | [metrics](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/flat-task-5/metrics.json) · [trace](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/flat-task-5/trace.jsonl) |

The earlier task reports provide acceptance, implementation, preservation,
and own-test context: [task 1](060-repeat-first-task.md),
[task 2](061-repeat-discount-task.md), [task 3](062-repeat-eligibility-task.md),
and [task 4](063-repeat-renewal-composition.md). Task 5's independent
acceptance records are linked alongside its saved
[Growing acceptance](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/growing-task-5/acceptance.json)
and [Flat acceptance](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/flat-task-5/acceptance.json).
Conventional's accepted
[Task 5 record](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/conventional-task-5/acceptance.json)
is also saved with its [final source](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/conventional-task-5/final-project/Domain.fs).

## What the repeat has tested

Tasks 2, 4, and 5 give Growing an opportunity to use accepted vocabulary from
earlier work. On task 5, its new customer.renewal-savings word calls the
previously accepted customer.renewal-balance word and computes the difference
from the original balance. The final state retains all four previous word
revisions and the two original type identities. Its new definition passed
seven attached tests and covers all 6 of its own instructions. Flat, starting
from the schema, passed six new tests with 35/35 own instructions and 4/4
branch outcomes covered. Both passed the independent 20-vector acceptance.
These figures show compactness of this specific new definition, not reduced
transitive execution or a general semantic advantage.

Reuse is not unique to Flow. Conventional Task 4 composes its existing
discounted-balance and annual-renewal helpers, and Conventional task 1–3
implementations also retained or reused prior helpers. Growing's compact task-5
definition is therefore evidence about vocabulary composition in this tested
case, not proof that conventional development cannot reuse code.

Conventional Task 5 also called its existing Customer.renewalBalance helper.
Its changed function passed 11 new self-tests, preserving the earlier 28 tests;
the root source audit found the project file and prior helper prefix unchanged.
It passed the same independent task acceptance. The
[Task 5 inventory](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/task-5-inventory.json)
records 60 arm-vector evaluations, 139 independent checks, 94 wire/provenance
checks, and 100 inventoried files.

Observed interface use varies by task. In task 5, Growing used 12 exchanges
and 2,197 request / 10,272 response bytes, while Flat used 22 exchanges and
4,287 / 17,371 bytes. Flat's raw trace records an existence probe before the
new word existed (NAME_UNKNOWN_WORD, exchange 11), then a declaration spelling
error (FLOW_EXPECTED_TOKEN, exchange 13): the agent used namespace-qualified
names for dictionary declarations, where dotted names were required. It
corrected the declaration and test owner names while retaining qualified call
references. In task 4 Growing used more exchanges and response bytes than Flat.
Conventional task 5 used 10 exchanges but returned 27,961 response bytes,
illustrating that exchange count alone does not describe interface use.
Across the sequence, Conventional had fewer exchanges than Growing, while its
request and response payloads were larger. Growing's exchange count was below
Flat's, but these observations are specific to the tested interfaces.

## Reconciled totals

Every accepted trial's exchange count, request bytes, response bytes, and error
response count from its raw trace matched its metrics file; there were no
discrepancies. The [reconciliation record](evidence/064-repeat-analysis.json)
lists each trace-derived error index, operation, and code as well as task, arm,
and overall totals. The [saved helper](evidence/064-repeat-analysis.ps1)
reproduces those totals read-only from the completed traces.

| Scope | Exchanges | Request bytes | Response bytes | Error responses |
| --- | ---: | ---: | ---: | ---: |
| Task 1 | 35 | 8,061 | 17,970 | 3 |
| Task 2 | 40 | 5,930 | 29,001 | 2 |
| Task 3 | 46 | 6,426 | 38,565 | 2 |
| Task 4 | 47 | 13,674 | 39,976 | 2 |
| Task 5 | 44 | 9,384 | 55,604 | 2 |
| Conventional, all five tasks | 67 | 21,618 | 75,276 | 6 |
| Growing, all five tasks | 70 | 9,124 | 51,680 | 0 |
| Flat, all five tasks | 75 | 12,733 | 54,160 | 5 |
| All 15 trials | 212 | 43,475 | 181,116 | 11 |

Exchanges and payload bytes are protocol proxies, not model turns or token
usage. Exact provider tokens/turns and controlled task duration are unavailable.
The arms also have different existing helper state and own publication/test
requirements; 43 conventional acceptance checks versus 48 language-arm checks
are not different quantities of behavioral correctness. Own instruction and
branch coverage describes the definition under test and does not establish
transitive execution cost, semantics by itself, or native performance. Earlier
task-specific interpretations are retained in reports 060–063.

The repeat still does not cover the full strong-type or business domain, decimal
Money, small-context operation, or provider usage. These remain open acceptance
criteria. A next useful experiment is compact selective context: expose only
relevant prior definitions and compare whether accepted composition survives
with less retrieved context. Broader domain expansion, LLVM, and memory work
should follow evidence on that question rather than these small examples.

The earlier pilot is not pooled with this repeat. Its executable was frozen at
e87931a and Conventional used whole-file replacement; this repeat uses
dc18d6e and the hash-guarded patch interface.

## Host verifier and gate evidence

Clean committed-source [CI run 37511554491](evidence/063-main-ci.json) passed
all 32 required checks on 07d5f4d1daeaa5190241bcb6c15ac442b74cde56 with a
clean checkout; see its [validation](evidence/063-main-validation.json) and
[host detail](evidence/063-main-subagent-host.json). The exact default local
Release workflow also passed all 32 checks on the same commit with a dirty
working tree containing trial/report artifacts; see the
[local validation](evidence/064-exact-ci-local-validation.json) and
[host detail](evidence/064-exact-ci-local-host.json). The focused fixed host check passed all 17
checks, and the explicit Release validation passed all 32, as recorded in
[report 063](063-repeat-renewal-composition.md).

The final [artifact audit](evidence/064-final-audit.json) verified 391 trial
artifact hashes across five inventories and 454 wire/provenance checks.

The original CI failure in
[run 37507670551](https://github.com/benwmaddox/AgentLang/actions/runs/37507670551)
was a single uncertain 2,000 ms first task.begin exchange; its request was
not automatically replayed. The original local focused check passed at the
same setting, so the delay cause was not established. The verifier now gives
only the real-runtime language-write and language-reload happy paths 15,000 ms
exchange allowances and request-count-based outer budgets of 90,000 ms and
60,000 ms. Adversarial partial-response and no-read deadlines, byte caps,
cleanup, no-replay behavior, recovery assertions, and persistence checks are
unchanged. No change to the host production defaults is claimed.

The saved repeat
[artifact tree](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/)
retains the frozen prompts, initial snapshots, raw exchanges, independent
acceptance records, metrics, and final states. Provider-internal prompt bytes
and exact model usage are unavailable.
