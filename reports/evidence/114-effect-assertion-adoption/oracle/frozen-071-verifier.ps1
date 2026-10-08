#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ProjectPath,
    [Parameter(Mandatory)][string]$OutputPath,
    [Parameter(Mandatory)][string]$RepoRoot,
    [Parameter(Mandatory)][string]$CliDll,
    [Parameter(Mandatory)][string]$StartingProjectPath
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath $RepoRoot).Path
$cli = (Resolve-Path -LiteralPath $CliDll).Path
$seedPath = (Resolve-Path -LiteralPath $StartingProjectPath).Path
$project = (Resolve-Path -LiteralPath $ProjectPath).Path
$seed = (Resolve-Path -LiteralPath $seedPath).Path
$output = [IO.Path]::GetFullPath($OutputPath, $repo)
$clock = '2000-01-01T00:00:00Z'
$helperName = 'invoice.reminder-path'
$targetName = 'invoice.queue-reminder-once'
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$checks = [Collections.Generic.List[object]]::new()
$sessions = [Collections.Generic.List[object]]::new()
$failure = $null
$scratchRoot = Join-Path (Split-Path -Parent $PSScriptRoot) ('scratch-flow-acceptance-' + [guid]::NewGuid().ToString('N'))

function Canonical($Value) { ConvertTo-Json -InputObject $Value -Depth 100 -Compress }

function Assert-Check([string]$Name, [bool]$Condition, [string]$Details = '') {
    $script:checks.Add([ordered]@{ name = $Name; passed = $Condition; details = $Details })
    if (-not $Condition) {
        if ([string]::IsNullOrWhiteSpace($Details)) { throw "Acceptance check failed: $Name" }
        throw "Acceptance check failed: $Name ($Details)"
    }
}

function Get-Inventory([string]$Root) {
    @(Get-ChildItem -LiteralPath $Root -File -Recurse -Force |
        Sort-Object { [IO.Path]::GetRelativePath($Root, $_.FullName) } |
        ForEach-Object {
            [ordered]@{
                path = [IO.Path]::GetRelativePath($Root, $_.FullName)
                bytes = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        })
}

function Get-Manifest([string]$Root) {
    $store = Join-Path $Root '.agentlang/store'
    $currentPath = Join-Path $store 'CURRENT'
    if (-not (Test-Path -LiteralPath $currentPath -PathType Leaf)) { throw "Missing project authority: $currentPath" }
    $current = Get-Content -LiteralPath $currentPath -Raw | ConvertFrom-Json -Depth 100
    $manifestPath = Join-Path $store "manifests/$($current.manifestHash).json"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "Missing current manifest: $manifestPath" }
    $hash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-Check "manifest hash validates for $Root" ($hash -ceq [string]$current.manifestHash) $hash
    [ordered]@{ current = $current; manifest = (Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 100); hash = $hash }
}

function Get-CurrentRevision($Manifest, [string]$Name) {
    $heads = @($Manifest.words | Where-Object { $_.currentName -ceq $Name })
    if ($heads.Count -ne 1) { throw "Expected one persisted word head named '$Name'; found $($heads.Count)." }
    $head = $heads[0]
    $revisions = @($Manifest.revisions | Where-Object { $_.wordId -ceq $head.wordId -and $_.revision -eq $head.currentRevision })
    if ($revisions.Count -ne 1) { throw "Expected one current revision for '$Name'; found $($revisions.Count)." }
    $revisions[0]
}

function Get-SourceText([string]$Root, $Reference) {
    $extension = if ($Reference.kind -eq 'virtual-file-state') { '.json' } else { '.agent' }
    $path = Join-Path $Root ".agentlang/store/objects/$($Reference.hash)$extension"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing immutable source object: $path" }
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-Check "source object hash validates $($Reference.kind)/$($Reference.hash)" ($hash -ceq [string]$Reference.hash) $hash
    [IO.File]::ReadAllText($path, $script:utf8)
}

function Copy-Project([string]$From, [string]$To) {
    [IO.Directory]::CreateDirectory($To) | Out-Null
    Get-ChildItem -LiteralPath $From -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $To -Recurse -Force }
}

function Get-ResponseSummary($Request, $Response) {
    $summary = [ordered]@{
        op = [string]$Request.op
        word = if ($Request.Contains('word')) { [string]$Request.word } else { $null }
        ok = [bool]$Response.ok
        errorCode = if ($Response.error) { [string]$Response.error.code } else { $null }
        data = $null
    }
    $data = $Response.data
    if ($null -eq $data) { return $summary }
    switch ([string]$Request.op) {
        { $_ -in @('test', 'test-all') } {
            $summary.data = [ordered]@{
                results = @($data.results | ForEach-Object { [ordered]@{ word = $_.word; name = $_.name; passed = $_.passed; errorCode = $_.errorCode } })
                coverage = $data.coverage
            }
        }
        'describe' {
            $summary.data = [ordered]@{
                name = $data.name; id = $data.id; inputs = @($data.inputs); outputs = @($data.outputs)
                effects = @($data.effects); dependencies = @($data.dependencies); transitiveDependencies = @($data.transitiveDependencies)
                documentation = $data.documentation; tests = @($data.tests); examples = @($data.examples)
                status = $data.status; maturity = $data.maturity; deprecated = $data.deprecated; revision = $data.revision
                testCount = $data.testCount; exampleCount = $data.exampleCount; coverage = $data.coverage
            }
        }
        'eval' {
            $summary.data = [ordered]@{ stack = @($data.stack); stackTypes = @($data.stackTypes); effects = $data.effects }
        }
        'task.status' {
            $summary.data = [ordered]@{ active = $data.active; effects = $data.effects; errors = @($data.errors) }
        }
        'snapshot.load' {
            $summary.data = [ordered]@{ name = $data.name; manifestHash = $data.manifestHash; virtualFiles = $data.virtualFiles }
        }
        'snapshot.save' { $summary.data = [ordered]@{ name = $data.name; excludedCandidates = @($data.excludedCandidates) } }
        default { $summary.data = [ordered]@{ effects = $data.effects } }
    }
    $summary
}

function Invoke-JsonlSession([string]$Name, [string]$TargetProject, [object[]]$Requests, [string[]]$Allow = @()) {
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @($script:cli, '--project', $TargetProject, '--clock', $script:clock)) { $start.ArgumentList.Add($argument) }
    if ($Allow.Count -gt 0) { $start.ArgumentList.Add('--allow'); $start.ArgumentList.Add(($Allow -join ',')) }
    $start.ArgumentList.Add('--jsonl')
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (-not $process.Start()) { throw "Could not start the pinned runtime for '$Name'." }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    try {
        foreach ($request in $Requests) { $process.StandardInput.WriteLine((ConvertTo-Json -InputObject $request -Depth 100 -Compress)) }
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(120000)) { $process.Kill($true); throw "Runtime session '$Name' exceeded 120 seconds." }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $lines = @($stdout -split "`r?`n" | Where-Object { $_.Length -gt 0 })
        $responses = [Collections.Generic.List[object]]::new()
        foreach ($line in $lines) {
            try { $responses.Add(($line | ConvertFrom-Json -Depth 100)) }
            catch { throw "Runtime session '$Name' emitted invalid JSONL." }
        }
        Assert-Check "JSONL response count for $Name" ($responses.Count -eq $Requests.Count) "requests=$($Requests.Count), responses=$($responses.Count)"
        Assert-Check "runtime process exit for $Name" ($process.ExitCode -eq 0) "exit=$($process.ExitCode); stderr=$stderr"
        $requestSummaries = @($Requests | ForEach-Object {
            [ordered]@{ op = $_.op; word = if ($_.Contains('word')) { $_.word } else { $null }; code = if ($_.code) { $_.code } else { $null }; goal = if ($_.goal) { $_.goal } else { $null }; name = if ($_.name) { $_.name } else { $null } }
        })
        $compactResponses = @()
        for ($i = 0; $i -lt $responses.Count; $i++) { $compactResponses += Get-ResponseSummary $Requests[$i] $responses[$i] }
        $script:sessions.Add([ordered]@{ name = $Name; project = $TargetProject; requests = $requestSummaries; responses = $compactResponses; stderr = $stderr; exitCode = $process.ExitCode })
        return ,($responses.ToArray())
    }
    finally {
        if (-not $process.HasExited) { $process.Kill($true) }
        $process.Dispose()
    }
}

function Assert-OK($Response, [string]$Name) { Assert-Check "$Name succeeds" ([bool]$Response.ok) ([string]$Response.error.message) }

function Get-EffectCount($Response, [string]$Effect) {
    if ($null -eq $Response.data -or $null -eq $Response.data.effects) { return 0 }
    $property = $Response.data.effects.PSObject.Properties[$Effect]
    if ($null -eq $property) { return 0 }
    [int]$property.Value
}

function Assert-Effects($Response, [int]$Read, [int]$Write, [string]$Name) {
    $actualRead = Get-EffectCount $Response 'fs.read'
    $actualWrite = Get-EffectCount $Response 'fs.write'
    Assert-Check "$Name exact provider effects" ($actualRead -eq $Read -and $actualWrite -eq $Write) "fs.read=$actualRead, fs.write=$actualWrite; expected $Read/$Write"
    $extra = @()
    if ($Response.data -and $Response.data.effects) { $extra = @($Response.data.effects.PSObject.Properties | Where-Object { $_.Name -notin @('fs.read', 'fs.write') } | ForEach-Object Name) }
    Assert-Check "$Name has no undeclared provider effects" ($extra.Count -eq 0) ($extra -join ', ')
}

function Get-RecordFields([string]$Source, [string]$TypeName) {
    $match = [regex]::Match($Source, "(?ms)\brecord\s+$([regex]::Escape($TypeName))\s*\{(?<body>.*?)\}")
    if (-not $match.Success) { $match = [regex]::Match($Source, "(?ms)\brecord\s+$([regex]::Escape($TypeName))\s*(?<body>.*?)\bend\b") }
    if (-not $match.Success) { throw "Could not read the $TypeName record fields from its frozen type source." }
    $fields = [Collections.Generic.List[object]]::new()
    foreach ($field in [regex]::Matches($match.Groups['body'].Value, '(?m)^\s*field\s+(?<name>[A-Za-z][A-Za-z0-9_-]*)\s*(?::\s*|\s+)(?<type>[A-Za-z][A-Za-z0-9_<>, ?]*)\s*;?\s*$')) {
        $fields.Add([ordered]@{ name = $field.Groups['name'].Value; type = ($field.Groups['type'].Value.Trim() -replace '\s+', '') })
    }
    if ($fields.Count -eq 0) { throw "No fields parsed from the frozen $TypeName record source." }
    $fields.ToArray()
}

function New-InvoiceExpression([string]$InvoiceId, [string]$Status, $Fields) {
    $hasExpectedFields = $Fields.Count -eq 2 -and
        @($Fields | Where-Object { $_.name -ceq 'id' -and $_.type -ceq 'InvoiceId' }).Count -eq 1 -and
        @($Fields | Where-Object { $_.name -ceq 'status' -and $_.type -ceq 'InvoiceStatus' }).Count -eq 1
    if (-not $hasExpectedFields) { throw 'Frozen Invoice shape changed; expected exactly InvoiceId id and InvoiceStatus status.' }
    "invoice::new(id = InvoiceId::new(`"$InvoiceId`"), status = InvoiceStatus::new(`"$Status`"))"
}

function Get-StackString($Response) {
    if ($null -eq $Response.data -or @($Response.data.stack).Count -lt 1) { return $null }
    $rendered = [string]$Response.data.stack[0]
    try {
        $parsed = ConvertFrom-Json -InputObject $rendered -Depth 10 -ErrorAction Stop
        if ($parsed -is [string]) { return $parsed }
    }
    catch { }
    $rendered
}

function Get-StackBoolean($Response) {
    if ($null -eq $Response.data -or @($Response.data.stack).Count -lt 1) { return $null }
    $rendered = [string]$Response.data.stack[0]
    if ($rendered -ceq 'true') { return $true }
    if ($rendered -ceq 'false') { return $false }
    try {
        $parsed = ConvertFrom-Json -InputObject $rendered -Depth 10 -ErrorAction Stop
        if ($parsed -is [bool]) { return $parsed }
        if ($parsed -is [string] -and $parsed -ceq 'true') { return $true }
        if ($parsed -is [string] -and $parsed -ceq 'false') { return $false }
    }
    catch { return $null }
    return $null
}

try {
    Assert-Check 'pinned CLI assembly exists' (Test-Path -LiteralPath $cli -PathType Leaf) $cli
    Assert-Check 'frozen starting project exists' (Test-Path -LiteralPath $seed -PathType Container) $seed
    Assert-Check 'final project is separate from frozen seed' (-not [string]::Equals($project, $seed, [StringComparison]::OrdinalIgnoreCase)) $project
    $seedBefore = Get-Inventory $seed
    $finalBefore = Get-Inventory $project
    $seedAuthority = Get-Manifest $seed
    $finalAuthority = Get-Manifest $project
    $seedManifest = $seedAuthority.manifest
    $finalManifest = $finalAuthority.manifest
    $seedHelper = Get-CurrentRevision $seedManifest $helperName
    $finalHelper = Get-CurrentRevision $finalManifest $helperName
    $targetHeads = @($finalManifest.words | Where-Object { $_.currentName -ceq $targetName })
    Assert-Check 'committed target exists exactly once' ($targetHeads.Count -eq 1) "count=$($targetHeads.Count)"
    Assert-Check 'seeded nominal Invoice types remain byte-identical' ((Canonical $seedManifest.types) -ceq (Canonical $finalManifest.types)) 'type sources and stable validator bindings'
    Assert-Check 'InvoiceId and InvoiceStatus types are present in the frozen seed' (@($seedManifest.types | Where-Object { $_.name -ceq 'InvoiceId' }).Count -eq 1 -and @($seedManifest.types | Where-Object { $_.name -ceq 'InvoiceStatus' }).Count -eq 1) 'both semantic field types must already exist'
    foreach ($head in $seedManifest.words) {
        $same = @($finalManifest.words | Where-Object { $_.wordId -ceq $head.wordId })
        Assert-Check "prior word identity/revision retained: $($head.currentName)" ($same.Count -eq 1 -and (Canonical $same[0]) -ceq (Canonical $head))
    }
    foreach ($revision in $seedManifest.revisions) {
        $same = @($finalManifest.revisions | Where-Object { $_.wordId -ceq $revision.wordId -and $_.revision -eq $revision.revision })
        Assert-Check "prior revision retained: $($revision.name)/$($revision.revision)" ($same.Count -eq 1 -and (Canonical $same[0]) -ceq (Canonical $revision))
    }
    Assert-Check 'pure reminder-path helper head is unchanged' ((Canonical ($seedManifest.words | Where-Object { $_.wordId -ceq $seedHelper.wordId })) -ceq (Canonical ($finalManifest.words | Where-Object { $_.wordId -ceq $finalHelper.wordId }))) 'stable helper identity and current revision'
    $seedHelperHistory = @($seedManifest.revisions | Where-Object { $_.wordId -ceq $seedHelper.wordId } | Sort-Object revision)
    $finalHelperHistory = @($finalManifest.revisions | Where-Object { $_.wordId -ceq $finalHelper.wordId } | Sort-Object revision)
    Assert-Check 'all helper revision/test/example references are retained' ((Canonical $seedHelperHistory) -ceq (Canonical $finalHelperHistory)) "seed=$($seedHelperHistory.Count), final=$($finalHelperHistory.Count)"
    $seedIds = @($seedManifest.words | ForEach-Object { [string]$_.wordId })
    $newNames = @($finalManifest.words | Where-Object { $seedIds -notcontains [string]$_.wordId } | ForEach-Object { $_.currentName } | Sort-Object)
    Assert-Check 'the only new persistent word is the requested library word' ((Canonical $newNames) -ceq (Canonical @($targetName))) ($newNames -join ', ')

    foreach ($seedFile in $seedBefore) {
        if ($seedFile.path -in @('dictionary.agent', 'project.agent', '.agentlang\store\CURRENT')) { continue }
        $matching = @($finalBefore | Where-Object { $_.path -ceq $seedFile.path })
        Assert-Check "immutable seeded file retained: $($seedFile.path)" ($matching.Count -eq 1 -and $matching[0].sha256 -ceq $seedFile.sha256)
    }

    $targetRevision = Get-CurrentRevision $finalManifest $targetName
    $targetSourceFormat = $targetRevision.sourceFormat
    Assert-Check 'target persisted source format is Flow/2' ($targetSourceFormat.frontend -ceq 'flow' -and $targetSourceFormat.version -eq 2) (Canonical $targetSourceFormat)
    $targetSource = Get-SourceText $project $targetRevision.definition
    $testSources = @($targetRevision.tests | ForEach-Object { Get-SourceText $project $_ })
    $exampleSources = @($targetRevision.examples | ForEach-Object { Get-SourceText $project $_ })
    Assert-Check 'target is a nondeprecated library revision with four or more attached tests' ($targetRevision.maturity -eq 'library' -and -not $targetRevision.deprecated -and $targetRevision.tests.Count -ge 4) "maturity=$($targetRevision.maturity), tests=$($targetRevision.tests.Count), deprecated=$($targetRevision.deprecated)"
    Assert-Check 'target has at least one attached example' ($targetRevision.examples.Count -ge 1) "examples=$($targetRevision.examples.Count)"
    Assert-Check 'target source includes documentation metadata' ($targetSource -match '(?m)^\s*doc\s+"') 'documentation must be attached inside the Flow word definition'
    Assert-Check 'target example source is attached to the requested word' (@($exampleSources | Where-Object { $_ -match '(?m)\bexample\s+invoice\.queue-reminder-once/' }).Count -ge 1)
    $attachedTestNames = @($testSources | ForEach-Object { [regex]::Matches($_, '(?m)\btest\s+invoice\.queue-reminder-once/(?<name>[A-Za-z0-9_.-]+)') | ForEach-Object { $_.Groups['name'].Value } })
    Assert-Check 'target test case names are unique and attached to this word' ($attachedTestNames.Count -ge 4 -and @($attachedTestNames | Sort-Object -Unique).Count -eq $attachedTestNames.Count) ($attachedTestNames -join ', ')

    $invoiceType = @($seedManifest.types | Where-Object { $_.name -ceq 'Invoice' })
    Assert-Check 'frozen seed declares the Invoice record' ($invoiceType.Count -eq 1)
    $invoiceFields = Get-RecordFields (Get-SourceText $seed $invoiceType[0].definition) 'Invoice'
    Assert-Check 'Invoice contains nominal id and status fields' (@($invoiceFields | Where-Object { $_.name -ceq 'id' -and $_.type -ceq 'InvoiceId' }).Count -eq 1 -and @($invoiceFields | Where-Object { $_.name -ceq 'status' -and $_.type -ceq 'InvoiceStatus' }).Count -eq 1) (Canonical $invoiceFields)

    $seedCopy = Join-Path $scratchRoot 'seed-baseline'
    Copy-Project $seed $seedCopy
    $baseline = Invoke-JsonlSession 'seed attached-test baseline' $seedCopy @(@{ op = 'test-all' }) @('fs.read', 'fs.write')
    $baselineTests = @($baseline[0].data.results)
    Assert-Check 'all seeded tests pass before agent changes' ($baseline[0].ok -and $baselineTests.Count -gt 0 -and @($baselineTests | Where-Object { -not $_.passed }).Count -eq 0) "tests=$($baselineTests.Count)"

    $inspectionCopy = Join-Path $scratchRoot 'final-inspection'
    Copy-Project $project $inspectionCopy
    $inspect = Invoke-JsonlSession 'fresh-process own tests and reloaded description' $inspectionCopy @(
        @{ op = 'test'; word = $targetName },
        @{ op = 'describe'; word = $targetName },
        @{ op = 'test-all' }
    ) @('fs.read', 'fs.write')
    $ownResults = @($inspect[0].data.results)
    $ownCoverage = $inspect[0].data.coverage
    Assert-Check 'every attached target test passes in a fresh process' ($inspect[0].ok -and $ownResults.Count -eq $targetRevision.tests.Count -and @($ownResults | Where-Object { -not $_.passed }).Count -eq 0) "results=$($ownResults.Count), attachments=$($targetRevision.tests.Count)"
    Assert-Check 'library instruction coverage is complete' ($ownCoverage.instructionsTotal -gt 0 -and $ownCoverage.instructionsCovered -eq $ownCoverage.instructionsTotal) "instructions=$($ownCoverage.instructionsCovered)/$($ownCoverage.instructionsTotal)"
    Assert-Check 'library branch coverage is complete and includes branch outcomes' ($ownCoverage.branchesTotal -gt 0 -and $ownCoverage.branchesCovered -eq $ownCoverage.branchesTotal) "branches=$($ownCoverage.branchesCovered)/$($ownCoverage.branchesTotal)"
    $description = $inspect[1].data
    Assert-Check 'reloaded target has Invoice -> String signature' (@($description.inputs).Count -eq 1 -and $description.inputs[0] -ceq 'Invoice' -and @($description.outputs).Count -eq 1 -and $description.outputs[0] -ceq 'String') "$(Canonical $description.inputs) -> $(Canonical $description.outputs)"
    Assert-Check 'reloaded target is persistent library vocabulary with exact effects' ($description.status -eq 'persistent' -and $description.maturity -eq 'library' -and (Canonical @($description.effects | Sort-Object)) -ceq (Canonical @('fs.read', 'fs.write'))) "status=$($description.status), maturity=$($description.maturity), effects=$(Canonical $description.effects)"
    $compiledDependencies = @($description.dependencies | ForEach-Object { ([string]$_).Replace('::', '.') })
    Assert-Check 'compiled target depends on the seeded path abstraction' ($compiledDependencies -contains $helperName) (Canonical $description.dependencies)
    Assert-Check 'compiled target includes file existence/read/write provider calls' (@('file.exists?', 'file.read', 'file.write' | Where-Object { $_ -notin $compiledDependencies }).Count -eq 0) (Canonical $compiledDependencies)
    Assert-Check 'reloaded target carries documentation, tests, example and complete own coverage' (-not [string]::IsNullOrWhiteSpace([string]$description.documentation) -and @($description.tests).Count -eq $targetRevision.tests.Count -and @($description.examples).Count -ge 1 -and $description.coverage.instructionsCovered -eq $description.coverage.instructionsTotal -and $description.coverage.branchesTotal -gt 0 -and $description.coverage.branchesCovered -eq $description.coverage.branchesTotal) 'test/describe are adjacent so this is the target-specific latest test coverage'
    $allResults = @($inspect[2].data.results)
    Assert-Check 'fresh-process test-all passes seeded and new cases' ($inspect[2].ok -and $allResults.Count -ge ($baselineTests.Count + $targetRevision.tests.Count) -and @($allResults | Where-Object { -not $_.passed }).Count -eq 0) "results=$($allResults.Count), minimum=$($baselineTests.Count + $targetRevision.tests.Count)"

    $allowed = @('fs.read', 'fs.write')
    $id1 = 'acceptance-open-001'
    $id2 = 'acceptance-open-002'
    $invoice1 = New-InvoiceExpression $id1 'open' $invoiceFields
    $invoice2 = New-InvoiceExpression $id2 'open' $invoiceFields
    $path1Project = Join-Path $scratchRoot 'path-1'
    Copy-Project $project $path1Project
    # The helper path is evaluated on a disposable project copy and is pure and stable across copies.
    $path1Response = Invoke-JsonlSession 'read helper path for id 1' $path1Project @(@{ op = 'eval'; frontend = 'flow'; code = "invoice::reminder-path(InvoiceId::new(`"$id1`"))" }) $allowed
    Assert-OK $path1Response[0] 'seeded reminder-path helper for id 1'
    $path1 = Get-StackString $path1Response[0]
    Assert-Check 'seeded reminder-path follows the documented outbox convention' ($path1 -ceq "outbox/invoice-reminders/$id1") $path1
    $path2Response = Invoke-JsonlSession 'read helper path for id 2' $path1Project @(@{ op = 'eval'; frontend = 'flow'; code = "invoice::reminder-path(InvoiceId::new(`"$id2`"))" }) $allowed
    Assert-OK $path2Response[0] 'seeded reminder-path helper for id 2'
    $path2 = Get-StackString $path2Response[0]
    Assert-Check 'two distinct invoice ids produce independent marker paths' ($path2 -ceq "outbox/invoice-reminders/$id2" -and $path2 -cne $path1) $path2

    $openProject = Join-Path $scratchRoot 'open-and-repeat'
    Copy-Project $project $openProject
    $openRequests = @(
        @{ op = 'eval'; frontend = 'flow'; code = "invoice::queue-reminder-once($invoice1)" },
        @{ op = 'snapshot.save'; name = 'after-first-reminder' },
        @{ op = 'eval'; frontend = 'flow'; code = "invoice::queue-reminder-once($invoice1)" },
        @{ op = 'eval'; frontend = 'flow'; code = "invoice::queue-reminder-once($invoice2)" },
        @{ op = 'eval'; frontend = 'flow'; code = "file::read(`"$path1`")" },
        @{ op = 'eval'; frontend = 'flow'; code = "file::read(`"$path2`")" }
    )
    $open = Invoke-JsonlSession 'new open invoice, repeat and independent id' $openProject $openRequests $allowed
    foreach ($i in @(0, 2, 3, 4, 5)) { Assert-OK $open[$i] "open-path request $i" }
    Assert-Check 'new open invoice returns queued marker' ((Get-StackString $open[0]) -ceq 'queued') (Canonical $open[0].data.stack)
    Assert-Effects $open[0] 1 1 'new open invoice'
    Assert-Check 'repeat returns exact existing marker text' ((Get-StackString $open[2]) -ceq 'queued') (Canonical $open[2].data.stack)
    Assert-Effects $open[2] 2 0 'repeat with existing queued marker'
    Assert-Check 'different invoice id receives its own queued marker' ((Get-StackString $open[3]) -ceq 'queued') (Canonical $open[3].data.stack)
    Assert-Effects $open[3] 1 1 'independent open invoice id'
    Assert-Check 'both independent outbox paths contain exactly queued' ((Get-StackString $open[4]) -ceq 'queued' -and (Get-StackString $open[5]) -ceq 'queued') "id1=$(Get-StackString $open[4]), id2=$(Get-StackString $open[5])"
    Assert-Check 'snapshot saved after first marker creation' ($open[1].ok -and $open[1].data.name -ceq 'after-first-reminder') ([string]$open[1].error.message)
    $snapshotCopy = Join-Path $scratchRoot 'snapshot-reload'
    Copy-Project $openProject $snapshotCopy
    $snapshot = Invoke-JsonlSession 'fresh process restores virtual marker snapshot' $snapshotCopy @(
        @{ op = 'snapshot.load'; name = 'after-first-reminder' },
        @{ op = 'eval'; frontend = 'flow'; code = "invoice::queue-reminder-once($invoice1)" },
        @{ op = 'eval'; frontend = 'flow'; code = "file::read(`"$path1`")" }
    ) $allowed
    Assert-OK $snapshot[0] 'load committed provider snapshot in a new process'
    Assert-OK $snapshot[1] 'repeat after loading persisted provider snapshot'
    Assert-Effects $snapshot[1] 2 0 'repeat after fresh-process snapshot load'
    Assert-Check 'fresh-process snapshot restores exact queued marker' ((Get-StackString $snapshot[1]) -ceq 'queued' -and (Get-StackString $snapshot[2]) -ceq 'queued') "call=$(Get-StackString $snapshot[1]), stored=$(Get-StackString $snapshot[2])"

    $customId = 'acceptance-custom-001'
    $customInvoice = New-InvoiceExpression $customId 'open' $invoiceFields
    $customPath = "outbox/invoice-reminders/$customId"
    $customProject = Join-Path $scratchRoot 'existing-custom-marker'
    Copy-Project $project $customProject
    $customText = 'held for manual review'
    $custom = Invoke-JsonlSession 'existing nonstandard marker is preserved' $customProject @(
        @{ op = 'eval'; frontend = 'flow'; code = "file::write(`"$customPath`", `"$customText`")" },
        @{ op = 'eval'; frontend = 'flow'; code = "invoice::queue-reminder-once($customInvoice)" },
        @{ op = 'eval'; frontend = 'flow'; code = "file::read(`"$customPath`")" }
    ) $allowed
    Assert-OK $custom[0] 'seed custom existing marker'
    Assert-OK $custom[1] 'read and return existing custom marker'
    Assert-OK $custom[2] 'independently read preserved marker'
    Assert-Effects $custom[1] 2 0 'existing different marker'
    Assert-Check 'existing marker text is returned byte-for-byte and is not overwritten' ((Get-StackString $custom[1]) -ceq $customText -and (Get-StackString $custom[2]) -ceq $customText) "returned=$(Get-StackString $custom[1]); stored=$(Get-StackString $custom[2])"

    $nonOpenProject = Join-Path $scratchRoot 'non-open-statuses'
    Copy-Project $project $nonOpenProject
    $nonOpenCases = @(
        [ordered]@{ id = 'acceptance-paid-001'; status = 'paid' },
        [ordered]@{ id = 'acceptance-cancelled-001'; status = 'cancelled' },
        [ordered]@{ id = 'acceptance-uppercase-001'; status = 'OPEN' }
    )
    $nonOpenRequests = [Collections.Generic.List[object]]::new()
    foreach ($case in $nonOpenCases) {
        $value = New-InvoiceExpression $case.id $case.status $invoiceFields
        $nonOpenRequests.Add(@{ op = 'eval'; frontend = 'flow'; code = "invoice::queue-reminder-once($value)" })
        $path = "outbox/invoice-reminders/$($case.id)"
        $nonOpenRequests.Add(@{ op = 'eval'; frontend = 'flow'; code = "file::exists?(`"$path`")" })
    }
    $nonOpen = Invoke-JsonlSession 'non-open statuses do not touch providers' $nonOpenProject $nonOpenRequests.ToArray() $allowed
    for ($i = 0; $i -lt $nonOpenCases.Count; $i++) {
        $targetResponse = $nonOpen[$i * 2]
        $existsResponse = $nonOpen[$i * 2 + 1]
        Assert-OK $targetResponse "non-open status $($nonOpenCases[$i].status)"
        Assert-Effects $targetResponse 0 0 "non-open status $($nonOpenCases[$i].status)"
        Assert-OK $existsResponse "marker check for non-open status $($nonOpenCases[$i].status)"
        Assert-Check "non-open status $($nonOpenCases[$i].status) creates no marker" ((Get-StackBoolean $existsResponse) -eq $false) (Canonical $existsResponse.data.stack)
    }

    $deniedProject = Join-Path $scratchRoot 'capability-denied'
    Copy-Project $project $deniedProject
    $deniedInvoice = New-InvoiceExpression 'acceptance-denied-001' 'open' $invoiceFields
    $denied = Invoke-JsonlSession 'host denies filesystem capabilities' $deniedProject @(
        @{ op = 'task.begin'; goal = 'Verify effect capability denial' },
        @{ op = 'eval'; frontend = 'flow'; code = "invoice::queue-reminder-once($deniedInvoice)" },
        @{ op = 'task.status' }
    ) @()
    Assert-Check 'host capability policy blocks the persisted effectful word' (-not $denied[1].ok -and $denied[1].error.code -ceq 'CAPABILITY_DENIED') ([string]$denied[1].error.message)
    $deniedEffects = $denied[2].data.effects
    Assert-Check 'capability denial executes no provider operations' ((Get-EffectCount ([pscustomobject]@{ data = [pscustomobject]@{ effects = $deniedEffects } }) 'fs.read') -eq 0 -and (Get-EffectCount ([pscustomobject]@{ data = [pscustomobject]@{ effects = $deniedEffects } }) 'fs.write') -eq 0) (Canonical $deniedEffects)

    $badTypeProject = Join-Path $scratchRoot 'wrong-invoice-type'
    Copy-Project $project $badTypeProject
    $badType = Invoke-JsonlSession 'nominal Invoice parameter rejects plain String' $badTypeProject @(@{ op = 'eval'; frontend = 'flow'; code = 'invoice::queue-reminder-once("not-an-invoice")' }) $allowed
    Assert-Check 'strongly typed target rejects a plain String before effects' (-not $badType[0].ok -and $badType[0].error.code -ceq 'FLOW_ARGUMENT_TYPE') ([string]$badType[0].error.code)

    $seedAfter = Get-Inventory $seed
    $finalAfter = Get-Inventory $project
    Assert-Check 'frozen seed is unchanged by acceptance' ((Canonical $seedBefore) -ceq (Canonical $seedAfter)) 'source seed inventory before/after'
    Assert-Check 'actor final project is unchanged by acceptance' ((Canonical $finalBefore) -ceq (Canonical $finalAfter)) 'all tests and behavioral effects ran on disposable copies'
}
catch {
    $failure = $_.Exception.Message
}
finally {
    $parent = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
    $scratchFull = [IO.Path]::GetFullPath($scratchRoot)
    if ($scratchFull.StartsWith($parent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $scratchFull)) {
        Remove-Item -LiteralPath $scratchFull -Recurse -Force
    }
    [IO.Directory]::CreateDirectory((Split-Path -Parent $output)) | Out-Null
    $evidence = [ordered]@{
        schemaVersion = 1
        passed = ($null -eq $failure)
        failure = $failure
        sourceRevision = (& git -C $repo rev-parse HEAD).Trim()
        cliPath = $cli
        cliSha256 = if (Test-Path -LiteralPath $cli) { (Get-FileHash -LiteralPath $cli -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
        seedProject = $seedPath
        finalProject = $project
        helperWord = $helperName
        targetWord = $targetName
        checks = $checks.ToArray()
        protocolSessions = $sessions.ToArray()
        scope = 'Independent final-word/history/type/test coverage audit; fresh-process first-write, idempotent repeat and snapshot restoration; exact preservation of an existing custom marker; separate id, three non-open statuses, capability denial, nominal-parameter rejection, and seed/final immutability. All runtime probes execute on disposable copies.'
    }
    [IO.File]::WriteAllText($output, (ConvertTo-Json -InputObject $evidence -Depth 100), [Text.UTF8Encoding]::new($false))
}

if ($failure) { throw $failure }
Write-Output "Stateful trial acceptance passed $($checks.Count) checks. Evidence: $output"
