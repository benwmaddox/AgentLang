# Fresh authoring-help agent follow-up

Two fresh external Luna/max subagents completed the Flat S06 and S07 tasks.
Both passed independent library metadata and behavior acceptance: **64/64 cases**
and **422 checks** in total. Both frozen trace audits failed because the host did
not record the documented Ctrl+C cancellation event. These are separate results.

| Task | Independent cases | Acceptance checks | Broker calls | Failed protocol responses | Final own tests |
| --- | ---: | ---: | ---: | ---: | ---: |
| S06 — discount basis points | 10/10 | 123 | 25 | 1 | 6/6 |
| S07 — discounted Money | 54/54 | 299 | 30 | 4 | 7/7 |

[Results](../experiments/AgentLang.SubagentTrials/business-policy-help-002/results.json)
and [run archives](../experiments/AgentLang.SubagentTrials/business-policy-help-002/runs)
preserve prompts, pins, raw exchanges, independent acceptance, starting/final
projects, byte-verification records and failed audit evidence. The original 001
study remains unchanged; its two Flat metadata failures are not reclassified.

The actors received only their frozen public prompts, with no inherited turns,
prior solutions or coordinator oracle. Each started from the same six-type
schema-only Customer project, used the supplied pure JSONL protocol and durably
committed the result. Coordinator verification ran on disposable copies, without
repairing either actor's implementation. Actual spawn arguments are saved in
[launch evidence](evidence/079-actor-launch.json); model settings cannot be inferred
from host traces alone.

Both actors queried `authoring`, `define` and `examples` help. Both retained
meaningful documentation and examples, passing own tests, complete current own
instruction/branch coverage, pure effects, correct nominal signatures and fresh
process reload. Each example passed. S06 recovered from a wrong Flow call spelling
and invalid Instant fixture. S07 recovered from lookup/syntax errors, failed eval
syntax and two incorrect expected boundary values. Explicit test-result batches
ran 12 S06 tests (six failed, then six passed) and 14 S07 tests (two failed, then
seven passed). Library commits rerun attached tests; those reruns are not included
in these explicit-batch counts.

The unchanged BigInteger oracle checks raw ordinal `premium`, signed Int64 Money,
negative truncation, large values beyond Float precision, and both endpoints.
All 64 cases passed. Full own coverage was a useful commit guard, but remains
insufficient by itself to establish correctness; independent cases are essential.

The frozen auditor requires an idle `host-cancelled` event and host exit 130,
or a separately classified explicit exchange-limit rejection. Both actors sent
`write_stdin` with `chars: "\u0003"` to their own sessions after acceptance. Both
tools returned exit 0, and the traces recorded `session-end` with host/runtime
exit 0 and no cancellation event. The audit therefore rejects both ends. No
criteria or frozen artifact was changed to turn this into a pass. Recorded
exchange counts and bytes below are coordinator observations, not results from
a passing full trace-integrity audit. Final project bytes match the independent
acceptance inventories after teardown.

S06 recorded 8,271 request and 29,877 response payload UTF-8 bytes. S07 recorded
11,515 and 29,618. These are protocol payloads, not LLM input/output tokens,
model turns or effective context limits. The runs were concurrent, and host
lifetimes include waiting for independent verification; no latency comparison
is made.

The prior Flat trials used 48 calls each and failed documentation acceptance;
these new trials used 25 and 30 and passed the full independent acceptance contract. That is encouraging
for discoverable authoring guidance. Public guidance and runtime help both
changed, there are only two fresh observations, and the prior trials' independent
behavior was not executed. This is not a controlled improvement estimate or an
efficiency advantage over conventional F#.

My assessment: the inspectable library workflow is usable on these tasks, and
agents can recover through runtime diagnostics and leave tested reusable words.
The shortest next work is to fix host termination observability in a versioned
host/audit protocol, then repeat a small matched sequence with rotated agents and
a reset-rich control to distinguish retained vocabulary from supplied APIs.
Measured model usage and context limits are still needed. The full 60-task
benchmark, task-log cleanup, broader providers and controlled evaluation remain
open. LLVM, arenas, mailboxes and no-heap research remain later work.

Validation: runtime source is unchanged from report 077, whose exact main CI
passed all 36 checks. Report 078 adds the passing 59-check control matrix,
repaired clean-source freezing and one-field tamper rejection before execution.
The exact 078 publication CI run 37571612474 is still in progress at report time;
no new full-gate pass is claimed here.
