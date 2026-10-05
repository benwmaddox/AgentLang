module AgentLang.Flow.Lint.Tests

open System
open AgentLang

let mutable private assertions = 0

let private check name condition =
    assertions <- assertions + 1
    if not condition then failwith $"{name}: assertion failed"

let private equal name expected actual =
    assertions <- assertions + 1
    if expected <> actual then failwith $"{name}: expected {expected}, got {actual}"

let private parseWord source =
    match FlowParser.parseWord "<flow-lint-test>" source with
    | Ok definition -> definition
    | Error diagnostic -> failwith $"Unable to parse test fixture: {Diagnostics.render diagnostic}"

let private lint options source =
    match FlowLint.analyze options (parseWord source) with
    | Ok warnings -> warnings
    | Error diagnostic -> failwith $"Unexpected lint error {diagnostic.Code}: {diagnostic.Message}"

let private testDefaultThresholdAndDisableOption () =
    let makeWord fillerCount =
        let filler = List.replicate fillerCount "1" |> String.concat "; "
        $"word threshold(value: Int) -> Int {{\n    effects none\n    let near = value; {filler}; near\n}}"

    let atDefaultLimit = lint FlowLint.defaultOptions (makeWord 8)
    equal "eight intervening statements are within the default distance" [] atDefaultLimit

    let beyondDefaultLimit = lint FlowLint.defaultOptions (makeWord 9)
    equal "nine intervening statements produce one warning" 1 beyondDefaultLimit.Length
    let distant = beyondDefaultLimit.Head
    equal "distant warning has a stable code" "FLOW_LOCAL_FIRST_USE_TOO_DISTANT" distant.Code
    equal "distant warning names its binding" "near" distant.Binding
    equal "distant warning reports statement gap" (Some 9) distant.Gap
    check "distant warning retains declaration span" (distant.DeclarationSpan.Length > 0)
    check "distant warning retains first-use span" distant.FirstUseSpan.IsSome

    let noDistanceWarnings = lint FlowLint.noDistanceLimit (makeWord 40)
    equal "distance can be disabled" [] noDistanceWarnings

    match FlowLint.tryCreateOptions (Some -1) with
    | Error error -> equal "negative threshold is a structured option error" "FLOW_LINT_INVALID_OPTIONS" error.Code
    | Ok _ -> failwith "negative threshold was accepted"

    match FlowLint.analyze { MaxInterveningStatements = Some(-1) } (parseWord (makeWord 1)) with
    | Error error -> equal "direct malformed options remain structured" "FLOW_LINT_INVALID_OPTIONS" error.Code
    | Ok _ -> failwith "negative direct threshold was accepted"

let private testLexicalStatementCountingIgnoresLineEndings () =
    let makeSource newline =
        String.concat newline
            [ "word line_endings(value: Int) -> Int {"
              "    effects none"
              "    let remembered = value;"
              "    1;"
              "    1;"
              "    1;"
              "    1;"
              "    1;"
              "    1;"
              "    1;"
              "    1;"
              "    1;"
              "    remembered"
              "}" ]

    let lf = lint FlowLint.defaultOptions (makeSource "\n")
    let crlf = lint FlowLint.defaultOptions (makeSource "\r\n")
    equal "CRLF and LF produce identical source diagnostics and statement gaps" lf crlf
    equal "physical lines do not change the lexical gap" (Some 9) lf.Head.Gap

let private testInitializerDoesNotUseItsOwnBinding () =
    // This is syntax-only input: local resolution belongs to the typechecker,
    // so the self-reference is intentionally invalid for compilation.
    let source =
        """word self_read(value: Int) -> Int {
    effects none
    let self = self;
    value
}"""
    let warnings = lint FlowLint.defaultOptions source
    equal "initializer self-reference does not credit the new binding" [ "self" ] (warnings |> List.map (fun warning -> warning.Binding))
    equal "self-read remains an unused diagnostic" "FLOW_LOCAL_UNUSED" warnings.Head.Code
    check "self-read does not create a first-use span" warnings.Head.FirstUseSpan.IsNone

let private testNestedScopesAndOuterReads () =
    let source =
        """word scoped(value: Int) -> Int {
    effects none
    let outer = value;
    0;
    if true {
        let inner = outer;
        0;
        inner
    } else {
        value
    };
    outer
}"""

    let warnings = lint { MaxInterveningStatements = Some 0 } source
    equal "outer and branch-local bindings both receive distance diagnostics" [ "outer"; "inner" ] (warnings |> List.map (fun warning -> warning.Binding))
    equal "outer read inside a branch is anchored at the containing if statement" (Some 1) warnings[0].Gap
    equal "outer read span points into the branch body" (Some 6) (warnings[0].FirstUseSpan |> Option.map (fun sourceSpan -> sourceSpan.Line))
    equal "branch-local first use is measured in its own statement block" (Some 1) warnings[1].Gap
    equal "branch-local use is tracked separately" (Some 8) (warnings[1].FirstUseSpan |> Option.map (fun sourceSpan -> sourceSpan.Line))

    let reuseSource =
        """word reused(value: Int) -> Int {
    effects none
    let outer = value;
    if true { outer } else { value };
    outer
}"""
    equal "branch reads count as a use and outer bindings remain reusable" [] (lint { MaxInterveningStatements = Some 0 } reuseSource)

let private testMultiLevelBranchUsesKeepTheirOwningPositions () =
    let source =
        """word deep_scope(value: Int) -> Int {
    effects none
    let root = value;
    0;
    if true {
        let branch = value;
        if true {
            if true { root } else { branch }
        } else {
            branch
        };
        branch
    } else {
        value
    };
    root
}"""
    let warnings = lint { MaxInterveningStatements = Some 0 } source
    equal "deep nested read is charged to the root binding's owning if" [ "root" ] (warnings |> List.map (fun warning -> warning.Binding))
    equal "outer binding keeps the root-level gap across nested branches" (Some 1) warnings.Head.Gap
    equal "outer binding retains the deep read's source span" (Some 8) (warnings.Head.FirstUseSpan |> Option.map (fun sourceSpan -> sourceSpan.Line))

let private testCallArgumentsConstructorsAndMatches () =
    let callWord =
        """word calls(value: Int) -> Int {
    effects none
    let optional = option::some<Int>(value);
    let chained = optional.first(named = value).second(value);
    let answer = result::ok<Int, Int>(chained);
    answer
}"""

    equal "constructors, named arguments, dot receivers, and dot arguments are visited" [] (lint FlowLint.defaultOptions callWord)

    let optionMatch =
        """word option_match(value: Int) -> Int {
    effects none
    let optional = option::some<Int>(value);
    match optional {
        some payload => { let boxed = option::some<Int>(payload); boxed }
        none => { option::none<Int>() }
    }
}"""
    equal "Option scrutinees, payloads, and case-local bindings are visited" [] (lint FlowLint.defaultOptions optionMatch)

    let resultMatch =
        """word result_match(value: Int) -> Int {
    effects none
    let result_value = result::ok<Int, Int>(value);
    match result_value {
        ok payload => { result::ok<Int, Int>(payload) }
        error failure => { result::error<Int, Int>(failure) }
    }
}"""
    equal "Result scrutinees and both payload bindings are visited" [] (lint FlowLint.defaultOptions resultMatch)

    let unusedPayloads =
        """word unused_payloads(value: Int) -> Int {
    effects none
    let optional = option::some<Int>(value);
    match optional {
        some unused_option => { value }
        none => { value }
    };
    let result_value = result::ok<Int, Int>(value);
    match result_value {
        ok unused_ok => { value }
        error unused_error => { value }
    }
}"""
    let payloadWarnings = lint FlowLint.defaultOptions unusedPayloads
    equal "unused Option and Result payload locals are reported" [ "unused_option"; "unused_ok"; "unused_error" ] (payloadWarnings |> List.map (fun warning -> warning.Binding))
    check "unused payload warnings carry their declaration spans" (payloadWarnings |> List.forall (fun warning -> warning.DeclarationSpan.Length > 0 && warning.FirstUseSpan.IsNone))

let private testAbsoluteRootCallArgumentsCountAsReads () =
    let source =
        """word root_arguments(value: Int) -> Int {
    effects none
    let remembered = value;
    ::consume(payload = remembered);
    remembered
}"""
    let definition = parseWord source
    let warnings = lint { MaxInterveningStatements = Some 0 } source
    equal "locals used in named arguments of absolute-root calls are traversed" [] warnings
    match definition.Body |> List.tryItem 1 with
    | Some(FlowStatement.Evaluate(FlowExpression.RootCall({ Name = "consume" }, [ FlowArgument.Named("payload", FlowExpression.Local("remembered", readSpan), _) ], _))) ->
        equal "root call local read retains its authored source span" (Some 4) (Some readSpan.Line)
    | other -> failwithf "Expected a named local argument in a root call, got %A" other

let private testUnusedEffectfulInitializerIsOnlyReported () =
    let source =
        """word effects_are_preserved(value: Int) -> Int {
    effects virtual.console.write
    let side_effect = console::write(value);
    value
}"""
    let original = parseWord source
    let originalBody = original.Body
    let originalSourceText = original.SourceText
    let warnings =
        match FlowLint.analyze FlowLint.defaultOptions original with
        | Ok values -> values
        | Error error -> failwith $"Unexpected lint error {error.Code}: {error.Message}"

    equal "effectful-looking unused initializer is reported" [ "side_effect" ] (warnings |> List.map (fun warning -> warning.Binding))
    equal "unused warning uses a distinct code" "FLOW_LOCAL_UNUSED" warnings.Head.Code
    equal "lint leaves the complete word AST unchanged" originalBody original.Body
    equal "lint does not rewrite source text" source originalSourceText

let private testUnusedBindingsAndDeterministicOrder () =
    let source =
        """word order(value: Int) -> Int {
    effects none
    let first = side();
    if true {
        let nested = side();
        0
    } else {
        0
    };
    let second = value;
    value
}"""
    let definition = parseWord source
    let first = FlowLint.analyze FlowLint.defaultOptions definition
    let second = FlowLint.analyze FlowLint.defaultOptions definition
    equal "analysis is repeatable" first second
    match first with
    | Ok warnings ->
        equal "unused warnings retain syntax traversal order" [ "first"; "nested"; "second" ] (warnings |> List.map (fun warning -> warning.Binding))
        check "unused warnings have no first-use span" (warnings |> List.forall (fun warning -> warning.FirstUseSpan.IsNone && warning.Gap.IsNone))
    | Error error -> failwith $"Unexpected lint error {error.Code}: {error.Message}"

let private testLargeFlatBlock () =
    let parsed = parseWord "word flat(value: Int) -> Int {\n    effects none\n    value\n}"
    let flatCount = 4096
    let flatBody =
        [ for index in 0 .. flatCount - 1 do
              let sourceSpan = { File = "<flat>"; Line = 1; Column = index + 1; Length = 1 }
              let name = sprintf "item%04d" index
              yield FlowStatement.Let(name, FlowExpression.Literal(LInt(int64 index), sourceSpan), sourceSpan) ]
        @ [ FlowStatement.Evaluate(FlowExpression.Literal(LInt 0L, { File = "<flat>"; Line = 1; Column = flatCount + 1; Length = 1 })) ]

    let largeWord = { parsed with Body = flatBody }
    match FlowLint.analyze FlowLint.defaultOptions largeWord with
    | Error error -> failwith $"Unexpected lint error {error.Code}: {error.Message}"
    | Ok warnings ->
        equal "large flat block reports each unused binding once" flatCount warnings.Length
        equal "large flat block retains first declaration order" "item0000" warnings.Head.Binding
        equal "large flat block retains last declaration order" (sprintf "item%04d" (flatCount - 1)) warnings[flatCount - 1].Binding
        check "large flat warnings are all unused diagnostics" (warnings |> List.forall (fun warning -> warning.Code = "FLOW_LOCAL_UNUSED"))

let private buildNestedWord nestedCount =
    let parsed = parseWord "word deep(value: Int) -> Int {\n    effects none\n    value\n}"
    let sourceSpan = { File = "<deep>"; Line = 1; Column = 1; Length = 1 }
    let mutable expression = FlowExpression.Local("value", sourceSpan)
    for _ in 1 .. nestedCount do
        expression <-
            FlowExpression.If(
                FlowExpression.Literal(LBool true, sourceSpan),
                [ FlowStatement.Evaluate expression ],
                [ FlowStatement.Evaluate(FlowExpression.Literal(LInt 0L, sourceSpan)) ],
                sourceSpan)
    { parsed with Body = [ FlowStatement.Evaluate expression ] }

let private testTraversalLimitIsStructured () =
    match FlowLint.analyze FlowLint.defaultOptions (buildNestedWord 127) with
    | Ok warnings -> equal "nesting immediately below the bound succeeds" [] warnings
    | Error error -> failwith $"Valid boundary depth was rejected as {error.Code}: {error.Message}"

    match FlowLint.analyze FlowLint.defaultOptions (buildNestedWord 128) with
    | Error error -> equal "hand-built excessive nesting has a structured error" "FLOW_LINT_NESTING_LIMIT" error.Code
    | Ok _ -> failwith "nesting over the bound was analyzed"

let private testStaticCallbacksAreNotLocalReads () =
    let source callback =
        $"word callbacks(value: Int) -> List<Int> {{\n    effects none\n    let normalize = value; let values = list::singleton<Int>(value); values.map({callback})\n}}"
    // Word targets live in the dictionary, even when a local shares their name.
    for callback in [ "word normalize"; "customer::normalize" ] do
        let warnings = lint FlowLint.defaultOptions (source callback)
        equal "static callback target does not credit a same-named local" [ "normalize" ] (warnings |> List.map (fun warning -> warning.Binding))
        equal "callback receiver counts as a local read" "FLOW_LOCAL_UNUSED" warnings.Head.Code
    let ordinary = lint FlowLint.defaultOptions (source "normalize")
    equal "ordinary value argument retains its lexical local read" [] ordinary

let private testVectorBindingVisibilityAndDiagnostics () =
    let shadowingSource =
        """word vector_shadow(value: Int) -> (Int, Int) {
    effects none
    let first = value;
    let second = value;
    let (first, second) = combine(first, second);
    first
}"""

    let shadowingDefinition = parseWord shadowingSource
    let shadowingWarnings =
        match FlowLint.analyze FlowLint.defaultOptions shadowingDefinition with
        | Ok warnings -> warnings
        | Error error -> failwith $"Unexpected lint error {error.Code}: {error.Message}"
    equal "vector initializer reads outer bindings before shadowing both names" [ "second" ] (shadowingWarnings |> List.map (fun warning -> warning.Binding))
    equal "the unused shadow binding keeps its own declaration span" "FLOW_LOCAL_UNUSED" shadowingWarnings.Head.Code
    let secondPatternSpan =
        shadowingDefinition.Body
        |> List.pick (function
            | FlowStatement.LetMany(pattern, _, _) -> pattern |> List.tryPick (fun (name, sourceSpan) -> if name = "second" then Some sourceSpan else None)
            | _ -> None)
    equal "vector warning retains that binding's authored name span" secondPatternSpan shadowingWarnings.Head.DeclarationSpan

    let unusedSource =
        """word unused_pair(value: Int) -> Int {
    effects none
    let (left, right) = combine(value, value);
    0
}"""
    let unusedWarnings = lint FlowLint.defaultOptions unusedSource
    equal "both unused vector bindings are reported in declaration order" [ "left"; "right" ] (unusedWarnings |> List.map (fun warning -> warning.Binding))
    check "each vector binding has its own source span" (unusedWarnings[0].DeclarationSpan <> unusedWarnings[1].DeclarationSpan)

    let distanceSource =
        """word vector_gap(value: Int) -> Int {
    effects none
    let (near, unused) = combine(value, value);
    0;
    near
}"""
    let distanceWarnings = lint { MaxInterveningStatements = Some 0 } distanceSource
    equal "vector first-use and unused warnings follow declaration order" [ "near"; "unused" ] (distanceWarnings |> List.map (fun warning -> warning.Binding))
    equal "vector binding uses the destructuring statement as its position" (Some 1) distanceWarnings.Head.Gap
    equal "the first vector binding is distance warned" "FLOW_LOCAL_FIRST_USE_TOO_DISTANT" distanceWarnings.Head.Code
    equal "the second vector binding remains unused" "FLOW_LOCAL_UNUSED" distanceWarnings[1].Code

let private testNestedBranchReturnReads () =
    let source =
        """word branch_return(value: Int) -> Int {
    effects none
    let outer = value;
    let returned = value;
    0;
    if true {
        let branch = outer;
        return (branch)
    } else {
        return (returned)
    }
}"""
    let warnings = lint { MaxInterveningStatements = Some 0 } source
    equal "outer reads in branch initialization and return members use enclosing positions" [ "outer"; "returned" ] (warnings |> List.map (fun warning -> warning.Binding))
    equal "outer branch read is measured at its enclosing if statement" (Some 2) warnings[0].Gap
    equal "branch-local value is read by its terminal Return member" "outer" warnings[0].Binding
    equal "return member reads are attributed to their enclosing if statement" (Some 1) warnings[1].Gap
    equal "return member source span is preserved" (Some 10) (warnings[1].FirstUseSpan |> Option.map (fun sourceSpan -> sourceSpan.Line))

let private testReturnVectorReadsKeepDeclarationAndSourceOrderDistinct () =
    let source =
        """word return_pair(value: Int) -> (Int, Int) {
    effects none
    let alpha = value;
    let beta = value;
    0;
    return (beta, alpha)
}"""
    let definition = parseWord source
    let returnMembers =
        definition.Body
        |> List.pick (function
            | FlowStatement.Return(expressions, _) ->
                expressions
                |> List.choose (function
                    | FlowExpression.Local(name, sourceSpan) -> Some(name, sourceSpan)
                    | _ -> None)
                |> Some
            | _ -> None)
        |> Map.ofList

    let warnings =
        match FlowLint.analyze { MaxInterveningStatements = Some 0 } definition with
        | Ok values -> values
        | Error error -> failwith $"Unexpected lint error {error.Code}: {error.Message}"

    equal "return vector warnings retain declaration order instead of member order" [ "alpha"; "beta" ] (warnings |> List.map (fun warning -> warning.Binding))
    equal "both return vector members receive distance warnings" [ "FLOW_LOCAL_FIRST_USE_TOO_DISTANT"; "FLOW_LOCAL_FIRST_USE_TOO_DISTANT" ] (warnings |> List.map (fun warning -> warning.Code))
    equal "alpha warning uses alpha's later written return-member span" (Some(returnMembers["alpha"])) warnings[0].FirstUseSpan
    equal "beta warning uses beta's earlier written return-member span" (Some(returnMembers["beta"])) warnings[1].FirstUseSpan
    check "written Return member order differs from warning declaration order" (returnMembers["beta"].Column < returnMembers["alpha"].Column)

[<EntryPoint>]
let main _ =
    testDefaultThresholdAndDisableOption ()
    testLexicalStatementCountingIgnoresLineEndings ()
    testInitializerDoesNotUseItsOwnBinding ()
    testNestedScopesAndOuterReads ()
    testMultiLevelBranchUsesKeepTheirOwningPositions ()
    testCallArgumentsConstructorsAndMatches ()
    testAbsoluteRootCallArgumentsCountAsReads ()
    testUnusedEffectfulInitializerIsOnlyReported ()
    testUnusedBindingsAndDeterministicOrder ()
    testLargeFlatBlock ()
    testTraversalLimitIsStructured ()
    testStaticCallbacksAreNotLocalReads ()
    testVectorBindingVisibilityAndDiagnostics ()
    testNestedBranchReturnReads ()
    testReturnVectorReadsKeepDeclarationAndSourceOrderDistinct ()
    printfn $"AgentLang.Flow.Lint.Tests: {assertions} assertions passed."
    0
