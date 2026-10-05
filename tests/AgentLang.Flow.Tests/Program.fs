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

let private parseTest source =
    match FlowParser.parseTest "<flow-test>" source with
    | Ok test -> test
    | Error diagnostic -> failwith (Diagnostics.render diagnostic)

let private parseExample source =
    match FlowParser.parseExample "<flow-test>" source with
    | Ok example -> example
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

    let rootCallSource = "::identity(1)"
    match parseExpression rootCallSource with
    | FlowExpression.RootCall(target, _, callSpan) ->
        equal "absolute-root call retains exact dictionary key" "identity" target.Name
        equal "absolute-root target span includes the prefix and name" (span "<flow-test>" 1 1 "::identity".Length) target.Span
        equal "absolute-root call span includes the arguments" (span "<flow-test>" 1 1 rootCallSource.Length) callSpan
        equal "absolute-root call rendering is canonical" rootCallSource (FlowSource.renderExpression (parseExpression (FlowSource.renderExpression (parseExpression rootCallSource))))
    | other -> failwithf "Expected absolute-root call AST, got %A" other

    let rootCallbackSource = "items.map(::identity)"
    match parseExpression rootCallbackSource with
    | FlowExpression.DotCall(_, "map", [ FlowArgument.WordReference reference ], _) ->
        equal "absolute-root callback preserves its qualifier" FlowWordReferenceQualification.AbsoluteRoot reference.Qualification
        equal "absolute-root callback retains the exact key" "identity" reference.Name
        equal "absolute-root callback span includes prefix and key" (span "<flow-test>" 1 11 "::identity".Length) reference.Span
        equal "root callback source round-trips" rootCallbackSource (rootCallbackSource |> parseExpression |> FlowSource.renderExpression |> parseExpression |> FlowSource.renderExpression)
    | other -> failwithf "Expected an absolute-root callback AST, got %A" other

    expectError "absolute root refuses a namespace suffix" "FLOW_ROOT_TARGET_QUALIFIED"
        (FlowParser.parseExpression "<root-qualified>" "::namespace::identity(1)") |> ignore
    expectError "absolute root must be called" "FLOW_ROOT_CALL_REQUIRES_ARGUMENTS"
        (FlowParser.parseExpression "<root-not-call>" "::identity") |> ignore

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

    let shortReference = { Name = "increment"; Qualification = FlowWordReferenceQualification.ExplicitShort; Span = sourceSpan }
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

    let malformedRootReference =
        { shortReference with Name = "math.increment"; Qualification = FlowWordReferenceQualification.AbsoluteRoot }
    let malformedRootCallback =
        FlowExpression.DotCall(
            FlowExpression.Container(FlowContainerConstructor.ListEmpty, [ { Type = TInt; Span = sourceSpan } ], None, sourceSpan),
            "map",
            [ FlowArgument.WordReference malformedRootReference ],
            sourceSpan)
    expectLanguageError "host AST root callbacks cannot smuggle a dotted namespace key" "FLOW_CALLBACK_REFERENCE_SHAPE" (fun () ->
        FlowSource.renderExpression malformedRootCallback |> ignore)
    expectLanguageError "lowering rejects malformed host-built root callback references" "FLOW_CALLBACK_REFERENCE_SHAPE" (fun () ->
        FlowLowering.checkExpression context malformedRootCallback |> ignore)

    let malformedRootCall =
        FlowExpression.RootCall({ Name = "namespace.increment"; Span = sourceSpan }, [], sourceSpan)
    expectLanguageError "host AST root calls reject namespace-shaped keys before rendering" "FLOW_ROOT_TARGET_INVALID" (fun () ->
        FlowSource.renderExpression malformedRootCall |> ignore)
    expectLanguageError "lowering rejects namespace-shaped host root calls" "FLOW_ROOT_TARGET_INVALID" (fun () ->
        FlowLowering.checkExpression context malformedRootCall |> ignore)
    let shortRootSpan = { Name = "increment"; Span = sourceSpan }
    let malformedRootCallSpan = FlowExpression.RootCall(shortRootSpan, [], sourceSpan)
    expectLanguageError "host AST root call spans must cover the :: prefix" "FLOW_ROOT_TARGET_SPAN_INVALID" (fun () ->
        FlowSource.renderExpression malformedRootCallSpan |> ignore)

let private testAbsoluteRootAddressing () =
    let rootIdentity = wordEntry "identity" [ TInt ] [ TInt ] Set.empty []
    let namespacedIdentity = wordEntry "one.identity" [ TInt ] [ TBool ] Set.empty [ Call("drop", sourceSpan); Push(LBool true, sourceSpan) ]
    let rootPair = wordEntry "pair" [ TInt; TInt ] [ TInt ] Set.empty [ Call("add", sourceSpan) ]
    let namespacedPair = wordEntry "branch.pair" [ TInt; TInt ] [ TBool ] Set.empty [ Call("drop", sourceSpan); Call("drop", sourceSpan); Push(LBool true, sourceSpan) ]
    let hiddenNamespaceWord = wordEntry "there.hidden" [ TInt ] [ TInt ] Set.empty []
    let rootMulti = wordEntry "multi-root" [ TInt ] [ TInt; TInt ] Set.empty [ Call("dup", sourceSpan) ]
    let rootString = wordEntry "string-root" [ TString ] [ TString ] Set.empty []
    let rootTwoInputs = wordEntry "two-input-root" [ TInt; TInt ] [ TInt ] Set.empty [ Call("add", sourceSpan) ]
    let effect = Set.singleton "console.write"
    let emitValue name text value =
        wordEntry name [] [ TInt ] effect
            [ Push(LString text, sourceSpan); Call("console.write", sourceSpan); Call("drop", sourceSpan); Push(LInt value, sourceSpan) ]
    let rootEmitter =
        wordEntry "emit-root" [ TInt ] [ TUnit ] effect
            [ Call("drop", sourceSpan); Push(LString "root-emitter", sourceSpan); Call("console.write", sourceSpan) ]
    let namespacedEmitter =
        wordEntry "effect.emit-root" [ TInt ] [ TUnit ] Set.empty
            [ Call("drop", sourceSpan); Push(LUnit, sourceSpan) ]
    let parameters = Map.ofList [ "pair", [ "first"; "second" ]; "branch.pair", [ "first"; "second" ] ]
    let makeValue = wordEntry "make-value" [] [ TInt ] Set.empty [ Push(LInt -7L, sourceSpan) ]
    let context =
        loweringContext
            [ rootIdentity; namespacedIdentity; rootPair; namespacedPair; hiddenNamespaceWord; rootMulti; rootString; rootTwoInputs
              emitValue "effect.left-value" "left" 10L; emitValue "effect.right-value" "right" 20L
              rootEmitter; namespacedEmitter; makeValue ]
            parameters
    let compile source = FlowLowering.compileExpression context (parseExpression source)
    let evaluate source events = IrInterpreter.executeBody (host events) source (compile source).Body
    let resolvedCalls (body: IrExecutableBody) =
        body.BodyBlock.Code
        |> List.choose (fun instruction ->
            match instruction.Operation with
            | IrOperation.Call call -> Some call.ResolvedTarget
            | _ -> None)

    let exactRoot = compile "::identity(4)"
    equal "absolute-root call resolves the unqualified dictionary entry exactly"
        [ UserWordTarget(WordId "user-identity", 1) ]
        (resolvedCalls (VerifiedIrBody.inspect exactRoot.Body))
    equal "absolute-root identity call executes its exact target" [ IntValue 4L ]
        (IrInterpreter.executeBody (host (ResizeArray())) "root-identity" exactRoot.Body)
    let namespaced = compile "one::identity(4)"
    equal "namespace qualification selects the namespace dictionary entry"
        [ UserWordTarget(WordId "user-one.identity", 1) ]
        (resolvedCalls (VerifiedIrBody.inspect namespaced.Body))
    equal "namespace identity keeps its distinct result type and behavior" [ BoolValue true ]
        (IrInterpreter.executeBody (host (ResizeArray())) "namespace-identity" namespaced.Body)
    expectLanguageError "ordinary short calls remain ambiguous between root and namespace keys" "FLOW_AMBIGUOUS_CALL" (fun () ->
        FlowLowering.checkExpression context (parseExpression "identity(4)") |> ignore)
    expectLanguageError "explicit short callback references remain ambiguous across root and namespace" "FLOW_AMBIGUOUS_CALLBACK" (fun () ->
        FlowLowering.checkExpression context (parseExpression "list::empty<Int>().map(word identity)") |> ignore)

    let rootCallback = compile "list::singleton<Int>(4).map(::identity)"
    equal "absolute-root callback executes the exact root identity" [ ListValue(TInt, [ IntValue 4L ]) ]
        (IrInterpreter.executeBody (host (ResizeArray())) "root-callback" rootCallback.Body)
    match (VerifiedIrBody.inspect rootCallback.Body).BodyBlock.Code |> List.tryPick (function | { Operation = IrOperation.ListMap(call, _, _) } -> Some call | _ -> None) with
    | Some call ->
        equal "absolute-root callback IR retains the root target identity" (UserWordTarget(WordId "user-identity", 1)) call.ResolvedTarget
        equal "absolute-root callback IR retains the exact resolved name" "identity" call.ResolvedName
    | None -> failwith "Expected root-qualified callback to lower as a direct ListMap operation."
    expectLanguageError "root callback signature is checked after exact identity selection" "FLOW_CALLBACK_RESULT_TYPE" (fun () ->
        FlowLowering.checkExpression context (parseExpression "list::empty<Int>().filter(::identity)") |> ignore)
    expectLanguageError "root callbacks retain the one-output contract" "FLOW_CALLBACK_OUTPUT_ARITY" (fun () ->
        FlowLowering.checkExpression context (parseExpression "list::empty<Int>().map(::multi-root)") |> ignore)
    expectLanguageError "root callback input types are checked against the exact target" "FLOW_CALLBACK_INPUT_TYPE" (fun () ->
        FlowLowering.checkExpression context (parseExpression "list::empty<Int>().map(::string-root)") |> ignore)
    expectLanguageError "root callbacks retain the one-input contract" "FLOW_CALLBACK_INPUT_ARITY" (fun () ->
        FlowLowering.checkExpression context (parseExpression "list::empty<Int>().map(::two-input-root)") |> ignore)
    expectLanguageError "multi-output root calls remain invalid in scalar expression position" "FLOW_CALL_OUTPUT_ARITY" (fun () ->
        FlowLowering.checkExpression context (parseExpression "::multi-root(1)") |> ignore)
    equal "namespace-qualified Bool callback still satisfies filter"
        [ ListValue(TInt, [ IntValue 3L ]) ]
        (IrInterpreter.executeBody (host (ResizeArray())) "namespace-filter"
            (compile "list::singleton<Int>(3).filter(one::identity)").Body)
    expectLanguageError "root calls do not fall back to a same-suffix namespaced candidate" "FLOW_UNKNOWN_ROOT_CALL" (fun () ->
        FlowLowering.checkExpression context (parseExpression "::hidden(1)") |> ignore)
    expectLanguageError "root callback references do not fall back to a same-suffix namespaced candidate" "FLOW_UNKNOWN_CALLBACK" (fun () ->
        FlowLowering.checkExpression context (parseExpression "list::empty<Int>().map(::hidden)") |> ignore)

    let rootPairSource = "::pair(second = effect::right-value(), first = effect::left-value())"
    let rootPairExpression = parseExpression rootPairSource
    match rootPairExpression with
    | FlowExpression.RootCall(target, [ FlowArgument.Named("second", _, _); FlowArgument.Named("first", _, _) ], callSpan) ->
        equal "root call preserves the root target source span" (span "<flow-test>" 1 1 "::pair".Length) target.Span
        equal "root call preserves its full named-argument call span" (span "<flow-test>" 1 1 rootPairSource.Length) callSpan
    | other -> failwithf "Expected a root call with named arguments, got %A" other
    let orderedEvents = ResizeArray<string>()
    equal "root named-argument expressions are evaluated in written order" [ IntValue 30L ] (evaluate rootPairSource orderedEvents)
    equal "root named-argument effects occur in written order" [ "right"; "left" ] (List.ofSeq orderedEvents)
    equal "absolute-root call bypasses suffix overload selection"
        [ UserWordTarget(WordId "user-pair", 1) ]
        (resolvedCalls (VerifiedIrBody.inspect (compile "::pair(1, 2)").Body))
    expectLanguageError "ordinary short pair calls remain ambiguous before output filtering" "FLOW_AMBIGUOUS_CALL" (fun () ->
        FlowLowering.checkExpression context (parseExpression "pair(1, 2)") |> ignore)

    match parseExpression "::make-value().abs()" with
    | FlowExpression.DotCall(FlowExpression.RootCall({ Name = "make-value" }, _, _), "abs", _, _) ->
        equal "root calls compose as dot-stage receivers" [ IntValue 7L ]
            (IrInterpreter.executeBody (host (ResizeArray())) "root-receiver-chain"
                (compile "::make-value().abs()" |> fun expression -> expression.Body))
    | other -> failwithf "Expected a root call as the dot receiver, got %A" other

    let rootEmitEvents = ResizeArray<string>()
    let rootEach = compile "list::singleton<Int>(1).each(::emit-root)"
    let rootEachBody = VerifiedIrBody.inspect rootEach.Body
    equal "absolute-root callback effects remain explicit in verified IR" (Set.singleton IrEffect.ConsoleWrite) rootEachBody.BodyInferredEffects
    equal "root-qualified callback call executes" [ UnitValue ] (IrInterpreter.executeBody (host rootEmitEvents) "root-each" rootEach.Body)
    equal "exact root callback identity invokes its provider" [ "root-emitter" ] (List.ofSeq rootEmitEvents)
    match rootEachBody.BodyBlock.Code |> List.tryPick (function | { Operation = IrOperation.ListEach(call, _) } -> Some call | _ -> None) with
    | Some call -> equal "effectful root callback operation targets exact root identity" (UserWordTarget(WordId "user-emit-root", 1)) call.ResolvedTarget
    | None -> failwith "Expected root-qualified callback to lower as a direct ListEach operation."

    let namespacedEach = compile "list::singleton<Int>(1).each(effect::emit-root)"
    let namespacedEachBody = VerifiedIrBody.inspect namespacedEach.Body
    equal "namespace-qualified same-suffix callback selects its pure signature" Set.empty namespacedEachBody.BodyInferredEffects
    match namespacedEachBody.BodyBlock.Code |> List.tryPick (function | { Operation = IrOperation.ListEach(call, _) } -> Some call | _ -> None) with
    | Some call -> equal "namespace-qualified callback retains its own target identity" (UserWordTarget(WordId "user-effect.emit-root", 1)) call.ResolvedTarget
    | None -> failwith "Expected namespace-qualified callback to lower as a direct ListEach operation."

    let emptyRootEach = compile "list::empty<Int>().each(::emit-root)"
    equal "empty list keeps the exact root callback effect in its type metadata"
        (Set.singleton IrEffect.ConsoleWrite) (VerifiedIrBody.inspect emptyRootEach.Body).BodyInferredEffects
    let deniedPreflight = ResizeArray<Set<IrEffect>>()
    let deniedProviders = ResizeArray<string>()
    let mutable instructionStartedBeforeDeniedPreflight = false
    let deniedRootHost =
        { host deniedProviders with
            PreflightEffects = fun effects _ _ ->
                deniedPreflight.Add effects
                if effects.Contains IrEffect.ConsoleWrite then
                    raise (LanguageException { Code = "CAPABILITY_DENIED"; Message = "root callback denied before execution"; Word = None; Span = None; Expected = []; Actual = [] })
            ChargeInstruction = fun _ _ ->
                if deniedPreflight.Count = 0 then instructionStartedBeforeDeniedPreflight <- true }
    expectLanguageError "exact root callback cannot escape a denied effect via a pure same-suffix word" "CAPABILITY_DENIED" (fun () ->
        IrInterpreter.executeBody deniedRootHost "denied-empty-root-each" emptyRootEach.Body |> ignore)
    equal "root callback capability preflight receives its exact effect" [ Set.singleton IrEffect.ConsoleWrite ] (List.ofSeq deniedPreflight)
    equal "root callback denial occurs before any instruction executes" false instructionStartedBeforeDeniedPreflight
    equal "empty root callback denial invokes no provider" [] (List.ofSeq deniedProviders)

    let listSource = "word root_receiver(value: Int) -> Int {\n    effects none\n    let list = list::singleton<Int>(value);\n    list.map(::identity).first()\n}"
    let lintWord = parseWord listSource
    check "Flow lint sees local reads nested inside root-call receiver chains"
        (FlowLint.analyze FlowLint.defaultOptions lintWord |> Result.map List.isEmpty |> Result.defaultValue false)

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
                    | FlowExpression.Call(_, _, source) | FlowExpression.RootCall(_, _, source) | FlowExpression.DotCall(_, _, _, source)
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

let private testFlowAuthoredCases () =
    let literalSource =
        "test storefront.select/some-literal {\n"
        + "    storefront::select(option::some<Int>(7))\n"
        + "    => 7\n"
        + "}"
    let literalTest =
        match FlowParser.parseTest "authored/storefront.flow" literalSource with
        | Ok value -> value
        | Error diagnostic -> failwith (Diagnostics.render diagnostic)
    equal "Flow test owner and case name stay separate" "storefront.select" literalTest.Word
    equal "Flow test retains its case name" "some-literal" literalTest.CaseName
    equal "Flow test preserves exact authored source" literalSource literalTest.SourceText
    equal "Flow test syntax version is explicit" 1 literalTest.SyntaxVersion
    equal "Flow test header has its authored span" (span "authored/storefront.flow" 1 1 "test storefront.select/some-literal".Length) literalTest.HeaderSpan
    equal "Flow test expectation marker has an authored span" (span "authored/storefront.flow" 3 5 2) literalTest.ExpectationSpan
    match literalTest.Expected with
    | FlowTestExpectation.Literal(LInt 7L, valueSpan) ->
        equal "literal expectation value span is retained" (span "authored/storefront.flow" 3 8 1) valueSpan
    | other -> failwithf "Expected literal Flow test, got %A" other
    let literalCanonical = FlowSource.renderTest literalTest
    equal "Flow test render/reparse is deterministic" literalCanonical (literalCanonical |> parseTest |> FlowSource.renderTest)
    let reparsedLiteral = parseTest literalCanonical
    equal "Flow test render retains owner and case" (literalTest.Word, literalTest.CaseName) (reparsedLiteral.Word, reparsedLiteral.CaseName)

    let choiceWord =
        parseWord
            """word storefront.select(value: Option<Int>) -> Int {
    effects none
    match value {
        some item => { return item }
        none => { return 0 }
    }
}"""
    let compiledChoice = FlowLowering.compileWord (loweringContext [] Map.empty) (WordId "user-storefront-select") choiceWord
    let compiledLiteral = FlowLowering.compileTest compiledChoice.Context compiledChoice.Program literalTest
    equal "literal Flow test lowers to the exact legacy test definition"
        (literalSource, 1, "storefront.select", "some-literal")
        (compiledLiteral.Lowered.Definition.SourceText, compiledLiteral.Lowered.SyntaxVersion,
         compiledLiteral.Lowered.Definition.Word, compiledLiteral.Lowered.Definition.Name)
    equal "literal Flow test executes its actual body" [ IntValue 7L ]
        (IrInterpreter.executeBody (host (ResizeArray())) "flow-literal-test" compiledLiteral.Body)
    check "literal expectation does not create an executable expectation body" compiledLiteral.ExpectationBody.IsNone
    check "test projection retains header and expectation marker spans"
        (compiledLiteral.Lowered.Projection.AuthoredSpans.Contains literalTest.HeaderSpan
         && compiledLiteral.Lowered.Projection.AuthoredSpans.Contains literalTest.ExpectationSpan)
    check "test body source sites map to authored spans"
        (compiledLiteral.BodySiteOrigins |> Map.forall (fun _ source -> source.File = "authored/storefront.flow" && source.Column < 1000))

    let rootCaseWord =
        parseWord
            """word root_case_identity(value: Int) -> Int {
    effects none
    value
}"""
    let compiledRootCaseWord = FlowLowering.compileWord (loweringContext [] Map.empty) (WordId "user-root_case_identity") rootCaseWord
    let rootTestSource =
        "test root_case_identity/root-addressed {\n"
        + "    ::root_case_identity(7)\n"
        + "    => value ::root_case_identity(7)\n"
        + "}"
    let rootTest = parseTest rootTestSource
    let canonicalRootTest = FlowSource.renderTest rootTest
    equal "Flow test attachment renders root calls in actual and expected bodies"
        canonicalRootTest
        (canonicalRootTest |> parseTest |> FlowSource.renderTest)
    let compiledRootTest = FlowLowering.compileTest compiledRootCaseWord.Context compiledRootCaseWord.Program rootTest
    equal "Flow test attachment preserves its exact root-qualified actual result" [ IntValue 7L ]
        (IrInterpreter.executeBody (host (ResizeArray())) "root-test-actual" compiledRootTest.Body)
    equal "Flow test attachment preserves its exact root-qualified expected result" [ IntValue 7L ]
        (compiledRootTest.ExpectationBody
         |> Option.defaultWith (fun () -> failwith "Expected the root-qualified value expectation body.")
         |> IrInterpreter.executeBody (host (ResizeArray())) "root-test-expected")
    equal "root-call Flow test attachment retains authored source bytes" rootTestSource compiledRootTest.Lowered.Definition.SourceText
    check "root-call test attachment retains target source spans in both traces"
        (compiledRootTest.BodySiteOrigins |> Map.exists (fun _ source -> source.Line = 2 && source.Column = 5)
         && (compiledRootTest.ExpectationSiteOrigins |> Option.exists (Map.exists (fun _ source -> source.Line = 3 && source.Column = 14))))

    let rootExampleSource =
        "example root_case_identity/root-literal {\n"
        + "    ::root_case_identity(9)\n"
        + "    => 9\n"
        + "}"
    let rootExample = parseExample rootExampleSource
    equal "Flow example attachment renders root calls canonically"
        (FlowSource.renderExample rootExample)
        (rootExampleSource |> parseExample |> FlowSource.renderExample)
    let compiledRootExample = FlowLowering.compileExample compiledRootCaseWord.Context compiledRootCaseWord.Program rootExample
    equal "Flow example attachment executes its root call" [ IntValue 9L ]
        (IrInterpreter.executeBody (host (ResizeArray())) "root-example-actual" compiledRootExample.Body)
    equal "root-call Flow example attachment retains authored source bytes" rootExampleSource compiledRootExample.Lowered.Definition.SourceText

    let valueSource =
        "test storefront.select/value-expression {\n"
        + "    storefront::select(option::some<Int>(7))\n"
        + "    => value add(3, 4)\n"
        + "}"
    let valueTest = parseTest valueSource
    match valueTest.Expected with
    | FlowTestExpectation.Expression(FlowExpression.Call("add", _, expressionSpan)) ->
        equal "value expectation records its expression source span" (span "<flow-test>" 3 14 "add(3, 4)".Length) expressionSpan
    | other -> failwithf "Expected expression Flow test, got %A" other
    let valueCanonical = FlowSource.renderTest valueTest
    equal "value-expression test has deterministic canonical source" valueCanonical (valueCanonical |> parseTest |> FlowSource.renderTest)
    let compiledValue = FlowLowering.compileTest compiledChoice.Context compiledChoice.Program valueTest
    let expectationBody = compiledValue.ExpectationBody |> Option.defaultWith (fun () -> failwith "Expected a separately compiled value expectation body.")
    equal "value Flow test actual body executes" [ IntValue 7L ]
        (IrInterpreter.executeBody (host (ResizeArray())) "flow-value-test-body" compiledValue.Body)
    equal "value Flow test expression executes independently" [ IntValue 7L ]
        (IrInterpreter.executeBody (host (ResizeArray())) "flow-value-test-expectation" expectationBody)
    check "value expectation source sites map to its authored expression"
        (compiledValue.ExpectationSiteOrigins |> Option.exists (Map.exists (fun _ source -> source.File = "<flow-test>" && source.Line = 3 && source.Column < 1000)))
    let contextMarkers = compiledChoice.Context.SourceOrigins |> Map.toSeq |> Seq.map fst |> Set.ofSeq
    let caseMarkers = compiledValue.Lowered.Projection.SyntheticOrigins |> Map.toSeq |> Seq.map fst |> Set.ofSeq
    check "test actual and expected lowerings use markers disjoint from prior Flow words" (Set.isEmpty (Set.intersect contextMarkers caseMarkers))

    let nominalWord =
        parseWord
            """word storefront.email(value: Email) -> Email {
    effects none
    Email::new(Email::value(value))
}"""
    let compiledEmail = FlowLowering.compileWord (richTypeContext []) (WordId "user-storefront-email") nominalWord
    let emailLabelWord =
        parseWord
            """word storefront.email-label(value: Email) -> String {
    effects none
    string::concat(Email::value(value), "!")
}"""
    let compiledEmailLabel = FlowLowering.compileWord compiledEmail.Context (WordId "user-storefront-email-label") emailLabelWord
    let emailTest =
        parseTest
            """test storefront.email/refined-value {
    if true {
        storefront::email(Email::new("a@b"))
    } else {
        storefront::email(Email::new("c@d"))
    }
    => value if true {
        storefront::email(Email::new("a@b"))
    } else {
        storefront::email(Email::new("c@d"))
    }
}"""
    let compiledEmailTest = FlowLowering.compileTest compiledEmail.Context compiledEmail.Program emailTest
    equal "Flow attachment actual body preserves the Email refinement"
        [ NamedValue("Email", StringValue "a@b") ]
        (IrInterpreter.executeBody (host (ResizeArray())) "flow-email-actual" compiledEmailTest.Body)
    equal "pure Flow attachment expectation can produce the same refined Email"
        [ NamedValue("Email", StringValue "a@b") ]
        (compiledEmailTest.ExpectationBody
         |> Option.defaultWith (fun () -> failwith "Expected a typed Email value expectation.")
         |> IrInterpreter.executeBody (host (ResizeArray())) "flow-email-expected")
    let emailWithStringLiteral = parseTest "test storefront.email/string-literal {\n    storefront::email(Email::new(\"a@b\"))\n    => \"a@b\"\n}"
    expectLanguageError "an Email test body rejects an underlying String literal expectation" "TEST_EXPECTED_STACK" (fun () ->
        FlowLowering.lowerTest compiledEmail.Context emailWithStringLiteral |> ignore)
    let emailWithStringExpression =
        parseTest
            """test storefront.email/string-expression {
    storefront::email(Email::new("a@b"))
    => value Email::value(Email::new("a@b"))
}"""
    expectLanguageError "an Email test body rejects an expression unwrapping to String" "TEST_EXPECTED_STACK" (fun () ->
        FlowLowering.lowerTest compiledEmail.Context emailWithStringExpression |> ignore)

    let markerIndex (marker: SourceSpan) = int64 Int32.MaxValue - int64 marker.Column
    let sparseOriginsBase = compiledEmailLabel.Context.SourceOrigins
    let shiftMarker marker =
        if marker.Length = 0 && marker.Column > 0 then
            let index = markerIndex marker
            let sparseIndex = index * 3L + 11L
            { marker with Column = int (int64 Int32.MaxValue - sparseIndex) }
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
        compiledEmailLabel.Context.CompilerContext.Words
        |> Map.map (fun _ entry ->
            { entry with
                Definition = { entry.Definition with Body = List.map shiftExpression entry.Definition.Body } })
    let sparseOrigins =
        compiledEmailLabel.Context.SourceOrigins
        |> Map.toList
        |> List.map (fun (marker, origin) -> shiftMarker marker, origin)
        |> Map.ofList
    let sparseEmailContext =
        { compiledEmailLabel.Context with
            CompilerContext = { compiledEmailLabel.Context.CompilerContext with Words = shiftedWords }
            SourceOrigins = sparseOrigins }
    let retainedSparseIndices = sparseOrigins |> Map.toSeq |> Seq.map (fst >> markerIndex) |> Seq.sort |> Seq.toList
    check "the attachment compiler fixture has gaps between retained source markers"
        (retainedSparseIndices.Length > 1
         && (retainedSparseIndices |> List.pairwise |> List.forall (fun (left, right) -> right - left > 1L)))
    let sparseProgram = Compiler.compileIrProgramWithSourceOrigins sparseEmailContext.CompilerContext sparseEmailContext.SourceOrigins
    let sparseEmailTest = FlowLowering.compileTest sparseEmailContext sparseProgram emailTest
    let sparseAttachmentMarkers = sparseEmailTest.Lowered.Projection.SyntheticOrigins
    let sparseAttachmentIndices = sparseAttachmentMarkers |> Map.toSeq |> Seq.map (fst >> markerIndex) |> Seq.toList
    let sparseRetainedMax = sparseOrigins |> Map.toSeq |> Seq.map (fst >> markerIndex) |> Seq.max
    check "Flow attachment markers allocate beyond the maximum sparse retained marker"
        (not sparseAttachmentIndices.IsEmpty && List.forall (fun marker -> marker > sparseRetainedMax) sparseAttachmentIndices)
    let actualAttachmentMarkers =
        sparseAttachmentMarkers
        |> Map.toSeq
        |> Seq.choose (fun (marker, origin) -> if origin.Line >= 2 && origin.Line <= 6 then Some marker else None)
        |> Set.ofSeq
    let expectedAttachmentMarkers =
        sparseAttachmentMarkers
        |> Map.toSeq
        |> Seq.choose (fun (marker, origin) -> if origin.Line >= 7 && origin.Line <= 11 then Some marker else None)
        |> Set.ofSeq
    check "Flow attachment actual and expected expressions receive disjoint markers"
        (not (Set.isEmpty actualAttachmentMarkers)
         && not (Set.isEmpty expectedAttachmentMarkers)
         && Set.isEmpty (Set.intersect actualAttachmentMarkers expectedAttachmentMarkers))
    check "compiled Flow attachment is bound to the same sparse-origin program snapshot"
        (Object.ReferenceEquals(sparseEmailTest.Program, sparseProgram))
    check "Flow attachment marker projection keeps exact authored source spans"
        (sparseAttachmentMarkers |> Map.forall (fun _ origin -> origin.File = "<flow-test>" && origin.Column < 1000)
         && (sparseEmailTest.BodySiteOrigins |> Map.forall (fun _ origin -> origin.File = "<flow-test>" && origin.Line >= 2 && origin.Line <= 6))
         && (sparseEmailTest.ExpectationSiteOrigins
             |> Option.exists (Map.forall (fun _ origin -> origin.File = "<flow-test>" && origin.Line >= 7 && origin.Line <= 11))))

    let errorWord =
        parseWord
            """word storefront.divide(value: Int) -> Int {
    effects none
    divide(value, 0)
}"""
    let compiledErrorWord = FlowLowering.compileWord compiledChoice.Context (WordId "user-storefront-divide") errorWord
    let errorSource =
        "test storefront.divide/divide-by-zero {\n"
        + "    storefront::divide(3)\n"
        + "    => error RUNTIME_DIVIDE_BY_ZERO\n"
        + "}"
    let errorTest = parseTest errorSource
    match errorTest.Expected with
    | FlowTestExpectation.RuntimeError(code, codeSpan) ->
        equal "runtime-error expectation retains its code" "RUNTIME_DIVIDE_BY_ZERO" code
        equal "runtime-error expectation retains the code span" (span "<flow-test>" 3 14 "RUNTIME_DIVIDE_BY_ZERO".Length) codeSpan
    | other -> failwithf "Expected runtime-error Flow test, got %A" other
    let compiledErrorTest = FlowLowering.compileTest compiledErrorWord.Context compiledErrorWord.Program errorTest
    let errorResult =
        try
            IrInterpreter.executeBody (host (ResizeArray())) "flow-error-test" compiledErrorTest.Body |> ignore
            None
        with LanguageException diagnostic -> Some diagnostic.Code
    equal "runtime-error Flow test executes and observes the expected diagnostic" (Some "RUNTIME_DIVIDE_BY_ZERO") errorResult

    let exampleSource =
        "example storefront.select/none-example {\n"
        + "    storefront::select(option::none<Int>())\n"
        + "    => 0\n"
        + "}"
    let example =
        match FlowParser.parseExample "authored/storefront.flow" exampleSource with
        | Ok value -> value
        | Error diagnostic -> failwith (Diagnostics.render diagnostic)
    equal "Flow example preserves exact authored source" exampleSource example.SourceText
    equal "Flow example retains the literal expectation span" (span "authored/storefront.flow" 3 8 1) example.ExpectedSpan
    let exampleCanonical = FlowSource.renderExample example
    equal "Flow example render/reparse is deterministic" exampleCanonical (exampleCanonical |> parseExample |> FlowSource.renderExample)
    let compiledExample = FlowLowering.compileExample compiledChoice.Context compiledChoice.Program example
    equal "Flow example executes through the exact verified program" [ IntValue 0L ]
        (IrInterpreter.executeBody (host (ResizeArray())) "flow-example" compiledExample.Body)
    equal "Flow example keeps its literal Core expectation" (LInt 0L) compiledExample.Lowered.Definition.Expected
    equal "Flow example keeps its source version" 1 compiledExample.Lowered.SyntaxVersion
    equal "Flow example lowering preserves the exact authored source" exampleSource compiledExample.Lowered.SourceText
    equal "Core example projection retains the same authored source bytes" exampleSource compiledExample.Lowered.Definition.SourceText

    let documentedWord =
        parseWord
            """word storefront.documented() -> String {
    effects none
    doc "A documented Flow word."
    "ready"
}"""
    let documentedCanonical = FlowSource.renderWord documentedWord
    equal "word documentation survives Flow source round-trip" documentedCanonical (documentedCanonical |> parseWord |> FlowSource.renderWord)
    equal "word documentation remains metadata" "A documented Flow word." (documentedCanonical |> parseWord).Documentation

    let nestedSource =
        """test storefront.select/nested-result {
    let result = result::ok<Int, String>(4)
    let (number, label) = match result {
        ok value => { return (value, "ok") }
        error message => { return (0, message) }
    };
    return string::concat(int::to-string(number), string::concat(":", label))
    => value "4:ok"
}"""
    let nestedTest = parseTest nestedSource
    let nestedCanonical = FlowSource.renderTest nestedTest
    equal "nested Result constructor/match/destructure source round-trips" nestedCanonical (nestedCanonical |> parseTest |> FlowSource.renderTest)
    let compiledNested = FlowLowering.compileTest compiledChoice.Context compiledChoice.Program nestedTest
    equal "nested constructor, match, destructuring, and terminal return execute" [ StringValue "4:ok" ]
        (IrInterpreter.executeBody (host (ResizeArray())) "flow-nested-test" compiledNested.Body)
    let nestedExpected = compiledNested.ExpectationBody |> Option.defaultWith (fun () -> failwith "Expected nested Flow value expectation.")
    equal "nested test expression expectation evaluates separately" [ StringValue "4:ok" ]
        (IrInterpreter.executeBody (host (ResizeArray())) "flow-nested-expected" nestedExpected)
    check "nested test contains no private source coordinates in either source map"
        (compiledNested.BodySiteOrigins
         |> Map.forall (fun _ source -> source.Column < 1000)
         && (compiledNested.ExpectationSiteOrigins |> Option.defaultValue Map.empty
             |> Map.forall (fun _ source -> source.Column < 1000)))

    let effectSource =
        "test storefront.select/separate-effects {\n"
        + "    console::write(\"actual\")\n"
        + "    unit\n"
        + "    => value unit\n"
        + "}"
    let effectTest = parseTest effectSource
    let compiledEffect = FlowLowering.compileTest compiledChoice.Context compiledChoice.Program effectTest
    let actualProvider = ResizeArray<string>()
    let expectationProvider = ResizeArray<string>()
    equal "effectful test body preserves its Unit result" [ UnitValue ]
        (IrInterpreter.executeBody (host actualProvider) "flow-effect-actual" compiledEffect.Body)
    let separateExpectation = compiledEffect.ExpectationBody |> Option.defaultWith (fun () -> failwith "Expected Unit value expectation.")
    equal "pure expected expression returns Unit" [ UnitValue ]
        (IrInterpreter.executeBody (host expectationProvider) "flow-effect-expectation" separateExpectation)
    equal "test body uses its own effect provider" [ "actual" ] (List.ofSeq actualProvider)
    equal "separate expected expression does not reuse actual effect provider" [] (List.ofSeq expectationProvider)

    let programData = VerifiedIrProgram.inspect compiledChoice.Program
    let targetId = WordId "user-storefront-select"
    let targetBranchLabels =
        programData.CoverageByWord[targetId].BranchOutcomes
        |> Map.toList |> List.collect snd |> Set.ofList
    equal "branch coverage obligation has both library-owned Option paths" (Set.ofList [ "some"; "none" ]) targetBranchLabels
    let targetBranchObligations =
        programData.CoverageByWord[targetId].BranchOutcomes
        |> Map.toList
        |> List.collect (fun (site, outcomes) -> outcomes |> List.map (fun outcome -> site, outcome))
        |> Set.ofList
    let targetSomeObligations = targetBranchObligations |> Set.filter (fun (_, outcome) -> outcome = "some")
    let targetNoneObligations = targetBranchObligations |> Set.filter (fun (_, outcome) -> outcome = "none")
    let collectLibraryBranches (rawBranches: ResizeArray<string * SourceSiteId * string>) (observedBranches: ResizeArray<SourceSiteId * string>) =
        { host (ResizeArray()) with
            RecordBranchOutcome = fun currentWord site outcome ->
                rawBranches.Add(currentWord, site, outcome)
                match programData.SourceMap.TryFind site with
                | Some source when currentWord = "storefront.select" && source.SiteOwner = Some targetId ->
                    observedBranches.Add(site, outcome)
                | _ -> () }
    let actualTraceBranches = ResizeArray<string * SourceSiteId * string>()
    let observedLibraryBranches = ResizeArray<SourceSiteId * string>()
    let coverageHost () = collectLibraryBranches actualTraceBranches observedLibraryBranches
    let fixtureSource =
        """test storefront.select/fixture-same-label {
    match option::none<Int>() {
        some value => { value }
        none => { 1 }
    }
    => 1
}"""
    let fixtureTest = FlowLowering.compileTest compiledChoice.Context compiledChoice.Program (parseTest fixtureSource)
    let fixtureBranches = ResizeArray<string * SourceSiteId * string>()
    let fixtureObservedLibraryBranches = ResizeArray<SourceSiteId * string>()
    let fixtureHost = collectLibraryBranches fixtureBranches fixtureObservedLibraryBranches
    equal "inline match fixture can pass its assertion" [ IntValue 1L ]
        (IrInterpreter.executeBody fixtureHost "flow-coverage-fixture" fixtureTest.Body)
    check "inline fixture executes a same-named none branch"
        (fixtureBranches |> Seq.exists (fun (_, _, outcome) -> outcome = "none"))
    equal "the identical target-site filter excludes an inline fixture branch" Set.empty (fixtureObservedLibraryBranches |> Set.ofSeq)

    let expectedBranchSource =
        """test storefront.select/expected-is-isolated {
    storefront::select(option::some<Int>(0))
    => value storefront::select(option::none<Int>())
}"""
    let expectedBranchTest = FlowLowering.compileTest compiledChoice.Context compiledChoice.Program (parseTest expectedBranchSource)
    equal "actual test call exercises the library some path" [ IntValue 0L ]
        (IrInterpreter.executeBody (coverageHost ()) "flow-coverage-actual" expectedBranchTest.Body)
    equal "the actual call covers the exact library-owned some obligation sites" targetSomeObligations
        (observedLibraryBranches |> Set.ofSeq)
    let expectedBranchEvents = ResizeArray<string * SourceSiteId * string>()
    let expectedObservedLibraryBranches = ResizeArray<SourceSiteId * string>()
    let expectedIsolationHost = collectLibraryBranches expectedBranchEvents expectedObservedLibraryBranches
    let isolatedExpectation = expectedBranchTest.ExpectationBody |> Option.defaultWith (fun () -> failwith "Expected match expression body.")
    equal "expected expression computes the same value through the opposite library path" [ IntValue 0L ]
        (IrInterpreter.executeBody expectedIsolationHost "flow-isolated-expectation" isolatedExpectation)
    let expectedObservedPairs = expectedObservedLibraryBranches |> Set.ofSeq
    check "expected expression takes the tested library's none branch in its isolated trace"
        (expectedObservedPairs = targetNoneObligations)
    equal "isolated expected-expression branches leave exact actual coverage unchanged" targetSomeObligations
        (observedLibraryBranches |> Set.ofSeq)

    let noneSource =
        """test storefront.select/actual-none {
    storefront::select(option::none<Int>())
    => 0
}"""
    let noneTest = FlowLowering.compileTest compiledChoice.Context compiledChoice.Program (parseTest noneSource)
    equal "actual call exercises the library none path" [ IntValue 0L ]
        (IrInterpreter.executeBody (coverageHost ()) "flow-coverage-none" noneTest.Body)
    equal "calls through the tested word cover every exact branch site/outcome pair" targetBranchObligations
        (observedLibraryBranches |> Set.ofSeq)

    let invalidSources =
        [ ("missing expectation", "test storefront.select/missing {\n    1\n}", "FLOW_EXPECTATION_REQUIRED")
          ("duplicate expectation", "test storefront.select/duplicate {\n    1\n    => 1\n    => 1\n}", "FLOW_EXPECTATION_TRAILING")
          ("malformed error code", "test storefront.select/bad-error {\n    1\n    => error bad-code\n}", "FLOW_INVALID_EXPECTED_ERROR_CODE")
          ("empty error body", "test storefront.select/empty-error {\n    => error RUNTIME_ERROR\n}", "FLOW_EXPECTATION_BODY_EMPTY")
          ("empty value-expression body", "test storefront.select/empty-value {\n    => value 1\n}", "FLOW_EXPECTATION_BODY_EMPTY")
          ("trailing expectation source", "test storefront.select/trailing {\n    1\n    => 1 extra\n}", "FLOW_EXPECTATION_TRAILING") ]
    for name, source, code in invalidSources do
        expectError ("Flow test parser rejects " + name) code (FlowParser.parseTest "<invalid-flow-test>" source) |> ignore

    for name, source, code in
        [ ("error example", "example storefront.select/error {\n    1\n    => error RUNTIME_ERROR\n}", "FLOW_EXAMPLE_EXPECTATION_KIND")
          ("expression example", "example storefront.select/value {\n    1\n    => value 1\n}", "FLOW_EXAMPLE_EXPECTATION_KIND")
          ("empty example body", "example storefront.select/empty {\n    => 1\n}", "FLOW_EXPECTATION_BODY_EMPTY")
          ("missing example expectation", "example storefront.select/missing {\n    1\n}", "FLOW_EXPECTATION_REQUIRED") ] do
        expectError ("Flow example parser rejects " + name) code (FlowParser.parseExample "<invalid-flow-example>" source) |> ignore

    let badTestSource =
        { literalTest with
            Expected = FlowTestExpectation.RuntimeError("bad-code", sourceSpan) }
    expectLanguageError "host-built invalid runtime code fails rendering" "FLOW_INVALID_EXPECTED_ERROR_CODE" (fun () -> FlowSource.renderTest badTestSource |> ignore)
    expectLanguageError "host-built invalid runtime code fails lowering" "FLOW_INVALID_EXPECTED_ERROR_CODE" (fun () -> FlowLowering.lowerTest compiledChoice.Context badTestSource |> ignore)
    let emptyErrorTest =
        { literalTest with Body = []; Expected = FlowTestExpectation.RuntimeError("RUNTIME_ERROR", sourceSpan) }
    expectLanguageError "host-built empty runtime-error test fails rendering" "FLOW_EXPECTATION_BODY_EMPTY" (fun () -> FlowSource.renderTest emptyErrorTest |> ignore)
    expectLanguageError "host-built empty runtime-error test fails lowering" "FLOW_EXPECTATION_BODY_EMPTY" (fun () -> FlowLowering.lowerTest compiledChoice.Context emptyErrorTest |> ignore)
    let invalidOwnerTest = { literalTest with Word = "storefront/select" }
    expectLanguageError "host-built noncanonical owner fails rendering" "FLOW_CASE_OWNER_NAME_INVALID" (fun () -> FlowSource.renderTest invalidOwnerTest |> ignore)
    expectLanguageError "host-built noncanonical owner fails lowering" "FLOW_CASE_OWNER_NAME_INVALID" (fun () -> FlowLowering.lowerTest compiledChoice.Context invalidOwnerTest |> ignore)
    let unsupportedCaseVersion = { literalTest with SyntaxVersion = 2 }
    expectLanguageError "host-built unsupported test version fails rendering" "FLOW_VERSION_UNSUPPORTED" (fun () -> FlowSource.renderTest unsupportedCaseVersion |> ignore)
    expectLanguageError "host-built unsupported test version fails lowering" "FLOW_VERSION_UNSUPPORTED" (fun () -> FlowLowering.lowerTest compiledChoice.Context unsupportedCaseVersion |> ignore)

    let badScalarTest =
        parseTest """test storefront.select/multi-output {
    return (1, 2)
    => 1
}"""
    expectLanguageError "test actual body must return exactly one value" "TEST_EXPECTED_STACK" (fun () -> FlowLowering.lowerTest compiledChoice.Context badScalarTest |> ignore)
    let badScalarExpectation =
        parseTest """test storefront.select/multi-expectation {
    1
    => value dup(1)
}"""
    expectLanguageError "value expectation must return exactly one value" "FLOW_CALL_OUTPUT_ARITY" (fun () -> FlowLowering.lowerTest compiledChoice.Context badScalarExpectation |> ignore)
    let badValueType = parseTest "test storefront.select/value-type {\n    unit\n    => value \"wrong\"\n}"
    let badValueTypeError = captureLanguageError "value expectation type mismatch" "TEST_EXPECTED_STACK" (fun () -> FlowLowering.lowerTest compiledChoice.Context badValueType |> ignore)
    equal "value expectation mismatch diagnostic points to authored test source" (Some badValueType.Span) badValueTypeError.Span
    let effectfulExpected = parseTest "test storefront.select/effectful-expected {\n    unit\n    => value console::write(\"denied\")\n}"
    let effectDiagnostic = captureLanguageError "value expectation rejects observable effects" "TEST_EXPECTED_VALUE_EFFECTS" (fun () -> FlowLowering.lowerTest compiledChoice.Context effectfulExpected |> ignore)
    check "effectful expectation diagnostics never expose private source coordinates"
        (effectDiagnostic.Span |> Option.forall (fun source -> source.Column < Int32.MaxValue - 1000))
    let effectfulUnselectedExpectation =
        parseTest
            """test storefront.select/unselected-expected-effect {
    unit
    => value if true { unit } else { console::write("unselected") }
}"""
    expectLanguageError "value expectation purity includes effects in an unselected branch" "TEST_EXPECTED_VALUE_EFFECTS" (fun () ->
        FlowLowering.lowerTest compiledChoice.Context effectfulUnselectedExpectation |> ignore)
    let conservativeActualEffect =
        parseTest
            """test storefront.select/unselected-actual-effect {
    if true { unit } else { console::write("unselected") }
    => value unit
}"""
    let compiledConservativeEffect = FlowLowering.compileTest compiledChoice.Context compiledChoice.Program conservativeActualEffect
    let conservativeEffectBody = VerifiedIrBody.inspect compiledConservativeEffect.Body
    equal "actual test body retains effect metadata from an unselected branch"
        (Set.singleton IrEffect.ConsoleWrite) conservativeEffectBody.BodyInferredEffects
    let actualPreflight = ResizeArray<Set<IrEffect>>()
    let actualEffectInvocations = ResizeArray<string>()
    let mutable instructionStartedBeforePreflight = false
    let preflightHost =
        { host actualEffectInvocations with
            PreflightEffects = fun effects _ _ -> actualPreflight.Add effects
            ChargeInstruction = fun _ _ ->
                if actualPreflight.Count = 0 then instructionStartedBeforePreflight <- true }
    equal "actual test takes the pure selected branch" [ UnitValue ]
        (IrInterpreter.executeBody preflightHost "flow-conservative-actual" compiledConservativeEffect.Body)
    equal "actual test effect preflight runs before any instruction" false instructionStartedBeforePreflight
    equal "unselected actual-branch effects remain in preflight" [ Set.singleton IrEffect.ConsoleWrite ] (List.ofSeq actualPreflight)
    equal "unselected actual-branch provider is not invoked" [] (List.ofSeq actualEffectInvocations)

    let outputBudgetLiteral = FlowExpression.Literal(LInt 1L, sourceSpan)
    let returnMembers = List.replicate 48000 outputBudgetLiteral
    let oversizedExpected =
        FlowExpression.If(
            FlowExpression.Literal(LBool true, sourceSpan),
            [ FlowStatement.Return(returnMembers, sourceSpan) ],
            [ FlowStatement.Return(returnMembers, sourceSpan) ],
            sourceSpan)
    let sharedBudgetTest =
        { literalTest with
            Body = List.replicate 5000 (FlowStatement.Evaluate outputBudgetLiteral)
            Expected = FlowTestExpectation.Expression oversizedExpected }
    expectLanguageError "actual and expression expectation share one expanded-node budget" "FLOW_STRUCTURE_LIMIT" (fun () ->
        FlowSource.renderTest sharedBudgetTest |> ignore)
    expectLanguageError "lowering shares the combined actual/expectation node budget" "FLOW_STRUCTURE_LIMIT" (fun () ->
        FlowLowering.lowerTest compiledChoice.Context sharedBudgetTest |> ignore)

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
    testAbsoluteRootAddressing ()
    testLoweringAndExecution ()
    testContainerAndMatchLowering ()
    testFlowOutputVectors ()
    testFlowAuthoredCases ()
    testFlowDiagnostics ()
    printfn "Flow tests passed: %d assertions" assertions
    0
