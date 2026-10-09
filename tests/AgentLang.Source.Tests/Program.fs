namespace AgentLang.Source.Tests

open System
open System.IO
open System.Text.Json.Nodes
open AgentLang

module Program =
    let mutable private assertions = 0

    let private check condition message =
        assertions <- assertions + 1
        if not condition then failwith message

    let private equal expected actual message =
        assertions <- assertions + 1
        if expected <> actual then failwith $"{message}: expected {expected}, got {actual}"

    let private parse source =
        match Parser.parse "<source-test>" source with
        | Ok parsed -> parsed
        | Error diagnostic -> failwith (Diagnostics.render diagnostic)

    let private stringNode (value: string) = JsonValue.Create(value) :> JsonNode

    let private define (runtime: Runtime.Engine) source =
        let args = JsonObject()
        args["frontend"] <- stringNode "stack"
        args["source"] <- stringNode source
        let response = runtime.Dispatch("define", args)
        if not (response["ok"].GetValue<bool>()) then failwith (response["text"].GetValue<string>())

    let private evaluate (runtime: Runtime.Engine) code =
        let args = JsonObject()
        args["frontend"] <- stringNode "stack"
        args["code"] <- stringNode code
        let response = runtime.Dispatch("eval", args)
        if not (response["ok"].GetValue<bool>()) then failwith (response["text"].GetValue<string>())
        (response["data"]["stack"]).AsArray() |> Seq.map (fun value -> value.GetValue<string>()) |> Seq.toList

    let private roundTripDocument includeHostMetadata (parsed: ParsedSource) =
        [ yield! parsed.Records |> List.map Source.renderRecord
          yield! parsed.Scalars |> List.map Source.renderScalar
          yield! parsed.Words |> List.map (Source.renderWord includeHostMetadata)
          yield! parsed.Tests |> List.map Source.renderTest
          yield! parsed.Examples |> List.map Source.renderExample ]
        |> String.concat "\n\n"

    let private testCanonicalRoundTrip () =
        let source =
            """record Archive
    field entries List<Result<Int, Option<Email>>>
    field notes Option<List<String>>
end

type Email : String
    validate old.validate-email
end

word old.compute : List<Result<Int, Option<Email>>> -> List<Result<Int, Option<Email>>>
    effects network.read fs.write db.read
    doc "Literal old.compute stays here: \"quoted\" and \\ escaped."
    maturity library
    revision 7
    1
    1.0
    -0.0
    true
    false
    "old.compute: \"quoted\" \\ path"
    unit
    old.compute
    list.empty<Result<Int, Option<Email>>>
    3 list.singleton<Int>
    option.none<Email>
    "address" option.some<Email>
    4 result.ok<Int, String>
    "failure" result.error<Int, String>
    list.map old.compute
    list.filter old.compute
    list.each old.compute
    let saved
    $saved
    if
        true
        old.compute
    else
        false
    end
    match-option
    some value
        $value
        old.compute
    none
        old.compute
    end
    match-result
    ok value
        $value
    error message
        old.compute
    end
end

test old.compute/case
    "old.compute"
    old.compute
    => "old.compute"
end

example old.compute/usage
    "old.compute"
    old.compute
    => "old.compute"
end

test old.compute/expected-error
    old.compute
    => error DIVIDE_BY_ZERO
end

test old.compute/value-expression
    "old.compute"
    old.compute
    => value
        option.none<Int>
        match-option
            some value
                $value
            none
                7
        end
end
"""
        let parsed = parse source
        equal 1 parsed.Records.Length "nested record parsed"
        equal 1 parsed.Scalars.Length "scalar parsed"
        equal 1 parsed.Words.Length "word parsed"
        equal 3 parsed.Tests.Length "literal, runtime-error, and expression tests parsed"

        let withMetadata = roundTripDocument true parsed
        check (not (withMetadata.Contains('\r')) ) "canonical source uses LF line endings"
        check (withMetadata.Contains("effects db.read fs.write network.read", StringComparison.Ordinal)) "effect names have deterministic ordering"
        check (withMetadata.Contains("maturity library\n    revision 7", StringComparison.Ordinal)) "host metadata is rendered explicitly when requested"
        check (withMetadata.Contains("doc \"Literal old.compute stays here:", StringComparison.Ordinal)) "documentation is quoted as a source string"
        let reparsedWithMetadata = parse withMetadata
        equal withMetadata (roundTripDocument true reparsedWithMetadata) "host-metadata source has stable canonical rendering"

        let withoutMetadata = Source.renderWord false parsed.Words.Head
        check (not (withoutMetadata.Contains("maturity ", StringComparison.Ordinal))) "non-host source omits maturity metadata"
        check (not (withoutMetadata.Contains("revision ", StringComparison.Ordinal))) "non-host source omits revision metadata"
        let reparsedWord = parse withoutMetadata |> fun document -> document.Words.Head
        equal withoutMetadata (Source.renderWord false reparsedWord) "source without host metadata has stable canonical rendering"

        let body = Source.renderBody parsed.Words.Head.Body
        let reparsedBody =
            match Parser.parseExpression "<body-roundtrip>" body with
            | Ok expressions -> expressions
            | Error diagnostic -> failwith (Diagnostics.render diagnostic)
        equal body (Source.renderBody reparsedBody) "all expression forms have stable canonical rendering"
        check (body.Contains("1.0", StringComparison.Ordinal)) "float formatting preserves the Float literal type"
        check (body.Contains("-0.0", StringComparison.Ordinal)) "negative zero remains a Float literal"

        let plainTest = Source.renderTest parsed.Tests.Head
        let reparsedTest = parse plainTest |> fun document -> document.Tests.Head
        equal plainTest (Source.renderTest reparsedTest) "test blocks round-trip"
        match reparsedTest.Expected with
        | ExpectedValue(LString value) -> equal "old.compute" value "quoted test expectation keeps exact value"
        | _ -> failwith "quoted string test expectation changed literal kind"

        let errorTest = parsed.Tests |> List.item 1
        let renderedErrorTest = Source.renderTest errorTest
        check (renderedErrorTest.Contains("=> error DIVIDE_BY_ZERO", StringComparison.Ordinal)) "runtime-error expectation has canonical source syntax"
        let reparsedErrorTest = parse renderedErrorTest |> fun document -> document.Tests.Head
        equal renderedErrorTest (Source.renderTest reparsedErrorTest) "runtime-error test blocks round-trip"
        match reparsedErrorTest.Expected with
        | ExpectedRuntimeError code -> equal "DIVIDE_BY_ZERO" code "runtime-error code is preserved exactly"
        | _ -> failwith "runtime-error expectation changed kind"

        let expressionTest = parsed.Tests |> List.item 2
        let renderedExpressionTest = Source.renderTest expressionTest
        check (renderedExpressionTest.Contains("=> value\n", StringComparison.Ordinal)) "expression expectation uses canonical block form"
        let reparsedExpressionTest = parse renderedExpressionTest |> fun document -> document.Tests.Head
        equal renderedExpressionTest (Source.renderTest reparsedExpressionTest) "nested match expectation round-trips canonically"
        match reparsedExpressionTest.Expected with
        | ExpectedExpression [ ConstructContainer(OptionNone, [ TInt ], _); MatchOption("value", [ Load("value", _) ], [ Push(LInt 7L, _) ], _) ] ->
            check true "nested expected option cases are preserved"
        | ExpectedExpression _ -> failwith "nested option expectation changed AST shape"
        | _ -> failwith "typed expectation changed kind"

        let plainExample = Source.renderExample parsed.Examples.Head
        let reparsedExample = parse plainExample |> fun document -> document.Examples.Head
        equal plainExample (Source.renderExample reparsedExample) "example blocks round-trip"

    let private testExpectedRuntimeErrorSyntax () =
        let expectParseError code source =
            match Parser.parse "<expected-error-test>" source with
            | Error diagnostic -> equal code diagnostic.Code $"invalid expectation reports {code}"
            | Ok _ -> failwith $"source unexpectedly parsed; expected diagnostic {code}"

        expectParseError "PARSE_INVALID_EXPECTED_ERROR" """test sample/failure
    1
    => error bad-code
end
"""

        expectParseError "PARSE_ERROR_EXPECTATION_NOT_ALLOWED" """example sample/failure
    1
    => error DIVIDE_BY_ZERO
end
"""

        expectParseError "PARSE_EMPTY_ERROR_TEST_BODY" """test sample/failure
    => error DIVIDE_BY_ZERO
end
"""

        let invalidBody =
            parse """test missing/failure
    missing.word
    => error TYPE_UNKNOWN_WORD
end
"""
            |> fun document -> document.Tests.Head
        try
            Compiler.checkTest Set.empty Map.empty invalidBody |> ignore
            failwith "expected-error expectation incorrectly accepted an ill-typed body"
        with
        | LanguageException diagnostic -> equal "NAME_UNKNOWN_WORD" diagnostic.Code "expected runtime error does not bypass static word resolution"

        let typedWordDefinition =
            { Name = "checked.add"
              Inputs = [ TInt; TInt ]
              Outputs = [ TInt ]
              Effects = Set.empty
              Maturity = ProjectWord
              Revision = 1
              Documentation = ""
              Body = []
              SourceText = ""
              Span = { File = "<test>"; Line = 1; Column = 1; Length = 1 } }
        let typedWord =
            { Definition = typedWordDefinition
              Builtin = None
              Status = Persistent
              Maturity = ProjectWord
              Revision = 1 }
        let validExpectedError =
            parse """test checked.add/expected-error
    1
    2
    checked.add
    => error RUNTIME_DIVIDE_BY_ZERO
end
"""
            |> fun document -> document.Tests.Head
        let checkedExpectedError = Compiler.checkTest Set.empty (Map.ofList [ "checked.add", typedWord ]) validExpectedError
        equal [ TInt ] checkedExpectedError.Stack "expected-error test body is type-checked without a final-value constraint"

        let invalidTypes =
            parse """test checked.add/failure
    1
    "not an integer"
    checked.add
    => error TYPE_STACK_MISMATCH
end
"""
            |> fun document -> document.Tests.Head
        try
            Compiler.checkTest Set.empty (Map.ofList [ "checked.add", typedWord ]) invalidTypes |> ignore
            failwith "expected-error expectation incorrectly accepted a type-invalid body"
        with
        | LanguageException diagnostic -> equal "TYPE_STACK_MISMATCH" diagnostic.Code "expected runtime error does not bypass static type checking"

    let private testValueExpressionExpectationSyntax () =
        let inlineSource =
            """test checked.add/inline
    1 2 checked.add
    => value value
end
"""
        let inlineTest = parse inlineSource |> fun document -> document.Tests.Head
        match inlineTest.Expected with
        | ExpectedExpression [ Call("value", span) ] -> equal 14 span.Column "inline expectation points at expression after duplicate marker word"
        | _ -> failwith "inline value expectation did not parse as one call"

        let tabbed =
            """test checked.add/tabbed
    1 2 checked.add
    =>	value	value
end
"""
            |> parse
            |> fun document -> document.Tests.Head
        match tabbed.Expected with
        | ExpectedExpression [ Call("value", span) ] -> equal 14 span.Column "tabbed expectation preserves expression source span"
        | _ -> failwith "tabbed value expectation did not parse as one call"

        let branchSource =
            """test checked.add/branch
    1 2 checked.add
    => value
        true
        if
            1 2 add
        else
            3
        end
end
"""
        let branchTest = parse branchSource |> fun document -> document.Tests.Head
        let branchCanonical = Source.renderTest branchTest
        let branchReparsed = parse branchCanonical |> fun document -> document.Tests.Head
        equal branchCanonical (Source.renderTest branchReparsed) "nested if value expectation round-trips canonically"
        match branchReparsed.Expected with
        | ExpectedExpression [ Push(LBool true, _); If(_, _, _) ] -> check true "block if expectation retains its typed AST"
        | _ -> failwith "block if value expectation changed AST shape"

        let expectParseError code source =
            match Parser.parse "<value-expectation>" source with
            | Error diagnostic -> equal code diagnostic.Code $"invalid value expectation reports {code}"
            | Ok _ -> failwith $"source unexpectedly parsed; expected diagnostic {code}"

        expectParseError "PARSE_VALUE_EXPECTATION_NOT_ALLOWED" """example sample/value
    1
    => value 1
end
"""
        expectParseError "PARSE_EMPTY_VALUE_EXPECTATION" """test sample/value
    1
    => value
end
"""
        expectParseError "PARSE_VALUE_EXPECTATION_NOT_FINAL" """test sample/value
    1
    => value 1
    2
end
"""
        expectParseError "PARSE_MISSING_EXPECTED" """test sample/value
    1
end
"""

        let typedWordDefinition =
            { Name = "checked.add"
              Inputs = [ TInt; TInt ]
              Outputs = [ TInt ]
              Effects = Set.empty
              Maturity = ProjectWord
              Revision = 1
              Documentation = ""
              Body = []
              SourceText = ""
              Span = { File = "<test>"; Line = 1; Column = 1; Length = 1 } }
        let typedWord =
            { Definition = typedWordDefinition
              Builtin = None
              Status = Persistent
              Maturity = ProjectWord
              Revision = 1 }
        let invalidType =
            parse """test checked.add/wrong-type
    1 2 checked.add
    => value
        "not an Int"
end
"""
            |> fun document -> document.Tests.Head
        try
            Compiler.checkTest Set.empty (Map.ofList [ "checked.add", typedWord ]) invalidType |> ignore
            failwith "a wrong-typed value expectation was accepted"
        with
        | LanguageException diagnostic -> equal "TEST_EXPECTED_STACK" diagnostic.Code "expected expression must have the actual test output type"

        let textWordDefinition = { typedWordDefinition with Name = "checked.text"; Inputs = [ TString ]; Outputs = [ TString ] }
        let textWord = { typedWord with Definition = textWordDefinition }
        let effectfulExpectation =
            parse """test checked.text/effectful
    "value" checked.text
    => value
        clock.now
end
"""
            |> fun document -> document.Tests.Head
        let words = Map.ofList [ "checked.text", textWord; "clock.now", Compiler.primitives["clock.now"] ]
        try
            Compiler.checkTest Set.empty words effectfulExpectation |> ignore
            failwith "an effectful value expectation was accepted"
        with
        | LanguageException diagnostic -> equal "TEST_EXPECTED_VALUE_EFFECTS" diagnostic.Code "value expectations require an empty inferred effect set"

    let private recursiveWordCalls expressions =
        let rec collect = function
            | [] -> []
            | Call(name, _) :: rest -> name :: collect rest
            | If(thenBranch, elseBranch, _) :: rest -> collect thenBranch @ collect elseBranch @ collect rest
            | MatchOption(_, someBranch, noneBranch, _) :: rest -> collect someBranch @ collect noneBranch @ collect rest
            | MatchResult(_, _, okBranch, errorBranch, _) :: rest -> collect okBranch @ collect errorBranch @ collect rest
            | FoldList(name, _) :: rest -> name :: collect rest
            | _ :: rest -> collect rest
        collect expressions

    let private testSemanticRename () =
        let span = { File = "rename.agent"; Line = 4; Column = 3; Length = 11 }
        let oldName = "old.compute"
        let newName = "new.compute"
        let body =
            [ Call(oldName, span)
              MapList(oldName, span)
              FilterList(oldName, span)
              EachList(oldName, span)
              FoldList(oldName, span)
              Push(LString oldName, span)
              Let(oldName, span)
              Load(oldName, span)
              If([ Call(oldName, span) ], [ Call(oldName, span) ], span)
              MatchOption(oldName, [ Load(oldName, span); Call(oldName, span) ], [ EachList(oldName, span) ], span)
              MatchResult("value", "reason", [ FilterList(oldName, span) ], [ Call(oldName, span) ], span)
              ConstructContainer(ListEmpty, [ TNamed oldName ], span) ]
        let renamed = Source.renameReferences oldName newName body
        check (recursiveWordCalls renamed |> List.forall ((<>) oldName)) "call references are rewritten recursively"
        check (recursiveWordCalls renamed |> List.contains newName) "renamed call target is present"
        match renamed with
        | Call(name, newSpan) :: MapList(mapName, _) :: FilterList(filterName, _) :: EachList(eachName, _) :: FoldList(foldName, foldSpan) :: Push(LString literal, _) :: Let(localName, _) :: Load(loadName, _) :: _ ->
            equal newName name "direct call is renamed"
            equal span newSpan "source span remains attached to rewritten call"
            equal newName mapName "map callback is renamed"
            equal newName filterName "filter callback is renamed"
            equal newName eachName "each callback is renamed"
            equal newName foldName "fold callback is renamed"
            equal span foldSpan "fold callback rename preserves its exact source span"
            equal oldName literal "string literal is unchanged"
            equal oldName localName "local binding is not a word reference"
            equal oldName loadName "local load is not a word reference"
        | _ -> failwith "rewrite changed expression structure or failed to cover all references"
        match renamed |> List.item 9 with
        | MatchOption(caseLocal, _, _, _) -> equal oldName caseLocal "match payload binder is not renamed"
        | _ -> failwith "option match structure was lost"
        match renamed |> List.tryPick (function ConstructContainer(_, types, _) -> Some types | _ -> None) with
        | Some [ TNamed typeName ] -> equal oldName typeName "nominal type arguments are not word references"
        | _ -> failwith "container type arguments were lost"

        let source =
            """word old.compute : Int -> Int
    effects none
    doc "old.compute in documentation is not a reference."
    old.compute
end
"""
        let definition = parse source |> fun document -> document.Words.Head
        let headerOnly = Source.renameWordHeader oldName newName definition
        equal newName headerOnly.Name "explicit header rename updates the name"
        equal oldName (recursiveWordCalls headerOnly.Body |> List.head) "header-only rename leaves body references unchanged"
        let renamedWord = Source.renameWordDefinition oldName newName definition
        equal newName renamedWord.Name "definition rename updates its header"
        equal newName (recursiveWordCalls renamedWord.Body |> List.head) "definition rename updates executable self-reference"
        equal definition.Documentation renamedWord.Documentation "definition rename leaves documentation unchanged"
        check ((Source.renderWord true renamedWord).Contains("doc \"old.compute in documentation", StringComparison.Ordinal)) "rendered renamed word preserves documentation text"

        let scalarSource = """type Email : String
    validate old.compute
end
"""
        let scalar = parse scalarSource |> fun document -> document.Scalars.Head
        let renamedScalar = Source.renameScalarValidator oldName newName scalar
        equal (Some newName) renamedScalar.Validator "scalar validator is rewritten by exact name"
        equal "String" (Types.format renamedScalar.BaseType) "scalar underlying type is unchanged"

        let recordSource = """record Archive
    field entries Int
    validate old.compute
end
"""
        let record = parse recordSource |> fun document -> document.Records.Head
        let renamedRecord = Source.renameRecordValidator oldName newName record
        equal (Some newName) renamedRecord.Validator "record validator is rewritten by exact name"
        equal record.Fields renamedRecord.Fields "record validator rename leaves the field layout unchanged"
        check ((Source.renderRecord renamedRecord).Contains("validate new.compute", StringComparison.Ordinal)) "record source renderer preserves the renamed validator"

        let testSource = """test old.compute/works
    old.compute
    => value
        "old.compute"
        list.empty<Int>
        old.compute
        list.map old.compute
end
"""
        let test = parse testSource |> fun document -> document.Tests.Head
        let renamedTest = Source.renameTestOwner oldName newName test
        equal "works" renamedTest.Name "test case name is unchanged"
        equal newName renamedTest.Word "attached test owner is renamed"
        equal [ newName ] (recursiveWordCalls renamedTest.Body) "test word calls are renamed"
        match renamedTest.Expected with
        | ExpectedExpression [ Push(LString literal, _); ConstructContainer(ListEmpty, [ TInt ], _); Call(callName, _); MapList(callbackName, _) ] ->
            equal oldName literal "expected-expression string literal is unchanged during rename"
            equal newName callName "expected-expression call is rewritten"
            equal newName callbackName "expected-expression static callback is rewritten"
        | _ -> failwith "expected-expression references were not rewritten semantically"
        let renamedTestSource = Source.renderTest renamedTest
        let reparsedRenamedTest = parse renamedTestSource |> fun document -> document.Tests.Head
        equal renamedTestSource (Source.renderTest reparsedRenamedTest) "renamed expected AST remains canonically reloadable"

        let foldSource = """word old.fold-all : List<Int> Int -> Int
    effects none
    list.fold old.compute
end
"""
        let foldDefinition = parse foldSource |> fun document -> document.Words.Head
        let renamedFold = Source.renameWordDefinition oldName newName foldDefinition
        equal [ newName ] (recursiveWordCalls renamedFold.Body) "fold callback identity is rewritten with a semantic rename"
        let renderedFold = Source.renderWord false renamedFold
        check (renderedFold.Contains("list.fold new.compute", StringComparison.Ordinal)) "rendered fold source uses the renamed callback"
        let reparsedFold = parse renderedFold |> fun document -> document.Words.Head
        equal renderedFold (Source.renderWord false reparsedFold) "renamed fold source remains canonically reloadable"

        let exampleSource = """example old.compute/sample
    old.compute
    => 1
end
"""
        let example = parse exampleSource |> fun document -> document.Examples.Head
        let renamedExample = Source.renameExampleOwner oldName newName example
        equal "sample" renamedExample.Name "example case name is unchanged"
        equal newName renamedExample.Word "attached example owner is renamed"
        equal [ newName ] (recursiveWordCalls renamedExample.Body) "example word calls are renamed"

    let private testRuntimeSemanticEquivalence () =
        let root = Path.Combine(Path.GetTempPath(), "agentlang-source-tests-" + Guid.NewGuid().ToString("N"))
        let oldPath = Path.Combine(root, "old")
        let newPath = Path.Combine(root, "new")
        Directory.CreateDirectory oldPath |> ignore
        Directory.CreateDirectory newPath |> ignore
        try
            let source =
                """word old.adjust : Int -> Int
    effects none
    1 add
end

word caller : Int -> Int
    effects none
    old.adjust
end

word old.adjust-all : List<Int> -> List<Int>
    effects none
    list.map old.adjust
end
"""
            let parsed = parse source
            let renamedWords =
                parsed.Words
                |> List.map (fun definition ->
                    if definition.Name = "old.adjust" then Source.renameWordDefinition "old.adjust" "new.adjust" definition
                    else
                        let rewritten = { definition with Body = Source.renameReferences "old.adjust" "new.adjust" definition.Body }
                        { rewritten with SourceText = Source.renderWord false rewritten })
            let rewrittenSource = renamedWords |> List.map (Source.renderWord false) |> String.concat "\n\n"
            let original = Runtime.Engine(oldPath, Set.empty)
            let changed = Runtime.Engine(newPath, Set.empty)
            define original source
            define changed rewrittenSource
            equal [ "11" ] (evaluate original "10 caller") "original direct-call behavior"
            equal [ "11" ] (evaluate changed "10 caller") "renamed direct-call behavior is equivalent"
            equal [ "[3, 4]" ] (evaluate original "2 list.singleton<Int> 3 list.append old.adjust-all") "original callback behavior"
            equal [ "[3, 4]" ] (evaluate changed "2 list.singleton<Int> 3 list.append old.adjust-all") "rewritten static callback behavior is equivalent"
        finally
            try Directory.Delete(root, true)
            with _ -> ()

    let private testParserNestingAndFlatInputLimits () =
        let parseFailure expectedCode source =
            match Parser.parse "<source-limit-test>" source with
            | Error diagnostic ->
                equal expectedCode diagnostic.Code "The parser should return the structured source-limit diagnostic."
                match diagnostic.Span with
                | Some sourceSpan ->
                    equal "<source-limit-test>" sourceSpan.File "The source-limit diagnostic should retain its file."
                    check (sourceSpan.Line > 0 && sourceSpan.Column > 0) "The source-limit diagnostic should retain a useful location."
                | None -> failwith "The source-limit diagnostic should include a source span."
            | Ok _ -> failwith $"Expected parser diagnostic {expectedCode}."

        let makeWord signature body =
            String.concat "\n" [ "word parser-limit : " + signature; "effects none"; body; "end" ]

        let typeAtDepth depth =
            String.replicate depth "Option<" + "Int" + String.replicate depth ">"

        let atTypeBoundary = parse (makeWord (typeAtDepth 256 + " -> Unit") "")
        equal 1 atTypeBoundary.Words.Length "Type nesting at the configured limit should parse."
        parseFailure "PARSE_TYPE_NESTING_LIMIT" (makeWord (typeAtDepth 257 + " -> Unit") "")

        let nestedIfWord depth =
            let lines =
                [ yield "word parser-limit : Bool -> Bool"
                  yield "effects none"
                  for _ in 1 .. depth do yield "if"
                  yield "true"
                  for _ in 1 .. depth do yield "end"
                  yield "end" ]
            String.concat "\n" lines

        let atBlockBoundary = parse (nestedIfWord 64)
        equal 1 atBlockBoundary.Words.Length "Block nesting at the configured limit should parse."
        parseFailure "PARSE_BLOCK_NESTING_LIMIT" (nestedIfWord 65)

        let operationPairs = 20_000
        let flatBody = String.replicate operationPairs "1 drop "
        let flatWord = parse (makeWord "Unit -> Unit" flatBody)
        equal (operationPairs * 2) flatWord.Words.Head.Body.Length "A large flat operation line should parse iteratively without losing or reordering expressions."
        let preservesPushCallOrder =
            flatWord.Words.Head.Body
            |> List.mapi (fun index expression ->
                if index % 2 = 0 then
                    match expression with
                    | Push _ -> true
                    | _ -> false
                else
                    match expression with
                    | Call("drop", _) -> true
                    | _ -> false)
            |> List.forall id
        check preservesPushCallOrder "A large flat operation line should preserve the source operation order."

    let private testFlow2CanonicalRoundTrip () =
        let source =
            "record Customer { field email: String; }\n\n"
            + "fn customer.has-email(value: Customer) -> Bool {\n"
            + "    value.email == \"a@example.com\"\n"
            + "}"
        let parsed =
            FlowParser.parseDocumentWithVersion 2 "<flow2-source-test>" source
            |> Result.defaultWith (fun diagnostic -> failwith (Diagnostics.render diagnostic))
        equal 2 parsed.SyntaxVersion "Flow/2 project document retains its syntax version"
        equal 1 parsed.Records.Length "Flow/2 document parses its record"
        let definition = parsed.Words |> List.exactlyOne
        equal 2 definition.SyntaxVersion "Flow/2 word retains its syntax version"
        equal false definition.EffectsDeclared "omitted Flow/2 effects remain distinguishable from an explicit declaration"
        let formatted = FlowSource.renderDocument parsed
        check (formatted.StartsWith("record Customer {\n    field email: String\n}", StringComparison.Ordinal)) "Flow/2 formatter emits newline-separated canonical record source"
        check (formatted.Contains("fn customer.has-email(value: Customer) -> Bool", StringComparison.Ordinal)) "Flow/2 formatter retains the fn declaration"
        check (formatted.Contains("value.email == \"a@example.com\"", StringComparison.Ordinal)) "Flow/2 formatter retains property access and equality"
        check (not (formatted.Contains("effects ", StringComparison.Ordinal))) "Flow/2 formatter does not invent an undeclared effects line"
        let reparsed =
            FlowParser.parseDocumentWithVersion 2 "<flow2-source-roundtrip>" formatted
            |> Result.defaultWith (fun diagnostic -> failwith (Diagnostics.render diagnostic))
        equal formatted (FlowSource.renderDocument reparsed) "Flow/2 document has stable canonical rendering"

    [<EntryPoint>]
    let main _ =
        try
            testCanonicalRoundTrip ()
            testExpectedRuntimeErrorSyntax ()
            testValueExpressionExpectationSyntax ()
            testSemanticRename ()
            testRuntimeSemanticEquivalence ()
            testParserNestingAndFlatInputLimits ()
            testFlow2CanonicalRoundTrip ()
            Console.WriteLine($"All source tests passed ({assertions} assertions).")
            0
        with error ->
            Console.Error.WriteLine($"Source tests failed after {assertions} assertions: {error}")
            1
