<#
.SYNOPSIS
Builds Campfire's room-page server on the portable Flow HTTP host for Windows or Linux.

.DESCRIPTION
Loads the shared Campfire Flow words the room GET uses (policies, views, CSRF,
user agent, session restore) plus applications/campfire/server/*.flow, and builds
them with scripts/Build-FlowHttpServer.ps1 (app campfire.server). Run the result
with a config file written by scripts/Write-CampfireFlowServerConfig.ps1.

-Unverified builds without running attached tests, at O0 unless -Optimization is
given: a runnable exe in seconds, marked UNVERIFIED. -TwoPart does that into
<OutDir>/unverified, then the verified O2 build into <OutDir>, so the first exe can be
used while the second (which runs every attached test) builds.

.EXAMPLE
scripts/Build-CampfireFlowServer.ps1 -Target linux-x64 -OutDir .agentlang/campfire-flow-server/linux

.EXAMPLE
scripts/Build-CampfireFlowServer.ps1 -TwoPart -OutDir .agentlang/campfire-flow-server/windows
#>
param(
    [ValidateSet('windows-x64', 'linux-x64')] [string] $Target = 'windows-x64',
    [ValidateSet('trusted', 'diagnostic')] [string] $RuntimeProfile = 'trusted',
    [ValidateSet('O0', 'O2')] [string] $Optimization = 'O2',
    [Parameter(Mandatory)] [string] $OutDir,
    [string] $LinuxName = 'campfire',
    [switch] $Profile,
    [switch] $Unverified,
    [switch] $TwoPart
)
$ErrorActionPreference = 'Stop'
$repo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$campfire = Join-Path $repo 'applications/campfire'
$shared = @(
    'campfire-types.flow', 'authentication-types.flow', 'http-types.flow', 'session-policy.flow',
    'request-policy.flow', 'browser-types.flow', 'browser-policy.flow', 'user-agent.flow',
    'views.flow', 'layout.flow', 'emoji.flow', 'sounds.flow', 'message.flow', 'presentation.flow', 'sidebar.flow', 'search.flow', 'users.flow', 'avatars.flow', 'account.flow', 'authentication.flow',
    # Attached tests: every committed word needs one.
    'authentication-tests.flow', 'views-tests.flow', 'browser-tests.flow', 'user-agent-tests.flow')
$sources = @($shared | ForEach-Object { Join-Path $campfire $_ })
$sources += @(Get-ChildItem -LiteralPath (Join-Path $campfire 'server') -Filter '*.flow' | Sort-Object Name | ForEach-Object FullName)
$build = Join-Path $PSScriptRoot 'Build-FlowHttpServer.ps1'
$optimizationGiven = $PSBoundParameters.ContainsKey('Optimization')
if ($TwoPart) {
    $fastDir = Join-Path $OutDir 'unverified'
    $fast = & $build -App 'campfire.server' -Source $sources -Target $Target -RuntimeProfile $RuntimeProfile `
        -Optimization $(if ($optimizationGiven) { $Optimization } else { 'O0' }) -OutDir $fastDir -LinuxName "$LinuxName-unverified" -Profile:$Profile -Unverified |
        Select-Object -Last 1
    Write-Host "part 1 ready (UNVERIFIED, attached tests not run): $fast"
    $verified = & $build -App 'campfire.server' -Source $sources -Target $Target -RuntimeProfile $RuntimeProfile `
        -Optimization $Optimization -OutDir $OutDir -LinuxName $LinuxName -Profile:$Profile |
        Select-Object -Last 1
    Write-Host "part 2 verified (every attached test passed): $verified"
    $verified
}
elseif ($Unverified) {
    & $build -App 'campfire.server' -Source $sources -Target $Target -RuntimeProfile $RuntimeProfile `
        -Optimization $(if ($optimizationGiven) { $Optimization } else { 'O0' }) -OutDir $OutDir -LinuxName $LinuxName -Profile:$Profile -Unverified
}
else {
    & $build -App 'campfire.server' -Source $sources -Target $Target -RuntimeProfile $RuntimeProfile `
        -Optimization $Optimization -OutDir $OutDir -LinuxName $LinuxName -Profile:$Profile
}
