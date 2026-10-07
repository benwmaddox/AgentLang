#requires -Version 7.0
[CmdletBinding(DefaultParameterSetName='Run')]
param(
    [Parameter(Mandatory,ParameterSetName='Run')]
    [ValidateSet('flat','retained','reset-rich')]
    [string]$Arm,

    [Parameter(Mandatory,ParameterSetName='Run')]
    [ValidateSet('B1','B2')]
    [string]$Block,

    [Parameter(Mandatory,ParameterSetName='Run')]
    [ValidateSet('S01','S07')]
    [string]$TaskId,

    [Parameter(Mandatory,ParameterSetName='Run')]
    [ValidatePattern('^R(?:0[1-9]|1[0-2])$')]
    [string]$RunId,

    [Parameter(ParameterSetName='Run')]
    [Parameter(ParameterSetName='Global')]
    [ValidateSet('gpt-6-luna')]
    [string]$Model = 'gpt-6-luna',

    [Parameter(ParameterSetName='Run')]
    [Parameter(ParameterSetName='Global')]
    [ValidateSet('max')]
    [string]$ReasoningEffort = 'max',

    [Parameter(ParameterSetName='Run')]
    [Parameter(ParameterSetName='Global')]
    [string]$CliDll,

    [Parameter(ParameterSetName='Run')]
    [Parameter(ParameterSetName='Global')]
    [string]$BusinessDll,

    [Parameter(Mandatory,ParameterSetName='Global')]
    [Parameter(Mandatory,ParameterSetName='Archive')]
    [ValidatePattern('^[0-9a-fA-F]{40,64}$')]
    [string]$RuntimeBuildSourceRevision,

    [Parameter(Mandatory,ParameterSetName='Archive')]
    [string]$BootstrapEvidencePath,

    [Parameter(ParameterSetName='Run')]
    [switch]$ControlOnly,

    [Parameter(Mandatory,ParameterSetName='Global')]
    [switch]$CreateGlobalManifest,

    [Parameter(Mandatory,ParameterSetName='Snapshots')]
    [switch]$CreateSourceSnapshots
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$studyId = 'business-policy-retention-003'
$studyPath = 'experiments/AgentLang.SubagentTrials/business-policy-retention-003'
$study = Join-Path $repo $studyPath
$localRoot = Join-Path $repo '.agentlang/business-policy-retention-003'
$archiveRoot = "$studyPath/artifacts/source-snapshot"
$globalManifestPath = "$studyPath/artifacts/global-freeze.json"

$allowedOperations = @(
    'callers','commit','context','define','dependencies','describe','diff','effects','eval','example','examples',
    'failed-tests','graph','help','history','ir','replace-word','search','search-dependency','search-output','search-type',
    'source','task.begin','task.commit','task.log','task.status','test','test-all','tests','transitive-callers',
    'transitive-dependencies','type-of','words'
)
[Array]::Sort($allowedOperations,[StringComparer]::Ordinal)

$hostContract = [ordered]@{
    hostProtocolVersion = 'subagent-trial-host-v2'
    transportControls = @('host.close')
    allowedOperations = $allowedOperations
    maxExchanges = 100
    maxRequestBytes = 262144
    maxResponseBytes = 524288
    exchangeTimeoutMilliseconds = 120000
    profile = 'agentlang'
    additionalCliArguments = @()
    capabilities = @()
    clockValue = '2000-01-01T00:00:00Z'
    maxInspectionResponseBytes = $null
    inspectionBudgetEnabled = $false
    inspectionClassifierVersion = 'trial-host-inspection-v1'
}

function Get-FullPath([string]$Path) {
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $repo $Path))
}

function Get-RepoPath([string]$Path) {
    $full = Get-FullPath $Path
    $relative = [IO.Path]::GetRelativePath($repo, $full)
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

function Read-Json([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $text = [Text.UTF8Encoding]::new($false, $true).GetString($bytes)
    $document = [Text.Json.JsonDocument]::Parse($text)
    try { return ,(Convert-JsonElement $document.RootElement) }
    finally { $document.Dispose() }
}

function Assert-NoReparseTree([string]$Path) {
    $root = Get-Item -LiteralPath $Path -Force
    if (($root.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse points are not allowed in frozen inputs: $Path" }
    foreach ($item in Get-ChildItem -LiteralPath $Path -Force -Recurse) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse points are not allowed in frozen inputs: $($item.FullName)" }
    }
}

function Get-ProjectInventory([string]$Path) {
    Assert-NoReparseTree $Path
    $rows = [Collections.Generic.List[object]]::new()
    foreach ($item in Get-ChildItem -LiteralPath $Path -File -Force -Recurse) {
        $relative = [IO.Path]::GetRelativePath($Path, $item.FullName).Replace('\','/')
        $segments = $relative.Split('/')
        if ($segments -contains 'bin' -or $segments -contains 'obj') { continue }
        $rows.Add([pscustomobject][ordered]@{
            path = $relative
            bytes = $item.Length
            sha256 = Get-Sha256 $item.FullName
        })
    }
    return Sort-OrdinalRows $rows.ToArray()
}

function Get-RuntimeInventory([string]$Runtime, [string]$Path) {
    Assert-NoReparseTree $Path
    $rows = [Collections.Generic.List[object]]::new()
    foreach ($item in Get-ChildItem -LiteralPath $Path -File -Force -Recurse) {
        $rows.Add([pscustomobject][ordered]@{
            runtime = $Runtime
            path = [IO.Path]::GetRelativePath($Path, $item.FullName).Replace('\','/')
            bytes = $item.Length
            sha256 = Get-Sha256 $item.FullName
        })
    }
    if ($rows.Count -eq 0) { throw "Pinned $Runtime runtime directory has no files: $Path" }
    return Sort-OrdinalRows $rows.ToArray() 'runtime' 'path'
}

function Get-CanonicalProjectRows([object[]]$Inventory) {
    $rows = @($Inventory | ForEach-Object { [pscustomobject][ordered]@{path=$_.path;bytes=[long]$_.bytes;sha256=([string]$_.sha256).ToLowerInvariant()} })
    return Sort-OrdinalRows $rows
}

function Get-InventoryHash([object[]]$Rows) {
    $canonical = ConvertTo-Json -InputObject $Rows -Depth 100 -Compress
    return Get-Sha256Bytes ([Text.UTF8Encoding]::new($false).GetBytes($canonical))
}

function Get-TreeHash([object[]]$Inventory) {
    $rows = Get-CanonicalProjectRows $Inventory
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
    $root = $archiveRoot
    return @(
        [pscustomobject][ordered]@{ path="$root/scripts/Prepare-RetentionTrial.ps1"; sourcePath='scripts/Prepare-RetentionTrial.ps1' },
        [pscustomobject][ordered]@{ path="$root/scripts/Freeze-RetentionTrial.ps1"; sourcePath='scripts/Freeze-RetentionTrial.ps1' },
        [pscustomobject][ordered]@{ path="$root/scripts/Verify-RetentionTrial.ps1"; sourcePath='scripts/Verify-RetentionTrial.ps1' },
        [pscustomobject][ordered]@{ path="$root/scripts/Verify-RetentionPreflight.ps1"; sourcePath='scripts/Verify-RetentionPreflight.ps1' },
        [pscustomobject][ordered]@{ path="$root/scripts/Audit-RetentionTrace.ps1"; sourcePath='scripts/Audit-RetentionTrace.ps1' },
        [pscustomobject][ordered]@{ path="$root/scripts/Prepare-BusinessPolicyTrial.ps1"; sourcePath='scripts/Prepare-BusinessPolicyTrial.ps1' },
        [pscustomobject][ordered]@{ path="$root/scripts/Start-SubagentTrialHostV2.ps1"; sourcePath='scripts/Start-SubagentTrialHostV2.ps1' },
        [pscustomobject][ordered]@{ path="$root/scripts/Audit-SubagentTrialTerminationV2.ps1"; sourcePath='scripts/Audit-SubagentTrialTerminationV2.ps1' },
        [pscustomobject][ordered]@{ path="$root/acceptance.json"; sourcePath="$studyPath/acceptance.json" },
        [pscustomobject][ordered]@{ path="$root/design.json"; sourcePath="$studyPath/design.json" },
        [pscustomobject][ordered]@{ path="$root/language-primer.md"; sourcePath="$studyPath/language-primer.md" },
        [pscustomobject][ordered]@{ path="$root/flat-customer.agent"; sourcePath="$studyPath/artifacts/flat-customer.agent" },
        [pscustomobject][ordered]@{ path="$root/acceptance-business-policy-001.json"; sourcePath='experiments/AgentLang.SubagentTrials/business-policy-001/acceptance.json' },
        [pscustomobject][ordered]@{ path="$root/acceptance-business-policy-help-002.json"; sourcePath='experiments/AgentLang.SubagentTrials/business-policy-help-002/acceptance.json' },
        [pscustomobject][ordered]@{ path="$root/language-primer-business-policy-help-002.md"; sourcePath='experiments/AgentLang.SubagentTrials/business-policy-help-002/language-primer.md' },
        [pscustomobject][ordered]@{ path="$root/public-task-S01.json"; sourcePath='experiments/AgentLang.Benchmarks/task-bank/public/S01.json' },
        [pscustomobject][ordered]@{ path="$root/public-task-S07.json"; sourcePath='experiments/AgentLang.Benchmarks/task-bank/public/S07.json' },
        [pscustomobject][ordered]@{ path="$root/examples/business-values.agent"; sourcePath='examples/business-values.agent' },
        [pscustomobject][ordered]@{ path="$root/examples/business-store.agent"; sourcePath='examples/business-store.agent' },
        [pscustomobject][ordered]@{ path="$root/examples/business-state.agent"; sourcePath='examples/business-state.agent' },
        [pscustomobject][ordered]@{ path="$root/examples/business-subscriptions.agent"; sourcePath='examples/business-subscriptions.agent' },
        [pscustomobject][ordered]@{ path="$root/examples/business-invoices.agent"; sourcePath='examples/business-invoices.agent' },
        [pscustomobject][ordered]@{ path="$root/examples/business-payments-email.agent"; sourcePath='examples/business-payments-email.agent' }
    )
}

function Get-SourceArtifactRows([string]$Revision, [bool]$AllowDirty) {
    $rows = [Collections.Generic.List[object]]::new()
    foreach ($entry in (Get-SourceArchiveMap)) {
        $snapshotPath = Get-FullPath $entry.path
        $sourcePath = Get-FullPath $entry.sourcePath
        foreach ($path in @($snapshotPath,$sourcePath)) {
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required frozen source artifact is missing: $path" }
        }
        $snapshotBytes = [IO.File]::ReadAllBytes($snapshotPath)
        $sourceBytes = [IO.File]::ReadAllBytes($sourcePath)
        $snapshotHash = Get-Sha256Bytes $snapshotBytes
        $sourceHash = Get-Sha256Bytes $sourceBytes
        if ($snapshotBytes.Length -ne $sourceBytes.Length -or $snapshotHash -cne $sourceHash) { throw "Source snapshot differs from its working source: $($entry.sourcePath)" }
        $committedSnapshot = Get-GitSnapshotBytes $Revision $entry.path
        $committedSource = Get-GitSnapshotBytes $Revision $entry.sourcePath
        $snapshotCommitHash = Get-Sha256Bytes $committedSnapshot
        $sourceCommitHash = Get-Sha256Bytes $committedSource
        if ($committedSnapshot.Length -ne $snapshotBytes.Length -or $snapshotCommitHash -cne $snapshotHash -or
            $committedSource.Length -ne $sourceBytes.Length -or $sourceCommitHash -cne $sourceHash) {
            if (-not $AllowDirty) { throw "Source snapshot does not match the exact committed Git bytes: $($entry.sourcePath)" }
        }
        $rows.Add([pscustomobject][ordered]@{
            path = $entry.path
            sourcePath = $entry.sourcePath
            bytes = $sourceBytes.Length
            sha256 = $sourceHash
            gitBlobSha256 = $snapshotCommitHash
            sourceGitBlobSha256 = $sourceCommitHash
        })
    }
    return @($rows.ToArray())
}

function New-SourceSnapshots {
    $head = (& git -C $repo rev-parse HEAD | Out-String).Trim()
    if ($head -notmatch '^[0-9a-fA-F]{40,64}$') { throw 'Git HEAD is unavailable for source snapshot creation.' }
    $targetRoot = Get-FullPath $archiveRoot
    if (Test-Path -LiteralPath $targetRoot) { throw "Refusing to replace an existing source snapshot tree: $targetRoot" }
    $map = @(Get-SourceArchiveMap)
    $inputs = [Collections.Generic.List[object]]::new()
    foreach ($entry in $map) {
        $sourcePath = Get-FullPath $entry.sourcePath
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw "Required source artifact is missing: $($entry.sourcePath)" }
        $workingBytes = [IO.File]::ReadAllBytes($sourcePath)
        $committedBytes = Get-GitSnapshotBytes $head $entry.sourcePath
        if ($workingBytes.Length -ne $committedBytes.Length -or (Get-Sha256Bytes $workingBytes) -cne (Get-Sha256Bytes $committedBytes)) {
            throw "Source snapshot input is not byte-identical to committed HEAD: $($entry.sourcePath)"
        }
        $inputs.Add([pscustomobject]@{entry=$entry;bytes=$workingBytes})
    }
    $stage = "$targetRoot.$([guid]::NewGuid().ToString('N')).tmp"
    [IO.Directory]::CreateDirectory($stage) | Out-Null
    try {
        foreach ($snapshotInput in $inputs) {
            $relative = [IO.Path]::GetRelativePath($targetRoot,(Get-FullPath $snapshotInput.entry.path))
            if ($relative -eq '..' -or $relative.StartsWith('..' + [IO.Path]::DirectorySeparatorChar,[StringComparison]::Ordinal) -or [IO.Path]::IsPathRooted($relative)) {
                throw "Source snapshot destination escapes its root: $($snapshotInput.entry.path)"
            }
            $destination = Join-Path $stage $relative
            [IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
            [IO.File]::WriteAllBytes($destination,[byte[]]$snapshotInput.bytes)
            if ((Get-Sha256 $destination) -cne (Get-Sha256Bytes ([byte[]]$snapshotInput.bytes)) -or (Get-Item -LiteralPath $destination).Length -ne $snapshotInput.bytes.Length) {
                throw "Source snapshot copy failed byte verification: $($snapshotInput.entry.sourcePath)"
            }
        }
        [IO.Directory]::Move($stage,$targetRoot)
    }
    finally {
        if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    }
    Write-Output "Created $($inputs.Count) immutable source snapshots from committed HEAD $head."
}

function Get-BaselineArchive([string]$Kind, [string]$Revision, [bool]$AllowDirty) {
    $manifestRelative = "$studyPath/artifacts/baselines/$Kind/manifest.json"
    $projectRelative = "$studyPath/artifacts/baselines/$Kind/project"
    $manifestPath = Get-FullPath $manifestRelative
    $projectPath = Get-FullPath $projectRelative
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or -not (Test-Path -LiteralPath $projectPath -PathType Container)) {
        throw "Frozen $Kind baseline archive is missing its manifest or project tree."
    }
    $manifest = Read-Json $manifestPath
    if ($manifest.schemaVersion -ne 1 -or $manifest.studyId -cne $studyId -or $manifest.kind -cne $Kind) { throw "Frozen $Kind baseline manifest identity is invalid." }
    $expectedMode = if ($Kind -eq 'flat') { 'flat' } else { 'growing' }
    $expectedCounts = if ($Kind -eq 'flat') { @{words=0;types=6;tests=0} } else { @{words=53;types=31;tests=151} }
    if ($manifest.mode -cne $expectedMode -or $manifest.counts.words -ne $expectedCounts.words -or
        $manifest.counts.types -ne $expectedCounts.types -or $manifest.counts.tests -ne $expectedCounts.tests) {
        throw "Frozen $Kind baseline manifest mode or word/type/test counts are invalid."
    }
    $inventory = Get-ProjectInventory $projectPath
    $actualRows = Get-CanonicalProjectRows $inventory
    $manifestRows = Sort-OrdinalRows @($manifest.files | ForEach-Object { [pscustomobject][ordered]@{path=$_.path;bytes=$_.bytes;sha256=$_.sha256} })
    if ((ConvertTo-Json -InputObject $actualRows -Depth 100 -Compress) -cne (ConvertTo-Json -InputObject $manifestRows -Depth 100 -Compress)) {
        throw "Frozen $Kind baseline files differ from its manifest inventory."
    }
    $canonicalRows = Get-CanonicalProjectRows $inventory
    $inventoryHash = Get-InventoryHash $canonicalRows
    if ($inventoryHash -cne $manifest.inventorySha256) { throw "Frozen $Kind baseline inventory hash is invalid." }
    $manifestHash = Get-Sha256 $manifestPath
    $manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
    $commitManifestBytes = Get-GitSnapshotBytes $Revision $manifestRelative
    if ((Get-Sha256Bytes $commitManifestBytes) -cne $manifestHash -or $commitManifestBytes.Length -ne $manifestBytes.Length) {
        if (-not $AllowDirty) { throw "Frozen $Kind baseline manifest is not present byte-for-byte in the committed revision." }
    }
    foreach ($file in $manifestRows) {
        $relative = "$projectRelative/$($file.path)"
        $committed = Get-GitSnapshotBytes $Revision $relative
        if ($committed.Length -ne $file.bytes -or (Get-Sha256Bytes $committed) -cne $file.sha256) {
            if (-not $AllowDirty) { throw "Frozen $Kind baseline file does not match its committed blob: $relative" }
        }
    }
    $sourceInputs = Sort-OrdinalRows @($manifest.sourceInputs | ForEach-Object { [pscustomobject][ordered]@{path=$_.path;sha256=$_.sha256} })
    $expectedInputs = if ($Kind -eq 'flat') {
        @('experiments/AgentLang.SubagentTrials/business-policy-retention-003/artifacts/flat-customer.agent')
    } else {
        @('examples/business-values.agent','examples/business-store.agent','examples/business-state.agent','examples/business-subscriptions.agent','examples/business-invoices.agent','examples/business-payments-email.agent')
    }
    if (($sourceInputs.path -join '|') -cne ((Sort-OrdinalStrings $expectedInputs) -join '|')) { throw "Frozen $Kind baseline source-input set is invalid." }
    foreach ($input in $sourceInputs) {
        $inputPath = Get-FullPath $input.path
        if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf) -or (Get-Sha256 $inputPath) -cne $input.sha256) { throw "Frozen $Kind baseline source input changed: $($input.path)" }
        $gitBytes = Get-GitSnapshotBytes $Revision $input.path
        if ((Get-Sha256Bytes $gitBytes) -cne $input.sha256) {
            if (-not $AllowDirty) { throw "Frozen $Kind source input is not bound to the pinned Git revision: $($input.path)" }
        }
    }
    if ($manifest.bootstrapperPath -cne 'scripts/Prepare-BusinessPolicyTrial.ps1' -or
        $manifest.bootstrapperSha256 -cne (Get-Sha256 (Get-FullPath 'scripts/Prepare-BusinessPolicyTrial.ps1')) -or
        $manifest.runtime.buildSourceRevision -notmatch '^[0-9a-fA-F]{40,64}$') {
        throw "Frozen $Kind baseline build provenance is incomplete or changed."
    }
    return [pscustomobject][ordered]@{
        kind = $Kind
        manifestPath = $manifestRelative
        manifestSha256 = $manifestHash
        projectPath = $projectRelative
        files = $manifestRows
        inventorySha256 = $inventoryHash
        treeSha256 = Get-TreeHash $inventory
        sourceInputs = $sourceInputs
        counts = $manifest.counts
        runtime = $manifest.runtime
        seedStateSha256 = $manifest.seedStateSha256
        bootstrapperPath = $manifest.bootstrapperPath
        bootstrapperSha256 = $manifest.bootstrapperSha256
    }
}

function New-BaselineArchives {
    $evidenceRelative = Get-RepoPath $BootstrapEvidencePath
    if ($evidenceRelative -notmatch '^(\.agentlang/business-policy-retention-003/|reports/evidence/081-preparation-bootstrap-)') {
        throw 'Bootstrap evidence must be a study-local .agentlang file or a 081 BootstrapOnly evidence file inside the repository.'
    }
    $evidencePath = Get-FullPath $BootstrapEvidencePath
    if (-not (Test-Path -LiteralPath $evidencePath -PathType Leaf)) { throw "BootstrapOnly evidence is missing: $evidencePath" }
    $evidence = Read-Json $evidencePath
    $result = Get-Property $evidence 'result'
    if ($null -eq $result -or $evidence.passed -ne $true -or $result.bootstrapOnly -ne $true -or
        $evidence.studyId -cne $studyId -or $result.studyId -cne $studyId -or $evidence.dirty -ne $false -or $result.dirty -ne $false) {
        throw 'Baseline archiving requires a passing clean BootstrapOnly result for this study.'
    }
    $revision = [string]$result.sourceRevision
    if ($revision -notmatch '^[0-9a-fA-F]{40,64}$' -or $evidence.sourceRevision -cne $revision) { throw 'BootstrapOnly source revision is invalid or inconsistent.' }
    $null = & git -C $repo cat-file -e "$revision^{commit}" 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'BootstrapOnly source revision does not resolve to a local commit.' }
    $preparerPath = Get-FullPath 'scripts/Prepare-RetentionTrial.ps1'
    $preparerHash = Get-Sha256 $preparerPath
    $preparerGitHash = Get-Sha256Bytes (Get-GitSnapshotBytes $revision 'scripts/Prepare-RetentionTrial.ps1')
    if ($preparerHash -cne $evidence.preparerSha256 -or $preparerGitHash -cne $preparerHash) { throw 'BootstrapOnly evidence preparer is not bound to committed source bytes.' }
    Assert-StudyOracle $revision @()
    foreach ($binding in @(
        @{name='oracle';path="$studyPath/acceptance.json";hash=$result.oracleSha256},
        @{name='design';path="$studyPath/design.json";hash=$result.designSha256},
        @{name='primer';path="$studyPath/language-primer.md";hash=$result.primerSha256}
    )) {
        if ((Get-Sha256 (Get-FullPath $binding.path)) -cne $binding.hash -or
            (Get-Sha256Bytes (Get-GitSnapshotBytes $revision $binding.path)) -cne $binding.hash) {
            throw "BootstrapOnly $($binding.name) is not bound to the clean source revision."
        }
    }
    if ((Get-Sha256 (Get-FullPath 'experiments/AgentLang.SubagentTrials/business-policy-help-002/language-primer.md')) -cne $result.primerSha256) {
        throw 'BootstrapOnly language primer does not preserve the exact archived 002 help bytes.'
    }
    $baselines = Get-Property $result 'baselines'
    if ($null -eq $baselines) { throw 'BootstrapOnly result lacks the flat/rich baseline records.' }
    $countsByKind = @{flat=@{words=0;types=6;tests=0};rich=@{words=53;types=31;tests=151}}
    $expectedInputsByKind = @{
        flat=@('experiments/AgentLang.SubagentTrials/business-policy-retention-003/artifacts/flat-customer.agent')
        rich=@('examples/business-values.agent','examples/business-store.agent','examples/business-state.agent','examples/business-subscriptions.agent','examples/business-invoices.agent','examples/business-payments-email.agent')
    }
    $flatStagingSourcePath = '.agentlang/business-policy-retention-003/bootstrap/flat/flat-customer.agent'
    $prepared = [Collections.Generic.List[object]]::new()
    foreach ($kind in @('flat','rich')) {
        $baseline = Get-Property $baselines $kind
        $mode = if ($kind -eq 'flat') { 'flat' } else { 'growing' }
        $expectedSeed = Get-FullPath ".agentlang/business-policy-retention-003/bootstrap/$mode/seeds/$mode/project"
        $expectedState = Join-Path (Split-Path -Parent $expectedSeed) 'seed-state.json'
        $projectPath = Get-FullPath ([string]$baseline.path)
        $statePath = Get-FullPath ([string]$baseline.statePath)
        if ($baseline.kind -cne $kind -or $baseline.mode -cne $mode -or $projectPath -cne $expectedSeed -or $statePath -cne $expectedState) {
            throw "$kind bootstrap tree is not the canonical preserved seed; scratch validation trees cannot be archived."
        }
        if (-not (Test-Path -LiteralPath $projectPath -PathType Container) -or -not (Test-Path -LiteralPath $statePath -PathType Leaf)) { throw "$kind canonical bootstrap tree or seed state is missing." }
        Assert-NoReparseTree $projectPath
        $seedState = Read-Json $statePath
        $expectedCounts = $countsByKind[$kind]
        if ($baseline.seedStateSha256 -cne (Get-Sha256 $statePath) -or $seedState.schemaVersion -ne 1 -or $seedState.mode -cne $mode -or
            $seedState.counts.authoredWords -ne $expectedCounts.words -or $seedState.counts.types -ne $expectedCounts.types -or $seedState.counts.tests -ne $expectedCounts.tests) {
            throw "$kind seed-state identity, hash, or expected counts differ from BootstrapOnly evidence."
        }
        $rows = Get-ProjectInventory $projectPath
        $recordedRows = Sort-OrdinalRows @($baseline.files | ForEach-Object { [pscustomobject][ordered]@{path=$_.path;bytes=[long]$_.bytes;sha256=([string]$_.sha256).ToLowerInvariant()} })
        $inventoryHash = Get-InventoryHash $rows
        if ((ConvertTo-Json -InputObject $rows -Depth 100 -Compress) -cne (ConvertTo-Json -InputObject $recordedRows -Depth 100 -Compress) -or
            $inventoryHash -cne $baseline.inventorySha256) { throw "$kind canonical seed tree differs from its BootstrapOnly full inventory/hash." }
        $sourceInputs = Sort-OrdinalRows @($baseline.sourceInputs | ForEach-Object { [pscustomobject][ordered]@{path=$_.path;sha256=([string]$_.sha256).ToLowerInvariant()} })
        if (($sourceInputs.path -join '|') -cne ((Sort-OrdinalStrings $expectedInputsByKind[$kind]) -join '|')) { throw "$kind baseline source input set is invalid." }
        $seedInputs = Sort-OrdinalRows @($seedState.sourceInputs | ForEach-Object { [pscustomobject][ordered]@{path=$_.path;sha256=([string]$_.sha256).ToLowerInvariant()} })
        if ($kind -ceq 'flat') {
            $canonicalFlatInput = @($sourceInputs | Where-Object { $_.path -ceq 'experiments/AgentLang.SubagentTrials/business-policy-retention-003/artifacts/flat-customer.agent' })
            $stagedFlatInput = @($seedInputs | Where-Object { $_.path -ceq $flatStagingSourcePath })
            $stagingPath = Get-FullPath $flatStagingSourcePath
            $canonicalPath = Get-FullPath 'experiments/AgentLang.SubagentTrials/business-policy-retention-003/artifacts/flat-customer.agent'
            if ($sourceInputs.Count -ne 1 -or $canonicalFlatInput.Count -ne 1 -or $seedInputs.Count -ne 1 -or $stagedFlatInput.Count -ne 1) {
                throw 'Flat seed-state source input must be the one exact bootstrap staging file bound to the one canonical flat artifact.'
            }
            if ($seedInputs[0].path -cne $flatStagingSourcePath -or
                -not (Test-Path -LiteralPath $stagingPath -PathType Leaf) -or
                -not (Test-Path -LiteralPath $canonicalPath -PathType Leaf)) {
                throw 'Flat seed-state source input path is not the exact whitelisted bootstrap staging path or its canonical source is missing.'
            }
            $stagingHash = Get-Sha256 $stagingPath
            $canonicalCurrentHash = Get-Sha256 $canonicalPath
            $canonicalGitHash = Get-Sha256Bytes (Get-GitSnapshotBytes $revision 'experiments/AgentLang.SubagentTrials/business-policy-retention-003/artifacts/flat-customer.agent')
            if ($seedInputs[0].sha256 -cne $stagingHash -or
                $stagingHash -cne $canonicalFlatInput[0].sha256 -or
                $canonicalCurrentHash -cne $canonicalFlatInput[0].sha256 -or
                $canonicalGitHash -cne $canonicalFlatInput[0].sha256) {
                throw 'Flat bootstrap staging bytes, seed-state hash, canonical artifact bytes, and committed source blob must all match.'
            }
        } elseif ((ConvertTo-Json -InputObject $seedInputs -Depth 100 -Compress) -cne (ConvertTo-Json -InputObject $sourceInputs -Depth 100 -Compress)) {
            throw "$kind seed-state source inputs differ from BootstrapOnly evidence."
        }
        foreach ($sourceInput in $sourceInputs) {
            $sourcePath = Get-FullPath $sourceInput.path
            if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf) -or (Get-Sha256 $sourcePath) -cne $sourceInput.sha256 -or
                (Get-Sha256Bytes (Get-GitSnapshotBytes $revision $sourceInput.path)) -cne $sourceInput.sha256) {
                throw "$kind baseline source input differs from committed bytes: $($sourceInput.path)"
            }
        }
        if ($baseline.counts.words -ne $expectedCounts.words -or $baseline.counts.types -ne $expectedCounts.types -or $baseline.counts.tests -ne $expectedCounts.tests -or
            $baseline.bootstrapperPath -cne 'scripts/Prepare-BusinessPolicyTrial.ps1' -or
            $baseline.bootstrapperSha256 -cne (Get-Sha256 (Get-FullPath 'scripts/Prepare-BusinessPolicyTrial.ps1')) -or
            (Get-Sha256Bytes (Get-GitSnapshotBytes $revision 'scripts/Prepare-BusinessPolicyTrial.ps1')) -cne $baseline.bootstrapperSha256) {
            throw "$kind baseline counts or bootstrapper provenance are invalid."
        }
        $runtime = $baseline.runtime
        if ($runtime.buildSourceRevision -cne $RuntimeBuildSourceRevision.ToLowerInvariant()) { throw "$kind baseline runtime build revision differs from the requested fresh build pin." }
        if ($seedState.runtime.cliSha256 -cne $runtime.cliSha256 -or $seedState.runtime.businessSha256 -cne $runtime.businessSha256 -or
            $seedState.runtime.cliPath -cne $runtime.cliPath -or $seedState.runtime.businessPath -cne $runtime.businessPath) {
            throw "$kind seed-state runtime paths or hashes differ from BootstrapOnly evidence."
        }
        $runtimeInfo = Get-RuntimeInputs $runtime $revision $false
        Assert-RuntimeSourceUnchanged $RuntimeBuildSourceRevision $revision
        Assert-RuntimeBuildProvenance $runtimeInfo $RuntimeBuildSourceRevision
        $manifest = [ordered]@{
            schemaVersion = 1
            studyId = $studyId
            kind = $kind
            mode = $mode
            inventorySha256 = $inventoryHash
            files = $rows
            sourceInputs = $sourceInputs
            counts = [ordered]@{words=[long]$expectedCounts.words;types=[long]$expectedCounts.types;tests=[long]$expectedCounts.tests}
            runtime = [ordered]@{
                cliPath = $runtime.cliPath
                cliSha256 = ([string]$runtime.cliSha256).ToLowerInvariant()
                businessPath = $runtime.businessPath
                businessSha256 = ([string]$runtime.businessSha256).ToLowerInvariant()
                buildSourceRevision = ([string]$runtime.buildSourceRevision).ToLowerInvariant()
            }
            seedStateSha256 = ([string]$baseline.seedStateSha256).ToLowerInvariant()
            bootstrapperPath = [string]$baseline.bootstrapperPath
            bootstrapperSha256 = ([string]$baseline.bootstrapperSha256).ToLowerInvariant()
            bootstrapLegacyInventorySha256 = ([string]$baseline.bootstrapLegacyInventorySha256).ToLowerInvariant()
        }
        $prepared.Add([pscustomobject][ordered]@{kind=$kind;projectPath=$projectPath;rows=$rows;manifest=$manifest})
    }
    if ((ConvertTo-Json -InputObject $prepared[0].manifest.runtime -Depth 100 -Compress) -cne (ConvertTo-Json -InputObject $prepared[1].manifest.runtime -Depth 100 -Compress)) {
        throw 'Flat and rich BootstrapOnly baseline runtime pins differ.'
    }
    $archiveBase = Get-FullPath "$studyPath/artifacts/baselines"
    foreach ($record in $prepared) {
        $target = Join-Path $archiveBase $record.kind
        if (Test-Path -LiteralPath $target) { throw "Refusing to replace an existing baseline archive: $target" }
    }
    $stageRoot = "$archiveBase.$([guid]::NewGuid().ToString('N')).tmp"
    [IO.Directory]::CreateDirectory($stageRoot) | Out-Null
    try {
        foreach ($record in $prepared) {
            $stageProject = Join-Path (Join-Path $stageRoot $record.kind) 'project'
            [IO.Directory]::CreateDirectory($stageProject) | Out-Null
            foreach ($row in $record.rows) {
                $relative = [string]$row.path
                $source = Join-Path $record.projectPath $relative.Replace('/',[IO.Path]::DirectorySeparatorChar)
                $destination = Join-Path $stageProject $relative.Replace('/',[IO.Path]::DirectorySeparatorChar)
                [IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
                [IO.File]::Copy($source,$destination,$false)
                if ((Get-Item -LiteralPath $destination).Length -ne $row.bytes -or (Get-Sha256 $destination) -cne $row.sha256) { throw "Archived $($record.kind) project file failed byte verification: $relative" }
            }
            $manifestBytes = [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $record.manifest -Depth 100) + "`n")
            [IO.File]::WriteAllBytes((Join-Path (Join-Path $stageRoot $record.kind) 'manifest.json'),$manifestBytes)
        }
        [IO.Directory]::CreateDirectory($archiveBase) | Out-Null
        foreach ($record in $prepared) {
            [IO.Directory]::Move((Join-Path $stageRoot $record.kind),(Join-Path $archiveBase $record.kind))
        }
    }
    finally { if (Test-Path -LiteralPath $stageRoot) { Remove-Item -LiteralPath $stageRoot -Recurse -Force } }
    Write-Output "Archived canonical flat and rich BootstrapOnly seeds with full-row SHA-256 inventory hashes."
}

function Get-RunIdentity([string]$A, [string]$B, [string]$T) {
    $armOrder = if ($B -eq 'B1') { @('flat','retained','reset-rich') } else { @('reset-rich','flat','retained') }
    $armPosition = [array]::IndexOf($armOrder,$A)
    $taskPosition = if ($T -eq 'S01') { 0 } elseif ($T -eq 'S07') { 1 } else { -1 }
    if ($armPosition -lt 0 -or $taskPosition -lt 0) { throw 'Invalid arm/block/task identity.' }
    $blockOffset = if ($B -eq 'B1') { 0 } else { 6 }
    return ('R{0:D2}' -f ($blockOffset + $armPosition * 2 + $taskPosition + 1))
}

function Assert-StudyOracle([string]$Revision, [object[]]$SourceArtifacts) {
    $oraclePath = Join-Path $repo "$studyPath/acceptance.json"
    $priorPath = Join-Path $repo 'experiments/AgentLang.SubagentTrials/business-policy-001/acceptance.json'
    $oracle = Read-Json $oraclePath
    $prior = Read-Json $priorPath
    if ($oracle.studyId -cne $studyId) { throw 'Retention oracle has the wrong studyId.' }
    $oracleBytes = [IO.File]::ReadAllBytes($oraclePath)
    $priorBytes = [IO.File]::ReadAllBytes($priorPath)
    if ($null -ne $oracle.predecessorSha256 -and $oracle.predecessorSha256 -cne (Get-Sha256Bytes $priorBytes)) { throw 'Retention oracle predecessor hash does not match business-policy-001.' }
    if ($null -ne $oracle.defaults -and $null -ne $prior.defaults -and
        (ConvertTo-Json -InputObject $oracle.defaults -Depth 100 -Compress) -cne (ConvertTo-Json -InputObject $prior.defaults -Depth 100 -Compress)) {
        throw 'Retention oracle defaults differ from business-policy-001.'
    }
    $primerPath = Get-FullPath "$studyPath/language-primer.md"
    $sharedPrimerPath = Get-FullPath 'experiments/AgentLang.SubagentTrials/business-policy-help-002/language-primer.md'
    $primerBytes = [IO.File]::ReadAllBytes($primerPath)
    $sharedPrimerBytes = [IO.File]::ReadAllBytes($sharedPrimerPath)
    if ($primerBytes.Length -ne $sharedPrimerBytes.Length -or (Get-Sha256Bytes $primerBytes) -cne (Get-Sha256Bytes $sharedPrimerBytes)) {
        throw 'Retention help primer must preserve the exact archived 002 bytes.'
    }
    $expectedIds = @('S01','S07')
    foreach ($task in $expectedIds) {
        $expectedCount = if ($task -eq 'S01') { 10 } else { 54 }
        $priorTask = @($prior.tasks | Where-Object { $_.id -ceq $task })
        $currentTask = @($oracle.tasks | Where-Object { $_.id -ceq $task })
        if ($priorTask.Count -ne 1 -or $currentTask.Count -ne 1 -or $currentTask[0].cases.Count -ne $expectedCount -or
            (ConvertTo-Json -InputObject $priorTask[0].cases -Depth 100 -Compress) -cne (ConvertTo-Json -InputObject $currentTask[0].cases -Depth 100 -Compress)) {
            throw "Retention oracle $task cases must remain exactly equal to business-policy-001 ($expectedCount cases)."
        }
    }
    $taskIds = Sort-OrdinalStrings @($oracle.tasks | ForEach-Object { [string]$_.id })
    if (($taskIds -join '|') -cne 'S01|S07') { throw 'Retention oracle must contain exactly S01 and S07.' }
    $design = Read-Json (Join-Path $repo "$studyPath/design.json")
    if ($design.schemaVersion -ne 1 -or $design.studyId -cne $studyId -or ($design.sequence -join '|') -cne 'S01|S07') { throw 'Retention design identity or task sequence is invalid.' }
    $cells = @($design.cells)
    if ($cells.Count -ne 12) { throw 'Retention design must preregister exactly twelve cells.' }
    $expectedCells = [Collections.Generic.List[string]]::new()
    foreach ($block in @('B1','B2')) {
        $armOrder = if ($block -eq 'B1') { @('flat','retained','reset-rich') } else { @('reset-rich','flat','retained') }
        foreach ($arm in $armOrder) {
            foreach ($taskId in @('S01','S07')) {
                $expectedRunId = Get-RunIdentity $arm $block $taskId
                $expectedCells.Add("$expectedRunId|$block|$arm|$taskId")
            }
        }
    }
    $actualCells = [Collections.Generic.List[string]]::new()
    foreach ($cell in $cells) {
        $expectedRunId = Get-RunIdentity ([string]$cell.arm) ([string]$cell.block) ([string]$cell.taskId)
        $expectedSequence = if ($cell.taskId -eq 'S01') { 1 } else { 2 }
        if ($cell.runId -cne $expectedRunId -or $cell.sequenceIndex -ne $expectedSequence) { throw "Retention design has a noncanonical cell: $($cell.runId)." }
        $actualCells.Add("$($cell.runId)|$($cell.block)|$($cell.arm)|$($cell.taskId)")
    }
    if (($actualCells.ToArray() -join '|') -cne ($expectedCells.ToArray() -join '|')) { throw 'Retention design cells do not use the preregistered block arm order and S01/S07 sequence.' }
    $blocks = @($design.blocks)
    if ($blocks.Count -ne 2 -or $blocks[0].block -cne 'B1' -or $blocks[1].block -cne 'B2' -or
        ($blocks[0].armOrder -join '|') -cne 'flat|retained|reset-rich' -or ($blocks[1].armOrder -join '|') -cne 'reset-rich|flat|retained') {
        throw 'Retention design block arm orders do not match the preregistered rotation.'
    }
}

function Get-RuntimeInputs([object]$Runtime, [string]$Revision, [bool]$AllowDirty) {
    $cliPath = Get-FullPath ([string]$Runtime.cliPath)
    $businessPath = Get-FullPath ([string]$Runtime.businessPath)
    if (-not (Test-Path -LiteralPath $cliPath -PathType Leaf) -or -not (Test-Path -LiteralPath $businessPath -PathType Leaf)) { throw 'Prepared CLI or Business runtime is missing.' }
    if ((Get-Sha256 $cliPath) -cne $Runtime.cliSha256 -or (Get-Sha256 $businessPath) -cne $Runtime.businessSha256) { throw 'Prepared CLI or Business runtime changed before freeze.' }
    if (-not [string]::IsNullOrWhiteSpace($CliDll) -and (Get-FullPath $CliDll) -cne $cliPath) { throw '-CliDll differs from the prepared CLI path.' }
    if (-not [string]::IsNullOrWhiteSpace($BusinessDll) -and (Get-FullPath $BusinessDll) -cne $businessPath) { throw '-BusinessDll differs from the prepared Business path.' }
    $cliDirectory = Split-Path -Parent $cliPath
    $businessDirectory = Split-Path -Parent $businessPath
    $runtimeRoots = [pscustomobject][ordered]@{
        cliDllPath = Get-RepoPath $cliPath
        cliDirectoryPath = Get-RepoPath $cliDirectory
        businessDllPath = Get-RepoPath $businessPath
        businessDirectoryPath = Get-RepoPath $businessDirectory
    }
    $runtimeFiles = Sort-OrdinalRows @((Get-RuntimeInventory 'cli' $cliDirectory) + (Get-RuntimeInventory 'business' $businessDirectory)) 'runtime' 'path'
    return [pscustomobject][ordered]@{ runtimeRoots=$runtimeRoots; runtimeFiles=$runtimeFiles; cliPath=$cliPath; businessPath=$businessPath }
}

function Get-AssemblyInformationalVersion([string]$Path) {
    $assembly = [Reflection.Assembly]::LoadFrom($Path)
    $attribute = @($assembly.GetCustomAttributesData() | Where-Object { $_.AttributeType.FullName -ceq 'System.Reflection.AssemblyInformationalVersionAttribute' })
    if ($attribute.Count -ne 1 -or $attribute[0].ConstructorArguments.Count -ne 1) { throw "Runtime assembly lacks a single informational version: $Path" }
    return [string]$attribute[0].ConstructorArguments[0].Value
}

function Assert-RuntimeBuildProvenance([object]$RuntimeInfo, [string]$BuildRevision) {
    $build = $BuildRevision.ToLowerInvariant()
    $paths = @($RuntimeInfo.cliPath,$RuntimeInfo.businessPath)
    foreach ($root in @($RuntimeInfo.runtimeRoots.cliDirectoryPath,$RuntimeInfo.runtimeRoots.businessDirectoryPath)) {
        $core = Join-Path (Get-FullPath $root) 'AgentLang.Core.dll'
        if (Test-Path -LiteralPath $core -PathType Leaf) { $paths += $core }
    }
    foreach ($path in @($paths | Select-Object -Unique)) {
        $informational = Get-AssemblyInformationalVersion $path
        if ($informational -notmatch '\+([0-9a-fA-F]{40,64})$' -or $Matches[1].ToLowerInvariant() -cne $build) {
            throw "Runtime assembly does not carry the required fresh-build commit $build in AssemblyInformationalVersion: $path ($informational)"
        }
    }
}

function Assert-RuntimeSourceUnchanged([string]$BuildRevision, [string]$StudyRevision) {
    $exists = & git -C $repo cat-file -e "$BuildRevision^{commit}" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Runtime build source revision is not a local Git commit: $BuildRevision" }
    $changed = @(& git -C $repo diff --name-only --no-renames $BuildRevision $StudyRevision -- src experiments/AgentLang.Business Directory.Build.props Directory.Build.targets '*.props' '*.targets')
    if ($LASTEXITCODE -ne 0) { throw 'Could not compare runtime source between its build commit and the study source revision.' }
    if ($changed.Count -gt 0) { throw ('Runtime source changed after the pinned fresh build: ' + ($changed -join ', ')) }
}

function Write-AtomicNewFile([string]$Path, [byte[]]$Bytes) {
    if (Test-Path -LiteralPath $Path) { throw "Refusing to replace a frozen file: $Path" }
    $directory = Split-Path -Parent $Path
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $temporary = "$Path.$([guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllBytes($temporary,$Bytes)
        [IO.File]::Move($temporary,$Path)
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
}

function Quote-PowerShell([string]$Value) { return "'" + $Value.Replace("'","''") + "'" }

function New-GlobalManifest {
    $head = (& git -C $repo rev-parse HEAD | Out-String).Trim()
    if ([string]::IsNullOrWhiteSpace($head)) { throw 'Git HEAD is unavailable.' }
    $changes = (& git -C $repo status --porcelain --untracked-files=all | Out-String)
    if (-not [string]::IsNullOrWhiteSpace($changes)) { throw 'The global freeze requires a clean committed source tree.' }
    Assert-RuntimeSourceUnchanged $RuntimeBuildSourceRevision $head
    $target = Get-FullPath $globalManifestPath
    if (Test-Path -LiteralPath $target) { throw "Refusing to replace the global freeze manifest: $target" }
    foreach ($required in @("$studyPath/acceptance.json","$studyPath/design.json","$studyPath/language-primer.md","$studyPath/artifacts/flat-customer.agent")) {
        if (-not (Test-Path -LiteralPath (Get-FullPath $required) -PathType Leaf)) { throw "Missing fixed 003 artifact: $required" }
    }
    $null = Assert-StudyOracle $head @()
    $sourceArtifacts = Get-SourceArtifactRows $head $false
    $flat = Get-BaselineArchive 'flat' $head $false
    $rich = Get-BaselineArchive 'rich' $head $false
    $flatManifest = Read-Json (Get-FullPath $flat.manifestPath)
    $richManifest = Read-Json (Get-FullPath $rich.manifestPath)
    if ((ConvertTo-Json -InputObject $flatManifest.runtime -Depth 100 -Compress) -cne (ConvertTo-Json -InputObject $richManifest.runtime -Depth 100 -Compress)) { throw 'Flat and rich baseline runtime pins differ.' }
    $runtime = $flatManifest.runtime
    if ($runtime.buildSourceRevision -cne $RuntimeBuildSourceRevision.ToLowerInvariant()) { throw 'Baseline archive runtime build provenance differs from -RuntimeBuildSourceRevision.' }
    if (-not [string]::IsNullOrWhiteSpace($CliDll) -and (Get-FullPath $CliDll) -cne (Get-FullPath ([string]$runtime.cliPath))) { throw '-CliDll differs from the frozen baseline runtime.' }
    if (-not [string]::IsNullOrWhiteSpace($BusinessDll) -and (Get-FullPath $BusinessDll) -cne (Get-FullPath ([string]$runtime.businessPath))) { throw '-BusinessDll differs from the frozen baseline runtime.' }
    $runtimeInfo = Get-RuntimeInputs $runtime $head $false
    Assert-RuntimeBuildProvenance $runtimeInfo $RuntimeBuildSourceRevision
    $design = Get-FullPath "$studyPath/design.json"
    $oracle = Get-FullPath "$studyPath/acceptance.json"
    $primer = Get-FullPath "$studyPath/language-primer.md"
    $tasks = foreach ($taskId in @('S01','S07')) {
        $path = "experiments/AgentLang.Benchmarks/task-bank/public/$taskId.json"
        [pscustomobject][ordered]@{taskId=$taskId;path=$path;sha256=Get-Sha256 (Get-FullPath $path)}
    }
    $hostPath = 'scripts/Start-SubagentTrialHostV2.ps1'
    $hostObject = [ordered]@{path=$hostPath;sha256=(Get-Sha256 (Get-FullPath $hostPath))}
    foreach ($key in $hostContract.Keys) { $hostObject[$key] = $hostContract[$key] }
    $global = [ordered]@{
        schemaVersion = 1
        studyId = $studyId
        frozenAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        model = $Model
        reasoningEffort = $ReasoningEffort
        forkTurns = 'none'
        profile = 'agentlang'
        design = [ordered]@{path="$studyPath/design.json";sha256=(Get-Sha256 $design)}
        oracle = [ordered]@{path="$studyPath/acceptance.json";sha256=(Get-Sha256 $oracle)}
        primer = [ordered]@{path="$studyPath/language-primer.md";sha256=(Get-Sha256 $primer)}
        tasks = @($tasks)
        sourceArtifacts = $sourceArtifacts
        baselineArchives = @($flat,$rich)
        runtime = [ordered]@{
            cliDllPath = $runtimeInfo.runtimeRoots.cliDllPath
            cliDirectoryPath = $runtimeInfo.runtimeRoots.cliDirectoryPath
            businessDllPath = $runtimeInfo.runtimeRoots.businessDllPath
            businessDirectoryPath = $runtimeInfo.runtimeRoots.businessDirectoryPath
            buildSourceRevision = $RuntimeBuildSourceRevision.ToLowerInvariant()
            files = $runtimeInfo.runtimeFiles
        }
        bootstrapper = [ordered]@{path=$flatManifest.bootstrapperPath;sha256=$flatManifest.bootstrapperSha256}
        preparer = [ordered]@{path='scripts/Prepare-RetentionTrial.ps1';sha256=(Get-Sha256 (Get-FullPath 'scripts/Prepare-RetentionTrial.ps1'))}
        host = $hostObject
        independentVerifier = [ordered]@{path='scripts/Verify-RetentionTrial.ps1';sha256=(Get-Sha256 (Get-FullPath 'scripts/Verify-RetentionTrial.ps1'))}
        preflightVerifier = [ordered]@{path='scripts/Verify-RetentionPreflight.ps1';sha256=(Get-Sha256 (Get-FullPath 'scripts/Verify-RetentionPreflight.ps1'))}
        traceAuditor = [ordered]@{path='scripts/Audit-RetentionTrace.ps1';sha256=(Get-Sha256 (Get-FullPath 'scripts/Audit-RetentionTrace.ps1'))}
        terminationAuditor = [ordered]@{path='scripts/Audit-SubagentTrialTerminationV2.ps1';sha256=(Get-Sha256 (Get-FullPath 'scripts/Audit-SubagentTrialTerminationV2.ps1'))}
        globalFreezePath = $globalManifestPath
    }
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $global -Depth 100) + "`n")
    Write-AtomicNewFile $target $bytes
    Write-Output "Created global freeze manifest $globalManifestPath with $($sourceArtifacts.Count) frozen source artifacts and two archived baselines."
}

if ($PSCmdlet.ParameterSetName -eq 'Snapshots') {
    New-SourceSnapshots
    return
}

if ($PSCmdlet.ParameterSetName -eq 'Archive') {
    New-BaselineArchives
    return
}

if ($PSCmdlet.ParameterSetName -eq 'Global') {
    New-GlobalManifest
    return
}

$expectedRun = Get-RunIdentity $Arm $Block $TaskId
if ($RunId -cne $expectedRun) { throw "RunId $RunId is not canonical for $Block/$Arm/$TaskId; expected $expectedRun." }
$run = Join-Path $localRoot "runs/$RunId"
$statePath = Join-Path $run 'starting-state.json'
$promptPath = Join-Path $run 'prompt.txt'
$pinPath = Join-Path $run 'prelaunch.json'
$tracePath = Join-Path $run 'trace.jsonl'
if (-not (Test-Path -LiteralPath $run -PathType Container)) { throw "Prepared run directory is missing: $run" }
foreach ($path in @($pinPath,$promptPath,$tracePath)) { if (Test-Path -LiteralPath $path) { throw "Refusing to replace frozen or launched trial file: $path" } }
if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) { throw "Prepared starting state is missing: $statePath" }

$head = (& git -C $repo rev-parse HEAD | Out-String).Trim()
if ([string]::IsNullOrWhiteSpace($head)) { throw 'Git HEAD is unavailable.' }
$changes = (& git -C $repo status --porcelain --untracked-files=all | Out-String)
$sourceDirty = -not [string]::IsNullOrWhiteSpace($changes)
if ($sourceDirty -and -not $ControlOnly) { throw 'A real actor trial can only be frozen from a clean committed source tree.' }
$sourceRevision = $head
$preparedState = Read-Json $statePath
if ($preparedState.schemaVersion -ne 1 -or $preparedState.studyId -cne $studyId -or $preparedState.arm -cne $Arm -or
    $preparedState.block -cne $Block -or $preparedState.taskId -cne $TaskId -or $preparedState.runId -cne $RunId -or
    $preparedState.sequenceIndex -ne $(if ($TaskId -eq 'S01') { 1 } else { 2 })) {
    throw 'Prepared starting state does not match the requested canonical run identity.'
}
if ($preparedState.preparation.sourceRevision -cne $sourceRevision -or $preparedState.preparation.dirty -ne $false) {
    throw 'Prepared state was not created from this committed clean source revision.'
}
if ($preparedState.preparation.scriptPath -cne 'scripts/Prepare-RetentionTrial.ps1' -or
    $preparedState.preparation.scriptSha256 -cne (Get-Sha256 (Get-FullPath 'scripts/Prepare-RetentionTrial.ps1')) -or
    $preparedState.preparation.bootstrapperPath -cne 'scripts/Prepare-BusinessPolicyTrial.ps1' -or
    $preparedState.preparation.bootstrapperSha256 -cne (Get-Sha256 (Get-FullPath 'scripts/Prepare-BusinessPolicyTrial.ps1'))) {
    throw 'Prepared state is not bound to the committed retention preparer and bootstrapper.'
}

$globalPath = Get-FullPath $globalManifestPath
if (-not (Test-Path -LiteralPath $globalPath -PathType Leaf)) { throw "Global freeze manifest is missing: $globalPath" }
$global = Read-Json $globalPath
if ($global.schemaVersion -ne 1 -or $global.studyId -cne $studyId -or $global.model -cne $Model -or
    $global.reasoningEffort -cne $ReasoningEffort -or $global.forkTurns -cne 'none' -or $global.profile -cne 'agentlang') {
    throw 'Global freeze identity or model declaration is invalid.'
}
if ($global.host.hostProtocolVersion -cne $hostContract.hostProtocolVersion -or
    $global.host.sha256 -cne (Get-Sha256 (Get-FullPath 'scripts/Start-SubagentTrialHostV2.ps1')) -or
    (ConvertTo-Json -InputObject @($global.host.allowedOperations) -Depth 100 -Compress) -cne (ConvertTo-Json -InputObject @($allowedOperations) -Depth 100 -Compress) -or
    $global.host.maxExchanges -ne $hostContract.maxExchanges -or $global.host.maxRequestBytes -ne $hostContract.maxRequestBytes -or
    $global.host.maxResponseBytes -ne $hostContract.maxResponseBytes -or $global.host.exchangeTimeoutMilliseconds -ne $hostContract.exchangeTimeoutMilliseconds -or
    $global.host.clockValue -cne $hostContract.clockValue -or @($global.host.capabilities).Count -ne 0 -or
    @($global.host.additionalCliArguments).Count -ne 0 -or $global.host.inspectionBudgetEnabled -ne $false) {
    throw 'Global freeze host contract or host script hash is invalid.'
}
$globalHash = Get-Sha256 $globalPath
$committedGlobal = Get-GitSnapshotBytes $sourceRevision $globalManifestPath
if ((Get-Sha256Bytes $committedGlobal) -cne $globalHash -or $committedGlobal.Length -ne (Get-Item -LiteralPath $globalPath).Length) {
    if (-not $ControlOnly) { throw 'Global freeze manifest is not byte-identical to the pinned Git revision.' }
}
if ($preparedState.preparation.oracleSha256 -cne $global.oracle.sha256 -or
    $preparedState.preparation.primerSha256 -cne $global.primer.sha256 -or
    $preparedState.preparation.designSha256 -cne $global.design.sha256) {
    throw 'Prepared state oracle, primer, or design hashes differ from the global freeze.'
}
Assert-StudyOracle $sourceRevision @()

$baselineKind = [string]$preparedState.baseline.kind
if ($baselineKind -cnotin @('flat','rich')) { throw 'Prepared baseline kind must be flat or rich.' }
$expectedBaselineKind = if ($Arm -eq 'flat') { 'flat' } else { 'rich' }
if ($baselineKind -cne $expectedBaselineKind) { throw "$Arm must use the frozen $expectedBaselineKind baseline." }
$baselineArchives = @(
    (Get-BaselineArchive 'flat' $sourceRevision $ControlOnly.IsPresent)
    (Get-BaselineArchive 'rich' $sourceRevision $ControlOnly.IsPresent)
)
if ((ConvertTo-Json -InputObject $baselineArchives -Depth 100 -Compress) -cne (ConvertTo-Json -InputObject @($global.baselineArchives) -Depth 100 -Compress)) {
    throw 'Per-run baseline archives differ from the global freeze.'
}
$frozenBaseline = @($baselineArchives | Where-Object { $_.kind -ceq $baselineKind })
if ($frozenBaseline.Count -ne 1) { throw 'The selected baseline archive is not in the global freeze.' }
$baseline = $preparedState.baseline
if ((Get-FullPath ([string]$baseline.path)) -cne (Get-FullPath $frozenBaseline[0].projectPath) -or
    (Get-FullPath ([string]$baseline.manifestPath)) -cne (Get-FullPath $frozenBaseline[0].manifestPath) -or
    $baseline.manifestSha256 -cne $frozenBaseline[0].manifestSha256 -or
    $baseline.seedStateSha256 -cne $frozenBaseline[0].seedStateSha256 -or
    $baseline.inventorySha256 -cne $frozenBaseline[0].inventorySha256 -or
    (ConvertTo-Json -InputObject @($baseline.counts) -Depth 100 -Compress) -cne (ConvertTo-Json -InputObject @($frozenBaseline[0].counts) -Depth 100 -Compress)) {
    throw 'Prepared baseline path, inventory, or counts differ from the archived global baseline.'
}
$baselineRows = Sort-OrdinalRows @($baseline.files | ForEach-Object { [pscustomobject][ordered]@{path=$_.path;bytes=$_.bytes;sha256=$_.sha256} })
$frozenRows = Sort-OrdinalRows @($frozenBaseline[0].files | ForEach-Object { [pscustomobject][ordered]@{path=$_.path;bytes=$_.bytes;sha256=$_.sha256} })
if ((ConvertTo-Json -InputObject $baselineRows -Depth 100 -Compress) -cne (ConvertTo-Json -InputObject $frozenRows -Depth 100 -Compress)) { throw 'Prepared baseline file inventory differs from the versioned baseline archive.' }
if ($preparedState.runtime.buildSourceRevision -cne $global.runtime.buildSourceRevision -or
    $frozenBaseline[0].runtime.buildSourceRevision -cne $global.runtime.buildSourceRevision) {
    throw 'Prepared runtime build provenance differs from the globally frozen fresh build.'
}

$previous = $preparedState.previousAcceptance
$fallback = $Arm -eq 'retained' -and $TaskId -eq 'S07' -and $null -ne $previous -and $previous.passed -eq $false -and $previous.carryForward -eq $false
$previousTraceAudit = $null
if ($Arm -eq 'retained' -and $TaskId -eq 'S07') {
    if ($null -eq $previous) { throw 'Retained S07 requires same-block Retained S01 acceptance or explicit failed-S01 baseline fallback evidence.' }
    $previousPath = Get-FullPath ([string]$previous.path)
    if (-not (Test-Path -LiteralPath $previousPath -PathType Leaf) -or (Get-Sha256 $previousPath) -cne $previous.sha256) { throw 'Retained S01 acceptance bytes changed before freeze.' }
    $priorResult = Read-Json $previousPath
    $priorProject = Get-Property $priorResult 'project'
    $priorProjectTreeHash = [string](Get-Property $priorProject 'treeSha256')
    if ([string]::IsNullOrWhiteSpace($priorProjectTreeHash)) { $priorProjectTreeHash = [string](Get-Property $priorResult 'outputTreeSha256') }
    $priorProjectPath = [string](Get-Property $priorProject 'path')
    if ([string]::IsNullOrWhiteSpace($priorProjectPath)) { throw 'Retained S01 acceptance evidence lacks its final actor project path.' }
    $priorProjectFullPath = Get-FullPath $priorProjectPath
    if (-not (Test-Path -LiteralPath $priorProjectFullPath -PathType Container) -or
        (Get-InventoryHash (Get-ProjectInventory $priorProjectFullPath)) -cne $priorProjectTreeHash) {
        throw 'Retained S01 acceptance evidence project no longer matches its recorded final tree.'
    }
    if ($priorResult.schemaVersion -ne 1 -or $priorResult.studyId -cne $studyId -or $priorResult.runId -cne $(if ($Block -eq 'B1') {'R03'} else {'R11'}) -or
        $priorResult.sequenceIndex -ne 1 -or $priorResult.resultKind -cne 'frozen-actor-acceptance' -or $priorResult.checkFrozenPin -ne $true -or
        $priorResult.arm -cne 'retained' -or $priorResult.block -cne $Block -or $priorResult.taskId -cne 'S01' -or
        [string]::IsNullOrWhiteSpace($priorProjectTreeHash)) { throw 'Retained S07 predecessor is not same-block frozen Retained S01 acceptance.' }
    $expectedPreviousRun = if ($Block -eq 'B1') { 'R03' } else { 'R11' }
    $canonicalPriorAcceptancePath = Get-FullPath "$localRoot/runs/$expectedPreviousRun/acceptance.json"
    if (-not (Test-Path -LiteralPath $canonicalPriorAcceptancePath -PathType Leaf) -or
        (Get-Sha256 $canonicalPriorAcceptancePath) -cne $previous.sha256) {
        throw 'Retained S07 requires the same acceptance bytes copied to the canonical predecessor run path.'
    }
    $previousAuditPath = Get-FullPath "$localRoot/runs/$expectedPreviousRun/trace-audit.json"
    if (-not (Test-Path -LiteralPath $previousAuditPath -PathType Leaf)) { throw 'Retained S07 requires a passing canonical predecessor trace audit.' }
    $previousAudit = Read-Json $previousAuditPath
    $canonicalPriorAcceptanceRepoPath = Get-RepoPath $canonicalPriorAcceptancePath
    if ($previousAudit.schemaVersion -ne 1 -or $previousAudit.passed -ne $true -or $previousAudit.studyId -cne $studyId -or
        $previousAudit.runId -cne $expectedPreviousRun -or $previousAudit.arm -cne 'retained' -or $previousAudit.block -cne $Block -or
        $previousAudit.taskId -cne 'S01' -or $previousAudit.sequenceIndex -ne 1 -or
        $previousAudit.acceptancePath -cne $canonicalPriorAcceptanceRepoPath -or $previousAudit.acceptanceSha256 -cne $previous.sha256 -or
        $previousAudit.acceptanceProjectTreeSha256 -cne $priorProjectTreeHash -or
        $previousAudit.finalActorInventorySha256 -cne $priorProjectTreeHash -or $previousAudit.projectTreeSha256 -cne $priorProjectTreeHash -or
        $previousAudit.acceptancePassed -ne $priorResult.passed) {
        throw 'Retained S07 predecessor trace audit does not pass or bind the accepted evidence to its final project tree.'
    }
    $previousTraceAudit = [pscustomobject][ordered]@{
        path = Get-RepoPath $previousAuditPath
        sha256 = Get-Sha256 $previousAuditPath
        acceptancePath = $canonicalPriorAcceptanceRepoPath
        acceptanceSha256 = $previousAudit.acceptanceSha256
        projectTreeSha256 = $previousAudit.projectTreeSha256
        passed = [bool]$previousAudit.passed
    }
    if ($fallback) {
        if ($preparedState.priorOutcome -cne 'baseline-fallback-after-failed-S01' -or $baselineKind -cne 'rich' -or
            $priorResult.passed -ne $false -or $previous.passed -ne $false -or $previous.carryForward -ne $false -or
            $previous.projectTreeSha256 -cne $priorProjectTreeHash -or
            $preparedState.project.inventorySha256 -cne $frozenBaseline[0].treeSha256) {
            throw 'Explicit fallback must use the frozen rich baseline after a failed same-block Retained S01.'
        }
    } else {
        if ($preparedState.priorOutcome -cne 'accepted-s01-carry-forward' -or $priorResult.passed -ne $true -or $previous.passed -ne $true -or $previous.carryForward -ne $true -or
            $previous.projectTreeSha256 -cne $priorProjectTreeHash -or $preparedState.project.inventorySha256 -cne $previous.projectTreeSha256) {
            throw 'Retained S07 project must equal independently accepted same-block Retained S01 output.'
        }
        if ($baselineKind -ne 'rich') { throw 'Retained S07 fallback/carry-forward baseline must remain the frozen rich baseline.' }
    }
} elseif ($null -ne $previous) { throw 'Only Retained S07 can name a predecessor acceptance.' }
if ($Arm -ne 'retained' -and $null -ne $preparedState.previousAcceptance) { throw 'Non-Retained runs cannot carry predecessor evidence.' }
if (-not ($Arm -eq 'retained' -and $TaskId -eq 'S07') -and $preparedState.priorOutcome -cne 'baseline') {
    throw 'Only Retained S07 may declare an accepted predecessor or baseline fallback outcome.'
}
if (-not ($Arm -eq 'retained' -and $TaskId -eq 'S07' -and -not $fallback) -and
    $preparedState.project.inventorySha256 -cne $frozenBaseline[0].treeSha256) {
    throw 'Every non-carry-forward cell must start from the exact frozen arm baseline.'
}

$startingPath = Get-FullPath ([string]$preparedState.project.path)
$project = Get-FullPath ([string]$preparedState.actor.projectPath)
if ($project -cne (Get-FullPath "$localRoot/actors/$RunId")) { throw 'Prepared actor project path is not the canonical run-specific path.' }
if ($startingPath -cne (Get-FullPath (Join-Path $run 'starting-project'))) { throw 'Prepared starting project path is not the canonical run-specific path.' }
$startingInventory = Get-ProjectInventory $startingPath
$actorInventory = Get-ProjectInventory $project
$startingRows = Get-CanonicalProjectRows $startingInventory
$actorRows = Get-CanonicalProjectRows $actorInventory
if ((ConvertTo-Json -InputObject $startingRows -Depth 100 -Compress) -cne (ConvertTo-Json -InputObject $actorRows -Depth 100 -Compress)) { throw 'Actor project differs from its frozen starting project.' }
if ((Get-TreeHash $startingInventory) -cne $preparedState.project.inventorySha256 -or
    (Get-TreeHash $actorInventory) -cne $preparedState.actor.inventorySha256) { throw 'Prepared starting-project or actor inventory changed before freeze.' }
$runtimeInfo = Get-RuntimeInputs $preparedState.runtime $sourceRevision $ControlOnly.IsPresent
$globalRuntimeRows = Sort-OrdinalRows @($global.runtime.files | ForEach-Object { [pscustomobject][ordered]@{runtime=$_.runtime;path=$_.path;bytes=$_.bytes;sha256=$_.sha256} }) 'runtime' 'path'
if ((ConvertTo-Json -InputObject $runtimeInfo.runtimeFiles -Depth 100 -Compress) -cne (ConvertTo-Json -InputObject $globalRuntimeRows -Depth 100 -Compress)) { throw 'Prepared runtime inventory differs from the global freeze.' }
if ($runtimeInfo.runtimeRoots.cliDllPath -cne $global.runtime.cliDllPath -or $runtimeInfo.runtimeRoots.businessDllPath -cne $global.runtime.businessDllPath) { throw 'Prepared runtime DLL paths differ from the global freeze.' }
if ($global.runtime.buildSourceRevision -cne $frozenBaseline[0].runtime.buildSourceRevision) { throw 'Global runtime build provenance differs from the selected baseline archive.' }
Assert-RuntimeSourceUnchanged ([string]$global.runtime.buildSourceRevision) $sourceRevision
Assert-RuntimeBuildProvenance $runtimeInfo ([string]$global.runtime.buildSourceRevision)

$sourceArtifacts = Get-SourceArtifactRows $sourceRevision $ControlOnly.IsPresent
if ((ConvertTo-Json -InputObject $sourceArtifacts -Depth 100 -Compress) -cne (ConvertTo-Json -InputObject @($global.sourceArtifacts) -Depth 100 -Compress)) { throw 'Per-run source artifacts differ from the global freeze.' }
$stateHash = Get-Sha256 $statePath
$taskPath = "experiments/AgentLang.Benchmarks/task-bank/public/$TaskId.json"
$task = Read-Json (Get-FullPath $taskPath)
if ($task.id -cne $TaskId) { throw "Public task id differs from $TaskId." }
$stateSourceInputs = Sort-OrdinalRows @($baseline.sourceInputs | ForEach-Object { [pscustomobject][ordered]@{path=$_.path;sha256=$_.sha256} })
if ((ConvertTo-Json -InputObject $stateSourceInputs -Depth 100 -Compress) -cne (ConvertTo-Json -InputObject @($frozenBaseline[0].sourceInputs) -Depth 100 -Compress)) { throw 'Prepared baseline source inputs differ from the versioned manifest.' }
foreach ($sourceInput in $stateSourceInputs) {
    $matchingArtifact = @($sourceArtifacts | Where-Object { $_.sourcePath -ceq $sourceInput.path -and $_.sha256 -ceq $sourceInput.sha256 })
    if ($matchingArtifact.Count -ne 1) { throw "Baseline source input is not bound to the 003 source snapshot: $($sourceInput.path)" }
}

$sequenceIndex = if ($TaskId -eq 'S01') { 1 } else { 2 }
$command = "& $(Quote-PowerShell (Get-FullPath 'scripts/Start-SubagentTrialHostV2.ps1')) -CliDll $(Quote-PowerShell $runtimeInfo.cliPath) -ProjectPath $(Quote-PowerShell $project) -TracePath $(Quote-PowerShell (Join-Path $run 'trace.jsonl')) -AllowedOperations @('" + ($allowedOperations -join "','") + "') -Profile agentlang -Capabilities @() -ClockValue '2000-01-01T00:00:00Z' -MaxRequestBytes 262144 -MaxResponseBytes 524288 -ExchangeTimeoutMilliseconds 120000 -MaxExchanges 100"
$taskGoal = [string]$task.goal
$primerPath = Get-FullPath "$studyPath/language-primer.md"
$primer = [IO.File]::ReadAllText($primerPath).TrimEnd()
$authorization = '"Yes, run the remaining isolated experiments"'
$prompt = @"
Run $studyId $RunId task $TaskId as a fresh gpt-6-luna/max coding agent with no inherited turns.
Your assigned project is $project.
You are an external coding agent in a disposable synthetic project. Use only the supplied JSONL protocol and the assigned project. Do not use raw file access, shell commands, other tools, network access, or any project outside this broker. Do not inspect coordinator source, acceptance logic, other runs, prior outputs, or sibling projects. Preserve unrelated definitions, types, and tests. Make only changes necessary for this task.

Human authorization directly covers these isolated project edits and local tests:
$authorization

Task $TaskId
$taskGoal
The Customer.Kind/kind value is raw data; comparisons are ordinal and case-sensitive. Do not trim or lowercase it. Money is signed Int64 minor units; Float is not an allowed implementation of money. Every signed Int64 value is valid, including negatives and both limits.

Use the help operation when needed. The broker exposes a bounded allowlist with no granted capabilities or side effects. Call task.begin before authoring, add meaningful boundary and branch tests, examples and concise documentation as the task requires, run the available task tests, and finish durably with task.commit. Preserve earlier task functions, tests, and examples when the task is S07.

The independent coordinator will review the final project and request acceptance. After you have completed the task, report the work and request independent acceptance while leaving this session live. Only after the coordinator has independently accepted the result and asks you to close, send the exact one-field JSONL request {"op":"host.close"}. Do not send it earlier. Do not treat EOF, Ctrl+C, timeout, or an observation delay as completion. Do not send any other transport-control request.

Do not spawn or delegate. Preserve user and coordinator changes. Report actual tests, errors, and unavailable capabilities. Model token usage, turns, and effective context windows are not measured.

$($primer)

Execute exactly the following PowerShell command with exec_command, workdir $repo, sandbox_permissions require_escalated, tty true, and a short initial yield. Do not issue other shell or file commands.

$command

No startup banner is printed. Send one JSONL request per line on the same write_stdin session. After successful task completion, report the host session ID and leave it live for independent acceptance. Then, only when the coordinator requests teardown after acceptance, send the exact host.close request above. Report the actual tests, errors, and missing capabilities.
"@
$prompt = $prompt.Replace("`r`n","`n")
$promptBytes = [Text.UTF8Encoding]::new($false).GetBytes($prompt)
$promptHash = Get-Sha256Bytes $promptBytes

$preparerPath = 'scripts/Prepare-RetentionTrial.ps1'
$freezerPath = 'scripts/Freeze-RetentionTrial.ps1'
$verifierPath = 'scripts/Verify-RetentionTrial.ps1'
$preflightPath = 'scripts/Verify-RetentionPreflight.ps1'
$auditorPath = 'scripts/Audit-RetentionTrace.ps1'
$hostPath = 'scripts/Start-SubagentTrialHostV2.ps1'
$terminationAuditorPath = 'scripts/Audit-SubagentTrialTerminationV2.ps1'
$baselineArchivePin = @($baselineArchives | ForEach-Object {
    [pscustomobject][ordered]@{
        kind=$_.kind;manifestPath=$_.manifestPath;manifestSha256=$_.manifestSha256;projectPath=$_.projectPath
        files=$_.files;inventorySha256=$_.inventorySha256;treeSha256=$_.treeSha256;sourceInputs=$_.sourceInputs;counts=$_.counts;runtime=$_.runtime
        seedStateSha256=$_.seedStateSha256;bootstrapperPath=$_.bootstrapperPath;bootstrapperSha256=$_.bootstrapperSha256
    }
})

$pin = [ordered]@{
    schemaVersion = 1
    studyId = $studyId
    arm = $Arm
    block = $Block
    taskId = $TaskId
    sequenceIndex = $sequenceIndex
    runId = $RunId
    preparedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    sourceRevision = $sourceRevision
    dirty = $sourceDirty
    controlOnly = [bool]$ControlOnly
    launchable = -not $ControlOnly -and -not $sourceDirty
    model = $Model
    reasoningEffort = $ReasoningEffort
    forkTurns = 'none'
    profile = 'agentlang'
    projectPath = $project
    launchCommand = $command
    allowedOperations = $allowedOperations
    maxExchanges = $hostContract.maxExchanges
    maxRequestBytes = $hostContract.maxRequestBytes
    maxResponseBytes = $hostContract.maxResponseBytes
    maxInspectionResponseBytes = $hostContract.maxInspectionResponseBytes
    exchangeTimeoutMilliseconds = $hostContract.exchangeTimeoutMilliseconds
    additionalCliArguments = $hostContract.additionalCliArguments
    capabilities = $hostContract.capabilities
    clockValue = $hostContract.clockValue
    inspectionBudgetEnabled = $hostContract.inspectionBudgetEnabled
    inspectionClassifierVersion = $hostContract.inspectionClassifierVersion
    hostProtocolVersion = $hostContract.hostProtocolVersion
    transportControls = $hostContract.transportControls
    promptPath = 'prompt.txt'
    promptSha256 = $promptHash
    promptUtf8Bytes = $promptBytes.Length
    globalFreezePath = $globalManifestPath
    globalFreezeSha256 = $globalHash
    runtimeBuildSourceRevision = $global.runtime.buildSourceRevision
    designPath = $global.design.path
    designSha256 = $global.design.sha256
    oraclePath = $global.oracle.path
    oracleSha256 = $global.oracle.sha256
    primerPath = $global.primer.path
    primerSha256 = $global.primer.sha256
    taskPath = $taskPath
    taskSha256 = Get-Sha256 (Get-FullPath $taskPath)
    independentVerifierPath = $global.independentVerifier.path
    independentVerifierSha256 = $global.independentVerifier.sha256
    preflightPath = $global.preflightVerifier.path
    preflightSha256 = $global.preflightVerifier.sha256
    preparerPath = $global.preparer.path
    preparerSha256 = $global.preparer.sha256
    bootstrapperPath = $global.bootstrapper.path
    bootstrapperSha256 = $global.bootstrapper.sha256
    freezerPath = $freezerPath
    freezerSha256 = Get-Sha256 (Get-FullPath $freezerPath)
    auditorPath = $global.traceAuditor.path
    auditorSha256 = $global.traceAuditor.sha256
    hostPath = $global.host.path
    hostSha256 = $global.host.sha256
    terminationAuditorPath = $global.terminationAuditor.path
    terminationAuditorSha256 = $global.terminationAuditor.sha256
    startingStatePath = Get-RepoPath $statePath
    startingStateSha256 = $stateHash
    startingProjectPath = Get-RepoPath $startingPath
    startingProjectFiles = $startingInventory
    startingProjectInventorySha256 = Get-TreeHash $startingInventory
    actorProjectFiles = $actorInventory
    actorProjectInventorySha256 = Get-TreeHash $actorInventory
    baseline = $baseline
    baselineArchives = $baselineArchivePin
    runtimeRoots = $runtimeInfo.runtimeRoots
    runtimeFiles = $runtimeInfo.runtimeFiles
    sourceArtifacts = $sourceArtifacts
    previousAcceptance = $previous
    previousTraceAudit = $previousTraceAudit
    priorOutcome = $preparedState.priorOutcome
    retentionFallback = $fallback
}

$pinBytes = [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $pin -Depth 100) + "`n")
Write-AtomicNewFile $promptPath $promptBytes
try { Write-AtomicNewFile $pinPath $pinBytes }
catch { Remove-Item -LiteralPath $promptPath -Force; throw }
Write-Output "Frozen $studyId $RunId ($Block/$Arm/$TaskId) with $(if ($pin.launchable) {'clean committed launchability'} else {'controlOnly diagnostics; actor launch forbidden'})."
