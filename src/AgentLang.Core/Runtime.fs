namespace AgentLang

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Text.Json
open System.Text.Json.Nodes

module Runtime =
    type private ReplacementBackup =
        { Word: WordEntry
          Tests: Map<string, TestDefinition>
          Examples: Map<string, ExampleDefinition> }

    type private DictionaryState =
        { Words: Map<string, WordEntry>
          WordIds: Map<string, string>
          Deprecated: Set<string>
          Records: Map<string, RecordEntry>
          Scalars: Map<string, ScalarEntry>
          Tests: Map<string, TestDefinition>
          Examples: Map<string, ExampleDefinition>
          History: Map<string, WordDefinition list>
          Replacements: Map<string, ReplacementBackup> }

    /// One executable view of one immutable dictionary projection. Detached
    /// bodies in the snapshot are verified against this exact program handle.
    type private RuntimeSnapshot =
        { State: DictionaryState
          Words: Map<string, WordEntry>
          Context: Compiler.IrLoweringContext
          Program: VerifiedIrProgram
          TestBodies: Map<string, VerifiedIrBody>
          TestExpectationBodies: Map<string, VerifiedIrBody>
          ExampleBodies: Map<string, VerifiedIrBody> }

    type private TaskSession =
        { Id: string
          Goal: string
          Snapshot: DictionaryState
          ExecutableSnapshot: RuntimeSnapshot
          StorageSnapshot: StoreSnapshot option
          ManifestSnapshot: ProjectManifest option
          ManifestHashSnapshot: string option
          VirtualFilesSnapshot: Map<string, string>
          ClockSnapshot: string
          mutable Active: bool
          mutable Inspected: Set<string>
          mutable Used: Set<string>
          mutable Created: Set<string>
          mutable TestsRun: int
          mutable TestsFailed: int
          mutable EffectCounts: Map<string, int>
          mutable Errors: string list
          mutable LogWarning: StorageError option }

    type private TestCaseResult =
        { Name: string
          Word: string
          Passed: bool
          Error: Diagnostic option
          Actual: Value list
          Expected: TestExpectation
          ExpectedValue: Value option
          Instructions: Set<SourceSiteId>
          BranchOutcomes: Set<SourceSiteId * string> }

    type private Trace =
        { mutable Steps: int
          mutable CoverageInstructions: Set<SourceSiteId>
          mutable CoverageBranches: Set<SourceSiteId * string>
          mutable FileSystem: Map<string, string>
          mutable Effects: Map<string, int>
          mutable Console: string list
          CoverageTarget: string option
          Isolated: bool }

    let rec private namedTypeReferences = function
        | TNamed name -> Set.singleton name
        | TList item | TOption item -> namedTypeReferences item
        | TResult(ok, error) -> Set.union (namedTypeReferences ok) (namedTypeReferences error)
        | _ -> Set.empty

    let private expressionTypeReferences (body: Expr list) =
        let rec collect (expressions: Expr list) =
            expressions
            |> List.fold (fun found expression ->
                match expression with
                | ConstructContainer(_, arguments, _) ->
                    arguments
                    |> List.map namedTypeReferences
                    |> Set.unionMany
                    |> Set.union found
                | Scope(body, _) -> Set.union found (collect body)
                | If(thenBranch, elseBranch, _)
                | MatchOption(_, thenBranch, elseBranch, _) ->
                    Set.union found (Set.union (collect thenBranch) (collect elseBranch))
                | MatchResult(_, _, okBranch, errorBranch, _) ->
                    Set.union found (Set.union (collect okBranch) (collect errorBranch))
                | _ -> found) Set.empty
        collect body

    let private testExpressions (test: TestDefinition) =
        match test.Expected with
        | ExpectedExpression expected -> test.Body @ expected
        | _ -> test.Body

    let private isValueInspectionLimit (diagnostic: Diagnostic) =
        diagnostic.Code.StartsWith("VALUE_INSPECTION_", StringComparison.Ordinal)
        && diagnostic.Code.EndsWith("_LIMIT", StringComparison.Ordinal)

    let private inspectStructuredValues (program: VerifiedIrProgram) (values: Value list) =
        try
            Choice1Of2(ValueInspection.toData program values)
        with
        | LanguageException diagnostic when isValueInspectionLimit diagnostic -> Choice2Of2 diagnostic

    let private preflightStructuredTestResults (program: VerifiedIrProgram) (results: TestCaseResult list) =
        for result in results do
            match result.Expected with
            | ExpectedExpression _ ->
                inspectStructuredValues program result.Actual |> ignore
                result.ExpectedValue |> Option.iter (fun value -> inspectStructuredValues program [ value ] |> ignore)
            | _ -> ()

    type private SyntaxDescriptor =
        { Name: string
          Syntax: string
          Inputs: string list
          Outputs: string list
          TypeParameters: string list
          Effects: string list
          EffectRule: string
          Documentation: string
          Coverage: string list }

    let private jsonNode<'T> (value: 'T) : JsonNode = JsonSerializer.SerializeToNode<'T>(value)
    let private jstr (value: string) = JsonValue.Create(value) :> JsonNode
    let private jbool (value: bool) = JsonValue.Create(value) :> JsonNode
    let private jint (value: int) = JsonValue.Create(value) :> JsonNode

    let private response (ok: bool) (kind: string) (text: string) (data: JsonNode option) (error: Diagnostic option) =
        let node = JsonObject()
        node["ok"] <- jbool ok
        node["kind"] <- jstr kind
        node["text"] <- jstr text
        match data with Some value -> node["data"] <- value | None -> ()
        match error with
        | Some value ->
            let problem = JsonObject()
            problem["code"] <- jstr value.Code
            problem["message"] <- jstr value.Message
            match value.Word with Some word -> problem["word"] <- jstr word | None -> ()
            match value.Span with
            | Some span ->
                let source = JsonObject()
                source["file"] <- jstr span.File
                source["line"] <- jint span.Line
                source["column"] <- jint span.Column
                source["length"] <- jint span.Length
                problem["span"] <- source
            | None -> ()
            problem["expected"] <- jsonNode value.Expected
            problem["actual"] <- jsonNode value.Actual
            node["error"] <- problem
        | None -> ()
        node

    let private success (kind: string) (text: string) (data: JsonNode option) = response true kind text data None

    let private error code message word span expected actual =
        Diagnostics.raiseError code message word span expected actual

    let private lowerFirst (name: string) =
        if String.IsNullOrEmpty name then name else name.Substring(0, 1).ToLowerInvariant() + name.Substring(1)

    let private wordDef name inputs outputs effects docs source =
        { Name = name
          Inputs = inputs
          Outputs = outputs
          Effects = effects
          Maturity = LibraryWord
          Revision = 1
          Documentation = docs
          Body = []
          SourceText = source
          Span = { File = "<generated>"; Line = 1; Column = 1; Length = max 1 name.Length } }

    let private entry (definition: WordDefinition) (builtin: Builtin option) (status: WordStatus) (maturity: WordMaturity) (revision: int) : WordEntry =
        { Definition = definition; Builtin = builtin; Status = status; Maturity = maturity; Revision = revision }

    let private newWordIdentity () = "word_" + Guid.NewGuid().ToString("N")

    type Engine(projectDirectory: string, capabilities: Set<string>, ?clockValue: string) =
        let projectRoot = if String.IsNullOrWhiteSpace projectDirectory then None else Some(Path.GetFullPath projectDirectory)
        let store = projectRoot |> Option.map Storage.create
        let mutable storageGeneration = 0L
        let mutable storageAuthority = EmptyAuthority
        let mutable currentManifest: ProjectManifest option = None
        let mutable currentManifestHash: string option = None
        let mutable lastExportWarning: StorageError option = None
        let mutable fixedClock = defaultArg clockValue "2000-01-01T00:00:00Z"
        let syntaxDescriptors =
            [ { Name = "list.empty"; Syntax = "list.empty<T>"; Inputs = []; Outputs = [ "List<T>" ]; TypeParameters = [ "T" ]; Effects = []; EffectRule = "none"; Documentation = "Constructs an empty List<T>. T must be a declared closed type."; Coverage = [] }
              { Name = "list.singleton"; Syntax = "T list.singleton<T>"; Inputs = [ "T" ]; Outputs = [ "List<T>" ]; TypeParameters = [ "T" ]; Effects = []; EffectRule = "none"; Documentation = "Constructs a one-element List<T> after checking the payload against the explicit T."; Coverage = [] }
              { Name = "option.none"; Syntax = "option.none<T>"; Inputs = []; Outputs = [ "Option<T>" ]; TypeParameters = [ "T" ]; Effects = []; EffectRule = "none"; Documentation = "Constructs a typed none; the element type is always explicit."; Coverage = [] }
              { Name = "option.some"; Syntax = "T option.some<T>"; Inputs = [ "T" ]; Outputs = [ "Option<T>" ]; TypeParameters = [ "T" ]; Effects = []; EffectRule = "none"; Documentation = "Constructs a typed some after checking the payload against the explicit T."; Coverage = [] }
              { Name = "result.ok"; Syntax = "T result.ok<T, E>"; Inputs = [ "T" ]; Outputs = [ "Result<T, E>" ]; TypeParameters = [ "T"; "E" ]; Effects = []; EffectRule = "none"; Documentation = "Constructs a typed ok; both result type parameters are always explicit."; Coverage = [] }
              { Name = "result.error"; Syntax = "E result.error<T, E>"; Inputs = [ "E" ]; Outputs = [ "Result<T, E>" ]; TypeParameters = [ "T"; "E" ]; Effects = []; EffectRule = "none"; Documentation = "Constructs a typed error; the inactive success type remains explicit."; Coverage = [] }
              { Name = "list.map"; Syntax = "list.map <word>"; Inputs = [ "List<T>" ]; Outputs = [ "List<U>" ]; TypeParameters = [ "T"; "U" ]; Effects = []; EffectRule = "inherits callback word effects"; Documentation = "Statically names one callback word with signature T -> U. The callback is type checked before any element is visited."; Coverage = [ "empty"; "nonempty" ] }
              { Name = "list.filter"; Syntax = "list.filter <word>"; Inputs = [ "List<T>" ]; Outputs = [ "List<T>" ]; TypeParameters = [ "T" ]; Effects = []; EffectRule = "inherits callback word effects"; Documentation = "Statically names one callback word with signature T -> Bool. Retains elements for true and drops them for false."; Coverage = [ "empty"; "nonempty"; "keep"; "drop" ] }
              { Name = "list.each"; Syntax = "list.each <word>"; Inputs = [ "List<T>" ]; Outputs = [ "Unit" ]; TypeParameters = [ "T" ]; Effects = []; EffectRule = "inherits callback word effects"; Documentation = "Statically names one callback word with signature T -> Unit and visits each element."; Coverage = [ "empty"; "nonempty" ] }
              { Name = "match-option"; Syntax = "match-option / some <local> / none / end"; Inputs = [ "Option<T> (top of stack)" ]; Outputs = [ "same stack from both cases" ]; TypeParameters = [ "T" ]; Effects = []; EffectRule = "union of both case effects"; Documentation = "Requires both cases. The some payload local exists only inside its case; both cases must leave identical stack and outer-local types."; Coverage = [ "some"; "none" ] }
              { Name = "match-result"; Syntax = "match-result / ok <local> / error <local> / end"; Inputs = [ "Result<T, E> (top of stack)" ]; Outputs = [ "same stack from both cases" ]; TypeParameters = [ "T"; "E" ]; Effects = []; EffectRule = "union of both case effects"; Documentation = "Requires both cases. Each payload local exists only inside its own case; both cases must leave identical stack and outer-local types."; Coverage = [ "ok"; "error" ] } ]

        let syntaxDescriptorJson (descriptor: SyntaxDescriptor) =
            let node = JsonObject()
            node["name"] <- jstr descriptor.Name
            node["kind"] <- jstr "syntax"
            node["syntax"] <- jstr descriptor.Syntax
            node["inputs"] <- jsonNode descriptor.Inputs
            node["outputs"] <- jsonNode descriptor.Outputs
            node["typeParameters"] <- jsonNode descriptor.TypeParameters
            node["effects"] <- jsonNode descriptor.Effects
            node["effectRule"] <- jstr descriptor.EffectRule
            node["documentation"] <- jstr descriptor.Documentation
            node["coverageOutcomes"] <- jsonNode descriptor.Coverage
            node
        let mutable data =
            { Words = Compiler.primitives
              WordIds = Map.empty
              Deprecated = Set.empty
              Records = Map.empty
              Scalars = Map.empty
              Tests = Map.empty
              Examples = Map.empty
              History = Map.empty
              Replacements = Map.empty }
        let mutable activeSnapshot: RuntimeSnapshot option = None

        let currentSnapshot () =
            activeSnapshot
            |> Option.defaultWith (fun () -> error "RUNTIME_SNAPSHOT_UNAVAILABLE" "No verified executable snapshot is active." None None [] [])

        let mutable virtualFiles = Map.empty
        let mutable activeTask: TaskSession option = None
        let mutable previousTaskLog: JsonObject option = None
        let mutable taskCounter = 0

        let userWords (state: DictionaryState) = state.Words |> Map.filter (fun _ value -> value.Builtin.IsNone)
        let recordDefinitions (state: DictionaryState) = state.Records |> Map.map (fun _ value -> value.Definition)
        let scalarDefinitions (state: DictionaryState) = state.Scalars |> Map.map (fun _ value -> value.Definition)
        let knownTypes (state: DictionaryState) =
            Set.union (state.Records |> Map.toSeq |> Seq.map fst |> Set.ofSeq) (state.Scalars |> Map.toSeq |> Seq.map fst |> Set.ofSeq)

        let makeGenerated (state: DictionaryState) : Map<string, WordEntry> =
            let recordWords =
                state.Records
                |> Map.toList
                |> List.collect (fun (name, recordEntry) ->
                    let record = recordEntry.Definition
                    let prefix = lowerFirst name
                    let constructorName = prefix + ".new"
                    let constructor =
                        wordDef constructorName (record.Fields |> List.map (fun field -> field.Type)) [ TNamed name ] Set.empty "Constructs a value after all record fields pass their declared types." record.SourceText
                    let constructorEntry = entry constructor (Some(RecordConstructor name)) recordEntry.Status LibraryWord 1
                    let getters =
                        record.Fields
                        |> List.map (fun field ->
                            let accessorName = prefix + "." + field.Name
                            let definition = wordDef accessorName [ TNamed name ] [ field.Type ] Set.empty $"Returns the {field.Name} field of a {name}." record.SourceText
                            accessorName, entry definition (Some(RecordAccessor(name, field.Name))) recordEntry.Status LibraryWord 1)
                    (constructorName, constructorEntry) :: getters)
            let scalarWords =
                state.Scalars
                |> Map.toList
                |> List.collect (fun (name, scalarEntry) ->
                    let scalar = scalarEntry.Definition
                    let constructorName = name + ".new"
                    let effects =
                        scalar.Validator
                        |> Option.bind (fun validator -> state.Words.TryFind validator)
                        |> Option.map (fun validatorEntry -> validatorEntry.Definition.Effects)
                        |> Option.defaultValue Set.empty
                    let constructor = wordDef constructorName [ scalar.BaseType ] [ TNamed name ] effects "Constructs a nominal scalar; an optional pure validator must accept it." scalar.SourceText
                    let accessorName = name + ".value"
                    let accessor = wordDef accessorName [ TNamed name ] [ scalar.BaseType ] Set.empty "Explicitly unwraps a nominal scalar to its underlying value." scalar.SourceText
                    [ constructorName, entry constructor (Some(ScalarConstructor name)) scalarEntry.Status LibraryWord 1
                      accessorName, entry accessor (Some(ScalarAccessor name)) scalarEntry.Status LibraryWord 1 ])
            let generatedWords = recordWords @ scalarWords
            match generatedWords |> List.groupBy fst |> List.tryFind (fun (_, entries) -> entries.Length > 1) with
            | Some(name, _) -> error "NAME_GENERATED_COLLISION" $"Generated type word '{name}' has more than one owner. Rename the type or record field." (Some name) None [] []
            | None ->
                let syntaxNames = syntaxDescriptors |> List.map (fun descriptor -> descriptor.Name) |> Set.ofList
                match generatedWords |> List.tryFind (fun (name, _) -> syntaxNames.Contains name) with
                | Some(name, _) -> error "NAME_GENERATED_COLLISION" $"Generated type word '{name}' collides with reserved language syntax." (Some name) None [] []
                | None -> Map.ofList generatedWords

        let effectiveWords (state: DictionaryState) : Map<string, WordEntry> =
            let generated = makeGenerated state
            let baseWords = Compiler.primitives
            let collisions = generated |> Map.exists (fun name _ -> baseWords.ContainsKey name)
            if collisions then error "NAME_GENERATED_COLLISION" "A generated record/type word collides with a standard primitive." None None [] []
            let userCollisions =
                generated
                |> Map.tryPick (fun name _ ->
                    state.Words.TryFind name
                    |> Option.filter (fun value -> value.Builtin.IsNone)
                    |> Option.map (fun _ -> name))
            match userCollisions with
            | Some name -> error "NAME_GENERATED_COLLISION" $"Generated type word '{name}' collides with a user-defined word." (Some name) None [] []
            | None -> ()
            let withGenerated = Map.fold (fun found name value -> Map.add name value found) baseWords generated
            Map.fold (fun found name value ->
                if value.Builtin.IsNone then Map.add name value found else found) withGenerated state.Words

        let wordIdentity (state: DictionaryState) (item: WordEntry) =
            match item.Builtin with
            | Some(BuiltinOp _) -> "primitive_" + item.Definition.Name
            | Some _ -> "generated_" + item.Definition.Name
            | None ->
                match state.WordIds.TryFind item.Definition.Name with
                | Some identity -> identity
                | None -> error "WORD_ID_MISSING" $"Word '{item.Definition.Name}' has no stable identity." (Some item.Definition.Name) None [] []

        let log (kind: string) (name: string) =
            match activeTask with
            | Some task when task.Active ->
                match kind with
                | "inspect" -> task.Inspected <- Set.add name task.Inspected
                | "use" -> task.Used <- Set.add name task.Used
                | "create" -> task.Created <- Set.add name task.Created
                | "effect" -> task.EffectCounts <- Map.change name (fun count -> Some(defaultArg count 0 + 1)) task.EffectCounts
                | "error" -> task.Errors <- task.Errors @ [ name ]
                | _ -> ()
            | _ -> ()

        let instructionId (span: SourceSpan) = $"{span.File}:{span.Line}:{span.Column}"

        let mutateEffect (trace: Trace) name =
            trace.Effects <- Map.change name (fun count -> Some(defaultArg count 0 + 1)) trace.Effects
            // Test effects run against isolated virtual providers and are not task effects.
            if not trace.Isolated then log "effect" name

        let countInstruction (trace: Trace) (currentWord: string) (site: SourceSiteId) (span: SourceSpan) (isAuthoredSite: bool) =
            trace.Steps <- trace.Steps + 1
            if trace.Steps > 10000 then error "RUNTIME_STEP_LIMIT" "Execution exceeded the 10,000 instruction limit." (Some currentWord) (Some span) [] []
            if trace.CoverageTarget = Some currentWord && isAuthoredSite then
                trace.CoverageInstructions <- Set.add site trace.CoverageInstructions

        let countBranchOutcome (trace: Trace) (currentWord: string) (site: SourceSiteId) (outcome: string) (isAuthoredSite: bool) =
            if trace.CoverageTarget = Some currentWord && isAuthoredSite then
                trace.CoverageBranches <- Set.add (site, outcome) trace.CoverageBranches

        let createTrace coverageTarget fileSystem =
            { Steps = 0
              CoverageInstructions = Set.empty
              CoverageBranches = Set.empty
              FileSystem = fileSystem
              Effects = Map.empty
              Console = []
              CoverageTarget = coverageTarget
              Isolated = coverageTarget.IsSome }

        let topologicalWords (state: DictionaryState) : WordEntry list =
            let words = userWords state |> Map.filter (fun _ value -> value.Status = Persistent)
            let visited = System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
            let visiting = System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
            let output = ResizeArray<WordEntry>()
            let rec visit name =
                if visiting.Contains name then error "WORD_DEPENDENCY_CYCLE" $"Word dependency cycle includes '{name}'." (Some name) None [] [ name ]
                if not (visited.Contains name) then
                    visiting.Add name |> ignore
                    let word = words[name]
                    for dependency in Compiler.dependencies word.Definition.Body do
                        if words.ContainsKey dependency then visit dependency
                    visiting.Remove name |> ignore
                    visited.Add name |> ignore
                    output.Add word
            words |> Map.toSeq |> Seq.map fst |> Seq.iter visit
            List.ofSeq output

        let durableState (state: DictionaryState) =
            let restored: DictionaryState =
                Map.fold
                    (fun (current: DictionaryState) name (backup: ReplacementBackup) ->
                        match current.Words.TryFind name with
                        | Some value when value.Status = Candidate || value.Status = Temporary ->
                            let tests =
                                current.Tests
                                |> Map.filter (fun _ test -> test.Word <> name)
                                |> fun found -> Map.fold (fun acc key value -> Map.add key value acc) found backup.Tests
                            let examples =
                                current.Examples
                                |> Map.filter (fun _ example -> example.Word <> name)
                                |> fun found -> Map.fold (fun acc key value -> Map.add key value acc) found backup.Examples
                            { current with
                                Words = Map.add name backup.Word current.Words
                                Tests = tests
                                Examples = examples
                                Replacements = Map.remove name current.Replacements }
                        | _ -> current)
                    state
                    state.Replacements
            let persistentWords = restored.Words |> Map.filter (fun _ value -> value.Builtin.IsSome || value.Status = Persistent)
            let projected =
                { restored with
                    Words = persistentWords
                    WordIds = restored.WordIds |> Map.filter (fun name _ -> persistentWords.ContainsKey name)
                    Records = restored.Records |> Map.filter (fun _ value -> value.Status = Persistent)
                    Scalars = restored.Scalars |> Map.filter (fun _ value -> value.Status = Persistent)
                    Tests = Map.empty
                    Examples = Map.empty
                    Replacements = Map.empty }
            let words = effectiveWords projected
            let isDurableCase target body = words.ContainsKey target && (Compiler.dependencies body |> Set.forall words.ContainsKey)
            { projected with
                Tests = restored.Tests |> Map.filter (fun _ test -> isDurableCase test.Word (testExpressions test))
                Examples = restored.Examples |> Map.filter (fun _ example -> isDurableCase example.Word example.Body) }

        let serializeWord (word: WordEntry) =
            let lines = word.Definition.SourceText.Replace("\r\n", "\n").Split('\n') |> Array.filter (fun line -> not (line.Trim().StartsWith("maturity ", StringComparison.Ordinal) || line.Trim().StartsWith("revision ", StringComparison.Ordinal)))
            if lines.Length = 0 then word.Definition.SourceText
            else
                let maturity = if word.Maturity = LibraryWord then "maturity library" else "maturity project"
                String.concat Environment.NewLine ([ lines[0]; maturity; $"revision {word.Revision}" ] @ (lines |> Array.skip 1 |> Array.toList))

        let sourceFor (state: DictionaryState) =
            let state = durableState state
            let sections = ResizeArray<string>()
            for _, item in state.Records |> Map.toSeq |> Seq.sortBy fst do if item.Status = Persistent then sections.Add(Source.renderRecord item.Definition)
            for _, item in state.Scalars |> Map.toSeq |> Seq.sortBy fst do if item.Status = Persistent then sections.Add(Source.renderScalar item.Definition)
            for word in topologicalWords state do sections.Add(Source.renderWord true word.Definition)
            let durableTypes =
                Set.union
                    (state.Records |> Map.toSeq |> Seq.choose (fun (name, item) -> if item.Status = Persistent then Some(lowerFirst name) else None) |> Set.ofSeq)
                    (state.Scalars |> Map.toSeq |> Seq.choose (fun (name, item) -> if item.Status = Persistent then Some name else None) |> Set.ofSeq)
            let durableTests =
                state.Tests
                |> Map.toList
                |> List.map snd
                |> List.filter (fun test ->
                    state.Words.TryFind test.Word |> Option.exists (fun word -> word.Status = Persistent)
                    || (durableTypes |> Set.exists (fun prefix -> test.Word = prefix + ".new" || test.Word = prefix + ".value" || test.Word.StartsWith(prefix + ".", StringComparison.Ordinal)))
                )
                |> List.sortBy (fun test -> test.Word, test.Name)
            for test in durableTests do sections.Add(Source.renderTest test)
            let durableExamples =
                state.Examples
                |> Map.toList
                |> List.map snd
                |> List.filter (fun example ->
                    state.Words.TryFind example.Word |> Option.exists (fun word -> word.Status = Persistent)
                    || (durableTypes |> Set.exists (fun prefix -> example.Word = prefix + ".new" || example.Word = prefix + ".value" || example.Word.StartsWith(prefix + ".", StringComparison.Ordinal)))
                )
                |> List.sortBy (fun example -> example.Word, example.Name)
            for example in durableExamples do sections.Add(Source.renderExample example)
            String.concat (Environment.NewLine + Environment.NewLine) sections + Environment.NewLine

        let sourceForAgent (source: string) =
            source.Replace("\r\n", "\n").Split('\n')
            |> Array.filter (fun line ->
                let trimmed = line.Trim()
                not (trimmed.StartsWith("maturity ", StringComparison.Ordinal) || trimmed.StartsWith("revision ", StringComparison.Ordinal)))
            |> String.concat Environment.NewLine

        let addTest (results: Map<string, TestDefinition>) (test: TestDefinition) =
            Map.add ($"{test.Word}/{test.Name}") test results

        let addExample (results: Map<string, ExampleDefinition>) (example: ExampleDefinition) =
            Map.add ($"{example.Word}/{example.Name}") example results

        let raiseStorageError (storageError: StorageError) =
            error storageError.Code storageError.Message None None [] (storageError.Path |> Option.toList)

        let parseProjectSource sourceName source =
            match Parser.parse sourceName source with
            | Error diagnostic -> raise (LanguageException diagnostic)
            | Ok parsed -> parsed

        let parsedState (parsed: ParsedSource) (previous: DictionaryState) (identityByName: Map<string, string>) =
            let records: Map<string, RecordEntry> =
                parsed.Records
                |> List.map (fun (value: RecordDefinition) -> value.Name, ({ Definition = value; Status = Persistent }: RecordEntry))
                |> Map.ofList
            let scalars: Map<string, ScalarEntry> =
                parsed.Scalars
                |> List.map (fun (value: ScalarTypeDefinition) -> value.Name, ({ Definition = value; Status = Persistent }: ScalarEntry))
                |> Map.ofList
            let words =
                parsed.Words
                |> List.map (fun value -> value.Name, entry value None Persistent value.Maturity value.Revision)
                |> Map.ofList
            let wordIds =
                parsed.Words
                |> List.map (fun value -> value.Name, (identityByName.TryFind value.Name |> Option.defaultWith newWordIdentity))
                |> Map.ofList
            let state =
                { previous with
                    Words = Map.fold (fun found name value -> Map.add name value found) Compiler.primitives words
                    WordIds = wordIds
                    Deprecated = Set.empty
                    Records = records
                    Scalars = scalars
                    Tests = parsed.Tests |> List.fold addTest Map.empty
                    Examples = parsed.Examples |> List.fold addExample Map.empty
                    History = Map.empty
                    Replacements = Map.empty }
            state

        let currentManifestFor (state: DictionaryState) (baseline: DictionaryState) (actor: string) =
            let durable = durableState state
            let exportText = sourceFor durable
            let projectObject = Storage.sourceObject StorageObjectKind.ProjectSource exportText
            let sourceObjects = ResizeArray<SourceObject>()
            sourceObjects.Add projectObject

            let typeSources =
                [ for KeyValue(name, item) in durable.Records do
                      let sourceObject = Storage.sourceObject StorageObjectKind.TypeDefinition (Source.renderRecord item.Definition)
                      sourceObjects.Add sourceObject
                      yield { Name = name; Definition = sourceObject.Reference }
                  for KeyValue(name, item) in durable.Scalars do
                      let sourceObject = Storage.sourceObject StorageObjectKind.TypeDefinition (Source.renderScalar item.Definition)
                      sourceObjects.Add sourceObject
                      yield { Name = name; Definition = sourceObject.Reference } ]

            let manifestBase = currentManifest |> Option.defaultValue { FormatVersion = 1; ProjectSource = projectObject.Reference; Types = []; Words = []; Revisions = [] }
            let revisions = ResizeArray<WordRevision>(manifestBase.Revisions)
            let mutable revisionKeys = revisions |> Seq.map (fun item -> item.WordId, item.Revision) |> Set.ofSeq
            let taskId = activeTask |> Option.filter (fun task -> task.Active) |> Option.map (fun task -> task.Id)
            let timestamp = DateTimeOffset.Parse(fixedClock, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime()

            let attachedSources (wordName: string) =
                let tests =
                    durable.Tests
                    |> Map.toSeq
                    |> Seq.map snd
                    |> Seq.filter (fun test -> test.Word = wordName)
                    |> Seq.sortBy (fun test -> test.Name)
                    |> Seq.map (fun test ->
                        let sourceObject = Storage.sourceObject StorageObjectKind.TestDefinition (Source.renderTest test)
                        sourceObjects.Add sourceObject
                        sourceObject.Reference)
                    |> Seq.toList
                let examples =
                    durable.Examples
                    |> Map.toSeq
                    |> Seq.map snd
                    |> Seq.filter (fun example -> example.Word = wordName)
                    |> Seq.sortBy (fun example -> example.Name)
                    |> Seq.map (fun example ->
                        let sourceObject = Storage.sourceObject StorageObjectKind.ExampleDefinition (Source.renderExample example)
                        sourceObjects.Add sourceObject
                        sourceObject.Reference)
                    |> Seq.toList
                tests, examples

            let addRevision revisionActor revisionTaskId (item: WordEntry) =
                match durable.WordIds.TryFind item.Definition.Name with
                | None -> error "WORD_ID_MISSING" $"Persistent word '{item.Definition.Name}' has no stable identity." (Some item.Definition.Name) None [] []
                | Some wordId when revisionKeys.Contains(wordId, item.Definition.Revision) -> ()
                | Some wordId ->
                    let definitionObject = Storage.sourceObject StorageObjectKind.WordDefinition (Source.renderWord true item.Definition)
                    sourceObjects.Add definitionObject
                    let tests, examples = attachedSources item.Definition.Name
                    let revision =
                        { WordId = wordId
                          Name = item.Definition.Name
                          Revision = item.Definition.Revision
                          Definition = definitionObject.Reference
                          Tests = tests
                          Examples = examples
                          Maturity = item.Maturity
                          Actor = revisionActor
                          TaskId = revisionTaskId
                          TimestampUtc = timestamp
                          Deprecated = durable.Deprecated.Contains item.Definition.Name }
                    revisions.Add revision
                    revisionKeys <- Set.add (wordId, item.Definition.Revision) revisionKeys

            // A legacy dictionary has no authoritative revision objects. On its
            // first manifest commit, record its currently durable definitions as
            // the honest migration baseline before adding staged revisions.
            if currentManifest.IsNone then
                let legacyBase = durableState baseline
                legacyBase.Words
                |> Map.toSeq
                |> Seq.map snd
                |> Seq.filter (fun item -> item.Builtin.IsNone && item.Status = Persistent)
                |> Seq.sortBy (fun item -> item.Definition.Name)
                |> Seq.iter (addRevision "host" None)

            durable.Words
            |> Map.toSeq
            |> Seq.map snd
            |> Seq.filter (fun item -> item.Builtin.IsNone && item.Status = Persistent)
            |> Seq.sortBy (fun item -> item.Definition.Name)
            |> Seq.iter (addRevision actor taskId)

            let heads =
                durable.Words
                |> Map.toSeq
                |> Seq.map snd
                |> Seq.filter (fun item -> item.Builtin.IsNone && item.Status = Persistent)
                |> Seq.map (fun item ->
                    match durable.WordIds.TryFind item.Definition.Name with
                    | None -> error "WORD_ID_MISSING" $"Persistent word '{item.Definition.Name}' has no stable identity." (Some item.Definition.Name) None [] []
                    | Some wordId ->
                        { WordId = wordId
                          CurrentName = item.Definition.Name
                          CurrentRevision = item.Definition.Revision
                          Deprecated = durable.Deprecated.Contains item.Definition.Name })
                |> Seq.toList

            let manifest =
                { FormatVersion = 1
                  ProjectSource = projectObject.Reference
                  Types = typeSources
                  Words = heads
                  Revisions = List.ofSeq revisions }
            manifest, List.ofSeq sourceObjects, exportText

        let publish (baseline: DictionaryState) (state: DictionaryState) actor =
            match store with
            | None -> lastExportWarning <- None
            | Some projectStore ->
                let manifest, sources, exportText = currentManifestFor state baseline actor
                match Storage.commit projectStore storageGeneration manifest sources exportText with
                | Error storageError -> raiseStorageError storageError
                | Ok committed ->
                    storageGeneration <- committed.Generation
                    storageAuthority <- committed.Authority
                    currentManifest <- Some manifest
                    currentManifestHash <- committed.ManifestHash
                    lastExportWarning <- committed.ExportWarning

        let validateGraph (state: DictionaryState) =
            let words = effectiveWords state
            let user = userWords state
            for entry in user |> Map.toSeq |> Seq.map snd do Compiler.checkDefinition (knownTypes state) words entry.Definition |> ignore
            for record in state.Records |> Map.toSeq |> Seq.map (fun (_, item) -> item.Definition) do
                let rec validateFieldType (field: RecordField) = function
                    | TNamed name when not ((knownTypes state).Contains name) ->
                        error "TYPE_UNKNOWN_FIELD_TYPE" $"Field '{record.Name}.{field.Name}' uses undeclared type '{name}'." (Some record.Name) (Some record.Span) [ "declared record or scalar type" ] [ name ]
                    | TList item | TOption item -> validateFieldType field item
                    | TResult(ok, failure) -> validateFieldType field ok; validateFieldType field failure
                    | TVar _ -> error "TYPE_UNSUPPORTED_GENERIC" "Record fields cannot contain free generic variables." (Some record.Name) (Some record.Span) [] [ Types.format field.Type ]
                    | _ -> ()
                for field in record.Fields do validateFieldType field field.Type
            for scalar in state.Scalars |> Map.toSeq |> Seq.map (fun (_, item) -> item.Definition) do
                let deps = Compiler.checkScalarValidator (knownTypes state) words scalar
                match scalar.Validator with
                | Some validator when deps.Contains(scalar.Name + ".new") || deps.Contains(scalar.Name + ".value") ->
                    error "TYPE_VALIDATOR_RECURSION" $"Validator '{validator}' cannot construct or unwrap '{scalar.Name}'." (Some scalar.Name) (Some scalar.Span) [] (Set.toList deps)
                | _ -> ()
            for test in state.Tests |> Map.toSeq |> Seq.map (fun (_, value) -> value) do Compiler.checkTest (knownTypes state) words test |> ignore
            for example in state.Examples |> Map.toSeq |> Seq.map (fun (_, value) -> value) do Compiler.checkExample (knownTypes state) words example |> ignore
            let graph =
                words
                |> Map.toSeq
                |> Seq.choose (fun (name, value) ->
                    if value.Builtin.IsSome then None
                    else Some(name, Compiler.dependencies value.Definition.Body))
                |> Map.ofSeq
                |> fun initial ->
                    state.Scalars
                    |> Map.toSeq
                    |> Seq.fold (fun found (_, scalar) ->
                        match scalar.Definition.Validator with
                        | Some validator -> Map.add (scalar.Definition.Name + ".new") (Set.singleton validator) found
                        | None -> found) initial
            let colors = System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal)
            let rec visit path name =
                match colors.TryGetValue name with
                | true, 1 ->
                    let cycle = String.concat " -> " (path @ [ name ])
                    error "WORD_DEPENDENCY_CYCLE" $"Word dependency cycle: {cycle}." (Some name) None [] (path @ [ name ])
                | true, 2 -> ()
                | _ ->
                    colors[name] <- 1
                    for dependency in graph.TryFind name |> Option.defaultValue Set.empty do
                        if graph.ContainsKey dependency then visit (path @ [ name ]) dependency
                    colors[name] <- 2
            graph |> Map.toSeq |> Seq.iter (fun (name, _) -> visit [] name)

        let compileRuntimeSnapshot (state: DictionaryState) =
            validateGraph state
            let words = effectiveWords state
            let context: Compiler.IrLoweringContext =
                { Words = words
                  Records = recordDefinitions state
                  Scalars = scalarDefinitions state
                  WordIds = words |> Map.map (fun _ item -> WordId(wordIdentity state item)) }
            let program = Compiler.compileIrProgram context
            IrInterpreter.validateProgram program
            let compiledTests =
                state.Tests
                |> Map.map (fun _ test -> Compiler.compileIrTestWithExpectationAgainstProgram context program test)
            let testBodies = compiledTests |> Map.map (fun _ (actual, _) -> actual)
            let testExpectationBodies =
                compiledTests
                |> Map.toSeq
                |> Seq.choose (fun (key, (_, expected)) -> expected |> Option.map (fun body -> key, body))
                |> Map.ofSeq
            let exampleBodies =
                state.Examples
                |> Map.map (fun _ example -> Compiler.compileIrExampleAgainstProgram context program example)
            { State = state
              Words = words
              Context = context
              Program = program
              TestBodies = testBodies
              TestExpectationBodies = testExpectationBodies
              ExampleBodies = exampleBodies }

        let sourceSite (snapshot: RuntimeSnapshot) (body: VerifiedIrBody option) (site: SourceSiteId) =
            let bodySource =
                body
                |> Option.map (VerifiedIrBody.inspect >> fun value -> value.BodySourceMap)
                |> Option.defaultValue Map.empty
            bodySource.TryFind site
            |> Option.orElseWith (fun () ->
                (VerifiedIrProgram.inspect snapshot.Program).SourceMap.TryFind site)
            |> Option.defaultWith (fun () ->
                error "IR_SOURCE_SITE_MISSING" "Verified executable source site has no source-map entry." None None [] [ sprintf "%A" site ])

        let siteSpan (snapshot: RuntimeSnapshot) (body: VerifiedIrBody option) (site: SourceSiteId) =
            (sourceSite snapshot body site).SiteSpan

        let isAuthoredCoverageSite (source: IrSourceSite) =
            match source.SourceKind with
            | "synthetic-scope"
            | "synthetic-store-local"
            | "synthetic-load-local" -> false
            | _ -> true

        let instructionCoverageLabel (snapshot: RuntimeSnapshot) site =
            instructionId (siteSpan snapshot None site)

        let branchCoverageLabel (snapshot: RuntimeSnapshot) (site, outcome) =
            instructionCoverageLabel snapshot site + ":" + outcome

        let coverageGapLabels (snapshot: RuntimeSnapshot) (instructions: Set<SourceSiteId>) (branches: Set<SourceSiteId * string>) =
            let instructionLabels = instructions |> Set.toList |> List.map (instructionCoverageLabel snapshot) |> List.sort
            let branchLabels = branches |> Set.toList |> List.map (branchCoverageLabel snapshot) |> List.sort
            instructionLabels @ branchLabels

        let coverageObligations (snapshot: RuntimeSnapshot) (word: string) : Set<SourceSiteId> * Set<SourceSiteId * string> =
            match snapshot.Words.TryFind word with
            | None -> Set.empty, Set.empty
            | Some entry ->
                let id = WordId(wordIdentity snapshot.State entry)
                match (VerifiedIrProgram.inspect snapshot.Program).CoverageByWord.TryFind id with
                | None -> Set.empty, Set.empty
                | Some obligations ->
                    let branchKeys =
                        obligations.BranchOutcomes
                        |> Map.toList
                        |> List.collect (fun (site, outcomes) ->
                            outcomes |> List.map (fun outcome -> site, outcome))
                        |> Set.ofList
                    obligations.CoveredSites, branchKeys

        let interpreterHost (snapshot: RuntimeSnapshot) (trace: Trace) (body: VerifiedIrBody option) =
            let source site = sourceSite snapshot body site
            let definitionSpan (name: string) =
                snapshot.Words.TryFind name |> Option.map (fun entry -> entry.Definition.Span)
            { PreflightEffects = fun effects word _ ->
                  let effectNames = IrEffects.names effects
                  let missing = Set.difference (Set.ofList effectNames) capabilities
                  if not trace.Isolated && not (Set.isEmpty missing) then
                      let expected = effectNames
                      let message, failureWord, failureSpan =
                          match word with
                          | Some name ->
                              let names = String.concat ", " (missing |> Set.toList)
                              $"Execution requires capabilities not granted by the host: {names}.", Some name, definitionSpan name
                          | None -> "The expression requires effects not granted by the host.", None, None
                      error "CAPABILITY_DENIED" message failureWord failureSpan expected (capabilities |> Set.toList)
              ChargeInstruction = fun currentWord site ->
                  let sourceInfo = source site
                  countInstruction trace currentWord site sourceInfo.SiteSpan (isAuthoredCoverageSite sourceInfo)
              RecordBranchOutcome = fun currentWord site outcome ->
                  let sourceInfo = source site
                  countBranchOutcome trace currentWord site outcome (isAuthoredCoverageSite sourceInfo)
              RecordUse = fun name -> log "use" name
              InvokeEffect = fun command ->
                  match command with
                  | ReadVirtualFile(operation, path) ->
                      mutateEffect trace "fs.read"
                      match trace.FileSystem.TryFind path with
                      | Some contents -> EffectString contents
                      | None -> error "EFFECT_FILE_NOT_FOUND" $"Virtual file '{path}' does not exist." (Some operation) None [] [ path ]
                  | VirtualFileExists(_, path) ->
                      mutateEffect trace "fs.read"
                      EffectBool(trace.FileSystem.ContainsKey path)
                  | WriteVirtualFile(_, path, contents) ->
                      mutateEffect trace "fs.write"
                      trace.FileSystem <- Map.add path contents trace.FileSystem
                      EffectUnit
                  | ReadFixedClock _ -> mutateEffect trace "clock.read"; EffectString fixedClock
                  | WriteVirtualConsole(_, contents) ->
                      mutateEffect trace "console.write"
                      trace.Console <- trace.Console @ [ contents ]
                      EffectUnit
              WordDefinitionSpan = definitionSpan
              PrimitiveDefinitionSpan = definitionSpan }

        let executeIRBody (snapshot: RuntimeSnapshot) (executionName: string) (trace: Trace) (body: VerifiedIrBody) =
            let host = interpreterHost snapshot trace (Some body)
            IrInterpreter.executeBody host executionName body

        let executeExpression (snapshot: RuntimeSnapshot) (coverageTarget: string option) (fileSystem: Map<string, string>) (expressions: Expr list) =
            Compiler.checkExpression (knownTypes snapshot.State) snapshot.Words expressions |> ignore
            let body = Compiler.compileIrBodyAgainstProgram snapshot.Context snapshot.Program "<eval>" [] expressions
            let trace = createTrace coverageTarget fileSystem
            let stack = executeIRBody snapshot "<eval>" trace body
            stack, trace

        let activateRuntimeSnapshot (snapshot: RuntimeSnapshot) =
            data <- snapshot.State
            activeSnapshot <- Some snapshot

        let validateStoredProject (projectStore: Store) (manifest: ProjectManifest option) (manifestHash: string option) (projectSource: string option) =
            let parsed = projectSource |> Option.map (parseProjectSource "dictionary.agent")
            let identities =
                manifest
                |> Option.map (fun value -> value.Words |> List.map (fun head -> head.CurrentName, head.WordId) |> Map.ofList)
                |> Option.defaultValue Map.empty
            let proposed =
                match parsed with
                | None -> data
                | Some source -> parsedState source data identities
            validateGraph proposed
            match manifest with
            | None -> proposed
            | Some value ->
                let headsByName = value.Words |> List.map (fun head -> head.CurrentName, head) |> Map.ofList
                let headsById = value.Words |> List.map (fun head -> head.WordId, head) |> Map.ofList
                let currentUserWords = proposed.Words |> Map.filter (fun _ item -> item.Builtin.IsNone && item.Status = Persistent)
                if currentUserWords.Count <> value.Words.Length then
                    error "STORAGE_PROJECT_MISMATCH" "Manifest word heads do not match definitions in the authoritative project source." None None [ string value.Words.Length ] [ string currentUserWords.Count ]
                if Set.ofList (value.Types |> List.map (fun item -> item.Name)) <> knownTypes proposed then
                    error "STORAGE_PROJECT_MISMATCH" "Manifest type names do not match definitions in the authoritative project source." None None (knownTypes proposed |> Set.toList) (value.Types |> List.map (fun item -> item.Name))
                for KeyValue(name, word) in currentUserWords do
                    match headsByName.TryFind name with
                    | None -> error "STORAGE_PROJECT_MISMATCH" $"Project source word '{name}' has no manifest head." (Some name) None [] []
                    | Some head when head.CurrentRevision <> word.Definition.Revision ->
                        error "STORAGE_PROJECT_MISMATCH" $"Project source revision for '{name}' does not match its manifest head." (Some name) None [ string head.CurrentRevision ] [ string word.Definition.Revision ]
                    | Some head ->
                        match value.Revisions |> List.tryFind (fun revision -> revision.WordId = head.WordId && revision.Revision = head.CurrentRevision) with
                        | Some revision when revision.Maturity = word.Maturity && revision.Deprecated = head.Deprecated -> ()
                        | _ -> error "STORAGE_PROJECT_MISMATCH" $"Current word metadata for '{name}' differs from its manifest revision." (Some name) None [] []
                for typeSource in value.Types do
                    match Storage.readSource projectStore typeSource.Definition with
                    | Error storageError -> raiseStorageError storageError
                    | Ok source ->
                        let typeParsed = parseProjectSource ($"<type:{typeSource.Name}>") source
                        let rendered =
                            match typeParsed.Records, typeParsed.Scalars with
                            | [ record ], [] when record.Name = typeSource.Name -> Source.renderRecord record
                            | [], [ scalar ] when scalar.Name = typeSource.Name -> Source.renderScalar scalar
                            | _ -> error "STORAGE_PROJECT_MISMATCH" $"Manifest type object '{typeSource.Name}' does not contain exactly that type." (Some typeSource.Name) None [] []
                        let expected =
                            match proposed.Records.TryFind typeSource.Name, proposed.Scalars.TryFind typeSource.Name with
                            | Some record, _ -> Source.renderRecord record.Definition
                            | _, Some scalar -> Source.renderScalar scalar.Definition
                            | _ -> error "STORAGE_PROJECT_MISMATCH" $"Manifest type '{typeSource.Name}' is missing from the project source." (Some typeSource.Name) None [] []
                        if rendered <> expected then error "STORAGE_PROJECT_MISMATCH" $"Manifest type '{typeSource.Name}' differs from project source." (Some typeSource.Name) None [] []
                let hash = manifestHash |> Option.defaultWith (fun () -> error "STORAGE_INVALID_MANIFEST" "Manifest authority has no manifest hash." None None [] [])
                let mutable loadedHistory: Map<string, WordDefinition list> = Map.empty
                for metadata in value.Revisions |> List.sortBy (fun revision -> revision.WordId, revision.Revision) do
                    match headsById.TryFind metadata.WordId with
                    | None -> error "STORAGE_PROJECT_MISMATCH" $"Revision '{metadata.WordId}/{metadata.Revision}' has no word head." (Some metadata.Name) None [] []
                    | Some head ->
                        match Storage.readRevision projectStore hash metadata.WordId metadata.Revision with
                        | Error storageError -> raiseStorageError storageError
                        | Ok revisionContent ->
                            let definitionParsed = parseProjectSource ($"<revision:{metadata.Name}/{metadata.Revision}>") revisionContent.DefinitionSource
                            match definitionParsed.Words with
                            | [ definition ] when definition.Name = metadata.Name && definition.Revision = metadata.Revision && definition.Maturity = metadata.Maturity ->
                                for testSource in revisionContent.TestSources do
                                    let parsedTest = parseProjectSource "<stored-test>" testSource
                                    match parsedTest.Tests with
                                    | [ test ] when test.Word = metadata.Name -> ()
                                    | _ -> error "STORAGE_PROJECT_MISMATCH" $"Stored test metadata for '{metadata.Name}/{metadata.Revision}' is invalid." (Some metadata.Name) None [] []
                                for exampleSource in revisionContent.ExampleSources do
                                    let parsedExample = parseProjectSource "<stored-example>" exampleSource
                                    match parsedExample.Examples with
                                    | [ example ] when example.Word = metadata.Name -> ()
                                    | _ -> error "STORAGE_PROJECT_MISMATCH" $"Stored example metadata for '{metadata.Name}/{metadata.Revision}' is invalid." (Some metadata.Name) None [] []
                                let previous = loadedHistory.TryFind head.CurrentName |> Option.defaultValue []
                                loadedHistory <- Map.add head.CurrentName (previous @ [ definition ]) loadedHistory
                            | _ -> error "STORAGE_PROJECT_MISMATCH" $"Stored revision {metadata.WordId}/{metadata.Revision} does not match its manifest metadata." (Some metadata.Name) None [] []
                let deprecated = value.Words |> List.filter (fun head -> head.Deprecated) |> List.map (fun head -> head.CurrentName) |> Set.ofList
                { proposed with History = loadedHistory; Deprecated = deprecated }

        let loadProject () =
            match store with
            | None -> compileRuntimeSnapshot data |> activateRuntimeSnapshot
            | Some projectStore ->
                match Storage.load projectStore with
                | Error storageError -> raiseStorageError storageError
                | Ok loaded ->
                    let proposed = validateStoredProject projectStore loaded.Manifest loaded.ManifestHash loaded.ProjectSource
                    let executable = compileRuntimeSnapshot proposed
                    activateRuntimeSnapshot executable
                    storageGeneration <- loaded.Generation
                    storageAuthority <- loaded.Authority
                    currentManifest <- loaded.Manifest
                    currentManifestHash <- loaded.ManifestHash
                    lastExportWarning <- loaded.ExportWarning

        do loadProject ()

        let toJsonValue value = jsonNode (Types.formatValue value)

        let checkedByTest (snapshot: RuntimeSnapshot) (test: TestDefinition) =
            let sites, branches = coverageObligations snapshot test.Word
            let trace = createTrace (Some test.Word) Map.empty
            let bodyKey = $"{test.Word}/{test.Name}"
            let body =
                snapshot.TestBodies.TryFind bodyKey
                |> Option.defaultWith (fun () ->
                    error "IR_TEST_BODY_MISSING" "Compiled test body is absent from its exact executable snapshot." (Some test.Word) (Some test.Span) [] [ bodyKey ])
            let makeResult passed diagnostic actual expectedValue =
                { Name = test.Name
                  Word = test.Word
                  Passed = passed
                  Error = diagnostic
                  Actual = actual
                  Expected = test.Expected
                  ExpectedValue = expectedValue
                  Instructions = trace.CoverageInstructions |> Set.intersect sites
                  BranchOutcomes = trace.CoverageBranches |> Set.intersect branches }
            try
                let stack = executeIRBody snapshot "<test>" trace body
                match test.Expected with
                | ExpectedValue literal ->
                    let expected = Types.literalValue literal
                    let passed = stack = [ expected ]
                    let diagnostic =
                        if passed then None
                        else
                            Some
                                { Code = "TEST_ASSERTION_FAILED"
                                  Message = "Actual value did not equal the expected literal."
                                  Word = Some test.Word
                                  Span = Some test.Span
                                  Expected = [ Types.formatValue expected ]
                                  Actual = stack |> List.map Types.formatValue }
                    makeResult passed diagnostic stack (Some expected)
                | ExpectedRuntimeError code ->
                    let diagnostic =
                        { Code = "TEST_EXPECTED_RUNTIME_ERROR"
                          Message = $"Expected runtime error '{code}', but the test expression completed normally."
                          Word = Some test.Word
                          Span = Some test.Span
                          Expected = [ code ]
                          Actual = stack |> List.map Types.formatValue }
                    makeResult false (Some diagnostic) stack None
                | ExpectedExpression _ ->
                    let expectedBody =
                        snapshot.TestExpectationBodies.TryFind bodyKey
                        |> Option.defaultWith (fun () ->
                            error "IR_TEST_EXPECTATION_BODY_MISSING" "Compiled test expectation is absent from its exact executable snapshot." (Some test.Word) (Some test.Span) [] [ bodyKey ])
                    let expectedTrace = { createTrace None Map.empty with Isolated = true }
                    try
                        let expectedStack = executeIRBody snapshot "<test-expectation>" expectedTrace expectedBody
                        match expectedStack with
                        | [ expectedValue ] ->
                            let passed = stack = [ expectedValue ]
                            let diagnostic =
                                if passed then None
                                else
                                    Some
                                        { Code = "TEST_ASSERTION_FAILED"
                                          Message = "Actual value did not equal the value produced by the pure expectation expression."
                                          Word = Some test.Word
                                          Span = Some test.Span
                                          Expected = [ Types.formatValue expectedValue ]
                                          Actual = stack |> List.map Types.formatValue }
                            makeResult passed diagnostic stack (Some expectedValue)
                        | values ->
                            let diagnostic =
                                { Code = "TEST_EXPECTED_VALUE_STACK"
                                  Message = "The verified value expectation returned an invalid stack shape."
                                  Word = Some test.Word
                                  Span = Some test.Span
                                  Expected = [ "one value" ]
                                  Actual = values |> List.map Types.formatValue }
                            makeResult false (Some diagnostic) stack None
                    with
                    | LanguageException expectedDiagnostic ->
                        let diagnostic =
                            { Code = "TEST_EXPECTED_VALUE_RUNTIME_ERROR"
                              Message = "The pure value expectation failed while being evaluated."
                              Word = Some test.Word
                              Span = expectedDiagnostic.Span |> Option.orElse (Some test.Span)
                              Expected = [ "normal completion" ]
                              Actual = [ expectedDiagnostic.Code ] }
                        makeResult false (Some diagnostic) stack None
            with
            | LanguageException diagnostic ->
                match test.Expected with
                | ExpectedRuntimeError expectedCode when diagnostic.Code = expectedCode -> makeResult true None [] None
                | _ -> makeResult false (Some diagnostic) [] None

        let resultJson (snapshot: RuntimeSnapshot) (result: TestCaseResult) =
            let node = JsonObject()
            node["name"] <- jstr result.Name
            node["word"] <- jstr result.Word
            node["passed"] <- jbool result.Passed
            let addStructuredObservation (fieldName: string) (values: Value list) =
                match inspectStructuredValues snapshot.Program values with
                | Choice1Of2 value -> node[fieldName] <- value
                | Choice2Of2 diagnostic ->
                    node[fieldName] <- null
                    let errorNode = JsonObject()
                    errorNode["code"] <- jstr diagnostic.Code
                    errorNode["message"] <- jstr diagnostic.Message
                    errorNode["expected"] <- jsonNode diagnostic.Expected
                    errorNode["actual"] <- jsonNode diagnostic.Actual
                    node[fieldName + "Error"] <- errorNode
            match result.Expected with
            | ExpectedValue literal -> node["expected"] <- toJsonValue (Types.literalValue literal)
            | ExpectedRuntimeError code ->
                node["expected"] <- jstr ("error " + code)
                node["expectedErrorCode"] <- jstr code
            | ExpectedExpression _ ->
                node["expectedKind"] <- jstr "value-expression"
                addStructuredObservation "actualStructured" result.Actual
                match result.ExpectedValue with
                | Some value ->
                    node["expected"] <- toJsonValue value
                    node["expectedType"] <- jstr (Types.ofValue value |> Types.format)
                    addStructuredObservation "expectedStructured" [ value ]
                | None -> node["expected"] <- jstr "value-expression"
            node["actual"] <- jsonNode (result.Actual |> List.map Types.formatValue)
            match result.Error with
            | Some diagnostic -> node["errorCode"] <- jstr diagnostic.Code; node["message"] <- jstr diagnostic.Message
            | None -> ()
            node

        let runTestsFor (snapshot: RuntimeSnapshot) target =
            let tests =
                snapshot.State.Tests
                |> Map.toList
                |> List.map snd
                |> List.filter (fun test -> target |> Option.forall ((=) test.Word))
                |> List.sortBy (fun test -> test.Word, test.Name)
            tests |> List.map (checkedByTest snapshot)

        let coverageJson (snapshot: RuntimeSnapshot) (word: string) (results: TestCaseResult list) =
            let requiredInstructions, requiredBranches = coverageObligations snapshot word
            let actualInstructions = results |> List.fold (fun found result -> Set.union found result.Instructions) Set.empty
            let actualBranches = results |> List.fold (fun found result -> Set.union found result.BranchOutcomes) Set.empty
            let uncoveredInstructions = Set.difference requiredInstructions actualInstructions
            let uncoveredBranches = Set.difference requiredBranches actualBranches
            let node = JsonObject()
            node["instructionsCovered"] <- jint actualInstructions.Count
            node["instructionsTotal"] <- jint requiredInstructions.Count
            node["branchesCovered"] <- jint actualBranches.Count
            node["branchesTotal"] <- jint requiredBranches.Count
            node["uncoveredInstructions"] <- jsonNode (uncoveredInstructions |> Set.toList |> List.map (instructionCoverageLabel snapshot) |> List.sort)
            node["uncoveredBranchOutcomes"] <- jsonNode (uncoveredBranches |> Set.toList |> List.map (branchCoverageLabel snapshot) |> List.sort)
            node

        let requireProjectPath () =
            match projectRoot with
            | Some path -> Directory.CreateDirectory path |> ignore; path
            | None -> error "PROJECT_PATH_REQUIRED" "This control-plane operation requires a configured project directory." None None [] []

        let makeTaskJson (task: TaskSession) =
            let node = JsonObject()
            node["task"] <- jstr task.Id
            node["goal"] <- jstr task.Goal
            node["active"] <- jbool task.Active
            node["wordsInspected"] <- jsonNode (task.Inspected |> Set.toList)
            node["wordsUsed"] <- jsonNode (task.Used |> Set.toList)
            node["wordsCreated"] <- jsonNode (task.Created |> Set.toList)
            node["testsRun"] <- jint task.TestsRun
            node["testsFailed"] <- jint task.TestsFailed
            node["effects"] <- jsonNode (task.EffectCounts |> Map.toSeq |> Map.ofSeq)
            node["errors"] <- jsonNode task.Errors
            match task.LogWarning with
            | Some warning ->
                let warningNode = JsonObject()
                warningNode["code"] <- jstr warning.Code
                warningNode["message"] <- jstr warning.Message
                match warning.Path with Some path -> warningNode["path"] <- jstr path | None -> ()
                warningNode["saved"] <- jbool false
                node["logWarning"] <- warningNode
            | None -> ()
            node

        let mutable lastResults: TestCaseResult list = []

        let recordTestResults (results: TestCaseResult list) =
            match activeTask with
            | Some task when task.Active ->
                task.TestsRun <- task.TestsRun + results.Length
                task.TestsFailed <- task.TestsFailed + (results |> List.filter (fun result -> not result.Passed) |> List.length)
            | _ -> ()

        let cleanupTaskTemporaries (task: TaskSession) =
            let temporaryNames = data.Words |> Map.toSeq |> Seq.choose (fun (name, item) -> if item.Status = Temporary then Some name else None) |> Seq.toList
            let mutable proposed = data
            for name in temporaryNames do
                match task.Snapshot.Words.TryFind name with
                | Some previous when previous.Status = Temporary ->
                    let tests =
                        proposed.Tests
                        |> Map.filter (fun _ test -> test.Word <> name)
                        |> fun current -> task.Snapshot.Tests |> Map.filter (fun _ test -> test.Word = name) |> Map.fold (fun found key value -> Map.add key value found) current
                    let examples =
                        proposed.Examples
                        |> Map.filter (fun _ example -> example.Word <> name)
                        |> fun current -> task.Snapshot.Examples |> Map.filter (fun _ example -> example.Word = name) |> Map.fold (fun found key value -> Map.add key value found) current
                    proposed <- { proposed with Words = Map.add name previous proposed.Words; Tests = tests; Examples = examples }
                | _ ->
                    match proposed.Replacements.TryFind name with
                    | Some backup ->
                        let tests =
                            proposed.Tests
                            |> Map.filter (fun _ test -> test.Word <> name)
                            |> fun current -> Map.fold (fun found key value -> Map.add key value found) current backup.Tests
                        let examples =
                            proposed.Examples
                            |> Map.filter (fun _ example -> example.Word <> name)
                            |> fun current -> Map.fold (fun found key value -> Map.add key value found) current backup.Examples
                        proposed <-
                            { proposed with
                                Words = Map.add name backup.Word proposed.Words
                                Tests = tests
                                Examples = examples
                                Replacements = Map.remove name proposed.Replacements }
                    | None ->
                        proposed <-
                            { proposed with
                                Words = Map.remove name proposed.Words
                                WordIds = Map.remove name proposed.WordIds
                                Tests = proposed.Tests |> Map.filter (fun _ test -> test.Word <> name)
                                Examples = proposed.Examples |> Map.filter (fun _ example -> example.Word <> name) }
            let executable = compileRuntimeSnapshot proposed
            activateRuntimeSnapshot executable
            lastResults <- []

        let availableDescription (snapshot: RuntimeSnapshot) name =
            let state = snapshot.State
            let words = snapshot.Words
            match words.TryFind name with
            | None -> error "NAME_UNKNOWN_WORD" $"Word '{name}' is not defined." (Some name) None [] []
            | Some item ->
                log "inspect" name
                let definition = item.Definition
                let direct =
                    match item.Builtin with
                    | Some(RecordConstructor record) -> Set.empty
                    | Some(ScalarConstructor scalar) -> scalarDefinitions state |> Map.tryFind scalar |> Option.bind (fun value -> value.Validator) |> Option.map Set.singleton |> Option.defaultValue Set.empty
                    | Some _ -> Set.empty
                    | None -> Compiler.dependencies definition.Body
                let transitive =
                    let rec walk (pending: Set<string>) (found: Set<string>) =
                        match pending |> Set.toList with
                        | [] -> found
                        | head :: tail when found.Contains head -> walk (Set.ofList tail) found
                        | head :: tail ->
                            let next = words.TryFind head |> Option.map (fun value -> if value.Builtin.IsNone then Compiler.dependencies value.Definition.Body else Set.empty) |> Option.defaultValue Set.empty
                            walk (Set.union (Set.ofList tail) next) (Set.add head found)
                    walk direct Set.empty
                let callers =
                    userWords state
                    |> Map.toList
                    |> List.choose (fun (caller, value) -> if (Compiler.dependencies value.Definition.Body).Contains name then Some caller else None)
                    |> List.sort
                let tests = state.Tests |> Map.toList |> List.map snd |> List.filter (fun test -> test.Word = name) |> List.sortBy (fun test -> test.Name)
                let examples = state.Examples |> Map.toList |> List.map snd |> List.filter (fun example -> example.Word = name) |> List.sortBy (fun example -> example.Name)
                let sourceFile = definition.Span.File
                let sourceLine = definition.Span.Line
                let maturity = if item.Maturity = LibraryWord then "library" else "project"
                let status = match item.Status with Primitive -> "primitive" | Candidate -> "candidate" | Temporary -> "temporary" | Persistent -> "persistent"
                let kind =
                    match item.Builtin with
                    | Some(BuiltinOp _) -> "primitive"
                    | Some _ -> "generated"
                    | None -> "word"
                let obj = JsonObject()
                obj["name"] <- jstr name
                obj["inputs"] <- jsonNode (definition.Inputs |> List.map Types.format)
                obj["outputs"] <- jsonNode (definition.Outputs |> List.map Types.format)
                obj["effects"] <- jsonNode (definition.Effects |> Set.toList)
                obj["documentation"] <- jstr definition.Documentation
                obj["id"] <- jstr (wordIdentity state item)
                obj["dependencies"] <- jsonNode (direct |> Set.toList)
                obj["transitiveDependencies"] <- jsonNode (transitive |> Set.toList)
                obj["callers"] <- jsonNode callers
                obj["tests"] <- jsonNode (tests |> List.map (fun test -> test.Name))
                obj["examples"] <- jsonNode (examples |> List.map (fun example -> example.Name))
                obj["source"] <- jstr sourceFile
                obj["line"] <- jint sourceLine
                obj["kind"] <- jstr kind
                obj["status"] <- jstr status
                obj["maturity"] <- jstr maturity
                obj["deprecated"] <- jbool (state.Deprecated.Contains name)
                obj["revision"] <- jint item.Revision
                obj["testCount"] <- jint tests.Length
                obj["exampleCount"] <- jint examples.Length
                let observed = lastResults |> List.filter (fun result -> result.Word = name)
                let currentCoverage = coverageJson snapshot name observed
                currentCoverage["status"] <- jstr (if List.isEmpty observed then "not-run" else "current")
                obj["coverage"] <- currentCoverage
                obj

        let frozenValidatorWords (state: DictionaryState) (words: Map<string, WordEntry>) =
            let rec reach (pending: Set<string>) (found: Set<string>) =
                match pending |> Set.toList with
                | [] -> found
                | head :: rest when found.Contains head -> reach (Set.ofList rest) found
                | head :: rest ->
                    let next = words.TryFind head |> Option.map (fun entry -> if entry.Builtin.IsNone then Compiler.dependencies entry.Definition.Body else Set.empty) |> Option.defaultValue Set.empty
                    reach (Set.union (Set.ofList rest) next) (Set.add head found)
            state.Scalars
            |> Map.toSeq
            |> Seq.choose (fun (_, scalar) ->
                if scalar.Status <> Persistent then None
                else scalar.Definition.Validator |> Option.map (fun validator -> reach (Set.singleton validator) Set.empty))
            |> Seq.fold Set.union Set.empty

        let registerParsed (parsed: ParsedSource) (temporary: bool) =
            let old = data
            if parsed.Words |> List.exists (fun definition -> definition.Maturity = LibraryWord || definition.Revision <> 1) then
                error "WORD_METADATA_HOST_MANAGED" "Word maturity and revision are assigned by the host and cannot be set in source." None None [] []
            let duplicateNames =
                (parsed.Records |> List.map (fun value -> value.Name)) @ (parsed.Scalars |> List.map (fun value -> value.Name))
                |> List.groupBy id
                |> List.tryFind (fun (_, values) -> List.length values > 1)
            match duplicateNames with
            | Some(name, _) -> error "TYPE_DUPLICATE_NAME" $"Type name '{name}' is declared more than once." (Some name) None [] []
            | None -> ()
            let duplicateWords = parsed.Words |> List.groupBy (fun value -> value.Name) |> List.tryFind (fun (_, values) -> List.length values > 1)
            match duplicateWords with
            | Some(name, _) -> error "WORD_DUPLICATE_NAME" $"Word '{name}' is declared more than once in the same source block." (Some name) None [] []
            | None -> ()
            for record in parsed.Records do
                if old.Records.ContainsKey record.Name || old.Scalars.ContainsKey record.Name then error "TYPE_REDEFINITION" $"Type '{record.Name}' is immutable after declaration." (Some record.Name) (Some record.Span) [] []
            for scalar in parsed.Scalars do
                if old.Records.ContainsKey scalar.Name || old.Scalars.ContainsKey scalar.Name then error "TYPE_REDEFINITION" $"Type '{scalar.Name}' is immutable after declaration." (Some scalar.Name) (Some scalar.Span) [] []
            let status = if temporary then Temporary else Candidate
            let records: Map<string, RecordEntry> = parsed.Records |> List.fold (fun result (record: RecordDefinition) -> Map.add record.Name ({ Definition = record; Status = Candidate }: RecordEntry) result) old.Records
            let scalars: Map<string, ScalarEntry> = parsed.Scalars |> List.fold (fun result (scalar: ScalarTypeDefinition) -> Map.add scalar.Name ({ Definition = scalar; Status = Candidate }: ScalarEntry) result) old.Scalars
            let wordIds =
                parsed.Words
                |> List.fold (fun (identities: Map<string, string>) (definition: WordDefinition) ->
                    if identities.ContainsKey definition.Name then identities
                    else Map.add definition.Name (newWordIdentity ()) identities) old.WordIds
            let initialEntries, initialReplacements : Map<string, WordEntry> * Map<string, ReplacementBackup> =
                List.fold (fun (words: Map<string, WordEntry>, backups: Map<string, ReplacementBackup>) (definition: WordDefinition) ->
                    if Compiler.primitives.ContainsKey definition.Name || (makeGenerated { old with Records = records; Scalars = scalars }).ContainsKey definition.Name then
                        error "NAME_RESERVED" $"Word name '{definition.Name}' is reserved by the standard library or a generated type word." (Some definition.Name) (Some definition.Span) [] []
                    let previous = words.TryFind definition.Name
                    let maturity, revision =
                        match previous with
                        | Some value ->
                            let revision = if value.Status = Persistent then value.Revision + 1 else value.Revision
                            (if value.Maturity = LibraryWord then LibraryWord else ProjectWord), revision
                        | None -> ProjectWord, 1
                    let adjusted = { definition with Maturity = maturity; Revision = revision }
                    let backups =
                        match previous, backups.TryFind definition.Name with
                        | Some value, None when value.Status = Persistent ->
                            let originalTests = old.Tests |> Map.filter (fun _ test -> test.Word = definition.Name)
                            let originalExamples = old.Examples |> Map.filter (fun _ example -> example.Word = definition.Name)
                            Map.add definition.Name { Word = value; Tests = originalTests; Examples = originalExamples } backups
                        | _ -> backups
                    let words = Map.add definition.Name (entry adjusted None status maturity revision) words
                    words, backups) (old.Words, old.Replacements) parsed.Words
            let tests = parsed.Tests |> List.fold addTest old.Tests
            let examples = parsed.Examples |> List.fold addExample old.Examples
            let mutable entries = initialEntries
            let mutable replacements = initialReplacements
            let generated = makeGenerated { old with Records = records; Scalars = scalars }
            let metadataTargets =
                (parsed.Tests |> List.map (fun test -> test.Word)) @ (parsed.Examples |> List.map (fun example -> example.Word))
                |> Set.ofList
            for name in metadataTargets do
                match entries.TryFind name with
                | Some item when item.Builtin.IsNone && item.Status = Persistent ->
                    let backup =
                        replacements.TryFind name
                        |> Option.defaultWith (fun () ->
                            { Word = item
                              Tests = old.Tests |> Map.filter (fun _ test -> test.Word = name)
                              Examples = old.Examples |> Map.filter (fun _ example -> example.Word = name) })
                    let revision = item.Revision + 1
                    let definition = { item.Definition with Revision = revision }
                    entries <- Map.add name { item with Definition = definition; Status = Candidate; Revision = revision } entries
                    replacements <- Map.add name backup replacements
                | Some item when item.Builtin.IsSome && item.Status = Persistent ->
                    error "TEST_TARGET_IMMUTABLE" $"Tests and examples for generated type word '{name}' must be attached before the type is committed." (Some name) None [] []
                | Some item when item.Builtin.IsSome && item.Status = Primitive ->
                    error "TEST_TARGET_IMMUTABLE" $"Tests and examples cannot be added to trusted primitive '{name}' through project source." (Some name) None [] []
                | None ->
                    match generated.TryFind name with
                    | Some item when item.Status = Persistent -> error "TEST_TARGET_IMMUTABLE" $"Tests and examples for generated type word '{name}' must be attached before the type is committed." (Some name) None [] []
                    | _ -> ()
                | _ -> ()
            let proposed = { old with Records = records; Scalars = scalars; Words = entries; WordIds = wordIds; Tests = tests; Examples = examples; Replacements = replacements }
            let executable = compileRuntimeSnapshot proposed
            let frozen = frozenValidatorWords old (effectiveWords old)
            let changed = parsed.Words |> List.map (fun word -> word.Name) |> Set.ofList
            let conflict = Set.intersect frozen changed
            if not (Set.isEmpty conflict) then
                error "TYPE_VALIDATOR_FROZEN" $"Cannot replace validator dependency '{Set.minElement conflict}' while a scalar type is persistent." None None [] (Set.toList conflict)
            activateRuntimeSnapshot executable
            lastResults <- []
            for word in parsed.Words do log "create" word.Name
            for name in metadataTargets do
                if initialEntries.TryFind name |> Option.exists (fun item -> item.Status = Persistent) then log "create" name

        let saveTaskLog (task: TaskSession) : StorageError option =
            task.LogWarning <- None
            let saveResult =
                match store with
                | Some projectStore ->
                    let taskLogId = task.Id.Substring("task-".Length)
                    Storage.saveTaskLog projectStore taskLogId ((makeTaskJson task).ToJsonString(JsonSerializerOptions(WriteIndented = true)))
                | None -> Ok ()
            match saveResult with
            | Ok () ->
                previousTaskLog <- Some(makeTaskJson task)
                None
            | Error storageError ->
                task.LogWarning <- Some storageError
                previousTaskLog <- Some(makeTaskJson task)
                Some storageError

        let taskLogStatusText (logWarning: StorageError option) =
            match logWarning with
            | Some warning -> $"The task log was not saved ({warning.Code}): {warning.Message}"
            | None -> "The task log was saved."

        let nextTaskNumber () =
            let persistedMaximum =
                match projectRoot with
                | Some root ->
                    let directory = Path.Combine(root, "history")
                    if not (Directory.Exists directory) then 0
                    else
                        Directory.GetFiles(directory, "task-*.json")
                        |> Array.choose (fun path ->
                            let stem = Path.GetFileNameWithoutExtension path
                            match Int32.TryParse(stem.Substring("task-".Length), NumberStyles.None, CultureInfo.InvariantCulture) with
                            | true, value -> Some value
                            | _ -> None)
                        |> Array.fold max 0
                | None -> 0
            taskCounter <- max taskCounter persistedMaximum + 1
            taskCounter

        let commitCandidates target library actor includeReplacementCallers =
            let candidateWords = data.Words |> Map.filter (fun _ value -> value.Status = Candidate)
            let candidateRecords = data.Records |> Map.filter (fun _ value -> value.Status = Candidate)
            let candidateScalars = data.Scalars |> Map.filter (fun _ value -> value.Status = Candidate)
            let candidateTypes = Set.union (candidateRecords |> Map.toSeq |> Seq.map fst |> Set.ofSeq) (candidateScalars |> Map.toSeq |> Seq.map fst |> Set.ofSeq)
            let currentWords = effectiveWords data

            let rec wordClosure (pending: string list) (found: Set<string>) =
                match pending with
                | [] -> found
                | name :: rest when found.Contains name -> wordClosure rest found
                | name :: rest ->
                    match candidateWords.TryFind name with
                    | None -> wordClosure rest found
                    | Some value ->
                        let dependencies = Compiler.dependencies value.Definition.Body |> Set.toList
                        wordClosure (rest @ dependencies) (Set.add name found)

            let rec namedTypes (typeValue: LangType) =
                match typeValue with
                | TNamed name -> Set.singleton name
                | TList item | TOption item -> namedTypes item
                | TResult(ok, err) -> Set.union (namedTypes ok) (namedTypes err)
                | _ -> Set.empty

            let ownerOfGeneratedWord name =
                match currentWords.TryFind name with
                | Some { Builtin = Some(RecordConstructor owner) } -> Some owner
                | Some { Builtin = Some(RecordAccessor(owner, _)) } -> Some owner
                | Some { Builtin = Some(ScalarConstructor owner) } -> Some owner
                | Some { Builtin = Some(ScalarAccessor owner) } -> Some owner
                | _ -> None

            let typeReferencesForWord (name: string) =
                match data.Words.TryFind name with
                | None -> Set.empty
                | Some value ->
                    let signatures = value.Definition.Inputs @ value.Definition.Outputs |> List.map namedTypes |> Set.unionMany
                    let generatedDependencies =
                        Compiler.dependencies value.Definition.Body
                        |> Set.toList
                        |> List.choose ownerOfGeneratedWord
                        |> Set.ofList
                    Set.union signatures generatedDependencies

            let rec typeClosure (pending: string list) (found: Set<string>) =
                match pending with
                | [] -> found
                | name :: rest when found.Contains name -> typeClosure rest found
                | name :: rest when not (candidateTypes.Contains name) -> typeClosure rest found
                | name :: rest ->
                    let referenced =
                        match candidateRecords.TryFind name, candidateScalars.TryFind name with
                        | Some record, _ -> record.Definition.Fields |> List.map (fun field -> namedTypes field.Type) |> Set.unionMany
                        | _, Some scalar -> namedTypes scalar.Definition.BaseType
                        | _ -> Set.empty
                    typeClosure (rest @ Set.toList referenced) (Set.add name found)

            let selectedTypeWordNames (types: Set<string>) =
                currentWords
                |> Map.toSeq
                |> Seq.choose (fun (name, _) ->
                    ownerOfGeneratedWord name
                    |> Option.filter types.Contains
                    |> Option.map (fun _ -> name))
                |> Set.ofSeq

            let selectedMetadata (owners: Set<string>) =
                let tests =
                    data.Tests
                    |> Map.toSeq
                    |> Seq.choose (fun (_, test) ->
                        if owners.Contains test.Word then Some(test.Word, "test", test.Name, testExpressions test)
                        else None)
                let examples =
                    data.Examples
                    |> Map.toSeq
                    |> Seq.choose (fun (_, example) ->
                        if owners.Contains example.Word then Some(example.Word, "example", example.Name, example.Body)
                        else None)
                Seq.append tests examples |> Seq.toList

            let validateSelectedMetadataTemporaryReferences metadata =
                for owner, kind, name, body in metadata do
                    let temporaryDependency =
                        Compiler.dependencies body
                        |> Set.toList
                        |> List.tryFind (fun dependency ->
                            data.Words.TryFind dependency
                            |> Option.exists (fun value -> value.Status = Temporary))
                    match temporaryDependency with
                    | Some dependency ->
                        let code = if kind = "test" then "COMMIT_TEMPORARY_TEST_DEPENDENCY" else "COMMIT_TEMPORARY_EXAMPLE_DEPENDENCY"
                        error code $"Selected {kind} '{name}' on '{owner}' references temporary word '{dependency}', which cannot be committed." (Some owner) None [] [ dependency ]
                    | None -> ()

            let mutable selectedWords, selectedTypes =
                match target with
                | None -> candidateWords |> Map.toSeq |> Seq.map fst |> Set.ofSeq, candidateTypes
                | Some name when candidateWords.ContainsKey name -> wordClosure [ name ] Set.empty, Set.empty
                | Some name when candidateTypes.Contains name -> Set.empty, typeClosure [ name ] Set.empty
                | Some name ->
                    match ownerOfGeneratedWord name with
                    | Some owner when candidateTypes.Contains owner -> Set.empty, typeClosure [ owner ] Set.empty
                    | _ -> error "COMMIT_NOT_CANDIDATE" $"'{name}' is not a candidate word or type." (Some name) None [] []

            if includeReplacementCallers then
                let mutable callerSearch = selectedWords
                let mutable addedCallers = true
                while addedCallers do
                    let stagedCallers =
                        data.Replacements
                        |> Map.toSeq
                        |> Seq.choose (fun (name, backup) ->
                            if candidateWords.ContainsKey name && not (selectedWords.Contains name) && backup.Word.Status = Persistent && not (Set.isEmpty (Set.intersect callerSearch (Compiler.dependencies backup.Word.Definition.Body))) then Some name
                            else None)
                        |> Set.ofSeq
                    if Set.isEmpty stagedCallers then addedCallers <- false
                    else
                        let additions = wordClosure (Set.toList stagedCallers) Set.empty
                        selectedWords <- Set.union selectedWords additions
                        callerSearch <- Set.union callerSearch additions

            let mutable changed = true
            while changed do
                let previousWords, previousTypes = selectedWords, selectedTypes
                let validatorWords =
                    selectedTypes
                    |> Set.toList
                    |> List.choose (fun name -> candidateScalars.TryFind name |> Option.bind (fun value -> value.Definition.Validator))
                    |> List.fold (fun found name -> Set.union found (wordClosure [ name ] Set.empty)) Set.empty
                let owners = Set.union selectedWords (selectedTypeWordNames selectedTypes)
                let metadata = selectedMetadata owners
                validateSelectedMetadataTemporaryReferences metadata
                let metadataDependencies = metadata |> List.map (fun (_, _, _, body) -> Compiler.dependencies body) |> Set.unionMany
                let metadataTypes =
                    metadata
                    |> List.map (fun (_, _, _, body) -> expressionTypeReferences body)
                    |> Set.unionMany
                let generatedMetadataTypes =
                    metadataDependencies
                    |> Set.toList
                    |> List.choose ownerOfGeneratedWord
                    |> Set.ofList
                let metadataWords = wordClosure (Set.toList metadataDependencies) Set.empty
                let replacementCallerWords =
                    if includeReplacementCallers then
                        let callerRoots =
                            data.Replacements
                            |> Map.toSeq
                            |> Seq.choose (fun (name, backup) ->
                                if candidateWords.ContainsKey name
                                   && not (selectedWords.Contains name)
                                   && backup.Word.Status = Persistent
                                   && not (Set.isEmpty (Set.intersect selectedWords (Compiler.dependencies backup.Word.Definition.Body))) then Some name
                                else None)
                            |> Set.ofSeq
                        wordClosure (Set.toList callerRoots) Set.empty
                    else Set.empty
                selectedWords <- Set.unionMany [ selectedWords; validatorWords; metadataWords; replacementCallerWords ]
                let referencedTypes = selectedWords |> Set.toList |> List.map typeReferencesForWord |> Set.unionMany
                let allReferencedTypes = Set.unionMany [ referencedTypes; metadataTypes; generatedMetadataTypes ]
                selectedTypes <- typeClosure (Set.union selectedTypes allReferencedTypes |> Set.toList) selectedTypes
                changed <- previousWords <> selectedWords || previousTypes <> selectedTypes

            if Set.isEmpty selectedWords && Set.isEmpty selectedTypes then
                error "COMMIT_NO_CANDIDATES" "There are no candidate words or types to commit." None None [] []

            let proposedWords =
                data.Words
                |> Map.map (fun name value ->
                    if selectedWords.Contains name then
                        let promoteToLibrary = library && (target.IsNone || target = Some name)
                        let maturity = if promoteToLibrary || value.Maturity = LibraryWord then LibraryWord else ProjectWord
                        { value with Status = Persistent; Maturity = maturity; Definition = { value.Definition with Maturity = maturity } }
                    else value)
            let proposedRecords = data.Records |> Map.map (fun name value -> if selectedTypes.Contains name then { value with Status = Persistent } else value)
            let proposedScalars = data.Scalars |> Map.map (fun name value -> if selectedTypes.Contains name then { value with Status = Persistent } else value)
            let proposed =
                { data with
                    Words = proposedWords
                    Records = proposedRecords
                    Scalars = proposedScalars
                    Replacements = data.Replacements |> Map.filter (fun name _ -> not (selectedWords.Contains name)) }
            compileRuntimeSnapshot proposed |> ignore
            for name in selectedWords do
                let candidate = candidateWords[name]
                let danglingTemp = Compiler.dependencies candidate.Definition.Body |> Set.filter (fun dependency -> data.Words.TryFind dependency |> Option.exists (fun value -> value.Status = Temporary))
                if not (Set.isEmpty danglingTemp) then error "COMMIT_TEMPORARY_DEPENDENCY" $"Candidate '{name}' depends on temporary word '{Set.minElement danglingTemp}'. Promote it and commit it before this word." (Some name) None [] (Set.toList danglingTemp)
                let attached = proposed.Tests |> Map.toList |> List.map snd |> List.filter (fun test -> test.Word = name)
                if List.isEmpty attached then error "COMMIT_TEST_REQUIRED" $"Candidate '{name}' needs at least one attached passing test before commit." (Some name) None [ "attached passing test" ] []
            let durableBefore = durableState data
            let replacing = selectedWords |> Set.filter (fun name -> durableBefore.Words.ContainsKey name)
            let durableProposed = durableState proposed
            let durableSnapshot = compileRuntimeSnapshot durableProposed
            let durableMetadataOwners = Set.union selectedWords (selectedTypeWordNames selectedTypes)
            let selectedMetadataEntries = selectedMetadata durableMetadataOwners
            let missingDurableTests =
                selectedMetadataEntries
                |> List.choose (fun (owner, kind, name, _) ->
                    if kind = "test" && not (durableProposed.Tests.ContainsKey($"{owner}/{name}")) then Some($"{owner}/{name}")
                    else None)
            if not (List.isEmpty missingDurableTests) then
                error "COMMIT_SELECTED_TEST_NOT_DURABLE" "A selected test would not survive the durable project projection." None None [] missingDurableTests
            let missingDurableExamples =
                selectedMetadataEntries
                |> List.choose (fun (owner, kind, name, _) ->
                    if kind = "example" && not (durableProposed.Examples.ContainsKey($"{owner}/{name}")) then Some($"{owner}/{name}")
                    else None)
            if not (List.isEmpty missingDurableExamples) then
                error "COMMIT_SELECTED_EXAMPLE_NOT_DURABLE" "A selected example would not survive the durable project projection." None None [] missingDurableExamples
            let selectedTestOwners = Set.union selectedWords (selectedTypeWordNames selectedTypes)
            let selectedResults = selectedTestOwners |> Set.toList |> List.collect (fun name -> runTestsFor durableSnapshot (Some name))
            let mutable changedNames = replacing
            let mutable callers = Set.empty
            let mutable foundCallers = true
            while foundCallers do
                let nextCallers =
                    durableBefore.Words
                    |> Map.toSeq
                    |> Seq.choose (fun (name, item) ->
                        if item.Status = Persistent && not (Set.isEmpty (Set.intersect changedNames (Compiler.dependencies item.Definition.Body))) then Some name
                        else None)
                    |> Set.ofSeq
                    |> Set.filter (fun name -> not (callers.Contains name))
                callers <- Set.union callers nextCallers
                changedNames <- Set.union changedNames nextCallers
                foundCallers <- not nextCallers.IsEmpty
            let callerResults =
                callers
                |> Set.filter (fun caller -> not (selectedWords.Contains caller))
                |> Set.toList
                |> List.collect (fun caller ->
                    let attached = durableProposed.Tests |> Map.toSeq |> Seq.map snd |> Seq.filter (fun test -> test.Word = caller) |> Seq.toList
                    if List.isEmpty attached then
                        error "REPLACE_CALLER_TESTS_REQUIRED" $"Replacing a dependency requires attached passing tests on persistent caller '{caller}'." (Some caller) None [ "attached passing test" ] []
                    runTestsFor durableSnapshot (Some caller))
            let results = selectedResults @ callerResults
            preflightStructuredTestResults durableSnapshot.Program results
            lastResults <- results
            recordTestResults results
            let failed = results |> List.filter (fun result -> not result.Passed)
            if not (List.isEmpty failed) then
                let names = failed |> List.map (fun value -> $"{value.Word}/{value.Name}")
                error "COMMIT_TESTS_FAILED" "Candidate tests must pass against the proposed dictionary before commit." None None [] names
            for name in selectedWords do
                let candidate = candidateWords[name]
                if proposedWords[name].Maturity = LibraryWord then
                    let result = results |> List.filter (fun test -> test.Word = name)
                    let requiredInstructions, requiredBranches = coverageObligations durableSnapshot name
                    let coveredInstructions = result |> List.fold (fun found test -> Set.union found test.Instructions) Set.empty
                    let coveredBranches = result |> List.fold (fun found test -> Set.union found test.BranchOutcomes) Set.empty
                    let uncovered = Set.difference requiredInstructions coveredInstructions
                    let missingBranches = Set.difference requiredBranches coveredBranches
                    if not (Set.isEmpty uncovered && Set.isEmpty missingBranches && not (List.isEmpty result)) then
                        error "LIBRARY_COVERAGE_INCOMPLETE" $"Library word '{name}' requires every instruction, case, and declared iteration outcome to be exercised by its own attached tests." (Some name) None [] (coverageGapLabels durableSnapshot uncovered missingBranches)
            let history =
                selectedWords
                |> Set.fold (fun (found: Map<string, WordDefinition list>) name ->
                    let previous = found.TryFind name |> Option.defaultValue []
                    Map.add name (previous @ [ proposedWords[name].Definition ]) found) data.History
            let finalState = { proposed with History = history }
            let finalSnapshot = compileRuntimeSnapshot finalState
            compileRuntimeSnapshot (durableState finalState) |> ignore
            publish data finalState actor
            activateRuntimeSnapshot finalSnapshot
            lastResults <- results
            results

        let readString (node: JsonObject) (key: string) (defaultValue: string) =
            match node[key] with
            | null -> defaultValue
            | value -> try value.GetValue<string>() with _ -> defaultValue

        let readBool (node: JsonObject) (key: string) (defaultValue: bool) =
            match node[key] with
            | null -> defaultValue
            | value -> try value.GetValue<bool>() with _ -> defaultValue

        let readOptionalStrictBool (node: JsonObject) (key: string) (defaultValue: bool) =
            let actualKind (value: JsonNode) =
                match value with
                | null -> "null"
                | :? JsonObject -> "object"
                | :? JsonArray -> "array"
                | :? JsonValue -> "non-boolean scalar"
                | _ -> "unknown JSON value"
            if not (node.ContainsKey key) then defaultValue
            else
                match node[key] with
                | :? JsonValue as value ->
                    let mutable parsed = false
                    if value.TryGetValue<bool>(&parsed) then parsed
                    else error "EVAL_INVALID_ARGUMENT" $"Argument '{key}' must be a JSON boolean." None None [ "boolean" ] [ actualKind (value :> JsonNode) ]
                | value ->
                    error "EVAL_INVALID_ARGUMENT" $"Argument '{key}' must be a JSON boolean." None None [ "boolean" ] [ actualKind value ]

        let discoveryArgumentKind (value: JsonNode) : string =
            match value with
            | null -> "null"
            | :? JsonObject -> "object"
            | :? JsonArray -> "array"
            | :? JsonValue as scalar ->
                let mutable stringValue: string = ""
                let mutable boolValue: bool = false
                let mutable numberValue: int = 0
                if scalar.TryGetValue<string>(&stringValue) then "string"
                elif scalar.TryGetValue<bool>(&boolValue) then "boolean"
                elif scalar.TryGetValue<int>(&numberValue) then "number"
                else "number"
            | _ -> "value"

        let invalidDiscoveryArgument (name: string) (expected: string) (actual: string) : 'T =
            error "DISCOVERY_INVALID_ARGUMENT" $"Discovery argument '{name}' must be {expected}." None None [ expected ] [ actual ]

        let requiredDiscoveryString (arguments: JsonObject) (name: string) : string =
            if not (arguments.ContainsKey name) then
                invalidDiscoveryArgument name "a string" "missing"
            match arguments[name] with
            | :? JsonValue as value ->
                let mutable parsed = ""
                if value.TryGetValue<string>(&parsed) then parsed
                else invalidDiscoveryArgument name "a string" (discoveryArgumentKind value)
            | value -> invalidDiscoveryArgument name "a string" (discoveryArgumentKind value)

        let optionalDiscoveryInt (arguments: JsonObject) (name: string) (defaultValue: int) (hardMaximum: int) : int =
            if not (arguments.ContainsKey name) then defaultValue
            else
                match arguments[name] with
                | :? JsonValue as value ->
                    let mutable parsed = 0
                    if not (value.TryGetValue<int>(&parsed)) then
                        invalidDiscoveryArgument name "an integer" (discoveryArgumentKind value)
                    if parsed > hardMaximum then
                        error "DISCOVERY_BUDGET_LIMIT" $"Discovery argument '{name}' cannot exceed {hardMaximum}." None None [ $"<= {hardMaximum}" ] [ string parsed ]
                    parsed
                | value -> invalidDiscoveryArgument name "an integer" (discoveryArgumentKind value)

        let buildDiscoveryIndex () : Map<string, WordEntry> * DiscoveryIndex =
            let words = effectiveWords data
            words, Discovery.build words data.Records data.Scalars

        let ensureKnownDiscoveryType (typeValue: LangType) : unit =
            let rec names = function
                | TNamed name -> Set.singleton name
                | TList item | TOption item -> names item
                | TResult(ok, error) -> Set.union (names ok) (names error)
                | TInt | TFloat | TBool | TString | TUnit | TVar _ -> Set.empty
            match Set.difference (names typeValue) (knownTypes data) |> Set.toList |> List.tryHead with
            | Some name -> error "DISCOVERY_UNKNOWN_TYPE" $"Type query refers to undeclared nominal type '{name}'." (Some name) None [ "declared record or scalar type" ] [ name ]
            | None -> ()

        let queryDiscoveryType (arguments: JsonObject) : LangType =
            let source = requiredDiscoveryString arguments "type"
            match Parser.parseClosedType source with
            | Error diagnostic -> raise (LanguageException diagnostic)
            | Ok typeValue ->
                ensureKnownDiscoveryType typeValue
                typeValue

        let logDiscoveryQuery (operation: string) (query: string) (words: string list) =
            log "inspect" $"{operation}:{query}"
            words |> List.iter (log "inspect")

        let discoveryWordsPayload (queryKey: string) (query: string) (words: string list) : JsonObject =
            let payload = JsonObject()
            payload[queryKey] <- jstr query
            payload["words"] <- jsonNode words
            payload["count"] <- jint words.Length
            payload

        let describeJson word =
            match syntaxDescriptors |> List.tryFind (fun descriptor -> descriptor.Name = word) with
            | Some descriptor ->
                log "inspect" word
                syntaxDescriptorJson descriptor
            | None ->
                availableDescription (currentSnapshot ()) word

        let resultList (snapshot: RuntimeSnapshot) (kind: string) (text: string) (results: TestCaseResult list) (target: string option) =
            let array = JsonArray()
            results |> List.iter (fun result -> array.Add(resultJson snapshot result))
            let dataNode = JsonObject()
            dataNode["results"] <- array
            match target with Some word when snapshot.Words.ContainsKey word -> dataNode["coverage"] <- coverageJson snapshot word results | _ -> ()
            success kind text (Some dataNode)

        member _.ProjectDirectory = projectRoot
        member _.Capabilities = capabilities

        member _.Dispatch(operation: string, args: JsonObject) =
            try
                match operation with
                | "eval" ->
                    let structured = readOptionalStrictBool args "structured" false
                    let code = readString args "code" ""
                    match Parser.parse "<eval>" code with
                    | Ok parsed when not (List.isEmpty parsed.Words && List.isEmpty parsed.Records && List.isEmpty parsed.Scalars && List.isEmpty parsed.Tests && List.isEmpty parsed.Examples) ->
                        registerParsed parsed (readBool args "temporary" false)
                        success "defined" "Definitions parsed, type checked, and staged as candidates." (Some(jsonNode (parsed.Words |> List.map (fun word -> word.Name))))
                    | _ ->
                        match Parser.parseExpression "<eval>" code with
                        | Error diagnostic -> response false "error" (Diagnostics.render diagnostic) None (Some diagnostic)
                        | Ok body ->
                            let snapshot = currentSnapshot ()
                            let result, trace = executeExpression snapshot None virtualFiles body
                            let structuredStack =
                                if structured then Some(ValueInspection.toData snapshot.Program result)
                                else None
                            virtualFiles <- trace.FileSystem
                            let values = JsonArray()
                            result |> List.iter (fun value -> values.Add(toJsonValue value))
                            let dataNode = JsonObject()
                            dataNode["stack"] <- values
                            dataNode["stackTypes"] <- jsonNode (result |> List.map (Types.ofValue >> Types.format))
                            dataNode["console"] <- jsonNode trace.Console
                            dataNode["effects"] <- jsonNode (trace.Effects |> Map.toSeq |> Map.ofSeq)
                            match structuredStack with Some value -> dataNode["structuredStack"] <- value | None -> ()
                            success "eval" (result |> List.map Types.formatValue |> String.concat " ") (Some dataNode)
                | "define" ->
                    let source = readString args "source" (readString args "code" "")
                    match Parser.parse "<definition>" source with
                    | Error diagnostic -> response false "error" (Diagnostics.render diagnostic) None (Some diagnostic)
                    | Ok parsed ->
                        registerParsed parsed (readBool args "temporary" false)
                        success "defined" "Definitions parsed, type checked, and staged." (Some(jsonNode (parsed.Words |> List.map (fun word -> word.Name))))
                | "words" ->
                    let words = effectiveWords data
                    let entries = words |> Map.toList |> List.map snd |> List.filter (fun item -> item.Status <> Primitive || item.Builtin.IsSome)
                    let array = JsonArray()
                    entries |> List.sortBy (fun item -> item.Definition.Name) |> List.iter (fun item ->
                        log "inspect" item.Definition.Name
                        let value = JsonObject()
                        value["name"] <- jstr item.Definition.Name
                        value["id"] <- jstr (wordIdentity data item)
                        value["inputs"] <- jsonNode (item.Definition.Inputs |> List.map Types.format)
                        value["outputs"] <- jsonNode (item.Definition.Outputs |> List.map Types.format)
                        value["effects"] <- jsonNode (item.Definition.Effects |> Set.toList)
                        value["status"] <- jstr (match item.Status with Primitive -> "primitive" | Candidate -> "candidate" | Temporary -> "temporary" | Persistent -> "persistent")
                        value["maturity"] <- jstr (if item.Maturity = LibraryWord then "library" else "project")
                        value["deprecated"] <- jbool (data.Deprecated.Contains item.Definition.Name)
                        array.Add value)
                    let payload = JsonObject()
                    payload["words"] <- array
                    let constructs = JsonArray()
                    syntaxDescriptors |> List.iter (syntaxDescriptorJson >> constructs.Add)
                    payload["constructs"] <- constructs
                    success "words" $"{entries.Length} word(s)." (Some payload)
                | "describe" ->
                    let name = readString args "word" ""
                    success "describe" $"Description for {name}." (Some(describeJson name))
                | "type-of" ->
                    let name = requiredDiscoveryString args "word"
                    buildDiscoveryIndex () |> ignore
                    success "type-of" $"Type metadata for {name}." (Some(describeJson name))
                | "search" ->
                    let query = readString args "query" (readString args "text" "")
                    let words = effectiveWords data
                    let matches =
                        let wordNames =
                            words |> Map.toList |> List.map snd |> List.filter (fun item ->
                                item.Definition.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || item.Definition.Documentation.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                (item.Definition.Inputs @ item.Definition.Outputs |> List.exists (fun ty -> (Types.format ty).Contains(query, StringComparison.OrdinalIgnoreCase))))
                            |> List.map (fun item -> item.Definition.Name)
                        let syntaxNames =
                            syntaxDescriptors
                            |> List.filter (fun item ->
                                item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                                || item.Syntax.Contains(query, StringComparison.OrdinalIgnoreCase)
                                || item.Documentation.Contains(query, StringComparison.OrdinalIgnoreCase)
                                || (item.Inputs @ item.Outputs @ item.TypeParameters @ item.Effects @ item.Coverage
                                    |> List.exists (fun value -> value.Contains(query, StringComparison.OrdinalIgnoreCase))))
                            |> List.map (fun item -> item.Name)
                        List.append wordNames syntaxNames |> List.distinct |> List.sort
                    let result = JsonArray()
                    matches |> List.iter (fun name -> result.Add(jstr name))
                    success "search" $"{matches.Length} match(es)." (Some(result :> JsonNode))
                | "search-type" | "search-output" ->
                    let target = queryDiscoveryType args
                    let _, index = buildDiscoveryIndex ()
                    let query = Types.format target
                    let matches = if operation = "search-type" then Discovery.searchType index target else Discovery.searchOutput index target
                    logDiscoveryQuery operation query matches
                    let payload = discoveryWordsPayload "type" query matches
                    success operation $"{matches.Length} match(es) for {query}." (Some payload)
                | "search-effect" ->
                    let effect = requiredDiscoveryString args "effect"
                    let _, index = buildDiscoveryIndex ()
                    let matches = Discovery.searchEffect index effect
                    logDiscoveryQuery operation effect matches
                    let payload = discoveryWordsPayload "effect" effect matches
                    success operation $"{matches.Length} word(s) declare effect '{effect}'." (Some payload)
                | "search-dependency" ->
                    let dependency = requiredDiscoveryString args "word"
                    let words, index = buildDiscoveryIndex ()
                    if not (words.ContainsKey dependency) then
                        error "DISCOVERY_UNKNOWN_WORD" $"Unknown dependency word '{dependency}'." (Some dependency) None [ "known word" ] [ dependency ]
                    let matches = Discovery.searchDependency index dependency
                    logDiscoveryQuery operation dependency matches
                    let payload = discoveryWordsPayload "dependency" dependency matches
                    success operation $"{matches.Length} word(s) depend directly on '{dependency}'." (Some payload)
                | "transitive-dependencies" | "transitive-callers" ->
                    let root = requiredDiscoveryString args "word"
                    let _, index = buildDiscoveryIndex ()
                    let matches =
                        if operation = "transitive-dependencies" then Discovery.transitiveDependencies index root
                        else Discovery.transitiveCallers index root
                    logDiscoveryQuery operation root (root :: matches)
                    let key = if operation = "transitive-dependencies" then "dependencies" else "callers"
                    let payload = JsonObject()
                    payload["word"] <- jstr root
                    payload[key] <- jsonNode matches
                    payload["count"] <- jint matches.Length
                    success operation $"{matches.Length} transitive {key} for '{root}'." (Some payload)
                | "graph" ->
                    let root = requiredDiscoveryString args "word"
                    let maxDepth = optionalDiscoveryInt args "maxDepth" 8 32
                    let maxNodes = optionalDiscoveryInt args "maxNodes" 128 512
                    let _, index = buildDiscoveryIndex ()
                    let graph = Discovery.graphText index root maxDepth maxNodes
                    logDiscoveryQuery operation root graph.ExpandedWords
                    let payload = JsonObject()
                    payload["word"] <- jstr root
                    payload["text"] <- jstr graph.Text
                    payload["expandedWords"] <- jsonNode graph.ExpandedWords
                    payload["omittedWords"] <- jint graph.OmittedWords
                    payload["truncated"] <- jbool graph.Truncated
                    success operation $"Dependency graph for '{root}'." (Some payload)
                | "context" ->
                    let root = requiredDiscoveryString args "word"
                    let maxDepth = optionalDiscoveryInt args "maxDepth" 6 32
                    let maxWords = optionalDiscoveryInt args "maxWords" 24 512
                    let maxUtf8Bytes = optionalDiscoveryInt args "maxUtf8Bytes" 12000 262144
                    let _, index = buildDiscoveryIndex ()
                    let context = Discovery.context index root maxDepth maxWords maxUtf8Bytes
                    logDiscoveryQuery operation root context.WordsIncluded
                    context.TypesIncluded |> List.iter (log "inspect")
                    // Content is already the complete compact, budgeted data document.
                    // Keep its own byte count untouched; the fixed protocol envelope
                    // is not part of that payload budget.
                    success operation $"Budgeted context for '{root}'." (Some(JsonNode.Parse context.Content))
                | "source" ->
                    let name = readString args "word" ""
                    match syntaxDescriptors |> List.tryFind (fun descriptor -> descriptor.Name = name) with
                    | Some descriptor ->
                        log "inspect" name
                        let source = "syntax " + descriptor.Syntax + " : " + (String.concat " " descriptor.Inputs) + " -> " + (String.concat " " descriptor.Outputs)
                        success "source" source (Some(jstr source))
                    | None ->
                        let words = effectiveWords data
                        match words.TryFind name with
                        | None -> error "NAME_UNKNOWN_WORD" $"Word '{name}' is not defined." (Some name) None [] []
                        | Some item ->
                            log "inspect" name
                            let inputText = String.concat " " (item.Definition.Inputs |> List.map Types.format)
                            let outputText = String.concat " " (item.Definition.Outputs |> List.map Types.format)
                            let source =
                                match item.Builtin with
                                | Some(BuiltinOp _) -> $"primitive {name} : {inputText} -> {outputText}"
                                | Some _ -> sourceForAgent item.Definition.SourceText
                                | None -> sourceForAgent item.Definition.SourceText
                            success "source" source (Some(jstr source))
                | "dependencies" ->
                    let name = readString args "word" ""
                    let words = effectiveWords data
                    match words.TryFind name with
                    | None -> error "NAME_UNKNOWN_WORD" $"Word '{name}' is not defined." (Some name) None [] []
                    | Some item ->
                        log "inspect" name
                        let deps = if item.Builtin.IsSome then Set.empty else Compiler.dependencies item.Definition.Body
                        let payload = JsonObject()
                        payload["dependencies"] <- jsonNode (deps |> Set.toList)
                        let callers = userWords data |> Map.toList |> List.choose (fun (caller, value) -> if (Compiler.dependencies value.Definition.Body).Contains name then Some caller else None) |> List.sort
                        payload["callers"] <- jsonNode callers
                        success "dependencies" $"{deps.Count} direct dependency/dependencies." (Some payload)
                | "callers" ->
                    let name = readString args "word" ""
                    let callers = userWords data |> Map.toList |> List.choose (fun (caller, value) -> if (Compiler.dependencies value.Definition.Body).Contains name then Some caller else None) |> List.sort
                    success "callers" $"{callers.Length} caller(s)." (Some(jsonNode callers))
                | "effects" ->
                    let name = readString args "word" ""
                    match (effectiveWords data).TryFind name with
                    | None -> error "NAME_UNKNOWN_WORD" $"Word '{name}' is not defined." (Some name) None [] []
                    | Some value -> success "effects" $"Effects for {name}." (Some(jsonNode (value.Definition.Effects |> Set.toList)))
                | "ir" ->
                    let name = readString args "word" ""
                    let snapshot = currentSnapshot ()
                    match snapshot.Words.TryFind name with
                    | None -> error "NAME_UNKNOWN_WORD" $"Word '{name}' is not defined." (Some name) None [] []
                    | Some value ->
                        let target =
                            match value.Builtin with
                            | Some(BuiltinOp operation) -> IrFormatTarget.PrimitiveContract(PrimitiveId operation)
                            | Some _ -> IrFormatTarget.GeneratedWordId(WordId(wordIdentity snapshot.State value))
                            | None -> IrFormatTarget.UserWordId(WordId(wordIdentity snapshot.State value))
                        let formatted = IrFormatting.toData snapshot.Program target
                        let label =
                            match value.Builtin with
                            | Some(BuiltinOp _) -> "Canonical primitive contract"
                            | _ -> "Verified executable IR"
                        success "ir" $"{label} for '{name}'." (Some formatted)
                | "tests" ->
                    let name = readString args "word" ""
                    let tests = data.Tests |> Map.toList |> List.map snd |> List.filter (fun test -> test.Word = name) |> List.sortBy (fun test -> test.Name)
                    success "tests" $"{tests.Length} attached test(s)." (Some(jsonNode (tests |> List.map (fun test -> test.Name))))
                | "examples" ->
                    let name = readString args "word" ""
                    let examples = data.Examples |> Map.toList |> List.map snd |> List.filter (fun example -> example.Word = name) |> List.sortBy (fun example -> example.Name)
                    success "examples" $"{examples.Length} example(s)." (Some(jsonNode (examples |> List.map (fun example -> example.Name))))
                | "test" ->
                    let target = readString args "word" ""
                    let snapshot = currentSnapshot ()
                    let results = runTestsFor snapshot (if target = "" then None else Some target)
                    lastResults <- results
                    recordTestResults results
                    resultList snapshot "test" $"{results |> List.filter (fun item -> item.Passed) |> List.length}/{results.Length} test(s) passed." results (if target = "" then None else Some target)
                | "test-all" ->
                    let snapshot = currentSnapshot ()
                    let results = runTestsFor snapshot None
                    lastResults <- results
                    recordTestResults results
                    resultList snapshot "test" $"{results |> List.filter (fun item -> item.Passed) |> List.length}/{results.Length} test(s) passed." results None
                | "failed-tests" ->
                    let snapshot = currentSnapshot ()
                    let allResults = runTestsFor snapshot None
                    recordTestResults allResults
                    let results = allResults |> List.filter (fun result -> not result.Passed)
                    resultList snapshot "failed-tests" $"{results.Length} failing test(s) on current dictionary." results None
                | "commit" | "commit-word" | "replace-word" | "task.commit" ->
                    let name = readString args "word" ""
                    let library = readBool args "library" false
                    let actor = readString args "actor" "client"
                    let hasCandidates =
                        (data.Words |> Map.exists (fun _ value -> value.Status = Candidate))
                        || (data.Records |> Map.exists (fun _ value -> value.Status = Candidate))
                        || (data.Scalars |> Map.exists (fun _ value -> value.Status = Candidate))
                    if operation = "task.commit" && not (activeTask |> Option.exists (fun task -> task.Active)) then
                        error "TASK_NOT_ACTIVE" "No active task can be committed." None None [] []
                    elif actor <> "client" && actor <> "host" then
                        error "PROVENANCE_INVALID_ACTOR" "Commit actor must be 'client' or 'host'." None None [ "client"; "host" ] [ actor ]
                    elif operation = "replace-word" && (name = "" || not (data.Replacements.ContainsKey name)) then
                        error "REPLACE_NOT_STAGED" "replace-word requires the name of a staged replacement for an existing persistent word." (if name = "" then None else Some name) None [] []
                    else
                        let results =
                            if operation = "task.commit" && not hasCandidates then []
                            else commitCandidates (if name = "" then None else Some name) library actor (operation = "replace-word")
                        if operation = "task.commit" then
                            match activeTask with
                            | Some task ->
                                cleanupTaskTemporaries task
                                task.Active <- false
                                let logWarning = saveTaskLog task
                                let warning = lastExportWarning |> Option.map (fun item -> $" Export warning: {item.Message}") |> Option.defaultValue ""
                                success "task.commit" ($"Task committed and temporary words were cleared. {taskLogStatusText logWarning}{warning}") (Some(makeTaskJson task))
                            | None -> error "TASK_NOT_ACTIVE" "No active task can be committed." None None [] []
                        elif operation = "replace-word" then
                            let warning = lastExportWarning |> Option.map (fun item -> $" Export warning: {item.Message}") |> Option.defaultValue ""
                            success "replace-word" ($"Replacement of '{name}' passed caller checks and was committed.{warning}") (Some(jsonNode (results |> List.map (fun value -> $"{value.Word}/{value.Name}"))))
                        else
                            let warning = lastExportWarning |> Option.map (fun item -> $" Export warning: {item.Message}") |> Option.defaultValue ""
                            success "commit" ($"{results.Length} attached test(s) passed; selected candidates committed.{warning}") (Some(jsonNode (results |> List.map (fun value -> $"{value.Word}/{value.Name}"))))
                | "rename" ->
                    let oldName = readString args "word" ""
                    let newName = readString args "to" ""
                    let actor = readString args "actor" "client"
                    if actor <> "client" && actor <> "host" then
                        error "PROVENANCE_INVALID_ACTOR" "Maintenance actor must be 'client' or 'host'." None None [ "client"; "host" ] [ actor ]
                    elif oldName = "" || newName = "" || oldName = newName then
                        error "RENAME_INVALID_NAME" "Rename requires distinct nonempty source and target names." (if oldName = "" then None else Some oldName) None [] [ oldName; newName ]
                    elif (data.Words |> Map.exists (fun _ item -> item.Status = Candidate || item.Status = Temporary))
                         || (data.Records |> Map.exists (fun _ item -> item.Status = Candidate))
                         || (data.Scalars |> Map.exists (fun _ item -> item.Status = Candidate)) then
                        error "RENAME_STAGED_CHANGES" "Commit or discard staged edits before renaming persistent vocabulary." None None [] []
                    else
                        let effective = effectiveWords data
                        match data.Words.TryFind oldName with
                        | None -> error "NAME_UNKNOWN_WORD" $"Word '{oldName}' is not a user-defined word." (Some oldName) None [] []
                        | Some original when original.Builtin.IsSome -> error "WORD_MAINTENANCE_IMMUTABLE" "Primitive and generated words cannot be renamed." (Some oldName) None [] []
                        | Some original when original.Status <> Persistent -> error "WORD_MAINTENANCE_REQUIRES_COMMIT" "Only committed words can be renamed." (Some oldName) None [ "persistent" ] [ string original.Status ]
                        | Some _ ->
                            if effective.ContainsKey newName || (knownTypes data).Contains newName then
                                error "RENAME_COLLISION" $"The name '{newName}' is already used by a word or type." (Some newName) None [] []
                            let frozen = frozenValidatorWords data effective
                            if frozen.Contains oldName then
                                error "TYPE_VALIDATOR_FROZEN" $"Cannot rename '{oldName}' because it belongs to a persistent scalar validator closure." (Some oldName) None [] [ oldName ]
                            let updates =
                                data.Words
                                |> Map.toList
                                |> List.choose (fun (oldKey, item) ->
                                    if item.Builtin.IsSome then None
                                    else
                                        let renamed = Source.renameWordDefinition oldName newName item.Definition
                                        if oldKey <> oldName && renamed.Body = item.Definition.Body then None
                                        else
                                            let revision = item.Revision + 1
                                            let definition = { renamed with Revision = revision; Maturity = item.Maturity }
                                            let definition = { definition with SourceText = Source.renderWord true definition }
                                            Some(oldKey, definition.Name, { item with Definition = definition; Revision = revision }))
                            let identity = data.WordIds.TryFind oldName |> Option.defaultWith (fun () -> error "WORD_ID_MISSING" $"Word '{oldName}' has no stable identity." (Some oldName) None [] [])
                            let wordIds = data.WordIds |> Map.remove oldName |> Map.add newName identity
                            let words =
                                updates
                                |> List.fold (fun found (_, name, item) -> Map.add name item found) (Map.remove oldName data.Words)
                            let expectationOnlyTestOwners =
                                data.Tests
                                |> Map.toSeq
                                |> Seq.choose (fun (_, test) ->
                                    match test.Expected with
                                    | ExpectedExpression expressions when (Compiler.dependencies expressions).Contains oldName ->
                                        Some(if test.Word = oldName then newName else test.Word)
                                    | _ -> None)
                                |> Set.ofSeq
                            let tests = data.Tests |> Map.toSeq |> Seq.map (fun (_, item) -> Source.renameTestOwner oldName newName item) |> Seq.fold addTest Map.empty
                            let examples = data.Examples |> Map.toSeq |> Seq.map (fun (_, item) -> Source.renameExampleOwner oldName newName item) |> Seq.fold addExample Map.empty
                            let scalars =
                                data.Scalars
                                |> Map.map (fun _ item ->
                                    let definition = Source.renameScalarValidator oldName newName item.Definition
                                    if definition.Validator = item.Definition.Validator then item
                                    else { item with Definition = definition })
                            let deprecated = if data.Deprecated.Contains oldName then data.Deprecated |> Set.remove oldName |> Set.add newName else data.Deprecated
                            let rewritten = { data with Words = words; WordIds = wordIds; Deprecated = deprecated; Scalars = scalars; Tests = tests; Examples = examples; Replacements = Map.empty }
                            let parsed = parseProjectSource "<rename>" (sourceFor rewritten)
                            let parsedProposed = parsedState parsed rewritten wordIds
                            let movedHistory: Map<string, WordDefinition list> =
                                match data.History.TryFind oldName with
                                | Some oldHistory -> data.History |> Map.remove oldName |> Map.add newName oldHistory
                                | None -> data.History
                            let history: Map<string, WordDefinition list> =
                                updates
                                |> List.fold (fun found (_, name, item) ->
                                    let previous = found.TryFind name |> Option.defaultValue []
                                    Map.add name (previous @ [ item.Definition ]) found) movedHistory
                            let proposed = { parsedProposed with History = history; Deprecated = deprecated }
                            let executable = compileRuntimeSnapshot proposed
                            let affectedNames = updates |> List.map (fun (_, name, _) -> name) |> Set.ofList
                            let testOwnersToRun = Set.union affectedNames expectationOnlyTestOwners
                            let wordsAfter = executable.Words
                            let results =
                                testOwnersToRun
                                |> Set.toList
                                |> List.collect (fun name ->
                                    let attached = proposed.Tests |> Map.toSeq |> Seq.map snd |> Seq.filter (fun test -> test.Word = name) |> Seq.toList
                                    if List.isEmpty attached then
                                        error "RENAME_TESTS_REQUIRED" $"Renaming or rewriting '{name}' requires at least one attached test." (Some name) None [ "attached passing test" ] []
                                    runTestsFor executable (Some name))
                            preflightStructuredTestResults executable.Program results
                            recordTestResults results
                            let failed = results |> List.filter (fun item -> not item.Passed)
                            if not (List.isEmpty failed) then
                                error "RENAME_TESTS_FAILED" "Affected tests must pass before the rename can be published." None None [] (failed |> List.map (fun item -> $"{item.Word}/{item.Name}"))
                            for name in affectedNames do
                                let candidate = wordsAfter[name]
                                if candidate.Maturity = LibraryWord then
                                    let ownTests = results |> List.filter (fun test -> test.Word = name)
                                    let requiredInstructions, requiredBranches = coverageObligations executable name
                                    let coveredInstructions = ownTests |> List.fold (fun found test -> Set.union found test.Instructions) Set.empty
                                    let coveredBranches = ownTests |> List.fold (fun found test -> Set.union found test.BranchOutcomes) Set.empty
                                    let uncovered = Set.difference requiredInstructions coveredInstructions
                                    let missingBranches = Set.difference requiredBranches coveredBranches
                                    if not (Set.isEmpty uncovered && Set.isEmpty missingBranches && not (List.isEmpty ownTests)) then
                                        error "LIBRARY_COVERAGE_INCOMPLETE" $"Renamed library word '{name}' requires complete attached test coverage." (Some name) None [] (coverageGapLabels executable uncovered missingBranches)
                            compileRuntimeSnapshot (durableState proposed) |> ignore
                            publish data proposed actor
                            activateRuntimeSnapshot executable
                            lastResults <- results
                            let payload = JsonObject()
                            payload["id"] <- jstr identity
                            payload["from"] <- jstr oldName
                            payload["to"] <- jstr newName
                            payload["rewrittenWords"] <- jsonNode (affectedNames |> Set.toList)
                            success "rename" $"Renamed '{oldName}' to '{newName}' and validated {results.Length} affected test(s)." (Some payload)
                | "deprecate" ->
                    let name = readString args "word" ""
                    let actor = readString args "actor" "client"
                    if actor <> "client" && actor <> "host" then
                        error "PROVENANCE_INVALID_ACTOR" "Maintenance actor must be 'client' or 'host'." None None [ "client"; "host" ] [ actor ]
                    elif (data.Words |> Map.exists (fun _ item -> item.Status = Candidate || item.Status = Temporary))
                         || (data.Records |> Map.exists (fun _ item -> item.Status = Candidate))
                         || (data.Scalars |> Map.exists (fun _ item -> item.Status = Candidate)) then
                        error "DEPRECATE_STAGED_CHANGES" "Commit or discard staged edits before deprecating a word." None None [] []
                    else
                        match data.Words.TryFind name with
                        | None -> error "NAME_UNKNOWN_WORD" $"Word '{name}' is not a user-defined word." (Some name) None [] []
                        | Some item when item.Builtin.IsSome -> error "WORD_MAINTENANCE_IMMUTABLE" "Primitive and generated words cannot be deprecated." (Some name) None [] []
                        | Some item when item.Status <> Persistent -> error "WORD_MAINTENANCE_REQUIRES_COMMIT" "Only committed words can be deprecated." (Some name) None [ "persistent" ] [ string item.Status ]
                        | Some item when data.Deprecated.Contains name ->
                            let payload = JsonObject()
                            payload["id"] <- jstr (wordIdentity data item)
                            payload["deprecated"] <- jbool true
                            success "deprecate" $"Word '{name}' is already deprecated." (Some payload)
                        | Some item ->
                            let revision = item.Revision + 1
                            let definition = { item.Definition with Revision = revision }
                            let definition = { definition with SourceText = Source.renderWord true definition }
                            let updated = { item with Definition = definition; Revision = revision }
                            let proposedWords = Map.add name updated data.Words
                            let testSnapshot = compileRuntimeSnapshot { data with Words = proposedWords }
                            let tests = runTestsFor testSnapshot (Some name)
                            if List.isEmpty tests then error "DEPRECATE_TESTS_REQUIRED" $"Word '{name}' needs an attached test before it can be deprecated." (Some name) None [ "attached passing test" ] []
                            recordTestResults tests
                            let failed = tests |> List.filter (fun result -> not result.Passed)
                            if not (List.isEmpty failed) then error "DEPRECATE_TESTS_FAILED" "The word's attached tests must pass before deprecation." (Some name) None [] (failed |> List.map (fun result -> result.Name))
                            let history = data.History.TryFind name |> Option.defaultValue []
                            let proposed =
                                { data with
                                    Words = proposedWords
                                    Deprecated = Set.add name data.Deprecated
                                    History = Map.add name (history @ [ definition ]) data.History }
                            let executable = compileRuntimeSnapshot proposed
                            compileRuntimeSnapshot (durableState proposed) |> ignore
                            publish data proposed actor
                            activateRuntimeSnapshot executable
                            lastResults <- tests
                            let payload = JsonObject()
                            payload["id"] <- jstr (wordIdentity proposed updated)
                            payload["deprecated"] <- jbool true
                            payload["revision"] <- jint revision
                            success "deprecate" $"Deprecated '{name}' after {tests.Length} passing test(s)." (Some payload)
                | "promote" ->
                    let name = readString args "word" ""
                    match data.Words.TryFind name with
                    | Some item when item.Status = Temporary ->
                        let proposed = { data with Words = Map.add name { item with Status = Candidate } data.Words }
                        let executable = compileRuntimeSnapshot proposed
                        activateRuntimeSnapshot executable
                        lastResults <- []
                        success "promote" $"Promoted '{name}' to a candidate." None
                    | _ -> error "PROMOTE_NOT_TEMPORARY" $"'{name}' is not a temporary word." (Some name) None [] []
                | "discard" ->
                    let name = readString args "word" ""
                    match data.Words.TryFind name with
                    | Some item when item.Status = Temporary || item.Status = Candidate ->
                        let proposed =
                            match data.Replacements.TryFind name with
                            | Some backup ->
                                let tests =
                                    data.Tests
                                    |> Map.filter (fun _ test -> test.Word <> name)
                                    |> fun current -> Map.fold (fun found key value -> Map.add key value found) current backup.Tests
                                let examples =
                                    data.Examples
                                    |> Map.filter (fun _ example -> example.Word <> name)
                                    |> fun current -> Map.fold (fun found key value -> Map.add key value found) current backup.Examples
                                { data with
                                    Words = Map.add name backup.Word data.Words
                                    Tests = tests
                                    Examples = examples
                                    Replacements = Map.remove name data.Replacements }
                            | None ->
                                { data with
                                    Words = Map.remove name data.Words
                                    WordIds = Map.remove name data.WordIds
                                    Tests = data.Tests |> Map.filter (fun _ test -> test.Word <> name)
                                    Examples = data.Examples |> Map.filter (fun _ example -> example.Word <> name) }
                        let executable = compileRuntimeSnapshot proposed
                        activateRuntimeSnapshot executable
                        lastResults <- []
                        success "discard" $"Discarded staged word '{name}'." None
                    | None when data.Records.TryFind name |> Option.exists (fun value -> value.Status = Candidate) ->
                        let prefix = lowerFirst name
                        let owns generated = generated = prefix + ".new" || generated = prefix + ".value" || generated.StartsWith(prefix + ".", StringComparison.Ordinal)
                        let proposed =
                            { data with
                                Records = Map.remove name data.Records
                                Tests = data.Tests |> Map.filter (fun _ test -> not (owns test.Word))
                                Examples = data.Examples |> Map.filter (fun _ example -> not (owns example.Word)) }
                        let executable = compileRuntimeSnapshot proposed
                        activateRuntimeSnapshot executable
                        lastResults <- []
                        success "discard" $"Discarded candidate type '{name}'." None
                    | None when data.Scalars.TryFind name |> Option.exists (fun value -> value.Status = Candidate) ->
                        let prefix = name
                        let owns generated = generated = prefix + ".new" || generated = prefix + ".value"
                        let proposed =
                            { data with
                                Scalars = Map.remove name data.Scalars
                                Tests = data.Tests |> Map.filter (fun _ test -> not (owns test.Word))
                                Examples = data.Examples |> Map.filter (fun _ example -> not (owns example.Word)) }
                        let executable = compileRuntimeSnapshot proposed
                        activateRuntimeSnapshot executable
                        lastResults <- []
                        success "discard" $"Discarded candidate scalar type '{name}'." None
                    | _ -> error "DISCARD_NOT_STAGED" $"'{name}' is not staged." (Some name) None [] []
                | "task.begin" ->
                    match activeTask with
                    | Some task when task.Active -> error "TASK_ALREADY_ACTIVE" "A task is already active." (Some task.Id) None [] []
                    | _ ->
                        let storageSnapshot =
                            match store with
                            | None -> None
                            | Some projectStore ->
                                match Storage.capture projectStore with
                                | Error storageError -> raiseStorageError storageError
                                | Ok snapshot when snapshot.Generation <> storageGeneration ->
                                    error "STORAGE_STALE_GENERATION" "Project storage changed since this engine loaded; reload the project before starting a task." None None [ string storageGeneration ] [ string snapshot.Generation ]
                                | Ok snapshot -> Some snapshot
                        let taskNumber = nextTaskNumber ()
                        let task =
                            { Id = $"task-{taskNumber:D4}"
                              Goal = readString args "goal" ""
                              Snapshot = data
                              ExecutableSnapshot = currentSnapshot ()
                              StorageSnapshot = storageSnapshot
                              ManifestSnapshot = currentManifest
                              ManifestHashSnapshot = currentManifestHash
                              VirtualFilesSnapshot = virtualFiles
                              ClockSnapshot = fixedClock
                              Active = true
                              Inspected = Set.empty
                              Used = Set.empty
                              Created = Set.empty
                              TestsRun = 0
                              TestsFailed = 0
                              EffectCounts = Map.empty
                              Errors = []
                              LogWarning = None }
                        activeTask <- Some task
                        success "task.begin" $"Started {task.Id}." (Some(makeTaskJson task))
                | "task.status" ->
                    match activeTask with Some task -> success "task.status" $"Status for {task.Id}." (Some(makeTaskJson task)) | None -> success "task.status" "No active task." (previousTaskLog |> Option.map (fun value -> value :> JsonNode))
                | "storage.status" ->
                    let authority, manifestHash =
                        match storageAuthority with
                        | EmptyAuthority -> "empty", None
                        | LegacyAuthority _ -> "legacy", None
                        | ManifestAuthority hash -> "manifest", Some hash
                    let payload = JsonObject()
                    payload["projectConfigured"] <- jbool store.IsSome
                    payload["generation"] <- JsonValue.Create(storageGeneration) :> JsonNode
                    payload["authority"] <- jstr authority
                    match manifestHash with
                    | Some hash -> payload["manifestHash"] <- jstr hash
                    | None -> ()
                    match lastExportWarning with
                    | Some warning ->
                        let warningNode = JsonObject()
                        warningNode["code"] <- jstr warning.Code
                        warningNode["message"] <- jstr warning.Message
                        match warning.Path with Some path -> warningNode["path"] <- jstr path | None -> ()
                        payload["exportWarning"] <- warningNode
                    | None -> payload["exportWarning"] <- null
                    success "storage.status" "Current durable storage authority and export status." (Some payload)
                | "task.log" ->
                    match activeTask with Some task -> success "task.log" $"Log for {task.Id}." (Some(makeTaskJson task)) | None -> success "task.log" "Most recent task log." (previousTaskLog |> Option.map (fun value -> value :> JsonNode))
                | "task.abort" ->
                    match activeTask with
                    | Some task when task.Active ->
                        let restored =
                            match store, task.StorageSnapshot with
                            | None, None -> None
                            | Some projectStore, Some storageSnapshot ->
                                match Storage.restore projectStore storageGeneration storageSnapshot with
                                | Error storageError -> raiseStorageError storageError
                                | Ok result -> Some result
                            | _ -> error "STORAGE_TASK_SNAPSHOT_MISSING" "The task's storage snapshot is unavailable." None None [] []
                        activateRuntimeSnapshot task.ExecutableSnapshot
                        virtualFiles <- task.VirtualFilesSnapshot
                        fixedClock <- task.ClockSnapshot
                        match restored with
                        | Some result ->
                            storageGeneration <- result.Generation
                            storageAuthority <- result.Authority
                            currentManifest <- task.ManifestSnapshot
                            currentManifestHash <- task.ManifestHashSnapshot
                            lastExportWarning <- result.ExportWarning
                        | None -> ()
                        task.Active <- false
                        lastResults <- []
                        let logWarning = saveTaskLog task
                        let warning = lastExportWarning |> Option.map (fun item -> $" Export warning: {item.Message}") |> Option.defaultValue ""
                        success "task.abort" ($"Aborted {task.Id}; dictionary changes were rolled back. {taskLogStatusText logWarning}{warning}") (Some(makeTaskJson task))
                    | _ -> error "TASK_NOT_ACTIVE" "No active task can be aborted." None None [] []
                | "snapshot.save" ->
                    let name = readString args "name" ""
                    match store with
                    | None -> error "PROJECT_PATH_REQUIRED" "Named snapshots require a configured project directory." None None [] []
                    | Some projectStore ->
                        let excluded = JsonArray()
                        data.Words |> Map.toSeq |> Seq.choose (fun (word, item) -> if item.Status = Candidate || item.Status = Temporary then Some word else None) |> Seq.sort |> Seq.iter (fun word -> excluded.Add(jstr word))
                        data.Records |> Map.toSeq |> Seq.choose (fun (typeName, item) -> if item.Status = Candidate then Some typeName else None) |> Seq.sort |> Seq.iter (fun typeName -> excluded.Add(jstr typeName))
                        data.Scalars |> Map.toSeq |> Seq.choose (fun (typeName, item) -> if item.Status = Candidate then Some typeName else None) |> Seq.sort |> Seq.iter (fun typeName -> excluded.Add(jstr typeName))
                        match Storage.saveSnapshot projectStore storageGeneration name virtualFiles (Some fixedClock) with
                        | Error storageError -> raiseStorageError storageError
                        | Ok () ->
                            let payload = JsonObject()
                            payload["name"] <- jstr name
                            payload["excludedCandidates"] <- excluded
                            success "snapshot.save" $"Saved committed snapshot '{name}' with {excluded.Count} staged item(s) excluded." (Some payload)
                | "snapshot.load" ->
                    let name = readString args "name" ""
                    match activeTask with
                    | Some task when task.Active -> error "SNAPSHOT_TASK_ACTIVE" "Cannot load a named snapshot during an active task." (Some task.Id) None [] []
                    | _ ->
                        match store with
                        | None -> error "PROJECT_PATH_REQUIRED" "Named snapshots require a configured project directory." None None [] []
                        | Some projectStore ->
                            match Storage.readSnapshot projectStore name with
                            | Error storageError -> raiseStorageError storageError
                            | Ok snapshot ->
                                let projectSource =
                                    match Storage.readSource projectStore snapshot.Manifest.ProjectSource with
                                    | Error storageError -> raiseStorageError storageError
                                    | Ok source -> source
                                let proposed = validateStoredProject projectStore (Some snapshot.Manifest) (Some snapshot.ManifestHash) (Some projectSource)
                                let executable = compileRuntimeSnapshot proposed
                                match Storage.restoreSnapshot projectStore storageGeneration snapshot with
                                | Error storageError -> raiseStorageError storageError
                                | Ok restored ->
                                    activateRuntimeSnapshot executable
                                    virtualFiles <- snapshot.VirtualFiles
                                    fixedClock <- defaultArg snapshot.ClockValue "2000-01-01T00:00:00Z"
                                    storageGeneration <- restored.Generation
                                    storageAuthority <- restored.Authority
                                    currentManifest <- Some snapshot.Manifest
                                    currentManifestHash <- Some snapshot.ManifestHash
                                    lastExportWarning <- restored.ExportWarning
                                    lastResults <- []
                                    let payload = JsonObject()
                                    payload["name"] <- jstr name
                                    payload["manifestHash"] <- jstr snapshot.ManifestHash
                                    payload["virtualFiles"] <- jint virtualFiles.Count
                                    payload["clockValue"] <- jstr fixedClock
                                    let warning = lastExportWarning |> Option.map (fun item -> $" Export warning: {item.Message}") |> Option.defaultValue ""
                                    success "snapshot.load" ($"Loaded committed snapshot '{name}'.{warning}") (Some payload)
                | "history" ->
                    let name = readString args "word" ""
                    match store, currentManifest, currentManifestHash with
                    | Some projectStore, Some manifest, Some manifestHash ->
                        match manifest.Words |> List.tryFind (fun head -> head.CurrentName = name) with
                        | None -> error "HISTORY_WORD_UNKNOWN" $"No durable history exists for '{name}'." (Some name) None [] []
                        | Some head ->
                            let revisions = manifest.Revisions |> List.filter (fun item -> item.WordId = head.WordId) |> List.sortBy (fun item -> item.Revision)
                            let payload = JsonArray()
                            for revision in revisions do
                                match Storage.readRevision projectStore manifestHash head.WordId revision.Revision with
                                | Error storageError -> raiseStorageError storageError
                                | Ok content ->
                                    let item = JsonObject()
                                    item["id"] <- jstr head.WordId
                                    item["name"] <- jstr revision.Name
                                    item["revision"] <- jint revision.Revision
                                    item["source"] <- jstr content.DefinitionSource
                                    item["maturity"] <- jstr (if revision.Maturity = LibraryWord then "library" else "project")
                                    item["actor"] <- jstr revision.Actor
                                    item["task"] <- revision.TaskId |> Option.map jstr |> Option.defaultValue null
                                    item["timestampUtc"] <- jstr (revision.TimestampUtc.ToString("O", CultureInfo.InvariantCulture))
                                    item["deprecated"] <- jbool revision.Deprecated
                                    item["tests"] <- jsonNode content.TestSources
                                    item["examples"] <- jsonNode content.ExampleSources
                                    payload.Add item
                            success "history" $"{revisions.Length} durable revision(s) for {name}." (Some(payload :> JsonNode))
                    | _ ->
                        let revisions = data.History.TryFind name |> Option.defaultValue []
                        let payload = revisions |> List.map (fun definition -> {| revision = definition.Revision; source = Source.renderWord true definition |})
                        success "history" $"{revisions.Length} revision(s) for {name}." (Some(jsonNode payload))
                | "diff" ->
                    let name = readString args "word" ""
                    let first = try args["from"].GetValue<int>() with _ -> -1
                    let second = try args["to"].GetValue<int>() with _ -> -1
                    let sourceForRevision revision =
                        match store, currentManifest, currentManifestHash with
                        | Some projectStore, Some manifest, Some manifestHash ->
                            match manifest.Words |> List.tryFind (fun head -> head.CurrentName = name) with
                            | None -> None
                            | Some head ->
                                match Storage.readRevision projectStore manifestHash head.WordId revision with
                                | Ok content -> Some content.DefinitionSource
                                | Error storageError when storageError.Code = "STORAGE_REVISION_NOT_FOUND" -> None
                                | Error storageError -> raiseStorageError storageError
                        | _ ->
                            data.History.TryFind name
                            |> Option.defaultValue []
                            |> List.tryFind (fun definition -> definition.Revision = revision)
                            |> Option.map (Source.renderWord true)
                    match sourceForRevision first, sourceForRevision second with
                    | Some firstSource, Some secondSource ->
                        let a = firstSource.Split('\n')
                        let b = secondSource.Split('\n')
                        let removed = String.concat "\n- " a
                        let added = String.concat "\n+ " b
                        let text = $"revision {first} -> {second}\n- {removed}\n+ {added}"
                        success "diff" text (Some(jstr text))
                    | _ -> error "HISTORY_REVISION_UNKNOWN" "Requested word revision does not exist." (Some name) None [] [ string first; string second ]
                | "stack" -> success "stack" "Evaluation is stateless; each eval begins with an empty stack." (Some(jsonNode ([]: string list)))
                | _ -> error "PROTOCOL_UNKNOWN_OPERATION" $"Unknown operation '{operation}'." None None [] [ operation ]
            with
            | LanguageException diagnostic ->
                log "error" diagnostic.Code
                response false "error" (Diagnostics.render diagnostic) None (Some diagnostic)
            | ex ->
                let diagnostic = { Code = "HOST_FAILURE"; Message = ex.Message; Word = None; Span = None; Expected = []; Actual = [] }
                log "error" diagnostic.Code
                response false "error" (Diagnostics.render diagnostic) None (Some diagnostic)
