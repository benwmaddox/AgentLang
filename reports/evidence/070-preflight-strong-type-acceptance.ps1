#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$run=Join-Path $repo 'experiments/AgentLang.SubagentTrials/strong-type-flow-001/runs/units-001'
$seed=Join-Path $run 'starting-project'
$copy=Join-Path $repo '.agentlang/strong-type-flow-001/acceptance-preflight'
$cli=Join-Path $repo '.agentlang/strong-type-flow-001/language-bin/AgentLang.Cli.dll'
if(Test-Path $copy){throw 'Refusing to overwrite preflight project.'}
New-Item -ItemType Directory -Path $copy -Force|Out-Null
Get-ChildItem $seed -Force|Copy-Item -Destination $copy -Recurse -Force
$source=@'
word delivery.speed-kph(delivery: Delivery) -> KilometersPerHour {
    effects none
    KilometersPerHour::new(float::multiply(MetersPerSecond::value(delivery::speed(delivery)), 3.6))
}
test delivery.speed-kph/zero {
    delivery::speed-kph(delivery::new(contact = Email::new("a@example.com"), speed = MetersPerSecond::new(0.0)))
    => value KilometersPerHour::new(0.0)
}
test delivery.speed-kph/unit {
    delivery::speed-kph(delivery::new(contact = Email::new("a@example.com"), speed = MetersPerSecond::new(1.0)))
    => value KilometersPerHour::new(3.6)
}
test delivery.speed-kph/fraction {
    delivery::speed-kph(delivery::new(contact = Email::new("a@example.com"), speed = MetersPerSecond::new(2.5)))
    => value KilometersPerHour::new(9.0)
}
test delivery.speed-kph/negative {
    delivery::speed-kph(delivery::new(contact = Email::new("a@example.com"), speed = MetersPerSecond::new(-2.0)))
    => value KilometersPerHour::new(-7.2)
}
'@
$requests=@(
    @{op='task.begin';goal='Coordinator acceptance preflight; not an agent outcome'},
    @{op='define';frontend='flow';source=$source},
    @{op='test';word='delivery.speed-kph'},
    @{op='commit';word='delivery.speed-kph';library=$true;actor='host'},
    @{op='task.commit';actor='host'}
)
$lines=@($requests|ForEach-Object{$_|ConvertTo-Json -Depth 20 -Compress})
$output=@($lines|& dotnet $cli --project $copy --jsonl)
if($LASTEXITCODE -ne 0 -or $output.Count -ne $requests.Count){throw 'Preflight setup process failed.'}
$responses=@($output|ForEach-Object{$_|ConvertFrom-Json -Depth 100})
[IO.File]::WriteAllText((Join-Path $run 'acceptance-preflight-setup.json'),(@{requests=$requests;responses=$responses;scope='Coordinator-created disposable control only; never supplied to the actor.'}|ConvertTo-Json -Depth 100),[Text.UTF8Encoding]::new($false))
if(@($responses|Where-Object{-not $_.ok}).Count){throw 'Preflight setup diagnostic; inspect archived evidence.'}
& (Join-Path $PSScriptRoot '070-verify-strong-type-trial.ps1') -ProjectPath $copy -OutputPath (Join-Path $run 'acceptance-preflight.json')
if($LASTEXITCODE -ne 0){throw 'Acceptance preflight failed.'}
