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
    testIterativeAstDepthLimit ()
    testSparseFlowSourceMarkerAllocation ()
    testFlowSourceCanonicalRoundTrip ()
    testContainerAndMatchSyntaxRoundTrip ()
    testContainerAndMatchDiagnostics ()
    testLoweringAndExecution ()
    testContainerAndMatchLowering ()
    testFlowDiagnostics ()
    printfn "Flow tests passed: %d assertions" assertions
    0
