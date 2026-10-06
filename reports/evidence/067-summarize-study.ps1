#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$root=Join-Path $repo 'experiments/AgentLang.SubagentTrials/selective-flow-001/runs/study-001'
$seed=Get-Content (Join-Path $repo 'experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/growing-task-4/starting-state.json') -Raw | ConvertFrom-Json
$rows=@();$previousEnd=$null
function Check($Condition,$Message){if(-not $Condition){throw $Message}}
function Canonical($Value){ConvertTo-Json -InputObject $Value -Compress -Depth 100}
foreach($actor in @('actor-01-full','actor-02-compact','actor-03-compact','actor-04-full')){
    $folder=Join-Path $root $actor
    $pin=Get-Content (Join-Path $folder 'prelaunch.json') -Raw | ConvertFrom-Json
    $start=Get-Content (Join-Path $folder 'starting-state.json') -Raw | ConvertFrom-Json
    Check ((Canonical @($start|Sort-Object path)) -ceq (Canonical @($seed|Sort-Object path))) 'Archived starting state differs from frozen seed.'
    $prompt=Join-Path $folder 'prompt.txt'
    Check ((Get-FileHash $prompt).Hash.ToLowerInvariant() -ceq $pin.promptSha256 -and (Get-Item $prompt).Length -eq $pin.promptUtf8Bytes) 'Prompt pin mismatch.'
    Check ($pin.sourceRevision -ceq '94a2d7f409eb7463654013e6a6bb3981ee0293a9' -and $pin.model -ceq 'gpt-6-luna' -and $pin.reasoningEffort -ceq 'max' -and $pin.forkTurns -ceq 'none') 'Source or model treatment changed.'
    $events=@(Get-Content (Join-Path $folder 'trace.jsonl') | ForEach-Object {$_|ConvertFrom-Json -Depth 100})
    $meta=$events[0];$end=$events[-1]
    Check ($meta.event -eq 'session-start' -and $end.event -eq 'session-end' -and $end.hostExitCode -eq 0 -and $end.runtimeExitCode -eq 0) 'Host not cleanly closed.'
    Check ($meta.projectPath -ceq $pin.projectPath -and $meta.inspectionBudget.maximumPayloadUtf8Bytes -eq 16000 -and $meta.limits.exchangeTimeoutMilliseconds -eq 120000 -and $meta.limits.maxExchanges -eq 100 -and @($meta.capabilities).Count -eq 0) 'Host configuration changed.'
    Check (((@($meta.allowedOperations)|Sort-Object) -join '|') -ceq ((@($pin.allowedOperations)|Sort-Object) -join '|')) 'Operation allowlist changed.'
    foreach($file in $meta.cliFiles){$expected=@($pin.runtimeFiles|Where-Object path -ceq $file.name);Check ($expected.Count -eq 1 -and $expected[0].sha256 -ceq $file.sha256) 'Runtime pin changed.'}
    Check ((Get-FileHash (Join-Path $repo 'scripts/Start-SubagentTrialHost.ps1')).Hash.ToLowerInvariant() -ceq $pin.hostSha256) 'Host source pin changed.'
    Check ([DateTimeOffset]$pin.preparedAtUtc -le [DateTimeOffset]$meta.startedUtc) 'Prompt provenance not prepared before launch.'
    if($null -ne $previousEnd){Check ([DateTimeOffset]$meta.startedUtc -ge $previousEnd) 'Trials overlapped.'}
    $previousEnd=[DateTimeOffset]$end.finishedUtc
    $acceptance=Get-Content (Join-Path $folder 'acceptance.json') -Raw | ConvertFrom-Json -Depth 100
    $preservation=Get-Content (Join-Path $folder 'preservation.json') -Raw | ConvertFrom-Json -Depth 100
    $trace=Get-Content (Join-Path $folder 'trace-audit.json') -Raw | ConvertFrom-Json -Depth 100
    Check ($acceptance.passed -and $acceptance.checks.Count -eq 48 -and $preservation.passed -and $preservation.checks.Count -eq 41 -and $trace.passed) 'Outcome audit failed.'
    $word=$acceptance.responses[1].data
    Check (((@($word.dependencies)|Sort-Object) -join '|') -ceq 'customer.discounted-balance|float.multiply|subscription.annual-renewable?') 'Expected direct helper reuse missing.'
    $rows+=@{actor=$actor;guidance=$pin.assignedGuidance;accepted=$true;exchanges=$trace.exchanges;inspectionBytes=$trace.admittedInspectionPayloadBytes;responseBytes=$trace.selectedResponsePayloadBytes;requestBytes=(@($events|Where-Object event -eq 'exchange'|ForEach-Object{$_.request.payloadUtf8Bytes})|Measure-Object -Sum).Sum;errors=$trace.errorResponses;selfTests=$word.testCount;ownCoverage=$word.coverage;dependencies=$word.dependencies;suppressedInspectionBytes=$trace.suppressedInspectionPayloadBytes;hostControlBytes=$trace.hostControlPayloadBytes;traceSha256=$trace.traceSha256}
}
$arms=@(foreach($guidance in @('full','compact')){$arm=@($rows|Where-Object guidance -eq $guidance);@{guidance=$guidance;actors=$arm.Count;exchanges=($arm.exchanges|Measure-Object -Sum).Sum;inspectionBytes=($arm.inspectionBytes|Measure-Object -Sum).Sum;responseBytes=($arm.responseBytes|Measure-Object -Sum).Sum;requestBytes=($arm.requestBytes|Measure-Object -Sum).Sum;errors=($arm.errors|Measure-Object -Sum).Sum}})
$evidence=@{schemaVersion=1;passed=$true;sourceRevision='94a2d7f409eb7463654013e6a6bb3981ee0293a9';actors=$rows;arms=$arms;acceptedTasks=4;independentBehavioralVectors=80;acceptanceChecks=192;preservationChecks=164;scope='Four serial fresh Luna/max actors, identical seed/runtime/host/allowlist/inspection cap, ABBA guidance order. Fallbacks retained under assigned guidance. Protocol traffic only, no model usage or statistical generalization.'}
[IO.File]::WriteAllText((Join-Path $root 'summary.json'),($evidence|ConvertTo-Json -Depth 100),[Text.UTF8Encoding]::new($false))
$arms|ConvertTo-Json -Depth 10
