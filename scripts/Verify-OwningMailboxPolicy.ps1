#requires -Version 7.0
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runId = [Guid]::NewGuid().ToString('N')
$evidenceRoot = Join-Path $repo '.agentlang/owning-mailbox-002'
$runDirectory = Join-Path $evidenceRoot "policy-run-$runId"
$reportPath = Join-Path $runDirectory 'policy-evidence.json'
$projectPath = Join-Path $repo 'experiments/AgentLang.OwningMailbox/AgentLang.OwningMailbox.fsproj'
$programPath = Join-Path $repo 'experiments/AgentLang.OwningMailbox/Program.fs'
$flowPath = Join-Path $repo 'experiments/AgentLang.OwningMailbox/owning-mailbox.flow'
$runnerSourcePath = Join-Path $repo 'experiments/AgentLang.OwningMailbox/native_owning_mailbox.c'
$fixturePath = Join-Path $repo 'tests/fixtures/native-conformance/owning-mailbox-policy.json'
$historicalFixturePath = Join-Path $repo 'tests/fixtures/native-conformance/owning-mailbox.json'
$nativeDirectory = Join-Path $repo 'src/AgentLang.Llvm/native'
$nativeSources = @(
    $runnerSourcePath,
    (Join-Path $nativeDirectory 'arena_runtime.c'),
    (Join-Path $nativeDirectory 'mailbox_runtime.c'),
    (Join-Path $nativeDirectory 'mailbox_runtime_windows.c'),
    (Join-Path $nativeDirectory 'owning_stack_runtime.c'),
    (Join-Path $nativeDirectory 'owning_bank.c')
)
$sourceInputPaths = @(
    (Join-Path $repo 'AgentLang.sln'), $projectPath, $programPath, $flowPath,
    $runnerSourcePath, $fixturePath, $historicalFixturePath, $PSCommandPath,
    (Join-Path $repo 'src/AgentLang.Llvm/AgentLang.Llvm.fsproj'),
    (Join-Path $repo 'src/AgentLang.Llvm/OwningStackAot.fs'),
    (Join-Path $nativeDirectory 'module_abi.h'),
    (Join-Path $nativeDirectory 'arena_runtime.h'),
    (Join-Path $nativeDirectory 'mailbox_runtime.h'),
    (Join-Path $nativeDirectory 'mailbox_runtime.c'),
    (Join-Path $nativeDirectory 'mailbox_runtime_windows.c'),
    (Join-Path $nativeDirectory 'owning_mailbox_abi.h'),
    (Join-Path $nativeDirectory 'owning_stack_runtime.h'),
    (Join-Path $nativeDirectory 'owning_stack_runtime.c'),
    (Join-Path $nativeDirectory 'owning_bank.h'),
    (Join-Path $nativeDirectory 'owning_bank.c')
)
foreach ($nativeSource in $nativeSources) {
    if ($sourceInputPaths -notcontains $nativeSource) {
        $sourceInputPaths += $nativeSource
    }
}
$utf8 = [Text.UTF8Encoding]::new($false)
$checks = [Collections.Generic.List[object]]::new()
$processes = [Collections.Generic.List[object]]::new()
$moduleBuilds = [Collections.Generic.List[object]]::new()
$nativeBuilds = [Collections.Generic.List[object]]::new()
$nativeRuns = [Collections.Generic.List[object]]::new()
$errors = [Collections.Generic.List[string]]::new()
$sourceInputBefore = @()
$sourceInputAfter = @()
$timeoutMilliseconds = 300000
$tempDirectory = Join-Path $runDirectory 'repo-temp'
$report = [ordered]@{
    schemaVersion = 1
    kind = 'owning-native-mailbox-policy-comparison'
    runId = $runId
    startedUtc = [DateTime]::UtcNow.ToString('O')
    repository = $repo
    fixture = $fixturePath
    historicalFixture = $historicalFixturePath
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
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Has-Fields($Object, [string[]]$Names) {
    if ($null -eq $Object) { return $false }
    foreach ($name in $Names) {
        if ($null -eq (Get-Field $Object $name)) { return $false }
    }
    return $true
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

function Compare-Stats($Actual, $CommonExpected, $PolicyExpected, [string]$PolicyName, [string]$CaseName) {
    $mapping = [ordered]@{
        utf8InputBytes = 'utf8InputBytes'
        utf16StagingBytes = 'utf16StagingBytes'
        deepCopyBytes = 'deepCopyBytes'
        moveBytes = 'moveBytes'
        returnedOutputDescriptors = 'returnedOutputDescriptors'
        handlerInvocations = 'handlerInvocations'
        handlerFailures = 'handlerFailures'
        finalLiveRetainedBytes = 'liveRetainedBytes'
        finalLiveRetainedRoots = 'liveRetainedRoots'
        pinnedScratchSlots = 'pinnedScratchSlots'
        pinnedScratchBytes = 'pinnedScratchBytes'
        outstandingScratchLeases = 'outstandingScratchLeases'
        inputImportBytes = 'inputImportBytes'
        publicationCopyBytes = 'publicationCopyBytes'
        beginPublicationCopyBytes = 'beginPublicationCopyBytes'
        resumeRootImportBytes = 'resumeRootImportBytes'
        scratchLeaseAcquisitions = 'scratchLeaseAcquisitions'
        scratchLeaseReturns = 'scratchLeaseReturns'
    }
    $expectedFields = @($CommonExpected.Keys) + @($PolicyExpected.Keys)
    $missingExpected = @($mapping.Keys | Where-Object { $_ -notin $expectedFields })
    $unmappedExpected = @($expectedFields | Where-Object { $_ -notin $mapping.Keys })
    if ($expectedFields.Count -ne $mapping.Count -or
        @($expectedFields | Sort-Object -Unique).Count -ne $mapping.Count -or
        $missingExpected.Count -gt 0 -or $unmappedExpected.Count -gt 0) {
        throw "$CaseName $PolicyName fixture stats must define every mapped metric exactly once; missing=[$($missingExpected -join ',')] unexpected=[$($unmappedExpected -join ',')]"
    }
    foreach ($expectedSet in @($CommonExpected, $PolicyExpected)) {
        foreach ($expectedField in $expectedSet.Keys) {
            $actualField = $mapping[$expectedField]
            if ([string]::IsNullOrWhiteSpace([string]$actualField)) { throw "No native stats mapping for fixture field $expectedField" }
            $expectedValue = [uint64]$expectedSet[$expectedField]
            $actualRaw = Get-Field $Actual $actualField
            if ($null -eq $actualRaw) {
                Add-Check "$CaseName $PolicyName $actualField is present in native stats" $false ([ordered]@{ expected = $expectedValue; actual = $null })
                continue
            }
            $actualValue = [uint64]$actualRaw
            Add-Check "$CaseName $PolicyName $actualField matches source-derived fixture" ($actualValue -eq $expectedValue) ([ordered]@{ expected = $expectedValue; actual = $actualValue })
        }
    }
}

function Compare-FinalStates($ActualStates, $ExpectedStates, $TypeIds, [string]$Label) {
    $states = @($ActualStates)
    $expected = @($ExpectedStates)
    $shapeOkay = $states.Count -eq $expected.Count
    Add-Check "$Label final state count matches fixture" $shapeOkay ([ordered]@{ expected = $expected.Count; actual = $states.Count })
    for ($index = 0; $index -lt [Math]::Min($states.Count, $expected.Count); $index++) {
        $actual = $states[$index]
        $rootExpected = $expected[$index]
        if (-not (Has-Fields $actual @('pending', 'usedBytes', 'roots')) -or
            -not (Has-Fields $rootExpected @('type', 'extentBytes', 'payloadBytes', 'serializedHex'))) {
            Add-Check "$Label final State[$index] and fixture contain required fields" $false ([ordered]@{ expected = $rootExpected; actual = $actual })
            continue
        }
        $roots = @(Get-Field $actual 'roots')
        $rootOkay = $roots.Count -eq 1
        if ($rootOkay) {
            $root = $roots[0]
            $typeName = [string](Get-Field $rootExpected 'type')
            $typeId = Get-Field $TypeIds $typeName
            $rootOkay = (Has-Fields $root @('typeId', 'offsetBytes', 'extentBytes', 'payloadBytes', 'serializedHex')) -and
                $null -ne $typeId -and
                [uint32](Get-Field $root 'typeId') -eq [uint32]$typeId -and
                [uint32](Get-Field $root 'offsetBytes') -eq 0 -and
                [uint32](Get-Field $root 'extentBytes') -eq [uint32](Get-Field $rootExpected 'extentBytes') -and
                [uint32](Get-Field $root 'payloadBytes') -eq [uint32](Get-Field $rootExpected 'payloadBytes') -and
                [string](Get-Field $root 'serializedHex') -ceq [string](Get-Field $rootExpected 'serializedHex') -and
                [uint32](Get-Field $actual 'usedBytes') -eq [uint32](Get-Field $rootExpected 'extentBytes') -and
                (Get-Field $actual 'pending') -eq $false
        }
        Add-Check "$Label final State[$index] matches exact type, extent, and serialized bytes" $rootOkay ([ordered]@{ expected = $rootExpected; actual = $actual })
    }
}

function Compare-Case($Case, $FixtureCase, $TypeIds, [string]$PolicyName, [string]$CaseName) {
    $common = Get-Field (Get-Field $FixtureCase 'sourceDerivedTotals') 'common'
    $policyExpected = Get-Field (Get-Field $FixtureCase 'sourceDerivedTotals') $PolicyName
    if (-not (Has-Fields $Case @('stats', 'storageRequirements', 'finalStates')) -or
        $null -eq $common -or $null -eq $policyExpected) {
        throw "$CaseName $PolicyName native result or source-derived fixture is missing required case fields."
    }
    Compare-Stats (Get-Field $Case 'stats') $common $policyExpected $PolicyName $CaseName
    $stats = Get-Field $Case 'stats'
    $requirements = Get-Field $Case 'storageRequirements'
    $reservation = if ($CaseName -ceq 'pairedUnicodeEmpty') {
        Get-Field $FixtureCase 'callerReservation'
    } else {
        Get-Field $fixture 'equalReservation'
    }
    if (-not (Has-Fields $requirements @('mailboxCapacity', 'retainedReservedBytes', 'scratchReservedBytes', 'textStagingReservedBytes', 'controllerReservedBytes', 'storageBytes')) -or
        -not (Has-Fields $stats @('storageReservedBytes', 'scratchSlotCapacity')) -or
        -not (Has-Fields $reservation @('mailboxCapacity', 'scratchSlotCapacity', 'retainedByteCapacityPerBank', 'owningBankRootBytes', 'bankRootCapacity', 'scratchReservedBytes', 'textStagingByteCapacity'))) {
        Add-Check "$CaseName $PolicyName reservation and stats contain all required fields" $false ([ordered]@{ requirements = $requirements; stats = $stats })
        return
    }
    $capacity = Get-Field $reservation 'scratchSlotCapacity'
    $mailboxes = [uint32](Get-Field $reservation 'mailboxCapacity')
    $retained = [uint64](Get-Field $reservation 'retainedByteCapacityPerBank')
    $rootBytes = [uint64](Get-Field $reservation 'owningBankRootBytes')
    $bankRoots = [uint64](Get-Field $reservation 'bankRootCapacity')
    $retainedExpected = ($retained + $rootBytes * $bankRoots) * 2 * $mailboxes
    $scratchExpected = [uint64](Get-Field (Get-Field $fixture 'equalReservation') 'scratchReservedBytes')
    $stagingExpected = [uint64](Get-Field (Get-Field $fixture 'equalReservation') 'textStagingByteCapacity')
    $requirementOkay = [uint32](Get-Field $requirements 'mailboxCapacity') -eq $mailboxes -and
        [uint64](Get-Field $requirements 'retainedReservedBytes') -eq $retainedExpected -and
        [uint64](Get-Field $requirements 'scratchReservedBytes') -eq $scratchExpected -and
        [uint64](Get-Field $requirements 'textStagingReservedBytes') -eq $stagingExpected -and
        [uint64](Get-Field $requirements 'controllerReservedBytes') -gt 0 -and
        [uint64](Get-Field $requirements 'storageBytes') -eq ($retainedExpected + $scratchExpected + $stagingExpected + [uint64](Get-Field $requirements 'controllerReservedBytes')) -and
        [uint64](Get-Field $stats 'storageReservedBytes') -eq [uint64](Get-Field $requirements 'storageBytes') -and
        [uint64](Get-Field $stats 'scratchSlotCapacity') -eq [uint64]$capacity
    Add-Check "$CaseName $PolicyName caller reservation matches equal-pool arithmetic" $requirementOkay $requirements
    Compare-FinalStates (Get-Field $Case 'finalStates') (Get-Field $FixtureCase 'finalStates') $TypeIds "$CaseName $PolicyName"
}

function Check-PendingAttachments($Case, $FixtureCase, [string]$PolicyName) {
    $attachments = Get-Field $Case 'pendingAttachments'
    $slotA = Get-Field $attachments 'a'
    $slotB = Get-Field $attachments 'b'
    if ($PolicyName -ceq 'RETURN') {
        $okay = (Has-Fields $slotA @('cursorBytes')) -and
            (Has-Fields $slotB @('cursorBytes')) -and
            [uint32](Get-Field $slotA 'cursorBytes') -eq 0 -and
            [uint32](Get-Field $slotB 'cursorBytes') -eq 0
        Add-Check 'RETURN exposes no associated arena roots at suspension' $okay $attachments
        return
    }
    $expected = Get-Field $FixtureCase 'keepAssociatedPendingRoots'
    if (-not (Has-Fields $slotA @('cursorBytes', 'scratchSlotIndex', 'roots')) -or
        -not (Has-Fields $slotB @('cursorBytes', 'scratchSlotIndex', 'roots')) -or
        -not (Has-Fields $expected @('cursorBytes', 'roots'))) {
        Add-Check 'KEEP exposes required actual roots and fixture shape' $false ([ordered]@{ expected = $expected; actual = $attachments })
        return
    }
    $expectedCursor = [uint32](Get-Field $expected 'cursorBytes')
    $rootsExpected = @(Get-Field $expected 'roots')
    $distinctSlots = [uint32](Get-Field $slotA 'scratchSlotIndex') -ne [uint32](Get-Field $slotB 'scratchSlotIndex')
    $okay = [uint32](Get-Field $slotA 'cursorBytes') -eq $expectedCursor -and
        [uint32](Get-Field $slotB 'cursorBytes') -eq $expectedCursor -and
        [uint32](Get-Field $slotA 'scratchSlotIndex') -lt 2 -and
        [uint32](Get-Field $slotB 'scratchSlotIndex') -lt 2 -and $distinctSlots
    foreach ($pair in @(@{ actual = $slotA; name = 'A' }, @{ actual = $slotB; name = 'B' })) {
        $actualRoots = @(Get-Field $pair.actual 'roots')
        if ($actualRoots.Count -ne $rootsExpected.Count) { $okay = $false; continue }
        for ($index = 0; $index -lt $rootsExpected.Count; $index++) {
            $actual = $actualRoots[$index]
            $expectedRoot = $rootsExpected[$index]
            if (-not (Has-Fields $actual @('typeIndex', 'offsetBytes', 'sourceOwnerEndBytes', 'reserved')) -or
                -not (Has-Fields $expectedRoot @('typeIndex', 'offsetBytes', 'extentBytes'))) {
                $okay = $false
                continue
            }
            $offset = [uint32](Get-Field $expectedRoot 'offsetBytes')
            $extent = [uint32](Get-Field $expectedRoot 'extentBytes')
            $ownerEnd = [uint32](Get-Field $actual 'sourceOwnerEndBytes')
            if ([uint32](Get-Field $actual 'typeIndex') -ne [uint32](Get-Field $expectedRoot 'typeIndex') -or
                [uint32](Get-Field $actual 'offsetBytes') -ne $offset -or
                [uint32](Get-Field $actual 'reserved') -ne 0 -or
                $ownerEnd -lt ($offset + $extent) -or $ownerEnd -gt $expectedCursor) { $okay = $false }
        }
    }
    Add-Check 'KEEP exposes actual State and Continuation offsets in two distinct pinned slots' $okay $attachments
}

try {
    [IO.Directory]::CreateDirectory($runDirectory) | Out-Null
    [IO.Directory]::CreateDirectory($tempDirectory) | Out-Null
    $fixture = Read-JsonFile $fixturePath
    $historicalFixture = Read-JsonFile $historicalFixturePath
    $fixtureFreeze = Get-Field $fixture 'oracleProvenance'
    Add-Check 'policy fixture records original and final pre-execution freeze provenance' (
        (Get-Field $fixture 'frozenBeforeNativeExecution') -eq $false -and
        (Get-Field $fixture 'frozenBeforeFinalNativeExecution') -eq $true -and
        (Get-Field $fixtureFreeze 'initialFixtureVersionFrozenBeforeFirstRun') -eq $true -and
        -not [string]::IsNullOrWhiteSpace([string](Get-Field $fixtureFreeze 'firstNativeRun')) -and
        -not [string]::IsNullOrWhiteSpace([string](Get-Field $fixtureFreeze 'postExecutionFixtureCorrection')))
    Add-Check 'milestone 131 regression fixture remains the published historical control' ((Get-Field $historicalFixture 'kind') -ceq 'owning-native-mailbox')
    foreach ($inputPath in $sourceInputPaths) {
        if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) { throw "Required policy acceptance input is missing: $inputPath" }
        $sourceInputBefore += [ordered]@{ path = [IO.Path]::GetFullPath($inputPath); sha256 = Get-Hash $inputPath }
    }

    $clangDefault = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\Llvm\x64\bin\clang.exe'
    $clang = Resolve-Executable 'AGENTLANG_LLVM_CLANG' $clangDefault 'clang.exe'
    $dotnetCommand = Get-Command -Name dotnet.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $dotnet = [IO.Path]::GetFullPath($dotnetCommand.Source)
    $report.compiler = [ordered]@{ dotnet = $dotnet; clang = $clang; clangSha256 = Get-Hash $clang; tempDirectory = $tempDirectory }

    $artifactsRoot = Join-Path $runDirectory 'dotnet-artifacts'
    $buildArguments = @('build', $projectPath, '--artifacts-path', $artifactsRoot, '--configuration', 'Release', '--verbosity', 'minimal', '-p:NuGetAudit=false', '-m:1')
    $build = Invoke-CapturedProcess 'fresh-release-project-build' $dotnet $buildArguments $repo
    Require-ProcessSuccess $build 'Fresh Release build of the owning mailbox bootstrap succeeded'
    Add-Check 'fresh Release build produced a successful process result' ($build.exitCode -eq 0)
    $assemblies = @(Get-ChildItem -LiteralPath (Join-Path $artifactsRoot 'bin') -Recurse -File -Filter 'AgentLang.OwningMailbox.dll' | Where-Object { $_.FullName -notmatch '[\\/]ref[\\/]' })
    Add-Check 'fresh isolated build produced exactly one executable bootstrap assembly' ($assemblies.Count -eq 1) ([ordered]@{ count = $assemblies.Count })
    if ($assemblies.Count -ne 1) { throw 'Expected one non-reference bootstrap assembly in the isolated build root.' }
    $assemblyPath = $assemblies[0].FullName

    foreach ($optimization in @('O0', 'O2')) {
        $moduleDirectory = Join-Path $runDirectory "module-$optimization"
        [IO.Directory]::CreateDirectory($moduleDirectory) | Out-Null
        $moduleProcess = Invoke-CapturedProcess "compile-module-$optimization" $dotnet @($assemblyPath, $optimization, $moduleDirectory, $flowPath) $repo
        Require-ProcessSuccess $moduleProcess "Fresh $optimization owning mailbox module compile succeeded"
        $bootstrap = ConvertFrom-JsonText $moduleProcess.stdout.Trim() "$optimization policy module bootstrap"
        $modulePath = [string](Get-Field $bootstrap 'modulePath')
        $manifestPath = [string](Get-Field $bootstrap 'manifestPath')
        if (-not (Test-Path -LiteralPath $modulePath -PathType Leaf) -or -not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "$optimization generated module or manifest is missing." }
        $manifest = Read-JsonFile $manifestPath
        $moduleInfo = Get-Field $manifest 'moduleInfo'
        $associated = Get-Field $moduleInfo 'associatedResume'
        $resumeEntries = @(Get-Field $moduleInfo 'entries' | Where-Object { [string](Get-Field $_ 'role') -ceq 'resume' })
        $expectedModule = Get-Field $fixture 'module'
        $associatedOkay = $resumeEntries.Count -eq 1 -and
            [int](Get-Field $moduleInfo 'abiVersion') -eq [int](Get-Field $expectedModule 'abiVersion') -and
            [string](Get-Field $associated 'functionSymbol') -ceq [string](Get-Field $expectedModule 'associatedResumeSymbol') -and
            [string](Get-Field $associated 'entryFrameSymbol') -ceq [string](Get-Field $resumeEntries[0] 'entryFrameSymbol') -and
            (@(Get-Field $associated 'inputTypeIndexes') -join ',') -ceq (@(Get-Field $expectedModule 'associatedResumeInputTypeIndexes') -join ',') -and
            (@(Get-Field $associated 'outputTypeIndexes') -join ',') -ceq (@(Get-Field $expectedModule 'associatedResumeOutputTypeIndexes') -join ',') -and
            [int](Get-Field $associated 'callbackMetadataBytes') -gt 0
        Add-Check "$optimization generated module manifest preserves ABI v1 and the associated resume frame contract" $associatedOkay $associated
        $moduleBuilds.Add([ordered]@{ optimization = $optimization; modulePath = $modulePath; manifestPath = $manifestPath; moduleSha256 = Get-Hash $modulePath; manifestSha256 = Get-Hash $manifestPath; bootstrap = $bootstrap; manifest = $manifest })
    }

    $sameArtifacts = [string](Get-Field $moduleBuilds[0].manifest.moduleInfo 'abiVersion') -ceq [string](Get-Field $moduleBuilds[1].manifest.moduleInfo 'abiVersion') -and
        (@(Get-Field $moduleBuilds[0].manifest.moduleInfo.associatedResume 'inputTypeIndexes') -join ',') -ceq (@(Get-Field $moduleBuilds[1].manifest.moduleInfo.associatedResume 'inputTypeIndexes') -join ',')
    Add-Check 'O0 and O2 expose the same associated callback ABI indexes' $sameArtifacts

    foreach ($optimization in @('O0', 'O2')) {
        $module = $moduleBuilds | Where-Object { $_.optimization -ceq $optimization } | Select-Object -First 1
        $runnerDirectory = Join-Path $runDirectory "native-$optimization"
        [IO.Directory]::CreateDirectory($runnerDirectory) | Out-Null
        $runnerPath = Join-Path $runnerDirectory "native-owning-mailbox-policy-$optimization.exe"
        $compileArguments = @('--target=x86_64-pc-windows-msvc', '-std=c11', '-Wall', '-Wextra', '-Werror', "-$optimization", '-DAL_MAILBOX_RUNTIME_TESTING', '-I', $nativeDirectory) + $nativeSources + @('-o', $runnerPath)
        $nativeBuild = Invoke-CapturedProcess "native-policy-build-$optimization" $clang $compileArguments $runnerDirectory
        Require-ProcessSuccess $nativeBuild "$optimization policy native runner compiled with strict warnings"
        $nativeBuilds.Add([ordered]@{ optimization = $optimization; executable = $runnerPath; sha256 = Get-Hash $runnerPath; process = $nativeBuild })
        $nativeRun = Invoke-CapturedProcess "native-policy-run-$optimization" $runnerPath @([string]$module.modulePath, '--policy') $runnerDirectory
        Require-ProcessSuccess $nativeRun "$optimization paired ownership-policy native suite passed"
        $native = ConvertFrom-JsonText $nativeRun.stdout.Trim() "$optimization policy native result"
        $nativeShape = Has-Fields $native @('passed', 'failureCount', 'checks')
        Add-Check "$optimization policy runner result has the required summary fields" $nativeShape
        $failureCountRaw = Get-Field $native 'failureCount'
        Add-Check "$optimization policy runner reports zero named assertion failures" ($nativeShape -and (Get-Field $native 'passed') -eq $true -and $null -ne $failureCountRaw -and [uint32]$failureCountRaw -eq 0) ([ordered]@{ failureCount = $failureCountRaw })
        $nativeChecks = @(Get-Field $native 'checks')
        $failedChecks = @($nativeChecks | Where-Object { (Get-Field $_ 'passed') -ne $true })
        $namesPresent = $nativeChecks.Count -eq 97 -and @($nativeChecks | Where-Object { -not (Has-Fields $_ @('name', 'passed')) -or [string]::IsNullOrWhiteSpace([string](Get-Field $_ 'name')) }).Count -eq 0
        Add-Check "$optimization policy runner emits the complete passing 97-check suite" ($namesPresent -and $failedChecks.Count -eq 0) ([ordered]@{ expectedAssertionCount = 97; assertionCount = $nativeChecks.Count; failed = $failedChecks })
        $nativeNames = @($nativeChecks | ForEach-Object { [string](Get-Field $_ 'name') })
        foreach ($required in @('direct fixture creates source-derived roots', 'mismatched saved mark', 'retained root count before writes', 'invalid retained type before writes', 'truncated retained owner after preflight', 'malformed bounded completion', 'overlapping protected payload', 'overlapping retained descriptors', 'overlapping module metadata', 'KEEP rejects a third begin before staging', 'suffix and restores prefix metadata', 'bank-capacity failure restores the attached prefix', 'C admission reuses')) {
            Add-Check "$optimization contains associated-policy oracle: $required" (@($nativeNames | Where-Object { $_.Contains($required, [StringComparison]::Ordinal) }).Count -gt 0)
        }

        $typeIds = Get-Field (Get-Field $historicalFixture 'sourceDerivedTypeIds') 'typeIds'
        foreach ($policyName in @('RETURN', 'KEEP_ASSOCIATED')) {
            $simpleCase = Get-Field (Get-Field $native 'pairedUnicodeEmpty') $policyName
            Compare-Case $simpleCase (Get-Field $fixture 'pairedUnicodeEmpty') $typeIds $policyName 'pairedUnicodeEmpty'
            $adversityCase = Get-Field (Get-Field $native 'interleavedFailures') $policyName
            Compare-Case $adversityCase (Get-Field $fixture 'interleavedFailures') $typeIds $policyName 'interleavedFailures'
            Check-PendingAttachments $adversityCase (Get-Field $fixture 'interleavedFailures') $policyName
        }
        $admission = Get-Field (Get-Field (Get-Field $native 'interleavedFailures') 'KEEP_ASSOCIATED') 'admission'
        $admissionExpected = Get-Field (Get-Field $fixture 'interleavedFailures') 'admission'
        $admissionShape = (Has-Fields $admission @('noSlotResult', 'noSlotTokenUntouched', 'noSlotCountersUnchanged', 'admittedCTokenSequence')) -and
            (Has-Fields $admissionExpected @('keepNoSlotResult', 'laterAdmittedTokenSequence'))
        Add-Check "$optimization KEEP no-slot admission fields are present" $admissionShape $admission
        Add-Check "$optimization KEEP no-slot rejection preserves token output and staging/import counters" ($admissionShape -and
            [uint32](Get-Field $admission 'noSlotResult') -eq [uint32](Get-Field $admissionExpected 'keepNoSlotResult') -and
            (Get-Field $admission 'noSlotTokenUntouched') -eq $true -and
            (Get-Field $admission 'noSlotCountersUnchanged') -eq $true -and
            [uint64](Get-Field $admission 'admittedCTokenSequence') -eq [uint64](Get-Field $admissionExpected 'laterAdmittedTokenSequence')) $admission
        $returnAdmission = Get-Field (Get-Field (Get-Field $native 'interleavedFailures') 'RETURN') 'admission'
        $returnAdmissionShape = (Has-Fields $returnAdmission @('admittedCTokenSequence')) -and
            (Has-Fields $admissionExpected @('laterAdmittedTokenSequence'))
        Add-Check "$optimization RETURN admission fields are present" $returnAdmissionShape $returnAdmission
        Add-Check "$optimization RETURN admits C with the same seeded token sequence" ($returnAdmissionShape -and [uint64](Get-Field $returnAdmission 'admittedCTokenSequence') -eq [uint64](Get-Field $admissionExpected 'laterAdmittedTokenSequence')) $returnAdmission
        $nativeRuns.Add([ordered]@{ optimization = $optimization; process = $nativeRun; result = $native })
    }

    foreach ($caseName in @('pairedUnicodeEmpty', 'interleavedFailures')) {
        $returnCase = if ($caseName -ceq 'pairedUnicodeEmpty') { Get-Field $nativeRuns[0].result.pairedUnicodeEmpty 'RETURN' } else { Get-Field $nativeRuns[0].result.interleavedFailures 'RETURN' }
        $keepCase = if ($caseName -ceq 'pairedUnicodeEmpty') { Get-Field $nativeRuns[0].result.pairedUnicodeEmpty 'KEEP_ASSOCIATED' } else { Get-Field $nativeRuns[0].result.interleavedFailures 'KEEP_ASSOCIATED' }
        Add-Check "$caseName RETURN and KEEP have exact equal storage reservation" (
            [uint64](Get-Field $returnCase.storageRequirements 'storageBytes') -eq [uint64](Get-Field $keepCase.storageRequirements 'storageBytes') -and
            [uint64](Get-Field $returnCase.storageRequirements 'scratchReservedBytes') -eq [uint64](Get-Field $keepCase.storageRequirements 'scratchReservedBytes'))
    }
} catch {
    $errors.Add($_.Exception.ToString())
    Add-Check 'policy verification completed without an uncaught error' $false $_.Exception.Message
} finally {
    foreach ($path in $sourceInputPaths) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $sourceInputAfter += [ordered]@{ path = [IO.Path]::GetFullPath($path); sha256 = Get-Hash $path }
        }
    }
    if ($sourceInputBefore.Count -gt 0 -and $sourceInputAfter.Count -eq $sourceInputBefore.Count) {
        $sourceStable = $true
        for ($index = 0; $index -lt $sourceInputBefore.Count; $index++) {
            if ($sourceInputBefore[$index].path -cne $sourceInputAfter[$index].path -or $sourceInputBefore[$index].sha256 -cne $sourceInputAfter[$index].sha256) { $sourceStable = $false }
        }
        Add-Check 'all policy acceptance source hashes remain unchanged during fresh O0/O2 builds and runs' $sourceStable
    } else {
        Add-Check 'all policy acceptance source hashes remain unchanged during fresh O0/O2 builds and runs' $false
    }
    $report.completedUtc = [DateTime]::UtcNow.ToString('O')
    $report.sourceHashesBefore = $sourceInputBefore
    $report.sourceHashesAfter = $sourceInputAfter
    $report.processes = @($processes)
    $report.moduleBuilds = @($moduleBuilds)
    $report.nativeBuilds = @($nativeBuilds)
    $report.nativeRuns = @($nativeRuns)
    $report.checks = @($checks)
    $report.errors = @($errors)
    $report.passed = $errors.Count -eq 0 -and @($checks | Where-Object { -not $_.passed }).Count -eq 0
    $report_path_dir = [IO.Path]::GetDirectoryName($reportPath)
    [IO.Directory]::CreateDirectory($report_path_dir) | Out-Null
    [IO.File]::WriteAllText($reportPath, (ConvertTo-Json -InputObject $report -Depth 90), $utf8)
}

Write-Output "Policy verifier report: $reportPath"
if (-not $report.passed) {
    foreach ($errorRecord in $errors) { Write-Error $errorRecord }
    exit 1
}
