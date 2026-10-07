#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('S06','S07')][string]$TaskId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$studyId = 'business-policy-help-002'
$study = Join-Path $repo 'experiments/AgentLang.SubagentTrials/business-policy-help-002'
$localRoot = Join-Path $repo '.agentlang/business-policy-help-002'
$run = Join-Path $repo ".agentlang/business-policy-help-002/runs/flat-$TaskId"
$project = Join-Path $localRoot "actors/flat-$TaskId"
$starting = Join-Path $run 'starting-project'
$statePath = Join-Path $run 'starting-state.json'
$promptPath = Join-Path $run 'prompt.txt'
$pinPath = Join-Path $run 'prelaunch.json'
$tracePath = Join-Path $run 'trace.jsonl'
$seedSourcePath = Join-Path $study 'artifacts/flat-customer.agent'
$archiveRoot = Join-Path $study 'artifacts/source-snapshot'

function Get-FullPath([string]$Path) {
    return [IO.Path]::GetFullPath($Path)
}

function Get-RelativePath([string]$Path) {
    $relative = [IO.Path]::GetRelativePath($repo, (Get-FullPath $Path))
    if ($relative -eq '..' -or $relative.StartsWith('..' + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal) -or [IO.Path]::IsPathRooted($relative)) {
        throw "Path escapes the repository: $Path"
    }
    return $relative.Replace('\','/')
}

function Resolve-RepoPath([string]$Path) {
    if ([IO.Path]::IsPathRooted($Path)) { return Get-FullPath $Path }
    return Get-FullPath (Join-Path $repo $Path)
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-NoReparseTree([string]$Path) {
    $root = Get-Item -LiteralPath $Path -Force
    if (($root.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Reparse points are not allowed in frozen inputs: $Path"
    }
    foreach ($item in Get-ChildItem -LiteralPath $Path -Force -Recurse) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Reparse points are not allowed in frozen inputs: $($item.FullName)"
        }
    }
}

function Get-ProjectInventory([string]$Path) {
    Assert-NoReparseTree $Path
    $rows = [Collections.Generic.List[object]]::new()
    foreach ($item in Get-ChildItem -LiteralPath $Path -File -Force -Recurse) {
        $relative = [IO.Path]::GetRelativePath($Path, $item.FullName).Replace('\','/')
        $segments = $relative.Split('/')
        if ($segments -contains 'bin' -or $segments -contains 'obj') { continue }
        $rows.Add([pscustomobject][ordered]@{
            path = $relative
            bytes = $item.Length
            sha256 = Get-Sha256 $item.FullName
        })
    }
    return @($rows | Sort-Object path)
}

function Get-RuntimeInventory([string]$Runtime, [string]$Path) {
    Assert-NoReparseTree $Path
    $rows = [Collections.Generic.List[object]]::new()
    foreach ($item in Get-ChildItem -LiteralPath $Path -File -Force -Recurse) {
        $relative = [IO.Path]::GetRelativePath($Path, $item.FullName).Replace('\','/')
        $rows.Add([pscustomobject][ordered]@{
            runtime = $Runtime
            path = $relative
            bytes = $item.Length
            sha256 = Get-Sha256 $item.FullName
        })
    }
    if ($rows.Count -eq 0) { throw "Pinned $Runtime runtime directory has no files: $Path" }
    return @($rows | Sort-Object runtime,path)
}

function Get-CanonicalProjectRows([object[]]$Inventory) {
    return @($Inventory | ForEach-Object { [pscustomobject][ordered]@{path=$_.path;sha256=$_.sha256} } | Sort-Object path)
}

function Get-InventoryHash([object[]]$Rows) {
    $canonical = ConvertTo-Json -InputObject $Rows -Depth 100 -Compress
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($canonical)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Get-GitSnapshotBytes([string]$Revision, [string]$Path) {
    $spec = "$Revision`:$Path"
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'git'
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.ArgumentList.Add('-C')
    $startInfo.ArgumentList.Add($repo)
    $startInfo.ArgumentList.Add('show')
    $startInfo.ArgumentList.Add('--no-ext-diff')
    $startInfo.ArgumentList.Add($spec)
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { throw "Could not read Git snapshot $spec." }
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $memory = [IO.MemoryStream]::new()
    try {
        $process.StandardOutput.BaseStream.CopyTo($memory)
        $process.WaitForExit()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "Could not read Git snapshot $spec`: $stderr" }
        return ,$memory.ToArray()
    }
    finally {
        $memory.Dispose()
        $process.Dispose()
    }
}

function Get-SourceArchiveMap {
    $root = 'experiments/AgentLang.SubagentTrials/business-policy-help-002/artifacts/source-snapshot'
    return @(
        [pscustomobject][ordered]@{ path="$root/scripts/Prepare-AuthoringHelpTrial.ps1"; sourcePath='scripts/Prepare-AuthoringHelpTrial.ps1' },
        [pscustomobject][ordered]@{ path="$root/scripts/Freeze-AuthoringHelpTrial.ps1"; sourcePath='scripts/Freeze-AuthoringHelpTrial.ps1' },
        [pscustomobject][ordered]@{ path="$root/scripts/Verify-AuthoringHelpTrial.ps1"; sourcePath='scripts/Verify-AuthoringHelpTrial.ps1' },
        [pscustomobject][ordered]@{ path="$root/scripts/Verify-AuthoringHelpPreflight.ps1"; sourcePath='scripts/Verify-AuthoringHelpPreflight.ps1' },
        [pscustomobject][ordered]@{ path="$root/scripts/Audit-AuthoringHelpTrace.ps1"; sourcePath='scripts/Audit-AuthoringHelpTrace.ps1' },
        [pscustomobject][ordered]@{ path="$root/scripts/Prepare-BusinessPolicyTrial.ps1"; sourcePath='scripts/Prepare-BusinessPolicyTrial.ps1' },
        [pscustomobject][ordered]@{ path="$root/scripts/Start-SubagentTrialHost.ps1"; sourcePath='scripts/Start-SubagentTrialHost.ps1' },
        [pscustomobject][ordered]@{ path="$root/acceptance.json"; sourcePath='experiments/AgentLang.SubagentTrials/business-policy-help-002/acceptance.json' },
        [pscustomobject][ordered]@{ path="$root/acceptance-business-policy-001.json"; sourcePath='experiments/AgentLang.SubagentTrials/business-policy-001/acceptance.json' },
        [pscustomobject][ordered]@{ path="$root/public-task-S06.json"; sourcePath='experiments/AgentLang.Benchmarks/task-bank/public/S06.json' },
        [pscustomobject][ordered]@{ path="$root/public-task-S07.json"; sourcePath='experiments/AgentLang.Benchmarks/task-bank/public/S07.json' },
        [pscustomobject][ordered]@{ path="$root/language-primer.md"; sourcePath='experiments/AgentLang.SubagentTrials/business-policy-help-002/language-primer.md' },
        [pscustomobject][ordered]@{ path="$root/flat-customer.agent"; sourcePath='experiments/AgentLang.SubagentTrials/business-policy-help-002/artifacts/flat-customer.agent' }
    )
}

if (-not (Test-Path -LiteralPath $run -PathType Container)) { throw "Prepared run is missing: $run" }
if (Test-Path -LiteralPath $pinPath) { throw "Refusing to replace a frozen trial: $pinPath" }
if (Test-Path -LiteralPath $promptPath) { throw "Refusing to replace a frozen prompt: $promptPath" }
if (Test-Path -LiteralPath $tracePath) { throw "Refusing to freeze a previously launched trial: $tracePath" }
foreach ($required in @($project, $starting, $statePath, (Join-Path $study 'acceptance.json'), (Join-Path $study 'language-primer.md'))) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Missing prepared or versioned artifact: $required" }
}

$gitHead = (& git -C $repo rev-parse HEAD | Out-String).Trim()
$gitChanges = (& git -C $repo status --porcelain --untracked-files=all | Out-String)
if ([string]::IsNullOrWhiteSpace($gitHead) -or -not [string]::IsNullOrWhiteSpace($gitChanges)) {
    throw 'A real trial can only be frozen from a clean committed implementation snapshot.'
}
$sourceRevision = $gitHead
$sourceDirty = $false

$preparedState = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json -Depth 100
$sequenceIndex = if ($TaskId -eq 'S06') { 2 } else { 3 }
if ($preparedState.schemaVersion -ne 1 -or $preparedState.studyId -cne $studyId -or $preparedState.mode -cne 'flat' -or
    $preparedState.taskId -cne $TaskId -or $preparedState.sequenceIndex -ne $sequenceIndex -or
    $preparedState.project.path -cne $starting -or $preparedState.actor.projectPath -cne $project -or
    $null -ne $preparedState.previousAcceptance) {
    throw 'Prepared state does not match the independent Flat task identity and canonical sequence position.'
}
if ($null -eq $preparedState.preparation -or $preparedState.preparation.dirty -ne $false -or
    $preparedState.preparation.sourceRevision -cne $sourceRevision -or
    $preparedState.preparation.scriptSha256 -cne (Get-Sha256 (Join-Path $repo 'scripts/Prepare-AuthoringHelpTrial.ps1')) -or
    $preparedState.preparation.dependencySha256 -cne (Get-Sha256 (Join-Path $repo 'scripts/Prepare-BusinessPolicyTrial.ps1'))) {
    throw 'Starting project was not prepared by the committed trial scripts at this clean source revision.'
}
$seedSummary = $preparedState.preparation.seedSummary
if ($null -eq $seedSummary -or $seedSummary.authoredWordCount -ne 0 -or $seedSummary.typeCount -ne 6 -or $seedSummary.testCount -ne 0) {
    throw 'Prepared Flat seed must report zero authored words, six nominal types, and zero tests.'
}
$seedSourceHash = Get-Sha256 $seedSourcePath
$seedText = [IO.File]::ReadAllText($seedSourcePath)
if (([regex]::Matches($seedText, '(?m)^\s*(?:type|record)\s+')).Count -ne 6 -or
    ([regex]::Matches($seedText, '(?m)^\s*field\s+')).Count -ne 5 -or
    [regex]::IsMatch($seedText, '(?m)^\s*word\s+') -or
    [regex]::IsMatch($seedText, '(?i)premium|discount')) {
    throw 'The versioned Flat seed is not the six-declaration schema-only Customer project.'
}
$sourceInputs = @($preparedState.sourceInputs)
if ($sourceInputs.Count -ne 1 -or $sourceInputs[0].path -cne 'experiments/AgentLang.SubagentTrials/business-policy-help-002/artifacts/flat-customer.agent' -or $sourceInputs[0].sha256 -cne $seedSourceHash) {
    throw 'Prepared state does not point to the exact versioned schema-only source input.'
}
$seedProject = Resolve-RepoPath ([string]$preparedState.seed.path)
$seedInventory = Get-ProjectInventory $seedProject
$seedRows = Get-CanonicalProjectRows $seedInventory
if ((Get-InventoryHash $seedRows) -cne $preparedState.seed.inventorySha256) {
    throw 'Prepared seed project inventory changed before freeze.'
}

$startingInventory = Get-ProjectInventory $starting
$actorInventory = Get-ProjectInventory $project
$startingRows = Get-CanonicalProjectRows $startingInventory
$actorRows = Get-CanonicalProjectRows $actorInventory
if ((ConvertTo-Json -InputObject $startingRows -Depth 100 -Compress) -cne (ConvertTo-Json -InputObject $actorRows -Depth 100 -Compress)) {
    throw 'Actor project differs from the archived schema-only starting project.'
}
if ((ConvertTo-Json -InputObject $startingRows -Depth 100 -Compress) -cne (ConvertTo-Json -InputObject $seedRows -Depth 100 -Compress)) {
    throw 'Flat project does not match the independent schema-only seed inventory.'
}
foreach ($pair in @(@{actual=$startingRows;recorded=$preparedState.project}, @{actual=$actorRows;recorded=$preparedState.actor})) {
    $recordedRows = @($pair.recorded.files | ForEach-Object { [pscustomobject][ordered]@{path=$_.path;sha256=$_.sha256} } | Sort-Object path)
    if ((ConvertTo-Json -InputObject $pair.actual -Depth 100 -Compress) -cne (ConvertTo-Json -InputObject $recordedRows -Depth 100 -Compress) -or
        (Get-InventoryHash $pair.actual) -cne $pair.recorded.inventorySha256) {
        throw 'Prepared project inventory changed before freeze.'
    }
}

$oldOraclePath = Join-Path $repo 'experiments/AgentLang.SubagentTrials/business-policy-001/acceptance.json'
$oraclePath = Join-Path $study 'acceptance.json'
$oracle = [System.Text.Json.Nodes.JsonNode]::Parse([IO.File]::ReadAllText($oraclePath))
$oldOracle = [System.Text.Json.Nodes.JsonNode]::Parse([IO.File]::ReadAllText($oldOraclePath))
if ($oracle['studyId'].ToString() -cne $studyId -or $oracle['predecessorSha256'].ToString() -cne (Get-Sha256 $oldOraclePath) -or
    $oracle['defaults'].ToJsonString() -cne $oldOracle['defaults'].ToJsonString()) {
    throw 'The versioned oracle identity, predecessor hash, or exact defaults differ from its source corpus.'
}
$oracleTaskIds = @($oracle['tasks'].AsArray() | ForEach-Object { $_['id'].ToString() } | Sort-Object -CaseSensitive)
if ($oracleTaskIds.Count -ne 2 -or ($oracleTaskIds -join '|') -cne 'S06|S07') {
    throw 'The frozen independent acceptance corpus must contain only S06 and S07.'
}
foreach ($caseTask in @('S06','S07')) {
    $oldTask = @($oldOracle['tasks'].AsArray() | Where-Object { $_['id'].ToString() -ceq $caseTask })[0]
    $newTask = @($oracle['tasks'].AsArray() | Where-Object { $_['id'].ToString() -ceq $caseTask })[0]
    if ($null -eq $oldTask -or $null -eq $newTask -or $oldTask['cases'].ToJsonString() -cne $newTask['cases'].ToJsonString()) {
        throw "Acceptance cases for $caseTask differ from the untouched predecessor corpus."
    }
}

$taskPath = Join-Path $repo "experiments/AgentLang.Benchmarks/task-bank/public/$TaskId.json"
$task = [System.Text.Json.Nodes.JsonNode]::Parse([IO.File]::ReadAllText($taskPath))
if ($task['id'].ToString() -cne $TaskId) { throw 'Public task JSON id does not match the requested task.' }
$goal = $task['goal'].ToString()
$contract = $task['requiredPublicContract']
$symbol = $contract['symbol'].ToString()
$signature = $contract['signature'].ToString()
$moneyRule = if ($TaskId -eq 'S07') {
    'The premium result is truncated toward zero after applying 90%; every signed Int64 value is valid, including negatives and both limits. Nonpremium returns the input unchanged.'
} else { '' }

$allowed = @(
    'callers','commit','context','define','dependencies','describe','diff','effects','eval','example','examples','failed-tests',
    'graph','help','history','ir','replace-word','search','search-dependency','search-output','search-type','source','task.begin',
    'task.commit','task.log','task.status','test','test-all','tests','transitive-callers','transitive-dependencies','type-of','words'
)
$allowed = @($allowed | Sort-Object -CaseSensitive)
$cliPath = Resolve-RepoPath ([string]$preparedState.runtime.cliPath)
$businessPath = Resolve-RepoPath ([string]$preparedState.runtime.businessPath)
foreach ($input in @(@{label='CLI';path=$cliPath},@{label='Business';path=$businessPath})) {
    if (-not (Test-Path -LiteralPath $input.path -PathType Leaf)) { throw "Pinned $($input.label) runtime is missing: $($input.path)" }
}
if ((Get-Sha256 $cliPath) -cne $preparedState.runtime.cliSha256 -or (Get-Sha256 $businessPath) -cne $preparedState.runtime.businessSha256) {
    throw 'Prepared CLI or Business assembly changed before freeze.'
}
$cliDirectory = Split-Path -Parent $cliPath
$businessDirectory = Split-Path -Parent $businessPath
$runtimeRoots = [pscustomobject][ordered]@{
    cliDllPath = Get-RelativePath $cliPath
    cliDirectoryPath = Get-RelativePath $cliDirectory
    businessDllPath = Get-RelativePath $businessPath
    businessDirectoryPath = Get-RelativePath $businessDirectory
}
$runtimeFiles = @(
    (Get-RuntimeInventory 'cli' $cliDirectory)
    (Get-RuntimeInventory 'business' $businessDirectory)
)
$runtimeFiles = @($runtimeFiles | Sort-Object runtime,path)

$artifactMap = Get-SourceArchiveMap
if (-not (Test-Path -LiteralPath $archiveRoot -PathType Container)) {
    throw "Committed source snapshots are required before freeze: $archiveRoot"
}
Assert-NoReparseTree $archiveRoot
$sourceArtifacts = [Collections.Generic.List[object]]::new()
foreach ($artifact in $artifactMap) {
    $snapshotPath = Resolve-RepoPath $artifact.path
    $sourcePath = Resolve-RepoPath $artifact.sourcePath
    foreach ($file in @($snapshotPath,$sourcePath)) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Required source snapshot is missing: $file" }
    }
    $snapshotHash = Get-Sha256 $snapshotPath
    $sourceHash = Get-Sha256 $sourcePath
    if ($snapshotHash -cne $sourceHash) { throw "Archived source differs from its source file: $($artifact.sourcePath)" }
    $committedSnapshotBytes = Get-GitSnapshotBytes $sourceRevision $artifact.path
    $committedSourceBytes = Get-GitSnapshotBytes $sourceRevision $artifact.sourcePath
    if ($committedSnapshotBytes.Length -ne (Get-Item -LiteralPath $snapshotPath).Length -or
        ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($committedSnapshotBytes)).ToLowerInvariant()) -cne $snapshotHash -or
        $committedSourceBytes.Length -ne (Get-Item -LiteralPath $sourcePath).Length -or
        ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($committedSourceBytes)).ToLowerInvariant()) -cne $sourceHash) {
        throw "Archived source does not match the exact committed Git snapshot: $($artifact.sourcePath)"
    }
    $sourceArtifacts.Add([pscustomobject][ordered]@{
        path = $artifact.path
        sourcePath = $artifact.sourcePath
        bytes = (Get-Item -LiteralPath $snapshotPath).Length
        sha256 = $snapshotHash
    })
}

$preparerPath = Join-Path $repo 'scripts/Prepare-AuthoringHelpTrial.ps1'
$freezerPath = Join-Path $repo 'scripts/Freeze-AuthoringHelpTrial.ps1'
$verifierPath = Join-Path $repo 'scripts/Verify-AuthoringHelpTrial.ps1'
$preflightPath = Join-Path $repo 'scripts/Verify-AuthoringHelpPreflight.ps1'
$auditorPath = Join-Path $repo 'scripts/Audit-AuthoringHelpTrace.ps1'
$hostPath = Join-Path $repo 'scripts/Start-SubagentTrialHost.ps1'
$originalPreparerPath = Join-Path $repo 'scripts/Prepare-BusinessPolicyTrial.ps1'
$primerPath = Join-Path $study 'language-primer.md'
$command = "& '$($hostPath.Replace("'","''"))' -CliDll '$($cliPath.Replace("'","''"))' -ProjectPath '$($project.Replace("'","''"))' -TracePath '$($tracePath.Replace("'","''"))' -AllowedOperations @('" + ($allowed -join "','") + "') -Profile agentlang -Capabilities @() -ClockValue '2000-01-01T00:00:00Z' -MaxRequestBytes 262144 -MaxResponseBytes 524288 -ExchangeTimeoutMilliseconds 120000 -MaxExchanges 100"
$prompt = @"
Run $studyId flat-$TaskId as a fresh gpt-6-luna/max coding agent with no inherited turns.
Your assigned project is $project.
You own definitions and their attached tests/examples in this project through the supplied JSONL protocol. Use Flow authoring. Every reusable helper must have passing own tests and full own instruction/branch coverage before library commit. Add meaningful boundary and branch tests, concise documentation, and a runnable example for the required operation. End with durable task.commit. Preserve the existing types, source, and tests. All operations for this task are pure; the host grants no effects.

You are an external coding agent in a disposable synthetic project. Inspect available capabilities before editing. Customer.Kind/kind is raw data: comparison is ordinal and case-sensitive. Do not trim or lowercase it. Money is signed Int64 minor units; Float is not an allowed implementation of money. Every signed Int64 value is valid, including both limits.
$moneyRule

Human authorization covers these isolated project edits and local tests:
"Yes, run the remaining isolated experiments"

Do not read coordinator source, independent acceptance, sibling projects, archived prior solutions, or other raw repository files. Do not use arbitrary shell commands or raw file tools after starting the host. If automatic approval rejects an action, report it and stop dependent work; do not bypass it. You are not alone in the checkout; preserve other work. Do not spawn or delegate. Observation timeout is not execution failure: resume the same live handle and inspect state before repeating an uncertain edit. LLM token usage, model turns, and effective context windows are not measured.

Task $TaskId
$goal
Required operation: $symbol : $signature
Customer fields are id/email/kind/balance/created-at. The Customer is a public record with strict nominal fields; direct construction preserves raw Kind. Construction helpers that trim Kind do not define this task's policy.

$([IO.File]::ReadAllText($primerPath).TrimEnd())

Execute exactly the following PowerShell command with exec_command, workdir $repo, sandbox_permissions require_escalated, tty true, and a short initial yield. The known sandbox ACL helper requires this host execution for the authorized experiment. Do not issue other shell/file commands.

$command

No startup banner is printed. Send one JSONL request per line on the same write_stdin session. After successful completion, report the host session ID and leave it live until independent acceptance completes. After acceptance, tear down your own session with Ctrl+C through write_stdin, without an extra JSONL request. Do not send quit commands or EOF. Report actual tests, errors, and missing capabilities.
"@
$prompt = $prompt.Replace("`r`n", "`n")
$promptBytes = [Text.UTF8Encoding]::new($false).GetBytes($prompt)

$oracleHash = Get-Sha256 $oraclePath
$preparerHash = Get-Sha256 $preparerPath
$freezerHash = Get-Sha256 $freezerPath
$verifierHash = Get-Sha256 $verifierPath
$auditorHash = Get-Sha256 $auditorPath
$hostHash = Get-Sha256 $hostPath
$originalPreparerHash = Get-Sha256 $originalPreparerPath
$primerHash = Get-Sha256 $primerPath
$seedHash = Get-Sha256 $seedSourcePath
$taskHash = Get-Sha256 $taskPath
$startingStateHash = Get-Sha256 $statePath
$promptHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($promptBytes)).ToLowerInvariant()

$pin = [ordered]@{
    schemaVersion = 1
    studyId = $studyId
    mode = 'flat'
    taskId = $TaskId
    sequenceIndex = $sequenceIndex
    preparedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    sourceRevision = $sourceRevision
    dirty = $sourceDirty
    model = 'gpt-6-luna'
    reasoningEffort = 'max'
    forkTurns = 'none'
    projectPath = $project
    launchCommand = $command
    allowedOperations = $allowed
    maxExchanges = 100
    maxRequestBytes = 262144
    maxResponseBytes = 524288
    exchangeTimeoutMilliseconds = 120000
    profile = 'agentlang'
    additionalCliArguments = @()
    capabilities = @()
    clockValue = '2000-01-01T00:00:00Z'
    inspectionBudgetEnabled = $false
    inspectionClassifierVersion = 'trial-host-inspection-v1'
    promptPath = 'prompt.txt'
    promptSha256 = $promptHash
    promptUtf8Bytes = $promptBytes.Length
    oraclePath = Get-RelativePath $oraclePath
    oracleSha256 = $oracleHash
    independentVerifierPath = Get-RelativePath $verifierPath
    independentVerifierSha256 = $verifierHash
    preflightPath = Get-RelativePath $preflightPath
    preflightSha256 = Get-Sha256 $preflightPath
    preparerPath = Get-RelativePath $preparerPath
    preparerSha256 = $preparerHash
    freezerPath = Get-RelativePath $freezerPath
    freezerSha256 = $freezerHash
    auditorPath = Get-RelativePath $auditorPath
    auditorSha256 = $auditorHash
    originalPreparerPath = Get-RelativePath $originalPreparerPath
    originalPreparerSha256 = $originalPreparerHash
    hostPath = Get-RelativePath $hostPath
    hostSha256 = $hostHash
    primerPath = Get-RelativePath $primerPath
    primerSha256 = $primerHash
    taskPath = Get-RelativePath $taskPath
    taskSha256 = $taskHash
    seedSourcePath = Get-RelativePath $seedSourcePath
    seedSourceSha256 = $seedHash
    runtimeRoots = $runtimeRoots
    runtimeFiles = $runtimeFiles
    startingProjectFiles = $startingInventory
    startingProjectInventorySha256 = Get-InventoryHash $startingRows
    startingStatePath = Get-RelativePath $statePath
    startingStateSha256 = $startingStateHash
    sourceArtifacts = @($sourceArtifacts.ToArray())
}

$pinBytes = [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $pin -Depth 100) + "`n")
$temporaryPrompt = "$promptPath.$([guid]::NewGuid().ToString('N')).tmp"
$temporaryPin = "$pinPath.$([guid]::NewGuid().ToString('N')).tmp"
try {
    [IO.File]::WriteAllBytes($temporaryPrompt, $promptBytes)
    [IO.File]::WriteAllBytes($temporaryPin, $pinBytes)
    [IO.File]::Move($temporaryPrompt, $promptPath)
    [IO.File]::Move($temporaryPin, $pinPath)
}
finally {
    foreach ($temporary in @($temporaryPrompt,$temporaryPin)) {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}

Write-Output "Frozen $studyId flat-$TaskId at clean source revision $sourceRevision with help explicitly allowed and no cumulative inspection-response budget."
