#requires -Version 7.0
[CmdletBinding()]
param([switch]$PrepareOnly)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$seed=Join-Path $repo 'experiments/AgentLang.SubagentTrials/stateful-conventional-001/runs/conventional-001/starting-project'
$control=Join-Path $repo '.agentlang/stateful-conventional-001/acceptance-control'
$run=Split-Path $seed -Parent
if(Test-Path $control){throw 'Refusing to overwrite control project.'}
[IO.Directory]::CreateDirectory($control)|Out-Null
Get-ChildItem -LiteralPath $seed -File|ForEach-Object{Copy-Item -LiteralPath $_.FullName -Destination $control}
$utf8=[Text.UTF8Encoding]::new($false)
$operations=@'
namespace AgentLang.StatefulPilot

module ReminderOperations =
    /// Queue an open invoice once, preserving an existing reminder marker.
    let queueOnce (files: IReminderFiles) (invoice: Invoice) : string =
        let (InvoiceStatus status) = invoice.Status
        if status = "open" then
            let path = Invoice.reminderPath invoice.Id
            if files.Exists path then files.Read path
            else
                files.Write(path, "queued")
                "queued"
        else "not-open"
'@
[IO.File]::WriteAllText((Join-Path $control 'Operations.fs'),$operations,$utf8)
$ownTests=@'
    let runOwnTests () =
        let invoice status = { Id = InvoiceId "control-1"; Status = InvoiceStatus status }
        let path = Invoice.reminderPath (InvoiceId "control-1")
        let invoke (files: VirtualFiles) status =
            Execution.run true true (files :> IReminderFiles) (invoice status) ReminderOperations.queueOnce
        let missing = VirtualFiles(Map.empty)
        assertEqual "queued" (invoke missing "open")
        assertEqual (Some "queued") (Map.tryFind path missing.State)
        assertEqual (1,1) (missing.ReadCount, missing.WriteCount)
        let queued = VirtualFiles(Map.ofList [path,"queued"])
        assertEqual "queued" (invoke queued "open")
        assertEqual (Map.ofList [path,"queued"]) queued.State
        assertEqual (2,0) (queued.ReadCount,queued.WriteCount)
        let other = VirtualFiles(Map.ofList [path,"sent-earlier"])
        assertEqual "sent-earlier" (invoke other "open")
        assertEqual (Map.ofList [path,"sent-earlier"]) other.State
        assertEqual (2,0) (other.ReadCount,other.WriteCount)
        let closed = VirtualFiles(Map.empty)
        assertEqual "not-open" (invoke closed "Open")
        assertEqual Map.empty closed.State
        assertEqual (0,0) (closed.ReadCount,closed.WriteCount)
        printfn "OWN_TESTS_PASSED=4"
        let example = VirtualFiles(Map.empty)
        assertEqual "queued" (invoke example "open")
        printfn "EXAMPLE_QUEUE_REMINDER=queued"
'@
$testsPath=Join-Path $control 'SelfTests.fs'
$tests=[IO.File]::ReadAllText($testsPath,$utf8)
$pattern='(?m)^    let runOwnTests \(\) =\r?\n        \(\)'
if([regex]::Matches($tests,$pattern).Count -ne 1){throw 'Expected one own-test stub.'}
$tests=[regex]::Replace($tests,$pattern,[Text.RegularExpressions.MatchEvaluator]{param($m) $ownTests})
[IO.File]::WriteAllText($testsPath,$tests,$utf8)
$pin=@{sourceRevision=(& git -C $repo rev-parse HEAD).Trim();controlPath=$control;createdAtUtc=[DateTimeOffset]::UtcNow.ToString('o');files=@(Get-ChildItem $control -File|ForEach-Object{@{path=$_.Name;sha256=(Get-FileHash $_.FullName).Hash.ToLowerInvariant()}})}
[IO.File]::WriteAllText((Join-Path $run 'acceptance-preflight-setup.json'),($pin|ConvertTo-Json -Depth 30),$utf8)
if($PrepareOnly){Write-Output 'Prepared independent accepted control without launching an actor.';exit 0}
& (Join-Path $PSScriptRoot '072-verify-conventional-stateful.ps1') -ProjectPath $control -OutputPath (Join-Path $run 'acceptance-preflight.json')
exit $LASTEXITCODE
