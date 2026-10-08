[CmdletBinding()]
param(
    [ValidatePattern('^\d{2}$')]
    [string]$Attempt = '02'
)

$ErrorActionPreference = 'Stop'
$trial = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repo = (Resolve-Path (Join-Path $trial '..\..')).Path
$runDirectory = Join-Path $PSScriptRoot "attempt-$Attempt"
if (Test-Path -LiteralPath $runDirectory) { throw "Preflight attempt already exists: $runDirectory" }
New-Item -ItemType Directory -Path $runDirectory | Out-Null
$runtimeDirectory = Join-Path $trial 'runtime\debug-artifacts'
$runtime = Join-Path $runtimeDirectory 'AgentLang.Cli.dll'
$hostScript = Join-Path $repo 'scripts\Start-SubagentTrialHostV2.ps1'
$pin = Get-Content (Join-Path $trial 'runtime-pin.json') -Raw | ConvertFrom-Json

foreach ($file in $pin.runtimeFiles) {
    $path = Join-Path $runtimeDirectory $file.path.Replace('/', '\')
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) {
        throw "Pinned runtime dependency mismatch: $($file.path)"
    }
}
if ((Get-FileHash -LiteralPath $hostScript -Algorithm SHA256).Hash -ne $pin.broker.sha256) {
    throw 'Pinned broker hash mismatch.'
}

$source = @'
record BatchBounds {
    field minimum: Int;
    field maximum: Int;
    validate batch::valid?;
}

fn batch.valid?(value: BatchBounds) -> Bool {
    bool::and(int::greater-or-equal(value.minimum, 1), int::greater-or-equal(value.maximum, value.minimum))
}

fn batch.span(value: BatchBounds) -> Int {
    subtract(value.maximum, value.minimum)
}

test batch.valid?/minimum-boundary-valid {
    batch::valid?(batchBounds::new(minimum = 1, maximum = 1))
    => true
}

test batch.valid?/zero-minimum-rejected {
    batchBounds::new(minimum = 0, maximum = 5)
    => error RECORD_VALIDATION_FAILED
}

test batch.valid?/reversed-rejected {
    batchBounds::new(minimum = 4, maximum = 3)
    => error RECORD_VALIDATION_FAILED
}

test batch.span/positive-span {
    batch::span(batchBounds::new(minimum = 1, maximum = 7))
    => 6
}
'@
$predicateExpression = 'bool::and(int::greater-or-equal(value.minimum, 1), int::greater-or-equal(value.maximum, value.minimum))'
$faultySource = $source.Replace($predicateExpression, 'true')
if ($faultySource -eq $source) { throw 'Fault control did not replace the predicate expression.' }

$cases = @(
    [pscustomobject]@{ minimum = 1; maximum = 1; kind = 'accepted'; span = 0 },
    [pscustomobject]@{ minimum = 1; maximum = 7; kind = 'accepted'; span = 6 },
    [pscustomobject]@{ minimum = 3; maximum = 8; kind = 'accepted'; span = 5 },
    [pscustomobject]@{ minimum = 9; maximum = 9; kind = 'accepted'; span = 0 },
    [pscustomobject]@{ minimum = 0; maximum = 0; kind = 'rejected'; span = $null },
    [pscustomobject]@{ minimum = 0; maximum = 5; kind = 'rejected'; span = $null },
    [pscustomobject]@{ minimum = -3; maximum = 4; kind = 'rejected'; span = $null },
    [pscustomobject]@{ minimum = 4; maximum = 3; kind = 'rejected'; span = $null },
    [pscustomobject]@{ minimum = 2; maximum = 0; kind = 'rejected'; span = $null },
    [pscustomobject]@{ minimum = -5; maximum = -1; kind = 'rejected'; span = $null }
)

function Write-JsonLines([string]$Path, [object[]]$Items) {
    $lines = foreach ($item in $Items) { $item | ConvertTo-Json -Depth 20 -Compress }
    [System.IO.File]::WriteAllLines($Path, [string[]]$lines, [System.Text.UTF8Encoding]::new($false))
}

function Invoke-Control([string]$Name, [string]$Definition, [string]$ProjectLeaf) {
    $project = Join-Path (Join-Path $trial 'controls') $ProjectLeaf
    if (@(Get-ChildItem -LiteralPath $project -Force -Recurse).Count -ne 0) {
        throw "Control project is not empty: $project"
    }

    $requests = [System.Collections.Generic.List[object]]::new()
    $requests.Add([ordered]@{ op = 'define'; source = $Definition; syntaxVersion = 2; frontend = 'flow' })
    $requests.Add([ordered]@{ op = 'test-all' })
    $requests.Add([ordered]@{ op = 'describe'; word = 'batch.valid?' })
    foreach ($case in $cases) {
        if ($case.kind -eq 'accepted') {
            $code = "int::to-string(batch::span(batchBounds::new(minimum = $($case.minimum), maximum = $($case.maximum))))"
        } else {
            $code = "batchBounds::new(minimum = $($case.minimum), maximum = $($case.maximum))"
        }
        $requests.Add([ordered]@{ op = 'eval'; code = $code; syntaxVersion = 2; frontend = 'flow'; structured = $true })
    }

    $requestPath = Join-Path $runDirectory "$Name.requests.jsonl"
    $responsePath = Join-Path $runDirectory "$Name.responses.jsonl"
    $tracePath = Join-Path $runDirectory "$Name.trace.jsonl"
    $stderrPath = Join-Path $runDirectory "$Name.stderr.log"
    Write-JsonLines $requestPath (@($requests.ToArray()) + @([ordered]@{ op = 'host.close' }))

    $start = [System.Diagnostics.ProcessStartInfo]::new()
    $start.FileName = (Get-Command pwsh).Source
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @(
        '-NoProfile', '-File', $hostScript,
        '-CliDll', $runtime,
        '-ProjectPath', $project,
        '-TracePath', $tracePath,
        '-AllowedOperations', 'define,test-all,describe,eval',
        '-Profile', 'agentlang',
        '-ClockValue', '2000-01-01T00:00:00Z',
        '-ExchangeTimeoutMilliseconds', '120000',
        '-MaxExchanges', '20'
    )) { $start.ArgumentList.Add($argument) }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (-not $process.Start()) { throw "Could not launch control $Name." }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $process.StandardInput.Write([System.IO.File]::ReadAllText($requestPath))
    $process.StandardInput.Close()
    $process.WaitForExit()
    $outText = $stdout.GetAwaiter().GetResult()
    $errText = $stderr.GetAwaiter().GetResult()
    [System.IO.File]::WriteAllText($responsePath, $outText, [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText($stderrPath, $errText, [System.Text.UTF8Encoding]::new($false))

    $responses = @(
        [System.IO.File]::ReadAllLines($responsePath) |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            ForEach-Object { $_ | ConvertFrom-Json -Depth 40 }
    )
    if ($process.ExitCode -ne 0 -or $responses.Count -ne 13) {
        throw "$Name host returned exit=$($process.ExitCode), responses=$($responses.Count); logs preserved."
    }
    if (-not $responses[0].ok) { throw "$Name definition failed: $($responses[0] | ConvertTo-Json -Depth 8 -Compress)" }
    if (-not $responses[1].ok) { throw "$Name test-all operation failed." }

    $tests = @($responses[1].data.results)
    $actualNames = @($tests | ForEach-Object { [string]$_.name } | Sort-Object)
    $expectedNames = @('minimum-boundary-valid', 'positive-span', 'reversed-rejected', 'zero-minimum-rejected')
    [Array]::Sort($expectedNames)
    if (($actualNames -join '|') -ne ($expectedNames -join '|')) {
        throw "$Name returned unexpected or duplicate test names: $($actualNames -join ', ')"
    }
    $failedTests = @($tests | Where-Object { -not $_.passed })
    if ($Name -eq 'correct' -and $failedTests.Count -ne 0) {
        throw 'Correct control must pass all four attached tests.'
    }
    if ($Name -eq 'always-true') {
        $expectedFailures = @($failedTests | Where-Object {
            $_.name -in @('zero-minimum-rejected', 'reversed-rejected') -and $_.errorCode -eq 'TEST_EXPECTED_RUNTIME_ERROR'
        })
        if ($failedTests.Count -ne 2 -or $expectedFailures.Count -ne 2) {
            throw 'Always-true control must fail only the two expected constructor-error tests.'
        }
    }

    if (-not $responses[2].ok) { throw "$Name predicate coverage could not be described." }
    $coverage = $responses[2].data.coverage.finiteCoverage
    $boolRows = @($coverage.returns | Where-Object { $_.position -eq 0 -and $_.type -eq 'Bool' })
    if ($boolRows.Count -ne 1) { throw "$Name did not expose one Bool-return coverage row." }
    $observedTrue = @($boolRows[0].observed | Where-Object { $_ -eq 'true' }).Count -gt 0
    $observedFalse = @($boolRows[0].observed | Where-Object { $_ -eq 'false' }).Count -gt 0
    if ($Name -eq 'correct' -and (-not $coverage.complete -or -not $observedTrue -or -not $observedFalse)) {
        throw 'Correct predicate control did not observe both Bool returns.'
    }
    if ($Name -eq 'always-true' -and $observedFalse) {
        throw 'Always-true predicate unexpectedly observed false.'
    }
    if ($Name -eq 'always-true' -and $coverage.complete -ne $false) {
        throw 'Always-true predicate should leave finite Bool coverage incomplete.'
    }

    $oracle = for ($index = 0; $index -lt $cases.Count; $index++) {
        $case = $cases[$index]
        $response = $responses[$index + 3]
        $actual = if (-not $response.ok) {
            $response.error.code
        } elseif ($case.kind -eq 'accepted') {
            $response.data.structuredStack.values[0].value
        } else {
            'accepted'
        }
        $expected = if ($case.kind -eq 'accepted') {
            [string]$case.span
        } elseif ($Name -eq 'correct') {
            'RECORD_VALIDATION_FAILED'
        } else {
            'accepted by faulty predicate'
        }
        $passed = if ($case.kind -eq 'accepted') {
            $response.ok -and $actual -eq [string]$case.span
        } elseif ($Name -eq 'correct') {
            (-not $response.ok) -and $actual -eq 'RECORD_VALIDATION_FAILED'
        } else {
            $response.ok
        }
        [pscustomobject]@{ minimum = $case.minimum; maximum = $case.maximum; expected = $expected; actual = $actual; passed = $passed }
    }
    if (@($oracle | Where-Object { -not $_.passed }).Count -ne 0) {
        throw "$Name direct constructor/span oracle failed: $($oracle | ConvertTo-Json -Depth 8 -Compress)"
    }

    [ordered]@{
        name = $Name
        hostExitCode = $process.ExitCode
        requestCount = $requests.Count
        responseCount = $responses.Count
        testsRun = $tests.Count
        testsPassed = @($tests | Where-Object { $_.passed }).Count
        testsFailed = $failedTests.Count
        tests = $tests
        predicateCoverage = $coverage
        oracle = @($oracle)
        requestsFile = [System.IO.Path]::GetFileName($requestPath)
        responsesFile = [System.IO.Path]::GetFileName($responsePath)
        traceFile = [System.IO.Path]::GetFileName($tracePath)
        stderrFile = [System.IO.Path]::GetFileName($stderrPath)
    }
}

$faultySource = $source.Replace($predicateExpression, 'true')
if ($faultySource -eq $source) { throw 'Fault control mutation was not applied.' }
$controls = @(
    (Invoke-Control 'correct' $source "correct-project-$Attempt"),
    (Invoke-Control 'always-true' $faultySource "always-true-project-$Attempt")
)
$summary = [ordered]@{
    schemaVersion = 1
    purpose = 'Coordinator-only direct-JSONL preflight; not actor evidence or a causal comparison.'
    runtimePin = '.agentlang/record-validation-help-adoption-001/runtime-pin.json'
    report121Commit = $pin.report121Commit
    controls = $controls
}
$summary | ConvertTo-Json -Depth 25 | Set-Content (Join-Path $runDirectory 'summary.json') -Encoding utf8NoBOM
$summary | ConvertTo-Json -Depth 25
