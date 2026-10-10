#requires -Version 7.0
[CmdletBinding()]
param(
    [switch]$SerialBuild,
    [switch]$RefinementsOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runId = [Guid]::NewGuid().ToString('N')
$evidenceRoot = Join-Path $repo '.agentlang/owning-mailbox-001'
$runDirectory = Join-Path $evidenceRoot "integration-run-$runId"
$reportPath = Join-Path $runDirectory 'integration-evidence.json'
$projectPath = Join-Path $repo 'experiments/AgentLang.OwningMailbox/AgentLang.OwningMailbox.fsproj'
$programPath = Join-Path $repo 'experiments/AgentLang.OwningMailbox/Program.fs'
$flowPath = Join-Path $repo 'experiments/AgentLang.OwningMailbox/owning-mailbox.flow'
$sumsFlowPath = Join-Path $repo 'experiments/AgentLang.OwningMailbox/owning-mailbox-sums.flow'
$runnerSourcePath = Join-Path $repo 'experiments/AgentLang.OwningMailbox/native_owning_mailbox.c'
$sumsRunnerSourcePath = Join-Path $repo 'experiments/AgentLang.OwningMailbox/native_owning_mailbox_sums.c'
$fixturePath = Join-Path $repo 'tests/fixtures/native-conformance/owning-mailbox.json'
$sumsFixturePath = Join-Path $repo 'tests/fixtures/native-conformance/owning-mailbox-sums.json'
$refinementsFlowPath = Join-Path $repo 'experiments/AgentLang.OwningMailbox/owning-mailbox-refinements.flow'
$refinementsRunnerSourcePath = Join-Path $repo 'experiments/AgentLang.OwningMailbox/native_owning_mailbox_refinements.c'
$refinementsFixturePath = Join-Path $repo 'tests/fixtures/native-conformance/owning-mailbox-refinements.json'
$refinementsEvidenceRoot = Join-Path $repo '.agentlang/refined-mailbox-180/runner'
$refinementsRunDirectory = Join-Path $refinementsEvidenceRoot "integration-run-$runId"
$refinementsReportPath = Join-Path $refinementsRunDirectory 'integration-evidence.json'
$nativeDirectory = Join-Path $repo 'src/AgentLang.Llvm/native'
$arenaRuntimePath = Join-Path $nativeDirectory 'arena_runtime.c'
$mailboxRuntimePath = Join-Path $nativeDirectory 'mailbox_runtime.c'
$owningStackPath = Join-Path $nativeDirectory 'owning_stack_runtime.c'
$owningStackHeaderPath = Join-Path $nativeDirectory 'owning_stack_runtime.h'
$owningBankPath = Join-Path $nativeDirectory 'owning_bank.c'
$nativeSources = @(
    $runnerSourcePath,
    $arenaRuntimePath,
    $mailboxRuntimePath,
    (Join-Path $nativeDirectory 'mailbox_runtime_windows.c'),
    $owningStackPath,
    $owningBankPath
)
$sumsNativeSources = @(
    $sumsRunnerSourcePath,
    $arenaRuntimePath,
    $mailboxRuntimePath,
    (Join-Path $nativeDirectory 'mailbox_runtime_windows.c'),
    $owningStackPath,
    $owningBankPath
)
$refinementsNativeSources = @(
    $refinementsRunnerSourcePath,
    $arenaRuntimePath,
    $mailboxRuntimePath,
    (Join-Path $nativeDirectory 'mailbox_runtime_windows.c'),
    $owningStackPath,
    $owningBankPath
)
$owningRuntimeObjectSources = @(
    $arenaRuntimePath,
    $mailboxRuntimePath,
    (Join-Path $nativeDirectory 'mailbox_runtime_windows.c'),
    $owningStackPath,
    $owningBankPath
)
$sourceInputPaths = @(
    (Join-Path $repo 'AgentLang.sln'),
    $projectPath,
    $programPath,
    $flowPath,
    $runnerSourcePath,
    $fixturePath,
    $sumsFlowPath,
    $sumsRunnerSourcePath,
    $sumsFixturePath,
    $refinementsFlowPath,
    $refinementsRunnerSourcePath,
    $refinementsFixturePath,
    (Join-Path $repo 'src/AgentLang.Llvm/AgentLang.Llvm.fsproj'),
    (Join-Path $repo 'src/AgentLang.Llvm/OwningStackAot.fs'),
    (Join-Path $repo 'src/AgentLang.Llvm/LlvmAot.fs'),
    (Join-Path $repo 'src/AgentLang.Llvm/LlvmToolchain.fs'),
    (Join-Path $nativeDirectory 'module_abi.h'),
    (Join-Path $nativeDirectory 'arena_runtime.h'),
    (Join-Path $nativeDirectory 'arena_runtime.c'),
    (Join-Path $nativeDirectory 'mailbox_runtime.h'),
    $mailboxRuntimePath,
    (Join-Path $nativeDirectory 'mailbox_runtime_windows.c'),
    (Join-Path $nativeDirectory 'owning_mailbox_abi.h'),
    (Join-Path $nativeDirectory 'owning_stack_runtime.h'),
    $owningStackPath,
    (Join-Path $nativeDirectory 'owning_bank.h'),
    $owningBankPath,
    $PSCommandPath
)
$checks = [Collections.Generic.List[object]]::new()
$processes = [Collections.Generic.List[object]]::new()
$moduleBuilds = [Collections.Generic.List[object]]::new()
$sumModuleBuilds = [Collections.Generic.List[object]]::new()
$refinementsModuleBuilds = [Collections.Generic.List[object]]::new()
$nativeBuilds = [Collections.Generic.List[object]]::new()
$sumNativeBuilds = [Collections.Generic.List[object]]::new()
$refinementsNativeBuilds = [Collections.Generic.List[object]]::new()
$nativeRuns = [Collections.Generic.List[object]]::new()
$sumNativeRuns = [Collections.Generic.List[object]]::new()
$refinementsNativeRuns = [Collections.Generic.List[object]]::new()
$errors = [Collections.Generic.List[string]]::new()
$timeoutMilliseconds = 300000
$utf8 = [Text.UTF8Encoding]::new($false)
$sourceInputBefore = @()
$refinementFirstCheck = 0
$refinementsStarted = $false
$report = [ordered]@{
    schemaVersion = 1
    kind = 'owning-native-mailbox-integration'
    runId = $runId
    startedUtc = [DateTime]::UtcNow.ToString('O')
    repository = $repo
    fixture = $fixturePath
    refinementsFixture = $refinementsFixturePath
    refinementsOnly = [bool]$RefinementsOnly
    checks = @()
}
$runtimeProfiles = @('diagnostic', 'trusted-generated')
$executionProfiles = @(
    [ordered]@{ name = 'diagnostic'; runtimeProfile = 'diagnostic'; resetProfile = 'diagnostic'; define = $null },
    [ordered]@{ name = 'fast-reset'; runtimeProfile = 'diagnostic'; resetProfile = 'fast'; define = '-DAL_MAILBOX_FAST_RESET=1' },
    [ordered]@{ name = 'trusted-generated'; runtimeProfile = 'trusted-generated'; resetProfile = 'trusted-generated'; define = '-DAL_OWNING_TRUSTED_GENERATED=1' }
)

function Add-Check([string]$Name, [bool]$Passed, $Detail = $null) {
    $script:checks.Add([ordered]@{ name = $Name; passed = $Passed; detail = $Detail })
}

function Get-Field($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    if ($Object -is [Collections.IDictionary]) {
        foreach ($key in $Object.Keys) {
            if ([string]$key -ieq $Name) { return $Object[$key] }
        }
        return $null
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Has-Field($Object, [string]$Name) {
    if ($null -eq $Object) { return $false }
    if ($Object -is [Collections.IDictionary]) {
        foreach ($key in $Object.Keys) {
            if ([string]$key -ieq $Name) { return $true }
        }
        return $false
    }
    return $null -ne $Object.PSObject.Properties[$Name]
}

function Get-Hash([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Resolve-Executable([string]$EnvironmentName, [string]$DefaultPath, [string]$CommandName) {
    $override = [Environment]::GetEnvironmentVariable($EnvironmentName)
    if (-not [string]::IsNullOrWhiteSpace($override)) {
        $candidate = [IO.Path]::GetFullPath($override)
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { throw "$EnvironmentName points to a missing executable: $candidate" }
        return $candidate
    }
    if (Test-Path -LiteralPath $DefaultPath -PathType Leaf) { return [IO.Path]::GetFullPath($DefaultPath) }
    $command = Get-Command -Name $CommandName -CommandType Application -ErrorAction Stop | Select-Object -First 1
    return [IO.Path]::GetFullPath($command.Source)
}

function Invoke-CapturedProcess([string]$Name, [string]$Executable, [string[]]$Arguments, [string]$WorkingDirectory) {
    $safeName = $Name -replace '[^a-zA-Z0-9_-]', '_'
    $stdoutPath = Join-Path $runDirectory "$safeName.stdout.txt"
    $stderrPath = Join-Path $runDirectory "$safeName.stderr.txt"
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Executable
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add([string]$argument) }
    $start.Environment['TEMP'] = $script:tempDirectory
    $start.Environment['TMP'] = $script:tempDirectory
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $timedOut = $false
    $exitCode = $null
    $stdout = ''
    $stderr = ''
    $startError = $null
    try {
        if (-not $process.Start()) { throw 'Process.Start returned false.' }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($timeoutMilliseconds)) {
            $timedOut = $true
            $process.Kill($true)
            $process.WaitForExit()
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if (-not $timedOut) { $exitCode = $process.ExitCode }
    } catch {
        $startError = $_.Exception.Message
    } finally {
        $clock.Stop()
        $process.Dispose()
        [IO.File]::WriteAllText($stdoutPath, $stdout, $utf8)
        [IO.File]::WriteAllText($stderrPath, $stderr, $utf8)
    }
    $record = [ordered]@{
        name = $Name
        executable = $Executable
        arguments = @($Arguments)
        workingDirectory = $WorkingDirectory
        exitCode = $exitCode
        timedOut = $timedOut
        startError = $startError
        elapsedMilliseconds = $clock.ElapsedMilliseconds
        stdoutPath = $stdoutPath
        stderrPath = $stderrPath
        stdout = $stdout
        stderr = $stderr
    }
    $script:processes.Add($record)
    return $record
}

function Require-ProcessSuccess($Process, [string]$Message) {
    if ($Process.timedOut -or $null -ne $Process.startError -or $Process.exitCode -ne 0) {
        throw "$Message (exit=$($Process.exitCode), timeout=$($Process.timedOut)): $($Process.stderr) $($Process.stdout)"
    }
}

function Read-JsonFile([string]$Path) {
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable -Depth 80 -ErrorAction Stop
}

function ConvertFrom-JsonText([string]$Text, [string]$Description) {
    try { return ConvertFrom-Json -InputObject $Text -AsHashtable -Depth 80 -ErrorAction Stop }
    catch { throw "$Description is not valid JSON: $($_.Exception.Message)" }
}

function Contains-StringValue($Value, [string]$Expected) {
    if ($Value -is [string]) { return $Value -ceq $Expected }
    if ($null -eq $Value) { return $false }
    if ($Value -is [Collections.IDictionary]) {
        foreach ($child in $Value.Values) {
            if (Contains-StringValue $child $Expected) { return $true }
        }
        return $false
    }
    if ($Value -is [Collections.IEnumerable]) {
        foreach ($child in $Value) {
            if (Contains-StringValue $child $Expected) { return $true }
        }
    }
    return $false
}

function Get-RootTypeId([string]$TypeName, $Fixture) {
    $ids = Get-Field (Get-Field $Fixture 'sourceDerivedTypeIds') 'typeIds'
    return [uint32](Get-Field $ids $TypeName)
}

function Get-ExpectedPayloadBytes([string]$Hex) {
    $count = [uint32]0
    for ($index = 3; $index -ge 0; $index--) {
        $byte = [Convert]::ToUInt32($Hex.Substring($index * 2, 2), 16)
        $count = ($count -shl 8) -bor $byte
    }
    return [uint32](8 + 2 * $count)
}

function Compare-Bank($Actual, [bool]$Pending, $ExpectedRoots, [string]$Label, $Fixture) {
    $okay = $null -ne $Actual -and [bool](Get-Field $Actual 'pending') -eq $Pending -and
        [int](Get-Field $Actual 'rootCount') -eq $ExpectedRoots.Count
    $roots = @(Get-Field $Actual 'roots')
    if ($roots.Count -ne $ExpectedRoots.Count) { $okay = $false }
    $offset = [uint32]0
    for ($index = 0; $index -lt [Math]::Min($roots.Count, $ExpectedRoots.Count); $index++) {
        $observed = $roots[$index]
        $expected = $ExpectedRoots[$index]
        $typeId = Get-RootTypeId ([string](Get-Field $expected 'type')) $Fixture
        $extent = [uint32](Get-Field $expected 'extentBytes')
        $hex = [string](Get-Field $expected 'serializedHex')
        $payloadBytes = Get-ExpectedPayloadBytes $hex
        $rootOkay = [uint32](Get-Field $observed 'typeId') -eq $typeId -and
            [uint32](Get-Field $observed 'offsetBytes') -eq $offset -and
            [uint32](Get-Field $observed 'extentBytes') -eq $extent -and
            [uint32](Get-Field $observed 'payloadBytes') -eq $payloadBytes -and
            [uint32](Get-Field $observed 'ownerEndBytes') -eq ($offset + $extent) -and
            [string](Get-Field $observed 'serializedHex') -ceq $hex
        Add-Check "$Label root $index matches fixture type, extent, owner range, and serialized bytes" $rootOkay $observed
        if (-not $rootOkay) { $okay = $false }
        $offset += $extent
    }
    $okay = $okay -and [uint32](Get-Field $Actual 'usedBytes') -eq $offset
    Add-Check "$Label bank metadata and used extent match the independent fixture" $okay $Actual
}

function Compare-StatsToFixture($Stats, $Expected, [string]$Label) {
    $fieldMap = [ordered]@{
        utf8InputBytes = 'utf8InputBytes'
        utf16StagingBytes = 'utf16StagingBytes'
        inputImportBytes = 'inputImportBytes'
        publicationCopyBytes = 'publicationCopyBytes'
        beginPublicationCopyBytes = 'beginPublicationCopyBytes'
        resumeRootImportBytes = 'resumeRootImportBytes'
        deepCopyBytes = 'constructionAndDuplicationBytes'
        moveBytes = 'payloadMoveBytes'
        returnedOutputDescriptors = 'returnedOutputDescriptors'
    }
    foreach ($field in $fieldMap.Keys) {
        $expectedValue = [uint64](Get-Field $Expected $fieldMap[$field])
        $actualValue = [uint64](Get-Field $Stats $field)
        Add-Check "$Label $field equals source-derived fixture total" ($actualValue -eq $expectedValue) ([ordered]@{ expected = $expectedValue; actual = $actualValue })
    }
    Add-Check "$Label reports zero payload movement and a balanced scratch lease" (
        [uint64](Get-Field $Stats 'moveBytes') -eq 0 -and
        [uint32](Get-Field $Stats 'outstandingScratchLeases') -eq 0 -and
        [uint64](Get-Field $Stats 'scratchLeaseAcquisitions') -eq [uint64](Get-Field $Stats 'scratchLeaseReturns')) $Stats
}

function Check-ResetTelemetry($Native, [string]$ExpectedProfile, [string]$Label) {
    $telemetry = Get-Field $Native 'resetTelemetry'
    $valid = (Get-Field $telemetry 'valid') -eq $true
    $profile = [string](Get-Field $telemetry 'profile')
    $scope = [string](Get-Field $telemetry 'scope')
    $payloadSemantics = [string](Get-Field $telemetry 'payloadWriteSemantics')
    $initializationPoison = [string](Get-Field $telemetry 'initializationPoisonWrites')
    $fullCapacity = [uint64](Get-Field $telemetry 'fullCapacityPayloadWriteBytesRequested')
    $livePrefix = [uint64](Get-Field $telemetry 'livePrefixPayloadWriteBytesRequested')
    $bitmapStores = [uint64](Get-Field $telemetry 'bitmapStoreOperations')
    $cursorExtent = [uint64](Get-Field $telemetry 'turnResetCursorExtentBytes')
    $shapeOkay = $null -ne $telemetry -and $valid -and $profile -ceq $ExpectedProfile -and
        $scope -ceq 'owningScratchCheckoutRelease' -and
        $payloadSemantics -ceq 'logicalRuntimeRequestedBytesNotHardwareTraffic' -and
        $initializationPoison -ceq 'excluded: scratch, retained banks, text staging'
    Add-Check "$Label reset telemetry identifies the selected profile and logical scope" $shapeOkay $telemetry
    $counterOkay = if ($ExpectedProfile -ceq 'diagnostic') {
        $fullCapacity -gt 0 -and $livePrefix -eq $cursorExtent -and $bitmapStores -gt 0
    } elseif ($ExpectedProfile -ceq 'fast') {
        $fullCapacity -eq 0 -and $livePrefix -eq $cursorExtent -and $bitmapStores -gt 0
    } else {
        $fullCapacity -eq 0 -and $livePrefix -eq 0 -and $bitmapStores -eq 0 -and $cursorExtent -gt 0
    }
    Add-Check "$Label reset counters match payload, bitmap, and cursor behavior for $ExpectedProfile" $counterOkay $telemetry
}

function Check-NativeEvidence($Native, $Fixture, [string]$Label, [string]$RuntimeProfile) {
    Add-Check "$Label native mailbox runner reports success" ((Get-Field $Native 'passed') -eq $true -and [int](Get-Field $Native 'failureCount') -eq 0)
    $named = @(Get-Field $Native 'checks')
    Add-Check "$Label native runner emits named passing assertions" ($named.Count -ge 40 -and @($named | Where-Object { (Get-Field $_ 'passed') -ne $true }).Count -eq 0) ([ordered]@{ assertionCount = $named.Count })
    $checkNames = @($named | ForEach-Object { [string](Get-Field $_ 'name') })
    $requiredNames = @(
        'invalid UTF-8 overlong sequence', 'invalid UTF-8 truncated sequence',
        'invalid UTF-8 surrogate scalar', 'invalid UTF-8 above-U+10FFFF scalar',
        'invalid UTF-8 lone continuation', 'wrong input count before writes',
        'incorrect layout type index before writes', 'out-of-range input index before layout dereference',
        'zero-length external descriptors',
        'truncated serialized value after invalidating requested output', 'aggregate input extents before partial import',
        'source overlapping its working arena', 'source overlapping context metadata',
        'source overlapping result descriptor metadata', 'overlapping input and output descriptor tables',
        'output descriptor table overlapping module metadata', 'overflowing source range without dereference',
        'bounds check precedes type-layout dereference', 'diagnostic failure',
        'scratch failure preserves active roots and pending token',
        'preflight rejects before staging, import, or callback',
        'retained-capacity failure leaves both active roots byte-stable',
        'wrong-owner token leaves mailbox B pending without handler dispatch',
        'cross-runtime token rejection does not invoke handler',
        'duplicate A token rejection does not invoke its handler',
        'stale token rejection does not invoke its handler'
    )
    foreach ($required in $requiredNames) {
        Add-Check "$Label acceptance includes required case: $required" (@($checkNames | Where-Object { $_.Contains($required, [StringComparison]::Ordinal) }).Count -gt 0)
    }

    $life = Get-Field $Fixture 'lifecycle'
    $observed = Get-Field $Native 'observed'
    $unicode = Get-Field $life 'unicode'
    $empty = Get-Field $life 'empty'
    Compare-Bank (Get-Field $observed 'unicodeInitialize') $false @((Get-Field $unicode 'initializeRoot')) "$Label Unicode initialize" $Fixture
    Compare-Bank (Get-Field $observed 'emptyInitialize') $false @((Get-Field $empty 'initializeRoot')) "$Label empty initialize" $Fixture
    Compare-Bank (Get-Field $observed 'unicodeBegin') $true @(Get-Field $unicode 'beginRoots') "$Label Unicode begin" $Fixture
    Compare-Bank (Get-Field $observed 'emptyBegin') $true @(Get-Field $empty 'beginRoots') "$Label empty begin" $Fixture
    Compare-Bank (Get-Field $observed 'unicodeResume') $false @((Get-Field $unicode 'resumeRoot')) "$Label Unicode resume" $Fixture
    Compare-Bank (Get-Field $observed 'emptyResume') $false @((Get-Field $empty 'resumeRoot')) "$Label empty resume" $Fixture
    $rangesOkay = $true
    foreach ($snapshotName in @('unicodeInitialize', 'emptyInitialize', 'unicodeBegin', 'emptyBegin', 'unicodeResume', 'emptyResume')) {
        $snapshot = Get-Field $observed $snapshotName
        $usedBytes = [uint32](Get-Field $snapshot 'usedBytes')
        foreach ($root in @(Get-Field $snapshot 'roots')) {
            $offset = [uint32](Get-Field $root 'offsetBytes')
            $extent = [uint32](Get-Field $root 'extentBytes')
            $ownerEnd = [uint32](Get-Field $root 'ownerEndBytes')
            if ($extent -eq 0 -or $ownerEnd -ne ($offset + $extent) -or $ownerEnd -gt $usedBytes) { $rangesOkay = $false }
        }
    }
    Add-Check "$Label published output ranges remain inside their owning banks" $rangesOkay

    $totals = Get-Field (Get-Field $Fixture 'copyAccounting') 'sourceDerivedSmallLifecycleTotals'
    $successStats = Get-Field $Native 'successStats'
    Compare-StatsToFixture $successStats $totals "$Label interleaved lifecycle"
    $expectedPublished = [uint64]0
    foreach ($name in @('unicodeInitialize', 'emptyInitialize', 'unicodeBegin', 'emptyBegin', 'unicodeResume', 'emptyResume')) {
        $expectedPublished += [uint64](Get-Field (Get-Field $observed $name) 'usedBytes')
    }
    Add-Check "$Label publication byte counter equals complete successful output extents" ([uint64](Get-Field $successStats 'publicationCopyBytes') -eq $expectedPublished) ([ordered]@{ expected = $expectedPublished; actual = (Get-Field $successStats 'publicationCopyBytes') })
    Add-Check "$Label interleaving leaves two retained roots and 40 serialized bytes" ([uint64](Get-Field $successStats 'liveRetainedRoots') -eq 2 -and [uint64](Get-Field $successStats 'liveRetainedBytes') -eq 40)
    Add-Check "$Label mailbox storage is caller-reserved and component totals are explicit" (
        [uint64](Get-Field (Get-Field $Native 'storageRequirements') 'storageBytes') -eq [uint64](Get-Field $successStats 'storageReservedBytes') -and
        [uint64](Get-Field (Get-Field $Native 'storageRequirements') 'controllerReservedBytes') -gt 0) (Get-Field $Native 'storageRequirements')
    $storageConfig = Get-Field (Get-Field $Fixture 'storage') 'focusedRunnerConfiguration'
    $expectedSlotCapacity = [uint64](Get-Field $storageConfig 'scratchSlotCapacity')
    $expectedPolicy = [uint32](Get-Field $storageConfig 'suspensionPolicyId')
    Add-Check "$Label milestone 131 regression uses one RETURN scratch slot" (
        [uint64](Get-Field $successStats 'scratchSlotCapacity') -eq $expectedSlotCapacity -and
        [uint32](Get-Field $successStats 'suspensionPolicy') -eq $expectedPolicy -and
        [uint32](Get-Field $successStats 'pinnedScratchSlots') -eq 0 -and
        [uint64](Get-Field $successStats 'pinnedScratchBytes') -eq 0) $successStats
    $retainedCapacity = [uint64](Get-Field $storageConfig 'retainedByteCapacityPerBank')
    $rootCapacity = [uint64](Get-Field $storageConfig 'bankRootCapacity')
    $rootBytes = [uint64](Get-Field $storageConfig 'owningBankRootBytes')
    $mailboxCapacity = [uint64](Get-Field $storageConfig 'mailboxCapacity')
    $scratchCapacity = [uint64](Get-Field $storageConfig 'scratchByteCapacity')
    $stagingCapacity = [uint64](Get-Field $storageConfig 'textStagingByteCapacity')
    $expectedRetained = ($retainedCapacity + $rootCapacity * $rootBytes) * 2 * $mailboxCapacity
    $bitmapBytes = [Math]::Ceiling($scratchCapacity / 8.0)
    $bitmapCount = if ($RuntimeProfile -ceq 'trusted-generated') { 0 } else { 2 }
    $expectedScratch = $expectedSlotCapacity * ($scratchCapacity + $bitmapCount * $bitmapBytes)
    $requirements = Get-Field $Native 'storageRequirements'
    Add-Check "$Label caller-storage components match independent bank and profile bitmap arithmetic" (
        [uint64](Get-Field $requirements 'retainedReservedBytes') -eq $expectedRetained -and
        [uint64](Get-Field $requirements 'scratchReservedBytes') -eq $expectedScratch -and
        [uint64](Get-Field $requirements 'textStagingReservedBytes') -eq $stagingCapacity -and
        [uint64](Get-Field $requirements 'storageBytes') -eq ($expectedRetained + $expectedScratch + $stagingCapacity + [uint64](Get-Field $requirements 'controllerReservedBytes'))) $requirements

    $boundaryExpected = [ordered]@{
        utf8InputBytes = 10; utf16StagingBytes = 64; inputImportBytes = 64
        beginPublicationCopyBytes = 0; resumeRootImportBytes = 0
        publicationCopyBytes = 64; constructionAndDuplicationBytes = 64
        payloadMoveBytes = 0; returnedOutputDescriptors = 4
    }
    Compare-StatsToFixture (Get-Field $Native 'boundaryStats') $boundaryExpected "$Label UTF-8 width boundaries"
    $largeExpected = Get-Field (Get-Field $life 'largeRequest') 'sourceDerivedStats'
    Compare-StatsToFixture (Get-Field $Native 'largeRequestStats') $largeExpected "$Label 4096-byte request and retained-capacity retry"

    $scratchStats = Get-Field $Native 'scratchFailureRetryStats'
    Add-Check "$Label scratch failure retry completes with no outstanding scratch lease" (
        [uint32](Get-Field $scratchStats 'outstandingScratchLeases') -eq 0 -and
        [uint64](Get-Field $scratchStats 'scratchLeaseAcquisitions') -eq [uint64](Get-Field $scratchStats 'scratchLeaseReturns') -and
        [uint64](Get-Field $scratchStats 'moveBytes') -eq 0)
}

function Get-NativeBehaviorProjection($Native) {
    $statsFields = @(
        'utf8InputBytes', 'utf16StagingBytes', 'inputImportBytes',
        'publicationCopyBytes', 'beginPublicationCopyBytes', 'resumeRootImportBytes',
        'deepCopyBytes', 'moveBytes', 'returnedOutputDescriptors',
        'handlerInvocations', 'handlerFailures', 'liveRetainedBytes',
        'liveRetainedRoots', 'scratchLeaseAcquisitions', 'scratchLeaseReturns',
        'outstandingScratchLeases'
    )
    $stats = [ordered]@{}
    foreach ($statsName in @('successStats', 'boundaryStats', 'scratchFailureRetryStats', 'largeRequestStats')) {
        $source = Get-Field $Native $statsName
        $values = [ordered]@{}
        foreach ($field in $statsFields) { $values[$field] = Get-Field $source $field }
        $stats[$statsName] = $values
    }
    return [ordered]@{
        observed = Get-Field $Native 'observed'
        semanticAndCopyStats = $stats
    }
}

function Compare-NativeBehavior($Left, $Right, [string]$Label) {
    $leftJson = ConvertTo-Json -InputObject (Get-NativeBehaviorProjection $Left) -Depth 90 -Compress
    $rightJson = ConvertTo-Json -InputObject (Get-NativeBehaviorProjection $Right) -Depth 90 -Compress
    Add-Check $Label ([string]::Equals($leftJson, $rightJson, [StringComparison]::Ordinal)) ([ordered]@{ compared = @('fixture output values and byte ranges', 'named status and failure assertions', 'semantic and copy counters'); excluded = @('caller storage totals', 'diagnostic reset tracking counters') })
}

function Compare-NativeStorageProfiles($Diagnostic, $Fast, $Trusted, $StorageConfig, [string]$Label) {
    $slotCapacity = [uint64](Get-Field $StorageConfig 'scratchSlotCapacity')
    $scratchCapacity = [uint64](Get-Field $StorageConfig 'scratchByteCapacity')
    $bitmapBytes = [uint64][Math]::Ceiling($scratchCapacity / 8.0)
    $expectedLogicalReduction = 2 * $bitmapBytes * $slotCapacity
    $alignedBitmapBytes = [uint64]([Math]::Ceiling($bitmapBytes / 8.0) * 8)
    $expectedStorageReduction = 2 * $alignedBitmapBytes * $slotCapacity
    $diagnosticRequirements = Get-Field $Diagnostic 'storageRequirements'
    $fastRequirements = Get-Field $Fast 'storageRequirements'
    $trustedRequirements = Get-Field $Trusted 'storageRequirements'
    $fastEqual = [uint64](Get-Field $diagnosticRequirements 'storageBytes') -eq [uint64](Get-Field $fastRequirements 'storageBytes') -and
        [uint64](Get-Field $diagnosticRequirements 'scratchReservedBytes') -eq [uint64](Get-Field $fastRequirements 'scratchReservedBytes')
    Add-Check "$Label diagnostic and fast reset keep identical caller storage" $fastEqual ([ordered]@{ diagnostic = $diagnosticRequirements; fast = $fastRequirements })
    $trustedReduction = [int64](Get-Field $diagnosticRequirements 'scratchReservedBytes') - [int64](Get-Field $trustedRequirements 'scratchReservedBytes')
    $storageReduction = [int64](Get-Field $diagnosticRequirements 'storageBytes') - [int64](Get-Field $trustedRequirements 'storageBytes')
    $componentsMatch = [uint64](Get-Field $diagnosticRequirements 'retainedReservedBytes') -eq [uint64](Get-Field $trustedRequirements 'retainedReservedBytes') -and
        [uint64](Get-Field $diagnosticRequirements 'textStagingReservedBytes') -eq [uint64](Get-Field $trustedRequirements 'textStagingReservedBytes') -and
        $trustedReduction -eq $expectedLogicalReduction -and $storageReduction -eq $expectedStorageReduction
    Add-Check "$Label trusted storage removes exactly both aligned bitmap regions per scratch slot" $componentsMatch ([ordered]@{ expectedLogicalBitmapReductionBytes = $expectedLogicalReduction; actualScratchReductionBytes = $trustedReduction; expectedAlignedCallerStorageReductionBytes = $expectedStorageReduction; actualCallerStorageReductionBytes = $storageReduction; diagnostic = $diagnosticRequirements; trusted = $trustedRequirements })
}

function Compare-NativeResetTelemetry($Diagnostic, $Fast, $Trusted, [string]$Label) {
    $diagnosticTelemetry = Get-Field $Diagnostic 'resetTelemetry'
    $fastTelemetry = Get-Field $Fast 'resetTelemetry'
    $trustedTelemetry = Get-Field $Trusted 'resetTelemetry'
    $diagFastOkay = [string](Get-Field $diagnosticTelemetry 'profile') -ceq 'diagnostic' -and
        [string](Get-Field $fastTelemetry 'profile') -ceq 'fast' -and
        [uint64](Get-Field $diagnosticTelemetry 'fullCapacityPayloadWriteBytesRequested') -gt [uint64](Get-Field $fastTelemetry 'fullCapacityPayloadWriteBytesRequested') -and
        [uint64](Get-Field $fastTelemetry 'fullCapacityPayloadWriteBytesRequested') -eq 0
    foreach ($field in @('livePrefixPayloadWriteBytesRequested', 'bitmapStoreOperations', 'turnResetCursorExtentBytes')) {
        if ([uint64](Get-Field $diagnosticTelemetry $field) -ne [uint64](Get-Field $fastTelemetry $field)) { $diagFastOkay = $false }
    }
    Add-Check "$Label fast reset removes only full-capacity payload writes" $diagFastOkay ([ordered]@{ diagnostic = $diagnosticTelemetry; fast = $fastTelemetry })
    $trustedOkay = [string](Get-Field $trustedTelemetry 'profile') -ceq 'trusted-generated' -and
        [uint64](Get-Field $trustedTelemetry 'fullCapacityPayloadWriteBytesRequested') -eq 0 -and
        [uint64](Get-Field $trustedTelemetry 'livePrefixPayloadWriteBytesRequested') -eq 0 -and
        [uint64](Get-Field $trustedTelemetry 'bitmapStoreOperations') -eq 0 -and
        [uint64](Get-Field $trustedTelemetry 'turnResetCursorExtentBytes') -eq [uint64](Get-Field $diagnosticTelemetry 'turnResetCursorExtentBytes')
    Add-Check "$Label trusted reset skips payload and bitmap writes while retaining cursor extent" $trustedOkay ([ordered]@{ diagnostic = $diagnosticTelemetry; trusted = $trustedTelemetry })
}

function Resolve-SumLayoutIndexes($Bootstrap) {
    $requiredTypes = @('State', 'Continuation', 'String', 'Chunk', 'Option<Chunk>', 'Result<Chunk, String>')
    $layouts = @(Get-Field $Bootstrap 'layouts')
    $indexes = [ordered]@{}
    foreach ($typeName in $requiredTypes) {
        $matches = @($layouts | Where-Object { [string](Get-Field $_ 'typeName') -ceq $typeName })
        if ($matches.Count -ne 1 -or -not (Has-Field $matches[0] 'layoutIndex')) {
            throw "Sum bootstrap must expose exactly one layout with typeName '$typeName' and a layoutIndex."
        }
        $indexes[$typeName] = [uint32](Get-Field $matches[0] 'layoutIndex')
    }
    if (@($indexes.Values | Select-Object -Unique).Count -ne $requiredTypes.Count) {
        throw 'Sum bootstrap layout indexes must be unique for State, Continuation, String, Chunk, Option<Chunk>, and Result<Chunk, String>.'
    }
    return $indexes
}

function Resolve-RefinementLayoutIndexes($Bootstrap) {
    $requiredTypes = @('State', 'Continuation', 'String', 'Option<NonEmptyString>', 'Option<PositiveId>', 'Result<PositiveId, NonEmptyString>')
    $layouts = @(Get-Field $Bootstrap 'layouts')
    $indexes = [ordered]@{}
    foreach ($typeName in $requiredTypes) {
        $matches = @($layouts | Where-Object { [string](Get-Field $_ 'typeName') -ceq $typeName })
        if ($matches.Count -ne 1 -or -not (Has-Field $matches[0] 'layoutIndex')) {
            throw "Refined bootstrap must expose exactly one layout with typeName '$typeName' and a layoutIndex."
        }
        $indexes[$typeName] = [uint32](Get-Field $matches[0] 'layoutIndex')
    }
    if (@($indexes.Values | Select-Object -Unique).Count -ne $requiredTypes.Count) {
        throw 'Refined mailbox layout indexes must be unique for every runner argument type.'
    }
    return $indexes
}

function Compare-RefinementSnapshot($Actual, [Parameter()][AllowEmptyCollection()][object[]]$ExpectedRoots, [bool]$Pending, [string]$Label) {
    $actualRoots = [Collections.Generic.List[object]]::new()
    $actualRootsValue = Get-Field $Actual 'roots'
    if ($null -ne $actualRootsValue) {
        foreach ($root in $actualRootsValue) { $actualRoots.Add($root) }
    }
    $expectedRootsNormalized = [Collections.Generic.List[object]]::new()
    if ($null -ne $ExpectedRoots) {
        foreach ($root in $ExpectedRoots) { $expectedRootsNormalized.Add($root) }
    }
    $okay = (Get-Field $Actual 'pending') -eq [int]$Pending -and $actualRoots.Count -eq $expectedRootsNormalized.Count
    $offset = [uint32]0
    for ($index = 0; $index -lt [Math]::Min($actualRoots.Count, $expectedRootsNormalized.Count); $index++) {
        $actualRoot = $actualRoots[$index]
        $expectedRoot = $expectedRootsNormalized[$index]
        $rootOkay = [uint32](Get-Field $actualRoot 'typeId') -eq [uint32](Get-Field $expectedRoot 'typeId') -and
            [uint32](Get-Field $actualRoot 'offsetBytes') -eq $offset -and
            [uint32](Get-Field $actualRoot 'extentBytes') -eq [uint32](Get-Field $expectedRoot 'extentBytes') -and
            [uint32](Get-Field $actualRoot 'payloadBytes') -eq [uint32](Get-Field $expectedRoot 'payloadBytes') -and
            [uint32](Get-Field $actualRoot 'ownerEndBytes') -eq ($offset + [uint32](Get-Field $expectedRoot 'extentBytes')) -and
            [string](Get-Field $actualRoot 'serializedHex') -ceq [string](Get-Field $expectedRoot 'serializedHex')
        Add-Check "$Label root $index matches independent type, payload, extent, and bytes" $rootOkay ([ordered]@{ expected = $expectedRoot; actual = $actualRoot })
        $okay = $okay -and $rootOkay
        $offset += [uint32](Get-Field $expectedRoot 'extentBytes')
    }
    $okay = $okay -and [uint32](Get-Field $Actual 'rootCount') -eq $expectedRootsNormalized.Count -and [uint32](Get-Field $Actual 'usedBytes') -eq $offset
    Add-Check "$Label root count, pending state, and used extent match the independent fixture" $okay ([ordered]@{ pending = Get-Field $Actual 'pending'; rootCount = Get-Field $Actual 'rootCount'; usedBytes = Get-Field $Actual 'usedBytes'; expectedRootCount = $expectedRootsNormalized.Count; expectedUsedBytes = $offset })
    return $okay
}

function Get-RefinementExpectedStepArray($Oracle, [string]$Policy) {
    $expected = Get-Field $Oracle 'expectedSteps'
    if ($Policy -ceq 'RETURN') {
        return @(
            [uint32](Get-Field $expected 'initialize'),
            [uint32](Get-Field $expected 'beginReturn'),
            [uint32](Get-Field $expected 'resumeEmptyReturn'),
            [uint32](Get-Field $expected 'resumeFailReturn'),
            [uint32](Get-Field $expected 'resumeSuccessReturn'))
    }
    return @(
        [uint32](Get-Field $expected 'initialize'),
        [uint32](Get-Field $expected 'beginKeep'),
        [uint32](Get-Field $expected 'resumeEmptyKeep'),
        [uint32](Get-Field $expected 'resumeFailKeep'),
        [uint32](Get-Field $expected 'resumeSuccessKeep'))
}

function Test-RefinementOracle($Bootstrap, $Fixture, [string]$Label) {
    $provenance = Get-Field $Fixture 'oracleProvenance'
    $firstEvidence = Get-Field $provenance 'firstNativeExecutionEvidence'
    $correctionValue = Get-Field $provenance 'postExecutionCorrections'
    $corrections = [Collections.Generic.List[object]]::new()
    if ($null -ne $correctionValue) {
        foreach ($correction in $correctionValue) { $corrections.Add($correction) }
    }
    $initialHash = [string](Get-Field $provenance 'initialFrozenSnapshotSha256')
    $firstRunId = [string](Get-Field $firstEvidence 'runId')
    $firstEvidencePath = [string](Get-Field $firstEvidence 'relativePath')
    $expectedFinalFixtureHash = 'A22BB448EE864713903D5EE7946C785CD7C8978CF54DA02E0F2E0B86911CBBB1'
    $actualFixtureHash = (Get-FileHash -LiteralPath $script:refinementsFixturePath -Algorithm SHA256).Hash.ToUpperInvariant()
    $expectedPriorHash = $initialHash
    $correctionKinds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $provenanceOkay = (Get-Field $Fixture 'frozenBeforeNativeExecution') -eq $false -and
        (Get-Field $provenance 'sourceDerivedBeforeNativeExecution') -eq $false -and
        (Get-Field $provenance 'initialFreezeBeforeNativeExecution') -eq $true -and
        (Get-Field $provenance 'valueAndStepOraclesFrozenBeforeNativeExecution') -eq $true -and
        (Get-Field $provenance 'copyOracleFrozenBeforeNativeExecution') -eq $false -and
        (Get-Field $provenance 'copyOracleCorrectedAfterFirstNativeExecution') -eq $true -and
        $initialHash -match '^[0-9a-fA-F]{64}$' -and
        $firstEvidencePath -match [regex]::Escape($firstRunId) -and
        $firstEvidencePath -match 'integration-evidence\.json$' -and
        $firstRunId -match '^[0-9a-fA-F]{32}$' -and
        -not [string]::IsNullOrWhiteSpace([string](Get-Field $firstEvidence 'scope')) -and
        $corrections.Count -ge 2
    for ($correctionIndex = 0; $correctionIndex -lt $corrections.Count; $correctionIndex++) {
        $correction = $corrections[$correctionIndex]
        $changeValue = Get-Field $correction 'changes'
        $changes = [Collections.Generic.List[object]]::new()
        if ($null -ne $changeValue) {
            foreach ($change in $changeValue) { $changes.Add($change) }
        }
        $kind = [string](Get-Field $correction 'kind')
        $priorHash = [string](Get-Field $correction 'priorFixtureSha256')
        $afterHash = [string](Get-Field $correction 'afterFixtureSha256')
        $hasFollowingCorrection = $correctionIndex -lt ($corrections.Count - 1)
        $provenanceOkay = $provenanceOkay -and
            [string](Get-Field $correction 'afterRunId') -ceq $firstRunId -and
            $priorHash -ceq $expectedPriorHash -and
            $priorHash -match '^[0-9a-fA-F]{64}$' -and
            -not [string]::IsNullOrWhiteSpace($kind) -and
            $changes.Count -gt 0 -and
            (-not $hasFollowingCorrection -or $afterHash -match '^[0-9a-fA-F]{64}$')
        [void]$correctionKinds.Add($kind)
        if ($hasFollowingCorrection) { $expectedPriorHash = $afterHash }
    }
    $lastCorrectionAfterHash = if ($corrections.Count -gt 0) { [string](Get-Field $corrections[$corrections.Count - 1] 'afterFixtureSha256') } else { '' }
    $provenanceOkay = $provenanceOkay -and
        $correctionKinds.Contains('diagnostic-code-classification') -and
        $correctionKinds.Contains('copy-accounting') -and
        [string]::IsNullOrWhiteSpace($lastCorrectionAfterHash) -and
        $actualFixtureHash -ceq $expectedFinalFixtureHash
    Add-Check "$Label fixture provenance links the initial freeze and corrections to the pinned current fixture bytes" $provenanceOkay ([ordered]@{ frozenBeforeNativeExecution = Get-Field $Fixture 'frozenBeforeNativeExecution'; initialFrozenSnapshotSha256 = $initialHash; terminalCorrectionPriorSha256 = $expectedPriorHash; actualFixtureSha256 = $actualFixtureHash; expectedFixtureSha256 = $expectedFinalFixtureHash; provenance = $provenance })

    $oracle = Get-Field $Bootstrap 'sourceDerivedRefinementOracle'
    $expected = Get-Field $Fixture 'sourceDerivedStepOracle'
    $expectedBodies = Get-Field $expected 'handlerBodyStepsExcludingExternalAdmission'
    $actualBodies = Get-Field $oracle 'bodySteps'
    $bodyPairs = @(
        @{ actual = 'initialize'; expected = 'initialize' },
        @{ actual = 'beginRole'; expected = 'begin' },
        @{ actual = 'resumeEmpty'; expected = 'resumeEmpty' },
        @{ actual = 'resumeFail'; expected = 'resumeFail' },
        @{ actual = 'resumeSuccess'; expected = 'resumeSuccess' })
    $bodyOkay = $true
    foreach ($pair in $bodyPairs) {
        $bodyOkay = $bodyOkay -and [uint32](Get-Field $actualBodies $pair.actual) -eq [uint32](Get-Field $expectedBodies $pair.expected)
    }
    Add-Check "$Label handler body instruction costs match the frozen verified-IR oracle" $bodyOkay ([ordered]@{ expected = $expectedBodies; actual = $actualBodies })

    $expectedIdentity = @(Get-Field $Fixture 'frozenPredicates')
    $actualIdentity = @(Get-Field $oracle 'validatorIdentity')
    $identityOkay = $expectedIdentity.Count -eq 2 -and $actualIdentity.Count -eq 2
    foreach ($expectedPredicate in $expectedIdentity) {
        $found = @($actualIdentity | Where-Object { [string](Get-Field $_ 'name') -ceq [string](Get-Field $expectedPredicate 'validator') })
        $identityOkay = $identityOkay -and $found.Count -eq 1 -and
            [string](Get-Field $found[0] 'wordId') -ceq [string](Get-Field $expectedPredicate 'wordId') -and
            [uint32](Get-Field $found[0] 'revision') -eq [uint32](Get-Field $expectedPredicate 'revision') -and
            [uint32](Get-Field $found[0] 'instructionCost') -eq [uint32](Get-Field $expectedPredicate 'stepsPerInvocation')
    }
    Add-Check "$Label validator target identities, revisions, and normal instruction costs match the fixture" $identityOkay ([ordered]@{ expected = $expectedIdentity; actual = $actualIdentity })

    $expectedCalls = Get-Field $expected 'sourceDerivedPredicateInvocations'
    $actualCalls = Get-Field $oracle 'expectedCalls'
    $callPairs = @(
        @{ actual = 'initialize'; expected = 'initialize' },
        @{ actual = 'beginRole'; expected = 'begin' },
        @{ actual = 'resumeEmptyReturn'; expected = 'resumeEmptyReturn' },
        @{ actual = 'resumeEmptyKeep'; expected = 'resumeEmptyKeep' },
        @{ actual = 'resumeFailReturn'; expected = 'resumeFailReturn' },
        @{ actual = 'resumeFailKeep'; expected = 'resumeFailKeep' },
        @{ actual = 'resumeSuccessReturn'; expected = 'resumeSuccessReturn' },
        @{ actual = 'resumeSuccessKeep'; expected = 'resumeSuccessKeep' },
        @{ actual = 'controllerTotalReturn'; expected = 'controllerTotalReturn' },
        @{ actual = 'controllerTotalKeep'; expected = 'controllerTotalKeep' })
    $callsOkay = $true
    foreach ($pair in $callPairs) {
        $actualPair = Get-Field $actualCalls $pair.actual
        $expectedPair = Get-Field $expectedCalls $pair.expected
        foreach ($name in @('positiveId', 'nonEmptyString')) {
            $expectedName = if ($name -ceq 'positiveId') { 'PositiveId' } else { 'NonEmptyString' }
            $callsOkay = $callsOkay -and [int](Get-Field $actualPair $name) -eq [int](Get-Field $expectedPair $expectedName)
        }
    }
    Add-Check "$Label source-derived refinement invocation counts match the frozen fixture" $callsOkay ([ordered]@{ expected = $expectedCalls; actual = $actualCalls })

    $returnSteps = Get-RefinementExpectedStepArray $oracle 'RETURN'
    $keepSteps = Get-RefinementExpectedStepArray $oracle 'KEEP_ASSOCIATED'
    $fixtureReturnSteps = @(Get-Field (Get-Field $expected 'stepsConsumedByCall') 'RETURN' | ForEach-Object { [uint32]$_ })
    $fixtureKeepSteps = @(Get-Field (Get-Field $expected 'stepsConsumedByCall') 'KEEP_ASSOCIATED' | ForEach-Object { [uint32]$_ })
    $stepsOkay = ($returnSteps -join ',') -ceq ($fixtureReturnSteps -join ',') -and ($keepSteps -join ',') -ceq ($fixtureKeepSteps -join ',')
    $actualExpectedSteps = Get-Field $oracle 'expectedSteps'
    $stepsOkay = $stepsOkay -and [uint32](Get-Field $actualExpectedSteps 'returnTotal') -eq [uint32](($fixtureReturnSteps | Measure-Object -Sum).Sum) -and
        [uint32](Get-Field $actualExpectedSteps 'keepTotal') -eq [uint32](($fixtureKeepSteps | Measure-Object -Sum).Sum)
    Add-Check "$Label source-derived per-call and total instruction counts match the frozen fixture" $stepsOkay ([ordered]@{ expectedReturn = $fixtureReturnSteps; actualReturn = $returnSteps; expectedKeep = $fixtureKeepSteps; actualKeep = $keepSteps })
    return $provenanceOkay -and $bodyOkay -and $identityOkay -and $callsOkay -and $stepsOkay
}

function Compare-RefinementStats($Actual, $Expected, [string]$Label) {
    $fields = @(
        'utf8InputBytes', 'utf16StagingBytes', 'inputImportBytes',
        'publicationCopyBytes', 'beginPublicationCopyBytes', 'resumeRootImportBytes',
        'deepCopyBytes', 'moveBytes', 'returnedOutputDescriptors',
        'handlerInvocations', 'handlerFailures')
    $okay = $true
    foreach ($field in $fields) {
        $hasActual = Has-Field $Actual $field
        $hasExpected = Has-Field $Expected $field
        $matches = $hasActual -and $hasExpected -and
            [uint64](Get-Field $Actual $field) -eq [uint64](Get-Field $Expected $field)
        Add-Check "$Label $field equals source-derived fixture accounting" $matches ([ordered]@{ expected = if ($hasExpected) { Get-Field $Expected $field } else { $null }; actual = if ($hasActual) { Get-Field $Actual $field } else { $null } })
        $okay = $okay -and $matches
    }
    return $okay
}

function Get-RefinementErrorCode($Bootstrap, $ErrorId) {
    if ($null -eq $ErrorId -or [uint32]$ErrorId -eq [uint32]::MaxValue) { return $null }
    $matches = @(Get-Field $Bootstrap 'diagnostics' | Where-Object { [uint32](Get-Field $_ 'id') -eq [uint32]$ErrorId })
    if ($matches.Count -ne 1) { return $null }
    return [string](Get-Field $matches[0] 'code')
}

function Get-RefinementConcatenatedHex([object[]]$Roots) {
    return (($Roots | ForEach-Object { [string](Get-Field $_ 'serializedHex') }) -join '')
}

function Get-RefinementRawProjection($Raw) {
    return [ordered]@{
        pending = Get-Field $Raw 'pending'
        bankRootCount = Get-Field $Raw 'bankRootCount'
        bankUsedBytes = Get-Field $Raw 'bankUsedBytes'
        bankBytesHex = Get-Field $Raw 'bankBytesHex'
        bankRoots = @(Get-Field $Raw 'bankRoots')
        associatedRootCount = Get-Field $Raw 'associatedRootCount'
        associatedCursorBytes = Get-Field $Raw 'associatedCursorBytes'
        associatedPrefixHex = Get-Field $Raw 'associatedPrefixHex'
        associatedRoots = @(Get-Field $Raw 'associatedRoots')
    }
}

function Compare-RefinementRawSnapshot($Actual, [Parameter()][AllowEmptyCollection()][object[]]$ExpectedBankRoots, [Parameter()][AllowEmptyCollection()][object[]]$ExpectedAssociatedRoots, $LayoutIndexes, [string]$Label) {
    $bankRoots = [Collections.Generic.List[object]]::new()
    $bankRootsValue = Get-Field $Actual 'bankRoots'
    if ($null -ne $bankRootsValue) {
        foreach ($root in $bankRootsValue) { $bankRoots.Add($root) }
    }
    $expectedBankRootsNormalized = [Collections.Generic.List[object]]::new()
    if ($null -ne $ExpectedBankRoots) {
        foreach ($root in $ExpectedBankRoots) { $expectedBankRootsNormalized.Add($root) }
    }
    $expectedAssociatedRootsNormalized = [Collections.Generic.List[object]]::new()
    if ($null -ne $ExpectedAssociatedRoots) {
        foreach ($root in $ExpectedAssociatedRoots) { $expectedAssociatedRootsNormalized.Add($root) }
    }
    $bankOkay = [uint32](Get-Field $Actual 'pending') -eq 1 -and
        [uint32](Get-Field $Actual 'bankRootCount') -eq $expectedBankRootsNormalized.Count -and
        [uint32](Get-Field $Actual 'bankUsedBytes') -eq [uint32](($expectedBankRootsNormalized | Measure-Object -Property extentBytes -Sum).Sum) -and
        [string](Get-Field $Actual 'bankBytesHex') -ceq (Get-RefinementConcatenatedHex $expectedBankRootsNormalized.ToArray()) -and
        $bankRoots.Count -eq $expectedBankRootsNormalized.Count
    $offset = [uint32]0
    for ($index = 0; $index -lt [Math]::Min($bankRoots.Count, $expectedBankRootsNormalized.Count); $index++) {
        $actualRoot = $bankRoots[$index]
        $expectedRoot = $expectedBankRootsNormalized[$index]
        $rootOkay = [uint32](Get-Field $actualRoot 'typeId') -eq [uint32](Get-Field $expectedRoot 'typeId') -and
            [uint32](Get-Field $actualRoot 'offsetBytes') -eq $offset -and
            [uint32](Get-Field $actualRoot 'extentBytes') -eq [uint32](Get-Field $expectedRoot 'extentBytes') -and
            [uint32](Get-Field $actualRoot 'payloadBytes') -eq [uint32](Get-Field $expectedRoot 'payloadBytes') -and
            [uint32](Get-Field $actualRoot 'ownerEndBytes') -eq ($offset + [uint32](Get-Field $expectedRoot 'extentBytes'))
        $bankOkay = $bankOkay -and $rootOkay
        $offset += [uint32](Get-Field $expectedRoot 'extentBytes')
    }
    Add-Check "$Label raw bank bytes and descriptors match independently serialized roots" $bankOkay ([ordered]@{ expectedRoots = $ExpectedBankRoots; actual = $Actual })

    $associatedRoots = [Collections.Generic.List[object]]::new()
    $associatedRootsValue = Get-Field $Actual 'associatedRoots'
    if ($null -ne $associatedRootsValue) {
        foreach ($root in $associatedRootsValue) { $associatedRoots.Add($root) }
    }
    $prefix = [string](Get-Field $Actual 'associatedPrefixHex')
    $associatedOkay = [uint32](Get-Field $Actual 'associatedRootCount') -eq $expectedAssociatedRootsNormalized.Count -and
        $associatedRoots.Count -eq $expectedAssociatedRootsNormalized.Count -and
        ([uint32](Get-Field $Actual 'associatedCursorBytes') * 2) -eq $prefix.Length
    $associatedIndexes = @(
        [uint32](Get-Field $LayoutIndexes 'State'),
        [uint32](Get-Field $LayoutIndexes 'Continuation'))
    for ($index = 0; $index -lt [Math]::Min($associatedRoots.Count, $expectedAssociatedRootsNormalized.Count); $index++) {
        $actualRoot = $associatedRoots[$index]
        $expectedRoot = $expectedAssociatedRootsNormalized[$index]
        $start = [uint32](Get-Field $actualRoot 'sourceOffsetBytes')
        $end = [uint32](Get-Field $actualRoot 'sourceOwnerEndBytes')
        $extent = [uint32](Get-Field $expectedRoot 'extentBytes')
        $sliceOkay = [uint32](Get-Field $actualRoot 'typeIndex') -eq $associatedIndexes[$index] -and
            [uint32](Get-Field $actualRoot 'reserved') -eq 0 -and
            $end -ge $start -and ($end - $start) -eq $extent -and
            ($end * 2) -le $prefix.Length
        if ($sliceOkay) {
            $sliceHex = $prefix.Substring([int]($start * 2), [int]($extent * 2))
            $sliceOkay = $sliceHex -ceq [string](Get-Field $expectedRoot 'serializedHex')
        }
        $associatedOkay = $associatedOkay -and $sliceOkay
    }
    Add-Check "$Label raw associated root slices identify and contain the independently serialized values" $associatedOkay ([ordered]@{ expectedRoots = $ExpectedAssociatedRoots; actual = $Actual })
    return $bankOkay -and $associatedOkay
}

function Check-RefinementNativeEvidence($Native, $Fixture, $Bootstrap, $LayoutIndexes, [string]$Label) {
    $nativePassed = (Get-Field $Native 'passed') -eq $true -and [int](Get-Field $Native 'failureCount') -eq 0
    Add-Check "$Label C runner completed all structural and lifecycle checks" $nativePassed (Get-Field $Native 'checks')
    $named = @(Get-Field $Native 'checks')
    Add-Check "$Label C runner named assertions all pass" ($named.Count -eq 13 -and @($named | Where-Object { (Get-Field $_ 'passed') -ne $true }).Count -eq 0) ([ordered]@{ assertionCount = $named.Count; failures = @($named | Where-Object { (Get-Field $_ 'passed') -ne $true }) })

    $direct = Get-Field $Native 'direct'
    $roots = Get-Field (Get-Field $Fixture 'lifecycle') 'roots'
    $validDirect = Get-Field $direct 'validBegin'
    $validDirectOkay = [uint32](Get-Field $validDirect 'callbackResult') -eq 0 -and
        [uint32](Get-Field $validDirect 'contextStatus') -eq 0 -and
        (Get-Field $validDirect 'outputContract') -eq $true -and
        [uint32](Get-Field $validDirect 'stepsConsumed') -eq [uint32](Get-Field (Get-Field (Get-Field $Fixture 'directAdmissionControls') 'validState') 'fullBeginStepsIncludingHandler')
    Add-Check "$Label direct valid raw State skips inactive alternatives and admits the full begin role" $validDirectOkay $validDirect
    Compare-RefinementSnapshot (Get-Field $validDirect 'outputs') @((Get-Field $roots 'initializedState'), (Get-Field $roots 'suspendedContinuation')) $false "$Label direct valid begin outputs"

    $directCases = @(
        @{ fixtureName = 'State.id-negative'; outputName = 'negativeId' },
        @{ fixtureName = 'State.id-minimum-integer'; outputName = 'overflowId' },
        @{ fixtureName = 'State.optional-some-empty-string'; outputName = 'someEmptyLabel' },
        @{ fixtureName = 'State.status-error-empty-string'; outputName = 'resultErrorEmptyLabel' })
    $invalidControls = @(Get-Field (Get-Field $Fixture 'directAdmissionControls') 'invalidActiveValues')
    foreach ($case in $directCases) {
        $expectedCase = @($invalidControls | Where-Object { [string](Get-Field $_ 'name') -ceq $case.fixtureName })
        if ($expectedCase.Count -ne 1) { throw "Refinement fixture is missing direct admission control '$($case.fixtureName)'." }
        $expectedCase = $expectedCase[0]
        $actualCase = Get-Field $direct $case.outputName
        $expectedError = [string](Get-Field $expectedCase 'expectedError')
        $actualError = Get-RefinementErrorCode $Bootstrap (Get-Field $actualCase 'errorId')
        $caseOkay = [uint32](Get-Field $actualCase 'callbackResult') -ne 0 -and
            [uint32](Get-Field $actualCase 'contextStatus') -ne 0 -and
            (Get-Field $actualCase 'outputContract') -eq $true -and
            [uint32](Get-Field $actualCase 'stepsConsumed') -eq [uint32](Get-Field $expectedCase 'stepsConsumed') -and
            $actualError -ceq $expectedError -and
            [uint32](Get-Field (Get-Field $actualCase 'outputs') 'rootCount') -eq 0
        Add-Check "$Label direct $($case.fixtureName) fails at the frozen step prefix with invalid output descriptors" $caseOkay ([ordered]@{ expected = $expectedCase; actual = $actualCase; actualError = $actualError })
    }
    $overflowExpected = @($invalidControls | Where-Object { [string](Get-Field $_ 'name') -ceq 'State.id-minimum-integer' })[0]
    $normalPositiveCost = [uint32](Get-Field (@(Get-Field $Fixture 'frozenPredicates' | Where-Object { [string](Get-Field $_ 'scalar') -ceq 'PositiveId' })[0]) 'stepsPerInvocation')
    $overflowPrefix = [uint32](Get-Field (@(Get-Field $Fixture 'frozenPredicates' | Where-Object { [string](Get-Field $_ 'scalar') -ceq 'PositiveId' })[0]) 'runtimeErrorPrefixSteps')
    Add-Check "$Label direct Int64.MinValue uses the independently audited checked-division prefix" ([uint32](Get-Field $overflowExpected 'stepsConsumed') -eq $overflowPrefix -and $overflowPrefix -lt $normalPositiveCost)

    $descriptorContract = Get-Field (Get-Field $Fixture 'directAdmissionControls') 'outputDescriptorContract'
    foreach ($case in @(
        @{ fixtureName = 'postPreflightMalformedOptionTag'; outputName = 'postPreflightMalformedOptionTag' },
        @{ fixtureName = 'postPreflightMalformedResultTag'; outputName = 'postPreflightMalformedResultTag' })) {
        $expectedContract = Get-Field $descriptorContract $case.fixtureName
        $malformed = Get-Field $direct $case.outputName
        $actualError = Get-RefinementErrorCode $Bootstrap (Get-Field $malformed 'errorId')
        $malformedOkay = [uint32](Get-Field $malformed 'callbackResult') -ne 0 -and
            [uint32](Get-Field $malformed 'contextStatus') -eq [uint32](Get-Field $expectedContract 'expectedContextStatus') -and
            (Get-Field $malformed 'outputContract') -eq $true -and
            [uint32](Get-Field $malformed 'stepsConsumed') -eq [uint32](Get-Field $expectedContract 'stepsConsumed') -and
            $actualError -ceq [string](Get-Field $expectedContract 'expectedError') -and
            [string](Get-Field $expectedContract 'outputDescriptors') -ceq 'invalid' -and
            [uint32](Get-Field (Get-Field $malformed 'outputs') 'rootCount') -eq 0
        Add-Check "$Label $($case.fixtureName) is a post-preflight scan failure with invalid outputs" $malformedOkay ([ordered]@{ expected = $expectedContract; actual = $malformed; actualError = $actualError })
    }
    $wrongIndexExpected = Get-Field $descriptorContract 'earlyBoundaryWrongInputTypeIndex'
    $wrongIndex = Get-Field $direct 'earlyBoundaryWrongInputTypeIndex'
    $wrongIndexError = Get-RefinementErrorCode $Bootstrap (Get-Field $wrongIndex 'errorId')
    $wrongIndexOkay = [uint32](Get-Field $wrongIndex 'callbackResult') -ne 0 -and
        [uint32](Get-Field $wrongIndex 'contextStatus') -eq [uint32](Get-Field $wrongIndexExpected 'expectedContextStatus') -and
        (Get-Field $wrongIndex 'outputContract') -eq $true -and
        [uint32](Get-Field $wrongIndex 'stepsConsumed') -eq [uint32](Get-Field $wrongIndexExpected 'stepsConsumed') -and
        $wrongIndexError -ceq [string](Get-Field $wrongIndexExpected 'expectedError') -and
        [string](Get-Field $wrongIndexExpected 'outputDescriptors') -ceq 'unchanged' -and
        [uint32](Get-Field $wrongIndexExpected 'actualInputTypeIndex') -eq [uint32](Get-Field $LayoutIndexes 'Continuation') -and
        [uint32](Get-Field $wrongIndexExpected 'expectedInputTypeIndex') -eq [uint32](Get-Field $LayoutIndexes 'State') -and
        [uint32](Get-Field (Get-Field $wrongIndex 'outputs') 'rootCount') -eq 0
    Add-Check "$Label early wrong input type index is rejected before callback output writes" $wrongIndexOkay ([ordered]@{ expected = $wrongIndexExpected; actual = $wrongIndex; actualError = $wrongIndexError })

    $policyEvidence = Get-Field $Native 'policies'
    $expectedRoots = @((Get-Field $roots 'beforeAndAfterFailedResume'))
    $expectedFinal = @((Get-Field $roots 'completedState'))
    $statsByPolicy = Get-Field (Get-Field $Fixture 'copyAccounting') 'expectedStatsBySuspensionPolicy'
    $stepOracle = Get-Field $Fixture 'sourceDerivedStepOracle'
    foreach ($policy in @('RETURN', 'KEEP_ASSOCIATED')) {
        $policyRun = Get-Field $policyEvidence $policy
        $isKeep = $policy -ceq 'KEEP_ASSOCIATED'
        $bankRoots = if ($isKeep) { @((Get-Field $roots 'initializedState')) } else { @((Get-Field $roots 'initializedState'), (Get-Field $roots 'suspendedContinuation')) }
        $associatedRoots = if ($isKeep) { $expectedRoots } else { @() }
        $expectedPending = if ($isKeep) { $expectedRoots } else { $expectedRoots }
        Compare-RefinementSnapshot (Get-Field $policyRun 'initialize') @((Get-Field $roots 'initializedState')) $false "$Label $policy initialized State"
        Compare-RefinementSnapshot (Get-Field $policyRun 'pendingBank') $bankRoots $true "$Label $policy pending bank"
        Compare-RefinementSnapshot (Get-Field $policyRun 'pendingAssociated') $associatedRoots $true "$Label $policy pending associated roots"
        Compare-RefinementSnapshot (Get-Field $policyRun 'beforeEmptyResume') $expectedPending $true "$Label $policy before failed resumes"
        Compare-RefinementSnapshot (Get-Field $policyRun 'afterEmptyResume') $expectedPending $true "$Label $policy after empty failure"
        Compare-RefinementSnapshot (Get-Field $policyRun 'afterFailResume') $expectedPending $true "$Label $policy after divide failure"
        Compare-RefinementSnapshot (Get-Field $policyRun 'final') $expectedFinal $false "$Label $policy same-token retry result"

        $rawExpectedBank = if ($isKeep) { @((Get-Field $roots 'initializedState')) } else { $expectedRoots }
        Compare-RefinementRawSnapshot (Get-Field $policyRun 'rawPending') $rawExpectedBank $associatedRoots $LayoutIndexes "$Label $policy raw pending roots"
        $rawProjection = ConvertTo-Json -InputObject (Get-RefinementRawProjection (Get-Field $policyRun 'rawPending')) -Depth 20 -Compress
        foreach ($rawName in @('rawAfterEmptyResume', 'rawAfterFailResume')) {
            $afterRaw = Get-Field $policyRun $rawName
            $afterProjection = ConvertTo-Json -InputObject (Get-RefinementRawProjection $afterRaw) -Depth 20 -Compress
            $sameRaw = [string]::Equals($rawProjection, $afterProjection, [StringComparison]::Ordinal)
            Add-Check "$Label $policy $rawName preserves raw root descriptors and bytes" $sameRaw ([ordered]@{ before = Get-RefinementRawProjection (Get-Field $policyRun 'rawPending'); after = Get-RefinementRawProjection $afterRaw })
            Compare-RefinementRawSnapshot $afterRaw $rawExpectedBank $associatedRoots $LayoutIndexes "$Label $policy $rawName"
        }

        $preservation = Get-Field $policyRun 'preservation'
        Add-Check "$Label $policy both semantic and runtime failures preserve pending logical roots" ((Get-Field $preservation 'emptyResume') -eq $true -and (Get-Field $preservation 'failResume') -eq $true)
        $tokens = Get-Field $policyRun 'tokens'
        $beforeToken = @(Get-Field (Get-Field $tokens 'before') 'opaque') -join ','
        $emptyToken = @(Get-Field (Get-Field $tokens 'afterEmptyResume') 'opaque') -join ','
        $failToken = @(Get-Field (Get-Field $tokens 'afterFailResume') 'opaque') -join ','
        Add-Check "$Label $policy both failures preserve the same opaque continuation token" ($beforeToken -ceq $emptyToken -and $beforeToken -ceq $failToken) ([ordered]@{ before = $beforeToken; afterEmpty = $emptyToken; afterFail = $failToken })

        $calls = Get-Field $policyRun 'calls'
        $expectedSteps = @(Get-Field (Get-Field $stepOracle 'stepsConsumedByCall') $policy | ForEach-Object { [uint32]$_ })
        $callNames = @('initialize', 'begin', 'resumeEmpty', 'resumeFail', 'retry')
        $expectedResults = @(0u, 0u, 17u, 17u, 0u)
        $expectedErrors = @($null, $null, 'REFINEMENT_FAILED', 'RUNTIME_DIVIDE_BY_ZERO', $null)
        $actualStepList = [Collections.Generic.List[uint32]]::new()
        for ($index = 0; $index -lt $callNames.Count; $index++) {
            $call = Get-Field $calls $callNames[$index]
            $errorCode = Get-RefinementErrorCode $Bootstrap (Get-Field $call 'errorId')
            $callOkay = [uint32](Get-Field $call 'result') -eq $expectedResults[$index] -and
                [uint32](Get-Field $call 'stepsConsumed') -eq $expectedSteps[$index] -and
                $errorCode -ceq $expectedErrors[$index]
            $actualStepList.Add([uint32](Get-Field $call 'stepsConsumed'))
            Add-Check "$Label $policy $($callNames[$index]) status, diagnostic, and measured IR steps match the fixture" $callOkay ([ordered]@{ expectedResult = $expectedResults[$index]; expectedError = $expectedErrors[$index]; expectedSteps = $expectedSteps[$index]; actual = $call; actualError = $errorCode })
        }
        $statsExpected = Get-Field $statsByPolicy $policy
        $stats = Get-Field $policyRun 'stats'
        Compare-RefinementStats $stats $statsExpected "$Label $policy controller"
        $cleanupOkay = [uint32](Get-Field $stats 'pendingMailboxes') -eq 0 -and
            [uint64](Get-Field $stats 'liveRetainedRoots') -eq 1 -and
            [uint32](Get-Field $stats 'outstandingScratchLeases') -eq 0 -and
            [uint32](Get-Field $stats 'pinnedScratchSlots') -eq 0 -and
            [uint64](Get-Field $stats 'pinnedScratchBytes') -eq 0
        Add-Check "$Label $policy final controller has one active root and no pending or retained scratch frame" $cleanupOkay $stats
    }
}

function Get-RefinementBehaviorProjection($Native) {
    $policies = [ordered]@{}
    foreach ($policyName in @('RETURN', 'KEEP_ASSOCIATED')) {
        $policy = Get-Field (Get-Field $Native 'policies') $policyName
        $policyProjection = [ordered]@{}
        foreach ($name in @('initialize', 'pendingBank', 'pendingAssociated', 'beforeEmptyResume', 'afterEmptyResume', 'afterFailResume', 'final', 'rawPending', 'rawAfterEmptyResume', 'rawAfterFailResume', 'calls', 'preservation', 'tokens')) {
            if ($name -like 'raw*') { $policyProjection[$name] = Get-RefinementRawProjection (Get-Field $policy $name) }
            else { $policyProjection[$name] = Get-Field $policy $name }
        }
        $stats = Get-Field $policy 'stats'
        $stableStats = [ordered]@{}
        foreach ($name in @('utf8InputBytes','utf16StagingBytes','inputImportBytes','publicationCopyBytes','beginPublicationCopyBytes','resumeRootImportBytes','deepCopyBytes','moveBytes','returnedOutputDescriptors','handlerInvocations','handlerFailures','pendingMailboxes','liveRetainedRoots','outstandingScratchLeases','pinnedScratchSlots','pinnedScratchBytes')) {
            $stableStats[$name] = Get-Field $stats $name
        }
        $policyProjection.stats = $stableStats
        $policies[$policyName] = $policyProjection
    }
    return [ordered]@{ passed = Get-Field $Native 'passed'; failureCount = Get-Field $Native 'failureCount'; checks = Get-Field $Native 'checks'; abi = Get-Field $Native 'abi'; direct = Get-Field $Native 'direct'; policies = $policies }
}

function Compare-RefinementBehavior($Left, $Right, [string]$Label) {
    $leftJson = ConvertTo-Json -InputObject (Get-RefinementBehaviorProjection $Left) -Depth 90 -Compress
    $rightJson = ConvertTo-Json -InputObject (Get-RefinementBehaviorProjection $Right) -Depth 90 -Compress
    Add-Check $Label ([string]::Equals($leftJson, $rightJson, [StringComparison]::Ordinal))
}

function Resolve-SumTypeNamesByIrType($Bootstrap) {
    $layouts = @(Get-Field $Bootstrap 'layouts')
    $typeNamesByIrType = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    $seenTypeNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($layout in $layouts) {
        $irType = [string](Get-Field $layout 'irType')
        $typeName = [string](Get-Field $layout 'typeName')
        if ([string]::IsNullOrWhiteSpace($irType) -or [string]::IsNullOrWhiteSpace($typeName)) {
            throw 'Every sum bootstrap layout must expose non-empty irType and typeName values.'
        }
        if ($typeNamesByIrType.ContainsKey($irType) -or -not $seenTypeNames.Add($typeName)) {
            throw "Sum bootstrap layout IR type names must map one-to-one; duplicate '$irType' or '$typeName' was found."
        }
        $typeNamesByIrType.Add($irType, $typeName)
    }
    return $typeNamesByIrType
}

function Convert-SumIrTypeToSourceName([string]$IrType, $TypeNamesByIrType) {
    $typeName = $IrType.Trim()
    if ($TypeNamesByIrType.ContainsKey($typeName)) { return $TypeNamesByIrType[$typeName] }
    return [regex]::Replace($typeName, '@type[0-9]+', {
        param($match)
        if (-not $TypeNamesByIrType.ContainsKey($match.Value)) {
            throw "Sum bootstrap type '$typeName' refers to '$($match.Value)' without a unique layout typeName."
        }
        return $TypeNamesByIrType[$match.Value]
    })
}

function Compare-SumBank {
    param(
        $Actual,
        [bool]$Pending,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$ExpectedRoots,
        [string]$Label,
        $Fixture
    )
    $roots = @(Get-Field $Actual 'roots')
    $okay = $null -ne $Actual -and
        (Has-Field $Actual 'pending') -and (Has-Field $Actual 'rootCount') -and
        (Has-Field $Actual 'usedBytes') -and (Has-Field $Actual 'roots') -and
        [bool](Get-Field $Actual 'pending') -eq $Pending -and
        [int](Get-Field $Actual 'rootCount') -eq $ExpectedRoots.Count -and
        $roots.Count -eq $ExpectedRoots.Count
    $typeIds = Get-Field (Get-Field $Fixture 'sourceDerivedTypeIds') 'typeIds'
    $offset = [uint32]0
    for ($index = 0; $index -lt [Math]::Min($roots.Count, $ExpectedRoots.Count); $index++) {
        $observed = $roots[$index]
        $expected = $ExpectedRoots[$index]
        $expectedTypeName = [string](Get-Field $expected 'type')
        $expectedTypeId = Get-Field $typeIds $expectedTypeName
        $rootOkay = $null -ne $expectedTypeId -and
            (Has-Field $observed 'typeId') -and (Has-Field $observed 'offsetBytes') -and
            (Has-Field $observed 'extentBytes') -and (Has-Field $observed 'payloadBytes') -and
            (Has-Field $observed 'ownerEndBytes') -and (Has-Field $observed 'serializedHex') -and
            [uint32](Get-Field $observed 'typeId') -eq [uint32]$expectedTypeId -and
            [uint32](Get-Field $observed 'offsetBytes') -eq $offset -and
            [uint32](Get-Field $observed 'extentBytes') -eq [uint32](Get-Field $expected 'extentBytes') -and
            [uint32](Get-Field $observed 'payloadBytes') -eq [uint32](Get-Field $expected 'payloadBytes') -and
            [uint32](Get-Field $observed 'ownerEndBytes') -eq ($offset + [uint32](Get-Field $expected 'extentBytes')) -and
            [string](Get-Field $observed 'serializedHex') -ceq [string](Get-Field $expected 'serializedHex')
        Add-Check "$Label root $index matches independent type, logical payload, extent, owner range, and bytes" $rootOkay ([ordered]@{ expected = $expected; actual = $observed })
        if (-not $rootOkay) { $okay = $false }
        $offset += [uint32](Get-Field $expected 'extentBytes')
    }
    $okay = $okay -and [uint32](Get-Field $Actual 'usedBytes') -eq $offset
    Add-Check "$Label bank pending state, root count, and used extent match the fixture" $okay ([ordered]@{ pending = Get-Field $Actual 'pending'; rootCount = Get-Field $Actual 'rootCount'; usedBytes = Get-Field $Actual 'usedBytes'; expectedUsedBytes = $offset; roots = $roots })
}

function Compare-SumStats($Actual, $Expected, [string]$PolicyLabel) {
    $fields = @(
        'utf8InputBytes', 'utf16StagingBytes', 'inputImportBytes',
        'publicationCopyBytes', 'beginPublicationCopyBytes', 'resumeRootImportBytes',
        'deepCopyBytes', 'moveBytes', 'returnedOutputDescriptors',
        'handlerInvocations', 'handlerFailures'
    )
    foreach ($field in $fields) {
        $hasActual = Has-Field $Actual $field
        $hasExpected = Has-Field $Expected $field
        $actualValue = if ($hasActual) { [uint64](Get-Field $Actual $field) } else { [uint64]::MaxValue }
        $expectedValue = if ($hasExpected) { [uint64](Get-Field $Expected $field) } else { [uint64]::MaxValue }
        Add-Check "$PolicyLabel $field equals independent fixture accounting" ($hasActual -and $hasExpected -and $actualValue -eq $expectedValue) ([ordered]@{ expected = if ($hasExpected) { Get-Field $Expected $field } else { $null }; actual = if ($hasActual) { Get-Field $Actual $field } else { $null } })
    }
    Add-Check "$PolicyLabel reports no payload movement" ((Has-Field $Actual 'moveBytes') -and [uint64](Get-Field $Actual 'moveBytes') -eq 0) $Actual
}

function Get-InspectedRecordField($Value, [string]$FieldName) {
    $fields = @(Get-Field $Value 'fields')
    $matches = @($fields | Where-Object { [string](Get-Field $_ 'name') -ceq $FieldName })
    if ($matches.Count -ne 1) { return $null }
    return Get-Field $matches[0] 'value'
}

function Check-SumInterpreterJson([string]$Json, [string]$Description, [int]$ExpectedRootCount, [string]$ExpectedResultCase, [string]$ExpectedResultText, [string]$ExpectedOptionCase, [string]$ExpectedOptionText) {
    $tree = ConvertFrom-JsonText $Json $Description
    $values = @(Get-Field $tree 'values')
    $shapeOkay = (Get-Field $tree 'formatVersion') -eq 1 -and $values.Count -eq $ExpectedRootCount
    Add-Check "$Description interpreter JSON contains the expected typed root count" $shapeOkay ([ordered]@{ expected = $ExpectedRootCount; actual = $values.Count })
    if ($values.Count -lt 1) {
        Add-Check "$Description interpreter State matches the expected Result case and text" $false
        if ($ExpectedRootCount -eq 2) { Add-Check "$Description interpreter Continuation matches the expected Option case and text" $false }
        return
    }
    $state = $values[0]
    $completion = Get-InspectedRecordField $state 'completion'
    $case = [string](Get-Field $completion 'case')
    $payload = Get-Field $completion 'value'
    if ($ExpectedResultCase -ceq 'ok') {
        $text = Get-InspectedRecordField $payload 'text'
        $payloadOkay = [string](Get-Field $payload 'name') -ceq 'Chunk'
    } else {
        $text = $payload
        $payloadOkay = $true
    }
    $stateOkay = [string](Get-Field $state 'kind') -ceq 'record' -and
        [string](Get-Field $state 'name') -ceq 'State' -and
        [string](Get-Field $completion 'kind') -ceq 'result' -and
        $case -ceq $ExpectedResultCase -and $payloadOkay -and
        [string](Get-Field $text 'kind') -ceq 'string' -and
        [string](Get-Field $text 'value') -ceq $ExpectedResultText
    Add-Check "$Description interpreter State matches the expected Result case and text" $stateOkay ([ordered]@{ expectedCase = $ExpectedResultCase; expectedText = $ExpectedResultText; actual = $state })
    if ($ExpectedRootCount -eq 2) {
        $continuation = $values[1]
        $pending = Get-InspectedRecordField $continuation 'pending'
        $optionCase = [string](Get-Field $pending 'case')
        $optionOkay = [string](Get-Field $continuation 'kind') -ceq 'record' -and
            [string](Get-Field $continuation 'name') -ceq 'Continuation' -and
            [string](Get-Field $pending 'kind') -ceq 'option' -and
            $optionCase -ceq $ExpectedOptionCase
        if ($ExpectedOptionCase -ceq 'some') {
            $optionText = Get-InspectedRecordField (Get-Field $pending 'value') 'text'
            $optionOkay = $optionOkay -and [string](Get-Field (Get-Field $pending 'value') 'name') -ceq 'Chunk' -and
                [string](Get-Field $optionText 'kind') -ceq 'string' -and
                [string](Get-Field $optionText 'value') -ceq $ExpectedOptionText
        } else {
            $optionOkay = $optionOkay -and -not (Has-Field $pending 'value')
        }
        Add-Check "$Description interpreter Continuation matches the expected Option case and text" $optionOkay ([ordered]@{ expectedCase = $ExpectedOptionCase; expectedText = $ExpectedOptionText; actual = $continuation })
    }
}

function Check-SumInterpreterOracles($Bootstrap, $Fixture, [string]$Label) {
    $oracle = Get-Field $Bootstrap 'interpreterOracle'
    $unicodeFixture = Get-Field (Get-Field $Fixture 'scenarios') 'unicodeSomeOk'
    $emptyFixture = Get-Field (Get-Field $Fixture 'scenarios') 'emptyNoneError'
    $unicode = Get-Field $oracle 'unicode'
    $empty = Get-Field $oracle 'empty'
    $error = Get-Field $oracle 'error'
    $errorRoundTrip = Get-Field $oracle 'errorRoundTrip'
    Check-SumInterpreterJson ([string](Get-Field $unicode 'initializedJson')) "$Label interpreter Unicode initialize" 1 'ok' ([string](Get-Field $unicodeFixture 'initializeValue')) '' ''
    Check-SumInterpreterJson ([string](Get-Field $unicode 'pendingJson')) "$Label interpreter Unicode pending" 2 'ok' ([string](Get-Field $unicodeFixture 'initializeValue')) 'some' ([string](Get-Field $unicodeFixture 'beginValue'))
    Check-SumInterpreterJson ([string](Get-Field $unicode 'completedJson')) "$Label interpreter Unicode completion" 1 'ok' ([string](Get-Field $unicodeFixture 'completionValue')) '' ''
    Check-SumInterpreterJson ([string](Get-Field $empty 'initializedJson')) "$Label interpreter empty initialize" 1 'ok' '' '' ''
    Check-SumInterpreterJson ([string](Get-Field $empty 'pendingJson')) "$Label interpreter empty pending" 2 'ok' '' 'none' ''
    Check-SumInterpreterJson ([string](Get-Field $empty 'completedJson')) "$Label interpreter empty completion" 1 'ok' 'B' '' ''
    Check-SumInterpreterJson ([string](Get-Field $error 'initializedJson')) "$Label interpreter Z initialize" 1 'ok' ([string](Get-Field $emptyFixture 'initializeValue')) '' ''
    Check-SumInterpreterJson ([string](Get-Field $error 'pendingJson')) "$Label interpreter Z with None pending" 2 'ok' ([string](Get-Field $emptyFixture 'initializeValue')) 'none' ''
    Check-SumInterpreterJson ([string](Get-Field $error 'completedJson')) "$Label interpreter Z/None/Error completion" 1 'error' ([string](Get-Field $emptyFixture 'resumeValue')) '' ''
    Check-SumInterpreterJson ([string](Get-Field $errorRoundTrip 'errorStateJson')) "$Label interpreter Error intermediate state" 1 'error' ([string](Get-Field $emptyFixture 'resumeValue')) '' ''
    Check-SumInterpreterJson ([string](Get-Field $errorRoundTrip 'followupPendingJson')) "$Label interpreter Error with None pending" 2 'error' ([string](Get-Field $emptyFixture 'resumeValue')) 'none' ''
    Check-SumInterpreterJson ([string](Get-Field $errorRoundTrip 'completedJson')) "$Label interpreter Error to Ok completion" 1 'ok' ([string](Get-Field $emptyFixture 'resumeValue')) '' ''
}

function Check-SumNativeEvidence($Native, $Fixture, [string]$Label) {
    Add-Check "$Label sum runner reports success" ((Get-Field $Native 'passed') -eq $true -and [int](Get-Field $Native 'failureCount') -eq 0)
    $expectedChecks = @(Get-Field $Fixture 'requiredChecks')
    $nativeChecks = @(Get-Field $Native 'checks')
    $names = @($nativeChecks | ForEach-Object { [string](Get-Field $_ 'name') })
    $checkSetOkay = $expectedChecks.Count -gt 0 -and $nativeChecks.Count -eq $expectedChecks.Count
    for ($index = 0; $index -lt [Math]::Min($expectedChecks.Count, $nativeChecks.Count); $index++) {
        $checkSetOkay = $checkSetOkay -and -not [string]::IsNullOrWhiteSpace($names[$index]) -and
            $names[$index] -ceq [string]$expectedChecks[$index] -and
            (Get-Field $nativeChecks[$index] 'passed') -eq $true
    }
    $uniqueNames = @($names | Select-Object -Unique)
    Add-Check "$Label emits every fixture-required native assertion exactly once, in order, with no empty or extra checks" ($checkSetOkay -and $uniqueNames.Count -eq $names.Count) ([ordered]@{ expectedCount = $expectedChecks.Count; actualCount = $nativeChecks.Count; names = $names; checks = $nativeChecks })

    $expectedAbi = Get-Field $Fixture 'abi'
    $actualAbi = Get-Field $Native 'abi'
    foreach ($abiField in @('moduleAbiVersion', 'moduleStructSizeBytes', 'layoutAbiVersion', 'stackAbiVersion', 'typeDescriptorSizeBytes', 'fieldDescriptorSizeBytes', 'layoutDescriptorSizeBytes')) {
        $present = (Has-Field $actualAbi $abiField) -and (Has-Field $expectedAbi $abiField)
        $matches = $present -and [uint64](Get-Field $actualAbi $abiField) -eq [uint64](Get-Field $expectedAbi $abiField)
        Add-Check "$Label ABI $abiField matches the independent ABI fixture" $matches ([ordered]@{ expected = Get-Field $expectedAbi $abiField; actual = Get-Field $actualAbi $abiField })
    }

    $policies = Get-Field $Native 'policies'
    $policyNames = @('RETURN', 'KEEP_ASSOCIATED')
    $policyKeys = if ($policies -is [Collections.IDictionary]) { @($policies.Keys | ForEach-Object { [string]$_ }) } else { @() }
    Add-Check "$Label returns both suspension policy results" ($policyKeys.Count -eq 2 -and @($policyNames | Where-Object { $policyKeys -cnotcontains $_ }).Count -eq 0) ([ordered]@{ expected = $policyNames; actual = $policyKeys })

    $scenarios = Get-Field $Fixture 'scenarios'
    $unicode = Get-Field $scenarios 'unicodeSomeOk'
    $empty = Get-Field $scenarios 'emptyNoneError'
    $errorToOk = Get-Field $scenarios 'errorToOkAfterError'
    $capacity = Get-Field $scenarios 'oneByteShortCapacity'
    $expectedStatsByPolicy = Get-Field (Get-Field $Fixture 'copyAccounting') 'expectedStatsBySuspensionPolicy'
    foreach ($policyName in $policyNames) {
        $policy = Get-Field $policies $policyName
        $policyFixture = Get-Field $expectedStatsByPolicy $policyName
        Compare-SumStats (Get-Field $policy 'stats') $policyFixture "$Label $policyName"
        $unicodeBankExpected = if ($policyName -ceq 'RETURN') { @(Get-Field $unicode 'beginRoots') } else { @((Get-Field $unicode 'initializeRoot')) }
        $unicodeAssociatedExpected = @(if ($policyName -ceq 'RETURN') { @() } else { @(Get-Field $unicode 'beginRoots') })
        $emptyBankExpected = if ($policyName -ceq 'RETURN') { @(Get-Field $empty 'beginRoots') } else { @((Get-Field $empty 'initializeRoot')) }
        $emptyAssociatedExpected = @(if ($policyName -ceq 'RETURN') { @() } else { @(Get-Field $empty 'beginRoots') })
        $errorToOkBankExpected = if ($policyName -ceq 'RETURN') { @(Get-Field $errorToOk 'beginRoots') } else { @((Get-Field $empty 'resumeRoot')) }
        $errorToOkAssociatedExpected = @(if ($policyName -ceq 'RETURN') { @() } else { @(Get-Field $errorToOk 'beginRoots') })
        $capacityRoots = @(Get-Field $capacity 'publishedRootsBeforeFailure')
        $capacityBankExpected = if ($policyName -ceq 'RETURN') { $capacityRoots } else { @($capacityRoots[0]) }
        $capacityAssociatedExpected = @(if ($policyName -ceq 'RETURN') { @() } else { $capacityRoots })

        $unicodePolicy = Get-Field $policy 'unicode'
        Compare-SumBank -Actual (Get-Field $unicodePolicy 'initialize') -Pending $false -ExpectedRoots @((Get-Field $unicode 'initializeRoot')) -Label "$Label $policyName Unicode initialize" -Fixture $Fixture
        Compare-SumBank -Actual (Get-Field $unicodePolicy 'suspendedBank') -Pending $true -ExpectedRoots $unicodeBankExpected -Label "$Label $policyName Unicode suspended bank" -Fixture $Fixture
        Compare-SumBank -Actual (Get-Field $unicodePolicy 'suspendedAssociated') -Pending $true -ExpectedRoots $unicodeAssociatedExpected -Label "$Label $policyName Unicode associated roots" -Fixture $Fixture
        Compare-SumBank -Actual (Get-Field $unicodePolicy 'beforeFailure') -Pending $true -ExpectedRoots @(Get-Field $unicode 'beginRoots') -Label "$Label $policyName Unicode before FAIL" -Fixture $Fixture
        Compare-SumBank -Actual (Get-Field $unicodePolicy 'afterFailure') -Pending $true -ExpectedRoots @(Get-Field $unicode 'beginRoots') -Label "$Label $policyName Unicode after FAIL" -Fixture $Fixture
        Compare-SumBank -Actual (Get-Field $unicodePolicy 'final') -Pending $false -ExpectedRoots @((Get-Field $unicode 'resumeRoot')) -Label "$Label $policyName Unicode final" -Fixture $Fixture
        Add-Check "$Label $policyName FAIL returns a failure status and preserves both active roots" ([uint32](Get-Field $unicodePolicy 'failStatus') -ne 0 -and (Get-Field $unicodePolicy 'failurePreserved') -eq $true) ([ordered]@{ status = Get-Field $unicodePolicy 'failStatus'; failurePreserved = Get-Field $unicodePolicy 'failurePreserved' })
        Add-Check "$Label $policyName malformed active tag and payload are rejected" ((Get-Field $unicodePolicy 'malformedTagRejected') -eq $true -and (Get-Field $unicodePolicy 'malformedPayloadRejected') -eq $true) ([ordered]@{ tagRejected = Get-Field $unicodePolicy 'malformedTagRejected'; payloadRejected = Get-Field $unicodePolicy 'malformedPayloadRejected' })

        $emptyPolicy = Get-Field $policy 'empty'
        Compare-SumBank -Actual (Get-Field $emptyPolicy 'initialize') -Pending $false -ExpectedRoots @((Get-Field $empty 'initializeRoot')) -Label "$Label $policyName empty initialize" -Fixture $Fixture
        Compare-SumBank -Actual (Get-Field $emptyPolicy 'beginBank') -Pending $true -ExpectedRoots $emptyBankExpected -Label "$Label $policyName empty begin bank" -Fixture $Fixture
        Compare-SumBank -Actual (Get-Field $emptyPolicy 'beginAssociated') -Pending $true -ExpectedRoots $emptyAssociatedExpected -Label "$Label $policyName empty associated roots" -Fixture $Fixture
        Compare-SumBank -Actual (Get-Field $emptyPolicy 'final') -Pending $false -ExpectedRoots @((Get-Field $empty 'resumeRoot')) -Label "$Label $policyName None/Error final" -Fixture $Fixture
        Compare-SumBank -Actual (Get-Field $emptyPolicy 'errorRoot') -Pending $false -ExpectedRoots @((Get-Field $empty 'resumeRoot')) -Label "$Label $policyName Error intermediate root" -Fixture $Fixture
        Compare-SumBank -Actual (Get-Field $emptyPolicy 'errorToOkBeginBank') -Pending $true -ExpectedRoots $errorToOkBankExpected -Label "$Label $policyName Error to Ok begin bank" -Fixture $Fixture
        Compare-SumBank -Actual (Get-Field $emptyPolicy 'errorToOkBeginAssociated') -Pending $true -ExpectedRoots $errorToOkAssociatedExpected -Label "$Label $policyName Error to Ok associated roots" -Fixture $Fixture
        Compare-SumBank -Actual (Get-Field $emptyPolicy 'errorToOkFinal') -Pending $false -ExpectedRoots @((Get-Field $errorToOk 'resumeRoot')) -Label "$Label $policyName Error to Ok final" -Fixture $Fixture
        Add-Check "$Label $policyName Error completion resumes through the Ok arm" ((Get-Field $emptyPolicy 'errorRoundTripOk') -eq $true)

        $capacityPolicy = Get-Field $policy 'capacity'
        Compare-SumBank -Actual (Get-Field $capacityPolicy 'initialize') -Pending $false -ExpectedRoots @($capacityRoots[0]) -Label "$Label $policyName capacity initialize" -Fixture $Fixture
        Compare-SumBank -Actual (Get-Field $capacityPolicy 'beginBank') -Pending $true -ExpectedRoots $capacityBankExpected -Label "$Label $policyName capacity begin bank" -Fixture $Fixture
        Compare-SumBank -Actual (Get-Field $capacityPolicy 'beginAssociated') -Pending $true -ExpectedRoots $capacityAssociatedExpected -Label "$Label $policyName capacity associated roots" -Fixture $Fixture
        Compare-SumBank -Actual (Get-Field $capacityPolicy 'beforeFailure') -Pending $true -ExpectedRoots $capacityRoots -Label "$Label $policyName before one-byte-short resume" -Fixture $Fixture
        Compare-SumBank -Actual (Get-Field $capacityPolicy 'failure') -Pending $true -ExpectedRoots $capacityRoots -Label "$Label $policyName one-byte-short failure" -Fixture $Fixture
        Compare-SumBank -Actual (Get-Field $capacityPolicy 'final') -Pending $false -ExpectedRoots @((Get-Field $capacity 'retryRoot')) -Label "$Label $policyName capacity retry final" -Fixture $Fixture
        Add-Check "$Label $policyName one-byte-short resume fails and preserves both active roots" ([uint32](Get-Field $capacityPolicy 'failureResult') -ne 0 -and (Get-Field $capacityPolicy 'failurePreserved') -eq $true) ([ordered]@{ status = Get-Field $capacityPolicy 'failureResult'; failurePreserved = Get-Field $capacityPolicy 'failurePreserved' })
    }
}

function Get-SumBehaviorProjection($Native) {
    return [ordered]@{
        passed = Get-Field $Native 'passed'
        failureCount = Get-Field $Native 'failureCount'
        abi = Get-Field $Native 'abi'
        policies = Get-Field $Native 'policies'
        checks = Get-Field $Native 'checks'
    }
}

function Compare-SumBehavior($Left, $Right, [string]$Label) {
    $leftJson = ConvertTo-Json -InputObject (Get-SumBehaviorProjection $Left) -Depth 90 -Compress
    $rightJson = ConvertTo-Json -InputObject (Get-SumBehaviorProjection $Right) -Depth 90 -Compress
    Add-Check $Label ([string]::Equals($leftJson, $rightJson, [StringComparison]::Ordinal))
}

try {
    [IO.Directory]::CreateDirectory($runDirectory) | Out-Null
    $script:tempDirectory = Join-Path $runDirectory 'repo-temp'
    [IO.Directory]::CreateDirectory($script:tempDirectory) | Out-Null
    $fixture = Read-JsonFile $fixturePath
    $sumsFixture = Read-JsonFile $sumsFixturePath
    $refinementsFixture = Read-JsonFile $refinementsFixturePath
    $report.sumsFixture = $sumsFixturePath
    foreach ($inputPath in $sourceInputPaths) {
        if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) { throw "Required acceptance input is missing: $inputPath" }
        $sourceInputBefore += [ordered]@{ path = [IO.Path]::GetFullPath($inputPath); sha256 = Get-Hash $inputPath }
    }

    $clangDefault = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\Llvm\x64\bin\clang.exe'
    $clang = Resolve-Executable 'AGENTLANG_LLVM_CLANG' $clangDefault 'clang.exe'
    $dotnetCommand = Get-Command -Name dotnet.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $dotnet = [IO.Path]::GetFullPath($dotnetCommand.Source)
    $report.compiler = [ordered]@{
        dotnet = $dotnet
        clang = $clang
        clangSha256 = Get-Hash $clang
        tempDirectory = $script:tempDirectory
        nativeSources = @($nativeSources | ForEach-Object { [IO.Path]::GetFullPath($_) })
        sumsNativeSources = @($sumsNativeSources | ForEach-Object { [IO.Path]::GetFullPath($_) })
    }

    $artifactsRoot = Join-Path $runDirectory 'dotnet-artifacts'
    $buildArguments = @('build', $projectPath, '--artifacts-path', $artifactsRoot, '--configuration', 'Release', '--verbosity', 'minimal', '-p:NuGetAudit=false')
    if ($SerialBuild) { $buildArguments += '-m:1' } else { $buildArguments += '-m:1' }
    $build = Invoke-CapturedProcess 'fresh-release-project-build' $dotnet $buildArguments $repo
    Require-ProcessSuccess $build 'Fresh Release build of the owning mailbox experiment succeeded'
    $assemblies = @(Get-ChildItem -LiteralPath (Join-Path $artifactsRoot 'bin') -Recurse -File -Filter 'AgentLang.OwningMailbox.dll' | Where-Object { $_.FullName -notmatch '[\\/]ref[\\/]' })
    Add-Check 'fresh isolated build produced exactly one executable bootstrap assembly' ($assemblies.Count -eq 1) ([ordered]@{ count = $assemblies.Count })
    if ($assemblies.Count -ne 1) { throw 'Expected exactly one non-reference AgentLang.OwningMailbox assembly in the fresh artifact root.' }
    $assemblyPath = $assemblies[0].FullName
    Add-Check 'bootstrap assembly came from this run artifact directory' ([IO.Path]::GetFullPath($assemblyPath).StartsWith([IO.Path]::GetFullPath($artifactsRoot), [StringComparison]::OrdinalIgnoreCase))

    if (-not $RefinementsOnly) {
    foreach ($optimization in @('O0', 'O2')) {
      foreach ($runtimeProfile in $runtimeProfiles) {
        $moduleLabel = "$optimization/$runtimeProfile"
        $moduleDirectory = Join-Path $runDirectory "module-$moduleLabel"
        [IO.Directory]::CreateDirectory($moduleDirectory) | Out-Null
        $moduleArguments = @($assemblyPath, $optimization, $moduleDirectory, $flowPath)
        if ($runtimeProfile -ceq 'trusted-generated') { $moduleArguments += @('--runtime-profile', 'trusted-generated') }
        $moduleProcess = Invoke-CapturedProcess "compile-module-$moduleLabel" $dotnet $moduleArguments $repo
        Require-ProcessSuccess $moduleProcess "Fresh $moduleLabel owning mailbox module compile succeeded"
        $bootstrap = ConvertFrom-JsonText $moduleProcess.stdout.Trim() "$moduleLabel module bootstrap"
        $modulePath = [string](Get-Field $bootstrap 'modulePath')
        $llvmIrPath = [string](Get-Field $bootstrap 'llvmIrPath')
        $metadataPath = [string](Get-Field $bootstrap 'metadataSourcePath')
        $manifestPath = [string](Get-Field $bootstrap 'manifestPath')
        foreach ($path in @($modulePath, $llvmIrPath, $metadataPath, $manifestPath)) {
            if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "$moduleLabel generated artifact is missing: $path" }
        }
        $manifest = Read-JsonFile $manifestPath
        $moduleInfo = Get-Field $manifest 'moduleInfo'
        Add-Check "$moduleLabel manifest separates module and layout contracts" (
            [int](Get-Field $moduleInfo 'formatVersion') -eq [int](Get-Field $fixture 'manifestFormatVersion') -and
            [int](Get-Field $moduleInfo 'layoutAbiVersion') -eq [int](Get-Field $fixture 'layoutAbiVersion') -and
            [int](Get-Field $moduleInfo 'typeDescriptorSizeBytes') -eq [int](Get-Field $fixture 'typeDescriptorSizeBytes')) ([ordered]@{
                formatVersion = Get-Field $moduleInfo 'formatVersion'
                layoutAbiVersion = Get-Field $moduleInfo 'layoutAbiVersion'
                typeDescriptorSizeBytes = Get-Field $moduleInfo 'typeDescriptorSizeBytes'
            })
        Add-Check "$moduleLabel manifest records the selected runtime profile" ([string](Get-Field $moduleInfo 'runtimeProfile') -ceq $runtimeProfile) ([ordered]@{ expected = $runtimeProfile; actual = Get-Field $moduleInfo 'runtimeProfile' })
        $associatedResume = Get-Field $moduleInfo 'associatedResume'
        $resumeEntry = @(Get-Field $moduleInfo 'entries' | Where-Object { [string](Get-Field $_ 'role') -ceq 'resume' })
        $expectedAssociated = Get-Field $fixture 'associatedResume'
        $associatedShapeMatches = $resumeEntry.Count -eq 1 -and
            [string](Get-Field $associatedResume 'functionSymbol') -ceq [string](Get-Field $expectedAssociated 'functionSymbol') -and
            [string](Get-Field $associatedResume 'entryFrameSymbol') -ceq [string](Get-Field $resumeEntry[0] 'entryFrameSymbol') -and
            (@(Get-Field $associatedResume 'inputTypeIndexes') -join ',') -ceq (@(Get-Field $resumeEntry[0] 'inputTypeIndexes') -join ',') -and
            (@(Get-Field $associatedResume 'outputTypeIndexes') -join ',') -ceq (@(Get-Field $resumeEntry[0] 'outputTypeIndexes') -join ',') -and
            [int](Get-Field $associatedResume 'callbackMetadataBytes') -gt 0
        Add-Check "$moduleLabel manifest exposes associated resume on the existing resume frame" $associatedShapeMatches $associatedResume
        Add-Check "$moduleLabel manifest retains ABI v1 with the appended callback descriptor" (
            [int](Get-Field $moduleInfo 'abiVersion') -eq [int](Get-Field $fixture 'moduleAbiVersion') -and
            [int](Get-Field $fixture 'moduleStructSizeBytes') -eq 144 -and
            [int](Get-Field $fixture 'associatedResumePointerOffsetBytes') -eq 136) ([ordered]@{ abiVersion = Get-Field $moduleInfo 'abiVersion'; moduleStructSizeBytes = Get-Field $fixture 'moduleStructSizeBytes'; associatedResumePointerOffsetBytes = Get-Field $fixture 'associatedResumePointerOffsetBytes' })
        $dlls = @(Get-ChildItem -LiteralPath $moduleDirectory -Recurse -File -Filter '*.dll')
        Add-Check "$moduleLabel compiler output contains one generated module DLL" ($dlls.Count -eq 1 -and [IO.Path]::GetFullPath($dlls[0].FullName) -ceq [IO.Path]::GetFullPath($modulePath)) ([ordered]@{ count = $dlls.Count; modulePath = $modulePath })
        Add-Check "$moduleLabel module roles share the verified program and three-entry surface" ((Get-Field $bootstrap 'sameVerifiedProgramInstance') -eq $true -and @(Get-Field $bootstrap 'entries').Count -eq 3)
        $sourceTypeEvidence = Get-Field $bootstrap 'sourceDerivedTypeIds'
        $sourceIds = Get-Field $sourceTypeEvidence 'typeIds'
        $expectedIds = Get-Field (Get-Field $fixture 'sourceDerivedTypeIds') 'typeIds'
        $idsMatch = $true
        foreach ($name in @('Continuation', 'State', 'String')) {
            if ([uint32](Get-Field $sourceIds $name) -ne [uint32](Get-Field $expectedIds $name)) { $idsMatch = $false }
        }
        Add-Check "$moduleLabel type IDs follow source-name-derived fixture ordering" $idsMatch $sourceIds
        $expectedLayoutOrder = @(Get-Field (Get-Field $fixture 'sourceDerivedTypeIds') 'reachableDescriptorOrder' | ForEach-Object { [string]$_ })
        $actualLayoutOrder = @(Get-Field $bootstrap 'layouts' | ForEach-Object { [string](Get-Field $_ 'typeName') })
        Add-Check "$moduleLabel reachable layout descriptors follow source-derived dense TypeId order" (($actualLayoutOrder -join ',') -ceq ($expectedLayoutOrder -join ',')) ([ordered]@{ expected = $expectedLayoutOrder; actual = $actualLayoutOrder })
        $expectedIndexes = Get-Field (Get-Field $fixture 'sourceDerivedTypeIds') 'typeIndexes'
        $expectedRoleIndexes = @(
            @{ role = 'initialize'; inputs = @([uint32](Get-Field $expectedIndexes 'String')); outputs = @([uint32](Get-Field $expectedIndexes 'State')) },
            @{ role = 'begin'; inputs = @([uint32](Get-Field $expectedIndexes 'State'), [uint32](Get-Field $expectedIndexes 'String')); outputs = @([uint32](Get-Field $expectedIndexes 'State'), [uint32](Get-Field $expectedIndexes 'Continuation')) },
            @{ role = 'resume'; inputs = @([uint32](Get-Field $expectedIndexes 'State'), [uint32](Get-Field $expectedIndexes 'Continuation'), [uint32](Get-Field $expectedIndexes 'String')); outputs = @([uint32](Get-Field $expectedIndexes 'State')) }
        )
        foreach ($expectedRole in $expectedRoleIndexes) {
            $roleEntry = @(Get-Field $bootstrap 'entries' | Where-Object { [string](Get-Field $_ 'role') -ceq $expectedRole.role })
            $roleIndexesMatch = $roleEntry.Count -eq 1
            if ($roleIndexesMatch) {
                $actualInputs = @(Get-Field $roleEntry[0] 'inputTypeIndexes' | ForEach-Object { [uint32]$_ })
                $actualOutputs = @(Get-Field $roleEntry[0] 'outputTypeIndexes' | ForEach-Object { [uint32]$_ })
                $roleIndexesMatch = (($actualInputs -join ',') -ceq ($expectedRole.inputs -join ',')) -and (($actualOutputs -join ',') -ceq ($expectedRole.outputs -join ','))
            }
            Add-Check "$moduleLabel $($expectedRole.role) descriptor indexes follow the independent fixture order" $roleIndexesMatch ([ordered]@{ expectedInputs = $expectedRole.inputs; expectedOutputs = $expectedRole.outputs; actualEntryCount = $roleEntry.Count; actualInputs = if ($roleEntry.Count -eq 1) { Get-Field $roleEntry[0] 'inputTypeIndexes' } else { @() }; actualOutputs = if ($roleEntry.Count -eq 1) { Get-Field $roleEntry[0] 'outputTypeIndexes' } else { @() } })
        }
        $bounds = Get-Field $bootstrap 'backendBounds'
        Add-Check "$moduleLabel reports generated frame and scanner bounds separately" (
            [uint64](Get-Field $bounds 'perFrameAllocaBytes') -gt 0 -and
            [uint64](Get-Field $bounds 'callbackMetadataPerEntryBytes') -gt 0 -and
            [uint64](Get-Field $bounds 'metadataPeakBoundBytes') -gt 0 -and
            [uint64](Get-Field $bounds 'layoutScannerScratchBytes') -gt 0 -and
            [uint64](Get-Field $bounds 'preflightSpanTableBytes') -ge 0 -and
            $null -eq (Get-Field $bounds 'controllerReservedStorageBytes')) $bounds
        $artifactPaths = @($modulePath, $llvmIrPath, $metadataPath, $manifestPath)
        $artifactHashes = @($artifactPaths | ForEach-Object { [ordered]@{ path = $_; bytes = (Get-Item -LiteralPath $_).Length; sha256 = Get-Hash $_ } })
        $moduleBuilds.Add([ordered]@{
            optimization = $optimization
            runtimeProfile = $runtimeProfile
            outputDirectory = $moduleDirectory
            bootstrap = $bootstrap
            manifest = $manifest
            artifacts = $artifactHashes
        })
      }
    }

    foreach ($runtimeProfile in $runtimeProfiles) {
        $o0Module = $moduleBuilds | Where-Object { $_.optimization -ceq 'O0' -and $_.runtimeProfile -ceq $runtimeProfile } | Select-Object -First 1
        $o2Module = $moduleBuilds | Where-Object { $_.optimization -ceq 'O2' -and $_.runtimeProfile -ceq $runtimeProfile } | Select-Object -First 1
        $semanticO0 = [ordered]@{ entries = Get-Field $o0Module.bootstrap 'entries'; layouts = Get-Field $o0Module.bootstrap 'layouts'; sourceDerivedTypeIds = Get-Field $o0Module.bootstrap 'sourceDerivedTypeIds'; diagnostics = Get-Field $o0Module.bootstrap 'diagnostics' }
        $semanticO2 = [ordered]@{ entries = Get-Field $o2Module.bootstrap 'entries'; layouts = Get-Field $o2Module.bootstrap 'layouts'; sourceDerivedTypeIds = Get-Field $o2Module.bootstrap 'sourceDerivedTypeIds'; diagnostics = Get-Field $o2Module.bootstrap 'diagnostics' }
        Add-Check "$runtimeProfile O0 and O2 module role, layout, source-type, and diagnostic metadata agree" ((ConvertTo-Json -InputObject $semanticO0 -Depth 60 -Compress) -ceq (ConvertTo-Json -InputObject $semanticO2 -Depth 60 -Compress))
    }

    $diagnosticO0 = $moduleBuilds | Where-Object { $_.optimization -ceq 'O0' -and $_.runtimeProfile -ceq 'diagnostic' } | Select-Object -First 1
    $trustedO0 = $moduleBuilds | Where-Object { $_.optimization -ceq 'O0' -and $_.runtimeProfile -ceq 'trusted-generated' } | Select-Object -First 1
    $diagnosticSemantic = [ordered]@{ entries = Get-Field $diagnosticO0.bootstrap 'entries'; layouts = Get-Field $diagnosticO0.bootstrap 'layouts'; sourceDerivedTypeIds = Get-Field $diagnosticO0.bootstrap 'sourceDerivedTypeIds'; diagnostics = Get-Field $diagnosticO0.bootstrap 'diagnostics' }
    $trustedSemantic = [ordered]@{ entries = Get-Field $trustedO0.bootstrap 'entries'; layouts = Get-Field $trustedO0.bootstrap 'layouts'; sourceDerivedTypeIds = Get-Field $trustedO0.bootstrap 'sourceDerivedTypeIds'; diagnostics = Get-Field $trustedO0.bootstrap 'diagnostics' }
    Add-Check 'diagnostic and trusted-generated module profiles preserve roles, layouts, source types, and diagnostics' ((ConvertTo-Json -InputObject $diagnosticSemantic -Depth 60 -Compress) -ceq (ConvertTo-Json -InputObject $trustedSemantic -Depth 60 -Compress))

    $expectedUnicode = [string](Get-Field (Get-Field (Get-Field $fixture 'lifecycle') 'unicode') 'resumeExpectedValue')
    foreach ($moduleBuild in $moduleBuilds) {
        $moduleLabel = "$($moduleBuild.optimization)/$($moduleBuild.runtimeProfile)"
        $oracle = Get-Field $moduleBuild.bootstrap 'interpreterOracle'
        $unicodeOracle = Get-Field $oracle 'unicode'
        $emptyOracle = Get-Field $oracle 'empty'
        foreach ($step in @(
            @{ field = 'initializedJson'; expected = 'A🙂' },
            @{ field = 'pendingJson'; expected = 'A🙂' },
            @{ field = 'pendingJson'; expected = 'δ' },
            @{ field = 'completedJson'; expected = $expectedUnicode }
        )) {
            $json = [string](Get-Field $unicodeOracle $step.field)
            $valueTree = ConvertFrom-JsonText $json "$moduleLabel interpreter $($step.field)"
            Add-Check "$moduleLabel interpreter $($step.field) contains fixture value '$($step.expected)'" (Contains-StringValue $valueTree ([string]$step.expected))
        }
        $emptyCompleted = ConvertFrom-JsonText ([string](Get-Field $emptyOracle 'completedJson')) "$moduleLabel empty interpreter completion"
        Add-Check "$moduleLabel interpreter executes empty lifecycle to B" (Contains-StringValue $emptyCompleted 'B')
    }

    $expectedSumTypeIds = Get-Field (Get-Field $sumsFixture 'sourceDerivedTypeIds') 'typeIds'
    $expectedSumRoles = Get-Field $sumsFixture 'roles'
    foreach ($optimization in @('O0', 'O2')) {
        foreach ($runtimeProfile in $runtimeProfiles) {
            $moduleLabel = "$optimization/$runtimeProfile sums"
            $moduleDirectory = Join-Path $runDirectory "module-sums-$optimization-$runtimeProfile"
            [IO.Directory]::CreateDirectory($moduleDirectory) | Out-Null
            $moduleArguments = @($assemblyPath, $optimization, $moduleDirectory, $sumsFlowPath)
            if ($runtimeProfile -ceq 'trusted-generated') { $moduleArguments += @('--runtime-profile', 'trusted-generated') }
            $moduleProcess = Invoke-CapturedProcess "compile-sums-module-$optimization-$runtimeProfile" $dotnet $moduleArguments $repo
            Require-ProcessSuccess $moduleProcess "Fresh $moduleLabel module compile succeeded"
            $bootstrap = ConvertFrom-JsonText $moduleProcess.stdout.Trim() "$moduleLabel module bootstrap"
            $modulePath = [string](Get-Field $bootstrap 'modulePath')
            $llvmIrPath = [string](Get-Field $bootstrap 'llvmIrPath')
            $metadataPath = [string](Get-Field $bootstrap 'metadataSourcePath')
            $manifestPath = [string](Get-Field $bootstrap 'manifestPath')
            foreach ($path in @($modulePath, $llvmIrPath, $metadataPath, $manifestPath)) {
                if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "$moduleLabel generated artifact is missing: $path" }
            }
            $manifest = Read-JsonFile $manifestPath
            $moduleInfo = Get-Field $manifest 'moduleInfo'
            $expectedAbi = Get-Field $sumsFixture 'abi'
            Add-Check "$moduleLabel manifest preserves module ABI 1, layout ABI 3, and type descriptor size" (
                [int](Get-Field $moduleInfo 'abiVersion') -eq [int](Get-Field $expectedAbi 'moduleAbiVersion') -and
                [int](Get-Field $moduleInfo 'layoutAbiVersion') -eq [int](Get-Field $expectedAbi 'layoutAbiVersion') -and
                [int](Get-Field $moduleInfo 'typeDescriptorSizeBytes') -eq [int](Get-Field $expectedAbi 'typeDescriptorSizeBytes')) ([ordered]@{
                    moduleAbiVersion = Get-Field $moduleInfo 'abiVersion'
                    layoutAbiVersion = Get-Field $moduleInfo 'layoutAbiVersion'
                    typeDescriptorSizeBytes = Get-Field $moduleInfo 'typeDescriptorSizeBytes'
                })
            Add-Check "$moduleLabel manifest records the selected runtime profile" ([string](Get-Field $moduleInfo 'runtimeProfile') -ceq $runtimeProfile) ([ordered]@{ expected = $runtimeProfile; actual = Get-Field $moduleInfo 'runtimeProfile' })
            Add-Check "$moduleLabel bootstrap uses one verified three-role program" ((Get-Field $bootstrap 'sameVerifiedProgramInstance') -eq $true -and @(Get-Field $bootstrap 'entries').Count -eq 3)

            $sourceIds = Get-Field (Get-Field $bootstrap 'sourceDerivedTypeIds') 'typeIds'
            $idsMatch = $true
            foreach ($typeName in $expectedSumTypeIds.Keys) {
                if (-not (Has-Field $sourceIds ([string]$typeName)) -or [uint32](Get-Field $sourceIds ([string]$typeName)) -ne [uint32](Get-Field $expectedSumTypeIds ([string]$typeName))) { $idsMatch = $false }
            }
            Add-Check "$moduleLabel source-derived nominal TypeIds match the independent sum fixture" $idsMatch $sourceIds

            $layoutIndexes = Resolve-SumLayoutIndexes $bootstrap
            Add-Check "$moduleLabel sum and nominal layout names resolve uniquely to emitted layoutIndex values" ($layoutIndexes.Count -eq 6) $layoutIndexes
            $typeNamesByIrType = Resolve-SumTypeNamesByIrType $bootstrap
            Add-Check "$moduleLabel layout irType values map one-to-one to source-facing type names" ($typeNamesByIrType.Count -eq @(Get-Field $bootstrap 'layouts').Count) $typeNamesByIrType
            $layoutFieldExpectations = @(
                @{ typeName = 'Chunk'; fieldName = 'text'; fieldType = 'String' },
                @{ typeName = 'Continuation'; fieldName = 'pending'; fieldType = [string](Get-Field $expectedSumRoles 'continuationField') },
                @{ typeName = 'State'; fieldName = 'completion'; fieldType = [string](Get-Field $expectedSumRoles 'stateField') }
            )
            foreach ($fieldExpectation in $layoutFieldExpectations) {
                $layout = @(Get-Field $bootstrap 'layouts' | Where-Object { [string](Get-Field $_ 'typeName') -ceq $fieldExpectation.typeName })
                $fields = if ($layout.Count -eq 1) { @(Get-Field $layout[0] 'fields') } else { @() }
                $field = @($fields | Where-Object { [string](Get-Field $_ 'fieldName') -ceq $fieldExpectation.fieldName })
                $actualFieldType = if ($field.Count -eq 1) { Convert-SumIrTypeToSourceName ([string](Get-Field $field[0] 'fieldType')) $typeNamesByIrType } else { $null }
                $fieldMatches = $field.Count -eq 1 -and $actualFieldType -ceq $fieldExpectation.fieldType
                Add-Check "$moduleLabel $($fieldExpectation.typeName).$($fieldExpectation.fieldName) layout retains the fixture sum type" $fieldMatches ([ordered]@{ expected = $fieldExpectation; actual = if ($field.Count -eq 1) { $field[0] }; normalizedFieldType = $actualFieldType })
            }
            foreach ($roleName in @('initialize', 'begin', 'resume')) {
                $roleFixture = Get-Field $expectedSumRoles $roleName
                $roleEntries = @(Get-Field $bootstrap 'entries' | Where-Object { [string](Get-Field $_ 'role') -ceq $roleName })
                $expectedInputs = @(Get-Field $roleFixture 'inputs' | ForEach-Object { [string]$_ })
                $expectedOutputs = @(Get-Field $roleFixture 'outputs' | ForEach-Object { [string]$_ })
                [object[]]$actualInputs = if ($roleEntries.Count -eq 1) { @(Get-Field $roleEntries[0] 'inputTypes' | ForEach-Object { Convert-SumIrTypeToSourceName ([string]$_) $typeNamesByIrType }) } else { @() }
                [object[]]$actualOutputs = if ($roleEntries.Count -eq 1) { @(Get-Field $roleEntries[0] 'outputTypes' | ForEach-Object { Convert-SumIrTypeToSourceName ([string]$_) $typeNamesByIrType }) } else { @() }
                $roleMatches = $roleEntries.Count -eq 1 -and ($actualInputs -join ',') -ceq ($expectedInputs -join ',') -and ($actualOutputs -join ',') -ceq ($expectedOutputs -join ',')
                Add-Check "$moduleLabel $roleName role signature matches independent sum metadata" $roleMatches ([ordered]@{ expectedInputs = $expectedInputs; expectedOutputs = $expectedOutputs; actualEntryCount = $roleEntries.Count; actualInputs = $actualInputs; actualOutputs = $actualOutputs })
            }

            Check-SumInterpreterOracles $bootstrap $sumsFixture $moduleLabel
            $artifactPaths = @($modulePath, $llvmIrPath, $metadataPath, $manifestPath)
            $artifactHashes = @($artifactPaths | ForEach-Object { [ordered]@{ path = $_; bytes = (Get-Item -LiteralPath $_).Length; sha256 = Get-Hash $_ } })
            $sumModuleBuilds.Add([ordered]@{
                optimization = $optimization
                runtimeProfile = $runtimeProfile
                outputDirectory = $moduleDirectory
                bootstrap = $bootstrap
                manifest = $manifest
                layoutIndexes = $layoutIndexes
                artifacts = $artifactHashes
                process = $moduleProcess
            })
        }
    }

    foreach ($runtimeProfile in $runtimeProfiles) {
        $o0SumModule = $sumModuleBuilds | Where-Object { $_.optimization -ceq 'O0' -and $_.runtimeProfile -ceq $runtimeProfile } | Select-Object -First 1
        $o2SumModule = $sumModuleBuilds | Where-Object { $_.optimization -ceq 'O2' -and $_.runtimeProfile -ceq $runtimeProfile } | Select-Object -First 1
        $semanticO0 = [ordered]@{ entries = Get-Field $o0SumModule.bootstrap 'entries'; layouts = Get-Field $o0SumModule.bootstrap 'layouts'; sourceDerivedTypeIds = Get-Field $o0SumModule.bootstrap 'sourceDerivedTypeIds'; diagnostics = Get-Field $o0SumModule.bootstrap 'diagnostics'; interpreterOracle = Get-Field $o0SumModule.bootstrap 'interpreterOracle' }
        $semanticO2 = [ordered]@{ entries = Get-Field $o2SumModule.bootstrap 'entries'; layouts = Get-Field $o2SumModule.bootstrap 'layouts'; sourceDerivedTypeIds = Get-Field $o2SumModule.bootstrap 'sourceDerivedTypeIds'; diagnostics = Get-Field $o2SumModule.bootstrap 'diagnostics'; interpreterOracle = Get-Field $o2SumModule.bootstrap 'interpreterOracle' }
        Add-Check "$runtimeProfile O0 and O2 sum roles, layouts, type IDs, diagnostics, and interpreter oracles agree" ((ConvertTo-Json -InputObject $semanticO0 -Depth 80 -Compress) -ceq (ConvertTo-Json -InputObject $semanticO2 -Depth 80 -Compress))
    }

    $clangVersion = Invoke-CapturedProcess 'clang-version' $clang @('--version') $runDirectory
    Require-ProcessSuccess $clangVersion 'Clang version query succeeded'
    foreach ($optimization in @('O0', 'O2')) {
        foreach ($executionProfile in $executionProfiles) {
            $profileName = [string]$executionProfile.name
            $module = $moduleBuilds | Where-Object { $_.optimization -ceq $optimization -and $_.runtimeProfile -ceq $executionProfile.runtimeProfile } | Select-Object -First 1
            if ($null -eq $module) { throw "No $optimization module was compiled for runtime profile $($executionProfile.runtimeProfile)." }
            $moduleLabel = "$optimization/$profileName"
            $runnerDirectory = Join-Path $runDirectory "native-$optimization-$profileName"
            [IO.Directory]::CreateDirectory($runnerDirectory) | Out-Null
            $runnerPath = Join-Path $runnerDirectory "native-owning-mailbox-$optimization-$profileName.exe"
            foreach ($source in $nativeSources) {
                if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Native owning-mailbox source is missing: $source" }
            }
            $compileArguments = @('--target=x86_64-pc-windows-msvc', '-std=c11', '-Wall', '-Wextra', '-Werror', "-$optimization", '-I', $nativeDirectory)
            if ($null -ne $executionProfile.define) { $compileArguments += [string]$executionProfile.define }
            $compileArguments += $nativeSources + @('-o', $runnerPath)
            $nativeBuild = Invoke-CapturedProcess "native-runner-build-$moduleLabel" $clang $compileArguments $runnerDirectory
            Require-ProcessSuccess $nativeBuild "$moduleLabel native owning-mailbox runner build succeeded"
            $nativeBuilds.Add([ordered]@{ optimization = $optimization; profile = $profileName; runtimeProfile = $executionProfile.runtimeProfile; resetProfile = $executionProfile.resetProfile; executable = $runnerPath; sha256 = Get-Hash $runnerPath; process = $nativeBuild })
            $nativeRun = Invoke-CapturedProcess "native-runner-run-$moduleLabel" $runnerPath @([string](Get-Field $module.bootstrap 'modulePath')) $runnerDirectory
            Require-ProcessSuccess $nativeRun "$moduleLabel native owning-mailbox lifecycle and boundary suite passed"
            $native = ConvertFrom-JsonText $nativeRun.stdout.Trim() "$moduleLabel native owning-mailbox result"
            Check-NativeEvidence $native $fixture $moduleLabel ([string]$executionProfile.runtimeProfile)
            Check-ResetTelemetry $native ([string]$executionProfile.resetProfile) $moduleLabel
            $nativeRuns.Add([ordered]@{ optimization = $optimization; profile = $profileName; runtimeProfile = $executionProfile.runtimeProfile; resetProfile = $executionProfile.resetProfile; modulePath = Get-Field $module.bootstrap 'modulePath'; process = $nativeRun; result = $native })
        }

        $diagnosticRun = $nativeRuns | Where-Object { $_.optimization -ceq $optimization -and $_.profile -ceq 'diagnostic' } | Select-Object -First 1
        $fastRun = $nativeRuns | Where-Object { $_.optimization -ceq $optimization -and $_.profile -ceq 'fast-reset' } | Select-Object -First 1
        $trustedRun = $nativeRuns | Where-Object { $_.optimization -ceq $optimization -and $_.profile -ceq 'trusted-generated' } | Select-Object -First 1
        Compare-NativeBehavior $diagnosticRun.result $fastRun.result "$optimization diagnostic and fast reset preserve output, status, and copy semantics"
        Compare-NativeBehavior $diagnosticRun.result $trustedRun.result "$optimization diagnostic and trusted-generated preserve output, status, and copy semantics"
        $storageConfig = Get-Field (Get-Field $fixture 'storage') 'focusedRunnerConfiguration'
        Compare-NativeStorageProfiles $diagnosticRun.result $fastRun.result $trustedRun.result $storageConfig $optimization
        Compare-NativeResetTelemetry $diagnosticRun.result $fastRun.result $trustedRun.result $optimization
    }

    $stackHeaderText = [IO.File]::ReadAllText($owningStackHeaderPath)
    $stackAbiMatch = [regex]::Match($stackHeaderText, '(?m)^\s*#define\s+AL_OWNING_STACK_ABI_VERSION\s+([0-9]+)u\b')
    $expectedStackAbi = [uint32](Get-Field (Get-Field $sumsFixture 'abi') 'stackAbiVersion')
    $headerStackAbi = if ($stackAbiMatch.Success) { [uint32]$stackAbiMatch.Groups[1].Value } else { [uint32]::MaxValue }
    Add-Check 'sum fixture stack ABI matches the owning runtime header' ($stackAbiMatch.Success -and $headerStackAbi -eq $expectedStackAbi) ([ordered]@{ expected = $expectedStackAbi; actual = if ($stackAbiMatch.Success) { $headerStackAbi } else { $null } })

    foreach ($optimization in @('O0', 'O2')) {
        foreach ($executionProfile in $executionProfiles) {
            $profileName = [string]$executionProfile.name
            $module = $sumModuleBuilds | Where-Object { $_.optimization -ceq $optimization -and $_.runtimeProfile -ceq $executionProfile.runtimeProfile } | Select-Object -First 1
            if ($null -eq $module) { throw "No $optimization sums module was compiled for runtime profile $($executionProfile.runtimeProfile)." }
            $moduleBootstrap = $module.bootstrap
            $moduleManifestInfo = Get-Field $module.manifest 'moduleInfo'
            $moduleLabel = "$optimization/$profileName sums"
            $runnerDirectory = Join-Path $runDirectory "native-sums-$optimization-$profileName"
            [IO.Directory]::CreateDirectory($runnerDirectory) | Out-Null
            $runnerPath = Join-Path $runnerDirectory "native-owning-mailbox-sums-$optimization-$profileName.exe"
            foreach ($source in $sumsNativeSources) {
                if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Native owning-mailbox sums source is missing: $source" }
            }
            $compileArguments = @('--target=x86_64-pc-windows-msvc', '-std=c11', '-Wall', '-Wextra', '-Werror', "-$optimization", '-I', $nativeDirectory)
            if ($null -ne $executionProfile.define) { $compileArguments += [string]$executionProfile.define }
            $compileArguments += $sumsNativeSources + @('-o', $runnerPath)
            $nativeBuild = Invoke-CapturedProcess "native-sums-runner-build-$moduleLabel" $clang $compileArguments $runnerDirectory
            Require-ProcessSuccess $nativeBuild "$moduleLabel native sums runner build succeeded"
            $sumNativeBuilds.Add([ordered]@{ optimization = $optimization; profile = $profileName; runtimeProfile = $executionProfile.runtimeProfile; resetProfile = $executionProfile.resetProfile; executable = $runnerPath; sha256 = Get-Hash $runnerPath; process = $nativeBuild })

            $layoutIndexes = $module.layoutIndexes
            $runArguments = @([string](Get-Field $moduleBootstrap 'modulePath'))
            foreach ($typeName in @('State', 'Continuation', 'String', 'Chunk', 'Option<Chunk>', 'Result<Chunk, String>')) {
                $layoutIndex = [uint32](Get-Field $layoutIndexes $typeName)
                $runArguments += $layoutIndex.ToString([Globalization.CultureInfo]::InvariantCulture)
            }
            $nativeRun = Invoke-CapturedProcess "native-sums-runner-run-$moduleLabel" $runnerPath $runArguments $runnerDirectory
            Require-ProcessSuccess $nativeRun "$moduleLabel native sums lifecycle and malformed-input suite passed"
            $native = ConvertFrom-JsonText $nativeRun.stdout.Trim() "$moduleLabel native sums result"
            Check-SumNativeEvidence $native $sumsFixture $moduleLabel
            Add-Check "$moduleLabel runner uses the matching generated module runtime profile" ([string](Get-Field $moduleManifestInfo 'runtimeProfile') -ceq [string]$executionProfile.runtimeProfile) ([ordered]@{ expected = $executionProfile.runtimeProfile; actual = Get-Field $moduleManifestInfo 'runtimeProfile' })
            $sumNativeRuns.Add([ordered]@{
                optimization = $optimization
                profile = $profileName
                runtimeProfile = $executionProfile.runtimeProfile
                resetProfile = $executionProfile.resetProfile
                modulePath = Get-Field $moduleBootstrap 'modulePath'
                layoutIndexes = $layoutIndexes
                process = $nativeRun
                result = $native
            })
        }

        $diagnosticSumRun = $sumNativeRuns | Where-Object { $_.optimization -ceq $optimization -and $_.profile -ceq 'diagnostic' } | Select-Object -First 1
        $fastSumRun = $sumNativeRuns | Where-Object { $_.optimization -ceq $optimization -and $_.profile -ceq 'fast-reset' } | Select-Object -First 1
        $trustedSumRun = $sumNativeRuns | Where-Object { $_.optimization -ceq $optimization -and $_.profile -ceq 'trusted-generated' } | Select-Object -First 1
        Compare-SumBehavior $diagnosticSumRun.result $fastSumRun.result "$optimization sums diagnostic and fast reset preserve both policy lifecycles, roots, statuses, and copy accounting"
        Compare-SumBehavior $diagnosticSumRun.result $trustedSumRun.result "$optimization sums diagnostic and trusted-generated preserve both policy lifecycles, roots, statuses, and copy accounting"
    }

    $expectedSumMatrix = @('O0/diagnostic', 'O0/fast-reset', 'O0/trusted-generated', 'O2/diagnostic', 'O2/fast-reset', 'O2/trusted-generated')
    $actualSumMatrix = @($sumNativeRuns | ForEach-Object { "$($_.optimization)/$($_.profile)" })
    $sumMatrixOkay = $actualSumMatrix.Count -eq $expectedSumMatrix.Count -and
        @($expectedSumMatrix | Where-Object { $actualSumMatrix -cnotcontains $_ }).Count -eq 0 -and
        @($sumNativeRuns | Where-Object { $_.process.exitCode -ne 0 -or $_.process.timedOut -or (Get-Field $_.result 'passed') -ne $true }).Count -eq 0
    Add-Check 'diagnostic, fast-reset, and trusted-generated host builds each run the sum lifecycle at O0 and O2' $sumMatrixOkay ([ordered]@{ expected = $expectedSumMatrix; actual = $actualSumMatrix })
    foreach ($profileName in @('diagnostic', 'fast-reset', 'trusted-generated')) {
        $o0Run = $sumNativeRuns | Where-Object { $_.optimization -ceq 'O0' -and $_.profile -ceq $profileName } | Select-Object -First 1
        $o2Run = $sumNativeRuns | Where-Object { $_.optimization -ceq 'O2' -and $_.profile -ceq $profileName } | Select-Object -First 1
        Compare-SumBehavior $o0Run.result $o2Run.result "$profileName O0 and O2 sum runner results agree for both suspension policies"
    }

    $diagnosticO0Runner = $nativeBuilds | Where-Object { $_.optimization -ceq 'O0' -and $_.profile -ceq 'diagnostic' } | Select-Object -First 1
    $trustedO0Runner = $nativeBuilds | Where-Object { $_.optimization -ceq 'O0' -and $_.profile -ceq 'trusted-generated' } | Select-Object -First 1
    $diagnosticO0Module = $moduleBuilds | Where-Object { $_.optimization -ceq 'O0' -and $_.runtimeProfile -ceq 'diagnostic' } | Select-Object -First 1
    $trustedO0Module = $moduleBuilds | Where-Object { $_.optimization -ceq 'O0' -and $_.runtimeProfile -ceq 'trusted-generated' } | Select-Object -First 1
    foreach ($control in @(
        [ordered]@{ name = 'diagnostic-host/diagnostic-module'; runner = $diagnosticO0Runner; module = $diagnosticO0Module },
        [ordered]@{ name = 'trusted-host/trusted-module'; runner = $trustedO0Runner; module = $trustedO0Module }
    )) {
        $controlRun = Invoke-CapturedProcess "profile-control-$($control.name)" ([string]$control.runner.executable) @([string](Get-Field $control.module.bootstrap 'modulePath'), '--profile-control') $runDirectory
        Require-ProcessSuccess $controlRun "$($control.name) valid associated callback positive control succeeded"
        $controlResult = ConvertFrom-JsonText $controlRun.stdout.Trim() "$($control.name) profile control result"
        $controlChecks = @(Get-Field $controlResult 'checks')
        $validFixture = @($controlChecks | Where-Object { (Get-Field $_ 'name') -ceq 'profile callback fixture contains valid empty State and Continuation roots' -and (Get-Field $_ 'passed') -eq $true }).Count -eq 1
        $acceptedCallback = @($controlChecks | Where-Object { (Get-Field $_ 'name') -ceq 'same-profile associated callback accepts valid empty State and Continuation roots' -and (Get-Field $_ 'passed') -eq $true }).Count -eq 1
        Add-Check "$($control.name) accepts the valid associated callback control fixture" ((Get-Field $controlResult 'passed') -eq $true -and [int](Get-Field $controlResult 'failureCount') -eq 0 -and $validFixture -and $acceptedCallback) $controlChecks
    }
    foreach ($mismatch in @(
        [ordered]@{ name = 'diagnostic-host/trusted-module'; runner = $diagnosticO0Runner; module = $trustedO0Module },
        [ordered]@{ name = 'trusted-host/diagnostic-module'; runner = $trustedO0Runner; module = $diagnosticO0Module }
    )) {
        $mismatchRun = Invoke-CapturedProcess "profile-mismatch-$($mismatch.name)" ([string]$mismatch.runner.executable) @([string](Get-Field $mismatch.module.bootstrap 'modulePath'), '--profile-mismatch') $runDirectory
        Require-ProcessSuccess $mismatchRun "$($mismatch.name) mismatch preflight rejected the incompatible module safely"
        $mismatchResult = ConvertFrom-JsonText $mismatchRun.stdout.Trim() "$($mismatch.name) profile mismatch result"
        $mismatchChecks = @(Get-Field $mismatchResult 'checks')
        $mismatchNames = @($mismatchChecks | ForEach-Object { [string](Get-Field $_ 'name') })
        $normalReject = @($mismatchChecks | Where-Object { (Get-Field $_ 'name') -ceq 'cross-profile module is rejected before stack writes and preserves caller outputs' -and (Get-Field $_ 'passed') -eq $true }).Count -eq 1
        $validFixture = @($mismatchChecks | Where-Object { (Get-Field $_ 'name') -ceq 'profile callback fixture contains valid empty State and Continuation roots' -and (Get-Field $_ 'passed') -eq $true }).Count -eq 1
        $associatedReject = @($mismatchChecks | Where-Object { (Get-Field $_ 'name') -ceq 'cross-profile associated callback rejects before writes and preserves valid active roots' -and (Get-Field $_ 'passed') -eq $true }).Count -eq 1
        Add-Check "$($mismatch.name) rejects before outputs, stack, cursor, or valid active roots mutate" ((Get-Field $mismatchResult 'passed') -eq $true -and [int](Get-Field $mismatchResult 'failureCount') -eq 0 -and $normalReject -and $validFixture -and $associatedReject) ([ordered]@{ names = $mismatchNames; checks = $mismatchChecks })
    }

    $readobj = Join-Path ([IO.Path]::GetDirectoryName($clang)) 'llvm-readobj.exe'
    $nm = Join-Path ([IO.Path]::GetDirectoryName($clang)) 'llvm-nm.exe'
    if (Test-Path -LiteralPath $readobj -PathType Leaf) {
        $moduleFiles = @($moduleBuilds | ForEach-Object { [string](Get-Field $_.bootstrap 'modulePath') }) + @($sumModuleBuilds | ForEach-Object { [string](Get-Field $_.bootstrap 'modulePath') })
        $runnerFiles = @($nativeBuilds | ForEach-Object { [string]$_.executable }) + @($sumNativeBuilds | ForEach-Object { [string]$_.executable })
        $runnerAllocatorImports = [Collections.Generic.List[object]]::new()
        foreach ($file in $moduleFiles + $runnerFiles) {
            $imports = Invoke-CapturedProcess "pe-import-audit-$([IO.Path]::GetFileName($file))" $readobj @('--coff-imports', $file) $runDirectory
            Require-ProcessSuccess $imports "PE import audit succeeded for $([IO.Path]::GetFileName($file))"
            $noClr = $imports.stdout -notmatch '(?i)mscoree|coreclr|clrjit|hostfxr|hostpolicy|mscorlib|system\.private\.corelib'
            Add-Check "$([IO.Path]::GetFileName($file)) has no managed-runtime imports" $noClr
            if ($moduleFiles -ccontains $file) {
                $noAllocator = $imports.stdout -notmatch '(?i)(HeapAlloc|HeapReAlloc|HeapFree|malloc|calloc|realloc|_aligned_malloc)'
                Add-Check "$([IO.Path]::GetFileName($file)) has no native heap allocator imports" $noAllocator
            } else {
                foreach ($match in [regex]::Matches($imports.stdout, '(?im)^\s*Symbol:\s*((?:HeapAlloc|HeapReAlloc|HeapFree|malloc|calloc|realloc|_aligned_malloc))\b')) {
                    $runnerAllocatorImports.Add([ordered]@{ file = [IO.Path]::GetFileName($file); symbol = $match.Groups[1].Value })
                }
            }
        }
        $report.nativeAllocationAudit = [ordered]@{
            runnerExecutableAllocatorImports = @($runnerAllocatorImports)
            runnerImportInterpretation = 'Windows CRT startup and console imports are recorded as harness overhead; runner PE imports do not establish turn allocation behavior.'
            harnessSourceReview = 'Caller storage is reserved with VirtualAlloc during runtime-fixture setup before handler API calls. printf/fprintf serialize evidence after test routines return. These harness operations are outside generated callback execution.'
            productAudit = 'Generated module and product-object audits report direct allocator references only; harness setup is excluded. This static symbol audit is not a dynamic process-wide allocation measurement.'
        }

        if (Test-Path -LiteralPath $nm -PathType Leaf) {
            $allocatorNamePattern = '(?i)^(?:__imp_)?_?(?:malloc|calloc|realloc|free|HeapAlloc|HeapReAlloc|HeapFree|aligned_malloc)$'
            foreach ($optimization in @('O0', 'O2')) {
                $objectDirectory = Join-Path $runDirectory "runtime-objects-$optimization"
                [IO.Directory]::CreateDirectory($objectDirectory) | Out-Null
                $allocatorSymbols = [Collections.Generic.List[object]]::new()
                $symbolAudits = [Collections.Generic.List[object]]::new()
                $unparsedSymbolLines = [Collections.Generic.List[object]]::new()
                foreach ($source in $owningRuntimeObjectSources) {
                    $objectPath = Join-Path $objectDirectory ("{0}-{1}.obj" -f [IO.Path]::GetFileNameWithoutExtension($source), $optimization)
                    $objectArguments = @('--target=x86_64-pc-windows-msvc', '-std=c11', '-Wall', '-Wextra', '-Werror', "-$optimization", '-I', $nativeDirectory, '-c', $source, '-o', $objectPath)
                    $objectBuild = Invoke-CapturedProcess "owning-runtime-object-build-$optimization-$([IO.Path]::GetFileName($source))" $clang $objectArguments $objectDirectory
                    Require-ProcessSuccess $objectBuild "$optimization owning runtime object compiled for $([IO.Path]::GetFileName($source))"
                    $objectSymbols = Invoke-CapturedProcess "owning-runtime-object-symbols-$optimization-$([IO.Path]::GetFileName($source))" $nm @('-u', $objectPath) $objectDirectory
                    Require-ProcessSuccess $objectSymbols "$optimization undefined-symbol audit succeeded for $([IO.Path]::GetFileName($source))"
                    $undefinedSymbols = [Collections.Generic.List[string]]::new()
                    $objectAllocatorSymbols = [Collections.Generic.List[string]]::new()
                    foreach ($line in ($objectSymbols.stdout -split "`r?`n")) {
                        if ([string]::IsNullOrWhiteSpace($line)) { continue }
                        if ($line -match '^\s*U\s+(\S+)\s*$') {
                            $undefinedSymbol = $Matches[1]
                            $undefinedSymbols.Add($undefinedSymbol)
                            if ($undefinedSymbol -match $allocatorNamePattern) {
                                $objectAllocatorSymbols.Add($undefinedSymbol)
                                $allocatorSymbols.Add([ordered]@{ source = $source; symbol = $undefinedSymbol })
                            }
                        } else {
                            $unparsedSymbolLines.Add([ordered]@{ source = $source; line = $line })
                        }
                    }
                    $symbolAudits.Add([ordered]@{ source = $source; undefinedSymbolCount = $undefinedSymbols.Count; undefinedSymbols = @($undefinedSymbols); allocatorSymbols = @($objectAllocatorSymbols); rawOutput = $objectSymbols.stdout })
                }
                $symbolOutputParsed = $unparsedSymbolLines.Count -eq 0
                Add-Check "$optimization fresh owning runtime objects have parsed symbol tables with no direct allocator references" ($symbolOutputParsed -and $allocatorSymbols.Count -eq 0) ([ordered]@{ objects = @($symbolAudits); forbiddenSymbols = @($allocatorSymbols); unparsedLines = @($unparsedSymbolLines) })
                $report.nativeAllocationAudit["$optimization`Objects"] = @($symbolAudits)
            }
        } else {
            Add-Check 'llvm-nm is available for fresh owning runtime allocator-symbol audit' $false $nm
        }
    } else {
        Add-Check 'llvm-readobj is available for native import audit' $false $readobj
    }
    }

    [IO.Directory]::CreateDirectory($refinementsRunDirectory) | Out-Null
    $runDirectory = $refinementsRunDirectory
    $refinementsStarted = $true
    $script:tempDirectory = Join-Path $refinementsRunDirectory 'repo-temp'
    [IO.Directory]::CreateDirectory($script:tempDirectory) | Out-Null
    $refinementFirstCheck = $checks.Count
    $refinementModuleMap = @{}
    foreach ($optimization in @('O0', 'O2')) {
        foreach ($runtimeProfile in $runtimeProfiles) {
            $moduleLabel = "$optimization/$runtimeProfile refined"
            $moduleDirectory = Join-Path $refinementsRunDirectory "module-refined-$optimization-$runtimeProfile"
            [IO.Directory]::CreateDirectory($moduleDirectory) | Out-Null
            $moduleArguments = @($assemblyPath, $optimization, $moduleDirectory, $refinementsFlowPath)
            if ($runtimeProfile -ceq 'trusted-generated') { $moduleArguments += @('--runtime-profile', 'trusted-generated') }
            $moduleProcess = Invoke-CapturedProcess "compile-refined-module-$optimization-$runtimeProfile" $dotnet $moduleArguments $repo
            Require-ProcessSuccess $moduleProcess "Fresh $moduleLabel module compile succeeded"
            $bootstrap = ConvertFrom-JsonText $moduleProcess.stdout.Trim() "$moduleLabel module bootstrap"
            $modulePath = [string](Get-Field $bootstrap 'modulePath')
            $manifestPath = [string](Get-Field $bootstrap 'manifestPath')
            $llvmIrPath = [string](Get-Field $bootstrap 'llvmIrPath')
            $metadataPath = [string](Get-Field $bootstrap 'metadataSourcePath')
            foreach ($path in @($modulePath, $manifestPath, $llvmIrPath, $metadataPath)) {
                if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "$moduleLabel generated artifact is missing: $path" }
            }
            $manifest = Read-JsonFile $manifestPath
            $moduleInfo = Get-Field $manifest 'moduleInfo'
            $expectedManifestAbi = [ordered]@{
                abiVersion = Get-Field $refinementsFixture 'moduleAbiVersion'
                layoutAbiVersion = Get-Field $refinementsFixture 'layoutAbiVersion'
            }
            $manifestOkay = [uint32](Get-Field $moduleInfo 'abiVersion') -eq [uint32]$expectedManifestAbi.abiVersion -and
                [uint32](Get-Field $moduleInfo 'layoutAbiVersion') -eq [uint32]$expectedManifestAbi.layoutAbiVersion -and
                [string](Get-Field $moduleInfo 'runtimeProfile') -ceq $runtimeProfile
            Add-Check "$moduleLabel manifest keeps the frozen module/layout ABI and selected profile" $manifestOkay ([ordered]@{ expected = $expectedManifestAbi; actual = $moduleInfo })

            $layoutIndexes = Resolve-RefinementLayoutIndexes $bootstrap
            $typeInfo = Get-Field (Get-Field $refinementsFixture 'sourceDerivedTypeIds') 'typeIds'
            $actualTypeIds = Get-Field (Get-Field $bootstrap 'sourceDerivedTypeIds') 'typeIds'
            $typeIdsOkay = $true
            foreach ($typeName in @('Continuation', 'NonEmptyString', 'PositiveId', 'State', 'String')) {
                $typeIdsOkay = $typeIdsOkay -and [uint32](Get-Field $actualTypeIds $typeName) -eq [uint32](Get-Field $typeInfo $typeName)
            }
            Add-Check "$moduleLabel nominal TypeIds match the independent source-name oracle" $typeIdsOkay ([ordered]@{ expected = $typeInfo; actual = $actualTypeIds })

            $expectedLayoutInfo = Get-Field (Get-Field $refinementsFixture 'sourceDerivedTypeIds') 'layoutIndexesFromSourceCompilation'
            $actualLayouts = @(Get-Field $bootstrap 'layouts')
            $expectedLayoutOrder = @(Get-Field (Get-Field $refinementsFixture 'sourceDerivedTypeIds') 'reachableDescriptorOrder' | ForEach-Object { [string]$_ })
            $actualLayoutOrder = @($actualLayouts | ForEach-Object { [string](Get-Field $_ 'typeName') })
            $layoutOkay = ($actualLayoutOrder -join ',') -ceq ($expectedLayoutOrder -join ',')
            foreach ($layout in $actualLayouts) {
                $typeName = [string](Get-Field $layout 'typeName')
                if (Has-Field $expectedLayoutInfo $typeName) {
                    $layoutOkay = $layoutOkay -and [uint32](Get-Field $layout 'layoutIndex') -eq [uint32](Get-Field $expectedLayoutInfo $typeName)
                }
            }
            foreach ($typeName in @('State', 'Continuation', 'String', 'Option<NonEmptyString>', 'Option<PositiveId>', 'Result<PositiveId, NonEmptyString>')) {
                $layoutOkay = $layoutOkay -and [uint32](Get-Field $layoutIndexes $typeName) -eq [uint32](Get-Field $expectedLayoutInfo $typeName)
            }
            Add-Check "$moduleLabel reachable descriptor order and emitted dense indexes match the frozen oracle" $layoutOkay ([ordered]@{ expectedOrder = $expectedLayoutOrder; actualOrder = $actualLayoutOrder; expectedIndexes = $expectedLayoutInfo; runnerIndexes = $layoutIndexes })

            $roleFixtures = Get-Field $refinementsFixture 'roles'
            $roleInputs = @(
                @{ role = 'initialize'; inputNames = @('String'); outputNames = @('State') },
                @{ role = 'begin'; inputNames = @('State', 'String'); outputNames = @('State', 'Continuation') },
                @{ role = 'resume'; inputNames = @('State', 'Continuation', 'String'); outputNames = @('State') })
            $rolesOkay = (Get-Field $bootstrap 'sameVerifiedProgramInstance') -eq $true -and @(Get-Field $bootstrap 'entries').Count -eq 3
            foreach ($role in $roleInputs) {
                $entry = @(Get-Field $bootstrap 'entries' | Where-Object { [string](Get-Field $_ 'role') -ceq $role.role })
                $fixtureRole = Get-Field $roleFixtures $role.role
                $entryOkay = $entry.Count -eq 1 -and
                    (@(Get-Field $fixtureRole 'inputs') -join ',') -ceq ($role.inputNames -join ',') -and
                    (@(Get-Field $fixtureRole 'outputs') -join ',') -ceq ($role.outputNames -join ',')
                if ($entryOkay) {
                    $expectedInputs = @($role.inputNames | ForEach-Object { [uint32](Get-Field $layoutIndexes $_) })
                    $expectedOutputs = @($role.outputNames | ForEach-Object { [uint32](Get-Field $layoutIndexes $_) })
                    $actualInputs = @(Get-Field $entry[0] 'inputTypeIndexes' | ForEach-Object { [uint32]$_ })
                    $actualOutputs = @(Get-Field $entry[0] 'outputTypeIndexes' | ForEach-Object { [uint32]$_ })
                    $entryOkay = ($actualInputs -join ',') -ceq ($expectedInputs -join ',') -and ($actualOutputs -join ',') -ceq ($expectedOutputs -join ',')
                }
                $rolesOkay = $rolesOkay -and $entryOkay
            }
            Add-Check "$moduleLabel uses the fixture roles and emitted layout indexes in the three entry signatures" $rolesOkay ([ordered]@{ expected = $roleFixtures; actual = Get-Field $bootstrap 'entries' })
            $oracleOkay = Test-RefinementOracle $bootstrap $refinementsFixture $moduleLabel

            $artifactPaths = @($modulePath, $llvmIrPath, $metadataPath, $manifestPath)
            $artifactHashes = @($artifactPaths | ForEach-Object { [ordered]@{ path = $_; bytes = (Get-Item -LiteralPath $_).Length; sha256 = Get-Hash $_ } })
            $moduleRecord = [ordered]@{
                optimization = $optimization
                runtimeProfile = $runtimeProfile
                outputDirectory = $moduleDirectory
                modulePath = $modulePath
                layoutIndexes = $layoutIndexes
                oraclePassed = $oracleOkay
                bootstrap = $bootstrap
                manifest = $manifest
                artifacts = $artifactHashes
                process = $moduleProcess
            }
            $refinementsModuleBuilds.Add($moduleRecord)
            $refinementModuleMap["$optimization/$runtimeProfile"] = $moduleRecord
        }
    }

    foreach ($optimization in @('O0', 'O2')) {
        foreach ($executionProfile in $executionProfiles) {
            $profileName = [string]$executionProfile.name
            $moduleRecord = $refinementModuleMap["$optimization/$($executionProfile.runtimeProfile)"]
            $moduleBootstrap = $moduleRecord.bootstrap
            $runnerDirectory = Join-Path $refinementsRunDirectory "native-refined-$optimization-$profileName"
            [IO.Directory]::CreateDirectory($runnerDirectory) | Out-Null
            foreach ($source in $refinementsNativeSources) {
                if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Native refined mailbox source is missing: $source" }
            }
            $runnerPath = Join-Path $runnerDirectory "native-owning-mailbox-refinements-$optimization-$profileName.exe"
            $compileArguments = @('--target=x86_64-pc-windows-msvc', '-std=c11', '-Wall', '-Wextra', '-Werror', "-$optimization", '-I', $nativeDirectory)
            if ($null -ne $executionProfile.define) { $compileArguments += [string]$executionProfile.define }
            $compileArguments += $refinementsNativeSources + @('-o', $runnerPath)
            $nativeBuild = Invoke-CapturedProcess "native-refined-runner-build-$optimization-$profileName" $clang $compileArguments $runnerDirectory
            Require-ProcessSuccess $nativeBuild "$optimization/$profileName refined runner build succeeded"
            $refinementsNativeBuilds.Add([ordered]@{ optimization = $optimization; profile = $profileName; runtimeProfile = $executionProfile.runtimeProfile; resetProfile = $executionProfile.resetProfile; executable = $runnerPath; sha256 = Get-Hash $runnerPath; process = $nativeBuild })

            $runArguments = @([string](Get-Field $moduleBootstrap 'modulePath'))
            foreach ($typeName in @('State', 'Continuation', 'String', 'Option<NonEmptyString>', 'Option<PositiveId>', 'Result<PositiveId, NonEmptyString>')) {
                $runArguments += ([uint32](Get-Field $moduleRecord.layoutIndexes $typeName)).ToString([Globalization.CultureInfo]::InvariantCulture)
            }
            $initializedState = Get-Field (Get-Field (Get-Field $refinementsFixture 'lifecycle') 'roots') 'initializedState'
            $initializedStateHex = [string](Get-Field $initializedState 'serializedHex')
            $runArguments += $initializedStateHex
            $nativeRun = Invoke-CapturedProcess "native-refined-runner-run-$optimization-$profileName" $runnerPath $runArguments $runnerDirectory
            Require-ProcessSuccess $nativeRun "$optimization/$profileName refined mailbox runner completed"
            $native = ConvertFrom-JsonText $nativeRun.stdout.Trim() "$optimization/$profileName refined mailbox result"
            Check-RefinementNativeEvidence $native $refinementsFixture $moduleBootstrap $moduleRecord.layoutIndexes "$optimization/$profileName refined"
            Add-Check "$optimization/$profileName runner uses the module with matching runtime profile" ([string](Get-Field (Get-Field $moduleRecord.manifest 'moduleInfo') 'runtimeProfile') -ceq [string]$executionProfile.runtimeProfile)
            $refinementsNativeRuns.Add([ordered]@{
                optimization = $optimization
                profile = $profileName
                runtimeProfile = $executionProfile.runtimeProfile
                resetProfile = $executionProfile.resetProfile
                modulePath = Get-Field $moduleBootstrap 'modulePath'
                layoutIndexes = $moduleRecord.layoutIndexes
                process = $nativeRun
                result = $native
            })
        }
        $diagnosticRun = $refinementsNativeRuns | Where-Object { $_.optimization -ceq $optimization -and $_.profile -ceq 'diagnostic' } | Select-Object -First 1
        $fastRun = $refinementsNativeRuns | Where-Object { $_.optimization -ceq $optimization -and $_.profile -ceq 'fast-reset' } | Select-Object -First 1
        $trustedRun = $refinementsNativeRuns | Where-Object { $_.optimization -ceq $optimization -and $_.profile -ceq 'trusted-generated' } | Select-Object -First 1
        Compare-RefinementBehavior $diagnosticRun.result $fastRun.result "$optimization diagnostic and fast-reset refined lifecycle behavior agrees"
        Compare-RefinementBehavior $diagnosticRun.result $trustedRun.result "$optimization diagnostic and trusted-generated refined lifecycle behavior agrees"
    }

    $expectedRefinementMatrix = @('O0/diagnostic', 'O0/fast-reset', 'O0/trusted-generated', 'O2/diagnostic', 'O2/fast-reset', 'O2/trusted-generated')
    $actualRefinementMatrix = @($refinementsNativeRuns | ForEach-Object { "$($_.optimization)/$($_.profile)" })
    $matrixOkay = $actualRefinementMatrix.Count -eq $expectedRefinementMatrix.Count -and
        @($expectedRefinementMatrix | Where-Object { $actualRefinementMatrix -cnotcontains $_ }).Count -eq 0 -and
        @($refinementsNativeRuns | Where-Object { $_.process.exitCode -ne 0 -or $_.process.timedOut -or (Get-Field $_.result 'passed') -ne $true }).Count -eq 0
    Add-Check 'O0/O2 diagnostic, fast-reset, and trusted-generated refined lifecycle matrix completed' $matrixOkay ([ordered]@{ expected = $expectedRefinementMatrix; actual = $actualRefinementMatrix })
    foreach ($profileName in @('diagnostic', 'fast-reset', 'trusted-generated')) {
        $o0Run = $refinementsNativeRuns | Where-Object { $_.optimization -ceq 'O0' -and $_.profile -ceq $profileName } | Select-Object -First 1
        $o2Run = $refinementsNativeRuns | Where-Object { $_.optimization -ceq 'O2' -and $_.profile -ceq $profileName } | Select-Object -First 1
        Compare-RefinementBehavior $o0Run.result $o2Run.result "$profileName refined runner behavior agrees between O0 and O2"
    }

    $refinementChecks = @($checks | Select-Object -Skip $refinementFirstCheck)
    $report.refinements = [ordered]@{
        fixture = $refinementsFixturePath
        flowSource = $refinementsFlowPath
        runnerSource = $refinementsRunnerSourcePath
        runDirectory = $refinementsRunDirectory
        modules = @($refinementsModuleBuilds)
        nativeBuilds = @($refinementsNativeBuilds)
        nativeRuns = @($refinementsNativeRuns)
        checks = $refinementChecks
        passed = $refinementChecks.Count -gt 0 -and @($refinementChecks | Where-Object { -not $_.passed }).Count -eq 0
    }
} catch {
    $errors.Add($_.Exception.ToString())
    Add-Check 'verification completed without an uncaught error' $false $_.Exception.Message
} finally {
    $sourceInputAfter = @()
    foreach ($path in $sourceInputPaths) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $sourceInputAfter += [ordered]@{ path = [IO.Path]::GetFullPath($path); sha256 = Get-Hash $path }
        } else {
            $sourceInputAfter += [ordered]@{ path = [IO.Path]::GetFullPath($path); sha256 = $null }
        }
    }
    $afterByPath = @{}
    foreach ($item in $sourceInputAfter) { $afterByPath[[string]$item.path] = [string]$item.sha256 }
    $changed = @($sourceInputBefore | Where-Object { -not $afterByPath.ContainsKey([string]$_.path) -or $afterByPath[[string]$_.path] -cne [string]$_.sha256 } | ForEach-Object { $_.path })
    $complete = $sourceInputBefore.Count -eq $sourceInputPaths.Count -and $sourceInputAfter.Count -eq $sourceInputPaths.Count
    Add-Check 'tracked compiler, runtime, fixture, and harness inputs stayed unchanged during run' ($complete -and $changed.Count -eq 0) ([ordered]@{ inputCount = $sourceInputPaths.Count; changed = $changed })
    $report.completedUtc = [DateTime]::UtcNow.ToString('O')
    $report.sourceInputHashes = [ordered]@{ before = @($sourceInputBefore); after = @($sourceInputAfter); unchanged = ($complete -and $changed.Count -eq 0) }
    $report.processes = @($processes)
    $report.moduleBuilds = @($moduleBuilds)
    $report.sumModuleBuilds = @($sumModuleBuilds)
    $report.nativeBuilds = @($nativeBuilds)
    $report.sumNativeBuilds = @($sumNativeBuilds)
    $report.nativeRuns = @($nativeRuns)
    $report.sumNativeRuns = @($sumNativeRuns)
    $report.refinementsModuleBuilds = @($refinementsModuleBuilds)
    $report.refinementsNativeBuilds = @($refinementsNativeBuilds)
    $report.refinementsNativeRuns = @($refinementsNativeRuns)
    $report.checks = @($checks)
    $report.passed = $checks.Count -gt 0 -and @($checks | Where-Object { -not $_.passed }).Count -eq 0
    if ($errors.Count -gt 0) { $report.errors = @($errors) }
    if (-not (Has-Field $report 'refinements')) {
        $refinementChecks = if ($refinementsStarted) { @($checks | Select-Object -Skip $refinementFirstCheck) } else { @() }
        $report.refinements = [ordered]@{
            fixture = $refinementsFixturePath
            flowSource = $refinementsFlowPath
            runnerSource = $refinementsRunnerSourcePath
            runDirectory = $refinementsRunDirectory
            modules = @($refinementsModuleBuilds)
            nativeBuilds = @($refinementsNativeBuilds)
            nativeRuns = @($refinementsNativeRuns)
            checks = $refinementChecks
            passed = $false
            started = [bool]$refinementsStarted
            errors = @($errors)
        }
    } else {
        $report.refinements.sourceInputHashes = [ordered]@{ before = @($sourceInputBefore); after = @($sourceInputAfter); unchanged = ($complete -and $changed.Count -eq 0) }
        $report.refinements.errors = @($errors)
        $report.refinements.passed = [bool]$report.refinements.passed -and $complete -and $changed.Count -eq 0 -and $errors.Count -eq 0
    }
    try {
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($reportPath)) | Out-Null
        [IO.File]::WriteAllText($reportPath, (ConvertTo-Json -InputObject $report -Depth 100) + [Environment]::NewLine, $utf8)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($refinementsReportPath)) | Out-Null
        [IO.File]::WriteAllText($refinementsReportPath, (ConvertTo-Json -InputObject $report.refinements -Depth 100) + [Environment]::NewLine, $utf8)
    } catch {
        Write-Error "Could not write owning mailbox evidence: $($_.Exception.Message)"
        exit 1
    }
}

Write-Output "EvidencePath=$reportPath"
Write-Output "RefinementsEvidencePath=$refinementsReportPath"
Write-Output "Passed=$($report.passed)"
if (-not $report.passed) {
    $failedNames = @($checks | Where-Object { -not $_.passed } | ForEach-Object { $_.name })
    Write-Error "Owning mailbox acceptance failed. $($failedNames -join '; ')"
    exit 1
}
