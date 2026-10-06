namespace AgentLang

open System
open System.Collections.Generic
open System.Globalization
open System.Text.Json

/// Parser for the explicit Flow frontend. This entry point never falls back to
/// the legacy stack parser after a Flow error.
module FlowParser =
    type private TokenKind = Identifier | Number | StringLiteral | Symbol

    type private Token =
        { Kind: TokenKind
          Text: string
          Offset: int
          Line: int
          Column: int }

    type private State =
        { File: string
          Source: string
          Tokens: Token array
          mutable Index: int
          mutable Depth: int
          EndLine: int
          EndColumn: int }

    let private expressionSpan = function
        | FlowExpression.Literal(_, span)
        | FlowExpression.Local(_, span)
        | FlowExpression.Call(_, _, span)
        | FlowExpression.RootCall(_, _, span)
        | FlowExpression.DotCall(_, _, _, span)
        | FlowExpression.If(_, _, _, span)
        | FlowExpression.Container(_, _, _, span)
        | FlowExpression.MatchOption(_, _, _, span)
        | FlowExpression.MatchResult(_, _, _, span) -> span

    let private maxSourceLength = 1_000_000
    let private maxTokens = 100_000
    let private maxNesting = FlowStructure.maxExpressionDepth
    let private maxStringLength = 100_000

    let private diagnostic code message file line column length =
        { Code = code
          Message = message
          Word = None
          Span = Some { File = file; Line = max 1 line; Column = max 1 column; Length = max 1 length }
          Expected = []
          Actual = [] }

    let private fail file line column length code message =
        raise (LanguageException(diagnostic code message file line column length))

    let private tokenize file (source: string) =
        if isNull source || source.Length > maxSourceLength then
            fail file 1 1 1 "FLOW_SOURCE_LIMIT" $"Flow source must be non-null and at most {maxSourceLength} UTF-16 code units."
        let tokens = ResizeArray<Token>()
        let mutable offset = 0
        let mutable line = 1
        let mutable column = 1
        let advance () =
            if offset < source.Length then
                if source[offset] = '\r' then
                    offset <- offset + 1
                    if offset < source.Length && source[offset] = '\n' then offset <- offset + 1
                    line <- line + 1
                    column <- 1
                elif source[offset] = '\n' then
                    offset <- offset + 1
                    line <- line + 1
                    column <- 1
                else
                    offset <- offset + 1
                    column <- column + 1
        let add kind startOffset startLine startColumn =
            if tokens.Count >= maxTokens then
                fail file startLine startColumn 1 "FLOW_TOKEN_LIMIT" $"Flow source exceeds the {maxTokens} token limit."
            tokens.Add
                { Kind = kind
                  Text = source.Substring(startOffset, offset - startOffset)
                  Offset = startOffset
                  Line = startLine
                  Column = startColumn }
        let isIdentifierStart ch = Char.IsLetter ch || ch = '_'
        let isIdentifierPart ch = Char.IsLetterOrDigit ch || ch = '_' || ch = '-' || ch = '?' || ch = '!'
        while offset < source.Length do
            if Char.IsWhiteSpace source[offset] then advance ()
            elif source[offset] = '#' || (source[offset] = '/' && offset + 1 < source.Length && source[offset + 1] = '/') then
                while offset < source.Length && source[offset] <> '\r' && source[offset] <> '\n' do advance ()
            else
                let startOffset, startLine, startColumn = offset, line, column
                if source[offset] = '"' then
                    advance ()
                    let mutable escaped = false
                    let mutable closed = false
                    while offset < source.Length && not closed do
                        let ch = source[offset]
                        if escaped then
                            escaped <- false
                            advance ()
                        elif ch = '\\' then
                            escaped <- true
                            advance ()
                        elif ch = '"' then
                            closed <- true
                            advance ()
                        elif ch = '\r' || ch = '\n' then
                            fail file startLine startColumn (offset - startOffset + 1) "FLOW_STRING_NEWLINE" "A quoted Flow string cannot contain a raw newline."
                        else advance ()
                    if not closed then fail file startLine startColumn (max 1 (offset - startOffset)) "FLOW_INCOMPLETE_INPUT" "String literal is missing its closing quote."
                    let value = source.Substring(startOffset, offset - startOffset)
                    if value.Length > maxStringLength then fail file startLine startColumn 1 "FLOW_STRING_LIMIT" $"String literal exceeds the {maxStringLength} character limit."
                    try JsonSerializer.Deserialize<string>(value) |> ignore
                    with _ -> fail file startLine startColumn value.Length "FLOW_INVALID_STRING" "String literal must use JSON-style quoted text."
                    add StringLiteral startOffset startLine startColumn
                elif isIdentifierStart source[offset] then
                    advance ()
                    while offset < source.Length && isIdentifierPart source[offset] do advance ()
                    add Identifier startOffset startLine startColumn
                elif Char.IsDigit source[offset]
                     || ((source[offset] = '-' || source[offset] = '+') && offset + 1 < source.Length && (Char.IsDigit source[offset + 1] || source[offset + 1] = '.'))
                     || (source[offset] = '.' && offset + 1 < source.Length && Char.IsDigit source[offset + 1]) then
                    if source[offset] = '-' || source[offset] = '+' then advance ()
                    while offset < source.Length && Char.IsDigit source[offset] do advance ()
                    if offset + 1 < source.Length && source[offset] = '.' && Char.IsDigit source[offset + 1] then
                        advance ()
                        while offset < source.Length && Char.IsDigit source[offset] do advance ()
                    if offset < source.Length && (source[offset] = 'e' || source[offset] = 'E') then
                        advance ()
                        if offset < source.Length && (source[offset] = '+' || source[offset] = '-') then advance ()
                        let exponentStart = offset
                        while offset < source.Length && Char.IsDigit source[offset] do advance ()
                        if exponentStart = offset then fail file startLine startColumn (offset - startOffset) "FLOW_INVALID_FLOAT" "Float exponent must contain at least one digit."
                    add Number startOffset startLine startColumn
                else
                    let pair = if offset + 1 < source.Length then source.Substring(offset, 2) else ""
                    let symbol =
                        match pair with
                        | "->" | "::" | "=>" -> pair
                        | _ -> string source[offset]
                    for _ in symbol do advance ()
                    add Symbol startOffset startLine startColumn
        tokens.ToArray(), line, column

    let private current (state: State) =
        if state.Index < state.Tokens.Length then Some state.Tokens[state.Index] else None

    let private peek (state: State) = current state |> Option.map (fun token -> token.Text)

    let private atEnd state = state.Index >= state.Tokens.Length

    let private previous (state: State) =
        if state.Index > 0 then Some state.Tokens[state.Index - 1] else None

    let private consume state =
        match current state with
        | Some token -> state.Index <- state.Index + 1; token
        | None -> fail state.File state.EndLine state.EndColumn 1 "FLOW_INCOMPLETE_INPUT" "Unexpected end of Flow source."

    let private tokenError (state: State) code message =
        match current state with
        | Some token -> fail state.File token.Line token.Column token.Text.Length code message
        | None -> fail state.File state.EndLine state.EndColumn 1 code message

    let private expect state expected =
        match current state with
        | Some token when token.Text = expected -> consume state
        | Some token -> fail state.File token.Line token.Column token.Text.Length "FLOW_EXPECTED_TOKEN" $"Expected '{expected}', found '{token.Text}'."
        | None -> tokenError state "FLOW_INCOMPLETE_INPUT" $"Expected '{expected}' before end of input."

    let private accept state expected =
        if peek state = Some expected then consume state |> ignore; true else false

    let private expectIdentifier state =
        match current state with
        | Some token when token.Kind = Identifier -> consume state
        | Some token -> fail state.File token.Line token.Column token.Text.Length "FLOW_EXPECTED_NAME" $"Expected an identifier, found '{token.Text}'."
        | None -> tokenError state "FLOW_INCOMPLETE_INPUT" "Expected an identifier before end of input."

    let private sourceSpan file startToken endToken =
        let length =
            match endToken with
            | Some last -> last.Offset + last.Text.Length - startToken.Offset
            | None -> startToken.Text.Length
        { File = file; Line = startToken.Line; Column = startToken.Column; Length = max 1 length }

    let private sourceSlice (state: State) (startToken: Token) (endToken: Token) =
        let startOffset = startToken.Offset
        let endOffset = endToken.Offset + endToken.Text.Length
        state.Source.Substring(startOffset, endOffset - startOffset)

    let private withDepth (state: State) action =
        state.Depth <- state.Depth + 1
        if state.Depth > maxNesting then
            state.Depth <- state.Depth - 1
            tokenError state "FLOW_NESTING_LIMIT" $"Flow syntax exceeds the nesting limit of {maxNesting}."
        try action ()
        finally state.Depth <- state.Depth - 1

    let rec private parseType state =
        withDepth state (fun () ->
            let nameToken = expectIdentifier state
            let arguments =
                if accept state "<" then
                    let values = ResizeArray<LangType>()
                    values.Add(parseType state)
                    while accept state "," do values.Add(parseType state)
                    expect state ">" |> ignore
                    Some(List.ofSeq values)
                else None
            match nameToken.Text, arguments with
            | "Int", None -> TInt
            | "Float", None -> TFloat
            | "Bool", None -> TBool
            | "String", None -> TString
            | "Unit", None -> TUnit
            | "List", Some [ item ] -> TList item
            | "Option", Some [ item ] -> TOption item
            | "Result", Some [ ok; error ] -> TResult(ok, error)
            | name, None -> TNamed name
            | ("List" | "Option" | "Result"), Some values ->
                fail state.File nameToken.Line nameToken.Column nameToken.Text.Length "FLOW_TYPE_ARITY" $"Type '{nameToken.Text}' has the wrong number of type arguments ({values.Length})."
            | name, Some _ ->
                fail state.File nameToken.Line nameToken.Column nameToken.Text.Length "FLOW_TYPE_NOT_PARAMETERIZED" $"Named type '{name}' cannot take type arguments in this frontend slice.")

    let private parseWordName state =
        let first = expectIdentifier state
        let parts = ResizeArray<string>()
        parts.Add first.Text
        while accept state "." do parts.Add((expectIdentifier state).Text)
        String.concat "." parts, sourceSpan state.File first (previous state)

    let private parseNamespaceName state =
        let first = expectIdentifier state
        let parts = ResizeArray<string>()
        parts.Add first.Text
        while accept state "::" do parts.Add((expectIdentifier state).Text)
        String.concat "." parts

    let private parseRootTarget state =
        let rootPrefix = expect state "::"
        let name = expectIdentifier state
        if peek state = Some "::" then
            fail state.File rootPrefix.Line rootPrefix.Column (name.Offset + name.Text.Length - rootPrefix.Offset)
                "FLOW_ROOT_TARGET_QUALIFIED" "An absolute-root spelling selects one unqualified dictionary key; do not append namespace segments."
        { Name = name.Text
          Span = sourceSpan state.File rootPrefix (Some name) }

    let private constructorKind = function
        | "list.empty" -> Some FlowContainerConstructor.ListEmpty
        | "list.singleton" -> Some FlowContainerConstructor.ListSingleton
        | "option.none" -> Some FlowContainerConstructor.OptionNone
        | "option.some" -> Some FlowContainerConstructor.OptionSome
        | "result.ok" -> Some FlowContainerConstructor.ResultOk
        | "result.error" -> Some FlowContainerConstructor.ResultError
        | _ -> None

    let private constructorTypeArity = function
        | FlowContainerConstructor.ResultOk | FlowContainerConstructor.ResultError -> 2
        | _ -> 1

    let private constructorHasPayload = function
        | FlowContainerConstructor.ListEmpty | FlowContainerConstructor.OptionNone -> false
        | FlowContainerConstructor.ListSingleton | FlowContainerConstructor.OptionSome
        | FlowContainerConstructor.ResultOk | FlowContainerConstructor.ResultError -> true

    let private callbackStage = function
        | "map" | "filter" | "each" -> true
        | _ -> false

    let private currentIs state index text =
        index >= 0 && index < state.Tokens.Length && state.Tokens[index].Text = text

    let private isArgumentTerminator state index =
        currentIs state index ")" || currentIs state index ","

    let private explicitShortReferenceAhead (state: State) =
        state.Index + 1 < state.Tokens.Length
        && state.Tokens[state.Index].Kind = Identifier
        && state.Tokens[state.Index].Text = "word"
        && state.Tokens[state.Index + 1].Kind = Identifier
        && (state.Index + 2 = state.Tokens.Length || isArgumentTerminator state (state.Index + 2))

    let private qualifiedReferenceAhead (state: State) =
        if state.Index >= state.Tokens.Length || state.Tokens[state.Index].Kind <> Identifier then false
        else
            let mutable cursor = state.Index + 1
            let mutable qualified = false
            let mutable scanning = true
            while scanning && cursor < state.Tokens.Length do
                if state.Tokens[cursor].Text = "::"
                   && cursor + 1 < state.Tokens.Length
                   && state.Tokens[cursor + 1].Kind = Identifier then
                    qualified <- true
                    cursor <- cursor + 2
                else scanning <- false
            qualified && (cursor = state.Tokens.Length || isArgumentTerminator state cursor)

    let private absoluteRootReferenceAhead (state: State) =
        state.Index + 1 < state.Tokens.Length
        && state.Tokens[state.Index].Text = "::"
        && state.Tokens[state.Index + 1].Kind = Identifier
        && (state.Index + 2 = state.Tokens.Length || isArgumentTerminator state (state.Index + 2))

    let private parseStaticWordReference (state: State) qualification =
        let first =
            match qualification with
            | FlowWordReferenceQualification.ExplicitShort -> expect state "word"
            | FlowWordReferenceQualification.NamespaceQualified ->
                current state |> Option.defaultWith (fun () -> tokenError state "FLOW_INCOMPLETE_INPUT" "Expected a qualified callback word reference.")
            | FlowWordReferenceQualification.AbsoluteRoot ->
                current state |> Option.defaultWith (fun () -> tokenError state "FLOW_INCOMPLETE_INPUT" "Expected an absolute-root callback reference.")
        let name, referenceSpan =
            match qualification with
            | FlowWordReferenceQualification.AbsoluteRoot ->
                let target = parseRootTarget state
                target.Name, target.Span
            | FlowWordReferenceQualification.ExplicitShort
            | FlowWordReferenceQualification.NamespaceQualified ->
                let name = parseNamespaceName state
                name, sourceSpan state.File first (previous state)
        if qualification = FlowWordReferenceQualification.ExplicitShort && name.Contains('.') then
            fail state.File first.Line first.Column (max first.Text.Length (previous state |> Option.map (fun token -> token.Offset + token.Text.Length - first.Offset) |> Option.defaultValue first.Text.Length))
                "FLOW_CALLBACK_SHORT_REFERENCE_QUALIFIED" "The `word` marker is for short callback names; use a bare qualified reference for namespaced words."
        if qualification = FlowWordReferenceQualification.NamespaceQualified && not (name.Contains('.')) then
            fail state.File first.Line first.Column first.Text.Length "FLOW_CALLBACK_REFERENCE_QUALIFIED" "A bare callback reference must be namespace-qualified; use `word name` for a short name."
        { Name = name
          Qualification = qualification
          Span = referenceSpan }

    let rec private parseArguments state allowStaticWordReferences =
        withDepth state (fun () ->
            expect state "(" |> ignore
            let values = ResizeArray<FlowArgument>()
            if not (accept state ")") then
                let mutable more = true
                while more do
                    match current state, state.Index + 1 < state.Tokens.Length && state.Tokens[state.Index + 1].Text = "=" with
                    | Some name, true when name.Kind = Identifier ->
                        consume state |> ignore
                        expect state "=" |> ignore
                        if allowStaticWordReferences && (explicitShortReferenceAhead state || qualifiedReferenceAhead state || absoluteRootReferenceAhead state) then
                            fail state.File name.Line name.Column name.Text.Length "FLOW_CALLBACK_NAMED_REFERENCE" "A static list callback must be one positional word reference."
                        let expression = parseExpressionState state
                        values.Add(FlowArgument.Named(name.Text, expression, sourceSpan state.File name (previous state)))
                    | _ when allowStaticWordReferences && explicitShortReferenceAhead state ->
                        values.Add(FlowArgument.WordReference(parseStaticWordReference state FlowWordReferenceQualification.ExplicitShort))
                    | _ when allowStaticWordReferences && qualifiedReferenceAhead state ->
                        values.Add(FlowArgument.WordReference(parseStaticWordReference state FlowWordReferenceQualification.NamespaceQualified))
                    | _ when allowStaticWordReferences && absoluteRootReferenceAhead state ->
                        values.Add(FlowArgument.WordReference(parseStaticWordReference state FlowWordReferenceQualification.AbsoluteRoot))
                    | _ -> values.Add(FlowArgument.Positional(parseExpressionState state))
                    if accept state "," then () else more <- false
                let hasReference = values |> Seq.exists (function FlowArgument.WordReference _ -> true | _ -> false)
                if hasReference && values.Count <> 1 then
                    let referenceSpan = values |> Seq.pick (function FlowArgument.WordReference reference -> Some reference.Span | _ -> None)
                    fail state.File referenceSpan.Line referenceSpan.Column referenceSpan.Length "FLOW_CALLBACK_ARGUMENT_ARITY" "A static list callback must be the only positional argument."
                expect state ")" |> ignore
            List.ofSeq values)

    and private parseBlock state =
        withDepth state (fun () ->
            expect state "{" |> ignore
            let statements = parseBlockStatements state
            expect state "}" |> ignore
            statements)

    and private parseBlockStatements state =
        parseBlockStatementsUntil state (Set.singleton "}")

    and private parseBlockStatementsUntil state terminators =
        let statements = ResizeArray<FlowStatement>()
        let isTerminator () =
            match current state with
            | Some token -> Set.contains token.Text terminators
            | None -> false
        while not (isTerminator ()) && not (atEnd state) do
            let statement = parseStatement state
            statements.Add statement
            if accept state ";" then ()
            else
                match current state, previous state with
                | Some next, Some last when not (Set.contains next.Text terminators) && next.Line <= last.Line ->
                    fail state.File next.Line next.Column next.Text.Length "FLOW_EXPECTED_SEPARATOR" "Statements on one line must be separated by ';'."
                | _ -> ()
        let values = List.ofSeq statements
        values
        |> List.indexed
        |> List.iter (fun (index, statement) ->
            match statement with
            | FlowStatement.Return(_, span) when index <> values.Length - 1 ->
                fail span.File span.Line span.Column span.Length "FLOW_RETURN_NOT_TERMINAL" "A return vector must be the final statement in its lexical block."
            | _ -> ())
        values

    and private parseStatement state =
        let start = current state |> Option.defaultWith (fun () -> tokenError state "FLOW_INCOMPLETE_INPUT" "Expected a Flow statement.")
        if accept state "let" then
            if accept state "(" then
                let bindings = ResizeArray<string * SourceSpan>()
                if peek state = Some ")" then
                    tokenError state "FLOW_DESTRUCTURE_EMPTY" "A destructuring binding must name every output."
                let mutable more = true
                while more do
                    let name = expectIdentifier state
                    bindings.Add(name.Text, sourceSpan state.File name (Some name))
                    if accept state "," then
                        if peek state = Some ")" then tokenError state "FLOW_DESTRUCTURE_ARITY" "A destructuring pattern cannot end with a missing name."
                    else more <- false
                expect state ")" |> ignore
                let names = bindings |> Seq.map fst |> Seq.toList
                if names |> List.exists ((=) "_") then
                    let name, nameSpan = bindings |> Seq.find (fun (name, _) -> name = "_")
                    fail state.File nameSpan.Line nameSpan.Column nameSpan.Length "FLOW_DESTRUCTURE_NAME" "'_' is not a discard pattern; every output must have a named local."
                match names |> List.countBy id |> List.tryFind (fun (_, count) -> count > 1) with
                | Some(duplicate, _) ->
                    let _, duplicateSpan = bindings |> Seq.find (fun (name, _) -> name = duplicate)
                    fail state.File duplicateSpan.Line duplicateSpan.Column duplicateSpan.Length "FLOW_DESTRUCTURE_DUPLICATE" $"Destructuring local '{duplicate}' is repeated."
                | None -> ()
                expect state "=" |> ignore
                let value = parseExpressionState state
                FlowStatement.LetMany(List.ofSeq bindings, value, sourceSpan state.File start (previous state))
            else
                let name = expectIdentifier state
                expect state "=" |> ignore
                let value = parseExpressionState state
                FlowStatement.Let(name.Text, value, sourceSpan state.File start (previous state))
        elif accept state "return" then
            let values = ResizeArray<FlowExpression>()
            if accept state "(" then
                if peek state = Some ")" then
                    tokenError state "FLOW_RETURN_EMPTY" "A return vector must contain at least one scalar expression."
                let mutable more = true
                while more do
                    values.Add(parseExpressionState state)
                    if accept state "," then
                        if peek state = Some ")" then tokenError state "FLOW_RETURN_VALUE_REQUIRED" "A return vector cannot end with a missing value."
                    else more <- false
                expect state ")" |> ignore
            else values.Add(parseExpressionState state)
            FlowStatement.Return(List.ofSeq values, sourceSpan state.File start (previous state))
        else FlowStatement.Evaluate(parseExpressionState state)

    and private parseConstructor state startToken kind =
        if not (accept state "<") then
            fail state.File startToken.Line startToken.Column startToken.Text.Length "FLOW_CONSTRUCTOR_TYPE_ARGUMENTS_REQUIRED" "Container constructors require explicit closed type arguments."
        if peek state = Some ">" then
            tokenError state "FLOW_CONSTRUCTOR_TYPE_ARGUMENTS_REQUIRED" "Container constructors require one or more explicit type arguments."
        let typeArguments = ResizeArray<FlowTypeArgument>()
        let parseTypeArgument () =
            let typeStart = current state |> Option.defaultWith (fun () -> tokenError state "FLOW_INCOMPLETE_INPUT" "Expected a constructor type argument.")
            let typeValue = parseType state
            { Type = typeValue; Span = sourceSpan state.File typeStart (previous state) }
        typeArguments.Add(parseTypeArgument ())
        while accept state "," do typeArguments.Add(parseTypeArgument ())
        expect state ">" |> ignore
        let requiredTypeArity = constructorTypeArity kind
        if typeArguments.Count <> requiredTypeArity then
            fail state.File startToken.Line startToken.Column (max 1 (previous state |> Option.map (fun token -> token.Offset + token.Text.Length - startToken.Offset) |> Option.defaultValue startToken.Text.Length))
                "FLOW_CONSTRUCTOR_TYPE_ARITY" "Container constructor has the wrong number of explicit type arguments."
        if peek state <> Some "(" then
            tokenError state "FLOW_CONSTRUCTOR_CALL_REQUIRED" "Container constructors must be followed by a parenthesized payload list."
        let arguments = parseArguments state false
        let payload =
            match constructorHasPayload kind, arguments with
            | false, [] -> None
            | true, [ FlowArgument.Positional expression ] -> Some expression
            | _ ->
                let expected = if constructorHasPayload kind then "one positional payload" else "no payload"
                fail state.File startToken.Line startToken.Column startToken.Text.Length "FLOW_CONSTRUCTOR_ARITY" $"Container constructor expects {expected}; received {arguments.Length} argument(s)."
        FlowExpression.Container(kind, List.ofSeq typeArguments, payload, sourceSpan state.File startToken (previous state))

    and private parsePayloadCase state labelToken =
        let name = expectIdentifier state
        expect state "=>" |> ignore
        let statements = parseBlock state
        { Name = name.Text
          NameSpan = sourceSpan state.File name (Some name)
          Statements = statements
          Span = sourceSpan state.File labelToken (previous state) }

    and private parseBlockCase state labelToken =
        expect state "=>" |> ignore
        let statements = parseBlock state
        { Statements = statements
          Span = sourceSpan state.File labelToken (previous state) }

    and private parseMatch state matchToken =
        let scrutinee = parseExpressionState state
        expect state "{" |> ignore
        let mutable caseKind: string option = None
        let mutable someCase: FlowPayloadCase option = None
        let mutable noneCase: FlowCaseBlock option = None
        let mutable okCase: FlowPayloadCase option = None
        let mutable errorCase: FlowPayloadCase option = None
        let setCaseKind kind labelToken =
            match caseKind with
            | None -> caseKind <- Some kind
            | Some existing when existing = kind -> ()
            | Some _ -> fail state.File labelToken.Line labelToken.Column labelToken.Text.Length "FLOW_MATCH_CASE_KIND" "Option and Result case labels cannot be mixed in one match."
        while peek state <> Some "}" && not (atEnd state) do
            let label = expectIdentifier state
            match label.Text with
            | "some" ->
                setCaseKind "option" label
                if someCase.IsSome then fail state.File label.Line label.Column label.Text.Length "FLOW_MATCH_CASE_DUPLICATE" "Option match contains the 'some' case more than once."
                someCase <- Some(parsePayloadCase state label)
            | "none" ->
                setCaseKind "option" label
                if noneCase.IsSome then fail state.File label.Line label.Column label.Text.Length "FLOW_MATCH_CASE_DUPLICATE" "Option match contains the 'none' case more than once."
                noneCase <- Some(parseBlockCase state label)
            | "ok" ->
                setCaseKind "result" label
                if okCase.IsSome then fail state.File label.Line label.Column label.Text.Length "FLOW_MATCH_CASE_DUPLICATE" "Result match contains the 'ok' case more than once."
                okCase <- Some(parsePayloadCase state label)
            | "error" ->
                setCaseKind "result" label
                if errorCase.IsSome then fail state.File label.Line label.Column label.Text.Length "FLOW_MATCH_CASE_DUPLICATE" "Result match contains the 'error' case more than once."
                errorCase <- Some(parsePayloadCase state label)
            | _ -> fail state.File label.Line label.Column label.Text.Length "FLOW_MATCH_CASE_UNKNOWN" $"Unknown match case '{label.Text}'."
            accept state ";" |> ignore
        expect state "}" |> ignore
        let matchSpan = sourceSpan state.File matchToken (previous state)
        match someCase, noneCase, okCase, errorCase with
        | Some someValue, Some noneValue, None, None -> FlowExpression.MatchOption(scrutinee, someValue, noneValue, matchSpan)
        | None, None, Some okValue, Some errorValue -> FlowExpression.MatchResult(scrutinee, okValue, errorValue, matchSpan)
        | Some _, None, None, None | None, Some _, None, None ->
            fail state.File matchSpan.Line matchSpan.Column matchSpan.Length "FLOW_MATCH_CASE_MISSING" "Option matches require exactly one 'some' and one 'none' case."
        | None, None, Some _, None | None, None, None, Some _ ->
            fail state.File matchSpan.Line matchSpan.Column matchSpan.Length "FLOW_MATCH_CASE_MISSING" "Result matches require exactly one 'ok' and one 'error' case."
        | None, None, None, None ->
            fail state.File matchSpan.Line matchSpan.Column matchSpan.Length "FLOW_MATCH_CASE_MISSING" "A match must contain both cases for Option or Result."
        | _ ->
            fail state.File matchSpan.Line matchSpan.Column matchSpan.Length "FLOW_MATCH_CASE_KIND" "Match cases must form exactly the Option pair or the Result pair."

    and private parseExpressionState state =
        withDepth state (fun () ->
            let first = current state |> Option.defaultWith (fun () -> tokenError state "FLOW_INCOMPLETE_INPUT" "Expected a Flow expression.")
            let primary =
                match first.Kind, first.Text with
                | Number, _ ->
                    consume state |> ignore
                    match Int64.TryParse(first.Text, NumberStyles.Integer, CultureInfo.InvariantCulture) with
                    | true, value -> FlowExpression.Literal(LInt value, sourceSpan state.File first (Some first))
                    | _ when first.Text |> Seq.exists (fun ch -> ch = '.' || ch = 'e' || ch = 'E') ->
                        match Double.TryParse(first.Text, NumberStyles.Float, CultureInfo.InvariantCulture) with
                        | true, value when Double.IsFinite value -> FlowExpression.Literal(LFloat value, sourceSpan state.File first (Some first))
                        | true, _ -> fail state.File first.Line first.Column first.Text.Length "FLOW_NONFINITE_FLOAT" "Float literals must be finite."
                        | _ -> fail state.File first.Line first.Column first.Text.Length "FLOW_INVALID_FLOAT" "Numeric token is not a valid finite Int64 or Float literal."
                    | _ -> fail state.File first.Line first.Column first.Text.Length "FLOW_INTEGER_RANGE" "Integer literal is outside the Int64 range."
                | StringLiteral, _ ->
                    consume state |> ignore
                    let value = JsonSerializer.Deserialize<string>(first.Text)
                    FlowExpression.Literal(LString value, sourceSpan state.File first (Some first))
                | Identifier, "true" -> consume state |> ignore; FlowExpression.Literal(LBool true, sourceSpan state.File first (Some first))
                | Identifier, "false" -> consume state |> ignore; FlowExpression.Literal(LBool false, sourceSpan state.File first (Some first))
                | Identifier, "unit" -> consume state |> ignore; FlowExpression.Literal(LUnit, sourceSpan state.File first (Some first))
                | Identifier, "match" -> consume state |> ignore; parseMatch state first
                | Identifier, "word" when explicitShortReferenceAhead state ->
                    fail state.File first.Line first.Column first.Text.Length "FLOW_CALLBACK_REFERENCE_CONTEXT" "Short word references are allowed only as static list callback arguments; use `word name` inside `.map`, `.filter`, or `.each`."
                | Identifier, "if" ->
                    consume state |> ignore
                    let condition = parseExpressionState state
                    let thenBranch = parseBlock state
                    expect state "else" |> ignore
                    let elseBranch = parseBlock state
                    FlowExpression.If(condition, thenBranch, elseBranch, sourceSpan state.File first (previous state))
                | Symbol, "::" ->
                    let target = parseRootTarget state
                    if peek state <> Some "(" then
                        fail state.File first.Line first.Column (previous state |> Option.map (fun token -> token.Offset + token.Text.Length - first.Offset) |> Option.defaultValue first.Text.Length)
                            "FLOW_ROOT_CALL_REQUIRES_ARGUMENTS" "An absolute-root dictionary name must be called with parentheses."
                    let arguments = parseArguments state false
                    FlowExpression.RootCall(target, arguments, sourceSpan state.File first (previous state))
                | Identifier, _ ->
                    let name = parseNamespaceName state
                    match constructorKind name with
                    | Some kind -> parseConstructor state first kind
                    | None when peek state = Some "(" ->
                        let args = parseArguments state false
                        FlowExpression.Call(name, args, sourceSpan state.File first (previous state))
                    | None when name.Contains('.') ->
                        fail state.File first.Line first.Column first.Text.Length "FLOW_QUALIFIED_CALL_REQUIRES_ARGUMENTS" "A qualified word reference must be called with parentheses."
                    | None -> FlowExpression.Local(name, sourceSpan state.File first (Some first))
                | _ ->
                    fail state.File first.Line first.Column first.Text.Length "FLOW_EXPECTED_EXPRESSION" $"Token '{first.Text}' cannot begin an expression."
            let mutable result = primary
            while accept state "." do
                let stage = expectIdentifier state
                if peek state <> Some "(" then
                    tokenError state "FLOW_DOT_CALL_REQUIRES_ARGUMENTS" "A dot stage must be a statically named call with parentheses."
                let arguments = parseArguments state (callbackStage stage.Text)
                result <- FlowExpression.DotCall(result, stage.Text, arguments, sourceSpan state.File first (previous state))
            result)

    let private parseEffects state markerLine =
        let first =
            match current state with
            | Some token when token.Line = markerLine -> token
            | Some token -> fail state.File token.Line token.Column token.Text.Length "FLOW_EFFECTS_MISSING" "Effects declaration must name at least one effect or 'none'."
            | None -> tokenError state "FLOW_INCOMPLETE_INPUT" "Expected an effect declaration."
        let names = ResizeArray<string>()
        if accept state "none" then
            match current state with
            | Some token when token.Line = markerLine -> fail state.File token.Line token.Column token.Text.Length "FLOW_EFFECTS_TRAILING" "'none' cannot be combined with named effects."
            | _ -> Set.empty
        else
            let mutable sameLine = true
            let mutable needName = true
            while sameLine && not (atEnd state) do
                match current state with
                | Some token when token.Line <> markerLine -> sameLine <- false
                | _ ->
                    if not needName then
                        if accept state "," then needName <- true
                        else needName <- true
                    if sameLine then
                        match current state with
                        | Some token when token.Line = markerLine && token.Kind = Identifier ->
                            let start = consume state
                            let parts = ResizeArray<string>()
                            parts.Add start.Text
                            while peek state = Some "." do
                                consume state |> ignore
                                parts.Add((expectIdentifier state).Text)
                            names.Add(String.concat "." parts)
                            needName <- false
                        | Some token when token.Line = markerLine -> fail state.File token.Line token.Column token.Text.Length "FLOW_EFFECT_NAME_REQUIRED" "Expected an effect name."
                        | _ -> sameLine <- false
            if needName && names.Count > 0 then fail state.File first.Line first.Column first.Text.Length "FLOW_EFFECT_NAME_REQUIRED" "Effect list cannot end after a comma."
            if names.Count = 0 then fail state.File first.Line first.Column first.Text.Length "FLOW_EFFECTS_MISSING" "Effects declaration must name at least one effect or 'none'."
            if names.Count <> (Set.ofSeq names).Count then fail state.File first.Line first.Column first.Text.Length "FLOW_DUPLICATE_EFFECT" "Effects declarations cannot repeat an effect."
            Set.ofSeq names

    let rec private parseWordState state =
        withDepth state (fun () ->
            let wordToken = expect state "word"
            let name, nameSpan = parseWordName state
            expect state "(" |> ignore
            let parameters = ResizeArray<FlowParameter>()
            if not (accept state ")") then
                let mutable more = true
                while more do
                    let parameter = expectIdentifier state
                    expect state ":" |> ignore
                    let parameterType = parseType state
                    parameters.Add { Name = parameter.Text; Type = parameterType; Span = sourceSpan state.File parameter (previous state) }
                    if accept state "," then () else more <- false
                expect state ")" |> ignore
            let duplicateParameters = parameters |> Seq.groupBy (fun parameter -> parameter.Name) |> Seq.tryFind (fun (_, group) -> Seq.length group > 1)
            match duplicateParameters with
            | Some(name, _) -> fail state.File nameSpan.Line nameSpan.Column nameSpan.Length "FLOW_DUPLICATE_PARAMETER" $"Parameter '{name}' is declared more than once."
            | None -> ()
            expect state "->" |> ignore
            let outputs =
                if accept state "(" then
                    let openToken = previous state |> Option.get
                    if peek state = Some ")" then
                        let closeToken = current state |> Option.get
                        fail state.File openToken.Line openToken.Column (closeToken.Offset + closeToken.Text.Length - openToken.Offset)
                            "FLOW_OUTPUT_VECTOR_EMPTY" "A word must declare at least one output type."
                    let values = ResizeArray<LangType>()
                    values.Add(parseType state)
                    while accept state "," do
                        if peek state = Some ")" then tokenError state "FLOW_OUTPUT_TYPE_REQUIRED" "An output vector cannot end with a missing type."
                        values.Add(parseType state)
                    expect state ")" |> ignore
                    List.ofSeq values
                else [ parseType state ]
            expect state "{" |> ignore
            let mutable effects = None
            let mutable documentation = ""
            let mutable documentationSeen = false
            let mutable inMetadata = true
            while inMetadata do
                match peek state with
                | Some "effects" ->
                    let marker = consume state
                    if effects.IsSome then fail state.File marker.Line marker.Column marker.Text.Length "FLOW_DUPLICATE_EFFECTS" "Word header may contain only one effects declaration."
                    effects <- Some(parseEffects state marker.Line)
                | Some "doc" ->
                    let marker = consume state
                    match current state with
                    | Some token when token.Kind = StringLiteral ->
                        if documentationSeen then fail state.File marker.Line marker.Column marker.Text.Length "FLOW_DUPLICATE_DOC" "Word header may contain only one documentation string."
                        documentation <- JsonSerializer.Deserialize<string>((consume state).Text)
                        documentationSeen <- true
                    | None -> tokenError state "FLOW_INCOMPLETE_INPUT" "Expected a JSON-style quoted documentation string before end of input."
                    | Some _ -> tokenError state "FLOW_DOC_STRING_REQUIRED" "Documentation must be a JSON-style quoted string."
                | _ -> inMetadata <- false
            let declaredEffects =
                effects
                |> Option.defaultWith (fun () ->
                    match current state with
                    | None -> tokenError state "FLOW_INCOMPLETE_INPUT" "Expected the required effects declaration before end of input."
                    | Some _ -> tokenError state "FLOW_EFFECTS_REQUIRED" "Word definitions require an explicit effects declaration.")
            let body = parseBlockBody state
            let endToken = expect state "}"
            let span = sourceSpan state.File wordToken (Some endToken)
            let definition =
                { Name = name
                  Parameters = List.ofSeq parameters
                  Outputs = outputs
                  Effects = declaredEffects
                  Documentation = documentation
                  Body = body
                  SourceText = sourceSlice state wordToken endToken
                  Span = span
                  SyntaxVersion = 1 }
            FlowStructure.validateWordNesting definition
            definition)

    and private parseBlockBody state =
        parseBlockStatements state

    and private parseCaseHeader state kind =
        let startToken = expect state kind
        let word, _ = parseWordName state
        expect state "/" |> ignore
        let caseToken = expectIdentifier state
        let headerSpan = sourceSpan state.File startToken (Some caseToken)
        startToken, word, caseToken.Text, headerSpan

    and private parseCaseExpectationLiteral state kind =
        let expression = parseExpressionState state
        match expression with
        | FlowExpression.Literal(literal, literalSpan) -> literal, literalSpan
        | _ ->
            let source = expressionSpan expression
            fail state.File source.Line source.Column source.Length "FLOW_EXPECTATION_LITERAL_REQUIRED" $"A Flow {kind} requires a literal expectation."

    and private closeCase state kind =
        match current state with
        | Some token when token.Text = "}" -> consume state
        | Some token -> fail state.File token.Line token.Column token.Text.Length "FLOW_EXPECTATION_TRAILING" $"Unexpected content follows the Flow {kind} expectation."
        | None -> tokenError state "FLOW_INCOMPLETE_INPUT" $"Expected '}}' after the Flow {kind} expectation."

    and private parseTestState state =
        withDepth state (fun () ->
            let startToken, word, caseName, headerSpan = parseCaseHeader state "test"
            expect state "{" |> ignore
            let body = parseBlockStatementsUntil state (Set.ofList [ "=>"; "}" ])
            match current state with
            | Some token when token.Text = "}" -> fail state.File token.Line token.Column token.Text.Length "FLOW_EXPECTATION_REQUIRED" "A Flow test must end with one '=>' expectation."
            | None -> tokenError state "FLOW_INCOMPLETE_INPUT" "A Flow test is missing its '=>' expectation."
            | _ -> ()
            let expectationToken = expect state "=>"
            let expectationSpan = sourceSpan state.File expectationToken (Some expectationToken)
            let expected =
                match current state with
                | Some token when token.Text = "error" ->
                    consume state |> ignore
                    let codeToken = expectIdentifier state
                    if not (TestExpectation.isValidRuntimeErrorCode codeToken.Text) then
                        fail state.File codeToken.Line codeToken.Column codeToken.Text.Length "FLOW_INVALID_EXPECTED_ERROR_CODE" "Runtime-error expectations use '=> error UPPERCASE_CODE'."
                    FlowTestExpectation.RuntimeError(codeToken.Text, sourceSpan state.File codeToken (Some codeToken))
                | Some token when token.Text = "value" ->
                    consume state |> ignore
                    FlowTestExpectation.Expression(parseExpressionState state)
                | _ ->
                    let literal, literalSpan = parseCaseExpectationLiteral state "test"
                    FlowTestExpectation.Literal(literal, literalSpan)
            match expected with
            | FlowTestExpectation.Expression _ | FlowTestExpectation.RuntimeError _ when List.isEmpty body ->
                fail state.File startToken.Line startToken.Column startToken.Text.Length "FLOW_EXPECTATION_BODY_EMPTY" "Runtime-error and value-expression tests require a nonempty actual body."
            | _ -> ()
            let endToken = closeCase state "test"
            let definition =
                { Word = word
                  CaseName = caseName
                  Body = body
                  Expected = expected
                  SourceText = sourceSlice state startToken endToken
                  Span = sourceSpan state.File startToken (Some endToken)
                  SyntaxVersion = 1
                  HeaderSpan = headerSpan
                  ExpectationSpan = expectationSpan }
            FlowStructure.validateTestNesting definition
            definition)

    and private parseExampleState state =
        withDepth state (fun () ->
            let startToken, word, caseName, headerSpan = parseCaseHeader state "example"
            expect state "{" |> ignore
            let body = parseBlockStatementsUntil state (Set.ofList [ "=>"; "}" ])
            match current state with
            | Some token when token.Text = "}" -> fail state.File token.Line token.Column token.Text.Length "FLOW_EXPECTATION_REQUIRED" "A Flow example must end with one '=>' literal expectation."
            | None -> tokenError state "FLOW_INCOMPLETE_INPUT" "A Flow example is missing its '=>' expectation."
            | _ -> ()
            let expectationToken = expect state "=>"
            let expectationSpan = sourceSpan state.File expectationToken (Some expectationToken)
            match current state with
            | Some token when token.Text = "error" || token.Text = "value" ->
                fail state.File token.Line token.Column token.Text.Length "FLOW_EXAMPLE_EXPECTATION_KIND" "Flow examples accept literal expectations only."
            | _ -> ()
            let expected, expectedSpan = parseCaseExpectationLiteral state "example"
            if List.isEmpty body then
                fail state.File startToken.Line startToken.Column startToken.Text.Length "FLOW_EXPECTATION_BODY_EMPTY" "A Flow example requires a nonempty actual body."
            let endToken = closeCase state "example"
            let definition =
                { Word = word
                  CaseName = caseName
                  Body = body
                  Expected = expected
                  ExpectedSpan = expectedSpan
                  SourceText = sourceSlice state startToken endToken
                  Span = sourceSpan state.File startToken (Some endToken)
                  SyntaxVersion = 1
                  HeaderSpan = headerSpan
                  ExpectationSpan = expectationSpan }
            FlowStructure.validateExampleNesting definition
            definition)

    let private reservedTypeNames =
        set [ "Int"; "Float"; "Bool"; "String"; "Unit"; "List"; "Option"; "Result"; "a"; "b"; "c" ]

    let private validTypeName (name: string) =
        not (String.IsNullOrWhiteSpace name)
        && Char.IsLetter name[0]
        && (name |> Seq.forall Char.IsLetterOrDigit)
        && not (reservedTypeNames.Contains name)

    let private validFieldName (name: string) =
        let reservedWordNames =
            set [ "if"; "else"; "end"; "let"; "true"; "false"; "unit"
                  "match-option"; "match-result"; "some"; "none"; "ok"; "error"
                  "list.empty"; "list.singleton"; "option.none"; "option.some"; "result.ok"; "result.error"
                  "list.map"; "list.filter"; "list.each" ]
        not (String.IsNullOrWhiteSpace name)
        && Char.IsLetter name[0]
        && (name |> Seq.forall (fun value -> Char.IsLetterOrDigit value || value = '.' || value = '-' || value = '_' || value = '?' || value = '!'))
        && not (name.Contains('.') || reservedWordNames.Contains name)

    let private parseRecordState (state: State) =
        let startToken = expect state "record"
        let nameToken = expectIdentifier state
        if not (validTypeName nameToken.Text) then
            fail state.File nameToken.Line nameToken.Column nameToken.Text.Length "FLOW_TYPE_NAME_INVALID" "Record names must be identifiers and cannot shadow built-in types or reserved type variables."
        expect state "{" |> ignore
        let fields = ResizeArray<RecordField>()
        let fieldNames = HashSet<string>(StringComparer.Ordinal)
        while not (atEnd state) && peek state <> Some "}" do
            expect state "field" |> ignore
            let fieldName = expectIdentifier state
            if not (validFieldName fieldName.Text) then
                fail state.File fieldName.Line fieldName.Column fieldName.Text.Length "FLOW_RECORD_FIELD_NAME_INVALID" "Record fields must use non-reserved identifier names."
            if not (fieldNames.Add fieldName.Text) then
                fail state.File fieldName.Line fieldName.Column fieldName.Text.Length "FLOW_RECORD_DUPLICATE_FIELD" $"Record field '{fieldName.Text}' is repeated."
            expect state ":" |> ignore
            let fieldType = parseType state
            match current state with
            | Some token when token.Text = ";" -> consume state |> ignore
            | Some token -> fail state.File token.Line token.Column token.Text.Length "FLOW_RECORD_FIELD_SEMICOLON" "Every Flow record field must end with ';'."
            | None -> tokenError state "FLOW_INCOMPLETE_INPUT" "Expected ';' after the Flow record field."
            fields.Add { Name = fieldName.Text; Type = fieldType }
        let endToken = expect state "}"
        if fields.Count = 0 then
            fail state.File startToken.Line startToken.Column (endToken.Offset + endToken.Text.Length - startToken.Offset) "FLOW_RECORD_EMPTY" "A Flow record must declare at least one field."
        { Name = nameToken.Text
          Fields = List.ofSeq fields
          SourceText = sourceSlice state startToken endToken
          Span = sourceSpan state.File startToken (Some endToken) }

    let private parseValidatorName (state: State) =
        match current state with
        | Some root when root.Text = "::" ->
            consume state |> ignore
            let target = expectIdentifier state
            if peek state = Some "::" then
                let next = current state |> Option.get
                fail state.File root.Line root.Column (next.Offset + next.Text.Length - root.Offset) "FLOW_SCALAR_VALIDATOR_QUALIFICATION" "An absolute-root scalar validator must name one unqualified dictionary key."
            target.Text
        | Some first when first.Kind = Identifier ->
            consume state |> ignore
            if not (accept state "::") then
                fail state.File first.Line first.Column first.Text.Length "FLOW_SCALAR_VALIDATOR_QUALIFICATION" "Scalar validators require an explicit '::rootName' or 'namespace::wordName' reference."
            let segments = ResizeArray<string>()
            segments.Add first.Text
            segments.Add((expectIdentifier state).Text)
            while accept state "::" do segments.Add((expectIdentifier state).Text)
            String.concat "." segments
        | Some token -> fail state.File token.Line token.Column token.Text.Length "FLOW_SCALAR_VALIDATOR_QUALIFICATION" "Scalar validators require an explicit '::rootName' or 'namespace::wordName' reference."
        | None -> tokenError state "FLOW_INCOMPLETE_INPUT" "Expected a qualified scalar validator reference."

    let private parseScalarState (state: State) =
        let startToken = expect state "type"
        let nameToken = expectIdentifier state
        if not (validTypeName nameToken.Text) then
            fail state.File nameToken.Line nameToken.Column nameToken.Text.Length "FLOW_TYPE_NAME_INVALID" "Scalar type names must be identifiers and cannot shadow built-in types or reserved type variables."
        expect state ":" |> ignore
        let baseType = parseType state
        match baseType with
        | TInt | TFloat | TString -> ()
        | other ->
            let typeSpan = sourceSpan state.File nameToken (previous state)
            fail state.File typeSpan.Line typeSpan.Column typeSpan.Length "FLOW_SCALAR_BASE_UNSUPPORTED" "Nominal scalar wrappers currently require an Int, Float, or String base type."
        expect state "{" |> ignore
        let mutable validator = None
        while not (atEnd state) && peek state <> Some "}" do
            let validateToken = expect state "validate"
            if validator.IsSome then
                fail state.File validateToken.Line validateToken.Column validateToken.Text.Length "FLOW_SCALAR_VALIDATOR_DUPLICATE" "A scalar type may declare at most one validator."
            let validatorName = parseValidatorName state
            if peek state = Some "(" then
                let token = current state |> Option.get
                fail state.File token.Line token.Column token.Text.Length "FLOW_SCALAR_VALIDATOR_CALL" "Scalar validator declarations name a word; they cannot invoke it."
            expect state ";" |> ignore
            validator <- Some validatorName
        let endToken = expect state "}"
        { Name = nameToken.Text
          BaseType = baseType
          Validator = validator
          SourceText = sourceSlice state startToken endToken
          Span = sourceSpan state.File startToken (Some endToken) }

    let private createState file source =
        let tokens, endLine, endColumn = tokenize file source
        { File = file; Source = source; Tokens = tokens; Index = 0; Depth = 0; EndLine = endLine; EndColumn = endColumn }

    let private rejectTrailing (state: State) kind =
        if not (atEnd state) then
            tokenError state "FLOW_TRAILING_INPUT" $"Unexpected content follows the Flow {kind} definition."

    let parseExpression file source =
        try
            let state = createState file source
            let expression = parseExpressionState state
            if not (atEnd state) then tokenError state "FLOW_TRAILING_INPUT" "Unexpected token follows the Flow expression."
            FlowStructure.validateExpressionNesting [ expression ]
            Ok expression
        with LanguageException error -> Error error

    let parseWord file source =
        try
            let state = createState file source
            let definition = parseWordState state
            rejectTrailing state "word"
            Ok { definition with SourceText = source }
        with LanguageException error -> Error error

    let parseTest file source =
        try
            let state = createState file source
            let definition = parseTestState state
            rejectTrailing state "test"
            Ok { definition with SourceText = source }
        with LanguageException error -> Error error

    let parseExample file source =
        try
            let state = createState file source
            let definition = parseExampleState state
            rejectTrailing state "example"
            Ok { definition with SourceText = source }
        with LanguageException error -> Error error

    let parseDocument file source =
        try
            let state = createState file source
            let records = ResizeArray<RecordDefinition>()
            let scalars = ResizeArray<ScalarTypeDefinition>()
            let words = ResizeArray<FlowWordDefinition>()
            let tests = ResizeArray<FlowTestDefinition>()
            let examples = ResizeArray<FlowExampleDefinition>()
            let typeNames = HashSet<string>(StringComparer.Ordinal)
            let addType (name: string) (span: SourceSpan) =
                if not (typeNames.Add name) then
                    fail state.File span.Line span.Column span.Length "FLOW_PROJECT_DUPLICATE_TYPE" $"Type name '{name}' is declared more than once in the Flow project."
            while not (atEnd state) do
                match peek state with
                | Some "record" ->
                    let definition = parseRecordState state
                    addType definition.Name definition.Span
                    records.Add definition
                | Some "type" ->
                    let definition = parseScalarState state
                    addType definition.Name definition.Span
                    scalars.Add definition
                | Some "word" -> words.Add(parseWordState state)
                | Some "test" -> tests.Add(parseTestState state)
                | Some "example" -> examples.Add(parseExampleState state)
                | Some _ -> tokenError state "FLOW_PROJECT_UNKNOWN_DECLARATION" "A Flow project document contains only record, type, word, test, and example declarations."
                | None -> ()
            Ok
                { SyntaxVersion = 1
                  SourceText = source
                  Records = List.ofSeq records
                  Scalars = List.ofSeq scalars
                  Words = List.ofSeq words
                  Tests = List.ofSeq tests
                  Examples = List.ofSeq examples }
        with LanguageException error -> Error error
