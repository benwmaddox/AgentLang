namespace AgentLang.Aot.EntryExport

open System
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Text.Json.Nodes
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open AgentLang
open AgentLang.Llvm
open AgentLang.Campfire.Authentication.Host

[<Struct; StructLayout(LayoutKind.Sequential, Pack = 8)>]
type private NativeSqliteContextAbi =
    val mutable Database: nativeint
    val mutable Statement: nativeint
    val mutable Cookie: uint64
    val mutable OwnerThreadId: uint32
    val mutable Active: uint32

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private NativeSqliteContextInit = delegate of nativeint * nativeint * uint32 * nativeint -> int32

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private NativeSqliteContextDestroy = delegate of nativeint -> int32

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private NativeSqliteRoomLookup = delegate of nativeint * int64 * int64 * nativeint * uint32 * nativeint -> int32

[<RequireQualifiedAccess>]
module NativeSqliteTests =
    let private repo = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))
    let private roomOperation = HostOperationId "campfire.rooms.find-for-user"
    let private roomType = TNamed "RoomRow"
    let private roomOptionType = TOption roomType
    let private resultType = TResult(roomOptionType, TNamed "DbError")

    let private dispatch (engine: Runtime.Engine) operation (args: JsonObject) =
        let response = engine.Dispatch(operation, args)
        if not (response["ok"].GetValue<bool>()) then
            failwith (response.ToJsonString())
        response

    let private define (engine: Runtime.Engine) (source: string) =
        let args = JsonObject()
        args["frontend"] <- JsonValue.Create("flow")
        args["syntaxVersion"] <- JsonValue.Create(2)
        args["source"] <- JsonValue.Create(source)
        dispatch engine "define" args |> ignore

    let private prepareEntry (engine: Runtime.Engine) =
        let types = File.ReadAllText(Path.Combine(repo, "applications", "campfire", "campfire-types.flow"))
        let source =
            types
            + """
fn native-sqlite.lookup(user: UserId, room: RoomId) -> Result<Option<RoomRow>, DbError> {
    effects db.read
    doc "Calls the registered Campfire room storage operation."

    campfire.rooms.find-for-user(user, room)
}
fn native-sqlite.entry(user: UserId, room: RoomId) -> Result<Option<RoomRow>, DbError> {
    effects db.read
    doc "Exercises the native database context through a nested typed entry."

    native-sqlite.lookup(user, room)
}
test-file native-sqlite-lookup-fixture {
    override fn campfire.rooms.find-for-user(user: UserId, room: RoomId) -> Result<Option<RoomRow>, DbError> {
        result.ok<Option<RoomRow>, DbError>(option.none<RoomRow>())
    }
    test native-sqlite.lookup/scoped-override {
        native-sqlite.lookup(UserId.new(7), RoomId.new(101))
        => value result.ok<Option<RoomRow>, DbError>(option.none<RoomRow>())
    }
}
test-file native-sqlite-entry-fixture {
    override fn campfire.rooms.find-for-user(user: UserId, room: RoomId) -> Result<Option<RoomRow>, DbError> {
        result.ok<Option<RoomRow>, DbError>(option.none<RoomRow>())
    }
    test native-sqlite.entry/scoped-override {
        native-sqlite.entry(UserId.new(7), RoomId.new(101))
        => value result.ok<Option<RoomRow>, DbError>(option.none<RoomRow>())
    }
}
"""
        define engine source
        for name in [ "native-sqlite.lookup"; "native-sqlite.entry" ] do
            let commit = JsonObject()
            commit["word"] <- JsonValue.Create(name)
            dispatch engine "commit" commit |> ignore
        engine.ExportVerifiedFlowEntryPoint("native-sqlite.entry", [ TNamed "UserId"; TNamed "RoomId" ], [ resultType ])

    let private row id name kind creator created updated =
        RecordValue(
            "RoomRow",
            Map.ofList
                [ "id", NamedValue("RoomId", IntValue id)
                  "name", OptionValue(TString, Option.map StringValue name)
                  "kind", EnumValue("RoomKind", kind)
                  "creator-id", NamedValue("UserId", IntValue creator)
                  "created-at", StringValue created
                  "updated-at", StringValue updated ])

    let private roomResult result =
        ResultValue(roomOptionType, TNamed "DbError", result)

    let private roomFound value = roomResult (Ok(OptionValue(roomType, Some value)))
    let private roomMissing = roomResult (Ok(OptionValue(roomType, None)))
    let private unavailable = roomResult (Error(EnumValue("DbError", "unavailable")))

    let private expectedCases =
        [ ("open room retains nullable and offset timestamp fields", 7L, 101L,
           roomFound (row 101L (Some "General") "open" 7L "2026-06-07 18:19:20.123456-04:00" "2026-06-07T22:19:21.654321+00:00"))
          ("closed room retains a null name", 7L, 102L,
           roomFound (row 102L None "closed" 7L "2026-06-08 08:00:00" "2026-06-08 08:01:02.000007Z"))
          ("direct room preserves Unicode and fractional timestamps", 8L, 103L,
           roomFound (row 103L (Some "λ🚀 東京") "direct" -2L "2026-06-09T14:15:16.987654+05:45" "2026-06-09 08:30:17.000001 UTC"))
          ("existing room without this user's membership is hidden", 7L, 103L, roomMissing)
          ("existing room with another user's membership is hidden", 8L, 101L, roomMissing)
          ("missing room returns none", 7L, 999L, roomMissing) ]

    let private connectionString path =
        let builder = SqliteConnectionStringBuilder()
        builder.DataSource <- path
        builder.Mode <- SqliteOpenMode.ReadWriteCreate
        builder.ForeignKeys <- true
        builder.Pooling <- false
        builder.ToString()

    let private withConnection path action =
        use connection = new SqliteConnection(connectionString path)
        connection.Open()
        action connection

    let private execSql (connection: SqliteConnection) sql =
        use command = connection.CreateCommand()
        command.CommandText <- sql
        command.ExecuteNonQuery() |> ignore

    let private execute path sql = withConnection path (fun connection -> execSql connection sql)

    let private insertRoom (connection: SqliteConnection) id name kind creator created updated =
        use command = connection.CreateCommand()
        command.CommandText <-
            "INSERT INTO rooms (id, name, type, creator_id, created_at, updated_at) "
            + "VALUES ($id, $name, $kind, $creator, $created, $updated)"
        command.Parameters.AddWithValue("$id", box id) |> ignore
        command.Parameters.AddWithValue("$name", (match name with Some value -> box value | None -> box DBNull.Value)) |> ignore
        command.Parameters.AddWithValue("$kind", box kind) |> ignore
        command.Parameters.AddWithValue("$creator", box creator) |> ignore
        command.Parameters.AddWithValue("$created", box created) |> ignore
        command.Parameters.AddWithValue("$updated", box updated) |> ignore
        command.ExecuteNonQuery() |> ignore

    let private insertMembership (connection: SqliteConnection) userId roomId =
        use command = connection.CreateCommand()
        command.CommandText <- "INSERT INTO memberships (user_id, room_id) VALUES ($user, $room)"
        command.Parameters.AddWithValue("$user", box userId) |> ignore
        command.Parameters.AddWithValue("$room", box roomId) |> ignore
        command.ExecuteNonQuery() |> ignore

    let private seedDatabase (path: string) =
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        withConnection path (fun connection ->
            execSql connection "CREATE TABLE rooms (id INTEGER PRIMARY KEY, name TEXT NULL, type TEXT NOT NULL, creator_id INTEGER NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL)"
            execSql connection "CREATE TABLE memberships (user_id INTEGER NOT NULL, room_id INTEGER NOT NULL, PRIMARY KEY (user_id, room_id))"
            insertRoom connection 101L (Some "General") "Rooms::Open" 7L "2026-06-07 18:19:20.123456-04:00" "2026-06-07T22:19:21.654321+00:00"
            insertRoom connection 102L None "Rooms::Closed" 7L "2026-06-08 08:00:00" "2026-06-08 08:01:02.000007Z"
            insertRoom connection 103L (Some "λ🚀 東京") "Rooms::Direct" -2L "2026-06-09T14:15:16.987654+05:45" "2026-06-09 08:30:17.000001 UTC"
            insertMembership connection 7L 101L
            insertMembership connection 7L 102L
            insertMembership connection 8L 103L)

    let private snapshot path =
        withConnection path (fun connection ->
            let read sql render =
                use command = connection.CreateCommand()
                command.CommandText <- sql
                use reader = command.ExecuteReader()
                let rows = ResizeArray<string>()
                while reader.Read() do rows.Add(render reader)
                List.ofSeq rows
            let rooms =
                read "SELECT id, name, type, creator_id, created_at, updated_at FROM rooms ORDER BY id" (fun reader ->
                    let name = if reader.IsDBNull(1) then "<null>" else reader.GetString(1)
                    String.Join("|", [| reader.GetInt64(0).ToString(); name; reader.GetString(2); reader.GetInt64(3).ToString(); reader.GetString(4); reader.GetString(5) |]))
            let memberships =
                read "SELECT user_id, room_id FROM memberships ORDER BY user_id, room_id" (fun reader ->
                    $"{reader.GetInt64(0)}|{reader.GetInt64(1)}")
            rooms @ memberships)

    let private updateRoom path column value roomId =
        if column <> "type" && column <> "creator_id" && column <> "created_at" then invalidArg (nameof column) "Unexpected fixture column."
        withConnection path (fun connection ->
            use command = connection.CreateCommand()
            command.CommandText <- $"UPDATE rooms SET {column} = $value WHERE id = $id"
            command.Parameters.AddWithValue("$value", box value) |> ignore
            command.Parameters.AddWithValue("$id", box roomId) |> ignore
            command.ExecuteNonQuery() |> ignore)

    let private addUtf8Path path (action: nativeint -> uint32 -> 'T) =
        let bytes = Encoding.UTF8.GetBytes(Path.GetFullPath path)
        if bytes |> Array.contains 0uy then invalidArg (nameof path) "The canonical database path contains an embedded NUL."
        let pointer = Marshal.AllocHGlobal(bytes.Length + 1)
        try
            Marshal.Copy(bytes, 0, pointer, bytes.Length)
            Marshal.WriteByte(pointer, bytes.Length, 0uy)
            action pointer (uint32 bytes.Length)
        finally Marshal.FreeHGlobal pointer

    let private databasePointer context = Marshal.ReadIntPtr(context, 0)
    let private statementPointer context = Marshal.ReadIntPtr(context, 8)
    let private contextCookie context = uint64 (Marshal.ReadInt64(context, 16))
    let private contextOwner context = uint32 (Marshal.ReadInt32(context, 24))
    let private contextActive context = uint32 (Marshal.ReadInt32(context, 28))

    let private allocateContext () =
        let context = Marshal.AllocHGlobal(32)
        Marshal.Copy(Array.zeroCreate<byte> 32, 0, context, 32)
        context

    let private initializeContext (initialize: NativeSqliteContextInit) context path =
        let errorTag = Marshal.AllocHGlobal(4)
        try
            Marshal.WriteInt32(errorTag, -1)
            let status = addUtf8Path path (fun pathPointer pathBytes -> initialize.Invoke(context, pathPointer, pathBytes, errorTag))
            status, uint32 (Marshal.ReadInt32(errorTag))
        finally Marshal.FreeHGlobal errorTag

    let private createProvider path =
        let nativeProvider =
            CampfireAuthenticationHost.createProvider path
            |> Result.defaultWith (fun error -> failwithf "Could not open seeded SQLite fixture: %A" error)
        let mutable calls = 0
        let provider =
            { nativeProvider with
                OperationIds = Set.singleton roomOperation
                Invoke = fun operation values ->
                    calls <- calls + 1
                    nativeProvider.Invoke operation values }
        provider, (fun () -> calls)

    let private createEngine runRoot path =
        let provider, calls = createProvider path
        let contract = CampfireAuthenticationHost.manifest[roomOperation]
        let environment: HostOperationEnvironment =
            { Manifest = Map.ofList [ roomOperation, contract ]
              ProductionProvider = provider
              CreateTestProvider = fun () -> Error HostOperationFailure.TestProviderUnavailable }
        let engine =
            Runtime.Engine(
                Path.Combine(runRoot, "native-sqlite-project"),
                Set.singleton "db.read",
                "2026-01-01T00:00:00Z",
                fileSystemMode = FileSystemMode.Virtual,
                testCapabilities = Set.singleton "db.read",
                hostOperations = environment)
        let body = prepareEntry engine
        let bound = engine.BindFlowEntryPoint("native-sqlite.entry", [ TNamed "UserId"; TNamed "RoomId" ], [ resultType ])
        body, bound, calls

    let private runInterpreterCase (check: string -> bool -> unit) (bound: Runtime.FlowEntryPoint) managedCallCount label userId roomId expected =
        let beforeCalls = managedCallCount()
        let inputs = [ NamedValue("UserId", IntValue userId); NamedValue("RoomId", IntValue roomId) ]
        let actual =
            match bound.Invoke inputs with
            | Ok result -> result.Values
            | Error diagnostic -> failwith diagnostic.Message
        check $"interpreter independent result: {label}" (actual = [ expected ])
        check $"managed Campfire SQLite provider invoked once: {label}" (managedCallCount() = beforeCalls + 1)
        inputs, actual

    let private runNativeCase (check: string -> bool -> unit) optimization (native: OwningStackCompiledProgram) context label inputs interpreted expected =
        let previousStatement = statementPointer context
        let actual = native.ExecuteWithNativeHostContext(inputs, 131072, 131072, context)
        check $"{optimization} native result matches seeded storage: {label}" (actual.Values = [ expected ])
        check $"{optimization} native and interpreter agree: {label}" (actual.Values = interpreted)
        check $"{optimization} persistent prepared statement survives lookup: {label}" (statementPointer context = previousStatement)
        check $"{optimization} callback leaves context inactive: {label}" (contextActive context = 0u)

    let private verifyRawCapacityAndThreadGuards (check: string -> bool -> unit) handle context =
        let initialize = Marshal.GetDelegateForFunctionPointer<NativeSqliteContextInit>(NativeLibrary.GetExport(handle, "al_campfire_room_sqlite_context_init_v1"))
        let destroy = Marshal.GetDelegateForFunctionPointer<NativeSqliteContextDestroy>(NativeLibrary.GetExport(handle, "al_campfire_room_sqlite_context_destroy_v1"))
        let lookup = Marshal.GetDelegateForFunctionPointer<NativeSqliteRoomLookup>(NativeLibrary.GetExport(handle, "al_host_campfire_rooms_find_for_user_v1"))
        let output = Marshal.AllocHGlobal(65536)
        let extent = Marshal.AllocHGlobal(4)
        try
            let originalStatement = statementPointer context
            let wrongThreadStatus =
                Task.Run(Func<int32>(fun () -> lookup.Invoke(context, 7L, 101L, output, 65536u, extent))).GetAwaiter().GetResult()
            check "native room callback rejects a non-owner thread" (wrongThreadStatus <> 0)
            check "wrong-thread callback leaves the prepared statement intact" (statementPointer context = originalStatement && contextActive context = 0u)

            let wrongThreadDestroy =
                Task.Run(Func<int32>(fun () -> destroy.Invoke(context))).GetAwaiter().GetResult()
            check "native context destroy rejects a non-owner thread" (wrongThreadDestroy <> 0)
            check "wrong-thread destroy leaves the context open" (databasePointer context <> IntPtr.Zero && statementPointer context = originalStatement)

            Marshal.WriteInt32(extent, -1)
            let shortStatus = lookup.Invoke(context, 7L, 101L, output, 1u, extent)
            check "native callback rejects an output buffer below its serialized extent" (shortStatus <> 0 && Marshal.ReadInt32(extent) = 0)
            check "capacity failure preserves the persistent statement and clears active state" (statementPointer context = originalStatement && contextActive context = 0u)

            Marshal.WriteInt32(extent, 0)
            let status = lookup.Invoke(context, 7L, 101L, output, 65536u, extent)
            check "native callback succeeds after capacity failure" (status = 0 && Marshal.ReadInt32(extent) > 0)
            check "successful raw callback keeps its prepared statement" (statementPointer context = originalStatement && contextActive context = 0u)

            let missingPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(".")), "missing-native-sqlite", Guid.NewGuid().ToString("N") + ".sqlite3")
            let missingContext = allocateContext()
            try
                let status, errorTag = initializeContext initialize missingContext missingPath
                check "native context refuses a missing database without CREATE" (status <> 0 && errorTag = 0u)
                check "failed native open does not create the requested database" (not (File.Exists missingPath))
            finally Marshal.FreeHGlobal missingContext
        finally
            Marshal.FreeHGlobal extent
            Marshal.FreeHGlobal output

    let private verifyContextAbi (check: string -> bool -> unit) =
        let offsets =
            [ Marshal.OffsetOf<NativeSqliteContextAbi>("Database").ToInt32()
              Marshal.OffsetOf<NativeSqliteContextAbi>("Statement").ToInt32()
              Marshal.OffsetOf<NativeSqliteContextAbi>("Cookie").ToInt32()
              Marshal.OffsetOf<NativeSqliteContextAbi>("OwnerThreadId").ToInt32()
              Marshal.OffsetOf<NativeSqliteContextAbi>("Active").ToInt32() ]
        check "native SQLite worker context ABI is 32 bytes with the pinned x64 offsets" (IntPtr.Size = 8 && Marshal.SizeOf<NativeSqliteContextAbi>() = 32 && offsets = [ 0; 8; 16; 24; 28 ])

    let run (toolchain: LlvmToolchain) (runRoot: string) (dependencies: OwningNativeHostDependencies) (check: string -> bool -> unit) =
        verifyContextAbi check
        let fixtureRoot = Path.Combine(runRoot, "native-sqlite")
        Directory.CreateDirectory fixtureRoot |> ignore
        let databasePath = Path.Combine(fixtureRoot, "campfire-東京.sqlite3")
        seedDatabase databasePath
        let expectedSnapshot = snapshot databasePath
        let verifiedBody, bound, managedCalls = createEngine runRoot databasePath
        let adapterSource = Path.Combine(repo, "applications", "campfire", "Native", "room_sqlite_adapter.c")
        check "SQLite adapter source is the application native implementation" (File.Exists adapterSource)
        let invalidDirectory = Path.Combine(fixtureRoot, "managed-dependency-rejected")
        try
            use invalid =
                OwningStackAot.compileWithNativeHostDependencies toolchain LlvmOptimization.O2
                    OwningNativeHostProfile.CampfireRoomFindForUserV1 adapterSource
                    { dependencies with RuntimeLibraryPaths = [ typeof<Runtime.Engine>.Assembly.Location ] }
                    invalidDirectory verifiedBody
            failwith "Native build admitted a managed runtime dependency"
        with :? ArgumentException ->
            check "native dependency validation rejects managed runtime libraries" (not (File.Exists(Path.Combine(invalidDirectory, "owning-stack-native-host.dll"))))

        for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
            let outputDirectory = Path.Combine(fixtureRoot, string optimization)
            use native =
                OwningStackAot.compileWithNativeHostDependencies
                    toolchain
                    optimization
                    OwningNativeHostProfile.CampfireRoomFindForUserV1
                    adapterSource
                    dependencies
                    outputDirectory
                    verifiedBody
            use nativeFile = File.OpenRead(native.LibraryPath)
            use nativePe = new System.Reflection.PortableExecutable.PEReader(nativeFile)
            check $"{optimization} SQLite host artifact is a native DLL" (isNull nativePe.PEHeaders.CorHeader)

            let handle = NativeLibrary.Load(native.LibraryPath)
            let initialize = Marshal.GetDelegateForFunctionPointer<NativeSqliteContextInit>(NativeLibrary.GetExport(handle, "al_campfire_room_sqlite_context_init_v1"))
            let destroy = Marshal.GetDelegateForFunctionPointer<NativeSqliteContextDestroy>(NativeLibrary.GetExport(handle, "al_campfire_room_sqlite_context_destroy_v1"))
            let context = allocateContext()
            let secondContext = allocateContext()
            try
                let initStatus, errorTag = initializeContext initialize context databasePath
                check $"{optimization} native SQLite context opens the existing canonical path" (initStatus = 0 && errorTag = 0u)
                let firstDatabase = databasePointer context
                let firstStatement = statementPointer context
                check $"{optimization} context owns a live SQLite connection and prepared statement" (firstDatabase <> IntPtr.Zero && firstStatement <> IntPtr.Zero)
                check $"{optimization} context pins a cookie, owner thread, and inactive state" (contextCookie context <> 0UL && contextOwner context <> 0u && contextActive context = 0u)

                let secondStatus, secondErrorTag = initializeContext initialize secondContext databasePath
                check $"{optimization} a second worker can open its own context" (secondStatus = 0 && secondErrorTag = 0u)
                check $"{optimization} concurrent contexts own separate connections and statements" (databasePointer secondContext <> firstDatabase && statementPointer secondContext <> firstStatement)

                verifyRawCapacityAndThreadGuards check handle context

                for label, userId, roomId, expected in expectedCases do
                    let inputs, interpreted = runInterpreterCase check bound managedCalls label userId roomId expected
                    runNativeCase check optimization native context label inputs interpreted expected

                check $"{optimization} managed and native lookups leave the seeded database unchanged" (snapshot databasePath = expectedSnapshot)

                updateRoom databasePath "type" "Rooms::Hidden" 101L
                let inputs, interpreted = runInterpreterCase check bound managedCalls "malformed room kind maps to unavailable" 7L 101L unavailable
                runNativeCase check optimization native context "malformed room kind maps to unavailable" inputs interpreted unavailable
                updateRoom databasePath "type" "Rooms::Open" 101L

                updateRoom databasePath "created_at" "not-a-timestamp" 101L
                let inputs, interpreted = runInterpreterCase check bound managedCalls "malformed timestamp maps to unavailable" 7L 101L unavailable
                runNativeCase check optimization native context "malformed timestamp maps to unavailable" inputs interpreted unavailable
                updateRoom databasePath "created_at" "2026-06-07 18:19:20.123456-04:00" 101L

                updateRoom databasePath "creator_id" DBNull.Value 101L
                let inputs, interpreted = runInterpreterCase check bound managedCalls "NULL creator id maps to unavailable" 7L 101L unavailable
                runNativeCase check optimization native context "NULL creator id maps to unavailable" inputs interpreted unavailable
                updateRoom databasePath "creator_id" 7L 101L

                execute databasePath "DROP TABLE memberships"
                let inputs, interpreted = runInterpreterCase check bound managedCalls "SQLite prepare/step error maps to unavailable" 7L 101L unavailable
                runNativeCase check optimization native context "SQLite prepare/step error maps to unavailable" inputs interpreted unavailable
                execute databasePath "CREATE TABLE memberships (user_id INTEGER NOT NULL, room_id INTEGER NOT NULL, PRIMARY KEY (user_id, room_id))"
                withConnection databasePath (fun connection ->
                    insertMembership connection 7L 101L
                    insertMembership connection 7L 102L
                    insertMembership connection 8L 103L)
                let openExpected = match List.item 0 expectedCases with _, _, _, value -> value
                let inputs, interpreted = runInterpreterCase check bound managedCalls "valid lookup recovers after a SQLite error" 7L 101L openExpected
                runNativeCase check optimization native context "valid lookup recovers after a SQLite error" inputs interpreted openExpected
                check $"{optimization} all database error fixtures restore the seed snapshot" (snapshot databasePath = expectedSnapshot)

                check $"{optimization} owning context destroy succeeds on its worker" (destroy.Invoke(context) = 0)
                check $"{optimization} second context destroy succeeds on its worker" (destroy.Invoke(secondContext) = 0)
                check $"{optimization} context destroy clears owned handles" (databasePointer context = IntPtr.Zero && statementPointer context = IntPtr.Zero)

                let reinitStatus, reinitErrorTag = initializeContext initialize context databasePath
                check $"{optimization} a destroyed context can be initialized again" (reinitStatus = 0 && reinitErrorTag = 0u && databasePointer context <> IntPtr.Zero && statementPointer context <> IntPtr.Zero)
                let directExpected = match List.item 2 expectedCases with _, _, _, value -> value
                let inputs, interpreted = runInterpreterCase check bound managedCalls "lookup after reinitialization" 8L 103L directExpected
                runNativeCase check optimization native context "lookup after reinitialization" inputs interpreted directExpected
                check $"{optimization} reinitialized context destroy succeeds" (destroy.Invoke(context) = 0)
            finally
                if databasePointer context <> IntPtr.Zero then destroy.Invoke(context) |> ignore
                if databasePointer secondContext <> IntPtr.Zero then destroy.Invoke(secondContext) |> ignore
                Marshal.FreeHGlobal secondContext
                Marshal.FreeHGlobal context
                NativeLibrary.Free handle

        check "both native optimization builds leave the seeded database unchanged" (snapshot databasePath = expectedSnapshot)
