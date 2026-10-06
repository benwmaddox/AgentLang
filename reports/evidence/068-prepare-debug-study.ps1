#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$revision=(& git -C $repo rev-parse HEAD).Trim()
if($revision -ne '412f249814ca21e7b7c014b6336ec5a258bf5f32'){throw 'Unexpected pinned source.'}
$root=Join-Path $repo 'experiments/AgentLang.SubagentTrials/debug-flow-001/runs/debug-001'
$local=Join-Path $repo '.agentlang/debug-flow-001'
$cli=Join-Path $local 'language-bin/AgentLang.Cli.dll'
$seed=Join-Path $local 'fault-seed'
$utf8=[Text.UTF8Encoding]::new($false,$true)
function Save($Path,$Value){[IO.File]::WriteAllText($Path,($Value|ConvertTo-Json -Depth 100),$utf8)}
function Inventory($Path){@(Get-ChildItem -LiteralPath $Path -Recurse -Force -File|Sort-Object FullName|ForEach-Object{[ordered]@{path=[IO.Path]::GetRelativePath($Path,$_.FullName);bytes=$_.Length;sha256=(Get-FileHash $_.FullName).Hash.ToLowerInvariant()}})}
if((Test-Path $root) -or (Test-Path $seed)){throw 'Refusing to overwrite experiment state.'}
New-Item -ItemType Directory -Path $root,$seed -Force|Out-Null
$original=Join-Path $repo 'experiments/AgentLang.SubagentTrials/selective-flow-001/runs/study-001/actor-01-full/final-project'
Get-ChildItem -LiteralPath $original -Force|Copy-Item -Destination $seed -Force -Recurse
$source=@'
word customer.discounted-balance(customer: Customer) -> Float {
    effects none
    let balance = customer::balance(customer);
    if float::less-than(balance, 0.0) {
        0.0
    } else {
        if customer::premium?(customer) {
            float::multiply(balance, 0.9)
        } else {
            balance
        }
    }
}
test customer.discounted-balance/negative-balance-clamped {
    customer::discounted-balance(customer::new("premium", -100.0)) => 0.0
}
'@
$requests=@(
    @{op='task.begin';goal='Host fixture setup: inject signed-balance defect'},
    @{op='define';frontend='flow';replace=$true;expectedRevision=1;source=$source},
    @{op='test';word='customer.discounted-balance'},
    @{op='replace-word';word='customer.discounted-balance';library=$true;actor='host'},
    @{op='task.commit';actor='host'},
    @{op='test-all'},
    @{op='describe';word='customer.discounted-balance'},
    @{op='describe';word='customer.renewal-balance'}
)
$lines=@($requests|ForEach-Object{$_|ConvertTo-Json -Compress})
$output=@($lines|& dotnet $cli --project $seed --clock '2000-01-01T00:00:00Z' --jsonl)
if($LASTEXITCODE -ne 0 -or $output.Count -ne $requests.Count){throw 'Fixture setup process failed.'}
$responses=@($output|ForEach-Object{$_|ConvertFrom-Json -Depth 100})
Save (Join-Path $root 'fixture-setup.json') ([ordered]@{schemaVersion=1;sourceRevision=$revision;requests=$requests;responses=$responses;runtimeFiles=(Inventory (Split-Path $cli -Parent));originalSeedFiles=(Inventory $original);injection='Shared discount helper clamps all negative balances; new historical self-test incorrectly expects zero. Existing cases remain unchanged.'})
if(@($responses|Where-Object{-not $_.ok}).Count -gt 0){throw 'Fixture setup returned a diagnostic; see archived responses.'}
$tests=@($responses[5].data.results)
if($tests.Count -ne 18 -or @($tests|Where-Object{-not $_.passed}).Count -gt 0){throw 'Injected fixture must pass all 18 self-tests.'}
$helper=$responses[6].data
if($helper.revision -ne 2 -or $helper.maturity -ne 'library' -or $helper.coverage.instructionsCovered -ne $helper.coverage.instructionsTotal -or $helper.coverage.branchesCovered -ne $helper.coverage.branchesTotal){throw 'Injected helper did not pass ordinary library gate.'}
$snapshot=Join-Path $root 'starting-project'
New-Item -ItemType Directory -Path $snapshot|Out-Null
Get-ChildItem -LiteralPath $seed -Force|Copy-Item -Destination $snapshot -Force -Recurse
$project=Join-Path $local 'actor-01-debug'
New-Item -ItemType Directory -Path $project|Out-Null
Get-ChildItem -LiteralPath $snapshot -Force|Copy-Item -Destination $project -Force -Recurse
$starting=Inventory $snapshot
if(($starting|ConvertTo-Json -Depth 20 -Compress) -cne ((Inventory $project)|ConvertTo-Json -Depth 20 -Compress)){throw 'Actor starting copy differs.'}
Save (Join-Path $root 'starting-state.json') $starting
Write-Output "Fault fixture ready: 18 passing self-tests, helper revision 2, full own coverage. No actor launched."
