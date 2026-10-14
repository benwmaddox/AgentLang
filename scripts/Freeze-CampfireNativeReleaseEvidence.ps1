#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $DiagnosticRoomRunRoot,
    [Parameter(Mandatory = $true)] [string] $DiagnosticNativeBuildRoot,
    [Parameter(Mandatory = $true)] [string] $TrustedGeneratedRoomRunRoot,
    [Parameter(Mandatory = $true)] [string] $TrustedGeneratedNativeBuildRoot,
    [Parameter(Mandatory = $true)] [string] $RangeRunRoot,
    [Parameter(Mandatory = $true)] [string] $CookieExpiryRunRoot,
    [Parameter(Mandatory = $true)] [string] $CookieParityRoot,
    [Parameter(Mandatory = $true)] [string[]] $MeasurementRoots,
    [string] $FullLlvmRunRoot,
    [string] $OutputRoot = 'reports/evidence/254-native-campfire-release',
    [switch] $ValidateOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$script:Repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$script:RepoPrefix = $script:Repo.TrimEnd('\') + '\'
$script:Files = [Collections.Generic.List[object]]::new()
$script:SeenArchive = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
$script:Sources = [ordered]@{}
$script:Hashes = @{}
$script:MeasurementModes = [Collections.Generic.List[object]]::new()
$script:BoundarySchemas = @{}
$script:SharedLayouts = @{}
$script:ProfileBuildPins = @{}

function Require([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

function Hash([string] $Path) {
    NoReparsePath $Path
    Require (Test-Path -LiteralPath $Path -PathType Leaf) "Missing file: $Path"
    Require (((Get-Item -LiteralPath $Path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) "Reparse-point file rejected: $Path"
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function CBytes([string] $Digest) {
    Require ($Digest -match '^[0-9a-fA-F]{64}$') 'Expected SHA-256 digest is malformed.'
    $bytes = for ($index = 0; $index -lt 64; $index += 2) { '0x' + $Digest.Substring($index, 2).ToLowerInvariant() }
    $bytes -join ','
}

function Json([string] $Path) {
    NoReparsePath $Path
    try { Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json -AsHashtable -Depth 100 -ErrorAction Stop }
    catch { throw "Invalid evidence JSON '$Path': $($_.Exception.Message)" }
}

function FullDir([string] $Path) {
    $resolved = (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path
    $item = Get-Item -LiteralPath $resolved -Force
    Require $item.PSIsContainer "Expected a directory: $Path"
    Require (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) "Reparse-point directory rejected: $Path"
    $full = [IO.Path]::GetFullPath($resolved)
    NoReparsePath $full
    $full
}

function InRepo([string] $Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $full.Equals($script:Repo, [StringComparison]::OrdinalIgnoreCase) -or $full.StartsWith($script:RepoPrefix, [StringComparison]::OrdinalIgnoreCase)
}

function NoReparsePath([string] $Path) {
    $current = [IO.Path]::GetFullPath($Path)
    Require (InRepo $current) "Path escaped repository: $Path"
    while ($true) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            Require (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) "Reparse-point path rejected: $current"
        }
        if ($current.Equals($script:Repo, [StringComparison]::OrdinalIgnoreCase)) { break }
        $current = [IO.Path]::GetDirectoryName($current)
        Require (InRepo $current) "Path escaped repository while resolving: $Path"
    }
}

function Rel([string] $Path) {
    $full = [IO.Path]::GetFullPath($Path)
    Require (InRepo $full) "Evidence must remain inside the repository: $Path"
    [IO.Path]::GetRelativePath($script:Repo, $full).Replace('\', '/')
}

function RepoFile([string] $RelativePath) {
    $relPath = $RelativePath.Replace('\', '/')
    Require ($relPath -notmatch '(^|/)\.\.(/|$)|^[A-Za-z]:|^/') "Unsafe receipt path: $RelativePath"
    $full = [IO.Path]::GetFullPath((Join-Path $script:Repo $relPath.Replace('/', [IO.Path]::DirectorySeparatorChar)))
    Require (InRepo $full) "Receipt path escaped repository: $RelativePath"
    NoReparsePath $full
    Require (Test-Path -LiteralPath $full -PathType Leaf) "Missing receipt source: $RelativePath"
    $full
}

function EqList([object[]] $Actual, [string[]] $Expected) {
    if ($Actual.Count -ne $Expected.Count) { return $false }
    for ($i = 0; $i -lt $Expected.Count; $i++) { if ([string]$Actual[$i] -cne $Expected[$i]) { return $false } }
    $true
}

function SameMap([System.Collections.IDictionary] $Left, [System.Collections.IDictionary] $Right, [string] $What) {
    $leftKeys = @($Left.Keys | Sort-Object)
    $rightKeys = @($Right.Keys | Sort-Object)
    Require (EqList $leftKeys $rightKeys) "$What path sets differ."
    foreach ($key in $leftKeys) {
        Require (([string]$Left[$key]).ToLowerInvariant() -eq ([string]$Right[$key]).ToLowerInvariant()) "$What hash changed: $key"
    }
}

function SamePinList([object[]] $Left, [object[]] $Right, [string] $What) {
    Require ($Left.Count -eq $Right.Count) "$What source counts differ."
    $leftMap = @{}
    foreach ($row in $Left) { $leftMap[[string]$row['path']] = ([string]$row['sha256']).ToLowerInvariant() }
    $rightMap = @{}
    foreach ($row in $Right) { $rightMap[[string]$row['path']] = ([string]$row['sha256']).ToLowerInvariant() }
    $keys = @($leftMap.Keys | Sort-Object)
    Require (EqList $keys @($rightMap.Keys | Sort-Object)) "$What source paths differ."
    foreach ($key in $keys) { Require ($leftMap[$key] -eq $rightMap[$key]) "$What source hash differs: $key" }
}

function AllowedSource([string] $Path) {
    $patterns = @(
        '^applications/campfire/.+\.(flow|fsproj|fs|c|h|def|md)$',
        '^src/AgentLang\.Llvm/.+\.(fsproj|fs|c|h)$',
        '^tests/(AgentLang\.Campfire\.NativeRoomRead\.Tests|AgentLang\.Campfire\.NativeHttp\.Tests|AgentLang\.Llvm\.Tests)/.+\.(fsproj|fs|fsx|c|h|ps1|md)$',
        '^tests/native/.+\.c$',
        '^scripts/(Verify-CampfireNativeRoomRead|Build-CampfireNativeHttp|Verify-OwningStackRuntimeRanges|Verify-CampfireCookieExpiry|Freeze-CampfireNativeHttpEvidence|Freeze-CampfireNativeReleaseEvidence)\.ps1$',
        '^experiments/AgentLang\.Campfire\.HttpMeasure/.+\.(csproj|cs|md)$',
        '^experiments/AgentLang\.Campfire\.HttpReference/.+\.(fsproj|fs|md)$',
        '^docs/(CAMPFIRE-AOT-PLAN|SQLITE-POLICY)\.md$',
        '^\.agentlang/campfire-native-http-253/(reference-memory-review|trusted-profile-plan)\.md$'
    )
    foreach ($pattern in $patterns) { if ($Path -cmatch $pattern) { return $true } }
    $false
}

function AddFile([string] $Path, [string] $Archive, [string] $Kind, [switch] $Text) {
    $full = [IO.Path]::GetFullPath($Path)
    Require (InRepo $full) "Archive source is outside the repository: $Path"
    NoReparsePath $full
    Require (Test-Path -LiteralPath $full -PathType Leaf) "Allowlisted evidence file is missing: $Path"
    Require (((Get-Item -LiteralPath $full -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) "Reparse-point evidence rejected: $Path"
    $archive = $Archive.Replace('\', '/').TrimStart('/')
    Require ($archive -notmatch '(^|/)\.\.(/|$)|^[A-Za-z]:') "Unsafe archive path: $Archive"
    Require ($archive -notmatch '(?i)(^|/)(?:private|secrets?|credentials?)(?:[-_./]|$)|\.config\.json$|private-(?:request|measure)|\.(?:sqlite\w*|db|wal|shm)$') "Private evidence rejected: $Archive"
    if ($script:SeenArchive.ContainsKey($archive)) {
        Require ($script:SeenArchive[$archive].Equals($full, [StringComparison]::OrdinalIgnoreCase)) "Archive path collision: $archive from $(Rel $($script:SeenArchive[$archive])) versus $(Rel $full)"
        return
    }
    if ($Text) {
        $contents = [IO.File]::ReadAllText($full, [Text.Encoding]::UTF8)
        Require (-not [regex]::IsMatch($contents, '(?im)^\s*(?:cookie|set-cookie|authorization|proxy-authorization)\s*:|(?:^|[?&;\s])(?:session_token|_campfire_session)\s*=|"(?:cookie|set-cookie|authorization|session_token|secret|password|privateRequest|privateMeasure|fixtureHeader|databasePath|sqlitePath|requestHeaders|responseHeaders)"\s*:|private-request|private-measure|synthetic-secret|native\.sqlite(?:-wal|-shm)?')) "Unredacted or private content rejected: $(Rel $full)"
    }
    $script:SeenArchive.Add($archive, $full)
    $script:Files.Add([pscustomobject]@{ Path = $full; Source = Rel $full; Archive = $archive; Kind = $Kind })
}

function PinSource([string] $RelativePath, [string] $ExpectedHash) {
    $relative = $RelativePath.Replace('\', '/')
    Require (AllowedSource $relative) "Source is outside the curated allowlist: $relative"
    $path = RepoFile $relative
    $actual = Hash $path
    Require ($actual -eq $ExpectedHash.ToLowerInvariant()) "Current source differs from pinned source: $relative"
    if ($script:Sources.Contains($relative)) {
        Require ($script:Sources[$relative] -eq $actual) "Conflicting source pin: $relative"
        return
    }
    $script:Sources[$relative] = $actual
    AddFile $path "sources/$relative" 'source'
}

function PinMap([System.Collections.IDictionary] $Map) {
    foreach ($key in @($Map.Keys | Sort-Object)) { PinSource ([string]$key) ([string]$Map[$key]) }
}

function PinList([object[]] $List) {
    foreach ($row in $List) { PinSource ([string]$row['path']) ([string]$row['sha256']) }
}

function AddReceiptLog([string] $RepoRelative, [string] $RunRoot, [string] $Prefix) {
    $path = RepoFile $RepoRelative
    Require ($path.StartsWith($RunRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) "Receipt log escapes selected run root: $RepoRelative"
    AddFile $path "$Prefix/$([IO.Path]::GetFileName($path))" 'log' -Text
}

function AddRoomRun([string] $Root, [string] $ExpectedProfile, [string] $ProfileKey) {
    $receiptPath = Join-Path $Root 'test-receipt.json'
    $freezePath = Join-Path $Root 'test-source-freeze.json'
    $receipt = Json $receiptPath
    $freeze = Json $freezePath
    Require ([bool]$receipt['passed'] -and [int]$receipt['assertions'] -eq 238) "$ProfileKey room-read receipt must pass all 238 assertions."
    Require ([string]$receipt['runtimeProfile'] -ceq $ExpectedProfile) "$ProfileKey room-read receipt has the wrong runtime profile."
    Require ([bool]$freeze['stable'] -and @($freeze['changedFiles']).Count -eq 0) "$ProfileKey room-read source freeze is not stable."
    Require ($receipt['sourceSha256'].Count -gt 0) "$ProfileKey room-read receipt has no source pins."
    SameMap $freeze['before'] $freeze['after'] "$ProfileKey room source freeze"
    SameMap $receipt['sourceSha256'] $freeze['before'] "$ProfileKey room receipt and source freeze"
    Require (EqList @($freeze['filesBefore'] | Sort-Object) @($freeze['filesAfter'] | Sort-Object)) "$ProfileKey room source file lists differ."
    Require (EqList @($freeze['filesBefore'] | Sort-Object) @($receipt['sourceSha256'].Keys | Sort-Object)) "$ProfileKey room source file list and hash map differ."
    PinMap $receipt['sourceSha256']
    AddFile $receiptPath "evidence/room-read/$ProfileKey/test-receipt.json" receipt -Text
    AddFile $freezePath "evidence/room-read/$ProfileKey/test-source-freeze.json" receipt -Text
    foreach ($name in @('test-restore.log', 'test-build.log', 'test-run.log')) {
        AddFile (Join-Path $Root $name) "evidence/room-read/$ProfileKey/$name" log -Text
    }
    foreach ($opt in @('O0', 'O2')) {
        $artifactDir = Join-Path $Root "test-artifacts/$opt"
        foreach ($artifact in @('owning-stack-native-host.dll', 'owning-stack-native-host-manifest.json', 'e_sqlite3.dll')) {
            $key = "$opt/" + $(switch ($artifact) { 'owning-stack-native-host.dll' { 'dll' } 'owning-stack-native-host-manifest.json' { 'manifest' } 'e_sqlite3.dll' { 'sqlite' } })
            $path = Join-Path $artifactDir $artifact
            Require ((Hash $path) -eq ([string]$receipt['artifactsSha256'][$key]).ToLowerInvariant()) "$ProfileKey $opt room artifact hash mismatch: $artifact"
            AddFile $path "evidence/room-read/$ProfileKey/$opt/$artifact" artifact
        }
        $smokePath = Join-Path $Root "test-smoke/$opt/room_read_standalone_smoke.exe"
        Require ((Hash $smokePath) -eq ([string]$receipt['artifactsSha256']["$opt/smoke-exe"]).ToLowerInvariant()) "$ProfileKey $opt standalone smoke hash mismatch."
        AddFile $smokePath "evidence/room-read/$ProfileKey/$opt/room_read_standalone_smoke.exe" artifact
    }
    $receipt
}

function AddBuild([string] $Root, [string] $RoomRoot, [hashtable] $Room, [string] $ExpectedProfile, [string] $ProfileKey) {
    $pinsPath = Join-Path $Root 'source-pins.json'
    $pins = Json $pinsPath
    Require ($pins.Contains('generated/http_host_pins.h')) "$ProfileKey native build source pins omit the generated pin header."
    foreach ($path in $Room['sourceSha256'].Keys) {
        Require ($pins.Contains([string]$path)) "$ProfileKey native build omitted room source pin: $path"
        Require (([string]$pins[$path]).ToLowerInvariant() -eq ([string]$Room['sourceSha256'][$path]).ToLowerInvariant()) "$ProfileKey native build/room source pin mismatch: $path"
    }
    $hostPath = 'applications/campfire/Native/http/native_http_host.c'
    $required = @(
        $hostPath,
        'applications/campfire/Native/http/request_encoder.c', 'applications/campfire/Native/http/request_encoder.h',
        'applications/campfire/Native/http/browser_normalizer.c', 'applications/campfire/Native/http/browser_normalizer.h',
        
        'applications/campfire/Native/http/campfire_http_renderer.c', 'applications/campfire/Native/http/campfire_http_renderer.h',
        'applications/campfire/Native/http/cookie_response_codec.c', 'applications/campfire/Native/http/cookie_response_codec.h',
        'applications/campfire/Native/http/cookie_wire.c', 'applications/campfire/Native/http/cookie_wire.h',
        'applications/campfire/Native/http/cookie_unescape.c', 'applications/campfire/Native/http/cookie_unescape.h',
        'applications/campfire/Native/http/cookie_expiry.c', 'applications/campfire/Native/http/cookie_expiry.h',
        'src/AgentLang.Llvm/native/owning_stack_runtime.c', 'src/AgentLang.Llvm/native/owning_stack_runtime.h',
        'src/AgentLang.Llvm/native/float_format.h', 'applications/campfire/Native/campfire_room_read_host_abi.h', 'applications/campfire/Native/room-read-host.json',
        'scripts/Build-CampfireNativeHttp.ps1'
    )
    foreach ($path in $required) { Require ($pins.Contains($path)) "$ProfileKey native build omitted required source pin: $path" }
    foreach ($path in $pins.Keys) {
        if ($path -eq 'generated/http_host_pins.h') { continue }
        PinSource ([string]$path) ([string]$pins[$path])
    }
    $header = Join-Path $Root 'http_host_pins.h'
    Require ((Hash $header) -eq ([string]$pins['generated/http_host_pins.h']).ToLowerInvariant()) "$ProfileKey generated pin header hash mismatch."
    $headerText = [IO.File]::ReadAllText($header, [Text.Encoding]::UTF8)
    $trustedDefine = if ($ExpectedProfile -ceq 'trusted-generated') { 1 } else { 0 }
    Require ([regex]::Matches($headerText, '(?m)^#define AL_OWNING_TRUSTED_GENERATED\s+' + $trustedDefine + '\s*$').Count -eq 1) "$ProfileKey generated header runtime define mismatch."
    Require ([regex]::Matches($headerText, '(?m)^#define CAMPFIRE_HTTP_OWNING_RUNTIME_PROFILE_ID\s+' + $trustedDefine + '\s*$').Count -eq 1) "$ProfileKey generated header profile id mismatch."
    Require ([regex]::Matches($headerText, '(?m)^#define CAMPFIRE_HTTP_OWNING_RUNTIME_PROFILE_NAME\s+"' + [regex]::Escape($ExpectedProfile) + '"\s*$').Count -eq 1) "$ProfileKey generated header profile name mismatch."
    $compactHeader = $headerText -replace '\s+', ''
    foreach ($pin in @(
        @{ symbol = 'g_expected_module_sha256'; digest = [string]$Room['artifactsSha256']['O2/dll'] },
        @{ symbol = 'g_expected_manifest_sha256'; digest = [string]$Room['artifactsSha256']['O2/manifest'] },
        @{ symbol = 'g_expected_sqlite_sha256'; digest = [string]$Room['artifactsSha256']['O2/sqlite'] }
    )) {
        $declaration = 'staticconstuint8_t' + $pin.symbol + '[32]={' + (CBytes $pin.digest) + '};'
        Require ($compactHeader.Contains($declaration, [StringComparison]::Ordinal)) "$ProfileKey generated header does not pin its selected $($pin.symbol) artifact."
    }
    AddFile $pinsPath "evidence/native-build/$ProfileKey/source-pins.json" receipt -Text
    AddFile $header "evidence/native-build/$ProfileKey/http_host_pins.h" artifact

    $expectedImports = @('BCRYPT.DLL', 'HTTPAPI.DLL', 'KERNEL32.DLL', 'SHELL32.DLL', 'WS2_32.DLL')
    $expectedOperations = @(
        [ordered]@{ id = 'campfire.cookies.verify-session-token'; tag = 1; input = @('String', 'UtcInstant'); output = @('Option<SessionToken>'); effects = @() },
        [ordered]@{ id = 'campfire.sessions.find-by-token'; tag = 2; input = @('SessionToken'); output = @('Result<Option<SessionRow>, DbError>'); effects = @('db.read') },
        [ordered]@{ id = 'campfire.sessions.refresh-before'; tag = 3; input = @('SessionRefresh'); output = @('Result<SessionRow, DbError>'); effects = @('db.write') },
        [ordered]@{ id = 'campfire.users.find-by-id'; tag = 4; input = @('UserId'); output = @('Result<Option<AuthUser>, DbError>'); effects = @('db.read') },
        [ordered]@{ id = 'campfire.users.authenticate-bot-key'; tag = 5; input = @('BotKey'); output = @('Result<Option<AuthUser>, DbError>'); effects = @('db.read') },
        [ordered]@{ id = 'campfire.browser.version-at-least?'; tag = 6; input = @('String', 'String'); output = @('Bool'); effects = @() },
        [ordered]@{ id = 'campfire.rooms.find-for-user'; tag = 7; input = @('UserId', 'RoomId'); output = @('Result<Option<RoomRow>, DbError>'); effects = @('db.read') },
        [ordered]@{ id = 'campfire.membership.exists?'; tag = 8; input = @('MembershipKey'); output = @('Result<Bool, DbError>'); effects = @('db.read') },
        [ordered]@{ id = 'campfire.messages.last-page'; tag = 9; input = @('RoomId'); output = @('Result<List<MessageRow>, DbError>'); effects = @('db.read') }
    )
    $receipts = @{}
    foreach ($opt in @('O0', 'O2')) {
        $dir = Join-Path $Root $opt
        $rPath = Join-Path $dir 'build-receipt.json'
        $r = Json $rPath
        $mPath = Join-Path $dir 'owning-stack-native-host-manifest.json'
        $m = Json $mPath
        $info = $m['moduleInfo']
        Require ([string]$r['Optimization'] -ceq $opt -and [string]$r['ProvenanceMode'] -ceq 'fresh-validation-receipt') "$ProfileKey $opt build receipt is not a fresh expected build."
        Require ([string]$r['RuntimeProfile'] -ceq $ExpectedProfile -and [string]$info['runtimeProfile'] -ceq $ExpectedProfile) "$ProfileKey $opt build receipt and manifest profile mismatch."
        Require ([string]$r['OwningRuntimeCompileDefine'] -ceq "-DAL_OWNING_TRUSTED_GENERATED=$trustedDefine") "$ProfileKey $opt build does not pin the runtime compile profile."
        Require (([IO.Path]::GetFullPath([string]$r['ValidationRunRoot'])).Equals($RoomRoot, [StringComparison]::OrdinalIgnoreCase)) "$ProfileKey $opt build receipt points at a different room run."
        Require (([string]$r['ValidationReceiptSha256']).ToLowerInvariant() -eq (Hash (Join-Path $RoomRoot 'test-receipt.json'))) "$ProfileKey $opt build does not pin the accepted room receipt."
        Require (([string]$r['ValidationSourceFreezeSha256']).ToLowerInvariant() -eq (Hash (Join-Path $RoomRoot 'test-source-freeze.json'))) "$ProfileKey $opt build does not pin the accepted room freeze."
        Require (([string]$r['HostSourceSha256']).ToLowerInvariant() -eq (Hash (RepoFile $hostPath))) "$ProfileKey $opt host source pin is stale."
        Require ([string]$info['profileId'] -ceq 'campfire-room-read-v1' -and [string]$info['operationId'] -ceq 'campfire.http.room-read' -and [string]$info['operationName'] -ceq 'campfire.http.room-read') "$ProfileKey $opt manifest profile identity mismatch."
        Require ([string]$info['exportName'] -ceq 'agentlang_owning_execute_with_host_context' -and [string]$info['adapterSymbol'] -ceq 'al_host_campfire_room_read_v1') "$ProfileKey $opt manifest export or adapter symbol mismatch."
        Require ([int]$info['formatVersion'] -eq 2 -and [int]$info['layoutSchemaVersion'] -eq 5 -and [int]$info['layoutAbiVersion'] -eq 5 -and [int]$info['owningRuntimeAbiVersion'] -eq 2 -and [int]$info['hostAbiVersion'] -eq 1) "$ProfileKey $opt manifest ABI versions mismatch."
        Require ([long]$info['outputLimitBytes'] -eq 16777216) "$ProfileKey $opt manifest output limit mismatch."
        Require ((EqList @($info['effects']) @('db.read', 'db.write')) -and (EqList @($info['fixedEffectAllowlist']) @('db.read', 'db.write'))) "$ProfileKey $opt manifest effects mismatch."
        Require ((EqList @($info['inputTypes']) @('CampfireHttpRequest')) -and (EqList @($info['outputTypes']) @('CampfireHttpResponse'))) "$ProfileKey $opt manifest entry types mismatch."
        Require (@($info['operations']).Count -eq $expectedOperations.Count) "$ProfileKey $opt manifest operation closure count mismatch."
        for ($index = 0; $index -lt $expectedOperations.Count; $index++) {
            $actual = $info['operations'][$index]
            $expected = $expectedOperations[$index]
            Require ([string]$actual['operationId'] -ceq $expected.id -and [string]$actual['operationName'] -ceq $expected.id -and [int]$actual['tag'] -eq $expected.tag) "$ProfileKey $opt manifest operation identity/tag mismatch at index $index."
            Require ((EqList @($actual['inputTypes'] | ForEach-Object { [string]$_ }) $expected.input) -and (EqList @($actual['outputTypes'] | ForEach-Object { [string]$_ }) $expected.output) -and (EqList @($actual['effects'] | ForEach-Object { [string]$_ }) $expected.effects)) "$ProfileKey $opt manifest operation signature/effects mismatch: $($expected.id)."
        }
        $boundaryTypes = @($info['boundarySchemas'] | ForEach-Object { [string]$_.typeName })
        Require (($boundaryTypes -contains 'CampfireHttpRequest') -and ($boundaryTypes -contains 'CampfireHttpResponse')) "$ProfileKey $opt manifest omits typed HTTP request/response boundary schemas."
        $nowField = @($info['boundarySchemas'] | Where-Object { [string]$_.typeName -ceq 'CampfireHttpRequest' } | ForEach-Object { $_.fields } | ForEach-Object { $_ } | Where-Object { [string]$_.name -ceq 'now' })
        Require ($nowField.Count -eq 1 -and [string]$nowField[0]['typeName'] -ceq 'UtcInstant' -and [int]$nowField[0]['offsetBytes'] -eq 24) "$ProfileKey $opt request schema does not pin UtcInstant at byte 24."
        $script:BoundarySchemas["$ProfileKey/$opt"] = ConvertTo-Json -InputObject $info['boundarySchemas'] -Depth 100 -Compress
        $script:SharedLayouts["$ProfileKey/$opt"] = ConvertTo-Json -InputObject $info['sharedLayout'] -Depth 100 -Compress
        Require (([string]$r['GeneratedPinsHeaderSha256']).ToLowerInvariant() -eq (Hash $header)) "$ProfileKey $opt generated pin-header hash mismatch."
        Require (EqList @($r['Imports'] | ForEach-Object { ([string]$_).ToUpperInvariant() } | Sort-Object) @($expectedImports | Sort-Object)) "$ProfileKey $opt build receipt import list mismatch."

        $exe = Join-Path $dir 'native_http_host.exe'
        $dll = Join-Path $dir 'owning-stack-native-host.dll'
        $sqlite = Join-Path $dir 'e_sqlite3.dll'
        $importsPath = Join-Path $dir 'native-imports.txt'
        Require ((Hash $exe) -eq ([string]$r['HostExeSha256']).ToLowerInvariant()) "$ProfileKey $opt host executable hash mismatch."
        Require ((Hash $dll) -eq ([string]$r['ModuleSha256']).ToLowerInvariant()) "$ProfileKey $opt native module hash mismatch."
        Require ((Hash $mPath) -eq ([string]$r['ManifestSha256']).ToLowerInvariant()) "$ProfileKey $opt manifest hash mismatch."
        Require ((Hash $sqlite) -eq ([string]$r['SqliteSha256']).ToLowerInvariant()) "$ProfileKey $opt SQLite DLL hash mismatch."
        Require (([string]$r['ModuleSha256']).ToLowerInvariant() -eq ([string]$Room['artifactsSha256']['O2/dll']).ToLowerInvariant()) "$ProfileKey $opt host uses a different validated room module."
        Require (([string]$r['ManifestSha256']).ToLowerInvariant() -eq ([string]$Room['artifactsSha256']['O2/manifest']).ToLowerInvariant()) "$ProfileKey $opt host uses a different validated room manifest."
        Require (([string]$r['SqliteSha256']).ToLowerInvariant() -eq ([string]$Room['artifactsSha256']['O2/sqlite']).ToLowerInvariant()) "$ProfileKey $opt host uses a different validated SQLite library."
        $imports = [IO.File]::ReadAllText($importsPath, [Text.Encoding]::UTF8)
        Require (-not ($imports -match '(?i)mscoree|hostfxr|hostpolicy|coreclr|clr\.dll')) "$ProfileKey $opt native import audit shows CLR imports."
        $modules = @([regex]::Matches($imports, '(?im)^\s*Name:\s*([^\r\n]+)') | ForEach-Object { $_.Groups[1].Value.Trim().ToUpperInvariant() } | Sort-Object -Unique)
        Require (EqList $modules @($expectedImports | Sort-Object)) "$ProfileKey $opt native import audit differs from the receipt."

        AddFile $rPath "evidence/native-build/$ProfileKey/$opt/build-receipt.json" receipt -Text
        AddFile $mPath "evidence/native-build/$ProfileKey/$opt/owning-stack-native-host-manifest.json" receipt -Text
        AddFile $exe "evidence/native-build/$ProfileKey/$opt/native_http_host.exe" artifact
        AddFile $dll "evidence/native-build/$ProfileKey/$opt/owning-stack-native-host.dll" artifact
        AddFile $sqlite "evidence/native-build/$ProfileKey/$opt/e_sqlite3.dll" artifact
        AddFile $importsPath "evidence/native-build/$ProfileKey/$opt/native-imports.txt" log -Text
        $stdout = Join-Path $dir 'native-run.stdout.log'
        if (Test-Path -LiteralPath $stdout -PathType Leaf) { AddFile $stdout "evidence/native-build/$ProfileKey/$opt/native-run.stdout.log" log -Text }
        $receipts[$opt] = $r
    }
    $script:ProfileBuildPins[$ProfileKey] = @{}
    foreach ($path in $pins.Keys) { if ($path -ne 'generated/http_host_pins.h') { $script:ProfileBuildPins[$ProfileKey][$path] = ([string]$pins[$path]).ToLowerInvariant() } }
    AddFile $pinsPath "evidence/native-build/$ProfileKey/source-pins.json" receipt -Text
    AddFile $header "evidence/native-build/$ProfileKey/http_host_pins.h" artifact
    $receipts
}

function AddRange([string] $Root) {
    $path = Join-Path $Root 'receipt.json'
    $r = Json $path
    Require ([string]$r['suite'] -ceq 'owning_stack_runtime_ranges_v1' -and [string]$r['status'] -ceq 'pass') 'Focused range receipt did not pass.'
    SamePinList @($r['sourceHashesBefore']) @($r['sourceHashesAfter']) 'Focused range source freeze'
    PinList @($r['sourceHashesBefore'])
    foreach ($run in $r['runs']) {
        $opt = [string]$run['optimization']
        Require ([string]$run['output'] -match 'owning_stack_runtime_ranges_v1=passed') "$opt focused range test failed."
        $exe = Join-Path $Root "owning-stack-ranges-$opt.exe"
        Require ((Hash $exe) -eq ([string]$run['executableSha256']).ToLowerInvariant()) "$opt focused range executable hash mismatch."
        AddFile $exe "evidence/ranges/owning-stack-ranges-$opt.exe" artifact
        AddReceiptLog ([string]$run['buildLog']) $Root evidence/ranges
        AddReceiptLog ([string]$run['runLog']) $Root evidence/ranges
    }
    foreach ($run in $r['existingNativeStorageRegression']) {
        $opt = [string]$run['optimization']
        $exe = Join-Path $Root "owning-stack-storage-$opt.exe"
        Require ((Hash $exe) -eq ([string]$run['executableSha256']).ToLowerInvariant()) "$opt storage executable hash mismatch."
        $result = [string]$run['output'] | ConvertFrom-Json -AsHashtable
        Require ([string]$result['status'] -ceq 'pass' -and [int]$result['checks'] -eq 872) "$opt storage regression did not pass."
        AddFile $exe "evidence/ranges/owning-stack-storage-$opt.exe" artifact
        AddReceiptLog ([string]$run['buildLog']) $Root evidence/ranges/storage
        AddReceiptLog ([string]$run['runLog']) $Root evidence/ranges/storage
    }
    foreach ($run in $r['trustedProfileBuilds']) {
        $opt = [string]$run['optimization']
        $obj = Join-Path $Root "owning-stack-runtime-trusted-$opt.obj"
        Require ((Hash $obj) -eq ([string]$run['objectSha256']).ToLowerInvariant()) "$opt trusted runtime object hash mismatch."
        AddFile $obj "evidence/ranges/owning-stack-runtime-trusted-$opt.obj" artifact
        AddReceiptLog ([string]$run['buildLog']) $Root evidence/ranges/trusted-runtime
    }
    AddFile $path 'evidence/ranges/focused-range-receipt.json' receipt -Text
}

function AddLlvm([string] $Root) {
    $path = Join-Path $Root 'full-suite-receipt.json'
    $r = Json $path
    Require ([string]$r['suite'] -ceq 'AgentLang.Llvm.Tests full suite' -and [int]$r['terminalExitCode'] -eq 0 -and [int]$r['assertions'] -gt 0) 'Full LLVM regression receipt is not a passing run.'
    PinList @($r['sourceHashes'])
    AddFile $path 'evidence/llvm/full-suite-receipt.json' receipt -Text
    AddReceiptLog ([string]$r['outputLog']) $Root evidence/llvm
}

function AddCookieExpiry([string] $Root) {
    $path = Join-Path $Root 'receipt.json'
    $r = Json $path
    Require ([string]$r['suite'] -ceq 'campfire_cookie_expiry_v1' -and [string]$r['status'] -ceq 'pass') 'Cookie-expiry receipt did not pass.'
    Require ([string]$r['oracle'] -ceq '.NET DateTimeOffset.AddYears(20).UtcDateTime.ToString("R", InvariantCulture)') 'Cookie-expiry receipt oracle mismatch.'
    Require ([int]$r['calendarOracleCases'] -eq 9600 -and [string]$r['calendarOracleSha256'] -match '^[0-9a-fA-F]{64}$') 'Cookie-expiry Gregorian oracle receipt mismatch.'
    SamePinList @($r['sourceHashesBefore']) @($r['sourceHashesAfter']) 'Cookie-expiry source freeze'
    PinList @($r['sourceHashesBefore'])
    $oraclePath = RepoFile ([string]$r['calendarOracleFile'])
    Require ($oraclePath.StartsWith($Root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) 'Cookie-expiry oracle file escapes the selected run root.'
    Require ((Hash $oraclePath) -eq ([string]$r['calendarOracleSha256']).ToLowerInvariant()) 'Cookie-expiry oracle file hash mismatch.'
    AddReceiptLog ([string]$r['calendarOracleFile']) $Root evidence/cookie-expiry
    foreach ($run in $r['runs']) {
        $opt = [string]$run['optimization']
        Require ([int]$run['dotnetOracleCases'] -eq 11 -and [int]$run['calendarOracleCases'] -eq 9600 -and ([string]$run['calendarOracleSha256']).ToLowerInvariant() -eq ([string]$r['calendarOracleSha256']).ToLowerInvariant() -and [string]$run['nativeReceipt'] -ceq 'campfire_cookie_expiry_v1=passed valid_cases=11 rejection_cases=5 short_capacity_cases=29 batch_cases=9600') "$opt cookie-expiry receipt mismatch."
        $exe = Join-Path $Root "campfire-cookie-expiry-$opt.exe"
        Require ((Hash $exe) -eq ([string]$run['executableSha256']).ToLowerInvariant()) "$opt cookie-expiry executable hash mismatch."
        AddFile $exe "evidence/cookie-expiry/campfire-cookie-expiry-$opt.exe" artifact
        AddReceiptLog ([string]$run['buildLog']) $Root evidence/cookie-expiry
        AddReceiptLog ([string]$run['runLog']) $Root evidence/cookie-expiry
    }
    AddFile $path 'evidence/cookie-expiry/receipt.json' receipt -Text
}

function AddCookieParity([string] $Root) {
    foreach ($profile in @('diagnostic', 'trusted-generated')) {
        foreach ($opt in @('O0', 'O2')) {
            $name = "$profile-$opt-redacted-receipt.json"
            $path = Join-Path $Root $name
            $r = Json $path
            Require ([string]$r['result'] -ceq 'pass' -and [int]$r['requestCount'] -eq 12 -and [int]$r['assertions'] -eq 291) "$profile $opt redacted cookie parity receipt did not pass."
            Require ([string]$r['nativeEndpoint'] -ceq '127.0.0.1:13773' -and [string]$r['controlEndpoint'] -ceq '127.0.0.1:13774') "$profile $opt cookie parity endpoint mismatch."
            Require (@($r['cases']).Count -eq 6 -and @($r['invalidExpiresWeekdays']).Count -eq 0) "$profile $opt cookie parity case count or expiry weekday check failed."
            Require ([bool]$r['headFraming']['nativeHeadContentLengthMatchesGet'] -and [bool]$r['headFraming']['controlHeadFramingDiffersFromNative']) "$profile $opt cookie parity HEAD framing checks failed."
            AddFile $path "evidence/http/$name" receipt -Text
        }
    }

    foreach ($name in @(
        'diagnostic-O0-corpus.json', 'diagnostic-O2-corpus.json',
        'trusted-generated-O0-corpus.json', 'trusted-generated-O2-corpus.json',
        'reference-corpus.json'
    )) {
        $path = Join-Path $Root $name
        $rows = @(Json $path)
        Require ($rows.Count -gt 0) "HTTP corpus is empty: $name"
        foreach ($row in $rows) {
            Require ($null -ne $row['name'] -and $null -ne $row['status'] -and $null -ne $row['expectedStatus'] -and $null -ne $row['bytes']) "HTTP corpus row lacks required result fields: $name"
            Require ([long]$row['status'] -eq [long]$row['expectedStatus'] -and [long]$row['bytes'] -ge 0) "HTTP corpus row status/length mismatch: $name"
            Require ([string]$row['bodySha256'] -match '^[0-9a-fA-F]{64}$') "HTTP corpus row body digest is invalid: $name"
            Require ($null -ne $row['cookieNames'] -and @($row['cookieNames'] | Where-Object { [string]$_ -match '(?i)session_token|secret|authorization' }).Count -eq 0) "HTTP corpus row has no cookie-name summary or contains a sensitive name: $name"
        }
        AddFile $path "evidence/http/$name" corpus -Text
    }

    foreach ($name in @('diagnostic-O2-transport.json', 'trusted-generated-O2-transport.json')) {
        $path = Join-Path $Root $name
        $r = Json $path
        Require ([bool]$r['passed'] -and @($r['cases']).Count -gt 0) "HTTP transport-limit validation did not pass: $name"
        foreach ($case in $r['cases']) {
            Require ([bool]$case['passed'] -and [long]$case['responseBytes'] -ge 0) "HTTP transport-limit case failed: $name"
        }
        AddFile $path "evidence/http/$name" receipt -Text
    }

    $summaryPath = Join-Path $Root 'corpus-parity-summary.json'
    if (Test-Path -LiteralPath $summaryPath -PathType Leaf) {
        $summary = Json $summaryPath
        Require ([bool]$summary['passed'] -and [int]$summary['casesPerProfile'] -eq 20 -and [int]$summary['profiles'] -eq 4 -and @($summary['differences']).Count -eq 0) 'HTTP corpus-parity summary is inconsistent with the four-profile corpus.'
        AddFile $summaryPath 'evidence/http/corpus-parity-summary.json' receipt -Text
    }

    $transientPath = Join-Path $Root 'transient-cookie-failure.json'
    if (Test-Path -LiteralPath $transientPath -PathType Leaf) {
        $transient = Json $transientPath
        Require ([string]$transient['test'] -ceq 'diagnostic-O2-cookie-parity' -and [int]$transient['firstAttemptExitCode'] -ne 0 -and [int]$transient['secondAttemptExitCode'] -eq 0 -and -not [bool]$transient['sourceChangedBetweenAttempts']) 'Transient cookie-parity history is malformed.'
        AddFile $transientPath 'evidence/http/transient-cookie-failure.json' history -Text
    }
    $lifecyclePath = Join-Path $Root 'listener-lifecycle.json'
    if (Test-Path -LiteralPath $lifecyclePath -PathType Leaf) {
        $lifecycle = Json $lifecyclePath
        Require ([bool]$lifecycle['fixtureOnly'] -and -not [bool]$lifecycle['secretTransmitted']) 'Listener lifecycle must describe local fixtures.'
        foreach ($run in $lifecycle['tests']) {
            Require ([int]$run['exitCode'] -eq 0 -and [int]$run['fatalStatus'] -eq 0 -and [int]$run['cleanedWorkers'] -gt 0) 'Native listener did not stop cleanly.'
        }
        Require ([int]$lifecycle['reference']['exitCode'] -eq 0 -and [string]$lifecycle['reference']['status'] -ceq 'STOPPED') 'Reference listener did not stop cleanly.'
        AddFile $lifecyclePath 'evidence/http/listener-lifecycle.json' receipt -Text
    }
}

function AddHttpRunEvidence([string] $DiagnosticBuildRoot, [string] $TrustedBuildRoot) {
    foreach ($relative in @(
        'tests/AgentLang.Campfire.NativeHttp.Tests/AgentLang.Campfire.NativeHttp.Tests.fsproj',
        'tests/AgentLang.Campfire.NativeHttp.Tests/Program.fs',
        'tests/AgentLang.Campfire.NativeHttp.Tests/README.md',
        'tests/AgentLang.Campfire.NativeHttp.Tests/transport-limits.ps1'
    )) {
        $path = RepoFile $relative
        PinSource $relative (Hash $path)
    }
    foreach ($profile in @(@{ key = 'diagnostic'; root = $DiagnosticBuildRoot }, @{ key = 'trusted-generated'; root = $TrustedBuildRoot })) {
        $oneWorker = Join-Path $profile.root 'O2/native-one-worker.stdout.log'
        if (Test-Path -LiteralPath $oneWorker -PathType Leaf) {
            AddFile $oneWorker "evidence/native-build/$($profile.key)/O2/native-one-worker.stdout.log" log -Text
        }
    }
}

function AddMeasurements([string[]] $Roots) {
    $picked = [Collections.Generic.List[string]]::new()
    $corpusCount = @($script:Files | Where-Object { $_.Archive -match '(?i)-corpus\.json$' }).Count
    $parityCount = @($script:Files | Where-Object { $_.Archive -match '(?i)(?:-parity\.json|-redacted-receipt\.json|-transport(?:-limits)?\.json)$' }).Count
    $i = 0
    foreach ($rootValue in $Roots) {
        $root = FullDir $rootValue
        Require (InRepo $root) "Measurement root must be inside the repository: $rootValue"
        $prefix = 'evidence/measurements/{0:D2}-{1}' -f $i, (Split-Path -Leaf $root)
        foreach ($f in @(Get-ChildItem -LiteralPath $root -File -Force | Sort-Object Name)) {
            $name = $f.Name
            if ($name -match '(?i)^(?:private-(?:request|measure)(?:\.[A-Za-z0-9._-]+)?|synthetic-secret\.txt|.*\.config\.json|.*\.sqlite\w*|.*\.db|.*-wal|.*-shm)$') { continue }
            $corpus = $name -match '^[A-Za-z0-9][A-Za-z0-9._-]*-corpus\.json$'
            $parity = $name -match '^[A-Za-z0-9][A-Za-z0-9._-]*-parity\.json$'
            $result = $name -match '^(?:native|reference)(?:-r[0-9]+)?(?:-[A-Za-z0-9][A-Za-z0-9._-]*)?-c[0-9]+\.result\.json$'
            $client = $name -match '^(?:native|reference)(?:-r[0-9]+)?(?:-[A-Za-z0-9][A-Za-z0-9._-]*)?-c[0-9]+\.client\.log$'
            $summary = $name -in @('measurement-summary.json', 'machine-and-run.json')
            $runLog = $name -match '^(?:native|reference)(?:-[A-Za-z0-9._-]+)*-(?:run|host|server)(?:[.-][A-Za-z0-9._-]+)*\.log$'
            if (-not ($corpus -or $parity -or $result -or $client -or $summary -or $runLog)) {
                if ($name -match '(?i)cookie|secret|authorization|request|fixture') { throw "Unrecognized sensitive-looking measurement file: $(Rel $f.FullName)" }
                continue
            }
            Require ($name -notmatch '(?i)cookie|secret|authorization|request|fixture|config|sqlite|\.db|\.wal|\.shm') "Sensitive measurement filename rejected: $name"
            $kind = if ($corpus -or $parity) { 'corpus' } elseif ($result -or $client -or $summary) { 'measurement' } else { 'log' }
            AddFile $f.FullName "$prefix/$name" $kind -Text
            $picked.Add($name)
            if ($corpus) { $corpusCount++ }
            if ($parity) { $parityCount++ }
            if ($result -or $client -or $summary -or $corpus -or $parity) {
                if ($name -match '\.json$|\.client\.log$') {
                    $text = [IO.File]::ReadAllText($f.FullName, [Text.Encoding]::UTF8)
                    if ($result) {
                        $row = $text | ConvertFrom-Json -AsHashtable
                        Require ([int]$row['failures'] -eq 0 -and [long]$row['correctResponses'] -gt 0) "Measurement failed: $(Rel $f.FullName)"
                        $uri = [Uri]([string]$row['uri'])
                        Require ($uri.Host -eq '127.0.0.1' -and -not $uri.UserInfo -and -not $uri.Query -and -not $uri.Fragment) "Measurement URI is not redacted loopback data: $(Rel $f.FullName)"
                    }
                    elseif ($client) {
                        $row = $text | ConvertFrom-Json -AsHashtable
                        Require ([int]$row['failures'] -eq 0 -and [long]$row['correctResponses'] -gt 0) "Client measurement failed: $(Rel $f.FullName)"
                    }
                    elseif ($name -eq 'measurement-summary.json') {
                        $rows = $text | ConvertFrom-Json -AsHashtable
                        $servers = @($rows | ForEach-Object { [string]$_.server } | Sort-Object -Unique)
                        Require (($servers -contains 'native') -and (@($servers | Where-Object { $_ -notin @('native', 'reference') }).Count -eq 0)) 'Measurement summary has an unknown or missing native server mode.'
                        Require (@($rows | Where-Object { [int]$_.failures -ne 0 }).Count -eq 0) 'Measurement summary reports failures.'
                        $script:MeasurementModes.Add([ordered]@{
                            root = Rel $root
                            mode = if ($servers -contains 'reference') { 'paired-native-reference' } else { 'native-only' }
                            servers = $servers
                            sampleCount = $rows.Count
                        })
                    }
                }
            }
        }
        $i++
    }
    Require (@($picked | Where-Object { $_ -match '^native(?:-r[0-9]+)?(?:-[A-Za-z0-9][A-Za-z0-9._-]*)?-c[0-9]+\.result\.json$' }).Count -gt 0) 'No native result JSON selected.'
    Require (@($picked | Where-Object { $_ -eq 'measurement-summary.json' }).Count -gt 0) 'No measurement summary selected.'
    Require ($corpusCount -gt 0 -and $parityCount -gt 0) 'At least one redacted corpus and parity JSON must be supplied.'
    Require (@($script:MeasurementModes | Where-Object { $_.mode -eq 'paired-native-reference' }).Count -gt 0) 'No paired native/reference measurement root was supplied.'
    Require (@($script:MeasurementModes | Where-Object { $_.mode -eq 'native-only' }).Count -gt 0) 'No explicitly native-only measurement root was supplied.'
}

function AddExtraSources {
    $paths = @(
        'applications/campfire/Native/http/README.md',
        'scripts/Build-CampfireNativeHttp.ps1', 'scripts/Verify-OwningStackRuntimeRanges.ps1',
        'tests/native/owning_stack_runtime_ranges_test.c',
        'tests/AgentLang.Llvm.Tests/AgentLang.Llvm.Tests.fsproj', 'tests/AgentLang.Llvm.Tests/Program.fs', 'tests/AgentLang.Llvm.Tests/ArenaLifetimeTests.fs',
        'experiments/AgentLang.Campfire.HttpMeasure/HttpMeasure.csproj', 'experiments/AgentLang.Campfire.HttpMeasure/Program.cs', 'experiments/AgentLang.Campfire.HttpMeasure/README.md',
        'experiments/AgentLang.Campfire.HttpReference/ReferenceHost.fsproj', 'experiments/AgentLang.Campfire.HttpReference/Program.fs', 'experiments/AgentLang.Campfire.HttpReference/README.md',
        'docs/CAMPFIRE-AOT-PLAN.md', 'docs/SQLITE-POLICY.md',
        'scripts/Freeze-CampfireNativeHttpEvidence.ps1', 'scripts/Freeze-CampfireNativeReleaseEvidence.ps1'
    )
    foreach ($relative in $paths) {
        $path = RepoFile $relative
        PinSource $relative (Hash $path)
    }
}

function AddReleaseProfileEvidence {
    $evidenceRoot = Join-Path $script:Repo '.agentlang/campfire-native-release-254/evidence'
    $knownFiles = @(
        @{ path = 'release-profile-build-summary.json'; archive = 'evidence/release-profile/build-summary.json'; kind = 'receipt' },
        @{ path = 'profile-rejection-tests.json'; archive = 'evidence/release-profile/profile-rejection-tests.json'; kind = 'receipt' },
        @{ path = 'diagnostic-reject-trusted-selection.json'; archive = 'evidence/release-profile/diagnostic-reject-trusted-selection.json'; kind = 'receipt' },
        @{ path = 'trusted-generated-reject-diagnostic-selection.json'; archive = 'evidence/release-profile/trusted-generated-reject-diagnostic-selection.json'; kind = 'receipt' },
        @{ path = 'diagnostic-both-build-final.log'; archive = 'evidence/release-profile/diagnostic-both-build-final.log'; kind = 'log' },
        @{ path = 'trusted-generated-both-build-final.log'; archive = 'evidence/release-profile/trusted-generated-both-build-final.log'; kind = 'log' }
    )
    foreach ($item in $knownFiles) {
        $path = Join-Path $evidenceRoot $item.path
        if (Test-Path -LiteralPath $path -PathType Leaf) { AddFile $path $item.archive $item.kind -Text }
    }
    $review = Join-Path $script:Repo '.agentlang/campfire-native-release-254/safety-review.md'
    if (Test-Path -LiteralPath $review -PathType Leaf) { AddFile $review 'evidence/release-profile/safety-review.md' review -Text }
}

function Stable {
    foreach ($file in $script:Files) { Require ((Hash $file.Path) -eq $script:Hashes[$file.Path]) "Evidence changed during freeze: $($file.Source)" }
    foreach ($relative in $script:Sources.Keys) { Require ((Hash (RepoFile $relative)) -eq $script:Sources[$relative]) "Source changed during freeze: $relative" }
}

function WriteJson([string] $Path, [object] $Value) {
    [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 100) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

try {
    $diagnosticRoomRoot = FullDir $DiagnosticRoomRunRoot
    $diagnosticBuildRoot = FullDir $DiagnosticNativeBuildRoot
    $trustedRoomRoot = FullDir $TrustedGeneratedRoomRunRoot
    $trustedBuildRoot = FullDir $TrustedGeneratedNativeBuildRoot
    $rangeRoot = FullDir $RangeRunRoot
    $cookieExpiryRoot = FullDir $CookieExpiryRunRoot
    $cookieParityRoot = FullDir $CookieParityRoot
    $llvmRoot = if ($FullLlvmRunRoot) { FullDir $FullLlvmRunRoot } else { FullDir (Join-Path $script:Repo '.agentlang/campfire-native-release-254/llvm-regression') }
    foreach ($root in @($diagnosticRoomRoot, $diagnosticBuildRoot, $trustedRoomRoot, $trustedBuildRoot, $rangeRoot, $cookieExpiryRoot, $cookieParityRoot, $llvmRoot)) { Require (InRepo $root) "Run roots must be inside the repository: $root" }
    Require ($MeasurementRoots.Count -gt 0) 'At least one MeasurementRoot is required.'
    $roomRunsParent = [IO.Path]::GetFullPath((Join-Path $script:Repo '.agentlang/campfire-native-room-read-252/test-runs'))
    $buildsParent = [IO.Path]::GetFullPath((Join-Path $script:Repo '.agentlang/campfire-native-release-254/http-host'))
    $httpValidationRoot = [IO.Path]::GetFullPath((Join-Path $script:Repo '.agentlang/campfire-native-release-254/http-validation'))
    Require ([IO.Path]::GetDirectoryName($diagnosticRoomRoot).Equals($roomRunsParent, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetDirectoryName($trustedRoomRoot).Equals($roomRunsParent, [StringComparison]::OrdinalIgnoreCase)) 'Profile room roots must be direct children of the fresh room-read test-runs directory.'
    Require ([IO.Path]::GetDirectoryName($diagnosticBuildRoot).Equals($buildsParent, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetDirectoryName($trustedBuildRoot).Equals($buildsParent, [StringComparison]::OrdinalIgnoreCase)) 'Profile build roots must be direct children of the 254 HTTP host build directory.'
    Require ([IO.Path]::GetFileName($diagnosticBuildRoot) -match '^build-diagnostic-' -and [IO.Path]::GetFileName($trustedBuildRoot) -match '^build-trusted-generated-') 'Build roots are not named for their selected runtime profiles.'
    Require ($cookieParityRoot.Equals($httpValidationRoot, [StringComparison]::OrdinalIgnoreCase)) 'CookieParityRoot must be the fixed 254 HTTP validation root.'
    $output = if ([IO.Path]::IsPathRooted($OutputRoot)) { [IO.Path]::GetFullPath($OutputRoot) } else { [IO.Path]::GetFullPath((Join-Path $script:Repo $OutputRoot)) }
    Require (InRepo $output) 'OutputRoot must be inside the repository.'
    NoReparsePath $output
    Require (-not (Test-Path -LiteralPath $output)) "Immutable output already exists: $output"

    $diagnosticRoom = AddRoomRun $diagnosticRoomRoot 'diagnostic' 'diagnostic'
    $trustedRoom = AddRoomRun $trustedRoomRoot 'trusted-generated' 'trusted-generated'
    SameMap $diagnosticRoom['sourceSha256'] $trustedRoom['sourceSha256'] 'Diagnostic/trusted room source freeze'
    $diagnosticBuildReceipts = AddBuild $diagnosticBuildRoot $diagnosticRoomRoot $diagnosticRoom 'diagnostic' 'diagnostic'
    $trustedBuildReceipts = AddBuild $trustedBuildRoot $trustedRoomRoot $trustedRoom 'trusted-generated' 'trusted-generated'
    SameMap $script:ProfileBuildPins['diagnostic'] $script:ProfileBuildPins['trusted-generated'] 'Profile build source pins'
    $schemaSerializations = @($script:BoundarySchemas.Values | Sort-Object -Unique)
    Require ($schemaSerializations.Count -eq 1) 'Diagnostic and trusted-generated manifests do not share the same boundary schemas.'
    $layoutSerializations = @($script:SharedLayouts.Values | Sort-Object -Unique)
    Require ($layoutSerializations.Count -eq 1) 'Diagnostic and trusted-generated manifests do not share the same shared layout.'
    AddRange $rangeRoot
    AddCookieExpiry $cookieExpiryRoot
    AddCookieParity $cookieParityRoot
    AddExtraSources
    AddReleaseProfileEvidence
    AddHttpRunEvidence $diagnosticBuildRoot $trustedBuildRoot
    AddMeasurements $MeasurementRoots
    AddLlvm $llvmRoot

    foreach ($file in $script:Files) { $script:Hashes[$file.Path] = Hash $file.Path }
    Stable
    $rangeHash = Hash (Join-Path $rangeRoot 'receipt.json')
    $expiryHash = Hash (Join-Path $cookieExpiryRoot 'receipt.json')
    $llvmHash = Hash (Join-Path $llvmRoot 'full-suite-receipt.json')
    $measureRel = @($MeasurementRoots | ForEach-Object { Rel (FullDir $_) })

    if ($ValidateOnly) {
        Stable
        [pscustomobject]@{ status = 'validated-only'; profiles = @('diagnostic', 'trusted-generated'); roomAssertions = 238; roomSourceCount = $diagnosticRoom['sourceSha256'].Count; curatedSources = $script:Sources.Count; archiveEntries = $script:Files.Count; rangeRoot = (Rel $rangeRoot); cookieExpiryRoot = (Rel $cookieExpiryRoot); cookieParityRoot = (Rel $cookieParityRoot); llvmRoot = (Rel $llvmRoot); measurementRoots = $measureRel; outputRoot = $output; archiveCreated = $false } | ConvertTo-Json -Depth 5
        return
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $stageParent = Join-Path $script:Repo '.agentlang/campfire-native-release-254'
    [void][IO.Directory]::CreateDirectory($stageParent)
    NoReparsePath $stageParent
    $stage = Join-Path $stageParent ('.release-evidence-stage-' + [Guid]::NewGuid().ToString('N'))
    $payload = Join-Path $stage 'payload'
    $publishStage = $null
    [void][IO.Directory]::CreateDirectory($payload)
    try {
        $entries = [Collections.Generic.List[object]]::new()
        foreach ($file in $script:Files) {
            $target = Join-Path $payload $file.Archive.Replace('/', [IO.Path]::DirectorySeparatorChar)
            [void][IO.Directory]::CreateDirectory((Split-Path -Parent $target))
            [IO.File]::Copy($file.Path, $target, $false)
            $copyHash = Hash $target
            Require ($copyHash -eq $script:Hashes[$file.Path]) "Copied file hash differs: $($file.Source)"
            $entries.Add([ordered]@{ archivePath = $file.Archive; sourcePath = $file.Source; sha256 = $copyHash; sizeBytes = [long](Get-Item -LiteralPath $target).Length; kind = $file.Kind })
        }

        $embedded = [ordered]@{
            format = 'campfire-native-release-evidence-v1'
            createdUtc = [DateTime]::UtcNow.ToString('o')
            profiles = @(
                [ordered]@{ runtimeProfile = 'diagnostic'; roomReceiptSha256 = (Hash (Join-Path $diagnosticRoomRoot 'test-receipt.json')); roomSourceFreezeSha256 = (Hash (Join-Path $diagnosticRoomRoot 'test-source-freeze.json')); buildSourcePinsSha256 = (Hash (Join-Path $diagnosticBuildRoot 'source-pins.json')); O0HostExeSha256 = $diagnosticBuildReceipts['O0']['HostExeSha256']; O2HostExeSha256 = $diagnosticBuildReceipts['O2']['HostExeSha256'] },
                [ordered]@{ runtimeProfile = 'trusted-generated'; roomReceiptSha256 = (Hash (Join-Path $trustedRoomRoot 'test-receipt.json')); roomSourceFreezeSha256 = (Hash (Join-Path $trustedRoomRoot 'test-source-freeze.json')); buildSourcePinsSha256 = (Hash (Join-Path $trustedBuildRoot 'source-pins.json')); O0HostExeSha256 = $trustedBuildReceipts['O0']['HostExeSha256']; O2HostExeSha256 = $trustedBuildReceipts['O2']['HostExeSha256'] }
            )
            focusedRangeReceiptSha256 = $rangeHash
            cookieExpiryReceiptSha256 = $expiryHash
            fullLlvmReceiptSha256 = $llvmHash
            measurementModes = @($script:MeasurementModes)
            roomAssertions = 238
            roomSourceCount = $diagnosticRoom['sourceSha256'].Count
            curatedSources = @($script:Sources.Keys | Sort-Object | ForEach-Object { [ordered]@{ path = $_; sha256 = $script:Sources[$_] } })
            entries = @($entries | Sort-Object archivePath)
            exclusions = @('*.config.json', 'private-request*', 'private-measure*', '*.sqlite*', '*.db', '*-wal', '*-shm', 'secrets/cookies/credentials', 'native HTTP cookies and authorization values')
        }
        $embeddedPath = Join-Path $payload 'evidence-manifest.json'
        WriteJson $embeddedPath $embedded
        $embeddedHash = Hash $embeddedPath
        $embeddedSize = (Get-Item -LiteralPath $embeddedPath).Length
        $zip = Join-Path $stage '254-native-campfire-release-evidence.zip'
        [IO.Compression.ZipFile]::CreateFromDirectory($payload, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
        Stable

        $external = [ordered]@{
            format = 'campfire-native-release-evidence-v1'
            archiveFile = [IO.Path]::GetFileName($zip)
            archiveSha256 = Hash $zip
            archiveSizeBytes = [long](Get-Item -LiteralPath $zip).Length
            entries = @($entries + @([ordered]@{ archivePath = 'evidence-manifest.json'; sourcePath = 'generated:embedded-evidence-manifest'; sha256 = $embeddedHash; sizeBytes = [long]$embeddedSize; kind = 'receipt' }) | Sort-Object archivePath)
        }
        WriteJson (Join-Path $stage 'manifest.json') $external
        Stable
        Require (-not (Test-Path -LiteralPath $output)) "Immutable output appeared during staging: $output"
        Require ((Split-Path -Parent ([IO.Path]::GetFullPath($stage))).Equals([IO.Path]::GetFullPath($stageParent), [StringComparison]::OrdinalIgnoreCase)) 'Staging path escaped the private evidence parent.'
        $outputParent = [IO.Path]::GetFullPath((Split-Path -Parent $output))
        [void][IO.Directory]::CreateDirectory($outputParent)
        NoReparsePath $outputParent
        $publishStage = Join-Path $outputParent ('.254-native-release-publish-' + [Guid]::NewGuid().ToString('N'))
        [void][IO.Directory]::CreateDirectory($publishStage)
        NoReparsePath $publishStage
        $publishedZip = Join-Path $publishStage '254-native-campfire-release-evidence.zip'
        $publishedManifest = Join-Path $publishStage 'manifest.json'
        [IO.File]::Copy($zip, $publishedZip, $false)
        [IO.File]::Copy((Join-Path $stage 'manifest.json'), $publishedManifest, $false)
        Require ((Hash $publishedZip) -eq $external['archiveSha256']) 'Published archive copy hash mismatch.'
        Require ((Hash $publishedManifest) -eq (Hash (Join-Path $stage 'manifest.json'))) 'Published manifest copy hash mismatch.'
        $publishItems = @(Get-ChildItem -LiteralPath $publishStage -Force)
        Require ($publishItems.Count -eq 2 -and @($publishItems | Where-Object { $_.Name -notin @('254-native-campfire-release-evidence.zip', 'manifest.json') }).Count -eq 0) 'Publication staging directory contains unexpected files.'
        Stable
        Require (-not (Test-Path -LiteralPath $output)) "Immutable output appeared before publication: $output"
        Require ((Split-Path -Parent ([IO.Path]::GetFullPath($publishStage))).Equals($outputParent, [StringComparison]::OrdinalIgnoreCase) -and (InRepo $publishStage)) 'Publication staging path escaped the output parent.'
        [IO.Directory]::Move([IO.Path]::GetFullPath($publishStage), $output)
        $publishStage = $null
        [pscustomobject]@{ status = 'sealed'; outputRoot = $output; privateEvidenceStage = $stage; archive = Join-Path $output '254-native-campfire-release-evidence.zip'; archiveSha256 = $external['archiveSha256']; manifest = Join-Path $output 'manifest.json'; archiveEntries = $external.entries.Count; curatedSources = $script:Sources.Count } | ConvertTo-Json -Depth 5
    }
    catch {
        if ($publishStage -and (Test-Path -LiteralPath $publishStage)) {
            $publishStageFull = [IO.Path]::GetFullPath($publishStage)
            $publishParent = [IO.Path]::GetFullPath((Split-Path -Parent $publishStageFull))
            $expectedOutputParent = [IO.Path]::GetFullPath((Split-Path -Parent $output))
            if ($publishParent.Equals($expectedOutputParent, [StringComparison]::OrdinalIgnoreCase) -and (InRepo $publishStageFull) -and [IO.Path]::GetFileName($publishStageFull) -match '^\.254-native-release-publish-[0-9a-f]{32}$') { Remove-Item -LiteralPath $publishStageFull -Recurse -Force }
        }
        if (Test-Path -LiteralPath $stage) {
            $stageFull = [IO.Path]::GetFullPath($stage)
            if ((Split-Path -Parent $stageFull).Equals([IO.Path]::GetFullPath($stageParent), [StringComparison]::OrdinalIgnoreCase) -and (InRepo $stageFull) -and [IO.Path]::GetFileName($stageFull) -match '^\.release-evidence-stage-[0-9a-f]{32}$') { Remove-Item -LiteralPath $stageFull -Recurse -Force }
        }
        throw
    }
}
catch {
    [Console]::Error.WriteLine("Evidence freeze failed: $($_.Exception.Message)")
    exit 1
}
