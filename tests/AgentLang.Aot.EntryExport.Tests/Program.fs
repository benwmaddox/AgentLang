module AgentLang.Aot.EntryExport.Tests

open System
open System.IO
open System.Diagnostics
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Reflection.PortableExecutable
open System.Text
open System.Text.Json.Nodes
open AgentLang
open AgentLang.Llvm
open AgentLang.Campfire.Http
open AgentLang.Campfire.Host
open AgentLang.Campfire.Authentication.Host
open AgentLang.Campfire.Browser.Host
open AgentLang.Aot.EntryExport

[<Struct; StructLayout(LayoutKind.Sequential, Pack = 8)>]
type private LoaderSqliteContext =
    val mutable Database: nativeint
    val mutable Statement: nativeint
    val mutable Cookie: uint64
    val mutable OwnerThreadId: uint32
    val mutable Active: uint32

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private LoaderSqliteContextInit = delegate of nativeint * nativeint * uint32 * nativeint -> int32

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private LoaderSqliteContextDestroy = delegate of nativeint -> int32

let private repo = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))
let private runRoot = Path.Combine(repo, ".agentlang", "campfire-native-sqlite-249", "entry-tests", "runs", DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N"))
let mutable private assertions = 0

let private check message condition =
    assertions <- assertions + 1
    if not condition then failwith message

let private command (engine: Runtime.Engine) operation (args: JsonObject) =
    let result = engine.Dispatch(operation, args)
    if not (result["ok"].GetValue<bool>()) then failwith (result.ToJsonString())
    result

let private define engine source =
    let args = JsonObject()
    args["frontend"] <- JsonValue.Create("flow")
    args["syntaxVersion"] <- JsonValue.Create(2)
    args["source"] <- JsonValue.Create(source: string)
    command engine "define" args |> ignore

let private nativeSqliteDependencies (toolchain: LlvmToolchain) =
    let nativeSource = Path.Combine(repo, "applications", "campfire", "Native")
    let dependencyDirectory = Path.Combine(runRoot, "dependency-build")
    Directory.CreateDirectory(dependencyDirectory) |> ignore
    let importLibrary = Path.Combine(dependencyDirectory, "e_sqlite3.lib")
    let start = ProcessStartInfo(Path.Combine(Path.GetDirectoryName(toolchain.ClangPath), "llvm-lib.exe"))
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    for argument in [ "/def:" + Path.Combine(nativeSource, "e_sqlite3.def"); "/machine:x64"; "/out:" + importLibrary ] do
        start.ArgumentList.Add(argument)
    use childProcess = Process.Start(start)
    let stdout = childProcess.StandardOutput.ReadToEndAsync()
    let stderr = childProcess.StandardError.ReadToEndAsync()
    childProcess.WaitForExit()
    let output = stdout.Result + stderr.Result
    File.WriteAllText(Path.Combine(dependencyDirectory, "import-library-build.log"), output)
    if childProcess.ExitCode <> 0 then failwith output
    let packageRoot =
        match Environment.GetEnvironmentVariable("NUGET_PACKAGES") with
        | null | "" -> Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages")
        | path -> path
    let sqliteDll = Path.Combine(packageRoot, "sqlitepclraw.lib.e_sqlite3", "2.1.12", "runtimes", "win-x64", "native", "e_sqlite3.dll")
    let hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sqliteDll))).ToLowerInvariant()
    check "SQLite dependency matches audited package bytes" (hash = "b7385d722c83fb52142a00477a726723745916d22a555711ee89834c1111fb2e")
    let sdkRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "Lib")
    let kernelLibrary =
        Directory.EnumerateDirectories(sdkRoot)
        |> Seq.sortByDescending Path.GetFileName
        |> Seq.map (fun path -> Path.Combine(path, "um", "x64", "kernel32.lib"))
        |> Seq.tryFind File.Exists
        |> Option.defaultWith (fun () -> failwith "Windows SDK x64 kernel32.lib is required")
    { HeaderPaths = [ Path.Combine(nativeSource, "room_sqlite_adapter.h"); Path.Combine(nativeSource, "sqlite_subset.h") ]
      LinkLibraryPaths = [ importLibrary; kernelLibrary ]
      RuntimeLibraryPaths = [ sqliteDll ] }

let private loadedSqliteModule () =
    use currentProcess = Process.GetCurrentProcess()
    currentProcess.Modules
    |> Seq.cast<ProcessModule>
    |> Seq.tryFind (fun item -> String.Equals(item.ModuleName, "e_sqlite3.dll", StringComparison.OrdinalIgnoreCase))

let private defaultDllSearchPathContainsSqlite () =
    let candidates = ResizeArray<string>()
    candidates.Add(Environment.CurrentDirectory)
    candidates.Add(AppContext.BaseDirectory)
    candidates.Add(Environment.SystemDirectory)
    let windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
    candidates.Add(windowsDirectory)
    candidates.Add(Path.Combine(windowsDirectory, "System"))
    match Environment.ProcessPath with
    | null | "" -> ()
    | processPath -> candidates.Add(Path.GetDirectoryName(processPath))
    match Environment.GetEnvironmentVariable("PATH") with
    | null | "" -> ()
    | path ->
        for directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) do
            candidates.Add(directory)
    candidates
    |> Seq.filter (String.IsNullOrWhiteSpace >> not)
    |> Seq.exists (fun directory -> File.Exists(Path.Combine(directory, "e_sqlite3.dll")))

let private runNativeLoaderProbe optimizationName priorArtifactPath databasePath =
    Directory.CreateDirectory(runRoot) |> ignore
    let optimization =
        match optimizationName with
        | "O0" -> LlvmOptimization.O0
        | "O2" -> LlvmOptimization.O2
        | _ -> invalidArg (nameof optimizationName) "Expected O0 or O2."
    let priorArtifactPath = Path.GetFullPath priorArtifactPath
    let databasePath = Path.GetFullPath databasePath
    let priorArtifactDirectory = Path.GetDirectoryName priorArtifactPath
    check "fresh loader child receives an existing prior native artifact" (File.Exists priorArtifactPath)
    check "fresh loader child receives the seeded database" (File.Exists databasePath)
    check "fresh child working directory differs from the native artifact directory"
        (not (String.Equals(Path.GetFullPath(Environment.CurrentDirectory), priorArtifactDirectory, StringComparison.OrdinalIgnoreCase)))
    check "fresh child application directory differs from the native artifact directory"
        (not (String.Equals(Path.GetFullPath(AppContext.BaseDirectory), priorArtifactDirectory, StringComparison.OrdinalIgnoreCase)))
    match Environment.ProcessPath with
    | null | "" -> check "fresh child process image directory differs from the native artifact directory" false
    | processPath ->
        check "fresh child process image directory differs from the native artifact directory"
            (not (String.Equals(Path.GetFullPath(Path.GetDirectoryName(processPath)), priorArtifactDirectory, StringComparison.OrdinalIgnoreCase)))
    check "e_sqlite3.dll is absent from the fresh child process modules" (loadedSqliteModule().IsNone)
    let managedSqliteLoaded =
        AppDomain.CurrentDomain.GetAssemblies()
        |> Array.exists (fun assembly ->
            let name = assembly.GetName().Name
            name = "Microsoft.Data.Sqlite" || name.StartsWith("SQLitePCLRaw", StringComparison.Ordinal))
    check "fresh probe has not loaded managed SQLite assemblies" (not managedSqliteLoaded)

    if defaultDllSearchPathContainsSqlite() then
        printfn "legacy string-loader negative control skipped: e_sqlite3.dll exists in a default Windows DLL search directory"
    else
        let legacyLoadFailed =
            try
                let handle = NativeLibrary.Load priorArtifactPath
                NativeLibrary.Free handle
                false
            with :? DllNotFoundException -> true
        check "legacy NativeLibrary.Load(string) fails while SQLite is not preloaded" (legacyLoadFailed && loadedSqliteModule().IsNone)

    let operation = HostOperationId "campfire.rooms.find-for-user"
    let contract = CampfireAuthenticationHost.manifest[operation]
    let provider: HostOperationProvider =
        { OperationIds = Set.singleton operation
          Invoke = fun _ _ -> Error HostOperationFailure.InvocationFailed
          Dispose = ignore }
    let environment: HostOperationEnvironment =
        { Manifest = Map.ofList [ operation, contract ]
          ProductionProvider = provider
          CreateTestProvider = fun () -> Error HostOperationFailure.TestProviderUnavailable }
    let engine =
        Runtime.Engine(
            Path.Combine(runRoot, "native-loader-probe-project"),
            Set.singleton "db.read",
            "2026-01-01T00:00:00Z",
            fileSystemMode = FileSystemMode.Virtual,
            testCapabilities = Set.singleton "db.read",
            hostOperations = environment)
    let types = File.ReadAllText(Path.Combine(repo, "applications", "campfire", "campfire-types.flow"))
    let source = types + """
fn native-loader.lookup(user: UserId, room: RoomId) -> Result<Option<RoomRow>, DbError> {
    effects db.read
    doc "Exercises the native room adapter through the owning-stack wrapper."

    campfire.rooms.find-for-user(user, room)
}
test-file native-loader-fixture {
    override fn campfire.rooms.find-for-user(user: UserId, room: RoomId) -> Result<Option<RoomRow>, DbError> {
        result.ok<Option<RoomRow>, DbError>(option.none<RoomRow>())
    }
    test native-loader.lookup/scoped-override {
        native-loader.lookup(UserId.new(7), RoomId.new(101))
        => value result.ok<Option<RoomRow>, DbError>(option.none<RoomRow>())
    }
}
"""
    define engine source
    let commit = JsonObject()
    commit["word"] <- JsonValue.Create("native-loader.lookup")
    command engine "commit" commit |> ignore
    let body =
        engine.ExportVerifiedFlowEntryPoint(
            "native-loader.lookup",
            [ TNamed "UserId"; TNamed "RoomId" ],
            [ TResult(TOption(TNamed "RoomRow"), TNamed "DbError") ])
    let toolchain = LlvmToolchain.discover()
    let adapterSource = Path.Combine(repo, "applications", "campfire", "Native", "room_sqlite_adapter.c")
    let outputDirectory = Path.Combine(runRoot, "fresh-loader", optimizationName)
    use native =
        OwningStackAot.compileWithNativeHostDependencies
            toolchain optimization OwningNativeHostProfile.CampfireRoomFindForUserV1 adapterSource
            (nativeSqliteDependencies toolchain) outputDirectory body
    let sqlitePath = Path.Combine(Path.GetDirectoryName(native.LibraryPath), "e_sqlite3.dll")
    match loadedSqliteModule() with
    | Some moduleInfo ->
        check "production wrapper loaded e_sqlite3 from beside the generated host DLL"
            (String.Equals(Path.GetFullPath(moduleInfo.FileName), Path.GetFullPath(sqlitePath), StringComparison.OrdinalIgnoreCase))
    | None -> check "production wrapper loaded the native SQLite dependency" false

    let handle = NativeLibrary.Load(native.LibraryPath)
    let initialize = Marshal.GetDelegateForFunctionPointer<LoaderSqliteContextInit>(NativeLibrary.GetExport(handle, "al_campfire_room_sqlite_context_init_v1"))
    let destroy = Marshal.GetDelegateForFunctionPointer<LoaderSqliteContextDestroy>(NativeLibrary.GetExport(handle, "al_campfire_room_sqlite_context_destroy_v1"))
    let context = Marshal.AllocHGlobal(32)
    let pathBytes = Encoding.UTF8.GetBytes databasePath
    let pathPointer = Marshal.AllocHGlobal(pathBytes.Length + 1)
    let errorTag = Marshal.AllocHGlobal(sizeof<uint32>)
    let mutable initialized = false
    try
        Marshal.Copy(Array.zeroCreate<byte> 32, 0, context, 32)
        Marshal.Copy(pathBytes, 0, pathPointer, pathBytes.Length)
        Marshal.WriteByte(pathPointer, pathBytes.Length, 0uy)
        Marshal.WriteInt32(errorTag, -1)
        let initStatus = initialize.Invoke(context, pathPointer, uint32 pathBytes.Length, errorTag)
        initialized <- initStatus = 0
        let contextState = Marshal.PtrToStructure<LoaderSqliteContext>(context)
        check "native SQLite context opens the database and prepared statement" (initStatus = 0 && Marshal.ReadInt32(errorTag) = 0 && contextState.Database <> IntPtr.Zero && contextState.Statement <> IntPtr.Zero)
        if initialized then
            let result =
                native.ExecuteWithNativeHostContext(
                    [ NamedValue("UserId", IntValue 7L); NamedValue("RoomId", IntValue 101L) ],
                    131072, 131072, context)
            let foundExpectedRoom =
                match result.Values with
                | [ ResultValue(_, _, Ok(OptionValue(_, Some(RecordValue("RoomRow", fields))))) ] ->
                    fields.TryFind "id" = Some(NamedValue("RoomId", IntValue 101L))
                | _ -> false
            check "freshly loaded native wrapper returns the seeded room" foundExpectedRoom
    finally
        if initialized then destroy.Invoke(context) |> ignore
        Marshal.FreeHGlobal errorTag
        Marshal.FreeHGlobal pathPointer
        Marshal.FreeHGlobal context
        NativeLibrary.Free handle
    printfn "native loader probe %s: %d assertions; passed=true; evidence=%s" optimizationName assertions runRoot
    0

let private verifyPureExport toolchain =
    let engine = Runtime.Engine(Path.Combine(runRoot, "pure-project"), Set.empty, "2026-01-01T00:00:00Z", fileSystemMode = FileSystemMode.Virtual)
    define engine """
type Counter : Int { }
record Summary {
    field value: Counter
    field positive: Bool
}
fn app.project(value: Counter) -> Summary {
    doc "Projects a nominal counter into a typed result record."

    summary.new(value = value, positive = int.greater-than(Counter.value(value), 0))
}
test app.project/positive {
    app.project(Counter.new(21))
    => value summary.new(value = Counter.new(21), positive = true)
}
test app.project/zero {
    app.project(Counter.new(0))
    => value summary.new(value = Counter.new(0), positive = false)
}
test app.project/negative {
    app.project(Counter.new(-2))
    => value summary.new(value = Counter.new(-2), positive = false)
}
"""
    let commit = JsonObject()
    commit["word"] <- JsonValue.Create("app.project")
    commit["library"] <- JsonValue.Create(true)
    command engine "commit" commit |> ignore
    let body = engine.ExportVerifiedFlowEntryPoint("app.project", [ TNamed "Counter" ], [ TNamed "Summary" ])
    check "pure export has compiler backend authority" (VerifiedIrProgram.isBackendExecutable (VerifiedIrBody.program body))
    check "pure export has no host contracts" (Map.isEmpty (VerifiedIrProgram.hostOperationContracts (VerifiedIrBody.program body)))
    let bound = engine.BindFlowEntryPoint("app.project", [ TNamed "Counter" ], [ TNamed "Summary" ])
    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        let nativeDirectory = Path.Combine(runRoot, "pure-native", string optimization)
        use native = OwningStackAot.compile toolchain optimization nativeDirectory body
        use nativeFile = File.OpenRead(Path.Combine(nativeDirectory, "owning-stack-native.dll"))
        use nativePe = new PEReader(nativeFile)
        check $"{optimization} artifact has no managed runtime header" (isNull nativePe.PEHeaders.CorHeader)
        for value, positive in [ -2L, false; 0L, false; 21L, true ] do
            let input = [ NamedValue("Counter", IntValue value) ]
            let expected = [ RecordValue("Summary", Map.ofList [ "value", NamedValue("Counter", IntValue value); "positive", BoolValue positive ]) ]
            let interpreted =
                match bound.Invoke input with
                | Ok result -> result.Values
                | Error diagnostic -> failwith diagnostic.Message
            check "interpreter matches independent projection result" (interpreted = expected)
            let actual = native.Execute(input, 65536, 65536)
            check $"{optimization} exported committed function matches independent result" (actual.Values = expected)
            check $"{optimization} native and interpreter agree" (actual.Values = interpreted)

let private verifyActualCampfireExport toolchain =
    let manifest = HostComposition.manifest [ CampfireHost.manifest; CampfireAuthenticationHost.manifest; CookieHost.manifest; BrowserHost.manifest; LoginHost.manifest ]
    let mutable providerCalls = 0
    let mutable testFactoryCalls = 0
    let mutable roomMode = 0
    let provider: HostOperationProvider =
        { OperationIds = manifest |> Map.toSeq |> Seq.map fst |> Set.ofSeq
          Invoke = fun id values ->
              providerCalls <- providerCalls + 1
              if id = HostOperationId "campfire.rooms.find-for-user" then NativeHostTests.interpretRoom roomMode values
              else Error HostOperationFailure.InvocationFailed
          Dispose = ignore }
    let environment: HostOperationEnvironment =
        { Manifest = manifest
          ProductionProvider = provider
          CreateTestProvider = fun () -> testFactoryCalls <- testFactoryCalls + 1; Error HostOperationFailure.TestProviderUnavailable }
    let application = Application.create (Path.Combine(runRoot, "campfire-project")) (Path.Combine(repo, "applications", "campfire")) environment
    check "Campfire qualification uses scoped overrides only" (providerCalls = 0 && testFactoryCalls = 0)
    let body = application.Engine.ExportVerifiedFlowEntryPoint("campfire.http.dispatch", [ TNamed "CampfireHttpRequest" ], [ TNamed "CampfireHttpResponse" ])
    check "Campfire export executes no provider" (providerCalls = 0 && testFactoryCalls = 0)
    check "Campfire export is compiler-authorized IR" (VerifiedIrProgram.isBackendExecutable (VerifiedIrBody.program body))
    let contracts = VerifiedIrProgram.hostOperationContracts (VerifiedIrBody.program body)
    let room = contracts[HostOperationId "campfire.rooms.find-for-user"]
    check "room contract identity is retained" (room.OperationName = "campfire.rooms.find-for-user")
    check "room contract effect is retained" (room.OperationEffects = Set.singleton IrEffect.DatabaseRead)
    check "full Campfire host catalog is retained" (contracts.Count = manifest.Count)
    let rejectedDirectory = Path.Combine(runRoot, "campfire-rejected-native")
    try
        use unexpected = OwningStackAot.compile toolchain LlvmOptimization.O2 rejectedDirectory body
        failwith "Native host operations unexpectedly compiled without admission or providers"
    with
    | LanguageException diagnostic ->
        check "native host boundary remains explicit" (diagnostic.Code = "IR_NATIVE_HOST_OPERATION_UNSUPPORTED")
        let blocker = JsonObject()
        blocker["code"] <- JsonValue.Create(diagnostic.Code)
        blocker["message"] <- JsonValue.Create(diagnostic.Message)
        File.WriteAllText(Path.Combine(runRoot, "campfire-native-blocker.json"), blocker.ToJsonString() + Environment.NewLine)
    check "rejected native Campfire emits no executable" (not (File.Exists(Path.Combine(rejectedDirectory, "owning-stack-native.dll"))))
    check "native rejection does not fall back to provider execution" (providerCalls = 0)
    NativeHostTests.run toolchain runRoot application.Engine (fun mode -> roomMode <- mode) check

[<EntryPoint>]
let main args =
    match args with
    | [| "--native-loader-probe"; optimizationName; priorArtifactPath; databasePath |] ->
        try runNativeLoaderProbe optimizationName priorArtifactPath databasePath
        with error -> eprintfn "%s" (error.ToString()); 1
    | _ ->
        Directory.CreateDirectory(runRoot) |> ignore
        let mutable failure = None
        try
            let toolchain = LlvmToolchain.discover()
            verifyPureExport toolchain
            verifyActualCampfireExport toolchain
            RoomReadEntryTests.run runRoot check |> ignore
            NativeSqliteTests.run toolchain runRoot (nativeSqliteDependencies toolchain) check
        with error -> failure <- Some(error.ToString())
        let receipt = JsonObject()
        receipt["passed"] <- JsonValue.Create(failure.IsNone)
        receipt["assertions"] <- JsonValue.Create(assertions)
        receipt["failure"] <- (match failure with Some text -> JsonValue.Create(text) :> JsonNode | None -> null)
        let pins = JsonObject()
        for relative in [
            "src/AgentLang.Core/Runtime.fs"
            "tests/AgentLang.Aot.EntryExport.Tests/Program.fs"
            "tests/AgentLang.Aot.EntryExport.Tests/NativeHostTests.fs"
            "tests/AgentLang.Aot.EntryExport.Tests/native/room_find_fixture.c"
            "tests/AgentLang.Aot.EntryExport.Tests/AgentLang.Aot.EntryExport.Tests.fsproj"
            "src/AgentLang.Llvm/OwningStackAot.fs"
            "src/AgentLang.Llvm/LlvmToolchain.fs"
            "src/AgentLang.Llvm/AgentLang.Llvm.fsproj"
            "src/AgentLang.Llvm/native/owning_stack_runtime.h"
            "src/AgentLang.Llvm/native/owning_stack_runtime.c"
            "src/AgentLang.Llvm/native/campfire_room_host_abi.h"
            "applications/campfire/Http/Application.fs"
            "applications/campfire/Native/room_sqlite_adapter.c"
            "applications/campfire/Native/room_sqlite_adapter.h"
            "applications/campfire/Native/sqlite_subset.h"
            "applications/campfire/Native/e_sqlite3.def"
            "applications/campfire/http.flow"
            "tests/AgentLang.Aot.EntryExport.Tests/NativeSqliteTests.fs"
            "tests/AgentLang.Aot.EntryExport.Tests/RoomReadEntryTests.fs"
            "tests/AgentLang.Aot.EntryExport.Tests/fixtures/room-read-reference-http.flow"
            "applications/campfire/http-room-read-tests.flow"
            "tests/AgentLang.Aot.EntryExport.Tests/native/room_sqlite_smoke.c"
        ] do
            pins[relative] <- JsonValue.Create(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(repo, relative)))).ToLowerInvariant())
        receipt["sourceSha256"] <- pins
        File.WriteAllText(Path.Combine(runRoot, "receipt.json"), receipt.ToJsonString() + Environment.NewLine)
        printfn "AOT entry export/native host: %d assertions; passed=%b; evidence=%s" assertions failure.IsNone runRoot
        match failure with
        | None -> 0
        | Some text -> eprintfn "%s" text; 1
