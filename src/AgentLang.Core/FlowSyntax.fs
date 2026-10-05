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

type FlowWordReference =
    { Name: string
      /// Short word names require the explicit `word name` callback marker.
      IsExplicitShort: bool
      Span: SourceSpan }

[<RequireQualifiedAccess>]
type FlowExpression =
    | Literal of Literal * SourceSpan
    | Local of string * SourceSpan
    | Call of string * FlowArgument list * SourceSpan
    | DotCall of FlowExpression * string * FlowArgument list * SourceSpan
    | If of FlowExpression * FlowStatement list * FlowStatement list * SourceSpan
    | Container of FlowContainerConstructor * FlowTypeArgument list * FlowExpression option * SourceSpan
    | MatchOption of FlowExpression * FlowPayloadCase * FlowCaseBlock * SourceSpan
    | MatchResult of FlowExpression * FlowPayloadCase * FlowPayloadCase * SourceSpan

and [<RequireQualifiedAccess>] FlowArgument =
    | Positional of FlowExpression
    | Named of string * FlowExpression * SourceSpan
    | WordReference of FlowWordReference

and [<RequireQualifiedAccess>] FlowStatement =
    | Let of string * FlowExpression * SourceSpan
    | Evaluate of FlowExpression

and FlowCaseBlock =
    { Statements: FlowStatement list
      Span: SourceSpan }

and FlowPayloadCase =
    { Name: string
      NameSpan: SourceSpan
      Statements: FlowStatement list
      Span: SourceSpan }

type FlowParameter =
    { Name: string
      Type: LangType
      Span: SourceSpan }

/// The first frontend slice deliberately has one output. Explicit destructuring
/// for multiple outputs is a later grammar extension.
type FlowWordDefinition =
    { Name: string
      Parameters: FlowParameter list
      Output: LangType
      Effects: Set<string>
      Documentation: string
      Body: FlowStatement list
      SourceText: string
      Span: SourceSpan
      SyntaxVersion: int }

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
        | FlowExpression.DotCall(_, _, _, span)
        | FlowExpression.If(_, _, _, span)
        | FlowExpression.Container(_, _, _, span)
        | FlowExpression.MatchOption(_, _, _, span)
        | FlowExpression.MatchResult(_, _, _, span) -> span

    let private validateStructure (expressionRoots: FlowExpression list) (typeRoots: (LangType * SourceSpan) list) (statementRoots: FlowStatement list) =
        let pending = Stack<Node>()
        let mutable scheduledNodes = 0
        let schedule node source =
            if scheduledNodes >= maxExpandedNodes then
                Diagnostics.raiseError "FLOW_STRUCTURE_LIMIT" $"Flow syntax exceeds the expanded structural-node budget of {maxExpandedNodes}." None (Some source) [] []
            scheduledNodes <- scheduledNodes + 1
            pending.Push node
        for expression in expressionRoots do schedule (ExpressionNode(expression, 1)) (expressionSpan expression)
        for typeValue, source in typeRoots do schedule (TypeNode(typeValue, 1, source)) source
        let scheduleStatement depth statement =
            let expression =
                match statement with
                | FlowStatement.Let(_, value, _) | FlowStatement.Evaluate value -> value
            schedule (ExpressionNode(expression, depth)) (expressionSpan expression)
        for statement in statementRoots do scheduleStatement 1 statement
        let scheduleArguments depth arguments =
            for argument in arguments do
                match argument with
                | FlowArgument.Positional expression -> schedule (ExpressionNode(expression, depth)) (expressionSpan expression)
                | FlowArgument.Named(_, expression, _) -> schedule (ExpressionNode(expression, depth)) (expressionSpan expression)
                | FlowArgument.WordReference _ -> ()
        let scheduleStatements depth statements =
            for statement in statements do scheduleStatement depth statement
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
                | FlowExpression.DotCall(receiver, _, arguments, _) ->
                    schedule (ExpressionNode(receiver, depth + 1)) (expressionSpan receiver)
                    scheduleArguments (depth + 1) arguments
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

    let validateExpressionNesting (roots: FlowExpression list) =
        validateStructure roots [] []

    let validateWordNesting (definition: FlowWordDefinition) =
        let typeRoots =
            (definition.Parameters |> List.map (fun parameter -> parameter.Type, parameter.Span))
            @ [ definition.Output, definition.Span ]
        validateStructure [] typeRoots definition.Body

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

    let rec private renderExpressionAt depth expression =
        let prefix = indent depth
        match expression with
        | FlowExpression.Literal(value, _) -> prefix + renderLiteral value
        | FlowExpression.Local(name, _) -> prefix + name
        | FlowExpression.Call(name, arguments, _) ->
            let shownName = renderQualifiedName name
            prefix + shownName + "(" + (arguments |> List.map renderArgument |> String.concat ", ") + ")"
        | FlowExpression.DotCall(receiver, stage, arguments, _) ->
            renderExpressionAt depth receiver + "." + stage + "(" + (arguments |> List.map renderArgument |> String.concat ", ") + ")"
        | FlowExpression.Container(kind, typeArguments, payload, _) ->
            prefix + renderContainer kind typeArguments payload
        | FlowExpression.If(condition, thenBranch, elseBranch, _) ->
            let lines = ResizeArray<string>()
            lines.Add(prefix + "if " + renderInlineExpression condition + " {")
            renderStatements (depth + 1) thenBranch |> List.iter lines.Add
            lines.Add(prefix + "} else {")
            renderStatements (depth + 1) elseBranch |> List.iter lines.Add
            lines.Add(prefix + "}")
            String.concat "\n" lines
        | FlowExpression.MatchOption(scrutinee, someCase, noneCase, _) ->
            renderMatchBlock depth scrutinee
                (renderMatchCase (depth + 1) ("some " + someCase.Name) someCase.Statements
                 @ renderMatchCase (depth + 1) "none" noneCase.Statements)
        | FlowExpression.MatchResult(scrutinee, okCase, errorCase, _) ->
            renderMatchBlock depth scrutinee
                (renderMatchCase (depth + 1) ("ok " + okCase.Name) okCase.Statements
                 @ renderMatchCase (depth + 1) ("error " + errorCase.Name) errorCase.Statements)
    and private renderInlineExpression expression =
        match expression with
        | FlowExpression.Literal(value, _) -> renderLiteral value
        | FlowExpression.Local(name, _) -> name
        | FlowExpression.Container(kind, typeArguments, payload, _) -> renderContainer kind typeArguments payload
        | FlowExpression.Call(name, arguments, _) ->
            let shownName = renderQualifiedName name
            shownName + "(" + (arguments |> List.map renderArgument |> String.concat ", ") + ")"
        | FlowExpression.DotCall(receiver, stage, arguments, _) ->
            renderInlineExpression receiver + "." + stage + "(" + (arguments |> List.map renderArgument |> String.concat ", ") + ")"
        | FlowExpression.If _ | FlowExpression.MatchOption _ | FlowExpression.MatchResult _ -> renderExpressionAt 0 expression
    and private renderArgument = function
        | FlowArgument.Positional expression -> renderInlineExpression expression
        | FlowArgument.Named(name, expression, _) -> name + " = " + renderInlineExpression expression
        | FlowArgument.WordReference reference ->
            let name = renderQualifiedName reference.Name
            if reference.IsExplicitShort then "word " + name else name
    and private renderStatements depth statements =
        statements
        |> List.mapi (fun index statement ->
            let suffix = if index < statements.Length - 1 then ";" else ""
            match statement with
            | FlowStatement.Let(name, value, _) -> indent depth + "let " + name + " = " + renderInlineExpression value + suffix
            | FlowStatement.Evaluate value -> renderExpressionAt depth value + suffix)

    and private renderContainer kind typeArguments payload =
        let name =
            match kind with
            | FlowContainerConstructor.ListEmpty -> "list::empty"
            | FlowContainerConstructor.ListSingleton -> "list::singleton"
            | FlowContainerConstructor.OptionNone -> "option::none"
            | FlowContainerConstructor.OptionSome -> "option::some"
            | FlowContainerConstructor.ResultOk -> "result::ok"
            | FlowContainerConstructor.ResultError -> "result::error"
        let genericArguments = typeArguments |> List.map (fun argument -> Types.format argument.Type) |> String.concat ", "
        let argument = payload |> Option.map renderInlineExpression |> Option.defaultValue ""
        name + "<" + genericArguments + ">(" + argument + ")"

    and private renderMatchBlock depth scrutinee lines =
        let prefix = indent depth
        prefix + "match " + renderInlineExpression scrutinee + " {\n" + String.concat "\n" lines + "\n" + prefix + "}"

    and private renderMatchCase depth header statements =
        [ indent depth + header + " => {" ]
        @ renderStatements (depth + 1) statements
        @ [ indent depth + "}" ]

    let renderExpression expression =
        FlowStructure.validateExpressionNesting [ expression ]
        renderExpressionAt 0 expression

    let renderWord (definition: FlowWordDefinition) =
        FlowStructure.validateWordNesting definition
        let parameters =
            definition.Parameters
            |> List.map (fun parameter -> parameter.Name + ": " + Types.format parameter.Type)
            |> String.concat ", "
        let effects = if Set.isEmpty definition.Effects then "none" else definition.Effects |> Set.toList |> String.concat ", "
        let lines = ResizeArray<string>()
        lines.Add($"word {definition.Name}({parameters}) -> {Types.format definition.Output} {{")
        lines.Add("    effects " + effects)
        if not (System.String.IsNullOrEmpty definition.Documentation) then lines.Add("    doc " + JsonSerializer.Serialize(definition.Documentation))
        renderStatements 1 definition.Body |> List.iter lines.Add
        lines.Add("}")
        String.concat "\n" lines
