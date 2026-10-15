module AgentLang.OwningTemplates.Tests

// html"…" templates: escaping (Rails ERB::Util.html_escape vectors), Html holes,
// multi-line dedent and literal braces; interpreter/native parity at O0 and O2;
// one sized write per template (no staged literals, no escaped temporaries);
// string.html-fragment? parity between the F# and C checkers on a fixed corpus
// and a deterministic random corpus; html.raw run-time validation; and every
// compile-time template rejection by diagnostic code.

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
let private sourcePath = Path.Combine(__SOURCE_DIRECTORY__, "owning-templates.flow")
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

let private hash (path: string) = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()

let private extentOf (text: string) =
    let bytes = Encoding.UTF8.GetByteCount text
    (8 + bytes + 7) / 8 * 8

let private htmlPrelude = ""

let private run () =
    let runRoot = Path.Combine(repo, ".agentlang", "templates-266", "runs", DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(runRoot) |> ignore
    let engine = Runtime.Engine(Path.Combine(runRoot, "flow-engine"), Set.empty, fileSystemMode = FileSystemMode.Virtual)
    let defined = engine.Dispatch("define", flowArgs (File.ReadAllText sourcePath))
    if not (defined["ok"].GetValue<bool>()) then failwith (defined.ToJsonString())
    for name in [ "native.tpl.item"; "native.tpl.list"; "native.tpl.card"; "native.tpl.braces"; "native.tpl.empty"; "native.tpl.only-hole"; "native.tpl.raw"; "native.tpl.reraw"; "native.tpl.reraw-open"; "native.tpl.reraw-extra"; "native.tpl.reraw-var"; "native.tpl.fragment?"; "native.tpl.names"; "native.tpl.pair"; "native.tpl.escaped" ] do
        let args = JsonObject()
        args["word"] <- JsonValue.Create(name)
        let response = engine.Dispatch("commit", args)
        if not (response["ok"].GetValue<bool>() || response.ToJsonString().Contains("COMMIT_NOT_CANDIDATE", StringComparison.Ordinal)) then
            failwith (response.ToJsonString())
        check $"{name} commits" true

    // Compile-time rejections, each by its stable diagnostic code.
    let rejects label (code: string) (body: string) =
        let probe = Runtime.Engine(Path.Combine(runRoot, "rejections", Guid.NewGuid().ToString("N")), Set.empty, fileSystemMode = FileSystemMode.Virtual)
        let source = htmlPrelude + "fn probe.f(name: String, h: Html, n: Int) -> Html {\n    effects none\n    " + body + "\n}\n"
        let response = probe.Dispatch("define", flowArgs source).ToJsonString()
        check $"rejects {label} with {code}" (response.Contains(code, StringComparison.Ordinal))
    rejects "an unclosed element" "FLOW_TEMPLATE_MARKUP" "html\"<div>{name}\""
    rejects "a misnested end tag" "FLOW_TEMPLATE_MARKUP" "html\"<b><i></b></i>\""
    rejects "a bare less-than" "FLOW_TEMPLATE_MARKUP" "html\"a < b\""
    rejects "an Html hole in an attribute" "FLOW_TEMPLATE_MARKUP" "html\"\"\"<a title=\"{h}\"></a>\"\"\""
    rejects "a hole in a tag name" "FLOW_TEMPLATE_MARKUP" "html\"<{name}></x>\""
    rejects "a hole in an unquoted attribute" "FLOW_TEMPLATE_MARKUP" "html\"<a title={name}></a>\""
    rejects "a hole between attributes" "FLOW_TEMPLATE_MARKUP" "html\"<a {name}></a>\""
    rejects "an Html hole in script raw text" "FLOW_TEMPLATE_MARKUP" "html\"<script>{h}</script>\""
    rejects "a hole in a comment" "FLOW_TEMPLATE_MARKUP" "html\"<!-- {name} -->\""
    rejects "an Int hole" "FLOW_TEMPLATE_HOLE_TYPE" "html\"<p>{n}</p>\""
    rejects "a lone closing brace" "FLOW_TEMPLATE_BRACE" "html\"<p>}</p>\""
    rejects "an empty hole" "FLOW_TEMPLATE_HOLE" "html\"<p>{ }</p>\""
    rejects "two expressions in one hole" "FLOW_TEMPLATE_HOLE" "html\"<p>{name name}</p>\""
    rejects "a raw newline in one-line form" "FLOW_STRING_NEWLINE" "html\"<p>\n</p>\""
    rejects "a closing delimiter with text" "FLOW_TEMPLATE_INDENT" "html\"\"\"\n        <p></p>\n    x\"\"\""
    rejects "a line less indented than the closing delimiter" "FLOW_TEMPLATE_INDENT" "html\"\"\"\n  <p></p>\n        \"\"\""
    let defineProbe label (source: string) =
        let probe = Runtime.Engine(Path.Combine(runRoot, "rejections", label), Set.empty, fileSystemMode = FileSystemMode.Virtual)
        probe.Dispatch("define", flowArgs source).ToJsonString()
    check "Html is built in: a program may not declare it (FLOW_HTML_BUILT_IN)"
        ((defineProbe "declared-html" "type Html : String {\n}\n\nfn probe.h(name: String) -> String {\n    effects none\n    name\n}\n").Contains("FLOW_HTML_BUILT_IN", StringComparison.Ordinal))
    check "html.raw is built in: a program may not declare it (FLOW_HTML_BUILT_IN)"
        ((defineProbe "declared-raw" "fn html.raw(text: String) -> String {\n    effects none\n    text\n}\n").Contains("FLOW_HTML_BUILT_IN", StringComparison.Ordinal))
    check "Html.new takes a String (FLOW_HTML_NEW_ARGUMENT)"
        ((defineProbe "new-int" "fn probe.n(n: Int) -> Html {\n    effects none\n    Html.new(n)\n}\n").Contains("FLOW_HTML_NEW_ARGUMENT", StringComparison.Ordinal))
    check "Html.new as a bare list callback is rejected (FLOW_HTML_CALLBACK)"
        ((defineProbe "new-callback" "fn probe.m(names: List<String>) -> List<Html> {\n    effects none\n    names.map(Html.new)\n}\n").Contains("FLOW_HTML_CALLBACK", StringComparison.Ordinal))
    check "html.raw as a bare list callback is rejected (FLOW_HTML_CALLBACK)"
        ((defineProbe "raw-callback" "fn probe.m(names: List<String>) -> List<Html> {\n    effects none\n    names.map(html.raw)\n}\n").Contains("FLOW_HTML_CALLBACK", StringComparison.Ordinal))
    check "an invalid html.raw literal is rejected at compile time (REFINEMENT_LITERAL_INVALID)"
        ((defineProbe "raw-literal" "fn probe.r() -> Html {\n    effects none\n    html.raw(\"<b>open\")\n}\n\ntest probe.r/x {\n    Html.value(probe.r())\n    => value \"x\"\n}\n").Contains("REFINEMENT_LITERAL_INVALID", StringComparison.Ordinal))

    let toolchain = LlvmToolchain.discover ()
    let interpret word inputs outputs values =
        match engine.BindFlowEntryPoint(word, inputs, outputs).Invoke(values) with
        | Ok value -> Ok value.Values
        | Error diagnostic -> Error diagnostic.Code
    // Rails ERB::Util.html_escape vectors (exactly & < > " ' change).
    let names =
        [ ""; "plain"; "Tom & Jerry"; "<script>alert('x')</script>"; "\"quoted\""; "a'b"; "&amp;"; "café 🚀"; "日本語"; String('<', 300); "x>y" ]
    let mutable fragmentNativeChecks = 0
    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        let native word inputs outputs =
            let body = engine.ExportVerifiedFlowEntryPoint(word, inputs, outputs)
            OwningStackAot.compile toolchain optimization (Path.Combine(runRoot, "native", string optimization, word.Replace("?", "-q"))) body
        use item = native "native.tpl.item" [ TString ] [ TString ]
        use list = native "native.tpl.list" [ TString; TString ] [ TString ]
        use card = native "native.tpl.card" [ TString; TString ] [ TString ]
        use braces = native "native.tpl.braces" [ TString ] [ TString ]
        use empty = native "native.tpl.empty" [] [ TString ]
        use onlyHole = native "native.tpl.only-hole" [ TString ] [ TString ]
        use raw = native "native.tpl.raw" [ TString ] [ TString ]
        use fragment = native "native.tpl.fragment?" [ TString ] [ TBool ]
        use namesProgram = native "native.tpl.names" [ TList TString ] [ TString ]
        use pair = native "native.tpl.pair" [ TString; TString ] [ TString ]
        use escapedProgram = native "native.tpl.escaped" [ TString ] [ TString ]
        let o = string optimization
        for name in names do
            let label = JsonSerializer.Serialize name
            let escaped = TrustedValues.htmlEscape name
            let itemExpected = "<li>" + escaped + "</li>"
            let itemNative = item.Execute([ StringValue name ], 65536, 65536)
            check $"item {label}/{o}: interpreter" (interpret "native.tpl.item" [ TString ] [ TString ] [ StringValue name ] = Ok [ StringValue itemExpected ])
            check $"item {label}/{o}: native = interpreter" (itemNative.Values = [ StringValue itemExpected ])
            // One sized write: the template result is the only copy (literals come
            // from read-only data and the escape writes straight into the result).
            check $"item {label}/{o}: deep copy is exactly the result extent" (itemNative.Metrics.DeepCopyBytes = uint64 (extentOf itemExpected))
            check $"item {label}/{o}: no moves" (itemNative.Metrics.MoveBytes = 0UL)
            let listExpected = "<ul class=\"names\">" + itemExpected + "<li>second</li></ul>"
            let listNative = list.Execute([ StringValue name; StringValue "second" ], 65536, 65536)
            check $"list {label}/{o}: interpreter" (interpret "native.tpl.list" [ TString; TString ] [ TString ] [ StringValue name; StringValue "second" ] = Ok [ StringValue listExpected ])
            check $"list {label}/{o}: native = interpreter" (listNative.Values = [ StringValue listExpected ])
            check $"list {label}/{o}: deep copy is the two items plus the list, each once"
                (listNative.Metrics.DeepCopyBytes = uint64 (extentOf itemExpected + extentOf "<li>second</li>" + extentOf listExpected))
            let cardExpected = "<article class=\"card\" data-title=\"" + escaped + "\">\n  <h2>" + escaped + "</h2>\n  <p>body</p>\n</article>"
            check $"card {label}/{o}: interpreter" (interpret "native.tpl.card" [ TString; TString ] [ TString ] [ StringValue name; StringValue "body" ] = Ok [ StringValue cardExpected ])
            check $"card {label}/{o}: native" (card.Execute([ StringValue name; StringValue "body" ], 65536, 65536).Values = [ StringValue cardExpected ])
            check $"only-hole {label}/{o}: native" (onlyHole.Execute([ StringValue name ], 65536, 65536).Values = [ StringValue escaped ])
            let utf8 = Encoding.UTF8.GetBytes itemExpected
            let wire = Array.concat [ BitConverter.GetBytes(uint32 utf8.Length); Array.zeroCreate<byte> 4; utf8; Array.zeroCreate<byte> ((8 - (utf8.Length % 8)) % 8) ]
            check $"item {label}/{o}: retained bytes" (itemNative.RetainedOutputBytes = wire)
        // List<Html> holes: every item inserted in order, once.
        for count in [ 0; 1; 3; 40 ] do
            let items = [ for index in 1 .. count -> names[index % names.Length] + string index ]
            let input = [ ListValue(TString, items |> List.map StringValue) ]
            let expected = "<ul class=\"names\">" + (items |> List.map (fun item -> "<li>" + TrustedValues.htmlEscape item + "</li>") |> String.concat "") + "</ul>"
            check $"names x{count}/{o}: interpreter" (interpret "native.tpl.names" [ TList TString ] [ TString ] input = Ok [ StringValue expected ])
            check $"names x{count}/{o}: native" (namesProgram.Execute(input, 1048576, 1048576).Values = [ StringValue expected ])
        check $"pair/{o}: native" (pair.Execute([ StringValue "a&"; StringValue "b" ], 4096, 4096).Values = [ StringValue "<ol><li>a&amp;</li><li>b</li></ol>" ])
        // Html.new escapes every String, in both backends, with no validator call.
        for name in names do
            let expected = "<p>" + TrustedValues.htmlEscape name + "</p>"
            check $"Html.new escapes {JsonSerializer.Serialize name}/{o}: interpreter" (interpret "native.tpl.escaped" [ TString ] [ TString ] [ StringValue name ] = Ok [ StringValue expected ])
            check $"Html.new escapes {JsonSerializer.Serialize name}/{o}: native" (escapedProgram.Execute([ StringValue name ], 65536, 65536).Values = [ StringValue expected ])
        check $"braces/{o}" (braces.Execute([ StringValue "n" ], 4096, 4096).Values = [ StringValue "<style>.a { color: red; }</style><b>{n}</b>" ])
        check $"empty/{o}" (empty.Execute([], 4096, 4096).Values = [ StringValue "" ])
        // html.raw validates at run time in both backends.
        for text, ok in [ "<b>bold</b>", true; "<br>", true; "plain & simple", true; "<b>open", false; "</b>", false; "a < b", false ] do
            let label = JsonSerializer.Serialize text
            let interpreted = interpret "native.tpl.raw" [ TString ] [ TString ] [ StringValue text ]
            let nativeOutcome =
                try Ok(raw.Execute([ StringValue text ], 65536, 65536).Values)
                with :? OwningStackExecutionException as error -> Error error.Diagnostic.Code
            if ok then
                check $"raw {label}/{o}: interpreter accepts" (interpreted = Ok [ StringValue("<div>" + text + "</div>") ])
                check $"raw {label}/{o}: native accepts" (nativeOutcome = Ok [ StringValue("<div>" + text + "</div>") ])
            else
                check $"raw {label}/{o}: interpreter rejects with REFINEMENT_FAILED" (interpreted = Error "REFINEMENT_FAILED")
                check $"raw {label}/{o}: native rejects" (nativeOutcome = Error "REFINEMENT_FAILED")
        // html.raw of markup that is already Html (report 278) skips the check; Html
        // joined with a String that is not a proven fragment is still checked.
        let compiledIr word =
            File.ReadAllText(Path.Combine(runRoot, "native", string optimization, word, "owning-stack-native.ll"))
        let fragmentChecks (ir: string) = Regex.Matches(ir, "call i32 @al_owning_string_html_fragment").Count
        use reraw = native "native.tpl.reraw" [ TString ] [ TString ]
        use rerawOpen = native "native.tpl.reraw-open" [ TString ] [ TString ]
        use rerawExtra = native "native.tpl.reraw-extra" [ TString; TString ] [ TString ]
        use rerawVar = native "native.tpl.reraw-var" [ TString; TString ] [ TString ]
        let outcome (program: OwningStackCompiledProgram) values =
            try Ok(program.Execute(values, 65536, 65536).Values)
            with :? OwningStackExecutionException as error -> Error error.Diagnostic.Code
        for name in names do
            let expected = "<p>" + TrustedValues.htmlEscape name + "</p>"
            let inputs = [ StringValue name ]
            check $"reraw {JsonSerializer.Serialize name}/{o}: native = interpreter" (
                outcome reraw inputs = interpret "native.tpl.reraw" [ TString ] [ TString ] inputs
                && outcome reraw inputs = Ok [ StringValue(expected + "<br>" + expected) ])
        check $"reraw/{o}: html.raw of Html-built markup runs no fragment check" (fragmentChecks (compiledIr "native.tpl.reraw") = 0)
        check $"reraw-open/{o}: Html + \"<b\" is still checked" (fragmentChecks (compiledIr "native.tpl.reraw-open") = 1)
        check $"reraw-open/{o}: and rejected in both backends" (
            outcome rerawOpen [ StringValue "a" ] = Error "REFINEMENT_FAILED"
            && interpret "native.tpl.reraw-open" [ TString ] [ TString ] [ StringValue "a" ] = Error "REFINEMENT_FAILED")
        for extra, ok in [ "<i>b</i>", true; "", true; "</p>", false; "<b>", false; "a < b", false; "<!-- x", false ] do
            for word, program in [ "native.tpl.reraw-extra", rerawExtra; "native.tpl.reraw-var", rerawVar ] do
                let inputs = [ StringValue "a"; StringValue extra ]
                let interpreted = interpret word [ TString; TString ] [ TString ] inputs
                let expected = if ok then Ok [ StringValue("<p>a</p>" + extra) ] else Error "REFINEMENT_FAILED"
                check $"{word} {JsonSerializer.Serialize extra}/{o}: interpreter" (interpreted = expected)
                check $"{word} {JsonSerializer.Serialize extra}/{o}: native" (outcome program inputs = expected)
        check $"reraw-extra/{o}: an untrusted String keeps its check" (fragmentChecks (compiledIr "native.tpl.reraw-extra") = 1)
        check $"reraw-var/{o}: a var keeps its check" (fragmentChecks (compiledIr "native.tpl.reraw-var") = 1)
        // string.html-fragment? parity: F# checker, interpreter and C checker.
        let corpus =
            [ "", true; "text", true; "<p>a</p>", true; "<p>", false; "</p>", false; "<br>", true; "<br/>", true; "<img src=x alt='y'>", true
              "<div class=\"a\" hidden>x</div>", true; "<div class=>", false; "<a href=x\"y>", false; "<!-- c <x> -->", true; "<!-- open", false
              "<!doctype html><html><body></body></html>", true; "<div><!doctype html></div>", false; "<!x>", false; "<!-x>", false
              "<script>if (a < b) { x('</div>') }</script>", true; "<script>x</SCRIPT>", true; "<style>a{}</style>", true; "<script>", false
              "<svg><path d=\"M0 0\"/></svg>", true; "<P>x</p>", true; "<b><i></b></i>", false; "a < b", false; "a > b", true; "a &lt; b", true
              "<p\n class=\"x\"\n>y</p>", true; "</br>", false; "<p></p >", true; "<p></p x>", false; "<a b c=d e='f' g=\"h\">", false
              "<a b c=d e='f' g=\"h\"></a>", true; "<1>", false; "<-->", false; "<!---->", true; "<!-- a -- b -->", true; "é<b>ü</b>🚀", true
              String.replicate 256 "<i>" + String.replicate 256 "</i>", true; String.replicate 257 "<i>" + String.replicate 257 "</i>", false ]
        for text, expected in corpus do
            let label = JsonSerializer.Serialize(if text.Length > 40 then text.Substring(0, 40) + "…" else text)
            check $"fragment {label}: F# checker" (HtmlMarkup.isFragment text = expected)
            check $"fragment {label}/{o}: native" (fragment.Execute([ StringValue text ], 65536, 4096).Values = [ BoolValue expected ])
        if optimization = LlvmOptimization.O0 then
            for text, expected in corpus |> List.filter (fun (text, _) -> text.Length < 200) do
                check $"fragment {JsonSerializer.Serialize text}: interpreter" (interpret "native.tpl.fragment?" [ TString ] [ TBool ] [ StringValue text ] = Ok [ BoolValue expected ])
        // Deterministic random corpus over markup-significant characters.
        let random = Random(266)
        let alphabet = [| "<"; ">"; "/"; "!"; "-"; "--"; "a"; "b"; "br"; "p"; "script"; "style"; " "; "\""; "'"; "="; "doctype"; "x"; "é" |]
        for _ in 1 .. 1500 do
            let text = String.Join("", Array.init (random.Next(1, 14)) (fun _ -> alphabet[random.Next(alphabet.Length)]))
            let expected = HtmlMarkup.isFragment text
            fragmentNativeChecks <- fragmentNativeChecks + 1
            check $"random fragment {JsonSerializer.Serialize text}/{o}: native = F#" (fragment.Execute([ StringValue text ], 65536, 4096).Values = [ BoolValue expected ])

    let receipt = JsonObject()
    receipt["status"] <- JsonValue.Create("pass")
    receipt["assertionCount"] <- JsonValue.Create(assertionCount.Value)
    receipt["randomFragmentChecks"] <- JsonValue.Create(fragmentNativeChecks)
    let pins = JsonArray()
    for path in [ sourcePath; Path.Combine(__SOURCE_DIRECTORY__, "Program.fs"); Path.Combine(repo, "src/AgentLang.Core/HtmlMarkup.fs"); Path.Combine(repo, "src/AgentLang.Llvm/native/owning_stack_runtime.c"); Path.Combine(repo, "src/AgentLang.Llvm/OwningStackAot.fs") ] do
        let pin = JsonObject()
        pin["path"] <- JsonValue.Create(Path.GetRelativePath(repo, path).Replace('\\', '/'))
        pin["sha256"] <- JsonValue.Create(hash path)
        pins.Add(pin)
    receipt["sourcePins"] <- pins
    File.WriteAllText(Path.Combine(runRoot, "receipt.json"), receipt.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + Environment.NewLine)
    printfn "owning templates: %d assertions passed; artifacts %s" assertionCount.Value runRoot

[<EntryPoint>]
let main _ =
    try
        run ()
        0
    with error ->
        eprintfn "%s" (error.ToString())
        1
