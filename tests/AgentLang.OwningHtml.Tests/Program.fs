module AgentLang.OwningHtml.Tests

// string.join / string.html-escape / string search conformance: Rails
// ERB::Util.html_escape vectors byte-for-byte, interpreter and owning AOT
// parity at O0 and O2, single-copy join accounting, the library-defined Markup
// refined type, and compile-time validation of refined literals.

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open AgentLang
open AgentLang.Llvm

let private repo = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))
let private sourcePath = Path.Combine(__SOURCE_DIRECTORY__, "owning-html.flow")
let private vectorsPath = Path.Combine(__SOURCE_DIRECTORY__, "vectors", "rails-html-escape-vectors.json")
let private assertionCount = ref 0

let private check label condition =
    assertionCount.Value <- assertionCount.Value + 1
    if not condition then failwith $"Assertion failed: {label}"

let private tryDispatch (engine: Runtime.Engine) operation (args: JsonObject) = engine.Dispatch(operation, args)

let private dispatch (engine: Runtime.Engine) operation (args: JsonObject) =
    let result = tryDispatch engine operation args
    if not (result["ok"].GetValue<bool>()) then failwith (result.ToJsonString())
    result

let private words =
    [ "html.safe?"; "html.escape"; "html.text"; "html.join"
      "native.html.escape-text"; "native.html.join-text"; "native.html.contains"; "native.html.starts-with"
      "native.html.ends-with"; "native.html.escape-all"; "native.html.row"; "native.html.page"; "native.html.trusted" ]

let private defineSource (engine: Runtime.Engine) (source: string) =
    let args = JsonObject()
    args["frontend"] <- JsonValue.Create("flow")
    args["syntaxVersion"] <- JsonValue.Create(2)
    args["source"] <- JsonValue.Create(source)
    tryDispatch engine "define" args

let private commitWord (engine: Runtime.Engine) (name: string) =
    let args = JsonObject()
    args["word"] <- JsonValue.Create(name)
    tryDispatch engine "commit" args

let private errorCode (response: JsonNode) =
    let text = response.ToJsonString()
    if text.Contains("REFINEMENT_LITERAL_INVALID", StringComparison.Ordinal) then "REFINEMENT_LITERAL_INVALID"
    elif response["ok"].GetValue<bool>() then "ok"
    else text

let private prepare (engine: Runtime.Engine) =
    dispatch engine "define" (let a = JsonObject() in a["frontend"] <- JsonValue.Create("flow"); a["syntaxVersion"] <- JsonValue.Create(2); a["source"] <- JsonValue.Create(File.ReadAllText(sourcePath)); a) |> ignore
    for name in words do
        // Committing a word also commits the words its tests depend on.
        let response = commitWord engine name
        let committed = response["ok"].GetValue<bool>() || response.ToJsonString().Contains("COMMIT_NOT_CANDIDATE", StringComparison.Ordinal)
        if not committed then eprintfn "%s" (response.ToJsonString())
        check $"{name} commits (valid Markup literals pass the compile-time validator)" committed
    for name in words |> List.filter (fun name -> name.StartsWith("native.", StringComparison.Ordinal)) do
        let testArgs = JsonObject()
        testArgs["word"] <- JsonValue.Create(name)
        let response = dispatch engine "test" testArgs
        let summary = response["data"]["testSummary"]
        if summary["executed"].GetValue<int>() > 0 then
            check $"{name} authored Flow tests pass" (summary["passed"].GetValue<int>() = summary["executed"].GetValue<int>())

let private strings values = ListValue(TString, values |> List.map StringValue)
let private hash (path: string) = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
let private stringExtent (text: string) = (8 + Text.Encoding.UTF8.GetByteCount text + 7) &&& ~~~7

let private run () =
    let args = Environment.GetCommandLineArgs()
    let runRoot =
        match args |> Array.tryFindIndex ((=) "--artifacts-path") with
        | Some index when index + 1 < args.Length -> Path.GetFullPath(args[index + 1])
        | _ -> Path.Combine(repo, ".agentlang", "stdlib-262", "runs", DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(runRoot) |> ignore
    let engine = Runtime.Engine(Path.Combine(runRoot, "flow-engine"), Set.empty, fileSystemMode = FileSystemMode.Virtual)
    prepare engine

    // Compile-time refined literals: an invalid literal passed straight to the
    // library constructor fails the build; a valid literal commits (above).
    let refinedTypeSource = File.ReadAllText(sourcePath).Split("fn native.html.escape-text")[0]
    let invalidLiteralSource =
        refinedTypeSource + """
fn bad.literal.page(name: String) -> String {
    effects none
    html.text(Markup.new("<script>alert(1)</script>"))
}
"""
    let literalEngine = Runtime.Engine(Path.Combine(runRoot, "literal-engine"), Set.empty, fileSystemMode = FileSystemMode.Virtual)
    let defined = defineSource literalEngine invalidLiteralSource
    let literalOutcome =
        if not (defined["ok"].GetValue<bool>()) then errorCode defined
        else errorCode (commitWord literalEngine "bad.literal.page")
    check $"an invalid Markup literal is rejected at compile time ({literalOutcome})" (literalOutcome = "REFINEMENT_LITERAL_INVALID")
    let nestedLiteralSource =
        refinedTypeSource + """
fn bad.literal.nested(flag: Bool) -> String {
    effects none
    if flag {
        "x"
    } else {
        html.text(Markup.new("<a href=\"javascript:void(0)\">x</a>"))
    }
}
"""
    let nestedEngine = Runtime.Engine(Path.Combine(runRoot, "nested-literal-engine"), Set.empty, fileSystemMode = FileSystemMode.Virtual)
    let nestedDefined = defineSource nestedEngine nestedLiteralSource
    let nestedOutcome =
        if not (nestedDefined["ok"].GetValue<bool>()) then errorCode nestedDefined
        else errorCode (commitWord nestedEngine "bad.literal.nested")
    check $"an invalid Markup literal inside a branch is rejected at compile time ({nestedOutcome})" (nestedOutcome = "REFINEMENT_LITERAL_INVALID")

    // A validator that itself fails on a literal (here DIVIDE_BY_ZERO) is also a
    // compile-time literal error, carrying the cause.
    let failingValidatorSource = """
type Ratio : String {
    validate ratio.ok?
}

fn ratio.ok?(value: String) -> Bool {
    effects none
    int.greater-than(divide(100, string.length(value)), 1)
}

fn ratio.empty() -> String {
    effects none
    Ratio.value(Ratio.new(""))
}
"""
    let failingEngine = Runtime.Engine(Path.Combine(runRoot, "failing-validator-engine"), Set.empty, fileSystemMode = FileSystemMode.Virtual)
    let failingDefined = defineSource failingEngine failingValidatorSource
    let failingText =
        if not (failingDefined["ok"].GetValue<bool>()) then failingDefined.ToJsonString()
        else (commitWord failingEngine "ratio.empty").ToJsonString()
    check $"a validator error on a literal is REFINEMENT_LITERAL_INVALID with its cause ({errorCode failingDefined})" (
        failingText.Contains("REFINEMENT_LITERAL_INVALID", StringComparison.Ordinal) && failingText.Contains("DIVIDE_BY_ZERO", StringComparison.Ordinal))
    // Validators are pure by language rule, so there is no effectful validator to
    // defer: declaring one is rejected before the literal check runs.
    let effectfulValidatorSource = """
type Tag : String {
    validate tag.ok?
}

fn tag.ok?(value: String) -> Bool {
    effects db.read
    int.greater-than(string.length(value), 5)
}
"""
    let effectfulEngine = Runtime.Engine(Path.Combine(runRoot, "effectful-validator-engine"), Set.empty, fileSystemMode = FileSystemMode.Virtual)
    let effectfulText = (defineSource effectfulEngine effectfulValidatorSource).ToJsonString()
    check "an effectful validator is rejected with TYPE_VALIDATOR_EFFECT" (effectfulText.Contains("TYPE_VALIDATOR_EFFECT", StringComparison.Ordinal))

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
        let result = (nativeFor optimization word inputTypes outputTypes).Execute(inputs, 16_777_216, 4_194_304)
        check $"{word}/{optimization}: native equals interpreter" (result.Values = interpreted)
        interpreted, result
    let record (label: string) optimization (result: OwningStackExecutionResult) =
        let row = JsonObject()
        row["case"] <- JsonValue.Create(label)
        row["optimization"] <- JsonValue.Create(string optimization)
        row["deepCopyBytes"] <- JsonValue.Create(result.Metrics.DeepCopyBytes)
        row["moveBytes"] <- JsonValue.Create(result.Metrics.MoveBytes)
        row["inputCopyBytes"] <- JsonValue.Create(result.Metrics.InputCopyBytes)
        row["peakCursorBytes"] <- JsonValue.Create(result.Metrics.ReservedStackBytes)
        metrics.Add(row)

    use vectorDocument = JsonDocument.Parse(File.ReadAllText(vectorsPath))
    let vectors =
        [ for vector in vectorDocument.RootElement.GetProperty("vectors").EnumerateArray() ->
            vector.GetProperty("input").GetString(), vector.GetProperty("escaped").GetString() ]
    check "the pinned Rails vector file has 18 vectors" (vectors.Length = 18)
    for input, expected in vectors do
        check $"TrustedValues.htmlEscape matches Rails for {JsonSerializer.Serialize input}" (TrustedValues.htmlEscape input = expected)
    // Unpaired surrogates and NUL cannot round-trip through the Ruby JSON file;
    // the rule copies every code unit other than the five unchanged.
    let extraInputs = [ "café λ 🚀 𐐷 <b>"; "nul\u0000<"; String('<', 300) ]
    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        for index, (input, expected) in List.indexed vectors do
            let values, result = execute optimization "native.html.escape-text" [ TString ] [ TString ] [ StringValue input ]
            check $"escape vector {index}/{optimization}: native output equals Rails byte-for-byte" (values = [ StringValue expected ])
            check $"escape vector {index}/{optimization}: the escaped String is written once (deep={result.Metrics.DeepCopyBytes})" (
                result.Metrics.DeepCopyBytes = uint64 (stringExtent expected))
        for input in extraInputs do
            let values, _ = execute optimization "native.html.escape-text" [ TString ] [ TString ] [ StringValue input ]
            check $"escape extra/{optimization}: {input.Length} units follow the five-character rule" (values = [ StringValue(TrustedValues.htmlEscape input) ])

        let joinCases =
            [ "empty", []
              "one-empty", [ "" ]
              "two", [ "a"; "b" ]
              "unicode", [ "café "; "λ"; "🚀"; "𐐷"; "" ]
              "many", [ for index in 1 .. 400 -> $"<li>{index}</li>" ] ]
        for label, parts in joinCases do
            let expected = String.Concat(parts)
            let values, result = execute optimization "native.html.join-text" [ TList TString ] [ TString ] [ strings parts ]
            check $"join {label}/{optimization}: equals String.Concat" (values = [ StringValue expected ])
            check $"join {label}/{optimization}: one copy of every part into the exact result (deep={result.Metrics.DeepCopyBytes})" (
                result.Metrics.DeepCopyBytes = uint64 (stringExtent expected))
            record $"join-{label}" optimization result

        let searchCases =
            [ "hello world", "world"; "hello world", "hello"; "hello", ""; "", ""; "", "a"; "ab", "abc"
              "a🚀b", "🚀"; "a🚀b", "🚀b"; "é", "e"; "<script>", "<script"; "aaab", "aab"; "abc", "abd" ]
        for haystack, needle in searchCases do
            let expectations =
                [ "native.html.contains", haystack.Contains(needle, StringComparison.Ordinal)
                  "native.html.starts-with", haystack.StartsWith(needle, StringComparison.Ordinal)
                  "native.html.ends-with", haystack.EndsWith(needle, StringComparison.Ordinal) ]
            for word, expected in expectations do
                let values, _ = execute optimization word [ TString; TString ] [ TBool ] [ StringValue haystack; StringValue needle ]
                check $"{word}({JsonSerializer.Serialize haystack}, {JsonSerializer.Serialize needle})/{optimization} = {expected}" (values = [ BoolValue expected ])

        let names = [ "Tom & Jerry"; "<b>bold</b>"; "it's \"quoted\""; "plain" ]
        let allValues, _ = execute optimization "native.html.escape-all" [ TList TString ] [ TString ] [ strings names ]
        check $"escape-all/{optimization}: Markup join of escaped parts" (allValues = [ StringValue(String.Concat(names |> List.map TrustedValues.htmlEscape)) ])
        let rowValues, row = execute optimization "native.html.row" [ TString ] [ TString ] [ StringValue "Tom & Jerry" ]
        check $"row/{optimization}: literal Markup + escaped text" (rowValues = [ StringValue "<li class=\"row\">Tom &amp; Jerry</li>" ])
        record "row" optimization row

        // Non-literal construction is still validated at run time.
        let trusted = nativeFor optimization "native.html.trusted" [ TString ] [ TString ]
        let okValues, _ = execute optimization "native.html.trusted" [ TString ] [ TString ] [ StringValue "<b>ok</b>" ]
        check $"trusted/{optimization}: valid runtime Markup passes" (okValues = [ StringValue "<b>ok</b>" ])
        let bound = engine.BindFlowEntryPoint("native.html.trusted", [ TString ], [ TString ])
        let interpreterRejects =
            match bound.Invoke([ StringValue "<script>x" ]) with
            | Ok _ -> false
            | Error diagnostic -> diagnostic.Code = "REFINEMENT_FAILED"
        check $"trusted/{optimization}: interpreter rejects invalid runtime Markup with REFINEMENT_FAILED" interpreterRejects
        let nativeRejection =
            try
                trusted.Execute([ StringValue "<script>x" ], 1_048_576, 1_048_576) |> ignore
                "accepted"
            with
            | :? OwningStackExecutionException as failure -> failure.Diagnostic.Code
            | LanguageException diagnostic -> diagnostic.Code
            | error -> error.GetType().Name + ": " + error.Message
        check $"trusted/{optimization}: native rejects invalid runtime Markup with REFINEMENT_FAILED ({nativeRejection})" (nativeRejection = "REFINEMENT_FAILED")

    // Synthetic page prototype (views plan step 2): 40 rows of ~9.6 KB each,
    // close to the ~416-464 KB reference pages of report 259.
    let pageNames = [ for index in 1 .. 40 -> $"member {index} & \"guest\" <{String('x', 9500)}>" ]
    let expectedPage =
        "<ul>" + String.Concat(pageNames |> List.map (fun name -> "<li class=\"row\">" + TrustedValues.htmlEscape name + "</li>")) + "</ul>"
    let prototype = JsonObject()
    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        let pageValues, page = execute optimization "native.html.page" [ TList TString ] [ TString ] [ strings pageNames ]
        check $"page/{optimization}: 40-row page equals the reference rendering" (pageValues = [ StringValue expectedPage ])
        record "page-40" optimization page
        let program = nativeFor optimization "native.html.page" [ TList TString ] [ TString ]
        let inputs = [ strings pageNames ]
        for _ in 1 .. 10 do program.Execute(inputs, 16_777_216, 4_194_304) |> ignore
        let iterations = 100
        let watch = Stopwatch.StartNew()
        for _ in 1 .. iterations do program.Execute(inputs, 16_777_216, 4_194_304) |> ignore
        watch.Stop()
        // Harness floor: a copy-free search over the same page-sized input pays
        // the per-call marshaling, stack and bitmap setup of the checked profile.
        let floorProgram = nativeFor optimization "native.html.contains" [ TString; TString ] [ TBool ]
        let floorInputs = [ StringValue expectedPage; StringValue "<script" ]
        for _ in 1 .. 10 do floorProgram.Execute(floorInputs, 16_777_216, 4_194_304) |> ignore
        let floorWatch = Stopwatch.StartNew()
        for _ in 1 .. iterations do floorProgram.Execute(floorInputs, 16_777_216, 4_194_304) |> ignore
        floorWatch.Stop()
        let encodeWatch = Stopwatch.StartNew()
        let mutable utf8Bytes = 0
        for _ in 1 .. iterations do utf8Bytes <- Encoding.UTF8.GetByteCount(expectedPage)
        encodeWatch.Stop()
        let row = JsonObject()
        row["pageCodeUnits"] <- JsonValue.Create(expectedPage.Length)
        row["pageUtf8Bytes"] <- JsonValue.Create(utf8Bytes)
        row["checkedProfileHarnessMicrosecondsPerExecute"] <- JsonValue.Create(watch.Elapsed.TotalMilliseconds * 1000.0 / float iterations)
        row["checkedProfileHarnessFloorMicroseconds"] <- JsonValue.Create(floorWatch.Elapsed.TotalMilliseconds * 1000.0 / float iterations)
        row["dotnetUtf8CountMicroseconds"] <- JsonValue.Create(encodeWatch.Elapsed.TotalMilliseconds * 1000.0 / float iterations)
        row["deepCopyBytes"] <- JsonValue.Create(page.Metrics.DeepCopyBytes)
        row["moveBytes"] <- JsonValue.Create(page.Metrics.MoveBytes)
        row["peakCursorBytes"] <- JsonValue.Create(page.Metrics.ReservedStackBytes)
        prototype[string optimization] <- row
    for program in compiled.Values do (program :> IDisposable).Dispose()

    let receipt = JsonObject()
    receipt["status"] <- JsonValue.Create("pass")
    receipt["assertionCount"] <- JsonValue.Create(assertionCount.Value)
    receipt["metrics"] <- metrics
    receipt["pagePrototype"] <- prototype
    let pins = JsonArray()
    for path in [ sourcePath; vectorsPath; Path.Combine(__SOURCE_DIRECTORY__, "Program.fs")
                  Path.Combine(repo, "src/AgentLang.Llvm/OwningStackAot.fs"); Path.Combine(repo, "src/AgentLang.Llvm/ArenaLifetime.fs")
                  Path.Combine(repo, "src/AgentLang.Llvm/native/owning_stack_runtime.c"); Path.Combine(repo, "src/AgentLang.Core/TrustedValues.fs") ] do
        let pin = JsonObject()
        pin["path"] <- JsonValue.Create(Path.GetRelativePath(repo, path).Replace('\\', '/'))
        pin["sha256"] <- JsonValue.Create(hash path)
        pins.Add(pin)
    receipt["sourcePins"] <- pins
    File.WriteAllText(Path.Combine(runRoot, "receipt.json"), receipt.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + Environment.NewLine)
    printfn "owning html: %d assertions passed; artifacts %s" assertionCount.Value runRoot

[<EntryPoint>]
let main _ =
    try
        run ()
        0
    with error ->
        eprintfn "%s" (error.ToString())
        1
