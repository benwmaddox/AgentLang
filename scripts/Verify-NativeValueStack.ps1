#requires -Version 7.0
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runId = [Guid]::NewGuid().ToString('N')
$runDirectory = Join-Path $repo ".agentlang/owning-stack-003/verification-$runId"
$buildDirectory = Join-Path $runDirectory 'dotnet-artifacts'
$nativeDirectory = Join-Path $runDirectory 'native'
$nativeOutputDirectory = Join-Path $runDirectory 'native-output'
$repoTempDirectory = Join-Path $runDirectory 'compiler-temp'
$evidencePath = Join-Path $runDirectory 'verification-evidence.json'
$runnerProject = Join-Path $repo 'experiments/AgentLang.NativeValueStack/AgentLang.NativeValueStack.fsproj'
$arenaLifetimeTestProject = Join-Path $repo 'tests/AgentLang.Llvm.Tests/AgentLang.Llvm.Tests.fsproj'
$fixturePath = Join-Path $repo 'tests/fixtures/native-conformance/native-value-stack.json'
$nativeSourceDirectory = Join-Path $repo 'src/AgentLang.Llvm/native'
$nativeTestSource = Join-Path $nativeSourceDirectory 'owning_stack_runtime_test.c'
$nativeRuntimeSource = Join-Path $nativeSourceDirectory 'owning_stack_runtime.c'
$clangDefault = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\Llvm\x64\bin\clang.exe'
$timeoutMilliseconds = 600000
$utf8 = [Text.UTF8Encoding]::new($false)
$processes = [Collections.Generic.List[object]]::new()
$checks = [Collections.Generic.List[object]]::new()
$nativeUnitBuilds = [Collections.Generic.List[object]]::new()
$nativeUnitRuns = [Collections.Generic.List[object]]::new()
$sourceInputPaths = @(
    $runnerProject,
    $arenaLifetimeTestProject,
    (Join-Path $repo 'experiments/AgentLang.NativeValueStack/Program.fs'),
    (Join-Path $repo 'experiments/AgentLang.NativeValueStack/value-stack.flow'),
    (Join-Path $repo 'scripts/Verify-NativeValueStack.ps1'),
    $fixturePath,
    (Join-Path $repo 'src/AgentLang.Llvm/AgentLang.Llvm.fsproj'),
    (Join-Path $repo 'src/AgentLang.Core/AgentLang.Core.fsproj'),
    (Join-Path $nativeSourceDirectory 'owning_stack_runtime.h'),
    $nativeRuntimeSource,
    $nativeTestSource,
    (Join-Path $repo 'docs/STACK-ONLY-RESEARCH.md'),
    (Join-Path $repo 'docs/NATIVE-NOMINALS-IMPLEMENTATION.md'),
    (Join-Path $repo '.agentlang/owning-stack-003/implementation-plan.md'),
    (Join-Path $repo '.agentlang/owning-stack-003/acceptance-plan.md')
)
$sourceInputPaths += @(
    Get-ChildItem -LiteralPath (Join-Path $repo 'tests/AgentLang.Llvm.Tests') -File -Filter '*.fs' |
    Sort-Object FullName |
    ForEach-Object { [IO.Path]::GetFullPath($_.FullName) }
)
$sourceInputPaths += @(
    Get-ChildItem -LiteralPath (Join-Path $repo 'src/AgentLang.Core') -File -Filter '*.fs' |
    Sort-Object FullName |
    ForEach-Object { [IO.Path]::GetFullPath($_.FullName) }
)
$sourceInputPaths += @(
    Get-ChildItem -LiteralPath (Join-Path $repo 'src/AgentLang.Llvm') -File -Filter '*.fs' |
    Sort-Object FullName |
    ForEach-Object { [IO.Path]::GetFullPath($_.FullName) }
)
$report = [ordered]@{
    schemaVersion = 2
    kind = 'native-owning-value-stack-verification'
    runId = $runId
    startedUtc = [DateTime]::UtcNow.ToString('O')
    completedUtc = $null
    passed = $false
    runDirectory = $runDirectory
    evidencePath = $evidencePath
    sourceInputHashesBefore = @()
    sourceInputHashesAfter = @()
    sourceInputsStable = $false
    dependencyBuild = $null
    arenaLifetimeTestBuild = $null
    arenaLifetimeTestRun = $null
    compiler = $null
    nativeUnitBuilds = @()
    nativeUnitRuns = @()
    ubsan = $null
    experimentRun = $null
    generatedArtifacts = @()
    checks = @()
    failure = $null
}

function Add-Check([string]$Name, [bool]$Passed, $Details = $null) {
    $item = [ordered]@{ name = $Name; passed = $Passed }
    if ($null -ne $Details) { $item.details = $Details }
    $checks.Add($item)
    if (-not $Passed) {
        $detailText = if ($null -eq $Details) { '' } else { ConvertTo-Json -InputObject $Details -Depth 30 -Compress }
        throw "$Name failed: $detailText"
    }
}

function Get-Hash([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-SourceHashes {
    foreach ($path in $sourceInputPaths) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required source input is missing: $path"
        }
        [ordered]@{
            path = [IO.Path]::GetFullPath($path)
            bytes = (Get-Item -LiteralPath $path).Length
            sha256 = Get-Hash $path
        }
    }
}

function Resolve-Executable([string]$EnvironmentName, [string]$DefaultPath, [string]$CommandName) {
    $override = [Environment]::GetEnvironmentVariable($EnvironmentName)
    if (-not [string]::IsNullOrWhiteSpace($override)) {
        if ([IO.Path]::IsPathFullyQualified($override)) {
            if (-not (Test-Path -LiteralPath $override -PathType Leaf)) { throw "$EnvironmentName is not an executable: $override" }
            return [IO.Path]::GetFullPath($override)
        }
        $foundOverride = Get-Command -Name $override -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -eq $foundOverride) { throw "$EnvironmentName is not an executable: $override" }
        return [IO.Path]::GetFullPath($foundOverride.Source)
    }
    if (Test-Path -LiteralPath $DefaultPath -PathType Leaf) { return [IO.Path]::GetFullPath($DefaultPath) }
    $found = Get-Command -Name $CommandName -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $found) { throw "Required executable is missing: $CommandName" }
    [IO.Path]::GetFullPath($found.Source)
}

function Invoke-CapturedProcess {
    param(
        [string]$Name,
        [string]$File,
        [string[]]$Arguments,
        [string]$WorkingDirectory
    )
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $File
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Environment['TMP'] = $repoTempDirectory
    $startInfo.Environment['TEMP'] = $repoTempDirectory
    foreach ($argument in $Arguments) { [void]$startInfo.ArgumentList.Add([string]$argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $started = [DateTime]::UtcNow
    $stdout = ''
    $stderr = ''
    $exitCode = $null
    $timedOut = $false
    $startError = $null
    try {
        if (-not $process.Start()) { throw 'Process.Start returned false.' }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($timeoutMilliseconds)) {
            $timedOut = $true
            try { $process.Kill($true) } catch { }
            [void]$process.WaitForExit(5000)
        }
        if ($process.HasExited) { $exitCode = $process.ExitCode }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
    } catch {
        $startError = $_.Exception.Message
        if ($process -and -not $process.HasExited) {
            try { $process.Kill($true) } catch { }
        }
    } finally {
        $process.Dispose()
    }
    $result = [ordered]@{
        name = $Name
        file = $File
        arguments = @($Arguments)
        workingDirectory = $WorkingDirectory
        elapsedMilliseconds = [Math]::Round(([DateTime]::UtcNow - $started).TotalMilliseconds, 3)
        exitCode = $exitCode
        timedOut = $timedOut
        startError = $startError
        stdout = $stdout
        stderr = $stderr
    }
    $processes.Add($result)
    $safeName = ($Name -replace '[^a-zA-Z0-9_.-]', '_')
    [IO.File]::WriteAllText((Join-Path $runDirectory "$safeName.stdout.txt"), $stdout, $utf8)
    [IO.File]::WriteAllText((Join-Path $runDirectory "$safeName.stderr.txt"), $stderr, $utf8)
    $result
}

function Require-ProcessSuccess($Result, [string]$Description) {
    $success = -not $Result.timedOut -and $null -eq $Result.startError -and $Result.exitCode -eq 0
    $stdoutTail = ($Result.stdout -split '\r?\n' | Select-Object -Last 24) -join [Environment]::NewLine
    $stderrTail = ($Result.stderr -split '\r?\n' | Select-Object -Last 24) -join [Environment]::NewLine
    Add-Check $Description $success ([ordered]@{ exitCode = $Result.exitCode; timedOut = $Result.timedOut; startError = $Result.startError; stdoutTail = $stdoutTail; stderrTail = $stderrTail })
}

function Read-JsonFile([string]$Path) {
    Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable -Depth 100 -ErrorAction Stop
}

try {
    New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $buildDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $nativeDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $nativeOutputDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $repoTempDirectory -Force | Out-Null

    $report.sourceInputHashesBefore = @(Get-SourceHashes)
    $dotnet = Resolve-Executable 'AGENTLANG_DOTNET' '' 'dotnet'
    $clang = Resolve-Executable 'AGENTLANG_LLVM_CLANG' $clangDefault 'clang.exe'
    $report.compiler = [ordered]@{
        dotnetPath = $dotnet
        dotnetSha256 = Get-Hash $dotnet
        clangPath = $clang
        clangSha256 = Get-Hash $clang
    }

    $build = Invoke-CapturedProcess 'fresh-dotnet-build' $dotnet @(
        'build', $runnerProject, '--artifacts-path', $buildDirectory, '--configuration', 'Release', '--verbosity', 'minimal', '-p:NuGetAudit=false', '-m:1'
    ) $repo
    $report.dependencyBuild = $build
    Require-ProcessSuccess $build 'fresh Release build of the comparison runner and dependencies'
    $assemblies = @(Get-ChildItem -LiteralPath (Join-Path $buildDirectory 'bin') -Recurse -File -Filter 'AgentLang.NativeValueStack.dll')
    Add-Check 'fresh build emitted exactly one comparison runner' ($assemblies.Count -eq 1) ([ordered]@{ count = $assemblies.Count })
    $runnerAssembly = [IO.Path]::GetFullPath($assemblies[0].FullName)
    Add-Check 'comparison runner assembly is inside fresh artifact root' $runnerAssembly.StartsWith([IO.Path]::GetFullPath($buildDirectory), [StringComparison]::OrdinalIgnoreCase) $runnerAssembly

    $arenaLifetimeBuild = Invoke-CapturedProcess 'fresh-arena-lifetime-tests-build' $dotnet @(
        'build', $arenaLifetimeTestProject, '--artifacts-path', $buildDirectory, '--configuration', 'Release', '--verbosity', 'minimal', '-p:NuGetAudit=false', '-m:1'
    ) $repo
    $report.arenaLifetimeTestBuild = $arenaLifetimeBuild
    Require-ProcessSuccess $arenaLifetimeBuild 'fresh Release build of the compiler-proved arena-lifetime analysis tests'
    $arenaLifetimeAssemblies = @(Get-ChildItem -LiteralPath (Join-Path $buildDirectory 'bin') -Recurse -File -Filter 'AgentLang.Llvm.Tests.dll')
    Add-Check 'fresh build emitted exactly one arena-lifetime test assembly' ($arenaLifetimeAssemblies.Count -eq 1) ([ordered]@{ count = $arenaLifetimeAssemblies.Count })
    $arenaLifetimeAssembly = [IO.Path]::GetFullPath($arenaLifetimeAssemblies[0].FullName)
    Add-Check 'arena-lifetime test assembly is inside fresh artifact root' $arenaLifetimeAssembly.StartsWith([IO.Path]::GetFullPath($buildDirectory), [StringComparison]::OrdinalIgnoreCase) $arenaLifetimeAssembly
    $arenaLifetimeRun = Invoke-CapturedProcess 'compiler-proved-arena-lifetime-tests' $dotnet @($arenaLifetimeAssembly, '--arena-lifetime') $repo
    $report.arenaLifetimeTestRun = $arenaLifetimeRun
    Require-ProcessSuccess $arenaLifetimeRun 'compiler-proved arena-lifetime analysis cases passed'
    $arenaLifetimeAssertionMatch = [regex]::Match($arenaLifetimeRun.stdout, 'arena lifetime: (?<count>[0-9]+) assertions passed')
    Add-Check 'arena-lifetime test output reports its assertion count' $arenaLifetimeAssertionMatch.Success ([ordered]@{ stdout = $arenaLifetimeRun.stdout })
    $report.arenaLifetimeAssertions = [int]$arenaLifetimeAssertionMatch.Groups['count'].Value
    Add-Check 'arena-lifetime test suite exercised its static cases' ($report.arenaLifetimeAssertions -gt 0) ([ordered]@{ assertions = $report.arenaLifetimeAssertions })

    $storageOracle = (Read-JsonFile $fixturePath).storageRuntimeTestOracle
    foreach ($optimizationName in @('O0', 'O2')) {
        $nativeUnitPath = Join-Path $nativeDirectory "owning_stack_runtime_test_$optimizationName.exe"
        $nativeBuild = Invoke-CapturedProcess "fresh-owning-stack-c-build-$optimizationName" $clang @(
            '--target=x86_64-pc-windows-msvc', '-std=c11', '-Wall', '-Wextra', '-Werror', "-$optimizationName",
            "-I$nativeSourceDirectory", $nativeTestSource, $nativeRuntimeSource, '-o', $nativeUnitPath
        ) $repo
        $nativeUnitBuilds.Add($nativeBuild)
        Require-ProcessSuccess $nativeBuild "$optimizationName fresh native owning-stack storage test build"
        Add-Check "$optimizationName native storage test executable was freshly produced" (Test-Path -LiteralPath $nativeUnitPath -PathType Leaf) $nativeUnitPath
        $nativeRun = Invoke-CapturedProcess "owning-stack-native-storage-tests-$optimizationName" $nativeUnitPath @() $nativeDirectory
        $nativeUnitRuns.Add($nativeRun)
        Require-ProcessSuccess $nativeRun "$optimizationName native owning-stack storage tests passed"
        $jsonLine = @($nativeRun.stdout -split '\r?\n' | Where-Object { $_.TrimStart().StartsWith('{') } | Select-Object -Last 1)
        if ($jsonLine.Count -ne 1) { throw "$optimizationName native storage test did not emit its final JSON metrics line." }
        $nativeTestMetrics = $jsonLine[0] | ConvertFrom-Json -AsHashtable -Depth 50 -ErrorAction Stop
        $metricsPassed = $nativeTestMetrics.suite -ceq $storageOracle.suite -and
            $nativeTestMetrics.status -ceq 'pass' -and
            $nativeTestMetrics.cases -eq $storageOracle.cases -and
            $nativeTestMetrics.checks -eq $storageOracle.checks -and
            $nativeTestMetrics.fixed_baseline.suite -ceq $storageOracle.fixedBaseline.suite -and
            $nativeTestMetrics.fixed_baseline.cases -eq $storageOracle.fixedBaseline.cases -and
            $nativeTestMetrics.fixed_baseline.checks -eq $storageOracle.fixedBaseline.checks -and
            $nativeTestMetrics.fixed_baseline.peak_operand_bytes -eq $storageOracle.fixedBaseline.peakOperandBytes -and
            $nativeTestMetrics.fixed_baseline.peak_local_bytes -eq $storageOracle.fixedBaseline.peakLocalBytes -and
            $nativeTestMetrics.fixed_baseline.peak_cursor_bytes -eq $storageOracle.fixedBaseline.peakCursorBytes -and
            $nativeTestMetrics.fixed_baseline.max_stack_capacity_bytes -eq $storageOracle.fixedBaseline.maxStackCapacityBytes -and
            $nativeTestMetrics.dynamic_cases.cases -eq $storageOracle.dynamicCases.cases -and
            $nativeTestMetrics.dynamic_cases.checks -eq $storageOracle.dynamicCases.checks -and
            $nativeTestMetrics.abi.event_size_bytes -eq $storageOracle.abi.eventSizeBytes -and
            $nativeTestMetrics.abi.event_checksum_offset_bytes -eq $storageOracle.abi.eventChecksumOffsetBytes -and
            $nativeTestMetrics.abi.context_size_bytes -eq $storageOracle.abi.contextSizeBytes -and
            (ConvertTo-Json -InputObject $nativeTestMetrics.abi.context_offsets_bytes -Depth 20 -Compress) -ceq (ConvertTo-Json -InputObject $storageOracle.abi.contextOffsetsBytes -Depth 20 -Compress) -and
            $nativeTestMetrics.abi.layout_abi_version -eq $storageOracle.abi.layoutAbiVersion -and
            $nativeTestMetrics.abi.layout_size_bytes -eq $storageOracle.abi.layoutSizeBytes -and
            (ConvertTo-Json -InputObject $nativeTestMetrics.abi.layout_offsets_bytes -Depth 20 -Compress) -ceq (ConvertTo-Json -InputObject $storageOracle.abi.layoutOffsetsBytes -Depth 20 -Compress) -and
            $nativeTestMetrics.abi.type_descriptor_size_bytes -eq $storageOracle.abi.typeDescriptorSizeBytes -and
            $nativeTestMetrics.abi.field_descriptor_size_bytes -eq $storageOracle.abi.fieldDescriptorSizeBytes -and
            $nativeTestMetrics.abi.value_size_bytes -eq $storageOracle.abi.valueSizeBytes -and
            $nativeTestMetrics.abi.field_location_size_bytes -eq $storageOracle.abi.fieldLocationSizeBytes -and
            $nativeTestMetrics.payload.all_cases_peak_operand_bytes -eq $storageOracle.payload.allCasesPeakOperandBytes -and
            $nativeTestMetrics.payload.all_cases_peak_local_bytes -eq $storageOracle.payload.allCasesPeakLocalBytes -and
            $nativeTestMetrics.payload.fixed_peak_operand_bytes -eq $storageOracle.payload.fixedPeakOperandBytes -and
            $nativeTestMetrics.payload.fixed_peak_local_bytes -eq $storageOracle.payload.fixedPeakLocalBytes -and
            $nativeTestMetrics.reserved.all_cases_max_stack_capacity_bytes -eq $storageOracle.reserved.allCasesMaxStackCapacityBytes -and
            $nativeTestMetrics.reserved.all_cases_peak_cursor_bytes -eq $storageOracle.reserved.allCasesPeakCursorBytes -and
            $nativeTestMetrics.reserved.all_cases_peak_local_reserved_bytes -eq $storageOracle.reserved.allCasesPeakLocalReservedBytes -and
            $nativeTestMetrics.reserved.fixed_peak_cursor_bytes -eq $storageOracle.reserved.fixedPeakCursorBytes -and
            $nativeTestMetrics.metadata.bitmap_bytes_each -eq $storageOracle.metadata.bitmapBytesEach -and
            $nativeTestMetrics.metadata.bitmap_total_bytes -eq $storageOracle.metadata.bitmapTotalBytes -and
            $nativeTestMetrics.metadata.trace_event_count -eq $storageOracle.metadata.traceEventCount -and
            $nativeTestMetrics.metadata.trace_event_capacity -eq $storageOracle.metadata.traceEventCapacity -and
            $nativeTestMetrics.metadata.trace_bytes -eq $storageOracle.metadata.traceBytes -and
            $nativeTestMetrics.metadata.layout_validation_memo_table_bytes -eq $storageOracle.metadata.layoutValidationMemoTableBytes -and
            $nativeTestMetrics.metadata.layout_max_types -eq $storageOracle.metadata.layoutMaxTypes -and
            $nativeTestMetrics.metadata.layout_max_fields -eq $storageOracle.metadata.layoutMaxFields -and
            $nativeTestMetrics.metadata.layout_max_depth -eq $storageOracle.metadata.layoutMaxDepth
        Add-Check "$optimizationName native storage measurements match the independent fixture" $metricsPassed ([ordered]@{ expected = $storageOracle; actual = $nativeTestMetrics })
        $nativeRun['metrics'] = $nativeTestMetrics
    }
    $o0Metrics = ConvertTo-Json -InputObject $nativeUnitRuns[0]['metrics'] -Depth 50 -Compress
    $o2Metrics = ConvertTo-Json -InputObject $nativeUnitRuns[1]['metrics'] -Depth 50 -Compress
    Add-Check 'O0 and O2 storage tests report the same deterministic ABI and byte categories' ($o0Metrics -ceq $o2Metrics)
    $report.nativeUnitBuilds = @($nativeUnitBuilds)
    $report.nativeUnitRuns = @($nativeUnitRuns)

    $ubsanNormalPath = Join-Path $nativeDirectory 'owning_stack_runtime_test_ubsan_normal.exe'
    $ubsanNormalBuild = Invoke-CapturedProcess 'owning-stack-ubsan-normal-build' $clang @(
        '--target=x86_64-pc-windows-msvc', '-std=c11', '-Wall', '-Wextra', '-Werror', '-O1', '-fsanitize=undefined',
        "-I$nativeSourceDirectory", $nativeTestSource, $nativeRuntimeSource, '-o', $ubsanNormalPath
    ) $repo
    $ubsanNormal = [ordered]@{ build = $ubsanNormalBuild; outcome = $null; run = $null; metrics = $null; limitation = $null }
    if (-not $ubsanNormalBuild.timedOut -and $null -eq $ubsanNormalBuild.startError -and $ubsanNormalBuild.exitCode -eq 0) {
        $ubsanNormal.outcome = 'linked-and-ran'
        $ubsanNormalRun = Invoke-CapturedProcess 'owning-stack-ubsan-normal-run' $ubsanNormalPath @() $nativeDirectory
        $ubsanNormal.run = $ubsanNormalRun
        Require-ProcessSuccess $ubsanNormalRun 'normal UBSan owning-stack storage run passed'
        $normalJsonLine = @($ubsanNormalRun.stdout -split '\r?\n' | Where-Object { $_.TrimStart().StartsWith('{') } | Select-Object -Last 1)
        if ($normalJsonLine.Count -ne 1) { throw 'Normal UBSan run did not emit its final JSON metrics line.' }
        $ubsanNormal.metrics = $normalJsonLine[0] | ConvertFrom-Json -AsHashtable -Depth 50 -ErrorAction Stop
        Add-Check 'normal UBSan run preserved the storage oracle checks' ($ubsanNormal.metrics.status -ceq 'pass' -and $ubsanNormal.metrics.checks -eq $storageOracle.checks) $ubsanNormal.metrics
    } else {
        $normalLinkerOutput = $ubsanNormalBuild.stdout + "`n" + $ubsanNormalBuild.stderr
        $knownLinkFailure = -not $ubsanNormalBuild.timedOut -and $null -eq $ubsanNormalBuild.startError -and $ubsanNormalBuild.exitCode -eq 1120 -and $normalLinkerOutput -match 'LNK2019' -and $normalLinkerOutput -match 'sanitizer_symbolizer|sanitizer_win'
        Add-Check 'normal UBSan linker limitation is recorded separately from trap-mode success' $knownLinkFailure ([ordered]@{ exitCode = $ubsanNormalBuild.exitCode; linkerDiagnostic = $normalLinkerOutput })
        $ubsanNormal.outcome = 'link-unavailable'
        $ubsanNormal.limitation = 'The MSVC-target standalone UBSan runtime failed to link Windows symbolizer/runtime imports; the trap sanitizer is validated separately.'
    }

    $ubsanTrapPath = Join-Path $nativeDirectory 'owning_stack_runtime_test_ubsan_trap.exe'
    $ubsanTrapBuild = Invoke-CapturedProcess 'owning-stack-ubsan-trap-build' $clang @(
        '--target=x86_64-pc-windows-msvc', '-std=c11', '-Wall', '-Wextra', '-Werror', '-O1', '-fsanitize=undefined', '-fsanitize-trap=undefined',
        "-I$nativeSourceDirectory", $nativeTestSource, $nativeRuntimeSource, '-o', $ubsanTrapPath
    ) $repo
    Require-ProcessSuccess $ubsanTrapBuild 'trap-mode UBSan owning-stack storage build passed'
    $ubsanTrapRun = Invoke-CapturedProcess 'owning-stack-ubsan-trap-run' $ubsanTrapPath @() $nativeDirectory
    Require-ProcessSuccess $ubsanTrapRun 'trap-mode UBSan owning-stack storage run passed'
    $trapJsonLine = @($ubsanTrapRun.stdout -split '\r?\n' | Where-Object { $_.TrimStart().StartsWith('{') } | Select-Object -Last 1)
    if ($trapJsonLine.Count -ne 1) { throw 'Trap-mode UBSan run did not emit its final JSON metrics line.' }
    $ubsanTrapMetrics = $trapJsonLine[0] | ConvertFrom-Json -AsHashtable -Depth 50 -ErrorAction Stop
    $trapMetricsPass = $ubsanTrapMetrics.suite -ceq $storageOracle.suite -and $ubsanTrapMetrics.status -ceq 'pass' -and $ubsanTrapMetrics.cases -eq $storageOracle.cases -and $ubsanTrapMetrics.checks -eq $storageOracle.checks
    Add-Check 'trap-mode UBSan result matches the storage fixture' $trapMetricsPass $ubsanTrapMetrics
    Add-Check 'trap-mode UBSan output matches O0/O2 ABI and byte categories' ((ConvertTo-Json -InputObject $ubsanTrapMetrics -Depth 50 -Compress) -ceq $o0Metrics)
    $report.ubsan = [ordered]@{ normalRuntime = $ubsanNormal; trapVariant = [ordered]@{ build = $ubsanTrapBuild; run = $ubsanTrapRun; metrics = $ubsanTrapMetrics } }

    $experimentEvidencePath = Join-Path $runDirectory 'native-value-stack-evidence.json'
    $experiment = Invoke-CapturedProcess 'same-ir-cross-backend-experiment' $dotnet @(
        $runnerAssembly, $fixturePath, $nativeOutputDirectory, $experimentEvidencePath
    ) $repo
    $report.experimentRun = $experiment
    Require-ProcessSuccess $experiment 'fixed interpreter/older sharedgraph ABI3 controls plus String and Option/Result interpreter/owning O0/O2 experiments passed'
    Add-Check 'experiment evidence file exists' (Test-Path -LiteralPath $experimentEvidencePath -PathType Leaf) $experimentEvidencePath
    $experimentEvidence = Read-JsonFile $experimentEvidencePath
    Add-Check 'experiment evidence reports success' ([bool]$experimentEvidence.passed) ([ordered]@{ failureCount = $experimentEvidence.failureCount; failure = $experimentEvidence.failure })
    $enumOracle = (Read-JsonFile $fixturePath).enumConformance
    $enumRuns = @($experimentEvidence.enumRuns)
    $enumCaseCount = @($enumOracle.cases).Count
    $enumEqualityCaseCount = @($enumOracle.equalityCases).Count
    $enumInvalidOrdinalCount = @($enumOracle.rawNativeEntry.invalidOrdinals).Count
    $enumMalformedInputCount = @($enumOracle.rawNativeEntry.malformedInputs).Count
    $enumOptimizationNames = @($enumRuns | ForEach-Object { $_.optimization } | Sort-Object) -join ','
    $enumRunsMissingCoverage = @($enumRuns | Where-Object {
        $_.caseCount -ne $enumCaseCount -or
        $_.equalityCaseCount -ne $enumEqualityCaseCount -or
        $_.invalidOrdinalCount -ne $enumInvalidOrdinalCount -or
        $_.malformedInputCount -ne $enumMalformedInputCount
    }).Count
    $enumCoveragePassed = $enumRuns.Count -eq 2 -and
        $enumOptimizationNames -ceq 'O0,O2' -and
        $enumRunsMissingCoverage -eq 0
    Add-Check 'closed enum parity covers every fixture case, equality pair, and raw-entry rejection at O0/O2' $enumCoveragePassed ([ordered]@{
        expectedOptimizations = @('O0', 'O2')
        expectedCaseCount = $enumCaseCount
        expectedEqualityCaseCount = $enumEqualityCaseCount
        expectedInvalidOrdinalCount = $enumInvalidOrdinalCount
        expectedMalformedInputCount = $enumMalformedInputCount
        actualOptimizationNames = $enumOptimizationNames
        incompleteRunCount = $enumRunsMissingCoverage
        actualRuns = $enumRuns
    })
    $sumOracle = (Read-JsonFile $fixturePath).sumConformance
    $sumRuns = @($experimentEvidence.sumRuns)
    $sumCaseCount = @($sumOracle.cases).Count
    $sumEqualityCaseCount = @($sumOracle.equalityCases).Count
    $sumMatchCaseCount = @($sumOracle.matches.Keys).Count
    $sumInvalidTagCount = @($sumOracle.rawNativeEntry.invalidTags).Count
    $sumMalformedInputCount = @($sumOracle.rawNativeEntry.malformedInputs).Count
    $sumRawInputCount = $sumInvalidTagCount + $sumMalformedInputCount
    $sumBranchJoinCaseCount = @($sumOracle.branchJoinCases).Count
    $sumOptimizationNames = @($sumRuns | ForEach-Object { $_.optimization } | Sort-Object) -join ','
    $sumRunsMissingCoverage = @($sumRuns | Where-Object {
        $_.caseCount -ne $sumCaseCount -or
        $_.equalityCaseCount -ne $sumEqualityCaseCount -or
        $_.matchCaseCount -ne $sumMatchCaseCount -or
        $_.invalidTagCount -ne $sumInvalidTagCount -or
        $_.malformedInputCount -ne $sumMalformedInputCount -or
        $_.rawInputCount -ne $sumRawInputCount -or
        $_.hostInputCount -ne $sumCaseCount -or
        $_.localCallCount -ne $sumCaseCount -or
        $_.retainedShortCount -ne $sumCaseCount -or
        $_.branchJoinCaseCount -ne $sumBranchJoinCaseCount -or
        $_.stackCapacityCaseCount -ne 1 -or
        $_.unwindCaseCount -ne 1
    }).Count
    $sumCoveragePassed = $sumRuns.Count -eq 2 -and
        $sumOptimizationNames -ceq 'O0,O2' -and
        $sumRunsMissingCoverage -eq 0
    Add-Check 'Option/Result parity covers every pinned case, equality, match, host input, capacity boundary, and raw-entry rejection at owning O0/O2' $sumCoveragePassed ([ordered]@{
        expectedOptimizations = @('O0', 'O2')
        expectedCaseCount = $sumCaseCount
        expectedEqualityCaseCount = $sumEqualityCaseCount
        expectedMatchCaseCount = $sumMatchCaseCount
        expectedInvalidTagCount = $sumInvalidTagCount
        expectedMalformedInputCount = $sumMalformedInputCount
        expectedRawInputCount = $sumRawInputCount
        expectedBranchJoinCaseCount = $sumBranchJoinCaseCount
        actualOptimizationNames = $sumOptimizationNames
        incompleteRunCount = $sumRunsMissingCoverage
        actualRuns = $sumRuns
    })

    $nativeFixture = Read-JsonFile $fixturePath
    $nominalOracle = $nativeFixture['nominalIntConformance']
    $nominalSignedExpected = @(
        [ordered]@{ name = 'zero'; value = '0'; intBytesHex = '0000000000000000' }
        [ordered]@{ name = 'negative'; value = '-42'; intBytesHex = 'd6ffffffffffffff' }
        [ordered]@{ name = 'minimum'; value = '-9223372036854775808'; intBytesHex = '0000000000000080' }
        [ordered]@{ name = 'maximum'; value = '9223372036854775807'; intBytesHex = 'ffffffffffffff7f' }
    )
    $nominalExpectedSignedNames = @($nominalSignedExpected | ForEach-Object { $_.name })
    $nominalOraclePassed = $false
    $nominalFixtureSignedNames = @()
    $nominalOwnerBytes = $null
    $nominalOwnerRange = $null
    if ($null -ne $nominalOracle) {
        $nominalSignedFixtures = @($nominalOracle['signedFixtures'])
        $nominalFixtureSignedNames = @($nominalSignedFixtures | ForEach-Object { [string]$_['name'] })
        $nominalSignedMap = @{}
        foreach ($signedFixture in $nominalSignedFixtures) {
            $nominalSignedMap[[string]$signedFixture['name']] = $signedFixture
        }
        $nominalSignedBytesPassed = $nominalSignedFixtures.Count -eq $nominalSignedExpected.Count
        foreach ($expectedFixture in $nominalSignedExpected) {
            $actualFixture = $nominalSignedMap[[string]$expectedFixture.name]
            $expectedPairBytes = $expectedFixture.intBytesHex + $expectedFixture.intBytesHex
            $casePassed = $null -ne $actualFixture -and
                [string]$actualFixture['value'] -ceq $expectedFixture.value -and
                [string]$actualFixture['intBytesHex'] -ceq $expectedFixture.intBytesHex -and
                [string]$actualFixture['pairBytesHex'] -ceq $expectedPairBytes
            $nominalSignedBytesPassed = $nominalSignedBytesPassed -and $casePassed
        }
        $nominalTypeIds = $nominalOracle['identityProgram']['typeIds']
        $nominalIdentityIdsPassed = [int]$nominalTypeIds['Meters'] -eq 4 -and [int]$nominalTypeIds['OrderId'] -eq 5
        $nominalOwner = $nominalOracle['ownerEnvelope']
        $nominalOwnerBytes = [string]$nominalOwner['retainedBytesHex']
        $nominalOwnerPassed = [string]$nominalOwner['ownerValue'] -ceq '-42' -and
            [string]$nominalOwner['tail'] -ceq 'tail' -and
            $nominalOwnerBytes -ceq 'd6ffffffffffffff04000000000000007400610069006c00' -and
            [string]$nominalOwner['projectedOwnerBytesHex'] -ceq 'd6ffffffffffffff'
        $nominalOwnerRange = $nominalOracle['ownerRangeTrace']['expectedDescriptorTransfer']
        $nominalOwnerRangePassed = [int]$nominalOwnerRange['typeId'] -eq 1 -and
            [int]$nominalOracle['ownerRangeTrace']['literalDeepCopyBytes'] -eq 80 -and
            [int]$nominalOwnerRange['offsetBytes'] -eq 0 -and
            [int]$nominalOwnerRange['sourceOffsetBytesOwnerEnd'] -eq 24 -and
            [int]$nominalOwnerRange['sourceExtentBytesPayloadExtent'] -eq 8
        $nominalAggregateBytesPassed =
            [string]$nominalOracle['option']['someBytesHex'] -ceq '0000000000000000d6ffffffffffffff' -and
            [string]$nominalOracle['option']['noneBytesHex'] -ceq '0100000000000000' -and
            [string]$nominalOracle['result']['okBytesHex'] -ceq '0000000000000000d6ffffffffffffff' -and
            [string]$nominalOracle['result']['errorBytesHex'] -ceq '0100000000000000d6ffffffffffffff' -and
            [string]$nominalOracle['booleans']['trueBytesHex'] -ceq '0100000000000000' -and
            [string]$nominalOracle['booleans']['falseBytesHex'] -ceq '0000000000000000' -and
            [int]$nominalOracle['capacityAndFailure']['optionSomeExactStackCapacityBytes'] -eq 24
        $nominalErrors = $nominalOracle['expectedErrors']
        $nominalErrorsPassed = [string]$nominalErrors['hostInputExceptionType'] -ceq 'System.ArgumentException' -and
            [string]$nominalErrors['hostInputParameterName'] -ceq 'values' -and
            [string]$nominalErrors['wrongAccessorDiagnosticCode'] -ceq 'TYPE_STACK_MISMATCH'
        $nominalOraclePassed = [int]$nativeFixture['schemaVersion'] -eq 3 -and
            $nominalIdentityIdsPassed -and $nominalSignedBytesPassed -and
            ($nominalFixtureSignedNames -join ',') -ceq ($nominalExpectedSignedNames -join ',') -and
            $nominalOwnerPassed -and $nominalOwnerRangePassed -and $nominalAggregateBytesPassed -and $nominalErrorsPassed
    }
    Add-Check 'nominal Int fixture pins schema 3, distinct TypeIds, signed bytes, aggregate bytes, and owner-range oracle' $nominalOraclePassed ([ordered]@{
        expectedSchemaVersion = 3
        actualSchemaVersion = $nativeFixture['schemaVersion']
        expectedTypeIds = [ordered]@{ Meters = 4; OrderId = 5 }
        expectedSignedFixtures = $nominalSignedExpected
        actualSignedFixtureNames = $nominalFixtureSignedNames
        expectedOwnerBytesHex = 'd6ffffffffffffff04000000000000007400610069006c00'
        actualOwnerBytesHex = $nominalOwnerBytes
        expectedOwnerDescriptorTransfer = [ordered]@{ typeId = 1; offsetBytes = 0; sourceOffsetBytesOwnerEnd = 24; sourceExtentBytesPayloadExtent = 8 }
        actualOwnerDescriptorTransfer = $nominalOwnerRange
    })

    $nominalRuns = @()
    if ($null -ne $experimentEvidence['nominalIntRuns']) { $nominalRuns = @($experimentEvidence['nominalIntRuns']) }
    $nominalRunOptimizationNames = @($nominalRuns | ForEach-Object { [string]$_['optimization'] } | Sort-Object) -join ','
    $nominalExpectedOwnerEnd = 24
    $nominalExpectedScalarExtent = 8
    $nominalRunsMissingCoverage = @($nominalRuns | Where-Object {
        $_['signedFixtureCount'] -ne $nominalExpectedSignedNames.Count -or
        $_['unsupportedDefinitionCount'] -ne 3 -or
        $_['ownerRangeTransferCount'] -lt 1 -or
        $_['ownerEndBytes'] -ne $nominalExpectedOwnerEnd -or
        $_['scalarPayloadExtentBytes'] -ne $nominalExpectedScalarExtent
    }).Count
    $nominalRunCoveragePassed = $nominalRuns.Count -eq 2 -and
        $nominalRunOptimizationNames -ceq 'O0,O2' -and
        $nominalRunsMissingCoverage -eq 0
    Add-Check 'nominal Int report has complete O0/O2 signed, unsupported, and owner-range summaries' $nominalRunCoveragePassed ([ordered]@{
        expectedOptimizations = @('O0', 'O2')
        expectedSignedFixtureCount = $nominalExpectedSignedNames.Count
        expectedUnsupportedDefinitionCount = 3
        expectedOwnerEndBytes = $nominalExpectedOwnerEnd
        expectedScalarPayloadExtentBytes = $nominalExpectedScalarExtent
        actualOptimizationNames = $nominalRunOptimizationNames
        incompleteRunCount = $nominalRunsMissingCoverage
        actualRuns = $nominalRuns
    })

    $nominalCaseSuffixes = [Collections.Generic.List[string]]::new()
    foreach ($suffix in @(
        'isolated-type-ids',
        'distinct-fixed-int-layouts',
        'locals-and-calls-preserve-meters',
        'locals-and-calls-preserve-order-id',
        'reject-bare-int-for-meters-atomically',
        'reject-wrong-nominal-for-meters-atomically',
        'reject-same-name-record-for-meters-atomically',
        'reject-wrong-nominal-for-order-id-atomically',
        'wrong-accessor-rejected-by-verifier',
        'record-construction-and-tail-bytes',
        'record-host-roundtrip',
        'scalar-first-record-preserves-full-owner-end',
        'owner-range-trace-complete',
        'record-equality-retains-nominal-field',
        'option-some-construction',
        'option-none-inactive-payload',
        'option-some-host-roundtrip',
        'option-none-host-roundtrip',
        'option-some-match-payload',
        'option-none-match-payload',
        'option-equality-compares-wrapped-payload',
        'result-ok-wraps-meters',
        'result-error-wraps-order-id',
        'result-ok-host-roundtrip',
        'result-error-host-roundtrip',
        'result-ok-match-retains-meter-identity',
        'result-error-match-retains-meter-identity',
        'result-equality-distinguishes-inactive-tag',
        'option-capacity-one-byte-short-is-atomic',
        'sum-retained-capacity-short-is-atomic',
        'failure-after-nominal-sum-unwinds-without-publishing'
    )) { $nominalCaseSuffixes.Add($suffix) }
    foreach ($caseName in $nominalExpectedSignedNames) {
        $nominalCaseSuffixes.Add("$caseName/host-nominal-identity")
        $nominalCaseSuffixes.Add("$caseName/host-identity-trace")
        $nominalCaseSuffixes.Add("$caseName/wrap-unwrap-retags")
    }
    foreach ($unsupportedName in @('BoolTag', 'UnvalidatedStringTag', 'FloatTag')) {
        $nominalCaseSuffixes.Add("unsupported-$unsupportedName-is-explicit")
    }
    $nominalExpectedCheckNames = [Collections.Generic.List[string]]::new()
    foreach ($optimizationName in @('O0', 'O2')) {
        foreach ($suffix in $nominalCaseSuffixes) {
            $nominalExpectedCheckNames.Add("nominal-int/$optimizationName/$suffix")
        }
    }
    $nominalEvidenceChecks = @()
    if ($null -ne $experimentEvidence['checks']) {
        $nominalEvidenceChecks = @($experimentEvidence['checks'] | Where-Object { [string]$_['name'] -like 'nominal-int/*' })
    }
    $nominalMissingChecks = [Collections.Generic.List[string]]::new()
    $nominalFailingChecks = [Collections.Generic.List[string]]::new()
    foreach ($expectedName in $nominalExpectedCheckNames) {
        $matchingChecks = @($nominalEvidenceChecks | Where-Object { [string]$_['name'] -ceq $expectedName })
        if ($matchingChecks.Count -ne 1) { $nominalMissingChecks.Add("$expectedName (count=$($matchingChecks.Count))") }
        elseif (-not [bool]$matchingChecks[0]['passed']) { $nominalFailingChecks.Add($expectedName) }
    }
    $nominalCheckCoveragePassed = $nominalCaseSuffixes.Count -eq 46 -and
        $nominalMissingChecks.Count -eq 0 -and $nominalFailingChecks.Count -eq 0
    Add-Check 'nominal Int case checks cover every pinned O0/O2 identity, wrap, aggregate, owner, capacity, rejection, and non-Int unsupported case' $nominalCheckCoveragePassed ([ordered]@{
        expectedCasesPerOptimization = $nominalCaseSuffixes.Count
        expectedTotalCheckCount = $nominalExpectedCheckNames.Count
        actualNominalCheckCount = $nominalEvidenceChecks.Count
        missingOrDuplicateChecks = @($nominalMissingChecks)
        failingChecks = @($nominalFailingChecks)
    })

    $positiveOracle = $nativeFixture['positiveIdConformance']
    $positiveFixturePassed = $false
    $positiveFixtureFalseNames = @()
    if ($null -ne $positiveOracle) {
        $positiveIds = $positiveOracle['identityProgram']['typeIds']
        $positiveFalseCases = @($positiveOracle['constructor']['falseCases'])
        $positiveFixtureFalseNames = @($positiveFalseCases | ForEach-Object { [string]$_['name'] })
        $positiveFalseDiagnosticsPassed = $positiveFalseCases.Count -eq 2
        foreach ($falseCase in $positiveFalseCases) {
            $expectedValue = if ([string]$falseCase['name'] -ceq 'zero') { '0' } else { '-1' }
            $diagnostic = $falseCase['diagnostic']
            $span = $diagnostic['span']
            $positiveFalseDiagnosticsPassed = $positiveFalseDiagnosticsPassed -and
                [string]$falseCase['value'] -ceq $expectedValue -and
                [string]$diagnostic['code'] -ceq 'REFINEMENT_FAILED' -and
                [string]$diagnostic['word'] -ceq 'PositiveId.construct' -and
                ([string[]]$diagnostic['expected'] -join ',') -ceq 'validator returns true' -and
                ([string[]]$diagnostic['actual'] -join ',') -ceq 'false' -and
                [string]$span['file'] -ceq '<native-value-stack-scalar-PositiveId>' -and
                [int]$span['line'] -eq 1 -and [int]$span['column'] -eq 1 -and [int]$span['length'] -eq 1
        }
        $hostFalse = $positiveOracle['hostFalseDiagnostic']
        $hostFalseSpan = $hostFalse['span']
        $hostShape = $positiveOracle['hostShapeRejections']
        $hostShapeCases = @($hostShape['cases'])
        $hostShapeCaseNames = @($hostShapeCases | ForEach-Object { [string]$_['name'] })
        $hostShapeFixturePassed =
            [string]$hostShape['exceptionType'] -ceq 'System.ArgumentException' -and
            [string]$hostShape['parameterName'] -ceq 'values' -and
            $hostShapeCases.Count -eq 2 -and
            ($hostShapeCaseNames -join ',') -ceq 'bare-int,wrong-nominal' -and
            [string]$hostShapeCases[0]['kind'] -ceq 'int' -and [string]$hostShapeCases[0]['value'] -ceq '1' -and
            [string]$hostShapeCases[1]['kind'] -ceq 'named-int' -and
            [string]$hostShapeCases[1]['typeName'] -ceq 'OrderId' -and
            [string]$hostShapeCases[1]['value'] -ceq '1'
        $overflowDiagnostic = $positiveOracle['overflowValidator']['diagnostic']
        $overflowSpan = $overflowDiagnostic['span']
        $bodyFailureDiagnostic = $positiveOracle['bodyFailureDiagnostic']
        $bodyFailureSpan = $bodyFailureDiagnostic['span']
        $nested = $positiveOracle['nestedHostInputs']
        $positiveFixturePassed =
            [int]$nativeFixture['schemaVersion'] -eq 3 -and
            [int]$positiveIds['OrderId'] -eq 4 -and [int]$positiveIds['PositiveId'] -eq 5 -and
            [int]$positiveIds['payloadBytes'] -eq 8 -and
            [string]$positiveOracle['identityProgram']['pairBytesHex'] -ceq '0100000000000000d6ffffffffffffff' -and
            [string]$positiveOracle['constructor']['success']['value'] -ceq '1' -and
            [string]$positiveOracle['constructor']['success']['intBytesHex'] -ceq '0100000000000000' -and
            ($positiveFixtureFalseNames -join ',') -ceq 'zero,negative-one' -and $positiveFalseDiagnosticsPassed -and
            [string]$positiveOracle['hostInputs']['positiveIdBytesHex'] -ceq '0100000000000000' -and
            $hostShapeFixturePassed -and
            [string]$nested['envelopeBytesHex'] -ceq '010000000000000002000000000000006f006b0000000000' -and
            [string]$nested['optionSomeBytesHex'] -ceq '00000000000000000100000000000000' -and
            [string]$nested['optionNoneBytesHex'] -ceq '0100000000000000' -and
            [string]$nested['resultOkBytesHex'] -ceq '00000000000000000100000000000000' -and
            [string]$nested['resultErrorBytesHex'] -ceq '01000000000000000100000000000000' -and
            [string]$nested['resultInactiveBytesHex'] -ceq '00000000000000000700000000000000' -and
            [string]$hostFalse['code'] -ceq 'REFINEMENT_FAILED' -and
            [string]$hostFalse['message'] -ceq "Value does not satisfy PositiveId's refinement validator." -and
            [string]$hostFalse['word'] -ceq 'PositiveId.construct' -and
            ([string[]]$hostFalse['expected'] -join ',') -ceq 'validator returns true' -and
            ([string[]]$hostFalse['actual'] -join ',') -ceq 'false' -and
            [string]$hostFalseSpan['file'] -ceq '<native-value-stack-scalar-PositiveId>' -and
            [int]$hostFalseSpan['line'] -eq 1 -and [int]$hostFalseSpan['column'] -eq 1 -and [int]$hostFalseSpan['length'] -eq 1 -and
            [string]$overflowDiagnostic['code'] -ceq 'RUNTIME_OVERFLOW' -and
            [string]$overflowDiagnostic['word'] -ceq 'divide' -and
            [string]$overflowDiagnostic['message'] -ceq 'Integer division overflow.' -and
            [string]$overflowSpan['file'] -ceq '<native-value-stack-positive-id-overflow-validator-divide>' -and [int]$overflowSpan['line'] -eq 1 -and [int]$overflowSpan['column'] -eq 4 -and [int]$overflowSpan['length'] -eq 1 -and
            [string]$bodyFailureDiagnostic['code'] -ceq 'RUNTIME_DIVIDE_BY_ZERO' -and
            [string]$bodyFailureDiagnostic['word'] -ceq 'divide' -and
            [string]$bodyFailureDiagnostic['message'] -ceq 'Integer division by zero.' -and
            [string]$bodyFailureSpan['file'] -ceq '<native-value-stack-positive-id-body-failure-divide>' -and [int]$bodyFailureSpan['line'] -eq 1 -and [int]$bodyFailureSpan['column'] -eq 3 -and [int]$bodyFailureSpan['length'] -eq 1 -and
            [string]$positiveOracle['sentinelByteHex'] -ceq 'a5'
    }
    Add-Check 'PositiveId fixture pins distinct Int TypeIds, constructor diagnostics, nested bytes, inactive alternatives, and validator errors' $positiveFixturePassed ([ordered]@{
        expectedTypeIds = [ordered]@{ OrderId = 4; PositiveId = 5 }
        actualTypeIds = if ($null -eq $positiveOracle) { $null } else { $positiveOracle['identityProgram']['typeIds'] }
        expectedFalseCaseNames = @('zero', 'negative-one')
        actualFalseCaseNames = $positiveFixtureFalseNames
        expectedHostShapeCases = @('bare-int', 'wrong-nominal OrderId')
        actualHostShapeCases = $hostShapeCaseNames
        expectedPositiveIdBytesHex = '0100000000000000'
        expectedEnvelopeBytesHex = '010000000000000002000000000000006f006b0000000000'
        expectedHostFailure = 'REFINEMENT_FAILED / PositiveId.construct / scalar declaration span'
        expectedOverflow = 'RUNTIME_OVERFLOW / divide / <native-value-stack-positive-id-overflow-validator-divide>:1:4:1'
        expectedBodyFailure = 'RUNTIME_DIVIDE_BY_ZERO / divide / <native-value-stack-positive-id-body-failure-divide>:1:3:1'
    })

    $positiveCaseSuffixes = @(
        'type-identities-and-eight-byte-payloads',
        'constructor-success-one',
        'constructor-reject-zero-parity',
        'constructor-reject-negative-one-parity',
        'input-only-validator-closure',
        'distinct-typeids-and-pair-bytes',
        'record-field-host-validation',
        'record-field-negative-rejected-atomically',
        'option-some-active-host-validation',
        'option-some-invalid-payload-rejected-atomically',
        'option-none-inactive-payload-skips-validator',
        'result-ok-active-host-validation',
        'result-ok-invalid-payload-rejected-atomically',
        'result-error-active-host-validation',
        'result-error-invalid-payload-rejected-atomically',
        'result-inactive-alternative-skips-validator',
        'overflow-validator-preserves-runtime-overflow',
        'overflowing-host-validator-preserves-runtime-overflow',
        'input-validation-precedes-pure-body-failure'
    )
    $positiveRuns = @()
    if ($null -ne $experimentEvidence['positiveIdRuns']) { $positiveRuns = @($experimentEvidence['positiveIdRuns']) }
    $positiveRunOptimizationNames = @($positiveRuns | ForEach-Object { [string]$_['optimization'] } | Sort-Object) -join ','
    $positiveExpectedProvenanceCoverage = 'Refined provenance is covered by the existing measured nominal owner-range tests; PositiveId has no separate OwnerEnd trace in this suite.'
    $positiveRunsMissingCoverage = @($positiveRuns | Where-Object {
        $_['caseCount'] -ne $positiveCaseSuffixes.Count -or
        $_['constructorFailureCount'] -ne 2 -or
        $_['nestedHostInputCount'] -ne 10 -or
        $_['overflowValidatorCount'] -ne 1 -or
        $_['inputOnlyValidatorClosureCount'] -ne 1 -or
        $_['divideDiagnosticSpanSemantics'] -cne 'Interpreter divide errors have no span; native diagnostics retain verified instruction-site spans.' -or
        $_['provenanceCoverage'] -cne $positiveExpectedProvenanceCoverage -or
        $_['positiveIdTypeId'] -ne 5 -or $_['orderIdTypeId'] -ne 4
    }).Count
    $positiveRunCoveragePassed = $positiveRuns.Count -eq 2 -and
        $positiveRunOptimizationNames -ceq 'O0,O2' -and
        $positiveRunsMissingCoverage -eq 0
    Add-Check 'PositiveId report has complete O0/O2 constructor, ingress, closure, and overflow summaries' $positiveRunCoveragePassed ([ordered]@{
        expectedOptimizations = @('O0', 'O2')
        expectedCaseCountPerOptimization = $positiveCaseSuffixes.Count
        expectedTypeIds = [ordered]@{ OrderId = 4; PositiveId = 5 }
        expectedProvenanceCoverage = $positiveExpectedProvenanceCoverage
        actualOptimizationNames = $positiveRunOptimizationNames
        incompleteRunCount = $positiveRunsMissingCoverage
        actualRuns = $positiveRuns
    })
    $positiveExpectedCheckNames = [Collections.Generic.List[string]]::new()
    foreach ($optimizationName in @('O0', 'O2')) {
        foreach ($suffix in $positiveCaseSuffixes) {
            $positiveExpectedCheckNames.Add("positive-id/$optimizationName/$suffix")
        }
    }
    $positiveEvidenceChecks = @()
    if ($null -ne $experimentEvidence['checks']) {
        $positiveEvidenceChecks = @($experimentEvidence['checks'] | Where-Object { [string]$_['name'] -like 'positive-id/*' })
    }
    $positiveMissingChecks = [Collections.Generic.List[string]]::new()
    $positiveFailingChecks = [Collections.Generic.List[string]]::new()
    foreach ($expectedName in $positiveExpectedCheckNames) {
        $matchingChecks = @($positiveEvidenceChecks | Where-Object { [string]$_['name'] -ceq $expectedName })
        if ($matchingChecks.Count -ne 1) { $positiveMissingChecks.Add("$expectedName (count=$($matchingChecks.Count))") }
        elseif (-not [bool]$matchingChecks[0]['passed']) { $positiveFailingChecks.Add($expectedName) }
    }
    $positiveCheckCoveragePassed = $positiveCaseSuffixes.Count -eq 19 -and
        $positiveExpectedCheckNames.Count -eq 38 -and
        $positiveEvidenceChecks.Count -eq $positiveExpectedCheckNames.Count -and
        $positiveMissingChecks.Count -eq 0 -and $positiveFailingChecks.Count -eq 0
    Add-Check 'PositiveId cases cover exact O0/O2 constructor parity, raw ingress shapes, inactive alternatives, and failure atomicity' $positiveCheckCoveragePassed ([ordered]@{
        expectedCasesPerOptimization = $positiveCaseSuffixes.Count
        expectedTotalCheckCount = $positiveExpectedCheckNames.Count
        actualPositiveIdCheckCount = $positiveEvidenceChecks.Count
        unexpectedPositiveIdCheckCount = $positiveEvidenceChecks.Count - $positiveExpectedCheckNames.Count
        missingOrDuplicateChecks = @($positiveMissingChecks)
        failingChecks = @($positiveFailingChecks)
    })

    $refinedOracle = $nativeFixture['nonEmptyStringConformance']
    $refinedIdentity = $refinedOracle['identityProgram']
    $refinedIdentityIds = $refinedIdentity['typeIds']
    $refinedSuccess = $refinedOracle['constructor']['success']
    $refinedEmpty = $refinedOracle['constructor']['empty']
    $refinedFalse = $refinedOracle['hostFalseDiagnostic']
    $refinedFalseSpan = $refinedFalse['span']
    $refinedHost = $refinedOracle['hostInputs']
    $refinedRaw = $refinedOracle['rawEntry']
    $refinedNested = $refinedOracle['nestedProgram']
    $refinedNestedIds = $refinedNested['typeIds']
    $refinedRecord = $refinedNested['record']
    $refinedOption = $refinedNested['option']
    $refinedResult = $refinedNested['result']
    $refinedInactive = $refinedResult['inactiveOkInt']
    $refinedRuntime = $refinedOracle['runtimeValidatorFailure']['diagnostic']
    $refinedRuntimeSpan = $refinedRuntime['span']
    $refinedUnsupportedString = $refinedOracle['unsupportedUnvalidatedStringDiagnostic']
    $refinedOwner = $refinedOracle['ownerRangeTrace']
    $refinedTransfer = $refinedOwner['expectedDescriptorTransfer']
    $refinedUtf16 = $refinedHost['utf16Cases']
    $refinedFixturePassed =
        $null -ne $refinedOracle -and
        [string]$refinedOracle['encoding'] -ceq 'String payload is an eight-byte UTF-16 code-unit count and zero reserved word, followed by UTF-16LE data and zero padding to an eight-byte extent.' -and
        [int]$refinedIdentityIds['NonEmptyString'] -eq 4 -and [int]$refinedIdentityIds['String'] -eq 5 -and
        [int]$refinedIdentity['stringKind'] -eq 5 -and
        [int]$refinedIdentity['minimumPayloadBytes'] -eq 8 -and [int]$refinedIdentity['minimumExtentBytes'] -eq 8 -and
        [string]$refinedSuccess['value'] -ceq 'ok' -and
        [string]$refinedSuccess['stringBytesHex'] -ceq '02000000000000006f006b0000000000' -and
        [int]$refinedSuccess['payloadBytes'] -eq 12 -and [int]$refinedSuccess['extentBytes'] -eq 16 -and
        [string]$refinedEmpty['bytesHex'] -ceq '0000000000000000' -and
        [string]$refinedFalse['code'] -ceq 'REFINEMENT_FAILED' -and
        [string]$refinedFalse['message'] -ceq "Value does not satisfy NonEmptyString's refinement validator." -and
        [string]$refinedFalse['word'] -ceq 'NonEmptyString.construct' -and
        ([string[]]$refinedFalse['expected'] -join ',') -ceq 'validator returns true' -and
        ([string[]]$refinedFalse['actual'] -join ',') -ceq 'false' -and
        [string]$refinedFalseSpan['file'] -ceq '<native-value-stack-scalar-NonEmptyString>' -and
        [int]$refinedFalseSpan['line'] -eq 1 -and [int]$refinedFalseSpan['column'] -eq 1 -and [int]$refinedFalseSpan['length'] -eq 1 -and
        [string]$refinedHost['validStringBytesHex'] -ceq '02000000000000006f006b0000000000' -and
        [string]$refinedHost['wrongNominalTypeName'] -ceq 'OtherString' -and
        [string]$refinedOracle['hostShapeRejections']['exceptionType'] -ceq 'System.ArgumentException' -and
        [string]$refinedOracle['hostShapeRejections']['parameterName'] -ceq 'values' -and
        [int]$refinedRaw['inputCount'] -eq 1 -and
        [string]$refinedRaw['invalidEmptyBytesHex'] -ceq '0000000000000000' -and [int]$refinedRaw['invalidEmptyExtentBytes'] -eq 8 -and
        [string]$refinedRaw['validBytesHex'] -ceq '02000000000000006f006b0000000000' -and [int]$refinedRaw['validExtentBytes'] -eq 16 -and
        [int]$refinedRaw['expectedDiagnosticStatus'] -eq 1 -and
        [string]$refinedRaw['sentinelBytesHex'] -ceq (('a5' * 32) -join '') -and
        [int]$refinedNestedIds['NonEmptyString'] -eq 4 -and [int]$refinedNestedIds['RefinedEnvelope'] -eq 5 -and [int]$refinedNestedIds['String'] -eq 6 -and
        [string]$refinedRecord['bytesHex'] -ceq '02000000000000006f006b000000000001000000000000007a00000000000000' -and
        [string]$refinedRecord['ownerValue'] -ceq 'ok' -and [string]$refinedRecord['tailValue'] -ceq 'z' -and
        [int]$refinedRecord['payloadBytes'] -eq 22 -and [int]$refinedRecord['extentBytes'] -eq 32 -and
        [int]$refinedNested['envelopeLayout']['minimumPayloadBytes'] -eq 16 -and
        [int]$refinedNested['envelopeLayout']['minimumExtentBytes'] -eq 16 -and
        [string]$refinedOption['someBytesHex'] -ceq '000000000000000002000000000000006f006b0000000000' -and
        [string]$refinedOption['noneBytesHex'] -ceq '0100000000000000' -and
        [string]$refinedResult['okBytesHex'] -ceq '000000000000000002000000000000006f006b0000000000' -and
        [string]$refinedResult['errorBytesHex'] -ceq '010000000000000002000000000000006f006b0000000000' -and
        [int]$refinedInactive['value'] -eq 7 -and
        [string]$refinedInactive['bytesHex'] -ceq '00000000000000000700000000000000' -and
        [int]$refinedOracle['validatorReplacement']['originalRevision'] -eq 1 -and
        [int]$refinedOracle['validatorReplacement']['replacementRevision'] -eq 2 -and
        [string]$refinedRuntime['code'] -ceq 'RUNTIME_DIVIDE_BY_ZERO' -and
        [string]$refinedRuntime['message'] -ceq 'Integer division by zero.' -and [string]$refinedRuntime['word'] -ceq 'divide' -and
        [string]$refinedRuntimeSpan['file'] -ceq '<native-value-stack-refined-string-runtime-validator-divide>' -and
        [int]$refinedRuntimeSpan['line'] -eq 1 -and [int]$refinedRuntimeSpan['column'] -eq 4 -and [int]$refinedRuntimeSpan['length'] -eq 1 -and
        [int]$refinedOwner['literalDeepCopyBytes'] -eq 80 -and
        [string]$refinedOwner['projectedOutputBytesHex'] -ceq '02000000000000006f006b0000000000' -and
        [int]$refinedTransfer['typeId'] -eq 6 -and [int]$refinedTransfer['offsetBytes'] -eq 0 -and
        [int]$refinedTransfer['sourceOffsetBytesOwnerEnd'] -eq 32 -and
        [int]$refinedTransfer['sourceExtentBytesPayloadExtent'] -eq 16 -and
        [string]$refinedUnsupportedString['code'] -ceq 'IR_OWNING_STACK_TYPE_UNSUPPORTED' -and
        [string]$refinedUnsupportedString['message'] -ceq 'Owning-stack requires String-backed nominal scalars to use a frozen pure String -> Bool validator; unvalidated String wrappers are unsupported.' -and
        [string]$refinedUtf16['supplementary']['value'] -ceq ("A" + [char]0xD83D + [char]0xDE00) -and
        [int]$refinedUtf16['supplementary']['codeUnitCount'] -eq 3 -and
        [string]$refinedUtf16['supplementary']['stringBytesHex'] -ceq '030000000000000041003dd800de0000' -and
        [string]$refinedUtf16['embeddedNul']['value'] -ceq ("A" + [char]0 + "B") -and
        [int]$refinedUtf16['embeddedNul']['codeUnitCount'] -eq 3 -and
        [string]$refinedUtf16['embeddedNul']['stringBytesHex'] -ceq '03000000000000004100000042000000'
    Add-Check 'NonEmptyString fixture pins UTF-16 bytes, IDs, dynamic layout, raw atomicity, validator diagnostics, and OwnerEnd provenance' $refinedFixturePassed ([ordered]@{
        expectedIdentityTypeIds = [ordered]@{ NonEmptyString = 4; String = 5 }
        expectedNestedTypeIds = [ordered]@{ NonEmptyString = 4; RefinedEnvelope = 5; String = 6 }
        expectedStringKind = 5
        expectedOkBytesHex = '02000000000000006f006b0000000000'
        expectedRecordBytesHex = '02000000000000006f006b000000000001000000000000007a00000000000000'
        expectedOwnerEndBytes = 32
        actualFixtureIdentityTypeIds = $refinedIdentityIds
        actualFixtureNestedTypeIds = $refinedNestedIds
    })

    $refinedCaseSuffixes = @(
        'type-identities-and-string-layout',
        'constructor-success-and-utf16-bytes',
        'constructor-reject-empty-parity-atomically',
        'host-identity-input-only-validator-closure',
        'host-identity-utf16-supplementary-roundtrip',
        'host-identity-utf16-embedded-nul-roundtrip',
        'base-string-type-id-and-kind-remain-distinct',
        'host-shape-rejects-bare-string-atomically',
        'host-shape-rejects-wrong-nominal-atomically',
        'raw-empty-entry-preflight-before-body',
        'recursive-record-layout-type-ids-and-dynamic-fields',
        'record-field-validates-recursively',
        'record-field-empty-rejected-atomically',
        'option-some-validates-active-payload',
        'option-some-empty-rejected-atomically',
        'option-none-skips-inactive-validator',
        'result-ok-validates-active-payload',
        'result-ok-empty-rejected-atomically',
        'result-error-validates-active-payload',
        'result-error-empty-rejected-atomically',
        'result-inactive-alternative-skips-validator',
        'validator-only-dependency-freezes-target-after-same-name-replacement',
        'validator-runtime-failure-preserves-classification',
        'refined-field-owner-end-project-unwrap-no-extra-copy',
        'owner-range-trace-complete',
        'unsupported-UnvalidatedStringTag-is-explicit',
        'unsupported-BoolTag-is-explicit',
        'unsupported-FloatTag-is-explicit',
        'refined-string-mailbox-compiled-admission-is-explicit'
    )
    $refinedRuns = @()
    if ($null -ne $experimentEvidence['refinedStringRuns']) { $refinedRuns = @($experimentEvidence['refinedStringRuns']) }
    $refinedRunOptimizationNames = @($refinedRuns | ForEach-Object { [string]$_['optimization'] } | Sort-Object) -join ','
    $refinedProvenanceCoverage = 'After projecting and unwrapping the NonEmptyString owner field, the returned base String (TypeId 6) descriptor transfer retains the outer OwnerEnd; projection and unwrap add no payload move or deep copy beyond the two pinned temporary literals.'
    $refinedRunsMissingCoverage = @($refinedRuns | Where-Object {
        $_['caseCount'] -ne $refinedCaseSuffixes.Count -or
        $_['rawInvalidInputCount'] -ne 1 -or
        $_['rawBodyFailureControlCount'] -ne 1 -or
        $_['recursiveRecordInputCount'] -ne 2 -or
        $_['activeSumValidationCaseCount'] -ne 4 -or
        $_['nonEmptyStringTypeId'] -ne 4 -or $_['stringTypeId'] -ne 5 -or
        $_['refinedEnvelopeTypeId'] -ne 5 -or $_['stringKind'] -ne 5 -or
        $_['ownerEndBytes'] -ne 32 -or
        $_['validatorRevision'] -ne 1 -or $_['replacementValidatorRevision'] -ne 2 -or
        $_['provenanceCoverage'] -cne $refinedProvenanceCoverage
    }).Count
    $refinedRunCoveragePassed = $refinedRuns.Count -eq 2 -and
        $refinedRunOptimizationNames -ceq 'O0,O2' -and $refinedRunsMissingCoverage -eq 0
    Add-Check 'NonEmptyString report has complete O0/O2 construction, recursive ingress, raw preflight, and owner-range summaries' $refinedRunCoveragePassed ([ordered]@{
        expectedOptimizations = @('O0', 'O2')
        expectedCaseCountPerOptimization = $refinedCaseSuffixes.Count
        expectedTypeIds = [ordered]@{ NonEmptyString = 4; String = 5; RefinedEnvelope = 5 }
        expectedOwnerEndBytes = 32
        actualOptimizationNames = $refinedRunOptimizationNames
        incompleteRunCount = $refinedRunsMissingCoverage
        actualRuns = $refinedRuns
    })
    $refinedExpectedCheckNames = [Collections.Generic.List[string]]::new()
    foreach ($optimizationName in @('O0', 'O2')) {
        foreach ($suffix in $refinedCaseSuffixes) {
            $refinedExpectedCheckNames.Add("refined-string/$optimizationName/$suffix")
        }
    }
    $refinedEvidenceChecks = @()
    if ($null -ne $experimentEvidence['checks']) {
        $refinedEvidenceChecks = @($experimentEvidence['checks'] | Where-Object { [string]$_['name'] -like 'refined-string/*' })
    }
    $refinedMissingChecks = [Collections.Generic.List[string]]::new()
    $refinedFailingChecks = [Collections.Generic.List[string]]::new()
    foreach ($expectedName in $refinedExpectedCheckNames) {
        $matchingChecks = @($refinedEvidenceChecks | Where-Object { [string]$_['name'] -ceq $expectedName })
        if ($matchingChecks.Count -ne 1) { $refinedMissingChecks.Add("$expectedName (count=$($matchingChecks.Count))") }
        elseif (-not [bool]$matchingChecks[0]['passed']) { $refinedFailingChecks.Add($expectedName) }
    }
    $refinedCheckCoveragePassed = $refinedCaseSuffixes.Count -eq 29 -and
        $refinedExpectedCheckNames.Count -eq 58 -and
        $refinedEvidenceChecks.Count -eq $refinedExpectedCheckNames.Count -and
        $refinedMissingChecks.Count -eq 0 -and $refinedFailingChecks.Count -eq 0
    Add-Check 'NonEmptyString matrix has exactly one passing check per pinned O0/O2 case and no missing, duplicate, or unexpected cases' $refinedCheckCoveragePassed ([ordered]@{
        expectedCasesPerOptimization = $refinedCaseSuffixes.Count
        expectedTotalCheckCount = $refinedExpectedCheckNames.Count
        actualRefinedStringCheckCount = $refinedEvidenceChecks.Count
        unexpectedRefinedStringCheckCount = $refinedEvidenceChecks.Count - $refinedExpectedCheckNames.Count
        missingOrDuplicateChecks = @($refinedMissingChecks)
        failingChecks = @($refinedFailingChecks)
    })

    Add-Check 'fixed three-way controls share one compiler-authorized program instance' ([bool]$experimentEvidence.fixedControlVerifiedProgramInstance) $experimentEvidence.backendScope
    Add-Check 'String interpreter and owning O0/O2 share one compiler-authorized program instance' ([bool]$experimentEvidence.stringVerifiedProgramInstance -and $experimentEvidence.stringBackendScope -match 'ABI3 String parity is unavailable') $experimentEvidence.stringBackendScope
    Add-Check 'String comparison records nested runtime Value input and no ABI3 String claim' ($experimentEvidence.stringBackendScope -match 'runtime Value input' -and $experimentEvidence.stringBackendScope -match 'ABI3 String parity is unavailable') $experimentEvidence.stringBackendScope
    Add-Check 'Option/Result parity is scoped to owning layout ABI3 and excludes older sharedgraph ABI3' ([bool]$experimentEvidence.sumVerifiedProgramInstance -and $experimentEvidence.sumBackendScope -match 'owning-stack physical layout contract' -and $experimentEvidence.sumBackendScope -match 'older LlvmAot sharedgraph ABI3' -and $experimentEvidence.sumBackendScope -match 'does not claim Option/Result support') $experimentEvidence.sumBackendScope
    Add-Check 'two-turn retained publication boundary is accurately scoped' ($experimentEvidence.turnBoundaryScope -match 'not a persistent native controller') $experimentEvidence.turnBoundaryScope
    Add-Check 'fixture declares no final memory-policy or throughput conclusion' (@($experimentEvidence.limitations | Where-Object { $_ -match 'No final memory-policy|throughput' }).Count -ge 1) $experimentEvidence.limitations

    $report.experimentEvidencePath = $experimentEvidencePath
    $report.experimentEvidenceSha256 = Get-Hash $experimentEvidencePath
    $artifactPaths = [Collections.Generic.List[string]]::new()
    foreach ($artifact in Get-ChildItem -LiteralPath (Join-Path $buildDirectory 'bin') -Recurse -File) {
        $artifactPaths.Add([IO.Path]::GetFullPath($artifact.FullName))
    }
    foreach ($artifact in Get-ChildItem -LiteralPath $nativeDirectory -Recurse -File) {
        $artifactPaths.Add([IO.Path]::GetFullPath($artifact.FullName))
    }
    foreach ($artifact in Get-ChildItem -LiteralPath $nativeOutputDirectory -Recurse -File) {
        $artifactPaths.Add([IO.Path]::GetFullPath($artifact.FullName))
    }
    $report.generatedArtifacts = @(
        $artifactPaths |
        Sort-Object -Unique |
        ForEach-Object { [ordered]@{ path = $_; bytes = (Get-Item -LiteralPath $_).Length; sha256 = Get-Hash $_ } }
    )
    $freshBuildArtifactCount = @($report.generatedArtifacts | Where-Object { $_.path.StartsWith([IO.Path]::GetFullPath((Join-Path $buildDirectory 'bin')), [StringComparison]::OrdinalIgnoreCase) }).Count
    $freshNativeTestArtifactCount = @($report.generatedArtifacts | Where-Object { [IO.Path]::GetFileName($_.path) -match '^owning_stack_runtime_test_(O0|O2|ubsan_trap)\.exe$' }).Count
    $candidateArtifactCount = @($report.generatedArtifacts | Where-Object { $_.path.StartsWith([IO.Path]::GetFullPath($nativeOutputDirectory), [StringComparison]::OrdinalIgnoreCase) }).Count
    Add-Check 'fresh runner and dependency binaries are hashed' ($freshBuildArtifactCount -gt 0) ([ordered]@{ fileCount = $freshBuildArtifactCount })
    Add-Check 'fresh native storage-test executables are hashed' ($freshNativeTestArtifactCount -ge 3) ([ordered]@{ fileCount = $freshNativeTestArtifactCount })
    Add-Check 'fresh native output includes compiled candidate and ABI3 artifacts' ($candidateArtifactCount -gt 0) ([ordered]@{ fileCount = $candidateArtifactCount })

    $report.sourceInputHashesAfter = @(Get-SourceHashes)
    $beforeJson = ConvertTo-Json -InputObject $report.sourceInputHashesBefore -Depth 20 -Compress
    $afterJson = ConvertTo-Json -InputObject $report.sourceInputHashesAfter -Depth 20 -Compress
    $report.sourceInputsStable = $beforeJson -ceq $afterJson
    Add-Check 'hashed source inputs stayed unchanged during the run' ([bool]$report.sourceInputsStable)
    $report.passed = $true
} catch {
    $report.failure = $_.Exception.ToString()
    if ($null -eq $report.sourceInputHashesAfter -or @($report.sourceInputHashesAfter).Count -eq 0) {
        try { $report.sourceInputHashesAfter = @(Get-SourceHashes) } catch { }
    }
    if (@($report.sourceInputHashesBefore).Count -gt 0 -and @($report.sourceInputHashesAfter).Count -gt 0) {
        $beforeJson = ConvertTo-Json -InputObject $report.sourceInputHashesBefore -Depth 20 -Compress
        $afterJson = ConvertTo-Json -InputObject $report.sourceInputHashesAfter -Depth 20 -Compress
        $report.sourceInputsStable = $beforeJson -ceq $afterJson
    }
} finally {
    $report.completedUtc = [DateTime]::UtcNow.ToString('O')
    $report.checks = @($checks)
    $report.processes = @($processes)
    [IO.File]::WriteAllText($evidencePath, (ConvertTo-Json -InputObject $report -Depth 100), $utf8)
}

if ($report.passed) {
    Write-Output "Native owning-value-stack verification passed. Evidence: $evidencePath"
    exit 0
}

Write-Error "Native owning-value-stack verification failed. Artifacts and failure evidence were retained at $runDirectory. $($report.failure)"
exit 1
