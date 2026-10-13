#requires -Version 7.0
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$nativeDirectory = Join-Path $repo 'src/AgentLang.Llvm/native'
$testSource = Join-Path $repo 'tests/native/owning_stack_runtime_ranges_test.c'
$nativeRegressionTest = Join-Path $nativeDirectory 'owning_stack_runtime_test.c'
$runtimeSource = Join-Path $nativeDirectory 'owning_stack_runtime.c'
$runDirectory = Join-Path $repo ".agentlang/owning-stack-runtime-ranges/$([Guid]::NewGuid().ToString('N'))"
$scriptPath = $PSCommandPath
$sourcePaths = @(
    $testSource,
    $nativeRegressionTest,
    $scriptPath,
    $runtimeSource,
    (Join-Path $nativeDirectory 'owning_stack_runtime.h'),
    (Join-Path $nativeDirectory 'float_format.h')
)

function Get-SourceHashes([string[]]$Paths, [string]$Root) {
    foreach ($path in $Paths) {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($Root, $path).Replace('\', '/')
            sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
}

$sourceHashesBefore = @(Get-SourceHashes $sourcePaths $repo)
$clang = $env:AGENTLANG_LLVM_CLANG
if ([string]::IsNullOrWhiteSpace($clang)) {
    $clang = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\Llvm\x64\bin\clang.exe'
}
if (-not (Test-Path -LiteralPath $clang -PathType Leaf)) {
    $command = Get-Command clang -ErrorAction SilentlyContinue
    if ($null -eq $command) { throw "Clang was not found at '$clang' or on PATH." }
    $clang = $command.Source
}

$originalTemp = $env:TEMP
$originalTmp = $env:TMP
$originalTmpDir = $env:TMPDIR
$receiptJson = $null
try {
    New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
    $env:TEMP = $runDirectory
    $env:TMP = $runDirectory
    $env:TMPDIR = $runDirectory
    $results = [Collections.Generic.List[object]]::new()
    $trustedResults = [Collections.Generic.List[object]]::new()
    $nativeRegressionResults = [Collections.Generic.List[object]]::new()
    foreach ($optimization in @('O0', 'O2')) {
        $executable = Join-Path $runDirectory "owning-stack-ranges-$optimization.exe"
        $buildLog = Join-Path $runDirectory "build-$optimization.log"
        $runLog = Join-Path $runDirectory "run-$optimization.log"
        $arguments = @(
            '--target=x86_64-pc-windows-msvc',
            '-std=c11',
            '-Wall',
            '-Wextra',
            '-Werror',
            "-$optimization",
            "-I$nativeDirectory",
            $testSource,
            '-o',
            $executable
        )
        $buildOutput = & $clang @arguments 2>&1 | Out-String
        $buildExitCode = $LASTEXITCODE
        Set-Content -LiteralPath $buildLog -Value $buildOutput -NoNewline
        if ($buildExitCode -ne 0) {
            throw "Clang $optimization build failed with exit code $buildExitCode. See $buildLog`n$buildOutput"
        }

        $runOutput = & $executable 2>&1 | Out-String
        $runExitCode = $LASTEXITCODE
        Set-Content -LiteralPath $runLog -Value $runOutput -NoNewline
        if ($runExitCode -ne 0) {
            throw "Owning-stack range differential $optimization run failed with exit code $runExitCode. See $runLog`n$runOutput"
        }
        if ($runOutput -notmatch 'owning_stack_runtime_ranges_v1=passed mark_cases=266260 fill_cases=66576 large_mark_cases=3 large_fill_cases=3') {
            throw "Owning-stack range differential $optimization run did not emit its expected receipt. See $runLog`n$runOutput"
        }
        $results.Add([ordered]@{
            optimization = $optimization
            executableSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
            buildLog = [IO.Path]::GetRelativePath($repo, $buildLog).Replace('\', '/')
            runLog = [IO.Path]::GetRelativePath($repo, $runLog).Replace('\', '/')
            output = $runOutput.Trim()
        })

        $nativeRegressionExecutable = Join-Path $runDirectory "owning-stack-storage-$optimization.exe"
        $nativeRegressionBuildLog = Join-Path $runDirectory "storage-build-$optimization.log"
        $nativeRegressionRunLog = Join-Path $runDirectory "storage-run-$optimization.log"
        $nativeRegressionArguments = @(
            '--target=x86_64-pc-windows-msvc',
            '-std=c11',
            '-Wall',
            '-Wextra',
            '-Werror',
            "-$optimization",
            "-I$nativeDirectory",
            $nativeRegressionTest,
            $runtimeSource,
            '-o',
            $nativeRegressionExecutable
        )
        $nativeRegressionBuildOutput = & $clang @nativeRegressionArguments 2>&1 | Out-String
        $nativeRegressionBuildExitCode = $LASTEXITCODE
        Set-Content -LiteralPath $nativeRegressionBuildLog -Value $nativeRegressionBuildOutput -NoNewline
        if ($nativeRegressionBuildExitCode -ne 0) {
            throw "Existing native storage test $optimization build failed with exit code $nativeRegressionBuildExitCode. See $nativeRegressionBuildLog`n$nativeRegressionBuildOutput"
        }
        $nativeRegressionOutput = & $nativeRegressionExecutable 2>&1 | Out-String
        $nativeRegressionExitCode = $LASTEXITCODE
        Set-Content -LiteralPath $nativeRegressionRunLog -Value $nativeRegressionOutput -NoNewline
        if ($nativeRegressionExitCode -ne 0 -or
            $nativeRegressionOutput -notmatch '"suite":"owning_stack_runtime_storage_v2","status":"pass"') {
            throw "Existing native storage test $optimization run failed or omitted its pass receipt. See $nativeRegressionRunLog`n$nativeRegressionOutput"
        }
        $nativeRegressionResults.Add([ordered]@{
            optimization = $optimization
            executableSha256 = (Get-FileHash -LiteralPath $nativeRegressionExecutable -Algorithm SHA256).Hash.ToLowerInvariant()
            buildLog = [IO.Path]::GetRelativePath($repo, $nativeRegressionBuildLog).Replace('\', '/')
            runLog = [IO.Path]::GetRelativePath($repo, $nativeRegressionRunLog).Replace('\', '/')
            output = $nativeRegressionOutput.Trim()
        })

        $trustedObject = Join-Path $runDirectory "owning-stack-runtime-trusted-$optimization.obj"
        $trustedBuildLog = Join-Path $runDirectory "trusted-build-$optimization.log"
        $trustedArguments = @(
            '--target=x86_64-pc-windows-msvc',
            '-std=c11',
            '-ffreestanding',
            '-fno-builtin',
            '-Wall',
            '-Wextra',
            '-Werror',
            '-DAL_OWNING_TRUSTED_GENERATED=1',
            "-$optimization",
            '-c',
            (Join-Path $nativeDirectory 'owning_stack_runtime.c'),
            "-I$nativeDirectory",
            '-o',
            $trustedObject
        )
        $trustedOutput = & $clang @trustedArguments 2>&1 | Out-String
        $trustedExitCode = $LASTEXITCODE
        Set-Content -LiteralPath $trustedBuildLog -Value $trustedOutput -NoNewline
        if ($trustedExitCode -ne 0) {
            throw "Trusted-profile Clang $optimization build failed with exit code $trustedExitCode. See $trustedBuildLog`n$trustedOutput"
        }
        $trustedResults.Add([ordered]@{
            optimization = $optimization
            objectSha256 = (Get-FileHash -LiteralPath $trustedObject -Algorithm SHA256).Hash.ToLowerInvariant()
            buildLog = [IO.Path]::GetRelativePath($repo, $trustedBuildLog).Replace('\', '/')
        })
    }

    $sourceHashesAfter = @(Get-SourceHashes $sourcePaths $repo)
    if ((ConvertTo-Json -InputObject $sourceHashesBefore -Depth 5 -Compress) -cne
        (ConvertTo-Json -InputObject $sourceHashesAfter -Depth 5 -Compress)) {
        throw 'Runtime, test, or verifier source pins changed during the differential run.'
    }
    $receipt = [ordered]@{
        suite = 'owning_stack_runtime_ranges_v1'
        status = 'pass'
        clang = [IO.Path]::GetFullPath($clang)
        clangSha256 = (Get-FileHash -LiteralPath $clang -Algorithm SHA256).Hash.ToLowerInvariant()
        runDirectory = [IO.Path]::GetRelativePath($repo, $runDirectory).Replace('\', '/')
        sourceHashesBefore = @($sourceHashesBefore)
        sourceHashesAfter = @($sourceHashesAfter)
        runs = @($results)
        existingNativeStorageRegression = @($nativeRegressionResults)
        trustedProfileBuilds = @($trustedResults)
    }
    $receiptJson = $receipt | ConvertTo-Json -Depth 10
    Set-Content -LiteralPath (Join-Path $runDirectory 'receipt.json') -Value $receiptJson -Encoding utf8
}
finally {
    if ($null -eq $originalTemp) { Remove-Item Env:TEMP -ErrorAction SilentlyContinue }
    else { $env:TEMP = $originalTemp }
    if ($null -eq $originalTmp) { Remove-Item Env:TMP -ErrorAction SilentlyContinue }
    else { $env:TMP = $originalTmp }
    if ($null -eq $originalTmpDir) { Remove-Item Env:TMPDIR -ErrorAction SilentlyContinue }
    else { $env:TMPDIR = $originalTmpDir }
}

Write-Output $receiptJson
