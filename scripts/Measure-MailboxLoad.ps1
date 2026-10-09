#requires -Version 7.0
[CmdletBinding()]
param(
    [switch]$Smoke
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$experiment = Join-Path $repo 'experiments/AgentLang.MailboxLoad'
$runId = [Guid]::NewGuid().ToString('N')
$runRoot = Join-Path $repo ".agentlang/mailbox-load-140/$runId"
$tempRoot = Join-Path $runRoot 'workspace-temp'
$utf8NoBom = [Text.UTF8Encoding]::new($false)
$dotnetCommand = Get-Command -Name dotnet.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1
$dotnet = [IO.Path]::GetFullPath($dotnetCommand.Source)
$clangCandidates = @(
    'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\Llvm\x64\bin\clang.exe',
    'C:\Program Files\Microsoft Visual Studio\2022\BuildTools\VC\Tools\Llvm\x64\bin\clang.exe',
    'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Tools\Llvm\x64\bin\clang.exe',
    'C:\Program Files (x86)\Microsoft Visual Studio\2022\Community\VC\Tools\Llvm\x64\bin\clang.exe'
)
$clang = $null
foreach ($candidate in $clangCandidates) {
    if (Test-Path -LiteralPath $candidate -PathType Leaf) { $clang = [IO.Path]::GetFullPath($candidate); break }
}
if ($null -eq $clang) {
    $clangCommand = Get-Command -Name clang.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $clang = [IO.Path]::GetFullPath($clangCommand.Source)
}

$bootstrapProject = Join-Path $repo 'experiments/AgentLang.OwningMailbox/AgentLang.OwningMailbox.fsproj'
$bootstrapProgram = Join-Path $repo 'experiments/AgentLang.OwningMailbox/Program.fs'
$flowPath = Join-Path $repo 'experiments/AgentLang.RealIoMailbox/mailbox.flow'
$providerProject = Join-Path $repo 'experiments/AgentLang.RealIoMailbox/Provider/AgentLang.RealIoMailbox.Provider.fsproj'
$providerProgram = Join-Path $repo 'experiments/AgentLang.RealIoMailbox/Provider/Program.fs'
$baselineProject = Join-Path $experiment 'Baseline/AgentLang.MailboxLoad.Baseline.fsproj'
$nativeHost = Join-Path $experiment 'load_host.c'
$nativeDirectory = Join-Path $repo 'src/AgentLang.Llvm/native'
$nativeSources = @(
    $nativeHost,
    (Join-Path $nativeDirectory 'arena_runtime.c'),
    (Join-Path $nativeDirectory 'mailbox_runtime.c'),
    (Join-Path $nativeDirectory 'mailbox_runtime_windows.c'),
    (Join-Path $nativeDirectory 'owning_stack_runtime.c'),
    (Join-Path $nativeDirectory 'owning_bank.c')
)
$nativeHeaders = @(
    (Join-Path $nativeDirectory 'module_abi.h'),
    (Join-Path $nativeDirectory 'arena_runtime.h'),
    (Join-Path $nativeDirectory 'mailbox_runtime.h'),
    (Join-Path $nativeDirectory 'owning_mailbox_abi.h'),
    (Join-Path $nativeDirectory 'owning_stack_runtime.h'),
    (Join-Path $nativeDirectory 'owning_bank.h')
)

$script:commands = [Collections.Generic.List[object]]::new()
$script:trialResults = [Collections.Generic.List[object]]::new()
$script:sourcePaths = @()
$script:sourceHashesBefore = @()
$script:sourceHashesAfter = @()
$script:runnerExe = $null
$script:providerAssembly = $null
$script:baselineAssembly = $null
$script:nativeExe = $null
$script:modulePath = $null
$script:availableAffinityMask = ''
$script:appAffinityMask = ''
$script:providerAffinityMask = ''
$script:buildIdentity = [ordered]@{}
$script:report = [ordered]@{
    schemaVersion = 1
    kind = 'matched-mailbox-load-140-measurement'
    runId = $runId
    startedUtc = [DateTime]::UtcNow.ToString('O')
    repository = $repo
    runDirectory = $runRoot
    tempDirectory = $tempRoot
    smoke = [bool]$Smoke
    measurementScope = 'Deterministic open-loop loopback mailbox workload; arrival scheduling overhead is included. The provider is measured in a separate Job Object and excluded from the application process-commit ceiling.'
    ceilings = [ordered]@{ equalLimitBytes = 268435456; nativeFollowupLimitBytes = 335544320; limitType = 'JOB_OBJECT_LIMIT_JOB_MEMORY process commit for the job and descendants; not resident memory' }
    cpu = [ordered]@{ policy = 'All app variants use one selected CPU; the provider uses a distinct available CPU when one exists.' }
    runtime = $null
    buildIdentity = $null
    sourceHashesBefore = @()
    sourceHashesAfter = @()
    sourceStable = $null
    commands = @()
    trials = @()
    scoringPreflight = $null
    score = $null
    error = $null
}

function Save-RunReport {
    $script:report.buildIdentity = $script:buildIdentity
    $script:report.commands = @($script:commands)
    $script:report.trials = @($script:trialResults)
    $script:report.sourceHashesBefore = @($script:sourceHashesBefore)
    $script:report.sourceHashesAfter = @($script:sourceHashesAfter)
    $script:report.completedUtc = [DateTime]::UtcNow.ToString('O')
    $reportPath = Join-Path $runRoot 'run-report.json'
    [IO.File]::WriteAllText($reportPath, (ConvertTo-Json -InputObject $script:report -Depth 100), $utf8NoBom)
    [IO.File]::WriteAllText((Join-Path $runRoot 'commands.json'), (ConvertTo-Json -InputObject @($script:commands) -Depth 30), $utf8NoBom)
}

function Get-Hash([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-SourceInputs {
    $files = [Collections.Generic.List[string]]::new()
    foreach ($path in @(
        $PSCommandPath, (Join-Path $experiment 'CONTRACT.md'),
        (Join-Path $experiment 'limit_runner.c'), $nativeHost,
        $bootstrapProject, $bootstrapProgram, $flowPath,
        $providerProject, $providerProgram, $baselineProject
    ) + $nativeSources + $nativeHeaders) {
        if (Test-Path -LiteralPath $path -PathType Leaf) { $files.Add([IO.Path]::GetFullPath($path)) }
    }
    foreach ($root in @((Join-Path $experiment 'Baseline'), (Join-Path $repo 'src/AgentLang.Core'), (Join-Path $repo 'src/AgentLang.Llvm'))) {
        if (Test-Path -LiteralPath $root -PathType Container) {
            Get-ChildItem -LiteralPath $root -Recurse -File |
                Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.Extension -in @('.fs', '.fsproj', '.props', '.targets', '.c', '.h') } |
                ForEach-Object { $files.Add([IO.Path]::GetFullPath($_.FullName)) }
        }
    }
    foreach ($name in @('AgentLang.sln', 'Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'global.json', 'NuGet.config')) {
        $path = Join-Path $repo $name
        if (Test-Path -LiteralPath $path -PathType Leaf) { $files.Add([IO.Path]::GetFullPath($path)) }
    }
    return @($files | Sort-Object -Unique)
}

function Get-HashList([string[]]$Paths) {
    $hashes = [Collections.Generic.List[object]]::new()
    foreach ($path in $Paths) { $hashes.Add([ordered]@{ path = $path; sha256 = Get-Hash $path }) }
    return @($hashes)
}

function New-ProcessStartInfo([string]$Executable, [string[]]$Arguments, [string]$WorkingDirectory, [string]$StdoutPath, [string]$StderrPath) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Executable
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add([string]$argument) }
    $start.Environment['TEMP'] = $tempRoot
    $start.Environment['TMP'] = $tempRoot
    return $start
}

function Add-CommandRecord([string]$Name, [string]$Executable, [string[]]$Arguments, [string]$WorkingDirectory, [string]$StdoutPath, [string]$StderrPath, [int]$ExitCode, [bool]$TimedOut) {
    $script:commands.Add([ordered]@{
        name = $Name; executable = $Executable; arguments = @($Arguments); workingDirectory = $WorkingDirectory
        stdoutPath = $StdoutPath; stderrPath = $StderrPath; exitCode = $ExitCode; timedOut = $TimedOut
    })
    Save-RunReport
}

function Invoke-LoggedProcess([string]$Name, [string]$Executable, [string[]]$Arguments, [string]$WorkingDirectory, [int]$TimeoutMs = 300000) {
    $safeName = $Name -replace '[^a-zA-Z0-9_-]', '_'
    $stdoutPath = Join-Path $runRoot "$safeName.stdout.txt"
    $stderrPath = Join-Path $runRoot "$safeName.stderr.txt"
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = New-ProcessStartInfo $Executable $Arguments $WorkingDirectory $stdoutPath $stderrPath
    $stdoutTask = $null
    $stderrTask = $null
    $exitCode = -1
    $timedOut = $false
    try {
        if (-not $process.Start()) { throw "${Name}: Process.Start returned false." }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutMs)) {
            $timedOut = $true
            try { $process.Kill($true) } catch { }
            [void]$process.WaitForExit(5000)
        }
        if ($process.HasExited) { $exitCode = $process.ExitCode }
        $stdout = if ($null -ne $stdoutTask) { $stdoutTask.GetAwaiter().GetResult() } else { '' }
        $stderr = if ($null -ne $stderrTask) { $stderrTask.GetAwaiter().GetResult() } else { '' }
        [IO.File]::WriteAllText($stdoutPath, $stdout, $utf8NoBom)
        [IO.File]::WriteAllText($stderrPath, $stderr, $utf8NoBom)
    } finally { $process.Dispose() }
    Add-CommandRecord $Name $Executable $Arguments $WorkingDirectory $stdoutPath $stderrPath $exitCode $timedOut
    if ($timedOut -or $exitCode -ne 0) { throw "$Name failed (exit=$exitCode, timedOut=$timedOut). See $stderrPath and $stdoutPath." }
    return [ordered]@{ stdout = $stdout; stderr = $stderr; exitCode = $exitCode; stdoutPath = $stdoutPath; stderrPath = $stderrPath }
}

function Get-BuiltAssembly([string]$ArtifactsRoot, [string]$AssemblyName) {
    $matches = @(Get-ChildItem -LiteralPath (Join-Path $ArtifactsRoot 'bin') -Recurse -File -Filter $AssemblyName |
        Where-Object { $_.FullName -notmatch '[\\/]ref[\\/]' })
    if ($matches.Count -ne 1) { throw "Expected one $AssemblyName in the fresh artifacts tree, found $($matches.Count)." }
    return [IO.Path]::GetFullPath($matches[0].FullName)
}

function Read-JsonFile([string]$Path, [string]$Label) {
    try { return (ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($Path)) -AsHashtable -Depth 100 -ErrorAction Stop) }
    catch { throw "$Label is invalid JSON at ${Path}: $($_.Exception.Message)" }
}

function Get-ExactField($Object, [string]$Name, [string]$Label) {
    if ($null -eq $Object -or $Object -isnot [Collections.IDictionary]) { throw "$Label must be a JSON object." }
    foreach ($key in $Object.Keys) {
        if ([string]::Equals([string]$key, $Name, [StringComparison]::Ordinal)) { return ,($Object[$key]) }
    }
    throw "$Label is missing required exact-case field '$Name'."
}

function Require-Integer($Value, [string]$Label, [long]$Minimum = 0) {
    if ($Value -is [bool] -or $Value -isnot [sbyte] -and $Value -isnot [byte] -and $Value -isnot [int16] -and $Value -isnot [uint16] -and $Value -isnot [int32] -and $Value -isnot [uint32] -and $Value -isnot [int64] -and $Value -isnot [uint64]) {
        throw "$Label must be a JSON integer; found $($Value.GetType().FullName)."
    }
    $number = [long]$Value
    if ($number -lt $Minimum) { throw "$Label must be at least $Minimum." }
    return $number
}

function Require-Boolean($Value, [string]$Label) {
    if ($Value -isnot [bool]) { throw "$Label must be a JSON boolean." }
    return $Value
}

function Normalize-AffinityMask([string]$Value, [string]$Label) {
    if ($Value -notmatch '^0[xX][0-9a-fA-F]+$') { throw "$Label must be a hexadecimal affinity mask." }
    $numeric = [Convert]::ToUInt64($Value.Substring(2), 16)
    if ($numeric -eq 0) { throw "$Label must be nonzero." }
    return ('0x{0:x}' -f $numeric)
}

function Start-RunnerProcess([string]$Name, [string[]]$RunnerArguments, [string]$WorkingDirectory, [string]$LogStem) {
    $runnerStdoutPath = Join-Path $runRoot "$LogStem.launcher.stdout.txt"
    $runnerStderrPath = Join-Path $runRoot "$LogStem.launcher.stderr.txt"
    $start = New-ProcessStartInfo $script:runnerExe $RunnerArguments $WorkingDirectory $runnerStdoutPath $runnerStderrPath
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (-not $process.Start()) { throw "$Name launcher failed to start." }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    return [pscustomobject]@{
        Name = $Name; Process = $process; StdoutTask = $stdoutTask; StderrTask = $stderrTask
        StdoutPath = $runnerStdoutPath; StderrPath = $runnerStderrPath; Arguments = @($RunnerArguments)
        WorkingDirectory = $WorkingDirectory; StartedUtc = [DateTime]::UtcNow.ToString('O'); Finalized = $false; Record = $null
    }
}

function Finish-RunnerProcess($Handle, [int]$ExpectedWaitMs) {
    if ($Handle.Finalized) { return $Handle.Record }
    $timedOut = $false
    $exitCode = -1
    if (-not $Handle.Process.WaitForExit($ExpectedWaitMs)) {
        $timedOut = $true
        try { $Handle.Process.Kill($true) } catch { }
        [void]$Handle.Process.WaitForExit(5000)
    }
    if ($Handle.Process.HasExited) { $exitCode = $Handle.Process.ExitCode }
    $launcherStdout = $Handle.StdoutTask.GetAwaiter().GetResult()
    $launcherStderr = $Handle.StderrTask.GetAwaiter().GetResult()
    [IO.File]::WriteAllText($Handle.StdoutPath, $launcherStdout, $utf8NoBom)
    [IO.File]::WriteAllText($Handle.StderrPath, $launcherStderr, $utf8NoBom)
    $Handle.Process.Dispose()
    $Handle.Finalized = $true
    Add-CommandRecord $Handle.Name $script:runnerExe $Handle.Arguments $Handle.WorkingDirectory $Handle.StdoutPath $Handle.StderrPath $exitCode $timedOut
    $Handle.Record = [ordered]@{ exitCode = $exitCode; timedOut = $timedOut; stdoutPath = $Handle.StdoutPath; stderrPath = $Handle.StderrPath; stdout = $launcherStdout; stderr = $launcherStderr }
    return $Handle.Record
}

function New-RunnerArguments([long]$LimitBytes, [int]$TimeoutMs, [string]$StdoutPath, [string]$StderrPath, [string]$ResultPath, [string]$Executable, [string[]]$ChildArguments, [string]$WorkingDirectory, [int]$DrainMs = 5000, [string]$AffinityMask = '') {
    $arguments = [Collections.Generic.List[string]]::new()
    foreach ($name in @('--limit-bytes', [string]$LimitBytes, '--timeout-ms', [string]$TimeoutMs, '--drain-ms', [string]$DrainMs,
        '--stdout', $StdoutPath, '--stderr', $StderrPath, '--result', $ResultPath, '--exe', $Executable, '--cwd', $WorkingDirectory)) {
        $arguments.Add($name)
    }
    if (-not [string]::IsNullOrWhiteSpace($AffinityMask)) { $arguments.Add('--affinity-mask'); $arguments.Add($AffinityMask) }
    foreach ($argument in $ChildArguments) { $arguments.Add('--arg'); $arguments.Add([string]$argument) }
    return @($arguments)
}

function Read-LastJsonLine([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "$Label output file is missing: $Path" }
    $lines = @([IO.File]::ReadAllLines($Path) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($lines.Count -eq 0) { throw "$Label output file is empty: $Path" }
    try { return (ConvertFrom-Json -InputObject $lines[$lines.Count - 1] -AsHashtable -Depth 100 -ErrorAction Stop) }
    catch { throw "$Label final line is not JSON: $($_.Exception.Message)" }
}

function Start-Provider([string]$TrialDirectory, [int]$TimeoutMs, [string]$CommonProviderLimit) {
    $providerDirectory = Join-Path $TrialDirectory 'provider'
    [IO.Directory]::CreateDirectory($providerDirectory) | Out-Null
    $readyPath = Join-Path $providerDirectory 'ready.json'
    $stopPath = Join-Path $providerDirectory 'stop.signal'
    $stdoutPath = Join-Path $providerDirectory 'provider.stdout.txt'
    $stderrPath = Join-Path $providerDirectory 'provider.stderr.txt'
    $runnerPath = Join-Path $providerDirectory 'runner.json'
    $work = $providerDirectory
    $childArgs = @(
        $script:providerAssembly, '--port', '0', '--fragment-bytes', '4096', '--ready-file', $readyPath,
        '--stop-file', $stopPath, '--persistent', 'true'
    )
    $runnerArgs = New-RunnerArguments ([long]$CommonProviderLimit) $TimeoutMs $stdoutPath $stderrPath $runnerPath $dotnet $childArgs $work 5000 $script:providerAffinityMask
    $handle = Start-RunnerProcess 'provider' $runnerArgs $work 'provider'
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        while ([DateTime]::UtcNow -lt $deadline) {
            if (Test-Path -LiteralPath $readyPath -PathType Leaf) { break }
            if ($handle.Process.HasExited) { break }
            Start-Sleep -Milliseconds 50
        }
        if (-not (Test-Path -LiteralPath $readyPath -PathType Leaf)) {
            throw "Provider did not publish its ready file within 20 seconds. See $stderrPath."
        }
        $ready = Read-JsonFile $readyPath 'Provider ready file'
        $address = [string](Get-ExactField $ready 'address' 'provider ready file')
        $port = Require-Integer (Get-ExactField $ready 'port' 'provider ready file') 'provider.port' 1
        if ($port -gt 65535 -or $address -cne '127.0.0.1') { throw "Provider must publish a loopback IPv4 endpoint and valid port; got $address`:$port." }
        return [pscustomobject]@{ Handle = $handle; Ready = $ready; Port = [int]$port; ReadyPath = $readyPath; StopPath = $stopPath; StdoutPath = $stdoutPath; StderrPath = $stderrPath; RunnerPath = $runnerPath; Directory = $providerDirectory }
    } catch {
        try { [IO.File]::WriteAllText($stopPath, 'stop', $utf8NoBom); $null = Finish-RunnerProcess $handle 12000 } catch { }
        throw
    }
}

function Stop-Provider($Provider, [long]$ExpectedFrames) {
    $alreadyExited = $Provider.Handle.Process.HasExited
    if (-not $alreadyExited) { [IO.File]::WriteAllText($Provider.StopPath, 'stop', $utf8NoBom) }
    $launcher = Finish-RunnerProcess $Provider.Handle 12000
    if ($launcher.timedOut -or $launcher.exitCode -ne 0) { throw "Provider Job Object launcher failed (exit=$($launcher.exitCode), timeout=$($launcher.timedOut))." }
    $runner = Read-JsonFile $Provider.RunnerPath 'Provider process measurement'
    $providerLimitsOkay = (Require-Boolean (Get-ExactField $runner 'capConfigured' 'provider runner metadata') 'provider.capConfigured') -and
        (Require-Boolean (Get-ExactField $runner 'jobAssigned' 'provider runner metadata') 'provider.jobAssigned')
    if (-not $providerLimitsOkay) { throw 'Provider did not run inside its separately measured Job Object.' }
    if ((Get-ExactField $runner 'memoryLimitScope' 'provider runner metadata') -cne 'jobProcessCommitIncludingDescendants') { throw 'Provider Job Object ceiling scope was not identified as process commit.' }
    if ((Require-Boolean (Get-ExactField $runner 'timedOut' 'provider runner metadata') 'provider.timedOut') -or
        (Require-Boolean (Get-ExactField $runner 'jobDrainTimedOut' 'provider runner metadata') 'provider.jobDrainTimedOut')) {
        throw 'Provider timed out or failed to drain its Job Object.'
    }
    $exit = Require-Integer (Get-ExactField $runner 'exitCode' 'provider runner metadata') 'provider.exitCode'
    if ($exit -ne 0) { throw "Provider exited with code $exit." }
    if ($alreadyExited) { throw 'Provider exited before the coordinator wrote its stop file.' }
    $counters = Read-LastJsonLine $Provider.StdoutPath 'Provider shutdown counters'
    foreach ($field in @('accepted', 'completed', 'acceptedConnections', 'acceptedFrames', 'completedFrames', 'rejectedBusy', 'invalidFrames', 'timedOut', 'cancelled', 'ioFailures', 'internalErrors')) {
        $null = Require-Integer (Get-ExactField $counters $field 'provider final counters') "provider.$field"
    }
    $acceptedConnections = [long]$counters.acceptedConnections
    $acceptedFrames = [long]$counters.acceptedFrames
    $completedFrames = [long]$counters.completedFrames
    if ($acceptedConnections -ne 16 -or $counters.accepted -ne $acceptedConnections -or
        $counters.completed -ne $completedFrames -or $acceptedFrames -ne $ExpectedFrames -or
        $completedFrames -ne $ExpectedFrames -or $counters.rejectedBusy -ne 0 -or
        $counters.invalidFrames -ne 0 -or $counters.timedOut -ne 0 -or $counters.cancelled -ne 0 -or
        $counters.ioFailures -ne 0 -or $counters.internalErrors -ne 0) {
        throw "Provider frames/connections did not reconcile: expectedFrames=$ExpectedFrames acceptedFrames=$acceptedFrames completedFrames=$completedFrames acceptedConnections=$acceptedConnections; see $($Provider.StdoutPath)."
    }
    return [ordered]@{
        counters = $counters
        process = $runner
        readyFile = $Provider.ReadyPath
        stdoutPath = $Provider.StdoutPath
        stderrPath = $Provider.StderrPath
        separatelyMeasured = $true
        excludedFromApplicationCeiling = $true
    }
}

function Get-Percentile([long[]]$Values, [double]$Percentile) {
    if ($Values.Length -eq 0) { return $null }
    $sorted = [long[]]$Values.Clone()
    [Array]::Sort($sorted)
    $rank = [int][Math]::Ceiling($Percentile * $sorted.Length)
    return $sorted[[Math]::Max(0, $rank - 1)]
}

function Invoke-LauncherControls {
    $probeSource = Join-Path $runRoot 'launcher-control-probe.c'
    $probeExe = Join-Path $runRoot 'launcher-control-probe.exe'
    $probeText = @'
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <wchar.h>

int wmain(int argc, wchar_t **argv) {
  if (argc != 2) return 81;
  if (wcscmp(argv[1], L"cap") == 0) {
    const SIZE_T request_bytes = 64U * 1024U * 1024U;
    const SIZE_T page_bytes = 4096U;
    SIZE_T offset;
    unsigned char *region = (unsigned char *)VirtualAlloc(NULL, request_bytes, MEM_RESERVE, PAGE_READWRITE);
    if (region == NULL) return 82;
    puts("cap probe reserved 64 MiB; committing and touching pages");
    fflush(stdout);
    for (offset = 0; offset < request_bytes; offset += page_bytes) {
      void *page = VirtualAlloc(region + offset, page_bytes, MEM_COMMIT, PAGE_READWRITE);
      if (page == NULL) {
        printf("job ceiling blocked commit at %llu bytes (error=%lu)\n",
               (unsigned long long)offset, (unsigned long)GetLastError());
        fflush(stdout);
        return 77;
      }
      ((volatile unsigned char *)page)[0] = (unsigned char)(offset / page_bytes);
    }
    puts("cap probe committed and touched all 64 MiB");
    fflush(stdout);
    return 0;
  }
  if (wcscmp(argv[1], L"timeout") == 0) {
    puts("timeout probe entered its bounded wait");
    fflush(stdout);
    Sleep(60000);
    return 0;
  }
  if (wcscmp(argv[1], L"exit") == 0) {
    puts("exit probe stdout marker");
    fputs("exit probe stderr marker\n", stderr);
    fflush(stdout);
    fflush(stderr);
    return 23;
  }
  return 82;
}
'@
    [IO.File]::WriteAllText($probeSource, $probeText, $utf8NoBom)
    $compileArgs = @('--target=x86_64-pc-windows-msvc', '-std=c11', '-Wall', '-Wextra', '-Werror', '-O2', $probeSource, '-o', $probeExe)
    $null = Invoke-LoggedProcess 'build-launcher-control-probe' $clang $compileArgs $repo
    $controlResults = [Collections.Generic.List[object]]::new()

    $capPositiveDir = Join-Path $runRoot 'controls/cap-positive'
    [IO.Directory]::CreateDirectory($capPositiveDir) | Out-Null
    $capPositiveOut = Join-Path $capPositiveDir 'stdout.txt'; $capPositiveErr = Join-Path $capPositiveDir 'stderr.txt'; $capPositiveResultPath = Join-Path $capPositiveDir 'runner.json'
    $capPositiveArgs = New-RunnerArguments 134217728 10000 $capPositiveOut $capPositiveErr $capPositiveResultPath $probeExe @('cap') $capPositiveDir
    $capPositiveHandle = Start-RunnerProcess 'launcher-control-cap-positive' $capPositiveArgs $capPositiveDir 'control-cap-positive'
    $capPositiveLauncher = Finish-RunnerProcess $capPositiveHandle 20000
    if ($capPositiveLauncher.exitCode -ne 0 -or $capPositiveLauncher.timedOut) { throw 'Memory-ceiling positive control launcher failed.' }
    $capPositive = Read-JsonFile $capPositiveResultPath 'Memory-ceiling positive control metadata'
    if (-not (Require-Boolean (Get-ExactField $capPositive 'capConfigured' 'memory positive control metadata') 'capPositive.capConfigured') -or
        -not (Require-Boolean (Get-ExactField $capPositive 'jobAssigned' 'memory positive control metadata') 'capPositive.jobAssigned')) { throw 'Memory-ceiling positive control did not configure and assign the job.' }
    if ((Get-ExactField $capPositive 'memoryLimitScope' 'memory positive control metadata') -cne 'jobProcessCommitIncludingDescendants' -or
        (Require-Integer (Get-ExactField $capPositive 'enforcedJobMemoryLimitBytes' 'memory positive control metadata') 'capPositive.limit') -ne 134217728 -or
        (Require-Integer (Get-ExactField $capPositive 'exitCode' 'memory positive control metadata') 'capPositive.exitCode') -ne 0 -or
        (Require-Boolean (Get-ExactField $capPositive 'timedOut' 'memory positive control metadata') 'capPositive.timedOut')) {
        throw 'Memory-ceiling positive control did not complete under its 128 MiB Job Object limit.'
    }
    if (-not [IO.File]::ReadAllText($capPositiveOut).Contains('cap probe committed and touched all 64 MiB')) { throw 'Memory-ceiling positive control did not commit and touch all 64 MiB.' }
    $controlResults.Add([ordered]@{ name = 'job-memory-cap-positive-control'; passed = $true; peakJobCommittedBytes = (Require-Integer (Get-ExactField $capPositive 'peakJobCommittedBytes' 'memory positive control metadata') 'capPositive.peakJobCommittedBytes'); enforcedLimitBytes = 134217728; childExitCode = 0; stdoutPath = $capPositiveOut; stderrPath = $capPositiveErr; runner = $capPositive })

    $capDir = Join-Path $runRoot 'controls/cap-negative'
    [IO.Directory]::CreateDirectory($capDir) | Out-Null
    $capOut = Join-Path $capDir 'stdout.txt'; $capErr = Join-Path $capDir 'stderr.txt'; $capResultPath = Join-Path $capDir 'runner.json'
    $capArgs = New-RunnerArguments 16777216 10000 $capOut $capErr $capResultPath $probeExe @('cap') $capDir
    $capHandle = Start-RunnerProcess 'launcher-control-cap-negative' $capArgs $capDir 'control-cap'
    $capLauncher = Finish-RunnerProcess $capHandle 20000
    if ($capLauncher.exitCode -ne 0 -or $capLauncher.timedOut) { throw 'Memory-ceiling negative control launcher failed.' }
    $cap = Read-JsonFile $capResultPath 'Memory-ceiling control metadata'
    if (-not (Require-Boolean (Get-ExactField $cap 'capConfigured' 'memory control metadata') 'cap.capConfigured') -or
        -not (Require-Boolean (Get-ExactField $cap 'jobAssigned' 'memory control metadata') 'cap.jobAssigned')) { throw 'Memory-ceiling control did not configure and assign the job.' }
    if ((Get-ExactField $cap 'memoryLimitScope' 'memory control metadata') -cne 'jobProcessCommitIncludingDescendants') { throw 'Memory-ceiling control reports the wrong limit scope.' }
    if ((Require-Integer (Get-ExactField $cap 'enforcedJobMemoryLimitBytes' 'memory control metadata') 'cap.limit') -ne 16777216) { throw 'Memory-ceiling control limit metadata does not match 16 MiB.' }
    $capPeak = Require-Integer (Get-ExactField $cap 'peakJobCommittedBytes' 'memory control metadata') 'cap.peakJobCommittedBytes'
    $capExit = Require-Integer (Get-ExactField $cap 'exitCode' 'memory control metadata') 'cap.exitCode'
    $capOutput = [IO.File]::ReadAllText($capOut)
    $blockedCommit = [regex]::Match($capOutput, 'job ceiling blocked commit at ([0-9]+) bytes \(error=([0-9]+)\)')
    if (-not $blockedCommit.Success) { throw 'Memory cap negative control did not report the failed page commit and committed payload count.' }
    $blockedPayloadBytes = [long]::Parse($blockedCommit.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture)
    $blockedCommitError = [int]::Parse($blockedCommit.Groups[2].Value, [Globalization.CultureInfo]::InvariantCulture)
    if ((Require-Boolean (Get-ExactField $cap 'timedOut' 'memory control metadata') 'cap.timedOut') -or
        $capExit -ne 77 -or $blockedCommitError -ne 1455 -or
        $blockedPayloadBytes -lt 12582912 -or $blockedPayloadBytes -ge 16777216 -or $capPeak -lt 12582912) {
        throw "Memory cap negative control did not demonstrate enforcement (peak=$capPeak exit=$capExit)."
    }
    if (-not $capOutput.Contains('cap probe reserved 64 MiB')) { throw 'Memory cap control did not reach its page-commit loop.' }
    $capPeakOvershoot = [Math]::Max(0, $capPeak - 16777216)
    $controlResults.Add([ordered]@{ name = 'job-memory-cap-negative-control'; passed = $true; peakJobCommittedBytes = $capPeak; peakJobCommitOvershootBytes = $capPeakOvershoot; enforcedLimitBytes = 16777216; childCommittedPayloadBytesBeforeBlock = $blockedPayloadBytes; failedCommitErrorCode = $blockedCommitError; childExitCode = $capExit; stdoutPath = $capOut; stderrPath = $capErr; runner = $cap })

    $timeoutDir = Join-Path $runRoot 'controls/timeout'
    [IO.Directory]::CreateDirectory($timeoutDir) | Out-Null
    $timeoutOut = Join-Path $timeoutDir 'stdout.txt'; $timeoutErr = Join-Path $timeoutDir 'stderr.txt'; $timeoutResultPath = Join-Path $timeoutDir 'runner.json'
    $timeoutArgs = New-RunnerArguments 67108864 1000 $timeoutOut $timeoutErr $timeoutResultPath $probeExe @('timeout') $timeoutDir
    $timeoutHandle = Start-RunnerProcess 'launcher-control-timeout' $timeoutArgs $timeoutDir 'control-timeout'
    $timeoutLauncher = Finish-RunnerProcess $timeoutHandle 16000
    if ($timeoutLauncher.exitCode -ne 0 -or $timeoutLauncher.timedOut) { throw 'Timeout control launcher failed instead of reporting a child timeout.' }
    $timeout = Read-JsonFile $timeoutResultPath 'Timeout control metadata'
    $timeoutChildExit = Require-Integer (Get-ExactField $timeout 'exitCode' 'timeout control metadata') 'timeout.exitCode'
    if (-not (Require-Boolean (Get-ExactField $timeout 'timedOut' 'timeout control metadata') 'timeout.timedOut') -or $timeoutChildExit -eq 0 -or
        -not (Require-Boolean (Get-ExactField $timeout 'jobAssigned' 'timeout control metadata') 'timeout.jobAssigned') -or
        (Require-Integer (Get-ExactField $timeout 'peakJobCommittedBytes' 'timeout control metadata') 'timeout.peakJobCommittedBytes') -eq 0) {
        throw 'Timeout control did not kill the child job and report actual resource metadata.'
    }
    if (-not [IO.File]::ReadAllText($timeoutOut).Contains('timeout probe entered its bounded wait')) { throw 'Timeout control did not start the child wait before termination.' }
    $controlResults.Add([ordered]@{ name = 'job-timeout-control'; passed = $true; childExitCode = $timeoutChildExit; stdoutPath = $timeoutOut; stderrPath = $timeoutErr; runner = $timeout })

    $exitDir = Join-Path $runRoot 'controls/exit-code'
    [IO.Directory]::CreateDirectory($exitDir) | Out-Null
    $exitOut = Join-Path $exitDir 'stdout.txt'; $exitErr = Join-Path $exitDir 'stderr.txt'; $exitResultPath = Join-Path $exitDir 'runner.json'
    $exitArgs = New-RunnerArguments 67108864 10000 $exitOut $exitErr $exitResultPath $probeExe @('exit') $exitDir
    $exitHandle = Start-RunnerProcess 'launcher-control-exit-code' $exitArgs $exitDir 'control-exit'
    $exitLauncher = Finish-RunnerProcess $exitHandle 20000
    if ($exitLauncher.exitCode -ne 0 -or $exitLauncher.timedOut) { throw 'Exit-code control launcher failed.' }
    $exitControl = Read-JsonFile $exitResultPath 'Exit-code control metadata'
    if ((Require-Integer (Get-ExactField $exitControl 'exitCode' 'exit control metadata') 'exit.childExitCode') -ne 23 -or
        (Require-Boolean (Get-ExactField $exitControl 'timedOut' 'exit control metadata') 'exit.timedOut')) { throw 'Runner did not preserve the child failure exit code independently of its own exit status.' }
    $capturedOut = [IO.File]::ReadAllText($exitOut)
    $capturedErr = [IO.File]::ReadAllText($exitErr)
    if (-not $capturedOut.Contains('exit probe stdout marker') -or -not $capturedErr.Contains('exit probe stderr marker')) { throw 'Child stdout/stderr capture control failed.' }
    $controlResults.Add([ordered]@{ name = 'child-exit-and-output-capture-control'; passed = $true; childExitCode = 23; stdoutPath = $exitOut; stderrPath = $exitErr; runner = $exitControl })
    $selectedMasks = @(@(
        (Normalize-AffinityMask ([string](Get-ExactField $cap 'affinityMask' 'memory control metadata')) 'cap.affinityMask'),
        (Normalize-AffinityMask ([string](Get-ExactField $timeout 'affinityMask' 'timeout control metadata')) 'timeout.affinityMask'),
        (Normalize-AffinityMask ([string](Get-ExactField $exitControl 'affinityMask' 'exit control metadata')) 'exit.affinityMask')
    ) | Sort-Object -Unique)
    if ($selectedMasks.Count -ne 1) { throw 'Launcher control children did not receive one consistent default CPU.' }
    $availableMasks = @(@(
        (Normalize-AffinityMask ([string](Get-ExactField $cap 'availableAffinityMask' 'memory control metadata')) 'cap.availableAffinityMask'),
        (Normalize-AffinityMask ([string](Get-ExactField $timeout 'availableAffinityMask' 'timeout control metadata')) 'timeout.availableAffinityMask'),
        (Normalize-AffinityMask ([string](Get-ExactField $exitControl 'availableAffinityMask' 'exit control metadata')) 'exit.availableAffinityMask')
    ) | Sort-Object -Unique)
    if ($availableMasks.Count -ne 1) { throw 'Launcher controls observed different available CPU masks.' }
    $script:appAffinityMask = $selectedMasks[0]
    $script:availableAffinityMask = $availableMasks[0]
    $availableValue = [Convert]::ToUInt64($script:availableAffinityMask.Substring(2), 16)
    $appValue = [Convert]::ToUInt64($script:appAffinityMask.Substring(2), 16)
    if (($availableValue -band $appValue) -ne $appValue) { throw 'Selected app CPU is absent from the launcher available mask.' }
    $script:providerAffinityMask = $script:appAffinityMask
    $candidateBit = [uint64]1
    for ($bitIndex = 0; $bitIndex -lt 64; $bitIndex++) {
        if (($availableValue -band $candidateBit) -ne 0 -and $candidateBit -ne $appValue) {
            $script:providerAffinityMask = '0x{0:x}' -f $candidateBit
            break
        }
        if ($bitIndex -lt 63) { $candidateBit = [uint64]($candidateBit * [uint64]2) }
    }
    $providerSeparate = $script:providerAffinityMask -cne $script:appAffinityMask
    $script:report.cpu = [ordered]@{
        availableAffinityMask = $script:availableAffinityMask
        applicationAffinityMask = $script:appAffinityMask
        providerAffinityMask = $script:providerAffinityMask
        providerUsesSeparateCpu = $providerSeparate
        limitation = if ($providerSeparate) { $null } else { 'Only one available processor bit was exposed to the launcher; provider and app share it.' }
        selectedFrom = 'GetProcessAffinityMask(processMask & systemMask)'
    }
    $script:report.controls = @($controlResults)
    Save-RunReport
}

function Validate-AppResult($Result, [string]$Backend, [string]$Policy, [int]$Rate, [int]$PayloadBytes, [int]$DelayMs, [int]$WarmupMs, [int]$DurationMs, [int]$ScratchSlots) {
    $schema = Require-Integer (Get-ExactField $Result 'schemaVersion' 'application result') 'schemaVersion'
    if ($schema -ne 1) { throw "Unsupported application result schemaVersion $schema." }
    foreach ($pair in @(@('backend', $Backend), @('policy', $Policy))) {
        $actual = [string](Get-ExactField $Result $pair[0] 'application result')
        if ($actual -cne $pair[1]) { throw "Application $($pair[0]) mismatch: expected '$($pair[1])', found '$actual'." }
    }
    foreach ($pair in @(
        @('rate', $Rate), @('payloadBytes', $PayloadBytes), @('delayMs', $DelayMs), @('warmupMs', $WarmupMs), @('durationMs', $DurationMs)
    )) {
        $actual = Require-Integer (Get-ExactField $Result $pair[0] 'application result') "result.$($pair[0])"
        if ($actual -ne $pair[1]) { throw "Application result $($pair[0]) mismatch: expected $($pair[1]), found $actual." }
    }
    $numericFields = @('offered', 'admitted', 'rejected', 'completed', 'completedWithinWindow', 'warmupCompleted', 'errors', 'timedOut', 'pendingAtEnd', 'peakPending', 'missedArrivals', 'maxDispatchLatenessMicroseconds')
    $values = [ordered]@{}
    foreach ($field in $numericFields) { $values[$field] = Require-Integer (Get-ExactField $Result $field 'application result') "result.$field" }
    $expectedOffered = [long][Math]::Ceiling(($Rate * $DurationMs) / 1000.0)
    if ($values.offered -ne $expectedOffered) { throw "Offered count differs from the open-loop schedule: expected=$expectedOffered actual=$($values.offered)." }
    if ($values.offered -ne ($values.admitted + $values.rejected)) { throw 'Application offered count does not equal admitted plus rejected.' }
    if ($values.missedArrivals -gt $values.rejected) { throw 'missedArrivals must be a subset of rejected.' }
    if ($values.admitted -ne ($values.completed + $values.errors + $values.timedOut)) { throw 'Application admitted outcomes do not reconcile after drain.' }
    if ($values.completedWithinWindow -gt $values.completed) { throw 'completedWithinWindow cannot exceed completed.' }
    if ($values.peakPending -gt 16) { throw 'Application pending-operation count exceeds the shared 16-operation bound.' }
    $verified = Require-Boolean (Get-ExactField $Result 'verified' 'application result') 'result.verified'
    if (-not $verified -or $values.errors -ne 0 -or $values.timedOut -ne 0 -or $values.pendingAtEnd -ne 0) {
        throw "Application correctness failed: verified=$verified errors=$($values.errors) timeouts=$($values.timedOut) pendingAtEnd=$($values.pendingAtEnd)."
    }
    if ($values.errors -ne 0 -or $values.timedOut -ne 0) { throw 'Correctness/protocol errors invalidate the comparison.' }

    $latenciesRaw = Get-ExactField $Result 'latencyMicroseconds' 'application result'
    if ($latenciesRaw -isnot [Array]) { throw 'latencyMicroseconds must be a JSON array.' }
    if ($latenciesRaw.Count -ne $values.completed) { throw "Latency sample count $($latenciesRaw.Count) differs from completed count $($values.completed)." }
    $latencies = [Collections.Generic.List[long]]::new()
    foreach ($latency in $latenciesRaw) { $latencies.Add((Require-Integer $latency 'latencyMicroseconds[]')) }

    $mailboxes = Get-ExactField $Result 'mailboxes' 'application result'
    if ($mailboxes -isnot [Array] -or $mailboxes.Count -ne 16) { throw 'Application must report exactly 16 mailbox states.' }
    $attemptedTotal = 0L
    $completedTotal = 0L
    $seenIds = [Collections.Generic.HashSet[int]]::new()
    foreach ($mailbox in $mailboxes) {
        $id = [int](Require-Integer (Get-ExactField $mailbox 'id' 'mailbox result') 'mailbox.id')
        if ($id -lt 0 -or $id -gt 15 -or -not $seenIds.Add($id)) { throw "Invalid or duplicate mailbox id $id." }
        $attempted = Require-Integer (Get-ExactField $mailbox 'attempted' 'mailbox result') "mailbox[$id].attempted"
        $mailboxCompleted = Require-Integer (Get-ExactField $mailbox 'completed' 'mailbox result') "mailbox[$id].completed"
        $latestVerified = Require-Boolean (Get-ExactField $mailbox 'latestVerified' 'mailbox result') "mailbox[$id].latestVerified"
        if (-not $latestVerified) { throw "Mailbox $id final latest content failed independent verification." }
        if ($mailboxCompleted -gt $attempted) { throw "Mailbox $id completed count exceeds attempted count." }
        $attemptedTotal += $attempted
        $completedTotal += $mailboxCompleted
    }
    if ($attemptedTotal -ne $values.admitted -or $completedTotal -ne $values.completed) {
        throw "Independent mailbox counters do not match application counters: attempts=$attemptedTotal/$($values.admitted), completions=$completedTotal/$($values.completed)."
    }
    $storage = Get-ExactField $Result 'storageReservedBytes' 'application result'
    $reportedScratchSlots = $null
    if ($Backend -ceq 'native') { $reportedScratchSlots = Get-ExactField $Result 'scratchSlots' 'application result' }
    elseif ($Result.Contains('scratchSlots')) { $reportedScratchSlots = $Result['scratchSlots'] }
    if ($Backend -ceq 'native') {
        $storageBytes = Require-Integer $storage 'result.storageReservedBytes' 1
        if ((Require-Integer $reportedScratchSlots 'result.scratchSlots' 1) -ne $ScratchSlots) { throw 'Native reported a different scratch slot count than requested.' }
    } else {
        if ($null -ne $storage) { throw 'F# storageReservedBytes must be null.' }
        $storageBytes = $null
        if ($null -ne $reportedScratchSlots) { throw 'F# scratchSlots must be null.' }
    }
    if ($Backend -ceq 'native' -and $Policy -ceq 'keep' -and $ScratchSlots -eq 4 -and $values.peakPending -gt 4) { throw 'Native four-slot KEEP mode exceeded its declared retained-scratch capacity.' }

    $p99 = Get-Percentile ([long[]]$latencies.ToArray()) 0.99
    $rejectionFraction = if ($values.offered -eq 0) { 0.0 } else { [double]$values.rejected / [double]$values.offered }
    $throughput = [double]$values.completedWithinWindow * 1000.0 / [double]$DurationMs
    $qualifies = $null -ne $p99 -and $p99 -le 50000 -and $rejectionFraction -le 0.01
    return [ordered]@{
        backend = $Backend; policy = $Policy; rate = $Rate; payloadBytes = $PayloadBytes; delayMs = $DelayMs
        warmupMs = $WarmupMs; durationMs = $DurationMs; scratchSlots = if ($Backend -ceq 'native') { $ScratchSlots } else { $null }
        offered = $values.offered; admitted = $values.admitted; rejected = $values.rejected; rejectedFraction = $rejectionFraction
        completed = $values.completed; completedWithinWindow = $values.completedWithinWindow; warmupCompleted = $values.warmupCompleted
        throughputPerSecond = $throughput; p99LatencyMicroseconds = $p99; latencySampleCount = $latencies.Count
        errors = $values.errors; timedOut = $values.timedOut; pendingAtEnd = $values.pendingAtEnd; peakPending = $values.peakPending
        missedArrivals = $values.missedArrivals; maxDispatchLatenessMicroseconds = $values.maxDispatchLatenessMicroseconds
        verified = $verified; storageReservedBytes = $storageBytes; mailboxAttempted = $attemptedTotal; mailboxCompleted = $completedTotal
        qualifies = $qualifies; qualification = [ordered]@{ p99AtMost50ms = $null -ne $p99 -and $p99 -le 50000; rejectionAtMost1Percent = $rejectionFraction -le 0.01; correctness = $verified -and $values.errors -eq 0 -and $values.timedOut -eq 0 -and $values.pendingAtEnd -eq 0 }
    }
}

function Invoke-Trial([string]$Backend, [string]$Policy, [int]$ScratchSlots, [int]$Rate, [int]$DelayMs, [int]$WarmupMs, [int]$DurationMs, [long]$LimitBytes, [int]$Repeat, [string]$Phase) {
    $trialId = '{0}-{1}-{2}-{3}-d{4}-r{5}-s{6}' -f $Phase, $Backend, $Policy, $Rate, $DelayMs, $Repeat, $ScratchSlots
    $trialDirectory = Join-Path $runRoot "trials/$trialId"
    [IO.Directory]::CreateDirectory($trialDirectory) | Out-Null
    $appResultPath = Join-Path $trialDirectory 'application-result.json'
    $appStdout = Join-Path $trialDirectory 'application.stdout.txt'
    $appStderr = Join-Path $trialDirectory 'application.stderr.txt'
    $appRunnerResult = Join-Path $trialDirectory 'application-runner.json'
    $providerTimeout = $WarmupMs + $DurationMs + 45000
    $provider = $null
    $providerSummary = $null
    $summary = $null
    $appRunner = $null
    $trialRecord = [ordered]@{
        trialId = $trialId; phase = $Phase; backend = $Backend; policy = $Policy; scratchSlots = if ($Backend -ceq 'native') { $ScratchSlots } else { $null }
        rate = $Rate; delayMs = $DelayMs; payloadBytes = 4096; warmupMs = $WarmupMs; durationMs = $DurationMs
        repeat = $Repeat; applicationLimitBytes = $LimitBytes; application = $null; provider = $null; qualification = $null
        paths = [ordered]@{ directory = $trialDirectory; applicationResult = $appResultPath; applicationStdout = $appStdout; applicationStderr = $appStderr; applicationRunner = $appRunnerResult }
    }
    [IO.File]::WriteAllText((Join-Path $trialDirectory 'configuration.json'), (ConvertTo-Json -InputObject $trialRecord -Depth 30), $utf8NoBom)
    try {
        $provider = Start-Provider $trialDirectory $providerTimeout '268435456'
        $commonArgs = @('--port', [string]$provider.Port, '--rate', [string]$Rate, '--payload-bytes', '4096', '--delay-ms', [string]$DelayMs, '--warmup-ms', [string]$WarmupMs, '--duration-ms', [string]$DurationMs, '--output', $appResultPath)
        if ($Backend -ceq 'native') {
            $childArgs = @('--module', $script:modulePath, '--policy', $Policy, '--scratch-slots', [string]$ScratchSlots) + $commonArgs
            $application = $script:nativeExe
            $appWork = $trialDirectory
        } else {
            $childArgs = @($script:baselineAssembly) + $commonArgs
            $application = $dotnet
            $appWork = $trialDirectory
        }
        $appTimeout = $WarmupMs + $DurationMs + 30000
        $appRunnerArguments = New-RunnerArguments $LimitBytes $appTimeout $appStdout $appStderr $appRunnerResult $application $childArgs $appWork 5000 $script:appAffinityMask
        $appRunner = Start-RunnerProcess "app-$trialId" $appRunnerArguments $appWork ("app-$trialId")
        $appLauncher = Finish-RunnerProcess $appRunner ($appTimeout + 15000)
        if ($appLauncher.timedOut -or $appLauncher.exitCode -ne 0) { throw "Application launcher failed for $trialId (exit=$($appLauncher.exitCode), timeout=$($appLauncher.timedOut))." }
        $runnerMetadata = Read-JsonFile $appRunnerResult 'Application process measurement'
        if (-not (Require-Boolean (Get-ExactField $runnerMetadata 'capConfigured' 'application runner metadata') 'application.capConfigured') -or
            -not (Require-Boolean (Get-ExactField $runnerMetadata 'jobAssigned' 'application runner metadata') 'application.jobAssigned')) {
            throw 'Application was not assigned to a Job Object with its commit limit.'
        }
        if ((Get-ExactField $runnerMetadata 'memoryLimitScope' 'application runner metadata') -cne 'jobProcessCommitIncludingDescendants') { throw 'Application Job Object ceiling scope was not identified as process commit.' }
        if ((Require-Boolean (Get-ExactField $runnerMetadata 'timedOut' 'application runner metadata') 'application.timedOut') -or
            (Require-Boolean (Get-ExactField $runnerMetadata 'jobDrainTimedOut' 'application runner metadata') 'application.jobDrainTimedOut')) {
            throw "Application process timed out or failed to drain for $trialId."
        }
        $appExit = Require-Integer (Get-ExactField $runnerMetadata 'exitCode' 'application runner metadata') 'application.exitCode'
        if ($appExit -ne 0) { throw "Application exited with code $appExit for $trialId." }
    $appAffinity = Normalize-AffinityMask ([string](Get-ExactField $runnerMetadata 'affinityMask' 'application runner metadata')) 'application.affinityMask'
        if ([string]::IsNullOrWhiteSpace($appAffinity)) { throw 'Application affinity was not recorded.' }
        if ($appAffinity -cne $script:appAffinityMask) { throw "Application CPU affinity changed from the predeclared mask $script:appAffinityMask to $appAffinity." }
        if ((Normalize-AffinityMask ([string](Get-ExactField $runnerMetadata 'availableAffinityMask' 'application runner metadata')) 'application.availableAffinityMask') -cne $script:availableAffinityMask) { throw 'Application runner reported a changed available CPU mask.' }
        $applicationResult = Read-JsonFile $appResultPath 'Application benchmark result'
        $summary = Validate-AppResult $applicationResult $Backend $Policy $Rate 4096 $DelayMs $WarmupMs $DurationMs $ScratchSlots
        $expectedProviderFrames = [long]$summary.warmupCompleted + [long]$summary.admitted
        $providerSummary = Stop-Provider $provider $expectedProviderFrames
        $providerAffinity = Normalize-AffinityMask ([string](Get-ExactField $providerSummary.process 'affinityMask' 'provider runner metadata')) 'provider.affinityMask'
        if ($providerAffinity -cne $script:providerAffinityMask) { throw "Provider CPU affinity changed from the predeclared mask $script:providerAffinityMask to $providerAffinity." }
        $runnerPeakCommitted = Require-Integer (Get-ExactField $runnerMetadata 'peakJobCommittedBytes' 'application runner metadata') 'application.peakJobCommittedBytes'
        $runnerPeakWorkingSet = Require-Integer (Get-ExactField $runnerMetadata 'peakWorkingSetBytes' 'application runner metadata') 'application.peakWorkingSetBytes'
        $runnerCpu = Require-Integer (Get-ExactField $runnerMetadata 'cpuTime100ns' 'application runner metadata') 'application.cpuTime100ns'
        $enforced = Require-Integer (Get-ExactField $runnerMetadata 'enforcedJobMemoryLimitBytes' 'application runner metadata') 'application.enforcedJobMemoryLimitBytes'
        if ($enforced -ne $LimitBytes) { throw "Application Job Object ceiling mismatch: expected=$LimitBytes actual=$enforced." }
        $summary.process = [ordered]@{
            peakJobCommittedBytes = $runnerPeakCommitted; peakWorkingSetBytes = $runnerPeakWorkingSet
            peakWorkingSetExact = [bool](Get-ExactField $runnerMetadata 'finalWorkingSetQuerySucceeded' 'application runner metadata')
            peakWorkingSetIsSampledLowerBound = [bool](Get-ExactField $runnerMetadata 'peakWorkingSetIsSampledLowerBound' 'application runner metadata')
            cpuTime100ns = $runnerCpu; enforcedJobMemoryLimitBytes = $enforced
            capNonbinding = $runnerPeakCommitted -lt $enforced
            affinityMask = $appAffinity; selectedCpuIndex = Get-ExactField $runnerMetadata 'selectedCpuIndex' 'application runner metadata'
            runner = $runnerMetadata; executablePath = $application; executableSha256 = Get-Hash $application
            outputPath = $appResultPath; stdoutPath = $appStdout; stderrPath = $appStderr
        }
        $summary.provider = $providerSummary
        $summary.validComparison = $true
        $summary.qualifies = [bool]$summary.qualifies
        $trialRecord.application = $summary
        $trialRecord.provider = $providerSummary
        $trialRecord.qualification = $summary.qualification
        $trialRecord.validComparison = $true
        $script:trialResults.Add($trialRecord)
        [IO.File]::WriteAllText((Join-Path $trialDirectory 'trial-summary.json'), (ConvertTo-Json -InputObject $trialRecord -Depth 100), $utf8NoBom)
        Save-RunReport
        return $trialRecord
    } catch {
        if ($null -ne $provider -and -not $provider.Handle.Finalized) {
            try { [IO.File]::WriteAllText($provider.StopPath, 'stop', $utf8NoBom); $null = Finish-RunnerProcess $provider.Handle 12000 } catch { }
        }
        $trialRecord.validComparison = $false
        $trialRecord.failure = $_.Exception.ToString()
        $trialRecord.applicationPartial = if (Test-Path -LiteralPath $appResultPath -PathType Leaf) { Read-JsonFile $appResultPath 'Partial application benchmark result' } else { $null }
        if (Test-Path -LiteralPath $appRunnerResult -PathType Leaf) { $trialRecord.applicationRunnerPartial = Read-JsonFile $appRunnerResult 'Partial application process measurement' }
        $script:trialResults.Add($trialRecord)
        [IO.File]::WriteAllText((Join-Path $trialDirectory 'trial-summary.json'), (ConvertTo-Json -InputObject $trialRecord -Depth 100), $utf8NoBom)
        Save-RunReport
        throw
    }
}

function Test-TrialQualifies($Trial) {
    return [bool]$Trial.application.qualifies
}

function Get-RuntimeSettings {
    $names = @('DOTNET_gcServer', 'COMPlus_gcServer', 'DOTNET_GCHeapHardLimit', 'COMPlus_GCHeapHardLimit', 'DOTNET_GCHeapHardLimitPercent', 'COMPlus_GCHeapHardLimitPercent', 'DOTNET_GCConserveMemory', 'COMPlus_GCConserveMemory', 'DOTNET_TieredCompilation', 'COMPlus_TieredCompilation', 'DOTNET_ReadyToRun', 'COMPlus_ReadyToRun')
    $settings = [ordered]@{}
    foreach ($name in $names) { $settings[$name] = [Environment]::GetEnvironmentVariable($name) }
    return [ordered]@{ dotnetPath = $dotnet; dotnetVersion = (Invoke-LoggedProcess 'dotnet-version' $dotnet @('--version') $repo 30000).stdout.Trim(); gcEnvironment = $settings; jit = 'Release build, normal .NET JIT; no artificial GC behavior overrides were introduced by this script' }
}

function Get-ScoreGroups($Trials, [string]$Phase = 'grid') {
    $groups = [Collections.Generic.List[object]]::new()
    $keys = @($Trials | Where-Object { $_.validComparison -and $_.phase -ceq $Phase } |
        ForEach-Object { '{0}|{1}|{2}|{3}' -f $_.backend, $_.policy, $_.scratchSlots, $_.delayMs } | Sort-Object -Unique)
    foreach ($key in $keys) {
        $parts = $key -split '\|'
        $rows = @($Trials | Where-Object { $_.phase -ceq $Phase -and $_.backend -ceq $parts[0] -and $_.policy -ceq $parts[1] -and [string]$_.scratchSlots -ceq $parts[2] -and $_.delayMs -eq [int]$parts[3] })
        $rates = @($rows | Group-Object { [int]$_.rate } | Sort-Object { [int]$_.Name })
        foreach ($rateGroup in $rates) {
            $runs = @($rateGroup.Group)
            $qualified = $runs.Count -eq 3 -and @($runs | Where-Object { -not (Test-TrialQualifies $_) }).Count -eq 0
            $groups.Add([ordered]@{
                backend = $parts[0]; policy = $parts[1]; scratchSlots = if ($parts[2] -eq '') { $null } else { [int]$parts[2] }
                delayMs = [int]$parts[3]; rate = [int]$rateGroup.Name; repeatCount = $runs.Count
                allRepeatsQualify = $qualified; trialIds = @($runs | ForEach-Object { $_.trialId })
                throughputPerSecond = @($runs | ForEach-Object { $_.application.throughputPerSecond })
                p99LatencyMicroseconds = @($runs | ForEach-Object { $_.application.p99LatencyMicroseconds })
                rejectedFraction = @($runs | ForEach-Object { $_.application.rejectedFraction })
                processMemory = @($runs | ForEach-Object { $_.application.process })
            })
        }
    }
    return @($groups)
}

function Get-HighestQualifyingTestedRate($Groups, [string]$Backend, [string]$Policy, $ScratchSlots, [int]$DelayMs) {
    $matches = @($Groups | Where-Object { $_.backend -ceq $Backend -and $_.policy -ceq $Policy -and $_.scratchSlots -eq $ScratchSlots -and $_.delayMs -eq $DelayMs -and $_.allRepeatsQualify } | Sort-Object { [int]$_.rate })
    if ($matches.Count -eq 0) { return $null }
    return [int]$matches[-1].rate
}

function Test-ScoreAggregationPreflight {
    $fixtureTrials = [Collections.Generic.List[object]]::new()
    $variants = @(
        [pscustomobject]@{ backend = 'fsharp'; policy = 'gc'; scratchSlots = $null },
        [pscustomobject]@{ backend = 'native'; policy = 'return'; scratchSlots = 4 },
        [pscustomobject]@{ backend = 'native'; policy = 'keep'; scratchSlots = 4 }
    )
    foreach ($variant in $variants) {
        foreach ($delay in @(0, 5)) {
            foreach ($rate in @(250, 500, 1000, 2000, 4000, 8000)) {
                if ($variant.backend -ceq 'fsharp') {
                    $qualifies = if ($delay -eq 0) { $rate -le 1000 } else { $rate -le 500 }
                } elseif ($variant.policy -ceq 'return') {
                    $qualifies = if ($delay -eq 0) { $true } else { $rate -le 500 }
                } else {
                    $qualifies = if ($delay -eq 0) { $rate -le 500 } else { $rate -le 250 }
                }
                for ($repeat = 1; $repeat -le 3; $repeat++) {
                    $fixtureTrials.Add([ordered]@{
                        trialId = "preflight-$($variant.backend)-$($variant.policy)-$delay-$rate-$repeat"
                        phase = 'grid'; validComparison = $true; backend = $variant.backend; policy = $variant.policy
                        scratchSlots = $variant.scratchSlots; delayMs = $delay; rate = $rate
                        application = [ordered]@{
                            qualifies = [bool]$qualifies; throughputPerSecond = [double]$rate
                            p99LatencyMicroseconds = 1000; rejectedFraction = 0.0; process = [ordered]@{}
                        }
                    })
                }
            }
        }
    }

    $groups = @(Get-ScoreGroups $fixtureTrials)
    if ($fixtureTrials.Count -ne 108 -or $groups.Count -ne 36 -or @($groups | Where-Object { $_.repeatCount -ne 3 -or @($_.trialIds | ForEach-Object { [int]($_ -split '-')[-1] } | Sort-Object -Unique) -join ',' -cne '1,2,3' }).Count -ne 0) {
        throw "Scoring preflight grouped ordered-dictionary trials incorrectly (groups=$($groups.Count))."
    }
    $fixtureRates = @($groups | Where-Object { $_.backend -ceq 'native' -and $_.policy -ceq 'return' -and $_.scratchSlots -eq 4 -and $_.delayMs -eq 0 } | Sort-Object { [int]$_.rate } | ForEach-Object { [int]$_.rate })
    if (($fixtureRates -join ',') -cne '250,500,1000,2000,4000,8000' -or
        @($groups | Where-Object { $_.backend -ceq 'fsharp' -and $null -ne $_.scratchSlots }).Count -ne 0 -or
        @($groups | Where-Object { $_.backend -ceq 'native' -and $_.scratchSlots -ne 4 }).Count -ne 0) {
        throw 'Scoring preflight did not preserve numeric rate order or the F# null/native four-slot axis.'
    }
    $oneHighTrial = @($fixtureTrials | Where-Object { $_.backend -ceq 'native' -and $_.policy -ceq 'return' -and $_.delayMs -eq 0 -and $_.rate -eq 8000 })[0]
    $oneHighTrial.application.qualifies = $false
    $oneFailureGroups = @(Get-ScoreGroups $fixtureTrials)
    $fallbackAfterOneFailure = Get-HighestQualifyingTestedRate $oneFailureGroups 'native' 'return' 4 0
    $oneHighTrial.application.qualifies = $true
    if ($fallbackAfterOneFailure -ne 4000) { throw "Scoring preflight failed to require all three repeats at the high-rate point (fallback=$fallbackAfterOneFailure)." }
    $groups = @(Get-ScoreGroups $fixtureTrials)
    $checks = @(
        [pscustomobject]@{ backend = 'native'; policy = 'return'; scratchSlots = 4; delayMs = 0; expectedRate = 8000; expectedNextRate = 16000 },
        [pscustomobject]@{ backend = 'fsharp'; policy = 'gc'; scratchSlots = $null; delayMs = 0; expectedRate = 1000; expectedNextRate = 2000 },
        [pscustomobject]@{ backend = 'fsharp'; policy = 'gc'; scratchSlots = $null; delayMs = 5; expectedRate = 500; expectedNextRate = 1000 }
    )
    foreach ($check in $checks) {
        $actual = Get-HighestQualifyingTestedRate $groups $check.backend $check.policy $check.scratchSlots $check.delayMs
        if ($actual -ne $check.expectedRate) {
            throw "Scoring preflight selected an incorrect rate for $($check.backend)/$($check.policy)/delay=$($check.delayMs): expected=$($check.expectedRate), actual=$actual."
        }
        if ((Get-NextGridRate $actual) -ne $check.expectedNextRate) {
            throw "Scoring preflight selected an incorrect next rate for $($check.backend)/$($check.policy)/delay=$($check.delayMs)."
        }
    }
    return [ordered]@{
        passed = $true; fixtureTrialCount = $fixtureTrials.Count; groupCount = $groups.Count; repeatsPerGroup = 3
        singleFailedRepeatFallbackRate = $fallbackAfterOneFailure
        highestQualifyingTestedRates = @($checks | ForEach-Object { [ordered]@{ backend = $_.backend; policy = $_.policy; scratchSlots = $_.scratchSlots; delayMs = $_.delayMs; rate = $_.expectedRate; nextRate = $_.expectedNextRate } })
        inputRepresentation = 'live System.Collections.Specialized.OrderedDictionary trial records'
    }
}

function Get-NextGridRate([int]$Rate) {
    $grid = @(250, 500, 1000, 2000, 4000, 8000, 16000, 32000, 64000)
    foreach ($candidate in $grid) { if ($candidate -gt $Rate) { return $candidate } }
    return $null
}

function Run-ScoredTrials {
    $repeats = 3
    $warmup = 1000
    $duration = 3000
    $rateGrid = [Collections.Generic.List[int]]::new()
    foreach ($rate in @(250, 500, 1000, 2000, 4000, 8000)) { $rateGrid.Add($rate) }
    $variants = @(
        [pscustomobject]@{ backend = 'fsharp'; policy = 'gc'; scratchSlots = 0 },
        [pscustomobject]@{ backend = 'native'; policy = 'return'; scratchSlots = 4 },
        [pscustomobject]@{ backend = 'native'; policy = 'keep'; scratchSlots = 4 }
    )
    foreach ($delay in @(0, 5)) {
        foreach ($rate in $rateGrid) {
            for ($repeat = 0; $repeat -lt $repeats; $repeat++) {
                $order = switch ($repeat % 3) {
                    0 { @($variants[0], $variants[1], $variants[2]) }
                    1 { @($variants[1], $variants[2], $variants[0]) }
                    default { @($variants[2], $variants[0], $variants[1]) }
                }
                foreach ($variant in $order) {
                    $slots = if ($variant.backend -ceq 'native') { [int]$variant.scratchSlots } else { 0 }
                    $null = Invoke-Trial $variant.backend $variant.policy $slots $rate $delay $warmup $duration 268435456 ($repeat + 1) 'grid'
                }
            }
        }
    }

    $firstGrid = @($script:trialResults | Where-Object { $_.phase -ceq 'grid' })
    $highestRate = 8000
    $gridGroups = @(Get-ScoreGroups $firstGrid)
    $highestRows = @($gridGroups | Where-Object { $_.rate -eq $highestRate })
    $mayExtend = $highestRows.Count -eq 6 -and @($highestRows | Where-Object { $_.allRepeatsQualify }).Count -gt 0
    while ($mayExtend -and $highestRate -lt 64000) {
        $next = [Math]::Min(64000, $highestRate * 2)
        foreach ($delay in @(0, 5)) {
            for ($repeat = 0; $repeat -lt $repeats; $repeat++) {
                $order = switch ($repeat % 3) {
                    0 { @($variants[0], $variants[1], $variants[2]) }
                    1 { @($variants[1], $variants[2], $variants[0]) }
                    default { @($variants[2], $variants[0], $variants[1]) }
                }
                foreach ($variant in $order) {
                    $slots = if ($variant.backend -ceq 'native') { [int]$variant.scratchSlots } else { 0 }
                    $null = Invoke-Trial $variant.backend $variant.policy $slots $next $delay $warmup $duration 268435456 ($repeat + 1) 'grid'
                }
            }
        }
        $highestRate = $next
        $gridGroups = @(Get-ScoreGroups @($script:trialResults))
        $highestRows = @($gridGroups | Where-Object { $_.rate -eq $highestRate })
        $mayExtend = $highestRate -lt 64000 -and $highestRows.Count -eq 6 -and @($highestRows | Where-Object { $_.allRepeatsQualify }).Count -gt 0
    }

    $gridGroups = @(Get-ScoreGroups @($script:trialResults))
    $scoreTargets = @(
        [pscustomobject]@{ backend = 'fsharp'; policy = 'gc'; scratchSlots = $null },
        [pscustomobject]@{ backend = 'native'; policy = 'return'; scratchSlots = 4 },
        [pscustomobject]@{ backend = 'native'; policy = 'keep'; scratchSlots = 4 }
    )
    foreach ($backend in $scoreTargets) {
        foreach ($delay in @(0, 5)) {
            $highestQualifyingRate = Get-HighestQualifyingTestedRate $gridGroups $backend.backend $backend.policy $backend.scratchSlots $delay
            $chosenRate = if ($null -ne $highestQualifyingRate) { [int]$highestQualifyingRate } else { 250 }
            if ($backend.backend -ceq 'native') {
                for ($repeat = 0; $repeat -lt 3; $repeat++) {
                    $null = Invoke-Trial 'native' $backend.policy 4 $chosenRate $delay $warmup $duration 335544320 ($repeat + 1) 'native-plus25'
                }
            }
            for ($repeat = 0; $repeat -lt 1; $repeat++) {
                $slots = if ($backend.backend -ceq 'native') { [int]$backend.scratchSlots } else { 0 }
                $null = Invoke-Trial $backend.backend $backend.policy $slots $chosenRate $delay $warmup 30000 268435456 ($repeat + 1) 'confirmation-30s'
            }
        }
    }

    $groups = @(Get-ScoreGroups @($script:trialResults))
    $confirmationTrials = @($script:trialResults | Where-Object { $_.phase -ceq 'confirmation-30s' })
    $confirmationOkay = $confirmationTrials.Count -eq 6 -and @($confirmationTrials | Where-Object { -not (Test-TrialQualifies $_) }).Count -eq 0
    $capacityRatesByDelay = [ordered]@{}
    foreach ($delay in @(0, 5)) {
        $delayRates = [Collections.Generic.HashSet[int]]::new()
        foreach ($policy in @('return', 'keep')) {
            $selected = Get-HighestQualifyingTestedRate $groups 'native' $policy 4 $delay
            if ($null -ne $selected) {
                $selected = [int]$selected
                $delayRates.Add($selected) | Out-Null
                $next = Get-NextGridRate $selected
                if ($null -ne $next) { $delayRates.Add([int]$next) | Out-Null }
            } else {
                $delayRates.Add(250) | Out-Null
                $delayRates.Add(500) | Out-Null
            }
        }
        $capacityRatesByDelay[[string]$delay] = @($delayRates | Sort-Object)
    }
    $capacityExtensions = [Collections.Generic.List[object]]::new()
    foreach ($delay in @(0, 5)) {
        $scheduledRates = [Collections.Generic.List[int]]::new()
        foreach ($rate in $capacityRatesByDelay[[string]$delay]) {
            if (-not $scheduledRates.Contains([int]$rate)) { $scheduledRates.Add([int]$rate) }
        }
        foreach ($rate in $scheduledRates) {
            for ($repeat = 0; $repeat -lt 3; $repeat++) {
                foreach ($policy in @('return', 'keep')) {
                    $null = Invoke-Trial 'native' $policy 16 $rate $delay $warmup $duration 268435456 ($repeat + 1) 'capacity-16-slot'
                }
            }
        }
        $capacityGroups = @(Get-ScoreGroups @($script:trialResults) 'capacity-16-slot')
        $capacityHighest = ($scheduledRates | Measure-Object -Maximum).Maximum
        while ($capacityHighest -lt 64000) {
            $atHighest = @($capacityGroups | Where-Object { $_.delayMs -eq $delay -and $_.rate -eq $capacityHighest -and $_.allRepeatsQualify })
            if ($atHighest.Count -eq 0) { break }
            $nextRate = [Math]::Min(64000, [int]$capacityHighest * 2)
            $fsharpReference = @($script:trialResults | Where-Object { $_.phase -in @('grid', 'capacity-fsharp-reference') -and $_.backend -ceq 'fsharp' -and $_.delayMs -eq $delay -and $_.rate -eq $nextRate })
            if ($fsharpReference.Count -ne 3) {
                for ($repeat = 0; $repeat -lt 3; $repeat++) {
                    $null = Invoke-Trial 'fsharp' 'gc' 0 $nextRate $delay $warmup $duration 268435456 ($repeat + 1) 'capacity-fsharp-reference'
                }
            }
            for ($repeat = 0; $repeat -lt 3; $repeat++) {
                foreach ($policy in @('return', 'keep')) {
                    $null = Invoke-Trial 'native' $policy 16 $nextRate $delay $warmup $duration 268435456 ($repeat + 1) 'capacity-16-slot'
                }
            }
            $capacityExtensions.Add([ordered]@{ delayMs = $delay; fromRate = $capacityHighest; rate = $nextRate; fsharpReferenceReused = ($fsharpReference.Count -eq 3) })
            $capacityHighest = $nextRate
            $capacityGroups = @(Get-ScoreGroups @($script:trialResults) 'capacity-16-slot')
        }
    }
    $script:report.score = [ordered]@{
        grid = @(Get-ScoreGroups @($script:trialResults))
        testedRateGrid = @($script:trialResults | Where-Object { $_.phase -ceq 'grid' } | ForEach-Object { [int]$_.rate } | Sort-Object { [int]$_ } -Unique)
        highestQualifyingTestedRates = @(
            foreach ($backend in $scoreTargets) {
                foreach ($delay in @(0, 5)) {
                    $highestQualifyingRate = Get-HighestQualifyingTestedRate $gridGroups $backend.backend $backend.policy $backend.scratchSlots $delay
                    [ordered]@{ backend = $backend.backend; policy = $backend.policy; scratchSlots = $backend.scratchSlots; delayMs = $delay; highestQualifyingTestedRate = $highestQualifyingRate }
                }
            }
        )
        ceilingFollowups = @($script:trialResults | Where-Object { $_.phase -ceq 'native-plus25' })
        confirmationRuns = $confirmationTrials
        sustainedClaimAllowed = $confirmationOkay
        sustainedClaimStatus = if ($confirmationOkay) { '30-second confirmation passed for every selected backend and delay point.' } else { 'Short grid results are exploratory because at least one selected 30-second confirmation did not qualify.' }
        capacityFollowups = [ordered]@{ trials = @($script:trialResults | Where-Object { $_.phase -in @('capacity-16-slot', 'capacity-fsharp-reference') }); extensions = @($capacityExtensions) }
        maximumRateSearch = 'The equal-limit grid doubles beyond 8000/s while at least one variant and provider delay at the current highest rate qualifies; the 16-slot native follow-up extends while at least one policy qualifies at its current highest tested rate. Both searches stop at 64000/s.'
    }
}

try {
    [IO.Directory]::CreateDirectory($runRoot) | Out-Null
    [IO.Directory]::CreateDirectory($tempRoot) | Out-Null
    $script:report.scoringPreflight = Test-ScoreAggregationPreflight
    Save-RunReport
    $artifactRoot = Join-Path $runRoot 'artifacts'
    $moduleDirectory = Join-Path $runRoot 'module-trusted-generated-O2'
    $nativeOutputDirectory = Join-Path $runRoot 'native'
    [IO.Directory]::CreateDirectory($artifactRoot) | Out-Null
    [IO.Directory]::CreateDirectory($moduleDirectory) | Out-Null
    [IO.Directory]::CreateDirectory($nativeOutputDirectory) | Out-Null

    foreach ($required in @($bootstrapProject, $bootstrapProgram, $flowPath, $providerProject, $providerProgram, $baselineProject, $nativeHost) + $nativeSources + $nativeHeaders + @((Join-Path $experiment 'limit_runner.c'))) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required fresh-build input is missing: $required" }
    }
    $script:sourcePaths = Get-SourceInputs
    $script:sourceHashesBefore = Get-HashList $script:sourcePaths

    $script:report.runtime = Get-RuntimeSettings
    $script:buildIdentity = [ordered]@{
        dotnetPath = $dotnet; dotnetSha256 = Get-Hash $dotnet; dotnetVersion = $script:report.runtime.dotnetVersion
        clangPath = $clang; clangSha256 = Get-Hash $clang
        compilerEnvironment = [ordered]@{ AGENTLANG_LLVM_CLANG = [Environment]::GetEnvironmentVariable('AGENTLANG_LLVM_CLANG') }
        runtimeGcEnvironment = $script:report.runtime.gcEnvironment
    }

    $runnerPath = Join-Path $runRoot 'limit_runner.exe'
    $runnerArgs = @('--target=x86_64-pc-windows-msvc', '-std=c11', '-Wall', '-Wextra', '-Werror', '-O2', (Join-Path $experiment 'limit_runner.c'), '-o', $runnerPath)
    $null = Invoke-LoggedProcess 'build-limit-runner' $clang $runnerArgs $repo
    $script:runnerExe = $runnerPath
    $script:buildIdentity.limitRunner = [ordered]@{ path = $runnerPath; sha256 = Get-Hash $runnerPath; command = $runnerArgs }

    $bootstrapArtifacts = Join-Path $artifactRoot 'owning-mailbox-bootstrap'
    $bootstrapBuildArgs = @('build', $bootstrapProject, '--artifacts-path', $bootstrapArtifacts, '--configuration', 'Release', '--verbosity', 'minimal', '-p:NuGetAudit=false', '-p:BuildInParallel=false', '-m:1')
    $null = Invoke-LoggedProcess 'build-owning-mailbox-bootstrap-release' $dotnet $bootstrapBuildArgs $repo
    $bootstrapAssembly = Get-BuiltAssembly $bootstrapArtifacts 'AgentLang.OwningMailbox.dll'
    $bootstrapArgs = @($bootstrapAssembly, 'O2', $moduleDirectory, $flowPath, '--runtime-profile', 'trusted-generated')
    $bootstrapProcess = Invoke-LoggedProcess 'compile-mailbox-flow-O2-trusted-generated' $dotnet $bootstrapArgs $repo
    $bootstrap = ConvertFrom-Json -InputObject $bootstrapProcess.stdout.Trim() -AsHashtable -Depth 100 -ErrorAction Stop
    if ((Get-ExactField $bootstrap 'optimization' 'mailbox compiler output') -cne 'O2' -or (Get-ExactField $bootstrap 'runtimeProfile' 'mailbox compiler output') -cne 'trusted-generated' -or -not (Require-Boolean (Get-ExactField $bootstrap 'sameVerifiedProgramInstance' 'mailbox compiler output') 'sameVerifiedProgramInstance')) {
        throw 'Fresh mailbox module did not report O2 trusted-generated from one verified program instance.'
    }
    $script:modulePath = [IO.Path]::GetFullPath([string](Get-ExactField $bootstrap 'modulePath' 'mailbox compiler output'))
    if (-not (Test-Path -LiteralPath $script:modulePath -PathType Leaf)) { throw "Fresh native module is missing: $script:modulePath" }
    $script:buildIdentity.mailboxModule = [ordered]@{ path = $script:modulePath; sha256 = Get-Hash $script:modulePath; manifestPath = [string](Get-ExactField $bootstrap 'manifestPath' 'mailbox compiler output'); bootstrap = $bootstrap }

    $providerArtifacts = Join-Path $artifactRoot 'real-io-provider'
    $providerBuildArgs = @('build', $providerProject, '--artifacts-path', $providerArtifacts, '--configuration', 'Release', '--verbosity', 'minimal', '-p:NuGetAudit=false', '-p:BuildInParallel=false', '-m:1')
    $null = Invoke-LoggedProcess 'build-provider-release' $dotnet $providerBuildArgs $repo
    $script:providerAssembly = Get-BuiltAssembly $providerArtifacts 'AgentLang.RealIoMailbox.Provider.dll'
    $script:buildIdentity.provider = [ordered]@{ path = $script:providerAssembly; sha256 = Get-Hash $script:providerAssembly; project = $providerProject }

    $baselineArtifacts = Join-Path $artifactRoot 'fsharp-baseline'
    $baselineBuildArgs = @('build', $baselineProject, '--artifacts-path', $baselineArtifacts, '--configuration', 'Release', '--verbosity', 'minimal', '-p:NuGetAudit=false', '-p:BuildInParallel=false', '-m:1')
    $null = Invoke-LoggedProcess 'build-fsharp-baseline-release' $dotnet $baselineBuildArgs $repo
    $script:baselineAssembly = Get-BuiltAssembly $baselineArtifacts 'AgentLang.MailboxLoad.Baseline.dll'
    $script:buildIdentity.fsharpBaseline = [ordered]@{ path = $script:baselineAssembly; sha256 = Get-Hash $script:baselineAssembly; project = $baselineProject; configuration = 'Release JIT' }

    $script:nativeExe = Join-Path $nativeOutputDirectory 'load-host.exe'
    $nativeCompileArgs = @('--target=x86_64-pc-windows-msvc', '-std=c11', '-Wall', '-Wextra', '-Werror', '-O2', '-DAL_OWNING_TRUSTED_GENERATED=1', '-I', $nativeDirectory) + $nativeSources + @('-lws2_32', '-o', $script:nativeExe)
    $null = Invoke-LoggedProcess 'build-native-load-host-O2-trusted-generated' $clang $nativeCompileArgs $repo
    $script:buildIdentity.native = [ordered]@{ path = $script:nativeExe; sha256 = Get-Hash $script:nativeExe; command = $nativeCompileArgs; runtimeProfile = 'trusted-generated'; optimization = 'O2' }
    Invoke-LauncherControls
    Save-RunReport

    $smokeCases = @(@{ rate = 250; delay = 0 }, @{ rate = 500; delay = 5 }, @{ rate = 64000; delay = 5 })
    $smokeVariants = @(
        [pscustomobject]@{ backend = 'fsharp'; policy = 'gc'; slots = 0 },
        [pscustomobject]@{ backend = 'native'; policy = 'return'; slots = 4 },
        [pscustomobject]@{ backend = 'native'; policy = 'keep'; slots = 4 }
    )
    foreach ($case in $smokeCases) {
        foreach ($variant in $smokeVariants) {
            $null = Invoke-Trial $variant.backend $variant.policy $variant.slots $case.rate $case.delay 100 250 268435456 1 'smoke'
        }
        if ($case.rate -eq 64000) {
            foreach ($policy in @('return', 'keep')) {
                $null = Invoke-Trial 'native' $policy 16 $case.rate $case.delay 100 250 268435456 1 'smoke-capacity-16-slot'
            }
        }
    }
    $script:report.smoke = [ordered]@{ trialCount = @($script:trialResults | Where-Object { $_.phase -like 'smoke*' }).Count; overloadRate = 64000; native16SlotPolicies = @('return', 'keep'); status = 'all smoke cases passed independent correctness and accounting checks' }
    Save-RunReport

    if ($Smoke) {
        $script:report.score = [ordered]@{ smokeCases = @($script:trialResults | Where-Object { $_.phase -like 'smoke*' } | ForEach-Object { $_.application }); scored = $false; reason = 'Smoke validation is not a scored result.' }
    } else {
        Run-ScoredTrials
    }
} catch {
    $script:report.error = $_.Exception.ToString()
} finally {
    try {
        $currentSourcePaths = Get-SourceInputs
        if ($currentSourcePaths.Count -gt 0) { $script:sourceHashesAfter = Get-HashList $currentSourcePaths }
        $stable = $script:sourceHashesBefore.Count -eq $script:sourceHashesAfter.Count
        if ($stable) {
            for ($index = 0; $index -lt $script:sourceHashesBefore.Count; $index++) {
                if ($script:sourceHashesBefore[$index].path -cne $script:sourceHashesAfter[$index].path) { $stable = $false; break }
            }
        }
        if ($stable) {
            for ($index = 0; $index -lt $script:sourceHashesBefore.Count; $index++) {
                if ($script:sourceHashesBefore[$index].path -cne $script:sourceHashesAfter[$index].path -or $script:sourceHashesBefore[$index].sha256 -cne $script:sourceHashesAfter[$index].sha256) { $stable = $false; break }
            }
        }
        $script:report.sourceStable = $stable
        if (-not $stable -and $null -eq $script:report.error) { $script:report.error = 'Source inputs changed during the build or measurement run.' }
        Save-RunReport
    } catch {
        $script:report.error = @($script:report.error, "Evidence finalization failed: $($_.Exception.ToString())" | Where-Object { $_ }) -join "`n"
    }
}

$reportPath = Join-Path $runRoot 'run-report.json'
Write-Output "Mailbox load report: $reportPath"
if ($null -ne $script:report.error) {
    Write-Error $script:report.error
    exit 1
}
