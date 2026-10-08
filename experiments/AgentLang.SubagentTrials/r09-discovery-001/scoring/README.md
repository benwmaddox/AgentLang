# R09 seed scorer

`score-seed.ps1` scores the seed-only `customer.paid-total` operation against
the frozen direct-result oracle. It requires the exact 13 accepted reference
fixtures and verifies that every embedded expectation matches `oracle.json`.
It also runs the candidate's attached `test-all` suite and reports the
function's runtime maturity separately from behavioral correctness.

The scorer copies the project into a retained attempt directory under
`.agentlang/r09-discovery-001/scorer-evidence/`. It first evaluates each full
Store literal with Flow/2 structured output and compares every typed field to
the reference projection, then invokes the candidate function for the same
cases. The reference fixture writes UTC instants with a `Z` suffix while the
Flow `Instant` refinement uses `+00:00`; the adapter changes only that suffix
when building a Flow literal and maps it back for projection comparison.
Money stays decimal text end to end, including Int64 boundary cases.

The Store is a value and the candidate must declare no effects. The scorer
records the unchanged reference Store projection, typed literal round-trip,
candidate effect declaration, and evaluation effects for each case. This
documents preservation without asking a `Result<Money, BusinessError>` return
value to carry an unrelated Store snapshot.

Example:

```powershell
pwsh experiments/AgentLang.SubagentTrials/r09-discovery-001/scoring/score-seed.ps1 `
  -CliDll .agentlang/r09-discovery-001/runtime-artifacts/bin/AgentLang.Cli/release/AgentLang.Cli.dll `
  -Project .agentlang/r09-discovery-001/base/runs/base-002/project `
  -ReferenceFixturesJson .agentlang/r09-discovery-001/reference-evidence/reference-fixtures.json `
  -Oracle experiments/AgentLang.SubagentTrials/r09-discovery-001/oracle.json `
  -OutputPath .agentlang/r09-discovery-001/scorer-evidence/score.json
```

A missing or invalid candidate function is rejected with exit code 2, even when
the base project's attached tests and all literal round-trips pass. Each run's
requests, stdout, stderr, process exit codes, source/runtime/input hashes, and
score report remain in its `attempt-*` directory. `scorer-error` is reserved
for setup, preflight, or evaluation harness exceptions; a candidate rejection
is never reported as a scorer error. Each JSONL process must exit cleanly and
return one parseable response per request before its responses are consumed;
any failed scorer check prevents an overall pass. Candidate `test-all` remains
a separate summary with an explicit `allPassed` field.
