module AgentLang.Flow.Tests

open System
open AgentLang
open AgentLang.FlowLowering

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

let private authoredFlowSource (ownerId: WordId) (ownerRevision: int) (source: string) : FlowLowering.FlowSourceDocument =
    let definition =
        match FlowParser.parseWord "<flow-test>" source with
        | Ok word -> word
        | Error diagnostic -> failwith (Diagnostics.render diagnostic)
    let stored = Storage.sourceObject StorageObjectKind.WordDefinition source
    { OwnerName = definition.Name
      OwnerId = ownerId
      OwnerRevision = ownerRevision
      SyntaxVersion = definition.SyntaxVersion
      EffectsDeclared = definition.EffectsDeclared
      Reference = stored.Reference
      SourceFile = definition.Span.File
      Content = source }

let private flowInventory (context: FlowLowering.Context) (names: string list) : FlowLowering.FlowSourceInventory =
    let sources =
        names
        |> List.map (fun name ->
            let entry = context.CompilerContext.Words[name]
            authoredFlowSource context.CompilerContext.WordIds[name] entry.Revision entry.Definition.SourceText)
    { ExpectedFlowOwnerIds = sources |> List.map (fun source -> source.OwnerId) |> Set.ofList
      Sources = sources }

let private parseTest source =
    match FlowParser.parseTest "<flow-test>" source with
    | Ok test -> test
    | Error diagnostic -> failwith (Diagnostics.render diagnostic)

let private parseExample source =
    match FlowParser.parseExample "<flow-test>" source with
    | Ok example -> example
    | Error diagnostic -> failwith (Diagnostics.render diagnostic)

let private authoredFlowAttachment
    (kind: FlowLowering.FlowAttachmentKind)
    (ownerId: WordId)
    (ownerRevision: int)
    (source: string)
    : FlowLowering.FlowAttachmentSourceDocument =
    let storageKind, ownerName, caseName, sourceFile =
        match kind with
        | FlowLowering.FlowAttachmentKind.Test ->
            let test = parseTest source
            StorageObjectKind.TestDefinition, test.Word, test.CaseName, test.Span.File
        | FlowLowering.FlowAttachmentKind.Example ->
            let example = parseExample source
            StorageObjectKind.ExampleDefinition, example.Word, example.CaseName, example.Span.File
    let stored = Storage.sourceObject storageKind source
    { OwnerName = ownerName
      OwnerId = ownerId
      OwnerRevision = ownerRevision
      SyntaxVersion = 1
      Kind = kind
      CaseName = caseName
      Reference = stored.Reference
      SourceFile = sourceFile
      Content = source }

let private flowAttachmentKey (source: FlowLowering.FlowAttachmentSourceDocument) =
    { FlowLowering.FlowAttachmentKey.OwnerId = source.OwnerId
      Kind = source.Kind
      CaseName = source.CaseName }

let private flowAttachmentInventory (sources: FlowLowering.FlowAttachmentSourceDocument list) : FlowLowering.FlowAttachmentInventory =
    { ExpectedSources = sources |> List.map (fun source -> flowAttachmentKey source, source.Reference) |> Map.ofList
      Sources = sources }

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

    let openWordHeader = "word pending(value: Int) -> Int {"
    expectError "EOF after a word's opening brace remains extendable" "FLOW_INCOMPLETE_INPUT"
        (FlowParser.parseWord "<eof>" openWordHeader) |> ignore
    expectError "project parser reports an open word header as incomplete" "FLOW_INCOMPLETE_INPUT"
        (FlowParser.parseDocument "<eof>" openWordHeader) |> ignore
    expectError "EOF after an unfinished doc marker remains extendable" "FLOW_INCOMPLETE_INPUT"
        (FlowParser.parseWord "<eof>" (openWordHeader + "\n    doc")) |> ignore
    expectError "project parser reports an unfinished doc marker as incomplete" "FLOW_INCOMPLETE_INPUT"
        (FlowParser.parseDocument "<eof>" (openWordHeader + "\n    doc")) |> ignore
    expectError "EOF after effects and an unfinished doc marker remains extendable" "FLOW_INCOMPLETE_INPUT"
        (FlowParser.parseWord "<eof>" (openWordHeader + "\n    effects none\n    doc")) |> ignore

    expectError "closed word without effects remains a real validation error" "FLOW_EFFECTS_REQUIRED"
        (FlowParser.parseWord "<closed>" "word closed() -> Int {\n}") |> ignore
    expectError "project parser preserves the closed-word effects error" "FLOW_EFFECTS_REQUIRED"
        (FlowParser.parseDocument "<closed>" "word closed() -> Int {\n}") |> ignore
    expectError "closed word with a doc marker but no string remains a real validation error" "FLOW_DOC_STRING_REQUIRED"
        (FlowParser.parseWord "<closed>" "word closed() -> Int {\n    doc\n}") |> ignore
    expectError "project parser preserves the closed-word doc error" "FLOW_DOC_STRING_REQUIRED"
        (FlowParser.parseDocument "<closed>" "word closed() -> Int {\n    effects none\n    doc\n}") |> ignore
    expectError "closed word with an invalid doc token remains a real validation error" "FLOW_DOC_STRING_REQUIRED"
        (FlowParser.parseWord "<closed>" "word closed() -> Int {\n    doc not-a-string\n    effects none\n}") |> ignore

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

let private resolvedCallsInBlock (block: IrBlock) =
    let rec visit (current: IrBlock) =
        current.Code
        |> List.collect (fun instruction ->
            match instruction.Operation with
            | IrOperation.Call call
            | IrOperation.ListMap(call, _, _)
            | IrOperation.ListFilter(call, _)
            | IrOperation.ListEach(call, _)
            | IrOperation.ListFold(call, _, _)
            | IrOperation.MakeRecord(call, _)
            | IrOperation.GetRecordField(call, _, _)
            | IrOperation.UnwrapScalar(call, _) -> [ call ]
            | IrOperation.WrapScalar(call, _, validator) -> call :: Option.toList validator
            | IrOperation.Scope nested -> visit nested
            | IrOperation.If(thenBlock, elseBlock) -> visit thenBlock @ visit elseBlock
            | IrOperation.MatchOption(_, someBlock, noneBlock) -> visit someBlock @ visit noneBlock
            | IrOperation.MatchResult(_, _, okBlock, errorBlock) -> visit okBlock @ visit errorBlock
            | _ -> [])
    visit block

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
          EffectsDeclared = true
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
    let recordWithFieldType fieldType =
        { Name = "DepthRecord"
          Fields = [ { Name = "value"; Type = fieldType } ]
          SourceText = "host-built record"
          Span = sourceSpan }
    let scalarWithBaseType baseType =
        { Name = "DepthScalar"
          BaseType = baseType
          Validator = None
          SourceText = "host-built scalar"
          Span = sourceSpan }
    let shallowRecordSource = FlowSource.renderRecord (recordWithFieldType (nestedListType 2))
    check "record renderer keeps ordinary closed container field types"
        (shallowRecordSource.Contains("field value: List<List<Int>>;", StringComparison.Ordinal))
    let recordBoundaryType = nestedListType (FlowStructure.maxExpressionDepth - 1)
    let recordBoundarySource = FlowSource.renderRecord (recordWithFieldType recordBoundaryType)
    check "record renderer accepts the exact closed-type depth boundary" (not (String.IsNullOrWhiteSpace recordBoundarySource))
    let overdeepRecord = recordWithFieldType (nestedListType (FlowStructure.maxExpressionDepth + 1))
    let recordTypeDepthError = captureLanguageError "record renderer bounds host-built field type depth" "FLOW_NESTING_LIMIT" (fun () ->
        FlowSource.renderRecord overdeepRecord |> ignore)
    equal "record renderer depth error points to its authored declaration" (Some sourceSpan) recordTypeDepthError.Span
    let scalarSource = FlowSource.renderScalar (scalarWithBaseType TString)
    check "scalar renderer keeps ordinary primitive bases" (scalarSource.Contains("type DepthScalar : String", StringComparison.Ordinal))
    let overdeepScalar = scalarWithBaseType (nestedListType (FlowStructure.maxExpressionDepth + 1))
    let scalarTypeDepthError = captureLanguageError "scalar renderer bounds type depth before formatting an unsupported base" "FLOW_NESTING_LIMIT" (fun () ->
        FlowSource.renderScalar overdeepScalar |> ignore)
    equal "scalar renderer depth error points to its authored declaration" (Some sourceSpan) scalarTypeDepthError.Span
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

let private testFlow2Frontend () =
    let parseWordV2 source =
        match FlowParser.parseWordWithVersion 2 "<flow2-test>" source with
        | Ok definition -> definition
        | Error diagnostic -> failwith (Diagnostics.render diagnostic)
    let parseExpressionV2 source =
        match FlowParser.parseExpressionWithVersion 2 "<flow2-test>" source with
        | Ok expression -> expression
        | Error diagnostic -> failwith (Diagnostics.render diagnostic)

    let implicitEffectsSource =
        """fn sample.pure() -> Bool {
    true
}"""
    let implicitEffects = parseWordV2 implicitEffectsSource
    check "Flow/2 fn accepts omitted effects" (not implicitEffects.EffectsDeclared && Set.isEmpty implicitEffects.Effects)
    equal "Flow/2 renderer preserves omitted effects" implicitEffectsSource (FlowSource.renderWord implicitEffects)
    let explicitEffects =
        parseWordV2
            """fn sample.documented() -> Bool {
    effects none
    doc "Flow/2 metadata"

    true
}"""
    check "Flow/2 tracks an authored pure effects clause" explicitEffects.EffectsDeclared
    let renderedExplicit = FlowSource.renderWord explicitEffects
    check "Flow/2 renderer inserts a blank line after metadata" (renderedExplicit.Contains("    doc \"Flow/2 metadata\"\n\n    true", StringComparison.Ordinal))
    equal "Flow/2 word renderer round-trips metadata shape" renderedExplicit
        (renderedExplicit |> parseWordV2 |> FlowSource.renderWord)

    let legacySource = "word legacy() -> Int {\n    effects none\n    1\n}"
    let legacy =
        match FlowParser.parseWord "<flow1-test>" legacySource with
        | Ok definition -> definition
        | Error diagnostic -> failwith (Diagnostics.render diagnostic)
    equal "Flow/1 keeps its required effects metadata and byte format" legacySource (FlowSource.renderWord legacy)
    expectError "Flow/1 rejects the Flow/2 declaration keyword" "FLOW_SYNTAX_VERSION"
        (FlowParser.parseWordWithVersion 1 "<flow1-test>" implicitEffectsSource) |> ignore
    expectError "Flow/2 rejects the Flow/1 declaration keyword" "FLOW_SYNTAX_VERSION"
        (FlowParser.parseWordWithVersion 2 "<flow2-test>" legacySource) |> ignore
    expectError "Flow/1 still requires effects" "FLOW_EFFECTS_REQUIRED"
        (FlowParser.parseWord "<flow1-test>" "word missing.effects() -> Bool {\n    true\n}") |> ignore
    let omittedV2 = parseWordV2 implicitEffectsSource
    check "Flow/2 accepts omitted effects" (Set.isEmpty omittedV2.Effects)
    expectError "Flow/1 rejects infix equality" "FLOW_SYNTAX_VERSION"
        (FlowParser.parseExpression "<flow1-test>" "1 == 1") |> ignore
    expectError "Flow/1 rejects property reads as dot calls" "FLOW_DOT_CALL_REQUIRES_ARGUMENTS"
        (FlowParser.parseExpression "<flow1-test>" "value.email") |> ignore
    expectError "Flow/1 does not gain transparent grouping" "FLOW_SYNTAX_VERSION"
        (FlowParser.parseExpressionWithVersion 1 "<flow1-test>" "(1)") |> ignore
    match FlowParser.parseExpressionWithVersion 2 "<flow2-test>" "(1)" with
    | Ok(FlowExpression.Literal(LInt 1L, _)) -> check "Flow/2 accepts grouping needed for non-associative equality" true
    | other -> failwithf "Expected grouped Flow/2 literal, got %A" other

    match parseExpressionV2 "value.email" with
    | FlowExpression.Property(FlowExpression.Local("value", _), "email", _) -> check "Flow/2 parses record property reads" true
    | other -> failwithf "Expected Flow/2 property AST, got %A" other
    let comparison = parseExpressionV2 "(1 == 1) == (1 == 2)"
    match comparison with
    | FlowExpression.Equality(FlowExpression.Equality _, FlowExpression.Equality _, _) -> check "parentheses permit explicitly grouped equality" true
    | other -> failwithf "Expected grouped Flow/2 equality AST, got %A" other
    let comparisonText = FlowSource.renderExpressionWithVersion 2 comparison
    equal "grouped equality renderer preserves operand parentheses" "(1 == 1) == (1 == 2)" comparisonText
    equal "grouped equality reparses deterministically" comparisonText
        (comparisonText |> parseExpressionV2 |> FlowSource.renderExpressionWithVersion 2)
    expectError "unparenthesized equality is non-associative" "FLOW_EQUALITY_CHAIN"
        (FlowParser.parseExpressionWithVersion 2 "<flow2-test>" "1 == 1 == true") |> ignore
    let groupedDot = parseExpressionV2 "(true == false).identity()"
    let groupedDotText = FlowSource.renderExpressionWithVersion 2 groupedDot
    equal "Flow/2 formatter groups equality before a postfix dot call" "(true == false).identity()" groupedDotText
    match groupedDotText |> parseExpressionV2 with
    | FlowExpression.DotCall(FlowExpression.Equality _, "identity", _, _) -> check "grouped equality dot-call round-trips without changing precedence" true
    | other -> failwithf "Expected grouped equality receiver, got %A" other

    let v2ProjectSource =
        """record Customer { field email: String; }
fn customer.has-email(value: Customer) -> Bool {
    doc "Flow/2 property and equality"

    value.email == "a@example.com"
}"""
    let project =
        match FlowParser.parseDocumentWithVersion 2 "<flow2-project>" v2ProjectSource with
        | Ok document -> document
        | Error diagnostic -> failwith (Diagnostics.render diagnostic)
    equal "Flow/2 project parser records its source version" 2 project.SyntaxVersion
    let canonicalProject = FlowSource.renderDocument project
    check "Flow/2 project formatter preserves fn declarations" (canonicalProject.Contains("fn customer.has-email", StringComparison.Ordinal))
    equal "Flow/2 project renderer round-trips" canonicalProject
        (canonicalProject
         |> fun source -> FlowParser.parseDocumentWithVersion 2 "<flow2-project>" source
         |> Result.map FlowSource.renderDocument
         |> Result.defaultWith (Diagnostics.render >> failwith))
    let v2TestText =
        """test customer.has-email/value {
    value.email == "a@example.com"
    => value ("a@example.com" == "a@example.com")
}"""
    let v2Test =
        match FlowParser.parseTestWithVersion 2 "<flow2-test>" v2TestText with
        | Ok definition -> definition
        | Error diagnostic -> failwith (Diagnostics.render diagnostic)
    equal "Flow/2 test parser retains the selected syntax version" 2 v2Test.SyntaxVersion
    let canonicalV2Test = FlowSource.renderTest v2Test
    equal "Flow/2 test renderer round-trips Flow/2 expressions" canonicalV2Test
        (canonicalV2Test
         |> fun source -> FlowParser.parseTestWithVersion 2 "<flow2-test>" source
         |> Result.map FlowSource.renderTest
         |> Result.defaultWith (Diagnostics.render >> failwith))
    let v2ExampleText =
        """example customer.has-email/sample {
    value.email == "a@example.com"
    => true
}"""
    let v2Example =
        match FlowParser.parseExampleWithVersion 2 "<flow2-example>" v2ExampleText with
        | Ok definition -> definition
        | Error diagnostic -> failwith (Diagnostics.render diagnostic)
    equal "Flow/2 example parser retains the selected syntax version" 2 v2Example.SyntaxVersion
    equal "Flow/2 example renderer round-trips Flow/2 property expressions" (FlowSource.renderExample v2Example)
        (FlowSource.renderExample v2Example
         |> fun source -> FlowParser.parseExampleWithVersion 2 "<flow2-example>" source
         |> Result.map FlowSource.renderExample
         |> Result.defaultWith (Diagnostics.render >> failwith))

    let recordDefinition =
        { Name = "Customer"
          Fields = [ { Name = "email"; Type = TString } ]
          SourceText = "record Customer { field email: String; }"
          Span = sourceSpan }
    let recordContext =
        loweringContextWith
            (Map.ofList [ "Customer", recordDefinition ]) Map.empty
            [ generatedEntry "customer.new" (RecordConstructor "Customer") [ TString ] [ TNamed "Customer" ] Set.empty
              generatedEntry "customer.email" (RecordAccessor("Customer", "email")) [ TNamed "Customer" ] [ TString ] Set.empty ]
            Map.empty
    let v2Word = parseWordV2 project.Words.Head.SourceText
    let compiledWord = FlowLowering.compileWord recordContext (WordId "flow2-has-email") v2Word
    let invocation =
        Compiler.compileIrBodyAgainstProgramWithSourceOrigins
            compiledWord.Context.CompilerContext compiledWord.Program "flow2-invoke" []
            [ Push(LString "a@example.com", sourceSpan)
              Call("customer.new", sourceSpan)
              Call("customer.has-email", sourceSpan) ]
            compiledWord.Context.SourceOrigins
    equal "Flow/2 property and equality lower and execute through declared record fields" [ BoolValue true ]
        (IrInterpreter.executeBody (host (ResizeArray())) "flow2-property-equality" invocation)
    let callBound = FlowLowering.compileWordWithCallBindings recordContext (WordId "flow2-bound-has-email") v2Word
    let propertySite = callBound.CallSites |> List.find (fun site -> match site.Form with | FlowCallForm.PropertyAccess "email" -> true | _ -> false)
    let equalitySite = callBound.CallSites |> List.find (fun site -> site.Form = FlowCallForm.Direct && site.RequestedName = "equals")
    equal "property binding path identifies its receiver"
        (FlowAstPath.FlowAstPath [ FlowAstPathSegment.BlockStatement 0; FlowAstPathSegment.EvaluateExpression; FlowAstPathSegment.EqualityLeft ])
        propertySite.Path
    equal "equality binding path identifies the operator"
        (FlowAstPath.FlowAstPath [ FlowAstPathSegment.BlockStatement 0; FlowAstPathSegment.EvaluateExpression ]) equalitySite.Path
    let rewriteSource = Storage.sourceObject StorageObjectKind.WordDefinition v2Word.SourceText
    let rewriteBindings =
        callBound.CallSites
        |> List.map (fun site ->
            { OwnerName = v2Word.Name
              OwnerId = WordId "flow2-bound-has-email"
              OwnerRevision = 1
              Source = rewriteSource.Reference
              Site = site })
        |> FlowPersistence.wordBindings
    let rewrittenV2 =
        match FlowRewrite.rewriteWord v2Word.Name "customer.has-email-renamed" (StoredCallTarget.UserWord "unrelated-target") v2Word rewriteBindings with
        | Ok result -> result
        | Error diagnostic -> failwith (Diagnostics.render diagnostic)
    equal "Flow rewrite renders and reparses using the authored Flow/2 version" 2 rewrittenV2.Definition.SyntaxVersion
    check "Flow/2 rewrite keeps the fn declaration" (FlowSource.renderWord rewrittenV2.Definition |> fun source -> source.StartsWith("fn customer.has-email-renamed", StringComparison.Ordinal))

    let sourceReference = Storage.sourceObject StorageObjectKind.WordDefinition implicitEffectsSource
    let inconsistentEffectsSource: FlowSourceDocument =
        { OwnerName = "sample.pure"
          OwnerId = WordId "flow2-effects-shape"
          OwnerRevision = 1
          SyntaxVersion = 2
          EffectsDeclared = true
          Reference = sourceReference.Reference
          SourceFile = "<flow2-source>"
          Content = implicitEffectsSource }
    let emptySourceInventory: FlowSourceInventory = { ExpectedFlowOwnerIds = Set.empty; Sources = [] }
    expectLanguageError "source-backed Flow validation compares EffectsDeclared with parsed metadata" "FLOW_SOURCE_EFFECTS_DECLARATION_MISMATCH" (fun () ->
        FlowLowering.compileBatchFlowSources recordContext emptySourceInventory
            [ { RevisionIntent = FlowWordRevisionIntent.Add(WordId "flow2-effects-shape", 1); Source = inconsistentEffectsSource } ]
        |> ignore)

    let invalidType = parseWordV2 "fn customer.bad-property(value: Int) -> String { value.email }"
    expectLanguageError "property access rejects non-record receiver types" "FLOW_PROPERTY_REQUIRES_RECORD" (fun () ->
        FlowLowering.checkWord recordContext invalidType |> ignore)
    let unknownField = parseWordV2 "fn customer.bad-field(value: Customer) -> String { value.missing }"
    expectLanguageError "property access rejects undeclared record fields" "FLOW_PROPERTY_UNKNOWN_FIELD" (fun () ->
        FlowLowering.checkWord recordContext unknownField |> ignore)
    let fakeAccessorContext =
        loweringContextWith
            (Map.ofList [ "Customer", recordDefinition ]) Map.empty
            [ wordEntry "customer.email" [ TNamed "Customer" ] [ TString ] Set.empty [] ] Map.empty
    expectLanguageError "property access does not resolve an arbitrary same-named host word" "FLOW_PROPERTY_ACCESSOR_MISSING" (fun () ->
        FlowLowering.checkWord fakeAccessorContext v2Word |> ignore)
    expectLanguageError "equality preserves generic equals type constraints" "FLOW_ARGUMENT_TYPE" (fun () ->
        FlowLowering.checkExpressionWithVersion 2 recordContext (parseExpressionV2 "\"email\" == 7") |> ignore)

    let hostV1Property =
        { legacy with
            Body = [ FlowStatement.Evaluate(FlowExpression.Property(FlowExpression.Local("value", sourceSpan), "field", sourceSpan)) ] }
    expectLanguageError "host-built Flow/1 words cannot contain Flow/2 property nodes" "FLOW_SYNTAX_VERSION" (fun () ->
        FlowSource.renderWord hostV1Property |> ignore)
    expectLanguageError "host-built Flow/1 expressions cannot contain Flow/2 equality nodes" "FLOW_SYNTAX_VERSION" (fun () ->
        FlowLowering.lowerExpression recordContext (FlowExpression.Equality(FlowExpression.Literal(LInt 1L, sourceSpan), FlowExpression.Literal(LInt 1L, sourceSpan), sourceSpan)) |> ignore)

    let lintWord =
        parseWordV2
            """fn customer.lint(value: Customer) -> Bool {
    let saved = value;
    let same = saved.email == "email";
    same
}"""
    match FlowLint.analyze FlowLint.defaultOptions lintWord with
    | Ok [] -> check "FlowLint follows locals through property and equality nodes" true
    | Ok warnings -> failwithf "Flow/2 property/equality linter reported unexpected warnings: %A" warnings
    | Error problem -> failwith $"{problem.Code}: {problem.Message}"

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
        | FoldList(name, source) -> FoldList(name, shiftMarker source)
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

let private testStaticListFold () =
    let step =
        wordEntry "math.fold-step" [ TInt; TInt ] [ TInt ] Set.empty
            [ Call("swap", sourceSpan)
              Push(LInt 10L, sourceSpan)
              Call("multiply", sourceSpan)
              Call("add", sourceSpan) ]
    let emailStep =
        wordEntry "scalar.email-step" [ TNamed "Email"; TInt ] [ TNamed "Email" ] Set.empty
            [ Call("drop", sourceSpan)
              Call("drop", sourceSpan)
              Push(LString "folded@example.com", sourceSpan)
              Call("Email.new", sourceSpan) ]
    let customerStep =
        wordEntry "customer.fold-step" [ TNamed "Customer"; TInt ] [ TNamed "Customer" ] Set.empty
            [ Call("drop", sourceSpan)
              Call("drop", sourceSpan)
              Push(LString "folded@example.com", sourceSpan)
              Call("Email.new", sourceSpan)
              Call("customer.new", sourceSpan) ]
    let badArity = wordEntry "bad.fold-arity" [ TInt ] [ TInt ] Set.empty []
    let badOrder = wordEntry "bad.fold-order" [ TInt; TNamed "Email" ] [ TInt ] Set.empty [ Call("drop", sourceSpan) ]
    let badOutput = wordEntry "bad.fold-output" [ TInt; TInt ] [ TNamed "Email" ] Set.empty [ Call("drop", sourceSpan); Call("drop", sourceSpan); Push(LString "wrong", sourceSpan); Call("Email.new", sourceSpan) ]
    let stringStep = wordEntry "bad.string-step" [ TString; TInt ] [ TString ] Set.empty [ Call("drop", sourceSpan) ]
    let ordinaryDot = wordEntry "number.fold" [ TInt; TInt ] [ TInt ] Set.empty [ Call("add", sourceSpan) ]
    let context = richTypeContext [ step; emailStep; customerStep; badArity; badOrder; badOutput; stringStep; ordinaryDot ]
    let compile source = FlowLowering.compileExpression context (parseExpression source)
    let evaluate source = IrInterpreter.executeBody (host (ResizeArray())) source (compile source).Body

    let source = "list::singleton<Int>(1).fold(0, math::fold-step)"
    match parseExpression source with
    | FlowExpression.DotCall(_, "fold", [ FlowArgument.Positional seed; FlowArgument.WordReference reference ], _) ->
        match seed with
        | FlowExpression.Literal(LInt 0L, _) -> check "fold keeps the seed as an ordinary value expression" true
        | other -> failwithf "Expected ordinary fold seed expression, got %A" other
        equal "fold callback preserves namespace qualification" FlowWordReferenceQualification.NamespaceQualified reference.Qualification
        equal "fold callback preserves its stable dictionary key" "math.fold-step" reference.Name
        let canonical = FlowSource.renderExpression (parseExpression source)
        equal "qualified fold source round-trips" canonical (canonical |> parseExpression |> FlowSource.renderExpression)
    | other -> failwithf "Expected a static fold dot call, got %A" other

    let empty = compile "list::empty<Int>().fold(7, math::fold-step)"
    let emptyVerified = VerifiedIrBody.inspect empty.Body
    let emptyCoverage = emptyVerified.BodyCoverage.BranchOutcomes |> Map.toList |> List.collect snd |> Set.ofList
    equal "fold declares empty and nonempty branch obligations" (Set.ofList [ "empty"; "nonempty" ]) emptyCoverage
    equal "empty fold returns the seed unchanged" [ IntValue 7L ] (IrInterpreter.executeBody (host (ResizeArray())) "fold-empty" empty.Body)
    let populated = compile "list::append(list::append(list::singleton<Int>(1), 2), 3).fold(0, math::fold-step)"
    let populatedVerified = VerifiedIrBody.inspect populated.Body
    equal "fold visits values left-to-right and preserves noncommutative order" [ IntValue 123L ]
        (IrInterpreter.executeBody (host (ResizeArray())) "fold-order" populated.Body)
    match populatedVerified.BodyBlock.Code |> List.tryPick (function | { Operation = IrOperation.ListFold(call, itemType, accumulatorType) } -> Some(call, itemType, accumulatorType) | _ -> None) with
    | Some(call, itemType, accumulatorType) ->
        equal "fold is an explicit typed IR operation" IrInt itemType
        equal "fold IR retains its accumulator type" IrInt accumulatorType
        equal "fold callback IR target is the resolved stable identity" (UserWordTarget(WordId "user-math.fold-step", 1)) call.ResolvedTarget
    | None -> failwith "Expected a verified ListFold operation."
    let callbackSpan =
        match parseExpression "list::append(list::append(list::singleton<Int>(1), 2), 3).fold(0, math::fold-step)" with
        | FlowExpression.DotCall(_, "fold", [ _; FlowArgument.WordReference reference ], _) -> reference.Span
        | _ -> failwith "Expected static fold callback syntax."
    let foldSites = populatedVerified.BodySourceMap |> Map.toList |> List.choose (fun (_, site) -> if site.SourceKind = "list-fold" then Some site else None)
    equal "fold source map contains one fold operation" 1 foldSites.Length
    equal "fold source map points to the authored callback reference" callbackSpan foldSites.Head.SiteSpan

    let emailResult = compile "list::singleton<Int>(1).fold(Email::new(\"seed@example.com\"), scalar::email-step)"
    equal "fold preserves nominal scalar accumulator identity" [ TNamed "Email" ] (FlowLowering.checkExpression context (parseExpression "list::empty<Int>().fold(Email::new(\"seed@example.com\"), scalar::email-step)") |> snd |> fun checkResult -> checkResult.Stack)
    match FlowLowering.checkExpression context (parseExpression "list::empty<Int>().fold(customer::new(Email::new(\"seed@example.com\")), customer::fold-step)") |> snd with
    | { Stack = [ TNamed "Customer" ] } -> check "fold preserves nominal record accumulator identity" true
    | checkResult -> failwithf "Expected a Customer accumulator result, got %A" checkResult.Stack
    equal "fold executes with a nominal Email accumulator" [ NamedValue("Email", StringValue "folded@example.com") ]
        (IrInterpreter.executeBody (host (ResizeArray())) "fold-email" emailResult.Body)
    expectLanguageError "String does not substitute for nominal Email accumulator" "FLOW_CALLBACK_INPUT_TYPE" (fun () ->
        FlowLowering.checkExpression context (parseExpression "list::empty<Int>().fold(Email::new(\"seed@example.com\"), bad::string-step)") |> ignore)
    expectLanguageError "callback inputs must be accumulator then item" "FLOW_CALLBACK_INPUT_TYPE" (fun () ->
        FlowLowering.checkExpression context (parseExpression "list::empty<Int>().fold(Email::new(\"seed@example.com\"), bad::fold-order)") |> ignore)
    expectLanguageError "fold callback must accept exactly two inputs" "FLOW_CALLBACK_INPUT_ARITY" (fun () ->
        FlowLowering.checkExpression context (parseExpression "list::empty<Int>().fold(0, bad::fold-arity)") |> ignore)
    expectLanguageError "fold callback must return the exact accumulator type" "FLOW_CALLBACK_RESULT_TYPE" (fun () ->
        FlowLowering.checkExpression context (parseExpression "list::empty<Int>().fold(0, bad::fold-output)") |> ignore)

    match parseExpression "1.fold(2)" with
    | FlowExpression.DotCall(_, "fold", [ FlowArgument.Positional(FlowExpression.Literal(LInt 2L, _)) ], _) ->
        equal "ordinary fold(value) remains an ordinary dot-stage call" [ IntValue 3L ] (evaluate "1.fold(2)")
    | other -> failwithf "Expected ordinary dot-call arguments to remain values, got %A" other
    match FlowParser.parseExpression "<fold-unrelated-stage>" "1.abs(math::fold-step)" with
    | Error diagnostic -> equal "unrelated stages reject bare qualified callbacks" "FLOW_QUALIFIED_CALL_REQUIRES_ARGUMENTS" diagnostic.Code
    | Ok _ -> failwith "An unrelated dot stage cannot accept a static callback."
    match FlowParser.parseExpression "<fold-closure>" "list::empty<Int>().fold(0, item => item)" with
    | Error diagnostic -> check "fold rejects inline capturing callback expressions" (not (String.IsNullOrWhiteSpace diagnostic.Code))
    | Ok _ -> failwith "Fold does not accept callback closures."
    match FlowParser.parseExpression "<fold-named-seed>" "list::empty<Int>().fold(seed = 0, math::fold-step)" with
    | Error diagnostic -> equal "fold seed must remain positional" "FLOW_CALLBACK_ARGUMENT_ARITY" diagnostic.Code
    | Ok _ -> failwith "Fold does not accept a named seed with a static callback."
    let shortReference = { Name = "math.fold-step"; Qualification = FlowWordReferenceQualification.NamespaceQualified; Span = sourceSpan }
    let malformed =
        FlowExpression.DotCall(
            FlowExpression.Container(FlowContainerConstructor.ListEmpty, [ { Type = TInt; Span = sourceSpan } ], None, sourceSpan),
            "fold",
            [ FlowArgument.WordReference shortReference ],
            sourceSpan)
    expectLanguageError "host AST rejects malformed fold callback arity" "FLOW_CALLBACK_ARGUMENT_ARITY" (fun () ->
        FlowLowering.checkExpression context malformed |> ignore)
    expectLanguageError "local variable names cannot resolve as fold callback captures" "FLOW_UNKNOWN_CALLBACK" (fun () ->
        FlowLowering.checkExpression context (parseExpression "list::empty<Int>().fold(0, word local-step)") |> ignore)

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
                    | FlowExpression.Property(_, _, source) | FlowExpression.Equality(_, _, source)
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
        | FoldList(name, source) -> FoldList(name, shiftMarker source)
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
    let unsupportedCaseVersion = { literalTest with SyntaxVersion = 3 }
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

let private testFlowBatchForwardResolution () =
    let context = loweringContext [] Map.empty
    let added (source: string) (identity: string) : FlowLowering.FlowWordChange =
        { Definition = parseWord source
          RevisionIntent = FlowLowering.FlowWordRevisionIntent.Add(WordId identity, 1) }
    let callbackConsumer =
        added
            """word batch.callback-consumer(items: List<Int>) -> List<Int> {
    effects none
    items.map(batch::increment)
}"""
            "batch-callback-consumer"
    let dotConsumer =
        added
            """word batch.dot-consumer(value: Int) -> Int {
    effects none
    value.bump(2)
}"""
            "batch-dot-consumer"
    let forwardConsumer =
        added
            """word batch.forward-consumer(value: Int) -> Int {
    effects none
    batch::increment(value)
}"""
            "batch-forward-consumer"
    let bump =
        added
            """word batch.bump(value: Int, amount: Int) -> Int {
    effects none
    add(value, amount)
}"""
            "batch-bump"
    let increment =
        added
            """word batch.increment(value: Int) -> Int {
    effects none
    add(value, 1)
}"""
            "batch-increment"
    let compiled = FlowLowering.compileBatchWords context [ callbackConsumer; dotConsumer; forwardConsumer; bump; increment ]
    equal "batch lowering preserves host change order" [ "batch.callback-consumer"; "batch.dot-consumer"; "batch.forward-consumer"; "batch.bump"; "batch.increment" ]
        (compiled.LoweredWords |> List.map (fun word -> word.Definition.Name))

    let invoke name body =
        let executable =
            Compiler.compileIrBodyAgainstProgramWithSourceOrigins
                compiled.Context.CompilerContext compiled.Program ("batch-invoke-" + name) [] body compiled.Context.SourceOrigins
        IrInterpreter.executeBody (host (ResizeArray())) ("batch-invoke-" + name) executable

    equal "forward ordinary batch call executes" [ IntValue 6L ]
        (invoke "ordinary" [ Push(LInt 5L, sourceSpan); Call("batch.forward-consumer", sourceSpan) ])
    equal "forward dot-stage batch call executes" [ IntValue 7L ]
        (invoke "dot" [ Push(LInt 5L, sourceSpan); Call("batch.dot-consumer", sourceSpan) ])
    equal "forward callback batch call executes" [ ListValue(TInt, [ IntValue 6L ]) ]
        (invoke "callback" [ Push(LInt 5L, sourceSpan); ConstructContainer(ListSingleton, [ TInt ], sourceSpan); Call("batch.callback-consumer", sourceSpan) ])

    let verified = VerifiedIrProgram.inspect compiled.Program
    let callTarget name =
        verified.FunctionsById[WordId name].FunctionBody
        |> resolvedCallsInBlock
        |> List.tryFind (fun call -> call.ResolvedName.StartsWith("batch.", StringComparison.Ordinal))
        |> Option.map (fun call -> call.ResolvedTarget)
    equal "forward ordinary call binds to the batch identity" (Some(UserWordTarget(WordId "batch-increment", 1))) (callTarget "batch-forward-consumer")
    equal "dot-stage call binds to the exact batch identity" (Some(UserWordTarget(WordId "batch-bump", 1))) (callTarget "batch-dot-consumer")
    equal "static callback binds to the exact batch identity" (Some(UserWordTarget(WordId "batch-increment", 1))) (callTarget "batch-callback-consumer")

let private testFlowBatchSignatureKindsAndNamedArguments () =
    let context = loweringContextWith Map.empty Map.empty [] (Map.ofList [ "add", [ "left"; "right" ] ])
    let added (source: string) (identity: string) : FlowLowering.FlowWordChange =
        { Definition = parseWord source
          RevisionIntent = FlowLowering.FlowWordRevisionIntent.Add(WordId identity, 1) }
    let namedConsumer =
        added
            """word batch.named-consumer() -> Int {
    effects console.write
    batch::pair(second = batch::mark-right(), first = batch::mark-left())
}"""
            "batch-named-consumer"
    let pair =
        added
            """word batch.pair(first: Int, second: Int) -> Int {
    effects none
    add(first, second)
}"""
            "batch-pair"
    let markLeft =
        added
            """word batch.mark-left() -> Int {
    effects console.write
    console::write("left");
    1
}"""
            "batch-mark-left"
    let markRight =
        added
            """word batch.mark-right() -> Int {
    effects console.write
    console::write("right");
    2
}"""
            "batch-mark-right"
    let sameText =
        added
            """word batch.same-text(left: String, right: String) -> Bool {
    effects none
    equals(left, right)
}"""
            "batch-same-text"
    let compiled = FlowLowering.compileBatchWords context [ namedConsumer; pair; markLeft; markRight; sameText ]
    let events = ResizeArray<string>()
    let invoke name body =
        let executable = Compiler.compileIrBodyAgainstProgramWithSourceOrigins compiled.Context.CompilerContext compiled.Program name [] body compiled.Context.SourceOrigins
        IrInterpreter.executeBody (host events) name executable

    equal "generic primitive variables unify in proposed word signatures" [ BoolValue true ]
        (invoke "same-text" [ Push(LString "same", sourceSpan); Push(LString "same", sourceSpan); Call("batch.same-text", sourceSpan) ])
    equal "named argument signature is available before its declaration is lowered" [ IntValue 3L ]
        (invoke "named-consumer" [ Call("batch.named-consumer", sourceSpan) ])
    equal "batch named arguments preserve written evaluation order" [ "right"; "left" ] (List.ofSeq events)

    let typedContext = richTypeContext []
    let recordContext =
        { typedContext with
            ParameterNames = Map.ofList [ "customer.new", [ "host-provided-name" ] ] }
    let malformedRecordContext =
        { typedContext with
            ParameterNames = Map.ofList [ "customer.new", [ "email"; "extra" ] ] }
    let makeCustomer =
        added
            """word batch.make-customer(address: String) -> Email {
    effects none
    let customer = customer::new(email = Email::new(address));
    customer.email()
}"""
            "batch-make-customer"
    expectLanguageError "explicit generated-constructor metadata is validated even though field names remain authoritative" "FLOW_BATCH_PARAMETER_ARITY" (fun () ->
        FlowLowering.compileBatchWords malformedRecordContext [ makeCustomer ] |> ignore)
    let compiledTypes = FlowLowering.compileBatchWords recordContext [ makeCustomer ]
    let typedProgram = VerifiedIrProgram.inspect compiledTypes.Program
    let makeFunction = typedProgram.FunctionsById[WordId "batch-make-customer"]
    let generatedCalls = resolvedCallsInBlock makeFunction.FunctionBody
    check "batch signature catalog uses generated record/scalar constructors and accessors"
        ((generatedCalls |> List.exists (fun call -> call.ResolvedTarget = GeneratedWordTarget(WordId "generated-customer.new", 1)))
         && (generatedCalls |> List.exists (fun call -> call.ResolvedTarget = GeneratedWordTarget(WordId "generated-customer.email", 1)))
         && (generatedCalls |> List.exists (fun call -> call.ResolvedTarget = GeneratedWordTarget(WordId "generated-Email.new", 1))))
    let customerBody =
        Compiler.compileIrBodyAgainstProgramWithSourceOrigins compiledTypes.Context.CompilerContext compiledTypes.Program "invoke-customer" []
            [ Push(LString "a@b", sourceSpan); Call("batch.make-customer", sourceSpan) ] compiledTypes.Context.SourceOrigins
    equal "record constructor keeps field-derived named parameters" [ NamedValue("Email", StringValue "a@b") ]
        (IrInterpreter.executeBody (host (ResizeArray())) "invoke-customer" customerBody)

let private testFlowBatchReplacementAndValidation () =
    let oldEntryBase = wordEntry "batch.transform" [ TInt ] [ TInt ] Set.empty [ Call("int.abs", sourceSpan) ]
    let oldEntry =
        { oldEntryBase with
            Definition = { oldEntryBase.Definition with Maturity = LibraryWord }
            Maturity = LibraryWord
            Status = Persistent }
    let baseContext = loweringContext [ oldEntry ] (Map.ofList [ "batch.transform", [ "value" ] ])
    let replaceWithString: FlowLowering.FlowWordChange =
        { Definition = parseWord "word batch.transform(value: String) -> String {\n    effects none\n    string::trim(value)\n}"
          RevisionIntent = FlowLowering.FlowWordRevisionIntent.Replace(WordId "user-batch.transform", 1, 2) }
    let consumer: FlowLowering.FlowWordChange =
        { Definition = parseWord "word batch.consumer(value: String) -> String {\n    effects none\n    batch::transform(value)\n}"
          RevisionIntent = FlowLowering.FlowWordRevisionIntent.Add(WordId "batch-consumer", 1) }
    let successful = FlowLowering.compileBatchWords baseContext [ replaceWithString; consumer ]
    let replacement = successful.Context.CompilerContext.Words["batch.transform"]
    equal "replacement preserves persistent status" Persistent replacement.Status
    equal "replacement preserves library maturity" LibraryWord replacement.Maturity
    equal "replacement keeps definition maturity synchronized" LibraryWord replacement.Definition.Maturity
    equal "replacement advances the entry revision" 2 replacement.Revision
    equal "replacement advances the definition revision" 2 replacement.Definition.Revision
    equal "replacement preserves stable identity" (WordId "user-batch.transform") successful.Context.CompilerContext.WordIds["batch.transform"]
    let updatedCall =
        VerifiedIrProgram.inspect successful.Program
        |> fun program -> program.FunctionsById[WordId "batch-consumer"].FunctionBody |> resolvedCallsInBlock
        |> List.find (fun call -> call.ResolvedName = "batch.transform")
    equal "changed replacement signature is visible to a sibling in the same batch" (UserWordTarget(WordId "user-batch.transform", 2)) updatedCall.ResolvedTarget
    let invoke =
        Compiler.compileIrBodyAgainstProgramWithSourceOrigins successful.Context.CompilerContext successful.Program "invoke-replaced" []
            [ Push(LString "  trimmed  ", sourceSpan); Call("batch.consumer", sourceSpan) ] successful.Context.SourceOrigins
    equal "sibling executes against the replacement body" [ StringValue "trimmed" ]
        (IrInterpreter.executeBody (host (ResizeArray())) "invoke-replaced" invoke)

    let retainedCaller =
        wordEntry "batch.retained-caller" [ TInt ] [ TInt ] Set.empty [ Call("batch.transform", sourceSpan) ]
    let incompatibleContext =
        loweringContext [ oldEntry; retainedCaller ] (Map.ofList [ "batch.transform", [ "value" ]; "batch.retained-caller", [ "value" ] ])
    let immutableSnapshot = incompatibleContext
    let retainedError = captureLanguageError "replacement rechecks unchanged callers against the complete new signatures" "TYPE_STACK_MISMATCH" (fun () ->
        FlowLowering.compileBatchWords incompatibleContext [ replaceWithString ] |> ignore)
    equal "retained-call mismatch identifies the resolved callee" (Some "batch.transform") retainedError.Word
    check "retained-call mismatch retains its source location" retainedError.Span.IsSome
    check "failed batch compilation leaves the input snapshot unchanged" (incompatibleContext = immutableSnapshot)

    let newWord = parseWord "word batch.added(value: Int) -> Int {\n    effects none\n    add(value, 1)\n}"
    let addChange (id: string) (revision: int) : FlowLowering.FlowWordChange =
        { Definition = newWord
          RevisionIntent = FlowLowering.FlowWordRevisionIntent.Add(WordId id, revision) }
    expectLanguageError "an add cannot reuse an existing name" "FLOW_BATCH_ADD_EXISTS" (fun () ->
        FlowLowering.compileBatchWords baseContext [ { addChange "fresh-id" 1 with Definition = parseWord "word batch.transform(value: Int) -> Int {\n    effects none\n    value\n}" } ] |> ignore)
    expectLanguageError "an add cannot reuse an existing stable identity" "FLOW_BATCH_ID_COLLISION" (fun () ->
        FlowLowering.compileBatchWords baseContext [ addChange "user-batch.transform" 1 ] |> ignore)
    expectLanguageError "two additions cannot share one identity" "FLOW_BATCH_ID_COLLISION" (fun () ->
        FlowLowering.compileBatchWords baseContext [ addChange "reused-id" 1; { addChange "reused-id" 1 with Definition = parseWord "word batch.other(value: Int) -> Int {\n    effects none\n    value\n}" } ] |> ignore)
    expectLanguageError "duplicate batch dictionary names fail before insertion" "FLOW_BATCH_DUPLICATE_NAME" (fun () ->
        FlowLowering.compileBatchWords baseContext [ addChange "first-id" 1; addChange "second-id" 1 ] |> ignore)
    expectLanguageError "replacement requires an existing user word" "FLOW_BATCH_REPLACE_MISSING" (fun () ->
        FlowLowering.compileBatchWords baseContext [ { replaceWithString with Definition = parseWord "word missing(value: String) -> String {\n    effects none\n    value\n}" } ] |> ignore)
    expectLanguageError "primitive replacement is protected" "FLOW_BATCH_REPLACE_PROTECTED" (fun () ->
        FlowLowering.compileBatchWords baseContext [ { replaceWithString with Definition = parseWord "word add(left: String, right: String) -> String {\n    effects none\n    string::concat(left, right)\n}"; RevisionIntent = FlowLowering.FlowWordRevisionIntent.Replace(WordId "primitive-add", 1, 2) } ] |> ignore)
    expectLanguageError "replacement identity must match the current dictionary identity" "FLOW_BATCH_REPLACE_ID_MISMATCH" (fun () ->
        FlowLowering.compileBatchWords baseContext [ { replaceWithString with RevisionIntent = FlowLowering.FlowWordRevisionIntent.Replace(WordId "other-id", 1, 2) } ] |> ignore)
    expectLanguageError "replacement expected revision must match the snapshot" "FLOW_BATCH_STALE_REVISION" (fun () ->
        FlowLowering.compileBatchWords baseContext [ { replaceWithString with RevisionIntent = FlowLowering.FlowWordRevisionIntent.Replace(WordId "user-batch.transform", 0, 2) } ] |> ignore)
    expectLanguageError "replacement revision must advance" "FLOW_BATCH_REVISION_NOT_ADVANCED" (fun () ->
        FlowLowering.compileBatchWords baseContext [ { replaceWithString with RevisionIntent = FlowLowering.FlowWordRevisionIntent.Replace(WordId "user-batch.transform", 1, 1) } ] |> ignore)

    let basicContext = loweringContext [] Map.empty
    let basicAdd = addChange "new-id" 1
    expectLanguageError "an empty batch is rejected explicitly" "FLOW_BATCH_EMPTY" (fun () ->
        FlowLowering.compileBatchWords basicContext [] |> ignore)
    expectLanguageError "base dictionary and identity keys must match exactly" "FLOW_BATCH_BASE_ID_MAP" (fun () ->
        let malformed = { basicContext with CompilerContext = { basicContext.CompilerContext with WordIds = Map.remove "add" basicContext.CompilerContext.WordIds } }
        FlowLowering.compileBatchWords malformed [ basicAdd ] |> ignore)
    expectLanguageError "base IDs must be globally unique" "FLOW_BATCH_BASE_ID_DUPLICATE" (fun () ->
        let ids = Map.add "subtract" (WordId "primitive-add") basicContext.CompilerContext.WordIds
        let malformed = { basicContext with CompilerContext = { basicContext.CompilerContext with WordIds = ids } }
        FlowLowering.compileBatchWords malformed [ basicAdd ] |> ignore)
    expectLanguageError "parameter metadata keys must exist in the base dictionary" "FLOW_BATCH_PARAMETER_CATALOG_KEY" (fun () ->
        let malformed = { basicContext with ParameterNames = Map.ofList [ "ghost.word", [ "value" ] ] }
        FlowLowering.compileBatchWords malformed [ basicAdd ] |> ignore)
    expectLanguageError "base parameter names must be unique and match arity" "FLOW_BATCH_PARAMETER_DUPLICATE" (fun () ->
        let malformed = { basicContext with ParameterNames = Map.ofList [ "add", [ "value"; "value" ] ] }
        FlowLowering.compileBatchWords malformed [ basicAdd ] |> ignore)
    expectLanguageError "base parameter names must match input arity" "FLOW_BATCH_PARAMETER_ARITY" (fun () ->
        let malformed = { basicContext with ParameterNames = Map.ofList [ "add", [ "value" ] ] }
        FlowLowering.compileBatchWords malformed [ basicAdd ] |> ignore)
    expectLanguageError "base word and definition revisions must be synchronized" "FLOW_BATCH_BASE_REVISION" (fun () ->
        let inconsistentWords = basicContext.CompilerContext.Words |> Map.add "add" { basicContext.CompilerContext.Words["add"] with Definition = { basicContext.CompilerContext.Words["add"].Definition with Revision = 0 } }
        let malformed = { basicContext with CompilerContext = { basicContext.CompilerContext with Words = inconsistentWords } }
        FlowLowering.compileBatchWords malformed [ basicAdd ] |> ignore)
    for malformedName in [ ""; "bad name"; "a..b"; ".leading"; "trailing." ] do
        let malformedDefinition = { newWord with Name = malformedName }
        let malformedChange: FlowLowering.FlowWordChange =
            { Definition = malformedDefinition
              RevisionIntent = FlowLowering.FlowWordRevisionIntent.Add(WordId "bad-name", 1) }
        expectLanguageError ("host-built malformed batch word name " + malformedName) "FLOW_BATCH_WORD_NAME_INVALID" (fun () ->
            FlowLowering.compileBatchWords basicContext [ malformedChange ] |> ignore)
    let unknownTypeWord =
        { newWord with Parameters = [ { newWord.Parameters.Head with Type = TNamed "MissingType" } ] }
    expectLanguageError "proposed signatures must use types present in the immutable type catalog" "FLOW_BATCH_UNKNOWN_TYPE" (fun () ->
        FlowLowering.compileBatchWords basicContext
            [ { Definition = unknownTypeWord; RevisionIntent = FlowLowering.FlowWordRevisionIntent.Add(WordId "unknown-type", 1) } ]
        |> ignore)
    let openTypeWord =
        { newWord with Parameters = [ { newWord.Parameters.Head with Type = TVar "a" } ] }
    expectLanguageError "Flow batch declarations cannot introduce generic type variables" "FLOW_BATCH_OPEN_TYPE" (fun () ->
        FlowLowering.compileBatchWords basicContext
            [ { Definition = openTypeWord; RevisionIntent = FlowLowering.FlowWordRevisionIntent.Add(WordId "open-type", 1) } ]
        |> ignore)
    let unknownEffectWord = { newWord with Effects = Set.singleton "memory.allocate" }
    expectLanguageError "proposed effects must belong to the closed host effect vocabulary" "FLOW_BATCH_UNKNOWN_EFFECT" (fun () ->
        FlowLowering.compileBatchWords basicContext
            [ { Definition = unknownEffectWord; RevisionIntent = FlowLowering.FlowWordRevisionIntent.Add(WordId "unknown-effect", 1) } ]
        |> ignore)

let private testFlowWordRehydrate () =
    let context = loweringContext [] Map.empty
    let emptyWordInventory: FlowLowering.FlowSourceInventory =
        { ExpectedFlowOwnerIds = Set.empty
          Sources = [] }
    let emptyAttachmentInventory = flowAttachmentInventory []
    let libraryDocument =
        authoredFlowSource (WordId "rehydrated-library") 7
            "word restore.library(value: Int) -> Int {\n    effects none\n    add(value, 1)\n}"
    let temporaryDocument =
        authoredFlowSource (WordId "rehydrated-temporary") 0
            "word restore.temporary(value: Int) -> Int {\n    effects none\n    value\n}"
    let candidateDocument =
        authoredFlowSource (WordId "ordinary-flow-add") 2
            "word restore.candidate(value: Int) -> Int {\n    effects none\n    value\n}"
    let sourceChanges: FlowLowering.FlowSourceChange list =
        [ { RevisionIntent = FlowLowering.FlowWordRevisionIntent.Rehydrate(WordId "rehydrated-library", 7, Persistent, LibraryWord)
            Source = libraryDocument }
          { RevisionIntent = FlowLowering.FlowWordRevisionIntent.Rehydrate(WordId "rehydrated-temporary", 0, Temporary, ProjectWord)
            Source = temporaryDocument }
          { RevisionIntent = FlowLowering.FlowWordRevisionIntent.Add(WordId "ordinary-flow-add", 2)
            Source = candidateDocument } ]
    let rebuilt =
        FlowLowering.compileBatchFlowProjectSources
            context emptyWordInventory sourceChanges emptyAttachmentInventory []
    let finalContext = rebuilt.WordCompilation.Context.CompilerContext
    let assertEntry (name: string) (identity: string) (revision: int) (status: WordStatus) (maturity: WordMaturity) =
        let entry = finalContext.Words[name]
        equal (name + " stable ID") (WordId identity) finalContext.WordIds[name]
        equal (name + " entry revision") revision entry.Revision
        equal (name + " definition revision") revision entry.Definition.Revision
        equal (name + " lifecycle status") status entry.Status
        equal (name + " entry maturity") maturity entry.Maturity
        equal (name + " definition maturity") maturity entry.Definition.Maturity

    assertEntry "restore.library" "rehydrated-library" 7 Persistent LibraryWord
    assertEntry "restore.temporary" "rehydrated-temporary" 0 Temporary ProjectWord
    assertEntry "restore.candidate" "ordinary-flow-add" 2 Candidate ProjectWord
    let verified = VerifiedIrProgram.inspect rebuilt.WordCompilation.Program
    equal "rehydrated library metadata reaches verified function revision" 7
        verified.FunctionsById[WordId "rehydrated-library"].FunctionRevision
    equal "rehydrated temporary metadata reaches verified function revision" 0
        verified.FunctionsById[WordId "rehydrated-temporary"].FunctionRevision
    equal "ordinary source-backed Add still publishes its requested function revision" 2
        verified.FunctionsById[WordId "ordinary-flow-add"].FunctionRevision

    let execute (name: string) (value: int64) =
        let body =
            Compiler.compileIrBodyAgainstProgramWithSourceOrigins
                finalContext rebuilt.WordCompilation.Program "rehydrate-test" []
                [ Push(LInt value, sourceSpan); Call(name, sourceSpan) ] rebuilt.WordCompilation.Context.SourceOrigins
        IrInterpreter.executeBody (host (ResizeArray())) "rehydrate-test" body
    equal "rehydrated persistent library Flow body remains executable" [ IntValue 10L ] (execute "restore.library" 9L)
    equal "rehydrated temporary project Flow body remains executable" [ IntValue 9L ] (execute "restore.temporary" 9L)

    let addedDefinition = parseWord "word restore.invalid(value: Int) -> Int {\n    effects none\n    value\n}"
    let directChange (intent: FlowLowering.FlowWordRevisionIntent) : FlowLowering.FlowWordChange =
        { Definition = addedDefinition
          RevisionIntent = intent }
    expectLanguageError "rehydrate rejects negative persisted revisions" "FLOW_BATCH_REVISION_INVALID" (fun () ->
        FlowLowering.compileBatchWords context
            [ directChange (FlowLowering.FlowWordRevisionIntent.Rehydrate(WordId "negative-rehydrate", -1, Persistent, ProjectWord)) ]
        |> ignore)
    expectLanguageError "rehydrate rejects primitive lifecycle status" "FLOW_BATCH_REHYDRATE_METADATA_INVALID" (fun () ->
        FlowLowering.compileBatchWords context
            [ directChange (FlowLowering.FlowWordRevisionIntent.Rehydrate(WordId "primitive-rehydrate", 1, Primitive, ProjectWord)) ]
        |> ignore)

    let firstDuplicateDocument =
        authoredFlowSource (WordId "duplicate-rehydrate") 1
            "word restore.first(value: Int) -> Int {\n    effects none\n    value\n}"
    let secondDuplicateDocument =
        authoredFlowSource (WordId "duplicate-rehydrate") 1
            "word restore.second(value: Int) -> Int {\n    effects none\n    value\n}"
    let duplicateIdChanges: FlowLowering.FlowSourceChange list =
        [ { RevisionIntent = FlowLowering.FlowWordRevisionIntent.Rehydrate(WordId "duplicate-rehydrate", 1, Persistent, LibraryWord)
            Source = firstDuplicateDocument }
          { RevisionIntent = FlowLowering.FlowWordRevisionIntent.Rehydrate(WordId "duplicate-rehydrate", 1, Temporary, ProjectWord)
            Source = secondDuplicateDocument } ]
    expectLanguageError "rehydrate rejects duplicate stable IDs across source owners" "FLOW_BATCH_ID_COLLISION" (fun () ->
        FlowLowering.compileBatchFlowProjectSources context emptyWordInventory duplicateIdChanges emptyAttachmentInventory []
        |> ignore)

let private testFlowBatchFinalValidationAndOrigins () =
    let context = loweringContext [] Map.empty
    let added (source: string) (identity: string) : FlowLowering.FlowWordChange =
        { Definition = parseWord source
          RevisionIntent = FlowLowering.FlowWordRevisionIntent.Add(WordId identity, 1) }
    let effectfulButUnderdeclared =
        added "word batch.underdeclared() -> Unit {\n    effects none\n    console::write(\"hello\")\n}" "batch-underdeclared"
    expectLanguageError "final compiler checks the aggregate body effect surface" "EFFECT_UNDECLARED" (fun () ->
        FlowLowering.compileBatchWords context [ effectfulButUnderdeclared ] |> ignore)
    let cycleA = added "word batch.cycle-a(value: Int) -> Int {\n    effects none\n    batch::cycle-b(value)\n}" "batch-cycle-a"
    let cycleB = added "word batch.cycle-b(value: Int) -> Int {\n    effects none\n    batch::cycle-a(value)\n}" "batch-cycle-b"
    expectLanguageError "final compiler checks cycles across the complete batch" "IR_RECURSIVE_CALL_GRAPH" (fun () ->
        FlowLowering.compileBatchWords context [ cycleA; cycleB ] |> ignore)

    let vectorProducer = added "word batch.split(value: Int) -> (Int, String) {\n    effects none\n    return(value, \"label\")\n}" "batch-split"
    let vectorConsumer = added "word batch.take-first(value: Int) -> Int {\n    effects none\n    let (number, label) = batch::split(value);\n    number\n}" "batch-take-first"
    let vectors = FlowLowering.compileBatchWords context [ vectorConsumer; vectorProducer ]
    let invokeVector =
        Compiler.compileIrBodyAgainstProgramWithSourceOrigins
            vectors.Context.CompilerContext vectors.Program "invoke-vector" []
            [ Push(LInt 17L, sourceSpan); Call("batch.take-first", sourceSpan) ] vectors.Context.SourceOrigins
    equal "batch vector outputs destructure positionally from forward producers" [ IntValue 17L ]
        (IrInterpreter.executeBody (host (ResizeArray())) "invoke-vector" invokeVector)
    let badVectorConsumer = added "word batch.vector-as-scalar(value: Int) -> Int {\n    effects none\n    batch::split(value)\n}" "batch-vector-as-scalar"
    expectLanguageError "a multi-output batch word cannot be used in a scalar expression position" "FLOW_CALL_OUTPUT_ARITY" (fun () ->
        FlowLowering.compileBatchWords context [ vectorProducer; badVectorConsumer ] |> ignore)

    let rich = richTypeContext []
    let badValidator: FlowLowering.FlowWordChange =
        { Definition = parseWord "word email.valid?(value: String) -> String {\n    effects none\n    string::trim(value)\n}"
          RevisionIntent = FlowLowering.FlowWordRevisionIntent.Replace(WordId "user-email.valid?", 1, 2) }
    expectLanguageError "aggregate compilation rechecks record and scalar validators" "TYPE_VALIDATOR_SIGNATURE" (fun () ->
        FlowLowering.compileBatchWords rich [ badValidator ] |> ignore)

    let firstWord = parseWord "word batch.replace(value: Int) -> Int {\n    effects none\n    value\n}"
    let retainedWord = parseWord "word batch.retained(value: Int) -> Int {\n    effects none\n    add(value, 1)\n}"
    let first = FlowLowering.compileWord context (WordId "batch-replace") firstWord
    let second = FlowLowering.compileWord first.Context (WordId "batch-retained") retainedWord
    let oldMarkers = first.Lowered.Projection.SyntheticOrigins |> Map.toSeq |> Seq.map fst |> Set.ofSeq
    let retainedMarkers = second.Lowered.Projection.SyntheticOrigins |> Map.toSeq |> Seq.map fst |> Set.ofSeq
    let oldOriginKeys = second.Context.SourceOrigins |> Map.toSeq |> Seq.map fst |> Set.ofSeq
    let replacement: FlowLowering.FlowWordChange =
        { Definition = parseWord "word batch.replace(value: Int) -> Int {\n    effects none\n    add(value, 2)\n}"
          RevisionIntent = FlowLowering.FlowWordRevisionIntent.Replace(WordId "batch-replace", 0, 1) }
    let compiled = FlowLowering.compileBatchWords second.Context [ replacement ]
    let replacementMarkers = compiled.LoweredWords.Head.Projection.SyntheticOrigins |> Map.toSeq |> Seq.map fst |> Set.ofSeq
    let finalOriginKeys = compiled.Context.SourceOrigins |> Map.toSeq |> Seq.map fst |> Set.ofSeq
    check "replacement pruning removes exactly old body markers and retains untouched markers"
        (Set.isSubset retainedMarkers finalOriginKeys
         && Set.isEmpty(Set.intersect oldMarkers finalOriginKeys)
         && Set.isSubset (Set.difference oldOriginKeys oldMarkers) finalOriginKeys)
    check "new batch markers are disjoint from every input marker"
        (Set.isEmpty(Set.intersect oldOriginKeys replacementMarkers))
    check "successful final compile proves the retained origin map has exactly final IR markers" (not finalOriginKeys.IsEmpty)

    let missingMarker = second.Context.SourceOrigins |> Map.toSeq |> Seq.head |> fst
    let missingOrigin = { second.Context with SourceOrigins = Map.remove missingMarker second.Context.SourceOrigins }
    expectLanguageError "missing input origins fail before a replacement can hide them" "IR_SOURCE_ORIGIN_MISSING" (fun () ->
        FlowLowering.compileBatchWords missingOrigin [ replacement ] |> ignore)
    let extraMarker = { File = "<stale>"; Line = 1; Column = Int32.MaxValue; Length = 0 }
    let extraOrigin = { File = "<stale-origin>"; Line = 1; Column = 1; Length = 1 }
    let staleOrigin = { second.Context with SourceOrigins = Map.add extraMarker extraOrigin second.Context.SourceOrigins }
    expectLanguageError "stale input origins fail before replacement pruning" "IR_SOURCE_ORIGIN_SET_MISMATCH" (fun () ->
        FlowLowering.compileBatchWords staleOrigin [ replacement ] |> ignore)
    let invalidOwnedOrigin =
        let marker = oldMarkers |> Set.toList |> List.head
        let invalidSpan = { File = ""; Line = 0; Column = 0; Length = 0 }
        { second.Context with SourceOrigins = Map.add marker invalidSpan second.Context.SourceOrigins }
    expectLanguageError "invalid origin values on replaced words cannot be pruned away" "IR_SOURCE_ORIGIN_INVALID" (fun () ->
        FlowLowering.compileBatchWords invalidOwnedOrigin [ replacement ] |> ignore)

let private testFlowCallBindingSources () =
    let transientContext =
        loweringContext
            [ wordEntry "left.value" [] [ TInt ] Set.empty [ Push(LInt 1L, sourceSpan) ]
              wordEntry "right.value" [] [ TInt ] Set.empty [ Push(LInt 2L, sourceSpan) ] ]
            Map.empty
    let duplicateSpan = span "<host-flow-ast>" 4 7 12
    let transientWord: FlowWordDefinition =
        { Name = "call-binding.same-span"
          Parameters = []
          Outputs = [ TInt ]
          Effects = Set.empty
          EffectsDeclared = true
          Documentation = ""
          Body =
            [ FlowStatement.Evaluate(FlowExpression.Call("left.value", [], duplicateSpan))
              FlowStatement.Return([ FlowExpression.Call("right.value", [], duplicateSpan) ], duplicateSpan) ]
          SourceText = "<host-constructed Flow AST>"
          Span = span "<host-flow-ast>" 1 1 24
          SyntaxVersion = 1 }
    let transient = FlowLowering.compileWordWithCallBindings transientContext (WordId "call-binding-same-span") transientWord
    equal "same-span host AST calls retain separate structural paths" 2 (transient.CallSites |> List.map (fun site -> site.Path) |> Set.ofList |> Set.count)
    equal "same-span host AST calls preserve both source spans" [ duplicateSpan; duplicateSpan ] (transient.CallSites |> List.map (fun site -> site.Span))
    equal "same-span host AST calls bind to their distinct stable identities"
        [ FlowCallTargetIdentity.UserWord(WordId "user-left.value"); FlowCallTargetIdentity.UserWord(WordId "user-right.value") ]
        (transient.CallSites |> List.map (fun site -> site.Target))
    equal "same-span evaluation and return paths remain distinguishable"
        [ FlowAstPath.FlowAstPath [ FlowAstPathSegment.BlockStatement 0; FlowAstPathSegment.EvaluateExpression ]
          FlowAstPath.FlowAstPath [ FlowAstPathSegment.BlockStatement 1; FlowAstPathSegment.ReturnOutput 0 ] ]
        (transient.CallSites |> List.map (fun site -> site.Path))

    let scaleSource =
        """word library.scale(value: Int) -> Int {
    effects none
    add(value, 1)
}"""
    let callerSource =
        """word client.read(value: Int) -> Int {
    effects none
    library::scale(value)
}"""
    let emptyContext = loweringContext [] Map.empty
    let change (source: string) (identity: string) : FlowLowering.FlowWordChange =
        { Definition = parseWord source
          RevisionIntent = FlowLowering.FlowWordRevisionIntent.Add(WordId identity, 1) }
    let seeded =
        FlowLowering.compileBatchWords emptyContext
            [ change scaleSource "library-scale"
              change callerSource "client-read" ]
    let persistentScale =
        { seeded.Context.CompilerContext.Words["library.scale"] with
            Status = Persistent
            Maturity = LibraryWord
            Definition =
                { seeded.Context.CompilerContext.Words["library.scale"].Definition with
                    Maturity = LibraryWord } }
    let baseContext =
        { seeded.Context with
            CompilerContext =
                { seeded.Context.CompilerContext with
                    Words = Map.add "library.scale" persistentScale seeded.Context.CompilerContext.Words } }
    let inventory = flowInventory baseContext [ "library.scale"; "client.read" ]
    let oldCallerSource = inventory.Sources |> List.find (fun source -> source.OwnerName = "client.read")
    let changedScale =
        authoredFlowSource (WordId "library-scale") 2
            """word library.scale(value: Int) -> Int {
    effects none
    add(value, 2)
}"""
    let replacement: FlowSourceChange =
        { RevisionIntent = FlowWordRevisionIntent.Replace(WordId "library-scale", 1, 2)
          Source = changedScale }
    let rebound =
        FlowLowering.compileBatchFlowSources baseContext inventory [ replacement ]
    let retainedBinding = rebound.CallBindings |> List.find (fun binding -> binding.OwnerName = "client.read")
    equal "retained call binds to its unchanged stable target ID"
        (FlowCallTargetIdentity.UserWord(WordId "library-scale")) retainedBinding.Site.Target
    equal "retained call accepts a target revision advance with the same identity" (Some 2) retainedBinding.Site.TargetRevision
    equal "retained owner revision remains unchanged" 1 retainedBinding.OwnerRevision
    equal "retained binding records the exact old caller SourceRef" oldCallerSource.Reference retainedBinding.Source
    let replacedEntry = rebound.Context.CompilerContext.Words["library.scale"]
    equal "source-backed replacement preserves the old stable ID" (WordId "library-scale") rebound.Context.CompilerContext.WordIds["library.scale"]
    equal "source-backed replacement preserves persistent status" Persistent replacedEntry.Status
    equal "source-backed replacement preserves library maturity" LibraryWord replacedEntry.Maturity
    equal "source-backed replacement advances entry and definition revisions together" (2, 2) (replacedEntry.Revision, replacedEntry.Definition.Revision)
    check "bound result includes unchanged and changed Flow owners"
        (rebound.LoweredWords |> List.map (fun word -> word.Definition.Name) |> Set.ofList = Set.ofList [ "library.scale"; "client.read" ])
    check "source-backed result includes final origins for retained owners"
        (rebound.SiteOrigins |> Map.exists (fun _ source -> source.File = "<flow-test>"))

    let validNewSource =
        authoredFlowSource (WordId "new-source-id") 1
            """word new.branch(value: Int) -> Int {
    effects none
    add(value, 1)
}"""
    let newChange: FlowSourceChange =
        { RevisionIntent = FlowWordRevisionIntent.Add(WordId "new-source-id", 1)
          Source = validNewSource }
    let runWith inventoryValue sourceChange =
        FlowLowering.compileBatchFlowSources baseContext inventoryValue [ sourceChange ] |> ignore
    expectLanguageError "source inventory rejects a missing host-declared Flow owner" "FLOW_SOURCE_INVENTORY_INCOMPLETE" (fun () ->
        runWith { inventory with Sources = inventory.Sources |> List.tail } newChange)
    expectLanguageError "source inventory rejects duplicate owner IDs" "FLOW_SOURCE_INVENTORY_DUPLICATE_ID" (fun () ->
        runWith { inventory with Sources = inventory.Sources @ [ inventory.Sources.Head ] } newChange)
    let badKindSource =
        { inventory.Sources.Head with
            Reference = { inventory.Sources.Head.Reference with Kind = StorageObjectKind.TestDefinition } }
    expectLanguageError "source inventory requires the WordDefinition source kind" "FLOW_SOURCE_KIND_MISMATCH" (fun () ->
        runWith { inventory with Sources = badKindSource :: inventory.Sources.Tail } newChange)
    let badBaseHash =
        { inventory.Sources.Head with
            Reference = { inventory.Sources.Head.Reference with Hash = String.replicate 64 "0" } }
    expectLanguageError "source inventory verifies exact content hashes" "FLOW_SOURCE_HASH_MISMATCH" (fun () ->
        runWith { inventory with Sources = badBaseHash :: inventory.Sources.Tail } newChange)
    let badOwnerName = { inventory.Sources.Head with OwnerName = "wrong.owner" }
    expectLanguageError "source inventory parses bytes and matches the declared owner name" "FLOW_SOURCE_OWNER_NAME_MISMATCH" (fun () ->
        runWith { inventory with Sources = badOwnerName :: inventory.Sources.Tail } newChange)
    let badOwnerRevision = { inventory.Sources.Head with OwnerRevision = 9 }
    expectLanguageError "source inventory checks the current owner revision" "FLOW_SOURCE_OWNER_REVISION_MISMATCH" (fun () ->
        runWith { inventory with Sources = badOwnerRevision :: inventory.Sources.Tail } newChange)
    let wrongFileLabel = { inventory.Sources.Head with SourceFile = "<other-flow-file>" }
    expectLanguageError "source inventory retains the exact source label used by base source spans" "FLOW_SOURCE_FILE_MISMATCH" (fun () ->
        runWith { inventory with Sources = wrongFileLabel :: inventory.Sources.Tail } newChange)
    let validRenameContext =
        { baseContext with
            ParameterNames = Map.add "library.scale" [ "quantity" ] baseContext.ParameterNames }
    equal "semantic metadata fixture keeps valid parameter arity" 1 validRenameContext.ParameterNames["library.scale"].Length
    expectLanguageError "source proof rejects valid same-arity but different parameter metadata" "FLOW_SOURCE_PARAMETER_METADATA_MISMATCH" (fun () ->
        FlowLowering.compileBatchFlowSources validRenameContext inventory [ newChange ] |> ignore)
    let missingParameterContext =
        { baseContext with ParameterNames = Map.remove "library.scale" baseContext.ParameterNames }
    expectLanguageError "source proof rejects missing named-parameter metadata" "FLOW_SOURCE_PARAMETER_METADATA_MISSING" (fun () ->
        FlowLowering.compileBatchFlowSources missingParameterContext inventory [ newChange ] |> ignore)
    let originalScaleEntry = baseContext.CompilerContext.Words["library.scale"]
    let originalAddSpans =
        originalScaleEntry.Definition.Body
        |> List.choose (function | Call("add", callSpan) -> Some callSpan | _ -> None)
    equal "semantic body fixture starts with one add operation" 1 originalAddSpans.Length
    let alteredScaleBody =
        originalScaleEntry.Definition.Body
        |> List.map (function | Call("add", callSpan) -> Call("subtract", callSpan) | expression -> expression)
    let alteredSubtractSpans =
        alteredScaleBody
        |> List.choose (function | Call("subtract", callSpan) -> Some callSpan | _ -> None)
    equal "type-correct body mutation preserves the operation source span" originalAddSpans alteredSubtractSpans
    check "body mutation changes semantics while retaining authored metadata" (alteredScaleBody <> originalScaleEntry.Definition.Body)
    let alteredScaleEntry =
        { originalScaleEntry with
            Definition = { originalScaleEntry.Definition with Body = alteredScaleBody } }
    let alteredBodyContext =
        { baseContext with
            CompilerContext =
                { baseContext.CompilerContext with
                    Words = Map.add "library.scale" alteredScaleEntry baseContext.CompilerContext.Words } }
    equal "body mutation leaves exact base source origins unchanged" baseContext.SourceOrigins alteredBodyContext.SourceOrigins
    let alteredBodyProgram = Compiler.compileIrProgramWithSourceOrigins alteredBodyContext.CompilerContext alteredBodyContext.SourceOrigins
    let alteredScaleCalls =
        VerifiedIrProgram.inspect alteredBodyProgram
        |> fun program -> program.FunctionsById[WordId "library-scale"].FunctionBody
        |> resolvedCallsInBlock
    check "mutated base body still compiles as a type-correct program"
        (alteredScaleCalls |> List.exists (fun call -> call.ResolvedName = "subtract"))
    expectLanguageError "source proof rejects a type-correct verified base body that differs from its source" "FLOW_SOURCE_BODY_MISMATCH" (fun () ->
        FlowLowering.compileBatchFlowSources alteredBodyContext inventory [ newChange ] |> ignore)
    let changedBaseBytes =
        { inventory.Sources.Head with
            Content = inventory.Sources.Head.Content.Replace("add(value, 1)", "add(value, 3)", StringComparison.Ordinal)
            Reference =
                (Storage.sourceObject StorageObjectKind.WordDefinition
                    (inventory.Sources.Head.Content.Replace("add(value, 1)", "add(value, 3)", StringComparison.Ordinal))).Reference }
    expectLanguageError "source inventory cannot substitute different bytes for the retained base definition" "FLOW_SOURCE_TEXT_MISMATCH" (fun () ->
        runWith { inventory with Sources = changedBaseBytes :: inventory.Sources.Tail } newChange)
    let invalidUtf16 =
        { validNewSource with
            Content = validNewSource.Content + string (char 0xD800)
            Reference = { validNewSource.Reference with Hash = String.replicate 64 "0" } }
    expectLanguageError "source hash validation rejects unpaired UTF-16 instead of replacement-encoding it" "FLOW_SOURCE_UTF8_INVALID" (fun () ->
        FlowLowering.compileBatchFlowSources baseContext inventory [ { newChange with Source = invalidUtf16 } ] |> ignore)
    let badChangeHash =
        { validNewSource with Reference = { validNewSource.Reference with Hash = String.replicate 64 "0" } }
    expectLanguageError "new source bytes must match the supplied content-addressed reference" "FLOW_SOURCE_HASH_MISMATCH" (fun () ->
        FlowLowering.compileBatchFlowSources baseContext inventory [ { newChange with Source = badChangeHash } ] |> ignore)
    let changeIdMismatch =
        { newChange with RevisionIntent = FlowWordRevisionIntent.Add(WordId "different-id", 1) }
    expectLanguageError "new source owner IDs must match host revision intent" "FLOW_SOURCE_CHANGE_REVISION_MISMATCH" (fun () ->
        FlowLowering.compileBatchFlowSources baseContext inventory [ changeIdMismatch ] |> ignore)

    let stackTarget = wordEntry "math.bump" [ TInt ] [ TInt ] Set.empty [ Call("int.abs", sourceSpan) ]
    let dotCallerSource =
        """word client.dot(value: Int) -> Int {
    effects none
    value.bump()
}"""
    let dotBase =
        FlowLowering.compileBatchWords (loweringContext [ stackTarget ] Map.empty)
            [ change dotCallerSource "client-dot" ]
    let dotInventory = flowInventory dotBase.Context [ "client.dot" ]
    let secondBump =
        authoredFlowSource (WordId "other-bump") 1
            """word other.bump(value: Int) -> Int {
    effects none
    add(value, 1)
}"""
    let ambiguous = captureLanguageError "retained dot call rejects a new candidate that makes it ambiguous" "FLOW_AMBIGUOUS_DOT_STAGE" (fun () ->
        FlowLowering.compileBatchFlowSources dotBase.Context dotInventory
            [ { RevisionIntent = FlowWordRevisionIntent.Add(WordId "other-bump", 1); Source = secondBump } ] |> ignore)
    equal "retained dot ambiguity names the unchanged caller" (Some "client.dot") ambiguous.Word
    check "retained dot ambiguity retains its source span" ambiguous.Span.IsSome
    check "retained dot ambiguity reports the structural Flow AST path" (ambiguous.Message.Contains("Flow AST path(s):", StringComparison.Ordinal))

    let redirectionBase =
        FlowLowering.compileBatchWords emptyContext
            [ change
                """word domain.answer() -> Int {
    effects none
    41
}"""
                "domain-answer"
              change
                """word client.use-answer() -> Int {
    effects none
    answer()
}"""
                "client-answer" ]
    let redirectionInventory = flowInventory redirectionBase.Context [ "domain.answer"; "client.use-answer" ]
    let exactAnswer =
        authoredFlowSource (WordId "exact-answer") 1
            """word answer() -> Int {
    effects none
    42
}"""
    let narrowedDomainAnswer =
        authoredFlowSource (WordId "domain-answer") 2
            """word domain.answer(unused: Bool) -> Int {
    effects none
    41
}"""
    let redirectionChanges: FlowSourceChange list =
        [ { RevisionIntent = FlowWordRevisionIntent.Replace(WordId "domain-answer", 1, 2)
            Source = narrowedDomainAnswer }
          { RevisionIntent = FlowWordRevisionIntent.Add(WordId "exact-answer", 1)
            Source = exactAnswer } ]
    let redirection = captureLanguageError "retained ordinary short call rejects a same-signature stable-ID redirect" "FLOW_CALL_REBOUND" (fun () ->
        FlowLowering.compileBatchFlowSources redirectionBase.Context redirectionInventory redirectionChanges |> ignore)
    equal "ordinary call redirection identifies its unchanged caller" (Some "client.use-answer") redirection.Word
    check "ordinary call redirection retains an authored call span" redirection.Span.IsSome
    let redirectDetails = String.concat " " (redirection.Expected @ redirection.Actual)
    check "ordinary call redirection reports its structural Flow path" (redirectDetails.Contains("FlowAstPath", StringComparison.Ordinal))
    check "ordinary call redirection names both stable target identities"
        (redirectDetails.Contains("domain-answer", StringComparison.Ordinal)
         && redirectDetails.Contains("exact-answer", StringComparison.Ordinal))

    let rootBase =
        FlowLowering.compileBatchWords emptyContext
            [ change
                """word identity(value: Int) -> Int {
    effects none
    value
}"""
                "root-identity-target"
              change
                """word domain.identity(value: Int) -> Int {
    effects none
    value
}"""
                "domain-identity-target"
              change
                """word client.root(value: Int) -> Int {
    effects none
    ::identity(value)
}"""
                "client-root" ]
    let rootInventory = flowInventory rootBase.Context [ "identity"; "domain.identity"; "client.root" ]
    let newlyAddedSuffix =
        authoredFlowSource (WordId "other-identity-target") 1
            """word other.identity(value: Int) -> Int {
    effects none
    add(value, 1)
}"""
    let rootStable =
        FlowLowering.compileBatchFlowSources rootBase.Context rootInventory
            [ { RevisionIntent = FlowWordRevisionIntent.Add(WordId "other-identity-target", 1); Source = newlyAddedSuffix } ]
    let rootBinding = rootStable.CallBindings |> List.find (fun binding -> binding.OwnerName = "client.root")
    equal "absolute-root resolution stays bound to its exact key as suffix candidates grow"
        (FlowCallTargetIdentity.UserWord(WordId "root-identity-target")) rootBinding.Site.Target
    equal "absolute-root target revision remains exact" (Some 1) rootBinding.Site.TargetRevision

    let legacyTarget = wordEntry "legacy.operate" [ TInt ] [ TInt ] Set.empty [ Call("int.abs", sourceSpan) ]
    let stackCaller = wordEntry "legacy.caller" [ TInt ] [ TInt ] Set.empty [ Call("legacy.operate", sourceSpan) ]
    let stackBase = loweringContext [ legacyTarget; stackCaller ] (Map.ofList [ "legacy.operate", [ "value" ]; "legacy.caller", [ "value" ] ])
    let flowReplacement =
        authoredFlowSource stackBase.CompilerContext.WordIds["legacy.operate"] 2
            """word legacy.operate(value: Int) -> Int {
    effects none
    add(value, 5)
}"""
    let migrated =
        FlowLowering.compileBatchFlowSources stackBase
            { ExpectedFlowOwnerIds = Set.empty; Sources = [] }
            [ { RevisionIntent = FlowWordRevisionIntent.Replace(stackBase.CompilerContext.WordIds["legacy.operate"], 1, 2)
                Source = flowReplacement } ]
    let migratedEntry = migrated.Context.CompilerContext.Words["legacy.operate"]
    equal "Stack-to-Flow replacement preserves stable ID" stackBase.CompilerContext.WordIds["legacy.operate"] migrated.Context.CompilerContext.WordIds["legacy.operate"]
    equal "Stack-to-Flow replacement keeps entry status" Persistent migratedEntry.Status
    equal "Stack-to-Flow replacement advances the definition revision" 2 migratedEntry.Definition.Revision
    let stackCallerCall =
        VerifiedIrProgram.inspect migrated.Program
        |> fun program -> program.FunctionsById[stackBase.CompilerContext.WordIds["legacy.caller"]].FunctionBody
        |> resolvedCallsInBlock
        |> List.find (fun call -> call.ResolvedName = "legacy.operate")
    equal "retained Stack caller is verified against the migrated Flow target revision"
        (UserWordTarget(stackBase.CompilerContext.WordIds["legacy.operate"], 2)) stackCallerCall.ResolvedTarget
    check "Stack callers without Flow source documents receive no invented Flow binding"
        (migrated.CallBindings |> List.forall (fun binding -> binding.OwnerName <> "legacy.caller"))

let private testFlowCallBindingStructuralPaths () =
    let helperSources =
        [ ("binding.identity", """word binding.identity(value: Int) -> Int {
    effects none
    value
}""", "binding-identity")
          ("binding.flag", """word binding.flag() -> Bool {
    effects none
    true
}""", "binding-flag")
          ("binding.option", """word binding.option() -> Option<Int> {
    effects none
    option::some<Int>(1)
}""", "binding-option")
          ("binding.result", """word binding.result() -> Result<Int, String> {
    effects none
    result::ok<Int, String>(1)
}""", "binding-result")
          ("binding.multi", """word binding.multi() -> (Int, String) {
    effects none
    return (1, "label")
}""", "binding-multi")
          ("binding.positive?", """word binding.positive?(value: Int) -> Bool {
    effects none
    true
}""", "binding-positive")
          ("binding.ignore", """word binding.ignore(value: Int) -> Unit {
    effects none
    unit
}""", "binding-ignore")
          ("binding.bump", """word binding.bump(value: Int, amount: Int) -> Int {
    effects none
    add(value, amount)
}""", "binding-bump")
          ("identity", """word identity(value: Int) -> Int {
    effects none
    value
}""", "root-identity") ]
    let emptyFlowContext =
        loweringContext [] (Map.ofList [ "add", [ "left"; "right" ] ])
    let helperChanges: FlowLowering.FlowWordChange list =
        helperSources
        |> List.map (fun (_, source, identity) ->
            { Definition = parseWord source
              RevisionIntent = FlowLowering.FlowWordRevisionIntent.Add(WordId identity, 1) })
    let seeded = FlowLowering.compileBatchWords emptyFlowContext helperChanges
    let coverageSource =
        """word binding.coverage(value: Int, values: List<Int>) -> (Int, Int, Int) {
    effects none
    let (first, label) = binding::multi();
    let wrapped = option::some<Int>(binding::identity(value));
    let branch = if binding::flag() { binding::identity(value) } else { binding::identity(first) };
    let option-value = match binding::option() { some item => { binding::identity(item) } none => { binding::identity(first) } };
    let result-value = match binding::result() { ok item => { binding::identity(item) } error message => { binding::identity(first) } };
    let mapped = values.map(binding::identity);
    let filtered = mapped.filter(binding::positive?);
    filtered.each(binding::ignore);
    let bumped = binding::identity(value).bump(amount = binding::identity(first));
    let summed = binding::bump(first, binding::identity(value));
    let rooted = ::identity(::identity(value));
    let root-mapped = values.map(::identity);
    return (binding::identity(branch), binding::identity(option-value), binding::identity(result-value))
}"""
    let coverageChange: FlowSourceChange =
        { RevisionIntent = FlowWordRevisionIntent.Add(WordId "binding-coverage", 1)
          Source = authoredFlowSource (WordId "binding-coverage") 1 coverageSource }
    let inventory = flowInventory seeded.Context (helperSources |> List.map (fun (name, _, _) -> name))
    let bound = FlowLowering.compileBatchFlowSources seeded.Context inventory [ coverageChange ]
    let coverageBindings = bound.CallBindings |> List.filter (fun binding -> binding.OwnerName = "binding.coverage")
    let segments =
        coverageBindings
        |> List.collect (fun binding ->
            let (FlowAstPath.FlowAstPath path) = binding.Site.Path
            path)
        |> Set.ofList
    for expected in
        [ FlowAstPathSegment.DestructureInitializer
          FlowAstPathSegment.ContainerPayload
          FlowAstPathSegment.IfCondition
          FlowAstPathSegment.IfThenStatement 0
          FlowAstPathSegment.IfElseStatement 0
          FlowAstPathSegment.OptionScrutinee
          FlowAstPathSegment.OptionSomeStatement 0
          FlowAstPathSegment.OptionNoneStatement 0
          FlowAstPathSegment.ResultScrutinee
          FlowAstPathSegment.ResultOkStatement 0
          FlowAstPathSegment.ResultErrorStatement 0
          FlowAstPathSegment.DotReceiver
          FlowAstPathSegment.DotArgument 0
          FlowAstPathSegment.CallArgument 1
          FlowAstPathSegment.RootCallArgument 0
          FlowAstPathSegment.ReturnOutput 0
          FlowAstPathSegment.ReturnOutput 1
          FlowAstPathSegment.ReturnOutput 2 ] do
        check ($"binding capture covers AST path role {expected}") (segments.Contains expected)
    for callbackName in [ "binding.identity"; "binding.positive?"; "binding.ignore"; "identity" ] do
        let sites =
            coverageBindings
            |> List.filter (fun binding ->
                match binding.Site.Form with
                | FlowCallForm.StaticCallback(_, _) -> binding.Site.RequestedName = callbackName
                | _ -> false)
        check ($"callback binding is captured for {callbackName}") (not (List.isEmpty sites))
        for site in sites do
            check ($"callback source span is retained for {callbackName}")
                (site.Site.Span.File = "<flow-test>" && site.Site.Span.Line > 0 && site.Site.Span.Column > 0 && site.Site.Span.Length > 0)
        let expectedTarget =
            match callbackName with
            | "binding.identity" -> FlowCallTargetIdentity.UserWord(WordId "binding-identity")
            | "binding.positive?" -> FlowCallTargetIdentity.UserWord(WordId "binding-positive")
            | "binding.ignore" -> FlowCallTargetIdentity.UserWord(WordId "binding-ignore")
            | "identity" -> FlowCallTargetIdentity.UserWord(WordId "root-identity")
            | _ -> failwithf "Unexpected callback fixture: %s" callbackName
        check ($"callback resolves to exact stable identity {callbackName}")
            (sites |> List.forall (fun site -> site.Site.Target = expectedTarget))
    let rootCall =
        coverageBindings
        |> List.find (fun binding -> binding.Site.Form = FlowCallForm.AbsoluteRoot)
    equal "absolute-root argument keeps its exact path" true
        (let (FlowAstPath.FlowAstPath path) = rootCall.Site.Path
         path |> List.contains (FlowAstPathSegment.RootCallArgument 0))
    let dotCall = coverageBindings |> List.find (fun binding -> match binding.Site.Form with | FlowCallForm.DotStage "bump" -> true | _ -> false)
    equal "dot stage resolves to its exact stable target" (FlowCallTargetIdentity.UserWord(WordId "binding-bump")) dotCall.Site.Target
    let (FlowAstPath.FlowAstPath dotParentPath) = dotCall.Site.Path
    let receiverPath = FlowAstPath.FlowAstPath(dotParentPath @ [ FlowAstPathSegment.DotReceiver ])
    let namedArgumentPath = FlowAstPath.FlowAstPath(dotParentPath @ [ FlowAstPathSegment.DotArgument 0 ])
    let receiverSite =
        coverageBindings
        |> List.find (fun binding -> binding.Site.Path = receiverPath)
    let namedArgumentSite =
        coverageBindings
        |> List.find (fun binding -> binding.Site.Path = namedArgumentPath)
    equal "named dot receiver selects the receiver-side word" (FlowCallTargetIdentity.UserWord(WordId "binding-identity")) receiverSite.Site.Target
    equal "named dot argument selects the written argument word" (FlowCallTargetIdentity.UserWord(WordId "binding-identity")) namedArgumentSite.Site.Target
    let dotStagePosition = coverageBindings |> List.findIndex (fun binding -> binding = dotCall)
    let receiverPosition = coverageBindings |> List.findIndex (fun binding -> binding = receiverSite)
    let argumentPosition = coverageBindings |> List.findIndex (fun binding -> binding = namedArgumentSite)
    check "named dot calls reconcile receiver before nested written argument and outer stage"
        (receiverPosition < argumentPosition && argumentPosition < dotStagePosition)

    let scalarContext = richTypeContext []
    let scalarSource =
        """word email.wrap(value: String) -> Email {
    effects none
    Email::new(value)
}"""
    let scalarChange: FlowSourceChange =
        { RevisionIntent = FlowWordRevisionIntent.Add(WordId "email-wrap", 1)
          Source = authoredFlowSource (WordId "email-wrap") 1 scalarSource }
    let scalarBound =
        FlowLowering.compileBatchFlowSources scalarContext { ExpectedFlowOwnerIds = Set.empty; Sources = [] } [ scalarChange ]
    let scalarSites = scalarBound.CallBindings |> List.filter (fun binding -> binding.OwnerName = "email.wrap")
    equal "scalar constructor's implicit validator creates no extra authored binding" 1 scalarSites.Length
    equal "scalar wrapper binds to its generated constructor identity" (FlowCallTargetIdentity.GeneratedWord(WordId "generated-Email.new")) scalarSites.Head.Site.Target
    equal "scalar constructor binding records the source call once" FlowCallForm.Direct scalarSites.Head.Site.Form

    let recordSource =
        """word record.identity(value: Email) -> Email {
    effects none
    let customer = customer::new(email = value);
    customer.email()
}"""
    let recordChange: FlowSourceChange =
        { RevisionIntent = FlowWordRevisionIntent.Add(WordId "record-identity", 1)
          Source = authoredFlowSource (WordId "record-identity") 1 recordSource }
    let recordBound =
        FlowLowering.compileBatchFlowSources scalarContext { ExpectedFlowOwnerIds = Set.empty; Sources = [] } [ recordChange ]
    let recordSites = recordBound.CallBindings |> List.filter (fun binding -> binding.OwnerName = "record.identity")
    equal "Flow call sidecar captures generated record constructor and accessor" 2 recordSites.Length
    check "generated record constructor has its stable generated ID"
        (recordSites |> List.exists (fun binding -> binding.Site.Target = FlowCallTargetIdentity.GeneratedWord(WordId "generated-customer.new")))
    check "generated record accessor has its stable generated ID"
        (recordSites |> List.exists (fun binding -> binding.Site.Target = FlowCallTargetIdentity.GeneratedWord(WordId "generated-customer.email")))

let private testFlowAttachmentCallBindings () =
    let ownerId = WordId "attachment-owner"
    let answerId = WordId "domain-answer"
    let storefrontId = WordId "storefront-select"
    let identityId = WordId "binding-identity"
    let ignoreId = WordId "binding-ignore"
    let divideId = WordId "attachment-divide"
    let emailId = WordId "storefront-email"
    let suffixId = WordId "domain-suffix"
    let pickId = WordId "domain-pick"
    let emptyWordInventory: FlowLowering.FlowSourceInventory = { ExpectedFlowOwnerIds = Set.empty; Sources = [] }
    let addWord (identity: WordId) (source: string) : FlowLowering.FlowSourceChange =
        { RevisionIntent = FlowLowering.FlowWordRevisionIntent.Add(identity, 1); Source = authoredFlowSource identity 1 source }
    let replaceWord (identity: WordId) (oldRevision: int) (newRevision: int) (source: string) : FlowLowering.FlowSourceChange =
        { RevisionIntent = FlowLowering.FlowWordRevisionIntent.Replace(identity, oldRevision, newRevision)
          Source = authoredFlowSource identity newRevision source }
    let wordNames =
        [ "attachment.owner"; "domain.answer"; "storefront.select"; "binding.identity"; "binding.ignore"
          "attachment.divide"; "storefront.email"; "domain.suffix"; "domain.pick" ]
    let initialWordChanges =
        [ addWord ownerId """word attachment.owner() -> Int {
    effects none
    1
}"""
          addWord answerId """word domain.answer() -> Int {
    effects none
    42
}"""
          addWord storefrontId """word storefront.select(value: Option<Int>) -> Int {
    effects none
    match value {
        some item => { return item }
        none => { return 0 }
    }
}"""
          addWord identityId """word binding.identity(value: Int) -> Int {
    effects none
    value
}"""
          addWord ignoreId """word binding.ignore(value: Int) -> Unit {
    effects none
    unit
}"""
          addWord divideId """word attachment.divide(value: Int) -> Int {
    effects none
    divide(value, 0)
}"""
          addWord emailId """word storefront.email(value: Email) -> String {
    effects none
    Email::value(value)
}"""
          addWord suffixId """word domain.suffix() -> Int {
    effects none
    41
}"""
          addWord pickId """word domain.pick(value: Int) -> Int {
    effects none
    value
}""" ]
    let cases =
        [ FlowAttachmentKind.Test, "test attachment.owner/literal-test {\n    storefront::select(option::some<Int>(7))\n    => 7\n}"
          FlowAttachmentKind.Test, "test attachment.owner/both-roles {\n    domain::answer()\n    => value domain::answer()\n}"
          FlowAttachmentKind.Test, "test attachment.owner/coverage-split {\n    storefront::select(option::some<Int>(0))\n    => value storefront::select(option::none<Int>())\n}"
          FlowAttachmentKind.Test, "test attachment.owner/inline-branch {\n    match option::none<Int>() {\n        some value => { value }\n        none => { 1 }\n    }\n    => 1\n}"
          FlowAttachmentKind.Test, "test attachment.owner/runtime-error {\n    attachment::divide(1)\n    => error RUNTIME_DIVIDE_BY_ZERO\n}"
          FlowAttachmentKind.Test, "test attachment.owner/ordered-calls {\n    list::singleton<Int>(1).each(binding::ignore)\n    if true {\n        return binding::identity(7)\n    } else {\n        return binding::identity(8)\n    }\n    => value binding::identity(7)\n}"
          FlowAttachmentKind.Test, "test attachment.owner/no-calls {\n    3\n    => 3\n}"
          FlowAttachmentKind.Test, "test attachment.owner/scalar-generated {\n    storefront::email(Email::new(\"a@b\"))\n    => value Email::value(Email::new(\"a@b\"))\n}"
          FlowAttachmentKind.Test, "test attachment.owner/suffix-actual {\n    suffix()\n    => 41\n}"
          FlowAttachmentKind.Test, "test attachment.owner/suffix-expected {\n    41\n    => value suffix()\n}"
          FlowAttachmentKind.Test, "test attachment.owner/dot-ambiguity {\n    1.pick()\n    => 1\n}"
          FlowAttachmentKind.Test, "test attachment.owner/dot-ambiguity-expected {\n    1\n    => value 1.pick()\n}"
          FlowAttachmentKind.Example, "example attachment.owner/literal-example {\n    domain::answer()\n    => 42\n}"
          FlowAttachmentKind.Example, "example attachment.owner/dot-ambiguity-example {\n    1.pick()\n    => 1\n}"
          FlowAttachmentKind.Example, "example attachment.owner/empty-example {\n    5\n    => 5\n}" ]
        |> List.map (fun (kind, source) -> authoredFlowAttachment kind ownerId 1 source)
    let inventory = flowAttachmentInventory []
    let initial =
        FlowLowering.compileBatchFlowProjectSources (richTypeContext []) emptyWordInventory initialWordChanges inventory
            (cases |> List.map FlowAttachmentChange.Add)
    let context = initial.WordCompilation.Context
    let wordInventory = flowInventory context wordNames
    let sources =
        initial.Attachments
        |> List.map (function | FlowCompiledAttachment.Test(source, _) | FlowCompiledAttachment.Example(source, _) -> source)
    let attachmentInventory = flowAttachmentInventory sources
    let testCase (compiled: FlowLowering.FlowBoundProjectCompilation) caseName =
        compiled.Attachments |> List.pick (function | FlowCompiledAttachment.Test(source, body) when source.CaseName = caseName -> Some(source, body) | _ -> None)
    let exampleCase (compiled: FlowLowering.FlowBoundProjectCompilation) caseName =
        compiled.Attachments |> List.pick (function | FlowCompiledAttachment.Example(source, body) when source.CaseName = caseName -> Some(source, body) | _ -> None)
    let roleBindings (compiled: FlowLowering.FlowBoundProjectCompilation) caseName role =
        compiled.AttachmentBindings |> List.filter (fun binding -> binding.Attachment.CaseName = caseName && binding.BodyRole = role)
    let actualBindings compiled caseName = roleBindings compiled caseName FlowAttachmentBodyRole.Actual
    let expectedBindings compiled caseName = roleBindings compiled caseName FlowAttachmentBodyRole.ExpectedExpression

    equal "source-backed test and example inventory is assembled" cases.Length initial.Attachments.Length
    let (_, literalTest) = testCase initial "literal-test"
    equal "source-backed literal test executes" [ IntValue 7L ] (IrInterpreter.executeBody (host (ResizeArray())) "attached-literal" literalTest.Body)
    check "literal expectation has no detached expression body" literalTest.ExpectationBody.IsNone
    let (bothDocument, bothTest) = testCase initial "both-roles"
    equal "source-backed actual expression executes" [ IntValue 42L ] (IrInterpreter.executeBody (host (ResizeArray())) "attached-actual" bothTest.Body)
    let bothExpected = bothTest.ExpectationBody |> Option.defaultWith (fun () -> failwith "Expected a detached value expression.")
    equal "source-backed expected expression executes separately" [ IntValue 42L ] (IrInterpreter.executeBody (host (ResizeArray())) "attached-expected" bothExpected)
    let actual = actualBindings initial "both-roles"
    let expected = expectedBindings initial "both-roles"
    equal "actual and expected roles each bind their authored root call" (1, 1) (actual.Length, expected.Length)
    equal "actual call retains the structural path through its body statement"
        (FlowAstPath.FlowAstPath [ FlowAstPathSegment.BlockStatement 0; FlowAstPathSegment.EvaluateExpression ]) actual.Head.Site.Path
    equal "expected expression retains its independent expression-root path"
        (FlowAstPath.FlowAstPath []) expected.Head.Site.Path
    equal "actual and expected roles retain distinct body-role keys"
        (FlowAttachmentBodyRole.Actual, FlowAttachmentBodyRole.ExpectedExpression) (actual.Head.BodyRole, expected.Head.BodyRole)
    equal "actual and expected roles retain the same stable attachment key" actual.Head.Attachment expected.Head.Attachment
    equal "each role binds the exact authored source reference" (bothDocument.Reference, bothDocument.Reference) (actual.Head.Source, expected.Head.Source)
    equal "actual and expected roles retain the same stable target identity" actual.Head.Site.Target expected.Head.Site.Target
    equal "initial call binding records the exact candidate target revision" (Some 1) actual.Head.Site.TargetRevision
    check "detached actual IR has no fabricated word owner"
        ((VerifiedIrBody.inspect bothTest.Body).BodySourceMap |> Map.forall (fun _ source -> source.SiteOwner.IsNone))
    check "detached expected IR has no fabricated word owner"
        ((VerifiedIrBody.inspect bothExpected).BodySourceMap |> Map.forall (fun _ source -> source.SiteOwner.IsNone))
    let (_, runtimeErrorTest) = testCase initial "runtime-error"
    let runtimeCode =
        try IrInterpreter.executeBody (host (ResizeArray())) "attached-runtime-error" runtimeErrorTest.Body |> ignore; None
        with LanguageException diagnostic -> Some diagnostic.Code
    equal "runtime-error attachment observes its declared diagnostic" (Some "RUNTIME_DIVIDE_BY_ZERO") runtimeCode
    let (_, noCallTest) = testCase initial "no-calls"
    equal "no-call attachment executes" [ IntValue 3L ] (IrInterpreter.executeBody (host (ResizeArray())) "attached-no-calls" noCallTest.Body)
    equal "no-call test emits no call bindings" [] (initial.AttachmentBindings |> List.filter (fun binding -> binding.Attachment.CaseName = "no-calls"))
    let (_, literalExample) = exampleCase initial "literal-example"
    equal "literal example executes through the source-backed project API" [ IntValue 42L ]
        (IrInterpreter.executeBody (host (ResizeArray())) "attached-example" literalExample.Body)
    let (_, emptyExample) = exampleCase initial "empty-example"
    equal "no-call example executes" [ IntValue 5L ] (IrInterpreter.executeBody (host (ResizeArray())) "attached-empty-example" emptyExample.Body)
    equal "no-call example emits no call bindings" [] (initial.AttachmentBindings |> List.filter (fun binding -> binding.Attachment.CaseName = "empty-example"))

    let program = VerifiedIrProgram.inspect initial.WordCompilation.Program
    let recordedOutcomes (events: ResizeArray<string * SourceSiteId * string>) =
        events |> Seq.choose (fun (name, site, outcome) ->
            match program.SourceMap.TryFind site with
            | Some source when name = "storefront.select" && source.SiteOwner = Some storefrontId -> Some outcome
            | _ -> None) |> Set.ofSeq
    let recordHost (events: ResizeArray<string * SourceSiteId * string>) =
        { host (ResizeArray()) with RecordBranchOutcome = fun name site outcome -> events.Add(name, site, outcome) }
    let (_, coverageTest) = testCase initial "coverage-split"
    let actualEvents = ResizeArray<string * SourceSiteId * string>()
    equal "actual trace exercises the library-owned some branch" [ IntValue 0L ]
        (IrInterpreter.executeBody (recordHost actualEvents) "attached-some" coverageTest.Body)
    equal "actual trace records only its own library some coverage" (Set.singleton "some") (recordedOutcomes actualEvents)
    let expectedEvents = ResizeArray<string * SourceSiteId * string>()
    let coverageExpected = coverageTest.ExpectationBody |> Option.defaultWith (fun () -> failwith "Expected coverage expression.")
    equal "expected trace independently exercises the library-owned none branch" [ IntValue 0L ]
        (IrInterpreter.executeBody (recordHost expectedEvents) "attached-none" coverageExpected)
    equal "expected-expression branches do not contaminate the actual trace" (Set.singleton "none") (recordedOutcomes expectedEvents)
    equal "actual library coverage remains the some branch" (Set.singleton "some") (recordedOutcomes actualEvents)
    let (_, inlineBranchTest) = testCase initial "inline-branch"
    let inlineEvents = ResizeArray<string * SourceSiteId * string>()
    equal "inline detached branch fixture executes through the project attachment API" [ IntValue 1L ]
        (IrInterpreter.executeBody (recordHost inlineEvents) "attached-inline" inlineBranchTest.Body)
    let inlineBody = VerifiedIrBody.inspect inlineBranchTest.Body
    check "same-labelled inline branch is detached from library-owned source sites"
        (inlineEvents |> Seq.exists (fun (_, site, outcome) ->
            outcome = "none" && (inlineBody.BodySourceMap.TryFind site |> Option.exists (fun source -> source.SiteOwner.IsNone))))
    equal "inline branch trace cannot satisfy the library's own none coverage" Set.empty (recordedOutcomes inlineEvents)

    let orderedActual = actualBindings initial "ordered-calls"
    let orderedExpected = expectedBindings initial "ordered-calls"
    check "attachment call inventory includes the authored static callback"
        (orderedActual |> List.exists (fun binding -> match binding.Site.Form with | FlowCallForm.StaticCallback(_, _) -> binding.Site.Target = FlowCallTargetIdentity.UserWord ignoreId | _ -> false))
    let identityPaths = orderedActual |> List.filter (fun binding -> binding.Site.Target = FlowCallTargetIdentity.UserWord identityId) |> List.map (fun binding -> binding.Site.Path)
    let branchKinds =
        identityPaths |> List.collect (fun (FlowAstPath.FlowAstPath path) -> path)
        |> List.choose (function | FlowAstPathSegment.IfThenStatement _ -> Some "then" | FlowAstPathSegment.IfElseStatement _ -> Some "else" | _ -> None)
        |> Set.ofList
    equal "structural attachment paths include both branch calls" (Set.ofList [ "then"; "else" ]) branchKinds
    let orderedPositions = orderedActual |> List.map (fun binding -> binding.Site.Span.Line, binding.Site.Span.Column)
    equal "attachment call events retain authored callback and branch ordering" (List.sort orderedPositions) orderedPositions
    check "expected-expression calls remain in their independent body role" (orderedExpected.Length = 1 && orderedExpected.Head.BodyRole = FlowAttachmentBodyRole.ExpectedExpression)

    let (_, scalarTest) = testCase initial "scalar-generated"
    let constructor = FlowCallTargetIdentity.GeneratedWord(WordId "generated-Email.new")
    for sites in [ actualBindings initial "scalar-generated"; expectedBindings initial "scalar-generated" ] do
        equal "each attachment body role has one authored generated-scalar constructor site" 1
            (sites |> List.filter (fun binding -> binding.Site.Target = constructor) |> List.length)
        check "implicit scalar validators do not appear as authored call bindings"
            (sites |> List.forall (fun binding -> binding.Site.Target <> FlowCallTargetIdentity.UserWord(WordId "user-email.valid?")))
    equal "generated scalar actual call executes" [ StringValue "a@b" ]
        (IrInterpreter.executeBody (host (ResizeArray())) "attached-scalar" scalarTest.Body)
    equal "generated scalar expected call executes separately" [ StringValue "a@b" ]
        (scalarTest.ExpectationBody |> Option.defaultWith (fun () -> failwith "Expected scalar expression.")
         |> IrInterpreter.executeBody (host (ResizeArray())) "attached-scalar-expected")

    let markerSets =
        initial.Attachments |> List.map (function
            | FlowCompiledAttachment.Test(_, compiled) -> compiled.Lowered.Projection.SyntheticOrigins |> Map.toSeq |> Seq.map fst |> Set.ofSeq
            | FlowCompiledAttachment.Example(_, compiled) -> compiled.Lowered.Projection.SyntheticOrigins |> Map.toSeq |> Seq.map fst |> Set.ofSeq)
    let markers = markerSets |> List.collect Set.toList
    check "calls and compound bodies allocate private source markers across attachments"
        (markerSets |> List.filter (Set.isEmpty >> not) |> List.length >= 2)
    let (_, noCallMarkerTest) = testCase initial "no-calls"
    equal "literal-only no-call test needs no synthetic marker allocation" Set.empty
        (noCallMarkerTest.Lowered.Projection.SyntheticOrigins |> Map.toSeq |> Seq.map fst |> Set.ofSeq)
    equal "private source marker allocation is disjoint across test and example cases" markers.Length (markers |> Set.ofList |> Set.count)
    for attachment in initial.Attachments do
        match attachment with
        | FlowCompiledAttachment.Test(source, compiled) ->
            equal "test retains exact authored bytes" source.Content compiled.Lowered.Definition.SourceText
            check "test actual and expected spans resolve only to the authored source file"
                (compiled.BodySiteOrigins |> Map.forall (fun _ span -> span.File = source.SourceFile && span.Column < 1000)
                 && (compiled.ExpectationSiteOrigins |> Option.defaultValue Map.empty |> Map.forall (fun _ span -> span.File = source.SourceFile && span.Column < 1000)))
        | FlowCompiledAttachment.Example(source, compiled) ->
            equal "example retains exact authored bytes" source.Content compiled.Lowered.Definition.SourceText
            check "example sites resolve only to the authored source file"
                (compiled.SiteOrigins |> Map.forall (fun _ span -> span.File = source.SourceFile && span.Column < 1000))

    let sourceProbe = authoredFlowAttachment FlowAttachmentKind.Test ownerId 1 "test attachment.owner/source-probe {\n    domain::answer()\n    => 42\n}"
    let proveWordBase inventoryValue =
        FlowLowering.compileBatchFlowProjectSources context inventoryValue [] attachmentInventory [ FlowAttachmentChange.Add sourceProbe ] |> ignore
    expectLanguageError "attachment-only batch proves a complete base Flow word inventory" "FLOW_SOURCE_INVENTORY_INCOMPLETE" (fun () ->
        proveWordBase { wordInventory with Sources = wordInventory.Sources |> List.tail })
    let answerSource = wordInventory.Sources |> List.find (fun source -> source.OwnerId = answerId)
    let alteredBytes = answerSource.Content.Replace("42", "43", StringComparison.Ordinal)
    let alteredSource = { answerSource with Content = alteredBytes; Reference = (Storage.sourceObject StorageObjectKind.WordDefinition alteredBytes).Reference }
    expectLanguageError "attachment-only batch authenticates the base Flow source against the exact Context snapshot" "FLOW_SOURCE_TEXT_MISMATCH" (fun () ->
        proveWordBase { wordInventory with Sources = alteredSource :: (wordInventory.Sources |> List.filter (fun source -> source.OwnerId <> answerId)) })

    let inventoryProbe = authoredFlowAttachment FlowAttachmentKind.Test ownerId 1 "test attachment.owner/inventory-probe {\n    domain::answer()\n    => 42\n}"
    let compileWithAttachmentInventory inventoryValue =
        FlowLowering.compileBatchFlowProjectSources context wordInventory [] inventoryValue [ FlowAttachmentChange.Add inventoryProbe ] |> ignore
    let firstDocument = sources.Head
    expectLanguageError "attachment inventory rejects duplicate documents" "FLOW_ATTACHMENT_INVENTORY_DUPLICATE" (fun () ->
        compileWithAttachmentInventory { attachmentInventory with Sources = attachmentInventory.Sources @ [ firstDocument ] })
    expectLanguageError "attachment inventory rejects missing expected keys" "FLOW_ATTACHMENT_INVENTORY_INCOMPLETE" (fun () ->
        compileWithAttachmentInventory { attachmentInventory with ExpectedSources = Map.remove (flowAttachmentKey firstDocument) attachmentInventory.ExpectedSources })
    let undeclaredKey = { flowAttachmentKey inventoryProbe with CaseName = "undeclared.extra" }
    expectLanguageError "attachment inventory rejects extra expected keys" "FLOW_ATTACHMENT_INVENTORY_INCOMPLETE" (fun () ->
        compileWithAttachmentInventory { attachmentInventory with ExpectedSources = Map.add undeclaredKey inventoryProbe.Reference attachmentInventory.ExpectedSources })
    let wrongExpectedReference = { firstDocument.Reference with Hash = String.replicate 64 "0" }
    expectLanguageError "attachment inventory requires each exact host-declared reference" "FLOW_ATTACHMENT_INVENTORY_REFERENCE_MISMATCH" (fun () ->
        compileWithAttachmentInventory { attachmentInventory with ExpectedSources = Map.add (flowAttachmentKey firstDocument) wrongExpectedReference attachmentInventory.ExpectedSources })
    let inventoryFor (mutate: FlowLowering.FlowAttachmentSourceDocument -> FlowLowering.FlowAttachmentSourceDocument) =
        let changed = mutate firstDocument
        let originalKey = flowAttachmentKey firstDocument
        { ExpectedSources = Map.add (flowAttachmentKey changed) changed.Reference (Map.remove originalKey attachmentInventory.ExpectedSources)
          Sources = changed :: (attachmentInventory.Sources |> List.filter (fun source -> flowAttachmentKey source <> originalKey)) }
    let malformed: (string * string * (FlowLowering.FlowAttachmentSourceDocument -> FlowLowering.FlowAttachmentSourceDocument)) list =
        [ "owner name", "FLOW_ATTACHMENT_OWNER_MISMATCH", (fun source -> { source with OwnerName = "wrong.owner" })
          "owner ID", "FLOW_ATTACHMENT_OWNER_ID_MISMATCH", (fun source -> { source with OwnerId = answerId })
          "owner revision", "FLOW_ATTACHMENT_OWNER_REVISION_MISMATCH", (fun source -> { source with OwnerRevision = 9 })
          "case name", "FLOW_ATTACHMENT_CASE_MISMATCH", (fun source -> { source with CaseName = "other-case" })
          "kind", "FLOW_ATTACHMENT_KIND_MISMATCH", (fun source -> { source with Reference = { source.Reference with Kind = StorageObjectKind.ExampleDefinition } })
          "hash", "FLOW_ATTACHMENT_HASH_MISMATCH", (fun source -> { source with Reference = { source.Reference with Hash = String.replicate 64 "0" } })
          "negative owner revision", "FLOW_ATTACHMENT_REVISION_INVALID", (fun source -> { source with OwnerRevision = -1 })
          "blank owner name", "FLOW_ATTACHMENT_OWNER_INVALID", (fun source -> { source with OwnerName = " " })
          "blank source file", "FLOW_ATTACHMENT_FILE_INVALID", (fun source -> { source with SourceFile = " " })
          "null source bytes", "FLOW_ATTACHMENT_CONTENT_INVALID", (fun source -> { source with Content = null }) ]
    for name, code, mutate in malformed do
        expectLanguageError ("attachment inventory rejects malformed " + name) code (fun () -> compileWithAttachmentInventory (inventoryFor mutate))
    let blankId = { firstDocument with OwnerId = WordId "" }
    let blankIdInventory =
        { ExpectedSources = Map.add (flowAttachmentKey blankId) blankId.Reference (Map.remove (flowAttachmentKey firstDocument) attachmentInventory.ExpectedSources)
          Sources = blankId :: (attachmentInventory.Sources |> List.filter (fun source -> flowAttachmentKey source <> flowAttachmentKey firstDocument)) }
    expectLanguageError "attachment inventory rejects an empty stable owner ID" "FLOW_ATTACHMENT_OWNER_ID_INVALID" (fun () -> compileWithAttachmentInventory blankIdInventory)
    let badUtf16 = inventoryFor (fun source -> { source with Content = source.Content + string (char 0xD800) })
    expectLanguageError "attachment source rejects unpaired UTF-16 rather than replacement-encoding it" "FLOW_ATTACHMENT_UTF8_INVALID" (fun () -> compileWithAttachmentInventory badUtf16)

    let impureExpected = authoredFlowAttachment FlowAttachmentKind.Test ownerId 1 "test attachment.owner/impure-expected {\n    unit\n    => value console::write(\"denied\")\n}"
    expectLanguageError "source-backed expected expression stays pure" "TEST_EXPECTED_VALUE_EFFECTS" (fun () ->
        FlowLowering.compileBatchFlowProjectSources context wordInventory [] attachmentInventory [ FlowAttachmentChange.Add impureExpected ] |> ignore)
    let wrongTypeExpected = authoredFlowAttachment FlowAttachmentKind.Test ownerId 1 "test attachment.owner/wrong-type {\n    unit\n    => value 1\n}"
    expectLanguageError "source-backed expected expression is checked against the actual type" "TEST_EXPECTED_STACK" (fun () ->
        FlowLowering.compileBatchFlowProjectSources context wordInventory [] attachmentInventory [ FlowAttachmentChange.Add wrongTypeExpected ] |> ignore)

    let addedSource = authoredFlowAttachment FlowAttachmentKind.Test ownerId 1 "test attachment.owner/cas-added {\n    domain::answer()\n    => 42\n}"
    let added = FlowLowering.compileBatchFlowProjectSources context wordInventory [] attachmentInventory [ FlowAttachmentChange.Add addedSource ]
    equal "attachment-only Add works with no word changes" (sources.Length + 1) added.Attachments.Length
    let afterAdd = added.Attachments |> List.map (function | FlowCompiledAttachment.Test(source, _) | FlowCompiledAttachment.Example(source, _) -> source) |> flowAttachmentInventory
    let literalDocument = sources |> List.find (fun source -> source.CaseName = "literal-test")
    let replacement = authoredFlowAttachment FlowAttachmentKind.Test ownerId 1 "test attachment.owner/literal-test {\n    domain::answer()\n    => 42\n}"
    let addReplaceRemove =
        FlowLowering.compileBatchFlowProjectSources context wordInventory [] afterAdd
            [ FlowAttachmentChange.Replace(literalDocument.Reference, replacement)
              FlowAttachmentChange.Remove(flowAttachmentKey addedSource, addedSource.Reference) ]
    equal "attachment-only Replace and Remove compose with exact prior references" sources.Length addReplaceRemove.Attachments.Length
    check "replacement keeps the key and installs the new source object"
        (addReplaceRemove.Attachments |> List.exists (function | FlowCompiledAttachment.Test(source, _) when source.CaseName = "literal-test" -> source.Content = replacement.Content && source.Reference = replacement.Reference | _ -> false))
    check "removed attachment is absent from the final inventory"
        (addReplaceRemove.Attachments |> List.forall (function | FlowCompiledAttachment.Test(source, _) | FlowCompiledAttachment.Example(source, _) -> source.CaseName <> "cas-added"))
    let staleReference = { literalDocument.Reference with Hash = String.replicate 64 "0" }
    expectLanguageError "Replace validates the prior reference as a compare-and-swap" "FLOW_ATTACHMENT_STALE_SOURCE" (fun () ->
        FlowLowering.compileBatchFlowProjectSources context wordInventory [] attachmentInventory [ FlowAttachmentChange.Replace(staleReference, replacement) ] |> ignore)
    expectLanguageError "Remove validates the prior reference as a compare-and-swap" "FLOW_ATTACHMENT_STALE_SOURCE" (fun () ->
        FlowLowering.compileBatchFlowProjectSources context wordInventory [] attachmentInventory [ FlowAttachmentChange.Remove(flowAttachmentKey literalDocument, staleReference) ] |> ignore)
    expectLanguageError "Add rejects an existing attachment key" "FLOW_ATTACHMENT_ADD_EXISTS" (fun () ->
        FlowLowering.compileBatchFlowProjectSources context wordInventory [] attachmentInventory [ FlowAttachmentChange.Add literalDocument ] |> ignore)
    let missingKey = { flowAttachmentKey literalDocument with CaseName = "missing-case" }
    let missingDocument = authoredFlowAttachment FlowAttachmentKind.Test ownerId 1 "test attachment.owner/missing-case {\n    1\n    => 1\n}"
    expectLanguageError "Replace rejects an absent attachment key" "FLOW_ATTACHMENT_REPLACE_MISSING" (fun () ->
        FlowLowering.compileBatchFlowProjectSources context wordInventory [] attachmentInventory [ FlowAttachmentChange.Replace(missingDocument.Reference, missingDocument) ] |> ignore)
    expectLanguageError "Remove rejects an absent attachment key" "FLOW_ATTACHMENT_REMOVE_MISSING" (fun () ->
        FlowLowering.compileBatchFlowProjectSources context wordInventory [] attachmentInventory [ FlowAttachmentChange.Remove(missingKey, missingDocument.Reference) ] |> ignore)
    expectLanguageError "one batch rejects duplicate intents for a stable attachment key" "FLOW_ATTACHMENT_DUPLICATE_CHANGE" (fun () ->
        FlowLowering.compileBatchFlowProjectSources context wordInventory [] attachmentInventory
            [ FlowAttachmentChange.Remove(flowAttachmentKey literalDocument, literalDocument.Reference)
              FlowAttachmentChange.Replace(literalDocument.Reference, replacement) ] |> ignore)

    let changed =
        FlowLowering.compileBatchFlowProjectSources context wordInventory
            [ replaceWord ownerId 1 2 "word attachment.owner() -> Int {\n    effects none\n    2\n}"
              replaceWord answerId 1 2 "word domain.answer() -> Int {\n    effects none\n    43\n}" ]
            attachmentInventory []
    let carriedDocument, carriedTest = testCase changed "both-roles"
    equal "unchanged attachment keeps its source object across owner update" (bothDocument.Reference, bothDocument.Content) (carriedDocument.Reference, carriedDocument.Content)
    equal "unchanged attachment owner revision auto-carries under the same stable owner ID" 2 carriedDocument.OwnerRevision
    equal "retained actual re-resolves against the final candidate IR" [ IntValue 43L ] (IrInterpreter.executeBody (host (ResizeArray())) "advanced-actual" carriedTest.Body)
    equal "retained expected expression re-resolves against the final candidate IR" [ IntValue 43L ]
        (carriedTest.ExpectationBody |> Option.defaultWith (fun () -> failwith "Expected carried value expression.") |> IrInterpreter.executeBody (host (ResizeArray())) "advanced-expected")
    for role in [ FlowAttachmentBodyRole.Actual; FlowAttachmentBodyRole.ExpectedExpression ] do
        let binding = changed.AttachmentBindings |> List.find (fun site -> site.Attachment.CaseName = "both-roles" && site.BodyRole = role)
        equal "retained binding keeps stable target identity and exact final revision" (FlowCallTargetIdentity.UserWord answerId, Some 2) (binding.Site.Target, binding.Site.TargetRevision)
        equal "retained binding auto-carries owner revision" 2 binding.OwnerRevision

    let sharedSpan = span "<same-attachment-span>" 2 5 14
    let sharedCall = FlowExpression.Call("domain.answer", [], sharedSpan)
    let hostAst = parseTest "test attachment.owner/transient-same-span {\n    1\n    => value 1\n}"
    let hostAst = { hostAst with Body = [ FlowStatement.Evaluate sharedCall ]; Expected = FlowTestExpectation.Expression sharedCall; SourceText = "<host-constructed>" }
    let hostBound = FlowLowering.compileTestWithCallBindings context initial.WordCompilation.Program hostAst
    let hostActual = hostBound.CallSites[FlowAttachmentBodyRole.Actual].Head
    let hostExpected = hostBound.CallSites[FlowAttachmentBodyRole.ExpectedExpression].Head
    equal "host AST actual and expected calls preserve one identical authored span" (sharedSpan, sharedSpan) (hostActual.Span, hostExpected.Span)
    equal "host AST actual call retains its body-statement structural path"
        (FlowAstPath.FlowAstPath [ FlowAstPathSegment.BlockStatement 0; FlowAstPathSegment.EvaluateExpression ]) hostActual.Path
    equal "host AST expected expression retains its own root structural path" (FlowAstPath.FlowAstPath []) hostExpected.Path
    check "host AST helper returns actual and expected calls under separate body-role keys"
        (Map.containsKey FlowAttachmentBodyRole.Actual hostBound.CallSites
         && Map.containsKey FlowAttachmentBodyRole.ExpectedExpression hostBound.CallSites)
    equal "host AST role separation retains the same stable target ID" hostActual.Target hostExpected.Target

    let suffixDocs = sources |> List.filter (fun source -> source.CaseName = "suffix-actual" || source.CaseName = "suffix-expected")
    let suffixReplacement = replaceWord suffixId 1 2 "word domain.suffix(unused: Bool) -> Int {\n    effects none\n    41\n}"
    let exactSuffix = addWord (WordId "exact-suffix") "word suffix() -> Int {\n    effects none\n    44\n}"
    let suffixRoleCases: (FlowLowering.FlowAttachmentSourceDocument * string) list =
        [ (suffixDocs |> List.find (fun source -> source.CaseName = "suffix-actual"), "Actual")
          (suffixDocs |> List.find (fun source -> source.CaseName = "suffix-expected"), "ExpectedExpression") ]
    for document, role in suffixRoleCases do
        let diagnostic = captureLanguageError ("unchanged " + role + " attachment rejects a real short-name redirect") "FLOW_ATTACHMENT_CALL_REBOUND" (fun () ->
            FlowLowering.compileBatchFlowProjectSources context wordInventory [ suffixReplacement; exactSuffix ] (flowAttachmentInventory [ document ]) [] |> ignore)
        let details = String.concat " " (diagnostic.Expected @ diagnostic.Actual @ [ diagnostic.Message ])
        check ("redirect diagnostic identifies role/path and both stable IDs")
            (details.Contains(role, StringComparison.Ordinal) && details.Contains("FlowAstPath", StringComparison.Ordinal)
             && details.Contains("domain-suffix", StringComparison.Ordinal) && details.Contains("exact-suffix", StringComparison.Ordinal))
    let anotherPick = addWord (WordId "other-pick") "word other.pick(value: Int) -> Int {\n    effects none\n    value\n}"
    let ambiguityCases: (string * string * string) list =
        [ "dot-ambiguity", "Actual", "test"
          "dot-ambiguity-expected", "ExpectedExpression", "test"
          "dot-ambiguity-example", "Actual", "example" ]
    for caseName, role, kind in ambiguityCases do
        let document = sources |> List.find (fun source -> source.CaseName = caseName)
        let ambiguity = captureLanguageError ("retained " + role + " attachment reports dot ambiguity") "FLOW_AMBIGUOUS_DOT_STAGE" (fun () ->
            FlowLowering.compileBatchFlowProjectSources context wordInventory [ anotherPick ] (flowAttachmentInventory [ document ]) [] |> ignore)
        equal "attachment ambiguity identifies its source owner" (Some "attachment.owner") ambiguity.Word
        check "attachment ambiguity retains its authored call span" ambiguity.Span.IsSome
        equal "attachment diagnostic preserves both original resolution candidates" (Set.ofList [ "domain.pick"; "other.pick" ]) (Set.ofList ambiguity.Actual)
        equal "attachment diagnostic preserves its original empty expected-candidate set" [] ambiguity.Expected
        check "attachment ambiguity adds owner, kind, case, body role, and structural path context"
            (ambiguity.Message.Contains("owner='attachment.owner'", StringComparison.Ordinal)
             && ambiguity.Message.Contains("kind='" + kind + "'", StringComparison.Ordinal)
             && ambiguity.Message.Contains("case='" + caseName + "'", StringComparison.Ordinal)
             && ambiguity.Message.Contains(role, StringComparison.Ordinal)
             && ambiguity.Message.Contains("FlowAstPath", StringComparison.Ordinal))

    let stackWord = wordEntry "legacy.operate" [ TInt ] [ TInt ] Set.empty [ Call("int.abs", sourceSpan) ]
    let stackContext = loweringContext [ stackWord ] (Map.ofList [ "legacy.operate", [ "value" ] ])
    let legacyId = stackContext.CompilerContext.WordIds["legacy.operate"]
    let stackCase = authoredFlowAttachment FlowAttachmentKind.Test legacyId 1 "test legacy.operate/stack-case {\n    4\n    => 4\n}"
    expectLanguageError "valid source attached to a legacy Stack owner requires Flow frontend proof" "FLOW_ATTACHMENT_OWNER_FRONTEND" (fun () ->
        FlowLowering.compileBatchFlowProjectSources stackContext emptyWordInventory [] (flowAttachmentInventory [])
            [ FlowAttachmentChange.Add stackCase ] |> ignore)
    let migratedCase = authoredFlowAttachment FlowAttachmentKind.Test legacyId 2 "test legacy.operate/flow-case {\n    legacy::operate(4)\n    => 5\n}"
    let migrated =
        FlowLowering.compileBatchFlowProjectSources stackContext emptyWordInventory
            [ replaceWord legacyId 1 2 "word legacy.operate(value: Int) -> Int {\n    effects none\n    add(value, 1)\n}" ]
            (flowAttachmentInventory []) [ FlowAttachmentChange.Add migratedCase ]
    equal "same-batch Stack-to-Flow replacement admits its new case" 1 migrated.Attachments.Length
    match migrated.Attachments with
    | [ FlowCompiledAttachment.Test(source, compiled) ] ->
        equal "Stack-to-Flow case preserves its owner's stable identity and final revision" (legacyId, 2) (source.OwnerId, source.OwnerRevision)
        equal "migrated case executes on the candidate Flow definition" [ IntValue 5L ] (IrInterpreter.executeBody (host (ResizeArray())) "migrated-case" compiled.Body)
    | other -> failwithf "Expected exactly the new Flow case after migration, got %A" other

    let retained = FlowLowering.compileFlowProjectSnapshot context wordInventory attachmentInventory
    let resolveOrigin (origins: Map<SourceSpan, SourceSpan>) (authoredSpan: SourceSpan) =
        origins.TryFind authoredSpan |> Option.defaultValue authoredSpan
    let originValues origins = origins |> Map.toList |> List.map snd |> List.sort
    let mergeOrigins origins projection =
        Map.fold (fun found marker authored -> Map.add marker authored found) origins projection.SyntheticOrigins
    let rec normalizeExpression origins = function
        | Push(literal, sourceSpan) -> Push(literal, resolveOrigin origins sourceSpan)
        | Call(name, sourceSpan) -> Call(name, resolveOrigin origins sourceSpan)
        | ConstructContainer(constructor, arguments, sourceSpan) -> ConstructContainer(constructor, arguments, resolveOrigin origins sourceSpan)
        | MapList(name, sourceSpan) -> MapList(name, resolveOrigin origins sourceSpan)
        | FilterList(name, sourceSpan) -> FilterList(name, resolveOrigin origins sourceSpan)
        | EachList(name, sourceSpan) -> EachList(name, resolveOrigin origins sourceSpan)
        | FoldList(name, sourceSpan) -> FoldList(name, resolveOrigin origins sourceSpan)
        | Let(name, sourceSpan) -> Let(name, resolveOrigin origins sourceSpan)
        | Load(name, sourceSpan) -> Load(name, resolveOrigin origins sourceSpan)
        | If(thenBranch, elseBranch, sourceSpan) ->
            If(normalizeExpressions origins thenBranch, normalizeExpressions origins elseBranch, resolveOrigin origins sourceSpan)
        | Scope(body, sourceSpan) -> Scope(normalizeExpressions origins body, resolveOrigin origins sourceSpan)
        | MatchOption(name, someBranch, noneBranch, sourceSpan) ->
            MatchOption(name, normalizeExpressions origins someBranch, normalizeExpressions origins noneBranch, resolveOrigin origins sourceSpan)
        | MatchResult(okName, errorName, okBranch, errorBranch, sourceSpan) ->
            MatchResult(okName, errorName, normalizeExpressions origins okBranch, normalizeExpressions origins errorBranch, resolveOrigin origins sourceSpan)
    and normalizeExpressions origins expressions = expressions |> List.map (normalizeExpression origins)
    let normalizeSpanMap origins sourceSpans = sourceSpans |> Map.map (fun _ sourceSpan -> resolveOrigin origins sourceSpan)
    let normalizeProgram program origins =
        let data = VerifiedIrProgram.inspect program
        { data with SourceMap = data.SourceMap |> Map.map (fun _ source -> { source with SiteSpan = resolveOrigin origins source.SiteSpan }) }
    let wordRows (compiled: FlowLowering.FlowBoundProjectCompilation) =
        let origins = compiled.WordCompilation.Context.SourceOrigins
        compiled.WordCompilation.LoweredWords
        |> List.map (fun word ->
            let definition = word.Definition
            let wordOrigins = mergeOrigins origins word.Projection
            (definition.Name, definition.Inputs, definition.Outputs, definition.Effects, definition.Maturity,
             definition.Revision, definition.Documentation, normalizeExpressions wordOrigins definition.Body,
             definition.SourceText, resolveOrigin wordOrigins definition.Span, word.ParameterNames,
             word.SourceText, word.SyntaxVersion, word.Projection.AuthoredSpans, originValues word.Projection.SyntheticOrigins))
    equal "no-op snapshot retains every authored word projection and normalized origin" (wordRows initial) (wordRows retained)
    equal "no-op snapshot retains the named-parameter catalog" initial.WordCompilation.Context.ParameterNames retained.WordCompilation.Context.ParameterNames
    equal "no-op snapshot retains all private-marker authored origin destinations"
        (originValues initial.WordCompilation.Context.SourceOrigins) (originValues retained.WordCompilation.Context.SourceOrigins)
    equal "no-op snapshot retains definition source origins after marker normalization"
        (normalizeSpanMap initial.WordCompilation.Context.SourceOrigins initial.WordCompilation.SiteOrigins)
        (normalizeSpanMap retained.WordCompilation.Context.SourceOrigins retained.WordCompilation.SiteOrigins)
    equal "no-op snapshot retains definition call bindings including exact target revisions" initial.WordCompilation.CallBindings retained.WordCompilation.CallBindings
    equal "no-op snapshot retains the complete verified IR program after marker normalization"
        (normalizeProgram initial.WordCompilation.Program initial.WordCompilation.Context.SourceOrigins)
        (normalizeProgram retained.WordCompilation.Program retained.WordCompilation.Context.SourceOrigins)
    equal "no-op snapshot retains the complete attachment call-binding inventory" initial.AttachmentBindings retained.AttachmentBindings
    let normalizeBody origins body =
        let data = VerifiedIrBody.inspect body
        { data with BodySourceMap = data.BodySourceMap |> Map.map (fun _ source -> { source with SiteSpan = resolveOrigin origins source.SiteSpan }) }
    let testArtifacts (compiled: FlowLowering.FlowBoundProjectCompilation) =
        compiled.Attachments
        |> List.choose (function
            | FlowCompiledAttachment.Test(source, value) ->
                let origins = mergeOrigins compiled.WordCompilation.Context.SourceOrigins value.Lowered.Projection
                let definition = value.Lowered.Definition
                let expectation =
                    match definition.Expected with
                    | ExpectedExpression expressions -> ExpectedExpression(normalizeExpressions origins expressions)
                    | other -> other
                let normalizedDefinition =
                    { definition with
                        Body = normalizeExpressions origins definition.Body
                        Expected = expectation
                        Span = resolveOrigin origins definition.Span }
                Some(source, normalizedDefinition, value.Lowered.SourceText, value.Lowered.SyntaxVersion,
                     value.Lowered.Projection.AuthoredSpans, originValues value.Lowered.Projection.SyntheticOrigins,
                     normalizeBody origins value.Body,
                     value.ExpectationBody |> Option.map (normalizeBody origins),
                     normalizeSpanMap origins value.BodySiteOrigins,
                     value.ExpectationSiteOrigins |> Option.map (normalizeSpanMap origins))
            | FlowCompiledAttachment.Example _ -> None)
    let exampleArtifacts (compiled: FlowLowering.FlowBoundProjectCompilation) =
        compiled.Attachments
        |> List.choose (function
            | FlowCompiledAttachment.Example(source, value) ->
                let origins = mergeOrigins compiled.WordCompilation.Context.SourceOrigins value.Lowered.Projection
                let definition = value.Lowered.Definition
                let normalizedDefinition =
                    { definition with
                        Body = normalizeExpressions origins definition.Body
                        Span = resolveOrigin origins definition.Span }
                Some(source, normalizedDefinition, value.Lowered.SourceText, value.Lowered.SyntaxVersion,
                     value.Lowered.Projection.AuthoredSpans, originValues value.Lowered.Projection.SyntheticOrigins,
                     normalizeBody origins value.Body, normalizeSpanMap origins value.SiteOrigins)
            | FlowCompiledAttachment.Test _ -> None)
    equal "no-op snapshot retains each test source, actual body, pure expected body, and exact authored origins"
        (testArtifacts initial) (testArtifacts retained)
    equal "no-op snapshot retains each example source, body, and exact authored origins"
        (exampleArtifacts initial) (exampleArtifacts retained)
    let (_, retainedBothTest) = testCase retained "both-roles"
    let retainedExpected = retainedBothTest.ExpectationBody |> Option.defaultWith (fun () -> failwith "Expected a retained expected-expression body.")
    check "retained test actual body references the exact snapshot program"
        (Object.ReferenceEquals(VerifiedIrBody.program retainedBothTest.Body, retained.WordCompilation.Program))
    check "retained expected-expression body references the exact snapshot program"
        (Object.ReferenceEquals(VerifiedIrBody.program retainedExpected, retained.WordCompilation.Program))
    equal "retained actual body remains executable" [ IntValue 42L ]
        (IrInterpreter.executeBody (host (ResizeArray())) "retained-actual" retainedBothTest.Body)
    equal "retained expected-expression body remains separately executable" [ IntValue 42L ]
        (IrInterpreter.executeBody (host (ResizeArray())) "retained-expected" retainedExpected)
    let (_, retainedExample) = exampleCase retained "literal-example"
    equal "retained example body remains executable" [ IntValue 42L ]
        (IrInterpreter.executeBody (host (ResizeArray())) "retained-example" retainedExample.Body)

    expectLanguageError "no-op snapshot rejects an incomplete retained word inventory" "FLOW_SOURCE_INVENTORY_INCOMPLETE" (fun () ->
        FlowLowering.compileFlowProjectSnapshot context
            { wordInventory with Sources = wordInventory.Sources |> List.tail }
            attachmentInventory |> ignore)
    expectLanguageError "no-op snapshot rejects an incomplete retained attachment inventory" "FLOW_ATTACHMENT_INVENTORY_INCOMPLETE" (fun () ->
        FlowLowering.compileFlowProjectSnapshot context wordInventory
            { attachmentInventory with Sources = attachmentInventory.Sources |> List.tail } |> ignore)

    let replacementAnswerId = WordId "replacement-domain-answer"
    let answerSource = wordInventory.Sources |> List.find (fun source -> source.OwnerId = answerId)
    let reboundContext =
        { context with
            CompilerContext =
                { context.CompilerContext with
                    WordIds = Map.add "domain.answer" replacementAnswerId context.CompilerContext.WordIds } }
    let reboundWordInventory =
        { ExpectedFlowOwnerIds = wordInventory.ExpectedFlowOwnerIds |> Set.remove answerId |> Set.add replacementAnswerId
          Sources =
            wordInventory.Sources
            |> List.map (fun source -> if source.OwnerId = answerId then { source with OwnerId = replacementAnswerId } else source) }
    let reboundAnswerSource = reboundWordInventory.Sources |> List.find (fun source -> source.OwnerId = replacementAnswerId)
    equal "rebind fixture changes only its declared stable owner ID"
        (answerSource.Content, answerSource.Reference, answerSource.OwnerRevision)
        (reboundAnswerSource.Content, reboundAnswerSource.Reference, reboundAnswerSource.OwnerRevision)
    let reboundSnapshot = FlowLowering.compileFlowProjectSnapshot reboundContext reboundWordInventory attachmentInventory
    let originalAnswerBindings =
        initial.AttachmentBindings
        |> List.filter (fun binding -> binding.Attachment.CaseName = "both-roles")
    let reboundAnswerBindings =
        reboundSnapshot.AttachmentBindings
        |> List.filter (fun binding -> binding.Attachment.CaseName = "both-roles")
    equal "no-op snapshot regenerates both actual and expected bindings for the changed target identity"
        (2, Set.ofList [ FlowAttachmentBodyRole.Actual; FlowAttachmentBodyRole.ExpectedExpression ])
        (reboundAnswerBindings.Length, reboundAnswerBindings |> List.map (fun binding -> binding.BodyRole) |> Set.ofList)
    check "regenerated source bindings expose the different stable target ID for Runtime reconciliation"
        (reboundAnswerBindings |> List.forall (fun binding -> binding.Site.Target = FlowCallTargetIdentity.UserWord replacementAnswerId))
    check "durable adapter rows detect the changed target identity for Runtime to reject against retained manifest metadata"
        (FlowPersistence.attachmentBindings reboundAnswerBindings
         <> (FlowPersistence.attachmentBindings originalAnswerBindings))

    let emptyStackSnapshot = FlowLowering.compileFlowProjectSnapshot stackContext emptyWordInventory (flowAttachmentInventory [])
    equal "empty Flow inventories retain a valid Stack-only program" (VerifiedIrProgram.inspect (Compiler.compileIrProgram stackContext.CompilerContext))
        (VerifiedIrProgram.inspect emptyStackSnapshot.WordCompilation.Program)
    equal "empty Flow inventories add no authored Flow words" [] emptyStackSnapshot.WordCompilation.LoweredWords
    equal "empty Flow inventories add no definition bindings" [] emptyStackSnapshot.WordCompilation.CallBindings
    equal "empty Flow inventories add no attachments" [] emptyStackSnapshot.Attachments
    equal "empty Flow inventories add no attachment bindings" [] emptyStackSnapshot.AttachmentBindings
    let detachedSpan = span "stack-only.agent" 1 1 1
    let detachedStackTest: TestDefinition =
        { Name = "detached-stack-case"
          Word = "legacy.operate"
          Body = [ Push(LInt 4L, detachedSpan); Call("legacy.operate", detachedSpan) ]
          Expected = ExpectedValue(LInt 4L)
          SourceText = ""
          Span = detachedSpan }
    let detachedBody, detachedExpected =
        Compiler.compileIrTestWithExpectationAgainstProgram stackContext.CompilerContext emptyStackSnapshot.WordCompilation.Program detachedStackTest
    equal "empty Flow snapshot still compiles the detached Stack test body" [ IntValue 4L ]
        (IrInterpreter.executeBody (host (ResizeArray())) "detached-stack-case" detachedBody)
    check "detached Stack test remains ownerless and does not enter the Flow source inventory"
        ((VerifiedIrBody.inspect detachedBody).BodySourceMap |> Map.forall (fun _ source -> source.SiteOwner.IsNone)
         && detachedExpected.IsNone)

let private testFlowPersistenceBindings () =
    let path =
        FlowAstPath.FlowAstPath
            [ FlowAstPathSegment.BlockStatement 1;
              FlowAstPathSegment.LetInitializer;
              FlowAstPathSegment.DestructureInitializer;
              FlowAstPathSegment.EvaluateExpression;
              FlowAstPathSegment.ReturnOutput 2;
              FlowAstPathSegment.CallArgument 3;
              FlowAstPathSegment.RootCallArgument 4;
              FlowAstPathSegment.DotReceiver;
              FlowAstPathSegment.DotArgument 5;
              FlowAstPathSegment.PropertyReceiver;
              FlowAstPathSegment.EqualityLeft;
              FlowAstPathSegment.EqualityRight;
              FlowAstPathSegment.IfCondition;
              FlowAstPathSegment.IfThenStatement 6;
              FlowAstPathSegment.IfElseStatement 7;
              FlowAstPathSegment.ContainerPayload;
              FlowAstPathSegment.OptionScrutinee;
              FlowAstPathSegment.OptionSomeStatement 8;
              FlowAstPathSegment.OptionNoneStatement 9;
              FlowAstPathSegment.ResultScrutinee;
              FlowAstPathSegment.ResultOkStatement 10;
              FlowAstPathSegment.ResultErrorStatement 11 ]
    let callSpan = span "persisted-bindings.flow" 9 4 18
    let forms: (FlowCallForm * StoredCallForm) list =
        [ FlowCallForm.Direct, StoredCallForm.Direct
          FlowCallForm.AbsoluteRoot, StoredCallForm.AbsoluteRoot
          FlowCallForm.DotStage "select", StoredCallForm.DotStage "select"
          FlowCallForm.PropertyAccess "email", StoredCallForm.PropertyAccess "email"
          FlowCallForm.StaticCallback("map", FlowWordReferenceQualification.ExplicitShort),
              StoredCallForm.StaticCallback("map", FlowWordReferenceQualification.ExplicitShort)
          FlowCallForm.StaticCallback("filter", FlowWordReferenceQualification.NamespaceQualified),
              StoredCallForm.StaticCallback("filter", FlowWordReferenceQualification.NamespaceQualified)
          FlowCallForm.StaticCallback("each", FlowWordReferenceQualification.AbsoluteRoot),
              StoredCallForm.StaticCallback("each", FlowWordReferenceQualification.AbsoluteRoot) ]
    let targets: (FlowCallTargetIdentity * StoredCallTarget) list =
        [ FlowCallTargetIdentity.UserWord(WordId "stable/user-word"), StoredCallTarget.UserWord "stable/user-word"
          FlowCallTargetIdentity.Primitive(PrimitiveId "primitive-id"), StoredCallTarget.Primitive "primitive-id"
          FlowCallTargetIdentity.GeneratedWord(WordId "stable/generated-word"), StoredCallTarget.GeneratedWord "stable/generated-word" ]
    let wordSource = (Storage.sourceObject StorageObjectKind.WordDefinition "word persisted.owner() -> Int {\n    effects none\n    1\n}\n").Reference
    let testSource = (Storage.sourceObject StorageObjectKind.TestDefinition "test persisted.owner/shared-case {\n    1\n    => 1\n}\n").Reference
    let exampleSource = (Storage.sourceObject StorageObjectKind.ExampleDefinition "example persisted.owner/shared-case {\n    1\n    => 1\n}\n").Reference
    let site form target revision : FlowCallSite =
        { Path = path
          Span = callSpan
          Form = form
          RequestedName = "requested.call"
          Target = target
          TargetRevision = Some revision }
    let wordBindings: FlowCallBinding list =
        [ for form, _ in forms do
              for target, _ in targets do
                  yield
                      { OwnerName = "persisted.owner"
                        OwnerId = WordId "persisted-owner-id"
                        OwnerRevision = 12
                        Source = wordSource
                        Site = site form target 7 } ]
    let storedWords = FlowPersistence.wordBindings wordBindings
    let expectedWords =
        [ for binding in wordBindings do
              let storedForm = forms |> List.find (fun (form, _) -> form = binding.Site.Form) |> snd
              let storedTarget = targets |> List.find (fun (target, _) -> target = binding.Site.Target) |> snd
              yield
                  { Source = wordSource
                    CaseName = None
                    BodyRole = StoredCallBodyRole.Definition
                    Path = path
                    Form = storedForm
                    RequestedName = "requested.call"
                    Target = storedTarget } ]
    equal "word binding adapter exhaustively retains forms, callback qualifications, target kinds, source, and path"
        expectedWords storedWords
    equal "word binding adapter preserves compiler traversal order" expectedWords storedWords
    let wordBindingsWithNewTargetRevisions =
        wordBindings
        |> List.map (fun binding -> { binding with Site = { binding.Site with TargetRevision = Some 991 } })
    equal "word binding adapter intentionally omits transient target revisions" storedWords
        (FlowPersistence.wordBindings wordBindingsWithNewTargetRevisions)
    let firstWordBinding = List.head wordBindings
    let changedWordIdentity =
        { firstWordBinding with
            Site = { firstWordBinding.Site with Target = FlowCallTargetIdentity.UserWord(WordId "different-stable-id") } }
    check "word binding adapter retains a changed stable target identity"
        (FlowPersistence.wordBindings [ changedWordIdentity ] <> [ List.head storedWords ])

    let attachmentSources =
        [ StorageObjectKind.TestDefinition, FlowAttachmentKind.Test, testSource, "shared-case"
          StorageObjectKind.ExampleDefinition, FlowAttachmentKind.Example, exampleSource, "shared-case" ]
    let roles =
        [ FlowAttachmentBodyRole.Actual, StoredCallBodyRole.Actual
          FlowAttachmentBodyRole.ExpectedExpression, StoredCallBodyRole.ExpectedExpression ]
    let attachmentBindings: FlowAttachmentCallBinding list =
        [ for sourceKind, kind, source, caseName in attachmentSources do
              equal "attachment fixture uses the case-kind-matched source reference" sourceKind source.Kind
              for bodyRole, _ in roles do
                  for form, _ in forms do
                      for target, _ in targets do
                          yield
                              { Attachment =
                                    { OwnerId = WordId "persisted-owner-id"
                                      Kind = kind
                                      CaseName = caseName }
                                OwnerName = "persisted.owner"
                                OwnerRevision = 12
                                Source = source
                                BodyRole = bodyRole
                                Site = site form target 7 } ]
    let storedAttachments = FlowPersistence.attachmentBindings attachmentBindings
    let expectedAttachments =
        [ for binding in attachmentBindings do
              let storedForm = forms |> List.find (fun (form, _) -> form = binding.Site.Form) |> snd
              let storedTarget = targets |> List.find (fun (target, _) -> target = binding.Site.Target) |> snd
              let storedRole = roles |> List.find (fun (role, _) -> role = binding.BodyRole) |> snd
              yield
                  { Source = binding.Source
                    CaseName = Some binding.Attachment.CaseName
                    BodyRole = storedRole
                    Path = path
                    Form = storedForm
                    RequestedName = "requested.call"
                    Target = storedTarget } ]
    equal "attachment adapter exhaustively retains case, body role, source kind, forms, targets, and path"
        expectedAttachments storedAttachments
    equal "attachment adapter preserves compiler traversal order" expectedAttachments storedAttachments
    let attachmentBindingsWithNewTargetRevisions =
        attachmentBindings
        |> List.map (fun binding -> { binding with Site = { binding.Site with TargetRevision = Some 991 } })
    equal "attachment binding adapter intentionally omits transient target revisions" storedAttachments
        (FlowPersistence.attachmentBindings attachmentBindingsWithNewTargetRevisions)
    let firstAttachmentBinding = List.head attachmentBindings
    let changedAttachmentIdentity =
        { firstAttachmentBinding with
            Site = { firstAttachmentBinding.Site with Target = FlowCallTargetIdentity.GeneratedWord(WordId "different-generated-id") } }
    check "attachment binding adapter retains a changed stable target identity"
        (FlowPersistence.attachmentBindings [ changedAttachmentIdentity ] <> [ List.head storedAttachments ])

let private testFlowRewrite () =
    let storedForm = function
        | FlowCallForm.Direct -> StoredCallForm.Direct
        | FlowCallForm.AbsoluteRoot -> StoredCallForm.AbsoluteRoot
        | FlowCallForm.DotStage stage -> StoredCallForm.DotStage stage
        | FlowCallForm.PropertyAccess field -> StoredCallForm.PropertyAccess field
        | FlowCallForm.StaticCallback(stage, qualification) -> StoredCallForm.StaticCallback(stage, qualification)

    let storedTarget = function
        | FlowCallTargetIdentity.UserWord(WordId identity) -> StoredCallTarget.UserWord identity
        | FlowCallTargetIdentity.Primitive(PrimitiveId identity) -> StoredCallTarget.Primitive identity
        | FlowCallTargetIdentity.GeneratedWord(WordId identity) -> StoredCallTarget.GeneratedWord identity

    let bindingForSite (source: SourceRef) caseName bodyRole (site: FlowCallSite) : StoredCallBinding =
        { Source = source
          CaseName = caseName
          BodyRole = bodyRole
          Path = site.Path
          Form = storedForm site.Form
          RequestedName = site.RequestedName
          Target = storedTarget site.Target }

    let wordRows content (sites: FlowCallSite list) =
        let source = Storage.sourceObject StorageObjectKind.WordDefinition content
        sites |> List.map (bindingForSite source.Reference None StoredCallBodyRole.Definition)

    let flatEntries =
        [ wordEntry "pick" [ TInt ] [ TInt ] Set.empty []
          wordEntry "other" [ TInt ] [ TInt ] Set.empty []
          wordEntry "pair" [ TInt ] [ TInt; TInt ] Set.empty [ Call("dup", sourceSpan) ]
          wordEntry "stage" [ TInt; TInt; TInt ] [ TInt ] Set.empty
              [ Call("add", sourceSpan); Call("add", sourceSpan) ] ]
    let flatParameters =
        Map.ofList
            [ "pick", [ "value" ]
              "other", [ "value" ]
              "pair", [ "value" ]
              "stage", [ "receiver"; "second"; "third" ] ]
    let flatContext = loweringContext flatEntries flatParameters
    let pickTarget =
        match flatContext.CompilerContext.WordIds["pick"] with
        | WordId identity -> StoredCallTarget.UserWord identity
    let otherTarget =
        match flatContext.CompilerContext.WordIds["other"] with
        | WordId identity -> StoredCallTarget.UserWord identity
    let pairTarget =
        match flatContext.CompilerContext.WordIds["pair"] with
        | WordId identity -> StoredCallTarget.UserWord identity
    let stageTarget =
        match flatContext.CompilerContext.WordIds["stage"] with
        | WordId identity -> StoredCallTarget.UserWord identity

    let flatCallerText =
        """word rewrite.caller(value: Int, values: List<Int>) -> Int {
    effects none
    let direct = pick(value);
    let absolute = ::pick(value);
    let short = values.map(word pick);
    let root-callback = values.map(::pick);
    let untouched = other(direct);
    let dotted = value.pick();
    let literal = "pick";
    let nested = if true {
        option::some<Int>(pick(value))
    } else {
        option::some<Int>(pick(value))
    };
    let selected = match nested {
        some item => { pick(item) }
        none => { pick(value) }
    };
    return pick(selected)
}"""
    let flatCaller = parseWord flatCallerText
    let flatCompiled = FlowLowering.compileWordWithCallBindings flatContext (WordId "rewrite-caller") flatCaller
    let flatRows = wordRows flatCallerText flatCompiled.CallSites
    let flatRewrite =
        match FlowRewrite.rewriteWord "pick" "choose" pickTarget flatCaller flatRows with
        | Ok value -> assertions <- assertions + 1; value
        | Error problem -> failwith $"Flat Flow rename fixture failed: {Diagnostics.render problem}"

    let expectedFlatRows =
        flatRows
        |> List.map (fun row ->
            if row.Target <> pickTarget then row
            else
                let form =
                    match row.Form with
                    | StoredCallForm.StaticCallback(stage, _) -> StoredCallForm.StaticCallback(stage, FlowWordReferenceQualification.AbsoluteRoot)
                    | StoredCallForm.PropertyAccess field -> StoredCallForm.PropertyAccess field
                    | StoredCallForm.Direct | StoredCallForm.AbsoluteRoot | StoredCallForm.DotStage _ -> StoredCallForm.AbsoluteRoot
                { row with Form = form; RequestedName = "choose" })
    equal "flat rewrite preserves every binding row and stable target while mapping all target forms" expectedFlatRows flatRewrite.Bindings
    check "flat rewrite changes the caller source" flatRewrite.Changed
    equal "caller header is unchanged when another word is renamed" "rewrite.caller" flatRewrite.Definition.Name
    equal "flat rewrite preserves the exact old immutable source refs for Runtime to replace later"
        (flatRows |> List.map (fun row -> row.Source)) (flatRewrite.Bindings |> List.map (fun row -> row.Source))
    let rewrittenFlatText = FlowSource.renderWord flatRewrite.Definition
    check "source literal text equal to the old target name remains literal"
        (rewrittenFlatText.Contains("\"pick\"", StringComparison.Ordinal))
    match flatRewrite.Definition.Body with
    | FlowStatement.Let("direct", FlowExpression.RootCall({ Name = "choose" }, _, _), _)
      :: FlowStatement.Let("absolute", FlowExpression.RootCall({ Name = "choose" }, _, _), _)
      :: FlowStatement.Let("short", FlowExpression.DotCall(_, "map", [ FlowArgument.WordReference shortReference ], _), _)
      :: FlowStatement.Let("root-callback", FlowExpression.DotCall(_, "map", [ FlowArgument.WordReference rootReference ], _), _)
      :: _ ->
        equal "short callback becomes an explicit root callback" FlowWordReferenceQualification.AbsoluteRoot shortReference.Qualification
        equal "root callback retains explicit root qualification" FlowWordReferenceQualification.AbsoluteRoot rootReference.Qualification
        equal "both callback references name the renamed stable target" ("choose", "choose") (shortReference.Name, rootReference.Name)
    | other -> failwithf "Expected flat/root/callback call forms after rewrite, got %A" other

    let rewrittenTargetRows = flatRewrite.Bindings |> List.filter (fun row -> row.Target = pickTarget)
    equal "every targeted nested call remains mapped once" (flatRows |> List.filter (fun row -> row.Target = pickTarget) |> List.length) rewrittenTargetRows.Length
    equal "rewriting simple direct/root/dot calls preserves their structural roots"
        (flatRows |> List.filter (fun row -> row.Target = pickTarget) |> List.map (fun row -> row.Path))
        (rewrittenTargetRows |> List.map (fun row -> row.Path))
    let pathSegments (FlowAstPath.FlowAstPath segments) = segments
    let flatTargetSegments = rewrittenTargetRows |> List.collect (fun row -> pathSegments row.Path)
    for required in
        [ FlowAstPathSegment.IfThenStatement 0
          FlowAstPathSegment.IfElseStatement 0
          FlowAstPathSegment.ContainerPayload
          FlowAstPathSegment.OptionSomeStatement 0
          FlowAstPathSegment.OptionNoneStatement 0 ] do
        check $"target bindings retain nested structural path segment {required}" (flatTargetSegments |> List.contains required)

    let namespaceContext =
        loweringContext
            [ wordEntry "ns.pick" [ TInt ] [ TInt ] Set.empty [] ]
            (Map.ofList [ "ns.pick", [ "value" ] ])
    let namespaceTarget =
        match namespaceContext.CompilerContext.WordIds["ns.pick"] with
        | WordId identity -> StoredCallTarget.UserWord identity
    let namespaceCallerText =
        """word rewrite.namespace(value: Int, values: List<Int>) -> Int {
    effects none
    let direct = ns::pick(value);
    let mapped = values.map(ns::pick);
    return direct
}"""
    let namespaceCaller = parseWord namespaceCallerText
    let namespaceCompiled = FlowLowering.compileWordWithCallBindings namespaceContext (WordId "rewrite-namespace") namespaceCaller
    let namespaceRows = wordRows namespaceCallerText namespaceCompiled.CallSites
    let namespaceRewrite =
        match FlowRewrite.rewriteWord "ns.pick" "modern.pick" namespaceTarget namespaceCaller namespaceRows with
        | Ok value -> assertions <- assertions + 1; value
        | Error problem -> failwith $"Namespaced Flow rename fixture failed: {Diagnostics.render problem}"
    equal "dotted rename retains a qualified ordinary-call form and exact target identity"
        (namespaceRows |> List.map (fun row -> { row with RequestedName = "modern.pick" })) namespaceRewrite.Bindings
    match namespaceRewrite.Definition.Body with
    | FlowStatement.Let("direct", FlowExpression.Call("modern.pick", _, _), _)
      :: FlowStatement.Let("mapped", FlowExpression.DotCall(_, "map", [ FlowArgument.WordReference reference ], _), _)
      :: _ ->
        equal "dotted callback rename retains namespace qualification" FlowWordReferenceQualification.NamespaceQualified reference.Qualification
        equal "dotted callback names the exact renamed namespace identity" "modern.pick" reference.Name
    | other -> failwithf "Expected namespace-qualified call forms after dotted rename, got %A" other

    let qualifiedContext = loweringContext flatEntries flatParameters
    let qualifiedCallerText =
        """word rewrite.qualified(value: Int, values: List<Int>) -> Int {
    effects none
    let direct = pick(value);
    let root = ::pick(value);
    let callback = values.map(::pick);
    return direct
}"""
    let qualifiedCaller = parseWord qualifiedCallerText
    let qualifiedCompiled = FlowLowering.compileWordWithCallBindings qualifiedContext (WordId "rewrite-qualified") qualifiedCaller
    let qualifiedRows = wordRows qualifiedCallerText qualifiedCompiled.CallSites
    let qualifiedRewrite =
        match FlowRewrite.rewriteWord "pick" "modern.pick" pickTarget qualifiedCaller qualifiedRows with
        | Ok value -> assertions <- assertions + 1; value
        | Error problem -> failwith $"Dotted exact-name rewrite fixture failed: {Diagnostics.render problem}"
    equal "root and direct target forms map to exact dotted-name bindings"
        (qualifiedRows
         |> List.map (fun row ->
             if row.Target <> pickTarget then row
             else
                 let form =
                     match row.Form with
                     | StoredCallForm.StaticCallback(stage, _) -> StoredCallForm.StaticCallback(stage, FlowWordReferenceQualification.NamespaceQualified)
                     | StoredCallForm.PropertyAccess field -> StoredCallForm.PropertyAccess field
                     | StoredCallForm.Direct | StoredCallForm.AbsoluteRoot | StoredCallForm.DotStage _ -> StoredCallForm.Direct
                 { row with RequestedName = "modern.pick"; Form = form }))
        qualifiedRewrite.Bindings
    match qualifiedRewrite.Definition.Body with
    | FlowStatement.Let("direct", FlowExpression.Call("modern.pick", _, _), _)
      :: FlowStatement.Let("root", FlowExpression.Call("modern.pick", _, _), _)
      :: FlowStatement.Let("callback", FlowExpression.DotCall(_, "map", [ FlowArgument.WordReference reference ], _), _)
      :: _ ->
        equal "absolute-root callback maps to namespace qualification for dotted names" FlowWordReferenceQualification.NamespaceQualified reference.Qualification
        equal "absolute-root callback maps to the exact dotted target name" "modern.pick" reference.Name
    | other -> failwithf "Expected qualified ordinary call forms after dotted rename, got %A" other

    let pathCallerText =
        """word rewrite.paths(value: Int) -> (Int, Int) {
    effects none
    let (left, right) = pair(value);
    let branch = if true {
        pick(left)
    } else {
        pick(right)
    };
    let wrapped = option::some<Int>(pick(branch));
    let matched = match wrapped {
        some item => { pick(item) }
        none => { pick(value) }
    };
    let result = result::ok<Int, String>(pick(matched));
    let result-value = match result {
        ok item => { pick(item) }
        error message => { pick(value) }
    };
    return (pick(left), other(right))
}"""
    let pathCaller = parseWord pathCallerText
    let pathCompiled = FlowLowering.compileWordWithCallBindings flatContext (WordId "rewrite-paths") pathCaller
    let pathRows = wordRows pathCallerText pathCompiled.CallSites
    let pathRewrite =
        match FlowRewrite.rewriteWord "pick" "choose" pickTarget pathCaller pathRows with
        | Ok value -> assertions <- assertions + 1; value
        | Error problem -> failwith $"Structural path rewrite fixture failed: {Diagnostics.render problem}"
    equal "nested path rewriting preserves all unrelated stable target rows"
        (pathRows |> List.map (fun row -> if row.Target = pickTarget then { row with Form = StoredCallForm.AbsoluteRoot; RequestedName = "choose" } else row))
        pathRewrite.Bindings
    let allPathSegments = pathRewrite.Bindings |> List.collect (fun row -> pathSegments row.Path)
    for required in
        [ FlowAstPathSegment.DestructureInitializer
          FlowAstPathSegment.IfThenStatement 0
          FlowAstPathSegment.IfElseStatement 0
          FlowAstPathSegment.ContainerPayload
          FlowAstPathSegment.OptionSomeStatement 0
          FlowAstPathSegment.OptionNoneStatement 0
          FlowAstPathSegment.ResultOkStatement 0
          FlowAstPathSegment.ResultErrorStatement 0
          FlowAstPathSegment.ReturnOutput 0
          FlowAstPathSegment.ReturnOutput 1 ] do
        check $"rewrite mapping preserves nested/multireturn path {required}" (allPathSegments |> List.contains required)

    let dotContext = loweringContext flatEntries flatParameters
    let dotCallerText =
        """word rewrite.dot(value: Int) -> Int {
    effects none
    let result = other(other(value)).stage(third = other(other(1)), 2);
    return result
}"""
    let dotCaller = parseWord dotCallerText
    let dotCompiled = FlowLowering.compileWordWithCallBindings dotContext (WordId "rewrite-dot") dotCaller
    let dotRows = wordRows dotCallerText dotCompiled.CallSites
    let dotPath =
        dotRows
        |> List.find (fun row -> row.Target = stageTarget && row.Form = StoredCallForm.DotStage "stage")
        |> fun row -> pathSegments row.Path
    let replacePathPrefix
        (prefix: FlowAstPathSegment list)
        (replacement: FlowAstPathSegment list)
        (path: FlowAstPath) =
        let segments = pathSegments path
        if segments.Length >= prefix.Length && List.take prefix.Length segments = prefix then
            FlowAstPath.FlowAstPath(replacement @ List.skip prefix.Length segments)
        else path
    let expectedDotRows newName outputForm receiverSegment argumentSegment =
        dotRows
        |> List.map (fun row ->
            let rowPath = pathSegments row.Path
            let mappedPath =
                if row.Target = stageTarget && row.Form = StoredCallForm.DotStage "stage" then row.Path
                else
                    let receiverPrefix = dotPath @ [ FlowAstPathSegment.DotReceiver ]
                    let argumentIndex =
                        if rowPath.Length > dotPath.Length && List.take dotPath.Length rowPath = dotPath then
                            match rowPath[dotPath.Length] with
                            | FlowAstPathSegment.DotArgument index -> Some index
                            | _ -> None
                        else None
                    if rowPath.Length >= receiverPrefix.Length && List.take receiverPrefix.Length rowPath = receiverPrefix then
                        replacePathPrefix receiverPrefix (dotPath @ [ receiverSegment 0 ]) row.Path
                    else
                        match argumentIndex with
                        | Some index ->
                            let prefix = dotPath @ [ FlowAstPathSegment.DotArgument index ]
                            replacePathPrefix prefix (dotPath @ [ argumentSegment (index + 1) ]) row.Path
                        | None -> row.Path
            if row.Target = stageTarget then
                { row with Path = mappedPath; Form = outputForm; RequestedName = newName }
            else { row with Path = mappedPath })
    let dotRewrite =
        match FlowRewrite.rewriteWord "stage" "choose" stageTarget dotCaller dotRows with
        | Ok value -> assertions <- assertions + 1; value
        | Error problem -> failwith $"Dot-to-root rewrite fixture failed: {Diagnostics.render problem}"
    let expectedRootDotRows = expectedDotRows "choose" StoredCallForm.AbsoluteRoot FlowAstPathSegment.RootCallArgument FlowAstPathSegment.RootCallArgument
    equal "dot-to-root rewrite remaps receiver and all written argument descendants" expectedRootDotRows dotRewrite.Bindings
    match dotRewrite.Definition.Body with
    | FlowStatement.Let("result",
        FlowExpression.RootCall({ Name = "choose" },
            [ FlowArgument.Positional(FlowExpression.Call("other", [ FlowArgument.Positional(FlowExpression.Call("other", _, _)) ], _))
              FlowArgument.Named("third", FlowExpression.Call("other", [ FlowArgument.Positional(FlowExpression.Call("other", _, _)) ], _), _)
              FlowArgument.Positional(FlowExpression.Literal(LInt 2L, _)) ], _), _)
      :: _ -> check "dot receiver is evaluated once and prepended before written mixed arguments" true
    | other -> failwithf "Expected receiver-first root call with preserved mixed argument order, got %A" other

    let dottedDotRewrite =
        match FlowRewrite.rewriteWord "stage" "modern.stage" stageTarget dotCaller dotRows with
        | Ok value -> assertions <- assertions + 1; value
        | Error problem -> failwith $"Dot-to-qualified-call rewrite fixture failed: {Diagnostics.render problem}"
    let expectedQualifiedDotRows = expectedDotRows "modern.stage" StoredCallForm.Direct FlowAstPathSegment.CallArgument FlowAstPathSegment.CallArgument
    equal "dot-to-qualified-call remaps receiver and written arguments to CallArgument paths" expectedQualifiedDotRows dottedDotRewrite.Bindings
    match dottedDotRewrite.Definition.Body with
    | FlowStatement.Let("result", FlowExpression.Call("modern.stage", FlowArgument.Positional _ :: FlowArgument.Named("third", _, _) :: FlowArgument.Positional _ :: _, _), _) :: _ ->
        check "dotted dot fallback emits a qualified ordinary call in written order" true
    | other -> failwithf "Expected receiver-first qualified Call form, got %A" other

    let testText =
        """test pick/roles {
    pick(5)
    => value pick(6)
}"""
    let testDefinition = parseTest testText
    let testProgram = Compiler.compileIrProgram flatContext.CompilerContext
    let compiledTest = FlowLowering.compileTestWithCallBindings flatContext testProgram testDefinition
    let testSource = Storage.sourceObject StorageObjectKind.TestDefinition testText
    let testRows =
        compiledTest.CallSites
        |> Map.toList
        |> List.collect (fun (role, sites) ->
            let storedRole =
                match role with
                | FlowAttachmentBodyRole.Actual -> StoredCallBodyRole.Actual
                | FlowAttachmentBodyRole.ExpectedExpression -> StoredCallBodyRole.ExpectedExpression
            sites |> List.map (bindingForSite testSource.Reference (Some testDefinition.CaseName) storedRole))
    let rewrittenTest =
        match FlowRewrite.rewriteTest "pick" "choose" pickTarget testDefinition testRows with
        | Ok value -> assertions <- assertions + 1; value
        | Error problem -> failwith $"Test attachment rewrite fixture failed: {Diagnostics.render problem}"
    equal "test header follows rename when the test is attached to the renamed owner" "choose" rewrittenTest.Definition.Word
    equal "test attachment retains both distinct body roles and their root paths"
        (testRows |> List.map (fun row -> { row with Form = StoredCallForm.AbsoluteRoot; RequestedName = "choose" })) rewrittenTest.Bindings
    check "actual test call rewrites independently"
        (match rewrittenTest.Definition.Body with | [ FlowStatement.Evaluate(FlowExpression.RootCall({ Name = "choose" }, _, _)) ] -> true | _ -> false)
    check "expected-expression test call rewrites independently"
        (match rewrittenTest.Definition.Expected with | FlowTestExpectation.Expression(FlowExpression.RootCall({ Name = "choose" }, _, _)) -> true | _ -> false)
    equal "test binding input contains actual and expected-expression sites" 2 rewrittenTest.Bindings.Length
    check "test case name and old source reference remain attached until Runtime rehashes the rendered source"
        (rewrittenTest.Bindings |> List.forall (fun row -> row.CaseName = Some "roles" && row.Source = testSource.Reference))

    let callerTestText = "test rewrite.caller/does-not-rename-owner {\n    pick(1)\n    => 1\n}"
    let callerTest = parseTest callerTestText
    let callerTestCompiled = FlowLowering.compileTestWithCallBindings flatContext testProgram callerTest
    let callerTestSource = Storage.sourceObject StorageObjectKind.TestDefinition callerTestText
    let callerTestRows =
        callerTestCompiled.CallSites
        |> Map.toList
        |> List.collect (fun (role, sites) ->
            let storedRole = if role = FlowAttachmentBodyRole.Actual then StoredCallBodyRole.Actual else StoredCallBodyRole.ExpectedExpression
            sites |> List.map (bindingForSite callerTestSource.Reference (Some callerTest.CaseName) storedRole))
    let callerTestRewrite =
        match FlowRewrite.rewriteTest "pick" "choose" pickTarget callerTest callerTestRows with
        | Ok value -> assertions <- assertions + 1; value
        | Error problem -> failwith $"Unrelated test owner fixture failed: {Diagnostics.render problem}"
    equal "caller test owner stays unchanged while its resolved call target is rewritten" "rewrite.caller" callerTestRewrite.Definition.Word

    let exampleText =
        """example pick/visible {
    pick(5)
    => 5
}"""
    let exampleDefinition = parseExample exampleText
    let exampleSource = Storage.sourceObject StorageObjectKind.ExampleDefinition exampleText
    let exampleRow: StoredCallBinding =
        { Source = exampleSource.Reference
          CaseName = Some exampleDefinition.CaseName
          BodyRole = StoredCallBodyRole.Actual
          Path = FlowAstPath.FlowAstPath [ FlowAstPathSegment.BlockStatement 0; FlowAstPathSegment.EvaluateExpression ]
          Form = StoredCallForm.Direct
          RequestedName = "pick"
          Target = pickTarget }
    let rewrittenExample =
        match FlowRewrite.rewriteExample "pick" "choose" pickTarget exampleDefinition [ exampleRow ] with
        | Ok value -> assertions <- assertions + 1; value
        | Error problem -> failwith $"Example attachment rewrite fixture failed: {Diagnostics.render problem}"
    equal "example header follows owner rename" "choose" rewrittenExample.Definition.Word
    equal "example actual binding retains its role and stable target with transformed call metadata"
        [ { exampleRow with Form = StoredCallForm.AbsoluteRoot; RequestedName = "choose" } ] rewrittenExample.Bindings
    check "example actual call is rewritten"
        (match rewrittenExample.Definition.Body with | [ FlowStatement.Evaluate(FlowExpression.RootCall({ Name = "choose" }, _, _)) ] -> true | _ -> false)

    let ownerText =
        """word pick(value: Int) -> Int {
    effects none
    let label = "pick";
    return value
}"""
    let ownerDefinition = parseWord ownerText
    let rewrittenOwner =
        match FlowRewrite.rewriteWord "pick" "choose" pickTarget ownerDefinition [] with
        | Ok value -> assertions <- assertions + 1; value
        | Error problem -> failwith $"Owner-header rewrite fixture failed: {Diagnostics.render problem}"
    equal "renamed definition header changes while retaining stable source-level owner identity" "choose" rewrittenOwner.Definition.Name
    check "owner-only rewrite changes source bytes" rewrittenOwner.Changed
    check "owner-only rewrite preserves literal text identical to the old word name"
        ((FlowSource.renderWord rewrittenOwner.Definition).Contains("\"pick\"", StringComparison.Ordinal))

    let missingBinding = FlowRewrite.rewriteWord "pick" "choose" pickTarget flatCaller (List.tail flatRows)
    match missingBinding with
    | Error problem -> equal "incomplete persisted site inventory is rejected" "FLOW_REWRITE_BINDING_MISSING" problem.Code
    | Ok _ -> failwith "Flow rewrite accepted an incomplete persisted binding inventory."
    let duplicateBinding = FlowRewrite.rewriteWord "pick" "choose" pickTarget flatCaller (flatRows @ [ List.head flatRows ])
    match duplicateBinding with
    | Error problem -> equal "duplicate persisted site key is rejected" "FLOW_REWRITE_BINDING_DUPLICATE" problem.Code
    | Ok _ -> failwith "Flow rewrite accepted duplicate persisted binding rows."
    let mismatchedBinding =
        flatRows
        |> List.mapi (fun index row -> if index = 0 then { row with Form = StoredCallForm.AbsoluteRoot } else row)
        |> FlowRewrite.rewriteWord "pick" "choose" pickTarget flatCaller
    match mismatchedBinding with
    | Error problem -> equal "persisted form/name must match the authored AST site" "FLOW_REWRITE_BINDING_MISMATCH" problem.Code
    | Ok _ -> failwith "Flow rewrite accepted a binding whose form does not match its authored call."
    let unmappedBinding =
        flatRows
        |> List.mapi (fun index row -> if index = 0 then { row with Path = FlowAstPath.FlowAstPath [] } else row)
        |> FlowRewrite.rewriteWord "pick" "choose" pickTarget flatCaller
    match unmappedBinding with
    | Error problem -> equal "binding path must map to an authored call site" "FLOW_REWRITE_BINDING_UNMAPPED" problem.Code
    | Ok _ -> failwith "Flow rewrite accepted an unmapped structural path."

    let callbackReference: FlowWordReference =
        { Name = "pick"
          Qualification = FlowWordReferenceQualification.ExplicitShort
          Span = sourceSpan }
    let malformedCallback =
        { flatCaller with
            Body =
                [ FlowStatement.Evaluate(
                    FlowExpression.DotCall(
                        FlowExpression.Local("values", sourceSpan), "map",
                        [ FlowArgument.WordReference callbackReference
                          FlowArgument.Positional(FlowExpression.Literal(LInt 1L, sourceSpan)) ], sourceSpan)) ] }
    match FlowRewrite.rewriteWord "pick" "choose" pickTarget malformedCallback [] with
    | Error problem -> equal "malformed host-built callback shape is rejected before rewrite" "FLOW_REWRITE_CALLBACK_SHAPE" problem.Code
    | Ok _ -> failwith "Flow rewrite accepted a malformed host-built callback call."

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

let private testFlowProjectDocumentParser () =
    let file = "<flow-project>"
    let recordSource =
        """record Customer {
    field email: Email;
    field balance: Float;
}"""
    let scalarSource =
        """type Email : String {
    validate email::valid;
}"""
    let validatorSource =
        """word email.valid(value: String) -> Bool {
    effects none
    true
}"""
    let identitySource =
        """word customer.identity(value: Customer) -> Customer {
    effects none
    value
}"""
    let firstTestSource =
        """test email.valid/literal {
    true
    => true
}"""
    let secondTestSource =
        """test customer.identity/literal {
    17
    => 17
}"""
    let exampleSource =
        """example customer.identity/sample {
    17
    => 17
}"""
    let source =
        [ recordSource
          scalarSource
          validatorSource
          identitySource
          firstTestSource
          secondTestSource
          exampleSource ]
        |> String.concat "\n"
    let parseDocument text =
        match FlowParser.parseDocument file text with
        | Ok document -> document
        | Error diagnostic -> failwith (Diagnostics.render diagnostic)
    let document = parseDocument source

    equal "project parser preserves source syntax version" 1 document.SyntaxVersion
    equal "project parser retains exact complete authored bytes" source document.SourceText
    equal "project parser retains record source order" [ "Customer" ] (document.Records |> List.map (fun record -> record.Name))
    equal "record field order and resolved syntax types are retained"
        [ "email", TNamed "Email"; "balance", TFloat ]
        (document.Records.Head.Fields |> List.map (fun field -> field.Name, field.Type))
    equal "project parser retains scalar source order" [ "Email" ] (document.Scalars |> List.map (fun scalar -> scalar.Name))
    equal "scalar base and qualified validator are retained" (TString, Some "email.valid")
        (document.Scalars.Head.BaseType, document.Scalars.Head.Validator)
    equal "project parser retains word source order" [ "email.valid"; "customer.identity" ] (document.Words |> List.map (fun word -> word.Name))
    equal "validator word signature is parsed" ([ TString ], [ TBool ])
        (document.Words.Head.Parameters |> List.map (fun parameter -> parameter.Type), document.Words.Head.Outputs)
    equal "dependent word names its authored record type" ([ TNamed "Customer" ], [ TNamed "Customer" ])
        (document.Words[1].Parameters |> List.map (fun parameter -> parameter.Type), document.Words[1].Outputs)
    equal "project parser retains attached test order" [ "email.valid", "literal"; "customer.identity", "literal" ]
        (document.Tests |> List.map (fun test -> test.Word, test.CaseName))
    equal "project parser retains attached examples" [ "customer.identity", "sample" ]
        (document.Examples |> List.map (fun example -> example.Word, example.CaseName))

    let assertGlobalSpan name (memberSource: string) (memberSpan: SourceSpan) =
        let offset = source.IndexOf(memberSource, StringComparison.Ordinal)
        check (name + " source slice occurs in project") (offset >= 0)
        equal (name + " span reports the document file") file memberSpan.File
        let prefix = source.Substring(0, offset)
        let expectedLine = 1 + (prefix |> Seq.filter ((=) '\n') |> Seq.length)
        let lastLineBreak = prefix.LastIndexOf('\n')
        let expectedColumn = offset - lastLineBreak
        equal (name + " span line is document-global") expectedLine memberSpan.Line
        equal (name + " span column is document-global") expectedColumn memberSpan.Column
        equal (name + " span covers the exact local source slice") memberSource.Length memberSpan.Length

    for record in document.Records do
        equal "record source bytes are its exact declaration slice" recordSource record.SourceText
        assertGlobalSpan "record" record.SourceText record.Span
    for scalar in document.Scalars do
        equal "scalar source bytes are its exact declaration slice" scalarSource scalar.SourceText
        assertGlobalSpan "scalar" scalar.SourceText scalar.Span
    for word in document.Words do
        assertGlobalSpan ("word " + word.Name) word.SourceText word.Span
    for test in document.Tests do
        assertGlobalSpan ("test " + test.CaseName) test.SourceText test.Span
    for example in document.Examples do
        assertGlobalSpan ("example " + example.CaseName) example.SourceText example.Span
    let parameter = document.Words.Head.Parameters.Head
    assertGlobalSpan "nested parameter" "value: String" parameter.Span

    let expectedRecord =
        """record Customer {
    field email: Email;
    field balance: Float;
}"""
    let expectedScalar =
        """type Email : String {
    validate email::valid;
}"""
    equal "Flow record renderer emits canonical field declarations" expectedRecord (FlowSource.renderRecord document.Records.Head)
    equal "Flow scalar renderer emits canonical qualified validator" expectedScalar (FlowSource.renderScalar document.Scalars.Head)

    let canonical = FlowSource.renderDocument document
    let canonicalAgain = canonical |> parseDocument |> FlowSource.renderDocument
    equal "mixed Flow project rendering is canonical and idempotent" canonical canonicalAgain
    let declarationOffset (text: string) = canonical.IndexOf(text, StringComparison.Ordinal)
    check "document renderer writes records before scalars" (declarationOffset "record Customer" < declarationOffset "type Email")
    check "document renderer writes scalars before words" (declarationOffset "type Email" < declarationOffset "word email.valid")
    check "document renderer writes words before tests" (declarationOffset "word customer.identity" < declarationOffset "test email.valid/literal")
    check "document renderer writes tests before examples" (declarationOffset "test customer.identity/literal" < declarationOffset "example customer.identity/sample")

    let reparsed = parseDocument canonical
    let semanticProjection (value: FlowProjectDocument) =
        (value.Records |> List.map (fun record -> record.Name, record.Fields |> List.map (fun field -> field.Name, field.Type)),
         value.Scalars |> List.map (fun scalar -> scalar.Name, scalar.BaseType, scalar.Validator),
         value.Words |> List.map (fun word -> word.Name, word.Parameters |> List.map (fun parameter -> parameter.Name, parameter.Type), word.Outputs, word.Effects, FlowSource.renderWord word),
         value.Tests |> List.map (fun test -> test.Word, test.CaseName, FlowSource.renderTest test),
         value.Examples |> List.map (fun example -> example.Word, example.CaseName, FlowSource.renderExample example))
    equal "parse/render preserves mixed document declaration semantics" (semanticProjection document) (semanticProjection reparsed)

    let expectDocumentError name code badSource =
        let diagnostic = expectError name code (FlowParser.parseDocument file badSource)
        check (name + " has a diagnostic message") (not (String.IsNullOrWhiteSpace diagnostic.Message))
        match diagnostic.Span with
        | Some diagnosticSpan ->
            equal (name + " diagnostic points to the document") file diagnosticSpan.File
            check (name + " diagnostic has positive source coordinates") (diagnosticSpan.Line > 0 && diagnosticSpan.Column > 0 && diagnosticSpan.Length > 0)
        | None -> failwith (name + ": expected a source span on the structured diagnostic")

    expectDocumentError "empty records are rejected" "FLOW_RECORD_EMPTY" "record Empty { }"
    expectDocumentError "duplicate record fields are rejected" "FLOW_RECORD_DUPLICATE_FIELD"
        "record Customer { field email: String; field email: Float; }"
    expectDocumentError "built-in type names cannot be redeclared" "FLOW_TYPE_NAME_INVALID" "type Int : String { }"
    expectDocumentError "unsupported scalar primitive bases are rejected" "FLOW_SCALAR_BASE_UNSUPPORTED" "type Flag : Bool { }"
    expectDocumentError "container scalar bases are rejected" "FLOW_SCALAR_BASE_UNSUPPORTED" "type Tags : List<String> { }"
    expectDocumentError "record scalar bases are rejected" "FLOW_SCALAR_BASE_UNSUPPORTED"
        "record Customer { field email: String; } type CustomerId : Customer { }"
    expectDocumentError "duplicate validators are rejected" "FLOW_SCALAR_VALIDATOR_DUPLICATE"
        "type Email : String { validate email::valid; validate email::check; }"
    expectDocumentError "short scalar validators require qualification" "FLOW_SCALAR_VALIDATOR_QUALIFICATION"
        "type Email : String { validate valid; }"
    expectDocumentError "dotted scalar validators require namespace qualification" "FLOW_SCALAR_VALIDATOR_QUALIFICATION"
        "type Email : String { validate email.valid; }"
    expectDocumentError "scalar validators cannot be written as calls" "FLOW_SCALAR_VALIDATOR_CALL"
        "type Email : String { validate email::valid(); }"
    expectDocumentError "record fields require a semicolon" "FLOW_RECORD_FIELD_SEMICOLON"
        "record Customer { field email: String }"
    expectDocumentError "incomplete top-level declarations are structured" "FLOW_INCOMPLETE_INPUT"
        "record Customer { field email: String;"
    expectDocumentError "duplicate project type names are rejected across categories" "FLOW_PROJECT_DUPLICATE_TYPE"
        "record Customer { field email: String; } type Customer : String { }"
    expectDocumentError "unknown project declarations are structured" "FLOW_PROJECT_UNKNOWN_DECLARATION"
        "namespace account { }"

    let nestedType = String.replicate 130 "List<" + "Int" + String.replicate 130 ">"
    expectDocumentError "project type nesting limit is enforced" "FLOW_NESTING_LIMIT"
        ($"record Deep {{ field value: {nestedType}; }}")
    expectDocumentError "project source length limit is enforced" "FLOW_SOURCE_LIMIT" (String.replicate 1_000_001 " ")
    expectDocumentError "project token limit is enforced" "FLOW_TOKEN_LIMIT" (String.replicate 100_001 "x ")

    expectError "standalone word parser still rejects trailing declarations" "FLOW_TRAILING_INPUT"
        (FlowParser.parseWord file (validatorSource + "\n" + identitySource)) |> ignore
    expectError "standalone test parser still rejects trailing declarations" "FLOW_TRAILING_INPUT"
        (FlowParser.parseTest file (firstTestSource + "\n" + secondTestSource)) |> ignore
    expectError "standalone example parser still rejects trailing declarations" "FLOW_TRAILING_INPUT"
        (FlowParser.parseExample file (exampleSource + "\n" + exampleSource)) |> ignore

[<EntryPoint>]
let main _ =
    testParserLocationsAndQualification ()
    testIterativeAstDepthLimit ()
    testFlow2Frontend ()
    testSparseFlowSourceMarkerAllocation ()
    testFlowSourceCanonicalRoundTrip ()
    testContainerAndMatchSyntaxRoundTrip ()
    testContainerAndMatchDiagnostics ()
    testStaticListCallbacks ()
    testStaticListFold ()
    testAbsoluteRootAddressing ()
    testLoweringAndExecution ()
    testContainerAndMatchLowering ()
    testFlowOutputVectors ()
    testFlowAuthoredCases ()
    testFlowBatchForwardResolution ()
    testFlowBatchSignatureKindsAndNamedArguments ()
    testFlowBatchReplacementAndValidation ()
    testFlowWordRehydrate ()
    testFlowBatchFinalValidationAndOrigins ()
    testFlowCallBindingSources ()
    testFlowCallBindingStructuralPaths ()
    testFlowAttachmentCallBindings ()
    testFlowPersistenceBindings ()
    testFlowRewrite ()
    testFlowDiagnostics ()
    testFlowProjectDocumentParser ()
    printfn "Flow tests passed: %d assertions" assertions
    0
