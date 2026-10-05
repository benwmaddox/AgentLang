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

let private loweringContextWith (records: Map<string, RecordDefinition>) (scalars: Map<string, ScalarTypeDefinition>) (words: WordEntry list) (parameters: Map<string, string list>) : FlowLowering.Context =
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
          Records = records
          Scalars = scalars
          WordIds = ids }
      ParameterNames = parameters
      SourceOrigins = Map.empty }

let private loweringContext (words: WordEntry list) (parameters: Map<string, string list>) : FlowLowering.Context =
    loweringContextWith Map.empty Map.empty words parameters

let private generatedEntry name builtin inputs outputs effects =
    let ordinary = wordEntry name inputs outputs effects []
    { ordinary with
        Definition = { ordinary.Definition with Maturity = LibraryWord }
        Builtin = Some builtin
        Status = Persistent
        Maturity = LibraryWord }

let private richTypeContext extraWords =
    let emailSpan = span "types.agent" 1 1 32
    let customerSpan = span "types.agent" 3 1 56
    let email =
        { Name = "Email"
          BaseType = TString
          Validator = Some "email.valid?"
          SourceText = "scalar Email : String validate email.valid?"
          Span = emailSpan }
    let customer =
        { Name = "Customer"
          Fields = [ { Name = "email"; Type = TNamed "Email" } ]
          SourceText = "record Customer"
          Span = customerSpan }
    let generated =
        [ generatedEntry "Email.new" (ScalarConstructor "Email") [ TString ] [ TNamed "Email" ] Set.empty
          generatedEntry "Email.value" (ScalarAccessor "Email") [ TNamed "Email" ] [ TString ] Set.empty
          generatedEntry "customer.new" (RecordConstructor "Customer") [ TNamed "Email" ] [ TNamed "Customer" ] Set.empty
          generatedEntry "customer.email" (RecordAccessor("Customer", "email")) [ TNamed "Customer" ] [ TNamed "Email" ] Set.empty ]
    let emailValidator =
        wordEntry "email.valid?" [ TString ] [ TBool ] Set.empty
            [ Push(LString "@", sourceSpan); Call("string.contains", sourceSpan) ]
    loweringContextWith
        (Map.ofList [ "Customer", customer ])
        (Map.ofList [ "Email", email ])
        (generated @ [ emailValidator ] @ extraWords)
        Map.empty

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

let private testIterativeAstDepthLimit () =
    let postfix stages = "1" + String.replicate stages ".stage()"
    let boundarySource = "1" + String.replicate 127 ".abs()"
    let boundaryExpression =
        match FlowParser.parseExpression "<depth>" boundarySource with
        | Ok expression -> expression
        | Error diagnostic -> failwith $"Exact depth boundary should parse, got {diagnostic.Code}: {diagnostic.Message}"
    check "Flow AST depth accepts the exact 128-node boundary" true
    let boundaryRendered = FlowSource.renderExpression boundaryExpression
    equal "exact-depth expression renders and reparses" boundaryRendered (boundaryRendered |> parseExpression |> FlowSource.renderExpression)
    let boundaryCompiled = FlowLowering.compileExpression (loweringContext [] Map.empty) boundaryExpression
    equal "exact-depth expression lowers and executes" [ IntValue 1L ]
        (IrInterpreter.executeBody (host (ResizeArray())) "flow-depth-boundary" boundaryCompiled.Body)
    expectError "Flow postfix AST rejects depth 129" "FLOW_NESTING_LIMIT" (FlowParser.parseExpression "<depth>" (postfix 128)) |> ignore

    let mixedIf =
        String.replicate 30 "identity("
        + "if true { 1"
        + String.replicate 100 ".stage()"
        + " } else { 1 }"
        + String.replicate 30 ")"
    expectError "combined call and if/postfix depth is checked" "FLOW_NESTING_LIMIT" (FlowParser.parseExpression "<depth>" mixedIf) |> ignore

    let mixedMatch =
        String.replicate 30 "identity("
        + "match input { some item => { 1"
        + String.replicate 100 ".stage()"
        + " } none => { 0 } }"
        + String.replicate 30 ")"
    expectError "combined call and match/postfix depth is checked" "FLOW_NESTING_LIMIT" (FlowParser.parseExpression "<depth>" mixedMatch) |> ignore

    let overdeepWord = "word deep() -> Int {\n    effects none\n    " + postfix 128 + "\n}"
    expectError "Flow word bodies receive the same iterative AST depth validation" "FLOW_NESTING_LIMIT" (FlowParser.parseWord "<depth>" overdeepWord) |> ignore

    let hostNested depth =
        [ 1 .. depth - 1 ]
        |> List.fold (fun inner _ -> FlowExpression.Call("int.abs", [ FlowArgument.Positional inner ], sourceSpan)) (FlowExpression.Literal(LInt 1L, sourceSpan))
    let hostWord body =
        { Name = "host-built.depth"
          Parameters = []
          Outputs = [ TInt ]
          Effects = Set.empty
          Documentation = ""
          Body = [ FlowStatement.Evaluate body ]
          SourceText = "host-built AST"
          Span = sourceSpan
          SyntaxVersion = 1 }
    let safeContext = loweringContext [] Map.empty
    let hostBoundary = hostNested FlowStructure.maxExpressionDepth
    let hostBoundaryWord = hostWord hostBoundary
    let renderedBoundary = FlowSource.renderExpression hostBoundary
    check "host-built expression at the exact boundary renders" (not (String.IsNullOrWhiteSpace renderedBoundary))
    check "host-built word at the exact boundary renders" (not (String.IsNullOrWhiteSpace (FlowSource.renderWord hostBoundaryWord)))
    FlowLowering.lowerExpression safeContext hostBoundary |> ignore
    FlowLowering.lowerWord safeContext hostBoundaryWord |> ignore
    equal "host-built expression at the exact boundary lowers and executes" [ IntValue 1L ]
        (let compiled = FlowLowering.compileExpression safeContext hostBoundary
         IrInterpreter.executeBody (host (ResizeArray())) "host-depth-boundary" compiled.Body)

    let hostTooDeep = hostNested (FlowStructure.maxExpressionDepth + 1)
    let hostTooDeepWord = hostWord hostTooDeep
    let renderedError = captureLanguageError "public expression renderer bounds host-built trees" "FLOW_NESTING_LIMIT" (fun () -> FlowSource.renderExpression hostTooDeep |> ignore)
    equal "renderer reports the over-limit node's source span" (Some sourceSpan) renderedError.Span
    let loweringError = captureLanguageError "public expression lowerer bounds host-built trees" "FLOW_NESTING_LIMIT" (fun () -> FlowLowering.lowerExpression safeContext hostTooDeep |> ignore)
    equal "lowerer reports the over-limit node's source span" (Some sourceSpan) loweringError.Span
    expectLanguageError "expression checking inherits the public lowering bound" "FLOW_NESTING_LIMIT" (fun () -> FlowLowering.checkExpression safeContext hostTooDeep |> ignore)
    expectLanguageError "expression compilation inherits the public lowering bound" "FLOW_NESTING_LIMIT" (fun () -> FlowLowering.compileExpression safeContext hostTooDeep |> ignore)
    expectLanguageError "word renderer bounds host-built body trees" "FLOW_NESTING_LIMIT" (fun () -> FlowSource.renderWord hostTooDeepWord |> ignore)
    expectLanguageError "word lowerer bounds host-built body trees" "FLOW_NESTING_LIMIT" (fun () -> FlowLowering.lowerWord safeContext hostTooDeepWord |> ignore)
    expectLanguageError "word checking inherits the lowerer bound" "FLOW_NESTING_LIMIT" (fun () -> FlowLowering.checkWord safeContext hostTooDeepWord |> ignore)
    expectLanguageError "word compilation inherits the lowerer bound" "FLOW_NESTING_LIMIT" (fun () -> FlowLowering.compileWord safeContext (WordId "user-host-too-deep") hostTooDeepWord |> ignore)

    let sharedResultType levels =
        let mutable currentType = TInt
        for _ in 1 .. levels do currentType <- TResult(currentType, currentType)
        currentType
    let nestedListType levels =
        let mutable currentType = TInt
        for _ in 1 .. levels do currentType <- TList currentType
        currentType
    let typeExpression typeValue =
        FlowExpression.Container(FlowContainerConstructor.ListEmpty, [ { Type = typeValue; Span = sourceSpan } ], None, sourceSpan)
    let boundaryType = nestedListType (FlowStructure.maxExpressionDepth - 1)
    let boundaryTypeExpression = typeExpression boundaryType
    check "container type argument at the exact depth boundary renders"
        (not (String.IsNullOrWhiteSpace (FlowSource.renderExpression boundaryTypeExpression)))
    FlowLowering.lowerExpression safeContext boundaryTypeExpression |> ignore
    let boundaryTypeWord =
        { hostWord (FlowExpression.Literal(LUnit, sourceSpan)) with
            Parameters = [ { Name = "value"; Type = boundaryType; Span = sourceSpan } ] }
    check "word signature type at the exact depth boundary renders" (not (String.IsNullOrWhiteSpace (FlowSource.renderWord boundaryTypeWord)))
    FlowLowering.lowerWord safeContext boundaryTypeWord |> ignore

    let tooDeepType = nestedListType FlowStructure.maxExpressionDepth
    let tooDeepTypeExpression = typeExpression tooDeepType
    let typeRenderError = captureLanguageError "container type renderer bounds host-built types" "FLOW_NESTING_LIMIT" (fun () -> FlowSource.renderExpression tooDeepTypeExpression |> ignore)
    equal "container type depth error points to the type argument" (Some sourceSpan) typeRenderError.Span
    expectLanguageError "container type lowerer bounds host-built types" "FLOW_NESTING_LIMIT" (fun () -> FlowLowering.lowerExpression safeContext tooDeepTypeExpression |> ignore)
    let tooDeepInputTypeWord =
        { hostWord (FlowExpression.Literal(LUnit, sourceSpan)) with
            Parameters = [ { Name = "value"; Type = tooDeepType; Span = sourceSpan } ] }
    expectLanguageError "word renderer bounds host-built parameter types" "FLOW_NESTING_LIMIT" (fun () -> FlowSource.renderWord tooDeepInputTypeWord |> ignore)
    expectLanguageError "word lowerer bounds host-built parameter types" "FLOW_NESTING_LIMIT" (fun () -> FlowLowering.lowerWord safeContext tooDeepInputTypeWord |> ignore)
    let tooDeepOutputTypeWord = { hostWord (FlowExpression.Literal(LUnit, sourceSpan)) with Outputs = [ tooDeepType ] }
    expectLanguageError "word renderer bounds host-built output types" "FLOW_NESTING_LIMIT" (fun () -> FlowSource.renderWord tooDeepOutputTypeWord |> ignore)
    expectLanguageError "word lowerer bounds host-built output types" "FLOW_NESTING_LIMIT" (fun () -> FlowLowering.lowerWord safeContext tooDeepOutputTypeWord |> ignore)

    let sharedExpression levels =
        let mutable currentExpression = FlowExpression.Literal(LInt 1L, sourceSpan)
        for _ in 1 .. levels do
            currentExpression <- FlowExpression.Call("add", [ FlowArgument.Positional currentExpression; FlowArgument.Positional currentExpression ], sourceSpan)
        currentExpression
    let expressionDagUnderBudget = sharedExpression 15
    FlowStructure.validateExpressionNesting [ expressionDagUnderBudget ]
    let typeDagUnderBudget = typeExpression (sharedResultType 15)
    FlowStructure.validateExpressionNesting [ typeDagUnderBudget ]
    let expressionDagOverBudget = sharedExpression 16
    expectLanguageError "shared expression DAG expansion is bounded before rendering" "FLOW_STRUCTURE_LIMIT" (fun () ->
        FlowSource.renderExpression expressionDagOverBudget |> ignore)
    expectLanguageError "shared expression DAG expansion is bounded before lowering" "FLOW_STRUCTURE_LIMIT" (fun () ->
        FlowLowering.lowerExpression safeContext expressionDagOverBudget |> ignore)
    let typeDagOverBudget = typeExpression (sharedResultType 16)
    expectLanguageError "shared type DAG expansion is bounded before rendering" "FLOW_STRUCTURE_LIMIT" (fun () ->
        FlowSource.renderExpression typeDagOverBudget |> ignore)
    let combinedDagWord =
        { hostWord expressionDagUnderBudget with
            Parameters = [ { Name = "value"; Type = sharedResultType 15; Span = sourceSpan } ] }
    expectLanguageError "word parameters and body share one expanded-node budget" "FLOW_STRUCTURE_LIMIT" (fun () ->
        FlowSource.renderWord combinedDagWord |> ignore)

let private testSparseFlowSourceMarkerAllocation () =
    let context = loweringContext [] (Map.ofList [ "add", [ "left"; "right" ] ])
    let flowWord =
        parseWord """word named(value: Int) -> Int {
    effects none
    value
}"""
    let first = FlowLowering.compileWord context (WordId "user-named") flowWord
    let shiftMarker marker =
        if marker.Length = 0 then { marker with Column = marker.Column - 2 }
        else marker
    let rec shiftExpression expression =
        match expression with
        | Push(literal, source) -> Push(literal, shiftMarker source)
        | Call(name, source) -> Call(name, shiftMarker source)
        | ConstructContainer(kind, types, source) -> ConstructContainer(kind, types, shiftMarker source)
        | MapList(name, source) -> MapList(name, shiftMarker source)
        | FilterList(name, source) -> FilterList(name, shiftMarker source)
        | EachList(name, source) -> EachList(name, shiftMarker source)
        | Let(name, source) -> Let(name, shiftMarker source)
        | Load(name, source) -> Load(name, shiftMarker source)
        | If(thenBody, elseBody, source) -> If(List.map shiftExpression thenBody, List.map shiftExpression elseBody, shiftMarker source)
        | Scope(body, source) -> Scope(List.map shiftExpression body, shiftMarker source)
        | MatchOption(name, someBody, noneBody, source) -> MatchOption(name, List.map shiftExpression someBody, List.map shiftExpression noneBody, shiftMarker source)
        | MatchResult(okName, errorName, okBody, errorBody, source) -> MatchResult(okName, errorName, List.map shiftExpression okBody, List.map shiftExpression errorBody, shiftMarker source)
    let shiftedWords =
        first.Context.CompilerContext.Words
        |> Map.map (fun _ entry ->
            { entry with
                Definition = { entry.Definition with Body = List.map shiftExpression entry.Definition.Body } })
    let shiftedOrigins = first.Context.SourceOrigins |> Map.toList |> List.map (fun (marker, origin) -> shiftMarker marker, origin) |> Map.ofList
    let sparseContext =
        { first.Context with
            CompilerContext = { first.Context.CompilerContext with Words = shiftedWords }
            SourceOrigins = shiftedOrigins }
    let newCall = FlowLowering.compileExpression sparseContext (parseExpression "add(right = 2, left = 1)")
    let markerIndex (marker: SourceSpan) = int64 Int32.MaxValue - int64 marker.Column
    let retainedMaxIndex = shiftedOrigins |> Map.toSeq |> Seq.map (fst >> markerIndex) |> Seq.max
    let allocatedIndices = newCall.Lowered.Projection.SyntheticOrigins |> Map.toSeq |> Seq.map (fst >> markerIndex) |> Seq.toList
    check "incremental Flow compilation allocates strictly beyond sparse retained markers"
        (not allocatedIndices.IsEmpty && List.forall (fun index -> index > retainedMaxIndex) allocatedIndices)

    let authored = span "<marker-exhaustion>" 1 1 1
    let exhaustedMarker = { authored with Column = 1; Length = 0 }
    let exhaustedContext = { context with SourceOrigins = Map.ofList [ exhaustedMarker, authored ] }
    expectLanguageError "synthetic marker exhaustion is a structured diagnostic" "FLOW_SOURCE_MARKER_EXHAUSTED" (fun () ->
        FlowLowering.compileExpression exhaustedContext (parseExpression "add(right = 2, left = 1)") |> ignore)


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

let private testContainerAndMatchSyntaxRoundTrip () =
    let constructors =
        [ "list::empty<Int>()"
          "list::singleton<Int>(5)"
          "option::none<Email>()"
          "option::some<Email>(Email::new(\"a@b\"))"
          "result::ok<Int, Email>(5)"
          "result::error<Int, Email>(Email::new(\"invalid@x\"))"
          "list::singleton<Int>(if true { 3 } else { 4 })"
          "list::append(list::singleton<Int>(1), 2)"
          "list::empty<List<Option<Result<Email, Customer>>>>()" ]
    for source in constructors do
        let canonical = source |> parseExpression |> FlowSource.renderExpression
        equal ("constructor source round-trips: " + source) canonical (canonical |> parseExpression |> FlowSource.renderExpression)

    for source in
        [ "match option::some<Int>(7) { none => { 0 } some value => { value } }"
          "match result::ok<String, String>(\"ok\") { error value => { value } ok value => { value } }"
          "list::singleton<Int>(match option::none<Int>() { some item => { item } none => { 0 } })"
          "list::singleton<Int>(if true { 3 } else { 4 })"
          "add(match option::some<Int>(3) { some item => { item } none => { 4 } }, 5)"
          "match option::some<Int>(3) { some item => { item } none => { 4 } }.add(5)" ] do
        let canonical = source |> parseExpression |> FlowSource.renderExpression
        equal ("nested constructor/case source round-trips: " + source) canonical (canonical |> parseExpression |> FlowSource.renderExpression)

    let wordSource =
        """word locate(value: List<Option<Result<Email, Customer>>>) -> List<Option<Result<Email, Customer>>> {
    effects none
    let empty = list::empty<Option<Result<Email, Customer>>>();
    match option::some<Email>(Email::new("a@b")) {
        none => { empty }
        some address => { empty }
    }
}"""
    let canonicalWord = wordSource |> parseWord |> FlowSource.renderWord
    equal "nested generic signature and exhaustive case word source round-trip" canonicalWord (canonicalWord |> parseWord |> FlowSource.renderWord)

    match parseExpression constructors[1] with
    | FlowExpression.Container(FlowContainerConstructor.ListSingleton, [ argument ], Some _, constructorSpan) ->
        equal "constructor type argument span text" "Int" ("list::singleton<Int>(5)".Substring(argument.Span.Column - 1, argument.Span.Length))
        equal "constructor source span covers its payload" "list::singleton<Int>(5)" ("list::singleton<Int>(5)".Substring(constructorSpan.Column - 1, constructorSpan.Length))
    | other -> failwithf "Expected a typed list constructor, got %A" other

    match parseExpression "list::append(list::singleton<Int>(1), 2)" with
    | FlowExpression.Call("list.append", _, _) -> check "non-constructor names in container namespaces remain ordinary calls" true
    | other -> failwithf "Expected qualified ordinary list call, got %A" other

    let reverseOrderResult = "match result::ok<Int, String>(1) {\n    error value => { 0 }\n    ok value => { 1 }\n}"
    let canonicalResult = reverseOrderResult |> parseExpression |> FlowSource.renderExpression
    check "Result cases render in canonical Ok then Error order"
        (canonicalResult.IndexOf("ok value", StringComparison.Ordinal) < canonicalResult.IndexOf("error value", StringComparison.Ordinal))
    match parseExpression reverseOrderResult with
    | FlowExpression.MatchResult(_, okCase, errorCase, _) ->
        equal "Result ok payload name is retained" "value" okCase.Name
        equal "Result error arm is retained" "value" errorCase.Name
    | other -> failwithf "Expected a result match AST, got %A" other

let private testContainerAndMatchDiagnostics () =
    for source, code in
        [ "list::empty()", "FLOW_CONSTRUCTOR_TYPE_ARGUMENTS_REQUIRED"
          "list::empty<Int, String>()", "FLOW_CONSTRUCTOR_TYPE_ARITY"
          "result::ok<Int>(1)", "FLOW_CONSTRUCTOR_TYPE_ARITY"
          "list::empty<Int>(1)", "FLOW_CONSTRUCTOR_ARITY"
          "option::some<Int>()", "FLOW_CONSTRUCTOR_ARITY"
          "match option::none<Int>() { some value => { value } }", "FLOW_MATCH_CASE_MISSING"
          "match option::none<Int>() { some value => { value } some other => { other } none => { 0 } }", "FLOW_MATCH_CASE_DUPLICATE"
          "match option::none<Int>() { some value => { value } ok other => { other } }", "FLOW_MATCH_CASE_KIND"
          "match option::none<Int>() { unknown => { 0 } }", "FLOW_MATCH_CASE_UNKNOWN" ] do
        expectError ("Flow syntax error: " + source) code (FlowParser.parseExpression "<flow-invalid>" source) |> ignore

let private testContainerAndMatchLowering () =
    let sideEffect =
        wordEntry "side-effect" [] [ TInt ] (Set.singleton "console.write")
            [ Push(LString "unexpected", sourceSpan); Call("console.write", sourceSpan); Call("drop", sourceSpan); Push(LInt 9L, sourceSpan) ]
    let context = richTypeContext [ sideEffect ]
    let evaluate source =
        let compiled = FlowLowering.compileExpression context (parseExpression source)
        IrInterpreter.executeBody (host (ResizeArray())) source compiled.Body

    equal "empty list retains its explicit primitive element type" [ ListValue(TInt, []) ] (evaluate "list::empty<Int>()")
    equal "singleton list retains its explicit primitive element type" [ ListValue(TInt, [ IntValue 42L ]) ] (evaluate "list::singleton<Int>(42)")
    equal "None retains a refined nominal payload type" [ OptionValue(TNamed "Email", None) ] (evaluate "option::none<Email>()")
    let customerValue = RecordValue("Customer", Map.ofList [ "email", NamedValue("Email", StringValue "a@b") ])
    equal "nested nominal record is constructed inside a typed container" [ ListValue(TNamed "Customer", [ customerValue ]) ]
        (evaluate "list::singleton<Customer>(customer::new(Email::new(\"a@b\")))")
    equal "Ok retains both declared result types" [ ResultValue(TInt, TNamed "Email", Ok(IntValue 7L)) ]
        (evaluate "result::ok<Int, Email>(7)")
    equal "Error retains both declared result types" [ ResultValue(TInt, TNamed "Email", Error(NamedValue("Email", StringValue "bad@x"))) ]
        (evaluate "result::error<Int, Email>(Email::new(\"bad@x\"))")
    captureLanguageError "refined nominal constructor rejects an invalid address" "REFINEMENT_FAILED" (fun () ->
        evaluate "option::some<Email>(Email::new(\"invalid\"))" |> ignore) |> ignore
    equal "deep empty list preserves nested nominal/container metadata"
        [ ListValue(TOption(TResult(TNamed "Email", TNamed "Customer")), []) ]
        (evaluate "list::empty<Option<Result<Email, Customer>>>()")
    equal "ordinary words in the list namespace remain callable"
        [ ListValue(TInt, [ IntValue 1L; IntValue 2L ]) ]
        (evaluate "list::append(list::singleton<Int>(1), 2)")
    equal "If expression is evaluated as a constructor payload" [ ListValue(TInt, [ IntValue 3L ]) ]
        (evaluate "list::singleton<Int>(if true { 3 } else { 4 })")

    equal "Some match arm runs and receives its payload" [ IntValue 41L ]
        (evaluate "match option::some<Int>(41) { none => { 0 } some value => { value } }")
    equal "None match arm runs without a payload" [ IntValue -1L ]
        (evaluate "match option::none<Int>() { some value => { value } none => { -1 } }")
    equal "Ok match arm binds its payload" [ StringValue "ok" ]
        (evaluate "match result::ok<String, String>(\"ok\") { error value => { value } ok value => { value } }")
    equal "Error match arm binds its payload using the same source name" [ StringValue "error" ]
        (evaluate "match result::error<String, String>(\"error\") { ok value => { value } error value => { value } }")
    equal "a match expression can be a constructor payload" [ ListValue(TInt, [ IntValue 0L ]) ]
        (evaluate "list::singleton<Int>(match option::none<Int>() { some item => { item } none => { 0 } })")
    equal "a match expression can be a call argument" [ IntValue 8L ]
        (evaluate "add(match option::some<Int>(3) { some item => { item } none => { 4 } }, 5)")
    equal "a match expression can be evaluated once as a dot receiver" [ IntValue 8L ]
        (evaluate "match option::some<Int>(3) { some item => { item } none => { 4 } }.add(5)")

    let optionWord =
        parseWord """word option.read(value: Option<Int>) -> Int {
    effects none
    match value {
        some payload => { payload }
        none => { 0 }
    }
}"""
    let compiledOptionWord = FlowLowering.compileWord context (WordId "user-option-read") optionWord
    let optionProgram = VerifiedIrProgram.inspect compiledOptionWord.Program
    let optionCoverage = optionProgram.CoverageByWord[WordId "user-option-read"]
    let optionBranches = optionCoverage.BranchOutcomes |> Map.toList |> List.collect snd
    equal "Option library body exposes both mandatory case outcomes" [ "some"; "none" ] optionBranches
    let optionBranchSites = optionCoverage.BranchOutcomes |> Map.toList |> List.map fst
    equal "Option match has one source-mapped branch site" 1 optionBranchSites.Length
    check "Option branch site points to authored match source"
        (let site = optionProgram.SourceMap[optionBranchSites.Head]
         site.SiteSpan.File = "<flow-test>" && site.SiteSpan.Column < 1000 && site.SourceKind <> "synthetic-scope")

    let resultWord =
        parseWord """word result.read(value: Result<Int, String>) -> Int {
    effects none
    match value {
        error payload => { 0 }
        ok payload => { payload }
    }
}"""
    let compiledResultWord = FlowLowering.compileWord context (WordId "user-result-read") resultWord
    let resultProgram = VerifiedIrProgram.inspect compiledResultWord.Program
    let resultCoverage = resultProgram.CoverageByWord[WordId "user-result-read"]
    equal "Result body exposes both mandatory case outcomes" [ "ok"; "error" ]
        (resultCoverage.BranchOutcomes |> Map.toList |> List.collect snd)

    let effectfulMatch = FlowLowering.compileExpression context (parseExpression "match option::none<Int>() { some item => { side-effect() } none => { 0 } }")
    let detached = VerifiedIrBody.inspect effectfulMatch.Body
    equal "effects from every case are preflighted conservatively" [ "console.write" ] (IrEffects.names detached.BodyInferredEffects)
    let seenPreflight = ResizeArray<Set<IrEffect>>()
    let invoked = ResizeArray<string>()
    let preflightHost =
        { host invoked with PreflightEffects = fun effects _ _ -> seenPreflight.Add effects }
    equal "unselected effectful case does not execute" [ IntValue 0L ] (IrInterpreter.executeBody preflightHost "effectful-match" effectfulMatch.Body)
    equal "unselected case effects are still checked before execution" [ Set.singleton IrEffect.ConsoleWrite ] (List.ofSeq seenPreflight)
    equal "unselected effectful case produces no effect" [] (List.ofSeq invoked)

    let effectUnderdeclared =
        parseWord """word effect-underdeclared(value: Option<Int>) -> Int {
    effects none
    match value {
        some item => { side-effect() }
        none => { 0 }
    }
}"""
    expectLanguageError "unused match arm effects cannot be omitted from the declaration" "EFFECT_UNDECLARED" (fun () ->
        FlowLowering.checkWord context effectUnderdeclared |> ignore)

    let wrongEmailPayload = parseExpression "option::some<Email>(\"not nominal\")"
    expectLanguageError "constructor rejects the base type where its nominal payload is required" "FLOW_CONSTRUCTOR_PAYLOAD_TYPE" (fun () ->
        FlowLowering.checkExpression context wrongEmailPayload |> ignore)
    let unknownType = parseExpression "result::error<Int, Missing>(\"bad\")"
    expectLanguageError "nested/closed constructor types must refer to declared nominal types" "FLOW_CONSTRUCTOR_UNKNOWN_TYPE" (fun () ->
        FlowLowering.checkExpression context unknownType |> ignore)
    let openType =
        FlowExpression.Container(
            FlowContainerConstructor.OptionNone,
            [ { Type = TVar "T"; Span = sourceSpan } ],
            None,
            sourceSpan)
    expectLanguageError "host-created open type arguments are rejected before lowering" "FLOW_CONSTRUCTOR_OPEN_TYPE" (fun () ->
        FlowLowering.checkExpression context openType |> ignore)
    expectLanguageError "Option match rejects a non-Option scrutinee" "FLOW_MATCH_REQUIRES_OPTION" (fun () ->
        FlowLowering.checkExpression context (parseExpression "match 1 { some item => { item } none => { 0 } }") |> ignore)
    expectLanguageError "Result match rejects a non-Result scrutinee" "FLOW_MATCH_REQUIRES_RESULT" (fun () ->
        FlowLowering.checkExpression context (parseExpression "match 1 { ok item => { item } error problem => { 0 } }") |> ignore)
    let shadowWord = parseWord """word shadow(value: Option<Int>) -> Int {
    effects none
    match value { some value => { value } none => { 0 } }
}"""
    expectLanguageError "case payload cannot shadow an outer binding" "FLOW_MATCH_PAYLOAD_SHADOW" (fun () ->
        FlowLowering.checkWord context shadowWord |> ignore)
    let noEscapeWord = parseWord """word no-escape(value: Option<Int>) -> Int {
    effects none
    let selected = match value { some hidden => { hidden } none => { 0 } };
    hidden
}"""
    expectLanguageError "case payload does not escape its match" "FLOW_UNKNOWN_LOCAL" (fun () ->
        FlowLowering.checkWord context noEscapeWord |> ignore)
    expectLanguageError "case outputs must agree exactly" "FLOW_MATCH_BRANCH_TYPE" (fun () ->
        FlowLowering.checkExpression context (parseExpression "match option::none<Int>() { some item => { item } none => { \"none\" } }") |> ignore)
    expectLanguageError "each case must produce one value" "FLOW_MATCH_BRANCH_VALUE" (fun () ->
        FlowLowering.checkExpression context (parseExpression "match option::none<Int>() { some item => { let ignored = item; } none => { 0 } }") |> ignore)

let private testStaticListCallbacks () =
    let increment = wordEntry "math.increment" [ TInt ] [ TInt ] Set.empty [ Push(LInt 1L, sourceSpan); Call("add", sourceSpan) ]
    let isTwo = wordEntry "math.is-two?" [ TInt ] [ TBool ] Set.empty [ Push(LInt 2L, sourceSpan); Call("equals", sourceSpan) ]
    let emit =
        wordEntry "effects.emit" [ TInt ] [ TUnit ] (Set.singleton "console.write")
            [ Call("drop", sourceSpan); Push(LString "item", sourceSpan); Call("console.write", sourceSpan) ]
    let customerMap =
        wordEntry "customer.map" [ TNamed "Customer"; TInt ] [ TString ] Set.empty
            [ Call("drop", sourceSpan); Call("drop", sourceSpan); Push(LString "ordinary-stage", sourceSpan) ]
    let sameShortInt = wordEntry "one.select" [ TInt ] [ TInt ] Set.empty [ Call("int.abs", sourceSpan) ]
    let sameShortBool = wordEntry "two.select" [ TInt ] [ TBool ] Set.empty [ Push(LInt 2L, sourceSpan); Call("equals", sourceSpan) ]
    let sameShortOtherInput =
        wordEntry "three.select" [ TString ] [ TBool ] Set.empty
            [ Call("string.length", sourceSpan); Push(LInt 0L, sourceSpan); Call("equals", sourceSpan) ]
    let multiOutput = wordEntry "bad.multi" [ TInt ] [ TInt; TInt ] Set.empty [ Call("dup", sourceSpan) ]
    let twoInputs = wordEntry "bad.two-inputs" [ TInt; TInt ] [ TInt ] Set.empty [ Call("add", sourceSpan) ]
    let stringInput = wordEntry "bad.string-input" [ TString ] [ TInt ] Set.empty [ Call("string.length", sourceSpan) ]
    let context = richTypeContext [ increment; isTwo; emit; customerMap; sameShortInt; sameShortBool; sameShortOtherInput; multiOutput; twoInputs; stringInput ]
    let compile source = FlowLowering.compileExpression context (parseExpression source)
    let evaluate source = IrInterpreter.executeBody (host (ResizeArray())) source (compile source).Body

    for source in
        [ "list::empty<Int>().map(math::increment)"
          "list::empty<Int>().map(word increment)"
          "list::empty<Int>().filter(math::is-two?)"
          "list::empty<Int>().each(effects::emit)" ] do
        let canonical = source |> parseExpression |> FlowSource.renderExpression
        equal ("static callback source round-trips: " + source) canonical (canonical |> parseExpression |> FlowSource.renderExpression)

    match parseExpression "1.map(value)" with
    | FlowExpression.DotCall(_, "map", [ FlowArgument.Positional(FlowExpression.Local("value", _)) ], _) ->
        check "ordinary map(value) remains a value argument, not a callback reference" true
    | other -> failwithf "Expected an ordinary local argument, got %A" other
    match parseExpression "list::map(1)" with
    | FlowExpression.Call("list.map", [ FlowArgument.Positional(FlowExpression.Literal(LInt 1L, _)) ], _) ->
        check "qualified namespace calls remain ordinary calls" true
    | other -> failwithf "Expected a qualified ordinary call, got %A" other
    match FlowParser.parseExpression "<callback-named>" "list::empty<Int>().map(callback = math::increment)" with
    | Error diagnostic -> equal "named arguments cannot smuggle a static callback reference" "FLOW_CALLBACK_NAMED_REFERENCE" diagnostic.Code
    | Ok _ -> failwith "Expected named callback reference syntax to be rejected."

    let mapEmpty = compile "list::empty<Int>().map(math::increment)"
    let mapEmptyCoverage = (VerifiedIrBody.inspect mapEmpty.Body).BodyCoverage.BranchOutcomes |> Map.toList |> List.collect snd |> Set.ofList
    equal "map exposes empty and nonempty coverage outcomes" (Set.ofList [ "empty"; "nonempty" ]) mapEmptyCoverage
    equal "map empty input keeps its static output element type" [ ListValue(TInt, []) ] (IrInterpreter.executeBody (host (ResizeArray())) "map-empty" mapEmpty.Body)
    equal "map invokes the statically named callback for each item"
        [ ListValue(TInt, [ IntValue 2L; IntValue 3L ]) ] (evaluate "list::append(list::singleton<Int>(1), 2).map(math::increment)")
    let mapSource = parseExpression "list::singleton<Int>(1).map(math::increment)"
    let callbackSpan =
        match mapSource with
        | FlowExpression.DotCall(_, "map", [ FlowArgument.WordReference reference ], _) -> reference.Span
        | _ -> failwith "Expected static map callback syntax."
    let mapBody = compile (FlowSource.renderExpression mapSource) |> fun compiled -> VerifiedIrBody.inspect compiled.Body
    let mapSites = mapBody.BodySourceMap |> Map.toList |> List.choose (fun (_, site) -> if site.SourceKind = "list-map" then Some site else None)
    equal "map lowers to one direct list operation with its authored callback span" 1 mapSites.Length
    equal "map callback operation source origin is the callback reference" callbackSpan mapSites.Head.SiteSpan
    match mapBody.BodyBlock.Code |> List.tryPick (fun instruction -> match instruction.Operation with | IrOperation.ListMap(call, _, _) -> Some call | _ -> None) with
    | Some call ->
        equal "callback dependency resolves to its stable dictionary identity" (UserWordTarget(WordId "user-math.increment", 1)) call.ResolvedTarget
    | None -> failwith "Expected verified ListMap operation in the detached body."

    let filterEmpty = compile "list::empty<Int>().filter(math::is-two?)"
    let filterCoverage = (VerifiedIrBody.inspect filterEmpty.Body).BodyCoverage.BranchOutcomes |> Map.toList |> List.collect snd |> Set.ofList
    equal "filter exposes empty, nonempty, keep, and drop outcomes" (Set.ofList [ "empty"; "nonempty"; "keep"; "drop" ]) filterCoverage
    equal "filter handles an empty list" [ ListValue(TInt, []) ] (IrInterpreter.executeBody (host (ResizeArray())) "filter-empty" filterEmpty.Body)
    equal "filter keeps and drops according to callback results" [ ListValue(TInt, [ IntValue 2L ]) ]
        (evaluate "list::append(list::singleton<Int>(1), 2).filter(math::is-two?)")
    equal "each handles an empty list" [ UnitValue ] (evaluate "list::empty<Int>().each(effects::emit)")
    let eachEvents = ResizeArray<string>()
    let eachExpression = compile "list::append(list::singleton<Int>(1), 2).each(effects::emit)"
    let eachCoverage = (VerifiedIrBody.inspect eachExpression.Body).BodyCoverage.BranchOutcomes |> Map.toList |> List.collect snd |> Set.ofList
    equal "each exposes empty and nonempty outcomes" (Set.ofList [ "empty"; "nonempty" ]) eachCoverage
    equal "each returns Unit and calls the callback for every item" [ UnitValue ] (IrInterpreter.executeBody (host eachEvents) "each-many" eachExpression.Body)
    equal "each callback effects run once per item" [ "item"; "item" ] (List.ofSeq eachEvents)

    let effectEmpty = compile "list::empty<Int>().each(effects::emit)"
    let effectBody = VerifiedIrBody.inspect effectEmpty.Body
    equal "static callback effects are inferred even for an empty list" (Set.singleton IrEffect.ConsoleWrite) effectBody.BodyInferredEffects
    let deniedEvents = ResizeArray<string>()
    let preflightChecks = ResizeArray<Set<IrEffect>>()
    let deniedHost =
        { host deniedEvents with
            PreflightEffects = fun effects _ _ ->
                preflightChecks.Add effects
                if effects.Contains IrEffect.ConsoleWrite then
                    raise (LanguageException { Code = "CAPABILITY_DENIED"; Message = "denied by test provider"; Word = None; Span = None; Expected = []; Actual = [] }) }
    expectLanguageError "callback capability is checked before empty-list iteration" "CAPABILITY_DENIED" (fun () ->
        IrInterpreter.executeBody deniedHost "empty-each-denied" effectEmpty.Body |> ignore)
    equal "empty callback preflight includes the denied effect" [ Set.singleton IrEffect.ConsoleWrite ] (List.ofSeq preflightChecks)
    equal "empty callback denial invokes no provider" [] (List.ofSeq deniedEvents)

    let ambiguousMap = parseExpression "list::empty<Int>().map(word select)"
    expectLanguageError "short callback lookup rejects identity ambiguity before output filtering" "FLOW_AMBIGUOUS_CALLBACK" (fun () ->
        FlowLowering.checkExpression context ambiguousMap |> ignore)
    let ambiguousFilter = parseExpression "list::empty<Int>().filter(word select)"
    expectLanguageError "filter cannot select an overload by its required Bool output" "FLOW_AMBIGUOUS_CALLBACK" (fun () ->
        FlowLowering.checkExpression context ambiguousFilter |> ignore)
    equal "a qualified Bool callback resolves directly for filter" [ ListValue(TInt, [ IntValue 2L ]) ]
        (evaluate "list::append(list::singleton<Int>(1), 2).filter(two::select)")
    expectLanguageError "callback input types must accept the list element" "FLOW_CALLBACK_INPUT_TYPE" (fun () ->
        FlowLowering.checkExpression context (parseExpression "list::empty<Int>().map(bad::string-input)") |> ignore)
    expectLanguageError "callbacks must have one output" "FLOW_CALLBACK_OUTPUT_ARITY" (fun () ->
        FlowLowering.checkExpression context (parseExpression "list::empty<Int>().map(bad::multi)") |> ignore)
    expectLanguageError "callbacks must have one input" "FLOW_CALLBACK_INPUT_ARITY" (fun () ->
        FlowLowering.checkExpression context (parseExpression "list::empty<Int>().map(bad::two-inputs)") |> ignore)
    expectLanguageError "filter callback result must be Bool" "FLOW_CALLBACK_RESULT_TYPE" (fun () ->
        FlowLowering.checkExpression context (parseExpression "list::empty<Int>().filter(one::select)") |> ignore)
    expectLanguageError "each callback result must be Unit" "FLOW_CALLBACK_RESULT_TYPE" (fun () ->
        FlowLowering.checkExpression context (parseExpression "list::empty<Int>().each(math::increment)") |> ignore)
    expectLanguageError "explicit callback references require a List receiver" "FLOW_CALLBACK_REQUIRES_LIST" (fun () ->
        FlowLowering.checkExpression context (parseExpression "5.map(math::increment)") |> ignore)

    equal "ordinary Customer.map(value) keeps normal strict dot resolution" [ StringValue "ordinary-stage" ]
        (evaluate "customer::new(Email::new(\"a@b\")).map(7)")
    expectLanguageError "record constructor aliases are not callback word identities" "FLOW_UNKNOWN_CALLBACK" (fun () ->
        FlowLowering.checkExpression context (parseExpression "list::empty<Email>().map(word Email)") |> ignore)
    expectLanguageError "List<Email> does not implicitly coerce to String for a callback" "FLOW_CALLBACK_INPUT_TYPE" (fun () ->
        FlowLowering.checkExpression context (parseExpression "list::empty<Email>().map(string::length)") |> ignore)
    equal "a nominal accessor can map List<Email> to List<String>" [ ListValue(TString, [ StringValue "a@b" ]) ]
        (evaluate "list::singleton<Email>(Email::new(\"a@b\")).map(Email::value)")

    let undeclaredEmptyCallback =
        parseWord "word empty-callback() -> Unit {\n    effects none\n    list::empty<Int>().each(effects::emit)\n}"
    expectLanguageError "effectful callback effects are checked even in an empty-list word" "EFFECT_UNDECLARED" (fun () ->
        FlowLowering.checkWord context undeclaredEmptyCallback |> ignore)
    let declaredEmptyCallback =
        parseWord "word empty-callback() -> Unit {\n    effects console.write\n    list::empty<Int>().each(effects::emit)\n}"
    let _, checkedEmptyCallback = FlowLowering.checkWord context declaredEmptyCallback
    check "empty-list callback dependency remains visible on the checked word" (checkedEmptyCallback.Dependencies.Contains "effects.emit")
    let compiledEmptyCallback = FlowLowering.compileWord context (WordId "user-empty-callback") declaredEmptyCallback
    let verifiedEmptyCallback = (VerifiedIrProgram.inspect compiledEmptyCallback.Program).FunctionsById[WordId "user-empty-callback"]
    equal "empty-list callback effects remain declared and inferred on the authored word"
        (Set.singleton IrEffect.ConsoleWrite) verifiedEmptyCallback.FunctionInferredEffects
    match verifiedEmptyCallback.FunctionBody.Code |> List.tryPick (fun instruction -> match instruction.Operation with | IrOperation.ListEach(call, _) -> Some call | _ -> None) with
    | Some call ->
        equal "empty-list callback target remains a static dependency" "effects.emit" call.ResolvedName
        let hasGeneratedCallbackHelper =
            (VerifiedIrProgram.inspect compiledEmptyCallback.Program).FunctionsById
            |> Map.exists (fun _ fn -> fn.FunctionName.StartsWith("$flow$", StringComparison.Ordinal))
        equal "no generated callback helper words are introduced" false hasGeneratedCallbackHelper
    | None -> failwith "Expected a direct verified ListEach operation."

    match FlowParser.parseExpression "<callback-eof>" "list::empty<Int>().map(word abs, 1" with
    | Error diagnostic -> equal "mixed callback reference EOF has a structured arity diagnostic" "FLOW_CALLBACK_ARGUMENT_ARITY" diagnostic.Code
    | Ok _ -> failwith "Expected an incomplete mixed callback argument to be rejected."

    match FlowParser.parseExpression "<callback-closure>" "list::empty<Int>().map(value => value)" with
    | Error diagnostic -> check "inline callback closures fail with a structured parser diagnostic" (not (String.IsNullOrWhiteSpace diagnostic.Code))
    | Ok _ -> failwith "Inline callback closures are not supported."
    for source in
        [ "list::empty<Int>().map(math::increment"
          "list::empty<Int>().map(word increment" ] do
        match FlowParser.parseExpression "<callback-incomplete>" source with
        | Error diagnostic -> equal ("callback prefix remains incomplete: " + source) "FLOW_INCOMPLETE_INPUT" diagnostic.Code
        | Ok _ -> failwithf "Expected incomplete callback syntax to remain incomplete: %s" source

    let shortReference = { Name = "increment"; IsExplicitShort = true; Span = sourceSpan }
    let callWithReference = FlowExpression.Call("math.increment", [ FlowArgument.WordReference shortReference ], sourceSpan)
    expectLanguageError "direct AST word references in ordinary calls are rejected" "FLOW_CALLBACK_REFERENCE_CONTEXT" (fun () ->
        FlowLowering.checkExpression context callWithReference |> ignore)
    let nonCallbackDot =
        FlowExpression.DotCall(FlowExpression.Literal(LInt 2L, sourceSpan), "abs", [ FlowArgument.WordReference shortReference ], sourceSpan)
    expectLanguageError "direct AST word references on ordinary dot stages are rejected" "FLOW_CALLBACK_REFERENCE_CONTEXT" (fun () ->
        FlowLowering.checkExpression context nonCallbackDot |> ignore)
    let malformedReference = { shortReference with Name = "math::increment" }
    let malformed = FlowExpression.DotCall(FlowExpression.Container(FlowContainerConstructor.ListEmpty, [ { Type = TInt; Span = sourceSpan } ], None, sourceSpan), "map", [ FlowArgument.WordReference malformedReference ], sourceSpan)
    expectLanguageError "host AST references must follow the explicit canonical shape" "FLOW_CALLBACK_REFERENCE_SHAPE" (fun () ->
        FlowLowering.checkExpression context malformed |> ignore)
    let emptyReference = { shortReference with Name = "" }
    let malformedEmpty = FlowExpression.DotCall(FlowExpression.Container(FlowContainerConstructor.ListEmpty, [ { Type = TInt; Span = sourceSpan } ], None, sourceSpan), "map", [ FlowArgument.WordReference emptyReference ], sourceSpan)
    expectLanguageError "host AST references reject empty names without throwing" "FLOW_CALLBACK_REFERENCE_SHAPE" (fun () ->
        FlowLowering.checkExpression context malformedEmpty |> ignore)

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

let private testFlowOutputVectors () =
    let context = loweringContext [] Map.empty
    let splitSource =
        """word vector.split(value: Int) -> (Int, String) {
    effects none
    return (value, "label")
}"""
    let splitWord = parseWord splitSource
    equal "multi-output signature preserves its declared order" [ TInt; TString ] splitWord.Outputs
    let splitCanonical = FlowSource.renderWord splitWord
    equal "multi-output signature and Return round-trip deterministically" splitCanonical (splitCanonical |> parseWord |> FlowSource.renderWord)
    let compiledSplit = FlowLowering.compileWord context (WordId "user-vector-split") splitWord

    let invoke (compiled: FlowLowering.CompiledWord) body =
        Compiler.compileIrBodyAgainstProgramWithSourceOrigins
            compiled.Context.CompilerContext
            compiled.Program
            "invoke-flow-vector"
            []
            body
            compiled.Context.SourceOrigins

    let reversed =
        parseWord
            """word vector.reversed(value: Int) -> (String, Int) {
    effects none
    let (number, label) = vector::split(value)
    return (label, number)
}"""
    let reversedBindings =
        reversed.Body
        |> List.pick (function
            | FlowStatement.LetMany(bindings, _, _) -> Some bindings
            | _ -> None)
    let compiledReversed = FlowLowering.compileWord compiledSplit.Context (WordId "user-vector-reversed") reversed
    let reversedResult =
        [ Push(LInt 7L, sourceSpan); Call("vector.reversed", sourceSpan) ]
        |> invoke compiledReversed
        |> IrInterpreter.executeBody (host (ResizeArray())) "vector-reversed"
    equal "destructuring and explicit Return preserve and reorder output positions"
        [ StringValue "label"; IntValue 7L ] reversedResult
    for _, nameSpan in reversedBindings do
        check "generated destructuring store retains its authored name span" (compiledReversed.SiteOrigins |> Map.exists (fun _ origin -> origin = nameSpan))
    let returnSpans =
        reversed.Body
        |> List.pick (function
            | FlowStatement.Return(values, _) ->
                values
                |> List.map (function
                    | FlowExpression.Literal(_, source) | FlowExpression.Local(_, source) -> source
                    | FlowExpression.Call(_, _, source) | FlowExpression.DotCall(_, _, _, source)
                    | FlowExpression.If(_, _, _, source) | FlowExpression.Container(_, _, _, source)
                    | FlowExpression.MatchOption(_, _, _, source) | FlowExpression.MatchResult(_, _, _, source) -> source)
                |> Some
            | _ -> None)
    for memberSpan in returnSpans do
        check "each Return member retains an authored source site" (compiledReversed.SiteOrigins |> Map.exists (fun _ origin -> origin = memberSpan))
    check "Flow output source sites contain no private marker coordinates"
        (compiledReversed.SiteOrigins |> Map.forall (fun _ origin -> origin.Column < 1000))

    let genericDuplicate =
        parseWord
            """word vector.generic-duplicate(value: Int) -> (Int, Int) {
    effects none
    let (left, right) = dup(value)
    return (right, left)
}"""
    let compiledGeneric = FlowLowering.compileWord compiledReversed.Context (WordId "user-vector-generic") genericDuplicate
    equal "generic primitive output substitution is preserved"
        [ IntValue 12L; IntValue 12L ]
        ([ Push(LInt 12L, sourceSpan); Call("vector.generic-duplicate", sourceSpan) ]
         |> invoke compiledGeneric
         |> IrInterpreter.executeBody (host (ResizeArray())) "vector-generic-duplicate")

    let afterBranch =
        parseWord
            """word vector.after-branch(value: Int) -> (Int, String) {
    effects none
    let (number, label) = if equals(value, 0) {
        return (value, "zero")
    } else {
        return (value, "other")
    };
    let incremented = add(number, 1)
    return (incremented, label)
}"""
    let compiledAfterBranch = FlowLowering.compileWord compiledGeneric.Context (WordId "user-vector-after-branch") afterBranch
    let runAfterBranch value =
        [ Push(LInt value, sourceSpan); Call("vector.after-branch", sourceSpan) ]
        |> invoke compiledAfterBranch
        |> IrInterpreter.executeBody (host (ResizeArray())) "vector-after-branch"
    equal "If Return vectors bind before the enclosing block continues" [ IntValue 1L; StringValue "zero" ] (runAfterBranch 0L)
    equal "the other If vector arm also continues with its bindings" [ IntValue 6L; StringValue "other" ] (runAfterBranch 5L)
    let afterBranchCoverage = (VerifiedIrProgram.inspect compiledAfterBranch.Program).CoverageByWord[WordId "user-vector-after-branch"]
    let ifOutcomes =
        afterBranchCoverage.BranchOutcomes
        |> Map.toList |> List.collect snd |> Set.ofList
    equal "vector If keeps both existing coverage outcomes" (Set.ofList [ "true"; "false" ]) ifOutcomes

    let optionPair =
        parseWord
            """word vector.option-pair(value: Option<Int>) -> (Int, String) {
    effects none
    let (number, label) = match value {
        some item => { return (item, "some") }
        none => { return (0, "none") }
    };
    return (number, label)
}"""
    let compiledOption = FlowLowering.compileWord compiledAfterBranch.Context (WordId "user-vector-option") optionPair
    equal "Option match accepts equal output vectors"
        [ IntValue 9L; StringValue "some" ]
        ([ Push(LInt 9L, sourceSpan); ConstructContainer(OptionSome, [ TInt ], sourceSpan); Call("vector.option-pair", sourceSpan) ]
         |> invoke compiledOption
         |> IrInterpreter.executeBody (host (ResizeArray())) "vector-option-some")
    equal "Option none arm returns its declared vector"
        [ IntValue 0L; StringValue "none" ]
        ([ ConstructContainer(OptionNone, [ TInt ], sourceSpan); Call("vector.option-pair", sourceSpan) ]
         |> invoke compiledOption
         |> IrInterpreter.executeBody (host (ResizeArray())) "vector-option-none")
    let optionCoverage = (VerifiedIrProgram.inspect compiledOption.Program).CoverageByWord[WordId "user-vector-option"]
    let optionOutcomes =
        optionCoverage.BranchOutcomes
        |> Map.toList |> List.collect snd |> Set.ofList
    check "vector Option match retains both coverage outcomes" (Set.isSubset (Set.ofList [ "some"; "none" ]) optionOutcomes)

    let resultPair =
        parseWord
            """word vector.result-pair(value: Result<Int, String>) -> (Int, String) {
    effects none
    let (number, label) = match value {
        ok item => { return (item, "ok") }
        error problem => { return (0, problem) }
    };
    return (number, label)
}"""
    let compiledResult = FlowLowering.compileWord compiledOption.Context (WordId "user-vector-result") resultPair
    equal "Result ok arm returns its declared vector"
        [ IntValue 8L; StringValue "ok" ]
        ([ Push(LInt 8L, sourceSpan); ConstructContainer(ResultOk, [ TInt; TString ], sourceSpan); Call("vector.result-pair", sourceSpan) ]
         |> invoke compiledResult
         |> IrInterpreter.executeBody (host (ResizeArray())) "vector-result-ok")
    equal "Result error arm returns its declared vector"
        [ IntValue 0L; StringValue "failed" ]
        ([ Push(LString "failed", sourceSpan); ConstructContainer(ResultError, [ TInt; TString ], sourceSpan); Call("vector.result-pair", sourceSpan) ]
         |> invoke compiledResult
         |> IrInterpreter.executeBody (host (ResizeArray())) "vector-result-error")
    let resultCoverage = (VerifiedIrProgram.inspect compiledResult.Program).CoverageByWord[WordId "user-vector-result"]
    let resultOutcomes =
        resultCoverage.BranchOutcomes
        |> Map.toList |> List.collect snd |> Set.ofList
    check "vector Result match retains both coverage outcomes" (Set.isSubset (Set.ofList [ "ok"; "error" ]) resultOutcomes)

    let effectfulPair =
        parseWord
            """word vector.effectful(value: Int) -> (Int, String) {
    effects console.write
    console::write("once")
    return (value, "done")
}"""
    let compiledEffectful = FlowLowering.compileWord compiledResult.Context (WordId "user-vector-effectful") effectfulPair
    let effectConsumer =
        parseWord
            """word vector.effect-consumer(value: Int) -> (Int, String) {
    effects console.write
    let (number, label) = vector::effectful(value)
    return (number, label)
}"""
    let compiledEffectConsumer = FlowLowering.compileWord compiledEffectful.Context (WordId "user-vector-effect-consumer") effectConsumer
    let effectEvents = ResizeArray<string>()
    equal "effectful multi-output initializer yields every declared value"
        [ IntValue 14L; StringValue "done" ]
        ([ Push(LInt 14L, sourceSpan); Call("vector.effect-consumer", sourceSpan) ]
         |> invoke compiledEffectConsumer
         |> IrInterpreter.executeBody (host effectEvents) "vector-effect-consumer")
    equal "effectful multi-output call executes exactly once" [ "once" ] (List.ofSeq effectEvents)

    let scalarReturn =
        parseWord
            """word vector.scalar-return(value: Int) -> Int {
    effects none
    return value
}"""
    equal "scalar output declarations remain one-element vectors" [ TInt ] scalarReturn.Outputs
    let scalarCanonical = FlowSource.renderWord scalarReturn
    equal "unparenthesized scalar Return canonicalizes and round-trips" scalarCanonical (scalarCanonical |> parseWord |> FlowSource.renderWord)
    let scalarCompiled = FlowLowering.compileWord compiledEffectConsumer.Context (WordId "user-vector-scalar") scalarReturn
    equal "scalar Return executes as the legacy single result"
        [ IntValue 3L ]
        ([ Push(LInt 3L, sourceSpan); Call("vector.scalar-return", sourceSpan) ]
         |> invoke scalarCompiled
         |> IrInterpreter.executeBody (host (ResizeArray())) "vector-scalar-return")

    let scalarContext = scalarCompiled.Context
    let isolatedVector = parseExpression "dup(1)"
    expectLanguageError "isolated Flow lowering requires exactly one result" "FLOW_CALL_OUTPUT_ARITY" (fun () ->
        FlowLowering.lowerExpression scalarContext isolatedVector |> ignore)
    expectLanguageError "isolated Flow checking rejects vector output" "FLOW_CALL_OUTPUT_ARITY" (fun () ->
        FlowLowering.checkExpression scalarContext isolatedVector |> ignore)
    expectLanguageError "isolated Flow compilation rejects vector output" "FLOW_CALL_OUTPUT_ARITY" (fun () ->
        FlowLowering.compileExpression scalarContext isolatedVector |> ignore)
    let scalarLetOfVector =
        parseWord
            """word vector.bad-scalar-let(value: Int) -> Int {
    effects none
    let only = dup(value)
    only
}"""
    expectLanguageError "ordinary let is a scalar context" "FLOW_CALL_OUTPUT_ARITY" (fun () ->
        FlowLowering.lowerWord scalarContext scalarLetOfVector |> ignore)
    let partialDestructure =
        parseWord
            """word vector.partial(value: Int) -> Int {
    effects none
    let (only) = dup(value)
    only
}"""
    expectLanguageError "destructuring must bind every output" "FLOW_DESTRUCTURE_ARITY" (fun () ->
        FlowLowering.lowerWord scalarContext partialDestructure |> ignore)
    let vectorInArgument =
        parseWord
            """word vector.scalar-argument(value: Int) -> Int {
    effects none
    add(vector::split(value), 1)
}"""
    expectLanguageError "multi-output call is rejected in an ordinary argument" "FLOW_CALL_OUTPUT_ARITY" (fun () ->
        FlowLowering.lowerWord scalarContext vectorInArgument |> ignore)
    let vectorAsReceiver =
        parseWord
            """word vector.scalar-receiver(value: Int) -> Int {
    effects none
    vector::split(value).abs()
}"""
    expectLanguageError "multi-output call is rejected as a dot receiver" "FLOW_CALL_OUTPUT_ARITY" (fun () ->
        FlowLowering.lowerWord scalarContext vectorAsReceiver |> ignore)
    let vectorAsPayload =
        parseWord
            """word vector.scalar-payload(value: Int) -> List<Int> {
    effects none
    list::singleton<Int>(vector::split(value))
}"""
    expectLanguageError "multi-output call is rejected as a container payload" "FLOW_CALL_OUTPUT_ARITY" (fun () ->
        FlowLowering.lowerWord scalarContext vectorAsPayload |> ignore)

    let vectorCandidate = wordEntry "second.lookup" [ TInt ] [ TInt; TString ] Set.empty [ Push(LString "label", sourceSpan) ]
    let scalarCandidate = wordEntry "first.lookup" [ TInt ] [ TInt ] Set.empty [ Call("int.abs", sourceSpan) ]
    let ambiguousContext = loweringContext [ scalarCandidate; vectorCandidate ] Map.empty
    expectLanguageError "short-name ambiguity is resolved before scalar output arity filtering" "FLOW_AMBIGUOUS_CALL" (fun () ->
        FlowLowering.checkExpression ambiguousContext (parseExpression "lookup(1)") |> ignore)

    let badIfArity =
        parseWord
            """word vector.bad-if-arity(value: Int) -> Int {
    effects none
    let (left, right) = if true { return (1, 2) } else { return (1) }
    left
}"""
    expectLanguageError "If arms must return vectors with equal arity" "FLOW_IF_BRANCH_TYPE" (fun () ->
        FlowLowering.lowerWord scalarContext badIfArity |> ignore)
    let badIfOrder =
        parseWord
            """word vector.bad-if-order(value: Int) -> Int {
    effects none
    let (left, right) = if true { return (1, "first") } else { return ("second", 1) }
    left
}"""
    expectLanguageError "If arm output positions must have equal types" "FLOW_IF_BRANCH_TYPE" (fun () ->
        FlowLowering.lowerWord scalarContext badIfOrder |> ignore)
    let badOptionOrder =
        parseWord
            """word vector.bad-option(value: Option<Int>) -> Int {
    effects none
    let (left, right) = match value { some item => { return (item, "some") } none => { return ("none", 0) } }
    left
}"""
    expectLanguageError "Option case output positions must have equal types" "FLOW_MATCH_BRANCH_TYPE" (fun () ->
        FlowLowering.lowerWord scalarContext badOptionOrder |> ignore)
    let badResultOrder =
        parseWord
            """word vector.bad-result(value: Result<Int, String>) -> Int {
    effects none
    let (left, right) = match value { ok item => { return (item, "ok") } error problem => { return (problem, 0) } }
    left
}"""
    expectLanguageError "Result case output positions must have equal types" "FLOW_MATCH_BRANCH_TYPE" (fun () ->
        FlowLowering.lowerWord scalarContext badResultOrder |> ignore)

    let nominalContext = richTypeContext []
    let nominalWord =
        parseWord
            """word vector.nominal(value: Email) -> (Email, String) {
    effects none
    return (value, Email::value(value))
}"""
    let compiledNominal = FlowLowering.compileWord nominalContext (WordId "user-vector-nominal") nominalWord
    equal "strong nominal output positions survive vector compilation"
        [ NamedValue("Email", StringValue "a@b"); StringValue "a@b" ]
        ([ Push(LString "a@b", sourceSpan); Call("Email.new", sourceSpan); Call("vector.nominal", sourceSpan) ]
         |> invoke compiledNominal
         |> IrInterpreter.executeBody (host (ResizeArray())) "vector-nominal")
    let badNominalOrder =
        parseWord
            """word vector.bad-nominal(value: Email) -> (Email, String) {
    effects none
    let (left, right) = if true { return (value, "label") } else { return ("label", value) }
    return (left, right)
}"""
    expectLanguageError "nominal and base types cannot exchange vector positions" "FLOW_IF_BRANCH_TYPE" (fun () ->
        FlowLowering.lowerWord compiledNominal.Context badNominalOrder |> ignore)

    for source, code in
        [ ("""word vector.empty-signature() -> () {
    effects none
    unit
}""", "FLOW_OUTPUT_VECTOR_EMPTY");
          ("""word vector.empty-pattern(value: Int) -> Int {
    effects none
    let () = value
    value
}""", "FLOW_DESTRUCTURE_EMPTY");
          ("""word vector.underscore(value: Int) -> Int {
    effects none
    let (_, named) = dup(value)
    named
}""", "FLOW_DESTRUCTURE_NAME");
          ("""word vector.duplicate-pattern(value: Int) -> Int {
    effects none
    let (same, same) = dup(value)
    same
}""", "FLOW_DESTRUCTURE_DUPLICATE");
          ("""word vector.empty-return() -> Unit {
    effects none
    return ()
}""", "FLOW_RETURN_EMPTY");
          ("""word vector.trailing-return() -> Int {
    effects none
    return (1)
    1
}""", "FLOW_RETURN_NOT_TERMINAL");
          ("""word vector.nested-trailing-return() -> Int {
    effects none
    if true { return (1); 1 } else { 1 }
}""", "FLOW_RETURN_NOT_TERMINAL") ] do
        expectError ("vector parser rejection " + code) code (FlowParser.parseWord "<invalid-vector>" source) |> ignore

    let literal = FlowExpression.Literal(LInt 1L, sourceSpan)
    let badNestedReturn =
        FlowExpression.If(
            FlowExpression.Literal(LBool true, sourceSpan),
            [ FlowStatement.Return([ literal ], sourceSpan); FlowStatement.Evaluate literal ],
            [ FlowStatement.Evaluate literal ],
            sourceSpan)
    let invalidNestedWord = { scalarReturn with Body = [ FlowStatement.Evaluate badNestedReturn ] }
    for name, action in
        [ "nested nonterminal Return renderer", fun () -> FlowSource.renderWord invalidNestedWord |> ignore
          "nested nonterminal Return lowerer", fun () -> FlowLowering.lowerWord scalarContext invalidNestedWord |> ignore ] do
        expectLanguageError name "FLOW_RETURN_NOT_TERMINAL" action
    let emptyReturnWord = { scalarReturn with Body = [ FlowStatement.Return([], sourceSpan) ] }
    expectLanguageError "empty host Return is rejected before render" "FLOW_RETURN_EMPTY" (fun () -> FlowSource.renderWord emptyReturnWord |> ignore)
    expectLanguageError "empty host Return is rejected before lowering" "FLOW_RETURN_EMPTY" (fun () -> FlowLowering.lowerWord scalarContext emptyReturnWord |> ignore)
    let emptyOutputWord = { scalarReturn with Outputs = [] }
    expectLanguageError "empty host output declaration is rejected before render" "FLOW_OUTPUT_VECTOR_EMPTY" (fun () -> FlowSource.renderWord emptyOutputWord |> ignore)
    expectLanguageError "empty host output declaration is rejected before lowering" "FLOW_OUTPUT_VECTOR_EMPTY" (fun () -> FlowLowering.lowerWord scalarContext emptyOutputWord |> ignore)
    let badPatternWord =
        { scalarReturn with
            Body = [ FlowStatement.LetMany([ "_", sourceSpan; "named", sourceSpan ], literal, sourceSpan); FlowStatement.Evaluate literal ] }
    expectLanguageError "host-built discard patterns fail closed before render" "FLOW_DESTRUCTURE_NAME" (fun () -> FlowSource.renderWord badPatternWord |> ignore)
    expectLanguageError "host-built discard patterns fail closed before lowering" "FLOW_DESTRUCTURE_NAME" (fun () -> FlowLowering.lowerWord scalarContext badPatternWord |> ignore)
    let duplicatePatternWord =
        { scalarReturn with
            Body = [ FlowStatement.LetMany([ "same", sourceSpan; "same", sourceSpan ], literal, sourceSpan); FlowStatement.Evaluate literal ] }
    expectLanguageError "host-built duplicate patterns fail closed before render" "FLOW_DESTRUCTURE_DUPLICATE" (fun () -> FlowSource.renderWord duplicatePatternWord |> ignore)
    expectLanguageError "host-built duplicate patterns fail closed before lowering" "FLOW_DESTRUCTURE_DUPLICATE" (fun () -> FlowLowering.lowerWord scalarContext duplicatePatternWord |> ignore)

    let repeated = List.replicate 50001 literal
    let returnBudgetExpression =
        FlowExpression.If(
            FlowExpression.Literal(LBool true, sourceSpan),
            [ FlowStatement.Return(repeated, sourceSpan) ],
            [ FlowStatement.Return(repeated, sourceSpan) ],
            sourceSpan)
    expectLanguageError "all vector Return members share one expanded-node budget" "FLOW_STRUCTURE_LIMIT" (fun () ->
        FlowSource.renderExpression returnBudgetExpression |> ignore)
    expectLanguageError "lowering shares the vector Return member budget" "FLOW_STRUCTURE_LIMIT" (fun () ->
        FlowLowering.lowerExpression scalarContext returnBudgetExpression |> ignore)

    let overLimit = FlowStructure.maxExpandedNodes + 1
    let wideOutputsWord = { scalarReturn with Outputs = List.replicate overLimit TInt }
    expectLanguageError "rendering streams output roots under the shared budget" "FLOW_STRUCTURE_LIMIT" (fun () ->
        FlowSource.renderWord wideOutputsWord |> ignore)
    expectLanguageError "lowering streams output roots under the shared budget" "FLOW_STRUCTURE_LIMIT" (fun () ->
        FlowLowering.lowerWord scalarContext wideOutputsWord |> ignore)

    let wideBindings = List.replicate overLimit ("duplicate", sourceSpan)
    let wideBindingsWord =
        { scalarReturn with
            Body = [ FlowStatement.LetMany(wideBindings, literal, sourceSpan); FlowStatement.Evaluate literal ] }
    expectLanguageError "rendering charges binding members before scanning names" "FLOW_STRUCTURE_LIMIT" (fun () ->
        FlowSource.renderWord wideBindingsWord |> ignore)
    expectLanguageError "lowering charges binding members before scanning names" "FLOW_STRUCTURE_LIMIT" (fun () ->
        FlowLowering.lowerWord scalarContext wideBindingsWord |> ignore)

    let wideBlockWord = { scalarReturn with Body = List.replicate overLimit (FlowStatement.Evaluate literal) }
    expectLanguageError "rendering streams wide statement blocks under the shared budget" "FLOW_STRUCTURE_LIMIT" (fun () ->
        FlowSource.renderWord wideBlockWord |> ignore)
    expectLanguageError "lowering streams wide statement blocks under the shared budget" "FLOW_STRUCTURE_LIMIT" (fun () ->
        FlowLowering.lowerWord scalarContext wideBlockWord |> ignore)

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
    testIterativeAstDepthLimit ()
    testSparseFlowSourceMarkerAllocation ()
    testFlowSourceCanonicalRoundTrip ()
    testContainerAndMatchSyntaxRoundTrip ()
    testContainerAndMatchDiagnostics ()
    testStaticListCallbacks ()
    testLoweringAndExecution ()
    testContainerAndMatchLowering ()
    testFlowOutputVectors ()
    testFlowDiagnostics ()
    printfn "Flow tests passed: %d assertions" assertions
    0
