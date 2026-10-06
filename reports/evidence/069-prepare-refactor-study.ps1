#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$revision=(& git -C $repo rev-parse HEAD).Trim()
if($revision -ne '9c20fbae0500a326f6ee33e86a486991091e23aa'){throw 'Unexpected source pin.'}
$root=Join-Path $repo 'experiments/AgentLang.SubagentTrials/refactor-flow-001/runs/refactor-001'
$local=Join-Path $repo '.agentlang/refactor-flow-001'
$cli=Join-Path $local 'language-bin/AgentLang.Cli.dll'
$seed=Join-Path $local 'duplicate-seed'
$utf8=[Text.UTF8Encoding]::new($false)
function Save($Path,$Value){[IO.File]::WriteAllText($Path,($Value|ConvertTo-Json -Depth 100),$utf8)}
function Inventory($Path){@(Get-ChildItem $Path -File -Recurse -Force|Sort-Object FullName|ForEach-Object{[ordered]@{path=[IO.Path]::GetRelativePath($Path,$_.FullName);bytes=$_.Length;sha256=(Get-FileHash $_.FullName).Hash.ToLowerInvariant()}})}
if((Test-Path $root) -or (Test-Path $seed)){throw 'Refusing to overwrite experiment evidence.'}
New-Item -ItemType Directory -Path $root,$seed -Force|Out-Null
$original=Join-Path $repo 'experiments/AgentLang.SubagentTrials/debug-flow-001/runs/debug-001/final-project'
Get-ChildItem $original -Force|Copy-Item -Destination $seed -Recurse -Force
$helper=@'
word customer.discounted-balance(customer: Customer) -> Float {
    effects none
    let balance = customer::balance(customer);
    if ::equals(customer::kind(customer), "premium") {
        float::multiply(balance, 0.9)
    } else {
        balance
    }
}
'@
$caller=@'
word customer.renewal-balance(customer: Customer, subscription: Subscription) -> Float {
    effects none
    let balance = customer::balance(customer);
    let discounted = if ::equals(customer::kind(customer), "premium") {
        float::multiply(balance, 0.9)
    } else {
        balance
    };
    if subscription::annual-renewable?(subscription) {
        float::multiply(discounted, 0.95)
    } else {
        discounted
    }
}
'@
$requests=@(
    @{op='task.begin';goal='Host fixture setup: duplicate classification and discount calculation'},
    @{op='define';frontend='flow';replace=$true;expectedRevision=3;source=$helper},
    @{op='test';word='customer.discounted-balance'},
    @{op='replace-word';word='customer.discounted-balance';library=$true;actor='host'},
    @{op='define';frontend='flow';replace=$true;expectedRevision=2;source=$caller},
    @{op='test';word='customer.renewal-balance'},
    @{op='replace-word';word='customer.renewal-balance';library=$true;actor='host'},
    @{op='task.commit';actor='host'},
    @{op='test-all'},
    @{op='describe';word='customer.discounted-balance'},
    @{op='describe';word='customer.renewal-balance'},
    @{op='describe';word='customer.premium?'}
)
$lines=@($requests|ForEach-Object{$_|ConvertTo-Json -Compress})
$output=@($lines|& dotnet $cli --project $seed --clock '2000-01-01T00:00:00Z' --jsonl)
if($LASTEXITCODE -ne 0 -or $output.Count -ne $requests.Count){throw 'Setup process failed.'}
$responses=@($output|ForEach-Object{$_|ConvertFrom-Json -Depth 100})
Save (Join-Path $root 'fixture-setup.json') ([ordered]@{schemaVersion=1;sourceRevision=$revision;runtimeFiles=(Inventory (Split-Path $cli -Parent));originalSeedFiles=(Inventory $original);requests=$requests;responses=$responses})
if(@($responses|Where-Object{-not $_.ok}).Count){throw 'Setup diagnostic; see archived responses.'}
if(@($responses[8].data.results).Count -ne 21 -or @($responses[8].data.results|Where-Object{-not $_.passed}).Count){throw 'Existing tests failed.'}
foreach($index in @(9,10)){
    $word=$responses[$index].data
    if($word.dependencies -contains 'customer.premium?' -or $word.dependencies -contains 'customer.discounted-balance'){throw 'Duplication seed already uses desired composition.'}
    if($word.coverage.instructionsCovered -ne $word.coverage.instructionsTotal -or $word.coverage.branchesCovered -ne $word.coverage.branchesTotal){throw 'Seed own coverage incomplete.'}
}
$snapshot=Join-Path $root 'starting-project'
$project=Join-Path $local 'actor-01-refactor'
New-Item -ItemType Directory -Path $snapshot,$project -Force|Out-Null
Get-ChildItem $seed -Force|Copy-Item -Destination $snapshot -Recurse -Force
Get-ChildItem $snapshot -Force|Copy-Item -Destination $project -Recurse -Force
$starting=Inventory $snapshot
if(($starting|ConvertTo-Json -Depth 20 -Compress) -cne ((Inventory $project)|ConvertTo-Json -Depth 20 -Compress)){throw 'Actor copy differs.'}
Save (Join-Path $root 'starting-state.json') $starting
Write-Output 'Prepared duplicate seed with 21 passing tests and full own coverage; no actor launched.'
