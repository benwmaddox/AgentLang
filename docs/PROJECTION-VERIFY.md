# Durable metadata projection verifier

`scripts/Verify-PersistenceProjection.ps1` runs the Release CLI as an independent JSON-lines client and checks which definitions and metadata survive a commit and a fresh process. It records every request, response, assertion, process exit, timeout, and output-limit result in JSON. The report also records SHA-256 hashes for the Release DLLs, the tested Git commit, and whether the working tree was dirty. It does not collect model or token metrics.

Run it after building the Release CLI:

```powershell
dotnet build AgentLang.sln -c Release
pwsh -NoProfile -File scripts/Verify-PersistenceProjection.ps1
```

The default CLI assembly is `src/AgentLang.Cli/bin/Release/net9.0/AgentLang.Cli.dll`. Use `-CliDll` to test another built CLI, or `-EvidencePath` to choose a report path. `-TimeoutSeconds` and `-MaxOutputMiB` bound each subprocess. The default report is `.agentlang/reports/projection-check.json`.

Each run creates a unique fixture beneath `.agentlang/projection-verification-*` and retains it for inspection; the verifier does not delete files. It refuses paths containing reparse points. The fixture is synthetic and isolated from the repository's source files.

The checks assert that a target commit includes candidate helpers and types referenced only by its attached tests or examples, while leaving unrelated candidates staged and absent from a fresh process. A second commit verifies that a replacement helper's exact next revision is used by the persisted target metadata. A library word must retain both passing branch tests and complete own-body instruction/branch coverage after reload. Finally, temporary helpers referenced by selected test metadata must be rejected before publication, leaving the fresh project empty.

This is a deterministic host-level persistence check, not an OS sandbox test or an agent benchmark. A passing result establishes these exact projection cases for the hashed CLI binary; it does not prove language correctness for all source graphs.
