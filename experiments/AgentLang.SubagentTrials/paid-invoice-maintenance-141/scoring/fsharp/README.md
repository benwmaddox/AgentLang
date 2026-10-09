# Maintenance 141 F# scorer and controls

`Program.fs` independently builds the frozen four stores from `oracle.json`,
calls `Store.customerPaidTotal`, `Customer.accountSummary`, and
`Store.customerMetrics`, then compares each exact result with the oracle. The
oracle supplies expectations; candidate output is never used to construct an
expected value. A clean scorer process returns one JSON response for every
case even when behavior misses the oracle.

Score any saved F# project with a fresh artifact tree and a unique evidence ID:

```powershell
.\Invoke-Maintenance141FSharpScore.ps1 `
  -CandidateProject 'D:\path\to\project\business\AgentLang.Business.fsproj' `
  -RunId 'participant-1'
```

Each run stores the exact build and scorer argument arrays, raw logs, compact
JSON score, and SHA-256 inputs under
`.agentlang/maintenance-141/fsharp-scoring/runs/<RunId>/`.

Run the baseline, correct, and plausible-incorrect controls from independent
copies of the frozen F# start, including the complete inherited suite on the
correct control:

```powershell
.\Invoke-Maintenance141FSharpControls.ps1
```

The control script creates isolated workspaces and artifact paths under
`.agentlang/maintenance-141/fsharp-scoring/workspaces/`. It writes the frozen
control matrix and source hashes to `.agentlang/maintenance-141/fsharp-scoring/preflight-controls.json`.
