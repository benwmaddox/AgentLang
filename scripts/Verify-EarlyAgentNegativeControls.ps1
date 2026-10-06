#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CliDll,
    [Parameter(Mandatory)][string]$EvidencePath
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$cli=(Resolve-Path $CliDll).Path
$oracle=Get-Content (Join-Path $repo 'experiments/AgentLang.SubagentTrials/early-flow-001/acceptance.json') -Raw | ConvertFrom-Json
$root=Join-Path $repo ('.agentlang/early-negative-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
$results=[Collections.Generic.List[object]]::new()
$failure=$null
$passed=$false
try {
    foreach ($target in $oracle.tasks) {
        $project=Join-Path $root "task-$($target.number)"
        [void][IO.Directory]::CreateDirectory($project)
        $constant=switch ([int]$target.number) { 1 {'true'}; 2 {'90.0'}; 3 {'true'}; 4 {'85.5'}; 5 {'14.5'} }
        $parameters=switch ([int]$target.number) { 3 {'subscription: Subscription'}; {$_ -in 1,2} {'customer: Customer'}; default {'customer: Customer, subscription: Subscription'} }
        $callArguments=switch ([int]$target.number) { 3 {'subscription::new("annual", true)'}; {$_ -in 1,2} {'customer::new("premium", 100.0)'}; default {'customer::new("premium", 100.0), subscription::new("annual", true)'} }
        $reference=$target.word -replace '^([^.]+)\.', '$1::'
        $source=(Get-Content (Join-Path $repo 'experiments/AgentLang.SubagentTrials/matched-renewal-001/fixtures/flow/flat.agent') -Raw) + @"

word $($target.word)($parameters) -> $($target.output) {
    effects none
    $constant
}
test $($target.word)/constant {
    $reference($callArguments) => $constant
}
"@
        $requests=@(@{op='define';frontend='flow';source=$source},@{op='commit';word='Customer'},@{op='commit';word='Subscription'},@{op='test-all'},@{op='commit';word=$target.word;library=$true})
        $lines=@($requests | ForEach-Object { ConvertTo-Json $_ -Depth 12 -Compress })
        $output=@($lines | & dotnet $cli --project $project --jsonl)
        if ($LASTEXITCODE -ne 0 -or $output.Count -ne $requests.Count) { throw 'Wrong-solution setup process failed.' }
        $responses=@($output | ForEach-Object { ConvertFrom-Json $_ -Depth 64 })
        if (@($responses | Where-Object {-not $_.ok}).Count) { throw "Wrong-solution setup rejected for task $($target.number)." }
        $sidecar=Join-Path $root "task-$($target.number)-acceptance.json"
        $rejection=$null
        try { & (Join-Path $PSScriptRoot 'Verify-EarlyFlowTask.ps1') -CliDll $cli -ProjectPath $project -Task $target.number -EvidencePath $sidecar }
        catch { $rejection=$_.Exception.Message }
        if ($null -eq $rejection) { throw "Incorrect task $($target.number) was accepted." }
        $observed=Get-Content $sidecar -Raw | ConvertFrom-Json -Depth 100
        $last=$observed.checks[-1]
        if ($observed.passed -or $last.passed -or $last.name -notlike '* independent * result') { throw "Wrong task $($target.number) rejected at an unexpected boundary: $rejection" }
        if (@($observed.checks | Where-Object name -eq 'complete current own coverage' | Where-Object passed).Count -ne 1) { throw 'Rejection did not follow passing library coverage.' }
        $results.Add([ordered]@{task=$target.number;passed=$true;rejectedAt=$last.name;passingPriorChecks=@($observed.checks | Where-Object passed).Count;setupRequests=$requests;setupResponses=$responses;acceptance=$observed})
    }
    if ($results.Count -ne 5) { throw 'Missing negative control.' }
    $passed=$true
} catch { $failure=$_.Exception.Message }
finally {
    $evidence=[ordered]@{schemaVersion=1;passed=$passed;failure=$failure;results=$results.ToArray();limits=@('Root-authored adversarial fixtures, not external-agent trials.','Positive solutions require separate acceptance verification.','Full coverage alone is not business correctness.')}
    $evidenceTarget=[IO.Path]::GetFullPath($EvidencePath,$repo)
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($evidenceTarget))
    [IO.File]::WriteAllText($evidenceTarget,(ConvertTo-Json $evidence -Depth 100),[Text.UTF8Encoding]::new($false))
}
if (-not $passed) { throw $failure }
Write-Output "5 wrong library solutions rejected after passing self-tests and own coverage. Evidence: $evidenceTarget"
