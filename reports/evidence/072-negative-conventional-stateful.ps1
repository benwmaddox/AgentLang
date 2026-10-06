#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$control=Join-Path $repo '.agentlang/stateful-conventional-001/acceptance-control'
$negative=Join-Path $repo '.agentlang/stateful-conventional-001/acceptance-wrong-empty'
$run=Join-Path $repo 'experiments/AgentLang.SubagentTrials/stateful-conventional-001/runs/conventional-001'
if(Test-Path $negative){throw 'Refusing to overwrite negative control.'}
[IO.Directory]::CreateDirectory($negative)|Out-Null
Get-ChildItem $control -File|ForEach-Object{Copy-Item -LiteralPath $_.FullName -Destination $negative}
$path=Join-Path $negative 'Operations.fs'
$source=[IO.File]::ReadAllText($path)
$old='if files.Exists path then files.Read path'
$replacement=@'
if files.Exists path then
                let marker = files.Read path
                if marker = "" then "queued" else marker
'@
if($source.IndexOf($old,[StringComparison]::Ordinal) -lt 0){throw 'Negative control anchor missing.'}
[IO.File]::WriteAllText($path,$source.Replace($old,$replacement),[Text.UTF8Encoding]::new($false))
$resultPath=Join-Path $run 'wrong-empty-acceptance.json'
try { & (Join-Path $PSScriptRoot '072-verify-conventional-stateful.ps1') -ProjectPath $negative -OutputPath $resultPath }
catch {
    $result=Get-Content $resultPath -Raw|ConvertFrom-Json -Depth 100
    if(-not $result.passed -and $result.failure -like '*existing empty marker returns an empty string without rewriting*') {
        Write-Output 'Wrong empty-marker control rejected by independent behavior after own tests passed.'
        exit 0
    }
    throw
}
throw 'Incorrect control was accepted.'
