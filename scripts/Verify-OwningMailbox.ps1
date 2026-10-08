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

function Check-NativeEvidence($Native, $Fixture, [string]$Optimization) {
    Add-Check "$Optimization native mailbox runner reports success" ((Get-Field $Native 'passed') -eq $true -and [int](Get-Field $Native 'failureCount') -eq 0)
    $named = @(Get-Field $Native 'checks')
    Add-Check "$Optimization native runner emits named passing assertions" ($named.Count -ge 40 -and @($named | Where-Object { (Get-Field $_ 'passed') -ne $true }).Count -eq 0) ([ordered]@{ assertionCount = $named.Count })
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
        Add-Check "$Optimization acceptance includes required case: $required" (@($checkNames | Where-Object { $_.Contains($required, [StringComparison]::Ordinal) }).Count -gt 0)
    }

    $life = Get-Field $Fixture 'lifecycle'
    $observed = Get-Field $Native 'observed'
    $unicode = Get-Field $life 'unicode'
    $empty = Get-Field $life 'empty'
    Compare-Bank (Get-Field $observed 'unicodeInitialize') $false @((Get-Field $unicode 'initializeRoot')) "$Optimization Unicode initialize" $Fixture
    Compare-Bank (Get-Field $observed 'emptyInitialize') $false @((Get-Field $empty 'initializeRoot')) "$Optimization empty initialize" $Fixture
    Compare-Bank (Get-Field $observed 'unicodeBegin') $true @(Get-Field $unicode 'beginRoots') "$Optimization Unicode begin" $Fixture
    Compare-Bank (Get-Field $observed 'emptyBegin') $true @(Get-Field $empty 'beginRoots') "$Optimization empty begin" $Fixture
    Compare-Bank (Get-Field $observed 'unicodeResume') $false @((Get-Field $unicode 'resumeRoot')) "$Optimization Unicode resume" $Fixture
    Compare-Bank (Get-Field $observed 'emptyResume') $false @((Get-Field $empty 'resumeRoot')) "$Optimization empty resume" $Fixture

    $totals = Get-Field (Get-Field $Fixture 'copyAccounting') 'sourceDerivedSmallLifecycleTotals'
    $successStats = Get-Field $Native 'successStats'
    Compare-StatsToFixture $successStats $totals "$Optimization interleaved lifecycle"
    $expectedPublished = [uint64]0
    foreach ($name in @('unicodeInitialize', 'emptyInitialize', 'unicodeBegin', 'emptyBegin', 'unicodeResume', 'emptyResume')) {
        $expectedPublished += [uint64](Get-Field (Get-Field $observed $name) 'usedBytes')
    }
    Add-Check "$Optimization publication byte counter equals complete successful output extents" ([uint64](Get-Field $successStats 'publicationCopyBytes') -eq $expectedPublished) ([ordered]@{ expected = $expectedPublished; actual = (Get-Field $successStats 'publicationCopyBytes') })
    Add-Check "$Optimization interleaving leaves two retained roots and 40 serialized bytes" ([uint64](Get-Field $successStats 'liveRetainedRoots') -eq 2 -and [uint64](Get-Field $successStats 'liveRetainedBytes') -eq 40)
    Add-Check "$Optimization mailbox storage is caller-reserved and component totals are explicit" (
        [uint64](Get-Field (Get-Field $Native 'storageRequirements') 'storageBytes') -eq [uint64](Get-Field $successStats 'storageReservedBytes') -and
        [uint64](Get-Field (Get-Field $Native 'storageRequirements') 'controllerReservedBytes') -gt 0) (Get-Field $Native 'storageRequirements')
    $storageConfig = Get-Field (Get-Field $Fixture 'storage') 'focusedRunnerConfiguration'
    $expectedSlotCapacity = [uint64](Get-Field $storageConfig 'scratchSlotCapacity')
    $expectedPolicy = [uint32](Get-Field $storageConfig 'suspensionPolicyId')
    Add-Check "$Optimization milestone 131 regression uses one RETURN scratch slot" (
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
    $expectedScratch = $expectedSlotCapacity * ($scratchCapacity + 2 * $bitmapBytes)
    $requirements = Get-Field $Native 'storageRequirements'
    Add-Check "$Optimization caller-storage components match independent bank and bitmap arithmetic" (
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
    Compare-StatsToFixture (Get-Field $Native 'boundaryStats') $boundaryExpected "$Optimization UTF-8 width boundaries"
    $largeExpected = Get-Field (Get-Field $life 'largeRequest') 'sourceDerivedStats'
    Compare-StatsToFixture (Get-Field $Native 'largeRequestStats') $largeExpected "$Optimization 4096-byte request and retained-capacity retry"

    $scratchStats = Get-Field $Native 'scratchFailureRetryStats'
    Add-Check "$Optimization scratch failure retry completes with no outstanding scratch lease" (
        [uint32](Get-Field $scratchStats 'outstandingScratchLeases') -eq 0 -and
        [uint64](Get-Field $scratchStats 'scratchLeaseAcquisitions') -eq [uint64](Get-Field $scratchStats 'scratchLeaseReturns') -and
        [uint64](Get-Field $scratchStats 'moveBytes') -eq 0)
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
        $moduleDirectory = Join-Path $runDirectory "module-$optimization"
        [IO.Directory]::CreateDirectory($moduleDirectory) | Out-Null
        $moduleProcess = Invoke-CapturedProcess "compile-module-$optimization" $dotnet @($assemblyPath, $optimization, $moduleDirectory, $flowPath) $repo
        Require-ProcessSuccess $moduleProcess "Fresh $optimization owning mailbox module compile succeeded"
        $bootstrap = ConvertFrom-JsonText $moduleProcess.stdout.Trim() "$optimization module bootstrap"
        $modulePath = [string](Get-Field $bootstrap 'modulePath')
        $llvmIrPath = [string](Get-Field $bootstrap 'llvmIrPath')
        $metadataPath = [string](Get-Field $bootstrap 'metadataSourcePath')
        $manifestPath = [string](Get-Field $bootstrap 'manifestPath')
        foreach ($path in @($modulePath, $llvmIrPath, $metadataPath, $manifestPath)) {
            if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "$optimization generated artifact is missing: $path" }
        }
        $manifest = Read-JsonFile $manifestPath
        $moduleInfo = Get-Field $manifest 'moduleInfo'
        $associatedResume = Get-Field $moduleInfo 'associatedResume'
        $resumeEntry = @(Get-Field $moduleInfo 'entries' | Where-Object { [string](Get-Field $_ 'role') -ceq 'resume' })
        $expectedAssociated = Get-Field $fixture 'associatedResume'
        $associatedShapeMatches = $resumeEntry.Count -eq 1 -and
            [string](Get-Field $associatedResume 'functionSymbol') -ceq [string](Get-Field $expectedAssociated 'functionSymbol') -and
            [string](Get-Field $associatedResume 'entryFrameSymbol') -ceq [string](Get-Field $resumeEntry[0] 'entryFrameSymbol') -and
            (@(Get-Field $associatedResume 'inputTypeIndexes') -join ',') -ceq (@(Get-Field $resumeEntry[0] 'inputTypeIndexes') -join ',') -and
            (@(Get-Field $associatedResume 'outputTypeIndexes') -join ',') -ceq (@(Get-Field $resumeEntry[0] 'outputTypeIndexes') -join ',') -and
            [int](Get-Field $associatedResume 'callbackMetadataBytes') -gt 0
        Add-Check "$optimization manifest exposes associated resume on the existing resume frame" $associatedShapeMatches $associatedResume
        Add-Check "$optimization manifest retains ABI v1 with the appended callback descriptor" (
            [int](Get-Field $moduleInfo 'abiVersion') -eq [int](Get-Field $fixture 'moduleAbiVersion') -and
            [int](Get-Field $fixture 'moduleStructSizeBytes') -eq 144 -and
            [int](Get-Field $fixture 'associatedResumePointerOffsetBytes') -eq 136) ([ordered]@{ abiVersion = Get-Field $moduleInfo 'abiVersion'; moduleStructSizeBytes = Get-Field $fixture 'moduleStructSizeBytes'; associatedResumePointerOffsetBytes = Get-Field $fixture 'associatedResumePointerOffsetBytes' })
        $dlls = @(Get-ChildItem -LiteralPath $moduleDirectory -Recurse -File -Filter '*.dll')
        Add-Check "$optimization compiler output contains one generated module DLL" ($dlls.Count -eq 1 -and [IO.Path]::GetFullPath($dlls[0].FullName) -ceq [IO.Path]::GetFullPath($modulePath)) ([ordered]@{ count = $dlls.Count; modulePath = $modulePath })
        Add-Check "$optimization module roles share the verified program and three-entry surface" ((Get-Field $bootstrap 'sameVerifiedProgramInstance') -eq $true -and @(Get-Field $bootstrap 'entries').Count -eq 3)
        $sourceTypeEvidence = Get-Field $bootstrap 'sourceDerivedTypeIds'
        $sourceIds = Get-Field $sourceTypeEvidence 'typeIds'
        $expectedIds = Get-Field (Get-Field $fixture 'sourceDerivedTypeIds') 'typeIds'
        $idsMatch = $true
        foreach ($name in @('Continuation', 'State', 'String')) {
            if ([uint32](Get-Field $sourceIds $name) -ne [uint32](Get-Field $expectedIds $name)) { $idsMatch = $false }
        }
        Add-Check "$optimization type IDs follow source-name-derived fixture ordering" $idsMatch $sourceIds
        $expectedLayoutOrder = @(Get-Field (Get-Field $fixture 'sourceDerivedTypeIds') 'reachableDescriptorOrder' | ForEach-Object { [string]$_ })
        $actualLayoutOrder = @(Get-Field $bootstrap 'layouts' | ForEach-Object { [string](Get-Field $_ 'typeName') })
        Add-Check "$optimization reachable layout descriptors follow source-derived dense TypeId order" (($actualLayoutOrder -join ',') -ceq ($expectedLayoutOrder -join ',')) ([ordered]@{ expected = $expectedLayoutOrder; actual = $actualLayoutOrder })
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
            Add-Check "$optimization $($expectedRole.role) descriptor indexes follow the independent fixture order" $roleIndexesMatch ([ordered]@{ expectedInputs = $expectedRole.inputs; expectedOutputs = $expectedRole.outputs; actualEntryCount = $roleEntry.Count; actualInputs = if ($roleEntry.Count -eq 1) { Get-Field $roleEntry[0] 'inputTypeIndexes' } else { @() }; actualOutputs = if ($roleEntry.Count -eq 1) { Get-Field $roleEntry[0] 'outputTypeIndexes' } else { @() } })
        }
        $bounds = Get-Field $bootstrap 'backendBounds'
        Add-Check "$optimization reports generated frame and scanner bounds separately" (
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
            outputDirectory = $moduleDirectory
            bootstrap = $bootstrap
            manifest = $manifest
            artifacts = $artifactHashes
        })
    }

    $semanticO0 = [ordered]@{
        entries = Get-Field $moduleBuilds[0].bootstrap 'entries'
        layouts = Get-Field $moduleBuilds[0].bootstrap 'layouts'
        sourceDerivedTypeIds = Get-Field $moduleBuilds[0].bootstrap 'sourceDerivedTypeIds'
        diagnostics = Get-Field $moduleBuilds[0].bootstrap 'diagnostics'
    }
    $semanticO2 = [ordered]@{
        entries = Get-Field $moduleBuilds[1].bootstrap 'entries'
        layouts = Get-Field $moduleBuilds[1].bootstrap 'layouts'
        sourceDerivedTypeIds = Get-Field $moduleBuilds[1].bootstrap 'sourceDerivedTypeIds'
        diagnostics = Get-Field $moduleBuilds[1].bootstrap 'diagnostics'
    }
    Add-Check 'O0 and O2 module role, layout, source-type, and diagnostic metadata agree' ((ConvertTo-Json -InputObject $semanticO0 -Depth 60 -Compress) -ceq (ConvertTo-Json -InputObject $semanticO2 -Depth 60 -Compress))

    $oracleO0 = Get-Field $moduleBuilds[0].bootstrap 'interpreterOracle'
    $oracleO2 = Get-Field $moduleBuilds[1].bootstrap 'interpreterOracle'
    $expectedUnicode = [string](Get-Field (Get-Field (Get-Field $fixture 'lifecycle') 'unicode') 'resumeExpectedValue')
    foreach ($optimizationOracle in @(@{ name = 'O0'; oracle = $oracleO0 }, @{ name = 'O2'; oracle = $oracleO2 })) {
        $unicodeOracle = Get-Field $optimizationOracle.oracle 'unicode'
        $emptyOracle = Get-Field $optimizationOracle.oracle 'empty'
        foreach ($step in @(
            @{ field = 'initializedJson'; expected = 'A🙂' },
            @{ field = 'pendingJson'; expected = 'A🙂' },
            @{ field = 'pendingJson'; expected = 'δ' },
            @{ field = 'completedJson'; expected = $expectedUnicode }
        )) {
            $json = [string](Get-Field $unicodeOracle $step.field)
            $valueTree = ConvertFrom-JsonText $json "$($optimizationOracle.name) interpreter $($step.field)"
            Add-Check "$($optimizationOracle.name) interpreter $($step.field) contains fixture value '$($step.expected)'" (Contains-StringValue $valueTree ([string]$step.expected))
        }
        $emptyCompleted = ConvertFrom-JsonText ([string](Get-Field $emptyOracle 'completedJson')) "$($optimizationOracle.name) empty interpreter completion"
        Add-Check "$($optimizationOracle.name) interpreter executes empty lifecycle to B" (Contains-StringValue $emptyCompleted 'B')
    }

    $clangVersion = Invoke-CapturedProcess 'clang-version' $clang @('--version') $runDirectory
    Require-ProcessSuccess $clangVersion 'Clang version query succeeded'
    foreach ($optimization in @('O0', 'O2')) {
        $module = $moduleBuilds | Where-Object { $_.optimization -ceq $optimization } | Select-Object -First 1
        $runnerDirectory = Join-Path $runDirectory "native-$optimization"
        [IO.Directory]::CreateDirectory($runnerDirectory) | Out-Null
        $runnerPath = Join-Path $runnerDirectory "native-owning-mailbox-$optimization.exe"
        foreach ($source in $nativeSources) {
            if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Native owning-mailbox source is missing: $source" }
        }
        $compileArguments = @('--target=x86_64-pc-windows-msvc', '-std=c11', '-Wall', '-Wextra', '-Werror', "-$optimization", '-I', $nativeDirectory) + $nativeSources + @('-o', $runnerPath)
        $nativeBuild = Invoke-CapturedProcess "native-runner-build-$optimization" $clang $compileArguments $runnerDirectory
        Require-ProcessSuccess $nativeBuild "$optimization native owning-mailbox runner build succeeded"
        $nativeBuilds.Add([ordered]@{ optimization = $optimization; executable = $runnerPath; sha256 = Get-Hash $runnerPath; process = $nativeBuild })
        $nativeRun = Invoke-CapturedProcess "native-runner-run-$optimization" $runnerPath @([string](Get-Field $module.bootstrap 'modulePath')) $runnerDirectory
        Require-ProcessSuccess $nativeRun "$optimization native owning-mailbox lifecycle and boundary suite passed"
        $native = ConvertFrom-JsonText $nativeRun.stdout.Trim() "$optimization native owning-mailbox result"
        Check-NativeEvidence $native $fixture $optimization
        $nativeRuns.Add([ordered]@{ optimization = $optimization; process = $nativeRun; result = $native })
    }

    $readobj = Join-Path ([IO.Path]::GetDirectoryName($clang)) 'llvm-readobj.exe'
    $nm = Join-Path ([IO.Path]::GetDirectoryName($clang)) 'llvm-nm.exe'
    if (Test-Path -LiteralPath $readobj -PathType Leaf) {
        $moduleFiles = @(
            [string](Get-Field $moduleBuilds[0].bootstrap 'modulePath'),
            [string](Get-Field $moduleBuilds[1].bootstrap 'modulePath')
        )
        $runnerFiles = @(
            [string]$nativeBuilds[0].executable,
            [string]$nativeBuilds[1].executable
        )
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
