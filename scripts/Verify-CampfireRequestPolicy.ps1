#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$CliDll,
    [ValidateRange(10, 600)]
    [int]$TimeoutSeconds = 120
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$utf8NoBom = [Text.UTF8Encoding]::new($false)
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$sourcePath = Join-Path $repo 'applications/campfire/request-policy.flow'
$evidenceRoot = Join-Path $repo '.agentlang/campfire-host-236/request-policy'
$sealedRuntimeRoot = Join-Path $repo '.agentlang/campfire-host-236/pure-policy-runtime'
$pinsPath = Join-Path $repo '.agentlang/campfire-host-236/pure-policy-runtime-pins.json'
$defaultCliDll = Join-Path $repo 'src/AgentLang.Cli/bin/Release/net9.0/AgentLang.Cli.dll'
if ([string]::IsNullOrWhiteSpace($CliDll)) {
    $CliDll = $defaultCliDll
}
$cliDllPath = (Resolve-Path -LiteralPath $CliDll).Path
$sealedCliDllPath = Join-Path $sealedRuntimeRoot 'AgentLang.Cli.dll'
$verifyPinnedRuntime = (Test-Path -LiteralPath $pinsPath -PathType Leaf) -and [string]::Equals($cliDllPath, $sealedCliDllPath, [StringComparison]::OrdinalIgnoreCase)
$runtimeFileNames = @(
    'AgentLang.Cli.deps.json',
    'AgentLang.Cli.dll',
    'AgentLang.Cli.runtimeconfig.json',
    'AgentLang.Core.dll',
    'FSharp.Core.dll'
)

function ConvertTo-RequestLine($Request) {
    ConvertTo-Json -InputObject $Request -Depth 20 -Compress
}

function Invoke-CliSession {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][System.Collections.IDictionary[]]$Requests,
        [Parameter(Mandatory)][string]$ProjectPath,
        [Parameter(Mandatory)][string]$RunPath,
        [Parameter(Mandatory)][string]$CliPath
    )

    $lines = @($Requests | ForEach-Object { ConvertTo-RequestLine $_ })
    $inputText = [string]::Join("`n", $lines) + "`n"
    if ([Text.Encoding]::UTF8.GetByteCount($inputText) -gt 1MB) {
        throw "The '$Name' JSONL input exceeds 1 MiB."
    }

    $inputPath = Join-Path $RunPath "$Name.stdin.jsonl"
    $stdoutPath = Join-Path $RunPath "$Name.stdout.jsonl"
    $stderrPath = Join-Path $RunPath "$Name.stderr.txt"
    [IO.File]::WriteAllText($inputPath, $inputText, $utf8NoBom)

    $processTemp = Join-Path $RunPath 'process-temp'
    [IO.Directory]::CreateDirectory($processTemp) | Out-Null
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'dotnet'
    $startInfo.WorkingDirectory = $repo
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Environment['TMP'] = $processTemp
    $startInfo.Environment['TEMP'] = $processTemp
    $startInfo.Environment['TMPDIR'] = $processTemp
    foreach ($argument in @($CliPath, '--project', $ProjectPath, '--filesystem', 'virtual', '--jsonl')) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Could not start the copied CLI for '$Name'."
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.Write($inputText)
        $process.StandardInput.Close()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            $process.WaitForExit()
            $timedOutStdout = $stdoutTask.GetAwaiter().GetResult()
            $timedOutStderr = $stderrTask.GetAwaiter().GetResult()
            [IO.File]::WriteAllText($stdoutPath, $timedOutStdout, $utf8NoBom)
            [IO.File]::WriteAllText($stderrPath, $timedOutStderr, $utf8NoBom)
            throw "CLI session '$Name' exceeded $TimeoutSeconds seconds; see '$stdoutPath' and '$stderrPath'."
        }

        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        [IO.File]::WriteAllText($stdoutPath, $stdout, $utf8NoBom)
        [IO.File]::WriteAllText($stderrPath, $stderr, $utf8NoBom)
        if ([Text.Encoding]::UTF8.GetByteCount($stdout) -gt 4MB -or [Text.Encoding]::UTF8.GetByteCount($stderr) -gt 4MB) {
            throw "CLI session '$Name' exceeded the 4 MiB output limit."
        }
        if ($process.ExitCode -ne 0) {
            throw "CLI session '$Name' exited with code $($process.ExitCode); see '$stderrPath'."
        }

        $responseLines = @($stdout -split "`r?`n" | Where-Object { $_.Length -gt 0 })
        if ($responseLines.Count -ne $Requests.Count) {
            throw "CLI session '$Name' returned $($responseLines.Count) response lines for $($Requests.Count) requests; see '$stdoutPath'."
        }
        $responses = @($responseLines | ForEach-Object { ConvertFrom-Json -InputObject $_ -Depth 60 })
        return [pscustomobject]@{
            name = $Name
            requestCount = $Requests.Count
            responseCount = $responses.Count
            stdinPath = $inputPath
            stdoutPath = $stdoutPath
            stderrPath = $stderrPath
            responses = $responses
        }
    }
    finally {
        $process.Dispose()
    }
}

function Assert-OkResponse($Response, [string]$Label) {
    if ($null -eq $Response -or $Response.ok -ne $true) {
        $json = if ($null -eq $Response) { '<missing response>' } else { ConvertTo-Json -InputObject $Response -Depth 12 -Compress }
        throw "$Label failed: $json"
    }
}

function Assert-LibraryDescription($Response, [string]$Word, [int]$ExpectedTestCount) {
    Assert-OkResponse $Response "fresh-process describe '$Word'"
    $description = $Response.data
    if ([string]$description.name -cne $Word -or [string]$description.status -cne 'persistent' -or [string]$description.maturity -cne 'library' -or [int]$description.testCount -ne $ExpectedTestCount) {
        throw "Fresh-process description for '$Word' did not retain its persistent library definition and $ExpectedTestCount attached cases."
    }
    if (@($description.effects).Count -ne 0) {
        throw "Fresh-process description for '$Word' declares effects."
    }
    $coverage = $description.coverage
    if ([string]$coverage.status -cne 'current' -or [int]$coverage.instructionsCovered -ne [int]$coverage.instructionsTotal -or [int]$coverage.branchesCovered -ne [int]$coverage.branchesTotal -or @($coverage.uncoveredInstructions).Count -ne 0 -or @($coverage.uncoveredBranchOutcomes).Count -ne 0) {
        throw "Fresh-process description for '$Word' is missing current instruction/branch coverage."
    }
    $finite = $coverage.finiteCoverage
    if ($null -eq $finite -or $finite.complete -ne $true -or @($finite.missing).Count -ne 0 -or @($finite.unsupported).Count -ne 0) {
        throw "Fresh-process description for '$Word' is missing complete supported finite-input and return coverage."
    }
}

function Get-JsonValue($Response) {
    if ($Response.data.stack.Count -ne 1 -or $Response.data.stackTypes.Count -ne 1 -or [string]$Response.data.stackTypes[0] -cne 'Bool') {
        throw 'An independent oracle expression did not return one Bool value.'
    }
    $structured = $Response.data.structuredStack
    if ($null -eq $structured -or [int]$structured.formatVersion -ne 1 -or $structured.values.Count -ne 1 -or [string]$structured.values[0].kind -cne 'bool' -or $structured.values[0].value -isnot [bool]) {
        throw 'An independent oracle expression did not return one structured Bool value.'
    }
    return [bool]$structured.values[0].value
}

function Get-ExpectedForgeryCheck($Case) {
    if ($Case.method -ceq 'get' -or $Case.method -ceq 'head') {
        return $true
    }

    $originValid = $false
    if (-not $Case.originEnabled -or -not $Case.originPresent) {
        $originValid = $true
    } elseif ([string]$Case.originValue -ceq 'https://chat.example.test') {
        $originValid = $true
    }

    $fetchSiteValid = $false
    if ($Case.fetchSitePresent) {
        if ([string]$Case.fetchSiteValue -ceq 'same-origin' -or [string]$Case.fetchSiteValue -ceq 'same-site') {
            $fetchSiteValid = $true
        }
    } elseif (-not $Case.requestIsSsl -and -not $Case.forceSsl) {
        $fetchSiteValid = $true
    }

    return ($originValid -and $fetchSiteValid)
}

$runId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$runPath = Join-Path $evidenceRoot $runId
$projectPath = Join-Path $runPath 'project'
[IO.Directory]::CreateDirectory($runPath) | Out-Null
[IO.Directory]::CreateDirectory($projectPath) | Out-Null
$runRoot = (Resolve-Path -LiteralPath $evidenceRoot).Path.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$fullRunPath = (Resolve-Path -LiteralPath $runPath).Path
if (-not $fullRunPath.StartsWith($runRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Run path '$fullRunPath' is outside verifier evidence root '$runRoot'."
}

$report = [ordered]@{
    schemaVersion = 1
    runId = $runId
    status = 'running'
    startedAtUtc = [DateTime]::UtcNow.ToString('o')
    sourcePath = 'applications/campfire/request-policy.flow'
    sourceSha256 = $null
    runtime = [ordered]@{ pinned = $verifyPinnedRuntime; files = @() }
    sessions = @()
    attachedTests = @()
    libraryQualifications = @()
    independentCases = @()
}
$sessions = [Collections.Generic.List[object]]::new()

try {
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Flow source is missing: '$sourcePath'."
    }
    $source = [IO.File]::ReadAllText($sourcePath, [Text.Encoding]::UTF8)
    if ($source.Contains("`r") -or -not $source.EndsWith("`n", [StringComparison]::Ordinal) -or $source.EndsWith("`n`n", [StringComparison]::Ordinal)) {
        throw 'Flow source must use LF line endings and exactly one final newline.'
    }
    $report.sourceSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $sourcePath).Hash.ToLowerInvariant()

    $runtimeSourceRoot = [IO.Path]::GetDirectoryName($cliDllPath)
    $pins = @()
    if ($verifyPinnedRuntime) {
        $pins = @(Get-Content -LiteralPath $pinsPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 10)
        if ($pins.Count -ne $runtimeFileNames.Count) {
            throw 'Pinned runtime manifest does not enumerate exactly the five required runtime files.'
        }
    }
    $runtimeRoot = Join-Path $runPath 'runtime'
    [IO.Directory]::CreateDirectory($runtimeRoot) | Out-Null
    $runtimeFiles = [Collections.Generic.List[object]]::new()
    foreach ($runtimeName in $runtimeFileNames) {
        $sourceRuntimeFile = Join-Path $runtimeSourceRoot $runtimeName
        if (-not (Test-Path -LiteralPath $sourceRuntimeFile -PathType Leaf)) {
            throw "Required runtime file is missing beside '$cliDllPath': '$runtimeName'."
        }
        $sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $sourceRuntimeFile).Hash.ToLowerInvariant()
        if ($verifyPinnedRuntime) {
            $pin = @($pins | Where-Object { [string]$_.name -ceq $runtimeName })
            if ($pin.Count -ne 1 -or [string]$pin[0].sha256 -cne $sourceHash) {
                throw "Sealed runtime hash mismatch for '$runtimeName'."
            }
        }
        $destination = Join-Path $runtimeRoot $runtimeName
        Copy-Item -LiteralPath $sourceRuntimeFile -Destination $destination
        $copiedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $destination).Hash.ToLowerInvariant()
        if ($copiedHash -cne $sourceHash) {
            throw "Private runtime copy differs from source for '$runtimeName'."
        }
        $runtimeFiles.Add([ordered]@{ name = $runtimeName; sha256 = $copiedHash; source = [IO.Path]::GetRelativePath($repo, $sourceRuntimeFile).Replace('\', '/') })
    }
    $report.runtime.files = @($runtimeFiles.ToArray())
    $cliPath = Join-Path $runtimeRoot 'AgentLang.Cli.dll'

    $setupRequests = @(
        [ordered]@{ op = 'define'; frontend = 'flow'; syntaxVersion = 2; source = $source },
        [ordered]@{ op = 'test-all' },
        [ordered]@{ op = 'commit'; word = 'campfire.request.origin-valid?'; library = $true },
        [ordered]@{ op = 'commit'; word = 'campfire.request.fetch-site-valid?'; library = $true },
        [ordered]@{ op = 'commit'; word = 'campfire.request.forgery-check-passed?'; library = $true }
    )
    $setup = Invoke-CliSession -Name 'define-test-commit' -Requests $setupRequests -ProjectPath $projectPath -RunPath $runPath -CliPath $cliPath
    $sessions.Add([ordered]@{ name = $setup.name; requests = $setup.requestCount; responses = $setup.responseCount; stdin = [IO.Path]::GetRelativePath($repo, $setup.stdinPath).Replace('\', '/'); stdout = [IO.Path]::GetRelativePath($repo, $setup.stdoutPath).Replace('\', '/') })
    $setupLabels = @('Flow/2 source definition', 'attached request-policy tests', 'origin helper library commit', 'fetch-site helper library commit', 'forgery-check library commit')
    for ($index = 0; $index -lt $setupLabels.Count; $index++) {
        Assert-OkResponse $setup.responses[$index] $setupLabels[$index]
    }

    $expectedCases = @(
        'campfire.request.origin-valid?/checking-disabled-allows-missing',
        'campfire.request.origin-valid?/checking-disabled-allows-null',
        'campfire.request.origin-valid?/checking-enabled-allows-missing',
        'campfire.request.origin-valid?/checking-enabled-allows-exact-match',
        'campfire.request.origin-valid?/checking-enabled-rejects-different-host',
        'campfire.request.origin-valid?/checking-enabled-rejects-case-changed-origin',
        'campfire.request.origin-valid?/checking-enabled-rejects-null',
        'campfire.request.fetch-site-valid?/accepts-same-origin',
        'campfire.request.fetch-site-valid?/accepts-same-site',
        'campfire.request.fetch-site-valid?/rejects-cross-site',
        'campfire.request.fetch-site-valid?/rejects-none-value',
        'campfire.request.fetch-site-valid?/rejects-invalid-value',
        'campfire.request.fetch-site-valid?/rejects-case-changed-value',
        'campfire.request.fetch-site-valid?/missing-is-allowed-for-http-without-force-ssl',
        'campfire.request.fetch-site-valid?/missing-is-rejected-for-http-with-force-ssl',
        'campfire.request.fetch-site-valid?/missing-is-rejected-for-https-without-force-ssl',
        'campfire.request.fetch-site-valid?/missing-is-rejected-for-https-with-force-ssl',
        'campfire.request.forgery-check-passed?/get-skips-forgery-headers',
        'campfire.request.forgery-check-passed?/head-skips-forgery-headers',
        'campfire.request.forgery-check-passed?/unsafe-accepts-exact-origin-and-same-origin',
        'campfire.request.forgery-check-passed?/unsafe-accepts-missing-origin-when-checking-enabled',
        'campfire.request.forgery-check-passed?/unsafe-accepts-missing-origin-when-checking-disabled',
        'campfire.request.forgery-check-passed?/unsafe-checking-disabled-allows-null-origin',
        'campfire.request.forgery-check-passed?/unsafe-rejects-wrong-origin',
        'campfire.request.forgery-check-passed?/unsafe-rejects-null-origin',
        'campfire.request.forgery-check-passed?/unsafe-rejects-cross-site',
        'campfire.request.forgery-check-passed?/unsafe-missing-fetch-site-is-allowed-for-http',
        'campfire.request.forgery-check-passed?/unsafe-missing-fetch-site-rejects-force-ssl-http',
        'campfire.request.forgery-check-passed?/unsafe-missing-fetch-site-rejects-https'
    )
    $stagedResults = @($setup.responses[1].data.results)
    $observedCases = @($stagedResults | ForEach-Object { "$($_.word)/$($_.name)" } | Sort-Object)
    $sortedExpectedCases = @($expectedCases | Sort-Object)
    if ($stagedResults.Count -ne $expectedCases.Count -or ($observedCases -join "`n") -cne ($sortedExpectedCases -join "`n")) {
        throw "Attached case inventory differs from the expected $($expectedCases.Count) policy cases; observed $($stagedResults.Count)."
    }
    foreach ($case in $stagedResults) {
        if ($case.passed -ne $true) {
            throw "Attached case '$($case.word)/$($case.name)' failed: $(@($case.actual) -join ', ')."
        }
        $report.attachedTests += [ordered]@{ word = [string]$case.word; name = [string]$case.name; passed = $true; actual = @($case.actual) }
    }
    $initialSummary = $setup.responses[1].data.testSummary
    if ([int]$initialSummary.executed -ne $expectedCases.Count -or [int]$initialSummary.passed -ne $expectedCases.Count -or [int]$initialSummary.failed -ne 0) {
        throw 'Initial attached-test summary does not report all policy cases passing.'
    }

    $baseUrl = 'https://chat.example.test'
    $originCases = @(
        [pscustomobject]@{ name = 'absent'; present = $false; value = $null; expression = 'option.none<String>()' },
        [pscustomobject]@{ name = 'null'; present = $true; value = 'null'; expression = 'option.some<String>("null")' },
        [pscustomobject]@{ name = 'exact'; present = $true; value = $baseUrl; expression = 'option.some<String>("https://chat.example.test")' },
        [pscustomobject]@{ name = 'different'; present = $true; value = 'https://other.example.test'; expression = 'option.some<String>("https://other.example.test")' },
        [pscustomobject]@{ name = 'case-changed'; present = $true; value = 'https://CHAT.example.test'; expression = 'option.some<String>("https://CHAT.example.test")' }
    )
    $fetchSiteCases = @(
        [pscustomobject]@{ name = 'absent'; present = $false; value = $null; expression = 'option.none<String>()' },
        [pscustomobject]@{ name = 'same-origin'; present = $true; value = 'same-origin'; expression = 'option.some<String>("same-origin")' },
        [pscustomobject]@{ name = 'same-site'; present = $true; value = 'same-site'; expression = 'option.some<String>("same-site")' },
        [pscustomobject]@{ name = 'cross-site'; present = $true; value = 'cross-site'; expression = 'option.some<String>("cross-site")' },
        [pscustomobject]@{ name = 'none'; present = $true; value = 'none'; expression = 'option.some<String>("none")' },
        [pscustomobject]@{ name = 'invalid'; present = $true; value = 'invalid'; expression = 'option.some<String>("invalid")' },
        [pscustomobject]@{ name = 'case-changed'; present = $true; value = 'Same-Origin'; expression = 'option.some<String>("Same-Origin")' }
    )
    $oracleRequests = [Collections.Generic.List[System.Collections.IDictionary]]::new()
    $oracleRequests.Add([ordered]@{ op = 'test-all' })
    $oracleRequests.Add([ordered]@{ op = 'describe'; word = 'campfire.request.origin-valid?' })
    $oracleRequests.Add([ordered]@{ op = 'describe'; word = 'campfire.request.fetch-site-valid?' })
    $oracleRequests.Add([ordered]@{ op = 'describe'; word = 'campfire.request.forgery-check-passed?' })
    $oracleCases = [Collections.Generic.List[object]]::new()
    foreach ($method in @('get', 'head', 'unsafe')) {
        foreach ($originEnabled in @($false, $true)) {
            foreach ($originCase in $originCases) {
                foreach ($fetchSiteCase in $fetchSiteCases) {
                    foreach ($requestIsSsl in @($false, $true)) {
                        foreach ($forceSsl in @($false, $true)) {
                            $originEnabledText = if ($originEnabled) { 'true' } else { 'false' }
                            $requestIsSslText = if ($requestIsSsl) { 'true' } else { 'false' }
                            $forceSslText = if ($forceSsl) { 'true' } else { 'false' }
                            $expression = "campfire.request.forgery-check-passed?(RequestMethod.$method(), $($originCase.expression), $($fetchSiteCase.expression), `"$baseUrl`", $originEnabledText, $requestIsSslText, $forceSslText)"
                            $case = [pscustomobject]@{
                                name = "$method/$($originCase.name)/$($fetchSiteCase.name)/ssl-$requestIsSsl/force-$forceSsl/origin-check-$originEnabled"
                                method = $method
                                originEnabled = [bool]$originEnabled
                                originPresent = [bool]$originCase.present
                                originValue = $originCase.value
                                fetchSitePresent = [bool]$fetchSiteCase.present
                                fetchSiteValue = $fetchSiteCase.value
                                requestIsSsl = [bool]$requestIsSsl
                                forceSsl = [bool]$forceSsl
                                expression = $expression
                                expected = $null
                            }
                            $case.expected = Get-ExpectedForgeryCheck $case
                            $oracleCases.Add($case)
                            $oracleRequests.Add([ordered]@{ op = 'eval'; frontend = 'flow'; syntaxVersion = 2; structured = $true; code = $expression })
                        }
                    }
                }
            }
        }
    }

    $oracle = Invoke-CliSession -Name 'fresh-reload-and-independent-matrix' -Requests $oracleRequests.ToArray() -ProjectPath $projectPath -RunPath $runPath -CliPath $cliPath
    $sessions.Add([ordered]@{ name = $oracle.name; requests = $oracle.requestCount; responses = $oracle.responseCount; stdin = [IO.Path]::GetRelativePath($repo, $oracle.stdinPath).Replace('\', '/'); stdout = [IO.Path]::GetRelativePath($repo, $oracle.stdoutPath).Replace('\', '/') })
    Assert-OkResponse $oracle.responses[0] 'fresh-process attached test run'
    $freshResults = @($oracle.responses[0].data.results)
    if ($freshResults.Count -ne $expectedCases.Count -or @($freshResults | Where-Object { $_.passed -ne $true }).Count -ne 0) {
        throw "Fresh process did not pass all $($expectedCases.Count) attached cases."
    }
    $freshSummary = $oracle.responses[0].data.testSummary
    if ([int]$freshSummary.executed -ne $expectedCases.Count -or [int]$freshSummary.passed -ne $expectedCases.Count -or [int]$freshSummary.failed -ne 0) {
        throw 'Fresh-process attached-test summary does not report all cases passing.'
    }

    $descriptors = @(
        [pscustomobject]@{ index = 1; word = 'campfire.request.origin-valid?'; testCount = 7 },
        [pscustomobject]@{ index = 2; word = 'campfire.request.fetch-site-valid?'; testCount = 10 },
        [pscustomobject]@{ index = 3; word = 'campfire.request.forgery-check-passed?'; testCount = 12 }
    )
    foreach ($descriptor in $descriptors) {
        Assert-LibraryDescription $oracle.responses[$descriptor.index] $descriptor.word $descriptor.testCount
        $report.libraryQualifications += [ordered]@{
            word = $descriptor.word
            testCount = $descriptor.testCount
            status = [string]$oracle.responses[$descriptor.index].data.status
            maturity = [string]$oracle.responses[$descriptor.index].data.maturity
            coverage = $oracle.responses[$descriptor.index].data.coverage
        }
    }

    if ($oracleCases.Count -ne 840 -or $oracle.responses.Count -ne $oracleCases.Count + 4) {
        throw "Independent matrix count mismatch; expected 840 expressions and 844 total responses, observed $($oracleCases.Count) expressions and $($oracle.responses.Count) responses."
    }
    for ($index = 0; $index -lt $oracleCases.Count; $index++) {
        $case = $oracleCases[$index]
        $response = $oracle.responses[$index + 4]
        Assert-OkResponse $response "independent case '$($case.name)'"
        $observed = Get-JsonValue $response
        if ($observed -ne [bool]$case.expected) {
            throw "Independent case '$($case.name)' expected $($case.expected) but returned $observed."
        }
        $report.independentCases += [ordered]@{ name = $case.name; expected = [bool]$case.expected; observed = $observed; passed = $true }
    }

    $report.sessions = @($sessions.ToArray())
    $report.outcome = [ordered]@{
        attachedTests = $expectedCases.Count
        freshAttachedPassed = $expectedCases.Count
        independentCases = $oracleCases.Count
        independentPassed = $oracleCases.Count
        publishedLibraries = @('campfire.request.origin-valid?', 'campfire.request.fetch-site-valid?', 'campfire.request.forgery-check-passed?')
    }
    $report.status = 'passed'
}
catch {
    $report.status = 'failed'
    $report.error = $_.ToString()
    $report.sessions = @($sessions.ToArray())
    throw
}
finally {
    $report.finishedAtUtc = [DateTime]::UtcNow.ToString('o')
    $reportPath = Join-Path $runPath 'verification.json'
    [IO.File]::WriteAllText($reportPath, (ConvertTo-Json -InputObject $report -Depth 30), $utf8NoBom)
}

Write-Output (ConvertTo-Json -InputObject ([ordered]@{
    status = $report.status
    runId = $runId
    attachedTests = $report.outcome.attachedTests
    independentCases = $report.outcome.independentCases
    report = [IO.Path]::GetRelativePath($repo, $reportPath).Replace('\', '/')
}) -Depth 8 -Compress)
