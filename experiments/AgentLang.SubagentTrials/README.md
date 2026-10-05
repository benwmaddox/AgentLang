# External subagent trial artifacts

These are reviewed artifacts from the exploratory two-task vocabulary pilot,
not scripted AI responses or a controlled comparison. See
[the report](../../reports/004-subagent-vocabulary-pilot.md) for outcomes and
limits. AI agents are external clients; none of these files is a runtime
dependency.

Each task directory preserves initial/final `.agent` source, the supplied task
prompt, runtime interactions, agent notes, task log, and host acceptance
results. Task 1 also has the pinned runtime's binary hash manifest and trial
metadata. Task 2 reused a different earlier implementation agent after the
framework refused another fresh thread. Exact LLM context/tokens are unknown.

The verification scripts replay the independent product checks. Supply a
working trial directory containing `host/AgentLang.Cli.dll` and dependencies,
plus `growing-project/dictionary.agent` copied from the appropriate final
fixture. The recorded executions used the pinned validated Release build at
`6d9e341c402144f5d168566e119906e5495e3cb4`; compare binaries with the saved
manifest before treating another execution as that exact build. Scripts assume
that build's protocol and report that recorded revision; they do not infer a
binary's provenance from its filename.

```powershell
./experiments/AgentLang.SubagentTrials/task-01/verify-task-01.ps1 -TrialRoot <working-trial-directory>
./experiments/AgentLang.SubagentTrials/task-02/verify-task-02.ps1 -TrialRoot <working-trial-directory>
```

Scripts write new acceptance results into the supplied working directory.
They verify behavior, not reproducibility of stochastic model decisions.
Keep the versioned observed traces/results intact. No compiled binaries,
credentials, or private keys are included.
