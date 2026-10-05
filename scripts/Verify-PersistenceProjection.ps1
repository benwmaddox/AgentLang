#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$CliDll = (Join-Path (Join-Path $PSScriptRoot '..') 'src/AgentLang.Cli/bin/Release/net9.0/AgentLang.Cli.dll'),
    [string]$EvidencePath = '.agentlang/reports/projection-check.json',
    [ValidateRange(5, 600)]
    [int]$TimeoutSeconds = 60,
    [ValidateRange(1, 64)]
    [int]$MaxOutputMiB = 2
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not ('AgentLangProjectionVerifier.ProcessRunner' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AgentLangProjectionVerifier
{
    public sealed class ProcessResult
    {
        public string Stdout { get; set; }
        public string Stderr { get; set; }
        public int ExitCode { get; set; }
        public bool TimedOut { get; set; }
        public bool OutputLimitExceeded { get; set; }
        public long DurationMilliseconds { get; set; }
        public string InputError { get; set; }
    }

    internal sealed class OutputBudget
    {
        private readonly int _maximumBytes;
        private int _usedBytes;
        private int _exceeded;

        public OutputBudget(int maximumBytes)
        {
            _maximumBytes = maximumBytes;
        }

        public bool Exceeded { get { return Volatile.Read(ref _exceeded) != 0; } }

        public bool TryAdd(int bytes, Process process)
        {
            if (Exceeded) return false;
            int total = Interlocked.Add(ref _usedBytes, bytes);
            if (total <= _maximumBytes) return true;
            Interlocked.Exchange(ref _exceeded, 1);
            try { if (!process.HasExited) process.Kill(true); } catch { }
            return false;
        }
    }

    public static class ProcessRunner
    {
        private static string ReadBounded(System.IO.StreamReader reader, OutputBudget budget, Process process)
        {
            var builder = new StringBuilder();
            var buffer = new char[4096];
            int count;
            while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                int bytes = Encoding.UTF8.GetByteCount(buffer, 0, count);
                if (!budget.TryAdd(bytes, process)) break;
                builder.Append(buffer, 0, count);
            }
            return builder.ToString();
        }

        private static void Kill(Process process)
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
        }

        public static ProcessResult Run(string fileName, string[] arguments, string input, int timeoutMilliseconds, int maximumOutputBytes)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

            using (var process = new Process())
            {
                process.StartInfo = startInfo;
                var stopwatch = Stopwatch.StartNew();
                if (!process.Start()) throw new InvalidOperationException("The CLI process did not start.");
                var budget = new OutputBudget(maximumOutputBytes);
                var stdoutTask = Task.Factory.StartNew(
                    () => ReadBounded(process.StandardOutput, budget, process),
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                var stderrTask = Task.Factory.StartNew(
                    () => ReadBounded(process.StandardError, budget, process),
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                string inputError = null;
                try
                {
                    using (var writer = process.StandardInput)
                    {
                        writer.Write(input);
                        writer.Flush();
                    }
                }
                catch (Exception ex)
                {
                    inputError = ex.GetType().Name + ": " + ex.Message;
                    Kill(process);
                }

                bool timedOut = !process.WaitForExit(timeoutMilliseconds);
                if (timedOut) Kill(process);
                if (process.HasExited) process.WaitForExit();
                if (!Task.WaitAll(new Task[] { stdoutTask, stderrTask }, 10000))
                {
                    Kill(process);
                    throw new TimeoutException("The CLI output streams did not close after process exit.");
                }

                stopwatch.Stop();
                return new ProcessResult
                {
                    Stdout = stdoutTask.Result,
                    Stderr = stderrTask.Result,
                    ExitCode = process.HasExited ? process.ExitCode : -1,
                    TimedOut = timedOut,
                    OutputLimitExceeded = budget.Exceeded,
                    DurationMilliseconds = stopwatch.ElapsedMilliseconds,
                    InputError = inputError
                };
            }
        }
    }
}
'@
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$cliPath = if ([IO.Path]::IsPathRooted($CliDll)) { [IO.Path]::GetFullPath($CliDll) } else { [IO.Path]::GetFullPath((Join-Path $repoRoot $CliDll)) }
$evidenceFullPath = if ([IO.Path]::IsPathRooted($EvidencePath)) { [IO.Path]::GetFullPath($EvidencePath) } else { [IO.Path]::GetFullPath((Join-Path $repoRoot $EvidencePath)) }
$maxOutputBytes = [int64]$MaxOutputMiB * 1MB

function Assert-NoReparseComponents {
    param([Parameter(Mandatory)][string]$Path)

    $current = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrEmpty($current)) {
        if (Test-Path -LiteralPath $current) {
            $attributes = [IO.File]::GetAttributes($current)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing a reparse-point path component: $current"
            }
        }
        $parent = [IO.Directory]::GetParent($current)
        if ($null -eq $parent) { break }
        $current = $parent.FullName
    }
}

function Ensure-SafeDirectory {
    param([Parameter(Mandatory)][string]$Path)

    Assert-NoReparseComponents -Path $Path
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
    Assert-NoReparseComponents -Path $Path
}

function Save-Report {
    $json = ConvertTo-Json -InputObject $script:report -Depth 80
    [IO.File]::WriteAllText($script:evidenceFullPath, $json, [Text.UTF8Encoding]::new($false))
}

function Add-Check {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][bool]$Passed,
        [Parameter(Mandatory)][string]$Message,
        [string]$Session,
        [int]$ExchangeIndex = -1
    )

    $check = [ordered]@{
        name = $Name
        passed = $Passed
        message = $Message
    }
    if ($Session) { $check.session = $Session }
    if ($ExchangeIndex -ge 0) { $check.exchangeIndex = $ExchangeIndex }
    $script:report.checks = @($script:report.checks) + @($check)

    if ($Session) {
        $sessionItem = $script:report.sessions | Where-Object { $_.name -eq $Session } | Select-Object -First 1
        if ($null -ne $sessionItem) {
            if ($ExchangeIndex -ge 0 -and $ExchangeIndex -lt $sessionItem.exchanges.Count) {
                $exchange = $sessionItem.exchanges[$ExchangeIndex]
                $exchange.checks = @($exchange.checks) + @($check)
            } else {
                $sessionItem.checks = @($sessionItem.checks) + @($check)
            }
        }
    }
    Save-Report
    if (-not $Passed) { throw "Verification failed: $Name — $Message" }
}

function ConvertTo-RequestLine {
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Request)
    ConvertTo-Json -InputObject $Request -Depth 40 -Compress
}

function New-Request {
    param([Parameter(Mandatory)][string]$Operation, [System.Collections.IDictionary]$Arguments = @{})
    $request = [ordered]@{ op = $Operation }
    foreach ($key in $Arguments.Keys) { $request[$key] = $Arguments[$key] }
    $request
}

function Invoke-CliSession {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$ProjectPath,
        [Parameter(Mandatory)][System.Collections.IDictionary[]]$Requests
    )

    Assert-NoReparseComponents -Path $ProjectPath
    $lines = @($Requests | ForEach-Object { ConvertTo-RequestLine -Request $_ })
    $inputText = [string]::Join("`n", $lines) + "`n"
    $inputBytes = [Text.Encoding]::UTF8.GetByteCount($inputText)
    if ($inputBytes -gt 1MB) { throw "Request batch exceeds the 1 MiB input limit for session '$Name'." }

    $arguments = @($cliPath, '--project', $ProjectPath, '--jsonl')
    $processResult = [AgentLangProjectionVerifier.ProcessRunner]::Run(
        'dotnet', $arguments, $inputText, $TimeoutSeconds * 1000, [int]$maxOutputBytes)

    $stdoutLines = @($processResult.Stdout -split "`r?`n" | Where-Object { $_.Length -gt 0 })
    $session = [ordered]@{
        name = $Name
        projectPath = $ProjectPath
        process = [ordered]@{
            exitCode = $processResult.ExitCode
            timedOut = $processResult.TimedOut
            outputLimitExceeded = $processResult.OutputLimitExceeded
            durationMilliseconds = $processResult.DurationMilliseconds
            inputError = $processResult.InputError
            stderr = $processResult.Stderr
            stdoutLines = $stdoutLines
        }
        exchanges = @()
        checks = @()
    }
    $script:report.sessions = @($script:report.sessions) + @($session)
    Save-Report

    Add-Check -Name "$Name process completed" `
        -Passed (-not $processResult.TimedOut -and -not $processResult.OutputLimitExceeded -and $null -eq $processResult.InputError -and $processResult.ExitCode -eq 0) `
        -Message ("exit={0}, timeout={1}, outputLimit={2}, durationMs={3}" -f $processResult.ExitCode, $processResult.TimedOut, $processResult.OutputLimitExceeded, $processResult.DurationMilliseconds) `
        -Session $Name
    Add-Check -Name "$Name response count matches requests" `
        -Passed ($stdoutLines.Count -eq $Requests.Count) `
        -Message ("received {0} response line(s) for {1} request(s)" -f $stdoutLines.Count, $Requests.Count) `
        -Session $Name

    for ($index = 0; $index -lt $Requests.Count; $index++) {
        $rawResponse = if ($index -lt $stdoutLines.Count) { $stdoutLines[$index] } else { $null }
        $parsedResponse = $null
        $parseError = $null
        if ($null -ne $rawResponse) {
            try { $parsedResponse = ConvertFrom-Json -InputObject $rawResponse -AsHashtable -Depth 40 }
            catch { $parseError = $_.Exception.Message }
        }
        $exchange = [ordered]@{
            index = $index
            request = $Requests[$index]
            requestLine = $lines[$index]
            responseRaw = $rawResponse
            response = $parsedResponse
            parseError = $parseError
            checks = @()
        }
        $session.exchanges = @($session.exchanges) + @($exchange)
        $parsePassed = $null -ne $parsedResponse -and $parsedResponse -is [System.Collections.IDictionary]
        Add-Check -Name "$Name response $index is valid JSON" -Passed $parsePassed -Message $(if ($parsePassed) { 'valid JSON response object' } else { "missing or invalid JSON response: $parseError" }) -Session $Name -ExchangeIndex $index
    }

    return $session
}

function Get-Response {
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Session, [Parameter(Mandatory)][int]$Index)
    $Session.exchanges[$Index].response
}

function Assert-OkResponse {
    param([string]$Session, [int]$Index, [string]$Name)
    $response = Get-Response -Session ($script:report.sessions | Where-Object { $_.name -eq $Session } | Select-Object -First 1) -Index $Index
    $passed = $null -ne $response -and $response.ok -eq $true
    $message = if ($passed) { [string]$response.text } else {
        $code = if ($null -ne $response.error) { [string]$response.error.code } else { 'no-response' }
        "${code}: $($response.text)"
    }
    Add-Check -Name $Name -Passed $passed -Message $message -Session $Session -ExchangeIndex $Index
    $response
}

function Assert-ErrorResponse {
    param([string]$Session, [int]$Index, [string]$ExpectedCode, [string]$Name)
    $response = Get-Response -Session ($script:report.sessions | Where-Object { $_.name -eq $Session } | Select-Object -First 1) -Index $Index
    $actualCode = if ($null -ne $response -and $null -ne $response.error) { [string]$response.error.code } else { 'no-response' }
    $passed = $null -ne $response -and $response.ok -eq $false -and $actualCode -eq $ExpectedCode
    Add-Check -Name $Name -Passed $passed -Message "expected $ExpectedCode; observed $actualCode" -Session $Session -ExchangeIndex $Index
    $response
}

function Assert-CheckValue {
    param([string]$Session, [int]$Index, [string]$Name, [bool]$Passed, [string]$Message)
    Add-Check -Name $Name -Passed $Passed -Message $Message -Session $Session -ExchangeIndex $Index
}

function New-ProjectDirectory {
    param([Parameter(Mandatory)][string]$Name)
    $path = Join-Path $script:runRoot $Name
    if (Test-Path -LiteralPath $path) { throw "Unique verification path already exists: $path" }
    Ensure-SafeDirectory -Path $path
    $path
}

function New-ReportMetadata {
    $binaryHashes = [ordered]@{}
    $binaryDirectory = Split-Path -Parent $script:cliPath
    foreach ($file in Get-ChildItem -LiteralPath $binaryDirectory -Filter '*.dll' -File | Sort-Object Name) {
        Assert-NoReparseComponents -Path $file.FullName
        $binaryHashes[$file.Name] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }

    $head = $null
    $dirty = $null
    try {
        $headOutput = & git -C $script:repoRoot rev-parse HEAD 2>$null
        if ($LASTEXITCODE -eq 0) {
            $head = [string]($headOutput | Select-Object -First 1)
            $statusOutput = & git -C $script:repoRoot status --porcelain 2>$null
            if ($LASTEXITCODE -eq 0) { $dirty = @($statusOutput).Count -gt 0 }
        }
    } catch { }

    [ordered]@{
        schemaVersion = 1
        startedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        state = 'running'
        repoRoot = $script:repoRoot
        testedHead = $head
        workingTreeDirty = $dirty
        cliDll = $script:cliPath
        binarySha256 = $binaryHashes
        verificationRoot = $script:runRoot
        limits = [ordered]@{
            timeoutSeconds = $script:TimeoutSeconds
            maxOutputBytesPerProcess = $script:maxOutputBytes
            maxInputBytesPerProcess = 1MB
        }
        sessions = @()
        checks = @()
        failure = $null
        completedUtc = $null
    }
}

try {
    if (-not (Test-Path -LiteralPath $cliPath -PathType Leaf)) { throw "Release CLI DLL not found: $cliPath" }
    Assert-NoReparseComponents -Path $repoRoot
    Assert-NoReparseComponents -Path $cliPath

    $script:repoRoot = $repoRoot
    $script:cliPath = $cliPath
    $script:TimeoutSeconds = $TimeoutSeconds
    $script:maxOutputBytes = $maxOutputBytes
    $reportDirectory = Split-Path -Parent $evidenceFullPath
    Ensure-SafeDirectory -Path $reportDirectory
    Assert-NoReparseComponents -Path $evidenceFullPath
    $script:evidenceFullPath = $evidenceFullPath
    $script:runRoot = Join-Path (Join-Path $repoRoot '.agentlang') ('projection-verification-' + [Guid]::NewGuid().ToString('N'))
    Ensure-SafeDirectory -Path $script:runRoot
    $script:report = New-ReportMetadata
    Save-Report

    $metadataProject = New-ProjectDirectory -Name 'metadata-closure'
    $metadataSource = @'
record ProbeToken
    field value Int
end

word candidate-helper : Int -> Int
    effects none
    1 add
end

test candidate-helper/basic
    1 candidate-helper
    => 2
end

word candidate-example-helper : Int -> Int
    effects none
    5 add
end

test candidate-example-helper/basic
    1 candidate-example-helper
    => 6
end

word metadata-target : Int -> Int
    effects none
    1 add
end

test metadata-target/helper-case
    option.none<ProbeToken> drop
    41 candidate-helper metadata-target
    => 43
end

example metadata-target/helper-example
    option.none<ProbeToken> drop
    41 candidate-example-helper metadata-target
    => 47
end

word unrelated-candidate : Int -> Int
    effects none
    2 add
end

test unrelated-candidate/basic
    1 unrelated-candidate
    => 3
end
'@
    $session = Invoke-CliSession -Name 'metadata-commit' -ProjectPath $metadataProject -Requests @(
        (New-Request -Operation 'define' -Arguments ([ordered]@{ source = $metadataSource })),
        (New-Request -Operation 'commit' -Arguments ([ordered]@{ word = 'metadata-target' })),
        (New-Request -Operation 'describe' -Arguments ([ordered]@{ word = 'unrelated-candidate' })),
        (New-Request -Operation 'tests' -Arguments ([ordered]@{ word = 'unrelated-candidate' }))
    )
    Assert-OkResponse -Session 'metadata-commit' -Index 0 -Name 'define metadata closure fixture' | Out-Null
    Assert-OkResponse -Session 'metadata-commit' -Index 1 -Name 'commit selected target with test/example dependencies' | Out-Null
    $unrelatedDescription = Assert-OkResponse -Session 'metadata-commit' -Index 2 -Name 'unrelated candidate remains staged'
    Assert-CheckValue -Session 'metadata-commit' -Index 2 -Name 'selected metadata closure excludes unrelated candidate words' -Passed ($unrelatedDescription.data.status -eq 'candidate') -Message "observed status '$($unrelatedDescription.data.status)'"
    $unrelatedTests = Assert-OkResponse -Session 'metadata-commit' -Index 3 -Name 'unrelated candidate tests remain in the staging session'
    Assert-CheckValue -Session 'metadata-commit' -Index 3 -Name 'unrelated staged test metadata was not discarded in memory' -Passed (@($unrelatedTests.data).Count -eq 1) -Message "observed $(@($unrelatedTests.data).Count) attached test(s)"

    $reload = Invoke-CliSession -Name 'metadata-reload' -ProjectPath $metadataProject -Requests @(
        (New-Request -Operation 'describe' -Arguments ([ordered]@{ word = 'candidate-helper' })),
        (New-Request -Operation 'describe' -Arguments ([ordered]@{ word = 'candidate-example-helper' })),
        (New-Request -Operation 'tests' -Arguments ([ordered]@{ word = 'metadata-target' })),
        (New-Request -Operation 'test' -Arguments ([ordered]@{ word = 'metadata-target' })),
        (New-Request -Operation 'test' -Arguments ([ordered]@{ word = 'candidate-example-helper' })),
        (New-Request -Operation 'examples' -Arguments ([ordered]@{ word = 'metadata-target' })),
        (New-Request -Operation 'source' -Arguments ([ordered]@{ word = 'unrelated-candidate' })),
        (New-Request -Operation 'storage.status')
    )
    $helper = Assert-OkResponse -Session 'metadata-reload' -Index 0 -Name 'fresh process loads helper included by selected metadata closure'
    Assert-CheckValue -Session 'metadata-reload' -Index 0 -Name 'metadata test helper is durable' -Passed ($helper.data.status -eq 'persistent') -Message "observed status '$($helper.data.status)'"
    $exampleHelper = Assert-OkResponse -Session 'metadata-reload' -Index 1 -Name 'fresh process loads helper included by selected example metadata'
    Assert-CheckValue -Session 'metadata-reload' -Index 1 -Name 'metadata example-only helper is durable' -Passed ($exampleHelper.data.status -eq 'persistent') -Message "observed status '$($exampleHelper.data.status)'"
    $targetTests = Assert-OkResponse -Session 'metadata-reload' -Index 2 -Name 'fresh process retains selected target test metadata'
    Assert-CheckValue -Session 'metadata-reload' -Index 2 -Name 'target test survives reload' -Passed (@($targetTests.data) -contains 'helper-case') -Message "observed tests: $(@($targetTests.data) -join ', ')"
    $targetTestRun = Assert-OkResponse -Session 'metadata-reload' -Index 3 -Name 'fresh process runs selected target test'
    $targetTestResults = @($targetTestRun.data.results)
    Assert-CheckValue -Session 'metadata-reload' -Index 3 -Name 'target test passes after reload' -Passed ($targetTestResults.Count -eq 1 -and $targetTestResults[0].passed -eq $true) -Message "observed $($targetTestResults.Count) test result(s)"
    $exampleHelperTest = Assert-OkResponse -Session 'metadata-reload' -Index 4 -Name 'fresh process runs example-only helper test'
    $exampleHelperResults = @($exampleHelperTest.data.results)
    Assert-CheckValue -Session 'metadata-reload' -Index 4 -Name 'example-only helper test passes after reload' -Passed ($exampleHelperResults.Count -eq 1 -and $exampleHelperResults[0].passed -eq $true) -Message "observed $($exampleHelperResults.Count) helper test result(s)"
    $targetExamples = Assert-OkResponse -Session 'metadata-reload' -Index 5 -Name 'fresh process retains selected target examples'
    Assert-CheckValue -Session 'metadata-reload' -Index 5 -Name 'target example survives reload' -Passed (@($targetExamples.data) -contains 'helper-example') -Message "observed examples: $(@($targetExamples.data) -join ', ')"
    Assert-ErrorResponse -Session 'metadata-reload' -Index 6 -ExpectedCode 'NAME_UNKNOWN_WORD' -Name 'unrelated candidate is absent from fresh committed projection' | Out-Null
    $storage = Assert-OkResponse -Session 'metadata-reload' -Index 7 -Name 'inspect metadata closure storage status'
    Assert-CheckValue -Session 'metadata-reload' -Index 7 -Name 'metadata closure reloads manifest authority' -Passed ($storage.data.authority -eq 'manifest' -and [int64]$storage.data.generation -gt 0 -and -not $storage.data.exportWarning) -Message "authority=$($storage.data.authority), generation=$($storage.data.generation), exportWarning=$($storage.data.exportWarning)"

    $replacementSource = @'
word candidate-helper : Int -> Int
    effects none
    2 add
end

test candidate-helper/basic
    1 candidate-helper
    => 3
end

test metadata-target/helper-case
    option.none<ProbeToken> drop
    41 candidate-helper metadata-target
    => 44
end

example metadata-target/helper-example
    option.none<ProbeToken> drop
    41 candidate-helper metadata-target
    => 44
end
'@
    $replacementStage = Invoke-CliSession -Name 'metadata-replacement-commit' -ProjectPath $metadataProject -Requests @(
        (New-Request -Operation 'define' -Arguments ([ordered]@{ source = $replacementSource })),
        (New-Request -Operation 'commit' -Arguments ([ordered]@{ word = 'metadata-target' }))
    )
    Assert-OkResponse -Session 'metadata-replacement-commit' -Index 0 -Name 'stage helper replacement referenced only by target metadata' | Out-Null
    Assert-OkResponse -Session 'metadata-replacement-commit' -Index 1 -Name 'commit metadata-selected helper replacement' | Out-Null

    $replacementReload = Invoke-CliSession -Name 'metadata-replacement-reload' -ProjectPath $metadataProject -Requests @(
        (New-Request -Operation 'describe' -Arguments ([ordered]@{ word = 'candidate-helper' })),
        (New-Request -Operation 'history' -Arguments ([ordered]@{ word = 'candidate-helper' })),
        (New-Request -Operation 'eval' -Arguments ([ordered]@{ code = '41 candidate-helper metadata-target' })),
        (New-Request -Operation 'test' -Arguments ([ordered]@{ word = 'metadata-target' })),
        (New-Request -Operation 'test' -Arguments ([ordered]@{ word = 'candidate-helper' })),
        (New-Request -Operation 'examples' -Arguments ([ordered]@{ word = 'metadata-target' }))
    )
    $replacementDescription = Assert-OkResponse -Session 'metadata-replacement-reload' -Index 0 -Name 'fresh process loads staged helper replacement'
    Assert-CheckValue -Session 'metadata-replacement-reload' -Index 0 -Name 'selected helper replacement is exactly revision two' -Passed ([int]$replacementDescription.data.revision -eq 2 -and $replacementDescription.data.status -eq 'persistent') -Message "status=$($replacementDescription.data.status), revision=$($replacementDescription.data.revision)"
    $history = Assert-OkResponse -Session 'metadata-replacement-reload' -Index 1 -Name 'fresh process loads immutable helper history'
    $historyEntries = @($history.data)
    $latestHistory = if ($historyEntries.Count -gt 0) { $historyEntries[$historyEntries.Count - 1] } else { $null }
    $latestSourceHasReplacement = $null -ne $latestHistory -and $latestHistory.source.Contains('revision 2', [StringComparison]::Ordinal) -and $latestHistory.source.Contains("`n    2`n    add", [StringComparison]::Ordinal)
    Assert-CheckValue -Session 'metadata-replacement-reload' -Index 1 -Name 'helper revision two is present in durable history' -Passed ($null -ne $latestHistory -and [int]$latestHistory.revision -eq 2 -and $latestSourceHasReplacement) -Message "history revisions: $(($historyEntries | ForEach-Object { $_.revision }) -join ', ')"
    $replacementValue = Assert-OkResponse -Session 'metadata-replacement-reload' -Index 2 -Name 'fresh process evaluates target using the replacement helper'
    $evalStack = @($replacementValue.data.stack)
    Assert-CheckValue -Session 'metadata-replacement-reload' -Index 2 -Name 'exact staged helper behavior survives reload' -Passed ($evalStack.Count -eq 1 -and $evalStack[0] -eq '44') -Message "observed stack: $($evalStack -join ', ')"
    $replacementTargetTest = Assert-OkResponse -Session 'metadata-replacement-reload' -Index 3 -Name 'fresh process runs target test after replacement'
    $replacementTargetResults = @($replacementTargetTest.data.results)
    Assert-CheckValue -Session 'metadata-replacement-reload' -Index 3 -Name 'updated target test passes on durable helper revision' -Passed ($replacementTargetResults.Count -eq 1 -and $replacementTargetResults[0].passed -eq $true) -Message "observed $($replacementTargetResults.Count) target test result(s)"
    $replacementHelperTest = Assert-OkResponse -Session 'metadata-replacement-reload' -Index 4 -Name 'fresh process runs helper test after replacement'
    $replacementHelperResults = @($replacementHelperTest.data.results)
    Assert-CheckValue -Session 'metadata-replacement-reload' -Index 4 -Name 'updated helper test passes after replacement' -Passed ($replacementHelperResults.Count -eq 1 -and $replacementHelperResults[0].passed -eq $true) -Message "observed $($replacementHelperResults.Count) helper test result(s)"
    $replacementExamples = Assert-OkResponse -Session 'metadata-replacement-reload' -Index 5 -Name 'fresh process retains selected example after replacement'
    Assert-CheckValue -Session 'metadata-replacement-reload' -Index 5 -Name 'replacement example metadata survives reload' -Passed (@($replacementExamples.data) -contains 'helper-example') -Message "observed examples: $(@($replacementExamples.data) -join ', ')"

    $libraryProject = New-ProjectDirectory -Name 'library-coverage'
    $librarySource = @'
word covered-choice : Bool -> Int
    effects none
    if
        1
    else
        2
    end
end

test covered-choice/true
    true covered-choice
    => 1
end

test covered-choice/false
    false covered-choice
    => 2
end
'@
    $libraryCommit = Invoke-CliSession -Name 'library-commit' -ProjectPath $libraryProject -Requests @(
        (New-Request -Operation 'define' -Arguments ([ordered]@{ source = $librarySource })),
        (New-Request -Operation 'commit' -Arguments ([ordered]@{ word = 'covered-choice'; library = $true }))
    )
    Assert-OkResponse -Session 'library-commit' -Index 0 -Name 'define complete-coverage library fixture' | Out-Null
    Assert-OkResponse -Session 'library-commit' -Index 1 -Name 'commit fully covered library word' | Out-Null

    $libraryReload = Invoke-CliSession -Name 'library-reload' -ProjectPath $libraryProject -Requests @(
        (New-Request -Operation 'test' -Arguments ([ordered]@{ word = 'covered-choice' })),
        (New-Request -Operation 'describe' -Arguments ([ordered]@{ word = 'covered-choice' })),
        (New-Request -Operation 'tests' -Arguments ([ordered]@{ word = 'covered-choice' }))
    )
    $libraryTests = Assert-OkResponse -Session 'library-reload' -Index 0 -Name 'fresh process runs library tests'
    $libraryTestResults = @($libraryTests.data.results)
    Assert-CheckValue -Session 'library-reload' -Index 0 -Name 'both library branch tests survive and pass' -Passed ($libraryTestResults.Count -eq 2 -and @($libraryTestResults | Where-Object { $_.passed -ne $true }).Count -eq 0) -Message "observed $($libraryTestResults.Count) test result(s)"
    $libraryDescription = Assert-OkResponse -Session 'library-reload' -Index 1 -Name 'fresh process describes library coverage'
    $coverage = $libraryDescription.data.coverage
    $coverageComplete = [int]$coverage.instructionsTotal -gt 0 -and [int]$coverage.instructionsCovered -eq [int]$coverage.instructionsTotal -and [int]$coverage.branchesTotal -gt 0 -and [int]$coverage.branchesCovered -eq [int]$coverage.branchesTotal
    Assert-CheckValue -Session 'library-reload' -Index 1 -Name 'library own-body coverage remains complete after reload' -Passed ($coverageComplete -and $libraryDescription.data.maturity -eq 'library') -Message "maturity=$($libraryDescription.data.maturity), instructions=$($coverage.instructionsCovered)/$($coverage.instructionsTotal), branches=$($coverage.branchesCovered)/$($coverage.branchesTotal)"
    $libraryTestNames = Assert-OkResponse -Session 'library-reload' -Index 2 -Name 'fresh process lists attached library tests'
    Assert-CheckValue -Session 'library-reload' -Index 2 -Name 'library test metadata survives reload' -Passed (@($libraryTestNames.data).Count -eq 2 -and @($libraryTestNames.data) -contains 'true' -and @($libraryTestNames.data) -contains 'false') -Message "observed tests: $(@($libraryTestNames.data) -join ', ')"

    $temporaryProject = New-ProjectDirectory -Name 'temporary-metadata'
    $temporarySource = @'
word temporary-test-helper : Int -> Int
    effects none
    1 add
end
'@
    $temporaryTargetSource = @'
word temporary-metadata-target : Int -> Int
    effects none
    1 add
end

test temporary-metadata-target/uses-temporary
    1 temporary-test-helper temporary-metadata-target
    => 3
end
'@
    $temporarySession = Invoke-CliSession -Name 'temporary-metadata-rejection' -ProjectPath $temporaryProject -Requests @(
        (New-Request -Operation 'define' -Arguments ([ordered]@{ source = $temporarySource; temporary = $true })),
        (New-Request -Operation 'define' -Arguments ([ordered]@{ source = $temporaryTargetSource })),
        (New-Request -Operation 'commit' -Arguments ([ordered]@{ word = 'temporary-metadata-target' })),
        (New-Request -Operation 'storage.status')
    )
    Assert-OkResponse -Session 'temporary-metadata-rejection' -Index 0 -Name 'define temporary metadata helper' | Out-Null
    Assert-OkResponse -Session 'temporary-metadata-rejection' -Index 1 -Name 'define target with temporary metadata reference' | Out-Null
    $temporaryRejection = Assert-ErrorResponse -Session 'temporary-metadata-rejection' -Index 2 -ExpectedCode 'COMMIT_TEMPORARY_TEST_DEPENDENCY' -Name 'reject temporary test dependency before publication'
    $temporaryActual = @($temporaryRejection.error.actual)
    Assert-CheckValue -Session 'temporary-metadata-rejection' -Index 2 -Name 'temporary dependency error identifies helper' -Passed ($temporaryActual -contains 'temporary-test-helper') -Message "reported dependencies: $($temporaryActual -join ', ')"
    $beforeReloadStatus = Assert-OkResponse -Session 'temporary-metadata-rejection' -Index 3 -Name 'inspect storage after rejected temporary commit'
    Assert-CheckValue -Session 'temporary-metadata-rejection' -Index 3 -Name 'rejected commit leaves empty authority unchanged' -Passed ($beforeReloadStatus.data.authority -eq 'empty' -and [int64]$beforeReloadStatus.data.generation -eq 0) -Message "authority=$($beforeReloadStatus.data.authority), generation=$($beforeReloadStatus.data.generation)"

    $temporaryReload = Invoke-CliSession -Name 'temporary-metadata-fresh-check' -ProjectPath $temporaryProject -Requests @(
        (New-Request -Operation 'source' -Arguments ([ordered]@{ word = 'temporary-metadata-target' })),
        (New-Request -Operation 'storage.status')
    )
    Assert-ErrorResponse -Session 'temporary-metadata-fresh-check' -Index 0 -ExpectedCode 'NAME_UNKNOWN_WORD' -Name 'fresh project remains unchanged after rejected commit' | Out-Null
    $afterReloadStatus = Assert-OkResponse -Session 'temporary-metadata-fresh-check' -Index 1 -Name 'recheck fresh storage authority'
    Assert-CheckValue -Session 'temporary-metadata-fresh-check' -Index 1 -Name 'fresh project still has no published manifest' -Passed ($afterReloadStatus.data.authority -eq 'empty' -and [int64]$afterReloadStatus.data.generation -eq 0) -Message "authority=$($afterReloadStatus.data.authority), generation=$($afterReloadStatus.data.generation)"

    $script:report.state = 'passed'
} catch {
    if ($null -ne $script:report) {
        $script:report.state = 'failed'
        $script:report.failure = $_.Exception.Message
    }
    Write-Error $_
    exit 1
} finally {
    if ($null -ne $script:report) {
        $script:report.completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        try { Save-Report } catch { Write-Warning "Could not save verification report: $($_.Exception.Message)" }
    }
}

Write-Output "Persistence projection verification passed. Evidence: $evidenceFullPath"
