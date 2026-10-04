namespace AgentLang

open System
open System.Globalization
open System.Text.Json

module Parser =
    type private Line = { Text: string; Number: int }
    type private Token = { Text: string; Column: int }

    let private reservedWordNames = set [ "if"; "else"; "end"; "let"; "true"; "false"; "unit" ]
    let private reservedTypeNames = set [ "Int"; "Float"; "Bool"; "String"; "Unit"; "a"; "b"; "c" ]

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
                    while index < text.Length && not (Char.IsWhiteSpace text[index]) do index <- index + 1
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

    let private parseType file line (source: string) =
        let text = source.Trim()
        let rec parse (text: string) =
            let value = text.Trim()
            match value with
            | "Int" -> TInt
            | "Float" -> TFloat
            | "Bool" -> TBool
            | "String" -> TString
            | "Unit" -> TUnit
            | _ ->
                let genericStart = value.IndexOf('<')
                if genericStart > 0 && value.EndsWith(">", StringComparison.Ordinal) then
                    fail file line 1 "PARSE_UNSUPPORTED_TYPE" $"Generic type syntax '{value}' is reserved but not implemented in this prototype."
                elif String.IsNullOrWhiteSpace value then fail file line 1 "PARSE_INVALID_TYPE" "Expected a type name."
                else TNamed value
        parse text

    let private parseHeader file (line: Line) (prefix: string) =
        let tokens = tokenize file line
        match tokens with
        | head :: [ name ] when head.Text = prefix -> name.Text
        | _ -> fail file line.Number 1 "PARSE_INVALID_HEADER" $"Expected '{prefix} <name>'."

    let private parseSignature file (line: Line) (source: string) =
        let pieces = source.Split([| "->" |], StringSplitOptions.None)
        if pieces.Length <> 2 then fail file line.Number 1 "PARSE_INVALID_SIGNATURE" "Word signature must use 'Inputs -> Outputs'."
        let parseSide (part: string) =
            part.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.toList
            |> List.map (parseType file line.Number)
        parseSide pieces[0], parseSide pieces[1]

    let private parseOps file (line: Line) =
        let tokens = tokenize file line
        let rec convert (items: Token list) =
            match items with
            | [] -> []
            | token :: rest ->
                match parseLiteral file line.Number token with
                | Some literal -> Push(literal, span file line.Number token.Column token.Text.Length) :: convert rest
                | None when token.Text.StartsWith("$", StringComparison.Ordinal) && token.Text.Length > 1 ->
                    Load(token.Text.Substring(1), span file line.Number token.Column token.Text.Length) :: convert rest
                | None when token.Text = "let" ->
                    match rest with
                    | name :: remaining -> Let(name.Text, span file line.Number token.Column (name.Column + name.Text.Length - token.Column)) :: convert remaining
                    | [] -> fail file line.Number token.Column "PARSE_EXPECTED_LOCAL_NAME" "'let' must be followed by a local name."
                | None -> Call(token.Text, span file line.Number token.Column token.Text.Length) :: convert rest
        convert tokens

    /// Parse expression lines. If blocks use standalone `if`, optional `else`, and `end` lines.
    let private parseExpressionLines file (lines: Line list) =
        let rec parseBlock index allowElse =
            let expressions = ResizeArray<Expr>()
            let mutable cursor = index
            let mutable terminator = "eof"
            let mutable running = true
            while cursor < lines.Length && running do
                let line = lines[cursor]
                let content = stripComment line.Text |> fun value -> value.Trim()
                match content with
                | "else" when allowElse ->
                    terminator <- "else"
                    cursor <- cursor + 1
                    running <- false
                | "end" ->
                    terminator <- "end"
                    cursor <- cursor + 1
                    running <- false
                | "if" ->
                    let thenBranch, afterThen, endKind = parseBlock (cursor + 1) true
                    let elseBranch, afterElse =
                        if endKind = "else" then
                            let branch, next, finalKind = parseBlock afterThen false
                            if finalKind <> "end" then fail file line.Number 1 "PARSE_UNCLOSED_IF" "The if branch is missing its closing 'end'."
                            branch, next
                        elif endKind = "end" then [], afterThen
                        else fail file line.Number 1 "PARSE_UNCLOSED_IF" "The if branch is missing its closing 'end'."
                    expressions.Add(If(thenBranch, elseBranch, span file line.Number 1 line.Text.Length))
                    cursor <- afterElse
                | "else" -> fail file line.Number 1 "PARSE_UNEXPECTED_ELSE" "'else' has no matching 'if'."
                | "" -> cursor <- cursor + 1
                | _ ->
                    for expression in parseOps file line do expressions.Add expression
                    cursor <- cursor + 1
            List.ofSeq expressions, cursor, terminator
        let expressions, next, terminator = parseBlock 0 false
        if terminator = "end" then
            let line = lines[min (next - 1) (lines.Length - 1)]
            fail file line.Number 1 "PARSE_UNEXPECTED_END" "Unexpected 'end'."
        expressions

    let private blockEnd file (lines: Line array) start =
        let mutable depth = 1
        let mutable cursor = start + 1
        while cursor < lines.Length && depth > 0 do
            let content = stripComment lines[cursor].Text |> fun value -> value.Trim()
            if content = "if" then depth <- depth + 1
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
        if List.isEmpty meaningful then fail file header.Number 1 "PARSE_MISSING_EXPECTED" "Test and example blocks require a final '=> literal' line."
        let expectedLine = List.last meaningful
        let expectedText = stripComment expectedLine.Text |> fun value -> value.Trim()
        let expectedTokens = tokenize file expectedLine
        let expected =
            if expectedText.StartsWith("=>", StringComparison.Ordinal) then
                let rest = expectedText.Substring(2).Trim()
                let token = { Text = rest; Column = expectedLine.Text.IndexOf(rest, StringComparison.Ordinal) + 1 }
                parseExpected file expectedLine [ token ]
            elif expectedText.StartsWith("expect ", StringComparison.Ordinal) then
                parseExpected file expectedLine (expectedTokens |> List.tail)
            else fail file expectedLine.Number 1 "PARSE_MISSING_EXPECTED" "Final test line must be '=> literal' or 'expect literal'."
        let bodyLines = meaningful |> List.take (meaningful.Length - 1)
        let body = parseExpressionLines file bodyLines
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
                    for index = cursor + 1 to finish - 1 do
                        let line = lines[index]
                        if not (String.IsNullOrWhiteSpace(stripComment line.Text)) then
                            let tokens = tokenize file line
                            match tokens with
                            | field :: fieldName :: fieldType :: [] when field.Text = "field" ->
                                fields.Add { Name = fieldName.Text; Type = parseType file line.Number fieldType.Text }
                            | _ -> fail file line.Number 1 "PARSE_INVALID_FIELD" "Record fields use 'field name Type'."
                    if fields.Count = 0 then fail file lines[cursor].Number 1 "PARSE_EMPTY_RECORD" "A record must declare at least one field."
                    let duplicate = fields |> Seq.groupBy (fun field -> field.Name) |> Seq.tryFind (fun (_, values) -> Seq.length values > 1)
                    if duplicate.IsSome then fail file lines[cursor].Number 1 "PARSE_DUPLICATE_FIELD" "Record field names must be unique."
                    records.Add { Name = name; Fields = List.ofSeq fields; SourceText = blockSource lines cursor finish; Span = span file lines[cursor].Number 1 lines[cursor].Text.Length }
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
                    tests.Add { Name = name; Word = word; Body = body; Expected = expected; SourceText = sourceText; Span = sourceSpan }
                    cursor <- finish + 1
                elif content.StartsWith("example ", StringComparison.Ordinal) then
                    let finish = blockEnd file lines cursor
                    let name, word, body, expected, sourceText, sourceSpan = parseTestLike file lines cursor finish true
                    examples.Add { Name = name; Word = word; Body = body; Expected = expected; SourceText = sourceText; Span = sourceSpan }
                    cursor <- finish + 1
                else fail file lines[cursor].Number 1 "PARSE_UNKNOWN_DECLARATION" $"Unknown declaration '{content}'."
            Ok { Records = List.ofSeq records; Scalars = List.ofSeq scalars; Words = List.ofSeq words; Tests = List.ofSeq tests; Examples = List.ofSeq examples }
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
