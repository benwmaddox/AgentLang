# AST and IR behavior parity check

`scripts/Verify-IRParity.ps1` runs the same fixed JSONL scenarios against two
real CLI assemblies in separate synthetic projects. It records each request
and response, process result, timeout/output-cap status, explicit oracle check,
and selected-field parity comparison in JSON. It also records the CLI DLL,
sibling DLL, `.deps.json`, and `.runtimeconfig.json` SHA-256 hashes, the local
Git head, and whether the worktree was dirty. It does not collect model or
token metrics.

The default reference and candidate are both the local Release CLI. That mode
is a baseline self-comparison which checks the runner and fixtures only. It is
not evidence that either binary executes IR. For an AST-to-IR comparison, build
the candidate after the interpreter cutover and pass the pinned pre-cutover
CLI as `-ReferenceCliDll` and the new Release CLI as `-CandidateCliDll`:

```powershell
dotnet build AgentLang.sln -c Release
pwsh -NoProfile -File scripts/Verify-IRParity.ps1 `
  -ReferenceCliDll .agentlang/ir-parity/host-019d754/AgentLang.Cli.dll `
  -CandidateCliDll src/AgentLang.Cli/bin/Release/net9.0/AgentLang.Cli.dll
```

Use `-EvidencePath` to select the report path. `-TimeoutSeconds` and
`-MaxOutputMiB` bound each CLI process. The default report is
`.agentlang/reports/ir-parity.json`. Every run creates and retains unique
reference/candidate project directories beneath `.agentlang/ir-parity-*`; the
verifier does not delete them and refuses paths containing reparse points.

The reviewed fixture manifest and `.agent` files live in
`tests/fixtures/ir-parity`. Cases cover checked integer overflow and division
by zero, nonfinite float rejection, exact versus mismatched expected runtime
errors, typed locals and every if/Option/Result case, record and refined scalar
construction, strong nominal mismatches, static map/filter/each callbacks,
capability denial before an effectful empty-list callback, own-body library
instruction/branch coverage, metadata-only helper commit and fresh-process
reload, and task abort followed by a fresh-process absence check.

Each expected projection is an explicit semantic oracle. Cross-binary
comparison uses only those selected fields; IDs, task numbers, timestamps,
storage hashes, diagnostic prose, and the current `ir` display are not
globally normalized or treated as parity evidence. Revision is compared where
the fixture makes it part of the contract. Error code, word, expected/actual
values, and stable in-memory source span fields are asserted where provided.

This is a bounded CLI behavior check, not an OS sandbox test, a proof of
equivalence for all programs, or an IR-execution claim. The language exposes no
arbitrary .NET escape hatch; the script itself never evaluates language source
outside the fixed CLI JSONL protocol. Run the existing persistence projection
verifier separately for its detailed replacement-caller revision checks.
