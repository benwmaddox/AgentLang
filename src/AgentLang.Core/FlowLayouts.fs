namespace AgentLang

/// Layouts (report 309): whether a parsed document has an html template with a
/// `{...}` slot, so the built-in Layout is added to it.
module FlowLayouts =
    let rec private expressionHasSlot expression =
        let statements = List.exists statementHasSlot
        let arguments (items: FlowArgument list) =
            items |> List.exists (function
                | FlowArgument.Positional value | FlowArgument.Named(_, value, _) -> expressionHasSlot value
                | FlowArgument.WordReference _ -> false)
        match expression with
        | FlowExpression.Call(name, _, _) when name = LayoutBuiltIn.slotName -> true
        | FlowExpression.Call(_, args, _) | FlowExpression.RootCall(_, args, _) -> arguments args
        | FlowExpression.DotCall(receiver, _, args, _) -> expressionHasSlot receiver || arguments args
        | FlowExpression.Property(receiver, _, _) -> expressionHasSlot receiver
        | FlowExpression.Equality(left, right, _) | FlowExpression.Binary(_, left, right, _) -> expressionHasSlot left || expressionHasSlot right
        | FlowExpression.If(condition, thenBranch, elseBranch, _) -> expressionHasSlot condition || statements thenBranch || statements elseBranch
        | FlowExpression.Compact(body, _) -> statements body
        | FlowExpression.ListLiteral(items, _) -> List.exists expressionHasSlot items
        | FlowExpression.Container(_, _, payload, _) -> payload |> Option.exists expressionHasSlot
        | FlowExpression.MatchOption(scrutinee, some, none, _) -> expressionHasSlot scrutinee || statements some.Statements || statements none.Statements
        | FlowExpression.MatchResult(scrutinee, ok, error, _) -> expressionHasSlot scrutinee || statements ok.Statements || statements error.Statements
        | FlowExpression.MatchEnum(scrutinee, cases, _) -> expressionHasSlot scrutinee || cases |> List.exists (fun case -> statements case.Statements)
        | FlowExpression.Literal _ | FlowExpression.Local _ -> false

    and private statementHasSlot statement =
        match statement with
        | FlowStatement.Let(_, value, _) | FlowStatement.LetMany(_, value, _) | FlowStatement.Var(_, value, _) | FlowStatement.Assign(_, value, _) -> expressionHasSlot value
        | FlowStatement.Evaluate value -> expressionHasSlot value
        | FlowStatement.Return(values, _) -> List.exists expressionHasSlot values
        | FlowStatement.ForRange(_, first, last, body, _) -> expressionHasSlot first || expressionHasSlot last || List.exists statementHasSlot body
        | FlowStatement.ForEach(_, _, items, body, _) -> expressionHasSlot items || List.exists statementHasSlot body
        | FlowStatement.Break _ | FlowStatement.Continue _ -> false

    let usesSlot (document: FlowProjectDocument) =
        let body = List.exists statementHasSlot
        (document.Words |> List.exists (fun word -> body word.Body))
        || (document.Tests |> List.exists (fun test -> body test.Body))
        || (document.TestFiles |> List.exists (fun scope ->
                (scope.Overrides |> List.exists (fun replacement -> body replacement.Definition.Body))
                || (scope.Tests |> List.exists (fun test -> body test.Body))))
