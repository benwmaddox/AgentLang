namespace AgentLang

/// Options for the advisory Flow local-binding lint.
type FlowLintOptions =
    { MaxInterveningStatements: int option }

/// A configuration or input-shape problem that prevents lint analysis.
type FlowLintError =
    { Code: string
      Message: string }

/// A stable, source-oriented result emitted by FlowLint.
type FlowLintWarning =
    { Code: string
      Binding: string
      DeclarationSpan: SourceSpan
      FirstUseSpan: SourceSpan option
      Gap: int option }

/// Pure, deterministic lints over the explicit Flow syntax tree.
/// This module does not rewrite source or participate in compiler gates.
[<RequireQualifiedAccess>]
module FlowLint =
    open System
    open System.Collections.Generic

    let defaultOptions: FlowLintOptions =
        { MaxInterveningStatements = Some 8 }

    let noDistanceLimit: FlowLintOptions =
        { MaxInterveningStatements = None }

    let tryCreateOptions maximumInterveningStatements =
        match maximumInterveningStatements with
        | Some maximum when maximum < 0 ->
            Error
                { Code = "FLOW_LINT_INVALID_OPTIONS"
                  Message = "The maximum number of intervening statements must be non-negative, or omitted to disable distance warnings." }
        | value -> Ok { MaxInterveningStatements = value }

    type private BindingInfo(name: string, declarationSpan: SourceSpan, declarationStatement: int) =
        member _.Name = name
        member _.DeclarationSpan = declarationSpan
        member _.DeclarationStatement = declarationStatement
        member val FirstUse: (SourceSpan * int) option = None with get, set

    type private ScopeFrame =
        { Bindings: Map<string, BindingInfo>
          ParameterNames: Set<string>
          UseStatement: int option }

    exception private NestingLimitReached

    // FlowParser already imposes a lower parser nesting bound. Keeping a
    // second bound here makes direct callers that hand-build an AST fail with
    // a diagnostic instead of recursing without limit.
    let private maximumTraversalDepth = 256

    let analyze (options: FlowLintOptions) (definition: FlowWordDefinition) : Result<FlowLintWarning list, FlowLintError> =
        match options.MaxInterveningStatements with
        | Some maximum when maximum < 0 ->
            Error
                { Code = "FLOW_LINT_INVALID_OPTIONS"
                  Message = "The maximum number of intervening statements must be non-negative, or omitted to disable distance warnings." }
        | _ ->
            try
                let bindings = ResizeArray<BindingInfo>()
                let mutable depth = 0

                let withinDepth action =
                    depth <- depth + 1
                    try
                        if depth > maximumTraversalDepth then raise NestingLimitReached
                        action ()
                    finally
                        depth <- depth - 1

                let newBinding name declarationSpan declarationStatement =
                    let binding = BindingInfo(name, declarationSpan, declarationStatement)
                    bindings.Add binding
                    binding

                let recordUse (binding: BindingInfo) sourceSpan statementIndex =
                    if binding.FirstUse.IsNone then binding.FirstUse <- Some(sourceSpan, statementIndex)

                let findBinding name (candidates: Map<string, BindingInfo>) =
                    Map.tryFind name candidates

                let rec analyzeBlock (outerFrames: ScopeFrame list) (initialBindings: (string * SourceSpan) list) (statements: FlowStatement list) =
                    withinDepth (fun () ->
                        let mutable visibleBindings = Map.empty
                        for name, sourceSpan in initialBindings do
                            let binding = newBinding name sourceSpan -1
                            visibleBindings <- Map.add name binding visibleBindings

                        for statementIndex, statement in statements |> List.indexed do
                            match statement with
                            | FlowStatement.Let(name, initializer, declarationSpan) ->
                                // Reserve diagnostic order at the declaration, but do
                                // not make the binding visible to its own initializer.
                                let binding = newBinding name declarationSpan statementIndex
                                analyzeExpression outerFrames visibleBindings statementIndex initializer
                                visibleBindings <- Map.add name binding visibleBindings
                            | FlowStatement.LetMany(pattern, initializer, _) ->
                                // Reserve all diagnostics in source order before walking
                                // the initializer, while keeping every new name hidden
                                // until the complete initializer has been visited.
                                let newBindings =
                                    pattern
                                    |> List.map (fun (name, declarationSpan) ->
                                        name, newBinding name declarationSpan statementIndex)

                                analyzeExpression outerFrames visibleBindings statementIndex initializer

                                // Destructured names become visible together after the
                                // initializer, and each binding keeps its own source span.
                                for name, binding in newBindings do
                                    visibleBindings <- Map.add name binding visibleBindings
                            | FlowStatement.Evaluate expression ->
                                analyzeExpression outerFrames visibleBindings statementIndex expression
                            | FlowStatement.Return(expressions, _) ->
                                for expression in expressions do
                                    analyzeExpression outerFrames visibleBindings statementIndex expression)

                and analyzeExpression (outerFrames: ScopeFrame list) (localBindings: Map<string, BindingInfo>) statementIndex expression =
                    withinDepth (fun () ->
                        match expression with
                        | FlowExpression.Literal _ -> ()
                        | FlowExpression.Local(name, sourceSpan) ->
                            match findBinding name localBindings with
                            | Some binding -> recordUse binding sourceSpan statementIndex
                            | None ->
                                let rec findOuter = function
                                    | [] -> ()
                                    | frame :: remaining ->
                                        match findBinding name frame.Bindings with
                                        | Some binding ->
                                            frame.UseStatement
                                            |> Option.iter (recordUse binding sourceSpan)
                                        | None when Set.contains name frame.ParameterNames -> ()
                                        | None -> findOuter remaining
                                findOuter outerFrames
                        | FlowExpression.Call(_, arguments, _) ->
                            for argument in arguments do analyzeArgument outerFrames localBindings statementIndex argument
                        | FlowExpression.RootCall(_, arguments, _) ->
                            for argument in arguments do analyzeArgument outerFrames localBindings statementIndex argument
                        | FlowExpression.DotCall(receiver, _, arguments, _) ->
                            analyzeExpression outerFrames localBindings statementIndex receiver
                            for argument in arguments do analyzeArgument outerFrames localBindings statementIndex argument
                        | FlowExpression.Property(receiver, _, _) ->
                            analyzeExpression outerFrames localBindings statementIndex receiver
                        | FlowExpression.Equality(left, right, _) ->
                            analyzeExpression outerFrames localBindings statementIndex left
                            analyzeExpression outerFrames localBindings statementIndex right
                        | FlowExpression.If(condition, thenBranch, elseBranch, _) ->
                            analyzeExpression outerFrames localBindings statementIndex condition
                            let parentFrame =
                                { Bindings = localBindings
                                  ParameterNames = Set.empty
                                  UseStatement = Some statementIndex }
                            analyzeBlock (parentFrame :: outerFrames) [] thenBranch
                            analyzeBlock (parentFrame :: outerFrames) [] elseBranch
                        | FlowExpression.Container(_, _, payload, _) ->
                            payload |> Option.iter (analyzeExpression outerFrames localBindings statementIndex)
                        | FlowExpression.MatchOption(scrutinee, someCase, noneCase, _) ->
                            analyzeExpression outerFrames localBindings statementIndex scrutinee
                            let parentFrame =
                                { Bindings = localBindings
                                  ParameterNames = Set.empty
                                  UseStatement = Some statementIndex }
                            [ someCase.Span, [ someCase.Name, someCase.NameSpan ], someCase.Statements
                              noneCase.Span, [], noneCase.Statements ]
                            |> List.sortBy (fun (sourceSpan, _, _) -> sourceSpan.File, sourceSpan.Line, sourceSpan.Column)
                            |> List.iter (fun (_, caseBindings, caseStatements) ->
                                analyzeBlock (parentFrame :: outerFrames) caseBindings caseStatements)
                        | FlowExpression.MatchResult(scrutinee, okCase, errorCase, _) ->
                            analyzeExpression outerFrames localBindings statementIndex scrutinee
                            let parentFrame =
                                { Bindings = localBindings
                                  ParameterNames = Set.empty
                                  UseStatement = Some statementIndex }
                            [ okCase.Span, [ okCase.Name, okCase.NameSpan ], okCase.Statements
                              errorCase.Span, [ errorCase.Name, errorCase.NameSpan ], errorCase.Statements ]
                            |> List.sortBy (fun (sourceSpan, _, _) -> sourceSpan.File, sourceSpan.Line, sourceSpan.Column)
                            |> List.iter (fun (_, caseBindings, caseStatements) ->
                                analyzeBlock (parentFrame :: outerFrames) caseBindings caseStatements)
                        | FlowExpression.MatchEnum(scrutinee, cases, _) ->
                            analyzeExpression outerFrames localBindings statementIndex scrutinee
                            let parentFrame =
                                { Bindings = localBindings
                                  ParameterNames = Set.empty
                                  UseStatement = Some statementIndex }
                            cases
                            |> List.sortBy (fun caseValue -> caseValue.Span.File, caseValue.Span.Line, caseValue.Span.Column)
                            |> List.iter (fun caseValue -> analyzeBlock (parentFrame :: outerFrames) [] caseValue.Statements))

                and analyzeArgument outerFrames localBindings statementIndex argument =
                    match argument with
                    | FlowArgument.Positional expression -> analyzeExpression outerFrames localBindings statementIndex expression
                    | FlowArgument.Named(_, expression, _) -> analyzeExpression outerFrames localBindings statementIndex expression
                    | FlowArgument.WordReference _ -> ()

                let parameterFrame =
                    { Bindings = Map.empty
                      ParameterNames = definition.Parameters |> List.map (fun parameter -> parameter.Name) |> Set.ofList
                      UseStatement = None }

                analyzeBlock [ parameterFrame ] [] definition.Body

                let warnings = ResizeArray<FlowLintWarning>()
                for binding in bindings do
                    match binding.FirstUse with
                    | None ->
                        warnings.Add
                            { Code = "FLOW_LOCAL_UNUSED"
                              Binding = binding.Name
                              DeclarationSpan = binding.DeclarationSpan
                              FirstUseSpan = None
                              Gap = None }
                    | Some(sourceSpan, useStatement) ->
                        let gap = max 0 (useStatement - binding.DeclarationStatement - 1)
                        match options.MaxInterveningStatements with
                        | Some maximum when gap > maximum ->
                            warnings.Add
                                { Code = "FLOW_LOCAL_FIRST_USE_TOO_DISTANT"
                                  Binding = binding.Name
                                  DeclarationSpan = binding.DeclarationSpan
                                  FirstUseSpan = Some sourceSpan
                                  Gap = Some gap }
                        | _ -> ()

                Ok(List.ofSeq warnings)
            with
            | NestingLimitReached ->
                Error
                    { Code = "FLOW_LINT_NESTING_LIMIT"
                      Message = $"Flow lint analysis is limited to {maximumTraversalDepth} nested syntax blocks or expressions." }
