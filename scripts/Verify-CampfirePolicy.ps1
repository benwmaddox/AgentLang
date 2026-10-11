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
$defaultCliDll = Join-Path $repo 'src/AgentLang.Cli/bin/Release/net9.0/AgentLang.Cli.dll'
if ([string]::IsNullOrWhiteSpace($CliDll)) { $CliDll = $defaultCliDll }
$cliDllPath = (Resolve-Path -LiteralPath $CliDll).Path
$referenceRoot = Join-Path $repo '.agentlang/campfire-reference'
$railsReference = Join-Path $referenceRoot 'reference'
$sourcePath = Join-Path $repo 'applications/campfire/message-policy.flow'
$evidenceRoot = Join-Path $repo '.agentlang/campfire-234/runs'
$expectedRustCommit = '6dae2fd3c1a2dce61ca11beb731bcce2b42b67ad'
$expectedRailsCommit = '90b330024dec3e757c79b6a7e6568f93da8e3148'
$expectedRuntimeFiles = @(
    'AgentLang.Cli.deps.json',
    'AgentLang.Cli.dll',
    'AgentLang.Cli.runtimeconfig.json',
    'AgentLang.Core.dll',
    'FSharp.Core.dll'
)

function Get-LocalHead([string]$Path) {
    $output = @(& git -C $Path rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or $output.Count -ne 1) {
        throw "Could not read the local Git HEAD at '$Path'."
    }
    return ([string]$output[0]).Trim()
}

function ConvertTo-RequestLine($Request) {
    ConvertTo-Json -InputObject $Request -Depth 30 -Compress
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
        throw "The '$Name' JSONL request exceeds 1 MiB."
    }

    $inputPath = Join-Path $RunPath "$Name.stdin.jsonl"
    $stdoutPath = Join-Path $RunPath "$Name.stdout.jsonl"
    $stderrPath = Join-Path $RunPath "$Name.stderr.txt"
    [IO.File]::WriteAllText($inputPath, $inputText, $utf8NoBom)

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'dotnet'
    $startInfo.WorkingDirectory = $repo
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $processTemp = Join-Path $RunPath 'process-temp'
    [IO.Directory]::CreateDirectory($processTemp) | Out-Null
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
            throw "Could not start the copied CLI for session '$Name'."
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
            throw "CLI session '$Name' exceeded the $TimeoutSeconds second timeout; partial output is in '$stdoutPath' and '$stderrPath'."
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
            throw "CLI session '$Name' returned $($responseLines.Count) response lines for $($Requests.Count) requests. See '$stdoutPath'."
        }
        $responses = @($responseLines | ForEach-Object { ConvertFrom-Json -InputObject $_ -Depth 100 })
        return [pscustomobject]@{
            name = $Name
            exitCode = $process.ExitCode
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
        $json = if ($null -eq $Response) { '<missing response>' } else { ConvertTo-Json -InputObject $Response -Depth 20 -Compress }
        throw "$Label failed: $json"
    }
}

function New-UserExpression([int]$Id, [string]$Role) {
    "user.new(id = UserId.new($Id), role = Role.$Role())"
}

function New-CreatorExpression([string]$Owner, [int]$UserId) {
    switch ($Owner) {
        'self' { return "option.some<UserId>(UserId.new($UserId))" }
        'other' { return "option.some<UserId>(UserId.new(22))" }
        'none' { return 'option.none<UserId>()' }
        default { throw "Unknown creator state '$Owner'." }
    }
}

$runId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$runPath = Join-Path $evidenceRoot $runId
$projectPath = Join-Path $runPath 'project'
[IO.Directory]::CreateDirectory($runPath) | Out-Null
[IO.Directory]::CreateDirectory($projectPath) | Out-Null
$runRoot = (Resolve-Path -LiteralPath $evidenceRoot).Path.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$fullRunPath = (Resolve-Path -LiteralPath $runPath).Path
if (-not $fullRunPath.StartsWith($runRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Run path '$fullRunPath' is outside the verifier evidence root '$runRoot'."
}

$report = [ordered]@{
    schemaVersion = 1
    runId = $runId
    status = 'running'
    startedAtUtc = [DateTime]::UtcNow.ToString('o')
    objective = 'Pure Campfire message-administration policy component only.'
    sourcePath = 'applications/campfire/message-policy.flow'
    sourceSha256 = $null
    reference = [ordered]@{
        rustRepositoryCommit = $expectedRustCommit
        railsReferenceCommit = $expectedRailsCommit
        localCheckoutVerification = $null
        licenseUrl = "https://github.com/basecamp/once-campfire-rust/blob/$expectedRustCommit/MIT-LICENSE"
        rustRulePath = 'crates/db/src/models/user.rs'
        rustMessageBoundaryPath = 'crates/campfire/src/controllers/messages.rs'
        railsRulePath = 'reference/app/models/user/role.rb'
        railsMessageBoundaryPath = 'reference/app/controllers/messages_controller.rb'
    }
    runtime = $null
    projectPath = [IO.Path]::GetRelativePath($repo, $projectPath).Replace('\', '/')
    sessions = @()
    attachedTests = @()
    independentCases = @()
    error = $null
}
$reportPath = Join-Path $runPath 'verification.json'
$sessions = [Collections.Generic.List[object]]::new()
$attachedTests = [Collections.Generic.List[object]]::new()
$independentCases = [Collections.Generic.List[object]]::new()

try {
    if (-not (Test-Path -LiteralPath $cliDllPath -PathType Leaf)) {
        throw "CLI assembly is missing: '$cliDllPath'. Build AgentLang.Cli in Release or pass -CliDll."
    }
    $localReferences = [ordered]@{ rust = 'not-checked-out'; rails = 'not-checked-out' }
    if (Test-Path -LiteralPath (Join-Path $referenceRoot '.git')) {
        $actualRustCommit = Get-LocalHead $referenceRoot
        if ($actualRustCommit -cne $expectedRustCommit) { throw "Pinned Rust source changed: expected $expectedRustCommit, found $actualRustCommit." }
        $localReferences.rust = $actualRustCommit
    }
    if (Test-Path -LiteralPath (Join-Path $railsReference '.git')) {
        $actualRailsCommit = Get-LocalHead $railsReference
        if ($actualRailsCommit -cne $expectedRailsCommit) { throw "Pinned Rails reference changed: expected $expectedRailsCommit, found $actualRailsCommit." }
        $localReferences.rails = $actualRailsCommit
    }
    $report.reference.localCheckoutVerification = $localReferences

    $runtimeRoot = Join-Path $runPath 'runtime'
    [IO.Directory]::CreateDirectory($runtimeRoot) | Out-Null
    $runtimeSourceRoot = [IO.Path]::GetDirectoryName($cliDllPath)
    $runtimeFileHashes = [Collections.Generic.List[object]]::new()
    foreach ($runtimeName in $expectedRuntimeFiles) {
        $sourceRuntimeFile = Join-Path $runtimeSourceRoot $runtimeName
        if (-not (Test-Path -LiteralPath $sourceRuntimeFile -PathType Leaf)) {
            throw "Required CLI runtime file is missing beside '$cliDllPath': '$runtimeName'."
        }
        $runtimeFile = Join-Path $runtimeRoot $runtimeName
        Copy-Item -LiteralPath $sourceRuntimeFile -Destination $runtimeFile
        $runtimeFileHashes.Add([ordered]@{
            path = $runtimeName
            sourcePath = [IO.Path]::GetRelativePath($repo, $sourceRuntimeFile).Replace('\', '/')
            length = (Get-Item -LiteralPath $runtimeFile).Length
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $runtimeFile).Hash.ToLowerInvariant()
        })
    }
    $cliPath = Join-Path $runtimeRoot 'AgentLang.Cli.dll'
    $report.runtime = [ordered]@{ sourceAssembly = [IO.Path]::GetRelativePath($repo, $cliDllPath).Replace('\', '/'); snapshotPath = [IO.Path]::GetRelativePath($repo, $runtimeRoot).Replace('\', '/'); files = @($runtimeFileHashes.ToArray()) }

    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Flow source is missing: '$sourcePath'."
    }
    $source = Get-Content -LiteralPath $sourcePath -Raw -Encoding utf8
    $sourceSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $sourcePath).Hash.ToLowerInvariant()
    $report.sourceSha256 = $sourceSha256

    $defineRequest = [ordered]@{ op = 'define'; frontend = 'flow'; syntaxVersion = 2; source = $source }
    $setupRequests = @(
        $defineRequest,
        [ordered]@{ op = 'test-all' },
        [ordered]@{ op = 'commit'; word = 'user.can-administer?'; library = $true },
        [ordered]@{ op = 'commit'; word = 'message.can-administer?'; library = $true }
    )
    $setup = Invoke-CliSession -Name 'define-test-commit' -Requests $setupRequests -ProjectPath $projectPath -RunPath $runPath -CliPath $cliPath
    $setupSession = [ordered]@{
        name = $setup.name
        exitCode = $setup.exitCode
        requestCount = $setup.requestCount
        responseCount = $setup.responseCount
        stdinPath = [IO.Path]::GetRelativePath($repo, $setup.stdinPath).Replace('\', '/')
        stdoutPath = [IO.Path]::GetRelativePath($repo, $setup.stdoutPath).Replace('\', '/')
        stderrPath = [IO.Path]::GetRelativePath($repo, $setup.stderrPath).Replace('\', '/')
        setupTestCount = $null
        helperCommit = 'user.can-administer?'
        wrapperCommit = 'message.can-administer?'
    }
    $sessions.Add($setupSession)
    foreach ($index in 0..3) {
        $labels = @('Flow/2 source definition', 'attached policy and wrapper tests', 'policy helper library commit', 'existing-message wrapper library commit')
        Assert-OkResponse $setup.responses[$index] $labels[$index]
    }
    $stagedResults = @($setup.responses[1].data.results)
    $expectedTestKeys = [Collections.Generic.List[string]]::new()
    $roles = @('member', 'administrator', 'bot')
    $owners = @('self', 'other', 'none')
    foreach ($role in $roles) {
        foreach ($owner in $owners) {
            foreach ($isNew in @($false, $true)) {
                $life = if ($isNew) { 'new' } else { 'existing' }
                $testOwner = if ($owner -ceq 'none') { 'no-creator' } else { $owner }
                $expectedTestKeys.Add("user.can-administer?/$role-$testOwner-$life")
            }
        }
    }
    foreach ($role in $roles) {
        foreach ($owner in @('own', 'other')) {
            $expectedTestKeys.Add("message.can-administer?/$role-$owner-message")
        }
    }
    $observedTestKeys = @($stagedResults | ForEach-Object { "$($_.word)/$($_.name)" } | Sort-Object)
    $sortedExpectedTestKeys = @($expectedTestKeys.ToArray() | Sort-Object)
    if ($stagedResults.Count -ne 24 -or ($observedTestKeys -join "`n") -cne ($sortedExpectedTestKeys -join "`n")) {
        throw "Expected exactly 24 attached cases with the complete role/creator/lifecycle inventory; observed $($stagedResults.Count)."
    }
    foreach ($case in $stagedResults) {
        $passed = $case.passed -eq $true
        $attachedTests.Add([ordered]@{ word = [string]$case.word; name = [string]$case.name; passed = $passed; actual = @($case.actual) })
        if (-not $passed) {
            throw "Attached case '$($case.word)/$($case.name)' failed: $(@($case.actual) -join ', ')."
        }
    }
    $setupSession.setupTestCount = $stagedResults.Count

    $oracleRequests = [Collections.Generic.List[System.Collections.IDictionary]]::new()
    $oracleRequests.Add([ordered]@{ op = 'test-all' })
    $oracleRequests.Add([ordered]@{ op = 'describe'; word = 'user.can-administer?' })
    $oracleRequests.Add([ordered]@{ op = 'describe'; word = 'message.can-administer?' })
    $oracleCaseExpressions = [Collections.Generic.List[object]]::new()
    foreach ($role in $roles) {
        foreach ($owner in $owners) {
            foreach ($isNew in @($false, $true)) {
                $isNewLiteral = if ($isNew) { 'true' } else { 'false' }
                $ownerExpression = New-CreatorExpression -Owner $owner -UserId 11
                $userExpression = New-UserExpression -Id 11 -Role $role
                $expression = "user.can-administer?($userExpression, $ownerExpression, $isNewLiteral)"
                $expected = ($role -ceq 'administrator') -or ($owner -ceq 'self') -or $isNew
                $caseName = "$role-$owner-$(if ($isNew) { 'new' } else { 'existing' })"
                $oracleCaseExpressions.Add([pscustomobject]@{ role = $role; owner = $owner; isNew = $isNew; name = $caseName; expected = $expected; expression = $expression })
                $oracleRequests.Add([ordered]@{ op = 'eval'; frontend = 'flow'; syntaxVersion = 2; structured = $true; code = $expression })
            }
        }
    }
    $oracle = Invoke-CliSession -Name 'fresh-test-and-independent-oracle' -Requests $oracleRequests.ToArray() -ProjectPath $projectPath -RunPath $runPath -CliPath $cliPath
    $sessions.Add([ordered]@{ name = $oracle.name; exitCode = $oracle.exitCode; requestCount = $oracle.requestCount; responseCount = $oracle.responseCount; stdinPath = [IO.Path]::GetRelativePath($repo, $oracle.stdinPath).Replace('\', '/'); stdoutPath = [IO.Path]::GetRelativePath($repo, $oracle.stdoutPath).Replace('\', '/'); stderrPath = [IO.Path]::GetRelativePath($repo, $oracle.stderrPath).Replace('\', '/') })
    Assert-OkResponse $oracle.responses[0] 'fresh-process attached test run'
    $freshResults = @($oracle.responses[0].data.results)
    if ($freshResults.Count -ne 24 -or @($freshResults | Where-Object { $_.passed -ne $true }).Count -ne 0) {
        throw "Fresh-process test run did not pass all 24 attached cases; observed $($freshResults.Count) cases."
    }
    $testSummary = $oracle.responses[0].data.testSummary
    if ([int]$testSummary.executed -ne 24 -or [int]$testSummary.passed -ne 24 -or [int]$testSummary.failed -ne 0) {
        throw 'Fresh-process test summary does not report 24/24 passes.'
    }

    foreach ($descriptor in @(
        [pscustomobject]@{ index = 1; word = 'user.can-administer?'; testCount = 18 },
        [pscustomobject]@{ index = 2; word = 'message.can-administer?'; testCount = 6 }
    )) {
        $describeResponse = $oracle.responses[$descriptor.index]
        Assert-OkResponse $describeResponse "fresh-process describe '$($descriptor.word)'"
        $description = $describeResponse.data
        if ([string]$description.name -cne $descriptor.word -or [string]$description.maturity -cne 'library' -or [string]$description.status -cne 'persistent' -or [int]$description.testCount -ne $descriptor.testCount) {
            throw "Fresh-process description for '$($descriptor.word)' did not retain published library maturity and expected attached tests."
        }
        if (@($description.effects).Count -ne 0) {
            throw "Fresh-process description for '$($descriptor.word)' reports unexpected effects."
        }
    }

    for ($index = 0; $index -lt $oracleCaseExpressions.Count; $index++) {
        $case = $oracleCaseExpressions[$index]
        $response = $oracle.responses[$index + 3]
        Assert-OkResponse $response "independent truth-table case '$($case.name)'"
        if ($response.data.stack.Count -ne 1 -or $response.data.stackTypes.Count -ne 1 -or [string]$response.data.stackTypes[0] -cne 'Bool') {
            throw "Independent case '$($case.name)' did not return one Bool value."
        }
        $structuredValues = $response.data.structuredStack.values
        if ($null -eq $response.data.structuredStack -or [int]$response.data.structuredStack.formatVersion -ne 1 -or $structuredValues.Count -ne 1) {
            throw "Independent case '$($case.name)' did not return one structured value."
        }
        $structuredValue = $structuredValues[0]
        if ([string]$structuredValue.kind -cne 'bool' -or $structuredValue.value -isnot [bool]) {
            throw "Independent case '$($case.name)' returned a non-Bool structured value."
        }
        $observed = $structuredValue.value
        $matches = $observed -eq [bool]$case.expected
        $independentCases.Add([ordered]@{ role = $case.role; creator = $case.owner; isNew = [bool]$case.isNew; expected = [bool]$case.expected; observed = $observed; passed = $matches; expression = $case.expression })
        if (-not $matches) {
            throw "Independent truth-table case '$($case.name)' expected $($case.expected) but returned $observed."
        }
    }

    $report.sessions = @($sessions.ToArray())
    $report.attachedTests = @($attachedTests.ToArray())
    $report.independentCases = @($independentCases.ToArray())
    $report.outcome = [ordered]@{ attachedTests = 24; attachedPassed = 24; independentCases = 18; independentPassed = 18; helperPublishedAsLibrary = $true; messageWrapperPublishedAsLibrary = $true }
    $report.status = 'passed'
}
catch {
    $report.status = 'failed'
    $report.error = $_.ToString()
    $report.sessions = @($sessions.ToArray())
    $report.attachedTests = @($attachedTests.ToArray())
    $report.independentCases = @($independentCases.ToArray())
    throw
}
finally {
    $report.finishedAtUtc = [DateTime]::UtcNow.ToString('o')
    $reportPath = Join-Path $runPath 'verification.json'
    [IO.File]::WriteAllText($reportPath, (ConvertTo-Json -InputObject $report -Depth 40), $utf8NoBom)
}

Write-Output (ConvertTo-Json -InputObject ([ordered]@{
    status = $report.status
    runPath = [IO.Path]::GetRelativePath($repo, $runPath).Replace('\', '/')
    attachedTestsPassed = $report.outcome.attachedPassed
    independentCasesPassed = $report.outcome.independentPassed
    rustCommit = $expectedRustCommit
    railsCommit = $expectedRailsCommit
    sourceSha256 = $report.sourceSha256
}) -Depth 5 -Compress)
