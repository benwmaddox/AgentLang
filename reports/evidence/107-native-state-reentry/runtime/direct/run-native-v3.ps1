$ErrorActionPreference = 'Stop'
$bin = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\Llvm\x64\bin'
$native = 'D:\code\AgentLang\src\AgentLang.Llvm\native'
$evidence = Join-Path $env:TEMP 'agentlang-native-reentry-evidence'
$cases = @(
  @{ Name = 'O0'; Opt = '-O0'; Suffix = 'O0'; Sanitizer = @() },
  @{ Name = 'O2'; Opt = '-O2'; Suffix = 'O2'; Sanitizer = @() },
  @{ Name = 'UBSan-trap'; Opt = '-O1'; Suffix = 'ubsan_O1'; Sanitizer = @('-fsanitize=undefined', '-fsanitize-trap=undefined') }
)
foreach ($case in $cases) {
  $obj = Join-Path $env:TEMP "agentlang_arena_v3_$($case.Suffix).obj"
  $exe = Join-Path $env:TEMP "agentlang_native_test_v3_$($case.Suffix).exe"
  & "$bin\clang.exe" -std=c11 -ffreestanding -fno-builtin -Wall -Wextra -Werror $case.Opt @($case.Sanitizer) -I $native -c "$native\arena_runtime.c" -o $obj
  if ($LASTEXITCODE -ne 0) { throw "runtime compile failed: $($case.Name)" }
  & "$bin\clang.exe" -std=c11 -Wall -Wextra -Werror $case.Opt @($case.Sanitizer) -I $native "$native\runtime_test.c" $obj -o $exe
  if ($LASTEXITCODE -ne 0) { throw "harness link failed: $($case.Name)" }
  & $exe | Tee-Object -FilePath (Join-Path $evidence "runtime-test-$($case.Name).log")
  if ($LASTEXITCODE -ne 0) { throw "harness failed: $($case.Name)" }
}
& "$bin\clang-format.exe" --dry-run --Werror "$native\arena_runtime.h" "$native\arena_runtime.c" "$native\runtime_test.c"
if ($LASTEXITCODE -ne 0) { throw 'clang-format check failed' }
& "$env:TEMP\agentlang_native_test_v3_O0.exe" --layout-json | Set-Content -LiteralPath (Join-Path $evidence 'c-layout.json') -NoNewline
if ($LASTEXITCODE -ne 0) { throw 'C layout query failed' }
& "$bin\llvm-nm.exe" -u "$env:TEMP\agentlang_arena_v3_O0.obj" "$env:TEMP\agentlang_arena_v3_O2.obj" "$env:TEMP\agentlang_arena_v3_ubsan_O1.obj" | Set-Content -LiteralPath (Join-Path $evidence 'undefined-symbols.txt')
if ($LASTEXITCODE -ne 0) { throw 'undefined symbol audit failed' }
