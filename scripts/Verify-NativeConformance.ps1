$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$runId = [Guid]::NewGuid().ToString('N')
$artifactsRoot = Join-Path $repoRoot ".agentlang/native-validation/run-$runId"
$testProject = Join-Path $repoRoot 'tests/AgentLang.Llvm.Tests/AgentLang.Llvm.Tests.fsproj'
$oldLocation = Get-Location

try {
    Set-Location $repoRoot
    dotnet build $testProject --artifacts-path $artifactsRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Native conformance test build failed with exit code $LASTEXITCODE."
    }

    $testAssembly = Get-ChildItem -LiteralPath (Join-Path $artifactsRoot 'bin') -Filter 'AgentLang.Llvm.Tests.dll' -File -Recurse |
        Select-Object -First 1 -ExpandProperty FullName
    if (-not $testAssembly) {
        throw "Native conformance test assembly was not found under $artifactsRoot."
    }

    dotnet $testAssembly
    if ($LASTEXITCODE -ne 0) {
        throw "Native conformance tests failed with exit code $LASTEXITCODE."
    }
}
finally {
    Set-Location $oldLocation
}
