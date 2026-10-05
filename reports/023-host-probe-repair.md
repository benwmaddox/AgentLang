# Partial-response host probe repair

Status: the focused host verifier passed 17 checks and fresh full Release validation passed all 23 required checks on 2026-10-05. Hosted CI remains the publication gate.

The earlier partial-response probe used a 400 ms exchange timeout and emitted only the 11-byte prefix `{"partial":`. The saved local trace records a 413.1464 ms exchange with confirmed request delivery and uncertain execution. CI run 37337744955 failed only this verifier assertion, but its artifacts omitted the exchange JSONL trace. The failed assertion’s specific conjunct therefore remains unknown; elapsed time alone does not establish the cause.

The verifier now uses a finite 4,000 ms exchange deadline. Its synthetic runtime flushes `{"partial":`, then emits eight one-space chunks 650 ms apart, and finally writes the closing brace and line feed after about 5.2 seconds. The probe requires multiple chunks to appear in the captured incomplete response, exit 124 with `TRIAL_EXCHANGE_TIMEOUT`, confirmed request delivery, uncertain execution, and no automatic retry. It checks the host trace’s `elapsedMilliseconds` against a broad 3,000–6,500 ms bound around the configured deadline; the outer process duration is reported separately and does not determine the deadline assertion. A timeout renewed by each arriving chunk would receive the completed response after 5.2 seconds and fail this check.

The focused run recorded a 4,013.1576 ms exchange and a 6,278.376 ms subprocess duration. The trace captured 17 response bytes, `{"partial":      `, before the host returned exit 124. Request delivery was confirmed, the response was incomplete, execution was uncertain, and retry was forbidden. The full JSONL trace is referenced in `.agentlang/reports/023-host-repair-focused.json`; the verifier now also includes bounded response and trace-event entries in both passing evidence and failure summaries. Failure summaries cap each run at 12 response and 12 trace entries, with serialized entries limited to 4,096 and 8,192 characters respectively. Full synthetic JSONL traces are uploaded by the validation workflow.

The verifier changes are confined to `scripts/Verify-SubagentTrialHost.ps1`; the validation workflow also uploads the full synthetic traces. The production host implementation was not changed. The focused probe demonstrates the intended behavior locally; it does not recover the missing trace from the prior CI run or prove which assertion conjunct failed there.

Command:

```powershell
pwsh -NoProfile -File scripts/Verify-SubagentTrialHost.ps1 -CliDll src/AgentLang.Cli/bin/Release/net9.0/AgentLang.Cli.dll -EvidencePath .agentlang/reports/023-host-repair-focused.json
```

Result: 17 checks passed, 0 failed (`Verification passed: 17 checks.`). The changed PowerShell script passes `git diff --check`.


Full validation: `pwsh -NoProfile -File scripts/Validate.ps1 -Configuration Release -ReportPath .agentlang/reports/023-host-repair-validation.json` passed all 23 checks, including a fresh solution build with zero warnings/errors and 17 host checks. Committed evidence is saved in `reports/evidence/023-host-repair-*`. The failed prior CI run and unchanged local reproduction are preserved alongside it; neither is overwritten by this successful run.

## Committed CI evidence

CI run [37341736314](https://github.com/benwmaddox/AgentLang/actions/runs/37341736314)
completed successfully for repair revision
146bece225a7e9ca54a4d6e73804df7786e15ea2. Its clean checkout passed all 23
required checks, including 17 host checks. The exact run identity, validation
report and bounded host evidence are saved in `reports/evidence/023-ci-run.json`,
`023-committed-ci-validation.json` and `023-committed-ci-subagent-host.json`.
This closes the failed publication gate for the implementation; complete Flow
semantic coverage, durable integration and default cutover remain required.
## Main publication

The foundation and repair were fast-forwarded and pushed to main at
d3f6e8ebcd0bce0efdcbf7f6a6441eddcedda31e. Publication CI run
[37342562608](https://github.com/benwmaddox/AgentLang/actions/runs/37342562608)
completed successfully on that exact revision with a clean checkout and all
23 required checks passing. Its identity and validation report are saved as
`reports/evidence/023-published-main-ci-run.json` and
`023-published-main-ci-validation.json`. Work now continues on the required
Flow semantic surface; this milestone does not complete the PRD.