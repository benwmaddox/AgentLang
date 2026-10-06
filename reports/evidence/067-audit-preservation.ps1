#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$FinalProject,[Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$seed=Join-Path $repo 'experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/growing-task-3/final-project'
$final=(Resolve-Path -LiteralPath $FinalProject).Path
$checks=[Collections.Generic.List[object]]::new()
function Check($Name,$Passed) { $checks.Add(@{name=$Name;passed=[bool]$Passed}); if(-not $Passed){throw $Name} }
function Manifest($Root) {
    $head=Get-Content -LiteralPath (Join-Path $Root '.agentlang/store/CURRENT') -Raw | ConvertFrom-Json
    $path=Join-Path $Root ".agentlang/store/manifests/$($head.manifestHash).json"
    Check 'current manifest content hash' ((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ceq $head.manifestHash)
    Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -Depth 100
}
function Canonical($Value) { ConvertTo-Json -InputObject $Value -Depth 100 -Compress }
$failure=$null
try {
    $before=Manifest $seed
    $after=Manifest $final
    Check 'exact retained type definitions and source format' ((Canonical $before.types) -ceq (Canonical $after.types))
    Check 'only required new persistent word' ($after.words.Count -eq 4 -and @($after.words | Where-Object currentName -eq 'customer.renewal-balance').Count -eq 1)
    foreach($word in $before.words) {
        $actual=@($after.words | Where-Object wordId -eq $word.wordId)
        Check "retained current identity/revision/name $($word.currentName)" ($actual.Count -eq 1 -and (Canonical $actual[0]) -ceq (Canonical $word))
        $oldRevisions=@($before.revisions | Where-Object wordId -eq $word.wordId)
        $newRevisions=@($after.revisions | Where-Object wordId -eq $word.wordId)
        Check "retained exact definitions/tests/source bindings/history $($word.currentName)" ((Canonical $oldRevisions) -ceq (Canonical $newRevisions))
    }
    foreach($file in Get-ChildItem -LiteralPath $seed -File -Recurse -Force) {
        $relative=[IO.Path]::GetRelativePath($seed,$file.FullName)
        if($relative -in @('dictionary.agent','.agentlang\store\CURRENT')) { continue }
        $actualPath=Join-Path $final $relative
        Check "retained file $relative" ((Test-Path -LiteralPath $actualPath) -and (Get-FileHash -LiteralPath $actualPath).Hash -ceq (Get-FileHash -LiteralPath $file.FullName).Hash)
    }
} catch { $failure=$_.Exception.Message }
$evidence=@{schemaVersion=1;passed=($null -eq $failure);failure=$failure;seed=$seed;finalProject=$final;checks=$checks.ToArray();scope='Exact current manifest types, prior word identities/revisions and full revision metadata including definition/test object hashes, source formats and call bindings; all immutable seed files retained byte-for-byte. Passing retained tests checked separately by Task 4 acceptance.'}
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath,$repo),($evidence | ConvertTo-Json -Depth 100),[Text.UTF8Encoding]::new($false))
if($failure){throw $failure}
Write-Output "Preservation passed $($checks.Count) checks."
