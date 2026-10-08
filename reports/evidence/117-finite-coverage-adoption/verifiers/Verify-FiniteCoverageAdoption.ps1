#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('preflight','score')][string]$Mode = 'preflight',
    [string]$CliDll = '.agentlang/finite-adoption-001/runtime/debug-artifacts/bin/AgentLang.Cli/debug/AgentLang.Cli.dll',
    [string]$SeedProject = '.agentlang/finite-adoption-001/seed/project',
    [string]$ActorProject,
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$studyRoot = Split-Path $PSScriptRoot -Parent
$repo = Split-Path (Split-Path $studyRoot -Parent) -Parent
function Resolve-RepoPath([string]$Path) {
    if ([IO.Path]::IsPathFullyQualified($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath($Path,$repo)
}
$script:cli = Resolve-RepoPath $CliDll
$script:seed = Resolve-RepoPath $SeedProject
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = if ($Mode -eq 'preflight') { '.agentlang/finite-adoption-001/oracle-preflight' } else { '.agentlang/finite-adoption-001/scoring' }
}
$output = Resolve-RepoPath $OutputDirectory
if ($Mode -eq 'score' -and [string]::IsNullOrWhiteSpace($ActorProject)) { throw 'Score mode requires -ActorProject after dispatch has completed.' }
if (-not (Test-Path -LiteralPath $script:cli -PathType Leaf)) { throw "Pinned CLI does not exist: $script:cli" }
if (-not (Test-Path -LiteralPath $script:seed -PathType Container)) { throw "Seed project does not exist: $script:seed" }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$script:checks = [Collections.Generic.List[object]]::new()
$script:invocations = [Collections.Generic.List[object]]::new()
$script:utf8 = [Text.UTF8Encoding]::new($false)

function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Get-TreeInventory([string]$Root) {
    $rows = [Collections.Generic.List[object]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $Root -Recurse -Force -File | Sort-Object FullName) {
        $relative = [IO.Path]::GetRelativePath($Root,$file.FullName).Replace('\','/')
        if ($relative -match '(^|/)(bin|obj)(/|$)') { continue }
        $rows.Add([ordered]@{ path=$relative; bytes=[long]$file.Length; sha256=(Get-Sha256 $file.FullName) })
    }
    return @($rows)
}
function Get-TreeHash([string]$Root) {
    $json = ConvertTo-Json -InputObject @(Get-TreeInventory $Root) -Depth 30 -Compress
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($script:utf8.GetBytes($json))).ToLowerInvariant()
}
function Copy-Project([string]$Source,[string]$Destination) {
    if (Test-Path -LiteralPath $Destination) { throw "Refusing to overwrite an existing control copy: $Destination" }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse -Force
    }
}
function Add-Check([string]$Name,[bool]$Passed,$Details=$null) {
    $row = [ordered]@{ name=$Name; passed=$Passed }
    if ($null -ne $Details) { $row.details = $Details }
    $script:checks.Add($row)
}
function Invoke-Language([string]$Project,[string]$Label,[object[]]$Requests) {
    $stderr = Join-Path $output ($Label + '.stderr.log')
    $requestPath = Join-Path $output ($Label + '.requests.jsonl')
    $responsePath = Join-Path $output ($Label + '.responses.jsonl')
    $lines = @($Requests | ForEach-Object { ConvertTo-Json -InputObject $_ -Depth 80 -Compress })
    [IO.File]::WriteAllLines($requestPath,$lines,$script:utf8)
    $raw = @($lines | & dotnet $script:cli --project $Project --jsonl 2> $stderr)
    $exitCode = $LASTEXITCODE
    [IO.File]::WriteAllLines($responsePath,$raw,$script:utf8)
    $responses = @($raw | ForEach-Object { ConvertFrom-Json -InputObject $_ -Depth 100 })
    $entry = [ordered]@{ label=$Label; project=[IO.Path]::GetFullPath($Project); requestCount=$Requests.Count; responseCount=$responses.Count; exitCode=$exitCode; requests=$Requests; responses=$responses }
    $script:invocations.Add($entry)
    Add-Check "$Label CLI exits successfully" ($exitCode -eq 0) @{ exitCode=$exitCode; stderrPath=$stderr }
    Add-Check "$Label returns one JSON response per request" ($responses.Count -eq $Requests.Count) @{ requests=$Requests.Count; responses=$responses.Count }
    return ,$responses
}
function Get-ObjectValue($Object,[string]$Name,$Default=$null) {
    if ($null -eq $Object) { return $Default }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $Default }
    return $property.Value
}
function Get-TestSourceSnapshot([string]$Project) {
    $store = Join-Path $Project '.agentlang/store'
    $currentPath = Join-Path $store 'CURRENT'
    if (-not (Test-Path -LiteralPath $currentPath -PathType Leaf)) { throw "No project CURRENT manifest in $Project" }
    $current = Get-Content -LiteralPath $currentPath -Raw | ConvertFrom-Json -Depth 40
    $manifestPath = Join-Path (Join-Path $store 'manifests') ([string]$current.manifestHash + '.json')
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 100
    $rows = [Collections.Generic.List[object]]::new()
    $currentRevisions = @{}
    foreach ($word in $manifest.words) { $currentRevisions[[string]$word.currentName] = [int]$word.currentRevision }
    foreach ($revision in $manifest.revisions) {
        if (-not $currentRevisions.ContainsKey([string]$revision.name) -or $currentRevisions[[string]$revision.name] -ne [int]$revision.revision) { continue }
        foreach ($test in $revision.tests) {
            $objectPath = Join-Path (Join-Path $store 'objects') ([string]$test.hash + '.agent')
            $source = Get-Content -LiteralPath $objectPath -Raw
            $match = [regex]::Match($source,'(?m)^test\s+(?<owner>[A-Za-z0-9_.?/-]+)/(?<case>[A-Za-z0-9_.-]+)\s*\{')
            $rows.Add([ordered]@{
                word=[string]$revision.name
                revision=[int]$revision.revision
                caseName=if ($match.Success) { $match.Groups['case'].Value } else { $null }
                sourceHash=[string]$test.hash
                source=$source
                sourceSha256=(Get-Sha256 $objectPath)
            })
        }
    }
    return @($rows | Sort-Object word,caseName)
}
function New-Request([string]$Operation,[Collections.IDictionary]$Fields=@{}) {
    $request = [ordered]@{ op=$Operation }
    foreach ($key in $Fields.Keys) { $request[$key] = $Fields[$key] }
    return $request
}
function Test-Ok($Response) { (Get-ObjectValue $Response 'ok' $false) -eq $true }
function Get-ErrorCode($Response) { [string](Get-ObjectValue (Get-ObjectValue $Response 'error') 'code' '') }
function Test-OptionResult($Response,[string]$ExpectedCase,[string]$ExpectedInteger) {
    if (-not (Test-Ok $Response)) { return $false }
    $values = Get-ObjectValue (Get-ObjectValue (Get-ObjectValue $Response 'data') 'structuredStack') 'values' @()
    if (@($values).Count -ne 1) { return $false }
    $value = $values[0]
    if ([string]$value.kind -cne 'option' -or [string]$value.case -cne $ExpectedCase) { return $false }
    if ($ExpectedCase -ceq 'none') { return $true }
    return ([string]$value.value.kind -ceq 'int' -and [string]$value.value.value -ceq $ExpectedInteger)
}
function Test-BoolResult($Response,[bool]$Expected) {
    if (-not (Test-Ok $Response)) { return $false }
    $values = Get-ObjectValue (Get-ObjectValue (Get-ObjectValue $Response 'data') 'structuredStack') 'values' @()
    return (@($values).Count -eq 1 -and [string]$values[0].kind -ceq 'bool' -and [bool]$values[0].value -eq $Expected)
}
function Get-FlagsTruthRows($Sources) {
    $rows = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $pattern = 'flags::agree\s*\(\s*left\s*=\s*(?<left>true|false)\s*,\s*right\s*=\s*(?<right>true|false)\s*\)'
    foreach ($item in $Sources | Where-Object { $_.word -ceq 'flags.agree' }) {
        foreach ($regexMatch in [regex]::Matches([string]$item.source,$pattern)) {
            [void]$rows.Add(($regexMatch.Groups['left'].Value + ',' + $regexMatch.Groups['right'].Value))
        }
    }
    return @($rows | Sort-Object)
}
function Get-SelectionTestValues($Sources) {
    $noneValues = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $someValues = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $patterns = @(
        'selection::accept\s*\(\s*selection::new\s*\(\s*first\s*=\s*option::none<Int>\s*\(\s*\)\s*\)\s*,\s*(?<item>-?[0-9]+)\s*\)',
        'selection::accept\s*\(\s*selection::new\s*\(\s*first\s*=\s*option::some<Int>\s*\(\s*(?<existing>-?[0-9]+)\s*\)\s*\)\s*,\s*(?<item>-?[0-9]+)\s*\)'
    )
    foreach ($item in $Sources | Where-Object { $_.word -ceq 'selection.accept' }) {
        foreach ($regexMatch in [regex]::Matches([string]$item.source,$patterns[0])) { [void]$noneValues.Add($regexMatch.Groups['item'].Value) }
        foreach ($regexMatch in [regex]::Matches([string]$item.source,$patterns[1])) { [void]$someValues.Add($regexMatch.Groups['existing'].Value) }
    }
    return [ordered]@{ noneInputItems=@($noneValues | Sort-Object); someExistingValues=@($someValues | Sort-Object) }
}
function Get-BaseChecks([string]$Project,[string]$Label) {
    $sources = Get-TestSourceSnapshot $Project
    $flags = @($sources | Where-Object { $_.word -ceq 'flags.agree' })
    $selection = @($sources | Where-Object { $_.word -ceq 'selection.accept' })
    return [ordered]@{
        sourceSnapshots=$sources
        flagsTestSources=$flags
        selectionTestSources=$selection
        flagsRows=@(Get-FlagsTruthRows $sources)
        selectionTestValues=Get-SelectionTestValues $sources
    }
}
function Get-MutantSource([string]$Name) {
    $path = Join-Path (Join-Path $PSScriptRoot 'mutants') ($Name + '.agent')
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing frozen mutant source $path" }
    return [IO.File]::ReadAllText($path)
}
function Invoke-MutantAttempt([string]$Project,[string]$Label,[string]$MutantName,[int]$Revision) {
    $treeBefore = @(Get-TreeInventory $Project)
    $sourceRowsBefore = @(Get-TestSourceSnapshot $Project)
    $source = Get-MutantSource $MutantName
    $responses = Invoke-Language $Project $Label @(
        (New-Request 'define' @{ frontend='flow'; syntaxVersion=2; replace=$true; expectedRevision=$Revision; source=$source }),
        (New-Request 'replace-word' @{ word='flags.agree' })
    )
    $treeAfter = @(Get-TreeInventory $Project)
    $beforeJson = ConvertTo-Json -InputObject $treeBefore -Depth 50 -Compress
    $afterJson = ConvertTo-Json -InputObject $treeAfter -Depth 50 -Compress
    $unchanged = $beforeJson -ceq $afterJson
    $sourceRowsAfter = @(Get-TestSourceSnapshot $Project)
    $sourceBeforeContent = @($sourceRowsBefore | ForEach-Object { [ordered]@{ word=$_.word; caseName=$_.caseName; sourceHash=$_.sourceHash; sourceSha256=$_.sourceSha256 } })
    $sourceAfterContent = @($sourceRowsAfter | ForEach-Object { [ordered]@{ word=$_.word; caseName=$_.caseName; sourceHash=$_.sourceHash; sourceSha256=$_.sourceSha256 } })
    $sourceRowsUnchanged = (ConvertTo-Json -InputObject $sourceBeforeContent -Depth 30 -Compress) -ceq (ConvertTo-Json -InputObject $sourceAfterContent -Depth 30 -Compress)
    return [ordered]@{
        mutant=$MutantName
        defineResponse=$responses[0]
        replaceResponse=$responses[1]
        replaceAccepted=(Test-Ok $responses[1])
        replaceErrorCode=(Get-ErrorCode $responses[1])
        durableTreeUnchanged=$unchanged
        attachedSourceUnchanged=$sourceRowsUnchanged
        testSourcesBefore=$sourceRowsBefore
        testSourcesAfter=$sourceRowsAfter
        treeBefore=$treeBefore
        treeAfter=$treeAfter
    }
}
function Get-AddFlagTestsSource([string[]]$Rows) {
    $lines = [Collections.Generic.List[string]]::new()
    foreach ($row in $Rows) {
        $parts = $row.Split(',')
        $left = $parts[0]
        $right = $parts[1]
        $expect = if ($left -ceq $right) { 'true' } else { 'false' }
        $caseName = if ($left -ceq 'true' -and $right -ceq 'false') { 'one-off-tf' } elseif ($left -ceq 'false' -and $right -ceq 'true') { 'one-off-ft' } else { 'row-' + $left + '-' + $right }
        $lines.Add("test flags.agree/$caseName {")
        $lines.Add("    flags::agree(left = $left, right = $right)")
        $lines.Add("    => $expect")
        $lines.Add('}')
    }
    return ($lines -join "`n")
}
function Assert-Preflight($Name,[bool]$Condition,$Detail=$null) {
    Add-Check $Name $Condition $Detail
    if (-not $Condition) { throw "Oracle preflight failed: $Name" }
}

if ($Mode -eq 'preflight') {
    $sourceFiles = @('M_TF.agent','M_FT.agent') | ForEach-Object {
        $path = Join-Path (Join-Path $PSScriptRoot 'mutants') $_
        [ordered]@{ name=[IO.Path]::GetFileNameWithoutExtension($_); path=[IO.Path]::GetRelativePath($repo,$path).Replace('\','/'); sha256=(Get-Sha256 $path); source=[IO.File]::ReadAllText($path) }
    }
    $cliHash = Get-Sha256 $script:cli
    $seedInventoryBefore = @(Get-TreeInventory $script:seed)
    $seedTreeHashBefore = Get-TreeHash $script:seed
    $controlRoot = Join-Path $output 'controls'
    if (Test-Path -LiteralPath $controlRoot) { throw "Refusing to overwrite existing preflight controls: $controlRoot" }
    New-Item -ItemType Directory -Path $controlRoot -Force | Out-Null
    $complete = Join-Path $controlRoot 'complete'
    Copy-Project $script:seed $complete
    $addBoth = Get-AddFlagTestsSource @('true,false','false,true')
    $addResponse = Invoke-Language $complete 'control-complete-add' @(
        (New-Request 'define' @{ frontend='flow'; syntaxVersion=2; source=$addBoth }),
        (New-Request 'commit' @{ word='flags.agree'; library=$true }),
        (New-Request 'test-all' @{}),
        (New-Request 'describe' @{ word='flags.agree' })
    )
    $completeDescribe = $addResponse[3].data
    Assert-Preflight 'complete control adds both off-diagonal rows' (Test-Ok $addResponse[0]) (Get-FlagsTruthRows (Get-TestSourceSnapshot $complete))
    $completeTestSources = @(Get-TestSourceSnapshot $complete | Where-Object { $_.word -ceq 'flags.agree' })
    Assert-Preflight 'complete control persists four active flag test sources' ($completeTestSources.Count -eq 4 -and (@(Get-FlagsTruthRows $completeTestSources).Count -eq 4)) $completeTestSources
    Assert-Preflight 'complete control correctly promotes equality to library' (Test-Ok $addResponse[1] -and [string]$completeDescribe.maturity -eq 'library' -and [string]$completeDescribe.status -eq 'persistent') $addResponse[1]
    Assert-Preflight 'complete control tests and finite/structural coverage pass' (
        @($addResponse[2].data.results | Where-Object { $_.passed -ne $true }).Count -eq 0 -and
        [int]$completeDescribe.coverage.instructionsCovered -eq [int]$completeDescribe.coverage.instructionsTotal -and
        [int]$completeDescribe.coverage.branchesCovered -eq [int]$completeDescribe.coverage.branchesTotal -and
        $completeDescribe.coverage.finiteCoverage.complete -eq $true
    ) $completeDescribe.coverage
    $completeControlTree = @(Get-TreeInventory $complete)
    $completeRevision = [int]$completeDescribe.revision
    $completeMutations = [Collections.Generic.List[object]]::new()
    foreach ($mutant in @('M_TF','M_FT')) {
        $project = Join-Path $controlRoot ('complete-' + $mutant)
        Copy-Project $complete $project
        $result = Invoke-MutantAttempt $project ('control-complete-' + $mutant) $mutant $completeRevision
        $result.expectedReplaceAccepted = $false
        $result.correctlyRejected = (-not $result.replaceAccepted -and $result.durableTreeUnchanged -and $result.replaceErrorCode -eq 'COMMIT_TESTS_FAILED')
        $completeMutations.Add($result)
    }
    Assert-Preflight 'complete tests reject both word-only equality mutants' (@($completeMutations | Where-Object { $_.correctlyRejected -ne $true }).Count -eq 0) @($completeMutations)

    $oneOff = Join-Path $controlRoot 'one-off-tf'
    Copy-Project $script:seed $oneOff
    $oneOffResponses = Invoke-Language $oneOff 'control-one-off-add' @(
        (New-Request 'define' @{ frontend='flow'; syntaxVersion=2; source=(Get-AddFlagTestsSource @('true,false')) }),
        (New-Request 'commit' @{ word='flags.agree'; library=$true }),
        (New-Request 'test-all' @{}),
        (New-Request 'describe' @{ word='flags.agree' })
    )
    $oneOffSources = Get-TestSourceSnapshot $oneOff
    Assert-Preflight 'one-off-diagonal control literal test sources contain exactly seed rows plus TF' ((@(Get-FlagsTruthRows $oneOffSources) -join '|') -ceq 'false,false|true,false|true,true') @(Get-FlagsTruthRows $oneOffSources)
    Assert-Preflight 'one-off-diagonal control qualifies as a persistent library function' (Test-Ok $oneOffResponses[1] -and [string]$oneOffResponses[3].data.maturity -ceq 'library' -and [string]$oneOffResponses[3].data.status -ceq 'persistent') $oneOffResponses[1]
    Assert-Preflight 'one-off-diagonal control seed and added tests pass' (@($oneOffResponses[2].data.results | Where-Object { $_.passed -ne $true }).Count -eq 0) $oneOffResponses[2].data.results
    Assert-Preflight 'one-off-diagonal control finite return coverage is complete despite missing product row' ($oneOffResponses[3].data.coverage.finiteCoverage.complete -eq $true) $oneOffResponses[3].data.coverage.finiteCoverage
    $oneOffDescribe = $oneOffResponses[3].data
    $oneOffMutations = [Collections.Generic.List[object]]::new()
    foreach ($mutant in @('M_TF','M_FT')) {
        $project = Join-Path $controlRoot ('one-off-' + $mutant)
        Copy-Project $oneOff $project
        $result = Invoke-MutantAttempt $project ('control-one-off-' + $mutant) $mutant ([int]$oneOffDescribe.revision)
        $result.expectedReplaceAccepted = ($mutant -eq 'M_FT')
        if ($result.replaceAccepted) {
            $rowRequests = @(
                (New-Request 'eval' @{ frontend='flow'; syntaxVersion=2; structured=$true; code='flags::agree(left = true, right = true)' }),
                (New-Request 'eval' @{ frontend='flow'; syntaxVersion=2; structured=$true; code='flags::agree(left = true, right = false)' }),
                (New-Request 'eval' @{ frontend='flow'; syntaxVersion=2; structured=$true; code='flags::agree(left = false, right = true)' }),
                (New-Request 'eval' @{ frontend='flow'; syntaxVersion=2; structured=$true; code='flags::agree(left = false, right = false)' })
            )
            $oracleResponses = Invoke-Language $project ('oracle-one-off-' + $mutant) $rowRequests
            $expected = @($true,$false,$false,$true)
            $rowChecks = for ($i=0; $i -lt 4; $i++) { Test-BoolResult $oracleResponses[$i] $expected[$i] }
            $result.independentTruthRowsPass = @($rowChecks | Where-Object { $_ -ne $true }).Count -eq 0
            $result.independentTruthRows = @($oracleResponses | ForEach-Object { $_.data.structuredStack.values[0].value })
            $result.mismatchedTruthRows = @(
                for ($i=0; $i -lt 4; $i++) { if ($rowChecks[$i] -ne $true) { @('true,true','true,false','false,true','false,false')[$i] } }
            )
            $result.durableTreeUnchanged = $false
        } else {
            $result.independentTruthRowsPass = $null
            $result.independentTruthRows = @()
            $result.mismatchedTruthRows = @()
        }
        $result.expectedAcceptanceMatched = ($result.replaceAccepted -eq $result.expectedReplaceAccepted)
        if ($mutant -eq 'M_FT') { $result.oracleDetectedEscapedMutant = (@($result.mismatchedTruthRows) -contains 'false,true') }
        else { $result.oracleDetectedEscapedMutant = $null }
        $oneOffMutations.Add($result)
    }
    Assert-Preflight 'TF-only test rejects the TF mutant while allowing the opposite FT mutant' (@($oneOffMutations | Where-Object { $_.expectedAcceptanceMatched -ne $true }).Count -eq 0) @($oneOffMutations)
    $escaped = @($oneOffMutations | Where-Object { $_.mutant -ceq 'M_FT' })[0]
    Assert-Preflight 'independent truth oracle catches FT after TF-only tests allow M_FT' ($escaped.replaceAccepted -and $escaped.oracleDetectedEscapedMutant -and @($escaped.mismatchedTruthRows).Count -eq 1) $escaped
    Assert-Preflight 'canonical seed inventory remains unchanged during controls' ((Get-TreeHash $script:seed) -ceq $seedTreeHashBefore) @{ before=$seedTreeHashBefore; after=(Get-TreeHash $script:seed) }

    $controlScoreDir = Join-Path $output 'complete-score-smoke'
    $scoreStdout = Join-Path $output 'complete-score-smoke.stdout.log'
    $scoreStderr = Join-Path $output 'complete-score-smoke.stderr.log'
    $scoreOutput = @(& pwsh -NoProfile -File $PSCommandPath -Mode score -CliDll $script:cli -SeedProject $script:seed -ActorProject $complete -OutputDirectory $controlScoreDir 2> $scoreStderr)
    $scoreExit = $LASTEXITCODE
    [IO.File]::WriteAllLines($scoreStdout,$scoreOutput,$script:utf8)
    $controlScorePath = Join-Path $controlScoreDir 'independent-score.json'
    $controlScore = if (Test-Path -LiteralPath $controlScorePath -PathType Leaf) { Get-Content -LiteralPath $controlScorePath -Raw | ConvertFrom-Json -Depth 100 } else { $null }
    Assert-Preflight 'score mode completes all independent behavior checks on complete control' (
        $scoreExit -eq 0 -and $null -ne $controlScore -and
        @($controlScore.flags.independentTruthOracle | Where-Object { $_.passed -ne $true }).Count -eq 0 -and
        @($controlScore.selection.independentBehaviorChecks | Where-Object { $_.passed -ne $true }).Count -eq 0 -and
        $controlScore.testAcceptance.selectionProjectAndSignature -eq $true -and
        $controlScore.actorUnchanged -eq $true
    ) @{ exitCode=$scoreExit; scorePath=$controlScorePath; stdout=$scoreOutput; stderrPath=$scoreStderr }

    $freeze = [ordered]@{
        formatVersion=1
        study='finite-adoption-001'
        verifier=[ordered]@{ path=[IO.Path]::GetRelativePath($repo,$PSCommandPath).Replace('\','/'); sha256=(Get-Sha256 $PSCommandPath) }
        mutantSources=$sourceFiles
        cli=[ordered]@{ path=[IO.Path]::GetRelativePath($repo,$script:cli).Replace('\','/'); sha256=$cliHash }
        seed=[ordered]@{ path=[IO.Path]::GetRelativePath($repo,$script:seed).Replace('\','/'); treeSha256=$seedTreeHashBefore; inventory=$seedInventoryBefore }
        controls=[ordered]@{ completeTreeInventory=$completeControlTree; completeMutations=@($completeMutations); oneOffMutations=@($oneOffMutations) }
        checks=@($script:checks)
        invocations=@($script:invocations)
        passed=(@($script:checks | Where-Object { $_.passed -ne $true }).Count -eq 0)
        preparedUtc=[DateTime]::UtcNow.ToString('O')
    }
    $freezePath = Join-Path $output 'oracle-preflight.json'
    [IO.File]::WriteAllText($freezePath,(ConvertTo-Json -InputObject $freeze -Depth 100),$script:utf8)
    $freezeHash = Get-Sha256 $freezePath
    [IO.File]::WriteAllText((Join-Path $output 'oracle-preflight.sha256'),$freezeHash + "`n",$script:utf8)
    if (-not $freeze.passed) { throw "Oracle preflight failed; see $freezePath" }
    Write-Output "Oracle preflight passed: $freezePath"
    exit 0
}

$actor = Resolve-RepoPath $ActorProject
if (-not (Test-Path -LiteralPath $actor -PathType Container)) { throw "Actor project does not exist: $actor" }
$actorBefore = @(Get-TreeInventory $actor)
$actorHashBefore = Get-TreeHash $actor
$scoreCopy = Join-Path $output 'score-copy'
if (Test-Path -LiteralPath $scoreCopy) { throw "Refusing to overwrite existing scoring copy: $scoreCopy" }
Copy-Project $actor $scoreCopy
$copyBefore = @(Get-TreeInventory $scoreCopy)
$testSources = Get-TestSourceSnapshot $scoreCopy
$flagRows = @(Get-FlagsTruthRows $testSources)
$selectionValues = Get-SelectionTestValues $testSources
$flagCodes = @(
    (New-Request 'eval' @{ frontend='flow'; syntaxVersion=2; structured=$true; code='flags::agree(left = true, right = true)' }),
    (New-Request 'eval' @{ frontend='flow'; syntaxVersion=2; structured=$true; code='flags::agree(left = true, right = false)' }),
    (New-Request 'eval' @{ frontend='flow'; syntaxVersion=2; structured=$true; code='flags::agree(left = false, right = true)' }),
    (New-Request 'eval' @{ frontend='flow'; syntaxVersion=2; structured=$true; code='flags::agree(left = false, right = false)' })
)
$integerValues = @([Int64]::MinValue,-1L,0L,1L,[Int64]::MaxValue)
$selectionRequests = [Collections.Generic.List[object]]::new()
$selectionCases = [Collections.Generic.List[object]]::new()
foreach ($value in $integerValues) {
    $literal = $value.ToString([Globalization.CultureInfo]::InvariantCulture)
    $selectionRequests.Add((New-Request 'eval' @{ frontend='flow'; syntaxVersion=2; structured=$true; code="selection::accept(selection::new(first = option::none<Int>()), $literal).first()" }))
    $selectionCases.Add([ordered]@{ input='none'; item=$literal; expectedCase='some'; expectedValue=$literal })
}
for ($i=0; $i -lt $integerValues.Count; $i++) {
    $existing = $integerValues[$i].ToString([Globalization.CultureInfo]::InvariantCulture)
    $item = $integerValues[($i + 2) % $integerValues.Count].ToString([Globalization.CultureInfo]::InvariantCulture)
    $selectionRequests.Add((New-Request 'eval' @{ frontend='flow'; syntaxVersion=2; structured=$true; code="selection::accept(selection::new(first = option::some<Int>($existing)), $item).first()" }))
    $selectionCases.Add([ordered]@{ input='some'; existing=$existing; item=$item; expectedCase='some'; expectedValue=$existing })
}
$requests = [Collections.Generic.List[object]]::new()
$requests.Add((New-Request 'test-all' @{}))
$requests.Add((New-Request 'describe' @{ word='flags.agree' }))
$requests.Add((New-Request 'describe' @{ word='selection.accept' }))
$requests.Add((New-Request 'source' @{ word='flags.agree' }))
$requests.Add((New-Request 'source' @{ word='selection.accept' }))
$requests.Add((New-Request 'source' @{ type='Selection' }))
foreach ($request in $flagCodes) { $requests.Add($request) }
foreach ($request in $selectionRequests) { $requests.Add($request) }
$responses = Invoke-Language $scoreCopy 'actor-independent-score' @($requests.ToArray())
$runAll = $responses[0]
$flagsDescribe = $responses[1].data
$selectionDescribe = $responses[2].data
$flagsSource = [string]$responses[3].data
$selectionSource = [string]$responses[4].data
$selectionTypeSource = [string]$responses[5].data
$flagChecks = [Collections.Generic.List[object]]::new()
$truthExpected = @($true,$false,$false,$true)
$truthLabels = @('true,true','true,false','false,true','false,false')
for ($i=0; $i -lt 4; $i++) {
    $ok = Test-BoolResult $responses[6 + $i] $truthExpected[$i]
    $flagChecks.Add([ordered]@{ row=$truthLabels[$i]; passed=$ok; response=$responses[6 + $i] })
}
$selectionChecks = [Collections.Generic.List[object]]::new()
for ($i=0; $i -lt $selectionCases.Count; $i++) {
    $case = $selectionCases[$i]
    $response = $responses[10 + $i]
    $ok = Test-OptionResult $response $case.expectedCase $case.expectedValue
    $selectionChecks.Add([ordered]@{ case=$case; passed=$ok; response=$response })
}
$ownTestResults = @($runAll.data.results)
$flagsTests = @($ownTestResults | Where-Object { $_.word -ceq 'flags.agree' })
$selectionTests = @($ownTestResults | Where-Object { $_.word -ceq 'selection.accept' })
$flagsCoverage = $flagsDescribe.coverage
$selectionCoverage = $selectionDescribe.coverage
$flagsStructuralComplete = (
    [int]$flagsCoverage.instructionsTotal -gt 0 -and [int]$flagsCoverage.instructionsCovered -eq [int]$flagsCoverage.instructionsTotal -and
    [int]$flagsCoverage.branchesCovered -eq [int]$flagsCoverage.branchesTotal
)
$flagsFiniteComplete = $flagsCoverage.finiteCoverage.complete -eq $true
$selectionStructuralComplete = (
    [int]$selectionCoverage.instructionsTotal -gt 0 -and [int]$selectionCoverage.instructionsCovered -eq [int]$selectionCoverage.instructionsTotal -and
    [int]$selectionCoverage.branchesCovered -eq [int]$selectionCoverage.branchesTotal
)
$flagsRowComplete = (@($truthLabels | Where-Object { $_ -notin $flagRows }).Count -eq 0)
$signaturePreserved = (
    (@($flagsDescribe.inputs) -join ',') -ceq 'Bool,Bool' -and (@($flagsDescribe.outputs) -join ',') -ceq 'Bool' -and
    (@($flagsDescribe.parameters | ForEach-Object name) -join ',') -ceq 'left,right' -and
    (@($selectionDescribe.inputs) -join ',') -ceq 'Selection,Int' -and (@($selectionDescribe.outputs) -join ',') -ceq 'Selection' -and
    (@($selectionDescribe.parameters | ForEach-Object name) -join ',') -ceq 'state,item' -and
    $selectionTypeSource -match '(?s)record\s+Selection\s*\{\s*field\s+first\s*:\s*Option<Int>\s*;\s*\}'
)
$persistentMaturity = ($flagsDescribe.status -ceq 'persistent' -and $selectionDescribe.status -ceq 'persistent' -and $selectionDescribe.maturity -ceq 'project')
$flagsLibraryMaturity = ($flagsDescribe.status -ceq 'persistent' -and $flagsDescribe.maturity -ceq 'library')
$testsPassed = (@($ownTestResults | Where-Object { $_.passed -ne $true }).Count -eq 0 -and $ownTestResults.Count -gt 0)

$mutationResults = [Collections.Generic.List[object]]::new()
$actorRevision = [int]$flagsDescribe.revision
foreach ($mutant in @('M_TF','M_FT')) {
    $project = Join-Path $output ('actor-' + $mutant)
    Copy-Project $scoreCopy $project
    $result = Invoke-MutantAttempt $project ('actor-' + $mutant) $mutant $actorRevision
    $result.testsCatchMutant = (-not $result.replaceAccepted -and $result.replaceErrorCode -eq 'COMMIT_TESTS_FAILED')
    if ($result.replaceAccepted) {
        $oracleRequests = @($flagCodes)
        $oracleResponses = Invoke-Language $project ('actor-mutant-oracle-' + $mutant) $oracleRequests
        $expected = @($true,$false,$false,$true)
        $rowChecks = for ($i=0; $i -lt 4; $i++) { Test-BoolResult $oracleResponses[$i] $expected[$i] }
        $result.oracleTruthRowsPass = @($rowChecks | Where-Object { $_ -ne $true }).Count -eq 0
        $result.mismatchedTruthRows = @(
            for ($i=0; $i -lt 4; $i++) { if ($rowChecks[$i] -ne $true) { $truthLabels[$i] } }
        )
    } else {
        $result.oracleTruthRowsPass = $null
        $result.mismatchedTruthRows = @()
    }
    $mutationResults.Add($result)
}
$actorAfter = @(Get-TreeInventory $actor)
$actorHashAfter = Get-TreeHash $actor
$actorUnchanged = ($actorHashBefore -ceq $actorHashAfter)
$copyAfter = @(Get-TreeInventory $scoreCopy)
$scoreCopyUnchanged = (ConvertTo-Json -InputObject $copyBefore -Depth 50 -Compress) -ceq (ConvertTo-Json -InputObject $copyAfter -Depth 50 -Compress)
$base = Get-BaseChecks $scoreCopy 'final'
$score = [ordered]@{
    formatVersion=1
    study='finite-adoption-001'
    mode='independent-actor-score'
    actorProject=[IO.Path]::GetFullPath($actor)
    actorTreeSha256Before=$actorHashBefore
    actorTreeSha256After=$actorHashAfter
    actorUnchanged=$actorUnchanged
    scoreCopy=[ordered]@{ path=[IO.Path]::GetRelativePath($repo,$scoreCopy).Replace('\','/'); treeInventoryBefore=$copyBefore; treeInventoryAfter=$copyAfter; unchanged=$scoreCopyUnchanged }
    sourceSnapshots=[ordered]@{ functions=@([ordered]@{name='flags.agree';source=$flagsSource},[ordered]@{name='selection.accept';source=$selectionSource}); types=@([ordered]@{name='Selection';source=$selectionTypeSource}); tests=$base.sourceSnapshots }
    testResults=[ordered]@{ allPassed=$testsPassed; total=$ownTestResults.Count; failed=@($ownTestResults | Where-Object { $_.passed -ne $true }); flagsAgree=@($flagsTests); selectionAccept=@($selectionTests) }
    flags=[ordered]@{ persistentLibrary=$flagsLibraryMaturity; signaturePreserved=(@($flagsDescribe.inputs) -join ',' -ceq 'Bool,Bool' -and (@($flagsDescribe.outputs) -join ',' -ceq 'Bool')); literalAttachedTestSourceRows=@($base.flagsRows); allFourLiteralTruthRowsInOwnTestSources=$flagsRowComplete; independentTruthOracle=@($flagChecks); structuralCoverageComplete=$flagsStructuralComplete; finiteCoverageComplete=$flagsFiniteComplete; coverage=$flagsCoverage; describe=$flagsDescribe }
    selection=[ordered]@{ persistentProject=($selectionDescribe.status -ceq 'persistent' -and $selectionDescribe.maturity -ceq 'project'); signatureAndRecordPreserved=($signaturePreserved -and ((@($selectionDescribe.inputs) -join ',') -ceq 'Selection,Int') -and ((@($selectionDescribe.outputs) -join ',') -ceq 'Selection')); literalOwnTestArgumentValues=$selectionValues; structuralCoverageComplete=$selectionStructuralComplete; finiteCoverage=$selectionCoverage.finiteCoverage; independentBehaviorChecks=@($selectionChecks); behaviorChecksAllPass=(@($selectionChecks | Where-Object { $_.passed -ne $true }).Count -eq 0); describe=$selectionDescribe }
    mutations=@($mutationResults)
    replaceWordMutationSummary=[ordered]@{ attempted=@($mutationResults | ForEach-Object mutant); rejected=@($mutationResults | Where-Object { $_.replaceAccepted -eq $false } | ForEach-Object mutant); accepted=@($mutationResults | Where-Object { $_.replaceAccepted -eq $true } | ForEach-Object mutant); results=@($mutationResults) }
    traceReview=[ordered]@{ status='root coordinator reviews actor trace for attempted promotion and missing-None diagnosis'; finalSelectionMaturity=[string]$selectionDescribe.maturity }
    testAcceptance=[ordered]@{
        actorProjectUntouched=$actorUnchanged
        testsPassed=$testsPassed
        flagsAllFourTruthRowsBehavior=(@($flagChecks | Where-Object { $_.passed -ne $true }).Count -eq 0)
        flagsLibraryStructuralAndFinite=$flagsLibraryMaturity -and $flagsStructuralComplete -and $flagsFiniteComplete
        selectionProjectAndSignature=$selectionDescribe.status -ceq 'persistent' -and $selectionDescribe.maturity -ceq 'project' -and $signaturePreserved
        independentSelectionBehavior=(@($selectionChecks | Where-Object { $_.passed -ne $true }).Count -eq 0)
    }
    checks=@($script:checks)
    invocations=@($script:invocations)
    scoredUtc=[DateTime]::UtcNow.ToString('O')
}
$scorePath = Join-Path $output 'independent-score.json'
[IO.File]::WriteAllText($scorePath,(ConvertTo-Json -InputObject $score -Depth 100),$script:utf8)
$snapshotPath = Join-Path $output 'test-source-snapshots.json'
[IO.File]::WriteAllText($snapshotPath,(ConvertTo-Json -InputObject $base.sourceSnapshots -Depth 50),$script:utf8)
Write-Output "Independent score saved: $scorePath"
Write-Output "Test source snapshots saved: $snapshotPath"
