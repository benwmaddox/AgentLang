param([string]$TrialRoot = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
$requests = [Collections.Generic.List[object]]::new()
$checks = [Collections.Generic.List[object]]::new()
function Add-EvalCheck([string]$code, [string]$expected, [string]$type) {
    $requests.Add(@{op='eval';code=$code})
    $checks.Add(@{kind='value';expected=$expected;type=$type;code=$code})
}
Add-EvalCheck '"premium" 100.0 customer.new customer.premium?' 'true' 'Bool'
Add-EvalCheck '"standard" 100.0 customer.new customer.premium?' 'false' 'Bool'
Add-EvalCheck '"Premium" 100.0 customer.new customer.premium?' 'false' 'Bool'
Add-EvalCheck '"" 100.0 customer.new customer.premium?' 'false' 'Bool'
Add-EvalCheck '"premium" 100.0 customer.new customer.discounted-balance' '90' 'Float'
Add-EvalCheck '"standard" 100.0 customer.new customer.discounted-balance' '100' 'Float'
Add-EvalCheck '"Premium" 100.0 customer.new customer.discounted-balance' '100' 'Float'
Add-EvalCheck '"" 100.0 customer.new customer.discounted-balance' '100' 'Float'
Add-EvalCheck '"premium" 0.0 customer.new customer.discounted-balance' '0' 'Float'
Add-EvalCheck '"premium" -100.0 customer.new customer.discounted-balance' '-90' 'Float'
Add-EvalCheck '"premium" 250.0 customer.new customer.discounted-balance' '225' 'Float'
$requests.Add(@{op='eval';code='"premium" 100 customer.new customer.discounted-balance'})
$checks.Add(@{kind='error';code='TYPE_STACK_MISMATCH'})
$requests.Add(@{op='test-all'})
$checks.Add(@{kind='tests';minimum=7})
$requests.Add(@{op='describe';word='customer.premium?'})
$checks.Add(@{kind='metadata';input='Customer';output='Bool'})
$requests.Add(@{op='describe';word='customer.discounted-balance'})
$checks.Add(@{kind='metadata';input='Customer';output='Float'})
$requests.Add(@{op='describe';word='customer.new'})
$checks.Add(@{kind='constructor'})
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
        'value' { $response.ok -and @($response.data.stack).Count -eq 1 -and $response.data.stack[0] -ceq $check.expected -and $response.data.stackTypes[0] -ceq $check.type }
        'error' { -not $response.ok -and $response.error.code -ceq $check.code }
        'tests' { $response.ok -and @($response.data.results).Count -ge $check.minimum -and @($response.data.results | Where-Object { -not $_.passed }).Count -eq 0 }
        'metadata' { $response.ok -and $response.data.status -eq 'persistent' -and $response.data.maturity -eq 'library' -and $response.data.inputs[0] -ceq $check.input -and $response.data.outputs[0] -ceq $check.output -and $response.data.coverage.instructionsCovered -eq $response.data.coverage.instructionsTotal -and $response.data.coverage.branchesCovered -eq $response.data.coverage.branchesTotal }
        'constructor' { $response.ok -and ($response.data.inputs -join ' ') -ceq 'String Float' -and ($response.data.outputs -join ' ') -ceq 'Customer' }
    }
    [ordered]@{request=$requests[$index];check=$check;passed=[bool]$passed;response=$response}
}
$failed = @($results | Where-Object { -not $_.passed })
[ordered]@{runtimeRevision='6d9e341c402144f5d168566e119906e5495e3cb4';startedUtc=$started;finishedUtc=[DateTimeOffset]::UtcNow;passed=$failed.Count -eq 0;checks=@($results)} | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $trialRoot 'task-01-independent-acceptance.json') -Encoding utf8
if ($failed.Count -gt 0) { throw "$($failed.Count) independent checks failed" }
Write-Output "PASS: $($results.Count) independent subagent-task checks"
