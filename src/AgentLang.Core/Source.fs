namespace AgentLang

open System
open System.Globalization
open System.Text.Json

/// Canonical, deterministic rendering and name-aware source transformations.
module Source =
    let private indent depth = String.replicate (depth * 4) " "

    let private quote (value: string) = JsonSerializer.Serialize(value)

    let private formatFloat (value: double) =
        let text = value.ToString("R", CultureInfo.InvariantCulture)
        if text.Contains('.') || text.Contains('e') || text.Contains('E') then text else text + ".0"

    let private renderLiteral = function
        | LInt value -> value.ToString(CultureInfo.InvariantCulture)
        | LFloat value -> formatFloat value
        | LBool true -> "true"
        | LBool false -> "false"
        | LString value -> quote value
        | LUnit -> "unit"

    let private renderConstructor name types =
        name + "<" + (types |> List.map Types.format |> String.concat ", ") + ">"

    let rec private renderExpressionAt depth expression =
        let prefix = indent depth
        match expression with
        | Push(literal, _) -> prefix + renderLiteral literal
        | Call(name, _) -> prefix + name
        | ConstructContainer(kind, types, _) ->
            let name =
                match kind with
                | ListEmpty -> "list.empty"
                | ListSingleton -> "list.singleton"
                | OptionNone -> "option.none"
                | OptionSome -> "option.some"
                | ResultOk -> "result.ok"
                | ResultError -> "result.error"
            prefix + renderConstructor name types
        | MapList(name, _) -> prefix + "list.map " + name
        | FilterList(name, _) -> prefix + "list.filter " + name
        | EachList(name, _) -> prefix + "list.each " + name
        | Let(name, _) -> prefix + "let " + name
        | Load(name, _) -> prefix + "$" + name
        | If(thenBranch, elseBranch, _) ->
            let lines = ResizeArray<string>()
            lines.Add(prefix + "if")
            renderBodyAt (depth + 1) thenBranch |> List.iter lines.Add
            if not (List.isEmpty elseBranch) then
                lines.Add(prefix + "else")
                renderBodyAt (depth + 1) elseBranch |> List.iter lines.Add
            lines.Add(prefix + "end")
            String.concat "\n" lines
        | MatchOption(someName, someBranch, noneBranch, _) ->
            let lines = ResizeArray<string>()
            lines.Add(prefix + "match-option")
            lines.Add(prefix + "some " + someName)
            renderBodyAt (depth + 1) someBranch |> List.iter lines.Add
            lines.Add(prefix + "none")
            renderBodyAt (depth + 1) noneBranch |> List.iter lines.Add
            lines.Add(prefix + "end")
            String.concat "\n" lines
        | MatchResult(okName, errorName, okBranch, errorBranch, _) ->
            let lines = ResizeArray<string>()
            lines.Add(prefix + "match-result")
            lines.Add(prefix + "ok " + okName)
            renderBodyAt (depth + 1) okBranch |> List.iter lines.Add
            lines.Add(prefix + "error " + errorName)
            renderBodyAt (depth + 1) errorBranch |> List.iter lines.Add
            lines.Add(prefix + "end")
            String.concat "\n" lines

    and private renderBodyAt depth body =
        body |> List.map (renderExpressionAt depth)

    /// Render one expression in canonical source form, using four spaces per nested block.
    let renderExpression expression = renderExpressionAt 0 expression

    /// Render a body as LF-separated canonical expressions without a trailing newline.
    let renderBody body = renderBodyAt 0 body |> String.concat "\n"

    let renderRecord (definition: RecordDefinition) =
        let fields =
            definition.Fields
            |> List.map (fun field -> indent 1 + "field " + field.Name + " " + Types.format field.Type)
        String.concat "\n" ([ "record " + definition.Name ] @ fields @ [ "end" ])

    let renderScalar (definition: ScalarTypeDefinition) =
        let body =
            definition.Validator
            |> Option.map (fun validator -> [ indent 1 + "validate " + validator ])
            |> Option.defaultValue []
        String.concat "\n" ([ "type " + definition.Name + " : " + Types.format definition.BaseType ] @ body @ [ "end" ])

    let renderWord includeHostMetadata (definition: WordDefinition) =
        let inputs = definition.Inputs |> List.map Types.format |> String.concat " "
        let outputs = definition.Outputs |> List.map Types.format |> String.concat " "
        let effects =
            if Set.isEmpty definition.Effects then "effects none"
            else "effects " + (definition.Effects |> Set.toList |> String.concat " ")
        let metadata =
            if includeHostMetadata then
                [ "maturity " + (if definition.Maturity = LibraryWord then "library" else "project")
                  "revision " + definition.Revision.ToString(CultureInfo.InvariantCulture) ]
            else []
        let documentation = if String.IsNullOrEmpty definition.Documentation then [] else [ "doc " + quote definition.Documentation ]
        let body = renderBodyAt 1 definition.Body
        String.concat "\n" ([ $"word {definition.Name} : {inputs} -> {outputs}"; indent 1 + effects ] @ (metadata |> List.map (fun line -> indent 1 + line)) @ (documentation |> List.map (fun line -> indent 1 + line)) @ body @ [ "end" ])

    let private renderTestLike keyword name word body expectedLine =
        let bodyLines = renderBodyAt 1 body
        String.concat "\n" ([ $"{keyword} {word}/{name}" ] @ bodyLines @ [ indent 1 + expectedLine; "end" ])

    let renderTest (definition: TestDefinition) =
        let expectedLine =
            match definition.Expected with
            | ExpectedValue literal -> "=> " + renderLiteral literal
            | ExpectedRuntimeError code -> "=> error " + code
        renderTestLike "test" definition.Name definition.Word definition.Body expectedLine

    let renderExample (definition: ExampleDefinition) =
        renderTestLike "example" definition.Name definition.Word definition.Body ("=> " + renderLiteral definition.Expected)

    /// Rewrite only executable word references. Literal values, locals, and metadata are unchanged.
    let rec renameReferences oldName newName expressions =
        let rewrite = renameReferences oldName newName
        expressions
        |> List.map (function
            | Call(name, span) when name = oldName -> Call(newName, span)
            | MapList(name, span) when name = oldName -> MapList(newName, span)
            | FilterList(name, span) when name = oldName -> FilterList(newName, span)
            | EachList(name, span) when name = oldName -> EachList(newName, span)
            | If(thenBranch, elseBranch, span) -> If(rewrite thenBranch, rewrite elseBranch, span)
            | MatchOption(name, someBranch, noneBranch, span) -> MatchOption(name, rewrite someBranch, rewrite noneBranch, span)
            | MatchResult(okName, errorName, okBranch, errorBranch, span) -> MatchResult(okName, errorName, rewrite okBranch, rewrite errorBranch, span)
            | expression -> expression)

    /// Rename a definition header without implicitly changing any body references.
    let renameWordHeader oldName newName (definition: WordDefinition) =
        if definition.Name <> oldName then definition
        else
            let renamed = { definition with Name = newName }
            { renamed with SourceText = renderWord true renamed }

    /// Rename a word and references in its own executable body.
    let renameWordDefinition oldName newName (definition: WordDefinition) =
        let renamed =
            { definition with
                Name = if definition.Name = oldName then newName else definition.Name
                Body = renameReferences oldName newName definition.Body }
        { renamed with SourceText = renderWord true renamed }

    /// Rename a scalar validator reference when it names the renamed word exactly.
    let renameScalarValidator oldName newName (definition: ScalarTypeDefinition) =
        let validator = definition.Validator |> Option.map (fun name -> if name = oldName then newName else name)
        let renamed = { definition with Validator = validator }
        { renamed with SourceText = renderScalar renamed }

    /// Rename the owning word and executable calls in an attached test.
    let renameTestOwner oldName newName (definition: TestDefinition) =
        let renamed =
            { definition with
                Word = if definition.Word = oldName then newName else definition.Word
                Body = renameReferences oldName newName definition.Body }
        { renamed with SourceText = renderTest renamed }

    /// Rename the owning word and executable calls in an attached example.
    let renameExampleOwner oldName newName (definition: ExampleDefinition) =
        let renamed =
            { definition with
                Word = if definition.Word = oldName then newName else definition.Word
                Body = renameReferences oldName newName definition.Body }
        { renamed with SourceText = renderExample renamed }
