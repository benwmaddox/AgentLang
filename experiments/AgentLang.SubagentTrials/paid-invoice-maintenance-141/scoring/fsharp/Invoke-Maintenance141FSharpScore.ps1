[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $CandidateProject,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string] $RunId,

    [string] $OraclePath
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..\..')).Path
$studyRoot = Join-Path $repositoryRoot 'experiments\AgentLang.SubagentTrials\paid-invoice-maintenance-141'
$scorerProject = Join-Path $PSScriptRoot 'AgentLang.Maintenance141.FSharpScorer.fsproj'
$evidenceRoot = Join-Path $repositoryRoot '.agentlang\maintenance-141\fsharp-scoring'
$runRoot = Join-Path (Join-Path $evidenceRoot 'runs') $RunId
$artifactRoot = Join-Path $runRoot 'artifacts'

if ([string]::IsNullOrWhiteSpace($OraclePath)) {
    $OraclePath = Join-Path $studyRoot 'oracle.json'
}
$candidatePath = (Resolve-Path $CandidateProject).Path
$oracleFullPath = (Resolve-Path $OraclePath).Path
if (-not (Test-Path -LiteralPath $candidatePath -PathType Leaf)) {
    throw "Candidate project file not found: $candidatePath"
}
if (-not (Test-Path -LiteralPath $oracleFullPath -PathType Leaf)) {
    throw "Oracle file not found: $oracleFullPath"
}
if (Test-Path -LiteralPath $runRoot) {
    throw "Evidence run already exists; choose a fresh RunId: $runRoot"
}
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null

$buildArguments = @(
    'build', $scorerProject,
    '--artifacts-path', $artifactRoot,
    '-c', 'Release',
    "-p:CandidateProject=$candidatePath",
    '-p:NuGetAudit=false',
    '-p:BuildInParallel=false',
    '-m:1'
)
$buildOutput = @(& dotnet @buildArguments 2>&1 | ForEach-Object { $_.ToString() })
$buildExitCode = $LASTEXITCODE
$buildOutput | Set-Content -LiteralPath (Join-Path $runRoot 'build.log') -Encoding utf8
if ($buildExitCode -ne 0) {
    throw "Scorer build failed with exit code $buildExitCode. See $(Join-Path $runRoot 'build.log')."
}

$scorerDll = Join-Path $artifactRoot 'bin\AgentLang.Maintenance141.FSharpScorer\release\AgentLang.Maintenance141.FSharpScorer.dll'
if (-not (Test-Path -LiteralPath $scorerDll -PathType Leaf)) {
    throw "Expected the freshly built scorer DLL at $scorerDll."
}
$scoreArguments = @($scorerDll, '--oracle', $oracleFullPath)
$scoreOutput = @(& dotnet @scoreArguments 2>&1 | ForEach-Object { $_.ToString() })
$scoreExitCode = $LASTEXITCODE
$scoreOutput | Set-Content -LiteralPath (Join-Path $runRoot 'scorer.log') -Encoding utf8

$marker = 'MAINTENANCE141_SCORE_JSON:'
$scoreLines = @($scoreOutput | Where-Object { $_.StartsWith($marker, [StringComparison]::Ordinal) })
if ($scoreExitCode -ne 0) {
    throw "Scorer process failed with exit code $scoreExitCode. See $(Join-Path $runRoot 'scorer.log')."
}
if ($scoreLines.Count -ne 1) {
    throw "Scorer returned $($scoreLines.Count) JSON responses; expected one. See $(Join-Path $runRoot 'scorer.log')."
}
$scoreJson = $scoreLines[0].Substring($marker.Length)
$score = $scoreJson | ConvertFrom-Json
if ($score.schemaVersion -ne 1 -or $score.responseCount -ne 4 -or $score.cases.Count -ne 4) {
    throw "Scorer response contract failed: schema=$($score.schemaVersion), responses=$($score.responseCount), cases=$($score.cases.Count)."
}
[System.IO.File]::WriteAllText((Join-Path $runRoot 'result.json'), $scoreJson + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))

$candidateBusiness = Join-Path (Split-Path -Parent $candidatePath) 'Business.fs'
$testsProgram = Join-Path (Split-Path -Parent (Split-Path -Parent $candidatePath)) 'tests\Program.fs'
$sourceHashes = [ordered]@{}
foreach ($path in @($candidatePath, $candidateBusiness, $testsProgram, $oracleFullPath, $scorerProject, (Join-Path $PSScriptRoot 'Program.fs'), $PSCommandPath)) {
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        $relativeKey = if ($path.StartsWith($repositoryRoot, [StringComparison]::OrdinalIgnoreCase)) { $path.Substring($repositoryRoot.Length).TrimStart('\') } else { $path }
        $sourceHashes[$relativeKey] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

$commandRecord = [ordered]@{
    runId = $RunId
    candidateProject = $candidatePath
    oraclePath = $oracleFullPath
    build = [ordered]@{ executable = 'dotnet'; arguments = $buildArguments; exitCode = $buildExitCode }
    scorer = [ordered]@{ executable = 'dotnet'; arguments = $scoreArguments; exitCode = $scoreExitCode; responseCount = $score.responseCount; behaviorPassed = [bool]$score.passed }
    sourceSha256 = $sourceHashes
}
$commandRecord | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $runRoot 'command.json') -Encoding utf8

[pscustomobject]@{
    RunId = $RunId
    RunRoot = $runRoot
    ResultPath = Join-Path $runRoot 'result.json'
    BehaviorPassed = [bool]$score.passed
    ResponseCount = [int]$score.responseCount
    CaseResults = @($score.cases | ForEach-Object { [pscustomobject]@{ Id = $_.id; Passed = [bool]$_.passed } })
}
