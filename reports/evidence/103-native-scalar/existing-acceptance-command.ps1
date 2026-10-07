$ErrorActionPreference = 'Stop'
$root = 'D:\code\AgentLang'
Set-Location $root
$out = Join-Path $root '.agentlang/native-integration-validation'
New-Item -ItemType Directory -Force $out | Out-Null
$checks = [Collections.Generic.List[object]]::new()
$build = @(& dotnet build AgentLang.sln -c Release --artifacts-path "$out/artifacts" 2>&1 | ForEach-Object ToString)
$code = $LASTEXITCODE
$checks.Add(@{name='solution-build'; exitCode=$code; output=$build})
if ($code -eq 0) {
  $env:AGENTLANG_TEST_CLI = "$out/artifacts/bin/AgentLang.Cli/release/AgentLang.Cli.dll"
  $projects = @('AgentLang.Acceptance') + @(Select-String -Path scripts/Validate.ps1 -Pattern "= '(AgentLang\.[^']+\.Tests)'" | ForEach-Object { $_.Matches[0].Groups[1].Value })
  foreach ($project in $projects) {
    $dll = "$out/artifacts/bin/$project/release/$project.dll"
    $result = @(& dotnet $dll 2>&1 | ForEach-Object ToString)
    $exit = $LASTEXITCODE
    $checks.Add(@{name=$project; exitCode=$exit; output=$result})
    Write-Output "$project exit=$exit"
  }
}
$checks | ConvertTo-Json -Depth 10 | Set-Content "$out/checks.json"
if (@($checks | Where-Object { $_.exitCode -ne 0 }).Count) { exit 1 }
