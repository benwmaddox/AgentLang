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
            'source-acceptance' = 'AgentLang.Source.Tests'
            'storage-acceptance' = 'AgentLang.Storage.Tests'
            'conventional-acceptance' = 'AgentLang.Conventional.Tests'
            'discovery-acceptance' = 'AgentLang.Discovery.Tests'
            'ir-acceptance' = 'AgentLang.IR.Tests'
        }
        foreach ($checkName in $acceptanceProjects.Keys) {
            $projectName = $acceptanceProjects[$checkName]
            $projectDirectory = "tests/$projectName"
            if (Test-Path -LiteralPath "$projectDirectory/$projectName.fsproj") {
                $null = Invoke-ValidationCheck $checkName 'dotnet' @('run', '--no-build', '--configuration', $Configuration, '--project', $projectDirectory)
            }
        }
        if (Test-Path -LiteralPath 'scripts/Verify-PersistenceProjection.ps1') {
            $projectionReportPath = [System.IO.Path]::ChangeExtension($ReportPath, 'projection.json')
            $cliBinaryPath = "src/AgentLang.Cli/bin/$Configuration/net9.0/AgentLang.Cli.dll"
            $null = Invoke-ValidationCheck 'fresh-process-projection' 'pwsh' @('-NoProfile', '-File', 'scripts/Verify-PersistenceProjection.ps1', '-CliDll', $cliBinaryPath, '-EvidencePath', $projectionReportPath)
        }
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
