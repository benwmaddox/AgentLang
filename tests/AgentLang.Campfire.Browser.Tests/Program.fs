namespace AgentLang.Campfire.Browser.Tests

open System
open System.Globalization
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open AgentLang
open AgentLang.Campfire.Browser
open AgentLang.Campfire.Browser.Host

module Program =
    let mutable private assertions = 0

    let private check condition message =
        assertions <- assertions + 1
        if not condition then failwith message

    let private equal expected actual message =
        assertions <- assertions + 1
        if expected <> actual then
            let expectedText = sprintf "%A" expected
            let actualText = sprintf "%A" actual
            failwith $"{message}: expected {expectedText}, got {actualText}"

    let private jstr (value: string) = JsonValue.Create(value) :> JsonNode
    let private jint (value: int) = JsonValue.Create(value) :> JsonNode

    let private arguments (values: (string * JsonNode) list) =
        let result = JsonObject()
        for name, value in values do
            result[name] <- if isNull value then null else value.DeepClone()
        result

    let private dispatch (engine: Runtime.Engine) operation values =
        engine.Dispatch(operation, arguments values)

    let private expectOk label (response: JsonObject) =
        if not (response.["ok"].GetValue<bool>()) then
            let code =
                try response.["error"].["code"].GetValue<string>()
                with _ -> "unknown"
            let message =
                try response.["error"].["message"].GetValue<string>()
                with _ -> response.ToJsonString()
            failwith $"{label}: expected success, got {code}: {message}"
        response

    let private expectAllPassed label (response: JsonObject) =
        response |> expectOk label |> ignore
        let results = response.["data"].["results"].AsArray()
        for result in results do
            if not (result.["passed"].GetValue<bool>()) then
                let word = result.["word"].GetValue<string>()
                let name = result.["name"].GetValue<string>()
                failwith $"{label}: test {word}/{name} failed: {result.ToJsonString()}"
        results

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

    let private vectorPath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "campfire_user_agents.json")

    let private sourcePath relative = Path.Combine(repositoryRoot, "applications", "campfire", relative)

    let private readApplicationFlowSource () =
        [ "browser-types.flow"; "browser-policy.flow"; "browser-tests.flow" ]
        |> List.map (sourcePath >> File.ReadAllText)
        |> String.concat "\n\n"

    let private readRealProviderTests () =
        Path.Combine(repositoryRoot, "tests", "AgentLang.Campfire.Browser.Tests", "real-browser-tests.flow")
        |> File.ReadAllText

    let private defineFlow (engine: Runtime.Engine) source =
        dispatch engine "define"
            [ "frontend", jstr "flow"
              "syntaxVersion", jint 2
              "source", jstr source ]
        |> expectOk "define browser types, policy, and tests"

    let private createEngine project hostOperations =
        Runtime.Engine(
            project,
            Set.empty,
            "2026-10-11T12:00:00Z",
            fileSystemMode = FileSystemMode.Virtual,
            testCapabilities = Set.empty,
            hostOperations = hostOperations)

    let private expectedFamily (browser: string option) =
        match browser with
        | Some browser when browser.Equals("Safari", StringComparison.OrdinalIgnoreCase) -> BrowserFamily.Safari
        | Some browser when browser.Equals("Chrome", StringComparison.OrdinalIgnoreCase) -> BrowserFamily.Chrome
        | Some browser when browser.Equals("Firefox", StringComparison.OrdinalIgnoreCase) -> BrowserFamily.Firefox
        | Some browser when browser.Equals("Opera", StringComparison.OrdinalIgnoreCase) -> BrowserFamily.Opera
        | Some browser when browser.Equals("Internet Explorer", StringComparison.OrdinalIgnoreCase) -> BrowserFamily.InternetExplorer
        | _ -> BrowserFamily.Other

    let private familyCase = function
        | BrowserFamily.Safari -> "safari"
        | BrowserFamily.Chrome -> "chrome"
        | BrowserFamily.Firefox -> "firefox"
        | BrowserFamily.Opera -> "opera"
        | BrowserFamily.InternetExplorer -> "internet-explorer"
        | BrowserFamily.Other -> "other"

    let private optionalString (value: JsonElement) =
        if value.ValueKind = JsonValueKind.String then
            let text = value.GetString()
            if String.IsNullOrWhiteSpace(text) then None else Some text
        else
            None

    let private rawStringOption (value: JsonElement) =
        if value.ValueKind = JsonValueKind.String then Some(value.GetString()) else None

    let private browserVectorCases () =
        use document = JsonDocument.Parse(File.ReadAllText vectorPath)
        let cases = document.RootElement.GetProperty("user_agents").EnumerateArray() |> Seq.toArray
        equal 385 cases.Length "pinned User-Agent vector count"

        for case in cases do
            let userAgent = rawStringOption (case.GetProperty("ua"))
            let expectedBrowser = optionalString (case.GetProperty("browser"))
            let expectedVersion = optionalString (case.GetProperty("version"))
            let expectedBot = case.GetProperty("bot").GetBoolean()
            let actual = BrowserInfo.normalize userAgent
            let label =
                match userAgent with
                | None -> "<null>"
                | Some "" -> "<empty>"
                | Some value -> value
            equal (expectedFamily expectedBrowser) actual.Family $"{label}: browser family"
            equal expectedVersion actual.Version $"{label}: browser version"
            equal expectedBot actual.IsBot $"{label}: bot flag"
            equal
                (RecordValue(
                    "BrowserInfo",
                    Map.ofList
                        [ "family", EnumValue("BrowserFamily", familyCase (expectedFamily expectedBrowser))
                          "version", OptionValue(TString, Option.map StringValue expectedVersion)
                          "is-bot", BoolValue expectedBot ]))
                (BrowserHost.toFlowValue actual)
                $"{label}: Flow BrowserInfo encoding"

        cases.Length

    let private versionVectorCases () =
        use document = JsonDocument.Parse(File.ReadAllText vectorPath)
        let root = document.RootElement
        let versions = root.GetProperty("versions").EnumerateArray() |> Seq.toArray
        let comparisons = root.GetProperty("comparisons").EnumerateArray() |> Seq.toArray
        equal 44 versions.Length "pinned Ruby-version vector count"
        equal 352 comparisons.Length "pinned Ruby-version comparison count"

        for vector in versions do
            let value = vector.GetProperty("string").GetString()
            equal 0 (RubyVersion.compare value value) $"Ruby Version equality for '{value}'"
            check (RubyVersion.isAtLeast value value) $"Ruby Version inclusive equality for '{value}'"

        for vector in comparisons do
            let left = vector.GetProperty("a").GetString()
            let right = vector.GetProperty("b").GetString()
            let expected = vector.GetProperty("cmp").GetInt32()
            let actual = Math.Sign(RubyVersion.compare left right)
            equal (Math.Sign expected) actual $"Ruby Version comparison '{left}' vs '{right}'"
            equal (expected >= 0) (RubyVersion.isAtLeast left right) $"Ruby Version threshold '{left}' vs '{right}'"

        versions.Length, comparisons.Length

    let private providerEnvironment allowTestProvider =
        let mutable productionCalls = 0
        let mutable testFactoryCalls = 0
        let mutable testInvocationCalls = 0
        let mutable testDisposeCalls = 0

        let productionProvider =
            { OperationIds = BrowserHost.operationIds
              Invoke = fun _ _ ->
                  productionCalls <- productionCalls + 1
                  Error HostOperationFailure.InvocationFailed
              Dispose = ignore }

        let createTestProvider () =
            testFactoryCalls <- testFactoryCalls + 1
            if not allowTestProvider then
                Error HostOperationFailure.TestProviderUnavailable
            else
                let provider = BrowserHost.createProvider ()
                Ok
                    { provider with
                        Invoke = fun operationId values ->
                            testInvocationCalls <- testInvocationCalls + 1
                            provider.Invoke operationId values
                        Dispose = fun () -> testDisposeCalls <- testDisposeCalls + 1 }

        let environment = BrowserHost.createEnvironment productionProvider createTestProvider
        environment,
        (fun () -> productionCalls),
        (fun () -> testFactoryCalls),
        (fun () -> testInvocationCalls),
        (fun () -> testDisposeCalls)

    let private flowRuntimeCases () =
        Directory.CreateDirectory runDirectory |> ignore
        let tempDirectory = Path.Combine(runDirectory, "temp")
        Directory.CreateDirectory tempDirectory |> ignore
        Environment.SetEnvironmentVariable("TMP", tempDirectory)
        Environment.SetEnvironmentVariable("TEMP", tempDirectory)

        let scopedProject = Path.Combine(runDirectory, "scoped-project")
        Directory.CreateDirectory scopedProject |> ignore
        let scopedEnvironment, scopedProductionCalls, scopedTestFactoryCalls, scopedTestInvocationCalls, scopedTestDisposeCalls =
            providerEnvironment false
        let scopedEngine = createEngine scopedProject scopedEnvironment
        defineFlow scopedEngine (readApplicationFlowSource ()) |> ignore

        let firstScopedRun = dispatch scopedEngine "test-all" [] |> expectAllPassed "scoped browser Flow tests"
        equal 18 firstScopedRun.Count "scoped application policy test count"
        equal 0 (scopedProductionCalls ()) "scoped tests never invoke the production browser provider"
        equal 0 (scopedTestFactoryCalls ()) "scoped tests do not request a host-operation provider"
        equal 0 (scopedTestInvocationCalls ()) "scoped tests use only their explicit Flow override"
        equal 0 (scopedTestDisposeCalls ()) "scoped tests construct no provider to dispose"

        let manifestEntry = BrowserHost.manifest.[BrowserHost.versionAtLeastId]
        let noEffects: Set<string> = Set.empty
        equal noEffects manifestEntry.Effects "version comparison has no declared effects"
        equal [ TString; TString ] manifestEntry.Inputs "version comparison input contract"
        equal [ TBool ] manifestEntry.Outputs "version comparison output contract"

        dispatch scopedEngine "commit"
            [ "word", jstr "campfire.browser.supported?"
              "library", JsonValue.Create(true) :> JsonNode ]
        |> expectOk "qualify browser policy as a library"
        |> ignore

        let reloaded = createEngine scopedProject scopedEnvironment
        let words = dispatch reloaded "words" [] |> expectOk "read freshly reloaded browser library"
        let names =
            words.["data"].["words"].AsArray()
            |> Seq.map (fun word -> word.["name"].GetValue<string>())
            |> Set.ofSeq
        check (names.Contains "campfire.browser.supported?") "fresh runtime loads the qualified browser policy"

        let reloadedRun = dispatch reloaded "test-all" [] |> expectAllPassed "fresh-runtime scoped browser tests"
        equal 18 reloadedRun.Count "fresh runtime retains all scoped application policy tests"
        equal 0 (scopedProductionCalls ()) "fresh scoped tests never invoke production provider"
        equal 0 (scopedTestFactoryCalls ()) "fresh scoped tests still request no provider"
        equal 0 (scopedTestInvocationCalls ()) "fresh scoped tests still use the Flow override"
        equal 0 (scopedTestDisposeCalls ()) "fresh scoped tests still construct no provider"

        let liveProject = Path.Combine(runDirectory, "live-provider-project")
        Directory.CreateDirectory liveProject |> ignore
        let liveEnvironment, liveProductionCalls, liveTestFactoryCalls, liveTestInvocationCalls, liveTestDisposeCalls =
            providerEnvironment true
        let liveEngine = createEngine liveProject liveEnvironment
        let liveSource =
            [ readApplicationFlowSource (); readRealProviderTests () ]
            |> String.concat "\n\n"
        defineFlow liveEngine liveSource |> ignore
        let liveRun = dispatch liveEngine "test-all" [] |> expectAllPassed "live browser provider tests"
        equal 22 liveRun.Count "scoped and live-provider attached test count"
        equal 0 (liveProductionCalls ()) "live tests never invoke the production browser provider"
        equal 4 (liveTestFactoryCalls ()) "only four live tests construct a test provider"
        equal 4 (liveTestInvocationCalls ()) "each live test invokes the checked host operation once"
        equal 4 (liveTestDisposeCalls ()) "each live test provider is disposed after its case"

    [<EntryPoint>]
    let main _ =
        let browserCases = browserVectorCases ()
        let versionCounts = versionVectorCases ()
        flowRuntimeCases ()
        Console.WriteLine(
            $"PASS: {assertions} assertions; {browserCases} pinned User-Agent cases; {fst versionCounts} pinned versions; {snd versionCounts} pinned Ruby comparisons; scoped attached tests passed before and after library reload; live provider tests passed separately.")
        0
