param([string]$TrialRoot = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
$requests = [Collections.Generic.List[object]]::new()
$checks = [Collections.Generic.List[object]]::new()
function Add-EvalCheck([string]$code, [string]$expected) {
    $requests.Add(@{op='eval';code=$code})
    $checks.Add(@{kind='value';expected=$expected})
}
foreach ($kind in @('premium','standard')) {
    foreach ($term in @('annual','monthly')) {
        foreach ($renewable in @('true','false')) {
            $expected = if ($kind -eq 'premium') { if ($term -eq 'annual' -and $renewable -eq 'true') { '85.5' } else { '90' } } else { '100' }
            Add-EvalCheck ('"{0}" 100.0 customer.new "{1}" {2} subscription.new customer.renewal-balance' -f $kind,$term,$renewable) $expected
        }
    }
}
Add-EvalCheck '"Premium" 100.0 customer.new "annual" true subscription.new customer.renewal-balance' '100'
Add-EvalCheck '"premium" 100.0 customer.new "Annual" true subscription.new customer.renewal-balance' '90'
Add-EvalCheck '"premium" 0.0 customer.new "annual" true subscription.new customer.renewal-balance' '0'
Add-EvalCheck '"premium" -100.0 customer.new "annual" true subscription.new customer.renewal-balance' '-85.5'
$requests.Add(@{op='eval';code='"premium" 100.0 customer.new "premium" 100.0 customer.new customer.renewal-balance'})
$checks.Add(@{kind='error';code='TYPE_STACK_MISMATCH'})
$requests.Add(@{op='test-all'})
$checks.Add(@{kind='tests';minimum=8})
$requests.Add(@{op='describe';word='customer.renewal-balance'})
$checks.Add(@{kind='metadata'})
$requests.Add(@{op='describe';word='customer.premium?'})
$checks.Add(@{kind='retained';output='Bool'})
$requests.Add(@{op='describe';word='customer.discounted-balance'})
$checks.Add(@{kind='retained';output='Float'})
foreach ($request in $requests) {
    if ($request.op -in @('define', 'eval') -and -not $request.Contains('frontend')) { $request.frontend = 'stack' }
}
$lines = @($requests | ForEach-Object { $_ | ConvertTo-Json -Compress -Depth 12 })
$started = [DateTimeOffset]::UtcNow
$raw = @($lines | & dotnet (Join-Path $trialRoot 'host/AgentLang.Cli.dll') --project (Join-Path $trialRoot 'growing-project') --jsonl)
if ($LASTEXITCODE -ne 0) { throw "Oracle CLI failed with exit code $LASTEXITCODE" }
if ($raw.Count -ne $checks.Count) { throw "Expected $($checks.Count) responses, received $($raw.Count)" }
$results = for ($index = 0; $index -lt $checks.Count; $index++) {
    $response = $raw[$index] | ConvertFrom-Json
    $check = $checks[$index]
    $passed = switch ($check.kind) {
        'value' { $response.ok -and @($response.data.stack).Count -eq 1 -and $response.data.stack[0] -ceq $check.expected -and $response.data.stackTypes[0] -ceq 'Float' }
        'error' { -not $response.ok -and $response.error.code -ceq $check.code }
        'tests' { $response.ok -and @($response.data.results).Count -ge $check.minimum -and @($response.data.results | Where-Object { -not $_.passed }).Count -eq 0 }
        'metadata' { $response.ok -and $response.data.status -eq 'persistent' -and $response.data.maturity -eq 'library' -and ($response.data.inputs -join ' ') -ceq 'Customer Subscription' -and ($response.data.outputs -join ' ') -ceq 'Float' -and $response.data.coverage.instructionsCovered -eq $response.data.coverage.instructionsTotal -and $response.data.coverage.branchesCovered -eq $response.data.coverage.branchesTotal -and $response.data.dependencies -contains 'customer.premium?' -and $response.data.dependencies -contains 'customer.discounted-balance' }
        'retained' { $response.ok -and $response.data.status -eq 'persistent' -and $response.data.maturity -eq 'library' -and $response.data.revision -eq 1 -and $response.data.inputs[0] -ceq 'Customer' -and $response.data.outputs[0] -ceq $check.output }
    }
    [ordered]@{request=$requests[$index];check=$check;passed=[bool]$passed;response=$response}
}
$failed = @($results | Where-Object { -not $_.passed })
[ordered]@{runtimeRevision='6d9e341c402144f5d168566e119906e5495e3cb4';startedUtc=$started;finishedUtc=[DateTimeOffset]::UtcNow;passed=$failed.Count -eq 0;checks=@($results)} | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $trialRoot 'task-02-independent-acceptance.json') -Encoding utf8
if ($failed.Count -gt 0) { throw "$($failed.Count) independent checks failed" }
Write-Output "PASS: $($results.Count) independent retained-vocabulary checks"
