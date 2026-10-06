#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CliDll,
    [string]$ConventionalDll = 'experiments/AgentLang.SubagentTrials/matched-renewal-001/fixtures/conventional/bin/Release/net9.0/MatchedRenewal.dll',
    [string]$EvidencePath = '.agentlang/reports/matched-renewal-fixtures.json'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $repo 'experiments/AgentLang.SubagentTrials/matched-renewal-001/fixtures'
$cli = (Resolve-Path -LiteralPath $CliDll).Path
$conventional = (Resolve-Path -LiteralPath $ConventionalDll).Path
$scratch = Join-Path $repo ('.agentlang/matched-fixture-verification/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
$checks = [Collections.Generic.List[object]]::new()
$traces = [Collections.Generic.List[object]]::new()

function Get-RuntimeFiles([string]$Dll) {
    $directory = Split-Path $Dll -Parent
    $stem = [IO.Path]::GetFileNameWithoutExtension($Dll)
    $paths = @(Get-ChildItem -LiteralPath $directory -Filter '*.dll' -File | ForEach-Object FullName)
    foreach ($suffix in @('.deps.json', '.runtimeconfig.json')) {
        $path = Join-Path $directory ($stem + $suffix)
        if (Test-Path -LiteralPath $path -PathType Leaf) { $paths += $path }
    }
    return @($paths | Sort-Object -Unique | ForEach-Object {
        [ordered]@{ name = [IO.Path]::GetFileName($_); sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
}
$cliFiles = Get-RuntimeFiles $cli
$conventionalFiles = Get-RuntimeFiles $conventional
$verifierHash = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()

function Assert-Check([string]$Name, [bool]$Passed) {
    $checks.Add([ordered]@{ name = $Name; passed = $Passed })
    if (-not $Passed) { throw "Fixture check failed: $Name" }
}

function Invoke-Language([string]$Project, [object[]]$Requests) {
    foreach ($request in $Requests) {
        if ($request.op -in @('define', 'eval') -and -not $request.Contains('frontend')) { $request.frontend = 'stack' }
    }
    $lines = @($Requests | ForEach-Object { ConvertTo-Json -InputObject $_ -Depth 12 -Compress })
    $output = @($lines | & dotnet $cli --project $Project --jsonl)
    $exit = $LASTEXITCODE
    Assert-Check 'language process exit' ($exit -eq 0)
    Assert-Check 'one language response per request' ($output.Count -eq $Requests.Count)
    $responses = @($output | ForEach-Object { ConvertFrom-Json -InputObject $_ -Depth 64 })
    $traces.Add([ordered]@{ profile = Split-Path $Project -Leaf; requests = $Requests; responses = $responses; exitCode = $exit })
    foreach ($response in $responses) { Assert-Check 'language request succeeds' ($response.ok -eq $true) }
    return ,$responses
}

function New-Seed([string]$Name) {
    $directory = Join-Path $scratch $Name
    New-Item -ItemType Directory -Path $directory | Out-Null
    Copy-Item -LiteralPath (Join-Path $fixture "$Name.agent") -Destination (Join-Path $directory 'dictionary.agent')
    return $directory
}

$cases = @(
    @{ kind = 'premium'; balance = 100.0 },
    @{ kind = 'standard'; balance = 100.0 },
    @{ kind = 'Premium'; balance = 80.0 },
    @{ kind = ''; balance = 100.0 },
    @{ kind = 'premium '; balance = 100.0 },
    @{ kind = 'premium'; balance = 0.0 },
    @{ kind = 'premium'; balance = -100.0 },
    @{ kind = 'premium'; balance = 250.0 },
    @{ kind = 'premium'; balance = 3.141592653589793 },
    @{ kind = 'premium'; balance = 1e-100 },
    @{ kind = 'standard'; balance = -250.0 },
    @{ kind = 'prémium'; balance = 100.0 }
)
$passed = $false
$failure = $null
try {
    $origin = Join-Path $repo 'experiments/AgentLang.SubagentTrials/task-02/initial.agent'
    Assert-Check 'Growing source is exact retained pilot fixture' ((Get-FileHash -LiteralPath $origin).Hash -eq (Get-FileHash -LiteralPath (Join-Path $fixture 'growing.agent')).Hash)
    $flatSource = Get-Content -LiteralPath (Join-Path $fixture 'flat.agent') -Raw
    Assert-Check 'Flat source has no authored words/tests/examples' ($flatSource -notmatch '(?m)^\s*(word|test|example)\s')
    $flat = New-Seed 'flat'
    $growing = New-Seed 'growing'
    $initialRequests = @(@{ op = 'words' }, @{ op = 'test-all' })
    $a = Invoke-Language $flat $initialRequests
    $b = Invoke-Language $growing $initialRequests
    $aWords = @($a[0].data.words)
    $bWords = @($b[0].data.words)
    Assert-Check 'Flat has no authored words' (@($aWords | Where-Object { $_.id.StartsWith('word_', [StringComparison]::Ordinal) }).Count -eq 0)
    Assert-Check 'Flat has zero tests' (@($a[1].data.results).Count -eq 0)
    Assert-Check 'Growing has exactly two authored words' (@($bWords | Where-Object { $_.id.StartsWith('word_', [StringComparison]::Ordinal) }).Count -eq 2)
    Assert-Check 'Growing has exactly seven passing tests' (@($b[1].data.results).Count -eq 7 -and @($b[1].data.results | Where-Object { -not $_.passed }).Count -eq 0)
    Assert-Check 'Renewal solution absent from both dictionaries' (@($aWords + $bWords | Where-Object { $_.name -eq 'customer.renewal-balance' }).Count -eq 0)

    $requests = @($cases | ForEach-Object {
        $quotedKind = ConvertTo-Json -InputObject $_.kind -Compress
        $number = ([double]$_.balance).ToString('R', [Globalization.CultureInfo]::InvariantCulture)
        if ($number -notmatch '[.eE]') { $number += '.0' }
        @{ op = 'eval'; args = @{ code = "$quotedKind $number customer.new customer.discounted-balance" } }
    })
    $language = Invoke-Language $growing $requests
    $caseLines = @($cases | ForEach-Object { ConvertTo-Json -InputObject $_ -Compress })
    $baselineOutput = @($caseLines | & dotnet $conventional --baseline-jsonl)
    $baselineExit = $LASTEXITCODE
    Assert-Check 'Conventional baseline adapter exits successfully' ($baselineExit -eq 0)
    Assert-Check 'Conventional baseline response count' ($baselineOutput.Count -eq $cases.Count)
    $baseline = @($baselineOutput | ForEach-Object { ConvertFrom-Json -InputObject $_ })
    $traces.Add([ordered]@{ profile = 'conventional-baseline'; requests = $cases; responses = $baseline; exitCode = $baselineExit })
    for ($i = 0; $i -lt $cases.Count; $i++) {
        Assert-Check "baseline adapter case $i succeeds" ($baseline[$i].ok -eq $true)
        Assert-Check "language case $i has exact Float output shape" ($language[$i].data.stack.Count -eq 1 -and $language[$i].data.stackTypes.Count -eq 1 -and $language[$i].data.stackTypes[0] -eq 'Float')
        $actual = [double]::Parse($language[$i].data.stack[0], [Globalization.CultureInfo]::InvariantCulture)
        Assert-Check "baseline semantics agree for case $i" ($actual -eq [double]$baseline[$i].discountedBalance)
    }
    $taskOutput = @(& dotnet $conventional)
    $taskExit = $LASTEXITCODE
    $traces.Add([ordered]@{ profile = 'conventional-unsolved'; output = $taskOutput; exitCode = $taskExit })
    Assert-Check 'Conventional placeholder fails task with exit 1' ($taskExit -eq 1)
    Assert-Check 'Exactly eligible combination fails' (@($taskOutput | Where-Object { $_ -like 'FAIL premium-annual-renewable:*' }).Count -eq 1 -and @($taskOutput | Where-Object { $_ -like 'FAIL *' }).Count -eq 1)
    Assert-Check 'Remaining seven eligibility combinations pass' (@($taskOutput | Where-Object { $_ -like 'PASS *' }).Count -eq 7)
    $passed = $true
} catch {
    $failure = $_.Exception.Message
} finally {
    $evidence = [ordered]@{
        schemaVersion = 2; passed = $passed; failure = $failure
        cli = [ordered]@{ path = $cli; sha256 = (Get-FileHash -LiteralPath $cli -Algorithm SHA256).Hash; files = $cliFiles }
        conventional = [ordered]@{ path = $conventional; sha256 = (Get-FileHash -LiteralPath $conventional -Algorithm SHA256).Hash; files = $conventionalFiles }
        verifierSha256 = $verifierHash
        scratch = $scratch; checks = @($checks.ToArray()); traces = @($traces.ToArray())
        limits = @('Selected finite baseline cases only; not all Float inputs or signed-zero representation parity.', 'Fresh legacy seeds assign word identities on load; actual trial snapshots must freeze identities before runs.', 'No agent trial or performance measurement is performed.')
    }
    $target = if ([IO.Path]::IsPathRooted($EvidencePath)) { $EvidencePath } else { Join-Path $repo $EvidencePath }
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    [IO.File]::WriteAllText($target, (ConvertTo-Json -InputObject $evidence -Depth 64), [Text.UTF8Encoding]::new($false))
}
if (-not $passed) { throw $failure }
Write-Output "$($checks.Count) matched fixture checks passed. Evidence: $target"
