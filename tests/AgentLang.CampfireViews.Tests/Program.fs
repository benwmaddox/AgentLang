module AgentLang.CampfireViews.Tests

// The Campfire room page as an app-side Flow template (applications/campfire/
// views.flow), checked three ways:
//   1. against an independent F# statement of the page the C renderer
//      (campfire_http_renderer.c) writes, with Rails ERB::Util.html_escape
//      escaping, in the interpreter and native O0/O2;
//   2. when the private snap-nc capture of the C-rendered page is present
//      locally, byte-for-byte against it after normalizing the C encoder's
//      numeric entities (&#xNN;) to Rails escaping (only hashes are recorded);
//   3. a trusted-profile timing of the native render, the cost a Flow-rendered
//      body adds per request once the host sends it (report 267).

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open AgentLang
open AgentLang.Llvm

let private repo = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))
let private sources =
    // The app's bootstrap type documents (as in Http/Application.fs), then the views.
    [ "campfire-types.flow"; "session-policy.flow"; "authentication-types.flow"; "browser-types.flow"
      "request-policy.flow"; "http-types.flow"; "views.flow"; "layout.flow"; "message.flow"; "views-tests.flow" ]
    |> List.map (fun name -> "applications/campfire/" + name)
    |> List.map (fun path -> Path.Combine(repo, path))
let private privateCapture = Path.Combine(repo, ".agentlang", "views-reference", "private", "native-265.html")
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

let private sha (bytes: byte array) = Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

type private Message = { Id: int64; ClientId: string; Body: string option }

let private roomValue (id: int64) (name: string option) =
    RecordValue("RoomRow", Map.ofList [
        "id", NamedValue("RoomId", IntValue id)
        "name", OptionValue(TString, name |> Option.map StringValue)
        "kind", EnumValue("RoomKind", "closed")
        "creator-id", NamedValue("UserId", IntValue 1L)
        "created-at", StringValue "2026-01-01T00:00:00Z"
        "updated-at", StringValue "2026-01-01T00:00:00Z" ])

let private messagesValue (roomId: int64) (messages: Message list) =
    ListValue(TNamed "MessageRow", messages |> List.map (fun message ->
        RecordValue("MessageRow", Map.ofList [
            "id", NamedValue("MessageId", IntValue message.Id)
            "room-id", NamedValue("RoomId", IntValue roomId)
            "creator-id", NamedValue("UserId", IntValue 1L)
            "client-message-id", StringValue message.ClientId
            "body", OptionValue(TString, message.Body |> Option.map StringValue)
            "created-at", StringValue "2026-01-01T00:00:00Z"
            "updated-at", StringValue "2026-01-01T00:00:00Z" ])))

/// The C renderer's room page, restated independently, with Rails escaping.
let private oracle (roomId: int64) (name: string option) (messages: Message list) =
    let e = TrustedValues.htmlEscape
    let builder = StringBuilder()
    builder.Append("<!doctype html><html><body><h1>").Append(e (defaultArg name "")).Append("</h1><section id=\"messages\">") |> ignore
    for message in messages do
        builder.Append("<div id=\"message_").Append(e message.ClientId).Append("\" class=\"message\" data-message-id=\"").Append(message.Id)
            .Append("\"><div id=\"presentation_message_").Append(e message.ClientId).Append("\" dir=\"auto\">").Append(e (defaultArg message.Body ""))
            .Append("</div></div>") |> ignore
    builder.Append("</section><form method=\"post\" action=\"/rooms/").Append(roomId)
        .Append("/messages\"><textarea name=\"message[body]\"></textarea><button type=\"submit\">Send</button></form></body></html>") |> ignore
    builder.ToString()

/// The C encoder writes ' and every non-ASCII or control scalar as &#xNN;
/// where Rails writes &#39; and raw UTF-8. Normalize C output to Rails.
let private normalizeCEntities (page: string) =
    Regex.Replace(page, "&#x([0-9A-F]+);", fun (m: Match) ->
        let scalar = Convert.ToInt32(m.Groups[1].Value, 16)
        if scalar = 0x27 then "&#39;" else Char.ConvertFromUtf32 scalar)

let private run () =
    let runRoot = Path.Combine(repo, ".agentlang", "templates-266", "views-runs", DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(runRoot) |> ignore
    let engine = Runtime.Engine(Path.Combine(runRoot, "flow-engine"), Set.empty, fileSystemMode = FileSystemMode.Virtual)
    let defined = engine.Dispatch("define", flowArgs (sources |> List.map File.ReadAllText |> String.concat "\n"))
    if not (defined["ok"].GetValue<bool>()) then failwith (defined.ToJsonString())
    for name in [ "campfire.views.text"; "campfire.views.message"; "campfire.views.room"; "campfire.views.room-text" ] do
        let args = JsonObject()
        args["word"] <- JsonValue.Create(name)
        let response = engine.Dispatch("commit", args)
        if not (response["ok"].GetValue<bool>() || response.ToJsonString().Contains("COMMIT_NOT_CANDIDATE", StringComparison.Ordinal)) then
            failwith (response.ToJsonString())
    let inputs = [ TNamed "RoomRow"; TList(TNamed "MessageRow") ]
    let interpret values =
        match engine.BindFlowEntryPoint("campfire.views.room-text", inputs, [ TString ]).Invoke(values) with
        | Ok value -> value.Values
        | Error diagnostic -> failwith $"Interpreter rejected the room view: {diagnostic.Code} {diagnostic.Message}"
    let body = engine.ExportVerifiedFlowEntryPoint("campfire.views.room-text", inputs, [ TString ])
    let toolchain = LlvmToolchain.discover ()
    let tricky =
        [ { Id = 1L; ClientId = "c-1"; Body = Some "<p>092. I'm heading out — for lunch.</p>" }
          { Id = 2L; ClientId = "a\"b<c>"; Body = Some "Tom & Jerry 🚀 café" }
          { Id = 3L; ClientId = "c-3"; Body = None }
          { Id = 933434601L; ClientId = "340319c2-9264-5710-82c9-b963bf3c6d18"; Body = Some "" } ]
    let cases =
        [ "tricky", 486777696L, Some "All <Talk> & 'more'", tricky
          "empty", 7L, None, []
          "forty", 12L, Some "Room", [ for index in 1 .. 40 -> { Id = int64 index; ClientId = Guid.NewGuid().ToString(); Body = Some(String.replicate 6 $"Message {index} body text with some words. ") } ] ]
    let programs =
        [ for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] ->
            optimization, OwningStackAot.compile toolchain optimization (Path.Combine(runRoot, "native", string optimization)) body ]
    for label, roomId, name, messages in cases do
        let values = [ roomValue roomId name; messagesValue roomId messages ]
        let expected = oracle roomId name messages
        check $"{label}: interpreter = oracle" (interpret values = [ StringValue expected ])
        for optimization, program in programs do
            check $"{label}/{optimization}: native = oracle" (program.Execute(values, 4 * 1048576, 1048576).Values = [ StringValue expected ])

    // Byte-for-byte against the captured C-rendered snap-nc page (private, local).
    let receipt = JsonObject()
    if File.Exists privateCapture then
        let page = File.ReadAllText(privateCapture, Encoding.UTF8)
        let roomId = int64 (Regex.Match(page, "action=\"/rooms/(\\d+)/messages\"").Groups[1].Value)
        let name = WebUtility.HtmlDecode(Regex.Match(page, "<h1>(.*?)</h1>").Groups[1].Value)
        let messages =
            [ for m in Regex.Matches(page, "<div id=\"message_([^\"]*)\" class=\"message\" data-message-id=\"(\\d+)\"><div id=\"presentation_message_[^\"]*\" dir=\"auto\">(.*?)</div></div>") ->
                { Id = int64 m.Groups[2].Value; ClientId = WebUtility.HtmlDecode m.Groups[1].Value; Body = Some(WebUtility.HtmlDecode m.Groups[3].Value) } ]
        let values = [ roomValue roomId (Some name); messagesValue roomId messages ]
        let normalized = normalizeCEntities page
        let flowPage =
            match programs |> List.last |> snd |> fun program -> program.Execute(values, 4 * 1048576, 1048576).Values with
            | [ StringValue text ] -> text
            | other -> failwith $"Unexpected native result {other}"
        check "captured C page parses into messages" (messages.Length > 0)
        check "Flow room page = C-rendered snap-nc page after Rails entity normalization (byte-identical)" (flowPage = normalized)
        check "the only C/Flow differences are numeric entities" (normalized <> page && Regex.Matches(page, "&#x[0-9A-F]+;").Count > 0)
        receipt["privateCapture"] <-
            JsonObject(dict [
                "cPageSha256", JsonValue.Create(sha (Encoding.UTF8.GetBytes page)) :> JsonNode
                "normalizedSha256", JsonValue.Create(sha (Encoding.UTF8.GetBytes normalized))
                "flowPageSha256", JsonValue.Create(sha (Encoding.UTF8.GetBytes flowPage))
                "messages", JsonValue.Create(messages.Length)
                "cNumericEntities", JsonValue.Create(Regex.Matches(page, "&#x[0-9A-F]+;").Count)
                "bytes", JsonValue.Create(Encoding.UTF8.GetByteCount flowPage) ])
    else
        receipt["privateCapture"] <- JsonValue.Create("absent: byte comparison skipped")

    // Trusted-profile timing of the native render (40-message page).
    let timing = JsonObject()
    use trusted = OwningStackAot.compileWithRuntimeProfile toolchain LlvmOptimization.O2 OwningRuntimeProfile.TrustedGenerated (Path.Combine(runRoot, "native", "trusted")) body
    // The trusted runtime has its own field-location fast path (report 268).
    for label, roomId, name, messages in cases do
        let values = [ roomValue roomId name; messagesValue roomId messages ]
        check $"{label}/trusted O2: native = oracle" (trusted.Execute(values, 4 * 1048576, 1048576).Values = [ StringValue(oracle roomId name messages) ])
    let _, roomId, name, messages = cases |> List.find (fun (label, _, _, _) -> label = "forty")
    let values = [ roomValue roomId name; messagesValue roomId messages ]
    let pageBytes = Encoding.UTF8.GetByteCount(oracle roomId name messages)
    for _ in 1 .. 200 do trusted.Execute(values, 4 * 1048576, 1048576) |> ignore
    let samples =
        [| for _ in 1 .. 2000 ->
            let result = trusted.Execute(values, 4 * 1048576, 1048576)
            ignore result
            float trusted.LastNativeElapsedTicks * 1_000_000.0 / float Stopwatch.Frequency |]
    Array.sortInPlace samples
    timing["pageBytes"] <- JsonValue.Create(pageBytes)
    timing["messages"] <- JsonValue.Create(messages.Length)
    timing["minMicroseconds"] <- JsonValue.Create(Math.Round(samples[0], 2))
    timing["medianMicroseconds"] <- JsonValue.Create(Math.Round(samples[samples.Length / 2], 2))
    timing["p99Microseconds"] <- JsonValue.Create(Math.Round(samples[samples.Length * 99 / 100], 2))
    receipt["trustedO2Render"] <- timing
    receipt["status"] <- JsonValue.Create("pass")
    receipt["assertionCount"] <- JsonValue.Create(assertionCount.Value)
    File.WriteAllText(Path.Combine(runRoot, "receipt.json"), receipt.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + Environment.NewLine)
    printfn "campfire views: %d assertions passed; render %s; artifacts %s" assertionCount.Value (timing.ToJsonString()) runRoot

/// Developer profiling: times the pieces of the room render separately.
let private profile (extraFlow: string option) =
    let runRoot = Path.Combine(repo, ".agentlang", "templates-266", "profile", Guid.NewGuid().ToString("N"))
    let engine = Runtime.Engine(Path.Combine(runRoot, "flow-engine"), Set.empty, fileSystemMode = FileSystemMode.Virtual)
    let source =
        (sources |> List.map File.ReadAllText |> String.concat "\n") + "\n" + File.ReadAllText(Path.Combine(__SOURCE_DIRECTORY__, "bench.flow"))
        + (match extraFlow with Some path -> "\n" + File.ReadAllText path | None -> "")
    let defined = engine.Dispatch("define", flowArgs source)
    if not (defined["ok"].GetValue<bool>()) then failwith (defined.ToJsonString())
    for name in [ "campfire.views.text"; "campfire.views.message"; "campfire.views.room"; "campfire.views.room-text"; "bench.item"; "bench.names"; "bench.client-id"; "bench.ids"; "bench.id-text"; "bench.id-texts"; "bench.count"; "bench.rails-page"; "bench.static-page"; "bench.ctx-page" ] |> List.filter (fun name -> not (name.StartsWith "bench.rails" || name.StartsWith "bench.static" || name.StartsWith "bench.ctx") || (extraFlow.IsSome && File.ReadAllText(extraFlow.Value).Contains(name))) do
        let args = JsonObject()
        args["word"] <- JsonValue.Create(name)
        let response = engine.Dispatch("commit", args)
        if not (response["ok"].GetValue<bool>() || response.ToJsonString().Contains("COMMIT_NOT_CANDIDATE", StringComparison.Ordinal)) then failwith (response.ToJsonString())
    let toolchain = LlvmToolchain.discover ()
    let messages = [ for index in 1 .. 40 -> { Id = int64 index; ClientId = Guid.NewGuid().ToString(); Body = Some(String.replicate 6 $"Message {index} body text with some words. ") } ]
    let messageList = messagesValue 12L messages
    let time label word inputs outputs values =
        let body = engine.ExportVerifiedFlowEntryPoint(word, inputs, outputs)
        use program = OwningStackAot.compileWithRuntimeProfile toolchain LlvmOptimization.O2 OwningRuntimeProfile.TrustedGenerated (Path.Combine(runRoot, word.Replace("?", "-q"))) body
        // Timing a wrong answer proves nothing: native must equal the interpreter.
        let expected =
            match engine.BindFlowEntryPoint(word, inputs, outputs).Invoke(values) with
            | Ok value -> value.Values
            | Error diagnostic -> failwith $"Interpreter rejected {word}: {diagnostic.Code} {diagnostic.Message}"
        if program.Execute(values, 4 * 1048576, 1048576).Values <> expected then failwith $"{label}: trusted native output differs from the interpreter"
        for _ in 1 .. 200 do program.Execute(values, 4 * 1048576, 1048576) |> ignore
        let samples = [| for _ in 1 .. 1000 -> (program.Execute(values, 4 * 1048576, 1048576) |> ignore; float program.LastNativeElapsedTicks * 1_000_000.0 / float Stopwatch.Frequency) |]
        Array.sortInPlace samples
        printfn "%-34s min %8.1f us  median %8.1f us" label samples[0] samples[samples.Length / 2]
    time "count(messages)" "bench.count" [ TList(TNamed "MessageRow") ] [ TInt ] [ messageList ]
    time "join(map client-id)" "bench.ids" [ TList(TNamed "MessageRow") ] [ TString ] [ messageList ]
    time "join(map int.to-string(id))" "bench.id-texts" [ TList(TNamed "MessageRow") ] [ TString ] [ messageList ]
    for count in [ 10; 40; 160; 640 ] do
        time $"template map over {count} Strings" "bench.names" [ TList TString ] [ TString ] [ ListValue(TString, [ for index in 1 .. count -> StringValue $"Message {index} body text with some words." ]) ]
    time "room page (40 messages)" "campfire.views.room-text" [ TNamed "RoomRow"; TList(TNamed "MessageRow") ] [ TString ] [ roomValue 12L (Some "Room"); messageList ]
    // Optional cost probe (report 268): a Rails-sized partial (~11.6 KB, 58
    // holes) from a private capture, passed as an extra flow file.
    if extraFlow.IsSome then
        let benchMessage index =
            RecordValue("BenchMessage", Map.ofList [
                "id", StringValue(string (933434481 + index)); "client-id", StringValue(Guid.NewGuid().ToString())
                "user-id", StringValue "773523953"; "author", StringValue "JZ"; "title", StringValue "JZ – Designer"
                "avatar", StringValue "/users/eyJfcmFpbHMiOnsiZGF0YSI6NzczNTIzOTUzfX0--0000000000000000000000000000000000000000000000000000000000000000/avatar?v=20260102180000"
                "token", StringValue "synthetic-token-synthetic-token-synthetic-token-synthetic-token-synthetic-token-synthe"
                "room-id", StringValue "486777696"; "room-name", StringValue "All Talk"
                "time", StringValue "2026-03-02T15:30:00Z"; "ms", StringValue "1772465400000" ])
        let messages = ListValue(TNamed "BenchMessage", [ for index in 1 .. 40 -> benchMessage index ])
        time "Rails-sized partial x40" "bench.rails-page" [ TList(TNamed "BenchMessage") ] [ TString ] [ messages ]
        if File.ReadAllText(extraFlow.Value).Contains("bench.ctx-page") then
            time "map with context x40" "bench.ctx-page" [ TList(TNamed "BenchMessage") ] [ TString ] [ messages ]
        if File.ReadAllText(extraFlow.Value).Contains("bench.static-page") then
            time "static partial x40 (no holes)" "bench.static-page" [ TList(TNamed "BenchMessage") ] [ TString ] [ messages ]

[<EntryPoint>]
let main argv =
    try
        if argv |> Array.contains "--profile" then
            let extra = argv |> Array.tryFindIndex ((=) "--probe") |> Option.map (fun index -> argv[index + 1])
            profile extra
        else run ()
        0
    with error ->
        eprintfn "%s" (error.ToString())
        1
