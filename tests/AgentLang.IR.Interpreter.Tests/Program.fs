module AgentLang.IR.Interpreter.Tests

open System
open AgentLang

let mutable private assertions = 0

let private check name condition =
    assertions <- assertions + 1
    if not condition then failwith $"{name}: assertion failed"

let private expectDiagnostic name code (action: unit -> unit) =
    try
        action ()
        failwith $"{name}: expected diagnostic {code}"
    with
    | LanguageException diagnostic when diagnostic.Code = code ->
        assertions <- assertions + 1
    | LanguageException diagnostic ->
        failwith $"{name}: expected {code}, got {diagnostic.Code}: {diagnostic.Message}"

let private span file =
    { File = file
      Line = 1
      Column = 1
      Length = 1 }

let private wordEntry name inputs outputs effects body builtin : WordEntry =
    let definitionSpan = span (name + ".agent")
    let definition =
        { Name = name
          Inputs = inputs
          Outputs = outputs
          Effects = effects
          Maturity = LibraryWord
          Revision = 1
          Documentation = "IR interpreter conformance fixture."
          Body = body
          SourceText = name
          Span = definitionSpan }
    { Definition = definition
      Builtin = builtin
      Status = Persistent
      Maturity = LibraryWord
      Revision = 1 }

let private contextWith (records: Map<string, RecordDefinition>) (extraWords: WordEntry list) : Compiler.IrLoweringContext =
    let words =
        extraWords
        |> List.fold (fun found entry -> Map.add entry.Definition.Name entry found) Compiler.primitives
    let wordIds =
        words
        |> Map.toList
        |> List.map (fun (name, entry) ->
            let prefix =
                match entry.Builtin with
                | Some(BuiltinOp _) -> "primitive-"
                | Some _ -> "generated-"
                | None -> "user-"
            name, WordId(prefix + name))
        |> Map.ofList
    { Words = words
      Records = records
      Scalars = Map.empty
      Enums = Map.empty
      WordIds = wordIds }

let private contextWithEnums
    (records: Map<string, RecordDefinition>)
    (enums: Map<string, EnumDefinition>)
    (extraWords: WordEntry list) =
    { contextWith records extraWords with Enums = enums }

let private defaultContext () = contextWith Map.empty []

let private host preflight charge invoke recordUse =
    { PreflightEffects = preflight
      ChargeInstruction = charge
      RecordBranchOutcome = fun _ _ _ -> ()
      RecordUse = recordUse
      InvokeEffect = invoke
      WordDefinitionSpan = fun _ -> None
      PrimitiveDefinitionSpan = fun _ -> None }

let private noOpHost () =
    host (fun _ _ _ -> ()) (fun _ _ -> ()) (fun _ -> EffectUnit) ignore

let private compileBody context name initialStack expressions =
    let program = Compiler.compileIrProgram context
    program, Compiler.compileIrBodyAgainstProgram context program name initialStack expressions

let private testProgramTrustAndSnapshotBinding () =
    let context = defaultContext ()
    let program, body = compileBody context "conformance" [] [ Push(LInt 10L, span "conformance.agent"); Push(LInt 20L, span "conformance.agent"); Call("add", span "conformance.agent") ]
    check "detached body is bound to the exact verified program handle" (Object.ReferenceEquals(VerifiedIrBody.program body, program))
    check "compiled arithmetic executes through the fixed primitive backend" (IrInterpreter.executeBody (noOpHost ()) "conformance" body = [ IntValue 30L ])

    let modelOnlyProgram =
        { NominalTypesByKey = Map.empty
          FunctionsById = Map.empty
          GeneratedTargetsById = Map.empty
          SourceMap = Map.empty
          CoverageByWord = Map.empty }
        |> IrVerifier.verify Compiler.primitiveIrCatalog
    check "model-only verification does not mint backend authority" (not (VerifiedIrProgram.isBackendExecutable modelOnlyProgram))
    expectDiagnostic "model-only verification is rejected before execution" "IR_BACKEND_UNTRUSTED_PROGRAM" (fun () -> IrInterpreter.validateProgram modelOnlyProgram)

let private testPublicBoundaryAndEmptyEntryOnly () =
    let context = defaultContext ()
    let pointSpan = span "Point.agent"
    let point =
        { Name = "Point"
          Fields = [ { Name = "x"; Type = TInt }; { Name = "y"; Type = TInt } ]
          SourceText = "record Point"
          Span = pointSpan }
    let generated =
        [ wordEntry "point.new" [ TInt; TInt ] [ TNamed "Point" ] Set.empty [] (Some(RecordConstructor "Point"))
          wordEntry "point.x" [ TNamed "Point" ] [ TInt ] Set.empty [] (Some(RecordAccessor("Point", "x")))
          wordEntry "point.y" [ TNamed "Point" ] [ TInt ] Set.empty [] (Some(RecordAccessor("Point", "y"))) ]
    let context = contextWith (Map.ofList [ "Point", point ]) generated
    let sourceSpan = span "record-value.agent"
    let _, recordBody = compileBody context "record-value" [] [
        Push(LInt 4L, sourceSpan)
        Push(LInt 9L, sourceSpan)
        Call("point.new", sourceSpan)
    ]
    check "nominal execution returns only the public structural record value" (
        IrInterpreter.executeBody (noOpHost ()) "record-value" recordBody =
            [ RecordValue("Point", Map.ofList [ "x", IntValue 4L; "y", IntValue 9L ]) ])

    let _, nonemptyInputBody = compileBody (defaultContext ()) "requires-input" [ TInt ] []
    let mutable preflightCount = 0
    let mutable chargeCount = 0
    let host =
        host (fun _ _ _ -> preflightCount <- preflightCount + 1)
            (fun _ _ -> chargeCount <- chargeCount + 1)
            (fun _ -> EffectUnit)
            ignore
    expectDiagnostic "nonempty detached initial stacks are rejected at the public interpreter boundary" "IR_BACKEND_BODY_INPUT_UNSUPPORTED" (fun () ->
        IrInterpreter.executeBody host "requires-input" nonemptyInputBody |> ignore)
    check "unsupported public input is rejected before any host hook" (preflightCount = 0 && chargeCount = 0)

let private testOpaqueTypedInterpreterReentry () =
    let source = span "typed-reentry.agent"
    let metersValidator =
        wordEntry "meters.valid?" [ TInt ] [ TBool ] Set.empty
            [ Push(LInt 0L, source); Call("int.greater-or-equal", source) ] None
    let meters =
        { Name = "Meters"
          BaseType = TInt
          Validator = Some "meters.valid?"
          SourceText = "scalar Meters = Int"
          Span = source }
    let reading =
        { Name = "Reading"
          Fields = [ { Name = "current"; Type = TNamed "Meters" }; { Name = "count"; Type = TInt } ]
          SourceText = "record Reading"
          Span = source }
    let envelope =
        { Name = "Envelope"
          Fields = [ { Name = "reading"; Type = TNamed "Reading" }; { Name = "active"; Type = TBool } ]
          SourceText = "record Envelope"
          Span = source }
    let generated =
        [ wordEntry "meters.new" [ TInt ] [ TNamed "Meters" ] Set.empty [] (Some(ScalarConstructor "Meters"))
          wordEntry "reading.new" [ TNamed "Meters"; TInt ] [ TNamed "Reading" ] Set.empty [] (Some(RecordConstructor "Reading"))
          wordEntry "reading.current" [ TNamed "Reading" ] [ TNamed "Meters" ] Set.empty [] (Some(RecordAccessor("Reading", "current")))
          wordEntry "envelope.new" [ TNamed "Reading"; TBool ] [ TNamed "Envelope" ] Set.empty [] (Some(RecordConstructor "Envelope"))
          wordEntry "envelope.reading" [ TNamed "Envelope" ] [ TNamed "Reading" ] Set.empty [] (Some(RecordAccessor("Envelope", "reading")))
          wordEntry "envelope.active" [ TNamed "Envelope" ] [ TBool ] Set.empty [] (Some(RecordAccessor("Envelope", "active"))) ]
    let baseContext = contextWith (Map.ofList [ "Reading", reading; "Envelope", envelope ]) (metersValidator :: generated)
    let context = { baseContext with Scalars = Map.ofList [ "Meters", meters ] }
    let program = Compiler.compileIrProgram context
    let compile name inputs expressions = Compiler.compileIrBodyAgainstProgram context program name inputs expressions

    let initialization = compile "initialize-state" [] [
        Push(LInt 12L, source)
        Call("meters.new", source)
        Push(LInt 4L, source)
        Call("reading.new", source)
        Push(LBool true, source)
        Call("envelope.new", source)
    ]
    let updateCount = compile "update-count" [ TNamed "Envelope"; TInt ] [
        Let("delta", source)
        Let("state", source)
        Load("state", source)
        Call("envelope.reading", source)
        Call("reading.current", source)
        Load("delta", source)
        Call("reading.new", source)
        Load("state", source)
        Call("envelope.active", source)
        Call("envelope.new", source)
    ]
    let updateActive = compile "update-active" [ TNamed "Envelope"; TUnit; TBool ] [
        Let("active", source)
        Call("drop", source)
        Let("state", source)
        Load("state", source)
        Call("envelope.reading", source)
        Load("active", source)
        Call("envelope.new", source)
    ]
    let retainTwice = compile "retain-twice" [ TNamed "Envelope"; TNamed "Envelope" ] []
    let nominalInput = compile "nominal-input" [ TNamed "Meters" ] []

    let mutable validatorCalls = 0
    let trackedHost =
        host
            (fun _ _ _ -> ())
            (fun _ _ -> ())
            (fun _ -> EffectUnit)
            (fun name -> if name = "meters.valid?" then validatorCalls <- validatorCalls + 1)
    let initialize = IrInterpreter.executeBodyWithInputs trackedHost "initialize-state" initialization None []
    check "initialization uses the exact verified program handle" (Object.ReferenceEquals(VerifiedIrBody.program initialization, program))
    check "initialization validates the refined scalar once" (validatorCalls = 1)

    let mutable preflightCount = 0
    let mutable chargeCount = 0
    let guardedHost =
        host
            (fun _ _ _ -> preflightCount <- preflightCount + 1)
            (fun _ _ -> chargeCount <- chargeCount + 1)
            (fun _ -> EffectUnit)
            ignore
    expectDiagnostic "typed entry rejects an incorrect argument count" "IR_BACKEND_ENTRY_ARGUMENT_COUNT" (fun () ->
        IrInterpreter.executeBodyWithInputs guardedHost "update-count" updateCount (Some initialize) [ IrEntryArgument.RetainedRoot 0 ] |> ignore)
    expectDiagnostic "typed entry rejects an out-of-range retained root" "IR_BACKEND_ENTRY_ROOT_INDEX" (fun () ->
        IrInterpreter.executeBodyWithInputs guardedHost "update-count" updateCount (Some initialize) [ IrEntryArgument.RetainedRoot 1; IrEntryArgument.IntArgument 3L ] |> ignore)
    let wrongTypeDiagnostic =
        try
            IrInterpreter.executeBodyWithInputs guardedHost "update-count" updateCount (Some initialize)
                [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.BoolArgument true ] |> ignore
            failwith "typed entry should reject the mismatched primitive argument"
        with LanguageException diagnostic -> diagnostic
    check "typed entry reports the expected argument types in order" (wrongTypeDiagnostic.Expected = [ "Envelope"; "Int" ])
    check "typed entry reports the actual argument types in order" (wrongTypeDiagnostic.Actual = [ "Envelope"; "Bool" ])
    check "typed entry assigns the diagnostic to the attempted execution" (wrongTypeDiagnostic.Word = Some "update-count" && wrongTypeDiagnostic.Span.IsNone)
    expectDiagnostic "primitive Int cannot impersonate a nominal scalar input" "IR_BACKEND_ENTRY_ARGUMENT_TYPE" (fun () ->
        IrInterpreter.executeBodyWithInputs guardedHost "nominal-input" nominalInput None [ IrEntryArgument.IntArgument 12L ] |> ignore)
    let otherProgram = Compiler.compileIrProgram context
    let otherBody = Compiler.compileIrBodyAgainstProgram context otherProgram "other-program" [ TNamed "Envelope" ] []
    let mismatchDiagnostic =
        try
            IrInterpreter.executeBodyWithInputs guardedHost "other-program" otherBody (Some initialize) [ IrEntryArgument.RetainedRoot 0 ] |> ignore
            failwith "typed entry should reject a different program identity"
        with LanguageException diagnostic -> diagnostic
    check "typed entry rejects an equivalent but different verified-program instance" (mismatchDiagnostic.Code = "IR_BACKEND_ENTRY_PROGRAM_MISMATCH")
    check "program mismatch diagnostics are attributed to the attempted execution" (mismatchDiagnostic.Word = Some "other-program" && mismatchDiagnostic.Span.IsNone)
    check "invalid entry arguments are rejected before host hooks or fuel charges" (preflightCount = 0 && chargeCount = 0)

    let afterCount =
        IrInterpreter.executeBodyWithInputs
            trackedHost "update-count" updateCount (Some initialize)
            [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.IntArgument 3L ]
    initialize.Dispose()
    check "disposing an input owner does not invalidate roots retained by a successful next turn" (validatorCalls = 1)
    expectDiagnostic "typed entry rejects a disposed retained owner" "IR_BACKEND_ENTRY_OWNER_DISPOSED" (fun () ->
        IrInterpreter.executeBodyWithInputs guardedHost "update-count" updateCount (Some initialize)
            [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.IntArgument 1L ] |> ignore)

    let finalState =
        IrInterpreter.executeBodyWithInputs
            trackedHost "update-active" updateActive (Some afterCount)
            [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.UnitArgument; IrEntryArgument.BoolArgument false ]
    afterCount.Dispose()
    check "cross-turn retained roots do not rerun scalar refinement validators" (validatorCalls = 1)

    use borrowStarted = new System.Threading.ManualResetEventSlim(false)
    use finishBorrow = new System.Threading.ManualResetEventSlim(false)
    let blockingHost =
        host
            (fun _ _ _ ->
                borrowStarted.Set()
                finishBorrow.Wait())
            (fun _ _ -> ())
            (fun _ -> EffectUnit)
            ignore
    let pendingBorrow =
        System.Threading.Tasks.Task.Run(fun () ->
            IrInterpreter.executeBodyWithInputs
                blockingHost "retain-twice" retainTwice (Some finalState)
                [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.RetainedRoot 0 ])
    if not (borrowStarted.Wait(TimeSpan.FromSeconds 10.0)) then
        finishBorrow.Set()
        failwith "typed re-entry did not reach the host preflight while borrowing input roots"
    finalState.Dispose()
    finishBorrow.Set()
    let repeatedRoots = pendingBorrow.GetAwaiter().GetResult()
    check "disposing input during an in-flight turn preserves its borrowed immutable roots" (pendingBorrow.IsCompleted)
    let expected =
        RecordValue("Envelope", Map.ofList [
            "active", BoolValue false
            "reading", RecordValue("Reading", Map.ofList [
                "count", IntValue 3L
                "current", NamedValue("Meters", IntValue 12L)
            ])
        ])
    check "multiple typed turns preserve nested records and refined scalar values without intermediate decoding" (
        repeatedRoots.Decode() = [ expected; expected ])
    repeatedRoots.Dispose()
    try
        repeatedRoots.Decode() |> ignore
        failwith "decoding a disposed interpreter result should fail"
    with :? ObjectDisposedException -> assertions <- assertions + 1

    // Keep the old public method's historical diagnostic for nonempty bodies.
    expectDiagnostic "legacy executeBody keeps its empty-entry-only diagnostic" "IR_BACKEND_BODY_INPUT_UNSUPPORTED" (fun () ->
        IrInterpreter.executeBody (noOpHost ()) "update-count" updateCount |> ignore)

let private testScopeRestoresOverwrittenOuterLocal () =
    let context = defaultContext ()
    let source = span "scope-restore.agent"
    let marker = { source with Column = Int32.MaxValue; Length = 0 }
    let program = Compiler.compileIrProgram context
    let body =
        Compiler.compileIrBodyAgainstProgramWithSourceOrigins context program "scope-restores-local" [] [
            Push(LInt 10L, source)
            Let("outer", source)
            Scope(
                [ Push(LInt 99L, source)
                  Let("outer", source)
                  Load("outer", source) ],
                marker)
            Load("outer", source)
        ] (Map.ofList [ marker, source ])
    let verifiedBody = VerifiedIrBody.inspect body
    check "private Scope marker is mapped to the authored source location" (
        verifiedBody.BodySourceMap |> Map.exists (fun _ site -> site.SourceKind = "synthetic-scope" && site.SiteSpan = source))
    let mutable charges = 0
    let countingHost = host (fun _ _ _ -> ()) (fun _ _ -> charges <- charges + 1) (fun _ -> EffectUnit) ignore
    check "Scope preserves its result stack and restores an overwritten outer local" (
        IrInterpreter.executeBody countingHost "scope-restores-local" body = [ IntValue 99L; IntValue 10L ])
    check "all instructions, including Scope and its body, consume fuel" (charges = 7)

let private testInterpreterOwnsFuel () =
    let context = defaultContext ()
    let expressionSpan = span "fuel.agent"
    let flatExpressions = ResizeArray<Expr>()
    for _ in 1 .. 5001 do
        flatExpressions.Add(Push(LInt 1L, expressionSpan))
        flatExpressions.Add(Call("drop", expressionSpan))
    let _, flatBody = compileBody context "flat-fuel" [] (List.ofSeq flatExpressions)
    expectDiagnostic "10,002 flat instructions hit interpreter-local fuel with a no-op host" "RUNTIME_STEP_LIMIT" (fun () ->
        IrInterpreter.executeBody (noOpHost ()) "flat-fuel" flatBody |> ignore)

    let buildList count =
        let expressions = ResizeArray<Expr>()
        expressions.Add(Push(LInt 0L, expressionSpan))
        expressions.Add(ConstructContainer(ListSingleton, [ TInt ], expressionSpan))
        for value in 1L .. int64 (count - 1) do
            expressions.Add(Push(LInt value, expressionSpan))
            expressions.Add(ConstructContainer(ListSingleton, [ TInt ], expressionSpan))
            expressions.Add(Call("list.concat", expressionSpan))
        List.ofSeq expressions

    let sink = wordEntry "sink" [ TInt ] [ TUnit ] Set.empty [ Call("drop", expressionSpan); Push(LUnit, expressionSpan) ] None
    let outerBody = [ Call("drop", expressionSpan) ] @ buildList 20 @ [ EachList("sink", expressionSpan) ]
    let outer = wordEntry "outer" [ TInt ] [ TUnit ] Set.empty outerBody None
    let callbackContext = contextWith Map.empty [ sink; outer ]
    let expressions = buildList 100 @ [ EachList("outer", expressionSpan) ]
    let _, body = compileBody callbackContext "fuel" [] expressions
    let mutable hostCharges = 0
    let host = host (fun _ _ _ -> ()) (fun _ _ -> hostCharges <- hostCharges + 1) (fun _ -> EffectUnit) ignore
    expectDiagnostic "local interpreter fuel includes nested callback iterations" "RUNTIME_STEP_LIMIT" (fun () ->
        IrInterpreter.executeBody host "fuel" body |> ignore)
    check "host tracing observes the over-limit instruction before the structured limit diagnostic" (hostCharges = 10001)

    let chainWords =
        [ 0 .. 65 ]
        |> List.map (fun index ->
            let name = $"deep.{index}"
            let body = if index = 65 then [] else [ Call($"deep.{index + 1}", span (name + ".agent")) ]
            wordEntry name [] [] Set.empty body None)
    let deepContext = contextWith Map.empty chainWords
    let _, deepBody = compileBody deepContext "deep-call-chain" [] [ Call("deep.0", expressionSpan) ]
    expectDiagnostic "local call-depth limit is enforced with no-op host hooks" "RUNTIME_CALL_DEPTH" (fun () ->
        IrInterpreter.executeBody (noOpHost ()) "deep-call-chain" deepBody |> ignore)

let private testEmptyEffectfulCallbackPreflight () =
    let effectSpan = span "effect-callback.agent"
    let write =
        wordEntry "write" [ TInt ] [ TUnit ] (Set.singleton "console.write")
            [ Call("int.to-string", effectSpan); Call("console.write", effectSpan) ] None
    let context = contextWith Map.empty [ write ]
    let sourceSpan = span "empty-callback.agent"
    let _, body = compileBody context "empty-callback" [] [
        ConstructContainer(ListEmpty, [ TInt ], sourceSpan)
        EachList("write", sourceSpan)
    ]
    let mutable preflightCount = 0
    let mutable instructionCount = 0
    let mutable effectCount = 0
    let host =
        host (fun effects word site ->
                preflightCount <- preflightCount + 1
                if not (Set.isEmpty effects) then
                    Diagnostics.raiseError "CAPABILITY_DENIED" "Test host denies all effects." word None [] (IrEffects.names effects))
            (fun _ _ -> instructionCount <- instructionCount + 1)
            (fun _ -> effectCount <- effectCount + 1; EffectUnit)
            ignore
    expectDiagnostic "empty effectful callback is denied during enclosing-body preflight" "CAPABILITY_DENIED" (fun () ->
        IrInterpreter.executeBody host "empty-callback" body |> ignore)
    check "effect denial precedes body instructions and provider effects" (preflightCount = 1 && instructionCount = 0 && effectCount = 0)

let private testListFoldExecutionAndPreflight () =
    let source = span "fold-runtime.agent"
    let step =
        wordEntry "fold.decimal-step" [ TInt; TInt ] [ TInt ] Set.empty
            [ Call("swap", source)
              Push(LInt 10L, source)
              Call("multiply", source)
              Call("add", source) ] None
    let context = contextWith Map.empty [ step ]
    let buildList values =
        match values with
        | [] -> [ ConstructContainer(ListEmpty, [ TInt ], source) ]
        | first :: rest ->
            [ Push(LInt first, source); ConstructContainer(ListSingleton, [ TInt ], source) ]
            @ (rest |> List.collect (fun value ->
                [ Push(LInt value, source)
                  ConstructContainer(ListSingleton, [ TInt ], source)
                  Call("list.concat", source) ]))
    let _, ordered = compileBody context "fold-ordered" [] (buildList [ 1L; 2L; 3L ] @ [ Push(LInt 0L, source); FoldList("fold.decimal-step", source) ])
    check "noncommutative fold executes items from left to right with [accumulator; item]" (
        IrInterpreter.executeBody (noOpHost ()) "fold-ordered" ordered = [ IntValue 123L ])
    let _, empty = compileBody context "fold-empty" [] (buildList [] @ [ Push(LInt 7L, source); FoldList("fold.decimal-step", source) ])
    check "empty fold returns its seed unchanged" (IrInterpreter.executeBody (noOpHost ()) "fold-empty" empty = [ IntValue 7L ])
    let _, prefix =
        compileBody context "fold-prefix" []
            ([ Push(LInt 99L, source) ] @ buildList [ 1L; 2L; 3L ] @ [ Push(LInt 0L, source); FoldList("fold.decimal-step", source) ])
    check "fold replaces only list and seed while preserving an earlier stack prefix" (
        IrInterpreter.executeBody (noOpHost ()) "fold-prefix" prefix = [ IntValue 99L; IntValue 123L ])

    let mutable branches = []
    let branchHost =
        { noOpHost () with
            RecordBranchOutcome = fun word _ outcome -> branches <- (word, outcome) :: branches }
    IrInterpreter.executeBody branchHost "fold-empty" empty |> ignore
    IrInterpreter.executeBody branchHost "fold-ordered" ordered |> ignore
    check "empty and nonempty fold executions report their own branch outcomes"
        (Set.ofList branches = Set.ofList [ "fold-empty", "empty"; "fold-ordered", "nonempty" ])

    let mutable used = []
    let trackingHost = host (fun _ _ _ -> ()) (fun _ _ -> ()) (fun _ -> EffectUnit) (fun name -> used <- name :: used)
    IrInterpreter.executeBody trackingHost "fold-empty" empty |> ignore
    check "empty fold does not record an uninvoked callback as used" (not (used |> List.contains "fold.decimal-step"))
    used <- []
    IrInterpreter.executeBody trackingHost "fold-ordered" ordered |> ignore
    check "nonempty fold records the callback when it actually runs" (used |> List.contains "fold.decimal-step")

    let effectfulStep =
        wordEntry "fold.effect-step" [ TInt; TInt ] [ TInt ] (Set.singleton "console.write")
            [ Call("int.to-string", source); Call("console.write", source); Call("drop", source) ] None
    let effectContext = contextWith Map.empty [ effectfulStep ]
    let _, deniedEmpty =
        compileBody effectContext "fold-denied-empty" []
            [ ConstructContainer(ListEmpty, [ TInt ], source); Push(LInt 0L, source); FoldList("fold.effect-step", source) ]
    let mutable preflightCount = 0
    let mutable instructionCount = 0
    let mutable effectCount = 0
    let denyingHost =
        host (fun effects word site ->
                preflightCount <- preflightCount + 1
                if not (Set.isEmpty effects) then
                    Diagnostics.raiseError "CAPABILITY_DENIED" "Test host denies all effects." word None [] (IrEffects.names effects))
            (fun _ _ -> instructionCount <- instructionCount + 1)
            (fun _ -> effectCount <- effectCount + 1; EffectUnit)
            ignore
    expectDiagnostic "empty fold preflights callback effects before execution" "CAPABILITY_DENIED" (fun () ->
        IrInterpreter.executeBody denyingHost "fold-denied-empty" deniedEmpty |> ignore)
    check "empty fold denial occurs before instructions or provider calls" (preflightCount = 1 && instructionCount = 0 && effectCount = 0)

let private testListFoldFuelLimit () =
    let source = span "fold-fuel.agent"
    let step = wordEntry "fold.fuel-step" [ TInt; TInt ] [ TInt ] Set.empty [ Call("add", source) ] None
    let context = contextWith Map.empty [ step ]
    let expressions = ResizeArray<Expr>()
    expressions.Add(Push(LInt 0L, source))
    expressions.Add(ConstructContainer(ListSingleton, [ TInt ], source))
    for value in 1L .. 2_500L do
        expressions.Add(Push(LInt value, source))
        expressions.Add(ConstructContainer(ListSingleton, [ TInt ], source))
        expressions.Add(Call("list.concat", source))
    expressions.Add(Push(LInt 0L, source))
    expressions.Add(FoldList("fold.fuel-step", source))
    let _, body = compileBody context "fold-fuel" [] (List.ofSeq expressions)
    expectDiagnostic "fold callback iterations and list construction share the interpreter instruction budget" "RUNTIME_STEP_LIMIT" (fun () ->
        IrInterpreter.executeBody (noOpHost ()) "fold-fuel" body |> ignore)

let private testBoundedRuntimeValues () =
    let site = span "bounded-values.agent"
    let chainRecord =
        { Name = "Chain"
          Fields = [ { Name = "tail"; Type = TOption(TNamed "Chain") } ]
          SourceText = "record Chain"
          Span = site }
    let chainConstructor =
        wordEntry "chain.new" [ TOption(TNamed "Chain") ] [ TNamed "Chain" ] Set.empty [] (Some(RecordConstructor "Chain"))
    let chainContext = contextWith (Map.ofList [ "Chain", chainRecord ]) [ chainConstructor ]
    let buildChain depth =
        let expressions = ResizeArray<Expr>()
        expressions.Add(ConstructContainer(OptionNone, [ TNamed "Chain" ], site))
        expressions.Add(Call("chain.new", site))
        for _ in 1 .. depth do
            expressions.Add(ConstructContainer(OptionSome, [ TNamed "Chain" ], site))
            expressions.Add(Call("chain.new", site))
        List.ofSeq expressions

    let expectedChain depth =
        let rec create remaining =
            let tail =
                if remaining = 0 then OptionValue(TNamed "Chain", None)
                else OptionValue(TNamed "Chain", Some(create (remaining - 1)))
            RecordValue("Chain", Map.ofList [ "tail", tail ])
        create depth

    let _, ordinaryChain = compileBody chainContext "ordinary-chain" [] (buildChain 20)
    check "a nested nominal value below the configured depth limit preserves its shape" (
        IrInterpreter.executeBody (noOpHost ()) "ordinary-chain" ordinaryChain = [ expectedChain 20 ])

    let _, deepChain = compileBody chainContext "deep-chain" [] (buildChain 1200)
    expectDiagnostic "the reported 1,200-level recursive record chain fails with a bounded value diagnostic" "RUNTIME_VALUE_LIMIT" (fun () ->
        IrInterpreter.executeBody (noOpHost ()) "deep-chain" deepChain |> ignore)

    let treeRecord =
        { Name = "Tree"
          Fields =
            [ { Name = "left"; Type = TOption(TNamed "Tree") }
              { Name = "right"; Type = TOption(TNamed "Tree") } ]
          SourceText = "record Tree"
          Span = site }
    let treeConstructor =
        wordEntry "tree.new"
            [ TOption(TNamed "Tree"); TOption(TNamed "Tree") ]
            [ TNamed "Tree" ] Set.empty [] (Some(RecordConstructor "Tree"))
    let treeContext = contextWith (Map.ofList [ "Tree", treeRecord ]) [ treeConstructor ]
    let sharedTreeExpressions = ResizeArray<Expr>()
    sharedTreeExpressions.Add(ConstructContainer(OptionNone, [ TNamed "Tree" ], site))
    sharedTreeExpressions.Add(ConstructContainer(OptionNone, [ TNamed "Tree" ], site))
    sharedTreeExpressions.Add(Call("tree.new", site))
    for _ in 1 .. 40 do
        sharedTreeExpressions.Add(Call("dup", site))
        sharedTreeExpressions.Add(ConstructContainer(OptionSome, [ TNamed "Tree" ], site))
        sharedTreeExpressions.Add(Call("swap", site))
        sharedTreeExpressions.Add(ConstructContainer(OptionSome, [ TNamed "Tree" ], site))
        sharedTreeExpressions.Add(Call("tree.new", site))
    sharedTreeExpressions.Add(Push(LString "must-not-be-written", site))
    sharedTreeExpressions.Add(Push(LString "bounded output", site))
    sharedTreeExpressions.Add(Call("file.write", site))
    let _, sharedTree = compileBody treeContext "shared-tree" [] (List.ofSeq sharedTreeExpressions)
    let mutable effectCount = 0
    let guardedHost = host (fun _ _ _ -> ()) (fun _ _ -> ()) (fun _ -> effectCount <- effectCount + 1; EffectUnit) ignore
    expectDiagnostic "shared recursive children are charged by expanded output size" "RUNTIME_VALUE_LIMIT" (fun () ->
        IrInterpreter.executeBody guardedHost "shared-tree" sharedTree |> ignore)
    check "an oversized shared value is rejected before the following host effect" (effectCount = 0)

    let oversizedString = String.replicate 1_400_000 "x"
    let _, largeText = compileBody (defaultContext ()) "large-text" [] [ Push(LString oversizedString, site) ]
    let mutable outputDiagnostic: Diagnostic option = None
    try
        IrInterpreter.executeBody (noOpHost ()) "large-text" largeText |> ignore
    with LanguageException diagnostic -> outputDiagnostic <- Some diagnostic
    match outputDiagnostic with
    | Some diagnostic ->
        check "oversized string output diagnostic code" (diagnostic.Code = "RUNTIME_VALUE_LIMIT")
        check "oversized string diagnostic names the output-size bound" (diagnostic.Expected |> List.exists (fun value -> value.Contains("output bytes", StringComparison.Ordinal)))
    | None -> failwith "oversized string output should be rejected before public conversion"

let private testClosedEnumExecution () =
    let sourceSpan = span "closed-enum.agent"
    let phaseCases = [ "pending"; "renewed"; "cancelled" ]
    let otherCases = [ "pending"; "archived" ]
    let phase =
        { Name = "Phase"
          Cases = phaseCases
          SourceText = "enum Phase"
          Span = sourceSpan }
    let otherPhase =
        { Name = "OtherPhase"
          Cases = otherCases
          SourceText = "enum OtherPhase"
          Span = sourceSpan }
    let point =
        { Name = "Point"
          Fields = [ { Name = "x"; Type = TInt } ]
          SourceText = "record Point"
          Span = sourceSpan }
    let constructors enumName cases =
        cases
        |> List.map (fun caseName ->
            wordEntry (enumName + "." + caseName) [] [ TNamed enumName ] Set.empty []
                (Some(EnumCaseConstructor(enumName, caseName))))
    let context =
        contextWithEnums
            (Map.ofList [ point.Name, point ])
            (Map.ofList [ phase.Name, phase; otherPhase.Name, otherPhase ])
            (constructors phase.Name phaseCases @ constructors otherPhase.Name otherCases)
    let verified = Compiler.compileIrProgram context
    let compile name expressions = Compiler.compileIrBodyAgainstProgram context verified name [] expressions
    let matchArms = phaseCases |> List.map (fun caseName -> caseName, [ Push(LString caseName, sourceSpan) ])

    for caseName in phaseCases do
        let body =
            compile ("match-phase-" + caseName)
                [ Call("Phase." + caseName, sourceSpan)
                  Call("dup", sourceSpan)
                  MatchEnum(matchArms, sourceSpan) ]
        check $"enum constructor and MatchEnum execute the {caseName} case" (
            IrInterpreter.executeBody (noOpHost ()) ("match-phase-" + caseName) body =
                [ EnumValue("Phase", caseName); StringValue caseName ])

    let sameCase =
        compile "same-phase-case"
            [ Call("Phase.pending", sourceSpan)
              Call("Phase.pending", sourceSpan)
              Call("equals", sourceSpan) ]
    check "enum equality recognizes equal cases of the same nominal type" (
        IrInterpreter.executeBody (noOpHost ()) "same-phase-case" sameCase = [ BoolValue true ])

    let differentCase =
        compile "different-phase-cases"
            [ Call("Phase.pending", sourceSpan)
              Call("Phase.cancelled", sourceSpan)
              Call("equals", sourceSpan) ]
    check "enum equality distinguishes cases of the same nominal type" (
        IrInterpreter.executeBody (noOpHost ()) "different-phase-cases" differentCase = [ BoolValue false ])

    expectDiagnostic "polymorphic equality rejects different enum nominal types during compilation" "TYPE_STACK_MISMATCH" (fun () ->
        compile "different-enum-types"
            [ Call("Phase.pending", sourceSpan)
              Call("OtherPhase.pending", sourceSpan)
              Call("equals", sourceSpan) ]
        |> ignore)

    expectDiagnostic "structured inspection rejects an enum case outside its closed table" "VALUE_ENUM_CASE_INVALID" (fun () ->
        ValueInspection.toData verified [ EnumValue("Phase", "unknown") ] |> ignore)
    expectDiagnostic "structured inspection rejects a null enum case" "VALUE_ENUM_CASE_INVALID" (fun () ->
        ValueInspection.toData verified [ EnumValue("Phase", null) ] |> ignore)
    expectDiagnostic "structured inspection rejects an enum value naming a record type" "VALUE_NOMINAL_KIND_MISMATCH" (fun () ->
        ValueInspection.toData verified [ EnumValue("Point", "pending") ] |> ignore)

[<EntryPoint>]
let main _ =
    testProgramTrustAndSnapshotBinding ()
    testPublicBoundaryAndEmptyEntryOnly ()
    testOpaqueTypedInterpreterReentry ()
    testScopeRestoresOverwrittenOuterLocal ()
    testInterpreterOwnsFuel ()
    testEmptyEffectfulCallbackPreflight ()
    testListFoldExecutionAndPreflight ()
    testListFoldFuelLimit ()
    testBoundedRuntimeValues ()
    testClosedEnumExecution ()
    printfn "IR Interpreter tests passed (%d assertions)." assertions
    0
