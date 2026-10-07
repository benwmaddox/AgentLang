#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$TracePath,
    [string]$AcceptancePath,
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$studyId = 'business-policy-retention-003'
$studyPath = 'experiments/AgentLang.SubagentTrials/business-policy-retention-003'
$trace = if ([IO.Path]::IsPathRooted($TracePath)) { [IO.Path]::GetFullPath($TracePath) } else { [IO.Path]::GetFullPath((Join-Path $repo $TracePath)) }
$run = Split-Path -Parent $trace
$pinPath = Join-Path $run 'prelaunch.json'
$statePath = Join-Path $run 'starting-state.json'
$promptPath = Join-Path $run 'prompt.txt'
$terminationAuditPath = Join-Path $repo 'scripts/Audit-SubagentTrialTerminationV2.ps1'
$checks = [Collections.Generic.List[object]]::new()

function Check([bool]$Passed, [string]$Name) {
    $checks.Add([pscustomobject][ordered]@{name=$Name;passed=$Passed})
    if (-not $Passed) { throw "Retention trace audit failed: $Name" }
}

function Get-FullPath([string]$Path) {
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $repo $Path))
}

function Get-RepoPath([string]$Path) {
    $relative = [IO.Path]::GetRelativePath($repo, [IO.Path]::GetFullPath($Path))
    if ($relative -eq '..' -or $relative.StartsWith('..' + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal) -or [IO.Path]::IsPathRooted($relative)) {
        throw "Path escapes the repository: $Path"
    }
    return $relative.Replace('\','/')
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-Sha256Bytes([byte[]]$Bytes) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
}

function Sort-OrdinalRows([object[]]$Rows, [string]$Primary = 'path', [string]$Secondary = '') {
    $sorted = [Collections.Generic.SortedDictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach ($row in $Rows) {
        $primaryValue = [string](Get-Property $row $Primary)
        if ($primaryValue.Contains("`n") -or $primaryValue.Contains("`r")) { throw 'Inventory path contains a line terminator.' }
        $key = if ([string]::IsNullOrEmpty($Secondary)) { $primaryValue } else { $primaryValue + [char]0 + [string](Get-Property $row $Secondary) }
        if ($sorted.ContainsKey($key)) { throw "Duplicate canonical inventory key: $key" }
        $sorted.Add($key,$row)
    }
    $result = [Collections.Generic.List[object]]::new()
    foreach ($row in $sorted.Values) { $result.Add($row) }
    return ,$result.ToArray()
}

function Sort-OrdinalStrings([string[]]$Values) {
    $result = [string[]]@($Values)
    [Array]::Sort($result,[StringComparer]::Ordinal)
    return ,$result
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

function Convert-JsonElement([Text.Json.JsonElement]$Element) {
    switch ($Element.ValueKind) {
        ([Text.Json.JsonValueKind]::Object) {
            $value = [ordered]@{}
            $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($property in $Element.EnumerateObject()) {
                if (-not $names.Add($property.Name)) { throw "Duplicate JSON property '$($property.Name)'." }
                $value[$property.Name] = Convert-JsonElement $property.Value
            }
            return ,$value
        }
        ([Text.Json.JsonValueKind]::Array) {
            $items = [Collections.Generic.List[object]]::new()
            foreach ($item in $Element.EnumerateArray()) { $items.Add((Convert-JsonElement $item)) }
            return ,$items.ToArray()
        }
        ([Text.Json.JsonValueKind]::String) { return $Element.GetString() }
        ([Text.Json.JsonValueKind]::Number) {
            $integer = 0L
            if ($Element.TryGetInt64([ref]$integer)) { return $integer }
            return $Element.GetDouble()
        }
        ([Text.Json.JsonValueKind]::True) { return $true }
        ([Text.Json.JsonValueKind]::False) { return $false }
        ([Text.Json.JsonValueKind]::Null) { return $null }
        default { throw "Unsupported JSON value kind $($Element.ValueKind)." }
    }
}

function Read-JsonText([string]$Text, [string]$Label) {
    $document = [Text.Json.JsonDocument]::Parse($Text)
    try { return ,(Convert-JsonElement $document.RootElement) }
    catch { throw "Invalid $Label JSON: $($_.Exception.Message)" }
    finally { $document.Dispose() }
}

function Read-Json([string]$Path) {
    return Read-JsonText ([Text.UTF8Encoding]::new($false, $true).GetString([IO.File]::ReadAllBytes($Path))) $Path
}

function Read-JsonLines([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191) { throw 'Trace has a UTF-8 BOM.' }
    $text = [Text.UTF8Encoding]::new($false, $true).GetString($bytes)
    if (-not $text.EndsWith("`n", [StringComparison]::Ordinal)) { throw 'Trace must end with an LF-delimited JSONL event.' }
    $lines = $text.Split("`n")
    $events = [Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt ($lines.Count - 1); $index++) {
        $line = $lines[$index]
        if ([string]::IsNullOrWhiteSpace($line)) { throw 'Trace contains an empty event line.' }
        if ($line.EndsWith("`r", [StringComparison]::Ordinal)) { throw 'Trace must use LF line endings without CR.' }
        $events.Add((Read-JsonText $line "trace event $($index + 1)"))
    }
    return ,$events.ToArray()
}

function Assert-NoReparseTree([string]$Path) {
    $root = Get-Item -LiteralPath $Path -Force
    if (($root.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point in audited input: $Path" }
    foreach ($item in Get-ChildItem -LiteralPath $Path -Force -Recurse) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point in audited input: $($item.FullName)" }
    }
}

function Get-ProjectInventory([string]$Path) {
    Assert-NoReparseTree $Path
    $rows = [Collections.Generic.List[object]]::new()
    foreach ($item in Get-ChildItem -LiteralPath $Path -File -Force -Recurse) {
        $relative = [IO.Path]::GetRelativePath($Path, $item.FullName).Replace('\','/')
        $segments = $relative.Split('/')
        if ($segments -contains 'bin' -or $segments -contains 'obj') { continue }
        $rows.Add([pscustomobject][ordered]@{path=$relative;bytes=[long]$item.Length;sha256=(Get-Sha256 $item.FullName)})
    }
    return Sort-OrdinalRows $rows.ToArray()
}

function Get-RuntimeInventory([string]$Runtime, [string]$Path) {
    Assert-NoReparseTree $Path
    $rows = [Collections.Generic.List[object]]::new()
    foreach ($item in Get-ChildItem -LiteralPath $Path -File -Force -Recurse) {
        $rows.Add([pscustomobject][ordered]@{runtime=$Runtime;path=[IO.Path]::GetRelativePath($Path,$item.FullName).Replace('\','/');bytes=[long]$item.Length;sha256=(Get-Sha256 $item.FullName)})
    }
    return Sort-OrdinalRows $rows.ToArray() 'runtime' 'path'
}

function Get-FullInventoryRows([object[]]$Inventory) {
    return Sort-OrdinalRows @($Inventory | ForEach-Object { [pscustomobject][ordered]@{path=$_.path;bytes=[long]$_.bytes;sha256=([string]$_.sha256).ToLowerInvariant()} })
}

function Get-InventoryHash([object[]]$Inventory) {
    $rows = Get-FullInventoryRows $Inventory
    return Get-Sha256Bytes ([Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $rows -Depth 100 -Compress)))
}

function Get-GitSnapshotBytes([string]$Revision, [string]$Path) {
    $spec = "$Revision`:$Path"
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'git'
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in @('-C',$repo,'show','--no-ext-diff',$spec)) { $startInfo.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { throw "Could not read Git snapshot $spec." }
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $memory = [IO.MemoryStream]::new()
    try {
        $process.StandardOutput.BaseStream.CopyTo($memory)
        $process.WaitForExit()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "Could not read Git snapshot $spec`: $stderr" }
        return ,$memory.ToArray()
    }
    finally { $memory.Dispose(); $process.Dispose() }
}

function Get-SourceArchiveMap {
    $root = "$studyPath/artifacts/source-snapshot"
    return @(
        [pscustomobject][ordered]@{path="$root/scripts/Prepare-RetentionTrial.ps1";sourcePath='scripts/Prepare-RetentionTrial.ps1'},
        [pscustomobject][ordered]@{path="$root/scripts/Freeze-RetentionTrial.ps1";sourcePath='scripts/Freeze-RetentionTrial.ps1'},
        [pscustomobject][ordered]@{path="$root/scripts/Verify-RetentionTrial.ps1";sourcePath='scripts/Verify-RetentionTrial.ps1'},
        [pscustomobject][ordered]@{path="$root/scripts/Verify-RetentionPreflight.ps1";sourcePath='scripts/Verify-RetentionPreflight.ps1'},
        [pscustomobject][ordered]@{path="$root/scripts/Audit-RetentionTrace.ps1";sourcePath='scripts/Audit-RetentionTrace.ps1'},
        [pscustomobject][ordered]@{path="$root/scripts/Prepare-BusinessPolicyTrial.ps1";sourcePath='scripts/Prepare-BusinessPolicyTrial.ps1'},
        [pscustomobject][ordered]@{path="$root/scripts/Start-SubagentTrialHostV2.ps1";sourcePath='scripts/Start-SubagentTrialHostV2.ps1'},
        [pscustomobject][ordered]@{path="$root/scripts/Audit-SubagentTrialTerminationV2.ps1";sourcePath='scripts/Audit-SubagentTrialTerminationV2.ps1'},
        [pscustomobject][ordered]@{path="$root/acceptance.json";sourcePath="$studyPath/acceptance.json"},
        [pscustomobject][ordered]@{path="$root/design.json";sourcePath="$studyPath/design.json"},
        [pscustomobject][ordered]@{path="$root/language-primer.md";sourcePath="$studyPath/language-primer.md"},
        [pscustomobject][ordered]@{path="$root/flat-customer.agent";sourcePath="$studyPath/artifacts/flat-customer.agent"},
        [pscustomobject][ordered]@{path="$root/acceptance-business-policy-001.json";sourcePath='experiments/AgentLang.SubagentTrials/business-policy-001/acceptance.json'},
        [pscustomobject][ordered]@{path="$root/acceptance-business-policy-help-002.json";sourcePath='experiments/AgentLang.SubagentTrials/business-policy-help-002/acceptance.json'},
        [pscustomobject][ordered]@{path="$root/language-primer-business-policy-help-002.md";sourcePath='experiments/AgentLang.SubagentTrials/business-policy-help-002/language-primer.md'},
        [pscustomobject][ordered]@{path="$root/public-task-S01.json";sourcePath='experiments/AgentLang.Benchmarks/task-bank/public/S01.json'},
        [pscustomobject][ordered]@{path="$root/public-task-S07.json";sourcePath='experiments/AgentLang.Benchmarks/task-bank/public/S07.json'},
        [pscustomobject][ordered]@{path="$root/examples/business-values.agent";sourcePath='examples/business-values.agent'},
        [pscustomobject][ordered]@{path="$root/examples/business-store.agent";sourcePath='examples/business-store.agent'},
        [pscustomobject][ordered]@{path="$root/examples/business-state.agent";sourcePath='examples/business-state.agent'},
        [pscustomobject][ordered]@{path="$root/examples/business-subscriptions.agent";sourcePath='examples/business-subscriptions.agent'},
        [pscustomobject][ordered]@{path="$root/examples/business-invoices.agent";sourcePath='examples/business-invoices.agent'},
        [pscustomobject][ordered]@{path="$root/examples/business-payments-email.agent";sourcePath='examples/business-payments-email.agent'}
    )
}

function Get-ExpectedRunId([string]$Arm, [string]$Block, [string]$TaskId) {
    $order = if ($Block -eq 'B1') { @('flat','retained','reset-rich') } elseif ($Block -eq 'B2') { @('reset-rich','flat','retained') } else { throw "Unknown block: $Block" }
    $armIndex = [array]::IndexOf($order,$Arm)
    $taskIndex = if ($TaskId -eq 'S01') { 0 } elseif ($TaskId -eq 'S07') { 1 } else { -1 }
    if ($armIndex -lt 0 -or $taskIndex -lt 0) { throw 'Invalid design cell identity.' }
    return ('R{0:D2}' -f ($(if ($Block -eq 'B1') { 0 } else { 6 }) + $armIndex * 2 + $taskIndex + 1))
}

function Assert-RunDesign([object]$Design, [object]$Pin) {
    Check ($Design.schemaVersion -eq 1 -and $Design.studyId -ceq $studyId -and ($Design.sequence -join '|') -ceq 'S01|S07') 'global design identity and task sequence'
    $expected = [Collections.Generic.List[string]]::new()
    foreach ($block in @('B1','B2')) {
        $arms = if ($block -eq 'B1') { @('flat','retained','reset-rich') } else { @('reset-rich','flat','retained') }
        foreach ($arm in $arms) {
            foreach ($taskId in @('S01','S07')) {
                $runId = Get-ExpectedRunId $arm $block $taskId
                $expected.Add("$runId|$block|$arm|$taskId|$(if ($taskId -eq 'S01') { 1 } else { 2 })")
            }
        }
    }
    $actual = [Collections.Generic.List[string]]::new()
    foreach ($cell in @($Design.cells)) {
        $expectedRun = Get-ExpectedRunId ([string]$cell.arm) ([string]$cell.block) ([string]$cell.taskId)
        $sequence = if ($cell.taskId -eq 'S01') { 1 } else { 2 }
        if ($cell.runId -cne $expectedRun -or $cell.sequenceIndex -ne $sequence) { throw "Design cell is not canonical: $($cell.runId)." }
        $actual.Add("$($cell.runId)|$($cell.block)|$($cell.arm)|$($cell.taskId)|$($cell.sequenceIndex)")
    }
    Check ($actual.Count -eq 12 -and ($actual.ToArray() -join '|') -ceq ($expected.ToArray() -join '|')) 'all twelve global cells use the rotated arm order and S01/S07 task sequence'
    $blocks = @($Design.blocks)
    Check ($blocks.Count -eq 2 -and $blocks[0].block -ceq 'B1' -and $blocks[1].block -ceq 'B2' -and
        ($blocks[0].armOrder -join '|') -ceq 'flat|retained|reset-rich' -and ($blocks[1].armOrder -join '|') -ceq 'reset-rich|flat|retained') 'design declares both preregistered block arm orders'
    $expectedPinRun = Get-ExpectedRunId ([string]$Pin.arm) ([string]$Pin.block) ([string]$Pin.taskId)
    Check ($Pin.runId -ceq $expectedPinRun -and $Pin.sequenceIndex -eq $(if ($Pin.taskId -eq 'S01') { 1 } else { 2 })) 'run pin matches its exact global cell identity'
}

function Assert-SourceArtifactBindings([object]$Pin, [object]$Global, [string]$Revision) {
    $expected = Get-SourceArchiveMap
    $artifacts = @($Pin.sourceArtifacts)
    Check ($artifacts.Count -eq $expected.Count -and $artifacts.Count -eq 23) 'complete fixed 003 source-artifact map'
    Check ((ConvertTo-Json -InputObject $artifacts -Depth 100 -Compress) -ceq (ConvertTo-Json -InputObject @($Global.sourceArtifacts) -Depth 100 -Compress)) 'per-run source artifacts equal the global freeze'
    foreach ($entry in $expected) {
        $match = @($artifacts | Where-Object { $_.path -ceq $entry.path -and $_.sourcePath -ceq $entry.sourcePath })
        Check ($match.Count -eq 1) "source snapshot map entry: $($entry.sourcePath)"
    }
    foreach ($artifact in $artifacts) {
        $snapshotBytes = Get-GitSnapshotBytes $Revision ([string]$artifact.path)
        $sourceBytes = Get-GitSnapshotBytes $Revision ([string]$artifact.sourcePath)
        Check ($snapshotBytes.Length -eq $artifact.bytes -and (Get-Sha256Bytes $snapshotBytes) -ceq $artifact.gitBlobSha256) "committed snapshot bytes: $($artifact.path)"
        Check ($sourceBytes.Length -eq $artifact.bytes -and (Get-Sha256Bytes $sourceBytes) -ceq $artifact.sourceGitBlobSha256) "committed source bytes: $($artifact.sourcePath)"
        Check ($artifact.gitBlobSha256 -ceq $artifact.sourceGitBlobSha256 -and $artifact.sha256 -ceq $artifact.gitBlobSha256) "source, snapshot, and working hashes agree: $($artifact.sourcePath)"
        $workingSnapshot = Get-FullPath ([string]$artifact.path)
        $workingSource = Get-FullPath ([string]$artifact.sourcePath)
        Check ((Test-Path -LiteralPath $workingSnapshot -PathType Leaf) -and (Get-Item -LiteralPath $workingSnapshot).Length -eq $artifact.bytes -and (Get-Sha256 $workingSnapshot) -ceq $artifact.sha256) "working snapshot matches frozen source: $($artifact.path)"
        Check ((Test-Path -LiteralPath $workingSource -PathType Leaf) -and (Get-Item -LiteralPath $workingSource).Length -eq $artifact.bytes -and (Get-Sha256 $workingSource) -ceq $artifact.sha256) "working source matches frozen source: $($artifact.sourcePath)"
    }
}

function Get-VerifiedBaselineArchive([object]$Record, [string]$Revision, [object[]]$SourceArtifacts) {
    $expectedManifestPath = "$studyPath/artifacts/baselines/$($Record.kind)/manifest.json"
    $expectedProjectPath = "$studyPath/artifacts/baselines/$($Record.kind)/project"
    Check ($Record.kind -cin @('flat','rich') -and $Record.manifestPath -ceq $expectedManifestPath -and $Record.projectPath -ceq $expectedProjectPath) "$($Record.kind) baseline uses canonical committed archive paths"
    $manifestPath = Get-FullPath ([string]$Record.manifestPath)
    $projectPath = Get-FullPath ([string]$Record.projectPath)
    Check ((Test-Path -LiteralPath $manifestPath -PathType Leaf) -and (Test-Path -LiteralPath $projectPath -PathType Container)) "versioned $($Record.kind) baseline paths exist"
    $manifest = Read-Json $manifestPath
    Check ($manifest.schemaVersion -eq 1 -and $manifest.studyId -ceq $studyId -and $manifest.kind -ceq $Record.kind) "$($Record.kind) baseline manifest identity"
    $expectedMode = if ($Record.kind -eq 'flat') { 'flat' } else { 'growing' }
    $expectedCounts = if ($Record.kind -eq 'flat') { @{words=0;types=6;tests=0} } else { @{words=53;types=31;tests=151} }
    Check ($manifest.mode -ceq $expectedMode -and $manifest.counts.words -eq $expectedCounts.words -and
        $manifest.counts.types -eq $expectedCounts.types -and $manifest.counts.tests -eq $expectedCounts.tests) "$($Record.kind) frozen baseline counts"
    Check ($manifest.bootstrapperPath -ceq 'scripts/Prepare-BusinessPolicyTrial.ps1' -and
        $manifest.bootstrapperSha256 -ceq (Get-Sha256 (Get-FullPath 'scripts/Prepare-BusinessPolicyTrial.ps1')) -and
        $manifest.runtime.buildSourceRevision -ceq $Record.runtime.buildSourceRevision) "$($Record.kind) baseline build provenance"
    Check ((ConvertTo-Json -InputObject $manifest.runtime -Depth 100 -Compress) -ceq (ConvertTo-Json -InputObject $Record.runtime -Depth 100 -Compress) -and
        (ConvertTo-Json -InputObject $manifest.counts -Depth 100 -Compress) -ceq (ConvertTo-Json -InputObject $Record.counts -Depth 100 -Compress) -and
        $manifest.seedStateSha256 -ceq $Record.seedStateSha256 -and $manifest.bootstrapperPath -ceq $Record.bootstrapperPath -and
        $manifest.bootstrapperSha256 -ceq $Record.bootstrapperSha256) "$($Record.kind) archive manifest provenance equals global freeze"
    $inventory = Get-ProjectInventory $projectPath
    $fileRows = Get-FullInventoryRows $inventory
    $manifestRows = Sort-OrdinalRows @($manifest.files | ForEach-Object { [pscustomobject][ordered]@{path=$_.path;bytes=[long]$_.bytes;sha256=([string]$_.sha256).ToLowerInvariant()} })
    Check ((ConvertTo-Json -InputObject $fileRows -Depth 100 -Compress) -ceq (ConvertTo-Json -InputObject $manifestRows -Depth 100 -Compress)) "$($Record.kind) baseline complete file inventory"
    $inventoryHash = Get-InventoryHash $inventory
    Check ($inventoryHash -ceq $manifest.inventorySha256 -and $inventoryHash -ceq $Record.inventorySha256) "$($Record.kind) baseline full-row inventory hash"
    if ($null -ne $Record.treeSha256) { Check ($Record.treeSha256 -ceq $inventoryHash) "$($Record.kind) baseline treeSha256 aliases inventorySha256" }
    Check ((Get-Sha256 $manifestPath) -ceq $Record.manifestSha256) "$($Record.kind) baseline manifest hash"
    $manifestGit = Get-GitSnapshotBytes $Revision ([string]$Record.manifestPath)
    Check ((Get-Sha256Bytes $manifestGit) -ceq $Record.manifestSha256) "$($Record.kind) baseline manifest committed bytes"
    foreach ($file in $fileRows) {
        $relative = "$($Record.projectPath)/$($file.path)"
        $gitBytes = Get-GitSnapshotBytes $Revision $relative
        Check ($gitBytes.Length -eq $file.bytes -and (Get-Sha256Bytes $gitBytes) -ceq $file.sha256) "$($Record.kind) baseline Git blob: $($file.path)"
    }
    $inputs = Sort-OrdinalRows @($manifest.sourceInputs | ForEach-Object { [pscustomobject][ordered]@{path=$_.path;sha256=$_.sha256} })
    Check ((ConvertTo-Json -InputObject $inputs -Depth 100 -Compress) -ceq (ConvertTo-Json -InputObject @($Record.sourceInputs) -Depth 100 -Compress)) "$($Record.kind) source inputs match the manifest"
    foreach ($sourceInput in $inputs) {
        $artifact = @($SourceArtifacts | Where-Object { $_.sourcePath -ceq $sourceInput.path -and $_.sha256 -ceq $sourceInput.sha256 })
        Check ($artifact.Count -eq 1) "$($Record.kind) source input is pinned as a source artifact: $($sourceInput.path)"
    }
    return [pscustomobject][ordered]@{manifest=$manifest;inventory=$inventory;inventoryHash=$inventoryHash}
}

function Invoke-GenericTerminationAudit([string]$TraceFile, [string]$AuditorFile) {
    $pwsh = (Get-Command pwsh -ErrorAction Stop).Source
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $pwsh
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in @('-NoProfile','-File',$AuditorFile,'-TracePath',$TraceFile)) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    if (-not $process.Start()) { throw 'Could not start the generic V2 termination auditor.' }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(90000)) {
        try { $process.Kill($true) } catch { }
        throw 'Generic V2 termination audit exceeded its 90-second bound.'
    }
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    $code = $process.ExitCode
    $process.Dispose()
    if ($code -ne 0) { throw "Generic V2 termination audit failed: $($stderr.Trim()) $($stdout.Trim())" }
    return [pscustomobject][ordered]@{exitCode=$code;output=$stdout.Trim();auditorPath=(Get-RepoPath $AuditorFile);auditorSha256=(Get-Sha256 $AuditorFile)}
}

function Assert-Frame([object]$Frame, [string]$Label, [long]$MaxPayloadBytes) {
    $rawPayload = Get-Property $Frame 'rawLine'
    if ($null -eq $rawPayload -or $rawPayload -isnot [string]) { throw "$Label is missing its raw JSON payload." }
    $singleTrailingCr = $rawPayload.EndsWith("`r",[StringComparison]::Ordinal) -and -not $rawPayload.Substring(0,$rawPayload.Length - 1).Contains("`r")
    if ($rawPayload.Contains("`n") -or ($rawPayload.Contains("`r") -and -not $singleTrailingCr)) { throw "$Label raw JSON payload contains a disallowed line terminator." }
    $null = Read-JsonText $rawPayload $Label
    $payloadBytes = [Text.UTF8Encoding]::new($false, $true).GetBytes($rawPayload)
    $wireBytes = [Convert]::FromBase64String([string]$Frame.wireBase64)
    $expectedWire = [byte[]]::new($payloadBytes.Length + 1)
    [Array]::Copy($payloadBytes,$expectedWire,$payloadBytes.Length)
    $expectedWire[-1] = 10
    Check ($payloadBytes.Length -eq [long]$Frame.payloadUtf8Bytes -and $payloadBytes.Length -le $MaxPayloadBytes) "$Label payload byte count and per-line limit"
    Check ($wireBytes.Length -eq [long]$Frame.wireUtf8Bytes -and $wireBytes.Length -eq $expectedWire.Length) "$Label wire byte count"
    for ($index = 0; $index -lt $wireBytes.Length; $index++) { if ($wireBytes[$index] -ne $expectedWire[$index]) { throw "$Label wire bytes differ from raw UTF-8 payload plus LF." } }
    Check ((Get-Sha256Bytes $wireBytes) -ceq $Frame.sha256) "$Label exact wire SHA-256"
    return [pscustomobject][ordered]@{payloadBytes=$payloadBytes.Length;wireBytes=$wireBytes.Length;wireSha256=$Frame.sha256;payloadSha256=(Get-Sha256Bytes $payloadBytes)}
}

function Assert-RuntimeSourceUnchanged([string]$BuildRevision, [string]$StudyRevision) {
    $null = & git -C $repo cat-file -e "$BuildRevision^{commit}" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Runtime build source revision is not a local Git commit: $BuildRevision" }
    $changed = @(& git -C $repo diff --name-only --no-renames $BuildRevision $StudyRevision -- src experiments/AgentLang.Business Directory.Build.props Directory.Build.targets '*.props' '*.targets')
    if ($LASTEXITCODE -ne 0) { throw 'Could not compare runtime source between its build commit and the study source revision.' }
    Check ($changed.Count -eq 0) 'runtime language/build source is unchanged since the clean pinned fresh build'
}

function Get-AssemblyInformationalVersion([string]$Path) {
    $assembly = [Reflection.Assembly]::LoadFrom($Path)
    $attribute = @($assembly.GetCustomAttributesData() | Where-Object { $_.AttributeType.FullName -ceq 'System.Reflection.AssemblyInformationalVersionAttribute' })
    if ($attribute.Count -ne 1 -or $attribute[0].ConstructorArguments.Count -ne 1) { throw "Runtime assembly lacks an informational version: $Path" }
    return [string]$attribute[0].ConstructorArguments[0].Value
}

function Assert-RuntimeAssemblyRevision([object]$RuntimeRoots, [string]$BuildRevision) {
    $paths = @((Get-FullPath $RuntimeRoots.cliDllPath),(Get-FullPath $RuntimeRoots.businessDllPath))
    foreach ($directoryPath in @($RuntimeRoots.cliDirectoryPath,$RuntimeRoots.businessDirectoryPath)) {
        $corePath = Join-Path (Get-FullPath $directoryPath) 'AgentLang.Core.dll'
        if (Test-Path -LiteralPath $corePath -PathType Leaf) { $paths += $corePath }
    }
    foreach ($path in @($paths | Select-Object -Unique)) {
        $value = Get-AssemblyInformationalVersion $path
        if ($value -notmatch '\+([0-9a-fA-F]{40,64})$' -or $Matches[1].ToLowerInvariant() -cne $BuildRevision.ToLowerInvariant()) { throw "Runtime build commit mismatch in ${path}: $value" }
    }
    Check ($true) 'CLI, Business, and available Core assemblies carry the frozen build commit'
}

try {
    foreach ($required in @($trace,$pinPath,$statePath,$promptPath,$terminationAuditPath)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required run or audit file is missing: $required" }
    }
    Assert-NoReparseTree $run
    $pin = Read-Json $pinPath
    $state = Read-Json $statePath
    $events = Read-JsonLines $trace
    $traceHash = Get-Sha256 $trace
    $pinHash = Get-Sha256 $pinPath
    $stateHash = Get-Sha256 $statePath
    $promptHash = Get-Sha256 $promptPath
    $globalPath = Get-FullPath ([string]$pin.globalFreezePath)
    if (-not (Test-Path -LiteralPath $globalPath -PathType Leaf)) { throw 'The global freeze manifest is missing.' }
    $global = Read-Json $globalPath
    $revision = [string]$pin.sourceRevision

    Check ($pin.schemaVersion -eq 1 -and $pin.studyId -ceq $studyId -and $state.schemaVersion -eq 1 -and $state.studyId -ceq $studyId) 'schema-1 study identity'
    Check ($pin.controlOnly -eq $false -and $pin.launchable -eq $true -and $pin.dirty -eq $false) 'actor pin is launchable and was frozen from a clean committed source tree'
    Check ($pin.model -ceq 'gpt-6-luna' -and $pin.reasoningEffort -ceq 'max' -and $pin.forkTurns -ceq 'none') 'fresh Luna/max model and no inherited turns are frozen'
    Check ($pin.arm -cin @('flat','retained','reset-rich') -and $pin.block -cin @('B1','B2') -and $pin.taskId -cin @('S01','S07')) 'pin uses a preregistered arm, block, and task'
    Check ($pin.dirty -eq $false -and -not [string]::IsNullOrWhiteSpace($revision)) 'source revision is marked clean and nonempty'
    $null = & git -C $repo cat-file -e "$revision^{commit}" 2>&1
    Check ($LASTEXITCODE -eq 0) 'pinned source revision is a local Git commit'
    $expectedRun = Get-ExpectedRunId ([string]$pin.arm) ([string]$pin.block) ([string]$pin.taskId)
    $expectedRunPath = Get-FullPath "$repo/.agentlang/business-policy-retention-003/runs/$expectedRun"
    Check ($pin.runId -ceq $expectedRun -and $run -ceq $expectedRunPath -and $trace -ceq (Join-Path $expectedRunPath 'trace.jsonl')) 'trace path identifies the pinned canonical run'
    Check ($global.schemaVersion -eq 1 -and $global.studyId -ceq $studyId -and $global.model -ceq $pin.model -and
        $global.reasoningEffort -ceq $pin.reasoningEffort -and $global.forkTurns -ceq $pin.forkTurns) 'global freeze matches study and model pin'
    Check ((Get-Sha256 $globalPath) -ceq $pin.globalFreezeSha256) 'working global-freeze bytes match the per-run hash'
    $globalBytes = Get-GitSnapshotBytes $revision ([string]$pin.globalFreezePath)
    Check ((Get-Sha256Bytes $globalBytes) -ceq $pin.globalFreezeSha256) 'global-freeze bytes resolve from pinned Git revision'
    Check ($globalPath -ceq (Get-FullPath "$studyPath/artifacts/global-freeze.json")) 'canonical global-freeze path'

    Check ($pin.preparedAtUtc -and ([DateTimeOffset]$pin.preparedAtUtc) -le ([DateTimeOffset]$events[0].startedUtc)) 'freeze predates actual host start'
    Check ($pin.promptPath -ceq 'prompt.txt' -and (Get-Item -LiteralPath $promptPath).Length -eq $pin.promptUtf8Bytes -and $promptHash -ceq $pin.promptSha256) 'anonymous task prompt exact bytes match pin'
    $promptText = [Text.UTF8Encoding]::new($false,$true).GetString([IO.File]::ReadAllBytes($promptPath))
    $promptHeader = ($promptText -split "`nTask $($pin.taskId)",2)[0]
    Check ($promptHeader.StartsWith("Run $studyId $($pin.runId) task $($pin.taskId) as a fresh gpt-6-luna/max coding agent with no inherited turns.",[StringComparison]::Ordinal) -and
        $promptHeader -notmatch '(?i)(?<![A-Za-z0-9_-])(flat|retained|reset-rich|B1|B2)(?![A-Za-z0-9_-])') 'actor prompt uses an anonymous run/task identity with no arm or block label'
    Check ($promptText.Contains('"Yes, run the remaining isolated experiments"',[StringComparison]::Ordinal) -and
        $promptText.Contains('Only after the coordinator has independently accepted the result and asks you to close',[StringComparison]::Ordinal) -and
        $promptText.Contains('{"op":"host.close"}',[StringComparison]::Ordinal) -and
        $promptText.Contains('Do not treat EOF, Ctrl+C, timeout, or an observation delay as completion.',[StringComparison]::Ordinal)) 'prompt requires direct authorization, independent acceptance, and exact explicit close'
    Check ($promptText.Contains('Do not use raw file access, shell commands, other tools, network access, or any project outside this broker.',[StringComparison]::Ordinal)) 'prompt restricts work to the broker and assigned project'
    Check ($pin.startingStatePath -ceq (Get-RepoPath $statePath) -and $stateHash -ceq $pin.startingStateSha256) 'starting-state exact bytes match pin'
    Check ($pin.projectPath -ceq $state.actor.projectPath -and $state.runId -ceq $pin.runId -and $state.arm -ceq $pin.arm -and
        $state.block -ceq $pin.block -and $state.taskId -ceq $pin.taskId -and $state.sequenceIndex -eq $pin.sequenceIndex) 'prepared state and pin cell identities match'
    Check ($state.preparation.sourceRevision -ceq $revision -and $state.preparation.dirty -eq $false) 'prepared state uses the frozen clean revision'
    Check ($state.preparation.scriptPath -ceq $pin.preparerPath -and $state.preparation.scriptSha256 -ceq $pin.preparerSha256 -and
        $state.preparation.bootstrapperPath -ceq $pin.bootstrapperPath -and $state.preparation.bootstrapperSha256 -ceq $pin.bootstrapperSha256) 'preparation and bootstrapper hashes match the per-run pin'
    Check ($state.preparation.oracleSha256 -ceq $pin.oracleSha256 -and $state.preparation.primerSha256 -ceq $pin.primerSha256 -and
        $state.preparation.designSha256 -ceq $pin.designSha256 -and $state.runtime.buildSourceRevision -ceq $pin.runtimeBuildSourceRevision) 'prepared state binds the frozen oracle, primer, design, and runtime build'
    Check ($pin.freezerPath -ceq 'scripts/Freeze-RetentionTrial.ps1' -and (Get-Sha256 (Get-FullPath $pin.freezerPath)) -ceq $pin.freezerSha256) 'running freezer matches the pinned source hash'
    Check ($pin.auditorPath -ceq 'scripts/Audit-RetentionTrace.ps1' -and (Get-Sha256 $PSCommandPath) -ceq $pin.auditorSha256) 'running trace auditor matches the frozen source hash'
    Check ($pin.terminationAuditorPath -ceq 'scripts/Audit-SubagentTrialTerminationV2.ps1' -and
        (Get-Sha256 $terminationAuditPath) -ceq $pin.terminationAuditorSha256 -and $global.terminationAuditor.sha256 -ceq $pin.terminationAuditorSha256) 'generic V2 termination auditor matches frozen hash'

    foreach ($entry in @(
        @{path='scripts/Prepare-RetentionTrial.ps1';hash=$pin.preparerSha256},
        @{path='scripts/Prepare-BusinessPolicyTrial.ps1';hash=$pin.bootstrapperSha256},
        @{path='scripts/Verify-RetentionTrial.ps1';hash=$pin.independentVerifierSha256},
        @{path='scripts/Verify-RetentionPreflight.ps1';hash=$pin.preflightSha256},
        @{path='scripts/Freeze-RetentionTrial.ps1';hash=$pin.freezerSha256},
        @{path='scripts/Audit-RetentionTrace.ps1';hash=$pin.auditorSha256},
        @{path='scripts/Start-SubagentTrialHostV2.ps1';hash=$pin.hostSha256},
        @{path='scripts/Audit-SubagentTrialTerminationV2.ps1';hash=$pin.terminationAuditorSha256},
        @{path=$pin.designPath;hash=$pin.designSha256},
        @{path=$pin.oraclePath;hash=$pin.oracleSha256},
        @{path=$pin.primerPath;hash=$pin.primerSha256},
        @{path=$pin.taskPath;hash=$pin.taskSha256}
    )) {
        Check ((Get-Sha256 (Get-FullPath $entry.path)) -ceq $entry.hash) "working frozen input hash: $($entry.path)"
        $gitBytes = Get-GitSnapshotBytes $revision $entry.path
        Check ((Get-Sha256Bytes $gitBytes) -ceq $entry.hash) "committed frozen input hash: $($entry.path)"
    }

    Check ($pin.designPath -ceq $global.design.path -and $pin.designSha256 -ceq $global.design.sha256 -and
        $pin.oraclePath -ceq $global.oracle.path -and $pin.oracleSha256 -ceq $global.oracle.sha256 -and
        $pin.primerPath -ceq $global.primer.path -and $pin.primerSha256 -ceq $global.primer.sha256) 'per-run design/oracle/primer bindings match global freeze'
    Check ($pin.preparerPath -ceq $global.preparer.path -and $pin.preparerSha256 -ceq $global.preparer.sha256 -and
        $pin.bootstrapperPath -ceq $global.bootstrapper.path -and $pin.bootstrapperSha256 -ceq $global.bootstrapper.sha256 -and
        $pin.independentVerifierPath -ceq $global.independentVerifier.path -and $pin.independentVerifierSha256 -ceq $global.independentVerifier.sha256 -and
        $pin.preflightPath -ceq $global.preflightVerifier.path -and $pin.preflightSha256 -ceq $global.preflightVerifier.sha256 -and
        $pin.auditorPath -ceq $global.traceAuditor.path -and $pin.auditorSha256 -ceq $global.traceAuditor.sha256 -and
        $pin.hostPath -ceq $global.host.path -and $pin.hostSha256 -ceq $global.host.sha256) 'per-run verifier, preparation, host, and auditor bindings match global freeze'
    $design = Read-Json (Get-FullPath $pin.designPath)
    Assert-RunDesign $design $pin
    $globalTask = @($global.tasks | Where-Object { $_.taskId -ceq $pin.taskId })
    Check ($globalTask.Count -eq 1 -and $pin.taskPath -ceq $globalTask[0].path -and $pin.taskSha256 -ceq $globalTask[0].sha256) 'per-run public task path and hash match global freeze'

    $sourceArtifacts = @($pin.sourceArtifacts)
    Assert-SourceArtifactBindings $pin $global $revision
    $newPrimerArtifact = @($sourceArtifacts | Where-Object { $_.sourcePath -ceq "$studyPath/language-primer.md" })
    $oldPrimerArtifact = @($sourceArtifacts | Where-Object { $_.sourcePath -ceq 'experiments/AgentLang.SubagentTrials/business-policy-help-002/language-primer.md' })
    Check ($newPrimerArtifact.Count -eq 1 -and $oldPrimerArtifact.Count -eq 1 -and $newPrimerArtifact[0].sha256 -ceq $oldPrimerArtifact[0].sha256) '003 language primer preserves the exact archived 002 help bytes'
    $oracle = Read-Json (Get-FullPath $pin.oraclePath)
    $priorOracle = Read-Json (Get-FullPath 'experiments/AgentLang.SubagentTrials/business-policy-001/acceptance.json')
    Check ($oracle.studyId -ceq $studyId -and $oracle.predecessorSha256 -ceq (Get-Sha256 (Get-FullPath 'experiments/AgentLang.SubagentTrials/business-policy-001/acceptance.json'))) 'retention acceptance oracle identity and 001 predecessor hash'
    $caseCounts = [ordered]@{}
    foreach ($taskId in @('S01','S07')) {
        $expectedCount = if ($taskId -eq 'S01') { 10 } else { 54 }
        $oldTask = @($priorOracle.tasks | Where-Object { $_.id -ceq $taskId })
        $newTask = @($oracle.tasks | Where-Object { $_.id -ceq $taskId })
        Check ($oldTask.Count -eq 1 -and $newTask.Count -eq 1 -and $newTask[0].cases.Count -eq $expectedCount -and
            (ConvertTo-Json -InputObject $oldTask[0].cases -Depth 100 -Compress) -ceq (ConvertTo-Json -InputObject $newTask[0].cases -Depth 100 -Compress)) "$taskId oracle keeps all $expectedCount exact 001 money cases"
        $caseCounts[$taskId] = $newTask[0].cases.Count
    }
    $oracleTaskIds = Sort-OrdinalStrings @($oracle.tasks | ForEach-Object { [string]$_.id })
    Check (($oracleTaskIds -join '|') -ceq 'S01|S07') 'oracle contains exactly S01 and S07'

    Check ($pin.profile -ceq 'agentlang' -and $pin.hostProtocolVersion -ceq 'subagent-trial-host-v2' -and
        $pin.hostPath -ceq 'scripts/Start-SubagentTrialHostV2.ps1' -and (Get-Sha256 (Get-FullPath $pin.hostPath)) -ceq $pin.hostSha256) 'current V2 host identity and source hash'
    Check ($pin.maxExchanges -eq 100 -and $pin.maxRequestBytes -eq 262144 -and $pin.maxResponseBytes -eq 524288 -and
        $pin.exchangeTimeoutMilliseconds -eq 120000 -and $pin.clockValue -ceq '2000-01-01T00:00:00Z') 'frozen 002-compatible per-line and exchange limits'
    Check (@($pin.capabilities).Count -eq 0 -and @($pin.additionalCliArguments).Count -eq 0 -and
        $pin.maxInspectionResponseBytes -eq $null -and $pin.inspectionBudgetEnabled -eq $false) 'no capabilities, extra CLI arguments, or cumulative inspection budget'
    Check (@($pin.transportControls).Count -eq 1 -and $pin.transportControls[0] -ceq 'host.close' -and
        $pin.allowedOperations -cnotcontains 'host.close' -and $pin.allowedOperations -ccontains 'help') 'host.close is transport-only and help is enabled'
    Check ((ConvertTo-Json -InputObject (Sort-OrdinalStrings @($pin.allowedOperations)) -Depth 100 -Compress) -ceq
        (ConvertTo-Json -InputObject (Sort-OrdinalStrings @($global.host.allowedOperations)) -Depth 100 -Compress)) 'pin operation allowlist equals the global host allowlist'

    $runtimeRoots = $pin.runtimeRoots
    $cliDll = Get-FullPath $runtimeRoots.cliDllPath
    $cliDirectory = Get-FullPath $runtimeRoots.cliDirectoryPath
    $businessDll = Get-FullPath $runtimeRoots.businessDllPath
    $businessDirectory = Get-FullPath $runtimeRoots.businessDirectoryPath
    $launchCommand = [string]$pin.launchCommand
    $operationArgument = "-AllowedOperations @('" + ($pin.allowedOperations -join "','") + "')"
    Check ($launchCommand.Contains("-CliDll '$cliDll'",[StringComparison]::Ordinal) -and
        $launchCommand.Contains("-ProjectPath '$($pin.projectPath)'",[StringComparison]::Ordinal) -and
        $launchCommand.Contains("-TracePath '$trace'",[StringComparison]::Ordinal) -and
        $launchCommand.Contains('Start-SubagentTrialHostV2.ps1',[StringComparison]::Ordinal) -and
        $launchCommand.Contains($operationArgument,[StringComparison]::Ordinal) -and
        $launchCommand.Contains("-ClockValue '2000-01-01T00:00:00Z'",[StringComparison]::Ordinal) -and
        $launchCommand.Contains('-MaxRequestBytes 262144',[StringComparison]::Ordinal) -and
        $launchCommand.Contains('-MaxResponseBytes 524288',[StringComparison]::Ordinal) -and
        $launchCommand.Contains('-ExchangeTimeoutMilliseconds 120000',[StringComparison]::Ordinal) -and
        $launchCommand.Contains('-MaxExchanges 100',[StringComparison]::Ordinal)) 'generated launch command pins the V2 host, runtime, project, operations, fixed clock, and limits'
    Check ([IO.Path]::GetDirectoryName($cliDll) -ieq $cliDirectory -and [IO.Path]::GetDirectoryName($businessDll) -ieq $businessDirectory) 'runtime DLLs are inside pinned inventory roots'
    Check ((Get-FullPath ([string]$state.runtime.cliPath)) -ceq $cliDll -and (Get-FullPath ([string]$state.runtime.businessPath)) -ceq $businessDll -and
        $state.runtime.cliSha256 -ceq (Get-Sha256 $cliDll) -and $state.runtime.businessSha256 -ceq (Get-Sha256 $businessDll)) 'prepared runtime DLL paths and raw hashes match state'
    $runtimeFiles = Sort-OrdinalRows @((Get-RuntimeInventory 'cli' $cliDirectory) + (Get-RuntimeInventory 'business' $businessDirectory)) 'runtime' 'path'
    Check ((ConvertTo-Json -InputObject $runtimeFiles -Depth 100 -Compress) -ceq (ConvertTo-Json -InputObject @($pin.runtimeFiles) -Depth 100 -Compress) -and
        (ConvertTo-Json -InputObject $runtimeFiles -Depth 100 -Compress) -ceq (ConvertTo-Json -InputObject @($global.runtime.files) -Depth 100 -Compress)) 'complete CLI and Business runtime inventories match per-run and global freezes'
    Check ($pin.runtimeBuildSourceRevision -ceq $global.runtime.buildSourceRevision -and $state.runtime.buildSourceRevision -ceq $global.runtime.buildSourceRevision) 'runtime build provenance matches state and global freeze'
    Assert-RuntimeSourceUnchanged ([string]$pin.runtimeBuildSourceRevision) $revision
    Assert-RuntimeAssemblyRevision $runtimeRoots ([string]$pin.runtimeBuildSourceRevision)

    $baselineArchives = @($pin.baselineArchives)
    Check ($baselineArchives.Count -eq 2 -and (ConvertTo-Json -InputObject $baselineArchives -Depth 100 -Compress) -ceq
        (ConvertTo-Json -InputObject @($global.baselineArchives) -Depth 100 -Compress)) 'flat and rich baseline archive pins match global freeze'
    $verifiedBaselines = [Collections.Generic.List[object]]::new()
    foreach ($record in $baselineArchives) { $verifiedBaselines.Add((Get-VerifiedBaselineArchive $record $revision $sourceArtifacts)) }
    $baselineKind = [string]$pin.baseline.kind
    $expectedBaselineKind = if ($pin.arm -ceq 'flat') { 'flat' } else { 'rich' }
    Check ($baselineKind -ceq $expectedBaselineKind) 'run baseline kind matches randomized arm'
    $selectedArchive = @($baselineArchives | Where-Object { $_.kind -ceq $baselineKind })
    Check ($selectedArchive.Count -eq 1 -and (Get-FullPath $pin.baseline.path) -ceq (Get-FullPath $selectedArchive[0].projectPath) -and
        (Get-FullPath $pin.baseline.manifestPath) -ceq (Get-FullPath $selectedArchive[0].manifestPath) -and
        $pin.baseline.manifestSha256 -ceq $selectedArchive[0].manifestSha256 -and $pin.baseline.seedStateSha256 -ceq $selectedArchive[0].seedStateSha256 -and
        $pin.baseline.inventorySha256 -ceq $selectedArchive[0].inventorySha256) 'prepared state uses the exact versioned arm baseline and manifest'
    $baselineRows = Sort-OrdinalRows @($pin.baseline.files | ForEach-Object { [pscustomobject][ordered]@{path=$_.path;bytes=[long]$_.bytes;sha256=$_.sha256} })
    Check ((ConvertTo-Json -InputObject $baselineRows -Depth 100 -Compress) -ceq (ConvertTo-Json -InputObject @($selectedArchive[0].files) -Depth 100 -Compress)) 'per-run baseline full file inventory equals global archive'
    $baselineInputs = Sort-OrdinalRows @($pin.baseline.sourceInputs | ForEach-Object { [pscustomobject][ordered]@{path=$_.path;sha256=$_.sha256} })
    Check ((ConvertTo-Json -InputObject $baselineInputs -Depth 100 -Compress) -ceq (ConvertTo-Json -InputObject @($selectedArchive[0].sourceInputs) -Depth 100 -Compress)) 'per-run baseline input hashes equal global archive'

    $startingPath = Get-FullPath $pin.startingProjectPath
    Check ($pin.startingProjectPath -ceq (Get-RepoPath (Join-Path $run 'starting-project')) -and $pin.projectPath -ceq (Get-FullPath "$repo/.agentlang/business-policy-retention-003/actors/$($pin.runId)")) 'run uses canonical starting and actor paths'
    $startingInventory = Get-ProjectInventory $startingPath
    $startingRows = Get-FullInventoryRows $startingInventory
    $pinStartingRows = Sort-OrdinalRows @($pin.startingProjectFiles | ForEach-Object { [pscustomobject][ordered]@{path=$_.path;bytes=[long]$_.bytes;sha256=$_.sha256} })
    Check ((ConvertTo-Json -InputObject $startingRows -Depth 100 -Compress) -ceq (ConvertTo-Json -InputObject $pinStartingRows -Depth 100 -Compress) -and
        (Get-InventoryHash $startingInventory) -ceq $pin.startingProjectInventorySha256 -and $pin.startingProjectInventorySha256 -ceq $state.project.inventorySha256) 'starting-project full inventory matches pin and preparation state'
    $stateProjectFiles = Sort-OrdinalRows @($state.project.files | ForEach-Object { [pscustomobject][ordered]@{path=$_.path;bytes=[long]$_.bytes;sha256=$_.sha256} })
    $stateActorFiles = Sort-OrdinalRows @($state.actor.files | ForEach-Object { [pscustomobject][ordered]@{path=$_.path;bytes=[long]$_.bytes;sha256=$_.sha256} })
    $pinActorFiles = Sort-OrdinalRows @($pin.actorProjectFiles | ForEach-Object { [pscustomobject][ordered]@{path=$_.path;bytes=[long]$_.bytes;sha256=$_.sha256} })
    Check ((ConvertTo-Json -InputObject $stateProjectFiles -Depth 100 -Compress) -ceq (ConvertTo-Json -InputObject $pinStartingRows -Depth 100 -Compress) -and
        (ConvertTo-Json -InputObject $stateActorFiles -Depth 100 -Compress) -ceq (ConvertTo-Json -InputObject $pinActorFiles -Depth 100 -Compress) -and
        $state.actor.projectPath -ceq $pin.projectPath -and $state.actor.inventorySha256 -ceq $pin.actorProjectInventorySha256) 'prepared project/actor inventories match the prelaunch pin'
    $finalActorInventory = Get-ProjectInventory (Get-FullPath $pin.projectPath)
    $finalActorTreeHash = Get-InventoryHash $finalActorInventory
    $finalActorRows = Get-FullInventoryRows $finalActorInventory

    $previous = $pin.previousAcceptance
    $fallback = $pin.arm -ceq 'retained' -and $pin.taskId -ceq 'S07' -and $null -ne $previous -and $previous.passed -eq $false -and $previous.carryForward -eq $false
    Check ((ConvertTo-Json -InputObject $previous -Depth 100 -Compress) -ceq (ConvertTo-Json -InputObject $state.previousAcceptance -Depth 100 -Compress) -and
        $pin.priorOutcome -ceq $state.priorOutcome) 'per-run predecessor decision matches the prepared starting state'
    if ($pin.arm -ceq 'retained' -and $pin.taskId -ceq 'S07') {
        Check ($null -ne $previous) 'Retained S07 names same-block S01 predecessor evidence'
        $previousPath = Get-FullPath ([string]$previous.path)
        Check ((Test-Path -LiteralPath $previousPath -PathType Leaf) -and (Get-Sha256 $previousPath) -ceq $previous.sha256) 'Retained S01 acceptance bytes match state'
        $previousResult = Read-Json $previousPath
        $previousProjectRecord = Get-Property $previousResult 'project'
        $previousProjectTreeHash = [string](Get-Property $previousProjectRecord 'treeSha256')
        if ([string]::IsNullOrWhiteSpace($previousProjectTreeHash)) { $previousProjectTreeHash = [string](Get-Property $previousResult 'outputTreeSha256') }
        $previousProjectPath = [string](Get-Property $previousProjectRecord 'path')
        $expectedPreviousRun = if ($pin.block -ceq 'B1') { 'R03' } else { 'R11' }
        $previousAcceptanceCanonicalPath = Get-FullPath "$repo/.agentlang/business-policy-retention-003/runs/$expectedPreviousRun/acceptance.json"
        Check ((Test-Path -LiteralPath $previousAcceptanceCanonicalPath -PathType Leaf) -and
            (Get-Sha256 $previousAcceptanceCanonicalPath) -ceq $previous.sha256) 'Retained S07 predecessor acceptance is present at its canonical run path'
        Check ($previousResult.schemaVersion -eq 1 -and $previousResult.studyId -ceq $studyId -and $previousResult.runId -ceq $expectedPreviousRun -and
            $previousResult.sequenceIndex -eq 1 -and $previousResult.resultKind -ceq 'frozen-actor-acceptance' -and $previousResult.checkFrozenPin -eq $true -and
            $previousResult.arm -ceq 'retained' -and $previousResult.block -ceq $pin.block -and $previousResult.taskId -ceq 'S01' -and
            $previous.runId -ceq $expectedPreviousRun -and $previous.arm -ceq 'retained' -and $previous.block -ceq $pin.block -and $previous.taskId -ceq 'S01') 'Retained S07 predecessor is same-block Retained S01'
        $expectedPreviousActor = Get-FullPath "$repo/.agentlang/business-policy-retention-003/actors/$expectedPreviousRun"
        Check ($previousProjectPath -and (Get-FullPath $previousProjectPath) -ceq $expectedPreviousActor -and
            (Test-Path -LiteralPath $expectedPreviousActor -PathType Container) -and
            (Get-InventoryHash (Get-ProjectInventory $expectedPreviousActor)) -ceq $previousProjectTreeHash -and
            $previous.projectTreeSha256 -ceq $previousProjectTreeHash) 'same-block S01 acceptance binds the unchanged final actor tree'
        $previousTraceAuditPin = Get-Property $pin 'previousTraceAudit'
        $previousTraceAuditPath = Get-FullPath "$repo/.agentlang/business-policy-retention-003/runs/$expectedPreviousRun/trace-audit.json"
        Check ($null -ne $previousTraceAuditPin -and $previousTraceAuditPin.passed -eq $true -and
            $previousTraceAuditPin.path -ceq (Get-RepoPath $previousTraceAuditPath) -and
            (Test-Path -LiteralPath $previousTraceAuditPath -PathType Leaf) -and
            (Get-Sha256 $previousTraceAuditPath) -ceq $previousTraceAuditPin.sha256) 'Retained S07 pin binds the canonical passing predecessor trace audit'
        $previousTraceAudit = Read-Json $previousTraceAuditPath
        Check ($previousTraceAudit.passed -eq $true -and $previousTraceAudit.acceptanceSha256 -ceq $previous.sha256 -and
            $previousTraceAudit.acceptancePath -ceq (Get-RepoPath (Get-FullPath "$repo/.agentlang/business-policy-retention-003/runs/$expectedPreviousRun/acceptance.json")) -and
            $previousTraceAudit.projectTreeSha256 -ceq $previousProjectTreeHash -and
            $previousTraceAudit.finalActorInventorySha256 -ceq $previousProjectTreeHash -and
            $previousTraceAuditPin.acceptanceSha256 -ceq $previousTraceAudit.acceptanceSha256 -and
            $previousTraceAuditPin.projectTreeSha256 -ceq $previousTraceAudit.projectTreeSha256 -and
            $previousTraceAudit.acceptancePassed -eq $previousResult.passed) 'predecessor trace audit binds the same acceptance bytes and final project tree'
        if ($fallback) {
            Check ($pin.priorOutcome -ceq 'baseline-fallback-after-failed-S01' -and $previousResult.passed -eq $false -and
                $previous.passed -eq $false -and $previous.carryForward -eq $false -and
                $pin.startingProjectInventorySha256 -ceq $selectedArchive[0].inventorySha256) 'explicit fallback starts from frozen rich baseline after failed S01'
        } else {
            Check ($pin.priorOutcome -ceq 'accepted-s01-carry-forward' -and $previousResult.passed -eq $true -and $previous.passed -eq $true -and $previous.carryForward -eq $true -and
                $pin.startingProjectInventorySha256 -ceq $previous.projectTreeSha256) 'Retained S07 starts from independently accepted same-block S01 final tree'
        }
    } else {
        Check ($null -eq $previous -and -not $pin.retentionFallback -and $pin.priorOutcome -ceq 'baseline') 'only Retained S07 may carry predecessor evidence or fallback'
        Check ($pin.startingProjectInventorySha256 -ceq $selectedArchive[0].inventorySha256) 'non-carry-forward run starts from exact frozen baseline tree'
    }

    $terminationResult = Invoke-GenericTerminationAudit $trace $terminationAuditPath
    Check ($terminationResult.exitCode -eq 0) 'existing generic V2 termination auditor accepts explicit host.close'

    $canonicalAcceptanceFile = [IO.Path]::GetFullPath((Join-Path $run 'acceptance.json'))
    $acceptanceFile = if ([string]::IsNullOrWhiteSpace($AcceptancePath)) { $canonicalAcceptanceFile } else { Get-FullPath $AcceptancePath }
    $acceptanceRunPrefix = $run.TrimEnd([char[]]@([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar)) + [IO.Path]::DirectorySeparatorChar
    if (-not $acceptanceFile.StartsWith($acceptanceRunPrefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Acceptance evidence must remain inside this run directory.' }
    Check ($acceptanceFile -ceq $canonicalAcceptanceFile) 'acceptance evidence uses the canonical run/acceptance.json path'
    Check ((Test-Path -LiteralPath $acceptanceFile -PathType Leaf)) 'canonical per-run acceptance evidence exists'
    $acceptance = Read-Json $acceptanceFile
    $acceptanceHash = Get-Sha256 $acceptanceFile
    $acceptancePin = Get-Property $acceptance 'frozenPin'
    $acceptancePrelaunch = Get-Property $acceptance 'prelaunch'
    $acceptancePrelaunchData = Get-Property $acceptancePrelaunch 'data'
    $acceptanceProject = Get-Property $acceptance 'project'
    $acceptanceProjectRows = Get-FullInventoryRows @(Get-Property $acceptanceProject 'files')
    $acceptanceProjectTreeHash = [string](Get-Property $acceptanceProject 'treeSha256')
    $canonicalPinPath = [IO.Path]::GetFullPath($pinPath)
    $canonicalAcceptancePinPath = [string](Get-Property $acceptancePin 'path')
    $canonicalAcceptancePrelaunchPath = [string](Get-Property $acceptancePrelaunch 'path')
    $acceptedPreparedAtValue = Get-Property $acceptancePrelaunchData 'preparedAtUtc'
    $pinnedPreparedAtValue = Get-Property $pin 'preparedAtUtc'
    $acceptedPreparedAt = [DateTimeOffset]::MinValue
    $pinnedPreparedAt = [DateTimeOffset]::MinValue
    $timestampCulture = [Globalization.CultureInfo]::InvariantCulture
    $timestampStyles = [Globalization.DateTimeStyles]::RoundtripKind
    $acceptedPreparedAtHasOffset = $acceptedPreparedAtValue -is [string] -and [regex]::IsMatch([string]$acceptedPreparedAtValue,'(?:Z|[+-][0-9]{2}:[0-9]{2})$',[Text.RegularExpressions.RegexOptions]::CultureInvariant)
    $pinnedPreparedAtHasOffset = $pinnedPreparedAtValue -is [string] -and [regex]::IsMatch([string]$pinnedPreparedAtValue,'(?:Z|[+-][0-9]{2}:[0-9]{2})$',[Text.RegularExpressions.RegexOptions]::CultureInvariant)
    $acceptedPreparedAtValid = $acceptedPreparedAtHasOffset -and [DateTimeOffset]::TryParse([string]$acceptedPreparedAtValue,$timestampCulture,$timestampStyles,[ref]$acceptedPreparedAt)
    $pinnedPreparedAtValid = $pinnedPreparedAtHasOffset -and [DateTimeOffset]::TryParse([string]$pinnedPreparedAtValue,$timestampCulture,$timestampStyles,[ref]$pinnedPreparedAt)
    $preparedAtEquivalent = $acceptedPreparedAtValid -and $pinnedPreparedAtValid -and $acceptedPreparedAt -eq $pinnedPreparedAt
    # ConvertFrom-Json can reserialize an offset timestamp in local time; require the same instant and normalize only this field.
    if ($preparedAtEquivalent) { $acceptancePrelaunchData['preparedAtUtc'] = [string]$pinnedPreparedAtValue }
    Check ($acceptance.schemaVersion -eq 1 -and $acceptance.studyId -ceq $studyId -and $acceptance.runId -ceq $pin.runId -and
        $acceptance.arm -ceq $pin.arm -and $acceptance.block -ceq $pin.block -and $acceptance.taskId -ceq $pin.taskId -and
        $acceptance.sequenceIndex -eq $pin.sequenceIndex -and $acceptance.resultKind -ceq 'frozen-actor-acceptance' -and
        $acceptance.checkFrozenPin -eq $true) 'acceptance evidence identifies this frozen study cell'
    Check ($acceptance.verifierSha256 -ceq $pin.independentVerifierSha256 -and
        $acceptancePin.required -eq $true -and $acceptancePin.checked -eq $true -and
        $acceptancePin.sha256 -ceq $pinHash -and
        (Get-FullPath $canonicalAcceptancePinPath) -ceq $canonicalPinPath -and
        $acceptancePrelaunch.sha256 -ceq $pinHash -and
        (Get-FullPath $canonicalAcceptancePrelaunchPath) -ceq $canonicalPinPath -and
        $acceptancePrelaunchData.runId -ceq $pin.runId -and $acceptancePrelaunchData.arm -ceq $pin.arm -and
        $acceptancePrelaunchData.block -ceq $pin.block -and $acceptancePrelaunchData.taskId -ceq $pin.taskId -and
        $preparedAtEquivalent -and
        (ConvertTo-Json -InputObject $acceptancePrelaunchData -Depth 100 -Compress) -ceq (ConvertTo-Json -InputObject $pin -Depth 100 -Compress)) 'acceptance evidence binds the frozen pin and independent verifier'
    Check ($acceptanceProjectTreeHash -ceq $finalActorTreeHash -and
        (ConvertTo-Json -InputObject $acceptanceProjectRows -Depth 100 -Compress) -ceq (ConvertTo-Json -InputObject $finalActorRows -Depth 100 -Compress) -and
        (Get-FullPath ([string](Get-Property $acceptanceProject 'path'))) -ceq (Get-FullPath ([string]$pin.projectPath)) -and
        [string]$acceptance.outputTreeSha256 -ceq $finalActorTreeHash -and $acceptance.passed -is [bool]) 'acceptance final project inventory equals the post-close actor tree'
    $start = $events[0]
    $end = $events[-1]
    $exchanges = @($events | Where-Object { $_.event -ceq 'exchange' })
    $eventNames = @($events | ForEach-Object { [string]$_.event })
    $expectedEvents = [Collections.Generic.List[string]]::new()
    $expectedEvents.Add('session-start')
    for ($eventIndex = 0; $eventIndex -lt $exchanges.Count; $eventIndex++) { $expectedEvents.Add('exchange') }
    $expectedEvents.Add('host-close')
    $expectedEvents.Add('session-end')
    Check ($eventNames.Count -eq ($exchanges.Count + 3) -and ($eventNames -join '|') -ceq ($expectedEvents -join '|')) 'trace has exactly start, all exchanges, explicit close, and end in order'
    Check ($events.Count -ge 3 -and $start.event -ceq 'session-start' -and $start.schemaVersion -eq 1 -and
        $start.hostProtocolVersion -ceq 'subagent-trial-host-v2') 'V2 schema-1 session-start is first event'
    Check ($events[-2].event -ceq 'host-close' -and $end.event -ceq 'session-end' -and $end.terminationKind -ceq 'host-close' -and
        $end.hostExitCode -eq 0 -and $end.runtimeExitCode -eq 0) 'actor ended only with explicit host.close after the final exchange'
    Check (@($events | Where-Object { $_.event -ceq 'host-cancelled' -or $_.event -ceq 'exchange-rejected' }).Count -eq 0) 'trace has no inferred cancellation or rejected/over-limit exchange'
    Check ($start.projectPath -ceq $pin.projectPath -and $start.profile -ceq $pin.profile -and $start.cliDll -ceq $cliDll) 'host loaded the pinned project/profile/CLI runtime'
    Check ($start.hostProtocolVersion -ceq $pin.hostProtocolVersion -and (@($start.transportControls) -join '|') -ceq 'host.close') 'host transport controls match freeze'
    Check (((Sort-OrdinalStrings @($start.allowedOperations)) -join '|') -ceq ((Sort-OrdinalStrings @($pin.allowedOperations)) -join '|')) 'host operation allowlist matches freeze'
    Check (@($start.capabilities).Count -eq 0 -and @($start.additionalCliArguments).Count -eq 0 -and $null -eq (Get-Property $start 'inspectionBudget')) 'actual host has no effects, extra arguments, or cumulative inspection budget'
    Check ($start.limits.maxExchanges -eq $pin.maxExchanges -and $start.limits.maxRequestBytes -eq $pin.maxRequestBytes -and
        $start.limits.maxResponseBytes -eq $pin.maxResponseBytes -and $start.limits.exchangeTimeoutMilliseconds -eq $pin.exchangeTimeoutMilliseconds) 'actual host limits match the frozen bounds'

    $actualCliFiles = Sort-OrdinalRows @($start.cliFiles | ForEach-Object { [pscustomobject][ordered]@{name=$_.name;sha256=$_.sha256} }) 'name'
    $cliDllName = [IO.Path]::GetFileName([string]$runtimeRoots.cliDllPath)
    $cliBaseName = [IO.Path]::GetFileNameWithoutExtension($cliDllName)
    $expectedCliFiles = Sort-OrdinalRows @($pin.runtimeFiles | Where-Object {
        $_.runtime -ceq 'cli' -and $_.path -notmatch '/' -and
        ($_.path.EndsWith('.dll',[StringComparison]::OrdinalIgnoreCase) -or $_.path -ceq "$cliBaseName.deps.json" -or $_.path -ceq "$cliBaseName.runtimeconfig.json")
    } | ForEach-Object { [pscustomobject][ordered]@{name=$_.path;sha256=$_.sha256} }) 'name'
    Check ((ConvertTo-Json -InputObject $actualCliFiles -Depth 100 -Compress) -ceq (ConvertTo-Json -InputObject $expectedCliFiles -Depth 100 -Compress)) 'host-loaded CLI files match complete pinned CLI inventory'

    $close = $events[-2]
    $previousEventAt = [DateTimeOffset]::Parse([string]$start.startedUtc)
    foreach ($exchange in $exchanges) {
        $exchangeAt = [DateTimeOffset]::Parse([string]$exchange.atUtc)
        Check ($exchangeAt -ge $previousEventAt -and [double]$exchange.elapsedMilliseconds -ge 0) "exchange $($exchange.index) timestamp follows the prior event"
        $previousEventAt = $exchangeAt
    }
    $closeAt = [DateTimeOffset]::Parse([string]$close.atUtc)
    $finishedAt = [DateTimeOffset]::Parse([string]$end.finishedUtc)
    Check ($closeAt -ge $previousEventAt -and $finishedAt -ge $closeAt) 'explicit close and session end follow all actor exchanges'
    $exactCloseJson = '{"op":"host.close"}'
    $closeRawIsExact = $close.requestRaw -is [string] -and
        (($close.requestRaw -ceq $exactCloseJson) -or ($close.requestRaw -ceq ($exactCloseJson + "`r")))
    Check ($close.requestCanonical -is [string] -and $close.requestCanonical -ceq $exactCloseJson -and $closeRawIsExact -and
        $close.exchangeCount -eq @($events | Where-Object { $_.event -ceq 'exchange' }).Count) 'final transport control is exact one-field host.close'
    Check ($exchanges.Count -le 100 -and $end.exchangeCount -eq $exchanges.Count -and $close.exchangeCount -eq $exchanges.Count) 'exchange count is complete and within 100-call cap'
    $requestBytes = 0L
    $responseBytes = 0L
    $requestWireBytes = 0L
    $responseWireBytes = 0L
    $successfulMutations = 0
    $successfulTaskBegins = 0
    $successfulTaskCommits = 0
    $helpQueries = [Collections.Generic.List[object]]::new()
    $testBatches = [Collections.Generic.List[object]]::new()
    $diagnostics = [Collections.Generic.List[object]]::new()
    $exchangeHistory = [Collections.Generic.List[object]]::new()
    $mutationOps = @('define','replace-word','commit','task.begin','task.commit')
    for ($index = 0; $index -lt $exchanges.Count; $index++) {
        $exchange = $exchanges[$index]
        $ordinal = $index + 1
        Check ($exchange.index -eq $ordinal) "exchange ordinal $ordinal is contiguous"
        Check ($exchange.executionState -cin @('response-observed','not-executed')) "exchange $ordinal has a conclusive execution state"
        $requestFrame = Assert-Frame $exchange.request "exchange $ordinal request" $pin.maxRequestBytes
        $responseFrame = Assert-Frame $exchange.response "exchange $ordinal response" $pin.maxResponseBytes
        Check ($exchange.requestDelivery.state -ceq 'confirmed' -and $exchange.requestDelivery.completeLineConfirmed -eq $true -and
            $exchange.requestDelivery.confirmedUtf8Bytes -eq $requestFrame.wireBytes) "exchange $ordinal request was fully delivered"
        $request = Read-JsonText ([string]$exchange.request.rawLine) "exchange $ordinal request"
        $response = Read-JsonText ([string]$exchange.response.rawLine) "exchange $ordinal response"
        $operation = [string](Get-Property $request 'op')
        Check ($exchange.operation -ceq $operation -and $pin.allowedOperations -ccontains $operation -and $operation -cne 'host.close') "exchange $ordinal operation matches allowlist"
        Check ($exchange.outcome -ceq 'forwarded' -and $exchange.response.source -ceq 'runtime' -and $null -eq $exchange.response.parseError) "exchange $ordinal was forwarded and returned a parsed runtime response"
        Check ((Get-Property $response 'ok') -is [bool]) "exchange $ordinal response has a boolean ok field"
        Check ($null -eq (Get-Property $exchange 'inspectionBudget')) "exchange $ordinal has no cumulative inspection accounting"
        $requestBytes += $requestFrame.payloadBytes
        $responseBytes += $responseFrame.payloadBytes
        $requestWireBytes += $requestFrame.wireBytes
        $responseWireBytes += $responseFrame.wireBytes
        $ok = (Get-Property $response 'ok') -eq $true
        if (-not $ok) { $diagnostics.Add([pscustomobject][ordered]@{index=$ordinal;op=$operation;error=(Get-Property $response 'error');text=(Get-Property $response 'text')}) }
        if ($ok -and $operation -cin $mutationOps) { $successfulMutations++ }
        if ($ok -and $operation -ceq 'task.begin') { $successfulTaskBegins++ }
        if ($ok -and $operation -ceq 'task.commit') { $successfulTaskCommits++ }
        if ($operation -ceq 'help') {
            $topicValue = Get-Property $request 'topic'
            $topic = if ($null -eq $topicValue -or [string]::IsNullOrWhiteSpace([string]$topicValue)) { 'authoring' } else { [string]$topicValue }
            $helpQueries.Add([pscustomobject][ordered]@{index=$ordinal;topic=$topic;ok=$ok;requestBytes=$requestFrame.payloadBytes;responseBytes=$responseFrame.payloadBytes})
        }
        if ($operation -cin @('test','test-all')) {
            $data = Get-Property $response 'data'
            $results = @(Get-Property $data 'results')
            $failed = @($results | Where-Object { $_.passed -ne $true }).Count
            $testBatches.Add([pscustomobject][ordered]@{index=$ordinal;op=$operation;requestedWord=(Get-Property $request 'word');executions=$results.Count;failed=$failed;responseOk=$ok})
        }
        $exchangeHistory.Add([pscustomobject][ordered]@{
            index=$ordinal;atUtc=$exchange.atUtc;elapsedMilliseconds=$exchange.elapsedMilliseconds;op=$operation
            outcome=$exchange.outcome;executionState=$exchange.executionState
            requestDelivery=$exchange.requestDelivery;responseSource=$exchange.response.source
            request=[ordered]@{payloadUtf8Bytes=$requestFrame.payloadBytes;wireUtf8Bytes=$requestFrame.wireBytes;payloadSha256=$requestFrame.payloadSha256;wireSha256=$requestFrame.wireSha256;wireBase64=$exchange.request.wireBase64;canonical=$exchange.request.canonical;rawLine=$exchange.request.rawLine}
            response=[ordered]@{payloadUtf8Bytes=$responseFrame.payloadBytes;wireUtf8Bytes=$responseFrame.wireBytes;payloadSha256=$responseFrame.payloadSha256;wireSha256=$responseFrame.wireSha256;wireBase64=$exchange.response.wireBase64;canonical=$exchange.response.canonical;ok=$ok;rawLine=$exchange.response.rawLine}
        })
    }
    Check ($successfulTaskBegins -eq 1 -and $successfulTaskCommits -eq 1) 'actor successfully began and durably committed exactly one task'
    $testExecutions = if ($testBatches.Count -gt 0) { [long](@($testBatches | Measure-Object -Property executions -Sum)[0].Sum) } else { 0L }
    $testsFailed = if ($testBatches.Count -gt 0) { [long](@($testBatches | Measure-Object -Property failed -Sum)[0].Sum) } else { 0L }
    Check ($testExecutions -gt 0) 'trace contains actual runtime test results'
    Check ($requestBytes -le ($pin.maxRequestBytes * $exchanges.Count) -and $responseBytes -le ($pin.maxResponseBytes * $exchanges.Count)) 'aggregate byte totals equal bounded exchange history'
    $operationNames = Sort-OrdinalStrings @($exchanges | ForEach-Object { [string]$_.operation } | Select-Object -Unique)
    $callCountsByOperation = [ordered]@{}
    foreach ($operationName in $operationNames) { $callCountsByOperation[$operationName] = @($exchanges | Where-Object { $_.operation -ceq $operationName }).Count }

    $output = if ([string]::IsNullOrWhiteSpace($OutputPath)) { Join-Path $run 'trace-audit.json' } else { Get-FullPath $OutputPath }
    $runPrefix = $run.TrimEnd([char[]]@('\','/')) + [IO.Path]::DirectorySeparatorChar
    if (-not $output.StartsWith($runPrefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Audit output must remain inside this run directory.' }
    if (Test-Path -LiteralPath $output) { throw "Refusing to replace an existing trace audit: $output" }
    $helpTopics = Sort-OrdinalStrings @($helpQueries | Select-Object -ExpandProperty topic -Unique)
    $report = [ordered]@{
        schemaVersion = 1
        passed = $true
        studyId = $studyId
        runId = $pin.runId
        arm = $pin.arm
        block = $pin.block
        taskId = $pin.taskId
        sequenceIndex = $pin.sequenceIndex
        sourceRevision = $pin.sourceRevision
        sourceDirtyAtFreeze = $pin.dirty
        runtimeBuildSourceRevision = $pin.runtimeBuildSourceRevision
        globalFreezeSha256 = $pin.globalFreezeSha256
        prelaunchPath = $pinPath
        prelaunchSha256 = $pinHash
        startingStatePath = $statePath
        startingStateSha256 = $stateHash
        promptPath = $promptPath
        promptSha256 = $promptHash
        designSha256 = $pin.designSha256
        oracleSha256 = $pin.oracleSha256
        sourceArtifacts = @($pin.sourceArtifacts)
        cellOrder = @($design.cells)
        launchCommand = $launchCommand
        checks = @($checks)
        calls = $exchanges.Count
        exchanges = $exchanges.Count
        callCountsByOperation = $callCountsByOperation
        exchangeHistory = @($exchangeHistory)
        requestPayloadBytes = $requestBytes
        responsePayloadBytes = $responseBytes
        requestWireBytes = $requestWireBytes
        responseWireBytes = $responseWireBytes
        diagnostics = @($diagnostics)
        successfulSourceMutationRequests = $successfulMutations
        successfulTaskBegins = $successfulTaskBegins
        successfulTaskCommits = $successfulTaskCommits
        helpCalls = $helpQueries.Count
        helpTopicsQueried = $helpTopics
        helpQueries = @($helpQueries)
        testRequestBatches = @($testBatches)
        testExecutions = $testExecutions
        testsFailed = $testsFailed
        finalActorInventorySha256 = $finalActorTreeHash
        projectTreeSha256 = $finalActorTreeHash
        acceptancePath = Get-RepoPath $acceptanceFile
        acceptanceSha256 = $acceptanceHash
        acceptancePassed = [bool]$acceptance.passed
        acceptanceProjectTreeSha256 = $acceptanceProjectTreeHash
        traceSha256 = $traceHash
        sessionStart = $start
        sessionEnd = $end
        hostClose = [ordered]@{requestRaw=$close.requestRaw;requestCanonical=$close.requestCanonical;exchangeCount=$close.exchangeCount;requestWireUtf8Bytes=$close.requestWireUtf8Bytes;requestWireSha256=$close.requestWireSha256}
        genericTerminationAudit = $terminationResult
        declaredModel = [ordered]@{model=$pin.model;reasoningEffort=$pin.reasoningEffort;forkTurns=$pin.forkTurns}
        allowedOperations = $pin.allowedOperations
        hostLimits = [ordered]@{maxExchanges=$pin.maxExchanges;maxRequestBytes=$pin.maxRequestBytes;maxResponseBytes=$pin.maxResponseBytes;exchangeTimeoutMilliseconds=$pin.exchangeTimeoutMilliseconds}
        grantedCapabilities = @()
        additionalCliArguments = @()
        hostSideEffectCapabilities = @()
        baselineArchives = @($pin.baselineArchives | ForEach-Object { [ordered]@{kind=$_.kind;manifestPath=$_.manifestPath;manifestSha256=$_.manifestSha256;inventorySha256=$_.inventorySha256;fileCount=$_.files.Count;counts=$_.counts} })
        previousAcceptance = $pin.previousAcceptance
        retentionFallback = $pin.retentionFallback
        auditSha256 = Get-Sha256 $PSCommandPath
        limitations = @(
            'This is a complete prelaunch and protocol trace audit, not independent task acceptance.',
            'Model and reasoning settings are frozen declarations; this audit cannot independently prove which external agent runtime used them.',
            'The host trace does not contain coordinator acceptance events, so this audit cannot establish acceptance-before-close; the coordinator must record that ordering separately.',
            'The host trace does not echo the fixed clock argument, so this audit binds its generated launch-command declaration but cannot independently prove the external invocation used it.',
            'Protocol byte totals are not token usage, model turns, or effective context windows.'
        )
    }
    [IO.Directory]::CreateDirectory((Split-Path -Parent $output)) | Out-Null
    [IO.File]::WriteAllText($output,(ConvertTo-Json -InputObject $report -Depth 100) + "`n",[Text.UTF8Encoding]::new($false))
    Write-Output "Audited $studyId $($pin.runId) $($pin.block)/$($pin.arm)/$($pin.taskId): $($exchanges.Count) conclusive exchanges; explicit host.close; $testExecutions test results."
}
catch {
    [Console]::Error.WriteLine(('Retention trace audit failed: ' + $_.Exception.Message))
    exit 1
}
