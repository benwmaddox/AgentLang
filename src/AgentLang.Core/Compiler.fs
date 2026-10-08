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
        | "string.guid-canonical?" | "string.email-address-valid?" | "instant.is-canonical-utc?" -> [ TString ], [ TBool ]
        | "string.guid-normalize" | "instant.parse-utc" -> [ TString ], [ TResult(TString, TString) ]
        | "int.add-checked" | "int.multiply-checked" -> [ TInt; TInt ], [ TResult(TInt, TString) ]
        | "int.scale-ratio-toward-zero" -> [ TInt; TInt; TInt ], [ TResult(TInt, TString) ]
        | "instant.before?" -> [ TString; TString ], [ TBool ]
        | "instant.add-days" -> [ TString; TInt ], [ TResult(TString, TString) ]
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
        | "list.tail" -> [ TList a ], [ TList a ]
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
              "equals"; "bool.and"; "bool.or"; "bool.not"
              "string.guid-canonical?"; "string.guid-normalize"; "string.email-address-valid?"
              "int.add-checked"; "int.multiply-checked"; "int.scale-ratio-toward-zero"
              "instant.parse-utc"; "instant.is-canonical-utc?"; "instant.before?"; "instant.add-days"
              "string.concat"; "string.contains"; "string.starts-with"; "string.ends-with"
              "string.length"; "string.trim"; "string.to-lower"; "string.to-upper"; "int.abs"; "int.min"; "int.max"; "int.to-float"; "float.to-int"
              "float.round"; "int.to-string"; "float.to-string"; "list.count"; "list.tail"; "list.append"; "list.concat"; "list.get"; "list.is-empty?"
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
                    | "string.guid-canonical?" -> "Returns true only for lower-case canonical D-format GUID text."
                    | "string.guid-normalize" -> "Accepts Guid.TryParse D, N, B, P, and X forms with surrounding whitespace; returns lower-case D-format text, or INVALID_GUID."
                    | "string.email-address-valid?" -> "Checks an ASCII address policy: local segments use letters, digits, underscore, percent, plus, or hyphen and are separated by single dots with no empty segments; the domain has at least two labels with alphanumeric ends and internal hyphens. Rejects whitespace and values over 254 characters."
                    | "int.add-checked" -> "Adds Int64 values exactly; returns INT_OVERFLOW on overflow."
                    | "int.multiply-checked" -> "Multiplies Int64 values exactly; returns INT_OVERFLOW on overflow."
                    | "int.scale-ratio-toward-zero" -> "Computes (value * numerator) / denominator with an exact Int128 intermediate for the full signed Int64 input range, truncating the quotient toward zero. Returns DIVIDE_BY_ZERO for a zero denominator and INT_OVERFLOW when the final quotient is outside Int64."
                    | "instant.parse-utc" -> "Parses invariant ISO yyyy-MM-ddTHH:mm:ss text with optional one-to-seven fractional digits and an explicit Z, z, or signed HH:mm offset; returns UTC round-trip text with seven fractional digits and +00:00. Unzoned or non-ISO input returns INVALID_INSTANT."
                    | "instant.is-canonical-utc?" -> "Returns true only for the exact UTC O-format text emitted by instant.parse-utc."
                    | "instant.before?" -> "Compares two canonical UTC O-format instants; equality is false and invalid operands raise RUNTIME_INVALID_INSTANT."
                    | "instant.add-days" -> "Adds an integral Int64 day count to a canonical UTC O-format instant; invalid input returns INVALID_INSTANT and range failures return INSTANT_RANGE."
                    | "list.count" -> "Returns the number of elements in List<T>; the closed List<T> parameter is inferred from the input."
                    | "list.tail" -> "Returns List<T> without its first element; empty lists remain empty, the element type and order are preserved, and the input is unchanged."
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
            let callOutputs = definition.Outputs |> List.map (substitute substitutions)
            prefix @ callOutputs, entry, args, callOutputs

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

    let private higherOrderFoldCall (knownTypes: Set<string>) (words: Map<string, WordEntry>) (wordName: string) (span: SourceSpan) (itemType: LangType) (accumulatorType: LangType) =
        match words.TryFind wordName with
        | None -> Diagnostics.raiseError "NAME_UNKNOWN_WORD" $"List fold target '{wordName}' is not defined." (Some wordName) (Some span) [] []
        | Some entry ->
            let definition = entry.Definition
            for typeValue in definition.Inputs @ definition.Outputs do
                validateType entry.Builtin.IsSome knownTypes (Some span) definition.Name typeValue
            if definition.Inputs.Length <> 2 || definition.Outputs.Length <> 1 then
                let inputs = definition.Inputs |> List.map Types.format |> String.concat " "
                let outputs = definition.Outputs |> List.map Types.format |> String.concat " "
                Diagnostics.raiseError "TYPE_LIST_FOLD_CALLBACK" $"List fold target '{wordName}' must have exactly two inputs and one output." (Some wordName) (Some span)
                    [ $"{Types.format accumulatorType} {Types.format itemType} -> {Types.format accumulatorType}" ] [ inputs + " -> " + outputs ]
            let substitutions =
                try
                    let accumulatorSubstitutions = unify Map.empty definition.Inputs[0] accumulatorType
                    unify accumulatorSubstitutions definition.Inputs[1] itemType
                with _ ->
                    Diagnostics.raiseError "TYPE_LIST_FOLD_CALLBACK" $"List fold target '{wordName}' must accept accumulator {Types.format accumulatorType} followed by item {Types.format itemType}." (Some wordName) (Some span)
                        [ $"{Types.format accumulatorType} {Types.format itemType} -> {Types.format accumulatorType}" ]
                        (definition.Inputs |> List.map Types.format)
            let actualOutput = substitute substitutions definition.Outputs.Head
            if actualOutput <> accumulatorType then
                Diagnostics.raiseError "TYPE_LIST_FOLD_CALLBACK" $"List fold target '{wordName}' must return the exact accumulator type {Types.format accumulatorType}." (Some wordName) (Some span)
                    [ Types.format accumulatorType ] [ Types.format actualOutput ]
            entry

    let listCallbackOutputType knownTypes words wordName span itemType =
        let outputType, _ = higherOrderCall knownTypes words wordName span itemType None
        outputType

    type private TypedCallSignature =
        { CallInputs: LangType list
          CallOutputs: LangType list }

    type private TypedNode =
        { SourceExpression: Expr
          InputStack: LangType list
          OutputStack: LangType list
          InputLocals: Map<string, LangType>
          OutputLocals: Map<string, LangType>
          CallSignature: TypedCallSignature option
          ChildBodies: TypedBody list }

    and private TypedBody =
        { EntryStack: LangType list
          ExitStack: LangType list
          EntryLocals: Map<string, LangType>
          ExitLocals: Map<string, LangType>
          Nodes: TypedNode list }

    type private BodyInference =
        { InferredBody: TypedBody
          Dependencies: Set<string>
          Effects: Set<string> }

    let private inferBody (knownTypes: Set<string>) (knownEnums: Map<string, EnumDefinition>) (words: Map<string, WordEntry>) (wordName: string) (wordSpan: SourceSpan option) (initialStack: LangType list) (initialLocals: Map<string, LangType>) (body: Expr list) =
        let mutable dependencies = Set.empty
        let mutable effects = Set.empty
        let rec visit (entryStack: LangType list) (entryLocals: Map<string, LangType>) (expressions: Expr list) =
            let mutable stack = entryStack
            let mutable locals = entryLocals
            let nodes = ResizeArray<TypedNode>()
            for expression in expressions do
                let inputStack = stack
                let inputLocals = locals
                let mutable callSignature = None
                let mutable childBodies = []
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
                        let output, entry, callInputs, callOutputs = inferCall knownTypes words name expressionSpan stack
                        callSignature <- Some { CallInputs = callInputs; CallOutputs = callOutputs }
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
                        callSignature <- Some { CallInputs = [ itemType ]; CallOutputs = [ callbackOutput ] }
                        dependencies <- Set.add target dependencies
                        effects <- Set.union effects entry.Definition.Effects
                        let output =
                            match operation with
                            | MapList _ -> TList callbackOutput
                            | FilterList _ -> TList itemType
                            | EachList _ -> TUnit
                            | _ -> failwith "unreachable"
                        prefix @ [ output ], locals
                    | FoldList(target, expressionSpan) ->
                        if stack.Length < 2 then
                            Diagnostics.raiseError "TYPE_STACK_UNDERFLOW" "List fold requires a List<T> followed by an initial accumulator." (Some wordName) (Some expressionSpan)
                                [ "List<T> Accumulator" ] (stack |> List.map Types.format)
                        let prefix = stack |> List.take (stack.Length - 2)
                        let actualInputs = stack |> List.skip (stack.Length - 2)
                        let itemType, accumulatorType =
                            match actualInputs with
                            | [ TList item; accumulator ] -> item, accumulator
                            | [ actualList; accumulator ] ->
                                Diagnostics.raiseError "TYPE_LIST_REQUIRED" "List fold requires List<T> below its initial accumulator." (Some wordName) (Some expressionSpan)
                                    [ "List<T> Accumulator" ] [ Types.format actualList; Types.format accumulator ]
                            | _ -> failwith "unreachable"
                        let entry = higherOrderFoldCall knownTypes words target expressionSpan itemType accumulatorType
                        callSignature <- Some { CallInputs = [ accumulatorType; itemType ]; CallOutputs = [ accumulatorType ] }
                        dependencies <- Set.add target dependencies
                        effects <- Set.union effects entry.Definition.Effects
                        prefix @ [ accumulatorType ], locals
                    | If(thenBranch, elseBranch, expressionSpan) ->
                        if List.isEmpty stack || List.last stack <> TBool then
                            let actual = stack |> List.tryLast |> Option.map Types.format |> Option.defaultValue "<empty>"
                            Diagnostics.raiseError "TYPE_IF_REQUIRES_BOOL" "'if' consumes a Bool from the top of the stack." (Some wordName) (Some expressionSpan) [ "Bool" ] [ actual ]
                        let before = stack |> List.take (stack.Length - 1)
                        let thenBody = visit before locals thenBranch
                        let elseBody = visit before locals elseBranch
                        if thenBody.ExitStack <> elseBody.ExitStack then Diagnostics.raiseError "TYPE_BRANCH_STACK_MISMATCH" "Both branches of an if expression must leave the same stack types." (Some wordName) (Some expressionSpan) (thenBody.ExitStack |> List.map Types.format) (elseBody.ExitStack |> List.map Types.format)
                        if thenBody.ExitLocals <> elseBody.ExitLocals then Diagnostics.raiseError "TYPE_BRANCH_LOCAL_MISMATCH" "Both branches of an if expression must bind the same locals with the same types." (Some wordName) (Some expressionSpan) (thenBody.ExitLocals |> Map.toList |> List.map (fun (name, ty) -> $"{name}:{Types.format ty}")) (elseBody.ExitLocals |> Map.toList |> List.map (fun (name, ty) -> $"{name}:{Types.format ty}"))
                        childBodies <- [ thenBody; elseBody ]
                        thenBody.ExitStack, thenBody.ExitLocals
                    | Scope(innerBody, _) ->
                        let scopedBody = visit stack locals innerBody
                        childBodies <- [ scopedBody ]
                        scopedBody.ExitStack, locals
                    | MatchOption(someName, someBranch, noneBranch, expressionSpan) ->
                        if List.isEmpty stack then
                            Diagnostics.raiseError "TYPE_MATCH_REQUIRES_OPTION" "match-option consumes an Option<T> from the top of the stack." (Some wordName) (Some expressionSpan) [ "Option<T>" ] []
                        let itemType, before =
                            match List.last stack with
                            | TOption item -> item, stack |> List.take (stack.Length - 1)
                            | actual -> Diagnostics.raiseError "TYPE_MATCH_REQUIRES_OPTION" "match-option consumes an Option<T> from the top of the stack." (Some wordName) (Some expressionSpan) [ "Option<T>" ] [ Types.format actual ]
                        if locals.ContainsKey someName then
                            Diagnostics.raiseError "TYPE_MATCH_LOCAL_SHADOW" $"Match payload local {someName} cannot shadow an existing local." (Some wordName) (Some expressionSpan) [] [ someName ]
                        let someBody = visit before (Map.add someName itemType locals) someBranch
                        let someLocals = Map.remove someName someBody.ExitLocals
                        let noneBody = visit before locals noneBranch
                        if someBody.ExitStack <> noneBody.ExitStack then
                            Diagnostics.raiseError "TYPE_MATCH_STACK_MISMATCH" "Both option match cases must leave the same stack types." (Some wordName) (Some expressionSpan) (someBody.ExitStack |> List.map Types.format) (noneBody.ExitStack |> List.map Types.format)
                        if someLocals <> noneBody.ExitLocals then
                            Diagnostics.raiseError "TYPE_MATCH_LOCAL_MISMATCH" "Both option match cases must leave the same outer locals with the same types." (Some wordName) (Some expressionSpan) (someLocals |> Map.toList |> List.map (fun (name, ty) -> $"{name}:{Types.format ty}")) (noneBody.ExitLocals |> Map.toList |> List.map (fun (name, ty) -> $"{name}:{Types.format ty}"))
                        childBodies <- [ someBody; noneBody ]
                        someBody.ExitStack, someLocals
                    | MatchResult(okName, errorName, okBranch, errorBranch, expressionSpan) ->
                        if List.isEmpty stack then
                            Diagnostics.raiseError "TYPE_MATCH_REQUIRES_RESULT" "match-result consumes a Result<T, E> from the top of the stack." (Some wordName) (Some expressionSpan) [ "Result<T, E>" ] []
                        let okType, errorType, before =
                            match List.last stack with
                            | TResult(ok, error) -> ok, error, stack |> List.take (stack.Length - 1)
                            | actual -> Diagnostics.raiseError "TYPE_MATCH_REQUIRES_RESULT" "match-result consumes a Result<T, E> from the top of the stack." (Some wordName) (Some expressionSpan) [ "Result<T, E>" ] [ Types.format actual ]
                        if locals.ContainsKey okName || locals.ContainsKey errorName then
                            Diagnostics.raiseError "TYPE_MATCH_LOCAL_SHADOW" "Result match payload locals cannot shadow existing locals." (Some wordName) (Some expressionSpan) [] [ okName; errorName ]
                        let okBody = visit before (Map.add okName okType locals) okBranch
                        let okLocals = Map.remove okName okBody.ExitLocals
                        let errorBody = visit before (Map.add errorName errorType locals) errorBranch
                        let errorLocals = Map.remove errorName errorBody.ExitLocals
                        if okBody.ExitStack <> errorBody.ExitStack then
                            Diagnostics.raiseError "TYPE_MATCH_STACK_MISMATCH" "Both result match cases must leave the same stack types." (Some wordName) (Some expressionSpan) (okBody.ExitStack |> List.map Types.format) (errorBody.ExitStack |> List.map Types.format)
                        if okLocals <> errorLocals then
                            Diagnostics.raiseError "TYPE_MATCH_LOCAL_MISMATCH" "Both result match cases must leave the same outer locals with the same types." (Some wordName) (Some expressionSpan) (okLocals |> Map.toList |> List.map (fun (name, ty) -> $"{name}:{Types.format ty}")) (errorLocals |> Map.toList |> List.map (fun (name, ty) -> $"{name}:{Types.format ty}"))
                        childBodies <- [ okBody; errorBody ]
                        okBody.ExitStack, okLocals
                    | MatchEnum(cases, expressionSpan) ->
                        if List.isEmpty stack then
                            Diagnostics.raiseError "TYPE_MATCH_REQUIRES_ENUM" "Enum match consumes a declared enum value from the top of the stack." (Some wordName) (Some expressionSpan) [ "enum value" ] []
                        let enumName =
                            match List.last stack with
                            | TNamed name when knownEnums.ContainsKey name -> name
                            | TNamed name -> Diagnostics.raiseError "TYPE_MATCH_REQUIRES_ENUM" "Enum match scrutinee must have a declared enum type." (Some wordName) (Some expressionSpan) [ "declared enum" ] [ name ]
                            | actual -> Diagnostics.raiseError "TYPE_MATCH_REQUIRES_ENUM" "Enum match scrutinee must have a declared enum type." (Some wordName) (Some expressionSpan) [ "declared enum" ] [ Types.format actual ]
                        let declaredCases = knownEnums[enumName].Cases
                        let authoredCases = cases |> List.map fst
                        let duplicateCases = authoredCases |> List.groupBy id |> List.choose (fun (name, values) -> if values.Length > 1 then Some name else None)
                        let unknownCases = authoredCases |> List.filter (fun name -> not (List.contains name declaredCases)) |> List.distinct
                        let missingCases = declaredCases |> List.filter (fun name -> not (List.contains name authoredCases))
                        if not duplicateCases.IsEmpty || not unknownCases.IsEmpty || not missingCases.IsEmpty then
                            Diagnostics.raiseError "TYPE_MATCH_ENUM_CASE_SET" "Enum match must contain every declared case exactly once and no other labels." (Some wordName) (Some expressionSpan)
                                declaredCases ((authoredCases |> List.distinct) @ (duplicateCases |> List.map (fun name -> "duplicate:" + name)) @ (unknownCases |> List.map (fun name -> "extra:" + name)) @ (missingCases |> List.map (fun name -> "missing:" + name)))
                        let before = stack |> List.take (stack.Length - 1)
                        let branchBodies = cases |> List.map (fun (_, branch) -> visit before locals branch)
                        match branchBodies with
                        | [] -> Diagnostics.raiseError "TYPE_MATCH_ENUM_CASE_SET" "Enum match must contain at least one case." (Some wordName) (Some expressionSpan) declaredCases []
                        | firstBranch :: remainingBranches ->
                            for branch in remainingBranches do
                                if branch.ExitStack <> firstBranch.ExitStack then
                                    Diagnostics.raiseError "TYPE_MATCH_STACK_MISMATCH" "Every enum match case must leave the same output stack types." (Some wordName) (Some expressionSpan) (firstBranch.ExitStack |> List.map Types.format) (branch.ExitStack |> List.map Types.format)
                                if branch.ExitLocals <> firstBranch.ExitLocals then
                                    Diagnostics.raiseError "TYPE_MATCH_LOCAL_MISMATCH" "Every enum match case must leave the same outer locals with the same types." (Some wordName) (Some expressionSpan)
                                        (firstBranch.ExitLocals |> Map.toList |> List.map (fun (name, ty) -> $"{name}:{Types.format ty}"))
                                        (branch.ExitLocals |> Map.toList |> List.map (fun (name, ty) -> $"{name}:{Types.format ty}"))
                            childBodies <- branchBodies
                            firstBranch.ExitStack, firstBranch.ExitLocals
                nodes.Add
                    { SourceExpression = expression
                      InputStack = inputStack
                      OutputStack = nextStack
                      InputLocals = inputLocals
                      OutputLocals = nextLocals
                      CallSignature = callSignature
                      ChildBodies = childBodies }
                stack <- nextStack
                locals <- nextLocals
            { EntryStack = entryStack
              ExitStack = stack
              EntryLocals = entryLocals
              ExitLocals = locals
              Nodes = List.ofSeq nodes }
        { InferredBody = visit initialStack initialLocals body
          Dependencies = dependencies
          Effects = effects }

    let private checkedExpression (inferred: BodyInference) =
        { Stack = inferred.InferredBody.ExitStack
          Dependencies = inferred.Dependencies
          Effects = inferred.Effects }

    let checkExpression knownTypes words body =
        inferBody knownTypes Map.empty words "<eval>" None [] Map.empty body |> checkedExpression

    let checkExpressionWithEnums knownTypes enums words body =
        inferBody knownTypes enums words "<eval>" None [] Map.empty body |> checkedExpression

    let private checkDefinitionDetailed knownTypes knownEnums words definition =
        for typeValue in definition.Inputs @ definition.Outputs do validateType false knownTypes (Some definition.Span) definition.Name typeValue
        let inferred = inferBody knownTypes knownEnums words definition.Name (Some definition.Span) definition.Inputs Map.empty definition.Body
        if inferred.InferredBody.ExitStack <> definition.Outputs then
            Diagnostics.raiseError "TYPE_WORD_OUTPUT_MISMATCH" $"Word '{definition.Name}' does not leave its declared output stack." (Some definition.Name) (Some definition.Span) (definition.Outputs |> List.map Types.format) (inferred.InferredBody.ExitStack |> List.map Types.format)
        let undeclared = Set.difference inferred.Effects definition.Effects
        if not (Set.isEmpty undeclared) then
            let missing = String.concat ", " undeclared
            Diagnostics.raiseError "EFFECT_UNDECLARED" $"Word '{definition.Name}' uses effects absent from its declaration: {missing}." (Some definition.Name) (Some definition.Span) (definition.Effects |> Set.toList) (inferred.Effects |> Set.toList)
        { Definition = definition; Dependencies = inferred.Dependencies; InferredEffects = inferred.Effects }, inferred.InferredBody

    let checkDefinition knownTypes words definition =
        checkDefinitionDetailed knownTypes Map.empty words definition |> fst

    let checkDefinitionWithEnums knownTypes enums words definition =
        checkDefinitionDetailed knownTypes enums words definition |> fst

    let private checkTestDetailed knownTypes knownEnums words (test: TestDefinition) =
        match test.Expected with
        | ExpectedRuntimeError _ when List.isEmpty test.Body ->
            Diagnostics.raiseError "TEST_EXPECTED_ERROR_BODY_EMPTY" $"Runtime-error test '{test.Name}' must contain an expression to execute." (Some test.Word) (Some test.Span) [ "nonempty test body" ] []
        | ExpectedRuntimeError code when not (TestExpectation.isValidRuntimeErrorCode code) ->
            Diagnostics.raiseError "TEST_INVALID_EXPECTED_ERROR_CODE" $"Runtime-error test '{test.Name}' uses an invalid diagnostic code." (Some test.Word) (Some test.Span) [ "[A-Z][A-Z0-9_]*" ] [ code ]
        | ExpectedExpression _ when List.isEmpty test.Body ->
            Diagnostics.raiseError "TEST_EXPECTED_VALUE_BODY_EMPTY" $"Value-expectation test '{test.Name}' must contain an expression to test." (Some test.Word) (Some test.Span) [ "nonempty test body" ] []
        | _ -> ()
        let inferred = inferBody knownTypes knownEnums words (test.Word + "/" + test.Name) (Some test.Span) [] Map.empty test.Body
        let checkedExpression = checkedExpression inferred
        let expectedInference =
            match test.Expected with
            | ExpectedExpression expressions ->
                if List.isEmpty expressions then
                    Diagnostics.raiseError "TEST_EXPECTED_VALUE_EMPTY" $"Value expectation in test '{test.Name}' must contain an expression." (Some test.Word) (Some test.Span) [ "nonempty expected expression" ] []
                let expected = inferBody knownTypes knownEnums words (test.Word + "/" + test.Name + "/expected") (Some test.Span) [] Map.empty expressions
                match expected.InferredBody.ExitStack with
                | [ expectedType ] ->
                    validateType false knownTypes (Some test.Span) (test.Word + "/" + test.Name + "/expected") expectedType
                    if not (Set.isEmpty expected.Effects) then
                        Diagnostics.raiseError "TEST_EXPECTED_VALUE_EFFECTS" $"Value expectation in test '{test.Name}' must be pure." (Some test.Word) (Some test.Span) [] (Set.toList expected.Effects)
                    if checkedExpression.Stack <> [ expectedType ] then
                        Diagnostics.raiseError "TEST_EXPECTED_STACK" $"Test '{test.Name}' and its value expectation must each leave exactly one value of the same closed type." (Some test.Word) (Some test.Span) [ Types.format expectedType ] (checkedExpression.Stack |> List.map Types.format)
                | actual ->
                    Diagnostics.raiseError "TEST_EXPECTED_VALUE_STACK" $"Value expectation in test '{test.Name}' must leave exactly one value." (Some test.Word) (Some test.Span) [ "one value" ] (actual |> List.map Types.format)
                Some expected
            | _ -> None
        match test.Expected with
        | ExpectedValue literal ->
            let expectedType = literal |> Types.literalValue |> Types.ofValue
            if checkedExpression.Stack <> [ expectedType ] then
                Diagnostics.raiseError "TEST_EXPECTED_STACK" $"Test '{test.Name}' must leave exactly one value matching its expected literal." (Some test.Word) (Some test.Span) [ Types.format expectedType ] (checkedExpression.Stack |> List.map Types.format)
        | ExpectedRuntimeError _ -> ()
        | ExpectedExpression _ -> ()
        checkedExpression, inferred.InferredBody, expectedInference

    let checkTest knownTypes words test = checkTestDetailed knownTypes Map.empty words test |> fun (checkedExpression, _, _) -> checkedExpression

    let checkTestWithEnums knownTypes enums words test = checkTestDetailed knownTypes enums words test |> fun (checkedExpression, _, _) -> checkedExpression

    let private checkExampleDetailed knownTypes knownEnums words (example: ExampleDefinition) =
        let inferred = inferBody knownTypes knownEnums words (example.Word + "/" + example.Name) (Some example.Span) [] Map.empty example.Body
        let checkedExpression = checkedExpression inferred
        let expectedType = example.Expected |> Types.literalValue |> Types.ofValue
        if checkedExpression.Stack <> [ expectedType ] then
            Diagnostics.raiseError "EXAMPLE_EXPECTED_STACK" $"Example '{example.Name}' must leave exactly one value matching its expected literal." (Some example.Word) (Some example.Span) [ Types.format expectedType ] (checkedExpression.Stack |> List.map Types.format)
        checkedExpression, inferred.InferredBody

    let checkExample knownTypes words example = checkExampleDetailed knownTypes Map.empty words example |> fst

    let checkExampleWithEnums knownTypes enums words example = checkExampleDetailed knownTypes enums words example |> fst

    let dependencies (body: Expr list) =
        let rec collect expressions =
            expressions
            |> List.fold (fun found expression ->
                match expression with
                | Call(name, _) -> Set.add name found
                | MapList(name, _) | FilterList(name, _) | EachList(name, _) | FoldList(name, _) -> Set.add name found
                | If(thenBranch, elseBranch, _) | MatchOption(_, thenBranch, elseBranch, _) ->
                    Set.union found (Set.union (collect thenBranch) (collect elseBranch))
                | Scope(innerBody, _) -> Set.union found (collect innerBody)
                    | MatchResult(_, _, thenBranch, elseBranch, _) -> Set.union found (Set.union (collect thenBranch) (collect elseBranch))
                    | MatchEnum(cases, _) -> cases |> List.fold (fun found (_, branch) -> Set.union found (collect branch)) found
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
                | FoldList(name, _) -> "list.fold " + name
                | Let(name, _) -> "let " + name
                | Load(name, _) -> $"${name}"
                | If(thenBranch, elseBranch, _) ->
                    let thenText = sourceExpressions thenBranch
                    let elseText = sourceExpressions elseBranch
                    if List.isEmpty elseBranch then "if\n" + thenText + "\nend" else "if\n" + thenText + "\nelse\n" + elseText + "\nend"
                | Scope(innerBody, _) -> "scope\n" + sourceExpressions innerBody + "\nend"
                | MatchOption(name, someBranch, noneBranch, _) ->
                    "match-option\nsome " + name + "\n" + sourceExpressions someBranch + "\nnone\n" + sourceExpressions noneBranch + "\nend"
                | MatchResult(okName, errorName, okBranch, errorBranch, _) ->
                    "match-result\nok " + okName + "\n" + sourceExpressions okBranch + "\nerror " + errorName + "\n" + sourceExpressions errorBranch + "\nend"
                | MatchEnum(cases, _) ->
                    "match-enum\n" + (cases |> List.map (fun (caseName, body) -> caseName + "\n" + sourceExpressions body) |> String.concat "\n") + "\nend"
            String.concat "\n" [ current; sourceExpressions rest ] |> fun text -> text.Trim('\n')

    /// Complete source snapshot used by compiler lowering. Every effective
    /// dictionary word, including primitives and generated words, has an ID.
    type IrLoweringContext =
        { Words: Map<string, WordEntry>
          Records: Map<string, RecordDefinition>
          Scalars: Map<string, ScalarTypeDefinition>
          Enums: Map<string, EnumDefinition>
          WordIds: Map<string, WordId> }

    let private irFailure code message word span expected actual =
        Diagnostics.raiseError code message word span expected actual

    let private irEffectsForWord word span (effects: Set<string>) =
        effects
        |> Set.fold (fun found name ->
            match IrEffects.ofName name with
            | Some effect -> Set.add effect found
            | None -> irFailure "IR_UNKNOWN_EFFECT" $"Effect '{name}' is not in the closed executable effect vocabulary." (Some word) span (IrEffects.names Set.empty) [ name ]) Set.empty

    let private typePatternOfLangType word (typeValue: LangType) =
        let variables = System.Collections.Generic.Dictionary<string, int>()
        let mutable nextVariable = 0
        let rec pattern = function
            | TInt -> PatternInt
            | TFloat -> PatternFloat
            | TBool -> PatternBool
            | TString -> PatternString
            | TUnit -> PatternUnit
            | TList item -> PatternList(pattern item)
            | TOption item -> PatternOption(pattern item)
            | TResult(ok, error) -> PatternResult(pattern ok, pattern error)
            | TVar name ->
                match variables.TryGetValue name with
                | true, index -> PatternVariable index
                | _ ->
                    let index = nextVariable
                    nextVariable <- nextVariable + 1
                    variables[name] <- index
                    PatternVariable index
            | TNamed name ->
                irFailure "IR_PRIMITIVE_TYPE_UNSUPPORTED" $"Trusted primitive '{word}' cannot use nominal type '{name}' in its generic contract." (Some word) None [] [ name ]
        pattern typeValue

    let private primitiveContractOfEntry (entry: WordEntry) operation =
        let definition = entry.Definition
        let typeVariables = System.Collections.Generic.Dictionary<string, int>()
        let mutable nextVariable = 0
        let rec pattern = function
            | TInt -> PatternInt
            | TFloat -> PatternFloat
            | TBool -> PatternBool
            | TString -> PatternString
            | TUnit -> PatternUnit
            | TList item -> PatternList(pattern item)
            | TOption item -> PatternOption(pattern item)
            | TResult(ok, error) -> PatternResult(pattern ok, pattern error)
            | TVar name ->
                match typeVariables.TryGetValue name with
                | true, index -> PatternVariable index
                | _ ->
                    let index = nextVariable
                    nextVariable <- nextVariable + 1
                    typeVariables[name] <- index
                    PatternVariable index
            | TNamed name ->
                irFailure "IR_PRIMITIVE_TYPE_UNSUPPORTED" $"Trusted primitive '{definition.Name}' cannot use nominal type '{name}' in its generic contract." (Some definition.Name) (Some definition.Span) [] [ name ]
        let effects = irEffectsForWord definition.Name (Some definition.Span) definition.Effects
        let primitive = PrimitiveId operation
        { Primitive = primitive
          InputPatterns = definition.Inputs |> List.map pattern
          OutputPatterns = definition.Outputs |> List.map pattern
          PrimitiveEffects = effects }

    /// Fixed host contract catalog. Source aliases resolve through BuiltinOp,
    /// so display names never select a host primitive implementation.
    let primitiveIrCatalog: IrPrimitiveCatalog =
        let grouped =
            primitives
            |> Map.toList
            |> List.choose (fun (_, entry) ->
                match entry.Builtin with
                | Some(BuiltinOp operation) -> Some(operation, entry)
                | _ -> None)
            |> List.groupBy fst
        grouped
        |> List.map (fun (operation, members) ->
            let entries = members |> List.map snd |> List.sortBy (fun entry -> entry.Definition.Name)
            let canonical = entries.Head
            for alias in entries.Tail do
                if alias.Definition.Inputs <> canonical.Definition.Inputs
                   || alias.Definition.Outputs <> canonical.Definition.Outputs
                   || alias.Definition.Effects <> canonical.Definition.Effects then
                    irFailure "IR_PRIMITIVE_REGISTRY_CONFLICT" $"Trusted BuiltinOp '{operation}' has aliases with different signatures or effects." (Some alias.Definition.Name) (Some alias.Definition.Span) [ canonical.Definition.Name ] [ alias.Definition.Name ]
            PrimitiveId operation, primitiveContractOfEntry canonical operation)
        |> Map.ofList

    let private verifyBuiltinMetadata (words: Map<string, WordEntry>) name (entry: WordEntry) =
        match entry.Builtin with
        | Some(BuiltinOp operation) ->
            match primitiveIrCatalog.TryFind(PrimitiveId operation),
                  (primitives |> Map.toList |> List.tryPick (fun (_, standard) -> if standard.Builtin = Some(BuiltinOp operation) then Some standard else None)) with
            | Some _, Some canonical ->
                if entry.Definition.Inputs <> canonical.Definition.Inputs
                   || entry.Definition.Outputs <> canonical.Definition.Outputs
                   || entry.Definition.Effects <> canonical.Definition.Effects then
                    irFailure "IR_PRIMITIVE_METADATA_MISMATCH" $"Primitive alias '{name}' does not match the trusted contract for BuiltinOp '{operation}'." (Some name) (Some entry.Definition.Span) [ String.concat " " (canonical.Definition.Inputs |> List.map Types.format) + " -> " + String.concat " " (canonical.Definition.Outputs |> List.map Types.format); String.concat ", " canonical.Definition.Effects ] [ String.concat " " (entry.Definition.Inputs |> List.map Types.format) + " -> " + String.concat " " (entry.Definition.Outputs |> List.map Types.format); String.concat ", " entry.Definition.Effects ]
            | _ ->
                irFailure "IR_UNKNOWN_PRIMITIVE_ID" $"BuiltinOp '{operation}' is not in the fixed trusted primitive registry." (Some name) (Some entry.Definition.Span) (primitiveIrCatalog |> Map.toList |> List.map (fun (id, _) -> sprintf "%A" id)) [ operation ]
        | _ -> ()
        if entry.Definition.Name <> name then
            irFailure "IR_WORD_NAME_MISMATCH" "Dictionary key differs from its source word name." (Some name) (Some entry.Definition.Span) [ name ] [ entry.Definition.Name ]
        words |> ignore

    let private closedIrType (typeKeys: Map<string, ProgramTypeKey>) word span (typeValue: LangType) =
        let rec convert = function
            | TInt -> IrInt
            | TFloat -> IrFloat
            | TBool -> IrBool
            | TString -> IrString
            | TUnit -> IrUnit
            | TList item -> IrList(convert item)
            | TOption item -> IrOption(convert item)
            | TResult(ok, error) -> IrResult(convert ok, convert error)
            | TNamed name ->
                match typeKeys.TryFind name with
                | Some key -> IrNominal key
                | None -> irFailure "TYPE_UNKNOWN_NAMED_TYPE" $"Type '{name}' has not been declared." (Some word) span [ "declared record, scalar, or enum" ] [ name ]
            | TVar name -> irFailure "TYPE_UNSUPPORTED_GENERIC" $"Generic type variable '{name}' cannot appear in closed executable IR." (Some word) span [] [ name ]
        convert typeValue

    let private contextTypes (context: IrLoweringContext) =
        let recordNames = context.Records |> Map.toSeq |> Seq.map fst |> Set.ofSeq
        let scalarNames = context.Scalars |> Map.toSeq |> Seq.map fst |> Set.ofSeq
        let enumNames = context.Enums |> Map.toSeq |> Seq.map fst |> Set.ofSeq
        let duplicateNames =
            [ recordNames; scalarNames; enumNames ]
            |> List.collect Set.toList
            |> List.groupBy id
            |> List.choose (fun (name, values) -> if values.Length > 1 then Some name else None)
            |> Set.ofList
        if not (Set.isEmpty duplicateNames) then
            irFailure "IR_TYPE_NAME_COLLISION" "A source snapshot cannot declare nominal types with the same name." None None [] (duplicateNames |> Set.toList)
        for KeyValue(name, record) in context.Records do
            if record.Name <> name then irFailure "IR_TYPE_NAME_MISMATCH" "Record map key differs from its definition name." (Some name) (Some record.Span) [ name ] [ record.Name ]
        for KeyValue(name, scalar) in context.Scalars do
            if scalar.Name <> name then irFailure "IR_TYPE_NAME_MISMATCH" "Scalar map key differs from its definition name." (Some name) (Some scalar.Span) [ name ] [ scalar.Name ]
        for KeyValue(name, enumDefinition) in context.Enums do
            if enumDefinition.Name <> name then irFailure "IR_TYPE_NAME_MISMATCH" "Enum map key differs from its definition name." (Some name) (Some enumDefinition.Span) [ name ] [ enumDefinition.Name ]
        Set.unionMany [ recordNames; scalarNames; enumNames ]

    let private validateLoweringContext (context: IrLoweringContext) =
        let names = context.Words |> Map.toSeq |> Seq.map fst |> Set.ofSeq
        let idNames = context.WordIds |> Map.toSeq |> Seq.map fst |> Set.ofSeq
        if names <> idNames then
            irFailure "IR_WORD_ID_MAP_INCOMPLETE" "Every effective dictionary word must have exactly one supplied stable ID." None None (names |> Set.toList) (idNames |> Set.toList)
        let duplicateIds =
            context.WordIds
            |> Map.toList
            |> List.groupBy snd
            |> List.choose (fun (wordId, values) -> if values.Length > 1 then Some(sprintf "%A: %s" wordId (values |> List.map fst |> String.concat ", ")) else None)
        if not (List.isEmpty duplicateIds) then
            irFailure "IR_WORD_ID_COLLISION" "Effective dictionary words must have globally unique stable IDs." None None [ "unique WordId values" ] duplicateIds
        for KeyValue(name, WordId rawId) in context.WordIds do
            if String.IsNullOrWhiteSpace rawId then irFailure "IR_WORD_ID_INVALID" "Stable word IDs must be nonempty." (Some name) None [ "nonempty WordId" ] [ rawId ]
        for KeyValue(name, entry) in context.Words do
            verifyBuiltinMetadata context.Words name entry
            if entry.Revision < 0 then irFailure "IR_INVALID_REVISION" "Dictionary word revisions cannot be negative." (Some name) (Some entry.Definition.Span) [ "nonnegative revision" ] [ string entry.Revision ]
        contextTypes context

    let private snapshotFingerprint (context: IrLoweringContext) (sourceOrigins: Map<SourceSpan, SourceSpan>) =
        let builder = System.Text.StringBuilder()
        let appendText (value: string) =
            if isNull value then builder.Append("N;") |> ignore
            else builder.Append('S').Append(value.Length).Append(':').Append(value).Append(';') |> ignore
        let appendInt (value: int) = builder.Append('I').Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(';') |> ignore
        let appendInt64 (value: int64) = builder.Append('L').Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(';') |> ignore
        let appendBool (value: bool) = builder.Append(if value then "B1;" else "B0;") |> ignore
        let appendSpan (span: SourceSpan) =
            appendText span.File
            appendInt span.Line
            appendInt span.Column
            appendInt span.Length
        let rec appendType (typeValue: LangType) =
            match typeValue with
            | TInt -> appendText "int"
            | TFloat -> appendText "float"
            | TBool -> appendText "bool"
            | TString -> appendText "string"
            | TUnit -> appendText "unit"
            | TList item -> appendText "list"; appendType item
            | TOption item -> appendText "option"; appendType item
            | TResult(ok, error) -> appendText "result"; appendType ok; appendType error
            | TNamed name -> appendText "named"; appendText name
            | TVar name -> appendText "variable"; appendText name
        let appendTypes (values: LangType list) =
            appendInt values.Length
            values |> List.iter appendType
        let appendEffects (values: Set<string>) =
            let names = values |> Set.toList |> List.sort
            appendInt names.Length
            names |> List.iter appendText
        let appendLiteral (literal: Literal) =
            match literal with
            | LInt value -> appendText "int-literal"; appendInt64 value
            | LFloat value -> appendText "float-literal"; appendInt64 (BitConverter.DoubleToInt64Bits value)
            | LBool value -> appendText "bool-literal"; appendBool value
            | LString value -> appendText "string-literal"; appendText value
            | LUnit -> appendText "unit-literal"
        let rec appendExpressions (expressions: Expr list) =
            appendInt expressions.Length
            for expression in expressions do
                match expression with
                | Push(literal, span) -> appendText "push"; appendLiteral literal; appendSpan span
                | Call(name, span) -> appendText "call"; appendText name; appendSpan span
                | ConstructContainer(kind, typeValues, span) ->
                    appendText "construct-container"
                    appendText
                        (match kind with
                         | ListEmpty -> "list-empty"
                         | ListSingleton -> "list-singleton"
                         | OptionNone -> "option-none"
                         | OptionSome -> "option-some"
                         | ResultOk -> "result-ok"
                         | ResultError -> "result-error")
                    appendTypes typeValues
                    appendSpan span
                | MapList(name, span) -> appendText "map-list"; appendText name; appendSpan span
                | FilterList(name, span) -> appendText "filter-list"; appendText name; appendSpan span
                | EachList(name, span) -> appendText "each-list"; appendText name; appendSpan span
                | FoldList(name, span) -> appendText "fold-list"; appendText name; appendSpan span
                | Let(name, span) -> appendText "let"; appendText name; appendSpan span
                | Load(name, span) -> appendText "load"; appendText name; appendSpan span
                | If(thenBranch, elseBranch, span) -> appendText "if"; appendExpressions thenBranch; appendExpressions elseBranch; appendSpan span
                | Scope(innerBody, span) -> appendText "scope"; appendExpressions innerBody; appendSpan span
                | MatchOption(name, someBranch, noneBranch, span) -> appendText "match-option"; appendText name; appendExpressions someBranch; appendExpressions noneBranch; appendSpan span
                | MatchResult(okName, errorName, okBranch, errorBranch, span) ->
                    appendText "match-result"
                    appendText okName
                    appendText errorName
                    appendExpressions okBranch
                    appendExpressions errorBranch
                    appendSpan span
                | MatchEnum(cases, span) ->
                    appendText "match-enum"
                    appendInt cases.Length
                    for caseName, branch in cases do
                        appendText caseName
                        appendExpressions branch
                    appendSpan span
        let appendBuiltin = function
            | None -> appendText "user-word"
            | Some(BuiltinOp operation) -> appendText "primitive"; appendText operation
            | Some(RecordConstructor name) -> appendText "record-constructor"; appendText name
            | Some(RecordAccessor(name, field)) -> appendText "record-accessor"; appendText name; appendText field
            | Some(ScalarConstructor name) -> appendText "scalar-constructor"; appendText name
            | Some(ScalarAccessor name) -> appendText "scalar-accessor"; appendText name
            | Some(EnumCaseConstructor(typeName, caseName)) -> appendText "enum-case-constructor"; appendText typeName; appendText caseName
        let appendMaturity = function | ProjectWord -> appendText "project" | LibraryWord -> appendText "library"
        let appendStatus = function | Primitive -> appendText "primitive" | Candidate -> appendText "candidate" | Temporary -> appendText "temporary" | Persistent -> appendText "persistent"
        appendText "agentlang-ir-snapshot-v3"
        for KeyValue(name, entry) in context.Words do
            appendText "word"
            appendText name
            let (WordId value) = context.WordIds[name]
            appendText value
            appendInt entry.Revision
            appendBuiltin entry.Builtin
            appendStatus entry.Status
            appendMaturity entry.Maturity
            appendText entry.Definition.Name
            appendTypes entry.Definition.Inputs
            appendTypes entry.Definition.Outputs
            appendEffects entry.Definition.Effects
            appendMaturity entry.Definition.Maturity
            appendInt entry.Definition.Revision
            appendText entry.Definition.Documentation
            appendText entry.Definition.SourceText
            appendSpan entry.Definition.Span
            appendExpressions entry.Definition.Body
        for KeyValue(name, record) in context.Records do
            appendText "record"
            appendText name
            appendText record.Name
            appendText record.SourceText
            appendSpan record.Span
            appendInt record.Fields.Length
            for field in record.Fields do
                appendText field.Name
                appendType field.Type
        for KeyValue(name, scalar) in context.Scalars do
            appendText "scalar"
            appendText name
            appendText scalar.Name
            appendText scalar.SourceText
            appendSpan scalar.Span
            appendType scalar.BaseType
            match scalar.Validator with
            | None -> appendText "no-validator"
            | Some validator -> appendText "validator"; appendText validator
        for KeyValue(name, enumDefinition) in context.Enums do
            appendText "enum"
            appendText name
            appendText enumDefinition.Name
            appendText enumDefinition.SourceText
            appendSpan enumDefinition.Span
            appendInt enumDefinition.Cases.Length
            enumDefinition.Cases |> List.iter appendText
        appendText "source-origin-map"
        appendInt sourceOrigins.Count
        for KeyValue(marker, origin) in sourceOrigins do
            appendSpan marker
            appendSpan origin
        let digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(builder.ToString()))
        Convert.ToHexString(digest)

    let private typeKeysForContext (context: IrLoweringContext) =
        let names =
            [ context.Records |> Map.toSeq |> Seq.map fst
              context.Scalars |> Map.toSeq |> Seq.map fst
              context.Enums |> Map.toSeq |> Seq.map fst ]
            |> Seq.concat
            |> Seq.sort
            |> Seq.toList
        names |> List.mapi (fun index name -> name, ProgramTypeKey index) |> Map.ofList

    let private buildGeneratedTargets (context: IrLoweringContext) (typeKeys: Map<string, ProgramTypeKey>) =
        let sourceMap = ResizeArray<SourceSiteId * IrSourceSite>()
        let targets =
            context.Words
            |> Map.toList
            |> List.choose (fun (name, entry) ->
                let id = context.WordIds[name]
                let definition = entry.Definition
                let effects = irEffectsForWord name (Some definition.Span) definition.Effects
                let makeTarget operation inputs outputs sourceSpan sourceKind =
                    let site = SourceSiteId(Some id, 0)
                    sourceMap.Add(site, { SiteOwner = Some id; SiteSpan = sourceSpan; SourceKind = sourceKind })
                    Some
                        (name,
                         { TargetId = id
                           TargetRevision = entry.Revision
                           TargetName = name
                           Operation = operation
                           InputTypes = inputs
                           OutputTypes = outputs
                           TargetDeclaredEffects = effects
                           TargetEffects = effects
                           SourceSite = Some site })
                match entry.Builtin with
                | Some(RecordConstructor typeName) ->
                    match context.Records.TryFind typeName, typeKeys.TryFind typeName with
                    | Some record, Some key ->
                        let inputs = record.Fields |> List.map (fun field -> closedIrType typeKeys name (Some record.Span) field.Type)
                        makeTarget (MakeRecordOperation key) inputs [ IrNominal key ] record.Span "record-declaration"
                    | _ -> irFailure "IR_GENERATED_TYPE_UNKNOWN" $"Generated constructor '{name}' has no matching record type." (Some name) (Some definition.Span) [] [ typeName ]
                | Some(RecordAccessor(typeName, fieldName)) ->
                    match context.Records.TryFind typeName, typeKeys.TryFind typeName with
                    | Some record, Some key ->
                        match record.Fields |> List.tryFindIndex (fun field -> field.Name = fieldName) with
                        | Some index ->
                            let fieldType = closedIrType typeKeys name (Some record.Span) record.Fields[index].Type
                            makeTarget (GetRecordFieldOperation(key, index)) [ IrNominal key ] [ fieldType ] record.Span "record-field"
                        | None -> irFailure "IR_GENERATED_FIELD_UNKNOWN" $"Generated accessor '{name}' refers to missing field '{fieldName}'." (Some name) (Some definition.Span) [] [ fieldName ]
                    | _ -> irFailure "IR_GENERATED_TYPE_UNKNOWN" $"Generated accessor '{name}' has no matching record type." (Some name) (Some definition.Span) [] [ typeName ]
                | Some(ScalarConstructor typeName) ->
                    match context.Scalars.TryFind typeName, typeKeys.TryFind typeName with
                    | Some scalar, Some key ->
                        let baseType = closedIrType typeKeys name (Some scalar.Span) scalar.BaseType
                        makeTarget (WrapScalarOperation key) [ baseType ] [ IrNominal key ] scalar.Span "scalar-declaration"
                    | _ -> irFailure "IR_GENERATED_TYPE_UNKNOWN" $"Generated scalar constructor '{name}' has no matching scalar type." (Some name) (Some definition.Span) [] [ typeName ]
                | Some(ScalarAccessor typeName) ->
                    match context.Scalars.TryFind typeName, typeKeys.TryFind typeName with
                    | Some scalar, Some key ->
                        let baseType = closedIrType typeKeys name (Some scalar.Span) scalar.BaseType
                        makeTarget (UnwrapScalarOperation key) [ IrNominal key ] [ baseType ] scalar.Span "scalar-declaration"
                    | _ -> irFailure "IR_GENERATED_TYPE_UNKNOWN" $"Generated scalar accessor '{name}' has no matching scalar type." (Some name) (Some definition.Span) [] [ typeName ]
                | Some(EnumCaseConstructor(typeName, caseName)) ->
                    match context.Enums.TryFind typeName, typeKeys.TryFind typeName with
                    | Some enumDefinition, Some key ->
                        match enumDefinition.Cases |> List.tryFindIndex ((=) caseName) with
                        | Some caseIndex -> makeTarget (MakeEnumCaseOperation(key, caseIndex)) [] [ IrNominal key ] enumDefinition.Span "enum-case-declaration"
                        | None -> irFailure "IR_GENERATED_CASE_UNKNOWN" $"Generated enum constructor '{name}' refers to unknown case '{caseName}'." (Some name) (Some definition.Span) enumDefinition.Cases [ caseName ]
                    | _ -> irFailure "IR_GENERATED_TYPE_UNKNOWN" $"Generated enum constructor '{name}' has no matching enum type." (Some name) (Some definition.Span) [] [ typeName ]
                | _ -> None)
        let duplicateIds = targets |> List.groupBy (fun (_, target) -> target.TargetId) |> List.choose (fun (id, values) -> if values.Length > 1 then Some(sprintf "%A" id) else None)
        if not (List.isEmpty duplicateIds) then irFailure "IR_WORD_ID_COLLISION" "Generated executable targets must have unique stable word IDs." None None [] duplicateIds
        let targetsByName = Map.ofList targets
        let targetsById = targets |> List.map (fun (_, target) -> target.TargetId, target) |> Map.ofList
        targetsByName, targetsById, Map.ofSeq sourceMap

    let private resolveIrCall (context: IrLoweringContext) (typeKeys: Map<string, ProgramTypeKey>) (generatedByName: Map<string, IrGeneratedTarget>) name span inputTypes outputTypes =
        match context.Words.TryFind name with
        | None -> irFailure "NAME_UNKNOWN_WORD" $"Word '{name}' is not defined." (Some name) span [] []
        | Some entry ->
            let id = context.WordIds[name]
            match entry.Builtin with
            | Some(BuiltinOp operation) ->
                verifyBuiltinMetadata context.Words name entry
                let contract = primitiveIrCatalog[PrimitiveId operation]
                { ResolvedTarget = PrimitiveTarget(PrimitiveId operation)
                  ResolvedName = name
                  InputTypes = inputTypes |> List.map (closedIrType typeKeys name span)
                  OutputTypes = outputTypes |> List.map (closedIrType typeKeys name span)
                  ResolvedDeclaredEffects = contract.PrimitiveEffects
                  ResolvedEffects = contract.PrimitiveEffects }
            | None ->
                let effects = irEffectsForWord name span entry.Definition.Effects
                { ResolvedTarget = UserWordTarget(id, entry.Revision)
                  ResolvedName = name
                  InputTypes = inputTypes |> List.map (closedIrType typeKeys name span)
                  OutputTypes = outputTypes |> List.map (closedIrType typeKeys name span)
                  ResolvedDeclaredEffects = effects
                  ResolvedEffects = effects }
            | Some(RecordConstructor _ | RecordAccessor _ | ScalarConstructor _ | ScalarAccessor _ | EnumCaseConstructor _) ->
                match generatedByName.TryFind name with
                | None -> irFailure "IR_GENERATED_TARGET_MISSING" $"Generated word '{name}' has no compiled operation target." (Some name) span [] []
                | Some target ->
                    { ResolvedTarget = GeneratedWordTarget(target.TargetId, target.TargetRevision)
                      ResolvedName = name
                      InputTypes = inputTypes |> List.map (closedIrType typeKeys name span)
                      OutputTypes = outputTypes |> List.map (closedIrType typeKeys name span)
                      ResolvedDeclaredEffects = target.TargetDeclaredEffects
                      ResolvedEffects = target.TargetEffects }

    let private buildNominalTypes (context: IrLoweringContext) (typeKeys: Map<string, ProgramTypeKey>) generatedByName =
        let recordTypes =
            context.Records
            |> Map.toList
            |> List.map (fun (name, record) ->
                let key = typeKeys[name]
                let fields =
                    record.Fields
                    |> List.mapi (fun index field ->
                        { FieldIndex = index
                          FieldName = field.Name
                          FieldType = closedIrType typeKeys name (Some record.Span) field.Type })
                key, IrRecordDefinition { TypeKey = key; TypeName = name; RecordFields = fields })
        let scalarTypes =
            context.Scalars
            |> Map.toList
            |> List.map (fun (name, scalar) ->
                let key = typeKeys[name]
                let baseType = closedIrType typeKeys name (Some scalar.Span) scalar.BaseType
                let validator =
                    scalar.Validator
                    |> Option.map (fun validatorName ->
                        resolveIrCall context typeKeys generatedByName validatorName (Some scalar.Span) [ scalar.BaseType ] [ TBool ])
                key, IrScalarDefinition { TypeKey = key; TypeName = name; BaseType = baseType; ValidatorCall = validator })
        let enumTypes =
            context.Enums
            |> Map.toList
            |> List.map (fun (name, enumDefinition) ->
                let key = typeKeys[name]
                key, IrEnumDefinition { TypeKey = key; TypeName = name; Cases = enumDefinition.Cases })
        Map.ofList (recordTypes @ scalarTypes @ enumTypes)

    let private expressionSpan = function
        | Push(_, span) | Call(_, span) | ConstructContainer(_, _, span)
        | MapList(_, span) | FilterList(_, span) | EachList(_, span) | FoldList(_, span)
        | Let(_, span) | Load(_, span) | If(_, _, span) | Scope(_, span)
        | MatchOption(_, _, _, span) | MatchResult(_, _, _, _, span) | MatchEnum(_, span) -> span

    let private expressionKind = function
        | Push _ -> "constant"
        | Call _ -> "call"
        | ConstructContainer(ListEmpty, _, _) -> "list-empty"
        | ConstructContainer(ListSingleton, _, _) -> "list-singleton"
        | ConstructContainer(OptionNone, _, _) -> "option-none"
        | ConstructContainer(OptionSome, _, _) -> "option-some"
        | ConstructContainer(ResultOk, _, _) -> "result-ok"
        | ConstructContainer(ResultError, _, _) -> "result-error"
        | MapList _ -> "list-map"
        | FilterList _ -> "list-filter"
        | EachList _ -> "list-each"
        | FoldList _ -> "list-fold"
        | Let _ -> "store-local"
        | Load _ -> "load-local"
        | If _ -> "if"
        | Scope _ -> "scope"
        | MatchOption _ -> "match-option"
        | MatchResult _ -> "match-result"
        | MatchEnum _ -> "match-enum"

    let private zeroWidthMarkers (expressions: Expr list) =
        let rec collect (body: Expr list) =
            body
            |> List.fold (fun found expression ->
                let span = expressionSpan expression
                let found = if span.Length = 0 then Set.add span found else found
                match expression with
                | If(thenBranch, elseBranch, _) -> Set.union found (Set.union (collect thenBranch) (collect elseBranch))
                | Scope(innerBody, _) -> Set.union found (collect innerBody)
                | MatchOption(_, someBranch, noneBranch, _) -> Set.union found (Set.union (collect someBranch) (collect noneBranch))
                | MatchResult(_, _, okBranch, errorBranch, _) -> Set.union found (Set.union (collect okBranch) (collect errorBranch))
                | MatchEnum(cases, _) -> cases |> List.fold (fun found (_, branch) -> Set.union found (collect branch)) found
                | _ -> found) Set.empty
        collect expressions

    let private contextZeroWidthMarkers (context: IrLoweringContext) =
        context.Words
        |> Map.toSeq
        |> Seq.fold (fun found (_, entry) -> Set.union found (zeroWidthMarkers entry.Definition.Body)) Set.empty

    let private ensureExactOriginKeys expectedKeys sourceOrigins owner span =
        let actualKeys = sourceOrigins |> Map.toSeq |> Seq.map fst |> Set.ofSeq
        let missing = Set.difference expectedKeys actualKeys
        let extra = Set.difference actualKeys expectedKeys
        if not (Set.isEmpty missing) then
            irFailure "IR_SOURCE_ORIGIN_MISSING" "Every private Flow source marker must have an authored source origin." owner span
                [ "one source origin per private marker" ] [ sprintf "missing marker count=%d" missing.Count ]
        elif not (Set.isEmpty extra) then
            irFailure "IR_SOURCE_ORIGIN_SET_MISMATCH" "Private source-origin mappings must not contain markers outside this compilation unit." owner span
                [ "no unrelated source-origin mappings" ] [ sprintf "extra marker count=%d" extra.Count ]

    let private contextOriginsFromMap (context: IrLoweringContext) (sourceOrigins: Map<SourceSpan, SourceSpan>) =
        let markers = contextZeroWidthMarkers context
        let missing = Set.difference markers (sourceOrigins |> Map.toSeq |> Seq.map fst |> Set.ofSeq)
        if not (Set.isEmpty missing) then
            irFailure "IR_SOURCE_ORIGIN_MISSING" "The compiler snapshot contains private Flow markers without authored source origins." None None
                [ "one origin per compiler marker" ] [ sprintf "missing marker count=%d" missing.Count ]
        sourceOrigins |> Map.filter (fun marker _ -> markers.Contains marker)

    let private lowerTypedBody (context: IrLoweringContext) (sourceOrigins: Map<SourceSpan, SourceSpan>) (typeKeys: Map<string, ProgramTypeKey>) (nominalTypes: Map<ProgramTypeKey, IrNominalDefinition>) (generatedByName: Map<string, IrGeneratedTarget>) (ownerName: string) (ownerId: WordId option) (initialStack: LangType list) (effects: Set<string>) (typedBody: TypedBody) =
        let allNodes =
            let rec visit (body: TypedBody) =
                body.Nodes |> List.collect (fun node -> node :: (node.ChildBodies |> List.collect visit))
            visit typedBody
        let normalLocalNames =
            allNodes
            |> List.choose (fun node -> match node.SourceExpression with | Let(name, _) -> Some name | _ -> None)
            |> List.distinct
        let mutable nextSlot = 0
        let mutable localNames = Map.empty
        let mutable nextSite = 0
        let mutable sourceMap = Map.empty
        let normalSlots =
            normalLocalNames
            |> List.map (fun name ->
                let slot = LocalSlot nextSlot
                nextSlot <- nextSlot + 1
                localNames <- Map.add slot name localNames
                name, slot)
            |> Map.ofList
        let freshSlot name =
            let slot = LocalSlot nextSlot
            nextSlot <- nextSlot + 1
            localNames <- Map.add slot name localNames
            slot
        let siteFor span kind =
            let site = SourceSiteId(ownerId, nextSite)
            nextSite <- nextSite + 1
            let sourceKind =
                if span.Length <> 0 then kind
                else
                    match kind with
                    | "scope" -> "synthetic-scope"
                    | "store-local" -> "synthetic-store-local"
                    | "load-local" -> "synthetic-load-local"
                    | _ -> irFailure "IR_SYNTHETIC_SOURCE_KIND_INVALID" "A zero-length source span is reserved for internal lexical-scope and temporary-local operations." (Some ownerName) None [ "Scope | StoreLocal | LoadLocal" ] [ kind ]
            let displaySpan =
                if span.Length = 0 then
                    match sourceOrigins.TryFind span with
                    | Some origin -> origin
                    | None -> irFailure "IR_SOURCE_ORIGIN_MISSING" "A private zero-width source marker must be mapped to its authored Flow origin before verified IR is created." (Some ownerName) None [ "registered authored source origin" ] []
                else
                    if sourceOrigins.ContainsKey span then
                        irFailure "IR_SOURCE_ORIGIN_INVALID" "Only private zero-width markers can be remapped to an authored source origin." (Some ownerName) (Some span) [ "zero-width source marker" ] []
                    span
            sourceMap <- Map.add site { SiteOwner = ownerId; SiteSpan = displaySpan; SourceKind = sourceKind } sourceMap
            site
        let irType word span typeValue = closedIrType typeKeys word span typeValue
        let shape word (env: Map<string, LocalSlot>) (stack: LangType list) (locals: Map<string, LangType>) =
            let localTypes =
                locals
                |> Map.toList
                |> List.map (fun (name, typeValue) ->
                    match env.TryFind name with
                    | Some slot -> slot, irType word None typeValue
                    | None -> irFailure "IR_LOCAL_SLOT_MISSING" $"Local '{name}' has no compiler-assigned slot." (Some word) None [] [ name ])
                |> Map.ofList
            { StackTypes = stack |> List.map (irType word None)
              LocalTypes = localTypes }
        let resolvedForNode name span signature =
            match signature with
            | None -> irFailure "IR_CALL_ANNOTATION_MISSING" $"Typed inference did not retain the concrete signature for '{name}'." (Some ownerName) (Some span) [] [ name ]
            | Some signature -> resolveIrCall context typeKeys generatedByName name (Some span) signature.CallInputs signature.CallOutputs
        let rec lowerBlock (env: Map<string, LocalSlot>) (typed: TypedBody) =
            let entryShape = shape ownerName env typed.EntryStack typed.EntryLocals
            let instructions =
                typed.Nodes
                |> List.map (fun node ->
                    let span = expressionSpan node.SourceExpression
                    let kind = expressionKind node.SourceExpression
                    let site = siteFor span kind
                    let operation =
                        match node.SourceExpression with
                        | Push(literal, _) ->
                            let outputType = node.OutputStack |> List.tryLast |> Option.defaultValue TUnit
                            IrOperation.Constant(literal, irType ownerName (Some span) outputType)
                        | Call(name, _) ->
                            let call = resolvedForNode name span node.CallSignature
                            match context.Words[name].Builtin with
                            | Some(RecordConstructor typeName) ->
                                match typeKeys.TryFind typeName with
                                | Some key -> IrOperation.MakeRecord(call, key)
                                | None -> irFailure "IR_GENERATED_TYPE_UNKNOWN" $"Record constructor '{name}' has no nominal type key." (Some name) (Some span) [] [ typeName ]
                            | Some(RecordAccessor(typeName, fieldName)) ->
                                match context.Records.TryFind typeName, typeKeys.TryFind typeName with
                                | Some record, Some key ->
                                    match record.Fields |> List.tryFindIndex (fun field -> field.Name = fieldName) with
                                    | Some index -> IrOperation.GetRecordField(call, key, index)
                                    | None -> irFailure "IR_GENERATED_FIELD_UNKNOWN" $"Record accessor '{name}' refers to missing field '{fieldName}'." (Some name) (Some span) [] [ fieldName ]
                                | _ -> irFailure "IR_GENERATED_TYPE_UNKNOWN" $"Record accessor '{name}' has no nominal type key." (Some name) (Some span) [] [ typeName ]
                            | Some(ScalarConstructor typeName) ->
                                match typeKeys.TryFind typeName with
                                | Some key ->
                                    match nominalTypes.TryFind key with
                                    | Some(IrScalarDefinition scalar) -> IrOperation.WrapScalar(call, key, scalar.ValidatorCall)
                                    | _ -> irFailure "IR_GENERATED_TYPE_UNKNOWN" $"Scalar constructor '{name}' has no scalar type definition." (Some name) (Some span) [] [ typeName ]
                                | None -> irFailure "IR_GENERATED_TYPE_UNKNOWN" $"Scalar constructor '{name}' has no nominal type key." (Some name) (Some span) [] [ typeName ]
                            | Some(ScalarAccessor typeName) ->
                                match typeKeys.TryFind typeName with
                                | Some key -> IrOperation.UnwrapScalar(call, key)
                                | None -> irFailure "IR_GENERATED_TYPE_UNKNOWN" $"Scalar accessor '{name}' has no nominal type key." (Some name) (Some span) [] [ typeName ]
                            | Some(EnumCaseConstructor(typeName, caseName)) ->
                                match context.Enums.TryFind typeName, typeKeys.TryFind typeName with
                                | Some enumDefinition, Some key ->
                                    match enumDefinition.Cases |> List.tryFindIndex ((=) caseName) with
                                    | Some caseIndex -> IrOperation.MakeEnumCase(call, key, caseIndex)
                                    | None -> irFailure "IR_GENERATED_CASE_UNKNOWN" $"Enum constructor '{name}' refers to unknown case '{caseName}'." (Some name) (Some span) enumDefinition.Cases [ caseName ]
                                | _ -> irFailure "IR_GENERATED_TYPE_UNKNOWN" $"Enum constructor '{name}' has no nominal type key." (Some name) (Some span) [] [ typeName ]
                            | Some(BuiltinOp _) -> IrOperation.Call call
                            | None -> IrOperation.Call call
                        | ConstructContainer(kind, arguments, _) ->
                            let args = arguments |> List.map (irType ownerName (Some span))
                            match kind, args with
                            | ListEmpty, [ item ] -> IrOperation.ListEmpty item
                            | ListSingleton, [ item ] -> IrOperation.ListSingleton item
                            | OptionNone, [ item ] -> IrOperation.OptionNone item
                            | OptionSome, [ item ] -> IrOperation.OptionSome item
                            | ResultOk, [ ok; error ] -> IrOperation.ResultOk(ok, error)
                            | ResultError, [ ok; error ] -> IrOperation.ResultError(ok, error)
                            | _ -> irFailure "IR_CONTAINER_ANNOTATION_INVALID" "Typed inference produced malformed constructor arguments." (Some ownerName) (Some span) [] (args |> List.map IrTypes.format)
                        | MapList(name, _) ->
                            let itemType = match List.last node.InputStack with | TList item -> item | _ -> irFailure "IR_LIST_ANNOTATION_INVALID" "Map lowering lost its input List<T> annotation." (Some ownerName) (Some span) [ "List<T>" ] []
                            let callback = resolvedForNode name span node.CallSignature
                            match callback.OutputTypes with
                            | [ output ] -> IrOperation.ListMap(callback, irType ownerName (Some span) itemType, output)
                            | _ -> irFailure "IR_CALLBACK_ANNOTATION_INVALID" "Map lowering requires one concrete callback output." (Some name) (Some span) [ "T -> U" ] (callback.OutputTypes |> List.map IrTypes.format)
                        | FilterList(name, _) ->
                            let itemType = match List.last node.InputStack with | TList item -> item | _ -> irFailure "IR_LIST_ANNOTATION_INVALID" "Filter lowering lost its input List<T> annotation." (Some ownerName) (Some span) [ "List<T>" ] []
                            let callback = resolvedForNode name span node.CallSignature
                            IrOperation.ListFilter(callback, irType ownerName (Some span) itemType)
                        | EachList(name, _) ->
                            let itemType = match List.last node.InputStack with | TList item -> item | _ -> irFailure "IR_LIST_ANNOTATION_INVALID" "Each lowering lost its input List<T> annotation." (Some ownerName) (Some span) [ "List<T>" ] []
                            let callback = resolvedForNode name span node.CallSignature
                            IrOperation.ListEach(callback, irType ownerName (Some span) itemType)
                        | FoldList(name, _) ->
                            let itemType, accumulatorType =
                                match node.InputStack |> List.rev |> List.take 2 with
                                | [ accumulator; TList item ] -> item, accumulator
                                | actual -> irFailure "IR_LIST_ANNOTATION_INVALID" "Fold lowering lost its List<T> and accumulator input annotations." (Some ownerName) (Some span) [ "List<T> Accumulator" ] (actual |> List.rev |> List.map Types.format)
                            let itemType = irType ownerName (Some span) itemType
                            let accumulatorType = irType ownerName (Some span) accumulatorType
                            let callback = resolvedForNode name span node.CallSignature
                            if callback.InputTypes <> [ accumulatorType; itemType ] || callback.OutputTypes <> [ accumulatorType ] then
                                irFailure "IR_CALLBACK_ANNOTATION_INVALID" "Fold lowering requires one concrete callback with signature Accumulator Item -> Accumulator." (Some name) (Some span)
                                    [ $"{IrTypes.format accumulatorType} {IrTypes.format itemType} -> {IrTypes.format accumulatorType}" ]
                                    [ (callback.InputTypes |> List.map IrTypes.format |> String.concat " ") + " -> " + (callback.OutputTypes |> List.map IrTypes.format |> String.concat " ") ]
                            IrOperation.ListFold(callback, itemType, accumulatorType)
                        | Let(name, _) ->
                            match env.TryFind name with
                            | Some slot -> IrOperation.StoreLocal slot
                            | None -> irFailure "IR_LOCAL_SLOT_MISSING" $"Local '{name}' has no compiler-assigned slot." (Some ownerName) (Some span) [] [ name ]
                        | Load(name, _) ->
                            match env.TryFind name with
                            | Some slot -> IrOperation.LoadLocal slot
                            | None -> irFailure "IR_LOCAL_SLOT_MISSING" $"Local '{name}' has no compiler-assigned slot." (Some ownerName) (Some span) [] [ name ]
                        | If(_, _, _) ->
                            match node.ChildBodies with
                            | [ thenBody; elseBody ] -> IrOperation.If(lowerBlock env thenBody, lowerBlock env elseBody)
                            | _ -> irFailure "IR_BRANCH_ANNOTATION_INVALID" "If lowering requires exactly two checked branches." (Some ownerName) (Some span) [ "2 branches" ] [ string node.ChildBodies.Length ]
                        | Scope(_, _) ->
                            match node.ChildBodies with
                            | [ scopedBody ] -> IrOperation.Scope(lowerBlock env scopedBody)
                            | _ -> irFailure "IR_SCOPE_ANNOTATION_INVALID" "Scope lowering requires exactly one checked child block." (Some ownerName) (Some span) [ "1 child block" ] [ string node.ChildBodies.Length ]
                        | MatchOption(someName, _, _, _) ->
                            match node.ChildBodies, List.last node.InputStack with
                            | [ someBody; noneBody ], TOption itemType ->
                                let someSlot = freshSlot someName
                                let someEnv = Map.add someName someSlot env
                                IrOperation.MatchOption(someSlot, lowerBlock someEnv someBody, lowerBlock env noneBody)
                            | _ -> irFailure "IR_MATCH_ANNOTATION_INVALID" "Option match lowering requires a checked Option<T> and both case bodies." (Some ownerName) (Some span) [ "Option<T> with Some and None cases" ] []
                        | MatchResult(okName, errorName, _, _, _) ->
                            match node.ChildBodies, List.last node.InputStack with
                            | [ okBody; errorBody ], TResult _ ->
                                let okSlot = freshSlot okName
                                let errorSlot = freshSlot errorName
                                IrOperation.MatchResult(okSlot, errorSlot, lowerBlock (Map.add okName okSlot env) okBody, lowerBlock (Map.add errorName errorSlot env) errorBody)
                            | _ -> irFailure "IR_MATCH_ANNOTATION_INVALID" "Result match lowering requires a checked Result<T, E> and both case bodies." (Some ownerName) (Some span) [ "Result<T, E> with Ok and Error cases" ] []
                        | MatchEnum(cases, _) ->
                            match node.ChildBodies, node.InputStack |> List.tryLast with
                            | childBodies, Some(TNamed typeName) when childBodies.Length = cases.Length ->
                                match context.Enums.TryFind typeName, typeKeys.TryFind typeName with
                                | Some enumDefinition, Some key ->
                                    let caseBlocks =
                                        List.zip cases childBodies
                                        |> List.map (fun ((caseName, _), childBody) ->
                                            match enumDefinition.Cases |> List.tryFindIndex ((=) caseName) with
                                            | Some caseIndex -> caseIndex, lowerBlock env childBody
                                            | None -> irFailure "IR_ENUM_MATCH_CASE_UNKNOWN" $"Enum match refers to unknown case '{caseName}'." (Some ownerName) (Some span) enumDefinition.Cases [ caseName ])
                                    IrOperation.MatchEnum(key, caseBlocks)
                                | _ -> irFailure "IR_ENUM_MATCH_TYPE_UNKNOWN" $"Enum match type '{typeName}' is absent from the frozen enum table." (Some ownerName) (Some span) [] [ typeName ]
                            | _ -> irFailure "IR_MATCH_ANNOTATION_INVALID" "Enum match lowering requires a declared enum and one checked block for every authored case." (Some ownerName) (Some span) [ string cases.Length + " enum case blocks" ] [ string node.ChildBodies.Length ]
                    { Site = site; Operation = operation })
            let exitShape = shape ownerName env typed.ExitStack typed.ExitLocals
            { EntryShape = entryShape; ExitShape = exitShape; Code = instructions }
        let initialSlots = normalSlots
        let block = lowerBlock initialSlots typedBody
        let declaredAndInferred = irEffectsForWord ownerName None effects
        block, localNames, sourceMap, declaredAndInferred

    let private typeKeysFromProgram (program: IrProgram) =
        program.NominalTypesByKey
        |> Map.toList
        |> List.map (fun (key, definition) ->
            match definition with
            | IrRecordDefinition record -> record.TypeName, key
            | IrScalarDefinition scalar -> scalar.TypeName, key
            | IrEnumDefinition enumDefinition -> enumDefinition.TypeName, key)
        |> Map.ofList

    let private generatedByNameFromProgram (program: IrProgram) =
        program.GeneratedTargetsById |> Map.toList |> List.map (fun (_, target) -> target.TargetName, target) |> Map.ofList

    let private validateSourceOrigins (sourceOrigins: Map<SourceSpan, SourceSpan>) =
        for KeyValue(marker, origin) in sourceOrigins do
            if marker.Length <> 0 || origin.Length <= 0 || String.IsNullOrWhiteSpace origin.File || origin.Line < 1 || origin.Column < 1 then
                irFailure "IR_SOURCE_ORIGIN_INVALID" "Source-origin overrides must map a zero-width private marker to a valid authored span." None None [ "private marker -> authored span" ] [ "invalid mapping" ]

    let private ensureDisjointOriginMarkers owner leftMarkers rightMarkers =
        let overlap = Set.intersect leftMarkers rightMarkers
        if not (Set.isEmpty overlap) then
            irFailure "IR_SOURCE_ORIGIN_MARKER_COLLISION" "Private source markers must be unique across compiler snapshots and attached bodies." owner None []
                [ sprintf "collision count=%d" overlap.Count ]

    let private remapSourceOriginDiagnostic (sourceOrigins: Map<SourceSpan, SourceSpan>) (action: unit -> 'value) : 'value =
        try action ()
        with
        | LanguageException diagnostic ->
            match diagnostic.Span with
            | Some marker when marker.Length = 0 ->
                match sourceOrigins.TryFind marker with
                | Some origin -> raise (LanguageException { diagnostic with Span = Some origin })
                | None -> reraise ()
            | _ -> reraise ()

    let private compileProgram (context: IrLoweringContext) (sourceOrigins: Map<SourceSpan, SourceSpan>) =
        validateLoweringContext context |> ignore
        validateSourceOrigins sourceOrigins
        ensureExactOriginKeys (contextZeroWidthMarkers context) sourceOrigins None None
        let knownTypes = contextTypes context
        for KeyValue(_, scalar) in context.Scalars do checkScalarValidator knownTypes context.Words scalar |> ignore
        let typeKeys = typeKeysForContext context
        let generatedByName, generatedById, generatedSourceMap = buildGeneratedTargets context typeKeys
        let nominalTypes = buildNominalTypes context typeKeys generatedByName
        let functions = ResizeArray<WordId * IrFunction>()
        let coverage = ResizeArray<WordId * IrCoverageObligations>()
        let sourceMap = ResizeArray<SourceSiteId * IrSourceSite>()
        generatedSourceMap |> Map.iter (fun site value -> sourceMap.Add(site, value))
        for KeyValue(name, entry) in context.Words do
            if entry.Builtin.IsNone then
                let checkedWord, typed = checkDefinitionDetailed knownTypes context.Enums context.Words entry.Definition
                let ownerId = context.WordIds[name]
                let block, localNames, wordSources, inferredEffects =
                    lowerTypedBody context sourceOrigins typeKeys nominalTypes generatedByName name (Some ownerId) entry.Definition.Inputs checkedWord.InferredEffects typed
                wordSources |> Map.iter (fun site value -> sourceMap.Add(site, value))
                let inputTypes = entry.Definition.Inputs |> List.map (closedIrType typeKeys name (Some entry.Definition.Span))
                let outputTypes = entry.Definition.Outputs |> List.map (closedIrType typeKeys name (Some entry.Definition.Span))
                let declaredEffects = irEffectsForWord name (Some entry.Definition.Span) entry.Definition.Effects
                let functionValue =
                    { FunctionId = ownerId
                      FunctionRevision = entry.Revision
                      FunctionName = name
                      InputTypes = inputTypes
                      OutputTypes = outputTypes
                      FunctionDeclaredEffects = declaredEffects
                      FunctionInferredEffects = inferredEffects
                      LocalNames = localNames
                      FunctionBody = block }
                functions.Add(ownerId, functionValue)
                coverage.Add(ownerId, IrVerifier.coverageObligationsWithTypes nominalTypes wordSources block)
        let program =
            { NominalTypesByKey = nominalTypes
              FunctionsById = Map.ofSeq functions
              GeneratedTargetsById = generatedById
              SourceMap = Map.ofSeq sourceMap
              CoverageByWord = Map.ofSeq coverage }
        program, snapshotFingerprint context sourceOrigins

    let compileIrProgram (context: IrLoweringContext) =
        let program, fingerprint = compileProgram context Map.empty
        IrVerifier.verifyCompilerProgram primitiveIrCatalog fingerprint program

    /// Compile a source snapshot with explicit private-marker to authored-span
    /// mappings. Only the verified source map is exported; marker coordinates
    /// remain part of the compiler input fingerprint but never reach IR.
    let compileIrProgramWithSourceOrigins (context: IrLoweringContext) sourceOrigins =
        let program, fingerprint = compileProgram context sourceOrigins
        IrVerifier.verifyCompilerProgram primitiveIrCatalog fingerprint program

    let private bodyFromInference (context: IrLoweringContext) (sourceOrigins: Map<SourceSpan, SourceSpan>) (verifiedProgram: VerifiedIrProgram) (name: string) (initialStack: LangType list) (effects: Set<string>) (typed: TypedBody) =
        validateLoweringContext context |> ignore
        validateSourceOrigins sourceOrigins
        VerifiedIrProgram.requireBackendRegistry primitiveIrCatalog verifiedProgram
        let programOrigins = contextOriginsFromMap context sourceOrigins
        match verifiedProgram.CompilerSnapshotFingerprint with
        | Some fingerprint when fingerprint = snapshotFingerprint context programOrigins -> ()
        | _ -> irFailure "IR_STALE_COMPILER_SNAPSHOT" "Detached body context does not match the exact program snapshot it will call." (Some name) None [ "same compiler snapshot fingerprint" ] []
        let program = VerifiedIrProgram.inspect verifiedProgram
        let typeKeys = typeKeysFromProgram program
        let generatedByName = generatedByNameFromProgram program
        let block, localNames, sourceMap, inferredEffects = lowerTypedBody context sourceOrigins typeKeys program.NominalTypesByKey generatedByName name None initialStack effects typed
        let inputTypes = initialStack |> List.map (closedIrType typeKeys name None)
        let outputTypes = typed.ExitStack |> List.map (closedIrType typeKeys name None)
        let body =
            { BodyName = name
              BodyInputTypes = inputTypes
              BodyOutputTypes = outputTypes
              BodyDeclaredEffects = inferredEffects
              BodyInferredEffects = inferredEffects
              BodyLocalNames = localNames
              BodyBlock = block
              BodySourceMap = sourceMap
              BodyCoverage = IrVerifier.coverageObligationsWithTypes program.NominalTypesByKey sourceMap block }
        IrVerifier.verifyBody verifiedProgram body

    let compileIrBodyAgainstProgramWithSourceOrigins context verifiedProgram name initialStack expressions sourceOrigins =
        validateLoweringContext context |> ignore
        validateSourceOrigins sourceOrigins
        VerifiedIrProgram.requireBackendRegistry primitiveIrCatalog verifiedProgram
        let contextMarkers = contextZeroWidthMarkers context
        let bodyMarkers = zeroWidthMarkers expressions
        let overlap = Set.intersect contextMarkers bodyMarkers
        if not (Set.isEmpty overlap) then
            irFailure "IR_SOURCE_ORIGIN_MARKER_COLLISION" "Detached Flow markers must be unique from compiler-snapshot markers." (Some name) None [] [ sprintf "collision count=%d" overlap.Count ]
        ensureExactOriginKeys (Set.union contextMarkers bodyMarkers) sourceOrigins (Some name) None
        let programOrigins = contextOriginsFromMap context sourceOrigins
        match verifiedProgram.CompilerSnapshotFingerprint with
        | Some fingerprint when fingerprint = snapshotFingerprint context programOrigins -> ()
        | _ -> irFailure "IR_STALE_COMPILER_SNAPSHOT" "Detached body context does not match the exact program snapshot it will call." (Some name) None [ "same compiler snapshot fingerprint" ] []
        let knownTypes = contextTypes context
        for typeValue in initialStack do validateType false knownTypes None name typeValue
        let inferred = inferBody knownTypes context.Enums context.Words name None initialStack Map.empty expressions
        bodyFromInference context sourceOrigins verifiedProgram name initialStack inferred.Effects inferred.InferredBody

    let compileIrBodyAgainstProgram context verifiedProgram name initialStack expressions =
        compileIrBodyAgainstProgramWithSourceOrigins context verifiedProgram name initialStack expressions Map.empty

    let compileIrBody context name initialStack expressions =
        let verifiedProgram = compileIrProgram context
        compileIrBodyAgainstProgram context verifiedProgram name initialStack expressions

    let compileIrTestWithExpectationAgainstProgramWithSourceOrigins context verifiedProgram (test: TestDefinition) (sourceOrigins: Map<SourceSpan, SourceSpan>) =
        validateLoweringContext context |> ignore
        validateSourceOrigins sourceOrigins
        VerifiedIrProgram.requireBackendRegistry primitiveIrCatalog verifiedProgram
        let contextMarkers = contextZeroWidthMarkers context
        let actualMarkers = zeroWidthMarkers test.Body
        let expectedExpressions =
            match test.Expected with
            | ExpectedExpression expressions -> expressions
            | ExpectedValue _ | ExpectedRuntimeError _ -> []
        let expectedMarkers = zeroWidthMarkers expectedExpressions
        ensureDisjointOriginMarkers (Some test.Word) contextMarkers actualMarkers
        ensureDisjointOriginMarkers (Some test.Word) contextMarkers expectedMarkers
        ensureDisjointOriginMarkers (Some test.Word) actualMarkers expectedMarkers
        ensureExactOriginKeys (Set.unionMany [ contextMarkers; actualMarkers; expectedMarkers ]) sourceOrigins (Some test.Word) (Some test.Span)
        let programOrigins = contextOriginsFromMap context sourceOrigins
        match verifiedProgram.CompilerSnapshotFingerprint with
        | Some fingerprint when fingerprint = snapshotFingerprint context programOrigins -> ()
        | _ -> irFailure "IR_STALE_COMPILER_SNAPSHOT" "Test context does not match the exact program snapshot it will call." (Some test.Word) (Some test.Span) [ "same compiler snapshot fingerprint" ] []

        remapSourceOriginDiagnostic sourceOrigins (fun () ->
            let knownTypes = contextTypes context
            let checkedTest, typed, expected = checkTestDetailed knownTypes context.Enums context.Words test
            let actualBody = bodyFromInference context sourceOrigins verifiedProgram (test.Word + "/" + test.Name) [] checkedTest.Effects typed
            let expectedBody =
                expected
                |> Option.map (fun inferred ->
                    bodyFromInference context sourceOrigins verifiedProgram (test.Word + "/" + test.Name + "/expected") [] Set.empty inferred.InferredBody)
            actualBody, expectedBody)

    let compileIrTestWithExpectationAgainstProgram context verifiedProgram test =
        compileIrTestWithExpectationAgainstProgramWithSourceOrigins context verifiedProgram test Map.empty

    let compileIrTestAgainstProgramWithSourceOrigins context verifiedProgram test sourceOrigins =
        compileIrTestWithExpectationAgainstProgramWithSourceOrigins context verifiedProgram test sourceOrigins |> fst

    let compileIrTestAgainstProgram context verifiedProgram test =
        compileIrTestAgainstProgramWithSourceOrigins context verifiedProgram test Map.empty

    let compileIrTest context test =
        let verifiedProgram = compileIrProgram context
        compileIrTestAgainstProgram context verifiedProgram test

    let compileIrExampleAgainstProgramWithSourceOrigins context verifiedProgram (example: ExampleDefinition) (sourceOrigins: Map<SourceSpan, SourceSpan>) =
        validateLoweringContext context |> ignore
        validateSourceOrigins sourceOrigins
        VerifiedIrProgram.requireBackendRegistry primitiveIrCatalog verifiedProgram
        let contextMarkers = contextZeroWidthMarkers context
        let bodyMarkers = zeroWidthMarkers example.Body
        ensureDisjointOriginMarkers (Some example.Word) contextMarkers bodyMarkers
        ensureExactOriginKeys (Set.union contextMarkers bodyMarkers) sourceOrigins (Some example.Word) (Some example.Span)
        let programOrigins = contextOriginsFromMap context sourceOrigins
        match verifiedProgram.CompilerSnapshotFingerprint with
        | Some fingerprint when fingerprint = snapshotFingerprint context programOrigins -> ()
        | _ -> irFailure "IR_STALE_COMPILER_SNAPSHOT" "Example context does not match the exact program snapshot it will call." (Some example.Word) (Some example.Span) [ "same compiler snapshot fingerprint" ] []

        remapSourceOriginDiagnostic sourceOrigins (fun () ->
            let knownTypes = contextTypes context
            let checkedExample, typed = checkExampleDetailed knownTypes context.Enums context.Words example
            bodyFromInference context sourceOrigins verifiedProgram (example.Word + "/" + example.Name) [] checkedExample.Effects typed)

    let compileIrExampleAgainstProgram context verifiedProgram example =
        compileIrExampleAgainstProgramWithSourceOrigins context verifiedProgram example Map.empty

    let compileIrExample context example =
        let verifiedProgram = compileIrProgram context
        compileIrExampleAgainstProgram context verifiedProgram example
