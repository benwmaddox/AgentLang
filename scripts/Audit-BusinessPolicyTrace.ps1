#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$TracePath,
    [Parameter(Mandatory)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$trace = [IO.Path]::GetFullPath($TracePath,$repo)
$run = Split-Path -Parent $trace
$pin = Get-Content -Raw -LiteralPath (Join-Path $run 'prelaunch.json') | ConvertFrom-Json -Depth 100
$events = @(Get-Content -LiteralPath $trace | ForEach-Object { ConvertFrom-Json -InputObject $_ -Depth 100 })
$checks = [Collections.Generic.List[object]]::new()
function Check([bool]$Passed,[string]$Name) {
    $checks.Add([ordered]@{ name=$Name;passed=$Passed })
    if (-not $Passed) { throw "Trace audit failed: $Name" }
}
function HashBytes([byte[]]$Bytes) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant() }
function HashFile([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
Check ($events.Count -ge 2) 'trace has start and terminal events'
$start = $events[0]
$end = $events[-1]
Check ($start.event -ceq 'session-start' -and $start.schemaVersion -eq 1) 'schema-1 unbudgeted session'
$cancelEvents = @($events | Where-Object { $_.event -ceq 'host-cancelled' })
$completeExchanges = @($events | Where-Object { $_.event -ceq 'exchange' })
$idleCancellation = $end.hostExitCode -eq 130 -and $cancelEvents.Count -eq 1 -and
    $cancelEvents[0].exchangeCount -eq $completeExchanges.Count -and
    @($completeExchanges | Where-Object { $_.executionState -ceq 'uncertain' -or $_.outcome -ceq 'cancelled' }).Count -eq 0 -and
    $events[-2].event -ceq 'host-cancelled'
Check ($end.event -ceq 'session-end' -and ($end.hostExitCode -eq 0 -or $idleCancellation) -and $end.runtimeExitCode -eq 0) 'runtime exited cleanly; host ended normally or was cancelled between complete exchanges'
Check ($start.projectPath -ceq $pin.projectPath -and $start.profile -ceq $pin.profile) 'actor project and profile match freeze'
Check ($start.limits.maxExchanges -eq $pin.maxExchanges -and $start.limits.exchangeTimeoutMilliseconds -eq $pin.exchangeTimeoutMilliseconds) 'exchange limits match freeze'
Check ((@($start.allowedOperations | Sort-Object) -join '|') -ceq (@($pin.allowedOperations | Sort-Object) -join '|')) 'allowlist matches freeze'
Check ((@($start.capabilities | Sort-Object) -join '|') -ceq (@($pin.capabilities | Sort-Object) -join '|')) 'capabilities match freeze'
Check ((@($start.additionalCliArguments) -join '|') -ceq (@($pin.additionalCliArguments) -join '|')) 'fixed validation target matches freeze'
Check ([DateTimeOffset]$pin.preparedAtUtc -le [DateTimeOffset]$start.startedUtc) 'freeze predates launch'
foreach ($file in $start.cliFiles) {
    $expected = @($pin.runtimeFiles | Where-Object { $_.path -ceq $file.name })
    Check ($expected.Count -eq 1 -and $expected[0].sha256 -ceq $file.sha256) "runtime file pin: $($file.name)"
}
Check ((HashFile (Join-Path $repo 'scripts/Start-SubagentTrialHost.ps1')) -ceq $pin.hostSha256) 'host unchanged after freeze'
Check ((HashFile (Join-Path $repo 'scripts/Verify-BusinessPolicyTrial.ps1')) -ceq $pin.independentVerifierSha256) 'verifier unchanged after freeze'
Check ((HashFile (Join-Path $repo 'experiments/AgentLang.SubagentTrials/business-policy-001/acceptance.json')) -ceq $pin.oracleSha256) 'oracle unchanged after freeze'
Check ((HashFile (Join-Path $run 'starting-state.json')) -ceq $pin.startingStateSha256) 'starting inventory unchanged after freeze'
$prompt = Join-Path $run 'prompt.txt'
Check ((HashFile $prompt) -ceq $pin.promptSha256 -and (Get-Item -LiteralPath $prompt).Length -eq $pin.promptUtf8Bytes) 'public prompt unchanged after freeze'
$requestBytes = 0L
$responseBytes = 0L
$requests = [Collections.Generic.List[object]]::new()
$diagnostics = [Collections.Generic.List[object]]::new()
$testBatches = [Collections.Generic.List[object]]::new()
$sourceModifications = 0
$exchanges = @($events | Where-Object { $_.event -ceq 'exchange' })
foreach ($exchange in $exchanges) {
    foreach ($frame in @($exchange.request,$exchange.response)) {
        $wire = [Convert]::FromBase64String($frame.wireBase64)
        Check ($wire.Length -gt 0 -and $wire[-1] -eq 10 -and $wire.Length -eq $frame.wireUtf8Bytes -and $wire.Length-1 -eq $frame.payloadUtf8Bytes -and (HashBytes $wire) -ceq $frame.sha256) "frame bytes/hash exchange $($exchange.index)"
        Check ((HashBytes ([Text.Encoding]::UTF8.GetBytes($frame.rawLine+"`n"))) -ceq $frame.sha256) "decoded frame exchange $($exchange.index)"
    }
    $request = ConvertFrom-Json -InputObject $exchange.request.rawLine -Depth 100
    $response = ConvertFrom-Json -InputObject $exchange.response.rawLine -Depth 100
    Check ($request.op -cin $pin.allowedOperations) "request allowlist exchange $($exchange.index)"
    $requestBytes += $exchange.request.payloadUtf8Bytes
    $responseBytes += $exchange.response.payloadUtf8Bytes
    $requests.Add([ordered]@{ index=$exchange.index;op=$request.op;requestBytes=$exchange.request.payloadUtf8Bytes;responseBytes=$exchange.response.payloadUtf8Bytes;ok=$response.ok })
    if (-not $response.ok) { $diagnostics.Add([ordered]@{index=$exchange.index;op=$request.op;error=$response.error}) }
    if ($response.ok -and $request.op -cin @('define','replace-word','patch','replace')) { $sourceModifications++ }
    if ($request.op -cin @('test','test-all') -and $null -ne $response.data.results) {
        $results = @($response.data.results)
        $testBatches.Add([pscustomobject][ordered]@{index=$exchange.index;op=$request.op;tests=$results.Count;failed=@($results | Where-Object {-not $_.passed}).Count})
    }
}
Check ($exchanges.Count -eq $end.exchangeCount) 'terminal exchange count matches frames'
$target = [IO.Path]::GetFullPath($OutputPath,$repo)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
$report = [ordered]@{
    schemaVersion=1;passed=$true;studyId=$pin.studyId;mode=$pin.mode;taskId=$pin.taskId
    checks=@($checks);exchanges=$exchanges.Count;requestPayloadBytes=$requestBytes;selectedResponsePayloadBytes=$responseBytes
    diagnostics=@($diagnostics);requests=@($requests);traceSha256=(HashFile $trace);sessionEnd=$end
    successfulSourceMutationRequests=$sourceModifications;testBatches=@($testBatches)
    testsRun=$(if($pin.profile -ceq 'conventional'){$null}else{@($testBatches | Measure-Object -Property tests -Sum)[0].Sum})
    testsFailed=$(if($pin.profile -ceq 'conventional'){$null}else{@($testBatches | Measure-Object -Property failed -Sum)[0].Sum})
    testCountStatus=$(if($pin.profile -ceq 'conventional'){'unavailable: broker validation does not enumerate individual F# assertions'}else{'counted runtime test results; repeated test batches count again'})
    auditSha256=(HashFile $PSCommandPath)
    protocolDurationMilliseconds=([DateTimeOffset]$end.finishedUtc-[DateTimeOffset]$start.startedUtc).TotalMilliseconds
    activeProtocolSpanMilliseconds=$(if($exchanges.Count -gt 0){([DateTimeOffset]$exchanges[-1].atUtc-[DateTimeOffset]$start.startedUtc).TotalMilliseconds}else{$null})
    oracleSha256=$pin.oracleSha256;verifierSha256=$pin.independentVerifierSha256
    limits=@('Schema1 counts selected response frames before stdout flush; it does not prove model consumption.','Protocol duration includes coordinator acceptance/teardown wait; active span ends at the last exchange. Both exclude setup before host start.','Trials run concurrently; repository validation overlaps the final language trials. Controlled latency is unavailable.','Exact LLM tokens, model turns and effective context are unavailable.','One exploratory task sequence; no efficiency conclusion.')
}
[IO.File]::WriteAllText($target,($report | ConvertTo-Json -Depth 100)+"`n",[Text.UTF8Encoding]::new($false))
Write-Output "Audited $($pin.mode)-$($pin.taskId): $($exchanges.Count) exchanges, $($diagnostics.Count) diagnostics."
