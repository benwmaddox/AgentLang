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

let private run () =
    let runRoot = Path.Combine(repo, ".agentlang", "payload-enums-306", "runs", DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(runRoot) |> ignore
    syntaxChecks runRoot
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
    let sidebar = EnumValue("Route", "sidebar", None)
    let routes = [ room 5L; room -1L; around 2L "m9"; around 2L ""; search (Some "cats"); search None; tags [ "a"; "b" ]; tags []; sidebar ]
    let describe = function
        | EnumValue(_, "room", Some(IntValue id)) -> $"room {id}"
        | EnumValue(_, "around", Some(RecordValue(_, fields))) ->
            match fields["room"], fields["message"] with
            | IntValue room, StringValue message -> $"around {room}@{message}"
            | _ -> failwith "bad around"
        | EnumValue(_, "search", Some(OptionValue(_, Some(StringValue text)))) -> $"search {text}"
        | EnumValue(_, "search", Some(OptionValue(_, None))) -> "search"
        | EnumValue(_, "tags", Some(ListValue(_, names))) -> $"tags {names.Length}"
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

    // Native builds reject payload enums until they lay them out.
    let toolchain = LlvmToolchain.discover ()
    let body = engine.ExportVerifiedFlowEntryPoint("route.describe", [ route ], [ TString ])
    let nativeError =
        try
            use _program = OwningStackAot.compileWithRuntimeProfile toolchain LlvmOptimization.O0 OwningRuntimeProfile.Diagnostic (Path.Combine(runRoot, "native", "describe")) body
            "compiled"
        with
        | LanguageException diagnostic -> diagnostic.Code
    check $"native builds reject a payload enum ({nativeError})" (nativeError = "IR_OWNING_STACK_ENUM_PAYLOAD")

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
