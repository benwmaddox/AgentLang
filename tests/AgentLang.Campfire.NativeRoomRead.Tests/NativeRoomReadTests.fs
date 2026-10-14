namespace AgentLang.Campfire.NativeRoomRead.Tests

open System
open System.Buffers.Binary
open System.Diagnostics
open System.IO
open System.Reflection.PortableExecutable
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open Microsoft.Data.Sqlite
open AgentLang
open AgentLang.Llvm
open AgentLang.Campfire.Authentication.Host
open AgentLang.Campfire.Browser.Host
open AgentLang.Campfire.Cookies
open AgentLang.Campfire.Host
open AgentLang.Campfire.Http
open AgentLang.Campfire.Persistence

module private NativeModuleInterop =
    [<DllImport("kernel32.dll", EntryPoint = "GetModuleFileNameW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)>]
    extern uint32 getModuleFileName(nativeint moduleHandle, StringBuilder fileName, uint32 size)

    [<DllImport("kernel32.dll", EntryPoint = "GetModuleHandleExW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)>]
    extern bool getModuleHandleEx(uint32 flags, nativeint address, nativeint& moduleHandle)

    [<DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId", ExactSpelling = true)>]
    extern uint32 getCurrentThreadId()

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private PureContextSize = delegate of unit -> uint32

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private PureContextAlignment = delegate of unit -> uint32

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private PureContextInit = delegate of nativeint * uint32 * nativeint * uint32 -> int32

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private PureContextDestroy = delegate of nativeint -> unit

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private RoomReadContextInit = delegate of nativeint * nativeint * uint32 * nativeint * nativeint -> int32

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private RoomReadProvisionWal = delegate of nativeint * uint32 * nativeint -> int32

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private RoomReadContextDestroy = delegate of nativeint -> int32

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private RoomReadDispatch = delegate of nativeint * uint32 * nativeint * nativeint * uint32 * uint32 * nativeint * uint32 * nativeint -> int32

[<Struct; StructLayout(LayoutKind.Sequential, Pack = 8)>]
type private RoomReadLayoutAbi =
    val mutable AbiVersion: uint32
    val mutable Types: nativeint
    val mutable TypeCount: uint32
    val mutable Fields: nativeint
    val mutable FieldCount: uint32

[<Struct; StructLayout(LayoutKind.Sequential, Pack = 8)>]
type private RoomReadInputAbi =
    val mutable Data: nativeint
    val mutable ExtentBytes: uint32
    val mutable TypeIndex: uint32

[<Struct; StructLayout(LayoutKind.Sequential, Pack = 8)>]
type private RoomReadContextAbi =
    val mutable Database: nativeint
    [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 13)>]
    val mutable Statements: nativeint array
    val mutable PureContext: nativeint
    val mutable Cookie: uint64
    val mutable OwnerThreadId: uint32
    val mutable Active: uint32

type private NativeSqliteSettings =
    { LoadedModulePath: string
      LoadedModuleSha256: string
      SqliteVersion: string
      JournalMode: string
      Synchronous: int
      ReadUncommitted: int
      ForeignKeys: int
      WalAutoCheckpoint: int
      BusyTimeout: int
      Autocommit: int }

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private SqlitePrepare = delegate of nativeint * nativeint * int32 * nativeint * nativeint -> int32

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private SqliteStep = delegate of nativeint -> int32

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private SqliteFinalize = delegate of nativeint -> int32

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private SqliteColumnInt64 = delegate of nativeint * int32 -> int64

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private SqliteColumnText16 = delegate of nativeint * int32 -> nativeint

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private SqliteColumnBytes16 = delegate of nativeint * int32 -> int32

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private SqliteGetAutocommit = delegate of nativeint -> int32

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private OwningProfileBegin = delegate of unit -> uint64

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private OwningProfileEnd = delegate of uint32 * uint64 * int32 -> int32

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private OwningProfileReset = delegate of uint32 -> int32

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private OwningProfileSnapshot = delegate of uint32 * nativeint * uint32 -> int32

type private OwningProfileApi =
    { Begin: OwningProfileBegin
      End: OwningProfileEnd
      Reset: OwningProfileReset
      Snapshot: OwningProfileSnapshot }

[<Sealed>]
type private FixedClock(instant: DateTimeOffset) =
    interface IClock with
        member _.UtcNow() = instant

[<RequireQualifiedAccess>]
module NativeRoomReadTests =
    let private secret = String.replicate 64 "n"
    let private fixedNow = DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)
    let private sessionToken = "native-room-read-session-token"
    let private botToken = "nativeRoomReadBotKey"
    let private runtimeProfileName = function
        | OwningRuntimeProfile.Diagnostic -> "diagnostic"
        | OwningRuntimeProfile.TrustedGenerated -> "trusted-generated"
    let private executionProfileName = function
        | OwningRuntimeInstrumentation.Off -> "off"
        | OwningRuntimeInstrumentation.Detailed -> "detailed"
    let private sourceFiles repo =
        let staticFiles =
            [ "applications/campfire/Native/room_read_adapter.c"
              "applications/campfire/Native/room_read_adapter.h"
              "applications/campfire/Native/room_read_pure.c"
              "applications/campfire/Native/room_read_pure.h"
              "applications/campfire/Native/room_read_sqlite.def"
              "applications/campfire/Native/room_read_sqlite_subset.h"
              "applications/campfire/Native/campfire_room_read_host_abi.h"
              "applications/campfire/Native/room-read-host.json"
              "src/AgentLang.Llvm/native/owning_stack_runtime.c"
              "src/AgentLang.Llvm/native/owning_stack_runtime.h"
              "src/AgentLang.Llvm/native/owning_stack_profile.h"
              "src/AgentLang.Llvm/native/float_format.h"
              "src/AgentLang.Llvm/AgentLang.Llvm.fsproj"
              "src/AgentLang.Llvm/LlvmToolchain.fs"
              "src/AgentLang.Llvm/OwningStackAot.fs"
              "tests/AgentLang.Campfire.NativeRoomRead.Tests/AgentLang.Campfire.NativeRoomRead.Tests.fsproj"
              "tests/AgentLang.Campfire.NativeRoomRead.Tests/NativeRoomReadTests.fs"
              "tests/AgentLang.Campfire.NativeRoomRead.Tests/Program.fs"
              "tests/AgentLang.Campfire.NativeRoomRead.Tests/native/room_read_standalone_smoke.c"
              "scripts/Verify-CampfireNativeRoomRead.ps1" ]
        let campfireRoot = Path.Combine(repo, "applications", "campfire")
        let applicationSources =
            [ "*.flow"; "*.fs"; "*.fsproj" ]
            |> List.collect (fun pattern ->
                Directory.GetFiles(campfireRoot, pattern, SearchOption.AllDirectories)
                |> Array.filter (fun path ->
                    let relative = Path.GetRelativePath(campfireRoot, path)
                    relative.Split([| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |])
                    |> Array.forall (fun segment -> segment <> "bin" && segment <> "obj"))
                |> Array.map (fun path -> Path.GetRelativePath(repo, path).Replace('\\', '/'))
                |> Array.toList)
            |> List.distinct
            |> List.sort
        staticFiles @ applicationSources

    type private TestCase =
        { Name: string
          Cookie: string option
          BotKey: string option
          BrowserFamily: string
          BrowserVersion: string option
          RequestNow: DateTimeOffset
          SessionToken: string option
          SessionUserId: int64
          LastActiveAt: string
          RoomId: int64
          ExpectedStatus: int64
          ExpectedView: string
          ExpectedRefresh: bool
          ExpectedMessageCount: int option }

    let private check (counter: int ref) label condition =
        counter.Value <- counter.Value + 1
        if not condition then failwith label

    let private profileApi (nativeHandle: nativeint) : OwningProfileApi =
        { Begin = Marshal.GetDelegateForFunctionPointer<OwningProfileBegin>(NativeLibrary.GetExport(nativeHandle, "al_owning_profile_begin_v1"))
          End = Marshal.GetDelegateForFunctionPointer<OwningProfileEnd>(NativeLibrary.GetExport(nativeHandle, "al_owning_profile_end_v1"))
          Reset = Marshal.GetDelegateForFunctionPointer<OwningProfileReset>(NativeLibrary.GetExport(nativeHandle, "al_owning_profile_reset_v1"))
          Snapshot = Marshal.GetDelegateForFunctionPointer<OwningProfileSnapshot>(NativeLibrary.GetExport(nativeHandle, "al_owning_profile_snapshot_v1")) }

    let private readProfileU32 (bytes: byte array) offset =
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4))

    let private readProfileU64 (bytes: byte array) offset =
        BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8))

    let private readProfileCounter (bytes: byte array) category =
        let offset = 24 + category * 24
        readProfileU64 bytes offset, readProfileU64 bytes (offset + 8), readProfileU64 bytes (offset + 16)

    let private snapshotProfile (api: OwningProfileApi) =
        let sizeBytes = 552
        let buffer = Marshal.AllocHGlobal sizeBytes
        try
            let status = api.Snapshot.Invoke(1u, buffer, uint32 sizeBytes)
            if status <> 0 then failwith $"Detailed native profile snapshot failed with status {status}."
            let bytes = Array.zeroCreate<byte> sizeBytes
            Marshal.Copy(buffer, bytes, 0, sizeBytes)
            bytes
        finally
            Marshal.FreeHGlobal buffer

    let private verifyDetailedProfileAbi (counter: int ref) (api: OwningProfileApi) =
        check counter "detailed profile reset accepts ABI v1" (api.Reset.Invoke(1u) = 0)
        let start = api.Begin.Invoke()
        check counter "detailed profile timer begins successfully" (start <> 0UL && start <> UInt64.MaxValue)
        check counter "detailed profile timer records a runtime category" (api.End.Invoke(0u, start, 0) = 0)
        let initial = snapshotProfile api
        check counter "detailed profile snapshot has the pinned v1 552-byte layout" (initial.Length = 552 && readProfileU32 initial 0 = 1u && readProfileU32 initial 4 = 552u && readProfileU32 initial 8 = 22u && readProfileU32 initial 12 = 0u && readProfileU64 initial 16 > 0UL)
        check counter "detailed profile counter array starts at byte 24" (let calls, _, failures = readProfileCounter initial 0 in calls = 1UL && failures = 0UL)
        let badResetStatus = api.Reset.Invoke(2u)
        let preserved = snapshotProfile api
        let preservedCalls, _, _ = readProfileCounter preserved 0
        check counter "detailed profile reset rejects unsupported ABI without clearing counters" (badResetStatus = 2 && preservedCalls = 1UL)

        let checkRejectedSnapshot label version capacity expectedStatus =
            let sentinel = Array.create 552 0xa5uy
            let buffer = Marshal.AllocHGlobal sentinel.Length
            try
                Marshal.Copy(sentinel, 0, buffer, sentinel.Length)
                let status = api.Snapshot.Invoke(version, buffer, capacity)
                let after = Array.zeroCreate<byte> sentinel.Length
                Marshal.Copy(buffer, after, 0, after.Length)
                check counter ($"{label}: returns the specified ABI status") (status = expectedStatus)
                check counter ($"{label}: leaves caller memory unchanged") (after = sentinel)
            finally
                Marshal.FreeHGlobal buffer

        check counter "detailed profile snapshot rejects a null output pointer" (api.Snapshot.Invoke(1u, 0n, 552u) = 1)
        checkRejectedSnapshot "unsupported profile snapshot version" 2u 552u 2
        checkRejectedSnapshot "truncated profile snapshot buffer" 1u 551u 3
        check counter "detailed profile reset clears counters" (api.Reset.Invoke(1u) = 0)
        let cleared = snapshotProfile api
        check counter "detailed profile reset clears every category without changing snapshot metadata" (
            readProfileU32 cleared 0 = 1u
            && readProfileU32 cleared 4 = 552u
            && readProfileU32 cleared 8 = 22u
            && ([ 0 .. 21 ] |> List.forall (fun category -> let calls, ticks, failures = readProfileCounter cleared category in calls = 0UL && ticks = 0UL && failures = 0UL)))

    let private verifyDetailedProfileThreadIsolation (counter: int ref) (api: OwningProfileApi) =
        check counter "calling-thread profile reset succeeds before isolation check" (api.Reset.Invoke(1u) = 0)
        let start = api.Begin.Invoke()
        check counter "calling-thread profile timer starts before isolation check" (start <> 0UL && start <> UInt64.MaxValue)
        check counter "calling-thread profile counter is seeded before isolation check" (api.End.Invoke(0u, start, 0) = 0)
        let mainThreadId = NativeModuleInterop.getCurrentThreadId()
        let mainBefore = snapshotProfile api
        let worker =
            System.Threading.Tasks.Task.Run(fun () ->
                let workerThreadId = NativeModuleInterop.getCurrentThreadId()
                let resetStatus = api.Reset.Invoke(1u)
                let snapshot = if resetStatus = 0 then snapshotProfile api else Array.empty
                workerThreadId, resetStatus, snapshot)
        let workerThreadId, workerResetStatus, workerSnapshot = worker.GetAwaiter().GetResult()
        check counter "profile isolation probe runs on a distinct native thread" (workerThreadId <> mainThreadId)
        check counter "worker-thread profile reset succeeds" (workerResetStatus = 0)
        check counter "worker-thread reset and snapshot contain only zero counters" (
            workerSnapshot.Length = 552
            && ([ 0 .. 21 ] |> List.forall (fun category -> let calls, ticks, failures = readProfileCounter workerSnapshot category in calls = 0UL && ticks = 0UL && failures = 0UL)))
        let mainAfter = snapshotProfile api
        let mainCalls, _, _ = readProfileCounter mainAfter 0
        check counter "worker-thread reset leaves the calling thread counters intact" (mainCalls = 1UL && mainAfter = mainBefore)
        check counter "calling-thread profile reset succeeds after isolation check" (api.Reset.Invoke(1u) = 0)

    let private verifyDetailedRoomReadCounters (counter: int ref) (api: OwningProfileApi) =
        let bytes = snapshotProfile api
        check counter "room-read detailed profile snapshot retains ABI v1 metadata" (readProfileU32 bytes 0 = 1u && readProfileU32 bytes 4 = 552u && readProfileU32 bytes 8 = 22u && readProfileU64 bytes 16 > 0UL)
        let providerCalls = [ 9 .. 17 ] |> List.map (fun category -> let calls, _, _ = readProfileCounter bytes category in calls)
        check counter "room GET invokes the exact expected provider operations" (providerCalls = [ 1UL; 1UL; 0UL; 1UL; 0UL; 0UL; 1UL; 1UL; 1UL ])
        check counter "room GET provider operations report no failures" ([ 9 .. 17 ] |> List.forall (fun category -> let _, _, failures = readProfileCounter bytes category in failures = 0UL))
        check counter "room GET records runtime work including output commit" (
            ([ 0 .. 8 ] |> List.sumBy (fun category -> let calls, _, _ = readProfileCounter bytes category in calls)) > 0UL
            && (let calls, _, _ = readProfileCounter bytes 6 in calls > 0UL))
        check counter "room GET runtime operations report no failures" ([ 0 .. 8 ] |> List.forall (fun category -> let _, _, failures = readProfileCounter bytes category in failures = 0UL))
        check counter "context preparation is excluded after explicit profile reset" (let calls, _, failures = readProfileCounter bytes 18 in calls = 0UL && failures = 0UL)
        check counter "room GET records SQL binding, stepping, and statement reset" (
            ([ 19 .. 21 ] |> List.forall (fun category -> let calls, _, failures = readProfileCounter bytes category in calls > 0UL && failures = 0UL)))

    let private progress message =
        Console.WriteLine($"[native-room-read] {message}")
        Console.Out.Flush()

    let private valueSql (value: string option) =
        match value with
        | Some text -> box text
        | None -> box DBNull.Value

    let private openDatabase path mode =
        let builder = SqliteConnectionStringBuilder()
        builder.DataSource <- path
        builder.Mode <- mode
        builder.ForeignKeys <- true
        builder.Pooling <- false
        let connection = new SqliteConnection(builder.ToString())
        connection.Open()
        connection

    let private execute (connection: SqliteConnection) sql parameters =
        use command = connection.CreateCommand()
        command.CommandText <- sql
        for name, value in parameters do command.Parameters.AddWithValue(name, value) |> ignore
        command.ExecuteNonQuery()

    let private scalar (connection: SqliteConnection) sql =
        use command = connection.CreateCommand()
        command.CommandText <- sql
        command.ExecuteScalar()

    let private seedFixture (path: string) =
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        use connection = openDatabase path SqliteOpenMode.ReadWriteCreate
        execute connection "PRAGMA foreign_keys = OFF" [] |> ignore
        execute connection
            ("CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL, bio TEXT, role INTEGER NOT NULL, status INTEGER NOT NULL, bot_token TEXT, email_address TEXT, password_digest TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL); " +
             "CREATE UNIQUE INDEX users_bot_token_unique ON users(bot_token); " +
             "CREATE TABLE sessions (id INTEGER PRIMARY KEY, user_id INTEGER NOT NULL, token TEXT NOT NULL UNIQUE, ip_address TEXT, user_agent TEXT, last_active_at TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL); " +
             "CREATE INDEX sessions_token_index ON sessions(token); " +
             "CREATE TABLE rooms (id INTEGER PRIMARY KEY, created_at TEXT NOT NULL, creator_id INTEGER NOT NULL, name TEXT, type TEXT NOT NULL, updated_at TEXT NOT NULL); " +
             "CREATE TABLE memberships (id INTEGER PRIMARY KEY, room_id INTEGER NOT NULL, user_id INTEGER NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL); " +
             "CREATE UNIQUE INDEX memberships_room_user_unique ON memberships(room_id, user_id); " +
             "CREATE TABLE messages (id INTEGER PRIMARY KEY AUTOINCREMENT, client_message_id TEXT NOT NULL, created_at TEXT NOT NULL, creator_id INTEGER NOT NULL, room_id INTEGER NOT NULL, updated_at TEXT NOT NULL); " +
             "CREATE TABLE action_text_rich_texts (id INTEGER PRIMARY KEY AUTOINCREMENT, body TEXT, created_at TEXT NOT NULL, name TEXT NOT NULL, record_id INTEGER NOT NULL, record_type TEXT NOT NULL, updated_at TEXT NOT NULL); " +
             "CREATE UNIQUE INDEX action_text_rich_texts_unique ON action_text_rich_texts(record_type, record_id, name); " +
             "CREATE TRIGGER reject_blocked_refresh BEFORE UPDATE ON sessions WHEN OLD.token = 'blocked-refresh-token' BEGIN SELECT RAISE(ABORT, 'test-only refresh failure'); END")
            []
        |> ignore

        for id, name, role, token in
            [ 7L, "room-reader", 0L, None
              13L, "reader-bot", 2L, Some botToken ] do
            execute connection
                "INSERT INTO users(id, name, role, status, bot_token, created_at, updated_at) VALUES ($id, $name, $role, 0, $token, '2025-01-01 00:00:00', '2025-01-01 00:00:00')"
                [ "$id", box id; "$name", box name; "$role", box role; "$token", valueSql token ]
            |> ignore

        for id, name, kind, creator in
            [ 101L, Some "General", "Rooms::Open", 7L
              102L, Some "Private", "Rooms::Closed", 8L
              103L, Some "Empty — café", "Rooms::Open", 7L
              104L, Some "Unicode — 東京", "Rooms::Open", 7L ] do
            execute connection
                "INSERT INTO rooms(id, created_at, creator_id, name, type, updated_at) VALUES ($id, '2026-01-01 00:00:00', $creator, $name, $kind, '2026-01-01 00:00:00')"
                [ "$id", box id; "$creator", box creator; "$name", valueSql name; "$kind", box kind ]
            |> ignore

        for id, room, user in [ 1L, 101L, 7L; 2L, 102L, 8L; 3L, 103L, 7L; 4L, 104L, 7L ] do
            execute connection
                "INSERT INTO memberships(id, room_id, user_id, created_at, updated_at) VALUES ($id, $room, $user, '2026-01-01 00:00:00', '2026-01-01 00:00:00')"
                [ "$id", box id; "$room", box room; "$user", box user ]
            |> ignore

        for id in 1L .. 47L do
            let createdAt =
                if id >= 46L then "2026-01-01 11:30:00.000000"
                else $"2026-01-01 10:{(int id % 60):D2}:00.000000"
            let body =
                if id = 17L then None
                elif id = 47L then Some "latest body — λ 東京 🚀"
                else Some $"message {id} — café"
            execute connection
                "INSERT INTO messages(id, client_message_id, created_at, creator_id, room_id, updated_at) VALUES ($id, $client, $created, 7, 104, $updated)"
                [ "$id", box id; "$client", box $"client-{id}"; "$created", box createdAt; "$updated", box createdAt ]
            |> ignore
            execute connection
                "INSERT INTO action_text_rich_texts(record_id, record_type, name, body, created_at, updated_at) VALUES ($id, 'Message', 'body', $body, $created, $created)"
                [ "$id", box id; "$body", valueSql body; "$created", box createdAt ]
            |> ignore

    let private configureSession path testCase =
        use connection = openDatabase path SqliteOpenMode.ReadWrite
        execute connection "DELETE FROM sessions" [] |> ignore
        match testCase.SessionToken with
        | None -> ()
        | Some token ->
            execute connection
                "INSERT INTO sessions(id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) VALUES (70, $user, $token, '198.51.100.9', 'stored-agent', $last, '2025-01-01 00:00:00', $last)"
                [ "$user", box testCase.SessionUserId; "$token", box token; "$last", box testCase.LastActiveAt ]
            |> ignore

    let private unixMicroseconds (instant: DateTimeOffset) =
        (instant.ToUniversalTime().Ticks - DateTimeOffset.UnixEpoch.Ticks) / 10L

    let private optionString value = OptionValue(TString, Option.map StringValue value)
    let private optionNamed name value = OptionValue(TNamed name, value)

    let private makeRequest testCase =
        RecordValue(
            "CampfireHttpRequest",
            Map.ofList
                [ "route", EnumValue("HttpRoute", "room-show")
                  "method", EnumValue("RequestMethod", "get")
                  "room-id", NamedValue("RoomId", IntValue testCase.RoomId)
                  "now", NamedValue("UtcInstant", IntValue(unixMicroseconds testCase.RequestNow))
                  "remote-ip", NamedValue("RemoteIp", StringValue "198.51.100.9")
                  "user-agent", optionString (Some "NativeRoomReadTests/1.0")
                  "base-url", StringValue "https://campfire.example.test"
                  "request-is-ssl", BoolValue true
                  "force-ssl", BoolValue false
                  "origin", optionString None
                  "fetch-site", optionString None
                  "session-token", optionString testCase.Cookie
                  "bot-key", optionNamed "BotKey" (Option.map (fun value -> NamedValue("BotKey", StringValue value)) testCase.BotKey)
                  "message-form", ResultValue(TOption(TNamed "HttpMessageInput"), TNamed "HttpFormError", Ok(optionNamed "HttpMessageInput" None))
                  "login-form", ResultValue(TOption(TNamed "HttpLoginInput"), TNamed "HttpFormError", Ok(optionNamed "HttpLoginInput" None))
                  "logout-form", ResultValue(TOption(TNamed "HttpLogoutInput"), TNamed "HttpFormError", Ok(optionNamed "HttpLogoutInput" None))
                  "accepts-turbo", BoolValue false
                  "accepts-html", BoolValue true
                  "last-room", optionString (Some "101")
                  "browser", RecordValue("BrowserInfo", Map.ofList
                      [ "family", EnumValue("BrowserFamily", testCase.BrowserFamily)
                        "version", optionString testCase.BrowserVersion
                        "is-bot", BoolValue false ]) ])

    let private withRequestMethod request methodName =
        match request with
        | RecordValue("CampfireHttpRequest", fields) ->
            RecordValue("CampfireHttpRequest", fields.Add("method", EnumValue("RequestMethod", methodName)))
        | _ -> failwith "room-read request had the wrong record type"

    let private createCookieService () =
        match CookieService.Create secret with
        | Ok cookies -> cookies
        | Error _ -> failwith "synthetic cookie service creation failed"

    let private signPayload (secretText: string) (payload: byte array) =
        let signingKey =
            Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes secretText,
                Encoding.UTF8.GetBytes "signed cookie",
                1000,
                HashAlgorithmName.SHA256,
                64)
        try
            let encoded = Convert.ToBase64String payload
            let mac = HMACSHA1.HashData(signingKey, Encoding.ASCII.GetBytes encoded)
            encoded + "--" + Convert.ToHexString(mac).ToLowerInvariant()
        finally
            CryptographicOperations.ZeroMemory(signingKey.AsSpan())

    let private signedDirectJson token =
        let payload = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize token)
        signPayload secret payload

    let private signedWrongPurpose token =
        let message = Convert.ToBase64String(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize token))
        let envelope =
            "{\"_rails\":{\"message\":\"" + message
            + "\",\"exp\":\"2046-01-01T00:00:00.000Z\",\"pur\":\"cookie.other\"}}"
        signPayload secret (Encoding.UTF8.GetBytes envelope)

    let private signedPurposeFallback token =
        let message = Convert.ToBase64String(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize token))
        let envelope = "{\"_rails\":{\"message\":\"" + message + "\",\"exp\":\"2046-01-01T00:00:00.000Z\"}}"
        signPayload secret (Encoding.UTF8.GetBytes envelope)

    let private cookieServiceCookie (cookies: CookieService) token instant =
        match cookies.SignSessionToken(token, instant) with
        | Ok value -> value
        | Error _ -> failwith "synthetic signed-cookie fixture creation failed"

    let private makeEnvironment databasePath (cookies: CookieService) : HostOperationEnvironment =
        let clock = FixedClock(fixedNow) :> IClock
        let campfire =
            CampfireHost.createProviderWithGuidSource databasePath clock (fun () -> Guid.Parse "11111111-1111-1111-1111-111111111111") secret
            |> function Ok provider -> provider | Error _ -> failwith "Campfire fixture database did not open"
        let authentication =
            CampfireAuthenticationHost.createProvider databasePath
            |> function Ok provider -> provider | Error _ -> failwith "authentication fixture database did not open"
        let parts =
            [ campfire
              authentication
              CookieHost.createProvider cookies
              BrowserHost.createProvider()
              LoginHost.createProvider() ]
        { Manifest = HostComposition.manifest [ CampfireHost.manifest; CampfireAuthenticationHost.manifest; CookieHost.manifest; BrowserHost.manifest; LoginHost.manifest ]
          ProductionProvider = HostComposition.provider parts
          CreateTestProvider = fun () -> Error HostOperationFailure.TestProviderUnavailable }

    let private invokeReference (entry: Runtime.FlowEntryPoint) request =
        match entry.Invoke [ request ] with
        | Ok result when result.Console.IsEmpty ->
            match result.Values with
            | [ value ] -> value
            | _ -> failwith "interpreter room-read entry returned an invalid value count"
        | Ok _ -> failwith "interpreter room-read entry wrote console output"
        | Error _ -> failwith "interpreter room-read entry failed"

    let private outputFields = function
        | RecordValue("CampfireHttpResponse", fields) -> fields
        | _ -> failwith "room-read returned a value with the wrong type"

    let private align8 size = (size + 7) &&& ~~~7

    let private writeU32 (bytes: byte array) offset value =
        let encoded = BitConverter.GetBytes(uint32 value)
        if not BitConverter.IsLittleEndian then Array.Reverse encoded
        Array.Copy(encoded, 0, bytes, offset, 4)

    let private writeI64 (bytes: byte array) offset value =
        let encoded = BitConverter.GetBytes(int64 value)
        if not BitConverter.IsLittleEndian then Array.Reverse encoded
        Array.Copy(encoded, 0, bytes, offset, 8)

    let private encodeValue (program: IrProgram) (layouts: OwningStackTypeLayout list) ty value =
        let layoutFor itemType =
            layouts
            |> List.tryFind (fun layout -> layout.Type = itemType)
            |> Option.defaultWith (fun () -> failwith "required value layout is absent")
        let enumCaseTag typeName caseName =
            let cases =
                program.NominalTypesByKey
                |> Map.toSeq
                |> Seq.tryPick (fun (_, definition) ->
                    match definition with
                    | IrEnumDefinition enumDefinition when enumDefinition.TypeName = typeName -> Some enumDefinition.Cases
                    | _ -> None)
                |> Option.defaultWith (fun () -> failwith $"enum definition is absent for {typeName}")
            cases
            |> List.tryFindIndex ((=) caseName)
            |> Option.defaultWith (fun () -> failwith $"enum case is absent for {typeName}.{caseName}")
        let rec encode itemType item =
            match itemType, item with
            | IrInt, IntValue number -> let bytes = Array.zeroCreate 8 in writeI64 bytes 0 number; bytes
            | IrBool, BoolValue flag -> let bytes = Array.zeroCreate 8 in writeI64 bytes 0 (if flag then 1L else 0L); bytes
            | IrUnit, UnitValue -> Array.zeroCreate 8
            | IrFloat, FloatValue number ->
                let bytes = Array.zeroCreate 8
                let valueBytes = BitConverter.GetBytes number
                if not BitConverter.IsLittleEndian then Array.Reverse valueBytes
                Array.Copy(valueBytes, bytes, 8)
                bytes
            | IrString, StringValue text -> encodeString text
            | IrList childType, ListValue(_, items) ->
                let encodedItems = items |> List.map (encode childType)
                let bytes = Array.zeroCreate (8 + (encodedItems |> List.sumBy (fun value -> value.Length)))
                writeU32 bytes 0 items.Length
                let mutable cursor = 8
                for child in encodedItems do Array.Copy(child, 0, bytes, cursor, child.Length); cursor <- cursor + child.Length
                bytes
            | IrOption childType, OptionValue(_, option) ->
                match option with
                | None -> let bytes = Array.zeroCreate 8 in writeI64 bytes 0 1L; bytes
                | Some child -> let encoded = encode childType child in let bytes = Array.zeroCreate(8 + encoded.Length) in Array.Copy(encoded, 0, bytes, 8, encoded.Length); bytes
            | IrResult(okType, errorType), ResultValue(_, _, result) ->
                match result with
                | Ok child -> let encoded = encode okType child in let bytes = Array.zeroCreate(8 + encoded.Length) in Array.Copy(encoded, 0, bytes, 8, encoded.Length); bytes
                | Error child -> let encoded = encode errorType child in let bytes = Array.zeroCreate(8 + encoded.Length) in writeI64 bytes 0 1L; Array.Copy(encoded, 0, bytes, 8, encoded.Length); bytes
            | IrNominal _, NamedValue(_, nested) -> encode (match nested with IntValue _ -> IrInt | BoolValue _ -> IrBool | FloatValue _ -> IrFloat | StringValue _ -> IrString | _ -> failwith "unsupported scalar input") nested
            | IrNominal _, EnumValue(typeName, caseName) ->
                let itemLayout = layoutFor itemType
                if itemLayout.TypeName <> typeName then failwith "enum input type differs from its layout"
                let tag = enumCaseTag typeName caseName
                let bytes = Array.zeroCreate 8
                writeI64 bytes 0 (int64 tag)
                bytes
            | IrNominal _, RecordValue(typeName, fields) ->
                let itemLayout = layoutFor itemType
                if itemLayout.TypeName <> typeName then failwith "record input type differs from its layout"
                let encodedFields =
                    itemLayout.Fields
                    |> List.map (fun field ->
                        match Map.tryFind field.FieldName fields with
                        | Some fieldValue -> encode field.FieldType fieldValue
                        | None -> failwith "record input omitted a field")
                let bytes = Array.zeroCreate(encodedFields |> List.sumBy (fun child -> child.Length))
                let mutable cursor = 0
                for child in encodedFields do Array.Copy(child, 0, bytes, cursor, child.Length); cursor <- cursor + child.Length
                bytes
            | _ -> failwith $"unsupported input encoding for {itemType}"
        and encodeString text =
            let data = Encoding.UTF8.GetBytes text
            let bytes = Array.zeroCreate(align8 (8 + data.Length))
            writeU32 bytes 0 data.Length
            Array.Copy(data, 0, bytes, 8, data.Length)
            bytes
        encode ty value

    let private cArray name (bytes: byte array) =
        let values = bytes |> Array.map (fun value -> "0x" + value.ToString("x2")) |> String.concat ","
        $"static const uint8_t {name}[] = {{{values}}};\n"

    let private writeSmokeFixture path requestBytes expectedBytes secretBytes =
        let contents =
            "#ifndef CAMPFIRE_ROOM_READ_TEST_FIXTURE_H\n#define CAMPFIRE_ROOM_READ_TEST_FIXTURE_H\n#include <stdint.h>\n"
            + cArray "room_read_input" requestBytes
            + cArray "room_read_expected" expectedBytes
            + cArray "room_read_secret" secretBytes
            + $"#define ROOM_READ_INPUT_BYTES {requestBytes.Length}u\n#define ROOM_READ_NOW_OFFSET_BYTES 24u\n#define ROOM_READ_EXPECTED_BYTES {expectedBytes.Length}u\n#define ROOM_READ_SECRET_BYTES {secretBytes.Length}u\n#endif\n"
        File.WriteAllText(path, contents, UTF8Encoding(false))

    let private runChild (executable: string) (arguments: string list) (workingDirectory: string) =
        let start = ProcessStartInfo(executable)
        start.WorkingDirectory <- workingDirectory
        start.UseShellExecute <- false
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true
        for argument in arguments do start.ArgumentList.Add argument
        use child = Process.Start(start)
        let stdout = child.StandardOutput.ReadToEndAsync()
        let stderr = child.StandardError.ReadToEndAsync()
        child.WaitForExit()
        let output = stdout.Result + stderr.Result
        File.WriteAllText(Path.Combine(workingDirectory, "tool-output.log"), output)
        if child.ExitCode <> 0 then failwith "a native tool returned a nonzero status; inspect its private tool-output.log"

    let private findSdkLibrary (name: string) =
        let sdkRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "Lib")
        Directory.EnumerateDirectories(sdkRoot)
        |> Seq.sortByDescending Path.GetFileName
        |> Seq.map (fun version -> Path.Combine(version, "um", "x64", name))
        |> Seq.tryFind File.Exists
        |> Option.defaultWith (fun () -> failwith $"Windows SDK x64 {name} is required")

    let private findMsvcRuntimeLibrary (toolchain: LlvmToolchain) (name: string) =
        let compilerRuntime =
            toolchain.CompilerRuntimeLibrary
            |> Option.defaultWith (fun () -> failwith "an MSVC compiler runtime library is required")
        let library = Path.Combine(Path.GetDirectoryName compilerRuntime, name)
        if not (File.Exists library) then failwith $"MSVC x64 {name} is required"
        library

    let private nativeDependencies (toolchain: LlvmToolchain) (repo: string) (runRoot: string) =
        let nativeSource = Path.Combine(repo, "applications", "campfire", "Native")
        let directory = Path.Combine(runRoot, "test-native-dependencies")
        Directory.CreateDirectory directory |> ignore
        let importLibrary = Path.Combine(directory, "e_sqlite3.lib")
        runChild
            (Path.Combine(Path.GetDirectoryName toolchain.ClangPath, "llvm-lib.exe"))
            [ "/def:" + Path.Combine(nativeSource, "room_read_sqlite.def")
              "/machine:x64"
              "/out:" + importLibrary ] directory
        let packageRoot =
            match Environment.GetEnvironmentVariable("NUGET_PACKAGES") with
            | null | "" -> Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages")
            | value -> value
        let sqliteDll = Path.Combine(packageRoot, "sqlitepclraw.lib.e_sqlite3", "2.1.12", "runtimes", "win-x64", "native", "e_sqlite3.dll")
        if not (File.Exists sqliteDll) then failwith "the pinned e_sqlite3.dll package is unavailable"
        let sqliteHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes sqliteDll)).ToLowerInvariant()
        if sqliteHash <> "b7385d722c83fb52142a00477a726723745916d22a555711ee89834c1111fb2e" then
            failwith "e_sqlite3.dll differs from the repository's audited package bytes"
        { HeaderPaths =
            [ Path.Combine(nativeSource, "room_read_adapter.h")
              Path.Combine(nativeSource, "room_read_pure.h")
              Path.Combine(nativeSource, "room_read_sqlite_subset.h") ]
          LinkLibraryPaths =
            [ importLibrary
              findSdkLibrary "bcrypt.lib"
              findSdkLibrary "kernel32.lib"
              findMsvcRuntimeLibrary toolchain "libvcruntime.lib" ]
          RuntimeLibraryPaths = [ sqliteDll ] }

    let private buildAdapterAggregator (repo: string) (runRoot: string) =
        let nativeSource = Path.Combine(repo, "applications", "campfire", "Native")
        let aggregator = Path.Combine(runRoot, "test-room-read-adapter-aggregator.c")
        let adapterSource = File.ReadAllText(Path.Combine(nativeSource, "room_read_adapter.c"))
        let pureSource = File.ReadAllText(Path.Combine(nativeSource, "room_read_pure.c"))
        File.WriteAllText(
            aggregator,
            "#line 1 \"room_read_adapter.c\"\n" + adapterSource + "\n"
            + "#line 1 \"room_read_pure.c\"\n" + pureSource + "\n",
            UTF8Encoding(false))
        aggregator

    let private compileSmoke (toolchain: LlvmToolchain) (optimization: LlvmOptimization) (executionProfile: OwningRuntimeInstrumentation) (source: string) (headerDirectory: string) (kernelLibrary: string) (shellLibrary: string) (runtimeLibrary: string option) (outputDirectory: string) =
        Directory.CreateDirectory outputDirectory |> ignore
        let objectPath = Path.Combine(outputDirectory, "room_read_standalone_smoke.obj")
        let executable = Path.Combine(outputDirectory, "room_read_standalone_smoke.exe")
        let profileDefine =
            match executionProfile with
            | OwningRuntimeInstrumentation.Off -> []
            | OwningRuntimeInstrumentation.Detailed -> [ "-DAL_OWNING_DETAILED_PROFILE=1" ]
        runChild toolchain.ClangPath
            ([ "--target=x86_64-pc-windows-msvc"; "-std=c11"; "-Wall"; "-Wextra"; "-Werror"; "-ffreestanding"; "-fno-builtin"
               (match optimization with LlvmOptimization.O0 -> "-O0" | LlvmOptimization.O2 -> "-O2") ]
             @ profileDefine
             @ [ "-c"; source; "-I"; headerDirectory; "-I"; outputDirectory; "-o"; objectPath ]) outputDirectory
        runChild toolchain.LldLinkPath
            ([ "/entry:room_read_smoke_start"; "/subsystem:console"; "/nodefaultlib"; "/machine:x64"; "/out:" + executable; objectPath; kernelLibrary; shellLibrary ]
             @ (runtimeLibrary |> Option.toList)) outputDirectory
        executable

    let private createNativeContext (nativeHandle: nativeint) (databasePath: string) =
        let pureSize = Marshal.GetDelegateForFunctionPointer<PureContextSize>(NativeLibrary.GetExport(nativeHandle, "al_campfire_pure_context_size_v1"))
        let pureAlignment = Marshal.GetDelegateForFunctionPointer<PureContextAlignment>(NativeLibrary.GetExport(nativeHandle, "al_campfire_pure_context_alignment_v1"))
        let pureInit = Marshal.GetDelegateForFunctionPointer<PureContextInit>(NativeLibrary.GetExport(nativeHandle, "al_campfire_pure_context_init_v1"))
        let pureDestroy = Marshal.GetDelegateForFunctionPointer<PureContextDestroy>(NativeLibrary.GetExport(nativeHandle, "al_campfire_pure_context_destroy_v1"))
        let provisionWal = Marshal.GetDelegateForFunctionPointer<RoomReadProvisionWal>(NativeLibrary.GetExport(nativeHandle, "al_campfire_room_read_provision_wal_v1"))
        let contextInit = Marshal.GetDelegateForFunctionPointer<RoomReadContextInit>(NativeLibrary.GetExport(nativeHandle, "al_campfire_room_read_context_init_v1"))
        let contextDestroy = Marshal.GetDelegateForFunctionPointer<RoomReadContextDestroy>(NativeLibrary.GetExport(nativeHandle, "al_campfire_room_read_context_destroy_v1"))
        let storageBytes = int (pureSize.Invoke())
        let alignment = int (pureAlignment.Invoke())
        if storageBytes <= 0 || alignment <= 0 || (alignment &&& (alignment - 1)) <> 0 then
            failwith "native pure context returned an invalid storage contract"
        let pureRaw = Marshal.AllocHGlobal(storageBytes + alignment - 1)
        let pureAddress = (pureRaw.ToInt64() + int64 alignment - 1L) &&& ~~~(int64 alignment - 1L)
        let pureStorage = nativeint pureAddress
        let secretBytes = Encoding.UTF8.GetBytes secret
        let secretPointer = Marshal.AllocHGlobal secretBytes.Length
        let adapterStorage = Marshal.AllocHGlobal(Marshal.SizeOf<RoomReadContextAbi>())
        let pathBytes = Encoding.UTF8.GetBytes(Path.GetFullPath databasePath)
        let pathPointer = Marshal.AllocHGlobal(pathBytes.Length + 1)
        let errorTag = Marshal.AllocHGlobal(sizeof<uint32>)
        Marshal.Copy(secretBytes, 0, secretPointer, secretBytes.Length)
        Marshal.Copy(pathBytes, 0, pathPointer, pathBytes.Length)
        Marshal.WriteByte(pathPointer, pathBytes.Length, 0uy)
        Marshal.WriteByte(errorTag, 0, 0xffuy)
        let mutable pureInitialized = false
        let mutable adapterInitialized = false
        try
            Marshal.Copy(Array.zeroCreate<byte> storageBytes, 0, pureStorage, storageBytes)
            Marshal.Copy(Array.zeroCreate<byte> (Marshal.SizeOf<RoomReadContextAbi>()), 0, adapterStorage, Marshal.SizeOf<RoomReadContextAbi>())
            let pureStatus = pureInit.Invoke(pureStorage, uint32 storageBytes, secretPointer, uint32 secretBytes.Length)
            if pureStatus <> 0 then failwith "native pure context initialization failed"
            pureInitialized <- true
            let provisionStatus = provisionWal.Invoke(pathPointer, uint32 pathBytes.Length, errorTag)
            if provisionStatus <> 0 || Marshal.ReadInt32 errorTag <> 0 then failwith "native room-read WAL provisioning failed"
            let adapterStatus = contextInit.Invoke(adapterStorage, pathPointer, uint32 pathBytes.Length, pureStorage, errorTag)
            if adapterStatus <> 0 || Marshal.ReadInt32 errorTag <> 0 then failwith "native room-read SQLite context initialization failed"
            adapterInitialized <- true
            let release () =
                if adapterInitialized then contextDestroy.Invoke(adapterStorage) |> ignore
                if pureInitialized then pureDestroy.Invoke pureStorage
                Marshal.FreeHGlobal errorTag
                Marshal.FreeHGlobal pathPointer
                Marshal.FreeHGlobal adapterStorage
                Marshal.FreeHGlobal secretPointer
                Marshal.FreeHGlobal pureRaw
            adapterStorage, release
        with _ ->
            if adapterInitialized then contextDestroy.Invoke(adapterStorage) |> ignore
            if pureInitialized then pureDestroy.Invoke pureStorage
            Marshal.FreeHGlobal errorTag
            Marshal.FreeHGlobal pathPointer
            Marshal.FreeHGlobal adapterStorage
            Marshal.FreeHGlobal secretPointer
            Marshal.FreeHGlobal pureRaw
            reraise()

    let private verifyEightByteBrowserDispatch (counter: int ref) nativeHandle providerContext =
        let dispatch = Marshal.GetDelegateForFunctionPointer<RoomReadDispatch>(NativeLibrary.GetExport(nativeHandle, "al_host_campfire_room_read_v1"))
        let descriptorSize = 36
        let descriptors = Marshal.AllocHGlobal(descriptorSize * 2)
        let leftBytes: byte array = Encoding.UTF8.GetBytes("120.0.0")
        let rightBytes: byte array = Encoding.UTF8.GetBytes("120.0.0")
        let encodeString (data: byte array) (codeUnits: int) =
            let extent = (8 + data.Length + 7) &&& ~~~7
            let bytes = Array.zeroCreate<byte> extent
            writeU32 bytes 0 codeUnits
            Array.Copy(data, 0, bytes, 8, data.Length)
            bytes
        let left = encodeString leftBytes 7
        let right = encodeString rightBytes 7
        let leftStorage = Marshal.AllocHGlobal left.Length
        let rightStorage = Marshal.AllocHGlobal right.Length
        let inputStorage = Marshal.AllocHGlobal(Marshal.SizeOf<RoomReadInputAbi>() * 2)
        let outputStorage = Marshal.AllocHGlobal 8
        let extentStorage = Marshal.AllocHGlobal sizeof<uint32>
        let layoutStorage = Marshal.AllocHGlobal(Marshal.SizeOf<RoomReadLayoutAbi>())
        try
            let writeDescriptor index kind typeId fixedPayload fixedExtent minimumPayload minimumExtent =
                let offset = index * descriptorSize
                Marshal.WriteInt32(descriptors, offset, kind)
                Marshal.WriteInt32(descriptors, offset + 4, typeId)
                Marshal.WriteInt32(descriptors, offset + 8, 0)
                Marshal.WriteInt32(descriptors, offset + 12, 0)
                Marshal.WriteInt32(descriptors, offset + 16, fixedPayload)
                Marshal.WriteInt32(descriptors, offset + 20, fixedExtent)
                Marshal.WriteInt32(descriptors, offset + 24, minimumPayload)
                Marshal.WriteInt32(descriptors, offset + 28, minimumExtent)
                Marshal.WriteInt32(descriptors, offset + 32, 0)
            // ABI5 descriptor indexes: String at 0, Bool at 1. The dispatcher
            // needs no fields for these two scalar kinds.
            writeDescriptor 0 5 1 -1 -1 8 8
            writeDescriptor 1 2 2 8 8 8 8
            Marshal.Copy(left, 0, leftStorage, left.Length)
            Marshal.Copy(right, 0, rightStorage, right.Length)
            Marshal.WriteIntPtr(inputStorage, 0, leftStorage)
            Marshal.WriteInt32(inputStorage, 8, left.Length)
            Marshal.WriteInt32(inputStorage, 12, 0)
            Marshal.WriteIntPtr(inputStorage, 16, rightStorage)
            Marshal.WriteInt32(inputStorage, 24, right.Length)
            Marshal.WriteInt32(inputStorage, 28, 0)
            let mutable layout = RoomReadLayoutAbi()
            layout.AbiVersion <- 5u
            layout.Types <- descriptors
            layout.TypeCount <- 2u
            layout.Fields <- 0n
            layout.FieldCount <- 0u
            Marshal.StructureToPtr(layout, layoutStorage, false)
            Marshal.WriteByte(outputStorage, 0, 0xa5uy)
            Marshal.WriteInt32(extentStorage, 0, -1)
            let status = dispatch.Invoke(providerContext, 6u, layoutStorage, inputStorage, 2u, 1u, outputStorage, 8u, extentStorage)
            let extent = Marshal.ReadInt32 extentStorage
            let result = Marshal.ReadInt64 outputStorage
            check counter "direct browser dispatch accepts the exact 8-byte Bool capacity" (status = 0 && extent = 8 && result = 1L)
        finally
            Marshal.FreeHGlobal layoutStorage
            Marshal.FreeHGlobal extentStorage
            Marshal.FreeHGlobal outputStorage
            Marshal.FreeHGlobal inputStorage
            Marshal.FreeHGlobal rightStorage
            Marshal.FreeHGlobal leftStorage
            Marshal.FreeHGlobal descriptors

    let private createCases cookies =
        let validCookie = cookieServiceCookie cookies sessionToken fixedNow
        let blockedRefreshCookie = cookieServiceCookie cookies "blocked-refresh-token" fixedNow
        let missingSessionCookie = cookieServiceCookie cookies "no-such-session-token" fixedNow
        let wrongPurpose = signedWrongPurpose sessionToken
        let purposeFallback = signedPurposeFallback sessionToken
        let directJsonFallback = signedDirectJson sessionToken
        let expiredNow = fixedNow.AddYears(21)
        let common testName =
            { Name = testName
              Cookie = Some validCookie
              BotKey = None
              BrowserFamily = "other"
              BrowserVersion = None
              RequestNow = fixedNow
              SessionToken = Some sessionToken
              SessionUserId = 7L
              LastActiveAt = "2026-01-01 11:30:00.000000"
              RoomId = 101L
              ExpectedStatus = 200L
              ExpectedView = "room"
              ExpectedRefresh = false
              ExpectedMessageCount = Some 0 }
        [ { common "missing-cookie" with Cookie = None; SessionToken = None; ExpectedStatus = 302L; ExpectedView = "redirect-session"; ExpectedMessageCount = None }
          { common "malformed-cookie" with Cookie = Some "invalid-signed-cookie"; SessionToken = None; ExpectedStatus = 302L; ExpectedView = "redirect-session"; ExpectedMessageCount = None }
          { common "expired-cookie" with RequestNow = expiredNow; SessionToken = None; ExpectedStatus = 302L; ExpectedView = "redirect-session"; ExpectedMessageCount = None }
          { common "wrong-purpose-cookie" with Cookie = Some wrongPurpose; SessionToken = None; ExpectedStatus = 302L; ExpectedView = "redirect-session"; ExpectedMessageCount = None }
          { common "legacy-cookie-purpose-metadata-fallback" with Cookie = Some purposeFallback }
          { common "direct-json-cookie-fallback" with Cookie = Some directJsonFallback }
          { common "valid-cookie-missing-session" with Cookie = Some missingSessionCookie; SessionToken = None; ExpectedStatus = 302L; ExpectedView = "redirect-session"; ExpectedMessageCount = None }
          { common "session-lookup-error" with LastActiveAt = "not-a-timestamp"; ExpectedStatus = 500L; ExpectedView = "empty"; ExpectedMessageCount = None }
          { common "refresh-exact-cutoff-is-not-due" with LastActiveAt = "2026-01-01 11:00:00.000000" }
          { common "refresh-one-microsecond-before-cutoff" with LastActiveAt = "2026-01-01 10:59:59.999999"; ExpectedRefresh = true }
          { common "refresh-error" with Cookie = Some blockedRefreshCookie; LastActiveAt = "2026-01-01 10:00:00.000000"; SessionToken = Some "blocked-refresh-token"; ExpectedStatus = 500L; ExpectedView = "empty"; ExpectedMessageCount = None }
          { common "refresh-before-missing-user" with LastActiveAt = "2026-01-01 10:00:00.000000"; SessionUserId = 99L; ExpectedStatus = 500L; ExpectedView = "empty"; ExpectedRefresh = true; ExpectedMessageCount = None }
          { common "bot-key-fallback-authorized-bot" with Cookie = None; SessionToken = None; BotKey = Some($"13-{botToken}"); ExpectedStatus = 403L; ExpectedView = "empty"; ExpectedMessageCount = None }
          { common "bot-key-fallback-unknown-key" with Cookie = None; SessionToken = None; BotKey = Some "unknown-bot-key"; ExpectedStatus = 302L; ExpectedView = "redirect-session"; ExpectedMessageCount = None }
          { common "old-browser-gate" with BrowserFamily = "chrome"; BrowserVersion = Some "119.0.0.0"; ExpectedView = "incompatible-browser"; ExpectedMessageCount = None }
          { common "minimum-browser-gate" with BrowserFamily = "chrome"; BrowserVersion = Some "120.0.0.0" }
          { common "missing-room" with RoomId = 999L; ExpectedStatus = 302L; ExpectedView = "redirect-root"; ExpectedMessageCount = None }
          { common "room-not-member" with RoomId = 102L; ExpectedStatus = 302L; ExpectedView = "redirect-root"; ExpectedMessageCount = None }
          { common "empty-message-list" with RoomId = 103L; ExpectedMessageCount = Some 0 }
          { common "unicode-forty-message-list-with-tied-latest-time" with RoomId = 104L; ExpectedMessageCount = Some 40 } ]

    let private findImportedFunctionIatRva (nativeLibraryPath: string) (importedModule: string) (functionName: string) =
        use stream = File.OpenRead nativeLibraryPath
        use reader = new PEReader(stream)
        let peHeader = reader.PEHeaders.PEHeader
        if isNull peHeader then failwith "native host has no PE header"
        let importDirectory = peHeader.ImportTableDirectory
        if importDirectory.RelativeVirtualAddress = 0 || importDirectory.Size < 20 then
            failwith "native host has no import directory"
        let importBytes = reader.GetSectionData(importDirectory.RelativeVirtualAddress).GetContent() |> Seq.toArray
        let readU32 (bytes: byte array) offset =
            if offset < 0 || offset > bytes.Length - 4 then failwith "native import metadata is truncated"
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4))
        let readU64 (bytes: byte array) offset =
            if offset < 0 || offset > bytes.Length - 8 then failwith "native import thunk metadata is truncated"
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8))
        let readCStringAtRva (rva: int) =
            let bytes = reader.GetSectionData(rva).GetContent() |> Seq.toArray
            let endIndex = bytes |> Array.tryFindIndex ((=) 0uy) |> Option.defaultWith (fun () -> failwith "native import string is unterminated")
            Encoding.ASCII.GetString(bytes, 0, endIndex)
        let descriptorCount = min (importDirectory.Size / 20) (importBytes.Length / 20)
        let mutable found = None
        let mutable descriptorIndex = 0
        while descriptorIndex < descriptorCount && found.IsNone do
            let offset = descriptorIndex * 20
            let originalFirstThunk = readU32 importBytes offset
            let nameRva = readU32 importBytes (offset + 12)
            let firstThunk = readU32 importBytes (offset + 16)
            if originalFirstThunk = 0u && nameRva = 0u && firstThunk = 0u then
                descriptorIndex <- descriptorCount
            else
                let name = readCStringAtRva (int nameRva)
                if String.Equals(name, importedModule, StringComparison.OrdinalIgnoreCase) then
                    let lookupRva = if originalFirstThunk = 0u then firstThunk else originalFirstThunk
                    let thunkBytes = reader.GetSectionData(int lookupRva).GetContent() |> Seq.toArray
                    let mutable thunkIndex = 0
                    let mutable searching = true
                    while searching do
                        let thunkOffset = thunkIndex * 8
                        let thunk = readU64 thunkBytes thunkOffset
                        if thunk = 0UL then
                            searching <- false
                        elif (thunk &&& 0x8000000000000000UL) = 0UL then
                            let importNameRva = int (uint32 thunk)
                            let importedName = readCStringAtRva (importNameRva + 2)
                            if String.Equals(importedName, functionName, StringComparison.Ordinal) then
                                found <- Some(int firstThunk + thunkIndex * 8)
                                searching <- false
                            else
                                thunkIndex <- thunkIndex + 1
                        else
                            thunkIndex <- thunkIndex + 1
                descriptorIndex <- descriptorIndex + 1
        found |> Option.defaultWith (fun () -> failwith $"native host does not import {functionName} from {importedModule}")

    let private readNativeSqliteSettings (nativeLibraryHandle: nativeint) (nativeLibraryPath: string) (nativeContext: nativeint) : NativeSqliteSettings =
        let iatRva = findImportedFunctionIatRva nativeLibraryPath "e_sqlite3.dll" "sqlite3_open_v2"
        let sqliteOpenAddress = Marshal.ReadIntPtr(nativeLibraryHandle, iatRva)
        if sqliteOpenAddress = 0n then failwith "native host SQLite import has a null address"
        let mutable handle = 0n
        if not (NativeModuleInterop.getModuleHandleEx(0x00000006u, sqliteOpenAddress, &handle)) || handle = 0n then
            let error = Marshal.GetLastWin32Error()
            failwith $"the module owning the native SQLite import could not be resolved ({error})"
        let modulePath = StringBuilder(32768)
        let pathLength = NativeModuleInterop.getModuleFileName(handle, modulePath, uint32 modulePath.Capacity)
        if pathLength = 0u || pathLength >= uint32 modulePath.Capacity then
            let error = Marshal.GetLastWin32Error()
            failwith $"the active SQLite module path could not be resolved ({error})"
        let moduleHash = SHA256.HashData(File.ReadAllBytes(modulePath.ToString())) |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
        progress $"native settings use imported SQLite module {modulePath} sha256={moduleHash}"
        let prepare = Marshal.GetDelegateForFunctionPointer<SqlitePrepare>(NativeLibrary.GetExport(handle, "sqlite3_prepare_v2"))
        let step = Marshal.GetDelegateForFunctionPointer<SqliteStep>(NativeLibrary.GetExport(handle, "sqlite3_step"))
        let finalize = Marshal.GetDelegateForFunctionPointer<SqliteFinalize>(NativeLibrary.GetExport(handle, "sqlite3_finalize"))
        let columnInt64 = Marshal.GetDelegateForFunctionPointer<SqliteColumnInt64>(NativeLibrary.GetExport(handle, "sqlite3_column_int64"))
        let columnText16 = Marshal.GetDelegateForFunctionPointer<SqliteColumnText16>(NativeLibrary.GetExport(handle, "sqlite3_column_text16"))
        let columnBytes16 = Marshal.GetDelegateForFunctionPointer<SqliteColumnBytes16>(NativeLibrary.GetExport(handle, "sqlite3_column_bytes16"))
        let getAutocommit = Marshal.GetDelegateForFunctionPointer<SqliteGetAutocommit>(NativeLibrary.GetExport(handle, "sqlite3_get_autocommit"))
        let database = Marshal.ReadIntPtr(nativeContext, 0)
        if database = 0n then failwith "native room-read context has a null SQLite handle"
        let run (sql: string) (readColumn: nativeint -> 'T) : 'T =
            let sqlBytes = Marshal.StringToHGlobalAnsi sql
            let statementStorage = Marshal.AllocHGlobal IntPtr.Size
            try
                Marshal.WriteIntPtr(statementStorage, 0n)
                let prepareStatus = prepare.Invoke(database, sqlBytes, -1, statementStorage, 0n)
                if prepareStatus <> 0 then failwith $"native SQLite settings query did not prepare ({prepareStatus})"
                let statement = Marshal.ReadIntPtr statementStorage
                if statement = 0n then failwith "native SQLite settings query returned no statement"
                try
                    let stepStatus = step.Invoke statement
                    if stepStatus <> 100 then failwith $"native SQLite settings query returned no row ({stepStatus})"
                    readColumn statement
                finally
                    let finalizeStatus = finalize.Invoke statement
                    if finalizeStatus <> 0 then failwith $"native SQLite settings query did not finalize cleanly ({finalizeStatus})"
            finally
                Marshal.FreeHGlobal statementStorage
                Marshal.FreeHGlobal sqlBytes
        let readInt64 (statement: nativeint) = columnInt64.Invoke(statement, 0)
        let readText (statement: nativeint) =
            let pointer = columnText16.Invoke(statement, 0)
            let byteCount = columnBytes16.Invoke(statement, 0)
            if pointer = 0n || byteCount < 0 || byteCount % 2 <> 0 then
                failwith "native SQLite text setting was not valid UTF-16"
            Marshal.PtrToStringUni(pointer, byteCount / 2)
        let readInt sql = run sql readInt64 |> int
        let readTextValue sql = run sql readText
        { LoadedModulePath = modulePath.ToString()
          LoadedModuleSha256 = moduleHash
          SqliteVersion = readTextValue "SELECT sqlite_version()"
          JournalMode = readTextValue "PRAGMA journal_mode"
          Synchronous = readInt "PRAGMA synchronous"
          ReadUncommitted = readInt "PRAGMA read_uncommitted"
          ForeignKeys = readInt "PRAGMA foreign_keys"
          WalAutoCheckpoint = readInt "PRAGMA wal_autocheckpoint"
          BusyTimeout = readInt "PRAGMA busy_timeout"
          Autocommit = getAutocommit.Invoke database }

    let private verifyNativeConnectionSettings (counter: int ref) (nativeLibraryHandle: nativeint) (nativeLibraryPath: string) (nativeContext: nativeint) =
        let settings = readNativeSqliteSettings nativeLibraryHandle nativeLibraryPath nativeContext
        let sqliteVersion = Version.Parse settings.SqliteVersion
        check counter "native worker settings query uses the pinned SQLite DLL bytes" (settings.LoadedModuleSha256 = "b7385d722c83fb52142a00477a726723745916d22a555711ee89834c1111fb2e")
        check counter "native worker connection uses SQLite >= 3.51.3" (sqliteVersion >= Version(3, 51, 3))
        check counter "native worker connection uses WAL mode" (String.Equals(settings.JournalMode, "wal", StringComparison.OrdinalIgnoreCase))
        check counter "native worker connection uses FULL synchronous durability" (settings.Synchronous = 2)
        check counter "native worker connection disables read-uncommitted" (settings.ReadUncommitted = 0)
        check counter "native worker connection enables foreign keys" (settings.ForeignKeys = 1)
        check counter "native worker connection uses WAL auto-checkpoint 1000" (settings.WalAutoCheckpoint = 1000)
        check counter "native worker connection uses the bounded five-second busy timeout" (settings.BusyTimeout = 5000)
        check counter "native worker starts in SQLite autocommit mode" (settings.Autocommit = 1)

    let private verifyWalSnapshots (counter: int ref) databasePath nativeLibraryHandle nativeLibraryPath nativeContext =
        use reader = openDatabase databasePath SqliteOpenMode.ReadWrite
        let journalMode = Convert.ToString(scalar reader "PRAGMA journal_mode", Globalization.CultureInfo.InvariantCulture)
        let sqliteVersion = Version.Parse(Convert.ToString(scalar reader "SELECT sqlite_version()", Globalization.CultureInfo.InvariantCulture))
        let synchronous = Convert.ToInt32(scalar reader "PRAGMA synchronous", Globalization.CultureInfo.InvariantCulture)
        let readUncommitted = Convert.ToInt32(scalar reader "PRAGMA read_uncommitted", Globalization.CultureInfo.InvariantCulture)
        let foreignKeys = Convert.ToInt32(scalar reader "PRAGMA foreign_keys", Globalization.CultureInfo.InvariantCulture)
        check counter "reference connection opens the patched SQLite runtime" (sqliteVersion >= Version(3, 51, 3))
        check counter "reference connection observes the provisioned WAL database" (String.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
        check counter "reference connection uses FULL synchronous durability" (synchronous = 2)
        check counter "reference connection disables read-uncommitted" (readUncommitted = 0)
        check counter "reference connection enables foreign keys" (foreignKeys = 1)
        verifyNativeConnectionSettings counter nativeLibraryHandle nativeLibraryPath nativeContext

        execute reader "PRAGMA busy_timeout = 500" [] |> ignore
        execute reader "BEGIN DEFERRED" [] |> ignore
        let nameSql = "SELECT name FROM rooms WHERE id = 101"
        let oldSnapshot = Convert.ToString(scalar reader nameSql, Globalization.CultureInfo.InvariantCulture)
        use writer = openDatabase databasePath SqliteOpenMode.ReadWrite
        execute writer "PRAGMA busy_timeout = 500" [] |> ignore
        execute writer "UPDATE rooms SET name = 'WAL committed room name' WHERE id = 101" [] |> ignore
        let stillOld = Convert.ToString(scalar reader nameSql, Globalization.CultureInfo.InvariantCulture)
        use freshReader = openDatabase databasePath SqliteOpenMode.ReadWrite
        let committed = Convert.ToString(scalar freshReader nameSql, Globalization.CultureInfo.InvariantCulture)
        check counter "WAL reader keeps its original snapshot while another connection commits" (stillOld = oldSnapshot && oldSnapshot = "General")
        check counter "a separate reader sees the concurrent writer commit" (committed = "WAL committed room name")
        execute reader "COMMIT" [] |> ignore
        let afterCommit = Convert.ToString(scalar reader nameSql, Globalization.CultureInfo.InvariantCulture)
        check counter "old reader observes the new snapshot after its transaction ends" (afterCommit = committed)
        execute writer "UPDATE rooms SET name = 'General' WHERE id = 101" [] |> ignore

    let private verifyNativeWriterRecovery (counter: int ref) (native: OwningStackCompiledProgram) nativeContext databasePath referenceDatabasePath referenceEntry referenceCookies =
        let validCookie = cookieServiceCookie referenceCookies sessionToken fixedNow
        let due =
            { Name = "native-refresh-under-writer-lock"
              Cookie = Some validCookie
              BotKey = None
              BrowserFamily = "other"
              BrowserVersion = None
              RequestNow = fixedNow
              SessionToken = Some sessionToken
              SessionUserId = 7L
              LastActiveAt = "2026-01-01 10:00:00.000000"
              RoomId = 101L
              ExpectedStatus = 200L
              ExpectedView = "room"
              ExpectedRefresh = true
              ExpectedMessageCount = Some 0 }
        configureSession referenceDatabasePath due
        configureSession databasePath due
        let request = makeRequest due
        let referenceExpected = invokeReference referenceEntry request
        use blocker = openDatabase databasePath SqliteOpenMode.ReadWrite
        execute blocker "PRAGMA busy_timeout = 500" [] |> ignore
        execute blocker "BEGIN IMMEDIATE" [] |> ignore
        let timer = Stopwatch.StartNew()
        let blockedResult = native.ExecuteWithNativeHostContext([ request ], 4 * 1024 * 1024, 4 * 1024 * 1024, nativeContext)
        timer.Stop()
        let blockedFields = outputFields blockedResult.Values.Head
        check counter "contended refresh returns a typed database error response" (blockedFields.TryFind "status" = Some(IntValue 500L))
        check counter "native writer contention is bounded by the configured busy timeout" (timer.Elapsed < TimeSpan.FromSeconds 7.0)
        let unchanged = Convert.ToString(scalar blocker "SELECT last_active_at FROM sessions WHERE id = 70", Globalization.CultureInfo.InvariantCulture)
        check counter "failed refresh does not partially update the session" (unchanged = due.LastActiveAt)
        execute blocker "ROLLBACK" [] |> ignore
        let retried = native.ExecuteWithNativeHostContext([ request ], 4 * 1024 * 1024, 4 * 1024 * 1024, nativeContext)
        check counter "same native context recovers and matches interpreter after releasing writer lock" (retried.Values = [ referenceExpected ])
        let updated = Convert.ToString(scalar blocker "SELECT last_active_at FROM sessions WHERE id = 70", Globalization.CultureInfo.InvariantCulture)
        check counter "successful retry persists the refreshed session timestamp" (updated = "2026-01-01 12:00:00")

    let private sha256 (path: string) = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes path)).ToLowerInvariant()

    let private writeReceipt (repo: string) (runRoot: string) (runtimeProfile: string) (executionProfile: string) (passed: bool) (assertionCount: int) (resultHashes: Map<string, string>) =
        let hashes = JsonObject()
        for relative in sourceFiles repo do
            let path = Path.Combine(repo, relative.Replace('/', Path.DirectorySeparatorChar))
            if File.Exists path then hashes[relative] <- JsonValue.Create(sha256 path)
        let results = JsonObject()
        for KeyValue(name, hash) in resultHashes do results[name] <- JsonValue.Create(hash)
        let receipt = JsonObject()
        receipt["passed"] <- JsonValue.Create passed
        receipt["runtimeProfile"] <- JsonValue.Create runtimeProfile
        receipt["executionProfile"] <- JsonValue.Create executionProfile
        receipt["assertions"] <- JsonValue.Create assertionCount
        receipt["sourceSha256"] <- hashes
        receipt["artifactsSha256"] <- results
        File.WriteAllText(Path.Combine(runRoot, "test-receipt.json"), receipt.ToJsonString(), UTF8Encoding(false))

    let writeFailureReceipt (repo: string) (runRoot: string) (runtimeProfile: string) (executionProfile: string) (safeMessage: string) (diagnostic: string) =
        let hashes = JsonObject()
        for relative in sourceFiles repo do
            let path = Path.Combine(repo, relative.Replace('/', Path.DirectorySeparatorChar))
            if File.Exists path then hashes[relative] <- JsonValue.Create(sha256 path)
        let receipt = JsonObject()
        receipt["passed"] <- JsonValue.Create false
        receipt["runtimeProfile"] <- JsonValue.Create runtimeProfile
        receipt["executionProfile"] <- JsonValue.Create executionProfile
        receipt["failure"] <- JsonValue.Create safeMessage
        receipt["privateDiagnostic"] <- JsonValue.Create diagnostic
        receipt["sourceSha256"] <- hashes
        File.WriteAllText(Path.Combine(runRoot, "test-receipt.json"), receipt.ToJsonString(), UTF8Encoding(false))

    let run toolchain repo runRoot runtimeProfile executionProfile =
        if not (OperatingSystem.IsWindows()) then failwith "the real native SQLite verifier is Windows-only"
        let assertions = ref 0
        let contextBytes = Marshal.SizeOf<RoomReadContextAbi>()
        let offset name = Marshal.OffsetOf<RoomReadContextAbi>(name).ToInt32()
        check assertions "native room-read context interop size matches ABI v1" (contextBytes = 136)
        check assertions "native room-read context statement array begins at byte 8" (offset "Statements" = 8)
        check assertions "native room-read context pure pointer is at byte 112" (offset "PureContext" = 112)
        check assertions "native room-read context cookie is at byte 120" (offset "Cookie" = 120)
        check assertions "native room-read context owner thread is at byte 128" (offset "OwnerThreadId" = 128)
        check assertions "native room-read context active flag is at byte 132" (offset "Active" = 132)
        let fixtureRoot = Path.Combine(runRoot, "test-fixture")
        Directory.CreateDirectory fixtureRoot |> ignore
        let referenceDatabase = Path.Combine(fixtureRoot, "interpreter.sqlite")
        let nativeDatabase = Path.Combine(fixtureRoot, "native.sqlite")
        seedFixture referenceDatabase
        File.Copy(referenceDatabase, nativeDatabase, true)

        let referenceCookies = createCookieService()
        let referenceEnvironment = makeEnvironment referenceDatabase referenceCookies
        progress "creating compiler-minted interpreter application and entry"
        let referenceApp = Application.create (Path.Combine(runRoot, "test-interpreter-app")) (Path.Combine(repo, "applications", "campfire")) referenceEnvironment
        let referenceEntry = referenceApp.Engine.BindFlowEntryPoint("campfire.http.room-read", [ TNamed "CampfireHttpRequest" ], [ TNamed "CampfireHttpResponse" ])
        let body = referenceApp.Engine.ExportVerifiedFlowEntryPoint("campfire.http.room-read", [ TNamed "CampfireHttpRequest" ], [ TNamed "CampfireHttpResponse" ])
        let program = body |> VerifiedIrBody.program |> VerifiedIrProgram.inspect
        progress "compiler-minted entry ready"
        let dependencies = nativeDependencies toolchain repo runRoot
        let aggregator = buildAdapterAggregator repo runRoot
        let cases = createCases referenceCookies
        let artifactHashes = ResizeArray<string * string>()
        let mutable smokeFixture: (TestCase * Value * byte array * byte array) option = None
        // The app-owned host manifest: the compiler knows nothing about Campfire.
        let hostManifest = OwningAppHostManifest.Load(Path.Combine(repo, "applications", "campfire", "Native", "room-read-host.json"))
        let hostProfile = OwningNativeHostProfile.AppHost hostManifest
        try
            for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
                let optimizationName = match optimization with LlvmOptimization.O0 -> "O0" | LlvmOptimization.O2 -> "O2"
                let artifactDirectory = Path.Combine(runRoot, "test-artifacts", optimizationName)
                progress $"{optimizationName}: compiling native profile artifact"
                use native = OwningStackAot.compileWithNativeHostDependenciesWithRuntimeProfileAndInstrumentation toolchain optimization runtimeProfile executionProfile hostProfile aggregator dependencies artifactDirectory body
                progress $"{optimizationName}: native profile artifact compiled"
                check assertions $"{optimizationName}: native manifest is emitted" native.NativeHostManifestPath.IsSome
                check assertions $"{optimizationName}: compiled program retains the selected runtime profile" (native.RuntimeProfile = runtimeProfile)
                let manifestPath = native.NativeHostManifestPath.Value
                let manifest = JsonNode.Parse(File.ReadAllText manifestPath).AsObject()
                check assertions $"{optimizationName}: native profile publishes the 16 MiB output limit" (manifest.ToJsonString().Contains("16777216", StringComparison.Ordinal))
                let moduleInfo = manifest["moduleInfo"].AsObject()
                let profileHeaderHash = sha256 (Path.Combine(repo, "src", "AgentLang.Llvm", "native", "owning_stack_profile.h"))
                let profileHeaderIsPinned =
                    moduleInfo["sourceHashes"].AsArray()
                    |> Seq.choose Option.ofObj
                    |> Seq.exists (fun source ->
                        source["path"].GetValue<string>() = "native-runtime/owning_stack_profile.h"
                        && source["sha256"].GetValue<string>() = profileHeaderHash)
                check assertions $"{optimizationName}: manifest pins runtime and execution profiles" (
                    moduleInfo["runtimeProfile"].GetValue<string>() = runtimeProfileName runtimeProfile
                    && moduleInfo["executionProfile"].GetValue<string>() = executionProfileName executionProfile
                    && profileHeaderIsPinned)
                let requestSchema =
                    moduleInfo["boundarySchemas"].AsArray()
                    |> Seq.choose Option.ofObj
                    |> Seq.tryFind (fun shape -> shape["typeName"].GetValue<string>() = "CampfireHttpRequest")
                let requestNowField =
                    requestSchema
                    |> Option.bind (fun shape ->
                        shape["fields"].AsArray()
                        |> Seq.choose Option.ofObj
                        |> Seq.tryFind (fun field -> field["name"].GetValue<string>() = "now"))
                let requestNowSchemaIsTyped =
                    match requestNowField with
                    | Some field ->
                        field["typeName"].GetValue<string>() = "UtcInstant"
                        && field["offsetBytes"].GetValue<int>() = 24
                        && field["extentBytes"].GetValue<int>() = 8
                    | None -> false
                check assertions $"{optimizationName}: host-profile request schema pins now as nominal UtcInstant at offset 24" requestNowSchemaIsTyped
                let typedNowRequest = makeRequest (List.head cases)
                let requestValueUsesNominalNow =
                    match typedNowRequest with
                    | RecordValue("CampfireHttpRequest", fields) ->
                        match fields.TryFind "now" with
                        | Some(NamedValue("UtcInstant", IntValue _)) -> true
                        | _ -> false
                    | _ -> false
                check assertions $"{optimizationName}: validated entry request uses a typed UtcInstant value" requestValueUsesNominalNow
                artifactHashes.Add($"{optimizationName}/dll", sha256 native.LibraryPath)
                artifactHashes.Add($"{optimizationName}/manifest", sha256 manifestPath)
                artifactHashes.Add($"{optimizationName}/sqlite", sha256 (Path.Combine(artifactDirectory, "e_sqlite3.dll")))

                let handle = NativeLibrary.Load native.LibraryPath
                let context, releaseContext = createNativeContext handle nativeDatabase
                try
                    progress $"{optimizationName}: native SQLite context initialized"
                    let detailedProfile =
                        match executionProfile with
                        | OwningRuntimeInstrumentation.Off -> None
                        | OwningRuntimeInstrumentation.Detailed ->
                            let api = profileApi handle
                            verifyDetailedProfileAbi assertions api
                            verifyDetailedProfileThreadIsolation assertions api
                            Some api
                    progress $"{optimizationName}: checking direct 8-byte browser callback capacity"
                    verifyEightByteBrowserDispatch assertions handle context
                    progress $"{optimizationName}: direct 8-byte browser callback passed"
                    for testCase in cases do
                        progress $"{optimizationName}/{testCase.Name}: invoking reference and native entry"
                        configureSession referenceDatabase testCase
                        configureSession nativeDatabase testCase
                        match detailedProfile, testCase.Name with
                        | Some api, "legacy-cookie-purpose-metadata-fallback" ->
                            check assertions $"{optimizationName}/{testCase.Name}: detailed profile reset succeeds" (api.Reset.Invoke(1u) = 0)
                        | _ -> ()
                        let request = makeRequest testCase
                        let expected = invokeReference referenceEntry request
                        let actual =
                            try
                                native.ExecuteWithNativeHostContext([ request ], 4 * 1024 * 1024, 4 * 1024 * 1024, context)
                            with
                            | :? OwningStackExecutionException as error ->
                                let diagnostic = error.Diagnostic
                                let word = diagnostic.Word |> Option.defaultValue "<none>"
                                let expectedText = String.concat ", " diagnostic.Expected
                                let actualText = String.concat ", " diagnostic.Actual
                                let detail = $"diagnostic={diagnostic.Code}; word={word}; expected=[{expectedText}]; actual=[{actualText}]"
                                raise (InvalidDataException($"{optimizationName}/{testCase.Name}: native execution or output decoding failed ({detail}).", error))
                            | error ->
                                raise (InvalidDataException($"{optimizationName}/{testCase.Name}: native execution or output decoding failed.", error))
                        match detailedProfile, testCase.Name with
                        | Some api, "legacy-cookie-purpose-metadata-fallback" -> verifyDetailedRoomReadCounters assertions api
                        | _ -> ()
                        check assertions $"{optimizationName}/{testCase.Name}: full typed interpreter/native values agree" (actual.Values = [ expected ])
                        let fields = outputFields expected
                        check assertions $"{optimizationName}/{testCase.Name}: expected status" (fields.TryFind "status" = Some(IntValue testCase.ExpectedStatus))
                        check assertions $"{optimizationName}/{testCase.Name}: expected view" (fields.TryFind "view" = Some(EnumValue("HttpView", testCase.ExpectedView)))
                        let refreshPresent =
                            match fields.TryFind "refresh-session-token" with
                            | Some(OptionValue(_, Some _)) -> true
                            | _ -> false
                        check assertions $"{optimizationName}/{testCase.Name}: refresh directive" (refreshPresent = testCase.ExpectedRefresh)
                        // Report 268: message rows are rendered into the Flow body; the
                        // response no longer carries them.
                        match testCase.ExpectedMessageCount, fields.TryFind "body" with
                        | Some expectedCount, Some(StringValue body) ->
                            let rendered = body.Split(" data-message-id=\"", StringSplitOptions.None).Length - 1
                            check assertions $"{optimizationName}/{testCase.Name}: message page size" (rendered = expectedCount)
                            if testCase.Name = "unicode-forty-message-list-with-tied-latest-time" then
                                check assertions "Unicode page retains body text and tied selected rows" (body.Contains("latest body — λ 東京 🚀", StringComparison.Ordinal))
                        | Some _, _ -> check assertions $"{optimizationName}/{testCase.Name}: rendered body exists" false
                        | None, _ -> ()
                        progress $"{optimizationName}/{testCase.Name}: typed result checks passed"
                        if testCase.Name = "unicode-forty-message-list-with-tied-latest-time" then
                            let requestBytes = encodeValue program native.Layouts native.InputTypes.Head request
                            let expectedBytes = encodeValue program native.Layouts native.OutputTypes.Head expected
                            smokeFixture <- Some(testCase, request, requestBytes, expectedBytes)
                        if testCase.Name = "unicode-forty-message-list-with-tied-latest-time" then
                            let sentinel = Array.create (actual.RetainedBytesWritten + 64) 0xa5uy
                            let before = Array.copy sentinel
                            let mutable rejected = false
                            try native.ExecuteWithNativeHostContextInto([ request ], 4 * 1024 * 1024, sentinel, actual.RetainedBytesWritten - 1, context) |> ignore
                            with :? OwningStackCapacityException as error -> rejected <- error.Code = "OWNING_RETAINED_CAPACITY"
                            check assertions $"{optimizationName}: retained output capacity rejects the large page" rejected
                            check assertions $"{optimizationName}: retained capacity failure leaves caller buffer unchanged" (sentinel = before)

                    let headCase = cases |> List.find (fun testCase -> testCase.Name = "legacy-cookie-purpose-metadata-fallback")
                    progress $"{optimizationName}/method-head: comparing GET and HEAD full values"
                    configureSession referenceDatabase headCase
                    configureSession nativeDatabase headCase
                    let getRequest = makeRequest headCase
                    let expectedGet = invokeReference referenceEntry getRequest
                    let actualGet = native.ExecuteWithNativeHostContext([ getRequest ], 4 * 1024 * 1024, 4 * 1024 * 1024, context)
                    check assertions $"{optimizationName}/method-get-baseline: full typed values agree" (actualGet.Values = [ expectedGet ])
                    let headRequest = withRequestMethod getRequest "head"
                    let expectedHead = invokeReference referenceEntry headRequest
                    let actualHead = native.ExecuteWithNativeHostContext([ headRequest ], 4 * 1024 * 1024, 4 * 1024 * 1024, context)
                    check assertions $"{optimizationName}/method-head: interpreter matches the same GET response" (expectedHead = expectedGet)
                    check assertions $"{optimizationName}/method-head: full typed native value matches GET" (actualHead.Values = [ expectedGet ])

                    let unsafeCase =
                        { headCase with
                            Name = "unsafe-method"
                            Cookie = Some "invalid-signed-cookie"
                            SessionToken = None
                            ExpectedStatus = 500L
                            ExpectedView = "empty"
                            ExpectedRefresh = false
                            ExpectedMessageCount = None }
                    progress $"{optimizationName}/method-unsafe: checking rejection before providers"
                    configureSession referenceDatabase unsafeCase
                    configureSession nativeDatabase unsafeCase
                    let unsafeRequest = makeRequest unsafeCase |> fun request -> withRequestMethod request "unsafe"
                    let expectedUnsafe = invokeReference referenceEntry unsafeRequest
                    let actualUnsafe = native.ExecuteWithNativeHostContext([ unsafeRequest ], 4 * 1024 * 1024, 4 * 1024 * 1024, context)
                    let unsafeFields = outputFields expectedUnsafe
                    check assertions $"{optimizationName}/method-unsafe: full typed interpreter/native values agree" (actualUnsafe.Values = [ expectedUnsafe ])
                    check assertions $"{optimizationName}/method-unsafe: rejected before room read" (unsafeFields.TryFind "status" = Some(IntValue 500L) && unsafeFields.TryFind "view" = Some(EnumValue("HttpView", "empty")))
                    if optimization = LlvmOptimization.O0 then
                        progress "O0: checking native SQLite connection settings and WAL snapshot behavior"
                        verifyWalSnapshots assertions nativeDatabase handle native.LibraryPath context
                        progress "O0: checking bounded writer contention and context recovery"
                        verifyNativeWriterRecovery assertions native context nativeDatabase referenceDatabase referenceEntry referenceCookies
                        progress "O0: writer contention and context recovery checks passed"
                finally
                    releaseContext()
                    NativeLibrary.Free handle

                match smokeFixture with
                | None -> failwith "the Unicode page did not provide the standalone smoke fixture"
                | Some(smokeCase, request, requestBytes, expectedBytes) ->
                    let smokeDirectory = Path.Combine(runRoot, "test-smoke", optimizationName)
                    progress $"{optimizationName}: compiling and running standalone no-CLR smoke"
                    configureSession referenceDatabase smokeCase
                    configureSession nativeDatabase smokeCase
                    Directory.CreateDirectory smokeDirectory |> ignore
                    let headerPath = Path.Combine(smokeDirectory, "room_read_test_fixture.h")
                    writeSmokeFixture headerPath requestBytes expectedBytes (Encoding.UTF8.GetBytes secret)
                    let smokeSource = Path.Combine(repo, "tests", "AgentLang.Campfire.NativeRoomRead.Tests", "native", "room_read_standalone_smoke.c")
                    let smokeExe = compileSmoke toolchain optimization executionProfile smokeSource (Path.GetDirectoryName(Path.Combine(artifactDirectory, "native-runtime", "room_read_adapter.h"))) (findSdkLibrary "kernel32.lib") (findSdkLibrary "shell32.lib") toolchain.CompilerRuntimeLibrary smokeDirectory
                    use executableStream = File.OpenRead smokeExe
                    use pe = new PEReader(executableStream)
                    check assertions $"{optimizationName}: standalone C smoke executable contains no CLR header" (isNull pe.PEHeaders.CorHeader)
                    let runSmoke = ProcessStartInfo(smokeExe)
                    runSmoke.WorkingDirectory <- smokeDirectory
                    runSmoke.UseShellExecute <- false
                    runSmoke.ArgumentList.Add native.LibraryPath
                    runSmoke.ArgumentList.Add nativeDatabase
                    runSmoke.ArgumentList.Add(match runtimeProfile with OwningRuntimeProfile.Diagnostic -> "0" | OwningRuntimeProfile.TrustedGenerated -> "1")
                    if executionProfile = OwningRuntimeInstrumentation.Detailed then runSmoke.ArgumentList.Add "Detailed"
                    use smokeProcess = Process.Start(runSmoke)
                    smokeProcess.WaitForExit()
                    check assertions $"{optimizationName}: standalone C smoke invokes the generated export without CLR" (smokeProcess.ExitCode = 0)
                    artifactHashes.Add($"{optimizationName}/smoke-exe", sha256 smokeExe)
                    progress $"{optimizationName}: standalone no-CLR smoke passed"

            let mismatchDirectory = Path.Combine(runRoot, "test-profile-mismatch-must-not-exist")
            progress "checking pre-emission rejection when the app manifest omits a reachable host operation"
            let narrowedProfile =
                OwningNativeHostProfile.AppHost
                    { hostManifest with Operations = hostManifest.Operations |> List.filter (fun operation -> operation.Name <> "campfire.messages.last-page") }
            let narrowedDiagnostic =
                try
                    OwningStackAot.compileWithNativeHostDependencies toolchain LlvmOptimization.O0 narrowedProfile aggregator dependencies mismatchDirectory body |> ignore
                    "compiled"
                with
                | LanguageException diagnostic -> diagnostic.Code
            check assertions $"a reachable host call missing from the app manifest is rejected (actual {narrowedDiagnostic})" (narrowedDiagnostic = "IR_NATIVE_HOST_OPERATION_UNSUPPORTED")
            check assertions "profile rejection occurs before native artifacts are emitted" (not (Directory.Exists mismatchDirectory))
            writeReceipt repo runRoot (runtimeProfileName runtimeProfile) (executionProfileName executionProfile) true assertions.Value (Map.ofSeq artifactHashes)
            progress "native room-read verifier passed"
            assertions.Value
        finally
            (referenceCookies :> IDisposable).Dispose()
