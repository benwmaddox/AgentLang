namespace AgentLang.Cli.Tests

open System
open System.Diagnostics
open System.IO
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
        let outputDirectory: DirectoryInfo = DirectoryInfo(AppContext.BaseDirectory.TrimEnd([| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |]))
        let configuration = outputDirectory.Parent.Name
        let assembly = Path.Combine(repositoryRoot (), "src", "AgentLang.Cli", "bin", configuration, "net9.0", "AgentLang.Cli.dll")
        if not (File.Exists assembly) then failwith $"CLI assembly was not built for {configuration}: {assembly}"
        assembly

    let private runCli (projectDirectory: string) (extraArguments: string list) (inputLines: string list) (timeoutMilliseconds: int) =
        let startInfo = ProcessStartInfo("dotnet")
        startInfo.UseShellExecute <- false
        startInfo.RedirectStandardInput <- true
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
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
        response["ok"].GetValue<bool>()

    let private responseErrorCode (line: string) =
        let response = JsonNode.Parse(line)
        try (response["error"]["code"]).GetValue<string>()
        with _ -> ""

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
                runCli project []
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
                runCli project [ "--eval"; "customer::discounted-balance(customer::new(kind = \"premium\", balance = 100.0))" ] [] 15000
            expectExit 0 customerReload "reload committed customer vocabulary in a fresh process"
            contains "90" customerReload.StandardOutput "fresh process calculates the premium customer balance as 90"

            let refined =
                runCli project []
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
                runCli project []
                    [ "MetersPerSecond::new(3.5)"; "KilometersPerHour::new(12.0)"; ":quit" ] 15000
            expectExit 0 speedReload "reload nominal speed wrappers in a fresh process"
            contains "MetersPerSecond" speedReload.StandardOutput "fresh process constructs the nominal meters-per-second type"
            contains "KilometersPerHour" speedReload.StandardOutput "fresh process constructs the nominal kilometers-per-hour type"

            let containers =
                runCli project []
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
                runCli project [ "--eval"; "container::option-default(option::some<Int>(90))" ] [] 15000
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

    [<EntryPoint>]
    let main _ =
        try
            group "CLI frontend selection and protocol authority" testFrontendSelection
            group "Flow parser-driven interactive buffering" testFlowInteractiveBuffering
            group "Flow incomplete EOF and malformed recovery" testIncompleteEofAndMalformedInput
            group "Flow file definition, CAS, and stable source identity" testFlowFileDefineCasAndSourceIdentity
            group "Flow type source inspection and durable reload" testTypeSourceInspectionAndReload
            group "migrated Flow examples and fresh-process durability" testMigratedFlowExamples
            group "explicit Stack REPL retains end blocks" testExplicitStackEndBlocks
            printfn "PASS %d groups, %d assertions" groups assertions
            0
        with ex ->
            eprintfn "FAIL after %d groups and %d assertions: %s" groups assertions ex.Message
            1
