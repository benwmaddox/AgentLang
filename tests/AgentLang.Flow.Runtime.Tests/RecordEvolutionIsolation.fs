module RecordEvolutionIsolation

open System
open System.IO
open System.Text.Json.Nodes
open AgentLang

let run (root: string) =
    let mutable assertions = 0

    let check condition message =
        assertions <- assertions + 1
        if not condition then failwith message

    let equal expected actual message =
        assertions <- assertions + 1
        if expected <> actual then failwith $"{message}: expected {expected}, got {actual}"

    let jstr (value: string) = JsonValue.Create(value) :> JsonNode
    let jint (value: int) = JsonValue.Create(value) :> JsonNode
    let jbool (value: bool) = JsonValue.Create(value) :> JsonNode
    let stringValue (node: JsonNode) = node.GetValue<string>()
    let boolValue (node: JsonNode) = node.GetValue<bool>()

    let arguments (values: (string * JsonNode) list) =
        let result = JsonObject()
        for name, value in values do
            result.[name] <- if isNull value then null else value.DeepClone()
        result

    let dispatch (engine: Runtime.Engine) operation values =
        engine.Dispatch(operation, arguments values)

    let succeeded (response: JsonObject) = boolValue response.["ok"]

    let expectOk label (response: JsonObject) =
        check (succeeded response) $"{label}: expected success, got {response.ToJsonString()}"
        response

    let errorCode (response: JsonObject) = stringValue response.["error"].["code"]

    let expectError expectedCode (response: JsonObject) =
        check (not (succeeded response)) $"expected {expectedCode}, got success: {response.ToJsonString()}"
        equal expectedCode (errorCode response) "runtime diagnostic code"
        response

    let defineFlowProject (engine: Runtime.Engine) source extra =
        dispatch engine "define" ([ "frontend", jstr "flow"; "source", jstr source ] @ extra)

    let defineStack (engine: Runtime.Engine) source =
        dispatch engine "define" [ "frontend", jstr "stack"; "source", jstr source ]

    let commit (engine: Runtime.Engine) name =
        dispatch engine "commit" [ "word", jstr name ]

    let expectedTypeSources (sources: (string * string) list) =
        let result = JsonObject()
        for name, sourceHash in sources do result.[name] <- jstr sourceHash
        result :> JsonNode

    let expectedRevisions (revisions: (string * int) list) =
        let result = JsonObject()
        for name, revision in revisions do result.[name] <- jint revision
        result :> JsonNode

    let wordRevision (engine: Runtime.Engine) name =
        dispatch engine "describe" [ "word", jstr name ]
        |> expectOk $"inspect {name} revision"
        |> fun response -> response.["data"].["revision"].GetValue<int>()

    let typeSourceHash (engine: Runtime.Engine) name =
        dispatch engine "describe" [ "type", jstr name ]
        |> expectOk $"inspect {name} source hash"
        |> fun response -> stringValue response.["data"].["sourceHash"]

    let typeSource (store: Store) (loaded: StorageLoadResult) name =
        let manifest = loaded.Manifest |> Option.defaultWith (fun () -> failwith "expected a durable manifest")
        let typeHead = manifest.Types |> List.find (fun item -> item.Name = name)
        Storage.readSource store typeHead.Definition
        |> Result.defaultWith (fun problem -> failwith $"read durable {name} source: {problem.Code}: {problem.Message}")

    let wordSource (store: Store) (loaded: StorageLoadResult) name =
        let manifest = loaded.Manifest |> Option.defaultWith (fun () -> failwith "expected a durable manifest")
        let wordHead = manifest.Words |> List.find (fun item -> item.CurrentName = name)
        let revision = manifest.Revisions |> List.find (fun item -> item.WordId = wordHead.WordId && item.Revision = wordHead.CurrentRevision)
        Storage.readSource store revision.Definition
        |> Result.defaultWith (fun problem -> failwith $"read durable {name} source: {problem.Code}: {problem.Message}")

    let testSourceRows (engine: Runtime.Engine) word =
        dispatch engine "tests" [ "word", jstr word; "includeSource", jbool true ]
        |> expectOk $"inspect test sources for {word}"
        |> fun response -> response.["data"].ToJsonString()

    let testResult (engine: Runtime.Engine) word =
        dispatch engine "test" [ "word", jstr word ]
        |> expectOk $"run tests for {word}"
        |> fun response -> response.["data"].ToJsonString()

    let testOutcomeRows (engine: Runtime.Engine) word =
        dispatch engine "test" [ "word", jstr word ]
        |> expectOk $"run outcome rows for {word}"
        |> fun response -> response.["data"].["results"].ToJsonString()

    let assertAllPassed expectedCount (response: JsonObject) label =
        let results = response.["data"].["results"].AsArray()
        equal expectedCount results.Count $"{label} result count"
        for result in results do
            let caseName = stringValue result.["name"]
            check (boolValue result.["passed"]) $"{label} case {caseName} passes"

    let persistedRoot = Path.Combine(root, ".agentlang", "type-evolution-188", "isolation-tests")
    Directory.CreateDirectory(persistedRoot) |> ignore

    // A Flow-authored record can acquire Stack cases on its generated accessor
    // before publication. Removing that accessor later must leave the complete
    // persisted record and its retained case sources untouched.
    let retainedProject = Path.Combine(persistedRoot, "retained-generated-cases")
    let retainedEngine = Runtime.Engine(retainedProject, Set.empty, "2030-01-02T03:04:05Z", fileSystemMode = FileSystemMode.Virtual)
    let retainedRecordSource = "record Candidate { field amount: Int; }"
    defineFlowProject retainedEngine retainedRecordSource [ "syntaxVersion", jint 2 ]
    |> expectOk "stage the Flow-authored Candidate record"
    |> ignore

    let retainedCases =
        "test candidate.amount/read\n"
        + "    7\n"
        + "    candidate.new\n"
        + "    candidate.amount\n"
        + "    => 7\n"
        + "end\n\n"
        + "example candidate.amount/read\n"
        + "    9\n"
        + "    candidate.new\n"
        + "    candidate.amount\n"
        + "    => 9\n"
        + "end"
    defineStack retainedEngine retainedCases
    |> expectOk "attach Stack test and example sources to the generated accessor"
    |> ignore
    let initialCommit = commit retainedEngine "Candidate" |> expectOk "commit Candidate and its generated cases"
    check
        ((initialCommit.["data"].AsArray() |> Seq.map stringValue |> Seq.toList) |> List.contains "candidate.amount/read")
        "record publication executes the generated accessor's attached test"

    let retainedStore = Storage.create retainedProject
    let durableBefore =
        Storage.load retainedStore
        |> Result.defaultWith (fun problem -> failwith $"load committed Candidate: {problem.Code}: {problem.Message}")
    let durableSourceBefore = typeSource retainedStore durableBefore "Candidate"
    let durableManifestBefore = durableBefore.ManifestHash
    let generatedWordsBefore =
        dispatch retainedEngine "words" []
        |> expectOk "capture generated record words before rejected evolution"
        |> fun response -> response.["data"].ToJsonString()
    let generatedTestsBefore = testSourceRows retainedEngine "candidate.amount"
    let generatedTestExecutionBefore = testResult retainedEngine "candidate.amount"
    let generatedExampleExecutionBefore =
        dispatch retainedEngine "example" [ "word", jstr "candidate.amount"; "caseName", jstr "read" ]
        |> expectOk "run generated accessor example before rejected evolution"
        |> fun response -> response.["data"].ToJsonString()

    let reloaded = Runtime.Engine(retainedProject, Set.empty, "2030-01-02T03:04:05Z", fileSystemMode = FileSystemMode.Virtual)
    equal generatedTestsBefore (testSourceRows reloaded "candidate.amount") "reload preserves exact Stack test source rows"
    assertAllPassed 1 (dispatch reloaded "test" [ "word", jstr "candidate.amount" ] |> expectOk "run generated accessor test after reload") "reloaded accessor test"
    let reloadedExample =
        dispatch reloaded "example" [ "word", jstr "candidate.amount"; "caseName", jstr "read" ]
        |> expectOk "run generated accessor example after reload"
    equal generatedExampleExecutionBefore (reloadedExample.["data"].ToJsonString()) "reload preserves exact generated example source and execution"

    let removedAccessorSource = "record Candidate { field other: Int; }"
    let rejectedRemoval =
        defineFlowProject reloaded removedAccessorSource
            [ "syntaxVersion", jint 2
              "replace", jbool true
              "expectedTypeSources", expectedTypeSources [ "Candidate", typeSourceHash reloaded "Candidate" ] ]
    expectError "FLOW_TYPE_EVOLUTION_GENERATED_CASE_INCOMPATIBLE" rejectedRemoval |> ignore

    let durableAfterRejectedRemoval =
        Storage.load retainedStore
        |> Result.defaultWith (fun problem -> failwith $"reload Candidate after rejected evolution: {problem.Code}: {problem.Message}")
    equal durableManifestBefore durableAfterRejectedRemoval.ManifestHash "rejected accessor removal leaves the exact manifest authority unchanged"
    equal durableSourceBefore (typeSource retainedStore durableAfterRejectedRemoval "Candidate") "rejected accessor removal keeps the exact durable record source bytes"
    equal generatedWordsBefore
        (dispatch reloaded "words" [] |> expectOk "inspect generated words after rejected accessor removal" |> fun response -> response.["data"].ToJsonString())
        "rejected accessor removal restores the exact generated word inventory"
    equal generatedTestsBefore (testSourceRows reloaded "candidate.amount") "rejected accessor removal keeps the exact durable test sources"
    equal generatedTestExecutionBefore (testResult reloaded "candidate.amount") "rejected accessor removal leaves generated test execution unchanged"
    equal generatedExampleExecutionBefore
        (dispatch reloaded "example" [ "word", jstr "candidate.amount"; "caseName", jstr "read" ] |> expectOk "rerun generated example after rejected accessor removal" |> fun response -> response.["data"].ToJsonString())
        "rejected accessor removal leaves generated example execution unchanged"
    equal retainedRecordSource
        (dispatch reloaded "source" [ "type", jstr "Candidate" ] |> expectOk "read Candidate after rejected accessor removal" |> fun response -> stringValue response.["data"])
        "rejected accessor removal leaves the in-memory record source unchanged"

    // A separate Stack attachment edit on an operation introduced by the staged
    // group is refused, and discarding the group restores the exact old schema.
    let attachmentProject = Path.Combine(persistedRoot, "staged-attachment-edit")
    let attachmentEngine = Runtime.Engine(attachmentProject, Set.empty, "2030-01-02T03:04:05Z", fileSystemMode = FileSystemMode.Virtual)
    let attachmentRecordSource = "record Candidate { field amount: Int; }"
    defineFlowProject attachmentEngine attachmentRecordSource [ "syntaxVersion", jint 2 ]
    |> expectOk "stage record for generated-operation attachment guard"
    |> ignore
    commit attachmentEngine "Candidate" |> expectOk "persist record attachment-guard baseline" |> ignore

    let attachmentStore = Storage.create attachmentProject
    let attachmentBaseline =
        Storage.load attachmentStore
        |> Result.defaultWith (fun problem -> failwith $"load attachment-guard baseline: {problem.Code}: {problem.Message}")
    let attachmentBaselineSource = typeSource attachmentStore attachmentBaseline "Candidate"
    let attachmentBaselineWords =
        dispatch attachmentEngine "words" []
        |> expectOk "capture attachment-guard generated words"
        |> fun response -> response.["data"].ToJsonString()
    let attachmentBaselineTests = testSourceRows attachmentEngine "candidate.amount"

    let evolvedAttachmentSource = "record Candidate { field amount: Int; field label: String; }"
    defineFlowProject attachmentEngine evolvedAttachmentSource
        [ "syntaxVersion", jint 2
          "replace", jbool true
          "expectedTypeSources", expectedTypeSources [ "Candidate", typeSourceHash attachmentEngine "Candidate" ] ]
    |> expectOk "stage record evolution that adds a generated accessor"
    |> ignore

    let lateGeneratedTest =
        "test candidate.label/read\n"
        + "    7 \"ready\" candidate.new candidate.label\n"
        + "    => \"ready\"\n"
        + "end"
    let rejectedTestEdit = defineStack attachmentEngine lateGeneratedTest
    expectError "FLOW_TYPE_EVOLUTION_GENERATED_ATTACHMENT_STAGED" rejectedTestEdit |> ignore
    check
        ((stringValue rejectedTestEdit.["error"].["message"]).Contains("staged record-evolution group", StringComparison.Ordinal))
        "generated-operation test rejection includes a structured explanation"

    let lateGeneratedExample =
        "example candidate.label/read\n"
        + "    7 \"ready\" candidate.new candidate.label\n"
        + "    => \"ready\"\n"
        + "end"
    let rejectedExampleEdit = defineStack attachmentEngine lateGeneratedExample
    expectError "FLOW_TYPE_EVOLUTION_GENERATED_ATTACHMENT_STAGED" rejectedExampleEdit |> ignore
    check
        ((stringValue rejectedExampleEdit.["error"].["message"]).Contains("staged record-evolution group", StringComparison.Ordinal))
        "generated-operation example rejection includes a structured explanation"

    dispatch attachmentEngine "discard" [ "word", jstr "Candidate" ]
    |> expectOk "discard staged Candidate record evolution"
    |> ignore
    let attachmentAfterDiscard =
        Storage.load attachmentStore
        |> Result.defaultWith (fun problem -> failwith $"load Candidate after evolution discard: {problem.Code}: {problem.Message}")
    equal attachmentBaseline.ManifestHash attachmentAfterDiscard.ManifestHash "discard restores the exact durable manifest baseline"
    equal attachmentBaselineSource (typeSource attachmentStore attachmentAfterDiscard "Candidate") "discard restores the exact durable record source bytes"
    equal attachmentBaselineWords
        (dispatch attachmentEngine "words" [] |> expectOk "inspect generated operations after group discard" |> fun response -> response.["data"].ToJsonString())
        "discard removes the staged generated accessor with no orphaned word"
    equal attachmentBaselineTests (testSourceRows attachmentEngine "candidate.amount") "discard preserves the baseline generated operation case rows"
    expectError "NAME_UNKNOWN_WORD" (dispatch attachmentEngine "source" [ "word", jstr "candidate.label" ]) |> ignore

    // An authored Flow function can have a case that alone references generated
    // record operations. A same-typed field reorder must compile and include that
    // function's test in the type-only publication result.
    let closureProject = Path.Combine(persistedRoot, "metadata-only-consumer-closure")
    let closureEngine = Runtime.Engine(closureProject, Set.empty, "2030-01-02T03:04:05Z", fileSystemMode = FileSystemMode.Virtual)
    let closureBaseline =
        "record Candidate { field first: Int; field second: Int; }\n\n"
        + "fn metrics.identity(value: Int) -> Int {\n"
        + "    effects none\n"
        + "    value\n"
        + "}\n\n"
        + "test metrics.identity/record {\n"
        + "    metrics.identity(candidate.new(first = 7, second = 11).first)\n"
        + "    => 7\n"
        + "}"
    defineFlowProject closureEngine closureBaseline [ "syntaxVersion", jint 2 ]
    |> expectOk "stage an authored function whose test alone references Candidate"
    |> ignore
    commit closureEngine "" |> expectOk "commit Candidate and its authored consumer" |> ignore
    assertAllPassed 1 (dispatch closureEngine "test" [ "word", jstr "metrics.identity" ] |> expectOk "run metadata-only consumer test before evolution") "baseline consumer"

    let closureTypeHash = typeSourceHash closureEngine "Candidate"
    let reorderedSource = "record Candidate { field second: Int; field first: Int; }"
    defineFlowProject closureEngine reorderedSource
        [ "syntaxVersion", jint 2
          "replace", jbool true
          "expectedTypeSources", expectedTypeSources [ "Candidate", closureTypeHash ] ]
    |> expectOk "stage same-typed field reorder"
    |> ignore

    let typeOnlyCommit = commit closureEngine "Candidate" |> expectOk "commit same-typed record reorder"
    let executedCases = typeOnlyCommit.["data"].AsArray() |> Seq.map stringValue |> Seq.toList
    check (List.contains "metrics.identity/record" executedCases) "type-only commit executes the metadata-only consumer's attached test"
    assertAllPassed 1 (dispatch closureEngine "test" [ "word", jstr "metrics.identity" ] |> expectOk "run metadata-only consumer after type evolution") "evolved consumer"
    let closureReloaded = Runtime.Engine(closureProject, Set.empty, "2030-01-02T03:04:05Z", fileSystemMode = FileSystemMode.Virtual)
    equal reorderedSource
        (dispatch closureReloaded "source" [ "type", jstr "Candidate" ] |> expectOk "read reordered Candidate after reload" |> fun response -> stringValue response.["data"])
        "fresh reload retains the same-typed field reorder"
    assertAllPassed 1 (dispatch closureReloaded "test" [ "word", jstr "metrics.identity" ] |> expectOk "run metadata-only consumer after reload") "reloaded consumer"

    // A library caller whose own source and tests do not change must still be
    // selected by type evolution. Its cases pass after this migration, but the
    // changed gate makes its false branch unreachable, so actual coverage must
    // reject publication and preserve the previous durable authority.
    let libraryProject = Path.Combine(persistedRoot, "library-caller-coverage-closure")
    let libraryEngine = Runtime.Engine(libraryProject, Set.empty, "2030-01-02T03:04:05Z", fileSystemMode = FileSystemMode.Virtual)
    let libraryBaseline =
        "record Signal { field first: Int; field second: Int; }\n\n"
        + "fn library.gate(value: Signal) -> Bool {\n"
        + "    effects none\n"
        + "    value.first == 0\n"
        + "}\n\n"
        + "fn library.caller(value: Signal) -> Int {\n"
        + "    effects none\n"
        + "    if library.gate(value) { 1 } else { 1 }\n"
        + "}\n\n"
        + "test library.gate/zero { library.gate(signal.new(first = 0, second = 0)) => true }\n\n"
        + "test library.gate/nonzero { library.gate(signal.new(first = 1, second = 1)) => false }\n\n"
        + "test library.caller/true { library.caller(signal.new(first = 0, second = 0)) => 1 }\n\n"
        + "test library.caller/false { library.caller(signal.new(first = 1, second = 0)) => 1 }"
    defineFlowProject libraryEngine libraryBaseline [ "syntaxVersion", jint 2 ]
    |> expectOk "stage the library gate and its persistent caller with branch evidence"
    |> ignore
    dispatch libraryEngine "commit" [ "library", jbool true ]
    |> expectOk "persist Signal, gate, and caller as covered library owners"
    |> ignore

    let libraryStore = Storage.create libraryProject
    let libraryDurableBefore =
        Storage.load libraryStore
        |> Result.defaultWith (fun problem -> failwith $"load library caller baseline: {problem.Code}: {problem.Message}")
    let libraryManifestBefore = libraryDurableBefore.ManifestHash
    let libraryTypeSourceBefore = typeSource libraryStore libraryDurableBefore "Signal"
    let libraryGateSourceBefore = wordSource libraryStore libraryDurableBefore "library.gate"
    let libraryCallerSourceBefore = wordSource libraryStore libraryDurableBefore "library.caller"
    let libraryGateCasesBefore = testSourceRows libraryEngine "library.gate"
    let libraryCallerCasesBefore = testSourceRows libraryEngine "library.caller"

    let evolvedLibrarySource =
        "record Signal { field second: Int; field first: Int; }\n\n"
        + "fn library.gate(value: Signal) -> Bool {\n"
        + "    effects none\n"
        + "    value.second == 0\n"
        + "}"
    defineFlowProject libraryEngine evolvedLibrarySource
        [ "syntaxVersion", jint 2
          "replace", jbool true
          "expectedTypeSources", expectedTypeSources [ "Signal", typeSourceHash libraryEngine "Signal" ]
          "expectedRevisions", expectedRevisions [ "library.gate", wordRevision libraryEngine "library.gate" ] ]
    |> expectOk "stage a same-typed Signal reorder and gate-only migration"
    |> ignore
    assertAllPassed 2 (dispatch libraryEngine "test" [ "word", jstr "library.gate" ] |> expectOk "run retained gate coverage cases") "evolved library gate"
    assertAllPassed 2 (dispatch libraryEngine "test" [ "word", jstr "library.caller" ] |> expectOk "run unchanged caller cases after gate migration") "evolved library caller"

    let rejectedLibraryCoverage =
        dispatch libraryEngine "commit" [ "word", jstr "Signal" ]
        |> expectError "LIBRARY_COVERAGE_INCOMPLETE"
    equal "library.caller" (stringValue rejectedLibraryCoverage.["error"].["word"]) "type-only commit reports coverage loss on the unchanged library caller"
    let callerCoverageAfterCommitAttempt =
        dispatch libraryEngine "describe" [ "word", jstr "library.caller" ]
        |> expectOk "inspect the caller results recorded by the rejected group commit"
        |> fun response -> response.["data"].["coverage"]
    equal 1 (callerCoverageAfterCommitAttempt.["branchesCovered"].GetValue<int>()) "rejected group commit executed caller cases that cover its reachable branch"
    equal 2 (callerCoverageAfterCommitAttempt.["branchesTotal"].GetValue<int>()) "rejected group commit retained both caller branch obligations"
    check
        (callerCoverageAfterCommitAttempt.["uncoveredBranchOutcomes"].AsArray()
         |> Seq.map stringValue
         |> Seq.exists (fun gap -> gap.Contains(":false", StringComparison.Ordinal)))
        "recorded caller execution leaves its false branch uncovered"
    check
        ((rejectedLibraryCoverage.["error"].["actual"].AsArray() |> Seq.map stringValue |> Seq.toList) |> List.exists (fun gap -> gap.Contains("false", StringComparison.OrdinalIgnoreCase)))
        "caller coverage diagnostic identifies its now-unreachable false branch"
    let libraryAfterRejectedCommit =
        Storage.load libraryStore
        |> Result.defaultWith (fun problem -> failwith $"reload library caller after rejected commit: {problem.Code}: {problem.Message}")
    equal libraryManifestBefore libraryAfterRejectedCommit.ManifestHash "failed actual-coverage gate leaves the old library manifest authoritative"
    equal libraryTypeSourceBefore (typeSource libraryStore libraryAfterRejectedCommit "Signal") "failed actual-coverage gate preserves the durable record source"
    dispatch libraryEngine "discard" [ "word", jstr "Signal" ]
    |> expectOk "discard the staged record and gate group after coverage rejection"
    |> ignore
    let libraryAfterDiscard = Storage.load libraryStore |> Result.defaultWith (fun problem -> failwith problem.Message)
    equal libraryManifestBefore libraryAfterDiscard.ManifestHash "discard restores the exact library-group manifest baseline"
    equal libraryTypeSourceBefore (typeSource libraryStore libraryAfterDiscard "Signal") "discard restores the exact library record source"
    equal libraryGateSourceBefore (wordSource libraryStore libraryAfterDiscard "library.gate") "discard restores the exact original gate source"
    equal libraryCallerSourceBefore (wordSource libraryStore libraryAfterDiscard "library.caller") "discard preserves the unchanged caller source bytes"
    equal libraryGateCasesBefore (testSourceRows libraryEngine "library.gate") "discard restores exact retained gate case sources"
    equal libraryCallerCasesBefore (testSourceRows libraryEngine "library.caller") "discard preserves exact unchanged caller case sources"
    assertAllPassed 2 (dispatch libraryEngine "test" [ "word", jstr "library.caller" ] |> expectOk "run the caller after discarding failed evolution") "discarded library caller"

    // A retained test-file override is part of a Flow owner contract. A type
    // migration that changes that owner's output shape must reject while the
    // wrapper remains untouched; callers cannot smuggle a wrapper edit through
    // the type-CAS route either.
    let wrapperProject = Path.Combine(persistedRoot, "retained-wrapper-type-incompatibility")
    let wrapperEngine = Runtime.Engine(wrapperProject, Set.empty, "2030-01-02T03:04:05Z", fileSystemMode = FileSystemMode.Virtual)
    let wrapperFunctionSource =
        "fn badge.read(value: Badge) -> Int {\n"
        + "    effects none\n"
        + "    value.value\n"
        + "}"
    let wrapperBaselineSource =
        "record Badge { field value: Int; }\n\n"
        + wrapperFunctionSource
        + "\n\n"
        + "test-file retained-badge {\n"
        + "    override fn badge.read(value: Badge) -> Int {\n"
        + "        effects none\n"
        + "        value.value\n"
        + "    }\n"
        + "    test badge.read/retained {\n"
        + "        badge.read(badge.new(value = 7))\n"
        + "        => 7\n"
        + "    }\n"
        + "}"
    defineFlowProject wrapperEngine wrapperBaselineSource [ "syntaxVersion", jint 2 ]
    |> expectOk "stage Badge and its retained test-file override"
    |> ignore
    commit wrapperEngine "badge.read" |> expectOk "persist Badge with its test-file wrapper" |> ignore

    let wrapperStore = Storage.create wrapperProject
    let wrapperDurableBefore =
        Storage.load wrapperStore
        |> Result.defaultWith (fun problem -> failwith $"load retained-wrapper baseline: {problem.Code}: {problem.Message}")
    let wrapperManifestBefore = wrapperDurableBefore.ManifestHash
    let wrapperTypeSourceBefore = typeSource wrapperStore wrapperDurableBefore "Badge"
    let wrapperWordSourceBefore = wordSource wrapperStore wrapperDurableBefore "badge.read"
    let wrapperCasesBefore = testSourceRows wrapperEngine "badge.read"
    let wrapperExecutionBefore = testOutcomeRows wrapperEngine "badge.read"
    let adaptedWrapperSource =
        "record Badge { field value: String; }\n\n"
        + "fn badge.read(value: Badge) -> String {\n"
        + "    effects none\n"
        + "    value.value\n"
        + "}\n\n"
        + "test-file retained-badge {\n"
        + "    override fn badge.read(value: Badge) -> String {\n"
        + "        effects none\n"
        + "        value.value\n"
        + "    }\n"
        + "    test badge.read/retained {\n"
        + "        badge.read(badge.new(value = \"ready\"))\n"
        + "        => \"ready\"\n"
        + "    }\n"
        + "}"
    let rejectedWrapperEdit =
        defineFlowProject wrapperEngine adaptedWrapperSource
            [ "syntaxVersion", jint 2
              "replace", jbool true
              "expectedTypeSources", expectedTypeSources [ "Badge", typeSourceHash wrapperEngine "Badge" ]
              "expectedRevisions", expectedRevisions [ "badge.read", wordRevision wrapperEngine "badge.read" ] ]
    expectError "FLOW_PROJECT_REPLACEMENT_WRAPPERS_UNSUPPORTED" rejectedWrapperEdit |> ignore
    equal wrapperManifestBefore (Storage.load wrapperStore |> Result.defaultWith (fun problem -> failwith problem.Message) |> fun loaded -> loaded.ManifestHash) "type-CAS wrapper-edit rejection leaves the exact durable manifest unchanged"
    equal wrapperCasesBefore (testSourceRows wrapperEngine "badge.read") "type-CAS wrapper-edit rejection keeps exact original wrapper and case bytes"

    let incompatibleRetainedWrapper =
        "record Badge { field value: String; }\n\n"
        + "fn badge.read(value: Badge) -> String {\n"
        + "    effects none\n"
        + "    value.value\n"
        + "}"
    let rejectedRetainedWrapper =
        defineFlowProject wrapperEngine incompatibleRetainedWrapper
            [ "syntaxVersion", jint 2
              "replace", jbool true
              "expectedTypeSources", expectedTypeSources [ "Badge", typeSourceHash wrapperEngine "Badge" ]
              "expectedRevisions", expectedRevisions [ "badge.read", wordRevision wrapperEngine "badge.read" ] ]
        |> expectError "FLOW_TEST_OVERRIDE_SIGNATURE_MISMATCH"
    equal wrapperManifestBefore (Storage.load wrapperStore |> Result.defaultWith (fun problem -> failwith problem.Message) |> fun loaded -> loaded.ManifestHash) "incompatible retained-wrapper rejection preserves the exact old manifest"
    equal wrapperTypeSourceBefore (typeSource wrapperStore wrapperDurableBefore "Badge") "incompatible retained-wrapper rejection preserves the durable type source"
    equal wrapperWordSourceBefore (wordSource wrapperStore wrapperDurableBefore "badge.read") "incompatible retained-wrapper rejection preserves the durable owner source"
    equal wrapperCasesBefore (testSourceRows wrapperEngine "badge.read") "incompatible retained-wrapper rejection preserves exact wrapper and test case sources"
    equal wrapperExecutionBefore (testOutcomeRows wrapperEngine "badge.read") "incompatible retained-wrapper rejection leaves baseline case outcomes unchanged"
    let wrapperReloaded = Runtime.Engine(wrapperProject, Set.empty, "2030-01-02T03:04:05Z", fileSystemMode = FileSystemMode.Virtual)
    equal wrapperCasesBefore (testSourceRows wrapperReloaded "badge.read") "fresh reload preserves exact wrapper and case sources after rejection"
    equal wrapperExecutionBefore (testOutcomeRows wrapperReloaded "badge.read") "fresh reload still executes the retained wrapper case unchanged"
    check (not (succeeded rejectedRetainedWrapper)) "retained incompatible wrapper never activates a partial type or word candidate"

    assertions
