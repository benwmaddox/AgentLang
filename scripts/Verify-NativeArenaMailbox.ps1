#requires -Version 7.0
[CmdletBinding()]
param([string]$EvidencePath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runId = [Guid]::NewGuid().ToString('N')
$runDir = Join-Path $repo ".agentlang/arena-probe/run-$runId"
if ([string]::IsNullOrWhiteSpace($EvidencePath)) {
    $reportPath = Join-Path $repo ".agentlang/arena-probe/evidence/$runId.json"
} elseif ([IO.Path]::IsPathFullyQualified($EvidencePath)) {
    $reportPath = [IO.Path]::GetFullPath($EvidencePath)
} else {
    $reportPath = [IO.Path]::GetFullPath((Join-Path $repo $EvidencePath))
}
$sourceRel = 'experiments/native-arena-mailbox/probe.c'
$sourcePath = Join-Path $repo $sourceRel
$optimizations = @('O0', 'O2')
$modes = @('turn', 'request')
$scenarios = @('cpu', 'delayed-small', 'delayed-large', 'slow-output', 'cancellation', 'retained-only')
$repeats = 3
$seed = [UInt64]17
$requests = 32
$mailboxCount = 8
$expectedMatrixRuns = 72
$budget = [UInt64](8 * 1024 * 1024)
$processLimit = [UInt64](64 * 1024 * 1024)
$timeoutSeconds = 60
$processes = [Collections.Generic.List[object]]::new()
$builds = [Collections.Generic.List[object]]::new()
$safety = [Collections.Generic.List[object]]::new()
$runs = [Collections.Generic.List[object]]::new()
$checks = [Collections.Generic.List[object]]::new()
$controls = [Collections.Generic.List[object]]::new()
$errors = [Collections.Generic.List[string]]::new()
$utf8 = [Text.UTF8Encoding]::new($false)

# Freeze the execution order and configuration before running any workload.
$matrix = [Collections.Generic.List[object]]::new()
$seq = 0
foreach ($opt in $optimizations) {
    for ($repeat = 1; $repeat -le $repeats; $repeat++) {
        $modeOrder = if (($repeat % 2) -eq 1) { @('turn', 'request') } else { @('request', 'turn') }
        foreach ($mode in $modeOrder) {
            foreach ($scenario in $scenarios) {
                $seq++
                $matrix.Add([ordered]@{
                    sequence = $seq; optimization = $opt; repeat = $repeat
                    mode = $mode; scenario = $scenario; seed = $seed; requests = $requests
                    budgetBytes = $budget; processLimitBytes = $processLimit
                })
            }
        }
    }
}
$report = [ordered]@{
    schemaVersion = 1
    kind = 'native-arena-mailbox-feasibility'
    runId = $runId
    startedUtc = [DateTime]::UtcNow.ToString('O')
    completedUtc = $null
    passed = $false
    evidencePath = $reportPath
    runDirectory = $runDir
    scope = 'native ownership, copy, and backing-footprint feasibility under deterministic logical delays; not saturated-server throughput or latency'
    configuration = [ordered]@{
        source = $sourceRel; optimizations = $optimizations; repeats = $repeats
        modes = $modes; modeOrder = 'alternate mode order by repeat; sequential executions'
        scenarios = $scenarios; seed = $seed; requests = $requests
        mailboxCount = $mailboxCount; arenaBudgetBytes = $budget; processLimitBytes = $processLimit
        expectedRuns = $expectedMatrixRuns; timeoutSeconds = $timeoutSeconds
        retainedOnlyNote = 'retained graph without disposable intermediate scratch; an unfavorable copy control'
    }
    preregisteredMatrix = @($matrix)
    compiler = $null
    source = $null
    executableHashes = @()
    processes = @()
    safety = @()
    workloadRuns = @()
    comparisons = $null
    negativeControls = @()
    checks = @()
    failure = $null
}

function Add-Check([string]$Name, [bool]$Passed, $Details = $null) {
    $item = [ordered]@{ name = $Name; passed = $Passed }
    if ($null -ne $Details) { $item.details = $Details }
    $checks.Add($item)
    if (-not $Passed -and $null -ne $Details) { $errors.Add(('{0}: {1}' -f $Name, $Details)) }
}
function Add-ValidationCheck([Collections.Generic.List[object]]$List, [string]$Name, [bool]$Passed) {
    $List.Add([ordered]@{ name = $Name; passed = $Passed })
}
function Get-Field($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    if ($Object -is [Collections.IDictionary]) { return $Object[$Name] }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -ne $property) { return $property.Value }
    return $null
}
function Has-Field($Object, [string]$Name) {
    if ($Object -is [Collections.IDictionary]) { return $Object.Contains($Name) }
    return $null -ne $Object -and $null -ne $Object.PSObject.Properties[$Name]
}
function Try-UInt($Value, [ref]$Number) {
    $Number.Value = [decimal]0
    if ($null -eq $Value -or $Value -is [bool] -or $Value -is [string]) { return $false }
    try { $n = [decimal]::Parse([Convert]::ToString($Value, [Globalization.CultureInfo]::InvariantCulture), [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture) }
    catch { return $false }
    if ($n -lt 0 -or $n -ne [decimal]::Truncate($n)) { return $false }
    $Number.Value = $n
    return $true
}
function Try-NonnegativeNumber($Value, [ref]$Number) {
    $Number.Value = [decimal]0
    if ($null -eq $Value -or $Value -is [bool] -or $Value -is [string]) { return $false }
    try { $n = [decimal]::Parse([Convert]::ToString($Value, [Globalization.CultureInfo]::InvariantCulture), [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture) }
    catch { return $false }
    if ($n -lt 0) { return $false }
    $Number.Value = $n
    return $true
}
function Get-Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Resolve-Clang {
    $override = [Environment]::GetEnvironmentVariable('AGENTLANG_LLVM_CLANG')
    if (-not [string]::IsNullOrWhiteSpace($override)) {
        if ([IO.Path]::IsPathFullyQualified($override)) { return [IO.Path]::GetFullPath($override) }
        if ($override.Contains('\') -or $override.Contains('/')) { return [IO.Path]::GetFullPath((Join-Path $repo $override)) }
        $found = Get-Command -Name $override -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -eq $found) { throw "AGENTLANG_LLVM_CLANG is not an executable: $override" }
        return [IO.Path]::GetFullPath($found.Source)
    }
    return 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\Llvm\x64\bin\clang.exe'
}
function Invoke-Direct {
    param([string]$File, [string[]]$CommandArguments, [string]$Directory, [int]$Timeout)
    $start = [DateTime]::UtcNow
    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $File
    $psi.WorkingDirectory = $Directory
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    foreach ($argument in $CommandArguments) { [void]$psi.ArgumentList.Add([string]$argument) }
    $p = [Diagnostics.Process]::new()
    $p.StartInfo = $psi
    $stdout = ''
    $stderr = ''
    $exit = $null
    $timedOut = $false
    $startError = $null
    $started = $false
    try {
        if (-not $p.Start()) { throw 'Process.Start returned false.' }
        $started = $true
        $outTask = $p.StandardOutput.ReadToEndAsync()
        $errTask = $p.StandardError.ReadToEndAsync()
        if (-not $p.WaitForExit($Timeout * 1000)) {
            $timedOut = $true
            try { $p.Kill($true) } catch { }
            [void]$p.WaitForExit(5000)
        }
        if ($p.HasExited) { $exit = $p.ExitCode }
        if ($outTask.Wait(5000)) { $stdout = $outTask.Result } else { $stdout = '[stdout capture timed out]' }
        if ($errTask.Wait(5000)) { $stderr = $errTask.Result } else { $stderr = '[stderr capture timed out]' }
    } catch {
        $startError = $_.Exception.Message
        if ($started -and -not $p.HasExited) { try { $p.Kill($true) } catch { } }
    } finally { $p.Dispose() }
    return [ordered]@{
        executable = $File; arguments = @($CommandArguments); workingDirectory = $Directory
        startedUtc = $start.ToString('O'); elapsedMilliseconds = [Math]::Round(([DateTime]::UtcNow - $start).TotalMilliseconds, 3)
        exitCode = $exit; timedOut = $timedOut; startError = $startError
        stdout = $stdout; stderr = $stderr
    }
}
function Record-Process([string]$Stage, $Process, $Context = $null) {
    $entry = [ordered]@{ stage = $Stage; process = $Process }
    if ($null -ne $Context) { foreach ($key in $Context.Keys) { $entry[$key] = $Context[$key] } }
    $processes.Add($entry)
}
function Value-Oracle([UInt64]$ExpectedSeed, [int]$Id) {
    $c = [Convert]::ToUInt64('41C64E6D', 16)
    return [UInt64]((([decimal]$ExpectedSeed * 1000003) + ([decimal]($Id + 1) * 97) + $c) % 4294967296)
}

function Test-Scenario($Json, [string]$Mode, [string]$Scenario, [UInt64]$ExpectedSeed, [int]$Count, [UInt64]$ArenaBudget, [UInt64]$OsLimit) {
    $v = [Collections.Generic.List[object]]::new()
    $numeric = @(
        'schemaVersion','seed','requestsOffered','mailboxCount','processLimitBytes',
        'completed','rejected','cancelled','failed','continuationCopyBytes','fixedMetadataBytes',
        'workingScratchBytes','cacheCapBytes',
        'arenaUsedBytes','arenaReservedBytes','arenaPeakUsedBytes','arenaCachedBytes','arenaPeakReservedBytes','arenaPeakCachedBytes',
        'allocatorCalls','freeCalls','providerLiveBytesBeforeCancel','providerLiveBytesAfterCancel','providerLiveBytesAtDrain',
        'outputLiveBytesBeforeCancel','outputLiveBytesAfterCancel','outputLiveBytesAtDrain','privateBytes','workingSetBytes',
        'processPeakCommittedBytes','elapsedQpcTicks','qpcFrequency','activeOwnerCountAtDrain'
    )
    $required = @($numeric) + @('mode','scenario','processLimitEnforced','results','elapsedMilliseconds','drained','staticStatePreserved')
    $missing = @($required | Where-Object { -not (Has-Field $Json $_) })
    Add-ValidationCheck $v 'all required fields are present' ($missing.Count -eq 0)
    $n = @{}
    foreach ($field in $numeric) {
        $x = [decimal]0
        $ok = Try-UInt (Get-Field $Json $field) ([ref]$x)
        Add-ValidationCheck $v "$field is an unsigned integer" $ok
        if ($ok) { $n[$field] = $x }
    }
    $ms = [decimal]0
    $msOk = Try-NonnegativeNumber (Get-Field $Json 'elapsedMilliseconds') ([ref]$ms)
    Add-ValidationCheck $v 'elapsedMilliseconds is a finite nonnegative number' $msOk
    if ($n.ContainsKey('schemaVersion')) { Add-ValidationCheck $v 'schemaVersion is 1' ($n.schemaVersion -eq 1) }
    Add-ValidationCheck $v 'mode matches command' ((Get-Field $Json 'mode') -ceq $Mode)
    Add-ValidationCheck $v 'scenario matches command' ((Get-Field $Json 'scenario') -ceq $Scenario)
    if ($n.ContainsKey('seed')) { Add-ValidationCheck $v 'seed matches command' ($n.seed -eq [decimal]$ExpectedSeed) }
    if ($n.ContainsKey('requestsOffered')) { Add-ValidationCheck $v 'request count matches command' ($n.requestsOffered -eq $Count) }
    if ($n.ContainsKey('mailboxCount') -and $n.ContainsKey('requestsOffered')) {
        Add-ValidationCheck $v 'mailboxCount matches the fixed matrix' ($n.mailboxCount -eq $mailboxCount)
    }
    if ($n.ContainsKey('processLimitBytes')) { Add-ValidationCheck $v 'process limit matches command' ($n.processLimitBytes -eq [decimal]$OsLimit) }
    if ($n.ContainsKey('workingScratchBytes')) {
        $expectedScratch = if ($Scenario -ceq 'retained-only') { [decimal]0 } else { [decimal](512 * 1024) }
        Add-ValidationCheck $v 'working scratch bytes match the frozen workload contract' ($n.workingScratchBytes -eq $expectedScratch)
    }
    if ($n.ContainsKey('cacheCapBytes')) { Add-ValidationCheck $v 'cache cap is one MiB' ($n.cacheCapBytes -eq [decimal](1024 * 1024)) }
    if ($n.ContainsKey('elapsedQpcTicks') -and $n.ContainsKey('qpcFrequency') -and $n.qpcFrequency -gt 0 -and $msOk) {
        $qpcMilliseconds = ($n.elapsedQpcTicks * [decimal]1000) / $n.qpcFrequency
        Add-ValidationCheck $v 'elapsedMilliseconds agrees with QPC within 0.001ms' ([decimal]::Abs($ms - $qpcMilliseconds) -le [decimal]0.001)
    } else {
        Add-ValidationCheck $v 'elapsedMilliseconds agrees with QPC within 0.001ms' $false
    }
    $limitOk = Get-Field $Json 'processLimitEnforced'
    Add-ValidationCheck $v 'process limit is enforced' ($limitOk -is [bool] -and $limitOk)

    $rows = if ($Json -is [Collections.IDictionary] -and $Json.Contains('results')) { @($Json['results']) } else { @() }
    $rowsAreArray = $Json -is [Collections.IDictionary] -and $Json.Contains('results') -and $Json['results'] -is [array]
    Add-ValidationCheck $v 'results is a JSON array' $rowsAreArray
    Add-ValidationCheck $v 'one result exists for every offered request' ($rows.Count -eq $Count)
    $known = @('completed','cancelled','rejected_capacity','rejected_queue','rejected_output_capacity','rejected_pending','failed')
    $ids = [Collections.Generic.HashSet[int]]::new()
    $seen = @{ completed = 0; rejected = 0; cancelled = 0; failed = 0 }
    $rowsOk = $true
    foreach ($row in $rows) {
        $id = [decimal]0
        $mailbox = [decimal]0
        $idOk = (Try-UInt (Get-Field $row 'id') ([ref]$id)) -and $id -lt $Count
        $mailboxOk = (Try-UInt (Get-Field $row 'mailboxId') ([ref]$mailbox)) -and $n.ContainsKey('mailboxCount') -and $mailbox -lt $n.mailboxCount -and $idOk -and $mailbox -eq ($id % $mailboxCount)
        $valueFieldPresent = Has-Field $row 'value'
        if (-not $idOk -or -not $ids.Add([int]$id) -or -not $mailboxOk -or -not $valueFieldPresent) { $rowsOk = $false }
        $status = Get-Field $row 'status'
        if ($status -isnot [string] -or $status -cnotin $known) { $rowsOk = $false; continue }
        $expectedStatus = if ($Scenario -ceq 'cancellation' -and (([int]$id % 3) -eq 0)) { 'cancelled' } else { 'completed' }
        if (-not $idOk -or $status -cne $expectedStatus) { $rowsOk = $false }
        if ($status -ceq 'completed') {
            $seen.completed++
            $value = [decimal]0
            $valueOk = Try-UInt (Get-Field $row 'value') ([ref]$value)
            if (-not $idOk -or -not $valueOk -or $value -ne [decimal](Value-Oracle $ExpectedSeed ([int]$id))) { $rowsOk = $false }
        } else {
            if ($status -ceq 'cancelled') { $seen.cancelled++ }
            elseif ($status -ceq 'failed') { $seen.failed++ }
            else { $seen.rejected++ }
            if ($null -ne (Get-Field $row 'value')) { $rowsOk = $false }
        }
    }
    Add-ValidationCheck $v 'result IDs, mailbox IDs, statuses, and values are valid' ($rowsOk -and $ids.Count -eq $Count)
    foreach ($counter in @('completed','rejected','cancelled','failed')) {
        if ($n.ContainsKey($counter)) { Add-ValidationCheck $v "$counter counter matches results" ($n[$counter] -eq $seen[$counter]) }
    }
    $counterNames = @('completed','rejected','cancelled','failed')
    $allCounts = @($counterNames | Where-Object { $n.ContainsKey($_) }).Count -eq $counterNames.Count
    if ($allCounts) {
        Add-ValidationCheck $v 'status counters conserve offered requests' (($n.completed + $n.rejected + $n.cancelled + $n.failed) -eq $Count)
        Add-ValidationCheck $v 'failed results are not accepted' ($n.failed -eq 0)
        if ($Scenario -ceq 'cancellation') {
            $expectedCancelled = @(@(0..($Count - 1)) | Where-Object { ($_ % 3) -eq 0 }).Count
            Add-ValidationCheck $v 'cancellation has exactly the declared cancelled and completed counts' ($n.cancelled -eq $expectedCancelled -and $n.completed -eq ($Count - $expectedCancelled) -and $n.rejected -eq 0)
        } else {
            Add-ValidationCheck $v 'non-cancellation workload completes every request' ($n.completed -eq $Count -and $n.rejected -eq 0 -and $n.cancelled -eq 0)
        }
    }
    $arenaFields = @('fixedMetadataBytes','arenaUsedBytes','arenaReservedBytes','arenaPeakUsedBytes','arenaCachedBytes','arenaPeakReservedBytes','arenaPeakCachedBytes')
    if (@($arenaFields | Where-Object { $n.ContainsKey($_) }).Count -eq $arenaFields.Count) {
        Add-ValidationCheck $v 'reserved arena peak plus metadata fits budget' (($n.arenaPeakReservedBytes + $n.fixedMetadataBytes) -le [decimal]$ArenaBudget)
        Add-ValidationCheck $v 'used and cached arena bytes fit reservations' ($n.arenaPeakUsedBytes -le $n.arenaPeakReservedBytes -and $n.arenaUsedBytes -le $n.arenaReservedBytes -and $n.arenaCachedBytes -le $n.arenaReservedBytes -and $n.arenaPeakCachedBytes -le $n.arenaPeakReservedBytes)
    }
    if ($n.ContainsKey('allocatorCalls') -and $n.ContainsKey('freeCalls')) { Add-ValidationCheck $v 'allocator and free calls balance after drain' ($n.allocatorCalls -eq $n.freeCalls) }
    if ($n.ContainsKey('processPeakCommittedBytes')) { Add-ValidationCheck $v 'process peak commit fits OS ceiling' ($n.processPeakCommittedBytes -le [decimal]$OsLimit) }
    foreach ($field in @('arenaUsedBytes','arenaReservedBytes','arenaCachedBytes','providerLiveBytesAtDrain','outputLiveBytesAtDrain','activeOwnerCountAtDrain')) {
        if ($n.ContainsKey($field)) { Add-ValidationCheck $v "$field is zero at drain" ($n[$field] -eq 0) }
    }
    $drained = Get-Field $Json 'drained'
    $preserved = Get-Field $Json 'staticStatePreserved'
    Add-ValidationCheck $v 'workload drained' ($drained -is [bool] -and $drained)
    Add-ValidationCheck $v 'static state survived scratch release' ($preserved -is [bool] -and $preserved)
    if ($Scenario -ceq 'cancellation' -and $n.ContainsKey('providerLiveBytesBeforeCancel') -and $n.ContainsKey('providerLiveBytesAfterCancel')) {
        Add-ValidationCheck $v 'provider ownership survives cancel request until drain' ($n.providerLiveBytesBeforeCancel -gt 0 -and $n.providerLiveBytesAfterCancel -eq $n.providerLiveBytesBeforeCancel)
        Add-ValidationCheck $v 'output ownership survives cancel request until drain' ($n.outputLiveBytesAfterCancel -eq $n.outputLiveBytesBeforeCancel)
    }
    if ($n.ContainsKey('qpcFrequency')) { Add-ValidationCheck $v 'QPC frequency is positive' ($n.qpcFrequency -gt 0) }
    return $v.ToArray()
}
function Copy-Json($Value) { ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $Value -Depth 20 -Compress) -AsHashtable -Depth 20 }
function Add-ScenarioChecks([string]$Prefix, [object[]]$Results) {
    foreach ($result in $Results) { Add-Check ('{0}: {1}' -f $Prefix, $result.name) ([bool]$result.passed) }
}
function Get-Signature($Json) {
    $rows = @((Get-Field $Json 'results') | Sort-Object { [int](Get-Field $_ 'id') } | ForEach-Object {
        [ordered]@{ id=(Get-Field $_ 'id'); mailboxId=(Get-Field $_ 'mailboxId'); status=(Get-Field $_ 'status'); value=(Get-Field $_ 'value') }
    })
    $same = [ordered]@{ results=$rows; counters=[ordered]@{}; metrics=[ordered]@{} }
    foreach ($f in @('completed','rejected','cancelled','failed')) { $same.counters[$f] = Get-Field $Json $f }
    foreach ($f in @('continuationCopyBytes','fixedMetadataBytes','workingScratchBytes','cacheCapBytes','arenaUsedBytes','arenaReservedBytes','arenaPeakUsedBytes','arenaCachedBytes','arenaPeakReservedBytes','arenaPeakCachedBytes','allocatorCalls','freeCalls','providerLiveBytesBeforeCancel','providerLiveBytesAfterCancel','providerLiveBytesAtDrain','outputLiveBytesBeforeCancel','outputLiveBytesAfterCancel','outputLiveBytesAtDrain','activeOwnerCountAtDrain','processLimitEnforced','drained','staticStatePreserved')) {
        $same.metrics[$f] = Get-Field $Json $f
    }
    return ConvertTo-Json -InputObject $same -Depth 20 -Compress
}

try {
    [IO.Directory]::CreateDirectory($runDir) | Out-Null
    Add-Check 'fresh isolated run directory created' (Test-Path -LiteralPath $runDir -PathType Container)
    $clang = Resolve-Clang
    if (-not (Test-Path -LiteralPath $clang -PathType Leaf)) { throw "Clang executable is missing: $clang" }
    $clangHash = Get-Hash $clang
    $version = Invoke-Direct $clang @('--version') $runDir $timeoutSeconds
    Record-Process 'compiler-version' $version
    Add-Check 'clang version command exited cleanly' (-not $version.timedOut -and $null -eq $version.startError -and $version.exitCode -eq 0) $version.stderr
    $report.compiler = [ordered]@{ path=$clang; sha256=$clangHash; versionStdout=$version.stdout; versionStderr=$version.stderr; override='AGENTLANG_LLVM_CLANG' }
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw "Native arena/mailbox source is missing: $sourcePath" }
    $sourceHash = Get-Hash $sourcePath
    $report.source = [ordered]@{ path=$sourceRel; bytes=(Get-Item -LiteralPath $sourcePath).Length; sha256=$sourceHash }

    foreach ($opt in $optimizations) {
        $exe = Join-Path $runDir "native-arena-mailbox-$opt.exe"
        $commandArguments = @('-std=c11','-Wall','-Wextra','-Werror',("-$opt"),$sourcePath,'-o',$exe,'-lpsapi')
        $build = Invoke-Direct $clang $commandArguments $runDir $timeoutSeconds
        Record-Process 'build' $build ([ordered]@{ optimization=$opt })
        $built = -not $build.timedOut -and $null -eq $build.startError -and $build.exitCode -eq 0 -and (Test-Path -LiteralPath $exe -PathType Leaf)
        Add-Check "$opt build succeeded" $built $build.stderr
        $exeHash = if (Test-Path -LiteralPath $exe -PathType Leaf) { Get-Hash $exe } else { $null }
        $builds.Add([ordered]@{ optimization=$opt; executable=$exe; executableSha256=$exeHash; process=$build })
        if ($built) {
            $self = Invoke-Direct $exe @('--self-test') $runDir $timeoutSeconds
            Record-Process 'self-test' $self ([ordered]@{ optimization=$opt })
            $selfOk = -not $self.timedOut -and $null -eq $self.startError -and $self.exitCode -eq 0
            $selfJson = $null
            $selfChecks = [Collections.Generic.List[object]]::new()
            Add-ValidationCheck $selfChecks 'self-test exits cleanly' $selfOk
            if ($selfOk) {
                try {
                    $selfJson = ConvertFrom-Json -InputObject $self.stdout -AsHashtable -Depth 20 -ErrorAction Stop
                    $schema = [decimal]0; $assertions = [decimal]0
                    Add-ValidationCheck $selfChecks 'self-test schemaVersion is 1' ((Try-UInt (Get-Field $selfJson 'schemaVersion') ([ref]$schema)) -and $schema -eq 1)
                    Add-ValidationCheck $selfChecks 'self-test kind is self-test' ((Get-Field $selfJson 'kind') -ceq 'self-test')
                    $passed = Get-Field $selfJson 'passed'
                    Add-ValidationCheck $selfChecks 'self-test passed is true' ($passed -is [bool] -and $passed)
                    Add-ValidationCheck $selfChecks 'self-test assertions are positive' ((Try-UInt (Get-Field $selfJson 'assertions') ([ref]$assertions)) -and $assertions -gt 0)
                    $enforced = Get-Field $selfJson 'processLimitEnforced'
                    Add-ValidationCheck $selfChecks 'self-test enforces process limit' ($enforced -is [bool] -and $enforced)
                } catch { Add-ValidationCheck $selfChecks 'self-test stdout is JSON' $false }
            }
            Add-ScenarioChecks "$opt self-test" $selfChecks.ToArray()
            $safety.Add([ordered]@{ optimization=$opt; stdout=$self.stdout; stderr=$self.stderr; rawJson=$selfJson; checks=$selfChecks.ToArray() })
        } else {
            $safety.Add([ordered]@{ optimization=$opt; checks=@(); passed=$false })
        }
    }
    if (@($checks | Where-Object { -not $_.passed }).Count -gt 0) { throw 'Compiler or native self-test gate failed; workload matrix was not started.' }

    $base = [ordered]@{
        schemaVersion=1; mode='turn'; scenario='cpu'; seed=17; requestsOffered=2; mailboxCount=8
        processLimitBytes=67108864; processLimitEnforced=$true
        results=@(
            [ordered]@{ id=0; mailboxId=0; status='completed'; value=(Value-Oracle 17 0) },
            [ordered]@{ id=1; mailboxId=1; status='completed'; value=(Value-Oracle 17 1) }
        )
        completed=2; rejected=0; cancelled=0; failed=0
        continuationCopyBytes=0; fixedMetadataBytes=0; workingScratchBytes=524288; cacheCapBytes=1048576
        arenaUsedBytes=0; arenaReservedBytes=0
        arenaPeakUsedBytes=0; arenaCachedBytes=0; arenaPeakReservedBytes=1024; arenaPeakCachedBytes=0
        allocatorCalls=0; freeCalls=0
        providerLiveBytesBeforeCancel=0; providerLiveBytesAfterCancel=0; providerLiveBytesAtDrain=0
        outputLiveBytesBeforeCancel=0; outputLiveBytesAfterCancel=0; outputLiveBytesAtDrain=0
        privateBytes=4096; workingSetBytes=4096; processPeakCommittedBytes=4096
        elapsedQpcTicks=10; qpcFrequency=1000; elapsedMilliseconds=10
        drained=$true; activeOwnerCountAtDrain=0; staticStatePreserved=$true
    }
    $baseChecks = @(Test-Scenario $base 'turn' 'cpu' 17 2 $budget $processLimit)
    $baseAccepted = @($baseChecks | Where-Object { -not $_.passed }).Count -eq 0
    $controls.Add([ordered]@{ name='valid synthetic workload accepted'; passed=$baseAccepted; rejectedBy=@() })
    Add-Check 'validator accepts its valid baseline' $baseAccepted
    $mutations = [ordered]@{
        'wrong completed value rejected' = { param($x) $x.results[0].value++ }
        'missing ID rejected' = { param($x) $x.results[0].id = 1 }
        'missing result value field rejected' = { param($x) $x.results[0].Remove('value') }
        'balanced resource rejection in normal workload rejected' = { param($x) $x.results[0].status='rejected_capacity'; $x.results[0].value=$null; $x.completed=1; $x.rejected=1 }
        'inconsistent counts rejected' = { param($x) $x.completed = 1 }
        'live owner at drain rejected' = { param($x) $x.activeOwnerCountAtDrain = 1 }
        'nonzero reserved bytes at drain rejected' = { param($x) $x.arenaReservedBytes = 16 }
        'nonzero cached bytes at drain rejected' = { param($x) $x.arenaCachedBytes = 16 }
        'disabled process cap rejected' = { param($x) $x.processLimitEnforced = $false }
        'arena budget overflow rejected' = { param($x) $x.fixedMetadataBytes = 1; $x.arenaPeakReservedBytes = 8388608 }
    }
    foreach ($case in $mutations.GetEnumerator()) {
        $mutated = Copy-Json $base
        & $case.Value $mutated
        $badChecks = @(Test-Scenario $mutated 'turn' 'cpu' 17 2 $budget $processLimit | Where-Object { -not $_.passed })
        $ok = $badChecks.Count -gt 0
        $controls.Add([ordered]@{ name=$case.Key; passed=$ok; rejectedBy=@($badChecks | ForEach-Object { $_.name }) })
        Add-Check "validator control: $($case.Key)" $ok
    }

    $cancellationBase = Copy-Json $base
    $cancellationBase.scenario = 'cancellation'
    $cancellationBase.results[0].status = 'cancelled'
    $cancellationBase.results[0].value = $null
    $cancellationBase.completed = 1
    $cancellationBase.cancelled = 1
    $cancellationBase.providerLiveBytesBeforeCancel = 64
    $cancellationBase.providerLiveBytesAfterCancel = 64
    $cancellationBase.outputLiveBytesBeforeCancel = 32
    $cancellationBase.outputLiveBytesAfterCancel = 32
    $cancellationChecks = @(Test-Scenario $cancellationBase 'turn' 'cancellation' 17 2 $budget $processLimit)
    $explicitNullAccepted = @($cancellationChecks | Where-Object { -not $_.passed }).Count -eq 0
    $controls.Add([ordered]@{ name='explicit null result value accepted'; passed=$explicitNullAccepted; rejectedBy=@() })
    Add-Check 'validator accepts an explicit null value for cancellation' $explicitNullAccepted
    $missingCancellationValue = Copy-Json $cancellationBase
    $missingCancellationValue.results[0].Remove('value')
    $missingCancellationChecks = @(Test-Scenario $missingCancellationValue 'turn' 'cancellation' 17 2 $budget $processLimit | Where-Object { -not $_.passed })
    $missingCancellationRejected = $missingCancellationChecks.Count -gt 0
    $controls.Add([ordered]@{ name='missing cancelled result value rejected'; passed=$missingCancellationRejected; rejectedBy=@($missingCancellationChecks | ForEach-Object { $_.name }) })
    Add-Check 'validator rejects a missing cancelled result value' $missingCancellationRejected

    foreach ($scheduled in $matrix) {
        $opt = [string]$scheduled.optimization
        $exe = Join-Path $runDir "native-arena-mailbox-$opt.exe"
        $commandArguments = @('--mode',[string]$scheduled.mode,'--scenario',[string]$scheduled.scenario,'--seed',[string]$seed,'--requests',[string]$requests,'--budget-bytes',[string]$budget,'--process-limit-bytes',[string]$processLimit)
        $p = Invoke-Direct $exe $commandArguments $runDir $timeoutSeconds
        Record-Process 'workload' $p $scheduled
        $run = [ordered]@{
            sequence=$scheduled.sequence; optimization=$opt; repeat=$scheduled.repeat
            mode=$scheduled.mode; scenario=$scheduled.scenario; seed=$seed
            configuration=[ordered]@{ requests=$requests; budgetBytes=$budget; processLimitBytes=$processLimit }
            arguments=$commandArguments; exitCode=$p.exitCode; timedOut=$p.timedOut; stdout=$p.stdout; stderr=$p.stderr
            rawJson=$null; checks=@(); passed=$false
        }
        $exitOk = -not $p.timedOut -and $null -eq $p.startError -and $p.exitCode -eq 0
        Add-Check "$opt R$($scheduled.repeat) $($scheduled.mode)/$($scheduled.scenario) exited cleanly" $exitOk $p.stderr
        if ($exitOk) {
            try {
                $json = ConvertFrom-Json -InputObject $p.stdout -AsHashtable -Depth 30 -ErrorAction Stop
                $run.rawJson = $json
                $run.checks = @(Test-Scenario $json $scheduled.mode $scheduled.scenario $seed $requests $budget $processLimit)
                Add-ScenarioChecks "$opt R$($scheduled.repeat) $($scheduled.mode)/$($scheduled.scenario)" $run.checks
                $run.passed = @($run.checks | Where-Object { -not $_.passed }).Count -eq 0
            } catch { Add-Check "$opt R$($scheduled.repeat) $($scheduled.mode)/$($scheduled.scenario) stdout is JSON" $false $_.Exception.Message }
        }
        $runs.Add($run)
    }

    $signatures = @{}
    $completedValues = @{}
    $semanticChecks = [Collections.Generic.List[object]]::new()
    foreach ($run in $runs) {
        if ($null -eq $run.rawJson) { continue }
        $key = "$($run.mode)|$($run.scenario)"
        $signature = Get-Signature $run.rawJson
        if (-not $signatures.ContainsKey($key)) { $signatures[$key] = [ordered]@{ value=$signature; first="$($run.optimization)/R$($run.repeat)" } }
        else {
            $same = $signatures[$key].value -ceq $signature
            Add-Check "deterministic outputs match for $($run.mode)/$($run.scenario)" $same
        }
        foreach ($row in @((Get-Field $run.rawJson 'results'))) {
            if ((Get-Field $row 'status') -cne 'completed') { continue }
            $id = [string](Get-Field $row 'id')
            $value = [decimal](Get-Field $row 'value')
            if ($completedValues.ContainsKey($id)) {
                $same = $completedValues[$id] -eq $value
                $semanticChecks.Add([ordered]@{ id=[int]$id; value=$value; matches=$same })
                if (-not $same) { Add-Check "completed value agrees for ID $id" $false }
            } else { $completedValues[$id] = $value }
        }
    }
    Add-Check 'all 72 preregistered workload runs were attempted' ($runs.Count -eq $expectedMatrixRuns -and $matrix.Count -eq $expectedMatrixRuns)
    $report.comparisons = [ordered]@{
        deterministicConfigurations=$signatures.Count
        deterministicFields='statuses, values, mailbox IDs, counters, arena/provider/output ownership, allocator/free counts, drain flags, and process-limit flag; physical memory and timing excluded'
        semanticCompletedValues=@($semanticChecks)
    }
} catch {
    $message = $_.Exception.Message
    $errors.Add($message)
    Add-Check 'verification completed without an uncaught failure' $false $message
} finally {
    $report.completedUtc = [DateTime]::UtcNow.ToString('O')
    $report.builds = @($builds)
    $report.executableHashes = @($builds | ForEach-Object { [ordered]@{ optimization=$_.optimization; path=$_.executable; sha256=$_.executableSha256 } })
    $report.processes = @($processes)
    $report.safety = @($safety)
    $report.workloadRuns = @($runs)
    $report.negativeControls = @($controls)
    $report.checks = @($checks)
    $failed = @($checks | Where-Object { -not $_.passed } | ForEach-Object { $_.name })
    $report.passed = $checks.Count -gt 0 -and $failed.Count -eq 0
    if ($failed.Count -gt 0) { $report.failure = $failed -join [Environment]::NewLine }
    elseif ($errors.Count -gt 0) { $report.failure = @($errors) -join [Environment]::NewLine }
    try {
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($reportPath)) | Out-Null
        [IO.File]::WriteAllText($reportPath, (ConvertTo-Json -InputObject $report -Depth 50) + [Environment]::NewLine, $utf8)
    } catch {
        Write-Error ('Could not save verifier evidence to {0}: {1}' -f $reportPath, $_.Exception.Message)
        exit 1
    }
}
Write-Output "EvidencePath=$reportPath"
Write-Output "Passed=$($report.passed)"
if (-not $report.passed) {
    Write-Error "Native arena/mailbox verification failed. Evidence saved to $reportPath. $($report.failure)"
    exit 1
}
