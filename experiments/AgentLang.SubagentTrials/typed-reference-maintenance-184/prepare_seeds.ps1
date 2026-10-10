#requires -Version 7.5
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$FlowCliDll,
    [string]$RepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..')),
    [string]$DraftId = 'draft-001'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$studyRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$RepoRoot = [IO.Path]::GetFullPath($RepoRoot)
$FlowCliDll = [IO.Path]::GetFullPath($FlowCliDll)
$ownedRoot = [IO.Path]::GetFullPath((Join-Path $RepoRoot '.agentlang\efficacy-maintenance-184\seed-draft'))
$projectsRoot = Join-Path $ownedRoot "projects\$DraftId"
$tempRoot = Join-Path $ownedRoot "tmp\$DraftId"

if (-not (Test-Path -LiteralPath $FlowCliDll -PathType Leaf)) { throw "Fresh Flow CLI DLL is missing: $FlowCliDll" }
if (Test-Path -LiteralPath $projectsRoot) { throw "Refusing to overwrite an existing seed draft: $projectsRoot" }
[void][IO.Directory]::CreateDirectory($projectsRoot)
[void][IO.Directory]::CreateDirectory($tempRoot)
$env:TEMP = $tempRoot
$env:TMP = $tempRoot

function Invoke-AgentJsonl([string]$Project, [object[]]$Requests, [string]$CliPath) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.WorkingDirectory = $Project
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @($CliPath, '--project', $Project, '--jsonl')) {
        [void]$start.ArgumentList.Add([string]$argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (-not $process.Start()) { throw "Flow CLI did not start for seed project: $Project" }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    foreach ($request in $Requests) {
        $process.StandardInput.WriteLine((ConvertTo-Json -InputObject $request -Depth 80 -Compress))
    }
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(180000)) {
        try { $process.Kill($true) } catch { }
        throw "Flow seed setup timed out for $Project."
    }

    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    $exitCode = $process.ExitCode
    $process.Dispose()
    $responses = @(
        $stdout -split '\r?\n' |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            ForEach-Object { ConvertFrom-Json -InputObject $_ -AsHashtable -Depth 80 }
    )
    if ($exitCode -ne 0 -or $responses.Count -ne $Requests.Count) {
        throw "Flow seed process failed (exit=$exitCode, responses=$($responses.Count)/$($Requests.Count)): $stderr`n$stdout"
    }
    $failed = @($responses | Where-Object { $_.ok -ne $true })
    if ($failed.Count -gt 0) {
        $details = $failed | ConvertTo-Json -Depth 80
        throw "Flow seed request failed: $details`n$stderr"
    }
    return ,$responses
}

function Initialize-FlowSeed([string]$Name, [string]$Project, [string]$Source, [string]$CliPath, [bool]$HasFindScan) {
    [void][IO.Directory]::CreateDirectory($Project)
    $requests = [Collections.Generic.List[object]]::new()
    $requests.Add([ordered]@{ op='task.begin'; goal="Initialize the $Name typed-reference seed project" })
    $requests.Add([ordered]@{ op='define'; frontend='flow'; syntaxVersion=2; source=$Source })
    $requests.Add([ordered]@{ op='test-all' })

    $requests.Add([ordered]@{ op='commit'; word='shipment.scan-lookup-step'; library=$true })
    $commonWords = @(
        'store.shipment-lookup-step',
        'store.shipment',
        'store.shipment-replacement-step',
        'store.with-shipment',
        'shipment.ingest-rule',
        'shipment.ingest',
        'shipment.batch-step',
        'shipment.ingest-batch'
    )
    foreach ($word in $commonWords) {
        $requests.Add([ordered]@{ op='commit'; word=$word; library=$false })
    }
    if ($HasFindScan) {
        $requests.Add([ordered]@{ op='commit'; word='shipment.find-scan'; library=$true })
    }
    $requests.Add([ordered]@{ op='test-all' })
    $requests.Add([ordered]@{ op='task.commit' })

    $setupResponses = Invoke-AgentJsonl $Project $requests.ToArray() $CliPath
    $verificationRequests = [Collections.Generic.List[object]]::new()
    $verificationRequests.Add([ordered]@{ op='test-all' })
    $verificationRequests.Add([ordered]@{ op='example'; word='shipment.ingest' })
    $verificationRequests.Add([ordered]@{ op='example'; word='shipment.ingest-batch' })
    if ($HasFindScan) { $verificationRequests.Add([ordered]@{ op='example'; word='shipment.find-scan' }) }
    $verificationRequests.Add([ordered]@{ op='history'; word='shipment.ingest' })
    $verificationResponses = Invoke-AgentJsonl $Project $verificationRequests.ToArray() $CliPath

    [ordered]@{
        name = $Name
        project = [IO.Path]::GetRelativePath($RepoRoot, $Project)
        initialTestResponse = $setupResponses[2]
        finalTestResponse = $setupResponses[$setupResponses.Count - 2]
        verificationResponses = $verificationResponses
        helper = if ($HasFindScan) { 'shipment.find-scan' } else { 'shipment.scan-lookup-step (lower-level fold callback only)' }
        flowCli = [IO.Path]::GetRelativePath($RepoRoot, $CliPath)
        setupHistory = 'actual seed initialization recorded by task.begin/task.commit; no participant history is represented'
    }
}

$commonPath = Join-Path $studyRoot 'seeds\agentlang\common.flow'
$retainedPath = Join-Path $studyRoot 'seeds\agentlang\retained-reference.flow'
$commonSource = [IO.File]::ReadAllText($commonPath)
$retainedSource = $commonSource + [Environment]::NewLine + [IO.File]::ReadAllText($retainedPath)

$flowRetained = Initialize-FlowSeed 'retained AgentLang' (Join-Path $projectsRoot 'flow-retained\project') $retainedSource $FlowCliDll $true
$flowResetRich = Initialize-FlowSeed 'reset-rich AgentLang' (Join-Path $projectsRoot 'flow-reset-rich\project') $commonSource $FlowCliDll $false

$manifest = [ordered]@{
    schemaVersion = 1
    study = 'typed-reference-maintenance-184'
    draftId = $DraftId
    status = 'draft; do not freeze or dispatch before milestone 183 acceptance and oracle calibration'
    seedInitialization = 'setup-created; this is not prior-participant work'
    arms = @($flowRetained, $flowResetRich)
    sourceHashes = [ordered]@{
        agentlangCommon = (Get-FileHash -LiteralPath $commonPath -Algorithm SHA256).Hash.ToLowerInvariant()
        retainedHelper = (Get-FileHash -LiteralPath $retainedPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$manifestPath = Join-Path $projectsRoot 'seed-draft-manifest.json'
[IO.File]::WriteAllText($manifestPath, (ConvertTo-Json -InputObject $manifest -Depth 80), [Text.UTF8Encoding]::new($false))
Write-Output $manifestPath
