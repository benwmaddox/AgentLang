#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$run = Join-Path $repo 'experiments/AgentLang.SubagentTrials/stateful-conventional-001/runs/conventional-001'
$project = Join-Path $repo '.agentlang/stateful-conventional-001/actor-01'
$cli = Join-Path $repo '.agentlang/stateful-conventional-001/broker-bin/AgentLang.Conventional.Cli.dll'
$revision = (& git -C $repo rev-parse HEAD).Trim()
if ($revision -cne '20388a3faa781f8318af3c503a794108e8e63776') { throw 'Unexpected source pin.' }
if (Test-Path (Join-Path $run 'prelaunch.json')) { throw 'Refusing to overwrite prelaunch evidence.' }
$allowed = @('inspect','read','search','patch','replace','validate')
$command = "& '$repo\scripts\Start-SubagentTrialHost.ps1' -CliDll '$cli' -ProjectPath '$project' -TracePath '$run\trace.jsonl' -AllowedOperations @('inspect','read','search','patch','replace','validate') -Profile conventional -AdditionalCliArguments @('--validation-project','StatefulPilot.fsproj') -ExchangeTimeoutMilliseconds 120000 -MaxExchanges 100"
$task = Get-Content (Join-Path $repo 'experiments/AgentLang.SubagentTrials/stateful-conventional-001/public-task.md') -Raw
$primer = Get-Content (Join-Path $repo 'experiments/AgentLang.SubagentTrials/early-flow-002/conventional-primer.md') -Raw
$prompt = @"
Run conventional-001 as a fresh GPT-6 Luna/max coding agent with no inherited turns.
Your ownership is only Operations.fs and SelfTests.fs in $project through the supplied broker. You are not alone; preserve other agents' files. Domain.fs and StatefulPilot.fsproj must remain byte-identical. Preserve runSeedTests exactly; add your tests through runOwnTests. Inspect existing capabilities before editing, reuse useful abstractions, and preserve seeded behavior.

Human authorization covers isolated trial-project edits and local tests: "Yes, run the remaining isolated experiments". If automatic approval rejects an operation, report the rejection and stop dependent work; do not bypass it. No arbitrary shell commands, raw file tools, coordinator files, independent oracles, sibling runs or previous solutions. Do not spawn or delegate.

$task

$primer

This task uses an explicit injected virtual-file provider. Exists and Read each count as fs.read; Write counts as fs.write. Snapshot state is a Map exposed by the fixture provider. Execution.run is a host-style preflight wrapper; retain it and do not implement a new capability policy. F# self-tests use the conventional console runner; no instruction-coverage tooling is supplied. Add the required meaningful test categories, plus a documented runnable example. Do not invent coverage or model-token measurements.

Execute exactly this PowerShell command using exec_command with workdir $repo, sandbox_permissions require_escalated, tty true, short initial yield. The known sandbox ACL helper requires host execution; this is the authorized local isolated experiment.

$command

No startup banner is printed. Send one JSONL request per line on the same write_stdin session. An observation timeout is not failure: resume the same handle without replaying an uncertain request. Leave the host open when finished and report its session ID for coordinator teardown. Do not send quit commands, EOF or control characters. Report actual validation outcomes, errors and missing capabilities.
"@
$utf8 = [Text.UTF8Encoding]::new($false)
$promptPath = Join-Path $run 'prompt.txt'
[IO.File]::WriteAllText($promptPath, $prompt.Replace("`r`n","`n"), $utf8)
function Inventory([string]$Path) {
    @(Get-ChildItem -LiteralPath $Path -File -Recurse -Force | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } | Sort-Object FullName | ForEach-Object {
        [ordered]@{path=[IO.Path]::GetRelativePath($Path,$_.FullName);bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}
    })
}
$seed = Join-Path $run 'starting-project'
if ((Inventory $seed | ConvertTo-Json -Depth 20 -Compress) -cne (Inventory $project | ConvertTo-Json -Depth 20 -Compress)) { throw 'Actor differs from frozen seed.' }
$pin = [ordered]@{
    schemaVersion=1;preparedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');sourceRevision=$revision
    model='gpt-6-luna';reasoningEffort='max';forkTurns='none';projectPath=$project
    launchCommand=$command;allowedOperations=$allowed;exchangeTimeoutMilliseconds=120000;maxExchanges=100
    inspectionBudgetEnabled=$false;capabilities=@();profile='conventional'
    runtimeFiles=(Inventory (Split-Path $cli -Parent))
    hostSha256=(Get-FileHash (Join-Path $repo 'scripts/Start-SubagentTrialHost.ps1')).Hash.ToLowerInvariant()
    promptSha256=(Get-FileHash $promptPath).Hash.ToLowerInvariant();promptUtf8Bytes=(Get-Item $promptPath).Length
    startingStateSha256=(Get-FileHash (Join-Path $run 'starting-state.json')).Hash.ToLowerInvariant()
    independentAcceptanceScriptSha256=(Get-FileHash (Join-Path $PSScriptRoot '072-verify-conventional-stateful.ps1')).Hash.ToLowerInvariant()
}
[IO.File]::WriteAllText((Join-Path $run 'prelaunch.json'), ($pin | ConvertTo-Json -Depth 100), $utf8)
Write-Output 'Frozen conventional stateful actor prompt, seed, broker and independent oracle.'
