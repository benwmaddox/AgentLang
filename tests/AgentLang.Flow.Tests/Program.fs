module AgentLang.Flow.Tests

open System
open AgentLang

let mutable private assertions = 0

let private check name condition =
    assertions <- assertions + 1
    if not condition then failwith $"{name}: assertion failed"

let private equal name expected actual =
    assertions <- assertions + 1
    if expected <> actual then failwith $"{name}: expected {expected}, got {actual}"

let private expectError name code result =
    match result with
    | Error diagnostic when diagnostic.Code = code -> assertions <- assertions + 1; diagnostic
    | Error diagnostic -> failwith $"{name}: expected {code}, got {diagnostic.Code}: {diagnostic.Message}"
    | Ok _ -> failwith $"{name}: expected diagnostic {code}"

let private captureLanguageError name code action =
    try
        action () |> ignore
        failwith $"{name}: expected diagnostic {code}"
    with
    | LanguageException diagnostic when diagnostic.Code = code -> assertions <- assertions + 1; diagnostic
    | LanguageException diagnostic -> failwith $"{name}: expected {code}, got {diagnostic.Code}: {diagnostic.Message}"

let private expectLanguageError name code action =
    captureLanguageError name code action |> ignore

let private parseWord source =
    match FlowParser.parseWord "<flow-test>" source with
    | Ok word -> word
    | Error diagnostic -> failwith (Diagnostics.render diagnostic)

let private parseExpression source =
    match FlowParser.parseExpression "<flow-test>" source with
    | Ok expression -> expression
    | Error diagnostic -> failwith (Diagnostics.render diagnostic)

let private span file line column length =
    { File = file; Line = line; Column = column; Length = length }

let private testParserLocationsAndQualification () =
    let multiline = "add(\n    1,\n    2\n)"
    match parseExpression multiline with
    | FlowExpression.Call(name, _, sourceSpan) ->
        equal "ordinary call name" "add" name
        equal "multiline call start" (span "<flow-test>" 1 1 multiline.Length) sourceSpan
    | other -> failwithf "Expected call AST, got %A" other

    let namespaced = parseExpression "outer::middle::inner(1)"
    match namespaced with
    | FlowExpression.Call(name, _, _) -> equal "multi-segment namespace maps to dictionary identity" "outer.middle.inner" name
    | other -> failwithf "Expected qualified call AST, got %A" other

    match parseExpression "make().stage(2)" with
    | FlowExpression.DotCall(FlowExpression.Call("make", _, _), "stage", _, callSpan) ->
        equal "dot call includes complete source range" (span "<flow-test>" 1 1 "make().stage(2)".Length) callSpan
    | other -> failwithf "Expected dot-call AST, got %A" other

    let wordSource = "word example(value: Int) -> Int {\n    effects none\n    add(value, 1)\n}"
    let word = parseWord wordSource
    equal "multiline word span covers closing delimiter" (span "<flow-test>" 1 1 wordSource.Length) word.Span

    for newline, expectedLine in [ "\n", 2; "\r\n", 2; "\r", 2 ] do
        let source = "word x" + newline
        let diagnostic = expectError (sprintf "EOF after %A" newline) "FLOW_INCOMPLETE_INPUT" (FlowParser.parseWord "<eof>" source)
        equal (sprintf "EOF line after %A" newline) expectedLine diagnostic.Span.Value.Line
        equal (sprintf "EOF column after %A" newline) 1 diagnostic.Span.Value.Column

    let eof = expectError "EOF after text retains final column" "FLOW_INCOMPLETE_INPUT" (FlowParser.parseWord "<eof>" "word x")
    equal "EOF after text line" 1 eof.Span.Value.Line
    equal "EOF after text column" 7 eof.Span.Value.Column

    let incomplete = expectError "nested EOF retains CR-only line" "FLOW_INCOMPLETE_INPUT" (FlowParser.parseWord "<eof>" "word x() -> Int {\reffects none\r1")
    equal "nested EOF line" 3 incomplete.Span.Value.Line
    equal "nested EOF column" 2 incomplete.Span.Value.Column

let private testFlowSourceCanonicalRoundTrip () =
    let source =
        """word choose(value: Int) -> Int {
    effects none
    let doubled = add(value, value);
    let selected = if equals(doubled, 2) {
        let branch = add(doubled, 40);
        branch
    } else {
        doubled
    };
    add(doubled, selected)
}"""
    let parsed = parseWord source
    let rendered = FlowSource.renderWord parsed
    let renderedAgain = rendered |> parseWord |> FlowSource.renderWord
    equal "Flow word has deterministic parse/render source" rendered renderedAgain
    check "canonical source keeps explicit parameter names" (rendered.Contains("choose(value: Int)", StringComparison.Ordinal))
    check "canonical source keeps namespace qualification" ((parseExpression "math::add(1, 2)") |> FlowSource.renderExpression |> fun text -> text.StartsWith("math::add(", StringComparison.Ordinal))
    for nested in [ "add(if true { 10 } else { 20 }, 5)"; "if false { 1 } else { 2 }.add(3)" ] do
        let firstRender = nested |> parseExpression |> FlowSource.renderExpression
        let secondRender = firstRender |> parseExpression |> FlowSource.renderExpression
        equal "nested if argument and dot-receiver forms parse/render canonically" firstRender secondRender

let private sourceSpan = span "<flow-lower>" 1 1 1

let private wordEntry name inputs outputs effects body : WordEntry =
    let definition =
        { Name = name
          Inputs = inputs
          Outputs = outputs
          Effects = effects
          Maturity = ProjectWord
          Revision = 1
          Documentation = "Flow test fixture."
          Body = body
          SourceText = name
          Span = sourceSpan }
    { Definition = definition
      Builtin = None
      Status = Persistent
      Maturity = ProjectWord
      Revision = 1 }

let private loweringContext (words: WordEntry list) (parameters: Map<string, string list>) : FlowLowering.Context =
    let allWords: Map<string, WordEntry> = words |> List.fold (fun current (entry: WordEntry) -> Map.add entry.Definition.Name entry current) Compiler.primitives
    let ids =
        allWords
        |> Map.toList
        |> List.map (fun (name, (entry: WordEntry)) ->
            let prefix =
                match entry.Builtin with
                | Some(BuiltinOp _) -> "primitive-"
                | Some _ -> "generated-"
                | None -> "user-"
            name, WordId(prefix + name))
        |> Map.ofList
    { CompilerContext =
        { Words = allWords
          Records = Map.empty
          Scalars = Map.empty
          WordIds = ids }
      ParameterNames = parameters
      SourceOrigins = Map.empty }

let private host (events: ResizeArray<string>) =
    { PreflightEffects = fun _ _ _ -> ()
      ChargeInstruction = fun _ _ -> ()
      RecordBranchOutcome = fun _ _ _ -> ()
      RecordUse = ignore
      InvokeEffect = function
          | WriteVirtualConsole(_, contents) -> events.Add(contents); EffectUnit
          | other -> failwithf "Unexpected effect command: %A" other
      WordDefinitionSpan = fun _ -> None
      PrimitiveDefinitionSpan = fun _ -> None }

let private testLoweringAndExecution () =
    let effect = Set.singleton "console.write"
    let emit name payload value =
        wordEntry name [] [ TInt ] effect
            [ Push(LString payload, sourceSpan); Call("console.write", sourceSpan); Call("drop", sourceSpan); Push(LInt value, sourceSpan) ]
    let pair = wordEntry "pair" [ TInt; TInt ] [ TInt ] Set.empty [ Call("add", sourceSpan) ]
    let context = loweringContext [ pair; emit "left" "left" 10L; emit "right" "right" 20L ] (Map.ofList [ "pair", [ "first"; "second" ] ])

    let ordinary = parseExpression "add(10, 20)"
    let dot = parseExpression "10.add(20)"
    let _, ordinaryChecked = FlowLowering.checkExpression context ordinary
    let _, dotChecked = FlowLowering.checkExpression context dot
    equal "ordinary calls infer their output" [ TInt ] ordinaryChecked.Stack
    equal "dot calls infer the same output" ordinaryChecked.Stack dotChecked.Stack

    let ordinaryCompiled = FlowLowering.compileExpression context ordinary
    let dotCompiled = FlowLowering.compileExpression context dot
    let ordinaryValue = IrInterpreter.executeBody (host (ResizeArray())) "ordinary" ordinaryCompiled.Body
    let dotValue = IrInterpreter.executeBody (host (ResizeArray())) "dot" dotCompiled.Body
    equal "ordinary and dot call have equal semantics" ordinaryValue dotValue
    equal "add result" [ IntValue 30L ] ordinaryValue

    let conditionalArgument = FlowLowering.compileExpression context (parseExpression "add(if true { 10 } else { 20 }, 5)")
    equal "conditional expression is evaluated as an ordinary call argument" [ IntValue 15L ] (IrInterpreter.executeBody (host (ResizeArray())) "conditional-argument" conditionalArgument.Body)
    let conditionalReceiver = FlowLowering.compileExpression context (parseExpression "if false { 1 } else { 2 }.add(3)")
    equal "conditional expression is evaluated once as a dot-stage receiver" [ IntValue 5L ] (IrInterpreter.executeBody (host (ResizeArray())) "conditional-receiver" conditionalReceiver.Body)

    let order = ResizeArray<string>()
    let named = parseExpression "pair(second = right(), first = left())"
    let compiledNamed = FlowLowering.compileExpression context named
    IrInterpreter.executeBody (host order) "named" compiledNamed.Body |> ignore
    equal "named arguments evaluate once in written order" [ "right"; "left" ] (List.ofSeq order)

    let receiverEvents = ResizeArray<string>()
    let receiverContext = loweringContext [ emit "make-value" "receiver" 4L ] Map.empty
    let receiverExpression = parseExpression "make-value().add(2)"
    let receiver = FlowLowering.compileExpression receiverContext receiverExpression
    let receiverResult = IrInterpreter.executeBody (host receiverEvents) "receiver-once" receiver.Body
    equal "dot receiver result" [ IntValue 6L ] receiverResult
    equal "dot receiver is evaluated exactly once" [ "receiver" ] (List.ofSeq receiverEvents)

    let branchSource =
        """word choose(value: Int) -> Int {
    effects none
    let doubled = add(value, value);
    let selected = if equals(doubled, 2) {
        let branch = add(doubled, 40);
        branch
    } else {
        doubled
    };
    add(doubled, selected)
}"""
    let flowWord = parseWord branchSource
    let compiledWord = FlowLowering.compileWord (loweringContext [] Map.empty) (WordId "user-choose") flowWord
    let verifiedProgram = VerifiedIrProgram.inspect compiledWord.Program
    let flowFunction = verifiedProgram.FunctionsById[WordId "user-choose"]
    let syntheticSites =
        verifiedProgram.SourceMap
        |> Map.toList
        |> List.choose (fun (_, source) ->
            if source.SiteOwner = Some(WordId "user-choose") && source.SourceKind.StartsWith("synthetic-", StringComparison.Ordinal) then Some source.SiteSpan else None)
    let expectedSyntheticOrigins = compiledWord.Lowered.Projection.SyntheticOrigins |> Map.toList |> List.map snd
    equal "each synthetic Flow operation has a distinct origin projection" (List.sort expectedSyntheticOrigins) (List.sort syntheticSites)
    equal "compiled word exposes every verified source-site identity" (verifiedProgram.SourceMap |> Map.toList |> List.filter (fun (_, source) -> source.SiteOwner = Some(WordId "user-choose")) |> List.length) compiledWord.SiteOrigins.Count
    check "verified source locations are real authored spans, never private marker columns"
        (flowFunction.FunctionBody.Code |> List.forall (fun instruction ->
            let source = verifiedProgram.SourceMap[instruction.Site]
            source.SiteSpan.Column < 1000))
    let missingOrigin =
        captureLanguageError "legacy compiler entrypoint rejects unprojected Flow marker spans" "IR_SOURCE_ORIGIN_MISSING" (fun () -> Compiler.compileIrProgram compiledWord.Context.CompilerContext |> ignore)
    let renderedDiagnostic = Diagnostics.render missingOrigin + sprintf "%A" missingOrigin
    check "missing-origin diagnostics do not expose private marker coordinates" (not (renderedDiagnostic.Contains("214748", StringComparison.Ordinal)))
    let invoke =
        Compiler.compileIrBodyAgainstProgramWithSourceOrigins
            compiledWord.Context.CompilerContext
            compiledWord.Program
            "invoke-choose"
            []
            [ Push(LInt 1L, sourceSpan); Call("choose", sourceSpan) ]
            compiledWord.Context.SourceOrigins
    equal "Flow Scope discards its branch-local while preserving outer locals and result" [ IntValue 44L ] (IrInterpreter.executeBody (host (ResizeArray())) "invoke-choose" invoke)
    let invokeElse =
        Compiler.compileIrBodyAgainstProgramWithSourceOrigins
            compiledWord.Context.CompilerContext
            compiledWord.Program
            "invoke-choose-else"
            []
            [ Push(LInt 2L, sourceSpan); Call("choose", sourceSpan) ]
            compiledWord.Context.SourceOrigins
    equal "Flow Scope preserves outer locals on the other branch" [ IntValue 8L ] (IrInterpreter.executeBody (host (ResizeArray())) "invoke-choose-else" invokeElse)

    let increment = parseWord "word increment(value: Int) -> Int {\n    effects none\n    add(value, 1)\n}"
    let first = FlowLowering.compileWord (loweringContext [] Map.empty) (WordId "user-increment") increment
    let twice = parseWord "word twice(value: Int) -> Int {\n    effects none\n    increment(increment(value))\n}"
    let second = FlowLowering.compileWord first.Context (WordId "user-twice") twice
    let secondProgram = VerifiedIrProgram.inspect second.Program
    check "growing Flow contexts retain prior source-origin projections"
        (first.Context.SourceOrigins.Count > 0
         && second.Context.SourceOrigins.Count > first.Context.SourceOrigins.Count
         && (secondProgram.SourceMap |> Map.toList |> List.exists (fun (_, source) -> source.SiteOwner = Some(WordId "user-increment") && source.SiteSpan.Column < 1000)))

let private testFlowDiagnostics () =
    let context = loweringContext [] Map.empty
    let ambiguous = parseExpression "1.unknown(2)"
    expectLanguageError "unknown dot stage is rejected statically" "FLOW_UNKNOWN_DOT_STAGE" (fun () -> FlowLowering.checkExpression context ambiguous |> ignore)

    let invalidWord = parseWord "word invalid(value: Int) -> Int {\n    effects none\n    missing(value)\n}"
    expectLanguageError "unknown ordinary call is rejected statically" "FLOW_UNKNOWN_CALL" (fun () -> FlowLowering.checkWord context invalidWord |> ignore)

    let scoped = parseWord "word scoped(value: Int) -> Int {\n    effects none\n    let answer = if true { let inner = value; inner } else { value };\n    inner\n}"
    expectLanguageError "branch-local names cannot escape their lexical scope" "FLOW_UNKNOWN_LOCAL" (fun () -> FlowLowering.checkWord context scoped |> ignore)

    let sameA = wordEntry "one.same" [ TInt ] [ TInt ] Set.empty [ Call("int.abs", sourceSpan) ]
    let sameB = wordEntry "two.same" [ TInt ] [ TInt ] Set.empty [ Call("int.abs", sourceSpan) ]
    let qualifiedContext = loweringContext [ sameA; sameB ] Map.empty
    expectLanguageError "ambiguous dot stages fail deterministically" "FLOW_AMBIGUOUS_DOT_STAGE" (fun () ->
        FlowLowering.checkExpression qualifiedContext (parseExpression "3.same()") |> ignore)
    let qualified = FlowLowering.compileExpression qualifiedContext (parseExpression "one::same(-3)")
    equal "explicit namespace qualification resolves the intended word" [ IntValue 3L ] (IrInterpreter.executeBody (host (ResizeArray())) "qualified" qualified.Body)

[<EntryPoint>]
let main _ =
    testParserLocationsAndQualification ()
    testFlowSourceCanonicalRoundTrip ()
    testLoweringAndExecution ()
    testFlowDiagnostics ()
    printfn "Flow tests passed: %d assertions" assertions
    0
