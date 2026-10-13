[CmdletBinding()]
#requires -Version 7.0
param(
    [string] $RunRoot,
    [ValidateSet('Diagnostic', 'TrustedGenerated')]
    [string] $RuntimeProfile = 'Diagnostic',
    [ValidateSet('Off', 'Detailed')]
    [string] $ExecutionProfile = 'Off'
)

$ErrorActionPreference = 'Stop'
# Preserve native stderr in the tee'd logs while checking each external exit code explicitly.
$PSNativeCommandUseErrorActionPreference = $false
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$privateRunsRoot = Join-Path $repositoryRoot '.agentlang/campfire-native-room-read-252/test-runs'
if ([string]::IsNullOrWhiteSpace($RunRoot)) {
    $stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
    $RunRoot = Join-Path $privateRunsRoot ($stamp + '-' + [Guid]::NewGuid().ToString('N'))
}
$RunRoot = [System.IO.Path]::GetFullPath($RunRoot)
$privateRunsRoot = [System.IO.Path]::GetFullPath($privateRunsRoot)
if (-not $RunRoot.StartsWith($privateRunsRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'RunRoot must remain under .agentlang/campfire-native-room-read-252/test-runs.'
}
if (Test-Path -LiteralPath $RunRoot -PathType Container) {
    if (Get-ChildItem -LiteralPath $RunRoot -Force | Select-Object -First 1) {
        throw 'RunRoot must be a new or empty directory; retained verification evidence is never overwritten.'
    }
}
[System.IO.Directory]::CreateDirectory($RunRoot) | Out-Null

$project = Join-Path $repositoryRoot 'tests/AgentLang.Campfire.NativeRoomRead.Tests/AgentLang.Campfire.NativeRoomRead.Tests.fsproj'
$artifacts = Join-Path $RunRoot 'test-dotnet-artifacts'
$restoreLog = Join-Path $RunRoot 'test-restore.log'
$buildLog = Join-Path $RunRoot 'test-build.log'
$runLog = Join-Path $RunRoot 'test-run.log'
$sourceFreezePath = Join-Path $RunRoot 'test-source-freeze.json'
$staticSourceFiles = @(
    'applications/campfire/Native/room_read_adapter.c',
    'applications/campfire/Native/room_read_adapter.h',
    'applications/campfire/Native/room_read_pure.c',
    'applications/campfire/Native/room_read_pure.h',
    'applications/campfire/Native/room_read_sqlite.def',
    'applications/campfire/Native/room_read_sqlite_subset.h',
    'src/AgentLang.Llvm/native/campfire_room_read_host_abi.h',
    'src/AgentLang.Llvm/native/owning_stack_runtime.c',
    'src/AgentLang.Llvm/native/owning_stack_runtime.h',
    'src/AgentLang.Llvm/native/owning_stack_profile.h',
    'src/AgentLang.Llvm/native/float_format.h',
    'src/AgentLang.Llvm/AgentLang.Llvm.fsproj',
    'src/AgentLang.Llvm/LlvmToolchain.fs',
    'src/AgentLang.Llvm/OwningStackAot.fs',
    'tests/AgentLang.Campfire.NativeRoomRead.Tests/AgentLang.Campfire.NativeRoomRead.Tests.fsproj',
    'tests/AgentLang.Campfire.NativeRoomRead.Tests/NativeRoomReadTests.fs',
    'tests/AgentLang.Campfire.NativeRoomRead.Tests/Program.fs',
    'tests/AgentLang.Campfire.NativeRoomRead.Tests/native/room_read_standalone_smoke.c',
    'scripts/Verify-CampfireNativeRoomRead.ps1'
)
function Get-PinnedSourceFiles {
    $campfireRoot = Join-Path $repositoryRoot 'applications/campfire'
    $applicationSources = @(
        Get-ChildItem -LiteralPath $campfireRoot -Recurse -File |
            Where-Object {
                $_.Extension -in @('.flow', '.fs', '.fsproj') -and
                -not (($_.FullName.Substring($campfireRoot.Length) -split '[\\/]') |
                    Where-Object { $_ -in @('bin', 'obj') })
            } |
            ForEach-Object { [System.IO.Path]::GetRelativePath($repositoryRoot, $_.FullName).Replace('\', '/') }
    )
    return @($staticSourceFiles + $applicationSources | Sort-Object -Unique)
}
function Get-SourceHashes([string[]] $files) {
    $hashes = [ordered]@{}
    foreach ($relativePath in $files) {
        $fullPath = Join-Path $repositoryRoot $relativePath
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "Pinned native room-read source is missing: $relativePath"
        }
        $hashes[$relativePath] = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    return $hashes
}
$sourceFilesBefore = Get-PinnedSourceFiles
$sourceHashesBefore = Get-SourceHashes $sourceFilesBefore
New-Item -ItemType File -Path $runLog -Force | Out-Null
& dotnet restore $project --artifacts-path $artifacts -m:1 -p:NuGetAudit=false --verbosity minimal 2>&1 |
    Tee-Object -FilePath $restoreLog
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE" }

& dotnet build $project -c Release --no-restore --artifacts-path $artifacts -m:1 --verbosity minimal 2>&1 |
    Tee-Object -FilePath $buildLog
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

$testDll = Get-ChildItem (Join-Path $artifacts 'bin') -Filter 'AgentLang.Campfire.NativeRoomRead.Tests.dll' -Recurse -File |
    Where-Object { $_.FullName -notmatch '[\\/]ref[\\/]' } |
    Select-Object -First 1
if ($null -eq $testDll) { throw 'The standalone native room-read test assembly was not produced.' }

$runErrorActionPreference = $ErrorActionPreference
try {
    $ErrorActionPreference = 'Continue'
    & dotnet $testDll.FullName --run-root $RunRoot --runtime-profile $RuntimeProfile --execution-profile $ExecutionProfile 2>&1 |
        Tee-Object -FilePath $runLog
    $runExitCode = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $runErrorActionPreference
}
$sourceFilesAfter = Get-PinnedSourceFiles
$sourceHashesAfter = Get-SourceHashes $sourceFilesAfter
$allPinnedFiles = @($sourceFilesBefore + $sourceFilesAfter | Sort-Object -Unique)
$changedSourceFiles = @($allPinnedFiles | Where-Object {
    -not $sourceHashesBefore.Contains($_) -or
    -not $sourceHashesAfter.Contains($_) -or
    $sourceHashesBefore[$_] -ne $sourceHashesAfter[$_]
})
$sourceFreeze = [ordered]@{
    stable = ($changedSourceFiles.Count -eq 0)
    runtimeProfile = $RuntimeProfile
    executionProfile = $ExecutionProfile
    changedFiles = $changedSourceFiles
    filesBefore = $sourceFilesBefore
    filesAfter = $sourceFilesAfter
    before = $sourceHashesBefore
    after = $sourceHashesAfter
}
$sourceFreeze | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $sourceFreezePath -Encoding utf8NoBOM
if ($changedSourceFiles.Count -ne 0) { throw "Pinned room-read source changed during validation: $($changedSourceFiles -join ', ')" }
if ($runExitCode -ne 0) { throw "native room-read verification failed with exit code $runExitCode" }
$receipt = Get-Content -LiteralPath (Join-Path $RunRoot 'test-receipt.json') -Raw -Encoding UTF8 | ConvertFrom-Json -AsHashtable
$expectedRuntimeProfile = if ($RuntimeProfile -eq 'Diagnostic') { 'diagnostic' } else { 'trusted-generated' }
$expectedExecutionProfile = $ExecutionProfile.ToLowerInvariant()
if (-not [bool]$receipt['passed'] -or
    [string]$receipt['runtimeProfile'] -cne $expectedRuntimeProfile -or
    [string]$receipt['executionProfile'] -cne $expectedExecutionProfile) {
    throw "Room-read receipt does not confirm runtime '$expectedRuntimeProfile' and execution '$expectedExecutionProfile'."
}
