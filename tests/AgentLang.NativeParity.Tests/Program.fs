module AgentLang.NativeParity.Tests

// Interpreter/native parity of the builtin set (report 303): every primitive
// the interpreter runs either has a native lowering or is on
// Compiler.interpreterOnlyPrimitives with its reason, and the hand-kept lists
// agree. The primitives this report lowered must give the interpreter's
// answers natively, and a server build must reject an interpreter-only
// primitive before any attached test runs.

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open AgentLang
open AgentLang.Llvm
open AgentLang.FlowHttp

let private repo = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))
let private sourcePath = Path.Combine(__SOURCE_DIRECTORY__, "native-parity.flow")
let private assertionCount = ref 0

let private check label condition =
    assertionCount.Value <- assertionCount.Value + 1
    if not condition then failwith $"Assertion failed: {label}"

let private show (names: Set<string>) = String.Join(", ", names)

let private setChecks () =
    let testIntrinsics = set [ "<expect>"; "<expect-equal>" ]
    let interpreter = IrInterpreter.implementedPrimitiveOperations - testIntrinsics
    let catalog = Compiler.primitives |> Map.keys |> Set.ofSeq
    let native = OwningStackAot.nativePrimitives
    let interpreterOnly = Compiler.interpreterOnlyPrimitives |> Map.keys |> Set.ofSeq
    check $"the compiler catalog and the interpreter list the same primitives (catalog only: {show (catalog - interpreter)}; interpreter only: {show (interpreter - catalog)})" (catalog = interpreter)
    check $"every native primitive runs in the interpreter (native only: {show (native - interpreter)})" (Set.isSubset native interpreter)
    let missing = interpreter - native - interpreterOnly
    check $"every primitive has a native lowering or a stated reason (missing: {show missing})" missing.IsEmpty
    let stale = Set.intersect native interpreterOnly
    check $"no primitive is both native and interpreter-only (stale reasons: {show stale})" stale.IsEmpty
    check $"interpreter-only names are real primitives (unknown: {show (interpreterOnly - interpreter)})" (Set.isSubset interpreterOnly interpreter)
    check "every interpreter-only primitive has a reason" (Compiler.interpreterOnlyPrimitives |> Map.forall (fun _ reason -> reason.Length > 10))
    let lifetime = OwningStackAot.lifetimeFreshPrimitives
    check $"the lifetime analysis names only native primitives (unknown: {show (lifetime - native)})" (Set.isSubset lifetime native)
    native.Count, interpreterOnly.Count

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

let private valueChecks (runRoot: string) =
    let engine = Runtime.Engine(Path.Combine(runRoot, "flow-engine"), Set.empty, fileSystemMode = FileSystemMode.Virtual)
    let defined = engine.Dispatch("define", flowArgs (File.ReadAllText sourcePath))
    if not (defined["ok"].GetValue<bool>()) then failwith (defined.ToJsonString())
    for name in [ "native.parity.member?"; "native.parity.literal-sum"; "native.parity.literal-grow"; "native.parity.literal-join"; "native.parity.same?"; "native.parity.same-bytes?"; "native.parity.same-label?"; "native.parity.abs"; "native.parity.min"; "native.parity.max"; "native.parity.empty?"; "native.parity.trim"; "native.parity.next-in"; "native.parity.next-not-in"; "native.parity.next-in-literal"; "native.parity.next-not-in-literal" ] do
        let response = engine.Dispatch("commit", wordArgs name)
        check $"{name} commits: {response.ToJsonString()}" (response["ok"].GetValue<bool>())
    let interpret word inputs outputs values =
        match engine.BindFlowEntryPoint(word, inputs, outputs).Invoke(values) with
        | Ok value -> Ok value.Values
        | Error(diagnostic: Diagnostic) -> Error diagnostic.Code
    let toolchain = LlvmToolchain.discover ()
    let ints = [ 0L; 1L; -1L; 7L; -7L; Int64.MaxValue; Int64.MinValue; Int64.MinValue + 1L ]
    // .NET String.Trim removes Char.IsWhiteSpace; ZWSP, BOM and U+180E are not white space.
    let texts =
        [ ""; " "; "a"; "  a b  "; "\t\n\r\u000b\u000c x \u0085"; " é "; "　日本　"; "  x  "
          "  x "; " y "; "​z​"; "﻿w﻿"; "᠎v᠎"; "🚀 "; " 🚀"; "   " ]
    let configurations =
        [ "O0", LlvmOptimization.O0, OwningRuntimeProfile.Diagnostic
          "O2", LlvmOptimization.O2, OwningRuntimeProfile.Diagnostic
          "trusted-O2", LlvmOptimization.O2, OwningRuntimeProfile.TrustedGenerated ]
    for configuration, level, profile in configurations do
        let native word inputs outputs =
            let body = engine.ExportVerifiedFlowEntryPoint(word, inputs, outputs)
            OwningStackAot.compileWithRuntimeProfile toolchain level profile (Path.Combine(runRoot, "native", configuration, word.Replace("?", "-p"))) body
        let runNative (program: OwningStackCompiledProgram) values =
            try Ok(program.Execute(values, 65536, 65536).Values)
            with :? OwningStackExecutionException as failure -> Error failure.Diagnostic.Code
        let pair word inputs outputs (cases: Value list list) (expected: Value list -> Result<Value list, string>) =
            use program = native word inputs outputs
            for values in cases do
                let label = $"{word} {JsonSerializer.Serialize(string values)}/{configuration}"
                let interpreted = interpret word inputs outputs values
                check $"{label}: interpreter {interpreted} = expected" (interpreted = expected values)
                let nativeResult = runNative program values
                check $"{label}: native {nativeResult} = interpreter" (nativeResult = interpreted)
        pair "native.parity.abs" [ TInt ] [ TInt ] [ for n in ints -> [ IntValue n ] ] (function
            | [ IntValue n ] when n = Int64.MinValue -> Error "RUNTIME_OVERFLOW"
            | [ IntValue n ] -> Ok [ IntValue(abs n) ]
            | other -> failwith $"bad case {other}")
        let intPairs = [ for a in ints do for b in ints -> [ IntValue a; IntValue b ] ]
        pair "native.parity.min" [ TInt; TInt ] [ TInt ] intPairs (function
            | [ IntValue a; IntValue b ] -> Ok [ IntValue(min a b) ]
            | other -> failwith $"bad case {other}")
        pair "native.parity.max" [ TInt; TInt ] [ TInt ] intPairs (function
            | [ IntValue a; IntValue b ] -> Ok [ IntValue(max a b) ]
            | other -> failwith $"bad case {other}")
        // Byte scans (report 312): ASCII and multi-byte texts and sets, from below 0,
        // inside, at the end and past it; the answer is a byte index.
        let scanTexts = [ ""; "abc"; "a<b>&c"; "  x y "; "é<🚀&"; " <p>"; String('x', 40) + "<" ]
        let scanSets = [ ""; "<"; "<>&"; " "; "abcx"; "é"; " " ]
        let scanCases = [ for text in scanTexts do for set in scanSets do for from in [ -3L; 0L; 1L; 2L; 41L; 99L ] -> [ StringValue text; StringValue set; IntValue from ] ]
        let scan wanted (text: string) (set: string) (from: int64) =
            let bytes = Text.Encoding.UTF8.GetBytes text
            let members = Text.Encoding.UTF8.GetBytes set |> Set.ofArray
            let mutable index = int (max 0L (min from (int64 bytes.Length)))
            while index < bytes.Length && members.Contains bytes[index] <> wanted do index <- index + 1
            int64 index
        pair "native.parity.next-in" [ TString; TString; TInt ] [ TInt ] scanCases (function
            | [ StringValue text; StringValue set; IntValue from ] -> Ok [ IntValue(scan true text set from) ]
            | other -> failwith $"bad case {other}")
        pair "native.parity.next-not-in" [ TString; TString; TInt ] [ TInt ] scanCases (function
            | [ StringValue text; StringValue set; IntValue from ] -> Ok [ IntValue(scan false text set from) ]
            | other -> failwith $"bad case {other}")
        // Literal sets: the compiler's byte table must match the runtime's.
        let literalTexts = scanTexts @ [ "ab-1>"; "\u007f~"; "x" + String('a', 70) + "\u00e9"; "AZaz09-:" ]
        let literalCases = [ for text in literalTexts do for from in [ -1L; 0L; 1L; 3L; 80L ] -> [ StringValue text; IntValue from ] ]
        pair "native.parity.next-in-literal" [ TString; TInt ] [ TInt ] literalCases (function
            | [ StringValue text; IntValue from ] -> Ok [ IntValue(scan true text "<>&\u00e9\u00a0~\u007f" from) ]
            | other -> failwith $"bad case {other}")
        pair "native.parity.next-not-in-literal" [ TString; TInt ] [ TInt ] literalCases (function
            | [ StringValue text; IntValue from ] -> Ok [ IntValue(scan false text "abcdefghijklmnopqrstuvwxyz0123456789-:" from) ]
            | other -> failwith $"bad case {other}")
        let lists = [ []; [ "a" ]; [ ""; "b" ]; [ for k in 1..20 -> string k ] ]
        pair "native.parity.empty?" [ TList TString ] [ TBool ] [ for items in lists -> [ ListValue(TString, items |> List.map StringValue) ] ] (function
            | [ ListValue(_, items) ] -> Ok [ BoolValue items.IsEmpty ]
            | other -> failwith $"bad case {other}")
        // Literal lists built once at compile time (report 304), called repeatedly.
        let memberCases = [ for name in [ "a"; "em"; "🚀"; "é"; "x"; ""; "span"; "spa"; "strong" ] -> [ StringValue name ] ]
        let members = set [ "a"; "abbr"; "b"; "blockquote"; "br"; "code"; "div"; "em"; "h1"; "p"; "pre"; "span"; "strong"; "é"; "🚀" ]
        pair "native.parity.member?" [ TString ] [ TBool ] (memberCases @ memberCases) (function
            | [ StringValue name ] -> Ok [ BoolValue(members.Contains name) ]
            | other -> failwith $"bad case {other}")
        pair "native.parity.literal-sum" [ TInt ] [ TInt ] [ for k in [ 0L; 1L; -303L; 5L; 0L ] -> [ IntValue k ] ] (function
            | [ IntValue k ] -> Ok [ IntValue(303L + k) ]
            | other -> failwith $"bad case {other}")
        pair "native.parity.literal-grow" [ TString ] [ TString ] [ for text in [ "z"; ""; "long text"; "🚀"; "z" ] -> [ StringValue text ] ] (function
            | [ StringValue text ] -> Ok [ StringValue("xyy" + text + text) ]
            | other -> failwith $"bad case {other}")
        pair "native.parity.literal-join" [ TString ] [ TString ] [ for text in [ "!"; ""; "é" ] -> [ StringValue text ] ] (function
            | [ StringValue text ] -> Ok [ StringValue("<b>" + text) ]
            | other -> failwith $"bad case {other}")
        // Equality (report 304): lengths on and off the 8-byte word, equal prefixes, Unicode.
        let words = [ ""; "a"; "b"; "abcdefg"; "abcdefgh"; "abcdefgi"; "abcdefghi"; "abcdefghj"; "abcdefghijklmnop"; "abcdefghijklmnoq"; "é"; "e\u0301"; "🚀🚀"; String('x', 100); String('x', 99) + "y" ]
        let wordPairs = [ for a in words do for b in words -> [ StringValue a; StringValue b ] ]
        for word in [ "native.parity.same?"; "native.parity.same-bytes?"; "native.parity.same-label?" ] do
            pair word [ TString; TString ] [ TBool ] wordPairs (function
                | [ StringValue a; StringValue b ] -> Ok [ BoolValue(String.Equals(a, b, StringComparison.Ordinal)) ]
                | other -> failwith $"bad case {other}")
        pair "native.parity.trim" [ TString ] [ TString ] [ for t in texts -> [ StringValue t ] ] (function
            | [ StringValue t ] -> Ok [ StringValue(t.Trim()) ]
            | other -> failwith $"bad case {other}")

/// Trusted measuring and case access (report 304) against the full scanner: the same
/// programs over every value kind, compared between the interpreter, diagnostic O2 (the
/// validating scan) and trusted O2 (the fast path), on seeded random values.
let private shapeChecks (runRoot: string) =
    let engine = Runtime.Engine(Path.Combine(runRoot, "shapes-engine"), Set.empty, fileSystemMode = FileSystemMode.Virtual)
    let defined = engine.Dispatch("define", flowArgs (File.ReadAllText(Path.Combine(__SOURCE_DIRECTORY__, "native-shapes.flow"))))
    if not (defined["ok"].GetValue<bool>()) then failwith (defined.ToJsonString())
    for name in [ "native.shapes.describe"; "native.shapes.pick"; "native.shapes.last-tag" ] do
        let response = engine.Dispatch("commit", wordArgs name)
        check $"{name} commits: {response.ToJsonString()}" (response["ok"].GetValue<bool>())
    let random = Random(304)
    let alphabet = [| "a"; "b"; "é"; "🚀"; "日"; " "; "<"; "&" |]
    let text () = String.Join("", [| for _ in 1 .. random.Next(0, 21) -> alphabet[random.Next(alphabet.Length)] |])
    let note () =
        RecordValue(
            "Note",
            Map.ofList
                [ "title", StringValue(text ())
                  "tags", ListValue(TString, [ for _ in 1 .. random.Next(0, 6) -> StringValue(text ()) ])
                  "extra", OptionValue(TString, (if random.Next(2) = 0 then None else Some(StringValue(text ()))))
                  "score", ResultValue(TInt, TString, (if random.Next(2) = 0 then Ok(IntValue(int64 (random.Next(-1000, 1000)))) else Error(StringValue(text ()))))
                  "flag", BoolValue(random.Next(2) = 0) ])
    let shelf () =
        let notes = [ for _ in 1 .. random.Next(0, 5) -> note () ]
        RecordValue(
            "Shelf",
            Map.ofList
                [ "name", StringValue(text ())
                  "notes", ListValue(TNamed "Note", notes)
                  "best", OptionValue(TNamed "Note", (if random.Next(3) = 0 then None else Some(note ()))) ])
    let shelves = [ for _ in 1 .. 24 -> ListValue(TNamed "Shelf", [ for _ in 1 .. random.Next(0, 5) -> shelf () ]) ]
    /// The describe reference, from the F# values.
    let describe (value: Value) =
        let parts = ResizeArray<string>()
        let field (fields: Map<string, Value>) name = fields[name]
        match value with
        | ListValue(_, items) ->
            for item in items do
                match item with
                | RecordValue(_, s) ->
                    match field s "name" with StringValue n -> parts.Add n | _ -> ()
                    match field s "notes" with
                    | ListValue(_, notes) ->
                        for n in notes do
                            match n with
                            | RecordValue(_, f) ->
                                match field f "title" with StringValue t -> parts.Add t | _ -> ()
                                match field f "tags" with ListValue(_, tags) -> (for t in tags do match t with StringValue v -> parts.Add v | _ -> ()) | _ -> ()
                                match field f "extra" with OptionValue(_, Some(StringValue e)) -> parts.Add e | _ -> parts.Add "-"
                                match field f "score" with
                                | ResultValue(_, _, Ok(IntValue k)) -> parts.Add(string k)
                                | ResultValue(_, _, Error(StringValue e)) -> parts.Add e
                                | _ -> ()
                                match field f "flag" with BoolValue true -> parts.Add "y" | _ -> parts.Add "n"
                            | _ -> ()
                    | _ -> ()
                    match field s "best" with
                    | OptionValue(_, Some(RecordValue(_, b))) -> (match field b "title" with StringValue t -> parts.Add t | _ -> ())
                    | _ -> parts.Add "no best"
                | _ -> ()
        | _ -> ()
        String.Join("", parts)
    let interpret word inputs outputs values =
        match engine.BindFlowEntryPoint(word, inputs, outputs).Invoke(values) with
        | Ok value -> Ok value.Values
        | Error(diagnostic: Diagnostic) -> Error diagnostic.Code
    let toolchain = LlvmToolchain.discover ()
    let shelvesType = TList(TNamed "Shelf")
    for configuration, level, profile in [ "O2", LlvmOptimization.O2, OwningRuntimeProfile.Diagnostic; "trusted-O2", LlvmOptimization.O2, OwningRuntimeProfile.TrustedGenerated ] do
        let native word inputs outputs =
            let body = engine.ExportVerifiedFlowEntryPoint(word, inputs, outputs)
            OwningStackAot.compileWithRuntimeProfile toolchain level profile (Path.Combine(runRoot, "shapes", configuration, word)) body
        let runNative (program: OwningStackCompiledProgram) values =
            try Ok(program.Execute(values, 1 <<< 20, 1 <<< 20).Values)
            with :? OwningStackExecutionException as failure -> Error failure.Diagnostic.Code
        use describeProgram = native "native.shapes.describe" [ shelvesType ] [ TString ]
        use pickProgram = native "native.shapes.pick" [ shelvesType; TInt; TInt ] [ TOption(TNamed "Note") ]
        use lastProgram = native "native.shapes.last-tag" [ shelvesType ] [ TOption TString ]
        shelves |> List.iteri (fun index value ->
            let interpreted = interpret "native.shapes.describe" [ shelvesType ] [ TString ] [ value ]
            check $"shapes {index}/{configuration}: interpreter describe = reference" (interpreted = Ok [ StringValue(describe value) ])
            check $"shapes {index}/{configuration}: native describe = interpreter" (runNative describeProgram [ value ] = interpreted)
            for shelfIndex in -1L .. 4L do
                for noteIndex in [ -1L; 0L; 1L; 3L; 9L ] do
                    let inputs = [ value; IntValue shelfIndex; IntValue noteIndex ]
                    let expected = interpret "native.shapes.pick" [ shelvesType; TInt; TInt ] [ TOption(TNamed "Note") ] inputs
                    check $"shapes {index} pick {shelfIndex},{noteIndex}/{configuration}: native = interpreter" (runNative pickProgram inputs = expected)
            let lastExpected = interpret "native.shapes.last-tag" [ shelvesType ] [ TOption TString ] [ value ]
            check $"shapes {index} last-tag/{configuration}: native = interpreter" (runNative lastProgram [ value ] = lastExpected))

/// A server whose handler reaches an interpreter-only primitive fails the native
/// check before its attached tests run: the counter app with counter.count on
/// list.tail, so its own test (expecting 2) would fail too if tests ran first.
let private earlyCheck (runRoot: string) =
    let counter = File.ReadAllText(Path.Combine(repo, "tests/AgentLang.FlowHttp.Tests/app/counter.flow"))
    let original = "    var count = 0\n    for header in headers {\n        count = count + 1\n    }\n    count\n"
    check "the counter app still has the counting loop this check edits" (counter.Contains(original, StringComparison.Ordinal))
    let edited = counter.Replace(original, "    list.count(list.tail(headers))\n")
    let path = Path.Combine(runRoot, "counter-tail.flow")
    File.WriteAllText(path, edited, UTF8Encoding(false))
    let outcome =
        try
            use _app = FlowApp.loadNativeVerified (Path.Combine(runRoot, "early")) "counter" [ path ] ":memory:" 0
            "built"
        with error -> error.Message
    check $"the native check names the primitive before any test runs ({outcome})"
        (outcome.StartsWith("Native check, before the attached tests", StringComparison.Ordinal)
         && outcome.Contains("IR_OWNING_STACK_PRIMITIVE_UNSUPPORTED", StringComparison.Ordinal)
         && outcome.Contains("list.tail", StringComparison.Ordinal))

let private run () =
    let runRoot = Path.Combine(repo, ".agentlang", "native-parity-303", "runs", DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(runRoot) |> ignore
    let nativeCount, onlyCount = setChecks ()
    valueChecks runRoot
    shapeChecks runRoot
    earlyCheck runRoot
    printfn "native parity: %d assertions passed (%d native primitives, %d interpreter-only with reasons); artifacts %s" assertionCount.Value nativeCount onlyCount runRoot

[<EntryPoint>]
let main _ =
    try
        run ()
        0
    with error ->
        eprintfn "%s" (error.ToString())
        1
