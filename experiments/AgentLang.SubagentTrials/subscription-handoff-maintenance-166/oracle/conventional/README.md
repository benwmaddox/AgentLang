# Conventional handoff scorer

This scorer reuses the frozen 33 fixtures and independent model from
`subscription-handoff-161/oracle`. It evaluates all 33 cases with `dryRun=false`
and `dryRun=true`, and separately invokes the retained five-argument
`renewAnnual` and `renewMonthly` callers against term-adjusted versions of those
same fixtures. The fixture report keeps the 26 common valid-reference cases and
7 adversarial orphan-reference cases separate.

The control suite includes a correct implementation and two dry-run faults:
skipping replacement-creation validation, and returning the modified store on
success. The control project is independent from the participant build. For a
participant run, the supplied saved project or `Operations.fs` is copied as-is
into a fresh disposable scorer project alongside the staged `Domain.fs` and
`AgentLang.Business.dll`. Its `SelfTests.fs` are excluded from the grader build.

From the repository root, run the scorer after the shared .NET artifacts are
ready:

```powershell
python experiments/AgentLang.SubagentTrials/subscription-handoff-maintenance-166/oracle/conventional/score_conventional.py
python experiments/AgentLang.SubagentTrials/subscription-handoff-maintenance-166/oracle/conventional/score_conventional.py --participant .agentlang/subscription-handoff-maintenance-166/actors/conventional
```

Use `--seed-directory`, `--cases`, and `--output-directory` to override the
staged inputs or ignored evidence destination. Each run creates a new evidence
directory under `.agentlang/subscription-handoff-maintenance-166/control/conventional`.
