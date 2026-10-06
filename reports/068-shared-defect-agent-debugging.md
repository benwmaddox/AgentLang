# Fresh-agent shared-defect debugging

2026-10-06. A fresh external Luna/max agent repaired a shared signed-balance
defect through runtime introspection and definition-level replacement. Both the
helper and its unchanged caller now pass independent acceptance. This is one
debugging feasibility outcome, with no conventional comparison or efficiency
claim.

## Fixture and prelaunch evidence

The [preparation script](evidence/068-prepare-debug-study.ps1) copies the accepted
selective-study actor 01 project into a new isolated fixture. It replaces
`customer.discounted-balance` through the ordinary Flow/type/effect/test/library
gates, preserving its identity and all original cases. The host-created revision
2 incorrectly returns zero for any negative balance. An added historical test
also incorrectly expects zero. No store integrity checks or commit gates are
bypassed.

The [setup transcript](../experiments/AgentLang.SubagentTrials/debug-flow-001/runs/debug-001/fixture-setup.json)
proves all 18 self-tests pass and the faulty helper covers 15/15 own instructions
and 4/4 branch outcomes. Independently frozen task-2 and task-4 vectors reject
the negative-balance results in both the
[helper](../experiments/AgentLang.SubagentTrials/debug-flow-001/runs/debug-001/before-task-2.json)
and [caller](../experiments/AgentLang.SubagentTrials/debug-flow-001/runs/debug-001/before-task-4.json).
These expected failures were verified before actor launch; parser, signature,
purity, persistence, self-tests and coverage checks pass first.

Fresh source `412f249` builds with zero warnings/errors. CLI SHA-256 is
`90c0672f68a019e3e7ae3a0707c2da4f40edb9719cc1f50dc310e78ce622e13e`;
Core is `8bb78d493484405499b886dbf15b2b09c348224872c796f7ce6abe6fb801950e`.
The [prelaunch record](../experiments/AgentLang.SubagentTrials/debug-flow-001/runs/debug-001/prelaunch.json)
pins runtime/host files, starting tree, complete prompt, model, no inherited
turns and protocol settings. The actor sees the
[public symptom and contract](../experiments/AgentLang.SubagentTrials/debug-flow-001/public-task.md),
not the injection source or independent oracle. It is instructed to inspect the
project through the supplied protocol only. The host allows 100 exchanges with a
120-second exchange deadline, no capabilities and a fixed clock. No cumulative
inspection cap is enabled; this is separate from the selective-retrieval study.
History/diff commands were not in this smoke's allowlist, so it does not evaluate
the complete history-driven debugging interface.

## Observed repair

The agent inspected the renewal dependency closure, helper/caller descriptions,
sources and helper tests. It removed the negative clamp from the shared helper,
corrected the same-named mistaken expectation to -90, added another negative
premium case, and attached premium/standard annual-credit regressions to the
renewal caller. No parallel workaround or new word was created.

The repaired helper keeps its stable ID and advances from revision 2 to 3. It
has 6/6 passing own tests, 10/10 instructions and 2/2 branch outcomes covered.
The renewal word's body is byte-identical; its attachment revision advances
from 1 to 2 with 8/8 passing tests and 10/10 instructions, 2/2 branch outcomes.
All 21 project self-tests pass. The two unrelated helpers and record definitions
are unchanged, with all historical revisions and valid old cases retained.

`replace-word` validated the selected helper and caller cases before publication.
An attempted separate `commit` for the already-persistent renewal word returned
`COMMIT_NOT_CANDIDATE`. The agent inspected status and completed the task without
coordinator repair hints. Fresh-process acceptance confirms the added caller
cases survived durable publication. The trace retains this protocol error;
it is not concealed by a final passing task.

Independent [helper acceptance](../experiments/AgentLang.SubagentTrials/debug-flow-001/runs/debug-001/after-task-2.json)
and [caller acceptance](../experiments/AgentLang.SubagentTrials/debug-flow-001/runs/debug-001/after-task-4.json)
pass 48 checks each: 40 behavioral vectors in total, plus signature, purity,
persistent library state, attached tests and current own coverage. The separate
[state audit](../experiments/AgentLang.SubagentTrials/debug-flow-001/runs/debug-001/preservation.json)
passes 111 checks, including stable identities, exact unchanged caller body,
type/unrelated-word preservation, unchanged valid tests, revised mistaken case,
new regressions and retained immutable history. Its
[script](evidence/068-audit-debug-state.ps1) compares current manifests and
hash-verified source objects, not just exported text.

The [raw-trace audit](../experiments/AgentLang.SubagentTrials/debug-flow-001/runs/debug-001/trace-audit.json)
verifies 25 exchanges, 2,295 request payload bytes, 27,517 selected response
payload bytes and one diagnostic. The
[audit script](evidence/068-audit-debug-trace.ps1) checks framing/hashes,
source/binary/prompt/configuration pins and the prelaunch ordering. This host's
schema 1 records selection before stdout flush; no delivery or model-consumption
claim follows. Exact model tokens, turns and controlled duration are unavailable.
The actor left its session open after completion; coordinator Ctrl+C teardown
on that same session ended host and runtime with exit code zero, without another
JSONL request.

## What this changes

The language workflow supports a fresh agent finding and fixing a shared cause,
updating an erroneous test, checking callers and publishing revisions without
rebuilding the F# runtime. This is observed interactive development behavior;
it does not establish the PRD's latency target or a speed advantage.

Full coverage and passing self-tests are useful publication gates, but they
cannot establish that expected behavior is correct. The initial fixture passed
both while violating the independent contract. Retain independent acceptance
and explicit domain rules when evaluating library quality. The corrected helper
also removes unnecessary branching rather than merely satisfying the planted
test.

Clean [main CI](evidence/067-main-ci.json) at `412f249` passed all 32 checks;
the [saved artifact](evidence/067-main-validation.json) records `dirty: false`.
This milestone changes experimental files, audit scripts and reports, with no
F# or host behavior change. It is not the full debugging/refactoring task suite,
strong business domain, controlled cost evaluation or 2k–32k context study. The
next short step is a fresh-agent refactoring task, followed by stronger nominal
domain-type and stateful behavior tasks. The full PRD goal remains active.
