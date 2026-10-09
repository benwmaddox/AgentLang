#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CliDll,
    [string]$EvidencePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$startScript = Join-Path $PSScriptRoot 'Start-SubagentTrialHostV2.ps1'
$auditScript = Join-Path $PSScriptRoot 'Audit-SubagentTrialTerminationV2.ps1'
if ([IO.Path]::IsPathRooted($CliDll)) { $CliDll = [IO.Path]::GetFullPath($CliDll) }
else { $CliDll = [IO.Path]::GetFullPath((Join-Path $repoRoot $CliDll)) }
if (-not (Test-Path -LiteralPath $CliDll -PathType Leaf)) { throw "CLI DLL not found: $CliDll" }

$runId = [guid]::NewGuid().ToString('N')
$artifactRoot = Join-Path $repoRoot ".agentlang/subagent-host-v2-verification-$runId"
$traceRoot = Join-Path $repoRoot '.agentlang/reports'
[void](New-Item -ItemType Directory -Path $artifactRoot)
if (-not (Test-Path -LiteralPath $traceRoot)) { [void](New-Item -ItemType Directory -Path $traceRoot) }
if ([string]::IsNullOrWhiteSpace($EvidencePath)) {
    $EvidencePath = Join-Path $traceRoot "subagent-trial-host-v2-$runId.json"
} elseif ([IO.Path]::IsPathRooted($EvidencePath)) {
    $EvidencePath = [IO.Path]::GetFullPath($EvidencePath)
} else {
    $EvidencePath = [IO.Path]::GetFullPath((Join-Path $repoRoot $EvidencePath))
}
if (Test-Path -LiteralPath $EvidencePath) {
    $directory = Split-Path -Parent $EvidencePath
    $stem = [IO.Path]::GetFileNameWithoutExtension($EvidencePath)
    $extension = [IO.Path]::GetExtension($EvidencePath)
    $attempt = 1
    do {
        $candidate = Join-Path $directory ("{0}-retry-{1:D2}{2}" -f $stem, $attempt, $extension)
        $attempt++
    } while (Test-Path -LiteralPath $candidate)
    $EvidencePath = $candidate
}

$pwsh = Join-Path $PSHOME 'pwsh.exe'
if (-not (Test-Path -LiteralPath $pwsh -PathType Leaf)) { throw "PowerShell Core executable not found: $pwsh" }
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$checks = [Collections.Generic.List[object]]::new()
$runs = [Collections.Generic.List[object]]::new()
$auditResults = [Collections.Generic.List[object]]::new()
$version = $null
$dirty = $null
$runtimeFiles = @()
$wrapperFiles = @()
$fakeDll = $null

$boundedProcessSource = @'
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace AgentLang.SubagentTrialHostV2Verification
{
    public sealed class ProcessResult
    {
        public int? ExitCode;
        public bool TimedOut;
        public bool OutputLimitExceeded;
        public bool CleanupTimedOut;
        public string TaskError;
        public byte[] StdoutBytes;
        public byte[] StderrBytes;
        public double DurationMilliseconds;
    }

    internal sealed class OutputCollector
    {
        private readonly object _gate = new object();
        private readonly MemoryStream _stdout = new MemoryStream();
        private readonly MemoryStream _stderr = new MemoryStream();
        private readonly int _limit;
        private int _total;
        private bool _exceeded;

        public OutputCollector(int limit) { _limit = limit; }
        public bool Exceeded { get { lock (_gate) return _exceeded; } }
        public byte[] Stdout { get { lock (_gate) return _stdout.ToArray(); } }
        public byte[] Stderr { get { lock (_gate) return _stderr.ToArray(); } }

        public bool Append(bool stdout, byte[] buffer, int count)
        {
            lock (_gate)
            {
                int keep = Math.Min(count, Math.Max(0, _limit - _total));
                if (keep > 0)
                {
                    (stdout ? _stdout : _stderr).Write(buffer, 0, keep);
                    _total += keep;
                }
                if (keep != count) _exceeded = true;
                return _exceeded;
            }
        }
    }

    public static class BoundedProcessRunner
    {
        public static ProcessResult Run(string fileName, string workingDirectory, string[] arguments,
            byte[] input, int timeoutMilliseconds, int outputLimitBytes)
        {
            var startInfo = new ProcessStartInfo {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var collector = new OutputCollector(outputLimitBytes);
            var result = new ProcessResult();
            var stopwatch = Stopwatch.StartNew();
            if (!process.Start()) throw new InvalidOperationException("Could not start verifier subprocess.");

            Task deadline = Task.Delay(timeoutMilliseconds);
            Task stdoutTask = Task.Run(() => DrainAsync(process.StandardOutput.BaseStream, collector, true, process));
            Task stderrTask = Task.Run(() => DrainAsync(process.StandardError.BaseStream, collector, false, process));
            Task inputTask = Task.Run(() => WriteAndCloseAsync(process, input ?? Array.Empty<byte>()));
            Task waitTask = process.WaitForExitAsync();
            Task all = Task.WhenAll(stdoutTask, stderrTask, inputTask, waitTask);
            try
            {
                Task winner = Task.WhenAny(all, deadline).GetAwaiter().GetResult();
                result.TimedOut = winner != all;
                result.OutputLimitExceeded = collector.Exceeded;
                if (result.TimedOut || result.OutputLimitExceeded)
                {
                    TryKill(process);
                    if (Task.WhenAny(all, Task.Delay(3000)).GetAwaiter().GetResult() == all)
                    {
                        try { all.GetAwaiter().GetResult(); } catch { }
                    }
                    else result.CleanupTimedOut = true;
                }
                else
                {
                    try { all.GetAwaiter().GetResult(); }
                    catch (Exception ex) { result.TaskError = ex.GetBaseException().GetType().Name + ": " + ex.GetBaseException().Message; }
                }
                result.OutputLimitExceeded = result.OutputLimitExceeded || collector.Exceeded;
                if (process.HasExited) result.ExitCode = process.ExitCode;
                result.StdoutBytes = collector.Stdout;
                result.StderrBytes = collector.Stderr;
                stopwatch.Stop();
                result.DurationMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                return result;
            }
            finally
            {
                if (!process.HasExited)
                {
                    TryKill(process);
                    try { process.WaitForExit(2000); } catch { }
                }
                try { process.StandardInput.Close(); } catch { }
                process.Dispose();
            }
        }

        private static async Task WriteAndCloseAsync(Process process, byte[] input)
        {
            try
            {
                if (input.Length > 0) await process.StandardInput.BaseStream.WriteAsync(input, 0, input.Length).ConfigureAwait(false);
                await process.StandardInput.BaseStream.FlushAsync().ConfigureAwait(false);
                process.StandardInput.Close();
            }
            catch
            {
                try { process.StandardInput.Close(); } catch { }
                throw;
            }
        }

        private static async Task DrainAsync(Stream stream, OutputCollector collector, bool stdout, Process process)
        {
            var buffer = new byte[4096];
            while (true)
            {
                int count = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (count == 0) return;
                if (collector.Append(stdout, buffer, count)) TryKill(process);
            }
        }

        private static void TryKill(Process process)
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch { try { if (!process.HasExited) process.Kill(); } catch { } }
        }
    }
}
'@
Add-Type -TypeDefinition $boundedProcessSource -Language CSharp -ErrorAction Stop

function Add-Check([string]$Name, [bool]$Passed, [string]$Detail) {
    $checks.Add([ordered]@{ name = $Name; passed = $Passed; detail = $Detail })
}

function Assert-Check([string]$Name, [bool]$Passed, [string]$Detail) {
    Add-Check -Name $Name -Passed $Passed -Detail $Detail
    if (-not $Passed) { throw "Verification failed: $Name — $Detail" }
}

function Invoke-BoundedProcess {
    param(
        [Parameter(Mandatory)][string]$FileName,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][AllowEmptyCollection()][byte[]]$InputBytes,
        [Parameter(Mandatory)][int]$TimeoutMilliseconds,
        [int]$OutputLimitBytes = 1048576
    )
    $result = [AgentLang.SubagentTrialHostV2Verification.BoundedProcessRunner]::Run(
        $FileName, $repoRoot, $Arguments, $InputBytes, $TimeoutMilliseconds, $OutputLimitBytes)
    $stdout = $utf8.GetString($result.StdoutBytes)
    $stderr = $utf8.GetString($result.StderrBytes)
    return [pscustomobject][ordered]@{
        exitCode = $result.ExitCode
        timedOut = $result.TimedOut
        outputLimitExceeded = $result.OutputLimitExceeded
        cleanupTimedOut = $result.CleanupTimedOut
        taskError = $result.TaskError
        durationMilliseconds = [math]::Round($result.DurationMilliseconds, 3)
        stdout = $stdout
        stderr = $stderr
        stdoutBytes = $result.StdoutBytes
        stderrBytes = $result.StderrBytes
        stdoutUtf8Bytes = $result.StdoutBytes.Length
        stderrUtf8Bytes = $result.StderrBytes.Length
    }
}

function Read-JsonLines([string]$Path) {
    $content = $utf8.GetString([IO.File]::ReadAllBytes($Path))
    if (-not $content.EndsWith("`n", [StringComparison]::Ordinal)) { throw "JSONL file lacks final LF: $Path" }
    $lines = $content.Split("`n")
    $objects = [Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt ($lines.Count - 1); $index++) {
        if ($lines[$index].Length -eq 0) { throw "JSONL file has an empty line: $Path" }
        $objects.Add((ConvertFrom-Json -InputObject $lines[$index] -AsHashtable -Depth 64))
    }
    return $objects.ToArray()
}

function New-JsonLine([object]$Value) {
    if ($Value -is [Collections.IDictionary] -and $Value.op -in @('define', 'eval') -and -not $Value.Contains('frontend')) {
        $Value.frontend = 'stack'
    }
    return ConvertTo-Json -InputObject $Value -Compress -Depth 64
}

function New-HostArguments {
    param(
        [string]$RuntimeDll,
        [string]$ProjectPath,
        [string]$TracePath,
        [string[]]$AllowedOperations,
        [string[]]$AdditionalCliArguments = @(),
        [string]$Profile = 'conventional',
        [int]$ExchangeTimeoutMilliseconds = 1500,
        [int]$MaxRequestBytes = 262144,
        [int]$MaxResponseBytes = 524288,
        [int]$MaxExchanges = 100
    )
    $arguments = [Collections.Generic.List[string]]::new()
    foreach ($item in @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', $startScript,
        '-CliDll', $RuntimeDll, '-ProjectPath', $ProjectPath, '-TracePath', $TracePath,
        '-AllowedOperations', ($AllowedOperations -join ','), '-Profile', $Profile,
        '-ExchangeTimeoutMilliseconds', $ExchangeTimeoutMilliseconds.ToString(),
        '-MaxRequestBytes', $MaxRequestBytes.ToString(), '-MaxResponseBytes', $MaxResponseBytes.ToString(),
        '-MaxExchanges', $MaxExchanges.ToString())) {
        $arguments.Add([string]$item)
    }
    if ($AdditionalCliArguments.Count -gt 0) {
        $arguments.Add('-AdditionalCliArgumentsJson')
        $arguments.Add((ConvertTo-Json -InputObject @($AdditionalCliArguments) -Compress -Depth 10))
    }
    return $arguments.ToArray()
}

function Invoke-HostRun {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$RuntimeDll,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Requests,
        [string[]]$AllowedOperations = @('fake.echo'),
        [string[]]$AdditionalCliArguments = @(),
        [string]$Profile = 'conventional',
        [string]$ProjectPath,
        [int]$ExchangeTimeoutMilliseconds = 1500,
        [int]$MaxRequestBytes = 262144,
        [int]$MaxResponseBytes = 524288,
        [int]$MaxExchanges = 100,
        [int]$OuterTimeoutMilliseconds = 15000,
        [byte[]]$InputBytesOverride,
        [switch]$NoFinalLf
    )
    if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
        $ProjectPath = Join-Path $artifactRoot "$Name-project"
        [void](New-Item -ItemType Directory -Path $ProjectPath)
    } elseif (-not (Test-Path -LiteralPath $ProjectPath -PathType Container)) {
        [void](New-Item -ItemType Directory -Path $ProjectPath)
    }
    $tracePath = Join-Path $traceRoot "trial-host-v2-$runId-$Name.jsonl"
    $inputText = if ($Requests.Count -gt 0) { $Requests -join "`n" } else { '' }
    if ($Requests.Count -gt 0 -and -not $NoFinalLf) { $inputText += "`n" }
    $inputBytes = $utf8.GetBytes($inputText)
    if ($PSBoundParameters.ContainsKey('InputBytesOverride')) { $inputBytes = $InputBytesOverride }
    $arguments = New-HostArguments -RuntimeDll $RuntimeDll -ProjectPath $ProjectPath -TracePath $tracePath `
        -AllowedOperations $AllowedOperations -AdditionalCliArguments $AdditionalCliArguments `
        -Profile $Profile `
        -ExchangeTimeoutMilliseconds $ExchangeTimeoutMilliseconds -MaxRequestBytes $MaxRequestBytes `
        -MaxResponseBytes $MaxResponseBytes -MaxExchanges $MaxExchanges
    $process = Invoke-BoundedProcess -FileName $pwsh -Arguments $arguments -InputBytes $inputBytes `
        -TimeoutMilliseconds $OuterTimeoutMilliseconds
    $responses = [Collections.Generic.List[object]]::new()
    foreach ($line in ($process.stdout -split "\r?\n" | Where-Object { $_.Length -gt 0 })) {
        try { $responses.Add((ConvertFrom-Json -InputObject $line -AsHashtable -Depth 64)) }
        catch { $responses.Add([ordered]@{ parseError = $_.Exception.Message; raw = $line }) }
    }
    $traceEvents = @()
    if (Test-Path -LiteralPath $tracePath -PathType Leaf) { $traceEvents = @(Read-JsonLines $tracePath) }
    $run = [ordered]@{
        name = $Name
        projectPath = $ProjectPath
        tracePath = $tracePath
        runtimeDll = $RuntimeDll
        runtimeDllSha256 = (Get-FileHash -LiteralPath $RuntimeDll -Algorithm SHA256).Hash.ToLowerInvariant()
        allowedOperations = $AllowedOperations
        process = $process
        responses = $responses.ToArray()
        traceEvents = $traceEvents
        inputUtf8Bytes = $inputBytes.Length
    }
    $runs.Add($run)
    return $run
}

function Build-FakeRuntime {
    $fakeRoot = Join-Path $artifactRoot 'fake-runtime-source'
    $outputRoot = Join-Path $artifactRoot 'fake-runtime-output'
    [void](New-Item -ItemType Directory -Path $fakeRoot)
    [void](New-Item -ItemType Directory -Path $outputRoot)
    $project = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
'@
    $program = @'
using System.Text;
Console.OutputEncoding = new UTF8Encoding(false);
var mode = args.FirstOrDefault(value => value.StartsWith("--fake-mode=", StringComparison.Ordinal))?.Substring("--fake-mode=".Length) ?? "echo";
var logPath = args.FirstOrDefault(value => value.StartsWith("--fake-log-path=", StringComparison.Ordinal))?.Substring("--fake-log-path=".Length);
var argumentLogPath = args.FirstOrDefault(value => value.StartsWith("--fake-args-log-path=", StringComparison.Ordinal))?.Substring("--fake-args-log-path=".Length);
if (argumentLogPath is not null) File.WriteAllLines(argumentLogPath, args, new UTF8Encoding(false));
string? line;
while ((line = Console.ReadLine()) is not null)
{
    if (logPath is not null) File.AppendAllText(logPath, line + "\n", new UTF8Encoding(false));
    Console.WriteLine("{\"ok\":true}");
    Console.Out.Flush();
}
if (mode == "hang-on-eof") Thread.Sleep(TimeSpan.FromSeconds(30));
return 0;
'@
    [IO.File]::WriteAllText((Join-Path $fakeRoot 'FakeRuntime.csproj'), $project, $utf8)
    [IO.File]::WriteAllText((Join-Path $fakeRoot 'Program.cs'), $program, $utf8)
    $build = Invoke-BoundedProcess -FileName 'dotnet' -Arguments @('build', (Join-Path $fakeRoot 'FakeRuntime.csproj'), '-c', 'Release', '--nologo', '-o', $outputRoot) `
        -InputBytes ([byte[]]@()) -TimeoutMilliseconds 90000
    Assert-Check -Name 'controlled fake child builds' -Passed (-not $build.timedOut -and $build.exitCode -eq 0 -and -not $build.outputLimitExceeded) `
        -Detail ($build.stdout + $build.stderr)
    return (Join-Path $outputRoot 'FakeRuntime.dll')
}

function Get-Events([object]$Run, [string]$ExpectedEventType) {
    $matched = [Collections.Generic.List[object]]::new()
    foreach ($traceEntry in $Run.traceEvents) {
        $actualEventType = Get-Property $traceEntry 'event'
        if ($actualEventType -ceq $ExpectedEventType) { $matched.Add($traceEntry) }
    }
    return $matched.ToArray()
}

function Get-Property([object]$Value, [string]$Name) {
    if ($null -eq $Value) { return $null }
    if ($Value -is [Collections.IDictionary]) {
        if ($Value.Contains($Name)) { return $Value[$Name] }
        return $null
    }
    $property = $Value.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Set-Property([object]$Value, [string]$Name, [object]$PropertyValue) {
    if ($Value -is [Collections.IDictionary]) {
        $Value[$Name] = $PropertyValue
        return
    }
    $property = $Value.PSObject.Properties[$Name]
    if ($null -ne $property) {
        $property.Value = $PropertyValue
    } else {
        $Value | Add-Member -MemberType NoteProperty -Name $Name -Value $PropertyValue -Force
    }
}

function Get-Termination([object]$Run) {
    $end = @(Get-Events $Run 'session-end') | Select-Object -First 1
    if ($null -eq $end) { return $null }
    return $end.terminationKind
}

function Invoke-Audit([string]$Name, [string]$Path) {
    $arguments = @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', $auditScript, '-TracePath', $Path)
    $result = Invoke-BoundedProcess -FileName $pwsh -Arguments $arguments -InputBytes ([byte[]]@()) -TimeoutMilliseconds 8000
    $auditResults.Add([ordered]@{ name=$Name; tracePath=$Path; exitCode=$result.exitCode; stdout=$result.stdout; stderr=$result.stderr })
    return $result
}

function Test-AuditRejection([object]$Result, [string]$ExpectedDiagnostic) {
    $output = [string]$Result.stdout + [string]$Result.stderr
    return [bool]($Result.exitCode -ne 0 -and $output.IndexOf($ExpectedDiagnostic, [StringComparison]::OrdinalIgnoreCase) -ge 0)
}

function Write-MutatedTrace([string]$Path, [object[]]$Events) {
    $lines = [Collections.Generic.List[string]]::new()
    foreach ($event in $Events) { $lines.Add((ConvertTo-Json -InputObject $event -Compress -Depth 64)) }
    [IO.File]::WriteAllText($Path, (($lines -join "`n") + "`n"), $utf8)
}

function New-DeepCopy([object]$Value) {
    $json = ConvertTo-Json -InputObject $Value -Compress -Depth 64
    return ConvertFrom-Json -InputObject $json -AsHashtable -Depth 64
}

function Get-TraceInventory([object[]]$Runs) {
    $rows = [Collections.Generic.List[object]]::new()
    foreach ($run in $Runs) {
        if (-not (Test-Path -LiteralPath $run.tracePath -PathType Leaf)) { continue }
        $item = Get-Item -LiteralPath $run.tracePath
        $rows.Add([ordered]@{
            run = $run.name
            path = [IO.Path]::GetRelativePath($repoRoot, $run.tracePath).Replace('\','/')
            bytes = $item.Length
            sha256 = (Get-FileHash -LiteralPath $run.tracePath -Algorithm SHA256).Hash.ToLowerInvariant()
            eventCount = $run.traceEvents.Count
            eventNames = @($run.traceEvents | ForEach-Object { $_.event })
            terminationKind = Get-Termination $run
        })
    }
    return $rows.ToArray()
}

try {
    if (-not (Test-Path -LiteralPath $startScript -PathType Leaf)) { throw "V2 wrapper script not found: $startScript" }
    if (-not (Test-Path -LiteralPath $auditScript -PathType Leaf)) { throw "V2 termination auditor not found: $auditScript" }
    $version = ([string](& git -C $repoRoot rev-parse HEAD)).Trim()
    $dirty = [bool](& git -C $repoRoot status --porcelain)
    $cliDirectory = Split-Path $CliDll -Parent
    $runtimeFiles = @((Get-ChildItem -LiteralPath $cliDirectory -Filter '*.dll' -File | ForEach-Object {
        [ordered]@{ name=$_.Name; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    }))
    $wrapperFiles = @($startScript, $auditScript, $PSCommandPath) | ForEach-Object {
        [ordered]@{ path=[IO.Path]::GetRelativePath($repoRoot, $_).Replace('\','/'); sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() }
    }

    $fakeDll = Build-FakeRuntime
    $fakeFiles = @((Get-ChildItem -LiteralPath (Split-Path $fakeDll -Parent) -File | ForEach-Object {
        [ordered]@{ name=$_.Name; bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    }))

    $guardLog = Join-Path $artifactRoot 'reserved-control-whitelist-child.jsonl'
    $reservedWhitelist = Invoke-HostRun -Name 'reserved-close-not-allowed-as-runtime-op' -RuntimeDll $fakeDll -Requests @() `
        -AdditionalCliArguments @('--fake-log-path=' + $guardLog) -AllowedOperations @('fake.echo','host.close')
    $reservedDiagnostic = 'The reserved host.close transport control cannot be exposed as a runtime operation.'
    $reservedOutput = [string]$reservedWhitelist.process.stdout + [string]$reservedWhitelist.process.stderr
    $reservedDiagnosticMatched = $reservedOutput.IndexOf($reservedDiagnostic, [StringComparison]::Ordinal) -ge 0
    Assert-Check -Name 'startup rejects exposing host.close as an allowed runtime operation before launching the child' -Passed (
        $reservedWhitelist.process.exitCode -ne 0 -and $reservedWhitelist.traceEvents.Count -eq 0 -and
        -not (Test-Path -LiteralPath $guardLog) -and $reservedDiagnosticMatched
    ) -Detail "exit=$($reservedWhitelist.process.exitCode); traceEvents=$($reservedWhitelist.traceEvents.Count); childStarted=$(Test-Path -LiteralPath $guardLog); expectedDiagnostic='$reservedDiagnostic'; diagnosticMatched=$reservedDiagnosticMatched; output=$($reservedOutput.Trim())"

    foreach ($reservedCliOption in @('--filesystem', '--filesystem=real', '--test-allow', '--test-allow=fs.write')) {
        $reservedName = ($reservedCliOption -replace '[^A-Za-z0-9]+', '-')
        $reservedArgsPath = Join-Path $artifactRoot "reserved-cli-option-$reservedName-args.txt"
        $reservedArguments = [Collections.Generic.List[string]]::new()
        $reservedArguments.Add('--fake-args-log-path=' + $reservedArgsPath)
        $reservedArguments.Add($reservedCliOption)
        $reservedCliOptionRun = Invoke-HostRun -Name "reserved-cli-option-$reservedName" -RuntimeDll $fakeDll -Requests @() `
            -Profile agentlang -AdditionalCliArguments $reservedArguments.ToArray() -AllowedOperations @('fake.echo')
        $reservedCliOptionOutput = [string]$reservedCliOptionRun.process.stdout + [string]$reservedCliOptionRun.process.stderr
        $reservedCliOptionRejected = $reservedCliOptionRun.process.exitCode -ne 0 -and
            -not (Test-Path -LiteralPath $reservedArgsPath) -and
            $reservedCliOptionOutput.IndexOf('cannot override wrapper-owned project, protocol, capability, or clock settings', [StringComparison]::Ordinal) -ge 0
        Assert-Check -Name "startup reserves $reservedCliOption for broker-owned filesystem and test capabilities" -Passed $reservedCliOptionRejected `
            -Detail "exit=$($reservedCliOptionRun.process.exitCode); childStarted=$(Test-Path -LiteralPath $reservedArgsPath); output=$($reservedCliOptionOutput.Trim())"
    }

    $agentlangArgsPath = Join-Path $artifactRoot 'agentlang-profile-runtime-arguments.txt'
    $agentlangProfile = Invoke-HostRun -Name 'agentlang-profile-virtual-filesystem' -RuntimeDll $fakeDll -Requests @('{"op":"fake.echo"}') `
        -Profile agentlang -AdditionalCliArguments @('--fake-args-log-path=' + $agentlangArgsPath) -AllowedOperations @('fake.echo')
    $agentlangRuntimeArgs = @(Get-Content -LiteralPath $agentlangArgsPath)
    $filesystemPairCount = 0
    for ($index = 0; $index -lt ($agentlangRuntimeArgs.Count - 1); $index++) {
        if ($agentlangRuntimeArgs[$index] -ceq '--filesystem' -and $agentlangRuntimeArgs[$index + 1] -ceq 'virtual') { $filesystemPairCount++ }
    }
    Assert-Check -Name 'agentlang broker profile pins the virtual filesystem and leaves test capabilities at their defaults' -Passed (
        $agentlangProfile.process.exitCode -eq 0 -and $filesystemPairCount -eq 1 -and
        $agentlangRuntimeArgs -ccontains '--clock' -and -not ($agentlangRuntimeArgs -ccontains '--test-allow') -and
        -not ($agentlangRuntimeArgs -ccontains '--allow')
    ) -Detail "exit=$($agentlangProfile.process.exitCode); args=$($agentlangRuntimeArgs -join ' ')"

    $conventionalArgsPath = Join-Path $artifactRoot 'conventional-profile-runtime-arguments.txt'
    $conventionalProfile = Invoke-HostRun -Name 'conventional-profile-arguments' -RuntimeDll $fakeDll -Requests @('{"op":"fake.echo"}') `
        -AdditionalCliArguments @('--fake-args-log-path=' + $conventionalArgsPath) -AllowedOperations @('fake.echo')
    $conventionalRuntimeArgs = @(Get-Content -LiteralPath $conventionalArgsPath)
    Assert-Check -Name 'conventional broker profile receives neither filesystem nor AgentLang capability flags' -Passed (
        $conventionalProfile.process.exitCode -eq 0 -and
        -not ($conventionalRuntimeArgs -ccontains '--filesystem') -and
        -not ($conventionalRuntimeArgs -ccontains '--test-allow') -and
        -not ($conventionalRuntimeArgs -ccontains '--allow') -and
        -not ($conventionalRuntimeArgs -ccontains '--clock')
    ) -Detail "exit=$($conventionalProfile.process.exitCode); args=$($conventionalRuntimeArgs -join ' ')"

    $successSource = @'
word trial.success : Int -> Int
    effects none
    1 add
end

test trial.success/basic
    1 trial.success
    => 2
end
'@
    $writeRequests = @(
        (New-JsonLine ([ordered]@{ op='task.begin'; goal='host v2 focused verification' })),
        (New-JsonLine ([ordered]@{ op='define'; source=$successSource })),
        (New-JsonLine ([ordered]@{ op='test'; word='trial.success' })),
        (New-JsonLine ([ordered]@{ op='commit'; word='trial.success' })),
        (New-JsonLine ([ordered]@{ op='task.commit' })),
        '{"op":"host.close"}'
    )
    $realWrite = Invoke-HostRun -Name 'real-cli-write-close' -RuntimeDll $CliDll -Requests $writeRequests `
        -AllowedOperations @('task.begin','define','test','commit','task.commit') -ExchangeTimeoutMilliseconds 15000 `
        -MaxResponseBytes 524288 -OuterTimeoutMilliseconds 90000
    $realCloseEvents = @(Get-Events $realWrite 'host-close')
    $realEnd = @(Get-Events $realWrite 'session-end') | Select-Object -First 1
    $realResponsesOk = $realWrite.responses.Count -eq 5
    foreach ($response in $realWrite.responses) {
        if ($null -eq $response.ok -or $response.ok -ne $true) { $realResponsesOk = $false }
    }
    $realTraceResponsesOk = @($realWrite.traceEvents | Where-Object { $_.event -ceq 'exchange' }).Count -eq 5
    foreach ($exchange in @(Get-Events $realWrite 'exchange')) {
        $traceResponse = ConvertFrom-Json -InputObject $exchange.response.rawLine -AsHashtable -Depth 64
        if ($null -eq $traceResponse.ok -or $traceResponse.ok -ne $true -or $exchange.response.source -cne 'runtime') { $realTraceResponsesOk = $false }
    }
    $realEndOk = $null -ne $realEnd -and $realEnd.terminationKind -ceq 'host-close' -and
        $realEnd.hostExitCode -eq 0 -and $realEnd.runtimeExitCode -eq 0
    $realWriteClean = $realWrite.process.exitCode -eq 0 -and -not $realWrite.process.timedOut -and
        $realResponsesOk -and $realTraceResponsesOk -and $realCloseEvents.Count -eq 1 -and $realEndOk
    Assert-Check -Name 'real CLI flow completes through explicit host.close' -Passed (
        $realWriteClean
    ) -Detail "processExit=$($realWrite.process.exitCode); timedOut=$($realWrite.process.timedOut); responses=$($realWrite.responses.Count); stdoutResponsesOk=$realResponsesOk; traceResponsesOk=$realTraceResponsesOk; closeEvents=$($realCloseEvents.Count); term=$($realEnd.terminationKind); hostExit=$($realEnd.hostExitCode); runtimeExit=$($realEnd.runtimeExitCode)"
    Assert-Check -Name 'host.close produces no stdout acknowledgement and does not consume an exchange' -Passed (
        $realWrite.responses.Count -eq 5 -and $realCloseEvents[0].exchangeCount -eq 5 -and
        @($realWrite.traceEvents | Where-Object { $_.event -ceq 'exchange' }).Count -eq 5 -and
        @($realWrite.traceEvents | Where-Object { $_.event -ceq 'response-delivered' }).Count -eq 0
    ) -Detail "closeCount=$($realCloseEvents[0].exchangeCount); exchangeEvents=$(@(Get-Events $realWrite 'exchange').Count)"

    $reloadRequests = @(
        (New-JsonLine ([ordered]@{ op='tests'; word='trial.success' })),
        (New-JsonLine ([ordered]@{ op='test'; word='trial.success' })),
        (New-JsonLine ([ordered]@{ op='describe'; word='trial.success' })),
        '{"op":"host.close"}'
    )
    $realReload = Invoke-HostRun -Name 'real-cli-reload-close' -RuntimeDll $CliDll -ProjectPath $realWrite.projectPath `
        -Requests $reloadRequests -AllowedOperations @('tests','test','describe') -ExchangeTimeoutMilliseconds 15000 `
        -OuterTimeoutMilliseconds 60000
    Assert-Check -Name 'fresh CLI process reloads committed definition and test after host.close' -Passed (
        $realReload.process.exitCode -eq 0 -and $realReload.responses.Count -eq 3 -and
        $realReload.responses[0].ok -and $realReload.responses[0].data -contains 'basic' -and
        $realReload.responses[1].ok -and $realReload.responses[1].data.results[0].passed -eq $true -and
        $realReload.responses[2].ok -and $realReload.responses[2].data.name -eq 'trial.success' -and
        (Get-Termination $realReload) -eq 'host-close'
    ) -Detail "responses=$($realReload.responses.Count); testPassed=$($realReload.responses[1].data.results[0].passed)"

    $auditReal = Invoke-Audit -Name 'real-cli-write-trace' -Path $realWrite.tracePath
    Assert-Check -Name 'generic termination audit accepts real CLI close trace' -Passed ($auditReal.exitCode -eq 0) -Detail ($auditReal.stdout + $auditReal.stderr)

    $unicodeValue = 'Ω π 🌿 配置'
    $unicodeRequest = New-JsonLine ([ordered]@{ op='eval'; frontend='flow'; syntaxVersion=2; code=('"' + $unicodeValue + '"') })
    $unicodeRun = Invoke-HostRun -Name 'real-cli-unicode' -RuntimeDll $CliDll `
        -Requests @($unicodeRequest, '{"op":"host.close"}') -AllowedOperations @('eval') `
        -ExchangeTimeoutMilliseconds 15000 -OuterTimeoutMilliseconds 60000
    $decodedValue = $null
    if ($unicodeRun.responses.Count -eq 1 -and $unicodeRun.responses[0].ok) {
        $decodedValue = ConvertFrom-Json -InputObject $unicodeRun.responses[0].data.stack[0] -NoEnumerate
    }
    Assert-Check -Name 'raw UTF-8 request preserves exact non-ASCII value through real CLI' -Passed (
        $unicodeRun.process.exitCode -eq 0 -and $unicodeRun.responses.Count -eq 1 -and
        $unicodeRun.responses[0].ok -and $decodedValue -ceq $unicodeValue -and
        (Get-Termination $unicodeRun) -eq 'host-close'
    ) -Detail "decoded='$decodedValue'"

    $recoveryInput = [byte[]](@(255, 10) + @($utf8.GetBytes('{"op":"eval","frontend":"flow","syntaxVersion":2,"code":"42"}' + "`n" + '{"op":"host.close"}' + "`n")))
    $invalidUtf8Run = Invoke-HostRun -Name 'invalid-utf8-recovery' -RuntimeDll $CliDll `
        -Requests @() -InputBytesOverride $recoveryInput -AllowedOperations @('eval') `
        -ExchangeTimeoutMilliseconds 15000 -OuterTimeoutMilliseconds 60000
    Assert-Check -Name 'malformed UTF-8 is rejected and next valid request still executes' -Passed (
        $invalidUtf8Run.process.exitCode -eq 0 -and $invalidUtf8Run.responses.Count -eq 2 -and
        $invalidUtf8Run.responses[0].error.code -ceq 'TRIAL_INVALID_UTF8' -and
        $invalidUtf8Run.responses[1].ok -and $invalidUtf8Run.responses[1].data.stack[0] -ceq '42' -and
        (Get-Termination $invalidUtf8Run) -eq 'host-close'
    ) -Detail "responses=$($invalidUtf8Run.responses.Count)"

    $maxLog = Join-Path $artifactRoot 'max-exchange-forwarded.jsonl'
    $maxRequests = @((New-JsonLine ([ordered]@{ op='fake.echo'; n=1 })), (New-JsonLine ([ordered]@{ op='fake.echo'; n=2 })),
        (New-JsonLine ([ordered]@{ op='fake.echo'; n=3 })), '{"op":"host.close"}')
    $atMax = Invoke-HostRun -Name 'close-at-exchange-limit' -RuntimeDll $fakeDll -Requests $maxRequests `
        -AdditionalCliArguments @('--fake-log-path=' + $maxLog) -AllowedOperations @('fake.echo') -MaxExchanges 3
    $maxForwarded = @(if (Test-Path -LiteralPath $maxLog) { [IO.File]::ReadAllLines($maxLog, $utf8) })
    $maxClose = @(Get-Events $atMax 'host-close') | Select-Object -First 1
    Assert-Check -Name 'host.close is accepted at the exact exchange limit without incrementing it' -Passed (
        $atMax.process.exitCode -eq 0 -and $atMax.responses.Count -eq 3 -and
        $maxForwarded.Count -eq 3 -and $maxClose.exchangeCount -eq 3 -and
        @(Get-Events $atMax 'exchange').Count -eq 3 -and (Get-Termination $atMax) -eq 'host-close'
    ) -Detail "responses=$($atMax.responses.Count); forwarded=$($maxForwarded.Count); closeCount=$($maxClose.exchangeCount)"
    $auditMax = Invoke-Audit -Name 'close-at-limit-trace' -Path $atMax.tracePath
    Assert-Check -Name 'generic termination audit checks exchange ordinals and preserved close wire at limit' -Passed ($auditMax.exitCode -eq 0) -Detail ($auditMax.stdout + $auditMax.stderr)

    $capLog = Join-Path $artifactRoot 'response-cap-close-forwarded.jsonl'
    $crlfCloseRequest = '{ "op" : "host.close" }' + "`r"
    $closeOnly = Invoke-HostRun -Name 'close-only-one-byte-response-cap' -RuntimeDll $fakeDll `
        -Requests @($crlfCloseRequest) -AdditionalCliArguments @('--fake-log-path=' + $capLog) `
        -AllowedOperations @('fake.echo') -MaxResponseBytes 1
    $capEnd = @(Get-Events $closeOnly 'session-end') | Select-Object -First 1
    $capClose = @(Get-Events $closeOnly 'host-close') | Select-Object -First 1
    Assert-Check -Name 'close-only CRLF control succeeds under a one-byte response cap without an ack' -Passed (
        $closeOnly.process.exitCode -eq 0 -and $closeOnly.process.stdoutUtf8Bytes -eq 0 -and
        $closeOnly.responses.Count -eq 0 -and @(Get-Events $closeOnly 'host-close').Count -eq 1 -and
        $capEnd.runtimeExitCode -eq 0 -and -not (Test-Path -LiteralPath $capLog) -and
        $capClose.requestRaw.EndsWith("`r") -and $capClose.requestCanonical -eq '{"op":"host.close"}'
    ) -Detail "exit=$($closeOnly.process.exitCode); stdoutBytes=$($closeOnly.process.stdoutUtf8Bytes); runtimeExit=$($capEnd.runtimeExitCode); rawHasCR=$($capClose.requestRaw.EndsWith("`r"))"
    $auditCrLf = Invoke-Audit -Name 'close-only-crlf-trace' -Path $closeOnly.tracePath
    Assert-Check -Name 'generic audit verifies CRLF wire preservation and one-field canonical close' -Passed ($auditCrLf.exitCode -eq 0) -Detail ($auditCrLf.stdout + $auditCrLf.stderr)

    $badLog = Join-Path $artifactRoot 'malformed-close-forwarded.jsonl'
    $badRequests = @(
        (New-JsonLine ([ordered]@{ op='fake.echo'; marker='only forwarded request' })),
        '{"op":"host.close","reason":"extra"}',
        '{"op":"host.close","op":"host.close"}',
        '{"op":"host.close"}'
    )
    $malformed = Invoke-HostRun -Name 'malformed-close-frames' -RuntimeDll $fakeDll -Requests $badRequests `
        -AdditionalCliArguments @('--fake-log-path=' + $badLog) -AllowedOperations @('fake.echo') -MaxExchanges 6
    $badForwarded = @(if (Test-Path -LiteralPath $badLog) { [IO.File]::ReadAllLines($badLog, $utf8) })
    $badExchanges = @(Get-Events $malformed 'exchange')
    $badErrors = [Collections.Generic.List[object]]::new()
    foreach ($response in $malformed.responses) {
        $responseError = Get-Property $response 'error'
        if ((Get-Property $responseError 'code') -ceq 'TRIAL_INVALID_REQUEST') { $badErrors.Add($response) }
    }
    $badCloseExchanges = [Collections.Generic.List[object]]::new()
    foreach ($exchange in $badExchanges) {
        if ((Get-Property $exchange 'errorCode') -ceq 'TRIAL_INVALID_REQUEST') { $badCloseExchanges.Add($exchange) }
    }
    $badCloseWithForwarding = [Collections.Generic.List[object]]::new()
    foreach ($exchange in $badCloseExchanges) {
        $delivery = Get-Property $exchange 'requestDelivery'
        if ((Get-Property $delivery 'state') -cne 'not-sent' -or (Get-Property $exchange 'executionState') -cne 'not-executed') {
            $badCloseWithForwarding.Add($exchange)
        }
    }
    Assert-Check -Name 'extra-field and duplicate-key close frames are rejected and never forwarded' -Passed (
        $malformed.process.exitCode -eq 0 -and $badForwarded.Count -eq 1 -and
        $badErrors.Count -eq 2 -and $badExchanges.Count -eq 3 -and $badCloseExchanges.Count -eq 2 -and
        $badCloseWithForwarding.Count -eq 0 -and
        (Get-Termination $malformed) -eq 'host-close'
    ) -Detail "invalidResponses=$($badErrors.Count); countedExchanges=$($badExchanges.Count); rejectedCloseExchanges=$($badCloseExchanges.Count); forwarded=$($badForwarded.Count)"
    $auditMalformed = Invoke-Audit -Name 'malformed-then-valid-close-trace' -Path $malformed.tracePath
    Assert-Check -Name 'audit accepts valid terminal close after rejected malformed close requests' -Passed ($auditMalformed.exitCode -eq 0) -Detail ($auditMalformed.stdout + $auditMalformed.stderr)

    $partialLog = Join-Path $artifactRoot 'partial-close-forwarded.jsonl'
    $partial = Invoke-HostRun -Name 'partial-lf-close' -RuntimeDll $fakeDll -Requests @('{"op":"host.close"}') `
        -NoFinalLf -AdditionalCliArguments @('--fake-log-path=' + $partialLog) -AllowedOperations @('fake.echo') -MaxExchanges 4
    $partialEnd = @(Get-Events $partial 'session-end') | Select-Object -First 1
    $partialDiagnostic = 'JSONL requests must end with LF; the unterminated final request was not forwarded.'
    $partialRejectedAsUnterminated = $false
    foreach ($response in $partial.responses) {
        $responseError = Get-Property $response 'error'
        if ((Get-Property $responseError 'code') -ceq 'TRIAL_UNTERMINATED_REQUEST' -and
            (Get-Property $responseError 'message') -ceq $partialDiagnostic) { $partialRejectedAsUnterminated = $true }
    }
    $partialAudit = Invoke-Audit -Name 'partial-close-trace' -Path $partial.tracePath
    Assert-Check -Name 'partial LF close is not recognized as the transport control' -Passed (
        $partial.process.exitCode -ne 0 -and @(Get-Events $partial 'host-close').Count -eq 0 -and
        $partialEnd.terminationKind -ne 'host-close' -and
        (-not (Test-Path -LiteralPath $partialLog) -or [IO.File]::ReadAllText($partialLog, $utf8) -notmatch 'host\.close') -and
        $partialRejectedAsUnterminated
    ) -Detail "exit=$($partial.process.exitCode); termination=$($partialEnd.terminationKind); rejectionCode=TRIAL_UNTERMINATED_REQUEST; expectedDiagnostic='$partialDiagnostic'; diagnosticMatched=$partialRejectedAsUnterminated; responseOutput=$($partial.process.stdout.Trim()); auditExit=$($partialAudit.exitCode)"
    Assert-Check -Name 'partial close fails the explicit termination audit because no complete close control was traced' `
        -Passed (Test-AuditRejection $partialAudit 'Expected exactly one host-close event') -Detail ($partialAudit.stdout + $partialAudit.stderr)

    $eofLog = Join-Path $artifactRoot 'ordinary-eof-forwarded.jsonl'
    $ordinaryEof = Invoke-HostRun -Name 'ordinary-input-eof' -RuntimeDll $fakeDll `
        -Requests @((New-JsonLine ([ordered]@{ op='fake.echo'; note='ordinary EOF' }))) `
        -AdditionalCliArguments @('--fake-log-path=' + $eofLog) -AllowedOperations @('fake.echo')
    $eofEnd = @(Get-Events $ordinaryEof 'session-end') | Select-Object -First 1
    $eofAudit = Invoke-Audit -Name 'ordinary-eof-trace' -Path $ordinaryEof.tracePath
    Assert-Check -Name 'ordinary stdin EOF is recorded separately from explicit actor completion' -Passed (
        $ordinaryEof.process.exitCode -eq 0 -and $eofEnd.terminationKind -eq 'input-eof' -and
        $eofEnd.runtimeExitCode -eq 0 -and @(Get-Events $ordinaryEof 'host-close').Count -eq 0
    ) -Detail "hostExit=$($ordinaryEof.process.exitCode); termination=$($eofEnd.terminationKind); runtimeExit=$($eofEnd.runtimeExitCode)"
    Assert-Check -Name 'generic audit rejects ordinary EOF because no host.close event was traced' `
        -Passed (Test-AuditRejection $eofAudit 'Expected exactly one host-close event') -Detail ($eofAudit.stdout + $eofAudit.stderr)

    $hangLog = Join-Path $artifactRoot 'hanging-child-forwarded.jsonl'
    $hanging = Invoke-HostRun -Name 'child-hang-on-close' -RuntimeDll $fakeDll -Requests @('{"op":"host.close"}') `
        -AdditionalCliArguments @('--fake-mode=hang-on-eof', "--fake-log-path=$hangLog") -AllowedOperations @('fake.echo') `
        -ExchangeTimeoutMilliseconds 350 -OuterTimeoutMilliseconds 8000
    $hangEnd = @(Get-Events $hanging 'session-end') | Select-Object -First 1
    $hangAudit = Invoke-Audit -Name 'hanging-child-trace' -Path $hanging.tracePath
    Assert-Check -Name 'hanging child shutdown is bounded and cannot pass termination audit' -Passed (
        -not $hanging.process.timedOut -and -not $hanging.process.cleanupTimedOut -and
        $hanging.process.durationMilliseconds -lt 5000 -and $hangAudit.exitCode -ne 0 -and
        ($hanging.process.exitCode -ne 0 -or $hangEnd.hostExitCode -ne 0 -or $hangEnd.runtimeExitCode -ne 0) -and
        (Test-AuditRejection $hangAudit 'host-close must be the final transport event before session-end')
    ) -Detail "hostProcessExit=$($hanging.process.exitCode); hostExit=$($hangEnd.hostExitCode); runtimeExit=$($hangEnd.runtimeExitCode); durationMs=$($hanging.process.durationMilliseconds); auditExit=$($hangAudit.exitCode)"

    $baseTraceEvents = @(Read-JsonLines $atMax.tracePath)
    $baseShapeValid = $baseTraceEvents.Count -gt 0
    foreach ($baseEvent in $baseTraceEvents) {
        if ($null -eq $baseEvent -or (Get-Property $baseEvent 'event') -isnot [string]) { $baseShapeValid = $false }
    }
    Assert-Check -Name 'mutation source trace parses as a flat list of typed events' -Passed $baseShapeValid `
        -Detail "eventCount=$($baseTraceEvents.Count); eventTypes=$((@($baseTraceEvents | ForEach-Object { Get-Property $_ 'event' }) -join ','))"
    $baselineAudit = Invoke-Audit -Name 'mutation-source-baseline' -Path $atMax.tracePath
    Assert-Check -Name 'mutation source baseline passes the termination auditor before mutation' -Passed ($baselineAudit.exitCode -eq 0) `
        -Detail ($baselineAudit.stdout + $baselineAudit.stderr)
    $baseWithoutStart = [Collections.Generic.List[object]]::new()
    foreach ($event in $baseTraceEvents) { $baseWithoutStart.Add((New-DeepCopy $event)) }
    $mutationRoot = Join-Path $artifactRoot 'audit-mutations'
    [void](New-Item -ItemType Directory -Path $mutationRoot)

    $tamperedEvents = [Collections.Generic.List[object]]::new()
    foreach ($sourceEvent in $baseWithoutStart) {
        $event = New-DeepCopy $sourceEvent
        if ($event.event -ceq 'host-close') { Set-Property $event 'requestWireSha256' ('0' * 64) }
        $tamperedEvents.Add($event)
    }
    $tamperedPath = Join-Path $mutationRoot 'mutated-wire-hash.jsonl'
    Write-MutatedTrace $tamperedPath $tamperedEvents.ToArray()
    $tamperedAudit = Invoke-Audit -Name 'mutated-host-close-wire-hash' -Path $tamperedPath
    Assert-Check -Name 'audit rejects a mutated host-close wire hash for the hash mismatch' `
        -Passed (Test-AuditRejection $tamperedAudit 'host-close requestWireSha256 does not match') -Detail ($tamperedAudit.stdout + $tamperedAudit.stderr)

    $duplicatedEvents = [Collections.Generic.List[object]]::new()
    foreach ($event in $baseWithoutStart) {
        if ($event.event -ceq 'session-end') {
            $closeCopy = New-DeepCopy (@($baseWithoutStart | Where-Object { $_.event -ceq 'host-close' })[0])
            $duplicatedEvents.Add($closeCopy)
        }
        $duplicatedEvents.Add((New-DeepCopy $event))
    }
    $duplicatePath = Join-Path $mutationRoot 'duplicate-host-close-event.jsonl'
    Write-MutatedTrace $duplicatePath $duplicatedEvents.ToArray()
    $duplicateAudit = Invoke-Audit -Name 'duplicate-host-close-event' -Path $duplicatePath
    Assert-Check -Name 'audit rejects duplicated host-close terminal evidence for the duplicate event' `
        -Passed (Test-AuditRejection $duplicateAudit 'Expected exactly one host-close event') -Detail ($duplicateAudit.stdout + $duplicateAudit.stderr)

    $missingCloseEvents = @($baseWithoutStart | Where-Object { $_.event -cne 'host-close' })
    $missingClosePath = Join-Path $mutationRoot 'missing-host-close-event.jsonl'
    Write-MutatedTrace $missingClosePath $missingCloseEvents
    $missingCloseAudit = Invoke-Audit -Name 'missing-host-close-event' -Path $missingClosePath
    Assert-Check -Name 'audit rejects a missing host-close event for the missing terminal control' `
        -Passed (Test-AuditRejection $missingCloseAudit 'Expected exactly one host-close event') -Detail ($missingCloseAudit.stdout + $missingCloseAudit.stderr)

    $missingEndEvents = @($baseWithoutStart | Where-Object { $_.event -cne 'session-end' })
    $missingEndPath = Join-Path $mutationRoot 'missing-session-end-event.jsonl'
    Write-MutatedTrace $missingEndPath $missingEndEvents
    $missingEndAudit = Invoke-Audit -Name 'missing-session-end-event' -Path $missingEndPath
    Assert-Check -Name 'audit rejects a missing terminal session-end event for the missing end' `
        -Passed (Test-AuditRejection $missingEndAudit 'Expected exactly one session-end event') -Detail ($missingEndAudit.stdout + $missingEndAudit.stderr)

    $overLimitEvents = [Collections.Generic.List[object]]::new()
    foreach ($sourceEvent in $baseWithoutStart) {
        $event = New-DeepCopy $sourceEvent
        if ($event.event -ceq 'session-start') {
            Set-Property (Get-Property $event 'limits') 'maxRequestBytes' ([long](@($baseWithoutStart | Where-Object { $_.event -ceq 'host-close' })[0].requestWireUtf8Bytes - 2))
        }
        $overLimitEvents.Add($event)
    }
    $overLimitPath = Join-Path $mutationRoot 'close-payload-over-configured-limit.jsonl'
    Write-MutatedTrace $overLimitPath $overLimitEvents.ToArray()
    $overLimitAudit = Invoke-Audit -Name 'close-payload-over-configured-limit' -Path $overLimitPath
    Assert-Check -Name 'audit rejects a preserved close frame larger than configured maxRequestBytes for the payload limit' `
        -Passed (Test-AuditRejection $overLimitAudit 'Preserved host-close payload exceeds the configured request limit') -Detail ($overLimitAudit.stdout + $overLimitAudit.stderr)

    $configCapEvents = [Collections.Generic.List[object]]::new()
    foreach ($sourceEvent in $baseWithoutStart) {
        $event = New-DeepCopy $sourceEvent
        if ($event.event -ceq 'session-start') { Set-Property (Get-Property $event 'limits') 'maxExchanges' 101 }
        $configCapEvents.Add($event)
    }
    $configCapPath = Join-Path $mutationRoot 'max-exchanges-over-host-cap.jsonl'
    Write-MutatedTrace $configCapPath $configCapEvents.ToArray()
    $configCapAudit = Invoke-Audit -Name 'max-exchanges-over-host-cap' -Path $configCapPath
    Assert-Check -Name 'audit rejects a session-start maxExchanges above the host cap for the configured cap' `
        -Passed (Test-AuditRejection $configCapAudit 'maxExchanges limit or exceeds the host cap') -Detail ($configCapAudit.stdout + $configCapAudit.stderr)

    $countTamperedEvents = [Collections.Generic.List[object]]::new()
    foreach ($sourceEvent in $baseWithoutStart) {
        $event = New-DeepCopy $sourceEvent
        if ($event.event -ceq 'host-close') { Set-Property $event 'exchangeCount' ([long]$event.exchangeCount + 1) }
        $countTamperedEvents.Add($event)
    }
    $countTamperedPath = Join-Path $mutationRoot 'mutated-close-exchange-count.jsonl'
    Write-MutatedTrace $countTamperedPath $countTamperedEvents.ToArray()
    $countTamperedAudit = Invoke-Audit -Name 'mutated-close-exchange-count' -Path $countTamperedPath
    Assert-Check -Name 'audit rejects a host-close exchange count that differs from the broker trace for the count mismatch' `
        -Passed (Test-AuditRejection $countTamperedAudit 'host-close exchangeCount does not equal') -Detail ($countTamperedAudit.stdout + $countTamperedAudit.stderr)

    $uncertainEvents = [Collections.Generic.List[object]]::new()
    foreach ($sourceEvent in $baseWithoutStart) {
        $event = New-DeepCopy $sourceEvent
        if ($event.event -ceq 'exchange') {
            Set-Property $event 'executionState' 'uncertain'
            Set-Property $event 'outcome' 'timeout'
            $delivery = Get-Property $event 'requestDelivery'
            Set-Property $delivery 'state' 'uncertain'
            Set-Property $delivery 'completeLineConfirmed' $false
        }
        $uncertainEvents.Add($event)
    }
    $uncertainPath = Join-Path $mutationRoot 'uncertain-prior-exchange.jsonl'
    Write-MutatedTrace $uncertainPath $uncertainEvents.ToArray()
    $uncertainAudit = Invoke-Audit -Name 'uncertain-prior-exchange' -Path $uncertainPath
    Assert-Check -Name 'audit rejects uncertain prior exchange history for the nonterminal state' `
        -Passed (Test-AuditRejection $uncertainAudit 'has nonterminal execution state') -Detail ($uncertainAudit.stdout + $uncertainAudit.stderr)

    $partialObservedEvents = [Collections.Generic.List[object]]::new()
    foreach ($sourceEvent in $baseWithoutStart) {
        $event = New-DeepCopy $sourceEvent
        if ($event.event -ceq 'exchange') {
            Set-Property $event 'observedRuntimeResponse' ([ordered]@{
                complete = $false
                validJson = $true
                utf8Bytes = 4
                sha256 = '0'
                base64 = 'e30='
                canonical = '{}'
                wireUtf8Bytes = 5
                wireSha256 = '0'
                wireBase64 = 'e30K'
            })
        }
        $partialObservedEvents.Add($event)
    }
    $partialObservedPath = Join-Path $mutationRoot 'partial-observed-runtime-response.jsonl'
    Write-MutatedTrace $partialObservedPath $partialObservedEvents.ToArray()
    $partialObservedAudit = Invoke-Audit -Name 'partial-observed-runtime-response' -Path $partialObservedPath
    Assert-Check -Name 'audit rejects incomplete observed runtime response bytes for incompleteness' `
        -Passed (Test-AuditRejection $partialObservedAudit 'observed runtime response is incomplete') -Detail ($partialObservedAudit.stdout + $partialObservedAudit.stderr)

    $cancelledHistoryEvents = [Collections.Generic.List[object]]::new()
    foreach ($event in $baseWithoutStart) {
        if ($event.event -ceq 'host-close') {
            $cancelledHistoryEvents.Add([ordered]@{ event='host-cancelled'; atUtc='2026-01-01T00:00:00Z'; exchangeCount=3 })
        }
        $cancelledHistoryEvents.Add((New-DeepCopy $event))
    }
    $cancelledHistoryPath = Join-Path $mutationRoot 'cancelled-prior-history.jsonl'
    Write-MutatedTrace $cancelledHistoryPath $cancelledHistoryEvents.ToArray()
    $cancelledHistoryAudit = Invoke-Audit -Name 'cancelled-prior-history' -Path $cancelledHistoryPath
    Assert-Check -Name 'audit rejects cancellation history before a synthetic clean close for the cancellation event' `
        -Passed (Test-AuditRejection $cancelledHistoryAudit 'Trace contains a host cancellation event') -Detail ($cancelledHistoryAudit.stdout + $cancelledHistoryAudit.stderr)

    $report = [ordered]@{
        schemaVersion = 1
        kind = 'subagent-trial-host-v2-focused-verification'
        outcome = 'passed'
        createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
        repoRoot = $repoRoot
        testedHead = $version
        workingTreeDirty = $dirty
        cliDll = $CliDll
        cliFiles = $runtimeFiles
        wrapperFiles = $wrapperFiles
        fakeChildFiles = $fakeFiles
        artifacts = [IO.Path]::GetRelativePath($repoRoot, $artifactRoot).Replace('\','/')
        checkCount = $checks.Count
        passedCheckCount = @($checks | Where-Object passed).Count
        failedCheckCount = @($checks | Where-Object { -not $_.passed }).Count
        checks = @($checks)
        traceInventory = @(Get-TraceInventory @($runs))
        audits = @($auditResults)
        limits = @{
            verification = 'Focused transport and persistence verification; no model behavior, throughput, or end-user turn metrics are inferred.'
            shutdown = 'The fake hanging child is used only to confirm bounded close cleanup and that the termination auditor rejects nonzero or missing runtime completion.'
        }
    }
    [IO.File]::WriteAllText($EvidencePath, (ConvertTo-Json -InputObject $report -Depth 64), $utf8)
    Write-Output ("V2 focused verification passed: {0} checks. Evidence: {1}" -f $checks.Count, $EvidencePath)
    exit 0
}
catch {
    $caughtError = $_
    $failed = [ordered]@{
        schemaVersion = 1
        kind = 'subagent-trial-host-v2-focused-verification'
        outcome = 'failed'
        createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
        repoRoot = $repoRoot
        testedHead = $version
        workingTreeDirty = $dirty
        cliDll = $CliDll
        cliFiles = $runtimeFiles
        wrapperFiles = $wrapperFiles
        fakeChildDll = $fakeDll
        artifacts = [IO.Path]::GetRelativePath($repoRoot, $artifactRoot).Replace('\','/')
        error = $caughtError.Exception.Message
        errorId = $caughtError.FullyQualifiedErrorId
        scriptStackTrace = $caughtError.ScriptStackTrace
        checkCount = $checks.Count
        passedCheckCount = @($checks | Where-Object passed).Count
        failedCheckCount = @($checks | Where-Object { -not $_.passed }).Count
        checks = @($checks)
        traceInventory = @(Get-TraceInventory @($runs))
        audits = @($auditResults)
    }
    if (-not (Test-Path -LiteralPath $EvidencePath)) {
        $evidenceDirectory = Split-Path -Parent $EvidencePath
        if (-not (Test-Path -LiteralPath $evidenceDirectory)) { [void](New-Item -ItemType Directory -Path $evidenceDirectory -Force) }
        [IO.File]::WriteAllText($EvidencePath, (ConvertTo-Json -InputObject $failed -Depth 64), $utf8)
    }
    [Console]::Error.WriteLine(("V2 focused verification failed; evidence: {0}`n{1}" -f $EvidencePath, $caughtError.Exception.Message))
    exit 1
}
