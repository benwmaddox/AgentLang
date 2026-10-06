# 057 — Early agent behavior purpose review

Status: complete exploratory sequence, 2026-10-06. All 15 trials independently
accepted: 300 behavioral evaluations and 695 acceptance checks. This establishes
the workflow, with no demonstrated overall agent-cost advantage.
The executable runtime remains frozen at `e87931a`. This report evaluates external
AI coding agents using the language, not AI behavior inside the runtime.

## Decision being tested

The shortest useful checkpoint is five matched tasks in three modes: Flat resets
to schema-only words; Growing retains independently accepted vocabulary;
Conventional retains independently accepted F# code. Each task uses a separate
Luna/max agent. Conventional task 4 and Flat task 4 receive a one-turn approval
fork; other launches use zero inherited turns. The first sequence checks
feasibility and apparatus; rotated
repetitions are necessary before claiming an efficiency improvement.

The language provides stronger publication requirements: a retained library word
must have passing own tests and complete own instruction/branch coverage. Those
requirements cost interactions. Independent behavioral acceptance remains
necessary: milestone 055's wrong-solution controls pass their own tests and
coverage but fail the external oracle.

## What the evidence already establishes

Fresh agents can discover nominal record accessors and existing word source,
define pure typed words, repair diagnostics, test them, publish library words,
and complete task sessions through the protocol. Growing task 2 uses the earlier
premium predicate. Growing task 4 composes the retained discounted-balance and
annual-renewal predicates. Their stable identities and earlier revisions remain
unchanged. Conventional agents also discover and reuse their prior helpers.

| Task | Flat exchanges / response bytes | Growing exchanges / response bytes | Conventional exchanges / response bytes |
| --- | ---: | ---: | ---: |
| Premium predicate | 16 / 20,063 | 17 / 23,095 | 6 / 2,552 |
| Discounted balance | 15 / 22,140 | 15 / 20,671 | 15 / 7,247 |
| Annual renewal predicate | 19 / 25,615 | 16 / 24,987 | 16 / 16,371 |
| Renewal balance | 19 / 27,571 | 18 / 26,754 | 13 / 15,167 |
| Renewal savings | 23 / 25,172 | 18 / 26,828 | 9 / 13,560 |
| Sequence total | 92 / 120,561 | 84 / 122,335 | 59 / 54,897 |

Broker exchanges include rejected shutdown input. They are runtime interactions,
not model turns or external tool invocations. Full counts, errors and request
bytes are retained in the pilot metrics. Every accepted language trial passes
48 independent checks; each conventional trial passes 43. Both use the same
20 behavioral vectors; the language adapter has additional structural checks.
These check counts must not be compared as differing amounts of task correctness.

Growing task 4's target has 10 own IR instructions versus Flat's 33, and 2 own
branch outcomes versus 4. This demonstrates compression of the new definition,
not reduced transitive execution, native memory, or overall agent cost. Growing
task 5 has 8 own instructions and composes the complete renewal policy, versus
Flat's 29 instructions / 4 branch outcomes. The final Growing dictionary retains
five pure library words and 23 passing attached tests; Conventional retains
five implemented functions and 41 passing self-tests. Flat's independent words
have 4, 3, 6, 10 and 7 passing own tests respectively.

Growing uses eight fewer exchanges across the sequence than Flat (84 versus 92),
and fewer rejected runtime requests (7 versus 14). Its response data is slightly
larger (122,335 versus 120,561 bytes). In the final task Growing uses 18 exchanges
versus Flat's 23, while Conventional uses 9. These descriptive results are a
reason to repeat the comparison, not a causal or model-token saving claim.

Growing response payloads exceed Conventional for all five tasks. Request bytes
are smaller for Growing on later tasks, but Conventional's broker replaces full
files, including retained tests, while the language edits definitions. A normal
repository patch tool could reduce that difference. Do not infer an output-token
advantage from these apparatus-specific byte counts.

Namespace spelling is repeated friction. Agents confuse root references such as
`::customer.renewal-balance(...)` with the supported qualified spelling
`customer::renewal-balance(...)`. Correct typed semantics do not eliminate that
interface cost. Inspection is useful, but metadata must be concise and directly
actionable to make it cheap.

The task-3 Flat broker records four rejected runtime requests while the agent's
final summary/task log emphasizes two errors. Aggregate task logs are not a
complete research oracle; use the preserved raw protocol traces for counts.

## Limits and next decision

These tasks use two small records with String/Float fields, not the full strong
business domain or decimal Money. Exact model tokens and turns are unavailable;
protocol bytes/exchanges are proxies. Concurrent runs do not provide controlled
latency. No physical memory, LLVM, arena, or native performance claim follows
from this experiment. Path-restricted tools and instructions are not OS isolation.

Launch messages were not identical and Growing task 2 received a coordinator
clarification that the broker emits no startup banner. Later launch messages
stated that explicitly. Exact per-agent launch prompts are not completely
archived; the public primer/task contract and protocol traces are retained.
Missing per-trial provenance files identified in independent review are completed
from recorded binary/starting-state evidence after the runs, explicitly marked as
reconstructed. These are apparatus limits, not a controlled prompt comparison.

The conventional task-4 launch was blocked twice before any host process or
project mutation. The human then explicitly confirmed the remaining isolated
experiments. The replacement receives that confirmation in a one-turn fork with
no inherited implementation source. Later one-turn launches can inherit
coordinator observations and metadata from the approval turn; they must not be
described as zero-history controls. This is an additional comparability limit.
Earlier Growing task 3 resumed the same agent and
host after a model-capacity interruption before its first request. Both are
apparatus events, not compiler failures. Report them separately from completed
trial interactions.

Flat task 4 also hit model capacity after nine discovery requests and resumed
the same agent/session. No model substitution or private implementation advice
was supplied. Growing task 5's one `words` response is 14,829 bytes out of 26,828
returned payload bytes (55.3%); this identifies a concrete retrieval cost rather
than a conjecture about model token usage.

Keep the next implementation work driven by this comparison. Concise discovery
responses, canonical callable references, clearer protocol argument contracts,
and normal broker shutdown are candidate low-cost improvements. Larger domain
expansion, allocator/mailbox experiments and LLVM remain behind this purpose
checkpoint. Full PRD evaluation and success criteria remain open.

## Validation and publication

Every trial's target, attached tests, independent outputs and state preservation
were checked before retaining its result. All language schema declarations are
unchanged; Growing preserves every earlier word head and revision. Conventional
preserves prior behavior/tests; task 4 moves the Subscription module before
Customer for F# name resolution, and task 5 reformats one existing then-clause.
Raw artifacts are retained in
[the byte inventory](../experiments/AgentLang.SubagentTrials/early-flow-001/runs/pilot-001/inventory.json).
No agent trial implementation was substituted with coordinator-written code.
Fresh reconstruction from the saved final Flat, Growing and Conventional
artifacts passed 48, 48 and 43 checks respectively (139 total). These are
coordinator replay checks, not additional agent trials; saved replay evidence
is under `reports/evidence/057-*-artifact-replay.json`.

The independent Luna/max report review spot-checked 14 completed trials and found
no material arithmetic inconsistency or unsupported efficiency claim. Its
provenance/status findings were addressed; root checked the final Flat trial
separately. The [raw review and resolution](evidence/057-independent-purpose-review.json)
retain the review's scope and its transport-clarification shorthand correction.

Executable source remains milestone 055. Clean main CI
[37473514628](https://github.com/benwmaddox/AgentLang/actions/runs/37473514628)
passed all 32 required checks on `4125ed62ef599ee14de7531a84cb0d06623003fa`.
[Saved validation](evidence/056-task2-main-validation.json) was audited for the
exact revision, clean checkout and all zero exit codes. This publication updates
reports and trial evidence; it makes no runtime changes. Focused trial acceptance
is separate from that CI, which does not replay all 15 newly captured trials.
All 342 inventoried artifact files, including the inventory, were checked against
their staged Git objects for exact byte preservation before publication.
