module AgentLang.Aot.EntryExport.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Reflection.PortableExecutable
open System.Text.Json.Nodes
open AgentLang
open AgentLang.Llvm
open AgentLang.Campfire.Http
open AgentLang.Campfire.Host
open AgentLang.Campfire.Authentication.Host
open AgentLang.Campfire.Browser.Host

let private repo = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))
let private runRoot = Path.Combine(repo, ".agentlang", "campfire-aot-export-246", "runs", DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N"))
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
    let provider: HostOperationProvider =
        { OperationIds = manifest |> Map.toSeq |> Seq.map fst |> Set.ofSeq
          Invoke = fun _ _ -> providerCalls <- providerCalls + 1; Error HostOperationFailure.InvocationFailed
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

[<EntryPoint>]
let main _ =
    Directory.CreateDirectory(runRoot) |> ignore
    let mutable failure = None
    try
        let toolchain = LlvmToolchain.discover()
        verifyPureExport toolchain
        verifyActualCampfireExport toolchain
    with error -> failure <- Some(error.ToString())
    let receipt = JsonObject()
    receipt["passed"] <- JsonValue.Create(failure.IsNone)
    receipt["assertions"] <- JsonValue.Create(assertions)
    receipt["failure"] <- (match failure with Some text -> JsonValue.Create(text) :> JsonNode | None -> null)
    let pins = JsonObject()
    for relative in [ "src/AgentLang.Core/Runtime.fs"; "tests/AgentLang.Aot.EntryExport.Tests/Program.fs"; "src/AgentLang.Llvm/OwningStackAot.fs"; "applications/campfire/Http/Application.fs" ] do
        pins[relative] <- JsonValue.Create(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(repo, relative)))).ToLowerInvariant())
    receipt["sourceSha256"] <- pins
    File.WriteAllText(Path.Combine(runRoot, "receipt.json"), receipt.ToJsonString() + Environment.NewLine)
    printfn "AOT entry export: %d assertions; passed=%b; evidence=%s" assertions failure.IsNone runRoot
    match failure with
    | None -> 0
    | Some text -> eprintfn "%s" text; 1
