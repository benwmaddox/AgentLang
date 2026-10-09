#requires -Version 7.5
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CliDll,
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][string]$ReferenceFixturesJson,
    [Parameter(Mandatory)][string]$ReferenceOracle,
    [Parameter(Mandatory)][string]$Oracle,
    [Parameter(Mandatory)][string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:processes = [Collections.Generic.List[object]]::new()
$script:checks = [Collections.Generic.List[object]]::new()
$script:caseResults = [Collections.Generic.List[object]]::new()
$script:failure = $null
$script:runDirectory = $null
$script:outputFull = $null
$script:inputHashesBefore = $null
$script:inputHashesAfter = $null
$script:candidateTests = $null
$script:oracleObject = $null
$script:referenceOracleObject = $null
$script:target = $null
$script:targetReady = $false
$script:candidateGate = 'missing-function'
$script:directCallsScheduled = 0

function Add-Check([string]$Name, [bool]$Passed, [string]$Details = '') {
    $script:checks.Add([ordered]@{ name = $Name; passed = $Passed; details = $Details })
}

function Require-Check([string]$Name, [bool]$Passed, [string]$Details = '') {
    Add-Check $Name $Passed $Details
    if (-not $Passed) { throw "Preflight failed: $Name. $Details" }
}

function Get-Hash([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-Value($Object, [string]$Name, $Default = $null) {
    if ($null -eq $Object) { return $Default }
    if ($Object -is [Collections.IDictionary]) {
        if ($Object.Contains($Name)) { return $Object[$Name] }
        return $Default
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $Default }
    return $property.Value
}

function Get-Inventory([string]$Root, [switch]$ExcludeBuildOutput) {
    $items = [Collections.Generic.List[object]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $Root -File -Recurse -Force) {
        $relative = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        if ($relative -match '(^|/)\.git(/|$)') { continue }
        if ($ExcludeBuildOutput -and $relative -match '(^|/)(bin|obj)(/|$)') { continue }
        $items.Add([ordered]@{ path = $relative; bytes = [int64]$file.Length; sha256 = Get-Hash $file.FullName })
    }
    return ,@($items | Sort-Object path)
}

function Get-InputHashes([string]$Candidate, [string]$RuntimeDirectory, [string]$FixturePath, [string]$ReferenceOraclePath, [string]$OraclePath, [string]$MetadataPath) {
    [ordered]@{
        candidateProject = @(Get-Inventory $Candidate -ExcludeBuildOutput)
        runtime = @(Get-Inventory $RuntimeDirectory)
        referenceFixtures = [ordered]@{ path = $FixturePath; sha256 = Get-Hash $FixturePath }
        referenceOracle = [ordered]@{ path = $ReferenceOraclePath; sha256 = Get-Hash $ReferenceOraclePath }
        oracle = [ordered]@{ path = $OraclePath; sha256 = Get-Hash $OraclePath }
        referenceMetadata = [ordered]@{ path = $MetadataPath; sha256 = Get-Hash $MetadataPath }
        scorer = [ordered]@{ path = $PSCommandPath; sha256 = Get-Hash $PSCommandPath }
    }
}

function Normalize-Json($Value) {
    if ($null -eq $Value) { return $null }
    if ($Value -is [Collections.IDictionary]) {
        $ordered = [ordered]@{}
        foreach ($key in @($Value.Keys | Sort-Object -CaseSensitive)) {
            $ordered[[string]$key] = Normalize-Json $Value[$key]
        }
        return $ordered
    }
    if ($Value -is [Collections.IList] -and $Value -isnot [string]) {
        $items = [Collections.Generic.List[object]]::new()
        foreach ($item in $Value) { $items.Add((Normalize-Json $item)) }
        return ,$items.ToArray()
    }
    return $Value
}

function Get-CanonicalJson($Value) {
    ConvertTo-Json -InputObject (Normalize-Json $Value) -Depth 100 -Compress
}

function Get-Array($Object, [string]$Name) {
    if ($null -eq $Object -or -not $Object.Contains($Name) -or $Object[$Name] -isnot [Collections.IList]) {
        throw "Expected array property '$Name'."
    }
    return ,$Object[$Name]
}

function Get-FlowString([string]$Value) {
    ConvertTo-Json -InputObject $Value -Compress -Depth 10
}

function Get-MinorUnits([string]$Value, [string]$Label) {
    $parsed = [long]0
    if ($Value -notmatch '^-?(0|[1-9][0-9]*)$' -or
        -not [long]::TryParse($Value, [Globalization.NumberStyles]::Integer, [Globalization.CultureInfo]::InvariantCulture, [ref]$parsed)) {
        throw "$Label is not a signed Int64 decimal string: '$Value'."
    }
    return $Value
}

function Get-FlowInstant([string]$Value, [string]$Label) {
    if ($Value -notmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z$') {
        throw "$Label is not the frozen UTC projection form: '$Value'."
    }
    # The F# projection emits Z; the typed Flow Instant contract uses +00:00.
    return ($Value.Substring(0, $Value.Length - 1) + '+00:00')
}

function Get-CanonicalCustomerId([string]$Symbol) {
    if ($Symbol -notmatch '^c(?<number>[1-9][0-9]*)$') { throw "Unsupported customer symbol '$Symbol'." }
    $number = [long]0
    if (-not [long]::TryParse($Matches.number, [Globalization.NumberStyles]::None, [Globalization.CultureInfo]::InvariantCulture, [ref]$number) -or $number -gt 999999999999) {
        throw "Customer symbol '$Symbol' is outside the frozen GUID mapping range."
    }
    '10000000-0000-0000-0000-{0:D12}' -f $number
}

function New-FlowList([string]$Type, [Collections.IList]$Items, [scriptblock]$Builder) {
    $expression = "list::empty<$Type>()"
    foreach ($item in $Items) {
        $value = [string](& $Builder $item)
        $expression = "list::append($expression, $value)"
    }
    return $expression
}

function New-FlowCustomer($Value) {
    $id = Get-FlowString ([string]$Value.id)
    $email = Get-FlowString ([string]$Value.email)
    $kind = Get-FlowString ([string]$Value.kind)
    $balance = Get-MinorUnits ([string]$Value.balanceMinorUnits) 'customer.balanceMinorUnits'
    $created = Get-FlowString (Get-FlowInstant ([string]$Value.createdAt) 'customer.createdAt')
    "customer::new(id = CustomerId::new($id), email = Email::new($email), kind = $kind, balance = Money::new($balance), created-at = Instant::new($created))"
}

function New-FlowProduct($Value) {
    $id = Get-FlowString ([string]$Value.id)
    $name = Get-FlowString ([string]$Value.name)
    $price = Get-MinorUnits ([string]$Value.unitPriceMinorUnits) 'product.unitPriceMinorUnits'
    "product::new(id = ProductId::new($id), name = $name, unit-price = Money::new($price))"
}

function New-FlowSubscription($Value) {
    $cancelled = if ($null -eq $Value.cancelledAt) { 'option::none<Instant>()' } else { $date = Get-FlowString (Get-FlowInstant ([string]$Value.cancelledAt) 'subscription.cancelledAt'); "option::some<Instant>(Instant::new($date))" }
    $id = Get-FlowString ([string]$Value.id)
    $customer = Get-FlowString ([string]$Value.customerId)
    $product = Get-FlowString ([string]$Value.productId)
    $term = Get-FlowString ([string]$Value.term)
    $started = Get-FlowString (Get-FlowInstant ([string]$Value.startedAt) 'subscription.startedAt')
    $expires = Get-FlowString (Get-FlowInstant ([string]$Value.expiresAt) 'subscription.expiresAt')
    $status = Get-FlowString ([string]$Value.status)
    "subscription::new(id = SubscriptionId::new($id), customer-id = CustomerId::new($customer), product-id = ProductId::new($product), term = $term, started-at = Instant::new($started), expires-at = Instant::new($expires), status = SubscriptionStatus::new($status), cancelled-at = $cancelled)"
}

function New-FlowInvoiceLine($Value) {
    $product = Get-FlowString ([string]$Value.productId)
    $description = Get-FlowString ([string]$Value.description)
    $quantity = [int]$Value.quantity
    $unitPrice = Get-MinorUnits ([string]$Value.unitPriceMinorUnits) 'invoiceLine.unitPriceMinorUnits'
    $lineTotal = Get-MinorUnits ([string]$Value.lineTotalMinorUnits) 'invoiceLine.lineTotalMinorUnits'
    "invoiceLine::new(product-id = ProductId::new($product), description = $description, quantity = $quantity, unit-price = Money::new($unitPrice), line-total = Money::new($lineTotal))"
}

function New-FlowInvoice($Value) {
    $lines = New-FlowList 'InvoiceLine' (Get-Array $Value 'lines') { param($line) New-FlowInvoiceLine $line }
    $id = Get-FlowString ([string]$Value.id)
    $customer = Get-FlowString ([string]$Value.customerId)
    $total = Get-MinorUnits ([string]$Value.totalMinorUnits) 'invoice.totalMinorUnits'
    $created = Get-FlowString (Get-FlowInstant ([string]$Value.createdAt) 'invoice.createdAt')
    $status = Get-FlowString ([string]$Value.status)
    "invoice::new(id = InvoiceId::new($id), customer-id = CustomerId::new($customer), lines = $lines, total = Money::new($total), created-at = Instant::new($created), status = InvoiceStatus::new($status))"
}

function New-FlowPayment($Value) {
    $id = Get-FlowString ([string]$Value.id)
    $invoice = Get-FlowString ([string]$Value.invoiceId)
    $amount = Get-MinorUnits ([string]$Value.amountMinorUnits) 'payment.amountMinorUnits'
    $reference = Get-FlowString ([string]$Value.providerReference)
    $paid = Get-FlowString (Get-FlowInstant ([string]$Value.paidAt) 'payment.paidAt')
    "payment::new(id = PaymentId::new($id), invoice-id = InvoiceId::new($invoice), amount = Money::new($amount), provider-reference = $reference, paid-at = Instant::new($paid))"
}

function New-FlowEmail($Value) {
    $to = Get-FlowString ([string]$Value.toAddress)
    $subject = Get-FlowString ([string]$Value.subject)
    $body = Get-FlowString ([string]$Value.body)
    "emailMessage::new(to = Email::new($to), subject = $subject, body = $body)"
}

function New-FlowStoreExpression($Projection) {
    $customers = New-FlowList 'Customer' (Get-Array $Projection 'customers') { param($item) New-FlowCustomer $item }
    $products = New-FlowList 'Product' (Get-Array $Projection 'products') { param($item) New-FlowProduct $item }
    $subscriptions = New-FlowList 'Subscription' (Get-Array $Projection 'subscriptions') { param($item) New-FlowSubscription $item }
    $invoices = New-FlowList 'Invoice' (Get-Array $Projection 'invoices') { param($item) New-FlowInvoice $item }
    $payments = New-FlowList 'Payment' (Get-Array $Projection 'payments') { param($item) New-FlowPayment $item }
    $pending = New-FlowList 'EmailMessage' (Get-Array $Projection 'pendingEmails') { param($item) New-FlowEmail $item }
    $sent = New-FlowList 'EmailMessage' (Get-Array $Projection 'sentEmails') { param($item) New-FlowEmail $item }
    "store::new(customers = $customers, products = $products, subscriptions = $subscriptions, invoices = $invoices, payments = $payments, email-outbox = $pending, sent-emails = $sent)"
}

$script:recordSchemas = @{
    Store = @(
        @{ field = 'customers'; output = 'customers'; type = 'List<Customer>' },
        @{ field = 'products'; output = 'products'; type = 'List<Product>' },
        @{ field = 'subscriptions'; output = 'subscriptions'; type = 'List<Subscription>' },
        @{ field = 'invoices'; output = 'invoices'; type = 'List<Invoice>' },
        @{ field = 'payments'; output = 'payments'; type = 'List<Payment>' },
        @{ field = 'email-outbox'; output = 'pendingEmails'; type = 'List<EmailMessage>' },
        @{ field = 'sent-emails'; output = 'sentEmails'; type = 'List<EmailMessage>' }
    )
    Customer = @(
        @{ field = 'id'; output = 'id'; type = 'CustomerId' }, @{ field = 'email'; output = 'email'; type = 'Email' },
        @{ field = 'kind'; output = 'kind'; type = 'String' }, @{ field = 'balance'; output = 'balanceMinorUnits'; type = 'Money' },
        @{ field = 'created-at'; output = 'createdAt'; type = 'Instant' }
    )
    Product = @(
        @{ field = 'id'; output = 'id'; type = 'ProductId' }, @{ field = 'name'; output = 'name'; type = 'String' },
        @{ field = 'unit-price'; output = 'unitPriceMinorUnits'; type = 'Money' }
    )
    Subscription = @(
        @{ field = 'id'; output = 'id'; type = 'SubscriptionId' }, @{ field = 'customer-id'; output = 'customerId'; type = 'CustomerId' },
        @{ field = 'product-id'; output = 'productId'; type = 'ProductId' }, @{ field = 'term'; output = 'term'; type = 'String' },
        @{ field = 'started-at'; output = 'startedAt'; type = 'Instant' }, @{ field = 'expires-at'; output = 'expiresAt'; type = 'Instant' },
        @{ field = 'status'; output = 'status'; type = 'SubscriptionStatus' }, @{ field = 'cancelled-at'; output = 'cancelledAt'; type = 'Option<Instant>' }
    )
    InvoiceLine = @(
        @{ field = 'product-id'; output = 'productId'; type = 'ProductId' }, @{ field = 'description'; output = 'description'; type = 'String' },
        @{ field = 'quantity'; output = 'quantity'; type = 'Int' }, @{ field = 'unit-price'; output = 'unitPriceMinorUnits'; type = 'Money' },
        @{ field = 'line-total'; output = 'lineTotalMinorUnits'; type = 'Money' }
    )
    Invoice = @(
        @{ field = 'id'; output = 'id'; type = 'InvoiceId' }, @{ field = 'customer-id'; output = 'customerId'; type = 'CustomerId' },
        @{ field = 'lines'; output = 'lines'; type = 'List<InvoiceLine>' }, @{ field = 'total'; output = 'totalMinorUnits'; type = 'Money' },
        @{ field = 'created-at'; output = 'createdAt'; type = 'Instant' }, @{ field = 'status'; output = 'status'; type = 'InvoiceStatus' }
    )
    Payment = @(
        @{ field = 'id'; output = 'id'; type = 'PaymentId' }, @{ field = 'invoice-id'; output = 'invoiceId'; type = 'InvoiceId' },
        @{ field = 'amount'; output = 'amountMinorUnits'; type = 'Money' }, @{ field = 'provider-reference'; output = 'providerReference'; type = 'String' },
        @{ field = 'paid-at'; output = 'paidAt'; type = 'Instant' }
    )
    EmailMessage = @(
        @{ field = 'to'; output = 'toAddress'; type = 'Email' }, @{ field = 'subject'; output = 'subject'; type = 'String' },
        @{ field = 'body'; output = 'body'; type = 'String' }
    )
}

function Assert-FlowType($Descriptor, [string]$Expected) {
    if ($Expected -match '^(List|Option)<(.+)>$') {
        $container = $Matches[1].ToLowerInvariant()
        if ([string]$Descriptor.kind -cne $container) { throw "Expected type $Expected, got descriptor kind '$($Descriptor.kind)'." }
        Assert-FlowType $Descriptor.elementType $Matches[2]
        return
    }
    if ($Expected -in @('String', 'Int', 'Bool')) {
        $kind = $Expected.ToLowerInvariant()
        if ([string]$Descriptor.kind -cne $kind) { throw "Expected type $Expected, got descriptor kind '$($Descriptor.kind)'." }
        return
    }
    $nominalKind = if ($Expected -in @('Store','Customer','Product','Subscription','InvoiceLine','Invoice','Payment','EmailMessage','BusinessError')) { 'record' } else { 'scalar' }
    if ([string]$Descriptor.kind -cne 'nominal' -or [string]$Descriptor.name -cne $Expected -or [string]$Descriptor.nominalKind -cne $nominalKind) {
        throw "Expected nominal type $Expected, got '$($Descriptor.name)' ($($Descriptor.nominalKind))."
    }
}

function Convert-FlowValue($Node, [string]$Expected) {
    if ($Expected -match '^List<(.+)>$') {
        $elementType = $Matches[1]
        if ([string]$Node.kind -cne 'list') { throw "Expected a structured list of $elementType." }
        Assert-FlowType $Node.elementType $elementType
        $items = [Collections.Generic.List[object]]::new()
        foreach ($item in $Node.items) { $items.Add((Convert-FlowValue $item $elementType)) }
        return ,$items.ToArray()
    }
    if ($Expected -match '^Option<(.+)>$') {
        $elementType = $Matches[1]
        if ([string]$Node.kind -cne 'option') { throw "Expected a structured option of $elementType." }
        Assert-FlowType $Node.elementType $elementType
        if ([string]$Node.case -ceq 'none') { return $null }
        if ([string]$Node.case -cne 'some') { throw "Unknown structured option case '$($Node.case)'." }
        return Convert-FlowValue $Node.value $elementType
    }
    if ($script:recordSchemas.ContainsKey($Expected)) {
        if ([string]$Node.kind -cne 'record' -or [string]$Node.name -cne $Expected) { throw "Expected structured record $Expected." }
        $fields = @{}
        foreach ($field in $Node.fields) {
            if ($fields.ContainsKey([string]$field.name)) { throw "Duplicate structured field '$($field.name)' in $Expected." }
            $fields[[string]$field.name] = $field
        }
        $schema = @($script:recordSchemas[$Expected])
        if ($fields.Count -ne $schema.Count) { throw "Record $Expected has $($fields.Count) fields; expected $($schema.Count)." }
        $projection = [ordered]@{}
        foreach ($spec in $schema) {
            if (-not $fields.ContainsKey([string]$spec.field)) { throw "Record $Expected is missing '$($spec.field)'." }
            $field = $fields[[string]$spec.field]
            Assert-FlowType $field.type ([string]$spec.type)
            $projection[[string]$spec.output] = Convert-FlowValue $field.value ([string]$spec.type)
        }
        return $projection
    }
    if ($Expected -in @('CustomerId','ProductId','SubscriptionId','InvoiceId','PaymentId','Email','Instant','SubscriptionStatus','InvoiceStatus','Money')) {
        if ([string]$Node.kind -cne 'scalar' -or [string]$Node.name -cne $Expected) { throw "Expected structured scalar $Expected." }
        $baseType = if ($Expected -eq 'Money') { 'Int' } else { 'String' }
        Assert-FlowType $Node.baseType $baseType
        $scalar = $Node.value.value
        if ($Expected -eq 'Money') { return Get-MinorUnits ([string]$scalar) "structured $Expected" }
        if ($Expected -eq 'Instant') {
            if ([string]$scalar -notmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}\+00:00$') { throw "Unexpected Flow Instant value '$scalar'." }
            return ([string]$scalar).Substring(0, ([string]$scalar).Length - 6) + 'Z'
        }
        return [string]$scalar
    }
    $expectedKind = $Expected.ToLowerInvariant()
    if ([string]$Node.kind -cne $expectedKind) { throw "Expected structured $Expected, got '$($Node.kind)'." }
    if ($Expected -eq 'String') { return [string]$Node.value }
    if ($Expected -eq 'Bool') { return [bool]$Node.value }
    if ($Expected -eq 'Int') {
        $parsed = [long]0
        if (-not [long]::TryParse([string]$Node.value, [Globalization.NumberStyles]::Integer, [Globalization.CultureInfo]::InvariantCulture, [ref]$parsed) -or $parsed -lt [int]::MinValue -or $parsed -gt [int]::MaxValue) {
            throw "Structured Int is outside Int32: '$($Node.value)'."
        }
        return [int]$parsed
    }
    throw "Unsupported structured projection type '$Expected'."
}

function Get-StructuredValue($Response, [string]$ExpectedType) {
    $values = $Response.data.structuredStack.values
    if ($null -eq $values -or @($values).Count -ne 1) { throw 'Structured result did not contain exactly one value.' }
    Convert-FlowValue $Response.data.structuredStack.values[0] $ExpectedType
}

function Get-StructuredResult($Response) {
    $full = Get-StructuredResultFull $Response
    if ($full.Contains('ok')) { return $full }
    return [ordered]@{ error = [string]$full.error.code }
}

function Get-StructuredResultFull($Response) {
    $values = $Response.data.structuredStack.values
    if ($null -eq $values -or @($values).Count -ne 1) { throw 'Direct call did not return exactly one structured value.' }
    $node = $values[0]
    if ([string]$node.kind -cne 'result') { throw "Expected Result<Money, BusinessError>, got '$($node.kind)'." }
    Assert-FlowType $node.okType 'Money'
    Assert-FlowType $node.errorType 'BusinessError'
    if ([string]$node.case -ceq 'ok') {
        $value = Convert-FlowValue $node.value 'Money'
        return [ordered]@{ ok = $value }
    }
    if ([string]$node.case -cne 'error' -or [string]$node.value.kind -cne 'record' -or [string]$node.value.name -cne 'BusinessError') {
        throw "Invalid structured Result error payload '$($node.case)'."
    }
    $fields = @{}
    foreach ($field in $node.value.fields) {
        if ($fields.ContainsKey([string]$field.name)) { throw "Duplicate BusinessError field '$($field.name)' in Result error." }
        $fields[[string]$field.name] = $field
    }
    if ($fields.Count -ne 2 -or -not $fields.ContainsKey('code') -or -not $fields.ContainsKey('message')) { throw 'BusinessError does not contain exactly its code and message fields.' }
    foreach ($name in @('code','message')) { Assert-FlowType $fields[$name].type 'String' }
    return [ordered]@{ error = [ordered]@{ code = [string]$fields.code.value.value; message = [string]$fields.message.value.value } }
}

function Get-ResponseEffects($Response) {
    $effects = $Response.data.effects
    if ($null -eq $effects) { return }
    if ($effects -is [Collections.IDictionary]) { return @($effects.Keys | Sort-Object -CaseSensitive) }
    return @()
}

function Invoke-FlowJsonl([string]$Name, [string]$ProjectPath, [object[]]$Requests, [string]$CliPath) {
    $folder = Join-Path $script:runDirectory $Name
    [void][IO.Directory]::CreateDirectory($folder)
    $requestLines = [Collections.Generic.List[string]]::new()
    foreach ($request in $Requests) { $requestLines.Add((ConvertTo-Json -InputObject $request -Depth 100 -Compress)) }
    [IO.File]::WriteAllLines((Join-Path $folder 'requests.jsonl'), $requestLines, [Text.UTF8Encoding]::new($false))
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.WorkingDirectory = $ProjectPath
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @($CliPath, '--project', $ProjectPath, '--jsonl')) { [void]$start.ArgumentList.Add([string]$argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $stdout = ''
    $stderr = ''
    $exitCode = $null
    $timedOut = $false
    $processStarted = $false
    $processExited = $false
    $drainTimedOut = $false
    $launchError = $null
    $readErrors = [Collections.Generic.List[string]]::new()
    $stdoutTask = $null
    $stderrTask = $null
    $responses = [Collections.Generic.List[object]]::new()
    $parseErrors = [Collections.Generic.List[string]]::new()
    try {
        if (-not $process.Start()) { throw 'Process did not start.' }
        $processStarted = $true
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        foreach ($line in $requestLines) { $process.StandardInput.WriteLine($line) }
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(180000)) {
            $timedOut = $true
            try { $process.Kill($true) } catch { }
            [void]$process.WaitForExit(5000)
        }
        $processExited = $process.HasExited
        if ($processExited) { $exitCode = $process.ExitCode }
    }
    catch { $launchError = $_.Exception.ToString() }
    finally {
        if ($processStarted) {
            try {
                if (-not $process.HasExited) {
                    try { $process.Kill($true) } catch { }
                    [void]$process.WaitForExit(5000)
                }
                $processExited = $process.HasExited
                if ($processExited -and $null -eq $exitCode) { $exitCode = $process.ExitCode }
            } catch { }
        }
        foreach ($read in @(@{ name = 'stdout'; task = $stdoutTask }, @{ name = 'stderr'; task = $stderrTask })) {
            if ($null -eq $read.task) { continue }
            try {
                if ($read.task.Wait(10000)) {
                    if ($read.name -eq 'stdout') { $stdout = $read.task.GetAwaiter().GetResult() }
                    else { $stderr = $read.task.GetAwaiter().GetResult() }
                } else {
                    $drainTimedOut = $true
                    $readErrors.Add("$($read.name) drain exceeded 10000 ms.")
                }
            } catch { $readErrors.Add("$($read.name) drain failed: $($_.Exception.Message)") }
        }
        $process.Dispose()
    }
    [IO.File]::WriteAllText((Join-Path $folder 'stdout.txt'), $stdout, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $folder 'stderr.txt'), $stderr, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $folder 'exit-code.txt'), $(if ($null -eq $exitCode) { 'null' } else { [string]$exitCode }), [Text.UTF8Encoding]::new($false))
    foreach ($line in @($stdout -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })) {
        try { $responses.Add((ConvertFrom-Json -InputObject $line -AsHashtable -Depth 100 -DateKind String -ErrorAction Stop)) }
        catch { $parseErrors.Add($_.Exception.Message + ' | ' + $line) }
    }
    $entry = [ordered]@{
        name = $Name; project = $ProjectPath; requests = @($Requests); requestCount = $Requests.Count
        responses = $responses.ToArray(); responseCount = $responses.Count; exitCode = $exitCode
        processStarted = $processStarted; processExited = $processExited; timedOut = $timedOut
        drainTimedOut = $drainTimedOut; launchError = $launchError; readErrors = $readErrors.ToArray(); parseErrors = $parseErrors.ToArray()
        stdoutPath = Join-Path $folder 'stdout.txt'; stderrPath = Join-Path $folder 'stderr.txt'
    }
    $script:processes.Add($entry)
    [IO.File]::WriteAllText((Join-Path $folder 'process.json'), (ConvertTo-Json -InputObject $entry -Depth 100), [Text.UTF8Encoding]::new($false))
    return $entry
}

function Assert-FlowJsonlProcess($Entry) {
    $ok = [bool]$Entry.processStarted -and [bool]$Entry.processExited -and
        -not [bool]$Entry.timedOut -and -not [bool]$Entry.drainTimedOut -and
        $null -eq $Entry.launchError -and @($Entry.readErrors).Count -eq 0 -and
        @($Entry.parseErrors).Count -eq 0 -and $Entry.exitCode -eq 0 -and
        [int]$Entry.responseCount -eq [int]$Entry.requestCount
    $details = Get-CanonicalJson ([ordered]@{
        started = $Entry.processStarted; exited = $Entry.processExited; exitCode = $Entry.exitCode
        timedOut = $Entry.timedOut; drainTimedOut = $Entry.drainTimedOut
        launchError = $Entry.launchError; readErrors = $Entry.readErrors; parseErrors = $Entry.parseErrors
        requests = $Entry.requestCount; responses = $Entry.responseCount
    })
    Add-Check "JSONL process completed cleanly: $($Entry.name)" $ok $details
    if (-not $ok) { throw "JSONL process failed its completion gate: $($Entry.name). $details" }
}

function Copy-Project([string]$From, [string]$To) {
    [void][IO.Directory]::CreateDirectory($To)
    foreach ($directory in Get-ChildItem -LiteralPath $From -Directory -Recurse -Force) {
        $relative = [IO.Path]::GetRelativePath($From, $directory.FullName).Replace('\', '/')
        if ($relative -match '(^|/)\.git(/|$)' -or $relative -match '(^|/)(bin|obj)(/|$)') { continue }
        [void][IO.Directory]::CreateDirectory((Join-Path $To ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))))
    }
    foreach ($file in Get-ChildItem -LiteralPath $From -File -Recurse -Force) {
        $relative = [IO.Path]::GetRelativePath($From, $file.FullName).Replace('\', '/')
        if ($relative -match '(^|/)\.git(/|$)' -or $relative -match '(^|/)(bin|obj)(/|$)') { continue }
        $destination = Join-Path $To ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
        [IO.File]::Copy($file.FullName, $destination, $true)
    }
}

function Assert-ReferenceFixture($Fixture, $OracleObject, [string]$OraclePath) {
    $oracleHash = Get-Hash $OraclePath
    Require-Check 'reference fixture schema and frozen status' ([int]$Fixture.schemaVersion -eq 1 -and [string]$Fixture.status -ceq 'all-reference-cases-match' -and [bool]$Fixture.inputUnchanged) ([string]$Fixture.status)
    Require-Check 'reference fixture embeds unchanged root oracle hash' (([string]$Fixture.oracleSha256Before).ToLowerInvariant() -ceq $oracleHash -and ([string]$Fixture.oracleSha256After).ToLowerInvariant() -ceq $oracleHash) $oracleHash
    Require-Check 'reference and oracle contain thirteen cases' (@($Fixture.cases).Count -eq 13 -and @($OracleObject.cases).Count -eq 13) "fixture=$(@($Fixture.cases).Count), oracle=$(@($OracleObject.cases).Count)"
    Require-Check 'all reference cases accepted and no blocked or mismatched cases' ([int]$Fixture.caseSummary.acceptedCaseCount -eq 13 -and [int]$Fixture.caseSummary.blockedCaseCount -eq 0 -and [int]$Fixture.caseSummary.mismatchedCaseCount -eq 0) (Get-CanonicalJson $Fixture.caseSummary)
    $oracleById = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($item in $OracleObject.cases) {
        if ($oracleById.ContainsKey([string]$item.id)) { throw "Duplicate oracle case '$($item.id)'." }
        $oracleById[[string]$item.id] = $item
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($case in $Fixture.cases) {
        if (-not $oracleById.ContainsKey([string]$case.id) -or -not $seen.Add([string]$case.id)) { throw "Fixture case '$($case.id)' is missing from or duplicated in the oracle." }
        $oracleCase = $oracleById[[string]$case.id]
        if ([string]$case.status -cne 'accepted-reference' -or $null -ne $case.failure -or @($case.constructorFailures).Count -ne 0) { throw "Reference case '$($case.id)' is not cleanly accepted." }
        $expected = $oracleById[[string]$case.id].expected
        if ((Get-CanonicalJson $case.oracleExpected) -cne (Get-CanonicalJson $expected)) { throw "Embedded oracle expectation differs for '$($case.id)'." }
        if ((Get-CanonicalJson (Convert-ReferenceOutcome $case.reference.direct)) -cne (Get-CanonicalJson $expected.direct)) { throw "Reference direct result differs from the frozen oracle for '$($case.id)'." }
        if (-not [bool]$case.storeBefore.complete -or -not [bool]$case.storeAfter.complete) { throw "Store projection is incomplete for '$($case.id)'." }
        if ((Get-CanonicalJson $case.storeBefore) -cne (Get-CanonicalJson $case.storeAfter)) { throw "Reference Store changed for '$($case.id)'." }
        foreach ($checkName in @('directMatchesOracle','storeProjectionUnchanged','projectionContainsEveryCountedValue','paymentProjectionOrderMatchesRecipe','metricsCountsMatchRecipe')) {
            if (-not [bool]$case.checks[$checkName]) { throw "Reference fixture check '$checkName' failed for '$($case.id)'." }
        }
        if ([string]$case.id -ceq 'overflow-remains-sticky' -and -not [bool]$case.checks.stickyOverflowAmountOrderVerified) { throw 'Sticky-overflow payment order was not verified.' }
        $store = $case.storeBefore
        $countProperties = @(
            @{name='customers'; count='customers'}, @{name='products'; count='products'}, @{name='subscriptions'; count='subscriptions'},
            @{name='invoices'; count='invoices'}, @{name='payments'; count='payments'}, @{name='pendingEmails'; count='pendingEmails'}, @{name='sentEmails'; count='sentEmails'}
        )
        foreach ($spec in $countProperties) {
            $values = Get-Array $store ([string]$spec.name)
            if (@($values).Count -ne [int]$store.counts[[string]$spec.count]) { throw "Fixture count for '$($spec.name)' does not match its full projection in '$($case.id)'." }
        }
        $query = [string]$oracleCase.query
        $queryGuid = Get-CanonicalCustomerId $query
        $known = @($store.customers | Where-Object { [string]$_.id -ceq $queryGuid }).Count -gt 0
        if ($known -and $expected.direct.Contains('error') -and [string]$expected.direct.error -ceq 'CUSTOMER_NOT_FOUND') { throw "Oracle marks known customer '$query' as missing in '$($case.id)'." }
        if (-not $known -and $expected.direct.Contains('ok')) { throw "Oracle expects a total for unknown customer '$query' in '$($case.id)'." }
        foreach ($symbol in $oracleCase.customers) {
            $knownGuid = Get-CanonicalCustomerId ([string]$symbol)
            if (@($store.customers | Where-Object { [string]$_.id -ceq $knownGuid }).Count -ne 1) { throw "Reference Store does not contain mapped customer '$symbol' in '$($case.id)'." }
        }
    }
    if ($seen.Count -ne 13) { throw 'Fixture and reference-oracle case sets differ.' }
}

function Assert-CompositionOracle($Fixture, $ReferenceOracle, $CompositionOracle) {
    $fixtureById = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($case in $Fixture.cases) { $fixtureById[[string]$case.id] = $case }
    $referenceById = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($case in $ReferenceOracle.cases) { $referenceById[[string]$case.id] = $case }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $max = [System.Numerics.BigInteger]::Parse('9223372036854775807', [Globalization.CultureInfo]::InvariantCulture)
    foreach ($case in $CompositionOracle.cases) {
        $id = [string]$case.id
        if (-not $seen.Add($id)) { throw "Duplicate composition case '$id'." }
        $fixtureId = [string]$case.fixtureCase
        if (-not $fixtureById.ContainsKey($fixtureId) -or -not $referenceById.ContainsKey($fixtureId)) { throw "Composition case '$id' references an unknown R09 fixture '$fixtureId'." }
        $fixtureCase = $fixtureById[$fixtureId]
        $referenceCase = $referenceById[$fixtureId]
        if ([string]$case.query -cne [string]$referenceCase.query) { throw "Composition case '$id' query differs from its frozen R09 case." }
        $cap = [System.Numerics.BigInteger]::Parse([string]$case.capMinorUnits, [Globalization.CultureInfo]::InvariantCulture)
        if ($cap -lt [System.Numerics.BigInteger]::Parse('-9223372036854775808', [Globalization.CultureInfo]::InvariantCulture) -or $cap -gt $max) { throw "Composition case '$id' cap is outside signed Int64." }
        $expected = $case.expected
        if ($null -eq $expected -or $expected.Count -ne 1) { throw "Composition case '$id' must have exactly one expected Result branch." }
        $paymentOverride = Get-Value $case 'paymentAmountOverride'
        if ($null -ne $paymentOverride) {
            if (-not [bool]$case.defensiveOutOfInvariant -or @($fixtureCase.storeBefore.payments).Count -ne 1) { throw "Composition case '$id' has an invalid defensive payment override." }
            $paid = [System.Numerics.BigInteger]::Parse([string]$paymentOverride, [Globalization.CultureInfo]::InvariantCulture)
            if ($paid -lt [System.Numerics.BigInteger]::Parse('-9223372036854775808', [Globalization.CultureInfo]::InvariantCulture) -or $paid -gt $max) { throw "Composition case '$id' override is outside signed Int64." }
            $expectedPaid = $cap - $paid
            $expectedResult = if ($expectedPaid -le [System.Numerics.BigInteger]::Zero) { [ordered]@{ ok = '0' } } elseif ($expectedPaid -gt $max) { [ordered]@{ error = 'MONEY_OVERFLOW' } } else { [ordered]@{ ok = $expectedPaid.ToString([Globalization.CultureInfo]::InvariantCulture) } }
        } elseif ($referenceCase.expected.direct.Contains('error')) {
            $code = [string]$referenceCase.expected.direct.error
            $expectedResult = [ordered]@{ error = $code }
        } else {
            $paid = [System.Numerics.BigInteger]::Parse([string]$referenceCase.expected.direct.ok, [Globalization.CultureInfo]::InvariantCulture)
            $difference = $cap - $paid
            $expectedResult = if ($difference -le [System.Numerics.BigInteger]::Zero) { [ordered]@{ ok = '0' } } elseif ($difference -gt $max) { [ordered]@{ error = 'MONEY_OVERFLOW' } } else { [ordered]@{ ok = $difference.ToString([Globalization.CultureInfo]::InvariantCulture) } }
        }
        if ((Get-CanonicalJson $expectedResult) -cne (Get-CanonicalJson $expected)) { throw "Composition oracle case '$id' disagrees with independent signed arithmetic or the frozen R09 paid-total result." }
    }
    if ($seen.Count -lt 9) { throw 'The composition oracle must contain at least nine cases.' }
    Require-Check 'composition oracle cases are unique and independently derived' $true "caseCount=$($seen.Count); signed arithmetic checked with BigInteger; R09 outcomes verified by the frozen fixture adapter."
}

function Get-ExpectedStoreProjection($Store) {
    [ordered]@{
        customers = $Store.customers; products = $Store.products; subscriptions = $Store.subscriptions
        invoices = $Store.invoices; payments = $Store.payments; pendingEmails = $Store.pendingEmails; sentEmails = $Store.sentEmails
    }
}

function Convert-ReferenceOutcome($Outcome) {
    if ([string]$Outcome.status -ceq 'ok') { return [ordered]@{ ok = [string]$Outcome.value } }
    if ([string]$Outcome.status -ceq 'error' -and -not [string]::IsNullOrWhiteSpace([string]$Outcome.errorCode)) {
        return [ordered]@{ error = [string]$Outcome.errorCode }
    }
    throw "Reference outcome has invalid status/code: $((Get-CanonicalJson $Outcome))."
}

function Get-TestAllSummary($Response) {
    $results = @()
    if ($null -ne $Response.data -and $Response.data.Contains('results')) { $results = @($Response.data.results) }
    $pass = 0
    $fail = 0
    foreach ($result in $results) {
        $passed = Get-Value $result 'passed'
        $status = [string](Get-Value $result 'status' '')
        if ($passed -eq $true -or $status -ceq 'passed') { $pass++ }
        if ($passed -eq $false -or $status -ceq 'failed') { $fail++ }
    }
    [ordered]@{ responseOk = [bool]$Response.ok; text = [string]$Response.text; resultCount = $results.Count; passedCount = $pass; failedCount = $fail; allPassed = [bool]$Response.ok -and $results.Count -gt 0 -and $pass -eq $results.Count -and $fail -eq 0; response = $Response }
}

function Write-JsonFile([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 100), [Text.UTF8Encoding]::new($false))
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..'))
$metadataPath = Join-Path $repoRoot 'experiments/AgentLang.SubagentTrials/r09-discovery-001/reference/README.md'
$outputParent = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($OutputPath))
[void][IO.Directory]::CreateDirectory($outputParent)
$script:outputFull = [IO.Path]::GetFullPath($OutputPath)
    $evidenceRoot = Join-Path $repoRoot '.agentlang/discovery-composition-137/scoring-evidence'
[void][IO.Directory]::CreateDirectory($evidenceRoot)
$runId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N')
$script:runDirectory = Join-Path $evidenceRoot ('attempt-' + $runId)
[void][IO.Directory]::CreateDirectory($script:runDirectory)
$workProject = Join-Path $script:runDirectory 'project-copy'

try {
    $cliFull = (Resolve-Path -LiteralPath $CliDll).Path
    $projectFull = (Resolve-Path -LiteralPath $Project).Path
    $fixtureFull = (Resolve-Path -LiteralPath $ReferenceFixturesJson).Path
    $referenceOracleFull = (Resolve-Path -LiteralPath $ReferenceOracle).Path
    $oracleFull = (Resolve-Path -LiteralPath $Oracle).Path
    $metadataFull = (Resolve-Path -LiteralPath $metadataPath).Path
    if (-not (Test-Path -LiteralPath (Join-Path $projectFull 'dictionary.agent') -PathType Leaf)) { throw "Project has no dictionary.agent: $projectFull" }
    $runtimeDirectory = Split-Path -Parent $cliFull
    $fixture = Get-Content -LiteralPath $fixtureFull -Raw | ConvertFrom-Json -AsHashtable -Depth 100 -DateKind String
    $script:referenceOracleObject = Get-Content -LiteralPath $referenceOracleFull -Raw | ConvertFrom-Json -AsHashtable -Depth 100 -DateKind String
    $script:oracleObject = Get-Content -LiteralPath $oracleFull -Raw | ConvertFrom-Json -AsHashtable -Depth 100 -DateKind String
    Assert-ReferenceFixture $fixture $script:referenceOracleObject $referenceOracleFull
    Assert-CompositionOracle $fixture $script:referenceOracleObject $script:oracleObject
    $script:inputHashesBefore = Get-InputHashes $projectFull $runtimeDirectory $fixtureFull $referenceOracleFull $oracleFull $metadataFull
    Write-JsonFile (Join-Path $script:runDirectory 'input-hashes-before.json') $script:inputHashesBefore
    Copy-Project $projectFull $workProject
    $workHashesBefore = @(Get-Inventory $workProject -ExcludeBuildOutput)

    $startupRequests = @([ordered]@{op='test-all'}, [ordered]@{op='words'})
    $startup = Invoke-FlowJsonl 'candidate-test-all-and-words' $workProject $startupRequests $cliFull
    Assert-FlowJsonlProcess $startup
    $testResponse = if ($startup.responses.Count -gt 0) { $startup.responses[0] } else { [ordered]@{ok=$false;text='missing test-all response'} }
    $script:candidateTests = Get-TestAllSummary $testResponse
    Add-Check 'candidate attached test suite passes' ([bool]$script:candidateTests.allPassed) (Get-CanonicalJson $script:candidateTests)
    $wordResponse = if ($startup.responses.Count -gt 1) { $startup.responses[1] } else { $null }
    if ($null -ne $wordResponse -and [bool]$wordResponse.ok -and $wordResponse.data.Contains('words')) {
        $targetWords = @($wordResponse.data.words | Where-Object { [string]$_.name -ceq 'customer.remaining-lifetime-allowance' })
    } else { $targetWords = @() }
    if ($targetWords.Count -eq 1) {
        $script:target = $targetWords[0]
        $signatureOk = (@($script:target.inputs) -join ',') -ceq 'Store,CustomerId,Money' -and @($script:target.outputs).Count -eq 1 -and [string]$script:target.outputs[0] -ceq 'Result<Money, BusinessError>'
        $pure = @($script:target.effects).Count -eq 0
        $libraryReady = [string]$script:target.status -ceq 'persistent' -and [string]$script:target.maturity -ceq 'library'
        $script:targetReady = $signatureOk -and $pure -and $libraryReady
        $script:candidateGate = if ($script:targetReady) { 'evaluated' } else { 'invalid-candidate' }
        Add-Check 'candidate function exists with requested typed signature' $signatureOk (Get-CanonicalJson ([ordered]@{inputs=$script:target.inputs;outputs=$script:target.outputs}))
        Add-Check 'candidate function declares no effects' $pure (Get-CanonicalJson $script:target.effects)
        Add-Check 'candidate is persistent library function' $libraryReady (Get-CanonicalJson ([ordered]@{status=$script:target.status;maturity=$script:target.maturity}))
    } else {
        $script:targetReady = $false
        $script:candidateGate = if ($targetWords.Count -eq 0) { 'missing-function' } else { 'ambiguous-function' }
        Add-Check 'candidate function exists exactly once' $false "found=$($targetWords.Count)"
    }

    if ($script:targetReady) {
        $inspect = Invoke-FlowJsonl 'candidate-source-and-dependency-inspection' $workProject @(
            [ordered]@{op='source';word='customer.remaining-lifetime-allowance'},
            [ordered]@{op='dependencies';word='customer.remaining-lifetime-allowance'},
            [ordered]@{op='describe';word='customer.remaining-lifetime-allowance'}
        ) $cliFull
        Assert-FlowJsonlProcess $inspect
        $sourceResponse = if ($inspect.responses.Count -gt 0) { $inspect.responses[0] } else { $null }
        $dependencyResponse = if ($inspect.responses.Count -gt 1) { $inspect.responses[1] } else { $null }
        $describeResponse = if ($inspect.responses.Count -gt 2) { $inspect.responses[2] } else { $null }
        $sourceText = if ($null -ne $sourceResponse -and [bool]$sourceResponse.ok) { [string]$sourceResponse.data } else { '' }
        $dependencies = if ($null -ne $dependencyResponse -and [bool]$dependencyResponse.ok) { @($dependencyResponse.data.dependencies | ForEach-Object { [string]$_ }) } else { @() }
        $describe = if ($null -ne $describeResponse -and [bool]$describeResponse.ok) { $describeResponse.data } else { $null }
        $helperReuse = $dependencies -ccontains 'customer.paid-total' -and $sourceText.Contains('customer::paid-total(')
        $noDuplicateAggregation = -not ($sourceText -match '(?i)store::payments\s*\(|store::invoice\s*\(|\.fold\s*\(|money::add\s*\(')
        $testCount = if ($null -ne $describe) { [int]$describe.testCount } else { 0 }
        $attachedTests = $null -ne $describe -and $testCount -ge 3
        Add-Check 'candidate directly calls existing paid-total helper' $helperReuse (Get-CanonicalJson ([ordered]@{dependencies=$dependencies;sourceCall=$sourceText.Contains('customer::paid-total(')}))
        Add-Check 'candidate does not duplicate paid-total aggregation' $noDuplicateAggregation 'Source is checked for payment/invoice traversal, fold aggregation, and money addition.'
        Add-Check 'candidate has attached documentation and meaningful tests' ($sourceText.Contains('doc "') -and $attachedTests) (Get-CanonicalJson ([ordered]@{testCount=$testCount;attachedTests=$attachedTests;hasDocumentation=$sourceText.Contains('doc "')}))
    }

    $evalRequests = [Collections.Generic.List[object]]::new()
    $caseBindings = [Collections.Generic.List[object]]::new()
    $fixtureById = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($item in $fixture.cases) { $fixtureById[[string]$item.id] = $item }
    foreach ($oracleCase in $script:oracleObject.cases) {
        $case = $fixtureById[[string]$oracleCase.fixtureCase]
        $paymentOverride = Get-Value $oracleCase 'paymentAmountOverride'
        $storeProjection = Get-ExpectedStoreProjection $case.storeBefore
        $storeProjection = ConvertFrom-Json -InputObject (Get-CanonicalJson $storeProjection) -AsHashtable -Depth 100 -DateKind String
        if ($null -ne $paymentOverride) { $storeProjection.payments[0].amountMinorUnits = [string]$paymentOverride }
        $storeExpr = New-FlowStoreExpression $storeProjection
        $roundtripIndex = $evalRequests.Count
        $evalRequests.Add([ordered]@{op='eval';frontend='flow';syntaxVersion=2;structured=$true;code=$storeExpr})
        $directIndex = $null
        $helperIndex = $null
        if ($script:targetReady) {
            $queryGuid = Get-CanonicalCustomerId ([string]$oracleCase.query)
            $directIndex = $evalRequests.Count
            $script:directCallsScheduled++
            $evalRequests.Add([ordered]@{op='eval';frontend='flow';syntaxVersion=2;structured=$true;code="customer::remaining-lifetime-allowance(store = $storeExpr, customer-id = CustomerId::new($(Get-FlowString $queryGuid)), cap = Money::new($([string]$oracleCase.capMinorUnits)))"})
            $helperIndex = $evalRequests.Count
            $evalRequests.Add([ordered]@{op='eval';frontend='flow';syntaxVersion=2;structured=$true;code="customer::paid-total(store = $storeExpr, customer-id = CustomerId::new($(Get-FlowString $queryGuid)))"})
        }
        $caseBindings.Add([ordered]@{case=$case;oracleCase=$oracleCase;paymentOverride=$paymentOverride;storeProjection=$storeProjection;expectedProjection=(Get-ExpectedStoreProjection $storeProjection);roundtripIndex=$roundtripIndex;directIndex=$directIndex;helperIndex=$helperIndex})
    }
    $evaluation = Invoke-FlowJsonl 'literal-roundtrip-and-direct-score' $workProject $evalRequests.ToArray() $cliFull
    Assert-FlowJsonlProcess $evaluation
    foreach ($binding in $caseBindings) {
        $case = $binding.case
        $roundtripResponse = if ($binding.roundtripIndex -lt $evaluation.responses.Count) { $evaluation.responses[$binding.roundtripIndex] } else { $null }
        $roundtripActual = $null
        $roundtripError = $null
        $roundtripPass = $false
        if ($null -ne $roundtripResponse -and [bool]$roundtripResponse.ok) {
            try {
                $roundtripActual = Get-StructuredValue $roundtripResponse 'Store'
                $roundtripPass = (Get-CanonicalJson $roundtripActual) -ceq (Get-CanonicalJson $binding.expectedProjection)
            } catch { $roundtripError = $_.Exception.Message }
        } elseif ($null -ne $roundtripResponse) { $roundtripError = [string]$roundtripResponse.text }
        $directActual = $null
        $directFull = $null
        $directPass = $false
        $errorPropagationPass = $true
        $directError = $null
        $directEffects = @()
        $helperActual = $null
        $helperFull = $null
        $helperPass = $false
        $helperError = $null
        if ($null -ne $binding.directIndex) {
            $directResponse = if ($binding.directIndex -lt $evaluation.responses.Count) { $evaluation.responses[$binding.directIndex] } else { $null }
            if ($null -ne $directResponse -and [bool]$directResponse.ok) {
                try {
                    $directFull = Get-StructuredResultFull $directResponse
                    $directActual = Get-StructuredResult $directResponse
                    $directPass = (Get-CanonicalJson $directActual) -ceq (Get-CanonicalJson $binding.oracleCase.expected)
                    $directEffects = @(Get-ResponseEffects $directResponse)
                    if ($directEffects.Count -gt 0) { $directPass = $false; $directError = 'candidate evaluation reported effects' }
                } catch { $directError = $_.Exception.Message }
            } elseif ($null -ne $directResponse) { $directError = [string]$directResponse.text }
            $helperResponse = if ($binding.helperIndex -lt $evaluation.responses.Count) { $evaluation.responses[$binding.helperIndex] } else { $null }
            if ($null -ne $helperResponse -and [bool]$helperResponse.ok) {
                try {
                    $helperFull = Get-StructuredResultFull $helperResponse
                    $helperActual = Get-StructuredResult $helperResponse
                    if ($null -ne $binding.paymentOverride) {
                        $expectedHelper = [ordered]@{ ok = [string]$binding.paymentOverride }
                    } else {
                        $referenceCase = @($script:referenceOracleObject.cases | Where-Object { [string]$_.id -ceq [string]$binding.oracleCase.fixtureCase })[0]
                        $expectedHelper = $referenceCase.expected.direct
                    }
                    $helperPass = (Get-CanonicalJson $helperActual) -ceq (Get-CanonicalJson $expectedHelper)
                    if (@(Get-ResponseEffects $helperResponse).Count -gt 0) { $helperPass = $false; $helperError = 'helper evaluation reported effects' }
                } catch { $helperError = $_.Exception.Message }
            } elseif ($null -ne $helperResponse) { $helperError = [string]$helperResponse.text }
            $referenceCase = @($script:referenceOracleObject.cases | Where-Object { [string]$_.id -ceq [string]$binding.oracleCase.fixtureCase })[0]
            $paidTotalFailed = $null -eq $binding.paymentOverride -and $referenceCase.expected.direct.Contains('error')
            if ($paidTotalFailed) {
                $errorPropagationPass = $null -ne $directFull -and $null -ne $helperFull -and (Get-CanonicalJson $directFull) -ceq (Get-CanonicalJson $helperFull)
                if (-not $errorPropagationPass) { $directError = 'Complete BusinessError payload differs from customer.paid-total.' }
            }
            $directPass = $directPass -and $errorPropagationPass
        }
        $script:caseResults.Add([ordered]@{
            id = [string]$binding.oracleCase.id
            referenceFixtureId = [string]$case.id
            querySymbol = [string]$binding.oracleCase.query
            queryGuid = Get-CanonicalCustomerId ([string]$binding.oracleCase.query)
            capMinorUnits = [string]$binding.oracleCase.capMinorUnits
            expected = $binding.oracleCase.expected
            actual = $directActual
            exactActual = $directFull
            directPassed = $directPass
            directError = $directError
            paidTotalErrorPropagatedExactly = $errorPropagationPass
            helperActual = $helperActual
            helperExact = $helperFull
            helperPassed = $helperPass
            helperError = $helperError
            literalRoundTripPassed = $roundtripPass
            literalRoundTripError = $roundtripError
            projectionTimestampNormalization = 'F# Z suffix mapped to Flow +00:00 and normalized back for exact projection comparison'
            storeBeforeAfter = [ordered]@{referenceProjectionUnchanged = $true; candidateFunctionDeclaredPure = $script:targetReady -and (@($script:target.effects).Count -eq 0); evaluationEffects = $directEffects; immutableStoreLiteralRoundTripPassed = $roundtripPass}
        })
        Add-Check "literal Store round-trip: $($case.id)" $roundtripPass ([string]$roundtripError)
        if ($null -ne $binding.directIndex) {
            Add-Check "remaining allowance matches oracle: $($binding.oracleCase.id)" $directPass ([string]$directError)
            Add-Check "reused paid-total matches reference: $($binding.oracleCase.id)" $helperPass ([string]$helperError)
            if ($paidTotalFailed) { Add-Check "complete paid-total BusinessError propagated unchanged: $($binding.oracleCase.id)" $errorPropagationPass 'Candidate and helper BusinessError record payloads must match exactly.' }
        }
    }
    if (-not $script:targetReady) {
        Add-Check 'missing or invalid candidate is gated before direct scoring' ($script:directCallsScheduled -eq 0) "gate=$($script:candidateGate); directCallsScheduled=$($script:directCallsScheduled)"
    }
    $workHashesAfter = @(Get-Inventory $workProject -ExcludeBuildOutput)
    Add-Check 'isolated project copy unchanged by evaluation' ((Get-CanonicalJson $workHashesBefore) -ceq (Get-CanonicalJson $workHashesAfter)) 'Source inventory before and after test-all/evaluation.'
    $script:inputHashesAfter = Get-InputHashes $projectFull $runtimeDirectory $fixtureFull $referenceOracleFull $oracleFull $metadataFull
    Write-JsonFile (Join-Path $script:runDirectory 'input-hashes-after.json') $script:inputHashesAfter
    Add-Check 'candidate source, runtime, fixture, oracle and metadata hashes unchanged' ((Get-CanonicalJson $script:inputHashesBefore) -ceq (Get-CanonicalJson $script:inputHashesAfter)) 'The candidate was evaluated only from its isolated copy.'
}
catch {
    $script:failure = $_.Exception.ToString()
    Add-Check 'scorer completed without setup or evaluation exception' $false $script:failure
    if ($null -ne $script:inputHashesBefore) {
        try {
            $script:inputHashesAfter = Get-InputHashes $projectFull $runtimeDirectory $fixtureFull $referenceOracleFull $oracleFull $metadataFull
            Write-JsonFile (Join-Path $script:runDirectory 'input-hashes-after.json') $script:inputHashesAfter
        } catch { }
    }
}

$caseArray = $script:caseResults.ToArray()
$oracleCaseCount = if ($null -ne $script:oracleObject) { @($script:oracleObject.cases).Count } else { 0 }
$behaviorPassed = $script:targetReady -and $caseArray.Count -eq $oracleCaseCount -and @($caseArray | Where-Object { -not $_.directPassed -or -not $_.helperPassed -or -not $_.literalRoundTripPassed }).Count -eq 0
$checksPassed = @($script:checks | Where-Object { $_.passed -ne $true }).Count -eq 0
$inputsStable = $null -ne $script:inputHashesBefore -and $null -ne $script:inputHashesAfter -and ((Get-CanonicalJson $script:inputHashesBefore) -ceq (Get-CanonicalJson $script:inputHashesAfter))
$score = [ordered]@{
    schemaVersion = 1
    status = if ($null -ne $script:failure) { 'scorer-error' } elseif ($behaviorPassed -and $inputsStable -and $checksPassed) { 'passed' } else { 'rejected' }
    behavioralScore = [ordered]@{ passed = $behaviorPassed; caseCount = $caseArray.Count; passedCases = @($caseArray | Where-Object { $_.directPassed }).Count; candidateGate = $script:candidateGate; directCallsScheduled = $script:directCallsScheduled; target = $script:target }
    candidateTestAll = $script:candidateTests
    checksPassed = $checksPassed
    maturity = if ($null -ne $script:target) { [string]$script:target.maturity } else { 'missing-function' }
    storePreservation = 'Each complete Store projection is reconstructed and typed-round-tripped before scoring. The candidate receives an immutable Store and must declare no effects; the direct call returns only Result<Money,BusinessError>, so before/after equality follows from the pure Flow value contract.'
    caseResults = $caseArray
    checks = $script:checks.ToArray()
    processes = $script:processes.ToArray()
    inputHashesBefore = $script:inputHashesBefore
    inputHashesAfter = $script:inputHashesAfter
    inputHashesStable = $inputsStable
    attemptDirectory = $script:runDirectory
    failure = $script:failure
}
Write-JsonFile $script:outputFull $score
Write-JsonFile (Join-Path $script:runDirectory 'score.json') $score
if ([string]$score.status -ceq 'scorer-error') { exit 1 }
if ([string]$score.status -cne 'passed') { exit 2 }
