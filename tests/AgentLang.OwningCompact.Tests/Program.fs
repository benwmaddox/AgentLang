module AgentLang.OwningCompact.Tests

// compact { } conformance: interpreter identity semantics, owning AOT relocation
// of the single result to the block mark, measured arena savings, and rejection
// of blocks whose lifetime proof fails.

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Nodes
open AgentLang
open AgentLang.Llvm

let private repo = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))
let private sourcePath = Path.Combine(__SOURCE_DIRECTORY__, "owning-compact.flow")
let private assertionCount = ref 0

let private check label condition =
    assertionCount.Value <- assertionCount.Value + 1
    if not condition then failwith $"Assertion failed: {label}"

let private dispatch (engine: Runtime.Engine) operation (args: JsonObject) =
    let result = engine.Dispatch(operation, args)
    if not (result["ok"].GetValue<bool>()) then failwith (result.ToJsonString())
    result

let private words =
    [ "native.compact.has-name"; "native.compact.label"; "native.compact.plain-label"; "native.compact.map-labels"
      "native.compact.map-plain-labels"; "native.compact.keep"; "native.compact.first-name"; "native.compact.nested"
      "native.compact.unproven" ]

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
        check $"{name} has passing authored Flow tests (interpreter identity semantics)" (
            summary["executed"].GetValue<int>() > 0
            && summary["passed"].GetValue<int>() = summary["executed"].GetValue<int>())

let private strings values = ListValue(TString, values |> List.map StringValue)
let private ints values = ListValue(TInt, values |> List.map IntValue)

let private hash path = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()

let private run () =
    let args = Environment.GetCommandLineArgs()
    let runRoot =
        match args |> Array.tryFindIndex ((=) "--artifacts-path") with
        | Some index when index + 1 < args.Length -> Path.GetFullPath(args[index + 1])
        | _ -> Path.Combine(repo, ".agentlang", "compact-261", "runs", DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(runRoot) |> ignore
    let engine = Runtime.Engine(Path.Combine(runRoot, "flow-engine"), Set.empty, fileSystemMode = FileSystemMode.Virtual)
    prepare engine
    // Formatter round-trip: canonical output keeps compact { } and is a fixed point.
    let formatSource source =
        let args = JsonObject()
        args["frontend"] <- JsonValue.Create("flow")
        args["syntaxVersion"] <- JsonValue.Create(2)
        args["source"] <- JsonValue.Create(source: string)
        let response = dispatch engine "format" args
        let data = response["data"]
        data["source"].GetValue<string>()
    let formatted = formatSource (File.ReadAllText(sourcePath))
    check "formatter preserves compact blocks" (formatted.Contains("compact {", StringComparison.Ordinal))
    check "formatting is idempotent for compact blocks" (formatSource formatted = formatted)
    // Persistence: committed compact words reload in a fresh engine and still pass.
    let reloaded = Runtime.Engine(Path.Combine(runRoot, "flow-engine"), Set.empty, fileSystemMode = FileSystemMode.Virtual)
    for name in words do
        let testArgs = JsonObject()
        testArgs["word"] <- JsonValue.Create(name)
        let response = dispatch reloaded "test" testArgs
        let summary = response["data"]["testSummary"]
        check $"{name} passes after reload" (summary["executed"].GetValue<int>() > 0 && summary["passed"].GetValue<int>() = summary["executed"].GetValue<int>())
    let toolchain = LlvmToolchain.discover ()
    let metrics = JsonArray()
    let execute optimization word inputTypes outputTypes inputs =
        let body = engine.ExportVerifiedFlowEntryPoint(word, inputTypes, outputTypes)
        let bound = engine.BindFlowEntryPoint(word, inputTypes, outputTypes)
        let interpreted =
            match bound.Invoke(inputs) with
            | Ok value -> value.Values
            | Error diagnostic -> failwith $"Interpreter rejected {word}: {diagnostic.Message}"
        let directory = Path.Combine(runRoot, "native", string optimization, word)
        use native = OwningStackAot.compile toolchain optimization directory body
        let result = native.Execute(inputs, 1_048_576, 1_048_576)
        check $"{word}/{optimization}: native equals interpreter" (result.Values = interpreted)
        let row = JsonObject()
        row["word"] <- JsonValue.Create(word)
        row["optimization"] <- JsonValue.Create(string optimization)
        row["deepCopyBytes"] <- JsonValue.Create(result.Metrics.DeepCopyBytes)
        row["moveBytes"] <- JsonValue.Create(result.Metrics.MoveBytes)
        row["peakCursorBytes"] <- JsonValue.Create(result.Metrics.ReservedStackBytes)
        metrics.Add(row)
        interpreted, result
    let manyInts = [ 1L .. 40L ]
    let expectedLabels = manyInts |> List.map (fun value -> $"#{value}!")
    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        let compactValues, compactMap = execute optimization "native.compact.map-labels" [ TList TInt ] [ TList TString ] [ ints manyInts ]
        let plainValues, plainMap = execute optimization "native.compact.map-plain-labels" [ TList TInt ] [ TList TString ] [ ints manyInts ]
        check $"map labels/{optimization}: both maps produce the expected labels" (compactValues = [ strings expectedLabels ] && plainValues = compactValues)
        // Each "#N!" label is a 16-byte String (8-byte header + UTF-16 padded to 8).
        let resultBytes = uint64 (16 * manyInts.Length)
        check $"map labels/{optimization}: each compact result is moved to its block mark exactly once (move={compactMap.Metrics.MoveBytes})" (
            compactMap.Metrics.MoveBytes = resultBytes)
        check $"map labels/{optimization}: the map copies nothing after compact; only callback-internal copies remain (compact={compactMap.Metrics.DeepCopyBytes}, plain={plainMap.Metrics.DeepCopyBytes})" (
            compactMap.Metrics.DeepCopyBytes + resultBytes = plainMap.Metrics.DeepCopyBytes)
        check $"map labels/{optimization}: without compact nothing moves" (plainMap.Metrics.MoveBytes = 0UL)
        check $"map labels/{optimization}: compact lowers peak arena use ({compactMap.Metrics.ReservedStackBytes} < {plainMap.Metrics.ReservedStackBytes})" (
            compactMap.Metrics.ReservedStackBytes < plainMap.Metrics.ReservedStackBytes)
        let keepValues, keep = execute optimization "native.compact.keep" [ TString ] [ TString ] [ StringValue "outer λ" ]
        check $"keep/{optimization}: an outer value is returned unchanged without a move" (keepValues = [ StringValue "outer λ" ] && keep.Metrics.MoveBytes = 0UL)
        let projectedValues, projected = execute optimization "native.compact.first-name" [ TInt ] [ TString ] [ IntValue -42L ]
        check $"first-name/{optimization}: a projected field of an in-block record is relocated" (projectedValues = [ StringValue "-42" ] && projected.Metrics.MoveBytes > 0UL)
        let nestedValues, _ = execute optimization "native.compact.nested" [ TInt ] [ TString ] [ IntValue 9L ]
        check $"nested/{optimization}: nested compact blocks compose" (nestedValues = [ StringValue "9ab" ])
        // Record validators are outside the owning AOT subset, so this block is
        // rejected before lifetime analysis; unprovable compact blocks inside the
        // subset (effectful calls) are covered by the arena-lifetime tests.
        let unprovenBody = engine.ExportVerifiedFlowEntryPoint("native.compact.unproven", [ TString ], [ TString ])
        let rejected =
            try
                use _ = OwningStackAot.compile toolchain optimization (Path.Combine(runRoot, "native", string optimization, "unproven")) unprovenBody
                None
            with LanguageException diagnostic -> Some diagnostic.Code
        check $"unproven/{optimization}: a compact block outside the owning subset is still rejected at compile time ({rejected})" (rejected.IsSome)
    let receipt = JsonObject()
    receipt["status"] <- JsonValue.Create("pass")
    receipt["assertionCount"] <- JsonValue.Create(assertionCount.Value)
    receipt["metrics"] <- metrics
    let pins = JsonArray()
    for path in [ sourcePath; Path.Combine(__SOURCE_DIRECTORY__, "Program.fs"); Path.Combine(repo, "src/AgentLang.Llvm/OwningStackAot.fs"); Path.Combine(repo, "src/AgentLang.Llvm/ArenaLifetime.fs") ] do
        let pin = JsonObject()
        pin["path"] <- JsonValue.Create(Path.GetRelativePath(repo, path).Replace('\\', '/'))
        pin["sha256"] <- JsonValue.Create(hash path)
        pins.Add(pin)
    receipt["sourcePins"] <- pins
    File.WriteAllText(Path.Combine(runRoot, "receipt.json"), receipt.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + Environment.NewLine)
    printfn "owning compact: %d assertions passed; artifacts %s" assertionCount.Value runRoot

[<EntryPoint>]
let main _ =
    try
        run ()
        0
    with error ->
        eprintfn "%s" (error.ToString())
        1
