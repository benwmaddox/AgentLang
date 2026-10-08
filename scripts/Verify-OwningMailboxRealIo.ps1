#requires -Version 7.0
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runId = [Guid]::NewGuid().ToString('N')
$evidenceRoot = Join-Path $repo '.agentlang/owning-mailbox-003'
$runDirectory = Join-Path $evidenceRoot "io-run-$runId"
$reportPath = Join-Path $runDirectory 'io-evidence.json'
$experimentDirectory = Join-Path $repo 'experiments/AgentLang.RealIoMailbox'
$fixturePath = Join-Path $experimentDirectory 'correctness-cases.json'
$flowPath = Join-Path $experimentDirectory 'mailbox.flow'
$bootstrapProjectPath = Join-Path $repo 'experiments/AgentLang.OwningMailbox/AgentLang.OwningMailbox.fsproj'
$bootstrapProgramPath = Join-Path $repo 'experiments/AgentLang.OwningMailbox/Program.fs'
$bootstrapContentFlowPath = Join-Path $repo 'experiments/AgentLang.OwningMailbox/owning-mailbox.flow'
$providerProjectPath = Join-Path $experimentDirectory 'Provider/AgentLang.RealIoMailbox.Provider.fsproj'
$providerProgramPath = Join-Path $experimentDirectory 'Provider/Program.fs'
$nativeDirectory = Join-Path $repo 'src/AgentLang.Llvm/native'
$nativeHostPath = Join-Path $experimentDirectory 'native_host.c'
$providerFreezePath = Join-Path $evidenceRoot 'oracle-freeze.json'
$nativeSources = @(
    $nativeHostPath,
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
$sourceInputPaths = @(
    (Join-Path $repo 'AgentLang.sln'),
    $bootstrapProjectPath,
    $bootstrapProgramPath,
    $bootstrapContentFlowPath,
    $flowPath,
    $fixturePath,
    $providerProjectPath,
    $providerProgramPath,
    $nativeHostPath,
    $PSCommandPath,
    (Join-Path $repo 'src/AgentLang.Llvm/AgentLang.Llvm.fsproj'),
    (Join-Path $repo 'src/AgentLang.Llvm/OwningStackAot.fs'),
    (Join-Path $repo 'src/AgentLang.Llvm/LlvmToolchain.fs'),
    (Join-Path $repo 'src/AgentLang.Core/AgentLang.Core.fsproj')
) + $nativeSources + $nativeHeaders
foreach ($optionalBuildInput in @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'global.json', 'NuGet.config')) {
    $optionalPath = Join-Path $repo $optionalBuildInput
    if (Test-Path -LiteralPath $optionalPath -PathType Leaf) { $sourceInputPaths += $optionalPath }
}
foreach ($compilerRoot in @((Join-Path $repo 'src/AgentLang.Core'), (Join-Path $repo 'src/AgentLang.Llvm'))) {
    if (Test-Path -LiteralPath $compilerRoot -PathType Container) {
        $sourceInputPaths += Get-ChildItem -LiteralPath $compilerRoot -Recurse -File |
            Where-Object {
                $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and
                ($_.Extension -in @('.fs', '.fsproj', '.props', '.targets'))
            } |
            ForEach-Object { $_.FullName }
    }
}
if (Test-Path -LiteralPath $providerFreezePath -PathType Leaf) { $sourceInputPaths += $providerFreezePath }
$sourceInputPaths = @($sourceInputPaths | ForEach-Object { [IO.Path]::GetFullPath($_) } | Sort-Object -Unique)

$utf8 = [Text.UTF8Encoding]::new($false)
$checks = [Collections.Generic.List[object]]::new()
$processes = [Collections.Generic.List[object]]::new()
$moduleBuilds = [Collections.Generic.List[object]]::new()
$nativeBuilds = [Collections.Generic.List[object]]::new()
$nativeRuns = [Collections.Generic.List[object]]::new()
$errors = [Collections.Generic.List[string]]::new()
$liveProcessHandles = [Collections.Generic.List[object]]::new()
$sourceInputBefore = @()
$sourceInputAfter = @()
$timeoutMilliseconds = 300000
$tempDirectory = Join-Path $runDirectory 'repo-temp'
$oracleHashBeforeNative = $null
$oracleFreezeCopyPath = $null
$oracleFreezeCopySha256 = $null
$providerHandle = $null
$providerReady = $null
$providerCounters = $null
$providerLifecycle = [ordered]@{ started = $false; ready = $false; stopFileWritten = $false; stopWriteError = $null; gracefulExit = $false; forcedTermination = $false }
$dotnet = $null
$clang = $null
$oracle = $null
$runChecksPassed = $false

$report = [ordered]@{
    schemaVersion = 1
    kind = 'owning-native-mailbox-real-io-correctness-gate'
    runId = $runId
    startedUtc = [DateTime]::UtcNow.ToString('O')
    repository = $repo
    fixture = $fixturePath
    flow = $flowPath
    sourceInputs = @($sourceInputPaths)
    measurementScope = 'Actual-I/O correctness and mailbox/provider counters. Reserved-storage counters are caller-owned controller storage; elapsed process diagnostics are not throughput or whole-process-RAM claims.'
    provider = $null
    checks = @()
}

function Add-Check([string]$Name, [bool]$Passed, $Detail = $null) {
    $script:checks.Add([ordered]@{ name = $Name; passed = $Passed; detail = $Detail })
}

function Has-ExactKey($Object, [string]$Name) {
    if ($null -eq $Object -or $Object -isnot [Collections.IDictionary]) { return $false }
    foreach ($key in $Object.Keys) {
        if ([string]::Equals([string]$key, $Name, [StringComparison]::Ordinal)) { return $true }
    }
    return $false
}

function Get-ExactField($Object, [string]$Name) {
    if ($null -eq $Object -or $Object -isnot [Collections.IDictionary]) { return $null }
    foreach ($key in $Object.Keys) {
        if ([string]::Equals([string]$key, $Name, [StringComparison]::Ordinal)) {
            return ,($Object[$key])
        }
    }
    return $null
}

function Require-Object($Object, [string]$Label) {
    if ($null -eq $Object -or $Object -isnot [Collections.IDictionary]) {
        Add-Check "$Label is a JSON object" $false ([ordered]@{ actualType = if ($null -eq $Object) { 'null' } else { $Object.GetType().FullName } })
        throw "$Label must be a JSON object."
    }
}

function Require-Fields($Object, [string[]]$Names, [string]$Label) {
    Require-Object $Object $Label
    $missing = @($Names | Where-Object { -not (Has-ExactKey $Object $_) })
    Add-Check "$Label contains required exact-case fields" ($missing.Count -eq 0) ([ordered]@{ required = $Names; missing = $missing })
    if ($missing.Count -gt 0) { throw "$Label is missing required fields: $($missing -join ', ')." }
}

function Test-JsonInteger($Value) {
    return ($Value -is [sbyte] -or $Value -is [byte] -or $Value -is [int16] -or $Value -is [uint16] -or
        $Value -is [int32] -or $Value -is [uint32] -or $Value -is [int64] -or $Value -is [uint64])
}

function Require-JsonInteger($Value, [string]$Label, [switch]$NonNegative) {
    if (-not (Test-JsonInteger $Value) -or $Value -is [bool]) {
        Add-Check "$Label is a JSON integer" $false ([ordered]@{ actual = $Value; actualType = if ($null -eq $Value) { 'null' } else { $Value.GetType().FullName } })
        throw "$Label must be a JSON integer; received $($Value.GetType().FullName)."
    }
    try { $integer = [Convert]::ToInt64($Value, [Globalization.CultureInfo]::InvariantCulture) }
    catch {
        Add-Check "$Label fits Int64" $false ([ordered]@{ actual = $Value })
        throw "$Label is outside the supported Int64 range."
    }
    if ($NonNegative -and $integer -lt 0) {
        Add-Check "$Label is nonnegative" $false ([ordered]@{ actual = $integer })
        throw "$Label must be nonnegative."
    }
    return $integer
}

function Require-JsonString($Value, [string]$Label) {
    if ($Value -isnot [string]) {
        Add-Check "$Label is a JSON string" $false ([ordered]@{ actual = $Value; actualType = if ($null -eq $Value) { 'null' } else { $Value.GetType().FullName } })
        throw "$Label must be a JSON string."
    }
    return $Value
}

function Require-JsonBoolean($Value, [string]$Label) {
    if ($Value -isnot [bool]) {
        Add-Check "$Label is a JSON boolean" $false ([ordered]@{ actual = $Value; actualType = if ($null -eq $Value) { 'null' } else { $Value.GetType().FullName } })
        throw "$Label must be a JSON boolean."
    }
    return $Value
}

function Require-JsonArray($Value, [string]$Label) {
    if ($Value -isnot [Array]) {
        Add-Check "$Label is a JSON array" $false ([ordered]@{ actual = $Value; actualType = if ($null -eq $Value) { 'null' } else { $Value.GetType().FullName } })
        throw "$Label must be a JSON array."
    }
    Write-Output -NoEnumerate $Value
}

function Get-Hash([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Read-JsonFile([string]$Path) {
    try { return (ConvertFrom-Json -InputObject (Get-Content -LiteralPath $Path -Raw) -AsHashtable -Depth 100 -ErrorAction Stop) }
    catch { throw "JSON file is invalid: $Path :: $($_.Exception.Message)" }
}

function ConvertFrom-JsonText([string]$Text, [string]$Description) {
    try { return (ConvertFrom-Json -InputObject $Text -AsHashtable -Depth 100 -ErrorAction Stop) }
    catch { throw "$Description is not valid JSON: $($_.Exception.Message)" }
}

function Resolve-Executable([string]$EnvironmentName, [string[]]$DefaultPaths, [string]$CommandName) {
    $override = [Environment]::GetEnvironmentVariable($EnvironmentName)
    if (-not [string]::IsNullOrWhiteSpace($override)) {
        $candidate = [IO.Path]::GetFullPath($override)
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { throw "$EnvironmentName points to a missing executable: $candidate" }
        return $candidate
    }
    foreach ($defaultPath in $DefaultPaths) {
        if (Test-Path -LiteralPath $defaultPath -PathType Leaf) { return [IO.Path]::GetFullPath($defaultPath) }
    }
    $command = Get-Command -Name $CommandName -CommandType Application -ErrorAction Stop | Select-Object -First 1
    return [IO.Path]::GetFullPath($command.Source)
}

function New-ProcessStartInfo([string]$Executable, [string[]]$Arguments, [string]$WorkingDirectory) {
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
    return $start
}

function Invoke-CapturedProcess([string]$Name, [string]$Executable, [string[]]$Arguments, [string]$WorkingDirectory, [int]$TimeoutMs = $script:timeoutMilliseconds) {
    $safeName = $Name -replace '[^a-zA-Z0-9_-]', '_'
    $stdoutPath = Join-Path $script:runDirectory "$safeName.stdout.txt"
    $stderrPath = Join-Path $script:runDirectory "$safeName.stderr.txt"
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = New-ProcessStartInfo $Executable $Arguments $WorkingDirectory
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $timedOut = $false
    $exitCode = $null
    $stdout = ''
    $stderr = ''
    $startError = $null
    $stdoutTask = $null
    $stderrTask = $null
    $processId = $null
    $cleanupError = $null
    $stdoutDrainTimedOut = $false
    $stderrDrainTimedOut = $false
    try {
        if (-not $process.Start()) { throw 'Process.Start returned false.' }
        $processId = $process.Id
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutMs)) {
            $timedOut = $true
            try { $process.Kill($true) }
            catch { $cleanupError = "Kill failed: $($_.Exception.Message)" }
            if (-not $process.WaitForExit(5000)) { $cleanupError = @($cleanupError, 'Process remained alive after the bounded 5000 ms termination wait.' | Where-Object { $_ }) -join ' ' }
        }
    } catch {
        $startError = $_.Exception.Message
        if ($processId -and -not $process.HasExited) {
            try { $process.Kill($true) } catch { $cleanupError = "Kill after process error failed: $($_.Exception.Message)" }
            try {
                if (-not $process.WaitForExit(5000)) { $cleanupError = @($cleanupError, 'Process remained alive after the bounded 5000 ms error-cleanup wait.' | Where-Object { $_ }) -join ' ' }
            } catch { $cleanupError = @($cleanupError, "Error-cleanup wait failed: $($_.Exception.Message)" | Where-Object { $_ }) -join ' ' }
        }
    } finally {
        foreach ($stream in @(
            [ordered]@{ name = 'stdout'; task = $stdoutTask },
            [ordered]@{ name = 'stderr'; task = $stderrTask }
        )) {
            $task = $stream.task
            if ($null -eq $task) { continue }
            try {
                if (-not $task.Wait(5000)) {
                    if ($stream.name -ceq 'stdout') { $stdoutDrainTimedOut = $true } else { $stderrDrainTimedOut = $true }
                    $cleanupError = @($cleanupError, "$($stream.name) drain did not complete within 5000 ms.") -join ' '
                } else {
                    $text = $task.GetAwaiter().GetResult()
                    if ($stream.name -ceq 'stdout') { $stdout = $text } else { $stderr = $text }
                }
            } catch { $cleanupError = @($cleanupError, "$($stream.name) drain failed: $($_.Exception.Message)") -join ' ' }
        }
        $clock.Stop()
        if ($processId) {
            try { if ($process.HasExited -and $null -eq $exitCode) { $exitCode = $process.ExitCode } }
            catch { $cleanupError = @($cleanupError, "Could not read process exit status: $($_.Exception.Message)") -join ' ' }
        }
        $process.Dispose()
        [IO.File]::WriteAllText($stdoutPath, $stdout, $script:utf8)
        [IO.File]::WriteAllText($stderrPath, $stderr, $script:utf8)
    }
    $record = [ordered]@{
        name = $Name
        executable = $Executable
        arguments = @($Arguments)
        workingDirectory = $WorkingDirectory
        processId = $processId
        exitCode = $exitCode
        timedOut = $timedOut
        startError = $startError
        cleanupError = $cleanupError
        stdoutDrainTimedOut = $stdoutDrainTimedOut
        stderrDrainTimedOut = $stderrDrainTimedOut
        elapsedMilliseconds = $clock.ElapsedMilliseconds
        stdoutPath = $stdoutPath
        stderrPath = $stderrPath
        stdout = $stdout
        stderr = $stderr
    }
    $script:processes.Add($record)
    return $record
}

function Start-CapturedLongProcess([string]$Name, [string]$Executable, [string[]]$Arguments, [string]$WorkingDirectory) {
    $safeName = $Name -replace '[^a-zA-Z0-9_-]', '_'
    $stdoutPath = Join-Path $script:runDirectory "$safeName.stdout.txt"
    $stderrPath = Join-Path $script:runDirectory "$safeName.stderr.txt"
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = New-ProcessStartInfo $Executable $Arguments $WorkingDirectory
    $record = [ordered]@{
        name = $Name
        executable = $Executable
        arguments = @($Arguments)
        workingDirectory = $WorkingDirectory
        processId = $null
        exitCode = $null
        timedOut = $false
        startError = $null
        elapsedMilliseconds = $null
        stdoutPath = $stdoutPath
        stderrPath = $stderrPath
        stdout = ''
        stderr = ''
        shutdown = $null
    }
    $handle = [ordered]@{ Process = $process; Record = $record; StdoutTask = $null; StderrTask = $null; Clock = [Diagnostics.Stopwatch]::StartNew(); Finalized = $false }
    try {
        if (-not $process.Start()) { throw 'Process.Start returned false.' }
        $record.processId = $process.Id
        $handle.StdoutTask = $process.StandardOutput.ReadToEndAsync()
        $handle.StderrTask = $process.StandardError.ReadToEndAsync()
    } catch {
        $record.startError = $_.Exception.Message
        $handle.Clock.Stop()
        $process.Dispose()
        $handle.Finalized = $true
        [IO.File]::WriteAllText($stdoutPath, '', $script:utf8)
        [IO.File]::WriteAllText($stderrPath, $record.startError, $script:utf8)
    }
    $script:processes.Add($record)
    $script:liveProcessHandles.Add($handle)
    return $handle
}

function Finish-CapturedLongProcess($Handle, [int]$GraceMs, [string]$ShutdownReason) {
    if ($Handle.Finalized) { return $Handle.Record }
    $process = $Handle.Process
    $forced = $false
    $waitError = $null
    $stdoutDrainTimedOut = $false
    $stderrDrainTimedOut = $false
    $processStillRunning = $false
    try {
        if (-not $process.HasExited) {
            if (-not $process.WaitForExit($GraceMs)) {
                $forced = $true
                try { $process.Kill($true) }
                catch { $waitError = "Kill failed: $($_.Exception.Message)" }
                try {
                    if (-not $process.WaitForExit(5000)) { $waitError = @($waitError, 'Process remained alive after the bounded 5000 ms termination wait.' | Where-Object { $_ }) -join ' ' }
                } catch { $waitError = @($waitError, "Termination wait failed: $($_.Exception.Message)") -join ' ' }
            }
        }
        if ($process.HasExited) { $Handle.Record.exitCode = $process.ExitCode }
    } catch { $waitError = $_.Exception.Message }
    finally {
        foreach ($stream in @(
            [ordered]@{ name = 'stdout'; task = $Handle.StdoutTask },
            [ordered]@{ name = 'stderr'; task = $Handle.StderrTask }
        )) {
            $task = $stream.task
            if ($null -eq $task) { continue }
            try {
                if (-not $task.Wait(5000)) {
                    if ($stream.name -ceq 'stdout') { $stdoutDrainTimedOut = $true } else { $stderrDrainTimedOut = $true }
                    $waitError = @($waitError, "$($stream.name) drain did not complete within 5000 ms.") -join ' '
                } else {
                    $text = $task.GetAwaiter().GetResult()
                    if ($stream.name -ceq 'stdout') { $Handle.Record.stdout = $text } else { $Handle.Record.stderr = $text }
                }
            } catch { $waitError = @($waitError, "$($stream.name) drain failed: $($_.Exception.Message)") -join ' ' }
        }
        $Handle.Clock.Stop()
        $Handle.Record.elapsedMilliseconds = $Handle.Clock.ElapsedMilliseconds
        $Handle.Record.timedOut = $forced
        try { $processStillRunning = -not $process.HasExited }
        catch { $processStillRunning = $true; $waitError = @($waitError, "Could not determine whether the process exited: $($_.Exception.Message)") -join ' ' }
        $Handle.Record.shutdown = [ordered]@{ reason = $ShutdownReason; forcedTermination = $forced; processStillRunning = $processStillRunning; stdoutDrainTimedOut = $stdoutDrainTimedOut; stderrDrainTimedOut = $stderrDrainTimedOut; error = $waitError }
        try { if (-not $Handle.Finalized) { $process.Dispose() } } catch { }
        $Handle.Finalized = $true
        [IO.File]::WriteAllText($Handle.Record.stdoutPath, [string]$Handle.Record.stdout, $script:utf8)
        [IO.File]::WriteAllText($Handle.Record.stderrPath, [string]$Handle.Record.stderr, $script:utf8)
    }
    return $Handle.Record
}

function Require-ProcessSuccess($ProcessRecord, [string]$Message) {
    $okay = -not $ProcessRecord.timedOut -and $null -eq $ProcessRecord.startError -and $ProcessRecord.exitCode -eq 0 -and
        $null -eq $ProcessRecord.cleanupError -and -not $ProcessRecord.stdoutDrainTimedOut -and -not $ProcessRecord.stderrDrainTimedOut
    Add-Check $Message $okay ([ordered]@{ exitCode = $ProcessRecord.exitCode; timedOut = $ProcessRecord.timedOut; startError = $ProcessRecord.startError; cleanupError = $ProcessRecord.cleanupError; stdoutDrainTimedOut = $ProcessRecord.stdoutDrainTimedOut; stderrDrainTimedOut = $ProcessRecord.stderrDrainTimedOut; stderrPath = $ProcessRecord.stderrPath })
    if (-not $okay) { throw "$Message (exit=$($ProcessRecord.exitCode), timeout=$($ProcessRecord.timedOut), startError=$($ProcessRecord.startError), cleanupError=$($ProcessRecord.cleanupError)): $($ProcessRecord.stderr) $($ProcessRecord.stdout)" }
}

function Get-UniqueAssembly([string]$ArtifactsRoot, [string]$AssemblyName, [string]$Label) {
    $assemblies = @(Get-ChildItem -LiteralPath (Join-Path $ArtifactsRoot 'bin') -Recurse -File -Filter $AssemblyName |
        Where-Object { $_.FullName -notmatch '[\\/]ref[\\/]' })
    Add-Check "$Label isolated build produced exactly one executable assembly" ($assemblies.Count -eq 1) ([ordered]@{ assembly = $AssemblyName; count = $assemblies.Count; paths = @($assemblies | ForEach-Object FullName) })
    if ($assemblies.Count -ne 1) { throw "Expected exactly one executable $AssemblyName under $ArtifactsRoot/bin." }
    return $assemblies[0].FullName
}

function Assert-StringEqual([string]$Actual, [string]$Expected, [string]$Label) {
    $equal = [string]::Equals($Actual, $Expected, [StringComparison]::Ordinal)
    Add-Check $Label $equal ([ordered]@{ expected = $Expected; actual = $Actual })
}

function Assert-IntegerEqual($ActualRaw, $ExpectedRaw, [string]$Label) {
    $actual = Require-JsonInteger $ActualRaw "$Label actual"
    $expected = Require-JsonInteger $ExpectedRaw "$Label expected"
    Add-Check $Label ($actual -eq $expected) ([ordered]@{ expected = $expected; actual = $actual })
}

function Get-OracleCaseById($OracleObject, [string]$Id) {
    $cases = Require-JsonArray (Get-ExactField $OracleObject 'cases') 'oracle.cases'
    foreach ($case in $cases) {
        if ((Get-ExactField $case 'id') -is [string] -and [string]::Equals([string](Get-ExactField $case 'id'), $Id, [StringComparison]::Ordinal)) { return $case }
    }
    return $null
}

function Get-RecordFields($Record, [string]$RecordName, [string]$Label) {
    Require-Fields $Record @('kind', 'name', 'fields') $Label
    $kind = Require-JsonString (Get-ExactField $Record 'kind') "$Label.kind"
    $name = Require-JsonString (Get-ExactField $Record 'name') "$Label.name"
    if ($kind -cne 'record' -or $name -cne $RecordName) { throw "$Label must be a $RecordName record value." }
    $fieldArray = Require-JsonArray (Get-ExactField $Record 'fields') "$Label.fields"
    $result = [ordered]@{}
    foreach ($entry in $fieldArray) {
        Require-Fields $entry @('name', 'value') "$Label field"
        $fieldName = Require-JsonString (Get-ExactField $entry 'name') "$Label field name"
        if ($result.Contains($fieldName)) { throw "$Label contains duplicate field $fieldName." }
        $result[$fieldName] = Get-ExactField $entry 'value'
    }
    return $result
}

function Compare-InterpreterRecord($Value, [string]$RecordName, $Expected, [string]$Label) {
    $recordFields = Get-RecordFields $Value $RecordName $Label
    foreach ($fieldName in $Expected.Keys) {
        if (-not $recordFields.Contains($fieldName)) { throw "$Label is missing $RecordName.$fieldName." }
        $actualNode = $recordFields[$fieldName]
        Require-Fields $actualNode @('kind', 'value') "$Label.$fieldName value"
        $actualKind = Require-JsonString (Get-ExactField $actualNode 'kind') "$Label.$fieldName.kind"
        $actualValue = Require-JsonString (Get-ExactField $actualNode 'value') "$Label.$fieldName.value"
        $expectedValue = [string]$Expected[$fieldName]
        if ($fieldName -in @('attempted', 'completed')) {
            if ($actualKind -cne 'int') { throw "$Label.$fieldName must be represented as interpreter kind int." }
        } elseif ($actualKind -cne 'string') {
            throw "$Label.$fieldName must be represented as interpreter kind string."
        }
        Assert-StringEqual $actualValue $expectedValue "$Label $RecordName.$fieldName matches frozen fixture"
    }
}

function Assert-InterpreterUnicode($Bootstrap, $OracleObject, [string]$Optimization) {
    $interpreter = Get-ExactField $Bootstrap 'interpreterOracle'
    Require-Fields $interpreter @('unicode', 'empty') "$Optimization bootstrap interpreterOracle"
    $unicode = Get-ExactField $interpreter 'unicode'
    Require-Fields $unicode @('initializedJson', 'pendingJson', 'completedJson') "$Optimization interpreter unicode cross-backend result"
    $case = Get-OracleCaseById $OracleObject 'unicode-and-nul'
    if ($null -eq $case) { throw 'Frozen fixture is missing unicode-and-nul.' }
    $steps = Require-JsonArray (Get-ExactField $case 'steps') 'unicode-and-nul.steps'
    if ($steps.Count -ne 3) { throw 'unicode-and-nul must retain exactly three oracle steps.' }
    $expectedInit = Get-ExactField (Get-ExactField $steps[0] 'expected') 'state'
    $expectedPending = Get-ExactField (Get-ExactField $steps[1] 'expected') 'state'
    $expectedContinuation = Get-ExactField (Get-ExactField $steps[1] 'expected') 'continuation'
    $expectedCompleted = Get-ExactField (Get-ExactField $steps[2] 'expected') 'state'
    $initDocument = ConvertFrom-JsonText (Require-JsonString (Get-ExactField $unicode 'initializedJson') "$Optimization interpreter initializedJson") "$Optimization interpreter initializedJson"
    $pendingDocument = ConvertFrom-JsonText (Require-JsonString (Get-ExactField $unicode 'pendingJson') "$Optimization interpreter pendingJson") "$Optimization interpreter pendingJson"
    $completedDocument = ConvertFrom-JsonText (Require-JsonString (Get-ExactField $unicode 'completedJson') "$Optimization interpreter completedJson") "$Optimization interpreter completedJson"
    $initValues = Require-JsonArray (Get-ExactField $initDocument 'values') "$Optimization interpreter initialized values"
    $pendingValues = Require-JsonArray (Get-ExactField $pendingDocument 'values') "$Optimization interpreter pending values"
    $completedValues = Require-JsonArray (Get-ExactField $completedDocument 'values') "$Optimization interpreter completed values"
    if ($initValues.Count -ne 1 -or $pendingValues.Count -ne 2 -or $completedValues.Count -ne 1) { throw "$Optimization interpreter Unicode output arity is incorrect." }
    Compare-InterpreterRecord $initValues[0] 'State' $expectedInit "$Optimization interpreter initialized"
    Compare-InterpreterRecord $pendingValues[0] 'State' $expectedPending "$Optimization interpreter pending"
    Compare-InterpreterRecord $pendingValues[1] 'Continuation' $expectedContinuation "$Optimization interpreter pending"
    Compare-InterpreterRecord $completedValues[0] 'State' $expectedCompleted "$Optimization interpreter completed"
    Add-Check "$Optimization interpreter output is an independent cross-backend check against the frozen fixture" $true ([ordered]@{ caseId = 'unicode-and-nul'; usedAsNativeOracle = $false })
    return $interpreter
}

function Get-ExpectedIoRequests($OracleCase) {
    $steps = Require-JsonArray (Get-ExactField $OracleCase 'steps') "oracle case $((Get-ExactField $OracleCase 'id')).steps"
    $requests = 0
    $failedTokenByMailbox = [Collections.Generic.Dictionary[string, bool]]::new([StringComparer]::Ordinal)
    foreach ($step in $steps) {
        $op = Require-JsonString (Get-ExactField $step 'op') 'oracle step op'
        $mailbox = Require-JsonString (Get-ExactField $step 'mailbox') 'oracle step mailbox'
        $expected = Get-ExactField $step 'expected'
        $hasError = Has-ExactKey $expected 'error'
        if ($op -ceq 'begin') { $requests++ }
        elseif ($op -ceq 'resume') {
            if ($failedTokenByMailbox.ContainsKey($mailbox) -and $failedTokenByMailbox[$mailbox]) { $requests++ }
        }
        if ($hasError) { $failedTokenByMailbox[$mailbox] = $true }
        elseif ($op -ceq 'resume') { $failedTokenByMailbox[$mailbox] = $false }
    }
    return $requests
}

function Get-ExpectedHandlerCounts($OracleCase) {
    $steps = Require-JsonArray (Get-ExactField $OracleCase 'steps') "oracle case $((Get-ExactField $OracleCase 'id')).steps"
    $invocations = 0
    $failures = 0
    foreach ($step in $steps) {
        $op = Require-JsonString (Get-ExactField $step 'op') 'oracle step op'
        if ($op -in @('initialize', 'begin', 'resume')) { $invocations++ }
        if (Has-ExactKey (Get-ExactField $step 'expected') 'error') { $failures++ }
    }
    return [ordered]@{ invocations = $invocations; failures = $failures }
}

function Compare-ExpectedState($Actual, $Expected, [string]$Label) {
    Require-Fields $Actual @('attempted', 'completed', 'latest') "$Label state"
    Require-Fields $Expected @('attempted', 'completed', 'latest') "$Label expected state"
    foreach ($field in @('attempted', 'completed')) {
        $actualValue = Require-JsonInteger (Get-ExactField $Actual $field) "$Label.state.$field" -NonNegative
        $expectedValue = Require-JsonInteger (Get-ExactField $Expected $field) "$Label.expected.state.$field" -NonNegative
        Add-Check "$Label state.$field matches the frozen Int64 value" ($actualValue -eq $expectedValue) ([ordered]@{ expected = $expectedValue; actual = $actualValue })
    }
    $actualLatest = Require-JsonString (Get-ExactField $Actual 'latest') "$Label.state.latest"
    $expectedLatest = Require-JsonString (Get-ExactField $Expected 'latest') "$Label.expected.state.latest"
    Assert-StringEqual $actualLatest $expectedLatest "$Label state.latest matches the exact frozen Unicode/string value"
}

function Compare-ExpectedContinuation($Actual, $Expected, [string]$Label) {
    if ($null -eq $Expected) {
        Add-Check "$Label continuation is explicitly null" ($null -eq $Actual) ([ordered]@{ expected = $null; actual = $Actual })
        return
    }
    Require-Fields $Actual @('request') "$Label continuation"
    Require-Fields $Expected @('request') "$Label expected continuation"
    $actualRequest = Require-JsonString (Get-ExactField $Actual 'request') "$Label.continuation.request"
    $expectedRequest = Require-JsonString (Get-ExactField $Expected 'request') "$Label.expected.continuation.request"
    Assert-StringEqual $actualRequest $expectedRequest "$Label continuation.request matches the exact frozen string"
}

function Get-ExpectedContinuation($Step, $PreviousContinuation) {
    $expected = Get-ExactField $Step 'expected'
    if (Has-ExactKey $expected 'continuation') { return Get-ExactField $expected 'continuation' }
    $op = [string](Get-ExactField $Step 'op')
    if ($op -ceq 'initialize' -or $op -ceq 'begin') {
        if ($op -ceq 'begin') { return Get-ExactField $expected 'continuation' }
        return $null
    }
    if ((Has-ExactKey $expected 'error') -and $null -ne $PreviousContinuation) { return $PreviousContinuation }
    return $null
}

function Get-ExpectedPending($Step) {
    $expected = Get-ExactField $Step 'expected'
    if (Has-ExactKey $expected 'tokenRemainsPending') { return Require-JsonBoolean (Get-ExactField $expected 'tokenRemainsPending') 'oracle expected.tokenRemainsPending' }
    if (Has-ExactKey $expected 'pendingToken') { return $null -ne (Get-ExactField $expected 'pendingToken') }
    $op = [string](Get-ExactField $Step 'op')
    if ($op -ceq 'begin') { return $true }
    if ($op -ceq 'initialize' -or $op -ceq 'host.cancelAfterProviderAcknowledgement') { return $false }
    return (Has-ExactKey $expected 'error')
}

function Get-TokenArray($Value, [string]$Label) {
    if ($null -eq $Value) { return $null }
    $items = Require-JsonArray $Value $Label
    if ($items.Count -ne 3) { throw "$Label must contain exactly three opaque uint64 strings." }
    $normalized = [Collections.Generic.List[string]]::new()
    for ($index = 0; $index -lt 3; $index++) {
        $tokenPart = Require-JsonString $items[$index] "$Label[$index]"
        if ($tokenPart -cnotmatch '^[0-9a-f]{16}$') { throw "$Label[$index] must be 16 lowercase hexadecimal characters." }
        $normalized.Add($tokenPart)
    }
    return ,@($normalized)
}

function Compare-Token($Actual, $ExpectedToken, [string]$Label) {
    if ($null -eq $ExpectedToken) {
        Add-Check "$Label token is retired/null" ($null -eq $Actual) ([ordered]@{ expected = $null; actual = $Actual })
        return $null
    }
    $actualArray = Get-TokenArray $Actual "$Label actual token"
    $expectedArray = Get-TokenArray $ExpectedToken "$Label expected token"
    if ($null -eq $actualArray -or $null -eq $expectedArray) {
        Add-Check "$Label token remains present" $false
        return $actualArray
    }
    $equal = (@($actualArray) -join ',') -ceq (@($expectedArray) -join ',')
    Add-Check "$Label token alias matches its earlier opaque capability" $equal ([ordered]@{ expected = $expectedArray; actual = $actualArray })
    return ,@($actualArray)
}

function Validate-NativeStateObject($Actual, $Expected, [string]$Label) {
    Compare-ExpectedState $Actual $Expected $Label
}

function Validate-OtherMailboxes($Actual, $Expected, $ActiveTokens, [string]$Label) {
    Require-Object $Actual "$Label otherMailboxes"
    if ($null -eq $Expected) { $Expected = [ordered]@{} }
    Require-Object $Expected "$Label expected otherMailboxes"
    $actualNames = @($Actual.Keys | ForEach-Object { [string]$_ } | Sort-Object -CaseSensitive)
    $expectedNames = @($Expected.Keys | ForEach-Object { [string]$_ } | Sort-Object -CaseSensitive)
    $namesEqual = (@($actualNames) -join "`n") -ceq (@($expectedNames) -join "`n")
    Add-Check "$Label other-mailbox key set matches the frozen case" $namesEqual ([ordered]@{ expected = $expectedNames; actual = $actualNames })
    foreach ($mailbox in $expectedNames) {
        if (-not (Has-ExactKey $Actual $mailbox)) { continue }
        $actualMailbox = Get-ExactField $Actual $mailbox
        $expectedMailbox = Get-ExactField $Expected $mailbox
        Require-Fields $actualMailbox @('state', 'continuation', 'pending', 'token') "$Label other mailbox $mailbox"
        Require-Fields $expectedMailbox @('state', 'continuation', 'token') "$Label expected other mailbox $mailbox"
        Validate-NativeStateObject (Get-ExactField $actualMailbox 'state') (Get-ExactField $expectedMailbox 'state') "$Label other mailbox $mailbox"
        $expectedContinuation = Get-ExactField $expectedMailbox 'continuation'
        Compare-ExpectedContinuation (Get-ExactField $actualMailbox 'continuation') $expectedContinuation "$Label other mailbox $mailbox"
        $expectedPending = $null -ne (Get-ExactField $expectedMailbox 'token')
        $actualPending = Require-JsonBoolean (Get-ExactField $actualMailbox 'pending') "$Label other mailbox $mailbox.pending"
        Add-Check "$Label other mailbox $mailbox pending flag agrees with its frozen token state" ($actualPending -eq $expectedPending)
        $expectedTokenLabel = Get-ExactField $expectedMailbox 'token'
        if ($null -eq $expectedTokenLabel) {
            $null = Compare-Token (Get-ExactField $actualMailbox 'token') $null "$Label other mailbox $mailbox"
        } else {
            $expectedTokenName = Require-JsonString $expectedTokenLabel "$Label expected other mailbox $mailbox token label"
            if (-not $ActiveTokens.ContainsKey($mailbox)) { throw "$Label expected token label $expectedTokenName is not associated with mailbox $mailbox." }
            if ($ActiveTokens[$mailbox].label -cne $expectedTokenName) { throw "$Label other mailbox $mailbox token label differs from its active token." }
            $null = Compare-Token (Get-ExactField $actualMailbox 'token') $ActiveTokens[$mailbox].value "$Label other mailbox $mailbox"
        }
    }
}

function Compare-DeclaredOtherMailboxes($Declared, $Derived, [string]$Label) {
    Require-Object $Declared "$Label declared oracle otherMailboxes"
    $declaredNames = @($Declared.Keys | ForEach-Object { [string]$_ } | Sort-Object -CaseSensitive)
    $derivedNames = @($Derived.Keys | ForEach-Object { [string]$_ } | Sort-Object -CaseSensitive)
    $namesEqual = (@($declaredNames) -join "`n") -ceq (@($derivedNames) -join "`n")
    Add-Check "$Label explicit other-mailbox oracle names agree with prior frozen steps" $namesEqual ([ordered]@{ expected = $declaredNames; derived = $derivedNames })
    foreach ($name in $declaredNames) {
        if (-not (Has-ExactKey $Derived $name)) { continue }
        $declared = Get-ExactField $Declared $name
        $derived = Get-ExactField $Derived $name
        Compare-ExpectedState (Get-ExactField $derived 'state') (Get-ExactField $declared 'state') "$Label declared other mailbox $name"
        Compare-ExpectedContinuation (Get-ExactField $derived 'continuation') (Get-ExactField $declared 'continuation') "$Label declared other mailbox $name"
        $declaredToken = Get-ExactField $declared 'token'
        $derivedToken = Get-ExactField $derived 'token'
        Add-Check "$Label declared other mailbox $name token label agrees with prior fixture history" (
            ($null -eq $declaredToken -and $null -eq $derivedToken) -or
            ($null -ne $declaredToken -and $null -ne $derivedToken -and [string]::Equals([string]$declaredToken, [string]$derivedToken, [StringComparison]::Ordinal))) ([ordered]@{ declared = $declaredToken; derived = $derivedToken })
    }
}

function Validate-CaseSteps($NativeCase, $OracleCase, [string]$Policy, [string]$Optimization, [int]$ExpectedDivideDiagnosticId) {
    $caseId = Require-JsonString (Get-ExactField $OracleCase 'id') 'oracle case id'
    Require-Fields $NativeCase @('id', 'steps', 'io', 'ioEvents', 'stats', 'afterDisposeStats') "$Policy/$Optimization native case"
    $nativeId = Require-JsonString (Get-ExactField $NativeCase 'id') "$Policy/$Optimization native case id"
    Assert-StringEqual $nativeId $caseId "$Policy/$Optimization case ID follows frozen oracle order"
    $oracleSteps = Require-JsonArray (Get-ExactField $OracleCase 'steps') "$caseId oracle steps"
    $nativeSteps = Require-JsonArray (Get-ExactField $NativeCase 'steps') "$caseId native steps"
    Add-Check "$Policy/$Optimization $caseId has the exact frozen step count" ($nativeSteps.Count -eq $oracleSteps.Count) ([ordered]@{ expected = $oracleSteps.Count; actual = $nativeSteps.Count })
    if ($nativeSteps.Count -ne $oracleSteps.Count) { throw "$Policy/$Optimization $caseId native step count differs from fixture." }
    $activeTokens = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $continuations = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $expectedStates = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $initializedMailboxes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $normalizedSteps = [Collections.Generic.List[object]]::new()
    $mailboxNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    for ($index = 0; $index -lt $oracleSteps.Count; $index++) {
        $oracleStep = $oracleSteps[$index]
        $actualStep = $nativeSteps[$index]
        $label = "$Policy/$Optimization $caseId step $index"
        Require-Fields $actualStep @('step', 'op', 'mailbox', 'status', 'handlerStatus', 'errorMetadataId', 'pending', 'state', 'continuation', 'token', 'otherMailboxes') $label
        $stepNumber = Require-JsonInteger (Get-ExactField $actualStep 'step') "$label.step" -NonNegative
        Add-Check "$label uses the zero-based fixture step index" ($stepNumber -eq $index) ([ordered]@{ expected = $index; actual = $stepNumber })
        $expectedOp = Require-JsonString (Get-ExactField $oracleStep 'op') "$label expected op"
        $actualOp = Require-JsonString (Get-ExactField $actualStep 'op') "$label op"
        Assert-StringEqual $actualOp $expectedOp "$label op matches frozen fixture"
        $mailbox = Require-JsonString (Get-ExactField $oracleStep 'mailbox') "$label expected mailbox"
        $actualMailbox = Require-JsonString (Get-ExactField $actualStep 'mailbox') "$label mailbox"
        Assert-StringEqual $actualMailbox $mailbox "$label mailbox matches frozen fixture"
        [void]$mailboxNames.Add($mailbox)
        $expected = Get-ExactField $oracleStep 'expected'
        Require-Object $expected "$label expected"
        $expectedError = if (Has-ExactKey $expected 'error') { Require-JsonString (Get-ExactField $expected 'error') "$label expected error" } else { $null }
        $status = Require-JsonInteger (Get-ExactField $actualStep 'status') "$label.status"
        $handlerStatus = Require-JsonInteger (Get-ExactField $actualStep 'handlerStatus') "$label.handlerStatus"
        $errorMetadataId = Require-JsonInteger (Get-ExactField $actualStep 'errorMetadataId') "$label.errorMetadataId"
        if ($null -ne $expectedError) {
            Add-Check "$label frozen checked-divide failure is named" ($expectedError -ceq 'checked-divide-by-zero') ([ordered]@{ expected = 'checked-divide-by-zero'; actual = $expectedError })
            Add-Check "$label native API returns AL_MAILBOX_HANDLER_FAILURE for the checked divide" ($status -eq 17 -and $handlerStatus -eq 1) ([ordered]@{ expectedStatus = 17; actualStatus = $status; expectedHandlerStatus = 1; actualHandlerStatus = $handlerStatus })
            Add-Check "$label native diagnostic metadata ID matches RUNTIME_DIVIDE_BY_ZERO" ($errorMetadataId -eq $ExpectedDivideDiagnosticId) ([ordered]@{ expected = $ExpectedDivideDiagnosticId; actual = $errorMetadataId; code = 'RUNTIME_DIVIDE_BY_ZERO' })
        } else {
            Add-Check "$label native API and handler report success" ($status -eq 0 -and $handlerStatus -eq 0 -and $errorMetadataId -eq -1) ([ordered]@{ expectedStatus = 0; actualStatus = $status; expectedHandlerStatus = 0; actualHandlerStatus = $handlerStatus; expectedErrorMetadataId = -1; actualErrorMetadataId = $errorMetadataId })
        }
        Validate-NativeStateObject (Get-ExactField $actualStep 'state') (Get-ExactField $expected 'state') $label
        $previousContinuation = if ($continuations.ContainsKey($mailbox)) { $continuations[$mailbox] } else { $null }
        $expectedContinuation = Get-ExpectedContinuation $oracleStep $previousContinuation
        Compare-ExpectedContinuation (Get-ExactField $actualStep 'continuation') $expectedContinuation $label
        if ($null -eq $expectedContinuation) { $continuations.Remove($mailbox) | Out-Null } else { $continuations[$mailbox] = $expectedContinuation }
        $expectedPending = Get-ExpectedPending $oracleStep
        $actualPending = Require-JsonBoolean (Get-ExactField $actualStep 'pending') "$label.pending"
        Add-Check "$label pending flag matches frozen operation outcome" ($actualPending -eq $expectedPending) ([ordered]@{ expected = $expectedPending; actual = $actualPending })
        $expectedTokenLabel = if (Has-ExactKey $oracleStep 'token') { Require-JsonString (Get-ExactField $oracleStep 'token') "$label oracle token label" } else { $null }
        $actualTokenRaw = Get-ExactField $actualStep 'token'
        if ($expectedOp -ceq 'begin') {
            if ($activeTokens.ContainsKey($mailbox)) { throw "$label begins while the mailbox already has an active token." }
            $actualToken = Get-TokenArray $actualTokenRaw "$label begin token"
            if ($null -eq $actualToken) { throw "$label Begin must return an active token." }
            $tokenLabel = if ($null -ne $expectedTokenLabel) { $expectedTokenLabel } else { "implicit:${caseId}:${mailbox}:${index}" }
            $activeTokens[$mailbox] = [ordered]@{ label = $tokenLabel; value = @($actualToken) }
            Add-Check "$label Begin creates a well-formed opaque token" $true ([ordered]@{ label = $tokenLabel })
        } elseif ($expectedOp -ceq 'initialize') {
            if ($null -ne $actualTokenRaw) { Add-Check "$label initialization has no pending token" $false $actualTokenRaw }
        } else {
            $currentToken = if ($activeTokens.ContainsKey($mailbox)) { $activeTokens[$mailbox] } else { $null }
            if ($null -ne $expectedTokenLabel -and ($null -eq $currentToken -or $currentToken.label -cne $expectedTokenLabel)) {
                Add-Check "$label uses the fixture's named token alias" $false ([ordered]@{ expectedLabel = $expectedTokenLabel; currentLabel = if ($null -eq $currentToken) { $null } else { $currentToken.label } })
            } elseif ($null -ne $expectedTokenLabel) {
                Add-Check "$label uses the fixture's named token alias" $true ([ordered]@{ expectedLabel = $expectedTokenLabel })
            }
            if ($expectedPending) {
                if ($null -eq $currentToken) { throw "$label must retain an active token after this operation." }
                $actualToken = Compare-Token $actualTokenRaw $currentToken.value $label
                if ($null -ne $expectedTokenLabel -and $currentToken.label -cne $expectedTokenLabel) { Add-Check "$label returned the expected named token alias" $false }
            } else {
                $null = Compare-Token $actualTokenRaw $null $label
                if ($activeTokens.ContainsKey($mailbox)) { $activeTokens.Remove($mailbox) | Out-Null }
            }
        }
        if ($actualPending -ne ($null -ne $actualTokenRaw)) { Add-Check "$label pending flag agrees with token presence" $false ([ordered]@{ pending = $actualPending; tokenPresent = $null -ne $actualTokenRaw }) }
        $expectedStates[$mailbox] = Get-ExactField $expected 'state'
        [void]$initializedMailboxes.Add($mailbox)
        $derivedOthers = [ordered]@{}
        foreach ($otherName in @($initializedMailboxes | Where-Object { $_ -cne $mailbox } | Sort-Object -CaseSensitive)) {
            $otherTokenLabel = if ($activeTokens.ContainsKey($otherName)) { $activeTokens[$otherName].label } else { $null }
            $otherContinuation = if ($continuations.ContainsKey($otherName)) { $continuations[$otherName] } else { $null }
            $derivedOthers[$otherName] = [ordered]@{
                state = $expectedStates[$otherName]
                continuation = $otherContinuation
                token = $otherTokenLabel
            }
        }
        if (Has-ExactKey $expected 'otherMailboxes') { Compare-DeclaredOtherMailboxes (Get-ExactField $expected 'otherMailboxes') $derivedOthers $label }
        Validate-OtherMailboxes (Get-ExactField $actualStep 'otherMailboxes') $derivedOthers $activeTokens $label
        $normalizedOthers = [Collections.Generic.List[object]]::new()
        $actualState = Get-ExactField $actualStep 'state'
        $actualContinuation = Get-ExactField $actualStep 'continuation'
        $actualOthers = Get-ExactField $actualStep 'otherMailboxes'
        foreach ($otherName in @($actualOthers.Keys | ForEach-Object { [string]$_ } | Sort-Object -CaseSensitive)) {
            $otherActual = Get-ExactField $actualOthers $otherName
            $otherState = Get-ExactField $otherActual 'state'
            $otherContinuation = Get-ExactField $otherActual 'continuation'
            $normalizedOthers.Add([ordered]@{
                mailbox = $otherName
                state = [ordered]@{ attempted = (Get-ExactField $otherState 'attempted'); completed = (Get-ExactField $otherState 'completed'); latest = (Get-ExactField $otherState 'latest') }
                continuation = if ($null -eq $otherContinuation) { $null } else { [ordered]@{ request = (Get-ExactField $otherContinuation 'request') } }
                pending = (Get-ExactField $otherActual 'pending')
            })
        }
        $normalizedSteps.Add([ordered]@{
            step = $index
            op = $expectedOp
            mailbox = $mailbox
            status = $status
            handlerStatus = $handlerStatus
            errorMetadataId = $errorMetadataId
            pending = $actualPending
            state = [ordered]@{ attempted = (Get-ExactField $actualState 'attempted'); completed = (Get-ExactField $actualState 'completed'); latest = (Get-ExactField $actualState 'latest') }
            continuation = if ($null -eq $actualContinuation) { $null } else { [ordered]@{ request = (Get-ExactField $actualContinuation 'request') } }
            otherMailboxes = @($normalizedOthers)
        })
    }
    Add-Check "$Policy/$Optimization $caseId retires every mailbox token by case end" ($activeTokens.Count -eq 0) ([ordered]@{ activeMailboxTokens = @($activeTokens.Keys) })
    $ioCheck = Validate-IoAndStats $NativeCase $OracleCase $Policy $Optimization @($mailboxNames)
    return [ordered]@{ caseId = $caseId; normalizedSteps = @($normalizedSteps); io = $ioCheck.io; stats = $ioCheck.stats; afterDisposeStats = $ioCheck.afterDisposeStats }
}

function Validate-IoAndStats($NativeCase, $OracleCase, [string]$Policy, [string]$Optimization, [string[]]$MailboxNames) {
    $caseId = [string](Get-ExactField $OracleCase 'id')
    $label = "$Policy/$Optimization $caseId"
    $io = Get-ExactField $NativeCase 'io'
    Require-Fields $io @('requests', 'receiveSubmissions', 'receiveCompletions', 'cancelRequests', 'cancelAcknowledgements', 'peakPendingReceives', 'pendingReceivesAtEnd') "$label io"
    $ioValues = [ordered]@{}
    foreach ($field in @('requests', 'receiveSubmissions', 'receiveCompletions', 'cancelRequests', 'cancelAcknowledgements', 'peakPendingReceives', 'pendingReceivesAtEnd')) {
        $ioValues[$field] = Require-JsonInteger (Get-ExactField $io $field) "$label.io.$field" -NonNegative
    }
    $events = Require-JsonArray (Get-ExactField $NativeCase 'ioEvents') "$label ioEvents"
    $eventCounts = [ordered]@{ 'provider-request' = 0; 'recv-submit' = 0; 'recv-terminal' = 0; 'cancel-request' = 0; 'cancel-api' = 0 }
    $lastSequence = $null
    $eventRows = [Collections.Generic.List[object]]::new()
    $outstandingByMailbox = [Collections.Generic.Dictionary[string, int]]::new([StringComparer]::Ordinal)
    $firstProviderEventByMailbox = [Collections.Generic.Dictionary[string, long]]::new([StringComparer]::Ordinal)
    $firstSubmitByMailbox = [Collections.Generic.Dictionary[string, long]]::new([StringComparer]::Ordinal)
    $synchronousTerminalCount = 0
    $maximumPendingSnapshot = 0
    $cancelRequestEvent = $null
    $terminalAfterCancel = $null
    $cancelApiEvent = $null
    $terminalCancellationAcknowledgements = 0
    foreach ($event in $events) {
        Require-Fields $event @('sequence', 'kind', 'mailbox', 'pendingReceives') "$label io event"
        $sequence = Require-JsonInteger (Get-ExactField $event 'sequence') "$label io event sequence" -NonNegative
        $kind = Require-JsonString (Get-ExactField $event 'kind') "$label io event kind"
        $mailbox = Require-JsonString (Get-ExactField $event 'mailbox') "$label io event mailbox"
        $pendingSnapshot = Require-JsonInteger (Get-ExactField $event 'pendingReceives') "$label io event pendingReceives" -NonNegative
        if ($null -ne $lastSequence -and $sequence -le $lastSequence) { Add-Check "$label event sequence is strictly monotonic" $false ([ordered]@{ previous = $lastSequence; actual = $sequence }) }
        else { Add-Check "$label event sequence is strictly monotonic" $true ([ordered]@{ previous = $lastSequence; actual = $sequence }) }
        $lastSequence = $sequence
        if ($mailbox -notin $MailboxNames) { Add-Check "$label I/O event names an oracle mailbox" $false ([ordered]@{ mailbox = $mailbox; allowed = $MailboxNames }) }
        if (@($eventCounts.Keys) -cnotcontains $kind) { Add-Check "$label I/O event kind is recognized" $false ([ordered]@{ kind = $kind }); continue }
        $eventCounts[$kind]++
        if ($kind -ceq 'provider-request') {
            Require-Fields $event @('delayMs', 'payloadBytes', 'payload') "$label provider-request event"
            $delayMs = Require-JsonInteger (Get-ExactField $event 'delayMs') "$label provider-request.delayMs" -NonNegative
            $payloadBytes = Require-JsonInteger (Get-ExactField $event 'payloadBytes') "$label provider-request.payloadBytes" -NonNegative
            $payload = Require-JsonString (Get-ExactField $event 'payload') "$label provider-request.payload"
            $actualPayloadBytes = [Text.Encoding]::UTF8.GetByteCount($payload)
            Add-Check "$label provider request payload byte count matches exact UTF-8 payload" ($payloadBytes -eq $actualPayloadBytes -and $payloadBytes -le 4096) ([ordered]@{ declaredBytes = $payloadBytes; actualBytes = $actualPayloadBytes; maximumBytes = 4096 })
            Add-Check "$label provider request delay is within the fixture protocol bound" ($delayMs -le 1000) ([ordered]@{ delayMs = $delayMs; maximumDelayMs = 1000 })
            if (-not $firstProviderEventByMailbox.ContainsKey($mailbox)) { $firstProviderEventByMailbox[$mailbox] = $sequence }
        } elseif ($kind -ceq 'recv-submit') {
            Require-Fields $event @('asynchronous') "$label recv-submit event"
            $null = Require-JsonBoolean (Get-ExactField $event 'asynchronous') "$label recv-submit.asynchronous"
            if (-not $firstSubmitByMailbox.ContainsKey($mailbox)) { $firstSubmitByMailbox[$mailbox] = $sequence }
            if (-not $outstandingByMailbox.ContainsKey($mailbox)) { $outstandingByMailbox[$mailbox] = 0 }
            $outstandingByMailbox[$mailbox]++
        } elseif ($kind -ceq 'recv-terminal') {
            Require-Fields $event @('error', 'transferred', 'cancelAcknowledgement') "$label recv-terminal event"
            $terminalError = Require-JsonInteger (Get-ExactField $event 'error') "$label recv-terminal.error" -NonNegative
            $null = Require-JsonInteger (Get-ExactField $event 'transferred') "$label recv-terminal.transferred" -NonNegative
            $isCancelAcknowledgement = Require-JsonBoolean (Get-ExactField $event 'cancelAcknowledgement') "$label recv-terminal.cancelAcknowledgement"
            if ($isCancelAcknowledgement) { $terminalCancellationAcknowledgements++ }
            if (-not $outstandingByMailbox.ContainsKey($mailbox) -or $outstandingByMailbox[$mailbox] -le 0) {
                if ($terminalError -ne 0) {
                    # WSARecv may fail synchronously before an overlapped receive is pending;
                    # the host records that terminal event without a recv-submit event.
                    $synchronousTerminalCount++
                } else {
                    Add-Check "$label terminal completion corresponds to a submitted receive or synchronous WSARecv error" $false ([ordered]@{ event = $event })
                }
            } else { $outstandingByMailbox[$mailbox]-- }
            if ($null -ne $cancelRequestEvent -and $null -eq $terminalAfterCancel -and $sequence -gt [long]$cancelRequestEvent.sequence -and $mailbox -ceq [string]$cancelRequestEvent.mailbox -and $isCancelAcknowledgement) { $terminalAfterCancel = $event }
        } elseif ($kind -ceq 'cancel-request') {
            Require-Fields $event @('result', 'error', 'cancelIoSucceeded', 'pendingReceives', 'pinnedScratchSlots') "$label cancel-request event"
            $result = Require-JsonInteger (Get-ExactField $event 'result') "$label cancel-request.result"
            $cancelError = Require-JsonInteger (Get-ExactField $event 'error') "$label cancel-request.error" -NonNegative
            $cancelIoSucceeded = Require-JsonBoolean (Get-ExactField $event 'cancelIoSucceeded') "$label cancel-request.cancelIoSucceeded"
            $null = Require-JsonInteger (Get-ExactField $event 'pinnedScratchSlots') "$label cancel-request.pinnedScratchSlots" -NonNegative
            Add-Check "$label CancelIoEx succeeds while a receive is pending" ($result -eq 1 -and $cancelIoSucceeded -and $cancelError -eq 0 -and $pendingSnapshot -gt 0) ([ordered]@{ result = $result; cancelIoSucceeded = $cancelIoSucceeded; error = $cancelError; pendingReceives = $pendingSnapshot })
            $cancelRequestEvent = $event
        } elseif ($kind -ceq 'cancel-api') {
            Require-Fields $event @('result', 'pinnedScratchSlots') "$label cancel-api event"
            $result = Require-JsonInteger (Get-ExactField $event 'result') "$label cancel-api.result"
            $pinnedAfter = Require-JsonInteger (Get-ExactField $event 'pinnedScratchSlots') "$label cancel-api.pinnedScratchSlots" -NonNegative
            Add-Check "$label al_mailbox_cancel_text succeeds after terminal acknowledgement" ($result -eq 0 -and $pendingSnapshot -eq 0 -and $pinnedAfter -eq 0) ([ordered]@{ result = $result; pendingReceives = $pendingSnapshot; pinnedScratchSlots = $pinnedAfter })
            $cancelApiEvent = $event
        }
        $trackedPendingReceives = 0
        foreach ($pendingCount in $outstandingByMailbox.Values) { $trackedPendingReceives += $pendingCount }
        Add-Check "$label event pendingReceives snapshot matches outstanding overlapped WSARecv operations" ($pendingSnapshot -eq $trackedPendingReceives) ([ordered]@{ sequence = $sequence; kind = $kind; expected = $trackedPendingReceives; actual = $pendingSnapshot })
        if ($trackedPendingReceives -gt $maximumPendingSnapshot) { $maximumPendingSnapshot = $trackedPendingReceives }
        $eventRows.Add([ordered]@{ sequence = $sequence; kind = $kind; mailbox = $mailbox; pendingReceives = $pendingSnapshot })
    }
    $expectedRequests = Get-ExpectedIoRequests $OracleCase
    Add-Check "$label provider frames match Begin and failed-token retry schedule" ($ioValues.requests -eq $expectedRequests -and $eventCounts['provider-request'] -eq $expectedRequests) ([ordered]@{ expected = $expectedRequests; ioRequests = $ioValues.requests; providerRequestEvents = $eventCounts['provider-request'] })
    Add-Check "$label receive counters match pending and synchronous WSARecv events" ($ioValues.receiveSubmissions -eq ($eventCounts['recv-submit'] + $synchronousTerminalCount) -and $ioValues.receiveCompletions -eq $eventCounts['recv-terminal']) ([ordered]@{ receiveSubmissions = $ioValues.receiveSubmissions; submitEvents = $eventCounts['recv-submit']; synchronousTerminalErrors = $synchronousTerminalCount; receiveCompletions = $ioValues.receiveCompletions; terminalEvents = $eventCounts['recv-terminal'] })
    Add-Check "$label every submitted receive has one terminal completion and pending I/O drains" ($ioValues.receiveSubmissions -eq $ioValues.receiveCompletions -and $ioValues.pendingReceivesAtEnd -eq 0 -and @($outstandingByMailbox.Values | Where-Object { $_ -ne 0 }).Count -eq 0) ([ordered]@{ receiveSubmissions = $ioValues.receiveSubmissions; receiveCompletions = $ioValues.receiveCompletions; pendingAtEnd = $ioValues.pendingReceivesAtEnd; outstandingByMailbox = $outstandingByMailbox })
    Add-Check "$label peak pending receives matches the recorded outstanding receive trace" ($ioValues.peakPendingReceives -eq $maximumPendingSnapshot) ([ordered]@{ expectedFromEvents = $maximumPendingSnapshot; actual = $ioValues.peakPendingReceives })
    Add-Check "$label starts at least one overlapped receive per provider frame" ($ioValues.receiveSubmissions -ge $expectedRequests) ([ordered]@{ providerFrames = $expectedRequests; receiveSubmissions = $ioValues.receiveSubmissions })
    $isCancelCase = $caseId -ceq 'cancel-after-begin-preserves-updated-state'
    $expectedCancelCount = if ($isCancelCase) { 1 } else { 0 }
    Add-Check "$label cancellation request and acknowledgement counts match the frozen case" ($ioValues.cancelRequests -eq $expectedCancelCount -and $ioValues.cancelAcknowledgements -eq $expectedCancelCount -and $eventCounts['cancel-request'] -eq $expectedCancelCount -and $eventCounts['cancel-api'] -eq $expectedCancelCount) ([ordered]@{ expected = $expectedCancelCount; io = $ioValues; eventCounts = $eventCounts })
    Add-Check "$label terminal event cancellation acknowledgement flags match the frozen case" ($terminalCancellationAcknowledgements -eq $expectedCancelCount) ([ordered]@{ expected = $expectedCancelCount; actual = $terminalCancellationAcknowledgements })
    if ($isCancelCase) {
        $ordered = $null -ne $cancelRequestEvent -and $null -ne $terminalAfterCancel -and $null -ne $cancelApiEvent -and
            [long]$cancelRequestEvent.sequence -lt [long]$terminalAfterCancel.sequence -and
            [long]$terminalAfterCancel.sequence -lt [long]$cancelApiEvent.sequence
        Add-Check "$label cancellation API follows terminal IOCP acknowledgement" $ordered ([ordered]@{ cancelRequest = if ($null -eq $cancelRequestEvent) { $null } else { $cancelRequestEvent.sequence }; terminalAcknowledgement = if ($null -eq $terminalAfterCancel) { $null } else { $terminalAfterCancel.sequence }; cancelApi = if ($null -eq $cancelApiEvent) { $null } else { $cancelApiEvent.sequence } })
        $expectedPinnedBefore = if ($Policy -ceq 'keep') { 1 } else { 0 }
        if ($null -ne $cancelRequestEvent) {
            $pinnedBefore = Require-JsonInteger (Get-ExactField $cancelRequestEvent 'pinnedScratchSlots') "$label cancel-request pinned snapshot" -NonNegative
            Add-Check "$label cancellation observes the policy's expected pending scratch pin" ($pinnedBefore -eq $expectedPinnedBefore) ([ordered]@{ policy = $Policy; expected = $expectedPinnedBefore; actual = $pinnedBefore })
        }
    } else {
        Add-Check "$label emits no cancellation event" ($null -eq $cancelRequestEvent -and $null -eq $cancelApiEvent)
    }
    if ($caseId -ceq 'two-mailboxes-remain-independent') {
        Add-Check "$label has at least two receive operations outstanding concurrently" ($ioValues.peakPendingReceives -ge 2) ([ordered]@{ expectedMinimum = 2; actual = $ioValues.peakPendingReceives })
    } else {
        Add-Check "$label peak pending receive count is bounded by provider handler capacity" ($ioValues.peakPendingReceives -le 16)
    }

    $stats = Get-ExactField $NativeCase 'stats'
    $afterDisposeStats = Get-ExactField $NativeCase 'afterDisposeStats'
    $statsFields = @('mailboxCapacity', 'initializedMailboxes', 'pendingMailboxes', 'storageReservedBytes', 'retainedReservedBytes', 'scratchReservedBytes', 'textStagingReservedBytes', 'liveRetainedBytes', 'liveRetainedRoots', 'scratchHighWaterBytes', 'utf8InputBytes', 'utf16StagingBytes', 'inputImportBytes', 'publicationCopyBytes', 'deepCopyBytes', 'moveBytes', 'returnedOutputDescriptors', 'turnResetBytes', 'handlerInvocations', 'handlerFailures', 'scratchLeaseAcquisitions', 'scratchLeaseReturns', 'outstandingScratchLeases', 'scratchSlotCapacity', 'pinnedScratchSlots', 'suspensionPolicy', 'pinnedScratchBytes', 'beginPublicationCopyBytes', 'resumeRootImportBytes')
    Require-Fields $stats $statsFields "$label stats"
    Require-Fields $afterDisposeStats $statsFields "$label afterDisposeStats"
    $statsValues = [ordered]@{}
    $afterValues = [ordered]@{}
    foreach ($field in $statsFields) {
        $statsValues[$field] = Require-JsonInteger (Get-ExactField $stats $field) "$label.stats.$field" -NonNegative
        $afterValues[$field] = Require-JsonInteger (Get-ExactField $afterDisposeStats $field) "$label.afterDisposeStats.$field" -NonNegative
    }
    $handlerCounts = Get-ExpectedHandlerCounts $OracleCase
    $expectedPolicyNumber = if ($Policy -ceq 'return') { 0 } else { 1 }
    $expectedInitialized = $MailboxNames.Count
    Add-Check "$label actual stats reflect the case's mailbox and handler transitions" (
        $statsValues.mailboxCapacity -eq 2 -and
        $statsValues.initializedMailboxes -eq $expectedInitialized -and
        $statsValues.pendingMailboxes -eq 0 -and
        $statsValues.handlerInvocations -eq $handlerCounts.invocations -and
        $statsValues.handlerFailures -eq $handlerCounts.failures -and
        $statsValues.suspensionPolicy -eq $expectedPolicyNumber) ([ordered]@{ mailboxCapacity = $statsValues.mailboxCapacity; initializedMailboxes = $statsValues.initializedMailboxes; expectedInitialized = $expectedInitialized; pendingMailboxes = $statsValues.pendingMailboxes; handlerInvocations = $statsValues.handlerInvocations; expectedHandlerInvocations = $handlerCounts.invocations; handlerFailures = $statsValues.handlerFailures; expectedHandlerFailures = $handlerCounts.failures; suspensionPolicy = $statsValues.suspensionPolicy; expectedPolicy = $expectedPolicyNumber })
    Add-Check "$label scratch leases balance before disposal with no retained pending pin" (
        $statsValues.scratchLeaseAcquisitions -eq $statsValues.scratchLeaseReturns -and
        $statsValues.outstandingScratchLeases -eq 0 -and $statsValues.pinnedScratchSlots -eq 0) ([ordered]@{ acquisitions = $statsValues.scratchLeaseAcquisitions; returns = $statsValues.scratchLeaseReturns; outstanding = $statsValues.outstandingScratchLeases; pinned = $statsValues.pinnedScratchSlots })
    Add-Check "$label disposed stats report no pending mailboxes, scratch leases, or pinned slots" (
        $afterValues.pendingMailboxes -eq 0 -and $afterValues.outstandingScratchLeases -eq 0 -and $afterValues.pinnedScratchSlots -eq 0) ([ordered]@{ pendingMailboxes = $afterValues.pendingMailboxes; outstandingScratchLeases = $afterValues.outstandingScratchLeases; pinnedScratchSlots = $afterValues.pinnedScratchSlots })
    return [ordered]@{ io = $ioValues; events = @($eventRows); stats = $statsValues; afterDisposeStats = $afterValues }
}

function Compare-RunSemantics($Left, $Right, [string]$Label) {
    if ($Left.cases.Count -ne $Right.cases.Count) { Add-Check $Label $false ([ordered]@{ leftCases = $Left.cases.Count; rightCases = $Right.cases.Count }); return }
    $allEqual = $true
    for ($caseIndex = 0; $caseIndex -lt $Left.cases.Count; $caseIndex++) {
        if ($Left.cases[$caseIndex].caseId -cne $Right.cases[$caseIndex].caseId) { $allEqual = $false; continue }
        $leftJson = ConvertTo-Json -InputObject $Left.cases[$caseIndex].normalizedSteps -Depth 100 -Compress
        $rightJson = ConvertTo-Json -InputObject $Right.cases[$caseIndex].normalizedSteps -Depth 100 -Compress
        if (-not [string]::Equals($leftJson, $rightJson, [StringComparison]::Ordinal)) { $allEqual = $false }
    }
    Add-Check $Label $allEqual ([ordered]@{ comparedCases = $Left.cases.Count; excluded = @('opaque token identities', 'runtime counters') })
}

function Compare-RunStorage($Left, $Right, [string]$Label) {
    if ($Left.cases.Count -ne $Right.cases.Count) { Add-Check $Label $false; return }
    $fields = @('mailboxCapacity', 'storageReservedBytes', 'retainedReservedBytes', 'scratchReservedBytes', 'textStagingReservedBytes', 'scratchSlotCapacity')
    $allEqual = $true
    $differences = [Collections.Generic.List[object]]::new()
    for ($caseIndex = 0; $caseIndex -lt $Left.cases.Count; $caseIndex++) {
        foreach ($field in $fields) {
            $leftValue = $Left.cases[$caseIndex].stats[$field]
            $rightValue = $Right.cases[$caseIndex].stats[$field]
            if ($leftValue -ne $rightValue) {
                $allEqual = $false
                $differences.Add([ordered]@{ caseId = $Left.cases[$caseIndex].caseId; field = $field; left = $leftValue; right = $rightValue })
            }
        }
    }
    Add-Check $Label $allEqual ([ordered]@{ comparedFields = $fields; differences = @($differences) })
}

function Start-Provider([string]$ProviderAssemblyPath) {
    $readyPath = Join-Path $script:runDirectory 'provider-ready.json'
    $stopPath = Join-Path $script:runDirectory 'provider-stop.signal'
    $providerWork = Join-Path $script:runDirectory 'provider'
    [IO.Directory]::CreateDirectory($providerWork) | Out-Null
    $script:providerLifecycle.readyFile = $readyPath
    $script:providerLifecycle.stopFile = $stopPath
    $arguments = @($ProviderAssemblyPath, '--port', '0', '--fragment-bytes', '2', '--ready-file', $readyPath, '--stop-file', $stopPath)
    $script:providerHandle = Start-CapturedLongProcess 'loopback-provider' $script:dotnet $arguments $providerWork
    $script:providerLifecycle.started = $null -eq $script:providerHandle.Record.startError
    if (-not $script:providerLifecycle.started) { throw "Provider did not start: $($script:providerHandle.Record.startError)" }
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $readyPath -PathType Leaf) { break }
        if ($script:providerHandle.Process.HasExited) { break }
        Start-Sleep -Milliseconds 50
    }
    if (-not (Test-Path -LiteralPath $readyPath -PathType Leaf)) {
        $exit = if ($script:providerHandle.Process.HasExited) { $script:providerHandle.Process.ExitCode } else { $null }
        Add-Check 'provider publishes a ready file before its bounded startup deadline' $false ([ordered]@{ readyPath = $readyPath; processExitCode = $exit })
        throw "Provider did not publish ready-file within 15 seconds (exit=$exit)."
    }
    $ready = Read-JsonFile $readyPath
    Require-Fields $ready @('address', 'port', 'protocol') 'provider ready file'
    $address = Require-JsonString (Get-ExactField $ready 'address') 'provider ready.address'
    $port = Require-JsonInteger (Get-ExactField $ready 'port') 'provider ready.port' -NonNegative
    $protocol = Get-ExactField $ready 'protocol'
    Require-Fields $protocol @('requestHeaderBytes', 'responseHeaderBytes', 'maximumDelayMs', 'maximumPayloadBytes', 'responseFragmentBytes', 'maximumLiveAcceptedHandlers', 'connectionTimeoutMs') 'provider ready.protocol'
    $protocolExpected = [ordered]@{ requestHeaderBytes = 8; responseHeaderBytes = 4; maximumDelayMs = 1000; maximumPayloadBytes = 4096; responseFragmentBytes = 2; maximumLiveAcceptedHandlers = 16; connectionTimeoutMs = 5000 }
    $protocolActual = [ordered]@{}
    foreach ($field in $protocolExpected.Keys) {
        $value = Require-JsonInteger (Get-ExactField $protocol $field) "provider ready.protocol.$field" -NonNegative
        $protocolActual[$field] = $value
        Add-Check "provider protocol $field matches the framed-I/O fixture" ($value -eq $protocolExpected[$field]) ([ordered]@{ expected = $protocolExpected[$field]; actual = $value })
    }
    Add-Check 'provider binds only to loopback and reports an ephemeral port' (($address -ceq '127.0.0.1' -or $address -ceq '::1') -and $port -gt 0 -and $port -le 65535) ([ordered]@{ address = $address; port = $port })
    if ($address -cne '127.0.0.1') { throw "Native host is configured for IPv4 loopback; provider reported unsupported address $address." }
    $script:providerReady = [ordered]@{ address = $address; port = $port; protocol = $protocolActual; raw = $ready; path = $readyPath }
    $script:providerLifecycle.ready = $true
    return $script:providerReady
}

function Stop-Provider([string]$Reason) {
    if ($null -eq $script:providerHandle -or $script:providerHandle.Finalized) { return }
    $stopPath = [string]$script:providerLifecycle.stopFile
    $wasExited = $script:providerHandle.Process.HasExited
    try {
        [IO.File]::WriteAllText($stopPath, 'stop', $script:utf8)
        $script:providerLifecycle.stopFileWritten = $true
        $script:providerLifecycle.stopFileWrittenUtc = [DateTime]::UtcNow.ToString('O')
    } catch {
        $script:providerLifecycle.stopWriteError = $_.Exception.Message
    }
    $record = Finish-CapturedLongProcess $script:providerHandle 8000 $Reason
    $shutdownClean = $record.exitCode -eq 0 -and -not $record.timedOut -and
        $script:providerLifecycle.stopFileWritten -and $null -eq $script:providerLifecycle.stopWriteError -and
        -not $wasExited -and -not $record.shutdown.processStillRunning -and
        -not $record.shutdown.stdoutDrainTimedOut -and -not $record.shutdown.stderrDrainTimedOut -and
        $null -eq $record.shutdown.error
    $script:providerLifecycle.gracefulExit = $shutdownClean
    $script:providerLifecycle.forcedTermination = [bool]$record.timedOut
    $script:providerLifecycle.processWasAlreadyExitedBeforeStop = $wasExited
    Add-Check 'provider stops through its stop-file before the bounded grace expires and drains both logs' $shutdownClean ([ordered]@{ exitCode = $record.exitCode; timedOut = $record.timedOut; stopFileWritten = $script:providerLifecycle.stopFileWritten; stopWriteError = $script:providerLifecycle.stopWriteError; alreadyExitedBeforeStop = $wasExited; shutdown = $record.shutdown; reason = $Reason })
    if ($record.exitCode -eq 0 -and -not $record.timedOut) {
        try {
            $lines = @($record.stdout -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
            if ($lines.Count -gt 0) {
                $script:providerCounters = ConvertFrom-JsonText $lines[$lines.Count - 1] 'provider shutdown counters'
            } else {
                Add-Check 'provider emits its final shutdown counter object' $false ([ordered]@{ stdout = $record.stdout; stdoutPath = $record.stdoutPath })
            }
        } catch {
            $script:providerLifecycle.counterParseError = $_.Exception.Message
            Add-Check 'provider shutdown counters parse as JSON' $false ([ordered]@{ error = $_.Exception.Message; stdoutPath = $record.stdoutPath; stdout = $record.stdout })
        }
    }
}

function Validate-ProviderCounters($Counters, $Runs) {
    Require-Fields $Counters @('accepted', 'completed', 'rejectedBusy', 'invalidFrames', 'timedOut', 'cancelled', 'ioFailures', 'internalErrors') 'provider final counters'
    $values = [ordered]@{}
    foreach ($field in @('accepted', 'completed', 'rejectedBusy', 'invalidFrames', 'timedOut', 'cancelled', 'ioFailures', 'internalErrors')) {
        $values[$field] = Require-JsonInteger (Get-ExactField $Counters $field) "provider counters.$field" -NonNegative
    }
    $totalRequests = 0
    $resumeAttempts = 0
    foreach ($run in $Runs) {
        foreach ($case in $run.cases) {
            $oracleCase = Get-OracleCaseById $script:oracle $case.caseId
            $totalRequests += Get-ExpectedIoRequests $oracleCase
            $steps = Require-JsonArray (Get-ExactField $oracleCase 'steps') "oracle $($case.caseId).steps"
            $resumeAttempts += @($steps | Where-Object { (Get-ExactField $_ 'op') -ceq 'resume' }).Count
        }
    }
    $cancelWindow = @($script:oracle.cases | Where-Object { (Get-ExactField $_ 'id') -ceq 'cancel-after-begin-preserves-updated-state' }).Count * $Runs.Count
    Add-Check 'provider accepts enough loopback requests for every normal mailbox response' ($values.accepted -ge $resumeAttempts -and $values.accepted -le $totalRequests) ([ordered]@{ minimumNormalResponses = $resumeAttempts; maximumPlannedFrames = $totalRequests; accepted = $values.accepted })
    Add-Check 'provider completes the exact minimum set of normal responses including FAIL completions' ($values.completed -ge $resumeAttempts -and $values.completed -le $totalRequests) ([ordered]@{ minimumNormalResponses = $resumeAttempts; maximumPlannedFrames = $totalRequests; completed = $values.completed })
    Add-Check 'provider completion count never exceeds accepted connections' ($values.completed -le $values.accepted) ([ordered]@{ accepted = $values.accepted; completed = $values.completed })
    Add-Check 'provider reports no rejected, invalid, timed-out, or internal-error requests' ($values.rejectedBusy -eq 0 -and $values.invalidFrames -eq 0 -and $values.timedOut -eq 0 -and $values.internalErrors -eq 0) ([ordered]@{ rejectedBusy = $values.rejectedBusy; invalidFrames = $values.invalidFrames; timedOut = $values.timedOut; internalErrors = $values.internalErrors })
    Add-Check 'provider incomplete work is confined to the intentionally delayed cancellation windows' ($values.cancelled -le $cancelWindow -and $values.ioFailures -le $cancelWindow -and ($values.cancelled + $values.ioFailures) -le $cancelWindow) ([ordered]@{ maximumCancellationWindows = $cancelWindow; cancelled = $values.cancelled; ioFailures = $values.ioFailures })
    return [ordered]@{ counts = $values; expectedNormalResponses = $resumeAttempts; expectedFrames = $totalRequests; cancellationWindows = $cancelWindow; additionalFieldsAllowed = $true }
}

try {
    [IO.Directory]::CreateDirectory($runDirectory) | Out-Null
    [IO.Directory]::CreateDirectory($tempDirectory) | Out-Null
    $oracle = Read-JsonFile $fixturePath
    Require-Fields $oracle @('schemaVersion', 'status', 'description', 'contract', 'cases') 'real-I/O correctness oracle'
    $oracleSchema = Require-JsonInteger (Get-ExactField $oracle 'schemaVersion') 'oracle.schemaVersion' -NonNegative
    $oracleStatus = Require-JsonString (Get-ExactField $oracle 'status') 'oracle.status'
    $oracleCases = Require-JsonArray (Get-ExactField $oracle 'cases') 'oracle.cases'
    Add-Check 'independent seven-case oracle is frozen before native execution' ($oracleSchema -eq 1 -and $oracleStatus -ceq 'expected-values-frozen-before-first-native-execution' -and $oracleCases.Count -eq 7) ([ordered]@{ schemaVersion = $oracleSchema; status = $oracleStatus; caseCount = $oracleCases.Count })
    if ($oracleSchema -ne 1 -or $oracleStatus -cne 'expected-values-frozen-before-first-native-execution' -or $oracleCases.Count -ne 7) { throw 'Expected the frozen seven-case real-I/O oracle, status, and schema.' }
    $contract = Get-ExactField $oracle 'contract'
    Require-Fields $contract @('stateFieldOrder', 'continuationFieldOrder', 'counterType', 'boundedness', 'resumeFailure', 'cancellation', 'notImplementedAtInitialAuthoring') 'oracle contract'
    $sourceInputBefore = [Collections.Generic.List[object]]::new()
    foreach ($path in $sourceInputPaths) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required real-I/O acceptance input is missing: $path" }
        $sourceInputBefore.Add([ordered]@{ path = $path; sha256 = Get-Hash $path })
    }
    $oracleHashBeforeNative = [string]($sourceInputBefore | Where-Object { $_.path -ceq [IO.Path]::GetFullPath($fixturePath) } | Select-Object -First 1).sha256
    Add-Check 'source hashes include the oracle before any build or native execution' (-not [string]::IsNullOrWhiteSpace($oracleHashBeforeNative)) ([ordered]@{ oracle = $fixturePath; sha256 = $oracleHashBeforeNative })

    if (Test-Path -LiteralPath $providerFreezePath -PathType Leaf) {
        $freeze = Read-JsonFile $providerFreezePath
        Require-Fields $freeze @('schemaVersion', 'createdUtc', 'revision', 'caseCount', 'files') 'oracle freeze sidecar'
        $freezeFiles = Require-JsonArray (Get-ExactField $freeze 'files') 'oracle freeze files'
        $frozenEntries = @{}
        foreach ($entry in $freezeFiles) {
            Require-Fields $entry @('path', 'sha256') 'oracle freeze file entry'
            $relative = Require-JsonString (Get-ExactField $entry 'path') 'oracle freeze relative path'
            $hash = Require-JsonString (Get-ExactField $entry 'sha256') 'oracle freeze sha256'
            $frozenEntries[$relative] = $hash
        }
        $requiredFreezePaths = @('experiments/AgentLang.RealIoMailbox/mailbox.flow', 'experiments/AgentLang.RealIoMailbox/correctness-cases.json')
        $freezeOkay = (Require-JsonInteger (Get-ExactField $freeze 'schemaVersion') 'oracle freeze schemaVersion' -NonNegative) -eq 1 -and
            (Require-JsonInteger (Get-ExactField $freeze 'caseCount') 'oracle freeze caseCount' -NonNegative) -eq $oracleCases.Count
        foreach ($relative in $requiredFreezePaths) {
            $fullPath = Join-Path $repo $relative
            if (-not $frozenEntries.ContainsKey($relative) -or -not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { $freezeOkay = $false; continue }
            if ((Get-Hash $fullPath) -cne [string]$frozenEntries[$relative]) { $freezeOkay = $false }
        }
        $oracleFreezeCopyPath = Join-Path $runDirectory 'oracle-freeze.json'
        [IO.File]::Copy($providerFreezePath, $oracleFreezeCopyPath, $true)
        $oracleFreezeCopySha256 = Get-Hash $oracleFreezeCopyPath
        Add-Check 'available pre-execution freeze sidecar records current Flow and oracle hashes' $freezeOkay ([ordered]@{ path = $providerFreezePath; copiedTo = $oracleFreezeCopyPath; sha256 = $oracleFreezeCopySha256; requiredPaths = $requiredFreezePaths; frozenHashes = $frozenEntries })
        if (-not $freezeOkay) { throw 'The oracle freeze sidecar does not match the current frozen fixture inputs.' }
    } else {
        Add-Check 'scratch oracle freeze sidecar is optional for a clean checkout' $true ([ordered]@{ path = $providerFreezePath; available = $false; beforeNativeOracleHash = $oracleHashBeforeNative })
    }

    $clangCandidates = @(
        'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\Llvm\x64\bin\clang.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\BuildTools\VC\Tools\Llvm\x64\bin\clang.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Tools\Llvm\x64\bin\clang.exe',
        'C:\Program Files (x86)\Microsoft Visual Studio\2022\Community\VC\Tools\Llvm\x64\bin\clang.exe'
    )
    $clang = Resolve-Executable 'AGENTLANG_LLVM_CLANG' $clangCandidates 'clang.exe'
    $dotnetCommand = Get-Command -Name dotnet.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $dotnet = [IO.Path]::GetFullPath($dotnetCommand.Source)
    $report.compiler = [ordered]@{ dotnet = $dotnet; dotnetSha256 = Get-Hash $dotnet; clang = $clang; clangSha256 = Get-Hash $clang; tempDirectory = $tempDirectory }

    $bootstrapArtifacts = Join-Path $runDirectory 'bootstrap-artifacts'
    $bootstrapBuildArguments = @('build', $bootstrapProjectPath, '--artifacts-path', $bootstrapArtifacts, '--configuration', 'Release', '--verbosity', 'minimal', '-p:NuGetAudit=false', '-m:1')
    $bootstrapBuild = Invoke-CapturedProcess 'fresh-release-owning-mailbox-bootstrap-build' $dotnet $bootstrapBuildArguments $repo
    Require-ProcessSuccess $bootstrapBuild 'fresh isolated Release owning-mailbox bootstrap build succeeds'
    $bootstrapAssembly = Get-UniqueAssembly $bootstrapArtifacts 'AgentLang.OwningMailbox.dll' 'owning-mailbox bootstrap'

    $providerArtifacts = Join-Path $runDirectory 'provider-artifacts'
    $providerBuildArguments = @('build', $providerProjectPath, '--artifacts-path', $providerArtifacts, '--configuration', 'Release', '--verbosity', 'minimal', '-p:NuGetAudit=false', '-m:1')
    $providerBuild = Invoke-CapturedProcess 'fresh-release-real-io-provider-build' $dotnet $providerBuildArguments $repo
    Require-ProcessSuccess $providerBuild 'fresh isolated Release real-I/O provider build succeeds'
    $providerAssembly = Get-UniqueAssembly $providerArtifacts 'AgentLang.RealIoMailbox.Provider.dll' 'real-I/O provider'

    foreach ($optimization in @('O0', 'O2')) {
        $moduleDirectory = Join-Path $runDirectory "module-$optimization"
        [IO.Directory]::CreateDirectory($moduleDirectory) | Out-Null
        $moduleProcess = Invoke-CapturedProcess "compile-real-io-module-$optimization" $dotnet @($bootstrapAssembly, $optimization, $moduleDirectory, $flowPath) $repo
        Require-ProcessSuccess $moduleProcess "fresh $optimization mailbox.flow compile succeeds"
        $bootstrap = ConvertFrom-JsonText $moduleProcess.stdout.Trim() "$optimization real-I/O module bootstrap"
        Require-Fields $bootstrap @('optimization', 'sourcePath', 'sameVerifiedProgramInstance', 'modulePath', 'manifestPath', 'diagnostics', 'interpreterOracle') "$optimization module bootstrap"
        $bootstrapOptimization = Require-JsonString (Get-ExactField $bootstrap 'optimization') "$optimization bootstrap optimization"
        $bootstrapSourcePath = Require-JsonString (Get-ExactField $bootstrap 'sourcePath') "$optimization bootstrap sourcePath"
        $sameProgramInstance = Require-JsonBoolean (Get-ExactField $bootstrap 'sameVerifiedProgramInstance') "$optimization bootstrap sameVerifiedProgramInstance"
        Add-Check "$optimization compiler bootstrap uses the requested optimization and Flow source" (
            $bootstrapOptimization -ceq $optimization -and
            [IO.Path]::GetFullPath($bootstrapSourcePath) -ceq [IO.Path]::GetFullPath($flowPath) -and
            $sameProgramInstance)
        $modulePath = Require-JsonString (Get-ExactField $bootstrap 'modulePath') "$optimization module path"
        $manifestPath = Require-JsonString (Get-ExactField $bootstrap 'manifestPath') "$optimization manifest path"
        if (-not (Test-Path -LiteralPath $modulePath -PathType Leaf) -or -not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "$optimization generated mailbox module or manifest is missing." }
        $diagnostics = Require-JsonArray (Get-ExactField $bootstrap 'diagnostics') "$optimization compiler diagnostics"
        $divideDiagnostics = @($diagnostics | Where-Object { (Get-ExactField $_ 'code') -ceq 'RUNTIME_DIVIDE_BY_ZERO' })
        Add-Check "$optimization compiler emits the checked-divide diagnostic needed by the failure case" ($divideDiagnostics.Count -eq 1) ([ordered]@{ diagnosticCount = $divideDiagnostics.Count; diagnostics = $divideDiagnostics })
        if ($divideDiagnostics.Count -ne 1) { throw "$optimization module must contain exactly one RUNTIME_DIVIDE_BY_ZERO diagnostic." }
        $divideDiagnosticId = Require-JsonInteger (Get-ExactField $divideDiagnostics[0] 'id') "$optimization divide diagnostic id" -NonNegative
        $interpreterEvidence = Assert-InterpreterUnicode $bootstrap $oracle $optimization
        $manifest = Read-JsonFile $manifestPath
        $moduleBuilds.Add([ordered]@{
            optimization = $optimization
            modulePath = [IO.Path]::GetFullPath($modulePath)
            manifestPath = [IO.Path]::GetFullPath($manifestPath)
            moduleSha256 = Get-Hash $modulePath
            manifestSha256 = Get-Hash $manifestPath
            divideByZeroDiagnosticId = $divideDiagnosticId
            bootstrap = $bootstrap
            manifest = $manifest
            interpreterEvidence = $interpreterEvidence
        })
    }

    $maxCase = Get-OracleCaseById $oracle 'maximum-sized-request-and-message'
    if ($null -eq $maxCase) { throw 'Frozen correctness oracle is missing maximum-sized-request-and-message.' }
    $maxSteps = Require-JsonArray (Get-ExactField $maxCase 'steps') 'maximum-sized-request-and-message.steps'
    $maxBegin = $maxSteps | Where-Object { (Get-ExactField $_ 'op') -ceq 'begin' } | Select-Object -First 1
    $maxResume = $maxSteps | Where-Object { (Get-ExactField $_ 'op') -ceq 'resume' } | Select-Object -First 1
    $maxRequest = Require-JsonString (Get-ExactField $maxBegin 'request') 'maximum fixture request'
    $maxMessage = Require-JsonString (Get-ExactField $maxResume 'message') 'maximum fixture completion message'
    $maxRequestBytes = [Text.Encoding]::UTF8.GetByteCount($maxRequest)
    $maxMessageBytes = [Text.Encoding]::UTF8.GetByteCount($maxMessage)
    $maxExpectedState = Get-ExactField (Get-ExactField $maxResume 'expected') 'state'
    $maxExpectedLatest = Require-JsonString (Get-ExactField $maxExpectedState 'latest') 'maximum fixture expected latest'
    Add-Check 'frozen maximum-size case exercises exact 4096-byte request and completion payloads' ($maxRequestBytes -eq 4096 -and $maxMessageBytes -eq 4096 -and $maxRequest -ceq ('R' * 4096) -and $maxMessage -ceq ('M' * 4096) -and $maxExpectedLatest -ceq ($maxRequest + $maxMessage)) ([ordered]@{ requestBytes = $maxRequestBytes; messageBytes = $maxMessageBytes; expectedLatestLength = $maxExpectedLatest.Length })
    foreach ($oracleCase in $oracleCases) {
        foreach ($step in (Require-JsonArray (Get-ExactField $oracleCase 'steps') "oracle $((Get-ExactField $oracleCase 'id')).steps")) {
            foreach ($field in @('request', 'message')) {
                if (Has-ExactKey $step $field) {
                    $text = Require-JsonString (Get-ExactField $step $field) "oracle $((Get-ExactField $oracleCase 'id')).$field"
                    $byteCount = [Text.Encoding]::UTF8.GetByteCount($text)
                    Add-Check "oracle $((Get-ExactField $oracleCase 'id')) $field fits the provider payload bound" ($byteCount -le 4096) ([ordered]@{ utf8Bytes = $byteCount; maximum = 4096 })
                }
            }
        }
    }

    $sourceHashBeforeNativeCheck = Get-Hash $fixturePath
    Add-Check 'frozen oracle hash is unchanged immediately before native execution' ($sourceHashBeforeNativeCheck -ceq $oracleHashBeforeNative) ([ordered]@{ beforeBuilds = $oracleHashBeforeNative; beforeNative = $sourceHashBeforeNativeCheck })
    if ($sourceHashBeforeNativeCheck -cne $oracleHashBeforeNative) { throw 'The correctness oracle changed after the initial source snapshot; native execution was skipped.' }

    $provider = Start-Provider $providerAssembly
    $semanticRuns = [Collections.Generic.List[object]]::new()
    foreach ($optimization in @('O0', 'O2')) {
        $module = $moduleBuilds | Where-Object { $_.optimization -ceq $optimization } | Select-Object -First 1
        $runnerDirectory = Join-Path $runDirectory "native-$optimization"
        [IO.Directory]::CreateDirectory($runnerDirectory) | Out-Null
        $runnerPath = Join-Path $runnerDirectory "native-realio-mailbox-$optimization.exe"
        $compileArguments = @('--target=x86_64-pc-windows-msvc', '-std=c11', '-Wall', '-Wextra', '-Werror', "-$optimization", '-I', $nativeDirectory) + $nativeSources + @('-lws2_32', '-o', $runnerPath)
        $nativeBuild = Invoke-CapturedProcess "native-real-io-build-$optimization" $clang $compileArguments $runnerDirectory
        Require-ProcessSuccess $nativeBuild "$optimization real-I/O native host compiles with strict warnings and Winsock"
        $nativeBuilds.Add([ordered]@{ optimization = $optimization; executable = $runnerPath; sha256 = Get-Hash $runnerPath; process = $nativeBuild })
        foreach ($policy in @('return', 'keep')) {
            $beforeRunHash = Get-Hash $fixturePath
            Add-Check "$optimization/$policy execution begins with the pre-native frozen oracle hash" ($beforeRunHash -ceq $oracleHashBeforeNative) ([ordered]@{ expected = $oracleHashBeforeNative; actual = $beforeRunHash })
            if ($beforeRunHash -cne $oracleHashBeforeNative) { throw 'The frozen correctness oracle changed before a native run.' }
            $nativeRun = Invoke-CapturedProcess "native-real-io-run-$optimization-$policy" $runnerPath @('--module', [string]$module.modulePath, '--policy', $policy, '--port', [string]$provider.port) $runnerDirectory 60000
            Require-ProcessSuccess $nativeRun "$optimization/$policy real-I/O native correctness run succeeds"
            $native = ConvertFrom-JsonText $nativeRun.stdout.Trim() "$optimization/$policy native result"
            Require-Fields $native @('schemaVersion', 'policy', 'cases') "$optimization/$policy native result"
            $nativeSchema = Require-JsonInteger (Get-ExactField $native 'schemaVersion') "$optimization/$policy native schemaVersion" -NonNegative
            $nativePolicy = Require-JsonString (Get-ExactField $native 'policy') "$optimization/$policy native policy"
            Add-Check "$optimization native result names schema v1 and selected policy" ($nativeSchema -eq 1 -and $nativePolicy -ceq $policy) ([ordered]@{ schemaVersion = $nativeSchema; policy = $nativePolicy; expectedPolicy = $policy })
            $nativeCases = Require-JsonArray (Get-ExactField $native 'cases') "$optimization/$policy native cases"
            Add-Check "$optimization/$policy native output preserves all frozen oracle cases in order" ($nativeCases.Count -eq $oracleCases.Count) ([ordered]@{ expectedCount = $oracleCases.Count; actualCount = $nativeCases.Count })
            if ($nativeCases.Count -ne $oracleCases.Count) { throw "$optimization/$policy native case count differs from fixture." }
            $normalizedCases = [Collections.Generic.List[object]]::new()
            for ($caseIndex = 0; $caseIndex -lt $oracleCases.Count; $caseIndex++) {
                $diagnosticId = [int64]$module.divideByZeroDiagnosticId
                $validated = Validate-CaseSteps $nativeCases[$caseIndex] $oracleCases[$caseIndex] $policy $optimization $diagnosticId
                $normalizedCases.Add($validated)
            }
            $semanticRuns.Add([ordered]@{ optimization = $optimization; policy = $policy; cases = @($normalizedCases); rawResult = $native; process = $nativeRun })
            $nativeRuns.Add([ordered]@{ optimization = $optimization; policy = $policy; process = $nativeRun; result = $native; validatedCases = @($normalizedCases) })
        }
    }
    Stop-Provider 'all four real-I/O policy/optimization runs completed'
    $providerEvidence = Validate-ProviderCounters $providerCounters @($semanticRuns)

    foreach ($optimization in @('O0', 'O2')) {
        $returnRun = $semanticRuns | Where-Object { $_.optimization -ceq $optimization -and $_.policy -ceq 'return' } | Select-Object -First 1
        $keepRun = $semanticRuns | Where-Object { $_.optimization -ceq $optimization -and $_.policy -ceq 'keep' } | Select-Object -First 1
        Compare-RunSemantics $returnRun $keepRun "$optimization RETURN and KEEP have identical exact semantic state traces"
        Compare-RunStorage $returnRun $keepRun "$optimization RETURN and KEEP use equal caller-reserved controller storage"
    }
    foreach ($policy in @('return', 'keep')) {
        $o0Run = $semanticRuns | Where-Object { $_.optimization -ceq 'O0' -and $_.policy -ceq $policy } | Select-Object -First 1
        $o2Run = $semanticRuns | Where-Object { $_.optimization -ceq 'O2' -and $_.policy -ceq $policy } | Select-Object -First 1
        Compare-RunSemantics $o0Run $o2Run "$policy O0 and O2 have identical exact semantic state traces"
        Compare-RunStorage $o0Run $o2Run "$policy O0 and O2 use equal caller-reserved controller storage"
    }
    $report.provider = [ordered]@{ ready = $providerReady; lifecycle = $providerLifecycle; evidence = $providerEvidence; counters = $providerCounters; counterOutput = if ($null -eq $providerHandle) { $null } else { $providerHandle.Record.stdout } }
    $runChecksPassed = $true
} catch {
    $errors.Add($_.Exception.ToString())
    Add-Check 'real-I/O verification completed without an uncaught error' $false $_.Exception.Message
} finally {
    if ($null -ne $providerHandle -and -not $providerHandle.Finalized) {
        try { Stop-Provider 'cleanup after success or failure' }
        catch {
            $errors.Add("Provider cleanup failed: $($_.Exception.ToString())")
            Add-Check 'provider cleanup completed without an exception' $false $_.Exception.Message
        }
        if ($null -ne $providerCounters -and $null -eq $report.provider) { $report.provider = [ordered]@{ ready = $providerReady; lifecycle = $providerLifecycle; counters = $providerCounters; cleanupOnly = $true } }
    }
    foreach ($path in $sourceInputPaths) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $sourceInputAfter += [ordered]@{ path = [IO.Path]::GetFullPath($path); sha256 = Get-Hash $path }
        } else {
            $sourceInputAfter += [ordered]@{ path = [IO.Path]::GetFullPath($path); sha256 = $null; missing = $true }
        }
    }
    $sourceStable = $sourceInputBefore.Count -eq $sourceInputAfter.Count -and $sourceInputBefore.Count -eq $sourceInputPaths.Count
    if ($sourceStable) {
        for ($index = 0; $index -lt $sourceInputBefore.Count; $index++) {
            if ($sourceInputBefore[$index].path -cne $sourceInputAfter[$index].path -or $sourceInputBefore[$index].sha256 -cne $sourceInputAfter[$index].sha256) { $sourceStable = $false }
        }
    }
    Add-Check 'all real-I/O acceptance source hashes remain unchanged during fresh builds and native runs' $sourceStable ([ordered]@{ beforeCount = $sourceInputBefore.Count; afterCount = $sourceInputAfter.Count; expectedCount = $sourceInputPaths.Count })
    if (-not [string]::IsNullOrWhiteSpace($oracleHashBeforeNative)) {
        $oracleHashAfter = if (Test-Path -LiteralPath $fixturePath -PathType Leaf) { Get-Hash $fixturePath } else { $null }
        Add-Check 'frozen expected-oracle hash remains unchanged through evidence capture' ($oracleHashAfter -ceq $oracleHashBeforeNative) ([ordered]@{ beforeNative = $oracleHashBeforeNative; afterRun = $oracleHashAfter })
    }
    $report.completedUtc = [DateTime]::UtcNow.ToString('O')
    $report.oracleFreeze = [ordered]@{ status = if ($null -eq $oracle) { $null } else { Get-ExactField $oracle 'status' }; sha256BeforeNative = $oracleHashBeforeNative; sidecarPath = $providerFreezePath; copiedTo = $oracleFreezeCopyPath; copiedSha256 = $oracleFreezeCopySha256 }
    $report.sourceHashesBefore = @($sourceInputBefore)
    $report.sourceHashesAfter = @($sourceInputAfter)
    $report.processes = @($processes)
    $report.moduleBuilds = @($moduleBuilds)
    $report.nativeBuilds = @($nativeBuilds)
    $report.nativeRuns = @($nativeRuns)
    if ($null -eq $report.provider -and $null -ne $providerHandle) { $report.provider = [ordered]@{ ready = $providerReady; lifecycle = $providerLifecycle; counters = $providerCounters; outputPath = $providerHandle.Record.stdoutPath; errorPath = $providerHandle.Record.stderrPath } }
    $report.providerLifecycle = $providerLifecycle
    $report.errors = @($errors)
    $fourNativeRuns = $nativeRuns.Count -eq 4
    Add-Check 'all four O0/O2 by RETURN/KEEP native runs completed' $fourNativeRuns ([ordered]@{ expected = 4; actual = $nativeRuns.Count })
    $report.checks = @($checks)
    $report.passed = $runChecksPassed -and $fourNativeRuns -and $errors.Count -eq 0 -and @($checks | Where-Object { -not $_.passed }).Count -eq 0
    try {
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($reportPath)) | Out-Null
        [IO.File]::WriteAllText($reportPath, (ConvertTo-Json -InputObject $report -Depth 100), $utf8)
    } catch {
        Write-Error "Could not write the required evidence report at ${reportPath}: $($_.Exception.Message)"
        throw
    }
}

Write-Output "Real-I/O mailbox verifier report: $reportPath"
if (-not $report.passed) {
    foreach ($errorRecord in $errors) { Write-Error $errorRecord }
    exit 1
}
