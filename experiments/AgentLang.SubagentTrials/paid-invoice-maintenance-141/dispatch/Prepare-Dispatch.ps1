#requires -Version 7.5
[CmdletBinding()]
param([string]$RepositoryRoot = 'D:\code\AgentLang')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$study = Join-Path $RepositoryRoot 'experiments/AgentLang.SubagentTrials/paid-invoice-maintenance-141'
$evidence = Join-Path $RepositoryRoot '.agentlang/maintenance-141'
$publicTask = [IO.File]::ReadAllText((Join-Path $study 'task.md'))
$records = [Collections.Generic.List[object]]::new()

foreach ($arm in @('flow-retained','fsharp')) {
    $oldArm = if ($arm -eq 'fsharp') { 'fsharp' } else { 'flow-a' }
    $oldPromptPath = Join-Path $RepositoryRoot "experiments/AgentLang.SubagentTrials/r09-discovery-001/main-dispatch/prompts/replica2/$oldArm.md"
    $oldPrompt = [IO.File]::ReadAllText($oldPromptPath)
    $marker = '# Public refactoring task'
    $markerIndex = $oldPrompt.IndexOf($marker, [StringComparison]::Ordinal)
    if ($markerIndex -lt 0) { throw "Missing public-task marker: $oldPromptPath" }
    $prefix = $oldPrompt.Substring(0, $markerIndex)
    foreach ($replica in 1..2) {
        $id = "$arm-$replica"
        $participantRoot = Join-Path $evidence "participants/$id"
        $project = Join-Path $participantRoot 'project'
        if (Test-Path -LiteralPath $participantRoot) { throw "Refusing to replace participant: $participantRoot" }
        $start = Join-Path $study "starts/$arm/project"
        if (-not (Test-Path -LiteralPath $start)) { throw "Missing start: $start" }
        [void][IO.Directory]::CreateDirectory($participantRoot)
        Copy-Item -LiteralPath $start -Destination $project -Recurse
        $oldRoot = Join-Path $RepositoryRoot ".agentlang/r09-discovery-001/comparison/participant-work/replica2/$arm"
        $prompt = $prefix.Replace($oldRoot, $participantRoot)
        $prompt = $prompt.Replace('.agentlang\r09-discovery-001\runtime-artifacts', '.agentlang\maintenance-141\runtime-artifacts')
        $prompt = $prompt.Replace('.agentlang\r09-discovery-001\conventional-broker-artifacts', '.agentlang\maintenance-141\conventional-artifacts')
        $prompt += "`n$publicTask`n"
        if ($arm -eq 'fsharp') {
            $prompt += "`nThe shared-result error type is DomainError in this F# project. The supplied Store.Maintenance141Fixture block in Business.fs is frozen: preserve it byte-for-byte. All permitted edits remain limited to business/Business.fs and tests/Program.fs.`n"
        } else {
            $prompt += "`nFlow/2 primer: use fn, immutable let bindings, record properties, named arguments and ==. Attach documentation and tests; select syntaxVersion 2 and frontend flow for the appropriate requests. The supplied maintenance141.fixture-import-payment function is frozen. Do not replace it. The coordinator will independently reload your saved project after you finish.`n"
        }
        $promptPath = Join-Path $PSScriptRoot "$id.prompt.md"
        [IO.File]::WriteAllText($promptPath, $prompt, [Text.UTF8Encoding]::new($false))
        $promptHash = (Get-FileHash -LiteralPath $promptPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $wrapper = @"
You are a fresh comparison participant. Read only the single assigned prompt file below, verify its SHA-256, then follow its instructions. The human authorizes edits only to the isolated project described in that prompt and its local tests. Do not read any other repository, study, oracle, scorer, control, or participant files. Project discovery, editing, evaluation, and tests must use only the assigned broker. If the prompt is missing or its hash does not match, stop without touching the project.

Prompt file: $promptPath
Expected SHA-256: $promptHash
"@
        $wrapperPath = Join-Path $PSScriptRoot "$id.wrapper.txt"
        [IO.File]::WriteAllText($wrapperPath, $wrapper, [Text.UTF8Encoding]::new($false))
        $records.Add([ordered]@{id=$id;arm=$arm;replica=$replica;project=$project;prompt=$promptPath;promptSha256=$promptHash;wrapper=$wrapperPath;wrapperSha256=(Get-FileHash -LiteralPath $wrapperPath -Algorithm SHA256).Hash.ToLowerInvariant()})
    }
}

[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'participants.json'), (ConvertTo-Json -InputObject @($records) -Depth 5), [Text.UTF8Encoding]::new($false))
Write-Output 'Prepared four isolated projects and exact dispatch prompts. NOT frozen or dispatched: review controls, prompts and source inventories first.'
