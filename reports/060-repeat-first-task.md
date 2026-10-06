# 060 — Repeated external-agent comparison: first-task checkpoint

Status: partial sequence, 2026-10-06. The repeat uses the published `dc18d6e`
runtime and conventional patch interface. These observations are separate from
the frozen exploratory pilot in report 057.

## Observed outcomes

Fresh zero-history GPT-6 Luna/max agents ran serially, starting from equivalent
Customer/Subscription schemas. The public task required exact, case-sensitive
premium classification. Independent acceptance uses 20 behavioral vectors per
arm, outside the editable projects.

| Arm | Independent checks | Own test cases | Exchanges | Request bytes | Response bytes | Runtime errors |
|---|---:|---:|---:|---:|---:|---:|
| Conventional | 43 passed | 6 | 15 | 5,908 | 6,251 | 3 |
| Growing | 48 passed | 3 | 11 | 1,008 | 6,188 | 0 |
| Flat | 48 passed | 5 | 9 | 1,145 | 5,531 | 0 |

All three arms passed: 60 behavioral evaluations and 139 independent checks.
Twelve trials across tasks 2–5 remain in this repeated sequence.

Byte totals are UTF-8 protocol payloads, excluding framing and coordinator
acceptance. They are proxies, not model token usage. Conventional's three
`HASH_INVALID` rejections preceded a successful patch and validation. The
agent's final summary reported two; the decoded raw trace is authoritative.
No rejected patch changed a file. Its final source differs only in the requested
predicate and self-tests; records, project file and future entry points remain
unchanged.

Growing discovered the generated record operations, defined the predicate,
passed its tests, committed it as a library word and committed the task. Its
coverage is 4/4 instructions and 0/0 branch outcomes: the equality predicate has
no explicit IR branch. Both original type identities/source objects remain
unchanged. This coverage does not prove general semantic correctness; separate
acceptance checks supply the behavioral evidence.

Flat also committed a pure library predicate and durable task, with 5/5 own
tests and 6/6 instructions covered (0/0 branch outcomes). Its extra instructions
come from named local bindings. The original type entries are preserved.
Flat's accepted task-1 state is archived but will not seed task 2; that arm
resets to the schema-only snapshot for every task.

## Evidence and interpretation

The [repeat artifacts](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/)
retain intended prompts archived before launch, common/primer/task hashes,
source and executable pins, starting states, raw exchanges, final projects,
metrics and independent acceptance. Provider-internal prompt bytes and exact
model usage are unavailable; submitted line endings may normalize. No controlled
wall-clock advantage is claimed. Hosts remained open after agent completion;
actor follow-ups stopped the existing sessions with Ctrl+C as separate apparatus
cleanup, without additional JSONL requests. The coordinator could not access a
child's process session directly.

Coordinator audit passed 76 wire/provenance checks across all 35 exchanges.
The inventory pins 61 artifact files; staging verified exact bytes for these
files and the inventory. Focused independent acceptance and Git diff checks
passed before publication. No runtime source changed, so the existing clean
32-check source gate remains the applicable source validation.

This task demonstrates usable inspect/define/test/commit behavior. It cannot
demonstrate a growing-vocabulary advantage: no previous domain word exists at
task 1. Different numbers of self-tests and library publication policies also
limit cost comparisons. The remaining tasks must test reuse and compare each
task across all three arms. Full PRD success, small-context studies and strong
business-domain coverage remain open.

The pinned source has clean main CI evidence: [run identity](evidence/059-main-ci.json)
and [validation](evidence/059-main-validation.json). Run 37495733969 passed all
32 required checks on clean `dc18d6efc0d002d1a752024301d42f54cdd49c07`.
This checkpoint changes reports and trial evidence, not runtime semantics.
