#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$EvidencePath,
    [switch]$NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$startedAt = [DateTimeOffset]::UtcNow
if ([string]::IsNullOrWhiteSpace($EvidencePath)) {
    $runName = $startedAt.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $EvidencePath = Join-Path $repo ".agentlang/campfire-logout-245/verification/$runName/receipt.json"
} elseif (-not [IO.Path]::IsPathRooted($EvidencePath)) {
    $EvidencePath = Join-Path $repo $EvidencePath
}
$EvidencePath = [IO.Path]::GetFullPath($EvidencePath)
$evidenceDirectory = Split-Path -Parent $EvidencePath
$null = New-Item -ItemType Directory -Force -Path $evidenceDirectory
$utf8 = [Text.UTF8Encoding]::new($false)
$checks = [Collections.Generic.List[object]]::new()
$pins = [Collections.Generic.List[object]]::new()
$previousAudit = $env:NuGetAudit
$previousTemp = $env:TEMP
$previousTmp = $env:TMP
$previousWorkloadResolver = $env:MSBuildEnableWorkloadResolver
$previousNodeReuse = $env:MSBUILDDISABLENODEREUSE
$temporaryDirectory = Join-Path $evidenceDirectory 'temp'
$null = New-Item -ItemType Directory -Force -Path $temporaryDirectory
$failed = $false
$revision = ''

function Invoke-Check([string]$Name, [string]$Executable, [string[]]$Arguments) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $logPath = Join-Path $evidenceDirectory "$Name.log"
    $output = @(& $Executable @Arguments 2>&1 | ForEach-Object { $_.ToString() })
    $exitCode = $LASTEXITCODE
    $timer.Stop()
    [IO.File]::WriteAllText($logPath, (($output -join "`n") + "`n"), $utf8)
    $checks.Add([ordered]@{
        name = $Name
        executable = $Executable
        arguments = $Arguments
        exitCode = $exitCode
        durationMilliseconds = $timer.ElapsedMilliseconds
        log = [IO.Path]::GetFileName($logPath)
        logSha256 = (Get-FileHash -LiteralPath $logPath -Algorithm SHA256).Hash.ToLowerInvariant()
    })
    Write-Host "$Name (exit $exitCode)"
    $output | ForEach-Object { Write-Host $_ }
    if ($exitCode -ne 0) { throw "Campfire check '$Name' failed; see '$logPath'." }
}

Push-Location -LiteralPath $repo
try {
    $env:NuGetAudit = 'true'
    $env:MSBuildEnableWorkloadResolver = 'false'
    $env:MSBUILDDISABLENODEREUSE = '1'
    $env:TEMP = $temporaryDirectory
    $env:TMP = $temporaryDirectory
    $revisionOutput = @(& git rev-parse HEAD)
    if ($LASTEXITCODE -ne 0 -or $revisionOutput.Count -ne 1) { throw 'Could not read repository revision.' }
    $revision = ([string]$revisionOutput[0]).Trim()

    $projects = [ordered]@{
        'cli' = 'src/AgentLang.Cli/AgentLang.Cli.fsproj'
        'persistence' = 'tests/AgentLang.Campfire.Persistence.Tests/AgentLang.Campfire.Persistence.Tests.fsproj'
        'integration' = 'tests/AgentLang.Campfire.Integration.Tests/AgentLang.Campfire.Integration.Tests.fsproj'
        'cookies' = 'tests/AgentLang.Campfire.Cookies.Tests/AgentLang.Campfire.Cookies.Tests.fsproj'
        'authentication-storage' = 'tests/AgentLang.Campfire.Authentication.Persistence.Tests/AgentLang.Campfire.Authentication.Persistence.Tests.fsproj'
        'authentication-integration' = 'tests/AgentLang.Campfire.Authentication.Integration.Tests/AgentLang.Campfire.Authentication.Integration.Tests.fsproj'
        'browser' = 'tests/AgentLang.Campfire.Browser.Tests/AgentLang.Campfire.Browser.Tests.fsproj'
        'http' = 'tests/AgentLang.Campfire.Http.Tests/AgentLang.Campfire.Http.Tests.fsproj'
        'sign-in' = 'tests/AgentLang.Campfire.SignIn.Tests/AgentLang.Campfire.SignIn.Tests.fsproj'
        'login-rate' = 'tests/AgentLang.Campfire.LoginRate.Tests/AgentLang.Campfire.LoginRate.Tests.fsproj'
    }
    foreach ($name in $projects.Keys) {
        $arguments = @('build', $projects[$name], '--configuration', $Configuration, '-m:1', '-nr:false')
        if ($NoRestore) { $arguments += '--no-restore' }
        Invoke-Check "build-$name" 'dotnet' $arguments
    }
    foreach ($name in @('persistence', 'integration', 'cookies', 'authentication-storage', 'authentication-integration', 'browser', 'http', 'sign-in', 'login-rate')) {
        Invoke-Check "test-$name" 'dotnet' @('run', '--project', $projects[$name], '--configuration', $Configuration, '--no-build', '--no-restore')
    }
    $cliPath = Join-Path $repo "src/AgentLang.Cli/bin/$Configuration/net9.0/AgentLang.Cli.dll"
    foreach ($scriptName in @('Verify-CampfirePolicy.ps1', 'Verify-CampfireRequestPolicy.ps1', 'Verify-CampfireSessionPolicy.ps1')) {
        Invoke-Check ([IO.Path]::GetFileNameWithoutExtension($scriptName)) 'pwsh' @('-NoProfile', '-File', (Join-Path $PSScriptRoot $scriptName), '-CliDll', $cliPath)
    }
    foreach ($relativePath in @(
        "src/AgentLang.Cli/bin/$Configuration/net9.0/AgentLang.Core.dll",
        "src/AgentLang.Cli/bin/$Configuration/net9.0/AgentLang.Cli.dll",
        'src/AgentLang.Core/Runtime.fs',
        'src/AgentLang.Core/IrInterpreter.fs',
        'src/AgentLang.Core/Core.fs',
        'src/AgentLang.Core/AuthoringHelp.fs',
        'tests/AgentLang.Flow.Runtime.Tests/Program.fs',
        'applications/campfire/message-policy.flow',
        'applications/campfire/campfire-types.flow',
        'applications/campfire/request-policy.flow',
        'applications/campfire/session-policy.flow',
        'applications/campfire/csrf.flow',
        'applications/campfire/csrf-tests.flow',
        'applications/campfire/room-message.flow',
        'applications/campfire/room-message-tests.flow',
        'applications/campfire/http-types.flow',
        'applications/campfire/http.flow',
        'applications/campfire/http-login.flow',
        'applications/campfire/http-login-tests.flow',
        'applications/campfire/Http/LoginHost.fs',
        'tests/AgentLang.Campfire.LoginRate.Tests/Program.fs',
        'tests/AgentLang.Campfire.LoginRate.Tests/AgentLang.Campfire.LoginRate.Tests.fsproj',
        'applications/campfire/http-tests.flow',
        'applications/campfire/browser-types.flow',
        'applications/campfire/browser-policy.flow',
        'applications/campfire/browser-tests.flow',
        'applications/campfire/user-agent.flow',
        'applications/campfire/user-agent-tests.flow',
        'applications/campfire/user-agent-corpus-tests.flow',
        'applications/campfire/Http/Application.fs',
        'applications/campfire/Http/CookieHost.fs',
        'applications/campfire/Http/ResponseRenderer.fs',
        'applications/campfire/Http/Transport.fs',
        'tests/AgentLang.Campfire.Browser.Tests/Program.fs',
        'tests/AgentLang.Campfire.Browser.Tests/native-browser.flow',
        'tests/AgentLang.Campfire.Browser.Tests/Fixtures/campfire_user_agents.json',
        'tests/AgentLang.Campfire.Http.Tests/Program.fs',
        'applications/campfire/Host/CampfireHost.fs',
        'applications/campfire/Persistence/SqliteProvider.fs',
        'tests/AgentLang.Campfire.Integration.Tests/Program.fs',
        'tests/AgentLang.Campfire.Integration.Tests/real-provider-tests.flow',
        'applications/campfire/Cookies/CookieService.fs',
        'tests/AgentLang.Campfire.Cookies.Tests/Program.fs',
        'tests/AgentLang.Campfire.Cookies.Tests/Fixtures/rails-cookie-vectors.json',
        'applications/campfire/Authentication.Persistence/SqliteProvider.fs',
        'tests/AgentLang.Campfire.Authentication.Persistence.Tests/Program.fs',
        'applications/campfire/authentication-types.flow',
        'applications/campfire/authentication.flow',
        'applications/campfire/sign-in.flow',
        'applications/campfire/sign-in-tests.flow',
        'applications/campfire/sign-out.flow',
        'applications/campfire/sign-out-tests.flow',
        'applications/campfire/http-logout-tests.flow',
        'applications/campfire/http-room-read-tests.flow',
        'applications/campfire/Authentication.Persistence/PasswordVerification.fs',
        'applications/campfire/Authentication.Persistence/AgentLang.Campfire.Authentication.Persistence.fsproj',
        'tests/AgentLang.Campfire.Authentication.Persistence.Tests/Fixtures/rails-password-vectors.json',
        'tests/AgentLang.Campfire.Authentication.Persistence.Tests/AgentLang.Campfire.Authentication.Persistence.Tests.fsproj',
        'tests/AgentLang.Campfire.SignIn.Tests/Program.fs',
        'tests/AgentLang.Campfire.SignIn.Tests/AgentLang.Campfire.SignIn.Tests.fsproj',
        'applications/campfire/authentication-tests.flow',
        'applications/campfire/Authentication.Host/CampfireAuthenticationHost.fs',
        'tests/AgentLang.Campfire.Authentication.Integration.Tests/Program.fs',
        'tests/AgentLang.Campfire.Authentication.Integration.Tests/real-authentication-tests.flow'
    )) {
        $pins.Add([ordered]@{
            path = $relativePath
            sha256 = (Get-FileHash -LiteralPath (Join-Path $repo $relativePath) -Algorithm SHA256).Hash.ToLowerInvariant()
        })
    }
} catch {
    $failed = $true
    Write-Host $_.Exception.Message
} finally {
    $receipt = [ordered]@{
        formatVersion = 1
        scope = 'Campfire interpreter storage/authentication, browser compatibility, public-login/authenticated loopback HTTP and pure application policies'
        passed = -not $failed
        revision = $revision
        configuration = $Configuration
        noRestore = [bool]$NoRestore
        packageAuditEnabled = $true
        startedAt = $startedAt.ToString('O')
        finishedAt = [DateTimeOffset]::UtcNow.ToString('O')
        checks = @($checks.ToArray())
        pins = @($pins.ToArray())
    }
    [IO.File]::WriteAllText($EvidencePath, (($receipt | ConvertTo-Json -Depth 12) + "`n"), $utf8)
    $env:NuGetAudit = $previousAudit
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
    $env:MSBuildEnableWorkloadResolver = $previousWorkloadResolver
    $env:MSBUILDDISABLENODEREUSE = $previousNodeReuse
    Pop-Location
}
Write-Host "Receipt: $EvidencePath"
if ($failed) { exit 1 }
