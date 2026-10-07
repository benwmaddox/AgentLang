#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('flat','retained','reset-rich')][string]$Arm,
    [Parameter(Mandatory)][ValidateSet('B1','B2')][string]$Block,
    [Parameter(Mandatory)][ValidateSet('S01','S07')][string]$TaskId,
    [string]$RunId,
    [string]$ProjectPath,
    [string]$CliDll,
    [string]$EvidencePath,
    [string]$StartingProjectPath,
    [switch]$BaselineFallback,
    [switch]$RequireFrozenPin
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$sequence = @('S01','S07')
$expectedCounts = @{ S01 = 10; S07 = 54 }
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
$oraclePath = Join-Path $repo 'experiments/AgentLang.SubagentTrials/business-policy-retention-003/acceptance.json'
$resolvedProject = $null
$resolvedStartingProject = $null
$originalProjectPath = $null
$originalStartingProjectPath = $null
$resolvedCli = $null
$scratchProject = $null
$baselineScratch = $null
$baselineCheck = $null
$testsRoot = Join-Path $repo '.agentlang/business-policy-retention-003/tests'
$actorInventoryBefore = @()
$actorInventoryAfter = @()
$preservation = [ordered]@{ arm=$Arm; checked=$false; sourceWords=0; nominalTypes=0; unchanged=$null }
$reuse = [ordered]@{ candidatePriorTaskSymbols=@(); priorTaskSymbols=$null; startingDictionaryTaskSymbols=$null; directDependencies=@(); transitiveDependencies=@(); reusedPriorTaskSymbols=$null; status='unavailable until starting inventory and dependency graph are checked' }
$runtimeInfo = [ordered]@{}
$evidenceTarget = $null
$startingStateMetadata = $null
$startingStatePassed = $false
$evidenceCollision = $false
$evidenceDestinationValid = $true
$checkFrozenPin = $false
$frozenPinInfo = [ordered]@{required=[bool]$RequireFrozenPin;checked=$false;status='not checked';path=$null;sha256=$null}
$frozenPinRecord = $null
$mustRequireFrozenPin = [bool]$RequireFrozenPin
$orchestrationError = $null
$actorHashBefore = $null
$actorHashAfter = $null
$actorInventoryCapturedAtUtc = $null
$requestedEvidenceTarget = $null
$startedUtc = [DateTime]::UtcNow.ToString('O')
$utf8NoBom = [Text.UTF8Encoding]::new($false)
$runIndexMap = @{
    B1 = @{ flat=@('R01','R02'); retained=@('R03','R04'); 'reset-rich'=@('R05','R06') }
    B2 = @{ 'reset-rich'=@('R07','R08'); flat=@('R09','R10'); retained=@('R11','R12') }
}
$taskSlot = [array]::IndexOf($sequence,$TaskId)
$expectedRunId = if ($taskSlot -ge 0 -and $runIndexMap.ContainsKey($Block)) { [string]$runIndexMap[$Block][$Arm][$taskSlot] } else { $null }
if ([string]::IsNullOrWhiteSpace($RunId)) { $RunId = $expectedRunId }

function Get-Field($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    if ($Object -is [Collections.IDictionary]) { return $Object[$Name] }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-JsonStringProperty([string]$Path, [string[]]$PropertyPath) {
    $document = [System.Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($Path))
    try {
        $element = $document.RootElement
        foreach ($name in $PropertyPath) {
            if ($element.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
                return [ordered]@{ found=$false; valueKind=[string]$element.ValueKind; value=$null }
            }
            $nextElement = $null
            $matches = 0
            $exactName = $false
            foreach ($property in $element.EnumerateObject()) {
                if ([StringComparer]::OrdinalIgnoreCase.Equals($property.Name,$name)) {
                    $matches++
                    $nextElement = $property.Value
                    $exactName = $property.Name -ceq $name
                }
            }
            if ($matches -ne 1 -or -not $exactName) {
                $kind = if ($matches -gt 1) { 'Duplicate' } elseif ($matches -eq 1) { 'NameMismatch' } else { 'Undefined' }
                return [ordered]@{ found=$false; valueKind=$kind; value=$null }
            }
            $element = $nextElement
        }
        $value = if ($element.ValueKind -eq [System.Text.Json.JsonValueKind]::String) { $element.GetString() } else { $null }
        return [ordered]@{ found=$true; valueKind=[string]$element.ValueKind; value=$value }
    } finally {
        $document.Dispose()
    }
}

function Test-PinnedRepoRelativePath([string]$PinnedPath, [string]$ExpectedFullPath) {
    $expectedFull = [IO.Path]::GetFullPath($ExpectedFullPath)
    $expectedRelative = [IO.Path]::GetRelativePath($repo,$expectedFull).Replace('\','/')
    $safe = -not [string]::IsNullOrWhiteSpace($PinnedPath) -and -not [IO.Path]::IsPathFullyQualified($PinnedPath) -and -not [IO.Path]::IsPathRooted($PinnedPath) -and $PinnedPath -notmatch '(^|/)\.\.(/|$)' -and $PinnedPath -notmatch '\\'
    $resolved = $null
    if ($safe) {
        try {
            $resolved = [IO.Path]::GetFullPath($PinnedPath,$repo)
            $safe = Test-Within $resolved $repo
        } catch {
            $safe = $false
            $resolved = $null
        }
    }
    return [ordered]@{
        passed=($safe -and $PinnedPath -ceq $expectedRelative -and $resolved -ieq $expectedFull)
        expected=$expectedRelative
        actual=$PinnedPath
        resolvedExpected=$expectedFull
        resolvedActual=$resolved
    }
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
    $rows = [Collections.Generic.SortedDictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach ($file in Get-ChildItem -LiteralPath $resolved -Recurse -Force -File) {
        $relative = [IO.Path]::GetRelativePath($resolved, $file.FullName).Replace('\','/')
        if ($relative -match '(^|/)(bin|obj)(/|$)') { continue }
        $rows.Add($relative,[ordered]@{ path=$relative; bytes=[long]$file.Length; sha256=(Get-Sha256 $file.FullName) })
    }
    $result = [object[]]::new($rows.Count)
    $index = 0
    foreach ($row in $rows.Values) { $result[$index] = $row; $index++ }
    return $result
}

function Get-FreezeInventory([string]$Root) {
    $resolved = [IO.Path]::GetFullPath($Root)
    $rows = [Collections.Generic.SortedDictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach ($file in Get-ChildItem -LiteralPath $resolved -Recurse -Force -File) {
        $relative = [IO.Path]::GetRelativePath($resolved,$file.FullName).Replace('\','/')
        if ($relative.Split('/') -contains 'bin' -or $relative.Split('/') -contains 'obj') { continue }
        $rows.Add($relative,[ordered]@{
            path=$relative
            bytes=[long]$file.Length
            sha256=Get-Sha256 $file.FullName
        })
    }
    $result = [object[]]::new($rows.Count)
    $index = 0
    foreach ($row in $rows.Values) { $result[$index] = $row; $index++ }
    return $result
}

function Get-CanonicalInventoryRows([object[]]$Rows) {
    $sorted = [Collections.Generic.SortedDictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach ($row in $Rows) {
        $path = ([string](Get-Field $row 'path')).Replace('\','/')
        $sorted.Add($path,[ordered]@{
            path=$path
            bytes=[long](Get-Field $row 'bytes')
            sha256=([string](Get-Field $row 'sha256')).ToLowerInvariant()
        })
    }
    $result = [object[]]::new($sorted.Count)
    $index = 0
    foreach ($row in $sorted.Values) { $result[$index] = $row; $index++ }
    return $result
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
    $directory = Join-Path $repo '.agentlang/business-policy-retention-003/evidence'
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    for ($attempt=0; $attempt -lt 20; $attempt++) {
        $candidate = Join-Path $directory ("081-verifier-$RunId-$Reason-$([Guid]::NewGuid().ToString('N')).json")
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

function Validate-FrozenPin([string]$StartPath,[string]$ActorPath,[string]$RuntimeCliPath,[string]$CurrentArm,[string]$CurrentBlock,[string]$CurrentTask,[string]$CurrentRunId,$Acceptance) {
    $runDirectory = Split-Path -Parent $StartPath
    $pinPath = Join-Path $runDirectory 'prelaunch.json'
    $frozenPinInfo.path = $pinPath
    if (-not (Test-Path -LiteralPath $pinPath -PathType Leaf)) {
        if ($mustRequireFrozenPin) { Add-Check 'required frozen prelaunch pin exists' $false $pinPath }
        else { $checks.Add([ordered]@{name='unfrozen verifier run classified as preflight/control';passed=$true;details='No sibling prelaunch.json was supplied.'}) }
        $frozenPinInfo.status = if ($mustRequireFrozenPin) { 'required pin missing' } else { 'unfrozen-preflight-or-control' }
        return $null
    }

    $pinCheckStart = $metadataChecks.Count
    $pin = Get-Content -LiteralPath $pinPath -Raw | ConvertFrom-Json -AsHashtable -Depth 100
    $pinHash = Get-Sha256 $pinPath
    $frozenPinInfo.sha256 = $pinHash
    $preparedStateData = if ($null -ne $startingStateMetadata) { Get-Field $startingStateMetadata 'data' } else { $null }
    $preparedStateAvailable = $null -ne $preparedStateData -and [bool]$startingStatePassed
    Add-Check 'frozen pin has validated prepared-state metadata for state-bound fields' $preparedStateAvailable @{available=$preparedStateAvailable}
    $expectedIndex = [array]::IndexOf($sequence,$CurrentTask) + 1
    Add-Check 'frozen pin schema, study, arm, block, task, sequence, and run match' ([int]$pin.schemaVersion -eq 1 -and [string]$pin.studyId -ceq 'business-policy-retention-003' -and [string]$pin.arm -ceq $CurrentArm -and [string]$pin.block -ceq $CurrentBlock -and [string]$pin.taskId -ceq $CurrentTask -and [int]$pin.sequenceIndex -eq $expectedIndex -and [string]$pin.runId -ceq $CurrentRunId)
    Add-Check 'frozen pin is launchable and not a diagnostic control' ((Get-Field $pin 'launchable') -eq $true -and (Get-Field $pin 'controlOnly') -ne $true) @{launchable=(Get-Field $pin 'launchable');controlOnly=(Get-Field $pin 'controlOnly')}
    $pinnedActor = [string](Get-Field $pin 'projectPath')
    Add-Check 'frozen pin actor project path matches argument' ([IO.Path]::IsPathFullyQualified($pinnedActor) -and [IO.Path]::GetFullPath($pinnedActor) -ieq [IO.Path]::GetFullPath($ActorPath)) @{expected=$ActorPath;actual=$pinnedActor}
    $pinnedStart = [string](Get-Field $pin 'startingProjectPath')
    $pinnedStartCheck = Test-PinnedRepoRelativePath $pinnedStart $StartPath
    Add-Check 'frozen pin starting project path matches argument' ([bool]$pinnedStartCheck.passed) $pinnedStartCheck

    $statePath = Join-Path $runDirectory 'starting-state.json'
    $declaredStatePath = [string](Get-Field $pin 'startingStatePath')
    $expectedStatePath = [IO.Path]::GetRelativePath($repo,$statePath).Replace('\','/')
    Add-Check 'frozen run starting-state path matches sibling state' ($declaredStatePath -ceq $expectedStatePath -and (Test-Path -LiteralPath $statePath -PathType Leaf)) @{expected=$expectedStatePath;actual=$declaredStatePath}
    $stateHash = if (Test-Path -LiteralPath $statePath -PathType Leaf) { Get-Sha256 $statePath } else { $null }
    Add-Check 'frozen starting-state hash matches pin' ($null -ne $stateHash -and $stateHash -ceq ([string]$pin.startingStateSha256).ToLowerInvariant()) @{expected=$pin.startingStateSha256;actual=$stateHash}
    $promptPathValue = [string](Get-Field $pin 'promptPath')
    if ([string]::IsNullOrWhiteSpace($promptPathValue)) { $promptPathValue = 'prompt.txt' }
    $promptPath = Resolve-FileFromRoot $runDirectory $promptPathValue
    Add-Check 'frozen actor prompt exists and stays in run directory' ((Test-Within $promptPath $runDirectory) -and (Test-Path -LiteralPath $promptPath -PathType Leaf)) $promptPath
    $promptHash = if (Test-Path -LiteralPath $promptPath -PathType Leaf) { Get-Sha256 $promptPath } else { $null }
    Add-Check 'frozen prompt hash and byte count match pin' ($null -ne $promptHash -and $promptHash -ceq ([string]$pin.promptSha256).ToLowerInvariant() -and (Get-Item -LiteralPath $promptPath).Length -eq [long]$pin.promptUtf8Bytes) @{expected=$pin.promptSha256;actual=$promptHash;bytes=$(if (Test-Path -LiteralPath $promptPath -PathType Leaf) { (Get-Item -LiteralPath $promptPath).Length } else { $null })}

    $pinnedRevision = [string]$pin.sourceRevision
    $revisionResolves = $pinnedRevision -match '^[0-9a-fA-F]{40}$'
    if ($revisionResolves) {
        $resolvedRevision = (& git -C $repo rev-parse --verify "$pinnedRevision^{commit}" 2>$null | Out-String).Trim()
        $revisionResolves = $LASTEXITCODE -eq 0 -and $resolvedRevision -ceq $pinnedRevision
    }
    Add-Check 'frozen source revision is a committed clean-source snapshot' ($revisionResolves -and (Get-Field $pin 'dirty') -eq $false) @{pinned=$pinnedRevision;resolves=$revisionResolves;pinDirty=(Get-Field $pin 'dirty')}

    $globalFreezeRelative = [string](Get-Field $pin 'globalFreezePath')
    $globalFreezeExpected = 'experiments/AgentLang.SubagentTrials/business-policy-retention-003/artifacts/global-freeze.json'
    $globalFreezePath = Resolve-RepoPath $globalFreezeRelative
    $globalFreezeSafe = $globalFreezeRelative -ceq $globalFreezeExpected -and (Test-Within $globalFreezePath $repo) -and (Test-Path -LiteralPath $globalFreezePath -PathType Leaf)
    $globalFreezeHash = if ($globalFreezeSafe) { Get-Sha256 $globalFreezePath } else { $null }
    $globalFreezeGitHash = if ($revisionResolves -and $globalFreezeSafe) { Get-GitBlobSha256 $pinnedRevision $globalFreezeRelative } else { $null }
    Add-Check 'global freeze manifest hash matches pin and committed source revision' ($globalFreezeSafe -and $globalFreezeHash -ceq ([string]$pin.globalFreezeSha256).ToLowerInvariant() -and $globalFreezeGitHash -ceq $globalFreezeHash) @{path=$globalFreezeRelative;expected=$pin.globalFreezeSha256;working=$globalFreezeHash;revision=$globalFreezeGitHash}
    if (-not $globalFreezeSafe) { throw 'Pinned global freeze manifest is missing or outside the repository.' }
    $globalFreeze = Get-Content -LiteralPath $globalFreezePath -Raw | ConvertFrom-Json -AsHashtable -Depth 100
    $pinClockToken = Get-JsonStringProperty $pinPath @('clockValue')
    $globalClockToken = Get-JsonStringProperty $globalFreezePath @('host','clockValue')
    $expectedClockValue = '2000-01-01T00:00:00Z'
    $pinClockIsExactString = $pinClockToken.found -and $pinClockToken.valueKind -ceq 'String' -and $pinClockToken.value -ceq $expectedClockValue
    $globalClockIsExactString = $globalClockToken.found -and $globalClockToken.valueKind -ceq 'String' -and $globalClockToken.value -ceq $expectedClockValue
    Add-Check 'frozen pin clock is the exact reviewed JSON string literal' $pinClockIsExactString @{valueKind=$pinClockToken.valueKind;value=$pinClockToken.value;expected=$expectedClockValue}
    Add-Check 'global host clock is the exact reviewed JSON string literal' $globalClockIsExactString @{valueKind=$globalClockToken.valueKind;value=$globalClockToken.value;expected=$expectedClockValue}
    Add-Check 'global freeze manifest study and model match pinned protocol' ([int]$globalFreeze.schemaVersion -eq 1 -and [string]$globalFreeze.studyId -ceq 'business-policy-retention-003' -and [string]$globalFreeze.model -ceq 'gpt-6-luna' -and [string]$globalFreeze.reasoningEffort -ceq 'max' -and [string]$globalFreeze.forkTurns -ceq 'none' -and [string]$globalFreeze.profile -ceq 'agentlang')

    $expectedAllowedOperations = @('callers','commit','context','define','dependencies','describe','diff','effects','eval','example','examples','failed-tests','graph','help','history','ir','replace-word','search','search-dependency','search-output','search-type','source','task.begin','task.commit','task.log','task.status','test','test-all','tests','transitive-callers','transitive-dependencies','type-of','words') | Sort-Object -CaseSensitive
    $hostSettings = Get-Field $globalFreeze 'host'
    $pinnedAllowed = @((Get-Field $pin 'allowedOperations') | ForEach-Object { [string]$_ } | Sort-Object -CaseSensitive)
    $globalAllowed = @((Get-Field $hostSettings 'allowedOperations') | ForEach-Object { [string]$_ } | Sort-Object -CaseSensitive)
    Add-Check 'frozen actor allowlist exactly matches the reviewed V2 help-enabled operation set' (($pinnedAllowed -join "`0") -ceq ($expectedAllowedOperations -join "`0") -and ($globalAllowed -join "`0") -ceq ($expectedAllowedOperations -join "`0")) $pinnedAllowed
    Add-Check 'frozen model and raw protocol limits match global design' ([string](Get-Field $pin 'model') -ceq 'gpt-6-luna' -and [string](Get-Field $pin 'reasoningEffort') -ceq 'max' -and [string](Get-Field $pin 'forkTurns') -ceq 'none' -and [string](Get-Field $pin 'hostProtocolVersion') -ceq 'subagent-trial-host-v2' -and [int](Get-Field $pin 'maxExchanges') -eq 100 -and [int](Get-Field $pin 'maxRequestBytes') -eq 262144 -and [int](Get-Field $pin 'maxResponseBytes') -eq 524288 -and (Get-Field $pin 'maxInspectionResponseBytes') -eq $null -and [int](Get-Field $pin 'exchangeTimeoutMilliseconds') -eq 120000 -and (Get-Field $pin 'inspectionBudgetEnabled') -eq $false -and [string](Get-Field $pin 'inspectionClassifierVersion') -ceq 'trial-host-inspection-v1' -and [string](Get-Field $pin 'profile') -ceq 'agentlang' -and $pinClockIsExactString -and @(Get-Field $pin 'additionalCliArguments').Count -eq 0 -and @(Get-Field $pin 'capabilities').Count -eq 0) @{protocolVersion=(Get-Field $pin 'hostProtocolVersion');allowlist=$pinnedAllowed;clockValue=$pinClockToken.value}
    $pinnedTransportControls = @((Get-Field $pin 'transportControls') | ForEach-Object { [string]$_ } | Sort-Object -CaseSensitive)
    $globalTransportControls = @((Get-Field $hostSettings 'transportControls') | ForEach-Object { [string]$_ } | Sort-Object -CaseSensitive)
    $hostPairOk = [string](Get-Field $hostSettings 'path') -ceq [string](Get-Field $pin 'hostPath') -and [string](Get-Field $hostSettings 'sha256') -ceq [string](Get-Field $pin 'hostSha256')
    $hostFieldsOk = [string](Get-Field $hostSettings 'hostProtocolVersion') -ceq [string](Get-Field $pin 'hostProtocolVersion') -and
        ($globalTransportControls -join "`0") -ceq ($pinnedTransportControls -join "`0") -and
        [string](Get-Field $hostSettings 'profile') -ceq [string](Get-Field $pin 'profile') -and
        [int](Get-Field $hostSettings 'maxExchanges') -eq [int](Get-Field $pin 'maxExchanges') -and
        [int](Get-Field $hostSettings 'maxRequestBytes') -eq [int](Get-Field $pin 'maxRequestBytes') -and
        [int](Get-Field $hostSettings 'maxResponseBytes') -eq [int](Get-Field $pin 'maxResponseBytes') -and
        (Get-Field $hostSettings 'maxInspectionResponseBytes') -eq (Get-Field $pin 'maxInspectionResponseBytes') -and
        [int](Get-Field $hostSettings 'exchangeTimeoutMilliseconds') -eq [int](Get-Field $pin 'exchangeTimeoutMilliseconds') -and
        (Get-CanonicalJson @((Get-Field $hostSettings 'additionalCliArguments'))) -ceq (Get-CanonicalJson @((Get-Field $pin 'additionalCliArguments'))) -and
        (Get-CanonicalJson @((Get-Field $hostSettings 'capabilities'))) -ceq (Get-CanonicalJson @((Get-Field $pin 'capabilities'))) -and
        $globalClockIsExactString -and $pinClockIsExactString -and $globalClockToken.value -ceq $pinClockToken.value -and
        (Get-Field $hostSettings 'inspectionBudgetEnabled') -eq (Get-Field $pin 'inspectionBudgetEnabled') -and
        [string](Get-Field $hostSettings 'inspectionClassifierVersion') -ceq [string](Get-Field $pin 'inspectionClassifierVersion')
    Add-Check 'global host settings and per-run host pin match field by field' ($hostPairOk -and $hostFieldsOk) @{global=$hostSettings;pinHostPath=$pin.hostPath;pinHostSha256=$pin.hostSha256;pinTransportControls=$pinnedTransportControls}

    $repoPins = @(
        @{path='oraclePath';hash='oracleSha256';expected='experiments/AgentLang.SubagentTrials/business-policy-retention-003/acceptance.json'},
        @{path='designPath';hash='designSha256';expected='experiments/AgentLang.SubagentTrials/business-policy-retention-003/design.json'},
        @{path='primerPath';hash='primerSha256';expected='experiments/AgentLang.SubagentTrials/business-policy-retention-003/language-primer.md'},
        @{path='taskPath';hash='taskSha256';expected="experiments/AgentLang.Benchmarks/task-bank/public/$CurrentTask.json"},
        @{path='independentVerifierPath';hash='independentVerifierSha256';expected='scripts/Verify-RetentionTrial.ps1'},
        @{path='preflightPath';hash='preflightSha256';expected='scripts/Verify-RetentionPreflight.ps1'},
        @{path='preparerPath';hash='preparerSha256';expected='scripts/Prepare-RetentionTrial.ps1'},
        @{path='bootstrapperPath';hash='bootstrapperSha256';expected='scripts/Prepare-BusinessPolicyTrial.ps1'},
        @{path='freezerPath';hash='freezerSha256';expected='scripts/Freeze-RetentionTrial.ps1'},
        @{path='auditorPath';hash='auditorSha256';expected='scripts/Audit-RetentionTrace.ps1'},
        @{path='terminationAuditorPath';hash='terminationAuditorSha256';expected='scripts/Audit-SubagentTrialTerminationV2.ps1'},
        @{path='hostPath';hash='hostSha256';expected='scripts/Start-SubagentTrialHostV2.ps1'}
    )
    $repoPinResults = [Collections.Generic.List[object]]::new()
    foreach ($row in $repoPins) {
        $declaredPath = [string](Get-Field $pin $row.path)
        $declaredHash = ([string](Get-Field $pin $row.hash)).ToLowerInvariant()
        $pathSafe = $declaredPath -ceq $row.expected -and -not [IO.Path]::IsPathFullyQualified($declaredPath) -and (Test-Path -LiteralPath (Resolve-RepoPath $declaredPath) -PathType Leaf)
        $actualHash = if ($pathSafe) { Get-Sha256 (Resolve-RepoPath $declaredPath) } else { $null }
        $revisionHash = if ($revisionResolves -and $pathSafe) { Get-GitBlobSha256 $pinnedRevision $declaredPath } else { $null }
        $ok = $pathSafe -and $actualHash -ceq $declaredHash -and $revisionHash -ceq $declaredHash
        $repoPinResults.Add([ordered]@{path=$declaredPath;expectedPath=$row.expected;expectedSha256=$declaredHash;workingSha256=$actualHash;revisionSha256=$revisionHash;passed=$ok})
    }
    Add-Check 'all frozen verifier, preparation, freeze, audit, host, oracle, task, and primer hashes match source revision' (@($repoPinResults | Where-Object { -not $_.passed }).Count -eq 0) @($repoPinResults)
    foreach ($entry in $repoPins) {
        $globalEntryName = switch ($entry.path) {
            'oraclePath' {'oracle'} 'designPath' {'design'} 'primerPath' {'primer'} 'taskPath' {$null}
            'preparerPath' {'preparer'} 'bootstrapperPath' {'bootstrapper'} 'independentVerifierPath' {'independentVerifier'}
            'preflightPath' {'preflightVerifier'} 'auditorPath' {'traceAuditor'} 'terminationAuditorPath' {'terminationAuditor'} 'hostPath' {'host'}
            default {$null}
        }
        if ($null -ne $globalEntryName) {
            $globalEntry = Get-Field $globalFreeze $globalEntryName
            Add-Check "global $globalEntryName path and hash match per-run pin" ([string](Get-Field $globalEntry 'path') -ceq [string](Get-Field $pin $entry.path) -and [string](Get-Field $globalEntry 'sha256') -ceq [string](Get-Field $pin $entry.hash)) $globalEntry
        }
    }
    $globalTask = @((Get-Field $globalFreeze 'tasks') | Where-Object { [string](Get-Field $_ 'taskId') -ceq $CurrentTask })
    Add-Check 'global freeze task path and hash match per-run task pin' ($globalTask.Count -eq 1 -and [string](Get-Field $globalTask[0] 'path') -ceq [string]$pin.taskPath -and [string](Get-Field $globalTask[0] 'sha256') -ceq [string]$pin.taskSha256) $globalTask
    Add-Check 'global freeze source artifact inventory matches per-run pin' ((Get-CanonicalJson @((Get-Field $globalFreeze 'sourceArtifacts') | Sort-Object sourcePath)) -ceq (Get-CanonicalJson @((Get-Field $pin 'sourceArtifacts') | Sort-Object sourcePath)))

    $startInventory = @(Get-FreezeInventory $StartPath)
    $pinnedStart = @(Get-Field $pin 'startingProjectFiles')
    $stateProject = if ($preparedStateAvailable) { Get-Field $preparedStateData 'project' } else { $null }
    $stateStartInventoryHash = [string](Get-Field $stateProject 'inventorySha256')
    Add-Check 'frozen starting project files and tree hash match prepared state' ($preparedStateAvailable -and (Get-CanonicalJson @(Get-CanonicalInventoryRows $startInventory)) -ceq (Get-CanonicalJson @(Get-CanonicalInventoryRows $pinnedStart)) -and (Get-TreeHash $StartPath) -ceq ([string](Get-Field $pin 'startingProjectInventorySha256')).ToLowerInvariant() -and $stateStartInventoryHash -ceq ([string](Get-Field $pin 'startingProjectInventorySha256')).ToLowerInvariant()) @{files=@($startInventory).Count;inventorySha256=(Get-TreeHash $StartPath)}
    $pinnedActorFiles = @(Get-Field $pin 'actorProjectFiles')
    $stateActor = if ($preparedStateAvailable) { Get-Field $preparedStateData 'actor' } else { $null }
    $stateActorFiles = @(Get-Field $stateActor 'files')
    $stateProjectFiles = @(Get-Field $stateProject 'files')
    Add-Check 'frozen prelaunch actor inventory matches prepared state and immutable start' ($preparedStateAvailable -and (Get-CanonicalJson @(Get-CanonicalInventoryRows $pinnedActorFiles)) -ceq (Get-CanonicalJson @(Get-CanonicalInventoryRows $stateActorFiles)) -and (Get-CanonicalJson @(Get-CanonicalInventoryRows $stateActorFiles)) -ceq (Get-CanonicalJson @(Get-CanonicalInventoryRows $stateProjectFiles)) -and [string](Get-Field $stateActor 'inventorySha256') -ceq ([string](Get-Field $pin 'actorProjectInventorySha256')).ToLowerInvariant() -and [string](Get-Field $stateActor 'inventorySha256') -ceq $stateStartInventoryHash) @{files=@($stateActorFiles).Count;inventorySha256=(Get-Field $stateActor 'inventorySha256');actorOutputInventoryCheckedSeparately=$true}

    $runtimeRoots = Get-Field $pin 'runtimeRoots'
    $runtimeRootValues = @((Get-Field $runtimeRoots 'cliDllPath'),(Get-Field $runtimeRoots 'cliDirectoryPath'),(Get-Field $runtimeRoots 'businessDllPath'),(Get-Field $runtimeRoots 'businessDirectoryPath'))
    $cliDllPath = Resolve-RepoPath ([string](Get-Field $runtimeRoots 'cliDllPath'))
    $cliDirectoryPath = Resolve-RepoPath ([string](Get-Field $runtimeRoots 'cliDirectoryPath'))
    $businessDllPath = Resolve-RepoPath ([string](Get-Field $runtimeRoots 'businessDllPath'))
    $businessDirectoryPath = Resolve-RepoPath ([string](Get-Field $runtimeRoots 'businessDirectoryPath'))
    $invalidRuntimeRoots = @($runtimeRootValues | Where-Object { [string]::IsNullOrWhiteSpace([string]$_) -or [IO.Path]::IsPathFullyQualified([string]$_) -or [string]$_ -match '(^|[\\/])\.\.([\\/]|$)' })
    $runtimePathsSafe = $invalidRuntimeRoots.Count -eq 0 -and (Test-Within $cliDllPath $repo) -and (Test-Within $cliDirectoryPath $repo) -and (Test-Within $businessDllPath $repo) -and (Test-Within $businessDirectoryPath $repo)
    $stateRuntime = if ($preparedStateAvailable) { Get-Field $preparedStateData 'runtime' } else { $null }
    $globalRuntime = Get-Field $globalFreeze 'runtime'
    $runtimeRootMatches = $preparedStateAvailable -and $runtimePathsSafe -and (Test-Path -LiteralPath $cliDllPath -PathType Leaf) -and (Test-Path -LiteralPath $businessDllPath -PathType Leaf) -and [IO.Path]::GetFullPath($cliDllPath) -ieq [IO.Path]::GetFullPath($RuntimeCliPath) -and [IO.Path]::GetFullPath($cliDllPath) -ieq [IO.Path]::GetFullPath((Resolve-RepoPath ([string](Get-Field $stateRuntime 'cliPath')))) -and [IO.Path]::GetFullPath($businessDllPath) -ieq [IO.Path]::GetFullPath((Resolve-RepoPath ([string](Get-Field $stateRuntime 'businessPath')))) -and (Get-Sha256 $cliDllPath) -ceq ([string](Get-Field $stateRuntime 'cliSha256')).ToLowerInvariant() -and (Get-Sha256 $businessDllPath) -ceq ([string](Get-Field $stateRuntime 'businessSha256')).ToLowerInvariant() -and [string](Get-Field $globalRuntime 'cliDllPath') -ceq [string](Get-Field $runtimeRoots 'cliDllPath') -and [string](Get-Field $globalRuntime 'cliDirectoryPath') -ceq [string](Get-Field $runtimeRoots 'cliDirectoryPath') -and [string](Get-Field $globalRuntime 'businessDllPath') -ceq [string](Get-Field $runtimeRoots 'businessDllPath') -and [string](Get-Field $globalRuntime 'businessDirectoryPath') -ceq [string](Get-Field $runtimeRoots 'businessDirectoryPath')
    Add-Check 'frozen CLI and Business runtime roots match prepared state' $runtimeRootMatches @{cliDll=$cliDllPath;businessDll=$businessDllPath;stateRuntime=$stateRuntime}
    $runtimeRows = [Collections.Generic.List[object]]::new()
    foreach ($runtime in @(@{name='cli';root=$cliDirectoryPath},@{name='business';root=$businessDirectoryPath})) {
        if (-not (Test-Path -LiteralPath $runtime.root -PathType Container)) { continue }
        foreach ($row in (Get-RuntimeInventory $runtime.root $runtime.name)) { $runtimeRows.Add($row) }
    }
    $actualRuntime = @($runtimeRows | Sort-Object runtime,path)
    $pinnedRuntime = @(Get-Field $pin 'runtimeFiles' | Sort-Object runtime,path)
    $globalRuntimeFiles = @(Get-Field $globalRuntime 'files' | Sort-Object runtime,path)
    Add-Check 'frozen runtime inventories match global freeze and prepared actor' ((Get-CanonicalJson @($actualRuntime)) -ceq (Get-CanonicalJson @($pinnedRuntime)) -and (Get-CanonicalJson @($actualRuntime)) -ceq (Get-CanonicalJson @($globalRuntimeFiles))) @{files=$actualRuntime.Count}
    $buildSourceRevision = [string](Get-Field $globalRuntime 'buildSourceRevision')
    $buildRevisionResolves = $buildSourceRevision -match '^[0-9a-fA-F]{40}$'
    if ($buildRevisionResolves) {
        $resolvedBuildRevision = (& git -C $repo rev-parse --verify "$buildSourceRevision^{commit}" 2>$null | Out-String).Trim()
        $buildRevisionResolves = $LASTEXITCODE -eq 0 -and $resolvedBuildRevision -ceq $buildSourceRevision
    }
    $languageDiff = @()
    if ($buildRevisionResolves -and $revisionResolves) {
        $languageDiff = @(& git -C $repo diff --name-only $buildSourceRevision $pinnedRevision -- src experiments/AgentLang.Business Directory.Build.props Directory.Build.targets global.json NuGet.config AgentLang.sln 2>$null)
        $languageDiffExit = $LASTEXITCODE
    } else { $languageDiffExit = 1 }
    Add-Check 'runtime build source is pinned and no language or build inputs changed afterward' ($buildRevisionResolves -and $languageDiffExit -eq 0 -and $languageDiff.Count -eq 0 -and [string](Get-Field $pin 'runtimeBuildSourceRevision') -ceq $buildSourceRevision) @{buildSourceRevision=$buildSourceRevision;pinRuntimeBuildSourceRevision=(Get-Field $pin 'runtimeBuildSourceRevision');changedPaths=$languageDiff;diffExit=$languageDiffExit}

    $fixedSourceRoot = 'experiments/AgentLang.SubagentTrials/business-policy-retention-003/artifacts/source-snapshot/'
    $requiredSourceMap = [ordered]@{}
    foreach ($sourcePath in @('scripts/Prepare-RetentionTrial.ps1','scripts/Freeze-RetentionTrial.ps1','scripts/Verify-RetentionTrial.ps1','scripts/Verify-RetentionPreflight.ps1','scripts/Audit-RetentionTrace.ps1','scripts/Prepare-BusinessPolicyTrial.ps1','scripts/Start-SubagentTrialHostV2.ps1','scripts/Audit-SubagentTrialTerminationV2.ps1','experiments/AgentLang.SubagentTrials/business-policy-retention-003/acceptance.json','experiments/AgentLang.SubagentTrials/business-policy-retention-003/design.json','experiments/AgentLang.SubagentTrials/business-policy-retention-003/language-primer.md','experiments/AgentLang.SubagentTrials/business-policy-retention-003/artifacts/flat-customer.agent','experiments/AgentLang.SubagentTrials/business-policy-001/acceptance.json','experiments/AgentLang.SubagentTrials/business-policy-help-002/acceptance.json','experiments/AgentLang.SubagentTrials/business-policy-help-002/language-primer.md','experiments/AgentLang.Benchmarks/task-bank/public/S01.json','experiments/AgentLang.Benchmarks/task-bank/public/S07.json','examples/business-values.agent','examples/business-store.agent','examples/business-state.agent','examples/business-subscriptions.agent','examples/business-invoices.agent','examples/business-payments-email.agent')) {
        $destination = if ($sourcePath.StartsWith('scripts/',[StringComparison]::Ordinal) -or $sourcePath.StartsWith('examples/',[StringComparison]::Ordinal)) { $fixedSourceRoot + $sourcePath } else {
            switch ($sourcePath) {
                'experiments/AgentLang.SubagentTrials/business-policy-retention-003/acceptance.json' { $fixedSourceRoot + 'acceptance.json' }
                'experiments/AgentLang.SubagentTrials/business-policy-retention-003/design.json' { $fixedSourceRoot + 'design.json' }
                'experiments/AgentLang.SubagentTrials/business-policy-retention-003/language-primer.md' { $fixedSourceRoot + 'language-primer.md' }
                'experiments/AgentLang.SubagentTrials/business-policy-retention-003/artifacts/flat-customer.agent' { $fixedSourceRoot + 'flat-customer.agent' }
                'experiments/AgentLang.SubagentTrials/business-policy-001/acceptance.json' { $fixedSourceRoot + 'acceptance-business-policy-001.json' }
                'experiments/AgentLang.SubagentTrials/business-policy-help-002/acceptance.json' { $fixedSourceRoot + 'acceptance-business-policy-help-002.json' }
                'experiments/AgentLang.SubagentTrials/business-policy-help-002/language-primer.md' { $fixedSourceRoot + 'language-primer-business-policy-help-002.md' }
                'experiments/AgentLang.Benchmarks/task-bank/public/S01.json' { $fixedSourceRoot + 'public-task-S01.json' }
                'experiments/AgentLang.Benchmarks/task-bank/public/S07.json' { $fixedSourceRoot + 'public-task-S07.json' }
            }
        }
        $requiredSourceMap[$sourcePath] = $destination
    }
    $sourceArtifacts = @(Get-Field $pin 'sourceArtifacts')
    $artifactSeenSources = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $artifactRowsValid = $sourceArtifacts.Count -eq $requiredSourceMap.Count
    foreach ($artifact in $sourceArtifacts) {
        $relativePath = [string](Get-Field $artifact 'path')
        $sourcePath = [string](Get-Field $artifact 'sourcePath')
        if (-not $artifactSeenSources.Add($sourcePath) -or -not $requiredSourceMap.Contains($sourcePath) -or $requiredSourceMap[$sourcePath] -cne $relativePath) { $artifactRowsValid = $false }
        $artifactPath = Resolve-RepoPath $relativePath
        $workingSourcePath = Resolve-RepoPath $sourcePath
        $safe = (Test-Within $artifactPath $repo) -and (Test-Within $workingSourcePath $repo)
        $exists = $safe -and (Test-Path -LiteralPath $artifactPath -PathType Leaf) -and (Test-Path -LiteralPath $workingSourcePath -PathType Leaf)
        $expectedHash = ([string](Get-Field $artifact 'sha256')).ToLowerInvariant()
        $snapshotHash = if ($exists) { Get-Sha256 $artifactPath } else { $null }
        $workingHash = if ($exists) { Get-Sha256 $workingSourcePath } else { $null }
        $revisionSnapshotHash = if ($revisionResolves -and $safe) { Get-GitBlobSha256 $pinnedRevision $relativePath } else { $null }
        $revisionSourceHash = if ($revisionResolves -and $safe) { Get-GitBlobSha256 $pinnedRevision $sourcePath } else { $null }
        $expectedSnapshotRevisionHash = ([string](Get-Field $artifact 'gitBlobSha256')).ToLowerInvariant()
        $expectedSourceRevisionHash = ([string](Get-Field $artifact 'sourceGitBlobSha256')).ToLowerInvariant()
        $ok = $exists -and $snapshotHash -ceq $expectedHash -and $workingHash -ceq $expectedHash -and $revisionSnapshotHash -ceq $expectedSnapshotRevisionHash -and $revisionSourceHash -ceq $expectedSourceRevisionHash -and $expectedHash -ceq $expectedSnapshotRevisionHash -and $expectedHash -ceq $expectedSourceRevisionHash -and (Get-Item -LiteralPath $artifactPath).Length -eq [long](Get-Field $artifact 'bytes')
        $repoPinResults.Add([ordered]@{path=$relativePath;sourcePath=$sourcePath;expectedSha256=$expectedHash;snapshotSha256=$snapshotHash;workingSourceSha256=$workingHash;revisionSnapshotSha256=$revisionSnapshotHash;revisionSourceSha256=$revisionSourceHash;passed=$ok})
    }
    Add-Check 'frozen source artifact inventory has exactly the 23 reviewed inputs' ($artifactRowsValid -and $artifactSeenSources.Count -eq $requiredSourceMap.Count) @{expectedCount=$requiredSourceMap.Count;actualCount=$sourceArtifacts.Count;missing=@($requiredSourceMap.Keys | Where-Object { -not $artifactSeenSources.Contains($_) })}
    Add-Check 'all frozen source artifact snapshots and versioned inputs match working copy and commit' (@($repoPinResults | Where-Object { -not $_.passed }).Count -eq 0) @($repoPinResults)

    $baselineArchives = @(Get-Field $globalFreeze 'baselineArchives')
    $perRunBaselineArchives = @(Get-Field $pin 'baselineArchives')
    Add-Check 'per-run baseline archive pins match global freeze' ((Get-CanonicalJson @($baselineArchives | Sort-Object kind)) -ceq (Get-CanonicalJson @($perRunBaselineArchives | Sort-Object kind)))
    $baselineArchiveResults = [Collections.Generic.List[object]]::new()
    foreach ($archive in $baselineArchives) {
        $kind = [string](Get-Field $archive 'kind')
        $manifestRelative = [string](Get-Field $archive 'manifestPath')
        $projectRelative = [string](Get-Field $archive 'projectPath')
        $manifestPath = Resolve-RepoPath $manifestRelative
        $projectPath = Resolve-RepoPath $projectRelative
        $safe = $manifestRelative -ceq "experiments/AgentLang.SubagentTrials/business-policy-retention-003/artifacts/baselines/$kind/manifest.json" -and $projectRelative -ceq "experiments/AgentLang.SubagentTrials/business-policy-retention-003/artifacts/baselines/$kind/project" -and (Test-Within $manifestPath $repo) -and (Test-Within $projectPath $repo)
        $manifestExists = $safe -and (Test-Path -LiteralPath $manifestPath -PathType Leaf) -and (Test-Path -LiteralPath $projectPath -PathType Container)
        $manifestHash = if ($manifestExists) { Get-Sha256 $manifestPath } else { $null }
        $manifestGitHash = if ($revisionResolves -and $manifestExists) { Get-GitBlobSha256 $pinnedRevision $manifestRelative } else { $null }
        $manifest = if ($manifestExists) { Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable -Depth 100 } else { $null }
        $actualFiles = if ($manifestExists) { Get-FreezeInventory $projectPath } else { @() }
        $manifestFiles = @(Get-Field $manifest 'files')
        $treeHash = if ($manifestExists) { Get-TreeHash $projectPath } else { $null }
        $filesMatch = (Get-CanonicalJson @($actualFiles)) -ceq (Get-CanonicalJson @($manifestFiles))
        $revisionRows = [Collections.Generic.List[object]]::new()
        foreach ($row in $manifestFiles) {
            $treeRelativePath = "$projectRelative/$($row.path)"
            $revisionHash = if ($revisionResolves) { Get-GitBlobSha256 $pinnedRevision $treeRelativePath } else { $null }
            $actualPath = Join-Path $projectPath ([string]$row.path)
            $actualHash = if (Test-Path -LiteralPath $actualPath -PathType Leaf) { Get-Sha256 $actualPath } else { $null }
            $revisionRows.Add([ordered]@{path=$treeRelativePath;expected=[string]$row.sha256;actual=$actualHash;revision=$revisionHash;passed=($actualHash -ceq [string]$row.sha256 -and $revisionHash -ceq [string]$row.sha256)})
        }
        $archiveTreeAlias = [string](Get-Field $archive 'treeSha256')
        $baselineOk = $manifestExists -and [int]$manifest.schemaVersion -eq 1 -and [string]$manifest.studyId -ceq 'business-policy-retention-003' -and [string]$manifest.kind -ceq $kind -and $manifestHash -ceq ([string](Get-Field $archive 'manifestSha256')).ToLowerInvariant() -and $manifestGitHash -ceq $manifestHash -and $filesMatch -and (Get-CanonicalJson @(Get-CanonicalInventoryRows $manifestFiles)) -ceq (Get-CanonicalJson @(Get-CanonicalInventoryRows (Get-Field $archive 'files'))) -and $treeHash -ceq ([string](Get-Field $archive 'inventorySha256')).ToLowerInvariant() -and $treeHash -ceq ([string](Get-Field $manifest 'inventorySha256')).ToLowerInvariant() -and ([string]::IsNullOrWhiteSpace($archiveTreeAlias) -or $archiveTreeAlias -ceq $treeHash) -and (Get-CanonicalJson $manifest.counts) -ceq (Get-CanonicalJson $archive.counts) -and (Get-CanonicalJson @($manifest.sourceInputs | Sort-Object -CaseSensitive path)) -ceq (Get-CanonicalJson @($archive.sourceInputs | Sort-Object -CaseSensitive path)) -and (Get-CanonicalJson $manifest.runtime) -ceq (Get-CanonicalJson $archive.runtime) -and [string]$manifest.seedStateSha256 -ceq [string]$archive.seedStateSha256 -and [string]$manifest.bootstrapperPath -ceq [string]$archive.bootstrapperPath -and [string]$manifest.bootstrapperSha256 -ceq [string]$archive.bootstrapperSha256 -and @($revisionRows | Where-Object { -not $_.passed }).Count -eq 0
        $baselineArchiveResults.Add([ordered]@{kind=$kind;manifestPath=$manifestRelative;manifestSha256=$manifestHash;projectPath=$projectRelative;inventorySha256=$treeHash;treeSha256=$treeHash;files=$actualFiles.Count;revisionFiles=@($revisionRows);passed=$baselineOk})
    }
    Add-Check 'global flat and rich baseline archives match manifests and committed trees' ($baselineArchiveResults.Count -eq 2 -and @($baselineArchiveResults | Where-Object { -not $_.passed }).Count -eq 0) @($baselineArchiveResults)

    $preparedState = $preparedStateData
    $preparation = Get-Field $preparedState 'preparation'
    $baselineState = Get-Field $preparedState 'baseline'
    $expectedBaselineKind = if ($CurrentArm -ceq 'flat') { 'flat' } else { 'rich' }
    $archiveForState = @($baselineArchives | Where-Object { [string](Get-Field $_ 'kind') -ceq $expectedBaselineKind })
    $baselineStateMatches = $preparedStateAvailable -and $archiveForState.Count -eq 1 -and [string](Get-Field $baselineState 'kind') -ceq $expectedBaselineKind -and [IO.Path]::GetFullPath((Resolve-RepoPath ([string](Get-Field $baselineState 'path')))) -ieq [IO.Path]::GetFullPath((Resolve-RepoPath ([string](Get-Field $archiveForState[0] 'projectPath')))) -and [string](Get-Field $baselineState 'inventorySha256') -ceq [string](Get-Field $archiveForState[0] 'inventorySha256') -and (Get-CanonicalJson @(Get-CanonicalInventoryRows (Get-Field $baselineState 'files'))) -ceq (Get-CanonicalJson @(Get-CanonicalInventoryRows (Get-Field $archiveForState[0] 'files')))
    Add-Check 'starting-state baseline archive and counts match global baseline pin' ($baselineStateMatches -and (Get-CanonicalJson (Get-Field $baselineState 'counts')) -ceq (Get-CanonicalJson (Get-Field $archiveForState[0] 'counts'))) @{expectedKind=$expectedBaselineKind;actualKind=(Get-Field $baselineState 'kind');counts=(Get-Field $baselineState 'counts')}
    $preparationOk = $preparedStateAvailable -and [string](Get-Field $preparation 'sourceRevision') -ceq $pinnedRevision -and (Get-Field $preparation 'dirty') -eq $false -and [string](Get-Field $preparation 'scriptPath') -ceq [string]$pin.preparerPath -and [string](Get-Field $preparation 'scriptSha256') -ceq ([string]$pin.preparerSha256).ToLowerInvariant() -and [string](Get-Field $preparation 'bootstrapperPath') -ceq [string]$pin.bootstrapperPath -and [string](Get-Field $preparation 'bootstrapperSha256') -ceq ([string]$pin.bootstrapperSha256).ToLowerInvariant() -and [string](Get-Field $preparation 'oracleSha256') -ceq ([string]$pin.oracleSha256).ToLowerInvariant() -and [string](Get-Field $preparation 'primerSha256') -ceq ([string]$pin.primerSha256).ToLowerInvariant() -and [string](Get-Field $preparation 'designSha256') -ceq ([string]$pin.designSha256).ToLowerInvariant()
    Add-Check 'starting state was prepared by pinned clean study and bootstrapper sources' $preparationOk @{preparation=$preparation;sourceRevision=$pinnedRevision}
    Add-Check 'pin prior acceptance and fallback outcome match prepared state' ($preparedStateAvailable -and (Get-CanonicalJson (Get-Field $pin 'previousAcceptance')) -ceq (Get-CanonicalJson (Get-Field $preparedState 'previousAcceptance')) -and [string](Get-Field $pin 'priorOutcome') -ceq [string](Get-Field $preparedState 'priorOutcome')) @{priorOutcome=(Get-Field $preparedState 'priorOutcome')}

    $pinChecksPassed = $true
    for ($index=$pinCheckStart; $index -lt $metadataChecks.Count; $index++) { if (-not [bool]$metadataChecks[$index].passed) { $pinChecksPassed = $false } }
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
    $rows = Get-CanonicalInventoryRows (Get-FreezeInventory $Path)
    $canonical = ConvertTo-Json -InputObject @($rows) -Depth 20 -Compress
    $bytes = $utf8NoBom.GetBytes($canonical)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Validate-StartingState([string]$StartPath,[string]$ActorPath,$Acceptance,[string]$CurrentArm,[string]$CurrentBlock,[string]$CurrentTask,[string]$CurrentRunId) {
    $runDirectory = Split-Path -Parent $StartPath
    $statePath = Join-Path $runDirectory 'starting-state.json'
    if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) { throw "Required starting-state metadata is missing: $statePath" }
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json -AsHashtable -Depth 100
    $expectedIndex = [array]::IndexOf($sequence,$CurrentTask) + 1
    Add-Check 'starting-state schema, study, arm, block, task, and run identity match' ([int]$state.schemaVersion -eq 1 -and [string]$state.studyId -ceq 'business-policy-retention-003' -and [string]$state.arm -ceq $CurrentArm -and [string]$state.block -ceq $CurrentBlock -and [string]$state.taskId -ceq $CurrentTask -and [int]$state.sequenceIndex -eq $expectedIndex -and [string]$state.runId -ceq $CurrentRunId) @{expectedRunId=$CurrentRunId;actualRunId=$state.runId;sequenceIndex=$state.sequenceIndex}

    $declaredStart = [string](Get-Field (Get-Field $state 'project') 'path')
    $declaredActor = [string](Get-Field (Get-Field $state 'actor') 'projectPath')
    $pathsMatch = [IO.Path]::IsPathFullyQualified($declaredStart) -and [IO.Path]::GetFullPath($declaredStart) -ieq [IO.Path]::GetFullPath($StartPath) -and [IO.Path]::IsPathFullyQualified($declaredActor) -and [IO.Path]::GetFullPath($declaredActor) -ieq [IO.Path]::GetFullPath($ActorPath)
    Add-Check 'starting-state project and actor paths match supplied run' $pathsMatch @{project=$declaredStart;actor=$declaredActor}

    $runtime = Get-Field $state 'runtime'
    $runtimePathsPresent = -not [string]::IsNullOrWhiteSpace([string](Get-Field $runtime 'cliPath')) -and -not [string]::IsNullOrWhiteSpace([string](Get-Field $runtime 'cliSha256')) -and -not [string]::IsNullOrWhiteSpace([string](Get-Field $runtime 'businessPath')) -and -not [string]::IsNullOrWhiteSpace([string](Get-Field $runtime 'businessSha256'))
    Add-Check 'starting-state pins CLI and Business runtime artifacts' $runtimePathsPresent $runtime
    $baseline = Get-Field $state 'baseline'
    $expectedBaselineKind = if ($CurrentArm -ceq 'flat') { 'flat' } else { 'rich' }
    $expectedBaselineRoot = "experiments/AgentLang.SubagentTrials/business-policy-retention-003/artifacts/baselines/$expectedBaselineKind"
    $expectedBaselineProjectRelative = "$expectedBaselineRoot/project"
    $expectedBaselineProjectFullPath = Resolve-RepoPath $expectedBaselineProjectRelative
    $baselinePath = [string](Get-Field $baseline 'path')
    $baselinePathResolved = $null
    $baselinePathHasTraversal = $baselinePath -match '(^|[\\/])\.\.([\\/]|$)'
    $baselinePathCanonical = $false
    if (-not [string]::IsNullOrWhiteSpace($baselinePath) -and -not $baselinePathHasTraversal) {
        if ([IO.Path]::IsPathFullyQualified($baselinePath)) {
            $candidateBaselinePath = [IO.Path]::GetFullPath($baselinePath)
            $baselinePathCanonical = $baselinePath.Replace('/','\') -ieq $candidateBaselinePath -and $candidateBaselinePath -ieq $expectedBaselineProjectFullPath
        } elseif (-not [IO.Path]::IsPathRooted($baselinePath)) {
            $baselinePathCanonical = $baselinePath -ceq $expectedBaselineProjectRelative
        }
        if ($baselinePathCanonical) { $baselinePathResolved = Resolve-RepoPath $baselinePath }
    }
    $baselinePathMatches = $baselinePathCanonical -and (Test-Within $baselinePathResolved $repo) -and (Test-Path -LiteralPath $baselinePathResolved -PathType Container)
    Add-Check 'starting-state selects the archived flat or rich baseline for its arm' ($baselinePathMatches -and [string](Get-Field $baseline 'kind') -ceq $expectedBaselineKind) @{expectedKind=$expectedBaselineKind;actualKind=(Get-Field $baseline 'kind');path=$baselinePath}
    $expectedManifestRelative = "$expectedBaselineRoot/manifest.json"
    $manifestRelative = [string](Get-Field $baseline 'manifestPath')
    $manifestPath = Resolve-RepoPath $manifestRelative
    $manifestExists = (Test-Within $manifestPath $repo) -and (Test-Path -LiteralPath $manifestPath -PathType Leaf)
    $manifest = if ($manifestExists) { Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable -Depth 100 } else { $null }
    $manifestHash = if ($manifestExists) { Get-Sha256 $manifestPath } else { $null }
    Add-Check 'starting-state baseline manifest matches archived baseline identity and hash' ($manifestRelative -ceq $expectedManifestRelative -and $manifestExists -and [string](Get-Field $manifest 'studyId') -ceq 'business-policy-retention-003' -and [string](Get-Field $manifest 'kind') -ceq $expectedBaselineKind -and $manifestHash -ceq ([string](Get-Field $baseline 'manifestSha256')).ToLowerInvariant()) @{manifestPath=$manifestRelative;expectedPath=$expectedManifestRelative;expected=(Get-Field $baseline 'manifestSha256');actual=$manifestHash}
    $baselineInventory = [object[]]@()
    if ($baselinePathMatches) { $baselineInventory = @(Get-FreezeInventory $baselinePathResolved) }
    $baselineRows = @(Get-Field $baseline 'files')
    $baselineTreeHash = if ($baselinePathMatches) { Get-TreeHash $baselinePathResolved } else { $null }
    $manifestTreeAlias = [string](Get-Field $manifest 'treeSha256')
    $stateTreeAlias = [string](Get-Field $baseline 'treeSha256')
    $baselineInventoryMatches = $manifestExists -and (Get-CanonicalJson @(Get-CanonicalInventoryRows $baselineInventory)) -ceq (Get-CanonicalJson @(Get-CanonicalInventoryRows $baselineRows)) -and (Get-CanonicalJson @(Get-CanonicalInventoryRows $baselineInventory)) -ceq (Get-CanonicalJson @(Get-CanonicalInventoryRows (Get-Field $manifest 'files'))) -and $baselineTreeHash -ceq ([string](Get-Field $baseline 'inventorySha256')).ToLowerInvariant() -and $baselineTreeHash -ceq ([string](Get-Field $manifest 'inventorySha256')).ToLowerInvariant() -and ([string]::IsNullOrWhiteSpace($manifestTreeAlias) -or $manifestTreeAlias -ceq $baselineTreeHash) -and ([string]::IsNullOrWhiteSpace($stateTreeAlias) -or $stateTreeAlias -ceq $baselineTreeHash)
    Add-Check 'starting-state baseline file inventory and tree hash match archived manifest' $baselineInventoryMatches @{files=@($baselineInventory).Count;treeSha256=$baselineTreeHash}
    $expectedCounts = if ($expectedBaselineKind -ceq 'flat') { [ordered]@{words=0;types=6;tests=0} } else { [ordered]@{words=53;types=31;tests=151} }
    Add-Check 'archived baseline counts are the reviewed flat/rich counts' ((Get-CanonicalJson (Get-Field $baseline 'counts')) -ceq (Get-CanonicalJson $expectedCounts) -and (Get-CanonicalJson (Get-Field $manifest 'counts')) -ceq (Get-CanonicalJson $expectedCounts)) @{expected=$expectedCounts;state=(Get-Field $baseline 'counts');manifest=(Get-Field $manifest 'counts')}
    $sourceInputsExpected = if ($expectedBaselineKind -ceq 'flat') { @('experiments/AgentLang.SubagentTrials/business-policy-retention-003/artifacts/flat-customer.agent') } else { @('examples/business-values.agent','examples/business-store.agent','examples/business-state.agent','examples/business-subscriptions.agent','examples/business-invoices.agent','examples/business-payments-email.agent') }
    $sourceInputs = @(Get-Field $baseline 'sourceInputs')
    $manifestSourceInputs = @(Get-Field $manifest 'sourceInputs')
    $sourcePaths = @($sourceInputs | ForEach-Object { [string](Get-Field $_ 'path') } | Sort-Object -CaseSensitive)
    $expectedSourcePaths = @($sourceInputsExpected | Sort-Object -CaseSensitive)
    $sourceRowsValid = $sourcePaths.Count -eq $expectedSourcePaths.Count -and ($sourcePaths -join "`0") -ceq ($expectedSourcePaths -join "`0") -and (Get-CanonicalJson @($sourceInputs | Sort-Object path)) -ceq (Get-CanonicalJson @($manifestSourceInputs | Sort-Object path))
    $sourceInputResults = [Collections.Generic.List[object]]::new()
    foreach ($row in $sourceInputs) {
        $sourcePath = [string](Get-Field $row 'path')
        $sourceResolved = Resolve-RepoPath $sourcePath
        $safe = -not [IO.Path]::IsPathFullyQualified($sourcePath) -and (Test-Within $sourceResolved $repo) -and (Test-Path -LiteralPath $sourceResolved -PathType Leaf)
        $actualHash = if ($safe) { Get-Sha256 $sourceResolved } else { $null }
        $sourceInputResults.Add([ordered]@{path=$sourcePath;expected=[string](Get-Field $row 'sha256');actual=$actualHash;passed=($safe -and $actualHash -ceq ([string](Get-Field $row 'sha256')).ToLowerInvariant())})
    }
    Add-Check 'starting-state baseline source inputs match exact reviewed set and bytes' ($sourceRowsValid -and @($sourceInputResults | Where-Object { -not $_.passed }).Count -eq 0) @($sourceInputResults)

    $projectInventory = Get-FreezeInventory $StartPath
    $projectRows = @(Get-Field (Get-Field $state 'project') 'files')
    $projectRowsJson = Get-CanonicalJson @(Get-CanonicalInventoryRows $projectRows)
    $actualProjectRowsJson = Get-CanonicalJson @(Get-CanonicalInventoryRows $projectInventory)
    $projectTreeHash = Get-TreeHash $StartPath
    Add-Check 'starting-state project file inventory and tree hash match immutable start' ($projectRowsJson -ceq $actualProjectRowsJson -and $projectTreeHash -ceq ([string](Get-Field (Get-Field $state 'project') 'inventorySha256')).ToLowerInvariant()) @{files=$projectInventory.Count;treeSha256=$projectTreeHash}
    $actorRows = @(Get-Field (Get-Field $state 'actor') 'files')
    $actorRowsJson = Get-CanonicalJson @(Get-CanonicalInventoryRows $actorRows)
    $stateActorTreeHash = [string](Get-Field (Get-Field $state 'actor') 'inventorySha256')
    Add-Check 'prepared prelaunch actor inventory equals the immutable project snapshot' ($actorRowsJson -ceq $projectRowsJson -and $stateActorTreeHash -ceq $projectTreeHash -and (Test-Path -LiteralPath $ActorPath -PathType Container)) @{files=$actorRows.Count;treeSha256=$stateActorTreeHash;actorOutputPath=$ActorPath}

    $baselineRuntime = Get-Field $manifest 'runtime'
    $baselineRuntimeMatches = [string](Get-Field $baselineRuntime 'cliPath') -ceq [string](Get-Field $runtime 'cliPath') -and [string](Get-Field $baselineRuntime 'cliSha256') -ceq [string](Get-Field $runtime 'cliSha256') -and [string](Get-Field $baselineRuntime 'businessPath') -ceq [string](Get-Field $runtime 'businessPath') -and [string](Get-Field $baselineRuntime 'businessSha256') -ceq [string](Get-Field $runtime 'businessSha256') -and [string](Get-Field $baselineRuntime 'buildSourceRevision') -ceq [string](Get-Field $runtime 'buildSourceRevision')
    Add-Check 'baseline manifest and prepared state share exact runtime artifacts' $baselineRuntimeMatches @{baseline=$baselineRuntime;run=$runtime}
    $preparation = Get-Field $state 'preparation'
    $bootstrapperPath = [string](Get-Field $preparation 'bootstrapperPath')
    $bootstrapperActualHash = if ((Test-Path -LiteralPath (Resolve-RepoPath $bootstrapperPath) -PathType Leaf)) { Get-Sha256 (Resolve-RepoPath $bootstrapperPath) } else { $null }
    $preparationRevision = [string](Get-Field $preparation 'sourceRevision')
    $preparerRelative = [string](Get-Field $preparation 'scriptPath')
    $preparerPath = Resolve-RepoPath $preparerRelative
    $preparerHash = if ((Test-Path -LiteralPath $preparerPath -PathType Leaf)) { Get-Sha256 $preparerPath } else { $null }
    $preparationDirty = Get-Field $preparation 'dirty'
    $preparationDirtyAcceptable = if ($mustRequireFrozenPin) { $preparationDirty -eq $false } else { $preparationDirty -is [bool] }
    $preparationOk = $preparationRevision -match '^[0-9a-fA-F]{40}$' -and $preparationDirtyAcceptable -and [string](Get-Field $baseline 'seedStateSha256') -ceq [string](Get-Field $manifest 'seedStateSha256') -and $preparerRelative -ceq 'scripts/Prepare-RetentionTrial.ps1' -and $preparerHash -ceq ([string](Get-Field $preparation 'scriptSha256')).ToLowerInvariant() -and [string](Get-Field $preparation 'bootstrapperPath') -ceq 'scripts/Prepare-BusinessPolicyTrial.ps1' -and $bootstrapperActualHash -ceq ([string](Get-Field $preparation 'bootstrapperSha256')).ToLowerInvariant() -and $bootstrapperActualHash -ceq ([string](Get-Field $manifest 'bootstrapperSha256')).ToLowerInvariant() -and [string](Get-Field $preparation 'oraclePath') -ceq 'experiments/AgentLang.SubagentTrials/business-policy-retention-003/acceptance.json' -and [string](Get-Field $preparation 'oracleSha256') -ceq (Get-Sha256 $oraclePath) -and [string](Get-Field $preparation 'primerPath') -ceq 'experiments/AgentLang.SubagentTrials/business-policy-retention-003/language-primer.md' -and [string](Get-Field $preparation 'primerSha256') -ceq (Get-Sha256 (Join-Path $repo 'experiments/AgentLang.SubagentTrials/business-policy-retention-003/language-primer.md')) -and [string](Get-Field $preparation 'designPath') -ceq 'experiments/AgentLang.SubagentTrials/business-policy-retention-003/design.json' -and [string](Get-Field $preparation 'designSha256') -ceq (Get-Sha256 (Join-Path $repo 'experiments/AgentLang.SubagentTrials/business-policy-retention-003/design.json'))
    Add-Check 'starting-state preparation source hashes, revision, and dirty classification match the verification mode' $preparationOk @{preparation=$preparation;frozenMode=$mustRequireFrozenPin;dirtyAccepted=$preparationDirtyAcceptable}

    $expectedFallback = [bool]$BaselineFallback
    $fallbackAllowed = $CurrentArm -ceq 'retained' -and $CurrentTask -ceq 'S07'
    Add-Check 'baseline fallback is only requested for retained S07' (-not $expectedFallback -or $fallbackAllowed) @{arm=$CurrentArm;taskId=$CurrentTask;fallback=$expectedFallback}
    $previous = Get-Field $state 'previousAcceptance'
    $priorOutcome = [string](Get-Field $state 'priorOutcome')
    $expectsPrior = $fallbackAllowed
    if ($expectsPrior) {
        $priorPathValue = [string](Get-Field $previous 'path')
        $priorPath = Resolve-RepoPath $priorPathValue
        $priorExists = (Test-Within $priorPath $repo) -and (Test-Path -LiteralPath $priorPath -PathType Leaf)
        $priorHash = if ($priorExists) { Get-Sha256 $priorPath } else { $null }
        $priorExpectedHash = ([string](Get-Field $previous 'sha256')).ToLowerInvariant()
        Add-Check 'retained S07 has a hash-pinned same-block S01 acceptance record' ($null -ne $previous -and $priorExists -and $priorHash -ceq $priorExpectedHash -and [string](Get-Field $previous 'arm') -ceq 'retained' -and [string](Get-Field $previous 'block') -ceq $CurrentBlock -and [string](Get-Field $previous 'taskId') -ceq 'S01' -and [string](Get-Field $previous 'runId') -ceq [string]$runIndexMap[$CurrentBlock]['retained'][0]) @{path=$priorPathValue;expectedSha256=$priorExpectedHash;actualSha256=$priorHash}
        if ($priorExists) {
            $priorResult = Get-Content -LiteralPath $priorPath -Raw | ConvertFrom-Json -AsHashtable -Depth 100
            $priorIdentityOk = [int](Get-Field $priorResult 'schemaVersion') -eq 1 -and [string](Get-Field $priorResult 'studyId') -ceq 'business-policy-retention-003' -and [string](Get-Field $priorResult 'arm') -ceq 'retained' -and [string](Get-Field $priorResult 'block') -ceq $CurrentBlock -and [string](Get-Field $priorResult 'taskId') -ceq 'S01' -and [string](Get-Field $priorResult 'runId') -ceq [string]$runIndexMap[$CurrentBlock]['retained'][0] -and [int](Get-Field $priorResult 'sequenceIndex') -eq 1 -and (Get-Field $priorResult 'checkFrozenPin') -eq $true -and [string](Get-Field $priorResult 'resultKind') -ceq 'frozen-actor-acceptance'
            $priorPassed = [bool](Get-Field $prior 'passed') -and [bool](Get-Field $previous 'carryForward') -and [bool](Get-Field $priorResult 'passed') -and $priorIdentityOk
            $priorReportedPassed = [bool](Get-Field $priorResult 'passed')
            $priorProjectTree = [string](Get-Field (Get-Field $priorResult 'project') 'treeSha256')
            if ([string]::IsNullOrWhiteSpace($priorProjectTree)) { $priorProjectTree = [string](Get-Field $priorResult 'outputTreeSha256') }
            $statePriorTree = [string](Get-Field $previous 'projectTreeSha256')
            Add-Check 'retained S07 predecessor evidence has the exact same-block S01 identity' ($priorIdentityOk -and $priorHash -ceq $priorExpectedHash) @{expectedBlock=$CurrentBlock;expectedRunId=$runIndexMap[$CurrentBlock]['retained'][0];actualBlock=(Get-Field $priorResult 'block');actualRunId=(Get-Field $priorResult 'runId')}
            if ($expectedFallback) {
                $priorProjectPath = [string](Get-Field (Get-Field $priorResult 'project') 'path')
                $priorProjectResolved = if ([string]::IsNullOrWhiteSpace($priorProjectPath)) { $null } else { Resolve-RepoPath $priorProjectPath }
                $priorProjectSafe = $null -ne $priorProjectResolved -and (Test-Within $priorProjectResolved $repo) -and (Test-Path -LiteralPath $priorProjectResolved -PathType Container)
                $priorProjectActualTree = if ($priorProjectSafe) { Get-TreeHash $priorProjectResolved } else { $null }
                $priorFailed = (-not [bool](Get-Field $previous 'passed')) -and (-not [bool](Get-Field $previous 'carryForward')) -and (-not $priorReportedPassed) -and $priorIdentityOk -and $priorProjectActualTree -ceq $priorProjectTree -and $priorProjectTree -ceq $statePriorTree
                Add-Check 'explicit fallback follows hash-pinned failed same-block S01 evidence without carrying failed code' ($priorFailed -and $priorOutcome -ceq 'baseline-fallback-after-failed-S01' -and $projectTreeHash -ceq $baselineTreeHash) @{priorOutcome=$priorOutcome;priorProjectTree=$priorProjectTree;actualPriorProjectTree=$priorProjectActualTree;startTree=$projectTreeHash;baselineTree=$baselineTreeHash}
            } else {
                Add-Check 'retained S07 predecessor evidence names a passing same-block S01 output' ($priorPassed -and $priorProjectTree -ceq $statePriorTree -and $priorOutcome -ceq 'accepted-s01-carry-forward') @{priorOutcome=$priorOutcome;expectedTree=$statePriorTree;actualTree=$priorProjectTree;passed=$priorReportedPassed}
                Add-Check 'accepted S01 output is the exact retained S07 starting project' ($priorPassed -and $statePriorTree -ceq $projectTreeHash -and [bool](Get-Field $previous 'carryForward')) @{expected=$statePriorTree;actual=$projectTreeHash}
            }
        } else { Add-Check 'retained S07 predecessor acceptance can be loaded' $false $priorPath }
    } else {
        Add-Check 'independent arm/task has no predecessor acceptance' ($null -eq $previous -and -not $expectedFallback -and $priorOutcome -ceq 'baseline' -and $projectTreeHash -ceq $baselineTreeHash) @{previousAcceptance=$previous;priorOutcome=$priorOutcome;startTree=$projectTreeHash;baselineTree=$baselineTreeHash}
    }
    return [ordered]@{path=$statePath;sha256=(Get-Sha256 $statePath);data=$state;projectTreeSha256=$projectTreeHash;baselineTreeSha256=$baselineTreeHash}
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

function Validate-ArchivedBaselineLive([string]$BaselinePath,$BaselineState) {
    $kind = [string](Get-Field $BaselineState 'kind')
    $expectedCounts = if ($kind -ceq 'flat') { [ordered]@{words=0;types=6;tests=0} } else { [ordered]@{words=53;types=31;tests=151} }
    $archiveHash = Get-TreeHash $BaselinePath
    $stateHash = ([string](Get-Field $BaselineState 'inventorySha256')).ToLowerInvariant()
    Add-Check 'live baseline validation uses the exact immutable archived tree' ($archiveHash -ceq $stateHash) @{kind=$kind;expected=$stateHash;actual=$archiveHash}
    if ($archiveHash -cne $stateHash) { return [ordered]@{kind=$kind;treeSha256=$archiveHash;passed=$false;reason='archive inventory mismatch'} }

    [IO.Directory]::CreateDirectory($testsRoot) | Out-Null
    $script:baselineScratch = Join-Path $testsRoot ('baseline-' + $Arm + '-' + $RunId + '-' + [Guid]::NewGuid().ToString('N'))
    Add-Check 'baseline validation scratch is inside verifier tests root' (Test-Within $baselineScratch $testsRoot) $baselineScratch
    Copy-ProjectToScratch $BaselinePath $baselineScratch
    $scratchTreeHash = Get-TreeHash $baselineScratch
    Add-Check 'baseline validation scratch is a byte-identical archive copy' ($scratchTreeHash -ceq $stateHash) @{expected=$stateHash;actual=$scratchTreeHash}

    $responses = Invoke-JsonlSession $baselineScratch @([ordered]@{op='words'},[ordered]@{op='test-all'}) "$kind archived baseline live check"
    $script:metadataTestExecutions += 1
    Assert-ResponsesOk $responses "$kind archived baseline live check"
    $wordRows = @(Get-Field (Get-Field $responses[0] 'data') 'words')
    $userRows = @($wordRows | Where-Object { ([string](Get-Field $_ 'id')).StartsWith('word_',[StringComparison]::Ordinal) })
    $nominalNames = @(Get-NominalTypeNames $wordRows)
    $testRows = @(Get-Field (Get-Field $responses[1] 'data') 'results')
    $failedTests = @($testRows | Where-Object { (Get-Field $_ 'passed') -ne $true })

    $sourceTypeSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($input in (Get-Field $BaselineState 'sourceInputs')) {
        $inputPath = Resolve-RepoPath ([string](Get-Field $input 'path'))
        if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) { continue }
        $source = Get-Content -LiteralPath $inputPath -Raw
        foreach ($match in [regex]::Matches($source,'(?m)^\s*(?:type|record)\s+([A-Za-z][A-Za-z0-9_]*)\b')) { [void]$sourceTypeSet.Add($match.Groups[1].Value) }
    }
    $sourceTypeNames = @($sourceTypeSet | Sort-Object)
    $sourceTypeRequests = [Collections.Generic.List[object]]::new()
    foreach ($typeName in $sourceTypeNames) { $sourceTypeRequests.Add([ordered]@{op='source';type=$typeName}) }
    $typeReloadPassed = $false
    if ($sourceTypeRequests.Count -gt 0 -and $sourceTypeRequests.Count -le 90) {
        $typeResponses = Invoke-JsonlSession $baselineScratch @($sourceTypeRequests) "$kind baseline nominal type source reload"
        $typeReloadPassed = $typeResponses.Count -eq $sourceTypeNames.Count -and @($typeResponses | Where-Object { -not [bool](Get-Field $_ 'ok') -or [string]::IsNullOrWhiteSpace([string](Get-Field $_ 'data')) }).Count -eq 0
    }
    $targets = @('customer.premium?','customer.discounted-balance','customer.discount-basis-points')
    $presentTargets = @($wordRows | Where-Object { [string](Get-Field $_ 'name') -cin $targets } | ForEach-Object { [string](Get-Field $_ 'name') } | Sort-Object -Unique)
    $counts = [ordered]@{words=$userRows.Count;types=$nominalNames.Count;tests=$testRows.Count}
    $countsMatch = (Get-CanonicalJson $counts) -ceq (Get-CanonicalJson $expectedCounts) -and $sourceTypeNames.Count -eq [int]$expectedCounts.types -and (Get-CanonicalJson @($sourceTypeNames)) -ceq (Get-CanonicalJson @($nominalNames))
    Add-Check 'fresh baseline dictionary has exact arm word and nominal type counts' $countsMatch @{kind=$kind;expected=$expectedCounts;actual=$counts;sourceTypeNames=$sourceTypeNames;dictionaryTypeNames=$nominalNames}
    Add-Check 'baseline live source reload resolves every nominal type' $typeReloadPassed @{typeCount=$sourceTypeNames.Count;types=$sourceTypeNames}
    Add-Check 'all archived baseline tests pass in a fresh process with exact count' ($testRows.Count -eq [int]$expectedCounts.tests -and $failedTests.Count -eq 0) @{expected=[int]$expectedCounts.tests;actual=$testRows.Count;failed=$failedTests}
    Add-Check 'both new policy targets and the retired S06 symbol are absent from baseline' ($presentTargets.Count -eq 0) @{forbidden=$targets;present=$presentTargets}
    $archiveStillHash = Get-TreeHash $BaselinePath
    Add-Check 'live validation never changes the versioned baseline archive' ($archiveStillHash -ceq $stateHash) @{expected=$stateHash;actual=$archiveStillHash}
    return [ordered]@{kind=$kind;path=$BaselinePath;treeSha256=$archiveStillHash;counts=$counts;sourceTypes=$sourceTypeNames;tests=$testRows.Count;failedTests=$failedTests;forbiddenPresent=$presentTargets;typeSourcesReloaded=$typeReloadPassed;passed=($countsMatch -and $typeReloadPassed -and $testRows.Count -eq [int]$expectedCounts.tests -and $failedTests.Count -eq 0 -and $presentTargets.Count -eq 0 -and $archiveStillHash -ceq $stateHash)}
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
    $expectedStartingUserWords = if ($Arm -ceq 'flat') { 0 } else { 53 }
    $expectedStartingTypes = if ($Arm -ceq 'flat') { 6 } else { 31 }
    if ($Arm -ceq 'retained' -and $CurrentTask -ceq 'S07') { $expectedStartingUserWords += 1 }
    $startingUserWordsOk = $startUserRows.Count -eq $expectedStartingUserWords
    $startingTypesOk = $startTypeNames.Count -eq $expectedStartingTypes
    $startingBaselineUsable = $startingUserWordsOk -and $startingTypesOk
    Add-Check 'starting project has the arm-specific retained word and nominal type counts' ($startingUserWordsOk -and $startingTypesOk) @{expectedUserWords=$expectedStartingUserWords;actualUserWords=$startUserRows.Count;expectedTypes=$expectedStartingTypes;actualTypes=$startTypeNames.Count;baselineKind=$(if ($Arm -ceq 'flat') { 'flat' } else { 'rich' })}
    $startingNames = @($startRows | ForEach-Object { [string](Get-Field $_ 'name') })
    $startingHasS01 = 'customer.premium?' -cin $startingNames
    $startingHasS07 = 'customer.discounted-balance' -cin $startingNames
    $expectedRetainedS01 = $Arm -ceq 'retained' -and $CurrentTask -ceq 'S07' -and -not $BaselineFallback
    Add-Check 'starting S01 helper presence matches the retained carry-forward rule' ($startingHasS01 -eq $expectedRetainedS01) @{arm=$Arm;taskId=$CurrentTask;baselineFallback=[bool]$BaselineFallback;expected=$expectedRetainedS01;actual=$startingHasS01}
    Add-Check 'current task target is absent from its immutable starting project' (-not ($TargetName -cin $startingNames)) @{taskId=$CurrentTask;target=$TargetName;present=($TargetName -cin $startingNames)}
    Add-Check 'S07 policy target is absent from every immutable starting project' (-not $startingHasS07) @{present=$startingHasS07}
    $preservation.startingBaselineUsable = $startingBaselineUsable
    if (-not $startingBaselineUsable) {
        $script:safetyStop = 'Starting project dictionary counts do not match the frozen arm and retention state.'
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
    $preservation.arm = $Arm
    $reuse.status = if ($Arm -ceq 'retained' -and $TaskId -ceq 'S07') { 'eligible predecessor links are observed from same-block accepted S01 source and dependency graph' } else { 'baseline-started arm/task; no prior-study helper reuse is claimed' }
    Add-Check 'arm, block, task, and run identity match the canonical rotated design' ($null -ne $expectedRunId -and $RunId -ceq $expectedRunId) @{arm=$Arm;block=$Block;taskId=$TaskId;expectedRunId=$expectedRunId;runId=$RunId}
    Add-Check 'task id is supported by the fresh study' ($TaskId -cin @('S01','S07')) $TaskId
    Add-Check 'baseline fallback flag is restricted to retained S07' (-not $BaselineFallback -or ($Arm -ceq 'retained' -and $TaskId -ceq 'S07')) @{arm=$Arm;taskId=$TaskId;baselineFallback=[bool]$BaselineFallback}

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
        $studyOk = ([int]$oracle.schemaVersion -eq 1 -and [string]$oracle.studyId -ceq 'business-policy-retention-003')
        Add-Check 'acceptance corpus study and canonical sequence match' ($studyOk -and $sequenceOk) @{studyId=$oracle.studyId;sequence=@($oracle.sequence)}
        $predecessorPath = Join-Path $repo 'experiments/AgentLang.SubagentTrials/business-policy-001/acceptance.json'
        $predecessorHash = Get-Sha256 $predecessorPath
        $declaredPredecessor = [string](Get-Field $oracle 'predecessorSha256')
        if ([string]::IsNullOrWhiteSpace($declaredPredecessor)) { $declaredPredecessor = [string](Get-Field (Get-Field $oracle 'predecessor') 'sha256') }
        Add-Check 'acceptance records the unchanged 001 predecessor hash' ($declaredPredecessor.ToLowerInvariant() -ceq $predecessorHash) @{expected=$predecessorHash;actual=$declaredPredecessor}

        $targetOracle = Get-TargetTask $oracle $TaskId
        $expectedOutput = if ($TaskId -ceq 'S01') { 'Bool' } else { 'Money' }
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
        if (-not $oracleUsable) { $safetyStop = 'The acceptance corpus is missing, malformed, or not the pinned S01/S07 oracle.' }
    } catch {
        Add-MetadataFailure 'acceptance and independent oracle load' $_
        if ($null -eq $safetyStop) { $safetyStop = "Acceptance oracle is unusable: $($_.Exception.Message)" }
    }

    try {
        $originalProjectPath = Resolve-RepoPath $ProjectPath
        $resolvedProject = $originalProjectPath
        $canonicalActorRoot = Join-Path $repo '.agentlang/business-policy-retention-003/actors'
        $mustRequireFrozenPin = [bool]$RequireFrozenPin -or (Test-Within $resolvedProject $canonicalActorRoot)
        $frozenPinInfo.required = [bool]$mustRequireFrozenPin
        $projectExists = Test-Path -LiteralPath $resolvedProject -PathType Container
        Add-Check 'actor project directory exists' $projectExists $resolvedProject
        if (-not $projectExists) { throw "Actor project does not exist: $resolvedProject" }

        $canonicalAcceptanceTarget = Join-Path $repo ".agentlang/business-policy-retention-003/runs/$RunId/acceptance.json"
        if ([string]::IsNullOrWhiteSpace($EvidencePath)) {
            if ($mustRequireFrozenPin) { $evidenceTarget = [IO.Path]::GetFullPath($canonicalAcceptanceTarget) }
            else { $evidenceTarget = Get-UniqueEvidencePath $null $TaskId 'operational' }
        } else {
            $evidenceTarget = Resolve-RepoPath $EvidencePath
        }
        if ($mustRequireFrozenPin) {
            $canonicalFullPath = [IO.Path]::GetFullPath($canonicalAcceptanceTarget)
            $evidenceDestinationValid = [IO.Path]::GetFullPath($evidenceTarget).Equals($canonicalFullPath,[StringComparison]::OrdinalIgnoreCase)
            Add-Check 'frozen acceptance destination is the canonical run acceptance.json' $evidenceDestinationValid @{expected=$canonicalFullPath;actual=$evidenceTarget}
            if (-not $evidenceDestinationValid) {
                if ($null -eq $safetyStop) { $safetyStop = 'Frozen actor acceptance must be written to the canonical run acceptance.json.' }
                $evidenceTarget = Get-UniqueEvidencePath $null $TaskId 'noncanonical-destination'
            }
        }
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
            $evidenceTarget = Get-UniqueEvidencePath $null $TaskId 'unsafe-destination'
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
        $actorInventoryCapturedAtUtc = [DateTime]::UtcNow.ToString('O')
        $actorHashBefore = Get-TreeHash $resolvedProject
        if ($null -ne $resolvedStartingProject -and (Test-Path -LiteralPath $resolvedStartingProject -PathType Container)) {
            try {
                $stateCheckStart = $metadataChecks.Count
                $startingStateMetadata = Validate-StartingState $resolvedStartingProject $resolvedProject $oracle $Arm $Block $TaskId $RunId
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
                $frozenPinRecord = Validate-FrozenPin $resolvedStartingProject $resolvedProject $resolvedCli $Arm $Block $TaskId $RunId $oracle
            } catch {
                Add-MetadataFailure 'frozen prelaunch pin and source snapshots' $_
                $frozenPinInfo.status = 'invalid'
            }
        } else {
            $startingStateMetadata = $null
            $startingStatePassed = $false
            if ($mustRequireFrozenPin) { Add-Check 'required frozen actor verification has a starting project' $false }
            $frozenPinInfo.status = 'unfrozen-preflight-or-control-without-starting-project'
            Add-Check 'unfrozen verifier run classified as preflight/control' ($null -eq $resolvedStartingProject) 'No StartingProjectPath was supplied.'
        }
    } catch {
        Add-MetadataFailure 'actor, CLI, project, or evidence setup' $_
        $evidenceCollision = $true
        if ($null -eq $safetyStop) { $safetyStop = "Actor project or CLI is unusable: $($_.Exception.Message)" }
    }

    if ($mustRequireFrozenPin -and (-not $startingStatePassed -or -not $checkFrozenPin)) {
        if ($null -eq $safetyStop) { $safetyStop = 'Frozen starting-state, runtime, source, prompt, allowlist, or verifier provenance did not validate; no CLI evaluation is allowed.' }
        Add-Check 'required frozen provenance is valid before execution' $false $safetyStop
    }

    $executionGateValid = -not $mustRequireFrozenPin -or ($startingStatePassed -and $checkFrozenPin -and $evidenceDestinationValid)
    if ($executionGateValid -and $startingStatePassed -and $null -ne $startingStateMetadata) {
        try {
            $baselineState = Get-Field (Get-Field $startingStateMetadata 'data') 'baseline'
            $baselinePath = Resolve-RepoPath ([string](Get-Field $baselineState 'path'))
            $baselineCheck = Validate-ArchivedBaselineLive $baselinePath $baselineState
        } catch { Add-MetadataFailure 'archived baseline fresh-process source and test validation' $_ }
    }

    $targetUsable = $false
    $targetRow = $null
    $targetDescription = $null
    $actorRows = @()
    $scratchReady = $false
    if (-not $evidenceCollision -and $null -ne $resolvedProject -and $null -ne $resolvedCli -and (Test-Path -LiteralPath $resolvedProject -PathType Container) -and (Test-Path -LiteralPath $resolvedCli -PathType Leaf) -and $executionGateValid) {
        try {
            [IO.Directory]::CreateDirectory($testsRoot) | Out-Null
            $scratchProject = Join-Path $testsRoot ('verify-' + $Arm + '-' + $RunId + '-' + [Guid]::NewGuid().ToString('N'))
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
            $expectedOutput = if ($TaskId -ceq 'S01') { 'Bool' } else { 'Money' }
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
            $actorInventoryCapturedAtUtc = [DateTime]::UtcNow.ToString('O')
            $actorInventoryAfter = Get-RelativeInventory $resolvedProject
            $actorHashAfter = Get-TreeHash $resolvedProject
            $beforeJson = Get-CanonicalJson @($actorInventoryBefore)
            $afterJson = Get-CanonicalJson @($actorInventoryAfter)
            Add-Check 'actor project remains unchanged by verification' ($actorHashBefore -ceq $actorHashAfter -and $beforeJson -ceq $afterJson) @{before=$actorHashBefore;after=$actorHashAfter}
        } catch { Add-MetadataFailure 'actor project preservation inventory' $_ }
    }
    if ($null -ne $scratchProject -and (Test-Path -LiteralPath $scratchProject -PathType Container)) {
        $safe = $false
        try { $safe = (Test-Within $scratchProject $testsRoot) -and [IO.Path]::GetFileName($scratchProject).StartsWith("verify-$Arm-$RunId-", [StringComparison]::Ordinal) } catch { }
        if ($safe) {
            try {
                Remove-Item -LiteralPath $scratchProject -Recurse -Force
                Add-Check 'verifier scratch copy safely removed' (-not (Test-Path -LiteralPath $scratchProject))
            } catch { Add-MetadataFailure 'verifier scratch cleanup' $_ }
        } else { Add-MetadataFailure 'verifier scratch cleanup stayed inside its owned root' 'Unsafe scratch path was not removed.' }
    }
    if ($null -ne $baselineScratch -and (Test-Path -LiteralPath $baselineScratch -PathType Container)) {
        $safe = $false
        try { $safe = (Test-Within $baselineScratch $testsRoot) -and [IO.Path]::GetFileName($baselineScratch).StartsWith("baseline-$Arm-$RunId-", [StringComparison]::Ordinal) } catch { }
        if ($safe) {
            try { Remove-Item -LiteralPath $baselineScratch -Recurse -Force; Add-Check 'baseline validation scratch copy safely removed' (-not (Test-Path -LiteralPath $baselineScratch)) }
            catch { Add-MetadataFailure 'baseline validation scratch cleanup' $_ }
        } else { Add-MetadataFailure 'baseline validation scratch cleanup stayed inside its owned root' 'Unsafe baseline scratch path was not removed.' }
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
        try {
            if ([string]::IsNullOrWhiteSpace($EvidencePath)) {
                if ($mustRequireFrozenPin) { $evidenceTarget = [IO.Path]::GetFullPath((Join-Path $repo ".agentlang/business-policy-retention-003/runs/$RunId/acceptance.json")) }
                else { $evidenceTarget = Get-UniqueEvidencePath $null $TaskId 'operational' }
            }
            else { $evidenceTarget = Resolve-RepoPath $EvidencePath }
        } catch { $evidenceTarget = Get-UniqueEvidencePath $null $TaskId 'recovery' }
    }
    try {
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($evidenceTarget)) | Out-Null
        $executedCount = @($caseResults | Where-Object { [bool]$_.executed }).Count
        $verifiedCompletedUtc = [DateTime]::UtcNow.ToString('O')
        $evidence = [ordered]@{
            schemaVersion=1; studyId='business-policy-retention-003'; arm=$Arm; block=$Block; taskId=$TaskId; runId=$RunId
            passed=$passed; metadataPassed=$metadataPassed; behaviorPassed=$behaviorPassed; safetyStop=$safetyStop; failure=$failure; orchestrationError=$orchestrationError
            evidencePath=$evidenceTarget;requestedEvidencePath=$requestedEvidenceTarget;evidencePathCollision=$evidenceCollision
            sequenceIndex=$(if ($TaskId -in $sequence) { [array]::IndexOf($sequence,$TaskId)+1 } else { $null })
            resultKind=$(if ($mustRequireFrozenPin -and $startingStatePassed -and $checkFrozenPin) { 'frozen-actor-acceptance' } else { 'unfrozen-preflight-or-control'})
            checkFrozenPin=[bool]$checkFrozenPin; frozenPin=$frozenPinInfo; prelaunch=$frozenPinRecord
            startedUtc=$startedUtc; verifiedStartedUtc=$startedUtc; verifiedCompletedUtc=$verifiedCompletedUtc; finishedUtc=$verifiedCompletedUtc
            verifierSha256=$(if (Test-Path -LiteralPath $PSCommandPath -PathType Leaf) { Get-Sha256 $PSCommandPath } else { $null })
            oracle=[ordered]@{path=$oraclePath;sha256=$(if (Test-Path -LiteralPath $oraclePath -PathType Leaf) { Get-Sha256 $oraclePath } else { $null });symbol=$(if ($null -ne $targetOracle) { $targetOracle.symbol } else { $null });cases=$expectedCaseCount;independentResults=$caseOracleEvidence}
            behavior=[ordered]@{targetUsable=$targetUsable;casesExpected=$expectedCaseCount;casesExecuted=$executedCount;casesRecorded=$caseResults.Count;cases=$caseResults.ToArray()}
            project=[ordered]@{argumentPath=$originalProjectPath;path=$resolvedProject;files=$actorInventoryAfter;inventorySha256=$actorHashAfter;treeSha256=$actorHashAfter;inventoryCapturedAtUtc=$actorInventoryCapturedAtUtc;filesBefore=$actorInventoryBefore;filesAfter=$actorInventoryAfter}
            outputTreeSha256=$actorHashAfter
            startingProject=[ordered]@{argumentPath=$originalStartingProjectPath;path=$resolvedStartingProject;state=$startingStateMetadata;preservation=$preservation;baselineLive=$baselineCheck}
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
    [Console]::Error.WriteLine("Retention $Block/$Arm/$TaskId ($RunId) failed. Evidence: $evidenceTarget. $failure")
    exit 1
}
Write-Output "Retention $Block/$Arm/$TaskId ($RunId) passed $($checks.Count) checks. Evidence: $evidenceTarget"
