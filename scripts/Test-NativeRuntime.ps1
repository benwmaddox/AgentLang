# Test-NativeRuntime.ps1: builds and runs the C runtime's own tests with the
# toolchain's clang. The unit tests (owning_stack_runtime_test.c) run at O0, O2
# and under trap-mode UBSan; the exhaustive HTML escape check (html_escape_test.c,
# report 310) and the substring search (string_find_test.c, report 312) run at
# O2 and under UBSan; external_values_test.c (report 313) runs with and without
# the trusted layout. Exits nonzero when any build or run fails.
[CmdletBinding()]
param([string] $OutDir = '')

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$native = Join-Path $repo 'src\AgentLang.Llvm\native'
$clang = $env:AGENTLANG_LLVM_CLANG
if (-not $clang) { $clang = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\Llvm\x64\bin\clang.exe' }
if (-not (Test-Path -LiteralPath $clang)) { throw "clang not found at $clang (set AGENTLANG_LLVM_CLANG)" }
if (-not $OutDir) { $OutDir = Join-Path $repo ('.agentlang\native-runtime-tests\' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')) }
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

$common = @('--target=x86_64-pc-windows-msvc', '-std=c11', '-Wall', '-Wextra', '-Werror', "-I$native")
$ubsan = @('-O1', '-fsanitize=undefined', '-fsanitize-trap=undefined')
$cases = @(
    @{ Name = 'unit-O0'; Flags = @('-O0'); Sources = @('owning_stack_runtime_test.c', 'owning_stack_runtime.c') },
    @{ Name = 'unit-O2'; Flags = @('-O2'); Sources = @('owning_stack_runtime_test.c', 'owning_stack_runtime.c') },
    @{ Name = 'unit-ubsan'; Flags = $ubsan; Sources = @('owning_stack_runtime_test.c', 'owning_stack_runtime.c') },
    # The escape test includes the runtime source to reach its static helpers.
    @{ Name = 'escape-O2'; Flags = @('-O2', '-Wno-unused-function'); Sources = @('html_escape_test.c') },
    @{ Name = 'escape-ubsan'; Flags = $ubsan + @('-Wno-unused-function'); Sources = @('html_escape_test.c') },
    # The 8-byte substring search (report 312) against the naive one.
    @{ Name = 'find-O2'; Flags = @('-O2', '-Wno-unused-function'); Sources = @('string_find_test.c') },
    @{ Name = 'find-ubsan'; Flags = $ubsan + @('-Wno-unused-function'); Sources = @('string_find_test.c') },
    # External values keep every value check when the layout is trusted (report 313):
    # built with the trusted runtime and with the layout-walking one.
    @{ Name = 'external-trusted'; Flags = @('-O2', '-Wno-unused-function', '-DAL_OWNING_TRUSTED_GENERATED=1'); Sources = @('external_values_test.c') },
    @{ Name = 'external-trusted-ubsan'; Flags = $ubsan + @('-Wno-unused-function', '-DAL_OWNING_TRUSTED_GENERATED=1'); Sources = @('external_values_test.c') },
    @{ Name = 'external-walked'; Flags = @('-O2', '-Wno-unused-function'); Sources = @('external_values_test.c') }
)
$failed = 0
foreach ($case in $cases) {
    $exe = Join-Path $OutDir "$($case.Name).exe"
    $sources = @($case.Sources | ForEach-Object { Join-Path $native $_ })
    $build = & $clang @common @($case.Flags) @sources -o $exe 2>&1
    if ($LASTEXITCODE -ne 0) {
        $failed++
        Write-Output "$($case.Name): build failed"
        $build | Select-Object -First 10 | ForEach-Object { Write-Output "  $_" }
        continue
    }
    $run = & $exe 2>&1
    $code = $LASTEXITCODE
    $last = ($run | Select-Object -Last 1) -as [string]
    if ($last.Length -gt 100) { $last = $last.Substring(0, 100) + '...' }
    if ($code -ne 0) { $failed++; Write-Output "$($case.Name): FAILED (exit $code) $last" }
    else { Write-Output "$($case.Name): ok $last" }
}
if ($failed -ne 0) { Write-Output "native runtime tests: $failed of $($cases.Count) failed"; exit 1 }
Write-Output "native runtime tests: all $($cases.Count) passed; artifacts $OutDir"
