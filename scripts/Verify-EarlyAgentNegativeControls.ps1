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
function Assert-ControlCheck($Record,[string]$Name,[bool]$Condition,$Details=$null) {
    $Record.checks.Add([ordered]@{name=$Name;passed=$Condition;details=$Details})
    if (-not $Condition) {
        throw "Task $($Record.task) failed '$Name': $($Details | ConvertTo-Json -Depth 40 -Compress)"
    }
}
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
        $requests=@(
            @{op='define';frontend='flow';source=$source},
            @{op='commit';word='Customer'},
            @{op='commit';word='Subscription'},
            @{op='test-all'},
            @{op='describe';word=$target.word},
            @{op='commit';word=$target.word;library=$true},
            @{op='describe';word=$target.word},
            @{op='history';word=$target.word}
        )
        $taskResult=[ordered]@{
            task=$target.number
            word=$target.word
            expectedBoundary=if ([int]$target.number -in @(1,3)) {'finite bool-return coverage at library commit'} else {'independent behavioral acceptance after library commit'}
            passed=$false
            checks=[Collections.Generic.List[object]]::new()
            setupRequests=$requests
            setupRawOutput=@()
            setupResponses=@()
            acceptance=$null
        }
        $results.Add($taskResult)
        $lines=@($requests | ForEach-Object { ConvertTo-Json $_ -Depth 12 -Compress })
        $output=@($lines | & dotnet $cli --project $project --filesystem virtual --jsonl)
        $taskResult.setupRawOutput=@($output)
        if ($LASTEXITCODE -ne 0 -or $output.Count -ne $requests.Count) {
            throw "Wrong-solution setup process failed for task $($target.number); exit=$LASTEXITCODE, responses=$($output.Count)/$($requests.Count)."
        }
        try {
            $responses=@($output | ForEach-Object { ConvertFrom-Json $_ -Depth 64 })
        } catch {
            throw "Wrong-solution setup returned invalid JSON for task $($target.number): $($_.Exception.Message)"
        }
        $taskResult.setupResponses=@($responses)
        if ($responses.Count -ne $requests.Count) {
            throw "Wrong-solution setup response count mismatch for task $($target.number)."
        }
        for ($index=0; $index -lt 5; $index++) {
            Assert-ControlCheck $taskResult "setup $($requests[$index].op) succeeds" ([bool]$responses[$index].ok) $responses[$index]
        }

        $selfTests=@($responses[3].data.results)
        $ownTests=@($selfTests | Where-Object { $_.word -ceq $target.word })
        Assert-ControlCheck $taskResult 'target self-test passes before library commit' ($ownTests.Count -eq 1 -and $ownTests[0].passed -eq $true) $ownTests

        $before=$responses[4].data
        $coverage=$before.coverage
        $structuralCoverageComplete=$coverage.status -eq 'current' -and
            $coverage.instructionsTotal -gt 0 -and
            $coverage.instructionsCovered -eq $coverage.instructionsTotal -and
            $coverage.branchesCovered -eq $coverage.branchesTotal
        Assert-ControlCheck $taskResult 'candidate has complete structural instruction and branch coverage' $structuralCoverageComplete $coverage
        Assert-ControlCheck $taskResult 'target is still an unpublished project candidate before commit' ($before.status -eq 'candidate' -and $before.maturity -eq 'project') @{status=$before.status;maturity=$before.maturity}

        $commit=$responses[5]
        if ([int]$target.number -in @(1,3)) {
            Assert-ControlCheck $taskResult 'library commit is rejected by finite coverage' ($commit.ok -eq $false -and $commit.error.code -ceq 'LIBRARY_FINITE_COVERAGE_INCOMPLETE' -and $commit.error.word -ceq $target.word) $commit
            $missing=@($commit.error.expected)
            $observed=@($commit.error.actual)
            Assert-ControlCheck $taskResult 'finite rejection identifies the uncovered false return' ($missing -contains 'return[0] Bool: false') @{expected=$missing;actual=$observed}
            Assert-ControlCheck $taskResult 'finite rejection shows the constant true return was observed' ($observed -contains 'return[0] Bool: true') @{expected=$missing;actual=$observed}

            $after=$responses[6]
            Assert-ControlCheck $taskResult 'rejected target remains only a project candidate' ($after.ok -eq $true -and $after.data.status -eq 'candidate' -and $after.data.maturity -eq 'project') $after
            $history=$responses[7]
            Assert-ControlCheck $taskResult 'rejected target has no durable history entry' ($history.ok -eq $false -and $history.error.code -ceq 'HISTORY_WORD_UNKNOWN') $history
            $taskResult.rejectedAt='LIBRARY_FINITE_COVERAGE_INCOMPLETE'
            $taskResult.passed=$true
            continue
        }

        Assert-ControlCheck $taskResult 'Float-result library commit succeeds' ($commit.ok -eq $true) $commit
        $after=$responses[6]
        Assert-ControlCheck $taskResult 'Float-result target is durably published as a library' ($after.ok -eq $true -and $after.data.status -eq 'persistent' -and $after.data.maturity -eq 'library') $after
        $history=$responses[7]
        $historyEntries=@($history.data)
        Assert-ControlCheck $taskResult 'published target has durable library history' ($history.ok -eq $true -and $historyEntries.Count -eq 1 -and $historyEntries[0].name -ceq $target.word -and $historyEntries[0].maturity -ceq 'library') $history

        $sidecar=Join-Path $root "task-$($target.number)-acceptance.json"
        $rejection=$null
        try { & (Join-Path $PSScriptRoot 'Verify-EarlyFlowTask.ps1') -CliDll $cli -ProjectPath $project -Task $target.number -EvidencePath $sidecar }
        catch { $rejection=$_.Exception.Message }
        if ($null -eq $rejection) { throw "Incorrect Float task $($target.number) was accepted by the independent oracle." }
        if (-not (Test-Path -LiteralPath $sidecar)) { throw "Independent acceptance verifier produced no evidence for task $($target.number): $rejection" }
        $observedAcceptance=Get-Content $sidecar -Raw | ConvertFrom-Json -Depth 100
        $last=$observedAcceptance.checks[-1]
        $failedResults=@($observedAcceptance.checks | Where-Object { $_.name -like '* independent * result' -and $_.passed -eq $false })
        if ($observedAcceptance.passed -or $last.passed -or $last.name -notlike '* independent * result' -or $failedResults.Count -eq 0) {
            throw "Float task $($target.number) was not rejected at independent result acceptance: $rejection"
        }
        Assert-ControlCheck $taskResult 'published wrong Float solution reaches independent result rejection' $true @{failedIndependentResults=$failedResults.Count;lastCheck=$last}
        if (@($observedAcceptance.checks | Where-Object name -eq 'complete current own coverage' | Where-Object passed).Count -ne 1) {
            throw "Float task $($target.number) did not pass its independent own-coverage check."
        }
        $taskResult.rejectedAt=$last.name
        $taskResult.acceptance=$observedAcceptance
        $taskResult.passed=$true
    }
    if ($results.Count -ne 5 -or @($results | Where-Object { -not $_.passed }).Count -ne 0) { throw 'Missing or failed negative control.' }
    $passed=$true
} catch {
    if ($results.Count -gt 0) {
        $latest=$results[$results.Count-1]
        if (-not $latest.passed) { $latest.failure=$_.Exception.Message }
    }
    $failure=$_.Exception.Message
} finally {
    $evidence=[ordered]@{
        schemaVersion=2
        passed=$passed
        failure=$failure
        results=$results.ToArray()
        limits=@('Root-authored adversarial fixtures, not external-agent trials.','Boolean-output controls are stopped at finite library qualification; Float controls proceed to the independent behavioral oracle.','Positive solutions require separate acceptance verification.','Full coverage alone is not business correctness.')
    }
    $evidenceTarget=[IO.Path]::GetFullPath($EvidencePath,$repo)
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($evidenceTarget))
    [IO.File]::WriteAllText($evidenceTarget,(ConvertTo-Json $evidence -Depth 100),[Text.UTF8Encoding]::new($false))
}
if (-not $passed) { throw $failure }
Write-Output "5 wrong library solutions rejected: 2 at finite-value library qualification and 3 by independent result acceptance. Evidence: $evidenceTarget"
