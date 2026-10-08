[CmdletBinding()]
param(
    [ValidatePattern('^\d{2}$')]
    [string]$Attempt = '02'
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$runtime = Join-Path $root 'runtime\debug-artifacts\AgentLang.Cli.dll'
$hostScript = Join-Path $repo 'scripts\Start-SubagentTrialHostV2.ps1'
$pwshPath = (Get-Command pwsh -ErrorAction Stop).Source
$sourceBase = @'
record BoundedRange {
    field start: Int;
    field finish: Int;
    validate range::valid?;
}

fn range.valid?(value: BoundedRange) -> Bool {
    int::less-or-equal(value.start, value.finish)
}

fn range.width(value: BoundedRange) -> Int {
    subtract(value.finish, value.start)
}

test range.valid?/ordered-range {
    range::valid?(boundedRange::new(start = 2, finish = 5))
    => true
}

test range.valid?/reversed-construction-is-rejected {
    boundedRange::new(start = 5, finish = 2)
    => error RECORD_VALIDATION_FAILED
}

test range.width/positive-range {
    range::width(boundedRange::new(start = 2, finish = 5))
    => 3
}
'@
$sourceMutant = $sourceBase.Replace(
    'int::less-or-equal(value.start, value.finish)',
    'true'
)
if ($sourceMutant -eq $sourceBase) { throw 'The always-true control mutation was not applied.' }
$cases = @(
    [pscustomobject]@{ start=2; finish=5; kind='accepted'; width=3 },
    [pscustomobject]@{ start=0; finish=0; kind='accepted'; width=0 },
    [pscustomobject]@{ start=-3; finish=4; kind='accepted'; width=7 },
    [pscustomobject]@{ start=-8; finish=-5; kind='accepted'; width=3 },
    [pscustomobject]@{ start=7; finish=7; kind='accepted'; width=0 },
    [pscustomobject]@{ start=5; finish=2; kind='rejected'; width=$null },
    [pscustomobject]@{ start=0; finish=-1; kind='rejected'; width=$null },
    [pscustomobject]@{ start=-4; finish=-8; kind='rejected'; width=$null },
    [pscustomobject]@{ start=9; finish=4; kind='rejected'; width=$null }
)

function Write-JsonLines([string]$path, [object[]]$items) {
    $lines = foreach ($item in $items) { $item | ConvertTo-Json -Depth 20 -Compress }
    [System.IO.File]::WriteAllLines($path, [string[]]$lines, [System.Text.UTF8Encoding]::new($false))
}

function Run-Control([string]$name, [string]$source, [string]$projectRelative) {
    $project = Join-Path $root $projectRelative
    $entries = @(Get-ChildItem -LiteralPath $project -Force -Recurse)
    if ($entries.Count -ne 0) { throw "Control project is not empty before run: $project" }
    $preflight = Join-Path $root 'preflight'
    $tracePath = Join-Path $preflight "$name-$Attempt.trace.jsonl"
    $inputPath = Join-Path $preflight "$name-$Attempt.requests.jsonl"
    $outputPath = Join-Path $preflight "$name-$Attempt.responses.jsonl"
    $stderrPath = Join-Path $preflight "$name-$Attempt.stderr.log"

    $requests = [System.Collections.Generic.List[object]]::new()
    $requests.Add([ordered]@{ op='define'; source=$source; syntaxVersion=2; frontend='flow' })
    $requests.Add([ordered]@{ op='test-all' })
    foreach ($case in $cases) {
        if ($case.kind -eq 'accepted') {
            $code = "int::to-string(range::width(boundedRange::new(start = $($case.start), finish = $($case.finish))))"
        } else {
            $code = "boundedRange::new(start = $($case.start), finish = $($case.finish))"
        }
        $requests.Add([ordered]@{ op='eval'; code=$code; syntaxVersion=2; frontend='flow'; structured=$true })
    }
    $requests.Add([ordered]@{ op='host.close' })
    Write-JsonLines $inputPath $requests.ToArray()
    $requestText = [System.IO.File]::ReadAllText($inputPath)

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $pwshPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in @(
        '-NoProfile', '-File', $hostScript,
        '-CliDll', $runtime,
        '-ProjectPath', $project,
        '-TracePath', $tracePath,
        '-AllowedOperations', 'define,test-all,eval',
        '-Profile', 'agentlang',
        '-ClockValue', '2000-01-01T00:00:00Z',
        '-ExchangeTimeoutMilliseconds', '120000',
        '-MaxExchanges', '20'
    )) { $startInfo.ArgumentList.Add($argument) }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { throw "Could not launch the existing trial host for $name." }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.StandardInput.Write($requestText)
    $process.StandardInput.Close()
    $process.WaitForExit()
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    [System.IO.File]::WriteAllText($outputPath, $stdout, [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText($stderrPath, $stderr, [System.Text.UTF8Encoding]::new($false))
    if ($process.ExitCode -ne 0) { throw "Trial host for $name exited $($process.ExitCode); see $stderrPath." }

    $responses = @(
        [System.IO.File]::ReadAllLines($outputPath) |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            ForEach-Object { $_ | ConvertFrom-Json -Depth 40 }
    )
    if ($responses.Count -ne 11) { throw "$name returned $($responses.Count) responses; expected 11." }
    if (-not $responses[0].ok) { throw "$name define failed: $($responses[0] | ConvertTo-Json -Depth 8 -Compress)" }

    if (-not $responses[1].ok) { throw "$name test-all operation failed: $($responses[1] | ConvertTo-Json -Depth 8 -Compress)" }
    $testResults = @($responses[1].data.results)
    if ($testResults.Count -ne 3) { throw "$name returned $($testResults.Count) test results; expected exactly 3." }
    $expectedTestNames = @('ordered-range', 'positive-range', 'reversed-construction-is-rejected') | Sort-Object
    $actualTestNames = @($testResults | ForEach-Object { [string]$_.name } | Sort-Object)
    if (($actualTestNames -join '|') -ne ($expectedTestNames -join '|')) { throw "$name returned unexpected or duplicate test names: $($actualTestNames -join ', ')." }
    $failedTests = @($testResults | Where-Object { -not $_.passed })
    if ($name -eq 'correct' -and ($failedTests.Count -ne 0 -or @($testResults | Where-Object { $_.passed }).Count -ne 3)) { throw 'Correct control did not pass exactly three tests.' }
    if ($name -eq 'always-true') {
        $expectedMutantFailure = @($failedTests | Where-Object { $_.name -eq 'reversed-construction-is-rejected' -and $_.errorCode -eq 'TEST_EXPECTED_RUNTIME_ERROR' })
        if ($failedTests.Count -ne 1 -or $expectedMutantFailure.Count -ne 1) { throw 'Always-true control did not fail only the reversed-construction expected-error test.' }
    }

    $evalChecks = for ($index = 0; $index -lt $cases.Count; $index++) {
        $case = $cases[$index]
        $response = $responses[$index + 2]
        $actual = if (-not $response.ok) {
            $response.error.code
        } elseif ($case.kind -eq 'accepted') {
            $response.data.structuredStack.values[0].value
        } else {
            'accepted'
        }
        $passed = if ($case.kind -eq 'accepted') {
            $response.ok -and ($actual -eq [string]$case.width)
        } elseif ($name -eq 'correct') {
            (-not $response.ok) -and ($actual -eq 'RECORD_VALIDATION_FAILED')
        } else {
            $response.ok
        }
        [pscustomobject]@{
            start = $case.start
            finish = $case.finish
            expected = if ($case.kind -eq 'accepted') { [string]$case.width } elseif ($name -eq 'correct') { 'RECORD_VALIDATION_FAILED' } else { 'accepted by mutant' }
            actual = $actual
            passed = $passed
        }
    }
    $failedChecks = @($evalChecks | Where-Object { -not $_.passed })
    if ($failedChecks.Count -ne 0) { throw "$name oracle checks failed: $($failedChecks | ConvertTo-Json -Depth 8 -Compress)" }

    return [pscustomobject]@{
        name = $name
        hostExitCode = $process.ExitCode
        requestCount = $requests.Count
        responseCount = $responses.Count
        definitionOk = $responses[0].ok
        testsRun = $testResults.Count
        testsPassed = @($testResults | Where-Object { $_.passed }).Count
        testsFailed = $failedTests.Count
        tests = $testResults
        oracle = @($evalChecks)
        tracePath = [System.IO.Path]::GetRelativePath($root, $tracePath)
        requestPath = [System.IO.Path]::GetRelativePath($root, $inputPath)
        responsePath = [System.IO.Path]::GetRelativePath($root, $outputPath)
        stderrPath = [System.IO.Path]::GetRelativePath($root, $stderrPath)
    }
}

$results = @(
    Run-Control 'correct' $sourceBase "controls\correct-project-$Attempt"
    Run-Control 'always-true' $sourceMutant "controls\always-true-project-$Attempt"
)
$summary = [ordered]@{
    schemaVersion = 1
    purpose = 'bounded direct-JSONL preflight; not actor evidence'
    runtime = [System.IO.Path]::GetRelativePath($root, $runtime)
    controls = $results
}
$summaryPath = Join-Path $root 'preflight\summary.json'
$summary | ConvertTo-Json -Depth 25 | Set-Content -LiteralPath $summaryPath -Encoding utf8NoBOM
$summary | ConvertTo-Json -Depth 25
