module AgentLang.FlowHttp.Program

open System
open System.IO
open System.Text
open AgentLang.Llvm

let private usage =
    "usage: AgentLang.FlowHttp emit --app <name> --source <file.flow> [--source ...] --target windows-x64|linux-x64 [--profile trusted|diagnostic] --out <dir>"

/// emit: compiles the stdlib and app sources and writes <out>/module.ll (the
/// two-entry owning module for the target), <out>/diagnostics.tsv and <out>/symbols.tsv
/// (agentlang_word_NNNN -> Flow word, for profilers).
let private emit (args: string list) =
    let mutable app = ""
    let mutable target = OwningTarget.WindowsX64
    let mutable profile = OwningRuntimeProfile.TrustedGenerated
    let mutable out = ""
    let sources = ResizeArray<string>()
    let rec parse = function
        | "--app" :: value :: rest -> app <- value; parse rest
        | "--source" :: value :: rest -> sources.Add(Path.GetFullPath value); parse rest
        | "--target" :: value :: rest -> target <- OwningTarget.Parse value; parse rest
        | "--profile" :: "trusted" :: rest -> profile <- OwningRuntimeProfile.TrustedGenerated; parse rest
        | "--profile" :: "diagnostic" :: rest -> profile <- OwningRuntimeProfile.Diagnostic; parse rest
        | "--out" :: value :: rest -> out <- Path.GetFullPath value; parse rest
        | [] -> ()
        | other :: _ -> failwith $"Unknown argument {other}. {usage}"
    parse args
    if app = "" || sources.Count = 0 || out = "" then failwith usage
    Directory.CreateDirectory out |> ignore
    let project = Path.Combine(out, "engine-" + Guid.NewGuid().ToString("N").Substring(0, 8))
    // The build never runs requests; its production provider is a scratch database.
    use flowApp = FlowApp.load project app (List.ofSeq sources) ":memory:"
    if not (isNull (Environment.GetEnvironmentVariable "AGENTLANG_BUILD_ADD_PROBE")) then
        // How long adding one function takes once the project is loaded: define a word
        // with its attached test, then commit that word alone.
        let request (operation: string) (fields: (string * System.Text.Json.Nodes.JsonNode) list) =
            let args = System.Text.Json.Nodes.JsonObject()
            for key, value in fields do args[key] <- value
            let response = flowApp.Engine.Dispatch(operation, args)
            if not (response["ok"].GetValue<bool>()) then failwith $"probe {operation}: {response.ToJsonString()}"
        let source = "fn probe.add-one(n: Int) -> Int {\n    n + 1\n}\n\ntest probe.add-one/two {\n    expect probe.add-one(1) == 2\n}\n"
        FlowApp.timed "probe-define" (fun () ->
            request "define" [ "frontend", System.Text.Json.Nodes.JsonValue.Create("flow"); "syntaxVersion", System.Text.Json.Nodes.JsonValue.Create(2); "source", System.Text.Json.Nodes.JsonValue.Create(source) ])
        FlowApp.timed "probe-commit" (fun () -> request "commit" [ "word", System.Text.Json.Nodes.JsonValue.Create("probe.add-one") ])
    let ir, diagnostics = FlowApp.timed "emit-native" (fun () -> FlowApp.emitNative flowApp profile target)
    File.WriteAllText(Path.Combine(out, "module.ll"), ir, UTF8Encoding(false))
    File.WriteAllText(Path.Combine(out, "diagnostics.tsv"), FlowApp.diagnosticsText diagnostics, UTF8Encoding(false))
    File.WriteAllText(Path.Combine(out, "symbols.tsv"), FlowApp.nativeSymbolsText flowApp, UTF8Encoding(false))
    try Directory.Delete(project, true) with _ -> ()
    printfn "emitted %s (%s, %s, context %s)" (Path.Combine(out, "module.ll")) target.Triple (if profile = OwningRuntimeProfile.TrustedGenerated then "trusted" else "diagnostic") flowApp.ContextType

[<EntryPoint>]
let main argv =
    try
        match List.ofArray argv with
        | "emit" :: rest -> emit rest; 0
        | _ -> eprintfn "%s" usage; 2
    with error ->
        eprintfn "%s" error.Message
        1
