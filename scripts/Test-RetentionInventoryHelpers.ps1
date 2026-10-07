#requires -Version 7.0
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$scorerPath = Join-Path $PSScriptRoot 'Score-RetentionOutputSupplement.ps1'
$pinPath = Join-Path $repo '.agentlang/business-policy-retention-003/runs/R04/prelaunch.json'
$freezePath = Join-Path $repo 'experiments/AgentLang.SubagentTrials/business-policy-retention-003/artifacts/global-freeze.json'
$sourceHash = (Get-FileHash -LiteralPath $scorerPath -Algorithm SHA256).Hash.ToLowerInvariant()
$pinHash = (Get-FileHash -LiteralPath $pinPath -Algorithm SHA256).Hash.ToLowerInvariant()
$freezeHash = (Get-FileHash -LiteralPath $freezePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($pinHash -cne '16fa34e42b018adea8b2c1321e52887d760726c5c8a06daa088c9f45892fe5a3' -or
    $freezeHash -cne '142c026f9775ca7207fac3197f256ecc1469cb1dfa1979b5f6824f49b6cb1499') {
    throw 'Inventory regression inputs differ from the saved R04 pin/global freeze.'
}

# Load only read-only inventory helpers, not the scorer's executable main body.
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($scorerPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw 'Scorer has parser errors.' }
$helperNames = @('Get-Field', 'Get-Sha256', 'Get-SafeFileInventory', 'Get-CanonicalSourceRows', 'Get-CanonicalRuntimeRows')
$definitions = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -in $helperNames
}, $true))
if ($definitions.Count -ne $helperNames.Count) { throw 'Inventory helper definitions are missing or duplicated.' }
foreach ($definition in $definitions) { Invoke-Expression $definition.Extent.Text }

function Assert-FlatRows([object[]]$Rows, [int]$Expected, [string]$Label) {
    if ($Rows.Count -ne $Expected) { throw "$Label has $($Rows.Count) rows; expected $Expected." }
    foreach ($row in $Rows) {
        if ($row -isnot [Collections.IDictionary] -or -not $row.Contains('path') -or
            [string]::IsNullOrWhiteSpace([string]$row.path)) {
            throw "$Label contains a nested array or malformed row."
        }
    }
}
function Canonical-Json([object[]]$Rows) {
    ConvertTo-Json -InputObject $Rows -Depth 20 -Compress
}

$pin = Get-Content -LiteralPath $pinPath -Raw | ConvertFrom-Json -AsHashtable -Depth 100
$freeze = Get-Content -LiteralPath $freezePath -Raw | ConvertFrom-Json -AsHashtable -Depth 100
$sourceRows = @(Get-CanonicalSourceRows $pin.sourceArtifacts)
$globalSourceRows = @(Get-CanonicalSourceRows $freeze.sourceArtifacts)
$runtimeRows = @(Get-CanonicalRuntimeRows $pin.runtimeFiles)
$globalRuntimeRows = @(Get-CanonicalRuntimeRows $freeze.runtime.files)
Assert-FlatRows $sourceRows 23 'Pin source inventory'
Assert-FlatRows $globalSourceRows 23 'Global source inventory'
Assert-FlatRows $runtimeRows 26 'Pin runtime inventory'
Assert-FlatRows $globalRuntimeRows 26 'Global runtime inventory'
if ((Canonical-Json $sourceRows) -cne (Canonical-Json $globalSourceRows) -or
    (Canonical-Json $runtimeRows) -cne (Canonical-Json $globalRuntimeRows)) {
    throw 'Actual pin/global canonical inventories differ.'
}

# Filesystem-generated rows use OrderedDictionary rather than JSON object rows.
# Both representations must normalize identically, including ordering.
$filesystemRows = foreach ($kind in @('cli', 'business')) {
    $relativeRoot = if ($kind -ceq 'cli') { $pin.runtimeRoots.cliDirectoryPath } else { $pin.runtimeRoots.businessDirectoryPath }
    foreach ($file in @(Get-SafeFileInventory (Join-Path $repo $relativeRoot) -IncludeBuildArtifacts)) {
        [ordered]@{runtime=$kind;path=$file.path;bytes=$file.bytes;sha256=$file.sha256}
    }
}
$filesystemCanonical = @(Get-CanonicalRuntimeRows @($filesystemRows))
Assert-FlatRows $filesystemCanonical 26 'Actual filesystem runtime inventory'
if ((Canonical-Json $filesystemCanonical) -cne (Canonical-Json $runtimeRows)) {
    throw 'Filesystem runtime rows differ from the pin after canonicalization.'
}

# Ordering must be deterministic, and a changed hash must remain observable.
foreach ($kind in @('source', 'runtime')) {
    $rows = if ($kind -ceq 'source') { $pin.sourceArtifacts } else { $pin.runtimeFiles }
    $normalize = if ($kind -ceq 'source') { 'Get-CanonicalSourceRows' } else { 'Get-CanonicalRuntimeRows' }
    $expected = Canonical-Json @(& $normalize $rows)
    $reversed = @($rows)
    [Array]::Reverse($reversed)
    if ((Canonical-Json @(& $normalize $reversed)) -cne $expected) { throw "$kind order is unstable." }
    $changed = ConvertFrom-Json (ConvertTo-Json -InputObject $rows -Depth 20) -AsHashtable
    $changed[0].sha256 = '0' * 64
    if ((Canonical-Json @(& $normalize $changed)) -ceq $expected) { throw "$kind hash mutation was hidden." }
}

if ((Get-FileHash -LiteralPath $scorerPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $sourceHash) {
    throw 'Scorer changed during inventory validation; rerun on stable source.'
}
[ordered]@{passed=$true;scorerSha256=$sourceHash;pinSha256=$pinHash;globalFreezeSha256=$freezeHash;
    sourceRows=23;runtimeRows=26;filesystemRuntimeMatched=$true;
    orderingControls=2;hashMutationControls=2;runtimeCalls=0} | ConvertTo-Json
