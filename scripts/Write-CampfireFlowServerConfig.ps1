<#
.SYNOPSIS
Writes the private config file for the Campfire Flow server (scripts/Build-CampfireFlowServer.ps1).

.DESCRIPTION
Reads SECRET_KEY_BASE from a fixture's reference.env (docker --env-file format)
and writes `secret_key_base=` and `authority=127.0.0.1:<port>` lines to -Out,
which must lie outside the repository or under its git-ignored .agentlang
directory. The secret is never printed.

.EXAMPLE
scripts/Write-CampfireFlowServerConfig.ps1 -ReferenceEnv D:\code\AgentLang\.agentlang\once-campfire-verification\fixtures\default\reference.env -Port 13773 -Out D:\code\AgentLang\.agentlang\campfire-flow-server\private\config.txt
#>
param(
    [Parameter(Mandatory)] [string] $ReferenceEnv,
    [Parameter(Mandatory)] [ValidateRange(1, 65535)] [int] $Port,
    [Parameter(Mandatory)] [string] $Out
)
$ErrorActionPreference = 'Stop'
$repo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$target = [System.IO.Path]::GetFullPath($Out)
$ignored = [System.IO.Path]::GetFullPath((Join-Path $repo '.agentlang')) + [System.IO.Path]::DirectorySeparatorChar
if ($target.StartsWith($repo + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
    -not $target.StartsWith($ignored, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The config holds secret_key_base: write it outside the repository or under .agentlang.'
}
$line = Get-Content -LiteralPath $ReferenceEnv | Where-Object { $_ -like 'SECRET_KEY_BASE=*' } | Select-Object -First 1
if (-not $line) { throw 'reference.env has no SECRET_KEY_BASE.' }
$secret = $line.Substring('SECRET_KEY_BASE='.Length).Trim()
New-Item -ItemType Directory -Force (Split-Path -Parent $target) | Out-Null
[System.IO.File]::WriteAllText($target, "secret_key_base=$secret`nauthority=127.0.0.1:$Port`n", [System.Text.UTF8Encoding]::new($false))
Write-Output "wrote $target"
