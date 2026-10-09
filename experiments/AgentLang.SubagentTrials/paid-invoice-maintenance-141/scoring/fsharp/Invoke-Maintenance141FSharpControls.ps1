[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string] $RunId = ('preflight-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..\..')).Path
$studyRoot = Join-Path $repositoryRoot 'experiments\AgentLang.SubagentTrials\paid-invoice-maintenance-141'
$startProject = Join-Path $studyRoot 'starts\fsharp\project'
$evidenceRoot = Join-Path $repositoryRoot '.agentlang\maintenance-141\fsharp-scoring'
$workspaceRoot = Join-Path (Join-Path $evidenceRoot 'workspaces') $RunId
$runner = Join-Path $PSScriptRoot 'Invoke-Maintenance141FSharpScore.ps1'
$oracle = Join-Path $studyRoot 'oracle.json'
$hashPaths = @(
    $oracle,
    (Join-Path $startProject 'business\AgentLang.Business.fsproj'),
    (Join-Path $startProject 'business\Business.fs'),
    (Join-Path $startProject 'tests\Program.fs'),
    (Join-Path $startProject 'tests\AgentLang.Business.Tests.fsproj'),
    (Join-Path $PSScriptRoot 'AgentLang.Maintenance141.FSharpScorer.fsproj'),
    (Join-Path $PSScriptRoot 'Program.fs'),
    $PSCommandPath,
    $runner
)
$sourceHashesBefore = [ordered]@{}
foreach ($path in $hashPaths) {
    $key = if ($path.StartsWith($repositoryRoot, [StringComparison]::OrdinalIgnoreCase)) { $path.Substring($repositoryRoot.Length).TrimStart('\') } else { $path }
    $sourceHashesBefore[$key] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

if (Test-Path -LiteralPath $workspaceRoot) {
    throw "Control workspace already exists; choose a fresh RunId: $workspaceRoot"
}
New-Item -ItemType Directory -Path $workspaceRoot -Force | Out-Null

$controlProjects = [ordered]@{}
foreach ($control in @('baseline', 'correct', 'plausible-incorrect')) {
    $target = Join-Path $workspaceRoot $control
    Copy-Item -LiteralPath $startProject -Destination $target -Recurse
    $controlProjects[$control] = $target
}

$originalCondition = '| Some ownedInvoice when ownedInvoice.CustomerId = state.CustomerId ->'
$correctCondition = '| Some ownedInvoice when ownedInvoice.Status = Paid && ownedInvoice.CustomerId = state.CustomerId ->'
$incorrectCondition = '| Some ownedInvoice when ownedInvoice.Status = Paid && ownedInvoice.CustomerId = state.CustomerId && not (Money.isNegative payment.Amount) ->'
$sourceCopies = [ordered]@{}
foreach ($control in @('correct', 'plausible-incorrect')) {
    $businessPath = Join-Path $controlProjects[$control] 'business\Business.fs'
    $source = [System.IO.File]::ReadAllText($businessPath)
    $matches = [regex]::Matches($source, [regex]::Escape($originalCondition))
    if ($matches.Count -ne 1) {
        throw "Expected one frozen aggregation match for $control; found $($matches.Count). No control was scored."
    }
    $replacement = if ($control -eq 'correct') { $correctCondition } else { $incorrectCondition }
    [System.IO.File]::WriteAllText($businessPath, $source.Replace($originalCondition, $replacement), [System.Text.UTF8Encoding]::new($false))
    $sourceCopies[$control] = $businessPath
}
$sourceCopies['baseline'] = Join-Path $controlProjects['baseline'] 'business\Business.fs'

$results = [ordered]@{}
foreach ($control in @('baseline', 'correct', 'plausible-incorrect')) {
    $scoreRunId = "$RunId-$($control -replace '[^A-Za-z0-9._-]', '-')"
    $candidate = Join-Path $controlProjects[$control] 'business\AgentLang.Business.fsproj'
    $run = & $runner -CandidateProject $candidate -RunId $scoreRunId -OraclePath $oracle
    $results[$control] = [ordered]@{
        runId = $run.RunId
        resultPath = $run.ResultPath
        runRoot = $run.RunRoot
        behaviorPassed = $run.BehaviorPassed
        responseCount = $run.ResponseCount
        cases = @($run.CaseResults | ForEach-Object { [ordered]@{ id = $_.Id; passed = $_.Passed } })
        businessSha256 = (Get-FileHash -LiteralPath $sourceCopies[$control] -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

$expectedCaseResults = @{
    baseline = @($false, $false, $true, $true)
    correct = @($true, $true, $true, $true)
    'plausible-incorrect' = @($false, $true, $true, $true)
}
foreach ($control in @('baseline', 'correct', 'plausible-incorrect')) {
    $actualCases = @($results[$control].cases | ForEach-Object { [bool]$_.passed })
    if ($actualCases.Count -ne 4) {
        throw "$control scorer returned $($actualCases.Count) cases, expected four."
    }
    for ($index = 0; $index -lt 4; $index++) {
        if ($actualCases[$index] -ne $expectedCaseResults[$control][$index]) {
            throw "$control control mismatch at oracle case $index ($($results[$control].cases[$index].id)): expected pass=$($expectedCaseResults[$control][$index]), actual pass=$($actualCases[$index])."
        }
    }
}

$testProject = Join-Path $controlProjects['correct'] 'tests\AgentLang.Business.Tests.fsproj'
$testArtifacts = Join-Path $workspaceRoot 'correct-inherited-test-artifacts'
$testBuildArguments = @(
    'build', $testProject,
    '--artifacts-path', $testArtifacts,
    '-c', 'Release',
    '-p:NuGetAudit=false',
    '-p:BuildInParallel=false',
    '-m:1'
)
$testBuildOutput = @(& dotnet @testBuildArguments 2>&1 | ForEach-Object { $_.ToString() })
$testBuildExitCode = $LASTEXITCODE
$testBuildOutput | Set-Content -LiteralPath (Join-Path $workspaceRoot 'correct-inherited-tests-build.log') -Encoding utf8
if ($testBuildExitCode -ne 0) {
    throw "Correct-control inherited test build failed with exit code $testBuildExitCode."
}
$testDll = Join-Path $testArtifacts 'bin\AgentLang.Business.Tests\release\AgentLang.Business.Tests.dll'
if (-not (Test-Path -LiteralPath $testDll -PathType Leaf)) {
    throw "Expected the freshly built inherited test DLL at $testDll."
}
$testArguments = @($testDll)
$testOutput = @(& dotnet @testArguments 2>&1 | ForEach-Object { $_.ToString() })
$testExitCode = $LASTEXITCODE
$testOutput | Set-Content -LiteralPath (Join-Path $workspaceRoot 'correct-inherited-tests.log') -Encoding utf8
if ($testExitCode -ne 0) {
    throw "Correct-control inherited test suite failed with exit code $testExitCode."
}

$sourceHashesAfter = [ordered]@{}
foreach ($path in $hashPaths) {
    $key = if ($path.StartsWith($repositoryRoot, [StringComparison]::OrdinalIgnoreCase)) { $path.Substring($repositoryRoot.Length).TrimStart('\') } else { $path }
    $sourceHashesAfter[$key] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}
foreach ($key in $sourceHashesBefore.Keys) {
    if ($sourceHashesBefore[$key] -ne $sourceHashesAfter[$key]) {
        throw "Frozen input changed during control run: $key"
    }
}

$preflight = [ordered]@{
    schemaVersion = 1
    status = 'passed'
    runId = $RunId
    workspace = $workspaceRoot
    operation = 'Store.customerPaidTotal'
    controls = $results
    inheritedTests = [ordered]@{
        buildExitCode = $testBuildExitCode
        buildArguments = $testBuildArguments
        runExitCode = $testExitCode
        runArguments = $testArguments
        buildLog = Join-Path $workspaceRoot 'correct-inherited-tests-build.log'
        runLog = Join-Path $workspaceRoot 'correct-inherited-tests.log'
        businessTestGroups = ($testOutput | Where-Object { $_ -match '^Business reference:' } | Select-Object -Last 1)
    }
    sourceSha256Before = $sourceHashesBefore
    sourceSha256After = $sourceHashesAfter
}
$preflightPath = Join-Path $evidenceRoot 'preflight-controls.json'
$preflight | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $preflightPath -Encoding utf8
$preflight | ConvertTo-Json -Depth 16
