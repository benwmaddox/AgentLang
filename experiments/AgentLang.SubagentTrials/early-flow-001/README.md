# Early external-agent comparison

The first exploratory five-task comparison is complete: 15 external-agent
trials independently accepted. See [the purpose review](../../../reports/057-early-agent-purpose-review.md)
and [pilot artifacts](runs/pilot-001/inventory.json). Controlled repetitions and
efficiency/context conclusions remain pending. The fixture and acceptance
contract follow [the pilot design](../../../docs/EARLY-AGENT-EVALUATION.md).
`acceptance.json` contains 20 input vectors per task. Task 4's
expectations preserve the existing audited renewal oracle; other targets have
independent outputs for their specified rules. The recorded source-vector hash
identifies that input set. Do not put the oracle in agents' editable projects.

Flat and Growing both start from the existing frozen **Flat** schema-only
snapshot. Growing retains accepted results; Flat resets between tasks. The
existing preseeded Growing snapshot is unsuitable for this baseline.

`conventional` starts with equivalent records and unimplemented typed entry
points. It contains no premium/discount policy implementation. The ordinary
file-tool broker can run `EarlyPilot.fsproj` as its host-selected validation
target. Its initial self-test runner has no assertions: exit zero proves only
that the fixture executes, never task correctness. The external acceptance
adapter and its wrong-solution controls must be validated before actual trials.

`scripts/Verify-EarlyConventionalTask.ps1` builds a fresh external adapter with a
project reference to the edited conventional fixture. It invokes fixed typed
entry points on oracle inputs and compares returned outputs outside the
editable fixture. Agent self-tests and complete state audits remain separate.
`scripts/Verify-EarlyAgentNegativeControls.ps1` creates five wrong language
library words that pass their own tests and coverage, then requires rejection
at the independent behavioral check; setup errors cannot count as rejection.

`scripts/Verify-EarlyFlowTask.ps1` checks a language task in a fresh process,
including exact signature, pure persistent library maturity, own coverage,
attached tests and all 20 independent outputs. This is an acceptance tool, not
an agent runner or measurement of model tokens. Whole-state and helper audits
are additional requirements. Bool transport uses exact canonical rendered text
alongside stackTypes, avoiding truthiness conversion of the string "false".

Keep runtime/version pins, prompts, broker traces, final state and available
usage for each actual trial. Report unknown token counts explicitly. Do not
count root-authored verifier probes or replay as external-agent outcomes.
