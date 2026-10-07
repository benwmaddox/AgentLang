#requires -Version 7.0
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scorer = Join-Path $PSScriptRoot 'Score-RetentionOutputSupplement.ps1'
$pwsh = (Get-Command pwsh -ErrorAction Stop).Source
$output = @(& $pwsh -NoLogo -NoProfile -File $scorer -SelfTest 2>&1)
$exitCode = $LASTEXITCODE
if ($exitCode -ne 0) { throw "Supplemental scorer self-test exited $exitCode. Output: $($output -join [Environment]::NewLine)" }
$jsonLines = @($output | ForEach-Object { [string]$_ } | Where-Object { $_.TrimStart().StartsWith('{') })
if ($jsonLines.Count -ne 1) { throw "Expected one self-test JSON result, received $($jsonLines.Count) lines." }
$result = ConvertFrom-Json -InputObject $jsonLines[0] -AsHashtable -Depth 50
if ($result.passed -ne $true -or [int]$result.actualCliCalls -ne 0) { throw 'Self-test result was not passing or unexpectedly invoked the CLI.' }

$required = @(
    'user-word inventory helper returns flat rows with exact cardinality',
    'canonical runtime inventory sorts ordered-dictionary rows by pinned runtime and path',
    'actual S07 structured-result comparison accepts correct value and rejects wrong rounding',
    'S07 structured-result comparison rejects missing protocol collections',
    'S07 structured-result comparison rejects the wrong nominal Money scalar',
    'BigInteger discount arithmetic and raw-kind ordinal behavior',
    'correct R03 predecessor binds before the guarded runtime call'
)
foreach ($name in $required) {
    if (@($result.tests | Where-Object { [string]$_.name -ceq $name -and $_.passed -eq $true }).Count -ne 1) { throw "Required supplemental control is missing or failed: $name" }
}

$negative = @($result.tests | Where-Object { [string]$_.name -like 'reject *' })
if ($negative.Count -lt 15) { throw "Expected at least 15 negative binding controls; found $($negative.Count)." }
foreach ($test in $negative) {
    if ($test.passed -ne $true -or [int]$test.actualCliCalls -ne 0 -or [int]$test.guardedActionCallsBefore -ne 0 -or [int]$test.guardedActionCallsAfter -ne 0) {
        throw "Negative control did not reject before any guarded/CLI call: $($test.name)"
    }
}

Write-Output "PASS: $($result.tests.Count) focused supplemental controls; $($negative.Count) rejected before runtime; 0 CLI calls."
