[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$trial = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$diag = $PSScriptRoot
$actor = Join-Path $trial 'project\actor'
$actorDictionary = Join-Path $actor 'dictionary.agent'
$project = Join-Path $diag 'project'
$pin = Get-Content (Join-Path $trial 'runtime-pin.json') -Raw | ConvertFrom-Json
$runtime = Join-Path $trial 'runtime\debug-artifacts\AgentLang.Cli.dll'
$hostScript = Join-Path $repo 'scripts\Start-SubagentTrialHostV2.ps1'
if (Test-Path -LiteralPath $project) { throw "Diagnostic project already exists: $project" }
if ((Get-FileHash $runtime -Algorithm SHA256).Hash -ne $pin.cliDllSha256) { throw 'Pinned CLI hash mismatch.' }
if ((Get-FileHash $hostScript -Algorithm SHA256).Hash -ne $pin.broker.sha256) { throw 'Pinned broker hash mismatch.' }
$sourcePath = Join-Path $diag 'reconstruction.source.agent'
$source = [System.IO.File]::ReadAllText($sourcePath)
$actorSource = [System.IO.File]::ReadAllText($actorDictionary)
function Get-BlockHash([string]$Text, [string]$Pattern) {
    $match = [regex]::Match($Text, $Pattern)
    if (-not $match.Success) { return $null }
    $body = $match.Groups['body'].Value.Replace(([string][char]13 + [char]10), ([string][char]10)).TrimEnd()
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($body)
    [pscustomobject]@{ sha256=[Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)); body=$body }
}
$bodyComparisons = foreach ($item in @(
    @{ name='range.is-valid'; pattern='(?s)fn\s+range\.is-valid\s*\([^)]*\)\s*->\s*Bool\s*\{(?<body>.*?)\}' },
    @{ name='range.width'; pattern='(?s)fn\s+range\.width\s*\([^)]*\)\s*->\s*Int\s*\{(?<body>.*?)\}' }
)) {
    $actorBody = Get-BlockHash $actorSource $item.pattern
    $diagnosticBody = Get-BlockHash $source $item.pattern
    [pscustomobject]@{ word=$item.name; actorSha256=$actorBody.sha256; diagnosticSha256=$diagnosticBody.sha256; exactBodyMatch=($actorBody.sha256 -eq $diagnosticBody.sha256) }
}
$testNames = @('range.is-valid/equal-endpoints','range.is-valid/increasing','range.width/equal-endpoints','range.width/positive','range.width/reversed-rejected')
$testComparisons = foreach ($name in $testNames) {
    $pattern = '(?s)test\s+' + [regex]::Escape($name) + '\s*\{(?<body>.*?)\}'
    $actorTest = Get-BlockHash $actorSource $pattern
    $diagnosticTest = Get-BlockHash $source $pattern
    [pscustomobject]@{ test=$name; actorSha256=$actorTest.sha256; diagnosticSha256=$diagnosticTest.sha256; exactTestMatch=($actorTest.sha256 -eq $diagnosticTest.sha256) }
}
@{ actorDictionarySha256=(Get-FileHash $actorDictionary -Algorithm SHA256).Hash; bodyComparisons=$bodyComparisons; testComparisons=$testComparisons } |
    ConvertTo-Json -Depth 10 | Set-Content (Join-Path $diag 'source-body-hashes.json') -Encoding utf8NoBOM
if (@($bodyComparisons | Where-Object { -not $_.exactBodyMatch }).Count -ne 0 -or @($testComparisons | Where-Object { -not $_.exactTestMatch }).Count -ne 0) { throw 'Reconstruction differs from actor function bodies or original tests.' }
New-Item -ItemType Directory -Path $project | Out-Null
$requests = @(
    [ordered]@{ op='define'; source=$source; syntaxVersion=2; frontend='flow' },
    [ordered]@{ op='test'; word='range.is-valid' },
    [ordered]@{ op='describe'; word='range.is-valid' },
    [ordered]@{ op='commit'; word='range.is-valid'; library=$true },
    [ordered]@{ op='describe'; word='range.is-valid' },
    [ordered]@{ op='describe'; word='range.width' },
    [ordered]@{ op='test-all' },
    [ordered]@{ op='source'; type='BoundedRange' },
    [ordered]@{ op='source'; word='range.is-valid' },
    [ordered]@{ op='source'; word='range.width' }
)
$allRequests = @($requests) + @([ordered]@{ op='host.close' })
$requestPath = Join-Path $diag 'requests.jsonl'
$responsePath = Join-Path $diag 'responses.jsonl'
$tracePath = Join-Path $diag 'trace.jsonl'
$stderrPath = Join-Path $diag 'stderr.log'
$lines = foreach ($request in $allRequests) { $request | ConvertTo-Json -Depth 25 -Compress }
[System.IO.File]::WriteAllLines($requestPath,[string[]]$lines,[System.Text.UTF8Encoding]::new($false))
$start = [System.Diagnostics.ProcessStartInfo]::new()
$start.FileName = (Get-Command pwsh).Source; $start.UseShellExecute = $false; $start.CreateNoWindow = $true
$start.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden; $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
foreach ($arg in @('-NoProfile','-File',$hostScript,'-CliDll',$runtime,'-ProjectPath',$project,'-TracePath',$tracePath,'-AllowedOperations','define,test,describe,commit,test-all,source','-Profile','agentlang','-ClockValue','2000-01-01T00:00:00Z','-ExchangeTimeoutMilliseconds','120000','-MaxExchanges','25')) { $start.ArgumentList.Add($arg) }
$process = [System.Diagnostics.Process]::new(); $process.StartInfo = $start
if (-not $process.Start()) { throw 'Could not start the diagnostic host.' }
$stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
$process.StandardInput.Write([System.IO.File]::ReadAllText($requestPath)); $process.StandardInput.Close(); $process.WaitForExit()
$outText = $stdout.GetAwaiter().GetResult(); $errText = $stderr.GetAwaiter().GetResult()
[System.IO.File]::WriteAllText($responsePath,$outText,[System.Text.UTF8Encoding]::new($false))
[System.IO.File]::WriteAllText($stderrPath,$errText,[System.Text.UTF8Encoding]::new($false))
$responses = @([System.IO.File]::ReadAllLines($responsePath) | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json -Depth 50 })
if ($process.ExitCode -ne 0 -or $responses.Count -ne $requests.Count) { throw 'Diagnostic host failed; preserve and inspect its request/response/trace logs.' }
$before = $responses[2]; $commit = $responses[3]; $after = $responses[4]; $width = $responses[5]; $allTests = $responses[6]
$preCoverage = $before.data.coverage.finiteCoverage
$postCoverage = $after.data.coverage.finiteCoverage
$store = Join-Path $project '.agentlang\store'
$pointer = Get-Content (Join-Path $store 'CURRENT') -Raw | ConvertFrom-Json
$manifestPath = Join-Path (Join-Path $store 'manifests') ($pointer.manifestHash + '.json')
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$typeRow = $manifest.types | Where-Object name -eq 'BoundedRange'
$predicateRow = $manifest.words | Where-Object currentName -eq 'range.is-valid'
$manualManifest = [ordered]@{
    manifestHash=$pointer.manifestHash; schemaFormatVersion=$manifest.formatVersion
    typeValidatorTarget=$typeRow.validatorTarget; predicateWordId=$predicateRow.wordId
    stableTargetMatches=($typeRow.validatorTarget.identity -eq $predicateRow.wordId)
    predicateMaturity=($manifest.revisions | Where-Object wordId -eq $predicateRow.wordId | Sort-Object revision -Descending | Select-Object -First 1).maturity
}
$manualManifest | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $diag 'manual-manifest-review.json') -Encoding utf8NoBOM
$summary = [ordered]@{
    diagnosticOnly=$true; actorProjectChanged=$false; originalBodiesAndTestsMatch=( (@($bodyComparisons | Where-Object { -not $_.exactBodyMatch }).Count -eq 0) -and (@($testComparisons | Where-Object { -not $_.exactTestMatch }).Count -eq 0) )
    addedTest='range.is-valid/reversed-library-evidence'; predicateTestPassed=@($responses[1].data.results | Where-Object { -not $_.passed }).Count -eq 0
    predicateTestResults=$responses[1].data.results; coverageBeforeCommit=$preCoverage
    libraryCommitOk=$commit.ok; libraryCommitText=$commit.text; predicateMaturityAfterCommit=$after.data.maturity
    coverageAfterCommit=$postCoverage; widthMaturityInScratch=$width.data.maturity
    allTestsPassed=@($allTests.data.results | Where-Object { -not $_.passed }).Count -eq 0
    allTestCount=$allTests.data.results.Count; manifest=$manualManifest
    requests='requests.jsonl'; responses='responses.jsonl'; trace='trace.jsonl'; stderr='stderr.log'
}
$summary | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $diag 'summary.json') -Encoding utf8NoBOM
$summary | ConvertTo-Json -Depth 20
