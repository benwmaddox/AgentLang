namespace AgentLang

open System.Collections.Generic

/// Versioned source syntax for the explicit data-flow frontend. These nodes
/// are authoring metadata only; executable expressions lower to the existing
/// Expr and verified semantic IR.
[<RequireQualifiedAccess>]
type FlowContainerConstructor =
    | ListEmpty
    | ListSingleton
    | OptionNone
    | OptionSome
    | ResultOk
    | ResultError

type FlowTypeArgument =
    { Type: LangType
      Span: SourceSpan }

[<RequireQualifiedAccess>]
type FlowWordReferenceQualification =
    | ExplicitShort
    | NamespaceQualified
    | AbsoluteRoot

type FlowRootTarget =
    { Name: string
      /// Authored span of the leading `::` and its unqualified name.
      Span: SourceSpan }

/// Stable structural positions for binding records. Paths distinguish authored
/// nodes even when a host-built AST assigns them identical source spans.
[<RequireQualifiedAccess; StructuralEquality; StructuralComparison>]
type FlowAstPathSegment =
    | BlockStatement of int
    | LetInitializer
    | DestructureInitializer
    | EvaluateExpression
    | ReturnOutput of int
    | CallArgument of int
    | RootCallArgument of int
    | DotReceiver
    | DotArgument of int
    | PropertyReceiver
    | EqualityLeft
    | EqualityRight
    | IfCondition
    | IfThenStatement of int
    | IfElseStatement of int
    | ContainerPayload
    | OptionScrutinee
    | OptionSomeStatement of int
    | OptionNoneStatement of int
    | ResultScrutinee
    | ResultOkStatement of int
    | ResultErrorStatement of int
    | EnumScrutinee
    | EnumCaseStatement of int * int

[<RequireQualifiedAccess; StructuralEquality; StructuralComparison>]
type FlowAstPath = FlowAstPath of FlowAstPathSegment list

type FlowWordReference =
    { Name: string
      Qualification: FlowWordReferenceQualification
      Span: SourceSpan }

[<RequireQualifiedAccess>]
type FlowExpression =
    | Literal of Literal * SourceSpan
    | Local of string * SourceSpan
    | Call of string * FlowArgument list * SourceSpan
    | RootCall of FlowRootTarget * FlowArgument list * SourceSpan
    | DotCall of FlowExpression * string * FlowArgument list * SourceSpan
    | Property of FlowExpression * string * SourceSpan
    | Equality of FlowExpression * FlowExpression * SourceSpan
    | If of FlowExpression * FlowStatement list * FlowStatement list * SourceSpan
    | Container of FlowContainerConstructor * FlowTypeArgument list * FlowExpression option * SourceSpan
    | MatchOption of FlowExpression * FlowPayloadCase * FlowCaseBlock * SourceSpan
    | MatchResult of FlowExpression * FlowPayloadCase * FlowPayloadCase * SourceSpan
    | MatchEnum of FlowExpression * FlowEnumCase list * SourceSpan

and [<RequireQualifiedAccess>] FlowArgument =
    | Positional of FlowExpression
    | Named of string * FlowExpression * SourceSpan
    | WordReference of FlowWordReference

and [<RequireQualifiedAccess>] FlowStatement =
    | Let of string * FlowExpression * SourceSpan
    | LetMany of (string * SourceSpan) list * FlowExpression * SourceSpan
    | Evaluate of FlowExpression
    | Return of FlowExpression list * SourceSpan

and FlowCaseBlock =
    { Statements: FlowStatement list
      Span: SourceSpan }

and FlowPayloadCase =
    { Name: string
      NameSpan: SourceSpan
      Statements: FlowStatement list
      Span: SourceSpan }

and FlowEnumCase =
    { Name: string
      NameSpan: SourceSpan
      Statements: FlowStatement list
      Span: SourceSpan }

type FlowParameter =
    { Name: string
      Type: LangType
      Span: SourceSpan }

type FlowWordDefinition =
    { Name: string
      Parameters: FlowParameter list
      Outputs: LangType list
      Effects: Set<string>
      EffectsDeclared: bool
      Documentation: string
      Body: FlowStatement list
      SourceText: string
      Span: SourceSpan
      SyntaxVersion: int }

[<RequireQualifiedAccess>]
type FlowTestExpectation =
    | Literal of Literal * SourceSpan
    | RuntimeError of string * SourceSpan
    | Expression of FlowExpression

type FlowTestDefinition =
    { Word: string
      CaseName: string
      Body: FlowStatement list
      Expected: FlowTestExpectation
      EffectAssertion: EffectCountAssertion option
      SourceText: string
      Span: SourceSpan
      SyntaxVersion: int
      HeaderSpan: SourceSpan
      ExpectationSpan: SourceSpan }

type FlowExampleDefinition =
    { Word: string
      CaseName: string
      Body: FlowStatement list
      Expected: Literal
      ExpectedSpan: SourceSpan
      SourceText: string
      Span: SourceSpan
      SyntaxVersion: int
      HeaderSpan: SourceSpan
      ExpectationSpan: SourceSpan }

/// A parsed Flow project document. Declarations retain their own exact source
/// slices and source spans, while SourceText retains the complete input bytes.
type FlowProjectDocument =
    { SyntaxVersion: int
      SourceText: string
      Records: RecordDefinition list
      Scalars: ScalarTypeDefinition list
      Enums: EnumDefinition list
      Words: FlowWordDefinition list
      Tests: FlowTestDefinition list
      Examples: FlowExampleDefinition list }

type FlowSourceProjection =
    { AuthoredSpans: Set<SourceSpan>
      /// Zero-length temporary/scope spans point at authored source positions.
      /// They are implementation sites, not independent library-test obligations.
      SyntheticOrigins: Map<SourceSpan, SourceSpan> }

type FlowLoweredExpression =
    { Expressions: Expr list
      SourceText: string
      SyntaxVersion: int
      Projection: FlowSourceProjection }

/// Parameter names and original Flow source stay beside the legacy-compatible
/// WordDefinition until durable frontend metadata is implemented in phase 3.
type FlowLoweredWord =
    { Definition: WordDefinition
      ParameterNames: string list
      SourceText: string
      SyntaxVersion: int
      Projection: FlowSourceProjection }

type FlowLoweredTest =
    { Definition: TestDefinition
      SourceText: string
      SyntaxVersion: int
      Projection: FlowSourceProjection }

type FlowLoweredExample =
    { Definition: ExampleDefinition
      SourceText: string
      SyntaxVersion: int
      Projection: FlowSourceProjection }

/// Shared structural limits and validation for source-authored and host-built
/// Flow trees. This walk is iterative so recursive consumers can rely on the
/// same bound before rendering or lowering an expression.
module FlowStructure =
    let maxExpressionDepth = 128
    let maxExpandedNodes = 100000

    type private Node =
        | ExpressionNode of FlowExpression * int
        | TypeNode of LangType * int * SourceSpan

    let private expressionSpan = function
        | FlowExpression.Literal(_, span)
        | FlowExpression.Local(_, span)
        | FlowExpression.Call(_, _, span)
        | FlowExpression.RootCall(_, _, span)
        | FlowExpression.DotCall(_, _, _, span)
        | FlowExpression.Property(_, _, span)
        | FlowExpression.Equality(_, _, span)
        | FlowExpression.If(_, _, _, span)
        | FlowExpression.Container(_, _, _, span)
        | FlowExpression.MatchOption(_, _, _, span)
        | FlowExpression.MatchResult(_, _, _, span)
        | FlowExpression.MatchEnum(_, _, span) -> span

    let private isIdentifierName (value: string) =
        not (System.String.IsNullOrEmpty value)
        && (System.Char.IsLetter value[0] || value[0] = '_')
        && (value |> Seq.skip 1 |> Seq.forall (fun ch -> System.Char.IsLetterOrDigit ch || ch = '_' || ch = '-' || ch = '?' || ch = '!'))

    let private validateRootTarget (target: FlowRootTarget) =
        if not (isIdentifierName target.Name) then
            Diagnostics.raiseError "FLOW_ROOT_TARGET_INVALID" "An absolute-root call must name one unqualified dictionary key." (Some target.Name) (Some target.Span) [ "::identifier" ] [ target.Name ]
        if target.Span.Length < target.Name.Length + 2 then
            Diagnostics.raiseError "FLOW_ROOT_TARGET_SPAN_INVALID" "An absolute-root target span must cover its leading `::` and identifier." (Some target.Name) (Some target.Span) [ string (target.Name.Length + 2) ] [ string target.Span.Length ]

    let private validateWordReference (reference: FlowWordReference) =
        let pieces = if System.String.IsNullOrEmpty reference.Name then [||] else reference.Name.Split('.')
        let validName = pieces.Length > 0 && (pieces |> Array.forall isIdentifierName)
        let validQualification =
            match reference.Qualification with
            | FlowWordReferenceQualification.ExplicitShort
            | FlowWordReferenceQualification.AbsoluteRoot -> pieces.Length = 1
            | FlowWordReferenceQualification.NamespaceQualified -> pieces.Length >= 2
        if not validName || not validQualification then
            let expected =
                match reference.Qualification with
                | FlowWordReferenceQualification.ExplicitShort -> "word shortName"
                | FlowWordReferenceQualification.NamespaceQualified -> "namespace::word"
                | FlowWordReferenceQualification.AbsoluteRoot -> "::rootName"
            Diagnostics.raiseError "FLOW_CALLBACK_REFERENCE_SHAPE" "Static callback references must preserve a valid qualification kind and matching name shape." None (Some reference.Span) [ expected ] [ reference.Name ]

    let private validateCaseHeader kind word caseName span =
        let validWord =
            not (System.String.IsNullOrEmpty word)
            && (word.Split('.') |> Array.forall isIdentifierName)
        if not validWord then
            Diagnostics.raiseError "FLOW_CASE_OWNER_NAME_INVALID" $"A Flow {kind} owner must use a canonical dotted word name." (Some word) (Some span) [ "word.name" ] [ word ]
        if not (isIdentifierName caseName) then
            Diagnostics.raiseError "FLOW_CASE_NAME_INVALID" $"A Flow {kind} case name must be an identifier." (Some word) (Some span) [ "case-name" ] [ caseName ]

    let private validateSyntaxVersion syntaxVersion owner span =
        if syntaxVersion <> 1 && syntaxVersion <> 2 then
            Diagnostics.raiseError "FLOW_VERSION_UNSUPPORTED" "Only Flow syntax versions 1 and 2 are supported." owner span [ "1"; "2" ] [ string syntaxVersion ]

    let private validateStructure syntaxVersion owner span (expressionRoots: FlowExpression list) (typeRoots: seq<LangType * SourceSpan>) (statementRoots: FlowStatement list) =
        validateSyntaxVersion syntaxVersion owner span
        let pending = Stack<Node>()
        let mutable scheduledNodes = 0
        let charge source =
            if scheduledNodes >= maxExpandedNodes then
                Diagnostics.raiseError "FLOW_STRUCTURE_LIMIT" $"Flow syntax exceeds the expanded structural-node budget of {maxExpandedNodes}." None (Some source) [] []
            scheduledNodes <- scheduledNodes + 1

        let schedule node source =
            charge source
            pending.Push node
        for expression in expressionRoots do schedule (ExpressionNode(expression, 1)) (expressionSpan expression)
        for typeValue, source in typeRoots do schedule (TypeNode(typeValue, 1, source)) source

        let validateBindings bindings span =
            if List.isEmpty bindings then
                Diagnostics.raiseError "FLOW_DESTRUCTURE_EMPTY" "A destructuring binding must name every output." None (Some span) [ "one or more names" ] []

            // Binding names are structural nodes too. Charge them in a first
            // streaming pass so oversized host-built patterns fail before
            // allocating a name list/set or scanning for duplicates.
            for _, nameSpan in bindings do charge nameSpan

            let names = HashSet<string>(System.StringComparer.Ordinal)
            for name, nameSpan in bindings do
                if System.String.IsNullOrEmpty name || name = "_" then
                    Diagnostics.raiseError "FLOW_DESTRUCTURE_NAME" "Destructuring names must be real local names; '_' is not a discard pattern." None (Some nameSpan) [ "named local" ] [ name ]
                elif not (names.Add name) then
                    Diagnostics.raiseError "FLOW_DESTRUCTURE_DUPLICATE" $"Destructuring local '{name}' is repeated." None (Some nameSpan) [] [ name ]

        let scheduleStatement depth statement =
            match statement with
            | FlowStatement.Let(_, value, _)
            | FlowStatement.LetMany(_, value, _)
            | FlowStatement.Evaluate value -> schedule (ExpressionNode(value, depth)) (expressionSpan value)
            | FlowStatement.Return(values, _) ->
                for value in values do schedule (ExpressionNode(value, depth)) (expressionSpan value)
        let scheduleArguments depth arguments =
            for argument in arguments do
                match argument with
                | FlowArgument.Positional expression -> schedule (ExpressionNode(expression, depth)) (expressionSpan expression)
                | FlowArgument.Named(_, expression, _) -> schedule (ExpressionNode(expression, depth)) (expressionSpan expression)
                | FlowArgument.WordReference reference -> validateWordReference reference
        let scheduleStatements depth statements =
            let validateStatement isTerminal statement =
                match statement with
                | FlowStatement.LetMany(bindings, _, span) -> validateBindings bindings span
                | FlowStatement.Return(values, span) when List.isEmpty values ->
                    Diagnostics.raiseError "FLOW_RETURN_EMPTY" "A return vector must contain at least one scalar expression." None (Some span) [ "one or more values" ] []
                | FlowStatement.Return(_, span) when not isTerminal ->
                    Diagnostics.raiseError "FLOW_RETURN_NOT_TERMINAL" "A return vector must be the final statement in its lexical block." None (Some span) [ "terminal return" ] [ "following statement" ]
                | _ -> ()

            let rec scheduleRemaining remaining =
                match remaining with
                | [] -> ()
                | statement :: following ->
                    validateStatement (List.isEmpty following) statement
                    scheduleStatement depth statement
                    scheduleRemaining following

            // Inspect one statement at a time. In particular, do not index or
            // copy an entire attacker-sized block before charging its nodes.
            scheduleRemaining statements
        scheduleStatements 1 statementRoots
        let scheduleType depth typeValue source = schedule (TypeNode(typeValue, depth, source)) source
        while pending.Count > 0 do
            match pending.Pop() with
            | TypeNode(typeValue, depth, source) ->
                if depth > maxExpressionDepth then
                    Diagnostics.raiseError "FLOW_NESTING_LIMIT" $"Flow syntax exceeds the nesting limit of {maxExpressionDepth}." None (Some source) [] []
                match typeValue with
                | TList item | TOption item -> scheduleType (depth + 1) item source
                | TResult(okType, errorType) ->
                    scheduleType (depth + 1) okType source
                    scheduleType (depth + 1) errorType source
                | TInt | TFloat | TBool | TString | TUnit | TNamed _ | TVar _ -> ()
            | ExpressionNode(expression, depth) ->
                if depth > maxExpressionDepth then
                    let source = expressionSpan expression
                    Diagnostics.raiseError "FLOW_NESTING_LIMIT" $"Flow syntax exceeds the nesting limit of {maxExpressionDepth}." None (Some source) [] []
                match expression with
                | FlowExpression.Literal _ | FlowExpression.Local _ -> ()
                | FlowExpression.Call(_, arguments, _) -> scheduleArguments (depth + 1) arguments
                | FlowExpression.RootCall(target, arguments, _) ->
                    validateRootTarget target
                    scheduleArguments (depth + 1) arguments
                | FlowExpression.DotCall(receiver, _, arguments, _) ->
                    schedule (ExpressionNode(receiver, depth + 1)) (expressionSpan receiver)
                    scheduleArguments (depth + 1) arguments
                | FlowExpression.Property(receiver, _, propertySpan) ->
                    if syntaxVersion <> 2 then
                        Diagnostics.raiseError "FLOW_SYNTAX_VERSION" "Record property access requires Flow/2 syntax." owner (Some propertySpan) [ "Flow/2" ] [ $"Flow/{syntaxVersion}" ]
                    schedule (ExpressionNode(receiver, depth + 1)) (expressionSpan receiver)
                | FlowExpression.Equality(left, right, equalitySpan) ->
                    if syntaxVersion <> 2 then
                        Diagnostics.raiseError "FLOW_SYNTAX_VERSION" "The '==' operator requires Flow/2 syntax." owner (Some equalitySpan) [ "Flow/2" ] [ $"Flow/{syntaxVersion}" ]
                    schedule (ExpressionNode(left, depth + 1)) (expressionSpan left)
                    schedule (ExpressionNode(right, depth + 1)) (expressionSpan right)
                | FlowExpression.If(condition, thenBody, elseBody, _) ->
                    schedule (ExpressionNode(condition, depth + 1)) (expressionSpan condition)
                    scheduleStatements (depth + 1) thenBody
                    scheduleStatements (depth + 1) elseBody
                | FlowExpression.Container(_, typeArguments, payload, _) ->
                    for argument in typeArguments do scheduleType 1 argument.Type argument.Span
                    match payload with
                    | Some value -> schedule (ExpressionNode(value, depth + 1)) (expressionSpan value)
                    | None -> ()
                | FlowExpression.MatchOption(scrutinee, someCase, noneCase, _) ->
                    schedule (ExpressionNode(scrutinee, depth + 1)) (expressionSpan scrutinee)
                    scheduleStatements (depth + 1) someCase.Statements
                    scheduleStatements (depth + 1) noneCase.Statements
                | FlowExpression.MatchResult(scrutinee, okCase, errorCase, _) ->
                    schedule (ExpressionNode(scrutinee, depth + 1)) (expressionSpan scrutinee)
                    scheduleStatements (depth + 1) okCase.Statements
                    scheduleStatements (depth + 1) errorCase.Statements
                | FlowExpression.MatchEnum(scrutinee, cases, matchSpan) ->
                    if syntaxVersion <> 2 then
                        Diagnostics.raiseError "FLOW_SYNTAX_VERSION" "Enum matching requires Flow/2 syntax." owner (Some matchSpan) [ "Flow/2" ] [ $"Flow/{syntaxVersion}" ]
                    if List.isEmpty cases then
                        Diagnostics.raiseError "FLOW_MATCH_CASE_MISSING" "An enum match must contain every declared case exactly once." owner (Some matchSpan) [ "one or more enum cases" ] []
                    let names = HashSet<string>(System.StringComparer.Ordinal)
                    for caseValue in cases do
                        if not (isIdentifierName caseValue.Name) then
                            Diagnostics.raiseError "FLOW_MATCH_CASE_NAME_INVALID" "Enum match labels must be identifiers." owner (Some caseValue.NameSpan) [ "case-name" ] [ caseValue.Name ]
                        elif caseValue.Name = "some" || caseValue.Name = "none" || caseValue.Name = "ok" || caseValue.Name = "error" then
                            Diagnostics.raiseError "FLOW_ENUM_RESERVED_CASE" "Enum match labels cannot use the reserved Option and Result case names." owner (Some caseValue.NameSpan) [] [ caseValue.Name ]
                        elif not (names.Add caseValue.Name) then
                            Diagnostics.raiseError "FLOW_MATCH_CASE_DUPLICATE" $"Enum match contains case '{caseValue.Name}' more than once." owner (Some caseValue.NameSpan) [] [ caseValue.Name ]
                    schedule (ExpressionNode(scrutinee, depth + 1)) (expressionSpan scrutinee)
                    for caseValue in cases do scheduleStatements (depth + 1) caseValue.Statements

    let validateExpressionNestingWithVersion syntaxVersion (roots: FlowExpression list) =
        validateStructure syntaxVersion None None roots Seq.empty []

    let validateExpressionNesting (roots: FlowExpression list) =
        validateExpressionNestingWithVersion 1 roots

    let validateWordNesting (definition: FlowWordDefinition) =
        validateSyntaxVersion definition.SyntaxVersion (Some definition.Name) (Some definition.Span)
        if definition.SyntaxVersion = 1 && not definition.EffectsDeclared then
            Diagnostics.raiseError "FLOW_EFFECTS_REQUIRED" "Flow/1 word definitions require an explicit effects declaration." (Some definition.Name) (Some definition.Span) [ "declared effects" ] []
        if definition.SyntaxVersion = 2 && not definition.EffectsDeclared && not (Set.isEmpty definition.Effects) then
            Diagnostics.raiseError "FLOW_EFFECTS_UNDECLARED" "Flow/2 cannot omit effects while carrying a nonempty declared effect set." (Some definition.Name) (Some definition.Span) [ "declared effects or an empty effect set" ] (Set.toList definition.Effects)
        if List.isEmpty definition.Outputs then
            Diagnostics.raiseError "FLOW_OUTPUT_VECTOR_EMPTY" "A Flow word must declare at least one output." (Some definition.Name) (Some definition.Span) [ "one or more output types" ] []
        let typeRoots =
            seq {
                for parameter in definition.Parameters do
                    yield parameter.Type, parameter.Span
                for output in definition.Outputs do
                    yield output, definition.Span
            }
        validateStructure definition.SyntaxVersion (Some definition.Name) (Some definition.Span) [] typeRoots definition.Body

    let validateTestNesting (definition: FlowTestDefinition) =
        validateSyntaxVersion definition.SyntaxVersion (Some definition.Word) (Some definition.Span)
        validateCaseHeader "test" definition.Word definition.CaseName definition.HeaderSpan
        match definition.EffectAssertion with
        | Some assertion when definition.SyntaxVersion <> 2 ->
            Diagnostics.raiseError "FLOW_EFFECT_ASSERTION_VERSION" "Provider effect-count assertions require Flow/2 test syntax." (Some definition.Word) (Some assertion.Span) [ "Flow/2" ] [ $"Flow/{definition.SyntaxVersion}" ]
        | Some assertion ->
            for KeyValue(name, count) in assertion.Counts do
                if not (EffectCountAssertion.observableEffects.Contains name) then
                    Diagnostics.raiseError "FLOW_EFFECT_ASSERTION_UNSUPPORTED" $"Effect-count assertions cannot observe provider category '{name}'." (Some definition.Word) (Some assertion.Span)
                        (EffectCountAssertion.observableEffects |> Set.toList) [ name ]
                if count < 0 || count > EffectCountAssertion.maximumCount then
                    Diagnostics.raiseError "FLOW_EFFECT_ASSERTION_COUNT_INVALID" $"Effect count for '{name}' must be between 0 and {EffectCountAssertion.maximumCount}." (Some definition.Word) (Some assertion.Span)
                        [ $"0..{EffectCountAssertion.maximumCount}" ] [ string count ]
        | None -> ()
        let expressionRoots =
            match definition.Expected with
            | FlowTestExpectation.Expression expression -> [ expression ]
            | FlowTestExpectation.Literal _ | FlowTestExpectation.RuntimeError _ -> []
        match definition.Expected with
        | FlowTestExpectation.RuntimeError(code, codeSpan) when not (TestExpectation.isValidRuntimeErrorCode code) ->
            Diagnostics.raiseError "FLOW_INVALID_EXPECTED_ERROR_CODE" "Runtime-error expectations use a stable uppercase diagnostic code." (Some definition.Word) (Some codeSpan) [ "[A-Z][A-Z0-9_]*" ] [ code ]
        | FlowTestExpectation.RuntimeError _ | FlowTestExpectation.Expression _ when List.isEmpty definition.Body ->
            Diagnostics.raiseError "FLOW_EXPECTATION_BODY_EMPTY" "Runtime-error and value-expression tests require a nonempty actual body." (Some definition.Word) (Some definition.Span) [ "nonempty test body" ] []
        | _ -> ()
        validateStructure definition.SyntaxVersion (Some definition.Word) (Some definition.Span) expressionRoots Seq.empty definition.Body

    let validateExampleNesting (definition: FlowExampleDefinition) =
        validateSyntaxVersion definition.SyntaxVersion (Some definition.Word) (Some definition.Span)
        validateCaseHeader "example" definition.Word definition.CaseName definition.HeaderSpan
        if List.isEmpty definition.Body then
            Diagnostics.raiseError "FLOW_EXPECTATION_BODY_EMPTY" "A Flow example requires a nonempty actual body." (Some definition.Word) (Some definition.Span) [ "nonempty example body" ] []
        validateStructure definition.SyntaxVersion (Some definition.Word) (Some definition.Span) [] Seq.empty definition.Body

/// Deterministic rendering for inspection and tests. Durable source storage is
/// intentionally not wired to this frontend in the current phase.
module FlowSource =
    open System.Text.Json

    let private renderLiteral literal =
        match literal with
        | LInt _ | LFloat _ | LBool _ | LUnit -> Types.formatValue (Types.literalValue literal)
        | LString value -> JsonSerializer.Serialize(value)

    let private indent depth = String.replicate (depth * 4) " "

    let private renderQualifiedName (name: string) = name.Replace(".", "::")

    let private requireSupportedVersion syntaxVersion owner span =
        if syntaxVersion <> 1 && syntaxVersion <> 2 then
            Diagnostics.raiseError "FLOW_VERSION_UNSUPPORTED" "Only Flow syntax versions 1 and 2 are supported by this renderer." owner span [ "1"; "2" ] [ string syntaxVersion ]

    let private requireFlow2 syntaxVersion owner span feature =
        if syntaxVersion <> 2 then
            Diagnostics.raiseError "FLOW_SYNTAX_VERSION" $"{feature} requires Flow/2 syntax." owner span [ "Flow/2" ] [ $"Flow/{syntaxVersion}" ]

    let rec private renderExpressionAt syntaxVersion depth expression =
        let prefix = indent depth
        match expression with
        | FlowExpression.Literal(value, _) -> prefix + renderLiteral value
        | FlowExpression.Local(name, _) -> prefix + name
        | FlowExpression.Call(name, arguments, _) ->
            let shownName = renderQualifiedName name
            prefix + shownName + "(" + (arguments |> List.map (renderArgument syntaxVersion) |> String.concat ", ") + ")"
        | FlowExpression.RootCall(target, arguments, _) ->
            prefix + "::" + target.Name + "(" + (arguments |> List.map (renderArgument syntaxVersion) |> String.concat ", ") + ")"
        | FlowExpression.DotCall(receiver, stage, arguments, _) ->
            prefix + renderPostfixReceiver syntaxVersion receiver + "." + stage + "(" + (arguments |> List.map (renderArgument syntaxVersion) |> String.concat ", ") + ")"
        | FlowExpression.Property(receiver, field, span) ->
            requireFlow2 syntaxVersion None (Some span) "Record property access"
            prefix + renderPostfixReceiver syntaxVersion receiver + "." + field
        | FlowExpression.Equality(left, right, span) ->
            requireFlow2 syntaxVersion None (Some span) "Equality operator"
            prefix + renderEqualityOperand syntaxVersion left + " == " + renderEqualityOperand syntaxVersion right
        | FlowExpression.Container(kind, typeArguments, payload, _) ->
            prefix + renderContainer syntaxVersion kind typeArguments payload
        | FlowExpression.If(condition, thenBranch, elseBranch, _) ->
            let lines = ResizeArray<string>()
            lines.Add(prefix + "if " + renderInlineExpression syntaxVersion condition + " {")
            renderStatements syntaxVersion (depth + 1) thenBranch |> List.iter lines.Add
            lines.Add(prefix + "} else {")
            renderStatements syntaxVersion (depth + 1) elseBranch |> List.iter lines.Add
            lines.Add(prefix + "}")
            String.concat "\n" lines
        | FlowExpression.MatchOption(scrutinee, someCase, noneCase, _) ->
            renderMatchBlock syntaxVersion depth scrutinee
                (renderMatchCase syntaxVersion (depth + 1) ("some " + someCase.Name) someCase.Statements
                 @ renderMatchCase syntaxVersion (depth + 1) "none" noneCase.Statements)
        | FlowExpression.MatchResult(scrutinee, okCase, errorCase, _) ->
            renderMatchBlock syntaxVersion depth scrutinee
                (renderMatchCase syntaxVersion (depth + 1) ("ok " + okCase.Name) okCase.Statements
                 @ renderMatchCase syntaxVersion (depth + 1) ("error " + errorCase.Name) errorCase.Statements)
        | FlowExpression.MatchEnum(scrutinee, cases, span) ->
            requireFlow2 syntaxVersion None (Some span) "Enum matching"
            renderMatchBlock syntaxVersion depth scrutinee
                (cases |> List.collect (fun caseValue -> renderMatchCase syntaxVersion (depth + 1) caseValue.Name caseValue.Statements))
    and private renderInlineExpression syntaxVersion expression =
        match expression with
        | FlowExpression.Literal(value, _) -> renderLiteral value
        | FlowExpression.Local(name, _) -> name
        | FlowExpression.Container(kind, typeArguments, payload, _) -> renderContainer syntaxVersion kind typeArguments payload
        | FlowExpression.Call(name, arguments, _) ->
            let shownName = renderQualifiedName name
            shownName + "(" + (arguments |> List.map (renderArgument syntaxVersion) |> String.concat ", ") + ")"
        | FlowExpression.RootCall(target, arguments, _) ->
            "::" + target.Name + "(" + (arguments |> List.map (renderArgument syntaxVersion) |> String.concat ", ") + ")"
        | FlowExpression.DotCall(receiver, stage, arguments, _) ->
            renderPostfixReceiver syntaxVersion receiver + "." + stage + "(" + (arguments |> List.map (renderArgument syntaxVersion) |> String.concat ", ") + ")"
        | FlowExpression.Property(receiver, field, span) ->
            requireFlow2 syntaxVersion None (Some span) "Record property access"
            renderPostfixReceiver syntaxVersion receiver + "." + field
        | FlowExpression.Equality(left, right, span) ->
            requireFlow2 syntaxVersion None (Some span) "Equality operator"
            renderEqualityOperand syntaxVersion left + " == " + renderEqualityOperand syntaxVersion right
        | FlowExpression.If _ | FlowExpression.MatchOption _ | FlowExpression.MatchResult _ | FlowExpression.MatchEnum _ -> renderExpressionAt syntaxVersion 0 expression
    and private renderEqualityOperand syntaxVersion expression =
        match expression with
        | FlowExpression.Equality _ -> "(" + renderInlineExpression syntaxVersion expression + ")"
        | _ -> renderInlineExpression syntaxVersion expression
    and private renderPostfixReceiver syntaxVersion expression =
        if syntaxVersion = 2 then
            match expression with
            | FlowExpression.Equality _
            | FlowExpression.If _
            | FlowExpression.MatchOption _
            | FlowExpression.MatchResult _
            | FlowExpression.MatchEnum _ -> "(" + renderInlineExpression syntaxVersion expression + ")"
            | _ -> renderInlineExpression syntaxVersion expression
        else renderInlineExpression syntaxVersion expression
    and private renderArgument syntaxVersion = function
        | FlowArgument.Positional expression -> renderInlineExpression syntaxVersion expression
        | FlowArgument.Named(name, expression, _) -> name + " = " + renderInlineExpression syntaxVersion expression
        | FlowArgument.WordReference reference ->
            let name = renderQualifiedName reference.Name
            match reference.Qualification with
            | FlowWordReferenceQualification.ExplicitShort -> "word " + name
            | FlowWordReferenceQualification.NamespaceQualified -> name
            | FlowWordReferenceQualification.AbsoluteRoot -> "::" + name
    and private renderStatements syntaxVersion depth statements =
        statements
        |> List.mapi (fun index statement ->
            let suffix = if index < statements.Length - 1 then ";" else ""
            match statement with
            | FlowStatement.Let(name, value, _) -> indent depth + "let " + name + " = " + renderInlineExpression syntaxVersion value + suffix
            | FlowStatement.LetMany(bindings, value, _) ->
                let names = bindings |> List.map fst |> String.concat ", "
                indent depth + "let (" + names + ") = " + renderInlineExpression syntaxVersion value + suffix
            | FlowStatement.Evaluate value -> renderExpressionAt syntaxVersion depth value + suffix
            | FlowStatement.Return(values, _) ->
                indent depth + "return (" + (values |> List.map (renderInlineExpression syntaxVersion) |> String.concat ", ") + ")" + suffix)

    and private renderContainer syntaxVersion kind typeArguments payload =
        let name =
            match kind with
            | FlowContainerConstructor.ListEmpty -> "list::empty"
            | FlowContainerConstructor.ListSingleton -> "list::singleton"
            | FlowContainerConstructor.OptionNone -> "option::none"
            | FlowContainerConstructor.OptionSome -> "option::some"
            | FlowContainerConstructor.ResultOk -> "result::ok"
            | FlowContainerConstructor.ResultError -> "result::error"
        let genericArguments = typeArguments |> List.map (fun argument -> Types.format argument.Type) |> String.concat ", "
        let argument = payload |> Option.map (renderInlineExpression syntaxVersion) |> Option.defaultValue ""
        name + "<" + genericArguments + ">(" + argument + ")"

    and private renderMatchBlock syntaxVersion depth scrutinee lines =
        let prefix = indent depth
        prefix + "match " + renderInlineExpression syntaxVersion scrutinee + " {\n" + String.concat "\n" lines + "\n" + prefix + "}"

    and private renderMatchCase syntaxVersion depth header statements =
        [ indent depth + header + " => {" ]
        @ renderStatements syntaxVersion (depth + 1) statements
        @ [ indent depth + "}" ]

    let renderExpression expression =
        FlowStructure.validateExpressionNesting [ expression ]
        requireSupportedVersion 1 None None
        renderExpressionAt 1 0 expression

    let renderExpressionWithVersion syntaxVersion expression =
        FlowStructure.validateExpressionNestingWithVersion syntaxVersion [ expression ]
        requireSupportedVersion syntaxVersion None None
        renderExpressionAt syntaxVersion 0 expression

    let renderWord (definition: FlowWordDefinition) =
        FlowStructure.validateWordNesting definition
        requireSupportedVersion definition.SyntaxVersion (Some definition.Name) (Some definition.Span)
        let parameters =
            definition.Parameters
            |> List.map (fun parameter -> parameter.Name + ": " + Types.format parameter.Type)
            |> String.concat ", "
        let effects = if Set.isEmpty definition.Effects then "none" else definition.Effects |> Set.toList |> String.concat ", "
        if definition.SyntaxVersion = 2 && not definition.EffectsDeclared && not (Set.isEmpty definition.Effects) then
            Diagnostics.raiseError "FLOW_EFFECTS_UNDECLARED" "Flow/2 cannot omit effects while carrying a nonempty declared effect set." (Some definition.Name) (Some definition.Span) [ "declared effects or an empty effect set" ] (Set.toList definition.Effects)
        let lines = ResizeArray<string>()
        let outputs =
            match definition.Outputs with
            | [ output ] -> Types.format output
            | values -> "(" + (values |> List.map Types.format |> String.concat ", ") + ")"
        let keyword = if definition.SyntaxVersion = 1 then "word" else "fn"
        lines.Add($"{keyword} {definition.Name}({parameters}) -> {outputs} {{")
        let metadata = ResizeArray<string>()
        if definition.SyntaxVersion = 1 || definition.EffectsDeclared then metadata.Add("    effects " + effects)
        if not (System.String.IsNullOrEmpty definition.Documentation) then metadata.Add("    doc " + JsonSerializer.Serialize(definition.Documentation))
        metadata |> Seq.iter lines.Add
        if definition.SyntaxVersion = 2 && metadata.Count > 0 then lines.Add("")
        renderStatements definition.SyntaxVersion 1 definition.Body |> List.iter lines.Add
        lines.Add("}")
        String.concat "\n" lines

    let private renderExpectation syntaxVersion = function
        | FlowTestExpectation.Literal(literal, _) -> renderLiteral literal
        | FlowTestExpectation.RuntimeError(code, _) -> "error " + code
        | FlowTestExpectation.Expression expression -> "value " + renderInlineExpression syntaxVersion expression

    let renderTest (definition: FlowTestDefinition) =
        FlowStructure.validateTestNesting definition
        requireSupportedVersion definition.SyntaxVersion (Some definition.Word) (Some definition.Span)
        match definition.Expected with
        | FlowTestExpectation.RuntimeError(code, codeSpan) when not (TestExpectation.isValidRuntimeErrorCode code) ->
            Diagnostics.raiseError "FLOW_INVALID_EXPECTED_ERROR_CODE" "Runtime-error expectations use a stable uppercase diagnostic code." (Some definition.Word) (Some codeSpan) [ "[A-Z][A-Z0-9_]*" ] [ code ]
        | _ -> ()
        let lines = ResizeArray<string>()
        lines.Add($"test {definition.Word}/{definition.CaseName} {{")
        renderStatements definition.SyntaxVersion 1 definition.Body |> List.iter lines.Add
        let suffix =
            definition.EffectAssertion
            |> Option.map (fun assertion ->
                let counts =
                    assertion.Counts
                    |> Map.toList
                    |> List.map (fun (name, count) -> $"{name}: {count};")
                    |> String.concat " "
                " effects {" + (if counts = "" then "" else " " + counts + " ") + "}")
            |> Option.defaultValue ""
        lines.Add("    => " + renderExpectation definition.SyntaxVersion definition.Expected + suffix)
        lines.Add("}")
        String.concat "\n" lines

    let renderExample (definition: FlowExampleDefinition) =
        FlowStructure.validateExampleNesting definition
        requireSupportedVersion definition.SyntaxVersion (Some definition.Word) (Some definition.Span)
        let lines = ResizeArray<string>()
        lines.Add($"example {definition.Word}/{definition.CaseName} {{")
        renderStatements definition.SyntaxVersion 1 definition.Body |> List.iter lines.Add
        lines.Add("    => " + renderLiteral definition.Expected)
        lines.Add("}")
        String.concat "\n" lines

    let private reservedTypeNames =
        set [ "Int"; "Float"; "Bool"; "String"; "Unit"; "List"; "Option"; "Result"; "a"; "b"; "c" ]

    let private validTypeName (name: string) =
        not (System.String.IsNullOrWhiteSpace name)
        && System.Char.IsLetter name[0]
        && (name |> Seq.forall System.Char.IsLetterOrDigit)
        && not (reservedTypeNames.Contains name)

    let private validFieldName (name: string) =
        let reservedWordNames =
            set [ "if"; "else"; "end"; "let"; "true"; "false"; "unit"
                  "match-option"; "match-result"; "some"; "none"; "ok"; "error"
                  "list.empty"; "list.singleton"; "option.none"; "option.some"; "result.ok"; "result.error"
                  "list.map"; "list.filter"; "list.each"; "list.fold" ]
        not (System.String.IsNullOrWhiteSpace name)
        && System.Char.IsLetter name[0]
        && (name |> Seq.forall (fun value -> System.Char.IsLetterOrDigit value || value = '.' || value = '-' || value = '_' || value = '?' || value = '!'))
        && not (name.Contains('.') || reservedWordNames.Contains name)

    let private validateTypeShape name span requireClosed typeValue =
        let pending = System.Collections.Generic.Stack<LangType * int>()
        pending.Push(typeValue, 1)
        while pending.Count > 0 do
            let current, depth = pending.Pop()
            if depth > FlowStructure.maxExpressionDepth then
                Diagnostics.raiseError "FLOW_NESTING_LIMIT" $"Flow syntax exceeds the nesting limit of {FlowStructure.maxExpressionDepth}." (Some name) (Some span) [] []
            match current with
            | TList item | TOption item -> pending.Push(item, depth + 1)
            | TResult(okType, errorType) -> pending.Push(okType, depth + 1); pending.Push(errorType, depth + 1)
            | TVar variable when requireClosed ->
                Diagnostics.raiseError "FLOW_TYPE_OPEN" "Flow project declarations require closed types; free type variables are not supported." (Some name) (Some span) [] [ variable ]
            | TVar _ -> ()
            | TInt | TFloat | TBool | TString | TUnit | TNamed _ -> ()

    let private validateClosedType name span typeValue =
        validateTypeShape name span true typeValue

    let renderRecord (definition: RecordDefinition) =
        if not (validTypeName definition.Name) then
            Diagnostics.raiseError "FLOW_TYPE_NAME_INVALID" "Record names must be identifiers and cannot shadow built-in types or reserved type variables." (Some definition.Name) (Some definition.Span) [ "non-reserved type identifier" ] [ definition.Name ]
        if List.isEmpty definition.Fields then
            Diagnostics.raiseError "FLOW_RECORD_EMPTY" "A Flow record must declare at least one field." (Some definition.Name) (Some definition.Span) [ "one or more fields" ] []
        let seen = System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal)
        for field in definition.Fields do
            if not (validFieldName field.Name) then
                Diagnostics.raiseError "FLOW_RECORD_FIELD_NAME_INVALID" "Record fields must use non-reserved identifier names." (Some definition.Name) (Some definition.Span) [ "field identifier" ] [ field.Name ]
            if not (seen.Add field.Name) then
                Diagnostics.raiseError "FLOW_RECORD_DUPLICATE_FIELD" $"Record field '{field.Name}' is repeated." (Some definition.Name) (Some definition.Span) [] [ field.Name ]
            validateClosedType definition.Name definition.Span field.Type
        let fields =
            definition.Fields
            |> List.map (fun field -> "    field " + field.Name + ": " + Types.format field.Type + ";")
        String.concat "\n" ([ "record " + definition.Name + " {" ] @ fields @ [ "}" ])

    let renderScalar (definition: ScalarTypeDefinition) =
        if not (validTypeName definition.Name) then
            Diagnostics.raiseError "FLOW_TYPE_NAME_INVALID" "Scalar type names must be identifiers and cannot shadow built-in types or reserved type variables." (Some definition.Name) (Some definition.Span) [ "non-reserved type identifier" ] [ definition.Name ]
        // Scalar diagnostics format unsupported host-built types below. Bound
        // their nesting before calling the recursive formatter, just as the
        // parser does for source-authored type expressions.
        validateTypeShape definition.Name definition.Span false definition.BaseType
        match definition.BaseType with
        | TInt | TFloat | TString -> ()
        | other ->
            Diagnostics.raiseError "FLOW_SCALAR_BASE_UNSUPPORTED" "Nominal scalar wrappers currently require an Int, Float, or String base type." (Some definition.Name) (Some definition.Span) [ "Int"; "Float"; "String" ] [ Types.format other ]
        let validatorLine =
            match definition.Validator with
            | None -> []
            | Some name ->
                let segments =
                    if System.String.IsNullOrEmpty name then [||]
                    else name.Split('.', System.StringSplitOptions.None)
                let validSegment (segment: string) =
                    not (System.String.IsNullOrWhiteSpace segment)
                    && (System.Char.IsLetter segment[0] || segment[0] = '_')
                    && (segment |> Seq.skip 1 |> Seq.forall (fun value -> System.Char.IsLetterOrDigit value || value = '_' || value = '-' || value = '?' || value = '!'))
                if segments.Length = 0 || (segments |> Array.exists (validSegment >> not)) then
                    Diagnostics.raiseError "FLOW_SCALAR_VALIDATOR_REFERENCE" "A scalar validator must name one exact dictionary word using a root or namespace-qualified reference." (Some definition.Name) (Some definition.Span) [ "::word"; "namespace::word" ] [ name ]
                let qualified =
                    if segments.Length = 1 then "::" + segments[0]
                    else String.concat "::" segments
                [ "    validate " + qualified + ";" ]
        String.concat "\n" ([ "type " + definition.Name + " : " + Types.format definition.BaseType + " {" ] @ validatorLine @ [ "}" ])

    let renderEnum (definition: EnumDefinition) =
        if not (validTypeName definition.Name) then
            Diagnostics.raiseError "FLOW_TYPE_NAME_INVALID" "Enum names must be identifiers and cannot shadow built-in types or reserved type variables." (Some definition.Name) (Some definition.Span) [ "non-reserved type identifier" ] [ definition.Name ]
        if List.isEmpty definition.Cases then
            Diagnostics.raiseError "FLOW_ENUM_EMPTY" "A Flow enum must declare at least one case." (Some definition.Name) (Some definition.Span) [ "one or more cases" ] []
        let seen = System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal)
        for caseName in definition.Cases do
            if not (System.String.IsNullOrEmpty caseName)
               && (System.Char.IsLetter caseName[0] || caseName[0] = '_')
               && (caseName |> Seq.skip 1 |> Seq.forall (fun value -> System.Char.IsLetterOrDigit value || value = '_' || value = '-' || value = '?' || value = '!')) then
                if caseName = "some" || caseName = "none" || caseName = "ok" || caseName = "error" then
                    Diagnostics.raiseError "FLOW_ENUM_RESERVED_CASE" "Enum case names cannot use the reserved Option and Result labels." (Some definition.Name) (Some definition.Span) [] [ caseName ]
                elif not (seen.Add caseName) then
                    Diagnostics.raiseError "FLOW_ENUM_DUPLICATE_CASE" $"Enum case '{caseName}' is repeated." (Some definition.Name) (Some definition.Span) [] [ caseName ]
            else
                Diagnostics.raiseError "FLOW_ENUM_CASE_NAME_INVALID" "Enum cases must use non-reserved identifiers." (Some definition.Name) (Some definition.Span) [ "case-name" ] [ caseName ]
        String.concat "\n" ([ "enum " + definition.Name + " {" ] @ (definition.Cases |> List.map (fun caseName -> "    case " + caseName + ";")) @ [ "}" ])

    let renderDocument (document: FlowProjectDocument) =
        requireSupportedVersion document.SyntaxVersion None None
        if not document.Enums.IsEmpty then requireFlow2 document.SyntaxVersion None None "Enum declarations"
        for definition in document.Words do
            if definition.SyntaxVersion <> document.SyntaxVersion then
                Diagnostics.raiseError "FLOW_VERSION_MISMATCH" "A Flow project document and its word declarations must use the same syntax version." (Some definition.Name) (Some definition.Span)
                    [ string document.SyntaxVersion ] [ string definition.SyntaxVersion ]
        for definition in document.Tests do
            if definition.SyntaxVersion <> document.SyntaxVersion then
                Diagnostics.raiseError "FLOW_VERSION_MISMATCH" "A Flow project document and its test declarations must use the same syntax version." (Some definition.Word) (Some definition.Span)
                    [ string document.SyntaxVersion ] [ string definition.SyntaxVersion ]
        for definition in document.Examples do
            if definition.SyntaxVersion <> document.SyntaxVersion then
                Diagnostics.raiseError "FLOW_VERSION_MISMATCH" "A Flow project document and its example declarations must use the same syntax version." (Some definition.Word) (Some definition.Span)
                    [ string document.SyntaxVersion ] [ string definition.SyntaxVersion ]
        let typeNames =
            (document.Records |> List.map (fun definition -> definition.Name))
            @ (document.Scalars |> List.map (fun definition -> definition.Name))
            @ (document.Enums |> List.map (fun definition -> definition.Name))
        let duplicateType = typeNames |> List.groupBy id |> List.tryFind (fun (_, values) -> values.Length > 1)
        match duplicateType with
        | Some(name, _) -> Diagnostics.raiseError "FLOW_PROJECT_DUPLICATE_TYPE" $"Type name '{name}' is declared more than once in the Flow project." (Some name) None [] [ name ]
        | None -> ()
        [ yield! document.Records |> List.map renderRecord
          yield! document.Scalars |> List.map renderScalar
          yield! document.Enums |> List.map renderEnum
          yield! document.Words |> List.map renderWord
          yield! document.Tests |> List.map renderTest
          yield! document.Examples |> List.map renderExample ]
        |> String.concat "\n\n"
