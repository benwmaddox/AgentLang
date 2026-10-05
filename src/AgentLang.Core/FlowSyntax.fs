namespace AgentLang

/// Versioned source syntax for the explicit data-flow frontend. These nodes
/// are authoring metadata only; executable expressions lower to the existing
/// Expr and verified semantic IR.
[<RequireQualifiedAccess>]
type FlowExpression =
    | Literal of Literal * SourceSpan
    | Local of string * SourceSpan
    | Call of string * FlowArgument list * SourceSpan
    | DotCall of FlowExpression * string * FlowArgument list * SourceSpan
    | If of FlowExpression * FlowStatement list * FlowStatement list * SourceSpan

and [<RequireQualifiedAccess>] FlowArgument =
    | Positional of FlowExpression
    | Named of string * FlowExpression * SourceSpan

and [<RequireQualifiedAccess>] FlowStatement =
    | Let of string * FlowExpression * SourceSpan
    | Evaluate of FlowExpression

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
        | FlowExpression.If(condition, thenBranch, elseBranch, _) ->
            let lines = ResizeArray<string>()
            lines.Add(prefix + "if " + renderInlineExpression condition + " {")
            renderStatements (depth + 1) thenBranch |> List.iter lines.Add
            lines.Add(prefix + "} else {")
            renderStatements (depth + 1) elseBranch |> List.iter lines.Add
            lines.Add(prefix + "}")
            String.concat "\n" lines
    and private renderInlineExpression expression =
        match expression with
        | FlowExpression.Literal(value, _) -> renderLiteral value
        | FlowExpression.Local(name, _) -> name
        | FlowExpression.Call(name, arguments, _) ->
            let shownName = renderQualifiedName name
            shownName + "(" + (arguments |> List.map renderArgument |> String.concat ", ") + ")"
        | FlowExpression.DotCall(receiver, stage, arguments, _) ->
            renderInlineExpression receiver + "." + stage + "(" + (arguments |> List.map renderArgument |> String.concat ", ") + ")"
        | FlowExpression.If _ -> renderExpressionAt 0 expression
    and private renderArgument = function
        | FlowArgument.Positional expression -> renderInlineExpression expression
        | FlowArgument.Named(name, expression, _) -> name + " = " + renderInlineExpression expression
    and private renderStatements depth statements =
        statements
        |> List.mapi (fun index statement ->
            let suffix = if index < statements.Length - 1 then ";" else ""
            match statement with
            | FlowStatement.Let(name, value, _) -> indent depth + "let " + name + " = " + renderInlineExpression value + suffix
            | FlowStatement.Evaluate value -> renderExpressionAt depth value + suffix)

    let renderExpression expression = renderExpressionAt 0 expression

    let renderWord (definition: FlowWordDefinition) =
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
