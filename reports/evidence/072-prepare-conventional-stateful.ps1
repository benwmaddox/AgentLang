#requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$sourceHead = (& git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceHead -notmatch '^[0-9a-fA-F]{40}$') {
    throw "Could not resolve the source revision: $sourceHead"
}

$localRoot = Join-Path $repo '.agentlang/stateful-conventional-001'
$seedProject = Join-Path $localRoot 'seed-project'
$actorProject = Join-Path $localRoot 'actor-01'
$runRoot = Join-Path $repo 'experiments/AgentLang.SubagentTrials/stateful-conventional-001/runs/conventional-001'
$startingProject = Join-Path $runRoot 'starting-project'
$startingStatePath = Join-Path $runRoot 'starting-state.json'
$pathsThatMustBeFresh = @($seedProject, $actorProject, $runRoot)
if ($pathsThatMustBeFresh | Where-Object { Test-Path -LiteralPath $_ }) {
    throw 'Refusing to overwrite existing conventional stateful trial files.'
}

$utf8 = [Text.UTF8Encoding]::new($false)
$projectFiles = [ordered]@{
    'StatefulPilot.fsproj' = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Domain.fs" />
    <Compile Include="Operations.fs" />
    <Compile Include="SelfTests.fs" />
  </ItemGroup>
</Project>
'@
    'Domain.fs' = @'
namespace AgentLang.StatefulPilot

open System

/// A nominal invoice identifier backed by text.
type InvoiceId = InvoiceId of string

/// A nominal invoice status backed by text.
type InvoiceStatus = InvoiceStatus of string

/// The invoice value used by the reminder operation.
type Invoice =
    { Id: InvoiceId
      Status: InvoiceStatus }

/// Helpers for invoice values.
module Invoice =
    /// Builds the relative virtual path used for an invoice reminder.
    let reminderPath (InvoiceId id) : string =
        "outbox/invoice-reminders/" + id

/// The explicit file provider used by reminder operations.
type IReminderFiles =
    abstract member Exists: path: string -> bool
    abstract member Read: path: string -> string
    abstract member Write: path: string * contents: string -> unit

/// An in-memory file provider with inspectable state and operation counts.
type VirtualFiles(initial: Map<string, string>) =
    let mutable state = initial
    let mutable readCount = 0
    let mutable writeCount = 0

    /// A persistent snapshot of the current virtual files.
    member _.State: Map<string, string> = state

    /// The number of existence and read operations since the last reset.
    member _.ReadCount: int = readCount

    /// The number of write operations since the last reset.
    member _.WriteCount: int = writeCount

    /// Resets operation counts without changing virtual file state.
    member _.ResetCounts() =
        readCount <- 0
        writeCount <- 0

    interface IReminderFiles with
        member _.Exists path =
            readCount <- readCount + 1
            Map.containsKey path state

        member _.Read path =
            readCount <- readCount + 1
            Map.find path state

        member _.Write(path, contents) =
            writeCount <- writeCount + 1
            state <- Map.add path contents state

/// Runs an operation only when both virtual file capabilities are granted.
module Execution =
    /// Preflights read and write access before invoking the supplied operation.
    let run
        (allowRead: bool)
        (allowWrite: bool)
        (files: IReminderFiles)
        (invoice: Invoice)
        (operation: IReminderFiles -> Invoice -> string)
        : string =
        if not (allowRead && allowWrite) then
            raise (InvalidOperationException("CAPABILITY_DENIED"))

        operation files invoice
'@
    'Operations.fs' = @'
namespace AgentLang.StatefulPilot

module ReminderOperations =
    let queueOnce (files: IReminderFiles) (invoice: Invoice) : string =
        failwith "Not implemented"
'@
    'SelfTests.fs' = @'
namespace AgentLang.StatefulPilot

module SelfTests =
    let private assertEqual expected actual =
        if actual <> expected then
            failwithf "Expected %A but received %A" expected actual

    // <immutable-seed-tests>
    let runSeedTests () =
        assertEqual
            "outbox/invoice-reminders/INV-001"
            (Invoice.reminderPath (InvoiceId "INV-001"))
        assertEqual
            "outbox/invoice-reminders/inv.alpha_2"
            (Invoice.reminderPath (InvoiceId "inv.alpha_2"))
        printfn "Seed helper tests passed: 2"
    // </immutable-seed-tests>

    // Add operation tests and the runnable example here, then invoke them below.
    let runOwnTests () =
        ()

    [<EntryPoint>]
    let main _ =
        runSeedTests ()
        runOwnTests ()
        0
'@
}

function Write-Project($ProjectRoot) {
    [IO.Directory]::CreateDirectory($ProjectRoot) | Out-Null
    foreach ($relativePath in $projectFiles.Keys) {
        $destination = Join-Path $ProjectRoot $relativePath
        [IO.File]::WriteAllText($destination, [string]$projectFiles[$relativePath], $utf8)
    }
}

function Copy-Project($Source, $Destination) {
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    foreach ($sourceFile in [IO.Directory]::EnumerateFiles($Source, '*', [IO.SearchOption]::AllDirectories)) {
        $relativePath = [IO.Path]::GetRelativePath($Source, $sourceFile)
        $destinationFile = Join-Path $Destination $relativePath
        [IO.Directory]::CreateDirectory((Split-Path -Parent $destinationFile)) | Out-Null
        [IO.File]::Copy($sourceFile, $destinationFile, $false)
    }
}

function Get-Inventory($Path) {
    @(
        Get-ChildItem -LiteralPath $Path -File -Recurse -Force |
            Sort-Object { [IO.Path]::GetRelativePath($Path, $_.FullName) } |
            ForEach-Object {
                [ordered]@{
                    path = [IO.Path]::GetRelativePath($Path, $_.FullName).Replace('\', '/')
                    bytes = $_.Length
                    sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                }
            }
    )
}

function Convert-Inventory($Inventory) {
    ConvertTo-Json -InputObject @($Inventory) -Depth 20 -Compress
}

[IO.Directory]::CreateDirectory((Split-Path -Parent $seedProject)) | Out-Null
[IO.Directory]::CreateDirectory((Split-Path -Parent $runRoot)) | Out-Null
Write-Project $seedProject
Copy-Project $seedProject $actorProject
Copy-Project $seedProject $startingProject

$seedInventory = Get-Inventory $seedProject
$actorInventory = Get-Inventory $actorProject
$startingInventory = Get-Inventory $startingProject
if ((Convert-Inventory $seedInventory) -cne (Convert-Inventory $actorInventory) -or
    (Convert-Inventory $seedInventory) -cne (Convert-Inventory $startingInventory)) {
    throw 'The actor or archived starting project differs from the fixture seed.'
}
foreach ($inventory in @($seedInventory, $actorInventory, $startingInventory)) {
    if (@($inventory | Where-Object { $_.path -match '(^|/)(bin|obj)(/|$)' }).Count -gt 0) {
        throw 'Fixture project inventories must not include build output.'
    }
}

$startingState = [ordered]@{
    schemaVersion = 1
    sourceHead = $sourceHead
    seedProjectPath = [IO.Path]::GetRelativePath($repo, $seedProject).Replace('\', '/')
    actorProjectPath = [IO.Path]::GetRelativePath($repo, $actorProject).Replace('\', '/')
    startingProjectPath = [IO.Path]::GetRelativePath($repo, $startingProject).Replace('\', '/')
    seedProjectFiles = $seedInventory
    actorProjectFiles = $actorInventory
    startingProjectFiles = $startingInventory
}
$manifestJson = ConvertTo-Json -InputObject $startingState -Depth 100
[IO.File]::WriteAllText($startingStatePath, $manifestJson + [Environment]::NewLine, $utf8)
Write-Output 'Prepared byte-identical conventional reminder projects; no build or actor was launched.'
