#requires -Version 7.0
<#
.SYNOPSIS
Room-page parity: the Campfire Flow server (portable host) against the native HTTP.sys host.

.DESCRIPTION
Runs each server in turn on 127.0.0.1:<Port> against its own fresh copy of a
Rails-login snapshot database, with the snapshot's session cookie, and records
for a fixed request list the status, headers, Set-Cookie semantics and body.
Bodies are compared byte for byte: since report 283 pages carry no CSRF tokens
(forgery protection is by Origin and Sec-Fetch-Site, as in the Rust reference), so
the script also checks that no token markup or session secret appears and that
repeated requests render identical pages.
Set-Cookie values are decrypted / verified with the fixture secret and compared
by content (the encryption nonce, the IV and the expiry instant differ by run).
Then harness loadgen validates the room_show contract for 2 s at 4 clients.
Prints booleans and counts only; secrets, cookies and bodies stay under -Out.

.EXAMPLE
scripts/Test-CampfireFlowServerParity.ps1 -FlowServer .agentlang/cfs/win/flow_http_server.exe -NativeBuild <native O2 build dir> -Out .agentlang/cfs/parity-1
#>
param(
    [Parameter(Mandatory)] [string] $FlowServer,
    [Parameter(Mandatory)] [string] $NativeBuild,
    [Parameter(Mandatory)] [string] $Out,
    [int] $Port = 13773,
    [string] $Snapshot = 'snap-nc',
    [string] $Bench = 'D:\code\AgentLang\.agentlang\campfire-matched-258',
    # With -LinuxServer (a WSL path, e.g. ~/linuxbench/flowhttp/campfire/flow_http_server)
    # the Flow side runs in WSL through scripts/campfire-flow-parity-linux.sh.
    [string] $LinuxServer = '',
    [string] $LinuxLoadgen = '/home/ben/cm258/loadgen-linux/release/loadgen',
    # The Flow server's --store-mb total (0 = no worker store, split between its
    # 4 workers). With a store the room page is streamed through the worker
    # cache (docs/WORKER-STORE-PLAN.md); bodies still match the native host
    # byte for byte. The process-wide --cache-mb cache stays off.
    [int] $StoreMb = 32
)
$ErrorActionPreference = 'Stop'
$repo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$out = [System.IO.Path]::GetFullPath($Out)
if (Test-Path -LiteralPath $out) { throw 'Run roots are immutable.' }
New-Item -ItemType Directory -Force $out | Out-Null
$labels = Get-Content (Join-Path $Bench '../once-campfire-verification/fixtures/default/labels.json') -Raw | ConvertFrom-Json
$room = $labels.'rooms.watercooler'
$cookieFile = Join-Path $Bench "private/rails-cookie-$Snapshot.txt"
$cookie = [IO.File]::ReadAllText($cookieFile).Trim()
$secretFile = Join-Path $Bench 'private/secret-key-base.txt'
$secret = [IO.File]::ReadAllText($secretFile).Trim()
$loadgen = Join-Path $Bench 'loadgen-target/release/loadgen.exe'
$contract = Join-Path $Bench 'contracts-preflight-1/room_show.json'
$chrome = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36'
$base = "http://127.0.0.1:$Port"

# Requests: name, method, path, cookie?, user agent ('' = none).
$cases = @(
    @('room-first', 'GET', "/rooms/$room", $true, ''),
    @('room-steady', 'GET', "/rooms/$room", $true, ''),
    @('room-chrome', 'GET', "/rooms/$room", $true, $chrome),
    @('room-head', 'HEAD', "/rooms/$room", $true, ''),
    @('room-last-room', 'GET', "/rooms/$room", 'last', ''),
    @('room-missing', 'GET', '/rooms/999999', $true, ''),
    @('token-only', 'GET', "/rooms/$room", 'token', ''),
    @('anonymous', 'GET', "/rooms/${room}?x=1", $false, ''),
    @('bad-bot-key', 'GET', "/rooms/${room}?bot_key=1-nope", $false, ''),
    @('old-browser', 'GET', "/rooms/$room", $true, 'Mozilla/5.0 (compatible; MSIE 9.0; Windows NT 6.1; Trident/5.0)'),
    @('cross-site', 'GET', "/rooms/$room", 'cross', ''),
    @('not-a-room', 'GET', '/up', $false, ''),
    @('post', 'POST', "/rooms/$room", $true, ''),
    @('bad-id', 'GET', '/rooms/x1', $true, '')
)

function Start-Server([string] $Kind, [string] $Root) {
    $db = Join-Path $Root 'db'
    New-Item -ItemType Directory -Force $db | Out-Null
    Copy-Item -LiteralPath (Join-Path $Bench "private/native-db-$Snapshot/production.sqlite3") -Destination $db
    $log = Join-Path $Root 'server.log'
    if ($Kind -eq 'native') {
        $arguments = @('--module', (Join-Path $NativeBuild 'owning-stack-native-host.dll'), '--manifest', (Join-Path $NativeBuild 'owning-stack-native-host-manifest.json'),
            '--db', (Join-Path $db 'production.sqlite3'), '--secret-file', $secretFile, '--port', "$Port", '--workers', '4', '--stop-event', 'Local\CampfireParityStop')
        $exe = Join-Path $NativeBuild 'native_http_host.exe'
    } else {
        $config = Join-Path $Root 'config.txt'
        & (Join-Path $PSScriptRoot 'Write-CampfireFlowServerConfig.ps1') -ReferenceEnv (Join-Path $Bench '../once-campfire-verification/fixtures/default/reference.env') -Port $Port -Out $config | Out-Null
        $arguments = @('--port', "$Port", '--db', (Join-Path $db 'production.sqlite3'), '--config', $config, '--workers', '4', '--allow-control', '--cache-mb', '0', '--store-mb', "$StoreMb")
        $exe = [System.IO.Path]::GetFullPath($FlowServer)
    }
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -NoNewWindow -RedirectStandardOutput $log -RedirectStandardError (Join-Path $Root 'server.stderr.log')
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while ([DateTime]::UtcNow -lt $deadline -and -not $process.HasExited -and -not ((Test-Path $log) -and (Select-String -LiteralPath $log -Pattern '^READY ' -Quiet))) { Start-Sleep -Milliseconds 100 }
    if (-not (Select-String -LiteralPath $log -Pattern '^READY ' -Quiet)) { throw "$Kind server not READY" }
    return $process
}

function Stop-Server([string] $Kind, $Process) {
    if ($Kind -eq 'native') {
        $event = [Threading.EventWaitHandle]::OpenExisting('Local\CampfireParityStop'); [void]$event.Set(); $event.Dispose()
    } else {
        try { Invoke-WebRequest -Method Post -Uri "$base/__flow_http/shutdown" -UseBasicParsing | Out-Null } catch { }
    }
    if (-not $Process.WaitForExit(60000)) { $Process.Kill(); throw "$Kind server did not stop" }
}

function Invoke-Case([string] $Root, $Case) {
    $name, $method, $path, $withCookie, $agent = $Case
    $headers = Join-Path $Root "$name.headers.txt"
    $body = Join-Path $Root "$name.body.html"
    $arguments = @('-s', '-S', '-X', $method, '-D', $headers, '-o', $body, '-A', $agent)
    if ($method -eq 'POST') { $arguments += @('-d', '') }
    if ($method -eq 'HEAD') { $arguments = @('-s', '-S', '-I', '-D', $headers, '-o', $body, '-A', $agent) }
    if ($withCookie -eq $true) { $arguments += @('-H', "Cookie: $cookie") }
    elseif ($withCookie -eq 'token') { $tokenOnly = ($cookie.Split(';') | ForEach-Object { $_.Trim() } | Where-Object { $_.StartsWith('session_token=') }) -join ''; $arguments += @('-H', "Cookie: $tokenOnly") }
    elseif ($withCookie -eq 'last') { $arguments += @('-H', "Cookie: $cookie; last_room=$room") }
    elseif ($withCookie -eq 'cross') { $arguments += @('-H', "Cookie: $cookie", '-H', 'Sec-Fetch-Site: cross-site', '-H', 'Origin: https://evil.example') }
    & curl.exe @arguments "$base$path" | Out-Null
}

function ConvertTo-WslPath([string] $Path) {
    $full = [System.IO.Path]::GetFullPath($Path)
    return '/mnt/' + $full.Substring(0, 1).ToLowerInvariant() + $full.Substring(2).Replace('\', '/')
}

$results = [ordered]@{}
foreach ($kind in @('native', 'flow')) {
    $root = Join-Path $out $kind
    New-Item -ItemType Directory -Force $root | Out-Null
    if ($kind -eq 'flow' -and $LinuxServer -ne '') {
        # The Flow server runs in WSL; the same cases are fetched there.
        $tsv = Join-Path $out 'cases.tsv'
        [IO.File]::WriteAllText($tsv, (($cases | ForEach-Object { $_ -join "`t" }) -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
        $script = ConvertTo-WslPath (Join-Path $PSScriptRoot 'campfire-flow-parity-linux.sh')
        $wslArguments = @($LinuxServer, (ConvertTo-WslPath $tsv), (ConvertTo-WslPath $root),
            (ConvertTo-WslPath (Join-Path $Bench "private/native-db-$Snapshot/production.sqlite3")),
            (ConvertTo-WslPath (Join-Path $Bench '../once-campfire-verification/fixtures/default/reference.env')),
            (ConvertTo-WslPath $cookieFile), $LinuxLoadgen, (ConvertTo-WslPath $contract), "$Port", (Split-Path -Leaf $out), "$StoreMb")
        $quoted = ($wslArguments | ForEach-Object { "'$_'" }) -join ' '
        & wsl -- bash -lc "tr -d '\r' < '$script' > /tmp/campfire-flow-parity-linux.sh && bash /tmp/campfire-flow-parity-linux.sh $quoted" | Select-Object -Last 1
        $validation = Get-Content -LiteralPath (Join-Path $root 'loadgen.json') -Raw | ConvertFrom-Json
        $results[$kind] = [ordered]@{ ok = $validation.ok; invalid = $validation.invalid_responses; errors = $validation.errors; rps = $validation.rps }
        continue
    }
    $process = Start-Server $kind $root
    try {
        foreach ($case in $cases) { Invoke-Case $root $case }
        $file = Join-Path $root 'loadgen-body.html'
        & $loadgen fetch --base $base --cookie $cookie --path "/rooms/$room" --out $file | Out-Null
        $validation = & $loadgen http --base $base --cookie $cookie --path "/rooms/$room" --validate $contract --conc 4 --duration 2 --gzip 0 | ConvertFrom-Json
        $results[$kind] = [ordered]@{ ok = $validation.ok; invalid = $validation.invalid_responses; errors = $validation.errors; rps = $validation.rps }
    } finally { Stop-Server $kind $process }
}

# ---------------------------------------------------------------- compare
$encryptionKey = [Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2([Text.Encoding]::UTF8.GetBytes($secret), [Text.Encoding]::UTF8.GetBytes('authenticated encrypted cookie'), 1000, [Security.Cryptography.HashAlgorithmName]::SHA256, 32)
$signingKey = [Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2([Text.Encoding]::UTF8.GetBytes($secret), [Text.Encoding]::UTF8.GetBytes('signed cookie'), 1000, [Security.Cryptography.HashAlgorithmName]::SHA256, 64)
function Open-Envelope([string] $Text) {
    $envelope = $Text | ConvertFrom-Json -DateKind String
    return [ordered]@{ message = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($envelope._rails.message)); pur = $envelope._rails.pur; exp = $envelope._rails.exp }
}
function Read-Cookie([string] $Line) {
    # name=value; attributes -> name, decoded content, attribute names (Expires date only).
    $parts = $Line.Split(';') | ForEach-Object { $_.Trim() }
    $name, $value = $parts[0].Split('=', 2)
    $value = [Uri]::UnescapeDataString($value)
    $attributes = ($parts | Select-Object -Skip 1 | ForEach-Object { if ($_ -like 'Expires=*') { 'Expires=' + ([DateTime]::Parse($_.Substring(8)).ToUniversalTime().ToString('yyyy-MM-dd')) } else { $_ } }) -join '; '
    $content = $value
    if ($name -eq '_campfire_session') {
        $pieces = $value.Split('--'); $data = [Convert]::FromBase64String($pieces[0]); $plain = [byte[]]::new($data.Length)
        [Security.Cryptography.AesGcm]::new($encryptionKey, 16).Decrypt([Convert]::FromBase64String($pieces[1]), $data, [Convert]::FromBase64String($pieces[2]), $plain)
        $opened = Open-Envelope ([Text.Encoding]::UTF8.GetString($plain))
        $session = $opened.message | ConvertFrom-Json -AsHashtable -DateKind String
        if ($session.ContainsKey('_csrf_token')) { $session['_csrf_token'] = "<{0} chars>" -f $session['_csrf_token'].Length }
        $content = "pur=$($opened.pur) exp=$($opened.exp.Substring(0, 10)) session=" + ($session | ConvertTo-Json -Compress -Depth 8)
    } elseif ($name -eq 'session_token') {
        $index = $value.LastIndexOf('--'); $data = $value.Substring(0, $index)
        $digest = [Convert]::ToHexString([Security.Cryptography.HMACSHA1]::HashData($signingKey, [Text.Encoding]::ASCII.GetBytes($data))).ToLowerInvariant()
        $opened = Open-Envelope ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($data)))
        $content = "digest-ok=$($digest -eq $value.Substring($index + 2)) pur=$($opened.pur) exp=$($opened.exp.Substring(0, 10)) token-sha=" + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($opened.message))).Substring(0, 12)
    }
    return "$name | $content | $attributes"
}
function Read-Response([string] $Root, [string] $Name) {
    $lines = Get-Content -LiteralPath (Join-Path $Root "$Name.headers.txt") | Where-Object { $_ -ne '' }
    $status = ($lines[0] -split ' ')[1]
    $headers = @(); $cookies = @()
    foreach ($line in ($lines | Select-Object -Skip 1)) {
        $headerName, $headerValue = $line.Split(':', 2)
        $headerName = $headerName.Trim().ToLowerInvariant(); $headerValue = $headerValue.Trim()
        if ($headerName -in @('date', 'server', 'connection', 'keep-alive')) { continue }
        if ($headerName -eq 'set-cookie') { $cookies += Read-Cookie $headerValue } else { $headers += "${headerName}: $headerValue" }
    }
    $bodyFile = Join-Path $Root "$Name.body.html"
    # curl -I writes the response head into the body file; HEAD has no body to compare.
    $body = if ((Test-Path $bodyFile) -and $Name -ne 'room-head') { [IO.File]::ReadAllText($bodyFile) } else { '' }
    return [ordered]@{ status = $status; headers = (@($headers | Sort-Object) -join "`n"); cookies = ($cookies -join "`n"); body = $body; bodyBytes = $body.Length }
}
$allSame = $true
foreach ($case in $cases) {
    $name = $case[0]
    $native = Read-Response (Join-Path $out 'native') $name
    $flow = Read-Response (Join-Path $out 'flow') $name
    $same = [ordered]@{ status = $native.status -eq $flow.status; headers = $native.headers -eq $flow.headers; cookies = $native.cookies -eq $flow.cookies; body = $native.body -ceq $flow.body }
    $ok = -not ($same.Values -contains $false)
    $allSame = $allSame -and $ok
    "{0,-15} status {1}/{2} headers={3} set-cookie={4} body={5} ({6} bytes)" -f $name, $native.status, $flow.status, $same.headers, $same.cookies, $same.body, $flow.bodyBytes
    if (-not $ok) {
        [IO.File]::WriteAllText((Join-Path $out "$name.native.txt"), "$($native.status)`n$($native.headers)`n$($native.cookies)`n`n$($native.body)")
        [IO.File]::WriteAllText((Join-Path $out "$name.flow.txt"), "$($flow.status)`n$($flow.headers)`n$($flow.cookies)`n`n$($flow.body)")
    }
}
# CSRF (report 283): no csrf meta tags, no authenticity_token fields and none of
# the session's _csrf_token (a Rails-written session still carries one).
$csrfSecret = $null
$sessionPart = $cookie.Split(';') | ForEach-Object { $_.Trim() } | Where-Object { $_.StartsWith('_campfire_session=') } | Select-Object -First 1
if ($sessionPart) {
    $pieces = [Uri]::UnescapeDataString($sessionPart.Substring(18)).Split('--'); $data = [Convert]::FromBase64String($pieces[0]); $plain = [byte[]]::new($data.Length)
    [Security.Cryptography.AesGcm]::new($encryptionKey, 16).Decrypt([Convert]::FromBase64String($pieces[1]), $data, [Convert]::FromBase64String($pieces[2]), $plain)
    $session = (Open-Envelope ([Text.Encoding]::UTF8.GetString($plain))).message | ConvertFrom-Json -AsHashtable
    if ($session.ContainsKey('_csrf_token')) { $csrfSecret = $session['_csrf_token'] }
}
function Test-NoCsrf([string] $Body) {
    return -not $Body.Contains('csrf') -and -not $Body.Contains('authenticity_token') -and ($null -eq $csrfSecret -or -not $Body.Contains($csrfSecret))
}
foreach ($kind in @('native', 'flow')) {
    $steady = [IO.File]::ReadAllText((Join-Path $out "$kind/room-steady.body.html"))
    $first = [IO.File]::ReadAllText((Join-Path $out "$kind/room-first.body.html"))
    $noCsrf = Test-NoCsrf $steady
    # No per-request tokens: the first and the steady room page are the same bytes,
    # with the fragment cache on or off.
    $repeat = $first -ceq $steady
    $allSame = $allSame -and $noCsrf -and $repeat
    "{0,-7} csrf: no-token-markup={1} session-secret-present={2} first==steady={3} (flow --store-mb {4})" -f $kind, $noCsrf, ($null -ne $csrfSecret), $repeat, $StoreMb
    "{0,-7} loadgen --validate 2 s: ok={1} invalid={2} errors={3}" -f $kind, $results[$kind].ok, $results[$kind].invalid, $results[$kind].errors
}
$flowServerLog = Join-Path $out 'flow/server.stderr.log'
if (Test-Path $flowServerLog) {
    "flow    {0}" -f ((Select-String -LiteralPath $flowServerLog -Pattern 'STOPPED' | Select-Object -Last 1).Line -replace '^.*(cache-mb=.*)$', '$1')
    foreach ($line in (Select-String -LiteralPath $flowServerLog -Pattern '^worker \d+: ')) { "flow    {0}" -f ($line.Line -replace '^(worker \d+): .*(store-bytes=\d+ store-pages-used=\d+).*$', '$1 $2') }
}
$loadgenSame = [IO.File]::ReadAllText((Join-Path $out 'native/loadgen-body.html')) -ceq [IO.File]::ReadAllText((Join-Path $out 'flow/loadgen-body.html'))
$allSame = $allSame -and $loadgenSame
"loadgen bodies identical: {0}" -f $loadgenSame
"PARITY {0}" -f ($(if ($allSame) { 'PASS' } else { 'FAIL' }))
