#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CliDll,
    [string]$ConventionalDll = 'experiments/AgentLang.SubagentTrials/matched-renewal-001/fixtures/flow/conventional/bin/Release/net9.0/MatchedRenewal.Flow.dll',
    [string]$EvidencePath = '.agentlang/reports/flow-matched-renewal-fixtures.json'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $repo 'experiments/AgentLang.SubagentTrials/matched-renewal-001/fixtures'
$flowFixture = Join-Path $fixture 'flow'
$cli = (Resolve-Path -LiteralPath $CliDll).Path
$conventional = (Resolve-Path -LiteralPath $ConventionalDll).Path
$scratch = Join-Path $repo ('.agentlang/matched-fixture-flow-verification/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
$checks = [Collections.Generic.List[object]]::new()
$traces = [Collections.Generic.List[object]]::new()
$profiles = [Collections.Generic.List[object]]::new()

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

function Assert-Check([string]$Name, [bool]$Passed, [string]$Detail = '') {
    $checks.Add([ordered]@{ name = $Name; passed = $Passed; detail = $Detail })
    if (-not $Passed) { throw "Fixture check failed: $Name. $Detail" }
}

function Invoke-Language([string]$Project, [string]$Profile, [object[]]$Requests) {
    foreach ($request in $Requests) {
        if ($request.op -in @('define', 'eval')) { $request.frontend = 'flow' }
    }
    $lines = @($Requests | ForEach-Object { ConvertTo-Json -InputObject $_ -Depth 24 -Compress })
    $output = @($lines | & dotnet $cli --project $Project --jsonl)
    $exit = $LASTEXITCODE
    Assert-Check "$Profile language process exit" ($exit -eq 0) "exit=$exit"
    Assert-Check "$Profile response count" ($output.Count -eq $Requests.Count) "requests=$($Requests.Count), responses=$($output.Count)"
    $responses = @($output | ForEach-Object { ConvertFrom-Json -InputObject $_ -Depth 64 })
    $traces.Add([ordered]@{ profile = $Profile; projectPath = [IO.Path]::GetFullPath($Project); requests = $Requests; responses = $responses; exitCode = $exit })
    foreach ($response in $responses) {
        Assert-Check "$Profile request succeeds" ($response.ok -eq $true) ([string]$response.text)
    }
    return ,$responses
}

function Get-UserWords([object]$WordsResponse) {
    return @($WordsResponse.data.words | Where-Object {
        ([string]$_.id).StartsWith('word_', [StringComparison]::Ordinal)
    } | Sort-Object name)
}

function New-SeedProject([string]$Name) {
    $directory = Join-Path $scratch $Name
    New-Item -ItemType Directory -Path $directory | Out-Null
    return $directory
}

function Get-ProjectProfile([string]$Name, [string]$SnapshotName) {
    $sourcePath = Join-Path $flowFixture ($Name + '.agent')
    $source = Get-Content -LiteralPath $sourcePath -Raw
    $project = New-SeedProject $Name
    $expectedWords = @()
    $expectedTests = @()
    if ($Name -ne 'flat') {
        $expectedWords = @('customer.discounted-balance', 'customer.premium?')
        $expectedTests = @(
            'customer.discounted-balance/case-sensitive',
            'customer.discounted-balance/premium',
            'customer.discounted-balance/premium-larger-balance',
            'customer.discounted-balance/standard',
            'customer.premium?/case-sensitive',
            'customer.premium?/premium',
            'customer.premium?/standard'
        )
    }
    $requests = [Collections.Generic.List[object]]::new()
    $requests.Add([ordered]@{ op = 'define'; frontend = 'flow'; source = $source })
    $requests.Add([ordered]@{ op = 'words' })
    $requests.Add([ordered]@{ op = 'source'; type = 'Customer' })
    $requests.Add([ordered]@{ op = 'source'; type = 'Subscription' })
    $requests.Add([ordered]@{ op = 'test-all' })
    $requests.Add([ordered]@{ op = 'commit'; word = 'Customer' })
    $requests.Add([ordered]@{ op = 'commit'; word = 'Subscription' })
    if ($expectedWords.Count -gt 0) {
        $requests.Add([ordered]@{ op = 'commit'; word = 'customer.premium?'; library = $true })
        $requests.Add([ordered]@{ op = 'commit'; word = 'customer.discounted-balance'; library = $true })
    }
    $requests.Add([ordered]@{ op = 'snapshot.save'; name = $SnapshotName })
    $seed = Invoke-Language $project ($Name + '-seed') @($requests.ToArray())
    $define = $seed[0]
    $stagedWords = @(Get-UserWords $seed[1])
    $typeCustomerSource = [string]$seed[2].data
    $typeSubscriptionSource = [string]$seed[3].data
    $seedTestResults = @($seed[4].data.results)
    $save = $seed[$seed.Count - 1]

    Assert-Check "$Name declares only Customer and Subscription" (
        (@($define.data.types | Sort-Object) -join ',') -eq 'Customer,Subscription'
    ) ([string](@($define.data.types | Sort-Object) -join ','))
    Assert-Check "$Name authored word inventory" (
        (@($stagedWords | ForEach-Object name) -join ',') -eq ($expectedWords -join ',')
    ) ("actual=" + (@($stagedWords | ForEach-Object name) -join ','))
    Assert-Check "$Name Customer fields are exactly kind:String and balance:Float" (
        $typeCustomerSource -match '(?s)record Customer\s*\{\s*field kind:\s*String;\s*field balance:\s*Float;\s*\}'
    ) $typeCustomerSource
    Assert-Check "$Name Subscription fields are exactly term:String and renewable:Bool" (
        $typeSubscriptionSource -match '(?s)record Subscription\s*\{\s*field term:\s*String;\s*field renewable:\s*Bool;\s*\}'
    ) $typeSubscriptionSource
    Assert-Check "$Name seed source contains no renewal solution" (
        $source -notmatch 'customer\.renewal-balance|subscription\.renewable\?|renewal-balance'
    )
    Assert-Check "$Name attached test count" ($seedTestResults.Count -eq $expectedTests.Count) "expected=$($expectedTests.Count), actual=$($seedTestResults.Count)"
    $seedTestNames = @($seedTestResults | ForEach-Object { "$($_.word)/$($_.name)" } | Sort-Object) -join ([Environment]::NewLine)
    $expectedTestNames = @($expectedTests | Sort-Object) -join ([Environment]::NewLine)
    $seedTestsPassed = @($seedTestResults | Where-Object { $_.passed -ne $true }).Count -eq 0
    Assert-Check "$Name attached test names and results" ($seedTestNames -eq $expectedTestNames -and $seedTestsPassed) (@($seedTestResults | ForEach-Object { "$($_.word)/$($_.name):$($_.passed)" } | Sort-Object) -join ', ')
    if ($expectedWords.Count -eq 0) {
        Assert-Check 'Flat seed has zero attached tests' ($seedTestResults.Count -eq 0)
    }

    $stagedMetadata = @($define.data.words | Sort-Object name)
    Assert-Check "$Name define response has expected authored word heads" (
        (@($stagedMetadata | ForEach-Object name) -join ',') -eq ($expectedWords -join ',')
    ) ("actual=" + (@($stagedMetadata | ForEach-Object name) -join ','))
    Assert-Check "$Name authored word IDs are well formed" (
        @($stagedMetadata | Where-Object { -not ([string]$_.id).StartsWith('word_', [StringComparison]::Ordinal) }).Count -eq 0
    )

    # A separate CLI invocation loads this same committed project and then restores its named snapshot.
    $reload = Invoke-Language $project ($Name + '-reload') @(
        [ordered]@{ op = 'snapshot.load'; name = $SnapshotName },
        [ordered]@{ op = 'words' },
        [ordered]@{ op = 'test-all' },
        [ordered]@{ op = 'source'; type = 'Customer' },
        [ordered]@{ op = 'source'; type = 'Subscription' }
    )
    $loadedWords = @(Get-UserWords $reload[1])
    $reloadedResults = @($reload[2].data.results)
    $load = $reload[0]
    Assert-Check "$Name snapshot load reports its committed manifest hash" (
        ([string]$load.data.manifestHash) -match '^[0-9a-f]{64}$'
    ) ([string]$load.data.manifestHash)
    $loadedIdentityRows = @($loadedWords | ForEach-Object { "$($_.name)|$($_.id)|$($_.maturity)|$($_.status)" }) -join ([Environment]::NewLine)
    $expectedIdentityRows = @($stagedMetadata | ForEach-Object { "$($_.name)|$($_.id)|library|persistent" }) -join ([Environment]::NewLine)
    Assert-Check "$Name committed word IDs survive fresh-process snapshot reload" ($loadedIdentityRows -eq $expectedIdentityRows) $loadedIdentityRows
    $reloadTestNames = @($reloadedResults | ForEach-Object { "$($_.word)/$($_.name)" } | Sort-Object) -join ([Environment]::NewLine)
    $reloadedTestsPassed = @($reloadedResults | Where-Object { $_.passed -ne $true }).Count -eq 0
    $reloadedTestNamesMatch = $reloadTestNames -eq $expectedTestNames
    $reloadTestsPass = $reloadedResults.Count -eq $expectedTests.Count -and $reloadedTestsPassed -and $reloadedTestNamesMatch
    Assert-Check "$Name tests pass after fresh-process snapshot reload" $reloadTestsPass "expected=$($expectedTests.Count), actual=$($reloadedResults.Count)"
    $describeRequests = @([ordered]@{ op = 'test-all' }) + @($expectedWords | ForEach-Object { [ordered]@{ op = 'describe'; word = $_ } })
    if ($expectedWords.Count -gt 0) {
        $describe = Invoke-Language $project ($Name + '-coverage') $describeRequests
        $byName = @{}
        foreach ($item in @($describe | Select-Object -Skip 1)) { $byName[[string]$item.data.name] = $item.data }
        $premiumDescription = $byName['customer.premium?']
        $premiumSignatureAndMetadataMatch = (@($premiumDescription.inputs) -join ',') -eq 'Customer' -and
            (@($premiumDescription.outputs) -join ',') -eq 'Bool' -and
            $premiumDescription.documentation -eq 'Returns true only when the customer kind is exactly premium.' -and
            $premiumDescription.maturity -eq 'library' -and
            $premiumDescription.revision -eq 1
        Assert-Check "$Name premium word signature, documentation, library level and revision" $premiumSignatureAndMetadataMatch ($premiumDescription | ConvertTo-Json -Depth 12 -Compress)
        $discountDescription = $byName['customer.discounted-balance']
        $discountSignatureAndMetadataMatch = (@($discountDescription.inputs) -join ',') -eq 'Customer' -and
            (@($discountDescription.outputs) -join ',') -eq 'Float' -and
            $discountDescription.documentation -eq 'Returns 90 percent of the balance for premium customers and the original balance for every other kind.' -and
            $discountDescription.maturity -eq 'library' -and
            $discountDescription.revision -eq 1
        Assert-Check "$Name discounted balance signature, documentation and library level" $discountSignatureAndMetadataMatch ($discountDescription | ConvertTo-Json -Depth 12 -Compress)
        foreach ($word in $expectedWords) {
            $coverage = $byName[$word].coverage
            $coverageComplete = $coverage.instructionsTotal -gt 0 -and
                $coverage.instructionsCovered -eq $coverage.instructionsTotal -and
                $coverage.branchesCovered -eq $coverage.branchesTotal
            Assert-Check "$Name library gate coverage is complete for $word" $coverageComplete ($coverage | ConvertTo-Json -Depth 8 -Compress)
        }
    }

    $profile = [ordered]@{
        name = $Name
        projectPath = [IO.Path]::GetFullPath($project)
        sourcePath = [IO.Path]::GetFullPath($sourcePath)
        sourceSha256 = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
        sourceBytes = [IO.File]::ReadAllBytes($sourcePath).Length
        snapshotName = $SnapshotName
        snapshotSaveResponse = $save
        snapshotLoadResponse = $load
        manifestHash = [string]$load.data.manifestHash
        definedWords = $stagedMetadata
        reloadedWords = $loadedWords
        testNames = $expectedTests
        seedTestResults = $seedTestResults
        reloadedTestResults = $reloadedResults
        customerTypeSource = $typeCustomerSource
        subscriptionTypeSource = $typeSubscriptionSource
        source = $source
    }
    $profiles.Add($profile)
    return $profile
}

$cases = @(
    @{ kind = 'premium'; balance = [double]100.0 },
    @{ kind = 'standard'; balance = [double]100.0 },
    @{ kind = 'Premium'; balance = [double]80.0 },
    @{ kind = ''; balance = [double]100.0 },
    @{ kind = 'premium '; balance = [double]100.0 },
    @{ kind = 'premium'; balance = [double]0.0 },
    @{ kind = 'premium'; balance = [double]-100.0 },
    @{ kind = 'premium'; balance = [double]250.0 },
    @{ kind = 'premium'; balance = [double]3.141592653589793 },
    @{ kind = 'premium'; balance = [double]1e-100 },
    @{ kind = 'standard'; balance = [double]-250.0 },
    @{ kind = 'prémium'; balance = [double]100.0 }
)
$passed = $false
$failure = $null
$cliFiles = Get-RuntimeFiles $cli
$conventionalFiles = Get-RuntimeFiles $conventional
$flowConventionalProject = Join-Path $flowFixture 'conventional/MatchedRenewal.Flow.fsproj'
$flowConventionalDomain = Join-Path $flowFixture 'conventional/Domain.fs'
$flowConventionalProgram = Join-Path $flowFixture 'conventional/Program.fs'
try {
    $flatPath = Join-Path $flowFixture 'flat.agent'
    $growingPath = Join-Path $flowFixture 'growing.agent'
    $legacyGrowingPath = Join-Path $fixture 'growing.agent'
    $flatSource = Get-Content -LiteralPath $flatPath -Raw
    $growingSource = Get-Content -LiteralPath $growingPath -Raw
    $legacyGrowingSource = Get-Content -LiteralPath $legacyGrowingPath -Raw

    Assert-Check 'Flow seeds preserve exact baseline record declarations' (
        $flatSource -match '(?s)record Customer\s*\{\s*field kind:\s*String;\s*field balance:\s*Float;\s*\}.*record Subscription\s*\{\s*field term:\s*String;\s*field renewable:\s*Bool;\s*\}'
    )
    $flowWordCount = @([regex]::Matches($growingSource, '(?m)^word customer\.')).Count
    $flowTestCount = @([regex]::Matches($growingSource, '(?m)^test customer\.')).Count
    $growingShapeMatches = $flowWordCount -eq 2 -and
        $flowTestCount -eq 7
    Assert-Check 'Flow growing seed declares exactly two library words and seven cases' $growingShapeMatches "words=$flowWordCount tests=$flowTestCount"
    $flowCaseNames = @([regex]::Matches($growingSource, '(?m)^test ([^/\s]+/[^ \s{]+)') | ForEach-Object { $_.Groups[1].Value } | Sort-Object) -join ','
    $legacyCaseNames = @([regex]::Matches($legacyGrowingSource, '(?m)^test ([^/\s]+/[^ \s]+)') | ForEach-Object { $_.Groups[1].Value } | Sort-Object) -join ','
    Assert-Check 'Flow and archived Stack growing fixtures retain matching test case names' ($flowCaseNames -eq $legacyCaseNames) "Flow=$flowCaseNames Stack=$legacyCaseNames"
    Assert-Check 'Flow seed owns no renewal solution' (
        $growingSource -notmatch 'customer\.renewal-balance|subscription\.renewable\?|renewal-balance'
    )

    $flat = Get-ProjectProfile 'flat' 'matched-renewal-flow-flat-v1'
    $growing = Get-ProjectProfile 'growing' 'matched-renewal-flow-growing-v1'

    $requests = @($cases | ForEach-Object {
        $quotedKind = ConvertTo-Json -InputObject $_.kind -Compress
        $number = ([double]$_.balance).ToString('R', [Globalization.CultureInfo]::InvariantCulture)
        if ($number -notmatch '[.eE]') { $number += '.0' }
        [ordered]@{
            op = 'eval'
            frontend = 'flow'
            code = "customer::discounted-balance(customer::new(kind = $quotedKind, balance = $number))"
        }
    })
    $language = Invoke-Language $growing.projectPath 'growing-baseline' $requests
    $caseLines = @($cases | ForEach-Object { ConvertTo-Json -InputObject $_ -Depth 8 -Compress })
    $baselineOutput = @($caseLines | & dotnet $conventional --baseline-jsonl)
    $baselineExit = $LASTEXITCODE
    Assert-Check 'Flow conventional baseline adapter exits successfully' ($baselineExit -eq 0) "exit=$baselineExit"
    Assert-Check 'Flow conventional baseline response count' ($baselineOutput.Count -eq $cases.Count) "expected=$($cases.Count), actual=$($baselineOutput.Count)"
    $baseline = @($baselineOutput | ForEach-Object { ConvertFrom-Json -InputObject $_ -Depth 32 })
    $traces.Add([ordered]@{ profile = 'flow-conventional-baseline'; requests = $cases; responses = $baseline; exitCode = $baselineExit })
    for ($i = 0; $i -lt $cases.Count; $i++) {
        $baselineResponseJson = $baseline[$i] | ConvertTo-Json -Depth 8 -Compress
        Assert-Check "Flow conventional baseline case $i succeeds" ($baseline[$i].ok -eq $true) $baselineResponseJson
        $flowResult = $language[$i].data
        $flowShapeMatches = $flowResult.stack.Count -eq 1 -and
            $flowResult.stackTypes.Count -eq 1 -and
            $flowResult.stackTypes[0] -eq 'Float'
        Assert-Check "Flow language case $i has exact Float output shape" $flowShapeMatches ($flowResult | ConvertTo-Json -Depth 8 -Compress)
        $actual = [double]::Parse([string]$language[$i].data.stack[0], [Globalization.CultureInfo]::InvariantCulture)
        Assert-Check "Flow baseline semantics agree for case $i" ($actual -eq [double]$baseline[$i].discountedBalance) "Flow=$actual conventional=$($baseline[$i].discountedBalance)"
    }

    $taskOutput = @(& dotnet $conventional)
    $taskExit = $LASTEXITCODE
    $traces.Add([ordered]@{ profile = 'flow-conventional-unsolved'; output = $taskOutput; exitCode = $taskExit })
    Assert-Check 'Flow conventional placeholder fails renewal task with exit 1' ($taskExit -eq 1) "exit=$taskExit"
    $twoExpectedCombinationsFail = @($taskOutput | Where-Object { $_ -like 'FAIL premium-annual-renewable:*' }).Count -eq 1 -and
        @($taskOutput | Where-Object { $_ -like 'FAIL standard-annual-renewable:*' }).Count -eq 1 -and
        @($taskOutput | Where-Object { $_ -like 'FAIL *' }).Count -eq 2
    Assert-Check 'Both eligible renewal combinations fail in the placeholder' $twoExpectedCombinationsFail ($taskOutput -join ([Environment]::NewLine))
    $sixOtherCombinationsPass = @($taskOutput | Where-Object { $_ -like 'PASS *' }).Count -eq 6
    Assert-Check 'Remaining six renewal combinations pass in the placeholder' $sixOtherCombinationsPass ($taskOutput -join ([Environment]::NewLine))
    $passed = $true
} catch {
    $failure = $_.Exception.Message
} finally {
    $sourcePaths = @(
        (Join-Path $flowFixture 'flat.agent')
        (Join-Path $flowFixture 'growing.agent')
        $flowConventionalProject
        $flowConventionalDomain
        $flowConventionalProgram
    )
    $sourceFiles = @($sourcePaths | ForEach-Object {
        [ordered]@{ path = [IO.Path]::GetFullPath($_); sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    $evidence = [ordered]@{
        schemaVersion = 1
        passed = $passed
        failure = $failure
        frontend = 'flow'
        cli = [ordered]@{ path = $cli; sha256 = (Get-FileHash -LiteralPath $cli -Algorithm SHA256).Hash.ToLowerInvariant(); files = $cliFiles }
        conventional = [ordered]@{
            path = $conventional
            sha256 = (Get-FileHash -LiteralPath $conventional -Algorithm SHA256).Hash.ToLowerInvariant()
            files = $conventionalFiles
            sourceFiles = $sourceFiles
        }
        verifierPath = [IO.Path]::GetFullPath($PSCommandPath)
        verifierSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
        taskSha256 = (Get-FileHash -LiteralPath (Join-Path (Split-Path $fixture -Parent) 'task.md') -Algorithm SHA256).Hash.ToLowerInvariant()
        scratchRoot = [IO.Path]::GetFullPath($scratch)
        profiles = @($profiles.ToArray())
        checks = @($checks.ToArray())
        traces = @($traces.ToArray())
        limits = @(
            'The 12 baseline inputs are a selected finite Float/String check, not exhaustive domain or signed-zero proof.',
            'Snapshot hashes and stable IDs prove only the generated seed projects; root publication must pin and review generated snapshot bundles before trials.',
            'The renewal adapter is an unsolved placeholder check; no agent trial, task solution, latency, or performance claim is made.',
            'The sample uses String, Float, and Bool fields; it does not validate refined Email, Money, identifier, or time types.'
        )
    }
    $target = if ([IO.Path]::IsPathRooted($EvidencePath)) { $EvidencePath } else { Join-Path $repo $EvidencePath }
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    [IO.File]::WriteAllText($target, (ConvertTo-Json -InputObject $evidence -Depth 80), [Text.UTF8Encoding]::new($false))
}
if (-not $passed) { throw $failure }
Write-Output "$($checks.Count) Flow matched fixture checks passed. Evidence: $target"
