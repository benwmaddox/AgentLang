#requires -Version 7.0

function New-CurrentMatchedRenewalFixture {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CliDll,
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][ValidateSet('flat', 'growing')][string]$Profile,
        [Parameter(Mandatory)][string]$ProjectPath,
        [Parameter(Mandatory)][string]$SnapshotName
    )

    Set-StrictMode -Version Latest
    $ErrorActionPreference = 'Stop'
    $repo = [IO.Path]::GetFullPath($RepoRoot)
    $cli = (Resolve-Path -LiteralPath $CliDll).Path
    $project = [IO.Path]::GetFullPath($ProjectPath, $repo)
    if (Test-Path -LiteralPath $project) {
        throw "Current matched-renewal fixture requires a new project directory: $project"
    }

    $bundlePath = Join-Path $repo "experiments/AgentLang.SubagentTrials/matched-renewal-001/snapshots/$Profile/bundle.json"
    $sourcePath = Join-Path $repo "experiments/AgentLang.SubagentTrials/matched-renewal-001/fixtures/flow/$Profile.agent"
    if (-not (Test-Path -LiteralPath $bundlePath -PathType Leaf)) { throw "Frozen $Profile bundle metadata is missing: $bundlePath" }
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw "Authoritative Flow $Profile source is missing: $sourcePath" }

    $bundle = Get-Content -LiteralPath $bundlePath -Raw | ConvertFrom-Json -Depth 16
    $sourceBytes = [IO.File]::ReadAllBytes($sourcePath)
    $sourceSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($sourceBytes)).ToLowerInvariant()
    if ($bundle.profile -cne $Profile -or $bundle.frontend -cne 'flow' -or $sourceSha256 -cne [string]$bundle.seedSourceSha256) {
        throw "Authoritative Flow $Profile source does not match the frozen bundle seedSourceSha256. expected=$($bundle.seedSourceSha256); actual=$sourceSha256"
    }
    $source = [IO.File]::ReadAllText($sourcePath, [Text.Encoding]::UTF8)

    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($project))
    [void][IO.Directory]::CreateDirectory($project)

    $checks = [Collections.Generic.List[object]]::new()
    $traces = [Collections.Generic.List[object]]::new()
    $passed = $false
    $failure = $null
    $seed = $null
    $reload = $null
    $manifestHash = $null
    $savedSnapshotManifestHash = $null
    $seedCurrentManifestHash = $null
    $snapshotSaveResponse = $null
    $snapshotLoadResponse = $null
    $stagedWords = @()
    $loadedWords = @()
    $seedTestResults = @()
    $reloadedTestResults = @()
    $customerSource = $null
    $subscriptionSource = $null
    $descriptions = @()

    $assertCheck = {
        param([string]$Name, [bool]$Okay, [string]$Detail = '')
        $checks.Add([ordered]@{ name = $Name; passed = $Okay; detail = $Detail })
        if (-not $Okay) { throw "Current fixture check failed: $Name. $Detail" }
    }

    function Invoke-CurrentFixtureSession {
        param(
            [Parameter(Mandatory)][string]$Name,
            [Parameter(Mandatory)][object[]]$Requests
        )

        foreach ($request in $Requests) {
            if ($request.op -in @('define', 'eval')) { $request.frontend = 'flow' }
        }
        $lines = @($Requests | ForEach-Object { ConvertTo-Json -InputObject $_ -Depth 24 -Compress })
        $rawResponses = @($lines | & dotnet $cli --project $project --filesystem virtual --jsonl)
        $exitCode = $LASTEXITCODE
        & $assertCheck "$Name process exits successfully" ($exitCode -eq 0) "exitCode=$exitCode"
        & $assertCheck "$Name returns one response per request" ($rawResponses.Count -eq $Requests.Count) "requests=$($Requests.Count); responses=$($rawResponses.Count)"
        $responses = [Collections.Generic.List[object]]::new()
        for ($index = 0; $index -lt $rawResponses.Count; $index++) {
            try {
                $responses.Add((ConvertFrom-Json -InputObject $rawResponses[$index] -Depth 64))
            } catch {
                throw "$Name response $index is not JSON: $($_.Exception.Message)"
            }
        }
        & $assertCheck "$Name requests succeed" (@($responses | Where-Object { $_.ok -ne $true }).Count -eq 0) (
            (@($responses | Where-Object { $_.ok -ne $true } | ForEach-Object { $_.text }) -join '; ')
        )
        $session = [ordered]@{
            name = $Name
            projectPath = $project
            processExitCode = $exitCode
            requests = $Requests
            rawResponses = $rawResponses
            responses = $responses.ToArray()
        }
        $traces.Add($session)
        return $session
    }

    try {
        $expectedWordNames = if ($Profile -ceq 'growing') {
            @('customer.discounted-balance', 'customer.premium?')
        } else { @() }
        $expectedTestNames = if ($Profile -ceq 'growing') {
            @(
                'customer.discounted-balance/case-sensitive',
                'customer.discounted-balance/premium',
                'customer.discounted-balance/premium-larger-balance',
                'customer.discounted-balance/standard',
                'customer.premium?/case-sensitive',
                'customer.premium?/premium',
                'customer.premium?/standard'
            )
        } else { @() }

        & $assertCheck 'seed contains no renewal solution' (
            $source -notmatch 'customer\.renewal-balance|subscription\.renewable\?|renewal-balance'
        ) $sourceSha256

        $seedRequests = [Collections.Generic.List[object]]::new()
        $seedRequests.Add([ordered]@{ op = 'define'; frontend = 'flow'; source = $source })
        $seedRequests.Add([ordered]@{ op = 'words' })
        $seedRequests.Add([ordered]@{ op = 'source'; type = 'Customer' })
        $seedRequests.Add([ordered]@{ op = 'source'; type = 'Subscription' })
        $seedRequests.Add([ordered]@{ op = 'test-all' })
        $seedRequests.Add([ordered]@{ op = 'commit'; word = 'Customer' })
        $seedRequests.Add([ordered]@{ op = 'commit'; word = 'Subscription' })
        if ($Profile -ceq 'growing') {
            $seedRequests.Add([ordered]@{ op = 'commit'; word = 'customer.premium?'; library = $true })
            $seedRequests.Add([ordered]@{ op = 'commit'; word = 'customer.discounted-balance'; library = $true })
        }
        $seedRequests.Add([ordered]@{ op = 'snapshot.save'; name = $SnapshotName })
        $seed = Invoke-CurrentFixtureSession -Name "$Profile-seed" -Requests @($seedRequests.ToArray())

        $define = $seed.responses[0]
        $stagedWords = @($seed.responses[1].data.words | Where-Object {
            ([string]$_.id).StartsWith('word_', [StringComparison]::Ordinal)
        } | Sort-Object name)
        $customerSource = [string]$seed.responses[2].data
        $subscriptionSource = [string]$seed.responses[3].data
        $seedTestResults = @($seed.responses[4].data.results)
        $snapshotSaveResponse = $seed.responses[$seed.responses.Count - 1]
        $stagedMetadata = @($define.data.words | Sort-Object name)
        $seedTestNames = @($seedTestResults | ForEach-Object { "$($_.word)/$($_.name)" } | Sort-Object)
        $expectedTestNamesSorted = @($expectedTestNames | Sort-Object)
        $snapshotMetadataPath = Join-Path $project ".agentlang/store/snapshots/$SnapshotName.json"
        $currentPointerPath = Join-Path $project '.agentlang/store/CURRENT'
        $savedSnapshotMetadata = Get-Content -LiteralPath $snapshotMetadataPath -Raw | ConvertFrom-Json -Depth 16
        $seedCurrentPointer = Get-Content -LiteralPath $currentPointerPath -Raw | ConvertFrom-Json -Depth 16
        $savedSnapshotManifestHash = [string]$savedSnapshotMetadata.manifestHash
        $seedCurrentManifestHash = [string]$seedCurrentPointer.manifestHash

        & $assertCheck "$Profile declares exactly Customer and Subscription" (
            (@($define.data.types | Sort-Object) -join ',') -ceq 'Customer,Subscription'
        ) (@($define.data.types | Sort-Object) -join ',')
        & $assertCheck "$Profile snapshot.save records the requested name and excludes no candidates" (
            [string]$snapshotSaveResponse.data.name -ceq $SnapshotName -and
            @($snapshotSaveResponse.data.excludedCandidates).Count -eq 0
        ) ($snapshotSaveResponse | ConvertTo-Json -Depth 12 -Compress)
        & $assertCheck "$Profile save-time snapshot metadata matches the committed CURRENT manifest" (
            $savedSnapshotManifestHash -match '^[0-9a-f]{64}$' -and
            $seedCurrentManifestHash -ceq $savedSnapshotManifestHash
        ) "snapshot=$savedSnapshotManifestHash; CURRENT=$seedCurrentManifestHash"
        & $assertCheck "$Profile Customer record has kind:String and balance:Float" (
            $customerSource -match '(?s)record Customer\s*\{\s*field kind:\s*String;\s*field balance:\s*Float;\s*\}'
        ) $customerSource
        & $assertCheck "$Profile Subscription record has term:String and renewable:Bool" (
            $subscriptionSource -match '(?s)record Subscription\s*\{\s*field term:\s*String;\s*field renewable:\s*Bool;\s*\}'
        ) $subscriptionSource
        & $assertCheck "$Profile seed has exactly the expected authored words" (
            (@($stagedWords | ForEach-Object name) -join ',') -ceq ($expectedWordNames -join ',') -and
            (@($stagedMetadata | ForEach-Object name) -join ',') -ceq ($expectedWordNames -join ',')
        ) (@($stagedMetadata | ForEach-Object name) -join ',')
        & $assertCheck "$Profile generated word IDs are valid and unique" (
            @($stagedMetadata | Where-Object { ([string]$_.id) -notmatch '^word_[0-9a-f]{32}$' }).Count -eq 0 -and
            @($stagedMetadata | ForEach-Object id | Sort-Object -Unique).Count -eq $stagedMetadata.Count
        ) (@($stagedMetadata | ForEach-Object { "$($_.name)|$($_.id)" }) -join '; ')
        & $assertCheck "$Profile seed has the expected existing test names and passing results" (
            $seedTestResults.Count -eq $expectedTestNamesSorted.Count -and
            ($seedTestNames -join ',') -ceq ($expectedTestNamesSorted -join ',') -and
            @($seedTestResults | Where-Object { $_.passed -ne $true }).Count -eq 0
        ) (@($seedTestResults | ForEach-Object { "$($_.word)/$($_.name):$($_.passed)" } | Sort-Object) -join ', ')

        $reloadRequests = @(
            [ordered]@{ op = 'snapshot.load'; name = $SnapshotName },
            [ordered]@{ op = 'words' },
            [ordered]@{ op = 'test-all' },
            [ordered]@{ op = 'source'; type = 'Customer' },
            [ordered]@{ op = 'source'; type = 'Subscription' }
        )
        if ($Profile -ceq 'growing') {
            $reloadRequests += @(
                [ordered]@{ op = 'describe'; word = 'customer.premium?' },
                [ordered]@{ op = 'describe'; word = 'customer.discounted-balance' }
            )
        }
        $reload = Invoke-CurrentFixtureSession -Name "$Profile-fresh-process-reload" -Requests $reloadRequests
        $load = $reload.responses[0]
        $snapshotLoadResponse = $load
        $loadedWords = @($reload.responses[1].data.words | Where-Object {
            ([string]$_.id).StartsWith('word_', [StringComparison]::Ordinal)
        } | Sort-Object name)
        $reloadedTestResults = @($reload.responses[2].data.results)
        $reloadedTestNames = @($reloadedTestResults | ForEach-Object { "$($_.word)/$($_.name)" } | Sort-Object)
        $manifestHash = [string]$load.data.manifestHash
        $loadedIdentityRows = @($loadedWords | ForEach-Object { "$($_.name)|$($_.id)|$($_.maturity)|$($_.status)" }) -join "`n"
        $expectedIdentityRows = @($stagedMetadata | ForEach-Object { "$($_.name)|$($_.id)|library|persistent" }) -join "`n"
        $manifestPath = Join-Path $project ".agentlang/store/manifests/$manifestHash.json"

        & $assertCheck "$Profile load reports a current manifest hash" ($manifestHash -match '^[0-9a-f]{64}$') $manifestHash
        & $assertCheck "$Profile snapshot.load returns the manifest captured at save time" (
            [string]$load.data.name -ceq $SnapshotName -and
            $manifestHash -ceq $savedSnapshotManifestHash -and
            $manifestHash -ceq $seedCurrentManifestHash
        ) "snapshot=$([string]$load.data.name); save=$savedSnapshotManifestHash; CURRENT=$seedCurrentManifestHash; load=$manifestHash"
        & $assertCheck "$Profile saved manifest exists and matches the returned hash" (
            (Test-Path -LiteralPath $manifestPath -PathType Leaf) -and
            (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $manifestHash
        ) $manifestPath
        & $assertCheck "$Profile generated word IDs survive snapshot reload" (
            $loadedIdentityRows -ceq $expectedIdentityRows
        ) $loadedIdentityRows
        & $assertCheck "$Profile snapshot reload retains the expected test names and passing results" (
            $reloadedTestResults.Count -eq $expectedTestNamesSorted.Count -and
            ($reloadedTestNames -join ',') -ceq ($expectedTestNamesSorted -join ',') -and
            @($reloadedTestResults | Where-Object { $_.passed -ne $true }).Count -eq 0
        ) (@($reloadedTestResults | ForEach-Object { "$($_.word)/$($_.name):$($_.passed)" } | Sort-Object) -join ', ')
        & $assertCheck "$Profile snapshot reload has no renewal-balance word" (
            @($reload.responses[1].data.words | Where-Object { $_.name -ceq 'customer.renewal-balance' }).Count -eq 0
        ) (@($loadedWords | ForEach-Object name) -join ',')
        & $assertCheck "$Profile reloaded record sources retain their declared fields" (
            ([string]$reload.responses[3].data) -match '(?s)record Customer\s*\{\s*field kind:\s*String;\s*field balance:\s*Float;\s*\}' -and
            ([string]$reload.responses[4].data) -match '(?s)record Subscription\s*\{\s*field term:\s*String;\s*field renewable:\s*Bool;\s*\}'
        ) "Customer=$($reload.responses[3].data); Subscription=$($reload.responses[4].data)"

        if ($Profile -ceq 'growing') {
            $premium = $reload.responses[5].data
            $discountedBalance = $reload.responses[6].data
            $descriptions = @($premium, $discountedBalance)
            & $assertCheck 'customer.premium? retains its Customer -> Bool signature' (
                $premium.name -ceq 'customer.premium?' -and
                (@($premium.inputs) -join ',') -ceq 'Customer' -and
                (@($premium.outputs) -join ',') -ceq 'Bool'
            ) ($premium | ConvertTo-Json -Depth 12 -Compress)
            & $assertCheck 'customer.discounted-balance retains its Customer -> Float signature' (
                $discountedBalance.name -ceq 'customer.discounted-balance' -and
                (@($discountedBalance.inputs) -join ',') -ceq 'Customer' -and
                (@($discountedBalance.outputs) -join ',') -ceq 'Float'
            ) ($discountedBalance | ConvertTo-Json -Depth 12 -Compress)
            & $assertCheck 'Growing snapshot reload retains exactly two persistent library words' (
                $loadedWords.Count -eq 2 -and
                @($loadedWords | Where-Object { $_.status -cne 'persistent' -or $_.maturity -cne 'library' }).Count -eq 0
            ) ($loadedIdentityRows -replace "`n", '; ')
        }

        $passed = $true
    } catch {
        $failure = $_.Exception.Message
    } finally {
        $evidence = [ordered]@{
            schemaVersion = 1
            passed = $passed
            failure = $failure
            profile = $Profile
            projectPath = $project
            cliPath = $cli
            cliSha256 = (Get-FileHash -LiteralPath $cli -Algorithm SHA256).Hash.ToLowerInvariant()
            sourcePath = $sourcePath
            sourceSha256 = $sourceSha256
            expectedSourceSha256 = [string]$bundle.seedSourceSha256
            snapshotName = $SnapshotName
            currentManifestHash = $manifestHash
            savedSnapshotManifestHash = $savedSnapshotManifestHash
            seedCurrentManifestHash = $seedCurrentManifestHash
            snapshotSaveResponse = $snapshotSaveResponse
            snapshotLoadResponse = $snapshotLoadResponse
            generatedWords = @($stagedWords | ForEach-Object { [ordered]@{ name = $_.name; id = $_.id; maturity = $_.maturity; status = $_.status } })
            reloadedWords = @($loadedWords | ForEach-Object { [ordered]@{ name = $_.name; id = $_.id; maturity = $_.maturity; status = $_.status } })
            seedTestResults = $seedTestResults
            reloadedTestResults = $reloadedTestResults
            descriptions = $descriptions
            checks = $checks.ToArray()
            traces = $traces.ToArray()
        }
        $evidencePath = Join-Path $project 'current-fixture-evidence.json'
        [IO.File]::WriteAllText($evidencePath, (ConvertTo-Json -InputObject $evidence -Depth 80), [Text.UTF8Encoding]::new($false))
    }

    if (-not $passed) { throw $failure }
    return [pscustomobject]$evidence
}
