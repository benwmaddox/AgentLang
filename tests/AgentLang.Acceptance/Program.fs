namespace AgentLang.Acceptance

open System
open System.IO
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

    let private dispatch (engine: Runtime.Engine) operation values =
        engine.Dispatch(operation, arguments values)

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
        let path = Path.Combine(Environment.CurrentDirectory, "examples", fileName)
        if not (File.Exists path) then failwith $"Could not find demo source at {path}."
        File.ReadAllText path

    let private demoSource () = exampleSource "customer.agent"

    let private testEvalAndJsonLines root =
        let runtime = engine (makeProject root "json-lines") []
        let result = evaluate runtime "10 20 add" |> expectOk "basic eval"
        equal "30" (stackValue result 0) "10 20 add"

        let input =
            new StringReader(
                "{\"op\":\"eval\",\"code\":\"10 20 add\"}\n" +
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
        check (sourceText.Contains("1 add", StringComparison.Ordinal)) "unselected replacement backup remains in durable dictionary"
        check (not (sourceText.Contains("2 add", StringComparison.Ordinal))) "unselected replacement body is absent from durable dictionary"
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
        check (reloadSourceText.Contains("1 add", StringComparison.Ordinal)) "abort restores original source on disk"
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
              "temporary session isolation", testTemporaryWordsStaySessionOnly
              "scoped replacement and discard", testScopedReplacementAndDiscard
              "scoped test metadata persistence", testScopedTestMetadataPersistence
              "nominal and refined types", testRefinedTypesAndNominality
              "validator rejection", testInvalidValidatorsRejected
              "type-only commit", testTypeOnlyCommit
              "frozen validator replacement", testValidatorTemporaryOverrideRejected
              "library coverage gate", testLibraryCoverageGate
              "task abort rollback", testTaskAbortRollsBackDictionary
              "task commit temporary cleanup", testTaskCommitClearsSessionWords
              "typed containers and syntax metadata", testTypedContainersAndLanguageConstructs
              "container library coverage", testContainerLibraryCoverage ]
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
