#requires -Version 7.0
[CmdletBinding()]
param(
    [switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:repo = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$script:study = Join-Path $script:repo '.agentlang/business-policy-retention-003'
$script:run04 = Join-Path $script:study 'runs/R04'
$script:run03 = Join-Path $script:study 'runs/R03'
$script:supplemental = Join-Path $script:study 'supplemental'
$script:scratchRoot = Join-Path $script:supplemental 'scratch'
$script:evidenceRoot = Join-Path $script:supplemental 'evidence'
$script:utf8NoBom = [Text.UTF8Encoding]::new($false)
$script:checks = [Collections.Generic.List[object]]::new()
$script:runtimeCalls = [Collections.Generic.List[object]]::new()
$script:caseResults = [Collections.Generic.List[object]]::new()
$script:metadataResults = [Collections.Generic.List[object]]::new()
$script:supplementalScratchAfter = @()
$script:startedUtc = [DateTime]::UtcNow.ToString('O')
$script:failure = $null
$script:originalAcceptancePath = $null
$script:prelaunchPath = $null
$script:startingStatePath = $null
$script:globalFreezePath = $null
$script:priorAcceptancePath = $null
$script:priorTracePath = $null
$script:originalAcceptanceHashBefore = $null
$script:prelaunchHashBefore = $null
$script:startingStateHashBefore = $null
$script:globalFreezeHashBefore = $null
$script:priorAcceptanceHashBefore = $null
$script:priorTraceHashBefore = $null
$script:bindingPassed = $false
$script:metadataPassed = $false
$script:behaviorPassed = $false
$script:scoringScratch = $null
$script:startingScratch = $null
$script:actorBefore = @()
$script:actorAfter = @()
$script:actorHashBefore = $null
$script:actorHashAfter = $null
$script:actorPath = $null
$script:startingProjectPath = $null
$script:priorActorPath = $null
$script:priorActorTreeHashAfter = $null
$script:startingTreeHashAfter = $null
$script:promptPath = $null
$script:promptHashBefore = $null
$script:currentHeadRevision = $null
$script:originalAcceptanceHashBefore = $null
$script:originalAcceptanceHashAfter = $null
$script:resolvedCli = $null
$script:pin = $null
$script:scratchActorProject = $null
$script:r04TraceAuditPath = $null
$script:startingTreeHashBefore = $null
$script:priorActorTreeHashBefore = $null
$script:oracle = $null
$script:targetOracle = $null
$script:independentCases = @()
$script:createdAt = $null
$script:protectedInputs = [Collections.Generic.List[object]]::new()

$script:expected = [ordered]@{
    studyId = 'business-policy-retention-003'
    originalAcceptanceSha256 = '0e6638878f89fe58c0859787f051652a6a6227e804192628cce75d80c71a5888'
    prelaunchSha256 = '16fa34e42b018adea8b2c1321e52887d760726c5c8a06daa088c9f45892fe5a3'
    startingStateSha256 = '391360f7ab7bb08d61a4c8a5a669d4010c04fac3105d1a36daa8e9bd9375aa50'
    globalFreezeSha256 = '142c026f9775ca7207fac3197f256ecc1469cb1dfa1979b5f6824f49b6cb1499'
    oracleSha256 = '8ca4f975161541b8c60397267fe7bb4ecb4345e00674d91467ac151a0bb7d3f2'
    designSha256 = 'c5b9d8a09cc671895f6ebd6bfd4023b8744cb8c8277cf6e2e7e6a4602d80b3ae'
    primerSha256 = '7ca98f0c8c9a20a84076d2a6b62da7a36ac12959e17ec1890ec74f9cbf9b578e'
    taskSha256 = 'f2a3b584fbec86f0395228fde31d561c240b9bc5d5a9a4a927042ea8a6111a04'
    priorAcceptanceSha256 = 'a6b74ac76864e6e1cae9e6f5c7de9f2c7fc4ec80df5cd7623b89112176b6b08a'
    priorTraceSha256 = 'f68d5269427d2aba3558b6bbf94b8971d6a9184e92b38ae554c02268288a1d9c'
    priorTreeSha256 = '9e72c4c0c6353e50339a2ef5cf7e61110062efc104a27984619a88595133e76e'
    actorOutputTreeSha256 = '56548f6c326262176c16761802a2f41f2850e6c0eef14ee85696316b449f65db'
    promptSha256 = '69f4d76ba1bc089716459a5537ce23a19ba253ae4b52fa906cfb5ec87bee88d3'
    promptUtf8Bytes = 7996
    sourceArtifactCount = 23
    runtimeFileCount = 26
}

function Get-Field($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    if ($Object -is [Collections.IDictionary]) { return $Object[$Name] }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-ProtocolField($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    if ($Object -is [Collections.IDictionary]) {
        if (-not $Object.Contains($Name)) { return $null }
        $value = $Object[$Name]
        if ($null -eq $value) { return $null }
        return ,$value
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) { return $null }
    return ,$property.Value
}

function Get-Sha256([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Hash input is missing: $Path" }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function ConvertTo-CanonicalJson($Value) {
    return ConvertTo-Json -InputObject $Value -Depth 100 -Compress
}

function Test-Within([string]$Path, [string]$Root) {
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if ($fullPath.Equals($fullRoot, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    return $fullPath.StartsWith($fullRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

function Resolve-FrozenProjectPath([string]$Path) {
    if ([IO.Path]::IsPathFullyQualified($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $script:repo $Path))
}

function Assert-NoReparseAncestors([string]$Path, [string]$Floor) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullFloor = [IO.Path]::GetFullPath($Floor)
    if (-not (Test-Within $fullPath $fullFloor)) { throw "Path escapes its allowed root: $fullPath" }
    $current = $fullPath
    while ($true) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point is not allowed in an evidence path: $current" }
        }
        if ($current.Equals($fullFloor, [StringComparison]::OrdinalIgnoreCase)) { break }
        $parent = Split-Path -Parent $current
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent.Equals($current, [StringComparison]::OrdinalIgnoreCase)) { throw "Could not validate path ancestors for $fullPath" }
        $current = $parent
    }
}

function Get-SafeFileInventory([string]$Root, [switch]$IncludeBuildArtifacts) {
    $resolved = [IO.Path]::GetFullPath($Root)
    if (-not (Test-Path -LiteralPath $resolved -PathType Container)) { throw "Inventory root is missing: $resolved" }
    $rootItem = Get-Item -LiteralPath $resolved -Force
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Inventory root is a reparse point: $resolved" }
    $queue = [Collections.Generic.Queue[string]]::new()
    $queue.Enqueue($resolved)
    $rows = [Collections.Generic.SortedDictionary[string,object]]::new([StringComparer]::Ordinal)
    while ($queue.Count -gt 0) {
        $directory = $queue.Dequeue()
        foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point is not allowed in a study project: $($item.FullName)" }
            if ($item.PSIsContainer) {
                $queue.Enqueue($item.FullName)
                continue
            }
            $relative = [IO.Path]::GetRelativePath($resolved, $item.FullName).Replace('\','/')
            if (-not $IncludeBuildArtifacts -and $relative.Split('/') -contains 'bin' -or -not $IncludeBuildArtifacts -and $relative.Split('/') -contains 'obj') { continue }
            $rows.Add($relative, [ordered]@{ path=$relative; bytes=[long]$item.Length; sha256=(Get-Sha256 $item.FullName) })
        }
    }
    $result = [object[]]::new($rows.Count)
    $index = 0
    foreach ($row in $rows.Values) { $result[$index] = $row; $index++ }
    return $result
}

function Get-TreeSha256([string]$Root) {
    # Match Verify-RetentionTrial.Get-TreeHash: canonical sorted inventory array, then compact UTF-8 JSON.
    $rows = ConvertTo-CanonicalInventoryRows (Get-SafeFileInventory $Root)
    $json = ConvertTo-Json -InputObject @($rows) -Depth 20 -Compress
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($script:utf8NoBom.GetBytes($json))).ToLowerInvariant()
}

function ConvertTo-CanonicalInventoryRows([object[]]$Rows) {
    $sorted = [Collections.Generic.SortedDictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach ($row in $Rows) {
        $path = ([string](Get-Field $row 'path')).Replace('\','/')
        $sorted.Add($path, [ordered]@{path=$path;bytes=[long](Get-Field $row 'bytes');sha256=([string](Get-Field $row 'sha256')).ToLowerInvariant()})
    }
    $result = [object[]]::new($sorted.Count)
    $index = 0
    foreach ($row in $sorted.Values) { $result[$index] = $row; $index++ }
    return $result
}

function Test-InventoryEqual([object[]]$Left, [object[]]$Right) {
    $leftRows = @(ConvertTo-CanonicalInventoryRows $Left)
    $rightRows = @(ConvertTo-CanonicalInventoryRows $Right)
    return (ConvertTo-CanonicalJson $leftRows) -ceq (ConvertTo-CanonicalJson $rightRows)
}

function Add-Check([string]$Name, [bool]$Passed, $Details = $null) {
    $script:checks.Add([ordered]@{name=$Name;passed=$Passed;details=$Details})
}

function Require-Check([string]$Name, [bool]$Passed, $Details = $null) {
    Add-Check $Name $Passed $Details
    if (-not $Passed) { throw "Supplemental binding check failed: $Name" }
}

function Resolve-RepoRelative([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or [IO.Path]::IsPathFullyQualified($Path) -or $Path -match '(^|[\\/])\.\.([\\/]|$)') {
        throw "Expected a safe repository-relative path, received '$Path'."
    }
    $full = [IO.Path]::GetFullPath((Join-Path $script:repo $Path))
    if (-not (Test-Within $full $script:repo)) { throw "Repository path escapes checkout: $Path" }
    Assert-NoReparseAncestors $full $script:repo
    return $full
}

function Get-GitBlobSha256([string]$Revision, [string]$RelativePath) {
    if ($RelativePath -match '^(?:[A-Za-z]:|/|\\)' -or $RelativePath -match '(^|[\\/])\.\.([\\/]|$)') { throw "Unsafe Git path: $RelativePath" }
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'git'
    $start.WorkingDirectory = $script:repo
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-C', $script:repo, 'show', "${Revision}:$RelativePath")) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (-not $process.Start()) { throw "Could not read committed source '$RelativePath'." }
    $stream = [IO.MemoryStream]::new()
    $copyTask = $process.StandardOutput.BaseStream.CopyToAsync($stream)
    $errorTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(30000)) {
        try { $process.Kill($true) } catch { }
        throw "Timed out reading committed source '$RelativePath'."
    }
    $copyTask.GetAwaiter().GetResult()
    $errorText = $errorTask.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) { throw "Committed source '$RelativePath' is unavailable: $errorText" }
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream.ToArray())).ToLowerInvariant()
    $stream.Dispose()
    $process.Dispose()
    return $hash
}

function Get-GitHead {
    $output = & git -C $script:repo rev-parse --verify HEAD 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the checkout HEAD commit.' }
    return ([string]$output).Trim()
}

function Get-CanonicalSourceRows([object[]]$Rows) {
    $result = foreach ($row in @($Rows | Sort-Object -Property path -CaseSensitive)) {
        [ordered]@{
            path=[string](Get-Field $row 'path')
            sourcePath=[string](Get-Field $row 'sourcePath')
            bytes=[long](Get-Field $row 'bytes')
            sha256=([string](Get-Field $row 'sha256')).ToLowerInvariant()
            gitBlobSha256=([string](Get-Field $row 'gitBlobSha256')).ToLowerInvariant()
            sourceGitBlobSha256=([string](Get-Field $row 'sourceGitBlobSha256')).ToLowerInvariant()
        }
    }
    return @($result)
}

function Get-CanonicalRuntimeRows([object[]]$Rows) {
    # Runtime rows are assembled as ordered dictionaries while frozen JSON rows
    # arrive as OrderedHashtable values. Sort-Object -Property does not resolve
    # keys consistently across those two dictionary types, so sort by explicit
    # field expressions to preserve the same canonical ordering for both.
    $sortedRows = @($Rows | Sort-Object -CaseSensitive -Property @{Expression={ [string](Get-Field $_ 'runtime') }},@{Expression={ [string](Get-Field $_ 'path') }})
    $result = foreach ($row in $sortedRows) {
        [ordered]@{
            runtime=[string](Get-Field $row 'runtime')
            path=([string](Get-Field $row 'path')).Replace('\','/')
            bytes=[long](Get-Field $row 'bytes')
            sha256=([string](Get-Field $row 'sha256')).ToLowerInvariant()
        }
    }
    return @($result)
}

function Test-PreviousAcceptanceRecordEqual($Left, $Right) {
    foreach ($name in @('runId','arm','block','taskId','path','sha256','projectTreeSha256','passed','carryForward')) {
        if ((Get-Field $Left $name) -cne (Get-Field $Right $name)) { return $false }
    }
    return $true
}

function Assert-PrelaunchProvenance($Context) {
    $pin = $Context.pin
    $state = $Context.state
    $global = $Context.globalFreeze
    $original = $Context.originalAcceptance
    $expected = $Context.expected

    Require-Check 'original canonical R04 acceptance hash is the saved failure' ([string]$Context.originalAcceptanceSha256 -ceq [string]$expected.originalAcceptanceSha256) @{expected=$expected.originalAcceptanceSha256;actual=$Context.originalAcceptanceSha256}
    Require-Check 'canonical R04 acceptance remains a failed non-frozen result' ($original.passed -eq $false -and [string]$original.resultKind -ceq 'unfrozen-preflight-or-control') @{passed=$original.passed;resultKind=$original.resultKind}
    Require-Check 'canonical R04 acceptance preserves the undefined prior failure and zero behavior cases' (([string]$original.failure -match '\$prior|Frozen starting-state') -and @($original.checks | Where-Object { [string]$_.name -ceq 'starting-state source and project pins' -and [string]$_.details -match '\$prior.*not been set' }).Count -eq 1 -and [int]$original.behavior.casesExecuted -eq 0 -and [int]$original.behavior.casesRecorded -eq 0) @{failure=$original.failure;casesExecuted=$original.behavior.casesExecuted;casesRecorded=$original.behavior.casesRecorded}
    Require-Check 'R04 has no completed canonical trace audit' (-not [bool]$Context.r04TraceAuditExists) $Context.r04TraceAuditPath
    Require-Check 'saved R04 prelaunch bytes match the hash in original acceptance' ([string]$Context.prelaunchSha256 -ceq [string]$original.prelaunch.sha256 -and [string]$Context.prelaunchSha256 -ceq [string]$expected.prelaunchSha256) @{acceptancePin=$original.prelaunch.sha256;actual=$Context.prelaunchSha256}
    Require-Check 'saved R04 starting-state bytes match its prelaunch pin' ([string]$Context.startingStateSha256 -ceq [string]$pin.startingStateSha256 -and [string]$Context.startingStateSha256 -ceq [string]$expected.startingStateSha256) @{pin=$pin.startingStateSha256;actual=$Context.startingStateSha256}
    Require-Check 'R04 prelaunch identity, launch status, and task slot match the frozen cell' ([int]$pin.schemaVersion -eq 1 -and [string]$pin.studyId -ceq $expected.studyId -and [string]$pin.arm -ceq 'retained' -and [string]$pin.block -ceq 'B1' -and [string]$pin.taskId -ceq 'S07' -and [int]$pin.sequenceIndex -eq 2 -and [string]$pin.runId -ceq 'R04' -and $pin.launchable -eq $true -and $pin.controlOnly -eq $false -and $pin.dirty -eq $false) @{studyId=$pin.studyId;arm=$pin.arm;block=$pin.block;taskId=$pin.taskId;runId=$pin.runId;launchable=$pin.launchable;dirty=$pin.dirty}
    $pinActorPath = Resolve-FrozenProjectPath ([string]$pin.projectPath)
    $pinStartingPath = Resolve-FrozenProjectPath ([string]$pin.startingProjectPath)
    $stateStartingPath = Resolve-FrozenProjectPath ([string]$state.project.path)
    Require-Check 'R04 project and starting-project paths match the saved starting-state paths' ($pinActorPath -ceq (Resolve-FrozenProjectPath ([string]$Context.expectedActorProjectPath)) -and $pinStartingPath -ceq (Resolve-FrozenProjectPath ([string]$Context.expectedStartProjectPath)) -and $stateStartingPath -ceq (Resolve-FrozenProjectPath ([string]$Context.expectedStartProjectPath))) @{actorPath=$pinActorPath;startingPath=$pinStartingPath;statePath=$stateStartingPath}
    Require-Check 'R04 frozen source revision is a full pinned Git identity' ([string]$pin.sourceRevision -match '^[0-9a-f]{40}$') @{frozenSourceRevision=$pin.sourceRevision;currentCheckoutHead=$Context.headRevision}
    Require-Check 'global freeze path and bytes match both R04 prelaunch and the expected snapshot' ([string]$pin.globalFreezePath -ceq 'experiments/AgentLang.SubagentTrials/business-policy-retention-003/artifacts/global-freeze.json' -and [string]$Context.globalFreezeSha256 -ceq [string]$pin.globalFreezeSha256 -and [string]$Context.globalFreezeSha256 -ceq [string]$expected.globalFreezeSha256 -and [string]$global.studyId -ceq $expected.studyId -and [int]$global.schemaVersion -eq 1) @{path=$pin.globalFreezePath;pin=$pin.globalFreezeSha256;actual=$Context.globalFreezeSha256;globalStudyId=$global.studyId}
    Require-Check 'pinned oracle, design, primer, and task hashes match the immutable study records' ([string]$pin.oraclePath -ceq 'experiments/AgentLang.SubagentTrials/business-policy-retention-003/acceptance.json' -and [string]$pin.oracleSha256 -ceq [string]$expected.oracleSha256 -and [string]$Context.oracleSha256 -ceq [string]$expected.oracleSha256 -and [string]$pin.designSha256 -ceq [string]$expected.designSha256 -and [string]$pin.primerSha256 -ceq [string]$expected.primerSha256 -and [string]$pin.taskPath -ceq 'experiments/AgentLang.Benchmarks/task-bank/public/S07.json' -and [string]$pin.taskSha256 -ceq [string]$expected.taskSha256) @{oracle=$pin.oracleSha256;design=$pin.designSha256;primer=$pin.primerSha256;task=$pin.taskSha256}
    Require-Check 'prelaunch and global source snapshots have the same frozen inventory' (@($pin.sourceArtifacts).Count -eq [int]$expected.sourceArtifactCount -and @($global.sourceArtifacts).Count -eq [int]$expected.sourceArtifactCount -and (ConvertTo-CanonicalJson (Get-CanonicalSourceRows @($pin.sourceArtifacts))) -ceq (ConvertTo-CanonicalJson (Get-CanonicalSourceRows @($global.sourceArtifacts)))) @{prelaunchCount=@($pin.sourceArtifacts).Count;globalCount=@($global.sourceArtifacts).Count}
    Require-Check 'prelaunch and global runtime inventories have the same frozen rows' (@($pin.runtimeFiles).Count -eq [int]$expected.runtimeFileCount -and @($global.runtime.files).Count -eq [int]$expected.runtimeFileCount -and (ConvertTo-CanonicalJson (Get-CanonicalRuntimeRows @($pin.runtimeFiles))) -ceq (ConvertTo-CanonicalJson (Get-CanonicalRuntimeRows @($global.runtime.files)))) @{prelaunchCount=@($pin.runtimeFiles).Count;globalCount=@($global.runtime.files).Count}
    Require-Check 'R04 prelaunch and starting-state predecessor pins agree' (Test-PreviousAcceptanceRecordEqual $pin.previousAcceptance $state.previousAcceptance)
    Require-Check 'R04 starting-state identity and prior outcome match the frozen pin' ([int]$state.schemaVersion -eq 1 -and [string]$state.studyId -ceq $expected.studyId -and [string]$state.arm -ceq 'retained' -and [string]$state.block -ceq 'B1' -and [string]$state.taskId -ceq 'S07' -and [int]$state.sequenceIndex -eq 2 -and [string]$state.runId -ceq 'R04' -and [string]$state.priorOutcome -ceq 'accepted-s01-carry-forward') @{runId=$state.runId;priorOutcome=$state.priorOutcome}
    Require-Check 'frozen R04 pin explicitly disables retention fallback' ($pin.retentionFallback -eq $false) @{retentionFallback=$pin.retentionFallback}
    $requiredOperations = @('words','test-all','describe','dependencies','transitive-dependencies','source','examples','example','eval')
    $missingOperations = @($requiredOperations | Where-Object { $_ -cnotin @($pin.allowedOperations) })
    $clockText = try { ([DateTimeOffset]$pin.clockValue).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ',[Globalization.CultureInfo]::InvariantCulture) } catch { '' }
    Require-Check 'frozen launch envelope permits only the requested read, test, example, and eval operations' ($missingOperations.Count -eq 0 -and [int]$pin.maxExchanges -ge 54 -and [int]$pin.maxRequestBytes -gt 0 -and [int]$pin.maxResponseBytes -gt 0 -and [int]$pin.exchangeTimeoutMilliseconds -gt 0 -and [string]$pin.profile -ceq 'agentlang' -and [string]$pin.hostProtocolVersion -ceq 'subagent-trial-host-v2' -and $clockText -ceq '2000-01-01T00:00:00Z' -and @($pin.capabilities).Count -eq 0 -and $pin.inspectionBudgetEnabled -eq $false) @{required=$requiredOperations;missing=$missingOperations;maxExchanges=$pin.maxExchanges;profile=$pin.profile;hostProtocolVersion=$pin.hostProtocolVersion;clockValue=$clockText;capabilities=@($pin.capabilities)}
    Require-Check 'original pinned prompt bytes are available and unchanged' ([string]$pin.promptPath -ceq [string]$Context.expectedPromptRelativePath -and [string]$Context.promptPath -ceq [string]$Context.expectedPromptPath -and [string]$Context.promptSha256 -ceq [string]$pin.promptSha256 -and [string]$pin.promptSha256 -ceq [string]$expected.promptSha256 -and [long]$pin.promptUtf8Bytes -eq [long]$expected.promptUtf8Bytes -and [long]$Context.promptBytes -eq [long]$pin.promptUtf8Bytes) @{path=$pin.promptPath;sha256=$Context.promptSha256;bytes=$Context.promptBytes}
}

function Assert-PredecessorBinding($Context) {
    $expected = $Context.expected
    $prior = $Context.priorAcceptance
    $trace = $Context.priorTrace
    $state = $Context.state
    $pinPrevious = $Context.pin.previousAcceptance
    $statePrevious = $Context.state.previousAcceptance
    Require-Check 'R03 was accepted, frozen, and identity-pinned as retained B1 S01' ([int]$prior.schemaVersion -eq 1 -and [string]$prior.studyId -ceq $expected.studyId -and [string]$prior.arm -ceq 'retained' -and [string]$prior.block -ceq 'B1' -and [string]$prior.taskId -ceq 'S01' -and [int]$prior.sequenceIndex -eq 1 -and [string]$prior.runId -ceq 'R03' -and $prior.passed -eq $true -and $prior.metadataPassed -eq $true -and $prior.behaviorPassed -eq $true -and $prior.checkFrozenPin -eq $true -and [string]$prior.resultKind -ceq 'frozen-actor-acceptance') @{studyId=$prior.studyId;arm=$prior.arm;block=$prior.block;taskId=$prior.taskId;runId=$prior.runId;passed=$prior.passed;resultKind=$prior.resultKind}
    Require-Check 'R04 prior acceptance record carries the exact accepted R03 hash and identity' (Test-PreviousAcceptanceRecordEqual $pinPrevious $statePrevious -and [string]$pinPrevious.runId -ceq 'R03' -and [string]$pinPrevious.taskId -ceq 'S01' -and [string]$pinPrevious.block -ceq 'B1' -and [string]$pinPrevious.arm -ceq 'retained' -and [string]$pinPrevious.path -ceq '.agentlang/business-policy-retention-003/runs/R03/acceptance.json' -and $pinPrevious.passed -eq $true -and $pinPrevious.carryForward -eq $true -and [string]$pinPrevious.sha256 -ceq [string]$expected.priorAcceptanceSha256 -and [string]$Context.priorAcceptanceSha256 -ceq [string]$pinPrevious.sha256) @{pin=$pinPrevious;state=$statePrevious;actualHash=$Context.priorAcceptanceSha256}
    Require-Check 'R03 acceptance project is the exact carried-forward starting tree' ([string]$prior.project.path -ceq $Context.expectedPriorProjectPath -and [string]$prior.project.treeSha256 -ceq [string]$expected.priorTreeSha256 -and [string]$prior.project.inventorySha256 -ceq [string]$expected.priorTreeSha256 -and [string]$prior.outputTreeSha256 -ceq [string]$expected.priorTreeSha256 -and [string]$Context.priorActorTreeSha256 -ceq [string]$expected.priorTreeSha256 -and [string]$Context.startingProjectTreeSha256 -ceq [string]$expected.priorTreeSha256 -and [string]$state.project.inventorySha256 -ceq [string]$pinPrevious.projectTreeSha256 -and [string]$pinPrevious.projectTreeSha256 -ceq [string]$expected.priorTreeSha256) @{priorTree=$prior.project.treeSha256;actualPriorActorTree=$Context.priorActorTreeSha256;startingTree=$Context.startingProjectTreeSha256;stateTree=$state.project.inventorySha256}
    Require-Check 'R03 passing trace audit binds the exact prior acceptance and project tree' ([string]$Context.priorTraceSha256 -ceq [string]$expected.priorTraceSha256 -and [string]$Context.priorTraceSha256 -ceq [string]$Context.pin.previousTraceAudit.sha256 -and [string]$Context.pin.previousTraceAudit.path -ceq '.agentlang/business-policy-retention-003/runs/R03/trace-audit.json' -and $Context.pin.previousTraceAudit.passed -eq $true -and $trace.passed -eq $true -and [string]$trace.studyId -ceq $expected.studyId -and [string]$trace.runId -ceq 'R03' -and [string]$trace.arm -ceq 'retained' -and [string]$trace.block -ceq 'B1' -and [string]$trace.taskId -ceq 'S01' -and [string]$trace.acceptancePath -ceq [string]$Context.pin.previousAcceptance.path -and [string]$trace.acceptanceSha256 -ceq [string]$pinPrevious.sha256 -and $trace.acceptancePassed -eq $true -and [string]$trace.projectTreeSha256 -ceq [string]$expected.priorTreeSha256 -and [string]$Context.pin.previousTraceAudit.acceptanceSha256 -ceq [string]$pinPrevious.sha256 -and [string]$Context.pin.previousTraceAudit.projectTreeSha256 -ceq [string]$expected.priorTreeSha256) @{traceAuditSha256=$Context.priorTraceSha256;acceptanceSha256=$trace.acceptanceSha256;projectTreeSha256=$trace.projectTreeSha256}
    Require-Check 'R04 starting project and launch-time actor inventories match the accepted predecessor tree' ([string]$Context.prelaunchStartingTreeSha256 -ceq [string]$expected.priorTreeSha256 -and [string]$Context.prelaunchActorTreeSha256 -ceq [string]$expected.priorTreeSha256 -and (Test-InventoryEqual @($Context.pin.startingProjectFiles) @($Context.startingProjectInventory)) -and (Test-InventoryEqual @($Context.pin.actorProjectFiles) @($Context.startingProjectInventory))) @{startingTree=$Context.prelaunchStartingTreeSha256;actorAtLaunch=$Context.prelaunchActorTreeSha256}
}

function Assert-ActorOutputBinding($Context) {
    $originalProject = $Context.originalAcceptance.project
    $expectedTree = [string]$Context.expected.actorOutputTreeSha256
    Require-Check 'failed canonical acceptance pins the current R04 actor output tree' ([string]$originalProject.path -ceq $Context.expectedActorProjectPath -and [string]$originalProject.treeSha256 -ceq $expectedTree -and [string]$originalProject.inventorySha256 -ceq $expectedTree -and [string]$Context.actorOutputTreeSha256 -ceq $expectedTree -and [int]$Context.actorOutputFileCount -eq @($originalProject.files).Count) @{expected=$expectedTree;acceptanceTree=$originalProject.treeSha256;actualTree=$Context.actorOutputTreeSha256;files=@($originalProject.files).Count}
    Require-Check 'R04 output inventory matches the original failed verifier before and after its stop' ((Test-InventoryEqual @($originalProject.files) @($Context.actorInventory)) -and (Test-InventoryEqual @($originalProject.filesBefore) @($Context.actorInventory)) -and (Test-InventoryEqual @($originalProject.filesAfter) @($Context.actorInventory))) @{actorFiles=$Context.actorOutputFileCount;before=@($originalProject.filesBefore).Count;after=@($originalProject.filesAfter).Count}
}

function Invoke-OnlyAfterBindings($Context, [scriptblock]$Action) {
    Assert-PrelaunchProvenance $Context
    Assert-PredecessorBinding $Context
    Assert-ActorOutputBinding $Context
    return & $Action
}

function Get-S07Expected($Task, $Case) {
    $kind = [string]$Case.kind
    $balanceText = [string]$Case.balanceMinor
    if ($balanceText -cnotmatch '^(0|-?[1-9][0-9]*)$') { throw "Case '$($Case.id)' has noncanonical balanceMinor '$balanceText'." }
    $balance = [Numerics.BigInteger]::Parse($balanceText, [Globalization.NumberStyles]::AllowLeadingSign, [Globalization.CultureInfo]::InvariantCulture)
    if ($balance -lt [Numerics.BigInteger]([long]::MinValue) -or $balance -gt [Numerics.BigInteger]([long]::MaxValue)) { throw "Case '$($Case.id)' is outside signed Int64 range." }
    $premium = [string]::Equals($kind, 'premium', [StringComparison]::Ordinal)
    $value = if ($premium) {
        # BigInteger division truncates toward zero and cannot overflow at Int64 boundaries.
        ([Numerics.BigInteger]::Divide(($balance * [Numerics.BigInteger]9), [Numerics.BigInteger]10)).ToString([Globalization.CultureInfo]::InvariantCulture)
    } else {
        $balance.ToString([Globalization.CultureInfo]::InvariantCulture)
    }
    if ([string]$Task.id -cne 'S07' -or @($Task.outputs).Count -ne 1 -or [string]$Task.outputs[0] -cne 'Money') { throw "Unsupported supplemental oracle task '$($Task.id)'." }
    if ([string]$Case.expected.type -cne 'Money' -or $Case.expected.value -isnot [string] -or [string]$Case.expected.value -cne $value) { throw "Case '$($Case.id)' pinned expected value differs from independent BigInteger arithmetic." }
    return [ordered]@{type='Money';value=$value;balanceMinor=$balanceText;premium=$premium;rounding='integer division truncates toward zero'}
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
    return @($found | Sort-Object -CaseSensitive)
}

function Get-UserWordRows([object[]]$WordRows) {
    # Callers wrap this helper in @(...); stream rows rather than returning a
    # single nested array so inventory cardinality and per-row checks stay exact.
    return @($WordRows | Where-Object { ([string](Get-Field $_ 'id')).StartsWith('word_', [StringComparison]::Ordinal) })
}

function Invoke-JsonlSession([string]$Project, [object[]]$Requests, [string]$Label) {
    if (-not (Test-Within $Project $script:supplemental) -or -not (Test-Within $Project $script:scratchRoot)) { throw "CLI project is outside supplemental scratch: $Project" }
    Assert-NoReparseAncestors $Project $script:supplemental
    if ($Requests.Count -gt 95) { throw "JSONL session '$Label' exceeds the independent 95-request cap." }
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.WorkingDirectory = $Project
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $script:utf8NoBom
    $start.StandardErrorEncoding = $script:utf8NoBom
    $start.StandardInputEncoding = $script:utf8NoBom
    $start.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $start.Environment['DOTNET_NOLOGO'] = '1'
    $start.ArgumentList.Add($script:resolvedCli)
    $start.ArgumentList.Add('--project')
    $start.ArgumentList.Add($Project)
    $start.ArgumentList.Add('--jsonl')
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $requestOps = @($Requests | ForEach-Object { [string](Get-Field $_ 'op') })
    $call = [ordered]@{label=$Label;project=$Project;requestCount=$Requests.Count;requestOps=$requestOps;responseCount=0;exitCode=$null;timedOut=$false;durationMs=$null;stderr=$null;passed=$false}
    $responses = [Collections.Generic.List[object]]::new()
    try {
        if (-not $process.Start()) { throw "Could not start pinned JSONL runtime session '$Label'." }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        foreach ($request in $Requests) { $process.StandardInput.WriteLine((ConvertTo-Json -InputObject $request -Depth 80 -Compress)) }
        $process.StandardInput.Close()
        $finished = $process.WaitForExit(180000)
        if (-not $finished) {
            $call.timedOut = $true
            try { $process.Kill($true) } catch { }
            $process.WaitForExit()
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $call.exitCode = if ($finished) { $process.ExitCode } else { $null }
        $call.stderr = if ([string]::IsNullOrWhiteSpace($stderr)) { '' } elseif ($stderr.Length -le 2000) { $stderr } else { $stderr.Substring(0,2000) }
        $lines = @($stdout -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        foreach ($line in $lines) { $responses.Add((ConvertFrom-Json -InputObject $line -AsHashtable -Depth 100)) }
        $call.responseCount = $responses.Count
        $clock.Stop()
        $call.durationMs = $clock.ElapsedMilliseconds
        $call.passed = $finished -and $process.ExitCode -eq 0 -and $responses.Count -eq $Requests.Count
        if (-not $finished) { throw "Pinned runtime session '$Label' timed out." }
        if ($process.ExitCode -ne 0) { throw "Pinned runtime session '$Label' exited $($process.ExitCode): $($call.stderr)" }
        if ($responses.Count -ne $Requests.Count) { throw "Pinned runtime session '$Label' returned $($responses.Count) responses for $($Requests.Count) requests." }
        foreach ($response in $responses) { if ((Get-Field $response 'ok') -ne $true) { $call.passed = $false } }
        return ,@($responses.ToArray())
    } catch {
        if ($clock.IsRunning) { $clock.Stop(); $call.durationMs = $clock.ElapsedMilliseconds }
        $call.error = $_.Exception.Message
        throw
    } finally {
        $script:runtimeCalls.Add($call)
        $process.Dispose()
    }
}

function Assert-ResponsesOk([object[]]$Responses, [string]$Label) {
    for ($index = 0; $index -lt $Responses.Count; $index++) {
        if ((Get-Field $Responses[$index] 'ok') -ne $true) {
            $errorValue = Get-Field (Get-Field $Responses[$index] 'error') 'message'
            if ([string]::IsNullOrWhiteSpace([string]$errorValue)) { $errorValue = ConvertTo-CanonicalJson $Responses[$index] }
            throw "$Label request $index failed: $errorValue"
        }
    }
}

function Get-SourceMap([string]$Project, [string]$Selector, [string[]]$Names, [string]$Label) {
    $map = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    for ($offset=0; $offset -lt $Names.Count; $offset += 90) {
        $end = [Math]::Min($offset + 90, $Names.Count)
        $requests = [Collections.Generic.List[object]]::new()
        for ($index=$offset; $index -lt $end; $index++) {
            $request = [ordered]@{op='source'}
            $request[$Selector] = $Names[$index]
            $requests.Add($request)
        }
        if ($requests.Count -eq 0) { continue }
        $responses = Invoke-JsonlSession $Project @($requests) "$Label source $($offset + 1)-$end"
        Assert-ResponsesOk $responses $Label
        for ($index=0; $index -lt $responses.Count; $index++) {
            $map[$Names[$offset + $index]] = [string](Get-Field $responses[$index] 'data')
        }
    }
    return ,$map
}

function Copy-ProjectToScratch([string]$Source, [string]$Destination) {
    if (-not (Test-Within $Destination $script:scratchRoot)) { throw "Scratch destination escapes supplemental scratch: $Destination" }
    Assert-NoReparseAncestors $Destination $script:supplemental
    $inventory = @(Get-SafeFileInventory $Source)
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    foreach ($row in $inventory) {
        $sourceFile = Join-Path $Source ([string]$row.path.Replace('/', [IO.Path]::DirectorySeparatorChar))
        $targetFile = Join-Path $Destination ([string]$row.path.Replace('/', [IO.Path]::DirectorySeparatorChar))
        if (-not (Test-Within $targetFile $Destination)) { throw "Scratch copy path escaped destination: $targetFile" }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($targetFile)) | Out-Null
        [IO.File]::Copy($sourceFile, $targetFile, $false)
    }
    $copied = @(Get-SafeFileInventory $Destination)
    if (-not (Test-InventoryEqual $inventory $copied)) { throw "Scratch project did not match its source inventory: $Destination" }
}

function Get-StructuredValue($Data) {
    $stack = Get-Field $Data 'structuredStack'
    $values = @(Get-Field $stack 'values')
    if ($values.Count -ne 1) { throw 'Structured eval must return exactly one value.' }
    return $values[0]
}

function Test-ProtocolCollection($Value) {
    return $null -ne $Value -and $Value -is [Collections.ICollection] -and $Value -isnot [string]
}

function Test-ProtocolEffects($Value) {
    return $null -ne $Value -and ($Value -is [Collections.IDictionary] -or (Test-ProtocolCollection $Value))
}

function New-S07ResponseFixture([string]$Value) {
    return [ordered]@{
        ok=$true
        data=[ordered]@{
            stackTypes=@('Money')
            effects=[ordered]@{}
            console=@()
            structuredStack=[ordered]@{values=@([ordered]@{kind='scalar';name='Money';baseType=[ordered]@{kind='int'};value=[ordered]@{kind='int';value=$Value}})}
        }
    }
}

function Get-Count($Value) {
    if ($null -eq $Value) { return 0 }
    if ($Value -is [Collections.IDictionary]) { return $Value.Count }
    if ($Value -is [Array] -or $Value -is [Collections.ICollection]) { return $Value.Count }
    return @($Value.PSObject.Properties).Count
}

function Test-S07Response($Response, $Expected) {
    if ((Get-Field $Response 'ok') -ne $true) { return [ordered]@{passed=$false;diagnostic='runtime protocol returned ok=false';actual=$null} }
    try {
        $data = Get-Field $Response 'data'
        $stackTypesField = Get-ProtocolField $data 'stackTypes'
        $effectsField = Get-ProtocolField $data 'effects'
        $consoleField = Get-ProtocolField $data 'console'
        $structuredStack = Get-Field $data 'structuredStack'
        $structuredValues = Get-ProtocolField $structuredStack 'values'
        $protocolShapeValid = (Test-ProtocolCollection $stackTypesField) -and (Test-ProtocolEffects $effectsField) -and (Test-ProtocolCollection $consoleField) -and (Test-ProtocolCollection $structuredValues)
        if (-not $protocolShapeValid) { return [ordered]@{passed=$false;diagnostic='missing or malformed stackTypes, effects, console, or structuredStack.values protocol collection';actual=[ordered]@{stackTypes=$stackTypesField;effects=$effectsField;console=$consoleField;structuredValues=$structuredValues}} }
        $stackTypes = @($stackTypesField)
        $effects = $effectsField
        $console = @($consoleField)
        $value = Get-StructuredValue $data
        $payload = Get-Field $value 'value'
        $baseType = Get-Field $value 'baseType'
        $actual = [ordered]@{stackTypes=$stackTypes;value=$value;effects=$effects;console=$console}
        $passed = $stackTypes.Count -eq 1 -and [string]$stackTypes[0] -ceq 'Money' -and (Get-Count $effects) -eq 0 -and $console.Count -eq 0 -and [string](Get-Field $value 'kind') -ceq 'scalar' -and [string](Get-Field $value 'name') -ceq 'Money' -and [string](Get-Field $baseType 'kind') -ceq 'int' -and [string](Get-Field $payload 'kind') -ceq 'int' -and [string](Get-Field $payload 'value') -ceq [string]$Expected.value
        $diagnostic = if ($passed) { $null } else { 'structured type/value, effect, or console output differs from independent oracle' }
        return [ordered]@{passed=$passed;diagnostic=$diagnostic;actual=$actual}
    } catch {
        return [ordered]@{passed=$false;diagnostic=$_.Exception.Message;actual=$null}
    }
}

function Invoke-MetadataChecks([string]$ActorProject, [string]$StartProject, $OracleTask) {
    $target = [string]$OracleTask.symbol
    $priorTask = @($script:oracle.tasks | Where-Object { [string]$_.id -ceq 'S01' })
    if ($priorTask.Count -ne 1) { throw 'Pinned oracle must contain exactly one preceding S01 task.' }
    $priorSymbol = [string]$priorTask[0].symbol

    $wordResponses = Invoke-JsonlSession $ActorProject @([ordered]@{op='words'}) 'fresh R04 actor dictionary reload'
    Assert-ResponsesOk $wordResponses 'fresh R04 actor dictionary reload'
    $actorRows = @((Get-Field (Get-Field $wordResponses[0] 'data') 'words'))
    $actorByName = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach ($row in $actorRows) { $actorByName[[string](Get-Field $row 'name')] = $row }
    Require-Check 'S07 target exists as an authored user word with the exact acceptance signature' ($actorByName.ContainsKey($target) -and ([string](Get-Field $actorByName[$target] 'id')).StartsWith('word_', [StringComparison]::Ordinal) -and (@(Get-Field $actorByName[$target] 'inputs') -join ',') -ceq 'Customer' -and (@(Get-Field $actorByName[$target] 'outputs') -join ',') -ceq 'Money') @{symbol=$target;id=$(if ($actorByName.ContainsKey($target)) { Get-Field $actorByName[$target] 'id' } else { $null })}
    $targetRow = $actorByName[$target]
    Require-Check 'S07 target is a persistent library word' ([string](Get-Field $targetRow 'status') -ceq 'persistent' -and [string](Get-Field $targetRow 'maturity') -ceq 'library') @{status=$targetRow.status;maturity=$targetRow.maturity}

    $requests = @(
        [ordered]@{op='test-all'},
        [ordered]@{op='describe';word=$target},
        [ordered]@{op='dependencies';word=$target},
        [ordered]@{op='transitive-dependencies';word=$target},
        [ordered]@{op='source';word=$target},
        [ordered]@{op='examples';word=$target}
    )
    $responses = Invoke-JsonlSession $ActorProject $requests 'R04 target tests, metadata, dependency graph, source, and example reload'
    Assert-ResponsesOk $responses 'R04 target metadata and test session'
    $byOp = @{}
    for ($index=0; $index -lt $requests.Count; $index++) { $byOp[[string]$requests[$index].op] = Get-Field $responses[$index] 'data' }
    $testRows = @((Get-Field $byOp['test-all'] 'results'))
    $describe = $byOp['describe']
    $directDeps = @((Get-Field $byOp['dependencies'] 'dependencies') | ForEach-Object { [string]$_ })
    $transitiveDeps = @((Get-Field $byOp['transitive-dependencies'] 'dependencies') | ForEach-Object { [string]$_ })
    if ($transitiveDeps.Count -eq 0) { $transitiveDeps = @((Get-Field $describe 'transitiveDependencies') | ForEach-Object { [string]$_ }) }
    $targetSource = [string]$byOp['source']
    $exampleNames = @($byOp['examples'])
    Require-Check 'all attached actor tests pass and the S07 target owns a passing test' ($testRows.Count -gt 0 -and @($testRows | Where-Object { (Get-Field $_ 'passed') -ne $true }).Count -eq 0 -and [int](Get-Field $describe 'testCount') -gt 0 -and @($testRows | Where-Object { [string](Get-Field $_ 'word') -ceq $target -and (Get-Field $_ 'passed') -eq $true }).Count -gt 0) @{testCount=$testRows.Count;targetTestCount=$describe.testCount;failed=@($testRows | Where-Object { (Get-Field $_ 'passed') -ne $true })}
    Require-Check 'S07 target has meaningful documentation and attached examples' ([string](Get-Field $describe 'documentation') -match '\S.{7,}' -and $targetSource -match '(?m)^\s*doc\s+".{8,}"' -and [int](Get-Field $describe 'exampleCount') -gt 0 -and $exampleNames.Count -gt 0) @{documentation=$describe.documentation;examples=$exampleNames}
    Require-Check 'S07 target is pure, supported, persistent, and nondeprecated' (@(Get-Field $describe 'effects').Count -eq 0 -and [string](Get-Field $describe 'status') -ceq 'persistent' -and [string](Get-Field $describe 'maturity') -ceq 'library' -and (Get-Field $describe 'deprecated') -eq $false -and [string](Get-Field $describe 'kind') -ceq 'word') @{effects=$describe.effects;status=$describe.status;maturity=$describe.maturity;deprecated=$describe.deprecated;kind=$describe.kind}
    $coverage = Get-Field $describe 'coverage'
    Require-Check 'S07 target has current complete instruction and branch coverage' ([string](Get-Field $coverage 'status') -ceq 'current' -and [int](Get-Field $describe 'testCount') -gt 0 -and [int](Get-Field $coverage 'instructionsTotal') -gt 0 -and [int](Get-Field $coverage 'instructionsCovered') -eq [int](Get-Field $coverage 'instructionsTotal') -and [int](Get-Field $coverage 'branchesCovered') -eq [int](Get-Field $coverage 'branchesTotal')) $coverage

    $exampleResponse = Invoke-JsonlSession $ActorProject @([ordered]@{op='example';word=$target}) 'run R04 target examples in fresh process'
    Assert-ResponsesOk $exampleResponse 'R04 target examples'
    $exampleResults = @((Get-Field (Get-Field $exampleResponse[0] 'data') 'results'))
    $targetReference = $target -replace '\.','::'
    Require-Check 'all attached examples pass and call the S07 target' ($exampleResults.Count -eq $exampleNames.Count -and $exampleResults.Count -gt 0 -and @($exampleResults | Where-Object { (Get-Field $_ 'passed') -ne $true -or [string](Get-Field $_ 'source') -notmatch [regex]::Escape($targetReference) -or [string](Get-Field $_ 'source') -notmatch '=>'}).Count -eq 0) @{count=$exampleResults.Count;results=$exampleResults}

    $startWordsResponse = Invoke-JsonlSession $StartProject @([ordered]@{op='words'}) 'fresh immutable R04 starting dictionary reload'
    Assert-ResponsesOk $startWordsResponse 'fresh R04 starting dictionary reload'
    $startRows = @((Get-Field (Get-Field $startWordsResponse[0] 'data') 'words'))
    $startUsers = @(Get-UserWordRows $startRows)
    $startTypes = @(Get-NominalTypeNames $startRows)
    $startNames = @($startRows | ForEach-Object { [string](Get-Field $_ 'name') })
    Require-Check 'retained S07 immutable start contains its accepted S01 helper but not the S07 target' ($startUsers.Count -eq 54 -and $startTypes.Count -eq 31 -and $priorSymbol -cin $startNames -and $target -cnotin $startNames) @{userWords=$startUsers.Count;nominalTypes=$startTypes.Count;priorHelper=$priorSymbol;targetPresent=($target -cin $startNames)}

    $startWordNames = @($startUsers | ForEach-Object { [string](Get-Field $_ 'name') } | Sort-Object -Unique -CaseSensitive)
    $startWordSources = Get-SourceMap $StartProject 'word' $startWordNames 'R04 starting dictionary'
    $actorWordSources = Get-SourceMap $ActorProject 'word' $startWordNames 'R04 output dictionary'
    $startTypeSources = Get-SourceMap $StartProject 'type' $startTypes 'R04 starting nominal types'
    $actorTypeSources = Get-SourceMap $ActorProject 'type' $startTypes 'R04 output nominal types'
    $changedWords = [Collections.Generic.List[string]]::new()
    $typeChanges = [Collections.Generic.List[string]]::new()
    foreach ($startRow in $startUsers) {
        $name = [string](Get-Field $startRow 'name')
        if (-not $actorByName.ContainsKey($name)) { $changedWords.Add("missing:$name"); continue }
        $actorRow = $actorByName[$name]
        $same = ([string]$actorRow.id -ceq [string]$startRow.id) -and (@($actorRow.inputs) -join "`0") -ceq (@($startRow.inputs) -join "`0") -and (@($actorRow.outputs) -join "`0") -ceq (@($startRow.outputs) -join "`0") -and ([string]$actorRow.status -ceq [string]$startRow.status) -and ([string]$actorRow.maturity -ceq [string]$startRow.maturity) -and $actorWordSources[$name] -ceq $startWordSources[$name]
        if (-not $same) { $changedWords.Add($name) }
    }
    Require-Check 'all prior user words retain identity, signature, status, maturity, and source' ($changedWords.Count -eq 0) @($changedWords)
    foreach ($name in $startTypes) {
        if (-not $actorTypeSources.ContainsKey($name) -or $actorTypeSources[$name] -cne $startTypeSources[$name]) { $typeChanges.Add($name) }
    }
    Require-Check 'all nominal types from the immutable start retain their source' ($typeChanges.Count -eq 0) @($typeChanges)
    $addedUserWords = @($actorRows | Where-Object { ([string](Get-Field $_ 'id')).StartsWith('word_', [StringComparison]::Ordinal) -and [string](Get-Field $_ 'name') -notin $startWordNames } | ForEach-Object { [string](Get-Field $_ 'name') } | Sort-Object -CaseSensitive)
    Require-Check 'retained S01 helper is an observed direct or transitive S07 dependency' ($priorSymbol -cin $directDeps -or $priorSymbol -cin $transitiveDeps) @{priorTaskSymbol=$priorSymbol;directDependencies=$directDeps;transitiveDependencies=$transitiveDeps}

    $refreshResponses = Invoke-JsonlSession $ActorProject @([ordered]@{op='test-all'},[ordered]@{op='describe';word=$target}) 'refresh R04 target tests and coverage in fresh process'
    Assert-ResponsesOk $refreshResponses 'R04 refreshed coverage'
    $freshTestRows = @((Get-Field (Get-Field $refreshResponses[0] 'data') 'results'))
    $freshDescribe = Get-Field $refreshResponses[1] 'data'
    $freshCoverage = Get-Field $freshDescribe 'coverage'
    Require-Check 'freshly refreshed target tests all pass' ($freshTestRows.Count -gt 0 -and @($freshTestRows | Where-Object { (Get-Field $_ 'passed') -ne $true }).Count -eq 0) @{count=$freshTestRows.Count;failed=@($freshTestRows | Where-Object { (Get-Field $_ 'passed') -ne $true })}
    Require-Check 'freshly refreshed target coverage remains complete' ([string](Get-Field $freshCoverage 'status') -ceq 'current' -and [int](Get-Field $freshCoverage 'instructionsTotal') -gt 0 -and [int](Get-Field $freshCoverage 'instructionsCovered') -eq [int](Get-Field $freshCoverage 'instructionsTotal') -and [int](Get-Field $freshCoverage 'branchesCovered') -eq [int](Get-Field $freshCoverage 'branchesTotal')) $freshCoverage

    $script:metadataResults.Add([ordered]@{target=$target;priorTaskSymbol=$priorSymbol;directDependencies=$directDeps;transitiveDependencies=$transitiveDeps;reusedPriorTaskSymbols=@($priorSymbol | Where-Object { $_ -cin $directDeps -or $_ -cin $transitiveDeps });startingUserWordCount=$startUsers.Count;startingNominalTypeCount=$startTypes.Count;preservedWords=$startWordNames.Count;preservedTypes=$startTypes.Count;addedUserWords=$addedUserWords;tests=$testRows.Count;examples=$exampleResults.Count;coverage=$freshCoverage})
    return $actorRows
}

function Get-S07EvalRequest($Case, $Expected) {
    $id = ConvertTo-Json -InputObject ([string]$script:oracle.defaults.customerId) -Compress
    $email = ConvertTo-Json -InputObject ([string]$script:oracle.defaults.email) -Compress
    $kind = ConvertTo-Json -InputObject ([string]$Case.kind) -Compress
    $customer = "customer::new(id = CustomerId::new($id), email = Email::new($email), kind = $kind, balance = Money::new($($Expected.balanceMinor)), created-at = Instant::new(`"$($script:createdAt)`"))"
    $reference = ([string]$script:targetOracle.symbol) -replace '^([^.]+)\.', '$1::'
    return [ordered]@{op='eval';frontend='flow';structured=$true;code="$reference($customer)"}
}

function Invoke-OracleCases([string]$ActorProject) {
    $cases = @($script:targetOracle.cases)
    $caseIndex = 0
    for ($offset=0; $offset -lt $cases.Count; $offset += 45) {
        $end = [Math]::Min($offset + 45, $cases.Count)
        $requests = [Collections.Generic.List[object]]::new()
        for ($index=$offset; $index -lt $end; $index++) { $requests.Add((Get-S07EvalRequest $cases[$index] $script:independentCases[$index])) }
        $responses = @()
        $batchFailure = $null
        try { $responses = Invoke-JsonlSession $ActorProject @($requests) "R04 S07 oracle cases $($offset + 1)-$end" }
        catch { $batchFailure = $_.Exception.Message }
        for ($index=$offset; $index -lt $end; $index++) {
            $case = $cases[$index]
            $expected = $script:independentCases[$index]
            $local = $index - $offset
            if ($null -ne $batchFailure -or $local -ge $responses.Count) {
                $script:caseResults.Add([ordered]@{index=$index+1;id=[string]$case.id;kind=[string]$case.kind;balanceMinor=[string]$case.balanceMinor;executed=$false;responseOk=$null;passed=$false;expected=[ordered]@{type=$expected.type;value=$expected.value};actual=$null;diagnostic=$batchFailure})
                continue
            }
            $response = $responses[$local]
            $check = Test-S07Response $response $expected
            $script:caseResults.Add([ordered]@{index=$index+1;id=[string]$case.id;kind=[string]$case.kind;balanceMinor=[string]$case.balanceMinor;executed=$true;responseOk=((Get-Field $response 'ok') -eq $true);passed=[bool]$check.passed;expected=[ordered]@{type=$expected.type;value=$expected.value};actual=$check.actual;diagnostic=$check.diagnostic})
        }
    }
    $executed = @($script:caseResults | Where-Object { $_.executed }).Count
    $failed = @($script:caseResults | Where-Object { -not $_.passed })
    Require-Check 'all 54 unchanged S07 oracle cases executed and matched' ($script:caseResults.Count -eq 54 -and $executed -eq 54 -and $failed.Count -eq 0) @{expected=54;recorded=$script:caseResults.Count;executed=$executed;failed=@($failed | Select-Object id,diagnostic)}
}

function New-SelfTestFixture {
    $sourceHash = 'a' * 64
    $originalHash = 'b' * 64
    $prelaunchHash = 'c' * 64
    $stateHash = 'd' * 64
    $globalHash = 'e' * 64
    $priorHash = 'f' * 64
    $traceHash = '1' * 64
    $priorTree = '2' * 64
    $outputTree = '3' * 64
    $revision = '4' * 40
    $inventory = @([ordered]@{path='dictionary.agent';bytes=7;sha256=$sourceHash})
    $previous = [ordered]@{runId='R03';arm='retained';block='B1';taskId='S01';path='.agentlang/business-policy-retention-003/runs/R03/acceptance.json';sha256=$priorHash;projectTreeSha256=$priorTree;passed=$true;carryForward=$true}
    $pin = [ordered]@{schemaVersion=1;studyId='business-policy-retention-003';arm='retained';block='B1';taskId='S07';sequenceIndex=2;runId='R04';launchable=$true;controlOnly=$false;dirty=$false;retentionFallback=$false;sourceRevision=$revision;projectPath='actors/R04';startingProjectPath='starting-project';promptPath='prompt.txt';promptSha256=$sourceHash;promptUtf8Bytes=7;allowedOperations=@('words','test-all','describe','dependencies','transitive-dependencies','source','examples','example','eval');maxExchanges=100;maxRequestBytes=262144;maxResponseBytes=524288;exchangeTimeoutMilliseconds=120000;profile='agentlang';hostProtocolVersion='subagent-trial-host-v2';clockValue='2000-01-01T00:00:00Z';capabilities=@();inspectionBudgetEnabled=$false;globalFreezePath='experiments/AgentLang.SubagentTrials/business-policy-retention-003/artifacts/global-freeze.json';globalFreezeSha256=$globalHash;startingStateSha256=$stateHash;oraclePath='experiments/AgentLang.SubagentTrials/business-policy-retention-003/acceptance.json';oracleSha256=$sourceHash;designPath='experiments/AgentLang.SubagentTrials/business-policy-retention-003/design.json';designSha256=$sourceHash;primerPath='experiments/AgentLang.SubagentTrials/business-policy-retention-003/language-primer.md';primerSha256=$sourceHash;taskPath='experiments/AgentLang.Benchmarks/task-bank/public/S07.json';taskSha256=$sourceHash;sourceArtifacts=@([ordered]@{path='snapshot/dictionary.agent';sourcePath='dictionary.agent';bytes=7;sha256=$sourceHash;gitBlobSha256=$sourceHash;sourceGitBlobSha256=$sourceHash});runtimeFiles=@([ordered]@{runtime='cli';path='cli.dll';bytes=7;sha256=$sourceHash});previousAcceptance=$previous;previousTraceAudit=[ordered]@{path='.agentlang/business-policy-retention-003/runs/R03/trace-audit.json';sha256=$traceHash;acceptanceSha256=$priorHash;projectTreeSha256=$priorTree;passed=$true};startingProjectFiles=$inventory;actorProjectFiles=$inventory}
    $prior = [ordered]@{schemaVersion=1;studyId='business-policy-retention-003';arm='retained';block='B1';taskId='S01';sequenceIndex=1;runId='R03';passed=$true;metadataPassed=$true;behaviorPassed=$true;checkFrozenPin=$true;resultKind='frozen-actor-acceptance';project=[ordered]@{path='actors/R03';treeSha256=$priorTree;inventorySha256=$priorTree};outputTreeSha256=$priorTree}
    $trace = [ordered]@{passed=$true;studyId='business-policy-retention-003';runId='R03';arm='retained';block='B1';taskId='S01';acceptancePath=$previous.path;acceptanceSha256=$priorHash;acceptancePassed=$true;projectTreeSha256=$priorTree}
    $actorFile = @([ordered]@{path='dictionary.agent';bytes=9;sha256=$sourceHash})
    $original = [ordered]@{passed=$false;resultKind='unfrozen-preflight-or-control';failure="The variable '`$prior' cannot be retrieved because it has not been set.";prelaunch=[ordered]@{sha256=$prelaunchHash};behavior=[ordered]@{casesExecuted=0;casesRecorded=0};checks=@([ordered]@{name='starting-state source and project pins';details="The variable '`$prior' cannot be retrieved because it has not been set."});project=[ordered]@{path='actors/R04';treeSha256=$outputTree;inventorySha256=$outputTree;files=$actorFile;filesBefore=$actorFile;filesAfter=$actorFile}}
    $state = [ordered]@{schemaVersion=1;studyId='business-policy-retention-003';arm='retained';block='B1';taskId='S07';sequenceIndex=2;runId='R04';priorOutcome='accepted-s01-carry-forward';previousAcceptance=$previous;project=[ordered]@{path='starting-project';inventorySha256=$priorTree}}
    $global = [ordered]@{schemaVersion=1;studyId='business-policy-retention-003';sourceArtifacts=$pin.sourceArtifacts;runtime=[ordered]@{files=$pin.runtimeFiles};oracle=[ordered]@{path=$pin.oraclePath;sha256=$pin.oracleSha256};design=[ordered]@{path=$pin.designPath;sha256=$pin.designSha256};primer=[ordered]@{path=$pin.primerPath;sha256=$pin.primerSha256};tasks=@([ordered]@{path='experiments/AgentLang.Benchmarks/task-bank/public/S01.json';sha256=$sourceHash},[ordered]@{path=$pin.taskPath;sha256=$pin.taskSha256})}
    return [ordered]@{expected=[ordered]@{studyId='business-policy-retention-003';originalAcceptanceSha256=$originalHash;prelaunchSha256=$prelaunchHash;startingStateSha256=$stateHash;globalFreezeSha256=$globalHash;oracleSha256=$sourceHash;designSha256=$sourceHash;primerSha256=$sourceHash;taskSha256=$sourceHash;priorAcceptanceSha256=$priorHash;priorTraceSha256=$traceHash;priorTreeSha256=$priorTree;actorOutputTreeSha256=$outputTree;promptSha256=$sourceHash;promptUtf8Bytes=7;sourceArtifactCount=1;runtimeFileCount=1};pin=$pin;state=$state;globalFreeze=$global;originalAcceptance=$original;originalAcceptanceSha256=$originalHash;prelaunchSha256=$prelaunchHash;startingStateSha256=$stateHash;globalFreezeSha256=$globalHash;oracleSha256=$sourceHash;headRevision=$revision;priorAcceptance=$prior;priorAcceptanceSha256=$priorHash;priorTrace=$trace;priorTraceSha256=$traceHash;priorActorTreeSha256=$priorTree;startingProjectTreeSha256=$priorTree;prelaunchStartingTreeSha256=$priorTree;prelaunchActorTreeSha256=$priorTree;startingProjectInventory=$inventory;expectedPriorProjectPath='actors/R03';expectedActorProjectPath='actors/R04';expectedStartProjectPath='starting-project';promptPath='runs/R04/prompt.txt';expectedPromptPath='runs/R04/prompt.txt';expectedPromptRelativePath='prompt.txt';promptSha256=$sourceHash;promptBytes=7;actorOutputTreeSha256=$outputTree;actorOutputFileCount=1;actorInventory=$actorFile;r04TraceAuditExists=$false;r04TraceAuditPath='runs/R04/trace-audit.json'}
}

function Set-NestedValue($Object, [string]$Path, $Value) {
    $parts = $Path.Split('.')
    $node = $Object
    for ($index=0; $index -lt $parts.Count - 1; $index++) {
        $part = $parts[$index]
        if ($part -match '^\d+$') { $node = $node[[int]$part] } else { $node = $node[$part] }
    }
    $last = $parts[$parts.Count - 1]
    if ($last -match '^\d+$') { $node[[int]$last] = $Value } else { $node[$last] = $Value }
}

function Invoke-SupplementalSelfTest {
    $tests = [Collections.Generic.List[object]]::new()
    $userRows = @(Get-UserWordRows @(
        [ordered]@{id='word_customer-premium';name='customer.premium?'}
        [ordered]@{id='builtin_customer';name='customer'}
    ))
    if ($userRows.Count -ne 1 -or [string]$userRows[0].name -cne 'customer.premium?') { throw 'User-word inventory helper returned a nested array or selected the wrong rows.' }
    $tests.Add([ordered]@{name='user-word inventory helper returns flat rows with exact cardinality';passed=$true;userWordCount=$userRows.Count;userWord=$userRows[0].name})
    $runtimeRows = @(Get-CanonicalRuntimeRows @(
        [ordered]@{runtime='cli';path='AgentLang.Cli.dll';bytes=1;sha256=('a' * 64)},
        [ordered]@{runtime='business';path='AgentLang.Business.dll';bytes=1;sha256=('b' * 64)}
    ))
    if ($runtimeRows.Count -ne 2 -or [string]$runtimeRows[0].runtime -cne 'business' -or [string]$runtimeRows[0].path -cne 'AgentLang.Business.dll' -or [string]$runtimeRows[1].runtime -cne 'cli' -or [string]$runtimeRows[1].path -cne 'AgentLang.Cli.dll') { throw 'Canonical runtime inventory ordering failed for ordered dictionary rows.' }
    $tests.Add([ordered]@{name='canonical runtime inventory sorts ordered-dictionary rows by pinned runtime and path';passed=$true;firstRuntime=$runtimeRows[0].runtime;lastRuntime=$runtimeRows[1].runtime})
    $task = [ordered]@{id='S07';outputs=@('Money')}
    $roundingCases = @(
        @{id='positive-one';kind='premium';balanceMinor='1';expected=[ordered]@{type='Money';value='0'}},
        @{id='negative-one';kind='premium';balanceMinor='-1';expected=[ordered]@{type='Money';value='0'}},
        @{id='positive-nine';kind='premium';balanceMinor='9';expected=[ordered]@{type='Money';value='8'}},
        @{id='raw-kind';kind='Premium';balanceMinor='9';expected=[ordered]@{type='Money';value='9'}},
        @{id='max-i64';kind='premium';balanceMinor='9223372036854775807';expected=[ordered]@{type='Money';value='8301034833169298226'}},
        @{id='min-i64';kind='premium';balanceMinor='-9223372036854775808';expected=[ordered]@{type='Money';value='-8301034833169298227'}}
    )
    foreach ($case in $roundingCases) {
        $expected = Get-S07Expected $task $case
        if ($expected.value -cne [string]$case.expected.value) { throw "Self-test arithmetic mismatch for $($case.id)." }
    }
    $positiveOneExpected = Get-S07Expected $task $roundingCases[0]
    $goodResponse = Test-S07Response (New-S07ResponseFixture $positiveOneExpected.value) $positiveOneExpected
    $wrongRoundedValue = ([Numerics.BigInteger]::Divide(([Numerics.BigInteger]1 * [Numerics.BigInteger]9) + [Numerics.BigInteger]5,[Numerics.BigInteger]10)).ToString([Globalization.CultureInfo]::InvariantCulture)
    $badResponse = Test-S07Response (New-S07ResponseFixture $wrongRoundedValue) $positiveOneExpected
    if (-not $goodResponse.passed -or $badResponse.passed -or $positiveOneExpected.value -cne '0' -or $wrongRoundedValue -cne '1') { throw "Actual structured-result comparison did not accept truncation and reject incorrect nearest rounding. good=$($goodResponse | ConvertTo-Json -Compress -Depth 20);bad=$($badResponse | ConvertTo-Json -Compress -Depth 20);expected=$($positiveOneExpected.value);wrong=$wrongRoundedValue" }
    $tests.Add([ordered]@{name='actual S07 structured-result comparison accepts correct value and rejects wrong rounding';passed=$true;correctValue=$positiveOneExpected.value;correctComparison=$goodResponse.passed;incorrectRoundedValue=$wrongRoundedValue;incorrectComparison=$badResponse.passed})
    $missingCollections = New-S07ResponseFixture $positiveOneExpected.value
    [void]$missingCollections.data.Remove('effects')
    $missingEffects = Test-S07Response $missingCollections $positiveOneExpected
    $missingCollections = New-S07ResponseFixture $positiveOneExpected.value
    [void]$missingCollections.data.Remove('console')
    $missingConsole = Test-S07Response $missingCollections $positiveOneExpected
    if ($missingEffects.passed -or $missingConsole.passed) { throw 'Structured-result comparison accepted omitted protocol collections.' }
    $tests.Add([ordered]@{name='S07 structured-result comparison rejects missing protocol collections';passed=$true;missingEffectsRejected=(-not $missingEffects.passed);missingConsoleRejected=(-not $missingConsole.passed)})
    $wrongNominal = New-S07ResponseFixture $positiveOneExpected.value
    $wrongNominal.data.structuredStack.values[0].name = 'MoneyLike'
    $wrongNominalCheck = Test-S07Response $wrongNominal $positiveOneExpected
    if ($wrongNominalCheck.passed) { throw 'Structured-result comparison accepted the wrong nominal Money scalar.' }
    $tests.Add([ordered]@{name='S07 structured-result comparison rejects the wrong nominal Money scalar';passed=$true;rejected=(-not $wrongNominalCheck.passed)})
    $tests.Add([ordered]@{name='BigInteger discount arithmetic and raw-kind ordinal behavior';passed=$true;examples=@($roundingCases | ForEach-Object { [ordered]@{id=$_.id;expected=$_.expected.value} });runtimeCalls=0})

    $base = New-SelfTestFixture
    $script:fakeRuntimeCalls = 0
    $ok = Invoke-OnlyAfterBindings $base { $script:fakeRuntimeCalls++; 'executed' }
    if ($ok -cne 'executed' -or $script:fakeRuntimeCalls -ne 1) { throw 'Positive binding control did not execute exactly once.' }
    $tests.Add([ordered]@{name='correct R03 predecessor binds before the guarded runtime call';passed=$true;runtimeCalls=$script:fakeRuntimeCalls})

    $mutations = @(
        [ordered]@{name='reject R04 retention fallback enabled';path='pin.retentionFallback';value=$true},
        [ordered]@{name='reject R03 passed=false';path='pin.previousAcceptance.passed';value=$false},
        [ordered]@{name='reject R03 carryForward=false';path='pin.previousAcceptance.carryForward';value=$false},
        [ordered]@{name='reject R03 acceptance passed=false';path='priorAcceptance.passed';value=$false},
        [ordered]@{name='reject R03 acceptance metadataPassed=false';path='priorAcceptance.metadataPassed';value=$false},
        [ordered]@{name='reject R03 acceptance behaviorPassed=false';path='priorAcceptance.behaviorPassed';value=$false},
        [ordered]@{name='reject R03 acceptance checkFrozenPin=false';path='priorAcceptance.checkFrozenPin';value=$false},
        [ordered]@{name='reject R03 acceptance resultKind change';path='priorAcceptance.resultKind';value='supplemental-post-hoc-behavior'},
        [ordered]@{name='reject wrong predecessor hash';path='pin.previousAcceptance.sha256';value=('0' * 64)},
        [ordered]@{name='reject wrong predecessor identity';path='priorAcceptance.runId';value='R11'},
        [ordered]@{name='reject wrong carried project tree';path='state.project.inventorySha256';value=('0' * 64)},
        [ordered]@{name='reject wrong R03 actor tree';path='priorActorTreeSha256';value=('0' * 64)},
        [ordered]@{name='reject wrong R04 actor output tree';path='actorOutputTreeSha256';value=('0' * 64)},
        [ordered]@{name='reject wrong R04 acceptance hash';path='originalAcceptanceSha256';value=('0' * 64)},
        [ordered]@{name='reject changed prelaunch provenance hash';path='prelaunchSha256';value=('0' * 64)},
        [ordered]@{name='reject changed global freeze provenance hash';path='pin.globalFreezeSha256';value=('0' * 64)},
        [ordered]@{name='reject changed source snapshot provenance';path='globalFreeze.sourceArtifacts.0.sha256';value=('0' * 64)},
        [ordered]@{name='reject changed R03 trace audit hash';path='priorTraceSha256';value=('0' * 64)}
    )
    foreach ($mutation in $mutations) {
        $fixture = ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $base -Depth 100 -Compress) -AsHashtable -Depth 100
        Set-NestedValue $fixture ([string]$mutation.path) $mutation.value
        $script:fakeRuntimeCalls = 0
        $callsBefore = $script:fakeRuntimeCalls
        $rejected = $false
        try { [void](Invoke-OnlyAfterBindings $fixture { $script:fakeRuntimeCalls++; 'should-not-run' }) } catch { $rejected = $true }
        if (-not $rejected -or $script:fakeRuntimeCalls -ne $callsBefore) { throw "Negative binding control did not reject before runtime: $($mutation.name)" }
        $tests.Add([ordered]@{name=$mutation.name;passed=$true;guardedActionCallsBefore=$callsBefore;guardedActionCallsAfter=$script:fakeRuntimeCalls;actualCliCalls=0})
    }
    return [ordered]@{passed=$true;tests=$tests.ToArray();actualCliCalls=0}
}

function Add-ProtectedInput([string]$Path, [string]$Label) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    Assert-NoReparseAncestors $fullPath $script:repo
    $script:protectedInputs.Add([ordered]@{path=$fullPath;label=$Label;sha256=(Get-Sha256 $fullPath)})
}

function Assert-FrozenSourceInventory($Pin, $GlobalFreeze, [string]$HeadRevision) {
    $pinRows = @(Get-CanonicalSourceRows @($Pin.sourceArtifacts))
    $globalRows = @(Get-CanonicalSourceRows @($GlobalFreeze.sourceArtifacts))
    Require-Check 'exactly 23 frozen source artifacts are present in both records' ($pinRows.Count -eq [int]$script:expected.sourceArtifactCount -and $globalRows.Count -eq [int]$script:expected.sourceArtifactCount)
    Require-Check 'R04 prelaunch and global freeze source artifact rows agree exactly' ((ConvertTo-CanonicalJson $pinRows) -ceq (ConvertTo-CanonicalJson $globalRows))
    foreach ($row in $pinRows) {
        $artifactPath = [string]$row.path
        $sourcePath = [string]$row.sourcePath
        $artifact = Resolve-RepoRelative $artifactPath
        $source = Resolve-RepoRelative $sourcePath
        $artifactHash = Get-Sha256 $artifact
        $sourceHash = Get-Sha256 $source
        $sourceCommitHash = Get-GitBlobSha256 $HeadRevision $sourcePath
        Require-Check "source artifact snapshot and current source match $sourcePath" ($artifactHash -ceq $row.sha256 -and $sourceHash -ceq $row.sha256 -and $sourceCommitHash -ceq $row.sourceGitBlobSha256 -and $row.gitBlobSha256 -ceq $row.sha256 -and $row.sourceGitBlobSha256 -ceq $row.sha256 -and (Get-Item -LiteralPath $artifact).Length -eq $row.bytes -and (Get-Item -LiteralPath $source).Length -eq $row.bytes) @{path=$sourcePath;artifactHash=$artifactHash;currentHash=$sourceHash;commitHash=$sourceCommitHash;expected=$row.sha256}
        Add-ProtectedInput $artifact "source snapshot $sourcePath"
        Add-ProtectedInput $source "frozen source $sourcePath"
    }
}

function Assert-FrozenRuntimeInventory($Pin, $GlobalFreeze, $State) {
    $pinRows = @(Get-CanonicalRuntimeRows @($Pin.runtimeFiles))
    $globalRows = @(Get-CanonicalRuntimeRows @($GlobalFreeze.runtime.files))
    Require-Check 'exactly 26 runtime artifacts are present in both frozen inventories' ($pinRows.Count -eq [int]$script:expected.runtimeFileCount -and $globalRows.Count -eq [int]$script:expected.runtimeFileCount)
    Require-Check 'R04 prelaunch and global runtime inventories agree exactly' ((ConvertTo-CanonicalJson $pinRows) -ceq (ConvertTo-CanonicalJson $globalRows))
    $cliRow = @($pinRows | Where-Object { $_.runtime -ceq 'cli' -and $_.path -ceq [IO.Path]::GetFileName([string]$Pin.runtimeRoots.cliDllPath) })
    $businessRow = @($pinRows | Where-Object { $_.runtime -ceq 'business' -and $_.path -ceq [IO.Path]::GetFileName([string]$Pin.runtimeRoots.businessDllPath) })
    Require-Check 'runtime roots and build revision match global freeze and starting state' ([string]$Pin.runtimeRoots.cliDllPath -ceq [string]$GlobalFreeze.runtime.cliDllPath -and [string]$Pin.runtimeRoots.cliDirectoryPath -ceq [string]$GlobalFreeze.runtime.cliDirectoryPath -and [string]$Pin.runtimeRoots.businessDllPath -ceq [string]$GlobalFreeze.runtime.businessDllPath -and [string]$Pin.runtimeRoots.businessDirectoryPath -ceq [string]$GlobalFreeze.runtime.businessDirectoryPath -and [string]$Pin.runtimeBuildSourceRevision -ceq [string]$GlobalFreeze.runtime.buildSourceRevision -and [string]$State.runtime.cliPath -ceq [string]$Pin.runtimeRoots.cliDllPath -and $cliRow.Count -eq 1 -and [string]$State.runtime.cliSha256 -ceq [string]$cliRow[0].sha256 -and [string]$State.runtime.businessPath -ceq [string]$Pin.runtimeRoots.businessDllPath -and $businessRow.Count -eq 1 -and [string]$State.runtime.businessSha256 -ceq [string]$businessRow[0].sha256) @{runtimeRoots=$Pin.runtimeRoots;buildSourceRevision=$Pin.runtimeBuildSourceRevision;cliSha256=$(if ($cliRow.Count -eq 1) {$cliRow[0].sha256} else {$null});businessSha256=$(if ($businessRow.Count -eq 1) {$businessRow[0].sha256} else {$null})}
    $actualRows = [Collections.Generic.List[object]]::new()
    foreach ($runtimeKind in @('cli','business')) {
        $rootRelative = if ($runtimeKind -ceq 'cli') { [string]$Pin.runtimeRoots.cliDirectoryPath } else { [string]$Pin.runtimeRoots.businessDirectoryPath }
        $root = Resolve-RepoRelative $rootRelative
        foreach ($file in @(Get-SafeFileInventory $root -IncludeBuildArtifacts)) {
            $actualRows.Add([ordered]@{runtime=$runtimeKind;path=[string]$file.path;bytes=[long]$file.bytes;sha256=[string]$file.sha256})
        }
        Add-ProtectedInput (Resolve-RepoRelative $(if ($runtimeKind -ceq 'cli') { [string]$Pin.runtimeRoots.cliDllPath } else { [string]$Pin.runtimeRoots.businessDllPath })) "$runtimeKind pinned runtime assembly"
    }
    $actualCanonical = @(Get-CanonicalRuntimeRows @($actualRows.ToArray()))
    Require-Check 'all pinned CLI and Business runtime files still match their original hashes and sizes' ((ConvertTo-CanonicalJson $actualCanonical) -ceq (ConvertTo-CanonicalJson $pinRows)) @{expectedCount=$pinRows.Count;actualCount=$actualCanonical.Count}
    foreach ($row in $pinRows) {
        $rootRelative = if ($row.runtime -ceq 'cli') { [string]$Pin.runtimeRoots.cliDirectoryPath } else { [string]$Pin.runtimeRoots.businessDirectoryPath }
        $path = [IO.Path]::GetFullPath((Join-Path (Resolve-RepoRelative $rootRelative) $row.path))
        Add-ProtectedInput $path "pinned $($row.runtime) runtime file $($row.path)"
    }
    return [IO.Path]::GetFullPath((Resolve-RepoRelative ([string]$Pin.runtimeRoots.cliDllPath)))
}

function Assert-OracleInputs($Pin, $GlobalFreeze) {
    $oraclePath = Resolve-RepoRelative ([string]$Pin.oraclePath)
    $designPath = Resolve-RepoRelative ([string]$Pin.designPath)
    $primerPath = Resolve-RepoRelative ([string]$Pin.primerPath)
    $taskPath = Resolve-RepoRelative ([string]$Pin.taskPath)
    Require-Check 'current oracle, design, primer, and public S07 task match their prelaunch hashes' ((Get-Sha256 $oraclePath) -ceq [string]$Pin.oracleSha256 -and (Get-Sha256 $designPath) -ceq [string]$Pin.designSha256 -and (Get-Sha256 $primerPath) -ceq [string]$Pin.primerSha256 -and (Get-Sha256 $taskPath) -ceq [string]$Pin.taskSha256)
    $globalTaskRows = @($GlobalFreeze.tasks | Where-Object { [string]$_.path -ceq [string]$Pin.taskPath })
    Require-Check 'global oracle, design, primer, and exact-path S07 task records match the per-run pins' ([string]$GlobalFreeze.oracle.path -ceq [string]$Pin.oraclePath -and [string]$GlobalFreeze.oracle.sha256 -ceq [string]$Pin.oracleSha256 -and [string]$GlobalFreeze.design.path -ceq [string]$Pin.designPath -and [string]$GlobalFreeze.design.sha256 -ceq [string]$Pin.designSha256 -and [string]$GlobalFreeze.primer.path -ceq [string]$Pin.primerPath -and [string]$GlobalFreeze.primer.sha256 -ceq [string]$Pin.primerSha256 -and $globalTaskRows.Count -eq 1 -and [string]$globalTaskRows[0].sha256 -ceq [string]$Pin.taskSha256) @{globalS07TaskMatches=$globalTaskRows.Count;path=$Pin.taskPath;sha256=$(if ($globalTaskRows.Count -eq 1) {$globalTaskRows[0].sha256} else {$null})}
    foreach ($path in @($oraclePath,$designPath,$primerPath,$taskPath)) { Add-ProtectedInput $path 'pinned study oracle/design/primer/task' }
    $json = [IO.File]::ReadAllText($oraclePath)
    $params = @{InputObject=$json;AsHashtable=$true;Depth=100}
    if ((Get-Command ConvertFrom-Json).Parameters.ContainsKey('DateKind')) { $params.DateKind = 'String' }
    $script:oracle = ConvertFrom-Json @params
    $document = [System.Text.Json.JsonDocument]::Parse($json)
    try {
        $createdElement = $document.RootElement.GetProperty('defaults').GetProperty('createdAt')
        Require-Check 'pinned S07 default Instant is an exact JSON string' ($createdElement.ValueKind -eq [System.Text.Json.JsonValueKind]::String)
        $script:createdAt = $createdElement.GetString()
    } finally { $document.Dispose() }
    Require-Check 'pinned acceptance corpus identity and sequence are unchanged' ([int]$script:oracle.schemaVersion -eq 1 -and [string]$script:oracle.studyId -ceq $script:expected.studyId -and (@($script:oracle.sequence) -join ',') -ceq 'S01,S07') @{studyId=$script:oracle.studyId;sequence=@($script:oracle.sequence)}
    $tasks = @($script:oracle.tasks | Where-Object { [string]$_.id -ceq 'S07' })
    Require-Check 'pinned oracle contains one S07 task with the strong Customer-to-Money signature' ($tasks.Count -eq 1 -and [string]$tasks[0].symbol -ceq 'customer.discounted-balance' -and (@($tasks[0].inputs) -join ',') -ceq 'Customer' -and @($tasks[0].outputs).Count -eq 1 -and [string]$tasks[0].outputs[0] -ceq 'Money' -and @($tasks[0].cases).Count -eq 54) @{taskCount=$tasks.Count;symbol=$(if ($tasks.Count -eq 1) {$tasks[0].symbol} else {$null});caseCount=$(if ($tasks.Count -eq 1) {@($tasks[0].cases).Count} else {0})}
    $created = $false
    try {
        $parsed = [DateTimeOffset]::ParseExact([string]$script:createdAt,'O',[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind)
        $created = $parsed.Offset -eq [TimeSpan]::Zero -and $parsed.ToString('O',[Globalization.CultureInfo]::InvariantCulture) -ceq [string]$script:createdAt
    } catch { $created = $false }
    Require-Check 'pinned S07 default Instant is canonical UTC round-trip text' $created $script:createdAt
    $script:targetOracle = $tasks[0]
    $all = [Collections.Generic.List[object]]::new()
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($case in $script:targetOracle.cases) {
        if ([string]::IsNullOrWhiteSpace([string]$case.id) -or -not $ids.Add([string]$case.id)) { throw 'S07 oracle contains an empty or duplicate case id.' }
        $all.Add((Get-S07Expected $script:targetOracle $case))
    }
    Require-Check 'all 54 S07 oracle expectations independently recompute with BigInteger' ($all.Count -eq 54)
    $script:independentCases = $all.ToArray()
}

function Assert-ProtectedInputsUnchanged {
    $changed = [Collections.Generic.List[object]]::new()
    foreach ($item in $script:protectedInputs) {
        try {
            $actual = Get-Sha256 ([string]$item.path)
            if ($actual -cne [string]$item.sha256) { $changed.Add([ordered]@{path=$item.path;label=$item.label;expected=$item.sha256;actual=$actual}) }
        } catch { $changed.Add([ordered]@{path=$item.path;label=$item.label;error=$_.Exception.Message}) }
    }
    Require-Check 'all saved acceptance, source, oracle, global, and runtime inputs stayed byte-identical' ($changed.Count -eq 0) @($changed)
}

function Invoke-Scoring {
    $originalAcceptancePath = Join-Path $script:run04 'acceptance.json'
    $prelaunchPath = Join-Path $script:run04 'prelaunch.json'
    $startingStatePath = Join-Path $script:run04 'starting-state.json'
    $globalFreezePath = Join-Path $script:repo 'experiments/AgentLang.SubagentTrials/business-policy-retention-003/artifacts/global-freeze.json'
    $priorAcceptancePath = Join-Path $script:run03 'acceptance.json'
    $priorTracePath = Join-Path $script:run03 'trace-audit.json'
    $actorPath = Join-Path $script:study 'actors/R04'
    $priorActorPath = Join-Path $script:study 'actors/R03'
    $startingProjectPath = Join-Path $script:run04 'starting-project'
    $r04TraceAuditPath = Join-Path $script:run04 'trace-audit.json'
    $promptPath = Join-Path $script:run04 'prompt.txt'

    foreach ($path in @($originalAcceptancePath,$prelaunchPath,$startingStatePath,$globalFreezePath,$priorAcceptancePath,$priorTracePath,$promptPath)) { Assert-NoReparseAncestors $path $script:repo }
    foreach ($directory in @($actorPath,$priorActorPath,$startingProjectPath)) { Assert-NoReparseAncestors $directory $script:repo; [void](Get-SafeFileInventory $directory) }
    $script:originalAcceptanceHashBefore = Get-Sha256 $originalAcceptancePath
    $prelaunchHash = Get-Sha256 $prelaunchPath
    $startingStateHash = Get-Sha256 $startingStatePath
    $globalFreezeHash = Get-Sha256 $globalFreezePath
    $priorAcceptanceHash = Get-Sha256 $priorAcceptancePath
    $priorTraceHash = Get-Sha256 $priorTracePath
    $promptHash = Get-Sha256 $promptPath
    $promptBytes = (Get-Item -LiteralPath $promptPath).Length
    $original = Get-Content -LiteralPath $originalAcceptancePath -Raw | ConvertFrom-Json -AsHashtable -Depth 100
    $pin = Get-Content -LiteralPath $prelaunchPath -Raw | ConvertFrom-Json -AsHashtable -Depth 100
    $state = Get-Content -LiteralPath $startingStatePath -Raw | ConvertFrom-Json -AsHashtable -Depth 100
    $globalFreeze = Get-Content -LiteralPath $globalFreezePath -Raw | ConvertFrom-Json -AsHashtable -Depth 100
    $priorAcceptance = Get-Content -LiteralPath $priorAcceptancePath -Raw | ConvertFrom-Json -AsHashtable -Depth 100
    $priorTrace = Get-Content -LiteralPath $priorTracePath -Raw | ConvertFrom-Json -AsHashtable -Depth 100

    $actorInventory = @(Get-SafeFileInventory $actorPath)
    $actorTree = Get-TreeSha256 $actorPath
    $priorActorTree = Get-TreeSha256 $priorActorPath
    $startingInventory = @(Get-SafeFileInventory $startingProjectPath)
    $startingTree = Get-TreeSha256 $startingProjectPath
    $script:actorBefore = $actorInventory
    $script:actorHashBefore = $actorTree
    $script:actorOutputTreeSha256 = $actorTree

    $context = [ordered]@{
        expected=$script:expected;pin=$pin;state=$state;globalFreeze=$globalFreeze;originalAcceptance=$original
        originalAcceptanceSha256=$script:originalAcceptanceHashBefore;prelaunchSha256=$prelaunchHash;startingStateSha256=$startingStateHash;globalFreezeSha256=$globalFreezeHash
        oracleSha256=(Get-Sha256 (Join-Path $script:repo 'experiments/AgentLang.SubagentTrials/business-policy-retention-003/acceptance.json'))
        headRevision=(Get-GitHead);priorAcceptance=$priorAcceptance;priorAcceptanceSha256=$priorAcceptanceHash;priorTrace=$priorTrace;priorTraceSha256=$priorTraceHash
        promptPath=[IO.Path]::GetFullPath($promptPath);expectedPromptPath=[IO.Path]::GetFullPath($promptPath);expectedPromptRelativePath='prompt.txt';promptSha256=$promptHash;promptBytes=$promptBytes
        priorActorTreeSha256=$priorActorTree;startingProjectTreeSha256=$startingTree;prelaunchStartingTreeSha256=$pin.startingProjectInventorySha256;prelaunchActorTreeSha256=$pin.actorProjectInventorySha256
        startingProjectInventory=$startingInventory;expectedPriorProjectPath=[IO.Path]::GetFullPath($priorActorPath);expectedActorProjectPath=[IO.Path]::GetFullPath($actorPath);expectedStartProjectPath=[IO.Path]::GetFullPath($startingProjectPath)
        actorOutputTreeSha256=$actorTree;actorOutputFileCount=$actorInventory.Count;actorInventory=$actorInventory;r04TraceAuditExists=(Test-Path -LiteralPath $r04TraceAuditPath -PathType Leaf);r04TraceAuditPath=$r04TraceAuditPath
    }
    $script:originalAcceptancePath = $originalAcceptancePath
    $script:actorPath = $actorPath
    $script:startingProjectPath = $startingProjectPath
    $script:prelaunchPath = $prelaunchPath
    $script:startingStatePath = $startingStatePath
    $script:globalFreezePath = $globalFreezePath
    $script:priorAcceptancePath = $priorAcceptancePath
    $script:priorTracePath = $priorTracePath
    $script:priorActorPath = $priorActorPath
    $script:promptPath = $promptPath
    $script:promptHashBefore = $promptHash
    $script:currentHeadRevision = $context.headRevision
    $script:pin = $pin
    $script:startingTreeHashBefore = $startingTree
    $script:priorActorTreeHashBefore = $priorActorTree
    $script:globalFreezeHashBefore = $globalFreezeHash
    $script:prelaunchHashBefore = $prelaunchHash
    $script:startingStateHashBefore = $startingStateHash
    $script:priorAcceptanceHashBefore = $priorAcceptanceHash
    $script:priorTraceHashBefore = $priorTraceHash

    # These checks all run before any CLI process. The canonical records are inputs only.
    Invoke-OnlyAfterBindings $context { $true } | Out-Null
    Require-Check 'canonical failed output records a stable actor inventory' ($original.project.inventorySha256 -ceq $actorTree -and (Test-InventoryEqual @($original.project.filesAfter) $actorInventory)) @{actorTree=$actorTree;canonicalTree=$original.project.inventorySha256;actorFiles=$actorInventory.Count}
    Add-ProtectedInput $originalAcceptancePath 'original failed R04 acceptance'
    Add-ProtectedInput $prelaunchPath 'R04 prelaunch pin'
    Add-ProtectedInput $startingStatePath 'R04 starting state'
    Add-ProtectedInput $globalFreezePath 'global freeze snapshot'
    Add-ProtectedInput $priorAcceptancePath 'accepted R03 predecessor acceptance'
    Add-ProtectedInput $priorTracePath 'accepted R03 predecessor trace audit'
    Add-ProtectedInput $promptPath 'pinned original R04 prompt'
    foreach ($row in $startingInventory) {
        $startingFile = Join-Path $startingProjectPath ([string]$row.path.Replace('/', [IO.Path]::DirectorySeparatorChar))
        Add-ProtectedInput $startingFile "R04 starting-project file $($row.path)"
    }
    Add-ProtectedInput (Resolve-RepoRelative ([string]$pin.oraclePath)) 'pinned S07 oracle'
    Add-ProtectedInput (Resolve-RepoRelative ([string]$pin.designPath)) 'pinned design'
    Add-ProtectedInput (Resolve-RepoRelative ([string]$pin.primerPath)) 'pinned language primer'
    Add-ProtectedInput (Resolve-RepoRelative ([string]$pin.taskPath)) 'pinned public S07 task'
    Assert-FrozenSourceInventory $pin $globalFreeze ([string]$pin.sourceRevision)
    $script:resolvedCli = Assert-FrozenRuntimeInventory $pin $globalFreeze $state
    Assert-OracleInputs $pin $globalFreeze
    Require-Check 'pinned CLI DLL exists at its frozen Release path' (Test-Path -LiteralPath $script:resolvedCli -PathType Leaf) $script:resolvedCli
    $script:bindingPassed = $true

    $script:scratchId = [Guid]::NewGuid().ToString('N')
    $script:scoringScratch = Join-Path $script:scratchRoot $script:scratchId
    $script:startingScratch = Join-Path $script:scoringScratch 'starting-project'
    $actorScratch = Join-Path $script:scoringScratch 'actor-project'
    $script:scratchActorProject = $actorScratch
    Assert-NoReparseAncestors $script:scoringScratch $script:supplemental
    [IO.Directory]::CreateDirectory($actorScratch) | Out-Null
    Copy-ProjectToScratch $actorPath $actorScratch
    Copy-ProjectToScratch $startingProjectPath $script:startingScratch
    Require-Check 'scratch actor and start copies match their source inventories before CLI execution' ((Get-TreeSha256 $actorScratch) -ceq $actorTree -and (Get-TreeSha256 $script:startingScratch) -ceq $startingTree) @{actor=$actorTree;starting=$startingTree;scratch=$script:scoringScratch}
    Require-Check 'supplemental work runs only on separate non-reparse scratch trees' ((Test-Within $actorScratch $script:scratchRoot) -and (Test-Within $script:startingScratch $script:scratchRoot)) @{actorScratch=$actorScratch;startingScratch=$script:startingScratch}

    [void](Invoke-MetadataChecks $actorScratch $script:startingScratch $script:targetOracle)
    Invoke-OracleCases $actorScratch
    $scratchAfter = Get-SafeFileInventory $actorScratch
    $script:supplementalScratchAfter = $scratchAfter
    $script:metadataPassed = @($script:checks | Where-Object { -not $_.passed -and $_.name -notmatch 'oracle case|54 unchanged S07' }).Count -eq 0
    $script:behaviorPassed = $script:caseResults.Count -eq 54 -and @($script:caseResults | Where-Object { -not $_.passed -or -not $_.executed }).Count -eq 0
}

if ($SelfTest) {
    try { $selfTestResult = Invoke-SupplementalSelfTest }
    catch { Write-Output $_.InvocationInfo.PositionMessage; Write-Output $_.ScriptStackTrace; Write-Output $_.Exception.StackTrace; Write-Output (($_ | Format-List * -Force | Out-String)); exit 1 }
    Write-Output (ConvertTo-Json -InputObject $selfTestResult -Depth 40 -Compress)
    if (-not $selfTestResult.passed) { exit 1 }
    exit 0
}

$script:outputPath = $null
try {
    [IO.Directory]::CreateDirectory($script:supplemental) | Out-Null
    [IO.Directory]::CreateDirectory($script:scratchRoot) | Out-Null
    [IO.Directory]::CreateDirectory($script:evidenceRoot) | Out-Null
    Assert-NoReparseAncestors $script:evidenceRoot $script:study
    $script:outputPath = Join-Path $script:evidenceRoot ("R04-retention-output-supplement-$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ'))-$([Guid]::NewGuid().ToString('N')).json")
    if (Test-Path -LiteralPath $script:outputPath) { throw "Refusing to overwrite supplemental evidence: $script:outputPath" }
    Invoke-Scoring
} catch {
    $script:failure = $_.Exception.Message
    Add-Check 'supplemental scorer completed without binding or execution errors' $false @{message=$_.Exception.Message;stack=$_.ScriptStackTrace}
} finally {
    try {
        if ($null -ne $script:actorPath -and (Test-Path -LiteralPath $script:actorPath -PathType Container)) {
            $script:actorAfter = @(Get-SafeFileInventory $script:actorPath)
            $script:actorHashAfter = Get-TreeSha256 $script:actorPath
            $actorPreserved = $script:actorHashBefore -ceq $script:actorHashAfter -and (Test-InventoryEqual $script:actorBefore $script:actorAfter)
            Add-Check 'R04 actor output remains byte-identical after supplemental scoring' $actorPreserved @{before=$script:actorHashBefore;after=$script:actorHashAfter;filesBefore=$script:actorBefore.Count;filesAfter=$script:actorAfter.Count}
        }
    } catch { Add-Check 'R04 actor output preservation could be confirmed' $false $_.Exception.Message }
    try {
        if ($null -ne $script:startingProjectPath -and (Test-Path -LiteralPath $script:startingProjectPath -PathType Container)) {
            $script:startingTreeHashAfter = Get-TreeSha256 $script:startingProjectPath
            Add-Check 'R04 carried-forward starting tree remains byte-identical' ($script:startingTreeHashBefore -ceq $script:startingTreeHashAfter -and $script:startingTreeHashAfter -ceq $script:expected.priorTreeSha256) @{before=$script:startingTreeHashBefore;after=$script:startingTreeHashAfter}
        }
        if ($null -ne $script:priorActorPath -and (Test-Path -LiteralPath $script:priorActorPath -PathType Container)) {
            $script:priorActorTreeHashAfter = Get-TreeSha256 $script:priorActorPath
            Add-Check 'accepted R03 predecessor actor tree remains byte-identical' ($script:priorActorTreeHashBefore -ceq $script:priorActorTreeHashAfter -and $script:priorActorTreeHashAfter -ceq $script:expected.priorTreeSha256) @{before=$script:priorActorTreeHashBefore;after=$script:priorActorTreeHashAfter}
        }
        if ($null -ne $script:promptPath -and (Test-Path -LiteralPath $script:promptPath -PathType Leaf)) { Add-Check 'original R04 prompt remains byte-identical' ((Get-Sha256 $script:promptPath) -ceq $script:promptHashBefore) $script:promptHashBefore }
    } catch { Add-Check 'canonical carried-forward files and predecessor actor remained unchanged' $false $_.Exception.Message }
    try {
        if ($null -ne $script:originalAcceptancePath -and (Test-Path -LiteralPath $script:originalAcceptancePath -PathType Leaf)) {
            $script:originalAcceptanceHashAfter = Get-Sha256 $script:originalAcceptancePath
            Add-Check 'original canonical R04 acceptance remains byte-identical' ($script:originalAcceptanceHashBefore -ceq $script:originalAcceptanceHashAfter) @{before=$script:originalAcceptanceHashBefore;after=$script:originalAcceptanceHashAfter}
        }
        if ($null -ne $script:prelaunchPath -and (Test-Path -LiteralPath $script:prelaunchPath -PathType Leaf)) { Add-Check 'R04 prelaunch pin remains byte-identical' ((Get-Sha256 $script:prelaunchPath) -ceq $script:prelaunchHashBefore) $script:prelaunchHashBefore }
        if ($null -ne $script:startingStatePath -and (Test-Path -LiteralPath $script:startingStatePath -PathType Leaf)) { Add-Check 'R04 starting state remains byte-identical' ((Get-Sha256 $script:startingStatePath) -ceq $script:startingStateHashBefore) $script:startingStateHashBefore }
        if ($null -ne $script:globalFreezePath -and (Test-Path -LiteralPath $script:globalFreezePath -PathType Leaf)) { Add-Check 'global freeze snapshot remains byte-identical' ((Get-Sha256 $script:globalFreezePath) -ceq $script:globalFreezeHashBefore) $script:globalFreezeHashBefore }
        if ($null -ne $script:priorAcceptancePath -and (Test-Path -LiteralPath $script:priorAcceptancePath -PathType Leaf)) { Add-Check 'R03 accepted predecessor remains byte-identical' ((Get-Sha256 $script:priorAcceptancePath) -ceq $script:priorAcceptanceHashBefore) $script:priorAcceptanceHashBefore }
        if ($null -ne $script:priorTracePath -and (Test-Path -LiteralPath $script:priorTracePath -PathType Leaf)) { Add-Check 'R03 passing trace audit remains byte-identical' ((Get-Sha256 $script:priorTracePath) -ceq $script:priorTraceHashBefore) $script:priorTraceHashBefore }
        if ($null -ne $script:r04TraceAuditPath) { Add-Check 'supplemental scorer did not create a canonical R04 trace audit' (-not (Test-Path -LiteralPath $script:r04TraceAuditPath -PathType Leaf)) $script:r04TraceAuditPath }
    } catch { Add-Check 'protected canonical inputs remained unchanged' $false $_.Exception.Message }
    try { Assert-ProtectedInputsUnchanged } catch { if ($null -eq $script:failure) { $script:failure = $_.Exception.Message } }

    $allMetadataChecks = @($script:checks | Where-Object { $_.name -notmatch 'oracle case|54 unchanged S07' })
    $script:metadataPassed = $script:bindingPassed -and $allMetadataChecks.Count -gt 0 -and @($allMetadataChecks | Where-Object { -not $_.passed }).Count -eq 0
    $script:behaviorPassed = $script:caseResults.Count -eq 54 -and @($script:caseResults | Where-Object { -not $_.passed -or -not $_.executed }).Count -eq 0
    $script:passed = $script:metadataPassed -and $script:behaviorPassed -and $script:failure -eq $null
    try {
        $scorerHash = Get-Sha256 $PSCommandPath
        $evidence = [ordered]@{
            schemaVersion=1
            studyId='business-policy-retention-003'
            arm='retained';block='B1';taskId='S07';runId='R04';sequenceIndex=2
            resultKind='supplemental-post-hoc-behavior'
            passed=[bool]$script:passed;metadataPassed=[bool]$script:metadataPassed;behaviorPassed=[bool]$script:behaviorPassed;bindingPassed=[bool]$script:bindingPassed
            failure=$script:failure;startedUtc=$script:startedUtc;completedUtc=[DateTime]::UtcNow.ToString('O')
            scorerPath='scripts/Score-RetentionOutputSupplement.ps1';scorerSha256=$scorerHash
            originalCanonical=[ordered]@{path='.agentlang/business-policy-retention-003/runs/R04/acceptance.json';sha256Before=$script:originalAcceptanceHashBefore;sha256After=$script:originalAcceptanceHashAfter;passed=$false;resultKind='unfrozen-preflight-or-control';traceAuditPath='.agentlang/business-policy-retention-003/runs/R04/trace-audit.json';traceAuditExists=[bool](Test-Path -LiteralPath (Join-Path $script:run04 'trace-audit.json'))}
            prelaunch=[ordered]@{path='.agentlang/business-policy-retention-003/runs/R04/prelaunch.json';sha256=$script:prelaunchHashBefore;globalFreezeSha256=$script:globalFreezeHashBefore;startingStateSha256=$script:startingStateHashBefore;previousAcceptanceSha256=$script:priorAcceptanceHashBefore;previousTraceAuditSha256=$script:priorTraceHashBefore;promptPath='.agentlang/business-policy-retention-003/runs/R04/prompt.txt';promptSha256=$script:promptHashBefore;frozenSourceRevision=$(if ($null -ne $script:pin) {$script:pin.sourceRevision} else {$null});checkoutHeadAtScoring=$script:currentHeadRevision;priorActorTreeAfter=$script:priorActorTreeHashAfter;startingTreeAfter=$script:startingTreeHashAfter}
            project=[ordered]@{actorPath='.agentlang/business-policy-retention-003/actors/R04';inventorySha256Before=$script:actorHashBefore;inventorySha256After=$script:actorHashAfter;filesBefore=$script:actorBefore;filesAfter=$script:actorAfter;startingTreeSha256=$(if ($null -ne $script:startingProjectPath) { Get-TreeSha256 $script:startingProjectPath } else { $null })}
            scratch=[ordered]@{root=$script:scoringScratch;actorProject=$script:scratchActorProject;startingProject=$script:startingScratch;actorFilesAfterCli=$script:supplementalScratchAfter;retainedForReview=$true}
            oracle=[ordered]@{path='experiments/AgentLang.SubagentTrials/business-policy-retention-003/acceptance.json';sha256=$(if ($null -ne $script:oracle) { $script:expected.oracleSha256 } else { $null });symbol=$(if ($null -ne $script:targetOracle) { $script:targetOracle.symbol } else { $null });casesExpected=54;casesExecuted=@($script:caseResults | Where-Object { $_.executed }).Count;independentResults=$script:independentCases;caseResults=$script:caseResults.ToArray();protocol='Independent BigInteger arithmetic; raw Kind uses ordinal exact comparison; Money is a nominal scalar with Int payload; discount uses integer division truncating toward zero.'}
            runtime=[ordered]@{calls=$script:runtimeCalls.ToArray();callCount=$script:runtimeCalls.Count;cliDll=$script:resolvedCli;cliSha256=$(if ($script:resolvedCli -and (Test-Path -LiteralPath $script:resolvedCli)) { Get-Sha256 $script:resolvedCli } else { $null });metadata=$script:metadataResults.ToArray()}
            checks=$script:checks.ToArray()
            limits=@(
                'This result is supplemental post-hoc behavior evidence. It does not change or replace canonical R04 acceptance.json and cannot classify R04 as canonically accepted.',
                'The original verifier stopped before behavior at the undefined $prior reference; this scorer records its own runtime calls and oracle outcomes.',
                'No actor PTY, prompt correction, termination event, or teardown was observed or altered; any prompt correction or teardown deviations remain as recorded by the original study evidence.',
                'No trace audit was created for R04. No efficiency, token-use, or model-quality claim is made.'
            )
        }
        if ([string]::IsNullOrWhiteSpace($script:outputPath)) { throw 'Supplemental evidence path was not allocated.' }
        Assert-NoReparseAncestors $script:outputPath $script:evidenceRoot
        if (Test-Path -LiteralPath $script:outputPath) { throw "Refusing to overwrite supplemental evidence: $script:outputPath" }
        [IO.File]::WriteAllText($script:outputPath, (ConvertTo-Json -InputObject $evidence -Depth 100) + [Environment]::NewLine, $script:utf8NoBom)
        Write-Output $script:outputPath
    } catch {
        Write-Error "Could not write supplemental evidence: $($_.Exception.Message)"
    }
}

if (-not $script:passed) { exit 1 }
exit 0
