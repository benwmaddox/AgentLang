[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ActorProjectPath,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9._-]+$')][string]$RunId
)
$ErrorActionPreference = 'Stop'
$trial = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$actor = (Resolve-Path $ActorProjectPath).Path
$run = Join-Path $PSScriptRoot $RunId
if (Test-Path -LiteralPath $run) { throw "Scoring run already exists: $run" }
$pin = Get-Content (Join-Path $trial 'runtime-pin.json') -Raw | ConvertFrom-Json
$oracle = Get-Content (Join-Path $trial 'oracle.json') -Raw | ConvertFrom-Json
$runtime = Join-Path $trial 'runtime\debug-artifacts\AgentLang.Cli.dll'
$hostScript = Join-Path $repo 'scripts\Start-SubagentTrialHostV2.ps1'
$pwshPath = (Get-Command pwsh -ErrorAction Stop).Source
if ((Get-FileHash $runtime -Algorithm SHA256).Hash -ne $pin.cliDllSha256) { throw 'Pinned CLI hash mismatch.' }
if ((Get-FileHash (Join-Path (Split-Path $runtime) 'AgentLang.Core.dll') -Algorithm SHA256).Hash -ne $pin.coreDllSha256) { throw 'Pinned Core hash mismatch.' }
if ((Get-FileHash $hostScript -Algorithm SHA256).Hash -ne $pin.broker.sha256) { throw 'Pinned broker hash mismatch.' }
foreach ($file in $pin.runtimeFiles) {
    if ((Get-FileHash (Join-Path (Join-Path $trial 'runtime\debug-artifacts') ($file.path.Replace('/', '\'))) -Algorithm SHA256).Hash -ne $file.sha256) { throw "Pinned runtime dependency hash mismatch: $($file.path)" }
}
New-Item -ItemType Directory -Path $run | Out-Null
$actorSnapshot = {
    param([string]$Path)
    @(Get-ChildItem -LiteralPath $Path -Force -Recurse | Sort-Object FullName | ForEach-Object {
        $relative = [System.IO.Path]::GetRelativePath($Path, $_.FullName).Replace('\', '/')
        if ($_.PSIsContainer) { [pscustomobject]@{ path=$relative; kind='directory' } }
        else { [pscustomobject]@{ path=$relative; kind='file'; bytes=$_.Length; sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash } }
    })
}
$before = & $actorSnapshot $actor
$before | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $run 'actor-before.json') -Encoding utf8NoBOM
$scoreProject = Join-Path $run 'project'
New-Item -ItemType Directory -Path $scoreProject | Out-Null
Get-ChildItem -LiteralPath $actor -Force | Copy-Item -Destination $scoreProject -Recurse -Force
function Write-JsonLines([string]$Path, [object[]]$Items) {
    $lines = foreach ($item in $Items) { $item | ConvertTo-Json -Depth 25 -Compress }
    [System.IO.File]::WriteAllLines($Path, [string[]]$lines, [System.Text.UTF8Encoding]::new($false))
}
function Invoke-Host([string]$Name, [object[]]$Requests) {
    $requestPath = Join-Path $run "$Name.requests.jsonl"
    $responsePath = Join-Path $run "$Name.responses.jsonl"
    $tracePath = Join-Path $run "$Name.trace.jsonl"
    $stderrPath = Join-Path $run "$Name.stderr.log"
    $all = @($Requests) + @([ordered]@{ op='host.close' })
    Write-JsonLines $requestPath $all
    $start = [System.Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $pwshPath
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($arg in @('-NoProfile','-File',$hostScript,'-CliDll',$runtime,'-ProjectPath',$scoreProject,'-TracePath',$tracePath,'-AllowedOperations','test-all,source,describe,eval','-Profile','agentlang','-ClockValue','2000-01-01T00:00:00Z','-ExchangeTimeoutMilliseconds','120000','-MaxExchanges','25')) { $start.ArgumentList.Add($arg) }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (-not $process.Start()) { throw "Could not start scoring host $Name." }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $process.StandardInput.Write([System.IO.File]::ReadAllText($requestPath))
    $process.StandardInput.Close()
    $process.WaitForExit()
    $outText = $stdout.GetAwaiter().GetResult()
    $errText = $stderr.GetAwaiter().GetResult()
    [System.IO.File]::WriteAllText($responsePath,$outText,[System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText($stderrPath,$errText,[System.Text.UTF8Encoding]::new($false))
    $rows = @([System.IO.File]::ReadAllLines($responsePath) | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json -Depth 50 })
    if ($process.ExitCode -ne 0) { throw "Scoring host $Name exited $($process.ExitCode); inspect its saved trace and stderr." }
    [pscustomobject]@{ responses=$rows; requestPath=$requestPath; responsePath=$responsePath; tracePath=$tracePath; stderrPath=$stderrPath; exitCode=$process.ExitCode }
}
$discovery = Invoke-Host 'discover-validator' @([ordered]@{ op='source'; type='BatchBounds' })
if ($discovery.responses.Count -ne 1 -or -not $discovery.responses[0].ok) { throw 'Could not read BatchBounds source from scoring copy.' }
$typeSource = [string]$discovery.responses[0].data
$targetMatch = [regex]::Match($typeSource,'(?m)\bvalidate\s+(?<target>(?:::)?(?:[A-Za-z][A-Za-z0-9_.-]*::)?[A-Za-z][A-Za-z0-9?._-]*)\s*;')
if (-not $targetMatch.Success) { throw 'Could not discover a record validator in BatchBounds source.' }
$validatorReference = $targetMatch.Groups['target'].Value
$validatorName = if ($validatorReference.StartsWith('::')) { $validatorReference.Substring(2) } else { $validatorReference }
$validatorName = $validatorName.Replace('::','.')
$requests = [System.Collections.Generic.List[object]]::new()
$requests.Add([ordered]@{ op='test-all' })
$requests.Add([ordered]@{ op='source'; type='BatchBounds' })
foreach ($word in @('batch.span',$validatorName)) { $requests.Add([ordered]@{ op='source'; word=$word }) }
foreach ($word in @('batch.span',$validatorName)) { $requests.Add([ordered]@{ op='describe'; word=$word }) }
foreach ($case in $oracle.cases) {
    if ($case.construction -eq 'accepted') { $code = "int::to-string(batch::span(batchBounds::new(minimum = $($case.minimum), maximum = $($case.maximum))))" }
    else { $code = "batchBounds::new(minimum = $($case.minimum), maximum = $($case.maximum))" }
    $requests.Add([ordered]@{ op='eval'; code=$code; syntaxVersion=2; frontend='flow'; structured=$true })
}
$score = Invoke-Host 'score' $requests.ToArray()
$rows = $score.responses
if ($rows.Count -ne (6 + $oracle.cases.Count)) { throw "Scoring response count $($rows.Count) does not match the request plan." }
$recordSource = [string]$rows[1].data
$widthSource = [string]$rows[2].data
$validatorSource = [string]$rows[3].data
$recordBody = [regex]::Match($recordSource,'(?s)record\s+BatchBounds\s*\{(?<body>.*?)\}')
$fieldNames = @([regex]::Matches($recordBody.Groups['body'].Value,'\bfield\s+([A-Za-z][A-Za-z0-9_-]*)\s*:\s*([A-Za-z][A-Za-z0-9_<>,.? ]*)\s*;') | ForEach-Object { "$($_.Groups[1].Value):$($_.Groups[2].Value.Trim())" } | Sort-Object)
$widthSignature = [regex]::IsMatch($widthSource,'fn\s+batch\.span\s*\(\s*[A-Za-z][A-Za-z0-9_-]*\s*:\s*BatchBounds\s*\)\s*->\s*Int')
$validatorSignature = [regex]::IsMatch($validatorSource,('fn\s+' + [regex]::Escape($validatorName) + '\s*\(\s*[A-Za-z][A-Za-z0-9_-]*\s*:\s*BatchBounds\s*\)\s*->\s*Bool'))
$testResults = @($rows[0].data.results)
$oracleChecks = for ($i=0; $i -lt $oracle.cases.Count; $i++) {
    $case = $oracle.cases[$i]; $response = $rows[$i+6]
    $actual = if ($response.ok -and $case.construction -eq 'accepted') { $response.data.structuredStack.values[0].value }
        elseif (-not $response.ok) { $response.error.code } else { 'accepted' }
    $expected = if ($case.construction -eq 'accepted') { [string]$case.span } else { [string]$case.construction }
    [pscustomobject]@{ minimum=$case.minimum; maximum=$case.maximum; expected=$expected; actual=$actual; passed=($actual -eq $expected) }
}
$after = & $actorSnapshot $actor
$after | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $run 'actor-after.json') -Encoding utf8NoBOM
$actorUnchanged = (ConvertTo-Json -InputObject @($before) -Depth 8 -Compress) -eq (ConvertTo-Json -InputObject @($after) -Depth 8 -Compress)
$describeWidth = $rows[4].data; $describeValidator = $rows[5].data
$sourceReadsOk = @($rows[1..3] | Where-Object { -not $_.ok }).Count -eq 0
$validatorPure = $rows[5].ok -and @($describeValidator.effects).Count -eq 0
$allTestsPassed = @($testResults | Where-Object { -not $_.passed }).Count -eq 0
$spanOwnedPassingValueTests = @($testResults | Where-Object { $_.word -eq 'batch.span' -and $_.passed -and -not $_.expectedErrorCode })
$validatorTests = @($testResults | Where-Object { $_.word -eq $validatorName })
$validatorOwnTrueTests = @($validatorTests | Where-Object { $_.passed -and [string]$_.expected -eq 'true' })
$validatorOwnRejectedTests = @($validatorTests | Where-Object { $_.passed -and $_.expectedErrorCode -eq 'RECORD_VALIDATION_FAILED' })
$validatorCoverage = $describeValidator.coverage.finiteCoverage
$validatorObserved = @($validatorCoverage.returns | Where-Object { $_.position -eq 0 } | ForEach-Object { $_.observed }) | Select-Object -Unique
$validatorOwnBothValues = (@($validatorObserved | Where-Object { $_ -eq 'true' }).Count -gt 0) -and (@($validatorObserved | Where-Object { $_ -eq 'false' }).Count -gt 0)
$manifestReview = [ordered]@{ available=$false; manifestHash=$null; schemaFormatVersion=$null; typeValidatorTarget=$null; predicateWordId=$null; stableTargetMatches=$false; error=$null }
try {
    $store = Join-Path $scoreProject '.agentlang\store'
    $pointer = Get-Content (Join-Path $store 'CURRENT') -Raw | ConvertFrom-Json
    $manifestPath = Join-Path (Join-Path $store 'manifests') ($pointer.manifestHash + '.json')
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json -Depth 40
    $typeRow = $manifest.types | Where-Object { $_.name -eq 'BatchBounds' } | Select-Object -First 1
    $wordRow = $manifest.words | Where-Object { $_.currentName -eq $validatorName } | Select-Object -First 1
    $manifestReview = [ordered]@{
        available = ($null -ne $typeRow -and $null -ne $wordRow)
        manifestHash = $pointer.manifestHash
        schemaFormatVersion = $manifest.formatVersion
        typeValidatorTarget = $typeRow.validatorTarget
        predicateWordId = $wordRow.wordId
        reloadedPredicateId = $describeValidator.id
        stableTargetMatches = ($null -ne $typeRow -and $null -ne $wordRow -and $typeRow.validatorTarget.kind -eq 'userWord' -and $typeRow.validatorTarget.identity -eq $describeValidator.id -and $wordRow.wordId -eq $describeValidator.id)
        error = $null
    }
} catch {
    $manifestReview.error = $_.Exception.Message
}
$summary = [ordered]@{
    schemaVersion=1; causalComparison=$false; actorProjectUnchanged=$actorUnchanged; actorProjectFileCount=@($after | Where-Object kind -eq 'file').Count
    scoringCopy=(Get-Item $scoreProject).FullName; freshReloadTestAllOk=$rows[0].ok; freshReloadSourceReadsOk=$sourceReadsOk; freshReloadLoadedCommittedProject=($rows[0].ok -and $sourceReadsOk -and $manifestReview.available); allTestsPassed=$allTestsPassed; testsRun=$testResults.Count; testsPassed=@($testResults | Where-Object passed).Count
    recordFields=$fieldNames; recordFieldsMatch=(($fieldNames -join '|') -eq 'maximum:Int|minimum:Int')
    spanSignatureMatch=$widthSignature; validatorReference=$validatorReference; validatorName=$validatorName; validatorPure=$validatorPure
    validatorSignatureMatch=$validatorSignature; validatorWordId=$describeValidator.id; validatorEffects=@($describeValidator.effects)
    validatorOwnTrueTestCount=$validatorOwnTrueTests.Count; validatorOwnRejectedConstructionTestCount=$validatorOwnRejectedTests.Count
    validatorObservedBoolReturns=$validatorObserved; validatorCoverageComplete=$validatorCoverage.complete; validatorOwnBothValuesObserved=$validatorOwnBothValues
    spanMaturity=$describeWidth.maturity; validatorMaturity=$describeValidator.maturity; bothWordsLibrary=($describeWidth.maturity -eq 'library' -and $describeValidator.maturity -eq 'library')
    spanOwnedPassingValueTestCount=$spanOwnedPassingValueTests.Count
    freshReloadManifest=$manifestReview; persistedValidatorBindingMatches=$manifestReview.stableTargetMatches
    oracle=$oracleChecks; oraclePassed=(@($oracleChecks | Where-Object { -not $_.passed }).Count -eq 0)
    operations=@('test-all','source type=BatchBounds','source word=batch.span','source word=<validator>','describe','eval'); actorProjectMutatedByScorer=$false; scoringProjectWasCopy=$true
}
$summaryPath = Join-Path $run 'summary.json'
$summary | ConvertTo-Json -Depth 25 | Set-Content $summaryPath -Encoding utf8NoBOM
$failed = -not $actorUnchanged -or -not $rows[0].ok -or -not $allTestsPassed -or -not $sourceReadsOk -or -not $validatorPure -or -not $recordBody.Success -or -not $summary.recordFieldsMatch -or -not $widthSignature -or -not $validatorSignature -or -not $manifestReview.stableTargetMatches -or @($oracleChecks | Where-Object { -not $_.passed }).Count -ne 0
$summary | ConvertTo-Json -Depth 25
if ($failed) { throw "Scoring checks failed; evidence is saved in $run." }
