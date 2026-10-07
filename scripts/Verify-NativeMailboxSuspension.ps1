#requires -Version 7.0
[CmdletBinding()]
param([string]$EvidencePath)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runRoot = Join-Path $repo ('.agentlang/native-mailbox/run-' + [Guid]::NewGuid().ToString('N'))
$buildRoot = Join-Path $runRoot 'build'
$nativeRoot = Join-Path $runRoot 'native'
$runReport = Join-Path $runRoot 'result.json'
$project = Join-Path $repo 'experiments/AgentLang.NativeMailbox/AgentLang.NativeMailbox.fsproj'
$report = if ([string]::IsNullOrWhiteSpace($EvidencePath)) {
    Join-Path $runRoot 'result.json'
} else {
    [IO.Path]::GetFullPath($EvidencePath, $repo)
}
$null = [IO.Directory]::CreateDirectory($runRoot)
$null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($report))
Push-Location $repo
try {
    dotnet build $project --artifacts-path $buildRoot 2>&1 | Tee-Object -FilePath (Join-Path $runRoot 'build.log')
    if ($LASTEXITCODE -ne 0) { throw "Mailbox experiment build failed: $LASTEXITCODE" }
    $assemblies = @(Get-ChildItem -LiteralPath (Join-Path $buildRoot 'bin') -Recurse -File -Filter 'AgentLang.NativeMailbox.dll')
    if ($assemblies.Count -ne 1) { throw 'Expected exactly one freshly built mailbox experiment assembly.' }
    dotnet $assemblies[0].FullName --output $runReport --artifacts $nativeRoot 2>&1 | Tee-Object -FilePath (Join-Path $runRoot 'run.log')
    if ($LASTEXITCODE -ne 0) { throw "Mailbox experiment failed: $LASTEXITCODE" }
    if (-not (Test-Path -LiteralPath $runReport -PathType Leaf)) { throw 'Mailbox experiment did not write evidence.' }
    $result = Get-Content -LiteralPath $runReport -Raw | ConvertFrom-Json
    if ($result.passed -ne $true) { throw 'Mailbox experiment evidence does not report success.' }
    $oraclePath = Join-Path $repo 'reports/evidence/108-native-mailbox-suspension/oracle.json'
    $oracle = Get-Content -LiteralPath $oraclePath -Raw | ConvertFrom-Json
    $runs = @($result.runs)
    $labels = @($runs | ForEach-Object { $_.backend + '/' + $_.optimization } | Sort-Object)
    if (($labels -join ',') -ne 'interpreter/Core,native/O0,native/O2') { throw 'Missing or duplicated backend results.' }
    if ($result.sameVerifiedProgramInstance -ne $true) { throw 'Handlers did not share a verified program instance.' }
    if (@($result.checks).Count -eq 0 -or @($result.checks | Where-Object { $_.passed -ne $true }).Count -ne 0) { throw 'Missing or failed ownership checks.' }
    foreach ($run in $runs) {
        if ($run.aAfterOverlap -ne $oracle.firstCompleted.A -or $run.bAfterOverlap -ne $oracle.firstCompleted.B -or $run.aFinal -ne $oracle.secondA.completed) { throw 'Handler outcomes differ from the frozen independent oracle.' }
        if ($run.languageFailureCode -ne 'RUNTIME_DIVIDE_BY_ZERO') { throw 'Expected handler failure was not observed.' }
        if ($run.scratchLeaseAcquisitions -ne $run.scratchLeaseReturns -or @($run.suspensionLeaseCounts | Where-Object { $_ -ne 0 }).Count -ne 0) { throw 'Scratch lease ownership is unbalanced.' }
        if ($run.backend -eq 'native') {
            if ($run.scratchArenaOwnersCreated -ne $oracle.nativeCapacity.pool.arenas -or $run.retainedMaximumBytes -ne $oracle.nativeCapacity.begin.liveRetainedBytes -or $run.retainedMaximumNodes -ne $oracle.nativeCapacity.begin.liveRetainedNodes) { throw 'Native capacity totals differ from the frozen oracle.' }
            if ($run.capacityFailureCode -ne 'NATIVE_RETAINED_CAPACITY' -or $run.capacityRequiredBytes -ne $oracle.nativeCapacity.resume.liveRetainedBytes -or $run.capacityRequiredNodes -ne $oracle.nativeCapacity.resume.liveRetainedNodes -or $run.capacityAvailableBytes -ne 7) { throw 'One-short retained capacity did not fail as expected.' }
        }
    }
    if ($runReport -ne $report) { Copy-Item -LiteralPath $runReport -Destination $report }
    Write-Output "Mailbox suspension evidence: $report"
    Write-Output "Fresh build and native artifacts: $runRoot"
}
finally {
    Pop-Location
}
