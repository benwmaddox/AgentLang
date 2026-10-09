#requires -Version 7.5
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CliDll,
    [Parameter(Mandatory)][string]$Project,
    [string]$Oracle = (Join-Path $PSScriptRoot '..\..\oracle.json'),
    [string]$OutputPath = (Join-Path $PSScriptRoot 'last-result.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$CliDll = (Resolve-Path -LiteralPath $CliDll).Path
$Project = (Resolve-Path -LiteralPath $Project).Path
$Oracle = (Resolve-Path -LiteralPath $Oracle).Path
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$requestPath = [IO.Path]::ChangeExtension($OutputPath, 'requests.jsonl')
$responsePath = [IO.Path]::ChangeExtension($OutputPath, 'responses.jsonl')
foreach ($path in @($OutputPath,$requestPath,$responsePath)) { if (Test-Path -LiteralPath $path) { throw "Refusing to overwrite scorer evidence: $path" } }
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPath))

function Q([string]$Value) { ConvertTo-Json -InputObject $Value -Compress -Depth 10 }
function IdExpr([string]$Type, [string]$Value) { "$Type`::new($(Q $Value))" }
function MoneyExpr([string]$Value) {
    $parsed = [long]0
    if (-not [long]::TryParse($Value, [Globalization.NumberStyles]::Integer, [Globalization.CultureInfo]::InvariantCulture, [ref]$parsed)) { throw "Not signed Int64: $Value" }
    "Money::new($Value)"
}
function InstantExpr([string]$Value) {
    if ($Value.EndsWith('Z', [StringComparison]::Ordinal)) { $Value = $Value.Substring(0, $Value.Length - 1) + '+00:00' }
    "Instant::new($(Q $Value))"
}
function L([string]$Type, [string[]]$Items) {
    $expression = "list::empty<$Type>()"
    foreach ($item in $Items) { $expression = "list::append($expression, $item)" }
    $expression
}
function CustomerExpr($Spec, [string]$Id) {
    "customer::new(id=$(IdExpr 'CustomerId' $Id), email=Email::new($(Q ([string]$Spec.email))), kind=$(Q ([string]$Spec.kind)), balance=$(MoneyExpr ([string]$Spec.balance)), created-at=$(InstantExpr ([string]$Spec.createdAt)))"
}
function StoreExpr($Case, $Fixture) {
    $customerExprs = [Collections.Generic.List[string]]::new()
    foreach ($alias in $Case.customers) {
        $spec = if ($Case.customerOverrides.Contains([string]$alias)) { $Case.customerOverrides[[string]$alias] } else {
            @{ email = "$alias@example.test"; kind = 'standard'; balance = '0'; createdAt = [string]$script:fixtureTimestamps }
        }
        $customerExprs.Add((CustomerExpr $spec ([string]$Fixture.customerIds[[string]$alias])))
    }
    $store = "store::new(customers=$(L 'Customer' $customerExprs.ToArray()), products=list::empty<Product>(), subscriptions=list::empty<Subscription>(), invoices=list::empty<Invoice>(), payments=list::empty<Payment>(), email-outbox=list::empty<EmailMessage>(), sent-emails=list::empty<EmailMessage>())"
    $invoices = @{}
    foreach ($invoice in $Case.invoices) { $invoices[[string]$Fixture.invoiceIds[[string]$invoice.id]] = $invoice }
    foreach ($payment in $Case.payments) {
        $invoiceId = [string]$Fixture.invoiceIds[[string]$payment.invoice]
        $invoice = $invoices[$invoiceId]
        $status = 'option::none<InvoiceStatus>()'
        $owner = [string]$Case.query
        $isMissingRecord = $payment.Contains('invoiceRecord') -and [string]$payment.invoiceRecord -ceq 'missing'
        if ($null -ne $invoice -and -not $isMissingRecord) {
            $statusName = ([string]$invoice.status).ToLowerInvariant()
            $status = "option::some<InvoiceStatus>(InvoiceStatus::new($(Q $statusName)))"
            $owner = [string]$invoice.owner
        }
        $store = "maintenance141::fixture-import-payment($store, $(IdExpr 'PaymentId' ([string]$Fixture.paymentIds[[string]$payment.id])), $(IdExpr 'InvoiceId' $invoiceId), $(IdExpr 'CustomerId' ([string]$Fixture.customerIds[$owner])), $status, $(MoneyExpr ([string]$payment.amount)))"
    }
    $store
}
function Decode-Node($Node) {
    switch ([string]$Node.kind) {
        'result' {
            if ([string]$Node.case -ceq 'ok') { return @{ ok = (Decode-Node $Node.value) } }
            $codeField = @($Node.value.fields | Where-Object { [string]$_.name -ceq 'code' })
            if ($codeField.Count -ne 1) { throw 'Result error value lacks exactly one BusinessError.code.' }
            return @{ error = [string]$codeField[0].value.value }
        }
        'record' {
            $record = [ordered]@{}
            foreach ($field in $Node.fields) {
                if ($record.Contains([string]$field.name)) { throw "Duplicate structured field $($field.name)." }
                $record[[string]$field.name] = Decode-Node $field.value
            }
            return $record
        }
        'scalar' { return [string]$Node.value.value }
        'int' { return [int]$Node.value }
        'string' { return [string]$Node.value }
        'bool' { return [bool]$Node.value }
        default { throw "Unsupported structured kind '$($Node.kind)'." }
    }
}
function Result-Matches($Actual, $Expected, [string]$Kind, $Fixture) {
    if ($Expected.Contains('error')) { return $Actual.Contains('error') -and [string]$Actual.error -ceq [string]$Expected.error }
    if (-not $Actual.Contains('ok')) { return $false }
    if ($Kind -ceq 'shared') { return [string]$Actual.ok -ceq [string]$Expected.ok }
    $value = $Actual.ok
    if ($Kind -ceq 'account') {
        $customer = $Expected.customer
        return [string]$value['paid-total'] -ceq [string]$Expected.ok -and
            [string]$value.customer.id -ceq [string]$Fixture.customerIds[[string]$customer.id] -and
            [string]$value.customer.email -ceq [string]$customer.email -and
            [string]$value.customer.kind -ceq [string]$customer.kind -and
            [string]$value.customer.balance -ceq [string]$customer.balance -and
            ([string]$value.customer['created-at']).Replace('+00:00','Z') -ceq [string]$customer.createdAt
    }
    $countFields = @('customers','products','subscriptions','invoices','payments','pending-emails','sent-emails')
    if ([string]$value['paid-total'] -cne [string]$Expected.ok) { return $false }
    for ($index = 0; $index -lt $countFields.Count; $index++) {
        if ([int]$value[$countFields[$index]] -ne [int]$Expected.counts[$index]) { return $false }
    }
    return $true
}
function Run-Requests([object[]]$Requests) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'; $start.WorkingDirectory = $Project; $start.UseShellExecute = $false
    $start.CreateNoWindow = $true; $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($argument in @($CliDll, '--project', $Project, '--jsonl')) { [void]$start.ArgumentList.Add([string]$argument) }
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $start
    if (-not $process.Start()) { throw 'Flow CLI did not start.' }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync(); $stderrTask = $process.StandardError.ReadToEndAsync()
    foreach ($request in $Requests) { $process.StandardInput.WriteLine((ConvertTo-Json -InputObject $request -Depth 30 -Compress)) }
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(180000)) { try { $process.Kill($true) } catch { }; throw 'Flow scoring timed out.' }
    $stdout = $stdoutTask.GetAwaiter().GetResult(); $stderr = $stderrTask.GetAwaiter().GetResult()
    $script:FlowRawOutput = $stdout
    $exitCode = $process.ExitCode; $process.Dispose()
    $responses = @($stdout -split '\r?\n' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { ConvertFrom-Json -InputObject $_ -AsHashtable -Depth 30 -DateKind String })
    if ($exitCode -ne 0 -or $responses.Count -ne $Requests.Count) { throw "Flow scorer expected $($Requests.Count) responses, got $($responses.Count), exit=$exitCode. $stderr`n$stdout" }
    return ,$responses
}

$oracleData = Get-Content -LiteralPath $Oracle -Raw | ConvertFrom-Json -AsHashtable -Depth 30 -DateKind String
$script:fixtureTimestamps = [string]$oracleData.fixtures.timestamps
$requests = [Collections.Generic.List[object]]::new()
$checks = [Collections.Generic.List[object]]::new()
foreach ($case in $oracleData.cases) {
    $store = StoreExpr $case $oracleData.fixtures
    $queryId = [string]$oracleData.fixtures.customerIds[[string]$case.query]
    $direct = "customer::payment-total($store, $(IdExpr 'CustomerId' $queryId))"
    $account = "customer::account-summary($store, $(IdExpr 'CustomerId' $queryId))"
    $metrics = "store::customer-metrics($store)"
    $caseChecks = @(
        @{ label = "$($case.id)/shared"; kind='shared'; expected=$case.expected.shared; code=$direct },
        @{ label = "$($case.id)/account"; kind='account'; expected=$case.expected.account; code=$account },
        @{ label = "$($case.id)/metrics"; kind='metrics'; expected=$case.expected.metrics; code=$metrics }
    )
    foreach ($check in $caseChecks) {
        $checks.Add([ordered]@{label=[string]$check.label;kind=$check.kind;expected=$check.expected})
        $requests.Add([ordered]@{ op='eval'; frontend='flow'; syntaxVersion=2; structured=$true; code=[string]$check.code })
    }
}
$responses = Run-Requests $requests.ToArray()
[IO.File]::WriteAllLines($requestPath, [string[]]@($requests | ForEach-Object { ConvertTo-Json -InputObject $_ -Depth 30 -Compress }), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($responsePath, $script:FlowRawOutput, [Text.UTF8Encoding]::new($false))
$results = [Collections.Generic.List[object]]::new()
for ($index = 0; $index -lt $responses.Count; $index++) {
    $response = $responses[$index]
    $pass = $false; $actual = $null
    if ($response.ok -and @($response.data.structuredStack.values).Count -eq 1) {
        $actual = Decode-Node $response.data.structuredStack.values[0]
        $check = $checks[$index]
        $pass = Result-Matches $actual $check.expected ([string]$check.kind) $oracleData.fixtures
    }
    $check = $checks[$index]
    $results.Add([ordered]@{ case = $check.label; passed = $pass; actual = $actual; error = if ($response.ok) { $null } else { $response.error.code }; errorText = if ($response.ok) { $null } else { $response.text } })
}
$summary = [ordered]@{
    schemaVersion = 1
    project = [IO.Path]::GetFullPath($Project)
    oracleSha256 = (Get-FileHash -LiteralPath $Oracle -Algorithm SHA256).Hash.ToLowerInvariant()
    passed = @($results | Where-Object { $_.passed }).Count
    total = $results.Count
    results = @($results)
}
[IO.File]::WriteAllText($OutputPath, (ConvertTo-Json -InputObject $summary -Depth 20), [Text.UTF8Encoding]::new($false))
$summary | ConvertTo-Json -Depth 20
