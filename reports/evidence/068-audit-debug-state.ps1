#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$FinalProject,[Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$seed=Join-Path $repo 'experiments/AgentLang.SubagentTrials/debug-flow-001/runs/debug-001/starting-project'
$final=(Resolve-Path $FinalProject).Path
$checks=[Collections.Generic.List[object]]::new()
function Check($Name,$Value){$checks.Add(@{name=$Name;passed=[bool]$Value});if(-not $Value){throw $Name}}
function Canonical($Value){ConvertTo-Json -InputObject $Value -Depth 100 -Compress}
function Manifest($Root){
    $head=Get-Content (Join-Path $Root '.agentlang/store/CURRENT') -Raw|ConvertFrom-Json
    $path=Join-Path $Root ".agentlang/store/manifests/$($head.manifestHash).json"
    Check 'manifest hash matches CURRENT' ((Get-FileHash $path).Hash.ToLowerInvariant() -ceq $head.manifestHash)
    Get-Content $path -Raw|ConvertFrom-Json -Depth 100
}
function CurrentRevision($Manifest,$Name){$word=@($Manifest.words|Where-Object currentName -eq $Name);if($word.Count -ne 1){throw 'Missing unique word'};@($Manifest.revisions|Where-Object{$_.wordId -eq $word[0].wordId -and $_.revision -eq $word[0].currentRevision})[0]}
function TestMap($Root,$Revision){
    $map=@{}
    foreach($ref in $Revision.tests){
        $path=Join-Path $Root ".agentlang/store/objects/$($ref.hash).agent"
        Check 'test object content hash' ((Get-FileHash $path).Hash.ToLowerInvariant() -ceq $ref.hash)
        $body=Get-Content $path -Raw
        $match=[regex]::Match($body,'^test\s+([^\s]+)\s*\{')
        if(-not $match.Success){throw 'Unexpected Flow test source'}
        $map[$match.Groups[1].Value]=@{hash=$ref.hash;source=$body}
    }
    return $map
}
$failure=$null
try{
    $before=Manifest $seed;$after=Manifest $final
    Check 'exact retained type definitions' ((Canonical $before.types) -ceq (Canonical $after.types))
    Check 'same word inventory and stable identities' ((Canonical @($before.words|Sort-Object currentName|Select-Object currentName,wordId)) -ceq (Canonical @($after.words|Sort-Object currentName|Select-Object currentName,wordId)))
    foreach($name in @('customer.premium?','subscription.annual-renewable?')){
        Check "unrelated current word unchanged $name" ((Canonical @($before.words|Where-Object currentName -eq $name)) -ceq (Canonical @($after.words|Where-Object currentName -eq $name)))
        Check "unrelated revisions unchanged $name" ((Canonical @($before.revisions|Where-Object name -eq $name)) -ceq (Canonical @($after.revisions|Where-Object name -eq $name)))
    }
    foreach($old in $before.revisions){$retained=@($after.revisions|Where-Object{$_.wordId -eq $old.wordId -and $_.revision -eq $old.revision});Check 'exact immutable history retained' ($retained.Count -eq 1 -and (Canonical $retained[0]) -ceq (Canonical $old))}
    $newCaseCount=0
    foreach($name in @('customer.discounted-balance','customer.renewal-balance')){
        $old=CurrentRevision $before $name;$current=CurrentRevision $after $name
        Check "pure library revision retained $name" ($current.maturity -eq 'library' -and -not $current.deprecated)
        if($name -eq 'customer.renewal-balance'){Check 'caller implementation unchanged' ($old.definition.hash -ceq $current.definition.hash)}else{Check 'shared helper revised in place' ($current.revision -gt $old.revision -and $current.definition.hash -cne $old.definition.hash)}
        $oldTests=TestMap $seed $old;$newTests=TestMap $final $current
        foreach($key in $oldTests.Keys){
            Check "existing test name retained $key" ($newTests.ContainsKey($key))
            if($key -eq 'customer.discounted-balance/negative-balance-clamped'){Check 'mistaken expectation corrected' ($newTests[$key].hash -cne $oldTests[$key].hash)}else{Check "valid test source unchanged $key" ($newTests[$key].hash -ceq $oldTests[$key].hash)}
        }
        $newCaseCount+=@($newTests.Keys|Where-Object{-not $oldTests.ContainsKey($_)}).Count
    }
    Check 'new regression case attached' ($newCaseCount -gt 0)
    foreach($file in Get-ChildItem $seed -File -Recurse -Force){
        $relative=[IO.Path]::GetRelativePath($seed,$file.FullName)
        if($relative -in @('dictionary.agent','.agentlang\store\CURRENT')){continue}
        $path=Join-Path $final $relative
        Check "immutable seed file retained $relative" ((Test-Path $path) -and (Get-FileHash $path).Hash -ceq (Get-FileHash $file.FullName).Hash)
    }
}catch{$failure=$_.Exception.Message}
$result=@{schemaVersion=1;passed=($null -eq $failure);failure=$failure;checks=$checks.ToArray();scope='Exact type/unrelated-word/caller-body preservation; stable helper identity with revised current definition; all old history and immutable files retained; all valid old cases byte-exact, mistaken case revised, and new regression case required. Behavior and pure effects checked separately.'}
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath,$repo),($result|ConvertTo-Json -Depth 100),[Text.UTF8Encoding]::new($false))
if($failure){throw $failure}
Write-Output "Debug state audit passed $($checks.Count) checks."
