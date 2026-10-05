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

    let private maxSourceLength = 1_000_000
    let private maxTokens = 100_000
    let private maxNesting = 128
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

    let rec private parseArguments state =
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
                        let expression = parseExpressionState state
                        values.Add(FlowArgument.Named(name.Text, expression, sourceSpan state.File name (previous state)))
                    | _ -> values.Add(FlowArgument.Positional(parseExpressionState state))
                    if accept state "," then () else more <- false
                expect state ")" |> ignore
            List.ofSeq values)

    and private parseBlock state =
        withDepth state (fun () ->
            expect state "{" |> ignore
            let statements = ResizeArray<FlowStatement>()
            while peek state <> Some "}" && not (atEnd state) do
                let start = current state |> Option.get
                let statement =
                    if accept state "let" then
                        let name = expectIdentifier state
                        expect state "=" |> ignore
                        let value = parseExpressionState state
                        FlowStatement.Let(name.Text, value, sourceSpan state.File start (previous state))
                    else FlowStatement.Evaluate(parseExpressionState state)
                statements.Add statement
                if accept state ";" then ()
                else
                    match current state, previous state with
                    | Some next, Some last when next.Text <> "}" && next.Line <= last.Line ->
                        fail state.File next.Line next.Column next.Text.Length "FLOW_EXPECTED_SEPARATOR" "Statements on one line must be separated by ';'."
                    | _ -> ()
            expect state "}" |> ignore
            List.ofSeq statements)

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
        let arguments = parseArguments state
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
                | Identifier, "if" ->
                    consume state |> ignore
                    let condition = parseExpressionState state
                    let thenBranch = parseBlock state
                    expect state "else" |> ignore
                    let elseBranch = parseBlock state
                    FlowExpression.If(condition, thenBranch, elseBranch, sourceSpan state.File first (previous state))
                | Identifier, _ ->
                    let name = parseNamespaceName state
                    match constructorKind name with
                    | Some kind -> parseConstructor state first kind
                    | None when peek state = Some "(" ->
                        let args = parseArguments state
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
                let arguments = parseArguments state
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
            let output = parseType state
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
                    | _ -> tokenError state "FLOW_DOC_STRING_REQUIRED" "Documentation must be a JSON-style quoted string."
                | _ -> inMetadata <- false
            let declaredEffects = effects |> Option.defaultWith (fun () -> tokenError state "FLOW_EFFECTS_REQUIRED" "Word definitions require an explicit effects declaration.")
            let body = parseBlockBody state
            let endToken = expect state "}"
            if not (atEnd state) then tokenError state "FLOW_TRAILING_INPUT" "Unexpected content follows the word definition."
            let span = sourceSpan state.File wordToken (Some endToken)
            { Name = name
              Parameters = List.ofSeq parameters
              Output = output
              Effects = declaredEffects
              Documentation = documentation
              Body = body
              SourceText = state.Source
              Span = span
              SyntaxVersion = 1 })

    and private parseBlockBody state =
        let statements = ResizeArray<FlowStatement>()
        while peek state <> Some "}" && not (atEnd state) do
            let start = current state |> Option.get
            let statement =
                if accept state "let" then
                    let name = expectIdentifier state
                    expect state "=" |> ignore
                    let value = parseExpressionState state
                    FlowStatement.Let(name.Text, value, sourceSpan state.File start (previous state))
                else FlowStatement.Evaluate(parseExpressionState state)
            statements.Add statement
            if accept state ";" then ()
            else
                match current state, previous state with
                | Some next, Some last when next.Text <> "}" && next.Line <= last.Line ->
                    fail state.File next.Line next.Column next.Text.Length "FLOW_EXPECTED_SEPARATOR" "Statements on one line must be separated by ';'."
                | _ -> ()
        List.ofSeq statements

    let private expressionSpan = function
        | FlowExpression.Literal(_, span)
        | FlowExpression.Local(_, span)
        | FlowExpression.Call(_, _, span)
        | FlowExpression.DotCall(_, _, _, span)
        | FlowExpression.If(_, _, _, span)
        | FlowExpression.Container(_, _, _, span)
        | FlowExpression.MatchOption(_, _, _, span)
        | FlowExpression.MatchResult(_, _, _, span) -> span

    let private expressionsInStatements statements =
        statements
        |> List.choose (function
            | FlowStatement.Let(_, expression, _) -> Some expression
            | FlowStatement.Evaluate expression -> Some expression)

    /// The recursive parser limit does not count the iterative postfix loop.
    /// Validate the resulting AST iteratively so a long receiver chain and its
    /// nested arguments cannot reach recursive lowering/rendering without a
    /// bound on their combined expression depth.
    let private validateExpressionNesting (roots: FlowExpression list) =
        let pending = Stack<FlowExpression * int>()
        for root in List.rev roots do pending.Push((root, 1))
        let argumentChildren arguments =
            arguments
            |> List.map (function
                | FlowArgument.Positional expression -> expression
                | FlowArgument.Named(_, expression, _) -> expression)
        let statementChildren statements = expressionsInStatements statements
        let children = function
            | FlowExpression.Literal _ | FlowExpression.Local _ -> []
            | FlowExpression.Call(_, arguments, _) -> argumentChildren arguments
            | FlowExpression.DotCall(receiver, _, arguments, _) -> receiver :: argumentChildren arguments
            | FlowExpression.If(condition, thenBody, elseBody, _) ->
                condition :: (statementChildren thenBody @ statementChildren elseBody)
            | FlowExpression.Container(_, _, payload, _) -> payload |> Option.toList
            | FlowExpression.MatchOption(scrutinee, someCase, noneCase, _) ->
                scrutinee :: (statementChildren someCase.Statements @ statementChildren noneCase.Statements)
            | FlowExpression.MatchResult(scrutinee, okCase, errorCase, _) ->
                scrutinee :: (statementChildren okCase.Statements @ statementChildren errorCase.Statements)
        while pending.Count > 0 do
            let expression, depth = pending.Pop()
            if depth > maxNesting then
                let source = expressionSpan expression
                fail source.File source.Line source.Column source.Length "FLOW_NESTING_LIMIT" $"Flow syntax exceeds the nesting limit of {maxNesting}."
            else
                for child in List.rev (children expression) do pending.Push((child, depth + 1))

    let private validateWordNesting (definition: FlowWordDefinition) =
        validateExpressionNesting (expressionsInStatements definition.Body)

    let parseExpression file source =
        try
            let tokens, endLine, endColumn = tokenize file source
            let state = { File = file; Source = source; Tokens = tokens; Index = 0; Depth = 0; EndLine = endLine; EndColumn = endColumn }
            let expression = parseExpressionState state
            if not (atEnd state) then tokenError state "FLOW_TRAILING_INPUT" "Unexpected token follows the Flow expression."
            validateExpressionNesting [ expression ]
            Ok expression
        with LanguageException error -> Error error

    let parseWord file source =
        try
            let tokens, endLine, endColumn = tokenize file source
            let state = { File = file; Source = source; Tokens = tokens; Index = 0; Depth = 0; EndLine = endLine; EndColumn = endColumn }
            let definition = parseWordState state
            validateWordNesting definition
            Ok definition
        with LanguageException error -> Error error
