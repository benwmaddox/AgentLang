#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$TracePath,[Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$run=Split-Path $TracePath -Parent
$pin=Get-Content (Join-Path $run 'prelaunch.json') -Raw|ConvertFrom-Json -Depth 100
if((Get-FileHash (Join-Path $PSScriptRoot '070-verify-strong-type-trial.ps1')).Hash.ToLowerInvariant() -cne $pin.independentAcceptanceScriptSha256){throw 'Independent oracle changed after freeze.'}
if((Get-FileHash (Join-Path $run 'starting-state.json')).Hash.ToLowerInvariant() -cne $pin.startingStateSha256){throw 'Starting inventory changed after freeze.'}
& (Join-Path $PSScriptRoot '068-audit-debug-trace.ps1') -TracePath $TracePath -OutputPath $OutputPath
$result=Get-Content $OutputPath -Raw|ConvertFrom-Json -Depth 100
$result.limits[3]='One strong-type usability smoke; no matched conventional comparison.'
$result|Add-Member -NotePropertyName independentAcceptanceScriptSha256 -NotePropertyValue $pin.independentAcceptanceScriptSha256
$result|Add-Member -NotePropertyName startingStateSha256 -NotePropertyValue $pin.startingStateSha256
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath,$repo),($result|ConvertTo-Json -Depth 100),[Text.UTF8Encoding]::new($false))
