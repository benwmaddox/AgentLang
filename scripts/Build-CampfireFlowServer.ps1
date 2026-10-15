<#
.SYNOPSIS
Builds Campfire's room-page server on the portable Flow HTTP host for Windows or Linux.

.DESCRIPTION
Loads the shared Campfire Flow words the room GET uses (policies, views, CSRF,
user agent, session restore) plus applications/campfire/server/*.flow, and builds
them with scripts/Build-FlowHttpServer.ps1 (app campfire.server). Run the result
with a config file written by scripts/Write-CampfireFlowServerConfig.ps1.

.EXAMPLE
scripts/Build-CampfireFlowServer.ps1 -Target linux-x64 -OutDir .agentlang/campfire-flow-server/linux
#>
param(
    [ValidateSet('windows-x64', 'linux-x64')] [string] $Target = 'windows-x64',
    [ValidateSet('trusted', 'diagnostic')] [string] $RuntimeProfile = 'trusted',
    [ValidateSet('O0', 'O2')] [string] $Optimization = 'O2',
    [Parameter(Mandatory)] [string] $OutDir,
    [string] $LinuxName = 'campfire',
    [switch] $Profile
)
$ErrorActionPreference = 'Stop'
$repo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$campfire = Join-Path $repo 'applications/campfire'
$shared = @(
    'campfire-types.flow', 'authentication-types.flow', 'http-types.flow', 'session-policy.flow',
    'request-policy.flow', 'browser-types.flow', 'browser-policy.flow', 'user-agent.flow', 'csrf.flow',
    'views.flow', 'layout.flow', 'message.flow', 'authentication.flow',
    # Attached tests: every committed word needs one.
    'authentication-tests.flow', 'csrf-tests.flow', 'views-tests.flow', 'browser-tests.flow', 'user-agent-tests.flow')
$sources = @($shared | ForEach-Object { Join-Path $campfire $_ })
$sources += @(Get-ChildItem -LiteralPath (Join-Path $campfire 'server') -Filter '*.flow' | Sort-Object Name | ForEach-Object FullName)
& (Join-Path $PSScriptRoot 'Build-FlowHttpServer.ps1') -App 'campfire.server' -Source $sources -Target $Target `
    -RuntimeProfile $RuntimeProfile -Optimization $Optimization -OutDir $OutDir -LinuxName $LinuxName -Profile:$Profile
