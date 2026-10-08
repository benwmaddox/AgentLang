namespace AgentLang

open System
open System.Globalization
open System.Text.Json

module Parser =
    type private Line = { Text: string; Number: int }
    type private Token = { Text: string; Column: int }

    let private reservedWordNames =
        set [ "if"; "else"; "end"; "let"; "true"; "false"; "unit"
              "match-option"; "match-result"; "some"; "none"; "ok"; "error"
              "list.empty"; "list.singleton"; "option.none"; "option.some"; "result.ok"; "result.error"
              "list.map"; "list.filter"; "list.each"; "list.fold" ]
    let private reservedTypeNames = set [ "Int"; "Float"; "Bool"; "String"; "Unit"; "List"; "Option"; "Result"; "a"; "b"; "c" ]

    let private validWordName (name: string) =
        not (String.IsNullOrWhiteSpace name)
        && Char.IsLetter name[0]
        && name |> Seq.forall (fun value -> Char.IsLetterOrDigit value || value = '.' || value = '-' || value = '_' || value = '?' || value = '!')
        && not (reservedWordNames.Contains name)

    let private validTypeName (name: string) =
        not (String.IsNullOrWhiteSpace name)
        && Char.IsLetter name[0]
        && name |> Seq.forall Char.IsLetterOrDigit
        && not (reservedTypeNames.Contains name)

    let private fail file line column code message =
        Diagnostics.raiseError code message None (Some { File = file; Line = line; Column = column; Length = 1 }) [] []

    let private span file line column length =
        { File = file; Line = line; Column = column; Length = max 1 length }

    let private stripComment (line: string) =
        let mutable quoted = false
        let mutable escaped = false
        let mutable stop = line.Length
        let mutable index = 0
        while index < line.Length && stop = line.Length do
            let ch = line[index]
            if quoted then
                if escaped then escaped <- false
                elif ch = '\\' then escaped <- true
                elif ch = '"' then quoted <- false
            elif ch = '"' then quoted <- true
            elif ch = '#' then stop <- index
            index <- index + 1
        line.Substring(0, stop).TrimEnd()

    let private tokenize file (line: Line) =
        let result = ResizeArray<Token>()
        let text = stripComment line.Text
        let mutable index = 0
        while index < text.Length do
            while index < text.Length && Char.IsWhiteSpace text[index] do index <- index + 1
            if index < text.Length then
                let start = index
                if text[index] = '"' then
                    index <- index + 1
                    let mutable escaped = false
                    let mutable closed = false
                    while index < text.Length && not closed do
                        if escaped then escaped <- false
                        elif text[index] = '\\' then escaped <- true
                        elif text[index] = '"' then closed <- true
                        index <- index + 1
                    if not closed then fail file line.Number (start + 1) "PARSE_UNTERMINATED_STRING" "String literal is missing its closing quote."
                else
                    // Keep whitespace inside a closed generic constructor token, e.g.
                    // result.ok<Int, String>, together so the type arguments remain exact.
                    let mutable genericDepth = 0
                    let mutable scanning = true
                    while index < text.Length && scanning do
                        let ch = text[index]
                        if Char.IsWhiteSpace ch && genericDepth = 0 then scanning <- false
                        else
                            if ch = '<' then genericDepth <- genericDepth + 1
                            elif ch = '>' then genericDepth <- genericDepth - 1
                            index <- index + 1
                result.Add { Text = text.Substring(start, index - start); Column = start + 1 }
        List.ofSeq result

    let private parseLiteral file line (token: Token) =
        let value = token.Text
        let literal =
            if value = "true" then Some(LBool true)
            elif value = "false" then Some(LBool false)
            elif value = "unit" then Some LUnit
            elif value.StartsWith("\"", StringComparison.Ordinal) then
                try Some(LString(JsonSerializer.Deserialize<string>(value)))
                with _ -> fail file line token.Column "PARSE_INVALID_STRING" "String literal is not valid JSON-style quoted text."
            else
                match Int64.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture) with
                | true, number -> Some(LInt number)
                | _ ->
                    let startsNumeric = value.Length > 0 && (Char.IsDigit value[0] || value[0] = '-' || value[0] = '+')
                    let integerDigits =
                        let digits = if value.StartsWith("-", StringComparison.Ordinal) || value.StartsWith("+", StringComparison.Ordinal) then value.Substring(1) else value
                        digits.Length > 0 && digits |> Seq.forall Char.IsDigit
                    if startsNumeric && integerDigits then
                        fail file line token.Column "PARSE_INTEGER_RANGE" "Integer literal is outside the Int64 range."
                    elif startsNumeric && (value.Contains('.') || value.Contains('e') || value.Contains('E')) then
                        match Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture) with
                        | true, number when Double.IsFinite number -> Some(LFloat number)
                        | true, _ -> fail file line token.Column "PARSE_NONFINITE_FLOAT" "Float literals must be finite."
                        | _ -> fail file line token.Column "PARSE_INVALID_FLOAT" "Float literal is not a valid numeric value."
                    else None
        literal

    let private maxSourceTypeNestingDepth = 256
    let private maxSourceBlockNestingDepth = 64

    let private parseType file line (source: string) =
        let text = source.Trim()
        let mutable cursor = 0
        let skipWhitespace () =
            while cursor < text.Length && Char.IsWhiteSpace text[cursor] do cursor <- cursor + 1
        let parseName () =
            let start = cursor
            while cursor < text.Length && Char.IsLetterOrDigit text[cursor] do cursor <- cursor + 1
            if start = cursor then fail file line (cursor + 1) "PARSE_INVALID_TYPE" "Expected a type name."
            text.Substring(start, cursor - start)
        let rec parse depth =
            if depth > maxSourceTypeNestingDepth then
                fail file line (cursor + 1) "PARSE_TYPE_NESTING_LIMIT" $"Type nesting exceeds the source limit of {maxSourceTypeNestingDepth}."
            skipWhitespace ()
            let name = parseName ()
            skipWhitespace ()
            let arguments =
                if cursor < text.Length && text[cursor] = '<' then
                    cursor <- cursor + 1
                    let values = ResizeArray<LangType>()
                    let mutable needsArgument = true
                    while needsArgument do
                        values.Add(parse (depth + 1))
                        skipWhitespace ()
                        if cursor >= text.Length then fail file line (cursor + 1) "PARSE_INVALID_TYPE" $"Type '{text}' is missing a closing '>'."
                        elif text[cursor] = ',' then cursor <- cursor + 1
                        elif text[cursor] = '>' then cursor <- cursor + 1; needsArgument <- false
                        else fail file line (cursor + 1) "PARSE_INVALID_TYPE" $"Expected ',' or '>' in type '{text}'."
                    Some(List.ofSeq values)
                else None
            match name, arguments with
            | "Int", None -> TInt
            | "Float", None -> TFloat
            | "Bool", None -> TBool
            | "String", None -> TString
            | "Unit", None -> TUnit
            | "List", Some [ item ] -> TList item
            | "Option", Some [ item ] -> TOption item
            | "Result", Some [ ok; error ] -> TResult(ok, error)
            | ("List" | "Option" | "Result"), _ -> fail file line 1 "PARSE_INVALID_TYPE_ARITY" $"Type '{name}' has the wrong number of type arguments."
            | _, Some _ -> fail file line 1 "PARSE_UNSUPPORTED_TYPE" $"Parameterized type '{name}' is not part of the closed built-in type set."
            | _, None when name = "Int" || name = "Float" || name = "Bool" || name = "String" || name = "Unit" -> fail file line 1 "PARSE_INVALID_TYPE" $"Type '{name}' cannot have type arguments."
            | _, None -> TNamed name
        let result = parse 0
        skipWhitespace ()
        if cursor <> text.Length then fail file line (cursor + 1) "PARSE_INVALID_TYPE" $"Unexpected text in type '{text}'."
        result

    /// Parse one closed type expression supplied by a Discovery query.
    /// The signature parser also accepts reserved type-variable names through
    /// its shared type parser, but query types must be fully concrete.
    let parseClosedType (source: string) : Result<LangType, Diagnostic> =
        let rec openVariable = function
            | TVar name -> Some name
            | TNamed name when reservedTypeNames.Contains name -> Some name
            | TList item | TOption item -> openVariable item
            | TResult(ok, error) -> openVariable ok |> Option.orElseWith (fun () -> openVariable error)
            | TInt | TFloat | TBool | TString | TUnit | TNamed _ -> None

        try
            let parsed = parseType "<type-query>" 1 source
            match openVariable parsed with
            | Some name ->
                Error
                    { Code = "PARSE_OPEN_TYPE"
                      Message = $"Type query must be closed; '{name}' is a reserved type variable."
                      Word = None
                      Span = Some { File = "<type-query>"; Line = 1; Column = 1; Length = max 1 source.Length }
                      Expected = [ "closed type" ]
                      Actual = [ name ] }
            | None -> Ok parsed
        with
        | LanguageException diagnostic -> Error diagnostic
        | ex ->
            Error
                { Code = "PARSE_FAILURE"
                  Message = ex.Message
                  Word = None
                  Span = None
                  Expected = []
                  Actual = [] }

    let private parseHeader file (line: Line) (prefix: string) =
        let tokens = tokenize file line
        match tokens with
        | head :: [ name ] when head.Text = prefix -> name.Text
        | _ -> fail file line.Number 1 "PARSE_INVALID_HEADER" $"Expected '{prefix} <name>'."

    let private parseSignature file (line: Line) (source: string) =
        let pieces = source.Split([| "->" |], StringSplitOptions.None)
        if pieces.Length <> 2 then fail file line.Number 1 "PARSE_INVALID_SIGNATURE" "Word signature must use 'Inputs -> Outputs'."
        let parseSide (part: string) =
            let values = ResizeArray<string>()
            let mutable start = -1
            let mutable depth = 0
            for index = 0 to part.Length - 1 do
                let ch = part[index]
                if start < 0 && not (Char.IsWhiteSpace ch) then start <- index
                if start >= 0 then
                    if ch = '<' then depth <- depth + 1
                    elif ch = '>' then depth <- depth - 1
                    if depth < 0 then fail file line.Number (index + 1) "PARSE_INVALID_TYPE" "Unexpected '>' in signature type."
                    if Char.IsWhiteSpace ch && depth = 0 then
                        let value = part.Substring(start, index - start).Trim()
                        if value <> "" then values.Add value
                        start <- -1
            if start >= 0 then values.Add(part.Substring(start).Trim())
            if depth <> 0 then fail file line.Number 1 "PARSE_INVALID_TYPE" "A signature type is missing a matching angle bracket."
            values |> Seq.map (parseType file line.Number) |> Seq.toList
        parseSide pieces[0], parseSide pieces[1]

    let private parseGenericArguments file line (text: string) =
        let values = ResizeArray<string>()
        let mutable depth = 0
        let mutable start = 0
        for index = 0 to text.Length - 1 do
            match text[index] with
            | '<' -> depth <- depth + 1
            | '>' -> depth <- depth - 1
            | ',' when depth = 0 ->
                values.Add(text.Substring(start, index - start).Trim())
                start <- index + 1
            | _ -> ()
        values.Add(text.Substring(start).Trim())
        if values |> Seq.exists String.IsNullOrWhiteSpace then fail file line.Number 1 "PARSE_INVALID_CONTAINER_CONSTRUCTOR" "Every container constructor type argument must be specified."
        values |> Seq.map (parseType file line.Number) |> Seq.toList

    let private parseContainerConstructor file (line: Line) (token: Token) =
        let constructors =
            [ "list.empty", ListEmpty; "list.singleton", ListSingleton
              "option.none", OptionNone; "option.some", OptionSome
              "result.ok", ResultOk; "result.error", ResultError ]
        constructors
        |> List.tryPick (fun (prefix, kind) ->
            if token.Text.StartsWith(prefix + "<", StringComparison.Ordinal) && token.Text.EndsWith(">", StringComparison.Ordinal) then
                let arguments = token.Text.Substring(prefix.Length + 1, token.Text.Length - prefix.Length - 2) |> parseGenericArguments file line
                let expected = match kind with ListEmpty | ListSingleton | OptionNone | OptionSome -> 1 | ResultOk | ResultError -> 2
                if arguments.Length <> expected then fail file line.Number token.Column "PARSE_INVALID_CONTAINER_CONSTRUCTOR" $"'{prefix}' requires {expected} explicit type argument(s)."
                Some(ConstructContainer(kind, arguments, span file line.Number token.Column token.Text.Length))
            elif token.Text.StartsWith(prefix, StringComparison.Ordinal) then
                fail file line.Number token.Column "PARSE_INVALID_CONTAINER_CONSTRUCTOR" $"Use '{prefix}<...>' with explicit closed type arguments."
            else None)

    let private parseOps file (line: Line) =
        let tokens = tokenize file line
        let expressions = ResizeArray<Expr>()
        let mutable remaining = tokens
        while not remaining.IsEmpty do
            match remaining with
            | token :: rest ->
                match parseLiteral file line.Number token with
                | Some literal ->
                    expressions.Add(Push(literal, span file line.Number token.Column token.Text.Length))
                    remaining <- rest
                | None when token.Text.StartsWith("$", StringComparison.Ordinal) && token.Text.Length > 1 ->
                    expressions.Add(Load(token.Text.Substring(1), span file line.Number token.Column token.Text.Length))
                    remaining <- rest
                | None when token.Text = "let" ->
                    match rest with
                    | name :: next ->
                        expressions.Add(Let(name.Text, span file line.Number token.Column (name.Column + name.Text.Length - token.Column)))
                        remaining <- next
                    | [] -> fail file line.Number token.Column "PARSE_EXPECTED_LOCAL_NAME" "'let' must be followed by a local name."
                | None when token.Text = "list.map" || token.Text = "list.filter" || token.Text = "list.each" || token.Text = "list.fold" ->
                    match rest with
                    | target :: next when validWordName target.Text ->
                        let expressionSpan = span file line.Number token.Column (target.Column + target.Text.Length - token.Column)
                        let operation =
                            match token.Text with
                            | "list.map" -> MapList(target.Text, expressionSpan)
                            | "list.filter" -> FilterList(target.Text, expressionSpan)
                            | "list.each" -> EachList(target.Text, expressionSpan)
                            | _ -> FoldList(target.Text, expressionSpan)
                        expressions.Add(operation)
                        remaining <- next
                    | target :: _ -> fail file line.Number target.Column "PARSE_INVALID_WORD_NAME" "List higher-order operations require a static word name argument."
                    | [] -> fail file line.Number token.Column "PARSE_EXPECTED_WORD_NAME" $"'{token.Text}' must be followed by a static word name."
                | None ->
                    match parseContainerConstructor file line token with
                    | Some expression -> expressions.Add(expression)
                    | None -> expressions.Add(Call(token.Text, span file line.Number token.Column token.Text.Length))
                    remaining <- rest
            | [] -> ()
        List.ofSeq expressions

    /// Parse expression lines. If blocks use standalone `if`, optional `else`, and `end` lines.
    let private parseExpressionLines file (lines: Line list) =
        let skipBlank index =
            let mutable cursor = index
            while cursor < lines.Length && String.IsNullOrWhiteSpace(stripComment lines[cursor].Text) do cursor <- cursor + 1
            cursor

        let parseLocalHeader (line: Line) keyword =
            let tokens = tokenize file line
            match tokens with
            | [ head; name ] when head.Text = keyword && validWordName name.Text -> name.Text
            | _ -> fail file line.Number 1 "PARSE_INVALID_MATCH_CASE" $"Expected '{keyword} <local-name>'."

        let rec parseBlock depth index (stops: Set<string>) =
            if depth > maxSourceBlockNestingDepth then
                let diagnosticLine =
                    if lines.IsEmpty then 1
                    else lines[min (max 0 index) (lines.Length - 1)].Number
                fail file diagnosticLine 1 "PARSE_BLOCK_NESTING_LIMIT" $"Block nesting exceeds the source limit of {maxSourceBlockNestingDepth}."
            let expressions = ResizeArray<Expr>()
            let mutable cursor = index
            let mutable terminator = "eof"
            let mutable running = true
            while cursor < lines.Length && running do
                let line = lines[cursor]
                let content = stripComment line.Text |> fun value -> value.Trim()
                match content with
                | value when stops.Contains value || (stops.Contains "error" && value.StartsWith("error ", StringComparison.Ordinal)) ->
                    terminator <- if value.StartsWith("error ", StringComparison.Ordinal) then "error" else value
                    cursor <- cursor + 1
                    running <- false
                | "end" ->
                    terminator <- "end"
                    cursor <- cursor + 1
                    running <- false
                | "if" ->
                    let thenBranch, afterThen, endKind = parseBlock (depth + 1) (cursor + 1) (Set.ofList [ "else"; "end" ])
                    let elseBranch, afterElse =
                        if endKind = "else" then
                            let branch, next, finalKind = parseBlock (depth + 1) afterThen (Set.singleton "end")
                            if finalKind <> "end" then fail file line.Number 1 "PARSE_UNCLOSED_IF" "The if branch is missing its closing 'end'."
                            branch, next
                        elif endKind = "end" then [], afterThen
                        else fail file line.Number 1 "PARSE_UNCLOSED_IF" "The if branch is missing its closing 'end'."
                    expressions.Add(If(thenBranch, elseBranch, span file line.Number 1 line.Text.Length))
                    cursor <- afterElse
                | "match-option" ->
                    let someHeader = skipBlank (cursor + 1)
                    if someHeader >= lines.Length then fail file line.Number 1 "PARSE_UNCLOSED_MATCH" "The option match requires a some case, a none case, and a closing 'end'."
                    let someName = parseLocalHeader lines[someHeader] "some"
                    let someBranch, afterSome, someTerminator = parseBlock (depth + 1) (someHeader + 1) (Set.ofList [ "none"; "end" ])
                    if someTerminator <> "none" then fail file line.Number 1 "PARSE_MISSING_MATCH_CASE" "An option match requires both 'some <name>' and 'none' cases."
                    let noneBranch, afterNone, finalTerminator = parseBlock (depth + 1) afterSome (Set.singleton "end")
                    if finalTerminator <> "end" then fail file line.Number 1 "PARSE_UNCLOSED_MATCH" "The option match is missing its closing 'end'."
                    expressions.Add(MatchOption(someName, someBranch, noneBranch, span file line.Number 1 line.Text.Length))
                    cursor <- afterNone
                | "match-result" ->
                    let okHeader = skipBlank (cursor + 1)
                    if okHeader >= lines.Length then fail file line.Number 1 "PARSE_UNCLOSED_MATCH" "The result match requires ok and error cases and a closing 'end'."
                    let okName = parseLocalHeader lines[okHeader] "ok"
                    let okBranch, afterOk, okTerminator = parseBlock (depth + 1) (okHeader + 1) (Set.ofList [ "error"; "end" ])
                    if okTerminator <> "error" then fail file line.Number 1 "PARSE_MISSING_MATCH_CASE" "A result match requires both 'ok <name>' and 'error <name>' cases."
                    let errorName = parseLocalHeader lines[afterOk - 1] "error"
                    let errorBranch, afterError, finalTerminator = parseBlock (depth + 1) afterOk (Set.singleton "end")
                    if finalTerminator <> "end" then fail file line.Number 1 "PARSE_UNCLOSED_MATCH" "The result match is missing its closing 'end'."
                    expressions.Add(MatchResult(okName, errorName, okBranch, errorBranch, span file line.Number 1 line.Text.Length))
                    cursor <- afterError
                | "else" -> fail file line.Number 1 "PARSE_UNEXPECTED_ELSE" "'else' has no matching 'if'."
                | "none" | "error" | "some" | "ok" -> fail file line.Number 1 "PARSE_UNEXPECTED_MATCH_CASE" $"'{content}' has no matching option or result match."
                | value when value.StartsWith("error ", StringComparison.Ordinal) -> fail file line.Number 1 "PARSE_UNEXPECTED_MATCH_CASE" $"'{content}' has no matching result match."
                | "" -> cursor <- cursor + 1
                | _ ->
                    for expression in parseOps file line do expressions.Add expression
                    cursor <- cursor + 1
            List.ofSeq expressions, cursor, terminator
        let expressions, next, terminator = parseBlock 0 0 Set.empty
        if terminator = "end" then
            let line = lines[min (next - 1) (lines.Length - 1)]
            fail file line.Number 1 "PARSE_UNEXPECTED_END" "Unexpected 'end'."
        expressions

    let private blockEnd file (lines: Line array) start =
        let mutable depth = 1
        let mutable cursor = start + 1
        while cursor < lines.Length && depth > 0 do
            let content = stripComment lines[cursor].Text |> fun value -> value.Trim()
            if content = "if" || content = "match-option" || content = "match-result" then depth <- depth + 1
            elif content = "end" then depth <- depth - 1
            cursor <- cursor + 1
        if depth <> 0 then fail file lines[start].Number 1 "PARSE_UNCLOSED_BLOCK" "Definition is missing its closing 'end'."
        cursor - 1

    let private linesForSource (source: string) =
        source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
        |> Array.mapi (fun index text -> { Text = text; Number = index + 1 })

    let private blockSource (lines: Line array) start finish =
        lines[start..finish] |> Array.map (fun line -> line.Text) |> String.concat Environment.NewLine

    let private parseWord file (lines: Line array) start finish =
        let header = lines[start]
        let headerText = stripComment header.Text |> fun value -> value.Trim()
        let colon = headerText.IndexOf(':')
        if colon < 0 then fail file header.Number 1 "PARSE_MISSING_SIGNATURE" "Word header must contain a signature after ':'."
        let name = headerText.Substring("word".Length, colon - "word".Length).Trim()
        if not (validWordName name) then fail file header.Number 1 "PARSE_INVALID_WORD_NAME" "Word names must be single callable identifiers and cannot use syntax or literal keywords."
        let inputs, outputs = parseSignature file header (headerText.Substring(colon + 1))
        let content = if finish > start + 1 then lines[start + 1 .. finish - 1] |> Array.toList else []
        let effects = ResizeArray<string>()
        let mutable sawEffects = false
        let mutable maturity = ProjectWord
        let mutable revision = 1
        let docs = ResizeArray<string>()
        let bodyLines = ResizeArray<Line>()
        for line in content do
            let trimmed = stripComment line.Text |> fun value -> value.Trim()
            if trimmed.StartsWith("effects ", StringComparison.Ordinal) then
                if sawEffects then fail file line.Number 1 "PARSE_DUPLICATE_EFFECTS" "A word may declare effects only once."
                sawEffects <- true
                let list = trimmed.Substring("effects ".Length).Trim()
                if list <> "none" then
                    list.Split([| ','; ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries) |> Array.iter effects.Add
            elif trimmed.StartsWith("doc ", StringComparison.Ordinal) then
                let token = { Text = trimmed.Substring("doc ".Length).Trim(); Column = 1 }
                match parseLiteral file line.Number token with
                | Some(LString text) -> docs.Add text
                | _ -> fail file line.Number 1 "PARSE_INVALID_DOC" "Documentation must be a quoted string."
            elif trimmed.StartsWith("doc", StringComparison.Ordinal) then
                fail file line.Number 1 "PARSE_INVALID_DOC" "Documentation syntax is 'doc \"quoted text\"'."
            elif trimmed.StartsWith("maturity ", StringComparison.Ordinal) then
                match trimmed.Substring("maturity ".Length).Trim() with
                | "project" -> maturity <- ProjectWord
                | "library" -> maturity <- LibraryWord
                | _ -> fail file line.Number 1 "PARSE_INVALID_MATURITY" "Maturity must be 'project' or 'library'."
            elif trimmed.StartsWith("revision ", StringComparison.Ordinal) then
                match Int32.TryParse(trimmed.Substring("revision ".Length).Trim(), NumberStyles.None, CultureInfo.InvariantCulture) with
                | true, value when value > 0 -> revision <- value
                | _ -> fail file line.Number 1 "PARSE_INVALID_REVISION" "Revision must be a positive integer."
            else bodyLines.Add line
        if not sawEffects then fail file header.Number 1 "PARSE_MISSING_EFFECTS" "Every word must declare 'effects none' or an explicit effect list."
        let reserved = set [ "fs.read"; "fs.write"; "db.read"; "db.write"; "network.read"; "network.write"; "process.execute"; "clock.read"; "random.read"; "console.write" ]
        for effect in effects do
            if not (reserved.Contains effect) then fail file header.Number 1 "PARSE_UNKNOWN_EFFECT" $"Unknown effect '{effect}'."
        { Name = name
          Inputs = inputs
          Outputs = outputs
          Effects = Set.ofSeq effects
          Maturity = maturity
          Revision = revision
          Documentation = String.concat " " docs
          Body = parseExpressionLines file (List.ofSeq bodyLines)
          SourceText = blockSource lines start finish
          Span = span file header.Number 1 header.Text.Length }

    let private parseExpected file (line: Line) (tokens: Token list) =
        match tokens with
        | [ token ] ->
            match parseLiteral file line.Number token with
            | Some literal -> literal
            | None -> fail file line.Number token.Column "PARSE_EXPECTED_LITERAL" "Expected value must be a literal."
        | _ -> fail file line.Number 1 "PARSE_EXPECTED_LITERAL" "Expected value must contain exactly one literal."

    let private parseTestLike file (lines: Line array) start finish isExample =
        let header = lines[start]
        let keyword = if isExample then "example" else "test"
        let identifier = parseHeader file header keyword
        let pieces = identifier.Split([| '/' |], 2)
        if pieces.Length <> 2 || String.IsNullOrWhiteSpace pieces[0] || String.IsNullOrWhiteSpace pieces[1] then
            fail file header.Number 1 "PARSE_INVALID_TEST_NAME" $"Use '{keyword} word-name/case-name'."
        let content = if finish > start + 1 then lines[start + 1 .. finish - 1] |> Array.toList else []
        let meaningful = content |> List.filter (fun line -> not (String.IsNullOrWhiteSpace(stripComment line.Text)))
        if List.isEmpty meaningful then fail file header.Number 1 "PARSE_MISSING_EXPECTED" "Test and example blocks require a final '=> literal' line (tests may also use '=> error CODE')."
        let expectationMarker (line: Line) =
            let text = stripComment line.Text |> fun value -> value.Trim()
            if text.StartsWith("=>", StringComparison.Ordinal)
               && text.Length > 2 && Char.IsWhiteSpace text[2] then
                let rest = text.Substring(2).TrimStart()
                rest = "value"
                || (rest.StartsWith("value", StringComparison.Ordinal)
                    && rest.Length > "value".Length
                    && Char.IsWhiteSpace rest["value".Length])
            else false
        let valueMarker = meaningful |> List.tryFindIndex expectationMarker
        let body, expected =
            match valueMarker with
            | Some markerIndex ->
                if isExample then
                    let marker = meaningful[markerIndex]
                    fail file marker.Number 1 "PARSE_VALUE_EXPECTATION_NOT_ALLOWED" "Examples use literal expectations; typed value-expression expectations are available only in tests."
                let marker = meaningful[markerIndex]
                let markerText = stripComment marker.Text |> fun value -> value.Trim()
                let rest = markerText.Substring(2).Trim()
                let inlineExpression = rest.Substring("value".Length).Trim()
                let bodyLines = meaningful |> List.take markerIndex
                let body = parseExpressionLines file bodyLines
                if List.isEmpty body then
                    fail file header.Number 1 "PARSE_EMPTY_VALUE_EXPECTATION_BODY" "A typed value expectation requires an expression to test."
                let expectedExpressions =
                    if String.IsNullOrEmpty inlineExpression then
                        meaningful |> List.skip (markerIndex + 1) |> parseExpressionLines file
                    else
                        if markerIndex <> meaningful.Length - 1 then
                            fail file marker.Number 1 "PARSE_VALUE_EXPECTATION_NOT_FINAL" "An inline '=> value <expression>' expectation must be the last meaningful test line."
                        let rawLine = stripComment marker.Text
                        let arrowIndex = rawLine.IndexOf("=>", StringComparison.Ordinal)
                        let restIndex = rawLine.IndexOf(rest, max 0 (arrowIndex + 2), StringComparison.Ordinal)
                        if arrowIndex < 0 || restIndex < 0 then
                            fail file marker.Number 1 "PARSE_INVALID_VALUE_EXPECTATION" "Could not locate the typed value expectation in its source line."
                        let expressionOffset = rest.IndexOf(inlineExpression, "value".Length, StringComparison.Ordinal)
                        let expressionColumn = restIndex + expressionOffset
                        let expressionLine =
                            { Text = String(' ', expressionColumn) + inlineExpression
                              Number = marker.Number }
                        parseExpressionLines file [ expressionLine ]
                if List.isEmpty expectedExpressions then
                    fail file marker.Number 1 "PARSE_EMPTY_VALUE_EXPECTATION" "Typed value expectations require a nonempty expression."
                body, ExpectedExpression expectedExpressions
            | None ->
                let expectedLine = List.last meaningful
                let expectedText = stripComment expectedLine.Text |> fun value -> value.Trim()
                let expectedTokens = tokenize file expectedLine
                let parseArrowExpectation (rest: string) : TestExpectation =
                    let parts: string array = rest.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
                    if parts.Length > 0 && parts[0] = "error" then
                        if isExample then
                            fail file expectedLine.Number 1 "PARSE_ERROR_EXPECTATION_NOT_ALLOWED" "Examples must end with a literal value expectation; runtime-error expectations are available only in tests."
                        match parts with
                        | [| "error"; code |] when TestExpectation.isValidRuntimeErrorCode code -> ExpectedRuntimeError code
                        | _ -> fail file expectedLine.Number 1 "PARSE_INVALID_EXPECTED_ERROR" "Runtime-error expectations use '=> error UPPERCASE_CODE' with a stable uppercase diagnostic code."
                    else
                        let token = { Text = rest; Column = expectedLine.Text.IndexOf(rest, StringComparison.Ordinal) + 1 }
                        ExpectedValue(parseExpected file expectedLine [ token ])
                let expected =
                    if expectedText.StartsWith("=>", StringComparison.Ordinal) then
                        let rest = expectedText.Substring(2).Trim()
                        parseArrowExpectation rest
                    elif expectedText.StartsWith("expect ", StringComparison.Ordinal) then
                        ExpectedValue(parseExpected file expectedLine (expectedTokens |> List.tail))
                    else fail file expectedLine.Number 1 "PARSE_MISSING_EXPECTED" "Final test line must be '=> literal' or 'expect literal'; tests may use '=> error CODE' or '=> value <expression>'."
                let bodyLines = meaningful |> List.take (meaningful.Length - 1)
                parseExpressionLines file bodyLines, expected
        match expected with
        | ExpectedRuntimeError _ when List.isEmpty body ->
            fail file header.Number 1 "PARSE_EMPTY_ERROR_TEST_BODY" "A runtime-error test must execute at least one expression before asserting an error."
        | ExpectedExpression _ when List.isEmpty body ->
            fail file header.Number 1 "PARSE_EMPTY_VALUE_EXPECTATION_BODY" "A typed value expectation requires an expression to test."
        | _ -> ()
        pieces[1], pieces[0], body, expected, blockSource lines start finish, span file header.Number 1 header.Text.Length

    /// Parse a complete, line-oriented source document containing records, words, tests, and examples.
    let parse file source =
        try
            let lines = linesForSource source
            let records = ResizeArray<RecordDefinition>()
            let scalars = ResizeArray<ScalarTypeDefinition>()
            let words = ResizeArray<WordDefinition>()
            let tests = ResizeArray<TestDefinition>()
            let examples = ResizeArray<ExampleDefinition>()
            let mutable cursor = 0
            while cursor < lines.Length do
                let content = stripComment lines[cursor].Text |> fun value -> value.Trim()
                if content = "" then cursor <- cursor + 1
                elif content.StartsWith("record ", StringComparison.Ordinal) then
                    let finish = blockEnd file lines cursor
                    let name = parseHeader file lines[cursor] "record"
                    if not (validTypeName name) then fail file lines[cursor].Number 1 "PARSE_INVALID_TYPE_NAME" "Record names must be identifiers and cannot shadow built-in types or reserved type variables."
                    let fields = ResizeArray<RecordField>()
                    let mutable validator = None
                    for index = cursor + 1 to finish - 1 do
                        let line = lines[index]
                        if not (String.IsNullOrWhiteSpace(stripComment line.Text)) then
                            let fieldText = stripComment line.Text |> fun value -> value.Trim()
                            if fieldText.StartsWith("validate ", StringComparison.Ordinal) then
                                if validator.IsSome then fail file line.Number 1 "PARSE_DUPLICATE_RECORD_VALIDATOR" "A record may declare at most one validator."
                                let name = fieldText.Substring("validate".Length).Trim()
                                if not (validWordName name) then fail file line.Number 1 "PARSE_INVALID_RECORD_VALIDATOR" "Record validators use 'validate word.name'."
                                validator <- Some name
                            else
                                let prefixValid =
                                    fieldText.StartsWith("field", StringComparison.Ordinal)
                                    && fieldText.Length > "field".Length
                                    && Char.IsWhiteSpace fieldText["field".Length]
                                if not prefixValid then fail file line.Number 1 "PARSE_INVALID_FIELD" "Record fields use 'field name Type'."
                                let rest = fieldText.Substring("field".Length).Trim()
                                let separator = rest |> Seq.tryFindIndex Char.IsWhiteSpace
                                match separator with
                                | Some index when index > 0 ->
                                    let fieldName = rest.Substring(0, index)
                                    let typeText = rest.Substring(index).Trim()
                                    if not (validWordName fieldName) || typeText = "" then fail file line.Number 1 "PARSE_INVALID_FIELD" "Record fields use 'field name Type' with a valid field name and a closed type."
                                    fields.Add { Name = fieldName; Type = parseType file line.Number typeText }
                                | _ -> fail file line.Number 1 "PARSE_INVALID_FIELD" "Record fields use 'field name Type'."
                    if fields.Count = 0 then fail file lines[cursor].Number 1 "PARSE_EMPTY_RECORD" "A record must declare at least one field."
                    let duplicate = fields |> Seq.groupBy (fun field -> field.Name) |> Seq.tryFind (fun (_, values) -> Seq.length values > 1)
                    if duplicate.IsSome then fail file lines[cursor].Number 1 "PARSE_DUPLICATE_FIELD" "Record field names must be unique."
                    records.Add { Name = name; Fields = List.ofSeq fields; Validator = validator; SourceText = blockSource lines cursor finish; Span = span file lines[cursor].Number 1 lines[cursor].Text.Length }
                    cursor <- finish + 1
                elif content.StartsWith("type ", StringComparison.Ordinal) then
                    let finish = blockEnd file lines cursor
                    let header = lines[cursor]
                    let headerText = stripComment header.Text |> fun value -> value.Trim()
                    let colon = headerText.IndexOf(':')
                    if colon < 0 then fail file header.Number 1 "PARSE_INVALID_SCALAR_TYPE" "Scalar type header uses 'type Name : BaseType'."
                    let name = headerText.Substring("type".Length, colon - "type".Length).Trim()
                    if not (validTypeName name) then fail file header.Number 1 "PARSE_INVALID_TYPE_NAME" "Scalar type names must be identifiers and cannot shadow built-in types or reserved type variables."
                    let baseType = parseType file header.Number (headerText.Substring(colon + 1))
                    if not ([ TInt; TFloat; TString ] |> List.contains baseType) then
                        fail file header.Number 1 "PARSE_UNSUPPORTED_SCALAR_BASE" "Nominal scalar wrappers currently require an Int, Float, or String base type."
                    let bodyLines = if finish > cursor + 1 then lines[cursor + 1 .. finish - 1] else [||]
                    let validator =
                        let declarations = bodyLines |> Array.filter (fun line -> not (String.IsNullOrWhiteSpace(stripComment line.Text)))
                        match declarations with
                        | [||] -> None
                        | [| line |] ->
                            match tokenize file line with
                            | [ directive; word ] when directive.Text = "validate" -> Some word.Text
                            | _ -> fail file line.Number 1 "PARSE_INVALID_SCALAR_VALIDATOR" "Scalar body allows only 'validate word.name'."
                        | _ -> fail file header.Number 1 "PARSE_INVALID_SCALAR_VALIDATOR" "Scalar type allows at most one validator declaration."
                    scalars.Add
                        { Name = name
                          BaseType = baseType
                          Validator = validator
                          SourceText = blockSource lines cursor finish
                          Span = span file header.Number 1 header.Text.Length }
                    cursor <- finish + 1
                elif content.StartsWith("word ", StringComparison.Ordinal) then
                    let finish = blockEnd file lines cursor
                    words.Add(parseWord file lines cursor finish)
                    cursor <- finish + 1
                elif content.StartsWith("test ", StringComparison.Ordinal) then
                    let finish = blockEnd file lines cursor
                    let name, word, body, expected, sourceText, sourceSpan = parseTestLike file lines cursor finish false
                    tests.Add { Name = name; Word = word; Body = body; Expected = expected; EffectAssertion = None; SourceText = sourceText; Span = sourceSpan }
                    cursor <- finish + 1
                elif content.StartsWith("example ", StringComparison.Ordinal) then
                    let finish = blockEnd file lines cursor
                    let name, word, body, expected, sourceText, sourceSpan = parseTestLike file lines cursor finish true
                    match expected with
                    | ExpectedValue literal -> examples.Add { Name = name; Word = word; Body = body; Expected = literal; SourceText = sourceText; Span = sourceSpan }
                    | ExpectedRuntimeError _ -> fail file lines[cursor].Number 1 "PARSE_ERROR_EXPECTATION_NOT_ALLOWED" "Examples must end with a literal value expectation; runtime-error expectations are available only in tests."
                    | ExpectedExpression _ -> fail file lines[cursor].Number 1 "PARSE_VALUE_EXPECTATION_NOT_ALLOWED" "Examples use literal expectations; typed value-expression expectations are available only in tests."
                    cursor <- finish + 1
                else fail file lines[cursor].Number 1 "PARSE_UNKNOWN_DECLARATION" $"Unknown declaration '{content}'."
            Ok ({ Records = List.ofSeq records; Scalars = List.ofSeq scalars; Enums = []; Words = List.ofSeq words; Tests = List.ofSeq tests; Examples = List.ofSeq examples }: ParsedSource)
        with
        | LanguageException diagnostic -> Error diagnostic
        | ex ->
            Error
                { Code = "PARSE_FAILURE"
                  Message = ex.Message
                  Word = None
                  Span = None
                  Expected = []
                  Actual = [] }

    /// Parse standalone REPL code without declaration syntax.
    let parseExpression file source =
        try
            let lines = linesForSource source |> Array.toList
            Ok(parseExpressionLines file lines)
        with
        | LanguageException diagnostic -> Error diagnostic
