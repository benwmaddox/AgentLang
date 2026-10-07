$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'focused-runner/StateReentryRunner.csproj'
dotnet build $project
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$runner = Join-Path $PSScriptRoot 'focused-runner/bin/Debug/net9.0/StateReentryRunner.dll'
dotnet $runner
exit $LASTEXITCODE
