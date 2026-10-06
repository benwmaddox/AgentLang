#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ProjectPath,
    [Parameter(Mandatory)][string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$seed = Join-Path $repo 'experiments/AgentLang.SubagentTrials/stateful-conventional-001/runs/conventional-001/starting-project'
$project = (Resolve-Path -LiteralPath $ProjectPath).Path
$seedProject = Join-Path $seed 'StatefulPilot.fsproj'
$targetProject = Join-Path $project 'StatefulPilot.fsproj'
$scratchRoot = Join-Path $repo ('.agentlang/stateful-conventional-acceptance-' + [guid]::NewGuid().ToString('N'))
$checks = [Collections.Generic.List[object]]::new()
$processes = [Collections.Generic.List[object]]::new()
$passed = $false
$failure = $null
$seedBefore = @()
$projectBefore = @()
$snapshotPath = Join-Path $scratchRoot 'after-first-reminder.json'

function Add-Check([string]$Name, [bool]$Condition, [string]$Details = '') {
    $script:checks.Add([ordered]@{ name = $Name; passed = $Condition; details = $Details })
    if (-not $Condition) { throw "Conventional stateful acceptance failed: $Name. $Details" }
}

function Get-Inventory([string]$Root) {
    $items = [Collections.Generic.List[object]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $Root -File -Recurse -Force) {
        $relative = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        if ($relative -match '(^|/)(bin|obj|\.git)(/|$)') { continue }
        $items.Add([ordered]@{
            path = $relative
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        })
    }
    return ,@($items | Sort-Object path)
}

function Copy-Project([string]$From, [string]$To) {
    [void][IO.Directory]::CreateDirectory($To)
    foreach ($file in Get-ChildItem -LiteralPath $From -File -Recurse -Force) {
        $relative = [IO.Path]::GetRelativePath($From, $file.FullName).Replace('\', '/')
        if ($relative -match '(^|/)(bin|obj|\.git)(/|$)') { continue }
        $target = Join-Path $To ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
        [IO.File]::Copy($file.FullName, $target, $true)
    }
}

function Get-BytesEqual([string]$Left, [string]$Right) {
    [Convert]::ToBase64String([IO.File]::ReadAllBytes($Left)) -ceq [Convert]::ToBase64String([IO.File]::ReadAllBytes($Right))
}

function Get-FrozenSeedSection([string]$Path) {
    $text = [IO.File]::ReadAllText($Path, [Text.UTF8Encoding]::new($false, $true))
    $pattern = '(?ms)^[ \t]*// <immutable-seed-tests>\r?\n(?<body>.*?)^[ \t]*// </immutable-seed-tests>(?:\r?\n|$)'
    $matches = [regex]::Matches($text, $pattern)
    if ($matches.Count -ne 1) { throw "Expected exactly one immutable seed test section in $Path." }
    $entryIndex = $text.IndexOf('[<EntryPoint>]', [StringComparison]::Ordinal)
    if ($entryIndex -lt 0) { throw "Missing frozen entry point in $Path." }
    [pscustomobject]@{
        Text = $text
        Body = $matches[0].Groups['body'].Value
        Prefix = $text.Substring(0, $matches[0].Index)
        EntryPoint = $text.Substring($entryIndex)
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
    foreach ($argument in $Arguments) { [void]$startInfo.ArgumentList.Add($argument) }

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
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        return [pscustomobject]@{
            exitCode = if ($exited) { $process.ExitCode } else { $null }
            timedOut = (-not $exited)
            launchError = $null
            stdout = $stdout
            stderr = $stderr
        }
    } catch {
        return [pscustomobject]@{ exitCode = $null; timedOut = $false; launchError = $_.Exception.Message; stdout = ''; stderr = '' }
    } finally {
        $process.Dispose()
    }
}

function Invoke-DotNet([string]$Purpose, [string[]]$Arguments, [string]$WorkingDirectory, [int]$TimeoutMilliseconds = 180000) {
    $result = Invoke-BoundedProcess 'dotnet' $Arguments $WorkingDirectory $TimeoutMilliseconds
    $script:processes.Add([ordered]@{
        purpose = $Purpose
        arguments = @($Arguments)
        workingDirectory = $WorkingDirectory
        exitCode = $result.exitCode
        timedOut = $result.timedOut
        launchError = $result.launchError
        stdout = if ($result.stdout.Length -gt 24000) { $result.stdout.Substring(0, 24000) } else { $result.stdout }
        stderr = if ($result.stderr.Length -gt 24000) { $result.stderr.Substring(0, 24000) } else { $result.stderr }
    })
    return $result
}

function Convert-AdapterJson([object]$ProcessResult, [string]$Purpose) {
    if ($ProcessResult.timedOut -or $ProcessResult.exitCode -ne 0 -or $ProcessResult.launchError) {
        throw "$Purpose failed: exit=$($ProcessResult.exitCode), timeout=$($ProcessResult.timedOut), launch=$($ProcessResult.launchError), stderr=$($ProcessResult.stderr)"
    }
    $lines = @($ProcessResult.stdout -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($lines.Count -ne 1) { throw "$Purpose emitted $($lines.Count) non-empty lines; expected one JSON response." }
    try { return (ConvertFrom-Json -InputObject $lines[0] -AsHashtable) }
    catch { throw "$Purpose returned invalid JSON: $($_.Exception.Message)" }
}

function New-ReferenceProject([string]$Directory, [string]$ProjectReference, [string]$AssemblyName, [string]$OutputType = 'Exe') {
    [void][IO.Directory]::CreateDirectory($Directory)
    $escapedProject = [Security.SecurityElement]::Escape($ProjectReference)
    $projectText = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>$OutputType</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AssemblyName>$AssemblyName</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$escapedProject" />
    <Compile Include="Program.fs" />
  </ItemGroup>
</Project>
"@
    [IO.File]::WriteAllText((Join-Path $Directory "$AssemblyName.fsproj"), $projectText, [Text.UTF8Encoding]::new($false))
    return (Join-Path $Directory "$AssemblyName.fsproj")
}

$adapterSource = @'
namespace IndependentAcceptance

open System
open System.Collections.Generic
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open AgentLang.StatefulPilot

type Probe =
    { Name: string
      Path: string
      Result: string
      Error: string
      ReadCount: int
      WriteCount: int
      State: Map<string, string> }

module Program =
    let private makeInvoice id status : Invoice =
        { Id = InvoiceId id
          Status = InvoiceStatus status }

    let private asDictionary (state: Map<string, string>) =
        let dictionary = Dictionary<string, string>(StringComparer.Ordinal)
        for KeyValue(key, value) in state do dictionary.Add(key, value)
        dictionary

    let private makeProbe (name: string) (id: string) (status: string) (files: VirtualFiles) (allowRead: bool) (allowWrite: bool) =
        files.ResetCounts()
        let path = Invoice.reminderPath (InvoiceId id)
        try
            let result =
                Execution.run allowRead allowWrite files (makeInvoice id status) ReminderOperations.queueOnce
            { Name = name
              Path = path
              Result = result
              Error = ""
              ReadCount = files.ReadCount
              WriteCount = files.WriteCount
              State = files.State }
        with error ->
            { Name = name
              Path = path
              Result = ""
              Error = error.GetType().Name + ":" + error.Message
              ReadCount = files.ReadCount
              WriteCount = files.WriteCount
              State = files.State }

    let private probeNode (probe: Probe) : JsonNode =
        let node = JsonObject()
        node["name"] <- JsonValue.Create<string>(probe.Name)
        node["path"] <- JsonValue.Create<string>(probe.Path)
        node["result"] <- JsonValue.Create<string>(probe.Result)
        node["error"] <- if String.IsNullOrEmpty(probe.Error) then null else JsonValue.Create<string>(probe.Error) :> JsonNode
        node["readCount"] <- JsonValue.Create(probe.ReadCount)
        node["writeCount"] <- JsonValue.Create(probe.WriteCount)
        let state = JsonObject()
        for KeyValue(key, value) in probe.State do state[key] <- JsonValue.Create(value)
        node["state"] <- state
        node :> JsonNode

    let private emit (processMode: string) (helperPaths: Map<string, string>) (probes: Probe list) =
        let root = JsonObject()
        root["mode"] <- JsonValue.Create<string>(processMode)
        root["processId"] <- JsonValue.Create(Environment.ProcessId)
        let paths = JsonObject()
        for KeyValue(key, value) in helperPaths do paths[key] <- JsonValue.Create(value)
        root["helperPaths"] <- paths
        let items = JsonArray()
        for probe in probes do items.Add(probeNode probe)
        root["probes"] <- items
        Console.WriteLine(root.ToJsonString())

    let private runProbe snapshotPath =
        let id1 = "conv-open-001"
        let id2 = "conv-open-002"
        let customId = "conv-custom-001"
        let emptyId = "conv-empty-marker-001"
        let customMarker = "held for manual review"
        let path1 = Invoice.reminderPath (InvoiceId id1)
        let path2 = Invoice.reminderPath (InvoiceId id2)
        let customPath = Invoice.reminderPath (InvoiceId customId)
        let emptyPath = Invoice.reminderPath (InvoiceId emptyId)

        let openFiles = VirtualFiles(Map.empty)
        let first = makeProbe "new-open" id1 "open" openFiles true true
        File.WriteAllText(snapshotPath, JsonSerializer.Serialize(asDictionary openFiles.State), Text.UTF8Encoding(false))
        let repeat = makeProbe "repeat" id1 "open" openFiles true true
        let secondId = makeProbe "second-id" id2 "open" openFiles true true

        let customFiles = VirtualFiles(Map.ofList [ customPath, customMarker; "unrelated/keep", "untouched" ])
        let custom = makeProbe "custom-marker" customId "open" customFiles true true
        let emptyFiles = VirtualFiles(Map.ofList [ emptyPath, "" ])
        let emptyMarker = makeProbe "empty-marker" emptyId "open" emptyFiles true true

        let nonOpenFiles = VirtualFiles(Map.empty)
        let paid = makeProbe "paid" "conv-paid-001" "paid" nonOpenFiles true true
        let cancelled = makeProbe "cancelled" "conv-cancelled-001" "cancelled" nonOpenFiles true true
        let uppercase = makeProbe "uppercase" "conv-uppercase-001" "OPEN" nonOpenFiles true true

        let deniedFiles = VirtualFiles(Map.empty)
        let denied = makeProbe "denied-no-capabilities" "conv-denied-001" "open" deniedFiles false false
        let deniedRead = makeProbe "denied-no-read" "conv-denied-read-001" "open" (VirtualFiles(Map.empty)) false true
        let deniedWrite = makeProbe "denied-no-write" "conv-denied-write-001" "open" (VirtualFiles(Map.empty)) true false
        let nonOpenDenied = makeProbe "non-open-denied" "conv-denied-paid-001" "paid" (VirtualFiles(Map.empty)) false false
        let helperPaths =
            [ "first", path1
              "second", path2
              "custom", customPath
              "empty", emptyPath ]
            |> Map.ofList
        emit "probe" helperPaths [ first; repeat; secondId; custom; emptyMarker; paid; cancelled; uppercase; denied; deniedRead; deniedWrite; nonOpenDenied ]

    let private runRestore snapshotPath =
        let json = File.ReadAllText(snapshotPath)
        let restored = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
        if isNull restored then failwith "Could not restore snapshot JSON."
        let state = restored |> Seq.map (fun pair -> pair.Key, pair.Value) |> Map.ofSeq
        let files = VirtualFiles(state)
        let id = "conv-open-001"
        let probe = makeProbe "snapshot-repeat" id "open" files true true
        let helperPaths = [ "restored", Invoice.reminderPath (InvoiceId id) ] |> Map.ofList
        emit "restore" helperPaths [ probe ]

    [<EntryPoint>]
    let main arguments =
        match arguments with
        | [| "probe"; snapshotPath |] -> runProbe snapshotPath; 0
        | [| "restore"; snapshotPath |] -> runRestore snapshotPath; 0
        | _ -> eprintfn "Expected: Acceptance.dll probe|restore SNAPSHOT.json"; 64
'@

try {
    Add-Check 'frozen seed and actor project are separate' (([IO.Path]::GetFullPath($seed) -ine $project) -and (Test-Path -LiteralPath $seedProject -PathType Leaf) -and (Test-Path -LiteralPath $targetProject -PathType Leaf)) $project
    $outputTarget = [IO.Path]::GetFullPath($OutputPath, $repo)
    $projectPrefix = $project.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $seedPrefix = [IO.Path]::GetFullPath($seed).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    Add-Check 'evidence path is outside the source and frozen seed' (-not $outputTarget.StartsWith($projectPrefix, [StringComparison]::OrdinalIgnoreCase) -and -not $outputTarget.StartsWith($seedPrefix, [StringComparison]::OrdinalIgnoreCase)) $outputTarget

    $seedBefore = Get-Inventory $seed
    $projectBefore = Get-Inventory $project
    $seedSelfTests = Get-FrozenSeedSection (Join-Path $seed 'SelfTests.fs')
    $projectSelfTests = Get-FrozenSeedSection (Join-Path $project 'SelfTests.fs')
    Add-Check 'Domain.fs remains byte-identical to the seed' (Get-BytesEqual (Join-Path $seed 'Domain.fs') (Join-Path $project 'Domain.fs'))
    Add-Check 'StatefulPilot.fsproj remains byte-identical to the seed' (Get-BytesEqual $seedProject $targetProject)
    Add-Check 'frozen seed self-test prefix and implementation are retained byte-for-byte' ($seedSelfTests.Prefix -ceq $projectSelfTests.Prefix -and $seedSelfTests.Body -ceq $projectSelfTests.Body) 'marker-delimited helper test body'
    Add-Check 'entry point still invokes both frozen and actor test hooks in order' ($seedSelfTests.EntryPoint -ceq $projectSelfTests.EntryPoint -and $projectSelfTests.EntryPoint -match '(?s)runSeedTests\s*\(\s*\).*?runOwnTests\s*\(\s*\).*?\b0\b')

    $changedSeedFiles = @($seedBefore | Where-Object { $_.path -notin @('Operations.fs', 'SelfTests.fs') } | ForEach-Object {
        $seedItem = $_
        $other = @($projectBefore | Where-Object path -ceq $seedItem.path)
        if ($other.Count -ne 1 -or $other[0].sha256 -cne $seedItem.sha256) { $seedItem.path }
    })
    $addedFiles = @($projectBefore | Where-Object { $_.path -notin @($seedBefore.path) } | ForEach-Object path)
    $removedFiles = @($seedBefore | Where-Object { $_.path -notin @($projectBefore.path) } | ForEach-Object path)
    Add-Check 'only Operations.fs and SelfTests.fs may change from the seed' ($changedSeedFiles.Count -eq 0 -and $addedFiles.Count -eq 0 -and $removedFiles.Count -eq 0) "changed=$($changedSeedFiles -join ','); added=$($addedFiles -join ','); removed=$($removedFiles -join ',')"

    $operationSource = [IO.File]::ReadAllText((Join-Path $project 'Operations.fs'), [Text.UTF8Encoding]::new($false, $true))
    $docPattern = '(?m)^\s*///\s*[^\r\n]+(?:\r?\n\s*///\s*[^\r\n]+)*\r?\n\s*let\s+queueOnce\b'
    Add-Check 'queueOnce has adjacent XML documentation' ([regex]::IsMatch($operationSource, $docPattern))
    Add-Check 'IndependentAcceptance output directory exists after setup' ([IO.Directory]::CreateDirectory($scratchRoot) -ne $null)

    $sourceCopy = Join-Path $scratchRoot 'actor-project'
    Copy-Project $project $sourceCopy
    $copyProject = Join-Path $sourceCopy 'StatefulPilot.fsproj'
    $ownRun = Invoke-DotNet 'fresh copied-project console tests and example' @('run', '--project', $copyProject, '--configuration', 'Release', '--no-launch-profile') $sourceCopy 240000
    Add-Check 'fresh copied-project self-tests and example exit successfully' (-not $ownRun.timedOut -and $ownRun.exitCode -eq 0 -and -not $ownRun.launchError) "timeout=$($ownRun.timedOut); exit=$($ownRun.exitCode); launch=$($ownRun.launchError); stderr=$($ownRun.stderr)"
    Add-Check 'frozen helper tests run in the fresh process' ($ownRun.stdout -match '(?m)^Seed helper tests passed: 2\s*$')
    Add-Check 'four actor-owned tests report success' ($ownRun.stdout -match '(?m)^OWN_TESTS_PASSED=4\s*$')
    Add-Check 'runnable example reports the queued marker' ($ownRun.stdout -match '(?m)^EXAMPLE_QUEUE_REMINDER=queued\s*$')

    $adapterDirectory = Join-Path $scratchRoot 'acceptance-adapter'
    $adapterProject = New-ReferenceProject $adapterDirectory $copyProject 'Acceptance'
    [IO.File]::WriteAllText((Join-Path $adapterDirectory 'Program.fs'), $adapterSource, [Text.UTF8Encoding]::new($false))
    $adapterBuild = Invoke-DotNet 'fresh independent behavioral adapter build' @('build', $adapterProject, '--configuration', 'Release', '--nologo') $adapterDirectory 240000
    Add-Check 'independent behavioral adapter builds against the public typed API' (-not $adapterBuild.timedOut -and $adapterBuild.exitCode -eq 0 -and -not $adapterBuild.launchError) "timeout=$($adapterBuild.timedOut); exit=$($adapterBuild.exitCode); launch=$($adapterBuild.launchError); stderr=$($adapterBuild.stderr)"
    $adapterDll = Join-Path $adapterDirectory 'bin/Release/net9.0/Acceptance.dll'

    $probeProcess = Invoke-DotNet 'independent state, marker, effects, and denial probes' @($adapterDll, 'probe', $snapshotPath) $adapterDirectory 120000
    $probeResponse = Convert-AdapterJson $probeProcess 'Independent probe adapter'
    $probeByName = @{}
    foreach ($probe in $probeResponse.probes) { $probeByName[[string]$probe.name] = $probe }
    Add-Check 'seeded helper derives two distinct outbox paths' ($probeResponse.helperPaths.first -ceq 'outbox/invoice-reminders/conv-open-001' -and $probeResponse.helperPaths.second -ceq 'outbox/invoice-reminders/conv-open-002' -and $probeResponse.helperPaths.first -cne $probeResponse.helperPaths.second)
    $first = $probeByName['new-open']
    Add-Check 'new open invoice writes and returns queued with exact effects' ($first.result -ceq 'queued' -and $first.readCount -eq 1 -and $first.writeCount -eq 1 -and $first.state.Count -eq 1 -and $first.state['outbox/invoice-reminders/conv-open-001'] -ceq 'queued')
    $repeat = $probeByName['repeat']
    Add-Check 'repeat open invoice returns the existing queued marker' ($repeat.result -ceq 'queued' -and $repeat.readCount -eq 2 -and $repeat.writeCount -eq 0 -and $repeat.state.Count -eq 1 -and $repeat.state['outbox/invoice-reminders/conv-open-001'] -ceq 'queued')
    $second = $probeByName['second-id']
    Add-Check 'second open invoice gets an independent marker' ($second.result -ceq 'queued' -and $second.readCount -eq 1 -and $second.writeCount -eq 1 -and $second.state.Count -eq 2 -and $second.state['outbox/invoice-reminders/conv-open-001'] -ceq 'queued' -and $second.state['outbox/invoice-reminders/conv-open-002'] -ceq 'queued')
    $custom = $probeByName['custom-marker']
    Add-Check 'existing arbitrary marker and unrelated state are preserved' ($custom.result -ceq 'held for manual review' -and $custom.readCount -eq 2 -and $custom.writeCount -eq 0 -and $custom.state.Count -eq 2 -and $custom.state['outbox/invoice-reminders/conv-custom-001'] -ceq 'held for manual review' -and $custom.state['unrelated/keep'] -ceq 'untouched')
    $empty = $probeByName['empty-marker']
    Add-Check 'existing empty marker returns an empty string without rewriting' ($empty.result -ceq '' -and $empty.readCount -eq 2 -and $empty.writeCount -eq 0 -and $empty.state.Count -eq 1 -and $empty.state['outbox/invoice-reminders/conv-empty-marker-001'] -ceq '')
    foreach ($status in @('paid', 'cancelled', 'uppercase')) {
        $nonOpen = $probeByName[$status]
        Add-Check "non-open $status invoice has no provider effects" ($nonOpen.result -ceq 'not-open' -and $nonOpen.readCount -eq 0 -and $nonOpen.writeCount -eq 0 -and $nonOpen.state.Count -eq 0)
    }
    foreach ($name in @('denied-no-capabilities', 'denied-no-read', 'denied-no-write', 'non-open-denied')) {
        $denied = $probeByName[$name]
        Add-Check "$name fails before provider effects" ($denied.error -match '^InvalidOperationException:CAPABILITY_DENIED$' -and $denied.readCount -eq 0 -and $denied.writeCount -eq 0 -and $denied.state.Count -eq 0)
    }
    $snapshotState = Get-Content -LiteralPath $snapshotPath -Raw | ConvertFrom-Json -AsHashtable
    Add-Check 'snapshot JSON contains only the first marker' ($snapshotState.Count -eq 1 -and $snapshotState['outbox/invoice-reminders/conv-open-001'] -ceq 'queued')

    $restoreProcess = Invoke-DotNet 'fresh-process snapshot restore and repeat' @($adapterDll, 'restore', $snapshotPath) $adapterDirectory 120000
    $restoreResponse = Convert-AdapterJson $restoreProcess 'Fresh-process snapshot restore adapter'
    $restored = @($restoreResponse.probes)[0]
    Add-Check 'snapshot reload uses a separate adapter process' ($probeResponse.processId -ne $restoreResponse.processId -and $restoreResponse.mode -ceq 'restore') "probe pid=$($probeResponse.processId); restore pid=$($restoreResponse.processId)"
    Add-Check 'restored snapshot repeats queued marker with exact effects' ($restored.name -ceq 'snapshot-repeat' -and $restored.result -ceq 'queued' -and $restored.readCount -eq 2 -and $restored.writeCount -eq 0 -and $restored.state.Count -eq 1 -and $restored.state['outbox/invoice-reminders/conv-open-001'] -ceq 'queued')

    $negativeCases = @(
        [ordered]@{
            name = 'plain String rejected for Invoice'
            assembly = 'WrongInvoiceProbe'
            source = @'
namespace NegativeInvoiceProbe
module Probe =
    let run (files: AgentLang.StatefulPilot.IReminderFiles) =
        AgentLang.StatefulPilot.ReminderOperations.queueOnce files "not-an-invoice"
'@
        },
        [ordered]@{
            name = 'InvoiceId and InvoiceStatus are distinct nominal types'
            assembly = 'SwappedInvoiceProbe'
            source = @'
namespace SwappedInvoiceProbe
module Probe =
    let run () : AgentLang.StatefulPilot.Invoice =
        { Id = AgentLang.StatefulPilot.InvoiceStatus "paid"
          Status = AgentLang.StatefulPilot.InvoiceId "INV-typed" }
'@
        }
    )
    foreach ($negativeCase in $negativeCases) {
        $directory = Join-Path $scratchRoot ([string]$negativeCase.assembly)
        $negativeProject = New-ReferenceProject $directory $copyProject ([string]$negativeCase.assembly) 'Library'
        [IO.File]::WriteAllText((Join-Path $directory 'Program.fs'), [string]$negativeCase.source, [Text.UTF8Encoding]::new($false))
        $negativeBuild = Invoke-DotNet ([string]$negativeCase.name) @('build', $negativeProject, '--configuration', 'Release', '--nologo') $directory 240000
        $compilerOutput = [string]$negativeBuild.stdout + "`n" + [string]$negativeBuild.stderr
        Add-Check "$($negativeCase.name) fails only after the valid typed adapter built" (-not $negativeBuild.timedOut -and $negativeBuild.exitCode -ne 0 -and $compilerOutput -match '\berror\s+FS0001\b') "timeout=$($negativeBuild.timedOut); exit=$($negativeBuild.exitCode); launch=$($negativeBuild.launchError); output=$compilerOutput"
    }

    $seedAfter = Get-Inventory $seed
    $projectAfter = Get-Inventory $project
    Add-Check 'independent acceptance leaves the frozen seed unchanged' ((ConvertTo-Json $seedBefore -Depth 8 -Compress) -ceq (ConvertTo-Json $seedAfter -Depth 8 -Compress))
    Add-Check 'independent acceptance leaves the actor input inventory unchanged' ((ConvertTo-Json $projectBefore -Depth 8 -Compress) -ceq (ConvertTo-Json $projectAfter -Depth 8 -Compress))
    $passed = $true
} catch {
    $failure = $_.Exception.Message
} finally {
    $seedAfterFinal = @()
    $projectAfterFinal = @()
    if (Test-Path -LiteralPath $seed -PathType Container) { $seedAfterFinal = Get-Inventory $seed }
    if (Test-Path -LiteralPath $project -PathType Container) { $projectAfterFinal = Get-Inventory $project }
    $evidenceTarget = [IO.Path]::GetFullPath($OutputPath, $repo)
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($evidenceTarget))
    $evidence = [ordered]@{
        schemaVersion = 1
        passed = $passed
        failure = $failure
        project = $project
        seed = $seed
        output = $evidenceTarget
        snapshotPath = $snapshotPath
        verifierSha256 = if (Test-Path -LiteralPath $PSCommandPath) { (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
        checks = $checks.ToArray()
        processes = $processes.ToArray()
        seedInventoryBefore = $seedBefore
        seedInventoryAfter = $seedAfterFinal
        projectInventoryBefore = $projectBefore
        projectInventoryAfter = $projectAfterFinal
        limits = @('Fresh copied conventional project and in-memory provider only; this is not a filesystem sandbox.', 'F# self-test markers are runner evidence; independent behavior, effect-count, state, capability and nominal-type probes are separate.', 'F# instruction and branch coverage and AgentLang effect-runtime parity are not claimed.')
    }
    [IO.File]::WriteAllText($evidenceTarget, (ConvertTo-Json $evidence -Depth 32), [Text.UTF8Encoding]::new($false))
}

if (-not $passed) { throw "Conventional stateful acceptance failed: $failure. Evidence: $evidenceTarget" }
Write-Output "$(($checks | Measure-Object).Count) conventional stateful acceptance checks passed. Evidence: $evidenceTarget"
