param([Parameter(Mandatory = $true)][string]$ReceiptPath)
$ErrorActionPreference = 'Stop'
$taskHandler = [System.Net.Http.SocketsHttpHandler]::new()
$taskHandler.AllowAutoRedirect = $false
$taskHandler.UseCookies = $false
$taskHandler.UseProxy = $false
$taskClient = [System.Net.Http.HttpClient]::new($taskHandler)
$taskClient.Timeout = [TimeSpan]::FromSeconds(10)
$taskRows = @()
$taskCases = @(
    @{name='normal-missing-auth'; expected=@(302); cookie=''},
    @{name='wrong-host-port'; expected=@(400,404); cookie=''; host='127.0.0.1:1'},
    @{name='selected-cookie-value-limit'; expected=@(400); cookie=('session_token=' + ('a' * 12289))},
    @{name='total-cookie-header-limit'; expected=@(400,431); cookie=('session_token=' + ('a' * 16400))},
    @{name='clean-reuse-after-rejections'; expected=@(302); cookie=''}
)
try {
    foreach ($taskCase in $taskCases) {
        $taskRequest = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, 'http://127.0.0.1:13773/rooms/104')
        if ($taskCase.ContainsKey('host')) { $taskRequest.Headers.Host = $taskCase.host }
        if ($taskCase.cookie.Length) { $null = $taskRequest.Headers.TryAddWithoutValidation('Cookie', $taskCase.cookie) }
        $taskResponse = $taskClient.Send($taskRequest)
        try {
            $taskBody = $taskResponse.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
            $taskStatus = [int]$taskResponse.StatusCode
            $taskPassed = $taskStatus -in $taskCase.expected
            $taskRows += [ordered]@{name=$taskCase.name; status=$taskStatus; passed=$taskPassed; responseBytes=$taskBody.Length}
            if (-not $taskPassed) { throw "Transport limit case failed: $($taskCase.name), status $taskStatus" }
        } finally { $taskResponse.Dispose(); $taskRequest.Dispose() }
    }
} finally { $taskClient.Dispose(); $taskHandler.Dispose() }
if (Test-Path -LiteralPath $ReceiptPath) { throw 'Receipt already exists; evidence is never overwritten.' }
[ordered]@{passed=$true; cases=$taskRows; note='HTTP.sys may reject wrong authority or oversized headers before the host; no registry limits are modified. Payloads are generated invalid test strings, not authentication credentials.'} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ReceiptPath -Encoding utf8
'Transport bounds: 5 cases passed, including clean reuse'
