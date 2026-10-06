#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$run=Join-Path $repo 'experiments/AgentLang.SubagentTrials/stateful-flow-001/runs/stateful-001'
$project=Join-Path $repo '.agentlang/stateful-flow-001/actor-01-reminder'
$cli=Join-Path $repo '.agentlang/stateful-flow-001/language-bin/AgentLang.Cli.dll'
$revision=(& git -C $repo rev-parse HEAD).Trim()
if($revision -ne 'cb1ebf6e3b5c4da48b1a2eedfae369bcc6261fcb'){throw 'Unexpected source pin.'}
if(Test-Path (Join-Path $run 'prelaunch.json')){throw 'Refusing to overwrite prelaunch evidence.'}
$allowed=@('words','describe','search','source','dependencies','callers','eval','define','test','test-all','tests','commit','replace-word','task.begin','task.commit','task.status','task.log','ir','context','failed-tests','examples','example','effects','type-of','search-type','search-output','history','diff','graph','transitive-dependencies','transitive-callers','search-dependency','snapshot.save','snapshot.load')
$command="& '$repo\scripts\Start-SubagentTrialHost.ps1' -CliDll '$cli' -ProjectPath '$project' -TracePath '$run\trace.jsonl' -AllowedOperations @('"+($allowed -join "','")+"') -Capabilities @('fs.read','fs.write') -Profile agentlang -ExchangeTimeoutMilliseconds 120000 -MaxExchanges 100"
$common=Get-Content (Join-Path $repo 'experiments/AgentLang.SubagentTrials/early-flow-002/common-instructions.md') -Raw
$common=$common.Replace("Float arithmetic is`r`nthe specified demo behavior, not a decimal billing implementation.", 'This is deterministic virtual-filesystem simulation, not actual host files or email delivery.')
$task=Get-Content (Join-Path $repo 'experiments/AgentLang.SubagentTrials/stateful-flow-001/public-task.md') -Raw
$primer=Get-Content (Join-Path $repo 'experiments/AgentLang.SubagentTrials/selective-flow-001/language-primer.md') -Raw
$primer=$primer -replace '(?s)The same host operation allowlist and inspection-response byte cap apply to all\r?\nactors\..*?measured by this cap\.', 'No cumulative inspection-response cap is enabled. Never automatically repeat an uncertain request; observe the same live session and inspect state first. The configured exchange count and deadline still apply. No model tokens or context windows are measured.'
$primer=$primer.Replace('These tasks require pure', 'This task requires').Replace('retained library words', 'retained library words with declared effects')
$prompt=@"
Run stateful-flow-001 stateful-001 as a fresh GPT-6 Luna/max coding agent with no inherited turns.
Your ownership is only $project through its supplied protocol. You are not alone in the checkout; preserve other agents' files. Do not touch repository source or sibling projects.

$common

$task

$primer

Effects are declared on their own line, for example 'effects fs.read fs.write'. Each test/example starts with its own empty virtual filesystem. You can seed a marker using file::write(path, text); as a statement before the tested call. Those fixtures do not alter live eval state. Dictionary reload preserves definitions; use named snapshot.save/snapshot.load to preserve live virtual provider state across processes. Snapshot commands take name:<snapshot name>.

Documentation is a line inside the word header: doc "A concise description.". An example uses the same owner/case and expectation grammar as a test, with 'example' instead of 'test'. Test or example effects require the granted capabilities. This host grants only fs.read and fs.write, using the virtual provider.

Execute exactly this PowerShell command using exec_command, workdir $repo, sandbox_permissions require_escalated, tty true, short initial yield. The known sandbox ACL helper requires host execution. Human authorization covers these isolated edits and local tests. Do not use shell/file tools for any other command or source access.

$command

Send one JSONL request per line on that same write_stdin session. Do not spawn other agents. Leave the host open at completion and report its session ID for coordinator teardown. No quit commands or control characters. Report tests, errors and missing capabilities; do not invent model-token or turn counts.
"@
$utf8=[Text.UTF8Encoding]::new($false)
$prompt=$prompt.Replace("`r`n","`n")
$promptPath=Join-Path $run 'prompt.txt'
[IO.File]::WriteAllText($promptPath,$prompt,$utf8)
function Inventory($Path){@(Get-ChildItem $Path -File -Recurse -Force|Sort-Object FullName|ForEach-Object{[ordered]@{path=[IO.Path]::GetRelativePath($Path,$_.FullName);bytes=$_.Length;sha256=(Get-FileHash $_.FullName).Hash.ToLowerInvariant()}})}
$seed=Join-Path $run 'starting-project'
if((Inventory $seed|ConvertTo-Json -Depth 20 -Compress) -cne (Inventory $project|ConvertTo-Json -Depth 20 -Compress)){throw 'Actor copy differs from frozen seed.'}
$pin=[ordered]@{
    schemaVersion=1;preparedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');sourceRevision=$revision
    model='gpt-6-luna';reasoningEffort='max';forkTurns='none';projectPath=$project
    launchCommand=$command;allowedOperations=$allowed;exchangeTimeoutMilliseconds=120000;maxExchanges=100
    inspectionBudgetEnabled=$false;capabilities=@('fs.read','fs.write');clockValue='2000-01-01T00:00:00Z'
    runtimeFiles=(Inventory (Split-Path $cli -Parent))
    hostSha256=(Get-FileHash (Join-Path $repo 'scripts/Start-SubagentTrialHost.ps1')).Hash.ToLowerInvariant()
    promptSha256=(Get-FileHash $promptPath).Hash.ToLowerInvariant();promptUtf8Bytes=(Get-Item $promptPath).Length
    startingStateSha256=(Get-FileHash (Join-Path $run 'starting-state.json')).Hash.ToLowerInvariant()
    independentAcceptanceScriptSha256=(Get-FileHash (Join-Path $PSScriptRoot '071-verify-stateful-trial.ps1')).Hash.ToLowerInvariant()
}
[IO.File]::WriteAllText((Join-Path $run 'prelaunch.json'),($pin|ConvertTo-Json -Depth 100),$utf8)
Write-Output 'Frozen stateful trial prompt, provider policy, seed and independent oracle before launch.'
