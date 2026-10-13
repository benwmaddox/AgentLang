#requires -Version 7.0
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$nativeRoot = Join-Path $repositoryRoot 'applications/campfire/Native/http'
$helperSource = Join-Path $nativeRoot 'cookie_expiry.c'
$headerSource = Join-Path $nativeRoot 'cookie_expiry.h'
$testSource = Join-Path $repositoryRoot 'tests/native/campfire_cookie_expiry_test.c'
$scriptPath = $PSCommandPath
$runParent = Join-Path $repositoryRoot '.agentlang/campfire-native-http-253/cookie-expiry-validation'
$runDirectory = Join-Path $runParent ([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N'))
$oracleFile = Join-Path $runDirectory 'oracle-gregorian-2000-2399.tsv'
$sourcePaths = @($helperSource, $headerSource, $testSource, $scriptPath)
$expectedHelperSha256 = '137a33d4e27f26135a85455c93da9002f66de3385d7332a9557a329f0eb0216c'
$expectedHeaderSha256 = '11ba70dde36397ee1df1187c08a0fc97048c7a6c96cc8d3c1c71860f7f4e68a1'
$calendarOracleCases = 9600

function Get-SourceHashes([string[]] $Paths, [string] $Root) {
    foreach ($path in $Paths) {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($Root, $path).Replace('\', '/')
            sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
}

function Add-OracleRow([System.Text.StringBuilder] $Builder, [DateTimeOffset] $Instant) {
    $unixMicroseconds = [Int64](($Instant.UtcDateTime.Ticks - [DateTime]::UnixEpoch.Ticks) / [Int64]10)
    $expiry = $Instant.AddYears(20).UtcDateTime.ToString('R', [Globalization.CultureInfo]::InvariantCulture)
    $null = $Builder.Append($unixMicroseconds.ToString([Globalization.CultureInfo]::InvariantCulture))
    $null = $Builder.Append('|').Append($expiry).Append("`n")
}

function Write-GregorianCycleOracle([string] $Path) {
    $builder = [System.Text.StringBuilder]::new(600000)
    $count = 0
    for ($year = 2000; $year -lt 2400; $year++) {
        for ($month = 1; $month -le 12; $month++) {
            $serialMonth = ($year - 2000) * 12 + ($month - 1)
            if (($serialMonth % 12) -eq 0) {
                $hour = 0; $minute = 0; $second = 0; $microsecond = 0
            }
            else {
                $hour = ($serialMonth * 7) % 24
                $minute = ($serialMonth * 13) % 60
                $second = ($serialMonth * 17) % 60
                $microsecond = ($serialMonth * 7919) % 1000000
            }
            $first = [DateTimeOffset]::new($year, $month, 1, $hour, $minute, $second, [TimeSpan]::Zero)
            $first = $first.AddTicks([Int64]$microsecond * [Int64]10)
            Add-OracleRow $builder $first
            $count++

            $lastDay = [DateTime]::DaysInMonth($year, $month)
            $last = [DateTimeOffset]::new($year, $month, $lastDay, 23, 59, 59, [TimeSpan]::Zero).AddTicks([Int64]9999990)
            Add-OracleRow $builder $last
            $count++
        }
    }
    if ($count -ne $calendarOracleCases) { throw "Expected $calendarOracleCases Gregorian oracle rows; generated $count." }
    [IO.File]::WriteAllText($Path, $builder.ToString(), [Text.Encoding]::ASCII)
    return $count
}

$clang = $env:AGENTLANG_LLVM_CLANG
if ([string]::IsNullOrWhiteSpace($clang)) {
    $clang = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\Llvm\x64\bin\clang.exe'
}
if (-not (Test-Path -LiteralPath $clang -PathType Leaf)) {
    $command = Get-Command clang -ErrorAction SilentlyContinue
    if ($null -eq $command) { throw "Clang was not found at '$clang' or on PATH." }
    $clang = $command.Source
}

$originalTemp = $env:TEMP
$originalTmp = $env:TMP
$originalTmpDir = $env:TMPDIR
$receiptJson = $null
try {
    $null = New-Item -ItemType Directory -Path $runDirectory -Force
    $env:TEMP = $runDirectory
    $env:TMP = $runDirectory
    $env:TMPDIR = $runDirectory

    $actualHelperSha256 = (Get-FileHash -LiteralPath $helperSource -Algorithm SHA256).Hash.ToLowerInvariant()
    $actualHeaderSha256 = (Get-FileHash -LiteralPath $headerSource -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHelperSha256 -cne $expectedHelperSha256 -or $actualHeaderSha256 -cne $expectedHeaderSha256) {
        throw 'Cookie-expiry helper source changed from the frozen implementation under calendar regression.'
    }
    $sourceHashesBefore = @(Get-SourceHashes $sourcePaths $repositoryRoot)
    $generatedOracleCases = Write-GregorianCycleOracle $oracleFile
    $oracleSha256 = (Get-FileHash -LiteralPath $oracleFile -Algorithm SHA256).Hash.ToLowerInvariant()
    $runs = [Collections.Generic.List[object]]::new()
    foreach ($optimization in @('O0', 'O2')) {
        $executable = Join-Path $runDirectory "campfire-cookie-expiry-$optimization.exe"
        $buildLog = Join-Path $runDirectory "build-$optimization.log"
        $runLog = Join-Path $runDirectory "run-$optimization.log"
        $arguments = @(
            '--target=x86_64-pc-windows-msvc',
            '-std=c11',
            '-Wall',
            '-Wextra',
            '-Werror',
            "-$optimization",
            $testSource,
            $helperSource,
            '-o',
            $executable
        )
        $buildOutput = & $clang @arguments 2>&1 | Out-String
        $buildExitCode = $LASTEXITCODE
        Set-Content -LiteralPath $buildLog -Value $buildOutput -NoNewline
        if ($buildExitCode -ne 0) {
            throw "Clang $optimization build failed with exit code $buildExitCode. See $buildLog`n$buildOutput"
        }

        $runOutput = & $executable '--batch-file' $oracleFile 2>&1 | Out-String
        $runExitCode = $LASTEXITCODE
        Set-Content -LiteralPath $runLog -Value $runOutput -NoNewline
        if ($runExitCode -ne 0) {
            throw "Cookie-expiry native fixture $optimization failed with exit code $runExitCode. See $runLog`n$runOutput"
        }
        if ($runOutput -notmatch 'BATCH\|cases=9600' -or
            $runOutput -notmatch 'campfire_cookie_expiry_v1=passed valid_cases=11 rejection_cases=5 short_capacity_cases=29 batch_cases=9600') {
            throw "Cookie-expiry native fixture $optimization omitted its expected receipt. See $runLog`n$runOutput"
        }

        $oracleCases = 0
        foreach ($line in ($runOutput -split "`r?`n" | Where-Object { $_.StartsWith('CASE|', [StringComparison]::Ordinal) })) {
            $parts = $line.Split('|')
            if ($parts.Length -ne 4) { throw "Malformed native oracle row in $optimization output." }
            $microseconds = [Int64]::Parse($parts[2], [Globalization.CultureInfo]::InvariantCulture)
            $expected = [DateTimeOffset]::UnixEpoch.AddTicks($microseconds * [Int64]10).AddYears(20).UtcDateTime.ToString('R', [Globalization.CultureInfo]::InvariantCulture)
            if (-not [StringComparer]::Ordinal.Equals($parts[3], $expected)) {
                throw "Native/.NET expiry mismatch for '$($parts[1])' at optimization $optimization. Expected '$expected'; got '$($parts[3])'."
            }
            $oracleCases++
        }
        if ($oracleCases -ne 11) { throw "Expected 11 .NET oracle comparisons in $optimization; found $oracleCases." }

        $runs.Add([ordered]@{
            optimization = $optimization
            executableSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
            buildLog = [IO.Path]::GetRelativePath($repositoryRoot, $buildLog).Replace('\', '/')
            runLog = [IO.Path]::GetRelativePath($repositoryRoot, $runLog).Replace('\', '/')
            dotnetOracleCases = $oracleCases
            calendarOracleCases = $generatedOracleCases
            calendarOracleSha256 = $oracleSha256
            nativeReceipt = ($runOutput -split "`r?`n" | Where-Object { $_.StartsWith('campfire_cookie_expiry_v1=', [StringComparison]::Ordinal) } | Select-Object -Last 1)
        })
    }

    $sourceHashesAfter = @(Get-SourceHashes $sourcePaths $repositoryRoot)
    if ((ConvertTo-Json -InputObject $sourceHashesBefore -Depth 5 -Compress) -cne
        (ConvertTo-Json -InputObject $sourceHashesAfter -Depth 5 -Compress)) {
        throw 'Cookie-expiry implementation, fixture, or verifier source changed during validation.'
    }
    $receipt = [ordered]@{
        suite = 'campfire_cookie_expiry_v1'
        status = 'pass'
        oracle = '.NET DateTimeOffset.AddYears(20).UtcDateTime.ToString("R", InvariantCulture)'
        calendarCoverage = 'first and last day of every month from 2000 through 2399; first-day times vary and last-day cases use 23:59:59.999999'
        calendarOracleCases = $generatedOracleCases
        calendarOracleSha256 = $oracleSha256
        calendarOracleFile = [IO.Path]::GetRelativePath($repositoryRoot, $oracleFile).Replace('\', '/')
        clang = [IO.Path]::GetFullPath($clang)
        clangSha256 = (Get-FileHash -LiteralPath $clang -Algorithm SHA256).Hash.ToLowerInvariant()
        runDirectory = [IO.Path]::GetRelativePath($repositoryRoot, $runDirectory).Replace('\', '/')
        sourceHashesBefore = @($sourceHashesBefore)
        sourceHashesAfter = @($sourceHashesAfter)
        runs = @($runs)
    }
    $receiptJson = $receipt | ConvertTo-Json -Depth 10
    Set-Content -LiteralPath (Join-Path $runDirectory 'receipt.json') -Value $receiptJson -Encoding utf8
}
finally {
    if ($null -eq $originalTemp) { Remove-Item Env:TEMP -ErrorAction SilentlyContinue }
    else { $env:TEMP = $originalTemp }
    if ($null -eq $originalTmp) { Remove-Item Env:TMP -ErrorAction SilentlyContinue }
    else { $env:TMP = $originalTmp }
    if ($null -eq $originalTmpDir) { Remove-Item Env:TMPDIR -ErrorAction SilentlyContinue }
    else { $env:TMPDIR = $originalTmpDir }
}

Write-Output $receiptJson
