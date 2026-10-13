module AgentLang.OwningUtf8.Tests

// UTF-8 Strings: byte and character length, interpreter/native parity on
// multi-byte text, exact UTF-8 retained bytes, and rejection of unpaired
// surrogates at every boundary (literals, interpreter inputs, native inputs).

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open AgentLang
open AgentLang.Llvm

let private repo = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))
let private sourcePath = Path.Combine(__SOURCE_DIRECTORY__, "owning-utf8.flow")
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
    [ "native.utf8.byte-length"; "native.utf8.char-length"; "native.utf8.legacy-length"; "native.utf8.decorate"; "native.utf8.has-rocket" ]

let private hash (path: string) = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()

let private run () =
    let runRoot = Path.Combine(repo, ".agentlang", "utf8-265", "runs", DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(runRoot) |> ignore
    let engine = Runtime.Engine(Path.Combine(runRoot, "flow-engine"), Set.empty, fileSystemMode = FileSystemMode.Virtual)
    let defined = engine.Dispatch("define", flowArgs (File.ReadAllText sourcePath))
    if not (defined["ok"].GetValue<bool>()) then failwith (defined.ToJsonString())
    for name in words do
        let response = engine.Dispatch("commit", wordArgs name)
        check $"{name} commits" (response["ok"].GetValue<bool>() || response.ToJsonString().Contains("COMMIT_NOT_CANDIDATE", StringComparison.Ordinal))

    // An unpaired surrogate in a literal is a compile-time error.
    let literalEngine = Runtime.Engine(Path.Combine(runRoot, "literal"), Set.empty, fileSystemMode = FileSystemMode.Virtual)
    // Raw Flow source text: the literal is the JSON escape for a lone high surrogate.
    let escapedSource = """fn bad.literal() -> String {
    "\ud800"
}
"""
    let badEscape = literalEngine.Dispatch("define", flowArgs escapedSource).ToJsonString()
    check "a literal escaping an unpaired surrogate is rejected (FLOW_INVALID_STRING)" (badEscape.Contains("FLOW_INVALID_STRING", StringComparison.Ordinal))
    let rawSource = "fn bad.raw() -> String {\n    \"" + string (char 0xD800) + "\"\n}\n"
    let badRaw = literalEngine.Dispatch("define", flowArgs rawSource).ToJsonString()
    check "a literal containing a raw unpaired surrogate is rejected (FLOW_INVALID_STRING)" (badRaw.Contains("FLOW_INVALID_STRING", StringComparison.Ordinal))

    let toolchain = LlvmToolchain.discover ()
    let samples =
        [ ""; "plain"; "café"; "λx"; "🚀"; "a🚀b𐐷c"; "日本語のテキスト"; "<é & 🚀>"; String('é', 500) ]
    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        let native word inputs outputs =
            let body = engine.ExportVerifiedFlowEntryPoint(word, inputs, outputs)
            OwningStackAot.compile toolchain optimization (Path.Combine(runRoot, "native", string optimization, word)) body
        use byteLength = native "native.utf8.byte-length" [ TString ] [ TInt ]
        use charLength = native "native.utf8.char-length" [ TString ] [ TInt ]
        use legacyLength = native "native.utf8.legacy-length" [ TString ] [ TInt ]
        use decorate = native "native.utf8.decorate" [ TString ] [ TString ]
        use hasRocket = native "native.utf8.has-rocket" [ TString ] [ TBool ]
        let interpret word inputs outputs values =
            match engine.BindFlowEntryPoint(word, inputs, outputs).Invoke(values) with
            | Ok value -> value.Values
            | Error diagnostic -> failwith $"Interpreter rejected {word}: {diagnostic.Code} {diagnostic.Message}"
        for text in samples do
            let input = [ StringValue text ]
            let bytes = int64 (Encoding.UTF8.GetByteCount text)
            let characters = int64 (text.EnumerateRunes() |> Seq.length)
            let label = JsonSerializer.Serialize text
            check $"byte-length {label}/{optimization}: interpreter = UTF-8 bytes" (interpret "native.utf8.byte-length" [ TString ] [ TInt ] input = [ IntValue bytes ])
            check $"byte-length {label}/{optimization}: native = interpreter" (byteLength.Execute(input, 65536, 4096).Values = [ IntValue bytes ])
            check $"char-length {label}/{optimization}: interpreter = scalar values" (interpret "native.utf8.char-length" [ TString ] [ TInt ] input = [ IntValue characters ])
            check $"char-length {label}/{optimization}: native = interpreter" (charLength.Execute(input, 65536, 4096).Values = [ IntValue characters ])
            check $"legacy string.length {label}/{optimization}: is byte length" (legacyLength.Execute(input, 65536, 4096).Values = [ IntValue bytes ])
            let decorated = interpret "native.utf8.decorate" [ TString ] [ TString ] input
            let nativeDecorated = decorate.Execute(input, 65536, 65536)
            check $"decorate {label}/{optimization}: native = interpreter" (nativeDecorated.Values = decorated)
            let expectedText = "«" + TrustedValues.htmlEscape text + "» " + string characters
            check $"decorate {label}/{optimization}: expected text" (decorated = [ StringValue expectedText ])
            // The retained result is exactly: u32 UTF-8 byte count, 4 zero bytes, UTF-8, zero padding to 8.
            let utf8 = Encoding.UTF8.GetBytes expectedText
            let expectedWire =
                Array.concat [ BitConverter.GetBytes(uint32 utf8.Length); Array.zeroCreate<byte> 4; utf8; Array.zeroCreate<byte> ((8 - (utf8.Length % 8)) % 8) ]
            check $"decorate {label}/{optimization}: retained bytes are UTF-8" (nativeDecorated.RetainedOutputBytes = expectedWire)
            let rocket = text.Contains("🚀", StringComparison.Ordinal)
            check $"contains-rocket {label}/{optimization}" (hasRocket.Execute(input, 65536, 4096).Values = [ BoolValue rocket ])
        // Unpaired surrogates have no UTF-8 form: both backends reject them.
        // Built from char values: F# compiles a lone surrogate escape in a literal to U+FFFD.
        for bad in [ string (char 0xD800); "x" + string (char 0xDC00) + "y"; "🚀"[0..0] ] do
            let label = Convert.ToHexString(Encoding.Unicode.GetBytes bad)
            let interpreterCode =
                match engine.BindFlowEntryPoint("native.utf8.byte-length", [ TString ], [ TInt ]).Invoke([ StringValue bad ]) with
                | Ok _ -> "accepted"
                | Error diagnostic -> diagnostic.Code
            check $"interpreter rejects unpaired surrogate {label}/{optimization} ({interpreterCode})" (interpreterCode = "STRING_INVALID_UTF16")
            let nativeOutcome =
                try
                    byteLength.Execute([ StringValue bad ], 65536, 4096) |> ignore
                    "accepted"
                with :? ArgumentException as error -> if error.Message.Contains("STRING_INVALID_UTF16", StringComparison.Ordinal) then "STRING_INVALID_UTF16" else error.Message
            check $"native boundary rejects unpaired surrogate {label}/{optimization} ({nativeOutcome})" (nativeOutcome = "STRING_INVALID_UTF16")

    let receipt = JsonObject()
    receipt["status"] <- JsonValue.Create("pass")
    receipt["assertionCount"] <- JsonValue.Create(assertionCount.Value)
    let pins = JsonArray()
    for path in [ sourcePath; Path.Combine(__SOURCE_DIRECTORY__, "Program.fs"); Path.Combine(repo, "src/AgentLang.Llvm/native/owning_stack_runtime.c"); Path.Combine(repo, "src/AgentLang.Llvm/OwningStackAot.fs") ] do
        let pin = JsonObject()
        pin["path"] <- JsonValue.Create(Path.GetRelativePath(repo, path).Replace('\\', '/'))
        pin["sha256"] <- JsonValue.Create(hash path)
        pins.Add(pin)
    receipt["sourcePins"] <- pins
    File.WriteAllText(Path.Combine(runRoot, "receipt.json"), receipt.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + Environment.NewLine)
    printfn "owning utf8: %d assertions passed; artifacts %s" assertionCount.Value runRoot

[<EntryPoint>]
let main _ =
    try
        run ()
        0
    with error ->
        eprintfn "%s" (error.ToString())
        1
