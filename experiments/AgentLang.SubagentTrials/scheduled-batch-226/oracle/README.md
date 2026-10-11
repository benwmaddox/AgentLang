# Scheduled batch 226 oracle

`model.py` reuses the independent bounded Store model from report 163. Seven
frozen cases carry each renewal together with its own exact minute-offset
instant. The model checks correct execution and four mutations across every
case: sharing one instant, sorting by time, returning the last error, and
leaking a successful prefix.

Run `python -B experiments/AgentLang.SubagentTrials/scheduled-batch-226/oracle/model.py`
to regenerate `cases.json` and `model-checks.json`; run
`python -B experiments/AgentLang.SubagentTrials/scheduled-batch-226/oracle/unit_checks.py`
to verify the frozen exports and semantic record gate.

The disposable F# grader compiles only participant `Domain.fs` and
`Operations.fs`, then checks complete Store and input projections without
including participant self-tests:

```powershell
python -B experiments/AgentLang.SubagentTrials/scheduled-batch-226/oracle/fsharp/score_fsharp.py --participant <source-directory>
```

Raw build and execution logs are retained under
`.agentlang/scheduled-batch-226/oracle-work/fsharp/runs/`.

The retained-language scorer runs a disposable project copy through the fresh
CLI, evaluates all seven scheduled cases and all 66 inherited creator cases in
one JSONL process, and retains the raw exchange and project hashes:

```powershell
python -B experiments/AgentLang.SubagentTrials/scheduled-batch-226/oracle/score_language.py `
  --cli .agentlang/scheduled-batch-226/runtime/agentlang/AgentLang.Cli.dll `
  --actor-project .agentlang/scheduled-batch-226/actors/retained `
  --seed-project .agentlang/typed-batch-comparison-220/baselines/retained `
  --evidence-root .agentlang/scheduled-batch-226/oracle-work/language `
  --mode preflight
```

Optional calibrated AgentLang controls are passed with repeated
`--control NAME=PROJECT_PATH` arguments using the five model variant names.
Each scorer run also probes newline and semicolon record syntax plus a wrong
field type through the actual fresh CLI. Preservation compares every inherited
type, word, and revision manifest row and verifies the referenced definition,
test, and example objects remain byte-identical to their content hashes.
Use `--source-purpose calibration` when `--actor-project` is a calibration
control; this labels the aggregate run and its evidence directory accordingly.
