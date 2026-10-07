#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$TracePath,
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$trace = if ([IO.Path]::IsPathRooted($TracePath)) { [IO.Path]::GetFullPath($TracePath) } else { [IO.Path]::GetFullPath((Join-Path $repo $TracePath)) }
$run = Split-Path -Parent $trace
$pinPath = Join-Path $run 'prelaunch.json'
$statePath = Join-Path $run 'starting-state.json'
$promptPath = Join-Path $run 'prompt.txt'
$studyId = 'business-policy-help-002'
$checks = [Collections.Generic.List[object]]::new()

function Check([bool]$Passed, [string]$Name) {
    $checks.Add([pscustomobject][ordered]@{ name=$Name; passed=$Passed })
    if (-not $Passed) { throw "Authoring-help trace audit failed: $Name" }
}

function Get-FullPath([string]$Path) {
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $repo $Path))
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-Sha256Bytes([byte[]]$Bytes) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
}

function Get-OptionalProperty([object]$Value, [string]$Name) {
    if ($null -eq $Value) { return $null }
    $property = $Value.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Assert-NoReparseTree([string]$Path) {
    $root = Get-Item -LiteralPath $Path -Force
    if (($root.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point in audited input: $Path" }
    foreach ($item in Get-ChildItem -LiteralPath $Path -Force -Recurse) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point in audited input: $($item.FullName)" }
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
    return @($rows | Sort-Object runtime,path)
}

function Get-CanonicalProjectRows([object[]]$Inventory) {
    return @($Inventory | ForEach-Object { [pscustomobject][ordered]@{path=$_.path;sha256=$_.sha256} } | Sort-Object path)
}

function Get-InventoryHash([object[]]$Rows) {
    $canonical = ConvertTo-Json -InputObject $Rows -Depth 100 -Compress
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($canonical)
    return Get-Sha256Bytes $bytes
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

if (-not (Test-Path -LiteralPath $pinPath -PathType Leaf) -or -not (Test-Path -LiteralPath $trace -PathType Leaf)) {
    throw "The frozen pin or trace is missing for $trace."
}
$pin = Get-Content -LiteralPath $pinPath -Raw | ConvertFrom-Json -Depth 100
$events = [Collections.Generic.List[object]]::new()
foreach ($line in Get-Content -LiteralPath $trace) {
    if ([string]::IsNullOrWhiteSpace($line)) { throw 'Trace contains an empty line.' }
    $events.Add((ConvertFrom-Json -InputObject $line -Depth 100))
}
$events = @($events.ToArray())
if ($events.Count -lt 2) { throw 'Trace must have session-start and session-end events.' }
$start = $events[0]
$end = $events[-1]
$sequenceIndex = if ($pin.taskId -ceq 'S06') { 2 } elseif ($pin.taskId -ceq 'S07') { 3 } else { -1 }
Check ($pin.schemaVersion -eq 1 -and $pin.studyId -ceq $studyId -and $pin.mode -ceq 'flat' -and $sequenceIndex -gt 0 -and $pin.sequenceIndex -eq $sequenceIndex) 'versioned identity and canonical sequence position'
Check ($pin.dirty -eq $false -and -not [string]::IsNullOrWhiteSpace($pin.sourceRevision)) 'pin records a clean committed source revision'
Check ($pin.model -ceq 'gpt-6-luna' -and $pin.reasoningEffort -ceq 'max' -and $pin.forkTurns -ceq 'none') 'pin declares Luna/max with no inherited turns'
Check ($pin.promptPath -ceq 'prompt.txt' -and $pin.startingStatePath -ceq [IO.Path]::GetRelativePath($repo, $statePath).Replace('\','/')) 'prompt and starting-state paths resolve from the audited run'
foreach ($expectedPath in @(
    @{ property='oraclePath'; value='experiments/AgentLang.SubagentTrials/business-policy-help-002/acceptance.json' },
    @{ property='independentVerifierPath'; value='scripts/Verify-AuthoringHelpTrial.ps1' },
    @{ property='preflightPath'; value='scripts/Verify-AuthoringHelpPreflight.ps1' },
    @{ property='preparerPath'; value='scripts/Prepare-AuthoringHelpTrial.ps1' },
    @{ property='freezerPath'; value='scripts/Freeze-AuthoringHelpTrial.ps1' },
    @{ property='auditorPath'; value='scripts/Audit-AuthoringHelpTrace.ps1' },
    @{ property='originalPreparerPath'; value='scripts/Prepare-BusinessPolicyTrial.ps1' },
    @{ property='hostPath'; value='scripts/Start-SubagentTrialHost.ps1' },
    @{ property='primerPath'; value='experiments/AgentLang.SubagentTrials/business-policy-help-002/language-primer.md' },
    @{ property='taskPath'; value="experiments/AgentLang.Benchmarks/task-bank/public/$($pin.taskId).json" },
    @{ property='seedSourcePath'; value='experiments/AgentLang.SubagentTrials/business-policy-help-002/artifacts/flat-customer.agent' }
)) {
    Check ([string]$pin.($expectedPath.property) -ceq $expectedPath.value) "canonical pinned path: $($expectedPath.property)"
}
Check ((Get-Sha256 $PSCommandPath) -ceq $pin.auditorSha256) 'running auditor is the pinned auditor version'
Check ((Get-Item -LiteralPath $promptPath).Length -eq $pin.promptUtf8Bytes -and (Get-Sha256 $promptPath) -ceq $pin.promptSha256) 'frozen public prompt bytes match pin'
Check ((Get-Sha256 $statePath) -ceq $pin.startingStateSha256) 'starting-state bytes match pin'

$preparedState = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json -Depth 100
$sequenceIndexInState = if ($pin.taskId -ceq 'S06') { 2 } else { 3 }
Check ($preparedState.studyId -ceq $studyId -and $preparedState.mode -ceq 'flat' -and $preparedState.taskId -ceq $pin.taskId -and $preparedState.sequenceIndex -eq $sequenceIndexInState) 'starting-state independent trial identity'
Check ($null -eq $preparedState.previousAcceptance) 'independent Flat starting state has no predecessor project'
Check ($preparedState.preparation.sourceRevision -ceq $pin.sourceRevision -and $preparedState.preparation.dirty -eq $false) 'starting state was prepared at the pinned clean revision'
Check ($preparedState.preparation.scriptSha256 -ceq $pin.preparerSha256 -and $preparedState.preparation.dependencySha256 -ceq $pin.originalPreparerSha256) 'starting-state preparation script hashes match pin'
$seedSummary = $preparedState.preparation.seedSummary
Check ($null -ne $seedSummary -and $seedSummary.authoredWordCount -eq 0 -and $seedSummary.typeCount -eq 6 -and $seedSummary.testCount -eq 0) 'prepared schema-only seed reported zero authored words, six types, and zero tests'

$revisionCheck = & git -C $repo cat-file -e "$($pin.sourceRevision)^{commit}" 2>&1
Check ($LASTEXITCODE -eq 0) 'pinned source revision exists in the local Git history'
$sourceSnapshotRoot = 'experiments/AgentLang.SubagentTrials/business-policy-help-002/artifacts/source-snapshot'
$requiredArtifacts = @(
    @{ path="$sourceSnapshotRoot/scripts/Prepare-AuthoringHelpTrial.ps1"; sourcePath='scripts/Prepare-AuthoringHelpTrial.ps1' },
    @{ path="$sourceSnapshotRoot/scripts/Freeze-AuthoringHelpTrial.ps1"; sourcePath='scripts/Freeze-AuthoringHelpTrial.ps1' },
    @{ path="$sourceSnapshotRoot/scripts/Verify-AuthoringHelpTrial.ps1"; sourcePath='scripts/Verify-AuthoringHelpTrial.ps1' },
    @{ path="$sourceSnapshotRoot/scripts/Verify-AuthoringHelpPreflight.ps1"; sourcePath='scripts/Verify-AuthoringHelpPreflight.ps1' },
    @{ path="$sourceSnapshotRoot/scripts/Audit-AuthoringHelpTrace.ps1"; sourcePath='scripts/Audit-AuthoringHelpTrace.ps1' },
    @{ path="$sourceSnapshotRoot/scripts/Prepare-BusinessPolicyTrial.ps1"; sourcePath='scripts/Prepare-BusinessPolicyTrial.ps1' },
    @{ path="$sourceSnapshotRoot/scripts/Start-SubagentTrialHost.ps1"; sourcePath='scripts/Start-SubagentTrialHost.ps1' },
    @{ path="$sourceSnapshotRoot/acceptance.json"; sourcePath='experiments/AgentLang.SubagentTrials/business-policy-help-002/acceptance.json' },
    @{ path="$sourceSnapshotRoot/acceptance-business-policy-001.json"; sourcePath='experiments/AgentLang.SubagentTrials/business-policy-001/acceptance.json' },
    @{ path="$sourceSnapshotRoot/public-task-S06.json"; sourcePath='experiments/AgentLang.Benchmarks/task-bank/public/S06.json' },
    @{ path="$sourceSnapshotRoot/public-task-S07.json"; sourcePath='experiments/AgentLang.Benchmarks/task-bank/public/S07.json' },
    @{ path="$sourceSnapshotRoot/language-primer.md"; sourcePath='experiments/AgentLang.SubagentTrials/business-policy-help-002/language-primer.md' },
    @{ path="$sourceSnapshotRoot/flat-customer.agent"; sourcePath='experiments/AgentLang.SubagentTrials/business-policy-help-002/artifacts/flat-customer.agent' }
)
Check (@($pin.sourceArtifacts).Count -eq $requiredArtifacts.Count) 'complete source artifact snapshot set'
foreach ($requiredArtifact in $requiredArtifacts) {
    $matchingArtifact = @($pin.sourceArtifacts | Where-Object { $_.path -ceq $requiredArtifact.path -and $_.sourcePath -ceq $requiredArtifact.sourcePath })
    Check ($matchingArtifact.Count -eq 1) "pinned source artifact exists: $($requiredArtifact.sourcePath)"
}
foreach ($artifact in $pin.sourceArtifacts) {
    $snapshotBytes = Get-GitSnapshotBytes ([string]$pin.sourceRevision) ([string]$artifact.path)
    $sourceBytes = Get-GitSnapshotBytes ([string]$pin.sourceRevision) ([string]$artifact.sourcePath)
    Check ($snapshotBytes.Length -eq $artifact.bytes -and (Get-Sha256Bytes $snapshotBytes) -ceq $artifact.sha256) "versioned source snapshot: $($artifact.path)"
    Check ($sourceBytes.Length -eq $artifact.bytes -and (Get-Sha256Bytes $sourceBytes) -ceq $artifact.sha256) "committed source input: $($artifact.sourcePath)"
    $workingSnapshot = Get-FullPath ([string]$artifact.path)
    $workingSource = Get-FullPath ([string]$artifact.sourcePath)
    Check ((Test-Path -LiteralPath $workingSnapshot -PathType Leaf) -and (Get-Item -LiteralPath $workingSnapshot).Length -eq $artifact.bytes -and (Get-Sha256 $workingSnapshot) -ceq $artifact.sha256) "working source snapshot matches its pinned bytes: $($artifact.path)"
    Check ((Test-Path -LiteralPath $workingSource -PathType Leaf) -and (Get-Item -LiteralPath $workingSource).Length -eq $artifact.bytes -and (Get-Sha256 $workingSource) -ceq $artifact.sha256) "working source input matches pinned bytes: $($artifact.sourcePath)"
}

foreach ($pinFile in @(
    @{ path=$pin.oraclePath; hash=$pin.oracleSha256; label='oracle' },
    @{ path=$pin.independentVerifierPath; hash=$pin.independentVerifierSha256; label='independent verifier' },
    @{ path=$pin.preflightPath; hash=$pin.preflightSha256; label='preflight verifier' },
    @{ path=$pin.preparerPath; hash=$pin.preparerSha256; label='new preparer' },
    @{ path=$pin.freezerPath; hash=$pin.freezerSha256; label='freezer' },
    @{ path=$pin.auditorPath; hash=$pin.auditorSha256; label='auditor snapshot' },
    @{ path=$pin.auditorPath; hash=$pin.auditorSha256; label='auditor' },
    @{ path=$pin.originalPreparerPath; hash=$pin.originalPreparerSha256; label='original preparation dependency' },
    @{ path=$pin.hostPath; hash=$pin.hostSha256; label='JSONL host' },
    @{ path=$pin.primerPath; hash=$pin.primerSha256; label='language primer' },
    @{ path=$pin.taskPath; hash=$pin.taskSha256; label='public task' },
    @{ path=$pin.seedSourcePath; hash=$pin.seedSourceSha256; label='schema-only seed source' }
)) {
    $snapshot = @($pin.sourceArtifacts | Where-Object { $_.sourcePath -ceq $pinFile.path -and $_.sha256 -ceq $pinFile.hash })
    Check ($snapshot.Count -eq 1) "pin resolves $($pinFile.label) through its committed source snapshot"
}

$oracleArtifact = @($pin.sourceArtifacts | Where-Object { $_.sourcePath -ceq $pin.oraclePath })
$oldAcceptancePath = 'experiments/AgentLang.SubagentTrials/business-policy-001/acceptance.json'
$oldOracleArtifact = @($pin.sourceArtifacts | Where-Object { $_.sourcePath -ceq $oldAcceptancePath })
if ($oracleArtifact.Count -ne 1 -or $oldOracleArtifact.Count -ne 1) { throw 'Oracle and predecessor oracle are missing from the pinned source snapshots.' }
$oracleBytes = Get-GitSnapshotBytes ([string]$pin.sourceRevision) ([string]$oracleArtifact[0].path)
$oldOracleBytes = Get-GitSnapshotBytes ([string]$pin.sourceRevision) ([string]$oldOracleArtifact[0].path)
Check ((Get-Sha256Bytes $oracleBytes) -ceq $pin.oracleSha256) 'oracle bytes resolve from the pinned Git snapshot'
$oracleNode = [System.Text.Json.Nodes.JsonNode]::Parse([Text.UTF8Encoding]::new($false, $true).GetString($oracleBytes))
$oldOracleNode = [System.Text.Json.Nodes.JsonNode]::Parse([Text.UTF8Encoding]::new($false, $true).GetString($oldOracleBytes))
Check ($oracleNode['studyId'].ToString() -ceq $studyId -and $oracleNode['predecessorSha256'].ToString() -ceq (Get-Sha256Bytes $oldOracleBytes)) 'oracle identity and predecessor hash'
Check ($oracleNode['defaults'].ToJsonString() -ceq $oldOracleNode['defaults'].ToJsonString()) 'oracle default fixture values are unchanged exactly'
$oracleTaskIds = @($oracleNode['tasks'].AsArray() | ForEach-Object { $_['id'].ToString() } | Sort-Object -CaseSensitive)
Check ($oracleTaskIds.Count -eq 2 -and ($oracleTaskIds -join '|') -ceq 'S06|S07') 'independent oracle contains only S06 and S07'
$caseCounts = [ordered]@{}
foreach ($taskId in @('S06','S07')) {
    $oldTask = @($oldOracleNode['tasks'].AsArray() | Where-Object { $_['id'].ToString() -ceq $taskId })[0]
    $newTask = @($oracleNode['tasks'].AsArray() | Where-Object { $_['id'].ToString() -ceq $taskId })[0]
    $expected = if ($taskId -eq 'S06') { 10 } else { 54 }
    Check ($null -ne $newTask -and $newTask['cases'].AsArray().Count -eq $expected -and $newTask['cases'].ToJsonString() -ceq $oldTask['cases'].ToJsonString()) "$taskId cases unchanged from predecessor"
    $caseCounts[$taskId] = $newTask['cases'].AsArray().Count
}

$seedArtifact = @($pin.sourceArtifacts | Where-Object { $_.sourcePath -ceq $pin.seedSourcePath })
if ($seedArtifact.Count -ne 1) { throw 'Schema-only seed source is missing from the pinned Git snapshot.' }
$seedBytes = Get-GitSnapshotBytes ([string]$pin.sourceRevision) ([string]$seedArtifact[0].path)
$seedText = [Text.UTF8Encoding]::new($false, $true).GetString($seedBytes)
Check ((Get-Sha256Bytes $seedBytes) -ceq $pin.seedSourceSha256) 'Flat seed source hash resolves from the pinned Git snapshot'
Check (([regex]::Matches($seedText, '(?m)^\s*(?:type|record)\s+')).Count -eq 6 -and
    ([regex]::Matches($seedText, '(?m)^\s*field\s+')).Count -eq 5 -and
    -not [regex]::IsMatch($seedText, '(?m)^\s*word\s+') -and
    -not [regex]::IsMatch($seedText, '(?i)premium|discount')) 'Flat seed source has only six types and five schema fields'
$stateSeedInputs = @($preparedState.sourceInputs)
Check ($stateSeedInputs.Count -eq 1 -and $stateSeedInputs[0].path -ceq $pin.seedSourcePath -and $stateSeedInputs[0].sha256 -ceq $pin.seedSourceSha256) 'starting state references the versioned schema-only seed source'

$project = Get-FullPath $pin.projectPath
$starting = [IO.Path]::GetFullPath([string]$preparedState.project.path)
$seedProject = Get-FullPath ([string]$preparedState.seed.path)
$actorInventory = Get-ProjectInventory $project
$startingInventory = Get-ProjectInventory $starting
$seedInventory = Get-ProjectInventory $seedProject
$actorRows = Get-CanonicalProjectRows $actorInventory
$startingRows = Get-CanonicalProjectRows $startingInventory
$seedRows = Get-CanonicalProjectRows $seedInventory
Check ((Get-InventoryHash $startingRows) -ceq $pin.startingProjectInventorySha256) 'starting-project inventory hash'
$pinnedRows = @($pin.startingProjectFiles | ForEach-Object { [pscustomobject][ordered]@{path=$_.path;sha256=$_.sha256} } | Sort-Object path)
Check ((ConvertTo-Json -InputObject $startingRows -Depth 100 -Compress) -ceq (ConvertTo-Json -InputObject $pinnedRows -Depth 100 -Compress)) 'starting-project files match frozen inventory'
Check ((Get-InventoryHash $startingRows) -ceq $preparedState.project.inventorySha256 -and $preparedState.actor.projectPath -ceq $pin.projectPath) 'starting-state project and actor identities match freeze'
Check ((Get-InventoryHash $seedRows) -ceq $preparedState.seed.inventorySha256 -and (ConvertTo-Json -InputObject $seedRows -Depth 100 -Compress) -ceq (ConvertTo-Json -InputObject $startingRows -Depth 100 -Compress)) 'schema-only seed project matches the starting project'
$seedFixture = Join-Path $starting 'fixtures/flat-customer.agent'
Check ((Get-Sha256 $seedFixture) -ceq $pin.seedSourceSha256) 'starting project contains the exact versioned Flat seed source'

$runtimeRoots = $pin.runtimeRoots
$runtimeCliDll = Get-FullPath $runtimeRoots.cliDllPath
$runtimeCliDir = Get-FullPath $runtimeRoots.cliDirectoryPath
$runtimeBusinessDll = Get-FullPath $runtimeRoots.businessDllPath
$runtimeBusinessDir = Get-FullPath $runtimeRoots.businessDirectoryPath
$preparedCliDll = Get-FullPath ([string]$preparedState.runtime.cliPath)
$preparedBusinessDll = Get-FullPath ([string]$preparedState.runtime.businessPath)
Check ([IO.Path]::GetDirectoryName($runtimeCliDll) -ieq $runtimeCliDir -and
    [IO.Path]::GetDirectoryName($runtimeBusinessDll) -ieq $runtimeBusinessDir) 'runtime DLLs are inside their pinned inventory roots'
$runtimeFiles = @(
    (Get-RuntimeInventory 'cli' $runtimeCliDir)
    (Get-RuntimeInventory 'business' $runtimeBusinessDir)
)
$runtimeFiles = @($runtimeFiles | Sort-Object runtime,path)
Check ($runtimeCliDll -ieq $preparedCliDll -and (Get-Sha256 $runtimeCliDll) -ceq $preparedState.runtime.cliSha256) 'pinned CLI DLL path and hash match prepared runtime'
Check ($runtimeBusinessDll -ieq $preparedBusinessDll -and (Get-Sha256 $runtimeBusinessDll) -ceq $preparedState.runtime.businessSha256) 'pinned Business DLL path and hash match prepared runtime'
Check ((ConvertTo-Json -InputObject $runtimeFiles -Depth 100 -Compress) -ceq (ConvertTo-Json -InputObject @($pin.runtimeFiles) -Depth 100 -Compress)) 'full CLI and Business runtime inventories'

Check ($start.event -ceq 'session-start' -and $start.schemaVersion -eq 1) 'schema-1 unbudgeted host session start'
Check ($start.projectPath -ceq $pin.projectPath -and $start.profile -ceq 'agentlang') 'host project and profile match freeze'
Check ($start.cliDll -ceq $runtimeCliDll) 'host loaded the pinned CLI assembly'
Check ($pin.allowedOperations -contains 'help' -and ($start.allowedOperations -contains 'help')) 'help is explicitly allowed by freeze and host'
Check ((@($start.allowedOperations | Sort-Object -CaseSensitive) -join '|') -ceq (@($pin.allowedOperations | Sort-Object -CaseSensitive) -join '|')) 'host operation allowlist matches freeze'
Check ($pin.maxExchanges -eq 100 -and $pin.exchangeTimeoutMilliseconds -eq 120000 -and $pin.maxRequestBytes -eq 262144 -and $pin.maxResponseBytes -eq 524288) 'frozen per-exchange limits match the v1 host contract'
Check ($start.limits.maxExchanges -eq $pin.maxExchanges -and $start.limits.exchangeTimeoutMilliseconds -eq $pin.exchangeTimeoutMilliseconds -and $start.limits.maxRequestBytes -eq $pin.maxRequestBytes -and $start.limits.maxResponseBytes -eq $pin.maxResponseBytes) 'host per-exchange limits match freeze'
Check (@($start.capabilities).Count -eq 0 -and @($pin.capabilities).Count -eq 0) 'host received no capabilities'
Check (@($start.additionalCliArguments).Count -eq 0 -and @($pin.additionalCliArguments).Count -eq 0) 'host received no additional CLI arguments'
Check ($pin.inspectionBudgetEnabled -eq $false -and $null -eq (Get-OptionalProperty $start 'inspectionBudget')) 'no cumulative inspection-response budget is enabled'
Check ($pin.inspectionClassifierVersion -ceq 'trial-host-inspection-v1') 'frozen classifier version is unchanged and unbudgeted for help'
Check (([DateTimeOffset]$pin.preparedAtUtc) -le ([DateTimeOffset]$start.startedUtc)) 'freeze predates host launch'

$actualCliFiles = @($start.cliFiles | ForEach-Object { [pscustomobject][ordered]@{name=$_.name;sha256=$_.sha256} } | Sort-Object name)
$cliDllName = [IO.Path]::GetFileName([string]$runtimeRoots.cliDllPath)
$cliBaseName = [IO.Path]::GetFileNameWithoutExtension($cliDllName)
$expectedCliFiles = @($pin.runtimeFiles | Where-Object {
    $_.runtime -ceq 'cli' -and $_.path -notmatch '/' -and
    ($_.path.EndsWith('.dll',[StringComparison]::OrdinalIgnoreCase) -or $_.path -ceq "$cliBaseName.deps.json" -or $_.path -ceq "$cliBaseName.runtimeconfig.json")
} | ForEach-Object { [pscustomobject][ordered]@{name=$_.path;sha256=$_.sha256} } | Sort-Object name)
Check ((ConvertTo-Json -InputObject $actualCliFiles -Depth 100 -Compress) -ceq (ConvertTo-Json -InputObject $expectedCliFiles -Depth 100 -Compress)) 'host-loaded CLI directory files match frozen inventory'

$cancelEvents = @($events | Where-Object { $_.event -ceq 'host-cancelled' })
$exchanges = @($events | Where-Object { $_.event -ceq 'exchange' })
$idleCancellation = $end.hostExitCode -eq 130 -and $cancelEvents.Count -eq 1 -and
    $cancelEvents[0].exchangeCount -eq $exchanges.Count -and
    @($exchanges | Where-Object { $_.executionState -ceq 'uncertain' -or $_.outcome -ceq 'cancelled' }).Count -eq 0 -and
    @($events | Where-Object { $_.event -ceq 'exchange-rejected' }).Count -eq 0 -and
    $events[-2].event -ceq 'host-cancelled'
$limitRejections = @($events | Where-Object { $_.event -ceq 'exchange-rejected' -and $_.code -ceq 'TRIAL_EXCHANGE_LIMIT' })
$hostLimitTermination = $end.hostExitCode -eq 2 -and $end.runtimeExitCode -eq 0 -and
    $exchanges.Count -eq $pin.maxExchanges -and $end.exchangeCount -eq ($pin.maxExchanges + 1) -and
    $limitRejections.Count -eq 1 -and @($events | Where-Object { $_.event -ceq 'exchange-rejected' }).Count -eq 1 -and
    $limitRejections[0].index -eq ($pin.maxExchanges + 1) -and $events[-2].event -ceq 'exchange-rejected'
if ($hostLimitTermination) {
    $rejectedWire = [Convert]::FromBase64String([string]$limitRejections[0].requestBase64)
    Check ($rejectedWire.Length -eq $limitRejections[0].requestUtf8Bytes -and
        $rejectedWire.Length -gt 1 -and $rejectedWire[-1] -eq 10 -and
        ($rejectedWire.Length - 1) -le $pin.maxRequestBytes) 'host-limit rejection records the complete bounded request line'
}
$terminationKind = if ($idleCancellation) { 'idle-ctrl-c' } elseif ($hostLimitTermination) { 'exchange-limit-rejection' } else { 'invalid-or-unrecorded-end' }
Check ($end.event -ceq 'session-end' -and ($idleCancellation -or $hostLimitTermination)) 'session ends by idle Ctrl+C or explicit exchange-limit rejection; normal EOF is invalid'
Check (($idleCancellation -and $end.exchangeCount -eq $exchanges.Count) -or
    ($hostLimitTermination -and $end.exchangeCount -eq ($pin.maxExchanges + 1)) ) 'terminal exchange count matches complete frames or explicit limit rejection'
Check ($exchanges.Count -le $pin.maxExchanges -and $end.runtimeExitCode -eq 0) 'runtime exited cleanly within the forwarded-exchange bound'
if ($exchanges.Count -gt 0) {
    $indices = @($exchanges | ForEach-Object { $_.index })
    Check ((($indices | Sort-Object) -join ',') -ceq ((1..$exchanges.Count) -join ',')) 'exchange indices are contiguous'
}

$requests = [Collections.Generic.List[object]]::new()
$diagnostics = [Collections.Generic.List[object]]::new()
$testBatches = [Collections.Generic.List[object]]::new()
$helpQueries = [Collections.Generic.List[object]]::new()
$requestBytes = 0L
$responseBytes = 0L
$sourceModifications = 0
$testExecutions = 0
foreach ($exchange in $exchanges) {
    foreach ($frame in @($exchange.request,$exchange.response)) {
        $wire = [Convert]::FromBase64String($frame.wireBase64)
        Check ($wire.Length -gt 0 -and $wire[-1] -eq 10 -and $wire.Length -eq $frame.wireUtf8Bytes -and $wire.Length - 1 -eq $frame.payloadUtf8Bytes -and (Get-Sha256Bytes $wire) -ceq $frame.sha256) "frame bytes and hash for exchange $($exchange.index)"
        $decodedWire = [Text.UTF8Encoding]::new($false, $true).GetBytes([string]$frame.rawLine + "`n")
        Check ((Get-Sha256Bytes $decodedWire) -ceq $frame.sha256) "decoded frame hash for exchange $($exchange.index)"
    }
    Check ($exchange.request.payloadUtf8Bytes -le $pin.maxRequestBytes -and $exchange.response.payloadUtf8Bytes -le $pin.maxResponseBytes) "per-exchange byte limits for exchange $($exchange.index)"
    $request = ConvertFrom-Json -InputObject $exchange.request.rawLine -Depth 100
    $response = ConvertFrom-Json -InputObject $exchange.response.rawLine -Depth 100
    $requestOp = [string](Get-OptionalProperty $request 'op')
    Check ([string]$exchange.operation -ceq $requestOp) "exchange operation matches request JSON for exchange $($exchange.index)"
    Check ($pin.allowedOperations -ccontains $requestOp) "request allowlist for exchange $($exchange.index)"
    Check ($null -eq (Get-OptionalProperty $exchange 'inspectionBudget')) "no inspection-response accounting for exchange $($exchange.index)"
    $requestBytes += $exchange.request.payloadUtf8Bytes
    $responseBytes += $exchange.response.payloadUtf8Bytes
    $requests.Add([pscustomobject][ordered]@{
        index = $exchange.index
        op = $requestOp
        requestBytes = $exchange.request.payloadUtf8Bytes
        responseBytes = $exchange.response.payloadUtf8Bytes
        ok = ((Get-OptionalProperty $response 'ok') -eq $true)
    })
    if ((Get-OptionalProperty $response 'ok') -ne $true) {
        $diagnostics.Add([pscustomobject][ordered]@{
            index = $exchange.index
            op = $requestOp
            error = Get-OptionalProperty $response 'error'
            text = Get-OptionalProperty $response 'text'
        })
    }
    if ($requestOp -ceq 'help') {
        $topicValue = Get-OptionalProperty $request 'topic'
        $topic = if ($null -eq $topicValue -or [string]::IsNullOrWhiteSpace([string]$topicValue)) { 'authoring' } else { [string]$topicValue }
        $helpQueries.Add([pscustomobject][ordered]@{ index=$exchange.index; topic=$topic; ok=((Get-OptionalProperty $response 'ok') -eq $true) })
    }
    if ((Get-OptionalProperty $response 'ok') -eq $true -and $requestOp -cin @('define','replace-word','commit','task.begin','task.commit')) { $sourceModifications++ }
    if ($requestOp -cin @('test','test-all')) {
        $data = Get-OptionalProperty $response 'data'
        $resultNode = Get-OptionalProperty $data 'results'
        $results = if ($null -eq $resultNode) { @() } else { @($resultNode) }
        $passed = @($results | Where-Object { $_.passed -eq $true }).Count
        $failed = @($results | Where-Object { $_.passed -ne $true }).Count
        $testExecutions += $results.Count
        $testBatches.Add([pscustomobject][ordered]@{
            index = $exchange.index
            op = $requestOp
            requestedWord = Get-OptionalProperty $request 'word'
            executions = $results.Count
            passed = $passed
            failed = $failed
            responseOk = ((Get-OptionalProperty $response 'ok') -eq $true)
        })
    }
}

$helpTopics = @($helpQueries | Select-Object -ExpandProperty topic -Unique | Sort-Object -CaseSensitive)
$testsPassed = if ($testBatches.Count -gt 0) { [long](@($testBatches | Measure-Object -Property passed -Sum)[0].Sum) } else { 0L }
$testsFailed = if ($testBatches.Count -gt 0) { [long](@($testBatches | Measure-Object -Property failed -Sum)[0].Sum) } else { 0L }
$output = if ([string]::IsNullOrWhiteSpace($OutputPath)) { Join-Path $run 'trace-audit.json' } else { Get-FullPath $OutputPath }
$runPrefix = $run.TrimEnd([char[]]@('\','/')) + [IO.Path]::DirectorySeparatorChar
if (-not $output.StartsWith($runPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Audit output must remain inside this trial run directory.' }
if (Test-Path -LiteralPath $output) { throw "Refusing to replace an existing trace audit: $output" }
$report = [ordered]@{
    schemaVersion = 1
    passed = $true
    studyId = $studyId
    mode = 'flat'
    taskId = $pin.taskId
    sourceRevision = $pin.sourceRevision
    sourceDirtyAtFreeze = $pin.dirty
    checks = @($checks)
    exchanges = $exchanges.Count
    requestPayloadBytes = $requestBytes
    responsePayloadBytes = $responseBytes
    diagnostics = @($diagnostics)
    requests = @($requests)
    traceSha256 = Get-Sha256 $trace
    sessionEnd = $end
    successfulSourceMutationRequests = $sourceModifications
    helpCalls = $helpQueries.Count
    helpTopicsQueried = $helpTopics
    helpQueries = @($helpQueries)
    testRequestBatches = @($testBatches)
    testExecutions = $testExecutions
    testsPassed = $testsPassed
    testsFailed = $testsFailed
    testCountStatus = 'counted runtime test results; repeated test and test-all batches count again'
    finalActorInventorySha256 = Get-InventoryHash $actorRows
    terminationKind = $terminationKind
    actorOwnedCtrlCObserved = [bool]$idleCancellation
    hostLimitTerminated = [bool]$hostLimitTermination
    hostLimitRejection = if ($hostLimitTermination) { [ordered]@{ index=$limitRejections[0].index; code=$limitRejections[0].code; requestUtf8Bytes=$limitRejections[0].requestUtf8Bytes } } else { $null }
    inspectionBudgetEnabled = $false
    declaredModel = [ordered]@{ model=$pin.model; reasoningEffort=$pin.reasoningEffort; forkTurns=$pin.forkTurns }
    auditSha256 = Get-Sha256 $PSCommandPath
    protocolDurationMilliseconds = ([DateTimeOffset]$end.finishedUtc - [DateTimeOffset]$start.startedUtc).TotalMilliseconds
    activeProtocolSpanMilliseconds = if ($exchanges.Count -gt 0) { ([DateTimeOffset]$exchanges[-1].atUtc - [DateTimeOffset]$start.startedUtc).TotalMilliseconds } else { $null }
    oracleSha256 = $pin.oracleSha256
    independentVerifierSha256 = $pin.independentVerifierSha256
    sourceArtifacts = @($pin.sourceArtifacts)
    limitations = @(
        'This is a trace integrity and protocol audit, not independent task acceptance.',
        'Model and reasoning settings are pinned declarations; this audit cannot independently prove the external agent runtime used them.',
        'A host-limit rejection is recorded as an auditable protocol termination, not as successful actor completion; a normal EOF has no v1 terminal event and fails this audit.',
        'Protocol duration includes the interval while the coordinator performs independent acceptance and teardown; active span ends with the final exchange. Both exclude setup before host start.',
        'The v1 inspection classifier does not include help. No cumulative inspection-response budget is enabled in this trial.',
        'Protocol bytes are not model token usage, model turns, or effective context windows.',
        'Two independent tasks do not establish a controlled efficiency comparison; no efficiency conclusion is made.'
    )
}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output)) | Out-Null
[IO.File]::WriteAllText($output, (ConvertTo-Json -InputObject $report -Depth 100) + "`n", [Text.UTF8Encoding]::new($false))
Write-Output "Audited $studyId flat-$($pin.taskId): $($exchanges.Count) exchanges, $($helpQueries.Count) help calls, $testExecutions test-result executions; termination=$terminationKind."
