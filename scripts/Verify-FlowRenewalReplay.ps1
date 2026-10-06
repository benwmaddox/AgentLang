#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CliDll,
    [string]$EvidencePath = '.agentlang/reports/flow-renewal-replay.json'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$startedUtc = [DateTimeOffset]::UtcNow.ToString('O')
$runId = [Guid]::NewGuid().ToString('N')
$runRootRelative = ".agentlang/renewal-acceptance-replay/$runId"
$runRoot = [IO.Path]::GetFullPath($runRootRelative, $repo)
$acceptedSourcePath = Join-Path $repo 'experiments/AgentLang.SubagentTrials/matched-renewal-001/runs/flow-history-control-001/accepted.agent'
$acceptedWordSourcePath = Join-Path $repo 'experiments/AgentLang.SubagentTrials/matched-renewal-001/runs/flow-history-control-001/accepted-word.agent'
$artifactManifestPath = Join-Path $repo 'experiments/AgentLang.SubagentTrials/matched-renewal-001/runs/flow-history-control-001/artifacts.json'
$restoreScript = Join-Path $PSScriptRoot 'Restore-MatchedRenewalSnapshot.ps1'
$solutionVerifier = Join-Path $PSScriptRoot 'Verify-FlowRenewalSolution.ps1'
$oraclePath = Join-Path $repo 'experiments/AgentLang.SubagentTrials/matched-renewal-001/flow-renewal-acceptance.json'
$evidenceTarget = if ([IO.Path]::IsPathRooted($EvidencePath)) {
    [IO.Path]::GetFullPath($EvidencePath)
} else {
    [IO.Path]::GetFullPath($EvidencePath, $repo)
}
$script:checks = [Collections.Generic.List[object]]::new()
$script:sessions = [Collections.Generic.List[object]]::new()
$script:cli = $null
$script:projectPath = $null
$script:acceptedSource = $null
$script:acceptedWordSource = $null
$script:sourceSha256 = $null
$script:acceptedWordSourceSha256 = $null
$script:artifactManifestSha256 = $null
$script:verifierEvidence = $null
$script:verifierException = $null
$passed = $false
$failure = $null
$baselineWords = $null
$replaySession = $null
$expectedSeed = @(
    [ordered]@{ name = 'customer.discounted-balance'; id = 'word_90c3fe0f880848bf84d63dd72b8707bf'; revision = 1 },
    [ordered]@{ name = 'customer.premium?'; id = 'word_6f1915c78d344e4386fedc1ffecc01f0'; revision = 1 }
)

function Assert-ReplayCheck([string]$Name, [bool]$Passed, [string]$Detail = '') {
    $script:checks.Add([ordered]@{ name = $Name; passed = $Passed; detail = $Detail })
    if (-not $Passed) { throw "Flow renewal replay failed: $Name. $Detail" }
}

function Invoke-CliSession {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$ProjectPath,
        [Parameter(Mandatory)][object[]]$Requests
    )

    $lines = @($Requests | ForEach-Object { ConvertTo-Json -InputObject $_ -Depth 32 -Compress })
    $rawResponses = @($lines | & dotnet $script:cli --project $ProjectPath --jsonl)
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
    Assert-ReplayCheck "$Name process exits successfully" ($exitCode -eq 0) "exitCode=$exitCode"
    Assert-ReplayCheck "$Name returns one JSON response per request" ($rawResponses.Count -eq $Requests.Count) "requests=$($Requests.Count); responses=$($rawResponses.Count)"
    Assert-ReplayCheck "$Name response JSON parses" (@($parseErrors | Where-Object { $null -ne $_ }).Count -eq 0) (($parseErrors | Where-Object { $null -ne $_ }) -join '; ')
    Assert-ReplayCheck "$Name requests succeed" (@($responses | Where-Object { $null -eq $_ -or $_.ok -ne $true }).Count -eq 0) (
        (@($responses | Where-Object { $null -eq $_ -or $_.ok -ne $true } | ForEach-Object { $_.text }) -join '; ')
    )
    return $session
}

function Get-AuthoredWords([object]$WordsResponse) {
    @($WordsResponse.data.words | Where-Object {
        ([string]$_.id).StartsWith('word_', [StringComparison]::Ordinal)
    } | Sort-Object name)
}

function Get-TestResults([object]$TestAllResponse) {
    @($TestAllResponse.data.results)
}

try {
    $script:cli = (Resolve-Path -LiteralPath $CliDll).Path
    Assert-ReplayCheck 'CLI artifact exists' (Test-Path -LiteralPath $script:cli -PathType Leaf) $script:cli
    Assert-ReplayCheck 'accepted external source exists' (Test-Path -LiteralPath $acceptedSourcePath -PathType Leaf) $acceptedSourcePath
    Assert-ReplayCheck 'accepted word source exists' (Test-Path -LiteralPath $acceptedWordSourcePath -PathType Leaf) $acceptedWordSourcePath
    Assert-ReplayCheck 'accepted artifact manifest exists' (Test-Path -LiteralPath $artifactManifestPath -PathType Leaf) $artifactManifestPath
    Assert-ReplayCheck 'snapshot restore helper exists' (Test-Path -LiteralPath $restoreScript -PathType Leaf) $restoreScript
    Assert-ReplayCheck 'acceptance verifier exists' (Test-Path -LiteralPath $solutionVerifier -PathType Leaf) $solutionVerifier

    $sourceBytes = [IO.File]::ReadAllBytes($acceptedSourcePath)
    $script:sourceSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($sourceBytes)).ToLowerInvariant()
    $script:acceptedSource = [IO.File]::ReadAllText($acceptedSourcePath, [Text.Encoding]::UTF8)
    Assert-ReplayCheck 'accepted source is non-empty' (-not [string]::IsNullOrWhiteSpace($script:acceptedSource)) "utf8Bytes=$($sourceBytes.Length)"
    $acceptedWordBytes = [IO.File]::ReadAllBytes($acceptedWordSourcePath)
    $script:acceptedWordSourceSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($acceptedWordBytes)).ToLowerInvariant()
    $script:acceptedWordSource = [IO.File]::ReadAllText($acceptedWordSourcePath, [Text.Encoding]::UTF8)
    Assert-ReplayCheck 'accepted word source is non-empty' (-not [string]::IsNullOrWhiteSpace($script:acceptedWordSource)) "utf8Bytes=$($acceptedWordBytes.Length)"

    $artifactManifestBytes = [IO.File]::ReadAllBytes($artifactManifestPath)
    $script:artifactManifestSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($artifactManifestBytes)).ToLowerInvariant()
    $artifactManifest = [IO.File]::ReadAllText($artifactManifestPath, [Text.Encoding]::UTF8) | ConvertFrom-Json -Depth 16
    Assert-ReplayCheck 'accepted artifact manifest uses schema version one' ([int]$artifactManifest.schemaVersion -eq 1) "schemaVersion=$($artifactManifest.schemaVersion)"
    $sourceEntry = @($artifactManifest.files | Where-Object { $_.name -ceq 'accepted.agent' })
    $wordSourceEntry = @($artifactManifest.files | Where-Object { $_.name -ceq 'accepted-word.agent' })
    Assert-ReplayCheck 'accepted artifact manifest has one entry for each source artifact' ($sourceEntry.Count -eq 1 -and $wordSourceEntry.Count -eq 1) "accepted.agent=$($sourceEntry.Count); accepted-word.agent=$($wordSourceEntry.Count)"
    Assert-ReplayCheck 'accepted.agent matches its pinned byte count and SHA-256' (
        $sourceEntry.Count -eq 1 -and [int64]$sourceEntry[0].bytes -eq $sourceBytes.Length -and $sourceEntry[0].sha256 -ceq $script:sourceSha256
    ) "manifestBytes=$(if ($sourceEntry.Count -eq 1) { $sourceEntry[0].bytes } else { 'missing' }); actualBytes=$($sourceBytes.Length); manifestSha=$(if ($sourceEntry.Count -eq 1) { $sourceEntry[0].sha256 } else { 'missing' }); actualSha=$($script:sourceSha256)"
    Assert-ReplayCheck 'accepted-word.agent matches its pinned byte count and SHA-256' (
        $wordSourceEntry.Count -eq 1 -and [int64]$wordSourceEntry[0].bytes -eq $acceptedWordBytes.Length -and $wordSourceEntry[0].sha256 -ceq $script:acceptedWordSourceSha256
    ) "manifestBytes=$(if ($wordSourceEntry.Count -eq 1) { $wordSourceEntry[0].bytes } else { 'missing' }); actualBytes=$($acceptedWordBytes.Length); manifestSha=$(if ($wordSourceEntry.Count -eq 1) { $wordSourceEntry[0].sha256 } else { 'missing' }); actualSha=$($script:acceptedWordSourceSha256)"

    $script:projectPath = [IO.Path]::GetFullPath((Join-Path $runRootRelative 'project'), $repo)
    if (Test-Path -LiteralPath $script:projectPath) { throw "Generated replay project already exists: $script:projectPath" }
    & $restoreScript -Profile growing -ProjectPath (Join-Path $runRootRelative 'project') | Out-Null
    Assert-ReplayCheck 'fresh replay project restores Growing snapshot' (Test-Path -LiteralPath (Join-Path $script:projectPath '.agentlang/store/CURRENT') -PathType Leaf) $script:projectPath

    $baselineSession = Invoke-CliSession -Name 'replay-baseline-words' -ProjectPath $script:projectPath -Requests @(
        [ordered]@{ op = 'words' },
        [ordered]@{ op = 'describe'; word = 'customer.discounted-balance' },
        [ordered]@{ op = 'describe'; word = 'customer.premium?' }
    )
    $baselineWords = @(Get-AuthoredWords $baselineSession.responses[0])
    $baselineIdentityRows = @($baselineWords | ForEach-Object { "$($_.name)|$($_.id)" } | Sort-Object)
    $expectedIdentityRows = @($expectedSeed | ForEach-Object { "$($_.name)|$($_.id)" } | Sort-Object)
    Assert-ReplayCheck 'Growing replay starts with exactly the two pinned seed identities' (
        ($baselineIdentityRows -join "`n") -ceq ($expectedIdentityRows -join "`n")
    ) ($baselineIdentityRows -join '; ')
    for ($index = 0; $index -lt $expectedSeed.Count; $index++) {
        $seed = $expectedSeed[$index]
        $baselineDescription = $baselineSession.responses[$index + 1].data
        Assert-ReplayCheck "baseline seed '$($seed.name)' has its pinned identity and revision one" (
            $baselineDescription.name -ceq $seed.name -and
            $baselineDescription.id -ceq $seed.id -and
            [int]$baselineDescription.revision -eq 1 -and
            $baselineDescription.status -ceq 'persistent' -and
            $baselineDescription.maturity -ceq 'library'
        ) "id=$($baselineDescription.id); revision=$($baselineDescription.revision); status=$($baselineDescription.status)"
    }
    Assert-ReplayCheck 'target renewal word is absent from initial Growing seed' (@($baselineWords | Where-Object name -CEQ 'customer.renewal-balance').Count -eq 0)

    $requests = @(
        [ordered]@{ op = 'task.begin'; goal = 'Replay accepted inherited-history customer renewal-balance definition' },
        [ordered]@{ op = 'define'; frontend = 'flow'; source = $script:acceptedSource },
        [ordered]@{ op = 'test-all' },
        [ordered]@{ op = 'commit'; word = 'customer.renewal-balance'; library = $true },
        [ordered]@{ op = 'task.commit' },
        [ordered]@{ op = 'test-all' },
        [ordered]@{ op = 'describe'; word = 'customer.renewal-balance' },
        [ordered]@{ op = 'source'; word = 'customer.renewal-balance' },
        [ordered]@{ op = 'dependencies'; word = 'customer.renewal-balance' },
        [ordered]@{ op = 'words' },
        [ordered]@{ op = 'describe'; word = 'customer.discounted-balance' },
        [ordered]@{ op = 'describe'; word = 'customer.premium?' }
    )
    $replaySession = Invoke-CliSession -Name 'accepted-source-replay' -ProjectPath $script:projectPath -Requests $requests
    $definedWordNames = @($replaySession.responses[1].data | ForEach-Object { [string]$_.name })
    Assert-ReplayCheck 'define stages only the renewal target word' (
        $definedWordNames.Count -eq 1 -and $definedWordNames[0] -ceq 'customer.renewal-balance'
    ) ($definedWordNames -join ',')

    $preCommitResults = @(Get-TestResults $replaySession.responses[2])
    Assert-ReplayCheck 'pre-commit test-all runs all fifteen baseline and target tests' ($preCommitResults.Count -eq 15) "results=$($preCommitResults.Count)"
    Assert-ReplayCheck 'all fifteen pre-commit tests pass' (@($preCommitResults | Where-Object { $_.passed -ne $true }).Count -eq 0) (
        (@($preCommitResults | Where-Object { $_.passed -ne $true } | ForEach-Object { "$($_.word)/$($_.name)" }) -join ',')
    )
    $preCommitTargetTests = @($preCommitResults | Where-Object { $_.word -ceq 'customer.renewal-balance' })
    Assert-ReplayCheck 'target contributes exactly eight passing tests before commit' (
        $preCommitTargetTests.Count -eq 8 -and @($preCommitTargetTests | Where-Object { $_.passed -ne $true }).Count -eq 0
    ) "targetTests=$($preCommitTargetTests.Count)"

    $postCommitResults = @(Get-TestResults $replaySession.responses[5])
    Assert-ReplayCheck 'post-commit test-all runs all fifteen baseline and target tests' ($postCommitResults.Count -eq 15) "results=$($postCommitResults.Count)"
    Assert-ReplayCheck 'all fifteen post-commit tests pass' (@($postCommitResults | Where-Object { $_.passed -ne $true }).Count -eq 0) (
        (@($postCommitResults | Where-Object { $_.passed -ne $true } | ForEach-Object { "$($_.word)/$($_.name)" }) -join ',')
    )
    $targetTests = @($postCommitResults | Where-Object { $_.word -ceq 'customer.renewal-balance' })
    Assert-ReplayCheck 'committed target retains exactly eight passing tests' (
        $targetTests.Count -eq 8 -and @($targetTests | Where-Object { $_.passed -ne $true }).Count -eq 0
    ) "targetTests=$($targetTests.Count)"

    $description = $replaySession.responses[6].data
    Assert-ReplayCheck 'target has the exact Customer Subscription to Float signature' (
        $description.name -ceq 'customer.renewal-balance' -and
        (@($description.inputs) -join ',') -ceq 'Customer,Subscription' -and
        (@($description.outputs) -join ',') -ceq 'Float'
    ) ($description | ConvertTo-Json -Depth 16 -Compress)
    Assert-ReplayCheck 'target is persistent, library maturity, revision one, and pure' (
        $description.status -ceq 'persistent' -and $description.maturity -ceq 'library' -and
        [int]$description.revision -eq 1 -and @($description.effects).Count -eq 0
    ) "status=$($description.status); maturity=$($description.maturity); revision=$($description.revision); effects=$(@($description.effects).Count)"
    Assert-ReplayCheck 'target metadata lists exactly eight attached tests' (@($description.tests).Count -eq 8) "tests=$(@($description.tests).Count)"
    Assert-ReplayCheck 'target depends on retained customer.discounted-balance' (@($description.dependencies) -contains 'customer.discounted-balance') (@($description.dependencies) -join ',')
    $coverage = $description.coverage
    Assert-ReplayCheck 'target reports current complete instruction and branch coverage' (
        $coverage.status -ceq 'current' -and
        [int]$coverage.instructionsTotal -gt 0 -and
        [int]$coverage.instructionsCovered -eq [int]$coverage.instructionsTotal -and
        [int]$coverage.branchesTotal -gt 0 -and
        [int]$coverage.branchesCovered -eq [int]$coverage.branchesTotal
    ) "instructions=$($coverage.instructionsCovered)/$($coverage.instructionsTotal); branches=$($coverage.branchesCovered)/$($coverage.branchesTotal); status=$($coverage.status)"

    Assert-ReplayCheck 'source inspection returns the exact accepted word source' ([string]$replaySession.responses[7].data -ceq $script:acceptedWordSource) (
        "acceptedWordSha256=$script:acceptedWordSourceSha256; inspectedUtf8Bytes=$([Text.Encoding]::UTF8.GetByteCount([string]$replaySession.responses[7].data))"
    )
    $dependencyPayload = $replaySession.responses[8].data
    Assert-ReplayCheck 'dependencies command lists retained customer.discounted-balance' (@($dependencyPayload.dependencies) -contains 'customer.discounted-balance') (@($dependencyPayload.dependencies) -join ',')

    $finalWords = @(Get-AuthoredWords $replaySession.responses[9])
    $expectedFinalNames = @('customer.discounted-balance', 'customer.premium?', 'customer.renewal-balance')
    $actualFinalNames = @($finalWords | ForEach-Object name | Sort-Object)
    Assert-ReplayCheck 'exactly one user word was added to the Growing vocabulary' (($actualFinalNames -join ',') -ceq ($expectedFinalNames -join ',')) ($actualFinalNames -join ',')
    for ($seedIndex = 0; $seedIndex -lt $expectedSeed.Count; $seedIndex++) {
        $seed = $expectedSeed[$seedIndex]
        $retained = @($finalWords | Where-Object name -CEQ $seed.name | Select-Object -First 1)
        $finalDescription = $replaySession.responses[$seedIndex + 10].data
        Assert-ReplayCheck "seed word '$($seed.name)' retains its exact identity and revision" (
            $retained.Count -eq 1 -and $retained[0].id -ceq $seed.id -and
            $finalDescription.id -ceq $seed.id -and [int]$finalDescription.revision -eq 1 -and
            $finalDescription.status -ceq 'persistent' -and $finalDescription.maturity -ceq 'library'
        ) "expectedId=$($seed.id); observedWordId=$(if ($retained.Count -eq 1) { $retained[0].id } else { 'missing' }); describedId=$($finalDescription.id); revision=$($finalDescription.revision)"
    }

    $verifierEvidencePath = [IO.Path]::GetFullPath((Join-Path $runRootRelative 'solution-verifier-evidence.json'), $repo)
    try {
        & $solutionVerifier -CliDll $script:cli -ProjectPath $script:projectPath -EvidencePath $verifierEvidencePath | Out-Null
    } catch {
        $script:verifierException = $_.Exception.Message
    }
    Assert-ReplayCheck 'independent acceptance verifier completes without error' ($null -eq $script:verifierException) ([string]$script:verifierException)
    Assert-ReplayCheck 'independent verifier evidence file exists' (Test-Path -LiteralPath $verifierEvidencePath -PathType Leaf) $verifierEvidencePath
    $script:verifierEvidence = Get-Content -LiteralPath $verifierEvidencePath -Raw | ConvertFrom-Json -Depth 80
    Assert-ReplayCheck 'independent verifier passes all forty-eight acceptance checks' (
        $script:verifierEvidence.passed -eq $true -and
        $script:verifierEvidence.checks.Count -eq 48 -and
        @($script:verifierEvidence.checks | Where-Object { $_.passed -ne $true }).Count -eq 0
    ) "passed=$($script:verifierEvidence.passed); checks=$($script:verifierEvidence.checks.Count); failure=$($script:verifierEvidence.failure)"
    $passed = $true
} catch {
    $failure = $_.Exception.Message
}
finally {
    $acceptedSourceHash = $null
    $acceptedSourceBytes = $null
    if (Test-Path -LiteralPath $acceptedSourcePath -PathType Leaf) {
        $acceptedSourceHash = (Get-FileHash -LiteralPath $acceptedSourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
        $acceptedSourceBytes = (Get-Item -LiteralPath $acceptedSourcePath).Length
    }
    $evidence = [ordered]@{
        schemaVersion = 1
        passed = $passed
        failure = $failure
        runId = $runId
        startedUtc = $startedUtc
        completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        cliPath = $script:cli
        cliSha256 = if ($null -ne $script:cli -and (Test-Path -LiteralPath $script:cli -PathType Leaf)) { (Get-FileHash -LiteralPath $script:cli -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
        restoreScriptSha256 = if (Test-Path -LiteralPath $restoreScript -PathType Leaf) { (Get-FileHash -LiteralPath $restoreScript -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
        verifierScriptSha256 = if (Test-Path -LiteralPath $solutionVerifier -PathType Leaf) { (Get-FileHash -LiteralPath $solutionVerifier -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
        replayScriptSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
        acceptedSource = [ordered]@{
            path = $acceptedSourcePath
            sha256 = $acceptedSourceHash
            utf8Bytes = $acceptedSourceBytes
            sourceSha256AtLoad = $script:sourceSha256
        }
        acceptedWordSource = [ordered]@{
            path = $acceptedWordSourcePath
            sha256AtLoad = $script:acceptedWordSourceSha256
            artifactManifestSha256 = $script:artifactManifestSha256
        }
        artifactManifest = [ordered]@{
            path = $artifactManifestPath
            sha256 = $script:artifactManifestSha256
            entries = if (Test-Path -LiteralPath $artifactManifestPath -PathType Leaf) { (Get-Content -LiteralPath $artifactManifestPath -Raw | ConvertFrom-Json -Depth 16).files } else { @() }
        }
        profile = 'growing'
        projectPath = $script:projectPath
        expectedSeedWords = $expectedSeed
        initialWords = $baselineWords
        replayRequests = if ($null -ne $replaySession) { $replaySession.requests } else { @() }
        replayResponses = if ($null -ne $replaySession) { $replaySession.responses } else { @() }
        sessions = $script:sessions.ToArray()
        checks = $script:checks.ToArray()
        verifierException = $script:verifierException
        verifierEvidence = $script:verifierEvidence
        limits = @(
            'This is deterministic replay of the accepted inherited-history external-agent source, not a new agent run.',
            'It makes no token, latency, or comparative-efficiency claim.',
            'The acceptance oracle covers selected Float behavior, not bitwise Float semantics or production Money.'
        )
    }
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($evidenceTarget))
    [IO.File]::WriteAllText($evidenceTarget, (ConvertTo-Json -InputObject $evidence -Depth 90), [Text.UTF8Encoding]::new($false))
}

if (-not $passed) { throw $failure }
Write-Output "$($script:checks.Count) Flow renewal replay checks passed. Evidence: $evidenceTarget"
