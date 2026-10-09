#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('flat')][string]$Mode = 'flat',
    [ValidateSet('S06','S07')][string]$TaskId = 'S06',
    [string]$ProjectPath,
    [string]$CliDll,
    [string]$EvidencePath,
    [string]$StartingProjectPath,
    [switch]$RequireFrozenPin
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$sequence = @('S01','S06','S07')
$expectedCounts = @{ S06 = 10; S07 = 54 }
$checks = [Collections.Generic.List[object]]::new()
$metadataChecks = [Collections.Generic.List[object]]::new()
$behaviorChecks = [Collections.Generic.List[object]]::new()
$checkCategory = 'metadata'
$caseResults = [Collections.Generic.List[object]]::new()
$sessions = [Collections.Generic.List[object]]::new()
$processRuns = [Collections.Generic.List[object]]::new()
$passed = $false
$metadataPassed = $false
$behaviorPassed = $false
$safetyStop = $null
$failure = $null
$targetOracle = $null
$oracle = $null
$oracleHash = $null
$caseOracleEvidence = @()
$metadataTestExecutions = 0
$oraclePath = Join-Path $repo 'experiments/AgentLang.SubagentTrials/business-policy-help-002/acceptance.json'
$resolvedProject = $null
$resolvedStartingProject = $null
$originalProjectPath = $null
$originalStartingProjectPath = $null
$resolvedCli = $null
$scratchProject = $null
$testsRoot = Join-Path $repo '.agentlang/business-policy-help-002/tests'
$actorInventoryBefore = @()
$actorInventoryAfter = @()
$preservation = [ordered]@{ mode=$Mode; checked=$false; sourceWords=0; nominalTypes=0; unchanged=$null }
$reuse = [ordered]@{ candidatePriorTaskSymbols=@(); priorTaskSymbols=$null; startingDictionaryTaskSymbols=$null; directDependencies=@(); transitiveDependencies=@(); reusedPriorTaskSymbols=$null; status='unavailable until starting inventory and dependency graph are checked' }
$runtimeInfo = [ordered]@{}
$evidenceTarget = $null
$startingStateMetadata = $null
$startingStatePassed = $false
$evidenceCollision = $false
$checkFrozenPin = $false
$frozenPinInfo = [ordered]@{required=[bool]$RequireFrozenPin;checked=$false;status='not checked';path=$null;sha256=$null}
$frozenPinRecord = $null
$orchestrationError = $null
$actorHashBefore = $null
$actorHashAfter = $null
$requestedEvidenceTarget = $null
$startedUtc = [DateTime]::UtcNow.ToString('O')
$utf8NoBom = [Text.UTF8Encoding]::new($false)

function Get-Field($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    if ($Object -is [Collections.IDictionary]) { return $Object[$Name] }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Add-Check([string]$Name, [bool]$Condition, [object]$Details = $null) {
    $entry = [ordered]@{ name=$Name; passed=$Condition }
    if ($null -ne $Details) { $entry.details = $Details }
    $checks.Add($entry)
    if ($script:checkCategory -ceq 'behavior') { $behaviorChecks.Add($entry) } else { $metadataChecks.Add($entry) }
}

function Add-MetadataFailure([string]$Name, [object]$ErrorRecord) {
    $message = if ($ErrorRecord -is [Management.Automation.ErrorRecord]) { [string]$ErrorRecord.Exception.Message } else { [string]$ErrorRecord }
    if ([string]::IsNullOrWhiteSpace($message)) { $message = 'Unspecified verifier failure.' }
    Add-Check $Name $false $message | Out-Null
}

function Get-ProtocolError($Response) {
    $errorObject = Get-Field $Response 'error'
    foreach ($candidate in @(
        (Get-Field $errorObject 'message'),
        (Get-Field $errorObject 'text'),
        (Get-Field $Response 'text'),
        (Get-Field $Response 'message'),
        $errorObject
    )) {
        if ($null -eq $candidate) { continue }
        if ($candidate -is [string] -and -not [string]::IsNullOrWhiteSpace($candidate)) { return $candidate }
        if ($candidate -isnot [string]) {
            $text = ConvertTo-Json -InputObject $candidate -Depth 30 -Compress
            if (-not [string]::IsNullOrWhiteSpace($text) -and $text -cne 'null') { return $text }
        }
    }
    return 'Protocol returned ok=false without error.message/text.'
}

function Resolve-RepoPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'A required path was empty.' }
    if ([IO.Path]::IsPathFullyQualified($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath($Path, $repo)
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-RelativeInventory([string]$Root) {
    $resolved = [IO.Path]::GetFullPath($Root)
    $rows = [Collections.Generic.List[object]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $resolved -Recurse -Force -File) {
        $relative = [IO.Path]::GetRelativePath($resolved, $file.FullName).Replace('\','/')
        if ($relative -match '(^|/)(bin|obj)(/|$)') { continue }
        $rows.Add([pscustomobject][ordered]@{ path=$relative; sha256=(Get-Sha256 $file.FullName) })
    }
    return @($rows | Sort-Object path)
}

function Get-FreezeInventory([string]$Root) {
    $resolved = [IO.Path]::GetFullPath($Root)
    $rows = [Collections.Generic.List[object]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $resolved -Recurse -Force -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } | Sort-Object FullName) {
        $rows.Add([ordered]@{
            path=[IO.Path]::GetRelativePath($resolved,$file.FullName).Replace('\','/')
            bytes=$file.Length
            sha256=Get-Sha256 $file.FullName
        })
    }
    return @($rows)
}

function Get-RuntimeInventory([string]$Root, [string]$Runtime) {
    $resolved = [IO.Path]::GetFullPath($Root)
    $rows = [Collections.Generic.List[object]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $resolved -Recurse -Force -File | Sort-Object FullName) {
        $rows.Add([pscustomobject][ordered]@{
            runtime=$Runtime
            path=[IO.Path]::GetRelativePath($resolved,$file.FullName).Replace('\','/')
            bytes=$file.Length
            sha256=Get-Sha256 $file.FullName
        })
    }
    return @($rows)
}

function Get-CanonicalJson($Value) {
    return ConvertTo-Json -InputObject $Value -Depth 100 -Compress
}

function Get-UniqueEvidencePath([string]$PreferredPath, [string]$Task, [string]$Reason) {
    $directory = Join-Path $repo 'reports/evidence'
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    for ($attempt=0; $attempt -lt 20; $attempt++) {
        $candidate = Join-Path $directory ("authoring-help-flat-$($Task.ToLowerInvariant())-$Reason-$([Guid]::NewGuid().ToString('N')).json")
        if (-not (Test-Path -LiteralPath $candidate)) { return $candidate }
    }
    throw "Could not allocate a new evidence file for $PreferredPath."
}

function Write-NewTextFile([string]$Path, [string]$Content) {
    $bytes = $utf8NoBom.GetBytes($Content)
    $stream = [IO.FileStream]::new($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $stream.Write($bytes,0,$bytes.Length); $stream.Flush($true) }
    finally { $stream.Dispose() }
}

function Get-GitBlobSha256([string]$Revision, [string]$RelativePath) {
    if ($RelativePath -match '^(?:[A-Za-z]:|/|\\)' -or $RelativePath -match '(^|[\\/])\.\.([\\/]|$)') {
        throw "Pinned source artifact path is not repository-relative: $RelativePath"
    }
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'git'
    $start.WorkingDirectory = $repo
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add('-C'); $start.ArgumentList.Add($repo)
    $start.ArgumentList.Add('show'); $start.ArgumentList.Add("${Revision}:$RelativePath")
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (-not $process.Start()) { throw "Could not inspect pinned Git artifact '$RelativePath'." }
    $bytes = [IO.MemoryStream]::new()
    $copyTask = $process.StandardOutput.BaseStream.CopyToAsync($bytes)
    $errorTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(30000)) {
        try { $process.Kill($true) } catch { }
        throw "Timed out reading pinned Git artifact '$RelativePath'."
    }
    $copyTask.GetAwaiter().GetResult()
    $gitError = $errorTask.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) { throw "Pinned Git artifact '$RelativePath' is unavailable: $gitError" }
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes.ToArray())).ToLowerInvariant()
    $bytes.Dispose()
    $process.Dispose()
    return $hash
}

function Validate-FrozenPin([string]$StartPath, [string]$ActorPath, [string]$RuntimeCliPath, [string]$CurrentMode, [string]$CurrentTask, $Acceptance) {
    $runDirectory = Split-Path -Parent $StartPath
    $pinPath = Join-Path $runDirectory 'prelaunch.json'
    $frozenPinInfo.path = $pinPath
    if (-not (Test-Path -LiteralPath $pinPath -PathType Leaf)) {
        if ($RequireFrozenPin) {
            Add-Check 'required frozen prelaunch pin exists' $false $pinPath
        }
        $checks.Add([ordered]@{name='unfrozen verifier run classified as preflight/control';passed=$true;details='No sibling prelaunch.json was supplied.'})
        $frozenPinInfo.status = 'unfrozen-preflight-or-control'
        return $null
    }
    $pinCheckStart = $metadataChecks.Count
    $pinRaw = Get-Content -LiteralPath $pinPath -Raw
    $pin = $pinRaw | ConvertFrom-Json -AsHashtable -Depth 100
    # ConvertFrom-Json can coerce ISO-like strings into DateTime values. Read this
    # protocol string from its JSON token so validation remains exact and invariant.
    $pinClockValue = $null
    $pinDocument = [System.Text.Json.JsonDocument]::Parse($pinRaw)
    try {
        $clockElement = [System.Text.Json.JsonElement]::new()
        if ($pinDocument.RootElement.TryGetProperty('clockValue', [ref]$clockElement) -and $clockElement.ValueKind -eq [System.Text.Json.JsonValueKind]::String) {
            $pinClockValue = $clockElement.GetString()
        }
    } finally {
        $pinDocument.Dispose()
    }
    $pinHash = Get-Sha256 $pinPath
    $frozenPinInfo.sha256 = $pinHash
    $expectedIndex = [array]::IndexOf($sequence,$CurrentTask) + 1
    Add-Check 'frozen pin schema, study, mode, task, and sequence match' ([int]$pin.schemaVersion -eq 1 -and [string]$pin.studyId -ceq [string]$Acceptance.studyId -and [string]$pin.mode -ceq $CurrentMode -and [string]$pin.taskId -ceq $CurrentTask -and [int]$pin.sequenceIndex -eq $expectedIndex)
    $pinnedActor = [string](Get-Field $pin 'actorProjectPath')
    if ([string]::IsNullOrWhiteSpace($pinnedActor)) { $pinnedActor = [string](Get-Field $pin 'projectPath') }
    $actorPathMatches = [IO.Path]::IsPathFullyQualified($pinnedActor) -and [IO.Path]::GetFullPath($pinnedActor) -ieq [IO.Path]::GetFullPath($ActorPath)
    Add-Check 'frozen pin actor project path matches argument' $actorPathMatches @{expected=$ActorPath;actual=$pinnedActor}

    $statePath = Join-Path $runDirectory 'starting-state.json'
    Add-Check 'frozen run starting-state exists' (Test-Path -LiteralPath $statePath -PathType Leaf) $statePath
    $stateHash = Get-Sha256 $statePath
    Add-Check 'frozen starting-state hash matches pin' ($stateHash -ceq ([string]$pin.startingStateSha256).ToLowerInvariant()) @{expected=$pin.startingStateSha256;actual=$stateHash}
    $promptPath = Join-Path $runDirectory 'prompt.txt'
    Add-Check 'frozen actor prompt exists' (Test-Path -LiteralPath $promptPath -PathType Leaf) $promptPath
    $promptHash = Get-Sha256 $promptPath
    Add-Check 'frozen prompt hash matches pin' ($promptHash -ceq ([string]$pin.promptSha256).ToLowerInvariant()) @{expected=$pin.promptSha256;actual=$promptHash}
    Add-Check 'frozen prompt byte count matches pin' ((Get-Item -LiteralPath $promptPath).Length -eq [long]$pin.promptUtf8Bytes)
    Add-Check 'frozen oracle hash matches acceptance corpus' ((Get-Sha256 $oraclePath) -ceq ([string]$pin.oracleSha256).ToLowerInvariant())
    $verifierPath = Join-Path $repo 'scripts/Verify-AuthoringHelpTrial.ps1'
    $hostPath = Join-Path $repo 'scripts/Start-SubagentTrialHost.ps1'
    $pinnedVerifierPath = [string](Get-Field $pin 'independentVerifierPath')
    if ([string]::IsNullOrWhiteSpace($pinnedVerifierPath)) { $pinnedVerifierPath = 'scripts/Verify-AuthoringHelpTrial.ps1' }
    Add-Check 'frozen verifier path matches versioned verifier' ([IO.Path]::GetFullPath((Resolve-RepoPath $pinnedVerifierPath)) -ieq [IO.Path]::GetFullPath($verifierPath)) @{expected=$verifierPath;actual=$pinnedVerifierPath}
    Add-Check 'frozen verifier hash matches current verifier' ((Get-Sha256 $verifierPath) -ceq ([string]$pin.independentVerifierSha256).ToLowerInvariant())
    $preflightPath = Join-Path $repo 'scripts/Verify-AuthoringHelpPreflight.ps1'
    $pinnedPreflightPath = [string](Get-Field $pin 'preflightPath')
    Add-Check 'frozen preflight path and hash match the versioned preflight' ([IO.Path]::GetFullPath((Resolve-RepoPath $pinnedPreflightPath)) -ieq [IO.Path]::GetFullPath($preflightPath) -and (Test-Path -LiteralPath $preflightPath -PathType Leaf) -and (Get-Sha256 $preflightPath) -ceq ([string](Get-Field $pin 'preflightSha256')).ToLowerInvariant()) @{expected=$preflightPath;actual=$pinnedPreflightPath;sha256=(Get-Field $pin 'preflightSha256')}
    Add-Check 'frozen host hash matches current protocol host' ((Get-Sha256 $hostPath) -ceq ([string]$pin.hostSha256).ToLowerInvariant())
    $allowedOperations = @((Get-Field $pin 'allowedOperations') | ForEach-Object { [string]$_ })
    $expectedAllowedOperations = @(
        'callers','commit','context','define','dependencies','describe','diff','effects','eval','example','examples','failed-tests',
        'graph','help','history','ir','replace-word','search','search-dependency','search-output','search-type','source','task.begin',
        'task.commit','task.log','task.status','test','test-all','tests','transitive-callers','transitive-dependencies','type-of','words'
    ) | Sort-Object -CaseSensitive
    Add-Check 'frozen actor allowlist exactly matches the reviewed help-enabled operation set' ((($allowedOperations | Sort-Object -CaseSensitive) -join "`0") -ceq ($expectedAllowedOperations -join "`0")) $allowedOperations
    Add-Check 'frozen actor model, reasoning, and fork settings match the trial' ([string](Get-Field $pin 'model') -ceq 'gpt-6-luna' -and [string](Get-Field $pin 'reasoningEffort') -ceq 'max' -and [string](Get-Field $pin 'forkTurns') -ceq 'none') @{model=(Get-Field $pin 'model');reasoningEffort=(Get-Field $pin 'reasoningEffort');forkTurns=(Get-Field $pin 'forkTurns')}
    Add-Check 'frozen host settings retain raw-protocol limits and no cumulative inspection budget' ([int](Get-Field $pin 'maxExchanges') -eq 100 -and [int](Get-Field $pin 'maxRequestBytes') -eq 262144 -and [int](Get-Field $pin 'maxResponseBytes') -eq 524288 -and [int](Get-Field $pin 'exchangeTimeoutMilliseconds') -eq 120000 -and (Get-Field $pin 'inspectionBudgetEnabled') -eq $false -and [string](Get-Field $pin 'inspectionClassifierVersion') -ceq 'trial-host-inspection-v1' -and [string](Get-Field $pin 'profile') -ceq 'agentlang' -and $pinClockValue -ceq '2000-01-01T00:00:00Z' -and @(Get-Field $pin 'additionalCliArguments').Count -eq 0 -and @(Get-Field $pin 'capabilities').Count -eq 0)
    $pinnedRevision = [string]$pin.sourceRevision
    $revisionResolves = $pinnedRevision -match '^[0-9a-fA-F]{40}$'
    if ($revisionResolves) {
        $resolvedRevision = (& git -C $repo rev-parse --verify "$pinnedRevision^{commit}" 2>$null | Out-String).Trim()
        $revisionResolves = $LASTEXITCODE -eq 0 -and $resolvedRevision -ceq $pinnedRevision
    }
    Add-Check 'frozen source revision is a committed clean-source snapshot' ($revisionResolves -and $pin.dirty -eq $false) @{pinned=$pinnedRevision;resolves=$revisionResolves;pinDirty=$pin.dirty}

    $startInventory = Get-FreezeInventory $StartPath
    $pinnedStart = @(Get-Field $pin 'startingProjectFiles')
    Add-Check 'frozen starting project file inventory matches' ((Get-CanonicalJson @($startInventory)) -ceq (Get-CanonicalJson @($pinnedStart))) @{files=$startInventory.Count}
    $runtimeRoots = Get-Field $pin 'runtimeRoots'
    $cliDllRelative = [string](Get-Field $runtimeRoots 'cliDllPath')
    $cliDirectoryRelative = [string](Get-Field $runtimeRoots 'cliDirectoryPath')
    $businessDllRelative = [string](Get-Field $runtimeRoots 'businessDllPath')
    $businessDirectoryRelative = [string](Get-Field $runtimeRoots 'businessDirectoryPath')
    $cliDllPath = Resolve-RepoPath $cliDllRelative
    $cliDirectoryPath = Resolve-RepoPath $cliDirectoryRelative
    $businessDllPath = Resolve-RepoPath $businessDllRelative
    $businessDirectoryPath = Resolve-RepoPath $businessDirectoryRelative
    $expectedLaunchCommand = "& '$($hostPath.Replace("'","''"))' -CliDll '$($cliDllPath.Replace("'","''"))' -ProjectPath '$($ActorPath.Replace("'","''"))' -TracePath '$((Join-Path $runDirectory 'trace.jsonl').Replace("'","''"))' -AllowedOperations @('" + ($expectedAllowedOperations -join "','") + "') -Profile agentlang -Capabilities @() -ClockValue '2000-01-01T00:00:00Z' -MaxRequestBytes 262144 -MaxResponseBytes 524288 -ExchangeTimeoutMilliseconds 120000 -MaxExchanges 100"
    Add-Check 'frozen launch command matches pinned host, project, CLI, and allowlist settings' ([string](Get-Field $pin 'launchCommand') -ceq $expectedLaunchCommand) @{expected=$expectedLaunchCommand;actual=(Get-Field $pin 'launchCommand')}
    $unsafeRuntimePaths = @(@($cliDllRelative,$cliDirectoryRelative,$businessDllRelative,$businessDirectoryRelative) | Where-Object { [string]::IsNullOrWhiteSpace($_) -or [IO.Path]::IsPathFullyQualified($_) -or $_ -match '(^|[\\/])\.\.([\\/]|$)' })
    $runtimePathsSafe = $unsafeRuntimePaths.Count -eq 0 -and (Test-Within $cliDllPath $repo) -and (Test-Within $cliDirectoryPath $repo) -and (Test-Within $businessDllPath $repo) -and (Test-Within $businessDirectoryPath $repo)
    $cliRootOk = $runtimePathsSafe -and (Test-Path -LiteralPath $cliDllPath -PathType Leaf) -and (Test-Path -LiteralPath $cliDirectoryPath -PathType Container) -and [IO.Path]::GetFullPath($cliDllPath) -ieq [IO.Path]::GetFullPath($RuntimeCliPath) -and [IO.Path]::GetFullPath($cliDirectoryPath) -ieq [IO.Path]::GetFullPath((Split-Path -Parent $RuntimeCliPath))
    $stateRuntime = if ($null -ne $startingStateMetadata) { Get-Field $startingStateMetadata.data 'runtime' } else { $null }
    $stateBusinessPath = [string](Get-Field $stateRuntime 'businessPath')
    if ([string]::IsNullOrWhiteSpace($stateBusinessPath)) { $stateBusinessPath = [string](Get-Field $stateRuntime 'businessDllPath') }
    $stateBusinessHash = [string](Get-Field $stateRuntime 'businessSha256')
    $businessRootOk = $runtimePathsSafe -and (Test-Path -LiteralPath $businessDllPath -PathType Leaf) -and (Test-Path -LiteralPath $businessDirectoryPath -PathType Container) -and [IO.Path]::GetFullPath($businessDllPath) -ieq [IO.Path]::GetFullPath((Resolve-RepoPath $stateBusinessPath)) -and [IO.Path]::GetFullPath($businessDirectoryPath) -ieq [IO.Path]::GetFullPath((Split-Path -Parent $businessDllPath)) -and (Get-Sha256 $businessDllPath) -ceq $stateBusinessHash.ToLowerInvariant()
    Add-Check 'frozen CLI and Business runtime roots match starting-state pins' ($cliRootOk -and $businessRootOk) @{cliDll=$cliDllPath;cliDirectory=$cliDirectoryPath;businessDll=$businessDllPath;businessDirectory=$businessDirectoryPath;startingBusinessPath=$stateBusinessPath;startingBusinessSha256=$stateBusinessHash}
    $runtimeInventory = [Collections.Generic.List[object]]::new()
    foreach ($runtime in @(@{name='cli';root=$cliDirectoryPath},@{name='business';root=$businessDirectoryPath})) {
        if (-not (Test-Path -LiteralPath $runtime.root -PathType Container)) { continue }
        foreach ($row in (Get-RuntimeInventory $runtime.root $runtime.name)) { $runtimeInventory.Add($row) }
    }
    $runtimeRows = @($runtimeInventory | Sort-Object runtime,path)
    $pinnedRuntime = @(Get-Field $pin 'runtimeFiles' | Sort-Object runtime,path)
    Add-Check 'frozen CLI and Business runtime file inventories match' ((Get-CanonicalJson @($runtimeRows)) -ceq (Get-CanonicalJson @($pinnedRuntime))) @{cliDirectory=$cliDirectoryPath;businessDirectory=$businessDirectoryPath;files=$runtimeRows.Count}

    $sourceArtifacts = @(Get-Field $pin 'sourceArtifacts')
    $snapshotRoot = 'experiments/AgentLang.SubagentTrials/business-policy-help-002/artifacts/source-snapshot/'
    $requiredSourcePaths = @(
        'scripts/Prepare-AuthoringHelpTrial.ps1',
        'scripts/Freeze-AuthoringHelpTrial.ps1',
        'scripts/Verify-AuthoringHelpTrial.ps1',
        'scripts/Verify-AuthoringHelpPreflight.ps1',
        'scripts/Audit-AuthoringHelpTrace.ps1',
        'scripts/Prepare-BusinessPolicyTrial.ps1',
        'scripts/Start-SubagentTrialHost.ps1',
        'experiments/AgentLang.SubagentTrials/business-policy-help-002/acceptance.json',
        'experiments/AgentLang.SubagentTrials/business-policy-001/acceptance.json',
        'experiments/AgentLang.Benchmarks/task-bank/public/S06.json',
        'experiments/AgentLang.Benchmarks/task-bank/public/S07.json',
        'experiments/AgentLang.SubagentTrials/business-policy-help-002/language-primer.md',
        'experiments/AgentLang.SubagentTrials/business-policy-help-002/artifacts/flat-customer.agent'
    )
    $requiredSourceMap = @{}
    foreach ($sourcePath in $requiredSourcePaths) {
        $suffix = if ($sourcePath.StartsWith('scripts/',[StringComparison]::Ordinal)) { $sourcePath } else {
            switch ($sourcePath) {
                'experiments/AgentLang.SubagentTrials/business-policy-help-002/acceptance.json' { 'acceptance.json' }
                'experiments/AgentLang.SubagentTrials/business-policy-001/acceptance.json' { 'acceptance-business-policy-001.json' }
                'experiments/AgentLang.Benchmarks/task-bank/public/S06.json' { 'public-task-S06.json' }
                'experiments/AgentLang.Benchmarks/task-bank/public/S07.json' { 'public-task-S07.json' }
                'experiments/AgentLang.SubagentTrials/business-policy-help-002/language-primer.md' { 'language-primer.md' }
                'experiments/AgentLang.SubagentTrials/business-policy-help-002/artifacts/flat-customer.agent' { 'flat-customer.agent' }
            }
        }
        $requiredSourceMap[$sourcePath] = "$snapshotRoot$suffix"
    }
    $artifactRowsValid = $true
    $artifactSeenPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $artifactSeenSources = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($artifact in $sourceArtifacts) {
        $snapshotPath = [string](Get-Field $artifact 'path')
        $sourcePath = [string](Get-Field $artifact 'sourcePath')
        if (-not $artifactSeenPaths.Add($snapshotPath) -or -not $artifactSeenSources.Add($sourcePath)) { $artifactRowsValid = $false }
        if (-not $requiredSourceMap.ContainsKey($sourcePath) -or $requiredSourceMap[$sourcePath] -cne $snapshotPath) { $artifactRowsValid = $false }
    }
    $missingSourcePaths = @($requiredSourcePaths | Where-Object { -not $artifactSeenSources.Contains($_) })
    Add-Check 'frozen source artifact inventory contains every required trial input exactly once' ($artifactRowsValid -and $missingSourcePaths.Count -eq 0 -and $sourceArtifacts.Count -eq $requiredSourcePaths.Count) @{expected=$requiredSourcePaths;actual=@($sourceArtifacts | ForEach-Object { [ordered]@{path=$_.path;sourcePath=$_.sourcePath} });missing=$missingSourcePaths}
    $sourceArtifactResults = [Collections.Generic.List[object]]::new()
    foreach ($artifact in $sourceArtifacts) {
        $relativePath = [string](Get-Field $artifact 'path')
        $sourcePath = [string](Get-Field $artifact 'sourcePath')
        $expectedHash = ([string](Get-Field $artifact 'sha256')).ToLowerInvariant()
        $artifactPath = Resolve-RepoPath $relativePath
        $workingSourcePath = Resolve-RepoPath $sourcePath
        $safe = (Test-Within $artifactPath $repo) -and (Test-Within $workingSourcePath $repo)
        $exists = $safe -and (Test-Path -LiteralPath $artifactPath -PathType Leaf) -and (Test-Path -LiteralPath $workingSourcePath -PathType Leaf)
        $actualHash = if ($exists) { Get-Sha256 $artifactPath } else { $null }
        $workingSourceHash = if ($exists) { Get-Sha256 $workingSourcePath } else { $null }
        $actualBytes = if ($exists) { (Get-Item -LiteralPath $artifactPath).Length } else { $null }
        $gitHash = if ($safe) { Get-GitBlobSha256 ([string]$pin.sourceRevision) $relativePath } else { $null }
        $gitSourceHash = if ($safe) { Get-GitBlobSha256 ([string]$pin.sourceRevision) $sourcePath } else { $null }
        $ok = $exists -and $actualHash -ceq $expectedHash -and $workingSourceHash -ceq $expectedHash -and $gitHash -ceq $expectedHash -and $gitSourceHash -ceq $expectedHash -and $actualBytes -eq [long](Get-Field $artifact 'bytes')
        $sourceArtifactResults.Add([ordered]@{path=$relativePath;sourcePath=$sourcePath;expectedSha256=$expectedHash;workingSnapshotSha256=$actualHash;workingSourceSha256=$workingSourceHash;sourceRevisionSnapshotSha256=$gitHash;sourceRevisionSourceSha256=$gitSourceHash;bytes=$actualBytes;passed=$ok})
    }
    Add-Check 'all frozen source artifact snapshots and versioned inputs match working copy and commit' (@($sourceArtifactResults | Where-Object { -not $_.passed }).Count -eq 0) @($sourceArtifactResults)
    $preparedState = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json -AsHashtable -Depth 100
    $preparation = Get-Field $preparedState 'preparation'
    $seedSummary = Get-Field $preparation 'seedSummary'
    $seedSummaryOk = [int](Get-Field $seedSummary 'authoredWordCount') -eq 0 -and [int](Get-Field $seedSummary 'typeCount') -eq 6 -and [int](Get-Field $seedSummary 'testCount') -eq 0
    Add-Check 'frozen starting state was prepared by the pinned clean trial scripts' ([string](Get-Field $preparation 'sourceRevision') -ceq $pinnedRevision -and (Get-Field $preparation 'dirty') -eq $false -and [string](Get-Field $preparation 'scriptPath') -ceq [string](Get-Field $pin 'preparerPath') -and [string](Get-Field $preparation 'dependencyPath') -ceq [string](Get-Field $pin 'originalPreparerPath') -and [string](Get-Field $preparation 'scriptSha256') -ceq ([string](Get-Field $pin 'preparerSha256')).ToLowerInvariant() -and [string](Get-Field $preparation 'dependencySha256') -ceq ([string](Get-Field $pin 'originalPreparerSha256')).ToLowerInvariant() -and $seedSummaryOk) @{preparation=$preparation;sourceRevision=$pinnedRevision;seedSummaryPassed=$seedSummaryOk}

    $pinChecksPassed = $true
    for ($index=$pinCheckStart; $index -lt $metadataChecks.Count; $index++) {
        if (-not [bool]$metadataChecks[$index].passed) { $pinChecksPassed = $false }
    }
    $frozenPinInfo.checked = $pinChecksPassed
    $frozenPinInfo.status = if ($pinChecksPassed) { 'validated' } else { 'failed validation' }
    $script:checkFrozenPin = $pinChecksPassed
    return [ordered]@{path=$pinPath;sha256=$pinHash;data=$pin;passed=$pinChecksPassed}
}

function Test-Within([string]$Path, [string]$Root) {
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $prefix = $fullRoot + [IO.Path]::DirectorySeparatorChar
    return $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Copy-ProjectToScratch([string]$Source, [string]$Destination) {
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $Source -Recurse -Force -File) {
        $relative = [IO.Path]::GetRelativePath($Source, $file.FullName)
        if ($relative -match '(^|[\\/])(\.git|bin|obj)([\\/]|$)') { continue }
        $target = Join-Path $Destination $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        [IO.File]::Copy($file.FullName, $target, $false)
    }
}

function Invoke-JsonlSession([string]$Project, [object[]]$Requests, [string]$Label) {
    if ($Requests.Count -gt 95) { throw "JSONL session '$Label' exceeds the 95-request verifier limit." }
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.WorkingDirectory = $Project
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $utf8NoBom
    $start.StandardErrorEncoding = $utf8NoBom
    $start.StandardInputEncoding = $utf8NoBom
    $start.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $start.Environment['DOTNET_NOLOGO'] = '1'
    $start.ArgumentList.Add($resolvedCli)
    $start.ArgumentList.Add('--project')
    $start.ArgumentList.Add($Project)
    $start.ArgumentList.Add('--filesystem')
    $start.ArgumentList.Add('virtual')
    $start.ArgumentList.Add('--jsonl')
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $clock = [Diagnostics.Stopwatch]::StartNew()
    if (-not $process.Start()) { throw "Could not start JSONL session '$Label'." }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $requestLines = [Collections.Generic.List[string]]::new()
    foreach ($request in $Requests) {
        $line = ConvertTo-Json -InputObject $request -Depth 80 -Compress
        $requestLines.Add($line)
        $process.StandardInput.WriteLine($line)
    }
    $process.StandardInput.Close()
    $finished = $process.WaitForExit(180000)
    if (-not $finished) {
        try { $process.Kill($true) } catch { }
        $process.WaitForExit()
    }
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    $clock.Stop()
    $responseLines = @($stdout -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $responses = [Collections.Generic.List[object]]::new()
    $parseErrors = [Collections.Generic.List[string]]::new()
    foreach ($responseLine in $responseLines) {
        try { $responses.Add((ConvertFrom-Json -InputObject $responseLine -AsHashtable -Depth 100)) }
        catch { $parseErrors.Add($_.Exception.Message) }
    }
    $session = [ordered]@{
        label=$Label; requestCount=$Requests.Count; requests=@($requestLines)
        responseCount=$responseLines.Count; responseLines=$responseLines
        responses=@($responses); parseErrors=@($parseErrors); exitCode=$(if ($finished) { $process.ExitCode } else { $null })
        timedOut=(-not $finished); durationMs=$clock.ElapsedMilliseconds; stderr=$stderr
    }
    $sessions.Add($session)
    $process.Dispose()
    if (-not $finished) { throw "JSONL session '$Label' timed out." }
    $exitCode = $session.exitCode
    if ($null -eq $exitCode -or [int]$exitCode -ne 0) { throw "JSONL session '$Label' exited with code ${exitCode}: $stderr" }
    if ($parseErrors.Count -gt 0) { throw "JSONL session '$Label' returned invalid JSON: $($parseErrors[0])" }
    if ($responses.Count -ne $Requests.Count) { throw "JSONL session '$Label' returned $($responses.Count) responses for $($Requests.Count) requests." }
    return ,@($responses)
}

function Assert-ResponsesOk([object[]]$Responses, [string]$Label) {
    for ($index = 0; $index -lt $Responses.Count; $index++) {
        if (-not [bool]$Responses[$index].ok) {
            $message = Get-ProtocolError $Responses[$index]
            throw "$Label request $index failed: $message"
        }
    }
}

function Get-TargetTask($Oracle, [string]$Id) {
    $matches = @($Oracle.tasks | Where-Object { $_.id -ceq $Id })
    if ($matches.Count -ne 1) { throw "Acceptance corpus must contain exactly one task '$Id'." }
    return $matches[0]
}

function Get-ExpectedForCase($Task, $Case) {
    $kind = [string]$Case.kind
    $balanceText = [string]$Case.balanceMinor
    if ($balanceText -notmatch '^(0|-?[1-9][0-9]*)$') { throw "Case '$($Case.id)' has noncanonical balanceMinor '$balanceText'." }
    $balance = [Numerics.BigInteger]::Parse($balanceText, [Globalization.NumberStyles]::AllowLeadingSign, [Globalization.CultureInfo]::InvariantCulture)
    if ($balance -lt [Numerics.BigInteger]([long]::MinValue) -or $balance -gt [Numerics.BigInteger]([long]::MaxValue)) {
        throw "Case '$($Case.id)' is outside signed Int64 range."
    }
    $premium = [string]::Equals($kind, 'premium', [StringComparison]::Ordinal)
    $type = [string]$Task.outputs[0]
    $value = switch ($Task.id) {
        'S01' { $premium }
        'S06' { if ($premium) { '1000' } else { '0' } }
        'S07' {
            if ($premium) {
                # BigInteger division truncates toward zero and cannot overflow at Int64 boundaries.
                ([Numerics.BigInteger]::Divide(($balance * [Numerics.BigInteger]9), [Numerics.BigInteger]10)).ToString([Globalization.CultureInfo]::InvariantCulture)
            } else { $balance.ToString([Globalization.CultureInfo]::InvariantCulture) }
        }
        default { throw "Unsupported task id '$($Task.id)'." }
    }
    $providedType = [string]$Case.expected.type
    if ($providedType -cne $type) { throw "Case '$($Case.id)' expected type '$providedType' differs from output '$type'." }
    if ($type -ceq 'Bool') {
        if ($Case.expected.value -isnot [bool] -or [bool]$Case.expected.value -ne [bool]$value) { throw "Case '$($Case.id)' expected Bool does not match the independent oracle." }
    } else {
        if ($Case.expected.value -isnot [string] -or [string]$Case.expected.value -cne [string]$value) { throw "Case '$($Case.id)' expected integer text does not match the independent oracle." }
    }
    return [ordered]@{ type=$type; value=$value; balanceMinor=$balanceText; premium=$premium }
}

function Resolve-FileFromRoot([string]$BasePath, [string]$Path) {
    if ([IO.Path]::IsPathFullyQualified($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath($Path, $BasePath)
}

function Get-TreeHash([string]$Path) {
    if (Test-Path -LiteralPath $Path -PathType Leaf) { return Get-Sha256 $Path }
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { throw "Hash input does not exist: $Path" }
    $rows = Get-RelativeInventory $Path
    $canonical = ConvertTo-Json -InputObject @($rows) -Depth 20 -Compress
    $bytes = $utf8NoBom.GetBytes($canonical)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Validate-StartingState([string]$StartPath, [string]$ActorPath, $Acceptance, [string]$CurrentMode, [string]$CurrentTask) {
    $parent = Split-Path -Parent $StartPath
    $statePath = Join-Path $parent 'starting-state.json'
    if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) {
        $checks.Add([ordered]@{name='optional starting-state metadata absent';passed=$true;details='Source and identity preservation still use the immutable StartingProjectPath.'})
        return $null
    }
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json -AsHashtable -Depth 100
    Add-Check 'starting-state schema and study match' ([int]$state.schemaVersion -eq 1 -and [string]$state.studyId -ceq [string]$Acceptance.studyId -and [string]$state.mode -ceq $CurrentMode -and [string]$state.taskId -ceq $CurrentTask)
    $expectedIndex = [array]::IndexOf($sequence, $CurrentTask) + 1
    Add-Check 'starting-state sequence index matches canonical order' ([int]$state.sequenceIndex -eq $expectedIndex) "expected one-based $expectedIndex; actual $($state.sequenceIndex)"
    $declaredStart = [string](Get-Field (Get-Field $state 'project') 'path')
    $declaredActor = [string](Get-Field (Get-Field $state 'actor') 'projectPath')
    $pathsMatch = [IO.Path]::IsPathFullyQualified($declaredStart) -and [IO.Path]::GetFullPath($declaredStart) -ieq [IO.Path]::GetFullPath($StartPath) -and [IO.Path]::IsPathFullyQualified($declaredActor) -and [IO.Path]::GetFullPath($declaredActor) -ieq [IO.Path]::GetFullPath($ActorPath)
    Add-Check 'starting-state project and actor paths match supplied run' $pathsMatch @{project=$declaredStart;actor=$declaredActor}
    $runtimeMetadata = Get-Field $state 'runtime'
    Add-Check 'starting-state pins CLI and Business runtime artifacts' (-not [string]::IsNullOrWhiteSpace([string](Get-Field $runtimeMetadata 'cliPath')) -and -not [string]::IsNullOrWhiteSpace([string](Get-Field $runtimeMetadata 'cliSha256')) -and -not [string]::IsNullOrWhiteSpace([string](Get-Field $runtimeMetadata 'businessPath')) -and -not [string]::IsNullOrWhiteSpace([string](Get-Field $runtimeMetadata 'businessSha256')))
    Add-Check 'starting-state has source input hashes' (@(Get-Field $state 'sourceInputs').Count -gt 0)
    $startInventory = Get-RelativeInventory $StartPath
    $listedFiles = @(Get-Field $state.project 'files')
    Add-Check 'starting-state includes project hash rows' ($listedFiles.Count -gt 0)
    $projectRowsOk = $true
    $projectHashDetails = [Collections.Generic.List[object]]::new()
    foreach ($row in $listedFiles) {
        $actualPath = Resolve-FileFromRoot $StartPath ([string]$row.path)
        $relative = [IO.Path]::GetRelativePath($StartPath, $actualPath).Replace('\','/')
        $safePath = -not [IO.Path]::IsPathFullyQualified([string]$row.path) -and (Test-Within $actualPath $StartPath)
        $exists = $safePath -and (Test-Path -LiteralPath $actualPath -PathType Leaf)
        $actualHash = if ($exists) { Get-Sha256 $actualPath } else { $null }
        $ok = $exists -and ([string]$actualHash -ceq ([string]$row.sha256).ToLowerInvariant())
        if (-not $ok) { $projectRowsOk = $false }
        $projectHashDetails.Add([ordered]@{path=$relative;expected=[string]$row.sha256;actual=$actualHash;passed=$ok})
    }
    Add-Check 'starting-state project file hashes match immutable start' $projectRowsOk @($projectHashDetails)
    $listedRelativePaths = @($projectHashDetails | ForEach-Object { [string]$_.path } | Sort-Object -Unique)
    $actualRelativePaths = @($startInventory | ForEach-Object { [string]$_.path } | Sort-Object -Unique)
    Add-Check 'starting-state rows cover the complete project inventory' ((@($projectHashDetails).Count -eq $listedFiles.Count) -and (($listedRelativePaths -join "`0") -ceq ($actualRelativePaths -join "`0"))) @{listed=$listedRelativePaths.Count;actual=$actualRelativePaths.Count}
    $projectInventoryHash = Get-TreeHash $StartPath
    $declaredProjectHash = [string](Get-Field (Get-Field $state 'project') 'inventorySha256')
    Add-Check 'starting-state project inventory hash matches immutable start' ($projectInventoryHash -ceq $declaredProjectHash) @{expected=$declaredProjectHash;actual=$projectInventoryHash}
    $actorState = Get-Field $state 'actor'
    $actorFiles = @(Get-Field $actorState 'files')
    $actorInventoryJson = ConvertTo-Json -InputObject @($actorFiles) -Depth 20 -Compress
    $projectInventoryJson = ConvertTo-Json -InputObject @($startInventory) -Depth 20 -Compress
    $actorStateHash = [string](Get-Field $actorState 'inventorySha256')
    Add-Check 'prepared actor inventory matches immutable start snapshot' ($actorInventoryJson -ceq $projectInventoryJson -and $actorStateHash -ceq $projectInventoryHash)

    $rootRowsOk = $true
    $rootHashDetails = [Collections.Generic.List[object]]::new()
    foreach ($row in @(Get-Field $state 'sourceInputs')) {
        $actualPath = Resolve-FileFromRoot $repo ([string]$row.path)
        $safePath = -not [IO.Path]::IsPathFullyQualified([string]$row.path) -and (Test-Within $actualPath $repo)
        $exists = $safePath -and (Test-Path -LiteralPath $actualPath -PathType Leaf)
        $actualHash = if ($exists) { Get-Sha256 $actualPath } else { $null }
        $ok = $exists -and ([string]$actualHash -ceq ([string]$row.sha256).ToLowerInvariant())
        if (-not $ok) { $rootRowsOk = $false }
        $rootHashDetails.Add([ordered]@{kind='sourceInput';path=$actualPath;expected=[string]$row.sha256;actual=$actualHash;passed=$ok})
    }
    foreach ($label in @('seed','runtime')) {
        $group = Get-Field $state $label
        if ($null -eq $group) { continue }
        if ($label -ceq 'runtime') {
            foreach ($entryName in @('cli','business')) {
                $filePath = [string](Get-Field $group ($entryName + 'Path'))
                $expectedHash = [string](Get-Field $group ($entryName + 'Sha256'))
                if ([string]::IsNullOrWhiteSpace($filePath)) { $filePath = [string](Get-Field $group ($entryName + 'DllPath')) }
                if ([string]::IsNullOrWhiteSpace($filePath)) { $filePath = [string](Get-Field $group ($entryName + 'Dll')) }
                if ([string]::IsNullOrWhiteSpace($expectedHash)) { $expectedHash = [string](Get-Field $group ($entryName + 'DllSha256')) }
                if ([string]::IsNullOrWhiteSpace($filePath) -or [string]::IsNullOrWhiteSpace($expectedHash)) { continue }
                $actualPath = Resolve-FileFromRoot $repo $filePath
                $safePath = -not [IO.Path]::IsPathFullyQualified($filePath) -and (Test-Within $actualPath $repo)
                $exists = $safePath -and (Test-Path -LiteralPath $actualPath -PathType Leaf)
                $actualHash = if ($exists) { Get-Sha256 $actualPath } else { $null }
                $ok = $exists -and ([string]$actualHash -ceq $expectedHash.ToLowerInvariant())
                if (-not $ok) { $rootRowsOk = $false }
                $rootHashDetails.Add([ordered]@{kind=$label;path=$actualPath;expected=$expectedHash;actual=$actualHash;passed=$ok})
            }
        } else {
            $seedPath = [string](Get-Field $group 'path')
            $expectedHash = [string](Get-Field $group 'inventorySha256')
            if ([string]::IsNullOrWhiteSpace($expectedHash)) { $expectedHash = [string](Get-Field $group 'sha256') }
            if (-not [string]::IsNullOrWhiteSpace($seedPath) -and -not [string]::IsNullOrWhiteSpace($expectedHash)) {
                $actualPath = Resolve-FileFromRoot $repo $seedPath
                $exists = (Test-Within $actualPath $repo) -and (Test-Path -LiteralPath $actualPath)
                $actualHash = if ($exists) { Get-TreeHash $actualPath } else { $null }
                $ok = $exists -and ([string]$actualHash -ceq $expectedHash.ToLowerInvariant())
                if (-not $ok) { $rootRowsOk = $false }
                $rootHashDetails.Add([ordered]@{kind=$label;path=$actualPath;expected=$expectedHash;actual=$actualHash;passed=$ok})
            }
        }
    }
    Add-Check 'starting-state seed, source, and runtime hashes match' $rootRowsOk @($rootHashDetails)
    $previous = Get-Field $state 'previousAcceptance'
    $previousAcceptancePath = [string](Get-Field $previous 'acceptancePath')
    if ([string]::IsNullOrWhiteSpace($previousAcceptancePath)) { $previousAcceptancePath = [string](Get-Field $previous 'path') }
    if ($expectedIndex -eq 1 -and ($null -ne $previous -and $previousAcceptancePath)) {
        Add-Check 'first task has no prior acceptance' $false
    } elseif ($null -ne $previous -and $previousAcceptancePath) {
        $previousPath = Resolve-FileFromRoot $repo $previousAcceptancePath
        $exists = (Test-Within $previousPath $repo) -and (Test-Path -LiteralPath $previousPath -PathType Leaf)
        $actualHash = if ($exists) { Get-Sha256 $previousPath } else { $null }
        $previousExpectedHash = [string](Get-Field $previous 'acceptanceSha256')
        if ([string]::IsNullOrWhiteSpace($previousExpectedHash)) { $previousExpectedHash = [string](Get-Field $previous 'sha256') }
        Add-Check 'previous acceptance hash matches starting-state' ($exists -and $actualHash -ceq $previousExpectedHash.ToLowerInvariant())
        $prior = Get-Content -LiteralPath $previousPath -Raw | ConvertFrom-Json -AsHashtable -Depth 100
        $expectedPriorTask = $sequence[$expectedIndex - 2]
        Add-Check 'previous acceptance metadata names immediate predecessor' ([string](Get-Field $previous 'taskId') -ceq $expectedPriorTask)
        $priorPassed = [bool]$prior.passed -and [int]$prior.schemaVersion -eq 1 -and [string]$prior.studyId -ceq [string]$Acceptance.studyId -and [string]$prior.mode -ceq $CurrentMode -and [string]$prior.taskId -ceq $expectedPriorTask -and [int]$prior.sequenceIndex -eq ($expectedIndex - 1)
        Add-Check 'previous task acceptance passed in same study and mode' $priorPassed "expected preceding task $expectedPriorTask at sequence index $($expectedIndex - 1)"
        $priorProjectHash = [string](Get-Field (Get-Field $prior 'project') 'treeSha256')
        if ([string]::IsNullOrWhiteSpace($priorProjectHash)) { $priorProjectHash = [string](Get-Field $prior 'outputTreeSha256') }
        if (-not [string]::IsNullOrWhiteSpace($priorProjectHash)) {
            $startingProjectHash = Get-TreeHash $StartPath
            Add-Check 'previous accepted project output matches current starting project' ($priorProjectHash -ceq $startingProjectHash) @{expected=$priorProjectHash;actual=$startingProjectHash}
        }
    } elseif ($expectedIndex -eq 1) {
        Add-Check 'first task has no prior acceptance' ($null -eq $previous)
    } elseif ($null -ne $previous) {
        Add-Check 'prior acceptance has a path and hash' $false
    } else {
        $checks.Add([ordered]@{name='optional prior acceptance absent';passed=$true;details='No starting-state record was supplied for this isolated control or task.'})
    }
    return [ordered]@{ path=$statePath; sha256=(Get-Sha256 $statePath); data=$state }
}

function Get-NominalTypeNames([object[]]$WordRows) {
    $builtins = @('Bool','Int','Float','String','Unit','List','Option','Result')
    $found = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($row in $WordRows) {
        foreach ($signature in @($row.inputs) + @($row.outputs)) {
            foreach ($match in [regex]::Matches([string]$signature, '[A-Z][A-Za-z0-9_]*')) {
                if ($match.Value -cnotin $builtins) { [void]$found.Add($match.Value) }
            }
        }
    }
    return @($found | Sort-Object)
}

function Invoke-SourceRequests([string]$Project, [string]$Selector, [string[]]$Names, [string]$Label) {
    $map = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    for ($offset = 0; $offset -lt $Names.Count; $offset += 90) {
        $end = [Math]::Min($offset + 90, $Names.Count)
        $requests = [Collections.Generic.List[object]]::new()
        for ($index = $offset; $index -lt $end; $index++) {
            $request = [ordered]@{ op='source' }
            $request[$Selector] = $Names[$index]
            $requests.Add($request)
        }
        if ($requests.Count -eq 0) { continue }
        $responses = Invoke-JsonlSession $Project @($requests) "$Label source $($offset + 1)-$end"
        Assert-ResponsesOk $responses $Label
        for ($index = 0; $index -lt $responses.Count; $index++) {
            $map[$Names[$offset + $index]] = [string]$responses[$index].data
        }
    }
    return $map
}

function Check-LanguagePreservation([string]$StartProject, [string]$ActorProject, [string]$TargetName, [string]$CurrentTask) {
    $startWordsResponse = Invoke-JsonlSession $StartProject @([ordered]@{op='words'}) 'starting words inventory'
    $actorWordsResponse = Invoke-JsonlSession $ActorProject @([ordered]@{op='words'}) 'actor words inventory'
    Assert-ResponsesOk $startWordsResponse 'starting words inventory'
    Assert-ResponsesOk $actorWordsResponse 'actor words inventory'
    $startRows = @($startWordsResponse[0].data.words)
    $actorRows = @($actorWordsResponse[0].data.words)
    $startUserRows = @($startRows | Where-Object { ([string]$_.id).StartsWith('word_',[StringComparison]::Ordinal) })
    $startTypeNames = Get-NominalTypeNames $startRows
    $expectedStartingTypes = @('Customer','CustomerId','Email','Instant','Money','ProductId') | Sort-Object -CaseSensitive
    $startingUserWordsOk = $startUserRows.Count -eq 0
    $startingTypesOk = (($startTypeNames | Sort-Object -CaseSensitive) -join "`0") -ceq ($expectedStartingTypes -join "`0")
    $startingBaselineUsable = $startingUserWordsOk -and $startingTypesOk
    Add-Check 'immutable Flat seed contains no authored policy words' $startingUserWordsOk @($startUserRows | ForEach-Object { [string]$_.name })
    Add-Check 'immutable Flat seed has exactly the six frozen nominal types' $startingTypesOk @{expected=$expectedStartingTypes;actual=$startTypeNames}
    $preservation.startingBaselineUsable = $startingBaselineUsable
    if (-not $startingBaselineUsable) {
        $script:safetyStop = 'The immutable Flat starting project is not the schema-only six-type seed; oracle execution is a safety stop.'
        $script:targetUsable = $false
    }
    $actorRowsByName = @{}
    foreach ($row in $actorRows) { $actorRowsByName[[string]$row.name] = $row }
    Add-Check 'target exists in actor project' ($actorRowsByName.ContainsKey($TargetName)) $TargetName
    Add-Check 'target is an authored user word' (([string]$actorRowsByName[$TargetName].id).StartsWith('word_',[StringComparison]::Ordinal)) $TargetName

    $startWordNames = @($startUserRows | ForEach-Object { [string]$_.name } | Sort-Object -Unique)
    $startWordSources = Invoke-SourceRequests $StartProject 'word' $startWordNames 'starting project'
    $actorWordSources = Invoke-SourceRequests $ActorProject 'word' $startWordNames 'actor project'
    $startTypeSources = Invoke-SourceRequests $StartProject 'type' $startTypeNames 'starting project'
    $actorTypeSources = Invoke-SourceRequests $ActorProject 'type' $startTypeNames 'actor project'

    $wordIdentityOk = $true
    $changedWords = [Collections.Generic.List[string]]::new()
    foreach ($startRow in $startUserRows) {
        $name = [string]$startRow.name
        if (-not $actorRowsByName.ContainsKey($name)) { $wordIdentityOk = $false; $changedWords.Add("missing:$name"); continue }
        $actorRow = $actorRowsByName[$name]
        $same = ([string]$actorRow.id -ceq [string]$startRow.id) -and
            (@($actorRow.inputs) -join "`0") -ceq (@($startRow.inputs) -join "`0") -and
            (@($actorRow.outputs) -join "`0") -ceq (@($startRow.outputs) -join "`0") -and
            ([string]$actorRow.status -ceq [string]$startRow.status) -and
            ([string]$actorRow.maturity -ceq [string]$startRow.maturity) -and
            ($actorWordSources[$name] -ceq $startWordSources[$name])
        if (-not $same) { $wordIdentityOk = $false; $changedWords.Add($name) }
    }
    Add-Check 'all starting user words retain identity, signature, status, and source' $wordIdentityOk @($changedWords)
    $typeSourcesOk = $true
    $changedTypes = [Collections.Generic.List[string]]::new()
    foreach ($name in $startTypeNames) {
        if (-not $actorTypeSources.ContainsKey($name) -or $actorTypeSources[$name] -cne $startTypeSources[$name]) {
            $typeSourcesOk = $false; $changedTypes.Add($name)
        }
    }
    Add-Check 'all starting nominal type sources are preserved' $typeSourcesOk @($changedTypes)

    $preservation.checked = $true
    $preservation.sourceWords = $startUserRows.Count
    $preservation.nominalTypes = $startTypeNames.Count
    $preservation.unchanged = $wordIdentityOk -and $typeSourcesOk
    $preservation.startingWords = $startWordNames
    $preservation.startingTypes = $startTypeNames
    $preservation.addedUserWords = @($actorRows | Where-Object { ([string]$_.id).StartsWith('word_',[StringComparison]::Ordinal) -and $_.name -notin $startWordNames } | ForEach-Object { [string]$_.name } | Sort-Object)
    $preservation.addedNominalTypes = @(Get-NominalTypeNames $actorRows | Where-Object { $_ -notin $startTypeNames })
    return [ordered]@{ words=$actorRows; target=$actorRowsByName[$TargetName]; startWords=$startUserRows; startTypes=$startTypeNames; startingBaselineUsable=$startingBaselineUsable }
}

function Get-StructuredValue($Data) {
    $root = Get-Field $Data 'structuredStack'
    $values = Get-Field $root 'values'
    if ($null -eq $values -or @($values).Count -ne 1) { throw 'Structured eval must return exactly one value.' }
    return @($values)[0]
}

function Assert-LanguageCase($Response, $Expected, [string]$CaseId) {
    if (-not [bool]$Response.ok) { throw "Eval case '$CaseId' failed: $(Get-ProtocolError $Response)" }
    $data = $Response.data
    Add-Check "$CaseId exact language output type" (@($data.stackTypes).Count -eq 1 -and [string]$data.stackTypes[0] -ceq [string]$Expected.type) ([string]$data.stackTypes -join ',')
    $effects = Get-Field $data 'effects'
    $console = @(Get-Field $data 'console')
    $effectsCount = if ($effects -is [Collections.IDictionary]) { $effects.Count } elseif ($null -eq $effects) { 0 } else { @($effects.PSObject.Properties).Count }
    Add-Check "$CaseId has no effects or console output" ($effectsCount -eq 0 -and $console.Count -eq 0)
    $value = Get-StructuredValue $data
    switch ([string]$Expected.type) {
        'Bool' {
            Add-Check "$CaseId structured Bool" ([string]$value.kind -ceq 'bool' -and $value.value -is [bool] -and [bool]$value.value -eq [bool]$Expected.value)
        }
        'Int' {
            Add-Check "$CaseId structured Int" ([string]$value.kind -ceq 'int' -and [string]$value.value -ceq [string]$Expected.value)
        }
        'Money' {
            $payload = Get-Field $value 'value'
            $baseType = Get-Field $value 'baseType'
            Add-Check "$CaseId structured nominal Money" ([string]$value.kind -ceq 'scalar' -and [string]$value.name -ceq 'Money' -and [string]$baseType.kind -ceq 'int' -and [string]$payload.kind -ceq 'int' -and [string]$payload.value -ceq [string]$Expected.value)
        }
        default { throw "Unsupported expected type '$($Expected.type)'." }
    }
}

try {
    if ([string]::IsNullOrWhiteSpace($TaskId)) { $TaskId = 'S06' }
    if ([string]::IsNullOrWhiteSpace($EvidencePath)) { $EvidencePath = "reports/evidence/authoring-help-flat-$($TaskId.ToLowerInvariant()).json" }
    $Mode = 'flat'
    $preservation.mode = $Mode
    $reuse.status = 'independent flat task; no predecessor dictionary reuse is claimed'
    Add-Check 'mode is flat' ($Mode -ceq 'flat') $Mode
    Add-Check 'task id is supported by the fresh study' ($TaskId -cin @('S06','S07')) $TaskId

    $oracleUsable = $false
    $oracleCreatedAt = $null
    $cases = @()
    $independentCases = [Collections.Generic.List[object]]::new()
    try {
        Add-Check 'versioned acceptance corpus exists' (Test-Path -LiteralPath $oraclePath -PathType Leaf) $oraclePath
        if (-not (Test-Path -LiteralPath $oraclePath -PathType Leaf)) { throw "Missing acceptance corpus: $oraclePath" }
        $oracleHash = Get-Sha256 $oraclePath
        $oracleJson = Get-Content -LiteralPath $oraclePath -Raw
        $oracleJsonParameters = @{ InputObject=$oracleJson; AsHashtable=$true; Depth=100 }
        if ((Get-Command ConvertFrom-Json).Parameters.ContainsKey('DateKind')) { $oracleJsonParameters.DateKind = 'String' }
        $oracle = ConvertFrom-Json @oracleJsonParameters
        $oracleDocument = [System.Text.Json.JsonDocument]::Parse($oracleJson)
        try {
            $createdAtElement = $oracleDocument.RootElement.GetProperty('defaults').GetProperty('createdAt')
            Add-Check 'acceptance default Instant is a JSON string' ($createdAtElement.ValueKind -eq [System.Text.Json.JsonValueKind]::String)
            $oracleCreatedAt = $createdAtElement.GetString()
            $oracle.defaults.createdAt = $oracleCreatedAt
        } finally { $oracleDocument.Dispose() }

        $sequenceOk = ([string]($oracle.sequence -join ',') -ceq ($sequence -join ','))
        $studyOk = ([int]$oracle.schemaVersion -eq 1 -and [string]$oracle.studyId -ceq 'business-policy-help-002')
        Add-Check 'acceptance corpus study and canonical sequence match' ($studyOk -and $sequenceOk) @{studyId=$oracle.studyId;sequence=@($oracle.sequence)}
        $predecessorPath = Join-Path $repo 'experiments/AgentLang.SubagentTrials/business-policy-001/acceptance.json'
        $predecessorHash = Get-Sha256 $predecessorPath
        $declaredPredecessor = [string](Get-Field $oracle 'predecessorSha256')
        if ([string]::IsNullOrWhiteSpace($declaredPredecessor)) { $declaredPredecessor = [string](Get-Field (Get-Field $oracle 'predecessor') 'sha256') }
        Add-Check 'acceptance records the unchanged 001 predecessor hash' ($declaredPredecessor.ToLowerInvariant() -ceq $predecessorHash) @{expected=$predecessorHash;actual=$declaredPredecessor}

        $targetOracle = Get-TargetTask $oracle $TaskId
        $expectedOutput = if ($TaskId -ceq 'S06') { 'Int' } else { 'Money' }
        $signatureOk = ((@($targetOracle.inputs) -join ',') -ceq 'Customer' -and @($targetOracle.outputs).Count -eq 1 -and [string]$targetOracle.outputs[0] -ceq $expectedOutput -and -not [string]::IsNullOrWhiteSpace([string]$targetOracle.symbol))
        Add-Check 'acceptance task has the required strong signature' $signatureOk @{symbol=$targetOracle.symbol;inputs=@($targetOracle.inputs);outputs=@($targetOracle.outputs)}
        $cases = @($targetOracle.cases)
        $countOk = $cases.Count -eq $expectedCounts[$TaskId]
        Add-Check 'acceptance case count matches the unchanged corpus' $countOk @{expected=$expectedCounts[$TaskId];actual=$cases.Count}
        $caseIds = @($cases | ForEach-Object { [string]$_.id })
        $idsOk = ($caseIds.Count -gt 0 -and @($caseIds | Sort-Object -Unique).Count -eq $caseIds.Count -and @($caseIds | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -eq 0)
        Add-Check 'acceptance case ids are nonempty and unique' $idsOk
        $caseOracleRows = [Collections.Generic.List[object]]::new()
        foreach ($case in $cases) {
            $expected = Get-ExpectedForCase $targetOracle $case
            $independentCases.Add($expected)
            $caseOracleRows.Add([ordered]@{id=[string]$case.id;kind=[string]$case.kind;balanceMinor=[string]$case.balanceMinor;expected=[ordered]@{type=$expected.type;value=$expected.value}})
        }
        $caseOracleEvidence = @($caseOracleRows)
        $createdAtValid = $false
        try {
            $createdAt = [DateTimeOffset]::ParseExact([string]$oracleCreatedAt,'O',[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind)
            $createdAtValid = $createdAt.Offset -eq [TimeSpan]::Zero -and $createdAt.ToString('O',[Globalization.CultureInfo]::InvariantCulture) -ceq [string]$oracleCreatedAt
        } catch { $createdAtValid = $false }
        Add-Check 'default Instant is canonical UTC round-trip text' $createdAtValid $oracleCreatedAt
        Add-Check 'all acceptance values match independent BigInteger oracle' ($independentCases.Count -eq $cases.Count)
        $oracleUsable = $studyOk -and $sequenceOk -and $signatureOk -and $countOk -and $idsOk -and $createdAtValid -and $independentCases.Count -eq $cases.Count
        if (-not $oracleUsable) { $safetyStop = 'The acceptance corpus is missing, malformed, or not the pinned S06/S07 oracle.' }
    } catch {
        Add-MetadataFailure 'acceptance and independent oracle load' $_
        if ($null -eq $safetyStop) { $safetyStop = "Acceptance oracle is unusable: $($_.Exception.Message)" }
    }

    try {
        $originalProjectPath = Resolve-RepoPath $ProjectPath
        $resolvedProject = $originalProjectPath
        $projectExists = Test-Path -LiteralPath $resolvedProject -PathType Container
        Add-Check 'actor project directory exists' $projectExists $resolvedProject
        if (-not $projectExists) { throw "Actor project does not exist: $resolvedProject" }

        $evidenceTarget = Resolve-RepoPath $EvidencePath
        $requestedEvidenceTarget = $evidenceTarget
        $requestedEvidenceExists = Test-Path -LiteralPath $requestedEvidenceTarget
        $evidenceOutsideActor = -not (Test-Within $evidenceTarget $resolvedProject)
        $evidenceOutsideStart = $true
        if (-not [string]::IsNullOrWhiteSpace($StartingProjectPath)) {
            $originalStartingProjectPath = Resolve-RepoPath $StartingProjectPath
            $resolvedStartingProject = $originalStartingProjectPath
            $startExists = Test-Path -LiteralPath $resolvedStartingProject -PathType Container
            Add-Check 'immutable starting project exists' $startExists $resolvedStartingProject
            if ($startExists) {
                $preservation.startingProjectPath = $resolvedStartingProject
                $preservation.startingProjectFiles = Get-RelativeInventory $resolvedStartingProject
            }
            $evidenceOutsideStart = -not (Test-Within $evidenceTarget $resolvedStartingProject)
        } else {
            Add-Check 'starting project supplied for preservation and freeze validation' $false
        }
        $evidenceIsSafe = $evidenceOutsideActor -and $evidenceOutsideStart
        Add-Check 'evidence file is outside actor and starting projects' $evidenceIsSafe $evidenceTarget
        if (-not $evidenceIsSafe) {
            $evidenceTarget = Join-Path $repo "reports/evidence/authoring-help-flat-$($TaskId.ToLowerInvariant()).json"
        }
        $evidenceCollision = $requestedEvidenceExists -or (Test-Path -LiteralPath $evidenceTarget)
        Add-Check 'evidence output destination is unused and will not overwrite a prior report' (-not $evidenceCollision) @{requested=$requestedEvidenceTarget;selected=$evidenceTarget;collision=$evidenceCollision}
        if ($evidenceCollision) {
            if ($null -eq $safetyStop) { $safetyStop = 'The requested evidence destination already exists; verifier stopped before CLI execution to preserve it.' }
            $evidenceTarget = Get-UniqueEvidencePath $evidenceTarget $TaskId 'preserved'
        }

        $resolvedCli = Resolve-RepoPath $CliDll
        $cliExists = Test-Path -LiteralPath $resolvedCli -PathType Leaf
        Add-Check 'fresh pinned AgentLang CLI exists' $cliExists $resolvedCli
        if (-not $cliExists) { throw "Pinned CLI does not exist: $resolvedCli" }
        $runtimeInfo.cliDll = $resolvedCli
        $runtimeInfo.cliSha256 = Get-Sha256 $resolvedCli
        $actorInventoryBefore = Get-RelativeInventory $resolvedProject
        $actorHashBefore = Get-TreeHash $resolvedProject
        if ($null -ne $resolvedStartingProject -and (Test-Path -LiteralPath $resolvedStartingProject -PathType Container)) {
            try {
                $stateCheckStart = $metadataChecks.Count
                $startingStateMetadata = Validate-StartingState $resolvedStartingProject $resolvedProject $oracle $Mode $TaskId
                $startingStatePassed = $null -ne $startingStateMetadata
                for ($stateCheckIndex=$stateCheckStart; $stateCheckIndex -lt $metadataChecks.Count; $stateCheckIndex++) {
                    if (-not [bool]$metadataChecks[$stateCheckIndex].passed) { $startingStatePassed = $false }
                }
            } catch {
                Add-MetadataFailure 'starting-state source and project pins' $_
                $startingStatePassed = $false
            }
            if ($null -ne $startingStateMetadata) {
                $pinnedCliHash = [string](Get-Field (Get-Field $startingStateMetadata.data 'runtime') 'cliSha256')
                $cliPinMatches = $runtimeInfo.cliSha256 -ceq $pinnedCliHash.ToLowerInvariant()
                Add-Check 'provided CLI matches starting-state pin' $cliPinMatches @{provided=$runtimeInfo.cliSha256;pinned=$pinnedCliHash}
                if (-not $cliPinMatches) { $startingStatePassed = $false }
            }
            try {
                $frozenPinRecord = Validate-FrozenPin $resolvedStartingProject $resolvedProject $resolvedCli $Mode $TaskId $oracle
            } catch {
                Add-MetadataFailure 'frozen prelaunch pin and source snapshots' $_
                $frozenPinInfo.status = 'invalid'
            }
        } else {
            $startingStateMetadata = $null
            $startingStatePassed = $false
            if ($RequireFrozenPin) { Add-Check 'required frozen actor verification has a starting project' $false }
            $frozenPinInfo.status = 'unfrozen-preflight-or-control-without-starting-project'
            Add-Check 'unfrozen verifier run classified as preflight/control' ($null -eq $resolvedStartingProject) 'No StartingProjectPath was supplied.'
        }
    } catch {
        Add-MetadataFailure 'actor, CLI, project, or evidence setup' $_
        $evidenceCollision = $true
        if ($null -eq $safetyStop) { $safetyStop = "Actor project or CLI is unusable: $($_.Exception.Message)" }
    }

    if ($RequireFrozenPin -and (-not $startingStatePassed -or -not $checkFrozenPin)) {
        if ($null -eq $safetyStop) { $safetyStop = 'Frozen starting-state, runtime, source, prompt, allowlist, or verifier provenance did not validate; no CLI evaluation is allowed.' }
        Add-Check 'required frozen provenance is valid before execution' $false $safetyStop
    }

    $targetUsable = $false
    $targetRow = $null
    $targetDescription = $null
    $actorRows = @()
    $scratchReady = $false
    if (-not $evidenceCollision -and $null -ne $resolvedProject -and $null -ne $resolvedCli -and (Test-Path -LiteralPath $resolvedProject -PathType Container) -and (Test-Path -LiteralPath $resolvedCli -PathType Leaf) -and (-not $RequireFrozenPin -or ($startingStatePassed -and $checkFrozenPin))) {
        try {
            [IO.Directory]::CreateDirectory($testsRoot) | Out-Null
            $scratchProject = Join-Path $testsRoot ('verify-flat-' + $TaskId + '-' + [Guid]::NewGuid().ToString('N'))
            Add-Check 'scratch project path is inside verifier tests root' (Test-Within $scratchProject $testsRoot) $scratchProject
            Copy-ProjectToScratch $resolvedProject $scratchProject
            $scratchInventory = Get-RelativeInventory $scratchProject
            $scratchReady = $scratchInventory.Count -gt 0
            Add-Check 'language scratch copy contains source project' $scratchReady @{files=$scratchInventory.Count}
        } catch {
            Add-MetadataFailure 'scratch project preparation' $_
            if ($null -eq $safetyStop) { $safetyStop = "Could not prepare a safe verifier copy: $($_.Exception.Message)" }
        }
    }
    if ($scratchReady -and $oracleUsable) {
        try {
            $wordResponses = Invoke-JsonlSession $scratchProject @([ordered]@{op='words'}) 'fresh actor words inventory'
            if (-not [bool]$wordResponses[0].ok) { throw "Words inventory failed: $(Get-ProtocolError $wordResponses[0])" }
            $actorRows = @($wordResponses[0].data.words)
            $actorRowsByName = @{}
            foreach ($row in $actorRows) { $actorRowsByName[[string]$row.name] = $row }
            $targetExists = $actorRowsByName.ContainsKey([string]$targetOracle.symbol)
            Add-Check 'target exists in the fresh actor project' $targetExists ([string]$targetOracle.symbol)
            if (-not $targetExists) {
                $safetyStop = "Target '$($targetOracle.symbol)' is missing; oracle execution is a safety stop."
                throw $safetyStop
            }
            $targetRow = $actorRowsByName[[string]$targetOracle.symbol]
            $targetIsAuthored = ([string]$targetRow.id).StartsWith('word_',[StringComparison]::Ordinal)
            Add-Check 'target is an authored user word' $targetIsAuthored @{id=$targetRow.id;name=$targetRow.name}
            $expectedOutput = if ($TaskId -ceq 'S06') { 'Int' } else { 'Money' }
            $signatureOk = ((@($targetRow.inputs) -join ',') -ceq 'Customer' -and (@($targetRow.outputs) -join ',') -ceq $expectedOutput)
            Add-Check 'target has the exact strong typed acceptance signature' $signatureOk @{inputs=@($targetRow.inputs);outputs=@($targetRow.outputs);expectedOutput=$expectedOutput}
            Add-Check 'target is a persistent library word' ([string]$targetRow.status -ceq 'persistent' -and [string]$targetRow.maturity -ceq 'library' -and $targetIsAuthored) @{status=$targetRow.status;maturity=$targetRow.maturity;id=$targetRow.id}

            $describeResponses = Invoke-JsonlSession $scratchProject @([ordered]@{op='describe';word=[string]$targetOracle.symbol}) 'fresh target metadata reload'
            if (-not [bool]$describeResponses[0].ok) {
                Add-Check 'fresh process can reload target metadata' $false (Get-ProtocolError $describeResponses[0])
                $safetyStop = "Target '$($targetOracle.symbol)' cannot be reloaded or described."
            } else {
                $targetDescription = $describeResponses[0].data
                Add-Check 'fresh process can reload target metadata' ($null -ne $targetDescription -and [string]$targetDescription.kind -ceq 'word') $targetDescription
                $effects = @(Get-Field $targetDescription 'effects')
                $pure = $effects.Count -eq 0
                Add-Check 'target has pure effects' $pure $effects
                $targetUsable = $targetIsAuthored -and $signatureOk -and $pure -and [string]$targetDescription.kind -ceq 'word'
                if (-not $targetUsable) {
                    $safetyStop = "Target '$($targetOracle.symbol)' is not a safe, strongly typed, effect-free authored word."
                }
            }
        } catch {
            Add-MetadataFailure 'target resolution and safe usability' $_
            if ($null -eq $safetyStop) { $safetyStop = "Target could not be safely resolved: $($_.Exception.Message)" }
        }
    } elseif (-not $oracleUsable) {
        Add-Check 'oracle execution has a valid frozen corpus' $false $safetyStop
    }

    if ($null -ne $targetRow -and $targetUsable) {
        if ($null -ne $resolvedStartingProject -and (Test-Path -LiteralPath $resolvedStartingProject -PathType Container)) {
            try {
                $preserved = Check-LanguagePreservation $resolvedStartingProject $scratchProject ([string]$targetOracle.symbol) $TaskId
                $targetRow = $preserved.target
            } catch { Add-MetadataFailure 'starting word and type source preservation' $_ }
        } else {
            Add-Check 'starting word and type preservation supplied' $false
        }

        $targetRequests = @(
            [ordered]@{op='test-all'},
            [ordered]@{op='describe';word=[string]$targetOracle.symbol},
            [ordered]@{op='dependencies';word=[string]$targetOracle.symbol},
            [ordered]@{op='transitive-dependencies';word=[string]$targetOracle.symbol},
            [ordered]@{op='source';word=[string]$targetOracle.symbol},
            [ordered]@{op='examples';word=[string]$targetOracle.symbol}
        )
        $targetResponses = @()
        $targetDataByOp = @{}
        try {
            $targetResponses = Invoke-JsonlSession $scratchProject @($targetRequests) 'target tests, documentation, dependencies, source, and examples'
            $metadataTestExecutions += 1
            for ($index=0; $index -lt $targetRequests.Count; $index++) {
                $op = [string]$targetRequests[$index].op
                $response = $targetResponses[$index]
                if (-not [bool]$response.ok) {
                    Add-Check "target $op metadata request succeeds" $false (Get-ProtocolError $response)
                    continue
                }
                $targetDataByOp[$op] = Get-Field $response 'data'
                Add-Check "fresh process reloads target $op metadata" $true
            }
        } catch { Add-MetadataFailure 'target test and metadata protocol session' $_ }

        $testData = $targetDataByOp['test-all']
        $describe = $targetDataByOp['describe']
        $testRows = @()
        if ($null -ne $testData) { $testRows = @(Get-Field $testData 'results') }
        $describeTestCount = [int](Get-Field $describe 'testCount')
        $allTestsPass = ($testRows.Count -gt 0 -and @($testRows | Where-Object { -not [bool](Get-Field $_ 'passed') }).Count -eq 0)
        Add-Check 'all attached tests pass' $allTestsPass @{count=$testRows.Count;failed=@($testRows | Where-Object { -not [bool](Get-Field $_ 'passed') })}
        Add-Check 'target owns passing tests' ($describeTestCount -gt 0 -and @($testRows | Where-Object { [string](Get-Field $_ 'word') -ceq [string]$targetOracle.symbol -and [bool](Get-Field $_ 'passed') }).Count -gt 0) $describeTestCount
        $documentation = [string](Get-Field $describe 'documentation')
        Add-Check 'target has meaningful documentation' ($documentation -match '\S.{7,}') $documentation
        $exampleNames = @()
        if ($null -ne $targetDataByOp['examples']) { $exampleNames = @($targetDataByOp['examples']) }
        Add-Check 'target has attached examples' ([int](Get-Field $describe 'exampleCount') -gt 0 -and $exampleNames.Count -gt 0)
        $describeEffects = @(Get-Field $describe 'effects')
        Add-Check 'target remains pure, supported, and nondeprecated' ($describeEffects.Count -eq 0 -and [string](Get-Field $describe 'status') -ceq 'persistent' -and [string](Get-Field $describe 'maturity') -ceq 'library' -and -not [bool](Get-Field $describe 'deprecated') -and [string](Get-Field $describe 'kind') -ceq 'word') @{effects=$describeEffects;status=(Get-Field $describe 'status');maturity=(Get-Field $describe 'maturity');deprecated=(Get-Field $describe 'deprecated');kind=(Get-Field $describe 'kind')}
        $coverage = Get-Field $describe 'coverage'
        Add-Check 'current complete own instruction and branch coverage' ([string](Get-Field $coverage 'status') -ceq 'current' -and $describeTestCount -gt 0 -and [int](Get-Field $coverage 'instructionsTotal') -gt 0 -and [int](Get-Field $coverage 'instructionsCovered') -eq [int](Get-Field $coverage 'instructionsTotal') -and [int](Get-Field $coverage 'branchesCovered') -eq [int](Get-Field $coverage 'branchesTotal')) $coverage

        $exampleResponses = @()
        try {
            $exampleResponses = Invoke-JsonlSession $scratchProject @([ordered]@{op='example';word=[string]$targetOracle.symbol}) 'run target examples'
            $exampleResponse = $exampleResponses[0]
            if (-not [bool]$exampleResponse.ok) {
                Add-Check 'target examples execute in a fresh process' $false (Get-ProtocolError $exampleResponse)
            } else {
                $exampleResults = @(Get-Field (Get-Field $exampleResponse 'data') 'results')
                $targetReference = ([string]$targetOracle.symbol -replace '\.','::')
                Add-Check 'all attached examples pass with meaningful target calls' ($exampleResults.Count -eq $exampleNames.Count -and $exampleResults.Count -gt 0 -and @($exampleResults | Where-Object { -not [bool](Get-Field $_ 'passed') -or [string](Get-Field $_ 'source') -notmatch [regex]::Escape($targetReference) -or [string](Get-Field $_ 'source') -notmatch '=>'}).Count -eq 0) @{count=$exampleResults.Count;results=$exampleResults}
            }
        } catch { Add-MetadataFailure 'target example execution' $_ }

        $coverageResponses = @()
        try {
            $coverageResponses = Invoke-JsonlSession $scratchProject @([ordered]@{op='test-all'},[ordered]@{op='describe';word=[string]$targetOracle.symbol}) 'refresh target coverage'
            $metadataTestExecutions += 1
            $freshTestResponse = $coverageResponses[0]
            $freshDescribeResponse = $coverageResponses[1]
            $freshTestRows = @()
            if ([bool]$freshTestResponse.ok) { $freshTestRows = @(Get-Field (Get-Field $freshTestResponse 'data') 'results') }
            $freshDescribe = if ([bool]$freshDescribeResponse.ok) { Get-Field $freshDescribeResponse 'data' } else { $null }
            $freshCoverage = Get-Field $freshDescribe 'coverage'
            Add-Check 'refreshed tests all pass' ([bool]$freshTestResponse.ok -and $freshTestRows.Count -gt 0 -and @($freshTestRows | Where-Object { -not [bool](Get-Field $_ 'passed') }).Count -eq 0) @{failed=@($freshTestRows | Where-Object { -not [bool](Get-Field $_ 'passed') });protocolError=$(if (-not [bool]$freshTestResponse.ok) { Get-ProtocolError $freshTestResponse } else { $null })}
            Add-Check 'refreshed own coverage remains complete' ([bool]$freshDescribeResponse.ok -and [string](Get-Field $freshCoverage 'status') -ceq 'current' -and [int](Get-Field $freshCoverage 'instructionsTotal') -gt 0 -and [int](Get-Field $freshCoverage 'instructionsCovered') -eq [int](Get-Field $freshCoverage 'instructionsTotal') -and [int](Get-Field $freshCoverage 'branchesCovered') -eq [int](Get-Field $freshCoverage 'branchesTotal')) $freshCoverage
        } catch { Add-MetadataFailure 'coverage refresh and repeated tests' $_ }
    }

    $script:checkCategory = 'behavior'
    if (-not $oracleUsable -or -not $targetUsable -or -not $scratchReady) {
        Add-Check 'safe executable target and independent oracle are available for behavior' $false @{targetUsable=$targetUsable;oracleUsable=$oracleUsable;safetyStop=$safetyStop}
    } else {
        $caseList = @($targetOracle.cases)
        for ($offset=0; $offset -lt $caseList.Count; $offset += 45) {
            $end = [Math]::Min($offset + 45,$caseList.Count)
            $evalRequests = [Collections.Generic.List[object]]::new()
            for ($index=$offset; $index -lt $end; $index++) {
                $case = $caseList[$index]
                $expected = $independentCases[$index]
                $id = ConvertTo-Json -InputObject ([string]$oracle.defaults.customerId) -Compress
                $email = ConvertTo-Json -InputObject ([string]$oracle.defaults.email) -Compress
                $kind = ConvertTo-Json -InputObject ([string]$case.kind) -Compress
                $balance = [string]$expected.balanceMinor
                $customer = 'customer::new(id = CustomerId::new(' + $id + '), email = Email::new(' + $email + '), kind = ' + $kind + ', balance = Money::new(' + $balance + '), created-at = Instant::new("' + $oracleCreatedAt + '"))'
                $reference = ([string]$targetOracle.symbol) -replace '^([^.]+)\.', '$1::'
                $evalRequests.Add([ordered]@{op='eval';frontend='flow';structured=$true;code="$reference($customer)"})
            }
            $batchResponses = @()
            $batchFailure = $null
            try {
                $batchResponses = Invoke-JsonlSession $scratchProject @($evalRequests) "independent oracle cases $($offset + 1)-$end"
            } catch { $batchFailure = $_.Exception.Message }
            if ($null -ne $batchFailure) {
                for ($index=$offset; $index -lt $end; $index++) {
                    $case = $caseList[$index]
                    $expected = $independentCases[$index]
                    $caseResults.Add([ordered]@{index=$index+1;id=[string]$case.id;executed=$false;responseOk=$null;passed=$false;expected=[ordered]@{type=$expected.type;value=$expected.value};actual=$null;diagnostic="Batch protocol failure: $batchFailure";checks=@()})
                    Add-Check "oracle case $($case.id) was executed" $false "Batch protocol failure: $batchFailure"
                }
                continue
            }
            for ($local=0; $local -lt $evalRequests.Count; $local++) {
                $absoluteIndex = $offset + $local
                $case = $caseList[$absoluteIndex]
                $expected = $independentCases[$absoluteIndex]
                $response = $batchResponses[$local]
                $checkStart = $behaviorChecks.Count
                $caseError = $null
                try { Assert-LanguageCase $response $expected ([string]$case.id) } catch { $caseError = $_.Exception.Message }
                $localChecks = [Collections.Generic.List[object]]::new()
                for ($checkIndex=$checkStart; $checkIndex -lt $behaviorChecks.Count; $checkIndex++) { $localChecks.Add($behaviorChecks[$checkIndex]) }
                $data = Get-Field $response 'data'
                $actual = [ordered]@{stackTypes=@(Get-Field $data 'stackTypes');structured=$null;effects=(Get-Field $data 'effects');console=@(Get-Field $data 'console')}
                if ([bool](Get-Field $response 'ok')) {
                    try { $actual.structured = Get-StructuredValue $data } catch { if ($null -eq $caseError) { $caseError = $_.Exception.Message } }
                }
                $localChecksPassed = @($localChecks | Where-Object { -not [bool]$_.passed }).Count -eq 0
                $casePassed = [bool](Get-Field $response 'ok') -and $null -eq $caseError -and $localChecksPassed
                $diagnostic = $caseError
                if ([string]::IsNullOrWhiteSpace($diagnostic) -and -not $casePassed) {
                    $diagnostic = (@($localChecks | Where-Object { -not [bool]$_.passed } | ForEach-Object { [string]$_.name }) -join '; ')
                    if ([string]::IsNullOrWhiteSpace($diagnostic)) { $diagnostic = 'The structured result did not match the independent oracle.' }
                }
                if (-not [bool](Get-Field $response 'ok')) { $diagnostic = Get-ProtocolError $response }
                Add-Check "oracle case $($case.id) matches independent result" $casePassed @{expected=[ordered]@{type=$expected.type;value=$expected.value};actual=$actual;diagnostic=$diagnostic}
                $caseResults.Add([ordered]@{index=$absoluteIndex+1;id=[string]$case.id;executed=$true;responseOk=[bool](Get-Field $response 'ok');passed=$casePassed;expected=[ordered]@{type=$expected.type;value=$expected.value};actual=$actual;diagnostic=$diagnostic;checks=@($localChecks)})
            }
        }
    }
} catch {
    $failure = $_.Exception.Message
    $orchestrationError = [ordered]@{message=$_.Exception.Message;scriptStackTrace=$_.ScriptStackTrace;position=$_.InvocationInfo.PositionMessage}
    Add-MetadataFailure 'verifier orchestration completed' $_
} finally {
    if ($null -ne $resolvedProject -and (Test-Path -LiteralPath $resolvedProject -PathType Container)) {
        try {
            $actorInventoryAfter = Get-RelativeInventory $resolvedProject
            $actorHashAfter = Get-TreeHash $resolvedProject
            $beforeJson = Get-CanonicalJson @($actorInventoryBefore)
            $afterJson = Get-CanonicalJson @($actorInventoryAfter)
            Add-Check 'actor project remains unchanged by verification' ($actorHashBefore -ceq $actorHashAfter -and $beforeJson -ceq $afterJson) @{before=$actorHashBefore;after=$actorHashAfter}
        } catch { Add-MetadataFailure 'actor project preservation inventory' $_ }
    }
    if ($null -ne $scratchProject -and (Test-Path -LiteralPath $scratchProject -PathType Container)) {
        $safe = $false
        try { $safe = (Test-Within $scratchProject $testsRoot) -and [IO.Path]::GetFileName($scratchProject).StartsWith('verify-flat-', [StringComparison]::Ordinal) } catch { }
        if ($safe) {
            try {
                Remove-Item -LiteralPath $scratchProject -Recurse -Force
                Add-Check 'verifier scratch copy safely removed' (-not (Test-Path -LiteralPath $scratchProject))
            } catch { Add-MetadataFailure 'verifier scratch cleanup' $_ }
        } else { Add-MetadataFailure 'verifier scratch cleanup stayed inside its owned root' 'Unsafe scratch path was not removed.' }
    }

    $metadataPassed = @($metadataChecks | Where-Object { -not [bool]$_.passed }).Count -eq 0
    $expectedCaseCount = if ($null -ne $targetOracle) { @($targetOracle.cases).Count } else { 0 }
    $behaviorPassed = $oracleUsable -and $targetUsable -and $caseResults.Count -eq $expectedCaseCount -and $expectedCaseCount -gt 0 -and @($caseResults | Where-Object { -not [bool]$_.executed -or -not [bool]$_.passed }).Count -eq 0
    $passed = $metadataPassed -and $behaviorPassed
    if (-not $passed -and [string]::IsNullOrWhiteSpace($failure)) {
        if (-not [string]::IsNullOrWhiteSpace($safetyStop)) { $failure = $safetyStop }
        else {
            $metadataFailures = @($metadataChecks | Where-Object { -not [bool]$_.passed } | ForEach-Object { [string]$_.name })
            $behaviorFailures = @($caseResults | Where-Object { -not [bool]$_.passed } | ForEach-Object { [string]$_.id })
            $failure = "metadataPassed=$metadataPassed; behaviorPassed=$behaviorPassed; metadataFailures=$($metadataFailures -join ', '); failedCases=$($behaviorFailures -join ', ')"
        }
    }
    if ($null -eq $evidenceTarget) {
        try { $evidenceTarget = Resolve-RepoPath $EvidencePath } catch { $evidenceTarget = Join-Path $repo "reports/evidence/authoring-help-flat-$($TaskId.ToLowerInvariant()).json" }
    }
    try {
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($evidenceTarget)) | Out-Null
        $executedCount = @($caseResults | Where-Object { [bool]$_.executed }).Count
        $evidence = [ordered]@{
            schemaVersion=1; studyId='business-policy-help-002'; mode=$Mode; taskId=$TaskId
            passed=$passed; metadataPassed=$metadataPassed; behaviorPassed=$behaviorPassed; safetyStop=$safetyStop; failure=$failure; orchestrationError=$orchestrationError
            evidencePath=$evidenceTarget;requestedEvidencePath=$requestedEvidenceTarget;evidencePathCollision=$evidenceCollision
            sequenceIndex=$(if ($TaskId -in $sequence) { [array]::IndexOf($sequence,$TaskId)+1 } else { $null })
            resultKind=$(if ($checkFrozenPin) { 'frozen-actor-acceptance' } else { 'unfrozen-preflight-or-control'})
            checkFrozenPin=[bool]$RequireFrozenPin; frozenPin=$frozenPinInfo; prelaunch=$frozenPinRecord
            startedUtc=$startedUtc; finishedUtc=[DateTime]::UtcNow.ToString('O')
            verifierSha256=$(if (Test-Path -LiteralPath $PSCommandPath -PathType Leaf) { Get-Sha256 $PSCommandPath } else { $null })
            oracle=[ordered]@{path=$oraclePath;sha256=$(if (Test-Path -LiteralPath $oraclePath -PathType Leaf) { Get-Sha256 $oraclePath } else { $null });symbol=$(if ($null -ne $targetOracle) { $targetOracle.symbol } else { $null });cases=$expectedCaseCount;independentResults=$caseOracleEvidence}
            behavior=[ordered]@{targetUsable=$targetUsable;casesExpected=$expectedCaseCount;casesExecuted=$executedCount;casesRecorded=$caseResults.Count;cases=$caseResults.ToArray()}
            project=[ordered]@{argumentPath=$originalProjectPath;path=$resolvedProject;treeSha256=$actorHashAfter;filesBefore=$actorInventoryBefore;filesAfter=$actorInventoryAfter}
            outputTreeSha256=$actorHashAfter
            startingProject=[ordered]@{argumentPath=$originalStartingProjectPath;path=$resolvedStartingProject;state=$startingStateMetadata;preservation=$preservation}
            runtime=$runtimeInfo; reuse=$reuse; checks=$checks.ToArray(); metadataChecks=$metadataChecks.ToArray(); behaviorChecks=$behaviorChecks.ToArray()
            testExecutionCount=$metadataTestExecutions
            jsonlSessions=$sessions.ToArray(); processRuns=$processRuns.ToArray()
            oracleProtocol='Independent BigInteger arithmetic; raw Kind uses ordinal exact comparison; Money and Int payloads are canonical signed Int64 decimal strings.'
            limits=@('Metadata failures remain distinct from behavior outcomes.','A missing, untyped, impure, or unreloadable target prevents oracle execution.','No efficiency or token-use claim is made.')
        }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($evidenceTarget)) | Out-Null
        Write-NewTextFile $evidenceTarget ((ConvertTo-Json -InputObject $evidence -Depth 100) + [Environment]::NewLine)
    } catch {
        $passed = $false
        $failure = "Could not write verifier evidence: $($_.Exception.Message)"
        try {
            $evidenceTarget = Get-UniqueEvidencePath ([string]$evidenceTarget) $TaskId 'recovery'
            if ($null -ne $evidence) { $evidence.passed=$false; $evidence.failure=$failure; $evidence.evidencePath=$evidenceTarget }
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($evidenceTarget)) | Out-Null
            Write-NewTextFile $evidenceTarget ((ConvertTo-Json -InputObject $evidence -Depth 100) + [Environment]::NewLine)
        } catch { $failure += "; recovery evidence write failed: $($_.Exception.Message)" }
    }
}

if (-not $passed) {
    [Console]::Error.WriteLine("Authoring-help flat/$TaskId failed. Evidence: $evidenceTarget. $failure")
    exit 1
}
Write-Output "Authoring-help flat/$TaskId passed $($checks.Count) checks. Evidence: $evidenceTarget"
