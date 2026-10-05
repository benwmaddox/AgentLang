namespace AgentLang

open System

module Compiler =
    type CheckedExpression =
        { Stack: LangType list
          Dependencies: Set<string>
          Effects: Set<string> }

    type CheckedWord =
        { Definition: WordDefinition
          Dependencies: Set<string>
          InferredEffects: Set<string> }

    let private validateType allowVariables (knownTypes: Set<string>) span word typeValue =
        let rec validate = function
            | TInt | TFloat | TBool | TString | TUnit -> ()
            | TNamed name when knownTypes.Contains name -> ()
            | TNamed name -> Diagnostics.raiseError "TYPE_UNKNOWN_NAMED_TYPE" $"Type '{name}' has not been declared." (Some word) span [ "declared record" ] [ name ]
            | TVar name when allowVariables -> ()
            | TVar name -> Diagnostics.raiseError "TYPE_UNSUPPORTED_GENERIC" $"Generic type variable '{name}' is not supported in this prototype." (Some word) span [] [ name ]
            | TList item | TOption item -> validate item
            | TResult(ok, error) -> validate ok; validate error
        validate typeValue

    let private unify substitutions expected actual =
        let rec resolve (substitutions: Map<string, LangType>) (ty: LangType) =
            match ty with
            | TVar name when substitutions.ContainsKey name -> resolve substitutions substitutions[name]
            | TList item -> TList(resolve substitutions item)
            | TOption item -> TOption(resolve substitutions item)
            | TResult(ok, error) -> TResult(resolve substitutions ok, resolve substitutions error)
            | _ -> ty
        let rec contains (name: string) (ty: LangType) =
            match ty with
            | TVar other -> name = other
            | TList item | TOption item -> contains name item
            | TResult(ok, error) -> contains name ok || contains name error
            | _ -> false
        let rec loop (substitutions: Map<string, LangType>) (expected: LangType) (actual: LangType) =
            let expected = resolve substitutions expected
            let actual = resolve substitutions actual
            let bind (name: string) (ty: LangType) =
                if contains name ty then raise (InvalidOperationException("recursive type"))
                Map.add name ty substitutions
            match expected, actual with
            | TVar name, ty | ty, TVar name -> bind name ty
            | TList left, TList right | TOption left, TOption right -> loop substitutions left right
            | TResult(leftOk, leftError), TResult(rightOk, rightError) ->
                loop (loop substitutions leftOk rightOk) leftError rightError
            | left, right when left = right -> substitutions
            | _ -> raise (InvalidOperationException("type mismatch"))
        loop substitutions expected actual

    let private substitute (substitutions: Map<string, LangType>) typeValue =
        let rec resolve = function
            | TVar name when substitutions.ContainsKey name -> resolve substitutions[name]
            | TList item -> TList(resolve item)
            | TOption item -> TOption(resolve item)
            | TResult(ok, error) -> TResult(resolve ok, resolve error)
            | other -> other
        resolve typeValue

    let private builtinSignature name =
        let a = TVar "a"
        let b = TVar "b"
        match name with
        | "add" | "subtract" | "multiply" | "divide" | "int.min" | "int.max" -> [ TInt; TInt ], [ TInt ]
        | "float.add" | "float.subtract" | "float.multiply" | "float.divide" -> [ TFloat; TFloat ], [ TFloat ]
        | "int.less-than" | "int.greater-than" | "int.less-or-equal" | "int.greater-or-equal" -> [ TInt; TInt ], [ TBool ]
        | "float.less-than" | "float.greater-than" | "float.less-or-equal" | "float.greater-or-equal" -> [ TFloat; TFloat ], [ TBool ]
        | "equals" -> [ a; a ], [ TBool ]
        | "bool.and" | "bool.or" -> [ TBool; TBool ], [ TBool ]
        | "bool.not" -> [ TBool ], [ TBool ]
        | "string.concat" -> [ TString; TString ], [ TString ]
        | "string.contains" | "string.starts-with" | "string.ends-with" -> [ TString; TString ], [ TBool ]
        | "string.length" -> [ TString ], [ TInt ]
        | "string.trim" | "string.to-lower" | "string.to-upper" -> [ TString ], [ TString ]
        | "int.abs" -> [ TInt ], [ TInt ]
        | "int.to-float" -> [ TInt ], [ TFloat ]
        | "float.to-int" | "float.round" -> [ TFloat ], [ TInt ]
        | "int.to-string" -> [ TInt ], [ TString ]
        | "float.to-string" -> [ TFloat ], [ TString ]
        | "list.count" -> [ TList a ], [ TInt ]
        | "list.append" -> [ TList a; a ], [ TList a ]
        | "list.concat" -> [ TList a; TList a ], [ TList a ]
        | "list.get" -> [ TList a; TInt ], [ TOption a ]
        | "list.is-empty?" -> [ TList a ], [ TBool ]
        | "dup" -> [ a ], [ a; a ]
        | "drop" -> [ a ], []
        | "swap" -> [ a; b ], [ b; a ]
        | "file.read" -> [ TString ], [ TString ]
        | "file.write" -> [ TString; TString ], [ TUnit ]
        | "file.exists?" -> [ TString ], [ TBool ]
        | "clock.now" -> [], [ TString ]
        | "console.write" -> [ TString ], [ TUnit ]
        | _ -> [], []

    let private builtinEffects name =
        match name with
        | "file.read" | "file.exists?" -> Set.singleton "fs.read"
        | "file.write" -> Set.singleton "fs.write"
        | "clock.now" -> Set.singleton "clock.read"
        | "console.write" -> Set.singleton "console.write"
        | _ -> Set.empty

    /// The initial standard vocabulary is deliberately small and boring.
    let primitives =
        let names =
            [ "add"; "subtract"; "multiply"; "divide"; "float.add"; "float.subtract"; "float.multiply"; "float.divide"
              "int.less-than"; "int.greater-than"; "int.less-or-equal"; "int.greater-or-equal"
              "float.less-than"; "float.greater-than"; "float.less-or-equal"; "float.greater-or-equal"
              "equals"; "bool.and"; "bool.or"; "bool.not"; "string.concat"; "string.contains"; "string.starts-with"; "string.ends-with"
              "string.length"; "string.trim"; "string.to-lower"; "string.to-upper"; "int.abs"; "int.min"; "int.max"; "int.to-float"; "float.to-int"
              "float.round"; "int.to-string"; "float.to-string"; "list.count"; "list.append"; "list.concat"; "list.get"; "list.is-empty?"
              "dup"; "drop"; "swap"; "file.read"; "file.write"; "file.exists?"; "clock.now"; "console.write" ]
        names
        |> List.map (fun name ->
            let inputs, outputs = builtinSignature name
            let definition =
                { Name = name
                  Inputs = inputs
                  Outputs = outputs
                  Effects = builtinEffects name
                  Maturity = LibraryWord
                  Revision = 1
                  Documentation =
                    match name with
                    | "list.count" -> "Returns the number of elements in List<T>; the closed List<T> parameter is inferred from the input."
                    | "list.append" -> "Appends one value with exactly the element type T to List<T>."
                    | "list.concat" -> "Concatenates two lists with the same exact element type T."
                    | "list.get" -> "Returns Option<T>; out-of-range indices produce a typed none."
                    | "list.is-empty?" -> "Tests whether List<T> contains no elements."
                    | _ when builtinEffects name |> Set.isEmpty -> "Trusted deterministic value operation."
                    | _ -> "Host capability operation; denied unless the host grants its declared effect."
                  Body = []
                  SourceText = ""
                  Span = { File = "<standard>"; Line = 1; Column = 1; Length = name.Length } }
            name,
            { Definition = definition
              Builtin = Some(BuiltinOp name)
              Status = Primitive
              Maturity = LibraryWord
              Revision = 1 })
        |> Map.ofList

    let private inferCall (knownTypes: Set<string>) (words: Map<string, WordEntry>) wordName (span: SourceSpan) (stack: LangType list) =
        match words.TryFind wordName with
        | None -> Diagnostics.raiseError "NAME_UNKNOWN_WORD" $"Word '{wordName}' is not defined." (Some wordName) (Some span) [] []
        | Some entry ->
            let definition = entry.Definition
            for typeValue in definition.Inputs @ definition.Outputs do validateType entry.Builtin.IsSome knownTypes (Some span) definition.Name typeValue
            if stack.Length < definition.Inputs.Length then
                Diagnostics.raiseError "TYPE_STACK_UNDERFLOW" $"'{wordName}' requires {definition.Inputs.Length} value(s), but the stack has {stack.Length}." (Some wordName) (Some span) (definition.Inputs |> List.map Types.format) (stack |> List.map Types.format)
            let args = stack |> List.skip (stack.Length - definition.Inputs.Length)
            let prefix = stack |> List.take (stack.Length - definition.Inputs.Length)
            let mutable substitutions = Map.empty
            try
                for expected, actual in List.zip definition.Inputs args do substitutions <- unify substitutions expected actual
            with _ ->
                Diagnostics.raiseError "TYPE_STACK_MISMATCH" $"Arguments passed to '{wordName}' do not match its signature." (Some wordName) (Some span) (definition.Inputs |> List.map Types.format) (args |> List.map Types.format)
            prefix @ (definition.Outputs |> List.map (substitute substitutions)), entry

    let private higherOrderCall (knownTypes: Set<string>) (words: Map<string, WordEntry>) (wordName: string) (span: SourceSpan) (itemType: LangType) (requiredOutput: LangType option) =
        match words.TryFind wordName with
        | None -> Diagnostics.raiseError "NAME_UNKNOWN_WORD" $"List operation target '{wordName}' is not defined." (Some wordName) (Some span) [] []
        | Some entry ->
            let definition = entry.Definition
            for typeValue in definition.Inputs @ definition.Outputs do validateType entry.Builtin.IsSome knownTypes (Some span) definition.Name typeValue
            if definition.Inputs.Length <> 1 || definition.Outputs.Length <> 1 then
                let inputs = definition.Inputs |> List.map Types.format |> String.concat " "
                let outputs = definition.Outputs |> List.map Types.format |> String.concat " "
                Diagnostics.raiseError "TYPE_LIST_CALLBACK" $"List operation target '{wordName}' must have exactly one input and one output." (Some wordName) (Some span) [ "T -> U" ] [ inputs + " -> " + outputs ]
            let substitutions =
                try unify Map.empty definition.Inputs.Head itemType
                with _ -> Diagnostics.raiseError "TYPE_LIST_CALLBACK" $"List operation target '{wordName}' cannot accept list elements of type {Types.format itemType}." (Some wordName) (Some span) [ Types.format itemType ] (definition.Inputs |> List.map Types.format)
            let actualOutput = substitute substitutions definition.Outputs.Head
            match requiredOutput with
            | Some expected when actualOutput <> expected ->
                Diagnostics.raiseError "TYPE_LIST_CALLBACK" $"List operation target '{wordName}' must return {Types.format expected}." (Some wordName) (Some span) [ Types.format expected ] [ Types.format actualOutput ]
            | _ -> actualOutput, entry

    let listCallbackOutputType knownTypes words wordName span itemType =
        let outputType, _ = higherOrderCall knownTypes words wordName span itemType None
        outputType

    let private inferBody (knownTypes: Set<string>) (words: Map<string, WordEntry>) (wordName: string) (wordSpan: SourceSpan option) (initialStack: LangType list) (initialLocals: Map<string, LangType>) (body: Expr list) =
        let mutable dependencies = Set.empty
        let mutable effects = Set.empty
        let rec visit (stack: LangType list) (locals: Map<string, LangType>) (expressions: Expr list) =
            match expressions with
            | [] -> stack, locals
            | expression :: rest ->
                let nextStack, nextLocals =
                    match expression with
                    | Push(literal, _) -> stack @ [ literal |> Types.literalValue |> Types.ofValue ], locals
                    | Load(name, expressionSpan) ->
                        match locals.TryFind name with
                        | Some typeValue -> stack @ [ typeValue ], locals
                        | None -> Diagnostics.raiseError "NAME_UNKNOWN_LOCAL" $"Local '${name}' has not been bound." (Some wordName) (Some expressionSpan) [] [ name ]
                    | Let(name, expressionSpan) ->
                        if List.isEmpty stack then Diagnostics.raiseError "TYPE_STACK_UNDERFLOW" $"Binding '{name}' requires one stack value." (Some wordName) (Some expressionSpan) [ "value" ] []
                        stack |> List.take (stack.Length - 1), Map.add name (List.last stack) locals
                    | Call(name, expressionSpan) ->
                        let output, entry = inferCall knownTypes words name expressionSpan stack
                        dependencies <- Set.add name dependencies
                        effects <- Set.union effects entry.Definition.Effects
                        output, locals
                    | ConstructContainer(kind, arguments, expressionSpan) ->
                        for typeValue in arguments do validateType false knownTypes (Some expressionSpan) wordName typeValue
                        let requireArity expected =
                            if arguments.Length <> expected then
                                Diagnostics.raiseError "TYPE_CONTAINER_CONSTRUCTOR" "Container constructor has the wrong number of explicit type arguments." (Some wordName) (Some expressionSpan) [ string expected ] [ string arguments.Length ]
                        let consume expectedType resultType =
                            if List.isEmpty stack then
                                Diagnostics.raiseError "TYPE_STACK_UNDERFLOW" "Typed container constructor requires one payload value." (Some wordName) (Some expressionSpan) [ Types.format expectedType ] []
                            let prefix = stack |> List.take (stack.Length - 1)
                            let actual = List.last stack
                            try unify Map.empty expectedType actual |> ignore
                            with _ ->
                                Diagnostics.raiseError "TYPE_CONTAINER_PAYLOAD" "Typed container constructor payload does not match its explicit type argument." (Some wordName) (Some expressionSpan) [ Types.format expectedType ] [ Types.format actual ]
                            prefix @ [ resultType ], locals
                        match kind with
                        | ListEmpty ->
                            requireArity 1
                            stack @ [ TList arguments.Head ], locals
                        | ListSingleton ->
                            requireArity 1
                            consume arguments.Head (TList arguments.Head)
                        | OptionNone ->
                            requireArity 1
                            stack @ [ TOption arguments.Head ], locals
                        | OptionSome ->
                            requireArity 1
                            consume arguments.Head (TOption arguments.Head)
                        | ResultOk ->
                            requireArity 2
                            consume arguments[0] (TResult(arguments[0], arguments[1]))
                        | ResultError ->
                            requireArity 2
                            consume arguments[1] (TResult(arguments[0], arguments[1]))
                    | (MapList(target, expressionSpan) | FilterList(target, expressionSpan) | EachList(target, expressionSpan)) as operation ->
                        if List.isEmpty stack then
                            Diagnostics.raiseError "TYPE_STACK_UNDERFLOW" "List higher-order operation requires a list." (Some wordName) (Some expressionSpan) [ "List<T>" ] []
                        let itemType, prefix =
                            match List.last stack with
                            | TList item -> item, stack |> List.take (stack.Length - 1)
                            | actual -> Diagnostics.raiseError "TYPE_LIST_REQUIRED" "List higher-order operation requires List<T> at the top of the stack." (Some wordName) (Some expressionSpan) [ "List<T>" ] [ Types.format actual ]
                        let requiredOutput =
                            match operation with
                            | MapList _ -> None
                            | FilterList _ -> Some TBool
                            | EachList _ -> Some TUnit
                            | _ -> failwith "unreachable"
                        let callbackOutput, entry = higherOrderCall knownTypes words target expressionSpan itemType requiredOutput
                        dependencies <- Set.add target dependencies
                        effects <- Set.union effects entry.Definition.Effects
                        let output =
                            match operation with
                            | MapList _ -> TList callbackOutput
                            | FilterList _ -> TList itemType
                            | EachList _ -> TUnit
                            | _ -> failwith "unreachable"
                        prefix @ [ output ], locals
                    | If(thenBranch, elseBranch, expressionSpan) ->
                        if List.isEmpty stack || List.last stack <> TBool then
                            let actual = stack |> List.tryLast |> Option.map Types.format |> Option.defaultValue "<empty>"
                            Diagnostics.raiseError "TYPE_IF_REQUIRES_BOOL" "'if' consumes a Bool from the top of the stack." (Some wordName) (Some expressionSpan) [ "Bool" ] [ actual ]
                        let before = stack |> List.take (stack.Length - 1)
                        let thenStack, thenLocals = visit before locals thenBranch
                        let elseStack, elseLocals = visit before locals elseBranch
                        if thenStack <> elseStack then Diagnostics.raiseError "TYPE_BRANCH_STACK_MISMATCH" "Both branches of an if expression must leave the same stack types." (Some wordName) (Some expressionSpan) (thenStack |> List.map Types.format) (elseStack |> List.map Types.format)
                        if thenLocals <> elseLocals then Diagnostics.raiseError "TYPE_BRANCH_LOCAL_MISMATCH" "Both branches of an if expression must bind the same locals with the same types." (Some wordName) (Some expressionSpan) (thenLocals |> Map.toList |> List.map (fun (name, ty) -> $"{name}:{Types.format ty}")) (elseLocals |> Map.toList |> List.map (fun (name, ty) -> $"{name}:{Types.format ty}"))
                        thenStack, thenLocals
                    | MatchOption(someName, someBranch, noneBranch, expressionSpan) ->
                        if List.isEmpty stack then
                            Diagnostics.raiseError "TYPE_MATCH_REQUIRES_OPTION" "match-option consumes an Option<T> from the top of the stack." (Some wordName) (Some expressionSpan) [ "Option<T>" ] []
                        let itemType, before =
                            match List.last stack with
                            | TOption item -> item, stack |> List.take (stack.Length - 1)
                            | actual -> Diagnostics.raiseError "TYPE_MATCH_REQUIRES_OPTION" "match-option consumes an Option<T> from the top of the stack." (Some wordName) (Some expressionSpan) [ "Option<T>" ] [ Types.format actual ]
                        if locals.ContainsKey someName then
                            Diagnostics.raiseError "TYPE_MATCH_LOCAL_SHADOW" $"Match payload local {someName} cannot shadow an existing local." (Some wordName) (Some expressionSpan) [] [ someName ]
                        let someStack, someLocals = visit before (Map.add someName itemType locals) someBranch
                        let someLocals = Map.remove someName someLocals
                        let noneStack, noneLocals = visit before locals noneBranch
                        if someStack <> noneStack then
                            Diagnostics.raiseError "TYPE_MATCH_STACK_MISMATCH" "Both option match cases must leave the same stack types." (Some wordName) (Some expressionSpan) (someStack |> List.map Types.format) (noneStack |> List.map Types.format)
                        if someLocals <> noneLocals then
                            Diagnostics.raiseError "TYPE_MATCH_LOCAL_MISMATCH" "Both option match cases must leave the same outer locals with the same types." (Some wordName) (Some expressionSpan) (someLocals |> Map.toList |> List.map (fun (name, ty) -> $"{name}:{Types.format ty}")) (noneLocals |> Map.toList |> List.map (fun (name, ty) -> $"{name}:{Types.format ty}"))
                        someStack, someLocals
                    | MatchResult(okName, errorName, okBranch, errorBranch, expressionSpan) ->
                        if List.isEmpty stack then
                            Diagnostics.raiseError "TYPE_MATCH_REQUIRES_RESULT" "match-result consumes a Result<T, E> from the top of the stack." (Some wordName) (Some expressionSpan) [ "Result<T, E>" ] []
                        let okType, errorType, before =
                            match List.last stack with
                            | TResult(ok, error) -> ok, error, stack |> List.take (stack.Length - 1)
                            | actual -> Diagnostics.raiseError "TYPE_MATCH_REQUIRES_RESULT" "match-result consumes a Result<T, E> from the top of the stack." (Some wordName) (Some expressionSpan) [ "Result<T, E>" ] [ Types.format actual ]
                        if locals.ContainsKey okName || locals.ContainsKey errorName then
                            Diagnostics.raiseError "TYPE_MATCH_LOCAL_SHADOW" "Result match payload locals cannot shadow existing locals." (Some wordName) (Some expressionSpan) [] [ okName; errorName ]
                        let okStack, okLocals = visit before (Map.add okName okType locals) okBranch
                        let okLocals = Map.remove okName okLocals
                        let errorStack, errorLocals = visit before (Map.add errorName errorType locals) errorBranch
                        let errorLocals = Map.remove errorName errorLocals
                        if okStack <> errorStack then
                            Diagnostics.raiseError "TYPE_MATCH_STACK_MISMATCH" "Both result match cases must leave the same stack types." (Some wordName) (Some expressionSpan) (okStack |> List.map Types.format) (errorStack |> List.map Types.format)
                        if okLocals <> errorLocals then
                            Diagnostics.raiseError "TYPE_MATCH_LOCAL_MISMATCH" "Both result match cases must leave the same outer locals with the same types." (Some wordName) (Some expressionSpan) (okLocals |> Map.toList |> List.map (fun (name, ty) -> $"{name}:{Types.format ty}")) (errorLocals |> Map.toList |> List.map (fun (name, ty) -> $"{name}:{Types.format ty}"))
                        okStack, okLocals
                visit nextStack nextLocals rest
        let finalStack, _ = visit initialStack initialLocals body
        { Stack = finalStack; Dependencies = dependencies; Effects = effects }

    let checkExpression knownTypes words body =
        let inferred = inferBody knownTypes words "<eval>" None [] Map.empty body
        { Stack = inferred.Stack; Dependencies = inferred.Dependencies; Effects = inferred.Effects }

    let checkDefinition knownTypes words definition =
        for typeValue in definition.Inputs @ definition.Outputs do validateType false knownTypes (Some definition.Span) definition.Name typeValue
        let inferred = inferBody knownTypes words definition.Name (Some definition.Span) definition.Inputs Map.empty definition.Body
        if inferred.Stack <> definition.Outputs then
            Diagnostics.raiseError "TYPE_WORD_OUTPUT_MISMATCH" $"Word '{definition.Name}' does not leave its declared output stack." (Some definition.Name) (Some definition.Span) (definition.Outputs |> List.map Types.format) (inferred.Stack |> List.map Types.format)
        let undeclared = Set.difference inferred.Effects definition.Effects
        if not (Set.isEmpty undeclared) then
            let missing = String.concat ", " undeclared
            Diagnostics.raiseError "EFFECT_UNDECLARED" $"Word '{definition.Name}' uses effects absent from its declaration: {missing}." (Some definition.Name) (Some definition.Span) (definition.Effects |> Set.toList) (inferred.Effects |> Set.toList)
        { Definition = definition; Dependencies = inferred.Dependencies; InferredEffects = inferred.Effects }

    let checkTest knownTypes words (test: TestDefinition) =
        let checkedExpression = checkExpression knownTypes words test.Body
        let expectedType = test.Expected |> Types.literalValue |> Types.ofValue
        if checkedExpression.Stack <> [ expectedType ] then
            Diagnostics.raiseError "TEST_EXPECTED_STACK" $"Test '{test.Name}' must leave exactly one value matching its expected literal." (Some test.Word) (Some test.Span) [ Types.format expectedType ] (checkedExpression.Stack |> List.map Types.format)
        checkedExpression

    let checkExample knownTypes words (example: ExampleDefinition) =
        let checkedExpression = checkExpression knownTypes words example.Body
        let expectedType = example.Expected |> Types.literalValue |> Types.ofValue
        if checkedExpression.Stack <> [ expectedType ] then
            Diagnostics.raiseError "EXAMPLE_EXPECTED_STACK" $"Example '{example.Name}' must leave exactly one value matching its expected literal." (Some example.Word) (Some example.Span) [ Types.format expectedType ] (checkedExpression.Stack |> List.map Types.format)
        checkedExpression

    let dependencies (body: Expr list) =
        let rec collect expressions =
            expressions
            |> List.fold (fun found expression ->
                match expression with
                | Call(name, _) -> Set.add name found
                | MapList(name, _) | FilterList(name, _) | EachList(name, _) -> Set.add name found
                | If(thenBranch, elseBranch, _) | MatchOption(_, thenBranch, elseBranch, _) ->
                    Set.union found (Set.union (collect thenBranch) (collect elseBranch))
                | MatchResult(_, _, thenBranch, elseBranch, _) -> Set.union found (Set.union (collect thenBranch) (collect elseBranch))
                | _ -> found) Set.empty
        collect body

    let checkScalarValidator (knownTypes: Set<string>) (words: Map<string, WordEntry>) (scalar: ScalarTypeDefinition) =
        match scalar.BaseType with
        | TString | TInt | TFloat | TBool -> ()
        | _ -> Diagnostics.raiseError "TYPE_UNSUPPORTED_SCALAR_BASE" "Nominal scalar wrappers currently require a primitive String, Int, Float, or Bool base type." (Some scalar.Name) (Some scalar.Span) [ "String | Int | Float | Bool" ] [ Types.format scalar.BaseType ]
        match scalar.Validator with
        | None -> Set.empty
        | Some name ->
            match words.TryFind name with
            | None -> Diagnostics.raiseError "TYPE_UNKNOWN_VALIDATOR" $"Validator word '{name}' is not defined." (Some scalar.Name) (Some scalar.Span) [ "known word" ] [ name ]
            | Some entry ->
                let definition = entry.Definition
                validateType entry.Builtin.IsSome knownTypes (Some scalar.Span) scalar.Name (TNamed scalar.Name)
                if definition.Inputs <> [ scalar.BaseType ] || definition.Outputs <> [ TBool ] then
                    let actualInputs = String.concat " " (definition.Inputs |> List.map Types.format)
                    let actualOutputs = String.concat " " (definition.Outputs |> List.map Types.format)
                    Diagnostics.raiseError "TYPE_VALIDATOR_SIGNATURE" $"Validator '{name}' must have signature {Types.format scalar.BaseType} -> Bool." (Some scalar.Name) (Some scalar.Span) [ $"{Types.format scalar.BaseType} -> Bool" ] [ $"{actualInputs} -> {actualOutputs}" ]
                if not (Set.isEmpty definition.Effects) then
                    Diagnostics.raiseError "TYPE_VALIDATOR_EFFECT" "Scalar validators must be pure." (Some scalar.Name) (Some scalar.Span) [] (Set.toList definition.Effects)
                dependencies definition.Body

    let rec sourceExpressions = function
        | [] -> ""
        | expression :: rest ->
            let current =
                match expression with
                | Push(literal, _) -> Types.formatValue (Types.literalValue literal)
                | Call(name, _) -> name
                | ConstructContainer(kind, types, _) ->
                    let name =
                        match kind with
                        | ListEmpty -> "list.empty"
                        | ListSingleton -> "list.singleton"
                        | OptionNone -> "option.none"
                        | OptionSome -> "option.some"
                        | ResultOk -> "result.ok"
                        | ResultError -> "result.error"
                    name + "<" + (types |> List.map Types.format |> String.concat ", ") + ">"
                | MapList(name, _) -> "list.map " + name
                | FilterList(name, _) -> "list.filter " + name
                | EachList(name, _) -> "list.each " + name
                | Let(name, _) -> "let " + name
                | Load(name, _) -> $"${name}"
                | If(thenBranch, elseBranch, _) ->
                    let thenText = sourceExpressions thenBranch
                    let elseText = sourceExpressions elseBranch
                    if List.isEmpty elseBranch then "if\n" + thenText + "\nend" else "if\n" + thenText + "\nelse\n" + elseText + "\nend"
                | MatchOption(name, someBranch, noneBranch, _) ->
                    "match-option\nsome " + name + "\n" + sourceExpressions someBranch + "\nnone\n" + sourceExpressions noneBranch + "\nend"
                | MatchResult(okName, errorName, okBranch, errorBranch, _) ->
                    "match-result\nok " + okName + "\n" + sourceExpressions okBranch + "\nerror " + errorName + "\n" + sourceExpressions errorBranch + "\nend"
            String.concat "\n" [ current; sourceExpressions rest ] |> fun text -> text.Trim('\n')
