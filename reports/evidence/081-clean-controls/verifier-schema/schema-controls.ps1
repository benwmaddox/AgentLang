# Schema-only controls for Verify-RetentionTrial. Git blob and commit reads are stubbed;
# this harness does not establish genuine source provenance or run a CLI/actor process.
[CmdletBinding()]
param(
    [string]$ReportPath = 'reports/evidence/081-verifier-schema-controls.json'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$sourceRepo = $repo
$verifierPath = Join-Path $sourceRepo 'scripts/Verify-RetentionTrial.ps1'
$utf8NoBom = [Text.UTF8Encoding]::new($false)
$controlResults = [Collections.Generic.List[object]]::new()
$mockGitCalls = 0
$externalProcessCount = 0
$tempRoot = Join-Path $PSScriptRoot ('081-verifier-controls-' + [Guid]::NewGuid().ToString('N'))

$tokens = $null
$parseErrors = $null
$verifierAst = [System.Management.Automation.Language.Parser]::ParseFile($verifierPath,[ref]$tokens,[ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw "Verifier parse failed: $($parseErrors[0].Message)" }

$importFunctions = @(
    'Get-Field','Add-Check','Get-Sha256','Get-FreezeInventory','Get-CanonicalInventoryRows',
    'Get-CanonicalJson','Resolve-RepoPath','Resolve-FileFromRoot','Test-Within','Get-TreeHash',
    'Get-RuntimeInventory','Get-JsonStringProperty','Test-PinnedRepoRelativePath',
    'Validate-StartingState','Validate-FrozenPin'
)
foreach ($name in $importFunctions) {
    $definition = $verifierAst.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name },$true)
    if ($null -eq $definition) { throw "Verifier helper was not found: $name" }
    Invoke-Expression $definition.Extent.Text
}

# Validate-FrozenPin ordinarily asks Git for commit/blob facts. These stubs ensure
# the harness uses no Git process and intentionally make those provenance checks fail.
function Get-GitBlobSha256([string]$Revision,[string]$RelativePath) { return $null }
function git {
    $script:mockGitCalls++
    $global:LASTEXITCODE = 0
    if (($args -join ' ') -match 'rev-parse') {
        return ([string]$args[-1] -replace '\^\{commit\}$','')
    }
    return @()
}

$sequence = @('S01','S07')
$expectedCounts = @{ S01=10; S07=54 }
$runIndexMap = @{
    B1 = @{ flat=@('R01','R02'); retained=@('R03','R04'); 'reset-rich'=@('R05','R06') }
    B2 = @{ 'reset-rich'=@('R07','R08'); flat=@('R09','R10'); retained=@('R11','R12') }
}
$mustRequireFrozenPin = $true
$BaselineFallback = $false
$oraclePath = Join-Path $repo 'experiments/AgentLang.SubagentTrials/business-policy-retention-003/acceptance.json'
$checkCategory = 'metadata'
$checks = [Collections.Generic.List[object]]::new()
$metadataChecks = [Collections.Generic.List[object]]::new()
$behaviorChecks = [Collections.Generic.List[object]]::new()
$frozenPinInfo = [ordered]@{ required=$true; checked=$false; status='not checked'; path=$null; sha256=$null }
$checkFrozenPin = $false
$startingStateMetadata = $null
$startingStatePassed = $false

function Reset-Checks {
    $checks.Clear()
    $metadataChecks.Clear()
    $behaviorChecks.Clear()
    $script:checkCategory = 'metadata'
}

function Add-ControlResult([string]$Name,[bool]$Passed,[object]$Details = $null) {
    $controlResults.Add([ordered]@{ name=$Name; passed=$Passed; details=$Details })
}

function Copy-Tree([string]$Source,[string]$Destination) {
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $Source -Recurse -Force -File) {
        $relative = [IO.Path]::GetRelativePath($Source,$file.FullName)
        $target = Join-Path $Destination $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        [IO.File]::Copy($file.FullName,$target,$false)
    }
}

function Get-CheckRows([object[]]$Rows,[string[]]$Names) {
    $result = [Collections.Generic.List[object]]::new()
    foreach ($name in $Names) {
        $matches = @($Rows | Where-Object { $_.name -ceq $name })
        $result.Add([ordered]@{
            name=$name
            count=$matches.Count
            passed=($matches.Count -eq 1 -and [bool]$matches[0].passed)
            details=$(if ($matches.Count -eq 1) { $matches[0].details } else { $null })
        })
    }
    return $result.ToArray()
}

function Write-JsonNew([string]$Path,[object]$Value) {
    $fullPath = if ([IO.Path]::IsPathFullyQualified($Path)) { [IO.Path]::GetFullPath($Path) } else { [IO.Path]::GetFullPath($Path,$sourceRepo) }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fullPath)) | Out-Null
    $content = ConvertTo-Json -InputObject $Value -Depth 80
    $bytes = $utf8NoBom.GetBytes($content + "`n")
    $stream = [IO.FileStream]::new($fullPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $stream.Write($bytes,0,$bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    return $fullPath
}

$report = $null
try {
    $savedRoot = Join-Path $sourceRepo '.agentlang/business-policy-retention-003/superseded-f3f4391'
    $savedRun = Join-Path $savedRoot 'run-R01'
    $savedStart = Join-Path $savedRun 'starting-project'
    $savedActor = Join-Path $savedRoot 'actor-R01'
    $savedStatePath = Join-Path $savedRun 'starting-state.json'
    $savedPinPath = Join-Path $savedRun 'prelaunch.json'
    $savedGlobalFreezePath = Join-Path $savedRoot 'global-freeze.json'
    foreach ($path in @($savedStart,$savedActor,$savedStatePath,$savedPinPath,$savedGlobalFreezePath)) {
        if (-not (Test-Path -LiteralPath $path)) { throw "Preserved saved fixture is missing: $path" }
    }

    [IO.Directory]::CreateDirectory($tempRoot) | Out-Null
    $controlRepo = Join-Path $tempRoot 'repo'
    $positiveRun = Join-Path $controlRepo '.agentlang/business-policy-retention-003/runs/R01'
    $canonicalStart = Join-Path $positiveRun 'starting-project'
    $canonicalActor = Join-Path $controlRepo '.agentlang/business-policy-retention-003/actors/R01'
    $canonicalStatePath = Join-Path $positiveRun 'starting-state.json'
    $canonicalPinPath = Join-Path $positiveRun 'prelaunch.json'
    Copy-Tree $savedStart $canonicalStart
    Copy-Tree $savedActor $canonicalActor
    Copy-Item -LiteralPath (Join-Path $savedRun 'prompt.txt') -Destination (Join-Path $positiveRun 'prompt.txt')

    # Populate only the immutable inputs needed by the repaired state/pin gates.
    $studyRoot = 'experiments/AgentLang.SubagentTrials/business-policy-retention-003'
    $baselineRoot = Join-Path $studyRoot 'artifacts/baselines'
    foreach ($kind in @('flat','rich')) {
        $relative = Join-Path $baselineRoot $kind
        Copy-Tree (Join-Path $sourceRepo $relative) (Join-Path $controlRepo $relative)
    }
    $neededFiles = @(
        'experiments/AgentLang.SubagentTrials/business-policy-retention-003/acceptance.json',
        'experiments/AgentLang.SubagentTrials/business-policy-retention-003/design.json',
        'experiments/AgentLang.SubagentTrials/business-policy-retention-003/language-primer.md',
        'experiments/AgentLang.SubagentTrials/business-policy-retention-003/artifacts/flat-customer.agent',
        'scripts/Prepare-RetentionTrial.ps1',
        'scripts/Prepare-BusinessPolicyTrial.ps1'
    )
    foreach ($relative in $neededFiles) {
        $target = Join-Path $controlRepo $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        [IO.File]::Copy((Join-Path $sourceRepo $relative),$target,$false)
    }
    $globalFreezePath = Join-Path $controlRepo 'experiments/AgentLang.SubagentTrials/business-policy-retention-003/artifacts/global-freeze.json'
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($globalFreezePath)) | Out-Null
    [IO.File]::Copy($savedGlobalFreezePath,$globalFreezePath,$false)

    $pin = Get-Content -LiteralPath $savedPinPath -Raw | ConvertFrom-Json -AsHashtable -Depth 100
    $state = Get-Content -LiteralPath $savedStatePath -Raw | ConvertFrom-Json -AsHashtable -Depth 100
    $state.project.path = $canonicalStart
    $state.actor.projectPath = $canonicalActor
    $state.baseline.path = Join-Path $controlRepo "$baselineRoot/flat/project"
    [IO.File]::WriteAllText($canonicalStatePath,(ConvertTo-Json -InputObject $state -Depth 100),$utf8NoBom)
    $pin.projectPath = $canonicalActor
    $pin.startingProjectPath = [IO.Path]::GetRelativePath($controlRepo,$canonicalStart).Replace('\','/')
    $pin.startingStatePath = [IO.Path]::GetRelativePath($controlRepo,$canonicalStatePath).Replace('\','/')
    $pin.startingStateSha256 = Get-Sha256 $canonicalStatePath
    [IO.File]::WriteAllText($canonicalPinPath,(ConvertTo-Json -InputObject $pin -Depth 100),$utf8NoBom)

    # Production functions resolve repository paths through this disposable mirror.
    $repo = $controlRepo
    $oraclePath = Join-Path $repo "$studyRoot/acceptance.json"
    $globalFreezePath = Join-Path $repo "$studyRoot/artifacts/global-freeze.json"
    $pin = Get-Content -LiteralPath $canonicalPinPath -Raw | ConvertFrom-Json -AsHashtable -Depth 100
    $runtimeCliPath = Resolve-RepoPath ([string]$pin.runtimeRoots.cliDllPath)

    # The real saved starting-state fixture must pass every local state/inventory gate.
    Reset-Checks
    $startingStateMetadata = Validate-StartingState $canonicalStart $canonicalActor $null 'flat' 'B1' 'S01' 'R01'
    $stateFailures = @($metadataChecks | Where-Object { -not $_.passed })
    $startingStatePassed = $null -ne $startingStateMetadata -and $stateFailures.Count -eq 0
    $preparedData = Get-Field $startingStateMetadata 'data'
    $preparedProjectFiles = @(Get-Field (Get-Field $preparedData 'project') 'files')
    $preparedActorFiles = @(Get-Field (Get-Field $preparedData 'actor') 'files')
    Add-ControlResult 'saved R01 starting-state positive fixture' ($startingStatePassed -and $metadataChecks.Count -eq 14) @{
        checkCount=$metadataChecks.Count
        failedChecks=@($stateFailures | ForEach-Object { $_.name })
        projectFileCount=$preparedProjectFiles.Count
        actorFileCount=$preparedActorFiles.Count
    }

    # Re-run the complete pin validator over the saved R01 inputs. The source blob
    # stub means only schema/path/clock/limit/inventory checks are claimed here.
    Reset-Checks
    $positivePin = Validate-FrozenPin $canonicalStart $canonicalActor $runtimeCliPath 'flat' 'B1' 'S01' 'R01' $null
    $positivePinNames = @(
        'frozen pin schema, study, arm, block, task, sequence, and run match',
        'frozen pin starting project path matches argument',
        'frozen run starting-state path matches sibling state',
        'frozen starting-state hash matches pin',
        'frozen pin clock is the exact reviewed JSON string literal',
        'global host clock is the exact reviewed JSON string literal',
        'frozen model and raw protocol limits match global design',
        'global host settings and per-run host pin match field by field',
        'frozen starting project files and tree hash match prepared state',
        'frozen prelaunch actor inventory matches prepared state and immutable start',
        'frozen pin has validated prepared-state metadata for state-bound fields'
    )
    $positivePinChecks = Get-CheckRows $metadataChecks.ToArray() $positivePinNames
    Add-ControlResult 'saved R01 frozen-pin repaired schema bindings' (@($positivePinChecks | Where-Object { -not $_.passed }).Count -eq 0) @{
        selectedCheckCount=$positivePinChecks.Count
        checks=$positivePinChecks
        wholePinGatePassed=($null -ne $positivePin -and [bool]$positivePin.passed)
        wholePinGateClaimed=$false
    }

    # Full saved-state negative: a rich archive path cannot stand in for flat.
    [IO.Directory]::CreateDirectory($tempRoot) | Out-Null
    $badBaselineRoot = Join-Path $tempRoot 'bad-baseline'
    $badBaselineRun = Join-Path $badBaselineRoot 'runs/R01'
    $badBaselineStart = Join-Path $badBaselineRun 'starting-project'
    $badBaselineActor = Join-Path $badBaselineRoot 'actors/R01'
    Copy-Tree $canonicalStart $badBaselineStart
    Copy-Tree $canonicalActor $badBaselineActor
    $badBaselineState = Get-Content -LiteralPath $canonicalStatePath -Raw | ConvertFrom-Json -AsHashtable -Depth 100
    $badBaselineState.project.path = $badBaselineStart
    $badBaselineState.actor.projectPath = $badBaselineActor
    $badBaselineState.baseline.path = Join-Path $repo 'experiments/AgentLang.SubagentTrials/business-policy-retention-003/artifacts/baselines/rich/project'
    $badBaselineStatePath = Join-Path $badBaselineRun 'starting-state.json'
    [IO.Directory]::CreateDirectory($badBaselineRun) | Out-Null
    [IO.File]::WriteAllText($badBaselineStatePath,(ConvertTo-Json -InputObject $badBaselineState -Depth 100),$utf8NoBom)
    Reset-Checks
    $badBaselineMetadata = Validate-StartingState $badBaselineStart $badBaselineActor $null 'flat' 'B1' 'S01' 'R01'
    $badBaselineExpectedFailures = @(
        'starting-state selects the archived flat or rich baseline for its arm',
        'starting-state baseline file inventory and tree hash match archived manifest'
    )
    $badBaselineChecks = Get-CheckRows $metadataChecks.ToArray() $badBaselineExpectedFailures
    Add-ControlResult 'wrong-arm baseline rejected' ($null -ne $badBaselineMetadata -and @($badBaselineChecks | Where-Object { -not $_.passed }).Count -eq 0) @{
        expectedFailureCount=$badBaselineExpectedFailures.Count
        expectedFailures=$badBaselineChecks
        actualFailedCheckNames=@($metadataChecks | Where-Object { -not $_.passed } | ForEach-Object { $_.name })
    }

    # The actual pin helper must reject rooted and traversal paths.
    $expectedStartPath = [IO.Path]::GetFullPath($canonicalStart)
    $validPinnedPath = Test-PinnedRepoRelativePath ([string]$pin.startingProjectPath) $expectedStartPath
    $absolutePinnedPath = Test-PinnedRepoRelativePath $expectedStartPath $expectedStartPath
    $traversalPinnedPath = Test-PinnedRepoRelativePath '.agentlang/business-policy-retention-003/runs/../runs/R01/starting-project' $expectedStartPath
    Add-ControlResult 'canonical start path accepted; absolute and traversal values rejected' ($validPinnedPath.passed -and -not $absolutePinnedPath.passed -and -not $traversalPinnedPath.passed) @{
        positive=$validPinnedPath
        absoluteRejected=(-not $absolutePinnedPath.passed)
        traversalRejected=(-not $traversalPinnedPath.passed)
    }

    # The exact JsonDocument reader must accept literal strings, reject wrong kinds
    # and values, and reject duplicate/case-ambiguous property names.
    $pinClock = Get-JsonStringProperty $canonicalPinPath @('clockValue')
    $globalClock = Get-JsonStringProperty $globalFreezePath @('host','clockValue')
    $clockTemp = Join-Path $tempRoot 'clock'
    [IO.Directory]::CreateDirectory($clockTemp) | Out-Null
    $pinText = [IO.File]::ReadAllText($canonicalPinPath)
    $globalText = [IO.File]::ReadAllText($globalFreezePath)
    $wrongTypeText = [regex]::Replace($pinText,'("clockValue"\s*:\s*)"2000-01-01T00:00:00Z"','$1 20000101',1)
    $wrongLiteralText = [regex]::Replace($pinText,'("clockValue"\s*:\s*)"2000-01-01T00:00:00Z"','$1 "2000-01-01T00:00:01Z"',1)
    $duplicateReplacement = '$1,' + "`r`n" + '  "clockValue": "2000-01-01T00:00:01Z"'
    $duplicateText = [regex]::Replace($pinText,'("clockValue"\s*:\s*"2000-01-01T00:00:00Z")',$duplicateReplacement,1)
    $wrongGlobalTypeText = [regex]::Replace($globalText,'("clockValue"\s*:\s*)"2000-01-01T00:00:00Z"','$1 20000101',1)
    $wrongGlobalLiteralText = [regex]::Replace($globalText,'("clockValue"\s*:\s*)"2000-01-01T00:00:00Z"','$1 "2000-01-01T00:00:01Z"',1)
    $duplicateGlobalText = [regex]::Replace($globalText,'("clockValue"\s*:\s*"2000-01-01T00:00:00Z")',$duplicateReplacement,1)
    $clockFixturePaths = @{}
    foreach ($fixture in @(
        @{name='pin-wrong-kind.json';text=$wrongTypeText;propertyPath=@('clockValue')},
        @{name='pin-wrong-literal.json';text=$wrongLiteralText;propertyPath=@('clockValue')},
        @{name='pin-duplicate.json';text=$duplicateText;propertyPath=@('clockValue')},
        @{name='global-wrong-kind.json';text=$wrongGlobalTypeText;propertyPath=@('host','clockValue')},
        @{name='global-wrong-literal.json';text=$wrongGlobalLiteralText;propertyPath=@('host','clockValue')},
        @{name='global-duplicate.json';text=$duplicateGlobalText;propertyPath=@('host','clockValue')}
    )) {
        $fixturePath = Join-Path $clockTemp $fixture.name
        [IO.File]::WriteAllText($fixturePath,$fixture.text,$utf8NoBom)
        $clockFixturePaths[$fixture.name] = Get-JsonStringProperty $fixturePath $fixture.propertyPath
    }
    $clockNegatives = @($clockFixturePaths.GetEnumerator() | ForEach-Object {
        [ordered]@{ name=$_.Key; rejected=((-not $_.Value.found) -or $_.Value.valueKind -cne 'String' -or $_.Value.value -cne '2000-01-01T00:00:00Z'); valueKind=$_.Value.valueKind; value=$_.Value.value }
    })
    Add-ControlResult 'pin/global exact clock strings accepted; wrong type/value and duplicate fields rejected' (
        $pinClock.found -and $pinClock.valueKind -ceq 'String' -and $pinClock.value -ceq '2000-01-01T00:00:00Z' -and
        $globalClock.found -and $globalClock.valueKind -ceq 'String' -and $globalClock.value -ceq '2000-01-01T00:00:00Z' -and
        $clockNegatives.Count -eq 6 -and @($clockNegatives | Where-Object { -not $_.rejected }).Count -eq 0
    ) @{ positivePin=$pinClock; positiveGlobal=$globalClock; rejectedFixtures=$clockNegatives }

    # The real pin validator must handle a missing sibling state without resolving
    # absent state fields or throwing a Count/path error.
    $missingRoot = Join-Path $controlRepo '.agentlang/business-policy-retention-003/missing-state'
    $missingRun = Join-Path $missingRoot 'runs/R01'
    $missingStart = Join-Path $missingRun 'starting-project'
    $missingActor = Join-Path $missingRoot 'actors/R01'
    [IO.Directory]::CreateDirectory($missingRun) | Out-Null
    Copy-Tree $canonicalStart $missingStart
    Copy-Tree $canonicalActor $missingActor
    Copy-Item -LiteralPath (Join-Path $positiveRun 'prompt.txt') -Destination (Join-Path $missingRun 'prompt.txt')
    $missingPin = Get-Content -LiteralPath $canonicalPinPath -Raw | ConvertFrom-Json -AsHashtable -Depth 100
    $missingPin.startingProjectPath = [IO.Path]::GetRelativePath($repo,$missingStart).Replace('\','/')
    $missingPin.projectPath = $missingActor
    $missingPin.startingStatePath = [IO.Path]::GetRelativePath($repo,(Join-Path $missingRun 'starting-state.json')).Replace('\','/')
    [IO.File]::WriteAllText((Join-Path $missingRun 'prelaunch.json'),(ConvertTo-Json -InputObject $missingPin -Depth 100),$utf8NoBom)
    Reset-Checks
    $startingStateMetadata = $null
    $startingStatePassed = $false
    $null = Validate-FrozenPin $missingStart $missingActor $runtimeCliPath 'flat' 'B1' 'S01' 'R01' $null
    $missingStateChecks = Get-CheckRows $metadataChecks.ToArray() @(
        'frozen pin has validated prepared-state metadata for state-bound fields',
        'frozen run starting-state path matches sibling state',
        'frozen starting-state hash matches pin',
        'frozen starting project files and tree hash match prepared state',
        'frozen prelaunch actor inventory matches prepared state and immutable start',
        'frozen CLI and Business runtime roots match prepared state'
    )
    Add-ControlResult 'missing sibling prepared state rejected without cascading exception' (@($missingStateChecks | Where-Object { -not $_.passed }).Count -eq 0) @{
        checkCount=$missingStateChecks.Count
        checks=$missingStateChecks
        stateFileExists=(Test-Path -LiteralPath (Join-Path $missingRun 'starting-state.json') -PathType Leaf)
    }

    $report = [ordered]@{
        schemaVersion=1
        studyId='business-policy-retention-003'
        resultKind='schema-only-verifier-controls'
        passed=(@($controlResults | Where-Object { -not $_.passed }).Count -eq 0)
        verifiedAtUtc=[DateTime]::UtcNow.ToString('O')
        verifierPath='scripts/Verify-RetentionTrial.ps1'
        verifierSha256=(Get-Sha256 $verifierPath)
        controlScriptPath=[IO.Path]::GetRelativePath($sourceRepo,$PSCommandPath).Replace('\','/')
        controlScriptSha256=(Get-Sha256 $PSCommandPath)
        savedFixtures=[ordered]@{
            startingState='.agentlang/business-policy-retention-003/superseded-f3f4391/run-R01/starting-state.json'
            prelaunch='.agentlang/business-policy-retention-003/superseded-f3f4391/run-R01/prelaunch.json'
            startingProject='.agentlang/business-policy-retention-003/superseded-f3f4391/run-R01/starting-project'
            actorProject='.agentlang/business-policy-retention-003/superseded-f3f4391/actor-R01'
            globalFreeze='.agentlang/business-policy-retention-003/superseded-f3f4391/global-freeze.json'
        }
        checks=$controlResults.ToArray()
        mockGitCalls=$mockGitCalls
        externalGitInvocations=0
        runtimeProcessesStarted=$externalProcessCount
        genuineSourceProvenanceVerified=$false
        limitation='Validate-FrozenPin commit/blob lookups were stubbed; this exercises schema/path/clock/limit/inventory binding only and is not a genuine acceptance result.'
    }
} catch {
    if ($null -eq $report) {
        $controlResults.Add([ordered]@{ name='control harness execution'; passed=$false; details=$_.Exception.Message })
        $report = [ordered]@{
            schemaVersion=1; studyId='business-policy-retention-003'; resultKind='schema-only-verifier-controls'; passed=$false
            verifiedAtUtc=[DateTime]::UtcNow.ToString('O'); verifierPath='scripts/Verify-RetentionTrial.ps1'
            verifierSha256=(Get-Sha256 $verifierPath); controlScriptPath=[IO.Path]::GetRelativePath($sourceRepo,$PSCommandPath).Replace('\','/')
            controlScriptSha256=(Get-Sha256 $PSCommandPath); checks=$controlResults.ToArray()
            externalGitInvocations=0; runtimeProcessesStarted=$externalProcessCount; genuineSourceProvenanceVerified=$false
            limitation='Control harness failed before proving all schema controls; Git blob reads are stubbed and no genuine acceptance is claimed.'
        }
    }
} finally {
    if (Test-Path -LiteralPath $tempRoot -PathType Container) {
        $resolvedTemp = [IO.Path]::GetFullPath($tempRoot)
        $safeRoot = [IO.Path]::GetFullPath($PSScriptRoot)
        if (-not $resolvedTemp.StartsWith($safeRoot + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing cleanup outside the ignored evidence directory: $resolvedTemp"
        }
        Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
    }
}

$reportPathResolved = Write-JsonNew $ReportPath $report
$report.reportPath = [IO.Path]::GetRelativePath($sourceRepo,$reportPathResolved).Replace('\','/')
$serialized = ConvertTo-Json -InputObject $report -Depth 80
Write-Output $serialized
if (-not $report.passed) { exit 1 }
