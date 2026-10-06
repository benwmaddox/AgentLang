#requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$expectedSourceCommit = '8d52195cd02a1b4f65a33f320bb00e41335de4f0'
$sourceCommit = (& git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -ne $expectedSourceCommit) {
    throw "Unexpected source pin: $sourceCommit"
}

$localRoot = Join-Path $repo '.agentlang/strong-type-flow-001'
$runtimeRoot = Join-Path $localRoot 'language-bin'
$cli = Join-Path $runtimeRoot 'AgentLang.Cli.dll'
$seed = Join-Path $localRoot 'seed-project'
$actorProject = Join-Path $localRoot 'actor-01-units'
$runRoot = Join-Path $repo 'experiments/AgentLang.SubagentTrials/strong-type-flow-001/runs/units-001'
$startingProject = Join-Path $runRoot 'starting-project'
$pathsThatMustBeFresh = @($seed, $actorProject, $runRoot, $startingProject)
if ($pathsThatMustBeFresh | Where-Object { Test-Path -LiteralPath $_ }) {
    throw 'Refusing to overwrite existing strong-type trial files.'
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
type Email : String {
    validate email::valid?;
}

type MetersPerSecond : Float { }

type KilometersPerHour : Float { }

record Delivery {
    field contact: Email;
    field speed: MetersPerSecond;
}

word email.valid?(address: String) -> Bool {
    effects none
    let contains-at = string::contains(address, "@");
    let contains-dot = string::contains(address, ".");
    let contains-space = string::contains(address, " ");
    let starts-with-at = string::starts-with(address, "@");
    let ends-with-at = string::ends-with(address, "@");
    let has-required-parts = bool::and(contains-at, contains-dot);
    let has-no-space = bool::not(contains-space);
    let has-no-leading-at = bool::not(starts-with-at);
    let has-no-trailing-at = bool::not(ends-with-at);
    bool::and(
        bool::and(
            bool::and(has-required-parts, has-no-space),
            has-no-leading-at),
        has-no-trailing-at)
}

test email.valid?/valid {
    email::valid?("dev@example.com")
    => true
}

test email.valid?/missing-at {
    email::valid?("dev.example.com")
    => false
}

test email.valid?/missing-dot {
    email::valid?("dev@examplecom")
    => false
}

test email.valid?/spaces {
    email::valid?("dev @example.com")
    => false
}

test email.valid?/leading-at {
    email::valid?("@dev.example")
    => false
}

test email.valid?/trailing-at {
    email::valid?("dev.example@")
    => false
}

example email.valid?/accepted-shape {
    email::valid?("dev@example.com")
    => true
}
'@

$requests = @(
    [ordered]@{ op = 'task.begin'; goal = 'Prepare immutable strong-type agent study seed' },
    [ordered]@{ op = 'define'; frontend = 'flow'; source = $document },
    [ordered]@{ op = 'test'; word = 'email.valid?' },
    [ordered]@{ op = 'commit'; word = 'email.valid?'; library = $true },
    [ordered]@{ op = 'commit'; word = 'Email' },
    [ordered]@{ op = 'commit'; word = 'MetersPerSecond' },
    [ordered]@{ op = 'commit'; word = 'KilometersPerHour' },
    [ordered]@{ op = 'commit'; word = 'Delivery' },
    [ordered]@{ op = 'test-all' },
    [ordered]@{ op = 'describe'; word = 'email.valid?' },
    [ordered]@{ op = 'source'; type = 'Email' },
    [ordered]@{ op = 'source'; type = 'MetersPerSecond' },
    [ordered]@{ op = 'source'; type = 'KilometersPerHour' },
    [ordered]@{ op = 'source'; type = 'Delivery' },
    [ordered]@{ op = 'example'; word = 'email.valid?'; caseName = 'accepted-shape' },
    [ordered]@{ op = 'task.commit'; actor = 'host' }
)

New-Item -ItemType Directory -Path $localRoot, $runRoot, $seed, $startingProject, $actorProject -Force | Out-Null
SaveJson (Join-Path $runRoot 'requests.json') $requests
$runtimeInventory = Inventory $runtimeRoot
$cliSha256 = (Get-FileHash -LiteralPath $cli -Algorithm SHA256).Hash.ToLowerInvariant()
$requestLines = @($requests | ForEach-Object { ConvertTo-Json -InputObject $_ -Depth 100 -Compress })
$output = @($requestLines | & dotnet $cli --project $seed --clock '2000-01-01T00:00:00Z' --jsonl)
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

$validatorTest = $responses[2].data
$validatorCases = @($validatorTest.results)
if ($validatorCases.Count -ne 6 -or @($validatorCases | Where-Object { -not $_.passed }).Count -ne 0) {
    throw 'The Email validator own tests did not all pass.'
}
$validatorCoverage = $validatorTest.coverage
if ($null -eq $validatorCoverage -or
    $validatorCoverage.instructionsCovered -ne $validatorCoverage.instructionsTotal -or
    $validatorCoverage.branchesCovered -ne $validatorCoverage.branchesTotal) {
    throw 'The Email validator did not reach complete own instruction and branch coverage.'
}
$committedDescription = $responses[9].data
if ($committedDescription.maturity -ne 'library' -or
    $committedDescription.coverage.instructionsCovered -ne $committedDescription.coverage.instructionsTotal -or
    $committedDescription.coverage.branchesCovered -ne $committedDescription.coverage.branchesTotal) {
    throw 'The persisted Email validator is not a fully covered library word.'
}
$allTests = @($responses[8].data.results)
if ($allTests.Count -ne 6 -or @($allTests | Where-Object { -not $_.passed }).Count -ne 0) {
    throw 'The committed seed tests did not all pass.'
}
if (-not $responses[14].data.results[0].passed) {
    throw 'The Email validator example did not pass.'
}
if ($responses[15].data.active) {
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
    requests = $requests
    responses = $responses
    validatorOwnCoverage = $validatorCoverage
})
Write-Output 'Prepared and verified the strong-type seed; no actor was launched.'
