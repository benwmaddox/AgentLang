#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CliDll,
    [Parameter(Mandatory)][string]$ProjectPath,
    [Parameter(Mandatory)][ValidateRange(1,5)][int]$Task,
    [Parameter(Mandatory)][string]$EvidencePath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$cli = (Resolve-Path -LiteralPath $CliDll).Path
$project = (Resolve-Path -LiteralPath $ProjectPath).Path
$oraclePath = Join-Path $repo 'experiments/AgentLang.SubagentTrials/early-flow-001/acceptance.json'
$oracle = Get-Content -LiteralPath $oraclePath -Raw | ConvertFrom-Json
if ($oracle.schemaVersion -ne 1 -or $oracle.tasks.Count -ne 5) { throw 'Unexpected pilot oracle shape.' }
$taskOracle = @($oracle.tasks | Where-Object number -eq $Task)
if ($taskOracle.Count -ne 1 -or $taskOracle[0].cases.Count -ne 20) { throw 'Unexpected task oracle shape.' }
$target = $taskOracle[0]
$checks = [Collections.Generic.List[object]]::new()
$responses = @()
$failure = $null
$passed = $false
function Check([string]$Name, [bool]$Condition) {
    $checks.Add([ordered]@{name=$Name;passed=$Condition})
    if (-not $Condition) { throw "Pilot acceptance failed: $Name" }
}
function FloatLiteral([double]$Value) {
    $literal = $Value.ToString('R', [Globalization.CultureInfo]::InvariantCulture)
    if ($literal -notmatch '[.eE]') { $literal += '.0' }
    return $literal
}
$requests = @(
    [ordered]@{op='test-all'},
    [ordered]@{op='describe';word=$target.word},
    [ordered]@{op='dependencies';word=$target.word},
    [ordered]@{op='source';word=$target.word}
)
foreach ($case in $target.cases) {
    $kind = ConvertTo-Json -InputObject ([string]$case.kind) -Compress
    $term = ConvertTo-Json -InputObject ([string]$case.term) -Compress
    $balance = FloatLiteral ([double]$case.balance)
    $renewable = ([bool]$case.renewable).ToString().ToLowerInvariant()
    $customer = "customer::new(kind = $kind, balance = $balance)"
    $subscription = "subscription::new(term = $term, renewable = $renewable)"
    $callArguments = switch ($Task) { 3 { $subscription }; { $_ -in 1,2 } { $customer }; default { "$customer, $subscription" } }
    $wordReference = ([string]$target.word) -replace '^([^.]+)\.', '$1::'
    $requests += [ordered]@{op='eval';frontend='flow';code="$wordReference($callArguments)"}
}
try {
    $lines = @($requests | ForEach-Object { ConvertTo-Json -InputObject $_ -Depth 12 -Compress })
    $output = @($lines | & dotnet $cli --project $project --filesystem virtual --jsonl)
    Check 'fresh process exit' ($LASTEXITCODE -eq 0)
    Check 'one response per request' ($output.Count -eq $requests.Count)
    $responses = @($output | ForEach-Object { ConvertFrom-Json -InputObject $_ -Depth 64 })
    Check 'all requests succeed' (@($responses | Where-Object { -not $_.ok }).Count -eq 0)
    $tests = @($responses[0].data.results)
    Check 'all attached tests pass' ($tests.Count -gt 0 -and @($tests | Where-Object { -not $_.passed }).Count -eq 0)
    $word = $responses[1].data
    Check 'exact target signature' ((@($word.inputs) -join ',') -ceq (@($target.inputs) -join ',') -and (@($word.outputs) -join ',') -ceq $target.output)
    Check 'pure persistent library target' ($word.status -eq 'persistent' -and $word.maturity -eq 'library' -and @($word.effects).Count -eq 0)
    Check 'target has attached tests' (@($tests | Where-Object word -eq $target.word).Count -gt 0)
    $coverage = $word.coverage
    Check 'complete current own coverage' ($coverage.status -eq 'current' -and $coverage.instructionsTotal -gt 0 -and $coverage.instructionsCovered -eq $coverage.instructionsTotal -and $coverage.branchesCovered -eq $coverage.branchesTotal)
    for ($index = 0; $index -lt $target.cases.Count; $index++) {
        $case = $target.cases[$index]
        $data = $responses[$index + 4].data
        Check "$($case.name) exact output shape" (@($data.stack).Count -eq 1 -and @($data.stackTypes).Count -eq 1 -and $data.stackTypes[0] -ceq $target.output)
        $raw = $data.stack[0]
        if ($target.output -ceq 'Bool') {
            # Eval currently transports rendered values beside exact stackTypes.
            # Accept only canonical Bool text (or a native JSON Bool), never
            # PowerShell's truthiness conversion of a nonempty "false" string.
            $expectedBoolText = ([bool]$case.expected).ToString().ToLowerInvariant()
            Check "$($case.name) independent Bool result" (($raw -is [bool] -and $raw -eq [bool]$case.expected) -or ($raw -is [string] -and $raw -ceq $expectedBoolText))
        } else {
            if ($raw -is [string]) {
                $actual = [double]::Parse($raw,[Globalization.NumberStyles]::Float,[Globalization.CultureInfo]::InvariantCulture)
            } elseif ($raw -is [ValueType] -and $raw -isnot [bool]) {
                $actual = [Convert]::ToDouble($raw,[Globalization.CultureInfo]::InvariantCulture)
            } else { throw 'Expected a numeric Float protocol value.' }
            $expected = [double]$case.expected
            $tolerance = [Math]::Max([double]$oracle.tolerance.minimumAbsolute,[Math]::Abs($expected) * [double]$oracle.tolerance.relative)
            Check "$($case.name) independent Float result" ([double]::IsFinite($actual) -and [Math]::Abs($actual - $expected) -le $tolerance)
        }
    }
    $passed = $true
} catch { $failure = $_.Exception.Message }
finally {
    $evidence = [ordered]@{
        schemaVersion=1;passed=$passed;failure=$failure;task=$Task;word=$target.word
        cliSha256=(Get-FileHash $cli -Algorithm SHA256).Hash.ToLowerInvariant()
        oracleSha256=(Get-FileHash $oraclePath -Algorithm SHA256).Hash.ToLowerInvariant()
        verifierSha256=(Get-FileHash $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
        checks=$checks.ToArray();requests=$requests;responses=$responses
        limits=@('Behavioral acceptance only; no model-efficiency claim.','Float tolerance is not bitwise or signed-zero equivalence.','Whole-state preservation and retained-helper quality require a separate audit.')
    }
    $evidenceTarget = [IO.Path]::GetFullPath($EvidencePath,$repo)
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($evidenceTarget))
    [IO.File]::WriteAllText($evidenceTarget,(ConvertTo-Json $evidence -Depth 64),[Text.UTF8Encoding]::new($false))
}
if (-not $passed) { throw $failure }
Write-Output "$($checks.Count) pilot task-$Task acceptance checks passed. Evidence: $evidenceTarget"
