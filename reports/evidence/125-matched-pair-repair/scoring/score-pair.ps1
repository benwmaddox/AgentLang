#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('flow', 'fsharp')][string]$Language,
    [Parameter(Mandatory)][string]$ActorProject,
    [string]$OraclePath = (Join-Path $PSScriptRoot '..\oracle.json'),
    [string]$RuntimePinPath = (Join-Path $PSScriptRoot '..\runtime\runtime-pin.json'),
    [string]$CliDll,
    [string]$OutputPath = (Join-Path $PSScriptRoot ("score-$Language.json")),
    [switch]$RequireFullPass
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$checks = [Collections.Generic.List[object]]::new()
$processes = [Collections.Generic.List[object]]::new()
$caseEvidence = [Collections.Generic.List[object]]::new()
$failure = $null
$passed = $false
$scratchRoot = Join-Path $PSScriptRoot ('.scratch-' + $Language + '-' + [guid]::NewGuid().ToString('N'))
$actorRoot = $null
$actorProjectFile = $null
$actorBefore = @()
$actorAfter = @()
$actorSourceBefore = @()
$actorSourceAfter = @()
$oracle = $null
$behaviorScore = $null
$outputFull = $null
$actorProjectPath = $null
$actorTestEvidence = $null
$cli = $null
$runtimePin = $null
$runtimeInventory = @()
$script:outputFull = [IO.Path]::GetFullPath($OutputPath)

function Add-Check([string]$Name, [bool]$Condition, [string]$Details = '') {
    $script:checks.Add([ordered]@{ name = $Name; passed = $Condition; details = $Details })
}

function Get-PropertyValue($Object, [string]$Name, $Default = $null) {
    if ($null -eq $Object) { return $Default }
    if ($Object -is [Collections.IDictionary]) {
        if ($Object.Contains($Name)) { return $Object[$Name] }
        return $Default
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $Default }
    $property.Value
}

function Get-Inventory([string]$Root, [switch]$ExcludeBuildOutput) {
    $items = [Collections.Generic.List[object]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $Root -File -Recurse -Force) {
        $relative = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        if ($relative -match '(^|/)\.git(/|$)') { continue }
        if ($ExcludeBuildOutput -and $relative -match '(^|/)(bin|obj)(/|$)') { continue }
        $items.Add([ordered]@{
            path = $relative
            bytes = $file.Length
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        })
    }
    return ,@($items | Sort-Object path)
}

function Get-CanonicalJson($Value) {
    ConvertTo-Json -InputObject $Value -Depth 100 -Compress
}

function Get-CanonicalMap($Map) {
    $ordered = [ordered]@{}
    if ($null -ne $Map) {
        if ($Map -is [Collections.IDictionary]) {
            foreach ($key in @($Map.Keys | Sort-Object -CaseSensitive)) { $ordered[[string]$key] = [string]$Map[$key] }
        }
        else {
            foreach ($entry in $Map) { $ordered[[string]$entry.Key] = [string]$entry.Value }
        }
    }
    Get-CanonicalJson $ordered
}

function Copy-Actor([string]$From, [string]$To) {
    [void][IO.Directory]::CreateDirectory($To)
    foreach ($file in Get-ChildItem -LiteralPath $From -File -Recurse -Force) {
        $relative = [IO.Path]::GetRelativePath($From, $file.FullName).Replace('\', '/')
        if ($relative -match '(^|/)\.git(/|$)' -or $relative -match '(^|/)(bin|obj)(/|$)') { continue }
        $destination = Join-Path $To ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
        [IO.File]::Copy($file.FullName, $destination, $true)
    }
}

function Invoke-BoundedProcess([string]$FileName, [string[]]$Arguments, [string]$WorkingDirectory, [int]$TimeoutMilliseconds = 180000) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    foreach ($argument in $Arguments) { [void]$startInfo.ArgumentList.Add([string]$argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            return [pscustomobject]@{ exitCode = $null; timedOut = $false; launchError = 'Process did not start.'; stdout = ''; stderr = '' }
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $exited = $process.WaitForExit($TimeoutMilliseconds)
        if (-not $exited) {
            try { $process.Kill($true) } catch { }
            [void]$process.WaitForExit(10000)
        }
        return [pscustomobject]@{
            exitCode = if ($exited) { $process.ExitCode } else { $null }
            timedOut = (-not $exited)
            launchError = $null
            stdout = $stdoutTask.GetAwaiter().GetResult()
            stderr = $stderrTask.GetAwaiter().GetResult()
        }
    }
    catch {
        return [pscustomobject]@{ exitCode = $null; timedOut = $false; launchError = $_.Exception.ToString(); stdout = ''; stderr = '' }
    }
    finally { $process.Dispose() }
}

function Invoke-DotNet([string]$Purpose, [string[]]$Arguments, [string]$WorkingDirectory, [int]$TimeoutMilliseconds = 180000) {
    $result = Invoke-BoundedProcess 'dotnet' $Arguments $WorkingDirectory $TimeoutMilliseconds
    $script:processes.Add([ordered]@{
        purpose = $Purpose
        executable = 'dotnet'
        arguments = @($Arguments)
        workingDirectory = $WorkingDirectory
        exitCode = $result.exitCode
        timedOut = $result.timedOut
        launchError = $result.launchError
        stdout = $result.stdout
        stderr = $result.stderr
    })
    return $result
}

function Verify-FlowRuntimePin([string]$PinPath, [string]$CliPath) {
    $pinFull = (Resolve-Path -LiteralPath $PinPath).Path
    $pinObject = Get-Content -LiteralPath $pinFull -Raw | ConvertFrom-Json -Depth 100
    $runtimeDirectory = Join-Path (Split-Path -Parent $pinFull) 'debug-artifacts'
    if (-not (Test-Path -LiteralPath $runtimeDirectory -PathType Container)) { throw "Missing pinned runtime directory: $runtimeDirectory" }
    $actual = Get-Inventory $runtimeDirectory
    $expected = @($pinObject.runtimeFiles | ForEach-Object {
        [ordered]@{ path = ([string]$_.path).Replace('\', '/'); bytes = [int64]$_.bytes; sha256 = ([string]$_.sha256).ToLowerInvariant() }
    })
    $actualByPath = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($item in $actual) { $actualByPath[[string]$item.path] = $item }
    $matches = $actualByPath.Count -eq $expected.Count
    foreach ($item in $expected) {
        if (-not $actualByPath.ContainsKey([string]$item.path)) { $matches = $false; continue }
        $found = $actualByPath[[string]$item.path]
        if ([int64]$found.bytes -ne [int64]$item.bytes -or ([string]$found.sha256).ToLowerInvariant() -cne ([string]$item.sha256).ToLowerInvariant()) { $matches = $false }
    }
    Add-Check 'pinned Flow runtime has the exact 22 files and hashes' ($matches -and $expected.Count -eq 22 -and $actual.Count -eq 22) "expected=$($expected.Count), actual=$($actual.Count)"
    if (-not $matches -or $expected.Count -ne 22 -or $actual.Count -ne 22) { throw 'Pinned Flow runtime inventory did not match the frozen 22-file manifest.' }
    $cliFull = if ([string]::IsNullOrWhiteSpace($CliPath)) { Join-Path $runtimeDirectory 'AgentLang.Cli.dll' } else { $CliPath }
    $cliFull = (Resolve-Path -LiteralPath $cliFull).Path
    $cliHash = (Get-FileHash -LiteralPath $cliFull -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedCliHash = ([string]$pinObject.cliDllSha256).ToLowerInvariant()
    Add-Check 'pinned Flow CLI hash matches its manifest' ($cliHash -ceq $expectedCliHash) "actual=$cliHash expected=$expectedCliHash"
    if ($cliHash -cne $expectedCliHash) { throw 'Flow CLI hash differs from runtime-pin.json.' }
    $script:runtimePin = [ordered]@{ path = $pinFull; sha256 = (Get-FileHash -LiteralPath $pinFull -Algorithm SHA256).Hash.ToLowerInvariant(); runtimeDirectory = $runtimeDirectory; fileCount = $actual.Count; cliPath = $cliFull; cliSha256 = $cliHash; runtimeFiles = $actual }
    $script:runtimeInventory = $actual
    $script:cli = $cliFull
}

function Invoke-FlowJsonl([string]$Name, [string]$Project, [object[]]$Requests, [string[]]$Allow = @()) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.WorkingDirectory = $Project
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @($script:cli, '--project', $Project, '--clock', '2000-01-01T00:00:00Z')) { [void]$start.ArgumentList.Add([string]$argument) }
    if ($Allow.Count -gt 0) { [void]$start.ArgumentList.Add('--allow'); [void]$start.ArgumentList.Add(($Allow -join ',')) }
    [void]$start.ArgumentList.Add('--jsonl')
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (-not $process.Start()) { throw "Could not start pinned Flow runtime for $Name." }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    try {
        foreach ($request in $Requests) { $process.StandardInput.WriteLine((ConvertTo-Json -InputObject $request -Depth 100 -Compress)) }
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(120000)) { $process.Kill($true); throw "Flow session '$Name' exceeded 120 seconds." }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $responses = [Collections.Generic.List[object]]::new()
        foreach ($line in @($stdout -split "`r?`n" | Where-Object { $_.Length -gt 0 })) {
            try { $responses.Add(($line | ConvertFrom-Json -Depth 100)) }
            catch { throw "Flow session '$Name' emitted invalid JSONL: $line" }
        }
        $entry = [ordered]@{
            purpose = $Name
            project = $Project
            allow = @($Allow)
            requests = @($Requests)
            responses = $responses.ToArray()
            stderr = $stderr
            exitCode = $process.ExitCode
            responseCountMatches = ($responses.Count -eq $Requests.Count)
        }
        $script:processes.Add($entry)
        if ($responses.Count -ne $Requests.Count) { throw "Flow session '$Name' had $($responses.Count) responses for $($Requests.Count) requests." }
        if ($process.ExitCode -ne 0) { throw "Flow session '$Name' exited $($process.ExitCode): $stderr" }
        return ,($responses.ToArray())
    }
    finally {
        if (-not $process.HasExited) { $process.Kill($true) }
        $process.Dispose()
    }
}

function Get-FlowEffects($Response) {
    $effects = Get-PropertyValue (Get-PropertyValue $Response 'data') 'effects'
    return [ordered]@{
        reads = [int](Get-PropertyValue $effects 'fs.read' 0)
        writes = [int](Get-PropertyValue $effects 'fs.write' 0)
    }
}

function Get-SnapshotState([string]$Project, [string]$Name) {
    $snapshotPath = Join-Path $Project ".agentlang/store/snapshots/$Name.json"
    if (-not (Test-Path -LiteralPath $snapshotPath -PathType Leaf)) { throw "Snapshot not written: $snapshotPath" }
    $descriptor = Get-Content -LiteralPath $snapshotPath -Raw | ConvertFrom-Json -AsHashtable -Depth 100
    if ($null -eq $descriptor.virtualFiles) { return [ordered]@{} }
    $reference = $descriptor.virtualFiles
    if ($reference.kind -cne 'virtual-file-state') { throw "Snapshot state reference has unexpected kind '$($reference.kind)'" }
    $objectPath = Join-Path $Project ".agentlang/store/objects/$($reference.hash).json"
    if (-not (Test-Path -LiteralPath $objectPath -PathType Leaf)) { throw "Snapshot state object not found: $objectPath" }
    $hash = (Get-FileHash -LiteralPath $objectPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -cne ([string]$reference.hash).ToLowerInvariant()) { throw "Snapshot state hash mismatch for $objectPath" }
    Get-Content -LiteralPath $objectPath -Raw | ConvertFrom-Json -AsHashtable -Depth 100
}

function Get-FlowString([string]$Value) {
    ConvertTo-Json -InputObject $Value -Compress
}

function New-FlowPairExpression($Case) {
    $firstId = Get-FlowString ([string]$Case.first.id)
    $firstStatus = Get-FlowString ([string]$Case.first.status)
    $secondId = Get-FlowString ([string]$Case.second.id)
    $secondStatus = Get-FlowString ([string]$Case.second.status)
    $first = "invoice::new(id = InvoiceId::new($firstId), status = InvoiceStatus::new($firstStatus))"
    $second = "invoice::new(id = InvoiceId::new($secondId), status = InvoiceStatus::new($secondStatus))"
    "invoice::queue-reminders-for-pair(invoicePair::new(first = $first, second = $second))"
}

function Read-FlowResult($Response) {
    $stack = @(Get-PropertyValue (Get-PropertyValue $Response 'data') 'stack')
    if ($stack.Count -lt 1) { return $null }
    $rendered = $stack[0]
    if ($rendered -isnot [string]) { return $rendered }
    try { return (ConvertFrom-Json -InputObject $rendered -AsHashtable -Depth 30 -ErrorAction Stop) }
    catch {
        $record = [regex]::Match($rendered, '^\s*[A-Za-z][A-Za-z0-9_]*\s*\{\s*first\s*=\s*(?<first>"(?:\\.|[^"\\])*")\s*,\s*second\s*=\s*(?<second>"(?:\\.|[^"\\])*")\s*\}\s*$')
        if (-not $record.Success) { return $rendered }
        return [ordered]@{
            first = ConvertFrom-Json -InputObject $record.Groups['first'].Value -AsHashtable -ErrorAction Stop
            second = ConvertFrom-Json -InputObject $record.Groups['second'].Value -AsHashtable -ErrorAction Stop
        }
    }
}

function Get-FlowErrorCode($Response) {
    $error = Get-PropertyValue $Response 'error'
    if ($null -eq $error) { return $null }
    [string](Get-PropertyValue $error 'code')
}

function Score-FlowCase($Case, [string]$CaseProject) {
    $initialRequests = [Collections.Generic.List[object]]::new()
    foreach ($key in @($Case.initial.Keys | Sort-Object -CaseSensitive)) {
        $path = Get-FlowString ([string]$key)
        $contents = Get-FlowString ([string]$Case.initial[$key])
        $initialRequests.Add(@{ op = 'eval'; frontend = 'flow'; code = "file::write($path, $contents)" })
    }
    $initialRequests.Add(@{ op = 'snapshot.save'; name = 'initial' })
    $primed = Invoke-FlowJsonl "prime $($Case.name)" $CaseProject $initialRequests.ToArray() @('fs.read', 'fs.write')
    $primeErrors = @($primed | Where-Object { -not $_.ok })
    Add-Check "Flow initial state saved: $($Case.name)" ($primeErrors.Count -eq 0) (Get-CanonicalJson $primeErrors)

    $allow = [Collections.Generic.List[string]]::new()
    if ([bool]$Case.allowRead) { $allow.Add('fs.read') }
    if ([bool]$Case.allowWrite) { $allow.Add('fs.write') }
    $requests = [Collections.Generic.List[object]]::new()
    $requests.Add(@{ op = 'snapshot.load'; name = 'initial' })
    $isDenied = -not [string]::IsNullOrWhiteSpace([string]$Case.expectedError)
    if ($isDenied) { $requests.Add(@{ op = 'task.begin'; goal = "Pair scorer $($Case.name) denied capability preflight" }) }
    $expression = New-FlowPairExpression $Case
    for ($iteration = 0; $iteration -lt [int]$Case.repeat; $iteration++) {
        $requests.Add(@{ op = 'eval'; frontend = 'flow'; code = $expression })
    }
    if ($isDenied) { $requests.Add(@{ op = 'task.status' }) }
    $requests.Add(@{ op = 'snapshot.save'; name = 'result' })
    $responses = Invoke-FlowJsonl "score $($Case.name)" $CaseProject $requests.ToArray() $allow.ToArray()
    $startIndex = 1 + [int]$isDenied
    $callResponses = @($responses[$startIndex..($startIndex + [int]$Case.repeat - 1)])
    $actualResults = [Collections.Generic.List[object]]::new()
    $parsedResults = [Collections.Generic.List[object]]::new()
    $errorResponses = [Collections.Generic.List[object]]::new()
    $reads = 0
    $writes = 0
    foreach ($response in $callResponses) {
        if (-not $response.ok) { $errorResponses.Add($response); continue }
        $result = Read-FlowResult $response
        $parsedResults.Add($result)
        if ($null -eq $result -or $result -is [string]) { $actualResults.Add($result); continue }
        $actualResults.Add([ordered]@{
            first = [string](Get-PropertyValue $result 'first')
            second = [string](Get-PropertyValue $result 'second')
        })
        $effects = Get-FlowEffects $response
        $reads += $effects.reads
        $writes += $effects.writes
    }
    $statusResponse = $null
    if ($isDenied) {
        $statusResponse = $responses[$startIndex + [int]$Case.repeat]
        $errorResponses.Add($statusResponse)
        $effects = Get-FlowEffects $statusResponse
        $reads = $effects.reads
        $writes = $effects.writes
    }
    $resultSave = $responses[-1]
    $state = Get-SnapshotState $CaseProject 'result'
    $targetCode = if ($callResponses.Count -gt 0) { Get-FlowErrorCode $callResponses[0] } else { $null }
    if ($isDenied -and -not $targetCode) {
        $taskErrors = Get-PropertyValue (Get-PropertyValue (Get-PropertyValue $statusResponse 'data') 'errors')
        if (@($taskErrors).Count -gt 0) { $targetCode = [string]$taskErrors[0] }
    }
    [ordered]@{
        name = [string]$Case.name
        resultCalls = @($actualResults.ToArray())
        errorCode = $targetCode
        errorRecords = @($errorResponses.ToArray())
        reads = $reads
        writes = $writes
        state = $state
        resultSnapshotSaved = [bool]$resultSave.ok
        sessionResponses = $responses
        initialSessionResponses = $primed
        parsedResults = @($parsedResults.ToArray())
    }
}

function New-FSharpAdapterSource {
@'
namespace PairAcceptance

open System
open System.IO
open System.Text.Json.Nodes
open AgentLang.StatefulPilot

type private ErrorRecord =
    { Type: string
      Message: string }

module Program =
    let private readString (node: JsonNode) (name: string) = node[name].GetValue<string>()
    let private readBool (node: JsonNode) (name: string) = node[name].GetValue<bool>()
    let private readInt (node: JsonNode) (name: string) = node[name].GetValue<int>()

    let private mapFromNode (node: JsonNode) =
        node.AsObject()
        |> Seq.map (fun pair -> pair.Key, pair.Value.GetValue<string>())
        |> Map.ofSeq

    let private invoice (node: JsonNode) : Invoice =
        { Id = InvoiceId(readString node "id")
          Status = InvoiceStatus(readString node "status") }

    let private pair (node: JsonNode) : InvoicePair =
        { first = invoice node["first"]
          second = invoice node["second"] }

    let private resultNode (result: ReminderPairResult) =
        let node = JsonObject()
        node["first"] <- JsonValue.Create<string>(result.first)
        node["second"] <- JsonValue.Create<string>(result.second)
        node :> JsonNode

    let private errorNode (error: ErrorRecord) =
        let node = JsonObject()
        node["type"] <- JsonValue.Create<string>(error.Type)
        node["message"] <- JsonValue.Create<string>(error.Message)
        node :> JsonNode

    let private stateNode (state: Map<string, string>) =
        let node = JsonObject()
        for KeyValue(key, value) in state do node[key] <- JsonValue.Create<string>(value)
        node :> JsonNode

    let private scoreCase (caseNode: JsonNode) : JsonNode =
        let files = VirtualFiles(mapFromNode caseNode["initial"])
        let provider = files :> IReminderFiles
        let invoicePair = pair caseNode
        let allowRead = readBool caseNode "allowRead"
        let allowWrite = readBool caseNode "allowWrite"
        let repeat = readInt caseNode "repeat"
        files.ResetCounts()
        let results = ResizeArray<ReminderPairResult>()
        let errors = ResizeArray<ErrorRecord>()
        let mutable keepGoing = true
        for _ in 1 .. repeat do
            if keepGoing then
                try results.Add(Execution.runPair allowRead allowWrite provider invoicePair ReminderOperations.queueRemindersForPair)
                with error ->
                    errors.Add({ Type = error.GetType().Name; Message = error.Message })
                    keepGoing <- false
        let node = JsonObject()
        node["name"] <- JsonValue.Create<string>(readString caseNode "name")
        let resultArray = JsonArray()
        for result in results do resultArray.Add(resultNode result)
        node["resultCalls"] <- resultArray
        let errorArray = JsonArray()
        for error in errors do errorArray.Add(errorNode error)
        node["errorRecords"] <- errorArray
        let errorCode : JsonNode =
            if errors.Count = 0 then null
            else JsonValue.Create<string>(if errors[0].Message = "CAPABILITY_DENIED" then "CAPABILITY_DENIED" else errors[0].Message) :> JsonNode
        node["errorCode"] <- errorCode
        node["reads"] <- JsonValue.Create(files.ReadCount)
        node["writes"] <- JsonValue.Create(files.WriteCount)
        node["state"] <- stateNode files.State
        node :> JsonNode

    [<EntryPoint>]
    let main arguments =
        match arguments with
        | [| oraclePath |] ->
            let oracle = JsonNode.Parse(File.ReadAllText(oraclePath))
            let cases = oracle["cases"].AsArray()
            let root = JsonObject()
            let outputs = JsonArray()
            for caseNode in cases do outputs.Add(scoreCase caseNode)
            root["cases"] <- outputs
            Console.WriteLine(root.ToJsonString())
            0
        | _ ->
            eprintfn "Expected: PairAcceptance.dll ORACLE.json"
            64
'@
}

function New-FSharpAdapterProject([string]$Directory, [string]$ProjectReference) {
    [void][IO.Directory]::CreateDirectory($Directory)
    $escapedProject = [Security.SecurityElement]::Escape($ProjectReference)
    $projectText = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AssemblyName>PairAcceptance</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$escapedProject" />
    <Compile Include="Program.fs" />
  </ItemGroup>
</Project>
"@
    [IO.File]::WriteAllText((Join-Path $Directory 'PairAcceptance.fsproj'), $projectText, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $Directory 'Program.fs'), (New-FSharpAdapterSource), [Text.UTF8Encoding]::new($false))
    Join-Path $Directory 'PairAcceptance.fsproj'
}

function Read-ProcessJson([object]$ProcessResult, [string]$Purpose) {
    if ($ProcessResult.timedOut -or $ProcessResult.exitCode -ne 0 -or $ProcessResult.launchError) {
        throw "$Purpose failed: exit=$($ProcessResult.exitCode), timeout=$($ProcessResult.timedOut), launch=$($ProcessResult.launchError), stderr=$($ProcessResult.stderr)"
    }
    $lines = @($ProcessResult.stdout -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($lines.Count -ne 1) { throw "$Purpose emitted $($lines.Count) non-empty output lines; expected one JSON response." }
    ConvertFrom-Json -InputObject $lines[0] -AsHashtable -Depth 100
}

function Score-Flow($OracleData) {
    foreach ($case in $OracleData.cases) {
        $caseProject = Join-Path $script:scratchRoot ([string]$case.name)
        Copy-Actor $script:actorRoot $caseProject
        $probe = Score-FlowCase $case $caseProject
        $script:caseEvidence.Add($probe)
    }
}

function Score-FSharp($OracleData) {
    $ownTestDirectory = Join-Path $script:scratchRoot 'actor-tests'
    Copy-Actor $script:actorRoot $ownTestDirectory
    $ownProject = Join-Path $ownTestDirectory 'StatefulPilot.fsproj'
    if (-not (Test-Path -LiteralPath $ownProject -PathType Leaf)) { throw "Missing F# actor project: $ownProject" }
    $ownRun = Invoke-DotNet 'actor-owned tests and example from a fresh copy' @('run', '--project', $ownProject, '--configuration', 'Release', '--no-launch-profile') $ownTestDirectory 240000
    $script:actorTestEvidence = [ordered]@{ exitCode = $ownRun.exitCode; timedOut = $ownRun.timedOut; launchError = $ownRun.launchError; stdout = $ownRun.stdout; stderr = $ownRun.stderr }
    Add-Check 'F# actor-owned tests exit successfully from a fresh copy' (-not $ownRun.timedOut -and $ownRun.exitCode -eq 0 -and -not $ownRun.launchError) "exit=$($ownRun.exitCode); launch=$($ownRun.launchError)"

    $behaviorDirectory = Join-Path $script:scratchRoot 'behavior-project'
    Copy-Actor $script:actorRoot $behaviorDirectory
    $behaviorProject = Join-Path $behaviorDirectory 'StatefulPilot.fsproj'
    if (-not (Test-Path -LiteralPath $behaviorProject -PathType Leaf)) { throw "Missing F# actor project: $behaviorProject" }
    $adapterDirectory = Join-Path $script:scratchRoot 'adapter'
    $adapterProject = New-FSharpAdapterProject $adapterDirectory $behaviorProject
    $build = Invoke-DotNet 'fresh independent adapter build against copied F# actor' @('build', $adapterProject, '--configuration', 'Release', '--nologo') $adapterDirectory 240000
    Add-Check 'F# independent adapter builds against the copied typed API' (-not $build.timedOut -and $build.exitCode -eq 0 -and -not $build.launchError) "exit=$($build.exitCode); launch=$($build.launchError); stderr=$($build.stderr)"
    if ($build.timedOut -or $build.exitCode -ne 0 -or $build.launchError) { throw "F# adapter build failed: $($build.stderr)" }
    $adapterDll = Join-Path $adapterDirectory 'bin/Release/net9.0/PairAcceptance.dll'
    $run = Invoke-DotNet 'independent pair behavior and provider probes' @($adapterDll, (Resolve-Path -LiteralPath $script:oraclePath).Path) $adapterDirectory 120000
    $response = Read-ProcessJson $run 'F# pair adapter'
    foreach ($case in $OracleData.cases) {
        $matching = @($response.cases | Where-Object { $_.name -ceq [string]$case.name })
        if ($matching.Count -ne 1) { throw "F# adapter emitted $($matching.Count) results for '$($case.name)'." }
        $script:caseEvidence.Add($matching[0])
    }
}

function Compare-CaseEvidence($OracleData) {
    foreach ($expectedCase in $OracleData.cases) {
        $actualMatches = @($script:caseEvidence | Where-Object { $_.name -ceq [string]$expectedCase.name })
        if ($actualMatches.Count -ne 1) {
            Add-Check "one scored record: $($expectedCase.name)" $false "found=$($actualMatches.Count)"
            continue
        }
        $actual = $actualMatches[0]
        $expectedError = [string]$expectedCase.expectedError
        if ([string]::IsNullOrWhiteSpace($expectedError)) {
            $expectedCalls = [Collections.Generic.List[object]]::new()
            for ($i = 0; $i -lt [int]$expectedCase.repeat; $i++) {
                $expectedCalls.Add([ordered]@{ first = [string]$expectedCase.expected[0]; second = [string]$expectedCase.expected[1] })
            }
            Add-Check "ordered pair results: $($expectedCase.name)" ((Get-CanonicalJson $actual.resultCalls) -ceq (Get-CanonicalJson $expectedCalls.ToArray())) ("actual=" + (Get-CanonicalJson $actual.resultCalls))
        }
        else {
            Add-Check "capability preflight error: $($expectedCase.name)" ($actual.errorCode -ceq $expectedError) "actual=$($actual.errorCode); errors=$(Get-CanonicalJson $actual.errorRecords)"
        }
        Add-Check "complete virtual filesystem state: $($expectedCase.name)" ((Get-CanonicalMap $actual.state) -ceq (Get-CanonicalMap $expectedCase.expectedState)) ("actual=" + (Get-CanonicalMap $actual.state))
        Add-Check "cumulative read count: $($expectedCase.name)" ([int]$actual.reads -eq [int]$expectedCase.reads) "actual=$($actual.reads); expected=$($expectedCase.reads)"
        Add-Check "cumulative write count: $($expectedCase.name)" ([int]$actual.writes -eq [int]$expectedCase.writes) "actual=$($actual.writes); expected=$($expectedCase.writes)"
        if (-not [string]::IsNullOrWhiteSpace($expectedError)) {
            Add-Check "denied preflight leaves provider untouched: $($expectedCase.name)" ([int]$actual.reads -eq 0 -and [int]$actual.writes -eq 0) "reads=$($actual.reads); writes=$($actual.writes)"
        }
    }
}

try {
    $script:actorProjectPath = (Resolve-Path -LiteralPath $ActorProject).Path
    if (Test-Path -LiteralPath $script:actorProjectPath -PathType Leaf) {
        $script:actorProjectFile = $script:actorProjectPath
        $script:actorRoot = Split-Path -Parent $script:actorProjectPath
    }
    else {
        $script:actorRoot = $script:actorProjectPath
        if ($Language -eq 'fsharp') {
            $projects = @(Get-ChildItem -LiteralPath $script:actorRoot -Filter '*.fsproj' -File)
            if ($projects.Count -ne 1) { throw "Expected one F# project in $script:actorRoot; found $($projects.Count)." }
            $script:actorProjectFile = $projects[0].FullName
        }
    }
    $script:oraclePath = (Resolve-Path -LiteralPath $OraclePath).Path
    $script:outputFull = [IO.Path]::GetFullPath($OutputPath)
    $script:oracle = Get-Content -LiteralPath $script:oraclePath -Raw | ConvertFrom-Json -AsHashtable -Depth 100
    Add-Check 'oracle schema contains exactly 12 fixed cases' ($script:oracle.schemaVersion -eq 1 -and @($script:oracle.cases).Count -eq 12) "cases=$(@($script:oracle.cases).Count)"
    if (@($script:oracle.cases).Count -ne 12) { throw 'The pair oracle must contain exactly 12 cases.' }
    [void][IO.Directory]::CreateDirectory($scratchRoot)

    if ($Language -eq 'flow') { Verify-FlowRuntimePin $RuntimePinPath $CliDll }
    $actorBefore = Get-Inventory $script:actorRoot
    $actorSourceBefore = Get-Inventory $script:actorRoot -ExcludeBuildOutput

    if ($Language -eq 'flow') {
        $testCopy = Join-Path $scratchRoot 'actor-tests'
        Copy-Actor $script:actorRoot $testCopy
        $test = Invoke-FlowJsonl 'actor-owned target test and test-all' $testCopy @(
            @{ op = 'test'; word = 'invoice.queue-reminders-for-pair' },
            @{ op = 'test-all' }
        ) @('fs.read', 'fs.write')
        $script:actorTestEvidence = @($test)
        $targetResults = @(Get-PropertyValue (Get-PropertyValue $test[0] 'data') 'results')
        $allResults = @(Get-PropertyValue (Get-PropertyValue $test[1] 'data') 'results')
        $actorTestsPass = $test[0].ok -and $test[1].ok -and $targetResults.Count -gt 0 -and $allResults.Count -gt 0 -and @($targetResults + $allResults | Where-Object { -not $_.passed }).Count -eq 0
        Add-Check 'Flow actor-owned tests pass separately from behavior probes' $actorTestsPass "target=$($targetResults.Count); all=$($allResults.Count)"
        Score-Flow $oracle
    }
    else {
        Score-FSharp $oracle
    }
    Compare-CaseEvidence $oracle

    $actorAfter = Get-Inventory $script:actorRoot
    $actorSourceAfter = Get-Inventory $script:actorRoot -ExcludeBuildOutput
    $actorUnchanged = (Get-CanonicalJson $actorBefore) -ceq (Get-CanonicalJson $actorAfter)
    Add-Check 'original actor input inventory is unchanged' $actorUnchanged "before=$($actorBefore.Count); after=$($actorAfter.Count)"
    Add-Check 'original actor source inventory is unchanged' ((Get-CanonicalJson $actorSourceBefore) -ceq (Get-CanonicalJson $actorSourceAfter)) 'bin/obj are excluded from the source-only comparison'
    $caseChecks = @($checks | Where-Object { $_.name -match 'pair results|preflight error|complete virtual filesystem|cumulative read count|cumulative write count|denied preflight leaves' })
    $behaviorPassed = $caseChecks.Count -eq 12 * 4 + @($oracle.cases | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.expectedError) }).Count -and @($caseChecks | Where-Object { -not $_.passed }).Count -eq 0
    $script:passed = $behaviorPassed -and $actorUnchanged -and @($checks | Where-Object { -not $_.passed }).Count -eq 0
}
catch {
    $failure = $_.Exception.ToString() + "`n" + $_.InvocationInfo.PositionMessage + "`n" + $_.ScriptStackTrace
    Add-Check 'scorer completed without a runtime or harness failure' $false $_.Exception.Message
}
finally {
    if ($null -ne $script:actorRoot -and (Test-Path -LiteralPath $script:actorRoot -PathType Container)) {
        $actorAfter = Get-Inventory $script:actorRoot
        $actorSourceAfter = Get-Inventory $script:actorRoot -ExcludeBuildOutput
    }
    $caseChecks = @($checks | Where-Object { $_.name -match 'pair results|preflight error|complete virtual filesystem|cumulative read count|cumulative write count|denied preflight leaves' })
    $expectedCases = if ($null -ne $oracle) { @($oracle.cases).Count } else { 0 }
    $caseFailureCount = @($caseChecks | Where-Object { -not $_.passed }).Count
    $behaviorScore = [ordered]@{
        passedCases = if ($expectedCases -gt 0) { @($caseEvidence | Where-Object {
            $probe = $_
            $case = @($oracle.cases | Where-Object name -ceq $probe.name)[0]
            $expectedError = [string]$case.expectedError
            $resultPass = if ([string]::IsNullOrWhiteSpace($expectedError)) {
                $expectedCalls = @()
                for ($i = 0; $i -lt [int]$case.repeat; $i++) { $expectedCalls += [ordered]@{ first = [string]$case.expected[0]; second = [string]$case.expected[1] } }
                (Get-CanonicalJson $probe.resultCalls) -ceq (Get-CanonicalJson $expectedCalls)
            } else { $probe.errorCode -ceq $expectedError -and [int]$probe.reads -eq 0 -and [int]$probe.writes -eq 0 }
            $resultPass -and (Get-CanonicalMap $probe.state) -ceq (Get-CanonicalMap $case.expectedState) -and [int]$probe.reads -eq [int]$case.reads -and [int]$probe.writes -eq [int]$case.writes
        }).Count } else { 0 }
        totalCases = $expectedCases
        failedChecks = $caseFailureCount
        passed = ($expectedCases -gt 0 -and $caseFailureCount -eq 0)
    }
    $allPassed = ($passed -and $caseFailureCount -eq 0)
    $evidence = [ordered]@{
        schemaVersion = 1
        language = $Language
        actorProject = $actorRoot
        actorProjectFile = $actorProjectFile
        oracle = $oraclePath
        oracleSha256 = if ($oraclePath -and (Test-Path -LiteralPath $oraclePath)) { (Get-FileHash -LiteralPath $oraclePath -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
        runtimePin = $runtimePin
        behavior = $behaviorScore
        passed = $allPassed
        failure = $failure
        actorTests = $actorTestEvidence
        checks = $checks.ToArray()
        cases = $caseEvidence.ToArray()
        processes = $processes.ToArray()
        actorInventoryBefore = $actorBefore
        actorInventoryAfter = $actorAfter
        actorSourceInventoryBefore = $actorSourceBefore
        actorSourceInventoryAfter = $actorSourceAfter
        actorUnchanged = ((Get-CanonicalJson $actorBefore) -ceq (Get-CanonicalJson $actorAfter))
        limits = @('Flow virtual filesystem state is read from the hash-verified state object in the result snapshot.', 'F# behavior is measured through the public provider/API in a fresh adapter process; runtime effect parity is not claimed.')
    }
    if ($script:outputFull) {
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($script:outputFull))
        [IO.File]::WriteAllText($script:outputFull, (ConvertTo-Json $evidence -Depth 100), [Text.UTF8Encoding]::new($false))
    }
    $scratchFull = [IO.Path]::GetFullPath($scratchRoot)
    $scoringPrefix = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + [IO.Path]::DirectorySeparatorChar
    if ($scratchFull.StartsWith($scoringPrefix, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $scratchFull)) {
        Remove-Item -LiteralPath $scratchFull -Recurse -Force
    }
}

if ($failure) { throw "Pair scoring failed to complete for ${Language}: $failure. Evidence: $OutputPath" }
if ($RequireFullPass -and (-not $passed -or @($checks | Where-Object { -not $_.passed }).Count -gt 0)) {
    throw "Pair score ${Language} did not satisfy the full oracle. Evidence: $OutputPath"
}
$passedCases = if ($behaviorScore) { [int]$behaviorScore.passedCases } else { 0 }
$totalCases = if ($behaviorScore) { [int]$behaviorScore.totalCases } else { 0 }
Write-Output "Pair score for ${Language}: $passedCases/$totalCases cases pass. Full acceptance: $passed. Evidence: $OutputPath"
