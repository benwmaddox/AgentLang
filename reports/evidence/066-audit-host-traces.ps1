param(
    [Parameter(Mandatory)][string]$VerificationPath,
    [Parameter(Mandatory)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$verification = Get-Content -LiteralPath $VerificationPath -Raw | ConvertFrom-Json -Depth 64
$audits = @()
function Check($condition, $message) { if (-not $condition) { throw $message } }
function HashBytes([byte[]]$bytes) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant() }
foreach ($run in $verification.runs) {
    if (-not (Test-Path -LiteralPath $run.tracePath)) { continue }
    $events = @(Get-Content -LiteralPath $run.tracePath | ForEach-Object { $_ | ConvertFrom-Json -Depth 64 })
    if ($events[0].schemaVersion -ne 2) { continue }
    $selected = 0L; $raw = 0L; $admitted = 0L; $noninspection = 0L; $control = 0L
    $delivered = 0L; $deliveredWire = 0L; $deliveries = 0L; $selectionQueue = [Collections.Generic.Queue[object]]::new()
    foreach ($event in $events) {
        if ($event.event -eq 'exchange') {
            $wire = [Convert]::FromBase64String($event.response.wireBase64)
            Check ($wire.Length -eq $event.response.wireUtf8Bytes -and $wire[-1] -eq 10) 'Selected wire framing mismatch.'
            $payload = [byte[]]$wire[0..($wire.Length-2)]
            Check ($payload.Length -eq $event.response.payloadUtf8Bytes -and (HashBytes $wire) -eq $event.response.sha256 -and (HashBytes $payload) -eq $event.response.payloadSha256) 'Selected byte/hash mismatch.'
            $selected += $payload.Length
            if ($event.response.source -eq 'host') { $control += $payload.Length }
            $selectionQueue.Enqueue(@{payload=$payload.Length; wire=$wire.Length; hash=(HashBytes $payload)})
            $observed = $event.observedRuntimeResponse
            if ($null -ne $observed -and $observed.complete -and $observed.validJson) {
                $rawPayload = [Convert]::FromBase64String($observed.base64)
                $rawWire = [Convert]::FromBase64String($observed.wireBase64)
                Check ($rawPayload.Length -eq $observed.utf8Bytes -and (HashBytes $rawPayload) -eq $observed.sha256) 'Raw payload mismatch.'
                Check ($rawWire.Length -eq $rawPayload.Length+1 -and (HashBytes $rawWire) -eq $observed.wireSha256 -and $rawWire[-1] -eq 10) 'Raw wire mismatch.'
                $raw += $rawPayload.Length
                if ($event.inspectionBudget.operationClass -eq 'non-inspection') { $noninspection += $rawPayload.Length }
            }
            Check ($event.inspectionBudget.admittedBeforePayloadUtf8Bytes -eq $admitted) 'Admission prefix mismatch.'
            $admitted += $event.inspectionBudget.admittedPayloadUtf8Bytes
            Check ($event.inspectionBudget.admittedAfterPayloadUtf8Bytes -eq $admitted -and $admitted -le $event.inspectionBudget.maximumPayloadUtf8Bytes) 'Admission cap mismatch.'
        } elseif ($event.event -eq 'host-response-selected') {
            $wire = [Convert]::FromBase64String($event.selectedResponse.wireBase64)
            $payload = [byte[]]$wire[0..($wire.Length-2)]
            Check ($wire.Length -eq $event.selectedResponse.wireUtf8Bytes -and (HashBytes $wire) -eq $event.selectedResponse.wireSha256 -and (HashBytes $payload) -eq $event.selectedResponse.payloadSha256) 'Terminal control hash mismatch.'
            $selected += $payload.Length; $control += $payload.Length
            $selectionQueue.Enqueue(@{payload=$payload.Length; wire=$wire.Length; hash=(HashBytes $payload)})
        } elseif ($event.event -eq 'response-delivered') {
            Check ($selectionQueue.Count -gt 0 -and $event.pipeWriteFlushCompleted) 'Delivery without prior selection.'
            $prior = $selectionQueue.Dequeue()
            Check ($prior.payload -eq $event.selectedResponse.payloadUtf8Bytes -and $prior.wire -eq $event.selectedResponse.wireUtf8Bytes -and $prior.hash -eq $event.selectedResponse.payloadSha256) 'Delivery differs from selected response.'
            $deliveries++; $delivered += $prior.payload; $deliveredWire += $prior.wire
        }
        if ($null -ne $event.responseAccounting) {
            $a = $event.responseAccounting
            Check ($a.rawValidRuntimeResponsePayloadUtf8Bytes -eq $raw -and $a.selectedResponsePayloadUtf8Bytes -eq $selected -and $a.admittedInspectionPayloadUtf8Bytes -eq $admitted -and $a.nonInspectionRuntimeResponsePayloadUtf8Bytes -eq $noninspection -and $a.hostDenialControlResponsePayloadUtf8Bytes -eq $control -and $a.stdoutPipeDeliveredResponseCount -eq $deliveries -and $a.stdoutPipeDeliveredResponsePayloadUtf8Bytes -eq $delivered -and $a.stdoutPipeDeliveredResponseWireUtf8Bytes -eq $deliveredWire) 'Counter snapshot differs from trace-derived totals.'
        }
    }
    Check ($events[-1].event -eq 'session-end' -and $selectionQueue.Count -eq 0) 'Incomplete successful trace accounting.'
    Check ($run.stdoutUtf8Bytes -eq $deliveredWire) 'Delivery total differs from independently captured stdout bytes.'
    $audits += @{run=$run.name;traceSha256=(Get-FileHash -LiteralPath $run.tracePath -Algorithm SHA256).Hash.ToLowerInvariant();events=$events.Count;deliveries=$deliveries;rawValidBytes=$raw;selectedBytes=$selected;admittedBytes=$admitted;passed=$true}
}
Check ($audits.Count -gt 0) 'No schema-2 traces audited.'
@{schemaVersion=1;passed=$true;verificationSha256=(Get-FileHash -LiteralPath $VerificationPath -Algorithm SHA256).Hash.ToLowerInvariant();schema2Sessions=$audits.Count;runs=$audits;scope='Actual trace payload/wire hashes, selected-to-delivered order, all counter snapshots and complete-response admission; pipe delivery is not model consumption.'} | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Output "Audited $($audits.Count) schema-2 sessions."
