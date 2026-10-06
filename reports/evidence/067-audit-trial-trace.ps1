#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$TracePath,[Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference='Stop'
$events=@(Get-Content -LiteralPath $TracePath | ForEach-Object { $_ | ConvertFrom-Json -Depth 100 })
function Check($Condition,$Message){if(-not $Condition){throw $Message}}
function Hash([byte[]]$Bytes){[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()}
$raw=0L;$selected=0L;$admitted=0L;$noninspection=0L;$control=0L;$delivered=0L;$wireDelivered=0L;$deliveries=0L;$rawInspection=0L;$suppressed=0L
$queue=[Collections.Generic.Queue[object]]::new()
$exchanges=@($events | Where-Object event -eq 'exchange')
Check ($events[0].schemaVersion -eq 2) 'Expected budgeted schema 2.'
foreach($event in $events){
    if($event.event -eq 'exchange'){
        $wire=[Convert]::FromBase64String($event.response.wireBase64)
        $payload=[byte[]]$wire[0..($wire.Length-2)]
        Check ($wire[-1] -eq 10 -and $wire.Length -eq $event.response.wireUtf8Bytes -and $payload.Length -eq $event.response.payloadUtf8Bytes -and (Hash $wire) -ceq $event.response.sha256 -and (Hash $payload) -ceq $event.response.payloadSha256) 'Selected response frame/hash mismatch.'
        $selected+=$payload.Length
        if($event.response.source -eq 'host'){$control+=$payload.Length}
        $queue.Enqueue(@{payload=$payload.Length;wire=$wire.Length;hash=(Hash $payload)})
        $observed=$event.observedRuntimeResponse
        if($null -ne $observed -and $observed.complete -and $observed.validJson){
            $rawPayload=[Convert]::FromBase64String($observed.base64)
            $rawWire=[Convert]::FromBase64String($observed.wireBase64)
            Check ($rawPayload.Length -eq $observed.utf8Bytes -and (Hash $rawPayload) -ceq $observed.sha256 -and $rawWire.Length -eq $rawPayload.Length+1 -and $rawWire[-1] -eq 10 -and (Hash $rawWire) -ceq $observed.wireSha256) 'Observed response frame/hash mismatch.'
            $null=[Text.UTF8Encoding]::new($false,$true).GetString($rawPayload) | ConvertFrom-Json
            $raw+=$rawPayload.Length
            if($event.inspectionBudget.operationClass -eq 'non-inspection'){$noninspection+=$rawPayload.Length}else{$rawInspection+=$rawPayload.Length}
            if($event.inspectionBudget.decision -eq 'withheld-after-response'){$suppressed+=$rawPayload.Length}
        }
        Check ($event.inspectionBudget.admittedBeforePayloadUtf8Bytes -eq $admitted) 'Admission prefix mismatch.'
        $admitted+=$event.inspectionBudget.admittedPayloadUtf8Bytes
        Check ($event.inspectionBudget.admittedAfterPayloadUtf8Bytes -eq $admitted -and $admitted -le 16000) 'Admission exceeds frozen cap.'
    }elseif($event.event -eq 'response-delivered'){
        Check ($queue.Count -gt 0 -and $event.pipeWriteFlushCompleted) 'Delivery without selection.'
        $prior=$queue.Dequeue()
        Check ($prior.payload -eq $event.selectedResponse.payloadUtf8Bytes -and $prior.wire -eq $event.selectedResponse.wireUtf8Bytes -and $prior.hash -ceq $event.selectedResponse.payloadSha256) 'Delivery differs from selection.'
        $deliveries++;$delivered+=$prior.payload;$wireDelivered+=$prior.wire
    }elseif($event.event -eq 'host-response-selected'){throw 'Unexpected terminal control; retain trace and extend audit explicitly.'}
    if($null -ne $event.responseAccounting){
        $a=$event.responseAccounting
        Check ($a.rawValidRuntimeResponsePayloadUtf8Bytes -eq $raw -and $a.selectedResponsePayloadUtf8Bytes -eq $selected -and $a.admittedInspectionPayloadUtf8Bytes -eq $admitted -and $a.nonInspectionRuntimeResponsePayloadUtf8Bytes -eq $noninspection -and $a.hostDenialControlResponsePayloadUtf8Bytes -eq $control -and $a.stdoutPipeDeliveredResponseCount -eq $deliveries -and $a.stdoutPipeDeliveredResponsePayloadUtf8Bytes -eq $delivered -and $a.stdoutPipeDeliveredResponseWireUtf8Bytes -eq $wireDelivered) 'Counter snapshot mismatch.'
    }
}
Check ($events[-1].event -eq 'session-end' -and $queue.Count -eq 0) 'Session not fully closed.'
$errorResponses=@($exchanges | Where-Object { -not ($_.response.rawLine | ConvertFrom-Json).ok })
$evidence=@{schemaVersion=1;passed=$true;traceSha256=(Get-FileHash -LiteralPath $TracePath).Hash.ToLowerInvariant();exchanges=$exchanges.Count;errorResponses=$errorResponses.Count;operations=@($exchanges | ForEach-Object {($_.request.rawLine | ConvertFrom-Json).op});rawInspectionPayloadBytes=$rawInspection;admittedInspectionPayloadBytes=$admitted;suppressedInspectionPayloadBytes=$suppressed;rawValidRuntimeResponsePayloadBytes=$raw;nonInspectionRuntimeResponsePayloadBytes=$noninspection;selectedResponsePayloadBytes=$selected;hostControlPayloadBytes=$control;pipeDeliveredResponses=$deliveries;pipeDeliveredPayloadBytes=$delivered;pipeDeliveredWireBytes=$wireDelivered;sessionEnd=$events[-1];limits=@('Trace-derived pipe-delivery audit; no independent capture of actor stdout.','Pipe delivery is not model consumption.','No model tokens, turns, controlled timing or context-window measurement.')}
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath),($evidence | ConvertTo-Json -Depth 100),[Text.UTF8Encoding]::new($false))
Write-Output "Audited $($exchanges.Count) exchanges; admitted $admitted inspection bytes."
