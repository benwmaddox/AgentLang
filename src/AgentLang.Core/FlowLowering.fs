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

    type CompiledTest =
        { Lowered: FlowLoweredTest
          Program: VerifiedIrProgram
          Body: VerifiedIrBody
          ExpectationBody: VerifiedIrBody option
          BodySiteOrigins: Map<SourceSiteId, SourceSpan>
          ExpectationSiteOrigins: Map<SourceSiteId, SourceSpan> option }

    type CompiledExample =
        { Lowered: FlowLoweredExample
          Program: VerifiedIrProgram
          Body: VerifiedIrBody
          SiteOrigins: Map<SourceSiteId, SourceSpan> }

    type private Binding =
        { InternalName: string
          Type: LangType }

    type private LoweringState =
        { mutable NextTemporary: int
          mutable NextSyntheticMarker: int64
          mutable AuthoredSpans: Set<SourceSpan>
          mutable SyntheticOrigins: Map<SourceSpan, SourceSpan> }

    type private Candidate =
        { Name: string
          Entry: WordEntry
          ParameterNames: string list option }

    [<RequireQualifiedAccess>]
    type private ListCallbackOperation =
        | Map
        | Filter
        | Each

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
        | FlowExpression.If(_, _, _, span)
        | FlowExpression.Container(_, _, _, span)
        | FlowExpression.MatchOption(_, _, _, span)
        | FlowExpression.MatchResult(_, _, _, span) -> span

    let private freshState (retainedOrigins: Map<SourceSpan, SourceSpan>) =
        let highestRetainedIndex =
            retainedOrigins
            |> Map.toSeq
            |> Seq.choose (fun (marker, _) ->
                if marker.Length = 0 && marker.Column > 0 then Some(int64 Int32.MaxValue - int64 marker.Column)
                else None)
            |> Seq.fold max -1L
        { NextTemporary = 0
          NextSyntheticMarker = highestRetainedIndex + 1L
          AuthoredSpans = Set.empty
          SyntheticOrigins = Map.empty }

    let private rememberSpan state span =
        state.AuthoredSpans <- Set.add span state.AuthoredSpans

    let private syntheticSpan state origin =
        rememberSpan state origin
        if state.NextSyntheticMarker < 0L || state.NextSyntheticMarker >= int64 Int32.MaxValue then
            fail "FLOW_SOURCE_MARKER_EXHAUSTED" "Flow lowering exhausted its bounded private source-marker range." None (Some origin) [ "available source marker" ] [ string state.NextSyntheticMarker ]
        let markerColumn = int32 (int64 Int32.MaxValue - state.NextSyntheticMarker)
        state.NextSyntheticMarker <- state.NextSyntheticMarker + 1L
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

    let private validateClosedTypeArgument (context: Context) (argument: FlowTypeArgument) =
        let known = knownTypes context
        let rec validate typeValue =
            match typeValue with
            | TInt | TFloat | TBool | TString | TUnit -> ()
            | TList item | TOption item -> validate item
            | TResult(okType, errorType) -> validate okType; validate errorType
            | TNamed name when known.Contains name -> ()
            | TNamed name -> fail "FLOW_CONSTRUCTOR_UNKNOWN_TYPE" $"Container constructor refers to undeclared type '{name}'." None (Some argument.Span) [ "known record or refined type" ] [ name ]
            | TVar name -> fail "FLOW_CONSTRUCTOR_OPEN_TYPE" "Container constructors require fully closed type arguments." None (Some argument.Span) [ "closed type" ] [ name ]
        validate argument.Type

    let private constructorTypeArguments (context: Context) (kind: FlowContainerConstructor) (typeArguments: FlowTypeArgument list) (span: SourceSpan) =
        let expected =
            match kind with
            | FlowContainerConstructor.ResultOk | FlowContainerConstructor.ResultError -> 2
            | _ -> 1
        if typeArguments.Length <> expected then
            fail "FLOW_CONSTRUCTOR_TYPE_ARITY" "Container constructor has the wrong number of explicit type arguments." None (Some span) [ string expected ] [ string typeArguments.Length ]
        typeArguments |> List.iter (validateClosedTypeArgument context)
        typeArguments |> List.map (fun argument -> argument.Type)

    let private coreConstructor = function
        | FlowContainerConstructor.ListEmpty -> ListEmpty
        | FlowContainerConstructor.ListSingleton -> ListSingleton
        | FlowContainerConstructor.OptionNone -> OptionNone
        | FlowContainerConstructor.OptionSome -> OptionSome
        | FlowContainerConstructor.ResultOk -> ResultOk
        | FlowContainerConstructor.ResultError -> ResultError

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

    let private listCallbackOperation = function
        | "map" -> Some ListCallbackOperation.Map
        | "filter" -> Some ListCallbackOperation.Filter
        | "each" -> Some ListCallbackOperation.Each
        | _ -> None

    let private callbackRequiredOutput = function
        | ListCallbackOperation.Map -> None
        | ListCallbackOperation.Filter -> Some TBool
        | ListCallbackOperation.Each -> Some TUnit

    let private callbackResultType operation itemType outputType =
        match operation with
        | ListCallbackOperation.Map -> TList outputType
        | ListCallbackOperation.Filter -> TList itemType
        | ListCallbackOperation.Each -> TUnit

    let private containsOpenType typeValue =
        let rec visit = function
            | TVar _ -> true
            | TList item | TOption item -> visit item
            | TResult(okType, errorType) -> visit okType || visit errorType
            | _ -> false
        visit typeValue

    let private validateWordReferenceShape (state: LoweringState) (reference: FlowWordReference) =
        let pieces = if String.IsNullOrEmpty reference.Name then [||] else reference.Name.Split('.')
        let validSegment (piece: string) =
            piece.Length > 0
            && (Char.IsLetter piece[0] || piece[0] = '_')
            && (piece
                |> Seq.skip 1
                |> Seq.forall (fun character ->
                    Char.IsLetterOrDigit character
                    || character = '_'
                    || character = '-'
                    || character = '?'
                    || character = '!'))
        let hasValidSegments = pieces.Length > 0 && (pieces |> Array.forall validSegment)
        if not hasValidSegments
           || (reference.IsExplicitShort && pieces.Length <> 1)
           || (not reference.IsExplicitShort && pieces.Length < 2) then
            fail "FLOW_CALLBACK_REFERENCE_SHAPE" "Static callback references must be either namespace-qualified or explicitly marked short names." None (Some reference.Span) [ "qualified name or `word shortName`" ] [ reference.Name ]
        rememberSpan state reference.Span

    let private callbackCandidates (context: Context) (reference: FlowWordReference) =
        let entries =
            if reference.IsExplicitShort then
                context.CompilerContext.Words
                |> Map.toList
                |> List.filter (fun (name, _) -> shortName name = reference.Name)
            else
                context.CompilerContext.Words.TryFind reference.Name
                |> Option.map (fun entry -> [ reference.Name, entry ])
                |> Option.defaultValue []
        entries
        |> List.sortBy fst
        |> List.map (fun (name, entry) ->
            { Name = name
              Entry = entry
              ParameterNames = entryParameterNames context name entry })

    let private resolveListCallback
        (context: Context)
        (state: LoweringState)
        (reference: FlowWordReference)
        (itemType: LangType)
        (requiredOutput: LangType option)
        : Candidate * LangType =
        validateWordReferenceShape state reference
        let candidates = callbackCandidates context reference
        if List.isEmpty candidates then
            fail "FLOW_UNKNOWN_CALLBACK" $"No static callback word matches '{reference.Name}'." None (Some reference.Span) [] [ reference.Name ]
        if candidates.Length > 1 then
            fail "FLOW_AMBIGUOUS_CALLBACK" $"Static callback '{reference.Name}' resolves to more than one word; use a qualified reference." None (Some reference.Span) [] (candidates |> List.map (fun candidate -> candidate.Name))

        let candidate = candidates.Head
        let inputs = candidate.Entry.Definition.Inputs
        let outputs = candidate.Entry.Definition.Outputs
        if inputs.Length <> 1 then
            fail "FLOW_CALLBACK_INPUT_ARITY" $"Callback '{candidate.Name}' must have exactly one input." (Some candidate.Name) (Some reference.Span) [ "one input" ] (inputs |> List.map Types.format)
        if outputs.Length <> 1 then
            fail "FLOW_CALLBACK_OUTPUT_ARITY" $"Callback '{candidate.Name}' must have exactly one output." (Some candidate.Name) (Some reference.Span) [ "one output" ] (outputs |> List.map Types.format)
        let substitutions =
            match unifyType inputs.Head itemType Map.empty with
            | Some values -> values
            | None -> fail "FLOW_CALLBACK_INPUT_TYPE" $"Callback '{candidate.Name}' cannot accept list elements of type {Types.format itemType}." (Some candidate.Name) (Some reference.Span) [ Types.format itemType ] [ Types.format inputs.Head ]
        let outputType = substituteType substitutions outputs.Head
        match requiredOutput with
        | Some expected when expected <> outputType ->
            fail "FLOW_CALLBACK_RESULT_TYPE" $"Callback '{candidate.Name}' must return {Types.format expected}." (Some candidate.Name) (Some reference.Span) [ Types.format expected ] [ Types.format outputType ]
        | _ when containsOpenType outputType ->
            fail "FLOW_CALLBACK_OPEN_OUTPUT" $"Callback '{candidate.Name}' has an output type that cannot be resolved from the list element." (Some candidate.Name) (Some reference.Span) [ "closed output type" ] [ Types.format outputType ]
        | _ -> candidate, outputType

    let private callbackReferenceIn arguments =
        arguments |> List.tryPick (function FlowArgument.WordReference reference -> Some reference | _ -> None)

    let private rejectWordReferenceContext (reference: FlowWordReference) =
        fail "FLOW_CALLBACK_REFERENCE_CONTEXT" "A static callback word reference is not a first-class value and is valid only as the sole argument to a list callback stage." None (Some reference.Span) [] [ reference.Name ]

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
                | FlowArgument.WordReference reference ->
                    error <- Some(diagnostic "FLOW_CALLBACK_REFERENCE_CONTEXT" "A static callback word reference is not an ordinary value argument." (Some reference.Span) [] [ reference.Name ])
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
                        let outputs = candidate.Entry.Definition.Outputs |> List.map (substituteType substitutions)
                        Ok({ ByParameter = byParameter; HasNamedArguments = hasNamed }, outputs)

    and private inferExpression (context: Context) (state: LoweringState) (environment: Map<string, Binding>) expression =
        let span = spanOfExpression expression
        let outputs = inferOutputs context state environment expression
        match outputs with
        | [ output ] -> output
        | values ->
            let code =
                match expression with
                | FlowExpression.Call _ | FlowExpression.DotCall _ -> "FLOW_CALL_OUTPUT_ARITY"
                | _ -> "FLOW_EXPRESSION_OUTPUT_ARITY"
            fail code "This Flow expression position requires exactly one output." None (Some span) [ "one output" ] (values |> List.map Types.format)

    and private inferOutputs (context: Context) (state: LoweringState) (environment: Map<string, Binding>) expression =
        let span = spanOfExpression expression
        rememberSpan state span
        match expression with
        | FlowExpression.Literal(literal, _) -> [ literal |> Types.literalValue |> Types.ofValue ]
        | FlowExpression.Local(name, _) ->
            match environment.TryFind name with
            | Some binding -> [ binding.Type ]
            | None -> fail "FLOW_UNKNOWN_LOCAL" $"Local '{name}' is not available before its immutable binding." None (Some span) [] [ name ]
        | FlowExpression.Call(name, arguments, _) ->
            match callbackReferenceIn arguments with
            | Some reference -> rejectWordReferenceContext reference
            | None -> ()
            let _, _, outputs = selectCallOutputs context state environment name None arguments span
            outputs
        | FlowExpression.DotCall(receiver, stage, arguments, _) ->
            let receiverType = inferExpression context state environment receiver
            match listCallbackOperation stage, arguments with
            | Some operation, [ FlowArgument.WordReference reference ] ->
                validateWordReferenceShape state reference
                match receiverType with
                | TList itemType ->
                    let _, outputType = resolveListCallback context state reference itemType (callbackRequiredOutput operation)
                    [ callbackResultType operation itemType outputType ]
                | actual -> fail "FLOW_CALLBACK_REQUIRES_LIST" $"Static '{stage}' callback stages require a List<T> receiver." None (Some reference.Span) [ "List<T>" ] [ Types.format actual ]
            | _, _ when Option.isSome (callbackReferenceIn arguments) ->
                match callbackReferenceIn arguments with
                | Some reference when listCallbackOperation stage |> Option.isSome ->
                    fail "FLOW_CALLBACK_ARGUMENT_ARITY" $"Static '{stage}' callback syntax requires exactly one positional word reference." None (Some reference.Span) [ "one callback reference" ] [ string arguments.Length ]
                | Some reference -> rejectWordReferenceContext reference
                | None -> failwith "unreachable"
            | _ ->
                let _, _, outputs = selectCallOutputs context state environment stage (Some receiverType) arguments span
                outputs
        | FlowExpression.Container(kind, typeArguments, payload, constructorSpan) ->
            let types = constructorTypeArguments context kind typeArguments constructorSpan
            let outputType, requiredPayloadType =
                match kind, types with
                | FlowContainerConstructor.ListEmpty, [ item ] -> TList item, None
                | FlowContainerConstructor.ListSingleton, [ item ] -> TList item, Some item
                | FlowContainerConstructor.OptionNone, [ item ] -> TOption item, None
                | FlowContainerConstructor.OptionSome, [ item ] -> TOption item, Some item
                | FlowContainerConstructor.ResultOk, [ okType; errorType ] -> TResult(okType, errorType), Some okType
                | FlowContainerConstructor.ResultError, [ okType; errorType ] -> TResult(okType, errorType), Some errorType
                | _ -> fail "FLOW_CONSTRUCTOR_TYPE_ARITY" "Container constructor type arguments do not match its closed type contract." None (Some constructorSpan) [] (types |> List.map Types.format)
            match requiredPayloadType, payload with
            | None, None -> [ outputType ]
            | Some expected, Some expression ->
                let actual = inferExpression context state environment expression
                if expected <> actual then
                    fail "FLOW_CONSTRUCTOR_PAYLOAD_TYPE" "Container constructor payload must exactly match its declared nominal type." None (Some(spanOfExpression expression)) [ Types.format expected ] [ Types.format actual ]
                [ outputType ]
            | None, Some expression -> fail "FLOW_CONSTRUCTOR_ARITY" "This container constructor does not accept a payload." None (Some(spanOfExpression expression)) [ "no payload" ] [ "payload supplied" ]
            | Some _, None -> fail "FLOW_CONSTRUCTOR_ARITY" "This container constructor requires a payload." None (Some constructorSpan) [ "one payload" ] []
        | FlowExpression.MatchOption(scrutinee, someCase, noneCase, matchSpan) ->
            let itemType =
                match inferExpression context state environment scrutinee with
                | TOption item -> item
                | actual -> fail "FLOW_MATCH_REQUIRES_OPTION" "Option match requires an Option<T> scrutinee." None (Some matchSpan) [ "Option<T>" ] [ Types.format actual ]
            if environment.ContainsKey someCase.Name then
                fail "FLOW_MATCH_PAYLOAD_SHADOW" $"Option payload local '{someCase.Name}' cannot shadow an outer local." None (Some someCase.NameSpan) [] [ someCase.Name ]
            let someEnvironment = Map.add someCase.Name { InternalName = someCase.Name; Type = itemType } environment
            let someOutputs = inferStatements context state someEnvironment someCase.Statements
            let noneOutputs = inferStatements context state environment noneCase.Statements
            match someOutputs, noneOutputs with
            | Some someValues, Some noneValues when someValues = noneValues -> someValues
            | Some someValues, Some noneValues -> fail "FLOW_MATCH_BRANCH_TYPE" "Option match cases must produce the same output vector in the same order." None (Some matchSpan) (someValues |> List.map Types.format) (noneValues |> List.map Types.format)
            | _ -> fail "FLOW_MATCH_BRANCH_VALUE" "Both Option match cases must produce a nonempty output vector." None (Some matchSpan) [ "one or more outputs from Some and None" ] []
        | FlowExpression.MatchResult(scrutinee, okCase, errorCase, matchSpan) ->
            let okType, errorType =
                match inferExpression context state environment scrutinee with
                | TResult(okValue, errorValue) -> okValue, errorValue
                | actual -> fail "FLOW_MATCH_REQUIRES_RESULT" "Result match requires a Result<T, E> scrutinee." None (Some matchSpan) [ "Result<T, E>" ] [ Types.format actual ]
            if environment.ContainsKey okCase.Name then
                fail "FLOW_MATCH_PAYLOAD_SHADOW" $"Result ok payload local '{okCase.Name}' cannot shadow an outer local." None (Some okCase.NameSpan) [] [ okCase.Name ]
            if environment.ContainsKey errorCase.Name then
                fail "FLOW_MATCH_PAYLOAD_SHADOW" $"Result error payload local '{errorCase.Name}' cannot shadow an outer local." None (Some errorCase.NameSpan) [] [ errorCase.Name ]
            let okEnvironment = Map.add okCase.Name { InternalName = okCase.Name; Type = okType } environment
            let errorEnvironment = Map.add errorCase.Name { InternalName = errorCase.Name; Type = errorType } environment
            let okOutputs = inferStatements context state okEnvironment okCase.Statements
            let errorOutputs = inferStatements context state errorEnvironment errorCase.Statements
            match okOutputs, errorOutputs with
            | Some okValues, Some errorValues when okValues = errorValues -> okValues
            | Some okValues, Some errorValues -> fail "FLOW_MATCH_BRANCH_TYPE" "Result match cases must produce the same output vector in the same order." None (Some matchSpan) (okValues |> List.map Types.format) (errorValues |> List.map Types.format)
            | _ -> fail "FLOW_MATCH_BRANCH_VALUE" "Both Result match cases must produce a nonempty output vector." None (Some matchSpan) [ "one or more outputs from Ok and Error" ] []
        | FlowExpression.If(condition, thenBody, elseBody, _) ->
            let conditionType = inferExpression context state environment condition
            if conditionType <> TBool then fail "FLOW_IF_CONDITION_TYPE" "Flow if condition must have type Bool." None (Some(spanOfExpression condition)) [ "Bool" ] [ Types.format conditionType ]
            let thenOutputs = inferStatements context state environment thenBody
            let elseOutputs = inferStatements context state environment elseBody
            match thenOutputs, elseOutputs with
            | Some left, Some right when left = right -> left
            | Some left, Some right -> fail "FLOW_IF_BRANCH_TYPE" "Both Flow if branches must produce the same output vector in the same order." None (Some span) (left |> List.map Types.format) (right |> List.map Types.format)
            | None, _ | _, None -> fail "FLOW_IF_BRANCH_VALUE" "Each Flow if branch must produce a nonempty output vector." None (Some span) [ "one or more outputs in each branch" ] []

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
            | FlowStatement.LetMany(bindings, value, statementSpan) ->
                rememberSpan state statementSpan
                let valueTypes = inferOutputs context state environment value
                if bindings.Length <> valueTypes.Length then
                    fail "FLOW_DESTRUCTURE_ARITY" "A destructuring binding must name every output exactly once." None (Some statementSpan) [ string valueTypes.Length ] [ string bindings.Length ]
                for (name, nameSpan), valueType in List.zip bindings valueTypes do
                    if environment.ContainsKey name then fail "FLOW_LOCAL_REBOUND" $"Immutable local '{name}' is already declared in this lexical scope." None (Some nameSpan) [] [ name ]
                    environment <- Map.add name { InternalName = name; Type = valueType } environment
                finalValue <- None
            | FlowStatement.Evaluate expression ->
                let valueType = inferExpression context state environment expression
                finalValue <- if index = statements.Length - 1 then Some [ valueType ] else None
            | FlowStatement.Return(values, statementSpan) ->
                rememberSpan state statementSpan
                let outputTypes = values |> List.map (inferExpression context state environment)
                finalValue <- if index = statements.Length - 1 then Some outputTypes else None
        finalValue

    and private selectCallOutputs context state environment requestedName receiverType arguments callSpan =
        let candidates = candidatesFor context requestedName
        if List.isEmpty candidates then
            let code = if receiverType.IsSome then "FLOW_UNKNOWN_DOT_STAGE" else "FLOW_UNKNOWN_CALL"
            fail code $"No word matches '{requestedName}'." None (Some callSpan) [] [ requestedName ]
        let attempts = candidates |> List.map (fun candidate -> candidate, mapArguments context state environment candidate receiverType arguments callSpan)
        let successful = attempts |> List.choose (fun (candidate, result) -> result |> Result.toOption |> Option.map (fun (bound, outputs) -> candidate, bound, outputs))
        match successful with
        | [ candidate, bound, outputs ] -> candidate, bound, outputs
        | [] when candidates.Length = 1 ->
            match attempts.Head with
            | _, Error problem -> raise (LanguageException { problem with Word = Some candidates.Head.Name })
            | _ -> fail "FLOW_NO_MATCHING_CALL" $"Arguments do not match '{requestedName}'." None (Some callSpan) [] [ requestedName ]
        | [] when receiverType.IsSome -> fail "FLOW_NO_MATCHING_DOT_STAGE" $"No '{requestedName}' stage accepts this receiver and its arguments." None (Some callSpan) (receiverType |> Option.map Types.format |> Option.toList) (candidates |> List.map (fun candidate -> candidate.Name))
        | [] -> fail "FLOW_NO_MATCHING_CALL" $"No overload of '{requestedName}' accepts these arguments." None (Some callSpan) [] (candidates |> List.map (fun candidate -> candidate.Name))
        | _ when receiverType.IsSome -> fail "FLOW_AMBIGUOUS_DOT_STAGE" $"Dot stage '{requestedName}' has more than one applicable first-input word; qualify the call explicitly." None (Some callSpan) [] (successful |> List.map (fun (candidate, _, _) -> candidate.Name))
        | _ -> fail "FLOW_AMBIGUOUS_CALL" $"Call '{requestedName}' matches more than one word; use namespace::word qualification." None (Some callSpan) [] (successful |> List.map (fun (candidate, _, _) -> candidate.Name))

    let private createOrigin state span =
        rememberSpan state span
        span

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
            match callbackReferenceIn arguments with
            | Some reference -> rejectWordReferenceContext reference
            | None -> ()
            let candidate, bound, _ = selectCallOutputs context state environment name None arguments callSpan
            lowerResolvedCall context state environment candidate bound None arguments callSpan
        | FlowExpression.DotCall(receiver, stage, arguments, callSpan) ->
            match listCallbackOperation stage, arguments with
            | Some operation, [ FlowArgument.WordReference reference ] ->
                validateWordReferenceShape state reference
                let receiverType = inferExpression context state environment receiver
                match receiverType with
                | TList itemType ->
                    let candidate, _ = resolveListCallback context state reference itemType (callbackRequiredOutput operation)
                    let receiverCode = lowerFlowExpression context state environment receiver
                    let listOperation =
                        match operation with
                        | ListCallbackOperation.Map -> MapList(candidate.Name, reference.Span)
                        | ListCallbackOperation.Filter -> FilterList(candidate.Name, reference.Span)
                        | ListCallbackOperation.Each -> EachList(candidate.Name, reference.Span)
                    rememberSpan state reference.Span
                    receiverCode @ [ listOperation ]
                | actual -> fail "FLOW_CALLBACK_REQUIRES_LIST" $"Static '{stage}' callback stages require a List<T> receiver." None (Some reference.Span) [ "List<T>" ] [ Types.format actual ]
            | _, _ when Option.isSome (callbackReferenceIn arguments) ->
                match callbackReferenceIn arguments with
                | Some reference when listCallbackOperation stage |> Option.isSome ->
                    fail "FLOW_CALLBACK_ARGUMENT_ARITY" $"Static '{stage}' callback syntax requires exactly one positional word reference." None (Some reference.Span) [ "one callback reference" ] [ string arguments.Length ]
                | Some reference -> rejectWordReferenceContext reference
                | None -> failwith "unreachable"
            | _ ->
                let receiverType = inferExpression context state environment receiver
                let candidate, bound, _ = selectCallOutputs context state environment stage (Some receiverType) arguments callSpan
                lowerResolvedCall context state environment candidate bound (Some receiver) arguments callSpan
        | FlowExpression.Container(kind, typeArguments, payload, constructorSpan) ->
            inferExpression context state environment expression |> ignore
            let payloadCode = payload |> Option.map (lowerFlowExpression context state environment) |> Option.defaultValue []
            payloadCode @ [ ConstructContainer(coreConstructor kind, typeArguments |> List.map (fun argument -> argument.Type), constructorSpan) ]
        | FlowExpression.MatchOption(scrutinee, someCase, noneCase, matchSpan) ->
            inferOutputs context state environment expression |> ignore
            let someType = inferExpression context state environment scrutinee |> function | TOption item -> item | _ -> failwith "validated option match changed type"
            let someEnvironment = Map.add someCase.Name { InternalName = someCase.Name; Type = someType } environment
            let someCode, _ = lowerStatements context state someEnvironment someCase.Statements
            let noneCode, _ = lowerStatements context state environment noneCase.Statements
            let someScope = Scope(someCode, syntheticSpan state someCase.Span)
            let noneScope = Scope(noneCode, syntheticSpan state noneCase.Span)
            lowerFlowExpression context state environment scrutinee @ [ MatchOption(someCase.Name, [ someScope ], [ noneScope ], matchSpan) ]
        | FlowExpression.MatchResult(scrutinee, okCase, errorCase, matchSpan) ->
            inferOutputs context state environment expression |> ignore
            let okType, errorType =
                match inferExpression context state environment scrutinee with
                | TResult(okValue, errorValue) -> okValue, errorValue
                | _ -> failwith "validated result match changed type"
            let okEnvironment = Map.add okCase.Name { InternalName = okCase.Name; Type = okType } environment
            let errorEnvironment = Map.add errorCase.Name { InternalName = errorCase.Name; Type = errorType } environment
            let okCode, _ = lowerStatements context state okEnvironment okCase.Statements
            let errorCode, _ = lowerStatements context state errorEnvironment errorCase.Statements
            let okScope = Scope(okCode, syntheticSpan state okCase.Span)
            let errorScope = Scope(errorCode, syntheticSpan state errorCase.Span)
            lowerFlowExpression context state environment scrutinee @ [ MatchResult(okCase.Name, errorCase.Name, [ okScope ], [ errorScope ], matchSpan) ]
        | FlowExpression.If(condition, thenStatements, elseStatements, ifSpan) ->
            inferOutputs context state environment expression |> ignore
            let conditionType = inferExpression context state environment condition
            if conditionType <> TBool then fail "FLOW_IF_CONDITION_TYPE" "Flow if condition must have type Bool." None (Some(spanOfExpression condition)) [ "Bool" ] [ Types.format conditionType ]
            let thenCode, _ = lowerStatements context state environment thenStatements
            let elseCode, _ = lowerStatements context state environment elseStatements
            let thenScope = Scope(thenCode, syntheticSpan state ifSpan)
            let elseScope = Scope(elseCode, syntheticSpan state ifSpan)
            lowerFlowExpression context state environment condition @ [ If([ thenScope ], [ elseScope ], ifSpan) ]

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
                    | _ -> -1
                | FlowArgument.WordReference reference -> rejectWordReferenceContext reference)
        let targetName = candidate.Name
        if not bound.HasNamedArguments then
            let receiverCode = receiver |> Option.map (lowerFlowExpression context state environment) |> Option.defaultValue []
            let explicitCode =
                arguments
                |> List.collect (function
                    | FlowArgument.Positional value | FlowArgument.Named(_, value, _) -> lowerFlowExpression context state environment value
                    | FlowArgument.WordReference reference -> rejectWordReferenceContext reference)
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
                let value =
                    match argument with
                    | FlowArgument.Positional value | FlowArgument.Named(_, value, _) -> value
                    | FlowArgument.WordReference reference -> rejectWordReferenceContext reference
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
            | FlowStatement.LetMany(bindings, value, statementSpan) ->
                rememberSpan state statementSpan
                let valueTypes = inferOutputs context state environment value
                if bindings.Length <> valueTypes.Length then
                    fail "FLOW_DESTRUCTURE_ARITY" "A destructuring binding must name every output exactly once." None (Some statementSpan) [ string valueTypes.Length ] [ string bindings.Length ]
                for name, nameSpan in bindings do
                    rememberSpan state nameSpan
                    if environment.ContainsKey name then fail "FLOW_LOCAL_REBOUND" $"Immutable local '{name}' is already declared in this lexical scope." None (Some nameSpan) [] [ name ]
                output.AddRange(lowerFlowExpression context state environment value)
                for (name, nameSpan), valueType in List.zip (List.rev bindings) (List.rev valueTypes) do
                    output.Add(Let(name, nameSpan))
                    environment <- Map.add name { InternalName = name; Type = valueType } environment
            | FlowStatement.Evaluate expression ->
                inferExpression context state environment expression |> ignore
                let code = lowerFlowExpression context state environment expression
                output.AddRange code
                if index < statements.Length - 1 then
                    let origin = spanOfExpression expression
                    let temporary, tempSpan = freshTemporary state origin
                    let scopeSpan = syntheticSpan state origin
                    output.Add(Scope([ Let(temporary, tempSpan) ], scopeSpan))
            | FlowStatement.Return(values, statementSpan) ->
                rememberSpan state statementSpan
                for value in values do inferExpression context state environment value |> ignore
                output.AddRange(values |> List.collect (lowerFlowExpression context state environment))
        List.ofSeq output, environment

    let private makeProjection (state: LoweringState) : FlowSourceProjection =
        { AuthoredSpans = state.AuthoredSpans
          SyntheticOrigins = state.SyntheticOrigins }

    let private mergeOrigins (context: Context) (projection: FlowSourceProjection) : Map<SourceSpan, SourceSpan> =
        let collision = projection.SyntheticOrigins |> Map.toSeq |> Seq.tryFind (fun (marker, _) -> context.SourceOrigins.ContainsKey marker)
        match collision with
        | Some _ -> fail "FLOW_SOURCE_ORIGIN_COLLISION" "Internal Flow source markers must remain unique across one compiler snapshot." None None [] [ "marker collision" ]
        | None -> Map.fold (fun found marker origin -> Map.add marker origin found) context.SourceOrigins projection.SyntheticOrigins

    let private remapAttachmentDiagnostic (sourceOrigins: Map<SourceSpan, SourceSpan>) action =
        try action ()
        with
        | LanguageException diagnostic ->
            match diagnostic.Span |> Option.bind (fun source -> sourceOrigins.TryFind source) with
            | Some authored -> raise (LanguageException { diagnostic with Span = Some authored })
            | None -> raise (LanguageException diagnostic)

    let private sourceSites (sourceMap: Map<SourceSiteId, IrSourceSite>) =
        sourceMap |> Map.map (fun _ source -> source.SiteSpan)

    let private lowerExpressionBody context expression : FlowLoweredExpression =
        let state = freshState context.SourceOrigins
        inferExpression context state Map.empty expression |> ignore
        let lowered = lowerFlowExpression context state Map.empty expression
        { Expressions = lowered
          SourceText = FlowSource.renderExpression expression
          SyntaxVersion = 1
          Projection = makeProjection state }

    let lowerExpression context expression =
        FlowStructure.validateExpressionNesting [ expression ]
        lowerExpressionBody context expression

    let checkExpression context expression =
        let lowered = lowerExpression context expression
        let checkedExpression = Compiler.checkExpression (knownTypes context) context.CompilerContext.Words lowered.Expressions
        lowered, checkedExpression

    let compileExpression context expression : CompiledExpression =
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

    let lowerWord context (flowWord: FlowWordDefinition) : FlowLoweredWord =
        FlowStructure.validateWordNesting flowWord
        if flowWord.SyntaxVersion <> 1 then fail "FLOW_VERSION_UNSUPPORTED" "Only Flow syntax version 1 is supported by this compiler slice." (Some flowWord.Name) (Some flowWord.Span) [ "1" ] [ string flowWord.SyntaxVersion ]
        let names = flowWord.Parameters |> List.map (fun parameter -> parameter.Name)
        if names.Length <> (Set.ofList names).Count then fail "FLOW_DUPLICATE_PARAMETER" "Flow word parameters must have unique names." (Some flowWord.Name) (Some flowWord.Span) [] names
        let state = freshState context.SourceOrigins
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
              Outputs = flowWord.Outputs
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

    let compileWord context wordId flowWord : CompiledWord =
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

    let private requireAttachmentVersion kind word span version =
        if version <> 1 then fail "FLOW_VERSION_UNSUPPORTED" $"Only Flow syntax version 1 is supported for Flow {kind} attachments." (Some word) (Some span) [ "1" ] [ string version ]

    let lowerTest (context: Context) (flowTest: FlowTestDefinition) : FlowLoweredTest =
        FlowStructure.validateTestNesting flowTest
        requireAttachmentVersion "test" flowTest.Word flowTest.Span flowTest.SyntaxVersion
        let state = freshState context.SourceOrigins
        rememberSpan state flowTest.Span
        rememberSpan state flowTest.HeaderSpan
        rememberSpan state flowTest.ExpectationSpan
        let body, _ = lowerStatements context state Map.empty flowTest.Body
        let expected =
            match flowTest.Expected with
            | FlowTestExpectation.Literal(literal, literalSpan) ->
                rememberSpan state literalSpan
                ExpectedValue literal
            | FlowTestExpectation.RuntimeError(code, codeSpan) ->
                rememberSpan state codeSpan
                ExpectedRuntimeError code
            | FlowTestExpectation.Expression expression ->
                inferExpression context state Map.empty expression |> ignore
                ExpectedExpression(lowerFlowExpression context state Map.empty expression)
        let definition: TestDefinition =
            { Name = flowTest.CaseName
              Word = flowTest.Word
              Body = body
              Expected = expected
              SourceText = flowTest.SourceText
              Span = flowTest.Span }
        let projection = makeProjection state
        let origins = mergeOrigins context projection
        remapAttachmentDiagnostic origins (fun () -> Compiler.checkTest (knownTypes context) context.CompilerContext.Words definition |> ignore)
        { Definition = definition
          SourceText = flowTest.SourceText
          SyntaxVersion = flowTest.SyntaxVersion
          Projection = projection }

    let lowerExample (context: Context) (flowExample: FlowExampleDefinition) : FlowLoweredExample =
        FlowStructure.validateExampleNesting flowExample
        requireAttachmentVersion "example" flowExample.Word flowExample.Span flowExample.SyntaxVersion
        let state = freshState context.SourceOrigins
        rememberSpan state flowExample.Span
        rememberSpan state flowExample.HeaderSpan
        rememberSpan state flowExample.ExpectationSpan
        rememberSpan state flowExample.ExpectedSpan
        let body, _ = lowerStatements context state Map.empty flowExample.Body
        let definition: ExampleDefinition =
            { Name = flowExample.CaseName
              Word = flowExample.Word
              Body = body
              Expected = flowExample.Expected
              SourceText = flowExample.SourceText
              Span = flowExample.Span }
        let projection = makeProjection state
        let origins = mergeOrigins context projection
        remapAttachmentDiagnostic origins (fun () -> Compiler.checkExample (knownTypes context) context.CompilerContext.Words definition |> ignore)
        { Definition = definition
          SourceText = flowExample.SourceText
          SyntaxVersion = flowExample.SyntaxVersion
          Projection = projection }

    let compileTest (context: Context) (verifiedProgram: VerifiedIrProgram) (flowTest: FlowTestDefinition) : CompiledTest =
        let lowered = lowerTest context flowTest
        let origins = mergeOrigins context lowered.Projection
        let body, expectationBody =
            Compiler.compileIrTestWithExpectationAgainstProgramWithSourceOrigins
                context.CompilerContext verifiedProgram lowered.Definition origins
        let bodySources = VerifiedIrBody.inspect body |> fun value -> value.BodySourceMap |> sourceSites
        let expectationSources =
            expectationBody
            |> Option.map (VerifiedIrBody.inspect >> fun value -> value.BodySourceMap |> sourceSites)
        { Lowered = lowered
          Program = verifiedProgram
          Body = body
          ExpectationBody = expectationBody
          BodySiteOrigins = bodySources
          ExpectationSiteOrigins = expectationSources }

    let compileExample (context: Context) (verifiedProgram: VerifiedIrProgram) (flowExample: FlowExampleDefinition) : CompiledExample =
        let lowered = lowerExample context flowExample
        let origins = mergeOrigins context lowered.Projection
        let body =
            Compiler.compileIrExampleAgainstProgramWithSourceOrigins
                context.CompilerContext verifiedProgram lowered.Definition origins
        let sites = VerifiedIrBody.inspect body |> fun value -> value.BodySourceMap |> sourceSites
        { Lowered = lowered
          Program = verifiedProgram
          Body = body
          SiteOrigins = sites }

    let parameterCatalog (definitions: FlowWordDefinition list) =
        definitions
        |> List.map (fun definition -> definition.Name, (definition.Parameters |> List.map (fun parameter -> parameter.Name)))
        |> Map.ofList
