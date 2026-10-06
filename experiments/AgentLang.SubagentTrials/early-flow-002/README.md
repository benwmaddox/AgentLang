# Repeated external-agent comparison

Status: six of fifteen repeated trials accepted; see [checkpoint 061](../../../reports/061-repeat-discount-task.md).

Repeat the five public tasks from `../early-flow-001/tasks.md` with the same
schema-only Flat seed, conventional seed and independent 20-vector/task oracle.
Do not edit the archived pilot-001 primers, binaries, fixtures or evidence.
The updated interface includes compact language discovery/exact Flow references
and a hash-guarded conventional text patch. Results are a new apparatus version,
not additional observations under pilot-001's frozen runtime.

Use GPT-6 Luna/max with zero inherited turns for each task. Run trials serially.
For tasks 1–5, rotate arm order: Conventional/Growing/Flat,
Growing/Flat/Conventional, Flat/Conventional/Growing,
Conventional/Growing/Flat, Growing/Flat/Conventional. Each arm retains only
independently accepted state; Flat instead resets to schema-only for each task.
Pause a failed sequence at its last accepted state; retain failures and costs.

Before launching, archive the exact complete prompt, chosen public task, common
instructions, arm primer, model/reasoning/context setting, source and executable
hashes, seed/state hashes, host arguments/allowlist and trace path. No post-hoc
reconstruction is a substitute for these prelaunch records. Pin the verified
new runtime/tool commit after the patch milestone is published. The acceptance
oracles stay outside editable trial roots and are never given to trial agents.

Launch prompts include the same goal, task policy, schema description and
common instructions, followed by the appropriate interface primer and exact
host launch configuration. They do not enumerate retained helpers or disclose
solutions. Save each prompt before passing that same text to spawn_agent.
Do not silently add implementation advice, change model, or inherit context
after a launch failure. Record capacity/approval interruptions separately and
resume the same agent/session where possible; any context fork becomes a
separately identified deviation.

Agents report completion after successful public tests and durable task commit
(language), or public validation (conventional). They leave the interactive
host session open. Coordinator teardown is a separate apparatus event, outside
the agent-work interval; preserve its raw trace and do not count cancellation
as a compiler/runtime failure. Time the serial active agent interval and report
interruptions rather than claiming controlled latency when capacity disrupts it.

Collect raw exchanges, request/response bytes, errors/recovery, final state,
self-tests, independent acceptance and preservation of prior types/helpers/tests.
Use existing `Verify-EarlyFlowTask.ps1` and `Verify-EarlyConventionalTask.ps1`.
Compare the same task across arms; different task difficulty prevents treating
a downward sequence trend as learning. Exact model tokens/turns remain unknown
unless the chosen interface actually supplies them. Protocol exchanges/bytes
are explicit proxies. Strong library coverage versus conventional self-tests
is a policy difference to disclose. The full PRD's success criteria and
small-context study remain open.
