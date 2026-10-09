namespace AgentLang

open System
open System.Text.Json.Nodes

/// Versioned, engine-independent contracts and authored examples for runtime help.
module AuthoringHelp =
    [<RequireQualifiedAccess>]
    type Topic =
        | Authoring
        | Define
        | Replacement
        | Examples

    type HelpRequest =
        { Topic: Topic
          SyntaxVersion: int }

    type TopicInstruction =
        { Topic: string
          Title: string
          Description: string }

    type FlowDefineField =
        { Name: string
          Type: string
          Required: bool
          Documentation: string }

    type SourceExample =
        { Name: string
          Description: string
          Source: string }

    type RequestValue =
        | Text of string
        | Boolean of bool
        | Integer of int

    type RequestExample =
        { Name: string
          Description: string
          Operation: string
          Fields: (string * RequestValue) list }

    type TopicContent =
        { Title: string
          Documentation: string
          AllowedFlowDefineFields: FlowDefineField list
          SourceExamples: SourceExample list
          RequestExamples: RequestExample list }

    let schemaVersion = 1

    let topicNames = [ "authoring"; "define"; "replacement"; "examples" ]

    let private topicName = function
        | Topic.Authoring -> "authoring"
        | Topic.Define -> "define"
        | Topic.Replacement -> "replacement"
        | Topic.Examples -> "examples"

    let private tryTopic = function
        | "authoring" -> Some Topic.Authoring
        | "define" -> Some Topic.Define
        | "replacement" -> Some Topic.Replacement
        | "examples" -> Some Topic.Examples
        | _ -> None

    let private diagnostic code message expected actual : Diagnostic =
        { Code = code
          Message = message
          Word = None
          Span = None
          Expected = expected
          Actual = actual }

    let private jsonKind (value: JsonNode) =
        match value with
        | null -> "null"
        | :? JsonObject -> "object"
        | :? JsonArray -> "array"
        | :? JsonValue as scalar ->
            let mutable text = ""
            let mutable boolean = false
            if scalar.TryGetValue<string>(&text) then "string"
            elif scalar.TryGetValue<bool>(&boolean) then "boolean"
            else "number"
        | _ -> "value"

    /// Parse optional topic and syntax-version selectors. Unknown names, unsupported
    /// versions, and selectors with the wrong JSON type are errors.
    let parseRequest (arguments: JsonObject) : Result<HelpRequest, Diagnostic> =
        let unknownFields =
            arguments
            |> Seq.map (fun (KeyValue(key, _)) -> key)
            |> Seq.filter (fun key -> key <> "topic" && key <> "syntaxVersion")
            |> Seq.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))
            |> Seq.toList
        if not (List.isEmpty unknownFields) then
            Error(diagnostic "HELP_INVALID_ARGUMENT" "Help accepts only the optional 'topic' and 'syntaxVersion' fields." [ "syntaxVersion"; "topic" ] unknownFields)
        else
            let topicResult =
                if not (arguments.ContainsKey "topic") then Ok Topic.Authoring
                else
                    match arguments["topic"] with
                    | :? JsonValue as scalar ->
                        let mutable name = ""
                        if not (scalar.TryGetValue<string>(&name)) then
                            Error(diagnostic "HELP_INVALID_ARGUMENT" "Help argument 'topic' must be a string." [ "string" ] [ jsonKind (scalar :> JsonNode) ])
                        else
                            match tryTopic name with
                            | Some topic -> Ok topic
                            | None -> Error(diagnostic "HELP_UNKNOWN_TOPIC" $"Unknown help topic '{name}'." topicNames [ name ])
                    | value -> Error(diagnostic "HELP_INVALID_ARGUMENT" "Help argument 'topic' must be a string." [ "string" ] [ jsonKind value ])
            let versionResult =
                if not (arguments.ContainsKey "syntaxVersion") then Ok 1
                else
                    match arguments["syntaxVersion"] with
                    | :? JsonValue as scalar ->
                        let mutable version = 0
                        if not (scalar.TryGetValue<int>(&version)) then
                            Error(diagnostic "HELP_INVALID_ARGUMENT" "Help argument 'syntaxVersion' must be an integer." [ "integer" ] [ jsonKind (scalar :> JsonNode) ])
                        elif version <> 1 && version <> 2 then
                            Error(diagnostic "HELP_SOURCE_VERSION_UNSUPPORTED" $"Flow syntax version {version} is not supported by help." [ "1"; "2" ] [ string version ])
                        else Ok version
                    | value -> Error(diagnostic "HELP_INVALID_ARGUMENT" "Help argument 'syntaxVersion' must be an integer." [ "integer" ] [ jsonKind value ])
            match topicResult, versionResult with
            | Error problem, _ -> Error problem
            | _, Error problem -> Error problem
            | Ok topic, Ok syntaxVersion -> Ok { Topic = topic; SyntaxVersion = syntaxVersion }

    let requestTopic (request: HelpRequest) = topicName request.Topic

    let topicInstructions =
        [ { Topic = "authoring"
            Title = "Start here"
            Description = "Runtime JSONL authoring basics and the available help topics." }
          { Topic = "define"
            Title = "Define Flow source"
            Description = "Flow source syntax, documentation, request fields, tests, and a complete example." }
          { Topic = "replacement"
            Title = "Replace a word"
            Description = "Revision compare-and-swap, staged publication, and caller test gates." }
          { Topic = "examples"
            Title = "Run tests and examples"
            Description = "Attached cases, literal expectations, effects, and coverage inspection." } ]

    let flowDefineFields =
        [ { Name = "frontend"
            Type = "string"
            Required = false
            Documentation = "Selects a frontend. Omit it to use Flow; the Flow allowlist applies only when Flow is selected." }
          { Name = "syntaxVersion"
            Type = "integer"
            Required = false
            Documentation = "Selects Flow syntax version 1 or 2. Omit it for version 1; Stack supports version 1 only." }
          { Name = "source"
            Type = "string"
            Required = true
            Documentation = "Complete Flow source for one declaration/document. Flow does not accept a code alias." }
          { Name = "temporary"
            Type = "boolean"
            Required = false
            Documentation = "New words default to candidate; true creates a task-scoped temporary word. Types cannot be temporary." }
          { Name = "replace"
            Type = "boolean"
            Required = false
            Documentation = "Set true to stage a replacement or a compare-and-swap edit to an attached case." }
          { Name = "expectedRevision"
            Type = "nonnegative integer"
            Required = false
            Documentation = "Required with replace=true for an existing owner; use the current describe.revision value." }
          { Name = "tests"
            Type = "array of source strings"
            Required = false
            Documentation = "Optional complete Flow test source strings for a single-word declaration. Multi-declaration and attachment-only documents must put cases inline; attachment-only documents must name one owner." }
          { Name = "examples"
            Type = "array of source strings"
            Required = false
            Documentation = "Optional complete Flow example source strings for a single-word declaration. Multi-declaration and attachment-only documents must put cases inline; attachment-only documents must name one owner." }
          { Name = "removeAttachments"
            Type = "array of objects"
            Required = false
            Documentation = "For one existing Flow owner, each object has exactly kind, caseName, and expectedSourceHash; removal is revision-bound." } ]

    let private tutorialSource =
        """word tutorial.sign(value: Int) -> Int {
    effects none
    doc "Returns -1 for negative integers and 1 for zero or positive integers."
    if int::less-than(value, 0) { -1 } else { 1 }
}

test tutorial.sign/negative { tutorial::sign(-2) => -1 }
test tutorial.sign/zero { tutorial::sign(0) => 1 }
test tutorial.sign/positive { tutorial::sign(2) => 1 }

example tutorial.sign/negative { tutorial::sign(-2) => -1 }"""

    let private tutorialSourceV2 =
        """fn tutorial.sign(value: Int) -> Int {
    doc "Returns -1 for negative integers and 1 for zero or positive integers."

    if int.less-than(value, 0) { -1 } else { 1 }
}

test tutorial.sign/negative { tutorial.sign(-2) => -1 }
test tutorial.sign/zero { tutorial.sign(0) => 1 }
test tutorial.sign/positive { tutorial.sign(2) => 1 }

example tutorial.sign/negative { tutorial.sign(-2) => -1 }"""

    let private tutorialWordSource =
        """word tutorial.sign(value: Int) -> Int {
    effects none
    doc "Returns -1 for negative integers and 1 for zero or positive integers."
    if int::less-than(value, 0) { -1 } else { 1 }
}"""

    let private tutorialWordSourceV2 =
        """fn tutorial.sign(value: Int) -> Int {
    doc "Returns -1 for negative integers and 1 for zero or positive integers."

    if int.less-than(value, 0) { -1 } else { 1 }
}"""

    let private tutorialSpanTypeSourceV2 =
        """record TutorialSpan {
    field start: Int
    field finish: Int
    validate tutorialSpan.valid?
}"""

    let private tutorialSpanValidatorSourceV2 =
        """fn tutorialSpan.valid?(value: TutorialSpan) -> Bool {
    doc "A span is ordered when its finish is not before its start."

    int.less-or-equal(value.start, value.finish)
}"""

    let private tutorialSpanTestsSourceV2 =
        """test tutorialSpan.valid?/ordered {
    tutorialSpan.valid?(tutorialSpan.new(start = 1, finish = 3))
    => true
}

test tutorialSpan.valid?/equal {
    tutorialSpan.valid?(tutorialSpan.new(start = 3, finish = 3))
    => true
}

test tutorialSpan.valid?/reversed {
    tutorialSpan.new(start = 3, finish = 1)
    => error RECORD_VALIDATION_FAILED
}"""

    let private tutorialSpanSourceV2 =
        String.concat "\n\n" [ tutorialSpanTypeSourceV2; tutorialSpanValidatorSourceV2; tutorialSpanTestsSourceV2 ]

    let private tutorialListFoldSourceV2 =
        """fn tutorial.fold-step(acc: Int, item: Int) -> Int {
    .add(acc, item)
}

fn tutorial.fold-sum(items: List<Int>) -> Int {
    items.fold(0, tutorial.fold-step)
}

test tutorial.fold-step/add { tutorial.fold-step(2, 3) => 5 }

test tutorial.fold-sum/empty { tutorial.fold-sum(list.empty<Int>()) => 0 }

test tutorial.fold-sum/multiple { tutorial.fold-sum(list.append(list.append(list.singleton<Int>(1), 2), 3)) => 6 }

example tutorial.fold-sum/multiple { tutorial.fold-sum(list.append(list.append(list.singleton<Int>(1), 2), 3)) => 6 }"""

    let private tutorialListFoldExampleV2 =
        "example tutorial.fold-sum/multiple { tutorial.fold-sum(list.append(list.append(list.singleton<Int>(1), 2), 3)) => 6 }"

    let private text value = Text value
    let private textField name value = name, text value

    let private helpRequestExample topic =
        { Name = "help-" + topic
          Description = "Request the named authoring help topic."
          Operation = "help"
          Fields = [ textField "topic" topic ] }

    let private tutorialSourceExample =
        { Name = "tutorial-sign"
          Description = "Complete Flow source with inline documentation, three tests, and one example."
          Source = tutorialSource }

    let private tutorialSpanSourceExample =
        { Name = "tutorial-span-validator"
          Description = "Complete Flow/2 record, pure predicate, predicate-owned valid and expected-error tests."
          Source = tutorialSpanSourceV2 }

    let private tutorialListFoldSourceExample =
        { Name = "tutorial-list-fold"
          Description = "Compact Flow/2 fold with a statically named callback and attached tests and example."
          Source = tutorialListFoldSourceV2 }

    let private tutorialListFoldCaseExample =
        { Name = "tutorial-list-fold-example"
          Description = "Flow/2 example that calls the named fold word after defining tutorial-list-fold."
          Source = tutorialListFoldExampleV2 }

    let private tutorialListFoldRequestExamples =
        [ { Name = "define-tutorial-list-fold"
            Description = "Define the Flow/2 fold source with its named callback, tests, and example."
            Operation = "define"
            Fields = [ textField "source" tutorialListFoldSourceV2; "syntaxVersion", Integer 2 ] }
          { Name = "test-tutorial-fold-step"
            Description = "Run the named fold callback's own test."
            Operation = "test"
            Fields = [ textField "word" "tutorial.fold-step" ] }
          { Name = "test-tutorial-fold-sum"
            Description = "Run the empty and multi-item fold tests."
            Operation = "test"
            Fields = [ textField "word" "tutorial.fold-sum" ] }
          { Name = "run-tutorial-list-fold-example"
            Description = "Run the fold example attached to tutorial.fold-sum."
            Operation = "example"
            Fields = [ textField "word" "tutorial.fold-sum" ] }
          { Name = "eval-tutorial-list-fold"
            Description = "Evaluate a Flow/2 call to the fold word with three items."
            Operation = "eval"
            Fields =
                [ textField "frontend" "flow"
                  "syntaxVersion", Integer 2
                  textField "code" "tutorial.fold-sum(list.append(list.append(list.singleton<Int>(1), 2), 3))" ] } ]

    let private tutorialSpanRequestExamples =
        [ { Name = "eval-tutorial-sign-v2"
            Description = "Evaluate compact Flow/2 code through eval's code field."
            Operation = "eval"
            Fields = [ textField "frontend" "flow"; "syntaxVersion", Integer 2; textField "code" "tutorial.sign(-2)" ] }
          { Name = "define-tutorial-span-validator"
            Description = "Define the complete Flow/2 record, predicate, and its own tests."
            Operation = "define"
            Fields = [ textField "source" tutorialSpanSourceV2; "syntaxVersion", Integer 2 ] }
          { Name = "test-tutorial-span-validator"
            Description = "Run the predicate's ordered, equal, and rejected-constructor tests."
            Operation = "test"
            Fields = [ textField "word" "tutorialSpan.valid?" ] }
          { Name = "commit-tutorial-span-validator-as-library"
            Description = "Publish the predicate after its own tests cover true and false returns."
            Operation = "commit"
            Fields = [ textField "word" "tutorialSpan.valid?"; "library", Boolean true ] }
          { Name = "source-tutorial-span-predicate"
            Description = "Read the exact authored Flow source for the predicate word."
            Operation = "source"
            Fields = [ textField "word" "tutorialSpan.valid?" ] }
          { Name = "source-tutorial-span-type"
            Description = "Read the exact authored Flow source for the record type."
            Operation = "source"
            Fields = [ textField "type" "TutorialSpan" ] } ]

    let private topicContent =
        [ Topic.Authoring,
            { Title = "Authoring through the runtime"
              Documentation =
                "Use one JSON object per JSONL request. Flow is the default frontend for define and eval. Select Flow syntax with syntaxVersion (version 1 is the default); use format to request canonical source without changing the project, then submit an explicit define request to stage an edit. Discover before editing: words with compact=true returns names only; search matches names, documentation, and signatures; describe shows full signatures and exact flowReference call spelling with its flowReferenceSyntaxVersion; context gives a bounded view of a known word and its reachable dependencies and types, with the syntax version for its references. Follow the reported version when copying a reference: Flow/2 references use shadow-safe leading-dot roots, even when the active source is Flow/1. Use transitive-dependencies to inspect the full dependency closure. Protocol queries use dictionary names with dots. Help returns instructions only; the active host still controls which operations it allows."
              AllowedFlowDefineFields = []
              SourceExamples = []
              RequestExamples =
                [ { Name = "help-index"
                    Description = "Return the concise authoring index."
                    Operation = "help"
                    Fields = [] }
                  { Name = "compact-words"
                    Description = "List only word and syntax-construct names."
                    Operation = "words"
                    Fields = [ "compact", Boolean true ] }
                  { Name = "search-add"
                    Description = "Find words and constructs matching the focused term add."
                    Operation = "search"
                    Fields = [ textField "query" "add" ] }
                  { Name = "context-add"
                    Description = "Inspect a bounded dependency and type context rooted at add."
                    Operation = "context"
                    Fields =
                        [ textField "word" "add"
                          "maxDepth", Integer 2
                          "maxWords", Integer 6
                          "maxUtf8Bytes", Integer 4096 ] }
                  helpRequestExample "define"
                  helpRequestExample "replacement"
                  helpRequestExample "examples" ] }
          Topic.Define,
            { Title = "Define Flow source"
              Documentation =
                "A Flow define request takes source and the optional fields listed below. Omit frontend to select Flow and syntaxVersion to select Flow/1; use syntaxVersion 2 for Flow/2. Put documentation in the word body as `doc \"...\"`; doc and documentation are not request fields. A local binding stays inside one invocation. Declare effects in the word source, using `effects none` when there are no effects. For one-word declarations, attach tests and examples inline or through their source arrays. Multi-declaration project documents are add-only and must keep cases inline. An attachment-only document must keep cases inline and name exactly one existing Flow owner. Adding a new attachment-only case captures the current owner revision; replacing a case or removing one requires replace=true with the current expectedRevision, and removals also require its current expected source hash. Each case owner is the dictionary name before the slash. Define stages a candidate or temporary word and does not persist it. The format operation returns canonical Flow text without changing the project; submit the result through define when you want to stage that edit. A record validator must be pure with the exact record-type-to-Bool signature; write `validate namespace::predicate;` in Flow/1 and `validate namespace.predicate;` in Flow/2. A false result raises `RECORD_VALIDATION_FAILED`. Test rejection with an attached expected-error case; the validator's completed false return remains observable. The generated constructor's validator dependency is available through introspection. Library words may call only trusted primitives, generated type operations, or authored dependencies committed as library words. Qualify each helper with its own passing tests and complete coverage; a group library commit may qualify selected candidates together when every selected library word passes its own gate."
              AllowedFlowDefineFields = flowDefineFields
              SourceExamples = [ tutorialSourceExample ]
              RequestExamples =
                [ { Name = "define-tutorial-sign"
                    Description = "Define the complete source example using Flow's default frontend."
                    Operation = "define"
                    Fields = [ textField "source" tutorialSource ] }
                  { Name = "format-tutorial-sign"
                    Description = "Return canonical Flow source without changing the project."
                    Operation = "format"
                    Fields = [ textField "source" tutorialSource ] }
                  { Name = "test-tutorial-sign"
                    Description = "Run the attached tests for this word."
                    Operation = "test"
                    Fields = [ textField "word" "tutorial.sign" ] }
                  { Name = "describe-tutorial-sign"
                    Description = "Inspect documentation, cases, effects, status, and revision."
                    Operation = "describe"
                    Fields = [ textField "word" "tutorial.sign" ] }
                  { Name = "commit-tutorial-sign"
                    Description = "Commit the tested candidate as a project word."
                    Operation = "commit"
                    Fields = [ textField "word" "tutorial.sign" ] }
                  { Name = "commit-tutorial-sign-as-library"
                    Description = "Alternative to project commit: passing own tests must call this exact revision, exercise every supported branch and iteration outcome, cover every Bool/enum input option at each parameter position, produce every value in each supported finite return domain, and keep every authored dependency qualified as a library word. A group library commit may qualify selected candidates together when each passes its own gate."
                    Operation = "commit"
                    Fields = [ textField "word" "tutorial.sign"; "library", Boolean true ] } ] }
          Topic.Replacement,
            { Title = "Replace a word"
              Documentation =
                "Read describe.revision immediately before replacing a definition. Set replace=true and expectedRevision to that exact current revision; a stale compare-and-swap leaves the word unchanged. Existing attached cases remain unless you replace a case with the same kind/name or remove it through removeAttachments with its current source hash. A candidate replacement is tested and published with normal commit. A temporary word must be promoted to a candidate before normal commit, or task.commit will end the task and clear temporary words. For a committed word, define stages the replacement; replace-word then requires the replacement's own tests and every affected persistent caller's tests to pass. replace-word accepts a word name, not replacement source. Library publication also checks the word's own instruction, branch, and iteration coverage, every Bool/enum option at each parameter position, and all values in supported finite return domains. A library word may depend only on trusted primitives, generated type operations, and authored library words; replacement rechecks the closure of affected library callers before publication. Evidence comes only from actual calls to the exact function revision in passing attached tests; expected-value expressions are isolated. Unsupported finite domains fail closed."
              AllowedFlowDefineFields = []
              SourceExamples = []
              RequestExamples =
                [ { Name = "inspect-revision"
                    Description = "Read the current revision before preparing a replacement."
                    Operation = "describe"
                    Fields = [ textField "word" "tutorial.sign" ] }
                  { Name = "stage-replacement"
                    Description = "Replace the definition at revision 1; substitute the current describe.revision."
                    Operation = "define"
                    Fields =
                        [ textField "source" tutorialWordSource
                          "replace", Boolean true
                          "expectedRevision", Integer 1 ] }
                  { Name = "commit-candidate-replacement"
                    Description = "Publish a candidate replacement after its own attached tests pass."
                    Operation = "commit"
                    Fields = [ textField "word" "tutorial.sign" ] }
                  { Name = "promote-temporary"
                    Description = "Move a temporary word to candidate status before normal commit."
                    Operation = "promote"
                    Fields = [ textField "word" "tutorial.sign" ] }
                  { Name = "publish-persistent-replacement"
                    Description = "Publish a staged persistent replacement after own and affected caller tests pass."
                    Operation = "replace-word"
                    Fields = [ textField "word" "tutorial.sign" ] } ] }
          Topic.Examples,
            { Title = "Run tests and examples"
              Documentation =
                "Use test with word to run one owner's attached tests; use test-all to run every attached test in the current dictionary. Test expectations support `=> <literal>`, `=> value <expression>`, and `=> error CODE`. A value-expression expectation runs in an isolated trace and does not count as coverage of the tested word. For a nominal result compared with an underlying literal, explicitly unwrap it with its accessor in the actual test body; a constructor call cannot follow a bare `=>`. Flow examples accept literal expectations only. Use example with word to run all of that owner's examples, or add caseName to select one. Examples are inspectable documentation metadata and are not a runtime commit gate by themselves. Effects are the declared effect names; inspect them with describe or effects. The latest test or test-all batch replaces the displayed coverage observations. After testing individual words, run test-all before inspecting coverage for several words together. Passing a test batch is not a proof of domain correctness. Library maturity requires every own instruction, branch, and supported iteration outcome; each Bool/enum option for each parameter position; every value in each supported finite return domain; and a dependency closure containing only trusted primitives, generated type operations, and authored library words. Only actual exact-revision calls from passing own tests count. Expected-expression evaluation is isolated, and unsupported finite domains fail closed."
              AllowedFlowDefineFields = []
              SourceExamples =
                [ { Name = "tutorial-sign-test"
                    Description = "A Flow test with a literal expectation."
                    Source = "test tutorial.sign/negative { tutorial::sign(-2) => -1 }" }
                  { Name = "value-expression-test-expectation"
                    Description = "A test may compare against an expression by writing the explicit value form."
                    Source = "test tutorial.sign/value-expression { tutorial::sign(2) => value ::add(0, 1) }" }
                  { Name = "runtime-error-test-expectation"
                    Description = "A test may assert the divide-by-zero runtime error with the explicit error form."
                    Source = "test tutorial.sign/divide-by-zero { ::divide(tutorial::sign(2), 0) => error RUNTIME_DIVIDE_BY_ZERO }" }
                  { Name = "tutorial-sign-example"
                    Description = "A Flow example with a literal expected result."
                    Source = "example tutorial.sign/negative { tutorial::sign(-2) => -1 }" } ]
              RequestExamples =
                [ { Name = "test-one-word"
                    Description = "Run tests for one word."
                    Operation = "test"
                    Fields = [ textField "word" "tutorial.sign" ] }
                  { Name = "test-all"
                    Description = "Run attached tests across the current dictionary."
                    Operation = "test-all"
                    Fields = [] }
                  { Name = "list-examples"
                    Description = "List an owner's attached example case names."
                    Operation = "examples"
                    Fields = [ textField "word" "tutorial.sign" ] }
                  { Name = "run-examples"
                    Description = "Run every example for an owner."
                    Operation = "example"
                    Fields = [ textField "word" "tutorial.sign" ] }
                  { Name = "inspect-effects"
                    Description = "Read an owner's declared effects."
                    Operation = "effects"
                    Fields = [ textField "word" "tutorial.sign" ] } ] } ]
        |> Map.ofList

    let contentForVersion syntaxVersion (topic: Topic) =
        let original = topicContent[topic]
        if syntaxVersion = 1 then original
        else
            let requestExamples =
                original.RequestExamples
                |> List.map (fun example ->
                    let source =
                        match topic, example.Name with
                        | Topic.Define, "define-tutorial-sign"
                        | Topic.Define, "format-tutorial-sign" -> Some tutorialSourceV2
                        | Topic.Replacement, "stage-replacement" -> Some tutorialWordSourceV2
                        | _ -> None
                    let fields =
                        example.Fields
                        |> List.map (fun (name, value) ->
                            if name = "source" then name, Text(source |> Option.defaultValue tutorialSourceV2)
                            else name, value)
                    let needsVersion =
                        example.Name = "define-tutorial-sign"
                        || example.Name = "format-tutorial-sign"
                        || example.Name = "stage-replacement"
                        || example.Name.StartsWith("help-", StringComparison.Ordinal)
                    let fields =
                        if needsVersion && not (fields |> List.exists (fun (name, _) -> name = "syntaxVersion")) then
                            fields @ [ "syntaxVersion", Integer 2 ]
                        else fields
                    { example with Fields = fields })
            let requestExamples =
                if topic = Topic.Define then requestExamples @ tutorialSpanRequestExamples @ tutorialListFoldRequestExamples
                elif topic = Topic.Examples then requestExamples @ tutorialListFoldRequestExamples
                else requestExamples
            { original with
                Documentation =
                    original.Documentation
                    + " This help response is selected for Flow/2; write function declarations with `fn`."
                    + (if topic = Topic.Define then
                           " Use dots for namespace calls and constructors, for example `file.read(path)`, `option.some<Int>(value)`, and `result.ok<Int, String>(value)`. A local record root reads declared properties such as `customer.email`; a bound-root call such as `customer.read-balance()` passes that record as the function's first argument. In call and property expressions, a local name wins over the same namespace root, and a failed local-root lookup does not fall back to the namespace. Use a leading dot to choose an exact dictionary root despite a shadowing local, as in `.customer.read-balance(customer)` or `.identity(value)`. Static callback references such as `values.map(customer.active?)` name dictionary functions and cannot capture caller-local values. Keep postfix dots on the same line as their receiver; a dot at the start of a new line, including inside grouping, begins a separate exact-root call. Argument lists can span lines. Existing Flow/2 `::` spellings remain readable for compatibility; formatting writes dotted calls. Newlines separate statements, record fields, validators, enum cases, and effect-count entries. Same-line entries require semicolons, while the final entry before a closing delimiter may omit one; the formatter omits optional semicolons. Effects and documentation precede executable code and are followed by a blank line; omitted function effects mean pure. A one-expression match arm may be written without a block, such as `pending => \"Pending\"`; an arm with multiple statements keeps its block."
                       elif topic = Topic.Authoring then
                           " Flow/2 uses `fn`, dotted calls, and newline-separated statements; omitted function effects mean pure. Select `syntaxVersion: 2` for Flow/2, and see the Define help topic for its call and separator rules."
                       else "")
                    + (if topic = Topic.Define then
                           " Use eval with the compact `code` field for expressions; define uses `source` for complete declarations. Use source with `word` to read authored word text and with `type` to read the exact type declaration. Static list callbacks use receiver forms such as `items.map(callback)`, `items.filter(callback)`, `items.each(callback)`, and `items.fold(seed, callback)`; callback must be a statically named word reference and cannot capture caller locals. A record validator sees the complete unchecked construction candidate and must not assume the invariant already holds. Put valid and expected-error constructor tests on the validator itself: its completed false return is observed before RECORD_VALIDATION_FAILED, while caller-owned tests do not qualify the callee and generated constructors cannot own authored Flow tests for it."
                       else "")
                    + (if topic = Topic.Examples then
                           " Define the `tutorial-list-fold` source before running its attached tests or example. Its callback is a statically named word reference and cannot capture caller locals. Flow/2 tests may add `effects { fs.read: 2; fs.write: 0 }` after the value or error expectation. This asserts exact counts of provider calls made while the attached user word is active, including nested helpers; omitted categories are zero. The observable categories are fs.read, fs.write, clock.read, and console.write. Other effect categories are unsupported, and examples cannot use this test-only suffix."
                       else "")
                SourceExamples =
                    let examples =
                        original.SourceExamples
                        |> List.map (fun example ->
                            let source =
                                match topic, example.Name with
                                | Topic.Define, "tutorial-sign" -> Some tutorialSourceV2
                                | Topic.Examples, "tutorial-sign-test" ->
                                    Some "test tutorial.sign/negative { tutorial.sign(-2) => -1 }"
                                | Topic.Examples, "value-expression-test-expectation" ->
                                    Some "test tutorial.sign/value-expression { tutorial.sign(2) => value .add(0, 1) }"
                                | Topic.Examples, "runtime-error-test-expectation" ->
                                    Some "test tutorial.sign/divide-by-zero { .divide(tutorial.sign(2), 0) => error RUNTIME_DIVIDE_BY_ZERO }"
                                | Topic.Examples, "tutorial-sign-example" ->
                                    Some "example tutorial.sign/negative { tutorial.sign(-2) => -1 }"
                                | _ -> None
                            match source with
                            | Some source -> { example with Source = source }
                            | None -> example)
                    if topic = Topic.Define then
                        examples @ [ tutorialSpanSourceExample; tutorialListFoldSourceExample ]
                    elif topic = Topic.Examples then
                        examples
                        @ [ { Name = "effect-count-test-v2"
                              Description = "Flow/2 test with exact provider counts for the attached user word and its nested helper calls."
                              Source = "test tutorial.queue/reads-once {\n    tutorial.queue()\n    => \"queued\" effects {\n        fs.read: 2\n        fs.write: 0\n    }\n}" }
                            tutorialListFoldSourceExample
                            tutorialListFoldCaseExample ]
                    else examples
                RequestExamples = requestExamples }

    let content (topic: Topic) = contentForVersion 1 topic
