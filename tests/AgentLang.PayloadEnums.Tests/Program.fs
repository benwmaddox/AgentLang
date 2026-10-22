module AgentLang.PayloadEnums.Tests

// Enums whose cases carry one payload value (report 306): parsing, formatting,
// type errors, the attached tests of payload-enums.flow, interpreter entry
// points with payload values in and out, and the native backend's rejection
// until it lays payload enums out.

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open AgentLang
open AgentLang.Llvm

let private repo = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))
let private sourcePath = Path.Combine(__SOURCE_DIRECTORY__, "payload-enums.flow")
let private routesPath = Path.Combine(__SOURCE_DIRECTORY__, "routes.flow")
let private assertionCount = ref 0

let private check label condition =
    assertionCount.Value <- assertionCount.Value + 1
    if not condition then failwith $"Assertion failed: {label}"

let private flowArgs (source: string) =
    let args = JsonObject()
    args["frontend"] <- JsonValue.Create("flow")
    args["syntaxVersion"] <- JsonValue.Create(2)
    args["source"] <- JsonValue.Create(source)
    args

let private wordArgs (name: string) =
    let args = JsonObject()
    args["word"] <- JsonValue.Create(name)
    args

let private words =
    [ "route.describe"; "route.kind"; "route.same?"; "route.room"; "route.held"; "route.all"; "route.maybe" ]

let private enumSource = "enum Shape {\n    circle(Int)\n    named(String)\n    empty\n}\n"

let private syntaxChecks (runRoot: string) =
    let engine = Runtime.Engine(Path.Combine(runRoot, "syntax"), Set.empty, fileSystemMode = FileSystemMode.Virtual)
    let define (source: string) = engine.Dispatch("define", flowArgs source).ToJsonString()
    let accepted label source =
        let response = define source
        check $"{label} is accepted: {response}" (response.Contains("\"ok\":true", StringComparison.Ordinal))
    let rejects label (code: string) source =
        let response = define source
        check $"{label} is {code}: {response}" (response.Contains(code, StringComparison.Ordinal))
    accepted "an enum with payload cases" enumSource
    rejects "a binding on a case without a payload" "FLOW_MATCH_ENUM_BINDING"
        "fn bad.bind(shape: Shape) -> Int {\n    match shape {\n        circle radius => radius\n        named => 0\n        empty nothing => 0\n    }\n}\n"
    rejects "a payload of the wrong type" "FLOW_ARGUMENT_TYPE" "fn bad.payload() -> Shape {\n    Shape.circle(\"wide\")\n}\n"
    rejects "a payload case built without its payload" "FLOW_ARGUMENT" "fn bad.missing() -> Shape {\n    Shape.circle()\n}\n"
    rejects "a match that misses a payload case" "FLOW_MATCH_CASE_MISSING"
        "fn bad.cover(shape: Shape) -> Int {\n    match shape {\n        circle radius => radius\n        empty => 0\n    }\n}\n"
    rejects "a payload used as the wrong type" "FLOW_"
        "fn bad.use(shape: Shape) -> Int {\n    match shape {\n        circle radius => 0\n        named text => text\n        empty => 0\n    }\n}\n"
    accepted "a match binding and ignoring payloads"
        "fn good.area(shape: Shape) -> Int {\n    match shape {\n        circle radius => radius * radius\n        named => 0\n        empty => 0\n    }\n}\n"
    // Formatting keeps payload declarations and bindings.
    let format (source: string) =
        let response = engine.Dispatch("format", flowArgs source)
        check $"format succeeds: {response.ToJsonString()}" (response["ok"].GetValue<bool>())
        (response["data"]["source"]).GetValue<string>()
    let formatted = format (enumSource + "\nfn fmt.area(shape: Shape) -> Int {\n    match shape {\n        circle radius => radius\n        named => 0\n        empty => 0\n    }\n}\n")
    check $"formatter keeps a payload declaration: {formatted}" (formatted.Contains("circle(Int)", StringComparison.Ordinal) && formatted.Contains("named(String)", StringComparison.Ordinal))
    check $"formatter keeps a payload binding: {formatted}" (formatted.Contains("circle radius =>", StringComparison.Ordinal))
    check "formatted source formats to itself" (format formatted = formatted)

/// Typed routes (306c): the generated words commit with their generated tests,
/// rejections name the broken rule, and parse/path agree between the interpreter
/// and native builds.
let private routeChecks (runRoot: string) =
    let syntax = Runtime.Engine(Path.Combine(runRoot, "route-syntax"), Set.empty, fileSystemMode = FileSystemMode.Virtual)
    let rejects label (code: string) source =
        let response = syntax.Dispatch("define", flowArgs source).ToJsonString()
        check $"{label} is {code}: {response}" (response.Contains(code, StringComparison.Ordinal))
    rejects "overlapping paths" "FLOW_ROUTE_OVERLAP" "enum A {\n    one(String) at \"/a/{x}\"\n    two(String) at \"/a/{y}\"\n}\n"
    rejects "a literal inside a prefix parameter" "FLOW_ROUTE_OVERLAP" "enum B {\n    one at \"/b/@me\"\n    two(String) at \"/b/@{x}\"\n}\n"
    rejects "a case without a path" "FLOW_ROUTE_MISSING" "enum C {\n    one at \"/c\"\n    two\n}\n"
    rejects "a parameter on a bare case" "FLOW_ROUTE_PAYLOAD" "enum D {\n    one at \"/d/{x}\"\n}\n"
    rejects "a Bool parameter" "FLOW_ROUTE_PAYLOAD" "enum E {\n    one(Bool) at \"/e/{x}\"\n}\n"
    rejects "two parameters for a String payload" "FLOW_ROUTE_PAYLOAD" "enum F {\n    one(String) at \"/f/{x}/{y}\"\n}\n"
    rejects "a path without its leading slash" "FLOW_ROUTE_PATTERN" "enum G {\n    one at \"g\"\n}\n"
    rejects "an empty segment" "FLOW_ROUTE_PATTERN" "enum H {\n    one at \"/h//x\"\n}\n"
    rejects "a parameter inside a segment" "FLOW_ROUTE_PATTERN" "enum I {\n    one(String) at \"/i/{x}y\"\n}\n"
    rejects "a word named like a generated one" "FLOW_ROUTE_NAME" "enum J {\n    one at \"/j\"\n}\n\nfn j.parse(target: String) -> Int {\n    1\n}\n"
    let format (source: string) =
        let response = syntax.Dispatch("format", flowArgs source)
        check $"format succeeds: {response.ToJsonString()}" (response["ok"].GetValue<bool>())
        (response["data"]["source"]).GetValue<string>()
    let formatted = format (File.ReadAllText routesPath)
    check $"formatter keeps at paths: {formatted}" (formatted.Contains("room(String) at \"/rooms/{room}\"", StringComparison.Ordinal))
    check "formatted routes format to themselves" (format formatted = formatted)

    let engine = Runtime.Engine(Path.Combine(runRoot, "route-engine"), Set.empty, fileSystemMode = FileSystemMode.Virtual)
    let defined = engine.Dispatch("define", flowArgs (File.ReadAllText routesPath))
    if not (defined["ok"].GetValue<bool>()) then failwith (defined.ToJsonString())
    for name in [ "siteRoute.parse"; "siteRoute.path"; "siteRoute.escape"; "site.describe" ] do
        let response = engine.Dispatch("commit", wordArgs name)
        // Committing a word commits the words its tests use, so a later one may already be in.
        let text = response.ToJsonString()
        check $"{name} commits (its generated or attached tests pass): {text}" (response["ok"].GetValue<bool>() || text.Contains("COMMIT_NOT_CANDIDATE", StringComparison.Ordinal))
    let interpret word inputs outputs values =
        match engine.BindFlowEntryPoint(word, inputs, outputs).Invoke(values) with
        | Ok value -> Ok value.Values
        | Error (diagnostic: Diagnostic) -> Error diagnostic.Code
    let route = TNamed "SiteRoute"
    let some value = OptionValue(route, Some value)
    let none = OptionValue(route, None)
    let case name payload = EnumValue("SiteRoute", name, payload)
    let around room message = case "around" (Some(RecordValue("RoomAround", Map.ofList [ "room", StringValue room; "message", StringValue message ])))
    let expectations =
        [ "/", some (case "home" None)
          "/rooms/1", some (case "room" (Some(StringValue "1")))
          "/rooms/%31", some (case "room" (Some(StringValue "%31")))
          "/rooms/", none
          "/rooms/1/", none
          "/Rooms/1", none
          "/rooms/1/@25", some (around "1" "25")
          "/rooms/1/@2.json", some (around "1" "2.json")
          "/rooms/1/@", none
          "/rooms/1/messages", some (case "messages" (Some(StringValue "1")))
          "/rooms/1/MESSAGES", none
          "/users/me/sidebar", some (case "sidebar" None)
          "/users/me/sidebar/", none
          "/users/me/avatar", some (case "avatar" (Some(StringValue "me")))
          "/users//avatar", none
          "/numbers/42", some (case "numbered" (Some(IntValue 42L)))
          "/numbers/x", none
          "/pairs/3/pz", some (case "pair" (Some(RecordValue("Pair", Map.ofList [ "left", IntValue 3L; "right", StringValue "z" ]))))
          "/pairs/3/p", none
          "rooms/1", none
          "", none ]
    for target, expected in expectations do
        check $"parse {target}" (interpret "siteRoute.parse" [ TString ] [ TOption route ] [ StringValue target ] = Ok [ expected ])
    let url text = NamedValue("Url", StringValue text)
    let printed =
        [ case "room" (Some(StringValue "a b/é")), "/rooms/a%20b%2F%C3%A9"
          around "1" "25", "/rooms/1/@25"
          case "numbered" (Some(IntValue -3L)), "/numbers/-3"
          case "home" None, "/" ]
    for value, expected in printed do
        check $"path {value}" (interpret "siteRoute.path" [ route ] [ TNamed "Url" ] [ value ] = Ok [ url expected ])
    let toolchain = LlvmToolchain.discover ()
    // O2 only: at O0 the generated parser of this eight-route table keeps a stack slot
    // per temporary and exceeds the 64 KB frame screen (report 306).
    let configurations =
        [ "O2", LlvmOptimization.O2, OwningRuntimeProfile.Diagnostic
          "trusted-O2", LlvmOptimization.O2, OwningRuntimeProfile.TrustedGenerated ]
    for optimization, level, profile in configurations do
        let pair word inputs outputs (cases: Value list list) =
            let body = engine.ExportVerifiedFlowEntryPoint(word, inputs, outputs)
            use program = OwningStackAot.compileWithRuntimeProfile toolchain level profile (Path.Combine(runRoot, "route-native", optimization, word)) body
            for values in cases do
                let interpreted = interpret word inputs outputs values
                let nativeResult =
                    try Ok(program.Execute(values, 65536, 4096).Values)
                    with :? OwningStackExecutionException as failure -> Error failure.Diagnostic.Code
                check $"{word} {values}/{optimization}: native {nativeResult} = interpreter {interpreted}" (nativeResult = interpreted)
        pair "siteRoute.parse" [ TString ] [ TOption route ] [ for target, _ in expectations -> [ StringValue target ] ]
        pair "siteRoute.path" [ route ] [ TNamed "Url" ] [ for value, _ in printed -> [ value ] ]
        pair "site.describe" [ TString ] [ TString ] [ for target, _ in expectations -> [ StringValue target ] ]

let private run () =
    let runRoot = Path.Combine(repo, ".agentlang", "payload-enums-306", "runs", DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(runRoot) |> ignore
    syntaxChecks runRoot
    routeChecks runRoot
    let engine = Runtime.Engine(Path.Combine(runRoot, "flow-engine"), Set.empty, fileSystemMode = FileSystemMode.Virtual)
    let defined = engine.Dispatch("define", flowArgs (File.ReadAllText sourcePath))
    if not (defined["ok"].GetValue<bool>()) then failwith (defined.ToJsonString())
    for name in words do
        let response = engine.Dispatch("commit", wordArgs name)
        check $"{name} commits (its attached tests pass): {response.ToJsonString()}" (response["ok"].GetValue<bool>())

    let interpret word inputs outputs values =
        match engine.BindFlowEntryPoint(word, inputs, outputs).Invoke(values) with
        | Ok value -> Ok value.Values
        | Error (diagnostic: Diagnostic) -> Error diagnostic.Code
    let route = TNamed "Route"
    let room id = EnumValue("Route", "room", Some(IntValue id))
    let around room message = EnumValue("Route", "around", Some(RecordValue("Around", Map.ofList [ "room", IntValue room; "message", StringValue message ])))
    let search text = EnumValue("Route", "search", Some(OptionValue(TString, text |> Option.map StringValue)))
    let tags names = EnumValue("Route", "tags", Some(ListValue(TString, names |> List.map StringValue)))
    let label text = EnumValue("Route", "label", Some(StringValue text))
    let sidebar = EnumValue("Route", "sidebar", None)
    let routes = [ room 5L; room -1L; around 2L "m9"; around 2L ""; search (Some "cats"); search None; tags [ "a"; "b" ]; tags []; label "x"; label ""; label "é🚀 long enough to pass a word"; sidebar ]
    let describe = function
        | EnumValue(_, "room", Some(IntValue id)) -> $"room {id}"
        | EnumValue(_, "around", Some(RecordValue(_, fields))) ->
            match fields["room"], fields["message"] with
            | IntValue room, StringValue message -> $"around {room}@{message}"
            | _ -> failwith "bad around"
        | EnumValue(_, "search", Some(OptionValue(_, Some(StringValue text)))) -> $"search {text}"
        | EnumValue(_, "search", Some(OptionValue(_, None))) -> "search"
        | EnumValue(_, "tags", Some(ListValue(_, names))) -> $"tags {names.Length}"
        | EnumValue(_, "label", Some(StringValue text)) -> $"label {text}"
        | EnumValue(_, "sidebar", None) -> "sidebar"
        | other -> failwith $"bad route {other}"
    for value in routes do
        check $"route.describe {value}" (interpret "route.describe" [ route ] [ TString ] [ value ] = Ok [ StringValue(describe value) ])
    for left in routes do
        for right in routes do
            check $"route.same? {left} {right}" (interpret "route.same?" [ route; route ] [ TBool ] [ left; right ] = Ok [ BoolValue(left = right) ])
    check "route.room returns a payload value" (interpret "route.room" [ TInt ] [ route ] [ IntValue 9L ] = Ok [ room 9L ])
    check "route.all returns payload values in a list"
        (interpret "route.all" [ TInt ] [ TList route ] [ IntValue 4L ] = Ok [ ListValue(route, [ room 4L; sidebar; tags [ "t" ] ]) ])
    check "route.maybe returns a payload value in an option" (interpret "route.maybe" [ TInt ] [ TOption route ] [ IntValue 2L ] = Ok [ OptionValue(route, Some(room 2L)) ])
    check "route.held stores a payload value in a record"
        (interpret "route.held" [ TString; route ] [ TNamed "Holder" ] [ StringValue "x"; around 1L "a" ] = Ok [ RecordValue("Holder", Map.ofList [ "label", StringValue "x"; "route", around 1L "a" ]) ])
    check "a value lacking its declared payload is rejected" (interpret "route.describe" [ route ] [ TString ] [ EnumValue("Route", "room", None) ] |> Result.isError)
    check "a payload on a bare case is rejected" (interpret "route.describe" [ route ] [ TString ] [ EnumValue("Route", "sidebar", Some(IntValue 1L)) ] |> Result.isError)

    // Native builds lay payload enums out as sums (306b): every entry point gives
    // the interpreter's values, at O0, O2 and trusted O2.
    let toolchain = LlvmToolchain.discover ()
    let configurations =
        [ "O0", LlvmOptimization.O0, OwningRuntimeProfile.Diagnostic
          "O2", LlvmOptimization.O2, OwningRuntimeProfile.Diagnostic
          "trusted-O2", LlvmOptimization.O2, OwningRuntimeProfile.TrustedGenerated ]
    for optimization, level, profile in configurations do
        let pair word inputs outputs (cases: Value list list) =
            let body = engine.ExportVerifiedFlowEntryPoint(word, inputs, outputs)
            use program = OwningStackAot.compileWithRuntimeProfile toolchain level profile (Path.Combine(runRoot, "native", optimization, word.Replace("?", "-p"))) body
            for values in cases do
                let interpreted = interpret word inputs outputs values
                let nativeResult =
                    try Ok(program.Execute(values, 65536, 4096).Values)
                    with
                    | :? OwningStackExecutionException as failure -> Error failure.Diagnostic.Code
                check $"{word} {values}/{optimization}: native {nativeResult} = interpreter {interpreted}" (nativeResult = interpreted)
        pair "route.describe" [ route ] [ TString ] [ for value in routes -> [ value ] ]
        pair "route.kind" [ route ] [ TInt ] [ for value in routes -> [ value ] ]
        pair "route.same?" [ route; route ] [ TBool ] [ for left in routes do for right in routes -> [ left; right ] ]
        pair "route.room" [ TInt ] [ route ] [ [ IntValue 9L ]; [ IntValue Int64.MinValue ] ]
        pair "route.all" [ TInt ] [ TList route ] [ [ IntValue 4L ] ]
        pair "route.maybe" [ TInt ] [ TOption route ] [ [ IntValue 2L ]; [ IntValue 0L ] ]
        pair "route.held" [ TString; route ] [ TNamed "Holder" ] [ for value in routes -> [ StringValue "x"; value ] ]

    let receipt = JsonObject()
    receipt["status"] <- JsonValue.Create("pass")
    receipt["assertionCount"] <- JsonValue.Create(assertionCount.Value)
    File.WriteAllText(Path.Combine(runRoot, "receipt.json"), receipt.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + Environment.NewLine)
    printfn "payload enums: %d assertions passed; artifacts %s" assertionCount.Value runRoot

[<EntryPoint>]
let main _ =
    try
        run ()
        0
    with error ->
        eprintfn "%s" (error.ToString())
        1
