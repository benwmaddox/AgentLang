#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$CliDll = 'src/AgentLang.Cli/bin/Release/net9.0/AgentLang.Cli.dll',
    [string]$EvidencePath = '.agentlang/reports/parser-limits.json'
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$cliPath = if ([IO.Path]::IsPathRooted($CliDll)) { [IO.Path]::GetFullPath($CliDll) } else { [IO.Path]::GetFullPath((Join-Path $repoRoot $CliDll)) }
$reportPath = if ([IO.Path]::IsPathRooted($EvidencePath)) { [IO.Path]::GetFullPath($EvidencePath) } else { [IO.Path]::GetFullPath((Join-Path $repoRoot $EvidencePath)) }
if (-not (Test-Path -LiteralPath $cliPath -PathType Leaf)) { throw "CLI artifact is missing: $cliPath" }
[IO.Directory]::CreateDirectory((Split-Path -Parent $reportPath)) | Out-Null
if (Test-Path -LiteralPath $reportPath) {
    $reportStem = [IO.Path]::GetFileNameWithoutExtension($reportPath)
    $reportExtension = [IO.Path]::GetExtension($reportPath)
    $retryNumber = 1
    do {
        $retryPath = Join-Path (Split-Path -Parent $reportPath) ($reportStem + '.retry-' + $retryNumber.ToString('D2') + $reportExtension)
        $retryNumber++
    } while (Test-Path -LiteralPath $retryPath)
    $reportPath = $retryPath
}
$probeId = [Guid]::NewGuid().ToString('N')
$projectPath = Join-Path $repoRoot ('.agentlang/parser-probe-' + $probeId)
$tracePath = Join-Path $repoRoot ('.agentlang/reports/015-parser-probe-' + $probeId + '.jsonl')
$nestedType = ('Option<' * 3000) + 'Int' + ('>' * 3000)
$typeSource = "word parser.type-limit : $nestedType -> Unit`neffects none`nend"
$blockSource = "word parser.block-limit : Bool -> Bool`neffects none`n" + ("if`n" * 1000) + "true`n" + ("end`n" * 1001)
$requests = @(
    (@{ op = 'define'; frontend = 'stack'; source = $typeSource } | ConvertTo-Json -Compress),
    (@{ op = 'define'; frontend = 'stack'; source = $blockSource } | ConvertTo-Json -Compress),
    (@{ op = 'eval'; frontend = 'stack'; code = '10 20 add' } | ConvertTo-Json -Compress)
)
$responseLines = @($requests | & pwsh -NoProfile -File (Join-Path $repoRoot 'scripts/Start-SubagentTrialHost.ps1') -CliDll $cliPath -ProjectPath $projectPath -TracePath $tracePath -AllowedOperations 'define,eval' -MaxRequestBytes 65536 -MaxResponseBytes 16384 -ExchangeTimeoutMilliseconds 15000 -MaxExchanges 3)
$hostExit = $LASTEXITCODE
$responses = @($responseLines | ForEach-Object { $_ | ConvertFrom-Json })
$checks = @(
    @{ name = 'bounded host exited cleanly'; passed = ($hostExit -eq 0) },
    @{ name = 'exactly three responses'; passed = ($responses.Count -eq 3) },
    @{ name = 'deep type rejected structurally'; passed = ($responses.Count -ge 1 -and -not $responses[0].ok -and $responses[0].error.code -eq 'PARSE_TYPE_NESTING_LIMIT') },
    @{ name = 'deep block rejected structurally'; passed = ($responses.Count -ge 2 -and -not $responses[1].ok -and $responses[1].error.code -eq 'PARSE_BLOCK_NESTING_LIMIT') },
    @{ name = 'process remains usable after errors'; passed = ($responses.Count -eq 3 -and $responses[2].ok -and $responses[2].data.stack[0] -eq '30') }
)
$report = [ordered]@{
    schemaVersion = 1
    runtimeFiles = @(Get-ChildItem -LiteralPath (Split-Path -Parent $cliPath) -File | Where-Object { $_.Extension -eq '.dll' -or $_.Name -in @('AgentLang.Cli.deps.json', 'AgentLang.Cli.runtimeconfig.json') } | Sort-Object Name | ForEach-Object { @{ name = $_.Name; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
    verificationFiles = @($PSCommandPath, (Join-Path $repoRoot 'scripts/Start-SubagentTrialHost.ps1')) | ForEach-Object { @{ path = $_; sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash } }
    trace = $tracePath
    hostExit = $hostExit
    requestBytes = @($requests | ForEach-Object { [Text.Encoding]::UTF8.GetByteCount($_) })
    bounds = @{ requestBytes = 65536; responseBytes = 16384; exchangeMilliseconds = 15000; exchanges = 3 }
    responses = $responses
    checks = $checks
    passed = (@($checks | Where-Object { -not $_.passed }).Count -eq 0)
}
$report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $reportPath -Encoding utf8
Write-Output $reportPath
Write-Output ($checks | ConvertTo-Json -Compress)
if (-not $report.passed) { exit 1 }
