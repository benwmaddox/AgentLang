#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CliDll,
    [Parameter(Mandatory)][string]$ProjectPath,
    [string]$EvidencePath = '.agentlang/reports/flow-renewal-solution.json'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$cli = (Resolve-Path -LiteralPath $CliDll).Path
$project = (Resolve-Path -LiteralPath $ProjectPath).Path
$oraclePath = Join-Path $repo 'experiments/AgentLang.SubagentTrials/matched-renewal-001/flow-renewal-acceptance.json'
$oracle = Get-Content -LiteralPath $oraclePath -Raw | ConvertFrom-Json
if ($oracle.schemaVersion -ne 1 -or $oracle.cases.Count -ne 20) { throw 'Unexpected acceptance oracle version/count.' }
$checks = [Collections.Generic.List[object]]::new()
$responses = @()
$passed = $false
$failure = $null
function Assert-Check([string]$Name, [bool]$Passed) {
    $checks.Add([ordered]@{name=$Name;passed=$Passed})
    if (-not $Passed) { throw "Renewal acceptance failed: $Name" }
}
function Float-Literal([double]$Value) {
    $text = $Value.ToString('R', [Globalization.CultureInfo]::InvariantCulture)
    if ($text -notmatch '[.eE]') { $text += '.0' }
    return $text
}
$requests = @(
    [ordered]@{op='test-all'},
    [ordered]@{op='describe';word=$oracle.word},
    [ordered]@{op='source';word=$oracle.word},
    [ordered]@{op='dependencies';word=$oracle.word},
    [ordered]@{op='words'}
)
foreach ($case in $oracle.cases) {
    $kind = ConvertTo-Json -InputObject ([string]$case.kind) -Compress
    $term = ConvertTo-Json -InputObject ([string]$case.term) -Compress
    $balance = Float-Literal ([double]$case.balance)
    $renewable = ([bool]$case.renewable).ToString().ToLowerInvariant()
    $code = "customer::renewal-balance(customer::new(kind = $kind, balance = $balance), subscription::new(term = $term, renewable = $renewable))"
    $requests += [ordered]@{op='eval';frontend='flow';code=$code}
}
try {
    $lines = @($requests | ForEach-Object { ConvertTo-Json -InputObject $_ -Depth 12 -Compress })
    $output = @($lines | & dotnet $cli --project $project --filesystem virtual --jsonl)
    $exitCode = $LASTEXITCODE
    Assert-Check 'fresh process exit' ($exitCode -eq 0)
    Assert-Check 'one response per request' ($output.Count -eq $requests.Count)
    $responses = @($output | ForEach-Object { ConvertFrom-Json -InputObject $_ -Depth 64 })
    Assert-Check 'all requests succeed' (@($responses | Where-Object { -not $_.ok }).Count -eq 0)
    $tests = @($responses[0].data.results)
    Assert-Check 'all attached tests pass' ($tests.Count -gt 0 -and @($tests | Where-Object { -not $_.passed }).Count -eq 0)
    $word = $responses[1].data
    Assert-Check 'exact target signature' ((@($word.inputs) -join ',') -ceq 'Customer,Subscription' -and (@($word.outputs) -join ',') -ceq 'Float')
    Assert-Check 'pure persistent library target' ($word.status -eq 'persistent' -and $word.maturity -eq 'library' -and @($word.effects).Count -eq 0)
    Assert-Check 'target has passing attached tests' (@($tests | Where-Object word -eq $oracle.word).Count -gt 0)
    $coverage = $word.coverage
    Assert-Check 'target own instruction and branch coverage complete' ($coverage.status -eq 'current' -and $coverage.instructionsTotal -gt 0 -and $coverage.instructionsCovered -eq $coverage.instructionsTotal -and $coverage.branchesCovered -eq $coverage.branchesTotal)
    foreach ($index in 0..($oracle.cases.Count - 1)) {
        $case = $oracle.cases[$index]
        $data = $responses[$index + 5].data
        Assert-Check "$($case.name) exact Float output shape" (@($data.stack).Count -eq 1 -and @($data.stackTypes).Count -eq 1 -and $data.stackTypes[0] -ceq 'Float')
        $rawActual = $data.stack[0]
        if ($rawActual -is [string]) {
            $actual = [double]::Parse($rawActual, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture)
        } elseif ($rawActual -is [byte] -or $rawActual -is [sbyte] -or $rawActual -is [int16] -or $rawActual -is [uint16] -or $rawActual -is [int32] -or $rawActual -is [uint32] -or $rawActual -is [int64] -or $rawActual -is [uint64] -or $rawActual -is [single] -or $rawActual -is [double] -or $rawActual -is [decimal]) {
            $actual = [Convert]::ToDouble($rawActual, [Globalization.CultureInfo]::InvariantCulture)
        } else {
            throw "Expected a numeric Float protocol value for case '$($case.name)'."
        }
        $expected = [double]$case.expected
        $tolerance = [Math]::Max([double]$oracle.tolerance.minimumAbsolute, [Math]::Abs($expected) * [double]$oracle.tolerance.relative)
        Assert-Check "$($case.name) independent numerical result" ([double]::IsFinite($actual) -and [Math]::Abs($actual - $expected) -le $tolerance)
    }
    $passed = $true
} catch { $failure = $_.Exception.Message }
finally {
    $evidence = [ordered]@{
        schemaVersion=1;passed=$passed;failure=$failure;project=$project
        cliSha256=(Get-FileHash -LiteralPath $cli -Algorithm SHA256).Hash.ToLowerInvariant()
        oracleSha256=(Get-FileHash -LiteralPath $oraclePath -Algorithm SHA256).Hash.ToLowerInvariant()
        verifierSha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
        cases=$oracle.cases;checks=$checks.ToArray();requests=$requests;responses=$responses
        limits=@('Selected numerical Float behavior, not bitwise/signed-zero proof.','No token, latency or comparative-efficiency claim.','All-new-word library policy and minimal edits require separate final-state audit.')
    }
    $target = [IO.Path]::GetFullPath($EvidencePath, $repo)
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
    [IO.File]::WriteAllText($target,(ConvertTo-Json -InputObject $evidence -Depth 64),[Text.UTF8Encoding]::new($false))
}
if (-not $passed) { throw $failure }
Write-Output "$($checks.Count) independent renewal checks passed. Evidence: $target"
