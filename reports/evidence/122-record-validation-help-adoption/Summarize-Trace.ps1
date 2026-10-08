$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$frames = @(Get-Content -LiteralPath "$root/runtime/actor.trace.jsonl" | ForEach-Object { $_ | ConvertFrom-Json -Depth 100 })
$exchanges = @($frames | Where-Object event -EQ exchange)
$end = @($frames | Where-Object event -EQ 'session-end')
if ($end.Count -ne 1) { throw 'Wait for exactly one terminal session event.' }
$details = @($exchanges | ForEach-Object {
    $request = $_.request.canonical | ConvertFrom-Json -Depth 100
    $response = $_.response.canonical | ConvertFrom-Json -Depth 100
    [pscustomobject][ordered]@{ index=$_.index; operation=$_.operation; atUtc=$_.atUtc; elapsedMilliseconds=$_.elapsedMilliseconds; requestBytes=$_.request.payloadUtf8Bytes; responseBytes=$_.response.payloadUtf8Bytes; responseOk=$response.ok; responseKind=$response.kind; errorCode=$response.error.code; errorMessage=$response.error.message; word=$request.word; topic=$request.topic; requestedSyntaxVersion=$request.syntaxVersion; returnedSyntaxVersion=$response.data.syntaxVersion }
})
$summary = [ordered]@{
    source='Exact broker JSONL payloads; bytes are not model tokens.'
    exchangeCount=$exchanges.Count
    operations=@($exchanges | Group-Object operation | Sort-Object Name | ForEach-Object { [ordered]@{ name=$_.Name; count=$_.Count } })
    requestPayloadBytes=($details | Measure-Object requestBytes -Sum).Sum
    responsePayloadBytes=($details | Measure-Object responseBytes -Sum).Sum
    protocolErrors=@($details | Where-Object responseOk -EQ $false)
    help=@($details | Where-Object operation -EQ help)
    exchanges=$details
    terminalEvents=@($frames | Where-Object { $_.event -in @('host-close','session-end') })
    modelUsage=$null
    modelUsageUnavailableReason='The subagent interface supplied no provider token counts.'
}
[IO.File]::WriteAllText("$root/trace-summary.json",($summary | ConvertTo-Json -Depth 100),[Text.UTF8Encoding]::new($false))
[pscustomobject]$summary | Select-Object exchangeCount,requestPayloadBytes,responsePayloadBytes | ConvertTo-Json
