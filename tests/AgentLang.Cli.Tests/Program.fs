namespace AgentLang.Cli.Tests

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open System.Threading.Tasks

module Program =
    type private Invocation =
        { ExitCode: int
          StandardOutput: string
          StandardError: string }

    let mutable private assertions = 0
    let mutable private groups = 0

    let private check (condition: bool) (message: string) =
        assertions <- assertions + 1
        if not condition then failwith message

    let private equal (expected: 'T) (actual: 'T) (message: string) =
        assertions <- assertions + 1
        if expected <> actual then failwith $"{message}: expected {expected}, got {actual}"

    let private contains (expected: string) (actual: string) (message: string) =
        assertions <- assertions + 1
        if not (actual.Contains(expected, StringComparison.Ordinal)) then
            failwith $"{message}: expected to find {expected} in output:\n{actual}"

    let private group name action =
        action ()
        groups <- groups + 1
        printfn "PASS %s" name

    let private repositoryRoot () : string =
        Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

    let private cliAssembly () : string =
        let explicitAssembly = Environment.GetEnvironmentVariable("AGENTLANG_TEST_CLI")
        let assembly =
            if not (String.IsNullOrWhiteSpace explicitAssembly) then Path.GetFullPath explicitAssembly
            else
                let outputDirectory: DirectoryInfo = DirectoryInfo(AppContext.BaseDirectory.TrimEnd([| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |]))
                let configuration = outputDirectory.Parent.Name
                Path.Combine(repositoryRoot (), "src", "AgentLang.Cli", "bin", configuration, "net9.0", "AgentLang.Cli.dll")
        if not (File.Exists assembly) then failwith $"CLI assembly was not built: {assembly}"
        assembly

    let private runCli (projectDirectory: string) (extraArguments: string list) (inputLines: string list) (timeoutMilliseconds: int) =
        let startInfo = ProcessStartInfo("dotnet")
        startInfo.UseShellExecute <- false
        startInfo.RedirectStandardInput <- true
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.StandardInputEncoding <- UTF8Encoding(false)
        startInfo.StandardOutputEncoding <- UTF8Encoding(false)
        startInfo.StandardErrorEncoding <- UTF8Encoding(false)
        startInfo.ArgumentList.Add(cliAssembly ())
        startInfo.ArgumentList.Add("--project")
        startInfo.ArgumentList.Add(projectDirectory)
        for argument in extraArguments do startInfo.ArgumentList.Add(argument)
        use child = new Process(StartInfo = startInfo)
        if not (child.Start()) then failwith "Could not start a fresh AgentLang CLI process."
        let stdout: Task<string> = child.StandardOutput.ReadToEndAsync()
        let stderr: Task<string> = child.StandardError.ReadToEndAsync()
        let outputTasks: Task array = [| stdout :> Task; stderr :> Task |]
        let outputDiagnostic (task: Task<string>) =
            if task.IsCompletedSuccessfully then task.Result
            elif task.IsFaulted then task.Exception.ToString()
            else "<stream read did not complete>"
        for line in inputLines do child.StandardInput.WriteLine(line)
        child.StandardInput.Close()
        if not (child.WaitForExit timeoutMilliseconds) then
            child.Kill(true)
            child.WaitForExit()
            let streamsDrained = Task.WaitAll(outputTasks, timeoutMilliseconds)
            failwith $"CLI did not exit within {timeoutMilliseconds} ms. Exit code after kill: {child.ExitCode}. Output reads drained: {streamsDrained}. stdout: {outputDiagnostic stdout}; stderr: {outputDiagnostic stderr}"
        if not (Task.WaitAll(outputTasks, timeoutMilliseconds)) then
            failwith $"CLI exited, but output reads did not complete within {timeoutMilliseconds} ms. Exit code: {child.ExitCode}. stdout: {outputDiagnostic stdout}; stderr: {outputDiagnostic stderr}"
        { ExitCode = child.ExitCode
          StandardOutput = stdout.Result
          StandardError = stderr.Result }

    let private withProject (action: string -> 'T) : 'T =
        let tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))
        let project = Path.GetFullPath(Path.Combine(tempRoot, $"agentlang-cli-tests-{Guid.NewGuid():N}"))
        let comparison = if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal
        if Path.GetDirectoryName(project) <> tempRoot
           || not (Path.GetFileName(project).StartsWith("agentlang-cli-tests-", comparison)) then
            failwith "Refusing to create a CLI test project outside the expected temporary directory prefix."
        Directory.CreateDirectory(project) |> ignore
        try action project
        finally
            if Directory.Exists project then Directory.Delete(project, true)

    let private responseLines (output: string) =
        output.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.toList

    let private responseOk (line: string) =
        let response = JsonNode.Parse(line)
        (response.["ok"]).GetValue<bool>()

    let private responseErrorCode (line: string) =
        let response = JsonNode.Parse(line)
        try (response["error"]["code"]).GetValue<string>()
        with _ -> ""

    let private humanDataObject (output: string) =
        let start = output.IndexOf("{", StringComparison.Ordinal)
        if start < 0 then failwith $"CLI output did not contain a JSON data object:\n{output}"
        let mutable depth = 0
        let mutable inString = false
        let mutable escaped = false
        let mutable finish = -1
        let mutable index = start
        while index < output.Length && finish < 0 do
            let ch = output[index]
            if inString then
                if escaped then escaped <- false
                elif ch = '\\' then escaped <- true
                elif ch = '"' then inString <- false
            elif ch = '"' then inString <- true
            elif ch = '{' then depth <- depth + 1
            elif ch = '}' then
                depth <- depth - 1
                if depth = 0 then finish <- index
            index <- index + 1
        if finish < 0 then failwith $"CLI output contained an incomplete JSON data object:\n{output}"
        match JsonNode.Parse(output.Substring(start, finish - start + 1)) with
        | :? JsonObject as data -> data
        | _ -> failwith $"CLI data payload was not a JSON object:\n{output}"

    let private stringArray (data: JsonNode) (field: string) =
        data[field].AsArray()
        |> Seq.map (fun item -> item.GetValue<string>())
        |> Seq.toList

    let private assertCompactData label (data: JsonNode) =
        check (data["compact"].GetValue<bool>()) $"{label} marks the payload as compact"
        let words = stringArray data "words"
        let constructs = stringArray data "constructs"
        check (not (List.isEmpty words)) $"{label} includes word names"
        check (not (List.isEmpty constructs)) $"{label} includes construct names"
        equal words (List.sort words) $"{label} sorts word names"
        equal constructs (List.sort constructs) $"{label} sorts construct names"
        check (List.contains "add" words) $"{label} includes the add word name"

    let private quotedPath (path: string) = "\"" + path.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""

    let private expectExit (expected: int) (invocation: Invocation) (label: string) =
        equal expected invocation.ExitCode $"{label} exit code; stderr: {invocation.StandardError}; output: {invocation.StandardOutput}"

    let private testFrontendSelection () =
        withProject (fun project ->
            let defaultFlow = runCli project [ "--eval"; "add(10, 20)" ] [] 10000
            expectExit 0 defaultFlow "default Flow --eval"
            contains "30" defaultFlow.StandardOutput "omitted frontend evaluates Flow syntax"

            let explicitStack = runCli project [ "--frontend"; "stack"; "--eval"; "10 20 add" ] [] 10000
            expectExit 0 explicitStack "explicit Stack --eval"
            contains "30" explicitStack.StandardOutput "explicit Stack evaluates RPN syntax"

            let requestDefault = runCli project [ "--jsonl" ] [ "{\"op\":\"eval\",\"code\":\"add(10, 20)\"}" ] 10000
            expectExit 0 requestDefault "JSONL request with omitted selector"
            let defaultLines = responseLines requestDefault.StandardOutput
            equal 1 defaultLines.Length "JSONL emits one response for one request"
            check (responseOk defaultLines.Head) "JSONL omitted selector uses the Flow default"
            contains "30" (JsonNode.Parse(defaultLines.Head).ToJsonString()) "JSONL request executes Flow source without CLI override"

            let requestDefaultRpn = runCli project [ "--jsonl" ] [ "{\"op\":\"eval\",\"code\":\"10 20 add\"}" ] 10000
            expectExit 0 requestDefaultRpn "JSONL Flow parse failure is a protocol response"
            let defaultRpnLines = responseLines requestDefaultRpn.StandardOutput
            equal 1 defaultRpnLines.Length "JSONL emits one response for malformed default-frontend source"
            check (not (responseOk defaultRpnLines.Head)) "RPN source without a selector is rejected by the Flow default"
            check
                ((responseErrorCode defaultRpnLines.Head).StartsWith("FLOW_", StringComparison.Ordinal))
                "omitted-selector RPN reports a Flow parser error instead of falling back to Stack"

            let requestStack = runCli project [ "--jsonl" ] [ "{\"op\":\"eval\",\"frontend\":\"stack\",\"code\":\"10 20 add\"}" ] 10000
            expectExit 0 requestStack "JSONL request with explicit Stack selector"
            let stackLine = responseLines requestStack.StandardOutput |> List.head
            check (responseOk stackLine) "JSONL honors an explicit request-level Stack selector"
            contains "30" (JsonNode.Parse(stackLine).ToJsonString()) "Stack JSONL response contains the RPN result"

            let requestFlow = runCli project [ "--jsonl" ] [ "{\"op\":\"eval\",\"frontend\":\"flow\",\"code\":\"add(2, 3)\"}" ] 10000
            expectExit 0 requestFlow "JSONL request with Flow selector"
            let flowLine = responseLines requestFlow.StandardOutput |> List.head
            check (responseOk flowLine) "JSONL honors an explicit request-level Flow selector"
            contains "5" (JsonNode.Parse(flowLine).ToJsonString()) "Flow JSONL response contains the Flow result"

            let oneShotRequest =
                runCli project [ "--request"; "{\"op\":\"eval\",\"frontend\":\"flow\",\"code\":\"add(7, 8)\"}" ] [] 10000
            expectExit 0 oneShotRequest "one-shot Flow request"
            contains "15" oneShotRequest.StandardOutput "one-shot request dispatch preserves its request-level frontend"

            let oneShotStackRequest =
                runCli project [ "--request"; "{\"op\":\"eval\",\"frontend\":\"stack\",\"code\":\"10 20 add\"}" ] [] 10000
            expectExit 0 oneShotStackRequest "one-shot Stack request"
            contains "30" oneShotStackRequest.StandardOutput "one-shot request preserves its explicit Stack frontend"

            let mixedProtocol = runCli project [ "--frontend"; "flow"; "--jsonl" ] [] 10000
            expectExit 2 mixedProtocol "reject frontend flag with JSONL"
            contains "request" mixedProtocol.StandardError "JSONL selector conflict explains per-request selection"

            let mixedRequest = runCli project [ "--frontend"; "stack"; "--request"; "{\"op\":\"eval\",\"frontend\":\"flow\",\"code\":\"add(2,3)\"}" ] [] 10000
            expectExit 2 mixedRequest "reject frontend flag with one-shot request"
            contains "request" mixedRequest.StandardError "one-shot request selector conflict is explicit")

    let private testWordsCommands () =
        withProject (fun project ->
            let compact = runCli project [] [ ":words --compact"; ":quit" ] 10000
            expectExit 0 compact "compact human REPL words request"
            let compactData = humanDataObject compact.StandardOutput
            assertCompactData "human REPL compact words request" compactData
            check (not (compact.StandardOutput.Contains("\"id\"", StringComparison.Ordinal))) "compact human REPL output omits stable IDs"
            check (not (compact.StandardOutput.Contains("\"inputs\"", StringComparison.Ordinal))) "compact human REPL output omits typed metadata"

            let full = runCli project [] [ ":words"; ":quit" ] 10000
            expectExit 0 full "full human REPL words request"
            let fullData = humanDataObject full.StandardOutput
            check (isNull fullData["compact"]) "the default words request does not set the compact marker"
            let fullWords = fullData["words"].AsArray()
            let add =
                fullWords
                |> Seq.tryFind (fun item -> item["name"].GetValue<string>() = "add")
                |> Option.defaultWith (fun () -> failwith "Full words output did not include add.")
            equal "primitive_add" (add["id"].GetValue<string>()) "full words output retains add's stable identity"
            check ((add["inputs"].AsArray()).Count > 0) "full words output retains typed input metadata"
            check ((add["outputs"].AsArray()).Count > 0) "full words output retains typed output metadata"

            let invalidAndRecovered =
                runCli project []
                    [ ":words --not-a-words-option"
                      "add(20, 22)"
                      ":words --compact --compact"
                      ":words --compact unexpected"
                      ":words --compact"
                      ":quit" ] 10000
            expectExit 0 invalidAndRecovered "invalid words options preserve the interactive session"
            equal 3 (Regex.Matches(invalidAndRecovered.StandardOutput, @"\[CLI_INVALID_COMMAND\]").Count) "unknown, duplicate, and extra words arguments are rejected"
            contains "42" invalidAndRecovered.StandardOutput "the REPL evaluates a later expression after a rejected words flag"
            check (not (invalidAndRecovered.StandardOutput.Contains("FLOW_", StringComparison.Ordinal))) "invalid words flags do not fall through to source evaluation"
            contains "\"compact\": true" invalidAndRecovered.StandardOutput "the REPL accepts a valid compact request after errors"

            let oneShotCompact =
                runCli project [ "--request"; "{\"op\":\"words\",\"compact\":true}" ] [] 10000
            expectExit 0 oneShotCompact "one-shot compact words request"
            assertCompactData "one-shot compact words request" (humanDataObject oneShotCompact.StandardOutput)

            let jsonLinesCompact =
                runCli project [ "--jsonl" ] [ "{\"op\":\"words\",\"compact\":true}" ] 10000
            expectExit 0 jsonLinesCompact "JSONL compact words request"
            let jsonLines = responseLines jsonLinesCompact.StandardOutput
            equal 1 jsonLines.Length "JSONL emits one compact words response"
            let response = JsonNode.Parse(jsonLines.Head)
            check ((response.["ok"]).GetValue<bool>()) "JSONL compact words request succeeds"
            assertCompactData "JSONL compact words request" response["data"])

    let private testFlowInteractiveBuffering () =
        withProject (fun project ->
            let input =
                [ "word demo.braces() -> String {"
                  "    effects none"
                  "    \"{ braces in this quoted string are data }\""
                  "}"
                  "test demo.braces/keeps-braces {"
                  "    demo::braces()"
                  "    => value \"{ braces in this quoted string are data }\""
                  "}"
                  "example demo.braces/keeps-braces {"
                  "    demo::braces()"
                  "    => \"{ braces in this quoted string are data }\""
                  "}"
                  "word demo.phrase() -> String {"
                  "    effects none"
                  "    // temp word in a comment is ordinary source text"
                  "    \"temp word inside a string is ordinary data\""
                  "}"
                  "temp word demo.temporary() -> Int {"
                  "    effects none"
                  "    7"
                  "}"
                  ":source demo.phrase"
                  ":eval demo::phrase()"
                  ":describe demo.temporary"
                  ":test demo.braces"
                  ":examples demo.braces"
                  ":quit" ]
            let result = runCli project [] input 10000
            expectExit 0 result "multiline pasted Flow declarations"
            contains "validated and staged" result.StandardOutput "pasted Flow definition validates and stages"
            contains "1/1 test(s) passed" result.StandardOutput "a later separate test declaration attaches to its Flow word"
            contains "1 example(s)." result.StandardOutput "a later separate example declaration attaches to its Flow word"
            contains "temp word in a comment is ordinary source text" result.StandardOutput "temporary preprocessing leaves comments untouched"
            contains "temp word inside a string is ordinary data" result.StandardOutput "temporary preprocessing leaves quoted values untouched"
            contains "temporary" result.StandardOutput "temp word definitions remain session-temporary"

            let expression = runCli project [] [ "if true {"; "    42"; "} else {"; "    0"; "}"; ":quit" ] 10000
            expectExit 0 expression "parser-buffered multiline Flow expression"
            contains "42" expression.StandardOutput "Flow expression continuation completes from parser syntax")

    let private testIncompleteEofAndMalformedInput () =
        withProject (fun project ->
            let incomplete =
                runCli project [] [ "word demo.never-finishes(value: Int) -> Int {" ] 5000
            expectExit 0 incomplete "incomplete Flow input at EOF"
            contains "FLOW_INCOMPLETE_INPUT" incomplete.StandardOutput "EOF reports the parser's incomplete-input diagnostic"

            let incompleteBeforeCommand =
                runCli project [] [ "word demo.interrupted(value: Int) -> Int {"; ":help"; ":quit" ] 5000
            expectExit 0 incompleteBeforeCommand "incomplete Flow input before a REPL command"
            contains "FLOW_INCOMPLETE_INPUT" incompleteBeforeCommand.StandardOutput "interrupted declaration reports its parser diagnostic"
            contains "Human REPL commands:" incompleteBeforeCommand.StandardOutput "the colon command remains available after an incomplete declaration"

            let malformed =
                runCli project [] [ "record Broken { field : Int; }"; ":quit" ] 5000
            expectExit 0 malformed "malformed Flow input returns to the prompt"
            contains "FLOW_" malformed.StandardOutput "malformed Flow syntax reports a structured parser diagnostic"
            check (not (malformed.StandardOutput.Contains("....>", StringComparison.Ordinal))) "malformed non-incomplete source does not request continuation")

    let private testRuntimeHelpCommandParityAndRecovery () =
        withProject (fun project ->
            let usage = runCli project [] [ ":help"; ":quit" ] 5000
            expectExit 0 usage "plain :help continues to show CLI usage"
            contains "Human REPL commands:" usage.StandardOutput "plain :help shows the CLI command list"
            contains ":help [TOPIC]" usage.StandardOutput "CLI usage advertises topic help"
            contains "Runtime topics: authoring, define, replacement, examples (default: authoring)" usage.StandardOutput "CLI usage distinguishes runtime topic help from bare CLI help"

            let repl = runCli project [] [ ":help \"define\""; ":quit" ] 5000
            expectExit 0 repl "quoted runtime help topic in the REPL"
            let request = runCli project [ "--request"; "{\"op\":\"help\",\"topic\":\"define\"}" ] [] 5000
            expectExit 0 request "equivalent JSON protocol help request"
            let replData = humanDataObject repl.StandardOutput
            let requestData = humanDataObject request.StandardOutput
            equal (requestData.ToJsonString()) (replData.ToJsonString()) "REPL topic help returns the same structured data as the JSON protocol"
            equal "define" (replData["topic"].GetValue<string>()) "the quote-aware parser forwards the selected topic"
            contains (requestData["topic"].GetValue<string>()) repl.StandardOutput "REPL output includes the same selected topic as the protocol request"

            let recovered = runCli project [] [ ":help \"unterminated"; ":help missing-topic"; ":help examples"; ":quit" ] 5000
            expectExit 0 recovered "malformed help input leaves the REPL usable"
            contains "A quoted command argument is missing its closing quote." recovered.StandardOutput "an unmatched topic quote reports a command error"
            contains "[CLI_INVALID_COMMAND]" recovered.StandardOutput "malformed topic errors use the CLI command error code"
            contains "[HELP_UNKNOWN_TOPIC]" recovered.StandardOutput "unknown help topics return a structured runtime error"
            let recoveredData = humanDataObject recovered.StandardOutput
            equal "examples" (recoveredData["topic"].GetValue<string>()) "a valid topic works after malformed help input")

    let private testFlowFileDefineCasAndSourceIdentity () =
        withProject (fun project ->
            let files = Path.Combine(project, "source files")
            Directory.CreateDirectory(files) |> ignore
            let initialPath = Path.Combine(files, "increment.agent")
            let initialSource =
                "word demo.increment(value: Int) -> Int {\n"
                + "    effects none\n"
                + "    add(value, 1)\n"
                + "}\n"
                + "test demo.increment/one {\n"
                + "    demo::increment(1)\n"
                + "    => value 2\n"
                + "}\n"
            File.WriteAllText(initialPath, initialSource)
            let initial =
                runCli project []
                    [ $":define {quotedPath initialPath}"; ":commit demo.increment"; ":describe demo.increment"; ":quit" ] 10000
            expectExit 0 initial "define and commit Flow source from a quoted path with spaces"
            contains "1 attached test(s) passed" initial.StandardOutput "Flow file definition commits after its attached test passes"
            let initialIdMatch = Regex.Match(initial.StandardOutput, "\"id\"\\s*:\\s*\"([^\"]+)\"")
            check initialIdMatch.Success "describe emits the stable word identity"
            let initialId = initialIdMatch.Groups[1].Value

            let replacementPath = Path.Combine(files, "increment replacement.agent")
            let replacementSource =
                "word demo.increment(value: Int) -> Int {\n"
                + "    effects none\n"
                + "    add(value, 2)\n"
                + "}\n"
                + "test demo.increment/two {\n"
                + "    demo::increment(1)\n"
                + "    => value 3\n"
                + "}\n"
            File.WriteAllText(replacementPath, replacementSource)
            let replaced =
                runCli project []
                    [ $":define {quotedPath replacementPath} --replace --expected-revision 0"
                      $":define {quotedPath replacementPath} --replace --expected-revision 1"
                      ":replace-word demo.increment"
                      ":describe demo.increment"
                      ":source demo.increment"
                      ":history demo.increment"
                      ":quit" ] 10000
            expectExit 0 replaced "CAS replacement from a quoted file path"
            contains "FLOW_BATCH_STALE_REVISION" replaced.StandardOutput "stale expected revision is rejected before replacement"
            contains "add(value, 2)" replaced.StandardOutput "the committed source query returns the replacement body"
            let ids = Regex.Matches(replaced.StandardOutput, "\"id\"\\s*:\\s*\"([^\"]+)\"")
            check (ids.Count > 0) "replacement description returns a word identity"
            equal initialId ids[ids.Count - 1].Groups[1].Value "replacement preserves the stable word identity"
            contains "add(value, 1)" replaced.StandardOutput "revision history retains the original implementation")

    let private testTypeSourceInspectionAndReload () =
        withProject (fun project ->
            let path = Path.Combine(project, "typed project.agent")
            let source =
                "type Email : String {\n"
                + "    // this comment belongs to the authored type declaration\n"
                + "}\n"
            File.WriteAllText(path, source)
            let defined =
                runCli project []
                    [ $":define {quotedPath path}"; ":commit Email"; ":source --type Email"; ":quit" ] 10000
            expectExit 0 defined "define and inspect a Flow type"
            let expectedType = "type Email : String {\n    // this comment belongs to the authored type declaration\n}"
            contains expectedType defined.StandardOutput "type source inspection preserves the exact authored member bytes"

            let reloaded = runCli project [ "--eval"; "Email::new(\"agent@example.test\")" ] [] 10000
            expectExit 0 reloaded "fresh CLI process reloads a committed Flow type"
            contains "agent@example.test" reloaded.StandardOutput "fresh process can construct a value of the committed nominal type")

    let private expectNoCliDiagnostics (invocation: Invocation) label =
        check
            (not (invocation.StandardOutput.Contains("error:", StringComparison.Ordinal))
             && not (Regex.IsMatch(invocation.StandardOutput, @"\[[A-Z][A-Z0-9_]+\]")))
            $"{label} completed without a CLI diagnostic:\n{invocation.StandardOutput}\n{invocation.StandardError}"

    let private testMigratedFlowExamples () =
        let example name = Path.Combine(repositoryRoot (), "examples", name)
        let customerDefinition = ":define " + quotedPath (example "customer.agent")
        let refinedDefinition = ":define " + quotedPath (example "refined-types.agent")
        let containersDefinition = ":define " + quotedPath (example "containers.agent")
        withProject (fun project ->
            let customer =
                runCli project [ "--syntax-version"; "2" ]
                    [ customerDefinition; ":test-all"
                      ":examples customer.discounted-balance"
                      ":commit customer.premium? --library"
                      ":commit customer.discounted-balance --library"
                      ":quit" ] 30000
            expectExit 0 customer "define and commit the Flow customer example"
            expectNoCliDiagnostics customer "customer example"
            contains "4/4 test(s) passed" customer.StandardOutput "customer example runs all premium and discount tests"
            contains "1 example(s)." customer.StandardOutput "customer example retains its authored example metadata"
            contains "selected candidates committed." customer.StandardOutput "customer words commit after library coverage passes"

            let customerReload =
                runCli project [ "--syntax-version"; "2"; "--eval"; "customer::discounted-balance(customer::new(kind = \"premium\", balance = 100.0))" ] [] 15000
            expectExit 0 customerReload "reload committed customer vocabulary in a fresh process"
            contains "90" customerReload.StandardOutput "fresh process calculates the premium customer balance as 90"

            let refined =
                runCli project [ "--syntax-version"; "2" ]
                    [ refinedDefinition; ":test-all"
                      ":examples email.valid?"
                      ":commit email.valid? --library"
                      ":commit Email"
                      ":commit MetersPerSecond"
                      ":commit KilometersPerHour"
                      ":quit" ] 30000
            expectExit 0 refined "define and commit refined Flow types"
            expectNoCliDiagnostics refined "refined type example"
            contains "10/10 test(s) passed" refined.StandardOutput "all existing and email validator cases pass before type publication"
            contains "1 example(s)." refined.StandardOutput "refined types retain their authored validator example metadata"
            contains "selected candidates committed." refined.StandardOutput "email validator and refined types commit"

            let speedReload =
                runCli project [ "--syntax-version"; "2" ]
                    [ "MetersPerSecond::new(3.5)"; "KilometersPerHour::new(12.0)"; ":quit" ] 15000
            expectExit 0 speedReload "reload nominal speed wrappers in a fresh process"
            contains "MetersPerSecond" speedReload.StandardOutput "fresh process constructs the nominal meters-per-second type"
            contains "KilometersPerHour" speedReload.StandardOutput "fresh process constructs the nominal kilometers-per-hour type"

            let containers =
                runCli project [ "--syntax-version"; "2" ]
                    [ containersDefinition; ":test-all"
                      ":commit container.option-default --library"
                      ":commit container.result-count --library"
                      ":commit container.envelope-email-count --library"
                      ":commit container.increment --library"
                      ":commit container.negative? --library"
                      ":commit container.ignore-int --library"
                      ":commit container.incremented-count --library"
                      ":commit container.negative-count --library"
                      ":commit container.visit-numbers --library"
                      ":quit" ] 45000
            expectExit 0 containers "define and commit the Flow containers example"
            expectNoCliDiagnostics containers "containers example"
            contains "25/25 test(s) passed" containers.StandardOutput "container edge cases and prior vocabulary cases pass"
            contains "selected candidates committed." containers.StandardOutput "container library words commit after their tests and coverage pass"

            let containerReload =
                runCli project [ "--syntax-version"; "2"; "--eval"; "container::option-default(option::some<Int>(90))" ] [] 15000
            expectExit 0 containerReload "reload committed generic container vocabulary"
            contains "90" containerReload.StandardOutput "fresh process executes a committed generic container word")

    let private testExplicitStackEndBlocks () =
        withProject (fun project ->
            let input =
                [ "word stack.increment : Int -> Int"
                  "    effects none"
                  "    1"
                  "    add"
                  "end"
                  ":eval 41 stack.increment"
                  ":quit" ]
            let result = runCli project [ "--frontend"; "stack" ] input 10000
            expectExit 0 result "explicit Stack REPL with legacy end blocks"
            contains "Definitions parsed, type checked, and staged." result.StandardOutput "legacy Stack definition stages"
            contains "42" result.StandardOutput "legacy end-terminated word executes in the explicit Stack frontend")

    let private testFlow2Cli () =
        withProject (fun project ->
            let input =
                [ "record Flag { field active: Bool; }"
                  "fn flag.active?(flag: Flag) -> Bool {"
                  "    doc \"Read a plain record property.\""
                  ""
                  "    flag.active == true"
                  "}"
                  "test flag.active?/true { flag::active?(flag::new(active = true)) => true }"
                  "test flag.active?/false { flag::active?(flag::new(active = false)) => false }"
                  ":test flag.active?"
                  ":commit flag.active? --library"
                  "temp fn scratch() -> Int { 42 }"
                  ":eval scratch()"
                  ":quit" ]
            let result = runCli project [ "--syntax-version"; "2" ] input 20000
            expectExit 0 result "Flow/2 human definitions and temporary functions"
            expectNoCliDiagnostics result "Flow/2 property and equality"
            contains "2/2 test(s) passed" result.StandardOutput "Flow/2 attached tests run"
            contains "selected candidates committed." result.StandardOutput "Flow/2 library commit passes"
            contains "42" result.StandardOutput "temporary fn is evaluated in the session"

            let unicodeValue = "Ω π 🌿 配置"
            let unicodeRequest = """{"op":"eval","syntaxVersion":2,"code":"\"Ω π 🌿 配置\""}"""
            let unicodeResult = runCli project [ "--jsonl" ] [ unicodeRequest ] 10000
            expectExit 0 unicodeResult "Flow/2 JSONL eval with a raw Unicode string literal"
            let unicodeLines = responseLines unicodeResult.StandardOutput
            equal 1 unicodeLines.Length "Unicode JSONL request receives one response"
            let unicodeResponse = JsonNode.Parse(unicodeLines.Head)
            check (unicodeResponse["ok"].GetValue<bool>()) "Unicode JSONL request succeeds"
            let unicodeStack = unicodeResponse.["data"].["stack"].AsArray()
            let renderedString = unicodeStack.[0].GetValue<string>()
            let exactValue = JsonSerializer.Deserialize<string>(renderedString)
            equal unicodeValue exactValue "Flow/2 string survives UTF-8 stdin and response rendering exactly"

            let reload = runCli project [ "--syntax-version"; "2"; "--eval"; "flag::active?(flag::new(active = false))" ] [] 15000
            expectExit 0 reload "Flow/2 committed function reload"
            contains "false" reload.StandardOutput "reloaded property function returns false"
            let stack2 = runCli project [ "--frontend"; "stack"; "--syntax-version"; "2"; "--eval"; "1" ] [] 10000
            expectExit 2 stack2 "Stack/2 is rejected"
            let jsonlFlag = runCli project [ "--syntax-version"; "2"; "--jsonl" ] [] 10000
            expectExit 2 jsonlFlag "JSONL requests own their version selector"
            let invalid = runCli project [ "--syntax-version"; "3"; "--eval"; "1" ] [] 10000
            expectExit 2 invalid "unsupported CLI syntax version is rejected")

    let private testFlow2Formatting () =
        withProject (fun project ->
            let source = "fn sample(value: Int) -> Int { doc \"Echo an integer.\" value }"
            let path = Path.Combine(project, "format-input.agent")
            File.WriteAllText(path, source)
            let result = runCli project [ "--syntax-version"; "2" ] [ ":format " + quotedPath path; ":quit" ] 10000
            expectExit 0 result "Flow/2 human formatter"
            expectNoCliDiagnostics result "Flow/2 formatting"
            contains "fn sample(value: Int) -> Int {" result.StandardOutput "canonical fn is printed"
            contains "doc \"Echo an integer.\"\n\n    value" (result.StandardOutput.Replace("\r\n", "\n")) "metadata has a blank line before code"
            equal source (File.ReadAllText path) "formatter does not overwrite input file"
            let absent = runCli project [ "--syntax-version"; "2"; "--eval"; "sample(1)" ] [] 10000
            expectExit 1 absent "formatter does not install a function")

    let private testFileSystemBoundary () =
        withProject (fun project ->
            let hostPath = Path.Combine(project, "shared.txt")
            let hostContents = "HOST Ω 🌿\r\n"
            File.WriteAllText(hostPath, hostContents, UTF8Encoding(false))
            let request (operation: string) (fields: (string * JsonNode) list) =
                let node = JsonObject()
                node["op"] <- JsonValue.Create(operation: string)
                node["frontend"] <- JsonValue.Create("flow")
                node["syntaxVersion"] <- JsonValue.Create(2)
                for name, value in fields do node[name] <- value
                node.ToJsonString()
            let eval code = request "eval" [ "code", JsonValue.Create(code: string) :> JsonNode ]
            let checkedResponses label (result: Invocation) =
                expectExit 0 result label
                responseLines result.StandardOutput |> List.map JsonNode.Parse
            let real =
                runCli project [ "--allow"; "fs.read,fs.write"; "--jsonl" ]
                    [ eval "file.read(\"shared.txt\")"
                      eval "file.write(\"actual.txt\", \"real Ω\")" ] 15000
                |> checkedResponses "normal execution uses real files"
            equal 2 real.Length "both real I/O requests return"
            for response in real do check ((response.["ok"]).GetValue<bool>()) "real I/O succeeds"
            let rendered = (real[0].["data"].["stack"].[0]).GetValue<string>()
            equal hostContents (JsonSerializer.Deserialize<string>(rendered)) "default read observes exact host contents"
            equal "real Ω" (File.ReadAllText(Path.Combine(project, "actual.txt"))) "default write reaches host filesystem"
            let presence =
                runCli project [ "--allow"; "fs.read"; "--jsonl" ]
                    [ eval "file.exists?(\"shared.txt\")"
                      eval "file.exists?(\"absent.txt\")"
                      eval "file.read(\"absent.txt\")" ] 15000
                |> checkedResponses "real existence and missing-read behavior"
            equal "true" ((presence[0].["data"].["stack"].[0]).GetValue<string>()) "real file existence is observed"
            equal "false" ((presence[1].["data"].["stack"].[0]).GetValue<string>()) "absent real file returns false"
            equal "EFFECT_FILE_NOT_FOUND" ((presence[2].["error"].["code"]).GetValue<string>()) "absent real read returns a structured error"

            let simulated =
                runCli project [ "--filesystem"; "virtual"; "--allow"; "fs.read,fs.write"; "--jsonl" ]
                    [ eval "file.write(\"simulated.txt\", \"fake\")"
                      eval "file.read(\"simulated.txt\")" ] 15000
                |> checkedResponses "explicit virtual session"
            for response in simulated do check ((response.["ok"]).GetValue<bool>()) "simulated I/O succeeds"
            check (not (File.Exists(Path.Combine(project, "simulated.txt")))) "simulation writes no physical file"

            let source = """fn fixture.read(path: String) -> String {
    effects fs.read
    doc "Read through the selected file provider."

    file.read(path)
}
test fixture.read/virtual-setup {
    file.write("shared.txt", "virtual-one")
    fixture.read("shared.txt")
    => "virtual-one"
}
test fixture.read/fresh-state {
    fixture.read("shared.txt")
    => error EFFECT_FILE_NOT_FOUND
}
"""
            let tested =
                runCli project [ "--allow"; "fs.read,fs.write"; "--jsonl" ]
                    [ request "define" [ "source", JsonValue.Create(source) :> JsonNode ]
                      request "test" [ "word", JsonValue.Create("fixture.read") :> JsonNode ]
                      request "commit" [ "word", JsonValue.Create("fixture.read") :> JsonNode
                                         "library", JsonValue.Create(true) :> JsonNode ]
                      eval "fixture.read(\"shared.txt\")" ] 20000
                |> checkedResponses "tests and library commit isolate I/O in a real session"
            equal 4 tested.Length "define, test, publication and normal invocation return"
            for response in tested do check ((response.["ok"]).GetValue<bool>()) "isolated tests and library publication pass"
            contains "2/2" ((tested[1].["text"]).GetValue<string>()) "both isolated test cases pass"
            equal hostContents (File.ReadAllText hostPath) "test setup and publication leave real file unchanged"
            let productionValue = (tested[3].["data"].["stack"].[0]).GetValue<string>()
            equal hostContents (JsonSerializer.Deserialize<string>(productionValue)) "production binding resumes actual file reads"

            let storePointerPath = Path.Combine(project, ".agentlang", "store", "CURRENT")
            check (File.Exists storePointerPath) "actual library commit creates the authoritative storage pointer"
            let storePointerBytes = File.ReadAllBytes storePointerPath
            let legacyExportPath = Path.Combine(project, "dictionary.agent")
            if not (File.Exists legacyExportPath) then
                // A current-authority fixture keeps the root legacy path populated
                // even if this commit mode did not materialize its export.
                File.WriteAllText(legacyExportPath, source, UTF8Encoding(false))
            let legacyExportExisted = File.Exists legacyExportPath
            let legacyExportBytes = File.ReadAllBytes legacyExportPath
            let protectedPaths =
                [ ".agentlang/store/CURRENT"
                  "./.agentlang/store/CURRENT"
                  "ordinary/../.agentlang/store/CURRENT"
                  ".agentlang"
                  ".agentlang/store"
                  "dictionary.agent"
                  "ordinary/../dictionary.agent"
                  ".AGENTLANG/store/CURRENT"
                  "DICTIONARY.AGENT" ]
            let protectedOperations (path: string) =
                let quotedPath = JsonSerializer.Serialize(path)
                [ eval $"file.read({quotedPath})"
                  eval $"file.write({quotedPath}, \"not-json\")"
                  eval $"file.exists?({quotedPath})" ]
            let protectedAttempts =
                runCli project [ "--allow"; "fs.read,fs.write"; "--test-allow"; "fs.read,fs.write"; "--jsonl" ]
                    (protectedPaths |> List.collect protectedOperations) 20000
                |> checkedResponses "real I/O rejects canonical AgentLang metadata paths"
            equal (protectedPaths.Length * 3) protectedAttempts.Length "each protected path is checked by read, write, and exists"
            for response in protectedAttempts do
                equal "EFFECT_FILE_PATH_RESERVED" ((response.["error"]["code"]).GetValue<string>()) "managed metadata paths have a structured reserved-path error"

            if OperatingSystem.IsWindows() then
                let ambiguousPaths =
                    [ ".agentlang./store/CURRENT"
                      ".agentlang /store/CURRENT"
                      ".agentlang:stream/store/CURRENT"
                      ".AGENTL~1/store/CURRENT"
                      "DICTIO~1.AGE" ]
                let ambiguousAttempts =
                    runCli project [ "--allow"; "fs.read,fs.write"; "--test-allow"; "fs.read,fs.write"; "--jsonl" ]
                        (ambiguousPaths |> List.collect protectedOperations) 20000
                    |> checkedResponses "real I/O rejects ambiguous Windows path spellings"
                equal (ambiguousPaths.Length * 3) ambiguousAttempts.Length "each ambiguous Windows path is checked by read, write, and exists"
                for response in ambiguousAttempts do
                    equal "EFFECT_FILE_PATH_INVALID" ((response.["error"]["code"]).GetValue<string>()) "ambiguous Windows path spelling has a structured invalid-path error"

            Directory.CreateDirectory(Path.Combine(project, ".agentlang-data")) |> ignore
            let siblingIo =
                runCli project [ "--allow"; "fs.read,fs.write"; "--test-allow"; "fs.read,fs.write"; "--jsonl" ]
                    [ eval "file.write(\".agentlang-data/ordinary.txt\", \"sibling Ω\")"
                      eval "file.read(\".agentlang-data/ordinary.txt\")"
                      eval "file.write(\"dictionary.agent.backup\", \"ordinary sibling\")"
                      eval "file.read(\"dictionary.agent.backup\")" ] 15000
                |> checkedResponses "ordinary files beside protected names remain available"
            for response in siblingIo do check ((response.["ok"]).GetValue<bool>()) "ordinary sibling file operation succeeds"
            let siblingValue (response: JsonNode) =
                let rendered = response.["data"].["stack"].[0].GetValue<string>()
                JsonSerializer.Deserialize<string>(rendered)
            equal "sibling Ω" (siblingValue siblingIo[1]) "reserved directory name uses a segment boundary"
            equal "ordinary sibling" (siblingValue siblingIo[3]) "reserved export name uses an exact root-name boundary"

            let freshReload =
                runCli project [ "--allow"; "fs.read"; "--test-allow"; "fs.read,fs.write"; "--jsonl" ] [ eval "fixture.read(\"shared.txt\")" ] 15000
                |> checkedResponses "fresh CLI reloads the committed library after reserved-path attempts"
            equal 1 freshReload.Length "fresh library reload returns one response"
            check ((freshReload[0].["ok"]).GetValue<bool>()) "fresh library reload succeeds"
            let reloadedValue = freshReload[0].["data"].["stack"].[0].GetValue<string>() |> fun rendered -> JsonSerializer.Deserialize<string>(rendered)
            equal hostContents reloadedValue "reloaded library keeps reading the original host file"
            equal storePointerBytes (File.ReadAllBytes storePointerPath) "language operations preserve authoritative CURRENT bytes"
            equal legacyExportExisted (File.Exists legacyExportPath) "language operations do not create or remove the legacy export"
            if legacyExportExisted then
                equal legacyExportBytes (File.ReadAllBytes legacyExportPath) "language operations preserve legacy export bytes"

            let readOnly =
                runCli project [ "--allow"; "fs.read"; "--test-allow"; "fs.read,fs.write"; "--jsonl" ]
                    [ request "test" [ "word", JsonValue.Create("fixture.read") :> JsonNode ]
                      eval "fixture.read(\"shared.txt\")"
                      eval "file.write(\"read-only-denied.txt\", \"blocked\")" ] 15000
                |> checkedResponses "isolated test grants are independent of real execution grants"
            check ((readOnly[0].["ok"]).GetValue<bool>()) "virtual setup can write with explicit test grants"
            check ((readOnly[1].["ok"]).GetValue<bool>()) "read-only production call succeeds after reload"
            check (not ((readOnly[2].["ok"]).GetValue<bool>())) "test write grant cannot authorize production write"
            equal "CAPABILITY_DENIED" ((readOnly[2].["error"].["code"]).GetValue<string>()) "production permission stays read-only"
            check (not (File.Exists(Path.Combine(project, "read-only-denied.txt")))) "test grant causes no real write"

            File.WriteAllBytes(Path.Combine(project, "invalid-utf8.txt"), [| 0xffuy |])
            let invalidIo =
                runCli project [ "--allow"; "fs.read,fs.write"; "--jsonl" ]
                    [ eval "file.read(\"invalid-utf8.txt\")"
                      eval "file.write(\"../escaped.txt\", \"blocked\")"
                      eval "file.write(\"shared.txt\", \"\\uD800\")" ] 15000
                |> checkedResponses "invalid paths and encodings fail structurally"
            equal "EFFECT_FILE_ENCODING" ((invalidIo[0].["error"].["code"]).GetValue<string>()) "invalid UTF-8 read fails"
            equal "EFFECT_FILE_PATH_INVALID" ((invalidIo[1].["error"].["code"]).GetValue<string>()) "relative escape is rejected"
            equal "FLOW_INVALID_STRING" ((invalidIo[2].["error"].["code"]).GetValue<string>()) "invalid Unicode is rejected before file writing"
            equal hostContents (File.ReadAllText hostPath) "invalid text never truncates an existing file"

            let deniedProject = Path.Combine(project, "denied-session")
            Directory.CreateDirectory(deniedProject) |> ignore
            let denied = runCli deniedProject [ "--jsonl" ] [ eval "file.write(\"denied.txt\", \"blocked\")" ] 10000
            contains "CAPABILITY_DENIED" denied.StandardOutput "normal I/O requires host capability"
            check (not (File.Exists(Path.Combine(deniedProject, "denied.txt")))) "denied effect performs no write"
            let deniedTest = runCli deniedProject [ "--jsonl" ]
                                 [ request "define" [ "source", JsonValue.Create(source) :> JsonNode ]
                                   request "test" [ "word", JsonValue.Create("fixture.read") :> JsonNode ] ] 15000
            contains "CAPABILITY_DENIED" (deniedTest.StandardOutput + deniedTest.StandardError) "test mode does not grant I/O capabilities"
            equal hostContents (File.ReadAllText hostPath) "denied tests preserve host files"
            let invalid = runCli project [ "--filesystem"; "unknown" ] [] 10000
            expectExit 2 invalid "unknown filesystem mode is rejected")

    [<EntryPoint>]
    let main _ =
        try
            group "CLI frontend selection and protocol authority" testFrontendSelection
            group "compact and full words CLI requests" testWordsCommands
            group "Flow parser-driven interactive buffering" testFlowInteractiveBuffering
            group "Flow incomplete EOF and malformed recovery" testIncompleteEofAndMalformedInput
            group "runtime help topics match protocol and recover from malformed input" testRuntimeHelpCommandParityAndRecovery
            group "Flow file definition, CAS, and stable source identity" testFlowFileDefineCasAndSourceIdentity
            group "Flow type source inspection and durable reload" testTypeSourceInspectionAndReload
            group "migrated Flow examples and fresh-process durability" testMigratedFlowExamples
            group "explicit Stack REPL retains end blocks" testExplicitStackEndBlocks
            group "Flow/2 CLI selection, properties, temporary functions and reload" testFlow2Cli
            group "Flow/2 human formatting is an explicit nonmutating operation" testFlow2Formatting
            group "real filesystem default and isolated test providers" testFileSystemBoundary
            printfn "PASS %d groups, %d assertions" groups assertions
            0
        with ex ->
            eprintfn "FAIL after %d groups and %d assertions: %s" groups assertions ex.Message
            1
