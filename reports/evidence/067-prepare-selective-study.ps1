#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$revision = (& git -C $repo rev-parse HEAD).Trim()
if ($revision -ne '94a2d7f409eb7463654013e6a6bb3981ee0293a9') { throw 'Unexpected study runtime source.' }
$seed = Join-Path $repo 'experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/growing-task-3/final-project'
$run = Join-Path $repo 'experiments/AgentLang.SubagentTrials/selective-flow-001/runs/study-001'
$localRoot = Join-Path $repo '.agentlang/selective-flow-001'
$cli = Join-Path $localRoot 'language-bin/AgentLang.Cli.dll'
$hostPath = Join-Path $repo 'scripts/Start-SubagentTrialHost.ps1'
$utf8 = [Text.UTF8Encoding]::new($false, $true)
function SaveJson($Path, $Value) { [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 100), $utf8) }
function Hash($Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Inventory($Root) {
    @(Get-ChildItem -LiteralPath $Root -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
        [ordered]@{path=[IO.Path]::GetRelativePath($Root,$_.FullName);bytes=$_.Length;sha256=(Hash $_.FullName)}
    })
}
function CopySeed($Destination) {
    if (Test-Path -LiteralPath $Destination) { throw "Refusing to overwrite $Destination" }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Get-ChildItem -LiteralPath $seed -Force | Copy-Item -Destination $Destination -Recurse -Force
    $actual = Inventory $Destination
    if (($actual | ConvertTo-Json -Depth 10 -Compress) -cne ($seedInventory | ConvertTo-Json -Depth 10 -Compress)) { throw 'Seed copy differs.' }
    return $actual
}
if (Test-Path -LiteralPath $run) { throw 'Study already prepared; do not overwrite evidence.' }
New-Item -ItemType Directory -Path $run -Force | Out-Null
$seedInventory = Inventory $seed
$runtimeInventory = Inventory (Split-Path $cli -Parent)
$preflight = Join-Path $localRoot 'published-preflight'
$null = CopySeed $preflight
$requests = @(
    @{op='test-all'},
    @{op='describe';word='customer.premium?'},
    @{op='describe';word='customer.discounted-balance'},
    @{op='describe';word='subscription.annual-renewable?'},
    @{op='describe';word='customer.new'},
    @{op='describe';word='subscription.new'},
    @{op='context';word='customer.discounted-balance';maxDepth=4;maxWords=16;maxUtf8Bytes=6000},
    @{op='context';word='subscription.annual-renewable?';maxDepth=4;maxWords=16;maxUtf8Bytes=6000},
    @{op='words';compact=$true},
    @{op='search';query='customer'},
    @{op='search';query='subscription'},
    @{op='source';word='customer.discounted-balance'},
    @{op='source';word='subscription.annual-renewable?'},
    @{op='dependencies';word='customer.discounted-balance'},
    @{op='dependencies';word='subscription.annual-renewable?'}
)
$start = [Diagnostics.ProcessStartInfo]::new('dotnet')
$start.UseShellExecute=$false
$start.RedirectStandardInput=$true
$start.RedirectStandardOutput=$true
$start.RedirectStandardError=$true
$start.StandardOutputEncoding=$utf8
$start.StandardErrorEncoding=$utf8
foreach ($arg in @($cli,'--project',$preflight,'--jsonl')) { $start.ArgumentList.Add($arg) }
$process = [Diagnostics.Process]::Start($start)
$outputTask=$process.StandardOutput.ReadToEndAsync()
$errorTask=$process.StandardError.ReadToEndAsync()
foreach ($request in $requests) { $process.StandardInput.WriteLine(($request | ConvertTo-Json -Compress)) }
$process.StandardInput.Close()
if (-not $process.WaitForExit(60000)) { $process.Kill($true); throw 'Preflight process timed out.' }
$stdout=$outputTask.GetAwaiter().GetResult()
$stderr=$errorTask.GetAwaiter().GetResult()
if ($process.ExitCode -ne 0 -or $stderr.Length -gt 0) { throw "Preflight failed: $stderr" }
$frames=@($stdout.Split("`n") | Where-Object Length -gt 0)
if ($frames.Count -ne $requests.Count) { throw 'Preflight response count differs.' }
$responseObjects=@($frames | ForEach-Object { $_ | ConvertFrom-Json })
if (@($responseObjects | Where-Object { -not $_.ok }).Count -gt 0) { throw 'Preflight returned a diagnostic.' }
if (@($responseObjects[0].data.results).Count -ne 11 -or @($responseObjects[0].data.results | Where-Object { -not $_.passed }).Count -gt 0) { throw 'Seed tests changed.' }
foreach ($index in @(6,7)) {
    if ($responseObjects[$index].data.wordsOmitted -ne 0 -or $responseObjects[$index].data.typesOmitted -ne 0) { throw 'Needed context omitted entries.' }
}
$preflightEvidence=[ordered]@{schemaVersion=1;sourceRevision=$revision;runtimeFiles=$runtimeInventory;hostSha256=(Hash $hostPath);requests=$requests;responses=$responseObjects;frames=@($frames | ForEach-Object { $bytes=$utf8.GetBytes($_); [ordered]@{payloadUtf8Bytes=$bytes.Length;base64=[Convert]::ToBase64String($bytes);sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()} });exitCode=$process.ExitCode;stderr=$stderr;seedTests=11;inspectionCap=16000;capSelectedBeforeActors=$true}
SaveJson (Join-Path $run 'published-preflight.json') $preflightEvidence
[IO.File]::WriteAllText((Join-Path $run 'published-preflight.stdout.jsonl'),$stdout,$utf8)
$common = [IO.File]::ReadAllText((Join-Path $repo 'experiments/AgentLang.SubagentTrials/early-flow-002/common-instructions.md'))
$primer = [IO.File]::ReadAllText((Join-Path $repo 'experiments/AgentLang.SubagentTrials/selective-flow-001/language-primer.md'))
$tasks = [IO.File]::ReadAllText((Join-Path $repo 'experiments/AgentLang.SubagentTrials/early-flow-001/tasks.md'))
$task = [regex]::Match($tasks,'(?s)## 4\s+(.*?)\s+## 5').Groups[1].Value
$allowed=@('words','describe','search','source','dependencies','callers','eval','define','test','test-all','tests','commit','task.begin','task.commit','task.status','task.log','ir','context','failed-tests','examples','effects','type-of','search-type','search-output')
$modes=@('full','compact','compact','full')
for ($i=0;$i -lt 4;$i++) {
    $name=('actor-{0:d2}-{1}' -f ($i+1),$modes[$i])
    $artifact=Join-Path $run $name
    $project=Join-Path $localRoot $name
    New-Item -ItemType Directory -Path $artifact -Force | Out-Null
    $starting=CopySeed $project
    SaveJson (Join-Path $artifact 'starting-state.json') $starting
    $trace=Join-Path $artifact 'trace.jsonl'
    $allowedLiteral="@('"+($allowed -join "','")+"')"
    $launch="& '$hostPath' -CliDll '$cli' -ProjectPath '$project' -TracePath '$trace' -AllowedOperations $allowedLiteral -Profile agentlang -ExchangeTimeoutMilliseconds 120000 -MaxExchanges 100 -MaxInspectionResponseBytes 16000"
    $guidance=[IO.File]::ReadAllText((Join-Path $repo "experiments/AgentLang.SubagentTrials/selective-flow-001/$($modes[$i])-guidance.md"))
    $prompt=@"
Run selective-flow-001 study-001 $name as a fresh GPT-6 Luna/max coding agent with no inherited turns.
Your ownership is only the isolated trial project $project through its supplied protocol. You are not alone in this checkout; preserve all other agents' files. Do not touch repository source or sibling projects.

$common

Schema: Customer(kind:String,balance:Float) and Subscription(term:String,renewable:Bool). Preserve all prior accepted types, helpers and attached tests.

Selected public task:
$task

$primer

Assigned retrieval guidance:
$guidance

Execute exactly the following PowerShell host launch with exec_command, workdir D:\code\AgentLang, sandbox_permissions require_escalated, tty true, and a short initial yield. Host execution is required because of the known sandbox ACL helper failure. This local isolated experiment is authorized by the human user. Do not use shell or file tools for any other command or source access.

$launch

There is no startup banner. Send JSONL through write_stdin on that same returned session. Leave the host open at completion and report its session ID. No quit requests, no control characters, no subdelegation. A progress or blocker update may use collaboration.send_message to /root, never Codex app thread messaging. Report actual tests, errors and missing capabilities; do not invent token counts.
"@
    $prompt=$prompt.Replace("`r`n","`n")
    $promptPath=Join-Path $artifact 'prompt.txt'
    [IO.File]::WriteAllText($promptPath,$prompt,$utf8)
    SaveJson (Join-Path $artifact 'prelaunch.json') ([ordered]@{schemaVersion=1;preparedAtUtc=[DateTime]::UtcNow.ToString('o');sourceRevision=$revision;actor=$name;order=$i+1;assignedGuidance=$modes[$i];model='gpt-6-luna';reasoningEffort='max';forkTurns='none';projectPath=$project;tracePath=$trace;launchCommand=$launch;allowedOperations=$allowed;profile='agentlang';clockValue='2000-01-01T00:00:00Z';capabilities=@();exchangeTimeoutMilliseconds=120000;maxExchanges=100;maxInspectionResponseBytes=16000;runtimeFiles=$runtimeInventory;hostSha256=(Hash $hostPath);promptSha256=(Hash $promptPath);promptUtf8Bytes=$utf8.GetByteCount($prompt);startingStateSha256=(Hash (Join-Path $artifact 'starting-state.json'));seedFiles=$seedInventory.Count;seedCopyMatches=$true;preflightSha256=(Hash (Join-Path $run 'published-preflight.json'));modelTokensAvailable=$false})
}
Write-Output "Prepared four matched actors from $($seedInventory.Count) seed files; 11 seed tests passed. No actors launched."
