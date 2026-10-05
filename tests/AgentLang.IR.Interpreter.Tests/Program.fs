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
      WordIds = wordIds }

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

[<EntryPoint>]
let main _ =
    testProgramTrustAndSnapshotBinding ()
    testPublicBoundaryAndEmptyEntryOnly ()
    testInterpreterOwnsFuel ()
    testEmptyEffectfulCallbackPreflight ()
    printfn "IR Interpreter tests passed (%d assertions)." assertions
    0
