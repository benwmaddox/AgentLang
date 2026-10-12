#requires -Version 7.0
[CmdletBinding()]
param([switch]$NoRestore)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$proof = Join-Path $repo ('.agentlang/campfire-native-sqlite-249/verification-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $proof | Out-Null
$compilerTemp = Join-Path $proof 'compiler-temp'
New-Item -ItemType Directory -Path $compilerTemp | Out-Null
$previousTemp = $env:TEMP
$previousTmp = $env:TMP
$checks = [Collections.Generic.List[object]]::new()
$failed = $false
$entryRoot = $null
$sourcePaths = @(
    'src/AgentLang.Llvm/OwningStackAot.fs', 'src/AgentLang.Llvm/LlvmToolchain.fs',
    'applications/campfire/Native/room_sqlite_adapter.c', 'applications/campfire/Native/room_sqlite_adapter.h',
    'applications/campfire/Native/sqlite_subset.h', 'applications/campfire/Native/e_sqlite3.def',
    'tests/AgentLang.Aot.EntryExport.Tests/NativeSqliteTests.fs', 'tests/AgentLang.Aot.EntryExport.Tests/NativeHostTests.fs',
    'tests/AgentLang.Aot.EntryExport.Tests/Program.fs', 'tests/AgentLang.Aot.EntryExport.Tests/native/room_sqlite_smoke.c',
    'scripts/Verify-CampfireNativeSqlite.ps1'
)
$before = @{}
foreach ($path in $sourcePaths) { $before[$path] = (Get-FileHash (Join-Path $repo $path) -Algorithm SHA256).Hash.ToLowerInvariant() }

function Invoke-Check([string]$name, [string]$executable, [string[]]$arguments, [string]$workingDirectory = $repo) {
    $start = [Diagnostics.ProcessStartInfo]::new($executable)
    $start.WorkingDirectory = $workingDirectory
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    $child = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $child.StandardOutput.ReadToEndAsync()
        $stderr = $child.StandardError.ReadToEndAsync()
        if (-not $child.WaitForExit(600000)) { $child.Kill($true); throw "$name timed out" }
        $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $proof "$name.log"), $output)
        $checks.Add(@{name=$name; exitCode=$child.ExitCode; output=$output})
        if ($child.ExitCode -ne 0) { throw "$name failed ($($child.ExitCode)): $output" }
        return $output
    } finally { $child.Dispose() }
}

Push-Location $repo
try {
    $env:TEMP = $compilerTemp
    $env:TMP = $compilerTemp
    $project = 'tests/AgentLang.Aot.EntryExport.Tests/AgentLang.Aot.EntryExport.Tests.fsproj'
    $buildArguments = @('build',$project,'--configuration','Release','-m:1','-nr:false','-p:MSBuildEnableWorkloadResolver=false','-p:NuGetAudit=true')
    if ($NoRestore) { $buildArguments += '--no-restore' }
    Invoke-Check 'build' 'dotnet' $buildArguments | Out-Null
    $testOutput = Invoke-Check 'entry-tests' 'dotnet' @('run','--project',$project,'--configuration','Release','--no-build','--no-restore')
    $match = [regex]::Match($testOutput, 'passed=true; evidence=(?<path>[^\r\n]+)')
    if (-not $match.Success) { throw 'Passing entry-test receipt path missing.' }
    $entryRoot = $match.Groups['path'].Value
    if (-not $entryRoot.StartsWith($repo + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Entry-test proof escaped checkout.' }
    $entry = Get-Content (Join-Path $entryRoot 'receipt.json') -Raw | ConvertFrom-Json
    if (-not $entry.passed) { throw 'Entry-test receipt is not passing.' }
    $testAssembly = Join-Path $repo 'tests/AgentLang.Aot.EntryExport.Tests/bin/Release/net9.0/AgentLang.Aot.EntryExport.Tests.dll'
    $databasePath = Join-Path $entryRoot 'native-sqlite/campfire-東京.sqlite3'
    $clang = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\Llvm\x64\bin\clang.exe'
    if ($env:AGENTLANG_LLVM_CLANG) { $clang = $env:AGENTLANG_LLVM_CLANG }
    foreach ($optimization in @('O0','O2')) {
        $moduleDirectory = Join-Path $entryRoot "native-sqlite/$optimization"
        $executable = Join-Path $moduleDirectory 'room-sqlite-smoke.exe'
        Invoke-Check "smoke-build-$optimization" $clang @(
            '--target=x86_64-pc-windows-msvc','-fuse-ld=lld','-fms-runtime-lib=static','-std=c11','-Wall','-Wextra','-Werror',"-$optimization",
            'tests/AgentLang.Aot.EntryExport.Tests/native/room_sqlite_smoke.c',
            '-I','applications/campfire/Native','-I','src/AgentLang.Llvm/native',
            (Join-Path $moduleDirectory 'owning-stack-native-host.lib'), (Join-Path $entryRoot 'dependency-build/e_sqlite3.lib'),
            '-o',$executable
        ) | Out-Null
        Invoke-Check "smoke-run-$optimization" $executable @((Join-Path $moduleDirectory 'standalone-smoke.sqlite3')) | Out-Null
        $checks.Add(@{name="smoke-artifact-$optimization"; sha256=(Get-FileHash $executable -Algorithm SHA256).Hash.ToLowerInvariant()})
        $priorNativeHost = Join-Path $moduleDirectory 'owning-stack-native-host.dll'
        Invoke-Check "fresh-loader-probe-$optimization" 'dotnet' @(
            $testAssembly, '--native-loader-probe', $optimization, $priorNativeHost, $databasePath
        ) $compilerTemp | Out-Null
    }
    foreach ($path in $sourcePaths) {
        if ((Get-FileHash (Join-Path $repo $path) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $before[$path]) { throw "Source changed during validation: $path" }
    }
} catch {
    $failed = $true
    $failure = $_.Exception.ToString()
    Write-Host $failure
} finally {
    $receipt = @{formatVersion=1; passed=-not $failed; entryRoot=$entryRoot; sourceSha256=$before; checks=@($checks.ToArray()); failure=$failure}
    $receipt | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $proof 'receipt.json')
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
    Pop-Location
}
Write-Host "Native SQLite receipt: $proof/receipt.json"
if ($failed) { exit 1 }
