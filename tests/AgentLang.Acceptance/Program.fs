namespace AgentLang.Acceptance

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open AgentLang

module Program =
    let mutable private assertions = 0

    let private check condition message =
        assertions <- assertions + 1
        if not condition then failwith message

    let private equal expected actual message =
        assertions <- assertions + 1
        if expected <> actual then
            failwith $"{message}: expected {expected}, got {actual}"

    let private jsonString (value: string) = JsonValue.Create(value) :> JsonNode
    let private jsonBool (value: bool) = JsonValue.Create(value) :> JsonNode

    let private arguments (values: (string * JsonNode) list) =
        let args = JsonObject()
        for name, value in values do
            args[name] <- if isNull value then null else value.DeepClone()
        args

    // This suite preserves pre-Flow Stack coverage. Pin the frontend at the
    // JSON protocol boundary so a runtime default change cannot change its intent.
    let private dispatch (engine: Runtime.Engine) operation values =
        let selected =
            if (operation = "define" || operation = "eval") && not (values |> List.exists (fun (name, _) -> name = "frontend")) then
                ("frontend", jsonString "stack") :: values
            else values
        engine.Dispatch(operation, arguments selected)

    let private expectOk label (response: JsonObject) =
        if not (response["ok"].GetValue<bool>()) then
            let message = response["text"].GetValue<string>()
            let code =
                try (response["error"]["code"]).GetValue<string>()
                with _ -> "unknown"
            failwith $"{label}: expected success, got {code}: {message}"
        response

    let private expectError expectedCode (response: JsonObject) =
        check (not (response["ok"].GetValue<bool>())) $"expected {expectedCode}, got success"
        let actualCode = (response["error"]["code"]).GetValue<string>()
        equal expectedCode actualCode "diagnostic code"
        response

    let private engine path capabilities =
        Runtime.Engine(path, (Set.ofList capabilities))

    let private define (runtime: Runtime.Engine) source =
        dispatch runtime "define" [ "source", jsonString source ]

    let private evaluate (runtime: Runtime.Engine) source =
        dispatch runtime "eval" [ "code", jsonString source ]

    let private stackValue (response: JsonObject) (index: int) =
        let dataNode = response["data"]
        let stackNode = dataNode["stack"]
        let values = stackNode.AsArray()
        values.[index].GetValue<string>()

    let private identityOf (response: JsonObject) =
        let data = response["data"]
        data["id"].GetValue<string>()

    let private stackType (response: JsonObject) (index: int) =
        let values = (response["data"]["stackTypes"]).AsArray()
        values.[index].GetValue<string>()

    let private assertAllTestsPassed label expectedCount (response: JsonObject) =
        let results = (response["data"]["results"]).AsArray()
        equal expectedCount results.Count $"{label} result count"
        for result in results do
            let name = result["name"].GetValue<string>()
            check (result["passed"].GetValue<bool>()) $"{label} test {name} passed"

    let private makeProject root name =
        let path = Path.Combine(root, name)
        Directory.CreateDirectory(path) |> ignore
        path

    let private exampleSource fileName =
        let path = Path.Combine(Environment.CurrentDirectory, "examples", "legacy", fileName)
        if not (File.Exists path) then failwith $"Could not find demo source at {path}."
        File.ReadAllText path

    let private demoSource () = exampleSource "customer.agent"

    let private testEvalAndJsonLines root =
        let runtime = engine (makeProject root "json-lines") []
        let result = evaluate runtime "10 20 add" |> expectOk "basic eval"
        equal "30" (stackValue result 0) "10 20 add"

        let input =
            new StringReader(
                "{\"op\":\"eval\",\"frontend\":\"stack\",\"code\":\"10 20 add\"}\n" +
                "{\"op\":\"words\"}\n")
        use output = new StringWriter()
        Protocol.serveJsonLines runtime input output
        let lines = output.ToString().TrimEnd().Split([| Environment.NewLine |], StringSplitOptions.None)
        equal 2 lines.Length "one JSON response per request line"
        let first = JsonNode.Parse(lines[0]).AsObject()
        check (first["ok"].GetValue<bool>()) "JSON response has ok=true"
        check (not (isNull first["kind"])) "JSON response has kind"
        check (not (isNull first["text"])) "JSON response has text"
        check (not (isNull first["data"])) "successful JSON response has data"
        let malformed = Protocol.dispatchLine runtime "{not-json"
        expectError "PROTOCOL_INVALID_JSON" malformed |> ignore
        let missingOperation = Protocol.dispatchLine runtime "{\"code\":\"10 20 add\"}"
        expectError "PROTOCOL_INVALID_REQUEST" missingOperation |> ignore

    let private testEffectsAndCapabilityBoundary root =
        let runtime = engine (makeProject root "effects") [ "fs.read"; "fs.write" ]
        let invalid = evaluate runtime "\"/should-not-exist\" \"value\" file.write 1 true add"
        expectError "TYPE_STACK_MISMATCH" invalid |> ignore
        let absent = evaluate runtime "\"/should-not-exist\" file.exists?" |> expectOk "effect was not run after type failure"
        equal "false" (stackValue absent 0) "compile-time failure leaves virtual file untouched"

        let undeclaredSource =
            """word undeclared.read : String -> String
    effects none
    file.read
end
"""
        expectError "EFFECT_UNDECLARED" (define runtime undeclaredSource) |> ignore

        let denyByDefault = engine (makeProject root "default-deny") []
        let denied = evaluate denyByDefault "\"/file\" \"text\" file.write"
        expectError "CAPABILITY_DENIED" denied |> ignore

        let preflight = engine (makeProject root "effect-preflight") [ "fs.read" ]
        expectError "CAPABILITY_DENIED" (evaluate preflight "\"/blocked\" \"text\" file.write") |> ignore
        let notWritten = evaluate preflight "\"/blocked\" file.exists?" |> expectOk "inspect virtual file after denied write"
        equal "false" (stackValue notWritten 0) "capability preflight denies before the effect runs"

    let private testCheckedArithmetic root =
        let runtime = engine (makeProject root "arithmetic") []
        expectError "RUNTIME_OVERFLOW" (evaluate runtime "9223372036854775807 1 add") |> ignore
        expectError "RUNTIME_RANGE" (evaluate runtime "9223372036854775808.0 float.to-int") |> ignore
        expectError "RUNTIME_RANGE" (evaluate runtime "9223372036854775807.0 float.round") |> ignore
        let lowerBoundary = evaluate runtime "-9223372036854775808.0 float.to-int" |> expectOk "convert inclusive lower Int64 boundary"
        equal "-9223372036854775808" (stackValue lowerBoundary 0) "Float-to-Int accepts inclusive lower boundary"

    let private testDemoRecordLocalsAndBranches root =
        let runtime = engine (makeProject root "demo") []
        define runtime (demoSource ()) |> expectOk "define demo" |> ignore

        let premium = evaluate runtime "\"premium\" 100.0 customer.new customer.premium?" |> expectOk "premium record check"
        equal "true" (stackValue premium 0) "premium predicate"
        let discounted = evaluate runtime "\"premium\" 100.0 customer.new customer.discounted-balance" |> expectOk "premium discount"
        equal "90" (stackValue discounted 0) "discounted balance"
        let regular = evaluate runtime "\"regular\" 100.0 customer.new customer.discounted-balance" |> expectOk "regular balance"
        equal "100" (stackValue regular 0) "regular customer keeps balance"

        let wrongFieldType = evaluate runtime "\"premium\" \"100\" customer.new"
        expectError "TYPE_STACK_MISMATCH" wrongFieldType |> ignore

        let testResults = dispatch runtime "test-all" [] |> expectOk "demo tests"
        assertAllTestsPassed "demo" 4 testResults

        let branchLocal =
            """word branch-local : Bool -> Int
    effects none
    if
        7 let selected
    else
        9 let selected
    end
    $selected
end
"""
        define runtime branchLocal |> expectOk "define branch-local word" |> ignore
        equal "7" (stackValue (evaluate runtime "true branch-local" |> expectOk "true branch-local") 0) "true branch local propagated"
        equal "9" (stackValue (evaluate runtime "false branch-local" |> expectOk "false branch-local") 0) "false branch local propagated"

        let bottomToTop = evaluate runtime "7 true\nif\n2\nelse\n3\nend" |> expectOk "bottom-to-top stack through if"
        equal "7" (stackValue bottomToTop 0) "preexisting stack bottom survives if"
        equal "2" (stackValue bottomToTop 1) "if result is appended above preexisting stack"
        let invalidBranch =
            """word invalid-branch : Bool -> Int
    effects none
    if
        1
    else
    end
end
"""
        expectError "TYPE_BRANCH_STACK_MISMATCH" (define runtime invalidBranch) |> ignore

    let private testProjectCommitRequirements root =
        let untestedPath = makeProject root "untested"
        let untested = engine untestedPath []
        let untestedSource =
            """word plus-one : Int -> Int
    effects none
    1 add
end
"""
        define untested untestedSource |> expectOk "define untested word" |> ignore
        expectError "COMMIT_TEST_REQUIRED" (dispatch untested "commit" [ "word", jsonString "plus-one" ]) |> ignore

        let failingPath = makeProject root "failing-commit"
        let failing = engine failingPath []
        let failingSource =
            """word plus-one : Int -> Int
    effects none
    1 add
end

test plus-one/off-by-one
    10 plus-one
    => 12
end
"""
        define failing failingSource |> expectOk "define failing candidate" |> ignore
        expectError "COMMIT_TESTS_FAILED" (dispatch failing "commit" [ "word", jsonString "plus-one" ]) |> ignore
        let afterFailure = engine failingPath []
        expectError "NAME_UNKNOWN_WORD" (dispatch afterFailure "source" [ "word", jsonString "plus-one" ]) |> ignore

        let passingPath = makeProject root "passing-commit"
        let passing = engine passingPath []
        let passingSource =
            """word plus-one : Int -> Int
    effects none
    1 add
end

test plus-one/basic
    10 plus-one
    => 11
end
"""
        define passing passingSource |> expectOk "define passing candidate" |> ignore
        dispatch passing "test-all" [] |> expectOk "attached project test" |> ignore
        dispatch passing "commit" [ "word", jsonString "plus-one" ] |> expectOk "commit project word" |> ignore

        let reloaded = engine passingPath []
        let result = evaluate reloaded "10 plus-one" |> expectOk "reload committed word"
        equal "11" (stackValue result 0) "reloaded result"
        let persistedTest = dispatch reloaded "test-all" [] |> expectOk "reload attached test"
        assertAllTestsPassed "reloaded" 1 persistedTest

    let private testCandidateDependenciesPersist root =
        let projectPath = makeProject root "dependencies"
        let runtime = engine projectPath []
        let dependentSource =
            """word add-two : Int -> Int
    effects none
    2 add
end

word times-three : Int -> Int
    effects none
    add-two
    3 multiply
end

test add-two/basic
    1 add-two
    => 3
end

test times-three/basic
    1 times-three
    => 9
end
"""
        define runtime dependentSource |> expectOk "define dependent candidates" |> ignore

        let deps = dispatch runtime "dependencies" [ "word", jsonString "times-three" ] |> expectOk "inspect dependencies"
        check ((deps["data"]["dependencies"]).ToJsonString().Contains("add-two")) "dependency graph includes add-two"
        dispatch runtime "commit" [ "word", jsonString "add-two" ] |> expectOk "commit dependency first" |> ignore
        dispatch runtime "commit" [ "word", jsonString "times-three" ] |> expectOk "commit dependent caller" |> ignore

        let reloaded = engine projectPath []
        let value = evaluate reloaded "1 times-three" |> expectOk "reload dependent words"
        equal "9" (stackValue value 0) "dependent word works after reload"
        let description = dispatch reloaded "describe" [ "word", jsonString "add-two" ] |> expectOk "reload helper metadata"
        equal "persistent" ((description["data"]["status"]).GetValue<string>()) "helper was committed with its caller"

    let private testSelectedMetadataDependenciesPersist root =
        let projectPath = makeProject root "selected-metadata-dependencies"
        let runtime = engine projectPath []
        let source =
            """record ProbeToken
    field value Int
end

word candidate-helper : Int -> Int
    effects none
    1 add
end

test candidate-helper/basic
    1 candidate-helper
    => 2
end

word metadata-target : Int -> Int
    effects none
    1 add
end

test metadata-target/helper-case
    option.none<ProbeToken> drop
    41 candidate-helper metadata-target
    => 43
end

example metadata-target/helper-example
    option.none<ProbeToken> drop
    41 candidate-helper metadata-target
    => 43
end

word unrelated-candidate : Int -> Int
    effects none
    2 add
end

test unrelated-candidate/basic
    1 unrelated-candidate
    => 3
end
"""
        define runtime source |> expectOk "define candidate metadata dependencies and unrelated candidate" |> ignore
        dispatch runtime "commit" [ "word", jsonString "metadata-target" ]
        |> expectOk "commit target with dependencies used only by attached metadata"
        |> ignore

        let firstReload = engine projectPath []
        let value = evaluate firstReload "41 candidate-helper metadata-target" |> expectOk "reload helper referenced by target test"
        equal "43" (stackValue value 0) "candidate helper survives when only target metadata calls it"
        dispatch firstReload "test" [ "word", jsonString "metadata-target" ]
        |> expectOk "run target test after metadata dependency reload"
        |> assertAllTestsPassed "metadata target" 1
        dispatch firstReload "test" [ "word", jsonString "candidate-helper" ]
        |> expectOk "run helper's attached test after reload"
        |> assertAllTestsPassed "metadata helper" 1
        let examples = dispatch firstReload "examples" [ "word", jsonString "metadata-target" ] |> expectOk "inspect durable target example"
        equal "[\"helper-example\"]" (examples["data"].ToJsonString()) "target example depending on candidate helper survives reload"
        expectError "NAME_UNKNOWN_WORD" (dispatch firstReload "source" [ "word", jsonString "unrelated-candidate" ]) |> ignore
        let unstoredTests = dispatch firstReload "tests" [ "word", jsonString "unrelated-candidate" ] |> expectOk "inspect unrelated tests in committed projection"
        equal "[]" (unstoredTests["data"].ToJsonString()) "unrelated staged metadata is not included in target commit"
        let stagedUnrelated = dispatch runtime "describe" [ "word", jsonString "unrelated-candidate" ] |> expectOk "inspect untouched unrelated candidate"
        equal "candidate" ((stagedUnrelated["data"]["status"]).GetValue<string>()) "unrelated candidate remains staged in the current engine"
        let stagedTests = dispatch runtime "tests" [ "word", jsonString "unrelated-candidate" ] |> expectOk "inspect staged unrelated test"
        equal "[\"basic\"]" (stagedTests["data"].ToJsonString()) "unrelated staged test remains available in the current engine"
        dispatch runtime "commit" [ "word", jsonString "unrelated-candidate" ]
        |> expectOk "commit the unrelated candidate separately"
        |> ignore
        let unrelatedReload = engine projectPath []
        evaluate unrelatedReload "1 unrelated-candidate" |> expectOk "reload separately committed unrelated candidate" |> ignore

        let replacement =
            """word candidate-helper : Int -> Int
    effects none
    2 add
end

test candidate-helper/basic
    1 candidate-helper
    => 3
end

test metadata-target/helper-case
    option.none<ProbeToken> drop
    41 candidate-helper metadata-target
    => 44
end

example metadata-target/helper-example
    option.none<ProbeToken> drop
    41 candidate-helper metadata-target
    => 44
end
"""
        define unrelatedReload replacement |> expectOk "stage a helper replacement used only by target metadata" |> ignore
        dispatch unrelatedReload "commit" [ "word", jsonString "metadata-target" ]
        |> expectOk "commit metadata-selected helper replacement"
        |> ignore
        let replacementReload = engine projectPath []
        let replacementValue = evaluate replacementReload "41 candidate-helper metadata-target" |> expectOk "reload committed helper replacement"
        equal "44" (stackValue replacementValue 0) "metadata-scoped replacement helper persists with its updated target test"
        dispatch replacementReload "test" [ "word", jsonString "metadata-target" ]
        |> expectOk "run target test after helper replacement reload"
        |> assertAllTestsPassed "replacement metadata target" 1
        dispatch replacementReload "test" [ "word", jsonString "candidate-helper" ]
        |> expectOk "run helper test after helper replacement reload"
        |> assertAllTestsPassed "replacement metadata helper" 1

    let private testTemporaryMetadataDependencyRejected root =
        let projectPath = makeProject root "temporary-metadata-dependency"
        let runtime = engine projectPath []
        let temporarySource =
            """word temporary-test-helper : Int -> Int
    effects none
    1 add
end
"""
        dispatch runtime "define" [ "source", jsonString temporarySource; "temporary", jsonBool true ]
        |> expectOk "define temporary helper referenced by candidate test"
        |> ignore
        let source =
            """word temporary-metadata-target : Int -> Int
    effects none
    1 add
end

test temporary-metadata-target/uses-temporary
    1 temporary-test-helper temporary-metadata-target
    => 3
end
"""
        define runtime source |> expectOk "define target test referencing temporary helper" |> ignore
        let rejection = dispatch runtime "commit" [ "word", jsonString "temporary-metadata-target" ]
        expectError "COMMIT_TEMPORARY_TEST_DEPENDENCY" rejection |> ignore
        let rejectionError = rejection["error"]
        let actualNode = rejectionError["actual"]
        let actual = actualNode.ToJsonString()
        check (actual.Contains("temporary-test-helper", StringComparison.Ordinal)) "temporary dependency diagnostic names the helper"
        let reloaded = engine projectPath []
        expectError "NAME_UNKNOWN_WORD" (dispatch reloaded "source" [ "word", jsonString "temporary-metadata-target" ]) |> ignore

    let private testTemporaryWordsStaySessionOnly root =
        let projectPath = makeProject root "temporary-isolation"
        let runtime = engine projectPath []
        let temporarySource =
            """word temporary-helper : Int -> Int
    effects none
    5 add
end
"""
        dispatch runtime "define" [ "source", jsonString temporarySource; "temporary", jsonBool true ]
        |> expectOk "define temporary helper"
        |> ignore

        let durableSource =
            """word durable : Int -> Int
    effects none
    1 add
end

test durable/basic
    4 durable
    => 5
end
"""
        define runtime durableSource |> expectOk "define durable candidate" |> ignore
        dispatch runtime "commit" [ "word", jsonString "durable" ] |> expectOk "commit unrelated candidate" |> ignore

        let reloaded = engine projectPath []
        expectError "NAME_UNKNOWN_WORD" (dispatch reloaded "source" [ "word", jsonString "temporary-helper" ]) |> ignore
        let value = evaluate reloaded "4 durable" |> expectOk "durable word reload"
        equal "5" (stackValue value 0) "durable word persists beside session temporary"

    let private testScopedReplacementAndDiscard root =
        let projectPath = makeProject root "scoped-replacement"
        let runtime = engine projectPath []
        let original =
            """word f : Int -> Int
    effects none
    1 add
end

test f/original
    10 f
    => 11
end
"""
        define runtime original |> expectOk "define original f" |> ignore
        dispatch runtime "commit" [ "word", jsonString "f" ] |> expectOk "commit original f" |> ignore

        let staged =
            """word f : Int -> Int
    effects none
    2 add
end

test f/replacement
    10 f
    => 12
end

word g : Int -> Int
    effects none
    3 add
end

test g/basic
    10 g
    => 13
end
"""
        define runtime staged |> expectOk "stage replacement f and unrelated g" |> ignore
        dispatch runtime "test" [ "word", jsonString "g" ]
        |> expectOk "test unrelated g"
        |> assertAllTestsPassed "g" 1
        dispatch runtime "commit" [ "word", jsonString "g" ] |> expectOk "commit unrelated g" |> ignore

        let stagedValue = evaluate runtime "10 f" |> expectOk "unselected replacement remains staged"
        equal "12" (stackValue stagedValue 0) "committing g leaves staged replacement f active"
        let afterCommit = engine projectPath []
        let persistedValue = evaluate afterCommit "10 f" |> expectOk "reload after committing g"
        equal "11" (stackValue persistedValue 0) "unselected replacement f was not persisted"
        let durableSource = dispatch afterCommit "source" [ "word", jsonString "f" ] |> expectOk "inspect persisted original f"
        let sourceText = durableSource["data"].GetValue<string>()
        check (sourceText.Contains("    1" + Environment.NewLine + "    add", StringComparison.Ordinal)) "unselected replacement backup remains in durable dictionary"
        check (not (sourceText.Contains("    2" + Environment.NewLine + "    add", StringComparison.Ordinal))) "unselected replacement body is absent from durable dictionary"
        let originalTests = dispatch afterCommit "test" [ "word", jsonString "f" ] |> expectOk "reload original f test"
        assertAllTestsPassed "original f" 1 originalTests

        let secondReplacement =
            """word f : Int -> Int
    effects none
    4 add
end

test f/discarded
    10 f
    => 14
end
"""
        define afterCommit secondReplacement |> expectOk "stage replacement for discard" |> ignore
        dispatch afterCommit "discard" [ "word", jsonString "f" ] |> expectOk "discard replacement f" |> ignore
        let afterDiscard = evaluate afterCommit "10 f" |> expectOk "evaluate restored f after discard"
        equal "11" (stackValue afterDiscard 0) "discard restores original persistent f"
        let discardReload = engine projectPath []
        let afterDiscardReload = evaluate discardReload "10 f" |> expectOk "reload after discard"
        equal "11" (stackValue afterDiscardReload 0) "discard leaves original f on disk"
        let restoredTests = dispatch discardReload "test" [ "word", jsonString "f" ] |> expectOk "reload tests after discard"
        assertAllTestsPassed "discarded f" 1 restoredTests

    let private testScopedTestMetadataPersistence root =
        let projectPath = makeProject root "scoped-test-metadata"
        let runtime = engine projectPath []
        let originalG =
            """word g : Int -> Int
    effects none
    1 add
end

test g/original
    1 g
    => 2
end
"""
        define runtime originalG |> expectOk "define g with its original test" |> ignore
        dispatch runtime "commit" [ "word", jsonString "g" ] |> expectOk "commit original g" |> ignore

        let stagedFailure =
            """test g/new-failing
    1 g
    => 999
end
"""
        define runtime stagedFailure |> expectOk "stage failing test metadata for persistent g" |> ignore

        let testedH =
            """word h : Int -> Int
    effects none
    2 add
end

test h/basic
    1 h
    => 3
end
"""
        define runtime testedH |> expectOk "define tested h" |> ignore
        dispatch runtime "commit" [ "word", jsonString "h" ] |> expectOk "commit h without g's staged test" |> ignore

        let reloaded = engine projectPath []
        let persistedGTests = dispatch reloaded "tests" [ "word", jsonString "g" ] |> expectOk "inspect g tests after scoped h commit"
        let persistedNames = persistedGTests["data"].AsArray() |> Seq.map (fun value -> value.GetValue<string>()) |> Seq.toList
        equal [ "original" ] persistedNames "unselected g test metadata is not persisted by committing h"
        let originalResult = dispatch reloaded "test" [ "word", jsonString "g" ] |> expectOk "run reloaded original g test"
        assertAllTestsPassed "reloaded g" 1 originalResult

        expectError "COMMIT_TESTS_FAILED" (dispatch runtime "commit" [ "word", jsonString "g" ])
        |> ignore

    let private testReplacementCallerTests root =
        let projectPath = makeProject root "replacement-caller-tests"
        let runtime = engine projectPath []
        let baseline =
            """word replacement-base : Int -> Int
    effects none
    1 add
end

test replacement-base/basic
    10 replacement-base
    => 11
end

word replacement-caller : Int -> Int
    effects none
    replacement-base
end

test replacement-caller/basic
    10 replacement-caller
    => 11
end
"""
        define runtime baseline |> expectOk "define replacement dependency and caller" |> ignore
        dispatch runtime "commit" [] |> expectOk "commit replacement dependency and caller" |> ignore

        let replacement =
            """word replacement-base : Int -> Int
    effects none
    2 add
end

test replacement-base/basic
    10 replacement-base
    => 12
end
"""
        define runtime replacement |> expectOk "stage replacement dependency" |> ignore
        let dependencyInfo = dispatch runtime "dependencies" [ "word", jsonString "replacement-caller" ] |> expectOk "inspect replacement caller dependencies"
        let dependencyNames = dependencyInfo["data"]["dependencies"]
        check (dependencyNames.ToJsonString().Contains("replacement-base", StringComparison.Ordinal)) "caller dependency analysis names the replaced word"
        let impactedCallerTest = dispatch runtime "test" [ "word", jsonString "replacement-caller" ] |> expectOk "run caller test against staged replacement"
        let impactedResults = impactedCallerTest["data"]["results"]
        let impactedFirstResult = impactedResults[0]
        let impactedPassed = impactedFirstResult["passed"].GetValue<bool>()
        check (not impactedPassed) "the old caller test fails against the staged replacement"
        for operation in [ "commit"; "commit-word"; "replace-word" ] do
            expectError "COMMIT_TESTS_FAILED" (dispatch runtime operation [ "word", jsonString "replacement-base" ])
            |> ignore
        let beforeCallerUpdate = evaluate runtime "10 replacement-caller" |> expectOk "failed replacement leaves candidate view available"
        equal "12" (stackValue beforeCallerUpdate 0) "staged replacement is visible in the session"
        let durableBeforeCallerUpdate = engine projectPath []
        let oldDurableValue = evaluate durableBeforeCallerUpdate "10 replacement-caller" |> expectOk "failed replacement leaves committed caller intact"
        equal "11" (stackValue oldDurableValue 0) "failed replacement does not publish changed dependency"

        let updatedCallerTest =
            """test replacement-caller/basic
    10 replacement-caller
    => 12
end
"""
        define runtime updatedCallerTest |> expectOk "stage passing caller test update" |> ignore
        let committed = dispatch runtime "replace-word" [ "word", jsonString "replacement-base" ] |> expectOk "commit replacement after caller tests pass"
        let committedResults = committed["data"].AsArray()
        check (committedResults |> Seq.exists (fun value -> value.GetValue<string>() = "replacement-caller/basic")) "replacement reports the checked caller test"
        let updatedValue = evaluate runtime "10 replacement-caller" |> expectOk "evaluate updated caller after replacement"
        equal "12" (stackValue updatedValue 0) "caller observes committed replacement"
        let reloaded = engine projectPath []
        let reloadedValue = evaluate reloaded "10 replacement-caller" |> expectOk "reload replacement and caller"
        equal "12" (stackValue reloadedValue 0) "replacement and caller persist together"
        dispatch reloaded "test" [ "word", jsonString "replacement-caller" ]
        |> expectOk "test reloaded updated caller"
        |> assertAllTestsPassed "replacement caller" 1

        let taskRuntime = engine projectPath []
        dispatch taskRuntime "task.begin" [ "goal", jsonString "reject unsafe replacement through task.commit" ]
        |> expectOk "begin task for caller guard"
        |> ignore
        let taskReplacement =
            """word replacement-base : Int -> Int
    effects none
    3 add
end

test replacement-base/basic
    10 replacement-base
    => 13
end
"""
        define taskRuntime taskReplacement |> expectOk "stage task replacement" |> ignore
        expectError "COMMIT_TESTS_FAILED" (dispatch taskRuntime "task.commit" []) |> ignore
        dispatch taskRuntime "task.abort" [] |> expectOk "abort task after caller test rejection" |> ignore
        let taskReload = engine projectPath []
        let rolledBack = evaluate taskReload "10 replacement-caller" |> expectOk "reload after rejected task replacement"
        equal "12" (stackValue rolledBack 0) "task.commit caller-test rejection does not publish replacement"

        let transitivePath = makeProject root "replacement-transitive-static-callback"
        let transitive = engine transitivePath []
        let transitiveBaseline =
            """word gate-base : Int -> Bool
    effects none
    drop
    true
end

test gate-base/basic
    1 gate-base
    => true
end

word gate-caller : Int -> Bool
    effects none
    gate-base
end

test gate-caller/basic
    1 gate-caller
    => true
end

word gate-ancestor : Int -> Int
    effects none
    drop
    1 list.singleton<Int>
    list.filter gate-caller
    list.count
end

test gate-ancestor/static-callback
    7 gate-ancestor
    => 1
end
"""
        define transitive transitiveBaseline |> expectOk "define static callback dependency chain" |> ignore
        dispatch transitive "commit" [] |> expectOk "commit static callback dependency chain" |> ignore
        let changedBaseAndCaller =
            """word gate-base : Int -> Bool
    effects none
    drop
    false
end

test gate-base/basic
    1 gate-base
    => false
end

test gate-caller/basic
    1 gate-caller
    => false
end
"""
        define transitive changedBaseAndCaller |> expectOk "stage base and direct caller test updates" |> ignore
        let blockedByAncestor = dispatch transitive "replace-word" [ "word", jsonString "gate-base" ]
        expectError "COMMIT_TESTS_FAILED" blockedByAncestor |> ignore
        let blockedError = blockedByAncestor["error"]
        let blockedActual = blockedError["actual"]
        let failedNames = blockedActual.ToJsonString()
        check (failedNames.Contains("gate-ancestor/static-callback", StringComparison.Ordinal)) "transitive static callback ancestor test blocks replacement"
        let oldAncestor = engine transitivePath []
        let oldResult = evaluate oldAncestor "7 gate-ancestor" |> expectOk "reload unchanged ancestor after blocked replacement"
        equal "1" (stackValue oldResult 0) "failed transitive caller check preserves durable dependency chain"
        let updatedAncestorTest =
            """test gate-ancestor/static-callback
    7 gate-ancestor
    => 0
end
"""
        define transitive updatedAncestorTest |> expectOk "stage passing ancestor test update" |> ignore
        dispatch transitive "replace-word" [ "word", jsonString "gate-base" ]
        |> expectOk "commit replacement after direct and transitive caller tests pass"
        |> ignore
        let transitiveReload = engine transitivePath []
        let updatedAncestor = evaluate transitiveReload "7 gate-ancestor" |> expectOk "reload updated static callback chain"
        equal "0" (stackValue updatedAncestor 0) "transitive replacement and callback tests persist together"
        dispatch transitiveReload "test-all" [] |> expectOk "run reloaded transitive callback tests" |> assertAllTestsPassed "transitive callback reload" 3

    let private testWordIdentityLifecycle root =
        let projectPath = makeProject root "word-identities"
        let runtime = engine projectPath []
        let temporarySource =
            """word stable-item : Int -> Int
    effects none
    doc "original implementation"
    1 add
end

test stable-item/original
    1 stable-item
    => 2
end
"""
        dispatch runtime "define" [ "source", jsonString temporarySource; "temporary", jsonBool true ]
        |> expectOk "define temporary word with test"
        |> ignore
        let originalIdentity = dispatch runtime "describe" [ "word", jsonString "stable-item" ] |> expectOk "inspect temporary identity" |> identityOf
        check (originalIdentity.StartsWith("word_", StringComparison.Ordinal)) "user identity has word_ prefix"
        check (originalIdentity.Length = 37) "user identity uses a 32-hex GUID suffix"
        let guidSuffixIsValid =
            try Guid.ParseExact(originalIdentity.Substring(5), "N") |> ignore; true
            with _ -> false
        check guidSuffixIsValid "user identity suffix parses as a compact GUID"

        dispatch runtime "promote" [ "word", jsonString "stable-item" ] |> expectOk "promote temporary identity" |> ignore
        let candidateIdentity = dispatch runtime "describe" [ "word", jsonString "stable-item" ] |> expectOk "inspect candidate identity" |> identityOf
        equal originalIdentity candidateIdentity "promotion preserves identity"
        dispatch runtime "commit" [ "word", jsonString "stable-item" ] |> expectOk "commit identity-bearing word" |> ignore
        let persistentIdentity = dispatch runtime "describe" [ "word", jsonString "stable-item" ] |> expectOk "inspect persistent identity" |> identityOf
        equal originalIdentity persistentIdentity "commit preserves identity"
        let listedWords = dispatch runtime "words" [] |> expectOk "list identity-bearing words"
        let listData = listedWords["data"]
        let wordEntries = (listData["words"]).AsArray()
        let listedIdentity =
            wordEntries
            |> Seq.find (fun item -> item["name"].GetValue<string>() = "stable-item")
            |> fun item -> item["id"].GetValue<string>()
        equal originalIdentity listedIdentity "words exposes the stable identity"

        let replacement =
            """word stable-item : Int -> Int
    effects none
    doc "replacement implementation"
    1 add
end

test stable-item/replacement
    2 stable-item
    => 3
end
"""
        define runtime replacement |> expectOk "stage replacement" |> ignore
        let replacementIdentity = dispatch runtime "describe" [ "word", jsonString "stable-item" ] |> expectOk "inspect replacement identity" |> identityOf
        equal originalIdentity replacementIdentity "replacement retains persistent identity"
        dispatch runtime "discard" [ "word", jsonString "stable-item" ] |> expectOk "discard replacement" |> ignore
        let restored = dispatch runtime "describe" [ "word", jsonString "stable-item" ] |> expectOk "inspect restored identity"
        equal originalIdentity (identityOf restored) "discard restores original identity"
        equal "original implementation" ((restored["data"]["documentation"]).GetValue<string>()) "discard restores original definition"

        let throwaway =
            """word throwaway : Int -> Int
    effects none
    1 add
end

test throwaway/basic
    1 throwaway
    => 2
end
"""
        define runtime throwaway |> expectOk "define throwaway identity" |> ignore
        let discardedIdentity = dispatch runtime "describe" [ "word", jsonString "throwaway" ] |> expectOk "inspect throwaway identity" |> identityOf
        dispatch runtime "discard" [ "word", jsonString "throwaway" ] |> expectOk "discard new word identity" |> ignore
        define runtime throwaway |> expectOk "redefine throwaway" |> ignore
        let newIdentity = dispatch runtime "describe" [ "word", jsonString "throwaway" ] |> expectOk "inspect redefined identity" |> identityOf
        check (discardedIdentity <> newIdentity) "discarding a new definition removes its identity"
        check (newIdentity <> originalIdentity) "distinct user words have distinct identities"

        let primitiveId = dispatch runtime "describe" [ "word", jsonString "add" ] |> expectOk "inspect primitive identity" |> identityOf
        check (primitiveId.StartsWith("primitive_", StringComparison.Ordinal)) "primitive identity is deterministic by builtin name"
        let otherRuntime = engine (makeProject root "word-identities-other") []
        let primitiveIdAgain = dispatch otherRuntime "describe" [ "word", jsonString "add" ] |> expectOk "inspect primitive identity in fresh engine" |> identityOf
        equal primitiveId primitiveIdAgain "primitive identities are deterministic across engines"

        define runtime "record Receipt\n    field amount Int\nend\n" |> expectOk "define generated-word owner" |> ignore
        dispatch runtime "commit" [ "word", jsonString "Receipt" ] |> expectOk "commit generated-word owner" |> ignore
        let generatedId = dispatch runtime "describe" [ "word", jsonString "receipt.new" ] |> expectOk "inspect generated identity" |> identityOf
        check (generatedId.StartsWith("generated_", StringComparison.Ordinal)) "generated identity is deterministic by generated word name"
        let generatedIdReloaded =
            engine projectPath []
            |> fun reloaded -> dispatch reloaded "describe" [ "word", jsonString "receipt.new" ]
            |> expectOk "inspect generated identity after reload"
            |> identityOf
        equal generatedId generatedIdReloaded "generated identities are deterministic across reload"

    let private testRuntimeErrorExpectations root =
        let runtime = engine (makeProject root "runtime-error-expectations") []
        let source =
            """word guarded-divide : Int -> Int
    effects none
    1 swap divide
end

test guarded-divide/divide-by-zero
    0 guarded-divide
    => error RUNTIME_DIVIDE_BY_ZERO
end

test guarded-divide/completes
    2 guarded-divide
    => error RUNTIME_DIVIDE_BY_ZERO
end

test guarded-divide/wrong-code
    0 guarded-divide
    => error RUNTIME_OVERFLOW
end
"""
        define runtime source |> expectOk "define runtime-error tests" |> ignore
        let testResponse = dispatch runtime "test" [ "word", jsonString "guarded-divide" ] |> expectOk "run runtime-error tests"
        let results = (testResponse["data"]["results"]).AsArray()
        equal 3 results.Count "runtime-error result count"
        let byName name = results |> Seq.find (fun result -> result["name"].GetValue<string>() = name)
        let matching = byName "divide-by-zero"
        check (matching["passed"].GetValue<bool>()) "exact runtime diagnostic code passes"
        equal "error RUNTIME_DIVIDE_BY_ZERO" (matching["expected"].GetValue<string>()) "expected-error JSON keeps an explicit expectation"
        equal "RUNTIME_DIVIDE_BY_ZERO" (matching["expectedErrorCode"].GetValue<string>()) "expected-error JSON exposes its code"
        check (not (matching.AsObject().ContainsKey("actualStructured"))) "runtime-error JSON keeps its legacy shape"
        check (not (matching.AsObject().ContainsKey("expectedStructured"))) "runtime-error JSON has no value-expression DTO"
        let completed = byName "completes"
        check (not (completed["passed"].GetValue<bool>())) "normal completion does not pass an expected-error test"
        equal "TEST_EXPECTED_RUNTIME_ERROR" (completed["errorCode"].GetValue<string>()) "normal completion reports an expected-error mismatch"
        let wrongCode = byName "wrong-code"
        check (not (wrongCode["passed"].GetValue<bool>())) "a different runtime diagnostic does not pass"
        equal "RUNTIME_DIVIDE_BY_ZERO" (wrongCode["errorCode"].GetValue<string>()) "wrong runtime code remains observable"

        let compileNegative =
            """test guarded-divide/compile-negative
    "text" 1 add
    => error TYPE_STACK_MISMATCH
end
"""
        expectError "TYPE_STACK_MISMATCH" (define runtime compileNegative) |> ignore
        let attached = dispatch runtime "tests" [ "word", jsonString "guarded-divide" ] |> expectOk "check compile-negative test was not staged"
        equal 3 ((attached["data"]).AsArray().Count) "compile-time mismatch cannot be staged as an expected runtime error"

    let private testValueExpressionExpectations root =
        let runtime = engine (makeProject root "value-expression-json") []
        let source =
            """record ExpectedMarker
    field value Int
end

word marker-target : Unit -> Option<ExpectedMarker>
    effects none
    drop option.none<ExpectedMarker>
end

test marker-target/none
    unit marker-target
    => value
        option.none<ExpectedMarker>
end

test marker-target/branch
    unit marker-target
    => value
        true
        if
            option.none<ExpectedMarker>
        else
            option.none<ExpectedMarker>
        end
end

word literal-target : Unit -> Int
    effects none
    drop 5
end

test literal-target/basic
    unit literal-target
    => 5
end
"""
        define runtime source |> expectOk "define nominal value-expression tests" |> ignore

        let response = dispatch runtime "test" [ "word", jsonString "marker-target" ] |> expectOk "run expression expectation tests"
        assertAllTestsPassed "expression expectation" 2 response
        let result = (response["data"]["results"]).AsArray()[0]
        equal "value-expression" (result["expectedKind"].GetValue<string>()) "expression expectation kind is explicit"
        equal "Option<ExpectedMarker>" (result["expectedType"].GetValue<string>()) "nominal container expectation type is visible"
        check (not (isNull result["actualStructured"])) "actual value has a bounded structured DTO"
        check (not (isNull result["expectedStructured"])) "expected value has a bounded structured DTO"
        equal 1 ((result["actualStructured"]["formatVersion"]).GetValue<int>()) "actual DTO uses ValueInspection format version"
        equal 1 ((result["expectedStructured"]["formatVersion"]).GetValue<int>()) "expected DTO uses ValueInspection format version"
        let actualValue = (result["actualStructured"]["values"]).AsArray()[0]
        let expectedValue = (result["expectedStructured"]["values"]).AsArray()[0]
        equal "option" (actualValue["kind"].GetValue<string>()) "actual option DTO is typed"
        equal "none" (actualValue["case"].GetValue<string>()) "actual none case is explicit"
        equal "nominal" ((expectedValue["elementType"]["kind"]).GetValue<string>()) "expected option preserves its nominal element type"
        equal "ExpectedMarker" ((expectedValue["elementType"]["name"]).GetValue<string>()) "nominal DTO names the exact type"
        let expressionActual = (result["actual"]).AsArray()
        equal "none" ((expressionActual[0]).GetValue<string>()) "legacy display actual stays available"

        let literal = dispatch runtime "test" [ "word", jsonString "literal-target" ] |> expectOk "run literal expectation"
        let literalResult = (literal["data"]["results"]).AsArray()[0]
        equal "5" (literalResult["expected"].GetValue<string>()) "literal expectation JSON keeps its legacy value"
        let literalActual = (literalResult["actual"]).AsArray()
        equal "5" ((literalActual[0]).GetValue<string>()) "literal result display stays unchanged"
        check (not (literalResult.AsObject().ContainsKey("expectedKind"))) "literal result has no expression discriminator"
        check (not (literalResult.AsObject().ContainsKey("actualStructured"))) "literal result keeps its legacy DTO shape"

        let mismatch = engine (makeProject root "value-expression-type-mismatch") []
        let mismatchedSource =
            """record ExpectedLeft
    field value Int
end
record ExpectedRight
    field value Int
end
word mismatch-target : Unit -> Option<ExpectedLeft>
    effects none
    drop option.none<ExpectedLeft>
end
test mismatch-target/wrong-nominal
    unit mismatch-target
    => value
        option.none<ExpectedRight>
end
"""
        expectError "TEST_EXPECTED_STACK" (define mismatch mismatchedSource) |> ignore
        expectError "NAME_UNKNOWN_WORD" (dispatch mismatch "describe" [ "word", jsonString "mismatch-target" ])
        |> ignore

        let effectPath = makeProject root "value-expression-no-effects"
        let effectful = engine effectPath [ "fs.read"; "fs.write" ]
        let effectfulExpectation =
            """word effect-target : Unit -> Unit
    effects none
    drop unit
end
test effect-target/writes-in-expectation
    unit effect-target
    => value
        "/expected-expression-must-not-run" "bad" file.write
end
"""
        expectError "TEST_EXPECTED_VALUE_EFFECTS" (define effectful effectfulExpectation) |> ignore
        let absent = evaluate effectful "\"/expected-expression-must-not-run\" file.exists?" |> expectOk "check rejected expectation did not run"
        equal "false" (stackValue absent 0) "effectful expected expression is rejected before it can write"

    let private testExpectedExpressionDependencies root =
        let projectPath = makeProject root "value-expression-metadata-closure"
        let runtime = engine projectPath []
        let source =
            """record ExpectedOnlyType
    field value Int
end

word expected-only-helper : Int -> Bool
    effects none
    drop true
end

test expected-only-helper/basic
    1 expected-only-helper
    => true
end

word metadata-target : Unit -> Bool
    effects none
    drop true
end

test metadata-target/helper-only
    unit metadata-target
    => value
        1 expected-only-helper
end

test metadata-target/type-only
    unit metadata-target
    => value
        option.none<ExpectedOnlyType>
        drop
        true
end
"""
        define runtime source |> expectOk "define expected-only word and type dependencies" |> ignore
        dispatch runtime "commit" [ "word", jsonString "metadata-target" ]
        |> expectOk "commit target with value-expectation dependencies"
        |> ignore

        let persistedHelper = dispatch runtime "describe" [ "word", jsonString "expected-only-helper" ] |> expectOk "inspect expectation-only helper"
        equal "persistent" ((persistedHelper["data"]["status"]).GetValue<string>()) "expected-only helper is included in the selected commit"
        let reloaded = engine projectPath []
        let typedNone = evaluate reloaded "option.none<ExpectedOnlyType>" |> expectOk "reload type referenced only by a test expectation"
        equal "Option<ExpectedOnlyType>" (stackType typedNone 0) "expectation-only type survives persistence"
        dispatch reloaded "test" [ "word", jsonString "metadata-target" ]
        |> expectOk "run expectation-only metadata after reload"
        |> assertAllTestsPassed "reloaded expectation dependencies" 2

        let deepPath = makeProject root "value-expression-deep-inspection"
        let deepRuntime = engine deepPath []
        let nestedConstructors =
            String.replicate 9 "    option.some<ObservationChain> observationChain.new\n"
        let deepSource =
            """record ObservationChain
    field child Option<ObservationChain>
end

word deep-chain : Unit -> ObservationChain
    effects none
    drop
    option.none<ObservationChain>
    observationChain.new
"""
            + nestedConstructors
            + """end

test deep-chain/value-expression
    unit deep-chain
    => value
        unit deep-chain
end
"""
        define deepRuntime deepSource |> expectOk "define bounded-observation deep value test" |> ignore
        dispatch deepRuntime "commit" [ "word", jsonString "deep-chain" ]
        |> expectOk "commit passing test whose structured observation exceeds the DTO depth budget"
        |> ignore
        let checkDeepObservation label (response: JsonObject) =
            assertAllTestsPassed label 1 response
            let result = (response["data"]["results"]).AsArray()[0]
            check (isNull (result["actualStructured"])) $"{label} actual DTO is omitted at the observation depth limit"
            equal "VALUE_INSPECTION_DEPTH_LIMIT" ((result["actualStructuredError"]["code"]).GetValue<string>()) $"{label} actual observation limit is typed"
            check (isNull (result["expectedStructured"])) $"{label} expected DTO is omitted at the observation depth limit"
            equal "VALUE_INSPECTION_DEPTH_LIMIT" ((result["expectedStructuredError"]["code"]).GetValue<string>()) $"{label} expected observation limit is typed"
        checkDeepObservation "committed deep value" (dispatch deepRuntime "test" [ "word", jsonString "deep-chain" ] |> expectOk "observe committed deep value")
        let deepReload = engine deepPath []
        checkDeepObservation "reloaded deep value" (dispatch deepReload "test" [ "word", jsonString "deep-chain" ] |> expectOk "observe reloaded deep value")

        let temporary = engine (makeProject root "value-expression-temporary-reference") []
        dispatch temporary "define"
            [ "source", jsonString "word temporary-expected-helper : Unit -> Bool\n    effects none\n    drop true\nend\n"
              "temporary", jsonBool true ]
        |> expectOk "stage temporary helper used only in expected expression"
        |> ignore
        let temporarySource =
            """word temporary-target : Unit -> Bool
    effects none
    drop true
end
test temporary-target/expected-helper
    unit temporary-target
    => value
        unit temporary-expected-helper
end
"""
        define temporary temporarySource |> expectOk "define test referring to temporary expectation helper" |> ignore
        expectError "COMMIT_TEMPORARY_TEST_DEPENDENCY" (dispatch temporary "commit" [ "word", jsonString "temporary-target" ]) |> ignore
        expectError "NAME_UNKNOWN_WORD" (dispatch (engine (makeProject root "value-expression-temporary-reference") []) "source" [ "word", jsonString "temporary-target" ])
        |> ignore

    let private testExpectedExpressionRenameAndCoverage root =
        let renamePath = makeProject root "value-expression-rename"
        let renameRuntime = engine renamePath []
        let renameSource =
            """word old-helper : Int -> Int
    effects none
    1 add
end
test old-helper/basic
    1 old-helper
    => 2
end

word rename-target : Unit -> Int
    effects none
    drop 2
end
test rename-target/expected-helper
    unit rename-target
    => value
        1 old-helper
end
"""
        define renameRuntime renameSource |> expectOk "define word referenced by another test expectation" |> ignore
        dispatch renameRuntime "commit" [] |> expectOk "persist rename fixture" |> ignore
        dispatch renameRuntime "rename" [ "word", jsonString "old-helper"; "to", jsonString "new-helper" ]
        |> expectOk "rename word referenced only by another test expectation"
        |> ignore
        dispatch (engine renamePath []) "test" [ "word", jsonString "rename-target" ]
        |> expectOk "run test owner after expected reference rename"
        |> assertAllTestsPassed "renamed expectation owner" 1

        let coverageRuntime = engine (makeProject root "value-expression-coverage") []
        let coverageSource =
            """word branchy : Bool -> Int
    effects none
    if
        7
    else
        7
    end
end
test branchy/expected-visits-other-outcome
    true branchy
    => value
        false branchy
end
"""
        define coverageRuntime coverageSource |> expectOk "define branch test with expectation-only second path" |> ignore
        let coverageTest = dispatch coverageRuntime "test" [ "word", jsonString "branchy" ] |> expectOk "run isolated expectation branch"
        assertAllTestsPassed "expectation branch comparison" 1 coverageTest
        let coverage = coverageTest["data"]["coverage"]
        equal 1 (coverage["branchesCovered"].GetValue<int>()) "only actual test-body branch execution counts"
        check (coverage["branchesTotal"].GetValue<int>() > coverage["branchesCovered"].GetValue<int>()) "expected expression does not cover the other branch"
        expectError "LIBRARY_COVERAGE_INCOMPLETE" (dispatch coverageRuntime "commit" [ "word", jsonString "branchy"; "library", jsonBool true ])
        |> ignore
        define coverageRuntime
            "test branchy/false-outcome\n    false branchy\n    => 7\nend\n"
        |> expectOk "add actual execution for the uncovered branch"
        |> ignore
        dispatch coverageRuntime "commit" [ "word", jsonString "branchy"; "library", jsonBool true ]
        |> expectOk "library commit passes after both actual branch outcomes are tested"
        |> ignore

    let private testStructuredEvalStrictnessAndPublication root =
        let runtime = engine (makeProject root "structured-eval-strictness") [ "fs.read"; "fs.write" ]
        let malformedFlags =
            [ "string", jsonString "true"
              "number", JsonValue.Create(1) :> JsonNode
              "object", JsonObject() :> JsonNode
              "array", JsonArray() :> JsonNode
              "null", null ]
        for name, flag in malformedFlags do
            let path = "/invalid-structured-flag-" + name
            let response =
                dispatch runtime "eval"
                    [ "code", jsonString ($"\"{path}\" \"should-not-write\" file.write")
                      "structured", flag ]
            expectError "EVAL_INVALID_ARGUMENT" response |> ignore
            let exists = evaluate runtime ($"\"{path}\" file.exists?") |> expectOk "check invalid structured flag did not run code"
            equal "false" (stackValue exists 0) $"{name} structured flag fails before effects"

        let inspectorPath = makeProject root "structured-eval-inspection-limit"
        let inspector = engine inspectorPath [ "fs.read"; "fs.write" ]
        define inspector "record InspectionChain\n    field child Option<InspectionChain>\nend\n" |> expectOk "define recursive record for structured-eval limit" |> ignore
        let chain =
            [ "option.none<InspectionChain> inspectionChain.new"
              yield! List.replicate 9 "option.some<InspectionChain> inspectionChain.new" ]
            |> String.concat " "
        let expression = $"\"/structured-observation-unpublished\" \"never-save\" file.write {chain}"
        expectError "VALUE_INSPECTION_DEPTH_LIMIT"
            (dispatch inspector "eval" [ "code", jsonString expression; "structured", jsonBool true ])
        |> ignore
        let absent = evaluate inspector "\"/structured-observation-unpublished\" file.exists?" |> expectOk "check bounded inspector failure left virtual files unchanged"
        equal "false" (stackValue absent 0) "structured observation limit is reported before virtual file publication"

    let private testRefinedTypesAndNominality root =
        let runtime = engine (makeProject root "refined-types") []
        let source = exampleSource "refined-types.agent"
        define runtime source |> expectOk "define nominal types" |> ignore

        let accepted = evaluate runtime "\"dev@example.com\" Email.new Email.value" |> expectOk "valid Email construction and unwrap"
        equal "\"dev@example.com\"" (stackValue accepted 0) "explicit Email unwrap"
        dispatch runtime "test" [ "word", jsonString "email.valid?" ]
        |> expectOk "email policy tests"
        |> assertAllTestsPassed "email validator" 6
        for invalidEmail in [ "\"missing-at-sign\""; "\"missingdot@examplecom\""; "\"dev @example.com\""; "\"@dev.example\""; "\"dev.example@\"" ] do
            expectError "REFINEMENT_FAILED" (evaluate runtime $"{invalidEmail} Email.new") |> ignore
        expectError "TYPE_STACK_MISMATCH" (evaluate runtime "\"dev@example.com\" Email.new string.length") |> ignore
        expectError "TYPE_STACK_MISMATCH" (evaluate runtime "3 Email.new") |> ignore
        expectError "TYPE_STACK_MISMATCH" (evaluate runtime "3.0 MetersPerSecond.new 2.0 float.add") |> ignore
        expectError "TYPE_STACK_MISMATCH" (evaluate runtime "3.0 MetersPerSecond.new 4.0 KilometersPerHour.new equals") |> ignore

    let private testInvalidValidatorsRejected root =
        let wrong = engine (makeProject root "wrong-validator") []
        let wrongSignature =
            """word integer-validator : Int -> Bool
    effects none
    drop
    true
end

type InvalidSpeed : Float
    validate integer-validator
end
"""
        expectError "TYPE_VALIDATOR_SIGNATURE" (define wrong wrongSignature) |> ignore

        let impure = engine (makeProject root "impure-validator") []
        let impureValidator =
            """word file-validator : Float -> Bool
    effects fs.read
    drop
    "/flag" file.exists?
end

type FileChecked : Float
    validate file-validator
end
"""
        expectError "TYPE_VALIDATOR_EFFECT" (define impure impureValidator) |> ignore

    let private testTypeOnlyCommit root =
        let projectPath = makeProject root "type-only-commit"
        let runtime = engine projectPath []
        let source =
            """record Token
    field value Int
end

type UserId : Int
end
"""
        define runtime source |> expectOk "define type-only project" |> ignore
        dispatch runtime "commit" [] |> expectOk "commit type-only project" |> ignore

        let reloaded = engine projectPath []
        let id = evaluate reloaded "42 UserId.new UserId.value" |> expectOk "reload committed scalar type"
        equal "42" (stackValue id 0) "scalar type survives type-only commit"
        let token = evaluate reloaded "7 token.new token.value" |> expectOk "reload committed record type"
        equal "7" (stackValue token 0) "record type survives type-only commit"

    let private testValidatorTemporaryOverrideRejected root =
        let runtime = engine (makeProject root "frozen-validator") []
        let source =
            """word email.valid? : String -> Bool
    effects none
    "@" string.contains
end

type Email : String
    validate email.valid?
end

test email.valid?/simple
    "a@b" email.valid?
    => true
end
"""
        define runtime source |> expectOk "define validator type" |> ignore
        dispatch runtime "commit" [] |> expectOk "persist validator and type" |> ignore
        let replacement =
            """word email.valid? : String -> Bool
    effects none
    "@" string.contains
end
"""
        let result = dispatch runtime "define" [ "source", jsonString replacement; "temporary", jsonBool true ]
        expectError "TYPE_VALIDATOR_FROZEN" result |> ignore

    let private testRawCoverageSiteIdentity _root =
        let authoredSpan =
            { File = "same-span.agent"
              Line = 1
              Column = 1
              Length = 20 }
        let syntheticSpan = { authoredSpan with Length = 0 }
        let definition : WordDefinition =
            { Name = "same-span"
              Inputs = []
              Outputs = [ TInt ]
              Effects = Set.empty
              Maturity = LibraryWord
              Revision = 1
              Documentation = "Coverage identity regression fixture."
              Body =
                [ Push(LBool true, authoredSpan)
                  If([ Push(LInt 1L, authoredSpan) ], [ Push(LInt 2L, authoredSpan) ], authoredSpan)
                  Scope([], syntheticSpan) ]
              SourceText = "same-span"
              Span = authoredSpan }
        let entry : WordEntry =
            { Definition = definition
              Builtin = None
              Status = Persistent
              Maturity = LibraryWord
              Revision = 1 }
        let words = Map.add "same-span" entry Compiler.primitives
        let wordIds =
            words
            |> Map.toList
            |> List.map (fun (name, word) ->
                let prefix =
                    match word.Builtin with
                    | Some(BuiltinOp _) -> "primitive-"
                    | Some _ -> "generated-"
                    | None -> "user-"
                name, WordId(prefix + name))
            |> Map.ofList
        let context : Compiler.IrLoweringContext =
            { Words = words
              Records = Map.empty
              Scalars = Map.empty
              WordIds = wordIds }
        let sourceOrigins = Map.ofList [ syntheticSpan, authoredSpan ]
        let verifiedProgram = Compiler.compileIrProgramWithSourceOrigins context sourceOrigins
        let program = VerifiedIrProgram.inspect verifiedProgram
        let wordId = wordIds["same-span"]
        let wordSource =
            program.SourceMap
            |> Map.toList
            |> List.filter (fun (_, source) -> source.SiteOwner = Some wordId)
        let authoredSource =
            wordSource
            |> List.filter (fun (_, source) ->
                source.SourceKind <> "synthetic-scope"
                && source.SourceKind <> "synthetic-store-local"
                && source.SourceKind <> "synthetic-load-local")
        equal 4 authoredSource.Length "same-span authored instructions keep distinct source identities"
        equal 1 (authoredSource |> List.map (fun (_, source) -> source.SiteSpan) |> Set.ofList |> Set.count) "same-span fixture shares one source location"
        let obligations = program.CoverageByWord[wordId]
        equal 4 obligations.CoveredSites.Count "coverage obligations count raw instruction identities"
        let branchSite, branchOutcomes = obligations.BranchOutcomes |> Map.toList |> List.exactlyOne
        check (obligations.CoveredSites.Contains branchSite) "branch operation has its own authored instruction identity"
        equal (Set.ofList [ "false"; "true" ]) (Set.ofList branchOutcomes) "branch outcomes remain distinct for their raw source identity"
        let syntheticSite, syntheticSource =
            wordSource
            |> List.find (fun (_, source) -> source.SourceKind = "synthetic-scope")
        check (not (obligations.CoveredSites.Contains syntheticSite)) "synthetic Scope is excluded from authored coverage"
        equal authoredSpan syntheticSource.SiteSpan "synthetic Scope keeps the remapped authored source span"

        let collisionBody =
            Compiler.compileIrBodyAgainstProgramWithSourceOrigins context verifiedProgram "same-span-body" []
                [ Push(LBool true, authoredSpan)
                  If([ Push(LInt 1L, authoredSpan) ], [ Push(LInt 2L, authoredSpan) ], authoredSpan) ] sourceOrigins
        let body = VerifiedIrBody.inspect collisionBody
        let mutable chargedSites = Set.empty<SourceSiteId>
        let mutable branchOutcomes = Set.empty<SourceSiteId * string>
        let interpreterHost : IrInterpreterHost =
            { PreflightEffects = fun _ _ _ -> ()
              ChargeInstruction = fun _ site -> chargedSites <- Set.add site chargedSites
              RecordBranchOutcome = fun _ site outcome -> branchOutcomes <- Set.add (site, outcome) branchOutcomes
              RecordUse = ignore
              InvokeEffect = fun _ -> EffectUnit
              WordDefinitionSpan = fun _ -> None
              PrimitiveDefinitionSpan = fun _ -> None }
        let output = IrInterpreter.executeBody interpreterHost "same-span-body" collisionBody
        equal 1 output.Length "same-span branch body returns its selected value"
        let constantSites =
            body.BodySourceMap
            |> Map.toList
            |> List.choose (fun (site, source) -> if source.SourceKind = "constant" then Some site else None)
        let siteOrdinal (SourceSiteId(_, ordinal)) = ordinal
        let unexecutedBranchValueSite = List.maxBy siteOrdinal constantSites
        check (not (chargedSites.Contains unexecutedBranchValueSite)) "unreached authored branch instruction stays uncovered despite sharing a span"
        check (Set.difference body.BodyCoverage.CoveredSites chargedSites = Set.singleton unexecutedBranchValueSite) "only the unexecuted raw source identity remains uncovered"
        let uncoveredSpan = body.BodySourceMap[unexecutedBranchValueSite].SiteSpan
        check (chargedSites |> Set.exists (fun site -> body.BodySourceMap[site].SiteSpan = uncoveredSpan)) "an executed instruction shares the uncovered instruction's rendered span"
        check (branchOutcomes |> Set.exists (fun (_, outcome) -> outcome = "true")) "executed branch outcome is recorded by site identity"
        check (not (branchOutcomes |> Set.exists (fun (_, outcome) -> outcome = "false"))) "unexecuted branch outcome remains uncovered"

        let detachedSyntheticSpan = { syntheticSpan with Column = syntheticSpan.Column + 1 }
        let detachedOrigins = Map.add detachedSyntheticSpan authoredSpan sourceOrigins
        let scopeBody = Compiler.compileIrBodyAgainstProgramWithSourceOrigins context verifiedProgram "scope-fuel" [] [ Scope([], detachedSyntheticSpan) ] detachedOrigins
        let scope = VerifiedIrBody.inspect scopeBody
        let chargedScope = scope.BodySourceMap |> Map.toList |> List.find (fun (_, source) -> source.SourceKind = "synthetic-scope") |> fst
        chargedSites <- Set.empty
        let scopeOutput = IrInterpreter.executeBody interpreterHost "scope-fuel" scopeBody
        equal [] scopeOutput "empty synthetic Scope preserves the stack"
        check (chargedSites.Contains chargedScope) "synthetic Scope is still charged as an executed instruction"

    let private testLibraryCoverageGate root =
        let runtime = engine (makeProject root "library-coverage") []
        let partialLibrarySource =
            """word choose : Bool -> Bool
    effects none
    if
        true
    else
        false
    end
end

test choose/true
    true choose
    => true
end
"""
        define runtime partialLibrarySource |> expectOk "define partially covered library candidate" |> ignore

        let libraryArgs = [ "word", jsonString "choose"; "library", jsonBool true ]
        expectError "LIBRARY_COVERAGE_INCOMPLETE" (dispatch runtime "commit" libraryArgs) |> ignore

        let falseOutcomeTest =
            """test choose/false
    false choose
    => false
end
"""
        define runtime falseOutcomeTest |> expectOk "add false outcome test" |> ignore
        dispatch runtime "commit" libraryArgs |> expectOk "commit fully covered library word" |> ignore

        let details = dispatch runtime "describe" [ "word", jsonString "choose" ] |> expectOk "inspect library word"
        equal "library" ((details["data"]["maturity"]).GetValue<string>()) "library maturity persists"
        let coverage = details["data"]["coverage"]
        equal 2 (coverage["branchesCovered"].GetValue<int>()) "both branch outcomes are covered"
        equal 2 (coverage["branchesTotal"].GetValue<int>()) "both branch outcomes are required"

        let reloaded =
            match runtime.ProjectDirectory with
            | Some path -> engine path []
            | None -> failwith "missing project path"
        let reloadedDetails = dispatch reloaded "describe" [ "word", jsonString "choose" ] |> expectOk "reload library maturity"
        equal "library" ((reloadedDetails["data"]["maturity"]).GetValue<string>()) "library maturity reloads"
        define reloaded partialLibrarySource |> expectOk "replace library candidate" |> ignore
        dispatch reloaded "commit" [ "word", jsonString "choose" ] |> expectOk "replace without downgrading library" |> ignore
        let afterReplacement = dispatch reloaded "describe" [ "word", jsonString "choose" ] |> expectOk "inspect replacement maturity"
        equal "library" ((afterReplacement["data"]["maturity"]).GetValue<string>()) "library quality cannot be downgraded"

        let metadataPath = makeProject root "library-metadata-helper-coverage"
        let metadataRuntime = engine metadataPath []
        let metadataSource =
            """word choose-true : Int -> Bool
    effects none
    drop
    true
end

test choose-true/basic
    0 choose-true
    => true
end

word choose-false : Int -> Bool
    effects none
    drop
    false
end

test choose-false/basic
    0 choose-false
    => false
end

word branch-library : Bool -> Int
    effects none
    if
        1
    else
        2
    end
end

test branch-library/true
    0 choose-true branch-library
    => 1
end

test branch-library/false
    0 choose-false branch-library
    => 2
end
"""
        define metadataRuntime metadataSource |> expectOk "define library tests with candidate helper dependencies" |> ignore
        dispatch metadataRuntime "commit" [ "word", jsonString "branch-library"; "library", jsonBool true ]
        |> expectOk "commit library word with metadata-only helper dependencies"
        |> ignore
        let metadataReload = engine metadataPath []
        let reloadedTests = dispatch metadataReload "test" [ "word", jsonString "branch-library" ] |> expectOk "run reloaded library branch tests"
        assertAllTestsPassed "reloaded library branch" 2 reloadedTests
        let durableCoverage = reloadedTests["data"]["coverage"]
        let covered = durableCoverage["branchesCovered"]
        let required = durableCoverage["branchesTotal"]
        equal 2 (covered.GetValue<int>()) "both library branches remain covered after reload"
        equal 2 (required.GetValue<int>()) "library branch requirements remain visible after reload"

    let private testTaskAbortRollsBackDictionary root =
        let projectPath = makeProject root "task-abort"
        let runtime = engine projectPath []
        let baseline =
            """word stable.value : Int -> Int
    effects none
    doc "durable original implementation"
    1 add
end

test stable.value/original
    1 stable.value
    => 2
end
"""
        define runtime baseline |> expectOk "define durable task baseline" |> ignore
        dispatch runtime "commit" [ "word", jsonString "stable.value" ] |> expectOk "commit durable task baseline" |> ignore
        let baselineIdentity = dispatch runtime "describe" [ "word", jsonString "stable.value" ] |> expectOk "capture baseline word identity" |> identityOf

        dispatch runtime "task.begin" [ "goal", jsonString "temporary vocabulary experiment" ] |> expectOk "begin task" |> ignore
        let stagedSource =
            """record DraftRecord
    field value Int
end

type DraftCount : Int
end

word draft.increment : Int -> Int
    effects none
    doc "task-local documentation"
    1 add
end

word stable.value : Int -> Int
    effects none
    doc "task-local replacement"
    1 add
end

test draft.increment/basic
    1 draft.increment
    => 2
end

test stable.value/replacement
    1 stable.value
    => 2
end

example draft.increment/sample
    10 draft.increment
    => 11
end
"""
        define runtime stagedSource |> expectOk "stage task vocabulary" |> ignore

        let draft = dispatch runtime "describe" [ "word", jsonString "draft.increment" ] |> expectOk "inspect staged docs"
        equal "task-local documentation" ((draft["data"]["documentation"]).GetValue<string>()) "staged documentation is present"

        let taskTests = dispatch runtime "test" [ "word", jsonString "draft.increment" ] |> expectOk "run task tests"
        assertAllTestsPassed "task" 1 taskTests
        dispatch runtime "commit" [ "word", jsonString "draft.increment" ] |> expectOk "commit interim task change" |> ignore
        let replacementTests = dispatch runtime "test" [ "word", jsonString "stable.value" ] |> expectOk "run replacement tests"
        assertAllTestsPassed "task replacement" 2 replacementTests
        dispatch runtime "commit" [ "word", jsonString "stable.value"; "library", jsonBool true ]
        |> expectOk "commit interim library replacement"
        |> ignore
        let interimDescription = dispatch runtime "describe" [ "word", jsonString "stable.value" ] |> expectOk "inspect interim replacement"
        equal "library" ((interimDescription["data"]["maturity"]).GetValue<string>()) "interim commit changes maturity policy"
        equal "task-local replacement" ((interimDescription["data"]["documentation"]).GetValue<string>()) "interim commit writes replacement metadata"
        dispatch runtime "task.abort" [] |> expectOk "abort task" |> ignore

        expectError "NAME_UNKNOWN_WORD" (dispatch runtime "describe" [ "word", jsonString "draft.increment" ]) |> ignore
        let tests = dispatch runtime "tests" [ "word", jsonString "draft.increment" ] |> expectOk "test metadata rolled back"
        equal "[]" (tests["data"].ToJsonString()) "attached test rolled back"
        let examples = dispatch runtime "examples" [ "word", jsonString "draft.increment" ] |> expectOk "example metadata rolled back"
        equal "[]" (examples["data"].ToJsonString()) "example rolled back"
        expectError "NAME_UNKNOWN_WORD" (evaluate runtime "1 DraftCount.new") |> ignore
        expectError "NAME_UNKNOWN_WORD" (evaluate runtime "1 draftRecord.new") |> ignore

        let restoredDescription = dispatch runtime "describe" [ "word", jsonString "stable.value" ] |> expectOk "inspect task baseline after abort"
        equal baselineIdentity (identityOf restoredDescription) "task abort restores the original word identity"
        equal "project" ((restoredDescription["data"]["maturity"]).GetValue<string>()) "abort restores original maturity policy"
        equal "durable original implementation" ((restoredDescription["data"]["documentation"]).GetValue<string>()) "abort restores original documentation"
        let restoredValue = evaluate runtime "1 stable.value" |> expectOk "evaluate baseline after abort"
        equal "2" (stackValue restoredValue 0) "abort restores original implementation in memory"
        let restoredTestResults = dispatch runtime "test" [ "word", jsonString "stable.value" ] |> expectOk "run restored baseline test"
        assertAllTestsPassed "restored baseline" 1 restoredTestResults

        let reloaded = engine projectPath []
        expectError "NAME_UNKNOWN_WORD" (dispatch reloaded "source" [ "word", jsonString "draft.increment" ]) |> ignore
        expectError "NAME_UNKNOWN_WORD" (evaluate reloaded "1 DraftCount.new") |> ignore
        expectError "NAME_UNKNOWN_WORD" (evaluate reloaded "1 draftRecord.new") |> ignore
        let reloadDescription = dispatch reloaded "describe" [ "word", jsonString "stable.value" ] |> expectOk "reload baseline after abort"
        equal "project" ((reloadDescription["data"]["maturity"]).GetValue<string>()) "abort restores original maturity policy on disk"
        let reloadSource = dispatch reloaded "source" [ "word", jsonString "stable.value" ] |> expectOk "inspect restored source after reload"
        let reloadSourceText = reloadSource["data"].GetValue<string>()
        check (reloadSourceText.Contains("    1" + Environment.NewLine + "    add", StringComparison.Ordinal)) "abort restores original source on disk"
        check (reloadSourceText.Contains("durable original implementation", StringComparison.Ordinal)) "abort restores original documentation on disk"
        check (not (reloadSourceText.Contains("task-local replacement", StringComparison.Ordinal))) "aborted replacement documentation is absent from disk"
        let reloadValue = evaluate reloaded "1 stable.value" |> expectOk "evaluate reloaded baseline after abort"
        equal "2" (stackValue reloadValue 0) "fresh engine executes original implementation"
        let reloadTests = dispatch reloaded "test" [ "word", jsonString "stable.value" ] |> expectOk "reload attached tests after abort"
        assertAllTestsPassed "reloaded baseline" 1 reloadTests

    let private testTaskCommitClearsSessionWords root =
        let runtime = engine (makeProject root "task-commit-temp") []
        let existingTemporary =
            """word existing.session : Int -> Int
    effects none
    1 add
end
"""
        dispatch runtime "define" [ "source", jsonString existingTemporary; "temporary", jsonBool true ]
        |> expectOk "define preexisting session word" |> ignore
        dispatch runtime "task.begin" [ "goal", jsonString "clear task-local words" ] |> expectOk "begin cleanup task" |> ignore
        let taskTemporary =
            """word task.only : Int -> Int
    effects none
    1 add
end

test task.only/basic
    1 task.only
    => 2
end
"""
        dispatch runtime "define" [ "source", jsonString taskTemporary; "temporary", jsonBool true ]
        |> expectOk "define task-local temporary" |> ignore
        dispatch runtime "task.commit" [] |> expectOk "commit task without candidates" |> ignore
        equal "2" (stackValue (evaluate runtime "1 existing.session" |> expectOk "preserve preexisting session word") 0) "session word from before task remains"
        expectError "NAME_UNKNOWN_WORD" (evaluate runtime "1 task.only") |> ignore
        let testNames = dispatch runtime "tests" [ "word", jsonString "task.only" ] |> expectOk "inspect cleared temporary tests"
        equal "[]" (testNames["data"].ToJsonString()) "temporary tests are removed when task ends"

        dispatch runtime "task.begin" [ "goal", jsonString "empty task" ] |> expectOk "begin empty task" |> ignore
        dispatch runtime "task.commit" [] |> expectOk "commit empty task" |> ignore

    let private testTaskLogsSurviveReload root =
        let projectPath = makeProject root "task-log-history"
        let completeTask runtime goal =
            let started = dispatch runtime "task.begin" [ "goal", jsonString goal ] |> expectOk "begin persisted task"
            let taskId = (started["data"]["task"]).GetValue<string>()
            dispatch runtime "task.abort" [] |> expectOk "persist task log" |> ignore
            taskId
        let first = completeTask (engine projectPath []) "first task session"
        let second = completeTask (engine projectPath []) "second task session"
        check (first <> second) "task identifiers advance across engine reloads"
        equal "task-0001" first "first persisted task id"
        equal "task-0002" second "second persisted task id"
        let historyPath = Path.Combine(projectPath, "history")
        let taskLogs = Directory.GetFiles(historyPath, "task-*.json") |> Array.sort
        equal 2 taskLogs.Length "both task logs remain on disk"
        check (File.Exists(Path.Combine(historyPath, first + ".json"))) "first task log is preserved"
        check (File.Exists(Path.Combine(historyPath, second + ".json"))) "second task log is preserved"
        let third = completeTask (engine projectPath []) "third task session after reload"
        equal "task-0003" third "task numbering continues after another reload"
        equal 3 (Directory.GetFiles(historyPath, "task-*.json").Length) "reloading does not overwrite earlier task logs"

    let private testTaskLogWriteFailureIsWarning root =
        let projectPath = makeProject root "task-log-write-warning"
        let historyPath = Path.Combine(projectPath, "history")
        Directory.CreateDirectory(Path.Combine(historyPath, "task-0001.json")) |> ignore
        let runtime = engine projectPath []
        let committedSource =
            """word log-warning.persist : Int -> Int
    effects none
    1 add
end

test log-warning.persist/basic
    2 log-warning.persist
    => 3
end
"""
        dispatch runtime "task.begin" [ "goal", jsonString "commit despite task log write failure" ]
        |> expectOk "begin task with blocked log destination" |> ignore
        define runtime committedSource |> expectOk "define candidate for task-log commit" |> ignore

        let committed = dispatch runtime "task.commit" [] |> expectOk "task commit remains successful when log write fails"
        let warning = committed["data"]["logWarning"]
        check (not (isNull warning)) "committed task result includes a log warning"
        check (not (warning["saved"].GetValue<bool>())) "commit warning states that the task log was not saved"
        check ((warning["code"].GetValue<string>()).StartsWith("STORAGE_", StringComparison.Ordinal)) "commit warning retains the structured storage error code"
        check (committed["text"].GetValue<string>().Contains("not saved", StringComparison.OrdinalIgnoreCase)) "commit response text explains that the task log was not saved"
        check (not ((committed["data"]["active"]).GetValue<bool>())) "task commit is complete despite the log warning"

        let completedStatus = dispatch runtime "task.status" [] |> expectOk "read completed task status after log failure"
        check (not (isNull (completedStatus["data"]["logWarning"]))) "in-memory completed task status retains the log warning"
        let reloaded = engine projectPath []
        equal "3" (stackValue (evaluate reloaded "2 log-warning.persist" |> expectOk "verify committed state after log failure") 0) "task-log failure does not undo the durable commit"

        Directory.CreateDirectory(Path.Combine(historyPath, "task-0002.json")) |> ignore
        dispatch runtime "task.begin" [ "goal", jsonString "abort despite task log write failure" ]
        |> expectOk "begin task with second blocked log destination" |> ignore
        let transientSource =
            """word log-warning.transient : Int -> Int
    effects none
    dup drop
end
"""
        dispatch runtime "define" [ "source", jsonString transientSource; "temporary", jsonBool true ]
        |> expectOk "define temporary word for task abort" |> ignore

        let aborted = dispatch runtime "task.abort" [] |> expectOk "task abort remains successful when log write fails"
        let abortWarning = aborted["data"]["logWarning"]
        check (not (isNull abortWarning)) "aborted task result includes a log warning"
        check (not (abortWarning["saved"].GetValue<bool>())) "abort warning states that the task log was not saved"
        check (aborted["text"].GetValue<string>().Contains("not saved", StringComparison.OrdinalIgnoreCase)) "abort response text explains that the task log was not saved"
        let abortedStatus = dispatch runtime "task.status" [] |> expectOk "read aborted task status after log failure"
        check (not (isNull (abortedStatus["data"]["logWarning"]))) "in-memory aborted task status retains the log warning"
        expectError "NAME_UNKNOWN_WORD" (evaluate (engine projectPath []) "1 log-warning.transient") |> ignore

    let private testNamedSnapshotRestoresProjectAndProviders root =
        let projectPath = makeProject root "named-snapshot"
        let capabilities = Set.ofList [ "fs.read"; "fs.write"; "clock.read" ]
        let savedClock = "2025-12-31T23:59:58Z"
        let runtime = Runtime.Engine(projectPath, capabilities, ?clockValue = Some savedClock)
        let baseline =
            """word snapshot.base : Int -> Int
    effects none
    1 add
end

test snapshot.base/basic
    1 snapshot.base
    => 2
end
"""
        define runtime baseline |> expectOk "define snapshot baseline" |> ignore
        dispatch runtime "commit" [ "word", jsonString "snapshot.base" ] |> expectOk "commit snapshot baseline" |> ignore
        let storageStatus = dispatch runtime "storage.status" [] |> expectOk "inspect durable storage status"
        equal "manifest" ((storageStatus["data"]["authority"]).GetValue<string>()) "storage status reports manifest authority"
        check ((storageStatus["data"]["generation"]).GetValue<int64>() > 0L) "storage status reports current generation"
        check ((storageStatus["data"]["manifestHash"]).GetValue<string>() <> "") "storage status reports the current manifest hash"
        let storageWarning = storageStatus["data"]["exportWarning"]
        check (isNull storageWarning) "storage status reports no warning after successful export"
        evaluate runtime "\"snapshot-file\" \"saved-value\" file.write" |> expectOk "write snapshot provider state" |> ignore
        dispatch runtime "snapshot.save" [ "name", jsonString "baseline" ] |> expectOk "save named snapshot" |> ignore

        let staged =
            """word snapshot.extra : Int -> Int
    effects none
    5 add
end

test snapshot.extra/basic
    1 snapshot.extra
    => 6
end
"""
        define runtime staged |> expectOk "stage snapshot-excluded candidate" |> ignore
        let candidateSnapshot = dispatch runtime "snapshot.save" [ "name", jsonString "committed-only" ] |> expectOk "save committed projection"
        let excludedNode = candidateSnapshot["data"]["excludedCandidates"]
        let excluded = excludedNode.AsArray()
        check (excluded |> Seq.exists (fun item -> item.GetValue<string>() = "snapshot.extra")) "snapshot save reports excluded candidate"
        dispatch runtime "commit" [ "word", jsonString "snapshot.extra" ] |> expectOk "commit post-snapshot candidate" |> ignore
        evaluate runtime "\"snapshot-file\" \"later-value\" file.write" |> expectOk "mutate virtual file after snapshot" |> ignore

        let laterClock = "2026-01-02T03:04:05Z"
        let reloaded = Runtime.Engine(projectPath, capabilities, ?clockValue = Some laterClock)
        let loaded = dispatch reloaded "snapshot.load" [ "name", jsonString "baseline" ] |> expectOk "restore named snapshot"
        let restoredClock = loaded["data"]["clockValue"]
        equal savedClock (restoredClock.GetValue<string>()) "snapshot clock state is restored"
        let fileText = evaluate reloaded "\"snapshot-file\" file.read" |> expectOk "read restored provider file"
        equal "\"saved-value\"" (stackValue fileText 0) "snapshot virtual file state is restored"
        expectError "NAME_UNKNOWN_WORD" (evaluate reloaded "1 snapshot.extra") |> ignore
        let restoredValue = evaluate reloaded "1 snapshot.base" |> expectOk "evaluate word restored from snapshot"
        equal "2" (stackValue restoredValue 0) "snapshot restores committed vocabulary"
        let clock = evaluate reloaded "clock.now" |> expectOk "host clock capability survives snapshot load"
        equal ($"\"{savedClock}\"") (stackValue clock 0) "snapshot restores fixed clock"

        dispatch reloaded "task.begin" [ "goal", jsonString "protect active task from snapshot load" ] |> expectOk "begin task before snapshot load" |> ignore
        expectError "SNAPSHOT_TASK_ACTIVE" (dispatch reloaded "snapshot.load" [ "name", jsonString "committed-only" ]) |> ignore
        dispatch reloaded "task.abort" [] |> expectOk "abort task after rejected snapshot load" |> ignore

    let private testSemanticRenameAndDeprecation root =
        let projectPath = makeProject root "semantic-maintenance"
        let runtime = engine projectPath []
        let source =
            """word rename-target : Int -> Int
    effects none
    1 add
end

word rename-caller : Int -> Int
    effects none
    rename-target
end

word rename-map : List<Int> -> Int
    effects none
    list.map rename-target
    drop
    9
end

test rename-target/basic
    1 rename-target
    => 2
end

test rename-target/literal-is-not-a-reference
    "rename-target" drop
    1 rename-target
    => 2
end

test rename-caller/basic
    1 rename-caller
    => 2
end

test rename-map/empty-callback
    list.empty<Int> rename-map
    => 9
end

test rename-map/nonempty-callback
    1 list.singleton<Int> rename-map
    => 9
end

example rename-target/basic-example
    1 rename-target
    => 2
end
"""
        define runtime source |> expectOk "define vocabulary for semantic rename" |> ignore
        dispatch runtime "commit" [] |> expectOk "commit vocabulary before semantic rename" |> ignore
        let oldDescription = dispatch runtime "describe" [ "word", jsonString "rename-target" ] |> expectOk "inspect old name before rename"
        let identity = identityOf oldDescription
        let renamed =
            dispatch runtime "rename" [ "word", jsonString "rename-target"; "to", jsonString "canonical-target"; "actor", jsonString "host" ]
            |> expectOk "rename committed word and references"
        let renamedIdNode = renamed["data"]["id"]
        equal identity (renamedIdNode.GetValue<string>()) "semantic rename preserves word identity"
        let callers = dispatch runtime "callers" [ "word", jsonString "canonical-target" ] |> expectOk "inspect renamed callers"
        let callerNames = callers["data"].AsArray() |> Seq.map (fun value -> value.GetValue<string>()) |> Set.ofSeq
        check (callerNames.Contains "rename-caller") "direct call is rewritten"
        check (callerNames.Contains "rename-map") "static list callback is rewritten"
        expectError "NAME_UNKNOWN_WORD" (dispatch runtime "describe" [ "word", jsonString "rename-target" ]) |> ignore
        equal "2" (stackValue (evaluate runtime "1 canonical-target" |> expectOk "evaluate renamed definition") 0) "renamed word remains callable"
        equal "2" (stackValue (evaluate runtime "1 rename-caller" |> expectOk "evaluate rewritten caller") 0) "rewritten caller preserves behavior"
        equal "9" (stackValue (evaluate runtime "1 list.singleton<Int> rename-map" |> expectOk "evaluate rewritten static callback") 0) "rewritten static callback executes"
        let allTests = dispatch runtime "test-all" [] |> expectOk "run tests after semantic rename"
        assertAllTestsPassed "renamed graph" 5 allTests

        let history = dispatch runtime "history" [ "word", jsonString "canonical-target" ] |> expectOk "inspect durable rename history"
        let revisions = history["data"].AsArray()
        equal 2 revisions.Count "rename adds a durable revision"
        let firstRevisionNode = revisions[0]["revision"]
        let secondRevisionNode = revisions[1]["revision"]
        let actorNode = revisions[1]["actor"]
        equal 1 (firstRevisionNode.GetValue<int>()) "original revision number is retained"
        equal 2 (secondRevisionNode.GetValue<int>()) "rename revision uses the actual durable revision number"
        equal "host" (actorNode.GetValue<string>()) "revision records explicit provenance"
        let latestTestsNode = revisions[1]["tests"]
        let latestTests = latestTestsNode.AsArray()
        check (latestTests |> Seq.exists (fun item ->
            let testSource = item.GetValue<string>()
            testSource.Contains("\"rename-target\"", StringComparison.Ordinal)
            && testSource.Contains("canonical-target", StringComparison.Ordinal))) "literal text is preserved while executable references are rewritten"

        expectError "RENAME_COLLISION" (dispatch runtime "rename" [ "word", jsonString "canonical-target"; "to", jsonString "rename-caller" ]) |> ignore
        equal identity (dispatch runtime "describe" [ "word", jsonString "canonical-target" ] |> expectOk "inspect after rejected collision" |> identityOf) "collision refusal leaves word identity unchanged"
        let reloaded = engine projectPath []
        equal identity (dispatch reloaded "describe" [ "word", jsonString "canonical-target" ] |> expectOk "reload renamed identity" |> identityOf) "renamed identity persists across reload"
        assertAllTestsPassed "reloaded renamed graph" 5 (dispatch reloaded "test-all" [] |> expectOk "run reloaded rename tests")

        let deprecated = dispatch reloaded "deprecate" [ "word", jsonString "canonical-target"; "actor", jsonString "client" ] |> expectOk "deprecate committed word"
        let deprecatedIdNode = deprecated["data"]["id"]
        let deprecatedFlagNode = deprecated["data"]["deprecated"]
        equal identity (deprecatedIdNode.GetValue<string>()) "deprecation preserves word identity"
        check (deprecatedFlagNode.GetValue<bool>()) "deprecation response exposes policy"
        let deprecatedDescription = dispatch reloaded "describe" [ "word", jsonString "canonical-target" ] |> expectOk "inspect deprecated word"
        let descriptionFlagNode = deprecatedDescription["data"]["deprecated"]
        check (descriptionFlagNode.GetValue<bool>()) "describe exposes deprecation state"
        let deprecationHistory = dispatch reloaded "history" [ "word", jsonString "canonical-target" ] |> expectOk "inspect deprecation revision"
        let deprecationRevisions = deprecationHistory["data"].AsArray()
        equal 3 deprecationRevisions.Count "deprecation adds a durable revision"
        let durableDeprecatedNode = deprecationRevisions[2]["deprecated"]
        check (durableDeprecatedNode.GetValue<bool>()) "durable history records deprecation"
        let fresh = engine projectPath []
        let freshDescription = dispatch fresh "describe" [ "word", jsonString "canonical-target" ] |> expectOk "reload deprecated word"
        let freshDeprecatedNode = freshDescription["data"]["deprecated"]
        check (freshDeprecatedNode.GetValue<bool>()) "deprecation survives reload"
        equal "2" (stackValue (evaluate fresh "1 canonical-target" |> expectOk "deprecated word remains callable") 0) "deprecation preserves callers"

        let frozenPath = makeProject root "frozen-validator-rename"
        let frozen = engine frozenPath []
        define frozen (exampleSource "refined-types.agent") |> expectOk "define persistent scalar validator" |> ignore
        dispatch frozen "commit" [] |> expectOk "commit scalar validator graph" |> ignore
        let frozenId = dispatch frozen "describe" [ "word", jsonString "email.valid?" ] |> expectOk "inspect frozen validator identity" |> identityOf
        expectError "TYPE_VALIDATOR_FROZEN" (dispatch frozen "rename" [ "word", jsonString "email.valid?"; "to", jsonString "email.accepts?" ]) |> ignore
        equal frozenId (dispatch frozen "describe" [ "word", jsonString "email.valid?" ] |> expectOk "inspect validator after refused rename" |> identityOf) "frozen validator refusal leaves identity unchanged"

    let private testTypedContainersAndLanguageConstructs root =
        let projectPath = makeProject root "typed-containers"
        let runtime = engine projectPath [ "fs.read"; "fs.write" ]
        let source = exampleSource "refined-types.agent" + Environment.NewLine + exampleSource "containers.agent"
        define runtime source |> expectOk "define container example" |> ignore

        let emptyList = evaluate runtime "list.empty<Int>" |> expectOk "construct typed empty list"
        equal "[]" (stackValue emptyList 0) "empty list display"
        equal "List<Int>" (stackType emptyList 0) "empty list retains its explicit item type"
        let nestedEmptyList = evaluate runtime "list.empty<List<Int>>" |> expectOk "construct nested typed empty list"
        equal "List<List<Int>>" (stackType nestedEmptyList 0) "nested container type arguments parse recursively"
        let nestedNone = evaluate runtime "option.none<List<Email>>" |> expectOk "construct nested typed none"
        equal "Option<List<Email>>" (stackType nestedNone 0) "option metadata retains nested nominal element types"
        let emptyMap = evaluate runtime "list.empty<Email> list.map Email.value" |> expectOk "map over empty nominal list"
        equal "List<String>" (stackType emptyMap 0) "empty map retains the callback output type"
        let none = evaluate runtime "option.none<Email>" |> expectOk "construct typed none"
        equal "Option<Email>" (stackType none 0) "none retains its explicit item type"
        let errorResult = evaluate runtime "\"offline\" result.error<List<Int>, String>" |> expectOk "construct typed error result"
        equal "Result<List<Int>, String>" (stackType errorResult 0) "error result retains its inactive success type"
        let missingIndex = evaluate runtime "list.empty<Email> 0 list.get" |> expectOk "get from empty nominal list"
        equal "Option<Email>" (stackType missingIndex 0) "out-of-range lookup retains the element type"
        let singleton = evaluate runtime "\"dev@example.com\" Email.new list.singleton<Email>" |> expectOk "construct nominal list"
        equal "List<Email>" (stackType singleton 0) "singleton retains its nominal element type"
        expectError "TYPE_STACK_MISMATCH" (evaluate runtime "\"dev@example.com\" Email.new list.singleton<Email> \"plain string\" list.append") |> ignore
        expectError "TYPE_CONTAINER_PAYLOAD" (evaluate runtime "\"dev@example.com\" Email.new list.singleton<String>") |> ignore
        expectError "TYPE_CONTAINER_PAYLOAD" (evaluate runtime "42 option.some<String>") |> ignore
        expectError "TYPE_CONTAINER_PAYLOAD" (evaluate runtime "\"not an int\" result.ok<Int, String>") |> ignore

        let listOps = evaluate runtime "2 list.singleton<Int> 3 list.append 4 list.singleton<Int> list.concat list.count" |> expectOk "append, concatenate, and count lists"
        equal "3" (stackValue listOps 0) "list operations preserve values"
        let foldStep =
            "word container.fold-step : Int Int -> Int\n"
            + "    effects none\n"
            + "    swap\n"
            + "    10\n"
            + "    multiply\n"
            + "    add\n"
            + "end"
        define runtime foldStep |> expectOk "define a static two-input fold step" |> ignore
        let folded = evaluate runtime "1 list.singleton<Int> 2 list.append 3 list.append 0 list.fold container.fold-step" |> expectOk "fold Stack list left-to-right"
        equal "123" (stackValue folded 0) "Stack list.fold applies callback items in source order"
        let emptyFold = evaluate runtime "list.empty<Int> 7 list.fold container.fold-step" |> expectOk "fold an empty Stack list"
        equal "7" (stackValue emptyFold 0) "Stack list.fold returns its seed for empty input"
        let prefixFold = evaluate runtime "99 1 list.singleton<Int> 2 list.append 0 list.fold container.fold-step" |> expectOk "fold while preserving the earlier Stack prefix"
        equal "99" (stackValue prefixFold 0) "Stack list.fold preserves preexisting stack values"
        equal "12" (stackValue prefixFold 1) "Stack fold result is appended above the preserved prefix"
        let flowFold =
            dispatch runtime "eval"
                [ "frontend", jsonString "flow"
                  "code", jsonString "list::append(list::append(list::singleton<Int>(1), 2), 3).fold(0, container::fold-step)" ]
            |> expectOk "use the same statically named fold step from Flow"
        equal "123" (stackValue flowFold 0) "Flow fold composes with a Stack-authored callback"
        let mapped = evaluate runtime "2 list.singleton<Int> 3 list.append list.map container.increment" |> expectOk "map callback over values"
        equal "[3, 4]" (stackValue mapped 0) "map executes its statically named callback"
        equal "List<Int>" (stackType mapped 0) "map reports callback result type"
        let filtered = evaluate runtime "-1 list.singleton<Int> 2 list.append list.filter container.negative? list.count" |> expectOk "filter callback over values"
        equal "1" (stackValue filtered 0) "filter keeps true results and drops false results"
        let visited = evaluate runtime "list.empty<Int> list.each container.ignore-int" |> expectOk "each over empty list"
        equal "unit" (stackValue visited 0) "each returns Unit"
        equal "Unit" (stackType visited 0) "each has a typed Unit output"

        let tests = dispatch runtime "test-all" [] |> expectOk "run container example tests"
        assertAllTestsPassed "container example" 21 tests

        let mapDescriptor = dispatch runtime "describe" [ "word", jsonString "list.map" ] |> expectOk "describe map syntax"
        equal "syntax" ((mapDescriptor["data"]["kind"]).GetValue<string>()) "map form is identified as syntax"
        equal "inherits callback word effects" ((mapDescriptor["data"]["effectRule"]).GetValue<string>()) "map describes its effect rule"
        check (((mapDescriptor["data"]["documentation"]).GetValue<string>()).Contains("before any element is visited", StringComparison.Ordinal)) "map description explains static preflight"
        let sourceDescriptor = dispatch runtime "source" [ "word", jsonString "result.error" ] |> expectOk "show result constructor syntax"
        check ((sourceDescriptor["data"].GetValue<string>()).Contains("result.error<T, E>", StringComparison.Ordinal)) "source exposes the typed constructor form"
        let search = dispatch runtime "search" [ "query", jsonString "List<T>" ] |> expectOk "search container constructs"
        check (search["data"].ToJsonString().Contains("list.map", StringComparison.Ordinal)) "search finds typed syntax forms"
        let words = dispatch runtime "words" [] |> expectOk "inspect words and constructs"
        let constructs = (words["data"]["constructs"]).AsArray()
        let wordEntries = (words["data"]["words"]).AsArray()
        let hasFilterConstruct = constructs |> Seq.exists (fun item -> (item["name"].GetValue<string>()) = "list.filter")
        let hasFilterWord = wordEntries |> Seq.exists (fun item -> (item["name"].GetValue<string>()) = "list.filter")
        check hasFilterConstruct "words exposes syntax metadata separately"
        check (not hasFilterWord) "syntax forms are not phantom executable words"

        let reservedSyntaxWord = """word list.map : List<Int> -> List<Int>
    effects none
    dup
end
"""
        expectError "PARSE_INVALID_WORD_NAME" (define runtime reservedSyntaxWord) |> ignore
        let malformedField = """record MalformedField
xxxxx value Int
end
"""
        expectError "PARSE_INVALID_FIELD" (define runtime malformedField) |> ignore
        let shortField = """record ShortField
x
end
"""
        expectError "PARSE_INVALID_FIELD" (define runtime shortField) |> ignore
        let fieldCollision = """record FieldCollision
    field new Int
end
"""
        expectError "NAME_GENERATED_COLLISION" (define runtime fieldCollision) |> ignore
        let caseCollision = """record Customer
    field id Int
end
record customer
    field id Int
end
"""
        expectError "NAME_GENERATED_COLLISION" (define runtime caseCollision) |> ignore
        let syntaxCollision = """record list
    field map Int
end
"""
        expectError "NAME_GENERATED_COLLISION" (define runtime syntaxCollision) |> ignore

        let reverseCollision = engine (makeProject root "reverse-generated-collision") []
        let reverseWordSource = """word box.new : Int -> Int
    effects none
    dup drop
end
"""
        define reverseCollision reverseWordSource |> expectOk "define word before its generated-name owner" |> ignore
        let reverseRecordSource = """record Box
    field value Int
end
"""
        expectError "NAME_GENERATED_COLLISION" (define reverseCollision reverseRecordSource) |> ignore
        let survivingWord = evaluate reverseCollision "7 box.new" |> expectOk "failed record definition rolls back atomically"
        equal "7" (stackValue survivingWord 0) "preexisting user word remains intact after collision"

        let reloadCollisionPath = makeProject root "reload-generated-collision"
        let collisionDictionary = """word box.new : Int -> Int
    effects none
    dup drop
end

record Box
    field value Int
end
"""
        File.WriteAllText(Path.Combine(reloadCollisionPath, "dictionary.agent"), collisionDictionary)
        let mutable reloadCollisionCode = ""
        try Runtime.Engine(reloadCollisionPath, Set.empty) |> ignore
        with LanguageException diagnostic -> reloadCollisionCode <- diagnostic.Code
        equal "NAME_GENERATED_COLLISION" reloadCollisionCode "dictionary reload rejects generated/user word collision"

        let badType = engine (makeProject root "free-container-type") []
        let unsupportedType = """word unsupported : List<a> -> List<a>
    effects none
end
"""
        expectError "TYPE_UNKNOWN_NAMED_TYPE" (define badType unsupportedType) |> ignore

        let oversized =
            [ "1"; "list.singleton<Int>" ] @ (List.replicate 14 "dup list.concat")
            |> String.concat " "
        expectError "RUNTIME_VALUE_LIMIT" (evaluate runtime oversized) |> ignore

        let preflightPath = makeProject root "container-effect-preflight"
        let preflight = engine preflightPath [ "fs.read"; "fs.write" ]
        let effectfulCallbacks =
            """word container.clock-touch : Int -> Unit
    effects clock.read
    drop clock.now drop unit
end

word container.clock-string : Int -> String
    effects clock.read
    drop clock.now
end
"""
        define preflight effectfulCallbacks |> expectOk "define effectful callbacks" |> ignore
        expectError "TYPE_CONTAINER_PAYLOAD" (evaluate preflight "\"bad-constructor\" \"value\" file.write \"not an int\" option.some<Int>") |> ignore
        let constructorAbsent = evaluate preflight "\"bad-constructor\" file.exists?" |> expectOk "check constructor type failure did not write"
        equal "false" (stackValue constructorAbsent 0) "constructor payload validation precedes earlier effects"
        expectError "CAPABILITY_DENIED" (evaluate preflight "\"guarded-write\" \"value\" file.write list.empty<Int> list.map container.clock-string") |> ignore
        let absent = evaluate preflight "\"guarded-write\" file.exists?" |> expectOk "check preflight did not write"
        equal "false" (stackValue absent 0) "an empty map callback still participates in effect preflight"
        expectError "CAPABILITY_DENIED" (evaluate preflight "\"guarded-each\" \"value\" file.write list.empty<Int> list.each container.clock-touch") |> ignore
        let eachAbsent = evaluate preflight "\"guarded-each\" file.exists?" |> expectOk "check each preflight did not write"
        equal "false" (stackValue eachAbsent 0) "an empty each callback still participates in effect preflight"

        dispatch runtime "commit" [ "word", jsonString "container.envelope-email-count" ]
        |> expectOk "commit nested-container word and referenced nominal types"
        |> ignore
        let reloaded = engine projectPath [ "fs.read"; "fs.write" ]
        let persisted =
            evaluate reloaded "\"dev@example.com\" Email.new list.singleton<Email> option.none<Email> \"unused\" result.error<List<Int>, String> containerEnvelope.new container.envelope-email-count"
            |> expectOk "run nested record word after fresh engine reload"
        equal "1" (stackValue persisted 0) "nested generic record field and signature persist"

        dispatch reloaded "task.begin" [ "goal", jsonString "temporary nested container word" ] |> expectOk "begin temporary-container task" |> ignore
        let temporary =
            """word container.temporary-nested : List<Result<Int, String>> -> List<Result<Int, String>>
    effects none
    dup drop
end
"""
        dispatch reloaded "define" [ "source", jsonString temporary; "temporary", jsonBool true ]
        |> expectOk "define temporary nested container word"
        |> ignore
        dispatch reloaded "task.abort" [] |> expectOk "abort temporary-container task" |> ignore
        expectError "NAME_UNKNOWN_WORD" (dispatch reloaded "source" [ "word", jsonString "container.temporary-nested" ]) |> ignore

    let private testContainerLibraryCoverage root =
        let runtime = engine (makeProject root "container-library-coverage") []
        let optionWord =
            """word option.library-default : Option<Int> -> Int
    effects none
    match-option
    some value
        $value
    none
        0
    end
end

test option.library-default/some
    3 option.some<Int> option.library-default
    => 3
end
"""
        define runtime optionWord |> expectOk "define partially covered option library word" |> ignore
        let optionCommit = [ "word", jsonString "option.library-default"; "library", jsonBool true ]
        let missingNone = dispatch runtime "commit" optionCommit |> expectError "LIBRARY_COVERAGE_INCOMPLETE"
        check ((missingNone["error"]["actual"]).ToJsonString().Contains("none", StringComparison.Ordinal)) "option coverage reports the missing none outcome"
        let optionNoneTest = """test option.library-default/none
    option.none<Int> option.library-default
    => 0
end
"""
        define runtime optionNoneTest |> expectOk "add none case test" |> ignore
        dispatch runtime "commit" optionCommit |> expectOk "commit option library after both cases" |> ignore
        let optionCoverage = dispatch runtime "describe" [ "word", jsonString "option.library-default" ] |> expectOk "inspect option coverage"
        let optionCoverageData = (optionCoverage["data"]["coverage"]).AsObject()
        let optionCoveredCases = optionCoverageData["branchesCovered"].GetValue<int>()
        equal 2 optionCoveredCases "option library covers both cases"

        let resultWord =
            """word result.library-value : Result<Int, String> -> Int
    effects none
    match-result
    ok value
        $value
    error message
        0
    end
end

test result.library-value/ok
    3 result.ok<Int, String> result.library-value
    => 3
end
"""
        define runtime resultWord |> expectOk "define partially covered result library word" |> ignore
        let resultCommit = [ "word", jsonString "result.library-value"; "library", jsonBool true ]
        let missingError = dispatch runtime "commit" resultCommit |> expectError "LIBRARY_COVERAGE_INCOMPLETE"
        check ((missingError["error"]["actual"]).ToJsonString().Contains("error", StringComparison.Ordinal)) "result coverage reports the missing error outcome"
        let resultErrorTest = """test result.library-value/error
    "failure" result.error<Int, String> result.library-value
    => 0
end
"""
        define runtime resultErrorTest |> expectOk "add error case test" |> ignore
        dispatch runtime "commit" resultCommit |> expectOk "commit result library after both cases" |> ignore

        let mapDefinitions =
            """word coverage.increment : Int -> Int
    effects none
    1 add
end

test coverage.increment/basic
    1 coverage.increment
    => 2
end

word coverage.map-count : List<Int> -> Int
    effects none
    list.map coverage.increment
    list.count
end

test coverage.map-count/empty
    list.empty<Int> coverage.map-count
    => 0
end
"""
        define runtime mapDefinitions |> expectOk "define map library word with empty test" |> ignore
        let mapCommit = [ "word", jsonString "coverage.map-count"; "library", jsonBool true ]
        let missingNonemptyMap = dispatch runtime "commit" mapCommit |> expectError "LIBRARY_COVERAGE_INCOMPLETE"
        check ((missingNonemptyMap["error"]["actual"]).ToJsonString().Contains("nonempty", StringComparison.Ordinal)) "map coverage reports nonempty iteration"
        let mapNonemptyTest = """test coverage.map-count/nonempty
    3 list.singleton<Int> coverage.map-count
    => 1
end
"""
        define runtime mapNonemptyTest |> expectOk "add nonempty map test" |> ignore
        dispatch runtime "commit" mapCommit |> expectOk "commit fully exercised map library word" |> ignore

        let filterDefinitions =
            """word coverage.negative? : Int -> Bool
    effects none
    0 int.less-than
end

test coverage.negative?/negative
    -1 coverage.negative?
    => true
end

word coverage.negative-count : List<Int> -> Int
    effects none
    list.filter coverage.negative?
    list.count
end

test coverage.negative-count/empty
    list.empty<Int> coverage.negative-count
    => 0
end
"""
        define runtime filterDefinitions |> expectOk "define filter library word with empty test" |> ignore
        let filterCommit = [ "word", jsonString "coverage.negative-count"; "library", jsonBool true ]
        let missingFilterOutcomes = dispatch runtime "commit" filterCommit |> expectError "LIBRARY_COVERAGE_INCOMPLETE"
        for outcome in [ "nonempty"; "keep"; "drop" ] do
            check ((missingFilterOutcomes["error"]["actual"]).ToJsonString().Contains(outcome, StringComparison.Ordinal)) $"filter coverage reports {outcome}"
        let filterOutcomesTest = """test coverage.negative-count/keep-and-drop
    -1 list.singleton<Int> 2 list.append coverage.negative-count
    => 1
end
"""
        define runtime filterOutcomesTest |> expectOk "add filter keep and drop test" |> ignore
        dispatch runtime "commit" filterCommit |> expectOk "commit filter after all iteration outcomes" |> ignore

        let eachDefinitions =
            """word coverage.ignore : Int -> Unit
    effects none
    drop unit
end

test coverage.ignore/basic
    1 coverage.ignore
    => unit
end

word coverage.each : List<Int> -> Unit
    effects none
    list.each coverage.ignore
end

test coverage.each/empty
    list.empty<Int> coverage.each
    => unit
end
"""
        define runtime eachDefinitions |> expectOk "define each library word with empty test" |> ignore
        let eachCommit = [ "word", jsonString "coverage.each"; "library", jsonBool true ]
        let missingEachIteration = dispatch runtime "commit" eachCommit |> expectError "LIBRARY_COVERAGE_INCOMPLETE"
        check ((missingEachIteration["error"]["actual"]).ToJsonString().Contains("nonempty", StringComparison.Ordinal)) "each coverage reports nonempty iteration"
        let eachNonemptyTest = """test coverage.each/nonempty
    1 list.singleton<Int> coverage.each
    => unit
end
"""
        define runtime eachNonemptyTest |> expectOk "add nonempty each test" |> ignore
        dispatch runtime "commit" eachCommit |> expectOk "commit each after empty and nonempty tests" |> ignore

    let private testDiscoveryCommands root =
        let runtime = engine (makeProject root "discovery-commands") [ "fs.read"; "fs.write" ]
        let definitions =
            """type Email : String
    validate email.valid?
end

record Contact
    field address Email
end

word email.valid? : String -> Bool
    effects none
    drop true
end

word email.echo : Email -> Email
    effects none
    dup drop
end

word email.boxed : Option<List<Email>> -> Option<List<Email>>
    effects none
    dup drop
end

word email.is-valid? : Email -> Bool
    effects none
    drop true
end

word email.filter : List<Email> -> List<Email>
    effects none
    list.filter email.is-valid?
    doc "Filter café addresses: Ω."
end

word discovery.write : Unit -> Unit
    effects fs.write
    drop
    "discovery-ran" "yes" file.write
end

word discovery.live : Email -> Email
    effects none
    dup drop
end
"""
        define runtime definitions |> expectOk "define discovery vocabulary" |> ignore

        let syntax = dispatch runtime "type-of" [ "word", jsonString "list.map" ] |> expectOk "type-of syntax descriptor"
        equal "syntax" ((syntax["data"]["kind"]).GetValue<string>()) "type-of uses the syntax descriptor shape"
        let metadata = dispatch runtime "type-of" [ "word", jsonString "email.echo" ] |> expectOk "type-of current word"
        equal "Email" (((metadata["data"]["inputs"]).[0]).GetValue<string>()) "type-of preserves nominal input type"
        expectError "NAME_UNKNOWN_WORD" (dispatch runtime "type-of" [ "word", jsonString "discovery.missing" ]) |> ignore

        let queryType value = dispatch runtime "search-type" [ "type", jsonString value ] |> expectOk $"search-type {value}"
        let nested = queryType "Option<List<Email>>"
        let nestedNames = ((nested["data"]["words"]).AsArray() |> Seq.map (fun item -> item.GetValue<string>()) |> Seq.toList)
        equal [ "email.boxed" ] nestedNames "nested nominal structural query is exact"
        let emailNames = queryType "Email" |> fun result -> (result["data"]["words"]).AsArray() |> Seq.map (fun item -> item.GetValue<string>()) |> Seq.toList
        check (emailNames |> List.contains "email.echo") "nominal query finds a direct Email signature"
        check (emailNames |> List.contains "email.filter") "nominal query descends through List"
        check (not (emailNames |> List.contains "email.valid?")) "nominal Email does not coerce to its String base"
        let stringNames = queryType "String" |> fun result -> (result["data"]["words"]).AsArray() |> Seq.map (fun item -> item.GetValue<string>()) |> Seq.toList
        check (stringNames |> List.contains "email.valid?") "String query finds the scalar validator signature"
        check (not (stringNames |> List.contains "email.echo")) "String query does not match nominal Email"
        let outputNames = dispatch runtime "search-output" [ "type", jsonString "Email" ] |> expectOk "search-output Email" |> fun result -> (result["data"]["words"]).AsArray() |> Seq.map (fun item -> item.GetValue<string>()) |> Seq.toList
        check (outputNames |> List.contains "email.echo") "output query finds Email result"
        check (not (outputNames |> List.contains "email.valid?")) "output query does not coerce validator Bool to Email"

        let effectMatches = dispatch runtime "search-effect" [ "effect", jsonString "fs.write" ] |> expectOk "search declared effect"
        let effectNames = (effectMatches["data"]["words"]).AsArray() |> Seq.map (fun item -> item.GetValue<string>()) |> Seq.toList
        check (effectNames |> List.contains "discovery.write") "effect search sees a live candidate"
        check (effectNames |> List.contains "file.write") "effect search sees the primitive declaration"
        equal 0 (dispatch runtime "search-effect" [ "effect", jsonString "unknown.effect" ] |> expectOk "unknown effect is an empty query" |> fun result -> (result["data"]["count"]).GetValue<int>()) "unknown nonempty effect returns no matches"

        let validatorEdges = dispatch runtime "search-dependency" [ "word", jsonString "email.valid?" ] |> expectOk "search scalar validator edge"
        let validatorNames = (validatorEdges["data"]["words"]).AsArray() |> Seq.map (fun item -> item.GetValue<string>()) |> Seq.toList
        check (validatorNames |> List.contains "Email.new") "generated scalar constructor has a validator edge"
        let callbackEdges = dispatch runtime "search-dependency" [ "word", jsonString "email.is-valid?" ] |> expectOk "search callback edge"
        let callbackNames = (callbackEdges["data"]["words"]).AsArray() |> Seq.map (fun item -> item.GetValue<string>()) |> Seq.toList
        check (callbackNames |> List.contains "email.filter") "static filter callback is a dependency edge"
        expectError "DISCOVERY_UNKNOWN_WORD" (dispatch runtime "search-dependency" [ "word", jsonString "discovery.missing" ]) |> ignore
        let dependencies = dispatch runtime "transitive-dependencies" [ "word", jsonString "email.filter" ] |> expectOk "transitive dependency closure"
        check (((dependencies["data"]["dependencies"]).ToJsonString()).Contains("email.is-valid?", StringComparison.Ordinal)) "transitive dependencies include static callback"
        let callers = dispatch runtime "transitive-callers" [ "word", jsonString "email.is-valid?" ] |> expectOk "transitive caller closure"
        check (((callers["data"]["callers"]).ToJsonString()).Contains("email.filter", StringComparison.Ordinal)) "transitive callers include static callback owner"
        expectError "DISCOVERY_UNKNOWN_ROOT" (dispatch runtime "transitive-dependencies" [ "word", jsonString "discovery.missing" ]) |> ignore

        let graph = dispatch runtime "graph" [ "word", jsonString "email.filter"; "maxDepth", JsonValue.Create(0) :> JsonNode; "maxNodes", JsonValue.Create(1) :> JsonNode ] |> expectOk "bounded graph"
        check ((graph["data"]["truncated"]).GetValue<bool>()) "graph reports depth truncation"
        equal 1 ((graph["data"]["expandedWords"]).AsArray().Count) "graph expands no more than the depth-zero root"
        check ((graph["data"]["text"]).GetValue<string>().Contains("depth limit", StringComparison.Ordinal)) "graph identifies depth-limited edges"

        let contextArgs = [ "word", jsonString "email.filter"; "maxDepth", JsonValue.Create(4) :> JsonNode; "maxWords", JsonValue.Create(12) :> JsonNode; "maxUtf8Bytes", JsonValue.Create(12000) :> JsonNode ]
        let context = dispatch runtime "context" contextArgs |> expectOk "bounded context"
        let contextData = context["data"]
        let compactOptions = JsonSerializerOptions(WriteIndented = false)
        let contextPayload = contextData.ToJsonString(compactOptions)
        equal ((contextData["utf8Bytes"]).GetValue<int>()) (Encoding.UTF8.GetByteCount(contextPayload)) "context utf8Bytes equals exact compact data payload bytes"
        use wireResponse = JsonDocument.Parse(Protocol.serializeResponse context)
        let wireDataPayload = wireResponse.RootElement.GetProperty("data").GetRawText()
        equal ((contextData["utf8Bytes"]).GetValue<int>()) (Encoding.UTF8.GetByteCount(wireDataPayload)) "context utf8Bytes matches the actual protocol data payload"
        let tightContextArgs = [ "word", jsonString "email.filter"; "maxDepth", JsonValue.Create(0) :> JsonNode; "maxWords", JsonValue.Create(1) :> JsonNode; "maxUtf8Bytes", JsonValue.Create(900) :> JsonNode ]
        let tightContext = dispatch runtime "context" tightContextArgs |> expectOk "context at tight UTF-8 budget"
        let tightData = tightContext["data"]
        let tightPayload = tightData.ToJsonString(compactOptions)
        let tightBytes = Encoding.UTF8.GetByteCount(tightPayload)
        check (tightBytes <= 900) "tight context stays within its UTF-8 budget"
        equal ((tightData["utf8Bytes"]).GetValue<int>()) tightBytes "tight context reports the serialized data bytes"
        use tightWireResponse = JsonDocument.Parse(Protocol.serializeResponse tightContext)
        let tightWirePayload = tightWireResponse.RootElement.GetProperty("data").GetRawText()
        equal ((tightData["utf8Bytes"]).GetValue<int>()) (Encoding.UTF8.GetByteCount(tightWirePayload)) "tight context matches the actual protocol payload"
        check ((tightData["truncated"]).GetValue<bool>()) "tight context reports omitted dependencies"
        let repeatedContext = dispatch runtime "context" contextArgs |> expectOk "repeat bounded context"
        equal contextPayload ((repeatedContext["data"]).ToJsonString(compactOptions)) "repeated context is deterministic"
        let contextWords = (contextData["words"]).AsArray()
        check (contextWords |> Seq.exists (fun item -> (item["name"]).GetValue<string>() = "email.filter")) "context contains the root word"
        let allWords = dispatch runtime "words" [] |> expectOk "inspect whole dictionary for comparison" |> fun result -> (result["data"]["words"]).AsArray()
        check (contextWords.Count < allWords.Count) "context is not an unbounded dictionary dump"
        check (not (contextPayload.Contains("discovery.write", StringComparison.Ordinal))) "context excludes unrelated words"

        expectError "DISCOVERY_UNKNOWN_TYPE" (dispatch runtime "search-type" [ "type", jsonString "NotDeclared" ]) |> ignore
        expectError "PARSE_OPEN_TYPE" (dispatch runtime "search-type" [ "type", jsonString "Option<a>" ]) |> ignore
        expectError "PARSE_INVALID_TYPE" (dispatch runtime "search-type" [ "type", jsonString "List<Email" ]) |> ignore
        expectError "DISCOVERY_INVALID_ARGUMENT" (dispatch runtime "search-type" [ "type", JsonValue.Create(7) :> JsonNode ]) |> ignore
        expectError "DISCOVERY_INVALID_ARGUMENT" (dispatch runtime "search-effect" [ "effect", jsonBool true ]) |> ignore
        expectError "DISCOVERY_INVALID_ARGUMENT" (dispatch runtime "graph" [ "word", jsonString "email.filter"; "maxNodes", jsonString "2" ]) |> ignore
        expectError "DISCOVERY_INVALID_ARGUMENT" (dispatch runtime "context" [ "word", jsonString "email.filter"; "maxWords", jsonString "12" ]) |> ignore
        expectError "DISCOVERY_INVALID_BUDGET" (dispatch runtime "graph" [ "word", jsonString "email.filter"; "maxDepth", JsonValue.Create(-1) :> JsonNode ]) |> ignore
        expectError "DISCOVERY_INVALID_BUDGET" (dispatch runtime "context" [ "word", jsonString "email.filter"; "maxWords", JsonValue.Create(0) :> JsonNode ]) |> ignore
        expectError "DISCOVERY_BUDGET_LIMIT" (dispatch runtime "graph" [ "word", jsonString "email.filter"; "maxNodes", JsonValue.Create(513) :> JsonNode ]) |> ignore
        expectError "DISCOVERY_BUDGET_LIMIT" (dispatch runtime "context" [ "word", jsonString "email.filter"; "maxDepth", JsonValue.Create(33) :> JsonNode ]) |> ignore
        expectError "DISCOVERY_CONTEXT_BUDGET_TOO_SMALL" (dispatch runtime "context" [ "word", jsonString "email.filter"; "maxUtf8Bytes", JsonValue.Create(1) :> JsonNode ]) |> ignore
        expectError "DISCOVERY_INVALID_ARGUMENT" (dispatch runtime "context" [ "maxWords", JsonValue.Create(1) :> JsonNode ]) |> ignore

        let protocol = Protocol.dispatchLine runtime "{\"op\":\"search-type\",\"type\":\"Option<List<Email>>\"}" |> expectOk "JSON-lines Discovery request"
        equal "search-type" ((protocol["kind"]).GetValue<string>()) "JSON-lines routes Discovery commands"

        define runtime
            """word discovery.live : String -> String
    effects none
    dup drop
end
"""
        |> expectOk "replace live candidate signature" |> ignore
        let liveEmail = queryType "Email" |> fun result -> (result["data"]["words"]).ToJsonString()
        let liveString = queryType "String" |> fun result -> (result["data"]["words"]).ToJsonString()
        check (not (liveEmail.Contains("discovery.live", StringComparison.Ordinal))) "fresh index drops the replaced candidate's old type"
        check (liveString.Contains("discovery.live", StringComparison.Ordinal)) "fresh index sees the current candidate replacement"

        dispatch runtime "task.begin" [ "goal", jsonString "verify discovery is read-only" ] |> expectOk "begin read-only discovery task" |> ignore
        dispatch runtime "search-effect" [ "effect", jsonString "fs.write" ] |> expectOk "inspect effect without running it" |> ignore
        let status = dispatch runtime "task.status" [] |> expectOk "inspect task after query"
        equal 0 ((status["data"]["effects"]).AsObject().Count) "discovery does not record provider effects"
        equal 0 ((status["data"]["wordsUsed"]).AsArray().Count) "discovery does not invoke words"
        check ((status["data"]["wordsInspected"]).AsArray().Count > 0) "discovery records inspection activity"
        let missingFile = evaluate runtime "\"discovery-ran\" file.exists?" |> expectOk "check provider after discovery query"
        equal "false" (stackValue missingFile 0) "discovery did not run the effectful candidate"
        dispatch runtime "task.abort" [] |> expectOk "abort read-only discovery task" |> ignore

    let private testCompactWords root =
        let path = makeProject root "compact-words"
        let runtime = engine path [ "fs.write" ]
        let initialVocabulary =
            """record InventoryProbe
    field value Int
end

word inventory.retained : Int -> Int
    effects none
    1 add
end

test inventory.retained/basic
    1 inventory.retained
    => 2
end
"""
        define runtime initialVocabulary |> expectOk "define compact inventory schema and persistent word" |> ignore
        dispatch runtime "commit" [ "word", jsonString "InventoryProbe" ] |> expectOk "commit compact inventory schema" |> ignore
        dispatch runtime "commit" [ "word", jsonString "inventory.retained" ] |> expectOk "commit compact inventory word" |> ignore
        dispatch runtime "deprecate" [ "word", jsonString "inventory.retained" ] |> expectOk "deprecate compact inventory word" |> ignore

        let stagedVocabulary =
            """word inventory.candidate : Int -> Int
    effects none
    2 add
end

word inventory.writer : Unit -> Unit
    effects fs.write
    drop
    "compact-provider" "called" file.write
end
"""
        define runtime stagedVocabulary |> expectOk "define candidate compact inventory words" |> ignore
        dispatch runtime "define"
            [ "source", jsonString "word inventory.temporary : Int -> Int\n    effects none\n    3 add\nend\n"
              "temporary", jsonBool true ]
        |> expectOk "define temporary compact inventory word"
        |> ignore

        let fullDefault = dispatch runtime "words" [] |> expectOk "list the full compact fixture inventory"
        let fullFalse = dispatch runtime "words" [ "compact", jsonBool false ] |> expectOk "list full words with compact false"
        equal (fullDefault.ToJsonString()) (fullFalse.ToJsonString()) "default and compact=false preserve the exact full response"
        let fullData = fullDefault.["data"]
        check (not (fullData.AsObject().ContainsKey("compact"))) "full response has no compact marker"
        let fullWordEntries = fullData.["words"].AsArray()
        let fullNames = fullWordEntries |> Seq.map (fun item -> item.["name"].GetValue<string>()) |> Seq.toList
        let expectedNames = fullNames |> List.sort
        let fullConstructNames = fullData.["constructs"].AsArray() |> Seq.map (fun item -> item.["name"].GetValue<string>()) |> Seq.toList
        let expectedConstructNames = fullConstructNames |> List.sort
        for name in [ "add"; "inventoryProbe.new"; "inventory.retained"; "inventory.candidate"; "inventory.temporary"; "inventory.writer" ] do
            check (expectedNames |> List.contains name) $"full inventory includes {name}"

        let descriptionData name =
            dispatch runtime "describe" [ "word", jsonString name ]
            |> expectOk $"capture full description of {name}"
            |> fun response -> response.["data"]
        let describedNames = [ "add"; "inventoryProbe.new"; "inventory.retained"; "inventory.candidate"; "inventory.temporary"; "inventory.writer" ]
        let descriptionsBefore = describedNames |> List.map (fun name -> name, (descriptionData name).ToJsonString())
        equal "persistent" ((descriptionData "inventory.retained").["status"].GetValue<string>()) "persistent inventory word remains persistent"
        check ((descriptionData "inventory.retained").["deprecated"].GetValue<bool>()) "deprecated inventory word remains marked deprecated"
        equal "candidate" ((descriptionData "inventory.candidate").["status"].GetValue<string>()) "candidate inventory word remains a candidate"
        equal "temporary" ((descriptionData "inventory.temporary").["status"].GetValue<string>()) "temporary inventory word remains temporary"
        equal "generated" ((descriptionData "inventoryProbe.new").["kind"].GetValue<string>()) "generated inventory word retains its kind"

        let compact = dispatch runtime "words" [ "compact", jsonBool true ] |> expectOk "list compact words"
        let compactData = compact.["data"]
        check (compactData.["compact"].GetValue<bool>()) "compact response identifies its shape"
        equal 3 (compactData.AsObject().Count) "compact response contains only its marker and inventories"
        let compactNames = compactData.["words"].AsArray() |> Seq.map (fun item -> item.GetValue<string>()) |> Seq.toList
        equal expectedNames compactNames "compact word names exactly match the full sorted inventory"
        let compactConstructNames = compactData.["constructs"].AsArray() |> Seq.map (fun item -> item.GetValue<string>()) |> Seq.toList
        equal expectedConstructNames compactConstructNames "compact construct names exactly match the sorted syntax inventory"
        for name, before in descriptionsBefore do
            equal before ((descriptionData name).ToJsonString()) $"compact inventory leaves full {name} metadata unchanged"

        let fullBytes = Encoding.UTF8.GetByteCount(Protocol.serializeResponse fullDefault)
        let compactBytes = Encoding.UTF8.GetByteCount(Protocol.serializeResponse compact)
        check (compactBytes * 100 <= fullBytes * 30) $"compact response is at most 30 percent of full response ({compactBytes} vs {fullBytes} UTF-8 bytes)"

        dispatch runtime "task.begin" [ "goal", jsonString "validate compact argument behavior" ] |> expectOk "begin compact argument task" |> ignore
        let taskFingerprint (response: JsonObject) =
            let task = response.["data"]
            [ "wordsInspected"; "wordsUsed"; "wordsCreated"; "effects" ]
            |> List.map (fun key -> task.[key].ToJsonString())
        let taskBefore = dispatch runtime "task.status" [] |> expectOk "capture compact task state" |> taskFingerprint
        let invalidCompactValues: JsonNode list = [ jsonString "true"; null; JsonValue.Create(7) :> JsonNode ]
        for value in invalidCompactValues do
            dispatch runtime "words" [ "compact", value ]
            |> expectError "EVAL_INVALID_ARGUMENT"
            |> ignore
            let taskAfter = dispatch runtime "task.status" [] |> expectOk "inspect task after invalid compact argument" |> taskFingerprint
            equal taskBefore taskAfter "invalid compact argument does not inspect or invoke words or providers"
        let fullAfterInvalid = dispatch runtime "words" [] |> expectOk "verify inventory after invalid compact arguments"
        equal (fullDefault.ToJsonString()) (fullAfterInvalid.ToJsonString()) "invalid compact arguments leave the dictionary inventory unchanged"

        dispatch runtime "task.abort" [] |> expectOk "end compact argument task" |> ignore
        let topLevelProtocol = Protocol.dispatchLine runtime "{\"op\":\"words\",\"compact\":true}" |> expectOk "route top-level compact JSONL argument"
        let nestedProtocol = Protocol.dispatchLine runtime "{\"op\":\"words\",\"args\":{\"compact\":true}}" |> expectOk "route nested compact JSONL argument"
        check (topLevelProtocol.["data"].["compact"].GetValue<bool>()) "top-level compact argument passes through the JSONL protocol"
        check (nestedProtocol.["data"].["compact"].GetValue<bool>()) "nested compact argument passes through the JSONL protocol"
        equal (topLevelProtocol.["data"].ToJsonString()) (nestedProtocol.["data"].ToJsonString()) "top-level and nested protocol inventories match"

        let logRuntime = engine (makeProject root "compact-words-logs") []
        dispatch logRuntime "task.begin" [ "goal", jsonString "compare compact inspection logs" ] |> expectOk "begin full inventory log task" |> ignore
        dispatch logRuntime "words" [] |> expectOk "inspect full inventory" |> ignore
        let fullInspectionLog = dispatch logRuntime "task.status" [] |> expectOk "read full inventory inspection log" |> fun response -> response.["data"].["wordsInspected"].ToJsonString()
        dispatch logRuntime "task.abort" [] |> expectOk "reset full inventory inspection task" |> ignore
        dispatch logRuntime "task.begin" [ "goal", jsonString "compare compact inspection logs" ] |> expectOk "begin compact inventory log task" |> ignore
        dispatch logRuntime "words" [ "compact", jsonBool true ] |> expectOk "inspect compact inventory" |> ignore
        let compactInspectionLog = dispatch logRuntime "task.status" [] |> expectOk "read compact inventory inspection log" |> fun response -> response.["data"].["wordsInspected"].ToJsonString()
        equal fullInspectionLog compactInspectionLog "compact inventory preserves full inspection logging"

    let private testVerifiedIrRuntimeSurface root =
        let path = makeProject root "verified-ir-runtime"
        let runtime = engine path []
        let initialSource =
            """word ir.snapshot : Int -> Int
    effects none
    1 add
end

record IrPoint
    field x Int
end

test ir.snapshot/increment
    2 ir.snapshot
=> 3
end

test irPoint.x/read
    7 irPoint.new irPoint.x
=> 7
end
"""
        define runtime initialSource |> expectOk "define IR-backed candidate and test" |> ignore
        let candidateIr = dispatch runtime "ir" [ "word", jsonString "ir.snapshot" ] |> expectOk "format candidate IR"
        equal "function" ((candidateIr["data"]["kind"]).GetValue<string>()) "ir command returns a verified function DTO"
        check ((candidateIr["data"]["wordId"]).GetValue<string>().StartsWith("word_", StringComparison.Ordinal)) "IR formatter exposes the stable user word id"
        check (candidateIr.ToJsonString().Contains("\"kind\":\"call\"", StringComparison.Ordinal)) "IR formatter exposes typed call operations"
        check (candidateIr.ToJsonString().Contains("add", StringComparison.Ordinal)) "IR formatter resolves the primitive target"
        let generatedIr = dispatch runtime "ir" [ "word", jsonString "irPoint.new" ] |> expectOk "format generated constructor IR"
        equal "generated-word" ((generatedIr["data"]["kind"]).GetValue<string>()) "generated target formatter displays a verified operation"
        equal "3" (stackValue (evaluate runtime "2 ir.snapshot" |> expectOk "execute compiled candidate") 0) "candidate executes its verified snapshot"

        dispatch runtime "commit" [ "word", jsonString "ir.snapshot" ] |> expectOk "commit IR-backed word" |> ignore
        let changedSource =
            """word ir.snapshot : Int -> Int
    effects none
    2 add
end

test ir.snapshot/increment
    2 ir.snapshot
=> 4
end
"""
        define runtime changedSource |> expectOk "stage IR replacement" |> ignore
        let replacementIr = dispatch runtime "ir" [ "word", jsonString "ir.snapshot" ] |> expectOk "format staged replacement IR"
        check (candidateIr.ToJsonString() <> replacementIr.ToJsonString()) "IR query observes the current replacement snapshot"
        equal "4" (stackValue (evaluate runtime "2 ir.snapshot" |> expectOk "execute staged replacement") 0) "replacement executes through the new snapshot"
        dispatch runtime "discard" [ "word", jsonString "ir.snapshot" ] |> expectOk "discard IR replacement" |> ignore
        equal "3" (stackValue (evaluate runtime "2 ir.snapshot" |> expectOk "execute restored persistent snapshot") 0) "discard reactivates the prior compiled snapshot"

        let generatedCommit = dispatch runtime "commit" [ "word", jsonString "IrPoint" ] |> expectOk "commit generated-target metadata"
        let generatedResults = (generatedCommit["data"]).AsArray()
        equal 1 generatedResults.Count "generated target commit gate result count"
        equal "irPoint.x/read" (generatedResults[0].GetValue<string>()) "generated owner test ran before type publication"

        let reloaded = engine path []
        equal "3" (stackValue (evaluate reloaded "2 ir.snapshot" |> expectOk "execute reloaded IR program") 0) "reload compiles and executes the committed program"
        assertAllTestsPassed "reloaded IR program" 2 (dispatch reloaded "test-all" [] |> expectOk "run reloaded IR tests")
        let primitive = dispatch reloaded "ir" [ "word", jsonString "add" ] |> expectOk "format primitive contract"
        equal "primitive-contract" ((primitive["data"]["kind"]).GetValue<string>()) "primitive IR output is explicitly a canonical contract"

    [<EntryPoint>]
    let main _ =
        let temporaryRoot = Path.Combine(Path.GetTempPath(), "agentlang-acceptance-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(temporaryRoot) |> ignore
        let cases =
            [ "JSON-lines protocol and eval", testEvalAndJsonLines
              "checked numeric boundaries", testCheckedArithmetic
              "effects and capability boundary", testEffectsAndCapabilityBoundary
              "Customer records, locals, and branches", testDemoRecordLocalsAndBranches
              "test-gated project commits", testProjectCommitRequirements
              "candidate dependency persistence", testCandidateDependenciesPersist
              "selected metadata dependency closure", testSelectedMetadataDependenciesPersist
              "temporary metadata dependency rejection", testTemporaryMetadataDependencyRejected
              "temporary session isolation", testTemporaryWordsStaySessionOnly
              "scoped replacement and discard", testScopedReplacementAndDiscard
              "scoped test metadata persistence", testScopedTestMetadataPersistence
              "replacement caller tests gate durable updates", testReplacementCallerTests
              "stable word identity lifecycle", testWordIdentityLifecycle
              "runtime error test expectations", testRuntimeErrorExpectations
              "typed value-expression expectations", testValueExpressionExpectations
              "expectation-only dependency persistence", testExpectedExpressionDependencies
              "expected-expression rename and coverage isolation", testExpectedExpressionRenameAndCoverage
              "strict structured eval and publication", testStructuredEvalStrictnessAndPublication
              "nominal and refined types", testRefinedTypesAndNominality
              "validator rejection", testInvalidValidatorsRejected
              "type-only commit", testTypeOnlyCommit
              "frozen validator replacement", testValidatorTemporaryOverrideRejected
              "raw source-site coverage identity", testRawCoverageSiteIdentity
              "library coverage gate", testLibraryCoverageGate
              "task abort rollback", testTaskAbortRollsBackDictionary
              "task commit temporary cleanup", testTaskCommitClearsSessionWords
              "task logs survive reload", testTaskLogsSurviveReload
              "task log failures remain truthful warnings", testTaskLogWriteFailureIsWarning
              "named snapshot restores project and providers", testNamedSnapshotRestoresProjectAndProviders
              "semantic rename and deprecation", testSemanticRenameAndDeprecation
              "typed containers and syntax metadata", testTypedContainersAndLanguageConstructs
              "container library coverage", testContainerLibraryCoverage
              "live bounded Discovery commands", testDiscoveryCommands
              "compact word inventory", testCompactWords
              "verified IR runtime snapshots and formatter", testVerifiedIrRuntimeSurface ]
        let failures = ResizeArray<string>()
        try
            for name, run in cases do
                try
                    run temporaryRoot
                    Console.WriteLine($"PASS {name}")
                with ex ->
                    failures.Add($"{name}: {ex.Message}")
                    Console.Error.WriteLine($"FAIL {name}: {ex}")
        finally
            try Directory.Delete(temporaryRoot, true)
            with _ -> ()

        if failures.Count = 0 then
            Console.WriteLine($"All {cases.Length} acceptance groups passed ({assertions} assertions).")
            0
        else
            let summary = String.Join(" | ", failures)
            Console.Error.WriteLine($"{failures.Count} acceptance group(s) failed: {summary}")
            1
