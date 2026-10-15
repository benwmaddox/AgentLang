module AgentLang.Stdlib.Tests

// Standard library slice 1: every word in stdlib.flow commits, which runs its
// attached known-answer tests in the interpreter. Each test whose call has
// only literal arguments is then replayed through the owning-stack native
// build at O0 and O2, and native must equal the interpreter.

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open AgentLang
open AgentLang.Llvm

let private repo = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))
let private sourcePath = Path.Combine(__SOURCE_DIRECTORY__, "stdlib.flow")
let private slice2Path = Path.Combine(__SOURCE_DIRECTORY__, "stdlib2.flow")
// Word calls (report 280): register calling, Option registers, inline fn.
let private callsPath = Path.Combine(__SOURCE_DIRECTORY__, "calls.flow")
let private assertionCount = ref 0

let private check label condition =
    assertionCount.Value <- assertionCount.Value + 1
    if not condition then failwith $"Assertion failed: {label}"

let private hash (path: string) = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()

let private typeOf (text: string) =
    match text.Trim() with
    | "String" -> TString
    | "Int" -> TInt
    | "Bool" -> TBool
    | "Bytes" -> TBytes
    | "Json" -> TJson
    | "List<String>" -> TList TString
    | "Float" -> TFloat
    | "Unit" -> TUnit
    | "Option<Int>" -> TOption TInt
    | "Option<Bool>" -> TOption TBool
    | "Option<Float>" -> TOption TFloat
    | other -> failwith $"stdlib.flow boundary type {other} is not covered by the parity harness."

/// Literal call arguments: "quoted strings" with \n \" \\ escapes, and Ints.
let private parseArguments (text: string) : Value list option =
    let values = Collections.Generic.List<Value>()
    let mutable index = 0
    let mutable ok = true
    let skipSpace () =
        while index < text.Length && (text[index] = ' ' || text[index] = ',') do
            index <- index + 1
    skipSpace ()
    while ok && index < text.Length do
        if text[index] = '"' then
            let builder = StringBuilder()
            index <- index + 1
            while index < text.Length && text[index] <> '"' do
                if text[index] = '\\' && index + 1 < text.Length then
                    match text[index + 1] with
                    | 'n' -> builder.Append('\n') |> ignore
                    | other -> builder.Append(other) |> ignore
                    index <- index + 2
                else
                    builder.Append(text[index]) |> ignore
                    index <- index + 1
            index <- index + 1
            values.Add(StringValue(builder.ToString()))
        elif text.Substring(index).StartsWith "true" then
            values.Add(BoolValue true)
            index <- index + 4
        elif text.Substring(index).StartsWith "false" then
            values.Add(BoolValue false)
            index <- index + 5
        else
            let digits = Regex.Match(text.Substring index, @"^-?\d+")
            if digits.Success then
                values.Add(IntValue(Int64.Parse digits.Value))
                index <- index + digits.Length
            else ok <- false
        skipSpace ()
    if ok then Some(List.ofSeq values) else None

let private run () =
    let source = File.ReadAllText sourcePath + Environment.NewLine + File.ReadAllText slice2Path + Environment.NewLine + File.ReadAllText callsPath
    let runBase =
        match Environment.GetEnvironmentVariable "AGENTLANG_STDLIB_RUN_ROOT" with
        | null | "" -> Path.Combine(repo, ".agentlang", "stdlib-272", "runs")
        | configured -> configured
    let runRoot = Path.Combine(runBase, DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8))
    Directory.CreateDirectory(runRoot) |> ignore
    let randomRead = Set.singleton "random.read"
    let engine = Runtime.Engine(Path.Combine(runRoot, "engine"), randomRead, fileSystemMode = FileSystemMode.Virtual, testCapabilities = randomRead)
    let args = JsonObject()
    args["frontend"] <- JsonValue.Create("flow")
    args["syntaxVersion"] <- JsonValue.Create(2)
    args["source"] <- JsonValue.Create(source)
    let defined = engine.Dispatch("define", args)
    if not (defined["ok"].GetValue<bool>()) then failwith (defined.ToJsonString())
    let signatures =
        Regex.Matches(source, @"(?m)^(?:inline )?fn ([^\s(]+)\(([^)]*)\) -> ([^{(]+?) \{")
        |> Seq.map (fun m ->
            let inputs =
                if String.IsNullOrWhiteSpace m.Groups[2].Value then []
                else m.Groups[2].Value.Split(',') |> Array.map (fun part -> typeOf (part.Split(':')[1])) |> List.ofArray
            m.Groups[1].Value, (inputs, [ typeOf m.Groups[3].Value ]))
        |> Map.ofSeq
    check "stdlib.flow defines words" (signatures.Count > 10)
    for KeyValue(name, _) in signatures do
        let wordArgs = JsonObject()
        wordArgs["word"] <- JsonValue.Create(name)
        let response = engine.Dispatch("commit", wordArgs)
        // A word committed earlier as a dependency of another word is no longer a candidate.
        let committedAsDependency = not (response["ok"].GetValue<bool>()) && response.ToJsonString().Contains "COMMIT_NOT_CANDIDATE"
        check $"{name} commits with its tests: {response.ToJsonString()}" (response["ok"].GetValue<bool>() || committedAsDependency)

    // Replay literal test calls natively.
    let cases =
        Regex.Matches(source, @"(?m)^test [^\s{]+ \{\r?\n\s+([^\s(]+)\((.*)\)\r?\n")
        |> Seq.choose (fun m ->
            let word = m.Groups[1].Value
            match signatures.TryFind word, parseArguments m.Groups[2].Value with
            | Some signature, Some values when values.Length = (fst signature).Length -> Some(word, signature, values)
            | _ -> None)
        |> List.ofSeq
    check $"parity harness found literal cases ({cases.Length})" (cases.Length >= 50)
    let toolchain = LlvmToolchain.discover ()
    // Diagnostic O0 and O2, plus the trusted O2 build that benchmarks run, so
    // trusted-only fast paths are tested and not only timed.
    let builds =
        [ "O0", LlvmOptimization.O0, OwningRuntimeProfile.Diagnostic
          "O2", LlvmOptimization.O2, OwningRuntimeProfile.Diagnostic
          "trusted-O2", LlvmOptimization.O2, OwningRuntimeProfile.TrustedGenerated ]
    for optimization, level, profile in builds do
        let natives = Collections.Generic.Dictionary<string, OwningStackCompiledProgram>()
        try
            for word, (inputs, outputs), values in cases do
                if not (natives.ContainsKey word) then
                    let body = engine.ExportVerifiedFlowEntryPoint(word, inputs, outputs)
                    let directory = Path.Combine(runRoot, optimization, string natives.Count)
                    natives[word] <- OwningStackAot.compileWithRuntimeProfile toolchain level profile directory body
                    // Names the build directory, so a word can be rebuilt elsewhere (the Linux probe).
                    File.WriteAllText(Path.Combine(directory, "word.txt"), word)
                let label = JsonSerializer.Serialize(values |> List.map Types.formatValue)
                match engine.BindFlowEntryPoint(word, inputs, outputs).Invoke(values) with
                | Ok value ->
                    let interpreted = value.Values
                    let native = natives[word].Execute(values, 1 <<< 20, 1 <<< 20).Values
                    check $"{word} {label}/{optimization}: native {native |> List.map Types.formatValue} = interpreter {interpreted |> List.map Types.formatValue}" (native = interpreted)
                | Error diagnostic when word.StartsWith("calls.", StringComparison.Ordinal) ->
                    // calls.flow error tests: native fails with the interpreter's code.
                    let nativeCode =
                        try
                            natives[word].Execute(values, 1 <<< 20, 1 <<< 20) |> ignore
                            "<no error>"
                        with :? OwningStackExecutionException as failure -> failure.Diagnostic.Code
                    check $"{word} {label}/{optimization}: native error {nativeCode} = interpreter {diagnostic.Code}" (nativeCode = diagnostic.Code)
                | Error diagnostic -> failwith $"Interpreter rejected {word}: {diagnostic.Code} {diagnostic.Message}"
        finally
            for native in natives.Values do (native :> IDisposable).Dispose()

    let receipt = JsonObject()
    receipt["status"] <- JsonValue.Create("pass")
    receipt["assertionCount"] <- JsonValue.Create(assertionCount.Value)
    receipt["nativeCases"] <- JsonValue.Create(cases.Length)
    let pins = JsonArray()
    for path in [ sourcePath; slice2Path; callsPath; Path.Combine(repo, "src/AgentLang.Llvm/native/owning_stdlib2.h"); Path.Combine(__SOURCE_DIRECTORY__, "Program.fs"); Path.Combine(repo, "src/AgentLang.Llvm/native/owning_stack_runtime.c"); Path.Combine(repo, "src/AgentLang.Llvm/OwningStackAot.fs"); Path.Combine(repo, "src/AgentLang.Core/IrInterpreter.fs") ] do
        let pin = JsonObject()
        pin["path"] <- JsonValue.Create(Path.GetRelativePath(repo, path).Replace('\\', '/'))
        pin["sha256"] <- JsonValue.Create(hash path)
        pins.Add(pin)
    receipt["sourcePins"] <- pins
    File.WriteAllText(Path.Combine(runRoot, "receipt.json"), receipt.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + Environment.NewLine)
    printfn "stdlib: %d assertions passed (%d words, %d native cases x3: O0, O2, trusted O2); artifacts %s" assertionCount.Value signatures.Count cases.Length runRoot

[<EntryPoint>]
let main _ =
    try
        run ()
        0
    with error ->
        eprintfn "%s" (error.ToString())
        1
