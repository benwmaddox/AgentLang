#requires -Version 7.0
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runId = [Guid]::NewGuid().ToString('N')
$evidenceRoot = Join-Path $repo '.agentlang/native-dispatch-001'
$runDirectory = Join-Path $evidenceRoot "integration-run-$runId"
$reportPath = Join-Path $runDirectory 'integration-evidence.json'
$projectPath = Join-Path $repo 'experiments/AgentLang.NativeDispatch/AgentLang.NativeDispatch.fsproj'
$sourcePath = Join-Path $repo 'experiments/AgentLang.NativeDispatch/mailbox.flow'
$referenceSourcePath = Join-Path $repo 'experiments/AgentLang.NativeMailbox/mailbox.flow'
$runnerSourcePath = Join-Path $repo 'experiments/AgentLang.NativeDispatch/native_dispatch.c'
$nativeDirectory = Join-Path $repo 'src/AgentLang.Llvm/native'
$unitSourcePath = Join-Path $nativeDirectory 'mailbox_runtime_test.c'
$nativeSourcePaths = @(
    $runnerSourcePath,
    (Join-Path $nativeDirectory 'arena_runtime.c'),
    (Join-Path $nativeDirectory 'mailbox_runtime.c'),
    (Join-Path $nativeDirectory 'mailbox_runtime_windows.c'),
    $unitSourcePath
)
$moduleFixturePath = Join-Path $repo 'tests/fixtures/native-conformance/module-abi-v1.json'
$mailboxFixturePath = Join-Path $repo 'tests/fixtures/native-conformance/mailbox-abi-v1.json'
$dispatchOraclePath = Join-Path $repo 'tests/fixtures/native-conformance/native-dispatch-v1.json'
$mailboxOraclePath = Join-Path $repo 'reports/evidence/108-native-mailbox-suspension/oracle.json'
$sourceInputPaths = @(
    $projectPath,
    (Join-Path $repo 'experiments/AgentLang.NativeDispatch/Program.fs'),
    $sourcePath,
    $referenceSourcePath,
    $runnerSourcePath,
    (Join-Path $nativeDirectory 'arena_runtime.h'),
    (Join-Path $nativeDirectory 'arena_runtime.c'),
    (Join-Path $nativeDirectory 'module_abi.h'),
    (Join-Path $nativeDirectory 'mailbox_runtime.h'),
    (Join-Path $nativeDirectory 'mailbox_runtime.c'),
    (Join-Path $nativeDirectory 'mailbox_runtime_windows.c'),
    $unitSourcePath,
    (Join-Path $repo 'src/AgentLang.Llvm/AgentLang.Llvm.fsproj'),
    (Join-Path $repo 'src/AgentLang.Llvm/LlvmAot.fs'),
    (Join-Path $repo 'src/AgentLang.Llvm/LlvmToolchain.fs'),
    $moduleFixturePath,
    $mailboxFixturePath,
    $dispatchOraclePath,
    $mailboxOraclePath,
    $PSCommandPath
)
$sourceInputBefore = @()
$timeoutMilliseconds = 300000
$utf8 = [Text.UTF8Encoding]::new($false)
$processes = [Collections.Generic.List[object]]::new()
$moduleBuilds = [Collections.Generic.List[object]]::new()
$nativeBuilds = [Collections.Generic.List[object]]::new()
$nativeRuns = [Collections.Generic.List[object]]::new()
$mailboxUnitRuns = [Collections.Generic.List[object]]::new()
$importAudits = [Collections.Generic.List[object]]::new()
$peAudits = [Collections.Generic.List[object]]::new()
$checks = [Collections.Generic.List[object]]::new()
$errors = [Collections.Generic.List[string]]::new()
$report = [ordered]@{
    schemaVersion = 1
    kind = 'native-dispatch-integration'
    runId = $runId
    startedUtc = [DateTime]::UtcNow.ToString('O')
    completedUtc = $null
    passed = $false
    evidencePath = $reportPath
    runDirectory = $runDirectory
    scope = 'standalone native C mailbox controller dispatching Flow handlers from one verified-IR module; not a throughput or final allocation-policy claim'
    source = $null
    nativeSources = @()
    sourceInputHashes = $null
    dependencyBuild = $null
    compiler = $null
    moduleBuilds = @()
    nativeBuilds = @()
    nativeRuns = @()
    mailboxUnitRuns = @()
    importAudits = @()
    peAudits = @()
    fixtureComparisons = @()
    checks = @()
    failure = $null
}

function Add-Check([string]$Name, [bool]$Passed, $Details = $null) {
    $item = [ordered]@{ name = $Name; passed = $Passed }
    if ($null -ne $Details) { $item.details = $Details }
    $checks.Add($item)
    if (-not $Passed -and $null -ne $Details) {
        $errors.Add(('{0}: {1}' -f $Name, $Details))
    }
}

function Get-Field($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    if ($Object -is [Collections.IDictionary]) {
        if ($Object.Contains($Name)) { return $Object[$Name] }
        return $null
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-Hash([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Resolve-Executable([string]$EnvironmentName, [string]$DefaultPath, [string]$CommandName) {
    $override = [Environment]::GetEnvironmentVariable($EnvironmentName)
    if (-not [string]::IsNullOrWhiteSpace($override)) {
        if ([IO.Path]::IsPathFullyQualified($override)) { return [IO.Path]::GetFullPath($override) }
        $foundOverride = Get-Command -Name $override -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -eq $foundOverride) { throw "$EnvironmentName is not an executable: $override" }
        return [IO.Path]::GetFullPath($foundOverride.Source)
    }
    if (Test-Path -LiteralPath $DefaultPath -PathType Leaf) { return [IO.Path]::GetFullPath($DefaultPath) }
    $found = Get-Command -Name $CommandName -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $found) { throw "Required executable is missing: $CommandName" }
    return [IO.Path]::GetFullPath($found.Source)
}

function Invoke-CapturedProcess {
    param(
        [string]$Name,
        [string]$File,
        [string[]]$Arguments,
        [string]$WorkingDirectory
    )
    $started = [DateTime]::UtcNow
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $File
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) { [void]$startInfo.ArgumentList.Add([string]$argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $startedProcess = $false
    $stdout = ''
    $stderr = ''
    $exitCode = $null
    $timedOut = $false
    $startError = $null
    try {
        if (-not $process.Start()) { throw 'Process.Start returned false.' }
        $startedProcess = $true
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
        if ($startedProcess -and -not $process.HasExited) {
            try { $process.Kill($true) } catch { }
        }
    } finally {
        $process.Dispose()
    }
    $record = [ordered]@{
        name = $Name
        file = $File
        arguments = @($Arguments)
        workingDirectory = $WorkingDirectory
        startedUtc = $started.ToString('O')
        elapsedMilliseconds = [Math]::Round(([DateTime]::UtcNow - $started).TotalMilliseconds, 3)
        exitCode = $exitCode
        timedOut = $timedOut
        startError = $startError
        stdout = $stdout
        stderr = $stderr
    }
    $processes.Add($record)
    return $record
}

function Require-ProcessSuccess($ProcessResult, [string]$Description) {
    $ok = -not $ProcessResult.timedOut -and $null -eq $ProcessResult.startError -and $ProcessResult.exitCode -eq 0
    Add-Check $Description $ok $ProcessResult.stderr
    if (-not $ok) { throw "$Description failed (exit $($ProcessResult.exitCode)): $($ProcessResult.stderr)" }
}

function Read-JsonFile([string]$Path) {
    Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable -Depth 50 -ErrorAction Stop
}

function Compare-FixtureTree($Expected, $Actual, [string]$Prefix) {
    if ($Expected -is [Collections.IDictionary]) {
        foreach ($key in $Expected.Keys) {
            if ([string]$key -ceq 'source') { continue }
            if (-not ($Actual -is [Collections.IDictionary]) -or -not $Actual.Contains($key)) {
                Add-Check "$Prefix.$key exists in native probe" $false 'Expected fixture field is missing.'
                continue
            }
            Compare-FixtureTree $Expected[$key] $Actual[$key] "$Prefix.$key"
        }
        return
    }
    $same = $Expected -eq $Actual
    Add-Check "$Prefix matches independent fixture" ([bool]$same) ([ordered]@{ expected = $Expected; actual = $Actual })
}

function Get-ArtifactHashes($BootstrapResult) {
    $paths = [Collections.Generic.List[string]]::new()
    foreach ($field in @('libraryPath', 'manifestPath', 'metadataSourcePath')) {
        $path = [string](Get-Field $BootstrapResult $field)
        if (-not [string]::IsNullOrWhiteSpace($path)) { $paths.Add($path) }
    }
    foreach ($field in @('entryIrPaths', 'objectPaths')) {
        foreach ($path in @(Get-Field $BootstrapResult $field)) {
            if (-not [string]::IsNullOrWhiteSpace([string]$path)) { $paths.Add([string]$path) }
        }
    }
    $unique = @($paths | Sort-Object -Unique)
    foreach ($path in $unique) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Expected fresh compiler artifact is missing: $path" }
    }
    return @($unique | ForEach-Object { [ordered]@{ path = $_; bytes = (Get-Item -LiteralPath $_).Length; sha256 = Get-Hash $_ } })
}

function Get-ImportedModules([string]$Output) {
    $values = [Collections.Generic.List[object]]::new()
    foreach ($match in [regex]::Matches($Output, '(?ms)^\s*Import\s*\{(.*?)^\s*\}')) {
        $block = $match.Groups[1].Value
        $nameMatch = [regex]::Match($block, '(?im)^\s*Name:\s*([^\r\n]+?\.dll)\s*$')
        if (-not $nameMatch.Success) { continue }
        $symbols = @([regex]::Matches($block, '(?im)^\s*Symbol:\s*([^\r\n]+?)\s*$') |
            ForEach-Object { $_.Groups[1].Value.Trim() } | Sort-Object -Unique)
        $values.Add([ordered]@{ module = $nameMatch.Groups[1].Value.Trim(); symbols = $symbols })
    }
    return @($values | Sort-Object -Property module)
}

function Test-AllowlistedNativeImportModule([string]$Module) {
    $name = $Module.Trim().ToLowerInvariant()
    return $name -ceq 'kernel32.dll'
}

function Test-AllowlistedNativeImportSymbol([string]$Module, [string]$Symbol) {
    if ($Module.Trim() -ine 'KERNEL32.dll') { return $false }
    $name = ([string]$Symbol -replace '\s+\(\d+\)$', '').Trim()
    return $script:allowedWindowsImportSymbols -ccontains $name
}

$allowedWindowsImportSymbols = @(
    'CloseHandle', 'CompareStringW', 'CreateFileW', 'CreateThread',
    'DeleteCriticalSection', 'EncodePointer', 'EnterCriticalSection', 'ExitProcess',
    'FindClose', 'FindFirstFileExW', 'FindNextFileW', 'FlsAlloc', 'FlsFree',
    'FlsGetValue', 'FlsSetValue', 'FlushFileBuffers', 'FreeEnvironmentStringsW',
    'FreeLibrary', 'GetACP', 'GetCommandLineA', 'GetCommandLineW', 'GetConsoleMode',
    'GetConsoleOutputCP', 'GetCPInfo', 'GetCurrentProcess', 'GetCurrentProcessId',
    'GetCurrentThreadId', 'GetEnvironmentStringsW', 'GetFileSizeEx', 'GetFileType',
    'GetLastError', 'GetModuleFileNameW', 'GetModuleHandleExW', 'GetModuleHandleW',
    'GetOEMCP', 'GetProcAddress', 'GetProcessHeap', 'GetStartupInfoW', 'GetStdHandle',
    'GetStringTypeW', 'GetSystemTimeAsFileTime', 'HeapAlloc', 'HeapFree', 'HeapReAlloc',
    'HeapSize', 'InitializeCriticalSectionAndSpinCount', 'InitializeCriticalSectionEx',
    'InitializeSListHead', 'IsDebuggerPresent', 'IsProcessorFeaturePresent',
    'IsValidCodePage', 'LCMapStringW', 'LeaveCriticalSection', 'LoadLibraryA',
    'LoadLibraryExW', 'MultiByteToWideChar', 'QueryPerformanceCounter', 'RaiseException',
    'RtlCaptureContext', 'RtlLookupFunctionEntry', 'RtlPcToFileHeader', 'RtlUnwind',
    'RtlUnwindEx', 'RtlVirtualUnwind', 'SetEnvironmentVariableW', 'SetFilePointerEx',
    'SetLastError', 'SetStdHandle', 'SetUnhandledExceptionFilter', 'TerminateProcess',
    'TlsAlloc', 'TlsFree', 'TlsGetValue', 'TlsSetValue', 'UnhandledExceptionFilter',
    'VirtualAlloc', 'VirtualFree', 'VirtualProtect', 'WaitForSingleObject',
    'WideCharToMultiByte', 'WriteConsoleW', 'WriteFile'
)

try {
    [IO.Directory]::CreateDirectory($runDirectory) | Out-Null
    $sourceInputBefore = @($sourceInputPaths | ForEach-Object {
        if (-not (Test-Path -LiteralPath $_ -PathType Leaf)) { throw "Required source input is missing: $_" }
        [ordered]@{ path = [IO.Path]::GetFullPath($_); sha256 = Get-Hash $_ }
    })
    $sourceHash = Get-Hash $sourcePath
    $referenceHash = Get-Hash $referenceSourcePath
    $report.source = [ordered]@{
        copiedFixture = 'experiments/AgentLang.NativeDispatch/mailbox.flow'
        referenceFixture = 'experiments/AgentLang.NativeMailbox/mailbox.flow'
        bytes = (Get-Item -LiteralPath $sourcePath).Length
        sha256 = $sourceHash
        referenceSha256 = $referenceHash
    }
    Add-Check 'new Flow fixture is byte-identical to report108 mailbox source' ($sourceHash -ceq $referenceHash)
    if ($sourceHash -cne $referenceHash) { throw 'Copied Flow fixture differs from the existing mailbox source.' }
    foreach ($nativeSource in $nativeSourcePaths) {
        if (-not (Test-Path -LiteralPath $nativeSource -PathType Leaf)) { throw "Native integration source is missing: $nativeSource" }
    }
    $report.nativeSources = @($nativeSourcePaths | ForEach-Object {
        [ordered]@{ path = $_; bytes = (Get-Item -LiteralPath $_).Length; sha256 = Get-Hash $_ }
    })

    $clangDefault = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\Llvm\x64\bin\clang.exe'
    $clang = Resolve-Executable 'AGENTLANG_LLVM_CLANG' $clangDefault 'clang.exe'
    $lldDefault = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\Llvm\x64\bin\lld-link.exe'
    $lld = Resolve-Executable 'AGENTLANG_LLVM_LLD' $lldDefault 'lld-link.exe'
    $dotnetCommand = Get-Command -Name dotnet.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $dotnet = [IO.Path]::GetFullPath($dotnetCommand.Source)
    $readobj = Join-Path ([IO.Path]::GetDirectoryName($clang)) 'llvm-readobj.exe'
    if (-not (Test-Path -LiteralPath $readobj -PathType Leaf)) { throw "llvm-readobj is missing beside Clang: $readobj" }
    $report.compiler = [ordered]@{
        dotnet = $dotnet; clang = $clang; clangSha256 = Get-Hash $clang
        lld = $lld; lldSha256 = Get-Hash $lld; llvmReadObj = $readobj
        overrides = [ordered]@{
            clang = [Environment]::GetEnvironmentVariable('AGENTLANG_LLVM_CLANG')
            lld = [Environment]::GetEnvironmentVariable('AGENTLANG_LLVM_LLD')
            compilerRuntimeLibrary = [Environment]::GetEnvironmentVariable('AGENTLANG_COMPILER_RUNTIME_LIB')
        }
    }
    $version = Invoke-CapturedProcess 'clang-version' $clang @('--version') $runDirectory
    Require-ProcessSuccess $version 'Clang version command exited cleanly'
    $report.compiler.clangVersion = $version.stdout.Trim()
    $dotnetVersion = Invoke-CapturedProcess 'dotnet-version' $dotnet @('--version') $runDirectory
    Require-ProcessSuccess $dotnetVersion '.NET SDK version command exited cleanly'
    $report.compiler.dotnetVersion = $dotnetVersion.stdout.Trim()

    $artifactsRoot = Join-Path $runDirectory 'dotnet-artifacts'
    $buildArguments = @('build', $projectPath, '--artifacts-path', $artifactsRoot, '--configuration', 'Release', '--verbosity', 'minimal')
    $build = Invoke-CapturedProcess 'fresh-dotnet-dependency-build' $dotnet $buildArguments $repo
    $report.dependencyBuild = $build
    Require-ProcessSuccess $build 'Fresh bootstrap and project dependency build succeeded'
    $assemblies = @(Get-ChildItem -LiteralPath (Join-Path $artifactsRoot 'bin') -Recurse -File -Filter 'AgentLang.NativeDispatch.dll' |
        Where-Object { $_.FullName -notmatch '[\\/]ref[\\/]' })
    Add-Check 'fresh build produced exactly one bootstrap assembly' ($assemblies.Count -eq 1) ([ordered]@{ count = $assemblies.Count })
    if ($assemblies.Count -ne 1) { throw 'Expected exactly one non-reference NativeDispatch bootstrap assembly.' }
    $assemblyPath = $assemblies[0].FullName
    Add-Check 'bootstrap assembly belongs to this run artifact root' ([IO.Path]::GetFullPath($assemblyPath).StartsWith([IO.Path]::GetFullPath($artifactsRoot), [StringComparison]::OrdinalIgnoreCase))

    $moduleSpecs = @(
        [ordered]@{ label = 'O0'; optimization = 'O0'; directory = (Join-Path $runDirectory 'module-O0') },
        [ordered]@{ label = 'O0-repeat'; optimization = 'O0'; directory = (Join-Path $runDirectory 'module-O0-repeat') },
        [ordered]@{ label = 'O2'; optimization = 'O2'; directory = (Join-Path $runDirectory 'module-O2') }
    )
    foreach ($spec in $moduleSpecs) {
        [IO.Directory]::CreateDirectory($spec.directory) | Out-Null
        $arguments = @($assemblyPath, $spec.optimization, $spec.directory, $sourcePath)
        $process = Invoke-CapturedProcess "compile-module-$($spec.label)" $dotnet $arguments $repo
        Require-ProcessSuccess $process "Fresh $($spec.label) native module compile succeeded"
        $bootstrap = $null
        try { $bootstrap = $process.stdout.Trim() | ConvertFrom-Json -AsHashtable -Depth 30 -ErrorAction Stop }
        catch { throw "Module bootstrap did not emit one JSON artifact record ($($spec.label)): $($_.Exception.Message)" }
        $manifestPath = [string](Get-Field $bootstrap 'manifestPath')
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "Fresh module manifest is missing: $manifestPath" }
        $manifest = Read-JsonFile $manifestPath
        $libraryPath = [string](Get-Field $bootstrap 'libraryPath')
        $dlls = @(Get-ChildItem -LiteralPath $spec.directory -Recurse -File -Filter '*.dll')
        Add-Check "$($spec.label) output contains one module DLL" ($dlls.Count -eq 1 -and [IO.Path]::GetFullPath($dlls[0].FullName) -ceq [IO.Path]::GetFullPath($libraryPath)) ([ordered]@{ dllCount = $dlls.Count; libraryPath = $libraryPath })
        $entries = @(Get-Field (Get-Field $manifest 'module') 'entries')
        $entryNames = @($entries | ForEach-Object { [string](Get-Field $_ 'name') } | Sort-Object -CaseSensitive)
        $expectedNames = @('mailbox.begin', 'mailbox.initialize', 'mailbox.resume')
        Add-Check "$($spec.label) manifest has the three Flow handlers" (($entryNames -join ',') -ceq ($expectedNames -join ',')) ([ordered]@{ entryNames = $entryNames })
        Add-Check "$($spec.label) records optimization separately from its semantic fingerprint" ((Get-Field $manifest 'optimization') -ceq $spec.optimization -and (Get-Field $bootstrap 'fingerprint') -ceq (Get-Field $manifest 'fingerprint'))
        Add-Check "$($spec.label) bootstrap proves one shared VerifiedIrProgram" ((Get-Field $bootstrap 'sameVerifiedProgramInstance') -is [bool] -and (Get-Field $bootstrap 'sameVerifiedProgramInstance'))
        $moduleBuilds.Add([ordered]@{
            label = $spec.label
            optimization = $spec.optimization
            outputDirectory = $spec.directory
            process = $process
            bootstrap = $bootstrap
            manifest = $manifest
            artifactHashes = Get-ArtifactHashes $bootstrap
        })
    }
    $primaryO0 = $moduleBuilds[0]
    $repeatO0 = $moduleBuilds[1]
    $primaryO2 = $moduleBuilds[2]
    $manifestO0 = $primaryO0.manifest
    $manifestRepeat = $repeatO0.manifest
    $manifestO2 = $primaryO2.manifest
    $semanticO0 = ConvertTo-Json -InputObject (Get-Field $manifestO0 'module') -Depth 40 -Compress
    $semanticRepeat = ConvertTo-Json -InputObject (Get-Field $manifestRepeat 'module') -Depth 40 -Compress
    $semanticO2 = ConvertTo-Json -InputObject (Get-Field $manifestO2 'module') -Depth 40 -Compress
    Add-Check 'two fresh O0 artifact builds have identical metadata and fingerprint' ($semanticO0 -ceq $semanticRepeat -and $manifestO0.fingerprint -ceq $manifestRepeat.fingerprint)
    Add-Check 'O0 and O2 builds share deterministic semantic metadata and fingerprint' ($semanticO0 -ceq $semanticO2 -and $manifestO0.fingerprint -ceq $manifestO2.fingerprint)
    $dispatchOracle = Read-JsonFile $dispatchOraclePath

    $runnerBinaries = @{}
    foreach ($optimization in @('O0', 'O2')) {
        $runnerDirectory = Join-Path $runDirectory "runner-$optimization"
        [IO.Directory]::CreateDirectory($runnerDirectory) | Out-Null
        $runnerPath = Join-Path $runnerDirectory "native-dispatch-$optimization.exe"
        $nativeSources = @(
            $runnerSourcePath,
            (Join-Path $nativeDirectory 'arena_runtime.c'),
            (Join-Path $nativeDirectory 'mailbox_runtime.c'),
            (Join-Path $nativeDirectory 'mailbox_runtime_windows.c')
        )
        foreach ($nativeSource in $nativeSources) {
            if (-not (Test-Path -LiteralPath $nativeSource -PathType Leaf)) { throw "Native dispatch source is missing: $nativeSource" }
        }
        $compileArguments = @('--target=x86_64-pc-windows-msvc', '-std=c11', '-Wall', '-Wextra', '-Werror', "-$optimization", '-I', $nativeDirectory) + $nativeSources + @('-o', $runnerPath)
        $nativeBuild = Invoke-CapturedProcess "native-runner-build-$optimization" $clang $compileArguments $runnerDirectory
        Require-ProcessSuccess $nativeBuild "$optimization native runner build succeeded"
        Add-Check "$optimization native runner was freshly produced" (Test-Path -LiteralPath $runnerPath -PathType Leaf)
        $nativeBuilds.Add([ordered]@{
            optimization = $optimization
            executable = $runnerPath
            executableSha256 = if (Test-Path -LiteralPath $runnerPath -PathType Leaf) { Get-Hash $runnerPath } else { $null }
            process = $nativeBuild
        })
        $runnerBinaries[$optimization] = $runnerPath
    }

    foreach ($optimization in @('O0', 'O2')) {
        $unitDirectory = Join-Path $runDirectory "mailbox-unit-$optimization"
        [IO.Directory]::CreateDirectory($unitDirectory) | Out-Null
        $unitPath = Join-Path $unitDirectory "mailbox-runtime-test-$optimization.exe"
        $unitSources = @(
            (Join-Path $nativeDirectory 'arena_runtime.c'),
            (Join-Path $nativeDirectory 'mailbox_runtime.c'),
            (Join-Path $nativeDirectory 'mailbox_runtime_windows.c'),
            $unitSourcePath
        )
        foreach ($unitSource in $unitSources) {
            if (-not (Test-Path -LiteralPath $unitSource -PathType Leaf)) { throw "Native mailbox unit source is missing: $unitSource" }
        }
        $unitBuildArguments = @('--target=x86_64-pc-windows-msvc', '-std=c11', '-Wall', '-Wextra', '-Werror', '-DAL_MAILBOX_RUNTIME_TESTING', "-$optimization", '-I', $nativeDirectory) + $unitSources + @('-o', $unitPath)
        $unitBuild = Invoke-CapturedProcess "mailbox-unit-build-$optimization" $clang $unitBuildArguments $unitDirectory
        Require-ProcessSuccess $unitBuild "$optimization focused mailbox C unit build succeeded"
        $unitRun = Invoke-CapturedProcess "mailbox-unit-run-$optimization" $unitPath @() $unitDirectory
        Require-ProcessSuccess $unitRun "$optimization focused mailbox C unit suite passed"
        $unitCheckResults = [Collections.Generic.List[object]]::new()
        $unitUnparsedLines = [Collections.Generic.List[string]]::new()
        $unitSummary = $null
        foreach ($line in ($unitRun.stdout -split "`r?`n")) {
            if ([string]::IsNullOrWhiteSpace($line) -or $line -ceq 'mailbox_runtime_test: passed') { continue }
            try {
                $unitRecord = ConvertFrom-Json -InputObject $line -AsHashtable -Depth 10 -ErrorAction Stop
                if ((Get-Field $unitRecord 'kind') -ceq 'mailbox-unit-check') { $unitCheckResults.Add($unitRecord) }
                elseif ((Get-Field $unitRecord 'kind') -ceq 'mailbox-unit-summary') { $unitSummary = $unitRecord }
                else { $unitUnparsedLines.Add($line) }
            } catch { $unitUnparsedLines.Add($line) }
        }
        Add-Check "$optimization mailbox unit suite emits named JSON checks and summary" ($unitCheckResults.Count -gt 0 -and $null -ne $unitSummary -and $unitUnparsedLines.Count -eq 0) ([ordered]@{ count = $unitCheckResults.Count; summary = $unitSummary; unparsed = @($unitUnparsedLines) })
        Add-Check "$optimization mailbox unit summary reports success" ($null -ne $unitSummary -and (Get-Field $unitSummary 'passed') -eq $true -and [int](Get-Field $unitSummary 'failureCount') -eq 0)
        Add-Check "$optimization named mailbox unit checks all pass" (@($unitCheckResults | Where-Object { (Get-Field $_ 'passed') -ne $true }).Count -eq 0)
        $rejectionCaseMap = [ordered]@{
            'busy' = 'busy'
            'wrong owner token' = 'wrong_owner_token'
            'stale token' = 'stale_token'
            'duplicate token' = 'duplicate_token'
            'cross-runtime token' = 'cross_runtime_token'
            'foreign thread' = 'foreign_thread'
            'invalid module' = 'invalid_module'
            'invalid or short storage' = 'invalid_storage'
            'token exhaustion' = 'token_exhaustion'
            'generation exhaustion' = 'generation_exhaustion'
            'post-disposal work' = 'post_disposal'
        }
        $unitCases = @($unitCheckResults | Where-Object { (Get-Field $_ 'passed') -eq $true } | ForEach-Object { [string](Get-Field $_ 'case') })
        foreach ($rejection in @($dispatchOracle.requiredRejections)) {
            $expectedCase = [string]$rejectionCaseMap[[string]$rejection]
            $caseObserved = -not [string]::IsNullOrWhiteSpace($expectedCase) -and $unitCases -ccontains $expectedCase
            Add-Check "$optimization unit suite covers frozen rejection: $rejection" $caseObserved ([ordered]@{ expectedCase = $expectedCase; observedCases = $unitCases })
        }
        $mailboxUnitRuns.Add([ordered]@{
            optimization = $optimization
            executable = $unitPath
            executableSha256 = Get-Hash $unitPath
            build = $unitBuild
            run = $unitRun
            namedChecks = @($unitCheckResults)
            summary = $unitSummary
        })
    }

    $oracle108 = Read-JsonFile $mailboxOraclePath
    foreach ($optimization in @('O0', 'O2')) {
        $module = if ($optimization -ceq 'O0') { $primaryO0 } else { $primaryO2 }
        $runProcess = Invoke-CapturedProcess "native-dispatch-run-$optimization" $runnerBinaries[$optimization] @($module.bootstrap.libraryPath) $runDirectory
        $runResult = $null
        if (-not [string]::IsNullOrWhiteSpace($runProcess.stdout)) {
            try { $runResult = $runProcess.stdout.Trim() | ConvertFrom-Json -AsHashtable -Depth 40 -ErrorAction Stop }
            catch { Add-Check "$optimization native runner emits JSON evidence" $false $_.Exception.Message }
        }
        $processOk = -not $runProcess.timedOut -and $null -eq $runProcess.startError -and $runProcess.exitCode -eq 0
        Add-Check "$optimization native runner exits cleanly" $processOk $runProcess.stderr
        if ($null -ne $runResult) {
            Add-Check "$optimization native runner reports all checks passed" ((Get-Field $runResult 'passed') -is [bool] -and (Get-Field $runResult 'passed'))
            $runChecks = @(Get-Field $runResult 'checks')
            Add-Check "$optimization native runner includes positive check evidence" ($runChecks.Count -gt 0 -and @($runChecks | Where-Object { (Get-Field $_ 'passed') -ne $true }).Count -eq 0)
            $observed = Get-Field $runResult 'observed'
            Add-Check "$optimization matches report108 A value 17" ([long](Get-Field $observed 'aAfterOverlap') -eq [long]$oracle108.firstCompleted.A)
            Add-Check "$optimization matches report108 B value 103" ([long](Get-Field $observed 'bAfterOverlap') -eq [long]$oracle108.firstCompleted.B)
            Add-Check "$optimization matches report108 final A value 19" ([long](Get-Field $observed 'aFinal') -eq [long]$oracle108.secondA.completed)
            Add-Check "$optimization preserves divide-by-zero diagnostic" ((Get-Field $observed 'languageFailureCode') -ceq $dispatchOracle.divideFailure.diagnosticCode)
            Add-Check "$optimization reports seven-byte retained failure needs eight bytes and one node" ([long](Get-Field $observed 'capacityRequiredBytes') -eq [long]$dispatchOracle.retainedFailure.requiredBytes -and [long](Get-Field $observed 'capacityRequiredNodes') -eq [long]$dispatchOracle.retainedFailure.requiredNodes)
            $stats = Get-Field $runResult 'finalStats'
            $requirements = Get-Field $runResult 'storageRequirements'
            Add-Check "$optimization reserves two retained banks per mailbox" ([uint64](Get-Field $requirements 'retainedReservedBytes') -eq [uint64]$dispatchOracle.expectedStorageComponents.retainedArenaBackingBytes)
            Add-Check "$optimization reserves one shared scratch arena" ([uint64](Get-Field $requirements 'scratchReservedBytes') -eq [uint64]$dispatchOracle.expectedStorageComponents.scratchArenaBackingBytes)
            Add-Check "$optimization reports workspace and controller backing" ([uint64](Get-Field $requirements 'workspaceReservedBytes') -gt 0 -and [uint64](Get-Field $requirements 'controllerReservedBytes') -gt 0 -and [uint64](Get-Field $requirements 'storageBytes') -ge ([uint64](Get-Field $requirements 'retainedReservedBytes') + [uint64](Get-Field $requirements 'scratchReservedBytes')))
            $peak = Get-Field $runResult 'peakLiveState'
            Add-Check "$optimization records the two-mailbox live state separately from reserved backing" ([uint64](Get-Field $peak 'retainedBytes') -eq ([uint64]$dispatchOracle.expectedMaximumLiveGraph.bytesPerMailbox * [uint64]$dispatchOracle.mailboxCount) -and [uint64](Get-Field $peak 'retainedNodes') -eq ([uint64]$dispatchOracle.expectedMaximumLiveGraph.nodesPerMailbox * [uint64]$dispatchOracle.mailboxCount))
            Add-Check "$optimization cleanup clears all live owners, mailboxes, and scratch leases" ([uint64](Get-Field $stats 'liveRetainedBytes') -eq 0 -and [uint64](Get-Field $stats 'liveRetainedNodes') -eq 0 -and [uint64](Get-Field $stats 'outstandingScratchLeases') -eq [uint64]$dispatchOracle.cleanup.outstandingScratchLeases -and [uint64](Get-Field $stats 'scratchLeaseAcquisitions') -eq [uint64](Get-Field $stats 'scratchLeaseReturns'))
            Add-Check "$optimization cleanup attempts dispose every initialized mailbox" (@($runChecks | Where-Object { (Get-Field $_ 'name') -ceq 'disposal is idempotent and releases every native owner' -and (Get-Field $_ 'passed') -eq $true }).Count -eq 1)
            $actualAbi = Get-Field $runResult 'abi'
            $moduleFixture = Read-JsonFile $moduleFixturePath
            $mailboxFixture = Read-JsonFile $mailboxFixturePath
            $beforeFixtureChecks = $checks.Count
            Compare-FixtureTree $moduleFixture $actualAbi 'module ABI fixture'
            $moduleFixtureCheckCount = $checks.Count - $beforeFixtureChecks
            $beforeFixtureChecks = $checks.Count
            Compare-FixtureTree $mailboxFixture $actualAbi 'mailbox ABI fixture'
            $mailboxFixtureCheckCount = $checks.Count - $beforeFixtureChecks
            $report.fixtureComparisons += [ordered]@{
                optimization = $optimization
                moduleFixture = $moduleFixturePath
                moduleLeafChecks = $moduleFixtureCheckCount
                mailboxFixture = $mailboxFixturePath
                mailboxLeafChecks = $mailboxFixtureCheckCount
            }
        }
        $nativeRuns.Add([ordered]@{
            optimization = $optimization
            modulePath = $module.bootstrap.libraryPath
            executable = $runnerBinaries[$optimization]
            process = $runProcess
            result = $runResult
        })
    }

    $runO0 = $nativeRuns[0].result
    $runO2 = $nativeRuns[1].result
    if ($null -ne $runO0 -and $null -ne $runO2) {
        $stableO0 = [ordered]@{
            fingerprint = $runO0.fingerprint
            moduleEntryIds = $runO0.moduleEntryIds
            observed = $runO0.observed
            storageRequirements = $runO0.storageRequirements
            peakLiveState = $runO0.peakLiveState
            finalStats = $runO0.finalStats
            abi = $runO0.abi
            checks = @($runO0.checks | ForEach-Object { [ordered]@{ name = $_.name; passed = $_.passed } })
        }
        $stableO2 = [ordered]@{
            fingerprint = $runO2.fingerprint
            moduleEntryIds = $runO2.moduleEntryIds
            observed = $runO2.observed
            storageRequirements = $runO2.storageRequirements
            peakLiveState = $runO2.peakLiveState
            finalStats = $runO2.finalStats
            abi = $runO2.abi
            checks = @($runO2.checks | ForEach-Object { [ordered]@{ name = $_.name; passed = $_.passed } })
        }
        Add-Check 'O0 and O2 native dispatch runs have deterministic metadata, outcomes, storage, and cleanup' ((ConvertTo-Json -InputObject $stableO0 -Depth 40 -Compress) -ceq (ConvertTo-Json -InputObject $stableO2 -Depth 40 -Compress))
    }

    foreach ($filePath in @($primaryO0.bootstrap.libraryPath, $primaryO2.bootstrap.libraryPath, $runnerBinaries.O0, $runnerBinaries.O2)) {
        $headerAudit = Invoke-CapturedProcess 'pe-header-audit' $readobj @('--file-headers', $filePath) $runDirectory
        Require-ProcessSuccess $headerAudit "PE header audit succeeded for $(Split-Path -Leaf $filePath)"
        $headerX64 = $headerAudit.stdout -match '(?m)^Format:\s*COFF-x86-64\s*$' -and
            $headerAudit.stdout -match '(?m)^Arch:\s*x86_64\s*$' -and
            $headerAudit.stdout -match '(?m)^AddressSize:\s*64bit\s*$'
        Add-Check "$(Split-Path -Leaf $filePath) is a valid Windows x64 PE image" $headerX64
        $clrRvaMatch = [regex]::Match($headerAudit.stdout, '(?im)^\s*CLRRuntimeHeaderRVA:\s*(0x[0-9a-f]+|[0-9]+)\s*$')
        $clrSizeMatch = [regex]::Match($headerAudit.stdout, '(?im)^\s*CLRRuntimeHeaderSize:\s*(0x[0-9a-f]+|[0-9]+)\s*$')
        $noClrHeader = $clrRvaMatch.Success -and $clrSizeMatch.Success -and
            $clrRvaMatch.Groups[1].Value -match '^(?i:0x0|0)$' -and
            $clrSizeMatch.Groups[1].Value -match '^(?i:0x0|0)$'
        Add-Check "$(Split-Path -Leaf $filePath) has a zero CLR runtime header RVA and size" $noClrHeader ([ordered]@{
            clrRuntimeHeaderRva = if ($clrRvaMatch.Success) { $clrRvaMatch.Groups[1].Value } else { $null }
            clrRuntimeHeaderSize = if ($clrSizeMatch.Success) { $clrSizeMatch.Groups[1].Value } else { $null }
        })
        $audit = Invoke-CapturedProcess 'pe-import-audit' $readobj @('--coff-imports', $filePath) $runDirectory
        $auditOk = -not $audit.timedOut -and $null -eq $audit.startError -and $audit.exitCode -eq 0
        Add-Check "PE import audit succeeded for $(Split-Path -Leaf $filePath)" $auditOk $audit.stderr
        $imports = @(Get-ImportedModules $audit.stdout)
        $importBlockCount = [regex]::Matches($audit.stdout, '(?im)^\s*Import\s*\{').Count
        $importTablesParsed = $audit.stdout -match '(?m)^File:\s*' -and @($imports).Count -eq $importBlockCount
        Add-Check "$(Split-Path -Leaf $filePath) PE import table parsed" $importTablesParsed ([ordered]@{ importBlockCount = $imports.Count })
        $unknownModules = @($imports | Where-Object { -not (Test-AllowlistedNativeImportModule ([string]$_.module)) } | ForEach-Object { $_.module })
        Add-Check "$(Split-Path -Leaf $filePath) imports only allowlisted Windows/CRT modules" ($unknownModules.Count -eq 0) ([ordered]@{ allowedWindowsAndCrtModules = @($imports | ForEach-Object { $_.module }); unknownModules = $unknownModules })
        $banned = $audit.stdout -match '(?i)\b(mscoree|coreclr|clrjit|hostfxr|hostpolicy|mscorlib|system\.private\.corelib|clr)\.dll\b'
        Add-Check "$(Split-Path -Leaf $filePath) has no CLR or managed runtime imports" (-not $banned) ([ordered]@{ importedModules = @($imports | ForEach-Object { $_.module }) })
        $allImportedSymbols = @($imports | ForEach-Object { $_.symbols } | ForEach-Object { ([string]$_ -replace '\s+\(\d+\)$', '').Trim() } | Sort-Object -Unique)
        $unknownSymbols = [Collections.Generic.List[string]]::new()
        foreach ($import in $imports) {
            foreach ($symbol in $import.symbols) {
                if (-not (Test-AllowlistedNativeImportSymbol ([string]$import.module) ([string]$symbol))) {
                    $unknownSymbols.Add(('{0}!{1}' -f $import.module, $symbol))
                }
            }
        }
        Add-Check "$(Split-Path -Leaf $filePath) imports only allowlisted Windows/CRT symbols" ($unknownSymbols.Count -eq 0) ([ordered]@{ unknownSymbols = @($unknownSymbols); supportedSymbolCount = $allowedWindowsImportSymbols.Count })
        if ((Split-Path -Leaf $filePath) -like 'native-dispatch-*.exe') {
            $requiredRunnerImports = @('LoadLibraryA', 'GetProcAddress', 'FreeLibrary', 'CreateThread', 'GetCurrentThreadId', 'WaitForSingleObject', 'CloseHandle', 'VirtualAlloc', 'VirtualFree')
            $missingRunnerImports = @($requiredRunnerImports | Where-Object { $allImportedSymbols -cnotcontains $_ })
            Add-Check "$(Split-Path -Leaf $filePath) imports required Windows loader/thread/storage APIs" ($missingRunnerImports.Count -eq 0) ([ordered]@{ requiredImports = $requiredRunnerImports; missingImports = $missingRunnerImports })
        }
        $importAudits.Add([ordered]@{
            path = $filePath
            sha256 = Get-Hash $filePath
            imports = $imports
            allowedWindowsThreadLoaderImportsRecorded = @($imports | Where-Object { $_.module -match '(?i)^kernel32\.dll$' } | ForEach-Object { $_.symbols })
            importedSymbols = $allImportedSymbols
            unknownModules = $unknownModules
            unknownSymbols = @($unknownSymbols)
            clrImportFound = $banned
            process = $audit
        })
        $peAudits.Add([ordered]@{
            path = $filePath
            sha256 = Get-Hash $filePath
            format = if ($headerX64) { 'COFF-x86-64' } else { $null }
            addressSize = if ($headerX64) { '64bit' } else { $null }
            clrRuntimeHeaderRva = if ($clrRvaMatch.Success) { $clrRvaMatch.Groups[1].Value } else { $null }
            clrRuntimeHeaderSize = if ($clrSizeMatch.Success) { $clrSizeMatch.Groups[1].Value } else { $null }
            process = $headerAudit
        })
    }
    foreach ($libraryPath in @($primaryO0.bootstrap.libraryPath, $primaryO2.bootstrap.libraryPath)) {
        $exportAudit = Invoke-CapturedProcess 'module-export-audit' $readobj @('--coff-exports', $libraryPath) $runDirectory
        Require-ProcessSuccess $exportAudit "PE export audit succeeded for $(Split-Path -Leaf $libraryPath)"
        $descriptorExports = [regex]::Matches($exportAudit.stdout, '(?im)^\s*Name:\s*agentlang_module_descriptor\s*$').Count
        Add-Check "$(Split-Path -Leaf $libraryPath) exports one module descriptor" ($descriptorExports -eq 1) ([ordered]@{ descriptorExportCount = $descriptorExports })
    }
} catch {
    $message = $_.Exception.Message
    $errors.Add($message)
    Add-Check 'verification completed without an uncaught failure' $false $message
} finally {
    $sourceInputAfter = @()
    foreach ($inputPath in $sourceInputPaths) {
        if (Test-Path -LiteralPath $inputPath -PathType Leaf) {
            $sourceInputAfter += [ordered]@{ path = [IO.Path]::GetFullPath($inputPath); sha256 = Get-Hash $inputPath }
        } else {
            $sourceInputAfter += [ordered]@{ path = [IO.Path]::GetFullPath($inputPath); sha256 = $null }
        }
    }
    $afterHashByPath = @{}
    foreach ($item in $sourceInputAfter) { $afterHashByPath[[string]$item.path] = [string]$item.sha256 }
    $changedSourceInputs = @($sourceInputBefore | Where-Object {
        -not $afterHashByPath.ContainsKey([string]$_.path) -or $afterHashByPath[[string]$_.path] -cne [string]$_.sha256
    } | ForEach-Object { $_.path })
    $sourceSnapshotComplete = $sourceInputBefore.Count -eq $sourceInputPaths.Count -and $sourceInputAfter.Count -eq $sourceInputPaths.Count
    $sourceInputsUnchanged = $sourceSnapshotComplete -and $changedSourceInputs.Count -eq 0
    Add-Check 'compiler, runtime, fixture, and verifier inputs stayed unchanged during this run' $sourceInputsUnchanged ([ordered]@{ sourceInputCount = $sourceInputPaths.Count; snapshotComplete = $sourceSnapshotComplete; changed = $changedSourceInputs })
    $report.sourceInputHashes = [ordered]@{
        before = @($sourceInputBefore)
        after = @($sourceInputAfter)
        unchanged = $sourceInputsUnchanged
    }
    $report.completedUtc = [DateTime]::UtcNow.ToString('O')
    $report.processes = @($processes)
    $report.moduleBuilds = @($moduleBuilds)
    $report.nativeBuilds = @($nativeBuilds)
    $report.nativeRuns = @($nativeRuns)
    $report.mailboxUnitRuns = @($mailboxUnitRuns)
    $report.importAudits = @($importAudits)
    $report.peAudits = @($peAudits)
    $report.checks = @($checks)
    $failed = @($checks | Where-Object { -not $_.passed } | ForEach-Object { $_.name })
    $report.passed = $checks.Count -gt 0 -and $failed.Count -eq 0
    if ($failed.Count -gt 0) { $report.failure = $failed -join [Environment]::NewLine }
    elseif ($errors.Count -gt 0) { $report.failure = @($errors) -join [Environment]::NewLine }
    try {
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($reportPath)) | Out-Null
        [IO.File]::WriteAllText($reportPath, (ConvertTo-Json -InputObject $report -Depth 60) + [Environment]::NewLine, $utf8)
    } catch {
        Write-Error ('Could not save integration evidence to {0}: {1}' -f $reportPath, $_.Exception.Message)
        exit 1
    }
}

Write-Output "EvidencePath=$reportPath"
Write-Output "Passed=$($report.passed)"
if (-not $report.passed) {
    Write-Error "Native dispatch integration verification failed. Evidence saved to $reportPath. $($report.failure)"
    exit 1
}
