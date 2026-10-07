#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$Mode,
    [string]$TaskId,
    [string]$ProjectPath,
    [string]$CliDll,
    [string]$EvidencePath,
    [string]$StartingProjectPath,
    [switch]$RequireFrozenPin
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$sequence = @('S01','S06','S07')
$expectedCounts = @{ S01 = 10; S06 = 10; S07 = 54 }
$checks = [Collections.Generic.List[object]]::new()
$sessions = [Collections.Generic.List[object]]::new()
$processRuns = [Collections.Generic.List[object]]::new()
$passed = $false
$failure = $null
$targetOracle = $null
$caseOracleEvidence = @()
$oraclePath = Join-Path $repo 'experiments/AgentLang.SubagentTrials/business-policy-001/acceptance.json'
$resolvedProject = $null
$resolvedStartingProject = $null
$originalProjectPath = $null
$originalStartingProjectPath = $null
$resolvedCli = $null
$scratchProject = $null
$testsRoot = Join-Path $repo '.agentlang/business-policy-001/tests'
$actorInventoryBefore = @()
$actorInventoryAfter = @()
$preservation = [ordered]@{ mode=$Mode; checked=$false; sourceWords=0; nominalTypes=0; unchanged=$null }
$reuse = [ordered]@{ candidatePriorTaskSymbols=@(); priorTaskSymbols=$null; startingDictionaryTaskSymbols=$null; directDependencies=@(); transitiveDependencies=@(); reusedPriorTaskSymbols=$null; status='unavailable until starting inventory and dependency graph are checked' }
$runtimeInfo = [ordered]@{}
$evidenceTarget = $null
$startingStateMetadata = $null
$checkFrozenPin = $false
$frozenPinInfo = [ordered]@{required=[bool]$RequireFrozenPin;checked=$false;status='not checked';path=$null;sha256=$null}
$frozenPinRecord = $null
$actorHashBefore = $null
$actorHashAfter = $null
$startedUtc = [DateTime]::UtcNow.ToString('O')
$utf8NoBom = [Text.UTF8Encoding]::new($false)

function Get-Field($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    if ($Object -is [Collections.IDictionary]) { return $Object[$Name] }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Add-Check([string]$Name, [bool]$Condition, [object]$Details = $null) {
    $entry = [ordered]@{ name=$Name; passed=$Condition }
    if ($null -ne $Details) { $entry.details = $Details }
    $checks.Add($entry)
    if (-not $Condition) { throw "Business policy verification failed: $Name" }
}

function Resolve-RepoPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'A required path was empty.' }
    if ([IO.Path]::IsPathFullyQualified($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath($Path, $repo)
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-RelativeInventory([string]$Root) {
    $resolved = [IO.Path]::GetFullPath($Root)
    $rows = [Collections.Generic.List[object]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $resolved -Recurse -Force -File) {
        $relative = [IO.Path]::GetRelativePath($resolved, $file.FullName).Replace('\','/')
        if ($relative -match '(^|/)(bin|obj)(/|$)') { continue }
        $rows.Add([pscustomobject][ordered]@{ path=$relative; sha256=(Get-Sha256 $file.FullName) })
    }
    return @($rows | Sort-Object path)
}

function Get-FreezeInventory([string]$Root) {
    $resolved = [IO.Path]::GetFullPath($Root)
    $rows = [Collections.Generic.List[object]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $resolved -Recurse -Force -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } | Sort-Object FullName) {
        $rows.Add([ordered]@{
            path=[IO.Path]::GetRelativePath($resolved,$file.FullName).Replace('\','/')
            bytes=$file.Length
            sha256=Get-Sha256 $file.FullName
        })
    }
    return @($rows)
}

function Get-CanonicalJson($Value) {
    return ConvertTo-Json -InputObject $Value -Depth 100 -Compress
}

function Validate-FrozenPin([string]$StartPath, [string]$ActorPath, [string]$RuntimeCliPath, [string]$CurrentMode, [string]$CurrentTask, $Acceptance) {
    $runDirectory = Split-Path -Parent $StartPath
    $pinPath = Join-Path $runDirectory 'prelaunch.json'
    $frozenPinInfo.path = $pinPath
    if (-not (Test-Path -LiteralPath $pinPath -PathType Leaf)) {
        if ($RequireFrozenPin) {
            Add-Check 'required frozen prelaunch pin exists' $false $pinPath
        }
        $checks.Add([ordered]@{name='unfrozen verifier run classified as preflight/control';passed=$true;details='No sibling prelaunch.json was supplied.'})
        $frozenPinInfo.status = 'unfrozen-preflight-or-control'
        return $null
    }
    $pin = Get-Content -LiteralPath $pinPath -Raw | ConvertFrom-Json -AsHashtable -Depth 100
    $pinHash = Get-Sha256 $pinPath
    $frozenPinInfo.sha256 = $pinHash
    Add-Check 'frozen pin schema, study, mode, and task match' ([int]$pin.schemaVersion -eq 1 -and [string]$pin.studyId -ceq [string]$Acceptance.studyId -and [string]$pin.mode -ceq $CurrentMode -and [string]$pin.taskId -ceq $CurrentTask)
    $pinnedActor = [string](Get-Field $pin 'actorProjectPath')
    if ([string]::IsNullOrWhiteSpace($pinnedActor)) { $pinnedActor = [string](Get-Field $pin 'projectPath') }
    $actorPathMatches = [IO.Path]::IsPathFullyQualified($pinnedActor) -and [IO.Path]::GetFullPath($pinnedActor) -ieq [IO.Path]::GetFullPath($ActorPath)
    Add-Check 'frozen pin actor project path matches argument' $actorPathMatches @{expected=$ActorPath;actual=$pinnedActor}

    $statePath = Join-Path $runDirectory 'starting-state.json'
    Add-Check 'frozen run starting-state exists' (Test-Path -LiteralPath $statePath -PathType Leaf) $statePath
    $stateHash = Get-Sha256 $statePath
    Add-Check 'frozen starting-state hash matches pin' ($stateHash -ceq ([string]$pin.startingStateSha256).ToLowerInvariant()) @{expected=$pin.startingStateSha256;actual=$stateHash}
    $promptPath = Join-Path $runDirectory 'prompt.txt'
    Add-Check 'frozen actor prompt exists' (Test-Path -LiteralPath $promptPath -PathType Leaf) $promptPath
    $promptHash = Get-Sha256 $promptPath
    Add-Check 'frozen prompt hash matches pin' ($promptHash -ceq ([string]$pin.promptSha256).ToLowerInvariant()) @{expected=$pin.promptSha256;actual=$promptHash}
    Add-Check 'frozen oracle hash matches acceptance corpus' ((Get-Sha256 $oraclePath) -ceq ([string]$pin.oracleSha256).ToLowerInvariant())
    $verifierPath = Join-Path $repo 'scripts/Verify-BusinessPolicyTrial.ps1'
    $hostPath = Join-Path $repo 'scripts/Start-SubagentTrialHost.ps1'
    Add-Check 'frozen verifier hash matches current verifier' ((Get-Sha256 $verifierPath) -ceq ([string]$pin.independentVerifierSha256).ToLowerInvariant())
    Add-Check 'frozen host hash matches current protocol host' ((Get-Sha256 $hostPath) -ceq ([string]$pin.hostSha256).ToLowerInvariant())

    $startInventory = Get-FreezeInventory $StartPath
    $pinnedStart = @(Get-Field $pin 'startingProjectFiles')
    Add-Check 'frozen starting project file inventory matches' ((Get-CanonicalJson @($startInventory)) -ceq (Get-CanonicalJson @($pinnedStart))) @{files=$startInventory.Count}
    $runtimeRoot = if ($CurrentMode -ceq 'conventional') { Join-Path $repo '.agentlang/business-policy-001/broker-bin' } else { Split-Path -Parent $RuntimeCliPath }
    Add-Check 'frozen runtime directory exists' (Test-Path -LiteralPath $runtimeRoot -PathType Container) $runtimeRoot
    $runtimeInventory = Get-FreezeInventory $runtimeRoot
    $pinnedRuntime = @(Get-Field $pin 'runtimeFiles')
    Add-Check 'frozen runtime file inventory matches' ((Get-CanonicalJson @($runtimeInventory)) -ceq (Get-CanonicalJson @($pinnedRuntime))) @{path=$runtimeRoot;files=$runtimeInventory.Count}

    $frozenPinInfo.checked = $true
    $frozenPinInfo.status = 'validated'
    $script:checkFrozenPin = $true
    return [ordered]@{path=$pinPath;sha256=$pinHash;data=$pin}
}

function Test-Within([string]$Path, [string]$Root) {
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $prefix = $fullRoot + [IO.Path]::DirectorySeparatorChar
    return $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Copy-ProjectToScratch([string]$Source, [string]$Destination) {
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $Source -Recurse -Force -File) {
        $relative = [IO.Path]::GetRelativePath($Source, $file.FullName)
        if ($relative -match '(^|[\\/])(\.git|bin|obj)([\\/]|$)') { continue }
        $target = Join-Path $Destination $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        [IO.File]::Copy($file.FullName, $target, $false)
    }
}

function Invoke-CapturedProcess([string]$FileName, [string[]]$Arguments, [string]$WorkingDirectory, [int]$TimeoutSeconds = 180) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $FileName
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $utf8NoBom
    $start.StandardErrorEncoding = $utf8NoBom
    $start.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $start.Environment['DOTNET_NOLOGO'] = '1'
    foreach ($argument in $Arguments) { $start.ArgumentList.Add([string]$argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $clock = [Diagnostics.Stopwatch]::StartNew()
    if (-not $process.Start()) { throw "Could not start process '$FileName'." }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $finished = $process.WaitForExit($TimeoutSeconds * 1000)
    if (-not $finished) {
        try { $process.Kill($true) } catch { }
        $process.WaitForExit()
    }
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    $clock.Stop()
    $result = [ordered]@{
        fileName=$FileName; arguments=@($Arguments); workingDirectory=$WorkingDirectory
        exitCode=$(if ($finished) { $process.ExitCode } else { $null })
        timedOut=(-not $finished); durationMs=$clock.ElapsedMilliseconds
        stdout=$stdout; stderr=$stderr
    }
    $process.Dispose()
    $processRuns.Add($result)
    return $result
}

function Invoke-JsonlSession([string]$Project, [object[]]$Requests, [string]$Label) {
    if ($Requests.Count -gt 95) { throw "JSONL session '$Label' exceeds the 95-request verifier limit." }
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.WorkingDirectory = $Project
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $utf8NoBom
    $start.StandardErrorEncoding = $utf8NoBom
    $start.StandardInputEncoding = $utf8NoBom
    $start.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $start.Environment['DOTNET_NOLOGO'] = '1'
    $start.ArgumentList.Add($resolvedCli)
    $start.ArgumentList.Add('--project')
    $start.ArgumentList.Add($Project)
    $start.ArgumentList.Add('--jsonl')
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $clock = [Diagnostics.Stopwatch]::StartNew()
    if (-not $process.Start()) { throw "Could not start JSONL session '$Label'." }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $requestLines = [Collections.Generic.List[string]]::new()
    foreach ($request in $Requests) {
        $line = ConvertTo-Json -InputObject $request -Depth 80 -Compress
        $requestLines.Add($line)
        $process.StandardInput.WriteLine($line)
    }
    $process.StandardInput.Close()
    $finished = $process.WaitForExit(180000)
    if (-not $finished) {
        try { $process.Kill($true) } catch { }
        $process.WaitForExit()
    }
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    $clock.Stop()
    $responseLines = @($stdout -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $responses = [Collections.Generic.List[object]]::new()
    $parseErrors = [Collections.Generic.List[string]]::new()
    foreach ($responseLine in $responseLines) {
        try { $responses.Add((ConvertFrom-Json -InputObject $responseLine -AsHashtable -Depth 100)) }
        catch { $parseErrors.Add($_.Exception.Message) }
    }
    $session = [ordered]@{
        label=$Label; requestCount=$Requests.Count; requests=@($requestLines)
        responseCount=$responseLines.Count; responseLines=$responseLines
        responses=@($responses); parseErrors=@($parseErrors); exitCode=$(if ($finished) { $process.ExitCode } else { $null })
        timedOut=(-not $finished); durationMs=$clock.ElapsedMilliseconds; stderr=$stderr
    }
    $sessions.Add($session)
    $process.Dispose()
    if (-not $finished) { throw "JSONL session '$Label' timed out." }
    $exitCode = $session.exitCode
    if ($null -eq $exitCode -or [int]$exitCode -ne 0) { throw "JSONL session '$Label' exited with code ${exitCode}: $stderr" }
    if ($parseErrors.Count -gt 0) { throw "JSONL session '$Label' returned invalid JSON: $($parseErrors[0])" }
    if ($responses.Count -ne $Requests.Count) { throw "JSONL session '$Label' returned $($responses.Count) responses for $($Requests.Count) requests." }
    return ,@($responses)
}

function Assert-ResponsesOk([object[]]$Responses, [string]$Label) {
    for ($index = 0; $index -lt $Responses.Count; $index++) {
        if (-not [bool]$Responses[$index].ok) {
            $message = [string]$Responses[$index].message
            if ([string]::IsNullOrWhiteSpace($message)) { $message = [string]$Responses[$index].error.message }
            throw "$Label request $index failed: $message"
        }
    }
}

function Get-TargetTask($Oracle, [string]$Id) {
    $matches = @($Oracle.tasks | Where-Object { $_.id -ceq $Id })
    if ($matches.Count -ne 1) { throw "Acceptance corpus must contain exactly one task '$Id'." }
    return $matches[0]
}

function Get-ExpectedForCase($Task, $Case) {
    $kind = [string]$Case.kind
    $balanceText = [string]$Case.balanceMinor
    if ($balanceText -notmatch '^(0|-?[1-9][0-9]*)$') { throw "Case '$($Case.id)' has noncanonical balanceMinor '$balanceText'." }
    $balance = [Numerics.BigInteger]::Parse($balanceText, [Globalization.NumberStyles]::AllowLeadingSign, [Globalization.CultureInfo]::InvariantCulture)
    if ($balance -lt [Numerics.BigInteger]([long]::MinValue) -or $balance -gt [Numerics.BigInteger]([long]::MaxValue)) {
        throw "Case '$($Case.id)' is outside signed Int64 range."
    }
    $premium = [string]::Equals($kind, 'premium', [StringComparison]::Ordinal)
    $type = [string]$Task.outputs[0]
    $value = switch ($Task.id) {
        'S01' { $premium }
        'S06' { if ($premium) { '1000' } else { '0' } }
        'S07' {
            if ($premium) {
                # BigInteger division truncates toward zero and cannot overflow at Int64 boundaries.
                ([Numerics.BigInteger]::Divide(($balance * [Numerics.BigInteger]9), [Numerics.BigInteger]10)).ToString([Globalization.CultureInfo]::InvariantCulture)
            } else { $balance.ToString([Globalization.CultureInfo]::InvariantCulture) }
        }
        default { throw "Unsupported task id '$($Task.id)'." }
    }
    $providedType = [string]$Case.expected.type
    if ($providedType -cne $type) { throw "Case '$($Case.id)' expected type '$providedType' differs from output '$type'." }
    if ($type -ceq 'Bool') {
        if ($Case.expected.value -isnot [bool] -or [bool]$Case.expected.value -ne [bool]$value) { throw "Case '$($Case.id)' expected Bool does not match the independent oracle." }
    } else {
        if ($Case.expected.value -isnot [string] -or [string]$Case.expected.value -cne [string]$value) { throw "Case '$($Case.id)' expected integer text does not match the independent oracle." }
    }
    return [ordered]@{ type=$type; value=$value; balanceMinor=$balanceText; premium=$premium }
}

function Resolve-FileFromRoot([string]$BasePath, [string]$Path) {
    if ([IO.Path]::IsPathFullyQualified($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath($Path, $BasePath)
}

function Get-TreeHash([string]$Path) {
    if (Test-Path -LiteralPath $Path -PathType Leaf) { return Get-Sha256 $Path }
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { throw "Hash input does not exist: $Path" }
    $rows = Get-RelativeInventory $Path
    $canonical = ConvertTo-Json -InputObject @($rows) -Depth 20 -Compress
    $bytes = $utf8NoBom.GetBytes($canonical)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Validate-StartingState([string]$StartPath, [string]$ActorPath, $Acceptance, [string]$CurrentMode, [string]$CurrentTask) {
    $parent = Split-Path -Parent $StartPath
    $statePath = Join-Path $parent 'starting-state.json'
    if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) {
        $checks.Add([ordered]@{name='optional starting-state metadata absent';passed=$true;details='Source and identity preservation still use the immutable StartingProjectPath.'})
        return $null
    }
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json -AsHashtable -Depth 100
    Add-Check 'starting-state schema and study match' ([int]$state.schemaVersion -eq 1 -and [string]$state.studyId -ceq [string]$Acceptance.studyId -and [string]$state.mode -ceq $CurrentMode -and [string]$state.taskId -ceq $CurrentTask)
    $expectedIndex = [array]::IndexOf($sequence, $CurrentTask) + 1
    Add-Check 'starting-state sequence index matches canonical order' ([int]$state.sequenceIndex -eq $expectedIndex) "expected one-based $expectedIndex; actual $($state.sequenceIndex)"
    $declaredStart = [string](Get-Field (Get-Field $state 'project') 'path')
    $declaredActor = [string](Get-Field (Get-Field $state 'actor') 'projectPath')
    $pathsMatch = [IO.Path]::IsPathFullyQualified($declaredStart) -and [IO.Path]::GetFullPath($declaredStart) -ieq [IO.Path]::GetFullPath($StartPath) -and [IO.Path]::IsPathFullyQualified($declaredActor) -and [IO.Path]::GetFullPath($declaredActor) -ieq [IO.Path]::GetFullPath($ActorPath)
    Add-Check 'starting-state project and actor paths match supplied run' $pathsMatch @{project=$declaredStart;actor=$declaredActor}
    $runtimeMetadata = Get-Field $state 'runtime'
    Add-Check 'starting-state pins CLI and Business runtime artifacts' (-not [string]::IsNullOrWhiteSpace([string](Get-Field $runtimeMetadata 'cliPath')) -and -not [string]::IsNullOrWhiteSpace([string](Get-Field $runtimeMetadata 'cliSha256')) -and -not [string]::IsNullOrWhiteSpace([string](Get-Field $runtimeMetadata 'businessPath')) -and -not [string]::IsNullOrWhiteSpace([string](Get-Field $runtimeMetadata 'businessSha256')))
    Add-Check 'starting-state has source input hashes' (@(Get-Field $state 'sourceInputs').Count -gt 0)
    $startInventory = Get-RelativeInventory $StartPath
    $listedFiles = @(Get-Field $state.project 'files')
    Add-Check 'starting-state includes project hash rows' ($listedFiles.Count -gt 0)
    $projectRowsOk = $true
    $projectHashDetails = [Collections.Generic.List[object]]::new()
    foreach ($row in $listedFiles) {
        $actualPath = Resolve-FileFromRoot $StartPath ([string]$row.path)
        $relative = [IO.Path]::GetRelativePath($StartPath, $actualPath).Replace('\','/')
        $safePath = -not [IO.Path]::IsPathFullyQualified([string]$row.path) -and (Test-Within $actualPath $StartPath)
        $exists = $safePath -and (Test-Path -LiteralPath $actualPath -PathType Leaf)
        $actualHash = if ($exists) { Get-Sha256 $actualPath } else { $null }
        $ok = $exists -and ([string]$actualHash -ceq ([string]$row.sha256).ToLowerInvariant())
        if (-not $ok) { $projectRowsOk = $false }
        $projectHashDetails.Add([ordered]@{path=$relative;expected=[string]$row.sha256;actual=$actualHash;passed=$ok})
    }
    Add-Check 'starting-state project file hashes match immutable start' $projectRowsOk @($projectHashDetails)
    $listedRelativePaths = @($projectHashDetails | ForEach-Object { [string]$_.path } | Sort-Object -Unique)
    $actualRelativePaths = @($startInventory | ForEach-Object { [string]$_.path } | Sort-Object -Unique)
    Add-Check 'starting-state rows cover the complete project inventory' ((@($projectHashDetails).Count -eq $listedFiles.Count) -and (($listedRelativePaths -join "`0") -ceq ($actualRelativePaths -join "`0"))) @{listed=$listedRelativePaths.Count;actual=$actualRelativePaths.Count}
    $projectInventoryHash = Get-TreeHash $StartPath
    $declaredProjectHash = [string](Get-Field (Get-Field $state 'project') 'inventorySha256')
    Add-Check 'starting-state project inventory hash matches immutable start' ($projectInventoryHash -ceq $declaredProjectHash) @{expected=$declaredProjectHash;actual=$projectInventoryHash}
    $actorState = Get-Field $state 'actor'
    $actorFiles = @(Get-Field $actorState 'files')
    $actorInventoryJson = ConvertTo-Json -InputObject @($actorFiles) -Depth 20 -Compress
    $projectInventoryJson = ConvertTo-Json -InputObject @($startInventory) -Depth 20 -Compress
    $actorStateHash = [string](Get-Field $actorState 'inventorySha256')
    Add-Check 'prepared actor inventory matches immutable start snapshot' ($actorInventoryJson -ceq $projectInventoryJson -and $actorStateHash -ceq $projectInventoryHash)

    $rootRowsOk = $true
    $rootHashDetails = [Collections.Generic.List[object]]::new()
    foreach ($row in @(Get-Field $state 'sourceInputs')) {
        $actualPath = Resolve-FileFromRoot $repo ([string]$row.path)
        $safePath = -not [IO.Path]::IsPathFullyQualified([string]$row.path) -and (Test-Within $actualPath $repo)
        $exists = $safePath -and (Test-Path -LiteralPath $actualPath -PathType Leaf)
        $actualHash = if ($exists) { Get-Sha256 $actualPath } else { $null }
        $ok = $exists -and ([string]$actualHash -ceq ([string]$row.sha256).ToLowerInvariant())
        if (-not $ok) { $rootRowsOk = $false }
        $rootHashDetails.Add([ordered]@{kind='sourceInput';path=$actualPath;expected=[string]$row.sha256;actual=$actualHash;passed=$ok})
    }
    foreach ($label in @('seed','runtime')) {
        $group = Get-Field $state $label
        if ($null -eq $group) { continue }
        if ($label -ceq 'runtime') {
            foreach ($entryName in @('cli','business')) {
                $filePath = [string](Get-Field $group ($entryName + 'Path'))
                $expectedHash = [string](Get-Field $group ($entryName + 'Sha256'))
                if ([string]::IsNullOrWhiteSpace($filePath)) { $filePath = [string](Get-Field $group ($entryName + 'DllPath')) }
                if ([string]::IsNullOrWhiteSpace($filePath)) { $filePath = [string](Get-Field $group ($entryName + 'Dll')) }
                if ([string]::IsNullOrWhiteSpace($expectedHash)) { $expectedHash = [string](Get-Field $group ($entryName + 'DllSha256')) }
                if ([string]::IsNullOrWhiteSpace($filePath) -or [string]::IsNullOrWhiteSpace($expectedHash)) { continue }
                $actualPath = Resolve-FileFromRoot $repo $filePath
                $safePath = -not [IO.Path]::IsPathFullyQualified($filePath) -and (Test-Within $actualPath $repo)
                $exists = $safePath -and (Test-Path -LiteralPath $actualPath -PathType Leaf)
                $actualHash = if ($exists) { Get-Sha256 $actualPath } else { $null }
                $ok = $exists -and ([string]$actualHash -ceq $expectedHash.ToLowerInvariant())
                if (-not $ok) { $rootRowsOk = $false }
                $rootHashDetails.Add([ordered]@{kind=$label;path=$actualPath;expected=$expectedHash;actual=$actualHash;passed=$ok})
            }
        } else {
            $seedPath = [string](Get-Field $group 'path')
            $expectedHash = [string](Get-Field $group 'inventorySha256')
            if ([string]::IsNullOrWhiteSpace($expectedHash)) { $expectedHash = [string](Get-Field $group 'sha256') }
            if (-not [string]::IsNullOrWhiteSpace($seedPath) -and -not [string]::IsNullOrWhiteSpace($expectedHash)) {
                $actualPath = Resolve-FileFromRoot $repo $seedPath
                $exists = (Test-Within $actualPath $repo) -and (Test-Path -LiteralPath $actualPath)
                $actualHash = if ($exists) { Get-TreeHash $actualPath } else { $null }
                $ok = $exists -and ([string]$actualHash -ceq $expectedHash.ToLowerInvariant())
                if (-not $ok) { $rootRowsOk = $false }
                $rootHashDetails.Add([ordered]@{kind=$label;path=$actualPath;expected=$expectedHash;actual=$actualHash;passed=$ok})
            }
        }
    }
    Add-Check 'starting-state seed, source, and runtime hashes match' $rootRowsOk @($rootHashDetails)
    if ($CurrentMode -ceq 'conventional') {
        $businessHash = [string](Get-Field (Get-Field $state 'runtime') 'businessSha256')
        $copiedHash = [string](Get-Field (Get-Field $state 'runtime') 'copiedBusinessSha256')
        Add-Check 'conventional starting-state records copied Business DLL hash' (-not [string]::IsNullOrWhiteSpace($copiedHash))
        if (-not [string]::IsNullOrWhiteSpace($copiedHash)) {
            $copiedDll = Join-Path $StartPath 'lib/AgentLang.Business.dll'
            $copiedActual = if (Test-Path -LiteralPath $copiedDll -PathType Leaf) { Get-Sha256 $copiedDll } else { $null }
            Add-Check 'conventional copied Business DLL matches pinned library hash' ($copiedHash -ceq $businessHash -and $copiedActual -ceq $copiedHash)
        }
    }

    $previous = Get-Field $state 'previousAcceptance'
    $previousAcceptancePath = [string](Get-Field $previous 'acceptancePath')
    if ([string]::IsNullOrWhiteSpace($previousAcceptancePath)) { $previousAcceptancePath = [string](Get-Field $previous 'path') }
    if ($expectedIndex -eq 1 -and ($null -ne $previous -and $previousAcceptancePath)) {
        Add-Check 'first task has no prior acceptance' $false
    } elseif ($null -ne $previous -and $previousAcceptancePath) {
        $previousPath = Resolve-FileFromRoot $repo $previousAcceptancePath
        $exists = (Test-Within $previousPath $repo) -and (Test-Path -LiteralPath $previousPath -PathType Leaf)
        $actualHash = if ($exists) { Get-Sha256 $previousPath } else { $null }
        $previousExpectedHash = [string](Get-Field $previous 'acceptanceSha256')
        if ([string]::IsNullOrWhiteSpace($previousExpectedHash)) { $previousExpectedHash = [string](Get-Field $previous 'sha256') }
        Add-Check 'previous acceptance hash matches starting-state' ($exists -and $actualHash -ceq $previousExpectedHash.ToLowerInvariant())
        $prior = Get-Content -LiteralPath $previousPath -Raw | ConvertFrom-Json -AsHashtable -Depth 100
        $expectedPriorTask = $sequence[$expectedIndex - 2]
        Add-Check 'previous acceptance metadata names immediate predecessor' ([string](Get-Field $previous 'taskId') -ceq $expectedPriorTask)
        $priorPassed = [bool]$prior.passed -and [int]$prior.schemaVersion -eq 1 -and [string]$prior.studyId -ceq [string]$Acceptance.studyId -and [string]$prior.mode -ceq $CurrentMode -and [string]$prior.taskId -ceq $expectedPriorTask -and [int]$prior.sequenceIndex -eq ($expectedIndex - 1)
        Add-Check 'previous task acceptance passed in same study and mode' $priorPassed "expected preceding task $expectedPriorTask at sequence index $($expectedIndex - 1)"
        $priorProjectHash = [string](Get-Field (Get-Field $prior 'project') 'treeSha256')
        if ([string]::IsNullOrWhiteSpace($priorProjectHash)) { $priorProjectHash = [string](Get-Field $prior 'outputTreeSha256') }
        if ($CurrentMode -ne 'flat' -and -not [string]::IsNullOrWhiteSpace($priorProjectHash)) {
            $startingProjectHash = Get-TreeHash $StartPath
            Add-Check 'previous accepted project output matches current starting project' ($priorProjectHash -ceq $startingProjectHash) @{expected=$priorProjectHash;actual=$startingProjectHash}
        }
    } elseif ($expectedIndex -eq 1) {
        Add-Check 'first task has no prior acceptance' ($null -eq $previous)
    } elseif ($null -ne $previous) {
        Add-Check 'prior acceptance has a path and hash' $false
    } else {
        $checks.Add([ordered]@{name='optional prior acceptance absent';passed=$true;details='No starting-state record was supplied for this isolated control or task.'})
    }
    return [ordered]@{ path=$statePath; sha256=(Get-Sha256 $statePath); data=$state }
}

function Get-NominalTypeNames([object[]]$WordRows) {
    $builtins = @('Bool','Int','Float','String','Unit','List','Option','Result')
    $found = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($row in $WordRows) {
        foreach ($signature in @($row.inputs) + @($row.outputs)) {
            foreach ($match in [regex]::Matches([string]$signature, '[A-Z][A-Za-z0-9_]*')) {
                if ($match.Value -cnotin $builtins) { [void]$found.Add($match.Value) }
            }
        }
    }
    return @($found | Sort-Object)
}

function Invoke-SourceRequests([string]$Project, [string]$Selector, [string[]]$Names, [string]$Label) {
    $map = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    for ($offset = 0; $offset -lt $Names.Count; $offset += 90) {
        $end = [Math]::Min($offset + 90, $Names.Count)
        $requests = [Collections.Generic.List[object]]::new()
        for ($index = $offset; $index -lt $end; $index++) {
            $request = [ordered]@{ op='source' }
            $request[$Selector] = $Names[$index]
            $requests.Add($request)
        }
        if ($requests.Count -eq 0) { continue }
        $responses = Invoke-JsonlSession $Project @($requests) "$Label source $($offset + 1)-$end"
        Assert-ResponsesOk $responses $Label
        for ($index = 0; $index -lt $responses.Count; $index++) {
            $map[$Names[$offset + $index]] = [string]$responses[$index].data
        }
    }
    return $map
}

function Check-LanguagePreservation([string]$StartProject, [string]$ActorProject, [string]$TargetName, [string]$CurrentTask) {
    $startWordsResponse = Invoke-JsonlSession $StartProject @([ordered]@{op='words'}) 'starting words inventory'
    $actorWordsResponse = Invoke-JsonlSession $ActorProject @([ordered]@{op='words'}) 'actor words inventory'
    Assert-ResponsesOk $startWordsResponse 'starting words inventory'
    Assert-ResponsesOk $actorWordsResponse 'actor words inventory'
    $startRows = @($startWordsResponse[0].data.words)
    $actorRows = @($actorWordsResponse[0].data.words)
    $startUserRows = @($startRows | Where-Object { ([string]$_.id).StartsWith('word_',[StringComparison]::Ordinal) })
    $actorRowsByName = @{}
    foreach ($row in $actorRows) { $actorRowsByName[[string]$row.name] = $row }
    Add-Check 'target exists in actor project' ($actorRowsByName.ContainsKey($TargetName)) $TargetName
    Add-Check 'target is an authored user word' (([string]$actorRowsByName[$TargetName].id).StartsWith('word_',[StringComparison]::Ordinal)) $TargetName

    $startWordNames = @($startUserRows | ForEach-Object { [string]$_.name } | Sort-Object -Unique)
    $startTypeNames = Get-NominalTypeNames $startRows
    $startWordSources = Invoke-SourceRequests $StartProject 'word' $startWordNames 'starting project'
    $actorWordSources = Invoke-SourceRequests $ActorProject 'word' $startWordNames 'actor project'
    $startTypeSources = Invoke-SourceRequests $StartProject 'type' $startTypeNames 'starting project'
    $actorTypeSources = Invoke-SourceRequests $ActorProject 'type' $startTypeNames 'actor project'

    $wordIdentityOk = $true
    $changedWords = [Collections.Generic.List[string]]::new()
    foreach ($startRow in $startUserRows) {
        $name = [string]$startRow.name
        if (-not $actorRowsByName.ContainsKey($name)) { $wordIdentityOk = $false; $changedWords.Add("missing:$name"); continue }
        $actorRow = $actorRowsByName[$name]
        $same = ([string]$actorRow.id -ceq [string]$startRow.id) -and
            (@($actorRow.inputs) -join "`0") -ceq (@($startRow.inputs) -join "`0") -and
            (@($actorRow.outputs) -join "`0") -ceq (@($startRow.outputs) -join "`0") -and
            ([string]$actorRow.status -ceq [string]$startRow.status) -and
            ([string]$actorRow.maturity -ceq [string]$startRow.maturity) -and
            ($actorWordSources[$name] -ceq $startWordSources[$name])
        if (-not $same) { $wordIdentityOk = $false; $changedWords.Add($name) }
    }
    Add-Check 'all starting user words retain identity, signature, status, and source' $wordIdentityOk @($changedWords)
    $typeSourcesOk = $true
    $changedTypes = [Collections.Generic.List[string]]::new()
    foreach ($name in $startTypeNames) {
        if (-not $actorTypeSources.ContainsKey($name) -or $actorTypeSources[$name] -cne $startTypeSources[$name]) {
            $typeSourcesOk = $false; $changedTypes.Add($name)
        }
    }
    Add-Check 'all starting nominal type sources are preserved' $typeSourcesOk @($changedTypes)

    $preservation.checked = $true
    $preservation.sourceWords = $startUserRows.Count
    $preservation.nominalTypes = $startTypeNames.Count
    $preservation.unchanged = $wordIdentityOk -and $typeSourcesOk
    $preservation.startingWords = $startWordNames
    $preservation.startingTypes = $startTypeNames
    $preservation.addedUserWords = @($actorRows | Where-Object { ([string]$_.id).StartsWith('word_',[StringComparison]::Ordinal) -and $_.name -notin $startWordNames } | ForEach-Object { [string]$_.name } | Sort-Object)
    $preservation.addedNominalTypes = @(Get-NominalTypeNames $actorRows | Where-Object { $_ -notin $startTypeNames })
    return [ordered]@{ words=$actorRows; target=$actorRowsByName[$TargetName]; startWords=$startUserRows; startTypes=$startTypeNames }
}

function Get-StructuredValue($Data) {
    $root = Get-Field $Data 'structuredStack'
    $values = Get-Field $root 'values'
    if ($null -eq $values -or @($values).Count -ne 1) { throw 'Structured eval must return exactly one value.' }
    return @($values)[0]
}

function Assert-LanguageCase($Response, $Expected, [string]$CaseId) {
    if (-not [bool]$Response.ok) { throw "Eval case '$CaseId' failed: $($Response.message)" }
    $data = $Response.data
    Add-Check "$CaseId exact language output type" (@($data.stackTypes).Count -eq 1 -and [string]$data.stackTypes[0] -ceq [string]$Expected.type) ([string]$data.stackTypes -join ',')
    $effects = Get-Field $data 'effects'
    $console = @(Get-Field $data 'console')
    $effectsCount = if ($effects -is [Collections.IDictionary]) { $effects.Count } elseif ($null -eq $effects) { 0 } else { @($effects.PSObject.Properties).Count }
    Add-Check "$CaseId has no effects or console output" ($effectsCount -eq 0 -and $console.Count -eq 0)
    $value = Get-StructuredValue $data
    switch ([string]$Expected.type) {
        'Bool' {
            Add-Check "$CaseId structured Bool" ([string]$value.kind -ceq 'bool' -and $value.value -is [bool] -and [bool]$value.value -eq [bool]$Expected.value)
        }
        'Int' {
            Add-Check "$CaseId structured Int" ([string]$value.kind -ceq 'int' -and [string]$value.value -ceq [string]$Expected.value)
        }
        'Money' {
            $payload = Get-Field $value 'value'
            $baseType = Get-Field $value 'baseType'
            Add-Check "$CaseId structured nominal Money" ([string]$value.kind -ceq 'scalar' -and [string]$value.name -ceq 'Money' -and [string]$baseType.kind -ceq 'int' -and [string]$payload.kind -ceq 'int' -and [string]$payload.value -ceq [string]$Expected.value)
        }
        default { throw "Unsupported expected type '$($Expected.type)'." }
    }
}

function Get-ConventionalFunctionBlock([string]$Source, [string]$FunctionName) {
    $lines = $Source -split "`r?`n"
    $start = -1
    for ($index = 0; $index -lt $lines.Count; $index++) {
        if ($lines[$index] -match ('^\s{4}let\s+' + [regex]::Escape($FunctionName) + '\b')) { $start = $index; break }
    }
    if ($start -lt 0) { return '' }
    $end = $lines.Count
    for ($index = $start + 1; $index -lt $lines.Count; $index++) {
        if ($lines[$index] -match '^\s{4}let\s+') { $end = $index; break }
    }
    return ($lines[$start..($end - 1)] -join "`n")
}

function Verify-ConventionalMode($Task, $Cases, [string]$Scratch, [string]$StartPath) {
    $starter = Join-Path $repo 'experiments/AgentLang.SubagentTrials/business-policy-001/conventional'
    $requiredFixed = @('BusinessPolicy.fsproj','Domain.fs','Program.fs','lib/AgentLang.Business.dll')
    if ($null -ne $StartPath) {
        $startInventory = Get-RelativeInventory $StartPath
        $actorInventory = Get-RelativeInventory $resolvedProject
        $allowedMutable = @('Operations.fs','SelfTests.fs')
        $startSet = @($startInventory | ForEach-Object { $_.path })
        $actorSet = @($actorInventory | ForEach-Object { $_.path })
        $samePaths = (@($startSet | Where-Object { $_ -notin $actorSet }).Count -eq 0) -and (@($actorSet | Where-Object { $_ -notin $startSet -and $_ -notin $allowedMutable }).Count -eq 0)
        Add-Check 'conventional project file set preserved outside allowed files' $samePaths
        $fixedOk = $true
        foreach ($relative in $requiredFixed) {
            $startFile = Join-Path $StartPath $relative
            $actorFile = Join-Path $resolvedProject $relative
            $same = (Test-Path -LiteralPath $startFile -PathType Leaf) -and (Test-Path -LiteralPath $actorFile -PathType Leaf) -and (Get-Sha256 $startFile) -ceq (Get-Sha256 $actorFile)
            if (-not $same) { $fixedOk = $false }
        }
        Add-Check 'conventional fixed sources and Business DLL are unchanged' $fixedOk $requiredFixed
    } else {
        $checks.Add([ordered]@{name='conventional starting snapshot preservation';passed=$true;details='Skipped because StartingProjectPath was not supplied.'})
    }

    $operationsPath = Join-Path $resolvedProject 'Operations.fs'
    $selfTestsPath = Join-Path $resolvedProject 'SelfTests.fs'
    $operations = Get-Content -LiteralPath $operationsPath -Raw
    $selfTests = Get-Content -LiteralPath $selfTestsPath -Raw
    $symbol = [string]$Task.conventionalSymbol
    $functionName = $symbol.Substring($symbol.LastIndexOf('.') + 1)
    $block = Get-ConventionalFunctionBlock $operations $functionName
    $taskType = [string]$Task.outputs[0]
    $fsharpType = switch ($taskType) { 'Bool' { 'bool' } 'Int' { 'int64' } 'Money' { 'Domain\.Money' } default { throw "Unsupported conventional type '$taskType'." } }
    $declarationRegex = '(?m)^\s*let\s+' + [regex]::Escape($functionName) + '\s+\([^\r\n]*\)\s*:\s*' + $fsharpType + '\s*='
    Add-Check 'conventional target has the typed public signature' ([regex]::IsMatch($operations,$declarationRegex)) $symbol
    $docRegex = '(?ms)^\s*///\s*[^\r\n]{8,}(?:\r?\n\s*///[^\r\n]*)*\r?\n\s*let\s+' + [regex]::Escape($functionName) + '\b'
    Add-Check 'conventional target has meaningful source documentation' ([regex]::IsMatch($operations,$docRegex)) $symbol
    Add-Check 'conventional target implementation is not a stub' (-not [string]::IsNullOrWhiteSpace($block) -and $block -notmatch '(?i)failwith\s+"Not implemented|TODO|IMPLEMENT ME')

    $ownBlock = Get-ConventionalFunctionBlock $selfTests 'runOwnTests'
    $exampleBlock = Get-ConventionalFunctionBlock $selfTests 'runExamples'
    Add-Check 'conventional own tests exercise target policy' ($ownBlock -match [regex]::Escape("CustomerPolicies.$functionName") -and $ownBlock -notmatch '^\s*let\s+runOwnTests\s*\(\)\s*=\s*\(\)\s*$')
    Add-Check 'conventional examples demonstrate target policy' ($exampleBlock -match [regex]::Escape("CustomerPolicies.$functionName") -and $exampleBlock -notmatch '^\s*let\s+runExamples\s*\(\)\s*=\s*\(\)\s*$')
    Add-Check 'conventional program runs seed, own tests, and examples' ((Get-Content -LiteralPath (Join-Path $resolvedProject 'Program.fs') -Raw) -match 'SelfTests\.runSeedTests\s*\(\)' -and (Get-Content -LiteralPath (Join-Path $resolvedProject 'Program.fs') -Raw) -match 'SelfTests\.runOwnTests\s*\(\)' -and (Get-Content -LiteralPath (Join-Path $resolvedProject 'Program.fs') -Raw) -match 'SelfTests\.runExamples\s*\(\)')

    $scratchProject = Join-Path $Scratch 'project'
    Copy-ProjectToScratch $resolvedProject $scratchProject
    $projectFile = Join-Path $scratchProject 'BusinessPolicy.fsproj'
    $build = Invoke-CapturedProcess 'dotnet' @('build',$projectFile,'--configuration','Release','--nologo') $scratchProject 300
    Add-Check 'conventional scratch project builds' (-not $build.timedOut -and [int]$build.exitCode -eq 0) @{stdout=$build.stdout;stderr=$build.stderr}
    $assembly = Join-Path $scratchProject 'bin/Release/net9.0/BusinessPolicy.dll'
    Add-Check 'conventional probe assembly exists' (Test-Path -LiteralPath $assembly -PathType Leaf)
    $selfTestRun = Invoke-CapturedProcess 'dotnet' @($assembly) $scratchProject 90
    Add-Check 'conventional seed, own tests, and examples execute' (-not $selfTestRun.timedOut -and [int]$selfTestRun.exitCode -eq 0 -and $selfTestRun.stdout -match 'Seed checks passed:') @{stdout=$selfTestRun.stdout;stderr=$selfTestRun.stderr}

    $casesPath = Join-Path $scratchProject 'business-policy-cases.json'
    $probeCases = @($Cases | ForEach-Object { [ordered]@{id=[string]$_.id;kind=[string]$_.kind;balanceMinor=[string]$_.balanceMinor} })
    [IO.File]::WriteAllText($casesPath,(ConvertTo-Json -InputObject $probeCases -Depth 20),$utf8NoBom)
    $probe = Invoke-CapturedProcess 'dotnet' @($assembly,'--probe',[string]$Task.id,$casesPath) $scratchProject 120
    Add-Check 'conventional typed probe exits successfully' (-not $probe.timedOut -and [int]$probe.exitCode -eq 0) @{stdout=$probe.stdout;stderr=$probe.stderr}
    $results = ConvertFrom-Json -InputObject $probe.stdout -AsHashtable -Depth 50
    Add-Check 'conventional probe returned one row per case' (@($results).Count -eq $Cases.Count)
    $byId = @{}
    foreach ($result in @($results)) { $byId[[string]$result.id] = $result }
    for ($index=0; $index -lt $Cases.Count; $index++) {
        $case = $Cases[$index]
        $expected = Get-ExpectedForCase $Task $case
        Add-Check "$($case.id) conventional case present" ($byId.ContainsKey([string]$case.id))
        $result = $byId[[string]$case.id]
        $shape = [string]$result.resultType -ceq [string]$expected.type
        if ($expected.type -ceq 'Bool') { $shape = $shape -and ($result.value -is [bool]) -and ([bool]$result.value -eq [bool]$expected.value) }
        else { $shape = $shape -and ($result.value -is [string]) -and ([string]$result.value -ceq [string]$expected.value) }
        Add-Check "$($case.id) conventional exact typed oracle result" $shape @{expected=$expected;actual=$result}
    }
    $runtimeInfo.conventionalAssemblySha256 = Get-Sha256 $assembly
    $runtimeInfo.conventionalBuildOutput = $build.stdout
    $runtimeInfo.conventionalSelfTestOutput = $selfTestRun.stdout
    $runtimeInfo.conventionalProbeOutput = $probe.stdout
}

try {
    if ([string]::IsNullOrWhiteSpace($Mode)) { $Mode = 'growing' }
    if ([string]::IsNullOrWhiteSpace($TaskId)) { $TaskId = 'S01' }
    if ([string]::IsNullOrWhiteSpace($EvidencePath)) { $EvidencePath = "reports/evidence/business-policy-$($Mode.ToLowerInvariant())-$($TaskId.ToLowerInvariant()).json" }
    $Mode = $Mode.ToLowerInvariant()
    $preservation.mode = $Mode
    Add-Check 'mode is supported' ($Mode -cin @('growing','flat','conventional')) $Mode
    Add-Check 'task id is supported' ($TaskId -cin $sequence) $TaskId
    Add-Check 'acceptance corpus exists' (Test-Path -LiteralPath $oraclePath -PathType Leaf) $oraclePath
    $oracleHash = Get-Sha256 $oraclePath
    $oracleJson = Get-Content -LiteralPath $oraclePath -Raw
    $oracleJsonParameters = @{ InputObject=$oracleJson; AsHashtable=$true; Depth=100 }
    if ((Get-Command ConvertFrom-Json).Parameters.ContainsKey('DateKind')) {
        $oracleJsonParameters.DateKind = 'String'
    }
    $oracle = ConvertFrom-Json @oracleJsonParameters
    # Older PowerShell versions may coerce ISO-looking JSON strings to DateTime.
    # Restore the acceptance Instant from the original JSON token so the oracle
    # and generated typed input always use its exact canonical UTC text.
    $oracleDocument = [System.Text.Json.JsonDocument]::Parse($oracleJson)
    try {
        $createdAtElement = $oracleDocument.RootElement.GetProperty('defaults').GetProperty('createdAt')
        Add-Check 'acceptance default Instant is a JSON string' ($createdAtElement.ValueKind -eq [System.Text.Json.JsonValueKind]::String)
        $oracle.defaults.createdAt = $createdAtElement.GetString()
    } finally {
        $oracleDocument.Dispose()
    }
    $oracleCreatedAt = [string]$oracle.defaults.createdAt
    Add-Check 'acceptance corpus schema and sequence match' ([int]$oracle.schemaVersion -eq 1 -and [string]$oracle.studyId -ceq 'business-policy-001' -and (@($oracle.sequence) -join ',') -ceq ($sequence -join ','))
    $targetOracle = Get-TargetTask $oracle $TaskId
    Add-Check 'acceptance task signature matches policy contract' ((@($targetOracle.inputs) -join ',') -ceq 'Customer' -and @($targetOracle.outputs).Count -eq 1 -and [string]$targetOracle.outputs[0] -cin @('Bool','Int','Money') -and -not [string]::IsNullOrWhiteSpace([string]$targetOracle.symbol)) $targetOracle.symbol
    $cases = @($targetOracle.cases)
    Add-Check 'acceptance case count matches policy corpus' ($cases.Count -eq $expectedCounts[$TaskId]) @{expected=$expectedCounts[$TaskId];actual=$cases.Count}
    $caseIds = @($cases | ForEach-Object { [string]$_.id })
    Add-Check 'acceptance case ids are nonempty and unique' ($caseIds.Count -gt 0 -and @($caseIds | Sort-Object -Unique).Count -eq $caseIds.Count -and @($caseIds | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -eq 0)
    $independentCases = [Collections.Generic.List[object]]::new()
    $caseOracleRows = [Collections.Generic.List[object]]::new()
    foreach ($case in $cases) {
        $expected = Get-ExpectedForCase $targetOracle $case
        $independentCases.Add($expected)
        $caseOracleRows.Add([ordered]@{id=[string]$case.id;kind=[string]$case.kind;balanceMinor=[string]$case.balanceMinor;expected=[ordered]@{type=$expected.type;value=$expected.value}})
    }
    $caseOracleEvidence = @($caseOracleRows)
    Add-Check 'all acceptance expected values match independent BigInteger oracle' ($independentCases.Count -eq $cases.Count)

    $originalProjectPath = Resolve-RepoPath $ProjectPath
    $resolvedProject = $originalProjectPath
    Add-Check 'actor project directory exists' (Test-Path -LiteralPath $resolvedProject -PathType Container) $resolvedProject
    $evidenceTarget = Resolve-RepoPath $EvidencePath
    $evidenceOutsideActor = -not (Test-Within $evidenceTarget $resolvedProject)
    $evidenceOutsideStart = $true
    if ($Mode -ne 'conventional') {
        $resolvedCli = Resolve-RepoPath $CliDll
        Add-Check 'pinned AgentLang CLI exists' (Test-Path -LiteralPath $resolvedCli -PathType Leaf) $resolvedCli
        $runtimeInfo.cliDll = $resolvedCli
        $runtimeInfo.cliSha256 = Get-Sha256 $resolvedCli
    }
    if (-not [string]::IsNullOrWhiteSpace($StartingProjectPath)) {
        $originalStartingProjectPath = Resolve-RepoPath $StartingProjectPath
        $resolvedStartingProject = $originalStartingProjectPath
        Add-Check 'immutable starting project exists' (Test-Path -LiteralPath $resolvedStartingProject -PathType Container) $resolvedStartingProject
        $preservation.startingProjectPath = $resolvedStartingProject
        $preservation.startingProjectFiles = Get-RelativeInventory $resolvedStartingProject
        $evidenceOutsideStart = -not (Test-Within $evidenceTarget $resolvedStartingProject)
    } else {
        $checks.Add([ordered]@{name='starting project preservation';passed=$true;details='Skipped because StartingProjectPath was not supplied.'})
    }
    Add-Check 'evidence file is outside actor and starting projects' ($evidenceOutsideActor -and $evidenceOutsideStart) $evidenceTarget
    $actorInventoryBefore = Get-RelativeInventory $resolvedProject
    $actorHashBefore = Get-TreeHash $resolvedProject
    if ($null -ne $resolvedStartingProject) {
        $startingStateMetadata = Validate-StartingState $resolvedStartingProject $resolvedProject $oracle $Mode $TaskId
        if ($Mode -ne 'conventional' -and $null -ne $startingStateMetadata) {
            $pinnedCliHash = [string](Get-Field (Get-Field $startingStateMetadata.data 'runtime') 'cliSha256')
            Add-Check 'provided CLI matches starting-state pin' ($runtimeInfo.cliSha256 -ceq $pinnedCliHash.ToLowerInvariant()) @{provided=$runtimeInfo.cliSha256;pinned=$pinnedCliHash}
        }
        $frozenPinRecord = Validate-FrozenPin $resolvedStartingProject $resolvedProject $resolvedCli $Mode $TaskId $oracle
    } else {
        $startingStateMetadata = $null
        if ($RequireFrozenPin) { Add-Check 'required frozen actor verification has a starting project' $false }
        $frozenPinInfo.status = 'unfrozen-preflight-or-control-without-starting-project'
        $checks.Add([ordered]@{name='unfrozen verifier run classified as preflight/control';passed=$true;details='No StartingProjectPath was supplied.'})
    }

    [IO.Directory]::CreateDirectory($testsRoot) | Out-Null
    $scratchProject = Join-Path $testsRoot ('verify-' + $Mode + '-' + $TaskId + '-' + [Guid]::NewGuid().ToString('N'))
    Add-Check 'scratch project path is inside verifier tests root' (Test-Within $scratchProject $testsRoot) $scratchProject

    if ($Mode -eq 'conventional') {
        $taskIndex = [array]::IndexOf($sequence,$TaskId)
        if ($taskIndex -gt 0) { $reuse.candidatePriorTaskSymbols = @($sequence[0..($taskIndex - 1)] | ForEach-Object { [string](Get-TargetTask $oracle $_).conventionalSymbol }) }
        $reuse.priorTaskSymbols = $null
        $reuse.startingDictionaryTaskSymbols = $null
        $reuse.reusedPriorTaskSymbols = $null
        $reuse.status = 'unavailable for conventional F#; task functions are not exposed through a runtime word dictionary'
        Verify-ConventionalMode $targetOracle $cases $scratchProject $resolvedStartingProject
    } else {
        Copy-ProjectToScratch $resolvedProject $scratchProject
        $scratchInventory = Get-RelativeInventory $scratchProject
        Add-Check 'language scratch copy contains source project' ($scratchInventory.Count -gt 0)
        $preserved = $null
        if ($null -ne $resolvedStartingProject) { $preserved = Check-LanguagePreservation $resolvedStartingProject $scratchProject ([string]$targetOracle.symbol) $TaskId }
        else {
            $inventory = Invoke-JsonlSession $scratchProject @([ordered]@{op='words'}) 'actor words inventory'
            Assert-ResponsesOk $inventory 'actor words inventory'
            $rows = @($inventory[0].data.words)
            $byName = @{}; foreach ($row in $rows) { $byName[[string]$row.name]=$row }
            Add-Check 'target exists in actor project' ($byName.ContainsKey([string]$targetOracle.symbol)) $targetOracle.symbol
            $preserved = [ordered]@{words=$rows;target=$byName[[string]$targetOracle.symbol]}
        }
        $targetRow = $preserved.target
        Add-Check 'target output matches acceptance signature' ((@($targetRow.inputs) -join ',') -ceq 'Customer' -and (@($targetRow.outputs) -join ',') -ceq ([string]$targetOracle.outputs[0])) @{inputs=@($targetRow.inputs);outputs=@($targetRow.outputs)}
        Add-Check 'target is a persistent library word' ([string]$targetRow.status -ceq 'persistent' -and [string]$targetRow.maturity -ceq 'library' -and [string]$targetRow.id -like 'word_*') @{status=$targetRow.status;maturity=$targetRow.maturity;id=$targetRow.id}

        $targetRequests = @(
            [ordered]@{op='test-all'},
            [ordered]@{op='describe';word=[string]$targetOracle.symbol},
            [ordered]@{op='dependencies';word=[string]$targetOracle.symbol},
            [ordered]@{op='transitive-dependencies';word=[string]$targetOracle.symbol},
            [ordered]@{op='source';word=[string]$targetOracle.symbol},
            [ordered]@{op='examples';word=[string]$targetOracle.symbol}
        )
        $targetResponses = Invoke-JsonlSession $scratchProject $targetRequests 'target library and attached cases'
        Assert-ResponsesOk $targetResponses 'target library and attached cases'
        $testRows = @($targetResponses[0].data.results)
        $describe = $targetResponses[1].data
        $directDeps = @($targetResponses[2].data.dependencies)
        $transitiveData = $targetResponses[3].data
        $transitiveDeps = if ($null -ne $transitiveData.dependencies) { @($transitiveData.dependencies) } else { @($describe.transitiveDependencies) }
        $targetSource = [string]$targetResponses[4].data
        $exampleNames = @($targetResponses[5].data)
        Add-Check 'all attached tests pass' ($testRows.Count -gt 0 -and @($testRows | Where-Object { -not [bool]$_.passed }).Count -eq 0) @{count=$testRows.Count;failed=@($testRows | Where-Object { -not [bool]$_.passed })}
        Add-Check 'target owns passing tests' ([int]$describe.testCount -gt 0 -and @($testRows | Where-Object { [string]$_.word -ceq [string]$targetOracle.symbol -and [bool]$_.passed }).Count -gt 0) $describe.testCount
        Add-Check 'target has meaningful documentation' ([string]$describe.documentation -match '\S.{7,}' -and $targetSource -match '(?m)^\s*doc\s+".{8,}"') ([string]$describe.documentation)
        Add-Check 'target has attached examples' ([int]$describe.exampleCount -gt 0 -and $exampleNames.Count -gt 0)
        Add-Check 'target remains pure, supported, and nondeprecated' (@($describe.effects).Count -eq 0 -and [string]$describe.status -ceq 'persistent' -and [string]$describe.maturity -ceq 'library' -and -not [bool]$describe.deprecated -and [string]$describe.kind -ceq 'word') @{effects=@($describe.effects);status=$describe.status;maturity=$describe.maturity;deprecated=$describe.deprecated;kind=$describe.kind}
        $coverage = $describe.coverage
        Add-Check 'current complete own instruction and branch coverage' ([string]$coverage.status -ceq 'current' -and [int]$describe.testCount -gt 0 -and [int]$coverage.instructionsTotal -gt 0 -and [int]$coverage.instructionsCovered -eq [int]$coverage.instructionsTotal -and [int]$coverage.branchesCovered -eq [int]$coverage.branchesTotal) $coverage

        $exampleResponses = Invoke-JsonlSession $scratchProject @([ordered]@{op='example';word=[string]$targetOracle.symbol}) 'run attached examples'
        Assert-ResponsesOk $exampleResponses 'run attached examples'
        $exampleResults = @($exampleResponses[0].data.results)
        Add-Check 'all attached examples pass with meaningful target calls' ($exampleResults.Count -eq $exampleNames.Count -and $exampleResults.Count -gt 0 -and @($exampleResults | Where-Object { -not [bool]$_.passed -or [string]$_.source -notmatch [regex]::Escape(([string]$targetOracle.symbol -replace '\.','::')) -or [string]$_.source -notmatch '=>'}).Count -eq 0) @{count=$exampleResults.Count;results=$exampleResults}

        # Re-run tests and describe in one fresh engine so current coverage cannot be stale.
        $coverageResponses = Invoke-JsonlSession $scratchProject @([ordered]@{op='test-all'},[ordered]@{op='describe';word=[string]$targetOracle.symbol}) 'refresh target coverage'
        Assert-ResponsesOk $coverageResponses 'refresh target coverage'
        $freshTestRows = @($coverageResponses[0].data.results)
        $freshDescribe = $coverageResponses[1].data
        $freshCoverage = $freshDescribe.coverage
        Add-Check 'refreshed tests all pass' ($freshTestRows.Count -gt 0 -and @($freshTestRows | Where-Object { -not [bool]$_.passed }).Count -eq 0)
        Add-Check 'refreshed own coverage remains complete' ([string]$freshCoverage.status -ceq 'current' -and [int]$freshCoverage.instructionsTotal -gt 0 -and [int]$freshCoverage.instructionsCovered -eq [int]$freshCoverage.instructionsTotal -and [int]$freshCoverage.branchesCovered -eq [int]$freshCoverage.branchesTotal) $freshCoverage

        $taskIndex = [array]::IndexOf($sequence,$TaskId)
        if ($taskIndex -gt 0) { $reuse.candidatePriorTaskSymbols = @($sequence[0..($taskIndex - 1)] | ForEach-Object { [string](Get-TargetTask $oracle $_).symbol }) }
        $reuse.directDependencies = $directDeps
        $reuse.transitiveDependencies = $transitiveDeps
        if ($null -ne $preserved) {
            $startNames = @($preserved.startWords | ForEach-Object { [string]$_.name })
            $reuse.startingDictionaryTaskSymbols = @($reuse.candidatePriorTaskSymbols | Where-Object { $_ -cin $startNames })
            $hasAcceptedPriorState = $false
            if ($null -ne $startingStateMetadata) {
                $previousState = Get-Field $startingStateMetadata.data 'previousAcceptance'
                $previousPath = [string](Get-Field $previousState 'acceptancePath')
                $hasAcceptedPriorState = $null -ne $previousState -and -not [string]::IsNullOrWhiteSpace($previousPath)
            }
            if ($Mode -ceq 'growing' -and $hasAcceptedPriorState) {
                $reuse.priorTaskSymbols = $reuse.startingDictionaryTaskSymbols
                $reuse.reusedPriorTaskSymbols = @($reuse.priorTaskSymbols | Where-Object { $_ -cin $directDeps -or $_ -cin $transitiveDeps })
                $reuse.status = 'measured against the immutable starting dictionary after a passing predecessor gate'
            } else {
                $reuse.priorTaskSymbols = @()
                $reuse.reusedPriorTaskSymbols = @()
                $reuse.status = 'no accepted prior policy helpers available in this mode/start state'
            }
        } else {
            $reuse.priorTaskSymbols = $null
            $reuse.startingDictionaryTaskSymbols = $null
            $reuse.reusedPriorTaskSymbols = $null
            $reuse.status = 'unavailable because no immutable starting dictionary was supplied'
        }

        for ($offset=0; $offset -lt $cases.Count; $offset += 45) {
            $end = [Math]::Min($offset + 45, $cases.Count)
            $evalRequests = [Collections.Generic.List[object]]::new()
            for ($index=$offset; $index -lt $end; $index++) {
                $case = $cases[$index]
                $expected = $independentCases[$index]
                $id = ConvertTo-Json -InputObject ([string]$oracle.defaults.customerId) -Compress
                $email = ConvertTo-Json -InputObject ([string]$oracle.defaults.email) -Compress
                $kind = ConvertTo-Json -InputObject ([string]$case.kind) -Compress
                $created = [DateTimeOffset]::ParseExact($oracleCreatedAt,'O',[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind)
                Add-Check 'default instant is canonical UTC round-trip text' ($created.Offset -eq [TimeSpan]::Zero -and $created.ToString('O',[Globalization.CultureInfo]::InvariantCulture) -ceq $oracleCreatedAt)
                $balance = [string]$expected.balanceMinor
                $customer = "customer::new(id = CustomerId::new($id), email = Email::new($email), kind = $kind, balance = Money::new($balance), created-at = Instant::new(`"$oracleCreatedAt`"))"
                $reference = ([string]$targetOracle.symbol) -replace '^([^.]+)\.', '$1::'
                $evalRequests.Add([ordered]@{op='eval';frontend='flow';structured=$true;code="$reference($customer)"})
            }
            $evalResponses = Invoke-JsonlSession $scratchProject @($evalRequests) "policy oracle cases $($offset + 1)-$end"
            for ($local=0; $local -lt $evalResponses.Count; $local++) {
                $absoluteIndex = $offset + $local
                Assert-LanguageCase $evalResponses[$local] $independentCases[$absoluteIndex] ([string]$cases[$absoluteIndex].id)
            }
        }
    }

    $actorInventoryAfter = Get-RelativeInventory $resolvedProject
    $actorHashAfter = Get-TreeHash $resolvedProject
    $actorInventoryBeforeJson = ConvertTo-Json -InputObject @($actorInventoryBefore) -Depth 20 -Compress
    $actorInventoryAfterJson = ConvertTo-Json -InputObject @($actorInventoryAfter) -Depth 20 -Compress
    Add-Check 'actor project remains unchanged by verification' ($actorHashBefore -ceq $actorHashAfter -and $actorInventoryBeforeJson -ceq $actorInventoryAfterJson) @{before=$actorHashBefore;after=$actorHashAfter}
    $passed = $true
} catch {
    $failure = $_.Exception.Message
} finally {
    if ($null -ne $resolvedProject -and (Test-Path -LiteralPath $resolvedProject -PathType Container)) {
        try { $actorInventoryAfter = Get-RelativeInventory $resolvedProject } catch { }
    }
    if ($null -ne $scratchProject -and (Test-Path -LiteralPath $scratchProject -PathType Container)) {
        $safe = $false
        try { $safe = Test-Within $scratchProject $testsRoot } catch { }
        if ($safe -and [IO.Path]::GetFileName($scratchProject).StartsWith('verify-',[StringComparison]::Ordinal)) {
            try { Remove-Item -LiteralPath $scratchProject -Recurse -Force } catch { if ($null -eq $failure) { $failure = "Scratch cleanup failed: $($_.Exception.Message)"; $passed = $false } }
        } elseif ($null -eq $failure) { $failure = 'Scratch cleanup refused an unverified path.'; $passed = $false }
    }
    if ($null -eq $evidenceTarget) {
        try { $evidenceTarget = Resolve-RepoPath $EvidencePath } catch { $evidenceTarget = Join-Path $repo "reports/evidence/business-policy-$($Mode.ToLowerInvariant())-$($TaskId.ToLowerInvariant()).json" }
    }
    try {
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($evidenceTarget)) | Out-Null
        $evidence = [ordered]@{
            schemaVersion=1; studyId='business-policy-001'; mode=$Mode; taskId=$TaskId
            passed=$passed; failure=$failure; sequenceIndex=$(if ($TaskId -in $sequence) { [array]::IndexOf($sequence,$TaskId)+1 } else { $null })
            resultKind=$(if ($checkFrozenPin) { 'frozen-actor-acceptance' } else { 'unfrozen-preflight-or-control' })
            checkFrozenPin=[bool]$checkFrozenPin; frozenPin=$frozenPinInfo; prelaunch=$frozenPinRecord
            startedUtc=$startedUtc; finishedUtc=[DateTime]::UtcNow.ToString('O')
            verifierSha256=$(if (Test-Path -LiteralPath $PSCommandPath -PathType Leaf) { Get-Sha256 $PSCommandPath } else { $null })
            oracle=[ordered]@{path=$oraclePath;sha256=$(if (Test-Path -LiteralPath $oraclePath -PathType Leaf) { Get-Sha256 $oraclePath } else { $null });symbol=$(if ($null -ne $targetOracle) { $targetOracle.symbol } else { $null });cases=$(if ($null -ne $targetOracle) { @($targetOracle.cases).Count } else { 0 });independentResults=$caseOracleEvidence}
            project=[ordered]@{argumentPath=$originalProjectPath;path=$resolvedProject;treeSha256=$actorHashAfter;filesBefore=$actorInventoryBefore;filesAfter=$actorInventoryAfter}
            outputTreeSha256=$actorHashAfter
            startingProject=[ordered]@{argumentPath=$originalStartingProjectPath;path=$resolvedStartingProject;state=$startingStateMetadata;preservation=$preservation}
            runtime=$runtimeInfo; reuse=$reuse; checks=$checks.ToArray(); jsonlSessions=$sessions.ToArray(); processRuns=$processRuns.ToArray()
            oracleProtocol='BigInteger integer oracle; raw Kind uses ordinal exact comparison; Money and Int payloads require canonical decimal strings.'
            limits=@('Behavioral acceptance and source preservation only; no model-efficiency or token claim.','Conventional checks use source-review signals, build, seed/self-test/example execution, and typed probe; they do not claim F# instruction coverage.','Conventional trial isolation is cooperative: validation builds and launches only a disposable copy.','Customer.Kind is intentionally raw in the public policy facade; no claim is made about private reference-constructor normalization.')
        }
        [IO.File]::WriteAllText($evidenceTarget,(ConvertTo-Json -InputObject $evidence -Depth 100) + "`n",$utf8NoBom)
    } catch {
        if ($null -eq $failure) { $failure = "Could not write verifier evidence: $($_.Exception.Message)" }
        $passed = $false
    }
}

if (-not $passed) { throw $failure }
Write-Output "Business policy $Mode/$TaskId passed $($checks.Count) checks. Evidence: $evidenceTarget"
