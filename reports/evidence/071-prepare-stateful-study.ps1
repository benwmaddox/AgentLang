#requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$expectedSourceCommit = 'cb1ebf6e3b5c4da48b1a2eedfae369bcc6261fcb'
$sourceCommit = (& git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -ne $expectedSourceCommit) {
    throw "Unexpected source pin: $sourceCommit"
}

$localRoot = Join-Path $repo '.agentlang/stateful-flow-001'
$runtimeRoot = Join-Path $localRoot 'language-bin'
$cli = Join-Path $runtimeRoot 'AgentLang.Cli.dll'
$seed = Join-Path $localRoot 'seed-project'
$actorProject = Join-Path $localRoot 'actor-01-reminder'
$runRoot = Join-Path $repo 'experiments/AgentLang.SubagentTrials/stateful-flow-001/runs/stateful-001'
$startingProject = Join-Path $runRoot 'starting-project'
$pathsThatMustBeFresh = @($seed, $actorProject, $runRoot, $startingProject)
if ($pathsThatMustBeFresh | Where-Object { Test-Path -LiteralPath $_ }) {
    throw 'Refusing to overwrite existing stateful trial files.'
}
if (-not (Test-Path -LiteralPath $cli -PathType Leaf)) {
    throw "The prebuilt runtime is missing: $cli"
}

$utf8 = [Text.UTF8Encoding]::new($false)
function SaveJson($Path, $Value) {
    $json = ConvertTo-Json -InputObject $Value -Depth 100
    [IO.File]::WriteAllText($Path, $json, $utf8)
}
function Inventory($Path) {
    @(Get-ChildItem -LiteralPath $Path -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($Path, $_.FullName)
            bytes = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
}

$document = @'
type InvoiceId : String { }

type InvoiceStatus : String { }

record Invoice {
    field id: InvoiceId;
    field status: InvoiceStatus;
}

word invoice.reminder-path(id: InvoiceId) -> String {
    effects none
    doc "Builds the fixed virtual reminder path for an invoice ID."
    string::concat("outbox/invoice-reminders/", InvoiceId::value(id))
}

test invoice.reminder-path/basic {
    invoice::reminder-path(InvoiceId::new("INV-001"))
    => "outbox/invoice-reminders/INV-001"
}

test invoice.reminder-path/preserves-id-text {
    invoice::reminder-path(InvoiceId::new("inv.alpha_2"))
    => "outbox/invoice-reminders/inv.alpha_2"
}

example invoice.reminder-path/standard-id {
    invoice::reminder-path(InvoiceId::new("INV-001"))
    => "outbox/invoice-reminders/INV-001"
}
'@

$requests = @(
    [ordered]@{ op = 'task.begin'; goal = 'Prepare immutable stateful reminder agent study seed' },
    [ordered]@{ op = 'define'; frontend = 'flow'; source = $document },
    [ordered]@{ op = 'test'; word = 'invoice.reminder-path' },
    [ordered]@{ op = 'commit'; word = 'InvoiceId' },
    [ordered]@{ op = 'commit'; word = 'InvoiceStatus' },
    [ordered]@{ op = 'commit'; word = 'Invoice' },
    [ordered]@{ op = 'commit'; word = 'invoice.reminder-path'; library = $true },
    [ordered]@{ op = 'test-all' },
    [ordered]@{ op = 'describe'; word = 'invoice.reminder-path' },
    [ordered]@{ op = 'source'; type = 'InvoiceId' },
    [ordered]@{ op = 'source'; type = 'InvoiceStatus' },
    [ordered]@{ op = 'source'; type = 'Invoice' },
    [ordered]@{ op = 'example'; word = 'invoice.reminder-path'; caseName = 'standard-id' },
    [ordered]@{ op = 'task.commit'; actor = 'host' }
)

New-Item -ItemType Directory -Path $localRoot, $runRoot, $seed, $startingProject, $actorProject -Force | Out-Null
SaveJson (Join-Path $runRoot 'requests.json') $requests
$runtimeInventory = Inventory $runtimeRoot
$cliSha256 = (Get-FileHash -LiteralPath $cli -Algorithm SHA256).Hash.ToLowerInvariant()
$requestLines = @($requests | ForEach-Object { ConvertTo-Json -InputObject $_ -Depth 100 -Compress })
$output = @($requestLines | & dotnet $cli --project $seed --allow 'fs.read,fs.write' --clock '2000-01-01T00:00:00Z' --jsonl)
$processExitCode = $LASTEXITCODE
$rawOutput = @($output | ForEach-Object { [string]$_ })
SaveJson (Join-Path $runRoot 'raw-responses.json') ([ordered]@{ exitCode = $processExitCode; lines = $rawOutput })
if ($processExitCode -ne 0 -or $rawOutput.Count -ne $requests.Count) {
    throw 'Fixture protocol process failed; raw request and response evidence was retained.'
}
$responses = @($rawOutput | ForEach-Object { ConvertFrom-Json -InputObject $_ -Depth 100 })
SaveJson (Join-Path $runRoot 'responses.json') $responses
if (@($responses | Where-Object { -not $_.ok }).Count -gt 0) {
    throw 'A fixture protocol operation failed; request and response evidence was retained.'
}

$helperTest = $responses[2].data
$helperCases = @($helperTest.results)
if ($helperCases.Count -ne 2 -or @($helperCases | Where-Object { -not $_.passed }).Count -ne 0) {
    throw 'The reminder-path helper own tests did not all pass.'
}
$helperCoverage = $helperTest.coverage
if ($null -eq $helperCoverage -or
    $helperCoverage.instructionsCovered -ne $helperCoverage.instructionsTotal -or
    $helperCoverage.branchesCovered -ne $helperCoverage.branchesTotal) {
    throw 'The reminder-path helper did not reach complete own instruction and branch coverage.'
}
$committedDescription = $responses[8].data
if ($committedDescription.maturity -ne 'library' -or
    $committedDescription.coverage.instructionsCovered -ne $committedDescription.coverage.instructionsTotal -or
    $committedDescription.coverage.branchesCovered -ne $committedDescription.coverage.branchesTotal) {
    throw 'The persisted reminder-path helper is not a fully covered library word.'
}
$allTests = @($responses[7].data.results)
if ($allTests.Count -ne 2 -or @($allTests | Where-Object { -not $_.passed }).Count -ne 0) {
    throw 'The committed seed tests did not all pass.'
}
if (-not $responses[12].data.results[0].passed) {
    throw 'The reminder-path example did not pass.'
}
if ($responses[13].data.active) {
    throw 'The fixture setup task remained active after task.commit.'
}

Get-ChildItem -LiteralPath $seed -Force | Copy-Item -Destination $startingProject -Recurse -Force
Get-ChildItem -LiteralPath $startingProject -Force | Copy-Item -Destination $actorProject -Recurse -Force
$startingInventory = Inventory $startingProject
$actorInventory = Inventory $actorProject
if ((ConvertTo-Json -InputObject $startingInventory -Depth 20 -Compress) -cne
    (ConvertTo-Json -InputObject $actorInventory -Depth 20 -Compress)) {
    throw 'The fresh actor project differs from the archived starting project.'
}
SaveJson (Join-Path $runRoot 'starting-state.json') $startingInventory
SaveJson (Join-Path $runRoot 'fixture-setup.json') ([ordered]@{
    schemaVersion = 1
    sourceCommit = $sourceCommit
    cliPath = [IO.Path]::GetRelativePath($repo, $cli)
    cliSha256 = $cliSha256
    runtimeFiles = $runtimeInventory
    seedProjectFiles = (Inventory $seed)
    startingProjectFiles = $startingInventory
    actorProjectPath = [IO.Path]::GetRelativePath($repo, $actorProject)
    capabilities = @('fs.read', 'fs.write')
    requests = $requests
    responses = $responses
    reminderPathOwnCoverage = $helperCoverage
})
Write-Output 'Prepared and verified the stateful reminder seed; no actor was launched.'
