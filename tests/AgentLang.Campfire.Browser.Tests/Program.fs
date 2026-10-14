namespace AgentLang.Campfire.Browser.Tests

open System
open System.Globalization
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open AgentLang
open AgentLang.Llvm

/// Browser normalization is pure Flow (applications/campfire/user-agent.flow).
/// The parity corpus (user-agent-corpus-tests.flow) froze the retired F# port's
/// outputs, which matched the Ruby-generated fixture; this runner proves the Flow
/// words pass it in the interpreter, commit, survive a reload, and that the
/// owning native build agrees with the interpreter on every corpus input.
module Program =
    let mutable private assertions = 0

    let private check condition message =
        assertions <- assertions + 1
        if not condition then failwith $"Assertion failed: {message}"

    let private equal expected actual message =
        check (expected = actual) $"{message}: expected {expected}, actual {actual}"

    let private repositoryRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

    let private runDirectory =
        Path.Combine(
            repositoryRoot,
            ".agentlang",
            "campfire-http-242",
            "browser",
            "runs",
            DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N"))

    let private applicationSource name = Path.Combine(repositoryRoot, "applications", "campfire", name)
    let private corpusPath = applicationSource "user-agent-corpus-tests.flow"
    let private fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "campfire_user_agents.json")

    let private flowSource includeCorpus =
        [ applicationSource "browser-types.flow"
          applicationSource "user-agent.flow"
          applicationSource "user-agent-tests.flow"
          if includeCorpus then corpusPath
          applicationSource "browser-policy.flow"
          applicationSource "browser-tests.flow"
          Path.Combine(__SOURCE_DIRECTORY__, "native-browser.flow") ]
        |> List.map File.ReadAllText
        |> String.concat "\n\n"

    /// The browser policy and its Ruby-version closure, in dependency order, as
    /// library words (complete coverage by their own tests).
    let private libraryWords =
        [ "campfire.ua.byte"
          "campfire.ua.digit?"
          "campfire.ua.space?"
          "campfire.ua.cut"
          "campfire.ua.ruby-blank?"
          "campfire.ua.letter?"
          "campfire.ua.leading-digits"
          "campfire.ua.comparable?"
          "campfire.ua.digit-end"
          "campfire.ua.word-end"
          "campfire.ua.trim-zeros"
          "campfire.ua.segments"
          "campfire.ua.segment-at"
          "campfire.ua.compare-bytes"
          "campfire.ua.compare-segment"
          "campfire.browser.version-compare"
          "campfire.browser.version-at-least?"
          "campfire.browser.supported?" ]

    let private dispatch (engine: Runtime.Engine) operation (fields: (string * JsonNode) list) =
        let arguments = JsonObject()
        for key, value in fields do arguments[key] <- value
        let response = engine.Dispatch(operation, arguments)
        if not (response["ok"].GetValue<bool>()) then
            let text = response.ToJsonString()
            failwith $"{operation} failed: {text.Substring(0, min 2000 text.Length)}"
        response

    let private createEngine project =
        Runtime.Engine(project, Set.empty, "2026-10-11T12:00:00Z", fileSystemMode = FileSystemMode.Virtual, testCapabilities = Set.empty)

    let private testAll engine label =
        let results = (dispatch engine "test-all" []).["data"].["results"].AsArray()
        for result in results do
            let word = result["word"].GetValue<string>()
            let name = result["name"].GetValue<string>()
            check (result["passed"].GetValue<bool>()) $"{label}: {word}/{name}"
        results.Count

    let private flowString = "\"(?:[^\"\\\\]|\\\\.)*\""
    let private decode (literal: string) = JsonSerializer.Deserialize<string>(literal)

    let private corpusUserAgents (source: string) =
        Regex.Matches(source, $@"(?m)^    campfire\.browser\.from-user-agent\((option\.none<String>\(\)|option\.some<String>\(({flowString})\))\)$")
        |> Seq.map (fun m -> if m.Groups[2].Success then Some(decode m.Groups[2].Value) else None)
        |> Seq.toList

    let private corpusComparisons (source: string) =
        Regex.Matches(source, $@"(?m)^    campfire\.browser\.version-compare\(({flowString}), ({flowString})\)$")
        |> Seq.map (fun m -> decode m.Groups[1].Value, decode m.Groups[2].Value)
        |> Seq.toList

    let private fixtureCoverage (userAgents: string option list) =
        use document = JsonDocument.Parse(File.ReadAllText fixturePath)
        let fixture =
            document.RootElement.GetProperty("user_agents").EnumerateArray()
            |> Seq.map (fun case -> let ua = case.GetProperty("ua") in if ua.ValueKind = JsonValueKind.String then Some(ua.GetString()) else None)
            |> Seq.toList
        equal 385 fixture.Length "pinned Ruby User-Agent fixture count"
        let corpus = Set.ofList userAgents
        for userAgent in fixture do
            check (corpus.Contains userAgent) $"Ruby fixture User-Agent {userAgent} is in the Flow parity corpus"
        let comparisons = document.RootElement.GetProperty("comparisons").GetArrayLength()
        equal 352 comparisons "pinned Ruby comparison fixture count"

    let private nativeParity (engine: Runtime.Engine) (userAgents: string option list) (comparisons: (string * string) list) =
        let toolchain = LlvmToolchain.discover ()
        let probeInputs = [ TBool; TString ]
        let compareInputs = [ TString; TString ]
        let probeBody = engine.ExportVerifiedFlowEntryPoint("campfire.browser.native-probe", probeInputs, [ TString ])
        let compareBody = engine.ExportVerifiedFlowEntryPoint("campfire.browser.version-compare", compareInputs, [ TInt ])
        let probe = engine.BindFlowEntryPoint("campfire.browser.native-probe", probeInputs, [ TString ])
        let compare = engine.BindFlowEntryPoint("campfire.browser.version-compare", compareInputs, [ TInt ])
        let interpret (entry: Runtime.FlowEntryPoint) values =
            match entry.Invoke values with
            | Ok result -> result.Values
            | Error diagnostic -> failwith $"interpreter rejected {diagnostic.Code}"
        let probeCases =
            userAgents |> List.map (function Some ua -> [ BoolValue true; StringValue ua ] | None -> [ BoolValue false; StringValue "" ])
        let compareCases = comparisons |> List.map (fun (left, right) -> [ StringValue left; StringValue right ])
        let expectedProbes = probeCases |> List.map (interpret probe)
        let expectedCompares = compareCases |> List.map (interpret compare)
        for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
            let root = Path.Combine(runDirectory, "native", string optimization)
            use nativeProbe = OwningStackAot.compile toolchain optimization (Path.Combine(root, "probe")) probeBody
            use nativeCompare = OwningStackAot.compile toolchain optimization (Path.Combine(root, "compare")) compareBody
            List.iter2
                (fun values expected ->
                    let actual = nativeProbe.Execute(values, 1 <<< 24, 1 <<< 24).Values
                    check (actual = expected) $"{optimization} native from-user-agent {values} = interpreter {expected}, got {actual}")
                probeCases expectedProbes
            List.iter2
                (fun values expected ->
                    let actual = nativeCompare.Execute(values, 1 <<< 20, 1 <<< 20).Values
                    check (actual = expected) $"{optimization} native version-compare {values} = interpreter {expected}, got {actual}")
                compareCases expectedCompares
        probeCases.Length, compareCases.Length

    [<EntryPoint>]
    let main _ =
        Directory.CreateDirectory runDirectory |> ignore
        let corpus = File.ReadAllText corpusPath
        let userAgents = corpusUserAgents corpus
        let comparisons = corpusComparisons corpus
        check (userAgents.Length >= 100) $"parity corpus has at least 100 User-Agents ({userAgents.Length})"
        check (comparisons.Length >= 40) $"parity corpus has at least 40 version comparisons ({comparisons.Length})"
        fixtureCoverage userAgents

        // The parity corpus runs uncommitted: commits rerun attached tests.
        let corpusEngine = createEngine (Path.Combine(runDirectory, "corpus-project"))
        dispatch corpusEngine "define" [ "frontend", JsonValue.Create("flow"); "syntaxVersion", JsonValue.Create(2); "source", JsonValue.Create(flowSource true) ] |> ignore
        let corpusRun = testAll corpusEngine "browser Flow tests with parity corpus"

        let project = Path.Combine(runDirectory, "project")
        let engine = createEngine project
        dispatch engine "define" [ "frontend", JsonValue.Create("flow"); "syntaxVersion", JsonValue.Create(2); "source", JsonValue.Create(flowSource false) ] |> ignore
        let firstRun = testAll engine "browser Flow tests"
        for word in libraryWords do
            dispatch engine "commit" [ "word", JsonValue.Create(word: string); "library", JsonValue.Create(true) ] |> ignore
        dispatch engine "commit" [] |> ignore

        let reloaded = createEngine project
        let names =
            (dispatch reloaded "words" []).["data"].["words"].AsArray()
            |> Seq.map (fun word -> word["name"].GetValue<string>())
            |> Set.ofSeq
        for name in [ "campfire.browser.supported?"; "campfire.browser.from-user-agent"; "campfire.browser.version-at-least?" ] do
            check (names.Contains name) $"fresh runtime loads {name}"
        let reloadedRun = testAll reloaded "reloaded browser Flow tests"
        equal firstRun reloadedRun "reloaded runtime keeps every attached browser test"

        let nativeUserAgents, nativeComparisons = nativeParity reloaded userAgents comparisons
        Console.WriteLine(
            $"PASS: {assertions} assertions; {corpusRun} Flow tests with the parity corpus ({userAgents.Length} User-Agents, {comparisons.Length} comparisons); {firstRun} committed tests (library closure) before and after reload; native O0/O2 parity on {nativeUserAgents} User-Agents and {nativeComparisons} comparisons.")
        0
