#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('growing','flat','conventional')][string]$Mode,
    [Parameter(Mandatory)][ValidateSet('S01','S06','S07')][string]$TaskId
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$study = Join-Path $repo 'experiments/AgentLang.SubagentTrials/business-policy-001'
$localRoot = Join-Path $repo '.agentlang/business-policy-001'
$run = Join-Path $study "runs/$Mode-$TaskId"
$project = Join-Path $localRoot "actors/$Mode-$TaskId"
$starting = Join-Path $run 'starting-project'
$promptPath = Join-Path $run 'prompt.txt'
$pinPath = Join-Path $run 'prelaunch.json'
if (Test-Path -LiteralPath $pinPath) { throw 'Refusing to replace a frozen trial.' }
if (Test-Path -LiteralPath (Join-Path $run 'trace.jsonl')) { throw 'Refusing to freeze a previously launched trial.' }
foreach ($path in @($project,$starting,(Join-Path $run 'starting-state.json'))) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing prepared artifact: $path" }
}
function Inventory([string]$Path) {
    @(Get-ChildItem -LiteralPath $Path -File -Recurse -Force |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
        Sort-Object FullName | ForEach-Object {
            [ordered]@{ path=[IO.Path]::GetRelativePath($Path,$_.FullName).Replace('\','/'); bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
        })
}
$seedInventory = Inventory $starting
$actorInventory = Inventory $project
if (($seedInventory | ConvertTo-Json -Depth 30 -Compress) -cne ($actorInventory | ConvertTo-Json -Depth 30 -Compress)) {
    throw 'Actor files differ from the archived starting project.'
}
$preparedState = Get-Content -Raw -LiteralPath (Join-Path $run 'starting-state.json') | ConvertFrom-Json -Depth 100
if ($preparedState.mode -cne $Mode -or $preparedState.taskId -cne $TaskId -or $preparedState.project.path -cne $starting -or $preparedState.actor.projectPath -cne $project) { throw 'Prepared state does not match the requested actor.' }
foreach ($inventoryCheck in @(
    @{ actual=$seedInventory; recorded=$preparedState.project },
    @{ actual=$actorInventory; recorded=$preparedState.actor }
)) {
    $rows = @($inventoryCheck.actual | ForEach-Object { [pscustomobject][ordered]@{path=$_.path;sha256=$_.sha256} } | Sort-Object path)
    $canonical = ConvertTo-Json -InputObject $rows -Depth 100 -Compress
    $recorded = ConvertTo-Json -InputObject @($inventoryCheck.recorded.files) -Depth 100 -Compress
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical))).ToLowerInvariant()
    if ($canonical -cne $recorded -or $hash -cne $inventoryCheck.recorded.inventorySha256) { throw 'Prepared project inventory changed before freeze.' }
}
$sequence = @('S01','S06','S07')
$index = [array]::IndexOf($sequence,$TaskId)
if ($preparedState.sequenceIndex -ne $index+1) { throw 'Prepared sequence index is invalid.' }
$previous = $preparedState.previousAcceptance
if ($Mode -eq 'flat' -or $index -eq 0) {
    if ($null -ne $previous) { throw 'Reset or first-task state must not carry predecessor evidence.' }
} else {
    if ($null -eq $previous) { throw 'Retained state requires predecessor evidence.' }
    $previousPath = [IO.Path]::GetFullPath([string]$previous.acceptancePath,$repo)
    if ((Get-FileHash -LiteralPath $previousPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $previous.acceptanceSha256) { throw 'Predecessor acceptance changed before freeze.' }
    $previousResult = Get-Content -Raw -LiteralPath $previousPath | ConvertFrom-Json -Depth 100
    if ($previousResult.schemaVersion -ne 1 -or $previousResult.studyId -cne 'business-policy-001' -or $previousResult.mode -cne $Mode -or $previousResult.taskId -cne $sequence[$index-1] -or $previousResult.sequenceIndex -ne $index -or $previousResult.passed -ne $true) { throw 'Predecessor acceptance does not authorize this retained state.' }
    $previousTreeHash = [string]$previousResult.project.treeSha256
    if ([string]::IsNullOrWhiteSpace($previousTreeHash)) { $previousTreeHash = [string]$previousResult.outputTreeSha256 }
    if ([string]::IsNullOrWhiteSpace($previousTreeHash) -or $previousTreeHash -cne $preparedState.project.inventorySha256) { throw 'Retained input does not match the independently accepted predecessor output.' }
}
foreach ($runtimeCheck in @(
    @{ path=$preparedState.runtime.cliPath; hash=$preparedState.runtime.cliSha256 },
    @{ path=$preparedState.runtime.businessPath; hash=$preparedState.runtime.businessSha256 }
)) {
    $runtimePath = [IO.Path]::GetFullPath([string]$runtimeCheck.path,$repo)
    if ((Get-FileHash -LiteralPath $runtimePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $runtimeCheck.hash) { throw "Prepared runtime changed: $runtimePath" }
}
foreach ($sourceInput in $preparedState.sourceInputs) {
    $sourceInputPath = [IO.Path]::GetFullPath([string]$sourceInput.path,$repo)
    if ((Get-FileHash -LiteralPath $sourceInputPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $sourceInput.sha256) { throw "Prepared fixture source changed: $sourceInputPath" }
}
$publicTask = Get-Content -Raw -LiteralPath (Join-Path $repo "experiments/AgentLang.Benchmarks/task-bank/public/$TaskId.json") | ConvertFrom-Json
$sourceRevision = (& git -C $repo rev-parse HEAD | Out-String).Trim()
$dirty = -not [string]::IsNullOrWhiteSpace((& git -C $repo status --porcelain | Out-String))
$common = @'
You are an external coding agent in a disposable synthetic project. Inspect
available capabilities before editing. Prefer useful existing abstractions.
Add meaningful branch and boundary tests, concise documentation, and a runnable
example for the required reusable operation. Preserve unrelated definitions,
types and previous tests. Complete the task through the supplied protocol only.

Customer.Kind/kind is raw data: comparison is ordinal and case-sensitive. Do
not trim or lowercase it. Money is signed Int64 minor units; Float is not an
allowed implementation of money.

Human authorization covers these isolated project edits and local tests:
"Yes, run the remaining isolated experiments". Do not read coordinator source,
independent acceptance, sibling projects, archived prior solutions or raw
repository files. Do not use arbitrary shell commands or raw file tools. If
automatic approval rejects an action, report it and stop dependent work;
do not bypass it. You are not alone in the checkout; preserve other work.
Do not spawn or delegate. Observation timeout is not execution failure:
resume the same live handle and inspect state before repeating an uncertain edit.
LLM token usage, model turns and effective context windows are not measured.
'@
$moneyRule = if ($TaskId -eq 'S07') {
    'The returned premium balance is truncated toward zero after applying 90%; every signed Int64 value is valid, including negatives and both limits. Nonpremium returns the input unchanged.'
} else { '' }
if ($Mode -eq 'conventional') {
    $cli = Join-Path $localRoot 'broker-bin/AgentLang.Conventional.Cli.dll'
    $allowed = @('inspect','read','search','patch','replace','validate')
    $command = "& '$repo\scripts\Start-SubagentTrialHost.ps1' -CliDll '$cli' -ProjectPath '$project' -TracePath '$run\trace.jsonl' -AllowedOperations @('inspect','read','search','patch','replace','validate') -Profile conventional -AdditionalCliArguments @('--validation-project','BusinessPolicy.fsproj') -ExchangeTimeoutMilliseconds 120000 -MaxExchanges 100"
    $primer = Get-Content -Raw -LiteralPath (Join-Path $repo 'experiments/AgentLang.SubagentTrials/early-flow-002/conventional-primer.md')
    $ownership = 'You own only Operations.fs and SelfTests.fs. Domain.fs, Program.fs, BusinessPolicy.fsproj and lib files are immutable. Preserve runSeedTests; add assertions to runOwnTests and runnable examples to runExamples. Preserve earlier task functions and their tests. No F# instruction/branch coverage tooling is supplied.'
    $symbol = switch ($TaskId) { 'S01' {'CustomerPolicies.isPremium : Customer -> bool'} 'S06' {'CustomerPolicies.discountBasisPoints : Customer -> int64'} 'S07' {'CustomerPolicies.discountedBalance : Customer -> AgentLang.Business.Domain.Money'} }
} else {
    $cli = Join-Path $localRoot 'language-bin/AgentLang.Cli.dll'
    $allowed = @('words','describe','search','source','dependencies','callers','eval','define','test','test-all','tests','commit','replace-word','task.begin','task.commit','task.status','task.log','ir','context','failed-tests','examples','example','effects','type-of','search-type','search-output','history','diff','graph','transitive-dependencies','transitive-callers','search-dependency')
    $command = "& '$repo\scripts\Start-SubagentTrialHost.ps1' -CliDll '$cli' -ProjectPath '$project' -TracePath '$run\trace.jsonl' -AllowedOperations @('" + ($allowed -join "','") + "') -Profile agentlang -ExchangeTimeoutMilliseconds 120000 -MaxExchanges 100"
    $primer = Get-Content -Raw -LiteralPath (Join-Path $repo 'experiments/AgentLang.SubagentTrials/selective-flow-001/language-primer.md')
    $primer = $primer -replace '(?s)The same host operation allowlist and inspection-response byte cap apply to all\r?\nactors\..*?windows are not measured by this cap\.', 'No cumulative inspection-response byte cap is enabled. The 100-exchange host bound and per-exchange deadline apply. Observe the same live session across timeouts.'
    $ownership = 'You own staged definitions in this project through the protocol. Use Flow authoring. Every reusable authored helper must have passing own tests and full own instruction/branch coverage before library commit. End with durable task.commit. Preserve existing types, source and tests. All task operations are pure; the host grants no effects.'
    $symbol = [string]$publicTask.requiredPublicContract.symbol + ' : ' + [string]$publicTask.requiredPublicContract.signature
}
if (-not (Test-Path -LiteralPath $cli -PathType Leaf)) { throw "Missing pinned runtime: $cli" }
$prompt = @"
Run business-policy-001 $Mode-$TaskId as a fresh gpt-6-luna/max coding agent with no inherited turns.
Your assigned project is $project.
$ownership

$common

Task $TaskId
$($publicTask.goal)
$moneyRule
Required operation: $symbol
Customer fields are id/email/kind/balance/created-at (F# Id/Email/Kind/Balance/CreatedAt).
The Customer is a public record with strict nominal fields; direct construction preserves raw Kind. Construction helpers that trim Kind do not define this task's policy.

$primer

Execute exactly the following PowerShell command with exec_command, workdir $repo, sandbox_permissions require_escalated, tty true and a short initial yield. The known sandbox ACL helper requires this host execution for the authorized experiment. Do not issue other shell/file commands.

$command

No startup banner is printed. Send one JSONL request per line on the same write_stdin session. After successful completion, report the host session ID and leave it live until the coordinator completes independent acceptance. Then, when requested, tear down your own session using Ctrl+C through write_stdin without an extra JSONL request. Do not send quit commands or EOF. Report actual tests, errors and missing capabilities.
"@
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText($promptPath,$prompt.Replace("`r`n","`n"),$utf8)
$pin = [ordered]@{
    schemaVersion=1; studyId='business-policy-001'; mode=$Mode; taskId=$TaskId
    preparedAtUtc=[DateTimeOffset]::UtcNow.ToString('o'); sourceRevision=$sourceRevision; dirty=$dirty
    model='gpt-6-luna'; reasoningEffort='max'; forkTurns='none'; projectPath=$project
    launchCommand=$command; allowedOperations=$allowed; maxExchanges=100; exchangeTimeoutMilliseconds=120000
    profile=$(if ($Mode -eq 'conventional') { 'conventional' } else { 'agentlang' })
    additionalCliArguments=$(if ($Mode -eq 'conventional') { @('--validation-project','BusinessPolicy.fsproj') } else { @() })
    inspectionBudgetEnabled=$false; capabilities=@(); promptSha256=(Get-FileHash -LiteralPath $promptPath).Hash.ToLowerInvariant()
    promptUtf8Bytes=(Get-Item -LiteralPath $promptPath).Length
    oracleSha256=(Get-FileHash -LiteralPath (Join-Path $study 'acceptance.json')).Hash.ToLowerInvariant()
    independentVerifierSha256=(Get-FileHash -LiteralPath (Join-Path $repo 'scripts/Verify-BusinessPolicyTrial.ps1')).Hash.ToLowerInvariant()
    hostSha256=(Get-FileHash -LiteralPath (Join-Path $repo 'scripts/Start-SubagentTrialHost.ps1')).Hash.ToLowerInvariant()
    runtimeFiles=(Inventory (Split-Path -Parent $cli)); startingProjectFiles=$seedInventory
    startingStateSha256=(Get-FileHash -LiteralPath (Join-Path $run 'starting-state.json')).Hash.ToLowerInvariant()
}
[IO.File]::WriteAllText($pinPath,($pin | ConvertTo-Json -Depth 100)+"`n",$utf8)
Write-Output "Frozen $Mode-$TaskId without exposing host acceptance to the actor."
