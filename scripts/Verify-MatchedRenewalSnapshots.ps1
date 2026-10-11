#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CliDll,
    [string]$EvidencePath = '.agentlang/reports/matched-renewal-snapshots.json'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$cli = (Resolve-Path -LiteralPath $CliDll).Path
. (Join-Path $PSScriptRoot 'Current-MatchedRenewalFixture.ps1')
$checks = [Collections.Generic.List[object]]::new()
$traces = [Collections.Generic.List[object]]::new()
$passed = $false
$failure = $null
$fixtureEvidenceRoot = Join-Path $repo '.agentlang/float-contract-229/current-fixtures/snapshot-verification'
$frozenBundlePins = @{
    flat = [ordered]@{
        bundleSha256 = '82cef4b9efd38d6ffecb162f1734609ea3122c5bc82251f61c6edfb31c75738b'
        manifestHash = '272b44c7e2a752f75f1554ed00c94c53d7632e7ecfedd82b9af6ade26c268a3e'
        words = @()
    }
    growing = [ordered]@{
        bundleSha256 = '4b88ad1a244230c91fc1b453dd3f2b472ae983d91cfb9c755a5b69023ee8e678'
        manifestHash = '480feeaec8a8ad5d860349890bbbe151b2345f7c746f1400e926da68eb181521'
        words = @(
            [pscustomobject]@{ name = 'customer.discounted-balance'; id = 'word_90c3fe0f880848bf84d63dd72b8707bf' }
            [pscustomobject]@{ name = 'customer.premium?'; id = 'word_6f1915c78d344e4386fedc1ffecc01f0' }
        )
    }
}
function Assert-Check([string]$Name, [bool]$Passed) {
    $checks.Add([ordered]@{name=$Name;passed=$Passed})
    if (-not $Passed) { throw "Snapshot check failed: $Name" }
}
try {
    foreach ($profile in @('flat', 'growing')) {
        $bundle = Join-Path $repo "experiments/AgentLang.SubagentTrials/matched-renewal-001/snapshots/$profile"
        $meta = Get-Content -LiteralPath (Join-Path $bundle 'bundle.json') -Raw | ConvertFrom-Json
        $seed = Join-Path $repo "experiments/AgentLang.SubagentTrials/matched-renewal-001/fixtures/flow/$profile.agent"
        $bundleHash = (Get-FileHash -LiteralPath (Join-Path $bundle 'bundle.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        $pin = $frozenBundlePins[$profile]
        Assert-Check "$profile bundle metadata matches its pinned SHA-256" ($bundleHash -ceq $pin.bundleSha256)
        Assert-Check "$profile frozen manifest hash is pinned" ([string]$meta.manifestHash -ceq $pin.manifestHash)
        Assert-Check "$profile source hash matches bundle seedSourceSha256" ((Get-FileHash -LiteralPath $seed -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $meta.seedSourceSha256)

        $artifactProject = Join-Path $repo ('.agentlang/matched-snapshot-verification/artifacts/' + $profile + '-' + [guid]::NewGuid().ToString('N'))
        $restoreText = @(& (Join-Path $repo 'scripts/Restore-MatchedRenewalSnapshot.ps1') -Profile $profile -ProjectPath $artifactProject)
        $restore = ($restoreText -join "`n") | ConvertFrom-Json
        Assert-Check "$profile restore reports the pinned manifest" ($restore.manifestHash -ceq $pin.manifestHash)
        Assert-Check "$profile restore reports the pinned bundle hash" ($restore.bundleSha256 -ceq $pin.bundleSha256)

        $manifestPath = Join-Path $bundle "store/manifests/$($pin.manifestHash).json"
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 32
        $manifestContentHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        Assert-Check "$profile manifest bytes have the pinned identity" ($manifestContentHash -ceq $pin.manifestHash)
        $actualFiles = @(Get-ChildItem -LiteralPath (Join-Path $artifactProject '.agentlang/store') -File -Recurse)
        Assert-Check "$profile restored file count matches the frozen bundle" ($actualFiles.Count -eq @($meta.files).Count)
        $copiedFilesMatch = $true
        foreach ($entry in $meta.files) {
            $copy = Join-Path $artifactProject ".agentlang/store/$($entry.path)"
            if (-not (Test-Path -LiteralPath $copy -PathType Leaf)) { $copiedFilesMatch = $false; break }
            $item = Get-Item -LiteralPath $copy
            $hash = (Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($item.Length -ne [int64]$entry.bytes -or $hash -cne [string]$entry.sha256) { $copiedFilesMatch = $false; break }
        }
        Assert-Check "$profile restored frozen files retain their pinned sizes and hashes" $copiedFilesMatch

        $pinnedWords = @($manifest.words | ForEach-Object { [pscustomobject]@{ name = [string]$_.currentName; id = [string]$_.wordId } } | Sort-Object name)
        $expectedPinnedWords = @($pin.words | Sort-Object name)
        Assert-Check "$profile historical word count remains pinned" ($pinnedWords.Count -eq $expectedPinnedWords.Count)
        Assert-Check "$profile historical word IDs remain pinned" (
            (@($pinnedWords | ForEach-Object { "$($_.name)|$($_.id)" }) -join ',') -ceq
            (@($expectedPinnedWords | ForEach-Object { "$($_.name)|$($_.id)" }) -join ',')
        )

        $refused = $false
        try { & (Join-Path $repo 'scripts/Restore-MatchedRenewalSnapshot.ps1') -Profile $profile -ProjectPath $artifactProject | Out-Null }
        catch { $refused = $_.Exception.Message -like '*new project directory*' }
        Assert-Check "$profile restore refuses existing project" $refused

        $fixtureProject = Join-Path $fixtureEvidenceRoot "$profile-$([guid]::NewGuid().ToString('N'))/project"
        $fixture = New-CurrentMatchedRenewalFixture -CliDll $cli -RepoRoot $repo -Profile $profile -ProjectPath $fixtureProject -SnapshotName "matched-renewal-current-$profile-v1"
        Assert-Check "$profile current-format fixture passes the strict runtime round trip" ($fixture.passed -eq $true)
        $currentManifestIdentityMatches = if ($profile -eq 'growing') {
            [string]$fixture.currentManifestHash -cne $pin.manifestHash
        } else {
            [string]$fixture.currentManifestHash -ceq $pin.manifestHash
        }
        Assert-Check "$profile current-format manifest follows its authored fixture identity" $currentManifestIdentityMatches
        $currentTests = @($fixture.reloadedTestResults)
        Assert-Check "$profile current-format test count remains unchanged" ($currentTests.Count -eq $(if ($profile -eq 'growing') {7} else {0}))
        Assert-Check "$profile current-format tests all pass" (@($currentTests | Where-Object { $_.passed -ne $true }).Count -eq 0)
        $traces.Add([ordered]@{
            profile = $profile
            bundlePath = $bundle
            bundleSha256 = $bundleHash
            pinnedManifestHash = $pin.manifestHash
            restore = $restore
            copiedFiles = @($meta.files)
            pinnedWords = $pinnedWords
            currentFixtureEvidencePath = (Join-Path $fixture.projectPath 'current-fixture-evidence.json')
            currentFixture = $fixture
        })
    }
    # A copied bundle with valid JSON but changed bytes must fail before any
    # destination is created. Never mutate the authoritative public bundle.
    $negativeRoot = Join-Path $repo ('.agentlang/matched-snapshot-negative/' + [guid]::NewGuid().ToString('N'))
    $negativeScripts = Join-Path $negativeRoot 'scripts'
    $negativeBundles = Join-Path $negativeRoot 'experiments/AgentLang.SubagentTrials/matched-renewal-001/snapshots'
    [void](New-Item -ItemType Directory -Path $negativeScripts -Force)
    [void](New-Item -ItemType Directory -Path $negativeBundles -Force)
    Copy-Item -LiteralPath (Join-Path $repo 'scripts/Restore-MatchedRenewalSnapshot.ps1') -Destination $negativeScripts
    Copy-Item -LiteralPath (Join-Path $repo 'experiments/AgentLang.SubagentTrials/matched-renewal-001/snapshots/flat') -Destination $negativeBundles -Recurse
    [IO.File]::AppendAllText((Join-Path $negativeBundles 'flat/store/CURRENT'), "`n", [Text.UTF8Encoding]::new($false))
    $negativeProject = Join-Path $negativeRoot 'project'
    $rejected = $false
    try { & (Join-Path $negativeScripts 'Restore-MatchedRenewalSnapshot.ps1') -Profile flat -ProjectPath $negativeProject | Out-Null }
    catch { $rejected = $_.Exception.Message -like '*hash/size mismatch*' }
    Assert-Check 'modified bundle rejected' $rejected
    Assert-Check 'modified bundle creates no project' (-not (Test-Path -LiteralPath $negativeProject))
    $passed = $true
} catch { $failure = $_.Exception.Message }
finally {
    $evidence = [ordered]@{schemaVersion=1;passed=$passed;failure=$failure;cliSha256=(Get-FileHash -LiteralPath $cli -Algorithm SHA256).Hash.ToLowerInvariant();checks=$checks.ToArray();traces=$traces.ToArray();limits='Frozen bundle checks validate artifact integrity separately from disposable current-format runtime fixtures; no agent trial or memory/performance measurement.'}
    $target = [IO.Path]::GetFullPath($EvidencePath, $repo)
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
    [IO.File]::WriteAllText($target, (ConvertTo-Json -InputObject $evidence -Depth 64), [Text.UTF8Encoding]::new($false))
}
if (-not $passed) { throw $failure }
Write-Output "$($checks.Count) frozen snapshot checks passed. Evidence: $target"
