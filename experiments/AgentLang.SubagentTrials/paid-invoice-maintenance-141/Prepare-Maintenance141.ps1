#requires -Version 7.5
[CmdletBinding()]
param(
    [string]$StudyRoot = $PSScriptRoot,
    [string]$RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..')),
    [string]$FlowCliDll
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Copy-StudyProject([string]$Source, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) { throw "Missing accepted source project: $Source" }
    if (Test-Path -LiteralPath $Destination) { throw "Study start already exists; refusing to replace it: $Destination" }
    [void][IO.Directory]::CreateDirectory($Destination)
    foreach ($item in Get-ChildItem -LiteralPath $Source -Recurse -Force) {
        $relative = [IO.Path]::GetRelativePath($Source, $item.FullName)
        if ($relative -match '(^|[\\/])(bin|obj)([\\/]|$)' -or $relative -match '(^|[\\/])\.git([\\/]|$)') { continue }
        $target = Join-Path $Destination $relative
        if ($item.PSIsContainer) {
            [void][IO.Directory]::CreateDirectory($target)
        } else {
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
            [IO.File]::Copy($item.FullName, $target, $false)
        }
    }
}

function Invoke-FlowRequests([string]$Project, [string]$Cli, [object[]]$Requests) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.WorkingDirectory = $Project
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @($Cli, '--project', $Project, '--jsonl')) { [void]$start.ArgumentList.Add([string]$argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (-not $process.Start()) { throw 'Flow CLI did not start.' }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    foreach ($request in $Requests) { $process.StandardInput.WriteLine((ConvertTo-Json -InputObject $request -Depth 50 -Compress)) }
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(180000)) {
        try { $process.Kill($true) } catch { }
        throw 'Flow fixture installation timed out.'
    }
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    $exitCode = $process.ExitCode
    $process.Dispose()
    $responses = @($stdout -split '\r?\n' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { ConvertFrom-Json -InputObject $_ -AsHashtable -Depth 50 })
    if ($exitCode -ne 0 -or $responses.Count -ne $Requests.Count -or @($responses | Where-Object { $_.ok -ne $true }).Count -gt 0) {
        throw "Flow fixture installation failed (exit=$exitCode, responses=$($responses.Count)/$($Requests.Count)): $stderr`n$stdout"
    }
    return ,$responses
}

function Add-FlowFixture([string]$Project, [string]$Cli) {
    $source = @'
fn maintenance141.fixture-import-payment(store: Store, payment-id: PaymentId, invoice-id: InvoiceId, customer-id: CustomerId, invoice-status: Option<InvoiceStatus>, amount: Money) -> Store {
    effects none
    doc "Test-only imported ledger row builder for Maintenance 141; never use in business definitions."

    let payment = payment::new(id = payment-id, invoice-id = invoice-id, amount = amount, provider-reference = "maintenance-141-fixture", paid-at = Instant::new("2001-02-03T04:05:06.0000000+00:00"));
    let invoices = match invoice-status {
        some status => {
            let invoice = invoice::new(id = invoice-id, customer-id = customer-id, lines = list::empty<InvoiceLine>(), total = amount, created-at = Instant::new("2001-02-03T04:05:06.0000000+00:00"), status = status);
            list::append(store.invoices(), invoice)
        }
        none => {
            store.invoices()
        }
    };
    store::new(customers = store.customers(), products = store.products(), subscriptions = store.subscriptions(), invoices = invoices, payments = list::append(store.payments(), payment), email-outbox = store.email-outbox(), sent-emails = store.sent-emails())
}

test maintenance141.fixture-import-payment/inserts-open-invoice-and-payment {
    let customer-id = CustomerId::new("10000000-0000-0000-0000-000000000001");
    let invoice-id = InvoiceId::new("20000000-0000-0000-0000-000000000001");
    let payment-id = PaymentId::new("30000000-0000-0000-0000-000000000001");
    let imported = maintenance141::fixture-import-payment(store::empty(unit), payment-id, invoice-id, customer-id, option::some<InvoiceStatus>(InvoiceStatus::new("open")), Money::new(0));
    bool::and(equals(imported.invoices().count(), 1), equals(imported.payments().count(), 1))
    => 2
}
'@
    $projectText = [IO.File]::ReadAllText((Join-Path $Project 'dictionary.agent'))
    if ($projectText.Contains('maintenance141.fixture-import-payment')) { throw 'Accepted Flow start unexpectedly already contains the maintenance fixture.' }
    $requests = @(
        [ordered]@{op='task.begin';goal='Install immutable imported-payment test fixture for Maintenance 141'},
        [ordered]@{op='define';frontend='flow';syntaxVersion=2;source=$source},
        [ordered]@{op='commit';word='maintenance141.fixture-import-payment';library=$false},
        [ordered]@{op='task.commit'}
    )
    $responses = Invoke-FlowRequests $Project $Cli $requests
    if ([string]$responses[1].data.name -cne 'maintenance141.fixture-import-payment') { throw 'Flow define returned an unexpected fixture name.' }
}

function Add-FsharpFixture([string]$Project) {
    $path = Join-Path $Project 'business/Business.fs'
    $source = [IO.File]::ReadAllText($path)
    $marker = '    module Subscription ='
    if (-not $source.Contains($marker)) { throw "Expected insertion point not found in $path" }
    if ($source.Contains('module Maintenance141Fixture =')) { throw "Fixture helper already exists in $path" }
    $fixture = @'
        /// Test-only imported ledger row builder for Maintenance 141.
        /// Do not use from production business definitions.
        module Maintenance141Fixture =
            let importPayment (store: Store) (invoiceId: InvoiceId) (customerId: CustomerId) (status: InvoiceStatus option) (paymentId: PaymentId) (amount: Money) : Store =
                let withInvoice =
                    match status with
                    | None -> store
                    | Some invoiceStatus ->
                        let importedInvoice = {
                            Id = invoiceId
                            CustomerId = customerId
                            Lines = []
                            Total = amount
                            CreatedAt = DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.Zero)
                            Status = invoiceStatus
                        }
                        { store with InvoicesById = Map.add invoiceId importedInvoice store.InvoicesById }
                let importedPayment = {
                    Id = paymentId
                    InvoiceId = invoiceId
                    Amount = amount
                    ProviderReference = "maintenance-141-fixture"
                    PaidAt = DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.Zero)
                }
                { withInvoice with PaymentsById = Map.add paymentId importedPayment withInvoice.PaymentsById }

'@
    [IO.File]::WriteAllText($path, $source.Replace($marker, $fixture + $marker), [Text.UTF8Encoding]::new($false))
}

$flowAccepted = Join-Path $RepositoryRoot '.agentlang/r09-discovery-001/comparison/participant-work/replica2/flow-retained/project'
$fsharpAccepted = Join-Path $RepositoryRoot '.agentlang/r09-discovery-001/comparison/participant-work/replica2/fsharp/project'
$starts = Join-Path $StudyRoot 'starts'
[void][IO.Directory]::CreateDirectory($starts)
$flowStart = Join-Path $starts 'flow-retained/project'
$fsharpStart = Join-Path $starts 'fsharp/project'
Copy-StudyProject $flowAccepted $flowStart
Copy-StudyProject $fsharpAccepted $fsharpStart
if ([string]::IsNullOrWhiteSpace($FlowCliDll)) { $FlowCliDll = Join-Path $RepositoryRoot '.agentlang/maintenance-141/runtime-artifacts/bin/AgentLang.Cli/release/AgentLang.Cli.dll' }
$FlowCliDll = (Resolve-Path -LiteralPath $FlowCliDll).Path
Add-FlowFixture $flowStart $FlowCliDll
Add-FsharpFixture $fsharpStart

$hash = { param([string]$Path) (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
$inventory = foreach ($entry in @(
    @{arm='flow-retained'; accepted=$flowAccepted; study=$flowStart; files=@('dictionary.agent')},
    @{arm='fsharp'; accepted=$fsharpAccepted; study=$fsharpStart; files=@('business/Business.fs','tests/Program.fs')}
)) {
    $fileHashes = foreach ($relative in $entry.files) {
        [ordered]@{
            path = $relative
            acceptedSha256 = & $hash (Join-Path $entry.accepted $relative)
            studySha256 = & $hash (Join-Path $entry.study $relative)
        }
    }
    [ordered]@{arm=$entry.arm;acceptedSource=$entry.accepted;studyStart=$entry.study;files=@($fileHashes)}
}
$manifest = [ordered]@{
    schemaVersion = 1
    sourceReport = '135-r09-shared-summary-comparison.md'
    sourceReplica = 'accepted replica two'
    fixtureHelpers = [ordered]@{
        flow = [ordered]@{function='maintenance141.fixture-import-payment';usage='test-only'}
        fsharp = [ordered]@{function='Store.Maintenance141Fixture.importPayment';usage='test-only'}
    }
    arms = @($inventory)
}
$manifestPath = Join-Path $StudyRoot 'start-inventory.json'
[IO.File]::WriteAllText($manifestPath, (ConvertTo-Json -InputObject $manifest -Depth 20), [Text.UTF8Encoding]::new($false))
Write-Output $manifestPath
