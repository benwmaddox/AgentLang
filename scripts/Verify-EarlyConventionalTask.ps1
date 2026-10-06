#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ProjectPath,
    [Parameter(Mandatory)][ValidateRange(1,5)][int]$Task,
    [Parameter(Mandatory)][string]$EvidencePath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = (Resolve-Path -LiteralPath $ProjectPath).Path
$targetProject = Join-Path $project 'EarlyPilot.fsproj'
if (-not (Test-Path $targetProject -PathType Leaf)) { throw 'Missing EarlyPilot.fsproj.' }
$oraclePath = Join-Path $repo 'experiments/AgentLang.SubagentTrials/early-flow-001/acceptance.json'
$oracle = Get-Content $oraclePath -Raw | ConvertFrom-Json
$selected = @($oracle.tasks | Where-Object number -eq $Task)
if ($oracle.schemaVersion -ne 1 -or $selected.Count -ne 1 -or $selected[0].cases.Count -ne 20) { throw 'Unexpected oracle shape.' }
$target = $selected[0]
$adapter = Join-Path $repo ('.agentlang/early-conventional-acceptance-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($adapter)
$escapedProject = [Security.SecurityElement]::Escape($targetProject)
$adapterProject = "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net9.0</TargetFramework><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup><ItemGroup><ProjectReference Include=`"$escapedProject`"/><Compile Include=`"Program.fs`"/></ItemGroup></Project>"
[IO.File]::WriteAllText((Join-Path $adapter 'Acceptance.fsproj'),$adapterProject,[Text.UTF8Encoding]::new($false))
$program = @'
module IndependentAcceptance
open System
open System.Text.Json
open System.Text.Json.Nodes
open AgentLang.EarlyPilot
[<EntryPoint>]
let main _ =
    let mutable source = Console.ReadLine()
    while not (isNull source) do
        let output = JsonObject()
        try
            use input = JsonDocument.Parse(source)
            let root = input.RootElement
            let customer : Customer = { Kind = root.GetProperty("kind").GetString(); Balance = root.GetProperty("balance").GetDouble() }
            let subscription : Subscription = { Term = root.GetProperty("term").GetString(); Renewable = root.GetProperty("renewable").GetBoolean() }
            let value : JsonNode =
                match root.GetProperty("task").GetInt32() with
                | 1 -> JsonValue.Create(Customer.premium customer)
                | 2 -> JsonValue.Create(Customer.discountedBalance customer)
                | 3 -> JsonValue.Create(Subscription.annualRenewable subscription)
                | 4 -> JsonValue.Create(Customer.renewalBalance customer subscription)
                | 5 -> JsonValue.Create(Customer.renewalSavings customer subscription)
                | _ -> invalidArg "task" "Unknown task."
            output["value"] <- value
            output["ok"] <- JsonValue.Create(true)
        with error ->
            output["ok"] <- JsonValue.Create(false)
            output["error"] <- JsonValue.Create(error.Message)
        Console.WriteLine(output.ToJsonString())
        source <- Console.ReadLine()
    0
'@
[IO.File]::WriteAllText((Join-Path $adapter 'Program.fs'),$program,[Text.UTF8Encoding]::new($false))
$checks = [Collections.Generic.List[object]]::new()
$responses = @()
$buildOutput = @()
$failure = $null
$passed = $false
function Check([string]$Name,[bool]$Condition) {
    $checks.Add([ordered]@{name=$Name;passed=$Condition})
    if (-not $Condition) { throw "Conventional acceptance failed: $Name" }
}
$requests = @($target.cases | ForEach-Object { [ordered]@{task=$Task;kind=$_.kind;balance=$_.balance;term=$_.term;renewable=$_.renewable} })
try {
    $buildOutput = @(& dotnet build (Join-Path $adapter 'Acceptance.fsproj') --configuration Release --nologo 2>&1 | ForEach-Object { "$_" })
    Check 'fresh adapter and target build' ($LASTEXITCODE -eq 0)
    $lines = @($requests | ForEach-Object { ConvertTo-Json $_ -Compress })
    $output = @($lines | & dotnet (Join-Path $adapter 'bin/Release/net9.0/Acceptance.dll'))
    Check 'acceptance process exit' ($LASTEXITCODE -eq 0)
    Check 'one response per input' ($output.Count -eq $requests.Count)
    $responses = @($output | ForEach-Object { ConvertFrom-Json $_ })
    for ($index=0; $index -lt $target.cases.Count; $index++) {
        $case = $target.cases[$index]
        $response = $responses[$index]
        Check "$($case.name) target executes" ([bool]$response.ok)
        if ($target.output -ceq 'Bool') {
            Check "$($case.name) independent Bool result" ($response.value -is [bool] -and $response.value -eq [bool]$case.expected)
        } else {
            $actual = [Convert]::ToDouble($response.value,[Globalization.CultureInfo]::InvariantCulture)
            $expected = [double]$case.expected
            $tolerance = [Math]::Max([double]$oracle.tolerance.minimumAbsolute,[Math]::Abs($expected)*[double]$oracle.tolerance.relative)
            Check "$($case.name) independent Float result" ($response.value -is [ValueType] -and $response.value -isnot [bool] -and [double]::IsFinite($actual) -and [Math]::Abs($actual-$expected) -le $tolerance)
        }
    }
    $passed = $true
} catch { $failure = $_.Exception.Message }
finally {
    $evidence = [ordered]@{schemaVersion=1;passed=$passed;failure=$failure;task=$Task;project=$project;adapter=$adapter;oracleSha256=(Get-FileHash $oraclePath -Algorithm SHA256).Hash.ToLowerInvariant();verifierSha256=(Get-FileHash $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant();buildOutput=$buildOutput;checks=$checks.ToArray();requests=$requests;responses=$responses;limits=@('Independent behavioral outputs only; agent self-tests and state preservation require separate checks.','Conventional F# execution is not an OS sandbox.','No agent performance claim.')}
    $evidenceTarget=[IO.Path]::GetFullPath($EvidencePath,$repo)
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($evidenceTarget))
    [IO.File]::WriteAllText($evidenceTarget,(ConvertTo-Json $evidence -Depth 64),[Text.UTF8Encoding]::new($false))
}
if (-not $passed) { throw $failure }
Write-Output "$($checks.Count) conventional task-$Task acceptance checks passed. Evidence: $evidenceTarget"
