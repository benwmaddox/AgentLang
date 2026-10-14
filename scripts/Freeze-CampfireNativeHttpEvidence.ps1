#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $RoomRunRoot,
    [Parameter(Mandatory = $true)] [string] $NativeBuildRoot,
    [Parameter(Mandatory = $true)] [string] $RangeRunRoot,
    [Parameter(Mandatory = $true)] [string] $CookieExpiryRunRoot,
    [Parameter(Mandatory = $true)] [string] $CookieParityRoot,
    [Parameter(Mandatory = $true)] [string[]] $MeasurementRoots,
    [string] $FullLlvmRunRoot,
    [string] $OutputRoot = 'reports/evidence/253-native-campfire-http',
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

function Require([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

function Hash([string] $Path) {
    NoReparsePath $Path
    Require (Test-Path -LiteralPath $Path -PathType Leaf) "Missing file: $Path"
    Require (((Get-Item -LiteralPath $Path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) "Reparse-point file rejected: $Path"
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
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
        '^scripts/(Verify-CampfireNativeRoomRead|Build-CampfireNativeHttp|Verify-OwningStackRuntimeRanges|Verify-CampfireCookieExpiry|Freeze-CampfireNativeHttpEvidence)\.ps1$',
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
        Require ($script:SeenArchive[$archive].Equals($full, [StringComparison]::OrdinalIgnoreCase)) "Archive path collision: $archive"
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

function AddRoomRun([string] $Root) {
    $receiptPath = Join-Path $Root 'test-receipt.json'
    $freezePath = Join-Path $Root 'test-source-freeze.json'
    $receipt = Json $receiptPath
    $freeze = Json $freezePath
    Require ([bool]$receipt['passed'] -and [int]$receipt['assertions'] -eq 234) 'Room-read receipt must pass all 234 assertions.'
    Require ([bool]$freeze['stable'] -and @($freeze['changedFiles']).Count -eq 0) 'Room-read source freeze is not stable.'
    Require ($receipt['sourceSha256'].Count -eq 63) 'Room-read receipt must pin exactly 63 original sources.'
    SameMap $freeze['before'] $freeze['after'] 'Room source freeze'
    SameMap $receipt['sourceSha256'] $freeze['before'] 'Room receipt and source freeze'
    Require (EqList @($freeze['filesBefore'] | Sort-Object) @($freeze['filesAfter'] | Sort-Object)) 'Room source file lists differ.'
    Require (EqList @($freeze['filesBefore'] | Sort-Object) @($receipt['sourceSha256'].Keys | Sort-Object)) 'Room source file list and hash map differ.'
    PinMap $receipt['sourceSha256']
    AddFile $receiptPath 'evidence/room-read/test-receipt.json' receipt -Text
    AddFile $freezePath 'evidence/room-read/test-source-freeze.json' receipt -Text
    foreach ($name in @('test-restore.log', 'test-build.log', 'test-run.log')) {
        AddFile (Join-Path $Root $name) "evidence/room-read/$name" log -Text
    }
    $receipt
}

function AddBuild([string] $Root, [string] $RoomRoot, [hashtable] $Room) {
    $pinsPath = Join-Path $Root 'source-pins.json'
    $pins = Json $pinsPath
    Require ($pins.Contains('generated/http_host_pins.h')) 'Native build source pins omit the generated pin header.'
    foreach ($path in $Room['sourceSha256'].Keys) {
        Require ($pins.Contains([string]$path)) "Native build omitted room source pin: $path"
        Require (([string]$pins[$path]).ToLowerInvariant() -eq ([string]$Room['sourceSha256'][$path]).ToLowerInvariant()) "Native build/room source pin mismatch: $path"
    }
    $hostPath = 'applications/campfire/Native/http/native_http_host.c'
    $required = @(
        $hostPath,
        'applications/campfire/Native/http/request_encoder.c', 'applications/campfire/Native/http/request_encoder.h',
        
        'applications/campfire/Native/http/campfire_http_renderer.c', 'applications/campfire/Native/http/campfire_http_renderer.h',
        'applications/campfire/Native/http/cookie_response_codec.c', 'applications/campfire/Native/http/cookie_response_codec.h',
        'applications/campfire/Native/http/cookie_wire.c', 'applications/campfire/Native/http/cookie_wire.h',
        'applications/campfire/Native/http/cookie_unescape.c', 'applications/campfire/Native/http/cookie_unescape.h',
        'applications/campfire/Native/http/cookie_expiry.c', 'applications/campfire/Native/http/cookie_expiry.h',
        'scripts/Build-CampfireNativeHttp.ps1'
    )
    foreach ($path in $required) { Require ($pins.Contains($path)) "Native build omitted required source pin: $path" }
    foreach ($path in $pins.Keys) {
        if ($path -eq 'generated/http_host_pins.h') { continue }
        PinSource ([string]$path) ([string]$pins[$path])
    }
    $header = Join-Path $Root 'http_host_pins.h'
    Require ((Hash $header) -eq ([string]$pins['generated/http_host_pins.h']).ToLowerInvariant()) 'Generated pin header hash mismatch.'
    AddFile $pinsPath 'evidence/native-build/source-pins.json' receipt -Text
    AddFile $header 'evidence/native-build/http_host_pins.h' artifact

    $expectedImports = @('BCRYPT.DLL', 'HTTPAPI.DLL', 'KERNEL32.DLL', 'SHELL32.DLL', 'WS2_32.DLL')
    $receipts = @{}
    foreach ($opt in @('O0', 'O2')) {
        $dir = Join-Path $Root $opt
        $rPath = Join-Path $dir 'build-receipt.json'
        $r = Json $rPath
        $mPath = Join-Path $dir 'owning-stack-native-host-manifest.json'
        $m = Json $mPath
        Require ([string]$r['Optimization'] -ceq $opt -and [string]$r['ProvenanceMode'] -ceq 'fresh-validation-receipt') "$opt build receipt is not a fresh expected build."
        Require (([IO.Path]::GetFullPath([string]$r['ValidationRunRoot'])).Equals($RoomRoot, [StringComparison]::OrdinalIgnoreCase)) "$opt build receipt points at a different room run."
        Require (([string]$r['ValidationReceiptSha256']).ToLowerInvariant() -eq (Hash (Join-Path $RoomRoot 'test-receipt.json'))) "$opt build does not pin the accepted room receipt."
        Require (([string]$r['ValidationSourceFreezeSha256']).ToLowerInvariant() -eq (Hash (Join-Path $RoomRoot 'test-source-freeze.json'))) "$opt build does not pin the accepted room freeze."
        Require (([string]$r['HostSourceSha256']).ToLowerInvariant() -eq (Hash (RepoFile $hostPath))) "$opt host source pin is stale."
        Require ([string]$m['moduleInfo']['runtimeProfile'] -ceq 'diagnostic') "$opt manifest is not the diagnostic runtime profile."
        Require ([string]$m['moduleInfo']['profileId'] -ceq 'campfire-room-read-v1' -and [string]$m['moduleInfo']['operationName'] -ceq 'campfire.http.room-read') "$opt manifest profile identity mismatch."
        Require ((EqList @($m['moduleInfo']['effects']) @('db.read', 'db.write')) -and (EqList @($m['moduleInfo']['fixedEffectAllowlist']) @('db.read', 'db.write'))) "$opt manifest effects mismatch."
        $opIds = @($m['moduleInfo']['operations'] | ForEach-Object { [string]$_.operationId })
        Require (EqList $opIds @('campfire.cookies.verify-session-token', 'campfire.sessions.find-by-token', 'campfire.sessions.refresh-before', 'campfire.users.find-by-id', 'campfire.users.authenticate-bot-key', 'campfire.rooms.find-for-user', 'campfire.membership.exists?', 'campfire.messages.last-page')) "$opt manifest operation closure mismatch."
        Require (EqList @($m['moduleInfo']['operations'] | ForEach-Object { [string]$_.tag }) @('1','2','3','4','5','6','7','8','9')) "$opt manifest operation tags mismatch."
        Require (EqList @($m['moduleInfo']['inputTypes']) @('CampfireHttpRequest') -and (EqList @($m['moduleInfo']['outputTypes']) @('CampfireHttpResponse'))) "$opt manifest entry types mismatch."
        Require (([string]$r['GeneratedPinsHeaderSha256']).ToLowerInvariant() -eq (Hash $header)) "$opt generated pin-header hash mismatch."
        Require (EqList @($r['Imports'] | ForEach-Object { ([string]$_).ToUpperInvariant() } | Sort-Object) @($expectedImports | Sort-Object)) "$opt build receipt import list mismatch."

        $exe = Join-Path $dir 'native_http_host.exe'
        $dll = Join-Path $dir 'owning-stack-native-host.dll'
        $sqlite = Join-Path $dir 'e_sqlite3.dll'
        $importsPath = Join-Path $dir 'native-imports.txt'
        Require ((Hash $exe) -eq ([string]$r['HostExeSha256']).ToLowerInvariant()) "$opt host executable hash mismatch."
        Require ((Hash $dll) -eq ([string]$r['ModuleSha256']).ToLowerInvariant()) "$opt native module hash mismatch."
        Require ((Hash $mPath) -eq ([string]$r['ManifestSha256']).ToLowerInvariant()) "$opt manifest hash mismatch."
        Require ((Hash $sqlite) -eq ([string]$r['SqliteSha256']).ToLowerInvariant()) "$opt SQLite DLL hash mismatch."
        $imports = [IO.File]::ReadAllText($importsPath, [Text.Encoding]::UTF8)
        Require (-not ($imports -match '(?i)mscoree|hostfxr|hostpolicy|coreclr|clr\.dll')) "$opt native import audit shows CLR imports."
        $modules = @([regex]::Matches($imports, '(?im)^\s*Name:\s*([^\r\n]+)') | ForEach-Object { $_.Groups[1].Value.Trim().ToUpperInvariant() } | Sort-Object -Unique)
        Require (EqList $modules @($expectedImports | Sort-Object)) "$opt native import audit differs from the receipt."

        AddFile $rPath "evidence/native-build/$opt/build-receipt.json" receipt -Text
        AddFile $mPath "evidence/native-build/$opt/owning-stack-native-host-manifest.json" receipt -Text
        AddFile $exe "evidence/native-build/$opt/native_http_host.exe" artifact
        AddFile $dll "evidence/native-build/$opt/owning-stack-native-host.dll" artifact
        AddFile $sqlite "evidence/native-build/$opt/e_sqlite3.dll" artifact
        AddFile $importsPath "evidence/native-build/$opt/native-imports.txt" log -Text
        $stdout = Join-Path $dir 'native-run.stdout.log'
        if (Test-Path -LiteralPath $stdout -PathType Leaf) { AddFile $stdout "evidence/native-build/$opt/native-run.stdout.log" log -Text }
        $receipts[$opt] = $r
    }
    Require (([string]$Room['artifactsSha256']['O2/dll']).ToLowerInvariant() -eq ([string]$receipts['O2']['ModuleSha256']).ToLowerInvariant()) 'Native O2 module differs from accepted room-read module.'
    Require (([string]$Room['artifactsSha256']['O0/sqlite']).ToLowerInvariant() -eq ([string]$receipts['O0']['SqliteSha256']).ToLowerInvariant()) 'Native O0 SQLite differs from room-read test runtime.'
    Require (([string]$Room['artifactsSha256']['O2/sqlite']).ToLowerInvariant() -eq ([string]$receipts['O2']['SqliteSha256']).ToLowerInvariant()) 'Native O2 SQLite differs from room-read test runtime.'
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
        AddReceiptLog ([string]$run['buildLog']) $Root evidence/ranges
        AddReceiptLog ([string]$run['runLog']) $Root evidence/ranges
    }
    foreach ($run in $r['trustedProfileBuilds']) {
        $opt = [string]$run['optimization']
        $obj = Join-Path $Root "owning-stack-runtime-trusted-$opt.obj"
        Require ((Hash $obj) -eq ([string]$run['objectSha256']).ToLowerInvariant()) "$opt trusted runtime object hash mismatch."
        AddFile $obj "evidence/ranges/owning-stack-runtime-trusted-$opt.obj" artifact
        AddReceiptLog ([string]$run['buildLog']) $Root evidence/ranges
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
    foreach ($opt in @('O0', 'O2')) {
        $path = Join-Path $Root "$opt-redacted-receipt.json"
        $r = Json $path
        Require ([string]$r['result'] -ceq 'pass' -and [int]$r['requestCount'] -eq 12 -and [int]$r['assertions'] -eq 291) "$opt redacted cookie parity receipt did not pass."
        Require ([string]$r['nativeEndpoint'] -ceq '127.0.0.1:13773' -and [string]$r['controlEndpoint'] -ceq '127.0.0.1:13774') "$opt cookie parity endpoint mismatch."
        Require (@($r['cases']).Count -eq 6 -and @($r['invalidExpiresWeekdays']).Count -eq 0) "$opt cookie parity case count or expiry weekday check failed."
        Require ([bool]$r['headFraming']['nativeHeadContentLengthMatchesGet'] -and [bool]$r['headFraming']['controlHeadFramingDiffersFromNative']) "$opt cookie parity HEAD framing checks failed."
        AddFile $path "evidence/cookie-parity/${opt}-redacted-receipt.json" receipt -Text
    }
}

function AddHttpRunEvidence([string] $NativeBuildRoot) {
    foreach ($relative in @(
        '.agentlang/campfire-native-http-253/native-smoke/corrected-O0-corpus.json',
        '.agentlang/campfire-native-http-253/native-smoke/corrected-O0-parity.json',
        '.agentlang/campfire-native-http-253/native-smoke/corrected-O2-corpus.json',
        '.agentlang/campfire-native-http-253/native-smoke/corrected-O2-parity.json',
        '.agentlang/campfire-native-http-253/native-smoke/corrected-O2-transport-limits.json',
        '.agentlang/campfire-native-http-253/native-smoke/pre-fix-expiry-diagnostic.json',
        '.agentlang/campfire-native-http-253/reference-memory-review.md',
        '.agentlang/campfire-native-http-253/trusted-profile-plan.md',
        'tests/AgentLang.Campfire.NativeHttp.Tests/AgentLang.Campfire.NativeHttp.Tests.fsproj',
        'tests/AgentLang.Campfire.NativeHttp.Tests/Program.fs',
        'tests/AgentLang.Campfire.NativeHttp.Tests/README.md',
        'tests/AgentLang.Campfire.NativeHttp.Tests/transport-limits.ps1'
    )) {
        $path = RepoFile $relative
        if ($relative.StartsWith('.agentlang/', [StringComparison]::OrdinalIgnoreCase)) {
            $archive = 'evidence/http/' + [IO.Path]::GetFileName($relative)
            $kind = if ($relative.EndsWith('.md', [StringComparison]::OrdinalIgnoreCase)) { 'review' } else { 'corpus' }
        }
        else {
            PinSource $relative (Hash $path)
            continue
        }
        AddFile $path $archive $kind -Text
    }
    $oneWorker = Join-Path $NativeBuildRoot 'O2/native-one-worker.stdout.log'
    Require (Test-Path -LiteralPath $oneWorker -PathType Leaf) 'O2 one-worker native host log is missing.'
    AddFile $oneWorker 'evidence/native-build/O2/native-one-worker.stdout.log' log -Text
}

function AddMeasurements([string[]] $Roots) {
    $picked = [Collections.Generic.List[string]]::new()
    $corpusCount = @($script:Files | Where-Object { $_.Archive -match '(?i)-corpus\.json$' }).Count
    $parityCount = @($script:Files | Where-Object { $_.Archive -match '(?i)-(?:parity|transport-limits)\.json$' }).Count
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
            $result = $name -match '^(?:native|reference)(?:-r[0-9]+)?-c[0-9]+\.result\.json$'
            $client = $name -match '^(?:native|reference)(?:-r[0-9]+)?-c[0-9]+\.client\.log$'
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
    Require (@($picked | Where-Object { $_ -match '^native(?:-r[0-9]+)?-c[0-9]+\.result\.json$' }).Count -gt 0) 'No native result JSON selected.'
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
        'docs/CAMPFIRE-AOT-PLAN.md', 'docs/SQLITE-POLICY.md', 'scripts/Freeze-CampfireNativeHttpEvidence.ps1'
    )
    foreach ($relative in $paths) {
        $path = RepoFile $relative
        PinSource $relative (Hash $path)
    }
}

function Stable {
    foreach ($file in $script:Files) { Require ((Hash $file.Path) -eq $script:Hashes[$file.Path]) "Evidence changed during freeze: $($file.Source)" }
    foreach ($relative in $script:Sources.Keys) { Require ((Hash (RepoFile $relative)) -eq $script:Sources[$relative]) "Source changed during freeze: $relative" }
}

function WriteJson([string] $Path, [object] $Value) {
    [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 100) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

try {
    $roomRoot = FullDir $RoomRunRoot
    $buildRoot = FullDir $NativeBuildRoot
    $rangeRoot = FullDir $RangeRunRoot
    $cookieExpiryRoot = FullDir $CookieExpiryRunRoot
    $cookieParityRoot = FullDir $CookieParityRoot
    $llvmRoot = if ($FullLlvmRunRoot) { FullDir $FullLlvmRunRoot } else { FullDir (Join-Path $script:Repo '.agentlang/owning-stack-runtime-ranges/llvm-regression-20261012') }
    foreach ($root in @($roomRoot, $buildRoot, $rangeRoot, $cookieExpiryRoot, $cookieParityRoot, $llvmRoot)) { Require (InRepo $root) "Run roots must be inside the repository: $root" }
    Require ($MeasurementRoots.Count -gt 0) 'At least one MeasurementRoot is required.'
    $output = if ([IO.Path]::IsPathRooted($OutputRoot)) { [IO.Path]::GetFullPath($OutputRoot) } else { [IO.Path]::GetFullPath((Join-Path $script:Repo $OutputRoot)) }
    Require (InRepo $output) 'OutputRoot must be inside the repository.'
    NoReparsePath $output
    Require (-not (Test-Path -LiteralPath $output)) "Immutable output already exists: $output"

    $room = AddRoomRun $roomRoot
    AddBuild $buildRoot $roomRoot $room
    AddRange $rangeRoot
    AddLlvm $llvmRoot
    AddCookieExpiry $cookieExpiryRoot
    AddCookieParity $cookieParityRoot
    AddExtraSources
    AddHttpRunEvidence $buildRoot
    AddMeasurements $MeasurementRoots

    foreach ($file in $script:Files) { $script:Hashes[$file.Path] = Hash $file.Path }
    Stable
    $roomReceiptHash = Hash (Join-Path $roomRoot 'test-receipt.json')
    $roomFreezeHash = Hash (Join-Path $roomRoot 'test-source-freeze.json')
    $pinsHash = Hash (Join-Path $buildRoot 'source-pins.json')
    $rangeHash = Hash (Join-Path $rangeRoot 'receipt.json')
    $expiryHash = Hash (Join-Path $cookieExpiryRoot 'receipt.json')
    $llvmHash = Hash (Join-Path $llvmRoot 'full-suite-receipt.json')
    $measureRel = @($MeasurementRoots | ForEach-Object { Rel (FullDir $_) })

    if ($ValidateOnly) {
        Stable
        [pscustomobject]@{ status = 'validated-only'; roomAssertions = 234; roomSources = 63; curatedSources = $script:Sources.Count; archiveEntries = $script:Files.Count; cookieExpiryRoot = (Rel $cookieExpiryRoot); cookieParityRoot = (Rel $cookieParityRoot); measurementRoots = $measureRel; outputRoot = $output; archiveCreated = $false } | ConvertTo-Json -Depth 5
        return
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $parent = Split-Path -Parent $output
    [void][IO.Directory]::CreateDirectory($parent)
    $stage = Join-Path $parent ('.campfire-http-evidence-stage-' + [Guid]::NewGuid().ToString('N'))
    $payload = Join-Path $stage 'payload'
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
            format = 'campfire-native-http-evidence-v1'
            createdUtc = [DateTime]::UtcNow.ToString('o')
            roomReceiptSha256 = $roomReceiptHash
            roomSourceFreezeSha256 = $roomFreezeHash
            nativeBuildSourcePinsSha256 = $pinsHash
            focusedRangeReceiptSha256 = $rangeHash
            cookieExpiryReceiptSha256 = $expiryHash
            fullLlvmReceiptSha256 = $llvmHash
            measurementModes = @($script:MeasurementModes)
            roomAssertions = 234
            originalRoomSourceCount = 63
            curatedSources = @($script:Sources.Keys | Sort-Object | ForEach-Object { [ordered]@{ path = $_; sha256 = $script:Sources[$_] } })
            entries = @($entries | Sort-Object archivePath)
            exclusions = @('*.config.json', 'private-request*', 'private-measure*', '*.sqlite*', '*.db', '*-wal', '*-shm', 'secrets/cookies/credentials', 'reports/253-native-campfire-http.md')
        }
        $embeddedPath = Join-Path $payload 'evidence-manifest.json'
        WriteJson $embeddedPath $embedded
        $embeddedHash = Hash $embeddedPath
        $embeddedSize = (Get-Item -LiteralPath $embeddedPath).Length
        $zip = Join-Path $stage '253-native-campfire-http-evidence.zip'
        [IO.Compression.ZipFile]::CreateFromDirectory($payload, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
        Stable

        $external = [ordered]@{
            format = 'campfire-native-http-evidence-v1'
            archiveFile = [IO.Path]::GetFileName($zip)
            archiveSha256 = Hash $zip
            archiveSizeBytes = [long](Get-Item -LiteralPath $zip).Length
            entries = @($entries + @([ordered]@{ archivePath = 'evidence-manifest.json'; sourcePath = 'generated:embedded-evidence-manifest'; sha256 = $embeddedHash; sizeBytes = [long]$embeddedSize; kind = 'receipt' }) | Sort-Object archivePath)
        }
        WriteJson (Join-Path $stage 'manifest.json') $external
        Stable
        Require (-not (Test-Path -LiteralPath $output)) "Immutable output appeared during staging: $output"
        Require ((Split-Path -Parent ([IO.Path]::GetFullPath($stage))).Equals([IO.Path]::GetFullPath($parent), [StringComparison]::OrdinalIgnoreCase)) 'Staging path escaped output parent.'
        [IO.Directory]::Move([IO.Path]::GetFullPath($stage), $output)
        [pscustomobject]@{ status = 'sealed'; outputRoot = $output; archive = Join-Path $output '253-native-campfire-http-evidence.zip'; archiveSha256 = $external['archiveSha256']; manifest = Join-Path $output 'manifest.json'; archiveEntries = $external.entries.Count; curatedSources = $script:Sources.Count } | ConvertTo-Json -Depth 5
    }
    catch {
        if (Test-Path -LiteralPath $stage) {
            $stageFull = [IO.Path]::GetFullPath($stage)
            if ((Split-Path -Parent $stageFull).Equals([IO.Path]::GetFullPath($parent), [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $stageFull -Recurse -Force }
        }
        throw
    }
}
catch {
    [Console]::Error.WriteLine("Evidence freeze failed: $($_.Exception.Message)")
    exit 1
}
