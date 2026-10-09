#requires -Version 7.5
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('correct','wrong-positive')][string]$Mode,
    [Parameter(Mandatory)][string]$CliDll,
    [Parameter(Mandatory)][string]$AcceptedProject,
    [Parameter(Mandatory)][string]$Destination
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$CliDll = (Resolve-Path -LiteralPath $CliDll).Path
$AcceptedProject = (Resolve-Path -LiteralPath $AcceptedProject).Path
$Destination = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $Destination) { throw "Refusing to overwrite existing control project: $Destination" }
[void][IO.Directory]::CreateDirectory($Destination)
foreach ($item in Get-ChildItem -LiteralPath $AcceptedProject -Recurse -Force) {
    $relative = [IO.Path]::GetRelativePath($AcceptedProject, $item.FullName)
    if ($relative -match '(^|[\\/])(bin|obj|\.git)([\\/]|$)') { continue }
    $target = Join-Path $Destination $relative
    if ($item.PSIsContainer) { [void][IO.Directory]::CreateDirectory($target) }
    else { [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)); [IO.File]::Copy($item.FullName, $target, $false) }
}
$gate = if ($Mode -ceq 'correct') {
    'bool::and(invoice::customer-id(invoice) == state.customer-id(), equals(invoice::status(invoice), InvoiceStatus::new("paid")))'
} else {
    'bool::and(invoice::customer-id(invoice) == state.customer-id(), bool::and(equals(invoice::status(invoice), InvoiceStatus::new("paid")), int::greater-than(Money::value(payment::amount(payment)), 0)))'
}
$source = @"
fn customer.paid-total-step(state: CustomerPaidTotalState, payment: Payment) -> CustomerPaidTotalState {
    effects none
    doc "Control implementation for Maintenance 141."
    match state.outcome() {
        ok current-total => {
            let linked-invoice = store::invoice(id = payment::invoice-id(payment), store = state.store());
            match linked-invoice {
                some invoice => {
                    if $gate {
                        let next-outcome = money::add(current-total, payment::amount(payment));
                        customerPaidTotalState::new(customer-id = state.customer-id(), store = state.store(), outcome = next-outcome)
                    } else {
                        state
                    }
                }
                none => { state }
            }
        }
        error earlier-error => { state }
    }
}
"@
$requests = @(
    [ordered]@{ op='task.begin'; goal="Prepare $Mode paid-invoice total control" },
    [ordered]@{ op='define'; frontend='flow'; syntaxVersion=2; replace=$true; expectedRevision=2; source=$source },
    [ordered]@{ op='replace-word'; word='customer.paid-total-step' },
    [ordered]@{ op='task.commit' }
)
$start = [Diagnostics.ProcessStartInfo]::new(); $start.FileName='dotnet'; $start.WorkingDirectory=$Destination
$start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.RedirectStandardInput=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
foreach ($argument in @($CliDll,'--project',$Destination,'--jsonl')) { [void]$start.ArgumentList.Add([string]$argument) }
$process = [Diagnostics.Process]::new(); $process.StartInfo=$start
if (-not $process.Start()) { throw 'Flow CLI did not start.' }
$stdoutTask=$process.StandardOutput.ReadToEndAsync(); $stderrTask=$process.StandardError.ReadToEndAsync()
foreach ($request in $requests) { $process.StandardInput.WriteLine((ConvertTo-Json -InputObject $request -Depth 30 -Compress)) }
$process.StandardInput.Close(); if (-not $process.WaitForExit(180000)) { try{$process.Kill($true)}catch{}; throw 'Control installation timed out.' }
$stdout=$stdoutTask.GetAwaiter().GetResult(); $stderr=$stderrTask.GetAwaiter().GetResult(); $exit=$process.ExitCode; $process.Dispose()
$responses=@($stdout -split '\r?\n' | Where-Object { $_ } | ForEach-Object { ConvertFrom-Json -InputObject $_ -AsHashtable -Depth 30 -DateKind String })
$evidence=Join-Path $Destination '..' ("control-{0}-install-Maintenance141.jsonl" -f $Mode)
[IO.File]::WriteAllText([IO.Path]::GetFullPath($evidence),$stdout,[Text.UTF8Encoding]::new($false))
if ($exit -ne 0 -or $responses.Count -ne $requests.Count -or @($responses | Where-Object { $_.ok -ne $true }).Count -gt 0) { throw "Control install failed (exit=$exit): $stderr`n$stdout" }
$responses | ForEach-Object { $_.text }
