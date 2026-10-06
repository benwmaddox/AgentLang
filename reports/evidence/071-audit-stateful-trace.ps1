#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$TracePath,[Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$run=Split-Path $TracePath -Parent
$pin=Get-Content (Join-Path $run 'prelaunch.json') -Raw|ConvertFrom-Json -Depth 100
$events=@(Get-Content $TracePath|ForEach-Object{$_|ConvertFrom-Json -Depth 100})
function Check($Condition,$Message){if(-not $Condition){throw $Message}}
function Hash([byte[]]$Bytes){[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()}
$meta=$events[0];$end=$events[-1]
Check ($meta.schemaVersion -eq 1 -and $meta.event -eq 'session-start' -and $end.event -eq 'session-end' -and $end.hostExitCode -eq 0 -and $end.runtimeExitCode -eq 0) 'Expected clean schema-1 session.'
Check ($meta.projectPath -ceq $pin.projectPath -and $meta.limits.exchangeTimeoutMilliseconds -eq $pin.exchangeTimeoutMilliseconds -and $meta.limits.maxExchanges -eq $pin.maxExchanges) 'Host configuration mismatch.'
Check (((@($meta.capabilities)|Sort-Object)-join '|') -ceq ((@($pin.capabilities)|Sort-Object)-join '|')) 'Capability policy mismatch.'
Check (((@($meta.allowedOperations)|Sort-Object)-join '|') -ceq ((@($pin.allowedOperations)|Sort-Object)-join '|')) 'Allowlist mismatch.'
Check ([DateTimeOffset]$pin.preparedAtUtc -le [DateTimeOffset]$meta.startedUtc) 'Provenance not prepared before launch.'
foreach($file in $meta.cliFiles){$expected=@($pin.runtimeFiles|Where-Object path -ceq $file.name);Check ($expected.Count -eq 1 -and $file.sha256 -ceq $expected[0].sha256) 'Runtime pin mismatch.'}
Check ((Get-FileHash (Join-Path $repo 'scripts/Start-SubagentTrialHost.ps1')).Hash.ToLowerInvariant() -ceq $pin.hostSha256) 'Host script pin mismatch.'
Check ((Get-FileHash (Join-Path $PSScriptRoot '071-verify-stateful-trial.ps1')).Hash.ToLowerInvariant() -ceq $pin.independentAcceptanceScriptSha256) 'Independent oracle changed after freeze.'
Check ((Get-FileHash (Join-Path $run 'starting-state.json')).Hash.ToLowerInvariant() -ceq $pin.startingStateSha256) 'Starting inventory changed after freeze.'
$prompt=Join-Path $run 'prompt.txt'
Check ((Get-FileHash $prompt).Hash.ToLowerInvariant() -ceq $pin.promptSha256 -and (Get-Item $prompt).Length -eq $pin.promptUtf8Bytes) 'Prompt pin mismatch.'
$requestBytes=0L;$responseBytes=0L;$errors=@();$requests=@()
$exchanges=@($events|Where-Object event -eq 'exchange')
foreach($event in $exchanges){
    foreach($frame in @($event.request,$event.response)){
        $wire=[Convert]::FromBase64String($frame.wireBase64)
        Check ($wire[-1] -eq 10 -and $wire.Length -eq $frame.wireUtf8Bytes -and $wire.Length-1 -eq $frame.payloadUtf8Bytes -and (Hash $wire) -ceq $frame.sha256) 'Frame length/hash mismatch.'
        Check ((Hash ([Text.Encoding]::UTF8.GetBytes($frame.rawLine+"`n"))) -ceq $frame.sha256) 'Decoded JSON does not match recorded wire bytes.'
    }
    $request=$event.request.rawLine|ConvertFrom-Json -Depth 100
    $response=$event.response.rawLine|ConvertFrom-Json -Depth 100
    Check ($request.op -cin $pin.allowedOperations) 'Request outside pinned allowlist.'
    $requests+=@{op=$request.op;word=$request.word;requestBytes=$event.request.payloadUtf8Bytes;responseBytes=$event.response.payloadUtf8Bytes;ok=$response.ok}
    $requestBytes+=$event.request.payloadUtf8Bytes;$responseBytes+=$event.response.payloadUtf8Bytes
    if(-not $response.ok){$errors+=@{index=$event.index;op=$request.op;error=$response.error}}
}
Check ($exchanges.Count -eq $end.exchangeCount) 'Exchange count mismatch.'
$result=@{
    schemaVersion=1;passed=$true;exchanges=$exchanges.Count;requestPayloadBytes=$requestBytes;selectedResponsePayloadBytes=$responseBytes
    errors=$errors;requests=$requests;traceSha256=(Get-FileHash $TracePath).Hash.ToLowerInvariant();sessionEnd=$end
    independentAcceptanceScriptSha256=$pin.independentAcceptanceScriptSha256;startingStateSha256=$pin.startingStateSha256
    limits=@('Schema 1 records selected responses before stdout flush; no delivery/model-consumption claim.','Payload counts exclude LF but include CR.','No exact model tokens, turns, controlled latency or window measurement.','One virtual-provider stateful smoke; no matched conventional comparison.')
}
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath,$repo),($result|ConvertTo-Json -Depth 100),[Text.UTF8Encoding]::new($false))
Write-Output "Audited $($exchanges.Count) exchanges and $($errors.Count) diagnostic responses."
