module AgentLang.Aot.EntryExport.NativeHostTests

open System
open System.IO
open System.Runtime.InteropServices
open System.Reflection.PortableExecutable
open System.Security.Cryptography
open System.Text.Json.Nodes
open AgentLang
open AgentLang.Llvm

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private BoundarySelfTest = delegate of unit -> int

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private RawHostExecute = delegate of nativeint * nativeint * nativeint * uint32 * nativeint * uint32 * nativeint * uint32 -> int32

let private verifyRawNullContext handle (check: string -> bool -> unit) =
    // Owning ABI2 x64 offsets are independently pinned by the native ABI gate.
    let context = Marshal.AllocHGlobal(176)
    let stack = Marshal.AllocHGlobal(4096)
    let initialized = Marshal.AllocHGlobal(512)
    let poison = Marshal.AllocHGlobal(512)
    try
        Marshal.Copy(Array.zeroCreate<byte> 176, 0, context, 176)
        Marshal.Copy(Array.zeroCreate<byte> 512, 0, initialized, 512)
        Marshal.Copy(Array.zeroCreate<byte> 512, 0, poison, 512)
        Marshal.WriteInt32(context, 0, 2)
        Marshal.WriteInt32(context, 4, 4096)
        Marshal.WriteInt32(context, 92, 512)
        Marshal.WriteIntPtr(context, 104, stack)
        Marshal.WriteIntPtr(context, 112, initialized)
        Marshal.WriteIntPtr(context, 120, poison)
        let execute = Marshal.GetDelegateForFunctionPointer<RawHostExecute>(NativeLibrary.GetExport(handle, "agentlang_owning_execute_with_host_context"))
        let status = execute.Invoke(context, IntPtr.Zero, IntPtr.Zero, 0u, IntPtr.Zero, 0u, IntPtr.Zero, 0u)
        check "raw native export rejects missing provider before entry-input validation" (status <> 0 && Marshal.ReadInt32(context, 76) = 1 && Marshal.ReadInt32(context, 80) <> 0)
        check "raw missing-provider rejection allocates no arena values" (Marshal.ReadInt32(context, 8) = 0)
    finally
        Marshal.FreeHGlobal(poison)
        Marshal.FreeHGlobal(initialized)
        Marshal.FreeHGlobal(stack)
        Marshal.FreeHGlobal(context)

let private roomType = TNamed "RoomRow"
let private outputType = TOption roomType
let private errorType = TNamed "DbError"

let expected mode userId roomId =
    let result =
        match mode with
        | 1 -> Ok(OptionValue(roomType, None))
        | 2 | 3 | 4 | 5 ->
            Error(EnumValue("DbError", [| "unavailable"; "not-found"; "constraint-conflict"; "invalid-input" |].[mode - 2]))
        | _ ->
            let name = if mode = 15 then None else Some(StringValue(if mode = 16 then "λ🚀" else "Lobby"))
            Ok(OptionValue(roomType, Some(RecordValue("RoomRow", Map.ofList [
                "id", NamedValue("RoomId", IntValue roomId)
                "name", OptionValue(TString, name)
                "kind", EnumValue("RoomKind", "open")
                "creator-id", NamedValue("UserId", IntValue userId)
                "created-at", StringValue "2026"
                "updated-at", StringValue "2026"
            ]))))
    ResultValue(outputType, errorType, result)

let interpretRoom mode = function
    | [ NamedValue("UserId", IntValue userId); NamedValue("RoomId", IntValue roomId) ] -> Ok [ expected mode userId roomId ]
    | _ -> Error HostOperationFailure.InvocationFailed

let private source = """
fn native-room.lookup(user: UserId, room: RoomId) -> Result<Option<RoomRow>, DbError> {
    effects db.read
    doc "Calls the existing typed room storage operation."

    campfire.rooms.find-for-user(user, room)
}
fn native-room.entry(user: UserId, room: RoomId) -> Result<Option<RoomRow>, DbError> {
    effects db.read
    doc "Exercises provider-context propagation through a nested compiled function."

    native-room.lookup(user, room)
}
test-file native-room-fixture {
    override fn campfire.rooms.find-for-user(user: UserId, room: RoomId) -> Result<Option<RoomRow>, DbError> {
        result.ok<Option<RoomRow>, DbError>(option.none<RoomRow>())
    }
    test native-room.lookup/missing {
        native-room.lookup(UserId.new(7), RoomId.new(11))
        => value result.ok<Option<RoomRow>, DbError>(option.none<RoomRow>())
    }
}
test-file native-room-entry-fixture {
    override fn campfire.rooms.find-for-user(user: UserId, room: RoomId) -> Result<Option<RoomRow>, DbError> {
        result.ok<Option<RoomRow>, DbError>(option.none<RoomRow>())
    }
    test native-room.entry/missing {
        native-room.entry(UserId.new(7), RoomId.new(11))
        => value result.ok<Option<RoomRow>, DbError>(option.none<RoomRow>())
    }
}
"""

let private dispatch (engine: Runtime.Engine) operation (args: JsonObject) =
    let result = engine.Dispatch(operation, args)
    if not (result["ok"].GetValue<bool>()) then failwith (result.ToJsonString())

let private prepareSource (engine: Runtime.Engine) (source: string) =
    let args = JsonObject()
    args["frontend"] <- JsonValue.Create("flow")
    args["syntaxVersion"] <- JsonValue.Create(2)
    args["source"] <- JsonValue.Create(source)
    dispatch engine "define" args
    for name in [ "native-room.lookup"; "native-room.entry" ] do
        let commit = JsonObject()
        commit["word"] <- JsonValue.Create(name)
        dispatch engine "commit" commit
    engine.ExportVerifiedFlowEntryPoint("native-room.entry", [ TNamed "UserId"; TNamed "RoomId" ], [ TResult(outputType, errorType) ])

let private prepare engine = prepareSource engine source

let private verifyDeniedContracts toolchain runRoot fixture (check: string -> bool -> unit) =
    let operationId = HostOperationId "campfire.rooms.find-for-user"
    let contract = AgentLang.Campfire.Authentication.Host.CampfireAuthenticationHost.manifest[operationId]
    let canonicalManifest = Map.ofList [ operationId, contract ]
    let otherId = HostOperationId "campfire.rooms.other"
    let otherManifest = canonicalManifest |> Map.add otherId { contract with Id = otherId; WordName = "campfire.rooms.other" }
    let types = File.ReadAllText(Path.Combine(__SOURCE_DIRECTORY__, "../../applications/campfire/campfire-types.flow"))
    for name, typeSource, definition, manifest in [
        "changed-enum", types.Replace("case open", "case hidden"), source, canonicalManifest
        "extra-declared-effect", types, source.Replace("effects db.read", "effects db.read, db.write"), canonicalManifest
        "unapproved-operation", types, source.Replace("campfire.rooms.find-for-user", "campfire.rooms.other"), otherManifest
    ] do
        let provider: HostOperationProvider =
            { OperationIds = manifest |> Map.toSeq |> Seq.map fst |> Set.ofSeq
              Invoke = fun _ _ -> failwith "Contract rejection invoked a managed provider"
              Dispose = ignore }
        let environment: HostOperationEnvironment =
            { Manifest = manifest
              ProductionProvider = provider
              CreateTestProvider = fun () -> Error HostOperationFailure.TestProviderUnavailable }
        let engine = Runtime.Engine(Path.Combine(runRoot, name), Set.ofList [ "db.read"; "db.write" ], fileSystemMode = FileSystemMode.Virtual, hostOperations = environment)
        let body = prepareSource engine (typeSource + Environment.NewLine + definition)
        let rejectedDirectory = Path.Combine(runRoot, name, "native")
        try
            use invalid = OwningStackAot.compileWithNativeHostProfile toolchain LlvmOptimization.O2 OwningNativeHostProfile.CampfireRoomFindForUserV1 fixture rejectedDirectory body
            failwith $"Native host profile admitted {name}"
        with LanguageException diagnostic ->
            check $"native host profile rejects {name} before artifact emission ({diagnostic.Code})" (not (File.Exists(Path.Combine(rejectedDirectory, "owning-stack-native-host.dll"))))

let run (toolchain: LlvmToolchain) (runRoot: string) (engine: Runtime.Engine) (setMode: int -> unit) (check: string -> bool -> unit) =
    let body = prepare engine
    let bound = engine.BindFlowEntryPoint("native-room.entry", [ TNamed "UserId"; TNamed "RoomId" ], [ TResult(outputType, errorType) ])
    let repo = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))
    let fixture = Path.Combine(__SOURCE_DIRECTORY__, "native", "room_find_fixture.c")
    verifyDeniedContracts toolchain runRoot fixture check
    let missingAdapter = Path.Combine(runRoot, "missing-room-adapter.c")
    File.WriteAllText(missingAdapter, "/* No required native host symbol. */\nint unrelated_native_adapter(void) { return 0; }\n")
    let missingDirectory = Path.Combine(runRoot, "missing-room-adapter")
    try
        use invalid = OwningStackAot.compileWithNativeHostProfile toolchain LlvmOptimization.O2 OwningNativeHostProfile.CampfireRoomFindForUserV1 missingAdapter missingDirectory body
        failwith "Native artifact linked without its required adapter"
    with :? InvalidOperationException as error ->
        check "missing statically linked provider fails at link time" (error.Message.Contains("al_host_campfire_rooms_find_for_user_v1", StringComparison.Ordinal))
        check "missing provider produces no executable artifact" (not (File.Exists(Path.Combine(missingDirectory, "owning-stack-native-host.dll"))))
    let wrongPrototype = Path.Combine(runRoot, "wrong-room-prototype.c")
    File.WriteAllText(wrongPrototype, "long long al_host_campfire_rooms_find_for_user_v1(void*, long long, long long, unsigned char*, unsigned int, unsigned int*);\n")
    let wrongDirectory = Path.Combine(runRoot, "wrong-room-prototype")
    try
        use invalid = OwningStackAot.compileWithNativeHostProfile toolchain LlvmOptimization.O2 OwningNativeHostProfile.CampfireRoomFindForUserV1 wrongPrototype wrongDirectory body
        failwith "Adapter compiled with an incompatible C ABI"
    with :? InvalidOperationException as error ->
        check "forced header rejects incompatible adapter declaration at C compile time" (error.Message.Contains("conflicting types", StringComparison.Ordinal) && error.Message.Contains("al_host_campfire_rooms_find_for_user_v1", StringComparison.Ordinal))
        check "incompatible adapter ABI produces no executable" (not (File.Exists(Path.Combine(wrongDirectory, "owning-stack-native-host.dll"))))
    // The actual Campfire snapshot retains unrelated operations; admission must
    // follow reachable calls rather than admitting the whole environment.
    check "typed probe retains full application host catalog" ((VerifiedIrProgram.hostOperationContracts (VerifiedIrBody.program body)).Count > 1)
    let state = Marshal.AllocHGlobal(32)
    try
        for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
            let directory = Path.Combine(runRoot, "room-host", string optimization)
            use native = OwningStackAot.compileWithNativeHostProfile toolchain optimization OwningNativeHostProfile.CampfireRoomFindForUserV1 fixture directory body
            use nativeFile = File.OpenRead(native.LibraryPath)
            use nativePe = new PEReader(nativeFile)
            check "host artifact contains no managed runtime header" (isNull nativePe.PEHeaders.CorHeader)
            let manifestPath = native.NativeHostManifestPath |> Option.defaultWith (fun () -> failwith "Missing native host manifest")
            let manifest = JsonNode.Parse(File.ReadAllText(manifestPath))
            let info = manifest["moduleInfo"]
            check "manifest pins host, owning and layout versions" (info["hostAbiVersion"].GetValue<int>() = 1 && info["owningRuntimeAbiVersion"].GetValue<int>() = 2 && info["layoutAbiVersion"].GetValue<int>() = 4)
            check "manifest pins diagnostic runtime profile" (info["runtimeProfile"].GetValue<string>() = "diagnostic")
            check "manifest pins exact operation and bounded output" (info["operationId"].GetValue<string>() = "campfire.rooms.find-for-user" && info["outputLimitBytes"].GetValue<int>() = 65536)
            check "manifest pins adapter source bytes" (info["adapterSourceSha256"].GetValue<string>() = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture))).ToLowerInvariant())
            check "manifest contains fingerprint" (manifest["fingerprint"].GetValue<string>().Length = 64)
            let handle = NativeLibrary.Load(native.LibraryPath)
            try
                let selfTest = Marshal.GetDelegateForFunctionPointer<BoundarySelfTest>(NativeLibrary.GetExport(handle, "al_test_host_commit_boundaries"))
                let failureLine = selfTest.Invoke()
                check $"{optimization} direct commit validation, rollback, prefix ownership and zero-copy checks (failure line {failureLine})" (failureLine = 0)
                verifyRawNullContext handle check
            finally NativeLibrary.Free(handle)
            for mode in [ 0; 1; 2; 3; 4; 5; 15; 16 ] do
                for user, room in [ 7L, 11L; -2L, Int64.MaxValue ] do
                    Marshal.Copy(Array.zeroCreate<byte> 32, 0, state, 32)
                    Marshal.WriteInt32(state, mode)
                    setMode mode
                    let inputs = [ NamedValue("UserId", IntValue user); NamedValue("RoomId", IntValue room) ]
                    let expectedValues = [ expected mode user room ]
                    let interpreted = match bound.Invoke(inputs) with Ok result -> result.Values | Error diagnostic -> failwith diagnostic.Message
                    check $"interpreter independent fixture mode {mode}" (interpreted = expectedValues)
                    let result = native.ExecuteWithNativeHostContext(inputs, 131072, 131072, state)
                    check $"{optimization} host mode {mode} matches independent result" (result.Values = expectedValues)
                    check $"{optimization} host mode {mode} interpreter parity" (result.Values = interpreted)
                    check "native host receives nominal integer values in source order" (Marshal.ReadInt64(state, 8) = user && Marshal.ReadInt64(state, 16) = room)
                    check "native host invoked once within nested entry" (Marshal.ReadInt32(state, 4) = 1)
                    check "native output receives bounded arena window" (Marshal.ReadInt32(state, 24) > 0 && Marshal.ReadInt32(state, 24) <= 65536)
                    check "host commit performs no deep copy or move" (result.Metrics.DeepCopyBytes = 0UL && result.Metrics.MoveBytes = 0UL)
            for mode in [ 6; 7; 8; 9; 10; 11; 12; 13 ] do
                Marshal.Copy(Array.zeroCreate<byte> 32, 0, state, 32)
                Marshal.WriteInt32(state, mode)
                let mutable rejected = false
                try native.ExecuteWithNativeHostContext([ NamedValue("UserId", IntValue 7L); NamedValue("RoomId", IntValue 11L) ], 131072, 131072, state) |> ignore
                with :? OwningStackExecutionException as error ->
                    rejected <- true
                    check "host failure reports source diagnostic" (error.Diagnostic.Word.IsSome && error.Diagnostic.Span.IsSome)
                    check "host failure rewinds temporary reservation" (error.Metrics.FinalCursorBytes < 65536)
                check $"{optimization} malformed/provider-failing host output mode {mode} rejected" rejected
                check "failing host invoked exactly once" (Marshal.ReadInt32(state, 4) = 1)
            let inputs = [ NamedValue("UserId", IntValue 7L); NamedValue("RoomId", IntValue 11L) ]
            Marshal.Copy(Array.zeroCreate<byte> 32, 0, state, 32)
            try
                native.ExecuteWithNativeHostContext(inputs, 131072, 131072, IntPtr.Zero) |> ignore
                failwith "Null provider context accepted"
            with :? ArgumentException -> check "null provider context rejected before native call" (Marshal.ReadInt32(state, 4) = 0)
            try
                native.Execute(inputs, 131072, 131072) |> ignore
                failwith "Effectful native module executed through pure API"
            with :? InvalidOperationException -> check "pure execution API rejects host module" (Marshal.ReadInt32(state, 4) = 0)
            Marshal.WriteInt32(state, 1)
            let small = native.ExecuteWithNativeHostContext(inputs, 1024, 1024, state)
            check "short host value works with sub-profile arena capacity" (small.Values = [ expected 1 7L 11L ])
            // Full generic dispatch must not be admitted by this narrow profile.
            let full = engine.ExportVerifiedFlowEntryPoint("campfire.http.dispatch", [ TNamed "CampfireHttpRequest" ], [ TNamed "CampfireHttpResponse" ])
            let rejectedDirectory = Path.Combine(directory, "rejected-full-dispatch")
            try
                use invalid = OwningStackAot.compileWithNativeHostProfile toolchain optimization OwningNativeHostProfile.CampfireRoomFindForUserV1 fixture rejectedDirectory full
                failwith "Narrow room profile admitted generic dispatcher"
            with LanguageException _ ->
                check "narrow profile rejects wider effect/operation closure before executable emission" (not (File.Exists(Path.Combine(rejectedDirectory, "owning-stack-native-host.dll"))))
        // Keep the source path visibly distinct from a native endpoint claim.
        check "native fixture source is in test tree" (fixture.StartsWith(Path.Combine(repo, "tests"), StringComparison.OrdinalIgnoreCase))
    finally Marshal.FreeHGlobal(state)
