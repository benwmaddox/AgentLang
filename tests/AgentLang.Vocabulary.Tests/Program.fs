namespace AgentLang.VocabularyTests

open System
open System.Numerics
open AgentLang

module Program =
    let mutable private assertions = 0

    let private check condition message =
        assertions <- assertions + 1
        if not condition then failwith message

    let private equal expected actual message =
        assertions <- assertions + 1
        if expected <> actual then failwith $"{message}: expected {expected}, got {actual}"

    let private span file line =
        { File = file
          Line = line
          Column = 1
          Length = 1 }

    let private word name inputs outputs effects body documentation maturity revision sourceSpan builtin : WordEntry =
        let definition =
            { Name = name
              Inputs = inputs
              Outputs = outputs
              Effects = effects
              Maturity = maturity
              Revision = revision
              Documentation = documentation
              Body = body
              SourceText = ""
              Span = sourceSpan }
        { Definition = definition
          Builtin = builtin
          Status = if builtin.IsSome then Primitive else Persistent
          Maturity = maturity
          Revision = revision }

    let private authored name inputs outputs effects body documentation =
        word name inputs outputs effects body documentation ProjectWord 1 (span name 1) None

    let private builtin name inputs outputs operation =
        word name inputs outputs Set.empty [] "" LibraryWord 1 (span "builtin" 1) (Some(BuiltinOp operation))

    let private call name = Call(name, span "body" 1)
    let private namedCall callback = MapList(callback, span "body" 2)
    let private foldCall callback = FoldList(callback, span "body" 3)

    let private record name fields : RecordEntry =
        { Definition =
            { Name = name
              Fields = fields
              SourceText = ""
              Span = span "types" 1 }
          Status = Persistent }

    let private scalar name baseType validator : ScalarEntry =
        { Definition =
            { Name = name
              BaseType = baseType
              Validator = validator
              SourceText = ""
              Span = span "types" 1 }
          Status = Persistent }

    let private idsFor (entries: WordEntry list) =
        entries
        |> List.choose (fun entry ->
            if entry.Builtin.IsNone then Some(entry.Definition.Name, "stable-" + entry.Definition.Name)
            else None)
        |> Map.ofList

    let private makeIndex (entries: WordEntry list) (records: RecordEntry list) (scalars: ScalarEntry list) wordIds =
        let words = entries |> List.map (fun entry -> entry.Definition.Name, entry) |> Map.ofList
        let recordMap = records |> List.map (fun entry -> entry.Definition.Name, entry) |> Map.ofList
        let scalarMap = scalars |> List.map (fun entry -> entry.Definition.Name, entry) |> Map.ofList
        VocabularyAnalysis.build words recordMap scalarMap Map.empty wordIds

    let private expectDiagnostic expectedCode action =
        try
            action () |> ignore
            failwith $"expected diagnostic {expectedCode}"
        with
        | LanguageException diagnostic when diagnostic.Code = expectedCode -> assertions <- assertions + 1
        | _ -> reraise ()

    let private testExactStructuralDuplicates () =
        let primitiveAdd = builtin "primitive.add" [ TInt; TInt ] [ TInt ] "host.add"
        let primitiveSubtract = builtin "primitive.subtract" [ TInt; TInt ] [ TInt ] "host.subtract"
        let primitiveDrop = builtin "primitive.drop" [ TInt ] [] "host.drop"
        let primitiveBoolNot = builtin "primitive.bool-not" [ TBool ] [ TBool ] "host.bool-not"
        let alpha = authored "customer.total-a" [ TInt ] [ TInt ] Set.empty [ call "primitive.add" ] "alpha documentation"
        let beta =
            word "billing.total-b" [ TInt ] [ TInt ] Set.empty [ Call("primitive.add", span "other-source" 48) ] "different docs" LibraryWord 99 (span "other-source" 48) None
        let localA = authored "local.a" [] [ TInt ] Set.empty [ Push(LInt 1L, span "a" 1); Let("result", span "a" 2); Load("result", span "a" 3) ] ""
        let localB = authored "local.b" [] [ TInt ] Set.empty [ Push(LInt 1L, span "b" 7); Let("value", span "b" 8); Load("value", span "b" 9) ] ""
        let literalA = authored "literal.a" [] [ TInt ] Set.empty [ Push(LInt 1L, span "a" 1) ] ""
        let literalB = authored "literal.b" [] [ TInt ] Set.empty [ Push(LInt 2L, span "b" 1) ] ""
        let nominal = authored "type.email" [ TNamed "Email" ] [ TUnit ] Set.empty [] ""
        let primitiveString = authored "type.string" [ TString ] [ TUnit ] Set.empty [] ""
        let effectA = authored "effect.a" [] [ TUnit ] Set.empty [] ""
        let effectB = authored "effect.b" [] [ TUnit ] (Set.singleton "db.read") [] ""
        let branchA = authored "branch.a" [] [ TBool ] Set.empty [ If([ Push(LBool true, span "a" 1) ], [ Push(LBool false, span "a" 2) ], span "a" 1) ] ""
        let branchB = authored "branch.b" [] [ TBool ] Set.empty [ If([ Push(LBool false, span "b" 1) ], [ Push(LBool true, span "b" 2) ], span "b" 1) ] ""
        let callbackOne = authored "callback.one" [ TInt ] [ TBool ] Set.empty [ Push(LBool true, span "c1" 1) ] ""
        let callbackTwo = authored "callback.two" [ TInt ] [ TBool ] Set.empty [ Push(LBool false, span "c2" 1) ] ""
        let callbackA = authored "callback.owner-a" [ TList TInt ] [ TList TBool ] Set.empty [ namedCall "callback.one" ] ""
        let callbackB = authored "callback.owner-b" [ TList TInt ] [ TList TBool ] Set.empty [ namedCall "callback.two" ] ""
        let foldStepA = authored "fold.step-a" [ TBool; TInt ] [ TBool ] Set.empty [ call "primitive.drop" ] ""
        let foldStepB = authored "fold.step-b" [ TBool; TInt ] [ TBool ] Set.empty [ call "primitive.drop"; call "primitive.bool-not" ] ""
        let foldOwnerA = authored "fold.owner-a" [ TList TInt; TBool ] [ TBool ] Set.empty [ foldCall "fold.step-a" ] ""
        let foldOwnerB = authored "fold.owner-b" [ TList TInt; TBool ] [ TBool ] Set.empty [ foldCall "fold.step-b" ] ""
        let entries =
            [ primitiveAdd; primitiveSubtract; primitiveDrop; primitiveBoolNot; alpha; beta; localA; localB; literalA; literalB
              nominal; primitiveString; effectA; effectB; branchA; branchB
              callbackOne; callbackTwo; callbackA; callbackB; foldStepA; foldStepB; foldOwnerA; foldOwnerB ]
        let email = scalar "Email" TString None
        let wordIds = idsFor entries
        let index = makeIndex entries [] [ email ] wordIds

        let hashAlpha = VocabularyAnalysis.fingerprint index "customer.total-a"
        let hashBeta = VocabularyAnalysis.fingerprint index "billing.total-b"
        equal hashAlpha hashBeta "word names, docs, source spans, revisions, and maturity do not affect the fingerprint"
        check (hashAlpha.Length = 64 && hashAlpha |> Seq.forall (fun c -> Char.IsAsciiHexDigit c && not (Char.IsUpper c))) "fingerprints are lowercase SHA-256"

        for leftName, rightName, reason in
            [ "local.a", "local.b", "local names are strict structural data"
              "literal.a", "literal.b", "literal values remain distinct"
              "type.email", "type.string", "nominal and primitive types remain distinct"
              "effect.a", "effect.b", "declared effects remain distinct"
              "branch.a", "branch.b", "branch structure remains distinct"
              "callback.owner-a", "callback.owner-b", "callback target identity remains distinct"
              "fold.owner-a", "fold.owner-b", "fold callback target identity remains distinct" ] do
            check
                (VocabularyAnalysis.fingerprint index leftName <> VocabularyAnalysis.fingerprint index rightName)
                reason

        equal
            [ [ "billing.total-b"; "customer.total-a" ] ]
            (VocabularyAnalysis.duplicateCandidates index |> List.map (fun candidate -> [ candidate.FirstWord; candidate.SecondWord ]))
            "duplicate candidates contain only the exact pair in stable name order"
        equal (idsFor entries) wordIds "input identity map remains unchanged after indexing"

    let private testStableCallIdentityAcrossRename () =
        let primitive = builtin "primitive.identity" [ TInt ] [ TInt ] "host.identity"
        let oldTarget = authored "target.old" [] [ TInt ] Set.empty [ Push(LInt 7L, span "old" 1) ] ""
        let oldCaller = authored "caller.old" [] [ TInt ] Set.empty [ call "target.old" ] "old caller docs"
        let oldEntries = [ primitive; oldTarget; oldCaller ]
        let oldIds = Map.ofList [ "target.old", "stable-target-1"; "caller.old", "stable-caller-old" ]
        let oldIndex = makeIndex oldEntries [] [] oldIds

        let renamedTarget = authored "target.renamed" [] [ TInt ] Set.empty [ Push(LInt 7L, span "new" 27) ] "renamed target"
        let renamedCaller = authored "caller.renamed" [] [ TInt ] Set.empty [ call "target.renamed" ] "renamed caller docs"
        let newEntries = [ primitive; renamedTarget; renamedCaller ]
        let newIds = Map.ofList [ "target.renamed", "stable-target-1"; "caller.renamed", "stable-caller-new" ]
        let newIndex = makeIndex newEntries [] [] newIds
        equal
            (VocabularyAnalysis.fingerprint oldIndex "caller.old")
            (VocabularyAnalysis.fingerprint newIndex "caller.renamed")
            "stable target ID preserves call identity through target and owner rename"

        let oldFoldTarget = authored "fold-target.old" [ TBool; TInt ] [ TBool ] Set.empty [] ""
        let oldFoldCaller = authored "fold-caller.old" [ TList TInt; TBool ] [ TBool ] Set.empty [ foldCall "fold-target.old" ] ""
        let oldFoldIndex =
            makeIndex [ oldFoldTarget; oldFoldCaller ] [] []
                (Map.ofList [ "fold-target.old", "stable-fold-target"; "fold-caller.old", "stable-fold-caller-old" ])
        let newFoldTarget = authored "fold-target.renamed" [ TBool; TInt ] [ TBool ] Set.empty [] "renamed target"
        let newFoldCaller = authored "fold-caller.renamed" [ TList TInt; TBool ] [ TBool ] Set.empty [ foldCall "fold-target.renamed" ] "renamed caller"
        let newFoldIndex =
            makeIndex [ newFoldTarget; newFoldCaller ] [] []
                (Map.ofList [ "fold-target.renamed", "stable-fold-target"; "fold-caller.renamed", "stable-fold-caller-new" ])
        equal
            (VocabularyAnalysis.fingerprint oldFoldIndex "fold-caller.old")
            (VocabularyAnalysis.fingerprint newFoldIndex "fold-caller.renamed")
            "stable callback ID preserves fold semantics across target and owner rename"

    let private testStaticDistanceAndCallbacks () =
        let primitive = builtin "primitive.step" [ TInt ] [ TInt ] "host.step"
        let stringPredicate = builtin "string.valid?" [ TString ] [ TBool ] "host.string-valid"
        let boolNot = builtin "bool.not" [ TBool ] [ TBool ] "host.bool-not"
        let generated = word "Customer.email" [ TNamed "Customer" ] [ TNamed "Email" ] Set.empty [] "" LibraryWord 1 (span "gen" 1) (Some(RecordAccessor("Customer", "email")))
        let emailValid = authored "email.validate" [ TString ] [ TBool ] Set.empty [ call "string.valid?" ] ""
        let emailConstructor = word "Email.make" [ TString ] [ TNamed "Email" ] Set.empty [] "" LibraryWord 1 (span "gen" 2) (Some(ScalarConstructor "Email"))
        let boolConstructor = word "ValidatedBool.make" [ TBool ] [ TNamed "ValidatedBool" ] Set.empty [] "" LibraryWord 1 (span "gen" 3) (Some(ScalarConstructor "ValidatedBool"))
        let leaf = authored "leaf" [] [ TInt ] Set.empty [ call "primitive.step"; call "primitive.step" ] ""
        let left = authored "left" [] [ TInt ] Set.empty [ call "leaf" ] ""
        let right = authored "right" [] [ TInt ] Set.empty [ call "leaf" ] ""
        let callback = authored "callback" [ TInt ] [ TBool ] Set.empty [ call "primitive.step" ] ""
        let foldStep = authored "fold.step" [ TBool; TInt ] [ TBool ] Set.empty [ call "primitive.step" ] ""
        let foldRoot = authored "fold.root" [ TList TInt; TBool ] [ TBool ] Set.empty [ foldCall "fold.step" ] ""
        let body =
            [ call "left"
              call "right"
              call "Customer.email"
              call "Email.make"
              call "ValidatedBool.make"
              MapList("callback", span "body" 1)
              FilterList("callback", span "body" 2)
              If([ call "primitive.step" ], [ call "primitive.step" ], span "body" 3)
              MatchOption("item", [ call "primitive.step" ], [ call "primitive.step" ], span "body" 4)
              MatchResult("ok", "error", [ call "primitive.step" ], [ call "primitive.step" ], span "body" 5) ]
        let root = authored "root" [] [ TUnit ] Set.empty body ""
        let entries = [ primitive; stringPredicate; boolNot; generated; leaf; left; right; callback; foldStep; foldRoot; emailValid; emailConstructor; boolConstructor; root ]
        let records = [ record "Customer" [ { Name = "email"; Type = TNamed "Email" } ] ]
        let scalars = [ scalar "Email" TString (Some "email.validate"); scalar "ValidatedBool" TBool (Some "bool.not") ]
        let index = makeIndex entries records scalars (idsFor entries)
        let estimate = VocabularyAnalysis.staticCallEstimate index "root"
        equal 14I estimate.PrimitiveCallSites "distance expands all call occurrences, builtin validators, and both sides of every branch"
        equal 3I estimate.GeneratedCallSites "generated accessor and scalar constructors are classified separately from validator calls"
        equal 7I estimate.AuthoredInvocationSites "nested authored calls and authored validator invocations count but the root invocation is excluded"
        equal [ "callback" ] estimate.DynamicCallbackTargets "callback target names are distinct and sorted"
        equal (Map.ofList [ "callback", 2I ]) estimate.DynamicCallbackOccurrences "each callback site retains its occurrence count"
        check estimate.DynamicCallbackRepetitionsUnknown "callback runtime repetition remains explicitly unknown"

        let foldEstimate = VocabularyAnalysis.staticCallEstimate index "fold.root"
        equal [ "fold.step" ] foldEstimate.DynamicCallbackTargets "fold step is a discoverable static callback dependency"
        equal (Map.ofList [ "fold.step", 1I ]) foldEstimate.DynamicCallbackOccurrences "fold contributes one structural callback occurrence"

        let repeated = authored "repeated" [] [ TUnit ] Set.empty [ call "primitive.step"; call "primitive.step" ] ""
        let repeatedIndex = makeIndex [ primitive; repeated ] [] [] (idsFor [ repeated ])
        equal 2I (VocabularyAnalysis.staticCallEstimate repeatedIndex "repeated").PrimitiveCallSites "repeated calls to one primitive retain multiplicity"

    let private testMemoizedDAGAndUnboundedCounts () =
        let primitive = builtin "primitive.step" [] [ TUnit ] "host.step"
        let leaf = authored "leaf" [] [ TUnit ] Set.empty [ call "primitive.step" ] ""
        let left = authored "left" [] [ TUnit ] Set.empty [ call "leaf" ] ""
        let right = authored "right" [] [ TUnit ] Set.empty [ call "leaf" ] ""
        let root = authored "root" [] [ TUnit ] Set.empty [ call "left"; call "right" ] ""
        let entries = [ primitive; leaf; left; right; root ]
        let estimate = VocabularyAnalysis.staticCallEstimate (makeIndex entries [] [] (idsFor entries)) "root"
        equal 2I estimate.PrimitiveCallSites "memoized diamond expansion still counts both references"
        equal 4I estimate.AuthoredInvocationSites "each authored call site and nested call occurrence is counted"

        let callback = authored "huge.callback" [ TUnit ] [ TUnit ] Set.empty [ call "primitive.step" ] ""
        let levels =
            [ for level in 0 .. 70 do
                  let name = $"level.{level}"
                  let body =
                      if level = 0 then [ call "primitive.step"; call "primitive.step"; MapList("huge.callback", span "huge" 1) ]
                      else [ call $"level.{level - 1}"; call $"level.{level - 1}" ]
                  yield authored name [] [ TUnit ] Set.empty body "" ]
        let hugeEntries = primitive :: callback :: levels
        let hugeIndex = makeIndex hugeEntries [] [] (idsFor (callback :: levels))
        let huge = VocabularyAnalysis.staticCallEstimate hugeIndex "level.70"
        equal (BigInteger.Pow(2I, 71) + BigInteger.Pow(2I, 70)) huge.PrimitiveCallSites "memoized DAG counts exceed Int64 without overflow"
        equal (Map.ofList [ "huge.callback", BigInteger.Pow(2I, 70) ]) huge.DynamicCallbackOccurrences "callback multiplicity remains compressed in a map at huge counts"

    let private testGeneratedValidatorClassification () =
        let accessor = word "Customer.valid?" [ TNamed "Customer" ] [ TBool ] Set.empty [] "" LibraryWord 1 (span "generated" 1) (Some(RecordAccessor("Customer", "valid")))
        let constructor = word "EligibleCustomer.make" [ TNamed "Customer" ] [ TNamed "EligibleCustomer" ] Set.empty [] "" LibraryWord 1 (span "generated" 2) (Some(ScalarConstructor "EligibleCustomer"))
        let root = authored "eligible" [ TNamed "Customer" ] [ TNamed "EligibleCustomer" ] Set.empty [ call "EligibleCustomer.make" ] ""
        let entries = [ accessor; constructor; root ]
        let records = [ record "Customer" [ { Name = "valid"; Type = TBool } ] ]
        let scalars = [ scalar "EligibleCustomer" (TNamed "Customer") (Some "Customer.valid?") ]
        let estimate = makeIndex entries records scalars (idsFor entries) |> fun index -> VocabularyAnalysis.staticCallEstimate index "eligible"
        equal 0I estimate.PrimitiveCallSites "generated validator does not fabricate a primitive call"
        equal 2I estimate.GeneratedCallSites "scalar constructor and generated validator accessor are both counted"
        equal 0I estimate.AuthoredInvocationSites "generated validator dispatch is not counted as an authored call"

    let private testFailuresAndImmutability () =
        let missing = authored "missing.caller" [] [ TUnit ] Set.empty [ call "not.present" ] ""
        expectDiagnostic "VOCABULARY_UNKNOWN_WORD" (fun () -> makeIndex [ missing ] [] [] (idsFor [ missing ]))

        let missingId = authored "no.id" [] [ TUnit ] Set.empty [] ""
        expectDiagnostic "VOCABULARY_MISSING_WORD_ID" (fun () -> makeIndex [ missingId ] [] [] Map.empty)

        let duplicateIdEntries =
            [ authored "first" [] [ TUnit ] Set.empty [] ""
              authored "second" [] [ TUnit ] Set.empty [] "" ]
        let duplicateIds = Map.ofList [ "first", "same"; "second", "same" ]
        expectDiagnostic "VOCABULARY_DUPLICATE_WORD_ID" (fun () -> makeIndex duplicateIdEntries [] [] duplicateIds)

        let first = authored "recursive.a" [] [ TUnit ] Set.empty [ call "recursive.b" ] ""
        let second = authored "recursive.b" [] [ TUnit ] Set.empty [ call "recursive.a" ] ""
        let primitive = builtin "primitive.step" [] [ TUnit ] "host.step"
        let cycleEntries = [ first; second; primitive ]
        let cycleIndex = makeIndex cycleEntries [] [] (idsFor cycleEntries)
        expectDiagnostic "VOCABULARY_RECURSIVE_EXPANSION" (fun () -> VocabularyAnalysis.staticCallEstimate cycleIndex "recursive.a")
        expectDiagnostic "VOCABULARY_UNKNOWN_ROOT" (fun () -> VocabularyAnalysis.staticCallEstimate cycleIndex "absent")
        expectDiagnostic "VOCABULARY_NOT_AUTHORED_WORD" (fun () -> VocabularyAnalysis.fingerprint cycleIndex "primitive.step")

        let recordConstructor = word "Missing.make" [ TUnit ] [ TUnit ] Set.empty [] "" LibraryWord 1 (span "generated" 1) (Some(RecordConstructor "Missing"))
        expectDiagnostic "VOCABULARY_UNKNOWN_RECORD" (fun () -> makeIndex [ recordConstructor ] [] [] Map.empty)

    [<EntryPoint>]
    let main _ =
        testExactStructuralDuplicates ()
        testStableCallIdentityAcrossRename ()
        testStaticDistanceAndCallbacks ()
        testMemoizedDAGAndUnboundedCounts ()
        testGeneratedValidatorClassification ()
        testFailuresAndImmutability ()
        printfn $"AgentLang.Vocabulary.Tests: {assertions} assertions passed."
        0
