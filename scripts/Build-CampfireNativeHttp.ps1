#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ValidationRunRoot,
    [ValidateSet('diagnostic', 'trusted-generated')]
    [string] $RuntimeProfile = 'diagnostic',
    [ValidateSet('Off', 'On')]
    [string] $StageProfile = 'Off',
    [ValidateSet('O0', 'O2', 'Both')]
    [string] $Optimization = 'Both'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if (-not [StringComparer]::Ordinal.Equals($RuntimeProfile, 'diagnostic') -and
    -not [StringComparer]::Ordinal.Equals($RuntimeProfile, 'trusted-generated')) {
    throw 'RuntimeProfile must be exactly diagnostic or trusted-generated.'
}
$stageProfileMacro = if ([StringComparer]::OrdinalIgnoreCase.Equals($StageProfile, 'On')) { 1 } else { 0 }
$stageProfileName = if ($stageProfileMacro -eq 1) { 'On' } else { 'Off' }
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputRootParent = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot '.agentlang\campfire-native-profile-256\http-host'))
$testRunsRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot '.agentlang\campfire-native-room-read-252\test-runs'))
$HostSourcePath = Join-Path $repositoryRoot 'applications\campfire\Native\http\native_http_host.c'
$HelperSourceRoot = Join-Path $repositoryRoot 'applications\campfire\Native\http'
$OutputRoot = Join-Path $outputRootParent ('build-' + $RuntimeProfile + '-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N'))
if (-not (Test-Path -LiteralPath $HostSourcePath -PathType Leaf)) { throw "HTTP host source is missing: $HostSourcePath" }
if (-not (Test-Path -LiteralPath $HelperSourceRoot -PathType Container)) { throw "HTTP helper source root is missing: $HelperSourceRoot" }
if (-not (Test-Path -LiteralPath $outputRootParent -PathType Container)) {
    $null = New-Item -ItemType Directory -Path $outputRootParent
}

function Get-Hash([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required file is missing: $Path" }
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToUpperInvariant()
}
function Assert-Hash([object] $Value, [string] $Label) {
    if ($Value -isnot [string] -or $Value -cnotmatch '^[0-9a-fA-F]{64}$') { throw "$Label is not a SHA-256 digest." }
    return $Value.ToUpperInvariant()
}
function Convert-HashToCBytes([string] $Hash) {
    $bytes = [System.Collections.Generic.List[string]]::new()
    for ($index = 0; $index -lt 64; $index += 2) {
        $bytes.Add('0x' + $Hash.Substring($index, 2).ToLowerInvariant())
    }
    return $bytes -join ','
}
function Get-MapKeys([object] $Map) {
    if ($Map -is [System.Collections.IDictionary]) { return [string[]]@($Map.Keys) }
    if ($null -ne $Map) { return [string[]]@($Map.PSObject.Properties.Name) }
    return [string[]]@()
}
function Sort-Ordinal([string[]] $Items) {
    $sorted = [string[]]@($Items)
    [Array]::Sort($sorted, [StringComparer]::Ordinal)
    return ,$sorted
}
function Assert-ExactStringSet([string[]] $Actual, [string[]] $Expected, [string] $Label) {
    $actualSorted = Sort-Ordinal $Actual
    $expectedSorted = Sort-Ordinal $Expected
    if ($actualSorted.Count -ne $expectedSorted.Count) { throw "$Label has an unexpected item count." }
    for ($index = 0; $index -lt $expectedSorted.Count; $index++) {
        if (-not [StringComparer]::Ordinal.Equals($actualSorted[$index], $expectedSorted[$index])) {
            throw "$Label differs from its expected exact set."
        }
    }
}
function Get-ExpectedRoomReadSources {
    $staticSourceFiles = @(
        'applications/campfire/Native/room_read_adapter.c',
        'applications/campfire/Native/room_read_adapter.h',
        'applications/campfire/Native/room_read_pure.c',
        'applications/campfire/Native/room_read_pure.h',
        'applications/campfire/Native/room_read_sqlite.def',
        'applications/campfire/Native/room_read_sqlite_subset.h',
        'applications/campfire/Native/campfire_room_read_host_abi.h', 'applications/campfire/Native/room-read-host.json',
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
    $allFiles = @($staticSourceFiles) + @($applicationSources)
    return [string[]]@($allFiles | Sort-Object -Unique)
}
function Get-CurrentSourceHashes([string[]] $RelativePaths) {
    $hashes = [ordered]@{}
    foreach ($relativePath in $RelativePaths) {
        $fullPath = Join-Path $repositoryRoot $relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { throw "Frozen source is missing: $relativePath" }
        $hashes[$relativePath] = Get-Hash $fullPath
    }
    return $hashes
}
function Assert-NotReparsePoint([string] $Path) {
    $item = Get-Item -LiteralPath $Path
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Receipt inputs cannot be reparse points: $Path"
    }
}
function Assert-HashMap([object] $Map, [string[]] $ExpectedPaths, [string] $Label) {
    $keys = Get-MapKeys $Map
    Assert-ExactStringSet $keys $ExpectedPaths $Label
    foreach ($relativePath in $ExpectedPaths) {
        if ($Map -is [System.Collections.IDictionary]) {
            if (-not $Map.Contains($relativePath)) { throw "$Label is missing $relativePath." }
            $value = $Map[$relativePath]
        }
        else {
            $property = $Map.PSObject.Properties[$relativePath]
            if ($null -eq $property) { throw "$Label is missing $relativePath." }
            $value = $property.Value
        }
        $null = Assert-Hash $value "$Label[$relativePath]"
    }
}
function Assert-FreshValidationRun([string] $RequestedRunRoot, [string] $ExpectedRuntimeProfile) {
    $resolvedRunRoot = [System.IO.Path]::GetFullPath($RequestedRunRoot)
    if (-not (Test-Path -LiteralPath $resolvedRunRoot -PathType Container)) { throw "Validation run directory is missing: $resolvedRunRoot" }
    $runDirectory = Get-Item -LiteralPath $resolvedRunRoot
    if (($runDirectory.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Validation run root cannot be a reparse point.' }
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals($runDirectory.Parent.FullName, $testRunsRoot)) {
        throw 'ValidationRunRoot must be one direct child of .agentlang/campfire-native-room-read-252/test-runs.'
    }

    $receiptPath = Join-Path $resolvedRunRoot 'test-receipt.json'
    $freezePath = Join-Path $resolvedRunRoot 'test-source-freeze.json'
    if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) { throw 'Validation run has no test-receipt.json.' }
    if (-not (Test-Path -LiteralPath $freezePath -PathType Leaf)) { throw 'Validation run has no test-source-freeze.json.' }
    Assert-NotReparsePoint $receiptPath
    Assert-NotReparsePoint $freezePath
    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json -AsHashtable
    $freeze = Get-Content -LiteralPath $freezePath -Raw | ConvertFrom-Json -AsHashtable
    if ($receipt.passed -isnot [bool] -or -not $receipt.passed) { throw 'Validation receipt is not a passing run.' }
    if ($receipt.runtimeProfile -isnot [string] -or
        -not [StringComparer]::Ordinal.Equals($receipt.runtimeProfile, $ExpectedRuntimeProfile)) {
        throw 'Validation receipt runtimeProfile does not exactly match the selected host profile.'
    }
    if ($freeze.stable -isnot [bool] -or -not $freeze.stable) { throw 'Validation source freeze is not stable.' }
    if (@($freeze.changedFiles).Count -ne 0) { throw 'Validation source freeze records changed files.' }

    $expectedPaths = Get-ExpectedRoomReadSources
    Assert-ExactStringSet ([string[]]@($freeze.filesBefore)) $expectedPaths 'Freeze filesBefore'
    Assert-ExactStringSet ([string[]]@($freeze.filesAfter)) $expectedPaths 'Freeze filesAfter'
    Assert-HashMap $receipt.sourceSha256 $expectedPaths 'Receipt sourceSha256'
    Assert-HashMap $freeze.before $expectedPaths 'Freeze before'
    Assert-HashMap $freeze.after $expectedPaths 'Freeze after'
    $currentHashes = Get-CurrentSourceHashes $expectedPaths
    foreach ($relativePath in $expectedPaths) {
        $receiptHash = Assert-Hash $receipt.sourceSha256[$relativePath] "Receipt sourceSha256[$relativePath]"
        $beforeHash = Assert-Hash $freeze.before[$relativePath] "Freeze before[$relativePath]"
        $afterHash = Assert-Hash $freeze.after[$relativePath] "Freeze after[$relativePath]"
        if ($receiptHash -ne $beforeHash -or $receiptHash -ne $afterHash -or $receiptHash -ne $currentHashes[$relativePath]) {
            throw "Frozen source no longer matches the passing validation run: $relativePath"
        }
    }

    $artifacts = $receipt.artifactsSha256
    foreach ($artifactKey in @('O2/dll', 'O2/manifest', 'O2/sqlite')) {
        if ($artifacts -isnot [System.Collections.IDictionary] -or -not $artifacts.Contains($artifactKey)) {
            throw "Validation receipt is missing required artifact hash $artifactKey."
        }
    }
    $artifactDirectory = Join-Path $resolvedRunRoot 'test-artifacts/O2'
    foreach ($directory in @((Join-Path $resolvedRunRoot 'test-artifacts'), $artifactDirectory)) {
        if (-not (Test-Path -LiteralPath $directory -PathType Container)) { throw "Validation artifact directory is missing: $directory" }
        Assert-NotReparsePoint $directory
    }
    $modulePath = Join-Path $artifactDirectory 'owning-stack-native-host.dll'
    $manifestPath = Join-Path $artifactDirectory 'owning-stack-native-host-manifest.json'
    $sqlitePath = Join-Path $artifactDirectory 'e_sqlite3.dll'
    foreach ($artifactPath in @($modulePath, $manifestPath, $sqlitePath)) {
        if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) { throw "Validation artifact is missing: $artifactPath" }
        $artifactItem = Get-Item -LiteralPath $artifactPath
        if (($artifactItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Validation artifact cannot be a reparse point: $artifactPath" }
    }
    $expectedModule = Assert-Hash $artifacts['O2/dll'] 'O2/dll'
    $expectedManifest = Assert-Hash $artifacts['O2/manifest'] 'O2/manifest'
    $expectedSqlite = Assert-Hash $artifacts['O2/sqlite'] 'O2/sqlite'
    if ((Get-Hash $modulePath) -ne $expectedModule) { throw 'O2 module differs from the passing validation receipt.' }
    if ((Get-Hash $manifestPath) -ne $expectedManifest) { throw 'O2 manifest differs from the passing validation receipt.' }
    if ((Get-Hash $sqlitePath) -ne $expectedSqlite) { throw 'O2 SQLite DLL differs from the passing validation receipt.' }
    return [ordered]@{
        RunRoot = $resolvedRunRoot
        ReceiptPath = $receiptPath
        FreezePath = $freezePath
        ReceiptSha256 = Get-Hash $receiptPath
        FreezeSha256 = Get-Hash $freezePath
        SourcePaths = $expectedPaths
        SourceHashes = $currentHashes
        ModulePath = $modulePath
        ManifestPath = $manifestPath
        SqlitePath = $sqlitePath
        ModuleSha256 = $expectedModule
        ManifestSha256 = $expectedManifest
        SqliteSha256 = $expectedSqlite
        RuntimeProfile = $receipt.runtimeProfile
        ExecutionProfile = $receipt.executionProfile
    }
}
function Assert-ClosedRoomReadManifest([string] $ManifestPath, [string] $ExpectedRuntimeProfile) {
    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    $info = $manifest.moduleInfo
    if ($info.profileId -ne 'campfire-room-read-v1' -or
        $info.runtimeProfile -isnot [string] -or
        -not [StringComparer]::Ordinal.Equals($info.runtimeProfile, $ExpectedRuntimeProfile) -or
        $info.operationName -ne 'campfire.http.room-read' -or
        $info.exportName -ne 'agentlang_owning_execute_with_host_context' -or
        $info.inputTypes.Count -ne 1 -or $info.inputTypes[0] -ne 'CampfireHttpRequest' -or
        $info.outputTypes.Count -ne 1 -or $info.outputTypes[0] -ne 'CampfireHttpResponse' -or
        $info.outputLimitBytes -ne 16777216 -or
        $info.formatVersion -ne 2 -or $info.layoutSchemaVersion -ne 5 -or
        $info.layoutAbiVersion -ne 5 -or $info.hostAbiVersion -ne 1) {
        throw 'Manifest does not match the exact selected-profile campfire-room-read-v1 contract.'
    }
    $expectedEffects = @('db.read', 'db.write', 'random.read')
    $actualEffects = @($info.effects | ForEach-Object { [string]$_ })
    Assert-ExactStringSet $actualEffects $expectedEffects 'Manifest effects'
    $expectedOperations = @(
        'campfire.cookies.verify-session-token',
        'campfire.sessions.find-by-token',
        'campfire.sessions.refresh-before',
        'campfire.users.find-by-id',
        'campfire.users.authenticate-bot-key',
        'campfire.rooms.find-for-user',
        'campfire.membership.exists?',
        'campfire.messages.last-page-with-creators',
        'campfire.csrf.session-secret',
        'campfire.csrf.form-token',
        'campfire.csrf.boost-tokens'
    )
    $actualOperations = @($info.operations | ForEach-Object { [string]$_.operationName })
    Assert-ExactStringSet $actualOperations $expectedOperations 'Manifest operations'
    return [string]$info.runtimeProfile
}
function Find-UniqueHelper([string] $Name) {
    $matches = @(Get-ChildItem -LiteralPath $HelperSourceRoot -Filter $Name -File -Recurse)
    if ($matches.Count -ne 1) { throw "Expected exactly one helper source named $Name under $HelperSourceRoot; found $($matches.Count)." }
    return $matches[0].FullName
}

$helperPins = [ordered]@{
    'request_encoder.c' = '354AC77A792FB2415B3B1E8E6CBBE30B9AE4B3D7B26917F08A56228CEDC91BAD'
    'request_encoder.h' = 'B832F3FBFF28CB69C3CF482925C2DD1ECE15A6449089BD15EF1C97F0DFCA148F'
    'campfire_http_renderer.c' = 'EE596DE77C16F09E6335B57E72CC6B02768CBF3F1B53C179C50E2256409EE6E8'
    'campfire_http_renderer.h' = 'B90B258883BEABCC69F9FBF35ECE8572778741F9E47BF22F62127EE121FD5BF3'
    'cookie_response_codec.c' = 'DF6F6AA20DB915DE028F76DB8D7EB51553FB4BB1C23D73EE24BEFF41296E6CBD'
    'cookie_response_codec.h' = 'A59B947F18A76EC25850D7DE0D3B4FF912B05C5B5483B66BC4591EB7EA79E7BA'
    'cookie_wire.c' = '121E01E1D4CC6335CDB37AB996DA7078E3C0460A47AFD89556B34A92B5D4D410'
    'cookie_wire.h' = '14C011052333923391BE1FCF5BCEF311A7A46B5716DC729B04992FBD146FA5D3'
    'cookie_unescape.c' = '599FC1C0EEDF57B26AE0761B53E5606831D42A957BEAF12DEB6FDDEEFB72F74C'
    'cookie_unescape.h' = 'AF490B7D4E3134E69F9762A2531453E09C011BBCA7BD9FBFBBFBCA8D04D40B99'
    'cookie_expiry.c' = '137A33D4E27F26135A85455C93DA9002F66DE3385D7332A9557A329F0EB0216C'
    'cookie_expiry.h' = '11BA70DDE36397EE1DF1187C08A0FC97048C7A6C96CC8D3C1C71860F7F4E68A1'
}
$helperPaths = [ordered]@{}
foreach ($helperName in $helperPins.Keys) {
    $helperPath = Find-UniqueHelper $helperName
    if ((Get-Hash $helperPath) -ne $helperPins[$helperName]) { throw "Pinned helper source changed: $helperName" }
    $helperPaths[$helperName] = $helperPath
}

$validation = Assert-FreshValidationRun $ValidationRunRoot $RuntimeProfile
$modulePath = $validation.ModulePath
$manifestPath = $validation.ManifestPath
$sqlitePath = $validation.SqlitePath
$expectedModule = $validation.ModuleSha256
$expectedManifest = $validation.ManifestSha256
$expectedSqlite = $validation.SqliteSha256
$provenanceMode = 'fresh-validation-receipt'
if ((Get-Hash $modulePath) -ne $expectedModule) { throw 'Native module hash does not match the selected artifact source.' }
if ((Get-Hash $manifestPath) -ne $expectedManifest) { throw 'Manifest hash does not match the selected artifact source.' }
if ((Get-Hash $sqlitePath) -ne $expectedSqlite) { throw 'SQLite DLL hash does not match the selected artifact source.' }
$manifestRuntimeProfile = Assert-ClosedRoomReadManifest $manifestPath $validation.RuntimeProfile
$manifestInfo = (Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json).moduleInfo
$executionProfileName = [string]$manifestInfo.executionProfile
if ($executionProfileName -cnotin @('off', 'detailed') -or $executionProfileName -cne $validation.ExecutionProfile) {
    throw 'Execution instrumentation must match the fresh passing gate receipt.'
}
$executionProfileMacro = if ($executionProfileName -ceq 'detailed') { 1 } else { 0 }
if ($executionProfileMacro -eq 1 -and $stageProfileMacro -ne 1) {
    throw 'Detailed execution instrumentation requires the enclosing stage profile.'
}
$runtimeProfileMacro = if ([StringComparer]::Ordinal.Equals($manifestRuntimeProfile, 'trusted-generated')) { 1 } else { 0 }

$toolRoot = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\Llvm\x64\bin'
$clang = Join-Path $toolRoot 'clang.exe'
$lldLink = Join-Path $toolRoot 'lld-link.exe'
$llvmReadObj = Join-Path $toolRoot 'llvm-readobj.exe'
$vsMsvcRoot = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC'
if (-not (Test-Path -LiteralPath $vsMsvcRoot -PathType Container)) { throw 'MSVC x64 libraries are unavailable.' }
$msvc = Get-ChildItem -Directory $vsMsvcRoot | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if ($null -eq $msvc) { throw 'MSVC x64 libraries are unavailable.' }
$sdkRoot = 'C:\Program Files (x86)\Windows Kits\10\Lib'
if (-not (Test-Path -LiteralPath $sdkRoot -PathType Container)) { throw 'Windows SDK libraries are unavailable.' }
$sdk = Get-ChildItem -Directory $sdkRoot | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if ($null -eq $sdk) { throw 'Windows SDK libraries are unavailable.' }
$msvcInclude = Join-Path $msvc.FullName 'include'
$msvcLib = Join-Path $msvc.FullName 'lib\x64'
$sdkInclude = Join-Path (Split-Path -Parent $sdkRoot) (Join-Path 'Include' $sdk.Name)
$sdkUcrtInclude = Join-Path $sdkInclude 'ucrt'
$sdkSharedInclude = Join-Path $sdkInclude 'shared'
$sdkUmInclude = Join-Path $sdkInclude 'um'
$sdkWinrtInclude = Join-Path $sdkInclude 'winrt'
$sdkUmLib = Join-Path $sdk.FullName 'um\x64'
foreach ($tool in @($clang, $lldLink, $llvmReadObj)) { if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { throw "Required LLVM tool missing: $tool" } }
foreach ($include in @($msvcInclude, $sdkUcrtInclude, $sdkSharedInclude, $sdkUmInclude, $sdkWinrtInclude)) {
    if (-not (Test-Path -LiteralPath $include -PathType Container)) { throw "Required include directory missing: $include" }
}

$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
if (-not [StringComparer]::OrdinalIgnoreCase.Equals([System.IO.Path]::GetDirectoryName($OutputRoot), $outputRootParent)) {
    throw 'OutputRoot must be a direct child of the private HTTP host output directory.'
}
if (Test-Path -LiteralPath $OutputRoot) { throw 'OutputRoot already exists; build outputs are never overwritten.' }
$null = New-Item -ItemType Directory -Path $OutputRoot

$pinsHeader = Join-Path $OutputRoot 'http_host_pins.h'
$headerText = @(
    '#ifndef CAMPFIRE_HTTP_HOST_PINS_H',
    '#define CAMPFIRE_HTTP_HOST_PINS_H',
    ('#define AL_OWNING_TRUSTED_GENERATED ' + $runtimeProfileMacro),
    ('#define CAMPFIRE_HTTP_OWNING_RUNTIME_PROFILE_ID ' + $runtimeProfileMacro),
    ('#define CAMPFIRE_HTTP_OWNING_RUNTIME_PROFILE_NAME "' + $manifestRuntimeProfile + '"'),
    ('#define CAMPFIRE_HTTP_STAGE_PROFILE ' + $stageProfileMacro),
    ('#define CAMPFIRE_HTTP_EXECUTION_PROFILE ' + $executionProfileMacro),
    'static const uint8_t g_expected_module_sha256[32] = {' + (Convert-HashToCBytes $expectedModule) + '};',
    'static const uint8_t g_expected_manifest_sha256[32] = {' + (Convert-HashToCBytes $expectedManifest) + '};',
    'static const uint8_t g_expected_sqlite_sha256[32] = {' + (Convert-HashToCBytes $expectedSqlite) + '};',
    '#endif'
) -join [Environment]::NewLine
Set-Content -LiteralPath $pinsHeader -Value $headerText -Encoding utf8NoBOM
$pinsHeaderHash = Get-Hash $pinsHeader

$sourcePins = [ordered]@{}
foreach ($relativePath in $validation.SourcePaths) { $sourcePins[$relativePath] = $validation.SourceHashes[$relativePath] }
foreach ($helperName in $helperPaths.Keys) {
    $helperPath = $helperPaths[$helperName]
    $relativePath = [System.IO.Path]::GetRelativePath($repositoryRoot, $helperPath).Replace('\', '/')
    $sourcePins[$relativePath] = Get-Hash $helperPath
}
$hostHash = Get-Hash $HostSourcePath
$expectedHostHash = '88A09A18731CFFEF80561DA40FAB9A39C24669104EC1B181D22A96C8B1BB9E1F'  # report 271: the session CSRF secret in, a fresh one out
if ($hostHash -ne $expectedHostHash) {
    throw 'HTTP host source differs from the reviewed native host revision.'
}
$hostPinPath = [System.IO.Path]::GetRelativePath($repositoryRoot, $HostSourcePath).Replace('\', '/')
$sourcePins[$hostPinPath] = $hostHash
$builderPath = $PSCommandPath
$builderHash = Get-Hash $builderPath
$sourcePins['scripts/Build-CampfireNativeHttp.ps1'] = $builderHash
$sourcePins['generated/http_host_pins.h'] = $pinsHeaderHash

$llvmNative = Join-Path $repositoryRoot 'src\AgentLang.Llvm\native'
$campfireNative = Join-Path $repositoryRoot 'applications\campfire\Native'
$runtimeSource = Join-Path $llvmNative 'owning_stack_runtime.c'
$helperSources = @(
    $helperPaths['request_encoder.c'],
    $helperPaths['campfire_http_renderer.c'],
    $helperPaths['cookie_response_codec.c'],
    $helperPaths['cookie_wire.c'],
    $helperPaths['cookie_unescape.c'],
    $helperPaths['cookie_expiry.c']
)
$helperDirs = @($helperPaths.Values | ForEach-Object { Split-Path -Parent $_ } | Sort-Object -Unique)
$includeDirs = @(
    $OutputRoot,
    $llvmNative,
    $campfireNative,
    (Split-Path -Parent $HostSourcePath)
) + $helperDirs + @($msvcInclude, $sdkUcrtInclude, $sdkSharedInclude, $sdkUmInclude, $sdkWinrtInclude)
$includeDirs = @($includeDirs | Sort-Object -Unique)
$sources = @($HostSourcePath, $runtimeSource) + $helperSources
$sdkLibs = @('httpapi.lib', 'kernel32.lib', 'shell32.lib', 'bcrypt.lib', 'ws2_32.lib') | ForEach-Object { Join-Path $sdkUmLib $_ }
$vcruntime = Join-Path $msvcLib 'libvcruntime.lib'
$libcmt = Join-Path $msvcLib 'libcmt.lib'
foreach ($lib in @($sdkLibs) + @($vcruntime, $libcmt)) { if (-not (Test-Path -LiteralPath $lib -PathType Leaf)) { throw "Required import/runtime library missing: $lib" } }

$pinJson = Join-Path $OutputRoot 'source-pins.json'
$sourcePins | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $pinJson -Encoding utf8NoBOM
$builds = if ($Optimization -eq 'Both') { @('O0', 'O2') } else { @($Optimization) }
foreach ($opt in $builds) {
    $outDir = Join-Path $OutputRoot $opt
    $null = New-Item -ItemType Directory -Path $outDir
    $objects = [System.Collections.Generic.List[string]]::new()
    foreach ($source in $sources) {
        $baseName = [System.IO.Path]::GetFileNameWithoutExtension($source)
        $object = Join-Path $outDir ($baseName + '.obj')
        $compileArgs = @('--target=x86_64-pc-windows-msvc', '-std=c11', '-Wall', '-Wextra', '-Werror', '-Wno-gnu-folding-constant', '-ffreestanding', '-fno-builtin', '-fstack-usage', '-include', 'stddef.h')
        $compileArgs += if ($opt -eq 'O0') { '-O0' } else { '-O2' }
        if ([StringComparer]::OrdinalIgnoreCase.Equals([System.IO.Path]::GetFullPath($source), [System.IO.Path]::GetFullPath($runtimeSource))) {
            $compileArgs += '-DAL_OWNING_TRUSTED_GENERATED=' + $runtimeProfileMacro
        }
        foreach ($include in $includeDirs) { $compileArgs += @('-I', $include) }
        $compileArgs += @('-c', $source, '-o', $object)
        $compileOutput = & $clang @compileArgs 2>&1
        if ($LASTEXITCODE -ne 0) { throw ('clang ' + $opt + ' failed for ' + $baseName + ': ' + ($compileOutput -join [Environment]::NewLine)) }
        $objects.Add($object)
    }
    $executable = Join-Path $outDir 'native_http_host.exe'
    $linkArgs = @('/entry:native_http_host_start', '/subsystem:console', '/nodefaultlib', '/machine:x64', "/out:$executable")
    $linkArgs += $objects.ToArray()
    $linkArgs += $sdkLibs
    $linkArgs += @($vcruntime, $libcmt)
    $linkOutput = & $lldLink @linkArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "lld-link $opt failed: $($linkOutput -join [Environment]::NewLine)" }
    $importOutput = & $llvmReadObj --coff-imports $executable 2>&1
    if ($LASTEXITCODE -ne 0) { throw "llvm-readobj $opt import audit failed: $($importOutput -join [Environment]::NewLine)" }
    $importText = $importOutput -join [Environment]::NewLine
    $hasQpcCounter = $importText -match '(?im)^\s*Symbol:\s*QueryPerformanceCounter(?:\s|$)'
    $hasQpcFrequency = $importText -match '(?im)^\s*Symbol:\s*QueryPerformanceFrequency(?:\s|$)'
    $hasThreadTimes = $importText -match '(?im)^\s*Symbol:\s*GetThreadTimes(?:\s|$)'
    if ($stageProfileMacro -eq 0 -and ($hasQpcCounter -or $hasQpcFrequency -or $hasThreadTimes)) {
        throw "$opt off-profile executable imports stage profiling APIs."
    }
    if ($stageProfileMacro -eq 1 -and
        (-not $hasQpcCounter -or -not $hasQpcFrequency -or -not $hasThreadTimes)) {
        throw "$opt on-profile executable is missing a required stage profiling API."
    }
    if ($importText -match '(?i)(mscoree|hostfxr|coreclr|e_sqlite3)\.dll') {
        throw "The $opt executable imports a forbidden CLR or static SQLite module."
    }
    $importNames = @([regex]::Matches($importText, '(?im)^\s*Name:\s*(\S+\.dll)\s*$') | ForEach-Object { $_.Groups[1].Value.ToUpperInvariant() } | Sort-Object -Unique)
    $requiredImports = @('HTTPAPI.DLL', 'KERNEL32.DLL', 'SHELL32.DLL', 'BCRYPT.DLL', 'WS2_32.DLL')
    foreach ($requiredImport in $requiredImports) {
        if ($importNames -notcontains $requiredImport) { throw "$opt executable is missing required import $requiredImport." }
    }
    $importPath = Join-Path $outDir 'native-imports.txt'
    Set-Content -LiteralPath $importPath -Value $importText -Encoding utf8NoBOM
    Copy-Item -LiteralPath $modulePath -Destination (Join-Path $outDir 'owning-stack-native-host.dll')
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $outDir 'owning-stack-native-host-manifest.json')
    Copy-Item -LiteralPath $sqlitePath -Destination (Join-Path $outDir 'e_sqlite3.dll')
    if ((Get-Hash (Join-Path $outDir 'owning-stack-native-host.dll')) -ne $expectedModule -or
        (Get-Hash (Join-Path $outDir 'owning-stack-native-host-manifest.json')) -ne $expectedManifest -or
        (Get-Hash (Join-Path $outDir 'e_sqlite3.dll')) -ne $expectedSqlite) {
        throw "$opt copied native artifacts changed from their receipt-pinned hashes."
    }
    $build = [ordered]@{
        Optimization = $opt
        RuntimeProfile = $manifestRuntimeProfile
        StageProfile = $stageProfileName
        ExecutionProfile = $executionProfileName
        ExecutionProfileGeneratedPin = 'CAMPFIRE_HTTP_EXECUTION_PROFILE=' + $executionProfileMacro
        ModuleInstrumentationCompileDefine = '-DAL_OWNING_DETAILED_PROFILE=' + $executionProfileMacro
        HostLocalRuntimeInstrumentation = 'off'
        OwningRuntimeCompileDefine = '-DAL_OWNING_TRUSTED_GENERATED=' + $runtimeProfileMacro
        StageProfileGeneratedPin = 'CAMPFIRE_HTTP_STAGE_PROFILE=' + $stageProfileMacro
        ProvenanceMode = $provenanceMode
        ValidationRunRoot = $validation.RunRoot
        ValidationReceiptSha256 = $validation.ReceiptSha256
        ValidationSourceFreezeSha256 = $validation.FreezeSha256
        HostSourceSha256 = $hostHash
        GeneratedPinsHeaderSha256 = $pinsHeaderHash
        Imports = $importNames
        HostExeSha256 = Get-Hash $executable
        ModuleSha256 = Get-Hash (Join-Path $outDir 'owning-stack-native-host.dll')
        ManifestSha256 = Get-Hash (Join-Path $outDir 'owning-stack-native-host-manifest.json')
        SqliteSha256 = Get-Hash (Join-Path $outDir 'e_sqlite3.dll')
        ModuleOutputLimitBytes = 16777216
        HostRetainedCapacityBytes = 4194304
        Compiler = $clang
        Linker = $lldLink
        SdkVersion = $sdk.Name
        MsvcVersion = $msvc.Name
        Libraries = @($sdkLibs + @($vcruntime, $libcmt))
    }
    $build | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outDir 'build-receipt.json') -Encoding utf8NoBOM
    Write-Output "built=$executable"
}

 $afterHashes = Get-CurrentSourceHashes $validation.SourcePaths
foreach ($relativePath in $validation.SourcePaths) {
    if ($afterHashes[$relativePath] -ne $validation.SourceHashes[$relativePath]) {
        throw "A verifier-frozen source changed during the native host build: $relativePath"
    }
}
if ((Get-Hash $HostSourcePath) -ne $hostHash -or (Get-Hash $builderPath) -ne $builderHash) {
    throw 'A host or build script source changed during the native host build.'
}
foreach ($helperName in $helperPaths.Keys) {
    if ((Get-Hash $helperPaths[$helperName]) -ne $helperPins[$helperName]) {
        throw "A pinned helper source changed during the native host build: $helperName"
    }
}
Write-Output "output_root=$OutputRoot"
Write-Output "source_pins=$pinJson"
