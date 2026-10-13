module AgentLang.OwningLists.Tests

open System
open System.Buffers.Binary
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open AgentLang
open AgentLang.Llvm

type private NativeCase =
    { Name: string
      Word: string
      Inputs: Value list
      Expected: Value list }

let private repo = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))
let private sourcePath = Path.Combine(__SOURCE_DIRECTORY__, "owning-lists.flow")
let private fixturePath = Path.Combine(__SOURCE_DIRECTORY__, "../fixtures/native-conformance/native-value-stack.json")
let private assertionCount = ref 0

let private check label condition =
    assertionCount.Value <- assertionCount.Value + 1
    if not condition then failwith $"Assertion failed: {label}"

let private dispatch (engine: Runtime.Engine) operation (args: JsonObject) =
    let result = engine.Dispatch(operation, args)
    if not (result["ok"].GetValue<bool>()) then failwith (result.ToJsonString())
    result

let private prepare (engine: Runtime.Engine) (source: string) =
    let args = JsonObject()
    args["frontend"] <- JsonValue.Create("flow")
    args["syntaxVersion"] <- JsonValue.Create(2)
    args["source"] <- JsonValue.Create(source)
    dispatch engine "define" args |> ignore
    for name in [
        "native.lists.echo-rows"
        "native.lists.empty-rows"
        "native.lists.echo-nested-strings"
        "native.lists.echo-pages"
        "native.lists.echo-units"
        "native.lists.int-format"
    ] do
        let commit = JsonObject()
        commit["word"] <- JsonValue.Create(name)
        dispatch engine "commit" commit |> ignore

let private messageRow messageId roomId authorId body clientId tags =
    RecordValue("MessageRow", Map.ofList [
        "id", NamedValue("MessageId", IntValue messageId)
        "roomId", NamedValue("RoomId", IntValue roomId)
        "authorId", NamedValue("UserId", IntValue authorId)
        "body", OptionValue(TString, body |> Option.map StringValue)
        "clientMessageId", OptionValue(TString, clientId |> Option.map StringValue)
        "tags", ListValue(TString, tags |> List.map StringValue)
    ])

let private page rows cursor query =
    RecordValue("MessagePage", Map.ofList [
        "rows", ListValue(TNamed "MessageRow", rows)
        "cursor", cursor
        "queryResult", query
    ])

let private makeCase name word inputs expected =
    { Name = name; Word = word; Inputs = inputs; Expected = expected }

let private cases () =
    let row1 = messageRow 1L 8L 13L (Some "first λ🚀") None [ "mention"; "café" ]
    let row2 = messageRow -2L 8L 21L None (Some "client-𐐷") []
    let row3 = messageRow Int64.MaxValue Int64.MinValue 0L (Some "") (Some "") [ "one" ]
    let rows = [ row1; row2; row3 ]
    let inner = ListValue(TString, [ StringValue ""; StringValue "inner λ"; StringValue "🚀" ])
    let pages = [
        page [ row1; row2 ] (OptionValue(TList TString, Some inner)) (ResultValue(TList(TNamed "MessageRow"), TString, Ok(ListValue(TNamed "MessageRow", [ row3 ]))))
        page [] (OptionValue(TList TString, None)) (ResultValue(TList(TNamed "MessageRow"), TString, Error(StringValue "empty-page error")))
    ]
    [
        makeCase "empty-list-function" "native.lists.empty-rows" [] [ ListValue(TNamed "MessageRow", []) ]
        makeCase "empty-input-list" "native.lists.echo-rows" [ ListValue(TNamed "MessageRow", []) ] [ ListValue(TNamed "MessageRow", []) ]
        makeCase "nominal-records-options-and-string-list" "native.lists.echo-rows" [ ListValue(TNamed "MessageRow", rows) ] [ ListValue(TNamed "MessageRow", rows) ]
        makeCase "nested-list-with-empty-and-unicode-elements" "native.lists.echo-nested-strings" [ ListValue(TList TString, [ ListValue(TString, []); inner; ListValue(TString, [ StringValue "last" ]) ]) ] [ ListValue(TList TString, [ ListValue(TString, []); inner; ListValue(TString, [ StringValue "last" ]) ]) ]
        makeCase "record-list-containers-option-and-result" "native.lists.echo-pages" [ ListValue(TNamed "MessagePage", pages) ] [ ListValue(TNamed "MessagePage", pages) ]
        makeCase "zero-width-unit-roots" "native.lists.echo-units" [ ListValue(TUnit, [ UnitValue; UnitValue; UnitValue ]) ] [ ListValue(TUnit, [ UnitValue; UnitValue; UnitValue ]) ]
    ]

let private intCases () =
    let boundaryValues = [
        Int64.MinValue; -1_000_000_000_000_000_000L; -10L; -1L; 0L; 1L; 10L
        999_999_999_999_999_999L; Int64.MaxValue
    ]
    let random = Random(251)
    let generated =
        Array.init 256 (fun _ ->
            let bytes = Array.zeroCreate<byte> 8
            random.NextBytes(bytes)
            BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan()))
        |> Array.toList
    boundaryValues @ generated

let private u32 (value: uint32) =
    let bytes = Array.zeroCreate<byte> 4
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(), value)
    bytes

let private u64 (value: uint64) =
    let bytes = Array.zeroCreate<byte> 8
    BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(), value)
    bytes

let private i64 (value: int64) =
    let bytes = Array.zeroCreate<byte> 8
    BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(), value)
    bytes

let private zeroHeader = Array.zeroCreate<byte> 8

let private recordFields =
    Map.ofList [
        "MessageRow", [ "id", TNamed "MessageId"; "roomId", TNamed "RoomId"; "authorId", TNamed "UserId"; "body", TOption TString; "clientMessageId", TOption TString; "tags", TList TString ]
        "MessagePage", [ "rows", TList(TNamed "MessageRow"); "cursor", TOption(TList TString); "queryResult", TResult(TList(TNamed "MessageRow"), TString) ]
    ]

let rec private encodeWire ty value =
    let align8 (bytes: byte array) =
        let padding = (8 - bytes.Length % 8) % 8
        if padding = 0 then bytes else Array.append bytes (Array.zeroCreate<byte> padding)
    let join (parts: byte array list) = Array.concat parts
    match ty, value with
    | TInt, IntValue number -> i64 number
    | TUnit, UnitValue -> zeroHeader
    | TString, StringValue text ->
        let content = Encoding.UTF8.GetBytes(text)
        align8 (join [ u32 (uint32 content.Length); Array.zeroCreate<byte> 4; content ])
    | TNamed name, NamedValue(actualName, inner) when actualName = name && (name = "MessageId" || name = "RoomId" || name = "UserId") ->
        encodeWire TInt inner
    | TNamed name, RecordValue(actualName, fields) when actualName = name && recordFields.ContainsKey(name) ->
        recordFields[name]
        |> List.map (fun (fieldName, fieldType) -> encodeWire fieldType fields[fieldName])
        |> join
    | TList itemType, ListValue(storedItemType, items) when storedItemType = itemType ->
        join ([ u32 (uint32 items.Length); Array.zeroCreate<byte> 4 ] @ (items |> List.map (encodeWire itemType)))
    | TOption childType, OptionValue(storedChildType, item) when storedChildType = childType ->
        match item with
        | None -> u64 1UL
        | Some child -> join [ u64 0UL; encodeWire childType child ]
    | TResult(okType, errorType), ResultValue(storedOkType, storedErrorType, result)
        when storedOkType = okType && storedErrorType = errorType ->
        match result with
        | Ok child -> join [ u64 0UL; encodeWire okType child ]
        | Error child -> join [ u64 1UL; encodeWire errorType child ]
    | _ -> failwith $"The static owning-list wire oracle does not cover {ty} / {value}."

let private outputType word =
    match word with
    | "native.lists.empty-rows" | "native.lists.echo-rows" -> TList(TNamed "MessageRow")
    | "native.lists.echo-nested-strings" -> TList(TList TString)
    | "native.lists.echo-pages" -> TList(TNamed "MessagePage")
    | "native.lists.echo-units" -> TList TUnit
    | _ -> failwith $"No list output type oracle for {word}."

let private directWireChecks (listOracle: JsonNode) =
    // These frozen byte strings are the direct ABI5 list-header/root oracles;
    // the broader composite cases are checked against the independent Flow interpreter.
    let threeUnits = Array.concat [ u32 3u; Array.zeroCreate<byte> 4; Array.zeroCreate<byte> 24 ]
    let oneEmptyString = Array.concat [ u32 1u; Array.zeroCreate<byte> 4; u32 0u; Array.zeroCreate<byte> 4 ]
    check "empty List root is exactly an eight-byte zero header" (Convert.ToHexString(zeroHeader).ToLowerInvariant() = listOracle.["emptyList"].["retainedBytesHex"].GetValue<string>())
    check "three zero-width Unit children occupy their 32-byte root table" (threeUnits.Length = 32 && Convert.ToHexString(threeUnits).ToLowerInvariant() = listOracle.["zeroWidthUnitList"].["retainedBytesHex"].GetValue<string>())
    check "empty String list element carries its own zero-length string header" (Convert.ToHexString(oneEmptyString).ToLowerInvariant() = listOracle.["emptyStringList"].["retainedBytesHex"].GetValue<string>())

let private hash path =
    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()

let private repositoryRelative path = Path.GetRelativePath(repo, path).Replace('\\', '/')

let private artifactRoot () =
    let args = Environment.GetCommandLineArgs()
    match args |> Array.tryFindIndex ((=) "--artifacts-path") with
    | Some index when index + 1 < args.Length -> Path.GetFullPath(args[index + 1])
    | Some _ -> invalidArg "--artifacts-path" "An artifact path must follow the option."
    | None -> Path.Combine(repo, ".agentlang", "campfire-native-lists-251", "runs", DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N"))

let private writeReceipt (runRoot: string) (sourcePins: {| path: string; sha256: string |} list) (caseNames: string list) (intValueCount: int) (optimizations: LlvmOptimization list) =
    let receipt = {| schemaVersion = 1
                     status = "pass"
                     assertionCount = assertionCount.Value
                     caseCount = caseNames.Length
                     intFormattingValueCount = intValueCount
                     optimizations = (optimizations |> List.map string)
                     owningLayoutAbiVersion = 5
                     moduleAbiVersion = 2
                     owningStackAbiVersion = 2
                     listDescriptorKind = 10
                     cases = caseNames
                     sourcePins = sourcePins |}
    let options = JsonSerializerOptions(WriteIndented = true)
    File.WriteAllText(Path.Combine(runRoot, "receipt.json"), JsonSerializer.Serialize(receipt, options) + Environment.NewLine)

let private layoutForList (native: OwningStackCompiledProgram) =
    native.Layouts
    |> List.filter (fun layout -> match layout.Type with IrList _ -> true | _ -> false)

let private executeCase (engine: Runtime.Engine) toolchain runRoot optimization (testCase: NativeCase) =
    let inputTypes, outputTypes =
        match testCase.Word with
        | "native.lists.empty-rows" -> [], [ TList(TNamed "MessageRow") ]
        | "native.lists.echo-rows" -> [ TList(TNamed "MessageRow") ], [ TList(TNamed "MessageRow") ]
        | "native.lists.echo-nested-strings" -> [ TList(TList TString) ], [ TList(TList TString) ]
        | "native.lists.echo-pages" -> [ TList(TNamed "MessagePage") ], [ TList(TNamed "MessagePage") ]
        | "native.lists.echo-units" -> [ TList TUnit ], [ TList TUnit ]
        | _ -> failwith $"Unknown list conformance word: {testCase.Word}"
    let body = engine.ExportVerifiedFlowEntryPoint(testCase.Word, inputTypes, outputTypes)
    let bound = engine.BindFlowEntryPoint(testCase.Word, inputTypes, outputTypes)
    let interpreted =
        match bound.Invoke(testCase.Inputs) with
        | Ok value -> value.Values
        | Error diagnostic -> failwith $"Interpreter rejected {testCase.Name}: {diagnostic.Message}"
    check $"{testCase.Name}: interpreter expected values" (interpreted = testCase.Expected)
    let nativeDirectory = Path.Combine(runRoot, "native", optimization.ToString(), testCase.Name)
    use native = OwningStackAot.compile toolchain optimization nativeDirectory body
    let result = native.Execute(testCase.Inputs, 1_048_576, 1_048_576)
    check $"{testCase.Name}/{optimization}: layout schema is ABI5" (result.LayoutSchemaVersion = 5)
    check $"{testCase.Name}/{optimization}: native values equal interpreter and fixture" (result.Values = interpreted && result.Values = testCase.Expected)
    check $"{testCase.Name}/{optimization}: retained extent equals bytes written" (result.RetainedBytesWritten = result.RetainedOutputBytes.Length)
    let wireOracle = encodeWire (outputType testCase.Word) (List.head testCase.Expected)
    check $"{testCase.Name}/{optimization}: exact retained bytes match explicit wire oracle" (result.RetainedOutputBytes = wireOracle)
    let listLayouts = layoutForList native
    check $"{testCase.Name}/{optimization}: List descriptor has an eight-byte minimum root" (listLayouts |> List.exists (fun layout -> layout.MinimumExtentBytes = 8))
    check $"{testCase.Name}/{optimization}: emitted type table contains ABI kind 10" (native.LlvmIr.Contains("i32 10", StringComparison.Ordinal))
    let exact = native.Execute(testCase.Inputs, 1_048_576, result.RetainedBytesWritten)
    check $"{testCase.Name}/{optimization}: exact retained capacity succeeds" (exact.Values = testCase.Expected && exact.RetainedBytesWritten = result.RetainedBytesWritten)
    let tooSmallRejected =
        if result.RetainedBytesWritten = 0 then false
        else
            try
                native.Execute(testCase.Inputs, 1_048_576, result.RetainedBytesWritten - 1) |> ignore
                false
            with :? OwningStackCapacityException as error ->
                check $"{testCase.Name}/{optimization}: short retained capacity identifies the retained boundary" (error.Code = "OWNING_RETAINED_CAPACITY" && error.Boundary = "retained-output" && error.RequiredBytes = int64 result.RetainedBytesWritten && error.AvailableBytes = int64 (result.RetainedBytesWritten - 1))
                true
    check $"{testCase.Name}/{optimization}: one-byte-short retained capacity is rejected" tooSmallRejected

let private executeIntFormatting (engine: Runtime.Engine) toolchain runRoot optimization =
    let word = "native.lists.int-format"
    let inputTypes = [ TInt ]
    let outputTypes = [ TString ]
    let body = engine.ExportVerifiedFlowEntryPoint(word, inputTypes, outputTypes)
    let bound = engine.BindFlowEntryPoint(word, inputTypes, outputTypes)
    let nativeDirectory = Path.Combine(runRoot, "native", optimization.ToString(), "int-format")
    use native = OwningStackAot.compile toolchain optimization nativeDirectory body
    let values = intCases ()
    for index, value in values |> List.indexed do
        let expectedText = value.ToString(CultureInfo.InvariantCulture)
        let expected = StringValue expectedText
        let interpreted =
            match bound.Invoke([ IntValue value ]) with
            | Ok result -> result.Values
            | Error diagnostic -> failwith $"Interpreter rejected int.to-string({value}): {diagnostic.Message}"
        check $"int-format/{optimization}/{index}: interpreter oracle" (interpreted = [ expected ])
        let result = native.Execute([ IntValue value ], 4096, 128)
        check $"int-format/{optimization}/{index}: native matches interpreter and invariant signed-decimal oracle" (result.Values = interpreted && result.Values = [ expected ])
        check $"int-format/{optimization}/{index}: exact retained output bytes for UTF-8 String" (result.RetainedOutputBytes = (u32 (uint32 (Encoding.UTF8.GetByteCount expectedText)) |> fun header -> Array.concat [ header; Array.zeroCreate<byte> 4; Encoding.UTF8.GetBytes(expectedText); Array.zeroCreate<byte> ((8 - ((8 + Encoding.UTF8.GetByteCount(expectedText)) % 8)) % 8) ]))
    values.Length

let private run () =
    let runRoot = artifactRoot ()
    Directory.CreateDirectory(runRoot) |> ignore
    let fixture = JsonNode.Parse(File.ReadAllText(fixturePath))
    let listOracle = fixture["listConformance"]
    check "fixture pins layout schema ABI5 and List kind 10" (listOracle["layoutAbiVersion"].GetValue<int>() = 5 && listOracle["layoutSchemaVersion"].GetValue<int>() = 5 && listOracle["descriptorKind"].GetValue<int>() = 10)
    check "fixture pins the stable module and stack ABIs" (listOracle["moduleAbiVersion"].GetValue<int>() = 2 && listOracle["stackAbiVersion"].GetValue<int>() = 2)
    check "fixture pins rejection of stale layout ABI4 modules" (listOracle.["abiCompatibility"].["abi4LayoutRejected"].GetValue<bool>())
    directWireChecks listOracle
    let engine = Runtime.Engine(Path.Combine(runRoot, "flow-engine"), Set.empty, fileSystemMode = FileSystemMode.Virtual)
    let source = File.ReadAllText(sourcePath)
    prepare engine source
    let attachedTestWords =
        [ "native.lists.echo-rows"
          "native.lists.empty-rows"
          "native.lists.echo-nested-strings"
          "native.lists.echo-pages"
          "native.lists.echo-units"
          "native.lists.int-format" ]
    for word in attachedTestWords do
        let testArgs = JsonObject()
        testArgs["word"] <- JsonValue.Create(word)
        let testResult = dispatch engine "test" testArgs
        let summary = testResult["data"]["testSummary"]
        check $"{word} has passing authored Flow tests" (summary["executed"].GetValue<int>() > 0 && summary["passed"].GetValue<int>() = summary["executed"].GetValue<int>() && summary["failed"].GetValue<int>() = 0)
    let toolchain = LlvmToolchain.discover ()
    let testCases = cases ()
    let intOracle = listOracle["intToString"]
    let expectedBoundaries = intCases () |> List.take 9 |> List.map (fun value -> value.ToString(CultureInfo.InvariantCulture))
    let fixtureBoundaries = intOracle["boundaryValues"].AsArray() |> Seq.map (fun value -> value.GetValue<string>()) |> List.ofSeq
    check "fixture list-case count matches the active Flow conformance matrix" (listOracle.["coverage"].["fixtureCaseCount"].GetValue<int>() = testCases.Length)
    check "fixture pins the signed Int64 boundary oracle" (fixtureBoundaries = expectedBoundaries)
    check "fixture pins the deterministic Int64 corpus seed and size" (intOracle["randomSeed"].GetValue<int>() = 251 && intOracle["randomValueCount"].GetValue<int>() = 256)
    let mutable intCount = 0
    let optimizations = [ LlvmOptimization.O0; LlvmOptimization.O2 ]
    for optimization in optimizations do
        for testCase in testCases do executeCase engine toolchain runRoot optimization testCase
        intCount <- intCount + executeIntFormatting engine toolchain runRoot optimization
    let sourcePins =
        [ sourcePath
          Path.Combine(__SOURCE_DIRECTORY__, "Program.fs")
          Path.Combine(__SOURCE_DIRECTORY__, "AgentLang.OwningLists.Tests.fsproj")
          fixturePath
          Path.Combine(repo, "src/AgentLang.Llvm/OwningStackAot.fs")
          Path.Combine(repo, "src/AgentLang.Llvm/ArenaLifetime.fs")
          Path.Combine(repo, "src/AgentLang.Llvm/native/owning_stack_runtime.h")
          Path.Combine(repo, "src/AgentLang.Llvm/native/owning_stack_runtime.c") ]
        |> List.map (fun path -> {| path = repositoryRelative path; sha256 = hash path |})
    let caseNames = testCases |> List.map (fun item -> item.Name) |> List.append [ "int-format-boundaries-and-seeded-random" ]
    writeReceipt runRoot sourcePins caseNames intCount optimizations
    printfn "owning lists: %d assertions passed; %d list cases; %d int-format values; artifacts %s" assertionCount.Value testCases.Length intCount runRoot

[<EntryPoint>]
let main _ =
    try
        run ()
        0
    with error ->
        eprintfn "%s" (error.ToString())
        1
