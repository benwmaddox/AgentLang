module AgentLang.TemplateContext.Tests

// Template holes by context (report 302): the compile-time rules for URL
// attributes, handlers, style and script; the built-in Url type and its
// string.url-safe? validator; Int holes written as digits; refined String holes
// escaped. Interpreter and native code must agree at O0, O2 and trusted O2.

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open AgentLang
open AgentLang.Llvm

let private repo = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))
let private sourcePath = Path.Combine(__SOURCE_DIRECTORY__, "template-context.flow")
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

let private words = [ "native.template.link"; "native.template.safe?"; "native.template.count"; "native.template.name" ]

/// The reference for string.url-safe?, written independently of TrustedValues.
let private urlSafe (text: string) =
    let control = text |> Seq.exists (fun ch -> ch < ' ' || ch = '\u007f')
    let spaced = text.Length > 0 && (text.StartsWith " " || text.EndsWith " ")
    if control || spaced then false
    else
        match text |> Seq.tryFindIndex (fun ch -> ch = ':' || ch = '/' || ch = '?' || ch = '#') with
        | Some stop when text[stop] = ':' -> List.contains (text.Substring(0, stop).ToLowerInvariant()) [ "http"; "https"; "mailto"; "tel" ]
        | _ -> true

let private ruleChecks (runRoot: string) =
    let engine = Runtime.Engine(Path.Combine(runRoot, "rules"), Set.empty, fileSystemMode = FileSystemMode.Virtual)
    let mutable counter = 0
    let define (body: string) =
        counter <- counter + 1
        let source = $"fn rule.case{counter}(s: String, i: Int, u: Url, h: Html) -> Html {{\n    html\"\"\"\n{body}\n\"\"\"\n}}\n"
        engine.Dispatch("define", flowArgs source).ToJsonString()
    let rejects label (body: string) =
        let response = define body
        check $"{label} is rejected (FLOW_TEMPLATE_HOLE_CONTEXT): {response}" (response.Contains("FLOW_TEMPLATE_HOLE_CONTEXT", StringComparison.Ordinal))
    let accepts label (body: string) =
        let response = define body
        check $"{label} is accepted: {response}" (response.Contains("\"ok\":true", StringComparison.Ordinal))
    // URL attributes: a hole that could start the scheme must be a Url (or an Int).
    rejects "href starting with a String" "<a href=\"{s}\">x</a>"
    rejects "src starting with a String" "<img src=\"{s}\" />"
    rejects "action starting with a String" "<form action=\"{s}\"></form>"
    rejects "formaction starting with a String" "<button formaction=\"{s}\">x</button>"
    rejects "a single-quoted href" "<a href='{s}'>x</a>"
    rejects "an upper-case HREF" "<a HREF=\"{s}\">x</a>"
    rejects "a String after a letter (j + avascript:)" "<a href=\"j{s}\">x</a>"
    rejects "a String after a space" "<a href=\" {s}\">x</a>"
    accepts "href starting with a Url" "<a href=\"{u}\">x</a>"
    accepts "a String after a Url" "<a href=\"{u}/{s}\">x</a>"
    accepts "a String after a path" "<a href=\"/rooms/{s}\">x</a>"
    accepts "a String after ?" "<a href=\"?q={s}\">x</a>"
    accepts "a String after #" "<a href=\"#{s}\">x</a>"
    accepts "a String after a scheme" "<a href=\"mailto:{s}\">x</a>"
    accepts "an Int at the start" "<a href=\"{i}\">x</a>"
    accepts "a data- attribute is not a URL" "<a href=\"/\" data-url=\"{s}\">x</a>"
    // Script, CSS and documents in attributes: only Int holes.
    rejects "onclick with a String" "<a href=\"/\" onclick=\"go('{s}')\">x</a>"
    rejects "onclick with a Url" "<a href=\"/\" onclick=\"{u}\">x</a>"
    rejects "style with a String" "<p style=\"color: {s}\">x</p>"
    rejects "srcdoc with a String" "<iframe srcdoc=\"{s}\"></iframe>"
    accepts "onclick with an Int" "<a href=\"/\" onclick=\"go({i})\">x</a>"
    accepts "style with an Int" "<p style=\"order: {i}\">x</p>"
    // Script and style text.
    rejects "a String in a script" "<script>var x = {s};</script>"
    rejects "an Int in a script" "<script>var x = {i};</script>"
    rejects "a String in a module script" "<script type=\"module\">{s}</script>"
    rejects "a String in a JSON script" "<script type=\"application/json\">{s}</script>"
    rejects "a script whose type is a hole" "<script type=\"{s}\">{s}</script>"
    rejects "a String in a style element" "<style>p {{ color: {s} }}</style>"
    accepts "an Int in a style element" "<style>p {{ order: {i} }}</style>"
    accepts "a String in a template script" "<script type=\"text/template\"><p>{s}</p></script>"
    accepts "a String in a text/x- script" "<script type=\"text/x-tmpl\"><p>{s}</p></script>"
    // Content: unchanged rules.
    accepts "Int and String content" "<p>{i} of {s}</p>"
    accepts "an Html hole in content" "<div>{h}</div>"
    // Url is built in, and a literal is checked at compile time.
    let literal = engine.Dispatch("define", flowArgs "fn rule.literal() -> Url {\n    Url.new(\"javascript:alert(1)\")\n}\n").ToJsonString()
    check $"an unsafe Url literal fails the build: {literal}" (literal.Contains("REFINEMENT_LITERAL_INVALID", StringComparison.Ordinal))
    let declared = engine.Dispatch("define", flowArgs "type Url : String {\n}\n").ToJsonString()
    check $"Url cannot be redeclared: {declared}" (declared.Contains("FLOW_URL_BUILT_IN", StringComparison.Ordinal))

let private run () =
    let runRoot = Path.Combine(repo, ".agentlang", "template-context-302", "runs", DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(runRoot) |> ignore
    ruleChecks runRoot
    let engine = Runtime.Engine(Path.Combine(runRoot, "flow-engine"), Set.empty, fileSystemMode = FileSystemMode.Virtual)
    let defined = engine.Dispatch("define", flowArgs (File.ReadAllText sourcePath))
    if not (defined["ok"].GetValue<bool>()) then failwith (defined.ToJsonString())
    for name in words do
        let response = engine.Dispatch("commit", wordArgs name)
        check $"{name} commits (its attached test passes): {response.ToJsonString()}" (response["ok"].GetValue<bool>())
    let interpret word inputs outputs values =
        match engine.BindFlowEntryPoint(word, inputs, outputs).Invoke(values) with
        | Ok value -> Ok value.Values
        | Error(diagnostic: Diagnostic) -> Error diagnostic.Code
    let toolchain = LlvmToolchain.discover ()
    let urls =
        [ ""; "/"; "/rooms/1"; "?q=1"; "#top"; "rooms/1"; "a/b:c"; "//example.com/x"; "http://example.com"; "HTTPS://Example.com/a?b#c"
          "mailto:ann@example.com"; "tel:+15551234"; "javascript:alert(1)"; "JavaScript:x"; "vbscript:x"; "data:text/html,<b>"; "a:b"
          " /a"; "/a "; "java\tscript:x"; "/a\nb"; "/\u007f"; "/é/🚀"; "file:///etc/passwd" ]
    let ints = [ 0L; 1L; -1L; 42L; -3L; 1000000L; Int64.MaxValue; Int64.MinValue ]
    let names = [ ""; "Ann"; "<A&B>"; "\"quoted\" 'single'"; "é🚀" ]
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
        let html (text: string) = Ok [ NamedValue("Html", StringValue text) ]
        pair "native.template.safe?" [ TString ] [ TBool ] [ for u in urls -> [ StringValue u ] ] (function
            | [ StringValue u ] -> Ok [ BoolValue(urlSafe u) ]
            | other -> failwith $"bad case {other}")
        pair "native.template.link" [ TString ] [ TNamed "Html" ] [ for u in urls -> [ StringValue u ] ] (function
            | [ StringValue u ] when urlSafe u -> html $"<a href=\"{TrustedValues.htmlEscape u}\">go</a>"
            | [ StringValue _ ] -> Error "REFINEMENT_FAILED"
            | other -> failwith $"bad case {other}")
        pair "native.template.count" [ TInt ] [ TNamed "Html" ] [ for n in ints -> [ IntValue n ] ] (function
            | [ IntValue n ] -> html $"<p data-n=\"{n}\" style=\"order: {n}\">{n} items</p>"
            | other -> failwith $"bad case {other}")
        pair "native.template.name" [ TString ] [ TNamed "Html" ] [ for n in names -> [ StringValue n ] ] (function
            | [ StringValue n ] -> let e = TrustedValues.htmlEscape n in html $"<b title=\"{e}\">{e}</b>"
            | other -> failwith $"bad case {other}")
    let receipt = JsonObject()
    receipt["status"] <- JsonValue.Create("pass")
    receipt["assertionCount"] <- JsonValue.Create(assertionCount.Value)
    File.WriteAllText(Path.Combine(runRoot, "receipt.json"), receipt.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + Environment.NewLine)
    printfn "template context: %d assertions passed; artifacts %s" assertionCount.Value runRoot

[<EntryPoint>]
let main _ =
    try
        run ()
        0
    with error ->
        eprintfn "%s" (error.ToString())
        1
