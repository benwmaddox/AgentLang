[CmdletBinding()]
param(
    [string]$CliDll,
    [string]$EvidencePath
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$startScript = Join-Path $PSScriptRoot 'Start-SubagentTrialHost.ps1'
if ([string]::IsNullOrWhiteSpace($CliDll)) {
    $CliDll = Join-Path $repoRoot '.agentlang/subagent-matched/host-9c6ba75/AgentLang.Cli.dll'
}
if ([System.IO.Path]::IsPathRooted($CliDll)) {
    $CliDll = [System.IO.Path]::GetFullPath($CliDll)
} else {
    $CliDll = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $CliDll))
}
if (-not (Test-Path -LiteralPath $CliDll -PathType Leaf)) { throw "Pinned CLI DLL not found: $CliDll" }

$runId = [guid]::NewGuid().ToString('N')
$artifactRoot = Join-Path $repoRoot ".agentlang/subagent-host-verification-$runId"
$traceRoot = Join-Path $repoRoot '.agentlang/reports'
[void](New-Item -ItemType Directory -Path $artifactRoot)
if (-not (Test-Path -LiteralPath $traceRoot)) { [void](New-Item -ItemType Directory -Path $traceRoot) }
if ([string]::IsNullOrWhiteSpace($EvidencePath)) {
    $EvidencePath = Join-Path $traceRoot "subagent-trial-host-$runId.json"
} else {
    if ([System.IO.Path]::IsPathRooted($EvidencePath)) {
        $EvidencePath = [System.IO.Path]::GetFullPath($EvidencePath)
    } else {
        $EvidencePath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $EvidencePath))
    }
}
if (Test-Path -LiteralPath $EvidencePath) {
    $evidenceDirectory = Split-Path $EvidencePath -Parent
    $evidenceStem = [System.IO.Path]::GetFileNameWithoutExtension($EvidencePath)
    $evidenceExtension = [System.IO.Path]::GetExtension($EvidencePath)
    $attempt = 1
    do {
        $candidate = Join-Path $evidenceDirectory ("{0}-retry-{1:D2}{2}" -f $evidenceStem, $attempt, $evidenceExtension)
        $attempt++
    } while (Test-Path -LiteralPath $candidate)
    $EvidencePath = $candidate
}

$pwsh = Join-Path $PSHOME 'pwsh.exe'
if (-not (Test-Path -LiteralPath $pwsh -PathType Leaf)) { throw "PowerShell Core executable not found: $pwsh" }
$utf8 = [System.Text.UTF8Encoding]::new($false, $true)
$checks = [System.Collections.Generic.List[object]]::new()
$runs = [System.Collections.Generic.List[object]]::new()

$boundedProcessSource = @'
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace AgentLang.SubagentTrialVerification
{
    public sealed class ProcessRunResult
    {
        public int? ExitCode;
        public bool TimedOut;
        public bool OutputLimitExceeded;
        public bool CleanupTimedOut;
        public string InputError;
        public string TaskError;
        public byte[] StdoutBytes;
        public byte[] StderrBytes;
        public double DurationMilliseconds;
    }

    internal sealed class BoundedOutputCollector
    {
        private readonly object _gate = new object();
        private readonly MemoryStream _stdout = new MemoryStream();
        private readonly MemoryStream _stderr = new MemoryStream();
        private readonly int _limit;
        private int _total;
        private bool _exceeded;

        public BoundedOutputCollector(int limit) { _limit = limit; }
        public bool Exceeded { get { lock (_gate) return _exceeded; } }
        public byte[] Stdout { get { lock (_gate) return _stdout.ToArray(); } }
        public byte[] Stderr { get { lock (_gate) return _stderr.ToArray(); } }

        public bool Append(bool stdout, byte[] buffer, int count)
        {
            lock (_gate)
            {
                int retain = Math.Min(count, Math.Max(0, _limit - _total));
                if (retain > 0)
                {
                    (stdout ? _stdout : _stderr).Write(buffer, 0, retain);
                    _total += retain;
                }
                if (retain != count) _exceeded = true;
                return _exceeded;
            }
        }
    }

    public static class BoundedProcessRunner
    {
        public static ProcessRunResult Run(string fileName, string workingDirectory, string[] arguments,
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
            var collector = new BoundedOutputCollector(outputLimitBytes);
            var result = new ProcessRunResult();
            var stopwatch = Stopwatch.StartNew();
            if (!process.Start()) throw new InvalidOperationException("Could not start verifier subprocess.");

            Task deadline = Task.Delay(timeoutMilliseconds);
            Task stdoutTask = Task.Run(() => DrainAsync(process.StandardOutput.BaseStream, collector, true, process));
            Task stderrTask = Task.Run(() => DrainAsync(process.StandardError.BaseStream, collector, false, process));
            Task writeTask = Task.Run(() => WriteAndCloseAsync(process, input ?? new byte[0]));
            Task waitTask = process.WaitForExitAsync();
            Task all = Task.WhenAll(stdoutTask, stderrTask, writeTask, waitTask);

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
                    catch (Exception ex)
                    {
                        result.TaskError = ex.GetBaseException().GetType().Name + ": " + ex.GetBaseException().Message;
                        if (writeTask.IsFaulted) result.InputError = result.TaskError;
                    }
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
                if (input.Length > 0)
                    await process.StandardInput.BaseStream.WriteAsync(input, 0, input.Length).ConfigureAwait(false);
                await process.StandardInput.BaseStream.FlushAsync().ConfigureAwait(false);
                process.StandardInput.Close();
            }
            catch
            {
                try { process.StandardInput.Close(); } catch { }
                throw;
            }
        }

        private static async Task DrainAsync(Stream stream, BoundedOutputCollector collector, bool stdout, Process process)
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
            try { if (!process.HasExited) process.Kill(true); } catch { try { if (!process.HasExited) process.Kill(); } catch { } }
        }
    }
}
'@
Add-Type -TypeDefinition $boundedProcessSource -Language CSharp -ErrorAction Stop

function Add-Check {
    param([string]$Name, [bool]$Passed, [string]$Detail)
    $script:checks.Add([ordered]@{ name = $Name; passed = $Passed; detail = $Detail })
}

function Assert-Check {
    param([string]$Name, [bool]$Passed, [string]$Detail)
    Add-Check -Name $Name -Passed $Passed -Detail $Detail
    if (-not $Passed) { throw "Verification failed: $Name — $Detail" }
}

function Invoke-BoundedProcess {
    param(
        [Parameter(Mandatory)][string]$FileName,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][AllowEmptyString()][string]$InputText,
        [Parameter(Mandatory)][int]$TimeoutMilliseconds,
        [Parameter(Mandatory)][int]$OutputLimitBytes
    )

    $inputBytes = $utf8.GetBytes($InputText)
    $result = [AgentLang.SubagentTrialVerification.BoundedProcessRunner]::Run(
        $FileName, $repoRoot, $Arguments, $inputBytes, $TimeoutMilliseconds, $OutputLimitBytes)
    $stdout = $utf8.GetString($result.StdoutBytes)
    $stderr = $utf8.GetString($result.StderrBytes)
    return [ordered]@{
        exitCode = $result.ExitCode
        timedOut = $result.TimedOut
        outputLimitExceeded = $result.OutputLimitExceeded
        cleanupTimedOut = $result.CleanupTimedOut
        inputError = $result.InputError
        taskError = $result.TaskError
        durationMilliseconds = [math]::Round($result.DurationMilliseconds, 3)
        stdout = $stdout
        stderr = $stderr
        stdoutUtf8Bytes = $result.StdoutBytes.Length
        stderrUtf8Bytes = $result.StderrBytes.Length
    }
}

function New-HostArguments {
    param(
        [string]$RuntimeDll = $CliDll,
        [string]$ProjectPath,
        [string]$TracePath,
        [string[]]$AllowedOperations,
        [string]$Profile = 'agentlang',
        [string[]]$AdditionalCliArguments = @(),
        [string[]]$Capabilities = @(),
        [int]$ExchangeTimeoutMilliseconds = 2000,
        [int]$MaxRequestBytes = 262144,
        [int]$MaxResponseBytes = 524288,
        [Nullable[long]]$MaxInspectionResponseBytes = $null
    )
    $arguments = [System.Collections.Generic.List[string]]::new()
    foreach ($item in @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', $startScript,
        '-CliDll', $RuntimeDll, '-ProjectPath', $ProjectPath, '-TracePath', $TracePath,
        '-AllowedOperations', ($AllowedOperations -join ','), '-Profile', $Profile,
        '-ExchangeTimeoutMilliseconds', $ExchangeTimeoutMilliseconds.ToString(),
        '-MaxRequestBytes', $MaxRequestBytes.ToString(), '-MaxResponseBytes', $MaxResponseBytes.ToString())) {
        $arguments.Add([string]$item)
    }
    if ($null -ne $MaxInspectionResponseBytes) {
        $arguments.Add('-MaxInspectionResponseBytes')
        $arguments.Add(([long]$MaxInspectionResponseBytes).ToString())
    }
    if ($AdditionalCliArguments.Count -gt 0) {
        $arguments.Add('-AdditionalCliArgumentsJson')
        $arguments.Add((ConvertTo-Json -InputObject @($AdditionalCliArguments) -Compress -Depth 10))
    }
    if ($Capabilities.Count -gt 0) {
        $arguments.Add('-Capabilities')
        $arguments.Add(($Capabilities -join ','))
    }
    return $arguments.ToArray()
}

function Invoke-TrialHost {
    param(
        [string]$Name,
        [string]$RuntimeDll = $CliDll,
        [string]$ProjectPath,
        [string[]]$Requests,
        [string[]]$AllowedOperations,
        [string]$Profile = 'agentlang',
        [string[]]$AdditionalCliArguments = @(),
        [string[]]$Capabilities = @(),
        [int]$ExchangeTimeoutMilliseconds = 2000,
        [int]$MaxRequestBytes = 262144,
        [int]$MaxResponseBytes = 524288,
        [Nullable[long]]$MaxInspectionResponseBytes = $null,
        [int]$OuterTimeoutMilliseconds = 15000
    )
    if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
        $projectPath = Join-Path $artifactRoot $Name
        [void](New-Item -ItemType Directory -Path $projectPath)
    } else {
        $projectPath = [System.IO.Path]::GetFullPath($ProjectPath)
        if (-not (Test-Path -LiteralPath $projectPath -PathType Container)) { throw "Reused project directory does not exist: $projectPath" }
    }
    $tracePath = Join-Path $traceRoot "trial-host-$runId-$Name.jsonl"
    $arguments = New-HostArguments -RuntimeDll $RuntimeDll -ProjectPath $projectPath -TracePath $tracePath `
        -AllowedOperations $AllowedOperations -Profile $Profile -AdditionalCliArguments $AdditionalCliArguments `
        -Capabilities $Capabilities -ExchangeTimeoutMilliseconds $ExchangeTimeoutMilliseconds `
        -MaxRequestBytes $MaxRequestBytes -MaxResponseBytes $MaxResponseBytes `
        -MaxInspectionResponseBytes $MaxInspectionResponseBytes
    $inputText = if ($Requests.Count -eq 0) { '' } else { ($Requests -join "`n") + "`n" }
    $result = Invoke-BoundedProcess -FileName $pwsh -Arguments $arguments -InputText $inputText `
        -TimeoutMilliseconds $OuterTimeoutMilliseconds -OutputLimitBytes 2097152
    $parsedResponses = @()
    foreach ($line in ($result.stdout -split "`r?`n" | Where-Object { $_.Length -gt 0 })) {
        try { $parsedResponses += ,(ConvertFrom-Json -InputObject $line -AsHashtable -Depth 64) }
        catch { $parsedResponses += ,([ordered]@{ parseError = $_.Exception.Message; raw = $line }) }
    }
    $traceEvents = @()
    if (Test-Path -LiteralPath $tracePath) {
        foreach ($line in Get-Content -LiteralPath $tracePath -Encoding utf8) {
            $traceEvents += ,(ConvertFrom-Json -InputObject $line -AsHashtable -Depth 64)
        }
    }
    $run = [ordered]@{
        name = $Name
        projectPath = $projectPath
        tracePath = $tracePath
        runtimeDll = $RuntimeDll
        runtimeDllSha256 = (Get-FileHash -LiteralPath $RuntimeDll -Algorithm SHA256).Hash.ToLowerInvariant()
        allowedOperations = $AllowedOperations
        profile = $Profile
        process = $result
        responses = $parsedResponses
        traceEvents = $traceEvents
    }
    $runs.Add($run)
    return $run
}

function Get-ExchangeTrace {
    param([object]$Run, [int]$Index = 1)
    return @($Run.traceEvents | Where-Object { $_.event -eq 'exchange' }) | Select-Object -Index ($Index - 1)
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
using System.Text.Json;

Console.OutputEncoding = new System.Text.UTF8Encoding(false);
var mode = args.FirstOrDefault(value => value.StartsWith("--fake-mode=", StringComparison.Ordinal))?.Substring("--fake-mode=".Length) ?? "echo";
switch (mode)
{
    case "no-read":
        Thread.Sleep(TimeSpan.FromSeconds(30));
        return 0;
    case "partial":
        if (Console.ReadLine() is null) return 11;
        Console.Write("{\"partial\":");
        Console.Out.Flush();
        for (var i = 0; i < 8; i++)
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(650));
            Console.Write(" ");
            Console.Out.Flush();
        }
        Console.Write("}\n");
        Console.Out.Flush();
        return 0;
    case "oversize":
        if (Console.ReadLine() is null) return 12;
        Console.WriteLine(new string('x', 4096));
        Console.Out.Flush();
        return 0;
    case "invalid-json":
        if (Console.ReadLine() is null) return 14;
        Console.Write("not-json\n");
        Console.Out.Flush();
        return 0;
    case "exit-after-read":
        if (Console.ReadLine() is null) return 13;
        return 42;
    case "response-before-write-timeout":
        if (Console.Read() < 0) return 15;
        Console.Write("{\"ok\":true}\n");
        Console.Out.Flush();
        Thread.Sleep(TimeSpan.FromSeconds(30));
        return 0;
    case "budget-responses":
        string? budgetRequest;
        while ((budgetRequest = Console.ReadLine()) is not null)
        {
            using var document = JsonDocument.Parse(budgetRequest);
            var reply = document.RootElement.TryGetProperty("reply", out var replyValue) ? replyValue.GetString() : "small";
            var response = reply switch
            {
                "large" => "{\"ok\":true,\"text\":\"abcdefgh\"}",
                "unicode-crlf" => "{\"ok\":true,\"text\":\"é\"}\r",
                "diagnostic" => "{\"ok\":false,\"kind\":\"error\",\"text\":\"café\",\"error\":{\"code\":\"SAMPLE\",\"message\":\"café\"}}",
                _ => "{\"ok\":true}"
            };
            Console.Write(response);
            Console.Write("\n");
            Console.Out.Flush();
        }
        return 0;
    case "inspect-args":
        var hasClock = args.Contains("--clock", StringComparer.Ordinal);
        var hasAllow = args.Contains("--allow", StringComparer.Ordinal);
        Console.WriteLine(JsonSerializer.Serialize(new { ok = !hasClock && !hasAllow, hasClock, hasAllow, args }));
        Console.Out.Flush();
        return 0;
    case "spam":
        for (var i = 0; i < 16384; i++) Console.Write(new string('z', 1024));
        Console.Out.Flush();
        return 0;
    default:
        while (Console.ReadLine() is not null)
        {
            Console.WriteLine("{\"ok\":true}");
            Console.Out.Flush();
        }
        return 0;
}
'@
    [System.IO.File]::WriteAllText((Join-Path $fakeRoot 'FakeRuntime.csproj'), $project, $utf8)
    [System.IO.File]::WriteAllText((Join-Path $fakeRoot 'Program.cs'), $program, $utf8)
    $build = Invoke-BoundedProcess -FileName 'dotnet' -Arguments @('build', (Join-Path $fakeRoot 'FakeRuntime.csproj'), '-c', 'Release', '--nologo', '-o', $outputRoot) `
        -InputText '' -TimeoutMilliseconds 90000 -OutputLimitBytes 2097152
    Assert-Check -Name 'fake child build succeeds' -Passed (-not $build.timedOut -and $build.exitCode -eq 0) -Detail ($build.stdout + $build.stderr)
    return (Join-Path $outputRoot 'FakeRuntime.dll')
}

function New-JsonLine {
    param([object]$Value)
    if ($Value -is [System.Collections.IDictionary] -and $Value.op -in @('define', 'eval') -and -not $Value.Contains('frontend')) {
        $Value.frontend = 'stack'
    }
    return ConvertTo-Json -InputObject $Value -Compress -Depth 64
}

function Get-Utf8Sha256 {
    param([Parameter(Mandatory)][string]$Value)
    $bytes = $utf8.GetBytes($Value)
    $hash = [System.Security.Cryptography.SHA256]::Create()
    try { return [Convert]::ToHexString($hash.ComputeHash($bytes)).ToLowerInvariant() }
    finally { $hash.Dispose() }
}

function New-BudgetRequest {
    param([string]$Operation, [string]$Reply = 'small')
    return New-JsonLine @{ op = $Operation; reply = $Reply }
}

function Get-BoundedEvidenceItems {
    param(
        [AllowEmptyCollection()][object[]]$Items,
        [int]$MaximumItems = 32,
        [int]$MaximumJsonCharacters = 8192
    )
    $bounded = [System.Collections.Generic.List[object]]::new()
    $count = 0
    foreach ($item in @($Items)) {
        if ($count -ge $MaximumItems) { break }
        $json = ConvertTo-Json -InputObject $item -Compress -Depth 24
        if ($json.Length -le $MaximumJsonCharacters) {
            $bounded.Add($item)
        } else {
            $summary = [ordered]@{
                truncated = $true
                originalJsonCharacters = $json.Length
                jsonPrefix = $json.Substring(0, $MaximumJsonCharacters)
            }
            # Keep deadline/delivery evidence even when the request payload consumes
            # the bounded prefix. These fields come from the full observed trace.
            if ($item.event -eq 'exchange') {
                foreach ($field in @('event', 'index', 'operation', 'outcome', 'errorCode', 'elapsedMilliseconds', 'requestDelivery', 'executionState', 'automaticRetry')) {
                    $summary[$field] = $item.$field
                }
            }
            $bounded.Add($summary)
        }
        $count++
    }
    return $bounded.ToArray()
}

try {
    $version = & git -C $repoRoot rev-parse HEAD
    $dirty = [bool](& git -C $repoRoot status --porcelain)
    $runtimeDirectory = Split-Path $CliDll -Parent
    $runtimePaths = [System.Collections.Generic.List[string]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $runtimeDirectory -Filter '*.dll' -File) { $runtimePaths.Add($file.FullName) }
    foreach ($name in @('AgentLang.Cli.deps.json', 'AgentLang.Cli.runtimeconfig.json')) {
        $path = Join-Path $runtimeDirectory $name
        if (Test-Path -LiteralPath $path -PathType Leaf) { $runtimePaths.Add($path) }
    }
    $cliFiles = @($runtimePaths | Sort-Object -Unique | ForEach-Object {
        [ordered]@{
            name = [System.IO.Path]::GetFileName($_)
            sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
    $wrapperFiles = @($startScript, $PSCommandPath, (Join-Path $repoRoot 'docs/SUBAGENT-TRIAL-HOST.md')) | ForEach-Object {
        [ordered]@{ path = [System.IO.Path]::GetRelativePath($repoRoot, $_); sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() }
    }

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
    $initialRequests = @(
        (New-JsonLine @{ op = 'task.begin'; goal = 'wrapper persistent-flow verification' }),
        (New-JsonLine @{ op = 'define'; source = $successSource }),
        (New-JsonLine @{ op = 'test'; word = 'trial.success' }),
        (New-JsonLine @{ op = 'commit'; word = 'trial.success' }),
        (New-JsonLine @{ op = 'task.commit' })
    )
    # Happy-path real-runtime sessions use the host's 15-second allowance and outer budgets for their request counts.
    $initial = Invoke-TrialHost -Name 'language-write-session' -Requests $initialRequests `
        -AllowedOperations @('task.begin', 'define', 'test', 'commit', 'task.commit') `
        -ExchangeTimeoutMilliseconds 15000 -OuterTimeoutMilliseconds 90000
    Assert-Check -Name 'language write session exits cleanly' -Passed ($initial.process.exitCode -eq 0 -and -not $initial.process.timedOut) -Detail "exit=$($initial.process.exitCode); stderr=$($initial.process.stderr)"
    Assert-Check -Name 'language write responses all succeed' -Passed ($initial.responses.Count -eq 5 -and @($initial.responses | Where-Object { -not $_.ok }).Count -eq 0) -Detail "responses=$($initial.responses.Count)"
    Assert-Check -Name 'language write session confirms full request delivery' -Passed (@($initial.traceEvents | Where-Object { $_.event -eq 'exchange' -and $_.requestDelivery.state -ne 'confirmed' }).Count -eq 0) -Detail 'All five forwarded exchanges have a confirmed complete line.'
    $startEvent = @($initial.traceEvents | Where-Object { $_.event -eq 'session-start' })[0]
    Assert-Check -Name 'disabled inspection budget preserves schema 1 trace shape' -Passed (
        $startEvent.schemaVersion -eq 1 -and $initial.traceEvents.Count -eq 7 -and
        @($initial.traceEvents | Where-Object { $_.event -eq 'response-delivered' }).Count -eq 0 -and
        -not $startEvent.ContainsKey('inspectionBudget') -and
        @($initial.traceEvents | Where-Object { $_.event -eq 'exchange' -and ($_.ContainsKey('inspectionBudget') -or $_.ContainsKey('responseAccounting')) }).Count -eq 0
    ) -Detail "schema=$($startEvent.schemaVersion); events=$($initial.traceEvents.Count); deliveredEvents=$(@($initial.traceEvents | Where-Object { $_.event -eq 'response-delivered' }).Count)"
    Assert-Check -Name 'x64 JobObject ABI layout is recorded and validated' -Passed (
        $startEvent.processArchitecture -eq 'x64' -and
        $startEvent.jobObjectLayout.basicLimitInformationBytes -eq 64 -and
        $startEvent.jobObjectLayout.basicLimitFlagsOffset -eq 16 -and
        $startEvent.jobObjectLayout.ioCountersBytes -eq 48 -and
        $startEvent.jobObjectLayout.extendedLimitInformationBytes -eq 144 -and
        $startEvent.jobObjectLayout.extendedIoInfoOffset -eq 64 -and
        $startEvent.jobObjectLayout.processMemoryLimitOffset -eq 112 -and
        $startEvent.jobObjectLayout.jobMemoryLimitOffset -eq 120 -and
        $startEvent.jobObjectLayout.peakProcessMemoryUsedOffset -eq 128 -and
        $startEvent.jobObjectLayout.peakJobMemoryUsedOffset -eq 136
    ) -Detail ($startEvent.jobObjectLayout | ConvertTo-Json -Compress)

    $reloadRequests = @(
        (New-JsonLine @{ op = 'tests'; word = 'trial.success' }),
        (New-JsonLine @{ op = 'test'; word = 'trial.success' }),
        (New-JsonLine @{ op = 'describe'; word = 'trial.success' })
    )
    $reload = Invoke-TrialHost -Name 'language-reload-session' -ProjectPath $initial.projectPath -Requests $reloadRequests `
        -AllowedOperations @('tests', 'test', 'describe') `
        -ExchangeTimeoutMilliseconds 15000 -OuterTimeoutMilliseconds 60000
    Assert-Check -Name 'fresh process loads committed definition and tests' -Passed (
        $reload.process.exitCode -eq 0 -and $reload.responses.Count -eq 3 -and
        $reload.responses[0].ok -and $reload.responses[0].data -contains 'basic' -and
        $reload.responses[1].ok -and $reload.responses[1].data.results[0].passed -and
        $reload.responses[2].ok -and $reload.responses[2].data.name -eq 'trial.success'
    ) -Detail "exit=$($reload.process.exitCode); test=$($reload.responses[1].text)"

    $budgetEnabledWriteRequests = @($initialRequests) + @((New-JsonLine @{ op = 'describe'; word = 'trial.success' }))
    $budgetEnabledWrite = Invoke-TrialHost -Name 'language-write-zero-inspection-budget' -Requests $budgetEnabledWriteRequests `
        -AllowedOperations @('task.begin', 'define', 'test', 'commit', 'task.commit', 'describe') `
        -MaxInspectionResponseBytes ([long]0) -ExchangeTimeoutMilliseconds 15000 -OuterTimeoutMilliseconds 90000
    $budgetWriteExchanges = @($budgetEnabledWrite.traceEvents | Where-Object { $_.event -eq 'exchange' })
    Assert-Check -Name 'real language writes and test results remain visible at zero inspection budget' -Passed (
        $budgetEnabledWrite.process.exitCode -eq 0 -and $budgetEnabledWrite.responses.Count -eq 6 -and
        @($budgetEnabledWrite.responses | Select-Object -First 5 | Where-Object { -not $_.ok }).Count -eq 0 -and
        $budgetEnabledWrite.responses[2].data.results[0].passed -eq $true -and
        $budgetEnabledWrite.responses[4].ok -eq $true -and
        $budgetEnabledWrite.responses[5].error.code -eq 'TRIAL_INSPECTION_BUDGET_EXHAUSTED' -and
        $budgetWriteExchanges[5].requestDelivery.state -eq 'not-sent' -and
        $budgetEnabledWrite.traceEvents[-1].responseAccounting.admittedInspectionPayloadUtf8Bytes -eq 0 -and
        $budgetEnabledWrite.traceEvents[-1].responseAccounting.nonInspectionRuntimeResponsePayloadUtf8Bytes -gt 0 -and
        $budgetEnabledWrite.traceEvents[-1].responseAccounting.stdoutPipeDeliveredResponseCount -eq 6
    ) -Detail "responses=$($budgetEnabledWrite.responses.Count); last=$($budgetEnabledWrite.responses[5].error.code); nonInspectionBytes=$($budgetEnabledWrite.traceEvents[-1].responseAccounting.nonInspectionRuntimeResponsePayloadUtf8Bytes)"

    $malformedRequests = @('{"op":', (New-JsonLine @{ op = 'task.commit' }))
    $malformed = Invoke-TrialHost -Name 'malformed-denied' -Requests $malformedRequests -AllowedOperations @('describe')
    Assert-Check -Name 'malformed request returns structured host error without forwarding' -Passed (
        $malformed.responses.Count -eq 2 -and $malformed.responses[0].error.code -eq 'TRIAL_INVALID_JSON' -and
        $malformed.traceEvents[1].requestDelivery.state -eq 'not-sent' -and $malformed.traceEvents[1].executionState -eq 'not-executed'
    ) -Detail ($malformed.responses[0].text)
    Assert-Check -Name 'disallowed operation is denied before runtime execution' -Passed (
        $malformed.responses[1].error.code -eq 'TRIAL_OPERATION_DENIED' -and
        $malformed.traceEvents[2].requestDelivery.state -eq 'not-sent' -and $malformed.traceEvents[2].executionState -eq 'not-executed'
    ) -Detail ($malformed.responses[1].text)

    $oversizedRequest = Invoke-TrialHost -Name 'oversized-request' -Requests @((New-JsonLine @{ op = 'describe'; word = ('z' * 256) })) `
        -AllowedOperations @('describe') -MaxRequestBytes 64
    Assert-Check -Name 'oversized request is rejected before runtime forwarding' -Passed (
        $oversizedRequest.process.exitCode -eq 2 -and $oversizedRequest.responses[0].error.code -eq 'TRIAL_REQUEST_TOO_LARGE' -and
        $oversizedRequest.traceEvents[1].event -eq 'request-rejected'
    ) -Detail "exit=$($oversizedRequest.process.exitCode); response=$($oversizedRequest.responses[0].text)"

    $fakeDll = Build-FakeRuntime
    $fakeRequest = New-JsonLine @{ op = 'fake.echo' }
    $spamArguments = [string[]]@($fakeDll, '--fake-mode=spam')
    $boundedOutput = [AgentLang.SubagentTrialVerification.BoundedProcessRunner]::Run(
        'dotnet', $repoRoot, $spamArguments, [byte[]]@(), 5000, 1024)
    Assert-Check -Name 'verifier subprocess output capture enforces a hard byte cap and kills the child' -Passed (
        $boundedOutput.OutputLimitExceeded -and $boundedOutput.StdoutBytes.Length -le 1024 -and
        -not $boundedOutput.CleanupTimedOut
    ) -Detail "captured=$($boundedOutput.StdoutBytes.Length); cleanupTimedOut=$($boundedOutput.CleanupTimedOut)"

    $partial = Invoke-TrialHost -Name 'timeout-partial-response' -RuntimeDll $fakeDll -Requests @($fakeRequest) `
        -AllowedOperations @('fake.echo') -AdditionalCliArguments @('--fake-mode=partial') -ExchangeTimeoutMilliseconds 4000
    $partialExchange = Get-ExchangeTrace -Run $partial
    $partialPrefix = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($partialExchange.observedRuntimeResponse.base64))
    Assert-Check -Name 'dripped partial response captures multiple chunks and preserves uncertain no-retry outcome' -Passed (
        $partial.process.exitCode -eq 124 -and $partial.responses[0].error.code -eq 'TRIAL_EXCHANGE_TIMEOUT' -and
        $partialExchange.requestDelivery.state -eq 'confirmed' -and $partialExchange.executionState -eq 'uncertain' -and
        $partialExchange.automaticRetry -like 'never*' -and
        $partialExchange.observedRuntimeResponse.complete -eq $false -and
        $partialPrefix -match '^\{"partial": {2,}$' -and
        $partialExchange.observedRuntimeResponse.utf8Bytes -eq $partialPrefix.Length
    ) -Detail "exit=$($partial.process.exitCode); capturedPrefix='$partialPrefix'; bytes=$($partialExchange.observedRuntimeResponse.utf8Bytes)"
    Assert-Check -Name 'dripped response is bounded by one absolute exchange deadline' -Passed (
        $partialExchange.outcome -eq 'timeout' -and
        $partialExchange.errorCode -eq 'TRIAL_EXCHANGE_TIMEOUT' -and
        $partialExchange.elapsedMilliseconds -ge 3000 -and $partialExchange.elapsedMilliseconds -lt 6500
    ) -Detail "exchangeTraceElapsed=$($partialExchange.elapsedMilliseconds)ms; subprocessTotal=$($partial.process.durationMilliseconds)ms; configuredDeadline=4000ms"

    $noReadPayload = New-JsonLine @{ op = 'fake.echo'; data = ('x' * 250000) }
    $noRead = Invoke-TrialHost -Name 'large-write-no-reader' -RuntimeDll $fakeDll -Requests @($noReadPayload) `
        -AllowedOperations @('fake.echo') -AdditionalCliArguments @('--fake-mode=no-read') -ExchangeTimeoutMilliseconds 500 `
        -MaxRequestBytes 300000 -OuterTimeoutMilliseconds 10000
    $noReadExchange = Get-ExchangeTrace -Run $noRead
    Assert-Check -Name 'non-reading child cannot block a large stdin write past deadline' -Passed (
        $noRead.process.exitCode -eq 124 -and $noRead.responses[0].error.code -eq 'TRIAL_EXCHANGE_TIMEOUT' -and
        $noReadExchange.requestDelivery.state -eq 'uncertain' -and $noReadExchange.requestDelivery.confirmedUtf8Bytes -eq $null -and
        $noReadExchange.requestDelivery.attemptedUtf8Bytes -gt 65536 -and $noReadExchange.executionState -eq 'uncertain' -and
        $noReadExchange.automaticRetry -like 'never*' -and
        $noReadExchange.outcome -eq 'timeout' -and $noReadExchange.errorCode -eq 'TRIAL_EXCHANGE_TIMEOUT' -and
        $noReadExchange.elapsedMilliseconds -ge 400 -and $noReadExchange.elapsedMilliseconds -lt 2500 -and
        -not $noRead.process.timedOut -and $noRead.process.durationMilliseconds -lt 10000
    ) -Detail "exit=$($noRead.process.exitCode); exchangeTraceElapsed=$($noReadExchange.elapsedMilliseconds)ms; subprocessTotal=$($noRead.process.durationMilliseconds)ms; configuredDeadline=500ms; attempted=$($noReadExchange.requestDelivery.attemptedUtf8Bytes)"

    $tooLargeResponse = Invoke-TrialHost -Name 'oversized-response' -RuntimeDll $fakeDll -Requests @($fakeRequest) `
        -AllowedOperations @('fake.echo') -AdditionalCliArguments @('--fake-mode=oversize') -MaxResponseBytes 64
    $tooLargeExchange = Get-ExchangeTrace -Run $tooLargeResponse
    Assert-Check -Name 'oversized runtime response marks execution uncertain and forbids retry' -Passed (
        $tooLargeResponse.process.exitCode -eq 2 -and $tooLargeResponse.responses[0].error.code -eq 'TRIAL_RESPONSE_TOO_LARGE' -and
        $tooLargeExchange.requestDelivery.state -eq 'confirmed' -and $tooLargeExchange.executionState -eq 'uncertain' -and
        $tooLargeExchange.observedRuntimeResponse.complete -eq $false -and
        $tooLargeExchange.observedRuntimeResponse.utf8Bytes -eq 65 -and $tooLargeExchange.automaticRetry -like 'never*'
    ) -Detail "exit=$($tooLargeResponse.process.exitCode); prefixBytes=$($tooLargeExchange.observedRuntimeResponse.utf8Bytes)"

    $invalidJson = Invoke-TrialHost -Name 'invalid-runtime-json' -RuntimeDll $fakeDll -Requests @($fakeRequest) `
        -AllowedOperations @('fake.echo') -AdditionalCliArguments @('--fake-mode=invalid-json')
    $invalidJsonExchange = Get-ExchangeTrace -Run $invalidJson
    Assert-Check -Name 'invalid runtime JSON returns a structured uncertain host error and preserves exact bytes' -Passed (
        $invalidJson.process.exitCode -eq 4 -and $invalidJson.responses[0].error.code -eq 'TRIAL_INVALID_RUNTIME_RESPONSE' -and
        $invalidJsonExchange.executionState -eq 'uncertain' -and $invalidJsonExchange.observedRuntimeResponse.complete -eq $true -and
        $invalidJsonExchange.observedRuntimeResponse.validJson -eq $false -and
        [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($invalidJsonExchange.observedRuntimeResponse.base64)) -eq 'not-json'
    ) -Detail "exit=$($invalidJson.process.exitCode); executionState=$($invalidJsonExchange.executionState)"

    $childExit = Invoke-TrialHost -Name 'child-exits-after-request' -RuntimeDll $fakeDll -Requests @($fakeRequest) `
        -AllowedOperations @('fake.echo') -AdditionalCliArguments @('--fake-mode=exit-after-read')
    $childExitExchange = Get-ExchangeTrace -Run $childExit
    Assert-Check -Name 'lost child response never reports rollback or safe retry' -Passed (
        $childExit.process.exitCode -eq 3 -and $childExit.responses[0].error.code -eq 'TRIAL_CHILD_EXITED' -and
        $childExitExchange.requestDelivery.state -eq 'confirmed' -and $childExitExchange.executionState -eq 'uncertain' -and
        $childExitExchange.automaticRetry -like 'never*'
    ) -Detail "exit=$($childExit.process.exitCode); executionState=$($childExitExchange.executionState)"

    $conventional = Invoke-TrialHost -Name 'conventional-profile-args' -RuntimeDll $fakeDll -Requests @($fakeRequest) `
        -AllowedOperations @('fake.echo') -Profile conventional -AdditionalCliArguments @('--fake-mode=inspect-args')
    Assert-Check -Name 'conventional profile omits AgentLang-only clock and capability flags' -Passed (
        $conventional.process.exitCode -eq 0 -and $conventional.responses[0].ok -and
        $conventional.responses[0].hasClock -eq $false -and $conventional.responses[0].hasAllow -eq $false -and
        $conventional.traceEvents[0].profile -eq 'conventional' -and $conventional.traceEvents[0].additionalCliArguments[0] -eq '--fake-mode=inspect-args'
    ) -Detail ($conventional.responses[0] | ConvertTo-Json -Compress)

    $smallRaw = '{"ok":true}'
    $smallBytes = $utf8.GetByteCount($smallRaw)
    $underBudget = Invoke-TrialHost -Name 'inspection-under-budget' -RuntimeDll $fakeDll `
        -Requests @((New-BudgetRequest -Operation 'search')) -AllowedOperations @('search') `
        -AdditionalCliArguments @('--fake-mode=budget-responses') -MaxInspectionResponseBytes ([long]($smallBytes + 1))
    $underExchange = Get-ExchangeTrace -Run $underBudget
    Assert-Check -Name 'positive remaining inspection budget admits the complete runtime response' -Passed (
        $underBudget.process.exitCode -eq 0 -and $underBudget.responses[0].ok -and
        $underBudget.traceEvents[0].schemaVersion -eq 2 -and $underExchange.inspectionBudget.decision -eq 'admitted' -and
        $underExchange.inspectionBudget.remainingBeforePayloadUtf8Bytes -eq ($smallBytes + 1) -and
        $underExchange.inspectionBudget.admittedPayloadUtf8Bytes -eq $smallBytes -and
        $underExchange.observedRuntimeResponse.validJson -eq $true -and
        $underExchange.observedRuntimeResponse.sha256 -eq (Get-Utf8Sha256 $smallRaw) -and
        $underExchange.response.payloadSha256 -eq (Get-Utf8Sha256 $smallRaw) -and
        $underBudget.traceEvents[-1].responseAccounting.admittedInspectionPayloadUtf8Bytes -eq $smallBytes -and
        $underBudget.traceEvents[-1].responseAccounting.stdoutPipeDeliveredResponsePayloadUtf8Bytes -eq $smallBytes
    ) -Detail "decision=$($underExchange.inspectionBudget.decision); admitted=$($underExchange.inspectionBudget.admittedPayloadUtf8Bytes); remaining=$($underExchange.inspectionBudget.remainingAfterPayloadUtf8Bytes)"

    $equalBudget = Invoke-TrialHost -Name 'inspection-equal-budget' -RuntimeDll $fakeDll `
        -Requests @((New-BudgetRequest -Operation 'search')) -AllowedOperations @('search') `
        -AdditionalCliArguments @('--fake-mode=budget-responses') -MaxInspectionResponseBytes ([long]$smallBytes)
    $equalExchange = Get-ExchangeTrace -Run $equalBudget
    Assert-Check -Name 'response equal to the remaining inspection budget is admitted' -Passed (
        $equalBudget.responses[0].ok -and $equalExchange.inspectionBudget.decision -eq 'admitted' -and
        $equalExchange.inspectionBudget.remainingAfterPayloadUtf8Bytes -eq 0 -and
        $equalBudget.traceEvents[-1].responseAccounting.admittedInspectionPayloadUtf8Bytes -eq $smallBytes
    ) -Detail "cap=$smallBytes; admitted=$($equalExchange.inspectionBudget.admittedPayloadUtf8Bytes)"

    $cumulativeRequests = @(
        (New-BudgetRequest -Operation 'search'),
        (New-BudgetRequest -Operation 'describe'),
        (New-BudgetRequest -Operation 'context')
    )
    $cumulativeBoundary = Invoke-TrialHost -Name 'inspection-cumulative-boundary' -RuntimeDll $fakeDll `
        -Requests $cumulativeRequests -AllowedOperations @('search', 'describe', 'context') `
        -AdditionalCliArguments @('--fake-mode=budget-responses') -MaxInspectionResponseBytes ([long]($smallBytes * 2))
    $cumulativeExchanges = @($cumulativeBoundary.traceEvents | Where-Object { $_.event -eq 'exchange' })
    Assert-Check -Name 'cumulative exact boundary admits whole responses then denies before the next runtime write' -Passed (
        $cumulativeBoundary.process.exitCode -eq 0 -and $cumulativeBoundary.responses.Count -eq 3 -and
        $cumulativeBoundary.responses[0].ok -and $cumulativeBoundary.responses[1].ok -and
        $cumulativeBoundary.responses[2].error.code -eq 'TRIAL_INSPECTION_BUDGET_EXHAUSTED' -and
        $cumulativeExchanges.Count -eq 3 -and $cumulativeExchanges[0].inspectionBudget.decision -eq 'admitted' -and
        $cumulativeExchanges[1].inspectionBudget.decision -eq 'admitted' -and
        $cumulativeExchanges[2].requestDelivery.state -eq 'not-sent' -and
        $cumulativeExchanges[2].executionState -eq 'not-executed' -and
        $cumulativeExchanges[2].observedRuntimeResponse -eq $null -and
        $cumulativeBoundary.traceEvents[-1].responseAccounting.rawValidRuntimeResponsePayloadUtf8Bytes -eq ($smallBytes * 2) -and
        $cumulativeBoundary.traceEvents[-1].responseAccounting.admittedInspectionPayloadUtf8Bytes -eq ($smallBytes * 2) -and
        $cumulativeBoundary.traceEvents[-1].responseAccounting.hostDenialControlResponsePayloadUtf8Bytes -eq $cumulativeExchanges[2].response.payloadUtf8Bytes -and
        $cumulativeBoundary.traceEvents[-1].responseAccounting.stdoutPipeDeliveredResponseCount -eq 3
    ) -Detail "responses=$($cumulativeBoundary.responses.Count); finalDecision=$($cumulativeExchanges[2].inspectionBudget.decision); totals=$($cumulativeBoundary.traceEvents[-1].responseAccounting | ConvertTo-Json -Compress)"

    $zeroBudget = Invoke-TrialHost -Name 'inspection-zero-budget' -RuntimeDll $fakeDll `
        -Requests @((New-BudgetRequest -Operation 'search')) -AllowedOperations @('search') `
        -AdditionalCliArguments @('--fake-mode=budget-responses') -MaxInspectionResponseBytes ([long]0)
    $zeroExchange = Get-ExchangeTrace -Run $zeroBudget
    Assert-Check -Name 'zero budget rejects inspection before forwarding and does not observe a runtime response' -Passed (
        $zeroBudget.responses[0].error.code -eq 'TRIAL_INSPECTION_BUDGET_EXHAUSTED' -and
        $zeroExchange.requestDelivery.state -eq 'not-sent' -and $zeroExchange.executionState -eq 'not-executed' -and
        $zeroExchange.inspectionBudget.decision -eq 'rejected-before-forwarding' -and
        $zeroExchange.observedRuntimeResponse -eq $null -and
        $zeroBudget.traceEvents[-1].responseAccounting.rawValidRuntimeResponsePayloadUtf8Bytes -eq 0
    ) -Detail "exit=$($zeroBudget.process.exitCode); code=$($zeroBudget.responses[0].error.code); delivery=$($zeroExchange.requestDelivery.state)"

    $largeRaw = '{"ok":true,"text":"abcdefgh"}'
    $overBudget = Invoke-TrialHost -Name 'inspection-positive-remaining-over-budget' -RuntimeDll $fakeDll `
        -Requests @((New-BudgetRequest -Operation 'search' -Reply 'large')) -AllowedOperations @('search') `
        -AdditionalCliArguments @('--fake-mode=budget-responses') -MaxInspectionResponseBytes ([long]1)
    $overExchange = Get-ExchangeTrace -Run $overBudget
    $selectedErrorBytes = $utf8.GetBytes($overExchange.response.rawLine)
    $selectedErrorHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($selectedErrorBytes)).ToLowerInvariant()
    Assert-Check -Name 'positive remaining but oversized inspection response is withheld after execution with raw forensic bytes' -Passed (
        $overBudget.process.exitCode -eq 0 -and $overBudget.responses[0].error.code -eq 'TRIAL_INSPECTION_BUDGET_EXCEEDED' -and
        $overExchange.outcome -eq 'inspection-budget-exceeded' -and $overExchange.response.source -eq 'host' -and
        $overExchange.requestDelivery.state -eq 'confirmed' -and $overExchange.executionState -eq 'response-observed' -and
        $overExchange.observedRuntimeResponse.complete -eq $true -and $overExchange.observedRuntimeResponse.validJson -eq $true -and
        [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($overExchange.observedRuntimeResponse.base64)) -eq $largeRaw -and
        $overExchange.observedRuntimeResponse.sha256 -eq (Get-Utf8Sha256 $largeRaw) -and
        $overExchange.observedRuntimeResponse.canonical -eq $largeRaw -and
        $overExchange.response.payloadSha256 -eq $selectedErrorHash -and
        $overExchange.observedRuntimeResponse.sha256 -ne $overExchange.response.payloadSha256 -and
        $overBudget.traceEvents[-1].event -eq 'session-end' -and
        $overBudget.traceEvents[-1].responseAccounting.admittedInspectionPayloadUtf8Bytes -eq 0 -and
        $overBudget.traceEvents[-1].responseAccounting.rawValidRuntimeResponsePayloadUtf8Bytes -eq $utf8.GetByteCount($largeRaw)
    ) -Detail "rawBytes=$($overExchange.observedRuntimeResponse.utf8Bytes); selectedCode=$($overExchange.errorCode); execution=$($overExchange.executionState)"

    $diagnosticRaw = '{"ok":false,"kind":"error","text":"café","error":{"code":"SAMPLE","message":"café"}}'
    $diagnosticBytes = $utf8.GetByteCount($diagnosticRaw)
    $diagnosticBudget = Invoke-TrialHost -Name 'inspection-runtime-diagnostic' -RuntimeDll $fakeDll `
        -Requests @((New-BudgetRequest -Operation 'describe' -Reply 'diagnostic')) -AllowedOperations @('describe') `
        -AdditionalCliArguments @('--fake-mode=budget-responses') -MaxInspectionResponseBytes ([long]$diagnosticBytes)
    $diagnosticExchange = Get-ExchangeTrace -Run $diagnosticBudget
    Assert-Check -Name 'complete runtime diagnostic payload is included in exact inspection admission' -Passed (
        $diagnosticBudget.responses[0].error.code -eq 'SAMPLE' -and $diagnosticExchange.inspectionBudget.decision -eq 'admitted' -and
        $diagnosticExchange.observedRuntimeResponse.utf8Bytes -eq $diagnosticBytes -and
        $diagnosticExchange.observedRuntimeResponse.sha256 -eq (Get-Utf8Sha256 $diagnosticRaw) -and
        $diagnosticBudget.traceEvents[-1].responseAccounting.admittedInspectionPayloadUtf8Bytes -eq $diagnosticBytes
    ) -Detail "diagnosticBytes=$diagnosticBytes; total=$($diagnosticBudget.traceEvents[-1].responseAccounting.admittedInspectionPayloadUtf8Bytes)"

    $crlfRaw = "{`"ok`":true,`"text`":`"é`"}`r"
    $crlfBytes = $utf8.GetByteCount($crlfRaw)
    $crlfBudget = Invoke-TrialHost -Name 'inspection-utf8-crlf-boundary' -RuntimeDll $fakeDll `
        -Requests @((New-BudgetRequest -Operation 'search' -Reply 'unicode-crlf')) -AllowedOperations @('search') `
        -AdditionalCliArguments @('--fake-mode=budget-responses') -MaxInspectionResponseBytes ([long]$crlfBytes)
    $crlfExchange = Get-ExchangeTrace -Run $crlfBudget
    $crlfWireBytes = $utf8.GetBytes($crlfRaw + "`n")
    $crlfObserved = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($crlfExchange.observedRuntimeResponse.base64))
    $crlfWireHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($crlfWireBytes)).ToLowerInvariant()
    Assert-Check -Name 'UTF-8 inspection cap counts CR and excludes LF while preserving raw and wire hashes' -Passed (
        $crlfBudget.responses[0].ok -and $crlfExchange.inspectionBudget.decision -eq 'admitted' -and
        $crlfExchange.observedRuntimeResponse.utf8Bytes -eq $crlfBytes -and $crlfExchange.observedRuntimeResponse.wireUtf8Bytes -eq ($crlfBytes + 1) -and
        $crlfExchange.observedRuntimeResponse.sha256 -eq (Get-Utf8Sha256 $crlfRaw) -and
        $crlfExchange.observedRuntimeResponse.wireSha256 -eq $crlfWireHash -and
        $crlfObserved.EndsWith("`r") -and
        $crlfExchange.inspectionBudget.admittedPayloadUtf8Bytes -eq $crlfBytes
    ) -Detail "payloadBytes=$($crlfExchange.observedRuntimeResponse.utf8Bytes); wireBytes=$($crlfExchange.observedRuntimeResponse.wireUtf8Bytes); rawLineEndsCR=$($crlfObserved.EndsWith("`r"))"

    $freshBudget = Invoke-TrialHost -Name 'inspection-fresh-session-reset' -RuntimeDll $fakeDll `
        -Requests @((New-BudgetRequest -Operation 'search')) -AllowedOperations @('search') `
        -AdditionalCliArguments @('--fake-mode=budget-responses') -MaxInspectionResponseBytes ([long]$smallBytes)
    Assert-Check -Name 'a fresh host session starts with an independent zeroed inspection counter' -Passed (
        $freshBudget.responses[0].ok -and $freshBudget.traceEvents[0].responseAccounting.admittedInspectionPayloadUtf8Bytes -eq 0 -and
        (Get-ExchangeTrace -Run $freshBudget).inspectionBudget.admittedBeforePayloadUtf8Bytes -eq 0 -and
        $freshBudget.traceEvents[-1].responseAccounting.admittedInspectionPayloadUtf8Bytes -eq $smallBytes
    ) -Detail "start=$($freshBudget.traceEvents[0].responseAccounting.admittedInspectionPayloadUtf8Bytes); end=$($freshBudget.traceEvents[-1].responseAccounting.admittedInspectionPayloadUtf8Bytes)"

    $visibleNonInspection = Invoke-TrialHost -Name 'noninspection-results-visible-over-budget' -RuntimeDll $fakeDll `
        -Requests @((New-BudgetRequest -Operation 'commit' -Reply 'large'), (New-BudgetRequest -Operation 'test' -Reply 'large'), (New-BudgetRequest -Operation 'failed-tests' -Reply 'diagnostic')) `
        -AllowedOperations @('commit', 'test', 'failed-tests') -AdditionalCliArguments @('--fake-mode=budget-responses') -MaxInspectionResponseBytes ([long]1)
    $visibleExchanges = @($visibleNonInspection.traceEvents | Where-Object { $_.event -eq 'exchange' })
    Assert-Check -Name 'mutation and failed-tests responses stay visible beyond inspection budget' -Passed (
        $visibleNonInspection.responses.Count -eq 3 -and $visibleNonInspection.responses[0].text -eq 'abcdefgh' -and
        $visibleNonInspection.responses[1].text -eq 'abcdefgh' -and $visibleNonInspection.responses[2].error.code -eq 'SAMPLE' -and
        @($visibleExchanges | Where-Object { $_.inspectionBudget.operationClass -ne 'non-inspection' }).Count -eq 0 -and
        @($visibleExchanges | Where-Object { $_.inspectionBudget.decision -ne 'not-applicable' }).Count -eq 0 -and
        @($visibleExchanges | Where-Object { $_.response.source -ne 'runtime' -or $_.response.payloadUtf8Bytes -ne $_.observedRuntimeResponse.utf8Bytes }).Count -eq 0 -and
        $visibleNonInspection.traceEvents[-1].responseAccounting.admittedInspectionPayloadUtf8Bytes -eq 0 -and
        $visibleNonInspection.traceEvents[-1].responseAccounting.nonInspectionRuntimeResponsePayloadUtf8Bytes -gt 1
    ) -Detail "commit=$($visibleNonInspection.responses[0].text); test=$($visibleNonInspection.responses[1].text); failedTests=$($visibleNonInspection.responses[2].error.code); nonInspectionBytes=$($visibleNonInspection.traceEvents[-1].responseAccounting.nonInspectionRuntimeResponsePayloadUtf8Bytes)"

    $profileClassifier = Invoke-TrialHost -Name 'inspection-profile-classifier' -RuntimeDll $fakeDll `
        -Requests @((New-BudgetRequest -Operation 'inspect'), (New-BudgetRequest -Operation 'describe')) `
        -AllowedOperations @('inspect', 'describe') -AdditionalCliArguments @('--fake-mode=budget-responses') -MaxInspectionResponseBytes ([long]1)
    $profileExchanges = @($profileClassifier.traceEvents | Where-Object { $_.event -eq 'exchange' })
    $agentInspectionOperations = @($profileClassifier.traceEvents[0].inspectionBudget.operations)
    Assert-Check -Name 'AgentLang classifier is fixed to its audited exact operation set' -Passed (
        $profileExchanges[0].inspectionBudget.operationClass -eq 'non-inspection' -and
        $profileExchanges[1].inspectionBudget.operationClass -eq 'inspection' -and
        $agentInspectionOperations -notcontains 'inspect' -and $agentInspectionOperations -notcontains 'read' -and
        $agentInspectionOperations -contains 'search' -and $agentInspectionOperations -notcontains 'failed-tests'
    ) -Detail "inspect=$($profileExchanges[0].inspectionBudget.operationClass); describe=$($profileExchanges[1].inspectionBudget.operationClass); operations=$($agentInspectionOperations -join ',')"
    $conventionalClassifier = Invoke-TrialHost -Name 'conventional-profile-classifier' -RuntimeDll $fakeDll `
        -Requests @((New-BudgetRequest -Operation 'inspect')) -AllowedOperations @('inspect') -Profile conventional `
        -AdditionalCliArguments @('--fake-mode=budget-responses') -MaxInspectionResponseBytes ([long]$smallBytes)
    Assert-Check -Name 'conventional classifier uses only exact inspect/read/search names' -Passed (
        (Get-ExchangeTrace -Run $conventionalClassifier).inspectionBudget.operationClass -eq 'inspection' -and
        @($conventionalClassifier.traceEvents[0].inspectionBudget.operations).Count -eq 3 -and
        @($conventionalClassifier.traceEvents[0].inspectionBudget.operations) -contains 'inspect' -and
        @($conventionalClassifier.traceEvents[0].inspectionBudget.operations) -notcontains 'describe'
    ) -Detail ($conventionalClassifier.traceEvents[0].inspectionBudget.operations -join ',')

    $terminalControl = Invoke-TrialHost -Name 'terminal-control-accounting' -RuntimeDll $fakeDll `
        -Requests @((New-JsonLine @{ op = 'search'; query = ('x' * 256) })) -AllowedOperations @('search') `
        -AdditionalCliArguments @('--fake-mode=budget-responses') -MaxInspectionResponseBytes ([long]0) -MaxRequestBytes 64
    $terminalSelection = @($terminalControl.traceEvents | Where-Object { $_.event -eq 'host-response-selected' })[0]
    $terminalDelivery = @($terminalControl.traceEvents | Where-Object { $_.event -eq 'response-delivered' })[0]
    Assert-Check -Name 'terminal host control responses are selected and counted before post-flush delivery' -Passed (
        $terminalControl.responses[0].error.code -eq 'TRIAL_REQUEST_TOO_LARGE' -and
        $terminalSelection.scope -eq 'terminal-control' -and $terminalDelivery.scope -eq 'terminal-control' -and
        $terminalSelection.selectedResponse.payloadSha256 -eq $terminalDelivery.selectedResponse.payloadSha256 -and
        $terminalSelection.selectedResponse.payloadUtf8Bytes -eq $utf8.GetByteCount($terminalSelection.selectedResponse.rawLine) -and
        $terminalSelection.responseAccounting.selectedResponsePayloadUtf8Bytes -eq $terminalSelection.selectedResponse.payloadUtf8Bytes -and
        $terminalSelection.responseAccounting.stdoutPipeDeliveredResponseCount -eq 0 -and
        $terminalDelivery.responseAccounting.stdoutPipeDeliveredResponseCount -eq 1 -and
        $terminalControl.traceEvents[-1].responseAccounting.hostDenialControlResponsePayloadUtf8Bytes -eq $terminalSelection.selectedResponse.payloadUtf8Bytes -and
        $terminalControl.traceEvents[-1].responseAccounting.stdoutPipeDeliveredResponsePayloadUtf8Bytes -eq $terminalSelection.selectedResponse.payloadUtf8Bytes
    ) -Detail "selected=$($terminalSelection.selectedResponse.payloadUtf8Bytes); delivered=$($terminalDelivery.selectedResponse.payloadUtf8Bytes)"

    $negativeBudget = Invoke-TrialHost -Name 'inspection-negative-budget-rejected' -RuntimeDll $fakeDll `
        -Requests @() -AllowedOperations @('search') -MaxInspectionResponseBytes ([long](-1))
    Assert-Check -Name 'negative explicit inspection budget is rejected as configuration' -Passed (
        $negativeBudget.process.exitCode -ne 0 -and $negativeBudget.traceEvents.Count -eq 0
    ) -Detail "exit=$($negativeBudget.process.exitCode); stderr=$($negativeBudget.process.stderr)"

    $earlyResponse = Invoke-TrialHost -Name 'valid-response-before-write-timeout' -RuntimeDll $fakeDll `
        -Requests @((New-JsonLine @{ op = 'search'; data = ('x' * 250000) })) -AllowedOperations @('search') `
        -AdditionalCliArguments @('--fake-mode=response-before-write-timeout') -ExchangeTimeoutMilliseconds 2000 `
        -MaxRequestBytes 300000 -MaxInspectionResponseBytes ([long]1000) -OuterTimeoutMilliseconds 15000
    $earlyExchange = Get-ExchangeTrace -Run $earlyResponse
    Assert-Check -Name 'valid early runtime response remains forensic and non-admitted when request delivery times out' -Passed (
        $earlyResponse.process.exitCode -eq 124 -and $earlyResponse.responses[0].error.code -eq 'TRIAL_EXCHANGE_TIMEOUT' -and
        $earlyExchange.requestDelivery.state -eq 'uncertain' -and $earlyExchange.executionState -eq 'uncertain' -and
        $earlyExchange.observedRuntimeResponse.complete -eq $true -and $earlyExchange.observedRuntimeResponse.validJson -eq $true -and
        $earlyExchange.inspectionBudget.decision -eq 'observed-not-admitted-uncertain' -and
        $earlyExchange.inspectionBudget.admittedPayloadUtf8Bytes -eq 0 -and
        $earlyResponse.traceEvents[-1].responseAccounting.admittedInspectionPayloadUtf8Bytes -eq 0 -and
        $earlyResponse.traceEvents[-1].responseAccounting.rawValidRuntimeResponsePayloadUtf8Bytes -eq $smallBytes -and
        $earlyExchange.automaticRetry -like 'never*'
    ) -Detail "outcome=$($earlyExchange.outcome); delivery=$($earlyExchange.requestDelivery.state); execution=$($earlyExchange.executionState); budget=$($earlyExchange.inspectionBudget.decision)"

    $report = [ordered]@{
        schemaVersion = 1
        kind = 'subagent-trial-host-wrapper-verification'
        outcome = 'passed'
        createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
        repoRoot = $repoRoot
        testedHead = ([string]$version).Trim()
        workingTreeDirty = $dirty
        cliFiles = @($cliFiles)
        wrapperFiles = @($wrapperFiles)
        artifacts = $artifactRoot
        checks = @($checks)
        runs = @($runs | ForEach-Object {
            [ordered]@{
                name = $_.name
                projectPath = $_.projectPath
                tracePath = $_.tracePath
                runtimeDll = $_.runtimeDll
                runtimeDllSha256 = $_.runtimeDllSha256
                allowedOperations = $_.allowedOperations
                profile = $_.profile
                exitCode = $_.process.exitCode
                timedOut = $_.process.timedOut
                durationMilliseconds = $_.process.durationMilliseconds
                stdoutUtf8Bytes = $_.process.stdoutUtf8Bytes
                stderrUtf8Bytes = $_.process.stderrUtf8Bytes
                responseCount = $_.responses.Count
                responses = @(Get-BoundedEvidenceItems -Items $_.responses)
                responseEntriesOmitted = [math]::Max(0, $_.responses.Count - 32)
                traceEventCount = $_.traceEvents.Count
                traceEvents = @(Get-BoundedEvidenceItems -Items $_.traceEvents)
                traceEventEntriesOmitted = [math]::Max(0, $_.traceEvents.Count - 32)
            }
        })
        limitations = @(
            'This wrapper verifies transport and persistence mechanics; it does not run a model or measure model turns, tokens, or context windows.',
            'A requested protocol whitelist is enforced at this broker only. External agents can have other host tools unless the surrounding platform restricts them.',
            'Runtime child processes are not an OS sandbox. The wrapper launches only a host-selected DLL, project path, profile, and argument list.',
            'Any request with uncertain delivery or response has an unknown execution result; the operator must inspect authoritative stored state before deciding what to do next.'
        )
    }
    [System.IO.File]::WriteAllText($EvidencePath, (ConvertTo-Json -InputObject $report -Depth 32), $utf8)
    Write-Output ("Verification passed: {0} checks. Evidence: {1}" -f $checks.Count, $EvidencePath)
    exit 0
} catch {
    $caughtError = $_
    $failed = [ordered]@{
        schemaVersion = 1
        kind = 'subagent-trial-host-wrapper-verification'
        outcome = 'failed'
        createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
        repoRoot = $repoRoot
        testedHead = ([string]$version).Trim()
        workingTreeDirty = $dirty
        cliDll = $CliDll
        cliFiles = @($cliFiles)
        wrapperFiles = @($wrapperFiles)
        artifacts = $artifactRoot
        error = $caughtError.Exception.Message
        checks = @($checks)
        runs = @($runs | ForEach-Object {
            [ordered]@{
                name = $_.name
                tracePath = $_.tracePath
                exitCode = $_.process.exitCode
                timedOut = $_.process.timedOut
                durationMilliseconds = $_.process.durationMilliseconds
                responseCount = $_.responses.Count
                responses = @(Get-BoundedEvidenceItems -Items $_.responses -MaximumItems 12 -MaximumJsonCharacters 4096)
                responseEntriesOmitted = [math]::Max(0, $_.responses.Count - 12)
                traceEventCount = $_.traceEvents.Count
                traceEvents = @(Get-BoundedEvidenceItems -Items $_.traceEvents -MaximumItems 12 -MaximumJsonCharacters 8192)
                traceEventEntriesOmitted = [math]::Max(0, $_.traceEvents.Count - 12)
            }
        })
    }
    if (-not (Test-Path -LiteralPath $EvidencePath)) {
        [System.IO.File]::WriteAllText($EvidencePath, (ConvertTo-Json -InputObject $failed -Depth 24), $utf8)
    }
    [Console]::Error.WriteLine(("Verification failed; evidence: {0}`n{1}" -f $EvidencePath, $caughtError.Exception.Message))
    exit 1
}
