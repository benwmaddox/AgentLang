#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('S06','S07')][string]$TaskId,
    [Parameter(Mandatory)][string]$CliDll,
    [Parameter(Mandatory)][string]$BusinessDll
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$studyId = 'business-policy-help-002'
$study = Join-Path $repo 'experiments/AgentLang.SubagentTrials/business-policy-help-002'
$localRoot = Join-Path $repo '.agentlang/business-policy-help-002'
$runRoot = Join-Path $repo '.agentlang/business-policy-help-002/runs'
$actorName = "flat-$TaskId"
$actorPath = Join-Path $localRoot "actors/$actorName"
$runPath = Join-Path $runRoot $actorName
$seedSource = Join-Path $study 'artifacts/flat-customer.agent'
$localSeedSource = Join-Path $localRoot 'flat-customer.agent'
$acceptancePath = Join-Path $study 'acceptance.json'
$taskPath = Join-Path $repo "experiments/AgentLang.Benchmarks/task-bank/public/$TaskId.json"
$primerPath = Join-Path $study 'language-primer.md'
$originalPreparer = Join-Path $repo 'scripts/Prepare-BusinessPolicyTrial.ps1'

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

foreach ($input in @($seedSource, $acceptancePath, $taskPath, $primerPath, $originalPreparer)) {
    if (-not (Test-Path -LiteralPath $input -PathType Leaf)) {
        throw "Required versioned trial input is missing: $input"
    }
}
foreach ($target in @($actorPath, $runPath)) {
    if (Test-Path -LiteralPath $target) {
        throw "Fresh trial destination already exists; refusing to replace it: $target"
    }
}

$oracle = [System.Text.Json.Nodes.JsonNode]::Parse([IO.File]::ReadAllText($acceptancePath))
if ($oracle['studyId'].ToString() -cne $studyId -or
    $oracle['predecessorSha256'].ToString() -cne (Get-Sha256 (Join-Path $repo 'experiments/AgentLang.SubagentTrials/business-policy-001/acceptance.json'))) {
    throw 'The versioned acceptance corpus identity or predecessor hash is invalid.'
}
$taskIds = @($oracle['tasks'].AsArray() | ForEach-Object { $_['id'].ToString() } | Sort-Object -CaseSensitive)
if ($taskIds.Count -ne 2 -or ($taskIds -join '|') -cne 'S06|S07') {
    throw 'The independent acceptance corpus must contain only the two fresh Flat tasks S06 and S07.'
}
$oracleTask = @($oracle['tasks'].AsArray() | Where-Object { $_['id'].ToString() -ceq $TaskId })
if ($oracleTask.Count -ne 1) {
    throw "The versioned acceptance corpus does not contain exactly one $TaskId task."
}
$expectedCases = if ($TaskId -eq 'S06') { 10 } else { 54 }
if ($oracleTask[0]['cases'].AsArray().Count -ne $expectedCases) {
    throw "$TaskId must retain its $expectedCases frozen acceptance cases."
}

$sourceBytes = [IO.File]::ReadAllBytes($seedSource)
$sourceHash = Get-Sha256 $seedSource
if (Test-Path -LiteralPath $localSeedSource) {
    if ((Get-Sha256 $localSeedSource) -cne $sourceHash) {
        throw "Existing local Flat seed source differs from the archived source: $localSeedSource"
    }
}
else {
    [IO.Directory]::CreateDirectory($localRoot) | Out-Null
    [IO.File]::WriteAllBytes($localSeedSource, $sourceBytes)
}
$seedText = [IO.File]::ReadAllText($localSeedSource)
$declaredTypes = ([regex]::Matches($seedText, '(?m)^\s*(?:type|record)\s+')).Count
if ($declaredTypes -ne 6 -or ([regex]::Matches($seedText, '(?m)^\s*field\s+')).Count -ne 5 -or
    [regex]::IsMatch($seedText, '(?m)^\s*word\s+') -or
    [regex]::IsMatch($seedText, '(?i)premium|discount')) {
    throw 'The archived Flat seed must contain six type declarations, five Customer fields, and no authored policy word.'
}

# The historical preparer supplies the validated schema-only Flat seed and
# exact project/runtime inventories. Unique roots keep this trial isolated.
$prepareResultText = (& $originalPreparer `
    -Mode flat `
    -TaskId $TaskId `
    -CliDll $CliDll `
    -BusinessDll $BusinessDll `
    -LocalRoot $localRoot `
    -RunRoot $runRoot | Out-String).Trim()
$prepareResult = ConvertFrom-Json -InputObject $prepareResultText -Depth 30
if ($prepareResult.mode -cne 'flat' -or $prepareResult.taskId -cne $TaskId -or
    $prepareResult.authoredWordCount -ne 0 -or $prepareResult.typeCount -ne 6 -or $prepareResult.testCount -ne 0) {
    throw 'The prepared seed must report zero authored words, exactly six nominal types, and zero tests.'
}

$startingStatePath = Join-Path $runPath 'starting-state.json'
if (-not (Test-Path -LiteralPath $startingStatePath -PathType Leaf)) {
    throw 'The original preparer returned without a starting-state.json.'
}
$startingState = Get-Content -LiteralPath $startingStatePath -Raw | ConvertFrom-Json -Depth 100
if ($startingState.mode -cne 'flat' -or $startingState.taskId -cne $TaskId -or
    $startingState.sequenceIndex -ne $(if ($TaskId -eq 'S06') { 2 } else { 3 })) {
    throw 'The original preparer returned the wrong Flat task or canonical sequence index.'
}
$startingState.studyId = $studyId
$startingState.sourceInputs = @(
    [pscustomobject]@{
        path = 'experiments/AgentLang.SubagentTrials/business-policy-help-002/artifacts/flat-customer.agent'
        sha256 = $sourceHash
    }
)
$sourceRevision = (& git -C $repo rev-parse HEAD | Out-String).Trim()
$workingChanges = (& git -C $repo status --porcelain --untracked-files=all | Out-String)
$startingState.preparation = [pscustomobject]@{
    scriptPath = 'scripts/Prepare-AuthoringHelpTrial.ps1'
    scriptSha256 = Get-Sha256 $PSCommandPath
    dependencyPath = 'scripts/Prepare-BusinessPolicyTrial.ps1'
    dependencySha256 = Get-Sha256 $originalPreparer
    sourceRevision = $sourceRevision
    dirty = -not [string]::IsNullOrWhiteSpace($workingChanges)
    seedSummary = [pscustomobject][ordered]@{
        authoredWordCount = [int]$prepareResult.authoredWordCount
        typeCount = [int]$prepareResult.typeCount
        testCount = [int]$prepareResult.testCount
    }
}
$startingState | ConvertTo-Json -Depth 100 | ForEach-Object {
    [IO.File]::WriteAllText($startingStatePath, $_ + "`n", [Text.UTF8Encoding]::new($false))
}

[pscustomobject]@{
    schemaVersion = 1
    studyId = $studyId
    mode = 'flat'
    taskId = $TaskId
    sequenceIndex = $startingState.sequenceIndex
    actorProject = $actorPath
    startingProject = Join-Path $runPath 'starting-project'
    startingState = $startingStatePath
    acceptancePath = $acceptancePath
    acceptanceSha256 = Get-Sha256 $acceptancePath
    predecessorSha256 = $oracle['predecessorSha256'].ToString()
    seedSourcePath = $seedSource
    seedSourceSha256 = $sourceHash
    originalPreparerPath = $originalPreparer
    originalPreparerSha256 = Get-Sha256 $originalPreparer
} | ConvertTo-Json -Depth 20
