#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('flat', 'growing')][string]$Profile,
    [Parameter(Mandatory)][string]$ProjectPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$bundle = Join-Path $repo "experiments/AgentLang.SubagentTrials/matched-renewal-001/snapshots/$Profile"
$manifest = Get-Content -LiteralPath (Join-Path $bundle 'bundle.json') -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.profile -ne $Profile) { throw 'Snapshot bundle identity mismatch.' }
$destination = [IO.Path]::GetFullPath($ProjectPath, $repo)
if (Test-Path -LiteralPath $destination) { throw 'Restore requires a new project directory; existing state is never overwritten.' }
$ancestor = [IO.DirectoryInfo]::new([IO.Path]::GetDirectoryName($destination))
while ($null -ne $ancestor) {
    if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Project destination traverses a reparse point.'
    }
    $ancestor = $ancestor.Parent
}

# Validate the complete bundle before creating a destination. This deliberately
# does not restore credentials, capabilities, task history, or compiled code.
$store = Join-Path $bundle 'store'
$entries = @($manifest.files)
$actual = @(Get-ChildItem -LiteralPath $store -File -Recurse)
if ($entries.Count -ne $actual.Count -or $entries.Count -eq 0) { throw 'Snapshot bundle file count mismatch.' }
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $entries) {
    $relative = [string]$entry.path
    if ($relative -notmatch '^(CURRENT|(?:objects|manifests|snapshots)/[A-Za-z0-9_.-]+)$' -or -not $seen.Add($relative)) {
        throw 'Invalid or duplicate snapshot bundle path.'
    }
    $source = Join-Path $store $relative
    $item = Get-Item -LiteralPath $source
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Invalid snapshot bundle file.' }
    $parent = $item.Directory
    while ($null -ne $parent) {
        if ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Snapshot bundle traverses a reparse point.' }
        $parent = $parent.Parent
    }
    if ($item.Length -ne $entry.bytes -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256) {
        throw "Snapshot bundle hash/size mismatch: $relative"
    }
}

$targetStore = Join-Path $destination '.agentlang/store'
[void](New-Item -ItemType Directory -Path $targetStore)
foreach ($entry in $entries) {
    $target = Join-Path $targetStore $entry.path
    [void](New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force)
    Copy-Item -LiteralPath (Join-Path $store $entry.path) -Destination $target
}
[ordered]@{
    profile = $Profile
    project = $destination
    snapshot = $manifest.snapshot
    manifestHash = $manifest.manifestHash
    bundleSha256 = (Get-FileHash -LiteralPath (Join-Path $bundle 'bundle.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    limits = 'Runtime load still validates storage semantics. Host capabilities and binaries are pinned separately.'
} | ConvertTo-Json
