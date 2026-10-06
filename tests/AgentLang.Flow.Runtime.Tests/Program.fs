namespace AgentLang.Flow.Runtime.Tests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open AgentLang

module Program =
    let mutable private assertions = 0

    let private check condition message =
        assertions <- assertions + 1
        if not condition then failwith message

    let private equal expected actual message =
        assertions <- assertions + 1
        if expected <> actual then failwith $"{message}: expected {expected}, got {actual}"

    let private jstr (value: string) = JsonValue.Create(value) :> JsonNode
    let private jint (value: int) = JsonValue.Create(value) :> JsonNode
    let private jbool (value: bool) = JsonValue.Create(value) :> JsonNode
    let private stringValue (node: JsonNode) = node.GetValue<string>()
    let private boolValue (node: JsonNode) = node.GetValue<bool>()

    let private strings values =
        let result = JsonArray()
        for value in values do result.Add(jstr value)
        result :> JsonNode

    let private digest (text: string) =
        UTF8Encoding(false, true).GetBytes text
        |> SHA256.HashData
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    let private arguments (values: (string * JsonNode) list) =
        let result = JsonObject()
        for name, value in values do
            result.[name] <- if isNull value then null else value.DeepClone()
        result

    let private dispatch (engine: Runtime.Engine) operation values =
        engine.Dispatch(operation, arguments values)

    let private succeeded (response: JsonObject) = boolValue (response.["ok"])

    let private expectOk label (response: JsonObject) =
        if not (succeeded response) then
            let code =
                try stringValue (response.["error"].["code"])
                with _ -> "unknown"
            let message =
                try stringValue (response.["error"].["message"])
                with _ -> response.ToJsonString()
            failwith $"{label}: expected success, got {code}: {message}"
        response

    let private expectError expectedCode (response: JsonObject) =
        check (not (succeeded response)) $"expected {expectedCode}, got success: {response.ToJsonString()}"
        let actualCode = stringValue (response.["error"].["code"])
        equal expectedCode actualCode "runtime diagnostic code"
        response

    let private errorCode (response: JsonObject) = stringValue (response.["error"].["code"])

    let private newRoot () =
        let path = Path.Combine(Path.GetTempPath(), $"agentlang-flow-runtime-{Guid.NewGuid():N}")
        Directory.CreateDirectory path |> ignore
        path

    let private jsonArrayStrings (node: JsonNode) =
        node.AsArray() |> Seq.map stringValue |> Seq.toList

    let private flowWordSource =
        "word durable.increment(value: Int) -> Int {\n"
        + "    effects none\n"
        + "    add(value, 1)\n"
        + "}"

    let private flowTestSource =
        "test durable.increment/basic {\n"
        + "    durable::increment(41)\n"
        + "    => value durable::increment(41)\n"
        + "}"

    let private flowExampleSource =
        "example durable.increment/one {\n"
        + "    durable::increment(41)\n"
        + "    => 42\n"
        + "}"

    let private defineFlow (engine: Runtime.Engine) source tests examples extra =
        let fields =
            [ "frontend", jstr "flow"
              "source", jstr source ]
            @ (if List.isEmpty tests then [] else [ "tests", strings tests ])
            @ (if List.isEmpty examples then [] else [ "examples", strings examples ])
            @ extra
        dispatch engine "define" fields

    let private commit (engine: Runtime.Engine) operation name extra =
        dispatch engine operation ([ "word", jstr name ] @ extra)

    let private evalFlow (engine: Runtime.Engine) code =
        dispatch engine "eval" [ "frontend", jstr "flow"; "code", jstr code ]

    let private cliEval project code =
        let outputDirectory = DirectoryInfo(AppContext.BaseDirectory.TrimEnd([| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |]))
        let configuration = outputDirectory.Parent.Name
        let repositoryRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
        let cliAssembly = Path.Combine(repositoryRoot, "src", "AgentLang.Cli", "bin", configuration, "net9.0", "AgentLang.Cli.dll")
        check (File.Exists cliAssembly) $"CLI assembly exists for the {configuration} integration-test build"
        let startInfo = ProcessStartInfo("dotnet")
        startInfo.UseShellExecute <- false
        startInfo.RedirectStandardInput <- true
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.ArgumentList.Add cliAssembly
        startInfo.ArgumentList.Add "--project"
        startInfo.ArgumentList.Add project
        startInfo.ArgumentList.Add "--jsonl"
        use childProcess = new Process(StartInfo = startInfo)
        if not (childProcess.Start()) then failwith "Could not start the AgentLang CLI integration process."
        let request = JsonObject()
        request.["op"] <- jstr "eval"
        request.["frontend"] <- jstr "flow"
        request.["code"] <- jstr code
        childProcess.StandardInput.WriteLine(request.ToJsonString())
        childProcess.StandardInput.Close()
        if not (childProcess.WaitForExit 30000) then
            childProcess.Kill(true)
            failwith "The AgentLang CLI did not exit after its JSON-lines input closed."
        let output = childProcess.StandardOutput.ReadToEnd()
        let error = childProcess.StandardError.ReadToEnd()
        check (childProcess.ExitCode = 0) $"fresh-process Flow CLI exits successfully: {error}"
        let line =
            output.Split([| "\r\n"; "\n" |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.tryHead
            |> Option.defaultWith (fun () -> failwith $"The AgentLang CLI returned no JSON response. stderr: {error}")
        JsonNode.Parse(line).AsObject()

    let private findWord (response: JsonObject) name =
        (response.["data"].["words"]).AsArray()
        |> Seq.find (fun entry -> stringValue (entry.["name"]) = name)

    let private getWordId (engine: Runtime.Engine) name =
        let words = dispatch engine "words" [] |> expectOk "words"
        let word = findWord words name
        stringValue (word.["id"])

    let private assertAllPassed expectedCount (response: JsonObject) =
        let results = (response.["data"].["results"]).AsArray()
        equal expectedCount results.Count "test result count"
        for item in results do
            let name = stringValue (item.["name"])
            check (boolValue (item.["passed"])) $"test {name} passed"

    let private testExplicitFrontendAndDurableReload root =
        let project = Path.Combine(root, "durable-reload")
        let engine = Runtime.Engine(project, Set.empty, "2030-01-02T03:04:05Z")
        let defined = defineFlow engine flowWordSource [ flowTestSource ] [ flowExampleSource ] []
        expectOk "define an explicit Flow word with authored attachments" defined |> ignore
        equal "flow" (stringValue (defined.["data"].["frontend"])) "definition reports selected frontend"
        equal [ "basic" ] (jsonArrayStrings defined.["data"].["tests"]) "definition reports test case names"
        equal [ "one" ] (jsonArrayStrings defined.["data"].["examples"]) "definition reports example case names"

        let wordId = getWordId engine "durable.increment"
        let committed = commit engine "commit" "durable.increment" [] |> expectOk "commit Flow candidate"
        check (not (isNull committed.["data"])) "Flow commit returns its ordinary commit result"

        let stackWrapperSource =
            "word durable.stack_wrapper : Int -> Int\n"
            + "    effects none\n"
            + "    durable.increment\n"
            + "end\n"
            + "\n"
            + "test durable.stack_wrapper/basic\n"
            + "    9 durable.stack_wrapper\n"
            + "    expect 10\n"
            + "end\n"
            + "\n"
            + "example durable.stack_wrapper/basic\n"
            + "    4 durable.stack_wrapper\n"
            + "    => 5\n"
            + "end\n"
        dispatch engine "define" [ "source", jstr stackWrapperSource ]
        |> expectOk "define a Stack wrapper and cases after committing Flow"
        |> ignore
        let stackWrapperId = getWordId engine "durable.stack_wrapper"
        commit engine "commit" "durable.stack_wrapper" []
        |> expectOk "commit a Stack caller of the Flow owner"
        |> ignore
        assertAllPassed 1 (dispatch engine "test" [ "word", jstr "durable.stack_wrapper" ] |> expectOk "run Stack case calling Flow")
        let stackExampleResult = dispatch engine "example" [ "word", jstr "durable.stack_wrapper"; "caseName", jstr "basic" ] |> expectOk "run Stack example calling Flow"
        check (boolValue (stackExampleResult.["data"].["results"].[0].["passed"])) "Stack example calling Flow passes before reload"

        let store = Storage.create project
        let loaded = Storage.load store
        let manifest =
            match loaded with
            | Ok snapshot -> snapshot.Manifest |> Option.defaultWith (fun () -> failwith "committed Flow manifest is missing")
            | Error problem -> failwith $"load Flow manifest: {problem.Code}: {problem.Message}"
        equal 2 manifest.FormatVersion "Flow publication writes manifest v2"
        let revision = manifest.Revisions |> List.find (fun item -> item.WordId = wordId)
        equal { Frontend = SourceFrontend.Flow; Version = 1 } revision.SourceFormat "revision records Flow/1"
        equal 1 revision.Revision "initial Flow revision is revision one"
        equal (digest flowWordSource) revision.Definition.Hash "definition SourceRef hashes the exact authored UTF-8 bytes"
        equal [ digest flowTestSource ] (revision.Tests |> List.map _.Hash) "test SourceRef hashes its exact authored UTF-8 bytes"
        equal [ digest flowExampleSource ] (revision.Examples |> List.map _.Hash) "example SourceRef hashes its exact authored UTF-8 bytes"
        equal 1 (revision.CallBindings |> List.filter (fun binding -> binding.BodyRole = StoredCallBodyRole.Definition) |> List.length) "word body call bindings use the Definition role"
        equal 2 (revision.CallBindings |> List.filter (fun binding -> binding.BodyRole = StoredCallBodyRole.Actual) |> List.length) "actual test/example bodies use the Actual role"
        equal 1 (revision.CallBindings |> List.filter (fun binding -> binding.BodyRole = StoredCallBodyRole.ExpectedExpression) |> List.length) "pure expectation calls use the ExpectedExpression role"
        equal flowWordSource (Storage.readSource store revision.Definition |> Result.defaultWith (fun problem -> failwith problem.Message)) "authored definition object survives exactly"
        equal [ flowTestSource ] (revision.Tests |> List.map (fun reference -> Storage.readSource store reference |> Result.defaultWith (fun problem -> failwith problem.Message))) "authored test object survives exactly"
        equal [ flowExampleSource ] (revision.Examples |> List.map (fun reference -> Storage.readSource store reference |> Result.defaultWith (fun problem -> failwith problem.Message))) "authored example object survives exactly"
        equal 4 revision.CallBindings.Length "definition, actual, expected and example call sites are all persisted"
        check (revision.CallBindings |> List.exists (fun binding -> binding.BodyRole = StoredCallBodyRole.ExpectedExpression)) "expected-expression call binding is persisted"
        let stackWrapperRevision = manifest.Revisions |> List.find (fun item -> item.WordId = stackWrapperId)
        equal { Frontend = SourceFrontend.Stack; Version = 1 } stackWrapperRevision.SourceFormat "Stack caller remains Stack/1 beside Flow"
        check (File.ReadAllText(Path.Combine(project, "dictionary.agent")).Contains("// frontend: flow/1", StringComparison.Ordinal)) "mixed-language export marks the Flow source block"
        check (File.ReadAllText(Path.Combine(project, "dictionary.agent")).Contains(flowWordSource, StringComparison.Ordinal)) "export contains authored Flow instead of lowered RPN"

        let reloaded = Runtime.Engine(project, Set.empty, "2030-01-02T03:04:05Z")
        equal wordId (getWordId reloaded "durable.increment") "fresh Engine reload preserves WordId"
        let source = dispatch reloaded "source" [ "word", jstr "durable.increment" ] |> expectOk "source after reload"
        equal flowWordSource (stringValue (source.["data"])) "source query exposes exact authored Flow definition text"
        let described = dispatch reloaded "describe" [ "word", jstr "durable.increment" ] |> expectOk "describe after reload"
        equal "value" (stringValue (described.["data"].["parameters"].[0].["name"])) "describe exposes authored parameter names"
        equal "Int" (stringValue (described.["data"].["parameters"].[0].["type"])) "describe exposes authored parameter type"
        equal [ "basic" ] (jsonArrayStrings (dispatch reloaded "tests" [ "word", jstr "durable.increment" ] |> expectOk "tests after reload" |> fun result -> result.["data"])) "tests remain attached after reload"
        equal [ "one" ] (jsonArrayStrings (dispatch reloaded "examples" [ "word", jstr "durable.increment" ] |> expectOk "examples after reload" |> fun result -> result.["data"])) "examples remain attached after reload"
        assertAllPassed 1 (dispatch reloaded "test" [ "word", jstr "durable.increment" ] |> expectOk "run reloaded Flow test")
        let evaluated = evalFlow reloaded "durable::increment(9)" |> expectOk "evaluate a reloaded Flow word"
        equal "10" (stringValue (evaluated.["data"].["stack"].[0])) "Flow eval invokes the verified durable body"
        let exampleResult = dispatch reloaded "example" [ "word", jstr "durable.increment"; "caseName", jstr "one" ] |> expectOk "run a reloaded Flow example"
        check (boolValue (exampleResult.["data"].["results"].[0].["passed"])) "reloaded example matches its expectation"
        equal flowExampleSource (stringValue (exampleResult.["data"].["results"].[0].["source"])) "example response preserves exact authored case source"
        let cliResponse = cliEval project "durable::increment(9)" |> expectOk "fresh-process Flow CLI evaluation"
        equal "10" (stringValue (cliResponse.["data"].["stack"].[0])) "fresh-process CLI loads the authored Flow program"
        assertAllPassed 1 (dispatch reloaded "test" [ "word", jstr "durable.stack_wrapper" ] |> expectOk "run reloaded Stack case calling Flow")
        let reloadedStackExample = dispatch reloaded "example" [ "word", jstr "durable.stack_wrapper"; "caseName", jstr "basic" ] |> expectOk "run reloaded Stack example calling Flow"
        check (boolValue (reloadedStackExample.["data"].["results"].[0].["passed"])) "reloaded Stack example calling Flow passes"
        let stackWrapperValue = dispatch reloaded "eval" [ "code", jstr "9 durable.stack_wrapper" ] |> expectOk "evaluate reloaded Stack caller"
        equal "10" (stringValue (stackWrapperValue.["data"].["stack"].[0])) "fresh Engine reload executes the Stack caller against Flow"

    let private testExplicitFrontendCannotFallBack root =
        let engine = Runtime.Engine(Path.Combine(root, "frontend-selector"), Set.empty)
        let stack = dispatch engine "eval" [ "code", jstr "10 20 add" ] |> expectOk "omitted frontend keeps Stack evaluation"
        equal "30" (stringValue (stack.["data"].["stack"].[0])) "default Stack expression remains supported"
        let freshFlowEngine = Runtime.Engine(Path.Combine(root, "flow-expression-without-words"), Set.empty)
        let primitiveOnly = evalFlow freshFlowEngine "add(1, 2)" |> expectOk "evaluate a Flow primitive with no user Flow words"
        equal "3" (stringValue (primitiveOnly.["data"].["stack"].[0])) "explicit Flow expression resolves primitives in a fresh empty Engine"
        let unknown = dispatch engine "eval" [ "frontend", jstr "flow2"; "code", jstr "10 20 add" ]
        expectError "RUNTIME_FRONTEND_UNSUPPORTED" unknown |> ignore
        let invalidFrontendValues: JsonNode list = [ null; jbool true; JsonObject() :> JsonNode ]
        for value in invalidFrontendValues do
            dispatch engine "eval" [ "frontend", value; "code", jstr "10 20 add" ]
            |> expectError "RUNTIME_FRONTEND_INVALID"
            |> ignore
        let malformedEval = evalFlow engine "word broken"
        check (not (succeeded malformedEval)) "malformed explicit Flow eval is rejected"
        check ((errorCode malformedEval).StartsWith("FLOW_", StringComparison.Ordinal)) "malformed explicit Flow eval keeps its Flow diagnostic"
        let malformedDefine = defineFlow engine "word malformed" [] [] []
        check (not (succeeded malformedDefine)) "malformed explicit Flow definition is rejected"
        check ((errorCode malformedDefine).StartsWith("FLOW_", StringComparison.Ordinal)) "malformed Flow definition is not passed to the Stack parser"

    let private testGeneratedRecordCasesPersistBesideFlow root =
        let project = Path.Combine(root, "flow-generated-record-cases")
        let engine = Runtime.Engine(project, Set.empty, "2030-01-02T03:04:05Z")
        defineFlow engine flowWordSource [ flowTestSource ] [ flowExampleSource ] []
        |> expectOk "define Flow authority beside a generated record type"
        |> ignore
        commit engine "commit" "durable.increment" []
        |> expectOk "commit Flow authority before adding a Stack record"
        |> ignore

        let stackSource =
            "record Receipt\n"
            + "    field amount Int\n"
            + "end\n"
            + "\n"
            + "test receipt.amount/read\n"
            + "    7 receipt.new receipt.amount\n"
            + "    => 7\n"
            + "end\n"
            + "\n"
            + "example receipt.amount/read\n"
            + "    9 receipt.new receipt.amount\n"
            + "    => 9\n"
            + "end\n"
        let parsed =
            match Parser.parse "<runtime-generated-record>" stackSource with
            | Ok value -> value
            | Error diagnostic -> failwith (Diagnostics.render diagnostic)
        let canonicalRecord = parsed.Records |> List.exactlyOne |> Source.renderRecord
        let canonicalTest = parsed.Tests |> List.exactlyOne |> Source.renderTest
        let canonicalExample = parsed.Examples |> List.exactlyOne |> Source.renderExample

        dispatch engine "define" [ "source", jstr stackSource ]
        |> expectOk "define a record and generated accessor test/example"
        |> ignore
        equal [ "read" ]
            (dispatch engine "tests" [ "word", jstr "receipt.amount" ] |> expectOk "inspect staged generated accessor tests" |> fun response -> jsonArrayStrings response.["data"])
            "generated accessor test is attached before type publication"
        equal [ "read" ]
            (dispatch engine "examples" [ "word", jstr "receipt.amount" ] |> expectOk "inspect staged generated accessor examples" |> fun response -> jsonArrayStrings response.["data"])
            "generated accessor example is attached before type publication"

        commit engine "commit" "Receipt" []
        |> expectOk "commit record type with generated accessor cases"
        |> ignore
        assertAllPassed 1 (dispatch engine "test" [ "word", jstr "receipt.amount" ] |> expectOk "run committed generated accessor test")
        let stagedExample = dispatch engine "example" [ "word", jstr "receipt.amount"; "caseName", jstr "read" ] |> expectOk "run committed generated accessor example"
        check (boolValue (stagedExample.["data"].["results"].[0].["passed"])) "generated accessor example passes before reload"

        let store = Storage.create project
        let loaded = Storage.load store |> Result.defaultWith (fun problem -> failwith $"load mixed Flow and generated record manifest: {problem.Code}: {problem.Message}")
        let manifest = loaded.Manifest |> Option.defaultWith (fun () -> failwith "mixed Flow/generated-record manifest is missing")
        check (manifest.Types |> List.exists (fun item -> item.Name = "Receipt")) "type-only commit persists the record definition"
        let export = loaded.ProjectSource |> Option.defaultWith (fun () -> failwith "mixed project export is missing")
        equal (File.ReadAllText(Path.Combine(project, "dictionary.agent"))) export "aggregate export bytes match the authoritative ProjectSource"
        check (export.Contains("// frontend: flow/1", StringComparison.Ordinal)) "mixed aggregate export preserves its Flow source marker"
        check (export.Contains(canonicalRecord, StringComparison.Ordinal)) "aggregate export contains the canonical record declaration"
        check (export.Contains(canonicalTest, StringComparison.Ordinal)) "aggregate export contains the canonical generated accessor test"
        check (export.Contains(canonicalExample, StringComparison.Ordinal)) "aggregate export contains the canonical generated accessor example"

        let reloaded = Runtime.Engine(project, Set.empty, "2030-01-02T03:04:05Z")
        equal [ "read" ]
            (dispatch reloaded "tests" [ "word", jstr "receipt.amount" ] |> expectOk "inspect generated accessor tests after reload" |> fun response -> jsonArrayStrings response.["data"])
            "generated accessor test survives fresh Engine reload"
        equal [ "read" ]
            (dispatch reloaded "examples" [ "word", jstr "receipt.amount" ] |> expectOk "inspect generated accessor examples after reload" |> fun response -> jsonArrayStrings response.["data"])
            "generated accessor example survives fresh Engine reload"
        assertAllPassed 1 (dispatch reloaded "test" [ "word", jstr "receipt.amount" ] |> expectOk "run generated accessor test after reload")
        let reloadedExample = dispatch reloaded "example" [ "word", jstr "receipt.amount"; "caseName", jstr "read" ] |> expectOk "run generated accessor example after reload"
        check (boolValue (reloadedExample.["data"].["results"].[0].["passed"])) "generated accessor example passes after reload"
        let value = dispatch reloaded "eval" [ "code", jstr "7 receipt.new receipt.amount" ] |> expectOk "evaluate generated accessor after reload"
        equal "7" (stringValue (value.["data"].["stack"].[0])) "generated accessor body remains executable after reload"

    let private testStackGeneratedCasesSurviveV1Manifest root =
        let project = Path.Combine(root, "stack-generated-cases-v1")
        let engine = Runtime.Engine(project, Set.empty, "2030-01-02T03:04:05Z")
        let source =
            "record Receipt\n"
            + "    field amount Int\n"
            + "end\n"
            + "\n"
            + "word receipt.double : Receipt -> Int\n"
            + "    effects none\n"
            + "    receipt.amount\n"
            + "end\n"
            + "\n"
            + "test receipt.double/basic\n"
            + "    9 receipt.new receipt.double\n"
            + "    => 9\n"
            + "end\n"
            + "\n"
            + "test receipt.amount/read\n"
            + "    7 receipt.new receipt.amount\n"
            + "    => 7\n"
            + "end\n"
            + "\n"
            + "example receipt.amount/read\n"
            + "    11 receipt.new receipt.amount\n"
            + "    => 11\n"
            + "end\n"
        dispatch engine "define" [ "source", jstr source ]
        |> expectOk "define a Stack record, word, and generated accessor cases"
        |> ignore
        commit engine "commit" "Receipt" []
        |> expectOk "commit Stack record and generated accessor cases"
        |> ignore
        commit engine "commit" "receipt.double" []
        |> expectOk "commit a Stack word using the generated accessor"
        |> ignore

        let store = Storage.create project
        let loaded = Storage.load store |> Result.defaultWith (fun problem -> failwith $"load Stack-only manifest: {problem.Code}: {problem.Message}")
        let manifest = loaded.Manifest |> Option.defaultWith (fun () -> failwith "Stack-only manifest is missing")
        equal 2 manifest.FormatVersion "Runtime initially publishes the Stack project as manifest v2"
        check (not (List.isEmpty manifest.Revisions)) "Stack-only fixture has a real word revision"
        check (manifest.Revisions |> List.forall (fun revision -> revision.SourceFormat = { Frontend = SourceFrontend.Stack; Version = 1 })) "every revision uses Stack/1"
        check (manifest.Revisions |> List.forall (fun revision -> List.isEmpty revision.CallBindings)) "Stack/1 revisions retain default empty call-binding metadata"

        let export = loaded.ProjectSource |> Option.defaultWith (fun () -> failwith "Stack-only project export is missing")
        let sourceReferences =
            [ manifest.ProjectSource ]
            @ (manifest.Types |> List.map (fun item -> item.Definition))
            @ (manifest.Revisions |> List.collect (fun revision -> revision.Definition :: revision.Tests @ revision.Examples))
            |> List.distinct
        let sourceObjects =
            sourceReferences
            |> List.map (fun reference ->
                let content = Storage.readSource store reference |> Result.defaultWith (fun problem -> failwith problem.Message)
                { Reference = reference; Content = content })
        let legacyManifest = { manifest with FormatVersion = 1 }
        match Storage.commit store loaded.Generation legacyManifest sourceObjects export with
        | Ok _ -> ()
        | Error problem -> failwith $"recommit the same Stack objects as a v1 manifest: {problem.Code}: {problem.Message}"

        let converted = Storage.load store |> Result.defaultWith (fun problem -> failwith $"load v1 Stack manifest: {problem.Code}: {problem.Message}")
        let convertedManifest = converted.Manifest |> Option.defaultWith (fun () -> failwith "converted v1 manifest is missing")
        equal 1 convertedManifest.FormatVersion "the compatibility step publishes v1 authority"
        equal manifest.Types convertedManifest.Types "v1 conversion keeps the exact type source references"
        equal manifest.Revisions convertedManifest.Revisions "v1 conversion keeps the exact Stack revisions and empty binding metadata"
        equal export (converted.ProjectSource |> Option.defaultWith (fun () -> failwith "converted project export is missing")) "v1 conversion keeps the identical aggregate source"

        let reloaded = Runtime.Engine(project, Set.empty, "2030-01-02T03:04:05Z")
        equal [ "read" ]
            (dispatch reloaded "tests" [ "word", jstr "receipt.amount" ] |> expectOk "inspect generated accessor tests from v1" |> fun response -> jsonArrayStrings response.["data"])
            "v1 aggregate loading retains the generated accessor test"
        equal [ "read" ]
            (dispatch reloaded "examples" [ "word", jstr "receipt.amount" ] |> expectOk "inspect generated accessor examples from v1" |> fun response -> jsonArrayStrings response.["data"])
            "v1 aggregate loading retains the generated accessor example"
        assertAllPassed 1 (dispatch reloaded "test" [ "word", jstr "receipt.amount" ] |> expectOk "run generated accessor test from v1")
        let example = dispatch reloaded "example" [ "word", jstr "receipt.amount"; "caseName", jstr "read" ] |> expectOk "run generated accessor example from v1"
        check (boolValue (example.["data"].["results"].[0].["passed"])) "generated accessor example passes after v1 migration"
        let value = dispatch reloaded "eval" [ "code", jstr "9 receipt.new receipt.double" ] |> expectOk "evaluate Stack word from v1"
        equal "9" (stringValue (value.["data"].["stack"].[0])) "Stack word remains executable after v1 migration"

    let private testStackOwnerMigrationToFlow root =
        let project = Path.Combine(root, "stack-to-flow-migration")
        let engine = Runtime.Engine(project, Set.empty, "2030-01-02T03:04:05Z")
        let stackDefinition =
            "word migration.increment : Int -> Int\n"
            + "    effects none\n"
            + "    1 add\n"
            + "end\n"
        let stackTest =
            "test migration.increment/basic\n"
            + "    41 migration.increment\n"
            + "    expect 42\n"
            + "end\n"
        let stackExample =
            "example migration.increment/basic\n"
            + "    1 migration.increment\n"
            + "    => 2\n"
            + "end\n"
        let completeStackSource = stackDefinition + "\n" + stackTest + "\n" + stackExample
        dispatch engine "define" [ "source", jstr completeStackSource ]
        |> expectOk "define a Stack owner with authored cases"
        |> ignore
        let wordId = getWordId engine "migration.increment"
        commit engine "commit" "migration.increment" [] |> expectOk "commit Stack owner before Flow migration" |> ignore

        let store = Storage.create project
        let stackSnapshot = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        let stackManifest = stackSnapshot.Manifest |> Option.defaultWith (fun () -> failwith "Stack migration baseline is missing")
        let stackRevision = stackManifest.Revisions |> List.find (fun item -> item.WordId = wordId)
        equal { Frontend = SourceFrontend.Stack; Version = 1 } stackRevision.SourceFormat "migration baseline is explicitly Stack/1"
        equal 1 stackRevision.Tests.Length "migration baseline has one Stack test"
        equal 1 stackRevision.Examples.Length "migration baseline has one Stack example"
        let originalTestReference = stackRevision.Tests.Head
        let originalExampleReference = stackRevision.Examples.Head
        let originalStoredTest = Storage.readSource store originalTestReference |> Result.defaultWith (fun problem -> failwith problem.Message)
        let originalStoredExample = Storage.readSource store originalExampleReference |> Result.defaultWith (fun problem -> failwith problem.Message)
        check (originalStoredTest.Contains("test migration.increment/basic", StringComparison.Ordinal)) "baseline Stack test object contains its declared case"
        check (originalStoredExample.Contains("example migration.increment/basic", StringComparison.Ordinal)) "baseline Stack example object contains its declared case"

        let flowDefinition =
            "word migration.increment(value: Int) -> Int {\n"
            + "    effects none\n"
            + "    add(value, 1)\n"
            + "}"
        let flowTest =
            "test migration.increment/basic {\n"
            + "    migration::increment(41)\n"
            + "    => value migration::increment(41)\n"
            + "}"
        let flowExample =
            "example migration.increment/basic {\n"
            + "    migration::increment(1)\n"
            + "    => 2\n"
            + "}"

        // Omitting case arrays requests retention. A Flow revision cannot inherit
        // Stack case objects, so either staging or publication must reject this.
        let mixedStage =
            defineFlow engine flowDefinition [] [] [ "replace", jbool true; "expectedRevision", jint 1 ]
        let mixedFailure =
            if not (succeeded mixedStage) then mixedStage
            else commit engine "replace-word" "migration.increment" []
        check (not (succeeded mixedFailure)) "Flow owner replacement rejects retained Stack test/example objects"
        check (not (String.IsNullOrWhiteSpace(errorCode mixedFailure))) "mixed-frontend rejection is structured"
        let afterMixedAttempt = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        equal stackSnapshot.ManifestHash afterMixedAttempt.ManifestHash "rejected mixed-frontend migration leaves CURRENT unchanged"
        let afterMixedManifest = afterMixedAttempt.Manifest |> Option.defaultWith (fun () -> failwith "Stack owner disappeared after rejected migration")
        let afterMixedRevision = afterMixedManifest.Revisions |> List.find (fun item -> item.WordId = wordId && item.Revision = 1)
        equal stackRevision.Tests afterMixedRevision.Tests "rejected mixed migration retains the exact old Stack test reference"
        equal stackRevision.Examples afterMixedRevision.Examples "rejected mixed migration retains the exact old Stack example reference"
        if succeeded mixedStage then
            dispatch engine "discard" [ "word", jstr "migration.increment" ]
            |> expectOk "discard rejected mixed-frontend Flow candidate"
            |> ignore
        let stillStack = Runtime.Engine(project, Set.empty, "2030-01-02T03:04:05Z")
        let oldEvaluation = dispatch stillStack "eval" [ "code", jstr "1 migration.increment" ] |> expectOk "reload Stack owner after rejected migration"
        equal "2" (stringValue (oldEvaluation.["data"].["stack"].[0])) "failed migration leaves the Stack implementation executable"
        assertAllPassed 1 (dispatch stillStack "test" [ "word", jstr "migration.increment" ] |> expectOk "rerun Stack case after rejected migration")

        defineFlow engine flowDefinition [ flowTest ] [ flowExample ]
            [ "replace", jbool true
              "expectedRevision", jint 1 ]
        |> expectOk "replace every Stack case with Flow-authored cases"
        |> ignore
        commit engine "replace-word" "migration.increment" []
        |> expectOk "publish complete Stack-to-Flow migration"
        |> ignore
        equal wordId (getWordId engine "migration.increment") "frontend migration preserves the stable owner ID"

        let migrated = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        let migratedManifest = migrated.Manifest |> Option.defaultWith (fun () -> failwith "Flow migration did not publish")
        let revisions = migratedManifest.Revisions |> List.filter (fun item -> item.WordId = wordId) |> List.sortBy _.Revision
        equal [ 1; 2 ] (revisions |> List.map _.Revision) "migration retains both immutable owner revisions"
        let oldRevision = revisions.Head
        let flowRevision = List.item 1 revisions
        equal { Frontend = SourceFrontend.Stack; Version = 1 } oldRevision.SourceFormat "historical revision remains Stack/1"
        equal [ originalTestReference ] oldRevision.Tests "historical Stack test reference is unchanged"
        equal [ originalExampleReference ] oldRevision.Examples "historical Stack example reference is unchanged"
        equal originalStoredTest (Storage.readSource store oldRevision.Tests.Head |> Result.defaultWith (fun problem -> failwith problem.Message)) "historical Stack test bytes remain available unchanged"
        equal originalStoredExample (Storage.readSource store oldRevision.Examples.Head |> Result.defaultWith (fun problem -> failwith problem.Message)) "historical Stack example bytes remain available unchanged"
        equal { Frontend = SourceFrontend.Flow; Version = 1 } flowRevision.SourceFormat "current revision is Flow/1"
        equal flowDefinition (Storage.readSource store flowRevision.Definition |> Result.defaultWith (fun problem -> failwith problem.Message)) "migrated definition preserves authored Flow source"
        equal [ flowTest ] (flowRevision.Tests |> List.map (fun reference -> Storage.readSource store reference |> Result.defaultWith (fun problem -> failwith problem.Message))) "all current tests are Flow-authored"
        equal [ flowExample ] (flowRevision.Examples |> List.map (fun reference -> Storage.readSource store reference |> Result.defaultWith (fun problem -> failwith problem.Message))) "all current examples are Flow-authored"

        let reloaded = Runtime.Engine(project, Set.empty, "2030-01-02T03:04:05Z")
        equal flowDefinition (dispatch reloaded "source" [ "word", jstr "migration.increment" ] |> expectOk "read migrated source after reload" |> fun response -> stringValue (response.["data"])) "fresh Engine loads the Flow owner"
        assertAllPassed 1 (dispatch reloaded "test" [ "word", jstr "migration.increment" ] |> expectOk "run migrated Flow test after reload")
        let example = dispatch reloaded "example" [ "word", jstr "migration.increment"; "caseName", jstr "basic" ] |> expectOk "run migrated Flow example after reload"
        check (boolValue (example.["data"].["results"].[0].["passed"])) "migrated Flow example passes after reload"
        equal flowExample (stringValue (example.["data"].["results"].[0].["source"])) "migrated Flow example source survives reload"

    let private testReplacementCasAndRollback root =
        let project = Path.Combine(root, "replacement-cas")
        let engine = Runtime.Engine(project, Set.empty, "2030-01-02T03:04:05Z")
        defineFlow engine flowWordSource [ flowTestSource ] [ flowExampleSource ] []
        |> expectOk "define initial replacement target"
        |> ignore
        let originalId = getWordId engine "durable.increment"
        commit engine "commit" "durable.increment" [] |> expectOk "commit initial replacement target" |> ignore

        let store = Storage.create project
        let before = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        let originalManifestHash = before.ManifestHash

        let compatibleReplacement =
            "word durable.increment(value: Int) -> Int {\n"
            + "    effects none\n"
            + "    add(1, value)\n"
            + "}"
        defineFlow engine compatibleReplacement [] [] [ "replace", jbool true; "expectedRevision", jint 1 ]
        |> expectOk "stage same-ID Flow replacement"
        |> ignore
        commit engine "replace-word" "durable.increment" []
        |> expectOk "commit same-ID Flow replacement"
        |> ignore
        equal originalId (getWordId engine "durable.increment") "Flow replacement keeps the stable word identity"
        let replaced = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        let replacedManifest = replaced.Manifest.Value
        let revisions = replacedManifest.Revisions |> List.filter (fun item -> item.WordId = originalId) |> List.sortBy _.Revision
        equal [ 1; 2 ] (revisions |> List.map _.Revision) "Flow replacement appends a second durable revision"
        check originalManifestHash.IsSome "initial replacement target was durably published"
        equal compatibleReplacement (Storage.readSource store (List.item 1 revisions).Definition |> Result.defaultWith (fun problem -> failwith problem.Message)) "replacement stores its authored Flow source"
        equal ProjectWord (List.item 1 revisions).Maturity "same-ID replacement preserves project maturity"
        let afterValue = evalFlow engine "durable::increment(9)" |> expectOk "evaluate compatible replacement"
        equal "10" (stringValue (afterValue.["data"].["stack"].[0])) "same-ID replacement updates the executable snapshot"
        let committedHistory = dispatch engine "history" [ "word", jstr "durable.increment" ] |> expectOk "read history after Flow replacement"
        equal flowWordSource (stringValue (committedHistory.["data"].[0].["source"])) "replacement retains the original authored revision"

        let failedReplacement =
            "word durable.increment(value: Int) -> Int {\n"
            + "    effects none\n"
            + "    add(value, 2)\n"
            + "}"
        let wrongTest =
            "test durable.increment/wrong_expectation {\n"
            + "    durable::increment(41)\n"
            + "    => 42\n"
            + "}"
        defineFlow engine failedReplacement [ wrongTest ] [] [ "replace", jbool true; "expectedRevision", jint 2 ]
        |> expectOk "stage replacement whose new attached test fails"
        |> ignore
        let failingCommit = commit engine "replace-word" "durable.increment" []
        check (not (succeeded failingCommit)) "a replacement with a failing authored case is rejected"
        let afterFailure = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        equal replaced.ManifestHash afterFailure.ManifestHash "failed replacement leaves the manifest authority unchanged"
        let freshAfterFailure = Runtime.Engine(project, Set.empty, "2030-01-02T03:04:05Z")
        equal "10" (evalFlow freshAfterFailure "durable::increment(9)" |> expectOk "evaluate durable word after rejected replacement" |> fun response -> stringValue (response.["data"].["stack"].[0])) "failed replacement is not reachable after a fresh Engine reload"
        dispatch engine "discard" [ "word", jstr "durable.increment" ] |> expectOk "discard failed replacement candidate" |> ignore
        equal "10" (evalFlow engine "durable::increment(9)" |> expectOk "evaluate after discarding rejected replacement" |> fun response -> stringValue (response.["data"].["stack"].[0])) "discard restores the live pre-replacement body"

        let staleRemove =
            let removal = JsonObject()
            removal.["kind"] <- jstr "example"
            removal.["caseName"] <- jstr "one"
            removal.["expectedSourceHash"] <- jstr (String.replicate 64 "0")
            let removals = JsonArray()
            removals.Add removal
            removals :> JsonNode
        let staleArgs =
            [ "frontend", jstr "flow"
              "source", jstr compatibleReplacement
              "replace", jbool true
              "expectedRevision", jint 2
              "removeAttachments", staleRemove ]
        let stale = dispatch engine "define" staleArgs
        expectError "FLOW_ATTACHMENT_STALE_SOURCE" stale |> ignore
        let afterStale = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        equal replaced.ManifestHash afterStale.ManifestHash "stale attachment removal CAS does not publish"

        let updatedBasicTest =
            "test durable.increment/basic {\n"
            + "    durable::increment(40)\n"
            + "    => value durable::increment(40)\n"
            + "}"
        let extraTest =
            "test durable.increment/extra {\n"
            + "    durable::increment(5)\n"
            + "    => value durable::increment(5)\n"
            + "}"
        defineFlow engine compatibleReplacement [ updatedBasicTest; extraTest ] []
            [ "replace", jbool true
              "expectedRevision", jint 2 ]
        |> expectOk "replace an attached Flow test and add another case"
        |> ignore
        commit engine "replace-word" "durable.increment" [] |> expectOk "publish Flow test replacement and addition" |> ignore
        let afterTestEdit = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        let editedRevision = afterTestEdit.Manifest.Value.Revisions |> List.find (fun item -> item.WordId = originalId && item.Revision = 3)
        let editedTestSources = editedRevision.Tests |> List.map (fun reference -> Storage.readSource store reference |> Result.defaultWith (fun problem -> failwith problem.Message)) |> Set.ofList
        equal (Set.ofList [ updatedBasicTest; extraTest ]) editedTestSources "attachment edit replaces one exact case and adds another"
        equal 1 editedRevision.Examples.Length "omitted examples retain the original example source"
        assertAllPassed 2 (dispatch engine "test" [ "word", jstr "durable.increment" ] |> expectOk "run retained and updated Flow tests")

        let currentRevision = editedRevision
        let exampleHash = currentRevision.Examples.Head.Hash
        let removeExample = JsonObject()
        removeExample.["kind"] <- jstr "example"
        removeExample.["caseName"] <- jstr "one"
        removeExample.["expectedSourceHash"] <- jstr exampleHash
        let validRemovals = JsonArray()
        validRemovals.Add removeExample
        defineFlow engine compatibleReplacement [] []
            [ "replace", jbool true
              "expectedRevision", jint 3
              "removeAttachments", (validRemovals :> JsonNode) ]
        |> expectOk "stage attachment removal with its exact current source hash"
        |> ignore
        commit engine "replace-word" "durable.increment" []
        |> expectOk "publish source-bound example removal"
        |> ignore
        let afterRemoval = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        let removalRevision = afterRemoval.Manifest.Value.Revisions |> List.find (fun item -> item.WordId = originalId && item.Revision = 4)
        equal [] removalRevision.Examples "successful CAS removes the requested authored example"
        equal [ "basic"; "extra" ] (jsonArrayStrings (dispatch engine "tests" [ "word", jstr "durable.increment" ] |> expectOk "test remains after example removal" |> fun response -> response.["data"])) "unmentioned attachments remain intact"

    let private testTemporaryPromotionAndTaskAbort root =
        let project = Path.Combine(root, "temporary-promotion")
        let engine = Runtime.Engine(project, Set.empty, "2030-01-02T03:04:05Z")
        let discardedSource =
            "word temporary.discarded(value: Int) -> Int {\n    effects none\n    add(value, 1)\n}"
        let discardedTest =
            "test temporary.discarded/basic {\n    temporary::discarded(1)\n    => value temporary::discarded(1)\n}"
        defineFlow engine discardedSource [ discardedTest ] [] [ "temporary", jbool true ]
        |> expectOk "define temporary Flow word"
        |> ignore
        let discardedId = getWordId engine "temporary.discarded"
        let emptyStore = Storage.load (Storage.create project) |> Result.defaultWith (fun problem -> failwith problem.Message)
        check emptyStore.Manifest.IsNone "temporary Flow definition does not enter durable Storage"
        dispatch engine "discard" [ "word", jstr "temporary.discarded" ] |> expectOk "discard temporary Flow word" |> ignore
        let missingAfterDiscard = dispatch engine "describe" [ "word", jstr "temporary.discarded" ]
        expectError "NAME_UNKNOWN_WORD" missingAfterDiscard |> ignore

        let promotedSource =
            "word temporary.promoted(value: Int) -> Int {\n    effects none\n    add(value, 1)\n}"
        let promotedTest =
            "test temporary.promoted/basic {\n    temporary::promoted(1)\n    => value temporary::promoted(1)\n}"
        defineFlow engine promotedSource [ promotedTest ] [] [ "temporary", jbool true ]
        |> expectOk "define Flow word for promotion"
        |> ignore
        let promotedId = getWordId engine "temporary.promoted"
        dispatch engine "promote" [ "word", jstr "temporary.promoted" ] |> expectOk "promote temporary Flow word" |> ignore
        commit engine "commit" "temporary.promoted" [] |> expectOk "commit promoted Flow word" |> ignore
        equal promotedId (getWordId engine "temporary.promoted") "promotion preserves the in-memory Flow word ID"
        let promotedManifest = (Storage.load (Storage.create project) |> Result.defaultWith (fun problem -> failwith problem.Message)).Manifest.Value
        let promotedRevision = promotedManifest.Revisions |> List.find (fun item -> item.WordId = promotedId)
        equal { Frontend = SourceFrontend.Flow; Version = 1 } promotedRevision.SourceFormat "promoted definition persists as Flow/1"
        check (discardedId <> promotedId) "distinct temporary definitions receive distinct IDs"

        let cleanupProject = Path.Combine(root, "task-commit-flow-temporaries")
        let cleanupEngine = Runtime.Engine(cleanupProject, Set.empty, "2030-01-02T03:04:05Z")
        let priorSource =
            "word temporary.prior(value: Int) -> Int {\n    effects none\n    add(value, 1)\n}"
        let priorTest =
            "test temporary.prior/basic {\n    temporary::prior(1)\n    => value temporary::prior(1)\n}"
        defineFlow cleanupEngine priorSource [ priorTest ] [] [ "temporary", jbool true ]
        |> expectOk "define Flow temporary before a task"
        |> ignore
        let priorId = getWordId cleanupEngine "temporary.prior"
        let priorDescribe = dispatch cleanupEngine "describe" [ "word", jstr "temporary.prior" ] |> expectOk "capture prior temporary metadata"
        let priorTests = dispatch cleanupEngine "tests" [ "word", jstr "temporary.prior" ] |> expectOk "capture prior temporary cases"
        let priorExamples = dispatch cleanupEngine "examples" [ "word", jstr "temporary.prior" ] |> expectOk "capture prior temporary examples"
        dispatch cleanupEngine "task.begin" [ "goal", jstr "clear only task-local Flow words" ]
        |> expectOk "begin Flow temporary cleanup task"
        |> ignore
        let taskOnlySource =
            "word temporary.task_only(value: Int) -> Int {\n    effects none\n    add(value, 2)\n}"
        let taskOnlyTest =
            "test temporary.task_only/basic {\n    temporary::task_only(1)\n    => value temporary::task_only(1)\n}"
        defineFlow cleanupEngine taskOnlySource [ taskOnlyTest ] [] [ "temporary", jbool true ]
        |> expectOk "define task-local Flow temporary"
        |> ignore
        let taskOnlyId = getWordId cleanupEngine "temporary.task_only"
        dispatch cleanupEngine "task.commit" [] |> expectOk "commit Flow temporary cleanup task" |> ignore

        equal priorId (getWordId cleanupEngine "temporary.prior") "task.commit preserves the exact pre-task temporary identity"
        equal ((priorDescribe.["data"]).ToJsonString()) (((dispatch cleanupEngine "describe" [ "word", jstr "temporary.prior" ] |> expectOk "read prior metadata after task.commit").["data"]).ToJsonString()) "task.commit preserves pre-task Flow metadata exactly"
        equal (stringValue ((dispatch cleanupEngine "source" [ "word", jstr "temporary.prior" ] |> expectOk "read prior Flow source after task.commit").["data"])) priorSource "task.commit preserves pre-task Flow source bytes"
        equal ((priorTests.["data"]).ToJsonString()) (((dispatch cleanupEngine "tests" [ "word", jstr "temporary.prior" ] |> expectOk "read prior Flow cases after task.commit").["data"]).ToJsonString()) "task.commit preserves pre-task Flow test metadata exactly"
        equal ((priorExamples.["data"]).ToJsonString()) (((dispatch cleanupEngine "examples" [ "word", jstr "temporary.prior" ] |> expectOk "read prior Flow examples after task.commit").["data"]).ToJsonString()) "task.commit preserves pre-task Flow example metadata exactly"
        equal "2" (evalFlow cleanupEngine "temporary::prior(1)" |> expectOk "execute pre-task Flow temporary after task.commit" |> fun response -> stringValue (response.["data"].["stack"].[0])) "pre-task Flow temporary remains executable"
        check (taskOnlyId <> priorId) "task-local and pre-task temporaries have distinct identities"
        expectError "NAME_UNKNOWN_WORD" (dispatch cleanupEngine "source" [ "word", jstr "temporary.task_only" ]) |> ignore
        let cleanupStore = Storage.create cleanupProject
        let afterTaskCommit = Storage.load cleanupStore |> Result.defaultWith (fun problem -> failwith problem.Message)
        check afterTaskCommit.Manifest.IsNone "task.commit does not persist session-only Flow words"
        let objectRoot = Path.Combine(cleanupProject, ".agentlang", "store", "objects")
        let objectCount = if Directory.Exists objectRoot then Directory.GetFiles(objectRoot, "*", SearchOption.AllDirectories).Length else 0
        equal 0 objectCount "task.commit leaves no durable orphan source objects for Flow temporaries"
        let freshCleanupEngine = Runtime.Engine(cleanupProject, Set.empty, "2030-01-02T03:04:05Z")
        expectError "NAME_UNKNOWN_WORD" (dispatch freshCleanupEngine "source" [ "word", jstr "temporary.prior" ]) |> ignore
        expectError "NAME_UNKNOWN_WORD" (dispatch freshCleanupEngine "source" [ "word", jstr "temporary.task_only" ]) |> ignore

        let emptyTaskProject = Path.Combine(root, "task-abort-empty-authority")
        let emptyTaskEngine = Runtime.Engine(emptyTaskProject, Set.empty, "2030-01-02T03:04:05Z")
        let emptyTaskStore = Storage.create emptyTaskProject
        let emptyBefore = Storage.load emptyTaskStore |> Result.defaultWith (fun problem -> failwith problem.Message)
        check emptyBefore.Manifest.IsNone "empty task rollback begins without a manifest"
        expectError "STORAGE_SNAPSHOT_REQUIRES_MANIFEST" (dispatch emptyTaskEngine "snapshot.save" [ "name", jstr "not-allowed" ]) |> ignore
        let emptyBaselineWords = dispatch emptyTaskEngine "words" [] |> expectOk "capture empty-authority builtin inventory"
        let emptyBaselineInventory = (emptyBaselineWords.["data"].["words"]).ToJsonString()
        let emptyExportPath = Path.Combine(emptyTaskProject, "dictionary.agent")
        let emptyExport = if File.Exists emptyExportPath then Some(File.ReadAllBytes emptyExportPath) else None
        dispatch emptyTaskEngine "task.begin" [ "goal", jstr "commit and roll back from empty authority" ]
        |> expectOk "begin task from empty storage authority"
        |> ignore
        let committedDuringTask =
            "word task.empty_abort(value: Int) -> Int {\n"
            + "    effects none\n"
            + "    add(value, 1)\n"
            + "}"
        let committedDuringTaskTest =
            "test task.empty_abort/basic {\n"
            + "    task::empty_abort(1)\n"
            + "    => value task::empty_abort(1)\n"
            + "}"
        defineFlow emptyTaskEngine committedDuringTask [ committedDuringTaskTest ] [] []
        |> expectOk "define Flow word inside empty-authority task"
        |> ignore
        let committedTaskId = getWordId emptyTaskEngine "task.empty_abort"
        commit emptyTaskEngine "commit" "task.empty_abort" [] |> expectOk "commit Flow into empty-authority task" |> ignore
        let duringEmptyTask = Storage.load emptyTaskStore |> Result.defaultWith (fun problem -> failwith problem.Message)
        check duringEmptyTask.Manifest.IsSome "Flow commit creates the task-local manifest authority"
        equal "2" (evalFlow emptyTaskEngine "task::empty_abort(1)" |> expectOk "evaluate committed Flow in empty-authority task" |> fun response -> stringValue (response.["data"].["stack"].[0])) "Flow word is active before empty-authority rollback"
        dispatch emptyTaskEngine "task.abort" [] |> expectOk "abort Flow commit to empty authority" |> ignore
        let restoredEmpty = Storage.load emptyTaskStore |> Result.defaultWith (fun problem -> failwith problem.Message)
        check restoredEmpty.Manifest.IsNone "task.abort restores the original EmptyAuthority"
        let restoredInventory = dispatch emptyTaskEngine "words" [] |> expectOk "inspect in-memory words after empty-authority abort"
        equal emptyBaselineInventory ((restoredInventory.["data"].["words"]).ToJsonString()) "task.abort restores the exact pre-task names, IDs, and word metadata"
        expectError "NAME_UNKNOWN_WORD" (dispatch emptyTaskEngine "source" [ "word", jstr "task.empty_abort" ]) |> ignore
        match emptyExport with
        | Some expected ->
            check (File.Exists emptyExportPath) "empty-authority abort restores its original export"
            check (expected.AsSpan().SequenceEqual(File.ReadAllBytes(emptyExportPath).AsSpan())) "empty-authority abort restores exact export bytes"
        | None -> check (not (File.Exists emptyExportPath)) "empty-authority abort restores absence of a prior export"
        check (not (String.IsNullOrWhiteSpace committedTaskId)) "the Flow owner had a concrete ID before rollback"
        let freshEmptyTaskEngine = Runtime.Engine(emptyTaskProject, Set.empty, "2030-01-02T03:04:05Z")
        let freshEmptyInventory = dispatch freshEmptyTaskEngine "words" [] |> expectOk "fresh-load empty task authority"
        equal emptyBaselineInventory ((freshEmptyInventory.["data"].["words"]).ToJsonString()) "fresh Engine exposes the same restored builtin word inventory and IDs"
        expectError "NAME_UNKNOWN_WORD" (dispatch freshEmptyTaskEngine "source" [ "word", jstr "task.empty_abort" ]) |> ignore

        let rollbackProject = Path.Combine(root, "task-abort-flow")
        let rollbackEngine = Runtime.Engine(rollbackProject, Set.empty, "2030-01-02T03:04:05Z")
        defineFlow rollbackEngine flowWordSource [ flowTestSource ] [ flowExampleSource ] []
        |> expectOk "define Flow baseline for task rollback"
        |> ignore
        commit rollbackEngine "commit" "durable.increment" [] |> expectOk "commit Flow baseline for task rollback" |> ignore
        let rollbackStore = Storage.create rollbackProject
        let before = Storage.load rollbackStore |> Result.defaultWith (fun problem -> failwith problem.Message)
        let beforeExport = File.ReadAllBytes(Path.Combine(rollbackProject, "dictionary.agent"))
        let task = dispatch rollbackEngine "task.begin" [ "goal", jstr "replace a Flow revision and roll it back" ] |> expectOk "begin Flow rollback task"
        check (not (isNull task.["data"])) "task starts with a committed Flow snapshot"
        let replacement = "word durable.increment(value: Int) -> Int {\n    effects none\n    add(value, 2)\n}"
        defineFlow rollbackEngine replacement [] [] [ "replace", jbool true; "expectedRevision", jint 1 ]
        |> expectOk "stage Flow replacement inside task"
        |> ignore
        commit rollbackEngine "replace-word" "durable.increment" [] |> expectOk "publish Flow replacement inside task" |> ignore
        let duringTask = Storage.load rollbackStore |> Result.defaultWith (fun problem -> failwith problem.Message)
        check (duringTask.ManifestHash <> before.ManifestHash) "Flow replacement becomes durable inside task before abort"
        dispatch rollbackEngine "task.abort" [] |> expectOk "abort Flow replacement task" |> ignore
        let restored = Storage.load rollbackStore |> Result.defaultWith (fun problem -> failwith problem.Message)
        equal before.ManifestHash restored.ManifestHash "task abort restores the original Flow manifest authority"
        check (restored.Generation > duringTask.Generation) "task abort advances Storage generation while restoring authority"
        check (beforeExport.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(rollbackProject, "dictionary.agent")).AsSpan())) "task abort restores exact Flow export bytes"
        let history = dispatch rollbackEngine "history" [ "word", jstr "durable.increment" ] |> expectOk "inspect restored Flow history"
        equal 1 ((history.["data"]).AsArray().Count) "task abort removes the rolled-back Flow revision from current history"
        equal flowWordSource (stringValue (history.["data"].[0].["source"])) "task abort restores the original authored Flow source"
        equal "10" (evalFlow rollbackEngine "durable::increment(9)" |> expectOk "evaluate restored Flow body" |> fun response -> stringValue (response.["data"].["stack"].[0])) "task abort restores the verified executable body"

    let private testNamedSnapshotRestoresFlowAndProviders root =
        let project = Path.Combine(root, "snapshot-flow")
        let savedClock = "2031-02-03T04:05:06Z"
        let capabilities = Set.ofList [ "fs.read"; "fs.write" ]
        let engine = Runtime.Engine(project, capabilities, savedClock)
        defineFlow engine flowWordSource [ flowTestSource ] [ flowExampleSource ] []
        |> expectOk "define snapshot Flow word"
        |> ignore
        commit engine "commit" "durable.increment" [] |> expectOk "commit snapshot Flow word" |> ignore
        dispatch engine "eval" [ "code", jstr "\"snapshot-file\" \"saved-value\" file.write" ]
        |> expectOk "write virtual provider state before snapshot"
        |> ignore
        dispatch engine "snapshot.save" [ "name", jstr "flow-baseline" ] |> expectOk "save Flow/provider snapshot" |> ignore
        let baseline = Storage.load (Storage.create project) |> Result.defaultWith (fun problem -> failwith problem.Message)

        let replacement = "word durable.increment(value: Int) -> Int {\n    effects none\n    add(value, 2)\n}"
        defineFlow engine replacement [] [] [ "replace", jbool true; "expectedRevision", jint 1 ]
        |> expectOk "stage later Flow revision after named snapshot"
        |> ignore
        commit engine "replace-word" "durable.increment" [] |> expectOk "commit later Flow revision after named snapshot" |> ignore
        dispatch engine "eval" [ "code", jstr "\"snapshot-file\" \"later-value\" file.write" ]
        |> expectOk "mutate virtual provider state after snapshot"
        |> ignore

        let reloaded = Runtime.Engine(project, capabilities, "2040-01-01T00:00:00Z")
        let loaded = dispatch reloaded "snapshot.load" [ "name", jstr "flow-baseline" ] |> expectOk "load named Flow/provider snapshot"
        equal savedClock (stringValue (loaded.["data"].["clockValue"])) "named snapshot restores its saved clock value"
        let restored = Storage.load (Storage.create project) |> Result.defaultWith (fun problem -> failwith problem.Message)
        equal baseline.ManifestHash restored.ManifestHash "named snapshot restores the original Flow manifest"
        let fileValue = dispatch reloaded "eval" [ "code", jstr "\"snapshot-file\" file.read" ] |> expectOk "read restored virtual provider file"
        equal "\"saved-value\"" (stringValue (fileValue.["data"].["stack"].[0])) "named snapshot restores exact virtual-file state"
        equal "2" (evalFlow reloaded "durable::increment(1)" |> expectOk "evaluate restored Flow revision" |> fun response -> stringValue (response.["data"].["stack"].[0])) "named snapshot rehydrates the earlier executable Flow revision"
        assertAllPassed 1 (dispatch reloaded "test" [ "word", jstr "durable.increment" ] |> expectOk "run restored Flow attachment after snapshot load")

    let private testPersistedBindingsAreVerified root =
        let project = Path.Combine(root, "persisted-binding-tamper")
        let engine = Runtime.Engine(project, Set.empty, "2030-01-02T03:04:05Z")
        defineFlow engine flowWordSource [ flowTestSource ] [ flowExampleSource ] []
        |> expectOk "define Flow source before persisted binding tamper"
        |> ignore
        commit engine "commit" "durable.increment" [] |> expectOk "commit source before persisted binding tamper" |> ignore

        let store = Storage.create project
        let snapshot = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        let manifest = snapshot.Manifest.Value
        let ownerId = getWordId engine "durable.increment"
        let ownerRevision = manifest.Revisions |> List.find (fun item -> item.WordId = ownerId)
        let targetBinding =
            ownerRevision.CallBindings
            |> List.find (fun binding -> binding.BodyRole = StoredCallBodyRole.Actual && binding.CaseName = Some "basic")
        let forgedRevision =
            { ownerRevision with
                CallBindings =
                    ownerRevision.CallBindings
                    |> List.map (fun binding ->
                        if binding = targetBinding then
                            { binding with Target = StoredCallTarget.UserWord "user-forged-runtime-target" }
                        else binding) }
        let forgedManifest =
            { manifest with
                Revisions = manifest.Revisions |> List.map (fun revision -> if revision.WordId = ownerId then forgedRevision else revision) }
        let sourceReferences =
            [ manifest.ProjectSource ]
            @ (manifest.Types |> List.map _.Definition)
            @ (manifest.Revisions |> List.collect (fun revision -> revision.Definition :: revision.Tests @ revision.Examples))
            |> List.distinct
        let sourceObjects =
            sourceReferences
            |> List.map (fun reference ->
                let content = Storage.readSource store reference |> Result.defaultWith (fun problem -> failwith problem.Message)
                { Reference = reference; Content = content })
        let exportText = File.ReadAllText(Path.Combine(project, "dictionary.agent"))
        let tamperCommit = Storage.commit store snapshot.Generation forgedManifest sourceObjects exportText
        match tamperCommit with
        | Ok _ -> ()
        | Error problem -> failwith $"Storage should accept the structurally well-shaped binding edit: {problem.Code}: {problem.Message}"

        let diagnostic =
            try
                Runtime.Engine(project, Set.empty) |> ignore
                None
            with
            | LanguageException problem -> Some problem
        let diagnostic = diagnostic |> Option.defaultWith (fun () -> failwith "A fresh Engine trusted a forged persisted Flow call target.")
        equal "FLOW_RUNTIME_BINDING_MISMATCH" diagnostic.Code "fresh Engine rejects mismatched persisted Flow metadata"
        equal (Some "durable.increment/basic") diagnostic.Word "binding mismatch identifies its owner and case"
        check diagnostic.Span.IsSome "binding mismatch identifies the authored call site"

    let private testRetainedDotBindingAcrossReplacement root =
        let project = Path.Combine(root, "retained-dot-binding")
        let engine = Runtime.Engine(project, Set.empty, "2030-01-02T03:04:05Z")
        let bumpSource =
            "word math.bump(value: Int, amount: Int) -> Int {\n"
            + "    effects none\n"
            + "    add(value, amount)\n"
            + "}"
        let bumpTest =
            "test math.bump/basic {\n"
            + "    math::bump(5, 1)\n"
            + "    => value math::bump(5, 1)\n"
            + "}"
        defineFlow engine bumpSource [ bumpTest ] [] [] |> expectOk "define a Flow dot-stage target" |> ignore
        let bumpId = getWordId engine "math.bump"
        commit engine "commit" "math.bump" [] |> expectOk "commit a Flow dot-stage target" |> ignore

        let callerSource =
            "word client.dot(value: Int) -> Int {\n"
            + "    effects none\n"
            + "    value.bump(amount = 1)\n"
            + "}"
        let callerTest =
            "test client.dot/basic {\n"
            + "    client::dot(5)\n"
            + "    => value client::dot(5)\n"
            + "}"
        defineFlow engine callerSource [ callerTest ] [] [] |> expectOk "define a retained named-argument dot caller" |> ignore
        commit engine "commit" "client.dot" [] |> expectOk "commit a retained named-argument dot caller" |> ignore
        equal "6" (evalFlow engine "client::dot(5)" |> expectOk "execute retained dot caller before replacement" |> fun response -> stringValue (response.["data"].["stack"].[0])) "dot caller binds to the original target"

        let replacementSource =
            "word math.bump(value: Int, amount: Int) -> Int {\n"
            + "    effects none\n"
            + "    add(amount, value)\n"
            + "}"
        defineFlow engine replacementSource [] [] [ "replace", jbool true; "expectedRevision", jint 1 ]
        |> expectOk "stage same-ID target replacement for retained dot caller"
        |> ignore
        commit engine "replace-word" "math.bump" []
        |> expectOk "commit replacement while validating retained dot caller"
        |> ignore
        equal bumpId (getWordId engine "math.bump") "compatible Flow replacement keeps the dot target ID"
        let history = dispatch engine "history" [ "word", jstr "math.bump" ] |> expectOk "inspect retained dot target history"
        equal 2 ((history.["data"]).AsArray().Count) "compatible Flow replacement advances target revision"
        equal "6" (evalFlow engine "client::dot(5)" |> expectOk "execute retained dot caller after target replacement" |> fun response -> stringValue (response.["data"].["stack"].[0])) "retained dot caller follows the same stable target ID"

        let store = Storage.create project
        let beforeAmbiguousAdd = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        let competingSource =
            "word other.bump(value: Int, amount: Int) -> Int {\n"
            + "    effects none\n"
            + "    add(value, amount)\n"
            + "}"
        let competingTest =
            "test other.bump/basic {\n"
            + "    other::bump(5, 1)\n"
            + "    => value other::bump(5, 1)\n"
            + "}"
        let ambiguous = defineFlow engine competingSource [ competingTest ] [] []
        check (not (succeeded ambiguous)) "adding a second compatible dot stage is rejected while the retained source becomes ambiguous"
        check ((errorCode ambiguous).StartsWith("FLOW_", StringComparison.Ordinal)) "retained dot ambiguity keeps a Flow diagnostic"
        let ambiguousOwner =
            try stringValue (ambiguous.["error"].["word"])
            with _ -> ""
        equal "client.dot" ambiguousOwner "retained source rebinding diagnostic identifies the original caller"
        check (not (isNull ambiguous.["error"].["span"])) "retained source rebinding diagnostic includes its call site"
        let afterAmbiguousAdd = Storage.load store |> Result.defaultWith (fun problem -> failwith problem.Message)
        equal beforeAmbiguousAdd.ManifestHash afterAmbiguousAdd.ManifestHash "failed retained-call rebind leaves current manifest unchanged"
        equal "6" (evalFlow engine "client::dot(5)" |> expectOk "evaluate retained caller after rejected dot-stage addition" |> fun response -> stringValue (response.["data"].["stack"].[0])) "failed source rebind leaves the previous executable snapshot active"

    let private testExpectationCoverageIsNotActualCoverage root =
        let word =
            "word coverage.same(value: Bool) -> Int {\n"
            + "    effects none\n"
            + "    if value { 7 } else { 7 }\n"
            + "}"
        let oppositeExpectation =
            "test coverage.same/true_actual {\n"
            + "    coverage::same(true)\n"
            + "    => value coverage::same(false)\n"
            + "}"
        let bothActualBranches =
            [ oppositeExpectation
              "test coverage.same/false_actual {\n"
              + "    coverage::same(false)\n"
              + "    => value coverage::same(true)\n"
              + "}" ]

        let missingBranchProject = Path.Combine(root, "expected-branch-not-coverage")
        let missingBranchEngine = Runtime.Engine(missingBranchProject, Set.empty, "2030-01-02T03:04:05Z")
        defineFlow missingBranchEngine word [ oppositeExpectation ] [] []
        |> expectOk "define library Flow word with opposite pure expectation"
        |> ignore
        let rejected = commit missingBranchEngine "commit" "coverage.same" [ "library", jbool true ]
        expectError "LIBRARY_COVERAGE_INCOMPLETE" rejected |> ignore
        let unchanged = Storage.load (Storage.create missingBranchProject) |> Result.defaultWith (fun problem -> failwith problem.Message)
        check unchanged.Manifest.IsNone "expectation-only branch trace does not publish the Flow library"
        dispatch missingBranchEngine "discard" [ "word", jstr "coverage.same" ] |> expectOk "discard incomplete library candidate" |> ignore

        let completeProject = Path.Combine(root, "actual-both-branches")
        let completeEngine = Runtime.Engine(completeProject, Set.empty, "2030-01-02T03:04:05Z")
        defineFlow completeEngine word bothActualBranches [] []
        |> expectOk "define library Flow word with tests that execute both actual branches"
        |> ignore
        let committed = commit completeEngine "commit" "coverage.same" [ "library", jbool true ]
        expectOk "commit library after both actual branches are covered" committed |> ignore
        let complete = Storage.load (Storage.create completeProject) |> Result.defaultWith (fun problem -> failwith problem.Message)
        check complete.Manifest.IsSome "actual execution of both branches permits the Flow library commit"

    [<EntryPoint>]
    let main _ =
        let root = newRoot ()
        try
            testExplicitFrontendAndDurableReload root
            testExplicitFrontendCannotFallBack root
            testGeneratedRecordCasesPersistBesideFlow root
            testStackGeneratedCasesSurviveV1Manifest root
            testStackOwnerMigrationToFlow root
            testReplacementCasAndRollback root
            testTemporaryPromotionAndTaskAbort root
            testNamedSnapshotRestoresFlowAndProviders root
            testPersistedBindingsAreVerified root
            testRetainedDotBindingAcrossReplacement root
            testExpectationCoverageIsNotActualCoverage root
            printfn $"Flow Runtime tests passed: 11 groups, {assertions} assertions."
            0
        finally
            if Directory.Exists root then Directory.Delete(root, true)
