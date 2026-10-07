#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CliDll,
    [Parameter(Mandatory)][string]$BusinessDll,
    [string]$EvidencePath = 'experiments/AgentLang.SubagentTrials/business-policy-help-002/evidence/078-verifier-focused.json',
    [ValidateSet('S06','S07')][string[]]$TaskIds = @('S06','S07')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$studyRoot = Join-Path $repo 'experiments/AgentLang.SubagentTrials/business-policy-help-002'
$oldOraclePath = Join-Path $repo 'experiments/AgentLang.SubagentTrials/business-policy-001/acceptance.json'
$newOraclePath = Join-Path $studyRoot 'acceptance.json'
$verifierPath = Join-Path $repo 'scripts/Verify-AuthoringHelpTrial.ps1'
$preparerPath = Join-Path $repo 'scripts/Prepare-BusinessPolicyTrial.ps1'
$localRoot = Join-Path $repo '.agentlang/business-policy-help-002'
$fixtureParent = Join-Path $localRoot 'control-fixtures'
$fixtureRoot = Join-Path $fixtureParent ('authoring-help-preflight-' + [Guid]::NewGuid().ToString('N'))
$preparedLocalRoot = Join-Path $fixtureRoot 'prepared-local'
$preparedRunRoot = Join-Path $fixtureRoot 'prepared-runs'
$checks = [Collections.Generic.List[object]]::new()
$runs = [Collections.Generic.List[object]]::new()
$processRuns = [Collections.Generic.List[object]]::new()
$passed = $false
$failure = $null
$oracleComparison = $null
$preparedSeedProject = $null
$preparedSeedState = $null
$resolvedCli = $null
$resolvedBusiness = $null
$resolvedSeed = $null
$preparedStatePath = $null
$requestedReportPath = $null
$reportPath = $null
$reportPathCollision = $false
$existingReportSha256 = $null
$utf8NoBom = [Text.UTF8Encoding]::new($false)

function Get-Field($Object,[string]$Name) {
    if ($null -eq $Object) { return $null }
    if ($Object -is [Collections.IDictionary]) { return $Object[$Name] }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Resolve-RepoPath([string]$Path) {
    if ([IO.Path]::IsPathFullyQualified($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath($Path,$repo)
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-CanonicalJson($Value) {
    return ConvertTo-Json -InputObject $Value -Depth 100 -Compress
}

function Get-DirectoryHash([string]$Root) {
    $rows = [Collections.Generic.List[object]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $Root -Recurse -Force -File | Sort-Object FullName) {
        $rows.Add([ordered]@{path=[IO.Path]::GetRelativePath($Root,$file.FullName).Replace('\','/');bytes=$file.Length;sha256=(Get-Sha256 $file.FullName)})
    }
    $canonical = Get-CanonicalJson @($rows)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($utf8NoBom.GetBytes($canonical))).ToLowerInvariant()
}

function Check([string]$Name,[bool]$Condition,$Details=$null) {
    $entry = [ordered]@{name=$Name;passed=$Condition}
    if ($null -ne $Details) { $entry.details=$Details }
    $checks.Add($entry)
}

function Read-Oracle([string]$Path) {
    $json = Get-Content -LiteralPath $Path -Raw
    $parameters = @{InputObject=$json;AsHashtable=$true;Depth=100}
    if ((Get-Command ConvertFrom-Json).Parameters.ContainsKey('DateKind')) { $parameters.DateKind='String' }
    $value = ConvertFrom-Json @parameters
    $document = [System.Text.Json.JsonDocument]::Parse($json)
    try {
        $createdAt = $document.RootElement.GetProperty('defaults').GetProperty('createdAt')
        if ($createdAt.ValueKind -ne [System.Text.Json.JsonValueKind]::String) { throw "Acceptance $Path has a non-string createdAt." }
        $value.defaults.createdAt = $createdAt.GetString()
    } finally { $document.Dispose() }
    return $value
}

function Get-ExpectedValue($Task,[string]$Kind,[string]$BalanceText) {
    if ($BalanceText -notmatch '^(0|-?[1-9][0-9]*)$') { throw "Noncanonical balance text '$BalanceText'." }
    $balance = [Numerics.BigInteger]::Parse($BalanceText,[Globalization.NumberStyles]::AllowLeadingSign,[Globalization.CultureInfo]::InvariantCulture)
    if ($balance -lt [Numerics.BigInteger]([long]::MinValue) -or $balance -gt [Numerics.BigInteger]([long]::MaxValue)) { throw "Balance '$BalanceText' is outside Int64." }
    $premium = [string]::Equals($Kind,'premium',[StringComparison]::Ordinal)
    $type = [string]$Task.outputs[0]
    $value = switch ([string]$Task.id) {
        'S01' { $premium }
        'S06' { if ($premium) { '1000' } else { '0' } }
        'S07' {
            if ($premium) {
                [Numerics.BigInteger]::Divide(($balance * [Numerics.BigInteger]9),[Numerics.BigInteger]10).ToString([Globalization.CultureInfo]::InvariantCulture)
            } else { $balance.ToString([Globalization.CultureInfo]::InvariantCulture) }
        }
        default { throw "Unsupported task '$($Task.id)' in acceptance corpus." }
    }
    return [ordered]@{type=$type;value=$value}
}

function Get-CasePayload($Oracle) {
    $payload = [Collections.Generic.List[object]]::new()
    foreach ($taskId in @('S06','S07')) {
        $matches = @($Oracle.tasks | Where-Object { [string]$_.id -ceq $taskId })
        if ($matches.Count -ne 1) { throw "Oracle must contain exactly one '$taskId' task." }
        $task = $matches[0]
        $cases = [Collections.Generic.List[object]]::new()
        foreach ($case in @($task.cases)) {
            $expected = Get-ExpectedValue $task ([string]$case.kind) ([string]$case.balanceMinor)
            $expectedMatches = ([string]$case.expected.type -ceq $expected.type)
            if ($expected.type -ceq 'Bool') { $expectedMatches = $expectedMatches -and $case.expected.value -is [bool] -and [bool]$case.expected.value -eq [bool]$expected.value }
            else { $expectedMatches = $expectedMatches -and $case.expected.value -is [string] -and [string]$case.expected.value -ceq [string]$expected.value }
            if (-not $expectedMatches) { throw "Oracle case '$($case.id)' does not match independent BigInteger evaluation." }
            $cases.Add([ordered]@{
                id=[string]$case.id
                kind=[string]$case.kind
                balanceMinor=[string]$case.balanceMinor
                expected=[ordered]@{type=[string]$case.expected.type;value=$case.expected.value}
            })
        }
        $payload.Add([ordered]@{
            id=$taskId
            symbol=[string]$task.symbol
            inputs=@($task.inputs)
            outputs=@($task.outputs)
            cases=@($cases)
        })
    }
    return @($payload)
}

function Get-StructuredJsonlResponses([string]$Project,[object[]]$Requests,[string]$Label) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.WorkingDirectory = $Project
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardInputEncoding = $utf8NoBom
    $start.StandardOutputEncoding = $utf8NoBom
    $start.StandardErrorEncoding = $utf8NoBom
    $start.Environment['DOTNET_CLI_TELEMETRY_OPTOUT']='1'
    $start.Environment['DOTNET_NOLOGO']='1'
    foreach ($argument in @($CliDll,'--project',$Project,'--jsonl')) { $start.ArgumentList.Add([string]$argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $clock = [Diagnostics.Stopwatch]::StartNew()
    if (-not $process.Start()) { throw "Could not start CLI control '$Label'." }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $requestLines = [Collections.Generic.List[string]]::new()
    foreach ($request in $Requests) {
        $line = ConvertTo-Json -InputObject $request -Depth 80 -Compress
        $requestLines.Add($line)
        $process.StandardInput.WriteLine($line)
    }
    $process.StandardInput.Close()
    $finished = $process.WaitForExit(180000)
    if (-not $finished) {
        try { $process.Kill($true) } catch { }
        $process.WaitForExit()
    }
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    $clock.Stop()
    $responseLines = @($stdout -split '\r?\n' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $responses = [Collections.Generic.List[object]]::new()
    $parseErrors = [Collections.Generic.List[string]]::new()
    foreach ($line in $responseLines) {
        try { $responses.Add((ConvertFrom-Json -InputObject $line -AsHashtable -Depth 100)) }
        catch { $parseErrors.Add($_.Exception.Message) }
    }
    $processRuns.Add([ordered]@{
        label=$Label;arguments=@($CliDll,'--project',$Project,'--jsonl');requestCount=$Requests.Count
        requests=@($requestLines);responseCount=$responseLines.Count;responses=@($responses)
        parseErrors=@($parseErrors);exitCode=$(if ($finished) { $process.ExitCode } else { $null })
        timedOut=(-not $finished);durationMs=$clock.ElapsedMilliseconds;stderr=$stderr
    })
    $process.Dispose()
    if (-not $finished) { throw "CLI control '$Label' timed out." }
    if ($processRuns[-1].exitCode -ne 0) { throw "CLI control '$Label' exited with code $($processRuns[-1].exitCode): $stderr" }
    if ($parseErrors.Count -gt 0) { throw "CLI control '$Label' returned invalid JSON: $($parseErrors[0])" }
    if ($responses.Count -ne $Requests.Count) { throw "CLI control '$Label' returned $($responses.Count) responses for $($Requests.Count) requests." }
    return ,@($responses)
}

function Copy-Project([string]$Source,[string]$Destination) {
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $Source -Recurse -Force -File) {
        $relative = [IO.Path]::GetRelativePath($Source,$file.FullName)
        if ($relative -match '(^|[\\/])(\.git|bin|obj)([\\/]|$)') { continue }
        $target = Join-Path $Destination $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        [IO.File]::Copy($file.FullName,$target,$false)
    }
}

function New-Customer([string]$Kind,[string]$Balance='1050') {
    return 'customer::new(id = CustomerId::new("10000000-0000-0000-0000-000000000001"), email = Email::new("ada@example.test"), kind = "' + $Kind + '", balance = Money::new(' + $Balance + '), created-at = Instant::new("2026-01-01T12:00:00.0000000+00:00"))'
}

function Get-ControlSource([string]$TaskId,[string]$Mutation) {
    $predicate = if ($Mutation -ceq 'wrong-boundary' -and $TaskId -ceq 'S06') {
        'equals(string::trim(customer::kind(customer)), "premium")'
    } else { 'equals(customer::kind(customer), "premium")' }
    $predicateDoc = 'doc "Classifies the raw premium customer kind."'
    $targetDoc = if ($Mutation -ceq 'missing-documentation') { 'doc "x"' } else { 'doc "Returns the policy result for this customer."' }
    $premium = New-Customer 'premium'
    $regular = New-Customer 'regular'
    $spaced = New-Customer ' premium '
    $predicateSpacedExpected = if ($Mutation -ceq 'wrong-boundary' -and $TaskId -ceq 'S06') { 'true' } else { 'false' }
    $predicateSpaced = "test customer.premium?/spaced { customer::premium?($spaced) => $predicateSpacedExpected }"

    if ($TaskId -ceq 'S06') {
        $premiumRate = if ($Mutation -ceq 'usable-noop') { '0' } else { '1000' }
        $regularRate = '0'
        $spacedRate = if ($Mutation -ceq 'wrong-boundary') { '1000' } else { '0' }
        $body = if ($Mutation -ceq 'usable-noop') {
            'if customer::premium?(customer) { 0 } else { 0 }'
        } else { 'if customer::premium?(customer) { 1000 } else { 0 }' }
        $source = @"
word customer.premium?(customer: Customer) -> Bool {
    effects none
    $predicateDoc
    $predicate
}
test customer.premium?/premium { customer::premium?($premium) => true }
test customer.premium?/regular { customer::premium?($regular) => false }
$predicateSpaced
example customer.premium?/premium { customer::premium?($premium) => true }
word customer.discount-basis-points(customer: Customer) -> Int {
    effects none
    $targetDoc
    $body
}
test customer.discount-basis-points/premium { customer::discount-basis-points($premium) => $premiumRate }
test customer.discount-basis-points/regular { customer::discount-basis-points($regular) => $regularRate }
test customer.discount-basis-points/spaced { customer::discount-basis-points($spaced) => $spacedRate }
example customer.discount-basis-points/premium { customer::discount-basis-points($premium) => $premiumRate }
"@
        return $source
    }

    $discount = if ($Mutation -ceq 'usable-noop') { 'balance' } else { $null }
    $formula = @'
        let quotient = ::divide(amount, 10);
        let remainder = ::subtract(amount, ::multiply(quotient, 10));
        Money::new(::add(::multiply(quotient, 9), ::divide(::multiply(remainder, 9), 10)))
'@
    if ($Mutation -ceq 'wrong-boundary') { $formula = 'Money::new(::divide(::multiply(amount, 9), 10))' }
    if ([string]::IsNullOrWhiteSpace($discount)) { $discount = $formula }
    if ($Mutation -ceq 'usable-noop') {
        $premiumValue = '1050'
        $exampleValue = '1050'
    } else {
        $premiumValue = '945'
        $exampleValue = '945'
    }
    $source = @"
word customer.premium?(customer: Customer) -> Bool {
    effects none
    $predicateDoc
    $predicate
}
test customer.premium?/premium { customer::premium?($premium) => true }
test customer.premium?/regular { customer::premium?($regular) => false }
$predicateSpaced
example customer.premium?/premium { customer::premium?($premium) => true }
word customer.discount-basis-points(customer: Customer) -> Int {
    effects none
    doc "Returns premium discount basis points."
    if customer::premium?(customer) { 1000 } else { 0 }
}
test customer.discount-basis-points/premium { customer::discount-basis-points($premium) => 1000 }
test customer.discount-basis-points/regular { customer::discount-basis-points($regular) => 0 }
example customer.discount-basis-points/premium { customer::discount-basis-points($premium) => 1000 }
word customer.discounted-balance(customer: Customer) -> Money {
    effects none
    $targetDoc
    let balance = customer::balance(customer);
    if customer::premium?(customer) {
        let amount = Money::value(balance);
$discount
    } else { balance }
}
test customer.discounted-balance/premium { Money::value(customer::discounted-balance($premium)) => $premiumValue }
test customer.discounted-balance/regular { Money::value(customer::discounted-balance($regular)) => 1050 }
example customer.discounted-balance/premium { Money::value(customer::discounted-balance($premium)) => $exampleValue }
"@
    return $source
}

function Install-Control([string]$Project,[string]$TaskId,[string]$Mutation) {
    $source = Get-ControlSource $TaskId $Mutation
    $requests = @(
        [ordered]@{op='define';frontend='flow';source=$source},
        [ordered]@{op='test-all'},
        [ordered]@{op='commit';library=$true}
    )
    $responses = Get-StructuredJsonlResponses $Project $requests "$Mutation/$TaskId author and validate"
    $defineOk = [bool]$responses[0].ok
    $tests = if ([bool]$responses[1].ok) { @(Get-Field $responses[1].data 'results') } else { @() }
    $testsPassed = [bool]$responses[1].ok -and $tests.Count -gt 0 -and @($tests | Where-Object { -not [bool](Get-Field $_ 'passed') }).Count -eq 0
    $commitOk = [bool]$responses[2].ok
    Check "$Mutation/$TaskId control definitions parse" $defineOk $responses[0]
    Check "$Mutation/$TaskId attached control tests pass" $testsPassed @{results=$tests;response=$responses[1]}
    $commitExpected = $Mutation -ceq 'missing-documentation' -or $commitOk
    Check "$Mutation/$TaskId library commit succeeds or short documentation rejects it" $commitExpected $responses[2]
    if (-not $defineOk -or -not $testsPassed -or -not $commitExpected) { throw "Control source setup failed for $Mutation/$TaskId." }
    return [ordered]@{
        sourceSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($utf8NoBom.GetBytes($source))).ToLowerInvariant()
        testCount=$tests.Count;tests=$tests;commitOk=$commitOk;responses=$responses
    }
}

function Get-VerifierEvidence([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable -Depth 100
}

function Get-UniqueReportPath([string]$Path) {
    $directory = Split-Path -Parent $Path
    $stem = [IO.Path]::GetFileNameWithoutExtension($Path)
    $extension = [IO.Path]::GetExtension($Path)
    if ([string]::IsNullOrWhiteSpace($extension)) { $extension = '.json' }
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    for ($attempt=0; $attempt -lt 20; $attempt++) {
        $candidate = Join-Path $directory ("$stem-run-$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))-$([Guid]::NewGuid().ToString('N'))$extension")
        if (-not (Test-Path -LiteralPath $candidate)) { return $candidate }
    }
    throw "Could not allocate a new preflight evidence path based on '$Path'."
}

function Write-NewTextFile([string]$Path,[string]$Text) {
    $bytes = $utf8NoBom.GetBytes($Text)
    $stream = [IO.FileStream]::new($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $stream.Write($bytes,0,$bytes.Length); $stream.Flush($true) }
    finally { $stream.Dispose() }
}

try {
    $requestedReportPath = Resolve-RepoPath $EvidencePath
    $reportPath = $requestedReportPath
    $reportPathCollision = Test-Path -LiteralPath $requestedReportPath
    if ($reportPathCollision) {
        $existingReportSha256 = if (Test-Path -LiteralPath $requestedReportPath -PathType Leaf) { Get-Sha256 $requestedReportPath } else { $null }
        $reportPath = Get-UniqueReportPath $requestedReportPath
    }
    Check 'preflight report uses a fresh destination without replacing prior evidence' (-not (Test-Path -LiteralPath $reportPath)) @{requested=$requestedReportPath;selected=$reportPath;priorExists=$reportPathCollision;priorSha256=$existingReportSha256}
    $resolvedCli = Resolve-RepoPath $CliDll
    $resolvedBusiness = Resolve-RepoPath $BusinessDll
    $CliDll = $resolvedCli
    $BusinessDll = $resolvedBusiness
    Check 'fresh CLI assembly exists' (Test-Path -LiteralPath $resolvedCli -PathType Leaf) $resolvedCli
    Check 'fresh Business assembly exists' (Test-Path -LiteralPath $resolvedBusiness -PathType Leaf) $resolvedBusiness
    if (-not (Test-Path -LiteralPath $resolvedCli -PathType Leaf) -or -not (Test-Path -LiteralPath $resolvedBusiness -PathType Leaf)) {
        throw 'Preflight requires the freshly built CLI and Business DLLs.'
    }
    Check 'original seed preparation dependency exists' (Test-Path -LiteralPath $preparerPath -PathType Leaf) $preparerPath
    Check 'versioned verifier exists' (Test-Path -LiteralPath $verifierPath -PathType Leaf) $verifierPath
    Check 'both acceptance corpora exist' ((Test-Path -LiteralPath $oldOraclePath -PathType Leaf) -and (Test-Path -LiteralPath $newOraclePath -PathType Leaf))
    if (-not (Test-Path -LiteralPath $oldOraclePath -PathType Leaf) -or -not (Test-Path -LiteralPath $newOraclePath -PathType Leaf)) { throw 'Cannot compare the 001 and 002 acceptance corpora.' }

    $oldOracle = Read-Oracle $oldOraclePath
    $newOracle = Read-Oracle $newOraclePath
    $oldPayload = Get-CasePayload $oldOracle
    $newPayload = Get-CasePayload $newOracle
    $oldPayloadJson = Get-CanonicalJson @($oldPayload)
    $newPayloadJson = Get-CanonicalJson @($newPayload)
    $oldPayloadHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($utf8NoBom.GetBytes($oldPayloadJson))).ToLowerInvariant()
    $newPayloadHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($utf8NoBom.GetBytes($newPayloadJson))).ToLowerInvariant()
    $predecessorHash = Get-Sha256 $oldOraclePath
    $declaredPredecessor = [string]$newOracle.predecessorSha256
    $payloadsMatch = $oldPayloadJson -ceq $newPayloadJson
    $createdAtMatches = [string]$oldOracle.defaults.createdAt -ceq [string]$newOracle.defaults.createdAt -and [string]$newOracle.defaults.createdAt -ceq '2026-01-01T12:00:00.0000000+00:00'
    $identityOk = [string]$oldOracle.studyId -ceq 'business-policy-001' -and [string]$newOracle.studyId -ceq 'business-policy-help-002'
    $predecessorOk = $declaredPredecessor.ToLowerInvariant() -ceq $predecessorHash
    $caseCount = @($newPayload | ForEach-Object { @($_.cases).Count } | Measure-Object -Sum).Sum
    $oracleComparison = [ordered]@{
        passed=($payloadsMatch -and $createdAtMatches -and $identityOk -and $predecessorOk)
        identity=[ordered]@{old=$oldOracle.studyId;new=$newOracle.studyId}
        predecessor=[ordered]@{declared=$declaredPredecessor;actual=$predecessorHash;passed=$predecessorOk}
        defaults=[ordered]@{oldCreatedAt=$oldOracle.defaults.createdAt;newCreatedAt=$newOracle.defaults.createdAt;passed=$createdAtMatches}
        casePayloads=[ordered]@{taskCount=$newPayload.Count;caseCount=$caseCount;oldSha256=$oldPayloadHash;newSha256=$newPayloadHash;equal=$payloadsMatch;tasks=$newPayload}
    }
    Check 'study identity is versioned and predecessor hash matches untouched 001 bytes' ($identityOk -and $predecessorOk) $oracleComparison.predecessor
    Check 'createdAt remains the exact canonical JSON string' $createdAtMatches $oracleComparison.defaults
    Check 'all Flat S06/S07 case payloads match the old corpus after identity removal' $payloadsMatch @{oldSha256=$oldPayloadHash;newSha256=$newPayloadHash;taskCount=$newPayload.Count;caseCount=$caseCount}

    [IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
    $preparedOutput = @(& $preparerPath -Mode flat -TaskId S06 -CliDll $resolvedCli -BusinessDll $resolvedBusiness -LocalRoot $preparedLocalRoot -RunRoot $preparedRunRoot)
    $lastExitVariable = Get-Variable -Name LASTEXITCODE -ErrorAction SilentlyContinue
    $preparerExitCode = if ($null -ne $lastExitVariable) { [int]$lastExitVariable.Value } else { 0 }
    $preparedSeedProject = Join-Path $preparedLocalRoot 'seeds/flat/project'
    $preparedStatePath = Join-Path $preparedRunRoot 'flat-S06/starting-state.json'
    $preparedSeedState = if (Test-Path -LiteralPath $preparedStatePath -PathType Leaf) { Get-Content -LiteralPath $preparedStatePath -Raw | ConvertFrom-Json -AsHashtable -Depth 100 } else { $null }
    $seedFiles = if (Test-Path -LiteralPath $preparedSeedProject -PathType Container) { @(Get-ChildItem -LiteralPath $preparedSeedProject -Recurse -Force -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }) } else { @() }
    Check 'fresh isolated schema-only Flat seed is prepared by the original preparation script' ($preparerExitCode -eq 0 -and (Test-Path -LiteralPath $preparedSeedProject -PathType Container) -and $seedFiles.Count -gt 0 -and $null -ne $preparedSeedState -and [string]$preparedSeedState.mode -ceq 'flat' -and [string]$preparedSeedState.taskId -ceq 'S06' -and [int]$preparedSeedState.sequenceIndex -eq 2) @{localRoot=$preparedLocalRoot;runRoot=$preparedRunRoot;seedProject=$preparedSeedProject;startingState=$preparedStatePath;preparerOutput=@($preparedOutput | ForEach-Object { [string]$_ })}
    if ($preparerExitCode -ne 0 -or -not (Test-Path -LiteralPath $preparedSeedProject -PathType Container) -or $seedFiles.Count -eq 0 -or $null -eq $preparedSeedState) {
        throw 'Original preparation did not produce an isolated Flat schema-only seed and starting state.'
    }
    $resolvedSeed = [IO.Path]::GetFullPath($preparedSeedProject)

    foreach ($taskId in $TaskIds) {
        $expectedCaseCount = if ($taskId -ceq 'S06') { 10 } else { 54 }
        foreach ($mutation in @('correct','missing-documentation','usable-noop','wrong-boundary')) {
            $project = Join-Path $fixtureRoot "$mutation-$taskId"
            Copy-Project $resolvedSeed $project
            $selfValidation = Install-Control $project $taskId $mutation
            $resultPath = Join-Path $fixtureRoot "$mutation-$taskId-verifier.json"
            $verifierOutput = @(& pwsh -NoProfile -File $verifierPath -Mode flat -TaskId $taskId -ProjectPath $project -CliDll $resolvedCli -StartingProjectPath $resolvedSeed -EvidencePath $resultPath 2>&1 | ForEach-Object { $_.ToString() })
            $verifierExit = $LASTEXITCODE
            $result = Get-VerifierEvidence $resultPath
            $runs.Add([ordered]@{control=$mutation;taskId=$taskId;selfValidation=$selfValidation;verifierExitCode=$verifierExit;verifierOutput=$verifierOutput;acceptance=$result})
            Check "$mutation/$taskId verifier evidence exists" ($null -ne $result) $resultPath
            if ($null -eq $result) { continue }
            $casesExecuted = [int]$result.behavior.casesExecuted
            $casesRecorded = [int]$result.behavior.casesRecorded
            $failedCases = @($result.behavior.cases | Where-Object { -not [bool]$_.passed })
            $fullCorpus = $casesExecuted -eq $expectedCaseCount -and $casesRecorded -eq $expectedCaseCount
            $expectedOutcome = switch ($mutation) {
                'correct' { [bool]$result.passed -and [bool]$result.metadataPassed -and [bool]$result.behaviorPassed -and $fullCorpus -and $verifierExit -eq 0 }
                'missing-documentation' { -not [bool]$result.passed -and -not [bool]$result.metadataPassed -and [bool]$result.behaviorPassed -and $fullCorpus -and @($result.metadataChecks | Where-Object { $_.name -ceq 'target has meaningful documentation' -and -not [bool]$_.passed }).Count -eq 1 -and $verifierExit -ne 0 }
                'usable-noop' { -not [bool]$result.passed -and [bool]$result.metadataPassed -and -not [bool]$result.behaviorPassed -and $fullCorpus -and $failedCases.Count -gt 0 -and $verifierExit -ne 0 }
                'wrong-boundary' {
                    $diagnosticOk = $false
                    foreach ($caseResult in $failedCases) {
                        if (-not [string]::IsNullOrWhiteSpace([string]$caseResult.diagnostic) -and $null -ne $caseResult.actual) { $diagnosticOk = $true; break }
                    }
                    -not [bool]$result.passed -and [bool]$result.metadataPassed -and -not [bool]$result.behaviorPassed -and $fullCorpus -and $failedCases.Count -gt 0 -and $diagnosticOk -and $verifierExit -ne 0
                }
            }
            Check "$mutation/$taskId outcome separates metadata and behavior with all expected cases" $expectedOutcome @{
                expectedCases=$expectedCaseCount;casesExecuted=$casesExecuted;casesRecorded=$casesRecorded
                passed=$result.passed;metadataPassed=$result.metadataPassed;behaviorPassed=$result.behaviorPassed
                failedCaseIds=@($failedCases | ForEach-Object { [string]$_.id })
            }
            if ($mutation -ceq 'wrong-boundary') {
                $failedWithProtocolDiagnostic = @($failedCases | Where-Object { -not [bool]$_.responseOk -and -not [string]::IsNullOrWhiteSpace([string]$_.diagnostic) -and [string]$_.diagnostic -cne 'Protocol returned ok=false without error.message/text.' })
                $failedWithStructuredActual = @($failedCases | Where-Object { $null -ne $_.actual -and $_.actual.structured })
                Check "$mutation/$taskId preserves structured wrong-boundary diagnostics" ($failedWithProtocolDiagnostic.Count -gt 0 -or $failedWithStructuredActual.Count -gt 0) @{protocolDiagnosticCount=$failedWithProtocolDiagnostic.Count;structuredActualCount=$failedWithStructuredActual.Count;examples=@($failedCases | Select-Object -First 3)}
            }
        }

        $missingTargetProject = Join-Path $fixtureRoot "missing-target-$taskId"
        Copy-Project $resolvedSeed $missingTargetProject
        $missingTargetPath = Join-Path $fixtureRoot "missing-target-$taskId-verifier.json"
        $missingTargetOutput = @(& pwsh -NoProfile -File $verifierPath -Mode flat -TaskId $taskId -ProjectPath $missingTargetProject -CliDll $resolvedCli -StartingProjectPath $resolvedSeed -EvidencePath $missingTargetPath 2>&1 | ForEach-Object { $_.ToString() })
        $missingTargetExit = $LASTEXITCODE
        $missingTargetResult = Get-VerifierEvidence $missingTargetPath
        $runs.Add([ordered]@{control='missing-target';taskId=$taskId;verifierExitCode=$missingTargetExit;verifierOutput=$missingTargetOutput;acceptance=$missingTargetResult})
        $missingTargetOk = $null -ne $missingTargetResult -and -not [bool]$missingTargetResult.passed -and -not [bool]$missingTargetResult.behaviorPassed -and [int]$missingTargetResult.behavior.casesExecuted -eq 0 -and -not [string]::IsNullOrWhiteSpace([string]$missingTargetResult.safetyStop) -and $missingTargetExit -ne 0
        Check "missing-target/$taskId remains a pre-oracle safety stop" $missingTargetOk $missingTargetResult

        $pinControlRoot = Join-Path $fixtureRoot "required-pin-$taskId"
        $pinControlProject = Join-Path $pinControlRoot 'actor'
        $pinControlRun = Join-Path $pinControlRoot 'run'
        $pinControlStarting = Join-Path $pinControlRun 'starting-project'
        Copy-Project $resolvedSeed $pinControlProject
        Copy-Project $resolvedSeed $pinControlStarting
        [IO.Directory]::CreateDirectory($pinControlRun) | Out-Null
        $pinState = Get-Content -LiteralPath $preparedStatePath -Raw | ConvertFrom-Json -AsHashtable -Depth 100
        $pinState.studyId = 'business-policy-help-002'
        $pinState.taskId = $taskId
        $pinState.sequenceIndex = if ($taskId -ceq 'S06') { 2 } else { 3 }
        $pinState.project.path = $pinControlStarting
        $pinState.actor.projectPath = $pinControlProject
        $pinState.previousAcceptance = $null
        $pinStateJson = ConvertTo-Json -InputObject $pinState -Depth 100
        [IO.File]::WriteAllText((Join-Path $pinControlRun 'starting-state.json'),$pinStateJson + [Environment]::NewLine,$utf8NoBom)
        $missingPinEvidence = Join-Path $fixtureRoot "required-pin-$taskId-verifier.json"
        $missingPinOutput = @(& pwsh -NoProfile -File $verifierPath -Mode flat -TaskId $taskId -ProjectPath $pinControlProject -CliDll $resolvedCli -StartingProjectPath $pinControlStarting -EvidencePath $missingPinEvidence -RequireFrozenPin 2>&1 | ForEach-Object { $_.ToString() })
        $missingPinExit = $LASTEXITCODE
        $missingPinResult = Get-VerifierEvidence $missingPinEvidence
        $runs.Add([ordered]@{control='required-pin-missing';taskId=$taskId;verifierExitCode=$missingPinExit;verifierOutput=$missingPinOutput;acceptance=$missingPinResult})
        $missingPinCheckFailed = $null -ne $missingPinResult -and @($missingPinResult.checks | Where-Object { $_.name -ceq 'required frozen prelaunch pin exists' -and -not [bool]$_.passed }).Count -eq 1
        $missingPinNoExecution = $null -ne $missingPinResult -and @($missingPinResult.jsonlSessions).Count -eq 0 -and @($missingPinResult.processRuns).Count -eq 0 -and [int]$missingPinResult.behavior.casesExecuted -eq 0 -and [int]$missingPinResult.behavior.casesRecorded -eq 0
        $startingStateChecksPassed = $null -ne $missingPinResult -and @($missingPinResult.metadataChecks | Where-Object { $_.name -match '^starting-state|^prepared actor inventory' -and -not [bool]$_.passed }).Count -eq 0 -and @($missingPinResult.metadataChecks | Where-Object { $_.name -ceq 'starting-state schema and study match' -and [bool]$_.passed }).Count -eq 1
        Check "required-pin-missing/$taskId blocks all CLI and oracle execution after valid starting-state checks" ($missingPinExit -ne 0 -and $missingPinCheckFailed -and $missingPinNoExecution -and $startingStateChecksPassed -and -not [bool]$missingPinResult.passed -and -not [string]::IsNullOrWhiteSpace([string]$missingPinResult.safetyStop)) @{validStartingState=$startingStateChecksPassed;requiredPinCheckFailed=$missingPinCheckFailed;jsonlSessionCount=$(if ($null -ne $missingPinResult) { @($missingPinResult.jsonlSessions).Count } else { $null });processRunCount=$(if ($null -ne $missingPinResult) { @($missingPinResult.processRuns).Count } else { $null });oracleCases=$(if ($null -ne $missingPinResult) { $missingPinResult.behavior } else { $null })}

        $collisionProject = Join-Path $fixtureRoot "existing-evidence-$taskId"
        Copy-Project $resolvedSeed $collisionProject
        $sentinelPath = Join-Path $fixtureRoot "existing-evidence-$taskId.json"
        $sentinelText = "prior evidence must remain byte-identical for $taskId`n"
        [IO.File]::WriteAllText($sentinelPath,$sentinelText,$utf8NoBom)
        $sentinelHash = Get-Sha256 $sentinelPath
        $collisionOutput = @(& pwsh -NoProfile -File $verifierPath -Mode flat -TaskId $taskId -ProjectPath $collisionProject -CliDll $resolvedCli -EvidencePath $sentinelPath 2>&1 | ForEach-Object { $_.ToString() })
        $collisionExit = $LASTEXITCODE
        $collisionReportedPath = $null
        foreach ($line in $collisionOutput) { if ([string]$line -match 'Evidence:\s(?<path>.+?\.json)(?:\.|$)') { $collisionReportedPath = $Matches.path; break } }
        $collisionResult = if (-not [string]::IsNullOrWhiteSpace($collisionReportedPath)) { Get-VerifierEvidence $collisionReportedPath } else { $null }
        $sentinelPreserved = (Test-Path -LiteralPath $sentinelPath -PathType Leaf) -and (Get-Sha256 $sentinelPath) -ceq $sentinelHash
        $runs.Add([ordered]@{control='existing-evidence-path';taskId=$taskId;verifierExitCode=$collisionExit;verifierOutput=$collisionOutput;sentinelPath=$sentinelPath;sentinelSha256=$sentinelHash;acceptance=$collisionResult})
        $collisionNoExecution = $null -ne $collisionResult -and @($collisionResult.jsonlSessions).Count -eq 0 -and @($collisionResult.processRuns).Count -eq 0 -and [int]$collisionResult.behavior.casesExecuted -eq 0 -and [int]$collisionResult.behavior.casesRecorded -eq 0
        $collisionRerouted = $null -ne $collisionResult -and [bool]$collisionResult.evidencePathCollision -and [IO.Path]::GetFullPath([string]$collisionResult.evidencePath) -ine [IO.Path]::GetFullPath($sentinelPath) -and [string]$collisionResult.requestedEvidencePath -ceq [IO.Path]::GetFullPath($sentinelPath)
        Check "existing-evidence/$taskId preserves prior bytes and blocks all CLI/oracle execution" ($collisionExit -ne 0 -and $sentinelPreserved -and $collisionRerouted -and $collisionNoExecution -and -not [bool]$collisionResult.passed -and -not [string]::IsNullOrWhiteSpace([string]$collisionResult.safetyStop)) @{sentinelPreserved=$sentinelPreserved;collisionRerouted=$collisionRerouted;noExecution=$collisionNoExecution;sentinelSha256=$sentinelHash;selectedEvidencePath=$(if ($null -ne $collisionResult) { $collisionResult.evidencePath } else { $collisionReportedPath })}
    }
    $passed = @($checks | Where-Object { -not [bool]$_.passed }).Count -eq 0 -and [bool]$oracleComparison.passed
} catch {
    $failure = $_.Exception.Message
} finally {
    if ([string]::IsNullOrWhiteSpace($reportPath)) {
        try { $reportPath = Resolve-RepoPath $EvidencePath } catch { $reportPath = Join-Path $repo 'experiments/AgentLang.SubagentTrials/business-policy-help-002/evidence/078-verifier-focused.json' }
        if (Test-Path -LiteralPath $reportPath) { $reportPath = Get-UniqueReportPath $reportPath }
    }
    $existingReportUnchanged = $true
    if ($reportPathCollision) {
        if ($null -eq $existingReportSha256) { $existingReportUnchanged = Test-Path -LiteralPath $requestedReportPath -PathType Container }
        else { $existingReportUnchanged = (Test-Path -LiteralPath $requestedReportPath -PathType Leaf) -and (Get-Sha256 $requestedReportPath) -ceq $existingReportSha256 }
        Check 'pre-existing preflight evidence remains unchanged' $existingReportUnchanged @{path=$requestedReportPath;expectedSha256=$existingReportSha256;actualSha256=$(if (Test-Path -LiteralPath $requestedReportPath -PathType Leaf) { Get-Sha256 $requestedReportPath } else { $null })}
    }
    $passed = $passed -and @($checks | Where-Object { -not [bool]$_.passed }).Count -eq 0
    if (-not $passed -and [string]::IsNullOrWhiteSpace($failure)) {
        $failedCheckNames = @($checks | Where-Object { -not [bool]$_.passed } | ForEach-Object { [string]$_.name })
        $failure = "Failed preflight checks: $($failedCheckNames -join '; ')"
    }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($reportPath)) | Out-Null
    $dirty = -not [string]::IsNullOrWhiteSpace((& git -C $repo status --porcelain | Out-String))
    $report = [ordered]@{
        schemaVersion=1;studyId='business-policy-help-002';passed=$passed;failure=$failure
        requestedEvidencePath=$requestedReportPath;evidencePath=$reportPath;evidencePathCollision=$reportPathCollision;preexistingEvidenceUnchanged=$existingReportUnchanged;preexistingEvidenceSha256=$existingReportSha256
        checks=$checks.ToArray();oracleComparison=$oracleComparison;runs=$runs.ToArray()
        sourceRevision=(& git -C $repo rev-parse HEAD | Out-String).Trim();dirty=$dirty
        cli=[ordered]@{path=$CliDll;sha256=$(if (-not [string]::IsNullOrWhiteSpace([string]$resolvedCli) -and (Test-Path -LiteralPath $resolvedCli -PathType Leaf)) { Get-Sha256 $resolvedCli } else { $null })}
        business=[ordered]@{path=$BusinessDll;sha256=$(if (-not [string]::IsNullOrWhiteSpace([string]$resolvedBusiness) -and (Test-Path -LiteralPath $resolvedBusiness -PathType Leaf)) { Get-Sha256 $resolvedBusiness } else { $null })}
        seed=[ordered]@{path=$preparedSeedProject;sha256=$(if (-not [string]::IsNullOrWhiteSpace([string]$preparedSeedProject) -and (Test-Path -LiteralPath $preparedSeedProject -PathType Container)) { Get-DirectoryHash $preparedSeedProject } else { $null });preparationStartingState=$preparedStatePath}
        verifierSha256=$(if (Test-Path -LiteralPath $verifierPath -PathType Leaf) { Get-Sha256 $verifierPath } else { $null })
        preflightSha256=$(if (Test-Path -LiteralPath $PSCommandPath -PathType Leaf) { Get-Sha256 $PSCommandPath } else { $null })
        processRuns=$processRuns.ToArray()
    }
    try { Write-NewTextFile $reportPath ((ConvertTo-Json -InputObject $report -Depth 100) + [Environment]::NewLine) }
    catch {
        $failure = "Could not write preflight evidence without overwriting: $($_.Exception.Message)"
        $passed = $false
        $reportPath = Get-UniqueReportPath $reportPath
        $report.passed = $false
        $report.failure = $failure
        $report.evidencePath = $reportPath
        Write-NewTextFile $reportPath ((ConvertTo-Json -InputObject $report -Depth 100) + [Environment]::NewLine)
    }
    if (Test-Path -LiteralPath $fixtureRoot -PathType Container) {
        $resolvedRoot = [IO.Path]::GetFullPath($fixtureRoot)
        $allowedRoot = [IO.Path]::GetFullPath($fixtureParent).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
        if (-not $resolvedRoot.StartsWith($allowedRoot,[StringComparison]::OrdinalIgnoreCase) -or -not [IO.Path]::GetFileName($resolvedRoot).StartsWith('authoring-help-preflight-',[StringComparison]::Ordinal)) { throw 'Unsafe preflight fixture cleanup path.' }
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
    }
}

if (-not $passed) {
    [Console]::Error.WriteLine("Authoring-help focused preflight failed. Evidence: $reportPath. $failure")
    exit 1
}
Write-Output "Authoring-help focused preflight passed $($checks.Count) checks across $($runs.Count) verifier outcomes. Evidence: $reportPath"
