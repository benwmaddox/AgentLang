[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('growing', 'flat', 'conventional')]
    [string]$Mode,

    [Parameter(Mandatory = $true)]
    [ValidateSet('S01', 'S06', 'S07')]
    [string]$TaskId,

    [Parameter(Mandatory = $true)]
    [string]$CliDll,

    [Parameter(Mandatory = $true)]
    [string]$BusinessDll,

    [string]$PreviousProject,
    [string]$PreviousAcceptance,
    [string]$LocalRoot,
    [string]$RunRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($LocalRoot)) {
    $LocalRoot = Join-Path $script:RepoRoot '.agentlang/business-policy-001'
}
if ([string]::IsNullOrWhiteSpace($RunRoot)) {
    $RunRoot = Join-Path $script:RepoRoot 'experiments/AgentLang.SubagentTrials/business-policy-001/runs'
}

function Get-FullPath([string]$Path) {
    return [System.IO.Path]::GetFullPath($Path)
}

function Resolve-RepoPath([string]$Path) {
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return Get-FullPath $Path
    }
    return Get-FullPath (Join-Path $script:RepoRoot $Path)
}

function Assert-ContainedPath([string]$Root, [string]$Candidate, [string]$Label) {
    $fullRoot = Get-FullPath $Root
    $fullCandidate = Get-FullPath $Candidate
    $prefix = $fullRoot.TrimEnd([char[]]@('\', '/')) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullCandidate.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label must be within '$fullRoot'; received '$fullCandidate'."
    }
    return $fullCandidate
}

function Get-RelativePath([string]$Root, [string]$Path) {
    $relative = [System.IO.Path]::GetRelativePath((Get-FullPath $Root), (Get-FullPath $Path))
    if ($relative -eq '..' -or $relative.StartsWith('..' + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::Ordinal) -or [System.IO.Path]::IsPathRooted($relative)) {
        throw "Path '$Path' escapes '$Root'."
    }
    return $relative.Replace('\', '/')
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-NoReparsePoints([string]$Root) {
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        throw "Directory does not exist: $Root"
    }
    $rootItem = Get-Item -LiteralPath $Root -Force
    if (($rootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Reparse-point roots are not allowed: $Root"
    }
    foreach ($item in Get-ChildItem -LiteralPath $Root -Force -Recurse) {
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Reparse points are not allowed in prepared projects: $($item.FullName)"
        }
    }
}

function Assert-NoReparseAncestors([string]$ContainmentRoot, [string]$Candidate) {
    $fullRoot = Get-FullPath $ContainmentRoot
    $fullCandidate = Assert-ContainedPath $fullRoot $Candidate 'Path'
    $current = $fullCandidate
    while ($true) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Reparse-point path component is not allowed: $current"
            }
        }
        if ($current -eq $fullRoot) {
            break
        }
        $current = Split-Path -Parent $current
        if ([string]::IsNullOrWhiteSpace($current)) {
            throw "Could not resolve parent path for '$fullCandidate'."
        }
    }
}

function Get-ProjectInventory([string]$Root) {
    Assert-NoReparsePoints $Root
    $rows = [System.Collections.Generic.List[object]]::new()
    foreach ($item in Get-ChildItem -LiteralPath $Root -File -Force -Recurse) {
        $relative = Get-RelativePath $Root $item.FullName
        $segments = $relative.Split('/')
        if ($segments -contains 'bin' -or $segments -contains 'obj') {
            continue
        }
        $rows.Add([pscustomobject]@{
            path = $relative
            sha256 = Get-Sha256 $item.FullName
        })
    }
    return @($rows | Sort-Object path)
}

function ConvertTo-CanonicalJson([object]$Value) {
    return ConvertTo-Json -InputObject $Value -Depth 100 -Compress
}

function Get-Sha256FromInventory([object[]]$Inventory) {
    $canonical = ConvertTo-CanonicalJson $Inventory
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($canonical)
    $hash = [System.Security.Cryptography.SHA256]::HashData($bytes)
    return [System.Convert]::ToHexString($hash).ToLowerInvariant()
}

function Write-JsonFile([string]$Path, [object]$Value) {
    $json = ConvertTo-Json -InputObject $Value -Depth 100
    $encoding = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, $encoding)
}

function Copy-ProjectTree([string]$Source, [string]$Destination) {
    if (Test-Path -LiteralPath $Destination) {
        throw "Fresh destination already exists: $Destination"
    }
    $sourceInventory = Get-ProjectInventory $Source
    [System.IO.Directory]::CreateDirectory($Destination) | Out-Null
    foreach ($row in $sourceInventory) {
        $sourceFile = Join-Path $Source ($row.path.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        $destinationFile = Join-Path $Destination ($row.path.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        $destinationDirectory = Split-Path -Parent $destinationFile
        [System.IO.Directory]::CreateDirectory($destinationDirectory) | Out-Null
        [System.IO.File]::Copy($sourceFile, $destinationFile, $false)
    }
    $destinationInventory = Get-ProjectInventory $Destination
    if ((ConvertTo-CanonicalJson $sourceInventory) -cne (ConvertTo-CanonicalJson $destinationInventory)) {
        throw "Copied project inventory differs from source '$Source'."
    }
    return $destinationInventory
}

function Remove-CreatedDirectory([string]$Path, [string]$ContainmentRoot) {
    if (Test-Path -LiteralPath $Path) {
        $fullPath = Assert-ContainedPath $ContainmentRoot $Path 'Cleanup target'
        if ($fullPath -eq (Get-FullPath $ContainmentRoot)) {
            throw "Refusing to remove a containment root: $fullPath"
        }
        Assert-NoReparsePoints $fullPath
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
}

function Invoke-AgentJsonl([string]$Project, [object[]]$Requests, [string]$RuntimeDll) {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'dotnet'
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.ArgumentList.Add((Get-FullPath $RuntimeDll))
    $startInfo.ArgumentList.Add('--project')
    $startInfo.ArgumentList.Add((Get-FullPath $Project))
    $startInfo.ArgumentList.Add('--jsonl')

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $responses = [System.Collections.Generic.List[object]]::new()
    if (-not $process.Start()) {
        throw "Could not start pinned CLI '$RuntimeDll'."
    }
    $stderrTask = $process.StandardError.ReadToEndAsync()
    try {
        foreach ($request in $Requests) {
            $json = ConvertTo-Json -InputObject $request -Depth 100 -Compress
            $process.StandardInput.WriteLine($json)
            $process.StandardInput.Flush()
            $readTask = $process.StandardOutput.ReadLineAsync()
            if (-not $readTask.Wait(120000)) {
                $process.Kill($true)
                throw "Pinned CLI timed out while processing '$($request.op)'."
            }
            $line = $readTask.Result
            if ([string]::IsNullOrWhiteSpace($line)) {
                $process.WaitForExit()
                $stderr = $stderrTask.GetAwaiter().GetResult()
                throw "Pinned CLI ended before replying to '$($request.op)'. stderr: $stderr"
            }
            $response = ConvertFrom-Json -InputObject $line -Depth 100
            if ($response.ok -ne $true) {
                $code = if ($null -ne $response.error) { $response.error.code } else { 'unknown' }
                $detail = if ($null -ne $response.text) { $response.text } else { $line }
                throw "Pinned CLI request '$($request.op)' failed ($code): $detail"
            }
            $responses.Add($response)
        }
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(60000)) {
            $process.Kill($true)
            throw 'Pinned CLI did not exit after JSONL input closed.'
        }
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            throw "Pinned CLI exited with code $($process.ExitCode). stderr: $stderr"
        }
    }
    finally {
        if (-not $process.HasExited) {
            $process.Kill($true)
        }
        $process.Dispose()
    }
    return ,$responses.ToArray()
}

function Assert-TestResults([object]$Response, [int]$ExpectedCount, [string]$Label) {
    $results = @($Response.data.results)
    if ($results.Count -ne $ExpectedCount) {
        throw "$Label expected $ExpectedCount tests but the runtime reported $($results.Count)."
    }
    $failed = @($results | Where-Object { $_.passed -ne $true })
    if ($failed.Count -gt 0) {
        $names = ($failed | ForEach-Object { $_.name }) -join ', '
        throw "$Label has failing cases: $names"
    }
}

function Get-FlowNames([string[]]$Paths, [string]$Pattern, [int]$Capture = 1) {
    $values = [System.Collections.Generic.List[string]]::new()
    foreach ($path in $Paths) {
        $text = [System.IO.File]::ReadAllText($path)
        foreach ($match in [regex]::Matches($text, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Multiline)) {
            $values.Add($match.Groups[$Capture].Value)
        }
    }
    return @($values)
}

function Test-ExistingSeed([string]$SeedRoot, [string]$Mode, [object[]]$ExpectedInputs, [string]$CliPath, [string]$BusinessPath) {
    $statePath = Join-Path $SeedRoot 'seed-state.json'
    $projectPath = Join-Path $SeedRoot 'project'
    if (-not (Test-Path -LiteralPath $statePath -PathType Leaf) -or -not (Test-Path -LiteralPath $projectPath -PathType Container)) {
        throw "Seed '$SeedRoot' exists without a complete seed-state.json and project. Refusing to replace it."
    }
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json -Depth 100
    if ($state.schemaVersion -ne 1 -or $state.mode -ne $Mode) {
        throw "Seed '$SeedRoot' has an unsupported or mismatched seed manifest."
    }
    if ($state.runtime.cliSha256 -ne (Get-Sha256 $CliPath) -or $state.runtime.businessSha256 -ne (Get-Sha256 $BusinessPath)) {
        throw "Seed '$SeedRoot' was prepared with different pinned runtime inputs."
    }
    if ((ConvertTo-CanonicalJson $state.sourceInputs) -cne (ConvertTo-CanonicalJson $ExpectedInputs)) {
        throw "Seed '$SeedRoot' no longer matches the current source input hashes."
    }
    $actualInventory = Get-ProjectInventory $projectPath
    if ((ConvertTo-CanonicalJson $state.project.files) -cne (ConvertTo-CanonicalJson $actualInventory)) {
        throw "Seed '$SeedRoot' project contents differ from its recorded inventory."
    }
    return $state
}

function Initialize-LanguageSeed([string]$Mode, [string]$SeedRoot, [string[]]$InputPaths, [string]$CliPath, [string]$BusinessPath, [bool]$Growing) {
    $sourceInputs = @($InputPaths | ForEach-Object {
        [pscustomobject]@{
            path = Get-RelativePath $script:RepoRoot $_
            sha256 = Get-Sha256 $_
        }
    })
    if (Test-Path -LiteralPath $SeedRoot) {
        return (Test-ExistingSeed $SeedRoot $Mode $sourceInputs $CliPath $BusinessPath)
    }

    $script:createdPaths.Add($SeedRoot)
    [System.IO.Directory]::CreateDirectory($SeedRoot) | Out-Null
    $projectPath = Join-Path $SeedRoot 'project'
    [System.IO.Directory]::CreateDirectory($projectPath) | Out-Null
    $fixtureDirectory = Join-Path $projectPath 'fixtures'
    [System.IO.Directory]::CreateDirectory($fixtureDirectory) | Out-Null
    $requests = [System.Collections.Generic.List[object]]::new()
    foreach ($inputPath in $InputPaths) {
        $name = [System.IO.Path]::GetFileName($inputPath)
        $fixturePath = Join-Path $fixtureDirectory $name
        [System.IO.File]::Copy($inputPath, $fixturePath, $false)
        $requests.Add([ordered]@{
            op = 'define'
            frontend = 'flow'
            source = [System.IO.File]::ReadAllText($inputPath)
        })
    }
    $requests.Add([ordered]@{ op = 'test-all' })
    $requests.Add([ordered]@{ op = 'commit'; library = $true })
    $responses = Invoke-AgentJsonl $projectPath $requests.ToArray() $CliPath
    if ($Growing) {
        Assert-TestResults $responses[$InputPaths.Count] 151 'Growing seed test-all'
        if ($sourceInputs.Count -ne 6) {
            throw 'Growing seed must comprise exactly the six frozen business Flow documents.'
        }
        $wordNames = Get-FlowNames $InputPaths '^\s*word\s+([A-Za-z0-9_.?!-]+)\s*\('
        $typeNames = Get-FlowNames $InputPaths '^\s*(?:type|record)\s+([A-Za-z][A-Za-z0-9_]*)\b'
        $testNames = Get-FlowNames $InputPaths '^\s*test\s+([A-Za-z0-9_./?!-]+)\s*\{'
        if ($wordNames.Count -ne 53 -or $typeNames.Count -ne 31 -or $testNames.Count -ne 151) {
            throw "Growing source inventory drifted: expected 53 words, 31 types, 151 tests; found $($wordNames.Count), $($typeNames.Count), $($testNames.Count)."
        }
        $forbiddenWords = @($wordNames | Where-Object { $_ -match '^customer\.(premium\?|discount-basis-points|discounted-balance)$' })
        if ($forbiddenWords.Count -gt 0) {
            throw 'The Growing foundation contains a task-specific policy word.'
        }
    }
    $commitResponse = $responses[$responses.Count - 1]
    if ($commitResponse.kind -ne 'commit') {
        throw "Expected one aggregate library commit, received '$($commitResponse.kind)'."
    }

    $verifyRequests = [System.Collections.Generic.List[object]]::new()
    $verifyRequests.Add([ordered]@{ op = 'words' })
    $verifyRequests.Add([ordered]@{ op = 'test-all' })
    $allTypes = Get-FlowNames $InputPaths '^\s*(?:type|record)\s+([A-Za-z][A-Za-z0-9_]*)\b'
    foreach ($typeName in $allTypes) {
        $verifyRequests.Add([ordered]@{ op = 'source'; type = $typeName })
    }
    $freshResponses = Invoke-AgentJsonl $projectPath $verifyRequests.ToArray() $CliPath
    if ($Growing) {
        Assert-TestResults $freshResponses[1] 151 'Growing fresh-process test-all'
        $inventory = @($freshResponses[0].data.words)
        foreach ($wordName in $wordNames) {
            $entries = @($inventory | Where-Object { $_.name -ceq $wordName })
            if ($entries.Count -ne 1 -or $entries[0].status -ne 'persistent' -or $entries[0].maturity -ne 'library') {
                throw "Authored word '$wordName' did not persist as a library word."
            }
        }
    }

    $runtime = [pscustomobject]@{
        cliPath = Get-RelativePath $script:RepoRoot $CliPath
        cliSha256 = Get-Sha256 $CliPath
        businessPath = Get-RelativePath $script:RepoRoot $BusinessPath
        businessSha256 = Get-Sha256 $BusinessPath
    }
    $projectInventory = Get-ProjectInventory $projectPath
    $state = [pscustomobject]@{
        schemaVersion = 1
        mode = $Mode
        sourceInputs = $sourceInputs
        runtime = $runtime
        project = [pscustomobject]@{
            files = $projectInventory
        }
        counts = [pscustomobject]@{
            authoredWords = if ($Growing) { 53 } else { 0 }
            types = if ($Growing) { 31 } else { $allTypes.Count }
            tests = if ($Growing) { 151 } else { 0 }
        }
    }
    Write-JsonFile (Join-Path $SeedRoot 'seed-state.json') $state
    return $state
}

function Initialize-ConventionalSeed([string]$SeedRoot, [string[]]$TemplatePaths, [string]$CliPath, [string]$BusinessPath, [string]$LocalRootPath) {
    $sourceInputs = @($TemplatePaths | ForEach-Object {
        [pscustomobject]@{
            path = Get-RelativePath $script:RepoRoot $_
            sha256 = Get-Sha256 $_
        }
    })
    if (Test-Path -LiteralPath $SeedRoot) {
        return (Test-ExistingSeed $SeedRoot 'conventional' $sourceInputs $CliPath $BusinessPath)
    }

    $script:createdPaths.Add($SeedRoot)
    [System.IO.Directory]::CreateDirectory($SeedRoot) | Out-Null
    $projectPath = Join-Path $SeedRoot 'project'
    [System.IO.Directory]::CreateDirectory($projectPath) | Out-Null
    foreach ($templatePath in $TemplatePaths) {
        $relative = Get-RelativePath (Join-Path $script:RepoRoot 'experiments/AgentLang.SubagentTrials/business-policy-001/conventional') $templatePath
        $destination = Join-Path $projectPath $relative
        [System.IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
        [System.IO.File]::Copy($templatePath, $destination, $false)
    }
    $libraryDirectory = Join-Path $projectPath 'lib'
    [System.IO.Directory]::CreateDirectory($libraryDirectory) | Out-Null
    $libraryCopy = Join-Path $libraryDirectory 'AgentLang.Business.dll'
    [System.IO.File]::Copy($BusinessPath, $libraryCopy, $false)

    $buildRoot = Join-Path $LocalRootPath ('scratch/conventional-seed-' + [guid]::NewGuid().ToString('N'))
    Assert-ContainedPath $LocalRootPath $buildRoot 'Conventional build scratch directory' | Out-Null
    [System.IO.Directory]::CreateDirectory($buildRoot) | Out-Null
    try {
        $projectFile = Join-Path $projectPath 'BusinessPolicy.fsproj'
        $buildLog = Join-Path $buildRoot 'build.log'
        & dotnet build $projectFile --configuration Release --artifacts-path $buildRoot --nologo *> $buildLog
        if ($LASTEXITCODE -ne 0) {
            $buildOutput = Get-Content -LiteralPath $buildLog -Raw
            throw "Conventional seed build failed with exit code $LASTEXITCODE. $buildOutput"
        }
        $programDll = Get-ChildItem -LiteralPath $buildRoot -File -Filter 'BusinessPolicy.dll' -Recurse | Select-Object -First 1
        if ($null -eq $programDll) {
            throw 'Conventional seed build produced no BusinessPolicy.dll.'
        }
        $testLog = Join-Path $buildRoot 'seed-tests.log'
        & dotnet $programDll.FullName *> $testLog
        if ($LASTEXITCODE -ne 0) {
            $testOutput = Get-Content -LiteralPath $testLog -Raw
            throw "Conventional seed immutable checks failed with exit code $LASTEXITCODE. $testOutput"
        }
    }
    finally {
        Remove-CreatedDirectory $buildRoot $LocalRootPath
    }

    $runtime = [pscustomobject]@{
        cliPath = Get-RelativePath $script:RepoRoot $CliPath
        cliSha256 = Get-Sha256 $CliPath
        businessPath = Get-RelativePath $script:RepoRoot $BusinessPath
        businessSha256 = Get-Sha256 $BusinessPath
        copiedBusinessSha256 = Get-Sha256 $libraryCopy
    }
    $state = [pscustomobject]@{
        schemaVersion = 1
        mode = 'conventional'
        sourceInputs = $sourceInputs
        runtime = $runtime
        project = [pscustomobject]@{
            files = Get-ProjectInventory $projectPath
        }
        counts = [pscustomobject]@{
            authoredWords = 0
            types = 0
            tests = 7
        }
    }
    Write-JsonFile (Join-Path $SeedRoot 'seed-state.json') $state
    return $state
}

function Get-SequenceIndex([string]$Id) {
    switch ($Id) {
        'S01' { return 1 }
        'S06' { return 2 }
        'S07' { return 3 }
        default { throw "Unknown task in sequence: $Id" }
    }
}

function Assert-PassedPrevious([string]$Mode, [string]$Task, [string]$ProjectPath, [string]$AcceptancePath) {
    $taskIndex = Get-SequenceIndex $Task
    if ($taskIndex -eq 1) {
        if (-not [string]::IsNullOrWhiteSpace($ProjectPath) -or -not [string]::IsNullOrWhiteSpace($AcceptancePath)) {
            throw 'S01 must start from the frozen seed and must not receive previous-stage inputs.'
        }
        return $null
    }
    if ([string]::IsNullOrWhiteSpace($ProjectPath) -or [string]::IsNullOrWhiteSpace($AcceptancePath)) {
        throw "$Task in $Mode requires both -PreviousProject and -PreviousAcceptance from the immediately preceding passing task."
    }
    $previousIndex = $taskIndex - 1
    $previousTask = @('S01', 'S06', 'S07')[$previousIndex - 1]
    $fullProject = Assert-ContainedPath $script:RepoRoot (Resolve-RepoPath $ProjectPath) 'Previous project'
    $fullAcceptance = Assert-ContainedPath $script:RepoRoot (Resolve-RepoPath $AcceptancePath) 'Previous acceptance'
    Assert-NoReparseAncestors $script:RepoRoot $fullProject
    Assert-NoReparseAncestors $script:RepoRoot $fullAcceptance
    if (-not (Test-Path -LiteralPath $fullProject -PathType Container) -or -not (Test-Path -LiteralPath $fullAcceptance -PathType Leaf)) {
        throw 'Previous accepted project and acceptance evidence must both exist.'
    }
    Assert-NoReparsePoints $fullProject
    $acceptance = Get-Content -LiteralPath $fullAcceptance -Raw | ConvertFrom-Json -Depth 100
    if ($acceptance.schemaVersion -ne 1 -or $acceptance.studyId -ne 'business-policy-001' -or $acceptance.mode -ne $Mode -or $acceptance.taskId -ne $previousTask -or $acceptance.passed -ne $true) {
        throw "Previous acceptance must be a passing $Mode/$previousTask artifact."
    }
    if ($acceptance.sequenceIndex -ne $previousIndex) {
        throw "Previous acceptance sequenceIndex must be $previousIndex."
    }
    return [pscustomobject]@{
        taskId = $previousTask
        projectPath = $fullProject
        acceptancePath = $fullAcceptance
        acceptanceSha256 = Get-Sha256 $fullAcceptance
    }
}

$localRootFull = Assert-ContainedPath $script:RepoRoot (Resolve-RepoPath $LocalRoot) 'LocalRoot'
$runRootFull = Assert-ContainedPath $script:RepoRoot (Resolve-RepoPath $RunRoot) 'RunRoot'
$cliPath = Resolve-RepoPath $CliDll
$businessPath = Resolve-RepoPath $BusinessDll
Assert-NoReparseAncestors $script:RepoRoot $localRootFull
Assert-NoReparseAncestors $script:RepoRoot $runRootFull
foreach ($input in @(@{ label = 'Pinned CLI'; path = $cliPath }, @{ label = 'Business DLL'; path = $businessPath })) {
    Assert-ContainedPath $script:RepoRoot $input.path $input.label | Out-Null
    if (-not (Test-Path -LiteralPath $input.path -PathType Leaf)) {
        throw "$($input.label) does not exist: $($input.path)"
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $localRootFull '.'))) {
    [System.IO.Directory]::CreateDirectory($localRootFull) | Out-Null
}

$previous = $null
if ($Mode -eq 'flat') {
    if (-not [string]::IsNullOrWhiteSpace($PreviousProject) -or -not [string]::IsNullOrWhiteSpace($PreviousAcceptance)) {
        throw 'Flat mode always starts from its schema-only reset and does not accept previous-stage inputs.'
    }
}
else {
    $previous = Assert-PassedPrevious $Mode $TaskId $PreviousProject $PreviousAcceptance
}

$actorName = "$Mode-$TaskId"
$actorRoot = Join-Path $localRootFull "actors/$actorName"
$runDirectory = Join-Path $runRootFull $actorName
$startingProject = Join-Path $runDirectory 'starting-project'
if ((Test-Path -LiteralPath $actorRoot) -or (Test-Path -LiteralPath $runDirectory)) {
    throw "Fresh actor and run destinations must not already exist ('$actorRoot' or '$runDirectory')."
}
Assert-ContainedPath $localRootFull $actorRoot 'Actor destination' | Out-Null
Assert-ContainedPath $runRootFull $runDirectory 'Run destination' | Out-Null
Assert-NoReparseAncestors $localRootFull $actorRoot
Assert-NoReparseAncestors $runRootFull $runDirectory

$seedRoot = Join-Path $localRootFull "seeds/$Mode"
Assert-NoReparseAncestors $localRootFull $seedRoot
$script:createdPaths = [System.Collections.Generic.List[string]]::new()
$success = $false
try {
    switch ($Mode) {
        'growing' {
            $inputPaths = @(
                (Join-Path $script:RepoRoot 'examples/business-values.agent'),
                (Join-Path $script:RepoRoot 'examples/business-store.agent'),
                (Join-Path $script:RepoRoot 'examples/business-state.agent'),
                (Join-Path $script:RepoRoot 'examples/business-subscriptions.agent'),
                (Join-Path $script:RepoRoot 'examples/business-invoices.agent'),
                (Join-Path $script:RepoRoot 'examples/business-payments-email.agent')
            )
            $seedState = Initialize-LanguageSeed 'growing' $seedRoot $inputPaths $cliPath $businessPath $true
        }
        'flat' {
            $flatSource = Join-Path $localRootFull 'flat-customer.agent'
            $flatText = @'
# Flat reset: typed Customer shape only; no task policy words.
type CustomerId : String {
    validate string::guid-canonical?;
}

type ProductId : String {
    validate string::guid-canonical?;
}

type Email : String {
    validate string::email-address-valid?;
}

type Money : Int { }

type Instant : String {
    validate instant::is-canonical-utc?;
}

record Customer {
    field id: CustomerId;
    field email: Email;
    field kind: String;
    field balance: Money;
    field created-at: Instant;
}
'@
            if (Test-Path -LiteralPath $flatSource -PathType Leaf) {
                if ((Get-Content -LiteralPath $flatSource -Raw) -cne $flatText) {
                    throw "Flat source '$flatSource' differs from the frozen schema-only definition."
                }
            }
            else {
                [System.IO.File]::WriteAllText($flatSource, $flatText, [System.Text.UTF8Encoding]::new($false))
            }
            $seedState = Initialize-LanguageSeed 'flat' $seedRoot @($flatSource) $cliPath $businessPath $false
            $fieldCount = ([regex]::Matches($flatText, '(?m)^\s*field\s+')).Count
            if ($fieldCount -ne 5 -or [regex]::IsMatch($flatText, '(?m)^\s*word\s+')) {
                throw 'Flat seed must contain exactly the five Customer fields and zero authored policy words.'
            }
        }
        'conventional' {
            $templateRoot = Join-Path $script:RepoRoot 'experiments/AgentLang.SubagentTrials/business-policy-001/conventional'
            $templatePaths = @(
                (Join-Path $templateRoot 'BusinessPolicy.fsproj'),
                (Join-Path $templateRoot 'Domain.fs'),
                (Join-Path $templateRoot 'Operations.fs'),
                (Join-Path $templateRoot 'SelfTests.fs'),
                (Join-Path $templateRoot 'Program.fs')
            )
            $seedState = Initialize-ConventionalSeed $seedRoot $templatePaths $cliPath $businessPath $localRootFull
        }
    }

    $seedProject = Join-Path $seedRoot 'project'
    $sourceProject = if ($null -ne $previous) { $previous.projectPath } else { $seedProject }
    $sourceInventory = Get-ProjectInventory $sourceProject
    $script:createdPaths.Add($actorRoot)
    [System.IO.Directory]::CreateDirectory($runDirectory) | Out-Null
    $script:createdPaths.Add($runDirectory)
    $startingInventory = Copy-ProjectTree $sourceProject $startingProject
    $actorInventory = Copy-ProjectTree $sourceProject $actorRoot
    if ((ConvertTo-CanonicalJson $startingInventory) -cne (ConvertTo-CanonicalJson $actorInventory)) {
        throw 'Actor working project differs from the immutable starting-project snapshot.'
    }

    $sourceInputs = @($seedState.sourceInputs)
    $previousEvidence = $null
    if ($null -ne $previous) {
        $previousEvidence = [pscustomobject]@{
            taskId = $previous.taskId
            acceptancePath = $previous.acceptancePath
            acceptanceSha256 = $previous.acceptanceSha256
        }
    }
    $startingState = [pscustomobject]@{
        schemaVersion = 1
        studyId = 'business-policy-001'
        mode = $Mode
        taskId = $TaskId
        sequenceIndex = Get-SequenceIndex $TaskId
        seed = [pscustomobject]@{
            path = $sourceProject
            inventorySha256 = (Get-Sha256FromInventory $sourceInventory)
        }
        sourceInputs = $sourceInputs
        runtime = $seedState.runtime
        previousAcceptance = $previousEvidence
        project = [pscustomobject]@{
            path = $startingProject
            files = $startingInventory
            inventorySha256 = Get-Sha256FromInventory $startingInventory
        }
        actor = [pscustomobject]@{
            projectPath = $actorRoot
            files = $actorInventory
            inventorySha256 = Get-Sha256FromInventory $actorInventory
        }
    }
    Write-JsonFile (Join-Path $runDirectory 'starting-state.json') $startingState
    $success = $true
    [pscustomobject]@{
        mode = $Mode
        taskId = $TaskId
        actorProject = $actorRoot
        startingProject = $startingProject
        startingState = Join-Path $runDirectory 'starting-state.json'
        seedProject = $seedProject
        authoredWordCount = $seedState.counts.authoredWords
        typeCount = $seedState.counts.types
        testCount = $seedState.counts.tests
    } | ConvertTo-Json -Depth 20
}
finally {
    if (-not $success) {
        foreach ($createdPath in @($script:createdPaths | Sort-Object Length -Descending)) {
            $containmentRoot = if ($createdPath.StartsWith($localRootFull, [System.StringComparison]::OrdinalIgnoreCase)) { $localRootFull } else { $runRootFull }
            Remove-CreatedDirectory $createdPath $containmentRoot
        }
    }
}
