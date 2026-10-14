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
    | "List<String>" -> TList TString
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
        else
            let digits = Regex.Match(text.Substring index, @"^-?\d+")
            if digits.Success then
                values.Add(IntValue(Int64.Parse digits.Value))
                index <- index + digits.Length
            else ok <- false
        skipSpace ()
    if ok then Some(List.ofSeq values) else None

let private run () =
    let source = File.ReadAllText sourcePath
    let runBase =
        match Environment.GetEnvironmentVariable "AGENTLANG_STDLIB_RUN_ROOT" with
        | null | "" -> Path.Combine(repo, ".agentlang", "stdlib-272", "runs")
        | configured -> configured
    let runRoot = Path.Combine(runBase, DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8))
    Directory.CreateDirectory(runRoot) |> ignore
    let engine = Runtime.Engine(Path.Combine(runRoot, "engine"), Set.empty, fileSystemMode = FileSystemMode.Virtual)
    let args = JsonObject()
    args["frontend"] <- JsonValue.Create("flow")
    args["syntaxVersion"] <- JsonValue.Create(2)
    args["source"] <- JsonValue.Create(source)
    let defined = engine.Dispatch("define", args)
    if not (defined["ok"].GetValue<bool>()) then failwith (defined.ToJsonString())
    let signatures =
        Regex.Matches(source, @"(?m)^fn ([^\s(]+)\(([^)]*)\) -> ([^{]+?) \{")
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
        check $"{name} commits with its tests: {response.ToJsonString()}" (response["ok"].GetValue<bool>())

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
    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        let natives = Collections.Generic.Dictionary<string, OwningStackCompiledProgram>()
        try
            for word, (inputs, outputs), values in cases do
                if not (natives.ContainsKey word) then
                    let body = engine.ExportVerifiedFlowEntryPoint(word, inputs, outputs)
                    natives[word] <- OwningStackAot.compile toolchain optimization (Path.Combine(runRoot, string optimization, string natives.Count)) body
                let interpreted =
                    match engine.BindFlowEntryPoint(word, inputs, outputs).Invoke(values) with
                    | Ok value -> value.Values
                    | Error diagnostic -> failwith $"Interpreter rejected {word}: {diagnostic.Code} {diagnostic.Message}"
                let native = natives[word].Execute(values, 1 <<< 20, 1 <<< 20).Values
                let label = JsonSerializer.Serialize(values |> List.map Types.formatValue)
                check $"{word} {label}/{optimization}: native {native |> List.map Types.formatValue} = interpreter {interpreted |> List.map Types.formatValue}" (native = interpreted)
        finally
            for native in natives.Values do (native :> IDisposable).Dispose()

    let receipt = JsonObject()
    receipt["status"] <- JsonValue.Create("pass")
    receipt["assertionCount"] <- JsonValue.Create(assertionCount.Value)
    receipt["nativeCases"] <- JsonValue.Create(cases.Length)
    let pins = JsonArray()
    for path in [ sourcePath; Path.Combine(__SOURCE_DIRECTORY__, "Program.fs"); Path.Combine(repo, "src/AgentLang.Llvm/native/owning_stack_runtime.c"); Path.Combine(repo, "src/AgentLang.Llvm/OwningStackAot.fs"); Path.Combine(repo, "src/AgentLang.Core/IrInterpreter.fs") ] do
        let pin = JsonObject()
        pin["path"] <- JsonValue.Create(Path.GetRelativePath(repo, path).Replace('\\', '/'))
        pin["sha256"] <- JsonValue.Create(hash path)
        pins.Add(pin)
    receipt["sourcePins"] <- pins
    File.WriteAllText(Path.Combine(runRoot, "receipt.json"), receipt.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + Environment.NewLine)
    printfn "stdlib: %d assertions passed (%d words, %d native cases x2); artifacts %s" assertionCount.Value signatures.Count cases.Length runRoot

[<EntryPoint>]
let main _ =
    try
        run ()
        0
    with error ->
        eprintfn "%s" (error.ToString())
        1
