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

function Invoke-FlowOwnedSourceExtractor([string[]]$Paths, [string[]]$Owners, [string]$RuntimeDll, [string]$SeedRoot) {
    $extractorPath = Join-Path $script:RepoRoot 'scripts/Extract-FlowOwnedSource.fsx'
    $corePath = Join-Path (Split-Path -Parent (Get-FullPath $RuntimeDll)) 'AgentLang.Core.dll'
    if (-not (Test-Path -LiteralPath $extractorPath -PathType Leaf) -or -not (Test-Path -LiteralPath $corePath -PathType Leaf)) {
        throw "Flow source extraction requires '$extractorPath' and the pinned sibling Core assembly '$corePath'."
    }

    $requestPath = Join-Path $SeedRoot 'project-only-source-request.json'
    $outputPath = Join-Path $SeedRoot 'project-only-source.json'
    Write-JsonFile $requestPath ([pscustomobject]@{ paths = @($Paths | ForEach-Object { Get-FullPath $_ }); owners = $Owners })
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'dotnet'
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.ArgumentList.Add('fsi')
    $startInfo.ArgumentList.Add('--quiet')
    $startInfo.ArgumentList.Add('--exec')
    $startInfo.ArgumentList.Add("--reference:$corePath")
    $startInfo.ArgumentList.Add($extractorPath)
    $startInfo.Environment['AGENTLANG_FLOW_SOURCE_REQUEST'] = $requestPath

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw 'Could not start the .NET F# Interactive source extractor.'
    }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    try {
        if (-not $process.WaitForExit(60000)) {
            $process.Kill($true)
            throw 'F# Interactive source extraction exceeded 60 seconds.'
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            throw "F# Interactive source extraction failed ($($process.ExitCode)). stdout: $stdout stderr: $stderr"
        }
    }
    finally {
        if (-not $process.HasExited) {
            $process.Kill($true)
        }
        $process.Dispose()
    }
    if ([string]::IsNullOrWhiteSpace($stdout)) {
        throw "F# Interactive source extraction produced no JSON output. stderr: $stderr"
    }
    [System.IO.File]::WriteAllText($outputPath, $stdout, [System.Text.UTF8Encoding]::new($false))
    try {
        $result = ConvertFrom-Json -InputObject $stdout -Depth 100
    }
    catch {
        throw "F# Interactive source extraction did not emit valid JSON. stdout: $stdout stderr: $stderr"
    }
    $sourceBytes = [System.Text.UTF8Encoding]::new($false).GetBytes([string]$result.source)
    $sourceHash = [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($sourceBytes)).ToLowerInvariant()
    if ($sourceHash -cne $result.sourceSha256) {
        throw 'F# Interactive source extraction output hash does not match its exact source text.'
    }
    return [pscustomobject]@{
        source = [string]$result.source
        sourceSha256 = [string]$result.sourceSha256
        words = @($result.words | ForEach-Object { [string]$_ })
        tests = @($result.tests | ForEach-Object { [string]$_ })
        examples = @($result.examples | ForEach-Object { [string]$_ })
        requestPath = $requestPath
        outputPath = $outputPath
    }
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

function Get-SeedCount([object]$Counts, [string]$Name) {
    $property = $Counts.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return 0
    }
    return [int]$property.Value
}

function Assert-WordInventory([object]$Response, [string[]]$ExpectedNames, [string]$ExpectedStatus, [string[]]$LibraryNames, [string]$Label) {
    $expectedNames = @($ExpectedNames | Sort-Object -Unique)
    if ($expectedNames.Count -ne $ExpectedNames.Count) {
        throw "$Label expected-word inventory contains duplicate names."
    }
    foreach ($name in $ExpectedNames) {
        $entries = @($Response.data.words | Where-Object { $_.name -ceq $name })
        if ($entries.Count -ne 1 -or $entries[0].status -cne $ExpectedStatus) {
            throw "$Label expected '$name' to have status '$ExpectedStatus'."
        }
        if ($ExpectedStatus -ceq 'persistent') {
            $expectedMaturity = if ($LibraryNames -ccontains $name) { 'library' } else { 'project' }
            if ($entries[0].maturity -cne $expectedMaturity) {
                throw "$Label expected '$name' to have maturity '$expectedMaturity'."
            }
        }
    }
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
    if ($Mode -eq 'growing') {
        $expectedCounts = [ordered]@{
            authoredWords = 59
            types = 33
            tests = 184
            examples = 47
            libraryWords = 57
            projectWords = 2
        }
        foreach ($countName in $expectedCounts.Keys) {
            if ((Get-SeedCount $state.counts $countName) -ne $expectedCounts[$countName]) {
                throw "Seed '$SeedRoot' has a stale or incomplete '$countName' count; regenerate it from current source inputs."
            }
        }
        $sourceExtractionProperty = $state.PSObject.Properties['sourceExtraction']
        if ($null -eq $sourceExtractionProperty -or $null -eq $sourceExtractionProperty.Value -or $sourceExtractionProperty.Value.sourceSha256 -notmatch '^[0-9a-f]{64}$') {
            throw "Seed '$SeedRoot' lacks a valid parsed-source extraction record."
        }
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

    $wordNames = @()
    $typeNames = @()
    $testNames = @()
    $exampleNames = @()
    $projectNames = @('email.delivery-fold-step', 'email.apply-delivery-result')
    $libraryNames = @()
    if ($Growing) {
        $wordNames = Get-FlowNames $InputPaths '^\s*(?:word|fn)\s+([A-Za-z0-9_.?!-]+)\s*\('
        $typeNames = Get-FlowNames $InputPaths '^\s*(?:type|record)\s+([A-Za-z][A-Za-z0-9_]*)\b'
        $testNames = Get-FlowNames $InputPaths '^\s*test\s+([A-Za-z0-9_./?!-]+)\s*\{'
        $exampleNames = Get-FlowNames $InputPaths '^\s*example\s+([A-Za-z0-9_./?!-]+)\s*\{'
        if ($sourceInputs.Count -ne 6) {
            throw 'Growing seed must comprise exactly the six frozen business Flow documents.'
        }
        if ($wordNames.Count -ne 59 -or $typeNames.Count -ne 33 -or $testNames.Count -ne 184 -or $exampleNames.Count -ne 47) {
            throw "Growing source inventory drifted: expected 59 words, 33 types, 184 tests, and 47 examples; found $($wordNames.Count), $($typeNames.Count), $($testNames.Count), and $($exampleNames.Count)."
        }
        if (@($wordNames | Sort-Object -Unique).Count -ne 59 -or @($typeNames | Sort-Object -Unique).Count -ne 33) {
            throw 'Growing source inventory contains duplicate word or type names.'
        }
        foreach ($name in $projectNames) {
            if (@($wordNames | Where-Object { $_ -ceq $name }).Count -ne 1) {
                throw "Growing seed must contain exactly one project-only function named '$name'."
            }
        }
        $libraryNames = @($wordNames | Where-Object { $projectNames -cnotcontains $_ })
        if ($libraryNames.Count -ne 57) {
            throw "Growing seed expected 57 library functions after maturity split; found $($libraryNames.Count)."
        }
        $forbiddenWords = @($wordNames | Where-Object { $_ -match '^customer\.(premium\?|discount-basis-points|discounted-balance)$' })
        if ($forbiddenWords.Count -gt 0) {
            throw 'The Growing foundation contains a task-specific policy word.'
        }
    }

    $script:createdPaths.Add($SeedRoot)
    [System.IO.Directory]::CreateDirectory($SeedRoot) | Out-Null
    $projectOnlySource = $null
    if ($Growing) {
        $projectOwnerInputPaths = @($InputPaths | Where-Object { [System.IO.Path]::GetFileName($_) -ceq 'business-payments-email.agent' })
        if ($projectOwnerInputPaths.Count -ne 1) {
            throw 'Growing seed source extraction requires business-payments-email.agent as the project-only owner document.'
        }
        $projectOnlySource = Invoke-FlowOwnedSourceExtractor $projectOwnerInputPaths $projectNames $CliPath $SeedRoot
        $actualOwners = @($projectOnlySource.words | Sort-Object -CaseSensitive)
        $expectedOwners = @($projectNames | Sort-Object -CaseSensitive)
        if (($actualOwners -join "`n") -cne ($expectedOwners -join "`n")) {
            throw 'Parsed project-only source does not contain exactly the two maturity exceptions.'
        }

        $expectedProjectTests = [System.Collections.Generic.List[string]]::new()
        foreach ($testName in $testNames) {
            foreach ($owner in $projectNames) {
                if ($testName.StartsWith($owner + '/', [StringComparison]::Ordinal)) {
                    $expectedProjectTests.Add($testName)
                    break
                }
            }
        }
        $actualProjectTests = @($projectOnlySource.tests | Sort-Object -CaseSensitive)
        $expectedProjectTestsSorted = @($expectedProjectTests | Sort-Object -CaseSensitive)
        if (($actualProjectTests -join "`n") -cne ($expectedProjectTestsSorted -join "`n")) {
            throw 'Parsed project-only test attachments differ from the current source inventory.'
        }

        $expectedProjectExamples = [System.Collections.Generic.List[string]]::new()
        foreach ($exampleName in $exampleNames) {
            foreach ($owner in $projectNames) {
                if ($exampleName.StartsWith($owner + '/', [StringComparison]::Ordinal)) {
                    $expectedProjectExamples.Add($exampleName)
                    break
                }
            }
        }
        $actualProjectExamples = @($projectOnlySource.examples | Sort-Object -CaseSensitive)
        $expectedProjectExamplesSorted = @($expectedProjectExamples | Sort-Object -CaseSensitive)
        if (($actualProjectExamples -join "`n") -cne ($expectedProjectExamplesSorted -join "`n")) {
            throw 'Parsed project-only example attachments differ from the current source inventory.'
        }
        if ((184 - $projectOnlySource.tests.Count) -ne 179) {
            throw "Expected the project-only functions to own five of the 184 seed tests; found $($projectOnlySource.tests.Count)."
        }
    }
    $projectPath = Join-Path $SeedRoot 'project'
    [System.IO.Directory]::CreateDirectory($projectPath) | Out-Null
    $fixtureDirectory = Join-Path $projectPath 'fixtures'
    [System.IO.Directory]::CreateDirectory($fixtureDirectory) | Out-Null
    $requests = [System.Collections.Generic.List[object]]::new()
    foreach ($inputPath in $InputPaths) {
        $name = [System.IO.Path]::GetFileName($inputPath)
        $syntaxVersion = if ($name -ceq 'business-subscriptions.agent') { 2 } else { 1 }
        $fixturePath = Join-Path $fixtureDirectory $name
        [System.IO.File]::Copy($inputPath, $fixturePath, $false)
        $requests.Add([ordered]@{
            op = 'define'
            frontend = 'flow'
            syntaxVersion = $syntaxVersion
            source = [System.IO.File]::ReadAllText($inputPath)
        })
    }
    $initialTestsIndex = $requests.Count
    $requests.Add([ordered]@{ op = 'test-all' })
    if ($Growing) {
        $beforeDiscardWordsIndex = $requests.Count
        $requests.Add([ordered]@{ op = 'words' })
        $discardCallerIndex = $requests.Count
        $requests.Add([ordered]@{ op = 'discard'; word = 'email.apply-delivery-result' })
        $discardHelperIndex = $requests.Count
        $requests.Add([ordered]@{ op = 'discard'; word = 'email.delivery-fold-step' })
        $remainingTestsIndex = $requests.Count
        $requests.Add([ordered]@{ op = 'test-all' })
        $remainingWordsIndex = $requests.Count
        $requests.Add([ordered]@{ op = 'words' })
        $libraryCommitIndex = $requests.Count
        $requests.Add([ordered]@{ op = 'commit'; library = $true })
        $libraryWordsIndex = $requests.Count
        $requests.Add([ordered]@{ op = 'words' })
        $restoreSourceIndex = $requests.Count
        $requests.Add([ordered]@{ op = 'define'; frontend = 'flow'; syntaxVersion = 1; source = $projectOnlySource.source })
        $restoredTestsIndex = $requests.Count
        $requests.Add([ordered]@{ op = 'test-all' })
        $projectCommitIndex = $requests.Count
        $requests.Add([ordered]@{ op = 'commit-word'; word = 'email.apply-delivery-result'; library = $false })
        $finalWordsIndex = $requests.Count
        $requests.Add([ordered]@{ op = 'words' })
    }
    else {
        $libraryCommitIndex = $requests.Count
        $requests.Add([ordered]@{ op = 'commit'; library = $true })
    }
    $responses = Invoke-AgentJsonl $projectPath $requests.ToArray() $CliPath
    if ($Growing) {
        Assert-TestResults $responses[$initialTestsIndex] 184 'Growing full-source seed test-all'
        Assert-WordInventory $responses[$beforeDiscardWordsIndex] $wordNames 'candidate' @() 'Growing full staged-word inventory'
        if ($responses[$discardCallerIndex].kind -cne 'discard' -or $responses[$discardHelperIndex].kind -cne 'discard') {
            throw 'Expected to discard the two finite-domain project-only functions before the aggregate library commit.'
        }
        Assert-TestResults $responses[$remainingTestsIndex] 179 'Growing library-only test-all after project-only discard'
        Assert-WordInventory $responses[$remainingWordsIndex] $libraryNames 'candidate' @() 'Growing library-only staged-word inventory'
        if ($responses[$libraryCommitIndex].kind -cne 'commit') {
            throw 'Expected one aggregate commit for the complete library-eligible vocabulary.'
        }
        Assert-WordInventory $responses[$libraryWordsIndex] $libraryNames 'persistent' $libraryNames 'Growing aggregate library commit'
        if ($responses[$restoreSourceIndex].kind -cne 'defined') {
            throw 'Expected the parsed project-only source declarations and attachments to be restored as candidates.'
        }
        Assert-TestResults $responses[$restoredTestsIndex] 184 'Growing restored full-source test-all'
        if ($responses[$projectCommitIndex].kind -cne 'commit') {
            throw 'Expected the two finite-domain project-only functions to commit together as project vocabulary.'
        }
        Assert-WordInventory $responses[$finalWordsIndex] ($libraryNames + $projectNames) 'persistent' $libraryNames 'Growing final seed inventory'
    }
    else {
        if ($responses[$libraryCommitIndex].kind -ne 'commit') {
            throw 'Expected the language seed aggregate commit to succeed.'
        }
    }

    $verifyRequests = [System.Collections.Generic.List[object]]::new()
    $verifyRequests.Add([ordered]@{ op = 'words' })
    $verifyRequests.Add([ordered]@{ op = 'test-all' })
    $allTypes = if ($Growing) { $typeNames } else { Get-FlowNames $InputPaths '^\s*(?:type|record)\s+([A-Za-z][A-Za-z0-9_]*)\b' }
    $describeIndexes = @{}
    if ($Growing) {
        foreach ($wordName in $wordNames) {
            $describeIndexes[$wordName] = $verifyRequests.Count
            $verifyRequests.Add([ordered]@{ op = 'describe'; word = $wordName })
        }
    }
    $typeSourceIndexes = @{}
    foreach ($typeName in $allTypes) {
        $typeSourceIndexes[$typeName] = $verifyRequests.Count
        $verifyRequests.Add([ordered]@{ op = 'source'; type = $typeName })
    }
    $freshResponses = Invoke-AgentJsonl $projectPath $verifyRequests.ToArray() $CliPath
    if ($Growing) {
        Assert-TestResults $freshResponses[1] 184 'Growing fresh-process test-all'
        Assert-WordInventory $freshResponses[0] ($libraryNames + $projectNames) 'persistent' $libraryNames 'Growing fresh-process inventory'
        $inventory = @($freshResponses[0].data.words)
        foreach ($wordName in $wordNames) {
            $entries = @($inventory | Where-Object { $_.name -ceq $wordName })
            if ($entries.Count -ne 1 -or $entries[0].status -cne 'persistent') {
                throw "Authored word '$wordName' did not persist in the fresh process."
            }
            $description = $freshResponses[$describeIndexes[$wordName]].data
            if ($description.kind -cne 'word') {
                throw "Source declaration '$wordName' did not persist as an authored function."
            }
            $expectedTests = @($testNames | Where-Object { $_.StartsWith($wordName + '/', [StringComparison]::Ordinal) } | ForEach-Object { $_.Substring($wordName.Length + 1) } | Sort-Object -CaseSensitive)
            $actualTests = @($description.tests | ForEach-Object { [string]$_ } | Sort-Object -CaseSensitive)
            if (($actualTests -join "`n") -cne ($expectedTests -join "`n")) {
                throw "Fresh process attached tests for '$wordName' differ from the frozen source inventory."
            }
            $expectedExamples = @($exampleNames | Where-Object { $_.StartsWith($wordName + '/', [StringComparison]::Ordinal) } | ForEach-Object { $_.Substring($wordName.Length + 1) } | Sort-Object -CaseSensitive)
            $actualExamples = @($description.examples | ForEach-Object { [string]$_ } | Sort-Object -CaseSensitive)
            if (($actualExamples -join "`n") -cne ($expectedExamples -join "`n")) {
                throw "Fresh process attached examples for '$wordName' differ from the frozen source inventory."
            }
        }
        $durableTests = @($wordNames | ForEach-Object { $freshResponses[$describeIndexes[$_]].data.testCount } | Measure-Object -Sum).Sum
        $durableExamples = @($wordNames | ForEach-Object { $freshResponses[$describeIndexes[$_]].data.exampleCount } | Measure-Object -Sum).Sum
        if ($durableTests -ne 184 -or $durableExamples -ne 47) {
            throw "Fresh process metadata inventory drifted: expected 184 tests and 47 examples; found $durableTests and $durableExamples."
        }
        foreach ($typeName in $allTypes) {
            if ($freshResponses[$typeSourceIndexes[$typeName]].kind -cne 'source') {
                throw "Fresh process could not inspect persisted type '$typeName'."
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
        sourceExtraction = if ($Growing) {
            [pscustomobject]@{
                parser = 'AgentLang.Core.FlowParser.parseDocumentWithVersion (Flow/1 project-only owner documents)'
                owners = @($projectNames)
                sourceSha256 = $projectOnlySource.sourceSha256
                words = @($projectOnlySource.words)
                tests = @($projectOnlySource.tests)
                examples = @($projectOnlySource.examples)
            }
        }
        else { $null }
        runtime = $runtime
        project = [pscustomobject]@{
            files = $projectInventory
        }
        counts = [pscustomobject]@{
            authoredWords = if ($Growing) { 59 } else { 0 }
            types = if ($Growing) { 33 } else { $allTypes.Count }
            tests = if ($Growing) { 184 } else { 0 }
            examples = if ($Growing) { 47 } else { 0 }
            libraryWords = if ($Growing) { 57 } else { 0 }
            projectWords = if ($Growing) { 2 } else { 0 }
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
            examples = 0
            libraryWords = 0
            projectWords = 0
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
        exampleCount = Get-SeedCount $seedState.counts 'examples'
        libraryWordCount = Get-SeedCount $seedState.counts 'libraryWords'
        projectWordCount = Get-SeedCount $seedState.counts 'projectWords'
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
