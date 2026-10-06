#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$FinalProject,[Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$seed=Join-Path $repo 'experiments/AgentLang.SubagentTrials/refactor-flow-001/runs/refactor-001/starting-project'
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
$failure=$null
try{
    $before=Manifest $seed;$after=Manifest $final
    Check 'exact type definitions retained' ((Canonical $before.types) -ceq (Canonical $after.types))
    Check 'same word inventory and stable identities' ((Canonical @($before.words|Sort-Object currentName|Select-Object currentName,wordId)) -ceq (Canonical @($after.words|Sort-Object currentName|Select-Object currentName,wordId)))
    foreach($name in @('customer.premium?','subscription.annual-renewable?')){
        Check "unrelated current word unchanged $name" ((Canonical @($before.words|Where-Object currentName -eq $name)) -ceq (Canonical @($after.words|Where-Object currentName -eq $name)))
        Check "unrelated revisions unchanged $name" ((Canonical @($before.revisions|Where-Object name -eq $name)) -ceq (Canonical @($after.revisions|Where-Object name -eq $name)))
    }
    foreach($old in $before.revisions){$retained=@($after.revisions|Where-Object{$_.wordId -eq $old.wordId -and $_.revision -eq $old.revision});Check 'exact historical revision retained' ($retained.Count -eq 1 -and (Canonical $retained[0]) -ceq (Canonical $old))}
    foreach($name in @('customer.discounted-balance','customer.renewal-balance')){
        $old=CurrentRevision $before $name;$current=CurrentRevision $after $name
        Check "revised existing library word $name" ($current.revision -gt $old.revision -and $current.definition.hash -cne $old.definition.hash -and $current.maturity -eq 'library' -and -not $current.deprecated)
        Check "exact existing test objects retained $name" ((Canonical @($old.tests|Sort-Object hash)) -ceq (Canonical @($current.tests|Sort-Object hash)))
        Check "exact examples retained $name" ((Canonical $old.examples) -ceq (Canonical $current.examples))
    }
    foreach($file in Get-ChildItem $seed -File -Recurse -Force){
        $relative=[IO.Path]::GetRelativePath($seed,$file.FullName)
        if($relative -in @('dictionary.agent','.agentlang\store\CURRENT')){continue}
        $path=Join-Path $final $relative
        Check "immutable seed file retained $relative" ((Test-Path $path) -and (Get-FileHash $path).Hash -ceq (Get-FileHash $file.FullName).Hash)
    }
    $folder=Split-Path $OutputPath -Parent
    foreach($pair in @(@{file='after-task-2.json';expected='customer.balance|customer.premium?|float.multiply'},@{file='after-task-4.json';expected='customer.discounted-balance|float.multiply|subscription.annual-renewable?'})){
        $acceptance=Get-Content (Join-Path $folder $pair.file) -Raw|ConvertFrom-Json -Depth 100
        Check "independent acceptance passed $($pair.file)" ($acceptance.passed -and $acceptance.checks.Count -eq 48)
        Check "required direct dependencies $($pair.file)" (((@($acceptance.responses[1].data.dependencies)|Sort-Object)-join '|') -ceq $pair.expected)
    }
}catch{$failure=$_.Exception.Message}
$result=@{schemaVersion=1;passed=($null -eq $failure);failure=$failure;checks=$checks.ToArray();scope='Stable inventory/identities; exact type/unrelated-word/test/example/history preservation; in-place revisions; retained immutable files; independent behavior/library coverage and exact direct dependency sets proving reuse instead of duplicated classification.'}
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath,$repo),($result|ConvertTo-Json -Depth 100),[Text.UTF8Encoding]::new($false))
if($failure){throw $failure}
Write-Output "Refactor state and structure audit passed $($checks.Count) checks."
