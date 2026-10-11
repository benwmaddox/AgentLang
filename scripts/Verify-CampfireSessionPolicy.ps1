#requires -Version 7.0
[CmdletBinding()]
param([string]$CliDll)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($CliDll)) {
    $CliDll = Join-Path $repo 'src/AgentLang.Cli/bin/Release/net9.0/AgentLang.Cli.dll'
}
$CliDll = (Resolve-Path -LiteralPath $CliDll).Path
$runName = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$runDirectory = Join-Path $repo ".agentlang/campfire-session-policy-240/runs/$runName"
$projectDirectory = Join-Path $runDirectory 'project'
$null = New-Item -ItemType Directory -Force -Path $projectDirectory
$utf8 = [Text.UTF8Encoding]::new($false)
$sourcePath = Join-Path $repo 'applications/campfire/session-policy.flow'
$source = [IO.File]::ReadAllText($sourcePath)
$passed = $false
$failure = $null
$oracleCount = 0
$descriptions = @()

function Invoke-Session([string]$Name, [object[]]$Requests) {
    $inputText = (($Requests | ForEach-Object { ConvertTo-Json -InputObject $_ -Depth 16 -Compress }) -join "`n") + "`n"
    [IO.File]::WriteAllText((Join-Path $runDirectory "$Name.stdin.jsonl"), $inputText, $utf8)
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.WorkingDirectory = $repo
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['TEMP'] = $runDirectory
    $start.Environment['TMP'] = $runDirectory
    foreach ($argument in @($CliDll, '--project', $projectDirectory, '--filesystem', 'virtual', '--jsonl')) {
        $start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw "Cannot launch '$Name'." }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.Write($inputText)
        $process.StandardInput.Close()
        $completed = $process.WaitForExit(60000)
        if (-not $completed) { $process.Kill($true); $process.WaitForExit() }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $runDirectory "$Name.stdout.jsonl"), $stdout, $utf8)
        [IO.File]::WriteAllText((Join-Path $runDirectory "$Name.stderr.txt"), $stderr, $utf8)
        if (-not $completed -or $process.ExitCode -ne 0) { throw "CLI session '$Name' failed or timed out." }
        $responses = @($stdout -split "`r?`n" | Where-Object { $_.Length -gt 0 } | ForEach-Object { ConvertFrom-Json -InputObject $_ -Depth 60 })
        if ($responses.Count -ne $Requests.Count) { throw "Wrong response count for '$Name'." }
        return $responses
    } finally {
        $process.Dispose()
    }
}

function Require-Success($Response) {
    if ($Response.ok -ne $true) { throw ($Response | ConvertTo-Json -Depth 12 -Compress) }
}

try {
    $setup = @(Invoke-Session 'setup' @(
        @{ op = 'define'; frontend = 'flow'; syntaxVersion = 2; source = $source },
        @{ op = 'test-all' },
        @{ op = 'commit'; word = 'utc-instant.valid?'; library = $true },
        @{ op = 'commit'; word = 'campfire.sessions.refresh-due?'; library = $true }
    ))
    foreach ($response in $setup) { Require-Success $response }
    if ($setup[1].data.testSummary.executed -ne 10 -or $setup[1].data.testSummary.failed -ne 0) { throw 'Attached session tests failed.' }

    $requests = [Collections.Generic.List[object]]::new()
    $requests.Add(@{ op = 'test-all' })
    foreach ($name in @('utc-instant.valid?', 'campfire.sessions.refresh-due?')) { $requests.Add(@{ op = 'describe'; word = $name }) }
    $cases = [Collections.Generic.List[object]]::new()
    $minimum = [long]-62135596800000000
    $maximum = [long]253402300799999999
    foreach ($now in @($minimum, [long]-3600000000, [long]-1, [long]0, [long]3600000000, $maximum)) {
        foreach ($age in @([long]-3600000001, [long]-1, [long]0, [long]3599999999, [long]3600000000, [long]3600000001, [long]7200000000)) {
            $lastActive = $now - $age
            if ($lastActive -lt $minimum -or $lastActive -gt $maximum) { continue }
            $expected = ([Numerics.BigInteger]$now - [Numerics.BigInteger]$lastActive) -gt [Numerics.BigInteger]3600000000
            $cases.Add(@{ now = $now; lastActiveAt = $lastActive; expected = $expected })
            $requests.Add(@{ op = 'eval'; frontend = 'flow'; syntaxVersion = 2; structured = $true; code = "campfire.sessions.refresh-due?(UtcInstant.new($lastActive), UtcInstant.new($now))" })
        }
    }
    $oracleCount = $cases.Count
    foreach ($value in @(($minimum - 1), ($maximum + 1))) {
        $requests.Add(@{ op = 'eval'; frontend = 'flow'; syntaxVersion = 2; code = "UtcInstant.new($value)" })
    }
    $responses = @(Invoke-Session 'reload-oracle' $requests.ToArray())
    Require-Success $responses[0]
    if ($responses[0].data.testSummary.executed -ne 10 -or $responses[0].data.testSummary.failed -ne 0) { throw 'Reloaded tests failed.' }
    foreach ($index in @(1, 2)) {
        Require-Success $responses[$index]
        $description = $responses[$index].data
        $coverage = $description.coverage
        if ($description.status -cne 'persistent' -or $description.maturity -cne 'library' -or $coverage.status -cne 'current' -or $coverage.instructionsCovered -ne $coverage.instructionsTotal -or $coverage.branchesCovered -ne $coverage.branchesTotal -or $coverage.finiteCoverage.complete -ne $true) { throw 'Reloaded library coverage is incomplete.' }
        $descriptions += $description
    }
    for ($index = 0; $index -lt $cases.Count; $index++) {
        $response = $responses[$index + 3]
        Require-Success $response
        $values = @($response.data.structuredStack.values)
        if ($values.Count -ne 1 -or $values[0].kind -cne 'bool' -or $values[0].value -isnot [bool] -or $values[0].value -ne $cases[$index].expected) { throw "Duration oracle mismatch at case $index." }
    }
    foreach ($response in $responses[($responses.Count - 2)..($responses.Count - 1)]) {
        if ($response.ok -ne $false -or $response.error.code -cne 'REFINEMENT_FAILED') { throw 'An out-of-range UTC constructor was not rejected.' }
    }
    [IO.File]::WriteAllText((Join-Path $runDirectory 'oracle-cases.json'), ($cases.ToArray() | ConvertTo-Json -Depth 8) + "`n", $utf8)
    $passed = $true
} catch {
    $failure = $_.Exception.Message
    Write-Host $failure
} finally {
    $runtimePins = @()
    foreach ($fileName in @('AgentLang.Cli.dll', 'AgentLang.Core.dll', 'AgentLang.Cli.deps.json', 'AgentLang.Cli.runtimeconfig.json', 'FSharp.Core.dll')) {
        $path = Join-Path (Split-Path -Parent $CliDll) $fileName
        $runtimePins += @{ name = $fileName; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    $receipt = [ordered]@{
        formatVersion = 1
        passed = $passed
        failure = $failure
        sourceSha256 = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
        runtime = $runtimePins
        attachedTests = 10
        oracleCases = $oracleCount
        requiredConstructorRejections = 2
        descriptions = $descriptions
    }
    [IO.File]::WriteAllText((Join-Path $runDirectory 'receipt.json'), ($receipt | ConvertTo-Json -Depth 20) + "`n", $utf8)
}
Write-Host "Receipt: $(Join-Path $runDirectory 'receipt.json')"
if (-not $passed) { exit 1 }
Write-Host "Session policy passed: 10 attached tests, $oracleCount duration cases, 2 refinement rejections and fresh library reload."
