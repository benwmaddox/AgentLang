#requires -Version 7.5
[CmdletBinding()]
param([string]$RepositoryRoot = 'D:\code\AgentLang')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$study = Join-Path $RepositoryRoot 'experiments/AgentLang.SubagentTrials/paid-invoice-maintenance-141'
$evidence = Join-Path $RepositoryRoot '.agentlang/maintenance-141'
$destination = Join-Path $PSScriptRoot 'input-freeze.json'
if (Test-Path -LiteralPath $destination) { throw 'Refusing to overwrite an existing input freeze.' }
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Inventory([string]$Root) {
    @(Get-ChildItem -LiteralPath $Root -Recurse -File -Force |
        Where-Object { [IO.Path]::GetRelativePath($Root, $_.FullName) -notmatch '(^|[\\/])(bin|obj)([\\/]|$)' } |
        ForEach-Object { [ordered]@{path=[IO.Path]::GetRelativePath($Root,$_.FullName).Replace('\','/');sha256=Hash $_.FullName} } |
        Sort-Object { $_.path })
}
$fsharp = Get-Content (Join-Path $evidence 'fsharp-scoring/preflight-controls.json') -Raw | ConvertFrom-Json -AsHashtable -DateKind String
if ($fsharp.status -ne 'passed') { throw 'F# controls have not passed.' }
foreach ($path in $fsharp.sourceSha256Before.Keys) {
    if ($fsharp.sourceSha256Before[$path] -ne $fsharp.sourceSha256After[$path] -or
        (Hash (Join-Path $RepositoryRoot $path)) -ne $fsharp.sourceSha256After[$path]) { throw "F# preflight input changed: $path" }
}
$vectors = @{baseline=@($false,$false,$false,$false,$false,$false,$true,$true,$true,$true,$true,$true);correct=@($true)*12;'wrong-positive'=@($false,$false,$false,$true,$true,$true,$true,$true,$true,$true,$true,$true)}
foreach ($name in $vectors.Keys) {
    $result = Get-Content (Join-Path $evidence "root-control-audit/$name.json") -Raw | ConvertFrom-Json -AsHashtable -DateKind String
    if ($result.total -ne 12 -or $result.oracleSha256 -ne (Hash (Join-Path $study 'oracle.json'))) { throw "Wrong Flow result contract: $name" }
    for ($index=0; $index -lt 12; $index++) {
        if ($null -ne $result.results[$index].error -or $result.results[$index].passed -ne $vectors[$name][$index]) { throw "Wrong Flow control outcome: $name/$index" }
    }
}
$actors = Get-Content (Join-Path $PSScriptRoot 'participants.json') -Raw | ConvertFrom-Json -AsHashtable -DateKind String
$actorInputs = @(foreach ($actor in $actors) {
    if ((Hash $actor.prompt) -ne $actor.promptSha256 -or (Hash $actor.wrapper) -ne $actor.wrapperSha256) { throw "Prompt changed: $($actor.id)" }
    $start = Join-Path $study "starts/$($actor.arm)/project"
    $startFiles = Inventory $start
    $actorFiles = Inventory $actor.project
    if ((ConvertTo-Json -InputObject $startFiles -Depth 5 -Compress) -cne (ConvertTo-Json -InputObject $actorFiles -Depth 5 -Compress)) { throw "Actor differs from frozen start: $($actor.id)" }
    [ordered]@{id=$actor.id;arm=$actor.arm;project=$actor.project;files=$actorFiles;promptSha256=$actor.promptSha256;wrapperSha256=$actor.wrapperSha256}
})
$studyFiles = @(Inventory $study | Where-Object { $_.path -notmatch '^(starts|artifacts)/' -and $_.path -notmatch '\.(jsonl)$' -and $_.path -notmatch '/baseline-control\.json$' })
$build = Get-Content (Join-Path $evidence 'build-provenance.json') -Raw | ConvertFrom-Json -AsHashtable -DateKind String
foreach ($source in $build.sources) { if ((Hash (Join-Path $RepositoryRoot $source.path)) -ne $source.sha256) { throw "Build source changed: $($source.path)" } }
foreach ($artifact in $build.artifacts) { if ((Hash (Join-Path $evidence $artifact.path)) -ne $artifact.sha256) { throw "Runtime artifact changed: $($artifact.path)" } }
$freeze = [ordered]@{
    status='frozen-before-dispatch';utc=[DateTime]::UtcNow.ToString('o');commit=(git -C $RepositoryRoot rev-parse HEAD)
    participants=$actorInputs;studyFiles=$studyFiles;build=$build
    broker=[ordered]@{path='scripts/Start-SubagentTrialHostV2.ps1';sha256=Hash (Join-Path $RepositoryRoot 'scripts/Start-SubagentTrialHostV2.ps1')}
    fsharpPreflightSha256=Hash (Join-Path $evidence 'fsharp-scoring/preflight-controls.json')
    flowAudit=@(Inventory (Join-Path $evidence 'root-control-audit'))
}
[IO.File]::WriteAllText($destination, (ConvertTo-Json -InputObject $freeze -Depth 16), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'input-freeze.sha256'), ((Hash $destination) + "  input-freeze.json`n"), [Text.UTF8Encoding]::new($false))
Write-Output "Frozen $($actorInputs.Count) participants after matching starts, prompts, controls, build sources and runtime artifacts."
