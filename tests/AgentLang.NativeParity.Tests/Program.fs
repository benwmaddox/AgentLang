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
    for name in [ "native.parity.same?"; "native.parity.same-bytes?"; "native.parity.same-label?"; "native.parity.abs"; "native.parity.min"; "native.parity.max"; "native.parity.empty?"; "native.parity.trim" ] do
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
        let lists = [ []; [ "a" ]; [ ""; "b" ]; [ for k in 1..20 -> string k ] ]
        pair "native.parity.empty?" [ TList TString ] [ TBool ] [ for items in lists -> [ ListValue(TString, items |> List.map StringValue) ] ] (function
            | [ ListValue(_, items) ] -> Ok [ BoolValue items.IsEmpty ]
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
