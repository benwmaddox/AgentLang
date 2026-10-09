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
$checks = [Collections.Generic.List[object]]::new()
$traces = [Collections.Generic.List[object]]::new()
$passed = $false
$failure = $null
function Assert-Check([string]$Name, [bool]$Passed) {
    $checks.Add([ordered]@{name=$Name;passed=$Passed})
    if (-not $Passed) { throw "Snapshot check failed: $Name" }
}
try {
    foreach ($profile in @('flat', 'growing')) {
        $bundle = Join-Path $repo "experiments/AgentLang.SubagentTrials/matched-renewal-001/snapshots/$profile"
        $meta = Get-Content -LiteralPath (Join-Path $bundle 'bundle.json') -Raw | ConvertFrom-Json
        $seed = Join-Path $repo "experiments/AgentLang.SubagentTrials/matched-renewal-001/fixtures/flow/$profile.agent"
        Assert-Check "$profile source hash" ((Get-FileHash -LiteralPath $seed -Algorithm SHA256).Hash.ToLowerInvariant() -eq $meta.seedSourceSha256)
        $project = Join-Path $repo ('.agentlang/matched-snapshot-verification/' + $profile + '-' + [guid]::NewGuid().ToString('N'))
        $restoreText = @(& (Join-Path $repo 'scripts/Restore-MatchedRenewalSnapshot.ps1') -Profile $profile -ProjectPath $project)
        $restore = ($restoreText -join "`n") | ConvertFrom-Json
        $requests = @(@{op='snapshot.load';args=@{name=$meta.snapshot}},@{op='words'},@{op='test-all'})
        $lines = @($requests | ForEach-Object { ConvertTo-Json -InputObject $_ -Depth 8 -Compress })
        $output = @($lines | & dotnet $cli --project $project --filesystem virtual --jsonl)
        $exitCode = $LASTEXITCODE
        Assert-Check "$profile process exit" ($exitCode -eq 0)
        Assert-Check "$profile response count" ($output.Count -eq $requests.Count)
        $responses = @($output | ForEach-Object { ConvertFrom-Json -InputObject $_ -Depth 64 })
        $traces.Add([ordered]@{profile=$profile;restore=$restore;requests=$requests;responses=$responses;exitCode=$exitCode})
        Assert-Check "$profile protocol succeeds" (@($responses | Where-Object { -not $_.ok }).Count -eq 0)
        Assert-Check "$profile exact manifest" ($responses[0].data.manifestHash -eq $meta.manifestHash)
        $manifest = Get-Content -LiteralPath (Join-Path $bundle "store/manifests/$($meta.manifestHash).json") -Raw | ConvertFrom-Json
        $authored = @($responses[1].data.words | Where-Object { $_.id.StartsWith('word_', [StringComparison]::Ordinal) })
        Assert-Check "$profile authored count" ($authored.Count -eq $(if ($profile -eq 'growing') {2} else {0}))
        Assert-Check "$profile manifest word count" ($authored.Count -eq @($manifest.words).Count)
        foreach ($head in $manifest.words) {
            $actual = @($authored | Where-Object { $_.name -eq $head.currentName -and $_.id -eq $head.wordId })
            Assert-Check "$profile stable identity $($head.currentName)" ($actual.Count -eq 1 -and $actual[0].maturity -eq 'library' -and $actual[0].status -eq 'persistent')
        }
        Assert-Check "$profile renewal absent" (@($responses[1].data.words | Where-Object name -eq 'customer.renewal-balance').Count -eq 0)
        $tests = @($responses[2].data.results)
        Assert-Check "$profile test count" ($tests.Count -eq $(if ($profile -eq 'growing') {7} else {0}))
        Assert-Check "$profile tests pass" (@($tests | Where-Object { -not $_.passed }).Count -eq 0)
        $refused = $false
        try { & (Join-Path $repo 'scripts/Restore-MatchedRenewalSnapshot.ps1') -Profile $profile -ProjectPath $project | Out-Null }
        catch { $refused = $_.Exception.Message -like '*new project directory*' }
        Assert-Check "$profile restore refuses existing project" $refused
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
    $evidence = [ordered]@{schemaVersion=1;passed=$passed;failure=$failure;cliSha256=(Get-FileHash -LiteralPath $cli -Algorithm SHA256).Hash.ToLowerInvariant();checks=$checks.ToArray();traces=$traces.ToArray();limits='Frozen toy starting states only; no agent trial or memory/performance measurement.'}
    $target = [IO.Path]::GetFullPath($EvidencePath, $repo)
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
    [IO.File]::WriteAllText($target, (ConvertTo-Json -InputObject $evidence -Depth 64), [Text.UTF8Encoding]::new($false))
}
if (-not $passed) { throw $failure }
Write-Output "$($checks.Count) frozen snapshot checks passed. Evidence: $target"
