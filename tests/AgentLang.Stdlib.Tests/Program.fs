module AgentLang.Stdlib.Tests

// Standard library slice 1: every word in stdlib.flow commits, which runs its
// attached known-answer tests in the interpreter.

open System
open System.IO
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open AgentLang

let private repo = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))
let private sourcePath = Path.Combine(__SOURCE_DIRECTORY__, "stdlib.flow")
let private assertionCount = ref 0

let private check label condition =
    assertionCount.Value <- assertionCount.Value + 1
    if not condition then failwith $"Assertion failed: {label}"

let private run () =
    let source = File.ReadAllText sourcePath
    let runRoot = Path.Combine(repo, ".agentlang", "stdlib-272", "runs", DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(runRoot) |> ignore
    let engine = Runtime.Engine(Path.Combine(runRoot, "flow-engine"), Set.empty, fileSystemMode = FileSystemMode.Virtual)
    let args = JsonObject()
    args["frontend"] <- JsonValue.Create("flow")
    args["syntaxVersion"] <- JsonValue.Create(2)
    args["source"] <- JsonValue.Create(source)
    let defined = engine.Dispatch("define", args)
    if not (defined["ok"].GetValue<bool>()) then failwith (defined.ToJsonString())
    let words = Regex.Matches(source, @"(?m)^fn ([^\s(]+)\(") |> Seq.map (fun m -> m.Groups[1].Value) |> List.ofSeq
    check "stdlib.flow defines words" (words.Length > 10)
    for name in words do
        let wordArgs = JsonObject()
        wordArgs["word"] <- JsonValue.Create(name)
        let response = engine.Dispatch("commit", wordArgs)
        check $"{name} commits with its tests: {response.ToJsonString()}" (response["ok"].GetValue<bool>())
    printfn "stdlib: %d assertions passed (%d words)" assertionCount.Value words.Length

[<EntryPoint>]
let main _ =
    try
        run ()
        0
    with error ->
        eprintfn "%s" (error.ToString())
        1
