#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$Arm,
    [string]$Block,
    [string]$TaskId,
    [Parameter(Mandatory)][string]$CliDll,
    [Parameter(Mandatory)][string]$BusinessDll,
    [string]$PreviousProject,
    [string]$PreviousAcceptance,
    [switch]$BaselineFallback,
    [string]$LocalRoot,
    [string]$RunRoot,
    [string]$EvidencePath,
    [switch]$BootstrapOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$studyId = 'business-policy-retention-003'
$studyRoot = Join-Path $repo 'experiments/AgentLang.SubagentTrials/business-policy-retention-003'
$designPath = Join-Path $studyRoot 'design.json'
$acceptancePath = Join-Path $studyRoot 'acceptance.json'
$flatSourcePath = Join-Path $studyRoot 'artifacts/flat-customer.agent'
$primerPath = Join-Path $studyRoot 'language-primer.md'
$predecessorOraclePath = Join-Path $repo 'experiments/AgentLang.SubagentTrials/business-policy-001/acceptance.json'
$predecessorPrimerPath = Join-Path $repo 'experiments/AgentLang.SubagentTrials/business-policy-help-002/language-primer.md'
$predecessorFlatSeedPath = Join-Path $repo 'experiments/AgentLang.SubagentTrials/business-policy-help-002/artifacts/flat-customer.agent'
$preparerPath = Join-Path $repo 'scripts/Prepare-BusinessPolicyTrial.ps1'
$scriptPath = $PSCommandPath
$sourceRevision = $null
$sourceDirty = $true
$checks = [Collections.Generic.List[object]]::new()
$startedUtc = [DateTime]::UtcNow.ToString('O')
$failure = $null
$result = $null
$createdPaths = [Collections.Generic.List[string]]::new()
$scratchPaths = [Collections.Generic.List[string]]::new()
$evidencePath = $null
$requestedEvidencePath = $EvidencePath
$runId = if ([string]::IsNullOrWhiteSpace($TaskId)) { 'bootstrap' } else { $null }

if ([string]::IsNullOrWhiteSpace($LocalRoot)) {
    $LocalRoot = Join-Path $repo '.agentlang/business-policy-retention-003'
}
if ([string]::IsNullOrWhiteSpace($RunRoot)) {
    $RunRoot = Join-Path $LocalRoot 'runs'
}

function Get-Field($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    if ($Object -is [Collections.IDictionary]) { return $Object[$Name] }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-FullPath([string]$Path) {
    return [IO.Path]::GetFullPath($Path)
}

function Resolve-RepoPath([string]$Path) {
    if ([IO.Path]::IsPathFullyQualified($Path)) { return Get-FullPath $Path }
    return Get-FullPath (Join-Path $repo $Path)
}

function Assert-ContainedPath([string]$Root, [string]$Candidate, [string]$Label) {
    $fullRoot = Get-FullPath $Root
    $fullCandidate = Get-FullPath $Candidate
    $prefix = $fullRoot.TrimEnd([char[]]@('\','/')) + [IO.Path]::DirectorySeparatorChar
    if (-not $fullCandidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label must be inside '$fullRoot'; received '$fullCandidate'."
    }
    return $fullCandidate
}

function Test-Within([string]$Path, [string]$Root) {
    $fullPath = Get-FullPath $Path
    $fullRoot = Get-FullPath $Root
    if ($fullPath -ieq $fullRoot) { return $true }
    $prefix = $fullRoot.TrimEnd([char[]]@('\','/')) + [IO.Path]::DirectorySeparatorChar
    return $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-NoReparseTree([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { throw "Input is missing: $Path" }
    $rootItem = Get-Item -LiteralPath $Path -Force
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Reparse points are not allowed in frozen inputs: $Path"
    }
    if (-not $rootItem.PSIsContainer) { return }
    foreach ($item in Get-ChildItem -LiteralPath $Path -Force -Recurse) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Reparse points are not allowed in frozen inputs: $($item.FullName)"
        }
    }
}

function Assert-NoReparseAncestors([string]$ContainmentRoot, [string]$Candidate) {
    $fullRoot = Get-FullPath $ContainmentRoot
    $fullCandidate = Assert-ContainedPath $fullRoot $Candidate 'Path'
    $current = $fullCandidate
    while ($true) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Reparse-point path component is not allowed: $current"
            }
        }
        if ($current -ieq $fullRoot) { break }
        $current = Split-Path -Parent $current
        if ([string]::IsNullOrWhiteSpace($current)) { throw "Could not resolve parent path for '$fullCandidate'." }
    }
    return $fullCandidate
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-OrdinalPathRows([object[]]$Rows) {
    $sorted = [Collections.Generic.SortedDictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach ($row in $Rows) { $sorted.Add([string]$row.path,$row) }
    $result = [object[]]::new($sorted.Count)
    $index = 0
    foreach ($row in $sorted.Values) { $result[$index] = $row; $index++ }
    return $result
}

function ConvertTo-CanonicalJson($Value) {
    return ConvertTo-Json -InputObject $Value -Depth 100 -Compress
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

function ConvertFrom-JsonText([string]$Json) {
    $document = [Text.Json.JsonDocument]::Parse($Json)
    try { return ,(Convert-JsonElement $document.RootElement) }
    finally { $document.Dispose() }
}

function Get-CanonicalSha256($Value) {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-CanonicalJson $Value))
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Get-RelativePath([string]$Root, [string]$Path) {
    $relative = [IO.Path]::GetRelativePath((Get-FullPath $Root), (Get-FullPath $Path))
    if ($relative -eq '..' -or $relative.StartsWith('..' + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal) -or [IO.Path]::IsPathRooted($relative)) {
        throw "Path escapes repository root: $Path"
    }
    return $relative.Replace('\','/')
}

function Get-ProjectInventory([string]$Root) {
    Assert-NoReparseTree $Root
    $rows = [Collections.Generic.List[object]]::new()
    foreach ($item in Get-ChildItem -LiteralPath $Root -File -Force -Recurse) {
        $relative = [IO.Path]::GetRelativePath($Root, $item.FullName).Replace('\','/')
        if ($relative.Split('/') -contains 'bin' -or $relative.Split('/') -contains 'obj') { continue }
        $rows.Add([ordered]@{
            path = $relative
            bytes = [long]$item.Length
            sha256 = Get-Sha256 $item.FullName
        })
    }
    return @(Get-OrdinalPathRows $rows.ToArray())
}

function Get-TreeHash([string]$Root) {
    return Get-CanonicalSha256 @(Get-ProjectInventory $Root)
}

function Get-CanonicalProjectRows([object[]]$Inventory) {
    $rows = @($Inventory | ForEach-Object { [ordered]@{path=[string]$_.path;bytes=[long]$_.bytes;sha256=([string]$_.sha256).ToLowerInvariant()} })
    return @(Get-OrdinalPathRows $rows)
}

function Get-ProjectInventoryHash([object[]]$Inventory) {
    return Get-CanonicalSha256 @(Get-CanonicalProjectRows $Inventory)
}

function Get-GitBlobObjectId([string]$Revision, [string]$RelativePath) {
    $spec = "$Revision`:$RelativePath"
    $output = @(& git -C $repo rev-parse $spec 2>$null)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0 -or $output.Count -ne 1) { throw "Committed Git blob is missing for '$RelativePath' at $Revision." }
    return ([string]$output[0]).Trim().ToLowerInvariant()
}

function Assert-CommittedFile([string]$Path, [string]$Revision, [string]$Label) {
    $fullPath = Get-FullPath $Path
    Assert-ContainedPath $repo $fullPath $Label | Out-Null
    Assert-NoReparseAncestors $repo $fullPath | Out-Null
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { throw "$Label is missing: $fullPath" }
    $relative = Get-RelativePath $repo $fullPath
    $committedBlob = Get-GitBlobObjectId $Revision $relative
    $workingOutput = @(& git -C $repo hash-object --no-filters -- $fullPath 2>$null)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0 -or $workingOutput.Count -ne 1) { throw "Could not hash working ${Label}: $fullPath" }
    if (([string]$workingOutput[0]).Trim().ToLowerInvariant() -cne $committedBlob) {
        throw "$Label differs from the committed Git blob at ${Revision}: $relative"
    }
}

function Get-LegacyBootstrapRows([object[]]$Inventory) {
    return @($Inventory | ForEach-Object { [ordered]@{path=[string]$_.path;sha256=([string]$_.sha256).ToLowerInvariant()} })
}

function Assert-InventoryMatches([string]$Root, [object[]]$ExpectedRows, [string]$Label) {
    $actual = @(Get-ProjectInventory $Root)
    $expectedJson = ConvertTo-CanonicalJson @(Get-OrdinalPathRows @($ExpectedRows))
    $actualJson = ConvertTo-CanonicalJson @(Get-OrdinalPathRows @($actual))
    if ($actualJson -cne $expectedJson) { throw "$Label inventory differs from its recorded complete tree." }
    return $actual
}

function Copy-ProjectTree([string]$Source, [string]$Destination) {
    if (Test-Path -LiteralPath $Destination) { throw "Fresh destination already exists: $Destination" }
    $sourceInventory = @(Get-ProjectInventory $Source)
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    foreach ($row in $sourceInventory) {
        $sourceFile = Join-Path $Source ([string]$row.path.Replace('/', [IO.Path]::DirectorySeparatorChar))
        $destinationFile = Join-Path $Destination ([string]$row.path.Replace('/', [IO.Path]::DirectorySeparatorChar))
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destinationFile)) | Out-Null
        [IO.File]::Copy($sourceFile, $destinationFile, $false)
    }
    $destinationInventory = @(Get-ProjectInventory $Destination)
    if ((ConvertTo-CanonicalJson @($sourceInventory)) -cne (ConvertTo-CanonicalJson @($destinationInventory))) {
        throw "Copied project inventory differs from source '$Source'."
    }
    return ,$destinationInventory
}

function Remove-SafeDirectory([string]$Path, [string]$Root) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $full = Assert-ContainedPath $Root $Path 'Cleanup path'
    if ($full -ieq (Get-FullPath $Root)) { throw "Refusing to remove containment root: $full" }
    Assert-NoReparseTree $full
    Remove-Item -LiteralPath $full -Recurse -Force
}

function Invoke-JsonlSession([string]$Project, [object[]]$Requests, [string]$RuntimeDll, [string]$Label) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'dotnet'
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.ArgumentList.Add((Get-FullPath $RuntimeDll))
    $startInfo.ArgumentList.Add('--project')
    $startInfo.ArgumentList.Add((Get-FullPath $Project))
    $startInfo.ArgumentList.Add('--jsonl')
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $responses = [Collections.Generic.List[object]]::new()
    if (-not $process.Start()) { throw "Could not start pinned CLI for $Label." }
    $stderrTask = $process.StandardError.ReadToEndAsync()
    try {
        foreach ($request in $Requests) {
            $process.StandardInput.WriteLine((ConvertTo-Json -InputObject $request -Depth 100 -Compress))
            $process.StandardInput.Flush()
            $readTask = $process.StandardOutput.ReadLineAsync()
            if (-not $readTask.Wait(120000)) {
                $process.Kill($true)
                throw "Pinned CLI timed out during $Label ($($request.op))."
            }
            $line = $readTask.Result
            if ([string]::IsNullOrWhiteSpace($line)) {
                $process.WaitForExit()
                $stderr = $stderrTask.GetAwaiter().GetResult()
                throw "Pinned CLI ended before replying during $Label ($($request.op)). stderr: $stderr"
            }
            $response = ConvertFrom-Json -InputObject $line -AsHashtable -Depth 100
            if ($response.ok -ne $true) {
                $code = if ($null -ne $response.error) { $response.error.code } else { 'unknown' }
                $detail = if ($null -ne $response.text) { $response.text } else { $line }
                throw "Pinned CLI request '$($request.op)' failed during $Label ($code): $detail"
            }
            $responses.Add($response)
        }
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(60000)) {
            $process.Kill($true)
            throw "Pinned CLI did not exit after $Label JSONL input closed."
        }
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "Pinned CLI exited $($process.ExitCode) during $Label. stderr: $stderr" }
    } finally {
        if (-not $process.HasExited) { $process.Kill($true) }
        $process.Dispose()
    }
    return ,$responses.ToArray()
}

function Add-Check([string]$Name, [bool]$Passed, $Details = $null) {
    $entry = [ordered]@{name=$Name;passed=$Passed}
    if ($null -ne $Details) { $entry.details = $Details }
    $checks.Add($entry)
    if (-not $Passed) { throw "Preparation check failed: $Name" }
}

function Get-SourceInputs([string]$Kind) {
    if ($Kind -eq 'flat') {
        return ,@([ordered]@{path='experiments/AgentLang.SubagentTrials/business-policy-retention-003/artifacts/flat-customer.agent';sha256=(Get-Sha256 $flatSourcePath)})
    }
    $paths = @(
        'examples/business-values.agent',
        'examples/business-store.agent',
        'examples/business-state.agent',
        'examples/business-subscriptions.agent',
        'examples/business-invoices.agent',
        'examples/business-payments-email.agent'
    )
    return ,@($paths | ForEach-Object { [ordered]@{path=$_;sha256=(Get-Sha256 (Join-Path $repo $_))} })
}

function Get-ExpectedCounts([string]$Kind) {
    if ($Kind -eq 'flat') { return [ordered]@{words=0;types=6;tests=0} }
    return [ordered]@{words=53;types=31;tests=151}
}

function Assert-SourceCorpus {
    foreach ($path in @($designPath,$acceptancePath,$flatSourcePath,$primerPath,$predecessorOraclePath,$predecessorPrimerPath,$predecessorFlatSeedPath,$preparerPath)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required versioned input is missing: $path" }
        Assert-NoReparseAncestors $repo $path | Out-Null
    }

    $design = ConvertFrom-JsonText (Get-Content -LiteralPath $designPath -Raw)
    Add-Check 'design schema and study id' ([int]$design.schemaVersion -eq 1 -and [string]$design.studyId -ceq $studyId)
    Add-Check 'design sequence and arm order' ((@($design.sequence) -join ',') -ceq 'S01,S07' -and (@($design.arms) -join ',') -ceq 'flat,retained,reset-rich')
    Add-Check 'design conceals arm hypotheses from public prompts' ([bool]$design.publicPromptsExposeArmHypothesis -eq $false)
    $expected = [Collections.Generic.List[object]]::new()
    $expectedRun = 1
    foreach ($block in @('B1','B2')) {
        $armOrder = if ($block -eq 'B1') { @('flat','retained','reset-rich') } else { @('reset-rich','flat','retained') }
        $blockRow = @($design.blocks | Where-Object { $_.block -ceq $block })
        if ($blockRow.Count -ne 1 -or (@($blockRow[0].armOrder) -join ',') -cne ($armOrder -join ',')) {
            throw "Design block $block does not have the declared rotated arm order."
        }
        foreach ($arm in $armOrder) {
            foreach ($task in @('S01','S07')) {
                $baselineKind = if ($arm -eq 'flat') { 'flat' } else { 'rich' }
                $startingRule = if ($arm -eq 'flat') { 'flat-seed' } elseif ($arm -eq 'retained' -and $task -eq 'S07') { 'accepted-same-block-retained-S01-only' } else { 'rich-seed' }
                $expected.Add([ordered]@{
                    runId = ('R{0:D2}' -f $expectedRun)
                    block = $block
                    arm = $arm
                    taskId = $task
                    sequenceIndex = if ($task -eq 'S01') { 1 } else { 2 }
                    baselineKind = $baselineKind
                    startingRule = $startingRule
                })
                $expectedRun++
            }
        }
    }
    $actualCells = @($design.cells)
    Add-Check 'design declares exactly twelve ordered run cells' ($actualCells.Count -eq 12)
    for ($index=0; $index -lt $expected.Count; $index++) {
        $expectedJson = ConvertTo-CanonicalJson $expected[$index]
        $actualJson = ConvertTo-CanonicalJson $actualCells[$index]
        if ($expectedJson -cne $actualJson) { throw "Design cell $($index + 1) differs from the fixed rotation and run ID order." }
    }
    Add-Check 'design source paths are fixed and study-local' (
        [string]$design.acceptance.path -ceq 'experiments/AgentLang.SubagentTrials/business-policy-retention-003/acceptance.json' -and
        [string]$design.publicTasks.S01 -ceq 'experiments/AgentLang.Benchmarks/task-bank/public/S01.json' -and
        [string]$design.publicTasks.S07 -ceq 'experiments/AgentLang.Benchmarks/task-bank/public/S07.json' -and
        [string]$design.baselines.flat.projectPath -ceq 'experiments/AgentLang.SubagentTrials/business-policy-retention-003/artifacts/baselines/flat/project' -and
        [string]$design.baselines.rich.projectPath -ceq 'experiments/AgentLang.SubagentTrials/business-policy-retention-003/artifacts/baselines/rich/project')

    $sourceOracleRaw = Get-Content -LiteralPath $predecessorOraclePath -Raw
    $newOracleRaw = Get-Content -LiteralPath $acceptancePath -Raw
    $sourceOracle = ConvertFrom-JsonText $sourceOracleRaw
    $newOracle = ConvertFrom-JsonText $newOracleRaw
    $sourceOracleHash = Get-Sha256 $predecessorOraclePath
    $expectedTasks = @($sourceOracle.tasks | Where-Object { $_.id -ceq 'S01' -or $_.id -ceq 'S07' })
    Add-Check 'acceptance identity, sequence, and predecessor SHA' (
        [int]$newOracle.schemaVersion -eq 1 -and [string]$newOracle.studyId -ceq $studyId -and
        (@($newOracle.sequence) -join ',') -ceq 'S01,S07' -and
        [string]$newOracle.predecessorStudyId -ceq 'business-policy-001' -and
        [string]$newOracle.predecessorSha256 -ceq $sourceOracleHash)
    Add-Check 'acceptance oracle and defaults equal the parsed predecessor corpus' (
        (ConvertTo-CanonicalJson $newOracle.oracle) -ceq (ConvertTo-CanonicalJson $sourceOracle.oracle) -and
        (ConvertTo-CanonicalJson $newOracle.defaults) -ceq (ConvertTo-CanonicalJson $sourceOracle.defaults))
    Add-Check 'acceptance cases and task contracts equal predecessor S01/S07' (
        (ConvertTo-CanonicalJson @($newOracle.tasks)) -ceq (ConvertTo-CanonicalJson @($expectedTasks)) -and
        (@($newOracle.tasks | ForEach-Object { $_.id }) -join ',') -ceq 'S01,S07' -and
        @($newOracle.tasks | Where-Object { $_.id -ceq 'S01' })[0].cases.Count -eq 10 -and
        @($newOracle.tasks | Where-Object { $_.id -ceq 'S07' })[0].cases.Count -eq 54)
    Add-Check 'acceptance includes no S06 arm or case corpus' (@($newOracle.tasks | Where-Object { $_.id -ceq 'S06' }).Count -eq 0)

    foreach ($taskId in @('S01','S07')) {
        $taskPath = Join-Path $repo ([string]$design.publicTasks[$taskId])
        if (-not (Test-Path -LiteralPath $taskPath -PathType Leaf)) { throw "Public task source is missing: $taskPath" }
        $task = ConvertFrom-JsonText (Get-Content -LiteralPath $taskPath -Raw)
        $oracleTask = @($newOracle.tasks | Where-Object { $_.id -ceq $taskId })[0]
        $expectedSignature = (@($oracleTask.inputs) -join ',') + ' -> ' + (@($oracleTask.outputs) -join ',')
        Add-Check "$taskId public contract matches independent oracle" (
            [string]$task.id -ceq $taskId -and
            [string]$task.requiredPublicContract.symbol -ceq [string]$oracleTask.symbol -and
            [string]$task.requiredPublicContract.signature -ceq $expectedSignature)
    }

    $sourcePrimerHash = Get-Sha256 $predecessorPrimerPath
    Add-Check 'shared primer is the exact preserved 002 help file' (
        (Get-Sha256 $primerPath) -ceq $sourcePrimerHash -and
        (Get-Item -LiteralPath $primerPath).Length -eq (Get-Item -LiteralPath $predecessorPrimerPath).Length)
    Add-Check 'Flat source seed is the exact preserved schema-only seed' (
        (Get-Sha256 $flatSourcePath) -ceq (Get-Sha256 $predecessorFlatSeedPath) -and
        ([regex]::Matches([IO.File]::ReadAllText($flatSourcePath), '(?m)^\s*(?:type|record)\s+')).Count -eq 6 -and
        ([regex]::Matches([IO.File]::ReadAllText($flatSourcePath), '(?m)^\s*field\s+')).Count -eq 5 -and
        -not [regex]::IsMatch([IO.File]::ReadAllText($flatSourcePath), '(?m)^\s*word\s+') -and
        -not [regex]::IsMatch([IO.File]::ReadAllText($flatSourcePath), '(?i)premium|discount'))
    return [pscustomobject]@{
        design = $design
        acceptance = $newOracle
        oracleSha256 = Get-Sha256 $acceptancePath
        predecessorOracleSha256 = $sourceOracleHash
        primerSha256 = Get-Sha256 $primerPath
        designSha256 = Get-Sha256 $designPath
        flatSourceSha256 = Get-Sha256 $flatSourcePath
    }
}

function Get-ExpectedSourceTypeCount([string]$Kind) {
    $sourceInputs = Get-SourceInputs $Kind
    $count = 0
    foreach ($input in $sourceInputs) {
        $path = Join-Path $repo ([string]$input.path)
        $text = [IO.File]::ReadAllText($path)
        $count += [regex]::Matches($text, '(?m)^\s*(?:type|record)\s+').Count
    }
    return $count
}

function Assert-LiveBaseline([string]$Project, [string]$Kind, [string]$CliPath, [string]$Label) {
    $expectedCounts = Get-ExpectedCounts $Kind
    $expectedTypeCount = Get-ExpectedSourceTypeCount $Kind
    $before = @(Get-ProjectInventory $Project)
    $responses = Invoke-JsonlSession $Project @([ordered]@{op='words'},[ordered]@{op='test-all'}) $CliPath $Label
    $wordRows = @((Get-Field $responses[0].data 'words'))
    $authoredRows = @($wordRows | Where-Object {
        $id = [string](Get-Field $_ 'id')
        $id.StartsWith('word_', [StringComparison]::Ordinal)
    })
    $authoredNames = @($authoredRows | ForEach-Object { [string](Get-Field $_ 'name') } | Sort-Object -Unique)
    $tests = @((Get-Field $responses[1].data 'results'))
    $targetSymbols = @('customer.premium?','customer.discount-basis-points','customer.discounted-balance')
    $absent = @($targetSymbols | Where-Object { $_ -cin $authoredNames }).Count -eq 0
    Add-Check "$Label live dictionary has expected authored word count" ($authoredRows.Count -eq [int]$expectedCounts.words) @{expected=$expectedCounts.words;actual=$authoredRows.Count}
    Add-Check "$Label live dictionary contains no S01/S06/S07 policy symbols" $absent @{forbidden=$targetSymbols;present=@($targetSymbols | Where-Object { $_ -cin $authoredNames })}
    Add-Check "$Label source inventory has expected nominal type count" ($expectedTypeCount -eq [int]$expectedCounts.types) @{expected=$expectedCounts.types;actual=$expectedTypeCount}
    Add-Check "$Label attached tests report expected count and pass" ($tests.Count -eq [int]$expectedCounts.tests -and @($tests | Where-Object { (Get-Field $_ 'passed') -ne $true }).Count -eq 0) @{expected=$expectedCounts.tests;actual=$tests.Count;failed=@($tests | Where-Object { (Get-Field $_ 'passed') -ne $true })}
    $after = @(Get-ProjectInventory $Project)
    Add-Check "$Label live inspection and tests leave project bytes unchanged" ((ConvertTo-CanonicalJson @($before)) -ceq (ConvertTo-CanonicalJson @($after))) @{beforeHash=(Get-CanonicalSha256 @($before));afterHash=(Get-CanonicalSha256 @($after))}
    return [ordered]@{counts=[ordered]@{words=$authoredRows.Count;types=$expectedTypeCount;tests=$tests.Count};inventorySha256=(Get-ProjectInventoryHash @($after));files=$after}
}

function Invoke-OldPreparer([string]$Mode, [string]$Local, [string]$Runs, [string]$CliPath, [string]$BusinessPath) {
    $arguments = @('-NoProfile','-File',$preparerPath,'-Mode',$Mode,'-TaskId','S01','-CliDll',$CliPath,'-BusinessDll',$BusinessPath,'-LocalRoot',$Local,'-RunRoot',$Runs)
    $output = @(& (Join-Path $PSHOME 'pwsh.exe') @arguments 2>&1 | ForEach-Object { $_.ToString() })
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) { throw "Existing $Mode seed preparer failed with exit $exitCode`: $($output -join [Environment]::NewLine)" }
    $outputText = ($output -join [Environment]::NewLine).Trim()
    if ([string]::IsNullOrWhiteSpace($outputText)) { throw "Existing $Mode seed preparer returned no metadata." }
    return ConvertFrom-Json -InputObject $outputText -AsHashtable -Depth 100
}

function Get-BootstrapSeed([string]$Kind, [string]$CliPath, [string]$BusinessPath, [bool]$RunLiveGate) {
    $mode = if ($Kind -eq 'flat') { 'flat' } else { 'growing' }
    $bootstrapRoot = Join-Path $script:localRootFull ("bootstrap/$mode")
    $bootstrapRuns = Join-Path $script:localRootFull ("bootstrap/runs/$mode")
    $seedRoot = Join-Path $bootstrapRoot "seeds/$mode"
    $seedProject = Join-Path $seedRoot 'project'
    $seedStatePath = Join-Path $seedRoot 'seed-state.json'
    $bootstrapActor = Join-Path $bootstrapRoot "actors/$mode-S01"
    $bootstrapRun = Join-Path $bootstrapRuns "$mode-S01"
    Assert-NoReparseAncestors $repo $bootstrapRoot | Out-Null
    Assert-NoReparseAncestors $repo $bootstrapRuns | Out-Null

    if (-not (Test-Path -LiteralPath $seedStatePath -PathType Leaf)) {
        if ((Test-Path -LiteralPath $seedRoot) -or (Test-Path -LiteralPath $bootstrapActor) -or (Test-Path -LiteralPath $bootstrapRun)) {
            throw "Incomplete $Kind bootstrap exists; refusing to regenerate a potentially nondeterministic seed."
        }
        [IO.Directory]::CreateDirectory($bootstrapRoot) | Out-Null
        if ($Kind -eq 'flat') {
            $localFlatSource = Join-Path $bootstrapRoot 'flat-customer.agent'
            if (Test-Path -LiteralPath $localFlatSource) {
                if ((Get-Sha256 $localFlatSource) -cne (Get-Sha256 $flatSourcePath)) { throw 'Bootstrap Flat source differs from the versioned Flat artifact.' }
            } else {
                [IO.File]::WriteAllBytes($localFlatSource,[IO.File]::ReadAllBytes($flatSourcePath))
            }
        }
        $bootstrapResult = Invoke-OldPreparer $mode $bootstrapRoot $bootstrapRuns $CliPath $BusinessPath
        Add-Check "$Kind seed is freshly bootstrapped by the historical preparer" (
            [string]$bootstrapResult.mode -ceq $mode -and [string]$bootstrapResult.taskId -ceq 'S01' -and
            (Get-FullPath ([string]$bootstrapResult.seedProject)) -ieq $seedProject)
    }
    if (-not (Test-Path -LiteralPath $seedStatePath -PathType Leaf) -or -not (Test-Path -LiteralPath $seedProject -PathType Container)) {
        throw "$Kind bootstrap is missing seed-state.json or project."
    }
    Assert-NoReparseTree $seedProject
    $state = ConvertFrom-JsonText (Get-Content -LiteralPath $seedStatePath -Raw)
    $expectedCounts = Get-ExpectedCounts $Kind
    $actualCounts = [ordered]@{
        words = [int]$state.counts.authoredWords
        types = [int]$state.counts.types
        tests = [int]$state.counts.tests
    }
    Add-Check "$Kind bootstrap manifest identity and counts" (
        [int]$state.schemaVersion -eq 1 -and [string]$state.mode -ceq $mode -and
        [int]$actualCounts.words -eq [int]$expectedCounts.words -and
        [int]$actualCounts.types -eq [int]$expectedCounts.types -and
        [int]$actualCounts.tests -eq [int]$expectedCounts.tests)
    Add-Check "$Kind bootstrap runtime hashes match supplied runtime" (
        [string]$state.runtime.cliSha256 -ceq (Get-Sha256 $CliPath) -and
        [string]$state.runtime.businessSha256 -ceq (Get-Sha256 $BusinessPath) -and
        (Resolve-RepoPath ([string]$state.runtime.cliPath)) -ieq $CliPath -and
        (Resolve-RepoPath ([string]$state.runtime.businessPath)) -ieq $BusinessPath)

    $sourceInputs = Get-SourceInputs $Kind
    $stateInputs = @($state.sourceInputs)
    if ($Kind -eq 'rich') {
        Add-Check 'Growing bootstrap source input paths and hashes match six versioned examples' (
            $stateInputs.Count -eq 6 -and (ConvertTo-CanonicalJson @($stateInputs)) -ceq (ConvertTo-CanonicalJson @($sourceInputs)))
    } else {
        $localFlatSource = Join-Path $bootstrapRoot 'flat-customer.agent'
        Add-Check 'Flat bootstrap input is the exact versioned six-type seed' (
            $stateInputs.Count -eq 1 -and (Get-Sha256 $localFlatSource) -ceq (Get-Sha256 $flatSourcePath) -and
            [string]$stateInputs[0].sha256 -ceq [string]$sourceInputs[0].sha256)
    }
    $actualInventory = @(Get-ProjectInventory $seedProject)
    $recordedLegacyRows = @(Get-LegacyBootstrapRows @($state.project.files))
    $recordedRows = @(Get-OrdinalPathRows $recordedLegacyRows)
    $actualHashRows = @(Get-OrdinalPathRows (Get-LegacyBootstrapRows $actualInventory))
    $legacyInventorySha256 = Get-CanonicalSha256 @($recordedLegacyRows)
    $recordedTreeJson = ConvertTo-CanonicalJson @($recordedRows)
    $actualTreeJson = ConvertTo-CanonicalJson @($actualHashRows)
    Add-Check "$Kind bootstrap tree matches its historical complete inventory" ($recordedTreeJson -ceq $actualTreeJson) @{
        recordedRows=@($recordedRows).Count
        actualRows=@($actualHashRows).Count
        recordedJson=$recordedTreeJson
        actualJson=$actualTreeJson
    }
    if ($RunLiveGate) {
        $gate = Assert-LiveBaseline $seedProject $Kind $CliPath "$Kind bootstrap seed"
        $actualInventory = @($gate.files)
    }
    return [ordered]@{
        kind = $Kind
        mode = $mode
        path = $seedProject
        statePath = $seedStatePath
        seedStateSha256 = Get-Sha256 $seedStatePath
        bootstrapLegacyInventorySha256 = $legacyInventorySha256
        files = $actualInventory
        inventorySha256 = Get-ProjectInventoryHash @($actualInventory)
        sourceInputs = $sourceInputs
        counts = $actualCounts
        runtime = [ordered]@{
            cliPath = [string]$state.runtime.cliPath
            cliSha256 = [string]$state.runtime.cliSha256
            businessPath = [string]$state.runtime.businessPath
            businessSha256 = [string]$state.runtime.businessSha256
            buildSourceRevision = $sourceRevision
        }
        bootstrapperPath = 'scripts/Prepare-BusinessPolicyTrial.ps1'
        bootstrapperSha256 = Get-Sha256 $preparerPath
    }
}

function Get-FrozenBaseline([string]$Kind, [string]$CliPath, [string]$BusinessPath, [bool]$RunLiveGate) {
    $design = $script:studyDesign
    $paths = $design.baselines[$Kind]
    $projectPath = Resolve-RepoPath ([string]$paths.projectPath)
    $manifestPath = Resolve-RepoPath ([string]$paths.manifestPath)
    foreach ($path in @($projectPath,$manifestPath)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Committed frozen $Kind baseline is missing: $path" }
        Assert-NoReparseTree $path
    }
    Assert-CommittedFile $manifestPath $script:sourceRevision "$Kind baseline manifest"
    $manifest = ConvertFrom-JsonText (Get-Content -LiteralPath $manifestPath -Raw)
    $expectedCounts = Get-ExpectedCounts $Kind
    Add-Check "$Kind frozen baseline manifest identity and counts" (
        [int]$manifest.schemaVersion -eq 1 -and [string]$manifest.studyId -ceq $studyId -and [string]$manifest.kind -ceq $Kind -and
        [int]$manifest.counts.words -eq [int]$expectedCounts.words -and
        [int]$manifest.counts.types -eq [int]$expectedCounts.types -and
        [int]$manifest.counts.tests -eq [int]$expectedCounts.tests)
    $sourceInputs = Get-SourceInputs $Kind
    Add-Check "$Kind frozen baseline source inputs match current source hashes" (
        (ConvertTo-CanonicalJson @($manifest.sourceInputs | Sort-Object -CaseSensitive path)) -ceq
        (ConvertTo-CanonicalJson @($sourceInputs | Sort-Object -CaseSensitive path)))
    Add-Check "$Kind frozen baseline runtime hashes match supplied runtime" (
        [string]$manifest.runtime.cliSha256 -ceq (Get-Sha256 $CliPath) -and
        [string]$manifest.runtime.businessSha256 -ceq (Get-Sha256 $BusinessPath) -and
        (Resolve-RepoPath ([string]$manifest.runtime.cliPath)) -ieq $CliPath -and
        (Resolve-RepoPath ([string]$manifest.runtime.businessPath)) -ieq $BusinessPath -and
        -not [string]::IsNullOrWhiteSpace([string]$manifest.runtime.buildSourceRevision))
    Add-Check "$Kind frozen baseline pins seed builder provenance" (
        [string]$manifest.bootstrapperPath -ceq 'scripts/Prepare-BusinessPolicyTrial.ps1' -and
        [string]$manifest.bootstrapperSha256 -ceq (Get-Sha256 $preparerPath) -and
        -not [string]::IsNullOrWhiteSpace([string]$manifest.seedStateSha256))
    $files = @(Get-ProjectInventory $projectPath)
    Add-Check "$Kind frozen baseline file rows and hash match complete archived tree" (
        (ConvertTo-CanonicalJson @($files)) -ceq (ConvertTo-CanonicalJson @(Get-OrdinalPathRows @($manifest.files))) -and
        (Get-ProjectInventoryHash @($files)) -ceq [string]$manifest.inventorySha256)
    foreach ($row in $files) {
        $archivedFile = Join-Path $projectPath ([string]$row.path.Replace('/',[IO.Path]::DirectorySeparatorChar))
        Assert-CommittedFile $archivedFile $script:sourceRevision "$Kind baseline project file"
    }
    foreach ($input in $sourceInputs) {
        Assert-CommittedFile (Join-Path $repo ([string]$input.path)) $script:sourceRevision "$Kind baseline source input"
    }
    if ($RunLiveGate) {
        $checksRoot = Join-Path $script:localRootFull 'baseline-checks'
        [IO.Directory]::CreateDirectory($checksRoot) | Out-Null
        $checkPath = Join-Path $checksRoot ("$Kind-" + [Guid]::NewGuid().ToString('N'))
        Assert-NoReparseAncestors $repo $checkPath | Out-Null
        $scratchPaths.Add($checkPath)
        $null = Copy-ProjectTree $projectPath $checkPath
        $gate = Assert-LiveBaseline $checkPath $Kind $CliPath "$Kind archived baseline copy"
        Add-Check "$Kind archived baseline live validation preserves archived seed IDs" (
            (ConvertTo-CanonicalJson @($gate.files)) -ceq (ConvertTo-CanonicalJson @($files)))
    }
    return [ordered]@{
        kind = $Kind
        mode = [string]$manifest.mode
        path = $projectPath
        manifestPath = $manifestPath
        manifestSha256 = Get-Sha256 $manifestPath
        seedStateSha256 = [string]$manifest.seedStateSha256
        files = $files
        inventorySha256 = [string]$manifest.inventorySha256
        sourceInputs = @($manifest.sourceInputs)
        counts = [ordered]@{words=[int]$manifest.counts.words;types=[int]$manifest.counts.types;tests=[int]$manifest.counts.tests}
        runtime = [ordered]@{
            cliPath = [string]$manifest.runtime.cliPath
            cliSha256 = [string]$manifest.runtime.cliSha256
            businessPath = [string]$manifest.runtime.businessPath
            businessSha256 = [string]$manifest.runtime.businessSha256
            buildSourceRevision = [string]$manifest.runtime.buildSourceRevision
        }
        bootstrapperPath = [string]$manifest.bootstrapperPath
        bootstrapperSha256 = [string]$manifest.bootstrapperSha256
    }
}

function Get-Cell([object]$Design, [string]$RequestedArm, [string]$RequestedBlock, [string]$RequestedTask) {
    $matches = @($Design.cells | Where-Object { $_.arm -ceq $RequestedArm -and $_.block -ceq $RequestedBlock -and $_.taskId -ceq $RequestedTask })
    if ($matches.Count -ne 1) { throw "Design must contain exactly one $RequestedBlock/$RequestedArm/$RequestedTask cell." }
    return $matches[0]
}

function Get-PreviousEvidence([string]$AcceptanceFile, [string]$ExpectedBlock, [string]$ExpectedRunId, [bool]$RequirePassed, [string]$PreviousProjectPath = $null) {
    $fullAcceptance = Resolve-RepoPath $AcceptanceFile
    Assert-NoReparseAncestors $repo $fullAcceptance | Out-Null
    if (-not (Test-Path -LiteralPath $fullAcceptance -PathType Leaf)) { throw "Previous acceptance evidence is missing: $fullAcceptance" }
    $expectedRunDirectory = Join-Path $repo ".agentlang/business-policy-retention-003/runs/$ExpectedRunId"
    $canonicalAcceptancePath = Join-Path $expectedRunDirectory 'acceptance.json'
    $canonicalTraceAuditPath = Join-Path $expectedRunDirectory 'trace-audit.json'
    $canonicalAcceptanceMatches = [IO.Path]::GetFullPath($fullAcceptance).Equals([IO.Path]::GetFullPath($canonicalAcceptancePath),[StringComparison]::OrdinalIgnoreCase)
    Add-Check 'predecessor acceptance comes from canonical run acceptance.json' $canonicalAcceptanceMatches @{expected=$canonicalAcceptancePath;actual=$fullAcceptance}
    if (-not $canonicalAcceptanceMatches) { throw 'Retained S07 predecessor evidence must be copied to its canonical runs/R03 or runs/R11 acceptance.json path.' }
    $evidence = ConvertFrom-JsonText (Get-Content -LiteralPath $fullAcceptance -Raw)
    $acceptanceHash = Get-Sha256 $fullAcceptance
    $passed = [bool](Get-Field $evidence 'passed')
    $projectRecord = Get-Field $evidence 'project'
    $evidenceProjectPath = [string](Get-Field $projectRecord 'path')
    $evidenceTreeHash = [string](Get-Field $projectRecord 'treeSha256')
    if ([string]::IsNullOrWhiteSpace($evidenceTreeHash)) { $evidenceTreeHash = [string](Get-Field $evidence 'outputTreeSha256') }
    if (-not (Test-Path -LiteralPath $canonicalTraceAuditPath -PathType Leaf)) { throw "Retained S07 requires the passing canonical predecessor trace audit: $canonicalTraceAuditPath" }
    $traceAudit = ConvertFrom-JsonText (Get-Content -LiteralPath $canonicalTraceAuditPath -Raw)
    $expectedAcceptanceRepoPath = Get-RelativePath $repo $canonicalAcceptancePath
    $traceAuditPassed = [int](Get-Field $traceAudit 'schemaVersion') -eq 1 -and
        [bool](Get-Field $traceAudit 'passed') -and
        [string](Get-Field $traceAudit 'studyId') -ceq $studyId -and
        [string](Get-Field $traceAudit 'arm') -ceq 'retained' -and
        [string](Get-Field $traceAudit 'block') -ceq $ExpectedBlock -and
        [string](Get-Field $traceAudit 'taskId') -ceq 'S01' -and
        [string](Get-Field $traceAudit 'runId') -ceq $ExpectedRunId -and
        [int](Get-Field $traceAudit 'sequenceIndex') -eq 1 -and
        [string](Get-Field $traceAudit 'acceptancePath') -ceq $expectedAcceptanceRepoPath -and
        [string](Get-Field $traceAudit 'acceptanceSha256') -ceq $acceptanceHash -and
        (Get-Field $traceAudit 'acceptancePassed') -eq $passed -and
        [string](Get-Field $traceAudit 'acceptanceProjectTreeSha256') -ceq $evidenceTreeHash -and
        [string](Get-Field $traceAudit 'finalActorInventorySha256') -ceq $evidenceTreeHash -and
        [string](Get-Field $traceAudit 'projectTreeSha256') -ceq $evidenceTreeHash
    Add-Check 'predecessor trace audit passes and binds exact acceptance bytes and actor tree' $traceAuditPassed @{path=$canonicalTraceAuditPath;sha256=(Get-Sha256 $canonicalTraceAuditPath);acceptanceSha256=(Get-Field $traceAudit 'acceptanceSha256');acceptancePassed=(Get-Field $traceAudit 'acceptancePassed');projectTreeSha256=(Get-Field $traceAudit 'projectTreeSha256');expectedProjectTreeSha256=$evidenceTreeHash}
    if (-not $traceAuditPassed) { throw 'Retained S07 predecessor trace audit failed or does not bind the canonical acceptance bytes and project tree.' }
    Add-Check 'predecessor evidence names same-block retained S01' (
        [int](Get-Field $evidence 'schemaVersion') -eq 1 -and
        [string](Get-Field $evidence 'studyId') -ceq $studyId -and
        [string](Get-Field $evidence 'arm') -ceq 'retained' -and
        [string](Get-Field $evidence 'block') -ceq $ExpectedBlock -and
        [string](Get-Field $evidence 'taskId') -ceq 'S01' -and
        [string](Get-Field $evidence 'runId') -ceq $ExpectedRunId -and
        [int](Get-Field $evidence 'sequenceIndex') -eq 1 -and
        (Get-Field $evidence 'checkFrozenPin') -eq $true -and
        [string](Get-Field $evidence 'resultKind') -ceq 'frozen-actor-acceptance')
    if ($RequirePassed -and -not $passed) { throw 'Retained S07 cannot use a failed S01 project; request explicit -BaselineFallback with the failed evidence.' }
    if (-not $RequirePassed -and $passed) { throw 'Baseline fallback requires failed S01 acceptance evidence.' }
    if (-not [string]::IsNullOrWhiteSpace($PreviousProjectPath)) {
        $resolvedPreviousProject = Resolve-RepoPath $PreviousProjectPath
        Assert-NoReparseAncestors $repo $resolvedPreviousProject | Out-Null
        if (-not (Test-Path -LiteralPath $resolvedPreviousProject -PathType Container)) { throw "Previous accepted project is missing: $resolvedPreviousProject" }
        $actualTreeHash = Get-TreeHash $resolvedPreviousProject
        $projectPathMatches = [string]::IsNullOrWhiteSpace($evidenceProjectPath)
        if (-not $projectPathMatches) { $projectPathMatches = (Resolve-RepoPath $evidenceProjectPath) -ieq $resolvedPreviousProject }
        Add-Check 'accepted predecessor actor tree matches supplied project' (
            $passed -and -not [string]::IsNullOrWhiteSpace($evidenceTreeHash) -and
            $actualTreeHash -ceq $evidenceTreeHash.ToLowerInvariant() -and $projectPathMatches)
        return [ordered]@{
            runId = $ExpectedRunId
            arm = 'retained'
            block = $ExpectedBlock
            taskId = 'S01'
            path = Get-RelativePath $repo $fullAcceptance
            sha256 = $acceptanceHash
            projectTreeSha256 = $actualTreeHash
            passed = $true
            carryForward = $true
            projectPath = $resolvedPreviousProject
        }
    }
    if ([string]::IsNullOrWhiteSpace($evidenceProjectPath) -or [string]::IsNullOrWhiteSpace($evidenceTreeHash)) {
        throw 'Baseline fallback requires failed S01 evidence with a recorded actor project path and tree hash.'
    }
    $resolvedEvidenceProject = Resolve-RepoPath $evidenceProjectPath
    Assert-NoReparseAncestors $repo $resolvedEvidenceProject | Out-Null
    if (-not (Test-Path -LiteralPath $resolvedEvidenceProject -PathType Container)) { throw "Failed predecessor evidence project is missing: $resolvedEvidenceProject" }
    Add-Check 'failed S01 evidence actor tree still matches recorded output' ((Get-TreeHash $resolvedEvidenceProject) -ceq $evidenceTreeHash.ToLowerInvariant())
    return [ordered]@{
        runId = $ExpectedRunId
        arm = 'retained'
        block = $ExpectedBlock
        taskId = 'S01'
        path = Get-RelativePath $repo $fullAcceptance
        sha256 = $acceptanceHash
        projectTreeSha256 = $evidenceTreeHash
        passed = $false
        carryForward = $false
        projectPath = $evidenceProjectPath
    }
}

function Write-NewJson([string]$Path, [object]$Value) {
    if (Test-Path -LiteralPath $Path) { throw "Refusing to replace evidence file: $Path" }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 100) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

function Get-UniquePreparationEvidencePath([string]$PreferredPath) {
    $directory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($PreferredPath))
    $stem = [IO.Path]::GetFileNameWithoutExtension($PreferredPath)
    $extension = [IO.Path]::GetExtension($PreferredPath)
    if ([string]::IsNullOrWhiteSpace($extension)) { $extension = '.json' }
    for ($attempt=0; $attempt -lt 20; $attempt++) {
        $candidate = Join-Path $directory ("$stem-$([Guid]::NewGuid().ToString('N'))$extension")
        if (-not (Test-Path -LiteralPath $candidate)) { return $candidate }
    }
    throw "Could not allocate a unique preparation evidence path beside '$PreferredPath'."
}

function Get-DefaultPreparationEvidencePath([string]$Suffix) {
    $directory = Join-Path $repo '.agentlang/business-policy-retention-003/preparation-evidence'
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    return Get-UniquePreparationEvidencePath (Join-Path $directory "081-preparation-$Suffix.json")
}

try {
    $script:studyDesign = $null
    foreach ($path in @($CliDll,$BusinessDll)) {
        $full = Resolve-RepoPath $path
        Assert-NoReparseAncestors $repo $full | Out-Null
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "Pinned runtime file is missing: $full" }
    }
    $cliPath = Resolve-RepoPath $CliDll
    $businessPath = Resolve-RepoPath $BusinessDll
    Assert-ContainedPath $repo $cliPath 'CLI DLL' | Out-Null
    Assert-ContainedPath $repo $businessPath 'Business DLL' | Out-Null
    $script:localRootFull = Resolve-RepoPath $LocalRoot
    $script:runRootFull = Resolve-RepoPath $RunRoot
    $workspaceRoot = Join-Path $repo '.agentlang'
    Assert-ContainedPath $workspaceRoot $script:localRootFull 'LocalRoot' | Out-Null
    Assert-ContainedPath $workspaceRoot $script:runRootFull 'RunRoot' | Out-Null
    Assert-NoReparseAncestors $repo $script:localRootFull | Out-Null
    Assert-NoReparseAncestors $repo $script:runRootFull | Out-Null

    $corpus = Assert-SourceCorpus
    $script:studyDesign = $corpus.design
    $sourceRevision = (& git -C $repo rev-parse HEAD | Out-String).Trim()
    $gitStatus = (& git -C $repo status --porcelain --untracked-files=all | Out-String)
    $sourceDirty = -not [string]::IsNullOrWhiteSpace($gitStatus)

    if ($BootstrapOnly) {
        $flat = Get-BootstrapSeed 'flat' $cliPath $businessPath $true
        $rich = Get-BootstrapSeed 'rich' $cliPath $businessPath $true
        $result = [ordered]@{
            schemaVersion = 1
            studyId = $studyId
            bootstrapOnly = $true
            sourceRevision = $sourceRevision
            dirty = $sourceDirty
            oraclePath = Get-RelativePath $repo $acceptancePath
            oracleSha256 = $corpus.oracleSha256
            designPath = Get-RelativePath $repo $designPath
            designSha256 = $corpus.designSha256
            primerPath = Get-RelativePath $repo $primerPath
            primerSha256 = $corpus.primerSha256
            baselines = [ordered]@{flat=$flat;rich=$rich}
        }
    } else {
        if ([string]::IsNullOrWhiteSpace($Arm) -or $Arm -notin @('flat','retained','reset-rich')) { throw '-Arm must be flat, retained, or reset-rich.' }
        if ([string]::IsNullOrWhiteSpace($Block) -or $Block -notin @('B1','B2')) { throw '-Block must be B1 or B2.' }
        if ([string]::IsNullOrWhiteSpace($TaskId) -or $TaskId -notin @('S01','S07')) { throw '-TaskId must be S01 or S07.' }
        $cell = Get-Cell $corpus.design $Arm $Block $TaskId
        $runId = [string]$cell.runId
        $expectedIndex = if ($TaskId -eq 'S01') { 1 } else { 2 }
        Add-Check 'requested run maps to fixed public run id and task index' ([int]$cell.sequenceIndex -eq $expectedIndex)

        $isRetainedS07 = $Arm -eq 'retained' -and $TaskId -eq 'S07'
        if ($isRetainedS07) {
            if ($BaselineFallback) {
                if (-not [string]::IsNullOrWhiteSpace($PreviousProject)) { throw '-BaselineFallback rejects -PreviousProject so failed S01 code cannot be copied.' }
                if ([string]::IsNullOrWhiteSpace($PreviousAcceptance)) { throw '-BaselineFallback requires failed same-block S01 -PreviousAcceptance evidence.' }
            } elseif ([string]::IsNullOrWhiteSpace($PreviousProject) -or [string]::IsNullOrWhiteSpace($PreviousAcceptance)) {
                throw 'Retained S07 requires both accepted same-block S01 -PreviousProject and -PreviousAcceptance; fallback is explicit.'
            }
        } elseif (-not [string]::IsNullOrWhiteSpace($PreviousProject) -or -not [string]::IsNullOrWhiteSpace($PreviousAcceptance) -or $BaselineFallback) {
            throw 'Previous-stage inputs and -BaselineFallback are allowed only for Retained S07.'
        }

        $baselineKind = [string]$cell.baselineKind
        $baseline = Get-FrozenBaseline $baselineKind $cliPath $businessPath $true
        $previousEvidence = $null
        $priorOutcome = 'baseline'
        $sourceProject = [string]$baseline.path
        if ($isRetainedS07) {
            $previousCell = Get-Cell $corpus.design 'retained' $Block 'S01'
            $previousEvidence = Get-PreviousEvidence $PreviousAcceptance $Block ([string]$previousCell.runId) (-not $BaselineFallback) $PreviousProject
            if ($BaselineFallback) {
                $priorOutcome = 'baseline-fallback-after-failed-S01'
                $sourceProject = [string]$baseline.path
            } else {
                $priorOutcome = 'accepted-s01-carry-forward'
                $sourceProject = [string]$previousEvidence.projectPath
            }
        }

        $actorPath = Join-Path $script:localRootFull "actors/$runId"
        $runPath = Join-Path $script:runRootFull $runId
        $startingPath = Join-Path $runPath 'starting-project'
        $statePath = Join-Path $runPath 'starting-state.json'
        foreach ($destination in @($actorPath,$runPath)) {
            if (Test-Path -LiteralPath $destination) { throw "Fresh run destination already exists; refusing to replace it: $destination" }
        }
        Assert-ContainedPath $script:localRootFull $actorPath 'Actor destination' | Out-Null
        Assert-ContainedPath $script:runRootFull $runPath 'Run destination' | Out-Null
        Assert-NoReparseAncestors $script:localRootFull $actorPath | Out-Null
        Assert-NoReparseAncestors $script:runRootFull $runPath | Out-Null

        [IO.Directory]::CreateDirectory($runPath) | Out-Null
        $createdPaths.Add($runPath)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($actorPath)) | Out-Null
        [IO.Directory]::CreateDirectory($actorPath) | Out-Null
        $createdPaths.Add($actorPath)
        $startingInventory = Copy-ProjectTree $sourceProject $startingPath
        $actorInventory = @(Get-ProjectInventory $actorPath)
        Add-Check 'actor and immutable start are byte-identical fresh project copies' (
            (ConvertTo-CanonicalJson @($startingInventory)) -ceq (ConvertTo-CanonicalJson @($actorInventory)))

        $previousState = if ($null -ne $previousEvidence) { $previousEvidence } else { $null }
        if ($null -ne $previousState) { $previousState.Remove('projectPath') }
        $preparation = [ordered]@{
            scriptPath = Get-RelativePath $repo $scriptPath
            scriptSha256 = Get-Sha256 $scriptPath
            bootstrapperPath = $baseline.bootstrapperPath
            bootstrapperSha256 = $baseline.bootstrapperSha256
            sourceRevision = $sourceRevision
            dirty = $sourceDirty
            oraclePath = Get-RelativePath $repo $acceptancePath
            oracleSha256 = $corpus.oracleSha256
            primerPath = Get-RelativePath $repo $primerPath
            primerSha256 = $corpus.primerSha256
            designPath = Get-RelativePath $repo $designPath
            designSha256 = $corpus.designSha256
            publicTaskPath = [string]$corpus.design.publicTasks[$TaskId]
            publicTaskSha256 = Get-Sha256 (Join-Path $repo ([string]$corpus.design.publicTasks[$TaskId]))
        }
        $startingState = [ordered]@{
            schemaVersion = 1
            studyId = $studyId
            arm = $Arm
            block = $Block
            taskId = $TaskId
            sequenceIndex = $expectedIndex
            runId = $runId
            baseline = [ordered]@{
                kind = $baselineKind
                path = $baseline.path
                manifestPath = Get-RelativePath $repo $baseline.manifestPath
                manifestSha256 = $baseline.manifestSha256
                seedStateSha256 = $baseline.seedStateSha256
                files = $baseline.files
                inventorySha256 = $baseline.inventorySha256
                sourceInputs = $baseline.sourceInputs
                counts = $baseline.counts
            }
            runtime = [ordered]@{
                cliPath = Get-RelativePath $repo $cliPath
                cliSha256 = Get-Sha256 $cliPath
                businessPath = Get-RelativePath $repo $businessPath
                businessSha256 = Get-Sha256 $businessPath
                buildSourceRevision = $baseline.runtime.buildSourceRevision
            }
            preparation = $preparation
            sourceInputs = $baseline.sourceInputs
            previousAcceptance = $previousState
            priorOutcome = $priorOutcome
            project = [ordered]@{
                path = $startingPath
                files = $startingInventory
                inventorySha256 = Get-CanonicalSha256 @($startingInventory)
            }
            actor = [ordered]@{
                projectPath = $actorPath
                files = $actorInventory
                inventorySha256 = Get-CanonicalSha256 @($actorInventory)
            }
        }
        [IO.File]::WriteAllText($statePath,(ConvertTo-Json -InputObject $startingState -Depth 100) + [Environment]::NewLine,[Text.UTF8Encoding]::new($false))
        $result = [ordered]@{
            schemaVersion = 1
            studyId = $studyId
            bootstrapOnly = $false
            arm = $Arm
            block = $Block
            taskId = $TaskId
            sequenceIndex = $expectedIndex
            runId = $runId
            actorProject = $actorPath
            startingProject = $startingPath
            startingState = $statePath
            baselineProject = $baseline.path
            baselineKind = $baselineKind
            baselineInventorySha256 = $baseline.inventorySha256
            previousAcceptance = $previousState
            priorOutcome = $priorOutcome
            acceptancePath = $acceptancePath
            acceptanceSha256 = $corpus.oracleSha256
            primerPath = $primerPath
            primerSha256 = $corpus.primerSha256
        }
    }
} catch {
    $failure = $_.Exception.Message
} finally {
    foreach ($scratch in @($scratchPaths | Sort-Object Length -Descending)) {
        try { Remove-SafeDirectory $scratch (Join-Path $script:localRootFull 'baseline-checks') } catch { if ($null -eq $failure) { $failure = "Scratch cleanup failed: $($_.Exception.Message)" } }
    }
    if ($null -ne $failure -and $createdPaths.Count -gt 0) {
        foreach ($created in @($createdPaths | Sort-Object Length -Descending)) {
            try {
                $root = if (Test-Within $created $script:localRootFull) { $script:localRootFull } else { $script:runRootFull }
                Remove-SafeDirectory $created $root
            } catch { $failure += "; cleanup failed: $($_.Exception.Message)" }
        }
    }
    $suffix = if ($null -ne $runId) { $runId } else { 'bootstrap' }
    if ([string]::IsNullOrWhiteSpace($requestedEvidencePath)) {
        $evidencePath = Get-DefaultPreparationEvidencePath $suffix
    } else {
        $candidateEvidencePath = Resolve-RepoPath $requestedEvidencePath
        Assert-NoReparseAncestors $repo $candidateEvidencePath | Out-Null
        $evidencePath = if (Test-Path -LiteralPath $candidateEvidencePath) { Get-UniquePreparationEvidencePath $candidateEvidencePath } else { $candidateEvidencePath }
    }
    $evidence = [ordered]@{
        schemaVersion = 1
        studyId = $studyId
        passed = ($null -eq $failure)
        failure = $failure
        startedUtc = $startedUtc
        finishedUtc = [DateTime]::UtcNow.ToString('O')
        sourceRevision = $sourceRevision
        dirty = $sourceDirty
        arm = $Arm
        block = $Block
        taskId = $TaskId
        runId = $runId
        bootstrapOnly = [bool]$BootstrapOnly
        result = $result
        checks = @($checks)
        preparerSha256 = if (Test-Path -LiteralPath $scriptPath -PathType Leaf) { Get-Sha256 $scriptPath } else { $null }
        requestedEvidencePath = $requestedEvidencePath
        evidencePath = $evidencePath
    }
    try { Write-NewJson $evidencePath $evidence } catch { $failure = if ($null -eq $failure) { "Could not write preparation evidence: $($_.Exception.Message)" } else { "$failure; could not write preparation evidence: $($_.Exception.Message)" } }
}

if ($null -ne $failure) { throw $failure }
$result.evidencePath = $evidencePath
Write-Output (ConvertTo-Json -InputObject $result -Depth 100 -Compress)
