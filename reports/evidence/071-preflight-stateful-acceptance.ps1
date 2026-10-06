#requires -Version 7.0
[CmdletBinding()]
param([switch]$PrepareOnly)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$run=Join-Path $repo 'experiments/AgentLang.SubagentTrials/stateful-flow-001/runs/stateful-001'
$seed=Join-Path $run 'starting-project'
$copy=Join-Path $repo '.agentlang/stateful-flow-001/acceptance-preflight'
$cli=Join-Path $repo '.agentlang/stateful-flow-001/language-bin/AgentLang.Cli.dll'
if(Test-Path $copy){throw 'Refusing to overwrite preflight project.'}
New-Item -ItemType Directory -Path $copy -Force|Out-Null
Get-ChildItem $seed -Force|Copy-Item -Destination $copy -Recurse -Force
$source=@'
word invoice.queue-reminder-once(invoice: Invoice) -> String {
    effects fs.read fs.write
    doc "Queue a reminder for an open invoice, preserving an existing marker."
    if ::equals(InvoiceStatus::value(invoice::status(invoice)), "open") {
        let path = invoice::reminder-path(invoice::id(invoice));
        if file::exists?(path) {
            file::read(path)
        } else {
            file::write(path, "queued");
            "queued"
        }
    } else {
        "not-open"
    }
}
test invoice.queue-reminder-once/new-open {
    invoice::queue-reminder-once(invoice::new(id = InvoiceId::new("control-new"), status = InvoiceStatus::new("open")))
    => "queued"
}
test invoice.queue-reminder-once/existing-queued {
    file::write("outbox/invoice-reminders/control-existing", "queued");
    invoice::queue-reminder-once(invoice::new(id = InvoiceId::new("control-existing"), status = InvoiceStatus::new("open")))
    => "queued"
}
test invoice.queue-reminder-once/existing-custom {
    file::write("outbox/invoice-reminders/control-custom", "sent");
    invoice::queue-reminder-once(invoice::new(id = InvoiceId::new("control-custom"), status = InvoiceStatus::new("open")))
    => "sent"
}
test invoice.queue-reminder-once/not-open {
    invoice::queue-reminder-once(invoice::new(id = InvoiceId::new("control-paid"), status = InvoiceStatus::new("paid")))
    => "not-open"
}
example invoice.queue-reminder-once/first-reminder {
    invoice::queue-reminder-once(invoice::new(id = InvoiceId::new("control-example"), status = InvoiceStatus::new("open")))
    => "queued"
}
'@
$requests=@(
    @{op='task.begin';goal='Coordinator acceptance preflight; not an agent outcome'},
    @{op='define';frontend='flow';source=$source},
    @{op='test';word='invoice.queue-reminder-once'},
    @{op='example';word='invoice.queue-reminder-once'},
    @{op='commit';word='invoice.queue-reminder-once';library=$true;actor='host'},
    @{op='task.commit';actor='host'}
)
$lines=@($requests|ForEach-Object{$_|ConvertTo-Json -Depth 20 -Compress})
$output=@($lines|& dotnet $cli --project $copy --allow 'fs.read,fs.write' --jsonl)
if($LASTEXITCODE -ne 0 -or $output.Count -ne $requests.Count){throw 'Preflight setup process failed.'}
$responses=@($output|ForEach-Object{$_|ConvertFrom-Json -Depth 100})
[IO.File]::WriteAllText((Join-Path $run 'acceptance-preflight-setup.json'),(@{requests=$requests;responses=$responses;scope='Coordinator-created disposable control only; never supplied to the actor.'}|ConvertTo-Json -Depth 100),[Text.UTF8Encoding]::new($false))
if(@($responses|Where-Object{-not $_.ok}).Count){throw 'Preflight setup diagnostic; inspect archived evidence.'}
if($PrepareOnly){Write-Output 'Correct coordinator control prepared; independent acceptance not yet run.';return}
& (Join-Path $PSScriptRoot '071-verify-stateful-trial.ps1') -ProjectPath $copy -OutputPath (Join-Path $run 'acceptance-preflight.json')
