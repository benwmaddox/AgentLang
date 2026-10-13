module AgentLang.OwningListOps.Tests

// Owning AOT list traversal conformance: ListSingleton, ListMap, ListFilter,
// ListEach and ListFold against the interpreter at O0/O2, explicit wire bytes,
// and the no-implicit-relocation rule (MoveBytes stays zero; copies are bounded).

open System
open System.Buffers.Binary
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open AgentLang
open AgentLang.Llvm

type private Case =
    { Name: string
      Word: string
      InputTypes: LangType list
      OutputTypes: LangType list
      Inputs: Value list
      Expected: Value list
      /// Upper bound on deep-copied bytes for this case (None = record only).
      MaxDeepCopyBytes: int option }

let private repo = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))
let private sourcePath = Path.Combine(__SOURCE_DIRECTORY__, "owning-list-ops.flow")
let private assertionCount = ref 0

let private check label condition =
    assertionCount.Value <- assertionCount.Value + 1
    if not condition then failwith $"Assertion failed: {label}"

let private dispatch (engine: Runtime.Engine) operation (args: JsonObject) =
    let result = engine.Dispatch(operation, args)
    if not (result["ok"].GetValue<bool>()) then failwith (result.ToJsonString())
    result

let private words =
    [ "native.listops.double"; "native.listops.map-double"; "native.listops.format"; "native.listops.map-format"
      "native.listops.same"; "native.listops.map-same"; "native.listops.pair"; "native.listops.map-pairs"
      "native.listops.positive"; "native.listops.filter-positive"; "native.listops.plus"; "native.listops.sum"
      "native.listops.join"; "native.listops.join-all"; "native.listops.map-join"; "native.listops.ignore"
      "native.listops.each-ignore"; "native.listops.singleton" ]

let private prepare (engine: Runtime.Engine) =
    let args = JsonObject()
    args["frontend"] <- JsonValue.Create("flow")
    args["syntaxVersion"] <- JsonValue.Create(2)
    args["source"] <- JsonValue.Create(File.ReadAllText(sourcePath))
    dispatch engine "define" args |> ignore
    for name in words do
        let commit = JsonObject()
        commit["word"] <- JsonValue.Create(name)
        dispatch engine "commit" commit |> ignore
    for name in words do
        let testArgs = JsonObject()
        testArgs["word"] <- JsonValue.Create(name)
        let response = dispatch engine "test" testArgs
        let summary = response["data"]["testSummary"]
        check $"{name} has passing authored Flow tests" (
            summary["executed"].GetValue<int>() > 0
            && summary["passed"].GetValue<int>() = summary["executed"].GetValue<int>()
            && summary["failed"].GetValue<int>() = 0)

let private ints values = ListValue(TInt, values |> List.map IntValue)
let private strings values = ListValue(TString, values |> List.map StringValue)
let private pair (name: string) count = RecordValue("Pair", Map.ofList [ "name", StringValue name; "count", IntValue count ])

let private u32 (value: uint32) =
    let bytes = Array.zeroCreate<byte> 4
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(), value)
    bytes

let private i64 (value: int64) =
    let bytes = Array.zeroCreate<byte> 8
    BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(), value)
    bytes

/// Independent wire oracle for the owning layout (ABI5): Int 8 bytes; String
/// u32 code units + 4 zero bytes + UTF-16 padded to 8; List u32 count + 4 zero
/// bytes + packed elements; records concatenate fields; Unit/Bool 8 bytes.
let rec private wire ty value =
    let align8 (bytes: byte array) =
        let padding = (8 - bytes.Length % 8) % 8
        if padding = 0 then bytes else Array.append bytes (Array.zeroCreate<byte> padding)
    match ty, value with
    | TInt, IntValue number -> i64 number
    | TBool, BoolValue flag -> i64 (if flag then 1L else 0L)
    | TUnit, UnitValue -> Array.zeroCreate<byte> 8
    | TString, StringValue text ->
        align8 (Array.concat [ u32 (uint32 text.Length); Array.zeroCreate<byte> 4; Encoding.Unicode.GetBytes(text) ])
    | TNamed "Pair", RecordValue("Pair", fields) -> Array.append (wire TString fields["name"]) (wire TInt fields["count"])
    | TList itemType, ListValue(storedType, items) when storedType = itemType ->
        Array.concat ([ u32 (uint32 items.Length); Array.zeroCreate<byte> 4 ] @ (items |> List.map (wire itemType)))
    | _ -> failwith $"No wire oracle for {ty} / {value}."

let private cases () =
    let manyInts = [ 0L; 1L; -1L; 7L; Int64.MaxValue; Int64.MinValue; 42L; -900L ]
    let unicode = [ ""; "λ"; "🚀 rocket"; "plain text"; "𐐷"; "ok" ]
    let nested = [ []; [ "a" ]; [ "b"; "c"; "λ" ]; [ ""; "" ] ]
    let joined = nested |> List.map (String.concat "")
    let positives = manyInts |> List.filter (fun value -> value > 0L)
    let formatted = manyInts |> List.map (fun value -> value.ToString(Globalization.CultureInfo.InvariantCulture))
    let stringExtent (text: string) = (wire TString (StringValue text)).Length
    [ { Name = "singleton-string"; Word = "native.listops.singleton"; InputTypes = [ TString ]; OutputTypes = [ TList TString ]
        Inputs = [ StringValue "🚀 one" ]; Expected = [ strings [ "🚀 one" ] ]
        MaxDeepCopyBytes = Some(stringExtent "🚀 one") }
      { Name = "map-double-empty"; Word = "native.listops.map-double"; InputTypes = [ TList TInt ]; OutputTypes = [ TList TInt ]
        Inputs = [ ints [] ]; Expected = [ ints [] ]; MaxDeepCopyBytes = Some 0 }
      { Name = "map-double-many"; Word = "native.listops.map-double"; InputTypes = [ TList TInt ]; OutputTypes = [ TList TInt ]
        Inputs = [ ints [ 1L; -2L; 3L; 1_000_000L ] ]; Expected = [ ints [ 2L; -4L; 6L; 2_000_000L ] ]
        MaxDeepCopyBytes = Some(8 * 4) }
      { Name = "map-format-many"; Word = "native.listops.map-format"; InputTypes = [ TList TInt ]; OutputTypes = [ TList TString ]
        Inputs = [ ints manyInts ]; Expected = [ strings formatted ]
        MaxDeepCopyBytes = Some(formatted |> List.sumBy stringExtent) }
      { Name = "map-identity-copies-once"; Word = "native.listops.map-same"; InputTypes = [ TList TString ]; OutputTypes = [ TList TString ]
        Inputs = [ strings unicode ]; Expected = [ strings unicode ]
        MaxDeepCopyBytes = Some(unicode |> List.sumBy stringExtent) }
      { Name = "map-records"; Word = "native.listops.map-pairs"; InputTypes = [ TList TInt ]; OutputTypes = [ TList(TNamed "Pair") ]
        Inputs = [ ints [ 5L; -6L; 70L ] ]; Expected = [ ListValue(TNamed "Pair", [ pair "5" 5L; pair "-6" -6L; pair "70" 70L ]) ]
        MaxDeepCopyBytes = None }
      { Name = "filter-mixed"; Word = "native.listops.filter-positive"; InputTypes = [ TList TInt ]; OutputTypes = [ TList TInt ]
        Inputs = [ ints manyInts ]; Expected = [ ints positives ]; MaxDeepCopyBytes = Some(8 * positives.Length) }
      { Name = "filter-none-kept"; Word = "native.listops.filter-positive"; InputTypes = [ TList TInt ]; OutputTypes = [ TList TInt ]
        Inputs = [ ints [ 0L; -5L ] ]; Expected = [ ints [] ]; MaxDeepCopyBytes = Some 0 }
      { Name = "fold-sum"; Word = "native.listops.sum"; InputTypes = [ TList TInt ]; OutputTypes = [ TInt ]
        Inputs = [ ints [ 1L; 2L; 3L; 4L; 5L ] ]; Expected = [ IntValue 15L ]; MaxDeepCopyBytes = Some 0 }
      { Name = "fold-sum-empty-returns-seed"; Word = "native.listops.sum"; InputTypes = [ TList TInt ]; OutputTypes = [ TInt ]
        Inputs = [ ints [] ]; Expected = [ IntValue 0L ]; MaxDeepCopyBytes = Some 0 }
      { Name = "fold-join-strings"; Word = "native.listops.join-all"; InputTypes = [ TList TString ]; OutputTypes = [ TString ]
        Inputs = [ strings unicode ]; Expected = [ StringValue(String.concat "" unicode) ]; MaxDeepCopyBytes = None }
      { Name = "map-nested-fold"; Word = "native.listops.map-join"; InputTypes = [ TList(TList TString) ]; OutputTypes = [ TList TString ]
        Inputs = [ ListValue(TList TString, nested |> List.map strings) ]; Expected = [ strings joined ]; MaxDeepCopyBytes = None }
      { Name = "each-unit"; Word = "native.listops.each-ignore"; InputTypes = [ TList TInt ]; OutputTypes = [ TUnit ]
        Inputs = [ ints [ 1L; 2L; 3L ] ]; Expected = [ UnitValue ]; MaxDeepCopyBytes = Some 0 } ]

let private hash path = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()

let private artifactRoot () =
    let args = Environment.GetCommandLineArgs()
    match args |> Array.tryFindIndex ((=) "--artifacts-path") with
    | Some index when index + 1 < args.Length -> Path.GetFullPath(args[index + 1])
    | _ -> Path.Combine(repo, ".agentlang", "owning-list-ops-260", "runs", DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N"))

let private run () =
    let runRoot = artifactRoot ()
    Directory.CreateDirectory(runRoot) |> ignore
    let engine = Runtime.Engine(Path.Combine(runRoot, "flow-engine"), Set.empty, fileSystemMode = FileSystemMode.Virtual)
    prepare engine
    let toolchain = LlvmToolchain.discover ()
    let metrics = JsonArray()
    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        for case in cases () do
            let body = engine.ExportVerifiedFlowEntryPoint(case.Word, case.InputTypes, case.OutputTypes)
            let bound = engine.BindFlowEntryPoint(case.Word, case.InputTypes, case.OutputTypes)
            let interpreted =
                match bound.Invoke(case.Inputs) with
                | Ok value -> value.Values
                | Error diagnostic -> failwith $"Interpreter rejected {case.Name}: {diagnostic.Message}"
            check $"{case.Name}: interpreter matches the explicit expectation" (interpreted = case.Expected)
            let directory = Path.Combine(runRoot, "native", optimization.ToString(), case.Name)
            use native = OwningStackAot.compile toolchain optimization directory body
            let result = native.Execute(case.Inputs, 1_048_576, 1_048_576)
            let label = $"{case.Name}/{optimization}"
            check $"{label}: native values equal the interpreter" (result.Values = interpreted)
            check $"{label}: retained bytes match the explicit wire oracle" (
                result.RetainedOutputBytes = Array.concat (List.map2 wire case.OutputTypes case.Expected))
            check $"{label}: list traversal never relocates arena data" (result.Metrics.MoveBytes = 0UL)
            match case.MaxDeepCopyBytes with
            | Some bound -> check $"{label}: deep copies stay within one copy per output element ({result.Metrics.DeepCopyBytes} <= {bound})" (result.Metrics.DeepCopyBytes <= uint64 bound)
            | None -> ()
            let row = JsonObject()
            row["case"] <- JsonValue.Create(case.Name)
            row["optimization"] <- JsonValue.Create(string optimization)
            row["deepCopyBytes"] <- JsonValue.Create(result.Metrics.DeepCopyBytes)
            row["moveBytes"] <- JsonValue.Create(result.Metrics.MoveBytes)
            row["finalCursorBytes"] <- JsonValue.Create(result.Metrics.FinalCursorBytes)
            row["retainedBytes"] <- JsonValue.Create(result.RetainedBytesWritten)
            metrics.Add(row)
    let receipt = JsonObject()
    receipt["status"] <- JsonValue.Create("pass")
    receipt["assertionCount"] <- JsonValue.Create(assertionCount.Value)
    receipt["metrics"] <- metrics
    let pins = JsonArray()
    for path in [ sourcePath; Path.Combine(__SOURCE_DIRECTORY__, "Program.fs"); Path.Combine(repo, "src/AgentLang.Llvm/OwningStackAot.fs") ] do
        let pin = JsonObject()
        pin["path"] <- JsonValue.Create(Path.GetRelativePath(repo, path).Replace('\\', '/'))
        pin["sha256"] <- JsonValue.Create(hash path)
        pins.Add(pin)
    receipt["sourcePins"] <- pins
    File.WriteAllText(Path.Combine(runRoot, "receipt.json"), receipt.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + Environment.NewLine)
    printfn "owning list ops: %d assertions passed; artifacts %s" assertionCount.Value runRoot

[<EntryPoint>]
let main _ =
    try
        run ()
        0
    with error ->
        eprintfn "%s" (error.ToString())
        1
