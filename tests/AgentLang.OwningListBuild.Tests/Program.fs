module AgentLang.OwningListBuild.Tests

// List building conformance: [a, b, c] literals, list.append and list.concat in
// the interpreter and the owning AOT backend (O0/O2), range-copy accounting, no
// element-count limit, and the omitted runtime validator for refined literals
// proven at compile time.

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Nodes
open AgentLang
open AgentLang.Llvm

let private repo = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))
let private sourcePath = Path.Combine(__SOURCE_DIRECTORY__, "owning-listbuild.flow")
let private assertionCount = ref 0

let private check label condition =
    assertionCount.Value <- assertionCount.Value + 1
    if not condition then failwith $"Assertion failed: {label}"

let private dispatch (engine: Runtime.Engine) operation (args: JsonObject) =
    let result = engine.Dispatch(operation, args)
    if not (result["ok"].GetValue<bool>()) then failwith (result.ToJsonString())
    result

let private flowArgs (source: string) =
    let args = JsonObject()
    args["frontend"] <- JsonValue.Create("flow")
    args["syntaxVersion"] <- JsonValue.Create(2)
    args["source"] <- JsonValue.Create(source)
    args

let private words =
    [ "tag.ok?"; "native.lists.ints"; "native.lists.strings"; "native.lists.records"; "native.lists.nested"; "native.lists.joined"
      "native.lists.compact-joined"; "native.lists.append"; "native.lists.concat"; "native.lists.append-records"
      "native.lists.literal-tag"; "native.lists.runtime-tag"; "native.lists.append-top"; "native.lists.append-adjacent"
      "native.lists.concat-adjacent"; "native.lists.append-aliased"; "tag.text"; "native.lists.retag"
      "native.acc.ints"; "native.acc.strings"; "native.acc.records"; "native.acc.aliases"; "native.acc.snapshots"
      "native.acc.break"; "native.acc.branches"; "native.acc.with-text"; "native.acc.rows"; "native.acc.kept"
      "native.lists.get"; "native.lists.get-int"; "native.lists.get-pair"; "native.lists.count" ]

let private wordArgs (name: string) =
    let args = JsonObject()
    args["word"] <- JsonValue.Create(name)
    args

let private prepare (engine: Runtime.Engine) =
    dispatch engine "define" (flowArgs (File.ReadAllText(sourcePath))) |> ignore
    for name in words do
        let response = engine.Dispatch("commit", wordArgs name)
        let committed = response["ok"].GetValue<bool>() || response.ToJsonString().Contains("COMMIT_NOT_CANDIDATE", StringComparison.Ordinal)
        if not committed then eprintfn "%s" (response.ToJsonString())
        check $"{name} commits" committed
    for name in words do
        let response = dispatch engine "test" (wordArgs name)
        let summary = response["data"]["testSummary"]
        check $"{name} authored Flow tests pass" (
            summary["executed"].GetValue<int>() > 0 && summary["passed"].GetValue<int>() = summary["executed"].GetValue<int>())

let private strings values = ListValue(TString, values |> List.map StringValue)
let private hash (path: string) = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
let private stringExtent (text: string) = (8 + Text.Encoding.UTF8.GetByteCount text + 7) &&& ~~~7

let private run () =
    let args = Environment.GetCommandLineArgs()
    let runRoot =
        match args |> Array.tryFindIndex ((=) "--artifacts-path") with
        | Some index when index + 1 < args.Length -> Path.GetFullPath(args[index + 1])
        | _ -> Path.Combine(repo, ".agentlang", "listbuild-263", "runs", DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(runRoot) |> ignore
    let engine = Runtime.Engine(Path.Combine(runRoot, "flow-engine"), Set.empty, fileSystemMode = FileSystemMode.Virtual)
    prepare engine

    // Formatter: canonical output keeps list literals, drops the trailing comma,
    // and is a fixed point.
    let formatSource source =
        let response = dispatch engine "format" (flowArgs source)
        let data = response["data"]
        data["source"].GetValue<string>()
    let formatted = formatSource (File.ReadAllText(sourcePath))
    check "formatter renders list literals" (formatted.Contains("[\"<li>\", name, \"</li>\"]", StringComparison.Ordinal))
    check "formatting is idempotent for list literals" (formatSource formatted = formatted)
    let reloaded = Runtime.Engine(Path.Combine(runRoot, "flow-engine"), Set.empty, fileSystemMode = FileSystemMode.Virtual)
    for name in words do
        let response = dispatch reloaded "test" (wordArgs name)
        let summary = response["data"]["testSummary"]
        check $"{name} passes after reload" (summary["executed"].GetValue<int>() > 0 && summary["passed"].GetValue<int>() = summary["executed"].GetValue<int>())

    // Static errors.
    let errorFor source =
        let engine = Runtime.Engine(Path.Combine(runRoot, "error-" + Guid.NewGuid().ToString("N")), Set.empty, fileSystemMode = FileSystemMode.Virtual)
        engine.Dispatch("define", flowArgs source).ToJsonString()
    let emptyError = errorFor "fn bad.empty() -> List<Int> {\n    effects none\n    []\n}\n"
    check "an empty list literal is rejected with FLOW_LIST_LITERAL_EMPTY" (emptyError.Contains("FLOW_LIST_LITERAL_EMPTY", StringComparison.Ordinal))
    let mixedError = errorFor "fn bad.mixed() -> List<Int> {\n    effects none\n    [1, \"a\"]\n}\n"
    check "a mixed-type list literal is rejected with FLOW_LIST_LITERAL_TYPE" (mixedError.Contains("FLOW_LIST_LITERAL_TYPE", StringComparison.Ordinal))

    let toolchain = LlvmToolchain.discover ()
    let metrics = JsonArray()
    let compiled = Collections.Generic.Dictionary<string, OwningStackCompiledProgram>()
    let nativeFor optimization word inputTypes outputTypes =
        let key = $"{optimization}/{word}"
        match compiled.TryGetValue key with
        | true, program -> program
        | _ ->
            let body = engine.ExportVerifiedFlowEntryPoint(word, inputTypes, outputTypes)
            let program = OwningStackAot.compile toolchain optimization (Path.Combine(runRoot, "native", string optimization, word)) body
            compiled[key] <- program
            program
    let execute optimization word inputTypes outputTypes inputs =
        let bound = engine.BindFlowEntryPoint(word, inputTypes, outputTypes)
        let interpreted =
            match bound.Invoke(inputs) with
            | Ok value -> value.Values
            | Error diagnostic -> failwith $"Interpreter rejected {word}: {diagnostic.Message}"
        let result = (nativeFor optimization word inputTypes outputTypes).Execute(inputs, 16_777_216, 8_388_608)
        check $"{word}/{optimization}: native equals interpreter" (result.Values = interpreted)
        let row = JsonObject()
        row["word"] <- JsonValue.Create(word)
        row["optimization"] <- JsonValue.Create(string optimization)
        row["deepCopyBytes"] <- JsonValue.Create(result.Metrics.DeepCopyBytes)
        row["moveBytes"] <- JsonValue.Create(result.Metrics.MoveBytes)
        row["peakCursorBytes"] <- JsonValue.Create(result.Metrics.ReservedStackBytes)
        row["frameReturns"] <- JsonValue.Create(result.Metrics.FrameReturnCount)
        metrics.Add(row)
        interpreted, result

    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        let ints, _ = execute optimization "native.lists.ints" [ TInt; TInt ] [ TList TInt ] [ IntValue 4L; IntValue 5L ]
        check $"ints/{optimization}: [a, b, a + b]" (ints = [ ListValue(TInt, [ IntValue 4L; IntValue 5L; IntValue 9L ]) ])
        let parts, _ = execute optimization "native.lists.strings" [ TString ] [ TList TString ] [ StringValue "café λ 🚀" ]
        check $"strings/{optimization}: literal elements in order" (parts = [ strings [ "<li>"; "café λ 🚀"; "</li>" ] ])
        let records, _ = execute optimization "native.lists.records" [ TString; TInt ] [ TList(TNamed "Pair") ] [ StringValue "n"; IntValue 2L ]
        check $"records/{optimization}: two records" (match records with [ ListValue(_, items) ] -> items.Length = 2 | _ -> false)
        let nested, _ = execute optimization "native.lists.nested" [ TString ] [ TList(TList TString) ] [ StringValue "z" ]
        check $"nested/{optimization}: list of lists" (nested = [ ListValue(TList TString, [ strings [ "z" ]; strings [ "a"; "b" ] ]) ])
        let joined, joinedResult = execute optimization "native.lists.joined" [ TString ] [ TString ] [ StringValue "x" ]
        check $"joined/{optimization}: string.join of a list literal" (joined = [ StringValue "<li class=\"row\">x</li>" ])
        // The literal is fused into an n-way join: no list is built and the result is
        // written once. Today each String constant is also materialized into the arena
        // (one copy from read-only data); shared static literals will remove that.
        let literalBytes = stringExtent "<li class=\"row\">" + stringExtent "</li>"
        check $"joined/{optimization}: fused join writes the result once beside the constant materialization (deep={joinedResult.Metrics.DeepCopyBytes})" (
            joinedResult.Metrics.DeepCopyBytes = uint64 (stringExtent "<li class=\"row\">x</li>" + literalBytes))
        let compactJoined, _ = execute optimization "native.lists.compact-joined" [ TString ] [ TString ] [ StringValue "<b>" ]
        check $"compact-joined/{optimization}: provable inside compact" (compactJoined = [ StringValue "<li>&lt;b&gt;</li>" ])

        // append/concat: elements are range-copied once into the sized result.
        let many = [ for index in 1 .. 20_000 -> $"item-{index}" ]
        let appended, appendResult = execute optimization "native.lists.append" [ TList TString; TString ] [ TList TString ] [ strings many; StringValue "last" ]
        check $"append/{optimization}: 20,001 elements (no element-count limit)" (appended = [ strings (many @ [ "last" ]) ])
        let elementBytes = many |> List.sumBy stringExtent
        check $"append/{optimization}: copies the elements and the new value once (deep={appendResult.Metrics.DeepCopyBytes})" (
            appendResult.Metrics.DeepCopyBytes = uint64 (elementBytes + stringExtent "last"))
        let concatenated, concatResult = execute optimization "native.lists.concat" [ TList TString; TList TString ] [ TList TString ] [ strings many; strings [ "a"; "b" ] ]
        check $"concat/{optimization}: 20,002 elements" (concatenated = [ strings (many @ [ "a"; "b" ]) ])
        check $"concat/{optimization}: copies each element once (deep={concatResult.Metrics.DeepCopyBytes})" (
            concatResult.Metrics.DeepCopyBytes = uint64 (elementBytes + stringExtent "a" + stringExtent "b"))
        let emptyAppend, _ = execute optimization "native.lists.append" [ TList TString; TString ] [ TList TString ] [ strings []; StringValue "only" ]
        check $"append/{optimization}: onto an empty list" (emptyAppend = [ strings [ "only" ] ])
        let emptyConcat, _ = execute optimization "native.lists.concat" [ TList TString; TList TString ] [ TList TString ] [ strings []; strings [] ]
        check $"concat/{optimization}: two empty lists" (emptyConcat = [ strings [] ])
        let recordAppend, _ = execute optimization "native.lists.append-records" [ TList(TNamed "Pair"); TString ] [ TList(TNamed "Pair") ] [ ListValue(TNamed "Pair", []); StringValue "r" ]
        check $"append-records/{optimization}: record element" (match recordAppend with [ ListValue(_, [ _ ]) ] -> true | _ -> false)

        // In-place list growth: unaliased lists are extended without recopying.
        let topValues, top = execute optimization "native.lists.append-top" [ TString; TString; TString ] [ TList TString ] [ StringValue "aa"; StringValue "bbb"; StringValue "c" ]
        check $"append-top/{optimization}: values" (topValues = [ strings [ "aa"; "bbb"; "c" ] ])
        check $"append-top/{optimization}: each element copied once, no list recopy (deep={top.Metrics.DeepCopyBytes})" (
            top.Metrics.DeepCopyBytes = uint64 (stringExtent "aa" + stringExtent "bbb" + stringExtent "c"))
        let adjacentValues, adjacent = execute optimization "native.lists.append-adjacent" [ TString; TString ] [ TList TString ] [ StringValue "aa"; StringValue "<b>" ]
        check $"append-adjacent/{optimization}: values" (adjacentValues = [ strings [ "aa"; "&lt;b&gt;" ] ])
        // [a] copies a; the escape writes its result directly after the list; the append copies nothing.
        check $"append-adjacent/{optimization}: the adjacent element is not copied (deep={adjacent.Metrics.DeepCopyBytes})" (
            adjacent.Metrics.DeepCopyBytes = uint64 (stringExtent "aa" + stringExtent "&lt;b&gt;"))
        let concatValues, concatAdjacent = execute optimization "native.lists.concat-adjacent" [ TString; TString; TString ] [ TList TString ] [ StringValue "a"; StringValue "b"; StringValue "c" ]
        check $"concat-adjacent/{optimization}: values" (concatValues = [ strings [ "a"; "b"; "c" ] ])
        check $"concat-adjacent/{optimization}: right elements slide over the right header instead of being copied (deep={concatAdjacent.Metrics.DeepCopyBytes}, move={concatAdjacent.Metrics.MoveBytes})" (
            concatAdjacent.Metrics.DeepCopyBytes = uint64 (stringExtent "a" + stringExtent "b" + stringExtent "c")
            && concatAdjacent.Metrics.MoveBytes = uint64 (stringExtent "b" + stringExtent "c"))
        let aliasedValues, _ = execute optimization "native.lists.append-aliased" [ TString; TString ] [ TList(TList TString) ] [ StringValue "a"; StringValue "b" ]
        check $"append-aliased/{optimization}: a list bound to a local is never mutated" (
            aliasedValues = [ ListValue(TList TString, [ strings [ "a" ]; strings [ "a"; "b" ] ]) ])
        let tags = ListValue(TNamed "Tag", [ NamedValue("Tag", StringValue "x"); NamedValue("Tag", StringValue "yy") ])
        let retagValues, retag = execute optimization "native.lists.retag" [ TList(TNamed "Tag") ] [ TList TString ] [ tags ]
        check $"retag/{optimization}: values" (retagValues = [ strings [ "x"; "yy" ] ])
        check $"retag/{optimization}: map of a nominal unwrap is a retag: no copy, no callback frame (deep={retag.Metrics.DeepCopyBytes}, frames={retag.Metrics.FrameReturnCount})" (
            // Frames: the entry and the word itself; the two elements add no callback frames.
            retag.Metrics.DeepCopyBytes = 0UL && retag.Metrics.FrameReturnCount = 2)

        // A literal proven at compile time skips the runtime validator frame.
        let literalValues, literal = execute optimization "native.lists.literal-tag" [] [ TString ] []
        let runtimeValues, runtime = execute optimization "native.lists.runtime-tag" [ TString ] [ TString ] [ StringValue "literal" ]
        check $"tags/{optimization}: same value" (literalValues = runtimeValues)
        check $"tags/{optimization}: the proven literal runs one validator frame fewer ({literal.Metrics.FrameReturnCount} + 1 = {runtime.Metrics.FrameReturnCount})" (
            literal.Metrics.FrameReturnCount + 1 = runtime.Metrics.FrameReturnCount)
        let rejection =
            try
                (nativeFor optimization "native.lists.runtime-tag" [ TString ] [ TString ]).Execute([ StringValue "" ], 1_048_576, 1_048_576) |> ignore
                "accepted"
            with
            | :? OwningStackExecutionException as failure -> failure.Diagnostic.Code
            | error -> error.GetType().Name
        check $"tags/{optimization}: runtime construction is still validated ({rejection})" (rejection = "REFINEMENT_FAILED")

        // Loop accumulation (report 278): `v = list.append(v, x)` in a loop. Every
        // shape must equal the interpreter; aliases and snapshots stay unchanged.
        let ints n = ListValue(TInt, [ for i in 0L .. n - 1L -> IntValue i ])
        let accInts, _ = execute optimization "native.acc.ints" [ TInt ] [ TList TInt ] [ IntValue 5L ]
        check $"acc ints/{optimization}" (accInts = [ ints 5L ])
        let accStrings, _ = execute optimization "native.acc.strings" [ TInt ] [ TList TString ] [ IntValue 12L ]
        check $"acc strings/{optimization}" (accStrings = [ strings [ for i in 0 .. 11 -> string i ] ])
        let accRecords, _ = execute optimization "native.acc.records" [ TInt ] [ TList(TNamed "Pair") ] [ IntValue 3L ]
        check $"acc records/{optimization}" (match accRecords with [ ListValue(_, items) ] -> items.Length = 4 | _ -> false)
        let aliases, _ = execute optimization "native.acc.aliases" [ TInt ] [ TList(TList TInt) ] [ IntValue 3L ]
        check $"acc aliases/{optimization}: the pre-loop alias and the let are unchanged" (
            aliases = [ ListValue(TList TInt, [ ListValue(TInt, [ IntValue 7L ]); ListValue(TInt, [ IntValue 7L ]); ListValue(TInt, [ IntValue 7L; IntValue 0L; IntValue 1L; IntValue 2L ]) ]) ])
        let snapshots, _ = execute optimization "native.acc.snapshots" [ TInt ] [ TList(TList TInt) ] [ IntValue 4L ]
        check $"acc snapshots/{optimization}: each snapshot keeps its length" (snapshots = [ ListValue(TList TInt, [ for n in 0L .. 4L -> ints n ]) ])
        for stop in [ 0L; 3L; 20L ] do
            let broken, _ = execute optimization "native.acc.break" [ TInt; TInt ] [ TList TInt ] [ IntValue 10L; IntValue stop ]
            check $"acc break {stop}/{optimization}: pending elements survive break" (broken = [ ints (min 10L (stop + 1L)) ])
        let branches, _ = execute optimization "native.acc.branches" [ TInt ] [ TList TInt ] [ IntValue 12L ]
        check $"acc branches/{optimization}" (match branches with [ ListValue(_, items) ] -> items.Head = IntValue -1L && items.Length = 9 | _ -> false)
        let withText, _ = execute optimization "native.acc.with-text" [ TInt ] [ TList TString ] [ IntValue 4L ]
        check $"acc with another carried var/{optimization}" (withText = [ strings [ "0"; "1"; "2"; "3"; "xxxx"; "end" ] ])
        let rows, _ = execute optimization "native.acc.rows" [ TInt ] [ TList(TList TInt) ] [ IntValue 5L ]
        check $"acc nested loops/{optimization}" (rows = [ ListValue(TList TInt, [ for n in 0L .. 4L -> ints n ]) ])
        let kept, _ = execute optimization "native.acc.kept" [ TList TString ] [ TList TString ] [ strings [ "a"; ""; "b"; "c"; "" ] ]
        check $"acc list loop views/{optimization}" (kept = [ strings [ "a"; "b"; "c" ] ])

        // Native list.get / list.count (interpreter parity, including out-of-range indexes).
        for index in [ -1L; 0L; 2L; 3L; 4_294_967_296L ] do
            let got, _ = execute optimization "native.lists.get" [ TList TString; TInt ] [ TOption TString ] [ strings [ "a"; "bé"; "c🚀" ]; IntValue index ]
            let expected = if index >= 0L && index < 3L then Some(StringValue([| "a"; "bé"; "c🚀" |][int index])) else None
            check $"list.get {index}/{optimization}" (got = [ OptionValue(TString, expected) ])
            let gotInt, _ = execute optimization "native.lists.get-int" [ TList TInt; TInt ] [ TOption TInt ] [ ints 3L; IntValue index ]
            check $"list.get Int {index}/{optimization}" (gotInt = [ OptionValue(TInt, (if index >= 0L && index < 3L then Some(IntValue index) else None)) ])
        let pairs = ListValue(TNamed "Pair", [ for i in 1L .. 3L -> RecordValue("Pair", Map.ofList [ "name", StringValue(String.replicate (int i) "n"); "count", IntValue i ]) ])
        let gotPair, _ = execute optimization "native.lists.get-pair" [ TList(TNamed "Pair"); TInt ] [ TOption(TNamed "Pair") ] [ pairs; IntValue 2L ]
        check $"list.get record/{optimization}" (match gotPair with [ OptionValue(_, Some(RecordValue(_, fields))) ] -> fields["count"] = IntValue 3L | _ -> false)
        for values in [ []; [ "a" ]; [ "a"; "b"; "c" ] ] do
            let counted, _ = execute optimization "native.lists.count" [ TList TString ] [ TInt ] [ strings values ]
            check $"list.count {values.Length}/{optimization}" (counted = [ IntValue(int64 values.Length) ])

    // Accumulation is linear: copied/moved bytes and peak arena double (not
    // quadruple) when the element count doubles, in every profile. Trusted O0 and
    // O2 also time 5,000 vs 10,000 elements. (The diagnostic profile re-checks
    // every live local's init bits after each instruction, a self-test whose cost
    // grows with the list, so it is measured on bytes at 1,000 vs 2,000.)
    let trustedFor optimization =
        let body = engine.ExportVerifiedFlowEntryPoint("native.acc.strings", [ TInt ], [ TList TString ])
        OwningStackAot.compileWithRuntimeProfile toolchain optimization OwningRuntimeProfile.TrustedGenerated (Path.Combine(runRoot, "native", "trusted", string optimization, "native.acc.strings")) body
    use trustedO0 = trustedFor LlvmOptimization.O0
    use trustedO2 = trustedFor LlvmOptimization.O2
    let programs =
        [ "diagnostic O0", nativeFor LlvmOptimization.O0 "native.acc.strings" [ TInt ] [ TList TString ], 1_000L, false
          "diagnostic O2", nativeFor LlvmOptimization.O2 "native.acc.strings" [ TInt ] [ TList TString ], 1_000L, false
          "trusted O0", trustedO0, 5_000L, true
          "trusted O2", trustedO2, 5_000L, true ]
    for label, program, half, timed in programs do
        let measure n =
            let result = program.Execute([ IntValue n ], 16_777_216, 8_388_608)
            check $"acc linear/{label}: {n} elements" (match result.Values with [ ListValue(_, items) ] -> items.Length = int n | _ -> false)
            let ticks =
                if timed then [| for _ in 1 .. 40 -> (program.Execute([ IntValue n ], 16_777_216, 8_388_608) |> ignore; program.LastNativeElapsedTicks) |] |> Array.min
                else 0L
            result.Metrics, ticks
        let small, smallTicks = measure half
        let large, largeTicks = measure (2L * half)
        let ratio (a: uint64) (b: uint64) = float a / float (max 1UL b)
        let copies = ratio (large.DeepCopyBytes + large.MoveBytes) (small.DeepCopyBytes + small.MoveBytes)
        let peak = float large.ReservedStackBytes / float small.ReservedStackBytes
        let row = JsonObject()
        row["word"] <- JsonValue.Create($"native.acc.strings {half}->{2L * half} {label}")
        row["copyRatio"] <- JsonValue.Create(copies)
        row["peakRatio"] <- JsonValue.Create(peak)
        row["deepCopyBytes"] <- JsonValue.Create(large.DeepCopyBytes)
        row["moveBytes"] <- JsonValue.Create(large.MoveBytes)
        row["peakCursorBytes"] <- JsonValue.Create(large.ReservedStackBytes)
        check $"acc linear/{label}: copied+moved bytes scale linearly ({copies:F2}x)" (copies < 2.2)
        check $"acc linear/{label}: peak arena scales linearly ({peak:F2}x)" (peak < 2.2)
        // Every element is copied once into the pending run, plus the one String
        // per iteration int.to-string writes: no list recopy.
        check $"acc linear/{label}: no list recopy ({large.DeepCopyBytes} bytes for {2L * half})" (large.DeepCopyBytes < uint64 (2L * half) * 40UL)
        if timed then
            let time = float largeTicks / float (max 1L smallTicks)
            row["timeRatio"] <- JsonValue.Create(time)
            // Quadratic growth would be 4x; allow noise from a busy machine.
            check $"acc linear/{label}: time scales linearly ({time:F2}x)" (time < 3.0)
        metrics.Add(row)
    for program in compiled.Values do (program :> IDisposable).Dispose()

    let receipt = JsonObject()
    receipt["status"] <- JsonValue.Create("pass")
    receipt["assertionCount"] <- JsonValue.Create(assertionCount.Value)
    receipt["metrics"] <- metrics
    let pins = JsonArray()
    for path in [ sourcePath; Path.Combine(__SOURCE_DIRECTORY__, "Program.fs")
                  Path.Combine(repo, "src/AgentLang.Llvm/OwningStackAot.fs"); Path.Combine(repo, "src/AgentLang.Llvm/ArenaLifetime.fs")
                  Path.Combine(repo, "src/AgentLang.Llvm/native/owning_stack_runtime.c"); Path.Combine(repo, "src/AgentLang.Core/FlowLowering.fs")
                  Path.Combine(repo, "src/AgentLang.Core/FlowParser.fs"); Path.Combine(repo, "src/AgentLang.Core/IrInterpreter.fs") ] do
        let pin = JsonObject()
        pin["path"] <- JsonValue.Create(Path.GetRelativePath(repo, path).Replace('\\', '/'))
        pin["sha256"] <- JsonValue.Create(hash path)
        pins.Add(pin)
    receipt["sourcePins"] <- pins
    File.WriteAllText(Path.Combine(runRoot, "receipt.json"), receipt.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + Environment.NewLine)
    printfn "owning list build: %d assertions passed; artifacts %s" assertionCount.Value runRoot

[<EntryPoint>]
let main _ =
    try
        run ()
        0
    with error ->
        eprintfn "%s" (error.ToString())
        1
