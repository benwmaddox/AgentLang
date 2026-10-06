#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$ReferenceCliDll = (Join-Path (Join-Path $PSScriptRoot '..') 'src/AgentLang.Cli/bin/Release/net9.0/AgentLang.Cli.dll'),
    [string]$CandidateCliDll = (Join-Path (Join-Path $PSScriptRoot '..') 'src/AgentLang.Cli/bin/Release/net9.0/AgentLang.Cli.dll'),
    [string]$EvidencePath = '.agentlang/reports/ir-parity.json',
    [ValidateRange(5, 600)]
    [int]$TimeoutSeconds = 60,
    [ValidateRange(1, 64)]
    [int]$MaxOutputMiB = 4
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not ('AgentLangIrParityVerifier.ProcessRunner' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AgentLangIrParityVerifier
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

        public OutputBudget(int maximumBytes) { _maximumBytes = maximumBytes; }
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

        public static ProcessResult Run(string fileName, string[] arguments, string input, string workingDirectory, int timeoutMilliseconds, int maximumOutputBytes)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
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
$referencePath = if ([IO.Path]::IsPathRooted($ReferenceCliDll)) { [IO.Path]::GetFullPath($ReferenceCliDll) } else { [IO.Path]::GetFullPath((Join-Path $repoRoot $ReferenceCliDll)) }
$candidatePath = if ([IO.Path]::IsPathRooted($CandidateCliDll)) { [IO.Path]::GetFullPath($CandidateCliDll) } else { [IO.Path]::GetFullPath((Join-Path $repoRoot $CandidateCliDll)) }
$evidenceFullPath = if ([IO.Path]::IsPathRooted($EvidencePath)) { [IO.Path]::GetFullPath($EvidencePath) } else { [IO.Path]::GetFullPath((Join-Path $repoRoot $EvidencePath)) }
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'tests/fixtures/ir-parity'))
$maxOutputBytes = [int64]$MaxOutputMiB * 1MB
$maxInputBytes = 1MB
$script:report = $null
$script:anyFailed = $false

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
    if ($null -eq $script:report) { return }
    Assert-NoReparseComponents -Path $script:evidenceFullPath
    $json = ConvertTo-Json -InputObject $script:report -Depth 90
    [IO.File]::WriteAllText($script:evidenceFullPath, $json, [Text.UTF8Encoding]::new($false))
}

function Add-Check {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][bool]$Passed,
        [Parameter(Mandatory)][string]$Message,
        [string]$Fixture,
        [string]$Session,
        [string]$Side,
        [int]$ExchangeIndex = -1
    )

    $check = [ordered]@{ name = $Name; passed = $Passed; message = $Message }
    if ($Fixture) { $check.fixture = $Fixture }
    if ($Session) { $check.session = $Session }
    if ($Side) { $check.side = $Side }
    if ($ExchangeIndex -ge 0) { $check.exchangeIndex = $ExchangeIndex }
    $script:report.checks = @($script:report.checks) + @($check)
    if (-not $Passed) { $script:anyFailed = $true }
    Save-Report
}

function Get-BinaryRecord {
    param([Parameter(Mandatory)][string]$Path)

    Assert-NoReparseComponents -Path $Path
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "CLI DLL not found: $Path" }
    $directory = Split-Path -Parent $Path
    $files = Get-ChildItem -LiteralPath $directory -Filter '*.dll' -File | Sort-Object Name
    $dependencies = [Collections.Generic.List[object]]::new()
    foreach ($file in $files) {
        Assert-NoReparseComponents -Path $file.FullName
        $dependencies.Add([ordered]@{
            name = $file.Name
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        })
    }
    $cliHash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    $runtimeFiles = [Collections.Generic.List[object]]::new()
    foreach ($suffix in @('.deps.json', '.runtimeconfig.json')) {
        $runtimePath = [IO.Path]::ChangeExtension($Path, $suffix)
        Assert-NoReparseComponents -Path $runtimePath
        if (-not (Test-Path -LiteralPath $runtimePath -PathType Leaf)) { throw "Required CLI runtime file not found: $runtimePath" }
        $runtimeFiles.Add([ordered]@{
            name = [IO.Path]::GetFileName($runtimePath)
            sha256 = (Get-FileHash -LiteralPath $runtimePath -Algorithm SHA256).Hash.ToLowerInvariant()
        })
    }
    [ordered]@{ cliDll = $Path; cliSha256 = $cliHash; dllDependencies = @($dependencies); runtimeFiles = @($runtimeFiles) }
}

function Get-GitMetadata {
    $head = $null
    $dirty = $null
    try {
        $headOutput = & git -C $repoRoot rev-parse HEAD 2>$null
        if ($LASTEXITCODE -eq 0) {
            $head = [string]($headOutput | Select-Object -First 1)
            $statusOutput = & git -C $repoRoot status --porcelain 2>$null
            if ($LASTEXITCODE -eq 0) { $dirty = @($statusOutput).Count -gt 0 }
        }
    } catch { }
    [ordered]@{ head = $head; workingTreeDirty = $dirty }
}

function ConvertTo-RequestLine {
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Request)
    ConvertTo-Json -InputObject $Request -Depth 50 -Compress
}

function New-ExpandedRequest {
    param(
        [Parameter(Mandatory)][System.Collections.IDictionary]$RequestSpec,
        [Parameter(Mandatory)][string]$FixtureName
    )

    if (-not ($RequestSpec.request -is [System.Collections.IDictionary])) { throw "Fixture '$FixtureName' contains a request without an object payload." }
    $request = [ordered]@{}
    foreach ($key in $RequestSpec.request.Keys) { $request[$key] = $RequestSpec.request[$key] }
    $sourceFile = $null
    if ($RequestSpec.Contains('sourceFile')) { $sourceFile = [string]$RequestSpec.sourceFile }
    elseif ($request.Contains('sourceFile')) { $sourceFile = [string]$request.sourceFile }
    if ($null -ne $sourceFile) {
        $relative = $sourceFile
        if ([IO.Path]::IsPathRooted($relative)) { throw "Fixture '$FixtureName' source path must be relative to its fixture directory." }
        $sourcePath = [IO.Path]::GetFullPath((Join-Path $fixtureRoot $relative))
        $prefix = $fixtureRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $sourcePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Fixture '$FixtureName' source path escapes its fixture directory." }
        Assert-NoReparseComponents -Path $sourcePath
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw "Fixture source file not found: $sourcePath" }
        if ((Get-Item -LiteralPath $sourcePath).Length -gt 256KB) { throw "Fixture source exceeds the 256 KiB source limit: $relative" }
        [void]$request.Remove('sourceFile')
        $request['source'] = [IO.File]::ReadAllText($sourcePath, [Text.Encoding]::UTF8)
    }
    if (-not $request.Contains('op')) { throw "Fixture '$FixtureName' request has no operation." }
    $allowedOperations = @('define', 'eval', 'test', 'commit', 'task.begin', 'task.status', 'task.abort', 'describe', 'source', 'storage.status')
    if ($allowedOperations -cnotcontains [string]$request.op) { throw "Fixture '$FixtureName' uses unsupported operation '$($request.op)'." }
    if ($request.op -in @('define', 'eval') -and -not $request.Contains('frontend')) { $request['frontend'] = 'stack' }
    $request
}

function Invoke-CliSession {
    param(
        [Parameter(Mandatory)][string]$Side,
        [Parameter(Mandatory)][string]$FixtureName,
        [Parameter(Mandatory)][System.Collections.IDictionary]$SessionSpec,
        [Parameter(Mandatory)][string]$ProjectPath
    )

    Assert-NoReparseComponents -Path $ProjectPath
    Ensure-SafeDirectory -Path $ProjectPath
    $requests = [Collections.Generic.List[object]]::new()
    foreach ($requestSpec in $SessionSpec.requests) {
        if (-not ($requestSpec -is [System.Collections.IDictionary])) { throw "Fixture '$FixtureName' has a malformed request specification." }
        $requests.Add([ordered]@{
            request = New-ExpandedRequest -RequestSpec $requestSpec -FixtureName $FixtureName
            expect = $requestSpec.expect
        })
    }
    if ($requests.Count -lt 1 -or $requests.Count -gt 100) { throw "Fixture '$FixtureName' session request count must be between 1 and 100." }

    $lines = @($requests | ForEach-Object { ConvertTo-RequestLine -Request $_.request })
    $inputText = [string]::Join("`n", $lines) + "`n"
    $inputBytes = [Text.Encoding]::UTF8.GetByteCount($inputText)
    if ($inputBytes -gt $maxInputBytes) { throw "Fixture '$FixtureName' request batch exceeds the 1 MiB input limit." }

    $capabilities = @()
    if ($SessionSpec.Contains('capabilities')) { $capabilities = @($SessionSpec.capabilities) }
    $knownCapabilities = @('fs.read', 'fs.write', 'db.read', 'db.write', 'network.read', 'network.write', 'process.execute', 'clock.read', 'random.read', 'console.write')
    foreach ($capability in $capabilities) {
        if ($knownCapabilities -cnotcontains [string]$capability) { throw "Fixture '$FixtureName' requests unsupported host capability '$capability'." }
    }
    $cliPath = if ($Side -eq 'reference') { $referencePath } else { $candidatePath }
    $arguments = [Collections.Generic.List[string]]::new()
    $arguments.Add($cliPath)
    $arguments.Add('--project')
    $arguments.Add($ProjectPath)
    $arguments.Add('--jsonl')
    if ($capabilities.Count -gt 0) {
        $arguments.Add('--allow')
        $arguments.Add(($capabilities | Sort-Object -Unique) -join ',')
    }
    $processResult = [AgentLangIrParityVerifier.ProcessRunner]::Run(
        'dotnet', $arguments.ToArray(), $inputText, $repoRoot, $TimeoutSeconds * 1000, [int]$maxOutputBytes)

    $stdoutLines = @($processResult.Stdout -split "`r?`n" | Where-Object { $_.Length -gt 0 })
    $session = [ordered]@{
        side = $Side
        fixture = $FixtureName
        name = [string]$SessionSpec.name
        projectPath = $ProjectPath
        capabilities = $capabilities
        freshProcess = $true
        process = [ordered]@{
            exitCode = $processResult.ExitCode
            timedOut = $processResult.TimedOut
            outputLimitExceeded = $processResult.OutputLimitExceeded
            durationMilliseconds = $processResult.DurationMilliseconds
            inputBytes = $inputBytes
            inputError = $processResult.InputError
            stderr = $processResult.Stderr
            stdoutLines = $stdoutLines
        }
        exchanges = @()
    }
    $script:report.sessions = @($script:report.sessions) + @($session)
    Save-Report
    Add-Check -Name 'CLI subprocess completed within bounds' `
        -Passed (-not $processResult.TimedOut -and -not $processResult.OutputLimitExceeded -and $null -eq $processResult.InputError -and $processResult.ExitCode -eq 0) `
        -Message ("exit={0}, timeout={1}, outputLimit={2}, durationMs={3}" -f $processResult.ExitCode, $processResult.TimedOut, $processResult.OutputLimitExceeded, $processResult.DurationMilliseconds) `
        -Fixture $FixtureName -Session ([string]$SessionSpec.name) -Side $Side
    Add-Check -Name 'JSONL response count matches requests' `
        -Passed ($stdoutLines.Count -eq $requests.Count) `
        -Message ("received {0} response line(s) for {1} request(s)" -f $stdoutLines.Count, $requests.Count) `
        -Fixture $FixtureName -Session ([string]$SessionSpec.name) -Side $Side

    for ($index = 0; $index -lt $requests.Count; $index++) {
        $rawResponse = if ($index -lt $stdoutLines.Count) { $stdoutLines[$index] } else { $null }
        $parsedResponse = $null
        $parseError = $null
        if ($null -ne $rawResponse) {
            try { $parsedResponse = ConvertFrom-Json -InputObject $rawResponse -AsHashtable -Depth 60 }
            catch { $parseError = $_.Exception.Message }
        }
        $exchange = [ordered]@{
            index = $index
            request = $requests[$index].request
            requestLine = $lines[$index]
            expected = $requests[$index].expect
            responseRaw = $rawResponse
            response = $parsedResponse
            projection = $null
            parseError = $parseError
        }
        $sessionItem = $script:report.sessions | Where-Object { $_.side -eq $Side -and $_.fixture -eq $FixtureName -and $_.name -eq [string]$SessionSpec.name } | Select-Object -Last 1
        $sessionItem.exchanges = @($sessionItem.exchanges) + @($exchange)
        $valid = $null -ne $parsedResponse -and $parsedResponse -is [System.Collections.IDictionary]
        Add-Check -Name "Response $index is a JSON object" -Passed $valid -Message $(if ($valid) { 'valid JSON response object' } else { "missing or invalid JSON response: $parseError" }) -Fixture $FixtureName -Session ([string]$SessionSpec.name) -Side $Side -ExchangeIndex $index
    }
}

function Get-JsonMismatch {
    param(
        [AllowNull()][object]$Expected,
        [AllowNull()][object]$Actual,
        [Parameter(Mandatory)][string]$Path,
        [bool]$Exact = $false
    )

    if ($Expected -is [System.Collections.IDictionary]) {
        if (-not ($Actual -is [System.Collections.IDictionary])) { return "$Path expected an object." }
        $expectedKeys = @($Expected.Keys | ForEach-Object { [string]$_ })
        $actualKeys = @($Actual.Keys | ForEach-Object { [string]$_ })
        if ($Exact -and $expectedKeys.Count -ne $actualKeys.Count) { return "$Path expected exactly $($expectedKeys.Count) property/properties, observed $($actualKeys.Count)." }
        foreach ($key in $expectedKeys) {
            if (-not $Actual.Contains($key)) { return "$Path is missing property '$key'." }
            $mismatch = Get-JsonMismatch -Expected $Expected[$key] -Actual $Actual[$key] -Path "$Path.$key" -Exact $Exact
            if ($null -ne $mismatch) { return $mismatch }
        }
        if ($Exact) {
            foreach ($key in $actualKeys) { if ($expectedKeys -cnotcontains $key) { return "$Path contains unexpected property '$key'." } }
        }
        return $null
    }

    if ($Expected -is [System.Collections.IList] -and $Expected -isnot [string]) {
        if (-not ($Actual -is [System.Collections.IList]) -or $Actual -is [string]) { return "$Path expected an array." }
        if ($Expected.Count -ne $Actual.Count) { return "$Path expected $($Expected.Count) item(s), observed $($Actual.Count)." }
        for ($index = 0; $index -lt $Expected.Count; $index++) {
            $mismatch = Get-JsonMismatch -Expected $Expected[$index] -Actual $Actual[$index] -Path "$Path[$index]" -Exact $Exact
            if ($null -ne $mismatch) { return $mismatch }
        }
        return $null
    }

    $expectedJson = ConvertTo-Json -InputObject $Expected -Depth 20 -Compress
    $actualJson = ConvertTo-Json -InputObject $Actual -Depth 20 -Compress
    if ($expectedJson -cne $actualJson) { return "$Path expected $expectedJson, observed $actualJson." }
    $null
}

function Get-ExpectedProjection {
    param([Parameter(Mandatory)][object]$Expected, [Parameter(Mandatory)][object]$Actual)
    if ($Expected -is [System.Collections.IDictionary]) {
        $selected = [ordered]@{}
        foreach ($key in $Expected.Keys) { if ($Actual -is [System.Collections.IDictionary] -and $Actual.Contains([string]$key)) { $selected[[string]$key] = Get-ExpectedProjection -Expected $Expected[$key] -Actual $Actual[[string]$key] } }
        return $selected
    }
    if ($Expected -is [System.Collections.IList] -and $Expected -isnot [string]) {
        $selected = [Collections.Generic.List[object]]::new()
        if ($Actual -is [System.Collections.IList]) {
            for ($index = 0; $index -lt [Math]::Min($Expected.Count, $Actual.Count); $index++) { $selected.Add((Get-ExpectedProjection -Expected $Expected[$index] -Actual $Actual[$index])) }
        }
        return ,$selected.ToArray()
    }
    $Actual
}

function Get-JsonPathValue {
    param([Parameter(Mandatory)][object]$Value, [Parameter(Mandatory)][string]$Path)
    $current = $Value
    foreach ($segment in $Path.Split('.')) {
        if (-not ($current -is [System.Collections.IDictionary]) -or -not $current.Contains($segment)) { throw "JSON path '$Path' is missing segment '$segment'." }
        $current = $current[$segment]
    }
    $current
}

function Get-ResponseExchange {
    param([string]$Side, [string]$FixtureName, [string]$SessionName, [int]$Index)
    $session = $script:report.sessions | Where-Object { $_.side -eq $Side -and $_.fixture -eq $FixtureName -and $_.name -eq $SessionName } | Select-Object -Last 1
    if ($null -eq $session -or $Index -ge $session.exchanges.Count) { return $null }
    $session.exchanges[$Index]
}

function Assert-FixtureExpectations {
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Fixture)

    foreach ($sessionSpec in $Fixture.sessions) {
        $sessionName = [string]$sessionSpec.name
        for ($index = 0; $index -lt $sessionSpec.requests.Count; $index++) {
            $spec = $sessionSpec.requests[$index]
            $sideProjections = [ordered]@{}
            foreach ($side in @('reference', 'candidate')) {
                $exchange = Get-ResponseExchange -Side $side -FixtureName ([string]$Fixture.name) -SessionName $sessionName -Index $index
                if ($null -eq $exchange -or -not ($exchange.response -is [System.Collections.IDictionary])) {
                    Add-Check -Name 'Response satisfies explicit fixture oracle' -Passed $false -Message 'No parsed response object is available.' -Fixture ([string]$Fixture.name) -Session $sessionName -Side $side -ExchangeIndex $index
                    continue
                }
                $expect = $spec.expect
                $subsetMismatch = Get-JsonMismatch -Expected $expect.subset -Actual $exchange.response -Path '$' -Exact $false
                Add-Check -Name 'Response satisfies explicit fixture oracle' -Passed ($null -eq $subsetMismatch) -Message $(if ($null -eq $subsetMismatch) { 'selected semantic fields match the reviewed expected oracle' } else { $subsetMismatch }) -Fixture ([string]$Fixture.name) -Session $sessionName -Side $side -ExchangeIndex $index
                $projected = [ordered]@{ subset = Get-ExpectedProjection -Expected $expect.subset -Actual $exchange.response; exact = [ordered]@{} }
                $exactItems = if ($expect.Contains('exact')) { @($expect.exact) } else { @() }
                foreach ($exactItem in $exactItems) {
                    try {
                        $actualValue = Get-JsonPathValue -Value $exchange.response -Path ([string]$exactItem.path)
                        $exactMismatch = Get-JsonMismatch -Expected $exactItem.value -Actual $actualValue -Path ([string]$exactItem.path) -Exact $true
                        Add-Check -Name 'Exact fixture field matches expected oracle' -Passed ($null -eq $exactMismatch) -Message $(if ($null -eq $exactMismatch) { 'field exactly matches the reviewed expected oracle' } else { $exactMismatch }) -Fixture ([string]$Fixture.name) -Session $sessionName -Side $side -ExchangeIndex $index
                        $projected.exact[[string]$exactItem.path] = $actualValue
                    } catch {
                        Add-Check -Name 'Exact fixture field matches expected oracle' -Passed $false -Message $_.Exception.Message -Fixture ([string]$Fixture.name) -Session $sessionName -Side $side -ExchangeIndex $index
                    }
                }
                $exchange.projection = $projected
                $sideProjections[$side] = $projected
            }
            if ($sideProjections.Contains('reference') -and $sideProjections.Contains('candidate')) {
                $parityMismatch = Get-JsonMismatch -Expected $sideProjections.reference -Actual $sideProjections.candidate -Path '$projection' -Exact $true
                Add-Check -Name 'Reference and candidate semantic projections match' -Passed ($null -eq $parityMismatch) -Message $(if ($null -eq $parityMismatch) { 'selected explicit contract fields are identical' } else { $parityMismatch }) -Fixture ([string]$Fixture.name) -Session $sessionName -ExchangeIndex $index
            }
        }
    }
}

function New-ProjectDirectory {
    param([Parameter(Mandatory)][string]$Side, [Parameter(Mandatory)][string]$FixtureName)
    $sideRoot = Join-Path $script:runRoot $Side
    Ensure-SafeDirectory -Path $sideRoot
    $path = Join-Path $sideRoot $FixtureName
    if (Test-Path -LiteralPath $path) { throw "Unique verification project already exists: $path" }
    Ensure-SafeDirectory -Path $path
    $path
}

try {
    Assert-NoReparseComponents -Path $repoRoot
    Assert-NoReparseComponents -Path $fixtureRoot
    Assert-NoReparseComponents -Path $referencePath
    Assert-NoReparseComponents -Path $candidatePath
    if (-not (Test-Path -LiteralPath $fixtureRoot -PathType Container)) { throw "Parity fixture directory not found: $fixtureRoot" }

    $reportDirectory = Split-Path -Parent $evidenceFullPath
    Ensure-SafeDirectory -Path $reportDirectory
    Assert-NoReparseComponents -Path $evidenceFullPath
    $script:evidenceFullPath = $evidenceFullPath
    $script:runRoot = Join-Path (Join-Path $repoRoot '.agentlang') ('ir-parity-' + [Guid]::NewGuid().ToString('N'))
    Ensure-SafeDirectory -Path $script:runRoot

    $referenceBinary = Get-BinaryRecord -Path $referencePath
    $candidateBinary = Get-BinaryRecord -Path $candidatePath
    $referenceFingerprint = ConvertTo-Json -InputObject @(
        @($referenceBinary.dllDependencies | ForEach-Object { "$($_.name):$($_.sha256)" }) +
        @($referenceBinary.runtimeFiles | ForEach-Object { "$($_.name):$($_.sha256)" })
    ) -Compress
    $candidateFingerprint = ConvertTo-Json -InputObject @(
        @($candidateBinary.dllDependencies | ForEach-Object { "$($_.name):$($_.sha256)" }) +
        @($candidateBinary.runtimeFiles | ForEach-Object { "$($_.name):$($_.sha256)" })
    ) -Compress
    $sameBuild = $referenceBinary.cliSha256 -eq $candidateBinary.cliSha256 -and $referenceFingerprint -ceq $candidateFingerprint
    $gitMetadata = Get-GitMetadata
    $manifestPath = Join-Path $fixtureRoot 'manifest.json'
    Assert-NoReparseComponents -Path $manifestPath
    if ((Get-Item -LiteralPath $manifestPath).Length -gt 1MB) { throw 'Parity manifest exceeds the 1 MiB limit.' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable -Depth 80
    if ($manifest.schemaVersion -ne 1 -or $manifest.fixtures.Count -lt 1 -or $manifest.fixtures.Count -gt 30) { throw 'Parity manifest schema or fixture count is invalid.' }

    $script:report = [ordered]@{
        schemaVersion = 1
        purpose = 'behavioral parity over selected CLI-visible contracts; this report makes no claim that either binary executes IR'
        startedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        state = 'running'
        comparisonMode = if ($sameBuild) { 'baseline-self-comparison-infrastructure-only' } else { 'cross-binary-behavior-parity-no-IR-claim' }
        testedHead = $gitMetadata.head
        workingTreeDirty = $gitMetadata.workingTreeDirty
        referenceBinary = $referenceBinary
        candidateBinary = $candidateBinary
        repositoryRoot = $repoRoot
        fixtureRoot = $fixtureRoot
        verificationRoot = $script:runRoot
        limits = [ordered]@{ timeoutSecondsPerProcess = $TimeoutSeconds; maxOutputBytesPerProcess = $maxOutputBytes; maxInputBytesPerProcess = $maxInputBytes; maxFixtureSourceBytes = 256KB }
        sessions = @()
        checks = @()
        failure = $null
        completedUtc = $null
    }
    Save-Report

    $fixtureNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($fixture in $manifest.fixtures) {
        $fixtureName = [string]$fixture.name
        if ($fixtureName -notmatch '^[a-z0-9-]{1,64}$' -or -not $fixtureNames.Add($fixtureName)) { throw "Parity fixture name is invalid or duplicated: '$fixtureName'." }
        if ($fixture.sessions.Count -lt 1 -or $fixture.sessions.Count -gt 10) { throw "Fixture '$fixtureName' session count must be between 1 and 10." }
        foreach ($side in @('reference', 'candidate')) {
            $projectPath = New-ProjectDirectory -Side $side -FixtureName $fixtureName
            foreach ($sessionSpec in $fixture.sessions) {
                if ([string]::IsNullOrWhiteSpace([string]$sessionSpec.name)) { throw "Fixture '$fixtureName' has a session without a name." }
                Invoke-CliSession -Side $side -FixtureName $fixtureName -SessionSpec $sessionSpec -ProjectPath $projectPath
            }
        }
        Assert-FixtureExpectations -Fixture $fixture
    }

    if ($sameBuild) {
        Add-Check -Name 'Self-comparison is clearly labeled' -Passed ($script:report.comparisonMode -eq 'baseline-self-comparison-infrastructure-only') -Message 'Identical CLI and dependency DLL hashes are an infrastructure self-check, not evidence of IR parity.'
    } else {
        Add-Check -Name 'Cross-binary comparison remains behavior-only' -Passed ($script:report.comparisonMode -eq 'cross-binary-behavior-parity-no-IR-claim') -Message 'Different hashes permit selected behavior comparison only; the script does not infer which backend executes.'
    }

    $script:report.state = if ($script:anyFailed) { 'failed' } else { 'passed' }
} catch {
    if ($null -ne $script:report) {
        $script:report.state = 'failed'
        $script:report.failure = $_.Exception.Message
    }
    Write-Error $_
    if ($null -eq $script:report) { exit 1 }
} finally {
    if ($null -ne $script:report) {
        $script:report.completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        try { Save-Report } catch { Write-Warning "Could not save parity report: $($_.Exception.Message)" }
    }
}

if ($script:anyFailed -or $script:report.state -ne 'passed') {
    Write-Output "IR parity verification failed. Evidence: $evidenceFullPath"
    exit 1
}
Write-Output "IR parity contract verification passed. Evidence: $evidenceFullPath"
