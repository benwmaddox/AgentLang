#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$CliDll = 'src/AgentLang.Cli/bin/Release/net9.0/AgentLang.Cli.dll',
    [string]$BusinessDll = 'experiments/AgentLang.Business/bin/Release/net9.0/AgentLang.Business.dll',
    [string]$EvidencePath,
    [string]$CandidateEvidencePath,
    [switch]$RequireFrozenControls
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$studyId = 'business-policy-retention-004'
$studyPath = 'experiments/AgentLang.SubagentTrials/business-policy-retention-004'
$studyRoot = Join-Path $repo $studyPath
$baselineRoot = Join-Path $studyRoot 'artifacts/baselines'
$verifierPath = Join-Path $repo 'scripts/Verify-RetentionTrialV4.ps1'
$preparerPath = Join-Path $repo 'scripts/Prepare-RetentionTrialV4.ps1'
$fixtureParent = Join-Path $repo '.agentlang/business-policy-retention-004/control-fixtures'
$fixtureRoot = Join-Path $fixtureParent 'retention-preflight-control-slot'
$preflightRunsRoot = Join-Path $repo '.agentlang/business-policy-retention-004/runs'
$m00ControlRoot = Join-Path $fixtureParent 'M00'
$pinRejectRunId = 'M00'
$pinRejectOutputRoot = Join-Path $m00ControlRoot 'runs/M00'
$pinRejectOutputPath = Join-Path $pinRejectOutputRoot 'acceptance.json'
$evidenceRoot = Join-Path $repo '.agentlang/business-policy-retention-004/evidence'
$evidenceId = [Guid]::NewGuid().ToString('N')
$m00PredecessorRoot = Join-Path $m00ControlRoot "predecessor-controls/$evidenceId"
$pinRejectOutputMarker = Join-Path $pinRejectOutputRoot ".preflight-owner-$evidenceId"
$rawEvidenceRoot = Join-Path $evidenceRoot "004-preflight-candidate-$evidenceId"
$checks = [Collections.Generic.List[object]]::new()
$processRuns = [Collections.Generic.List[object]]::new()
$baselineResults = [Collections.Generic.List[object]]::new()
$controlResults = [Collections.Generic.List[object]]::new()
$predecessorControlResults = [Collections.Generic.List[object]]::new()
$pendingControls = [Collections.Generic.List[object]]::new()
$passed = $false
$failure = $null
$resolvedCli = $null
$resolvedBusiness = $null
$reportPath = $null
$sourceRevision = $null
$preflightSha256 = $null
$sourceHashInventory = @()
$oracle = $null
$predecessorOracle = $null
$design = $null
$oracleRows = [Collections.Generic.List[object]]::new()
$actualCells = @()
$primerEqual = $false
$cliHash = $null
$businessHash = $null
$bootstrapEvidence = $null
$baselineState = $null
$dirty = $true
$controlSlotOwned = $false
$pinRejectOutputOwned = $false
$pinRejectActorOwned = $false
$pinRejectOutputCleanupPassed = $null
$cleanupPassed = $null
$candidatePassed = $false
$frozenPhaseStatus = 'pending'
$frozenPhaseFailure = $null
$exitSuccess = $false
$reportPath = $null
$requestedEvidencePath = $EvidencePath
$startedUtc = [DateTime]::UtcNow.ToString('O')
$utf8NoBom = [Text.UTF8Encoding]::new($false)
$nl = [Environment]::NewLine
$script:liveChecks = [Collections.Generic.List[object]]::new()
$expectedCounts = @{
    flat = [ordered]@{words=0;types=6;tests=0}
    rich = [ordered]@{words=53;types=31;tests=151}
}
$forbiddenTargets = @('customer.premium?','customer.discount-basis-points','customer.discounted-balance')
$runIndexMap = @{
    B1 = @{ flat=@('R01','R02'); retained=@('R03','R04'); 'reset-rich'=@('R05','R06') }
    B2 = @{ 'reset-rich'=@('R07','R08'); flat=@('R09','R10'); retained=@('R11','R12') }
}

function Get-Field($Object,[string]$Name) {
    if ($null -eq $Object) { return $null }
    if ($Object -is [Collections.IDictionary]) { return $Object[$Name] }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}
function Get-Sha256([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Get-CanonicalJson($Value) { return ConvertTo-Json -InputObject $Value -Depth 100 -Compress }
function Resolve-RepoPath([string]$Path) {
    if ([IO.Path]::IsPathFullyQualified($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath($Path,$repo)
}
function Get-Inventory([string]$Root) {
    $resolved=[IO.Path]::GetFullPath($Root)
    $sorted=[Collections.Generic.SortedDictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach($file in Get-ChildItem -LiteralPath $resolved -Recurse -Force -File) {
        $rel=[IO.Path]::GetRelativePath($resolved,$file.FullName).Replace('\','/')
        if($rel.Split('/') -contains 'bin' -or $rel.Split('/') -contains 'obj'){continue}
        $sorted.Add($rel,[ordered]@{path=$rel;bytes=[long]$file.Length;sha256=Get-Sha256 $file.FullName})
    }
    $rows=[object[]]::new($sorted.Count);$i=0
    foreach($row in $sorted.Values){$rows[$i]=$row;$i++}
    return $rows
}
function Get-TreeHash([string]$Root) {
    $rows=Get-Inventory $Root
    $bytes=$utf8NoBom.GetBytes((Get-CanonicalJson @($rows)))
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}
function Add-Check([string]$Name,[bool]$Condition,$Details=$null) {
    $entry=[ordered]@{name=$Name;passed=$Condition}
    if($null -ne $Details){$entry.details=$Details}
    $checks.Add($entry)
}
function Assert-Check([string]$Name,[bool]$Condition,$Details=$null) {
    Add-Check $Name $Condition $Details
    if(-not $Condition){throw "Preflight gate failed before mutation: $Name"}
}
function Get-UniqueEvidencePath([string]$Reason) {
    $dir=Join-Path $repo '.agentlang/business-policy-retention-004/evidence'
    [IO.Directory]::CreateDirectory($dir)|Out-Null
    for($i=0;$i -lt 20;$i++){
        $path=Join-Path $dir ("004-preflight-candidate-$Reason-$([Guid]::NewGuid().ToString('N')).json")
        if(-not(Test-Path -LiteralPath $path)){return $path}
    }
    throw 'Could not allocate unique preflight evidence.'
}
function Write-NewTextFile([string]$Path,[string]$Content) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))|Out-Null
    $stream=[IO.FileStream]::new($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try{$bytes=$utf8NoBom.GetBytes($Content);$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
}
function Copy-Project([string]$Source,[string]$Destination) {
    if(Test-Path -LiteralPath $Destination){throw "Scratch destination already exists: $Destination"}
    [IO.Directory]::CreateDirectory($Destination)|Out-Null
    foreach($file in Get-ChildItem -LiteralPath $Source -Recurse -Force -File){
        $rel=[IO.Path]::GetRelativePath($Source,$file.FullName)
        if($rel -match '(^|[\\/])(\.git|bin|obj)([\\/]|$)'){continue}
        $target=Join-Path $Destination $rel
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))|Out-Null
        [IO.File]::Copy($file.FullName,$target,$false)
    }
}
function Get-NominalTypes([object[]]$Words) {
    $builtins=@('Bool','Int','Float','String','Unit','List','Option','Result')
    $set=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($word in $Words){
        foreach($signature in @((Get-Field $word 'inputs'))+@((Get-Field $word 'outputs'))){
            foreach($m in [regex]::Matches([string]$signature,'[A-Z][A-Za-z0-9_]*')){
                if($m.Value -cnotin $builtins){[void]$set.Add($m.Value)}
            }
        }
    }
    return @($set|Sort-Object)
}
function Get-SourceTypes($Manifest) {
    $set=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($input in @((Get-Field $Manifest 'sourceInputs'))){
        $path=Resolve-RepoPath ([string](Get-Field $input 'path'))
        if((Get-Sha256 $path) -cne ([string](Get-Field $input 'sha256')).ToLowerInvariant()){throw "Source input hash mismatch: $path"}
        $source=Get-Content -LiteralPath $path -Raw
        foreach($m in [regex]::Matches($source,'(?m)^\s*(?:type|record)\s+([A-Za-z][A-Za-z0-9_]*)\b')){[void]$set.Add($m.Groups[1].Value)}
    }
    return @($set|Sort-Object)
}
function Read-Report([string]$Path) {
    if(-not(Test-Path -LiteralPath $Path -PathType Leaf)){return $null}
    return Get-Content -LiteralPath $Path -Raw|ConvertFrom-Json -AsHashtable -Depth 100
}

function Get-SourceHashInventory {
    $paths = @(
        'scripts/Prepare-RetentionTrialV4.ps1','scripts/Freeze-RetentionTrialV4.ps1','scripts/Verify-RetentionTrialV4.ps1',
        'scripts/Verify-RetentionPreflightV4.ps1','scripts/Audit-RetentionTraceV4.ps1','scripts/Prepare-BusinessPolicyTrial.ps1',
        'scripts/Start-SubagentTrialHostV2.ps1','scripts/Audit-SubagentTrialTerminationV2.ps1',
        'experiments/AgentLang.SubagentTrials/business-policy-retention-004/acceptance.json',
        'experiments/AgentLang.SubagentTrials/business-policy-retention-004/design.json',
        'experiments/AgentLang.SubagentTrials/business-policy-retention-004/language-primer.md','experiments/AgentLang.SubagentTrials/business-policy-retention-004/protocol.md',
        'experiments/AgentLang.SubagentTrials/business-policy-retention-004/artifacts/flat-customer.agent',
        'experiments/AgentLang.SubagentTrials/business-policy-001/acceptance.json',
        'experiments/AgentLang.SubagentTrials/business-policy-help-002/acceptance.json',
        'experiments/AgentLang.SubagentTrials/business-policy-help-002/language-primer.md',
        'experiments/AgentLang.Benchmarks/task-bank/public/S01.json',
        'experiments/AgentLang.Benchmarks/task-bank/public/S07.json',
        'examples/business-values.agent','examples/business-store.agent','examples/business-state.agent',
        'examples/business-subscriptions.agent','examples/business-invoices.agent','examples/business-payments-email.agent'
    )
    [Array]::Sort($paths,[StringComparer]::Ordinal)
    $rows = [Collections.Generic.List[object]]::new()
    foreach($relative in $paths){
        $path=Resolve-RepoPath $relative
        if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw "Frozen source input is missing: $relative"}
        $rows.Add([ordered]@{path=$relative;sha256=Get-Sha256 $path})
    }
    return ,$rows.ToArray()
}

function Assert-JsonNoDuplicateProperties([System.Text.Json.JsonElement]$Element,[string]$Label) {
    if($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Object){
        $names=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach($property in $Element.EnumerateObject()){
            if(-not $names.Add($property.Name)){throw "$Label has duplicate JSON property '$($property.Name)'."}
            Assert-JsonNoDuplicateProperties $property.Value $Label
        }
    } elseif($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Array){
        foreach($item in $Element.EnumerateArray()){Assert-JsonNoDuplicateProperties $item $Label}
    }
}

function Get-StrictJsonStringPath([System.Text.Json.JsonElement]$Root,[object[]]$Segments,[string]$Label) {
    $element=$Root
    foreach($segment in $Segments){
        if($segment -is [int] -or $segment -is [long]){
            $index=[int]$segment
            if($element.ValueKind -ne [System.Text.Json.JsonValueKind]::Array -or $index -lt 0 -or $index -ge $element.GetArrayLength()){
                throw "$Label has no JSON array element at index $index."
            }
            $element=$element[$index]
            continue
        }
        if($element.ValueKind -ne [System.Text.Json.JsonValueKind]::Object){throw "$Label is not a JSON object at property '$segment'."}
        $found=$false
        $nextElement=$null
        foreach($property in $element.EnumerateObject()){
            if([StringComparer]::Ordinal.Equals($property.Name,[string]$segment)){
                if($found){throw "$Label has an ambiguous duplicate property '$segment'."}
                $found=$true
                $nextElement=$property.Value
            }
        }
        if(-not $found){throw "$Label is missing JSON property '$segment'."}
        $element=$nextElement
    }
    if($element.ValueKind -ne [System.Text.Json.JsonValueKind]::String){throw "$Label must be a JSON string."}
    return $element.GetString()
}

function Read-StrictReport([string]$Path,[hashtable]$RawStringPaths=$null) {
    $bytes=[IO.File]::ReadAllBytes($Path)
    if($bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191){throw "JSON input has a UTF-8 BOM: $Path"}
    $text=[Text.UTF8Encoding]::new($false,$true).GetString($bytes)
    $document=[System.Text.Json.JsonDocument]::Parse($text)
    try{
        Assert-JsonNoDuplicateProperties $document.RootElement $Path
        if($null -ne $RawStringPaths){
            foreach($key in @($RawStringPaths.Keys)){
                $RawStringPaths[$key]=Get-StrictJsonStringPath -Root $document.RootElement -Segments @($RawStringPaths[$key]) -Label "$Path $key"
            }
        }
    }finally{$document.Dispose()}
    return ConvertFrom-Json -InputObject $text -AsHashtable -Depth 100
}

function Assert-NoReparseTree([string]$Path) {
    $root = Get-Item -LiteralPath $Path -Force
    if (($root.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point in control input: $Path" }
    foreach ($item in Get-ChildItem -LiteralPath $Path -Force -Recurse) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point in control tree: $($item.FullName)" }
    }
}

function Test-ContainedPath([string]$Path,[string]$Root) {
    $candidate = [IO.Path]::GetFullPath($Path)
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd([char[]]@('\','/')) + [IO.Path]::DirectorySeparatorChar
    return $candidate.StartsWith($rootPath,[StringComparison]::OrdinalIgnoreCase)
}

function Write-RawControlText([string]$Stem,[string]$Suffix,[string]$Content) {
    $safeStem = [regex]::Replace($Stem,'[^A-Za-z0-9_.-]','-')
    $path = Join-Path $rawEvidenceRoot "$safeStem-$Suffix.txt"
    if (Test-Path -LiteralPath $path) { throw "Refusing to replace raw control output: $path" }
    Write-NewTextFile $path $Content
    return [ordered]@{path=$path;sha256=Get-Sha256 $path;bytes=(Get-Item -LiteralPath $path).Length}
}

function Invoke-CapturedProcess([string]$Name,[string]$FileName,[string[]]$Arguments,[string[]]$InputLines=@(),[int]$TimeoutMilliseconds=180000) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.WorkingDirectory = $repo
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $startInfo.ArgumentList.Add([string]$argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $startedAt = [DateTimeOffset]::UtcNow
    if (-not $process.Start()) { throw "Could not start $Name process '$FileName'." }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    try {
        foreach ($line in $InputLines) { $process.StandardInput.WriteLine($line) }
    } finally { $process.StandardInput.Close() }
    $completed = $process.WaitForExit($TimeoutMilliseconds)
    if (-not $completed) {
        try { $process.Kill($true) } catch { }
        [void]$process.WaitForExit(5000)
    }
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    $finishedAt = [DateTimeOffset]::UtcNow
    $exitCode = if ($completed) { $process.ExitCode } else { $null }
    $stdoutRaw = Write-RawControlText $Name 'stdout' $stdout
    $stderrRaw = Write-RawControlText $Name 'stderr' $stderr
    $record = [ordered]@{
        name=$Name;fileName=$FileName;arguments=@($Arguments);inputLineCount=@($InputLines).Count
        startedUtc=$startedAt.ToString('o');finishedUtc=$finishedAt.ToString('o')
        elapsedMilliseconds=[long]($finishedAt-$startedAt).TotalMilliseconds
        timedOut=(-not $completed);exitCode=$exitCode
        stdout=$stdoutRaw;stderr=$stderrRaw
    }
    $processRuns.Add($record)
    $process.Dispose()
    if (-not $completed) { throw "$Name timed out after $TimeoutMilliseconds ms." }
    return [pscustomobject][ordered]@{exitCode=$exitCode;stdout=$stdout;stderr=$stderr;record=$record}
}

function Invoke-CliRequests([string]$Project,[object[]]$Requests,[string]$Label) {
    $lines = @($Requests | ForEach-Object { ConvertTo-Json -InputObject $_ -Depth 100 -Compress })
    $captured = Invoke-CapturedProcess $Label 'dotnet' @($resolvedCli,'--project',$Project,'--jsonl') $lines
    $responseLines = @($captured.stdout -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($responseLines.Count -ne $Requests.Count) { throw "$Label returned $($responseLines.Count) responses for $($Requests.Count) requests." }
    $responses = @($responseLines | ForEach-Object { ConvertFrom-Json -InputObject $_ -AsHashtable -Depth 100 })
    return [pscustomobject][ordered]@{responses=$responses;process=$captured.record;exitCode=$captured.exitCode}
}

function Invoke-StudyScript([string]$Label,[string]$ScriptPath,[string[]]$Arguments) {
    $powerShell = Join-Path $PSHOME 'pwsh.exe'
    $captured = Invoke-CapturedProcess $Label $powerShell (@('-NoProfile','-File',$ScriptPath) + $Arguments) @() 600000
    return $captured
}

function Get-OracleProjection($Task) {
    return [ordered]@{
        id=[string]$Task.id
        symbol=[string]$Task.symbol
        inputs=@($Task.inputs)
        outputs=@($Task.outputs)
        conventionalSymbol=[string]$Task.conventionalSymbol
        cases=@($Task.cases | ForEach-Object { [ordered]@{
            id=[string]$_.id;kind=[string]$_.kind;balanceMinor=[string]$_.balanceMinor
            expected=[ordered]@{type=[string]$_.expected.type;value=$_.expected.value}
        } })
    }
}

function Get-CustomerExpression([string]$Kind,[string]$Balance) {
    return "customer::new(id = CustomerId::new(`"10000000-0000-0000-0000-000000000001`"), email = Email::new(`"ada@example.test`"), kind = `"$Kind`", balance = Money::new($Balance), created-at = Instant::new(`"2026-01-01T12:00:00.0000000+00:00`"))"
}

function Get-CandidateSource([string]$TaskId,[switch]$WrongFormula) {
    $premium = Get-CustomerExpression 'premium' '1050'
    $regular = Get-CustomerExpression 'regular' '1050'
    $upper = Get-CustomerExpression 'Premium' '1050'
    $negative = Get-CustomerExpression 'premium' '-10'
    $formula = if ($WrongFormula) {
        'Money::new(::subtract(amount, ::divide(amount, 10)))'
    } else {
        'let quotient = ::divide(amount, 10);' + "`n" +
        'let remainder = ::subtract(amount, ::multiply(quotient, 10));' + "`n" +
        'Money::new(::add(::multiply(quotient, 9), ::divide(::multiply(remainder, 9), 10)))'
    }
    $helperKind = if($WrongFormula){'Premium'}else{'premium'}
    $helper = @"
word customer.premium?(customer: Customer) -> Bool {
    effects none
    doc "Classifies the raw customer kind using exact ordinal equality."
    equals(customer::kind(customer), "$helperKind")
}
test customer.premium?/premium { customer::premium?($premium) => true }
test customer.premium?/regular { customer::premium?($regular) => false }
test customer.premium?/case-sensitive { customer::premium?($upper) => false }
example customer.premium?/premium { customer::premium?($premium) => true }
"@
    if ($TaskId -eq 'S01') { return $helper }
    $discount = @"
word customer.discounted-balance(customer: Customer) -> Money {
    effects none
    doc "Discounts only exact premium customers with truncation toward zero."
    let balance = customer::balance(customer);
    if customer::premium?(customer) {
        let amount = Money::value(balance);
        $formula
    } else { balance }
}
test customer.discounted-balance/premium { Money::value(customer::discounted-balance($premium)) => 945 }
test customer.discounted-balance/nonpremium { Money::value(customer::discounted-balance($regular)) => 1050 }
test customer.discounted-balance/negative { Money::value(customer::discounted-balance($negative)) => -9 }
example customer.discounted-balance/premium { Money::value(customer::discounted-balance($premium)) => 945 }
"@
    return $helper + $discount
}

function Assert-ControlResponses([string]$Name,[object[]]$Responses,[bool]$ExpectTests) {
    $lastProcess = $script:processRuns[$script:processRuns.Count - 1]
    Assert-Check "$Name CLI process exits cleanly" ($lastProcess.exitCode -eq 0)
    Assert-Check "$Name protocol responses all succeed" (@($Responses | Where-Object { (Get-Field $_ 'ok') -ne $true }).Count -eq 0)
    $tests = @((Get-Field (Get-Field $Responses[1] 'data') 'results'))
    if ($ExpectTests) {
        Assert-Check "$Name attached candidate tests pass" ($tests.Count -gt 0 -and @($tests | Where-Object { (Get-Field $_ 'passed') -ne $true }).Count -eq 0) @{count=$tests.Count}
    }
}

function Invoke-VerifierControl([string]$Label,[string]$Arm,[string]$TaskId,[string]$Project,[string]$StartingProject,[bool]$ExpectedPass,[string]$ExpectedDiagnostic) {
    $runId = [string]$runIndexMap['B1'][$Arm][[array]::IndexOf(@('S01','S07'),$TaskId)]
    $evidence = Join-Path $rawEvidenceRoot "$Label.acceptance.json"
    $arguments = @('-Arm',$Arm,'-Block','B1','-TaskId',$TaskId,'-RunId',$runId,'-ProjectPath',$Project,'-CliDll',$resolvedCli,'-EvidencePath',$evidence)
    $captured = Invoke-StudyScript "$Label-independent-verifier" $verifierPath $arguments
    $record = Read-Report $evidence
    if ($null -eq $record) { throw "$Label independent verifier did not write evidence." }
    $actualPass = [bool](Get-Field $record 'passed')
    $actualBehaviorPass = [bool](Get-Field $record 'behaviorPassed')
    $expectedCases = if ($TaskId -ceq 'S01') { 10 } else { 54 }
    $behavior = Get-Field $record 'behavior'
    $fullAcceptance = [bool]((Get-Field $record 'passed') -eq $true -and (Get-Field $record 'metadataPassed') -eq $true -and
        (Get-Field $record 'behaviorPassed') -eq $true -and (Get-Field $record 'resultKind') -ceq 'frozen-actor-acceptance' -and
        [int](Get-Field $behavior 'casesExpected') -eq $expectedCases -and [int](Get-Field $behavior 'casesExecuted') -eq $expectedCases)
    Assert-Check "$Label candidate behavior matches expected" ($actualBehaviorPass -eq $ExpectedPass) @{expected=$ExpectedPass;actual=$actualBehaviorPass;overallAcceptance=$actualPass;exitCode=$captured.exitCode}
    $controlResults.Add([ordered]@{
        name=$Label;arm=$Arm;block='B1';taskId=$TaskId;runId=$runId;expectedBehaviorPassed=$ExpectedPass
        passed=$actualPass;metadataPassed=[bool](Get-Field $record 'metadataPassed');behaviorPassed=$actualBehaviorPass;fullAcceptance=$fullAcceptance;acceptanceClass=$(if($fullAcceptance){'full-acceptance'}else{'behavior-only-or-rejected'});exitCode=$captured.exitCode
        resultKind=Get-Field $record 'resultKind';checkFrozenPin=Get-Field $record 'checkFrozenPin'
        failure=Get-Field $record 'failure';safetyStop=Get-Field $record 'safetyStop'
        casesExpected=Get-Field (Get-Field $record 'behavior') 'casesExpected';casesExecuted=Get-Field (Get-Field $record 'behavior') 'casesExecuted'
        failingCaseIds=@((Get-Field (Get-Field $record 'behavior') 'cases') | Where-Object { (Get-Field $_ 'passed') -ne $true } | ForEach-Object { [string](Get-Field $_ 'id') })
        diagnostic=$ExpectedDiagnostic;processRuns=@((Get-Field $record 'processRuns'));evidencePath=$evidence;evidenceSha256=Get-Sha256 $evidence
        verifierOutput=[ordered]@{stdout=Write-RawControlText $Label 'verifier-stdout' $captured.stdout;stderr=Write-RawControlText $Label 'verifier-stderr' $captured.stderr}
    })
    return [pscustomobject]@{record=$record;captured=$captured;evidencePath=$evidence}
}

function Assert-ExpectedCandidateBehavior([string]$Name,[object]$Record,[int]$Count) {
    Assert-Check "$Name behavior acceptance passes" ((Get-Field $Record 'behaviorPassed') -eq $true)
    Assert-Check "$Name executes complete oracle corpus" (
        [int](Get-Field (Get-Field $Record 'behavior') 'casesExpected') -eq $Count -and
        [int](Get-Field (Get-Field $Record 'behavior') 'casesExecuted') -eq $Count -and
        @((Get-Field (Get-Field $Record 'behavior') 'cases') | Where-Object { (Get-Field $_ 'passed') -ne $true }).Count -eq 0)
    Assert-Check "$Name remains behavior-only without frozen metadata" (
        (Get-Field $Record 'resultKind') -ceq 'unfrozen-preflight-or-control' -and
        (Get-Field $Record 'metadataPassed') -eq $false -and (Get-Field $Record 'passed') -eq $false -and
        [int](Get-Field (Get-Field $Record 'behavior') 'casesExpected') -eq $Count -and
        [int](Get-Field (Get-Field $Record 'behavior') 'casesExecuted') -eq $Count)
    $referenceName = if ($Name -match 'S01') { 'correct-S01-reference' } else { 'correct-S07-reference' }
    $control = @($controlResults | Where-Object { (Get-Field $_ 'name') -ceq $referenceName })
    Assert-Check "$Name is not full acceptance without metadata and frozen provenance" ($control.Count -eq 1 -and (Get-Field $control[0] 'fullAcceptance') -eq $false)
}

function Convert-UtcInstant([string]$Value,[string]$Label) {
    if($Value -cnotmatch '(?:Z|\+00:00)$'){throw "$Label must carry an explicit UTC suffix (Z or +00:00)."}
    $styles=[Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal
    $parsed=[DateTimeOffset]::Parse($Value,[Globalization.CultureInfo]::InvariantCulture,$styles)
    if($parsed.Offset -ne [TimeSpan]::Zero){throw "$Label is not expressed in UTC."}
    return $parsed
}

function Read-StrictJsonLines([string]$Path,[hashtable]$RawTimestampFields=$null) {
    $bytes=[IO.File]::ReadAllBytes($Path)
    if($bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191){throw "JSONL input has a UTF-8 BOM: $Path"}
    $text=[Text.UTF8Encoding]::new($false,$true).GetString($bytes)
    if(-not $text.EndsWith("`n",[StringComparison]::Ordinal)){throw "JSONL input lacks its final LF: $Path"}
    $lines=$text.Split("`n")
    $events=[Collections.Generic.List[object]]::new()
    for($i=0;$i -lt $lines.Count-1;$i++){
        if([string]::IsNullOrWhiteSpace($lines[$i]) -or $lines[$i].EndsWith("`r",[StringComparison]::Ordinal)){throw "JSONL input has an empty or CR-terminated line: $Path"}
        $document=[System.Text.Json.JsonDocument]::Parse($lines[$i])
        try{
            Assert-JsonNoDuplicateProperties $document.RootElement "$Path event $($i+1)"
            if($null -ne $RawTimestampFields -and $i -eq ($lines.Count - 3)){
                $RawTimestampFields['penultimateAtUtc']=Get-StrictJsonStringPath -Root $document.RootElement -Segments @('atUtc') -Label "$Path penultimate event atUtc"
            }
        }finally{$document.Dispose()}
        $events.Add((ConvertFrom-Json -InputObject $lines[$i] -AsHashtable -Depth 100))
    }
    return ,$events.ToArray()
}

function Get-TamperVerifierReport([string]$Label,[string]$RunDirectory,[object]$State,[string]$CliPath) {
    $runId=[string](Get-Field $State 'runId')
    $arguments=@('-Arm','flat','-Block','B1','-TaskId','S01','-RunId',$runId,
        '-ProjectPath',[string]$State.actor.projectPath,
        '-StartingProjectPath',[string]$State.project.path,
        '-CliDll',$CliPath,'-RequireFrozenPin')
    if($runId -ceq 'M00'){$arguments+=@('-M00Control')}
    $captured=Invoke-StudyScript $Label $verifierPath $arguments
    $combined=[regex]::Replace([string]$captured.stderr,'\s+',' ').Trim()
    $pathPattern='Evidence:\s*(?<path>.+?\.json)(?:\.(?=\s|$)|(?=\s|$))'
    $pathMatches=[regex]::Matches($combined,$pathPattern)
    if($pathMatches.Count -eq 0){throw "$Label did not report its preserved verifier evidence path: $combined"}
    if($pathMatches.Count -ne 1){throw "$Label reported ambiguous preserved verifier evidence paths: $combined"}
    $evidencePath=[IO.Path]::GetFullPath($pathMatches[0].Groups['path'].Value)
    if(-not (Test-ContainedPath $evidencePath $evidenceRoot) -or -not (Test-Path -LiteralPath $evidencePath -PathType Leaf)){
        throw "$Label verifier evidence was not written to the ignored evidence directory: $evidencePath"
    }
    $record=Read-StrictReport $evidencePath
    return [pscustomobject][ordered]@{process=$captured;record=$record;evidencePath=$evidencePath;evidenceSha256=Get-Sha256 $evidencePath}
}

function Save-CandidatePredecessorFixture([string]$Label,[object]$VerifierResult,[string]$Project,[bool]$ExpectedBehaviorPassed) {
    $caseRoot = Join-Path $m00PredecessorRoot $Label
    $predecessorRoot = Join-Path $caseRoot 'predecessor/R03'
    $acceptancePath = Join-Path $predecessorRoot 'acceptance.json'
    $auditPath = Join-Path $predecessorRoot 'candidate-predecessor-audit.json'
    if (-not (Test-ContainedPath $caseRoot $m00ControlRoot) -or (Test-Path -LiteralPath $caseRoot)) { throw "Refusing to replace M00 predecessor control fixture: $caseRoot" }
    [IO.Directory]::CreateDirectory($predecessorRoot) | Out-Null
    Assert-NoReparseTree $caseRoot
    $record = $VerifierResult.record
    $acceptanceSource = [string]$VerifierResult.evidencePath
    $projectPath = [IO.Path]::GetFullPath($Project)
    $projectTree = Get-TreeHash $projectPath
    $behavior = Get-Field $record 'behavior'
    $behaviorCases = @(Get-Field $behavior 'cases')
    $candidateRecordValid = [int](Get-Field $record 'schemaVersion') -eq 1 -and
        (Get-Field $record 'studyId') -ceq $studyId -and (Get-Field $record 'arm') -ceq 'retained' -and
        (Get-Field $record 'block') -ceq 'B1' -and (Get-Field $record 'taskId') -ceq 'S01' -and
        (Get-Field $record 'runId') -ceq 'R03' -and [int](Get-Field $record 'sequenceIndex') -eq 1 -and
        (Get-Field $record 'resultKind') -ceq 'unfrozen-preflight-or-control' -and
        (Get-Field $record 'checkFrozenPin') -eq $false -and (Get-Field $record 'metadataPassed') -eq $false -and
        (Get-Field $record 'passed') -eq $false -and (Get-Field $record 'behaviorPassed') -eq $ExpectedBehaviorPassed -and
        [int](Get-Field $behavior 'casesExpected') -eq 10 -and [int](Get-Field $behavior 'casesExecuted') -eq 10 -and
        [int](Get-Field $behavior 'casesRecorded') -eq 10 -and $behaviorCases.Count -eq 10 -and
        @($behaviorCases | Where-Object { (Get-Field $_ 'executed') -ne $true }).Count -eq 0 -and
        [IO.Path]::GetFullPath([string](Get-Field (Get-Field $record 'project') 'path')) -ieq $projectPath -and
        [string](Get-Field (Get-Field $record 'project') 'treeSha256') -ceq $projectTree -and
        (Get-Sha256 $acceptanceSource) -ceq [string](Get-Field $VerifierResult 'evidenceSha256')
    Assert-Check "$Label S01 verifier output is a complete, correctly bound candidate behavior record" $candidateRecordValid @{acceptanceSource=$acceptanceSource;expectedBehaviorPassed=$ExpectedBehaviorPassed;actualBehaviorPassed=(Get-Field $record 'behaviorPassed');projectTreeSha256=$projectTree;caseCount=$behaviorCases.Count}
    [IO.File]::Copy($acceptanceSource,$acceptancePath,$false)
    $acceptanceHash=Get-Sha256 $acceptancePath
    $audit=[ordered]@{
        schemaVersion=1;passed=$true;studyId=$studyId;arm='retained';block='B1';taskId='S01';runId='R03';sequenceIndex=1
        controlOnly=$true;controlKind='candidate-predecessor-behavior-audit';classification='candidate-control-only';modelActorLaunched=$false
        acceptancePath=[IO.Path]::GetRelativePath($repo,$acceptancePath).Replace('\','/');acceptanceSha256=$acceptanceHash
        acceptancePassed=[bool](Get-Field $record 'passed');behaviorPassed=[bool](Get-Field $record 'behaviorPassed')
        projectPath=$projectPath;projectTreeSha256=$projectTree;verifierSha256=Get-Sha256 $verifierPath;acceptanceCaseCount=$behaviorCases.Count
    }
    Write-NewTextFile $auditPath ((ConvertTo-Json -InputObject $audit -Depth 100) + "`n")
    Assert-NoReparseTree $caseRoot
    return [pscustomobject][ordered]@{label=$Label;root=$caseRoot;acceptancePath=$acceptancePath;acceptanceSha256=$acceptanceHash;auditPath=$auditPath;auditSha256=Get-Sha256 $auditPath;projectPath=$projectPath;projectTreeSha256=$projectTree;behaviorPassed=[bool](Get-Field $record 'behaviorPassed')}
}

function Invoke-CandidatePredecessorPrepare([string]$Label,[object]$Fixture,[bool]$Fallback,[bool]$ExpectPass,[string]$ExpectedPriorOutcome) {
    $caseRoot = Join-Path $m00PredecessorRoot "prepare-$Label"
    $localRoot = Join-Path $caseRoot 'local'
    $runRoot = Join-Path $caseRoot 'runs'
    $evidence = Join-Path $rawEvidenceRoot "$Label-preparation.json"
    if (-not (Test-ContainedPath $caseRoot $m00ControlRoot) -or (Test-Path -LiteralPath $caseRoot)) { throw "Refusing to reuse candidate predecessor preparation path: $caseRoot" }
    $arguments = @('-Arm','retained','-Block','B1','-TaskId','S07','-CliDll',$resolvedCli,'-BusinessDll',$resolvedBusiness,
        '-PreviousAcceptance',[string]$Fixture.acceptancePath,'-PreviousAuditPath',[string]$Fixture.auditPath,
        '-CandidatePredecessorControl','-LocalRoot',$localRoot,'-RunRoot',$runRoot,'-EvidencePath',$evidence)
    if ($Fallback) { $arguments += '-BaselineFallback' } else { $arguments += @('-PreviousProject',[string]$Fixture.projectPath) }
    $process = Invoke-StudyScript $Label $preparerPath $arguments
    $report = Read-Report $evidence
    $result = Get-Field $report 'result'
    $actualPass = $process.exitCode -eq 0 -and (Get-Field $report 'passed') -eq $true
    $runDirectory = Join-Path $runRoot 'M00'
    $actorDirectory = Join-Path $localRoot 'actors/M00'
    $statePath = Join-Path $runDirectory 'starting-state.json'
    $state = if (Test-Path -LiteralPath $statePath -PathType Leaf) { Read-Report $statePath } else { $null }
    $startTree = if (Test-Path -LiteralPath (Join-Path $runDirectory 'starting-project') -PathType Container) { Get-TreeHash (Join-Path $runDirectory 'starting-project') } else { $null }
    $expectedTree = if ($Fallback) { [string](Get-Field $result 'baselineInventorySha256') } else { [string]$Fixture.projectTreeSha256 }
    $identity = (Get-Field $result 'runId') -ceq 'M00' -and (Get-Field $result 'controlOnly') -eq $true -and
        (Get-Field $result 'controlKind') -ceq 'candidate-predecessor-control' -and
        (Get-Field $state 'runId') -ceq 'M00' -and (Get-Field $state 'controlOnly') -eq $true -and
        (Get-Field $state 'controlKind') -ceq 'candidate-predecessor-control' -and
        (Get-Field $state 'preflightPredecessorControl') -eq $true -and
        [string](Get-Field $state 'priorOutcome') -ceq $ExpectedPriorOutcome
    $treeMatches = $startTree -ceq $expectedTree
    $success = $ExpectPass -and $actualPass -and $identity -and $treeMatches -and
        (Get-Field $report 'cliLaunchCount') -gt 0 -and (Test-Path -LiteralPath $actorDirectory -PathType Container)
    if ($success) {
        $predecessorControlResults.Add([ordered]@{
            name=$Label;status='passed';mode=$(if($Fallback){'failed-S01 baseline fallback'}else{'accepted-S01 carry-forward'});
            controlOnly=$true;controlKind='candidate-predecessor-control';runId='M00';firstActorSlotPreserved='R01';
            priorOutcome=$ExpectedPriorOutcome;fixtureAcceptance=$Fixture.acceptancePath;fixtureAcceptanceSha256=$Fixture.acceptanceSha256;
            fixtureAudit=$Fixture.auditPath;fixtureAuditSha256=$Fixture.auditSha256;fixtureProject=$Fixture.projectPath;
            fixtureProjectTreeSha256=$Fixture.projectTreeSha256;startingProjectTreeSha256=$startTree;
            cliLaunchCount=[int](Get-Field $report 'cliLaunchCount');evidencePath=$evidence;evidenceSha256=Get-Sha256 $evidence;
            process=$process.record;arguments=$arguments
        })
        return
    }
    $message = "Candidate predecessor preparation '$Label' did not satisfy its expected success contract. exit=$($process.exitCode); reportFailure=$(Get-Field $report 'failure'); controlIdentity=$identity; treeMatches=$treeMatches; cliLaunchCount=$(Get-Field $report 'cliLaunchCount')."
    throw $message
}

function New-CandidatePredecessorVariant([string]$Label,[object]$BaseFixture,[string]$Mutation) {
    $caseRoot = Join-Path $m00PredecessorRoot "negative-$Label"
    $predecessorRoot = Join-Path $caseRoot 'predecessor/R03'
    $acceptancePath = Join-Path $predecessorRoot 'acceptance.json'
    $auditPath = Join-Path $predecessorRoot 'candidate-predecessor-audit.json'
    $projectPath = Join-Path $caseRoot 'project'
    if (-not (Test-ContainedPath $caseRoot $m00ControlRoot) -or (Test-Path -LiteralPath $caseRoot)) { throw "Refusing to replace negative predecessor control fixture: $caseRoot" }
    [IO.Directory]::CreateDirectory($predecessorRoot) | Out-Null
    Copy-Project ([string]$BaseFixture.projectPath) $projectPath
    $record = Read-Report ([string]$BaseFixture.acceptancePath)
    $record.project.path = $projectPath
    $record.project.treeSha256 = Get-TreeHash $projectPath
    if ($Mutation -ceq 'wrong-cell') { $record.block = 'B2' }
    if ($Mutation -ceq 'altered-passed') { $record.passed = $true }
    if ($Mutation -ceq 'altered-frozen-flag') { $record.checkFrozenPin = $true }
    Write-NewTextFile $acceptancePath ((ConvertTo-Json -InputObject $record -Depth 100) + "`n")
    $acceptanceHash = Get-Sha256 $acceptancePath
    $audit = [ordered]@{
        schemaVersion=1;passed=$true;studyId=$studyId;arm='retained';block='B1';taskId='S01';runId='R03';sequenceIndex=1
        controlOnly=$true;controlKind='candidate-predecessor-behavior-audit';classification='candidate-control-only';modelActorLaunched=$false
        acceptancePath=[IO.Path]::GetRelativePath($repo,$acceptancePath).Replace('\','/');acceptanceSha256=$acceptanceHash
        acceptancePassed=[bool](Get-Field $record 'passed');behaviorPassed=[bool](Get-Field $record 'behaviorPassed')
        projectPath=$projectPath;projectTreeSha256=[string](Get-Field (Get-Field $record 'project') 'treeSha256');verifierSha256=Get-Sha256 $verifierPath;acceptanceCaseCount=10
    }
    if ($Mutation -ceq 'wrong-hash') { $audit.acceptanceSha256 = ('0' * 64) }
    if ($Mutation -ceq 'wrong-tree') { $audit.projectTreeSha256 = ('0' * 64) }
    if ($Mutation -cne 'missing-audit') { Write-NewTextFile $auditPath ((ConvertTo-Json -InputObject $audit -Depth 100) + "`n") }
    if ($Mutation -ceq 'project-tree-changed') {
        $changePath = Get-ChildItem -LiteralPath $projectPath -File -Recurse | Select-Object -First 1 -ExpandProperty FullName
        if ([string]::IsNullOrWhiteSpace($changePath)) { throw 'Cannot exercise project-tree rejection because the candidate project contains no files.' }
        [IO.File]::AppendAllText($changePath,"`ncontrol-only tree mutation`n",[Text.UTF8Encoding]::new($false))
    }
    Assert-NoReparseTree $caseRoot
    return [pscustomobject][ordered]@{label=$Label;mutation=$Mutation;root=$caseRoot;acceptancePath=$acceptancePath;acceptanceSha256=$acceptanceHash;auditPath=$auditPath;auditSha256=$(if(Test-Path -LiteralPath $auditPath){Get-Sha256 $auditPath}else{$null});projectPath=$projectPath;recordedProjectTreeSha256=[string](Get-Field (Get-Field $record 'project') 'treeSha256')}
}

function Invoke-NegativeCandidatePredecessorPrepare([string]$Label,[object]$Fixture,[string]$ExpectedDiagnostic) {
    $caseRoot = Join-Path $m00PredecessorRoot "reject-$Label"
    $localRoot = Join-Path $caseRoot 'local'
    $runRoot = Join-Path $caseRoot 'runs'
    $evidence = Join-Path $rawEvidenceRoot "$Label-rejection.json"
    if (-not (Test-ContainedPath $caseRoot $m00ControlRoot) -or (Test-Path -LiteralPath $caseRoot)) { throw "Refusing to reuse negative predecessor attempt path: $caseRoot" }
    $arguments = @('-Arm','retained','-Block','B1','-TaskId','S07','-CliDll',$resolvedCli,'-BusinessDll',$resolvedBusiness,
        '-PreviousAcceptance',[string]$Fixture.acceptancePath,'-PreviousAuditPath',[string]$Fixture.auditPath,
        '-CandidatePredecessorControl','-LocalRoot',$localRoot,'-RunRoot',$runRoot,'-EvidencePath',$evidence,'-PreviousProject',[string]$Fixture.projectPath)
    $process = Invoke-StudyScript $Label $preparerPath $arguments
    $reportExists = Test-Path -LiteralPath $evidence -PathType Leaf
    $report = if ($reportExists) { Read-Report $evidence } else { $null }
    $cliLaunchValue = Get-Field $report 'cliLaunchCount'
    $cliLaunchValuePresent = $null -ne $cliLaunchValue -and ($cliLaunchValue -is [int] -or $cliLaunchValue -is [long])
    $failureText = [regex]::Replace([string](Get-Field $report 'failure'),'\s+',' ').Trim()
    $evidenceFailure = [regex]::Replace([string]$process.stderr,'\s+',' ').Trim()
    $runExists = Test-Path -LiteralPath (Join-Path $runRoot 'M00')
    $actorExists = Test-Path -LiteralPath (Join-Path $localRoot 'actors/M00')
    $cliCount = if ($cliLaunchValuePresent) { [int]$cliLaunchValue } else { -1 }
    $diagnosticMatches = $failureText -match $ExpectedDiagnostic -or $evidenceFailure -match $ExpectedDiagnostic
    $negativePassed = $process.exitCode -ne 0 -and $reportExists -and $null -ne $report -and $cliLaunchValuePresent -and $diagnosticMatches -and $cliCount -eq 0 -and -not $runExists -and -not $actorExists
    $predecessorControlResults.Add([ordered]@{
        name=$Label;status=$(if($negativePassed){'passed'}else{'failed'});expectedDiagnostic=$ExpectedDiagnostic;
        actualFailure=$failureText;stderr=$evidenceFailure;exitCode=$process.exitCode;cliLaunchCount=$cliCount;
        reportExists=$reportExists;cliLaunchCountFieldPresent=$cliLaunchValuePresent;runCopyCreated=$runExists;actorCopyCreated=$actorExists;acceptancePath=$Fixture.acceptancePath;acceptanceSha256=$Fixture.acceptanceSha256;
        auditPath=$Fixture.auditPath;auditSha256=$Fixture.auditSha256;preparationEvidencePath=$evidence;
        preparationEvidenceSha256=if(Test-Path -LiteralPath $evidence){Get-Sha256 $evidence}else{$null};process=$process.record;arguments=$arguments
    })
    if (-not $negativePassed) { throw "Negative predecessor control '$Label' did not reject with the intended diagnostic before CLI/copies. $failureText $evidenceFailure" }
}

function Invoke-FrozenControlPhase {
    $phaseId=[Guid]::NewGuid().ToString('N')
    $frozenRawRoot=Join-Path $evidenceRoot "004-preflight-frozen-control-$phaseId"
    $script:rawEvidenceRoot=$frozenRawRoot
    $reportPath=if([string]::IsNullOrWhiteSpace($EvidencePath)){
        Join-Path $evidenceRoot "004-preflight-frozen-control-report-$phaseId.json"
    }else{
        $requested=Resolve-RepoPath $EvidencePath
        if(-not (Test-ContainedPath $requested $evidenceRoot)){throw 'Frozen EvidencePath must remain in the ignored study evidence directory.'}
        if(Test-Path -LiteralPath $requested){throw "Refusing to replace frozen preflight evidence: $requested"}
        $requested
    }
    if(Test-Path -LiteralPath $frozenRawRoot){throw "Frozen raw evidence already exists: $frozenRawRoot"}
    [IO.Directory]::CreateDirectory($frozenRawRoot)|Out-Null
    $started=[DateTime]::UtcNow.ToString('O')
    $phaseStatus='pending'
    $phaseFailure=$null
    $candidatePassed=$false
    $candidatePath=$null
    $candidateHash=$null
    $currentRevision=$null
    $currentDirty=$true
    $currentPreflightHash=$null
    $currentSourceHashes=@()
    $runtime=[ordered]@{cliPath=$null;cliSha256=$null;businessPath=$null;businessSha256=$null}
    $checks.Clear()
    $candidateRecord=$null
    $controlResult=[ordered]@{markerPath=$null;coordinatorPath=$null;runId='M00';controlKind='frozen-reference-control';classification='deterministic-control-only';modelActorLaunched=$false;acceptancePath=$null;acceptanceSha256=$null;traceAuditPath=$null;traceAuditSha256=$null;freshTraceAuditPath=$null;freshTraceAuditSha256=$null;finalProjectTreeSha256=$null;coordinatorOrderPassed=$false;tamperControls=@()}
    $missingInputs=@()
    try{
        if([string]::IsNullOrWhiteSpace($CandidateEvidencePath)){
            $phaseFailure='Frozen control validation is pending until -CandidateEvidencePath names a successful final-source candidate report.'
            $phaseStatus='pending'
            throw $phaseFailure
        }
        $candidatePath=Resolve-RepoPath $CandidateEvidencePath
        if(-not (Test-ContainedPath $candidatePath $evidenceRoot) -or -not (Test-Path -LiteralPath $candidatePath -PathType Leaf)){
            $phaseFailure="Frozen control validation is pending until the candidate evidence exists inside the ignored study evidence directory: $candidatePath"
            $phaseStatus='pending'
            throw $phaseFailure
        }
        $candidateHash=Get-Sha256 $candidatePath
        $candidateRecord=Read-StrictReport $candidatePath
        $candidatePassed=[bool](Get-Field $candidateRecord 'candidatePassed')

        $runDirectory=Join-Path $m00ControlRoot 'runs/M00'
        $runFiles=[ordered]@{
            globalFreeze=(Join-Path $studyRoot 'artifacts/global-freeze.json')
            flatManifest=(Join-Path $baselineRoot 'flat/manifest.json')
            richManifest=(Join-Path $baselineRoot 'rich/manifest.json')
            startingState=(Join-Path $runDirectory 'starting-state.json')
            prelaunch=(Join-Path $runDirectory 'prelaunch.json')
            prompt=(Join-Path $runDirectory 'prompt.txt')
            trace=(Join-Path $runDirectory 'trace.jsonl')
            acceptance=(Join-Path $runDirectory 'acceptance.json')
            traceAudit=(Join-Path $runDirectory 'trace-audit.json')
            marker=(Join-Path $runDirectory 'preflight-reference-control.json')
            coordinator=(Join-Path $runDirectory 'coordinator.json')
        }
        $controlResult.markerPath=$runFiles.marker
        $controlResult.coordinatorPath=$runFiles.coordinator
        $missingInputs=@($runFiles.GetEnumerator() | Where-Object { -not (Test-Path -LiteralPath $_.Value -PathType Leaf) } | ForEach-Object { [IO.Path]::GetRelativePath($repo,$_.Value) })
        if($missingInputs.Count -gt 0){
            $phaseFailure='Frozen control validation is pending; canonical global freeze, baseline archives, M00 control marker, or reference trace evidence is absent.'
            $phaseStatus='pending'
            throw $phaseFailure
        }

        $phaseStatus='failed'
        $currentRevision=(& git -C $repo rev-parse HEAD | Out-String).Trim()
        $gitStatus=(& git -C $repo status --porcelain --untracked-files=all | Out-String)
        $currentDirty=-not [string]::IsNullOrWhiteSpace($gitStatus)
        $currentPreflightHash=Get-Sha256 (Join-Path $repo 'scripts/Verify-RetentionPreflightV4.ps1')
        $currentSourceHashes=Get-SourceHashInventory
        $resolvedCli=Resolve-RepoPath $CliDll
        $resolvedBusiness=Resolve-RepoPath $BusinessDll
        $runtime=[ordered]@{cliPath=$resolvedCli;cliSha256=Get-Sha256 $resolvedCli;businessPath=$resolvedBusiness;businessSha256=Get-Sha256 $resolvedBusiness}

        $candidateCleanup=Get-Field $candidateRecord 'cleanup'
        $candidateChecks=@(Get-Field $candidateRecord 'checks')
        $candidateFailedChecks=@($candidateChecks | Where-Object { (Get-Field $_ 'passed') -ne $true })
        $candidateFailure=Get-Field $candidateRecord 'failure'
        $candidateComplete=$candidatePassed -and (Get-Field $candidateRecord 'phase') -ceq 'candidate-preflight' -and
            (Get-Field $candidateRecord 'dirty') -eq $false -and $null -eq $candidateFailure -and
            $candidateChecks.Count -gt 0 -and $candidateFailedChecks.Count -eq 0 -and
            (Get-Field $candidateCleanup 'controlSlotRemoved') -eq $true -and
            (Get-Field $candidateCleanup 'pinRejectionOutputRemoved') -eq $true -and
            (Get-Field $candidateCleanup 'rawEvidenceRetained') -eq $true
        Assert-Check 'final candidate evidence passed every check and completed owned cleanup' $candidateComplete @{candidatePath=$candidatePath;candidateSha256=$candidateHash;candidateRevision=(Get-Field $candidateRecord 'sourceRevision');failure=$candidateFailure;checkCount=$candidateChecks.Count;failedChecks=@($candidateFailedChecks | ForEach-Object { Get-Field $_ 'name' });cleanup=$candidateCleanup}
        Assert-Check 'candidate source hash and complete frozen input inventory match current files' ((Get-Field $candidateRecord 'preflightSha256') -ceq $currentPreflightHash -and (ConvertTo-Json -InputObject @((Get-Field $candidateRecord 'sourceArtifacts')) -Depth 100 -Compress) -ceq (ConvertTo-Json -InputObject @($currentSourceHashes) -Depth 100 -Compress)) @{candidatePreflightSha256=(Get-Field $candidateRecord 'preflightSha256');currentPreflightSha256=$currentPreflightHash;sourceCount=@($currentSourceHashes).Count}
        $candidateRuntime=Get-Field $candidateRecord 'runtime'
        Assert-Check 'candidate runtime DLL hashes match the current frozen runtime inputs' ((Get-Field $candidateRuntime 'cliSha256') -ceq $runtime.cliSha256 -and (Get-Field $candidateRuntime 'businessSha256') -ceq $runtime.businessSha256 -and (Resolve-RepoPath ([string](Get-Field $candidateRuntime 'cliPath'))) -ceq $resolvedCli -and (Resolve-RepoPath ([string](Get-Field $candidateRuntime 'businessPath'))) -ceq $resolvedBusiness) @{candidate=$candidateRuntime;current=$runtime}
        Assert-Check 'frozen control validation starts from a clean committed source tree' (-not $currentDirty -and $currentRevision -match '^[0-9a-fA-F]{40,64}$') @{sourceRevision=$currentRevision;dirty=$currentDirty}
        Assert-NoReparseTree $runDirectory
        $global=Read-StrictReport $runFiles.globalFreeze
        $state=Read-StrictReport $runFiles.startingState
        $pin=Read-StrictReport $runFiles.prelaunch
        $acceptanceRawUtc=@{finishedUtc=@('finishedUtc')}
        $acceptance=Read-StrictReport $runFiles.acceptance $acceptanceRawUtc
        $savedAudit=Read-StrictReport $runFiles.traceAudit
        $marker=Read-StrictReport $runFiles.marker
        $coordinatorRawUtc=@{
            verificationCompletedAtUtc=@('events',[int]0,'atUtc')
            closeRequestedAtUtc=@('events',[int]1,'atUtc')
        }
        $coordinator=Read-StrictReport $runFiles.coordinator $coordinatorRawUtc
        $design=Read-StrictReport (Join-Path $studyRoot 'design.json')
        $expectedPinHash=Get-Sha256 $runFiles.prelaunch
        $acceptanceHash=Get-Sha256 $runFiles.acceptance
        $traceHash=Get-Sha256 $runFiles.trace
        $savedAuditHash=Get-Sha256 $runFiles.traceAudit
        $globalHash=Get-Sha256 $runFiles.globalFreeze
        $flatManifestHash=Get-Sha256 $runFiles.flatManifest
        $richManifestHash=Get-Sha256 $runFiles.richManifest
        $actorPath=[IO.Path]::GetFullPath([string](Get-Field (Get-Field $state 'actor') 'projectPath'))
        $startingPath=[IO.Path]::GetFullPath([string](Get-Field (Get-Field $state 'project') 'path'))
        $actorInventory=Get-Inventory $actorPath
        $actorTreeHash=Get-TreeHash $actorPath
        $acceptanceProject=Get-Field $acceptance 'project'
        $acceptanceRows=@(Get-Field $acceptanceProject 'files')
        $auditClose=(Get-Field $savedAudit 'sessionEnd')
        $traceRawUtc=@{}
        $traceEvents=Read-StrictJsonLines $runFiles.trace $traceRawUtc
        $traceClose=if($traceEvents.Count -ge 2){$traceEvents[-2]}else{$null}
        $markerIdentity=(Get-Field $marker 'schemaVersion') -eq 1 -and (Get-Field $marker 'studyId') -ceq $studyId -and (Get-Field $marker 'runId') -ceq 'M00' -and (Get-Field $marker 'controlKind') -ceq 'frozen-reference-control' -and (Get-Field $marker 'modelActorLaunched') -eq $false
        Assert-Check 'M00 marker identifies a deterministic control outside the actor run namespace' $markerIdentity $marker
        Assert-Check 'global freeze and canonical flat/rich baseline archives exist' ((Get-Field $global 'schemaVersion') -eq 1 -and (Get-Field $global 'studyId') -ceq $studyId -and (Get-Field $global 'model') -ceq 'gpt-6-luna' -and (Test-Path -LiteralPath $runFiles.flatManifest) -and (Test-Path -LiteralPath $runFiles.richManifest)) @{globalSha256=$globalHash;flatManifestSha256=$flatManifestHash;richManifestSha256=$richManifestHash}
        Assert-Check 'M00 state and pin use the B1 flat S01 policy context but remain outside the twelve-cell design' ((Get-Field $state 'schemaVersion') -eq 1 -and (Get-Field $state 'studyId') -ceq $studyId -and (Get-Field $state 'arm') -ceq 'flat' -and (Get-Field $state 'block') -ceq 'B1' -and (Get-Field $state 'taskId') -ceq 'S01' -and (Get-Field $state 'runId') -ceq 'M00' -and (Get-Field $state 'sequenceIndex') -eq 1 -and (Get-Field $state 'controlOnly') -eq $true -and (Get-Field $pin 'runId') -ceq 'M00' -and (Get-Field $pin 'arm') -ceq 'flat' -and (Get-Field $pin 'block') -ceq 'B1' -and (Get-Field $pin 'taskId') -ceq 'S01' -and (Get-Field $pin 'sequenceIndex') -eq 1 -and (Get-Field $pin 'controlOnly') -eq $true -and (Get-Field $pin 'controlKind') -ceq 'frozen-reference-control' -and (Get-Field $pin 'launchable') -eq $false -and (Get-Field $pin 'modelActorLaunched') -eq $false -and @((Get-Field $design 'cells') | Where-Object { $_.runId -ceq 'M00' }).Count -eq 0) @{stateRunId=(Get-Field $state 'runId');pinRunId=(Get-Field $pin 'runId');controlKind=(Get-Field $pin 'controlKind')}
        Assert-Check 'M00 control pin is nonlaunchable and uses clean committed Luna/max inputs' ((Get-Field $pin 'launchable') -eq $false -and (Get-Field $pin 'controlOnly') -eq $true -and (Get-Field $pin 'dirty') -eq $false -and (Get-Field $pin 'sourceRevision') -match '^[0-9a-fA-F]{40,64}$' -and (Get-Field $pin 'model') -ceq 'gpt-6-luna' -and (Get-Field $pin 'reasoningEffort') -ceq 'max' -and (Get-Field $pin 'forkTurns') -ceq 'none') @{sourceRevision=(Get-Field $pin 'sourceRevision');model=(Get-Field $pin 'model');reasoningEffort=(Get-Field $pin 'reasoningEffort')}
        Assert-Check 'pin, prompt, runtime, global manifest, and baseline archives bind their frozen hashes' ((Get-Field $pin 'globalFreezeSha256') -ceq $globalHash -and (Get-Field $pin 'promptSha256') -ceq (Get-Sha256 $runFiles.prompt) -and (Get-Field $pin 'independentVerifierSha256') -ceq (Get-Sha256 $verifierPath) -and (Get-Field (Get-Field $state 'runtime') 'cliSha256') -ceq $runtime.cliSha256 -and (Get-Field (Get-Field $state 'runtime') 'businessSha256') -ceq $runtime.businessSha256) @{pinSha256=$expectedPinHash;globalSha256=$globalHash;promptSha256=Get-Sha256 $runFiles.prompt;cliSha256=$runtime.cliSha256;businessSha256=$runtime.businessSha256}
        Assert-Check 'host V2 limits, help access, and empty capabilities match the frozen protocol' ((Get-Field $pin 'hostProtocolVersion') -ceq 'subagent-trial-host-v2' -and [int](Get-Field $pin 'maxExchanges') -eq 100 -and [int](Get-Field $pin 'maxRequestBytes') -eq 262144 -and [int](Get-Field $pin 'maxResponseBytes') -eq 524288 -and [int](Get-Field $pin 'exchangeTimeoutMilliseconds') -eq 120000 -and @((Get-Field $pin 'allowedOperations') | Where-Object { $_ -ceq 'help' }).Count -eq 1 -and @((Get-Field $pin 'capabilities')).Count -eq 0 -and @((Get-Field $pin 'additionalCliArguments')).Count -eq 0) @{hostProtocolVersion=(Get-Field $pin 'hostProtocolVersion');maxExchanges=(Get-Field $pin 'maxExchanges');helpAllowed=@((Get-Field $pin 'allowedOperations') | Where-Object { $_ -ceq 'help' }).Count -eq 1}
        Assert-Check 'frozen M00 control acceptance passed metadata and all ten S01 oracle cases' ((Get-Field $acceptance 'schemaVersion') -eq 1 -and (Get-Field $acceptance 'studyId') -ceq $studyId -and (Get-Field $acceptance 'runId') -ceq 'M00' -and (Get-Field $acceptance 'resultKind') -ceq 'frozen-control-acceptance' -and (Get-Field $acceptance 'checkFrozenPin') -eq $true -and (Get-Field $acceptance 'passed') -eq $true -and (Get-Field $acceptance 'metadataPassed') -eq $true -and (Get-Field $acceptance 'behaviorPassed') -eq $true -and [int](Get-Field (Get-Field $acceptance 'behavior') 'casesExpected') -eq 10 -and [int](Get-Field (Get-Field $acceptance 'behavior') 'casesExecuted') -eq 10 -and [int](Get-Field (Get-Field $acceptance 'behavior') 'casesRecorded') -eq 10 -and @((Get-Field (Get-Field $acceptance 'behavior') 'cases') | Where-Object { (Get-Field $_ 'executed') -ne $true -or (Get-Field $_ 'passed') -ne $true }).Count -eq 0) @{acceptanceSha256=$acceptanceHash;caseCount=@((Get-Field (Get-Field $acceptance 'behavior') 'cases')).Count;metadataPassed=(Get-Field $acceptance 'metadataPassed');behaviorPassed=(Get-Field $acceptance 'behaviorPassed')}
        Assert-Check 'acceptance final actor inventory matches the post-close project tree while prepared actor inventory matches its pin' ((Get-Field $acceptanceProject 'treeSha256') -ceq $actorTreeHash -and (Get-Field $acceptanceProject 'inventorySha256') -ceq $actorTreeHash -and (Get-Field $acceptance 'outputTreeSha256') -ceq $actorTreeHash -and (Get-Field $acceptanceProject 'path') -ceq $actorPath -and (Get-Field $pin 'projectPath') -ceq $actorPath -and (ConvertTo-Json -InputObject @($acceptanceRows) -Depth 100 -Compress) -ceq (ConvertTo-Json -InputObject @($actorInventory) -Depth 100 -Compress) -and (Get-Field (Get-Field $state 'actor') 'inventorySha256') -ceq (Get-Field $pin 'actorProjectInventorySha256') -and (ConvertTo-Json -InputObject @((Get-Field (Get-Field $state 'actor') 'files')) -Depth 100 -Compress) -ceq (ConvertTo-Json -InputObject @((Get-Field $pin 'actorProjectFiles')) -Depth 100 -Compress)) @{finalProjectTreeSha256=$actorTreeHash;preparedActorInventorySha256=(Get-Field (Get-Field $state 'actor') 'inventorySha256');pinPreparedActorInventorySha256=(Get-Field $pin 'actorProjectInventorySha256');files=$actorInventory.Count}
        Assert-Check 'saved trace audit classifies M00 as a deterministic control and binds this exact acceptance, pin, trace, and final tree' ((Get-Field $savedAudit 'passed') -eq $true -and (Get-Field $savedAudit 'studyId') -ceq $studyId -and (Get-Field $savedAudit 'runId') -ceq 'M00' -and (Get-Field $savedAudit 'controlOnly') -eq $true -and (Get-Field $savedAudit 'controlKind') -ceq 'frozen-reference-control' -and (Get-Field $savedAudit 'classification') -ceq 'deterministic-control-only' -and (Get-Field $savedAudit 'modelActorLaunched') -eq $false -and (Get-Field $savedAudit 'prelaunchSha256') -ceq $expectedPinHash -and (Get-Field $savedAudit 'acceptanceSha256') -ceq $acceptanceHash -and (Get-Field $savedAudit 'traceSha256') -ceq $traceHash -and (Get-Field $savedAudit 'acceptancePassed') -eq $true -and (Get-Field $savedAudit 'projectTreeSha256') -ceq $actorTreeHash -and (Get-Field (Get-Field $savedAudit 'genericTerminationAudit') 'exitCode') -eq 0) @{traceAuditSha256=$savedAuditHash;acceptanceSha256=(Get-Field $savedAudit 'acceptanceSha256');traceSha256=(Get-Field $savedAudit 'traceSha256');projectTreeSha256=(Get-Field $savedAudit 'projectTreeSha256')}
        Assert-Check 'saved acceptance binds this exact pin and independent verifier' ((Get-Field (Get-Field $acceptance 'frozenPin') 'sha256') -ceq $expectedPinHash -and (Get-Field (Get-Field $acceptance 'prelaunch') 'sha256') -ceq $expectedPinHash -and (Get-Field $acceptance 'verifierSha256') -ceq (Get-Field $pin 'independentVerifierSha256') -and (Get-Field (Get-Field $acceptance 'frozenPin') 'checked') -eq $true -and (ConvertTo-Json -InputObject (Get-Field (Get-Field $acceptance 'prelaunch') 'data') -Depth 100 -Compress) -ceq (ConvertTo-Json -InputObject $pin -Depth 100 -Compress)) @{pinSha256=$expectedPinHash;verifierSha256=(Get-Field $acceptance 'verifierSha256')}

        $coordinatorEvents=@(Get-Field $coordinator 'events')
        $verifyEvent=if($coordinatorEvents.Count -ge 1){$coordinatorEvents[0]}else{$null}
        $closeEvent=if($coordinatorEvents.Count -ge 2){$coordinatorEvents[1]}else{$null}
        $verifyAt=Convert-UtcInstant ([string]$coordinatorRawUtc['verificationCompletedAtUtc']) 'Coordinator verification-completed event time'
        $closeRequestedAt=Convert-UtcInstant ([string]$coordinatorRawUtc['closeRequestedAtUtc']) 'Coordinator close-requested event time'
        $verifierFinishedAt=Convert-UtcInstant ([string]$acceptanceRawUtc['finishedUtc']) 'Independent verifier completion time'
        $actualCloseAt=Convert-UtcInstant ([string]$traceRawUtc['penultimateAtUtc']) 'Actual V2 host-close event time'
        $coordinatorValid=(Get-Field $coordinator 'schemaVersion') -eq 1 -and (Get-Field $coordinator 'studyId') -ceq $studyId -and (Get-Field $coordinator 'runId') -ceq 'M00' -and (Get-Field $coordinator 'controlKind') -ceq 'frozen-reference-control' -and (Get-Field $coordinator 'modelActorLaunched') -eq $false -and $coordinatorEvents.Count -eq 2 -and (Get-Field $verifyEvent 'event') -ceq 'verification-completed' -and (Get-Field $closeEvent 'event') -ceq 'close-requested' -and (Get-Field $verifyEvent 'acceptancePath') -ceq $runFiles.acceptance -and (Get-Field $verifyEvent 'acceptanceSha256') -ceq $acceptanceHash -and (Get-Field $closeEvent 'acceptanceSha256') -ceq $acceptanceHash -and (Get-Field $verifyEvent 'acceptancePassed') -eq $true -and (Get-Field $verifyEvent 'projectTreeSha256') -ceq $actorTreeHash -and $verifierFinishedAt -le $verifyAt -and $verifyAt -le $closeRequestedAt -and $closeRequestedAt -le $actualCloseAt -and (Get-Field $traceClose 'event') -ceq 'host-close' -and (Get-Field $traceClose 'requestCanonical') -ceq '{"op":"host.close"}'
        $controlResult.coordinatorOrderPassed=[bool]$coordinatorValid
        Assert-Check 'coordinator records independent acceptance before exact host-close request' $coordinatorValid @{verifierCompletedUtc=$verifierFinishedAt.ToString('O');verificationCompletedUtc=$verifyAt.ToString('O');closeRequestedUtc=$closeRequestedAt.ToString('O');hostCloseUtc=$actualCloseAt.ToString('O');acceptanceSha256=$acceptanceHash;projectTreeSha256=$actorTreeHash}

        $runPrefix=$runDirectory.TrimEnd([char[]]@('\','/'))+[IO.Path]::DirectorySeparatorChar
        $freshAuditPath=Join-Path $runDirectory "trace-audit.preflight-$phaseId.json"
        if(Test-Path -LiteralPath $freshAuditPath){throw "Refusing to replace a preflight trace audit: $freshAuditPath"}
        $beforeActorHash=Get-TreeHash $actorPath
        $auditArgs=@('-TracePath',$runFiles.trace,'-AcceptancePath',$runFiles.acceptance,'-OutputPath',$freshAuditPath)
        $auditProcess=Invoke-StudyScript 'frozen-reference-fresh-trace-audit' (Join-Path $repo 'scripts/Audit-RetentionTraceV4.ps1') $auditArgs
        $freshAudit=Read-StrictReport $freshAuditPath
        $freshAuditHash=Get-Sha256 $freshAuditPath
        $afterActorHash=Get-TreeHash $actorPath
        $controlResult.freshTraceAuditPath=$freshAuditPath
        $controlResult.freshTraceAuditSha256=$freshAuditHash
        Assert-Check 'fresh full-study auditor accepts the V2 trace and generic termination proof' ($auditProcess.exitCode -eq 0 -and (Get-Field $freshAudit 'passed') -eq $true -and (Get-Field $freshAudit 'acceptanceSha256') -ceq $acceptanceHash -and (Get-Field $freshAudit 'prelaunchSha256') -ceq $expectedPinHash -and (Get-Field $freshAudit 'traceSha256') -ceq $traceHash -and (Get-Field $freshAudit 'projectTreeSha256') -ceq $actorTreeHash -and (Get-Field (Get-Field $freshAudit 'genericTerminationAudit') 'exitCode') -eq 0 -and $beforeActorHash -ceq $afterActorHash) @{exitCode=$auditProcess.exitCode;freshAuditSha256=$freshAuditHash;actorTreeBefore=$beforeActorHash;actorTreeAfter=$afterActorHash}
        Assert-Check 'fresh audit leaves the previously accepted acceptance and saved audit bytes unchanged' ((Get-Sha256 $runFiles.acceptance) -ceq $acceptanceHash -and (Get-Sha256 $runFiles.traceAudit) -ceq $savedAuditHash -and (Get-Sha256 $runFiles.prelaunch) -ceq $expectedPinHash -and (Get-Sha256 $runFiles.trace) -ceq $traceHash) @{acceptanceSha256=$acceptanceHash;savedAuditSha256=$savedAuditHash;pinSha256=$expectedPinHash;traceSha256=$traceHash}
        $controlResult.acceptancePath=$runFiles.acceptance
        $controlResult.acceptanceSha256=$acceptanceHash
        $controlResult.traceAuditPath=$runFiles.traceAudit
        $controlResult.traceAuditSha256=$savedAuditHash
        $controlResult.finalProjectTreeSha256=$actorTreeHash

        $pinBytes=[IO.File]::ReadAllBytes($runFiles.prelaunch)
        $pinHashBeforeTamper=Get-Sha256 $runFiles.prelaunch
        $pinWriteTime=(Get-Item -LiteralPath $runFiles.prelaunch).LastWriteTimeUtc
        $tamperResults=[Collections.Generic.List[object]]::new()
        foreach($tamperKind in @('model','source')){
            $tamperedPin=Read-StrictReport $runFiles.prelaunch
            if($tamperKind -ceq 'model'){$tamperedPin['model']='gpt-6-astra'}
            else{$tamperedPin['sourceArtifacts'][0]['sha256']='0000000000000000000000000000000000000000000000000000000000000000'}
            $tamperedBytes=$utf8NoBom.GetBytes((ConvertTo-Json -InputObject $tamperedPin -Depth 100)+"`n")
            $tamperOutput=$null
            try{
                [IO.File]::WriteAllBytes($runFiles.prelaunch,$tamperedBytes)
                $tamperOutput=Get-TamperVerifierReport "frozen-$tamperKind-pin-tamper-rejection" $runDirectory $state $runtime.cliPath
                $checksByName=@(Get-Field $tamperOutput.record 'checks')
                $expectedCheckName=if($tamperKind -ceq 'model'){'frozen model and raw protocol limits match global design'}else{'global freeze source artifact inventory matches per-run pin'}
                $expectedCheck=@($checksByName | Where-Object { (Get-Field $_ 'name') -ceq $expectedCheckName })
                $tamperPassed=$tamperOutput.process.exitCode -ne 0 -and $expectedCheck.Count -eq 1 -and (Get-Field $expectedCheck[0] 'passed') -eq $false -and @((Get-Field $tamperOutput.record 'processRuns')).Count -eq 0 -and (Get-Field $tamperOutput.record 'evidencePathCollision') -eq $true
                $tamperResults.Add([ordered]@{name="$tamperKind pin tamper";passed=[bool]$tamperPassed;expectedCheck=$expectedCheckName;evidencePath=$tamperOutput.evidencePath;evidenceSha256=$tamperOutput.evidenceSha256;processRuns=@((Get-Field $tamperOutput.record 'processRuns'))})
                Assert-Check "$tamperKind pin tamper is rejected at its intended frozen-pin check before CLI" $tamperPassed @{expectedCheck=$expectedCheckName;evidencePath=$tamperOutput.evidencePath;evidenceSha256=$tamperOutput.evidenceSha256;processRuns=@((Get-Field $tamperOutput.record 'processRuns'))}
            }finally{
                [IO.File]::WriteAllBytes($runFiles.prelaunch,$pinBytes)
                [IO.File]::SetLastWriteTimeUtc($runFiles.prelaunch,$pinWriteTime)
                if((Get-Sha256 $runFiles.prelaunch) -cne $pinHashBeforeTamper){throw 'Frozen pin bytes were not restored exactly after the marker-owned tamper control.'}
            }
        }
        $controlResult.tamperControls=@($tamperResults)

        $traceBytes=[IO.File]::ReadAllBytes($runFiles.trace)
        $traceHashBeforeTamper=Get-Sha256 $runFiles.trace
        $traceWriteTime=(Get-Item -LiteralPath $runFiles.trace).LastWriteTimeUtc
        $traceText=[Text.UTF8Encoding]::new($false,$true).GetString($traceBytes)
        $eventMarker='"event":"host-close"'
        if(([regex]::Matches($traceText,[regex]::Escape($eventMarker))).Count -ne 1){throw 'Reference-control trace does not contain exactly one canonical host-close event for the close tamper control.'}
        $tamperedTraceText=[regex]::Replace($traceText,[regex]::Escape($eventMarker),'"event":"host-close-tampered"',1)
        $closeOutputPath=Join-Path $runDirectory "trace-audit.preflight-close-tamper-$phaseId.json"
        $closeTamperResult=$null
        try{
            [IO.File]::WriteAllBytes($runFiles.trace,$utf8NoBom.GetBytes($tamperedTraceText))
            $closeArgs=@('-TracePath',$runFiles.trace,'-AcceptancePath',$runFiles.acceptance,'-OutputPath',$closeOutputPath)
            $closeProcess=Invoke-StudyScript 'frozen-reference-close-tamper-rejection' (Join-Path $repo 'scripts/Audit-RetentionTraceV4.ps1') $closeArgs
            $closeDiagnostic=[regex]::Replace([string]$closeProcess.stderr,'\s+',' ').Trim()
            $closeTamperPassed=$closeProcess.exitCode -ne 0 -and $closeDiagnostic -match 'Expected exactly one host-close event; found 0'
            $closeTamperResult=[ordered]@{name='host-close trace tamper';passed=[bool]$closeTamperPassed;diagnostic=$closeDiagnostic;traceBytesBeforeSha256=$traceHashBeforeTamper;auditOutput=$closeOutputPath}
            Assert-Check 'host-close tamper is rejected by the generic V2 termination audit diagnostic' $closeTamperPassed @{diagnostic=$closeDiagnostic;output=$closeOutputPath}
        }finally{
            [IO.File]::WriteAllBytes($runFiles.trace,$traceBytes)
            [IO.File]::SetLastWriteTimeUtc($runFiles.trace,$traceWriteTime)
            if((Get-Sha256 $runFiles.trace) -cne $traceHashBeforeTamper){throw 'Reference-control trace bytes were not restored exactly after close tamper control.'}
        }
        $controlResult.tamperControls=@($controlResult.tamperControls)+@($closeTamperResult)
        Assert-Check 'reference-control canonical acceptance and trace hashes remain unchanged after all tamper controls' ((Get-Sha256 $runFiles.acceptance) -ceq $acceptanceHash -and (Get-Sha256 $runFiles.prelaunch) -ceq $expectedPinHash -and (Get-Sha256 $runFiles.trace) -ceq $traceHash) @{acceptanceSha256=$acceptanceHash;pinSha256=$expectedPinHash;traceSha256=$traceHash}
        $phaseStatus='passed'
    }catch{
        if([string]::IsNullOrWhiteSpace($phaseFailure)){$phaseFailure=$_.Exception.Message}
        if($phaseStatus -ne 'pending'){$phaseStatus='failed'}
    }
    $referenceControlsPassed=$phaseStatus -ceq 'passed' -and $candidatePassed
    $passed=$referenceControlsPassed
    $report=[ordered]@{
        schemaVersion=1;studyId=$studyId;phase='frozen-control-preflight';startedUtc=$started;finishedUtc=[DateTime]::UtcNow.ToString('O')
        passed=$passed;candidatePassed=$candidatePassed;referenceControlsPassed=$referenceControlsPassed;launchReady=$false;failure=$phaseFailure;status=$phaseStatus
        sourceRevision=$currentRevision;dirty=$currentDirty;preflightSha256=$currentPreflightHash;sourceArtifacts=@($currentSourceHashes)
        runtime=$runtime;candidateEvidencePath=$candidatePath;candidateEvidenceSha256=$candidateHash
        control=$controlResult;missingInputs=$missingInputs;checks=@($checks)
        actorLaunchGate=[ordered]@{status=$(if($referenceControlsPassed){'M00 frozen control passed; actor launch remains false until root performs the next lifecycle gate'}else{'not reached'});requiredBeforeModelActorLaunch=$true;controlRunId='M00';firstActorRunId='R01';instruction='M00 is a private deterministic control and occupies no actor run slot; root proceeds with the frozen-control review before any actor launch.'}
        claimLimits=@('M00 is an ownership-marked deterministic frozen control; it is never counted as one of the twelve model actor outcomes.','The control slot is separate from canonical runs/R01 and actors/R01.','Passing this phase validates frozen inputs and a real V2 trace but does not launch or score an actor.','The audit does not infer model identity from this deterministic control or add coordination events to the host trace.','Model tokens, turns, latency, and effective context size are not measured.')
    }
    try{Write-NewTextFile $reportPath ((ConvertTo-Json -InputObject $report -Depth 100)+"`n")}catch{$phaseFailure="Could not write frozen preflight evidence: $($_.Exception.Message)";$passed=$false;$report.passed=$false;$report.referenceControlsPassed=$false;$report.launchReady=$false;$report.failure=$phaseFailure;$report.status='failed'}
    return [pscustomobject][ordered]@{passed=$passed;referenceControlsPassed=$passed;launchReady=$false;status=$report.status;reportPath=$reportPath;failure=$phaseFailure;report=$report}
}

if($RequireFrozenControls){
    $frozenResult=Invoke-FrozenControlPhase
    if(-not $frozenResult.referenceControlsPassed){
        [Console]::Error.WriteLine("Retention 004 frozen reference-control phase is $($frozenResult.status); LaunchReady=false. Evidence: $($frozenResult.reportPath). $($frozenResult.failure)")
        exit 1
    }
    Write-Output "Retention 004 candidate evidence and M00 deterministic frozen-control checks passed. M00 is isolated from actor run R01; LaunchReady=false pending the root-controlled lifecycle gate. Evidence: $($frozenResult.reportPath)"
    exit 0
}

try {
    $sourceRevision = (& git -C $repo rev-parse HEAD | Out-String).Trim()
    $gitStatus = (& git -C $repo status --porcelain --untracked-files=all | Out-String)
    if ([string]::IsNullOrWhiteSpace($sourceRevision)) { throw 'Could not resolve the source revision.' }
    $dirty = -not [string]::IsNullOrWhiteSpace($gitStatus)
    $preflightSha256 = Get-Sha256 (Join-Path $repo 'scripts/Verify-RetentionPreflightV4.ps1')
    $sourceHashInventory = Get-SourceHashInventory

    if ([string]::IsNullOrWhiteSpace($EvidencePath)) {
        $reportPath = Get-UniqueEvidencePath 'report'
    } else {
        $requested = Resolve-RepoPath $EvidencePath
        if (-not (Test-ContainedPath $requested $evidenceRoot)) { throw 'EvidencePath must remain in the ignored study evidence directory.' }
        $reportPath = if (Test-Path -LiteralPath $requested) { Get-UniqueEvidencePath 'report' } else { $requested }
    }
    [IO.Directory]::CreateDirectory($evidenceRoot) | Out-Null
    if (Test-Path -LiteralPath $rawEvidenceRoot) { throw "Raw evidence directory already exists: $rawEvidenceRoot" }
    [IO.Directory]::CreateDirectory($rawEvidenceRoot) | Out-Null

    Assert-Check 'preflight control slot is initially absent' (-not (Test-Path -LiteralPath $fixtureRoot)) $fixtureRoot
    $frozenFiles = @(
        (Join-Path $studyRoot 'artifacts/global-freeze.json'),
        (Join-Path $baselineRoot 'flat/manifest.json'),
        (Join-Path $baselineRoot 'rich/manifest.json')
    )
    $missingFrozen = @($frozenFiles | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) })
    if ($missingFrozen.Count -eq 0) {
        $globalManifest = Read-Report $frozenFiles[0]
        Assert-Check 'global freeze belongs to the 004 study' ((Get-Field $globalManifest 'studyId') -ceq $studyId)
        $pendingControls.Add([ordered]@{name='clean global freeze and baseline live audits';status='pending';reason='Candidate controls do not claim the frozen launch gate; invoke -RequireFrozenControls after canonical archives and pins are staged.'})
    } else {
        $pendingControls.Add([ordered]@{name='clean global freeze and baseline live audits';status='pending';missing=@($missingFrozen | ForEach-Object { [IO.Path]::GetRelativePath($repo,$_) })})
    }

    if ($RequireFrozenControls) {
        $frozenPhaseFailure = if ($missingFrozen.Count -gt 0) { 'Required frozen controls cannot run until the committed global freeze and both baseline archives exist.' } else { 'Required frozen controls remain pending until the private M00 frozen-reference pin and V2 trace are available.' }
    }

    $resolvedCli = Resolve-RepoPath $CliDll
    $resolvedBusiness = Resolve-RepoPath $BusinessDll
    Assert-Check 'release CLI DLL exists' (Test-Path -LiteralPath $resolvedCli -PathType Leaf) $resolvedCli
    Assert-Check 'release Business DLL exists' (Test-Path -LiteralPath $resolvedBusiness -PathType Leaf) $resolvedBusiness
    Assert-Check 'runtime DLL inputs are repository-contained' ((Test-ContainedPath $resolvedCli $repo) -and (Test-ContainedPath $resolvedBusiness $repo)) @{cli=$resolvedCli;business=$resolvedBusiness}
    Assert-NoReparseTree (Split-Path -Parent $resolvedCli)
    Assert-NoReparseTree (Split-Path -Parent $resolvedBusiness)
    $cliHash = Get-Sha256 $resolvedCli
    $businessHash = Get-Sha256 $resolvedBusiness

    $oraclePath = Join-Path $studyRoot 'acceptance.json'
    $predecessorOraclePath = Join-Path $repo 'experiments/AgentLang.SubagentTrials/business-policy-001/acceptance.json'
    $designPath = Join-Path $studyRoot 'design.json'
    $primerPath = Join-Path $studyRoot 'language-primer.md'
    $predecessorPrimerPath = Join-Path $repo 'experiments/AgentLang.SubagentTrials/business-policy-help-002/language-primer.md'
    $oracle = Read-Report $oraclePath
    $predecessorOracle = Read-Report $predecessorOraclePath
    $design = Read-Report $designPath
    Assert-Check '004 and predecessor oracle sources exist' ($null -ne $oracle -and $null -ne $predecessorOracle -and $null -ne $design)
    Assert-Check '004 oracle predecessor hash binds exact 001 bytes' ($oracle.predecessorStudyId -ceq 'business-policy-001' -and $oracle.predecessorSha256 -ceq (Get-Sha256 $predecessorOraclePath)) @{expected=$oracle.predecessorSha256;actual=(Get-Sha256 $predecessorOraclePath)}
    Assert-Check '004 task sequence is S01 then S07' ((@($oracle.sequence) -join ',') -ceq 'S01,S07' -and (@($design.sequence) -join ',') -ceq 'S01,S07') @{oracle=@($oracle.sequence);design=@($design.sequence)}
    $oracleRows = [Collections.Generic.List[object]]::new()
    foreach ($taskId in @('S01','S07')) {
        $newTasks = @($oracle.tasks | Where-Object { $_.id -ceq $taskId })
        $oldTasks = @($predecessorOracle.tasks | Where-Object { $_.id -ceq $taskId })
        Assert-Check "$taskId oracle exists exactly once in both studies" ($newTasks.Count -eq 1 -and $oldTasks.Count -eq 1)
        $newProjection = Get-OracleProjection $newTasks[0]
        $oldProjection = Get-OracleProjection $oldTasks[0]
        $equal = (Get-CanonicalJson $newProjection) -ceq (Get-CanonicalJson $oldProjection)
        Assert-Check "$taskId independent case corpus exactly matches business-policy-001" $equal @{caseCount=@($newTasks[0].cases).Count;expectedCases=if($taskId -eq 'S01'){10}else{54}}
        $oracleRows.Add([ordered]@{taskId=$taskId;cases=@($newTasks[0].cases).Count;sourcePath=[IO.Path]::GetRelativePath($repo,$oraclePath);sourceSha256=Get-Sha256 $oraclePath;predecessorSha256=Get-Sha256 $predecessorOraclePath;caseCorpusMatch=$equal})
    }
    $taskFiles = @(
        @{id='S01';symbol='customer.premium?';signature='Customer -> Bool'},
        @{id='S07';symbol='customer.discounted-balance';signature='Customer -> Money'}
    )
    foreach ($expectedTask in $taskFiles) {
        $taskPath = Join-Path $repo "experiments/AgentLang.Benchmarks/task-bank/public/$($expectedTask.id).json"
        $task = Read-Report $taskPath
        Assert-Check "$($expectedTask.id) public task contract matches oracle" (
            $null -ne $task -and $task.id -ceq $expectedTask.id -and
            $task.requiredPublicContract.symbol -ceq $expectedTask.symbol -and
            $task.requiredPublicContract.signature -ceq $expectedTask.signature -and
            @($oracle.tasks | Where-Object { $_.id -ceq $expectedTask.id }).Count -eq 1 -and
            (@($oracle.tasks | Where-Object { $_.id -ceq $expectedTask.id })[0].symbol -ceq $expectedTask.symbol)) @{path=[IO.Path]::GetRelativePath($repo,$taskPath);sha256=Get-Sha256 $taskPath}
    }
    $primerEqual = [Convert]::ToBase64String([IO.File]::ReadAllBytes($primerPath)) -ceq [Convert]::ToBase64String([IO.File]::ReadAllBytes($predecessorPrimerPath))
    Assert-Check '004 primer bytes equal the archived 002 common-help primer' $primerEqual @{primerSha256=Get-Sha256 $primerPath;predecessorPrimerSha256=Get-Sha256 $predecessorPrimerPath}
    $expectedCells = @(
        [ordered]@{runId='R01';block='B1';arm='flat';taskId='S01';sequenceIndex=1;baselineKind='flat';startingRule='flat-seed'},
        [ordered]@{runId='R02';block='B1';arm='flat';taskId='S07';sequenceIndex=2;baselineKind='flat';startingRule='flat-seed'},
        [ordered]@{runId='R03';block='B1';arm='retained';taskId='S01';sequenceIndex=1;baselineKind='rich';startingRule='rich-seed'},
        [ordered]@{runId='R04';block='B1';arm='retained';taskId='S07';sequenceIndex=2;baselineKind='rich';startingRule='accepted-same-block-retained-S01-only'},
        [ordered]@{runId='R05';block='B1';arm='reset-rich';taskId='S01';sequenceIndex=1;baselineKind='rich';startingRule='rich-seed'},
        [ordered]@{runId='R06';block='B1';arm='reset-rich';taskId='S07';sequenceIndex=2;baselineKind='rich';startingRule='rich-seed'},
        [ordered]@{runId='R07';block='B2';arm='reset-rich';taskId='S01';sequenceIndex=1;baselineKind='rich';startingRule='rich-seed'},
        [ordered]@{runId='R08';block='B2';arm='reset-rich';taskId='S07';sequenceIndex=2;baselineKind='rich';startingRule='rich-seed'},
        [ordered]@{runId='R09';block='B2';arm='flat';taskId='S01';sequenceIndex=1;baselineKind='flat';startingRule='flat-seed'},
        [ordered]@{runId='R10';block='B2';arm='flat';taskId='S07';sequenceIndex=2;baselineKind='flat';startingRule='flat-seed'},
        [ordered]@{runId='R11';block='B2';arm='retained';taskId='S01';sequenceIndex=1;baselineKind='rich';startingRule='rich-seed'},
        [ordered]@{runId='R12';block='B2';arm='retained';taskId='S07';sequenceIndex=2;baselineKind='rich';startingRule='accepted-same-block-retained-S01-only'}
    )
    $actualCells = @($design.cells | ForEach-Object { [ordered]@{runId=$_.runId;block=$_.block;arm=$_.arm;taskId=$_.taskId;sequenceIndex=[int]$_.sequenceIndex;baselineKind=$_.baselineKind;startingRule=$_.startingRule} })
    Assert-Check 'design preregisters the exact twelve rotated cells' ((Get-CanonicalJson @($actualCells)) -ceq (Get-CanonicalJson @($expectedCells))) @{expected=@($expectedCells);actual=@($actualCells)}

    $controlSlotOwner = [IO.Path]::GetFullPath($fixtureRoot)
    if (Test-Path -LiteralPath $controlSlotOwner) { throw "Preflight-owned canonical control slot already exists; preserving it: $controlSlotOwner" }
    if (-not (Test-ContainedPath $controlSlotOwner $fixtureParent)) { throw 'Control slot path escaped its designated ignored parent.' }
    [IO.Directory]::CreateDirectory($controlSlotOwner) | Out-Null
    $controlSlotOwned = $true
    $bootstrapLocal = Join-Path $controlSlotOwner 'bootstrap/local'
    $bootstrapRuns = Join-Path $controlSlotOwner 'bootstrap/runs'
    $bootstrapArgs = @('-BootstrapOnly','-CliDll',$resolvedCli,'-BusinessDll',$resolvedBusiness,'-LocalRoot',$bootstrapLocal,'-RunRoot',$bootstrapRuns)
    $bootstrapProcess = Invoke-StudyScript 'bootstrap-only-fresh-seeds' $preparerPath $bootstrapArgs
    $bootstrapResultOutput = $null
    if (-not [string]::IsNullOrWhiteSpace($bootstrapProcess.stdout)) { $bootstrapResultOutput = ConvertFrom-Json -InputObject $bootstrapProcess.stdout -AsHashtable -Depth 100 }
    $bootstrapEvidence = [string](Get-Field $bootstrapResultOutput 'evidencePath')
    $preparationEvidenceRoot = Join-Path $repo '.agentlang/business-policy-retention-004/preparation-evidence'
    if (-not [string]::IsNullOrWhiteSpace($bootstrapEvidence) -and (Test-ContainedPath $bootstrapEvidence $preparationEvidenceRoot) -and (Test-Path -LiteralPath $bootstrapEvidence -PathType Leaf)) {
        $bootstrapRawReport = Join-Path $rawEvidenceRoot 'bootstrap-preparation.json'
        [IO.File]::Copy($bootstrapEvidence,$bootstrapRawReport,$false)
    }
    $bootstrapReport = Read-Report $bootstrapEvidence
    Assert-Check 'fresh BootstrapOnly preparation succeeds' ($bootstrapProcess.exitCode -eq 0 -and $null -ne $bootstrapResultOutput -and (Get-Field $bootstrapResultOutput 'bootstrapOnly') -eq $true -and $null -ne $bootstrapReport -and $bootstrapReport.passed -eq $true -and $bootstrapReport.bootstrapOnly -eq $true) @{exitCode=$bootstrapProcess.exitCode;failure=Get-Field $bootstrapReport 'failure';requestedOutput=[string](Get-Field $bootstrapResultOutput 'evidencePath');rawEvidencePath=$(if(Test-Path -LiteralPath (Join-Path $rawEvidenceRoot 'bootstrap-preparation.json')){Join-Path $rawEvidenceRoot 'bootstrap-preparation.json'}else{$null})}
    $baselineState = Get-Field (Get-Field $bootstrapReport 'result') 'baselines'
    foreach ($kind in @('flat','rich')) {
        $baseline = Get-Field $baselineState $kind
        $expected = $expectedCounts[$kind]
        $seed = [IO.Path]::GetFullPath([string](Get-Field $baseline 'path'))
        Assert-Check "$kind fresh seed has candidate counts" ((Get-CanonicalJson (Get-Field $baseline 'counts')) -ceq (Get-CanonicalJson $expected)) @{expected=$expected;actual=Get-Field $baseline 'counts';seed=$seed}
        Assert-Check "$kind fresh seed project exists inside preflight control slot" ((Test-Path -LiteralPath $seed -PathType Container) -and (Test-ContainedPath $seed $controlSlotOwner)) $seed
        Assert-NoReparseTree $seed
        $treeBefore = Get-TreeHash $seed
        $live = Invoke-CliRequests $seed @([ordered]@{op='words'},[ordered]@{op='test-all'}) "$kind-fresh-seed-live-check"
        Assert-Check "$kind fresh seed live CLI inspection succeeds" ($live.exitCode -eq 0 -and @($live.responses | Where-Object { (Get-Field $_ 'ok') -ne $true }).Count -eq 0)
        $wordRows = @((Get-Field (Get-Field $live.responses[0] 'data') 'words') | Where-Object { ([string](Get-Field $_ 'id')).StartsWith('word_',[StringComparison]::Ordinal) })
        $typeNames = @(Get-SourceTypes $baseline)
        $testRows = @((Get-Field (Get-Field $live.responses[1] 'data') 'results'))
        $names = @($wordRows | ForEach-Object { [string](Get-Field $_ 'name') })
        $presentTargets = @($forbiddenTargets | Where-Object { $_ -cin $names })
        $countsMatch = @($wordRows).Count -eq [int]$expected.words -and @($typeNames).Count -eq [int]$expected.types -and @($testRows).Count -eq [int]$expected.tests -and @($testRows | Where-Object { (Get-Field $_ 'passed') -ne $true }).Count -eq 0
        Assert-Check "$kind fresh seed live word/type/test counts match" $countsMatch @{expected=$expected;actual=[ordered]@{words=@($wordRows).Count;types=@($typeNames).Count;tests=@($testRows).Count};typeNames=@($typeNames);failedTests=@($testRows | Where-Object { (Get-Field $_ 'passed') -ne $true })}
        Assert-Check "$kind fresh seed has no target policy symbols" ($presentTargets.Count -eq 0) @{forbidden=$forbiddenTargets;present=$presentTargets}
        $treeAfter = Get-TreeHash $seed
        Assert-Check "$kind live fresh-seed inspection leaves bytes unchanged" ($treeBefore -ceq $treeAfter) @{before=$treeBefore;after=$treeAfter}
        $baselineResults.Add([ordered]@{
            kind=$kind;mode=Get-Field $baseline 'mode';path=$seed;files=@(Get-Field $baseline 'files')
            inventorySha256=Get-Field $baseline 'inventorySha256';counts=$expected
            liveCounts=[ordered]@{words=@($wordRows).Count;types=@($typeNames).Count;tests=@($testRows).Count}
            targetSymbolsPresent=$presentTargets;sourceInputs=Get-Field $baseline 'sourceInputs'
            runtime=[ordered]@{cliSha256=$cliHash;businessSha256=$businessHash}
        })
    }

    $flatSeed = [string](Get-Field (Get-Field $baselineState 'flat') 'path')
    $richSeed = [string](Get-Field (Get-Field $baselineState 'rich') 'path')
    $candidateProjects = Join-Path $controlSlotOwner 'projects'
    [IO.Directory]::CreateDirectory($candidateProjects) | Out-Null

    $positiveS01 = Join-Path $m00PredecessorRoot 'accepted/project'
    Copy-Project $flatSeed $positiveS01
    $sourceS01 = Get-CandidateSource 'S01'
    $authorS01 = Invoke-CliRequests $positiveS01 @(
        [ordered]@{op='define';frontend='flow';source=$sourceS01},
        [ordered]@{op='test-all'},
        [ordered]@{op='commit';library=$true}
    ) 'candidate-correct-S01-authoring'
    Assert-ControlResponses 'candidate-correct-S01-authoring' $authorS01.responses $true
    $positiveS01Result = Invoke-VerifierControl 'correct-S01-reference' 'flat' 'S01' $positiveS01 $null $true 'all 10 independent S01 oracle cases'
    Assert-ExpectedCandidateBehavior 'correct S01 reference' $positiveS01Result.record 10
    Assert-Check 'correct S01 candidate reports protocol-only unfrozen control classification' ($positiveS01Result.record.resultKind -ceq 'unfrozen-preflight-or-control')
    $acceptedS01Fixture = Save-CandidatePredecessorFixture 'accepted' $positiveS01Result $positiveS01 $true

    $failedS01Project = Join-Path $m00PredecessorRoot 'failed/project'
    Copy-Project $flatSeed $failedS01Project
    $failedS01Source = Get-CandidateSource 'S01' -WrongFormula
    $authorFailedS01 = Invoke-CliRequests $failedS01Project @(
        [ordered]@{op='define';frontend='flow';source=$failedS01Source},
        [ordered]@{op='test-all'},
        [ordered]@{op='commit';library=$true}
    ) 'candidate-failed-S01-authoring'
    Assert-ControlResponses 'candidate-failed-S01-authoring' $authorFailedS01.responses $false
    $failedS01LocalTests = @((Get-Field (Get-Field $authorFailedS01.responses[1] 'data') 'results') | Where-Object { (Get-Field $_ 'passed') -ne $true })
    Assert-Check 'failed S01 fixture has successful protocol authoring but a real local behavior failure' ($failedS01LocalTests.Count -gt 0) @{failedLocalTestCount=$failedS01LocalTests.Count;failedTests=$failedS01LocalTests}
    $failedS01Result = Invoke-VerifierControl 'failed-S01-fallback-reference' 'retained' 'S01' $failedS01Project $null $false 'candidate S01 deliberately fails complete independent behavior oracle'
    $failedS01Behavior = Get-Field $failedS01Result.record 'behavior'
    Assert-Check 'failed S01 fixture executes all ten independent oracle cases' ($failedS01Result.record.behaviorPassed -eq $false -and [int](Get-Field $failedS01Behavior 'casesExpected') -eq 10 -and [int](Get-Field $failedS01Behavior 'casesExecuted') -eq 10 -and [int](Get-Field $failedS01Behavior 'casesRecorded') -eq 10 -and @((Get-Field $failedS01Behavior 'cases') | Where-Object { (Get-Field $_ 'executed') -ne $true }).Count -eq 0) @{casesExpected=Get-Field $failedS01Behavior 'casesExpected';casesExecuted=Get-Field $failedS01Behavior 'casesExecuted';failingCaseIds=@((Get-Field $failedS01Behavior 'cases') | Where-Object { (Get-Field $_ 'passed') -ne $true } | ForEach-Object { [string](Get-Field $_ 'id') })}
    $failedS01Fixture = Save-CandidatePredecessorFixture 'failed' $failedS01Result $failedS01Project $false
    Invoke-CandidatePredecessorPrepare 'accepted-S01-carry-forward-M00' $acceptedS01Fixture $false $true 'accepted-s01-carry-forward'
    Invoke-CandidatePredecessorPrepare 'failed-S01-baseline-fallback-M00' $failedS01Fixture $true $true 'baseline-fallback-after-failed-S01'

    $negativeControls = @(
        @{label='wrong-cell';mutation='wrong-cell';diagnostic='not same-block retained S01 evidence'},
        @{label='wrong-acceptance-hash';mutation='wrong-hash';diagnostic='predecessor audit failed or does not bind'},
        @{label='wrong-audit-tree-hash';mutation='wrong-tree';diagnostic='predecessor audit failed or does not bind'},
        @{label='project-tree-changed';mutation='project-tree-changed';diagnostic='Accepted S01 predecessor project path or tree hash'},
        @{label='altered-passed-flag';mutation='altered-passed';diagnostic='not same-block retained S01 evidence'},
        @{label='altered-frozen-flag';mutation='altered-frozen-flag';diagnostic='not same-block retained S01 evidence'},
        @{label='missing-audit';mutation='missing-audit';diagnostic='requires the passing predecessor audit'}
    )
    foreach ($negative in $negativeControls) {
        $variant = New-CandidatePredecessorVariant ([string]$negative.label) $acceptedS01Fixture ([string]$negative.mutation)
        Invoke-NegativeCandidatePredecessorPrepare ([string]$negative.label) $variant ([string]$negative.diagnostic)
    }

    $positiveS07 = Join-Path $candidateProjects 'positive-S07'
    Copy-Project $richSeed $positiveS07
    $sourceS07 = Get-CandidateSource 'S07'
    $authorS07 = Invoke-CliRequests $positiveS07 @(
        [ordered]@{op='define';frontend='flow';source=$sourceS07},
        [ordered]@{op='test-all'},
        [ordered]@{op='commit';library=$true}
    ) 'candidate-correct-S07-authoring'
    Assert-ControlResponses 'candidate-correct-S07-authoring' $authorS07.responses $true
    $positiveS07Result = Invoke-VerifierControl 'correct-S07-reference' 'retained' 'S07' $positiveS07 $null $true 'all 54 independent S07 oracle cases'
    Assert-ExpectedCandidateBehavior 'correct S07 reference' $positiveS07Result.record 54
    Assert-Check 'correct S07 candidate reports protocol-only unfrozen control classification' ($positiveS07Result.record.resultKind -ceq 'unfrozen-preflight-or-control')

    $noOpProject = Join-Path $candidateProjects 'no-op-S01'
    Copy-Project $flatSeed $noOpProject
    $noOpResult = Invoke-VerifierControl 'no-op-missing-S01' 'flat' 'S01' $noOpProject $null $false 'target exists in the fresh actor project = false; oracle execution stops before behavioral cases'
    $noOpTargetCheck = @((Get-Field $noOpResult.record 'checks') | Where-Object { (Get-Field $_ 'name') -ceq 'target exists in the fresh actor project' })
    $noOpBehavior = Get-Field $noOpResult.record 'behavior'
    $noOpTargetUsable = Get-Field $noOpBehavior 'targetUsable'
    Assert-Check 'no-op fails at the intended missing-target diagnostic' ($noOpTargetUsable -eq $false -and $noOpTargetCheck.Count -eq 1 -and $noOpTargetCheck[0].passed -eq $false -and $noOpResult.record.safetyStop -match 'customer\.premium\?') @{check=$noOpTargetCheck;targetUsable=$noOpTargetUsable;safetyStop=$noOpResult.record.safetyStop}
    Assert-Check 'no-op diagnostic precedes independent S01 case execution' ([int](Get-Field (Get-Field $noOpResult.record 'behavior') 'casesExecuted') -eq 0)

    $wrongFormulaProject = Join-Path $candidateProjects 'wrong-formula-S07'
    Copy-Project $richSeed $wrongFormulaProject
    $wrongSource = Get-CandidateSource 'S07' -WrongFormula
    $authorWrong = Invoke-CliRequests $wrongFormulaProject @(
        [ordered]@{op='define';frontend='flow';source=$wrongSource},
        [ordered]@{op='test-all'},
        [ordered]@{op='commit';library=$true}
    ) 'candidate-wrong-formula-S07-authoring'
    Assert-ControlResponses 'candidate-wrong-formula-S07-authoring' $authorWrong.responses $true
    $wrongResult = Invoke-VerifierControl 'wrong-formula-S07' 'retained' 'S07' $wrongFormulaProject $null $false 'premium-2: one minor unit must truncate 0.9 toward zero to 0'
    $wrongCases = @((Get-Field (Get-Field $wrongResult.record 'behavior') 'cases') | Where-Object { (Get-Field $_ 'passed') -ne $true })
    $wrongCaseSet = @((Get-Field (Get-Field $wrongResult.record 'behavior') 'cases'))
    $premiumBoundaryRows = @($wrongCaseSet | Where-Object { (Get-Field $_ 'id') -ceq 'premium-2' })
    $premiumBoundary = if ($premiumBoundaryRows.Count -eq 1) { $premiumBoundaryRows[0] } else { $null }
    $premiumActual = Get-Field (Get-Field $premiumBoundary 'actual') 'structured'
    $premiumExpected = Get-Field $premiumBoundary 'expected'
    $premiumActualMinor = Get-Field (Get-Field $premiumActual 'value') 'value'
    $premiumExpectedMinor = Get-Field $premiumExpected 'value'
    $premiumActualType = Get-Field $premiumActual 'name'
    $premiumExpectedType = Get-Field $premiumExpected 'type'
    $canonicalMinorPattern = '^(0|-?[1-9][0-9]*)$'
    $premiumActualMinorText = [string]$premiumActualMinor
    $premiumExpectedMinorText = [string]$premiumExpectedMinor
    $premiumActualMinorValid = $premiumActualMinorText -cmatch $canonicalMinorPattern
    $premiumExpectedMinorValid = $premiumExpectedMinorText -cmatch $canonicalMinorPattern
    $premiumWasValueMismatch = $null -ne $premiumBoundary -and (Get-Field $premiumBoundary 'executed') -eq $true -and (Get-Field $premiumBoundary 'responseOk') -eq $true -and [string]$premiumActualType -ceq 'Money' -and [string]$premiumExpectedType -ceq 'Money' -and (Get-Field (Get-Field $premiumActual 'value') 'kind') -ceq 'int' -and $premiumActualMinorValid -and $premiumExpectedMinorValid -and $premiumActualMinorText -cne $premiumExpectedMinorText
    Assert-Check 'wrong S07 source has successful authoring and local test results before independent checking' (@($authorWrong.responses | Where-Object { (Get-Field $_ 'ok') -ne $true }).Count -eq 0 -and @((Get-Field (Get-Field $authorWrong.responses[1] 'data') 'results') | Where-Object { (Get-Field $_ 'passed') -ne $true }).Count -eq 0)
    $wrongBehavior = Get-Field $wrongResult.record 'behavior'
    $wrongTargetUsable = Get-Field $wrongBehavior 'targetUsable'
    Assert-Check 'wrong S07 formula fails the independent oracle after all 54 cases execute' ($wrongResult.record.behaviorPassed -eq $false -and $wrongResult.record.metadataPassed -eq $false -and $wrongTargetUsable -eq $true -and [int](Get-Field $wrongBehavior 'casesExpected') -eq 54 -and [int](Get-Field $wrongBehavior 'casesExecuted') -eq 54 -and $wrongCaseSet.Count -eq 54 -and @($wrongCaseSet | Where-Object { (Get-Field $_ 'executed') -ne $true }).Count -eq 0) @{targetUsable=$wrongTargetUsable;caseCount=$wrongCaseSet.Count}
    Assert-Check 'wrong S07 formula has an actual premium-2 value mismatch' $premiumWasValueMismatch @{case=$premiumBoundary;actualType=$premiumActualType;expectedType=$premiumExpectedType;actualMinor=$premiumActualMinor;expectedMinor=$premiumExpectedMinor}
    Assert-Check 'wrong S07 formula failure is not only a runtime or protocol error' ($null -ne $premiumBoundary -and (Get-Field $premiumBoundary 'responseOk') -eq $true -and (Get-Field $premiumBoundary 'diagnostic') -notmatch 'protocol|runtime|evaluation failed') @{case=$premiumBoundary}

    $malformedRoot = Join-Path $controlSlotOwner 'malformed-predecessor'
    $malformedLocal = Join-Path $malformedRoot 'local'
    $malformedRuns = Join-Path $malformedRoot 'runs'
    $missingInputEvidence = Join-Path $rawEvidenceRoot 'predecessor-without-project.json'
    Write-NewTextFile $missingInputEvidence '{"schemaVersion":1,"studyId":"business-policy-retention-004","passed":true}'
    $malformedProject = Join-Path $malformedRoot 'wrong-predecessor-project'
    Copy-Project $richSeed $malformedProject
    $prepareEvidence = Join-Path $rawEvidenceRoot 'malformed-predecessor-preparation.json'
    $missingInputArgs = @('-Arm','retained','-Block','B1','-TaskId','S07','-CliDll',$resolvedCli,'-BusinessDll',$resolvedBusiness,'-PreviousAcceptance',$missingInputEvidence,'-LocalRoot',$malformedLocal,'-RunRoot',$malformedRuns,'-EvidencePath',$prepareEvidence)
    $missingInputProcess = Invoke-StudyScript 'predecessor-without-project-preparation' $preparerPath $missingInputArgs
    $missingInputReport = Read-Report $prepareEvidence
    $expectedReject = [regex]::Replace([string]$missingInputProcess.stderr,'\s+',' ').Trim()
    $runCopy = Join-Path $malformedRuns 'R04'
    $actorCopy = Join-Path $malformedLocal 'actors/R04'
    Assert-Check 'S01 predecessor acceptance without project is rejected by the explicit carry-forward guard before copy' ($missingInputProcess.exitCode -ne 0 -and $expectedReject -match 'Retained S07 requires both accepted same-block S01 -PreviousProject and -PreviousAcceptance; fallback is(?:\s*\|\s*)?\s*explicit\.' -and -not (Test-Path -LiteralPath $runCopy) -and -not (Test-Path -LiteralPath $actorCopy)) @{exitCode=$missingInputProcess.exitCode;failure=$expectedReject;runCopyCreated=(Test-Path -LiteralPath $runCopy);actorCopyCreated=(Test-Path -LiteralPath $actorCopy);jsonParsed=$false;reportPathWritten=($null -ne $missingInputReport)}
    $pendingControls.Add([ordered]@{name='malformed S01 acceptance JSON after archive baseline gate';status='pending';reason='Candidate control proves that a missing predecessor project rejects before any run/actor copy. It does not claim to parse malformed JSON; that check requires the committed frozen baseline archives, which preparation gates first.'})

    $pinRejectActor = Join-Path $m00ControlRoot 'actors/M00'
    Assert-Check 'M00 pin-rejection run and actor control slots are initially absent' (-not (Test-Path -LiteralPath $pinRejectOutputRoot) -and -not (Test-Path -LiteralPath $pinRejectActor)) @{run=$pinRejectOutputRoot;actor=$pinRejectActor}
    if (Test-Path -LiteralPath $pinRejectOutputRoot) { throw "Preflight M00 pin-rejection slot already exists; preserving it: $pinRejectOutputRoot" }
    if (Test-Path -LiteralPath $pinRejectActor) { throw "Preflight M00 actor control slot already exists; preserving it: $pinRejectActor" }
    if (-not (Test-ContainedPath $pinRejectOutputRoot $m00ControlRoot) -or -not (Test-ContainedPath $pinRejectActor $m00ControlRoot)) { throw 'M00 pin-rejection paths escaped the dedicated ignored control root.' }
    [IO.Directory]::CreateDirectory($pinRejectOutputRoot) | Out-Null
    Write-NewTextFile $pinRejectOutputMarker $evidenceId
    $pinRejectOutputOwned = $true
    $pinRejectStart = Join-Path $pinRejectOutputRoot 'starting-project'
    Copy-Project $flatSeed $pinRejectStart
    [IO.Directory]::CreateDirectory((Split-Path -Parent $pinRejectActor)) | Out-Null
    Copy-Project $flatSeed $pinRejectActor
    $pinRejectActorOwned = $true
    $pinRejectArgs = @('-Arm','flat','-Block','B1','-TaskId','S01','-RunId',$pinRejectRunId,'-ProjectPath',$pinRejectActor,'-StartingProjectPath',$pinRejectStart,'-CliDll',$resolvedCli,'-EvidencePath',$pinRejectOutputPath,'-RequireFrozenPin','-M00Control')
    $pinRejectProcess = Invoke-StudyScript 'missing-frozen-pin-rejection' $verifierPath $pinRejectArgs
    $pinRejectReport = Read-Report $pinRejectOutputPath
    $pinRejectRawEvidence = Join-Path $rawEvidenceRoot 'required-pin-rejection.acceptance.json'
    if ($null -ne $pinRejectReport) { [IO.File]::Copy($pinRejectOutputPath,$pinRejectRawEvidence,$false) }
    $pinRejectRuns = @((Get-Field $pinRejectReport 'processRuns'))
    $missingPinCheck = @((Get-Field $pinRejectReport 'checks') | Where-Object { (Get-Field $_ 'name') -ceq 'required frozen prelaunch pin exists' })
    $validDestinationCheck = @((Get-Field $pinRejectReport 'checks') | Where-Object { (Get-Field $_ 'name') -ceq 'frozen acceptance destination is the canonical run acceptance.json' })
    $pinRejectIdentityCheck = @((Get-Field $pinRejectReport 'checks') | Where-Object { (Get-Field $_ 'name') -ceq 'arm, block, task, and run identity match the canonical rotated design' })
    Assert-Check 'M00 pin rejection has valid control identity and canonical private output validation' ($pinRejectIdentityCheck.Count -eq 1 -and $pinRejectIdentityCheck[0].passed -eq $true -and $validDestinationCheck.Count -eq 1 -and $validDestinationCheck[0].passed -eq $true -and [IO.Path]::GetFullPath([string](Get-Field $pinRejectReport 'evidencePath')) -ieq [IO.Path]::GetFullPath($pinRejectOutputPath)) @{runId=$pinRejectRunId;identityCheck=$pinRejectIdentityCheck;destinationCheck=$validDestinationCheck;acceptancePath=$pinRejectOutputPath;rawEvidencePath=$pinRejectRawEvidence;rawEvidenceSha256=if(Test-Path -LiteralPath $pinRejectRawEvidence){Get-Sha256 $pinRejectRawEvidence}else{$null}}
    Assert-Check 'required missing pin is rejected at the intended guard' ($pinRejectProcess.exitCode -ne 0 -and (Get-Field $pinRejectReport 'checkFrozenPin') -eq $false -and (Get-Field (Get-Field $pinRejectReport 'frozenPin') 'status') -ceq 'required pin missing' -and $missingPinCheck.Count -eq 1 -and $missingPinCheck[0].passed -eq $false) @{runId=$pinRejectRunId;pinStatus=Get-Field (Get-Field $pinRejectReport 'frozenPin') 'status';missingPinCheck=$missingPinCheck}
    Assert-Check 'M00 frozen pin rejection launches zero CLI processes and does not enter canonical actor R01 paths' ($pinRejectRuns.Count -eq 0 -and -not (Test-Path -LiteralPath (Join-Path $preflightRunsRoot 'R01')) -and -not (Test-Path -LiteralPath (Join-Path $repo '.agentlang/business-policy-retention-004/actors/R01'))) @{processRuns=$pinRejectRuns;checks=@((Get-Field $pinRejectReport 'checks') | Where-Object { (Get-Field $_ 'name') -match 'frozen|execution' });canonicalActorRunPathsTouched=$false}
    $tamperedAcceptanceRaw = Join-Path $rawEvidenceRoot 'tampered-pin-rejection.acceptance.json'
    if (Test-Path -LiteralPath $tamperedAcceptanceRaw) { throw "Refusing to replace tampered-pin raw evidence: $tamperedAcceptanceRaw" }
    [IO.File]::Copy($pinRejectOutputPath,$tamperedAcceptanceRaw,$false)
    $tamperedPinPath = Join-Path $pinRejectOutputRoot 'prelaunch.json'
    if (Test-Path -LiteralPath $tamperedPinPath) { throw "M00 tampered-pin fixture already exists; preserving it: $tamperedPinPath" }
    Remove-Item -LiteralPath $pinRejectOutputPath -Force
    $tamperedPin = [ordered]@{
        schemaVersion=1;studyId=$studyId;arm='flat';block='B1';taskId='S01';sequenceIndex=1;runId='M00-tampered'
        controlOnly=$true;controlKind='frozen-reference-control';launchable=$false;modelActorLaunched=$false
        projectPath=$pinRejectActor;startingProjectPath=[IO.Path]::GetRelativePath($repo,$pinRejectStart).Replace('\','/')
        startingStatePath=[IO.Path]::GetRelativePath($repo,(Join-Path $pinRejectOutputRoot 'starting-state.json')).Replace('\','/')
        startingStateSha256='';promptPath='prompt.txt';promptSha256='';promptUtf8Bytes=0
        sourceRevision='tampered';dirty=$true
        globalFreezePath='experiments/AgentLang.SubagentTrials/business-policy-retention-004/artifacts/global-freeze.json';globalFreezeSha256=('0' * 64)
    }
    Write-NewTextFile $tamperedPinPath ((ConvertTo-Json -InputObject $tamperedPin -Depth 20) + "`n")
    $tamperedPinProcess = Invoke-StudyScript 'tampered-frozen-pin-rejection' $verifierPath $pinRejectArgs
    $tamperedPinReport = Read-Report $pinRejectOutputPath
    $tamperedPinIdentityCheck = @((Get-Field $tamperedPinReport 'checks') | Where-Object { (Get-Field $_ 'name') -ceq 'frozen pin schema, study, arm, block, task, sequence, and run match' })
    $tamperedPinRuns = @((Get-Field $tamperedPinReport 'processRuns'))
    Assert-Check 'tampered M00 pin is rejected at exact identity guard before any CLI process' ($tamperedPinProcess.exitCode -ne 0 -and $tamperedPinIdentityCheck.Count -eq 1 -and $tamperedPinIdentityCheck[0].passed -eq $false -and $tamperedPinRuns.Count -eq 0) @{runId=$pinRejectRunId;tamperedRunId=(Get-Field (Get-Field $tamperedPinReport 'frozenPin') 'data' | ForEach-Object { Get-Field $_ 'runId' });identityCheck=$tamperedPinIdentityCheck;processRuns=$tamperedPinRuns;failure=Get-Field $tamperedPinReport 'failure';rawAcceptancePath=$tamperedAcceptanceRaw;rawAcceptanceSha256=Get-Sha256 $tamperedAcceptanceRaw}
    $predecessorControlResults.Add([ordered]@{name='tampered-pin-rejection';status='passed';controlOnly=$true;controlKind='frozen-reference-pin-negative-control';runId='M00';expectedDiagnostic='frozen pin schema, study, arm, block, task, sequence, and run match';identityCheck=$tamperedPinIdentityCheck;processRuns=$tamperedPinRuns;pinPath=$tamperedPinPath;pinSha256=Get-Sha256 $tamperedPinPath;acceptanceEvidencePath=$tamperedAcceptanceRaw;acceptanceEvidenceSha256=Get-Sha256 $tamperedAcceptanceRaw;verifierProcess=$tamperedPinProcess.record;arguments=$pinRejectArgs})

    $candidatePassed = $true
    $frozenPhaseStatus = 'pending'
    $pendingControls.Add([ordered]@{name='genuine frozen baseline, model/source/close tamper, and V2 trace controls';status='pending';reason='No canonical global freeze and actual per-cell pins/traces are available in this candidate phase; no actor or host session is launched here.'})
    if ($RequireFrozenControls) {
        if ($null -eq $frozenPhaseFailure) { $frozenPhaseFailure = 'Frozen control mode was requested, but no genuine frozen V2 trace and per-cell pin were available; these controls remain pending.' }
        $frozenPhaseStatus = 'pending'
    }
    $exitSuccess = $candidatePassed -and -not $RequireFrozenControls
} catch {
    $failure = $_.Exception.Message
    $exitSuccess = $false
} finally {
    if ($pinRejectOutputOwned) {
        try {
            $resolvedPinOutput = [IO.Path]::GetFullPath($pinRejectOutputRoot)
            $canonicalPinOutput = [IO.Path]::GetFullPath((Join-Path $m00ControlRoot 'runs/M00'))
            $ownerToken = if (Test-Path -LiteralPath $pinRejectOutputMarker -PathType Leaf) { (Get-Content -LiteralPath $pinRejectOutputMarker -Raw).Trim() } else { $null }
            if ($resolvedPinOutput -cne $canonicalPinOutput -or -not (Test-ContainedPath $resolvedPinOutput $m00ControlRoot) -or $ownerToken -cne $evidenceId) { throw 'Refusing cleanup of an unowned M00 pin-rejection output directory.' }
            Assert-NoReparseTree $resolvedPinOutput
            Remove-Item -LiteralPath $resolvedPinOutput -Recurse -Force
            $pinRejectOutputCleanupPassed = -not (Test-Path -LiteralPath $resolvedPinOutput)
        } catch {
            $pinRejectOutputCleanupPassed = $false
            if ($null -eq $failure) { $failure = "Pin-rejection output cleanup failed: $($_.Exception.Message)" }
            else { $failure += "; pin-rejection output cleanup failed: $($_.Exception.Message)" }
            $exitSuccess = $false
        }
    }
    if ($pinRejectActorOwned -and (Test-Path -LiteralPath $pinRejectActor -PathType Container)) {
        try {
            $resolvedActor = [IO.Path]::GetFullPath($pinRejectActor)
            $canonicalActor = [IO.Path]::GetFullPath((Join-Path $m00ControlRoot 'actors/M00'))
            if ($resolvedActor -cne $canonicalActor -or -not (Test-ContainedPath $resolvedActor $m00ControlRoot)) { throw 'Refusing cleanup of an unowned M00 pin-rejection actor project.' }
            Assert-NoReparseTree $resolvedActor
            Remove-Item -LiteralPath $resolvedActor -Recurse -Force
            if (Test-Path -LiteralPath $resolvedActor) { throw 'M00 pin-rejection actor project remains after cleanup.' }
        } catch {
            if ($null -eq $failure) { $failure = "M00 pin-rejection actor cleanup failed: $($_.Exception.Message)" }
            else { $failure += "; M00 pin-rejection actor cleanup failed: $($_.Exception.Message)" }
            $exitSuccess = $false
        }
    }
    if ($controlSlotOwned -and (Test-Path -LiteralPath $fixtureRoot -PathType Container)) {
        try {
            $resolvedSlot = [IO.Path]::GetFullPath($fixtureRoot)
            $canonicalSlot = [IO.Path]::GetFullPath((Join-Path $fixtureParent 'retention-preflight-control-slot'))
            if ($resolvedSlot -cne $canonicalSlot -or -not (Test-ContainedPath $resolvedSlot $fixtureParent)) { throw 'Refusing unsafe preflight slot cleanup target.' }
            Assert-NoReparseTree $resolvedSlot
            Remove-Item -LiteralPath $resolvedSlot -Recurse -Force
            $cleanupPassed = -not (Test-Path -LiteralPath $resolvedSlot)
        } catch {
            $cleanupPassed = $false
            if ($null -eq $failure) { $failure = "Preflight control cleanup failed: $($_.Exception.Message)" }
            else { $failure += "; preflight control cleanup failed: $($_.Exception.Message)" }
            $exitSuccess = $false
        }
    } elseif (-not $controlSlotOwned) {
        $cleanupPassed = $null
    }
    $frozenPassed = $frozenPhaseStatus -ceq 'passed'
    $overallPassed = $candidatePassed -and $frozenPassed
    $flatArchivePresent = Test-Path -LiteralPath (Join-Path $baselineRoot 'flat/manifest.json') -PathType Leaf
    $richArchivePresent = Test-Path -LiteralPath (Join-Path $baselineRoot 'rich/manifest.json') -PathType Leaf
    $frozenPhase = [ordered]@{
        status=$frozenPhaseStatus
        required=[bool]$RequireFrozenControls
        passed=$frozenPassed
        failure=$frozenPhaseFailure
        globalFreezePresent=(Test-Path -LiteralPath (Join-Path $studyRoot 'artifacts/global-freeze.json') -PathType Leaf)
        baselineArchivesPresent=($flatArchivePresent -and $richArchivePresent)
        flatArchivePresent=$flatArchivePresent;richArchivePresent=$richArchivePresent
        controls=@($pendingControls)
    }
    $report = [ordered]@{
        schemaVersion=1;studyId=$studyId;phase='candidate-preflight'
        startedUtc=$startedUtc;finishedUtc=[DateTime]::UtcNow.ToString('O')
        passed=$overallPassed;candidatePassed=$candidatePassed;launchReady=$overallPassed
        failure=$failure;frozenPhase=$frozenPhase
        sourceRevision=$sourceRevision;dirty=$dirty
        preflightSha256=$preflightSha256;sourceArtifacts=@($sourceHashInventory)
        runtime=[ordered]@{cliPath=$resolvedCli;cliSha256=$cliHash;businessPath=$resolvedBusiness;businessSha256=$businessHash}
        oracle=[ordered]@{path=[IO.Path]::GetRelativePath($repo,(Join-Path $studyRoot 'acceptance.json'));sha256=if(Test-Path -LiteralPath (Join-Path $studyRoot 'acceptance.json')){Get-Sha256 (Join-Path $studyRoot 'acceptance.json')}else{$null};predecessorPath='experiments/AgentLang.SubagentTrials/business-policy-001/acceptance.json';predecessorSha256=if(Test-Path -LiteralPath (Join-Path $repo 'experiments/AgentLang.SubagentTrials/business-policy-001/acceptance.json')){Get-Sha256 (Join-Path $repo 'experiments/AgentLang.SubagentTrials/business-policy-001/acceptance.json')}else{$null};tasks=@($oracleRows)}
        design=[ordered]@{path='experiments/AgentLang.SubagentTrials/business-policy-retention-004/design.json';sha256=if(Test-Path -LiteralPath (Join-Path $studyRoot 'design.json')){Get-Sha256 (Join-Path $studyRoot 'design.json')}else{$null};cells=@($actualCells)}
        primer=[ordered]@{path='experiments/AgentLang.SubagentTrials/business-policy-retention-004/language-primer.md';sha256=if(Test-Path -LiteralPath (Join-Path $studyRoot 'language-primer.md')){Get-Sha256 (Join-Path $studyRoot 'language-primer.md')}else{$null};matches002=$primerEqual}
        baselineCandidates=@($baselineResults);controls=@($controlResults);predecessorControls=@($predecessorControlResults);processRuns=@($processRuns);checks=@($checks)
        cleanup=[ordered]@{controlSlotRemoved=$cleanupPassed;pinRejectionOutputRemoved=$pinRejectOutputCleanupPassed;rawControlEvidence=$rawEvidenceRoot;rawEvidenceRetained=$null -ne $rawEvidenceRoot -and (Test-Path -LiteralPath $rawEvidenceRoot -PathType Container)}
        requestedEvidencePath=$requestedEvidencePath;evidencePath=$reportPath
        claimLimits=@('Candidate behavior controls use the independent verifier against fresh scratch projects but are not frozen actor acceptances.','A candidate run does not authorize a model actor, prove a V2 host trace, or establish launch readiness.','Model tokens, turns, latency, and effective context size are not measured.')
    }
    try {
        if ([string]::IsNullOrWhiteSpace($reportPath)) { $reportPath = Get-UniqueEvidencePath 'report' }
        if (Test-Path -LiteralPath $reportPath) { $reportPath = Get-UniqueEvidencePath 'report-recovery' }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($reportPath)) | Out-Null
        Write-NewTextFile $reportPath ((ConvertTo-Json -InputObject $report -Depth 100) + "`n")
    } catch {
        $failure = if ($null -eq $failure) { "Could not write preflight evidence: $($_.Exception.Message)" } else { "$failure; could not write preflight evidence: $($_.Exception.Message)" }
        $exitSuccess = $false
    }
}

if (-not $exitSuccess) {
    [Console]::Error.WriteLine("Retention 004 candidate preflight failed or frozen controls remain required. Candidate=$candidatePassed Frozen=$frozenPhaseStatus. Evidence: $reportPath. $failure $frozenPhaseFailure")
    exit 1
}
Write-Output "Retention 004 candidate controls passed; frozen phase remains pending. LaunchReady=false. Evidence: $reportPath"
