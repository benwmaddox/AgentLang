# Typed reference maintenance oracle draft

This directory contains the draft independent model and its 18 complete-state
vectors. Expected values are derived from the draft contract in
`.agentlang/efficacy-plan-184/task-contract.md`; the model does not load or call
participant code. Vectors cover single-event and batch ingestion, exact scoped
replay behavior, validation precedence, error identity, ordering, and unchanged
Store fields.

The draft check command is:

```powershell
python experiments/AgentLang.SubagentTrials/typed-reference-maintenance-184/oracle/model.py
```

It regenerates `cases.json` and writes model-only checks to
`.agentlang/efficacy-maintenance-184/oracle-draft/model-checks.json`, with a
matching stdout receipt. This is not runtime calibration and does not authorize
a freeze or participant run.
Once the seed and scorer are aligned, use the following commands against saved
local projects and the fresh Release CLI built from the accepted revision:

```powershell
python experiments/AgentLang.SubagentTrials/typed-reference-maintenance-184/oracle/score_reference.py --arm flow --project <captured-project> --cli <fresh-cli-dll> --label <unique-label>
python experiments/AgentLang.SubagentTrials/typed-reference-maintenance-184/oracle/score_reference.py --arm fsharp --project <captured-project> --label <unique-label>
```

The scorer reports behavior, field type metadata, helper signature, type-negative
controls, inherited evidence preservation, qualification, finalization, and
recovery separately. Flow field/helper signatures come from existing type-of
and context discovery operations; Flow negative controls preserve the eval
rejection diagnostics and are not described as a separate compiler invocation.
F# records public field-type metadata and uses fresh compile-positive and
compile-negative probes for the helper and field assignments. Reflection is
only used to construct and project a one-field string union or record; it is not
treated as type-identity evidence. Internal wiring from ingest and
ingest-batch to the helper remains captured source evidence and is not inferred
from matching behavior.

Local control calibration should use disposable isolated copies after the 183
acceptance gate. Do not dispatch participants or freeze/publish this draft from
the model check alone. Preservation, qualification, finalization, and recovery
must be recorded from their separate captured run evidence.

The Flow scorer validates the wrapper definition on a disposable project copy,
then defines it again in the same CLI process that evaluates all 18 vectors;
uncommitted definitions do not carry into later CLI processes. This preflight
keeps a failed wrapper definition from being mistaken for behavioral evidence.
When Flow discovery finds both the retained `find-scan` wrapper and the shared
`scan-lookup-step` callback, it scores the wrapper; the callback is scored only
for the reset-rich callback-only shape.
