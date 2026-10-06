# Matched selective-retrieval study

Status: frozen protocol, 2026-10-06; [study-001 checkpoint](../reports/067-selective-retrieval-study.md)
records execution on published 94a2d7f binaries after clean CI and fresh preflight.
This follows
[purpose review 064](../reports/064-repeat-agent-purpose-review.md) and the
[context plan](SELECTIVE-CONTEXT-PLAN.md). It studies external AI coding tools;
the language has no embedded AI agents.

## Matched task and state

Run four fresh GPT-6 Luna/max actors with no inherited turns, serially in ABBA
order: Full, Compact, Compact, Full. All receive public task 4 from
`experiments/AgentLang.SubagentTrials/early-flow-001/tasks.md`: calculate renewal
balance with the premium and annual-renewable discounts. Keep the task policy,
schema, library testing requirements and hidden acceptance identical.

Copy the complete archived
`early-flow-002/runs/repeat-001/growing-task-3/final-project` into four new isolated
roots. It contains Customer, Subscription and three accepted helper words.
Never modify the archived state. Record all starting file hashes and prove the
copies match. Verify current tests and type/word identities with the new pinned
runtime before launch; any migration or incompatibility requires a protocol
revision. The separate matched-renewal snapshot is a different fixture and
cannot substitute for this state.

## Retrieval treatment

Use the same host operation allowlist for every actor. Full guidance recommends
selected `describe` and `source` inspection. Compact guidance recommends bounded
`context` entries with explicit fixed depth, word and UTF-8 limits, including
the parser-verified call references added in milestone 065. Both allow fallback
to detailed inspection and ordinary search. Record crossovers instead of
discarding or censoring them. Analyze outcomes by assigned guidance.

Before launching any actor, preflight metadata retrieval on a separate read-only
copy and freeze both prompts, the compact query limits and any common cumulative
host inspection-response cap. Use metadata queries only; do not build or provide
a task solution. Ensure complete needed helper/type entries fit the compact
setting, and record exactly what the preflight queried and returned. Do not
tune limits after observing a trial outcome. The same optional cumulative cap
applies to both arms. Test/mutation/validation results remain visible outside
that inspection cap, so it does not bound all agent input.

The saved [preflight](../reports/evidence/066-study-preflight.json) returns 4,024
payload bytes for two helper contexts, compared with 2,135 bytes for two selected
`describe` responses. The context includes dependency/type closure while describe
includes detailed root metadata; these are different payloads. Compact guidance
does not guarantee smaller traffic on this task. Before selecting the common
cap, also measure shared discovery and plausible source/dependency retrieval,
then choose a round allowance above both prefixes with room for a fallback.

The [extended preflight and cap selection](../reports/evidence/066-study-budget-selection.json)
records 1,481 bytes for shared compact inventory and two searches. Adding two
describes, sources and dependency queries gives a 5,039-byte Full prefix;
adding the two contexts gives a 5,505-byte Compact prefix. Freeze the common
inspection allowance at 16,000 payload bytes, leaving at least 10,495 after
either measured prefix for fallback retrieval. This choice precedes every actor
launch and is a feasibility allowance, not a model-window claim. Runtime and
host pins still require publication and prelaunch records. The
[raw frame capture](../reports/evidence/066-raw-preflight-frames.json) retains CR
while excluding LF, matching host accounting. Serialized JSON alone is one
byte smaller for each Windows CRLF response; it is recorded separately.

## Acceptance and evidence

Run `scripts/Verify-EarlyFlowTask.ps1 -Task 4` with the pinned CLI, final isolated
project and a new evidence path. This checks 20 independent behavioral vectors,
the exact signature, pure persistent library status, attached tests and complete
current own instruction/branch coverage. Separately compare the starting and
final record/type identities and each of the three prior helpers' stable ID,
revision, definition and attached tests. They must remain unchanged.
Compare Customer/Subscription type IDs and definition/source hashes; compare
each helper's dictionary name, stable ID, revision, persistent library status,
exact definition/source hashes and attached test names, sources and owners.
Confirm all retained tests still pass. Only the new task-4 word/tests and task
bookkeeping may be added.

Archive prompts before launch, model/context settings, source/binary/host pins,
starting trees, host arguments, raw traces, coordinator interventions, final
projects, acceptance and preservation evidence. Teardown is a separate apparatus
event after the actor completes; no automatic replay of uncertain requests.
Retain failed trials and budget denials as outcomes.

Report accepted tasks, assigned-policy compliance, inspection payloads admitted,
raw responses suppressed, selected/delivered host bytes, all noninspection
responses, exchanges and diagnostic recovery. Pipe delivery is not evidence of
model consumption. Exact tokens, model turns and controlled latency remain
unavailable unless the actor interface supplies them. Four actors on one task
provide a bounded feasibility comparison; they cannot establish the full PRD's
cost-reduction thresholds, 2k–32k model-context results or domain generalization.
