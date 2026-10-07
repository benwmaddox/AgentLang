#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$EvidencePath = '.agentlang/reports/business-policy-preflight.json',
    [switch]$Isolated,
    [ValidateSet('growing','flat','conventional')][string[]]$Modes = @('growing','flat','conventional'),
    [ValidateSet('S01','S06','S07')][string[]]$TaskIds = @('S01','S06','S07'),
    [ValidateSet('Release','Debug')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$localRoot = Join-Path $repo '.agentlang/business-policy-001'
$cli = Join-Path $localRoot 'language-bin/AgentLang.Cli.dll'
if ($Isolated) {
    $localRoot = Join-Path $repo ('.agentlang/business-policy-preflight-' + [Guid]::NewGuid().ToString('N'))
    $cli = Join-Path $repo "src/AgentLang.Cli/bin/$Configuration/net9.0/AgentLang.Cli.dll"
}
$verifier = Join-Path $repo 'scripts/Verify-BusinessPolicyTrial.ps1'
$controlRoot = Join-Path $localRoot ('controls/preflight-' + [Guid]::NewGuid().ToString('N'))
$checks = [Collections.Generic.List[object]]::new()
$runs = [Collections.Generic.List[object]]::new()
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.Directory]::CreateDirectory($controlRoot) | Out-Null
function Check([string]$Name,[bool]$Passed,$Details=$null) {
    $checks.Add([ordered]@{ name=$Name; passed=$Passed; details=$Details })
    if (-not $Passed) { throw "Business policy preflight failed: $Name" }
}
function CopyProject([string]$Source,[string]$Destination) {
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $Source -Recurse -Force -File) {
        $relative = [IO.Path]::GetRelativePath($Source,$file.FullName)
        if ($relative -match '(^|[\\/])(bin|obj)([\\/]|$)') { continue }
        $target = Join-Path $Destination $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        [IO.File]::Copy($file.FullName,$target,$false)
    }
}
function LanguageControl([string]$Project,[string]$Mutation) {
    $premium = 'equals(customer::kind(customer), "premium")'
    if ($Mutation -eq 'trim') { $premium = 'equals(string::trim(customer::kind(customer)), "premium")' }
    $rate = if ($Mutation -eq 'rate') { '5000' } else { '1000' }
    $formula = @'
        let quotient = ::divide(amount, 10);
        let remainder = ::subtract(amount, ::multiply(quotient, 10));
        Money::new(::add(::multiply(quotient, 9), ::divide(::multiply(remainder, 9), 10)))
'@
    if ($Mutation -eq 'rounding') { $formula = '        Money::new(::subtract(amount, ::divide(amount, 10)))' }
    if ($Mutation -eq 'overflow') { $formula = '        Money::new(::divide(::multiply(amount, 9), 10))' }
    $customer = 'customer::new(id = CustomerId::new("10000000-0000-0000-0000-000000000001"), email = Email::new("ada@example.test"), kind = {0}, balance = Money::new(1050), created-at = Instant::new("2026-01-01T12:00:00.0000000+00:00"))'
    $premiumCustomer = $customer.Replace('{0}','"premium"')
    $regularCustomer = $customer.Replace('{0}','"regular"')
    $source = @"
word customer.premium?(customer: Customer) -> Bool {
    effects none
    doc "Classifies raw premium customer kind."
    $premium
}
test customer.premium?/premium { customer::premium?($premiumCustomer) => true }
test customer.premium?/regular { customer::premium?($regularCustomer) => false }
example customer.premium?/premium { customer::premium?($premiumCustomer) => true }
word customer.discount-basis-points(customer: Customer) -> Int {
    effects none
    doc "Returns the premium discount basis points."
    if customer::premium?(customer) { $rate } else { 0 }
}
test customer.discount-basis-points/premium { customer::discount-basis-points($premiumCustomer) => $rate }
test customer.discount-basis-points/regular { customer::discount-basis-points($regularCustomer) => 0 }
example customer.discount-basis-points/premium { customer::discount-basis-points($premiumCustomer) => $rate }
word customer.discounted-balance(customer: Customer) -> Money {
    effects none
    doc "Returns an exact discounted balance in signed minor units."
    let balance = customer::balance(customer);
    if int::greater-than(customer::discount-basis-points(customer), 0) {
        let amount = Money::value(balance);
$formula
    } else { balance }
}
test customer.discounted-balance/premium { Money::value(customer::discounted-balance($premiumCustomer)) => 945 }
test customer.discounted-balance/regular { Money::value(customer::discounted-balance($regularCustomer)) => 1050 }
example customer.discounted-balance/premium { Money::value(customer::discounted-balance($premiumCustomer)) => 945 }
"@
    $requests = @(
        [ordered]@{ op='define'; frontend='flow'; source=$source },
        [ordered]@{ op='test-all' },
        [ordered]@{ op='commit'; library=$true }
    )
    $lines = @($requests | ForEach-Object { ConvertTo-Json -InputObject $_ -Depth 30 -Compress })
    $output = @($lines | & dotnet $cli --project $Project --jsonl)
    Check "$Mutation control CLI exit" ($LASTEXITCODE -eq 0)
    Check "$Mutation control one response per request" ($output.Count -eq $requests.Count)
    $responses = @($output | ForEach-Object { ConvertFrom-Json -InputObject $_ -Depth 100 })
    Check "$Mutation control self-tests and library commit pass" (@($responses | Where-Object { -not $_.ok }).Count -eq 0 -and @($responses[1].data.results | Where-Object { -not $_.passed }).Count -eq 0) $responses
    return [ordered]@{ sourceSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($utf8.GetBytes($source))).ToLowerInvariant(); responses=$responses }
}
function ConventionalControl([string]$Project,[string]$Mutation) {
    $premium = if ($Mutation -eq 'trim') { 'customer.Kind.Trim() = "premium"' } else { 'customer.Kind = "premium"' }
    $rate = if ($Mutation -eq 'rate') { '5000L' } else { '1000L' }
    $formula = @'
            let q = amount / 10L
            let r = amount - q * 10L
            Domain.Money.ofMinorUnits (q * 9L + r * 9L / 10L)
'@
    if ($Mutation -eq 'rounding') { $formula = '            Domain.Money.ofMinorUnits (amount - amount / 10L)' }
    if ($Mutation -eq 'overflow') { $formula = '            Domain.Money.ofMinorUnits (Checked.(*) amount 9L / 10L)' }
    $operations = @"
namespace AgentLang.BusinessPolicy
open AgentLang.Business
module CustomerPolicies =
    /// Classifies raw premium customer kind.
    let isPremium (customer: Customer) : bool =
        $premium
    /// Returns discount basis points.
    let discountBasisPoints (customer: Customer) : int64 =
        if isPremium customer then $rate else 0L
    /// Returns exact discounted signed minor units.
    let discountedBalance (customer: Customer) : Domain.Money =
        if discountBasisPoints customer > 0L then
            let amount = Domain.Money.minorUnits customer.Balance
$formula
        else customer.Balance
"@
    [IO.File]::WriteAllText((Join-Path $Project 'Operations.fs'),$operations,$utf8)
    $testsPath = Join-Path $Project 'SelfTests.fs'
    $original = [IO.File]::ReadAllText($testsPath)
    $ownMarker = $original.IndexOf('    let runOwnTests', [StringComparison]::Ordinal)
    if ($ownMarker -lt 0) { throw 'Missing conventional own-test marker.' }
    $testTail = @"
    let runOwnTests () =
        let premium = Fixtures.customer "premium" 1050L
        let regular = Fixtures.customer "regular" 1050L
        if not (CustomerPolicies.isPremium premium) then failwith "premium"
        if CustomerPolicies.isPremium regular then failwith "regular"
        if CustomerPolicies.discountBasisPoints premium <> $rate then failwith "premium rate"
        if CustomerPolicies.discountBasisPoints regular <> 0L then failwith "regular rate"
        if AgentLang.Business.Domain.Money.minorUnits (CustomerPolicies.discountedBalance premium) <> 945L then failwith "premium balance"
        if AgentLang.Business.Domain.Money.minorUnits (CustomerPolicies.discountedBalance regular) <> 1050L then failwith "regular balance"
        printfn "PASS six own assertions"
    let runExamples () =
        let premium = Fixtures.customer "premium" 1050L
        if not (CustomerPolicies.isPremium premium) then failwith "premium example"
        if CustomerPolicies.discountBasisPoints premium <> $rate then failwith "basis-points example"
        let balance = AgentLang.Business.Domain.Money.minorUnits (CustomerPolicies.discountedBalance premium)
        if balance <> 945L then failwith "balance example"
        printfn "Examples: premium, basis points, discounted balance %d" balance
"@
    [IO.File]::WriteAllText($testsPath,$original.Substring(0,$ownMarker)+$testTail,$utf8)
    $buildOutput = @(& dotnet run --project (Join-Path $Project 'BusinessPolicy.fsproj') --configuration Release 2>&1 | ForEach-Object { $_.ToString() })
    Check "$Mutation conventional self-tests and examples pass" ($LASTEXITCODE -eq 0)
    return [ordered]@{ operationsSha256=(Get-FileHash -LiteralPath (Join-Path $Project 'Operations.fs')).Hash.ToLowerInvariant(); selfTestsSha256=(Get-FileHash -LiteralPath $testsPath).Hash.ToLowerInvariant(); output=$buildOutput }
}
$passed = $false
$failure = $null
try {
    if ($Isolated) {
        $businessDll = Join-Path $repo "experiments/AgentLang.Business/bin/$Configuration/net9.0/AgentLang.Business.dll"
        foreach ($mode in $Modes) {
            $null = & (Join-Path $repo 'scripts/Prepare-BusinessPolicyTrial.ps1') -Mode $mode -TaskId S01 -CliDll $cli -BusinessDll $businessDll -LocalRoot $localRoot -RunRoot (Join-Path $localRoot 'runs')
        }
    }
    foreach ($mode in $Modes) {
        $seed = Join-Path $localRoot "seeds/$mode/project"
        Check "$mode seed exists" (Test-Path -LiteralPath $seed -PathType Container)
        foreach ($control in @(
            @{name='correct';mutation='correct';tasks=@('S01','S06','S07');expected=$true},
            @{name='noop';mutation=$null;tasks=@('S01','S06','S07');expected=$false},
            @{name='wrong-trim';mutation='trim';tasks=@('S01');expected=$false},
            @{name='wrong-rate';mutation='rate';tasks=@('S06');expected=$false},
            @{name='wrong-rounding';mutation='rounding';tasks=@('S07');expected=$false},
            @{name='wrong-overflow';mutation='overflow';tasks=@('S07');expected=$false}
        )) {
            $selectedTasks = @($control.tasks | Where-Object { $_ -in $TaskIds })
            if ($selectedTasks.Count -eq 0) { continue }
            $project = Join-Path $controlRoot "$mode-$($control.name)"
            CopyProject $seed $project
            $selfValidation = $null
            if ($control.mutation) {
                $selfValidation = if ($mode -eq 'conventional') { ConventionalControl $project $control.mutation } else { LanguageControl $project $control.mutation }
            }
            foreach ($task in $selectedTasks) {
                $resultPath = Join-Path $controlRoot "$mode-$($control.name)-$task.json"
                $verifyOutput = @(& pwsh -NoProfile -File $verifier -Mode $mode -TaskId $task -ProjectPath $project -CliDll $cli -StartingProjectPath $seed -EvidencePath $resultPath 2>&1 | ForEach-Object { $_.ToString() })
                $verifyExit = $LASTEXITCODE
                Check "$mode $($control.name) $task verifier evidence exists" (Test-Path -LiteralPath $resultPath)
                $result = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json -Depth 100
                $runs.Add([ordered]@{ mode=$mode;control=$control.name;taskId=$task;expectedPassed=$control.expected;selfValidation=$selfValidation;acceptance=$result;output=$verifyOutput })
                Check "$mode $($control.name) $task matches independent expectation" ([bool]$result.passed -eq [bool]$control.expected -and (($verifyExit -eq 0) -eq [bool]$control.expected))
                Write-Output "$mode/$($control.name)/${task}: independent passed=$($result.passed), expected=$($control.expected)"
            }
        }
    }
    $passed = $true
} catch {
    $failure = $_.ToString()
} finally {
    $reportPath = [IO.Path]::GetFullPath($EvidencePath,$repo)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($reportPath)) | Out-Null
    $report = [ordered]@{ schemaVersion=1;studyId='business-policy-001';passed=$passed;failure=$failure;modes=$Modes;taskIds=$TaskIds;isolated=[bool]$Isolated;checks=@($checks);runs=@($runs);sourceRevision=(& git -C $repo rev-parse HEAD | Out-String).Trim();dirty=$true;oracleSha256=(Get-FileHash -LiteralPath (Join-Path $repo 'experiments/AgentLang.SubagentTrials/business-policy-001/acceptance.json')).Hash.ToLowerInvariant();verifierSha256=(Get-FileHash -LiteralPath $verifier).Hash.ToLowerInvariant();preflightSha256=(Get-FileHash -LiteralPath $PSCommandPath).Hash.ToLowerInvariant() }
    [IO.File]::WriteAllText($reportPath,($report | ConvertTo-Json -Depth 100)+"`n",$utf8)
    $resolvedRoot = [IO.Path]::GetFullPath($localRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
    $resolvedControls = [IO.Path]::GetFullPath($controlRoot)
    if (-not $resolvedControls.StartsWith($resolvedRoot,[StringComparison]::OrdinalIgnoreCase) -or -not [IO.Path]::GetFileName($resolvedControls).StartsWith('preflight-',[StringComparison]::Ordinal)) { throw 'Unsafe control cleanup path.' }
    Remove-Item -LiteralPath $resolvedControls -Recurse -Force
    if ($Isolated -and (Test-Path -LiteralPath $localRoot)) {
        $workspaceRoot = [IO.Path]::GetFullPath((Join-Path $repo '.agentlang')).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
        $resolvedIsolated = [IO.Path]::GetFullPath($localRoot)
        if (-not $resolvedIsolated.StartsWith($workspaceRoot,[StringComparison]::OrdinalIgnoreCase) -or -not [IO.Path]::GetFileName($resolvedIsolated).StartsWith('business-policy-preflight-',[StringComparison]::Ordinal)) { throw 'Unsafe isolated preflight cleanup path.' }
        Remove-Item -LiteralPath $resolvedIsolated -Recurse -Force
    }
}
if (-not $passed) { throw $failure }
Write-Output "Business policy preflight passes $($checks.Count) checks across $($runs.Count) independent outcomes."
