# Selective retrieval study review

2026-10-06. All four actors completed the matched study, following the
[frozen protocol](../docs/SELECTIVE-RETRIEVAL-STUDY.md). External coding agents
use the same seeded domain, runtime, operation allowlist, library-test gate and
16,000-byte cumulative inspection allowance. Assigned order is Full, Compact,
Compact, Full. All four results pass independent acceptance and preserve the
seed. Compact guidance did not reduce inspection bytes on this small task.

The [published-runtime preflight](../experiments/AgentLang.SubagentTrials/selective-flow-001/runs/study-001/published-preflight.json)
pins source `94a2d7f`, fresh CLI SHA-256
`87ad0d1f1471c603d88077ccd1ce248b3ccb778278f75e476db1c622a8941d96`,
Core SHA-256 `d2b2440d444192d6df858009c850f392061f458c7807e14f294abed0c3874cf0`,
and the host script. The build passed with zero warnings/errors. All 11 seed
tests pass and both helper contexts fit without omissions. Its raw frames
reproduce the earlier cap-selection measurements: 2,135 bytes for two describes,
4,024 for two contexts, and 1,481 for shared inventory/search. The cap was fixed
before any actor launch. Each isolated starting tree matches all 33 archived
seed files. The [preparation script](evidence/067-prepare-selective-study.ps1)
archives complete LF-normalized prompts and per-actor provenance before launch.

The first coordinator preparation attempt used a nonexistent `data.passed`
field instead of counting `data.results`. It stopped before actor launch;
its [setup error](../experiments/AgentLang.SubagentTrials/selective-flow-001/runs/study-001-setup-failed/setup-error.txt)
is retained and its local preflight copy was preserved. The assertion was
corrected to require exactly 11 passing results. No language or host changes
were made and no limits were tuned from actor outcomes.

## Completed outcomes

| Actor | Assigned guidance | Accepted | Exchanges | Inspection bytes admitted | All response payload bytes | Error responses |
|---|---|---|---:|---:|---:|---:|
| 01 | Full | Yes | 11 | 5,594 | 8,607 | 0 |
| 02 | Compact | Yes | 13 | 9,011 | 12,698 | 0 |
| 03 | Compact | Yes | 11 | 6,330 | 9,022 | 1 |
| 04 | Full | Yes | 17 | 8,145 | 12,617 | 0 |

Actor 01 discovered and directly composed `customer.discounted-balance` and
`subscription.annual-renewable?`, added six attached tests, and committed a pure
persistent library word. Own coverage is 10/10 instructions and 2/2 branch
outcomes. The independent [acceptance](../experiments/AgentLang.SubagentTrials/selective-flow-001/runs/study-001/actor-01-full/acceptance.json)
passed all 48 checks, including 20 hidden behavioral vectors. The separate
[preservation audit](../experiments/AgentLang.SubagentTrials/selective-flow-001/runs/study-001/actor-01-full/preservation.json)
passed 41 checks: exact prior type definitions, word identities/revisions,
definition/test hashes and source bindings, plus unchanged immutable seed files.
The host was closed only after completion, using Ctrl+C on the same session;
host and runtime both exited zero. No protocol teardown request was added.

The [trace audit](../experiments/AgentLang.SubagentTrials/selective-flow-001/runs/study-001/actor-01-full/trace-audit.json)
checks raw/selected frame hashes, admission prefixes, selected-to-delivered
ordering and all eight counters at every snapshot. Actor stdout was not
independently captured. Trace-derived pipe delivery does not prove model
consumption. Exact tokens, model turns, controlled duration and model context
windows remain unavailable. No responses were withheld and the cap did not
bind in any actor. No raw response was withheld and no host control response
was needed.

Actor 02 also directly reused both helpers and added six tests, with the same
10/10 instruction and 2/2 branch coverage. Its
[acceptance](../experiments/AgentLang.SubagentTrials/selective-flow-001/runs/study-001/actor-02-compact/acceptance.json),
[preservation](../experiments/AgentLang.SubagentTrials/selective-flow-001/runs/study-001/actor-02-compact/preservation.json),
and [trace audit](../experiments/AgentLang.SubagentTrials/selective-flow-001/runs/study-001/actor-02-compact/trace-audit.json)
passed 48, 41 and complete trace checks respectively. It used both prescribed
helper contexts, then fell back to a description and source, and separately
queried constructor contexts. Those fallback choices are retained in its
assigned Compact outcome. It retrieved more inspection bytes than actor 01;
two observations do not establish a policy effect. It also closed cleanly
after completion, with no protocol teardown request.

Actor 03 retained the same two-helper composition with five own tests, full
10/10 instruction and 2/2 branch coverage, and passing
[48-check acceptance](../experiments/AgentLang.SubagentTrials/selective-flow-001/runs/study-001/actor-03-compact/acceptance.json),
[41-check preservation](../experiments/AgentLang.SubagentTrials/selective-flow-001/runs/study-001/actor-03-compact/preservation.json),
and [trace accounting](../experiments/AgentLang.SubagentTrials/selective-flow-001/runs/study-001/actor-03-compact/trace-audit.json).
Its first context request supplied plural `words` instead of singular `word`.
The `DISCOVERY_INVALID_ARGUMENT` response explains the missing string argument;
the agent corrected the request without coordinator help. The 276-byte diagnostic
counts toward inspection admission. It then used a description and two sources
as fallbacks. Task logging reports no language-task errors, but the protocol
trace correctly retains this earlier error. Both host processes exited zero
after the separate completion/teardown boundary.

Actor 04 used descriptions, sources and test metadata without context fallback.
It composed the same helpers and added five own tests with 10/10 instruction
and 2/2 branch coverage. Its
[acceptance](../experiments/AgentLang.SubagentTrials/selective-flow-001/runs/study-001/actor-04-full/acceptance.json),
[preservation](../experiments/AgentLang.SubagentTrials/selective-flow-001/runs/study-001/actor-04-full/preservation.json),
and [trace audit](../experiments/AgentLang.SubagentTrials/selective-flow-001/runs/study-001/actor-04-full/trace-audit.json)
also pass. It additionally ran both retained helper suites before committing;
those two test operations contribute to its larger exchange count. The host
closed cleanly after completion.

The [aggregate audit](evidence/067-summarize-study.ps1) and saved
[summary](../experiments/AgentLang.SubagentTrials/selective-flow-001/runs/study-001/summary.json)
verify ABBA order without overlapping host sessions, prompt preparation before
launch, exact seed inventories, runtime pins, allowlists, caps, deadlines and
accepted helper reuse. Four fresh Luna/max actors produced 80 independent
behavioral evaluations, 192 acceptance checks and 164 preservation checks.
The first aggregate audit incorrectly reparsed JSON DateTime values as local
strings, losing UTC kind. Direct DateTimeOffset conversion corrected that
coordinator assertion; no timestamps or trial outcomes changed. The
[aggregation note](../experiments/AgentLang.SubagentTrials/selective-flow-001/runs/study-001/aggregation-notes.txt)
retains the correction.

| Assigned arm, two actors each | Exchanges | Inspection bytes | Request bytes | All response payload bytes | Errors |
|---|---:|---:|---:|---:|---:|
| Full | 28 | 13,739 | 4,383 | 21,224 | 0 |
| Compact | 24 | 15,341 | 4,749 | 21,720 | 1 |

Compact guidance used 11.7% more inspection bytes and 2.3% more overall response
bytes here. Its four fewer exchanges include the two extra helper-test calls
made by Full actor 04; this is not a clean retrieval-call or model-turn effect.
Both Compact actors used permitted detailed fallbacks, while both Full actors
stayed with detailed retrieval. Analyze assigned guidance rather than relabeling
fallbacks or discarding the diagnostic. With two actors per arm and one task,
these differences describe the observed traffic, not a general treatment effect.

## Validation and interpretation

Clean committed-source [main CI](evidence/066-main-ci.json) passed all 32 checks
at `94a2d7f`; its [artifact](evidence/066-main-validation.json) records
`dirty: false`. This milestone adds experimental evidence and read-only audits,
with no F# runtime or host changes. Each completed actor is independently
validated with `scripts/Verify-EarlyFlowTask.ps1 -Task 4`,
[preservation](evidence/067-audit-preservation.ps1), and
[trace accounting](evidence/067-audit-trial-trace.ps1).

The outcomes strengthen the feasibility evidence for inspectable discovery,
library test gates and composition: all agents found and reused the helpers,
kept their definitions/tests intact and committed correct abstractions. Own
coverage is not proof of correctness; the independent vectors provide separate
behavioral evidence. The compact dependency closure costs bytes, and these
agents still sought source for confidence in behavior. There is no byte-saving
signal supporting mandatory context-first retrieval on this fixture.

Keep both retrieval routes available. The next behavior-validation priority is
a bounded debugging/refactoring task, followed by stronger domain-type and
stateful tasks, rather than more optional syntax/runtime infrastructure. Four
actors on one small String/Float domain cannot establish the PRD's full
cost-reduction, strong business-type, domain-generalization or small-context
success criteria. Exact model usage remains unavailable. The full PRD goal
remains active.
Publication verification: clean [main CI](evidence/067-main-ci.json) for this
report/evidence milestone passed all 32 checks at `412f249`; the saved
[validation artifact](evidence/067-main-validation.json) records `dirty: false`.
