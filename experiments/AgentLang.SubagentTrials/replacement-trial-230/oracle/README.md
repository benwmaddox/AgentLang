# Replacement trial 230 oracle

The independent model retains the seven scheduled-renewal cases from 228 and
adds two direct monthly-helper cases: successful renewal with distinct `At` and
`ExpiresAt`, and the exact expiry error when those instants are equal. Each
scorer compares the full canonical Store projection and verifies input
preservation. The timing-swap control must match its predicted divergences.

Run the frozen model, type checks, and source-preservation controls from the
repository root:

```powershell
python -B experiments/AgentLang.SubagentTrials/replacement-trial-230/oracle/model.py
python -B experiments/AgentLang.SubagentTrials/replacement-trial-230/oracle/unit_checks.py
python -B experiments/AgentLang.SubagentTrials/replacement-trial-230/oracle/fsharp/preservation_checks.py
```

Run the prepared AgentLang calibration, including all nine model cases, the 66
inherited cases, preservation, and both behavior controls:

```powershell
python -B experiments/AgentLang.SubagentTrials/replacement-trial-230/oracle/score_language.py --cli .agentlang/replacement-trial-230/runtime/agentlang/AgentLang.Cli.dll --actor-project .agentlang/replacement-trial-230/calibration/ok/l --seed-project .agentlang/replacement-trial-230/baselines/retained --evidence-root .agentlang/replacement-trial-230/oracle-work/l --source-purpose calibration --control correct=.agentlang/replacement-trial-230/calibration/ok/l --control swapped-timing=.agentlang/replacement-trial-230/calibration/swap/l --control-source swapped-timing=.agentlang/replacement-trial-230/calibration/swap/swap-request.json
```

Run the F# behavior and preservation controls independently:

```powershell
python -B experiments/AgentLang.SubagentTrials/replacement-trial-230/oracle/fsharp/score_fsharp.py --participant .agentlang/replacement-trial-230/calibration/correct --control-variant correct --evidence-root .agentlang/replacement-trial-230/oracle-work/f
python -B experiments/AgentLang.SubagentTrials/replacement-trial-230/oracle/fsharp/score_fsharp.py --participant .agentlang/replacement-trial-230/calibration/swapped-timing --control-variant swapped-timing --evidence-root .agentlang/replacement-trial-230/oracle-work/f
```

For the AgentLang arm, `score_language.py` runs nine independent model cases,
the inherited 66-case organic-reuse regression, a library qualification gate,
and the scoped manifest/source preservation audit. The 66 cases are separate
from the nine cross-language cases. The preservation audit requires stable IDs
for the three migrated words, unchanged historical revision rows and referenced
objects, no new persisted word rows, only the `RenewalTiming` type, normalized
inherited monthly call shapes, and at least one added monthly test with a direct
grouped call and an inspected result. Added regression names and count are not
fixed. A disposable weakened-assertion control confirms the gate detects
changed expectations.

For the F# arm, `score_fsharp.py` builds an independent grader with only
`Domain.fs` and `Operations.fs`; it excludes `SelfTests.fs`. Its separate
lexical preservation audit requires the four other baseline files to remain
byte-identical, restricts operations changes to `RenewalTiming`, `renewMonthly`,
and the monthly batch branch, and preserves inherited assertions including
their exact quoted literals. It permits a direct grouped-time regression
anywhere in `SelfTests.run`, with timing supplied inline or by a local binding;
the record literal may use either field order, and the result may be checked by
ordinary assertion or match styles. The audit does not compile or execute
participant source. `preservation_checks.py` tests positive and negative source
controls, including these style variations.

The F# timing-swap candidate keeps to the scoped source-change allowance and
must pass the preservation audit while its nine-case behavior score detects the
predicted wrong outcomes. This verifies that behavior and source-preservation
checks report separate properties. A normal participant candidate and both
calibration controls must pass the preservation gate.

The F# build explicitly enables NuGet auditing. Calibration currently records
`NU1900` because the vulnerability service index is unreachable; a successful
build is not reported as a dependency-audit pass. Raw warnings, invocations,
scoring rows, and receipts remain under
`.agentlang/replacement-trial-230/oracle-work/{l,f}/`.

The trial report must disclose that F# routes both batch paths through
`renewBatchStep`, while AgentLang has separate common and scheduled step
callers. One matched pair is a feasibility observation and cannot establish
reliability, causality, or cost advantage. The trial makes no claim of
operating-system network confinement.
