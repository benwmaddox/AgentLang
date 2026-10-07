# 081 — Benchmark completion audit

Read-only audit, 2026-10-07. No actor, build, test, or Git operation was run.
This report distinguishes benchmark artifacts from evidence that fresh AI
agents actually completed those benchmark tasks.

## Finding

The 60-task bank exists as a planned specification, not an executable benchmark.
It has 60 public task files, 60 host-only acceptance files, and 180 proposed
acceptance cases (three per task). All 60 manifest entries remain `planned` and
`executable: false`; all acceptance vectors are
`proposed-pending-host-review`, all execution and AgentLang/Conventional/reference
adapter statuses are `pending`, and every Flat/Growing/Conventional snapshot
hash is null. The current S07 one-minor-unit case is correctly specified as 0,
but it too remains a proposal. See the [manifest](../../experiments/AgentLang.Benchmarks/task-bank/manifest.json),
[task-bank contract](../../docs/BENCHMARK-SUITE.md), and [S07 cases](../../experiments/AgentLang.Benchmarks/task-bank/acceptance/S07.json).

The validator's 2,114 assertions checked inventory, schema, prerequisites,
public/hidden separation, and promotion-gate shape. They did not execute task
answers or verify the proposed oracles ([report 009](../../reports/009-experiment-baselines-and-task-bank.md)).
The contract explicitly says no AgentLang task adapter, Conventional candidate
adapter, or reviewed executable oracle has run. In particular, the bank's public
fixtures are compact operation fragments rather than the full typed business
entities; task-specific data factories, normalization, adapters, and pinned
snapshots remain prerequisites ([suite contract](../../docs/BENCHMARK-SUITE.md)).

## Fresh-agent evidence is separate

There is real fresh-agent evidence for related tasks, but none of it constitutes
an execution of the full 60-task bank against its own hidden files and pinned
fixtures:

- Reports [057](../../reports/057-early-agent-purpose-review.md) and
  [064](../../reports/064-repeat-agent-purpose-review.md) each cover five small
  tasks across Flat, Growing, and Conventional (15 accepted trial cells each).
  The older fixture is a small String/Float domain, not the strong Money and
  typed business bank. These demonstrate scoped reuse and successful work, not
  execution of the bank or a measured efficiency gain.
- [Report 076](../../reports/076-exact-money-agent-policy-study.md) records
  nine fresh actors on the S01/S06/S07 policy concepts: seven full acceptance
  passes and 158 independent behavior cases passed. Flat S06/S07 stopped at
  missing documentation before their behavior oracle cases. These runs use a
  separate study, not the bank's pending adapters/snapshots.
- [Report 079](../../reports/079-fresh-authoring-help-agent-follow-up.md)
  records two fresh actors passing 64/64 policy behavior cases and authoring
  acceptance, but both frozen trace audits failed on session termination. This
  is useful task evidence with an integrity limitation, not a clean bank run.
- Narrow debugging, refactoring, selective-retrieval, and stateful tasks are
  separately evidenced in reports [067](../../reports/067-selective-retrieval-study.md),
  [068](../../reports/068-shared-defect-agent-debugging.md),
  [069](../../reports/069-existing-vocabulary-agent-refactoring.md), and
  [071–072](../../reports/071-stateful-agent-reminder.md). They do not cover
  the bank's ten debugging and ten refactoring tasks.

The current [retention-003 plan](../../docs/VOCABULARY-RETENTION-TRIAL-PLAN.md)
is not an outcome yet. Its prepared design specifies 12 fresh actor-task cells
across two rotated blocks, Flat/Retained/Reset-rich arms, and S01→S07; it has no
Conventional arm. [Report 081](../../reports/081-matched-vocabulary-retention-preparation.md)
and its current artifact inventory say no actors have launched, no study is
frozen, and preflight/pin validation remains pending. The static
[retention acceptance corpus](../../experiments/AgentLang.SubagentTrials/business-policy-retention-003/acceptance.json)
is an oracle definition, not an actor result.

## Measurements

Current external-subagent records contain independent acceptance, test batches,
diagnostics, tool/broker exchanges, successful edits, and JSONL request/response
or inspection bytes. Some include timestamps, but concurrent runs and waits for
verification make them unsuitable as controlled latency comparisons. Their
`modelTokens`, `modelTurns`, controlled duration, and framework-context fields
are null or explicitly unavailable; payload bytes and broker exchanges are not
substitutes. For example, the saved [repeat metrics](../../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/growing-task-5/metrics.json)
has `modelTokens: null`, `modelTurns: null`, and `controlledDuration: null`.
Reports 057, 064, 076, and 079 make the same limitation explicit.

The in-repository [benchmark runner](../../experiments/AgentLang.Benchmarks/Runner.fs)
can record provider-supplied input/output/total tokens, model request turns,
tool calls, elapsed duration, serialized request bytes, and a per-request
application byte cap. It labels its `ceil(UTF-8 bytes / 4)` token estimate as
approximate. The provider usage type does not currently record cached-token
details. These are capabilities of a separate API harness, not measurements
from the user's fresh subagent trials; its byte cap is not an effective model
context window. No recorded study varies 2k/4k/8k/16k/32k context budgets.
This leaves PRD token/context success criteria unmeasured even though some
instrumentation exists ([PRD §§47, 57–60](../../docs/PRD.md),
[requirements rows 42, 47, 50, 57–62, 71, 74](../../docs/REQUIREMENTS.md)).

## Shortest remaining sequence

1. Finish retention-003 as already specified: complete preflight, produce clean
   frozen pins, run the 12 cells with fresh subagents, verify each actor
   independently, and pass the explicit close/integrity audit. Report its narrow
   retained-versus-reset-rich result without calling it the full PRD comparison.
2. Make the existing 60 bank executable before spending 180+ actor launches:
   review all 180 hidden cases against the reference oracles; add matched full
   typed fixtures/data factories; implement AgentLang and Conventional
   task/acceptance adapters; create and hash the Flat, Growing, Conventional,
   debugging, and refactoring snapshots; and validate known-correct, no-op, and
   wrong-solution controls. Keep oracle files outside all agent-visible inputs.
3. Smoke one task from each category through every adapter with pinned binaries
   and acceptance controls. Then run the full matched suite: 60 tasks × three
   PRD modes (180 actor-task cells per round), with multiple rotated rounds,
   retained dependencies for Growing, reset snapshots for Flat/independent
   tasks, and failures preserved. Use the already chosen fresh-subagent protocol
   and equivalent goals/tool access; do not infer success from fixtures or
   deterministic replays.
4. For any cost/context claim, add authoritative usage telemetry to the actual
   chosen agent channel, or leave those values explicitly unavailable. Capture
   model version/settings, prompt and tool schema, provider tokens, model turns,
   exact context accounting, and retries per cell. Run the predeclared context
   budgets with a clearly labeled provider-enforced or application-enforced
   policy. Report paired correctness and uncertainty with cost; never substitute
   exchanges or bytes for tokens/turns. This is required for the PRD's 20%
   signal and marginal-cost question.

The immediate blocker is therefore not another language feature: it is finishing
retention-003, then promoting the task-bank proposals into verified adapters and
fixtures. Even after that, a successful 60-task execution without authoritative
model usage and context trials would validate task capability but not the PRD's
efficiency or small-context hypothesis.
