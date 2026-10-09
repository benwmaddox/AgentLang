#requires -Version 7.0
[CmdletBinding()]
param(
    [switch]$SerialBuild
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
$runnerSourcePath = Join-Path $repo 'experiments/AgentLang.OwningMailbox/native_owning_mailbox.c'
$fixturePath = Join-Path $repo 'tests/fixtures/native-conformance/owning-mailbox.json'
$nativeDirectory = Join-Path $repo 'src/AgentLang.Llvm/native'
$arenaRuntimePath = Join-Path $nativeDirectory 'arena_runtime.c'
$mailboxRuntimePath = Join-Path $nativeDirectory 'mailbox_runtime.c'
$owningStackPath = Join-Path $nativeDirectory 'owning_stack_runtime.c'
$owningBankPath = Join-Path $nativeDirectory 'owning_bank.c'
$nativeSources = @(
    $runnerSourcePath,
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
$nativeBuilds = [Collections.Generic.List[object]]::new()
$nativeRuns = [Collections.Generic.List[object]]::new()
$errors = [Collections.Generic.List[string]]::new()
$timeoutMilliseconds = 300000
$utf8 = [Text.UTF8Encoding]::new($false)
$sourceInputBefore = @()
$report = [ordered]@{
    schemaVersion = 1
    kind = 'owning-native-mailbox-integration'
    runId = $runId
    startedUtc = [DateTime]::UtcNow.ToString('O')
    repository = $repo
    fixture = $fixturePath
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

try {
    [IO.Directory]::CreateDirectory($runDirectory) | Out-Null
    $script:tempDirectory = Join-Path $runDirectory 'repo-temp'
    [IO.Directory]::CreateDirectory($script:tempDirectory) | Out-Null
    $fixture = Read-JsonFile $fixturePath
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
        $moduleFiles = @($moduleBuilds | ForEach-Object { [string](Get-Field $_.bootstrap 'modulePath') })
        $runnerFiles = @($nativeBuilds | ForEach-Object { [string]$_.executable })
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
    $report.nativeBuilds = @($nativeBuilds)
    $report.nativeRuns = @($nativeRuns)
    $report.checks = @($checks)
    $report.passed = $checks.Count -gt 0 -and @($checks | Where-Object { -not $_.passed }).Count -eq 0
    if ($errors.Count -gt 0) { $report.errors = @($errors) }
    try {
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($reportPath)) | Out-Null
        [IO.File]::WriteAllText($reportPath, (ConvertTo-Json -InputObject $report -Depth 100) + [Environment]::NewLine, $utf8)
    } catch {
        Write-Error "Could not write owning mailbox evidence: $($_.Exception.Message)"
        exit 1
    }
}

Write-Output "EvidencePath=$reportPath"
Write-Output "Passed=$($report.passed)"
if (-not $report.passed) {
    $failedNames = @($checks | Where-Object { -not $_.passed } | ForEach-Object { $_.name })
    Write-Error "Owning mailbox acceptance failed. $($failedNames -join '; ')"
    exit 1
}
