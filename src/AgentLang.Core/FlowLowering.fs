namespace AgentLang

open System

/// Lowering for the first explicit Flow syntax slice. Resolution is static and
/// the output remains ordinary Expr consumed by the existing checked compiler.
module FlowLowering =
    type Context =
        { CompilerContext: Compiler.IrLoweringContext
          /// Audited parameter names for words whose names are not carried by the
          /// legacy WordDefinition. Record constructor names derive from fields.
          ParameterNames: Map<string, string list>
          /// Transient private source markers already lowered into words in this context.
          SourceOrigins: Map<SourceSpan, SourceSpan> }

    type CompiledExpression =
        { Lowered: FlowLoweredExpression
          Program: VerifiedIrProgram
          Body: VerifiedIrBody
          SiteOrigins: Map<SourceSiteId, SourceSpan> }

    type CompiledWord =
        { Lowered: FlowLoweredWord
          Context: Context
          Program: VerifiedIrProgram
          SiteOrigins: Map<SourceSiteId, SourceSpan> }

    type private Binding =
        { InternalName: string
          Type: LangType }

    type private LoweringState =
        { mutable NextTemporary: int
          mutable NextSyntheticMarker: int
          mutable AuthoredSpans: Set<SourceSpan>
          mutable SyntheticOrigins: Map<SourceSpan, SourceSpan> }

    type private Candidate =
        { Name: string
          Entry: WordEntry
          ParameterNames: string list option }

    type private BoundArguments =
        { ByParameter: Map<int, FlowExpression>
          HasNamedArguments: bool }

    let private knownTypes (context: Context) =
        Set.union
            (context.CompilerContext.Records |> Map.toSeq |> Seq.map fst |> Set.ofSeq)
            (context.CompilerContext.Scalars |> Map.toSeq |> Seq.map fst |> Set.ofSeq)

    let private fail code message word span expected actual =
        Diagnostics.raiseError code message word span expected actual

    let private diagnostic code message span expected actual : Diagnostic =
        { Code = code
          Message = message
          Word = None
          Span = span
          Expected = expected
          Actual = actual }

    let private spanOfExpression = function
        | FlowExpression.Literal(_, span)
        | FlowExpression.Local(_, span)
        | FlowExpression.Call(_, _, span)
        | FlowExpression.DotCall(_, _, _, span)
        | FlowExpression.If(_, _, _, span) -> span

    let private freshState firstSyntheticMarker =
        { NextTemporary = 0
          NextSyntheticMarker = firstSyntheticMarker
          AuthoredSpans = Set.empty
          SyntheticOrigins = Map.empty }

    let private rememberSpan state span =
        state.AuthoredSpans <- Set.add span state.AuthoredSpans

    let private syntheticSpan state origin =
        rememberSpan state origin
        let markerColumn = Int32.MaxValue - state.NextSyntheticMarker
        state.NextSyntheticMarker <- state.NextSyntheticMarker + 1
        let generated = { origin with Column = markerColumn; Length = 0 }
        state.SyntheticOrigins <- Map.add generated origin state.SyntheticOrigins
        generated

    let private freshTemporary state origin =
        let name = "$flow$tmp$" + string state.NextTemporary
        state.NextTemporary <- state.NextTemporary + 1
        name, syntheticSpan state origin

    let private internalParameterName index = "$flow$param$" + string index

    let private compilerLocals environment =
        environment
        |> Map.toList
        |> List.map (fun (_, binding) -> binding.InternalName, binding.Type)
        |> Map.ofList

    let private shortName (name: string) =
        let index = name.LastIndexOf('.')
        if index < 0 then name else name.Substring(index + 1)

    let private entryParameterNames (context: Context) name (entry: WordEntry) =
        match entry.Builtin with
        | Some(RecordConstructor typeName) ->
            context.CompilerContext.Records.TryFind typeName
            |> Option.map (fun definition -> definition.Fields |> List.map (fun field -> field.Name))
        | _ -> context.ParameterNames.TryFind name

    let private candidatesFor (context: Context) requestedName =
        let words = context.CompilerContext.Words
        let generatedTypeCandidates =
            let matchingType =
                context.CompilerContext.Records.ContainsKey requestedName
                || context.CompilerContext.Scalars.ContainsKey requestedName
            if not matchingType then []
            else
                words
                |> Map.toList
                |> List.choose (fun (name, entry) ->
                    match entry.Builtin with
                    | Some(RecordConstructor typeName) when typeName = requestedName -> Some(name, entry)
                    | Some(ScalarConstructor typeName) when typeName = requestedName -> Some(name, entry)
                    | _ -> None)
        let namedCandidates =
            if requestedName.Contains('.') then
                words.TryFind requestedName |> Option.map (fun entry -> [ requestedName, entry ]) |> Option.defaultValue []
            else
                words
                |> Map.toList
                |> List.filter (fun (name, _) -> shortName name = requestedName)
        let all = List.distinctBy fst (generatedTypeCandidates @ namedCandidates)
        all
        |> List.map (fun (name, entry) ->
            { Name = name
              Entry = entry
              ParameterNames = entryParameterNames context name entry })
        |> List.sortBy (fun candidate -> candidate.Name)

    let private unifyType (expected: LangType) (actual: LangType) (substitutions: Map<string, LangType>) =
        let rec unify (expected: LangType) (actual: LangType) (substitutions: Map<string, LangType>) =
            match expected, actual with
            | TVar name, actual ->
                match substitutions.TryFind name with
                | None -> Some(Map.add name actual substitutions)
                | Some existing when existing = actual -> Some substitutions
                | Some existing -> unify existing actual substitutions
            | TList left, TList right
            | TOption left, TOption right -> unify left right substitutions
            | TResult(leftOk, leftError), TResult(rightOk, rightError) ->
                unify leftOk rightOk substitutions |> Option.bind (unify leftError rightError)
            | left, right when left = right -> Some substitutions
            | _ -> None
        unify expected actual substitutions

    let private substituteType (substitutions: Map<string, LangType>) (typeValue: LangType) =
        let rec substitute (typeValue: LangType) =
            match typeValue with
            | TVar name -> substitutions.TryFind name |> Option.map substitute |> Option.defaultValue (TVar name)
            | TList item -> TList(substitute item)
            | TOption item -> TOption(substitute item)
            | TResult(ok, error) -> TResult(substitute ok, substitute error)
            | other -> other
        substitute typeValue

    let rec private mapArguments (context: Context) (state: LoweringState) (environment: Map<string, Binding>) (candidate: Candidate) (receiverType: LangType option) (arguments: FlowArgument list) (callSpan: SourceSpan) =
        let signature = candidate.Entry.Definition.Inputs
        let offset = if receiverType.IsSome then 1 else 0
        let expectedArgumentCount = signature.Length - offset
        if expectedArgumentCount < 0 then
            Error(diagnostic "FLOW_DOT_SIGNATURE" $"Word '{candidate.Name}' has no receiver input." (Some callSpan) [ "first input receiver" ] (signature |> List.map Types.format))
        else
            let parameterNames = candidate.ParameterNames
            let suppliedNames =
                match parameterNames with
                | Some names when names.Length = signature.Length -> Some(names |> List.skip offset)
                | _ -> None
            let indexed = ResizeArray<int * FlowExpression>()
            let mutable positionalIndex = offset
            let mutable error: Diagnostic option = None
            let hasNamed = arguments |> List.exists (function FlowArgument.Named _ -> true | _ -> false)
            for argument in arguments do
                match argument with
                | FlowArgument.Positional expression ->
                    if positionalIndex >= signature.Length then
                        error <- Some(diagnostic "FLOW_ARGUMENT_ARITY" $"Call to '{candidate.Name}' has too many positional arguments." (Some callSpan) [ string expectedArgumentCount ] [ string arguments.Length ])
                    else
                        indexed.Add(positionalIndex, expression)
                        positionalIndex <- positionalIndex + 1
                | FlowArgument.Named(name, expression, nameSpan) ->
                    match suppliedNames with
                    | None -> error <- Some(diagnostic "FLOW_NAMED_ARGUMENTS_UNAVAILABLE" $"Named arguments are unavailable for '{candidate.Name}' because its parameter names are not declared in the Flow catalog." (Some nameSpan) [] [ candidate.Name ])
                    | Some names ->
                        match names |> List.tryFindIndex ((=) name) with
                        | None -> error <- Some(diagnostic "FLOW_UNKNOWN_ARGUMENT" $"'{name}' is not a parameter of '{candidate.Name}'." (Some nameSpan) names [ name ])
                        | Some index -> indexed.Add(index + offset, expression)
            match error with
            | Some diagnostic -> Error diagnostic
            | None ->
                let duplicates = indexed |> Seq.groupBy fst |> Seq.tryFind (fun (_, group) -> Seq.length group > 1)
                match duplicates with
                | Some(index, _) -> Error(diagnostic "FLOW_DUPLICATE_ARGUMENT" $"Input {index + 1} of '{candidate.Name}' is supplied more than once." (Some callSpan) [] [ string (index + 1) ])
                | None when indexed.Count <> expectedArgumentCount ->
                    Error(diagnostic "FLOW_ARGUMENT_ARITY" $"Call to '{candidate.Name}' is missing one or more inputs." (Some callSpan) [ string expectedArgumentCount ] [ string indexed.Count ])
                | None ->
                    let byParameter = indexed |> Seq.map id |> Map.ofSeq
                    let actualInputs =
                        [ if receiverType.IsSome then yield receiverType.Value
                          for index in offset .. signature.Length - 1 do
                              match byParameter.TryFind index with
                              | Some expression ->
                                  try yield inferExpression context state environment expression
                                  with LanguageException error -> raise (LanguageException error)
                              | None -> () ]
                    let mutable substitutions = Map.empty
                    let mutable mismatch = None
                    List.zip signature actualInputs
                    |> List.iteri (fun index (expected, actual) ->
                        if mismatch.IsNone then
                            match unifyType expected actual substitutions with
                            | Some next -> substitutions <- next
                            | None -> mismatch <- Some(index, expected, actual))
                    match mismatch with
                    | Some(index, expected, actual) ->
                        Error(diagnostic "FLOW_ARGUMENT_TYPE" $"Argument {index + 1} to '{candidate.Name}' has an incompatible type." (Some callSpan) [ Types.format expected ] [ Types.format actual ])
                    | None ->
                        match candidate.Entry.Definition.Outputs |> List.map (substituteType substitutions) with
                        | [ output ] -> Ok({ ByParameter = byParameter; HasNamedArguments = hasNamed }, output)
                        | outputs -> Error(diagnostic "FLOW_CALL_OUTPUT_ARITY" $"Flow expressions require one-output words; '{candidate.Name}' has {outputs.Length} outputs." (Some callSpan) [ "one output" ] (outputs |> List.map Types.format))

    and private inferExpression (context: Context) (state: LoweringState) (environment: Map<string, Binding>) expression =
        let span = spanOfExpression expression
        rememberSpan state span
        match expression with
        | FlowExpression.Literal(literal, _) -> literal |> Types.literalValue |> Types.ofValue
        | FlowExpression.Local(name, _) ->
            match environment.TryFind name with
            | Some binding -> binding.Type
            | None -> fail "FLOW_UNKNOWN_LOCAL" $"Local '{name}' is not available before its immutable binding." None (Some span) [] [ name ]
        | FlowExpression.Call(name, arguments, _) ->
            let candidates = candidatesFor context name
            if List.isEmpty candidates then fail "FLOW_UNKNOWN_CALL" $"No word matches '{name}'." None (Some span) [] [ name ]
            let attempts = candidates |> List.map (fun candidate -> candidate, mapArguments context state environment candidate None arguments span)
            let successful = attempts |> List.choose (fun (candidate, result) -> result |> Result.toOption |> Option.map (fun (_, output) -> candidate, output))
            match successful with
            | [ _, output ] -> output
            | [] when candidates.Length = 1 ->
                match attempts.Head with
                | _, Error problem -> raise (LanguageException { problem with Word = Some candidates.Head.Name })
                | _ -> fail "FLOW_NO_MATCHING_CALL" $"Arguments do not match '{name}'." None (Some span) [] [ name ]
            | [] -> fail "FLOW_NO_MATCHING_CALL" $"No overload of '{name}' accepts these argument types." None (Some span) [] (candidates |> List.map (fun candidate -> candidate.Name))
            | _ -> fail "FLOW_AMBIGUOUS_CALL" $"Call '{name}' matches more than one word; use namespace::word qualification." None (Some span) [] (successful |> List.map (fun (candidate, _) -> candidate.Name))
        | FlowExpression.DotCall(receiver, stage, arguments, _) ->
            let receiverType = inferExpression context state environment receiver
            let candidates = candidatesFor context stage
            if List.isEmpty candidates then fail "FLOW_UNKNOWN_DOT_STAGE" $"No first-input word matches dot stage '{stage}'." None (Some span) [] [ stage ]
            let attempts = candidates |> List.map (fun candidate -> candidate, mapArguments context state environment candidate (Some receiverType) arguments span)
            let successful = attempts |> List.choose (fun (candidate, result) -> result |> Result.toOption |> Option.map (fun (_, output) -> candidate, output))
            match successful with
            | [ _, output ] -> output
            | [] -> fail "FLOW_NO_MATCHING_DOT_STAGE" $"No '{stage}' stage accepts receiver type {Types.format receiverType} and these arguments." None (Some span) [ Types.format receiverType ] (candidates |> List.map (fun candidate -> candidate.Name))
            | _ -> fail "FLOW_AMBIGUOUS_DOT_STAGE" $"Dot stage '{stage}' has more than one applicable first-input word; qualify the call explicitly." None (Some span) [] (successful |> List.map (fun (candidate, _) -> candidate.Name))
        | FlowExpression.If(condition, thenBody, elseBody, _) ->
            let conditionType = inferExpression context state environment condition
            if conditionType <> TBool then fail "FLOW_IF_CONDITION_TYPE" "Flow if condition must have type Bool." None (Some(spanOfExpression condition)) [ "Bool" ] [ Types.format conditionType ]
            let thenType = inferStatements context state environment thenBody
            let elseType = inferStatements context state environment elseBody
            match thenType, elseType with
            | Some left, Some right when left = right -> left
            | Some left, Some right -> fail "FLOW_IF_BRANCH_TYPE" "Both Flow if branches must produce the same single type." None (Some span) [ Types.format left ] [ Types.format right ]
            | None, _ | _, None -> fail "FLOW_IF_BRANCH_VALUE" "Each Flow if branch must end with a value expression." None (Some span) [ "one value in each branch" ] []

    and private inferStatements (context: Context) (state: LoweringState) (initialEnvironment: Map<string, Binding>) (statements: FlowStatement list) =
        let mutable environment = initialEnvironment
        let mutable finalValue = None
        for index, statement in statements |> List.indexed do
            match statement with
            | FlowStatement.Let(name, value, statementSpan) ->
                rememberSpan state statementSpan
                if environment.ContainsKey name then fail "FLOW_LOCAL_REBOUND" $"Immutable local '{name}' is already declared in this lexical scope." None (Some statementSpan) [] [ name ]
                let valueType = inferExpression context state environment value
                environment <- Map.add name { InternalName = name; Type = valueType } environment
                finalValue <- None
            | FlowStatement.Evaluate expression ->
                let valueType = inferExpression context state environment expression
                finalValue <- if index = statements.Length - 1 then Some valueType else None
        finalValue

    let private createOrigin state span =
        rememberSpan state span
        span

    let private bindArguments (candidate: Candidate) receiverType arguments callSpan context state environment =
        match mapArguments context state environment candidate receiverType arguments callSpan with
        | Ok value -> value
        | Error problem -> raise (LanguageException { problem with Word = Some candidate.Name })

    let rec private lowerFlowExpression (context: Context) (state: LoweringState) (environment: Map<string, Binding>) expression : Expr list =
        let span = spanOfExpression expression
        rememberSpan state span
        match expression with
        | FlowExpression.Literal(literal, sourceSpan) -> [ Push(literal, sourceSpan) ]
        | FlowExpression.Local(name, sourceSpan) ->
            match environment.TryFind name with
            | Some binding -> [ Load(binding.InternalName, sourceSpan) ]
            | None -> fail "FLOW_UNKNOWN_LOCAL" $"Local '{name}' is not available before its immutable binding." None (Some sourceSpan) [] [ name ]
        | FlowExpression.Call(name, arguments, callSpan) ->
            let candidate, bound = selectCall context state environment name None arguments callSpan
            lowerResolvedCall context state environment candidate bound None arguments callSpan
        | FlowExpression.DotCall(receiver, stage, arguments, callSpan) ->
            let receiverType = inferExpression context state environment receiver
            let candidate, bound = selectCall context state environment stage (Some receiverType) arguments callSpan
            lowerResolvedCall context state environment candidate bound (Some receiver) arguments callSpan
        | FlowExpression.If(condition, thenStatements, elseStatements, ifSpan) ->
            let conditionType = inferExpression context state environment condition
            if conditionType <> TBool then fail "FLOW_IF_CONDITION_TYPE" "Flow if condition must have type Bool." None (Some(spanOfExpression condition)) [ "Bool" ] [ Types.format conditionType ]
            let thenValueType = inferStatements context state environment thenStatements
            let elseValueType = inferStatements context state environment elseStatements
            match thenValueType, elseValueType with
            | Some left, Some right when left = right -> ()
            | Some left, Some right -> fail "FLOW_IF_BRANCH_TYPE" "Both Flow if branches must produce the same single type." None (Some ifSpan) [ Types.format left ] [ Types.format right ]
            | _ -> fail "FLOW_IF_BRANCH_VALUE" "Each Flow if branch must end with a value expression." None (Some ifSpan) [ "one value in each branch" ] []
            let thenCode, _ = lowerStatements context state environment thenStatements
            let elseCode, _ = lowerStatements context state environment elseStatements
            let thenScope = Scope(thenCode, syntheticSpan state ifSpan)
            let elseScope = Scope(elseCode, syntheticSpan state ifSpan)
            lowerFlowExpression context state environment condition @ [ If([ thenScope ], [ elseScope ], ifSpan) ]

    and private selectCall context state environment requestedName receiverType arguments callSpan =
        let candidates = candidatesFor context requestedName
        if List.isEmpty candidates then
            let code = if receiverType.IsSome then "FLOW_UNKNOWN_DOT_STAGE" else "FLOW_UNKNOWN_CALL"
            fail code $"No word matches '{requestedName}'." None (Some callSpan) [] [ requestedName ]
        let attempts = candidates |> List.map (fun candidate -> candidate, mapArguments context state environment candidate receiverType arguments callSpan)
        let successful = attempts |> List.choose (fun (candidate, result) -> result |> Result.toOption |> Option.map (fun bound -> candidate, bound))
        match successful with
        | [ candidate, (bound, _) ] -> candidate, bound
        | [] when candidates.Length = 1 ->
            match attempts.Head with
            | _, Error problem -> raise (LanguageException { problem with Word = Some candidates.Head.Name })
            | _ -> fail "FLOW_NO_MATCHING_CALL" $"Arguments do not match '{requestedName}'." None (Some callSpan) [] [ requestedName ]
        | [] when receiverType.IsSome -> fail "FLOW_NO_MATCHING_DOT_STAGE" $"No '{requestedName}' stage accepts this receiver and its arguments." None (Some callSpan) (receiverType |> Option.map Types.format |> Option.toList) (candidates |> List.map (fun candidate -> candidate.Name))
        | [] -> fail "FLOW_NO_MATCHING_CALL" $"No overload of '{requestedName}' accepts these arguments." None (Some callSpan) [] (candidates |> List.map (fun candidate -> candidate.Name))
        | _ when receiverType.IsSome -> fail "FLOW_AMBIGUOUS_DOT_STAGE" $"Dot stage '{requestedName}' has more than one applicable first-input word; qualify the call explicitly." None (Some callSpan) [] (successful |> List.map (fun (candidate, _) -> candidate.Name))
        | _ -> fail "FLOW_AMBIGUOUS_CALL" $"Call '{requestedName}' matches more than one word; use namespace::word qualification." None (Some callSpan) [] (successful |> List.map (fun (candidate, _) -> candidate.Name))

    and private lowerResolvedCall context state environment candidate (bound: BoundArguments) receiver arguments callSpan =
        let argumentFormalIndexes =
            let offset = if receiver.IsSome then 1 else 0
            let mutable positional = offset
            arguments
            |> List.map (function
                | FlowArgument.Positional _ -> let current = positional in positional <- positional + 1; current
                | FlowArgument.Named(name, _, _) ->
                    match candidate.ParameterNames with
                    | Some names when names.Length = candidate.Entry.Definition.Inputs.Length ->
                        names |> List.skip offset |> List.tryFindIndex ((=) name) |> Option.map (fun value -> value + offset) |> Option.defaultValue -1
                    | _ -> -1)
        let targetName = candidate.Name
        if not bound.HasNamedArguments then
            let receiverCode = receiver |> Option.map (lowerFlowExpression context state environment) |> Option.defaultValue []
            let explicitCode = arguments |> List.collect (function FlowArgument.Positional value | FlowArgument.Named(_, value, _) -> lowerFlowExpression context state environment value)
            receiverCode @ explicitCode @ [ Call(targetName, callSpan) ]
        else
            let wrapperSpan = syntheticSpan state callSpan
            let mutable tempByParameter = Map.empty
            let setup = ResizeArray<Expr>()
            match receiver with
            | Some expression ->
                let receiverOrigin = spanOfExpression expression
                let temporary, tempSpan = freshTemporary state receiverOrigin
                setup.AddRange(lowerFlowExpression context state environment expression)
                setup.Add(Let(temporary, tempSpan))
                tempByParameter <- Map.add 0 temporary tempByParameter
            | None -> ()
            for argument, formalIndex in List.zip arguments argumentFormalIndexes do
                let value = match argument with | FlowArgument.Positional value | FlowArgument.Named(_, value, _) -> value
                let origin = match argument with | FlowArgument.Named(_, _, namedSpan) -> namedSpan | _ -> spanOfExpression value
                let temporary, tempSpan = freshTemporary state origin
                setup.AddRange(lowerFlowExpression context state environment value)
                setup.Add(Let(temporary, tempSpan))
                tempByParameter <- Map.add formalIndex temporary tempByParameter
            let loads =
                [ 0 .. candidate.Entry.Definition.Inputs.Length - 1 ]
                |> List.map (fun parameterIndex ->
                    match tempByParameter.TryFind parameterIndex with
                    | Some temporary -> Load(temporary, syntheticSpan state callSpan)
                    | None ->
                        let argument = bound.ByParameter.TryFind parameterIndex
                        match argument with
                        | Some _ -> fail "FLOW_ARGUMENT_REORDER_INVARIANT" "A named argument was not evaluated into its source-order temporary." (Some targetName) (Some callSpan) [] [ string parameterIndex ]
                        | None -> fail "FLOW_ARGUMENT_VALUE_MISSING" "A call input has no lowered value." (Some targetName) (Some callSpan) [] [ string parameterIndex ])
            Expr.Scope(List.ofSeq setup @ loads @ [ Call(targetName, callSpan) ], wrapperSpan) |> List.singleton

    and private lowerStatements context state initialEnvironment statements =
        let mutable environment = initialEnvironment
        let output = ResizeArray<Expr>()
        for index, statement in statements |> List.indexed do
            match statement with
            | FlowStatement.Let(name, value, statementSpan) ->
                rememberSpan state statementSpan
                if environment.ContainsKey name then fail "FLOW_LOCAL_REBOUND" $"Immutable local '{name}' is already declared in this lexical scope." None (Some statementSpan) [] [ name ]
                let valueType = inferExpression context state environment value
                output.AddRange(lowerFlowExpression context state environment value)
                output.Add(Let(name, statementSpan))
                environment <- Map.add name { InternalName = name; Type = valueType } environment
            | FlowStatement.Evaluate expression ->
                let code = lowerFlowExpression context state environment expression
                output.AddRange code
                if index < statements.Length - 1 then
                    let origin = spanOfExpression expression
                    let temporary, tempSpan = freshTemporary state origin
                    let scopeSpan = syntheticSpan state origin
                    output.Add(Scope([ Let(temporary, tempSpan) ], scopeSpan))
        List.ofSeq output, environment

    let private makeProjection (state: LoweringState) : FlowSourceProjection =
        { AuthoredSpans = state.AuthoredSpans
          SyntheticOrigins = state.SyntheticOrigins }

    let private mergeOrigins (context: Context) (projection: FlowSourceProjection) : Map<SourceSpan, SourceSpan> =
        let collision = projection.SyntheticOrigins |> Map.toSeq |> Seq.tryFind (fun (marker, _) -> context.SourceOrigins.ContainsKey marker)
        match collision with
        | Some _ -> fail "FLOW_SOURCE_ORIGIN_COLLISION" "Internal Flow source markers must remain unique across one compiler snapshot." None None [] [ "marker collision" ]
        | None -> Map.fold (fun found marker origin -> Map.add marker origin found) context.SourceOrigins projection.SyntheticOrigins

    let private sourceSites (sourceMap: Map<SourceSiteId, IrSourceSite>) =
        sourceMap |> Map.map (fun _ source -> source.SiteSpan)

    let private lowerExpressionBody context expression =
        let state = freshState context.SourceOrigins.Count
        let lowered = lowerFlowExpression context state Map.empty expression
        { Expressions = lowered
          SourceText = FlowSource.renderExpression expression
          SyntaxVersion = 1
          Projection = makeProjection state }

    let lowerExpression context expression = lowerExpressionBody context expression

    let checkExpression context expression =
        let lowered = lowerExpression context expression
        let checkedExpression = Compiler.checkExpression (knownTypes context) context.CompilerContext.Words lowered.Expressions
        lowered, checkedExpression

    let compileExpression context expression =
        let lowered, checkedExpression = checkExpression context expression
        match checkedExpression.Stack with
        | [ _ ] -> ()
        | actual -> fail "FLOW_EXPRESSION_ARITY" "An isolated Flow expression must produce exactly one value." None (Some(spanOfExpression expression)) [ "one value" ] (actual |> List.map Types.format)
        let program = Compiler.compileIrProgramWithSourceOrigins context.CompilerContext context.SourceOrigins
        let origins = mergeOrigins context lowered.Projection
        let body = Compiler.compileIrBodyAgainstProgramWithSourceOrigins context.CompilerContext program "<flow-expression>" [] lowered.Expressions origins
        let bodyData = VerifiedIrBody.inspect body
        { Lowered = lowered
          Program = program
          Body = body
          SiteOrigins = sourceSites bodyData.BodySourceMap }

    let lowerWord context (flowWord: FlowWordDefinition) =
        if flowWord.SyntaxVersion <> 1 then fail "FLOW_VERSION_UNSUPPORTED" "Only Flow syntax version 1 is supported by this compiler slice." (Some flowWord.Name) (Some flowWord.Span) [ "1" ] [ string flowWord.SyntaxVersion ]
        let names = flowWord.Parameters |> List.map (fun parameter -> parameter.Name)
        if names.Length <> (Set.ofList names).Count then fail "FLOW_DUPLICATE_PARAMETER" "Flow word parameters must have unique names." (Some flowWord.Name) (Some flowWord.Span) [] names
        let state = freshState context.SourceOrigins.Count
        rememberSpan state flowWord.Span
        let environment =
            flowWord.Parameters
            |> List.mapi (fun index parameter ->
                rememberSpan state parameter.Span
                parameter.Name, { InternalName = internalParameterName index; Type = parameter.Type })
            |> Map.ofList
        let parameterBindings =
            flowWord.Parameters
            |> List.mapi (fun index parameter -> index, parameter)
            |> List.rev
            |> List.map (fun (index, parameter) -> Let(internalParameterName index, syntheticSpan state parameter.Span))
        let body, _ = lowerStatements context state environment flowWord.Body
        let definition =
            { Name = flowWord.Name
              Inputs = flowWord.Parameters |> List.map (fun parameter -> parameter.Type)
              Outputs = [ flowWord.Output ]
              Effects = flowWord.Effects
              Maturity = ProjectWord
              Revision = 0
              Documentation = flowWord.Documentation
              Body = parameterBindings @ body
              SourceText = flowWord.SourceText
              Span = flowWord.Span }
        { Definition = definition
          ParameterNames = names
          SourceText = flowWord.SourceText
          SyntaxVersion = flowWord.SyntaxVersion
          Projection = makeProjection state }

    let checkWord context flowWord =
        let lowered = lowerWord context flowWord
        let checkedDefinition = Compiler.checkDefinition (knownTypes context) context.CompilerContext.Words lowered.Definition
        lowered, checkedDefinition

    let compileWord context wordId flowWord =
        let lowered, _ = checkWord context flowWord
        if context.CompilerContext.Words.ContainsKey lowered.Definition.Name then
            fail "FLOW_WORD_EXISTS" $"Word '{lowered.Definition.Name}' already exists in the supplied compiler snapshot." (Some lowered.Definition.Name) (Some flowWord.Span) [] [ lowered.Definition.Name ]
        if context.CompilerContext.WordIds.ContainsKey lowered.Definition.Name then
            fail "FLOW_WORD_ID_EXISTS" $"Stable ID for '{lowered.Definition.Name}' already exists in the supplied compiler snapshot." (Some lowered.Definition.Name) (Some flowWord.Span) [] [ lowered.Definition.Name ]
        let entry =
            { Definition = lowered.Definition
              Builtin = None
              Status = Candidate
              Maturity = ProjectWord
              Revision = lowered.Definition.Revision }
        let compilerContext =
            { context.CompilerContext with
                Words = Map.add lowered.Definition.Name entry context.CompilerContext.Words
                WordIds = Map.add lowered.Definition.Name wordId context.CompilerContext.WordIds }
        let combinedOrigins = mergeOrigins context lowered.Projection
        let nextContext =
            { CompilerContext = compilerContext
              ParameterNames = Map.add lowered.Definition.Name lowered.ParameterNames context.ParameterNames
              SourceOrigins = combinedOrigins }
        let program = Compiler.compileIrProgramWithSourceOrigins compilerContext combinedOrigins
        let programData = VerifiedIrProgram.inspect program
        let sites =
            programData.SourceMap
            |> Map.filter (fun _ source -> source.SiteOwner = Some wordId)
            |> sourceSites
        { Lowered = lowered; Context = nextContext; Program = program; SiteOrigins = sites }

    let parameterCatalog (definitions: FlowWordDefinition list) =
        definitions
        |> List.map (fun definition -> definition.Name, (definition.Parameters |> List.map (fun parameter -> parameter.Name)))
        |> Map.ofList
