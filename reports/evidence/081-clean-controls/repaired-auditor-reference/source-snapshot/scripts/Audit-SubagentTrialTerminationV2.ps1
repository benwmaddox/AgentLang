#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$TracePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$trace = if ([IO.Path]::IsPathRooted($TracePath)) {
    [IO.Path]::GetFullPath($TracePath)
} else {
    [IO.Path]::GetFullPath((Join-Path $repo $TracePath))
}

function Get-Property([object]$Value, [string]$Name) {
    if ($null -eq $Value) { return $null }
    if ($Value -is [Collections.IDictionary]) {
        if ($Value.Contains($Name)) { return $Value[$Name] }
        return $null
    }
    $property = $Value.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Get-Sha256([byte[]]$Bytes) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
}

function Assert-ValidJson([string]$Json, [string]$Label) {
    $document = [Text.Json.JsonDocument]::Parse($Json)
    try { [void](Convert-JsonElement $document.RootElement) }
    finally { $document.Dispose() }
}

function Assert-CompleteWire([object]$Record, [string]$Label, [string]$RawField, [string]$WireBase64Field,
    [string]$PayloadBytesField, [string]$WireBytesField, [string]$Sha256Field) {
    $raw = Get-Property $Record $RawField
    Require ($raw -is [string]) "$Label is missing its raw payload."
    $payloadBytes = $utf8.GetBytes($raw)
    Require ($raw.IndexOf("`n", [StringComparison]::Ordinal) -lt 0) "$Label raw payload includes an embedded LF."
    Assert-ValidJson $raw "$Label raw payload"
    $wireBytes = [Convert]::FromBase64String([string](Get-Property $Record $WireBase64Field))
    $expectedWire = [byte[]]::new($payloadBytes.Length + 1)
    [Array]::Copy($payloadBytes, $expectedWire, $payloadBytes.Length)
    $expectedWire[$expectedWire.Length - 1] = 10
    Require ($wireBytes.Length -eq $expectedWire.Length) "$Label wire length does not match its raw payload plus LF."
    for ($byteIndex = 0; $byteIndex -lt $wireBytes.Length; $byteIndex++) {
        Require ($wireBytes[$byteIndex] -eq $expectedWire[$byteIndex]) "$Label wire bytes do not preserve the exact raw payload and LF."
    }
    Require ([long](Get-Property $Record $PayloadBytesField) -eq $payloadBytes.Length) "$Label payload byte count is inconsistent."
    Require ([long](Get-Property $Record $WireBytesField) -eq $wireBytes.Length) "$Label wire byte count is inconsistent."
    Require ((Get-Property $Record $Sha256Field) -ceq (Get-Sha256 $wireBytes)) "$Label wire SHA-256 is inconsistent."
}

function Assert-CompleteObservedRuntime([object]$Record, [string]$Label) {
    Require ((Get-Property $Record 'complete') -eq $true) "$Label is incomplete."
    Require ((Get-Property $Record 'validJson') -eq $true) "$Label is not valid JSON."
    $payloadBytes = [Convert]::FromBase64String([string](Get-Property $Record 'base64'))
    $payloadText = $utf8.GetString($payloadBytes)
    Assert-ValidJson $payloadText "$Label payload"
    Require ([long](Get-Property $Record 'utf8Bytes') -eq $payloadBytes.Length) "$Label payload byte count is inconsistent."
    Require ((Get-Property $Record 'sha256') -ceq (Get-Sha256 $payloadBytes)) "$Label payload SHA-256 is inconsistent."
    Require (-not [string]::IsNullOrWhiteSpace([string](Get-Property $Record 'canonical'))) "$Label is missing its canonical JSON."
    $wireBytes = [Convert]::FromBase64String([string](Get-Property $Record 'wireBase64'))
    $expectedWire = [byte[]]::new($payloadBytes.Length + 1)
    [Array]::Copy($payloadBytes, $expectedWire, $payloadBytes.Length)
    $expectedWire[$expectedWire.Length - 1] = 10
    Require ($wireBytes.Length -eq $expectedWire.Length) "$Label wire length does not match its payload plus LF."
    for ($byteIndex = 0; $byteIndex -lt $wireBytes.Length; $byteIndex++) {
        Require ($wireBytes[$byteIndex] -eq $expectedWire[$byteIndex]) "$Label wire bytes do not preserve the exact payload and LF."
    }
    Require ([long](Get-Property $Record 'wireUtf8Bytes') -eq $wireBytes.Length) "$Label wire byte count is inconsistent."
    Require ((Get-Property $Record 'wireSha256') -ceq (Get-Sha256 $wireBytes)) "$Label wire SHA-256 is inconsistent."
}

function Convert-JsonElement([Text.Json.JsonElement]$Element) {
    switch ($Element.ValueKind) {
        ([Text.Json.JsonValueKind]::Object) {
            $object = [ordered]@{}
            $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($property in $Element.EnumerateObject()) {
                if (-not $names.Add($property.Name)) { throw "Duplicate JSON property '$($property.Name)' in trace event." }
                $object[$property.Name] = Convert-JsonElement $property.Value
            }
            return ,$object
        }
        ([Text.Json.JsonValueKind]::Array) {
            $items = [Collections.Generic.List[object]]::new()
            foreach ($item in $Element.EnumerateArray()) { $items.Add((Convert-JsonElement $item)) }
            return ,$items.ToArray()
        }
        ([Text.Json.JsonValueKind]::String) { return $Element.GetString() }
        ([Text.Json.JsonValueKind]::Number) {
            $number64 = 0L
            if ($Element.TryGetInt64([ref]$number64)) { return $number64 }
            return $Element.GetDouble()
        }
        ([Text.Json.JsonValueKind]::True) { return $true }
        ([Text.Json.JsonValueKind]::False) { return $false }
        ([Text.Json.JsonValueKind]::Null) { return $null }
        default { throw "Unsupported JSON value kind $($Element.ValueKind)." }
    }
}

try {
    Require (Test-Path -LiteralPath $trace -PathType Leaf) "Trace does not exist: $trace"
    $utf8 = [Text.UTF8Encoding]::new($false, $true)
    $traceBytes = [IO.File]::ReadAllBytes($trace)
    $traceText = $utf8.GetString($traceBytes)
    Require ($traceText.EndsWith("`n", [StringComparison]::Ordinal)) 'Trace must end with an LF-delimited JSONL event.'
    $lines = $traceText.Split("`n")
    Require ($lines.Count -ge 4) 'A completed v2 trace needs session-start, host-close, and session-end events.'

    $events = [Collections.Generic.List[object]]::new()
    for ($lineIndex = 0; $lineIndex -lt ($lines.Count - 1); $lineIndex++) {
        $line = $lines[$lineIndex]
        Require (-not [string]::IsNullOrWhiteSpace($line)) 'Trace contains an empty event line.'
        Require (-not $line.EndsWith("`r", [StringComparison]::Ordinal)) 'Trace must use LF line endings without CR.'
        $document = [Text.Json.JsonDocument]::Parse($line)
        try { $events.Add((Convert-JsonElement $document.RootElement)) }
        finally { $document.Dispose() }
    }

    $starts = @($events | Where-Object { $_.event -ceq 'session-start' })
    $closes = @($events | Where-Object { $_.event -ceq 'host-close' })
    $ends = @($events | Where-Object { $_.event -ceq 'session-end' })
    Require ($starts.Count -eq 1) "Expected exactly one session-start event; found $($starts.Count)."
    Require ($closes.Count -eq 1) "Expected exactly one host-close event; found $($closes.Count)."
    Require ($ends.Count -eq 1) "Expected exactly one session-end event; found $($ends.Count)."
    Require ($events[0].event -ceq 'session-start') 'session-start must be the first event.'
    Require ($events[-1].event -ceq 'session-end') 'session-end must be the final event.'
    Require ($events[-2].event -ceq 'host-close') 'host-close must be the final transport event before session-end.'

    $start = $starts[0]
    $close = $closes[0]
    $end = $ends[0]
    Require ((Get-Property $start 'hostProtocolVersion') -ceq 'subagent-trial-host-v2') 'session-start does not carry the v2 host protocol marker.'
    $controls = @(Get-Property $start 'transportControls')
    Require ($controls -ccontains 'host.close') 'session-start does not declare host.close as a transport control.'
    Require (@(Get-Property $start 'allowedOperations') -cnotcontains 'host.close') 'session-start exposes host.close as a runtime operation.'

    $raw = Get-Property $close 'requestRaw'
    $canonical = Get-Property $close 'requestCanonical'
    Require ($canonical -is [string] -and $canonical -ceq '{"op":"host.close"}') 'host-close requestCanonical is not the exact one-field close frame.'
    Require ($raw -is [string] -and $raw.Length -gt 0) 'host-close requestRaw is missing or empty.'
    $rawUtf8 = $utf8.GetBytes($raw)
    Require ($raw.IndexOf("`n", [StringComparison]::Ordinal) -lt 0) 'host-close requestRaw contains an LF before the JSONL frame terminator.'
    $requestDocument = [Text.Json.JsonDocument]::Parse($raw)
    try {
        $rootElement = $requestDocument.RootElement
        Require ($rootElement.ValueKind -eq [Text.Json.JsonValueKind]::Object) 'host-close requestRaw is not a JSON object.'
        $propertyNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $propertyCount = 0
        $validOperation = $false
        foreach ($property in $rootElement.EnumerateObject()) {
            $propertyCount++
            Require ($propertyNames.Add($property.Name)) "host-close requestRaw has duplicate property '$($property.Name)'."
            Require ($property.Name -ceq 'op') "host-close requestRaw has unexpected property '$($property.Name)'."
            $validOperation = ($property.Value.ValueKind -eq [Text.Json.JsonValueKind]::String -and $property.Value.GetString() -ceq 'host.close')
        }
        Require ($propertyCount -eq 1 -and $validOperation) 'host-close requestRaw must contain exactly one string property: op=host.close.'
    }
    finally { $requestDocument.Dispose() }

    $wireBase64 = Get-Property $close 'requestWireBase64'
    Require ($wireBase64 -is [string]) 'host-close event is missing requestWireBase64.'
    try { $wireBytes = [Convert]::FromBase64String($wireBase64) }
    catch { throw 'host-close requestWireBase64 is invalid base64.' }
    $wireText = $utf8.GetString($wireBytes)
    Require ($wireText -ceq ($raw + "`n")) 'host-close wire bytes do not preserve the exact UTF-8 frame and LF.'
    $wireLength = Get-Property $close 'requestWireUtf8Bytes'
    Require ($wireLength -is [ValueType] -and [long]$wireLength -eq $wireBytes.Length) 'host-close requestWireUtf8Bytes does not match the preserved wire bytes.'
    $wireHash = Get-Property $close 'requestWireSha256'
    Require ($wireHash -is [string] -and $wireHash -ceq (Get-Sha256 $wireBytes)) 'host-close requestWireSha256 does not match the preserved wire bytes.'

    $limits = Get-Property $start 'limits'
    $maxRequestBytes = Get-Property $limits 'maxRequestBytes'
    $maxExchanges = Get-Property $limits 'maxExchanges'
    Require ($null -ne $maxRequestBytes -and [long]$maxRequestBytes -ge 1 -and [long]$maxRequestBytes -le 524288) 'session-start is missing a valid maxRequestBytes limit or exceeds the host cap.'
    Require ($wireBytes.Length - 1 -le [long]$maxRequestBytes) 'Preserved host-close payload exceeds the configured request limit.'
    Require ($null -ne $maxExchanges -and [long]$maxExchanges -ge 1 -and [long]$maxExchanges -le 100) 'session-start is missing a valid maxExchanges limit or exceeds the host cap.'

    Require (@($events | Where-Object { $_.event -ceq 'host-cancelled' }).Count -eq 0) 'Trace contains a host cancellation event.'
    $exchangeEvents = @($events | Where-Object { $_.event -ceq 'exchange' })
    $closePosition = -1
    for ($index = 0; $index -lt $events.Count; $index++) {
        if ($events[$index].event -ceq 'host-close') { $closePosition = $index; break }
    }
    $beforeClose = [Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt $closePosition; $index++) {
        if ($events[$index].event -ceq 'exchange') { $beforeClose.Add($events[$index]) }
    }
    Require ($beforeClose.Count -eq $exchangeEvents.Count) 'An exchange event appears after the host-close control.'
    Require ($exchangeEvents.Count -le [long]$maxExchanges) 'Broker exchange count exceeds the configured maximum.'
    for ($index = 0; $index -lt $exchangeEvents.Count; $index++) {
        Require ([long]$exchangeEvents[$index].index -eq ($index + 1)) "Exchange ordinals are not contiguous at position $($index + 1)."
        $exchange = $exchangeEvents[$index]
        $delivery = Get-Property $exchange 'requestDelivery'
        $executionState = Get-Property $exchange 'executionState'
        $outcome = Get-Property $exchange 'outcome'
        $response = Get-Property $exchange 'response'
        $observed = Get-Property $exchange 'observedRuntimeResponse'
        if ($executionState -ceq 'response-observed') {
            Require ((Get-Property $delivery 'state') -ceq 'confirmed' -and (Get-Property $delivery 'completeLineConfirmed') -eq $true) "Exchange $($index + 1) lacks confirmed complete request delivery."
            if ($null -ne $observed) {
                Assert-CompleteObservedRuntime $observed "Exchange $($index + 1) observed runtime response"
            } elseif ((Get-Property $response 'source') -ceq 'runtime') {
                Assert-CompleteWire $response "Exchange $($index + 1) runtime response" 'rawLine' 'wireBase64' 'payloadUtf8Bytes' 'wireUtf8Bytes' 'sha256'
                Require ($null -eq (Get-Property $response 'parseError')) "Exchange $($index + 1) runtime response has a parse error."
            } else {
                Require ($outcome -ceq 'inspection-budget-exceeded' -and (Get-Property $exchange 'errorCode') -ceq 'TRIAL_INSPECTION_BUDGET_EXCEEDED') "Exchange $($index + 1) has no complete runtime response evidence."
            }
        } elseif ($executionState -ceq 'not-executed') {
            Require (@('malformed','denied','inspection-budget-exhausted') -ccontains $outcome) "Exchange $($index + 1) is not executed without a conclusive host rejection."
            Require ((Get-Property $delivery 'state') -ceq 'not-sent' -and (Get-Property $delivery 'completeLineConfirmed') -eq $false) "Exchange $($index + 1) host rejection does not prove the request was withheld."
            Require ((Get-Property $response 'source') -ceq 'host' -and -not [string]::IsNullOrWhiteSpace([string](Get-Property $exchange 'errorCode'))) "Exchange $($index + 1) host rejection is missing its diagnostic."
            Require ($null -eq $observed) "Exchange $($index + 1) has runtime response bytes despite a not-executed state."
        } else {
            throw "Exchange $($index + 1) has nonterminal execution state '$executionState'."
        }
    }
    $closeCount = Get-Property $close 'exchangeCount'
    $endCount = Get-Property $end 'exchangeCount'
    Require ($null -ne $closeCount -and [long]$closeCount -eq $exchangeEvents.Count) 'host-close exchangeCount does not equal the number of broker exchanges.'
    Require ($null -ne $endCount -and [long]$endCount -eq $exchangeEvents.Count) 'session-end exchangeCount does not equal the number of broker exchanges.'

    Require ((Get-Property $end 'terminationKind') -ceq 'host-close') 'Trace termination was not an explicit host.close; input EOF is not audited actor completion.'
    $hostExitCode = Get-Property $end 'hostExitCode'
    $runtimeExitCode = Get-Property $end 'runtimeExitCode'
    Require ($null -ne $hostExitCode -and [int]$hostExitCode -eq 0) 'The host did not exit successfully after host.close.'
    Require ($null -ne $runtimeExitCode -and [int]$runtimeExitCode -eq 0) 'The runtime child did not exit successfully after host.close.'

    Write-Output ("Termination audit passed: {0} broker exchanges, close wire {1} bytes, host/runtime exits 0/0." -f $exchangeEvents.Count, $wireBytes.Length)
    exit 0
}
catch {
    [Console]::Error.WriteLine(('Termination audit failed: ' + $_.Exception.Message))
    exit 1
}
