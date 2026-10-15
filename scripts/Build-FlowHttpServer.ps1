<#
.SYNOPSIS
Builds a native Flow HTTP server (portable host + SQLite db API) for Windows or Linux.

.DESCRIPTION
The compiler runs on Windows: AgentLang.FlowHttp emits the app's two-entry owning
module (worker-init, handle) for the requested target. Windows output is compiled
and linked here with the VS Build Tools clang/lld against e_sqlite3.dll. Linux
output is staged and built inside WSL (clang/lld 18, system libsqlite3.so.0) under
~/linuxbench/flowhttp/<name>, never under /mnt.

.EXAMPLE
scripts/Build-FlowHttpServer.ps1 -App counter -Source tests/AgentLang.FlowHttp.Tests/app/counter.flow -Target linux-x64 -OutDir .agentlang/flowhttp/linux
#>
param(
    [Parameter(Mandatory)] [string] $App,
    [Parameter(Mandatory)] [string[]] $Source,
    [ValidateSet('windows-x64', 'linux-x64')] [string] $Target = 'windows-x64',
    [ValidateSet('trusted', 'diagnostic')] [string] $RuntimeProfile = 'trusted',
    [ValidateSet('O0', 'O2')] [string] $Optimization = 'O2',
    [Parameter(Mandatory)] [string] $OutDir,
    [string] $LinuxName = ''
)
$ErrorActionPreference = 'Stop'
$repo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$out = [System.IO.Path]::GetFullPath($OutDir)
New-Item -ItemType Directory -Force $out | Out-Null

# 1. Emit the module for the target.
$project = Join-Path $repo 'src/AgentLang.FlowHttp/AgentLang.FlowHttp.fsproj'
$buildOutput = & dotnet build $project -c Release --nologo -v q 2>&1
if ($LASTEXITCODE -ne 0) { throw "AgentLang.FlowHttp build failed: $($buildOutput -join [Environment]::NewLine)" }
$tool = Join-Path $repo 'src/AgentLang.FlowHttp/bin/Release/net9.0/AgentLang.FlowHttp.dll'
$emitArgs = @($tool, 'emit', '--app', $App, '--target', $Target, '--profile', $RuntimeProfile, '--out', $out)
foreach ($file in $Source) { $emitArgs += @('--source', [System.IO.Path]::GetFullPath($file)) }
& dotnet @emitArgs
if ($LASTEXITCODE -ne 0) { throw 'Flow module emission failed.' }

# 2. Stage the C sources next to module.ll.
$src = Join-Path $out 'src'
New-Item -ItemType Directory -Force $src | Out-Null
$native = Join-Path $repo 'src/AgentLang.Llvm/native'
foreach ($name in @('owning_stack_runtime.c', 'owning_stack_runtime.h', 'owning_stack_profile.h', 'float_format.h', 'owning_stdlib2.h')) {
    Copy-Item -LiteralPath (Join-Path $native $name) -Destination $src -Force
}
Get-ChildItem -LiteralPath (Join-Path $native 'http_host') -File | Copy-Item -Destination $src -Force
$trusted = if ($RuntimeProfile -eq 'trusted') { '1' } else { '0' }

if ($Target -eq 'windows-x64') {
    $toolRoot = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\Llvm\x64\bin'
    $clang = Join-Path $toolRoot 'clang.exe'
    $dlltool = Join-Path $toolRoot 'llvm-dlltool.exe'
    $opt = "-$Optimization"
    $defs = "-DAL_OWNING_TRUSTED_GENERATED=$trusted"
    function Invoke-Clang([string[]] $Arguments) {
        $ErrorActionPreference = 'Continue' # clang warnings on stderr are not failures
        $output = & $clang -Wno-override-module @Arguments 2>&1
        if ($LASTEXITCODE -ne 0) { throw "clang failed: $($output -join [Environment]::NewLine)" }
    }
    Push-Location $out
    try {
        Invoke-Clang @('--target=x86_64-pc-windows-msvc', $opt, '-c', '-x', 'ir', 'module.ll', '-o', 'module.obj')
        Invoke-Clang @('--target=x86_64-pc-windows-msvc', $opt, '-std=c11', '-ffreestanding', '-fno-builtin', '-include', 'stddef.h', $defs, '-Isrc', '-c', 'src/owning_stack_runtime.c', '-o', 'runtime.obj')
        Invoke-Clang @('--target=x86_64-pc-windows-msvc', $opt, '-std=c11', '-Wall', '-Wno-unused-parameter', $defs, '-Isrc', '-c', 'src/flow_http_db.c', '-o', 'db.obj')
        Invoke-Clang @('--target=x86_64-pc-windows-msvc', $opt, '-std=c11', '-Wall', '-Wno-unused-parameter', $defs, '-Isrc', '-c', 'src/flow_http_cache.c', '-o', 'cache.obj')
        Invoke-Clang @('--target=x86_64-pc-windows-msvc', $opt, '-std=c11', '-Wall', '-Wno-unused-parameter', $defs, '-Isrc', '-c', 'src/flow_http_host.c', '-o', 'host.obj')
        $dllOutput = & $dlltool -m i386:x86-64 -d 'src/flow_http_sqlite.def' -l 'e_sqlite3.lib' 2>&1
        if ($LASTEXITCODE -ne 0) { throw "llvm-dlltool failed: $dllOutput" }
        Invoke-Clang @('--target=x86_64-pc-windows-msvc', '-fuse-ld=lld', 'host.obj', 'db.obj', 'cache.obj', 'module.obj', 'runtime.obj', 'e_sqlite3.lib', '-lws2_32', '-lbcrypt', '-o', 'flow_http_server.exe')
        $sqliteDll = Join-Path $repo 'src/AgentLang.FlowHttp/bin/Release/net9.0/runtimes/win-x64/native/e_sqlite3.dll'
        Copy-Item -LiteralPath $sqliteDll -Destination $out -Force
    }
    finally { Pop-Location }
    Write-Output (Join-Path $out 'flow_http_server.exe')
}
else {
    if ($LinuxName -eq '') { $LinuxName = $App }
    $drive = $out.Substring(0, 1).ToLowerInvariant()
    $wslStage = '/mnt/' + $drive + $out.Substring(2).Replace('\', '/')
    $wslScript = '/mnt/' + $repo.Substring(0, 1).ToLowerInvariant() + $repo.Substring(2).Replace('\', '/') + '/scripts/build-flow-http-linux.sh'
    $linuxOut = "`$HOME/linuxbench/flowhttp/$LinuxName"
    $ErrorActionPreference = 'Continue' # compiler warnings on stderr are not failures
    # A per-build staging name: concurrent builds must not overwrite each other's script.
    $stagedScript = "/tmp/build-flow-http-linux-$([Guid]::NewGuid().ToString('N')).sh"
    $result = & wsl -- bash -lc "tr -d '\r' < '$wslScript' > $stagedScript && bash $stagedScript '$wslStage' $linuxOut $trusted $Optimization; rc=`$?; rm -f $stagedScript; exit `$rc" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "WSL build failed: $($result -join [Environment]::NewLine)" }
    $result | Select-Object -Last 1 | ForEach-Object { $_.Replace('built ', '') }
}
