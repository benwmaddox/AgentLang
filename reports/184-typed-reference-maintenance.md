# 184 — Typed reference maintenance calibration

Status: calibrated locally, 2026-10-10. Six isolated participant projects and
prompts are prepared; no experimental participants have been dispatched.

The next study tests reliable maintenance: add same-shipment scan replay
idempotence and migrate scan reference fields to a nominal TrackingReference,
distinct from ShipmentId and String. Retained AgentLang vocabulary, AgentLang
with lower-level traversal helpers, and conventional F# share the same baseline
nominal ShipmentId and public behavior/error contract. Retained and F# have
equivalent setup-created lookup helpers. This is a vocabulary-availability
comparison, not organic accumulation from earlier agents.

The [study plan](../experiments/AgentLang.SubagentTrials/typed-reference-maintenance-184/study-plan.md)
and [task contract](../experiments/AgentLang.SubagentTrials/typed-reference-maintenance-184/task-contract.md)
define two fresh participants per condition. Independent acceptance has 18
complete-state/error vectors. Behavior, nominal identity, helper signatures,
inherited evidence preservation, qualification, finalization and recovery are
separate endpoints; the latter audits require captured source and ordered traces.

## Accepted calibration

All five accepted scores execute 18/18 cases with zero setup errors and leave
the input projects unchanged. Field metadata, helper signatures and nominal
negative probes pass separately. Flow negatives are eval rejection evidence;
F# uses positive and negative compilation controls.

| Control | Authored checks | Independent cases |
| --- | --- | --- |
| AgentLang correct | 25/25 tests | 18/18 |
| AgentLang always append | 22/22 tests | 13/18; five replay failures |
| AgentLang global deduplication | 25/25 tests | 17/18; cross-shipment failure |
| AgentLang global deduplication with matching wrong tests | 26/26 tests | 17/18; same failure |
| F# migrated correct | 9 assertions | 18/18 |

Each AgentLang setup reports successful library publication and task.commit.
The wrong-test control confirms that qualification can pass while behavior
remains wrong: coverage gates complement independent domain acceptance.
The F# migrated control preserves eight inherited assertions and adds one
full-Store replay assertion. Its fresh build has zero warnings/errors. Typed
positive probes compile; String/ShipmentId field assignments and a String
helper argument fail compilation with the expected types.

Both comparison CLIs were freshly built from accepted runtime revision
`49a7f30`, with zero warnings/errors. Baseline inherited suites pass: retained
AgentLang 21/21, reset-rich 18/18, F# 8 assertions. Each baseline scorer smoke
executes all 18 with the same 13 passes and five expected replay failures.
Unequal suite counts are setup differences; preserve each arm's own evidence
and do not compare raw assertion counts as a quality outcome.

The coordinator-only help/close broker and terminal-capture smoke passed. The
termination auditor reports one exchange and host/runtime exits 0/0. Capture
checks prompt/runtime hashes, terminal state, candidate snapshot hashes and
source/trace stability. This validates capture, not participant behavior.

The independent design review found matched contracts and resolved prompt and
evidence-rule issues. Frozen inputs include model, vectors, scorer, contract,
prompts, allowlist, seed sources/projects, broker scripts and runtime fingerprints.
The [calibration evidence index](evidence/184-typed-reference-maintenance/calibration.json)
and [archive](evidence/184-typed-reference-maintenance/calibration.zip) retain
accepted scores, available failed attempts, seed setup, review and capture smoke.

## Preparation failures and evidence gaps

The first retained-baseline scorer smoke executed zero cases: a temporary
observer was defined in an earlier CLI process and unavailable to later evals.
The corrected scorer defines and evaluates in one process after a disposable
preflight. The initial receipt remains retained as smoke-retained-baseline;
the corrected baseline is smoke-retained-baseline2.

An initial correct-control score omitted required status in its type-negative
probes, producing arity rejection. That receipt remains control-correct;
control-correct-2 supplies status and passes the intended type probes. Neither
earlier failure counts as behavioral or type-enforcement evidence.

The seed worker also reported an initial caller-before-helper publication order
that caused commit rejections despite passing selftests. Its failed manifests
and transcripts were deleted with disposable projects during regeneration.
That raw receipt is unavailable; the gap is disclosed, not reconstructed.
Corrected controls have retained clean publication/test/commit receipts.

## Limits and next step

Dispatch the six frozen external participants, capture terminal candidates and
score without interim oracle feedback. Review every inherited assertion/example
and permitted typed adaptation; independently audit library maturity, durable
task.commit plus reload, F# validation, broker finalization and trace-linked
recovery. A scorer placeholder is not evidence for these endpoints. Metadata
and behavior do not establish internal caller wiring without source review.

All trials use local isolated projects and existing brokers. No new application
capability, provider, package, external service or CI run is added. Calibration
is not an agent efficacy result. This small pilot cannot establish a reliability
ranking, vocabulary accumulation, LLM token/context savings, throughput or a
memory-policy advantage.
