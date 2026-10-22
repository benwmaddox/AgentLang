module AgentLang.Layouts.Tests

// Paired layouts (report 309): a template with one {...} slot is a Layout;
// rejections, formatting, the attached tests, interpreter = native at O0 and
// trusted O2, and a host-supplied Layout refused on both backends.

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open AgentLang
open AgentLang.Llvm

let private repo = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))
let private sourcePath = Path.Combine(__SOURCE_DIRECTORY__, "layouts.flow")
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

let private syntaxChecks (runRoot: string) =
    let fresh () = Runtime.Engine(Path.Combine(runRoot, "syntax", Guid.NewGuid().ToString("N")), Set.empty, fileSystemMode = FileSystemMode.Virtual)
    let rejects label (code: string) (source: string) =
        let response = (fresh ()).Dispatch("define", flowArgs source).ToJsonString()
        check $"{label} is {code}: {response}" (response.Contains(code, StringComparison.Ordinal))
    let wrap (body: string) = "fn bad.one(x: String) -> Layout {\n    " + body + "\n}\n"
    rejects "two slots" "FLOW_LAYOUT_SLOT" (wrap "html\"<p>{...}</p><p>{...}</p>\"")
    rejects "a slot in an attribute" "FLOW_TEMPLATE_MARKUP" (wrap "html\"<a title=\\\"{...}\\\">x</a>\"")
    rejects "a slot in a URL attribute" "FLOW_TEMPLATE_MARKUP" (wrap "html\"<a href=\\\"{...}\\\">x</a>\"")
    for tag in [ "title"; "textarea"; "script"; "style" ] do
        rejects $"a slot in <{tag}>" "FLOW_" (wrap $"html\"<{tag}>{{...}}</{tag}>\"")
    rejects "an unbalanced shell" "FLOW_TEMPLATE_MARKUP" (wrap "html\"<div>{...}\"")
    rejects "a declared Layout" "FLOW_LAYOUT_BUILT_IN" "record Layout {\n    head: String\n    tail: String\n}\n\nfn bad.two() -> Layout {\n    html\"<p>{...}</p>\"\n}\n"
    rejects "building a Layout by hand" "FLOW_" "fn bad.three() -> Layout {\n    html\"<p>{...}</p>\"\n}\n\nfn bad.four() -> Layout {\n    layout.new(head = \"<p>\", tail = \"\")\n}\n"
    rejects "wrapping a String" "FLOW_LAYOUT_WRAP" "fn bad.five() -> Html {\n    layout.wrap(html\"<p>{...}</p>\", \"text\")\n}\n"
    let engine = fresh ()
    let format (source: string) =
        let response = engine.Dispatch("format", flowArgs source)
        check $"format succeeds: {response.ToJsonString()}" (response["ok"].GetValue<bool>())
        (response["data"]["source"]).GetValue<string>()
    let formatted = format (File.ReadAllText sourcePath)
    check $"formatter keeps the slot: {formatted.Substring(0, 400)}" (formatted.Contains("{...}", StringComparison.Ordinal))
    check "formatted layouts format to themselves" (format formatted = formatted)

let private run () =
    let runRoot = Path.Combine(repo, ".agentlang", "layouts-309", "runs", DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(runRoot) |> ignore
    syntaxChecks runRoot
    let engine = Runtime.Engine(Path.Combine(runRoot, "flow-engine"), Set.empty, fileSystemMode = FileSystemMode.Virtual)
    let defined = engine.Dispatch("define", flowArgs (File.ReadAllText sourcePath + "\nfn page.head-of(page: Layout) -> String {\n    page.head\n}\n\ntest page.head-of/head {\n    expect page.head-of(edge.end(\"a\")) == \"<p>a</p>\"\n}\n"))
    if not (defined["ok"].GetValue<bool>()) then failwith (defined.ToJsonString())
    for name in [ "page.layout"; "page.wrapped"; "page.wrapped-list"; "edge.start"; "edge.end"; "edge.pair"; "edge.split"; "page.head-of"; "escape.text" ] do
        let response = engine.Dispatch("commit", wordArgs name)
        let text = response.ToJsonString()
        check $"{name} commits (its attached tests pass): {text}" (response["ok"].GetValue<bool>() || text.Contains("COMMIT_NOT_CANDIDATE", StringComparison.Ordinal))
    let interpret word inputs outputs values =
        match engine.BindFlowEntryPoint(word, inputs, outputs).Invoke(values) with
        | Ok value -> Ok value.Values
        | Error (diagnostic: Diagnostic) -> Error diagnostic.Code
    let html text = NamedValue("Html", StringValue text)
    let layoutType = TNamed "Layout"
    let htmlType = TNamed "Html"
    let texts = [ ""; "a"; "a<b>&\"'"; "é🚀"; String('x', 300) ]
    // Escaping scans 8 bytes at a time (report 310): each special at the start,
    // middle and end of texts around every 8-byte boundary, runs of specials, and
    // multi-byte text beside them.
    let escapeTexts =
        [ for length in [ 1; 7; 8; 9; 15; 16; 17; 24; 25 ] do
            for position in List.distinct [ 0; length / 2; length - 1 ] do
                for special in [ '&'; '\''; '<'; '>'; '"' ] -> String('a', position) + string special + String('b', length - position - 1)
          yield "&'<>\"&'<>\"&'<>\"&'<>"
          yield "é🚀&é🚀<é🚀>é🚀\"é🚀'"
          yield String('x', 63) + "<" + String('y', 64) ]
    let entries =
        [ "page.layout", [ TString; TInt ], [ layoutType ], [ for text in texts do for count in [ 0L; -7L; Int64.MaxValue ] -> [ StringValue text; IntValue count ] ]
          "page.wrapped", [ TString; TInt; htmlType ], [ htmlType ], [ for text in texts -> [ StringValue text; IntValue 2L; html "<p>body</p>" ] ]
          "page.wrapped-list", [ TString; TList htmlType ], [ htmlType ], [ for count in [ 0; 1; 40 ] -> [ StringValue "t"; ListValue(htmlType, List.replicate count (html "<p>m</p>")) ] ]
          "edge.start", [], [ layoutType ], [ [] ]
          "edge.end", [ TString ], [ layoutType ], [ for text in texts -> [ StringValue text ] ]
          "edge.pair", [ TInt; TString ], [ layoutType ], [ for text in texts -> [ IntValue 9L; StringValue text ] ]
          "edge.split", [ TInt; TString ], [ htmlType ], [ for text in texts @ escapeTexts -> [ IntValue -1L; StringValue text ] ]
          "escape.text", [ TString ], [ TString ], [ for text in texts @ escapeTexts -> [ StringValue text ] ] ]
    // The interpreter's head and tail match the template, joined around the content.
    match interpret "edge.split" [ TInt; TString ] [ htmlType ] [ IntValue 7L; StringValue "&" ] with
    | Ok [ NamedValue(_, StringValue text) ] -> check $"edge.split joins head, content and tail ({text})" (text = "<div data-n=\"7\"><b>mid</b><i>&amp;</i></div>")
    | other -> check $"edge.split returns Html ({other})" false
    let forged = RecordValue("Layout", Map.ofList [ "head", StringValue "<script>"; "tail", StringValue "" ])
    check "the interpreter refuses a Layout from outside" (interpret "page.head-of" [ layoutType ] [ TString ] [ forged ] |> Result.isError)
    let toolchain = LlvmToolchain.discover ()
    let configurations =
        [ "O0", LlvmOptimization.O0, OwningRuntimeProfile.Diagnostic
          "trusted-O2", LlvmOptimization.O2, OwningRuntimeProfile.TrustedGenerated ]
    for optimization, level, profile in configurations do
        for word, inputs, outputs, cases in entries do
            let body = engine.ExportVerifiedFlowEntryPoint(word, inputs, outputs)
            use program = OwningStackAot.compileWithRuntimeProfile toolchain level profile (Path.Combine(runRoot, "native", optimization, word)) body
            for values in cases do
                let interpreted = interpret word inputs outputs values
                let nativeResult =
                    try Ok(program.Execute(values, 1048576, 4096).Values)
                    with :? OwningStackExecutionException as failure -> Error failure.Diagnostic.Code
                check $"{word} {values}/{optimization}: native = interpreter" (nativeResult = interpreted)
        let body = engine.ExportVerifiedFlowEntryPoint("page.head-of", [ layoutType ], [ TString ])
        use program = OwningStackAot.compileWithRuntimeProfile toolchain level profile (Path.Combine(runRoot, "native", optimization, "head-of")) body
        let refused =
            try
                program.Execute([ forged ], 65536, 4096) |> ignore
                false
            with
            | :? ArgumentException -> true
            | :? OwningStackExecutionException -> true
        check $"native code refuses a Layout from outside ({optimization})" refused

    let receipt = JsonObject()
    receipt["status"] <- JsonValue.Create("pass")
    receipt["assertionCount"] <- JsonValue.Create(assertionCount.Value)
    File.WriteAllText(Path.Combine(runRoot, "receipt.json"), receipt.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + Environment.NewLine)
    printfn "layouts: %d assertions passed; artifacts %s" assertionCount.Value runRoot

[<EntryPoint>]
let main _ =
    try
        run ()
        0
    with error ->
        eprintfn "%s" (error.ToString())
        1
