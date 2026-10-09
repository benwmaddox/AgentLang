#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CliDll,
    [string]$EvidencePath = '.agentlang/reports/flow-renewal-acceptance-gate.json'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$cli = (Resolve-Path -LiteralPath $CliDll).Path
$restoreScript = Join-Path $PSScriptRoot 'Restore-MatchedRenewalSnapshot.ps1'
$solutionVerifier = Join-Path $PSScriptRoot 'Verify-FlowRenewalSolution.ps1'
$oraclePath = Join-Path $repo 'experiments/AgentLang.SubagentTrials/matched-renewal-001/flow-renewal-acceptance.json'
$oracle = Get-Content -LiteralPath $oraclePath -Raw | ConvertFrom-Json
if ($oracle.schemaVersion -ne 1 -or $oracle.word -ne 'customer.renewal-balance') {
    throw 'Unexpected renewal acceptance oracle identity.'
}

$runId = [Guid]::NewGuid().ToString('N')
$startedUtc = [DateTimeOffset]::UtcNow.ToString('O')
$runRootRelative = ".agentlang/renewal-acceptance-gate/$runId"
$runRoot = [IO.Path]::GetFullPath($runRootRelative, $repo)
[void][IO.Directory]::CreateDirectory($runRoot)
$evidenceTarget = if ([IO.Path]::IsPathRooted($EvidencePath)) {
    [IO.Path]::GetFullPath($EvidencePath)
} else {
    [IO.Path]::GetFullPath($EvidencePath, $repo)
}
$script:checks = [Collections.Generic.List[object]]::new()
$script:sessions = [Collections.Generic.List[object]]::new()
$scenarioResults = [Collections.Generic.List[object]]::new()
$passed = $false
$failure = $null

function Assert-GateCheck([string]$Name, [bool]$Passed, [string]$Detail = '') {
    $script:checks.Add([ordered]@{ name = $Name; passed = $Passed; detail = $Detail })
    if (-not $Passed) { throw "Renewal acceptance gate failed: $Name. $Detail" }
}

function Invoke-CliSession {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$ProjectPath,
        [Parameter(Mandatory)][object[]]$Requests
    )

    $lines = @($Requests | ForEach-Object { ConvertTo-Json -InputObject $_ -Depth 24 -Compress })
    $rawResponses = @($lines | & dotnet $script:cli --project $ProjectPath --filesystem virtual --jsonl)
    $exitCode = $LASTEXITCODE
    $responses = [Collections.Generic.List[object]]::new()
    $parseErrors = [Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt $rawResponses.Count; $index++) {
        try {
            $responses.Add((ConvertFrom-Json -InputObject $rawResponses[$index] -Depth 64))
            $parseErrors.Add($null)
        } catch {
            $responses.Add($null)
            $parseErrors.Add($_.Exception.Message)
        }
    }

    $session = [ordered]@{
        name = $Name
        projectPath = [IO.Path]::GetFullPath($ProjectPath)
        processExitCode = $exitCode
        requests = $Requests
        rawResponses = $rawResponses
        responses = $responses.ToArray()
        parseErrors = $parseErrors.ToArray()
    }
    $script:sessions.Add($session)
    Assert-GateCheck "$Name process exits successfully" ($exitCode -eq 0) "exitCode=$exitCode"
    Assert-GateCheck "$Name returns one JSON response per request" ($rawResponses.Count -eq $Requests.Count) "requests=$($Requests.Count); responses=$($rawResponses.Count)"
    Assert-GateCheck "$Name response JSON parses" (@($parseErrors | Where-Object { $null -ne $_ }).Count -eq 0) (($parseErrors | Where-Object { $null -ne $_ }) -join '; ')
    Assert-GateCheck "$Name requests succeed" (@($responses | Where-Object { $null -eq $_ -or $_.ok -ne $true }).Count -eq 0) (
        (@($responses | Where-Object { $null -eq $_ -or $_.ok -ne $true } | ForEach-Object { $_.text }) -join '; ')
    )
    return $session
}

function Get-UserWordNames([object]$WordsResponse) {
    @($WordsResponse.data.words | Where-Object {
        ([string]$_.id).StartsWith('word_', [StringComparison]::Ordinal)
    } | ForEach-Object { [string]$_.name } | Sort-Object)
}

function Get-PremiumOnlySource {
    @'
word customer.renewal-balance(customer: Customer, subscription: Subscription) -> Float {
    effects none
    doc "Adversarial control: applies the annual renewal discount only to premium customers."
    let balance = customer.discounted-balance();
    if subscription.renewable() {
        if equals(subscription.term(), "annual") {
            if customer.premium?() {
                float::multiply(balance, 0.95)
            } else {
                balance
            }
        } else {
            balance
        }
    } else {
        balance
    }
}

test customer.renewal-balance/premium-annual-renewable {
    customer::renewal-balance(
        customer::new(kind = "premium", balance = 100.0),
        subscription::new(term = "annual", renewable = true)
    )
    => 85.5
}

test customer.renewal-balance/premium-monthly-renewable {
    customer::renewal-balance(
        customer::new(kind = "premium", balance = 100.0),
        subscription::new(term = "monthly", renewable = true)
    )
    => 90.0
}

test customer.renewal-balance/premium-annual-not-renewable {
    customer::renewal-balance(
        customer::new(kind = "premium", balance = 100.0),
        subscription::new(term = "annual", renewable = false)
    )
    => 90.0
}

test customer.renewal-balance/standard-annual-renewable {
    customer::renewal-balance(
        customer::new(kind = "standard", balance = 100.0),
        subscription::new(term = "annual", renewable = true)
    )
    => 100.0
}
'@
}

function Get-HardcodedBalanceSource {
    @'
word customer.renewal-balance(customer: Customer, subscription: Subscription) -> Float {
    effects none
    doc "Adversarial control: applies correct categorical discounts to a hardcoded balance of 100.0."
    let base = if customer.premium?() {
        float::multiply(100.0, 0.9)
    } else {
        100.0
    };
    if subscription.renewable() {
        if equals(subscription.term(), "annual") {
            float::multiply(base, 0.95)
        } else {
            base
        }
    } else {
        base
    }
}

test customer.renewal-balance/premium-annual-renewable {
    customer::renewal-balance(
        customer::new(kind = "premium", balance = 100.0),
        subscription::new(term = "annual", renewable = true)
    )
    => 85.5
}

test customer.renewal-balance/premium-annual-not-renewable {
    customer::renewal-balance(
        customer::new(kind = "premium", balance = 100.0),
        subscription::new(term = "annual", renewable = false)
    )
    => 90.0
}

test customer.renewal-balance/premium-monthly-renewable {
    customer::renewal-balance(
        customer::new(kind = "premium", balance = 100.0),
        subscription::new(term = "monthly", renewable = true)
    )
    => 90.0
}

test customer.renewal-balance/premium-monthly-not-renewable {
    customer::renewal-balance(
        customer::new(kind = "premium", balance = 100.0),
        subscription::new(term = "monthly", renewable = false)
    )
    => 90.0
}

test customer.renewal-balance/standard-annual-renewable {
    customer::renewal-balance(
        customer::new(kind = "standard", balance = 100.0),
        subscription::new(term = "annual", renewable = true)
    )
    => 95.0
}

test customer.renewal-balance/standard-annual-not-renewable {
    customer::renewal-balance(
        customer::new(kind = "standard", balance = 100.0),
        subscription::new(term = "annual", renewable = false)
    )
    => 100.0
}

test customer.renewal-balance/standard-monthly-renewable {
    customer::renewal-balance(
        customer::new(kind = "standard", balance = 100.0),
        subscription::new(term = "monthly", renewable = true)
    )
    => 100.0
}

test customer.renewal-balance/standard-monthly-not-renewable {
    customer::renewal-balance(
        customer::new(kind = "standard", balance = 100.0),
        subscription::new(term = "monthly", renewable = false)
    )
    => 100.0
}

test customer.renewal-balance/case-sensitive-kind {
    customer::renewal-balance(
        customer::new(kind = "Premium", balance = 100.0),
        subscription::new(term = "annual", renewable = true)
    )
    => 95.0
}

test customer.renewal-balance/case-sensitive-term {
    customer::renewal-balance(
        customer::new(kind = "premium", balance = 100.0),
        subscription::new(term = "Annual", renewable = true)
    )
    => 90.0
}

test customer.renewal-balance/term-trailing-space {
    customer::renewal-balance(
        customer::new(kind = "premium", balance = 100.0),
        subscription::new(term = "annual ", renewable = true)
    )
    => 90.0
}

test customer.renewal-balance/empty-kind {
    customer::renewal-balance(
        customer::new(kind = "", balance = 100.0),
        subscription::new(term = "annual", renewable = true)
    )
    => 95.0
}
'@
}

function Invoke-ExpectedRejection {
    param(
        [Parameter(Mandatory)][System.Collections.IDictionary]$Scenario,
        [Parameter(Mandatory)][string]$ProjectPath,
        [Parameter(Mandatory)][string]$VerifierEvidencePath
    )

    $expectedFailure = "Renewal acceptance failed: $($Scenario.expectedFailedCheck)"
    $invocationError = $null
    $output = @()
    try {
        $output = @(& $script:solutionVerifier -CliDll $script:cli -ProjectPath $ProjectPath -EvidencePath $VerifierEvidencePath)
    } catch {
        $invocationError = $_.Exception.Message
    }

    Assert-GateCheck "$($Scenario.name) verifier rejects the control" ($null -ne $invocationError -and $invocationError -ceq $expectedFailure) "expected='$expectedFailure'; observed='$invocationError'; output='$($output -join ' ')'"
    if (-not (Test-Path -LiteralPath $VerifierEvidencePath -PathType Leaf)) {
        throw "Expected verifier evidence was not written for scenario '$($Scenario.name)'."
    }
    $verifierEvidence = Get-Content -LiteralPath $VerifierEvidencePath -Raw | ConvertFrom-Json -Depth 64
    $failedChecks = @($verifierEvidence.checks | Where-Object { $_.passed -ne $true })
    Assert-GateCheck "$($Scenario.name) verifier evidence records the exact rejection" (
        $verifierEvidence.passed -eq $false -and
        $verifierEvidence.failure -ceq $expectedFailure -and
        $failedChecks.Count -eq 1 -and
        $failedChecks[0].name -ceq $Scenario.expectedFailedCheck
    ) "failure='$($verifierEvidence.failure)'; failedChecks=$(($failedChecks | ForEach-Object name) -join ',')"

    if ($Scenario.name -ne 'missing-word') {
        $requiredPreconditions = @(
            'all attached tests pass',
            'exact target signature',
            'pure persistent library target',
            'target has passing attached tests',
            'target own instruction and branch coverage complete'
        )
        foreach ($checkName in $requiredPreconditions) {
            $check = @($verifierEvidence.checks | Where-Object name -CEQ $checkName | Select-Object -First 1)
            Assert-GateCheck "$($Scenario.name) verifier confirms '$checkName' before the oracle rejection" (
                $check.Count -eq 1 -and $check[0].passed -eq $true
            ) "observed=$(if ($check.Count -eq 1) { $check[0].passed } else { 'missing' })"
        }
        $expectedIndex = -1
        for ($index = 0; $index -lt $verifierEvidence.checks.Count; $index++) {
            if ([string]$verifierEvidence.checks[$index].name -ceq $Scenario.expectedFailedCheck) { $expectedIndex = $index; break }
        }
        $prefixPassed = $expectedIndex -gt 0
        if ($prefixPassed) {
            for ($index = 0; $index -lt $expectedIndex; $index++) {
                if ($verifierEvidence.checks[$index].passed -ne $true) { $prefixPassed = $false; break }
            }
        }
        Assert-GateCheck "$($Scenario.name) rejection follows only successful verifier preconditions" $prefixPassed "failedCheckIndex=$expectedIndex"
    }

    return [ordered]@{
        exception = $invocationError
        output = $output
        expectedFailedCheck = $Scenario.expectedFailedCheck
        verifierEvidencePath = $VerifierEvidencePath
        verifierEvidence = $verifierEvidence
    }
}

try {
    Assert-GateCheck 'restore helper exists' (Test-Path -LiteralPath $restoreScript -PathType Leaf) $restoreScript
    Assert-GateCheck 'solution verifier exists' (Test-Path -LiteralPath $solutionVerifier -PathType Leaf) $solutionVerifier
    Assert-GateCheck 'CLI artifact exists' (Test-Path -LiteralPath $cli -PathType Leaf) $cli

    $scenarios = @(
        [ordered]@{ name = 'missing-word'; expectedFailedCheck = 'all requests succeed'; source = $null; expectedTestNames = @() },
        [ordered]@{ name = 'premium-only-annual-discount'; expectedFailedCheck = 'standard-annual-true independent numerical result'; source = (Get-PremiumOnlySource); expectedTestNames = @('premium-annual-not-renewable', 'premium-annual-renewable', 'premium-monthly-renewable', 'standard-annual-renewable') },
        [ordered]@{ name = 'hardcoded-balance'; expectedFailedCheck = 'zero-balance independent numerical result'; source = (Get-HardcodedBalanceSource); expectedTestNames = @('case-sensitive-kind', 'case-sensitive-term', 'empty-kind', 'premium-annual-not-renewable', 'premium-annual-renewable', 'premium-monthly-not-renewable', 'premium-monthly-renewable', 'standard-annual-not-renewable', 'standard-annual-renewable', 'standard-monthly-not-renewable', 'standard-monthly-renewable', 'term-trailing-space') }
    )

    foreach ($scenario in $scenarios) {
        $scenarioRootRelative = Join-Path $runRootRelative $scenario.name
        $projectRelative = Join-Path $scenarioRootRelative 'project'
        $projectPath = [IO.Path]::GetFullPath($projectRelative, $repo)
        if (Test-Path -LiteralPath $projectPath) { throw "Generated control project already exists: $projectPath" }

        & $restoreScript -Profile growing -ProjectPath $projectRelative | Out-Null
        Assert-GateCheck "$($scenario.name) restores the frozen Growing snapshot" (Test-Path -LiteralPath (Join-Path $projectPath '.agentlang/store/CURRENT') -PathType Leaf) $projectPath

        $result = [ordered]@{
            name = $scenario.name
            projectPath = $projectPath
            expectedFailedCheck = $scenario.expectedFailedCheck
            restoreProfile = 'growing'
            baseline = $null
            setup = $null
            rejection = $null
        }
        $scenarioResults.Add($result)

        $baselineSession = Invoke-CliSession -Name "$($scenario.name)-baseline" -ProjectPath $projectPath -Requests @(
            [ordered]@{ op = 'test-all' },
            [ordered]@{ op = 'words' }
        )
        $result.baseline = $baselineSession
        $baselineTests = @($baselineSession.responses[0].data.results)
        Assert-GateCheck "$($scenario.name) frozen Growing baseline has seven passing tests" (
            $baselineTests.Count -eq 7 -and @($baselineTests | Where-Object { $_.passed -ne $true }).Count -eq 0
        ) "tests=$($baselineTests.Count); failed=$(@($baselineTests | Where-Object { $_.passed -ne $true }).Count)"
        $baselineWords = @(Get-UserWordNames $baselineSession.responses[1])
        Assert-GateCheck "$($scenario.name) baseline has no target word" ($baselineWords -notcontains $oracle.word) "userWords=$($baselineWords -join ',')"

        if ($null -ne $scenario.source) {
            $source = [string]$scenario.source
            $requests = @(
                [ordered]@{ op = 'task.begin'; goal = "Generated verifier control: $($scenario.name)" },
                [ordered]@{ op = 'define'; frontend = 'flow'; source = $source },
                [ordered]@{ op = 'test'; word = $oracle.word },
                [ordered]@{ op = 'commit'; word = $oracle.word; library = $true },
                [ordered]@{ op = 'test-all' },
                [ordered]@{ op = 'task.commit' }
            )
            $setupSession = Invoke-CliSession -Name "$($scenario.name)-setup" -ProjectPath $projectPath -Requests $requests
            $result.setup = $setupSession

            $definedWordNames = @($setupSession.responses[1].data | ForEach-Object { [string]$_.name })
            Assert-GateCheck "$($scenario.name) stages one generated target word" (
                $definedWordNames.Count -eq 1 -and $definedWordNames[0] -ceq $oracle.word
            ) ($definedWordNames -join ',')
            $targetTestResults = @($setupSession.responses[2].data.results)
            $actualTestNames = @($targetTestResults | ForEach-Object { [string]$_.name } | Sort-Object)
            $expectedTestNames = @($scenario.expectedTestNames | Sort-Object)
            Assert-GateCheck "$($scenario.name) attached control tests all pass" (
                $targetTestResults.Count -eq $expectedTestNames.Count -and
                @($targetTestResults | Where-Object { $_.passed -ne $true }).Count -eq 0 -and
                (($actualTestNames -join ',') -ceq ($expectedTestNames -join ','))
            ) "expected=$($expectedTestNames -join ','); actual=$($actualTestNames -join ',')"

            $postCommitTests = @($setupSession.responses[4].data.results)
            $targetPostCommitTests = @($postCommitTests | Where-Object { $_.word -ceq $oracle.word })
            Assert-GateCheck "$($scenario.name) library commit retains passing attached tests" (
                $targetPostCommitTests.Count -eq $expectedTestNames.Count -and
                @($targetPostCommitTests | Where-Object { $_.passed -ne $true }).Count -eq 0
            ) "targetTests=$($targetPostCommitTests.Count); failed=$(@($targetPostCommitTests | Where-Object { $_.passed -ne $true }).Count)"

            $finalInspectSession = Invoke-CliSession -Name "$($scenario.name)-post-task-commit-inspection" -ProjectPath $projectPath -Requests @(
                [ordered]@{ op = 'test-all' },
                [ordered]@{ op = 'describe'; word = $oracle.word },
                [ordered]@{ op = 'source'; word = $oracle.word },
                [ordered]@{ op = 'dependencies'; word = $oracle.word },
                [ordered]@{ op = 'words' }
            )
            $result.finalInspection = $finalInspectSession
            $finalTestResults = @($finalInspectSession.responses[0].data.results)
            Assert-GateCheck "$($scenario.name) post-task-commit fresh process runs every passing attached test" (
                $finalTestResults.Count -eq (7 + $expectedTestNames.Count) -and
                @($finalTestResults | Where-Object { $_.passed -ne $true }).Count -eq 0
            ) "tests=$($finalTestResults.Count); expected=$([int](7 + $expectedTestNames.Count)); failed=$(@($finalTestResults | Where-Object { $_.passed -ne $true }).Count)"

            $description = $finalInspectSession.responses[1].data
            Assert-GateCheck "$($scenario.name) target is a persistent pure library word" (
                $description.status -ceq 'persistent' -and $description.maturity -ceq 'library' -and
                (@($description.inputs) -join ',') -ceq 'Customer,Subscription' -and
                (@($description.outputs) -join ',') -ceq 'Float' -and
                @($description.effects).Count -eq 0
            ) ($description | ConvertTo-Json -Depth 16 -Compress)
            $coverage = $description.coverage
            Assert-GateCheck "$($scenario.name) target own instruction and branch coverage is complete" (
                $coverage.status -ceq 'current' -and
                [int]$coverage.instructionsTotal -gt 0 -and
                [int]$coverage.instructionsCovered -eq [int]$coverage.instructionsTotal -and
                [int]$coverage.branchesTotal -gt 0 -and
                [int]$coverage.branchesCovered -eq [int]$coverage.branchesTotal
            ) "instructions=$($coverage.instructionsCovered)/$($coverage.instructionsTotal); branches=$($coverage.branchesCovered)/$($coverage.branchesTotal); status=$($coverage.status)"
        }

        $verifierEvidencePath = Join-Path $scenarioRootRelative 'solution-verifier-evidence.json'
        $verifierEvidencePath = [IO.Path]::GetFullPath($verifierEvidencePath, $repo)
        $result.rejection = Invoke-ExpectedRejection -Scenario $scenario -ProjectPath $projectPath -VerifierEvidencePath $verifierEvidencePath
    }

    $passed = $true
} catch {
    $failure = $_.Exception.Message
}
finally {
    $evidence = [ordered]@{
        schemaVersion = 1
        passed = $passed
        failure = $failure
        runId = $runId
        startedUtc = $startedUtc
        cli = $cli
        cliSha256 = (Get-FileHash -LiteralPath $cli -Algorithm SHA256).Hash.ToLowerInvariant()
        oracle = [ordered]@{
            path = $oraclePath
            sha256 = (Get-FileHash -LiteralPath $oraclePath -Algorithm SHA256).Hash.ToLowerInvariant()
            word = $oracle.word
            caseCount = $oracle.cases.Count
        }
        restoreScriptSha256 = (Get-FileHash -LiteralPath $restoreScript -Algorithm SHA256).Hash.ToLowerInvariant()
        solutionVerifierSha256 = (Get-FileHash -LiteralPath $solutionVerifier -Algorithm SHA256).Hash.ToLowerInvariant()
        scriptSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
        scratchRoot = $runRoot
        scenarios = $scenarioResults.ToArray()
        sessions = $script:sessions.ToArray()
        checks = $script:checks.ToArray()
        limits = @(
            'Controls are generated only in isolated restored projects; they are not agent trial solutions or fixtures.',
            'The gate checks selected Float behavior from the current acceptance oracle.',
            'No token, latency, agent-efficiency, or production-money claim is made.'
        )
        completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($evidenceTarget))
    [IO.File]::WriteAllText($evidenceTarget, (ConvertTo-Json -InputObject $evidence -Depth 80), [Text.UTF8Encoding]::new($false))
}

if (-not $passed) { throw $failure }
Write-Output "$($script:checks.Count) renewal acceptance regression checks passed. Evidence: $evidenceTarget"
