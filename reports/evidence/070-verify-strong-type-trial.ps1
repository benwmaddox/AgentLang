#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ProjectPath,
    [Parameter(Mandatory)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$cli = Join-Path $repo '.agentlang/strong-type-flow-001/language-bin/AgentLang.Cli.dll'
$trialRoot = Join-Path $repo 'experiments/AgentLang.SubagentTrials/strong-type-flow-001/runs/units-001'
$seedPath = Join-Path $trialRoot 'starting-project'
$project = (Resolve-Path -LiteralPath $ProjectPath).Path
$output = [IO.Path]::GetFullPath($OutputPath, $repo)
$clock = '2000-01-01T00:00:00Z'
$targetName = 'delivery.speed-kph'
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$checks = [Collections.Generic.List[object]]::new()
$sessions = [Collections.Generic.List[object]]::new()
$probeEvidence = [Collections.Generic.List[object]]::new()
$failure = $null
$scratchRoot = Join-Path $repo ('.agentlang/strong-type-flow-001/acceptance-tmp-' + [guid]::NewGuid().ToString('N'))

function Canonical($Value) {
    ConvertTo-Json -InputObject $Value -Depth 100 -Compress
}

function Assert-Check([string]$Name, [bool]$Condition, [string]$Details = '') {
    $script:checks.Add([ordered]@{ name = $Name; passed = $Condition; details = $Details })
    if (-not $Condition) {
        if ([string]::IsNullOrWhiteSpace($Details)) { throw "Acceptance check failed: $Name" }
        throw "Acceptance check failed: $Name ($Details)"
    }
}

function Get-Inventory([string]$Root) {
    @(Get-ChildItem -LiteralPath $Root -File -Recurse -Force |
        Sort-Object { [IO.Path]::GetRelativePath($Root, $_.FullName) } |
        ForEach-Object {
            [ordered]@{
                path = [IO.Path]::GetRelativePath($Root, $_.FullName)
                bytes = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        })
}

function Get-Manifest([string]$Root) {
    $store = Join-Path $Root '.agentlang/store'
    $currentPath = Join-Path $store 'CURRENT'
    if (-not (Test-Path -LiteralPath $currentPath -PathType Leaf)) { throw "Missing project authority: $currentPath" }
    $current = Get-Content -LiteralPath $currentPath -Raw | ConvertFrom-Json -Depth 100
    $manifestPath = Join-Path $store "manifests/$($current.manifestHash).json"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "Missing current manifest: $manifestPath" }
    $manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-Check "manifest hash validates for $Root" ($manifestHash -ceq [string]$current.manifestHash) $manifestHash
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 100
    [ordered]@{ current = $current; manifest = $manifest; hash = $manifestHash; path = $manifestPath }
}

function Get-CurrentRevision($Manifest, [string]$Name) {
    $heads = @($Manifest.words | Where-Object { $_.currentName -ceq $Name })
    if ($heads.Count -ne 1) { throw "Expected one persisted word head named '$Name'; found $($heads.Count)." }
    $head = $heads[0]
    $revisions = @($Manifest.revisions | Where-Object { $_.wordId -ceq $head.wordId -and $_.revision -eq $head.currentRevision })
    if ($revisions.Count -ne 1) { throw "Expected one current revision for '$Name'; found $($revisions.Count)." }
    $revisions[0]
}

function Get-ObjectText([string]$Root, $Reference) {
    $extension = if ($Reference.kind -eq 'virtual-file-state') { '.json' } else { '.agent' }
    $path = Join-Path $Root ".agentlang/store/objects/$($Reference.hash)$extension"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing immutable source object: $path" }
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-Check "source object hash validates $($Reference.kind)/$($Reference.hash)" ($hash -ceq [string]$Reference.hash) $hash
    [IO.File]::ReadAllText($path, $script:utf8)
}

function Copy-Project([string]$From, [string]$To) {
    [IO.Directory]::CreateDirectory($To) | Out-Null
    Get-ChildItem -LiteralPath $From -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $To -Recurse -Force
    }
}

function Invoke-JsonlSession([string]$SessionName, [string]$TargetProject, [object[]]$Requests) {
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @($script:cli, '--project', $TargetProject, '--clock', $script:clock, '--jsonl')) {
        $start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (-not $process.Start()) { throw "Could not start the pinned runtime for session '$SessionName'." }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    try {
        foreach ($request in $Requests) {
            $process.StandardInput.WriteLine((ConvertTo-Json -InputObject $request -Depth 100 -Compress))
        }
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(120000)) {
            $process.Kill($true)
            throw "Runtime session '$SessionName' exceeded 120 seconds."
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $lines = @($stdout -split "`r?`n" | Where-Object { $_.Length -gt 0 })
        $responses = [Collections.Generic.List[object]]::new()
        foreach ($line in $lines) {
            try { $responses.Add(($line | ConvertFrom-Json -Depth 100)) }
            catch { throw "Runtime session '$SessionName' emitted invalid JSONL: $line" }
        }
        $session = [ordered]@{
            name = $SessionName
            project = $TargetProject
            requests = $Requests
            rawResponseLines = $lines
            responses = $responses.ToArray()
            stderr = $stderr
            exitCode = $process.ExitCode
        }
        $script:sessions.Add($session)
        Assert-Check "JSONL response count for $SessionName" ($responses.Count -eq $Requests.Count) "requests=$($Requests.Count), responses=$($responses.Count)"
        Assert-Check "runtime process exit for $SessionName" ($process.ExitCode -eq 0) "exit=$($process.ExitCode); stderr=$stderr"
        return $responses.ToArray()
    }
    finally {
        if (-not $process.HasExited) { $process.Kill($true) }
        $process.Dispose()
    }
}

function Get-WordFingerprint($WordsResponse) {
    @($WordsResponse.data.words | Sort-Object name | ForEach-Object {
        [ordered]@{
            name = $_.name
            id = $_.id
            inputs = @($_.inputs)
            outputs = @($_.outputs)
            status = $_.status
            maturity = $_.maturity
            deprecated = $_.deprecated
        }
    })
}

function Get-TestCategories([string[]]$Sources) {
    $categories = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $invariant = [Globalization.CultureInfo]::InvariantCulture
    foreach ($source in $Sources) {
        $inputs = [regex]::Matches($source, 'MetersPerSecond\s*::\s*new\s*\(\s*(-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)\s*\)')
        foreach ($input in $inputs) {
            $value = 0.0
            if (-not [double]::TryParse($input.Groups[1].Value, [Globalization.NumberStyles]::Float, $invariant, [ref]$value)) { continue }
            if ([Math]::Abs($value) -lt 1e-12) { $categories.Add('zero') | Out-Null }
            elseif ($value -lt 0.0) { $categories.Add('negative') | Out-Null }
            elseif ([Math]::Abs($value - [Math]::Truncate($value)) -gt 1e-12) { $categories.Add('fractional') | Out-Null }
            else { $categories.Add('positive') | Out-Null }
        }
    }
    @($categories | Sort-Object)
}

function Format-Float([double]$Value) {
    $formatted = $Value.ToString('0.0##########', [Globalization.CultureInfo]::InvariantCulture)
    if ($formatted -notmatch '\.') { $formatted += '.0' }
    $formatted
}

try {
    Assert-Check 'pinned CLI assembly exists' (Test-Path -LiteralPath $cli -PathType Leaf) $cli
    Assert-Check 'frozen starting project exists' (Test-Path -LiteralPath $seedPath -PathType Container) $seedPath
    Assert-Check 'final project is separate from the frozen seed' (-not [string]::Equals($project, (Resolve-Path -LiteralPath $seedPath).Path, [StringComparison]::OrdinalIgnoreCase)) $project

    $seedBefore = Get-Inventory $seedPath
    $finalBefore = Get-Inventory $project
    $seedAuthority = Get-Manifest $seedPath
    $finalAuthority = Get-Manifest $project
    $seedManifest = $seedAuthority.manifest
    $finalManifest = $finalAuthority.manifest
    $seedRevision = Get-CurrentRevision $seedManifest 'email.valid?'
    $targetHead = @($finalManifest.words | Where-Object { $_.currentName -ceq $targetName })
    Assert-Check 'required persisted target exists exactly once' ($targetHead.Count -eq 1) "found=$($targetHead.Count)"

    Assert-Check 'all seeded type declarations and validator binding are unchanged' ((Canonical $seedManifest.types) -ceq (Canonical $finalManifest.types)) 'type source references, formats and validator targets must match byte for byte'
    foreach ($priorHead in $seedManifest.words) {
        $matchingHead = @($finalManifest.words | Where-Object { $_.wordId -ceq $priorHead.wordId })
        Assert-Check "stable prior word head $($priorHead.currentName)" ($matchingHead.Count -eq 1 -and (Canonical $matchingHead[0]) -ceq (Canonical $priorHead)) 'prior name, ID, current revision and deprecation state'
    }
    foreach ($priorRevision in $seedManifest.revisions) {
        $matchingRevision = @($finalManifest.revisions | Where-Object { $_.wordId -ceq $priorRevision.wordId -and $_.revision -eq $priorRevision.revision })
        Assert-Check "retained prior revision $($priorRevision.name)/$($priorRevision.revision)" ($matchingRevision.Count -eq 1 -and (Canonical $matchingRevision[0]) -ceq (Canonical $priorRevision)) 'historical revision metadata and attached-source hashes'
    }
    $priorValidatorHistory = @($seedManifest.revisions | Where-Object { $_.name -ceq 'email.valid?' } | Sort-Object revision)
    $finalValidatorHistory = @($finalManifest.revisions | Where-Object { $_.name -ceq 'email.valid?' } | Sort-Object revision)
    Assert-Check 'Email validator revisions and all attached tests are unchanged' ((Canonical $priorValidatorHistory) -ceq (Canonical $finalValidatorHistory)) "seed=$($priorValidatorHistory.Count), final=$($finalValidatorHistory.Count) revisions"
    $seedWordIds = @($seedManifest.words | ForEach-Object { [string]$_.wordId })
    $finalNewNames = @($finalManifest.words | Where-Object { $seedWordIds -notcontains [string]$_.wordId } | ForEach-Object { $_.currentName } | Sort-Object)
    Assert-Check 'the only new persistent user word is the requested conversion' ((Canonical $finalNewNames) -ceq (Canonical @($targetName))) ("newWords=" + ($finalNewNames -join ', '))

    foreach ($seedFile in $seedBefore) {
        if ($seedFile.path -in @('dictionary.agent', 'project.agent', '.agentlang\store\CURRENT')) { continue }
        $matchingFile = @($finalBefore | Where-Object { $_.path -ceq $seedFile.path })
        Assert-Check "immutable seeded file retained $($seedFile.path)" ($matchingFile.Count -eq 1 -and $matchingFile[0].sha256 -ceq $seedFile.sha256) 'pre-existing content-addressed source/history data must not change'
    }

    $priorTypes = @($seedManifest.types | Sort-Object name)
    $emailType = @($priorTypes | Where-Object { $_.name -ceq 'Email' })
    $speedType = @($priorTypes | Where-Object { $_.name -ceq 'MetersPerSecond' })
    $kphType = @($priorTypes | Where-Object { $_.name -ceq 'KilometersPerHour' })
    $deliveryType = @($priorTypes | Where-Object { $_.name -ceq 'Delivery' })
    Assert-Check 'seed contains validated Email and two distinct unit types plus Delivery' ($emailType.Count -eq 1 -and $speedType.Count -eq 1 -and $kphType.Count -eq 1 -and $deliveryType.Count -eq 1) 'Email, MetersPerSecond, KilometersPerHour and Delivery are required'
    foreach ($type in $priorTypes) { Get-ObjectText $seedPath $type.definition | Out-Null }
    Assert-Check 'seed Email validator closure was already persistent' ($null -ne $seedRevision -and $emailType[0].validatorTarget -ne $null) 'Email must have a frozen validator target and revision history'

    $testAllSeed = Join-Path $scratchRoot 'seed-baseline'
    Copy-Project $seedPath $testAllSeed
    $baselineResponses = Invoke-JsonlSession 'seed baseline tests' $testAllSeed @(@{ op = 'test-all' })
    $baselineTests = @($baselineResponses[0].data.results)
    Assert-Check 'seed test baseline passed' ($baselineResponses[0].ok -and $baselineTests.Count -gt 0 -and @($baselineTests | Where-Object { -not $_.passed }).Count -eq 0) "tests=$($baselineTests.Count)"

    $targetRevision = Get-CurrentRevision $finalManifest $targetName
    $targetSource = Get-ObjectText $project $targetRevision.definition
    $testSources = @($targetRevision.tests | ForEach-Object { Get-ObjectText $project $_ })
    $categories = Get-TestCategories $testSources
    Assert-Check 'conversion has at least three independent attached test cases' ($targetRevision.tests.Count -ge 3) "count=$($targetRevision.tests.Count)"
    $requiredCategories = @('zero', 'positive', 'fractional', 'negative')
    Assert-Check 'attached tests exercise zero, positive, fractional and negative inputs' (@($requiredCategories | Where-Object { $_ -notin $categories }).Count -eq 0) ($categories -join ', ')
    Assert-Check 'conversion source explicitly applies factor 3.6' ($targetSource.Contains('3.6', [StringComparison]::Ordinal)) 'independent vectors below verify conversion behavior'
    Assert-Check 'new word is persisted as a library revision' ($targetRevision.maturity -eq 'library' -and -not $targetRevision.deprecated) "maturity=$($targetRevision.maturity), deprecated=$($targetRevision.deprecated)"

    $requests = [Collections.Generic.List[object]]::new()
    $requests.Add(@{ op = 'test'; word = $targetName })
    $requests.Add(@{ op = 'test-all' })
    $requests.Add(@{ op = 'describe'; word = $targetName })
    $requests.Add(@{ op = 'source'; word = $targetName })
    $requests.Add(@{ op = 'describe'; word = 'Email.new' })
    $requests.Add(@{ op = 'describe'; word = 'MetersPerSecond.new' })
    $requests.Add(@{ op = 'describe'; word = 'KilometersPerHour.new' })
    $requests.Add(@{ op = 'describe'; word = 'delivery.new' })
    foreach ($speed in @(0.0, 1.0, 2.5, -3.0, 0.125, 100.0)) {
        $literal = Format-Float ([double]$speed)
        $input = "delivery::new(contact = Email::new(`"dev@example.com`"), speed = MetersPerSecond::new($literal))"
        $requests.Add(@{ op = 'eval'; frontend = 'flow'; code = "delivery::speed-kph($input)" })
        $requests.Add(@{ op = 'eval'; frontend = 'flow'; code = "KilometersPerHour::value(delivery::speed-kph($input))" })
    }
    $requests.Add(@{ op = 'eval'; frontend = 'flow'; code = 'Email::new("dev@example.com")' })
    $requests.Add(@{ op = 'eval'; frontend = 'flow'; code = 'Email::value(Email::new("dev@example.com"))' })
    $requests.Add(@{ op = 'eval'; frontend = 'flow'; code = 'Email::new("missing-at-sign")' })
    $runtimeResponses = Invoke-JsonlSession 'fresh-process final acceptance' $project $requests.ToArray()

    $ownTestResponse = $runtimeResponses[0]
    $ownTestResults = @($ownTestResponse.data.results)
    Assert-Check 'target attached test run succeeds' ($ownTestResponse.ok -and $ownTestResults.Count -eq $targetRevision.tests.Count -and @($ownTestResults | Where-Object { -not $_.passed }).Count -eq 0) "results=$($ownTestResults.Count), attachments=$($targetRevision.tests.Count)"
    $coverage = $ownTestResponse.data.coverage
    Assert-Check 'library has nonzero full authored instruction coverage' ($coverage.instructionsTotal -gt 0 -and $coverage.instructionsCovered -eq $coverage.instructionsTotal) "instructions=$($coverage.instructionsCovered)/$($coverage.instructionsTotal)"
    Assert-Check 'every authored branch outcome is covered when branches exist' ($coverage.branchesTotal -eq 0 -or $coverage.branchesCovered -eq $coverage.branchesTotal) "branches=$($coverage.branchesCovered)/$($coverage.branchesTotal)"
    $allTestResults = @($runtimeResponses[1].data.results)
    Assert-Check 'fresh-process project test-all passes every test' ($runtimeResponses[1].ok -and $allTestResults.Count -ge ($baselineTests.Count + $targetRevision.tests.Count) -and @($allTestResults | Where-Object { -not $_.passed }).Count -eq 0) "results=$($allTestResults.Count), minimum=$($baselineTests.Count + $targetRevision.tests.Count)"

    $description = $runtimeResponses[2].data
    Assert-Check 'reloaded target signature is Delivery to KilometersPerHour' (@($description.inputs).Count -eq 1 -and $description.inputs[0] -ceq 'Delivery' -and @($description.outputs).Count -eq 1 -and $description.outputs[0] -ceq 'KilometersPerHour') "inputs=$(Canonical $description.inputs), outputs=$(Canonical $description.outputs)"
    Assert-Check 'reloaded target is pure persistent library vocabulary' ($description.status -eq 'persistent' -and $description.maturity -eq 'library' -and @($description.effects).Count -eq 0) "status=$($description.status), maturity=$($description.maturity), effects=$(Canonical $description.effects)"
    Assert-Check 'describe exposes all attached passing tests and full coverage' (@($description.tests).Count -eq $targetRevision.tests.Count -and $description.coverage.instructionsCovered -eq $description.coverage.instructionsTotal -and ($description.coverage.branchesTotal -eq 0 -or $description.coverage.branchesCovered -eq $description.coverage.branchesTotal)) 'fresh process describes the committed word after test-all'
    $dependencies = @($description.dependencies | ForEach-Object { ([string]$_).Replace('::', '.') })
    Assert-Check 'compiled dependencies expose field read, explicit unit unwrap, multiply, and unit constructor' ($dependencies -contains 'delivery.speed' -and $dependencies -contains 'MetersPerSecond.value' -and ($dependencies -contains 'float.multiply' -or $dependencies -contains 'multiply') -and $dependencies -contains 'KilometersPerHour.new') ($dependencies -join ', ')
    foreach ($row in @(
        [ordered]@{ label = 'Email'; index = 4; input = 'String'; output = 'Email' },
        [ordered]@{ label = 'MetersPerSecond'; index = 5; input = 'Float'; output = 'MetersPerSecond' },
        [ordered]@{ label = 'KilometersPerHour'; index = 6; input = 'Float'; output = 'KilometersPerHour' },
        [ordered]@{ label = 'Delivery'; index = 7; input = @('Email', 'MetersPerSecond'); output = 'Delivery' }
    )) {
        $generated = $runtimeResponses[$row.index].data
        $expectedInputs = @($row.input)
        Assert-Check "reloaded strong type constructor: $($row.label)" (@($generated.inputs).Count -eq $expectedInputs.Count -and (Canonical @($generated.inputs)) -ceq (Canonical $expectedInputs) -and @($generated.outputs).Count -eq 1 -and $generated.outputs[0] -ceq $row.output) "inputs=$(Canonical $generated.inputs), outputs=$(Canonical $generated.outputs)"
    }
    $runtimeSource = [string]$runtimeResponses[3].data
    Assert-Check 'runtime source is the committed implementation' ($runtimeSource -ceq $targetSource) 'source command must return the exact persisted word body'

    $responseIndex = 8
    $vectorEvidence = [Collections.Generic.List[object]]::new()
    foreach ($speed in @(0.0, 1.0, 2.5, -3.0, 0.125, 100.0)) {
        $nominal = $runtimeResponses[$responseIndex]
        $unwrapped = $runtimeResponses[$responseIndex + 1]
        $expected = [double]$speed * 3.6
        $actual = [double]::NaN
        $actualText = if ($unwrapped.ok -and @($unwrapped.data.stack).Count -eq 1) { [string]$unwrapped.data.stack[0] } else { '' }
        $parsed = [double]::TryParse($actualText, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$actual)
        $valueError = if ($parsed) { [Math]::Abs($actual - $expected) } else { [double]::PositiveInfinity }
        $nominalType = if ($nominal.ok -and @($nominal.data.stackTypes).Count -eq 1) { [string]$nominal.data.stackTypes[0] } else { '' }
        $vectorPassed = $nominal.ok -and $unwrapped.ok -and $nominalType -ceq 'KilometersPerHour' -and $unwrapped.data.stackTypes[0] -ceq 'Float' -and $parsed -and $valueError -le 1e-9 * [Math]::Max(1.0, [Math]::Abs($expected))
        Assert-Check "independent explicit-unit conversion vector $speed m/s" $vectorPassed "nominal=$nominalType, actual=$actualText, expected=$expected"
        $vectorEvidence.Add([ordered]@{ metersPerSecond = $speed; nominalType = $nominalType; actualKph = $actualText; expectedKph = $expected; absoluteError = $valueError; nominalResponse = $nominal; unwrappedResponse = $unwrapped })
        $responseIndex += 2
    }
    Assert-Check 'valid Email constructs and explicitly unwraps' ($runtimeResponses[20].ok -and @($runtimeResponses[20].data.stackTypes).Count -eq 1 -and $runtimeResponses[20].data.stackTypes[0] -ceq 'Email' -and $runtimeResponses[21].ok -and @($runtimeResponses[21].data.stack).Count -eq 1 -and $runtimeResponses[21].data.stack[0] -ceq '"dev@example.com"') 'Email remains a nominal String refinement with explicit access'
    Assert-Check 'invalid Email construction reports refinement failure' (-not $runtimeResponses[22].ok -and $runtimeResponses[22].error.code -ceq 'REFINEMENT_FAILED') (Canonical $runtimeResponses[22].error)

    $seedTypeSources = @{}
    foreach ($type in $seedManifest.types) { $seedTypeSources[$type.name] = Get-ObjectText $seedPath $type.definition }
    foreach ($type in $finalManifest.types) {
        $typeSource = Get-ObjectText $project $type.definition
        Assert-Check "type source retained exactly: $($type.name)" ($seedTypeSources.ContainsKey($type.name) -and $seedTypeSources[$type.name] -ceq $typeSource) 'all existing nominal/refined and record sources are unchanged'
    }

    $probeDefinitions = @(
        [ordered]@{ name = 'cross-unit constructor'; definition = 'word probe.wrong-unit(speed: KilometersPerHour) -> MetersPerSecond { effects none MetersPerSecond::new(speed) }'; code = 'FLOW_ARGUMENT_TYPE' },
        [ordered]@{ name = 'wrong Delivery speed field'; definition = 'word probe.wrong-delivery(contact: Email, speed: KilometersPerHour) -> Delivery { effects none delivery::new(contact = contact, speed = speed) }'; code = 'FLOW_ARGUMENT_TYPE' },
        [ordered]@{ name = 'plain String for Email field'; definition = 'word probe.plain-string(address: String, speed: MetersPerSecond) -> Delivery { effects none delivery::new(contact = address, speed = speed) }'; code = 'FLOW_ARGUMENT_TYPE' },
        [ordered]@{ name = 'Int for Float-backed wrapper'; definition = 'word probe.int-float(value: Int) -> MetersPerSecond { effects none MetersPerSecond::new(value) }'; code = 'FLOW_ARGUMENT_TYPE' }
    )
    $probes = foreach ($probe in $probeDefinitions) {
        [ordered]@{
            name = $probe.name
            expectedCode = $probe.code
            source = @"
type ProbeUnit : Float { }
record ProbeRecord { field amount: Float; }
word probe.sentinel(value: Int) -> Int {
    effects none
    ::add(value, 1)
}
$($probe.definition.Replace('effects none ', "effects none`n    "))
"@
        }
    }
    foreach ($probe in $probes) {
        $probePath = Join-Path $scratchRoot ('probe-' + [guid]::NewGuid().ToString('N'))
        Copy-Project $project $probePath
        $beforeFiles = Get-Inventory $probePath
        $probeRequests = @(
            @{ op = 'task.begin'; goal = "Acceptance negative type probe: $($probe.name)" },
            @{ op = 'words' },
            @{ op = 'task.status' },
            @{ op = 'source'; type = 'ProbeUnit' },
            @{ op = 'define'; frontend = 'flow'; source = [string]$probe.source },
            @{ op = 'words' },
            @{ op = 'task.status' },
            @{ op = 'source'; type = 'ProbeUnit' }
        )
        $probeResponses = Invoke-JsonlSession "negative probe $($probe.name)" $probePath $probeRequests
        $beforeStatus = $probeResponses[2].data
        $afterStatus = $probeResponses[6].data
        $beforeWords = Get-WordFingerprint $probeResponses[1]
        $afterWords = Get-WordFingerprint $probeResponses[5]
        $errorCode = [string]$probeResponses[4].error.code
        $supportedTypeError = $errorCode -in @('FLOW_ARGUMENT_TYPE', 'TYPE_STACK_MISMATCH')
        Assert-Check "structured compile-time type rejection: $($probe.name)" (-not $probeResponses[4].ok -and $supportedTypeError) "code=$errorCode"
        Assert-Check "no partial dictionary staging: $($probe.name)" ((Canonical $beforeWords) -ceq (Canonical $afterWords)) 'word inventory, identities, status, maturity and signatures must remain identical'
        Assert-Check "no task candidate creation: $($probe.name)" ((Canonical $beforeStatus.wordsCreated) -ceq (Canonical $afterStatus.wordsCreated) -and @($afterStatus.wordsCreated).Count -eq 0) 'task.status.wordsCreated must remain empty'
        $beforeProbeType = $probeResponses[3]
        $afterProbeType = $probeResponses[7]
        Assert-Check "no partial type staging: $($probe.name)" (-not $beforeProbeType.ok -and -not $afterProbeType.ok -and $beforeProbeType.error.code -ceq 'DISCOVERY_UNKNOWN_TYPE' -and $afterProbeType.error.code -ceq 'DISCOVERY_UNKNOWN_TYPE') 'source(ProbeUnit) must remain unavailable before and after the failed multi-declaration document'
        $afterFiles = Get-Inventory $probePath
        Assert-Check "negative probe leaves durable copy unchanged: $($probe.name)" ((Canonical $beforeFiles) -ceq (Canonical $afterFiles)) 'failed type definitions must not write project state'
        $probeEvidence.Add([ordered]@{ name = $probe.name; source = $probe.source; expectedCode = $probe.expectedCode; actualCode = $errorCode; beforeStatus = $beforeStatus; afterStatus = $afterStatus; beforeProbeType = $beforeProbeType; afterProbeType = $afterProbeType; beforeWords = $beforeWords; afterWords = $afterWords; beforeFiles = $beforeFiles; afterFiles = $afterFiles })
    }

    $seedAfter = Get-Inventory $seedPath
    $finalAfter = Get-Inventory $project
    Assert-Check 'frozen seed is unchanged by acceptance' ((Canonical $seedBefore) -ceq (Canonical $seedAfter)) 'source project inventory before and after'
    Assert-Check 'actor final project is unchanged by acceptance' ((Canonical $finalBefore) -ceq (Canonical $finalAfter)) 'acceptance only reads the supplied final project'
}
catch {
    $failure = $_.Exception.Message
}
finally {
    $parent = [IO.Path]::GetFullPath((Join-Path $repo '.agentlang/strong-type-flow-001'))
    $scratchFull = [IO.Path]::GetFullPath($scratchRoot)
    if ($scratchFull.StartsWith($parent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $scratchFull)) {
        Remove-Item -LiteralPath $scratchFull -Recurse -Force
    }
    $directory = Split-Path -Parent $output
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $evidence = [ordered]@{
        schemaVersion = 1
        passed = ($null -eq $failure)
        failure = $failure
        sourceRevision = (& git -C $repo rev-parse HEAD).Trim()
        cliPath = $cli
        cliSha256 = if (Test-Path -LiteralPath $cli) { (Get-FileHash -LiteralPath $cli -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
        seedProject = $seedPath
        finalProject = $project
        targetWord = $targetName
        noOpControl = 'Running against starting-project must fail the required persisted target check; it is the no-op rejection control.'
        checks = $checks.ToArray()
        independentVectors = if (Get-Variable vectorEvidence -Scope Script -ErrorAction SilentlyContinue) { $vectorEvidence.ToArray() } else { @() }
        negativeTypeProbes = $probeEvidence.ToArray()
        protocolSessions = $sessions.ToArray()
        scope = 'Fresh-process reload, final word signature/purity/library maturity, own tests and coverage, unchanged refined types/Email validator history, explicit cross-unit calculations, structured no-partial-stage type failures, and read-only preservation of seed/final projects.'
    }
    [IO.File]::WriteAllText($output, (ConvertTo-Json -InputObject $evidence -Depth 100), [Text.UTF8Encoding]::new($false))
}

if ($failure) { throw $failure }
Write-Output "Strong-type trial acceptance passed $($checks.Count) checks and six independent conversion vectors. Evidence: $output"
