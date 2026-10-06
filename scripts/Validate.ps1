param(
    [string]$ReportPath = '.agentlang/reports/validation.json',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repositoryPath = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $repositoryPath
try {
    $startedAt = [DateTimeOffset]::UtcNow
    $revision = (& git rev-parse HEAD | Out-String).Trim()
    $branch = (& git branch --show-current | Out-String).Trim()
    $dirty = -not [string]::IsNullOrWhiteSpace((& git status --porcelain | Out-String))
    $checks = [System.Collections.Generic.List[object]]::new()

    function Invoke-ValidationCheck {
        param([string]$Name, [string]$Executable, [string[]]$Arguments)
        $timer = [System.Diagnostics.Stopwatch]::StartNew()
        # Native programs report failure through their exit code. Preserve their
        # diagnostics in the report instead of treating stderr as a PS exception.
        $previousPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $null = Get-Command $Executable -ErrorAction Stop
            $output = @(& $Executable @Arguments 2>&1 | ForEach-Object { $_.ToString() })
            $code = $LASTEXITCODE
        } catch {
            $output = @($_.ToString())
            $code = 1
        }
        $ErrorActionPreference = $previousPreference
        $timer.Stop()
        $checks.Add([ordered]@{
            name = $Name
            command = (@($Executable) + $Arguments) -join ' '
            exitCode = $code
            durationMilliseconds = $timer.ElapsedMilliseconds
            output = $output
        })
        Write-Host "$Name (exit $code)"
        $output | ForEach-Object { Write-Host $_ }
        return $code
    }

    $buildCode = Invoke-ValidationCheck 'build' 'dotnet' @('build', 'AgentLang.sln', '--configuration', $Configuration)
    if ($buildCode -eq 0) {
        $null = Invoke-ValidationCheck 'language-acceptance' 'dotnet' @('run', '--no-build', '--configuration', $Configuration, '--project', 'tests/AgentLang.Acceptance')
        $acceptanceProjects = [ordered]@{
            'harness-acceptance' = 'AgentLang.Harness.Tests'
            'business-acceptance' = 'AgentLang.Business.Tests'
            'business-contracts-acceptance' = 'AgentLang.Business.Contracts.Tests'
            'value-inspection-acceptance' = 'AgentLang.ValueInspection.Tests'
            'source-acceptance' = 'AgentLang.Source.Tests'
            'flow-acceptance' = 'AgentLang.Flow.Tests'
            'flow-lint-acceptance' = 'AgentLang.Flow.Lint.Tests'
            'flow-runtime-acceptance' = 'AgentLang.Flow.Runtime.Tests'
            'cli-acceptance' = 'AgentLang.Cli.Tests'
            'storage-acceptance' = 'AgentLang.Storage.Tests'
            'conventional-acceptance' = 'AgentLang.Conventional.Tests'
            'conventional-cli-acceptance' = 'AgentLang.Conventional.Cli.Tests'
            'discovery-acceptance' = 'AgentLang.Discovery.Tests'
            'ir-acceptance' = 'AgentLang.IR.Tests'
            'ir-formatting-acceptance' = 'AgentLang.IR.Formatting.Tests'
            'ir-interpreter-acceptance' = 'AgentLang.IR.Interpreter.Tests'
            'vocabulary-acceptance' = 'AgentLang.Vocabulary.Tests'
            'task-bank-acceptance' = 'AgentLang.TaskBank.Tests'
        }
        foreach ($checkName in $acceptanceProjects.Keys) {
            $projectName = $acceptanceProjects[$checkName]
            $projectDirectory = "tests/$projectName"
            # Every listed acceptance project is required. Let dotnet report a
            # missing project as a failed check instead of silently omitting it.
            $null = Invoke-ValidationCheck $checkName 'dotnet' @('run', '--no-build', '--configuration', $Configuration, '--project', $projectDirectory)
        }
        # The structural binding probe uses host-built duplicate-span nodes;
        # load the assembly from this gate's build rather than an older pin.
        $coreBinaryPath = "src/AgentLang.Core/bin/$Configuration/net9.0/AgentLang.Core.dll"
        $null = Invoke-ValidationCheck 'flow-call-binding-probe' 'dotnet' @('fsi', '--exec', "--reference:$coreBinaryPath", 'scripts/Verify-FlowCallBindings.fsx')
        $projectionReportPath = [System.IO.Path]::ChangeExtension($ReportPath, 'projection.json')
        $cliBinaryPath = "src/AgentLang.Cli/bin/$Configuration/net9.0/AgentLang.Cli.dll"
        $null = Invoke-ValidationCheck 'fresh-process-projection' 'pwsh' @('-NoProfile', '-File', 'scripts/Verify-PersistenceProjection.ps1', '-CliDll', $cliBinaryPath, '-EvidencePath', $projectionReportPath)
        $fixtureReportPath = [System.IO.Path]::ChangeExtension($ReportPath, 'matched-fixtures.json')
        $cliBinaryPath = "src/AgentLang.Cli/bin/$Configuration/net9.0/AgentLang.Cli.dll"
        $conventionalBinaryPath = "experiments/AgentLang.SubagentTrials/matched-renewal-001/fixtures/conventional/bin/$Configuration/net9.0/MatchedRenewal.dll"
        $null = Invoke-ValidationCheck 'matched-renewal-fixtures' 'pwsh' @('-NoProfile', '-File', 'scripts/Verify-MatchedRenewalFixtures.ps1', '-CliDll', $cliBinaryPath, '-ConventionalDll', $conventionalBinaryPath, '-EvidencePath', $fixtureReportPath)
        $flowFixtureReportPath = [System.IO.Path]::ChangeExtension($ReportPath, 'flow-matched-fixtures.json')
        $flowConventionalBinaryPath = "experiments/AgentLang.SubagentTrials/matched-renewal-001/fixtures/flow/conventional/bin/$Configuration/net9.0/MatchedRenewal.Flow.dll"
        $null = Invoke-ValidationCheck 'flow-matched-renewal-fixtures' 'pwsh' @('-NoProfile', '-File', 'scripts/Verify-FlowMatchedRenewalFixtures.ps1', '-CliDll', $cliBinaryPath, '-ConventionalDll', $flowConventionalBinaryPath, '-EvidencePath', $flowFixtureReportPath)
        $snapshotReportPath = [System.IO.Path]::ChangeExtension($ReportPath, 'matched-snapshots.json')
        $null = Invoke-ValidationCheck 'matched-renewal-snapshots' 'pwsh' @('-NoProfile', '-File', 'scripts/Verify-MatchedRenewalSnapshots.ps1', '-CliDll', $cliBinaryPath, '-EvidencePath', $snapshotReportPath)
        $renewalAcceptanceReportPath = [System.IO.Path]::ChangeExtension($ReportPath, 'renewal-acceptance-gate.json')
        $null = Invoke-ValidationCheck 'flow-renewal-acceptance-gate' 'pwsh' @('-NoProfile', '-File', 'scripts/Verify-FlowRenewalAcceptanceGate.ps1', '-CliDll', $cliBinaryPath, '-EvidencePath', $renewalAcceptanceReportPath)
        $renewalReplayReportPath = [System.IO.Path]::ChangeExtension($ReportPath, 'renewal-replay.json')
        $null = Invoke-ValidationCheck 'flow-renewal-replay' 'pwsh' @('-NoProfile', '-File', 'scripts/Verify-FlowRenewalReplay.ps1', '-CliDll', $cliBinaryPath, '-EvidencePath', $renewalReplayReportPath)
        $earlyNegativeReportPath = [System.IO.Path]::ChangeExtension($ReportPath, 'early-agent-negative.json')
        $null = Invoke-ValidationCheck 'early-agent-negative-controls' 'pwsh' @('-NoProfile', '-File', 'scripts/Verify-EarlyAgentNegativeControls.ps1', '-CliDll', $cliBinaryPath, '-EvidencePath', $earlyNegativeReportPath)
        $hostReportPath = [System.IO.Path]::ChangeExtension($ReportPath, 'subagent-host.json')
        $cliBinaryPath = "src/AgentLang.Cli/bin/$Configuration/net9.0/AgentLang.Cli.dll"
        $null = Invoke-ValidationCheck 'subagent-trial-host' 'pwsh' @('-NoProfile', '-File', 'scripts/Verify-SubagentTrialHost.ps1', '-CliDll', $cliBinaryPath, '-EvidencePath', $hostReportPath)
        $parserReportPath = [System.IO.Path]::ChangeExtension($ReportPath, 'parser-limits.json')
        $cliBinaryPath = "src/AgentLang.Cli/bin/$Configuration/net9.0/AgentLang.Cli.dll"
        $null = Invoke-ValidationCheck 'parser-process-limits' 'pwsh' @('-NoProfile', '-File', 'scripts/Verify-ParserLimits.ps1', '-CliDll', $cliBinaryPath, '-EvidencePath', $parserReportPath)
    }
    $null = Invoke-ValidationCheck 'diff-whitespace' 'git' @('diff', '--check')
    $null = Invoke-ValidationCheck 'staged-diff-whitespace' 'git' @('diff', '--cached', '--check')
    $passed = @($checks | Where-Object { $_.exitCode -ne 0 }).Count -eq 0
    $report = [ordered]@{
        schemaVersion = 1
        startedAt = $startedAt.ToString('o')
        completedAt = [DateTimeOffset]::UtcNow.ToString('o')
        revision = $revision
        branch = $branch
        dirty = $dirty
        configuration = $Configuration
        passed = $passed
        checks = $checks.ToArray()
    }
    $resolvedReportPath = [System.IO.Path]::GetFullPath($ReportPath, $repositoryPath)
    $null = [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($resolvedReportPath))
    [System.IO.File]::WriteAllText($resolvedReportPath, ($report | ConvertTo-Json -Depth 20))
    Write-Host "Saved validation evidence: $resolvedReportPath"
    if (-not $passed) { exit 1 }
} finally {
    Pop-Location
}
