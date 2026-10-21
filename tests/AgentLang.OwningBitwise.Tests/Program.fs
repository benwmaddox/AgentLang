module AgentLang.OwningBitwise.Tests

// Bitwise operators, shifts and String ordering (reopen 300): parsing and
// formatting, type errors, interpreter/native parity at O0 and O2, the shift
// range failure on both backends, and a CRC-32 written with ^, & and >>.

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open AgentLang
open AgentLang.Llvm

let private repo = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))
let private sourcePath = Path.Combine(__SOURCE_DIRECTORY__, "owning-bitwise.flow")
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
    [ "native.bits.and"; "native.bits.or"; "native.bits.xor"; "native.bits.not"; "native.bits.shift-left"; "native.bits.shift-right"
      "native.bits.has-all?"; "native.bits.mixed"; "native.bits.crc32"
      "native.text.less?"; "native.text.less-or-equal?"; "native.text.greater?"; "native.text.greater-or-equal?"; "native.text.compare" ]

/// zlib's crc32 over UTF-8 bytes, as the reference.
let private crc32 (text: string) =
    let mutable crc = 0xFFFFFFFFu
    for b in Encoding.UTF8.GetBytes text do
        crc <- crc ^^^ uint32 b
        for _ in 1..8 do
            crc <- if crc &&& 1u = 1u then (crc >>> 1) ^^^ 0xEDB88320u else crc >>> 1
    int64 (crc ^^^ 0xFFFFFFFFu)

/// Ruby's String#<=> (byte order) as the reference.
let private byteOrder (a: string) (b: string) =
    let x = Encoding.UTF8.GetBytes a
    let y = Encoding.UTF8.GetBytes b
    let mutable k = 0
    let mutable result = 0
    while result = 0 && k < x.Length && k < y.Length do
        if x[k] <> y[k] then result <- (if x[k] < y[k] then -1 else 1)
        k <- k + 1
    if result <> 0 then result else compare x.Length y.Length |> sign

let private syntaxChecks (runRoot: string) =
    let engine = Runtime.Engine(Path.Combine(runRoot, "syntax"), Set.empty, fileSystemMode = FileSystemMode.Virtual)
    let define (source: string) = engine.Dispatch("define", flowArgs source).ToJsonString()
    let rejects label (code: string) source =
        let response = define source
        check $"{label} is {code}: {response}" (response.Contains(code, StringComparison.Ordinal))
    rejects "a bare bitwise operand of ==" "FLOW_BITWISE_PARENS" "fn bad.one(a: Int, b: Int) -> Bool {\n    a & b == b\n}\n"
    rejects "a bare bitwise right operand of ==" "FLOW_BITWISE_PARENS" "fn bad.two(a: Int, b: Int) -> Bool {\n    a == b | 1\n}\n"
    rejects "a bare bitwise operand of <" "FLOW_BITWISE_PARENS" "fn bad.three(a: Int, b: Int) -> Bool {\n    a ^ b < 4\n}\n"
    rejects "a bare bitwise operand of !=" "FLOW_BITWISE_PARENS" "fn bad.four(a: Int) -> Bool {\n    a & 1 != 0\n}\n"
    rejects "& on Strings" "FLOW_OPERATOR_TYPE" "fn bad.five(a: String) -> String {\n    a & a\n}\n"
    rejects "<< on Float" "FLOW_OPERATOR_TYPE" "fn bad.six(a: Float) -> Float {\n    a << 2\n}\n"
    rejects "& on Bool" "FLOW_OPERATOR_TYPE" "fn bad.seven(a: Bool) -> Bool {\n    a & a\n}\n"
    rejects "String < Int" "FLOW_OPERATOR_TYPE" "fn bad.eight(a: String) -> Bool {\n    a < 1\n}\n"
    let accepted label source =
        let response = define source
        check $"{label} is accepted: {response}" (response.Contains("\"ok\":true", StringComparison.Ordinal))
    accepted "nested generics still close with >>" "fn good.nested() -> List<List<Int>> {\n    list.empty<List<Int>>()\n}\n"
    accepted "a parenthesized bitwise operand of ==" "fn good.mask(a: Int) -> Bool {\n    (a & 1) == 1\n}\n"
    accepted "a shift operand of < needs no parentheses" "fn good.shift(a: Int) -> Bool {\n    a << 1 < 8\n}\n"
    // The formatter keeps the required parentheses and drops redundant ones.
    let format (source: string) =
        let response = engine.Dispatch("format", flowArgs source)
        check $"format succeeds: {response.ToJsonString()}" (response["ok"].GetValue<bool>())
        (response["data"]["source"]).GetValue<string>()
    let formatted = format "fn fmt.one(a: Int, b: Int) -> Bool {\n    (a & b) == (a | b)\n}\n\nfn fmt.two(a: Int, b: Int, c: Int) -> Int {\n    ((a & b) ^ c) | (a << (2 + b))\n}\n"
    check $"formatter keeps comparison parentheses: {formatted}" (formatted.Contains("(a & b) == (a | b)", StringComparison.Ordinal))
    check $"formatter drops precedence parentheses: {formatted}" (formatted.Contains("a & b ^ c | a << 2 + b", StringComparison.Ordinal))
    check "formatted source formats to itself" (format formatted = formatted)

let private run () =
    let runRoot = Path.Combine(repo, ".agentlang", "bitwise-300", "runs", DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(runRoot) |> ignore
    syntaxChecks runRoot
    let engine = Runtime.Engine(Path.Combine(runRoot, "flow-engine"), Set.empty, fileSystemMode = FileSystemMode.Virtual)
    let defined = engine.Dispatch("define", flowArgs (File.ReadAllText sourcePath))
    if not (defined["ok"].GetValue<bool>()) then failwith (defined.ToJsonString())
    for name in words do
        let response = engine.Dispatch("commit", wordArgs name)
        check $"{name} commits (its attached test passes): {response.ToJsonString()}" (response["ok"].GetValue<bool>())

    let interpret word inputs outputs values =
        match engine.BindFlowEntryPoint(word, inputs, outputs).Invoke(values) with
        | Ok value -> Ok value.Values
        | Error (diagnostic: Diagnostic) -> Error diagnostic.Code
    let toolchain = LlvmToolchain.discover ()
    let ints = [ 0L; 1L; -1L; 2L; 7L; 12L; 255L; 4294967295L; -4294967296L; Int64.MaxValue; Int64.MinValue; 0x5555555555555555L ]
    let counts = [ 0L; 1L; 7L; 31L; 32L; 63L ]
    let texts = [ ""; "a"; "b"; "ab"; "Zebra"; "apple"; "é"; "é"; ""; "🚀"; "日本"; "a\u0000b" ]
    let configurations =
        [ "O0", LlvmOptimization.O0, OwningRuntimeProfile.Diagnostic
          "O2", LlvmOptimization.O2, OwningRuntimeProfile.Diagnostic
          "trusted-O2", LlvmOptimization.O2, OwningRuntimeProfile.TrustedGenerated ]
    for optimization, level, profile in configurations do
        let native word inputs outputs =
            let body = engine.ExportVerifiedFlowEntryPoint(word, inputs, outputs)
            OwningStackAot.compileWithRuntimeProfile toolchain level profile (Path.Combine(runRoot, "native", optimization, word.Replace("?", "-p"))) body
        let runNative (program: OwningStackCompiledProgram) values =
            try Ok(program.Execute(values, 65536, 4096).Values)
            with :? OwningStackExecutionException as failure -> Error failure.Diagnostic.Code
        let pair word inputs outputs (cases: Value list list) (expected: Value list -> Result<Value list, string>) =
            use program = native word inputs outputs
            for values in cases do
                let label = $"{word} {values}/{optimization}"
                let interpreted = interpret word inputs outputs values
                check $"{label}: interpreter {interpreted} = expected" (interpreted = expected values)
                let nativeResult = runNative program values
                check $"{label}: native {nativeResult} = interpreter" (nativeResult = interpreted)
        let intPairs = [ for a in ints do for b in ints -> [ IntValue a; IntValue b ] ]
        let binary word (f: int64 -> int64 -> int64) =
            pair word [ TInt; TInt ] [ TInt ] intPairs (function
                | [ IntValue a; IntValue b ] -> Ok [ IntValue(f a b) ]
                | other -> failwith $"bad case {other}")
        binary "native.bits.and" (&&&)
        binary "native.bits.or" (|||)
        binary "native.bits.xor" (^^^)
        pair "native.bits.not" [ TInt ] [ TInt ] [ for a in ints -> [ IntValue a ] ] (function
            | [ IntValue a ] -> Ok [ IntValue(~~~a) ]
            | other -> failwith $"bad case {other}")
        let shiftCases = [ for a in ints do for n in counts @ [ -1L; 64L; 1000L; Int64.MinValue ] -> [ IntValue a; IntValue n ] ]
        let shift word (f: int64 -> int -> int64) =
            pair word [ TInt; TInt ] [ TInt ] shiftCases (function
                | [ IntValue _; IntValue n ] when n < 0L || n > 63L -> Error "RUNTIME_SHIFT_RANGE"
                | [ IntValue a; IntValue n ] -> Ok [ IntValue(f a (int n)) ]
                | other -> failwith $"bad case {other}")
        shift "native.bits.shift-left" (<<<)
        shift "native.bits.shift-right" (>>>)
        pair "native.bits.has-all?" [ TInt; TInt ] [ TBool ] intPairs (function
            | [ IntValue a; IntValue b ] -> Ok [ BoolValue((a &&& b) = b) ]
            | other -> failwith $"bad case {other}")
        let triples = [ for a in [ 0L; 6L; -1L; 255L ] do for b in [ 0L; 3L; 9L ] do for c in [ 0L; 5L; -7L ] -> [ IntValue a; IntValue b; IntValue c ] ]
        pair "native.bits.mixed" [ TInt; TInt; TInt ] [ TInt ] triples (function
            | [ IntValue a; IntValue b; IntValue c ] -> Ok [ IntValue(((a &&& b) ^^^ c) ||| (a <<< int (2L + b))) ]
            | other -> failwith $"bad case {other}")
        pair "native.bits.crc32" [ TString ] [ TInt ] [ for t in texts @ [ "123456789"; "The quick brown fox"; String('x', 300) ] -> [ StringValue t ] ] (function
            | [ StringValue t ] -> Ok [ IntValue(crc32 t) ]
            | other -> failwith $"bad case {other}")
        let textPairs = [ for a in texts do for b in texts -> [ StringValue a; StringValue b ] ]
        let ordered word (f: int -> bool) =
            pair word [ TString; TString ] [ TBool ] textPairs (function
                | [ StringValue a; StringValue b ] -> Ok [ BoolValue(f (byteOrder a b)) ]
                | other -> failwith $"bad case {other}")
        ordered "native.text.less?" (fun order -> order < 0)
        ordered "native.text.less-or-equal?" (fun order -> order <= 0)
        ordered "native.text.greater?" (fun order -> order > 0)
        ordered "native.text.greater-or-equal?" (fun order -> order >= 0)
        pair "native.text.compare" [ TString; TString ] [ TInt ] textPairs (function
            | [ StringValue a; StringValue b ] -> Ok [ IntValue(int64 (byteOrder a b)) ]
            | other -> failwith $"bad case {other}")
    // Byte order is not ordinal UTF-16 order: U+E000 sorts before U+1F680 by bytes.
    check "U+E000 sorts before an emoji by UTF-8 bytes" (byteOrder "" "🚀" < 0 && String.CompareOrdinal("", "🚀") > 0)

    let receipt = JsonObject()
    receipt["status"] <- JsonValue.Create("pass")
    receipt["assertionCount"] <- JsonValue.Create(assertionCount.Value)
    File.WriteAllText(Path.Combine(runRoot, "receipt.json"), receipt.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + Environment.NewLine)
    printfn "owning bitwise: %d assertions passed; artifacts %s" assertionCount.Value runRoot

[<EntryPoint>]
let main _ =
    try
        run ()
        0
    with error ->
        eprintfn "%s" (error.ToString())
        1
