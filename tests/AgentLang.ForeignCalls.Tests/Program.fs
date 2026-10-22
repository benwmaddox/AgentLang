module AgentLang.ForeignCalls.Tests

// C bindings (report 307): the test library flowffi (built here with the
// toolchain's clang) bound with `= c "<prototype>" from "flowffi"`; parsing and
// formatting, type errors, each ABI shape in the interpreter and native builds,
// argument ranges, and missing libraries and symbols.

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open AgentLang
open AgentLang.Llvm

let private repo = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))
let private sourcePath = Path.Combine(__SOURCE_DIRECTORY__, "ffi.flow")
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

/// flowffi.dll next to this executable, where both backends look first.
let private buildLibrary (toolchain: LlvmToolchain) =
    let output = Path.Combine(AppContext.BaseDirectory, "flowffi.dll")
    let source = Path.Combine(__SOURCE_DIRECTORY__, "flowffi.c")
    let info = ProcessStartInfo(toolchain.ClangPath, $"--target=x86_64-pc-windows-msvc -shared -fuse-ld=lld -O2 -o \"{output}\" \"{source}\"", RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false)
    use proc = Process.Start info
    let errors = proc.StandardError.ReadToEnd()
    proc.WaitForExit()
    if proc.ExitCode <> 0 then failwith $"clang failed: {errors}"

let private bindings =
    [ "ffi.add64"; "ffi.xor32"; "ffi.negate8"; "ffi.double16"; "ffi.flip-high"; "ffi.next-long"; "ffi.negate-long"; "ffi.scale"; "ffi.even?"; "ffi.not"
      "ffi.sum"; "ffi.first"; "ffi.middle"; "ffi.last"; "ffi.text-bytes"; "ffi.compare"; "ffi.crc32-update"; "ffi.crc32" ]

let private syntaxChecks (runRoot: string) =
    let engine = Runtime.Engine(Path.Combine(runRoot, "syntax"), Set.empty, fileSystemMode = FileSystemMode.Virtual)
    let define (source: string) = engine.Dispatch("define", flowArgs source).ToJsonString()
    let rejects label (code: string) source =
        let response = define source
        check $"{label} is {code}: {response}" (response.Contains(code, StringComparison.Ordinal))
    rejects "an unknown C type" "FLOW_C_PROTOTYPE" "fn bad.one(a: Int) -> Int = c \"int64_t f(float)\" from \"m\"\n"
    rejects "a prototype without parentheses" "FLOW_C_PROTOTYPE" "fn bad.two(a: Int) -> Int = c \"int64_t f\" from \"m\"\n"
    rejects "a returned pointer" "FLOW_C_PROTOTYPE" "fn bad.three(a: Bytes) -> Bytes = c \"bytes f(bytes)\" from \"m\"\n"
    rejects "a String passed as bytes" "FLOW_C_BINDING_TYPE" "fn bad.four(a: String) -> Int = c \"int64_t f(bytes)\" from \"m\"\n"
    rejects "too few parameters" "FLOW_C_BINDING_TYPE" "fn bad.five(a: Int) -> Int = c \"int64_t f(int64_t, int64_t)\" from \"m\"\n"
    rejects "a declared output that the return does not give" "TYPE_WORD_OUTPUT_MISMATCH" "fn bad.six(a: Int) -> String = c \"int64_t f(int64_t)\" from \"m\"\n"
    rejects "a binding without its library" "FLOW_C_BINDING" "fn bad.seven(a: Int) -> Int = c \"int64_t f(int64_t)\"\n"
    let format (source: string) =
        let response = engine.Dispatch("format", flowArgs source)
        check $"format succeeds: {response.ToJsonString()}" (response["ok"].GetValue<bool>())
        (response["data"]["source"]).GetValue<string>()
    let source = "fn x.sum(data: Bytes) -> Int = c \"int64_t ffi_sum(bytes)\" from \"flowffi\"\n\nfn x.read(data: Bytes) -> Int = c \"int64_t ffi_sum(bytes)\" from \"flowffi\" effects fs.read\n"
    let formatted = format source
    check $"a binding formats as it is written: {formatted}" (formatted.Contains("fn x.sum(data: Bytes) -> Int = c \"int64_t ffi_sum(bytes)\" from \"flowffi\"", StringComparison.Ordinal))
    check $"a binding keeps its effects: {formatted}" (formatted.Contains("from \"flowffi\" effects fs.read", StringComparison.Ordinal))
    check "formatted bindings format to themselves" (format formatted = formatted)

let private run () =
    let runRoot = Path.Combine(repo, ".agentlang", "foreign-307", "runs", DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(runRoot) |> ignore
    let toolchain = LlvmToolchain.discover ()
    buildLibrary toolchain
    syntaxChecks runRoot
    let engine = Runtime.Engine(Path.Combine(runRoot, "flow-engine"), Set.empty, fileSystemMode = FileSystemMode.Virtual)
    let defined = engine.Dispatch("define", flowArgs (File.ReadAllText sourcePath))
    if not (defined["ok"].GetValue<bool>()) then failwith (defined.ToJsonString())
    for name in bindings do
        let response = engine.Dispatch("commit", wordArgs name)
        let text = response.ToJsonString()
        check $"{name} commits (its attached tests pass in the interpreter): {text}" (response["ok"].GetValue<bool>() || text.Contains("COMMIT_NOT_CANDIDATE", StringComparison.Ordinal))
    let interpret word inputs outputs values =
        match engine.BindFlowEntryPoint(word, inputs, outputs).Invoke(values) with
        | Ok value -> Ok value.Values
        | Error (diagnostic: Diagnostic) -> Error diagnostic.Code
    let windows = OperatingSystem.IsWindows()
    let ints = [ 0L; 1L; -1L; 127L; -128L; 128L; 255L; 256L; 65535L; 65536L; 2147483647L; -2147483648L; 4294967295L; 4294967296L; 5000000000L; Int64.MaxValue; Int64.MinValue ]
    let range signed bits =
        if bits = 64 then Int64.MinValue, Int64.MaxValue
        elif signed then -(1L <<< (bits - 1)), (1L <<< (bits - 1)) - 1L
        else 0L, (1L <<< bits) - 1L
    let unary word signed bits (f: int64 -> int64) =
        for value in ints do
            let low, high = range signed bits
            let expected = if value < low || value > high then Error "RUNTIME_C_ARGUMENT_RANGE" else Ok [ IntValue(f value) ]
            check $"{word}({value})" (interpret word [ TInt ] [ TInt ] [ IntValue value ] = expected)
    unary "ffi.negate8" true 8 (fun v -> int64 (sbyte (-(int v))))
    unary "ffi.double16" false 16 (fun v -> int64 (uint16 (v * 2L)))
    unary "ffi.flip-high" false 64 (fun v -> v ^^^ (int64 0xFFFFFFFF00000000UL))
    let longBits = if windows then 32 else 64
    unary "ffi.next-long" false longBits (fun v -> if longBits = 32 then int64 (uint32 (v + 1L)) else v + 1L)
    unary "ffi.negate-long" true longBits (fun v -> if longBits = 32 then int64 (-(int32 v)) else -v)
    for a in ints do
        for b in [ 0L; 1L; -1L; 4294967296L ] do
            let expected =
                if a < 0L || a > 4294967295L || b < -2147483648L || b > 2147483647L then Error "RUNTIME_C_ARGUMENT_RANGE"
                else Ok [ IntValue(int64 (uint32 a ^^^ uint32 (int32 b))) ]
            check $"ffi.xor32({a}, {b})" (interpret "ffi.xor32" [ TInt; TInt ] [ TInt ] [ IntValue a; IntValue b ] = expected)
    check "ffi.add64 wraps as C does" (interpret "ffi.add64" [ TInt; TInt ] [ TInt ] [ IntValue Int64.MaxValue; IntValue 1L ] = Ok [ IntValue Int64.MinValue ])
    check "ffi.scale" (interpret "ffi.scale" [ TFloat; TInt ] [ TFloat ] [ FloatValue 2.5; IntValue -3L ] = Ok [ FloatValue -7.5 ])
    check "ffi.even? false" (interpret "ffi.even?" [ TInt ] [ TBool ] [ IntValue 7L ] = Ok [ BoolValue false ])
    check "ffi.not true" (interpret "ffi.not" [ TBool ] [ TBool ] [ BoolValue true ] = Ok [ BoolValue false ])
    let bytes (text: string) = BytesValue(Text.Encoding.UTF8.GetBytes text)
    check "empty bytes are a non-null pointer" (interpret "ffi.sum" [ TBytes ] [ TInt ] [ bytes "" ] = Ok [ IntValue 0L ])
    check "bytes first" (interpret "ffi.first" [ TBytes; TInt ] [ TInt ] [ bytes "ab"; IntValue 5L ] = Ok [ IntValue 195005L ])
    check "bytes in the middle" (interpret "ffi.middle" [ TInt; TBytes; TInt ] [ TInt ] [ IntValue 2L; bytes ""; IntValue 9L ] = Ok [ IntValue 2000009L ])
    check "bytes last" (interpret "ffi.last" [ TInt; TBytes ] [ TInt ] [ IntValue 4L; bytes (String('x', 300)) ] = Ok [ IntValue 4300L ])
    check "text is UTF-8" (interpret "ffi.text-bytes" [ TString ] [ TInt ] [ StringValue "é🚀" ] = Ok [ IntValue 6L ])
    check "text and bytes" (interpret "ffi.compare" [ TString; TBytes ] [ TInt ] [ StringValue "abc"; bytes "abd" ] = Ok [ IntValue -1L ])
    check "crc32 with a nonzero seed and empty data keeps the seed" (interpret "ffi.crc32-update" [ TInt; TBytes ] [ TInt ] [ IntValue 3421780262L; bytes "" ] = Ok [ IntValue 3421780262L ])

    // Native builds call the same functions: every binding at O0 and trusted O2,
    // against the interpreter, including the range failures.
    let configurations =
        [ "O0", LlvmOptimization.O0, OwningRuntimeProfile.Diagnostic
          "trusted-O2", LlvmOptimization.O2, OwningRuntimeProfile.TrustedGenerated ]
    let intCases = [ for value in ints -> [ IntValue value ] ]
    let pairs = [ for a in ints do for b in [ 0L; 1L; -1L; 4294967296L ] -> [ IntValue a; IntValue b ] ]
    let byteCases = [ for text in [ ""; "a"; "ab"; String('x', 300); "é🚀" ] -> BytesValue(Text.Encoding.UTF8.GetBytes text) ]
    let entries =
        [ "ffi.add64", [ TInt; TInt ], [ TInt ], pairs
          "ffi.xor32", [ TInt; TInt ], [ TInt ], pairs
          "ffi.negate8", [ TInt ], [ TInt ], intCases
          "ffi.double16", [ TInt ], [ TInt ], intCases
          "ffi.flip-high", [ TInt ], [ TInt ], intCases
          "ffi.next-long", [ TInt ], [ TInt ], intCases
          "ffi.negate-long", [ TInt ], [ TInt ], intCases
          "ffi.scale", [ TFloat; TInt ], [ TFloat ], [ for x in [ 0.0; 2.5; -1e300 ] do for n in [ 0L; 3L; -3L; 4294967296L ] -> [ FloatValue x; IntValue n ] ]
          "ffi.even?", [ TInt ], [ TBool ], intCases
          "ffi.not", [ TBool ], [ TBool ], [ [ BoolValue true ]; [ BoolValue false ] ]
          "ffi.sum", [ TBytes ], [ TInt ], [ for data in byteCases -> [ data ] ]
          "ffi.first", [ TBytes; TInt ], [ TInt ], [ for data in byteCases -> [ data; IntValue 9L ] ]
          "ffi.middle", [ TInt; TBytes; TInt ], [ TInt ], [ for data in byteCases -> [ IntValue -2L; data; IntValue 5L ] ]
          "ffi.last", [ TInt; TBytes ], [ TInt ], [ for data in byteCases -> [ IntValue 3L; data ] ]
          "ffi.text-bytes", [ TString ], [ TInt ], [ for text in [ ""; "a"; "é🚀"; String('y', 5000) ] -> [ StringValue text ] ]
          "ffi.compare", [ TString; TBytes ], [ TInt ], [ for text in [ ""; "a"; "ab" ] do for data in byteCases -> [ StringValue text; data ] ]
          "ffi.crc32-update", [ TInt; TBytes ], [ TInt ], [ for seed in [ 0L; 3421780262L; 4294967296L ] do for data in byteCases -> [ IntValue seed; data ] ]
          "ffi.crc32", [ TBytes ], [ TInt ], [ for data in byteCases -> [ data ] ] ]
    for optimization, level, profile in configurations do
        for word, inputs, outputs, cases in entries do
            let body = engine.ExportVerifiedFlowEntryPoint(word, inputs, outputs)
            use program = OwningStackAot.compileWithRuntimeProfile toolchain level profile (Path.Combine(runRoot, "native", optimization, word.Replace("?", "-p"))) body
            for values in cases do
                let interpreted = interpret word inputs outputs values
                let nativeResult =
                    try Ok(program.Execute(values, 65536, 4096).Values)
                    with :? OwningStackExecutionException as failure -> Error failure.Diagnostic.Code
                check $"{word} {values}/{optimization}: native {nativeResult} = interpreter {interpreted}" (nativeResult = interpreted)

    // Missing libraries and symbols are runtime errors of the call, not of the
    // definition: each binding commits with a test that expects its error.
    let missing = Runtime.Engine(Path.Combine(runRoot, "missing"), Set.empty, fileSystemMode = FileSystemMode.Virtual)
    let source =
        String.concat "\n"
            [ "fn gone.lib(a: Int) -> Int = c \"int64_t f(int64_t)\" from \"no-such-library-307\""
              ""
              "test gone.lib/missing {"
              "    gone.lib(1)"
              "    => error RUNTIME_C_LIBRARY_MISSING"
              "}"
              ""
              "fn gone.symbol(a: Int) -> Int = c \"int64_t no_such_symbol(int64_t)\" from \"flowffi\""
              ""
              "test gone.symbol/missing {"
              "    gone.symbol(1)"
              "    => error RUNTIME_C_SYMBOL_MISSING"
              "}"
              ""
              "fn gone.range(a: Int) -> Int = c \"int8_t ffi_negate8(int8_t)\" from \"flowffi\""
              ""
              "test gone.range/too-big {"
              "    gone.range(300)"
              "    => error RUNTIME_C_ARGUMENT_RANGE"
              "}"
              "" ]
    let defined = missing.Dispatch("define", flowArgs source)
    check $"bindings to missing code define: {defined.ToJsonString()}" (defined["ok"].GetValue<bool>())
    for name in [ "gone.lib"; "gone.symbol"; "gone.range" ] do
        let response = missing.Dispatch("commit", wordArgs name)
        check $"{name} commits with its expected error: {response.ToJsonString()}" (response["ok"].GetValue<bool>())
    for optimization, level, profile in configurations do
        for word, code in [ "gone.lib", "RUNTIME_C_LIBRARY_MISSING"; "gone.symbol", "RUNTIME_C_SYMBOL_MISSING"; "gone.range", "RUNTIME_C_ARGUMENT_RANGE" ] do
            let body = missing.ExportVerifiedFlowEntryPoint(word, [ TInt ], [ TInt ])
            use program = OwningStackAot.compileWithRuntimeProfile toolchain level profile (Path.Combine(runRoot, "native-missing", optimization, word)) body
            let nativeResult =
                try Ok(program.Execute([ IntValue 300L ], 65536, 4096).Values)
                with :? OwningStackExecutionException as failure -> Error failure.Diagnostic.Code
            check $"{word}/{optimization} natively is {code}: {nativeResult}" (nativeResult = Error code)

    let receipt = JsonObject()
    receipt["status"] <- JsonValue.Create("pass")
    receipt["assertionCount"] <- JsonValue.Create(assertionCount.Value)
    File.WriteAllText(Path.Combine(runRoot, "receipt.json"), receipt.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + Environment.NewLine)
    printfn "foreign calls: %d assertions passed; artifacts %s" assertionCount.Value runRoot

[<EntryPoint>]
let main _ =
    try
        run ()
        0
    with error ->
        eprintfn "%s" (error.ToString())
        1
