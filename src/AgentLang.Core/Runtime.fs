namespace AgentLang

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Text.Json
open System.Text.Json.Nodes

module Runtime =
    let private wordIdText (WordId value) = value
    let private flowAttachmentKey (owner: WordId) caseName = wordIdText owner + "/" + caseName
    let private flowTestFileKey (owner: WordId) (reference: SourceRef) = wordIdText owner + "/" + reference.Hash

    type private FlowAuthoredWord =
        { Definition: FlowWordDefinition
          Source: FlowLowering.FlowSourceDocument
          /// None marks a staged source change whose bindings will be minted
          /// from the next exact verified project compilation.
          StoredBindings: StoredCallBinding list option }

    type private FlowAuthoredAttachment =
        { Source: FlowLowering.FlowAttachmentSourceDocument
          /// Bindings retained from durable metadata, or None for a staged edit.
          StoredBindings: StoredCallBinding list option }

    type private FlowAuthoredTestFile =
        { OwnerName: string
          OwnerId: WordId
          OwnerRevision: int
          SyntaxVersion: int
          Reference: SourceRef
          SourceFile: string
          Settings: FlowTestFileSettings
          /// Wrapper-scoped target and fixture-body bindings, stored once per file.
          StoredBindings: StoredCallBinding list option }

    type private AuthoredTypeSource =
        { SourceFormat: SourceFormat
          Content: string
          Reference: SourceRef
          ValidatorTarget: StoredCallTarget option }

    type private ReplacementBackup =
        { Word: WordEntry
          Tests: Map<string, TestDefinition>
          Examples: Map<string, ExampleDefinition>
          FlowWord: FlowAuthoredWord option
          FlowTests: Map<string, FlowAuthoredAttachment>
          FlowTestFiles: Map<string, FlowAuthoredTestFile>
          FlowExamples: Map<string, FlowAuthoredAttachment> }

    type private DictionaryState =
        { Words: Map<string, WordEntry>
          WordIds: Map<string, string>
          Deprecated: Set<string>
          Records: Map<string, RecordEntry>
          Scalars: Map<string, ScalarEntry>
          Enums: Map<string, EnumEntry>
          TypeSources: Map<string, AuthoredTypeSource>
          Tests: Map<string, TestDefinition>
          Examples: Map<string, ExampleDefinition>
          History: Map<string, WordDefinition list>
          FlowWords: Map<string, FlowAuthoredWord>
          FlowTests: Map<string, FlowAuthoredAttachment>
          FlowTestFiles: Map<string, FlowAuthoredTestFile>
          FlowExamples: Map<string, FlowAuthoredAttachment>
          FlowHistory: Map<string, FlowAuthoredWord list>
          Replacements: Map<string, ReplacementBackup> }

    type private TestFileOverlay =
        { Program: VerifiedIrProgram
          Dispatch: Map<IrCallTarget, IrCallTarget>
          ActiveOverrides: string list }

    type private CompiledTestFile =
        { Source: FlowAuthoredTestFile
          Compilation: FlowLowering.CompiledTestFileSettings
          Tests: Map<string, FlowLowering.CompiledCallBoundTest>
          TestBindings: Map<string, StoredCallBinding list>
          BindingSpans: Map<StoredCallBinding, SourceSpan> }

    /// One executable view of one immutable dictionary projection. Detached
    /// bodies in the snapshot are verified against this exact program handle.
    type private RuntimeSnapshot =
        { State: DictionaryState
          Words: Map<string, WordEntry>
          Context: Compiler.IrLoweringContext
          Program: VerifiedIrProgram
          FlowContext: FlowLowering.Context option
          FlowProject: FlowLowering.FlowBoundProjectCompilation option
          TestBodies: Map<string, VerifiedIrBody>
          TestExpectationBodies: Map<string, VerifiedIrBody>
          TestFileOverlays: Map<string, TestFileOverlay>
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

    type private EffectAssertionObservation =
        { Span: SourceSpan
          Expected: Map<string, int>
          Actual: Map<string, int>
          TargetInvocationCount: int
          Error: Diagnostic option }

    type private TestCaseResult =
        { Name: string
          Word: string
          Passed: bool
          Error: Diagnostic option
          Actual: Value list
          Expected: TestExpectation
          ExpectedValue: Value option
          EffectAssertion: EffectAssertionObservation option
          ActiveOverrides: string list
          TargetInputs: Value list list
          TargetReturns: Value list list
          TargetInvocationCount: int
          Instructions: Set<SourceSiteId>
          BranchOutcomes: Set<SourceSiteId * string> }

    type private Trace =
        { mutable Steps: int
          mutable CoverageInstructions: Set<SourceSiteId>
          mutable CoverageBranches: Set<SourceSiteId * string>
          mutable FileSystem: Map<string, string>
          mutable Effects: Map<string, int>
          mutable TargetIdentity: (WordId * int) option
          mutable TargetDepth: int
          mutable TargetInvocationCount: int
          mutable TargetInputs: Value list list
          mutable TargetReturns: Value list list
          mutable TargetEffects: Map<string, int>
          mutable Console: string list
          CoverageTarget: string option
          FileSystemMode: FileSystemMode
          IsTest: bool
          SuppressTaskEffects: bool }

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

    let private enumReferencesForWord (state: DictionaryState) (words: Map<string, WordEntry>) (root: string) =
        let rec referencesInType (seen: Set<string>) (typeValue: LangType) =
            match typeValue with
            | TNamed name when state.Enums.ContainsKey name -> Set.singleton name
            | TNamed name when not (seen.Contains name) ->
                let nextSeen = Set.add name seen
                match state.Records.TryFind name, state.Scalars.TryFind name with
                | Some record, _ -> record.Definition.Fields |> List.map (fun field -> referencesInType nextSeen field.Type) |> Set.unionMany
                | None, Some scalar -> referencesInType nextSeen scalar.Definition.BaseType
                | _ -> Set.empty
            | TList item | TOption item -> referencesInType seen item
            | TResult(okType, errorType) -> Set.union (referencesInType seen okType) (referencesInType seen errorType)
            | _ -> Set.empty

        let rec containsEnumMatch expressions =
            expressions
            |> List.exists (function
                | MatchEnum _ -> true
                | If(thenBranch, elseBranch, _)
                | MatchOption(_, thenBranch, elseBranch, _) -> containsEnumMatch thenBranch || containsEnumMatch elseBranch
                | MatchResult(_, _, okBranch, errorBranch, _) -> containsEnumMatch okBranch || containsEnumMatch errorBranch
                | Scope(innerBody, _) -> containsEnumMatch innerBody
                | _ -> false)

        let rec visit (pending: string list) (seenWords: Set<string>) (foundEnums: Set<string>) =
            match pending with
            | [] -> foundEnums
            | name :: rest when seenWords.Contains name -> visit rest seenWords foundEnums
            | name :: rest ->
                match words.TryFind name with
                | None -> visit rest (Set.add name seenWords) foundEnums
                | Some entry ->
                    let signatureTypes = entry.Definition.Inputs @ entry.Definition.Outputs
                    let signatureEnums = signatureTypes |> List.map (referencesInType Set.empty) |> Set.unionMany
                    let bodyTypeEnums =
                        if entry.Builtin.IsNone then
                            expressionTypeReferences entry.Definition.Body
                            |> Set.toList
                            |> List.map (TNamed >> referencesInType Set.empty)
                            |> Set.unionMany
                        else Set.empty
                    let builtinEnums =
                        match entry.Builtin with
                        | Some(EnumCaseConstructor(enumName, _)) -> Set.singleton enumName
                        | _ -> Set.empty
                    let matchMarker = if entry.Builtin.IsNone && containsEnumMatch entry.Definition.Body then Set.singleton "<enum match>" else Set.empty
                    let dependencies =
                        if entry.Builtin.IsNone then Compiler.dependencies entry.Definition.Body |> Set.toList
                        else []
                    visit (rest @ dependencies) (Set.add name seenWords) (Set.unionMany [ foundEnums; signatureEnums; bodyTypeEnums; builtinEnums; matchMarker ])

        visit [ root ] Set.empty Set.empty

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
          Flow2Syntax: string option
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

    type Engine(projectDirectory: string, capabilities: Set<string>, ?clockValue: string, ?fileSystemMode: FileSystemMode, ?testCapabilities: Set<string>) =
        let projectRoot = if String.IsNullOrWhiteSpace projectDirectory then None else Some(Path.GetFullPath projectDirectory)
        let store = projectRoot |> Option.map Storage.create
        let mutable storageGeneration = 0L
        let mutable storageAuthority = EmptyAuthority
        let mutable currentManifest: ProjectManifest option = None
        let mutable currentManifestHash: string option = None
        let mutable lastExportWarning: StorageError option = None
        let mutable fixedClock = defaultArg clockValue "2000-01-01T00:00:00Z"
        let engineFileSystemMode = defaultArg fileSystemMode FileSystemMode.Real
        let engineTestCapabilities = defaultArg testCapabilities capabilities
        let syntaxDescriptors =
            [ { Name = "list.empty"; Syntax = "list.empty<T>"; Flow2Syntax = None; Inputs = []; Outputs = [ "List<T>" ]; TypeParameters = [ "T" ]; Effects = []; EffectRule = "none"; Documentation = "Constructs an empty List<T>. T must be a declared closed type."; Coverage = [] }
              { Name = "list.singleton"; Syntax = "T list.singleton<T>"; Flow2Syntax = None; Inputs = [ "T" ]; Outputs = [ "List<T>" ]; TypeParameters = [ "T" ]; Effects = []; EffectRule = "none"; Documentation = "Constructs a one-element List<T> after checking the payload against the explicit T."; Coverage = [] }
              { Name = "option.none"; Syntax = "option.none<T>"; Flow2Syntax = None; Inputs = []; Outputs = [ "Option<T>" ]; TypeParameters = [ "T" ]; Effects = []; EffectRule = "none"; Documentation = "Constructs a typed none; the element type is always explicit."; Coverage = [] }
              { Name = "option.some"; Syntax = "T option.some<T>"; Flow2Syntax = None; Inputs = [ "T" ]; Outputs = [ "Option<T>" ]; TypeParameters = [ "T" ]; Effects = []; EffectRule = "none"; Documentation = "Constructs a typed some after checking the payload against the explicit T."; Coverage = [] }
              { Name = "result.ok"; Syntax = "T result.ok<T, E>"; Flow2Syntax = None; Inputs = [ "T" ]; Outputs = [ "Result<T, E>" ]; TypeParameters = [ "T"; "E" ]; Effects = []; EffectRule = "none"; Documentation = "Constructs a typed ok; both result type parameters are always explicit."; Coverage = [] }
              { Name = "result.error"; Syntax = "E result.error<T, E>"; Flow2Syntax = None; Inputs = [ "E" ]; Outputs = [ "Result<T, E>" ]; TypeParameters = [ "T"; "E" ]; Effects = []; EffectRule = "none"; Documentation = "Constructs a typed error; the inactive success type remains explicit."; Coverage = [] }
              { Name = "list.map"; Syntax = "list.map <word>"; Flow2Syntax = Some "items.map(callback)"; Inputs = [ "List<T>" ]; Outputs = [ "List<U>" ]; TypeParameters = [ "T"; "U" ]; Effects = []; EffectRule = "inherits callback word effects"; Documentation = "Flow/2 uses a statically named word reference as callback; it cannot capture caller locals. The callback has signature T -> U and is type checked before any element is visited."; Coverage = [ "empty"; "nonempty" ] }
              { Name = "list.filter"; Syntax = "list.filter <word>"; Flow2Syntax = Some "items.filter(callback)"; Inputs = [ "List<T>" ]; Outputs = [ "List<T>" ]; TypeParameters = [ "T" ]; Effects = []; EffectRule = "inherits callback word effects"; Documentation = "Flow/2 uses a statically named word reference as callback; it cannot capture caller locals. The callback has signature T -> Bool. Retains elements for true and drops them for false."; Coverage = [ "empty"; "nonempty"; "keep"; "drop" ] }
              { Name = "list.each"; Syntax = "list.each <word>"; Flow2Syntax = Some "items.each(callback)"; Inputs = [ "List<T>" ]; Outputs = [ "Unit" ]; TypeParameters = [ "T" ]; Effects = []; EffectRule = "inherits callback word effects"; Documentation = "Flow/2 uses a statically named word reference as callback; it cannot capture caller locals. The callback has signature T -> Unit and is called for each item."; Coverage = [ "empty"; "nonempty" ] }
              { Name = "list.fold"; Syntax = "list.fold <word>"; Flow2Syntax = Some "items.fold(seed, callback)"; Inputs = [ "List<T>"; "Accumulator" ]; Outputs = [ "Accumulator" ]; TypeParameters = [ "T"; "Accumulator" ]; Effects = []; EffectRule = "inherits callback word effects"; Documentation = "Flow/2 uses a statically named word reference as callback; it cannot capture caller locals. The callback has signature Accumulator Item -> Accumulator. Visits items from left to right; an empty list returns the initial accumulator unchanged."; Coverage = [ "empty"; "nonempty" ] }
              { Name = "match-option"; Syntax = "match-option / some <local> / none / end"; Flow2Syntax = None; Inputs = [ "Option<T> (top of stack)" ]; Outputs = [ "same stack from both cases" ]; TypeParameters = [ "T" ]; Effects = []; EffectRule = "union of both case effects"; Documentation = "Requires both cases. The some payload local exists only inside its case; both cases must leave identical stack and outer-local types."; Coverage = [ "some"; "none" ] }
              { Name = "match-result"; Syntax = "match-result / ok <local> / error <local> / end"; Flow2Syntax = None; Inputs = [ "Result<T, E> (top of stack)" ]; Outputs = [ "same stack from both cases" ]; TypeParameters = [ "T"; "E" ]; Effects = []; EffectRule = "union of both case effects"; Documentation = "Requires both cases. Each payload local exists only inside its own case; both cases must leave identical stack and outer-local types."; Coverage = [ "ok"; "error" ] } ]

        let syntaxDescriptorJson (descriptor: SyntaxDescriptor) =
            let node = JsonObject()
            node["name"] <- jstr descriptor.Name
            node["kind"] <- jstr "syntax"
            node["syntax"] <- jstr descriptor.Syntax
            descriptor.Flow2Syntax |> Option.iter (fun syntax -> node["flow2Syntax"] <- jstr syntax)
            node["inputs"] <- jsonNode descriptor.Inputs
            node["outputs"] <- jsonNode descriptor.Outputs
            node["typeParameters"] <- jsonNode descriptor.TypeParameters
            node["effects"] <- jsonNode descriptor.Effects
            node["effectRule"] <- jstr descriptor.EffectRule
            node["documentation"] <- jstr descriptor.Documentation
            node["coverageOutcomes"] <- jsonNode descriptor.Coverage
            node

        let helpRequestValueJson (value: AuthoringHelp.RequestValue) : JsonNode =
            match value with
            | AuthoringHelp.RequestValue.Text text -> jstr text
            | AuthoringHelp.RequestValue.Boolean flag -> jbool flag
            | AuthoringHelp.RequestValue.Integer number -> jint number

        let helpPayload (request: AuthoringHelp.HelpRequest) =
            let topic = AuthoringHelp.requestTopic request
            let content = AuthoringHelp.contentForVersion request.SyntaxVersion request.Topic
            let payload = JsonObject()
            payload["schemaVersion"] <- jint AuthoringHelp.schemaVersion
            payload["topics"] <- jsonNode AuthoringHelp.topicNames
            payload["topic"] <- jstr topic
            payload["syntaxVersion"] <- jint request.SyntaxVersion
            payload["title"] <- jstr content.Title
            payload["documentation"] <- jstr content.Documentation

            let instructions = JsonArray()
            for instruction in AuthoringHelp.topicInstructions do
                let row = JsonObject()
                row["topic"] <- jstr instruction.Topic
                row["title"] <- jstr instruction.Title
                row["description"] <- jstr instruction.Description
                instructions.Add row
            payload["topicInstructions"] <- instructions

            let fields = JsonArray()
            for field in content.AllowedFlowDefineFields do
                let row = JsonObject()
                row["name"] <- jstr field.Name
                row["type"] <- jstr field.Type
                row["required"] <- jbool field.Required
                row["documentation"] <- jstr field.Documentation
                fields.Add row
            payload["allowedFlowDefineFields"] <- fields

            let sourceExamples = JsonArray()
            for example in content.SourceExamples do
                let row = JsonObject()
                row["name"] <- jstr example.Name
                row["description"] <- jstr example.Description
                row["source"] <- jstr example.Source
                sourceExamples.Add row
            payload["sourceExamples"] <- sourceExamples

            let limitations = JsonArray()
            for limitation in content.Limitations do
                let row = JsonObject()
                row["code"] <- jstr limitation.Code
                row["operation"] <- jstr limitation.Operation
                row["appliesWhen"] <- jstr limitation.AppliesWhen
                row["declarations"] <- jsonNode limitation.Declarations
                row["explanation"] <- jstr limitation.Explanation
                row["alternative"] <- jstr limitation.Alternative
                limitations.Add row
            payload["limitations"] <- limitations

            let requestExamples = JsonArray()
            for example in content.RequestExamples do
                let row = JsonObject()
                row["name"] <- jstr example.Name
                row["description"] <- jstr example.Description
                let requestNode = JsonObject()
                requestNode["op"] <- jstr example.Operation
                for name, value in example.Fields do requestNode[name] <- helpRequestValueJson value
                row["request"] <- requestNode
                requestExamples.Add row
            payload["requestExamples"] <- requestExamples
            payload

        let mutable data =
            { Words = Compiler.primitives
              WordIds = Map.empty
              Deprecated = Set.empty
              Records = Map.empty
              Scalars = Map.empty
              Enums = Map.empty
              TypeSources = Map.empty
              Tests = Map.empty
              Examples = Map.empty
              History = Map.empty
              FlowWords = Map.empty
              FlowTests = Map.empty
              FlowTestFiles = Map.empty
              FlowExamples = Map.empty
              FlowHistory = Map.empty
              Replacements = Map.empty }
        let mutable activeSnapshot: RuntimeSnapshot option = None

        let currentSnapshot () =
            activeSnapshot
            |> Option.defaultWith (fun () -> error "RUNTIME_SNAPSHOT_UNAVAILABLE" "No verified executable snapshot is active." None None [] [])

        let mutable virtualFiles = Map.empty
        let mutable activeTask: TaskSession option = None
        let mutable previousTaskLog: JsonObject option = None
        let mutable taskCounter = 0
        let mutable pendingLoadedSnapshot: RuntimeSnapshot option = None

        let userWords (state: DictionaryState) = state.Words |> Map.filter (fun _ value -> value.Builtin.IsNone)
        let recordDefinitions (state: DictionaryState) = state.Records |> Map.map (fun _ value -> value.Definition)
        let scalarDefinitions (state: DictionaryState) = state.Scalars |> Map.map (fun _ value -> value.Definition)
        let enumDefinitions (state: DictionaryState) = state.Enums |> Map.map (fun _ value -> value.Definition)
        let knownTypes (state: DictionaryState) =
            Set.unionMany [ state.Records |> Map.toSeq |> Seq.map fst |> Set.ofSeq
                            state.Scalars |> Map.toSeq |> Seq.map fst |> Set.ofSeq
                            state.Enums |> Map.toSeq |> Seq.map fst |> Set.ofSeq ]

        let makeGenerated (state: DictionaryState) : Map<string, WordEntry> =
            let recordWords =
                state.Records
                |> Map.toList
                |> List.collect (fun (name, recordEntry) ->
                    let record = recordEntry.Definition
                    let prefix = lowerFirst name
                    let constructorName = prefix + ".new"
                    let constructor =
                        wordDef constructorName (record.Fields |> List.map (fun field -> field.Type)) [ TNamed name ] Set.empty "Constructs a value after all record fields pass their declared types and optional validator." record.SourceText
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
            let enumWords =
                state.Enums
                |> Map.toList
                |> List.collect (fun (name, enumEntry) ->
                    enumEntry.Definition.Cases
                    |> List.map (fun caseName ->
                        let constructorName = name + "." + caseName
                        let constructor = wordDef constructorName [] [ TNamed name ] Set.empty $"Constructs the {caseName} case of {name}." enumEntry.Definition.SourceText
                        constructorName, entry constructor (Some(EnumCaseConstructor(name, caseName))) enumEntry.Status LibraryWord 1))
            let generatedWords = recordWords @ scalarWords @ enumWords
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

        let rejectUnqualifiedLibraryDependencies (words: Map<string, WordEntry>) (wordName: string) =
            match words.TryFind wordName with
            | Some item when item.Maturity = LibraryWord && item.Builtin.IsNone ->
                let pending = Compiler.dependencies item.Definition.Body |> Set.toList
                let rec visit (seen: Set<string>) (pending: string list) =
                    match pending with
                    | [] -> None
                    | name :: rest when seen.Contains name -> visit seen rest
                    | name :: rest ->
                        match words.TryFind name with
                        | Some dependency when dependency.Builtin.IsNone && dependency.Status = Candidate ->
                            // Candidate dependencies may be qualified together at commit.
                            // Publication checks their final status and maturity after
                            // the selected group has passed every own gate.
                            visit (Set.add name seen) rest
                        | Some dependency when dependency.Builtin.IsNone ->
                            let seen = Set.add name seen
                            if dependency.Status <> Persistent || dependency.Maturity <> LibraryWord then
                                Some name
                            else visit seen (rest @ (Compiler.dependencies dependency.Definition.Body |> Set.toList))
                        | _ -> visit (Set.add name seen) rest
                match visit Set.empty pending with
                | Some name ->
                    error "LIBRARY_DEPENDENCY_NOT_QUALIFIED"
                        $"Library word '{wordName}' depends on authored helper '{name}', which is not a persistent library word. Commit '{name}' as a library word with its own passing tests and complete coverage, then retry."
                        (Some wordName) (Some item.Definition.Span)
                        [ "every authored dependency committed as a library word" ] [ name ]
                | None -> ()
            | _ -> ()

        let wordIdentity (state: DictionaryState) (item: WordEntry) =
            match item.Builtin with
            | Some(BuiltinOp _) -> "primitive_" + item.Definition.Name
            | Some _ -> "generated_" + item.Definition.Name
            | None ->
                match state.WordIds.TryFind item.Definition.Name with
                | Some identity -> identity
                | None -> error "WORD_ID_MISSING" $"Word '{item.Definition.Name}' has no stable identity." (Some item.Definition.Name) None [] []

        let storedTarget (state: DictionaryState) (item: WordEntry) =
            let identity = wordIdentity state item
            match item.Builtin with
            | Some(BuiltinOp _) -> StoredCallTarget.Primitive identity
            | Some _ -> StoredCallTarget.GeneratedWord identity
            | None -> StoredCallTarget.UserWord identity

        let defaultTypeSource (state: DictionaryState) name =
            let definitionSource =
                match state.Records.TryFind name, state.Scalars.TryFind name, state.Enums.TryFind name with
                | Some record, _, _ -> Source.renderRecord record.Definition
                | _, Some scalar, _ -> Source.renderScalar scalar.Definition
                | _, _, Some enumEntry -> Source.renderEnum enumEntry.Definition
                | _ -> error "TYPE_SOURCE_OWNER_MISSING" $"Type source metadata has no declared type '{name}'." (Some name) None [] []
            let sourceObject = Storage.sourceObject StorageObjectKind.TypeDefinition definitionSource
            { SourceFormat = { Frontend = SourceFrontend.Stack; Version = 1 }
              Content = sourceObject.Content
              Reference = sourceObject.Reference
              ValidatorTarget = None }

        let resolvedValidatorTarget (state: DictionaryState) typeName (definition: ScalarTypeDefinition) =
            match definition.Validator with
            | None -> None
            | Some name ->
                let words = effectiveWords state
                match words.TryFind name with
                | Some item -> Some(storedTarget state item)
                | None -> error "TYPE_VALIDATOR_UNKNOWN_WORD" $"Scalar type '{typeName}' validator '{name}' is not an exact dictionary word." (Some typeName) (Some definition.Span) [ "exact word key" ] [ name ]

        let resolvedRecordValidatorTarget (state: DictionaryState) typeName (definition: RecordDefinition) =
            match definition.Validator with
            | None -> None
            | Some name ->
                let words = effectiveWords state
                match words.TryFind name with
                | Some item -> Some(storedTarget state item)
                | None -> error "TYPE_VALIDATOR_UNKNOWN_WORD" $"Record type '{typeName}' validator '{name}' is not an exact dictionary word." (Some typeName) (Some definition.Span) [ "exact word key" ] [ name ]

        let validateTypeSourceMetadata (state: DictionaryState) =
            for KeyValue(name, source) in state.TypeSources do
                match source.SourceFormat.Frontend, source.SourceFormat.Version with
                | SourceFrontend.Stack, 1
                | SourceFrontend.Flow, 1
                | SourceFrontend.Flow, 2 -> ()
                | frontend, version ->
                    let frontendName = if frontend = SourceFrontend.Flow then "Flow" else "Stack"
                    error "RUNTIME_UNSUPPORTED_TYPE_FRONTEND" $"Type '{name}' uses unsupported {frontendName} syntax version {version}." (Some name) None [ "Stack/1"; "Flow/1"; "Flow/2" ] [ $"{frontendName}/{version}" ]
                match state.Records.TryFind name, state.Scalars.TryFind name, state.Enums.TryFind name with
                | Some _, Some _, _ | Some _, _, Some _ | _, Some _, Some _ -> error "TYPE_SOURCE_OWNER_AMBIGUOUS" $"Type source '{name}' maps to more than one nominal type." (Some name) None [] []
                | Some record, None, None ->
                    match record.Definition.Validator, source.ValidatorTarget with
                    | None, Some target -> error "TYPE_VALIDATOR_TARGET_INVALID" $"Record type '{name}' has a validator target but no validator declaration." (Some name) None [] [ string target ]
                    | Some _, Some target ->
                        let actual = resolvedRecordValidatorTarget state name record.Definition
                        if actual <> Some target then
                            error "TYPE_VALIDATOR_TARGET_MISMATCH" $"Record type '{name}' validator does not resolve to its stored stable target." (Some name) (Some record.Definition.Span)
                                [ string target ] (actual |> Option.map string |> Option.toList)
                    | Some _, None ->
                        error "TYPE_VALIDATOR_TARGET_MISSING" $"Stored record type '{name}' requires a stable validator target binding." (Some name) (Some record.Definition.Span) [ "resolved user, generated, or primitive target" ] []
                    | _ -> ()
                | None, Some scalar, None ->
                    match scalar.Definition.Validator, source.ValidatorTarget with
                    | None, Some target -> error "TYPE_VALIDATOR_TARGET_INVALID" $"Scalar type '{name}' has a validator target but no validator declaration." (Some name) None [] [ string target ]
                    | Some _, Some target ->
                        let actual = resolvedValidatorTarget state name scalar.Definition
                        if actual <> Some target then
                            error "TYPE_VALIDATOR_TARGET_MISMATCH" $"Scalar type '{name}' validator does not resolve to its stored stable target." (Some name) (Some scalar.Definition.Span)
                                (Some target |> Option.map string |> Option.toList) (actual |> Option.map string |> Option.toList)
                    | Some _, None when source.SourceFormat.Frontend = SourceFrontend.Flow ->
                        error "TYPE_VALIDATOR_TARGET_MISSING" $"Flow scalar type '{name}' requires a stable validator target binding." (Some name) (Some scalar.Definition.Span) [ "resolved user, generated, or primitive target" ] []
                    | _ -> ()
                | None, None, Some _ when source.ValidatorTarget.IsSome ->
                    error "TYPE_VALIDATOR_TARGET_INVALID" $"Enum type '{name}' cannot carry a scalar validator target." (Some name) None [] [ string source.ValidatorTarget.Value ]
                | None, None, Some _ -> ()
                | None, None, None -> error "TYPE_SOURCE_OWNER_MISSING" $"Type source metadata has no declared type '{name}'." (Some name) None [] []

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
            if trace.TargetDepth > 0 then
                trace.TargetEffects <- Map.change name (fun count -> Some(defaultArg count 0 + 1)) trace.TargetEffects
            // Test and example effects are trace-local and are not task effects.
            if not trace.IsTest && not trace.SuppressTaskEffects then log "effect" name

        let countInstruction (trace: Trace) (currentWord: string) (site: SourceSiteId) (span: SourceSpan) (isAuthoredSite: bool) =
            trace.Steps <- trace.Steps + 1
            if trace.Steps > 10000 then error "RUNTIME_STEP_LIMIT" "Execution exceeded the 10,000 instruction limit." (Some currentWord) (Some span) [] []
            if trace.CoverageTarget = Some currentWord && isAuthoredSite then
                trace.CoverageInstructions <- Set.add site trace.CoverageInstructions

        let countBranchOutcome (trace: Trace) (currentWord: string) (site: SourceSiteId) (outcome: string) (isAuthoredSite: bool) =
            if trace.CoverageTarget = Some currentWord && isAuthoredSite then
                trace.CoverageBranches <- Set.add (site, outcome) trace.CoverageBranches

        let createTrace coverageTarget traceFileSystemMode isTest suppressTaskEffects fileSystem =
            { Steps = 0
              CoverageInstructions = Set.empty
              CoverageBranches = Set.empty
              FileSystem = fileSystem
              Effects = Map.empty
              TargetIdentity = None
              TargetDepth = 0
              TargetInvocationCount = 0
              TargetInputs = []
              TargetReturns = []
              TargetEffects = Map.empty
              Console = []
              CoverageTarget = coverageTarget
              FileSystemMode = traceFileSystemMode
              IsTest = isTest
              SuppressTaskEffects = suppressTaskEffects }

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
                            let ownerId = WordId(wordIdentity current backup.Word)
                            let tests =
                                current.Tests
                                |> Map.filter (fun _ test -> test.Word <> name)
                                |> fun found -> Map.fold (fun acc key value -> Map.add key value acc) found backup.Tests
                            let examples =
                                current.Examples
                                |> Map.filter (fun _ example -> example.Word <> name)
                                |> fun found -> Map.fold (fun acc key value -> Map.add key value acc) found backup.Examples
                            let flowWords =
                                current.FlowWords
                                |> Map.filter (fun _ authored -> authored.Source.OwnerId <> ownerId)
                                |> fun found ->
                                    match backup.FlowWord with
                                    | Some authored -> Map.add (wordIdText authored.Source.OwnerId) authored found
                                    | None -> found
                            let flowTests =
                                current.FlowTests
                                |> Map.filter (fun _ attachment -> attachment.Source.OwnerId <> ownerId)
                                |> fun found -> Map.fold (fun acc key value -> Map.add key value acc) found backup.FlowTests
                            let flowTestFiles =
                                current.FlowTestFiles
                                |> Map.filter (fun _ file -> file.OwnerId <> ownerId)
                                |> fun found -> Map.fold (fun acc key value -> Map.add key value acc) found backup.FlowTestFiles
                            let flowExamples =
                                current.FlowExamples
                                |> Map.filter (fun _ attachment -> attachment.Source.OwnerId <> ownerId)
                                |> fun found -> Map.fold (fun acc key value -> Map.add key value acc) found backup.FlowExamples
                            { current with
                                Words = Map.add name backup.Word current.Words
                                Tests = tests
                                Examples = examples
                                FlowWords = flowWords
                                FlowTests = flowTests
                                FlowTestFiles = flowTestFiles
                                FlowExamples = flowExamples
                                Replacements = Map.remove name current.Replacements }
                        | _ -> current)
                    state
                    state.Replacements
            let persistentWords = restored.Words |> Map.filter (fun _ value -> value.Builtin.IsSome || value.Status = Persistent)
            let persistentOwnerIds =
                persistentWords
                |> Map.toSeq
                |> Seq.choose (fun (name, item) ->
                    if item.Builtin.IsSome then None
                    else restored.WordIds.TryFind name |> Option.map WordId)
                |> Set.ofSeq
            let persistentTypeNames =
                Set.unionMany
                    [ restored.Records |> Map.toSeq |> Seq.choose (fun (name, item) -> if item.Status = Persistent then Some name else None) |> Set.ofSeq
                      restored.Scalars |> Map.toSeq |> Seq.choose (fun (name, item) -> if item.Status = Persistent then Some name else None) |> Set.ofSeq
                      restored.Enums |> Map.toSeq |> Seq.choose (fun (name, item) -> if item.Status = Persistent then Some name else None) |> Set.ofSeq ]
            let projected =
                { restored with
                    Words = persistentWords
                    WordIds = restored.WordIds |> Map.filter (fun name _ -> persistentWords.ContainsKey name)
                    Records = restored.Records |> Map.filter (fun _ value -> value.Status = Persistent)
                    Scalars = restored.Scalars |> Map.filter (fun _ value -> value.Status = Persistent)
                    Enums = restored.Enums |> Map.filter (fun _ value -> value.Status = Persistent)
                    TypeSources = restored.TypeSources |> Map.filter (fun name _ -> persistentTypeNames.Contains name)
                    Tests = Map.empty
                    Examples = Map.empty
                    FlowWords = restored.FlowWords |> Map.filter (fun _ authored -> persistentOwnerIds.Contains authored.Source.OwnerId)
                    FlowTests = restored.FlowTests |> Map.filter (fun _ attachment -> persistentOwnerIds.Contains attachment.Source.OwnerId)
                    FlowTestFiles = restored.FlowTestFiles |> Map.filter (fun _ file -> persistentOwnerIds.Contains file.OwnerId)
                    FlowExamples = restored.FlowExamples |> Map.filter (fun _ attachment -> persistentOwnerIds.Contains attachment.Source.OwnerId)
                    FlowHistory =
                        restored.FlowHistory
                        |> Map.filter (fun identity revisions ->
                            persistentOwnerIds.Contains (WordId identity)
                            && (revisions |> List.forall (fun authored -> wordIdText authored.Source.OwnerId = identity)))
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
            let flowTypeNames =
                state.TypeSources
                |> Map.toSeq
                |> Seq.choose (fun (name, source) -> if source.SourceFormat.Frontend = SourceFrontend.Flow then Some name else None)
                |> Set.ofSeq
            let hasFlow = not (Map.isEmpty state.FlowWords) || not (Map.isEmpty state.FlowTestFiles) || not flowTypeNames.IsEmpty
            let stackWordNames = state.FlowWords |> Map.toSeq |> Seq.map (fun (_, authored) -> authored.Source.OwnerName) |> Set.ofSeq
            let flowTestKeys = state.FlowTests |> Map.toSeq |> Seq.map (fun (_, item) -> item.Source.OwnerName + "/" + item.Source.CaseName) |> Set.ofSeq
            let flowExampleKeys = state.FlowExamples |> Map.toSeq |> Seq.map (fun (_, item) -> item.Source.OwnerName + "/" + item.Source.CaseName) |> Set.ofSeq
            let addSection (frontend: string) (version: int) (source: string) =
                if hasFlow then sections.Add("// frontend: " + frontend + "/" + string version)
                sections.Add(source.Replace("\r\n", "\n"))
            for name, item in state.Records |> Map.toSeq |> Seq.sortBy fst do
                if item.Status = Persistent then
                    match (state.TypeSources.TryFind name |> Option.defaultValue (defaultTypeSource state name)).SourceFormat with
                    | { Frontend = SourceFrontend.Stack; Version = 1 } -> addSection "stack" 1 (Source.renderRecord item.Definition)
                    | { Frontend = SourceFrontend.Flow; Version = version } when version = 1 || version = 2 -> addSection "flow" version (FlowSource.renderRecordWithVersion version item.Definition)
                    | format -> error "RUNTIME_UNSUPPORTED_TYPE_FRONTEND" $"Type '{name}' uses unsupported source format {format.Frontend}/{format.Version}." (Some name) None [ "Stack/1"; "Flow/1"; "Flow/2" ] [ $"{format.Frontend}/{format.Version}" ]
            for name, item in state.Scalars |> Map.toSeq |> Seq.sortBy fst do
                if item.Status = Persistent then
                    match (state.TypeSources.TryFind name |> Option.defaultValue (defaultTypeSource state name)).SourceFormat with
                    | { Frontend = SourceFrontend.Stack; Version = 1 } -> addSection "stack" 1 (Source.renderScalar item.Definition)
                    | { Frontend = SourceFrontend.Flow; Version = version } when version = 1 || version = 2 -> addSection "flow" version (FlowSource.renderScalarWithVersion version item.Definition)
                    | format -> error "RUNTIME_UNSUPPORTED_TYPE_FRONTEND" $"Type '{name}' uses unsupported source format {format.Frontend}/{format.Version}." (Some name) None [ "Stack/1"; "Flow/1"; "Flow/2" ] [ $"{format.Frontend}/{format.Version}" ]
            for name, item in state.Enums |> Map.toSeq |> Seq.sortBy fst do
                if item.Status = Persistent then
                    match (state.TypeSources.TryFind name |> Option.defaultValue (defaultTypeSource state name)).SourceFormat with
                    | { Frontend = SourceFrontend.Flow; Version = 2 } -> addSection "flow" 2 (FlowSource.renderEnumWithVersion 2 item.Definition)
                    | format -> error "RUNTIME_UNSUPPORTED_TYPE_FRONTEND" $"Enum type '{name}' uses unsupported source format {format.Frontend}/{format.Version}." (Some name) None [ "Flow/2" ] [ $"{format.Frontend}/{format.Version}" ]
            for word in topologicalWords state do
                if not (stackWordNames.Contains word.Definition.Name) then addSection "stack" 1 (Source.renderWord true word.Definition)
            state.FlowWords
            |> Map.toList
            |> List.map snd
            |> List.sortBy (fun authored -> authored.Definition.Name)
            |> List.iter (fun authored -> addSection "flow" authored.Definition.SyntaxVersion (FlowSource.renderWord authored.Definition))
            let durableTypes =
                Set.union
                    (state.Records |> Map.toSeq |> Seq.choose (fun (name, item) -> if item.Status = Persistent then Some(lowerFirst name) else None) |> Set.ofSeq)
                    (state.Scalars |> Map.toSeq |> Seq.choose (fun (name, item) -> if item.Status = Persistent then Some name else None) |> Set.ofSeq)
                    |> fun found -> Set.union found (state.Enums |> Map.toSeq |> Seq.choose (fun (name, item) -> if item.Status = Persistent then Some name else None) |> Set.ofSeq)
            let durableTests =
                state.Tests
                |> Map.toList
                |> List.map snd
                |> List.filter (fun test -> not (flowTestKeys.Contains(test.Word + "/" + test.Name)))
                |> List.filter (fun test ->
                    state.Words.TryFind test.Word |> Option.exists (fun word -> word.Status = Persistent)
                    || (durableTypes |> Set.exists (fun prefix -> test.Word = prefix + ".new" || test.Word = prefix + ".value" || test.Word.StartsWith(prefix + ".", StringComparison.Ordinal)))
                )
                |> List.sortBy (fun test -> test.Word, test.Name)
            for test in durableTests do addSection "stack" 1 (Source.renderTest test)
            state.FlowTests
            |> Map.toList
            |> List.map snd
            |> List.filter (fun authored -> not (state.FlowTestFiles.ContainsKey(flowTestFileKey authored.Source.OwnerId authored.Source.Reference)))
            |> List.filter (fun authored -> state.Words.TryFind authored.Source.OwnerName |> Option.exists (fun word -> word.Status = Persistent))
            |> List.sortBy (fun authored -> authored.Source.OwnerName, authored.Source.CaseName)
            |> List.iter (fun authored ->
                match FlowParser.parseTestWithVersion authored.Source.SyntaxVersion authored.Source.SourceFile authored.Source.Content with
                | Error diagnostic -> raise (LanguageException diagnostic)
                | Ok definition -> addSection "flow" authored.Source.SyntaxVersion (FlowSource.renderTest definition))
            state.FlowTestFiles
            |> Map.toList
            |> List.map snd
            |> List.filter (fun authored -> state.Words.TryFind authored.OwnerName |> Option.exists (fun word -> word.Status = Persistent))
            |> List.sortBy (fun authored -> authored.OwnerName, authored.Reference.Hash)
            |> List.iter (fun authored ->
                let sourceObject = Storage.sourceObject StorageObjectKind.TestDefinition authored.Settings.SourceText
                if sourceObject.Reference <> authored.Reference then
                    error "FLOW_RUNTIME_SOURCE_MISMATCH" "A test-file wrapper no longer matches its immutable source reference." (Some authored.OwnerName) None [ authored.Reference.Hash ] [ sourceObject.Reference.Hash ]
                addSection "flow" authored.SyntaxVersion authored.Settings.SourceText)
            let durableExamples =
                state.Examples
                |> Map.toList
                |> List.map snd
                |> List.filter (fun example -> not (flowExampleKeys.Contains(example.Word + "/" + example.Name)))
                |> List.filter (fun example ->
                    state.Words.TryFind example.Word |> Option.exists (fun word -> word.Status = Persistent)
                    || (durableTypes |> Set.exists (fun prefix -> example.Word = prefix + ".new" || example.Word = prefix + ".value" || example.Word.StartsWith(prefix + ".", StringComparison.Ordinal)))
                )
                |> List.sortBy (fun example -> example.Word, example.Name)
            for example in durableExamples do addSection "stack" 1 (Source.renderExample example)
            state.FlowExamples
            |> Map.toList
            |> List.map snd
            |> List.filter (fun authored -> state.Words.TryFind authored.Source.OwnerName |> Option.exists (fun word -> word.Status = Persistent))
            |> List.sortBy (fun authored -> authored.Source.OwnerName, authored.Source.CaseName)
            |> List.iter (fun authored ->
                match FlowParser.parseExampleWithVersion authored.Source.SyntaxVersion authored.Source.SourceFile authored.Source.Content with
                | Error diagnostic -> raise (LanguageException diagnostic)
                | Ok definition -> addSection "flow" authored.Source.SyntaxVersion (FlowSource.renderExample definition))
            if hasFlow then String.concat "\n\n" sections + "\n"
            else String.concat (Environment.NewLine + Environment.NewLine) sections + Environment.NewLine

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
            let enums: Map<string, EnumEntry> =
                parsed.Enums
                |> List.map (fun (value: EnumDefinition) -> value.Name, ({ Definition = value; Status = Persistent }: EnumEntry))
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
                    Enums = enums
                    TypeSources = Map.empty
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

            let manifestBase = currentManifest |> Option.defaultValue { FormatVersion = 2; ProjectSource = projectObject.Reference; Types = []; TypeRevisions = []; Words = []; Revisions = [] }
            let typeSources =
                [ for KeyValue(name, item) in durable.Records do
                      let authored = durable.TypeSources.TryFind name |> Option.defaultValue (defaultTypeSource durable name)
                      let sourceObject = Storage.sourceObject StorageObjectKind.TypeDefinition authored.Content
                      if sourceObject.Reference <> authored.Reference then
                          error "TYPE_SOURCE_REFERENCE_MISMATCH" $"Type source '{name}' does not match its immutable source reference." (Some name) None [ authored.Reference.Hash ] [ sourceObject.Reference.Hash ]
                      sourceObjects.Add sourceObject
                      let resolved = resolvedRecordValidatorTarget durable name item.Definition
                      match authored.ValidatorTarget, resolved with
                      | Some stored, Some actual when stored <> actual ->
                          error "TYPE_VALIDATOR_TARGET_MISMATCH" $"Record type '{name}' validator target changed since its authored source was accepted." (Some name) (Some item.Definition.Span) [ string stored ] [ string actual ]
                      | Some _, None ->
                          error "TYPE_VALIDATOR_TARGET_INVALID" $"Record type '{name}' has a stored validator target without a validator declaration." (Some name) (Some item.Definition.Span) [] []
                      | _ -> ()
                      yield { Name = name; Definition = sourceObject.Reference; SourceFormat = authored.SourceFormat; ValidatorTarget = resolved }
                  for KeyValue(name, item) in durable.Scalars do
                      let authored = durable.TypeSources.TryFind name |> Option.defaultValue (defaultTypeSource durable name)
                      let sourceObject = Storage.sourceObject StorageObjectKind.TypeDefinition authored.Content
                      if sourceObject.Reference <> authored.Reference then
                          error "TYPE_SOURCE_REFERENCE_MISMATCH" $"Type source '{name}' does not match its immutable source reference." (Some name) None [ authored.Reference.Hash ] [ sourceObject.Reference.Hash ]
                      sourceObjects.Add sourceObject
                      let resolved = resolvedValidatorTarget durable name item.Definition
                      match authored.ValidatorTarget, resolved with
                      | Some stored, Some actual when stored <> actual ->
                          error "TYPE_VALIDATOR_TARGET_MISMATCH" $"Scalar type '{name}' validator target changed since its authored source was accepted." (Some name) (Some item.Definition.Span) [ string stored ] [ string actual ]
                      | Some _, None ->
                          error "TYPE_VALIDATOR_TARGET_INVALID" $"Scalar type '{name}' has a stored validator target without a validator declaration." (Some name) (Some item.Definition.Span) [] []
                      | _ -> ()
                      yield { Name = name; Definition = sourceObject.Reference; SourceFormat = authored.SourceFormat; ValidatorTarget = resolved }
                  for KeyValue(name, _) in durable.Enums do
                      let authored = durable.TypeSources.TryFind name |> Option.defaultValue (defaultTypeSource durable name)
                      let sourceObject = Storage.sourceObject StorageObjectKind.TypeDefinition authored.Content
                      if sourceObject.Reference <> authored.Reference then
                          error "TYPE_SOURCE_REFERENCE_MISMATCH" $"Type source '{name}' does not match its immutable source reference." (Some name) None [ authored.Reference.Hash ] [ sourceObject.Reference.Hash ]
                      sourceObjects.Add sourceObject
                      if authored.ValidatorTarget.IsSome then
                          error "TYPE_VALIDATOR_TARGET_INVALID" $"Enum type '{name}' cannot carry a scalar validator target." (Some name) None [] []
                      yield { Name = name; Definition = sourceObject.Reference; SourceFormat = authored.SourceFormat; ValidatorTarget = None } ]
            let retainedTypeRevisions = ResizeArray<TypeSourceRevision>(manifestBase.TypeRevisions)
            let currentTypesByName = typeSources |> List.map (fun item -> item.Name, item) |> Map.ofList
            for previousHead in manifestBase.Types do
                match currentTypesByName.TryFind previousHead.Name with
                | Some nextHead when nextHead <> previousHead ->
                    let revision =
                        retainedTypeRevisions
                        |> Seq.filter (fun item -> item.Name = previousHead.Name)
                        |> Seq.length
                        |> fun previousCount -> previousCount + 1
                    retainedTypeRevisions.Add
                        { Name = previousHead.Name
                          Revision = revision
                          Definition = previousHead.Definition
                          SourceFormat = previousHead.SourceFormat
                          ValidatorTarget = previousHead.ValidatorTarget }
                | _ -> ()
            let typeRevisions = retainedTypeRevisions |> Seq.sortBy (fun item -> item.Name, item.Revision) |> Seq.toList
            let manifestVersion =
                let hasIndependentFlowAttachmentFormat =
                    let ownerVersion ownerId =
                        durable.FlowWords
                        |> Map.tryFind (wordIdText ownerId)
                        |> Option.map (fun owner -> owner.Source.SyntaxVersion)
                    let differs ownerId attachmentVersion =
                        ownerVersion ownerId |> Option.exists (fun version -> version <> attachmentVersion)
                    (durable.FlowTests |> Map.exists (fun _ item -> differs item.Source.OwnerId item.Source.SyntaxVersion))
                    || (durable.FlowTestFiles |> Map.exists (fun _ item -> differs item.OwnerId item.SyntaxVersion))
                    || (durable.FlowExamples |> Map.exists (fun _ item -> differs item.Source.OwnerId item.Source.SyntaxVersion))
                if manifestBase.FormatVersion >= 6 || not (List.isEmpty typeRevisions) then 6
                elif manifestBase.FormatVersion >= 5 || hasIndependentFlowAttachmentFormat then 5
                elif manifestBase.FormatVersion >= 4 || not (Map.isEmpty durable.FlowTestFiles) then 4
                elif manifestBase.FormatVersion >= 3
                     || (typeSources |> List.exists (fun source -> source.SourceFormat.Frontend = SourceFrontend.Flow || source.ValidatorTarget.IsSome)) then 3
                else 2
            let revisions = ResizeArray<WordRevision>(manifestBase.Revisions)
            let mutable revisionKeys = revisions |> Seq.map (fun item -> item.WordId, item.Revision) |> Set.ofSeq
            let taskId = activeTask |> Option.filter (fun task -> task.Active) |> Option.map (fun task -> task.Id)
            let timestamp = DateTimeOffset.Parse(fixedClock, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime()

            let attachedSources (wordName: string) =
                let ownerId = durable.WordIds.TryFind wordName |> Option.map WordId
                let flowWord = ownerId |> Option.bind (fun identity -> durable.FlowWords.TryFind(wordIdText identity))
                let flowTestKeys =
                    durable.FlowTests
                    |> Map.toSeq
                    |> Seq.choose (fun (_, item) -> if item.Source.OwnerName = wordName then Some(item.Source.OwnerName, item.Source.CaseName) else None)
                    |> Set.ofSeq
                let flowExampleKeys =
                    durable.FlowExamples
                    |> Map.toSeq
                    |> Seq.choose (fun (_, item) -> if item.Source.OwnerName = wordName then Some(item.Source.OwnerName, item.Source.CaseName) else None)
                    |> Set.ofSeq
                let stackTests =
                    durable.Tests
                    |> Map.toSeq
                    |> Seq.map snd
                    |> Seq.filter (fun test -> test.Word = wordName && not (flowTestKeys.Contains(test.Word, test.Name)))
                    |> Seq.toList
                let stackExamples =
                    durable.Examples
                    |> Map.toSeq
                    |> Seq.map snd
                    |> Seq.filter (fun example -> example.Word = wordName && not (flowExampleKeys.Contains(example.Word, example.Name)))
                    |> Seq.toList
                if flowWord.IsSome && (not (List.isEmpty stackTests) || not (List.isEmpty stackExamples)) then
                    error "FLOW_RUNTIME_FRONTEND_MIXED_ATTACHMENTS" "A Flow-authored word cannot persist Stack test or example sources; replace each inherited case with a Flow source before publishing." (Some wordName) None
                        (flowWord |> Option.map (fun authored -> authored.Source.OwnerName) |> Option.toList)
                        ((stackTests |> List.map (fun test -> "test/" + test.Name)) @ (stackExamples |> List.map (fun example -> "example/" + example.Name)))
                if flowWord.IsNone
                   && ((durable.FlowTests |> Map.exists (fun _ item -> ownerId = Some item.Source.OwnerId))
                       || (durable.FlowExamples |> Map.exists (fun _ item -> ownerId = Some item.Source.OwnerId))) then
                    error "FLOW_RUNTIME_FRONTEND_MIXED_ATTACHMENTS" "Flow test and example sources require a Flow-authored owner definition." (Some wordName) None [ "Flow definition" ] [ "Stack definition" ]
                let testsWithFormats =
                    durable.Tests
                    |> Map.toSeq
                    |> Seq.map snd
                    |> Seq.filter (fun test -> test.Word = wordName)
                    |> Seq.filter (fun test -> not (flowTestKeys.Contains(test.Word, test.Name)))
                    |> Seq.sortBy (fun test -> test.Name)
                    |> Seq.map (fun test ->
                        let sourceObject = Storage.sourceObject StorageObjectKind.TestDefinition (Source.renderTest test)
                        sourceObjects.Add sourceObject
                        sourceObject.Reference, { Frontend = SourceFrontend.Stack; Version = 1 })
                    |> Seq.toList
                    |> fun stack ->
                        let flow =
                            durable.FlowTests
                            |> Map.toSeq
                            |> Seq.map snd
                            |> Seq.filter (fun item ->
                                item.Source.OwnerName = wordName
                                && not (durable.FlowTestFiles.ContainsKey(flowTestFileKey item.Source.OwnerId item.Source.Reference)))
                            |> Seq.sortBy (fun item -> item.Source.CaseName)
                            |> Seq.map (fun item ->
                                let sourceObject = Storage.sourceObject StorageObjectKind.TestDefinition item.Source.Content
                                if sourceObject.Reference <> item.Source.Reference then
                                    error "FLOW_RUNTIME_SOURCE_MISMATCH" "A Flow test source no longer matches its immutable source reference." (Some wordName) None [ item.Source.Reference.Hash ] [ sourceObject.Reference.Hash ]
                                sourceObjects.Add sourceObject
                                sourceObject.Reference, { Frontend = SourceFrontend.Flow; Version = item.Source.SyntaxVersion })
                            |> Seq.toList
                        let files =
                            durable.FlowTestFiles
                            |> Map.toSeq
                            |> Seq.map snd
                            |> Seq.filter (fun item -> item.OwnerName = wordName)
                            |> Seq.sortBy (fun item -> item.Reference.Hash)
                            |> Seq.map (fun item ->
                                let sourceObject = Storage.sourceObject StorageObjectKind.TestDefinition item.Settings.SourceText
                                if sourceObject.Reference <> item.Reference then
                                    error "FLOW_RUNTIME_SOURCE_MISMATCH" "A Flow test-file source no longer matches its immutable source reference." (Some wordName) None [ item.Reference.Hash ] [ sourceObject.Reference.Hash ]
                                sourceObjects.Add sourceObject
                                sourceObject.Reference, { Frontend = SourceFrontend.Flow; Version = item.SyntaxVersion })
                            |> Seq.toList
                        stack @ flow @ files
                let examplesWithFormats =
                    durable.Examples
                    |> Map.toSeq
                    |> Seq.map snd
                    |> Seq.filter (fun example -> example.Word = wordName)
                    |> Seq.filter (fun example -> not (flowExampleKeys.Contains(example.Word, example.Name)))
                    |> Seq.sortBy (fun example -> example.Name)
                    |> Seq.map (fun example ->
                        let sourceObject = Storage.sourceObject StorageObjectKind.ExampleDefinition (Source.renderExample example)
                        sourceObjects.Add sourceObject
                        sourceObject.Reference, { Frontend = SourceFrontend.Stack; Version = 1 })
                    |> Seq.toList
                    |> fun stack ->
                        let flow =
                            durable.FlowExamples
                            |> Map.toSeq
                            |> Seq.map snd
                            |> Seq.filter (fun item -> item.Source.OwnerName = wordName)
                            |> Seq.sortBy (fun item -> item.Source.CaseName)
                            |> Seq.map (fun item ->
                                let sourceObject = Storage.sourceObject StorageObjectKind.ExampleDefinition item.Source.Content
                                if sourceObject.Reference <> item.Source.Reference then
                                    error "FLOW_RUNTIME_SOURCE_MISMATCH" "A Flow example source no longer matches its immutable source reference." (Some wordName) None [ item.Source.Reference.Hash ] [ sourceObject.Reference.Hash ]
                                sourceObjects.Add sourceObject
                                sourceObject.Reference, { Frontend = SourceFrontend.Flow; Version = item.Source.SyntaxVersion })
                            |> Seq.toList
                        stack @ flow
                let tests = testsWithFormats |> List.map fst
                let examples = examplesWithFormats |> List.map fst
                let attachmentSourceFormats =
                    testsWithFormats @ examplesWithFormats
                    |> List.groupBy fst
                    |> List.map (fun (reference, entries) ->
                        let formats = entries |> List.map snd |> List.distinct
                        match formats with
                        | [ sourceFormat ] -> reference, sourceFormat
                        | _ ->
                            error "FLOW_RUNTIME_ATTACHMENT_METADATA_MISMATCH" "One immutable Flow attachment reference cannot have multiple source syntax versions." (Some wordName) None
                                [ reference.Hash ] (formats |> List.map (fun format -> $"{format.Frontend}/{format.Version}")))
                    |> Map.ofList
                flowWord |> Option.iter (fun authored ->
                    if authored.Source.OwnerName <> wordName then
                        error "FLOW_RUNTIME_OWNER_MISMATCH" "A Flow source is indexed under a different owner name." (Some wordName) None [ wordName ] [ authored.Source.OwnerName ])
                tests, examples, attachmentSourceFormats

            let addRevision revisionActor revisionTaskId (item: WordEntry) =
                match durable.WordIds.TryFind item.Definition.Name with
                | None -> error "WORD_ID_MISSING" $"Persistent word '{item.Definition.Name}' has no stable identity." (Some item.Definition.Name) None [] []
                | Some wordId when revisionKeys.Contains(wordId, item.Definition.Revision) -> ()
                | Some wordId ->
                    let flowWord = durable.FlowWords.TryFind wordId
                    let ownerIdentity = WordId wordId
                    let definitionObject, sourceFormat, wordBindings =
                        match flowWord with
                        | Some authored ->
                            if authored.Source.OwnerId <> WordId wordId
                               || authored.Source.OwnerName <> item.Definition.Name
                               || authored.Source.OwnerRevision <> item.Definition.Revision
                               || authored.Source.SyntaxVersion <> authored.Definition.SyntaxVersion
                               || not (List.contains authored.Definition.SyntaxVersion [ 1; 2 ]) then
                                error "FLOW_RUNTIME_OWNER_MISMATCH" "A Flow definition must match the persistent word ID, name, revision, and supported source version." (Some item.Definition.Name) None
                                    [ wordId; item.Definition.Name; string item.Definition.Revision; "Flow/1 or Flow/2" ]
                                    [ wordIdText authored.Source.OwnerId; authored.Source.OwnerName; string authored.Source.OwnerRevision; string authored.Definition.SyntaxVersion ]
                            match authored.StoredBindings with
                            | None -> error "FLOW_RUNTIME_BINDINGS_MISSING" "Flow call bindings must be regenerated against the exact candidate program before publication." (Some item.Definition.Name) None [] []
                            | Some bindings ->
                                let sourceObject = Storage.sourceObject StorageObjectKind.WordDefinition authored.Source.Content
                                if sourceObject.Reference <> authored.Source.Reference then
                                    error "FLOW_RUNTIME_SOURCE_MISMATCH" "A Flow definition source no longer matches its immutable source reference." (Some item.Definition.Name) None [ authored.Source.Reference.Hash ] [ sourceObject.Reference.Hash ]
                                sourceObjects.Add sourceObject
                                sourceObject, { Frontend = SourceFrontend.Flow; Version = authored.Definition.SyntaxVersion }, bindings
                        | None ->
                            let sourceObject = Storage.sourceObject StorageObjectKind.WordDefinition (Source.renderWord true item.Definition)
                            sourceObjects.Add sourceObject
                            sourceObject, { Frontend = SourceFrontend.Stack; Version = 1 }, []
                    let tests, examples, attachmentSourceFormats = attachedSources item.Definition.Name
                    let flowTestBindings =
                        durable.FlowTests
                        |> Map.toSeq
                        |> Seq.map snd
                        |> Seq.filter (fun authored -> authored.Source.OwnerId = ownerIdentity)
                        |> Seq.collect (fun authored ->
                            match authored.StoredBindings with
                            | Some bindings -> bindings
                            | None -> error "FLOW_RUNTIME_BINDINGS_MISSING" "Flow test call bindings must be regenerated against the exact candidate program before publication." (Some(item.Definition.Name + "/" + authored.Source.CaseName)) None [] [])
                        |> Seq.toList
                    let flowTestFileBindings =
                        durable.FlowTestFiles
                        |> Map.toSeq
                        |> Seq.map snd
                        |> Seq.filter (fun authored -> authored.OwnerId = ownerIdentity)
                        |> Seq.collect (fun authored ->
                            match authored.StoredBindings with
                            | Some bindings -> bindings
                            | None -> error "FLOW_RUNTIME_BINDINGS_MISSING" "Flow test-file override bindings must be regenerated against the exact candidate program before publication." (Some item.Definition.Name) (Some authored.Settings.Span) [] [])
                        |> Seq.toList
                    let flowExampleBindings =
                        durable.FlowExamples
                        |> Map.toSeq
                        |> Seq.map snd
                        |> Seq.filter (fun authored -> authored.Source.OwnerId = ownerIdentity)
                        |> Seq.collect (fun authored ->
                            match authored.StoredBindings with
                            | Some bindings -> bindings
                            | None -> error "FLOW_RUNTIME_BINDINGS_MISSING" "Flow example call bindings must be regenerated against the exact candidate program before publication." (Some(item.Definition.Name + "/" + authored.Source.CaseName)) None [] [])
                        |> Seq.toList
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
                          Deprecated = durable.Deprecated.Contains item.Definition.Name
                          SourceFormat = sourceFormat
                          AttachmentSourceFormats = attachmentSourceFormats
                          CallBindings = wordBindings @ flowTestBindings @ flowTestFileBindings @ flowExampleBindings }
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
                { FormatVersion = manifestVersion
                  ProjectSource = projectObject.Reference
                  Types = typeSources
                  TypeRevisions = typeRevisions
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
            for entry in user |> Map.toSeq |> Seq.map snd do Compiler.checkDefinitionWithEnums (knownTypes state) (enumDefinitions state) words entry.Definition |> ignore
            for record in state.Records |> Map.toSeq |> Seq.map (fun (_, item) -> item.Definition) do
                let rec validateFieldType (field: RecordField) = function
                    | TNamed name when not ((knownTypes state).Contains name) ->
                        error "TYPE_UNKNOWN_FIELD_TYPE" $"Field '{record.Name}.{field.Name}' uses undeclared type '{name}'." (Some record.Name) (Some record.Span) [ "declared record or scalar type" ] [ name ]
                    | TList item | TOption item -> validateFieldType field item
                    | TResult(ok, failure) -> validateFieldType field ok; validateFieldType field failure
                    | TVar _ -> error "TYPE_UNSUPPORTED_GENERIC" "Record fields cannot contain free generic variables." (Some record.Name) (Some record.Span) [] [ Types.format field.Type ]
                    | _ -> ()
                for field in record.Fields do validateFieldType field field.Type
                let deps = Compiler.checkRecordValidator (knownTypes state) words record
                match record.Validator with
                | Some validator when deps.Contains(lowerFirst record.Name + ".new") ->
                    error "TYPE_VALIDATOR_RECURSION" $"Validator '{validator}' cannot construct '{record.Name}'." (Some record.Name) (Some record.Span) [] (Set.toList deps)
                | _ -> ()
            for scalar in state.Scalars |> Map.toSeq |> Seq.map (fun (_, item) -> item.Definition) do
                let deps = Compiler.checkScalarValidator (knownTypes state) words scalar
                match scalar.Validator with
                | Some validator when deps.Contains(scalar.Name + ".new") || deps.Contains(scalar.Name + ".value") ->
                    error "TYPE_VALIDATOR_RECURSION" $"Validator '{validator}' cannot construct or unwrap '{scalar.Name}'." (Some scalar.Name) (Some scalar.Span) [] (Set.toList deps)
                | _ -> ()
            for test in state.Tests |> Map.toSeq |> Seq.map (fun (_, value) -> value) do Compiler.checkTestWithEnums (knownTypes state) (enumDefinitions state) words test |> ignore
            for example in state.Examples |> Map.toSeq |> Seq.map (fun (_, value) -> value) do Compiler.checkExampleWithEnums (knownTypes state) (enumDefinitions state) words example |> ignore
            let graph =
                words
                |> Map.toSeq
                |> Seq.choose (fun (name, value) ->
                    if value.Builtin.IsSome then None
                    else Some(name, Compiler.dependencies value.Definition.Body))
                |> Map.ofSeq
                |> fun initial ->
                    state.Records
                    |> Map.toSeq
                    |> Seq.fold (fun found (_, record) ->
                        match record.Definition.Validator with
                        | Some validator -> Map.add (lowerFirst record.Definition.Name + ".new") (Set.singleton validator) found
                        | None -> found) initial
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
            if Map.isEmpty state.FlowWords && (not (Map.isEmpty state.FlowTests) || not (Map.isEmpty state.FlowTestFiles) || not (Map.isEmpty state.FlowExamples)) then
                error "FLOW_RUNTIME_ORPHAN_ATTACHMENT" "Flow source attachments cannot exist without a Flow-authored owner definition." None None [ "Flow owner" ] []
            if Map.isEmpty state.FlowWords then validateGraph state
            let flowNames = state.FlowWords |> Map.toSeq |> Seq.map (fun (_, authored) -> authored.Source.OwnerName) |> Set.ofSeq
            let stackBase =
                { state with
                    Words = state.Words |> Map.filter (fun name _ -> not (flowNames.Contains name))
                    WordIds = state.WordIds |> Map.filter (fun name _ -> not (flowNames.Contains name)) }
            let baseWords = effectiveWords stackBase
            let baseCompilerContext: Compiler.IrLoweringContext =
                { Words = baseWords
                  Records = recordDefinitions state
                  Scalars = scalarDefinitions state
                  Enums = enumDefinitions state
                  WordIds = baseWords |> Map.map (fun name item -> WordId(wordIdentity stackBase item)) }
            let flowContext: FlowLowering.Context =
                { FlowLowering.CompilerContext = baseCompilerContext
                  ParameterNames = Map.empty
                  Flow2OwnerIds = Set.empty
                  SourceOrigins = Map.empty }
            let flowProject =
                if Map.isEmpty state.FlowWords then None
                else
                    if not (Map.isEmpty state.FlowTests) || not (Map.isEmpty state.FlowTestFiles) || not (Map.isEmpty state.FlowExamples) then
                        let flowOwners = state.FlowWords |> Map.toSeq |> Seq.map (fun (_, authored) -> authored.Source.OwnerId) |> Set.ofSeq
                        for KeyValue(_, attachment) in state.FlowTests do
                            if not (flowOwners.Contains attachment.Source.OwnerId) then
                                error "FLOW_RUNTIME_ORPHAN_ATTACHMENT" "A Flow test source has no Flow-authored owner definition in this dictionary snapshot." (Some(attachment.Source.OwnerName + "/" + attachment.Source.CaseName)) None [] [ wordIdText attachment.Source.OwnerId ]
                        for KeyValue(_, attachment) in state.FlowExamples do
                            if not (flowOwners.Contains attachment.Source.OwnerId) then
                                error "FLOW_RUNTIME_ORPHAN_ATTACHMENT" "A Flow example source has no Flow-authored owner definition in this dictionary snapshot." (Some(attachment.Source.OwnerName + "/" + attachment.Source.CaseName)) None [] [ wordIdText attachment.Source.OwnerId ]
                    elif Map.isEmpty state.FlowWords && (not (Map.isEmpty state.FlowTests) || not (Map.isEmpty state.FlowExamples)) then
                        error "FLOW_RUNTIME_ORPHAN_ATTACHMENT" "Flow source attachments cannot exist without a Flow-authored owner definition." None None [ "Flow owner" ] []
                    let ownerNames = state.FlowWords |> Map.toSeq |> Seq.map (fun (_, authored) -> authored.Source.OwnerName) |> Seq.toList
                    if (ownerNames |> List.distinct).Length <> ownerNames.Length then
                        error "FLOW_RUNTIME_OWNER_DUPLICATE" "A dictionary snapshot cannot map multiple stable Flow owners to the same word name." None None [] ownerNames
                    for KeyValue(identity, authored) in state.FlowWords do
                        let entry =
                            state.Words.TryFind authored.Source.OwnerName
                            |> Option.defaultWith (fun () -> error "FLOW_RUNTIME_OWNER_MISSING" "A Flow source has no synchronized executable word entry." (Some authored.Source.OwnerName) None [] [])
                        let mappedIdentity = state.WordIds.TryFind authored.Source.OwnerName |> Option.map WordId
                        if identity <> wordIdText authored.Source.OwnerId
                           || mappedIdentity <> Some authored.Source.OwnerId
                           || entry.Builtin.IsSome
                           || entry.Status = Primitive
                           || entry.Revision <> authored.Source.OwnerRevision
                           || entry.Definition.Revision <> authored.Source.OwnerRevision
                           || entry.Definition.Name <> authored.Definition.Name
                           || authored.Definition.Name <> authored.Source.OwnerName
                           || authored.Source.SyntaxVersion <> authored.Definition.SyntaxVersion
                           || not (List.contains authored.Definition.SyntaxVersion [ 1; 2 ])
                           || authored.Source.Reference.Kind <> StorageObjectKind.WordDefinition then
                            error "FLOW_RUNTIME_OWNER_METADATA_MISMATCH" "Flow authored source identity, revision, kind, and executable projection must describe the same user word." (Some authored.Source.OwnerName) None
                                [ identity; wordIdText authored.Source.OwnerId; authored.Source.OwnerName; string entry.Revision; "Flow/1 or Flow/2" ]
                                [ mappedIdentity |> Option.map wordIdText |> Option.defaultValue "missing"; string authored.Source.OwnerRevision; string authored.Source.Reference.Kind; string authored.Definition.SyntaxVersion ]
                    let validateAttachment kind (key: string) (attachment: FlowAuthoredAttachment) =
                        let source = attachment.Source
                        let ownerKey = wordIdText source.OwnerId
                        let owner =
                            state.FlowWords.TryFind ownerKey
                            |> Option.defaultWith (fun () -> error "FLOW_RUNTIME_ORPHAN_ATTACHMENT" "A Flow attachment source has no Flow-authored owner definition." (Some(source.OwnerName + "/" + source.CaseName)) None [] [ ownerKey ])
                        let ownerEntry =
                            state.Words.TryFind source.OwnerName
                            |> Option.defaultWith (fun () -> error "FLOW_RUNTIME_OWNER_MISSING" "A Flow attachment owner has no executable word entry." (Some source.OwnerName) None [] [])
                        let expectedKind =
                            match kind with
                            | FlowLowering.FlowAttachmentKind.Test -> StorageObjectKind.TestDefinition
                            | FlowLowering.FlowAttachmentKind.Example -> StorageObjectKind.ExampleDefinition
                        let sourceVersionSupported = List.contains source.SyntaxVersion [ 1; 2 ]
                        if key <> flowAttachmentKey source.OwnerId source.CaseName
                           || source.OwnerName <> owner.Source.OwnerName
                           || not sourceVersionSupported
                           || source.OwnerRevision <> ownerEntry.Revision
                           || source.OwnerRevision <> owner.Source.OwnerRevision
                           || source.Reference.Kind <> expectedKind
                           || source.Kind <> kind then
                            error "FLOW_RUNTIME_ATTACHMENT_METADATA_MISMATCH" "Flow attachment key, supported source version, owner revision, source kind, and owner source must agree within the runtime snapshot." (Some(source.OwnerName + "/" + source.CaseName)) None
                                [ flowAttachmentKey source.OwnerId source.CaseName; owner.Source.OwnerName; "Flow/1 or Flow/2"; ownerEntry.Revision.ToString(CultureInfo.InvariantCulture); string expectedKind ]
                                [ key; source.OwnerName; string source.SyntaxVersion; string source.OwnerRevision; string source.Reference.Kind ]
                    state.FlowTests |> Map.iter (fun key attachment -> validateAttachment FlowLowering.FlowAttachmentKind.Test key attachment)
                    state.FlowExamples |> Map.iter (fun key attachment -> validateAttachment FlowLowering.FlowAttachmentKind.Example key attachment)
                    let wrapperCaseKeys = System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
                    let wrapperScopeKeys = System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
                    for KeyValue(key, file) in state.FlowTestFiles do
                        let ownerKey = wordIdText file.OwnerId
                        let owner =
                            state.FlowWords.TryFind ownerKey
                            |> Option.defaultWith (fun () -> error "FLOW_RUNTIME_ORPHAN_ATTACHMENT" "A test-file wrapper has no Flow-authored owner definition." (Some file.OwnerName) None [] [ ownerKey ])
                        let ownerEntry =
                            state.Words.TryFind file.OwnerName
                            |> Option.defaultWith (fun () -> error "FLOW_RUNTIME_OWNER_MISSING" "A test-file wrapper owner has no executable word entry." (Some file.OwnerName) None [] [])
                        let expectedKey = flowTestFileKey file.OwnerId file.Reference
                        let sourceObject = Storage.sourceObject StorageObjectKind.TestDefinition file.Settings.SourceText
                        let sourceVersionSupported = file.SyntaxVersion = 2
                        if key <> expectedKey
                           || owner.Source.OwnerName <> file.OwnerName
                           || file.Settings.SyntaxVersion <> file.SyntaxVersion
                           || not sourceVersionSupported
                           || file.OwnerRevision <> ownerEntry.Revision
                           || file.OwnerRevision <> owner.Source.OwnerRevision
                           || file.Reference.Kind <> StorageObjectKind.TestDefinition
                           || sourceObject.Reference <> file.Reference then
                            error "FLOW_RUNTIME_TEST_FILE_METADATA_MISMATCH" "Test-file owner, supported source version, revision, source kind, and immutable source must agree within the runtime snapshot." (Some file.OwnerName) None
                                 [ expectedKey; owner.Source.OwnerName; "Flow/2"; ownerEntry.Revision.ToString(CultureInfo.InvariantCulture); sourceObject.Reference.Hash ]
                                [ key; file.OwnerName; string file.SyntaxVersion; string file.OwnerRevision; file.Reference.Hash ]
                        if file.Settings.Tests.IsEmpty then
                            error "FLOW_RUNTIME_TEST_FILE_EMPTY" "A test-file wrapper must retain at least one case." (Some file.OwnerName) (Some file.Settings.Span) [ "one or more tests" ] []
                        let scopeKey = flowAttachmentKey file.OwnerId file.Settings.ScopeName
                        if not (wrapperScopeKeys.Add scopeKey) then
                            error "FLOW_TEST_FILE_SCOPE_DUPLICATE" "A Flow owner cannot have two test-file wrappers with the same scope label." (Some file.OwnerName) (Some file.Settings.Span) [ "unique test-file scope label per owner" ] [ file.Settings.ScopeName ]
                        for test in file.Settings.Tests do
                            let testKey = flowAttachmentKey file.OwnerId test.CaseName
                            if test.Word <> file.OwnerName || not (wrapperCaseKeys.Add testKey) then
                                error "FLOW_RUNTIME_TEST_FILE_OWNER_MISMATCH" "A wrapper case must belong to its one tested owner and may appear only once across wrapper files." (Some(test.Word + "/" + test.CaseName)) (Some test.Span) [ file.OwnerName; "unique case" ] [ test.Word; test.CaseName ]
                            match state.FlowTests.TryFind testKey with
                            | Some attachment when attachment.Source.Reference = file.Reference
                                                     && attachment.Source.OwnerId = file.OwnerId
                                                     && attachment.Source.CaseName = test.CaseName
                                                     && attachment.Source.SyntaxVersion = file.SyntaxVersion -> ()
                            | _ -> error "FLOW_RUNTIME_TEST_FILE_CASE_MISSING" "Each wrapper case must map to a test attachment row that points to the same full-file source." (Some(test.Word + "/" + test.CaseName)) (Some test.Span) [ file.Reference.Hash ] []
                    for KeyValue(_, attachment) in state.FlowTests do
                        let fileKey = flowTestFileKey attachment.Source.OwnerId attachment.Source.Reference
                        if state.FlowTestFiles.ContainsKey fileKey && not (wrapperCaseKeys.Contains(flowAttachmentKey attachment.Source.OwnerId attachment.Source.CaseName)) then
                            error "FLOW_RUNTIME_TEST_FILE_CASE_EXTRA" "A wrapper-backed attachment row is not declared by its full-file source." (Some(attachment.Source.OwnerName + "/" + attachment.Source.CaseName)) None [] [ attachment.Source.Reference.Hash ]
                    let wordInventory: FlowLowering.FlowSourceInventory =
                        { ExpectedFlowOwnerIds = Set.empty
                          Sources = [] }
                    let wordChanges: FlowLowering.FlowSourceChange list =
                        state.FlowWords
                        |> Map.toList
                        |> List.map (fun (_, authored) ->
                            let name = authored.Source.OwnerName
                            let entry =
                                state.Words.TryFind name
                                |> Option.defaultWith (fun () -> error "FLOW_RUNTIME_OWNER_MISSING" "A Flow source has no synchronized executable word entry." (Some name) None [] [])
                            { RevisionIntent = FlowLowering.FlowWordRevisionIntent.Rehydrate(authored.Source.OwnerId, entry.Revision, entry.Status, entry.Maturity)
                              Source = authored.Source })
                    let attachmentInventory: FlowLowering.FlowAttachmentInventory =
                        { ExpectedSources = Map.empty
                          Sources = [] }
                    let attachmentChanges =
                        [ yield!
                            state.FlowTests
                            |> Map.toList
                            |> List.filter (fun (_, attachment) ->
                                not (state.FlowTestFiles.ContainsKey(flowTestFileKey attachment.Source.OwnerId attachment.Source.Reference)))
                            |> List.map (fun (_, attachment) -> FlowLowering.FlowAttachmentChange.Add attachment.Source)
                          yield!
                            state.FlowExamples
                            |> Map.toList
                            |> List.map (fun (_, attachment) -> FlowLowering.FlowAttachmentChange.Add attachment.Source) ]
                    let project =
                        FlowLowering.compileBatchFlowProjectSources flowContext wordInventory wordChanges attachmentInventory attachmentChanges
                    let compiledTestFiles =
                        state.FlowTestFiles
                        |> Map.toList
                        |> List.map (fun (fileKey, file) ->
                            let settings =
                                match FlowParser.parseTestFileSettingsWithVersion file.SyntaxVersion file.SourceFile file.Settings.SourceText with
                                | Ok parsed -> parsed
                                | Error diagnostic -> raise (LanguageException { diagnostic with Word = Some file.OwnerName })
                            let parsedOwners = settings.Tests |> List.map (fun test -> test.Word) |> List.distinct
                            if parsedOwners <> [ file.OwnerName ] then
                                error "FLOW_RUNTIME_TEST_FILE_OWNER_MISMATCH" "A test-file wrapper must retain exactly its recorded Flow owner." (Some file.OwnerName) (Some settings.Span) [ file.OwnerName ] parsedOwners
                            let compilation =
                                FlowLowering.compileTestFileSettings project.WordCompilation.Context project.WordCompilation.Program settings
                            IrInterpreter.validateProgram compilation.Program
                            let overrideBindingSpans =
                                compilation.Overrides
                                |> List.collect (fun item ->
                                    let stored = FlowPersistence.testOverrideBindings file.Reference [ item ]
                                    let spans = item.Header.Span :: (item.BodySites |> List.map (fun site -> site.Span))
                                    if stored.Length <> spans.Length then
                                        error "FLOW_RUNTIME_BINDING_PROOF_MISMATCH" "Test override persistence rows do not correspond to their compiler-proven call sites." (Some file.OwnerName) (Some settings.Span) [ string stored.Length ] [ string spans.Length ]
                                    List.zip stored spans)
                                |> Map.ofList
                            let testsWithBindings =
                                settings.Tests
                                |> List.map (fun test ->
                                    let key = test.Word + "/" + test.CaseName
                                    let compiled = FlowLowering.compileTestWithCallBindings compilation.Context compilation.Program test
                                    let attachment: FlowLowering.FlowAttachmentKey =
                                        { OwnerId = file.OwnerId
                                          Kind = FlowLowering.FlowAttachmentKind.Test
                                          CaseName = test.CaseName }
                                    let callBindings =
                                        compiled.CallSites
                                        |> Map.toList
                                        |> List.collect (fun (role, sites) ->
                                            sites
                                            |> List.map (fun site ->
                                                let binding: FlowLowering.FlowAttachmentCallBinding =
                                                    { Attachment = attachment
                                                      OwnerName = file.OwnerName
                                                      OwnerRevision = file.OwnerRevision
                                                      Source = file.Reference
                                                      BodyRole = role
                                                      Site = site }
                                                binding))
                                    let storedBindings = FlowPersistence.attachmentBindings callBindings
                                    let callBindingSpans =
                                        callBindings
                                        |> List.map (fun item -> FlowPersistence.attachmentBindings [ item ] |> List.head, item.Site.Span)
                                        |> Map.ofList
                                    key, test, compiled, storedBindings, callBindingSpans)
                            let tests = testsWithBindings |> List.map (fun (key, _, compiled, _, _) -> key, compiled) |> Map.ofList
                            let testBindings = testsWithBindings |> List.map (fun (key, _, _, bindings, _) -> key, bindings) |> Map.ofList
                            let bindingSpans =
                                testsWithBindings
                                |> List.collect (fun (_, _, _, _, spans) -> spans |> Map.toList)
                                |> fun spans -> Map.ofList (Map.toList overrideBindingSpans @ spans)
                            let source =
                                { file with
                                    Settings = settings }
                            { Source = source
                              Compilation = compilation
                              Tests = tests
                              TestBindings = testBindings
                              BindingSpans = bindingSpans })
                    let generatedBindings =
                        FlowPersistence.wordBindings project.WordCompilation.CallBindings
                        @ FlowPersistence.attachmentBindings project.AttachmentBindings
                        @ (compiledTestFiles |> List.collect (fun file -> file.TestBindings |> Map.toList |> List.collect snd))
                    let bindingSpans =
                        [ yield!
                            List.zip
                                (FlowPersistence.wordBindings project.WordCompilation.CallBindings)
                                (project.WordCompilation.CallBindings |> List.map (fun binding -> binding.Site.Span))
                          yield!
                            project.AttachmentBindings
                            |> List.collect (fun binding ->
                                FlowPersistence.attachmentBindings [ binding ]
                                |> List.map (fun stored -> stored, binding.Site.Span))
                          yield!
                            compiledTestFiles
                            |> List.collect (fun file -> file.BindingSpans |> Map.toList) ]
                        |> Map.ofList
                    let wordBindingsFor source =
                        generatedBindings
                        |> List.filter (fun binding ->
                            binding.Source = source
                            && binding.CaseName.IsNone
                            && binding.BodyRole = StoredCallBodyRole.Definition)
                    let attachmentBindingsFor source caseName =
                        generatedBindings
                        |> List.filter (fun binding ->
                            binding.Source = source
                            && binding.CaseName = Some caseName
                            && (binding.BodyRole = StoredCallBodyRole.Actual || binding.BodyRole = StoredCallBodyRole.ExpectedExpression))
                    let bindingSiteKey (binding: StoredCallBinding) = binding.Source, binding.CaseName, binding.BodyRole, binding.Path
                    let describeBinding (binding: StoredCallBinding) =
                        sprintf "source=%s case=%A role=%A path=%A form=%A requested=%s target=%A" binding.Source.Hash binding.CaseName binding.BodyRole binding.Path binding.Form binding.RequestedName binding.Target
                    let checkRetainedBinding name fallbackSpan stored actual =
                        match stored with
                        | None -> ()
                        | Some expected ->
                            let sameSite left right = bindingSiteKey left = bindingSiteKey right
                            let changedSite =
                                expected
                                |> List.tryPick (fun prior -> actual |> List.tryFind (sameSite prior) |> Option.filter ((<>) prior))
                            let missingSite = expected |> List.tryFind (fun prior -> not (actual |> List.exists (sameSite prior)))
                            let extraSite = actual |> List.tryFind (fun candidate -> not (expected |> List.exists (sameSite candidate)))
                            let hasMismatch = expected.Length <> actual.Length || expected |> List.exists (fun prior -> not (actual |> List.contains prior))
                            if hasMismatch then
                                let changedActual =
                                    changedSite
                                    |> Option.orElseWith (fun () ->
                                        missingSite
                                        |> Option.bind (fun missing -> actual |> List.tryFind (sameSite missing)))
                                    |> Option.orElse extraSite
                                let changedExpected = changedSite |> Option.orElse missingSite
                                let span =
                                    changedActual
                                    |> Option.bind (fun binding -> bindingSpans.TryFind binding)
                                    |> Option.orElse fallbackSpan
                                error "FLOW_RUNTIME_BINDING_MISMATCH"
                                    $"Persisted Flow call binding for '{name}' changed at an authored site in the final runtime snapshot."
                                    (Some name) span
                                    (changedExpected |> Option.map describeBinding |> Option.toList)
                                    (changedActual |> Option.map describeBinding |> Option.toList)
                    for KeyValue(identity, authored) in state.FlowWords do
                        let mappedIdentity = state.WordIds.TryFind authored.Source.OwnerName |> Option.map WordId
                        if identity <> wordIdText authored.Source.OwnerId || mappedIdentity <> Some authored.Source.OwnerId then
                            error "FLOW_RUNTIME_OWNER_ID_MISMATCH" "Flow source owner identity must match both its stable-ID map key and dictionary ID catalog."
                                (Some authored.Source.OwnerName) None [ wordIdText authored.Source.OwnerId ] [ identity; mappedIdentity |> Option.map wordIdText |> Option.defaultValue "missing" ]
                        checkRetainedBinding authored.Source.OwnerName (Some authored.Definition.Span) authored.StoredBindings (wordBindingsFor authored.Source.Reference)
                    for KeyValue(_, attachment) in state.FlowTests do
                        let ownerSpan = state.FlowWords.TryFind(wordIdText attachment.Source.OwnerId) |> Option.map (fun authored -> authored.Definition.Span)
                        checkRetainedBinding (attachment.Source.OwnerName + "/" + attachment.Source.CaseName) ownerSpan attachment.StoredBindings
                            (attachmentBindingsFor attachment.Source.Reference attachment.Source.CaseName)
                    for KeyValue(_, attachment) in state.FlowExamples do
                        let ownerSpan = state.FlowWords.TryFind(wordIdText attachment.Source.OwnerId) |> Option.map (fun authored -> authored.Definition.Span)
                        checkRetainedBinding (attachment.Source.OwnerName + "/" + attachment.Source.CaseName) ownerSpan attachment.StoredBindings
                            (attachmentBindingsFor attachment.Source.Reference attachment.Source.CaseName)
                    for file in compiledTestFiles do
                        checkRetainedBinding (file.Source.OwnerName + "/test-file") (Some file.Source.Settings.Span) file.Source.StoredBindings
                            (FlowPersistence.testOverrideBindings file.Source.Reference file.Compilation.Overrides)
                    let compiledTestFiles =
                        compiledTestFiles
                        |> List.map (fun file ->
                            let storedBindings = FlowPersistence.testOverrideBindings file.Source.Reference file.Compilation.Overrides
                            { file with Source = { file.Source with StoredBindings = Some storedBindings } })
                    let flowWordBindings =
                        state.FlowWords
                        |> Map.map (fun _ authored -> { authored with StoredBindings = Some(wordBindingsFor authored.Source.Reference) })
                    let updateAttachments kind =
                        project.Attachments
                        |> List.choose (function
                            | FlowLowering.FlowCompiledAttachment.Test(source, _) when kind = FlowLowering.FlowAttachmentKind.Test -> Some(flowAttachmentKey source.OwnerId source.CaseName, source)
                            | FlowLowering.FlowCompiledAttachment.Example(source, _) when kind = FlowLowering.FlowAttachmentKind.Example -> Some(flowAttachmentKey source.OwnerId source.CaseName, source)
                            | _ -> None)
                        |> List.fold (fun found (key, source) ->
                            let actual = attachmentBindingsFor source.Reference source.CaseName
                            Map.add key { Source = source; StoredBindings = Some actual } found) Map.empty
                    let wrapperTestAttachments =
                        compiledTestFiles
                        |> List.collect (fun file ->
                            file.Source.Settings.Tests
                            |> List.map (fun test ->
                                let key = flowAttachmentKey file.Source.OwnerId test.CaseName
                                let source: FlowLowering.FlowAttachmentSourceDocument =
                                    { OwnerName = file.Source.OwnerName
                                      SyntaxVersion = file.Source.SyntaxVersion
                                      OwnerId = file.Source.OwnerId
                                      OwnerRevision = file.Source.OwnerRevision
                                      Kind = FlowLowering.FlowAttachmentKind.Test
                                      CaseName = test.CaseName
                                      Reference = file.Source.Reference
                                      SourceFile = file.Source.SourceFile
                                      Content = file.Source.Settings.SourceText }
                                key, { Source = source; StoredBindings = Some file.TestBindings[test.Word + "/" + test.CaseName] }))
                        |> Map.ofList
                    let updatedState =
                        let compiledWords = project.WordCompilation.Context.CompilerContext.Words
                        let words =
                            state.FlowWords
                            |> Map.fold (fun found _ authored -> Map.add authored.Source.OwnerName compiledWords[authored.Source.OwnerName] found) state.Words
                        let compiledTests =
                            project.Attachments
                            |> List.choose (function
                                | FlowLowering.FlowCompiledAttachment.Test(source, compiled) -> Some(source.OwnerName + "/" + source.CaseName, compiled.Lowered.Definition)
                                | _ -> None)
                            |> Map.ofList
                            |> fun found ->
                                compiledTestFiles
                                |> List.collect (fun file -> file.Source.Settings.Tests |> List.map (fun test -> test.Word + "/" + test.CaseName, file.Tests[test.Word + "/" + test.CaseName].Compiled.Lowered.Definition))
                                |> List.fold (fun current (key, value) -> Map.add key value current) found
                        let compiledExamples =
                            project.Attachments
                            |> List.choose (function
                                | FlowLowering.FlowCompiledAttachment.Example(source, compiled) -> Some(source.OwnerName + "/" + source.CaseName, compiled.Lowered.Definition)
                                | _ -> None)
                            |> Map.ofList
                        { state with
                            Words = words
                            Tests = Map.fold (fun found key value -> Map.add key value found) state.Tests compiledTests
                            Examples = Map.fold (fun found key value -> Map.add key value found) state.Examples compiledExamples
                            FlowWords = flowWordBindings
                            FlowTests = Map.fold (fun found key value -> Map.add key value found) (updateAttachments FlowLowering.FlowAttachmentKind.Test) wrapperTestAttachments
                            FlowTestFiles = compiledTestFiles |> List.map (fun file -> flowTestFileKey file.Source.OwnerId file.Source.Reference, file.Source) |> Map.ofList
                            FlowExamples = updateAttachments FlowLowering.FlowAttachmentKind.Example }
                    let testFileBodies =
                        compiledTestFiles
                        |> List.collect (fun file -> file.Tests |> Map.toList |> List.map (fun (key, compiled) -> key, compiled.Compiled.Body))
                        |> Map.ofList
                    let testFileExpectationBodies =
                        compiledTestFiles
                        |> List.collect (fun file ->
                            file.Tests
                            |> Map.toList
                            |> List.choose (fun (key, compiled) -> compiled.Compiled.ExpectationBody |> Option.map (fun body -> key, body)))
                        |> Map.ofList
                    let testFileOverlays =
                        compiledTestFiles
                        |> List.collect (fun file ->
                            file.Tests
                            |> Map.toList
                            |> List.map (fun (key, _) ->
                                key,
                                { Program = file.Compilation.Program
                                  Dispatch = file.Compilation.Dispatch
                                  ActiveOverrides = file.Source.Settings.Overrides |> List.map (fun item -> item.Definition.Name) }))
                        |> Map.ofList
                    Some(project, updatedState, project.WordCompilation.Context, project.WordCompilation.Program, testFileBodies, testFileExpectationBodies, testFileOverlays)
            let finalState, flowContext, flowProject, words, context, program, compiledFileTestBodies, compiledFileExpectationBodies, testFileOverlays =
                match flowProject with
                | Some(project, updatedState, loweredContext, verifiedProgram, fileTests, fileExpectations, fileOverlays) ->
                    updatedState, Some loweredContext, Some project, project.WordCompilation.Context.CompilerContext.Words, project.WordCompilation.Context.CompilerContext, verifiedProgram, fileTests, fileExpectations, fileOverlays
                | None ->
                    let words = effectiveWords state
                    let context: Compiler.IrLoweringContext =
                        { Words = words
                          Records = recordDefinitions state
                          Scalars = scalarDefinitions state
                          Enums = enumDefinitions state
                          WordIds = words |> Map.map (fun name item -> WordId(wordIdentity state item)) }
                    state, Some flowContext, None, words, context, Compiler.compileIrProgram context, Map.empty, Map.empty, Map.empty
            if not (Map.isEmpty finalState.FlowWords) then validateGraph finalState
            IrInterpreter.validateProgram program
            let flowTestBodies, flowExpectationBodies, flowExampleBodies =
                match flowProject with
                | None -> Map.empty, Map.empty, Map.empty
                | Some project ->
                    let tests, expectations, examples =
                        project.Attachments
                        |> List.fold (fun (tests, expectations, examples) attachment ->
                            match attachment with
                            | FlowLowering.FlowCompiledAttachment.Test(source, compiled) ->
                                let key = source.OwnerName + "/" + source.CaseName
                                let expectations =
                                    match compiled.ExpectationBody with
                                    | Some body -> Map.add key body expectations
                                    | None -> expectations
                                Map.add key compiled.Body tests, expectations, examples
                            | FlowLowering.FlowCompiledAttachment.Example(source, compiled) ->
                                let key = source.OwnerName + "/" + source.CaseName
                                tests, expectations, Map.add key compiled.Body examples) (Map.empty, Map.empty, Map.empty)
                    tests, expectations, examples
            let flowTestKeys = finalState.FlowTests |> Map.toSeq |> Seq.map (fun (_, item) -> item.Source.OwnerName + "/" + item.Source.CaseName) |> Set.ofSeq
            let snapshotSourceOrigins = flowContext |> Option.map (fun value -> value.SourceOrigins) |> Option.defaultValue Map.empty
            let stackTests = finalState.Tests |> Map.filter (fun key _ -> not (flowTestKeys.Contains key))
            let compiledTests =
                stackTests
                |> Map.map (fun _ test -> Compiler.compileIrTestWithExpectationAgainstProgramWithSourceOrigins context program test snapshotSourceOrigins)
            let testBodies =
                Map.fold (fun found key (actual, _) -> Map.add key actual found) flowTestBodies compiledTests
                |> fun found -> Map.fold (fun current key body -> Map.add key body current) found compiledFileTestBodies
            let testExpectationBodies =
                compiledTests
                |> Map.toSeq
                |> Seq.choose (fun (key, (_, expected)) -> expected |> Option.map (fun body -> key, body))
                |> Map.ofSeq
                |> Map.fold (fun found key body -> Map.add key body found) flowExpectationBodies
                |> fun found -> Map.fold (fun current key body -> Map.add key body current) found compiledFileExpectationBodies
            let flowExampleKeys = finalState.FlowExamples |> Map.toSeq |> Seq.map (fun (_, item) -> item.Source.OwnerName + "/" + item.Source.CaseName) |> Set.ofSeq
            let stackExamples = finalState.Examples |> Map.filter (fun key _ -> not (flowExampleKeys.Contains key))
            let exampleBodies =
                stackExamples
                |> Map.map (fun _ example -> Compiler.compileIrExampleAgainstProgramWithSourceOrigins context program example snapshotSourceOrigins)
                |> Map.fold (fun found key body -> Map.add key body found) flowExampleBodies
            { State = finalState
              Words = words
              Context = context
              Program = program
              FlowContext = flowContext
              FlowProject = flowProject
              TestBodies = testBodies
              TestExpectationBodies = testExpectationBodies
              TestFileOverlays = testFileOverlays
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

        let finiteCoverageEvidence (snapshot: RuntimeSnapshot) (word: string) (results: TestCaseResult list) =
            let entry =
                snapshot.Words.TryFind word
                |> Option.defaultWith (fun () -> error "NAME_UNKNOWN_WORD" $"Word '{word}' is not defined." (Some word) None [] [])
            let identity = WordId(wordIdentity snapshot.State entry)
            let program = VerifiedIrProgram.inspect snapshot.Program
            let functionValue =
                program.FunctionsById.TryFind identity
                |> Option.defaultWith (fun () -> error "LIBRARY_COVERAGE_TARGET_MISSING" $"Library target '{word}' is absent from the verified program." (Some word) (Some entry.Definition.Span) [ "verified user function" ] [])
            if functionValue.FunctionRevision <> entry.Revision then
                error "LIBRARY_COVERAGE_REVISION_MISMATCH" $"Library target '{word}' revision differs from the verified test snapshot." (Some word) (Some entry.Definition.Span) [ string entry.Revision ] [ string functionValue.FunctionRevision ]
            let passedOwn = results |> List.filter (fun result -> result.Word = word && result.Passed)
            let inputObservations = passedOwn |> List.collect (fun result -> result.TargetInputs)
            let returnObservations = passedOwn |> List.collect (fun result -> result.TargetReturns)
            let invocationCount = passedOwn |> List.sumBy (fun result -> result.TargetInvocationCount)
            invocationCount, FiniteCoverage.analyze program functionValue inputObservations returnObservations

        let finiteCoverageJson snapshot word results =
            let invocationCount, report = finiteCoverageEvidence snapshot word results
            let positionJson position =
                let node = JsonObject()
                node["position"] <- jint position.Position
                node["type"] <- jstr position.TypeName
                node["required"] <- jsonNode position.Required
                node["observed"] <- jsonNode position.Observed
                node["missing"] <- jsonNode position.Missing
                node
            let node = JsonObject()
            node["targetInvocations"] <- jint invocationCount
            node["inputs"] <- jsonNode (report.Inputs |> List.map positionJson)
            node["returns"] <- jsonNode (report.Returns |> List.map positionJson)
            node["unsupported"] <- jsonNode report.Unsupported
            node["missing"] <- jsonNode (FiniteCoverage.missing report)
            node["complete"] <- jbool (invocationCount > 0 && report.Unsupported.IsEmpty && (FiniteCoverage.missing report).IsEmpty)
            node

        let requireFiniteLibraryCoverage snapshot word results =
            let invocationCount, report = finiteCoverageEvidence snapshot word results
            if not report.Unsupported.IsEmpty then
                error "LIBRARY_FINITE_DOMAIN_UNSUPPORTED" $"Library word '{word}' uses a finite domain that cannot be proven within the bounded coverage model." (Some word) None
                    [ "all declared finite domains proven within 4096 values" ] report.Unsupported
            if invocationCount = 0 then
                error "LIBRARY_FINITE_COVERAGE_INCOMPLETE" $"Library word '{word}' requires at least one passing attached test that invokes this exact function revision." (Some word) None
                    [ "at least one actual target invocation in a passing own test" ] [ "targetInvocations=0" ]
            let missing = FiniteCoverage.missing report
            if not missing.IsEmpty then
                let observed =
                    [ yield! report.Inputs |> List.collect (fun position -> position.Observed |> List.map (fun label -> $"input[{position.Position}] {position.TypeName}: {label}"))
                      yield! report.Returns |> List.collect (fun position -> position.Observed |> List.map (fun label -> $"return[{position.Position}] {position.TypeName}: {label}")) ]
                error "LIBRARY_FINITE_COVERAGE_INCOMPLETE" $"Library word '{word}' has uncovered finite input or return values." (Some word) None missing observed

        let requireLibraryCoverage snapshot word results =
            let requiredInstructions, requiredBranches = coverageObligations snapshot word
            let coveredInstructions = results |> List.filter (fun result -> result.Passed && result.Word = word) |> List.fold (fun found test -> Set.union found test.Instructions) Set.empty
            let coveredBranches = results |> List.filter (fun result -> result.Passed && result.Word = word) |> List.fold (fun found test -> Set.union found test.BranchOutcomes) Set.empty
            let uncovered = Set.difference requiredInstructions coveredInstructions
            let missingBranches = Set.difference requiredBranches coveredBranches
            let ownTests = results |> List.filter (fun test -> test.Word = word && test.Passed)
            if not (Set.isEmpty uncovered && Set.isEmpty missingBranches && not (List.isEmpty ownTests)) then
                error "LIBRARY_COVERAGE_INCOMPLETE" $"Library word '{word}' requires every instruction, case, and declared iteration outcome to be exercised by its own passing attached tests." (Some word) None [] (coverageGapLabels snapshot uncovered missingBranches)
            requireFiniteLibraryCoverage snapshot word ownTests

        let interpreterHost (snapshot: RuntimeSnapshot) (trace: Trace) (body: VerifiedIrBody option) =
            let source site = sourceSite snapshot body site
            let userFunctionScopes = Stack<bool>()
            let finiteObservationPlan =
                match trace.TargetIdentity with
                | Some(functionId, revision) ->
                    let program = VerifiedIrProgram.inspect snapshot.Program
                    match program.FunctionsById.TryFind functionId with
                    | Some functionValue when functionValue.FunctionRevision = revision -> Some(FiniteCoverage.observationPlan program functionValue)
                    | _ -> None
                | None -> None
            let definitionSpan (name: string) =
                snapshot.Words.TryFind name |> Option.map (fun entry -> entry.Definition.Span)
            { PreflightEffects = fun effects word _ ->
                  let effectNames = IrEffects.names effects
                  let grantedCapabilities = if trace.IsTest then engineTestCapabilities else capabilities
                  let missing = Set.difference (Set.ofList effectNames) grantedCapabilities
                  if not (Set.isEmpty missing) then
                      let expected = effectNames
                      let message, failureWord, failureSpan =
                          match word with
                          | Some name ->
                              let names = String.concat ", " (missing |> Set.toList)
                              $"Execution requires capabilities not granted by the host: {names}.", Some name, definitionSpan name
                          | None -> "The expression requires effects not granted by the host.", None, None
                      error "CAPABILITY_DENIED" message failureWord failureSpan expected (grantedCapabilities |> Set.toList)
              ChargeInstruction = fun currentWord site ->
                  let sourceInfo = source site
                  countInstruction trace currentWord site sourceInfo.SiteSpan (isAuthoredCoverageSite sourceInfo)
              RecordBranchOutcome = fun currentWord site outcome ->
                  let sourceInfo = source site
                  countBranchOutcome trace currentWord site outcome (isAuthoredCoverageSite sourceInfo)
              RecordUse = fun name -> log "use" name
              InvokeEffect = fun command ->
                  match command with
                  | ReadFile(operation, path) ->
                      mutateEffect trace "fs.read"
                      match trace.FileSystemMode with
                      | FileSystemMode.Virtual ->
                          match trace.FileSystem.TryFind path with
                          | Some contents -> EffectString contents
                          | None -> error "EFFECT_FILE_NOT_FOUND" $"Virtual file '{path}' does not exist." (Some operation) None [] [ path ]
                      | FileSystemMode.Real ->
                          match FileSystem.readText projectRoot operation path with
                          | Ok contents -> EffectString contents
                          | Error failure -> error failure.Code failure.Message (Some operation) None failure.Expected failure.Actual
                  | FileExists(operation, path) ->
                      mutateEffect trace "fs.read"
                      match trace.FileSystemMode with
                      | FileSystemMode.Virtual -> EffectBool(trace.FileSystem.ContainsKey path)
                      | FileSystemMode.Real ->
                          match FileSystem.fileExists projectRoot operation path with
                          | Ok exists -> EffectBool exists
                          | Error failure -> error failure.Code failure.Message (Some operation) None failure.Expected failure.Actual
                  | WriteFile(operation, path, contents) ->
                      mutateEffect trace "fs.write"
                      match trace.FileSystemMode with
                      | FileSystemMode.Virtual ->
                          trace.FileSystem <- Map.add path contents trace.FileSystem
                          EffectUnit
                      | FileSystemMode.Real ->
                          match FileSystem.writeText projectRoot operation path contents with
                          | Ok () -> EffectUnit
                          | Error failure -> error failure.Code failure.Message (Some operation) None failure.Expected failure.Actual
                  | ReadFixedClock _ -> mutateEffect trace "clock.read"; EffectString fixedClock
                  | WriteVirtualConsole(_, contents) ->
                      mutateEffect trace "console.write"
                      trace.Console <- trace.Console @ [ contents ]
                      EffectUnit
              EnterUserFunction = fun functionId revision decodeArguments ->
                  let isTarget = trace.TargetIdentity = Some(functionId, revision)
                  let isInTarget = trace.TargetDepth > 0 || isTarget
                  userFunctionScopes.Push isInTarget
                  if isTarget then
                      trace.TargetInvocationCount <- trace.TargetInvocationCount + 1
                      if finiteObservationPlan |> Option.exists fst then
                          trace.TargetInputs <- decodeArguments () :: trace.TargetInputs
                  if isInTarget then trace.TargetDepth <- trace.TargetDepth + 1
                  fun () ->
                      let wasInTarget = userFunctionScopes.Pop()
                      if wasInTarget then trace.TargetDepth <- trace.TargetDepth - 1
              ReturnUserFunction = fun functionId revision decodeResults ->
                  if trace.TargetIdentity = Some(functionId, revision) && (finiteObservationPlan |> Option.exists snd) then
                      trace.TargetReturns <- decodeResults () :: trace.TargetReturns
              WordDefinitionSpan = definitionSpan
              PrimitiveDefinitionSpan = definitionSpan }

        let executeIRBody (snapshot: RuntimeSnapshot) (executionName: string) (trace: Trace) (body: VerifiedIrBody) =
            let host = interpreterHost snapshot trace (Some body)
            IrInterpreter.executeBody host executionName body

        let executeExpression (snapshot: RuntimeSnapshot) (coverageTarget: string option) (traceFileSystemMode: FileSystemMode) (fileSystem: Map<string, string>) (expressions: Expr list) =
            Compiler.checkExpression (knownTypes snapshot.State) snapshot.Words expressions |> ignore
            let sourceOrigins = snapshot.FlowContext |> Option.map (fun flowContext -> flowContext.SourceOrigins) |> Option.defaultValue Map.empty
            let body = Compiler.compileIrBodyAgainstProgramWithSourceOrigins snapshot.Context snapshot.Program "<eval>" [] expressions sourceOrigins
            let trace = createTrace coverageTarget traceFileSystemMode false false fileSystem
            let stack = executeIRBody snapshot "<eval>" trace body
            stack, trace

        let activateRuntimeSnapshot (snapshot: RuntimeSnapshot) =
            data <- snapshot.State
            activeSnapshot <- Some snapshot

        let validateStoredProject (projectStore: Store) (manifest: ProjectManifest option) (manifestHash: string option) (projectSource: string option) =
            match manifest with
            | None ->
                let parsed = projectSource |> Option.map (parseProjectSource "dictionary.agent")
                let emptyState =
                    { data with
                        Words = Compiler.primitives
                        WordIds = Map.empty
                        Deprecated = Set.empty
                        Records = Map.empty
                        Scalars = Map.empty
                        Enums = Map.empty
                        TypeSources = Map.empty
                        Tests = Map.empty
                        Examples = Map.empty
                        History = Map.empty
                        FlowWords = Map.empty
                        FlowTests = Map.empty
                        FlowTestFiles = Map.empty
                        FlowExamples = Map.empty
                        FlowHistory = Map.empty
                        Replacements = Map.empty }
                let proposed =
                    match parsed with
                    | None -> emptyState
                    | Some source -> parsedState source emptyState Map.empty
                validateGraph proposed
                compileRuntimeSnapshot proposed
            | Some value ->
                let mismatch message word = error "STORAGE_PROJECT_MISMATCH" message word None [] []
                let headsById = value.Words |> List.map (fun head -> head.WordId, head) |> Map.ofList
                let headsByName = value.Words |> List.map (fun head -> head.CurrentName, head) |> Map.ofList
                if headsById.Count <> value.Words.Length || headsByName.Count <> value.Words.Length then
                    mismatch "Manifest word heads contain duplicate stable IDs or current names." None
                let hash = manifestHash |> Option.defaultWith (fun () -> error "STORAGE_INVALID_MANIFEST" "Manifest authority has no manifest hash." None None [] [])
                let records = ResizeArray<RecordDefinition>()
                let scalars = ResizeArray<ScalarTypeDefinition>()
                let enums = ResizeArray<EnumDefinition>()
                let loadedTypeSources = ResizeArray<string * AuthoredTypeSource>()
                let stackWords = ResizeArray<WordDefinition>()
                let stackTests = ResizeArray<TestDefinition>()
                let stackExamples = ResizeArray<ExampleDefinition>()
                let currentFlowWords = ResizeArray<FlowAuthoredWord * WordMaturity>()
                let currentFlowTests = ResizeArray<string * FlowAuthoredAttachment>()
                let currentFlowTestFiles = ResizeArray<string * FlowAuthoredTestFile>()
                let currentFlowExamples = ResizeArray<string * FlowAuthoredAttachment>()
                let mutable loadedHistory: Map<string, WordDefinition list> = Map.empty
                let mutable loadedFlowHistory: Map<string, FlowAuthoredWord list> = Map.empty

                for typeSource in value.Types do
                    match Storage.readSource projectStore typeSource.Definition with
                    | Error storageError -> raiseStorageError storageError
                    | Ok source ->
                        let authored =
                            match typeSource.SourceFormat.Frontend, typeSource.SourceFormat.Version with
                            | SourceFrontend.Stack, 1 ->
                                let parsed = parseProjectSource ($"<type:{typeSource.Name}>") source
                                match parsed.Records, parsed.Scalars, parsed.Enums with
                                | [ record ], [], [] when record.Name = typeSource.Name && parsed.Words.IsEmpty && parsed.Tests.IsEmpty && parsed.Examples.IsEmpty ->
                                    records.Add record
                                    { SourceFormat = typeSource.SourceFormat; Content = source; Reference = typeSource.Definition; ValidatorTarget = typeSource.ValidatorTarget }
                                | [], [ scalar ], [] when scalar.Name = typeSource.Name && parsed.Words.IsEmpty && parsed.Tests.IsEmpty && parsed.Examples.IsEmpty ->
                                    scalars.Add scalar
                                    { SourceFormat = typeSource.SourceFormat; Content = source; Reference = typeSource.Definition; ValidatorTarget = typeSource.ValidatorTarget }
                                | [], [], [] -> mismatch $"Manifest type object '{typeSource.Name}' does not contain exactly that type." (Some typeSource.Name)
                                | _ -> mismatch $"Manifest type object '{typeSource.Name}' does not contain exactly that type." (Some typeSource.Name)
                            | SourceFrontend.Flow, version when version = 1 || version = 2 ->
                                let file = $"<type:{typeSource.Name}/{typeSource.Definition.Hash}>"
                                let parsed =
                                    match FlowParser.parseDocumentWithVersion version file source with
                                    | Ok document -> document
                                    | Error diagnostic -> raise (LanguageException diagnostic)
                                match parsed.Records, parsed.Scalars, parsed.Enums with
                                | [ record ], [], [] when record.Name = typeSource.Name && parsed.Words.IsEmpty && parsed.Tests.IsEmpty && parsed.Examples.IsEmpty ->
                                    records.Add record
                                    { SourceFormat = typeSource.SourceFormat; Content = source; Reference = typeSource.Definition; ValidatorTarget = typeSource.ValidatorTarget }
                                | [], [ scalar ], [] when scalar.Name = typeSource.Name && parsed.Words.IsEmpty && parsed.Tests.IsEmpty && parsed.Examples.IsEmpty ->
                                    scalars.Add scalar
                                    { SourceFormat = typeSource.SourceFormat; Content = source; Reference = typeSource.Definition; ValidatorTarget = typeSource.ValidatorTarget }
                                | [], [], [ enumDefinition ] when version = 2 && enumDefinition.Name = typeSource.Name && parsed.Words.IsEmpty && parsed.Tests.IsEmpty && parsed.Examples.IsEmpty ->
                                    enums.Add enumDefinition
                                    { SourceFormat = typeSource.SourceFormat; Content = source; Reference = typeSource.Definition; ValidatorTarget = typeSource.ValidatorTarget }
                                | _ -> mismatch $"Manifest Flow type object '{typeSource.Name}' does not contain exactly that type." (Some typeSource.Name)
                            | frontend, version ->
                                error "RUNTIME_UNSUPPORTED_TYPE_FRONTEND" $"Stored type '{typeSource.Name}' uses unsupported {frontend}/{version}." (Some typeSource.Name) None [ "Stack/1"; "Flow/1"; "Flow/2" ] [ $"{frontend}/{version}" ]
                        loadedTypeSources.Add(typeSource.Name, authored)

                let validateSourceFormat (metadata: WordRevision) =
                    let frontendName = function SourceFrontend.Stack -> "Stack" | SourceFrontend.Flow -> "Flow"
                    match metadata.SourceFormat.Frontend, metadata.SourceFormat.Version with
                    | SourceFrontend.Stack, 1
                    | SourceFrontend.Flow, 1
                    | SourceFrontend.Flow, 2 -> ()
                    | frontend, version ->
                        error "RUNTIME_UNSUPPORTED_FRONTEND"
                            $"Stored revision '{metadata.WordId}/{metadata.Revision}' uses unsupported {frontendName frontend} syntax version {version}."
                            (Some metadata.Name) None [ "Stack/1"; "Flow/1"; "Flow/2" ] [ $"{frontendName frontend}/{version}" ]

                let attachmentSourceFormat (metadata: WordRevision) (reference: SourceRef) =
                    let sourceFormat =
                        match metadata.AttachmentSourceFormats.TryFind reference with
                        | Some sourceFormat -> sourceFormat
                        | None when value.FormatVersion < 5 -> metadata.SourceFormat
                        | None -> mismatch $"Version-5 revision '{metadata.WordId}/{metadata.Revision}' has no syntax metadata for attached source '{reference.Hash}'." (Some metadata.Name)
                    let isSupported =
                        match sourceFormat.Frontend, sourceFormat.Version with
                        | SourceFrontend.Stack, 1
                        | SourceFrontend.Flow, 1
                        | SourceFrontend.Flow, 2 -> true
                        | _ -> false
                    if not isSupported then
                        mismatch $"Attached source '{reference.Hash}' uses unsupported {sourceFormat.Frontend}/{sourceFormat.Version}." (Some metadata.Name)
                    if sourceFormat.Frontend <> metadata.SourceFormat.Frontend then
                        mismatch $"Attached source '{reference.Hash}' uses a different frontend from revision '{metadata.WordId}/{metadata.Revision}'." (Some metadata.Name)
                    sourceFormat

                let stackDefinition file source (metadata: WordRevision) =
                    let parsed = parseProjectSource file source
                    match parsed.Words, parsed.Records, parsed.Scalars, parsed.Tests, parsed.Examples with
                    | [ definition ], [], [], [], []
                        when definition.Name = metadata.Name
                             && definition.Revision = metadata.Revision
                             && definition.Maturity = metadata.Maturity -> definition
                    | _ -> mismatch $"Stored Stack revision {metadata.WordId}/{metadata.Revision} does not match its manifest metadata." (Some metadata.Name)

                let flowDefinition file source (metadata: WordRevision) =
                    match FlowParser.parseWordWithVersion metadata.SourceFormat.Version file source with
                    | Error diagnostic -> raise (LanguageException diagnostic)
                    | Ok definition when definition.Name = metadata.Name && definition.SyntaxVersion = metadata.SourceFormat.Version -> definition
                    | Ok definition -> mismatch $"Stored Flow revision {metadata.WordId}/{metadata.Revision} does not match its manifest owner or syntax version." (Some metadata.Name)

                let stackTest file source owner =
                    let parsed = parseProjectSource file source
                    match parsed.Tests, parsed.Words, parsed.Records, parsed.Scalars, parsed.Examples with
                    | [ test ], [], [], [], [] when test.Word = owner -> test
                    | _ -> mismatch $"Stored Stack test for '{owner}' is invalid or has a foreign owner." (Some owner)

                let flowTest syntaxVersion file source owner =
                    match FlowParser.parseTestWithVersion syntaxVersion file source with
                    | Error diagnostic -> raise (LanguageException diagnostic)
                    | Ok test when test.Word = owner && test.SyntaxVersion = syntaxVersion -> test
                    | Ok test -> mismatch $"Stored Flow test for '{owner}' has foreign owner '{test.Word}'." (Some owner)

                let stackExample file source owner =
                    let parsed = parseProjectSource file source
                    match parsed.Examples, parsed.Words, parsed.Records, parsed.Scalars, parsed.Tests with
                    | [ example ], [], [], [], [] when example.Word = owner -> example
                    | _ -> mismatch $"Stored Stack example for '{owner}' is invalid or has a foreign owner." (Some owner)

                let flowExample syntaxVersion file source owner =
                    match FlowParser.parseExampleWithVersion syntaxVersion file source with
                    | Error diagnostic -> raise (LanguageException diagnostic)
                    | Ok example when example.Word = owner && example.SyntaxVersion = syntaxVersion -> example
                    | Ok example -> mismatch $"Stored Flow example for '{owner}' has foreign owner '{example.Word}'." (Some owner)

                let bindingSubset source caseName roles (metadata: WordRevision) =
                    metadata.CallBindings
                    |> List.filter (fun binding ->
                        binding.Source = source
                        && binding.CaseName = caseName
                        && List.contains binding.BodyRole roles)

                let sortedRevisions = value.Revisions |> List.sortBy (fun revision -> revision.WordId, revision.Revision)
                for metadata in sortedRevisions do
                    validateSourceFormat metadata
                    let head =
                        headsById.TryFind metadata.WordId
                        |> Option.defaultWith (fun () -> mismatch $"Revision '{metadata.WordId}/{metadata.Revision}' has no word head." (Some metadata.Name))
                    if metadata.Revision > head.CurrentRevision then
                        mismatch $"Revision '{metadata.WordId}/{metadata.Revision}' is newer than its manifest head." (Some metadata.Name)
                    let revisionContent =
                        match Storage.readRevision projectStore hash metadata.WordId metadata.Revision with
                        | Error storageError -> raiseStorageError storageError
                        | Ok content -> content
                    if revisionContent.TestSources.Length <> metadata.Tests.Length
                       || revisionContent.ExampleSources.Length <> metadata.Examples.Length then
                        mismatch $"Stored attachment source count for '{metadata.Name}/{metadata.Revision}' does not match its revision metadata." (Some metadata.Name)
                    let sourceFile = $"<revision:{metadata.Name}/{metadata.Revision}>"
                    let isCurrent = metadata.Revision = head.CurrentRevision
                    if isCurrent && metadata.Name <> head.CurrentName then
                        mismatch $"Current revision name for '{metadata.WordId}' differs from its manifest head." (Some head.CurrentName)

                    let parsedDefinition =
                        match metadata.SourceFormat.Frontend with
                        | SourceFrontend.Stack -> Choice1Of2(stackDefinition sourceFile revisionContent.DefinitionSource metadata)
                        | SourceFrontend.Flow -> Choice2Of2(flowDefinition sourceFile revisionContent.DefinitionSource metadata)

                    let parsedTests =
                        List.zip metadata.Tests revisionContent.TestSources
                        |> List.map (fun (reference, source) ->
                            let file = $"{sourceFile}/test:{reference.Hash}"
                            let sourceFormat = attachmentSourceFormat metadata reference
                            match sourceFormat.Frontend with
                            | SourceFrontend.Stack -> Choice1Of3(stackTest file source metadata.Name)
                            | SourceFrontend.Flow ->
                                match FlowParser.parseTestWithVersion sourceFormat.Version file source with
                                | Ok test when test.Word = metadata.Name && test.SyntaxVersion = sourceFormat.Version -> Choice2Of3 test
                                | _ ->
                                    let wrapperFile = $"{sourceFile}/test-file:{reference.Hash}"
                                    match FlowParser.parseTestFileSettingsWithVersion sourceFormat.Version wrapperFile source with
                                    | Ok settings -> Choice3Of3 settings
                                    | Error diagnostic -> raise (LanguageException diagnostic))
                    let parsedExamples =
                        List.zip metadata.Examples revisionContent.ExampleSources
                        |> List.map (fun (reference, source) ->
                            let file = $"{sourceFile}/example:{reference.Hash}"
                            let sourceFormat = attachmentSourceFormat metadata reference
                            match sourceFormat.Frontend with
                            | SourceFrontend.Stack -> Choice1Of2(stackExample file source metadata.Name)
                            | SourceFrontend.Flow -> Choice2Of2(flowExample sourceFormat.Version file source metadata.Name))
                    let testNames =
                        parsedTests
                        |> List.collect (function
                            | Choice1Of3 test -> [ test.Name ]
                            | Choice2Of3 test -> [ test.CaseName ]
                            | Choice3Of3 settings -> settings.Tests |> List.map (fun test -> test.CaseName))
                    let exampleNames =
                        parsedExamples
                        |> List.map (function Choice1Of2 example -> example.Name | Choice2Of2 example -> example.CaseName)
                    if (testNames |> List.distinct).Length <> testNames.Length || (exampleNames |> List.distinct).Length <> exampleNames.Length then
                        mismatch $"Stored attachment case names for '{metadata.Name}/{metadata.Revision}' are not unique." (Some metadata.Name)

                    match parsedDefinition with
                    | Choice1Of2 definition ->
                        for parsed in parsedTests do
                            match parsed with
                            | Choice1Of3 test -> if isCurrent then stackTests.Add test
                            | _ -> mismatch "A Stack definition cannot own Flow test sources or test-file wrappers." (Some metadata.Name)
                        for parsed in parsedExamples do
                            match parsed with
                            | Choice1Of2 example -> if isCurrent then stackExamples.Add example
                            | _ -> mismatch "A Stack definition cannot own Flow example sources." (Some metadata.Name)
                        if isCurrent then stackWords.Add definition
                        let previous = loadedHistory.TryFind head.CurrentName |> Option.defaultValue []
                        loadedHistory <- Map.add head.CurrentName (previous @ [ definition ]) loadedHistory
                    | Choice2Of2 definition ->
                        let ownerId = WordId metadata.WordId
                        let sourceDocument: FlowLowering.FlowSourceDocument =
                            { OwnerName = metadata.Name
                              SyntaxVersion = metadata.SourceFormat.Version
                              EffectsDeclared = definition.EffectsDeclared
                              OwnerId = ownerId
                              OwnerRevision = metadata.Revision
                              Reference = metadata.Definition
                              SourceFile = sourceFile
                              Content = revisionContent.DefinitionSource }
                        let historyAuthored =
                            { Definition = definition
                              Source = sourceDocument
                              StoredBindings = Some(bindingSubset metadata.Definition None [ StoredCallBodyRole.Definition ] metadata) }
                        let previous = loadedFlowHistory.TryFind metadata.WordId |> Option.defaultValue []
                        loadedFlowHistory <- Map.add metadata.WordId (previous @ [ historyAuthored ]) loadedFlowHistory
                        if isCurrent then currentFlowWords.Add(historyAuthored, metadata.Maturity)
                        for index in 0 .. parsedTests.Length - 1 do
                            match parsedTests[index], metadata.Tests[index] with
                            | Choice2Of3 test, reference when isCurrent ->
                                let key = flowAttachmentKey ownerId test.CaseName
                                let attachmentFormat = attachmentSourceFormat metadata reference
                                let sourceDocument: FlowLowering.FlowAttachmentSourceDocument =
                                    { OwnerName = metadata.Name
                                      SyntaxVersion = attachmentFormat.Version
                                      OwnerId = ownerId
                                      OwnerRevision = metadata.Revision
                                      Kind = FlowLowering.FlowAttachmentKind.Test
                                      CaseName = test.CaseName
                                      Reference = reference
                                      SourceFile = $"{sourceFile}/test:{reference.Hash}"
                                      Content = revisionContent.TestSources[index] }
                                let authored =
                                    { Source = sourceDocument
                                      StoredBindings = Some(bindingSubset reference (Some test.CaseName) [ StoredCallBodyRole.Actual; StoredCallBodyRole.ExpectedExpression ] metadata) }
                                currentFlowTests.Add(key, authored)
                            | Choice3Of3 settings, reference when isCurrent ->
                                let wrapperFile = $"{sourceFile}/test-file:{reference.Hash}"
                                let wrapperSourceFormat = attachmentSourceFormat metadata reference
                                let localSettings =
                                    match FlowParser.parseTestFileSettingsWithVersion wrapperSourceFormat.Version wrapperFile revisionContent.TestSources[index] with
                                    | Ok parsed -> parsed
                                    | Error diagnostic -> raise (LanguageException diagnostic)
                                let owners = localSettings.Tests |> List.map (fun test -> test.Word) |> List.distinct
                                if owners <> [ metadata.Name ] then
                                    mismatch $"Stored Flow test-file wrapper for '{metadata.Name}' has a foreign owner." (Some metadata.Name)
                                let fileKey = flowTestFileKey ownerId reference
                                let authoredFile: FlowAuthoredTestFile =
                                    { OwnerName = metadata.Name
                                      OwnerId = ownerId
                                      OwnerRevision = metadata.Revision
                                      SyntaxVersion = wrapperSourceFormat.Version
                                      Reference = reference
                                      SourceFile = wrapperFile
                                      Settings = localSettings
                                      StoredBindings = Some(bindingSubset reference None [ StoredCallBodyRole.TestOverride ] metadata) }
                                currentFlowTestFiles.Add(fileKey, authoredFile)
                                for test in localSettings.Tests do
                                    let key = flowAttachmentKey ownerId test.CaseName
                                    let sourceDocument: FlowLowering.FlowAttachmentSourceDocument =
                                        { OwnerName = metadata.Name
                                          OwnerId = ownerId
                                          OwnerRevision = metadata.Revision
                                          SyntaxVersion = wrapperSourceFormat.Version
                                          Kind = FlowLowering.FlowAttachmentKind.Test
                                          CaseName = test.CaseName
                                          Reference = reference
                                          SourceFile = wrapperFile
                                          Content = revisionContent.TestSources[index] }
                                    let authored =
                                        { Source = sourceDocument
                                          StoredBindings = Some(bindingSubset reference (Some test.CaseName) [ StoredCallBodyRole.Actual; StoredCallBodyRole.ExpectedExpression ] metadata) }
                                    currentFlowTests.Add(key, authored)
                            | Choice3Of3 _, _ -> ()
                            | Choice2Of3 _, _ -> ()
                            | _ -> mismatch "A Flow definition cannot own Stack test sources." (Some metadata.Name)
                        for index in 0 .. parsedExamples.Length - 1 do
                            match parsedExamples[index], metadata.Examples[index] with
                            | Choice2Of2 example, reference when isCurrent ->
                                let key = flowAttachmentKey ownerId example.CaseName
                                let attachmentFormat = attachmentSourceFormat metadata reference
                                let sourceDocument: FlowLowering.FlowAttachmentSourceDocument =
                                    { OwnerName = metadata.Name
                                      SyntaxVersion = attachmentFormat.Version
                                      OwnerId = ownerId
                                      OwnerRevision = metadata.Revision
                                      Kind = FlowLowering.FlowAttachmentKind.Example
                                      CaseName = example.CaseName
                                      Reference = reference
                                      SourceFile = $"{sourceFile}/example:{reference.Hash}"
                                      Content = revisionContent.ExampleSources[index] }
                                let authored =
                                    { Source = sourceDocument
                                      StoredBindings = Some(bindingSubset reference (Some example.CaseName) [ StoredCallBodyRole.Actual ] metadata) }
                                currentFlowExamples.Add(key, authored)
                            | Choice2Of2 _, _ -> ()
                            | _ -> mismatch "A Flow definition cannot own Stack example sources." (Some metadata.Name)

                let identities = value.Words |> List.map (fun head -> head.CurrentName, head.WordId) |> Map.ofList
                let stackParsed: ParsedSource =
                    { Records = List.ofSeq records
                      Scalars = List.ofSeq scalars
                      Enums = List.ofSeq enums
                      Words = List.ofSeq stackWords
                      Tests = List.ofSeq stackTests
                      Examples = List.ofSeq stackExamples }
                let parsedStackState = parsedState stackParsed data identities
                let stackState = { parsedStackState with TypeSources = loadedTypeSources |> Map.ofSeq }
                // Generated record/scalar words do not have WordRevision
                // identities, so manifest revisions have nowhere to store
                // their attached Stack cases except in the hash-verified
                // project export. Recover only cases owned by generated type
                // words; user-word cases continue to come exclusively from
                // their revision objects and the final export comparison
                // rejects any unreferenced user cases. This also preserves v1
                // behavior, where the aggregate was the source of these cases.
                let supportsGeneratedProjectCases version = version >= 1 && version <= 6
                let generatedProjectCases =
                    match value.FormatVersion, projectSource with
                    | version, Some exact when supportsGeneratedProjectCases version ->
                        let stackSource =
                            if currentFlowWords.Count = 0
                               && (loadedTypeSources |> Seq.forall (fun (_, authored) -> authored.SourceFormat.Frontend = SourceFrontend.Stack)) then exact
                            else
                                let stackSections = ResizeArray<string>()
                                let currentSection = ResizeArray<string>()
                                let mutable isStackSection = false
                                let mutable skipSectionSeparator = false
                                let flushSection () =
                                    while currentSection.Count > 0 && String.IsNullOrWhiteSpace currentSection[currentSection.Count - 1] do
                                        currentSection.RemoveAt(currentSection.Count - 1)
                                    if isStackSection && currentSection.Count > 0 then
                                        stackSections.Add(String.Join("\n", currentSection))
                                    currentSection.Clear()
                                for line in exact.Replace("\r\n", "\n").Split('\n') do
                                    if line.StartsWith("// frontend: ", StringComparison.Ordinal) then
                                        flushSection ()
                                        isStackSection <- line = "// frontend: stack/1"
                                        skipSectionSeparator <- true
                                    elif isStackSection then
                                        if skipSectionSeparator && String.IsNullOrWhiteSpace line then
                                            skipSectionSeparator <- false
                                        else
                                            skipSectionSeparator <- false
                                            currentSection.Add line
                                flushSection ()
                                String.concat "\n\n" stackSections
                        Some(parseProjectSource "<manifest-stack-project-cases>" stackSource)
                    | _ -> None
                let generatedNames = makeGenerated stackState |> Map.toSeq |> Seq.map fst |> Set.ofSeq
                let stackState =
                    match generatedProjectCases with
                    | None -> stackState
                    | Some aggregate ->
                        let generatedTests = aggregate.Tests |> List.filter (fun test -> generatedNames.Contains test.Word)
                        let generatedExamples = aggregate.Examples |> List.filter (fun example -> generatedNames.Contains example.Word)
                        { stackState with
                            Tests = generatedTests |> List.fold addTest stackState.Tests
                            Examples = generatedExamples |> List.fold addExample stackState.Examples }
                if Set.ofList (value.Types |> List.map (fun item -> item.Name)) <> knownTypes stackState then
                    mismatch "Manifest type names do not match the authoritative type source objects." None
                for typeSource in value.Types do
                    let authored = stackState.TypeSources[typeSource.Name]
                    let expected =
                        match stackState.Records.TryFind typeSource.Name, stackState.Scalars.TryFind typeSource.Name, stackState.Enums.TryFind typeSource.Name with
                        | Some record, _, _ when typeSource.SourceFormat.Frontend = SourceFrontend.Flow -> FlowSource.renderRecordWithVersion typeSource.SourceFormat.Version record.Definition
                        | Some record, _, _ -> Source.renderRecord record.Definition
                        | _, Some scalar, _ when typeSource.SourceFormat.Frontend = SourceFrontend.Flow -> FlowSource.renderScalarWithVersion typeSource.SourceFormat.Version scalar.Definition
                        | _, Some scalar, _ -> Source.renderScalar scalar.Definition
                        | _, _, Some enumEntry -> FlowSource.renderEnumWithVersion typeSource.SourceFormat.Version enumEntry.Definition
                        | _ -> mismatch $"Manifest type '{typeSource.Name}' is not present in the loaded type source objects." (Some typeSource.Name)
                    if authored.Reference <> typeSource.Definition
                       || authored.SourceFormat <> typeSource.SourceFormat
                       || authored.ValidatorTarget <> typeSource.ValidatorTarget then
                        mismatch $"Manifest type metadata for '{typeSource.Name}' differs from its authoritative source object." (Some typeSource.Name)
                    let canonical =
                        match typeSource.SourceFormat.Frontend with
                        | SourceFrontend.Flow ->
                            let file = $"<type:{typeSource.Name}/{typeSource.Definition.Hash}>"
                            let parsed =
                                match FlowParser.parseDocumentWithVersion typeSource.SourceFormat.Version file authored.Content with
                                | Ok document -> document
                                | Error diagnostic -> raise (LanguageException diagnostic)
                            match parsed.Records, parsed.Scalars, parsed.Enums with
                            | [ record ], [], [] -> FlowSource.renderRecordWithVersion typeSource.SourceFormat.Version record
                            | [], [ scalar ], [] -> FlowSource.renderScalarWithVersion typeSource.SourceFormat.Version scalar
                            | [], [], [ enumDefinition ] when typeSource.SourceFormat.Version = 2 -> FlowSource.renderEnumWithVersion typeSource.SourceFormat.Version enumDefinition
                            | _ -> mismatch $"Manifest Flow type object '{typeSource.Name}' does not contain exactly that type." (Some typeSource.Name)
                        | SourceFrontend.Stack ->
                            let parsed = parseProjectSource ($"<type:{typeSource.Name}>") authored.Content
                            match parsed.Records, parsed.Scalars, parsed.Enums with
                            | [ record ], [], [] -> Source.renderRecord record
                            | [], [ scalar ], [] -> Source.renderScalar scalar
                            | _ -> mismatch $"Manifest type object '{typeSource.Name}' does not contain exactly that type." (Some typeSource.Name)
                    if canonical <> expected then mismatch $"Manifest type '{typeSource.Name}' differs from its source object." (Some typeSource.Name)

                let flowWords = currentFlowWords |> Seq.map (fun (authored, _) -> wordIdText authored.Source.OwnerId, authored) |> Map.ofSeq
                let flowTests = currentFlowTests |> Map.ofSeq
                let flowTestFiles = currentFlowTestFiles |> Map.ofSeq
                let flowExamples = currentFlowExamples |> Map.ofSeq
                let orphanCases =
                    (flowTests |> Map.toSeq |> Seq.exists (fun (_, item) -> not (flowWords.ContainsKey(wordIdText item.Source.OwnerId))))
                    || (flowExamples |> Map.toSeq |> Seq.exists (fun (_, item) -> not (flowWords.ContainsKey(wordIdText item.Source.OwnerId))))
                if orphanCases then mismatch "Stored Flow attachments have no current Flow-authored owner revision." None
                let flowWordEntries =
                    currentFlowWords
                    |> Seq.map (fun (authored, maturity) ->
                        let definition = authored.Definition
                        let projection: WordDefinition =
                            { Name = definition.Name
                              Inputs = definition.Parameters |> List.map (fun parameter -> parameter.Type)
                              Outputs = definition.Outputs
                              Effects = definition.Effects
                              Maturity = maturity
                              Revision = authored.Source.OwnerRevision
                              Documentation = definition.Documentation
                              Body = []
                              SourceText = authored.Source.Content
                              Span = definition.Span }
                        definition.Name, (entry projection None Persistent maturity authored.Source.OwnerRevision, authored.Source.OwnerId))
                    |> Map.ofSeq
                let words =
                    flowWordEntries
                    |> Map.fold (fun found name (word, _) -> Map.add name word found) stackState.Words
                let wordIds =
                    flowWordEntries
                    |> Map.fold (fun found name (_, identity) -> Map.add name (wordIdText identity) found) stackState.WordIds
                let deprecated = value.Words |> List.filter (fun head -> head.Deprecated) |> List.map (fun head -> head.CurrentName) |> Set.ofList
                let proposed =
                    { stackState with
                        Words = words
                        WordIds = wordIds
                        Deprecated = deprecated
                        History = loadedHistory
                        FlowWords = flowWords
                        FlowTests = flowTests
                        FlowTestFiles = flowTestFiles
                        FlowExamples = flowExamples
                        FlowHistory = loadedFlowHistory
                        Replacements = Map.empty }
                validateTypeSourceMetadata proposed
                if proposed.Words.Count - Compiler.primitives.Count <> value.Words.Length then
                    mismatch "Manifest word heads do not match the revision-authored word sources." None
                for head in value.Words do
                    match proposed.Words.TryFind head.CurrentName with
                    | Some word when word.Definition.Revision = head.CurrentRevision && proposed.WordIds.TryFind head.CurrentName = Some head.WordId ->
                        let metadata = value.Revisions |> List.tryFind (fun item -> item.WordId = head.WordId && item.Revision = head.CurrentRevision)
                        match metadata with
                        | Some revision when revision.Maturity = word.Maturity && revision.Deprecated = head.Deprecated -> ()
                        | _ -> mismatch $"Current word metadata for '{head.CurrentName}' differs from its manifest revision." (Some head.CurrentName)
                    | _ -> mismatch $"Manifest head '{head.CurrentName}' does not match its revision-authored word source." (Some head.CurrentName)
                let executable = compileRuntimeSnapshot proposed
                let exportText = sourceFor executable.State
                match projectSource with
                | Some exact when value.FormatVersion >= 2 && exact = exportText -> ()
                | Some exact when value.FormatVersion = 1 ->
                    let aggregate = parseProjectSource "dictionary.agent" exact
                    let rendered (source: ParsedSource) =
                        [ yield! source.Records |> List.map Source.renderRecord
                          yield! source.Scalars |> List.map Source.renderScalar
                          yield! source.Words |> List.map (Source.renderWord true)
                          yield! source.Tests |> List.map Source.renderTest
                          yield! source.Examples |> List.map Source.renderExample ]
                        |> Set.ofList
                    let manifestBacked = parseProjectSource "<manifest-backed-v1>" exportText
                    if rendered aggregate <> rendered manifestBacked then
                        mismatch "The v1 project export differs semantically from its manifest-backed Stack source objects." None
                | Some _ -> mismatch "The durable project export does not match its manifest-backed source objects." None
                | None -> mismatch "Manifest authority has no project export source." None
                executable

        let loadProject () =
            match store with
            | None -> pendingLoadedSnapshot <- Some(compileRuntimeSnapshot data)
            | Some projectStore ->
                match Storage.load projectStore with
                | Error storageError -> raiseStorageError storageError
                | Ok loaded ->
                    let executable = validateStoredProject projectStore loaded.Manifest loaded.ManifestHash loaded.ProjectSource
                    pendingLoadedSnapshot <- Some executable
                    storageGeneration <- loaded.Generation
                    storageAuthority <- loaded.Authority
                    currentManifest <- loaded.Manifest
                    currentManifestHash <- loaded.ManifestHash
                    lastExportWarning <- loaded.ExportWarning

        let toJsonValue value = jsonNode (Types.formatValue value)

        let completeEffectCounts (counts: Map<string, int>) =
            EffectCountAssertion.observableEffects
            |> Set.fold (fun completed name -> Map.add name (counts.TryFind name |> Option.defaultValue 0) completed) Map.empty

        let formatEffectCounts (counts: Map<string, int>) =
            counts
            |> Map.toList
            |> List.map (fun (name, count) -> $"{name}:{count}")
            |> String.concat ","

        let checkedByTest (snapshot: RuntimeSnapshot) (test: TestDefinition) =
            let sites, branches = coverageObligations snapshot test.Word
            let trace = createTrace (Some test.Word) FileSystemMode.Virtual true false Map.empty
            let bodyKey = $"{test.Word}/{test.Name}"
            let testFileOverlay = snapshot.TestFileOverlays.TryFind bodyKey
            let executionSnapshot =
                testFileOverlay
                |> Option.map (fun overlay -> { snapshot with Program = overlay.Program })
                |> Option.defaultValue snapshot
            let executeTestBody executionName bodyTrace verifiedBody =
                match testFileOverlay with
                | Some overlay ->
                    let host = interpreterHost executionSnapshot bodyTrace (Some verifiedBody)
                    use result = IrInterpreter.executeBodyWithTestDispatch host executionName snapshot.Program overlay.Dispatch verifiedBody None []
                    result.Decode()
                | None -> executeIRBody snapshot executionName bodyTrace verifiedBody
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
                  EffectAssertion = None
                  ActiveOverrides = testFileOverlay |> Option.map (fun overlay -> overlay.ActiveOverrides) |> Option.defaultValue []
                  TargetInputs = trace.TargetInputs
                  TargetReturns = trace.TargetReturns
                  TargetInvocationCount = trace.TargetInvocationCount
                  Instructions = trace.CoverageInstructions |> Set.intersect sites
                  BranchOutcomes = trace.CoverageBranches |> Set.intersect branches }
            let targetIdentity = snapshot.State.WordIds.TryFind test.Word |> Option.map WordId
            match targetIdentity, snapshot.Words.TryFind test.Word with
            | Some identity, Some entry when entry.Builtin.IsNone && entry.Status <> Primitive ->
                trace.TargetIdentity <- Some(identity, entry.Revision)
            | _ -> trace.TargetIdentity <- None
            let executionResult =
                try
                    let stack = executeTestBody "<test>" trace body
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
                        let expectedTrace = createTrace None FileSystemMode.Virtual true false Map.empty
                        try
                            let expectedStack = executeTestBody "<test-expectation>" expectedTrace expectedBody
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
                    | ExpectedRuntimeError expectedCode when diagnostic.Code = expectedCode ->
                        makeResult true None [] None
                    | _ -> makeResult false (Some diagnostic) [] None
            match test.EffectAssertion with
            | None -> executionResult
            | Some assertion ->
                let expected = completeEffectCounts assertion.Counts
                let actual = completeEffectCounts trace.TargetEffects
                let matched = trace.TargetInvocationCount > 0 && expected = actual
                let effectError =
                    if matched then None
                    else
                        Some
                            { Code = "TEST_EFFECT_ASSERTION_FAILED"
                              Message =
                                if trace.TargetInvocationCount = 0 then "The effect-count assertion target was never invoked."
                                else "Target-scoped provider effect counts did not match the exact assertion."
                              Word = Some test.Word
                              Span = Some assertion.Span
                              Expected = [ "effects{" + formatEffectCounts expected + "}"; "target invocations>=1" ]
                              Actual = [ "effects{" + formatEffectCounts actual + "}"; $"target invocations={trace.TargetInvocationCount}" ] }
                { executionResult with
                    Passed = executionResult.Passed && matched
                    EffectAssertion =
                        Some
                            { Span = assertion.Span
                              Expected = expected
                              Actual = actual
                              TargetInvocationCount = trace.TargetInvocationCount
                              Error = effectError } }

        let resultJson (snapshot: RuntimeSnapshot) (result: TestCaseResult) =
            let node = JsonObject()
            node["name"] <- jstr result.Name
            node["word"] <- jstr result.Word
            node["passed"] <- jbool result.Passed
            if not result.ActiveOverrides.IsEmpty then
                node["activeOverrides"] <- jsonNode result.ActiveOverrides
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
            match result.EffectAssertion with
            | Some observation ->
                let effectNode = JsonObject()
                effectNode["passed"] <- jbool observation.Error.IsNone
                let countsNode (counts: Map<string, int>) =
                    let values = JsonObject()
                    for name, count in Map.toList counts do values[name] <- jint count
                    values
                effectNode["expected"] <- countsNode observation.Expected
                effectNode["actual"] <- countsNode observation.Actual
                effectNode["targetInvocationCount"] <- jint observation.TargetInvocationCount
                let spanNode = JsonObject()
                spanNode["file"] <- jstr observation.Span.File
                spanNode["line"] <- jint observation.Span.Line
                spanNode["column"] <- jint observation.Span.Column
                spanNode["length"] <- jint observation.Span.Length
                effectNode["span"] <- spanNode
                match observation.Error with
                | Some diagnostic ->
                    let errorNode = JsonObject()
                    errorNode["code"] <- jstr diagnostic.Code
                    errorNode["message"] <- jstr diagnostic.Message
                    effectNode["error"] <- errorNode
                | None -> ()
                node["effectAssertion"] <- effectNode
            | None -> ()
            let primaryError =
                result.Error
                |> Option.orElseWith (fun () -> result.EffectAssertion |> Option.bind (fun observation -> observation.Error))
            match primaryError with
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

        let requalifyDurableLibraries (snapshot: RuntimeSnapshot) =
            snapshot.State.Words
            |> Map.toList
            |> List.filter (fun (_, entry) -> entry.Builtin.IsNone && entry.Status = Persistent && entry.Maturity = LibraryWord)
            |> List.sortBy fst
            |> List.iter (fun (name, _) ->
                rejectUnqualifiedLibraryDependencies snapshot.Words name
                let tests = runTestsFor snapshot (Some name)
                preflightStructuredTestResults snapshot.Program tests
                let failed = tests |> List.filter (fun result -> not result.Passed)
                if not failed.IsEmpty then
                    error "LIBRARY_REQUALIFICATION_FAILED" $"Durable library word '{name}' failed one or more current attached tests during load." (Some name) None
                        [ "all attached tests pass against the loaded dictionary" ] (failed |> List.map (fun result -> result.Name))
                requireLibraryCoverage snapshot name tests)

        do
            loadProject ()
            let snapshot =
                pendingLoadedSnapshot
                |> Option.defaultWith (fun () -> error "RUNTIME_SNAPSHOT_UNAVAILABLE" "Project loading did not produce an executable snapshot." None None [] [])
            requalifyDurableLibraries snapshot
            activateRuntimeSnapshot snapshot
            pendingLoadedSnapshot <- None

        let runExamplesFor (snapshot: RuntimeSnapshot) target caseName =
            let examples =
                snapshot.State.Examples
                |> Map.toList
                |> List.map snd
                |> List.filter (fun example -> target |> Option.forall ((=) example.Word))
                |> List.filter (fun example -> caseName |> Option.forall ((=) example.Name))
                |> List.sortBy (fun example -> example.Word, example.Name)
            examples
            |> List.map (fun example ->
                let key = example.Word + "/" + example.Name
                let result = JsonObject()
                result["word"] <- jstr example.Word
                result["name"] <- jstr example.Name
                let authoredSource =
                    snapshot.State.WordIds.TryFind example.Word
                    |> Option.map WordId
                    |> Option.bind (fun ownerId -> snapshot.State.FlowExamples.TryFind(flowAttachmentKey ownerId example.Name))
                    |> Option.map (fun authored -> authored.Source.Content)
                    |> Option.defaultValue example.SourceText
                result["source"] <- jstr authoredSource
                result["expected"] <- toJsonValue (Types.literalValue example.Expected)
                try
                    let body =
                        snapshot.ExampleBodies.TryFind key
                        |> Option.defaultWith (fun () -> error "EXAMPLE_BODY_MISSING" "The example has no body compiled against the active verified program." (Some key) (Some example.Span) [] [])
                    let trace = createTrace (Some example.Word) engineFileSystemMode false true virtualFiles
                    let actual = executeIRBody snapshot key trace body
                    let passed = actual = [ Types.literalValue example.Expected ]
                    result["passed"] <- jbool passed
                    result["actual"] <- jsonNode (actual |> List.map Types.formatValue)
                    if not passed then
                        result["errorCode"] <- jstr "EXAMPLE_ASSERTION_FAILED"
                        result["message"] <- jstr "Actual result did not equal the example's declared value."
                with
                | LanguageException diagnostic ->
                    result["passed"] <- jbool false
                    result["actual"] <- jsonNode ([]: string list)
                    result["errorCode"] <- jstr diagnostic.Code
                    result["message"] <- jstr diagnostic.Message
                result)

        let testSourceRows (snapshot: RuntimeSnapshot) (tests: TestDefinition list) =
            let testSourceRow (test: TestDefinition) =
                let flowAttachment =
                    snapshot.State.WordIds.TryFind test.Word
                    |> Option.map WordId
                    |> Option.bind (fun ownerId -> snapshot.State.FlowTests.TryFind(flowAttachmentKey ownerId test.Name))
                let frontend, syntaxVersion, source, testFile =
                    match flowAttachment with
                    | Some attachment ->
                        let file = snapshot.State.FlowTestFiles.TryFind(flowTestFileKey attachment.Source.OwnerId attachment.Source.Reference)
                        let source =
                            match file with
                            | Some authored ->
                                authored.Settings.Tests
                                |> List.tryFind (fun item -> item.CaseName = test.Name)
                                |> Option.map (fun item -> item.SourceText)
                                |> Option.defaultWith (fun () ->
                                    error "FLOW_RUNTIME_TEST_SOURCE_MISSING" $"Flow test-file '{authored.Settings.ScopeName}' has no authored source for case '{test.Word}/{test.Name}'." (Some(test.Word + "/" + test.Name)) None
                                        [ "matching nested test source" ] [ "missing" ])
                            | None -> attachment.Source.Content
                        "flow", attachment.Source.SyntaxVersion, source, file
                    | None -> "stack", 1, test.SourceText, None
                let row = JsonObject()
                row["word"] <- jstr test.Word
                row["name"] <- jstr test.Name
                row["source"] <- jstr source
                row["sourceHash"] <- jstr (Storage.sourceObject StorageObjectKind.TestDefinition source).Reference.Hash
                row["frontend"] <- jstr frontend
                row["syntaxVersion"] <- jint syntaxVersion
                testFile
                |> Option.iter (fun file ->
                    let fileSource = file.Settings.SourceText
                    let fileNode = JsonObject()
                    fileNode["scope"] <- jstr file.Settings.ScopeName
                    fileNode["source"] <- jstr fileSource
                    fileNode["sourceHash"] <- jstr (Storage.sourceObject StorageObjectKind.TestDefinition fileSource).Reference.Hash
                    fileNode["overrides"] <-
                        jsonNode
                            (file.Settings.Overrides
                             |> List.map (fun item ->
                                 {| name = item.Definition.Name
                                    source = item.SourceText |}))
                    row["testFile"] <- fileNode)
                row
            tests |> List.map testSourceRow

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
            let isUserFunction =
                snapshot.Words.TryFind word
                |> Option.exists (fun entry -> entry.Builtin.IsNone && entry.Status <> Primitive)
            if isUserFunction then
                node["finiteCoverage"] <- finiteCoverageJson snapshot word results
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
            let restoreFlowOwner (current: DictionaryState) ownerId (flowWord: FlowAuthoredWord option) (flowTests: Map<string, FlowAuthoredAttachment>) (flowTestFiles: Map<string, FlowAuthoredTestFile>) (flowExamples: Map<string, FlowAuthoredAttachment>) =
                let withoutFlowWord = current.FlowWords |> Map.filter (fun _ authored -> authored.Source.OwnerId <> ownerId)
                let restoredFlowWords =
                    match flowWord with
                    | Some authored when authored.Source.OwnerId = ownerId -> Map.add (wordIdText ownerId) authored withoutFlowWord
                    | _ -> withoutFlowWord
                let restoreAttachments (existing: Map<string, FlowAuthoredAttachment>) (saved: Map<string, FlowAuthoredAttachment>) =
                    existing
                    |> Map.filter (fun _ attachment -> attachment.Source.OwnerId <> ownerId)
                    |> fun remaining ->
                        saved
                        |> Map.filter (fun _ attachment -> attachment.Source.OwnerId = ownerId)
                        |> Map.fold (fun found key attachment -> Map.add key attachment found) remaining
                let restoreTestFiles (existing: Map<string, FlowAuthoredTestFile>) (saved: Map<string, FlowAuthoredTestFile>) =
                    existing
                    |> Map.filter (fun _ file -> file.OwnerId <> ownerId)
                    |> fun remaining ->
                        saved
                        |> Map.filter (fun _ file -> file.OwnerId = ownerId)
                        |> Map.fold (fun found key file -> Map.add key file found) remaining
                { current with
                    FlowWords = restoredFlowWords
                    FlowTests = restoreAttachments current.FlowTests flowTests
                    FlowTestFiles = restoreTestFiles current.FlowTestFiles flowTestFiles
                    FlowExamples = restoreAttachments current.FlowExamples flowExamples }
            for name in temporaryNames do
                let ownerId = proposed.WordIds.TryFind name |> Option.map WordId
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
                    match ownerId with
                    | Some identity ->
                        proposed <-
                            restoreFlowOwner
                                proposed
                                identity
                                (task.Snapshot.FlowWords.TryFind(wordIdText identity))
                                task.Snapshot.FlowTests
                                task.Snapshot.FlowTestFiles
                                task.Snapshot.FlowExamples
                    | None -> ()
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
                        match ownerId with
                        | Some identity ->
                            proposed <- restoreFlowOwner proposed identity backup.FlowWord backup.FlowTests backup.FlowTestFiles backup.FlowExamples
                        | None -> ()
                    | None ->
                        proposed <-
                            { proposed with
                                Words = Map.remove name proposed.Words
                                WordIds = Map.remove name proposed.WordIds
                                Tests = proposed.Tests |> Map.filter (fun _ test -> test.Word <> name)
                                Examples = proposed.Examples |> Map.filter (fun _ example -> example.Word <> name) }
                        match ownerId with
                        | Some identity -> proposed <- restoreFlowOwner proposed identity None Map.empty Map.empty Map.empty
                        | None -> ()
            let retainedFlowOwnerIds =
                proposed.Words
                |> Map.toSeq
                |> Seq.choose (fun (name, item) ->
                    if item.Builtin.IsSome then None
                    else proposed.WordIds.TryFind name |> Option.map WordId)
                |> Set.ofSeq
            proposed <-
                { proposed with
                    FlowWords = proposed.FlowWords |> Map.filter (fun _ authored -> retainedFlowOwnerIds.Contains authored.Source.OwnerId)
                    FlowTests = proposed.FlowTests |> Map.filter (fun _ attachment -> retainedFlowOwnerIds.Contains attachment.Source.OwnerId)
                    FlowTestFiles = proposed.FlowTestFiles |> Map.filter (fun _ file -> retainedFlowOwnerIds.Contains file.OwnerId)
                    FlowExamples = proposed.FlowExamples |> Map.filter (fun _ attachment -> retainedFlowOwnerIds.Contains attachment.Source.OwnerId)
                    FlowHistory =
                        proposed.FlowHistory
                        |> Map.filter (fun identity revisions ->
                            retainedFlowOwnerIds.Contains (WordId identity)
                            && (revisions |> List.forall (fun authored -> wordIdText authored.Source.OwnerId = identity)) ) }
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
                let dependenciesOf wordName =
                    match words.TryFind wordName with
                    | None -> Set.empty
                    | Some value ->
                        match value.Builtin with
                        | Some(RecordConstructor record) -> recordDefinitions state |> Map.tryFind record |> Option.bind (fun item -> item.Validator) |> Option.map Set.singleton |> Option.defaultValue Set.empty
                        | Some(ScalarConstructor scalar) -> scalarDefinitions state |> Map.tryFind scalar |> Option.bind (fun item -> item.Validator) |> Option.map Set.singleton |> Option.defaultValue Set.empty
                        | Some _ -> Set.empty
                        | None -> Compiler.dependencies value.Definition.Body
                let direct = dependenciesOf name
                let transitive =
                    let rec walk (pending: Set<string>) (found: Set<string>) =
                        match pending |> Set.toList with
                        | [] -> found
                        | head :: tail when found.Contains head -> walk (Set.ofList tail) found
                        | head :: tail ->
                            let next = dependenciesOf head
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
                let flowReference, flowReferenceUnavailableReason = FlowParser.describeCallReference name
                obj["flowReferenceSyntaxVersion"] <- jint 2
                match flowReference, flowReferenceUnavailableReason with
                | Some reference, _ ->
                    obj["flowReference"] <- jstr reference
                    obj["flowReferenceUnavailableReason"] <- null
                | None, Some reason ->
                    obj["flowReference"] <- null
                    obj["flowReferenceUnavailableReason"] <- jstr reason
                | None, None ->
                    obj["flowReference"] <- null
                    obj["flowReferenceUnavailableReason"] <- jstr "The exact Flow call could not be determined."
                obj["inputs"] <- jsonNode (definition.Inputs |> List.map Types.format)
                obj["outputs"] <- jsonNode (definition.Outputs |> List.map Types.format)
                let parameters =
                    state.WordIds.TryFind name
                    |> Option.bind (fun identity -> state.FlowWords.TryFind identity)
                    |> Option.map (fun authored -> authored.Definition.Parameters)
                    |> Option.defaultValue []
                let parameterArray = JsonArray()
                parameters
                |> List.iter (fun parameter ->
                    let value = JsonObject()
                    value["name"] <- jstr parameter.Name
                    value["type"] <- jstr (Types.format parameter.Type)
                    parameterArray.Add value)
                obj["parameters"] <- parameterArray
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
                    let next =
                        match words.TryFind head with
                        | None -> Set.empty
                        | Some entry ->
                            match entry.Builtin with
                            | None -> Compiler.dependencies entry.Definition.Body
                            | Some(RecordConstructor recordName) -> recordDefinitions state |> Map.tryFind recordName |> Option.bind (fun record -> record.Validator) |> Option.map Set.singleton |> Option.defaultValue Set.empty
                            | Some(ScalarConstructor scalarName) -> scalarDefinitions state |> Map.tryFind scalarName |> Option.bind (fun scalar -> scalar.Validator) |> Option.map Set.singleton |> Option.defaultValue Set.empty
                            | Some _ -> Set.empty
                    reach (Set.union (Set.ofList rest) next) (Set.add head found)
            seq {
                for KeyValue(_, record) in state.Records do
                    if record.Status = Persistent then
                        match record.Definition.Validator with
                        | Some validator -> yield reach (Set.singleton validator) Set.empty
                        | None -> ()
                for KeyValue(_, scalar) in state.Scalars do
                    if scalar.Status = Persistent then
                        match scalar.Definition.Validator with
                        | Some validator -> yield reach (Set.singleton validator) Set.empty
                        | None -> () }
            |> Seq.fold Set.union Set.empty

        let registerParsed (parsed: ParsedSource) (temporary: bool) =
            let old = data
            for definition in parsed.Words do
                let isFlowOwner =
                    old.WordIds.TryFind definition.Name
                    |> Option.exists (fun identity -> old.FlowWords.ContainsKey identity)
                if isFlowOwner then
                    error "RUNTIME_FLOW_FRONTEND_CHANGE_REQUIRES_FLOW" "A Flow-authored word cannot be rewritten through the Stack parser; select frontend 'flow' explicitly." (Some definition.Name) (Some definition.Span) [ "frontend: flow" ] [ "frontend: stack" ]
            for name in (parsed.Tests |> List.map (fun test -> test.Word)) @ (parsed.Examples |> List.map (fun example -> example.Word)) do
                let isFlowOwner =
                    old.WordIds.TryFind name
                    |> Option.exists (fun identity -> old.FlowWords.ContainsKey identity)
                if isFlowOwner then
                    error "RUNTIME_FLOW_FRONTEND_CHANGE_REQUIRES_FLOW" "Stack test/example source cannot be attached to a Flow-authored owner; use the explicit Flow frontend." (Some name) None [ "frontend: flow" ] [ "frontend: stack" ]
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
                            let ownerId = old.WordIds.TryFind definition.Name |> Option.map WordId
                            let originalTests = old.Tests |> Map.filter (fun _ test -> test.Word = definition.Name)
                            let originalExamples = old.Examples |> Map.filter (fun _ example -> example.Word = definition.Name)
                            let originalFlowWords =
                                ownerId
                                |> Option.bind (fun identity -> old.FlowWords.TryFind(wordIdText identity))
                            let originalFlowTests =
                                old.FlowTests
                                |> Map.filter (fun _ attachment -> ownerId |> Option.exists (fun identity -> attachment.Source.OwnerId = identity))
                            let originalFlowExamples =
                                old.FlowExamples
                                |> Map.filter (fun _ attachment -> ownerId |> Option.exists (fun identity -> attachment.Source.OwnerId = identity))
                            Map.add definition.Name
                                { Word = value
                                  Tests = originalTests
                                  Examples = originalExamples
                                  FlowWord = originalFlowWords
                                  FlowTests = originalFlowTests
                                  FlowTestFiles = old.FlowTestFiles |> Map.filter (fun _ file -> ownerId |> Option.exists (fun identity -> file.OwnerId = identity))
                                  FlowExamples = originalFlowExamples }
                                backups
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
                            let ownerId = old.WordIds.TryFind name |> Option.map WordId
                            { Word = item
                              Tests = old.Tests |> Map.filter (fun _ test -> test.Word = name)
                              Examples = old.Examples |> Map.filter (fun _ example -> example.Word = name)
                              FlowWord = ownerId |> Option.bind (fun identity -> old.FlowWords.TryFind(wordIdText identity))
                              FlowTests = old.FlowTests |> Map.filter (fun _ attachment -> ownerId |> Option.exists (fun identity -> attachment.Source.OwnerId = identity))
                              FlowTestFiles = old.FlowTestFiles |> Map.filter (fun _ file -> ownerId |> Option.exists (fun identity -> file.OwnerId = identity))
                              FlowExamples = old.FlowExamples |> Map.filter (fun _ attachment -> ownerId |> Option.exists (fun identity -> attachment.Source.OwnerId = identity)) })
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
            for definition in parsed.Words do
                if old.Words.TryFind definition.Name |> Option.exists (fun prior -> prior.Maturity = LibraryWord) then
                    rejectUnqualifiedLibraryDependencies executable.Words definition.Name
            let frozen = frozenValidatorWords old (effectiveWords old)
            let changed = parsed.Words |> List.map (fun word -> word.Name) |> Set.ofList
            let conflict = Set.intersect frozen changed
            if not (Set.isEmpty conflict) then
                error "TYPE_VALIDATOR_FROZEN" $"Cannot replace validator dependency '{Set.minElement conflict}' while a nominal type is persistent." None None [] (Set.toList conflict)
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
            let candidateEnums = data.Enums |> Map.filter (fun _ value -> value.Status = Candidate)
            let candidateTypes =
                Set.unionMany [ candidateRecords |> Map.toSeq |> Seq.map fst |> Set.ofSeq
                                candidateScalars |> Map.toSeq |> Seq.map fst |> Set.ofSeq
                                candidateEnums |> Map.toSeq |> Seq.map fst |> Set.ofSeq ]
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
                | Some { Builtin = Some(EnumCaseConstructor(owner, _)) } -> Some owner
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
                        match candidateRecords.TryFind name, candidateScalars.TryFind name, candidateEnums.TryFind name with
                        | Some record, _, _ -> record.Definition.Fields |> List.map (fun field -> namedTypes field.Type) |> Set.unionMany
                        | _, Some scalar, _ -> namedTypes scalar.Definition.BaseType
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

            let includeStagedReplacementCallers () =
                includeReplacementCallers
                || (data.Replacements |> Map.exists (fun name _ -> selectedWords.Contains name))

            if includeStagedReplacementCallers () then
                let mutable callerSearch = selectedWords
                let mutable addedCallers = true
                while addedCallers do
                    let stagedCallers =
                        data.Replacements
                        |> Map.toSeq
                        |> Seq.choose (fun (name, backup) ->
                            let priorDependsOn = not (Set.isEmpty (Set.intersect callerSearch (Compiler.dependencies backup.Word.Definition.Body)))
                            let stagedDependsOn =
                                candidateWords.TryFind name
                                |> Option.exists (fun candidate -> not (Set.isEmpty (Set.intersect callerSearch (Compiler.dependencies candidate.Definition.Body))))
                            if candidateWords.ContainsKey name
                               && not (selectedWords.Contains name)
                               && backup.Word.Status = Persistent
                               && (priorDependsOn || stagedDependsOn) then Some name
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
                    |> List.choose (fun name ->
                        match candidateRecords.TryFind name, candidateScalars.TryFind name with
                        | Some value, _ -> value.Definition.Validator
                        | _, Some value -> value.Definition.Validator
                        | _ -> None)
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
                    if includeStagedReplacementCallers () then
                        let callerRoots =
                            data.Replacements
                            |> Map.toSeq
                            |> Seq.choose (fun (name, backup) ->
                                let priorDependsOn = not (Set.isEmpty (Set.intersect selectedWords (Compiler.dependencies backup.Word.Definition.Body)))
                                let stagedDependsOn =
                                    candidateWords.TryFind name
                                    |> Option.exists (fun candidate -> not (Set.isEmpty (Set.intersect selectedWords (Compiler.dependencies candidate.Definition.Body))))
                                if candidateWords.ContainsKey name
                                   && not (selectedWords.Contains name)
                                   && backup.Word.Status = Persistent
                                   && (priorDependsOn || stagedDependsOn) then Some name
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
            let proposedEnums = data.Enums |> Map.map (fun name value -> if selectedTypes.Contains name then { value with Status = Persistent } else value)
            let proposed =
                { data with
                    Words = proposedWords
                    Records = proposedRecords
                    Scalars = proposedScalars
                    Enums = proposedEnums
                    Replacements = data.Replacements |> Map.filter (fun name _ -> not (selectedWords.Contains name)) }
            for name in selectedWords do
                rejectUnqualifiedLibraryDependencies (effectiveWords proposed) name
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
            for caller in callers do
                if not (selectedWords.Contains caller)
                   && (durableProposed.Words.TryFind caller |> Option.exists (fun entry -> entry.Maturity = LibraryWord)) then
                    rejectUnqualifiedLibraryDependencies (effectiveWords durableProposed) caller
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
                if proposedWords[name].Maturity = LibraryWord then
                    let result = results |> List.filter (fun test -> test.Word = name)
                    requireLibraryCoverage durableSnapshot name result
            for caller in callers do
                if not (selectedWords.Contains caller)
                   && (durableProposed.Words.TryFind caller |> Option.exists (fun entry -> entry.Maturity = LibraryWord)) then
                    requireLibraryCoverage durableSnapshot caller (results |> List.filter (fun test -> test.Word = caller))
            let history =
                selectedWords
                |> Set.fold (fun (found: Map<string, WordDefinition list>) name ->
                    let previous = found.TryFind name |> Option.defaultValue []
                    Map.add name (previous @ [ proposedWords[name].Definition ]) found) data.History
            let finalState = { proposed with History = history }
            let compiledFinalSnapshot = compileRuntimeSnapshot finalState
            let committedFlowHistory: Map<string, FlowAuthoredWord list> =
                selectedWords
                |> Set.fold (fun (found: Map<string, FlowAuthoredWord list>) name ->
                    match compiledFinalSnapshot.State.WordIds.TryFind name with
                    | None -> found
                    | Some identity ->
                        match compiledFinalSnapshot.State.FlowWords.TryFind identity with
                        | None -> found
                        | Some (authored: FlowAuthoredWord) ->
                            let previous = found.TryFind identity |> Option.defaultValue []
                            if previous |> List.exists (fun item -> item.Source.OwnerRevision = authored.Source.OwnerRevision) then found
                            else Map.add identity (previous @ [ authored ]) found) compiledFinalSnapshot.State.FlowHistory
            let finalSnapshot =
                { compiledFinalSnapshot with
                    State = { compiledFinalSnapshot.State with FlowHistory = committedFlowHistory } }
            let durableSnapshot = compileRuntimeSnapshot (durableState finalSnapshot.State)
            publish data durableSnapshot.State actor
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

        let flowJsonKind (value: JsonNode) =
            match value with
            | null -> "null"
            | :? JsonObject -> "object"
            | :? JsonArray -> "array"
            | :? JsonValue as scalar ->
                let mutable stringValue = ""
                let mutable boolValue = false
                let mutable numberValue = 0
                if scalar.TryGetValue<string>(&stringValue) then "string"
                elif scalar.TryGetValue<bool>(&boolValue) then "boolean"
                elif scalar.TryGetValue<int>(&numberValue) then "number"
                else "number"
            | _ -> "value"

        let selectedFrontend (arguments: JsonObject) =
            if not (arguments.ContainsKey "frontend") then "flow"
            else
                match arguments["frontend"] with
                | :? JsonValue as value ->
                    let mutable frontend = ""
                    if not (value.TryGetValue<string>(&frontend)) then
                        error "RUNTIME_FRONTEND_INVALID" "An explicit frontend selector must be a string." None None [ "stack"; "flow" ] [ flowJsonKind (value :> JsonNode) ]
                    elif frontend = "stack" || frontend = "flow" then frontend
                    else error "RUNTIME_FRONTEND_UNSUPPORTED" $"Frontend '{frontend}' is not supported." None None [ "stack"; "flow" ] [ frontend ]
                | value ->
                    error "RUNTIME_FRONTEND_INVALID" "An explicit frontend selector must be a string." None None [ "stack"; "flow" ] [ flowJsonKind value ]

        let selectedSyntaxVersion (arguments: JsonObject) frontend =
            if not (arguments.ContainsKey "syntaxVersion") then 1
            else
                match arguments["syntaxVersion"] with
                | :? JsonValue as value ->
                    let mutable version = 0
                    if not (value.TryGetValue<int>(&version)) then
                        error "RUNTIME_SOURCE_VERSION_INVALID" "Argument 'syntaxVersion' must be an integer." None None [ "integer" ] [ flowJsonKind (value :> JsonNode) ]
                    match frontend, version with
                    | "stack", 1
                    | "flow", 1
                    | "flow", 2 -> version
                    | "stack", _ ->
                        error "RUNTIME_SOURCE_VERSION_UNSUPPORTED" $"Stack syntax version {version} is not supported." None None [ "1" ] [ string version ]
                    | "flow", _ ->
                        error "RUNTIME_SOURCE_VERSION_UNSUPPORTED" $"Flow syntax version {version} is not supported." None None [ "1"; "2" ] [ string version ]
                    | _ -> error "RUNTIME_FRONTEND_UNSUPPORTED" $"Frontend '{frontend}' is not supported." None None [ "stack"; "flow" ] [ frontend ]
                | value ->
                    error "RUNTIME_SOURCE_VERSION_INVALID" "Argument 'syntaxVersion' must be an integer." None None [ "integer" ] [ flowJsonKind value ]

        let flowArgumentError name expected actual =
            error "FLOW_RUNTIME_INVALID_ARGUMENT" $"Flow runtime argument '{name}' must be {expected}." None None [ expected ] [ actual ]

        let flowStringValue name (value: JsonNode) =
            match value with
            | :? JsonValue as scalar ->
                let mutable parsed = ""
                if scalar.TryGetValue<string>(&parsed) then parsed
                else flowArgumentError name "a string" (flowJsonKind value)
            | _ -> flowArgumentError name "a string" (flowJsonKind value)

        let requiredFlowString (arguments: JsonObject) name =
            if not (arguments.ContainsKey name) then flowArgumentError name "a string" "missing"
            flowStringValue name (arguments[name])

        let flowSourceStrings (arguments: JsonObject) name =
            if not (arguments.ContainsKey name) then []
            else
                match arguments[name] with
                | :? JsonArray as values ->
                    values
                    |> Seq.mapi (fun index value -> flowStringValue ($"{name}[{index}]") value)
                    |> Seq.toList
                | value -> flowArgumentError name "an array of source strings" (flowJsonKind value)

        let flowExpectedRevision (arguments: JsonObject) =
            if not (arguments.ContainsKey "expectedRevision") then flowArgumentError "expectedRevision" "a nonnegative integer" "missing"
            match arguments["expectedRevision"] with
            | :? JsonValue as value ->
                let mutable parsed = 0
                if value.TryGetValue<int>(&parsed) && parsed >= 0 then parsed
                else flowArgumentError "expectedRevision" "a nonnegative integer" (flowJsonKind value)
            | value -> flowArgumentError "expectedRevision" "a nonnegative integer" (flowJsonKind value)

        let flowExpectedRevisions (arguments: JsonObject) =
            if not (arguments.ContainsKey "expectedRevisions") then
                flowArgumentError "expectedRevisions" "an object mapping each replaced word to its nonnegative current revision" "missing"
            match arguments["expectedRevisions"] with
            | :? JsonObject as values ->
                values
                |> Seq.map (fun (KeyValue(name, node)) ->
                    match node with
                    | :? JsonValue as value ->
                        let mutable parsed = 0
                        if value.TryGetValue<int>(&parsed) && parsed >= 0 then name, parsed
                        else flowArgumentError ($"expectedRevisions.{name}") "a nonnegative integer" (flowJsonKind node)
                    | _ -> flowArgumentError ($"expectedRevisions.{name}") "a nonnegative integer" (flowJsonKind node))
                |> Map.ofSeq
            | value -> flowArgumentError "expectedRevisions" "an object mapping each replaced word to its nonnegative current revision" (flowJsonKind value)

        let flowAttachmentRemovals (arguments: JsonObject) =
            if not (arguments.ContainsKey "removeAttachments") then []
            else
                match arguments["removeAttachments"] with
                | :? JsonArray as rows ->
                    rows
                    |> Seq.mapi (fun index row ->
                        let prefix = $"removeAttachments[{index}]"
                        match row with
                        | :? JsonObject as item ->
                            let kind = requiredFlowString item "kind"
                            let caseName = requiredFlowString item "caseName"
                            let expectedSourceHash = requiredFlowString item "expectedSourceHash"
                            let attachmentKind =
                                match kind with
                                | "test" -> FlowLowering.FlowAttachmentKind.Test
                                | "example" -> FlowLowering.FlowAttachmentKind.Example
                                | _ -> flowArgumentError ($"{prefix}.kind") "'test' or 'example'" kind
                            attachmentKind, caseName, expectedSourceHash
                        | value -> flowArgumentError prefix "an attachment removal object" (flowJsonKind value))
                    |> Seq.toList
                | value -> flowArgumentError "removeAttachments" "an array of attachment removal objects" (flowJsonKind value)

        let ordinalSort values =
            values |> List.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))

        let flowDefineAllowedKeys = AuthoringHelp.flowDefineFields |> List.map (fun field -> field.Name)

        let rejectUnknownFields path allowed (actual: string list) =
            let unknown = actual |> ordinalSort
            if not (List.isEmpty unknown) then
                let location =
                    if path = "" then "Flow define request"
                    elif path = "format" then "Flow format request"
                    else $"Flow define attachment row '{path}'"
                let docHint =
                    if unknown |> List.exists (fun name -> name = "doc" || name = "documentation") then
                        " Documentation belongs inside Flow word source as `doc \"...\"`; use help topic `define` for the request schema."
                    else " See help topic `define` for the request schema."
                let unknownText = String.concat ", " unknown
                error "FLOW_RUNTIME_UNKNOWN_ARGUMENT" $"{location} has unsupported field(s): {unknownText}.{docHint}" None None
                    (allowed |> ordinalSort) unknown

        let validateFlowDefineArguments (arguments: JsonObject) =
            let allowed = Set.ofList flowDefineAllowedKeys
            let topLevelUnknown =
                arguments
                |> Seq.map (fun (KeyValue(key, _)) -> key)
                |> Seq.filter (allowed.Contains >> not)
                |> Seq.toList
            rejectUnknownFields "" flowDefineAllowedKeys topLevelUnknown

            match arguments["removeAttachments"] with
            | :? JsonArray as rows ->
                rows
                |> Seq.iteri (fun index row ->
                    match row with
                    | :? JsonObject as item ->
                        let nestedAllowed = [ "kind"; "caseName"; "expectedSourceHash" ]
                        let nestedSet = Set.ofList nestedAllowed
                        let nestedUnknown =
                            item
                            |> Seq.map (fun (KeyValue(key, _)) -> key)
                            |> Seq.filter (nestedSet.Contains >> not)
                            |> Seq.toList
                        rejectUnknownFields ($"removeAttachments[{index}]") nestedAllowed nestedUnknown
                    | _ -> ())
            | _ -> ()

        let rejectFlowTypeReplacement (document: FlowProjectDocument) =
            let declarations =
                [ document.Records |> List.map (fun definition -> "record", definition.Name, definition.Span)
                  document.Scalars |> List.map (fun definition -> "scalar", definition.Name, definition.Span)
                  document.Enums |> List.map (fun definition -> "enum", definition.Name, definition.Span) ]
                |> List.concat
                |> List.sortBy (fun (_, _, span) -> span.Line, span.Column)
            match declarations with
            | (kind, name, span) :: _ ->
                error "FLOW_PROJECT_REPLACEMENT_TYPES_UNSUPPORTED"
                    $"Flow {kind} type '{name}' cannot be replaced: record, scalar, and enum schemas are immutable after creation. expectedRevision and expectedRevisions compare word revisions only; they cannot change type schemas. Define a new type under an unused name and migrate dependent words separately."
                    (Some name) (Some span)
                    [ "existing authored Flow word declarations only" ]
                    [ $"{kind} type declaration"; name ]
            | [] -> ()

        let registerFlowReplacementProjectParsed (arguments: JsonObject) syntaxVersion (document: FlowProjectDocument) =
            let old = data
            let wordNames = document.Words |> List.map (fun item -> item.Name)
            if not (readOptionalStrictBool arguments "replace" false) then
                error "FLOW_PROJECT_REQUEST_SHAPE" "A multi-declaration Flow replacement requires replace=true and an expectedRevisions map." None None [ "replace=true with expectedRevisions" ] []
            if document.Words.Length < 2 then
                error "FLOW_PROJECT_REPLACEMENT_SHAPE" "expectedRevisions is reserved for replacing at least two existing authored Flow words in one source document." None None [ "at least two word declarations" ] [ string document.Words.Length ]
            if document.SyntaxVersion <> syntaxVersion then
                error "FLOW_VERSION_UNSUPPORTED" "Flow project syntax version does not match the selected syntaxVersion." None None [ string syntaxVersion ] [ string document.SyntaxVersion ]
            if not document.TestFiles.IsEmpty then
                error "FLOW_PROJECT_REPLACEMENT_WRAPPERS_UNSUPPORTED" "A multiword replacement cannot add or edit test-file wrappers; existing wrappers are retained with their owner revisions." None None [ "standalone inline tests and examples" ] [ "test-file wrapper" ]
            if arguments.ContainsKey "expectedRevision"
               || arguments.ContainsKey "removeAttachments"
               || arguments.ContainsKey "tests"
               || arguments.ContainsKey "examples"
               || arguments.ContainsKey "temporary" then
                error "FLOW_PROJECT_REQUEST_SHAPE" "A multiword replacement requires one exact expectedRevisions map; temporary lifecycle changes, scalar CAS, removals, and external attachments are unsupported." None None
                    [ "replace=true, expectedRevisions, and complete Flow source with inline cases" ]
                    ((if arguments.ContainsKey "expectedRevision" then [ "expectedRevision" ] else [])
                     @ (if arguments.ContainsKey "removeAttachments" then [ "removeAttachments" ] else [])
                     @ (if arguments.ContainsKey "tests" then [ "tests" ] else [])
                     @ (if arguments.ContainsKey "examples" then [ "examples" ] else [])
                     @ (if arguments.ContainsKey "temporary" then [ "temporary" ] else []))
            let revisionsByName = flowExpectedRevisions arguments
            match wordNames |> List.groupBy id |> List.tryFind (fun (_, grouped) -> grouped.Length > 1) with
            | Some(name, _) -> error "FLOW_PROJECT_DUPLICATE_WORD" $"Word '{name}' is declared more than once in the Flow project." (Some name) None [] [ name ]
            | None -> ()
            if Set.ofList wordNames <> (revisionsByName |> Map.toSeq |> Seq.map fst |> Set.ofSeq) then
                error "FLOW_PROJECT_REPLACEMENT_CAS_SHAPE" "expectedRevisions must contain exactly one entry for each word declared in source." None None
                    (ordinalSort wordNames) (revisionsByName |> Map.toList |> List.map fst |> ordinalSort)

            let replacementRows =
                document.Words
                |> List.map (fun parsed ->
                    if parsed.SyntaxVersion <> syntaxVersion then
                        error "FLOW_VERSION_UNSUPPORTED" "Flow word syntax version does not match the selected syntaxVersion." (Some parsed.Name) (Some parsed.Span) [ string syntaxVersion ] [ string parsed.SyntaxVersion ]
                    let prior =
                        old.Words.TryFind parsed.Name
                        |> Option.defaultWith (fun () -> error "FLOW_BATCH_REPLACE_MISSING" "A batch replacement requires each declared word to exist already." (Some parsed.Name) (Some parsed.Span) [ "existing authored Flow word" ] [])
                    if prior.Builtin.IsSome || prior.Status <> Persistent then
                        error "FLOW_BATCH_REPLACE_PROTECTED" "A batch replacement accepts only persistent user-authored Flow words." (Some parsed.Name) (Some parsed.Span) [ "persistent authored Flow word" ] [ string prior.Status ]
                    let expected = revisionsByName[parsed.Name]
                    if expected <> prior.Revision || expected <> prior.Definition.Revision then
                        error "FLOW_BATCH_STALE_REVISION" "Batch replacement expectedRevisions does not match every current owner revision." (Some parsed.Name) (Some parsed.Span) [ string prior.Revision ] [ string expected ]
                    if old.Replacements.ContainsKey parsed.Name then
                        error "FLOW_REPLACEMENT_ALREADY_STAGED" "A word with an existing staged replacement cannot be included in another batch replacement." (Some parsed.Name) (Some parsed.Span) [ "no staged replacement" ] [ parsed.Name ]
                    let identity =
                        old.WordIds.TryFind parsed.Name
                        |> Option.defaultWith (fun () -> error "WORD_ID_MISSING" "A batch replacement requires each word's existing stable identity." (Some parsed.Name) (Some parsed.Span) [] [])
                        |> WordId
                    let authored =
                        old.FlowWords.TryFind(wordIdText identity)
                        |> Option.defaultWith (fun () -> error "FLOW_BATCH_REPLACE_NOT_FLOW" "A batch replacement accepts only existing authored Flow words." (Some parsed.Name) (Some parsed.Span) [ "Flow-authored word" ] [ "Stack-authored word" ])
                    if authored.Source.OwnerName <> parsed.Name || authored.Source.OwnerRevision <> prior.Revision then
                        error "FLOW_ATTACHMENT_OWNER_STALE" $"Flow word '{parsed.Name}' does not match its current immutable owner revision." (Some parsed.Name) (Some parsed.Span) [ $"{parsed.Name}@{prior.Revision}" ] [ $"{authored.Source.OwnerName}@{authored.Source.OwnerRevision}" ]
                    if authored.Source.SyntaxVersion <> syntaxVersion && not (arguments.ContainsKey "syntaxVersion") then
                        error "FLOW_SOURCE_VERSION_CHANGE_REQUIRES_SELECTION" "Replacing a Flow word with a different syntax version requires an explicit syntaxVersion selector." (Some parsed.Name) (Some parsed.Span) [ string authored.Source.SyntaxVersion; "explicit syntaxVersion" ] [ string syntaxVersion ]
                    let revision = prior.Revision + 1
                    let content = parsed.SourceText
                    let sourceObject = Storage.sourceObject StorageObjectKind.WordDefinition content
                    let sourceFile = $"<flow:{parsed.Name}/{revision}:{sourceObject.Reference.Hash}>"
                    let definition =
                        match FlowParser.parseWordWithVersion syntaxVersion sourceFile content with
                        | Ok value when value.Name = parsed.Name -> value
                        | Ok value -> error "FLOW_RUNTIME_OWNER_MISMATCH" "A standalone Flow word source changed its declared owner during validation." (Some parsed.Name) (Some value.Span) [ parsed.Name ] [ value.Name ]
                        | Error diagnostic -> raise (LanguageException diagnostic)
                    let source: FlowLowering.FlowSourceDocument =
                        { OwnerName = parsed.Name
                          SyntaxVersion = syntaxVersion
                          EffectsDeclared = definition.EffectsDeclared
                          OwnerId = identity
                          OwnerRevision = revision
                          Reference = sourceObject.Reference
                          SourceFile = sourceFile
                          Content = content }
                    let flowWord: FlowAuthoredWord = { Definition = definition; Source = source; StoredBindings = None }
                    let projected: WordDefinition =
                        { Name = definition.Name
                          Inputs = definition.Parameters |> List.map (fun parameter -> parameter.Type)
                          Outputs = definition.Outputs
                          Effects = definition.Effects
                          Maturity = prior.Maturity
                          Revision = revision
                          Documentation = definition.Documentation
                          Body = []
                          SourceText = content
                          Span = definition.Span }
                    parsed.Name, prior, identity, revision, flowWord, projected)
            let rowByName = replacementRows |> List.map (fun (name, prior, identity, revision, authored, projected) -> name, (prior, identity, revision, authored, projected)) |> Map.ofList
            let rowByIdentity = replacementRows |> List.map (fun (name, _, identity, revision, _, _) -> wordIdText identity, (name, revision)) |> Map.ofList
            let owners = replacementRows |> List.map (fun (_, _, identity, _, _, _) -> wordIdText identity) |> Set.ofList

            let parseTests =
                document.Tests
                |> List.map (fun parsed ->
                    match rowByName.TryFind parsed.Word with
                    | None -> error "FLOW_ATTACHMENT_OWNER_MISMATCH" $"Batch test '{parsed.CaseName}' must attach to a word declared in the same replacement document." (Some parsed.Word) (Some parsed.Span) wordNames [ parsed.Word ]
                    | Some(_, identity, revision, _, _) ->
                        let content = parsed.SourceText
                        let sourceObject = Storage.sourceObject StorageObjectKind.TestDefinition content
                        let sourceFile = $"<flow:{parsed.Word}/{revision}>/test:{sourceObject.Reference.Hash}"
                        let definition =
                            match FlowParser.parseTestWithVersion syntaxVersion sourceFile content with
                            | Ok value when value.Word = parsed.Word -> value
                            | Ok value -> error "FLOW_ATTACHMENT_OWNER_MISMATCH" "A Flow test source changed its declared owner during batch validation." (Some parsed.Word) (Some value.Span) [ parsed.Word ] [ value.Word ]
                            | Error diagnostic -> raise (LanguageException diagnostic)
                        let source: FlowLowering.FlowAttachmentSourceDocument =
                            { OwnerName = parsed.Word
                              SyntaxVersion = syntaxVersion
                              OwnerId = identity
                              OwnerRevision = revision
                              Kind = FlowLowering.FlowAttachmentKind.Test
                              CaseName = definition.CaseName
                              Reference = sourceObject.Reference
                              SourceFile = sourceFile
                              Content = content }
                        flowAttachmentKey identity definition.CaseName, { Source = source; StoredBindings = None })
            let parseExamples =
                document.Examples
                |> List.map (fun parsed ->
                    match rowByName.TryFind parsed.Word with
                    | None -> error "FLOW_ATTACHMENT_OWNER_MISMATCH" $"Batch example '{parsed.CaseName}' must attach to a word declared in the same replacement document." (Some parsed.Word) (Some parsed.Span) wordNames [ parsed.Word ]
                    | Some(_, identity, revision, _, _) ->
                        let content = parsed.SourceText
                        let sourceObject = Storage.sourceObject StorageObjectKind.ExampleDefinition content
                        let sourceFile = $"<flow:{parsed.Word}/{revision}>/example:{sourceObject.Reference.Hash}"
                        let definition =
                            match FlowParser.parseExampleWithVersion syntaxVersion sourceFile content with
                            | Ok value when value.Word = parsed.Word -> value
                            | Ok value -> error "FLOW_ATTACHMENT_OWNER_MISMATCH" "A Flow example source changed its declared owner during batch validation." (Some parsed.Word) (Some value.Span) [ parsed.Word ] [ value.Word ]
                            | Error diagnostic -> raise (LanguageException diagnostic)
                        let source: FlowLowering.FlowAttachmentSourceDocument =
                            { OwnerName = parsed.Word
                              SyntaxVersion = syntaxVersion
                              OwnerId = identity
                              OwnerRevision = revision
                              Kind = FlowLowering.FlowAttachmentKind.Example
                              CaseName = definition.CaseName
                              Reference = sourceObject.Reference
                              SourceFile = sourceFile
                              Content = content }
                        flowAttachmentKey identity definition.CaseName, { Source = source; StoredBindings = None })
            let rejectDuplicateAttachmentKeys kind rows =
                match rows |> List.groupBy fst |> List.tryFind (fun (_, grouped) -> grouped.Length > 1) with
                | Some(key, _) -> error "FLOW_ATTACHMENT_DUPLICATE_CHANGE" $"A Flow batch may change one {kind} attachment key only once." (Some key) None [] [ key ]
                | None -> ()
            rejectDuplicateAttachmentKeys "test" parseTests
            rejectDuplicateAttachmentKeys "example" parseExamples
            for name, prior, identity, _, _, _ in replacementRows do
                let currentFlowWord = old.FlowWords[wordIdText identity]
                if currentFlowWord.Source.SyntaxVersion <> syntaxVersion && not (arguments.ContainsKey "syntaxVersion") then
                    error "FLOW_SOURCE_VERSION_CHANGE_REQUIRES_SELECTION" "Replacing a Flow word with a different syntax version requires an explicit syntaxVersion selector." (Some name) (Some currentFlowWord.Definition.Span) [ string currentFlowWord.Source.SyntaxVersion; "explicit syntaxVersion" ] [ string syntaxVersion ]

            let incomingTestsByKey = parseTests |> Map.ofList
            let incomingExamplesByKey = parseExamples |> Map.ofList
            let rejectWrappedReplacement kind (incoming: (string * FlowAuthoredAttachment) list) (priorItems: Map<string, FlowAuthoredAttachment>) =
                for key, _ in incoming do
                    match priorItems.TryFind key with
                    | Some prior when old.FlowTestFiles.ContainsKey(flowTestFileKey prior.Source.OwnerId prior.Source.Reference) ->
                        error "FLOW_BATCH_REPLACE_WRAPPED_CASE" $"A batch cannot replace standalone {kind} case key '{key}' while it is represented by a retained test-file wrapper." (Some key) None [ "retained wrapper or a separate wrapper edit" ] [ "standalone case replacement" ]
                    | _ -> ()
            rejectWrappedReplacement "test" parseTests old.FlowTests

            let nextTestFiles =
                old.FlowTestFiles
                |> Map.map (fun _ file ->
                    if owners.Contains(wordIdText file.OwnerId) then
                        let name, revision = rowByIdentity[wordIdText file.OwnerId]
                        { file with OwnerRevision = revision; SourceFile = $"<flow:{name}/{revision}>/test-file:{file.Reference.Hash}" }
                    else file)
            let refreshRetainedTest (key: string) (item: FlowAuthoredAttachment) : FlowAuthoredAttachment =
                if owners.Contains(wordIdText item.Source.OwnerId) then
                    let name, revision = rowByIdentity[wordIdText item.Source.OwnerId]
                    match old.FlowTestFiles.TryFind(flowTestFileKey item.Source.OwnerId item.Source.Reference) with
                    | Some file ->
                        let updatedFile = nextTestFiles[flowTestFileKey file.OwnerId file.Reference]
                        { item with Source = { item.Source with OwnerRevision = revision; Reference = updatedFile.Reference; SourceFile = updatedFile.SourceFile; Content = updatedFile.Settings.SourceText } }
                    | None ->
                        { item with Source = { item.Source with OwnerRevision = revision; SourceFile = $"<flow:{name}/{revision}>/test:{item.Source.Reference.Hash}" } }
                else item
            let retainedTests = old.FlowTests |> Map.map refreshRetainedTest
            let nextTests =
                incomingTestsByKey
                |> Map.fold (fun found key incoming ->
                    let prior = old.FlowTests.TryFind key
                    let stored =
                        prior
                        |> Option.filter (fun item -> item.Source.Reference = incoming.Source.Reference)
                        |> Option.bind (fun item -> item.StoredBindings)
                    Map.add key { incoming with StoredBindings = stored } found) retainedTests
            let retainedExamples =
                old.FlowExamples
                |> Map.map (fun _ item ->
                    if owners.Contains(wordIdText item.Source.OwnerId) then
                        let name, revision = rowByIdentity[wordIdText item.Source.OwnerId]
                        { item with Source = { item.Source with OwnerRevision = revision; SourceFile = $"<flow:{name}/{revision}>/example:{item.Source.Reference.Hash}" } }
                    else item)
            let nextExamples =
                incomingExamplesByKey
                |> Map.fold (fun found key incoming ->
                    let prior = old.FlowExamples.TryFind key
                    let stored =
                        prior
                        |> Option.filter (fun item -> item.Source.Reference = incoming.Source.Reference)
                        |> Option.bind (fun item -> item.StoredBindings)
                    Map.add key { incoming with StoredBindings = stored } found) retainedExamples
            let replacementBackups =
                replacementRows
                |> List.fold (fun found (name, prior, identity, _, _, _) ->
                    let ownerId = wordIdText identity
                    let backup: ReplacementBackup =
                        { Word = prior
                          Tests = old.Tests |> Map.filter (fun _ test -> test.Word = name)
                          Examples = old.Examples |> Map.filter (fun _ example -> example.Word = name)
                          FlowWord = old.FlowWords.TryFind ownerId
                          FlowTests = old.FlowTests |> Map.filter (fun _ item -> item.Source.OwnerId = identity)
                          FlowTestFiles = old.FlowTestFiles |> Map.filter (fun _ item -> item.OwnerId = identity)
                          FlowExamples = old.FlowExamples |> Map.filter (fun _ item -> item.Source.OwnerId = identity) }
                    Map.add name backup found) old.Replacements
            let nextWords =
                replacementRows
                |> List.fold (fun found (name, _, _, revision, _, projected) -> Map.add name (entry projected None Candidate projected.Maturity revision) found) old.Words
            let nextFlowWords =
                replacementRows
                |> List.fold (fun found (_, _, identity, _, authored, _) -> Map.add (wordIdText identity) authored found) old.FlowWords
            let proposed =
                { old with
                    Words = nextWords
                    FlowWords = nextFlowWords
                    FlowTests = nextTests
                    FlowTestFiles = nextTestFiles
                    FlowExamples = nextExamples
                    Replacements = replacementBackups }
            let executable = compileRuntimeSnapshot proposed
            let frozen = frozenValidatorWords old (effectiveWords old)
            match Set.intersect frozen (Set.ofList wordNames) |> Set.toList with
            | name :: _ -> error "TYPE_VALIDATOR_FROZEN" $"Cannot replace validator dependency '{name}' while a nominal type is persistent." (Some name) None [] [ name ]
            | [] -> ()
            for name in wordNames do
                if old.Words[name].Maturity = LibraryWord then
                    rejectUnqualifiedLibraryDependencies executable.Words name
            activateRuntimeSnapshot executable
            lastResults <- []
            for name in wordNames do log "create" name
            let payload = JsonObject()
            payload["frontend"] <- jstr "flow"
            let wordPayload = JsonArray()
            for name in wordNames do
                let item = executable.State.Words[name]
                let row = JsonObject()
                row["name"] <- jstr name
                row["id"] <- jstr (wordIdentity executable.State item)
                row["revision"] <- jint item.Revision
                wordPayload.Add row
            payload["words"] <- wordPayload
            success "defined" "Flow word replacements and inline standalone cases were validated and staged atomically." (Some payload)

        let registerFlowProjectAddOnlyParsed (arguments: JsonObject) syntaxVersion (document: FlowProjectDocument) =
            let old = data
            let temporary = readOptionalStrictBool arguments "temporary" false
            let hasTypes = not document.Records.IsEmpty || not document.Scalars.IsEmpty || not document.Enums.IsEmpty
            if document.SyntaxVersion <> syntaxVersion then
                error "FLOW_VERSION_UNSUPPORTED" "Flow project syntax version does not match the selected syntaxVersion." None None [ string syntaxVersion ] [ string document.SyntaxVersion ]
            if document.Records.IsEmpty && document.Scalars.IsEmpty && document.Enums.IsEmpty && document.Words.IsEmpty && document.TestFiles.IsEmpty then
                error "FLOW_PROJECT_EMPTY" "A Flow project document must declare at least one type or word." None None [ "record, scalar, enum, or word declaration" ] []
            if hasTypes && temporary then
                error "FLOW_PROJECT_TEMPORARY_TYPES_UNSUPPORTED" "Flow types do not have a temporary lifecycle; define the typed project as candidates or omit its type declarations." None None [ "temporary=false for project types" ] [ "temporary=true" ]
            if readOptionalStrictBool arguments "replace" false
               || arguments.ContainsKey "expectedRevision"
               || arguments.ContainsKey "expectedRevisions"
               || arguments.ContainsKey "removeAttachments"
               || (arguments.ContainsKey "tests" && not (flowSourceStrings arguments "tests").IsEmpty)
               || (arguments.ContainsKey "examples" && not (flowSourceStrings arguments "examples").IsEmpty) then
                error "FLOW_PROJECT_REQUEST_SHAPE" "A multi-declaration Flow project is add-only; put tests and examples in the document and use the existing one-word CAS route for replacement." None None
                    [ "new project declarations with inline test/example sources" ] [ "replace, expectedRevision, or external attachment changes" ]

            let typeNames = (document.Records |> List.map (fun item -> item.Name)) @ (document.Scalars |> List.map (fun item -> item.Name)) @ (document.Enums |> List.map (fun item -> item.Name))
            let wordNames = document.Words |> List.map (fun item -> item.Name)
            let duplicateWord = wordNames |> List.groupBy id |> List.tryFind (fun (_, grouped) -> grouped.Length > 1)
            match duplicateWord with
            | Some(name, _) -> error "FLOW_PROJECT_DUPLICATE_WORD" $"Word '{name}' is declared more than once in the Flow project." (Some name) None [] [ name ]
            | None -> ()
            match Set.intersect (Set.ofList typeNames) (Set.ofList wordNames) |> Set.toList with
            | name :: _ -> error "FLOW_PROJECT_NAME_COLLISION" $"'{name}' is declared as both a type and a word." (Some name) None [] [ name ]
            | [] -> ()
            match typeNames |> List.groupBy id |> List.tryFind (fun (_, grouped) -> grouped.Length > 1) with
            | Some(name, _) -> error "FLOW_PROJECT_DUPLICATE_TYPE" $"Type '{name}' is declared more than once in the Flow project." (Some name) None [] [ name ]
            | None -> ()
            for name in typeNames do
                if (knownTypes old).Contains name then
                    error "FLOW_PROJECT_TYPE_ALREADY_EXISTS" $"Type '{name}' already exists; project documents do not replace type sources." (Some name) None [ "unused type name" ] [ name ]
            let existingWords = effectiveWords old
            let typeCollisionState =
                let records: Map<string, RecordEntry> =
                    document.Records
                    |> List.fold (fun found definition -> Map.add definition.Name ({ Definition = definition; Status = Candidate }: RecordEntry) found) old.Records
                let scalars: Map<string, ScalarEntry> =
                    document.Scalars
                    |> List.fold (fun found definition -> Map.add definition.Name ({ Definition = definition; Status = Candidate }: ScalarEntry) found) old.Scalars
                let enums: Map<string, EnumEntry> =
                    document.Enums
                    |> List.fold (fun found definition -> Map.add definition.Name ({ Definition = definition; Status = Candidate }: EnumEntry) found) old.Enums
                { old with Records = records; Scalars = scalars; Enums = enums }
            let generatedBeforeWords = makeGenerated typeCollisionState
            for name in wordNames do
                if existingWords.ContainsKey name || generatedBeforeWords.ContainsKey name then
                    error "FLOW_PROJECT_WORD_ALREADY_EXISTS" $"Word '{name}' already exists as a primitive, generated word, or user word." (Some name) None [ "unused word name" ] [ name ]

            let newRecords: (string * RecordEntry * AuthoredTypeSource) list =
                document.Records
                |> List.map (fun parsed ->
                    let content = parsed.SourceText
                    let sourceObject = Storage.sourceObject StorageObjectKind.TypeDefinition content
                    let file = $"<flow-type:{parsed.Name}/{sourceObject.Reference.Hash}>"
                    let reparsed =
                        match FlowParser.parseDocumentWithVersion syntaxVersion file content with
                        | Ok value -> value
                        | Error diagnostic -> raise (LanguageException diagnostic)
                    match reparsed.Records, reparsed.Scalars, reparsed.Enums, reparsed.Words, reparsed.Tests, reparsed.Examples with
                    | [ definition ], [], [], [], [], [] when definition.Name = parsed.Name ->
                        parsed.Name,
                        ({ Definition = definition; Status = Candidate }: RecordEntry),
                        { SourceFormat = { Frontend = SourceFrontend.Flow; Version = syntaxVersion }
                          Content = content
                          Reference = sourceObject.Reference
                          ValidatorTarget = None }
                    | _ -> error "FLOW_PROJECT_TYPE_SOURCE_INVALID" $"Record source '{parsed.Name}' must contain exactly its authored Flow record declaration." (Some parsed.Name) (Some parsed.Span) [] [] )
            let newScalars: (string * ScalarEntry * AuthoredTypeSource) list =
                document.Scalars
                |> List.map (fun parsed ->
                    let content = parsed.SourceText
                    let sourceObject = Storage.sourceObject StorageObjectKind.TypeDefinition content
                    let file = $"<flow-type:{parsed.Name}/{sourceObject.Reference.Hash}>"
                    let reparsed =
                        match FlowParser.parseDocumentWithVersion syntaxVersion file content with
                        | Ok value -> value
                        | Error diagnostic -> raise (LanguageException diagnostic)
                    match reparsed.Records, reparsed.Scalars, reparsed.Enums, reparsed.Words, reparsed.Tests, reparsed.Examples with
                    | [], [ definition ], [], [], [], [] when definition.Name = parsed.Name ->
                        parsed.Name,
                        ({ Definition = definition; Status = Candidate }: ScalarEntry),
                        { SourceFormat = { Frontend = SourceFrontend.Flow; Version = syntaxVersion }
                          Content = content
                          Reference = sourceObject.Reference
                          ValidatorTarget = None }
                    | _ -> error "FLOW_PROJECT_TYPE_SOURCE_INVALID" $"Scalar source '{parsed.Name}' must contain exactly its authored Flow type declaration." (Some parsed.Name) (Some parsed.Span) [] [] )
            let newEnums: (string * EnumEntry * AuthoredTypeSource) list =
                document.Enums
                |> List.map (fun parsed ->
                    let content = parsed.SourceText
                    let sourceObject = Storage.sourceObject StorageObjectKind.TypeDefinition content
                    let file = $"<flow-type:{parsed.Name}/{sourceObject.Reference.Hash}>"
                    let reparsed =
                        match FlowParser.parseDocumentWithVersion syntaxVersion file content with
                        | Ok value -> value
                        | Error diagnostic -> raise (LanguageException diagnostic)
                    match reparsed.Records, reparsed.Scalars, reparsed.Enums, reparsed.Words, reparsed.Tests, reparsed.Examples with
                    | [], [], [ definition ], [], [], [] when syntaxVersion = 2 && definition.Name = parsed.Name ->
                        parsed.Name,
                        ({ Definition = definition; Status = Candidate }: EnumEntry),
                        { SourceFormat = { Frontend = SourceFrontend.Flow; Version = syntaxVersion }
                          Content = content
                          Reference = sourceObject.Reference
                          ValidatorTarget = None }
                    | _ -> error "FLOW_PROJECT_TYPE_SOURCE_INVALID" $"Enum source '{parsed.Name}' must contain exactly its authored Flow enum declaration." (Some parsed.Name) (Some parsed.Span) [] [] )

            let identities =
                document.Words
                |> List.map (fun definition -> definition.Name, WordId(newWordIdentity ()))
                |> Map.ofList
            let revision = 1
            let wordRows =
                document.Words
                |> List.map (fun parsed ->
                    if parsed.SyntaxVersion <> syntaxVersion then
                        error "FLOW_VERSION_UNSUPPORTED" "Flow word syntax version does not match the selected syntaxVersion." (Some parsed.Name) (Some parsed.Span) [ string syntaxVersion ] [ string parsed.SyntaxVersion ]
                    let ownerId = identities[parsed.Name]
                    let content = parsed.SourceText
                    let sourceObject = Storage.sourceObject StorageObjectKind.WordDefinition content
                    let sourceFile = $"<flow:{parsed.Name}/{revision}:{sourceObject.Reference.Hash}>"
                    let definition =
                        match FlowParser.parseWordWithVersion syntaxVersion sourceFile content with
                        | Ok value when value.Name = parsed.Name -> value
                        | Ok value -> error "FLOW_RUNTIME_OWNER_MISMATCH" "A standalone Flow word source changed its declared owner during validation." (Some parsed.Name) (Some value.Span) [ parsed.Name ] [ value.Name ]
                        | Error diagnostic -> raise (LanguageException diagnostic)
                    let source: FlowLowering.FlowSourceDocument =
                        { OwnerName = definition.Name
                          SyntaxVersion = syntaxVersion
                          EffectsDeclared = definition.EffectsDeclared
                          OwnerId = ownerId
                          OwnerRevision = revision
                          Reference = sourceObject.Reference
                          SourceFile = sourceFile
                          Content = content }
                    let authored: FlowAuthoredWord = { Definition = definition; Source = source; StoredBindings = None }
                    let projected: WordDefinition =
                        { Name = definition.Name
                          Inputs = definition.Parameters |> List.map (fun parameter -> parameter.Type)
                          Outputs = definition.Outputs
                          Effects = definition.Effects
                          Maturity = ProjectWord
                          Revision = revision
                          Documentation = definition.Documentation
                          Body = []
                          SourceText = content
                          Span = definition.Span }
                    let status = if temporary then Temporary else Candidate
                    definition.Name, entry projected None status ProjectWord revision, authored)
            let newWordMap = wordRows |> List.map (fun (name, word, _) -> name, word) |> Map.ofList
            let flowWordMap = wordRows |> List.map (fun (_, _, authored) -> wordIdText authored.Source.OwnerId, authored) |> Map.ofList
            let ensureAttachmentOwner kind owner caseName span =
                match identities.TryFind owner with
                | Some identity -> identity
                | None ->
                    error "FLOW_ATTACHMENT_OWNER_MISMATCH" $"A Flow project {kind} '{caseName}' must attach to a word declared in the same project document." (Some owner) (Some span) wordNames [ owner ]
            let newTests =
                document.Tests
                |> List.map (fun parsed ->
                    let identity = ensureAttachmentOwner "test" parsed.Word parsed.CaseName parsed.Span
                    let content = parsed.SourceText
                    let sourceObject = Storage.sourceObject StorageObjectKind.TestDefinition content
                    let sourceFile = $"<flow:{parsed.Word}/{revision}>/test:{sourceObject.Reference.Hash}"
                    let definition =
                        match FlowParser.parseTestWithVersion syntaxVersion sourceFile content with
                        | Ok value when value.Word = parsed.Word -> value
                        | Ok value -> error "FLOW_ATTACHMENT_OWNER_MISMATCH" "A standalone Flow test source changed its declared owner during validation." (Some parsed.Word) (Some value.Span) [ parsed.Word ] [ value.Word ]
                        | Error diagnostic -> raise (LanguageException diagnostic)
                    let source: FlowLowering.FlowAttachmentSourceDocument =
                        { OwnerName = parsed.Word
                          SyntaxVersion = syntaxVersion
                          OwnerId = identity
                          OwnerRevision = revision
                          Kind = FlowLowering.FlowAttachmentKind.Test
                          CaseName = definition.CaseName
                          Reference = sourceObject.Reference
                          SourceFile = sourceFile
                          Content = content }
                    flowAttachmentKey identity definition.CaseName, { Source = source; StoredBindings = None })
            let newExamples =
                document.Examples
                |> List.map (fun parsed ->
                    let identity = ensureAttachmentOwner "example" parsed.Word parsed.CaseName parsed.Span
                    let content = parsed.SourceText
                    let sourceObject = Storage.sourceObject StorageObjectKind.ExampleDefinition content
                    let sourceFile = $"<flow:{parsed.Word}/{revision}>/example:{sourceObject.Reference.Hash}"
                    let definition =
                        match FlowParser.parseExampleWithVersion syntaxVersion sourceFile content with
                        | Ok value when value.Word = parsed.Word -> value
                        | Ok value -> error "FLOW_ATTACHMENT_OWNER_MISMATCH" "A standalone Flow example source changed its declared owner during validation." (Some parsed.Word) (Some value.Span) [ parsed.Word ] [ value.Word ]
                        | Error diagnostic -> raise (LanguageException diagnostic)
                    let source: FlowLowering.FlowAttachmentSourceDocument =
                        { OwnerName = parsed.Word
                          SyntaxVersion = syntaxVersion
                          OwnerId = identity
                          OwnerRevision = revision
                          Kind = FlowLowering.FlowAttachmentKind.Example
                          CaseName = definition.CaseName
                          Reference = sourceObject.Reference
                          SourceFile = sourceFile
                          Content = content }
                    flowAttachmentKey identity definition.CaseName, { Source = source; StoredBindings = None })
            let duplicates rows kind =
                match rows |> List.groupBy fst |> List.tryFind (fun (_, grouped) -> grouped.Length > 1) with
                | Some(key, _) -> error "FLOW_ATTACHMENT_DUPLICATE_CHANGE" $"A Flow project may declare one {kind} case key only once." (Some key) None [] [ key ]
                | None -> ()
            duplicates newTests "test"
            duplicates newExamples "example"

            let newTestFiles =
                document.TestFiles
                |> List.map (fun parsed ->
                    if parsed.SyntaxVersion <> syntaxVersion then
                        error "FLOW_VERSION_UNSUPPORTED" "Flow test-file syntax version does not match the selected syntaxVersion." None (Some parsed.Span) [ string syntaxVersion ] [ string parsed.SyntaxVersion ]
                    let sourceObject = Storage.sourceObject StorageObjectKind.TestDefinition parsed.SourceText
                    let sourceFile = $"<flow-test-file:{parsed.ScopeName}/{sourceObject.Reference.Hash}>"
                    let settings =
                        match FlowParser.parseTestFileSettingsWithVersion syntaxVersion sourceFile parsed.SourceText with
                        | Ok value -> value
                        | Error diagnostic -> raise (LanguageException diagnostic)
                    let owners = settings.Tests |> List.map (fun test -> test.Word) |> List.distinct
                    let ownerName =
                        match owners with
                        | [ name ] -> name
                        | _ -> error "FLOW_TEST_FILE_OWNER_MISMATCH" "A test-file wrapper must target exactly one Flow word owner." None (Some settings.Span) [ "one owner" ] owners
                    let ownerId, ownerRevision =
                        match identities.TryFind ownerName with
                        | Some identity -> identity, revision
                        | None ->
                            match old.WordIds.TryFind ownerName, old.Words.TryFind ownerName with
                            | Some identity, Some word when word.Builtin.IsNone && word.Status <> Primitive ->
                                let ownerId = WordId identity
                                match old.FlowWords.TryFind(wordIdText ownerId) with
                                | Some authored when authored.Source.OwnerRevision = word.Revision && authored.Source.SyntaxVersion = syntaxVersion ->
                                    error "FLOW_PROJECT_TEST_FILE_OWNER_NOT_DECLARED" "A multi-declaration Flow project may attach test-file wrappers only to words declared in that transaction. Submit a wrapper-only document to attach to an existing Flow owner." (Some ownerName) (Some settings.Span) [ "owner declared in this project; otherwise wrapper-only attachment" ] [ "existing Flow owner outside this project" ]
                                | Some authored ->
                                    error "FLOW_ATTACHMENT_OWNER_STALE" $"Flow test-file owner '{ownerName}' does not match its current Flow revision or syntax version." (Some ownerName) (Some settings.Span) [ $"{ownerName}@{word.Revision}"; string syntaxVersion ] [ $"{authored.Source.OwnerName}@{authored.Source.OwnerRevision}"; string authored.Source.SyntaxVersion ]
                                | None -> error "FLOW_ATTACHMENT_OWNER_NOT_FLOW_WORD" $"Test-file owner '{ownerName}' is not authored in Flow." (Some ownerName) (Some settings.Span) [ "Flow-authored word" ] [ "Stack word" ]
                            | _ -> error "FLOW_ATTACHMENT_OWNER_MISMATCH" $"Test-file owner '{ownerName}' must be declared in this project or name an existing Flow word." (Some ownerName) (Some settings.Span) wordNames [ ownerName ]
                    let key = flowTestFileKey ownerId sourceObject.Reference
                    if old.FlowTestFiles.ContainsKey key then
                        error "FLOW_TEST_FILE_ALREADY_EXISTS" "A test-file wrapper with the same immutable source is already attached to this owner." (Some ownerName) (Some settings.Span) [] [ sourceObject.Reference.Hash ]
                    let file: FlowAuthoredTestFile =
                        { OwnerName = ownerName
                          OwnerId = ownerId
                          OwnerRevision = ownerRevision
                          SyntaxVersion = syntaxVersion
                          Reference = sourceObject.Reference
                          SourceFile = sourceFile
                          Settings = settings
                          StoredBindings = None }
                    file, settings.Tests)
            let newTestFileCaseKeys =
                newTestFiles
                |> List.collect (fun (file, tests) -> tests |> List.map (fun test -> flowAttachmentKey file.OwnerId test.CaseName))
            let allNewFlowTestCaseKeys = (newTests |> List.map fst) @ newTestFileCaseKeys
            let duplicateNewTestFileCase = allNewFlowTestCaseKeys |> List.groupBy id |> List.tryFind (fun (_, grouped) -> grouped.Length > 1)
            match duplicateNewTestFileCase with
            | Some(key, _) -> error "FLOW_ATTACHMENT_DUPLICATE_CHANGE" "A Flow project may declare one test case key across standalone tests and test-file wrappers only once." (Some key) None [] [ key ]
            | None -> ()
            for key in allNewFlowTestCaseKeys do
                if old.FlowTests.ContainsKey key then
                    error "FLOW_TEST_CASE_ALREADY_EXISTS" "A Flow test-file wrapper cannot replace an existing test attachment in an add-only project registration." (Some key) None [ "unused Flow test case key" ] [ key ]

            let records = newRecords |> List.fold (fun found (name, item, _) -> Map.add name item found) old.Records
            let scalars = newScalars |> List.fold (fun found (name, item, _) -> Map.add name item found) old.Scalars
            let enums = newEnums |> List.fold (fun found (name, item, _) -> Map.add name item found) old.Enums
            let typeSourceRows =
                (newRecords |> List.map (fun (name, _, source) -> name, source))
                @ (newScalars |> List.map (fun (name, _, source) -> name, source))
                @ (newEnums |> List.map (fun (name, _, source) -> name, source))
            let typeSources = typeSourceRows |> List.fold (fun found (name, source) -> Map.add name source found) old.TypeSources
            let words = newWordMap |> Map.fold (fun found name item -> Map.add name item found) old.Words
            let wordIds = identities |> Map.fold (fun found name identity -> Map.add name (wordIdText identity) found) old.WordIds
            let flowWords = flowWordMap |> Map.fold (fun found identity item -> Map.add identity item found) old.FlowWords
            let wrapperCaseRows =
                newTestFiles
                |> List.collect (fun (file, tests) ->
                    tests
                    |> List.map (fun test ->
                        let key = flowAttachmentKey file.OwnerId test.CaseName
                        let source: FlowLowering.FlowAttachmentSourceDocument =
                            { OwnerName = file.OwnerName
                              SyntaxVersion = file.SyntaxVersion
                              OwnerId = file.OwnerId
                              OwnerRevision = file.OwnerRevision
                              Kind = FlowLowering.FlowAttachmentKind.Test
                              CaseName = test.CaseName
                              Reference = file.Reference
                              SourceFile = file.SourceFile
                              Content = file.Settings.SourceText }
                        key, { Source = source; StoredBindings = None }))
            let flowTests =
                (newTests @ wrapperCaseRows)
                |> List.fold (fun found (key, item) -> Map.add key item found) old.FlowTests
            let flowTestFiles =
                newTestFiles
                |> List.map (fun (file, _) -> flowTestFileKey file.OwnerId file.Reference, file)
                |> List.fold (fun found (key, item) -> Map.add key item found) old.FlowTestFiles
            let flowExamples = newExamples |> List.fold (fun found (key, item) -> Map.add key item found) old.FlowExamples
            let proposed: DictionaryState =
                { old with
                    Words = words
                    WordIds = wordIds
                    Records = records
                    Scalars = scalars
                    Enums = enums
                    TypeSources = typeSources
                    FlowWords = flowWords
                    FlowTests = flowTests
                    FlowTestFiles = flowTestFiles
                    FlowExamples = flowExamples }
            let stableTypeSources =
                proposed.TypeSources
                |> Map.map (fun name authored ->
                    match proposed.Records.TryFind name, proposed.Scalars.TryFind name with
                    | Some record, _ -> { authored with ValidatorTarget = resolvedRecordValidatorTarget proposed name record.Definition }
                    | _, Some scalar -> { authored with ValidatorTarget = resolvedValidatorTarget proposed name scalar.Definition }
                    | _ -> authored)
            let proposed = { proposed with TypeSources = stableTypeSources }
            validateTypeSourceMetadata proposed
            let executable = compileRuntimeSnapshot proposed
            let frozen = frozenValidatorWords old (effectiveWords old)
            let changedWords = Set.ofList wordNames
            match Set.intersect frozen changedWords |> Set.toList with
            | name :: _ -> error "TYPE_VALIDATOR_FROZEN" $"Cannot replace validator dependency '{name}' while a nominal type is persistent." (Some name) None [] [ name ]
            | [] -> ()
            activateRuntimeSnapshot executable
            lastResults <- []
            for name in typeNames @ wordNames do log "create" name
            let payload = JsonObject()
            payload["frontend"] <- jstr "flow"
            let wordPayload = JsonArray()
            for name in wordNames do
                let item = executable.State.Words[name]
                let row = JsonObject()
                row["name"] <- jstr name
                row["id"] <- jstr (wordIdentity executable.State item)
                row["revision"] <- jint item.Revision
                wordPayload.Add row
            payload["words"] <- wordPayload
            payload["types"] <- jsonNode (typeNames |> List.sort)
            if wordNames.Length = 1 && typeNames.IsEmpty then
                let name = wordNames.Head
                let item = executable.State.Words[name]
                payload["name"] <- jstr name
                payload["id"] <- jstr (wordIdentity executable.State item)
                payload["revision"] <- jint item.Revision
                payload["tests"] <- jsonNode ((newTests |> List.map (fun (_, attachment) -> attachment.Source.CaseName)) @ (wrapperCaseRows |> List.map (fun (_, attachment) -> attachment.Source.CaseName)) |> List.sort)
                payload["examples"] <- jsonNode (newExamples |> List.map (fun (_, attachment) -> attachment.Source.CaseName) |> List.sort)
            success "defined" "Flow project declarations, attached cases, refined type metadata, and bindings validated atomically." (Some payload)

        let registerFlowProjectParsed (arguments: JsonObject) syntaxVersion (document: FlowProjectDocument) =
            if readOptionalStrictBool arguments "replace" false then
                registerFlowReplacementProjectParsed arguments syntaxVersion document
            else
                registerFlowProjectAddOnlyParsed arguments syntaxVersion document

        let registerFlowParsedLegacyWithAttachmentVersion (arguments: JsonObject) definitionSyntaxVersion attachmentSyntaxVersion =
            let old = data
            let source = requiredFlowString arguments "source"
            let parsedWord =
                match FlowParser.parseWordWithVersion definitionSyntaxVersion "<flow-definition>" source with
                | Ok definition -> definition
                | Error diagnostic -> raise (LanguageException diagnostic)
            if parsedWord.SyntaxVersion <> definitionSyntaxVersion then
                error "FLOW_VERSION_UNSUPPORTED" "Flow word syntax version does not match the selected syntaxVersion." (Some parsedWord.Name) (Some parsedWord.Span) [ string definitionSyntaxVersion ] [ string parsedWord.SyntaxVersion ]

            let replace = readOptionalStrictBool arguments "replace" false
            let temporary = readOptionalStrictBool arguments "temporary" false
            let previous = old.Words.TryFind parsedWord.Name
            if replace && previous.IsNone then
                error "FLOW_BATCH_REPLACE_MISSING" "A Flow replacement requires an existing user word." (Some parsedWord.Name) (Some parsedWord.Span) [ "existing word" ] []
            if not replace && previous.IsSome then
                error "FLOW_WORD_ALREADY_EXISTS" $"Flow word '{parsedWord.Name}' already exists; use define with replace=true and its current expectedRevision (see help topic 'replacement')." (Some parsedWord.Name) (Some parsedWord.Span) [ "unused word name or replace=true with current expectedRevision" ] [ parsedWord.Name ]
            match previous with
            | Some item when item.Builtin.IsSome || item.Status = Primitive ->
                error "FLOW_BATCH_REPLACE_PROTECTED" "Built-in and generated words cannot be replaced by Flow." (Some parsedWord.Name) (Some parsedWord.Span) [ "user-authored word" ] [ parsedWord.Name ]
            | Some item when not replace -> ()
            | _ -> ()

            let expectedRevision = if replace then Some(flowExpectedRevision arguments) else None
            let ownerId, revision, maturity, status =
                match previous with
                | Some item ->
                    let identity =
                        old.WordIds.TryFind parsedWord.Name
                        |> Option.defaultWith (fun () -> error "WORD_ID_MISSING" "A Flow replacement requires the existing stable word identity." (Some parsedWord.Name) (Some parsedWord.Span) [] [])
                    let expected = expectedRevision.Value
                    if expected <> item.Revision || expected <> item.Definition.Revision then
                        error "FLOW_BATCH_STALE_REVISION" "Flow replacement expectedRevision does not match the current owner revision." (Some parsedWord.Name) (Some parsedWord.Span) [ string item.Revision ] [ string expected ]
                    if temporary && item.Status = Persistent then
                        flowArgumentError "temporary" "false when replacing a persistent word" "true"
                    let nextStatus = if item.Status = Persistent then Candidate else item.Status
                    WordId identity, item.Revision + 1, item.Maturity, nextStatus
                | None -> WordId(newWordIdentity ()), 1, ProjectWord, (if temporary then Temporary else Candidate)

            let tests = flowSourceStrings arguments "tests"
            let examples = flowSourceStrings arguments "examples"
            let parsedTests =
                tests
                |> List.map (fun caseSource ->
                    let sourceObject = Storage.sourceObject StorageObjectKind.TestDefinition caseSource
                    let file = $"<flow:{parsedWord.Name}/{revision}>/test:{sourceObject.Reference.Hash}"
                    let definition =
                        match FlowParser.parseTestWithVersion attachmentSyntaxVersion file caseSource with
                        | Ok value -> value
                        | Error diagnostic -> raise (LanguageException diagnostic)
                    if definition.Word <> parsedWord.Name then
                        error "FLOW_ATTACHMENT_OWNER_MISMATCH" "A Flow test source must name the word being defined." (Some parsedWord.Name) (Some definition.Span) [ parsedWord.Name ] [ definition.Word ]
                    let sourceDocument: FlowLowering.FlowAttachmentSourceDocument =
                        { OwnerName = parsedWord.Name
                          SyntaxVersion = attachmentSyntaxVersion
                          OwnerId = ownerId
                          OwnerRevision = revision
                          Kind = FlowLowering.FlowAttachmentKind.Test
                          CaseName = definition.CaseName
                          Reference = sourceObject.Reference
                          SourceFile = file
                          Content = caseSource }
                    definition.CaseName, sourceDocument)
            let parsedExamples =
                examples
                |> List.map (fun caseSource ->
                    let sourceObject = Storage.sourceObject StorageObjectKind.ExampleDefinition caseSource
                    let file = $"<flow:{parsedWord.Name}/{revision}>/example:{sourceObject.Reference.Hash}"
                    let definition =
                        match FlowParser.parseExampleWithVersion attachmentSyntaxVersion file caseSource with
                        | Ok value -> value
                        | Error diagnostic -> raise (LanguageException diagnostic)
                    if definition.Word <> parsedWord.Name then
                        error "FLOW_ATTACHMENT_OWNER_MISMATCH" "A Flow example source must name the word being defined." (Some parsedWord.Name) (Some definition.Span) [ parsedWord.Name ] [ definition.Word ]
                    let sourceDocument: FlowLowering.FlowAttachmentSourceDocument =
                        { OwnerName = parsedWord.Name
                          SyntaxVersion = attachmentSyntaxVersion
                          OwnerId = ownerId
                          OwnerRevision = revision
                          Kind = FlowLowering.FlowAttachmentKind.Example
                          CaseName = definition.CaseName
                          Reference = sourceObject.Reference
                          SourceFile = file
                          Content = caseSource }
                    definition.CaseName, sourceDocument)
            let rejectDuplicateCases kind (items: (string * FlowLowering.FlowAttachmentSourceDocument) list) =
                match items |> List.groupBy fst |> List.tryFind (fun (_, grouped) -> grouped.Length > 1) with
                | Some(caseName, _) -> error "FLOW_ATTACHMENT_DUPLICATE_CHANGE" $"A Flow definition may change one {kind} case at most once." (Some parsedWord.Name) None [] [ caseName ]
                | None -> ()
            rejectDuplicateCases "test" parsedTests
            rejectDuplicateCases "example" parsedExamples

            let currentFlowWord = old.FlowWords.TryFind(wordIdText ownerId)
            match currentFlowWord with
            | Some authored when authored.Source.SyntaxVersion <> definitionSyntaxVersion && not (arguments.ContainsKey "syntaxVersion") ->
                error "FLOW_SOURCE_VERSION_CHANGE_REQUIRES_SELECTION" "Replacing a Flow word with a different syntax version requires an explicit syntaxVersion selector." (Some parsedWord.Name) (Some parsedWord.Span) [ string authored.Source.SyntaxVersion; "explicit syntaxVersion" ] [ string definitionSyntaxVersion ]
            | _ -> ()
            let currentFlowTests = old.FlowTests |> Map.filter (fun _ item -> item.Source.OwnerId = ownerId)
            let currentFlowTestFiles = old.FlowTestFiles |> Map.filter (fun _ item -> item.OwnerId = ownerId)
            let currentFlowExamples = old.FlowExamples |> Map.filter (fun _ item -> item.Source.OwnerId = ownerId)
            let suppliedTestNames = parsedTests |> List.map fst |> Set.ofList
            let suppliedExampleNames = parsedExamples |> List.map fst |> Set.ofList
            let previousWasStack = previous.IsSome && currentFlowWord.IsNone
            if previousWasStack then
                let oldTestNames = old.Tests |> Map.toSeq |> Seq.map snd |> Seq.filter (fun test -> test.Word = parsedWord.Name) |> Seq.map (fun test -> test.Name) |> Set.ofSeq
                let oldExampleNames = old.Examples |> Map.toSeq |> Seq.map snd |> Seq.filter (fun example -> example.Word = parsedWord.Name) |> Seq.map (fun example -> example.Name) |> Set.ofSeq
                let missingTests = Set.difference oldTestNames suppliedTestNames
                let missingExamples = Set.difference oldExampleNames suppliedExampleNames
                if not missingTests.IsEmpty || not missingExamples.IsEmpty then
                    error "FLOW_RUNTIME_FRONTEND_MIXED_ATTACHMENTS" "Migrating a Stack owner to Flow requires Flow-authored replacements for every inherited Stack test and example." (Some parsedWord.Name) None
                        ((missingTests |> Set.toList |> List.map (fun name -> "test/" + name)) @ (missingExamples |> Set.toList |> List.map (fun name -> "example/" + name)))
                        ((suppliedTestNames |> Set.toList |> List.map (fun name -> "test/" + name)) @ (suppliedExampleNames |> Set.toList |> List.map (fun name -> "example/" + name)))

            let removals = flowAttachmentRemovals arguments
            match removals |> List.groupBy (fun (kind, caseName, _) -> kind, caseName) |> List.tryFind (fun (_, grouped) -> grouped.Length > 1) with
            | Some((kind, caseName), _) -> error "FLOW_ATTACHMENT_DUPLICATE_CHANGE" "A Flow definition may change one attachment key at most once." (Some parsedWord.Name) None [] [ sprintf "%A/%s" kind caseName ]
            | None -> ()
            let changedKeys =
                (parsedTests |> List.map (fun (caseName, _) -> FlowLowering.FlowAttachmentKind.Test, caseName))
                @ (parsedExamples |> List.map (fun (caseName, _) -> FlowLowering.FlowAttachmentKind.Example, caseName))
            for kind, caseName, expectedHash in removals do
                if List.contains (kind, caseName) changedKeys then
                    error "FLOW_ATTACHMENT_DUPLICATE_CHANGE" "One Flow attachment key cannot be replaced and removed in the same definition request." (Some parsedWord.Name) None [] [ sprintf "%A/%s" kind caseName ]
                let inventory = if kind = FlowLowering.FlowAttachmentKind.Test then currentFlowTests else currentFlowExamples
                let key = flowAttachmentKey ownerId caseName
                let prior =
                    inventory.TryFind key
                    |> Option.defaultWith (fun () -> error "FLOW_ATTACHMENT_REMOVE_MISSING" "Attachment removal requires an existing Flow test or example source." (Some(parsedWord.Name + "/" + caseName)) None [ "existing Flow attachment" ] [])
                if prior.Source.Reference.Hash <> expectedHash then
                    error "FLOW_ATTACHMENT_STALE_SOURCE" "Attachment removal expectedSourceHash does not match the current immutable source reference." (Some(parsedWord.Name + "/" + caseName)) None [ prior.Source.Reference.Hash ] [ expectedHash ]

            let definitionObject = Storage.sourceObject StorageObjectKind.WordDefinition source
            let oldDefinitionBindings = currentFlowWord |> Option.bind (fun authored -> authored.StoredBindings)
            let storedDefinitionBindings =
                currentFlowWord
                |> Option.filter (fun authored -> authored.Source.Reference = definitionObject.Reference)
                |> Option.bind (fun _ -> oldDefinitionBindings)
            let flowSource: FlowLowering.FlowSourceDocument =
                { OwnerName = parsedWord.Name
                  SyntaxVersion = definitionSyntaxVersion
                  EffectsDeclared = parsedWord.EffectsDeclared
                  OwnerId = ownerId
                  OwnerRevision = revision
                  Reference = definitionObject.Reference
                  SourceFile = $"<flow:{parsedWord.Name}/{revision}>"
                  Content = source }
            let flowWord: FlowAuthoredWord =
                { Definition = parsedWord
                  Source = flowSource
                  StoredBindings = storedDefinitionBindings }
            let projectedDefinition: WordDefinition =
                { Name = parsedWord.Name
                  Inputs = parsedWord.Parameters |> List.map (fun parameter -> parameter.Type)
                  Outputs = parsedWord.Outputs
                  Effects = parsedWord.Effects
                  Maturity = maturity
                  Revision = revision
                  Documentation = parsedWord.Documentation
                  Body = []
                  SourceText = source
                  Span = parsedWord.Span }

            let rewrittenRemovedTestFiles =
                currentFlowTestFiles
                |> Map.toList
                |> List.map (fun (key, file) ->
                    let removedCases =
                        removals
                        |> List.choose (fun (kind, caseName, _) ->
                            if kind = FlowLowering.FlowAttachmentKind.Test
                               && (file.Settings.Tests |> List.exists (fun test -> test.CaseName = caseName)) then Some caseName
                            else None)
                    if removedCases.IsEmpty then
                        key,
                        Some
                            { file with
                                OwnerRevision = revision
                                SourceFile = $"<flow:{parsedWord.Name}/{revision}>/test-file:{file.Reference.Hash}" }
                    else
                        let remaining =
                            removedCases
                            |> List.fold (fun current caseName ->
                                match current with
                                | None -> None
                                | Some settings ->
                                    match FlowRewrite.removeTestFileCase caseName settings with
                                    | Ok updated -> updated
                                    | Error diagnostic -> raise (LanguageException diagnostic))
                                (Some file.Settings)
                        match remaining with
                        | None -> key, None
                        | Some settings ->
                            let content = FlowSource.renderTestFileSettings settings
                            let sourceObject = Storage.sourceObject StorageObjectKind.TestDefinition content
                            let sourceFile = $"<flow:{parsedWord.Name}/{revision}>/test-file:{sourceObject.Reference.Hash}"
                            let parsed =
                                match FlowParser.parseTestFileSettingsWithVersion file.SyntaxVersion sourceFile content with
                                | Ok value -> value
                                | Error diagnostic -> raise (LanguageException diagnostic)
                            key,
                            Some
                                { file with
                                    OwnerRevision = revision
                                    Reference = sourceObject.Reference
                                    SourceFile = sourceFile
                                    Settings = parsed
                                    StoredBindings = None })
                |> Map.ofList

            let priorStackTestNames =
                old.Tests |> Map.toSeq |> Seq.choose (fun (key, test) -> if test.Word = parsedWord.Name then Some key else None) |> Set.ofSeq
            let priorStackExampleNames =
                old.Examples |> Map.toSeq |> Seq.choose (fun (key, example) -> if example.Word = parsedWord.Name then Some key else None) |> Set.ofSeq
            let nextTests =
                currentFlowTests
                |> Map.filter (fun _ item -> not (List.contains (FlowLowering.FlowAttachmentKind.Test, item.Source.CaseName) (removals |> List.map (fun (kind, name, _) -> kind, name))))
                |> Map.map (fun _ item ->
                    let oldFileKey = flowTestFileKey item.Source.OwnerId item.Source.Reference
                    match rewrittenRemovedTestFiles.TryFind oldFileKey with
                    | Some None -> error "FLOW_RUNTIME_TEST_FILE_CASE_MISSING" "A surviving test-file case has no wrapper after source-level case removal." (Some(parsedWord.Name + "/" + item.Source.CaseName)) None [] [ item.Source.Reference.Hash ]
                    | Some(Some file) ->
                        { item with
                            StoredBindings = if item.Source.Reference = file.Reference then item.StoredBindings else None
                            Source =
                                { item.Source with
                                    OwnerRevision = revision
                                    Reference = file.Reference
                                    SourceFile = file.SourceFile
                                    Content = file.Settings.SourceText } }
                    | None ->
                        { item with
                            Source =
                                { item.Source with
                                    OwnerRevision = revision
                                    SourceFile = $"<flow:{parsedWord.Name}/{revision}>/test:{item.Source.Reference.Hash}" } })
                |> fun items ->
                    (parsedTests
                     |> List.map (fun (caseName, document) ->
                         let key = flowAttachmentKey ownerId caseName
                         let prior = currentFlowTests.TryFind key
                         let stored = prior |> Option.filter (fun item -> item.Source.Reference = document.Reference) |> Option.bind (fun item -> item.StoredBindings)
                         key, { Source = document; StoredBindings = stored }))
                    |> List.fold (fun found (key, item) -> Map.add key item found) items
            let nextExamples =
                currentFlowExamples
                |> Map.filter (fun _ item -> not (List.contains (FlowLowering.FlowAttachmentKind.Example, item.Source.CaseName) (removals |> List.map (fun (kind, name, _) -> kind, name))))
                |> Map.map (fun _ item ->
                    { item with
                        Source = { item.Source with OwnerRevision = revision; SourceFile = $"<flow:{parsedWord.Name}/{revision}>/example:{item.Source.Reference.Hash}" } })
                |> fun items ->
                    (parsedExamples
                     |> List.map (fun (caseName, document) ->
                         let key = flowAttachmentKey ownerId caseName
                         let prior = currentFlowExamples.TryFind key
                         let stored = prior |> Option.filter (fun item -> item.Source.Reference = document.Reference) |> Option.bind (fun item -> item.StoredBindings)
                         key, { Source = document; StoredBindings = stored }))
                    |> List.fold (fun found (key, item) -> Map.add key item found) items

            let replacementBackups =
                match previous, old.Replacements.TryFind parsedWord.Name with
                | Some item, None when item.Status = Persistent ->
                    let backup =
                        { Word = item
                          Tests = old.Tests |> Map.filter (fun _ test -> test.Word = parsedWord.Name)
                          Examples = old.Examples |> Map.filter (fun _ example -> example.Word = parsedWord.Name)
                          FlowWord = currentFlowWord
                          FlowTests = currentFlowTests
                          FlowTestFiles = currentFlowTestFiles
                          FlowExamples = currentFlowExamples }
                    Map.add parsedWord.Name backup old.Replacements
                | _ -> old.Replacements
            let nextWords = Map.add parsedWord.Name (entry projectedDefinition None status maturity revision) old.Words
            let nextWordIds = Map.add parsedWord.Name (wordIdText ownerId) old.WordIds
            let nextFlowWords =
                old.FlowWords
                |> Map.filter (fun _ authored -> authored.Source.OwnerId <> ownerId)
                |> Map.add (wordIdText ownerId) flowWord
            let nextFlowTests =
                old.FlowTests
                |> Map.filter (fun _ authored -> authored.Source.OwnerId <> ownerId)
                |> fun found -> Map.fold (fun current key authored -> Map.add key authored current) found nextTests
            let nextFlowTestFiles =
                let unrelatedTestFiles = old.FlowTestFiles |> Map.filter (fun _ file -> file.OwnerId <> ownerId)
                let rewrittenTestFiles =
                    rewrittenRemovedTestFiles
                    |> Map.toList
                    |> List.choose (fun (_, updated) -> updated |> Option.map (fun file -> flowTestFileKey file.OwnerId file.Reference, file))
                    |> Map.ofList
                Map.fold (fun found key file -> Map.add key file found) unrelatedTestFiles rewrittenTestFiles
            let nextFlowExamples =
                old.FlowExamples
                |> Map.filter (fun _ authored -> authored.Source.OwnerId <> ownerId)
                |> fun found -> Map.fold (fun current key authored -> Map.add key authored current) found nextExamples
            let proposed =
                { old with
                    Words = nextWords
                    WordIds = nextWordIds
                    Tests = old.Tests |> Map.filter (fun key _ -> not (priorStackTestNames.Contains key))
                    Examples = old.Examples |> Map.filter (fun key _ -> not (priorStackExampleNames.Contains key))
                    FlowWords = nextFlowWords
                    FlowTests = nextFlowTests
                    FlowTestFiles = nextFlowTestFiles
                    FlowExamples = nextFlowExamples
                    Replacements = replacementBackups }
            let executable = compileRuntimeSnapshot proposed
            if maturity = LibraryWord then
                rejectUnqualifiedLibraryDependencies executable.Words parsedWord.Name
            let frozen = frozenValidatorWords old (effectiveWords old)
            if frozen.Contains parsedWord.Name then
                error "TYPE_VALIDATOR_FROZEN" $"Cannot replace validator dependency '{parsedWord.Name}' while a nominal type is persistent." (Some parsedWord.Name) (Some parsedWord.Span) [] [ parsedWord.Name ]
            activateRuntimeSnapshot executable
            lastResults <- []
            log "create" parsedWord.Name
            let payload = JsonObject()
            payload["frontend"] <- jstr "flow"
            payload["name"] <- jstr parsedWord.Name
            payload["id"] <- jstr (wordIdText ownerId)
            payload["revision"] <- jint revision
            payload["tests"] <- jsonNode (executable.State.FlowTests |> Map.toList |> List.map snd |> List.filter (fun item -> item.Source.OwnerId = ownerId) |> List.map (fun item -> item.Source.CaseName) |> List.sort)
            payload["examples"] <- jsonNode (executable.State.FlowExamples |> Map.toList |> List.map snd |> List.filter (fun item -> item.Source.OwnerId = ownerId) |> List.map (fun item -> item.Source.CaseName) |> List.sort)
            success "defined" "Flow definition, tests, examples, and retained source bindings validated and staged." (Some payload)

        let registerFlowParsedLegacy (arguments: JsonObject) syntaxVersion =
            registerFlowParsedLegacyWithAttachmentVersion arguments syntaxVersion syntaxVersion

        let registerFlowTestFilesOnly (arguments: JsonObject) syntaxVersion (document: FlowProjectDocument) =
            let old = data
            if not document.Records.IsEmpty || not document.Scalars.IsEmpty || not document.Enums.IsEmpty || not document.Words.IsEmpty then
                error "FLOW_ATTACHMENT_DOCUMENT_SHAPE" "A test-file attachment document cannot also define a word or type." None None [ "test-file and attachment declarations only" ] []
            if not (flowSourceStrings arguments "tests").IsEmpty || not (flowSourceStrings arguments "examples").IsEmpty then
                error "FLOW_PROJECT_REQUEST_SHAPE" "A test-file attachment document must keep all source in its inline Flow declarations." None None [ "inline test-file source" ] [ "external tests or examples" ]
            if document.SyntaxVersion <> syntaxVersion then
                error "FLOW_VERSION_UNSUPPORTED" "Flow test-file source syntax version does not match the selected syntaxVersion." None None [ string syntaxVersion ] [ string document.SyntaxVersion ]
            let ownerNames =
                [ yield! document.Tests |> List.map (fun test -> test.Word)
                  yield! document.Examples |> List.map (fun example -> example.Word)
                  yield! document.TestFiles |> List.collect (fun settings -> settings.Tests |> List.map (fun test -> test.Word)) ]
                |> List.distinct
            let ownerName =
                match ownerNames with
                | [ name ] -> name
                | _ -> error "FLOW_ATTACHMENT_OWNER_MISMATCH" "A test-file attachment document must name exactly one owner across every nested case." None None [ "one Flow word owner" ] ownerNames
            if document.TestFiles.IsEmpty then
                error "FLOW_ATTACHMENT_DOCUMENT_SHAPE" "This registration route requires at least one test-file wrapper." (Some ownerName) None [ "test-file wrapper" ] []
            if (makeGenerated old).ContainsKey ownerName then
                error "FLOW_ATTACHMENT_OWNER_NOT_FLOW_WORD" $"Flow attachment owner '{ownerName}' is a generated type word and cannot own authored Flow cases." (Some ownerName) None [ "user-authored Flow word" ] [ "generated type word" ]
            let ownerWord =
                old.Words.TryFind ownerName
                |> Option.defaultWith (fun () -> error "FLOW_ATTACHMENT_OWNER_NOT_FOUND" $"Flow attachment owner '{ownerName}' is not a current user word." (Some ownerName) None [ "existing Flow user word" ] [])
            if ownerWord.Builtin.IsSome || ownerWord.Status = Primitive then
                error "FLOW_ATTACHMENT_OWNER_NOT_FLOW_WORD" $"Flow attachment owner '{ownerName}' is a primitive or generated word and cannot own authored Flow cases." (Some ownerName) None [ "user-authored Flow word" ] [ ownerName ]
            let ownerIdText =
                old.WordIds.TryFind ownerName
                |> Option.defaultWith (fun () -> error "FLOW_ATTACHMENT_OWNER_NOT_FLOW_WORD" $"Flow attachment owner '{ownerName}' has no stable user-word identity." (Some ownerName) None [ "existing Flow user word" ] [])
            let ownerId = WordId ownerIdText
            let currentFlowWord =
                old.FlowWords.TryFind ownerIdText
                |> Option.defaultWith (fun () -> error "FLOW_ATTACHMENT_OWNER_NOT_FLOW_WORD" $"Flow attachment owner '{ownerName}' is not authored in Flow." (Some ownerName) None [ "Flow-authored word" ] [ "Stack definition" ])
            if currentFlowWord.Source.OwnerName <> ownerName || currentFlowWord.Source.OwnerRevision <> ownerWord.Revision then
                error "FLOW_ATTACHMENT_OWNER_STALE" $"Flow attachment owner '{ownerName}' does not match its current immutable word revision." (Some ownerName) None [ $"{ownerName}@{ownerWord.Revision}" ] [ $"{currentFlowWord.Source.OwnerName}@{currentFlowWord.Source.OwnerRevision}" ]
            let currentFlowTests = old.FlowTests |> Map.filter (fun _ item -> item.Source.OwnerId = ownerId)
            let currentFlowFiles = old.FlowTestFiles |> Map.filter (fun _ file -> file.OwnerId = ownerId)
            let currentFlowExamples = old.FlowExamples |> Map.filter (fun _ item -> item.Source.OwnerId = ownerId)
            let replace = readOptionalStrictBool arguments "replace" false
            let removals = flowAttachmentRemovals arguments
            if arguments.ContainsKey "expectedRevision" && not replace then
                error "FLOW_ATTACHMENT_CAS_REQUIRED" "An explicit expectedRevision for test-file edits must be paired with replace=true." (Some ownerName) None [ "replace=true with expectedRevision" ] [ "expectedRevision without replace=true" ]
            match removals |> List.groupBy (fun (kind, caseName, _) -> kind, caseName) |> List.tryFind (fun (_, grouped) -> grouped.Length > 1) with
            | Some((kind, caseName), _) -> error "FLOW_ATTACHMENT_DUPLICATE_CHANGE" "A Flow test-file document may change one attachment key at most once." (Some ownerName) None [] [ sprintf "%A/%s" kind caseName ]
            | None -> ()

            let incomingFileSettings =
                document.TestFiles
                |> List.map (fun parsed ->
                    if parsed.SyntaxVersion <> syntaxVersion then
                        error "FLOW_VERSION_UNSUPPORTED" "Flow test-file syntax version does not match the selected syntaxVersion." (Some ownerName) (Some parsed.Span) [ string syntaxVersion ] [ string parsed.SyntaxVersion ]
                    let sourceObject = Storage.sourceObject StorageObjectKind.TestDefinition parsed.SourceText
                    let sourceFile = $"<flow:{ownerName}/{ownerWord.Revision + 1}>/test-file:{sourceObject.Reference.Hash}"
                    let settings =
                        match FlowParser.parseTestFileSettingsWithVersion syntaxVersion sourceFile parsed.SourceText with
                        | Ok value -> value
                        | Error diagnostic -> raise (LanguageException diagnostic)
                    let owners = settings.Tests |> List.map (fun test -> test.Word) |> List.distinct
                    if owners <> [ ownerName ] then
                        error "FLOW_TEST_FILE_OWNER_MISMATCH" "A test-file wrapper must retain exactly its existing Flow owner." (Some ownerName) (Some settings.Span) [ ownerName ] owners
                    { OwnerName = ownerName
                      OwnerId = ownerId
                      OwnerRevision = ownerWord.Revision + 1
                      SyntaxVersion = syntaxVersion
                      Reference = sourceObject.Reference
                      SourceFile = sourceFile
                      Settings = settings
                      StoredBindings = None })
            let incomingScopes = incomingFileSettings |> List.map (fun file -> file.Settings.ScopeName)
            if (incomingScopes |> List.distinct).Length <> incomingScopes.Length then
                error "FLOW_TEST_FILE_SCOPE_DUPLICATE" "A test-file registration may declare each wrapper label once." (Some ownerName) None [] incomingScopes
            let replacementScopeNames =
                incomingScopes
                |> List.choose (fun scope ->
                    currentFlowFiles
                    |> Map.toList
                    |> List.map snd
                    |> List.tryFind (fun file -> file.Settings.ScopeName = scope)
                    |> Option.map (fun file -> file.Settings.ScopeName))
                |> Set.ofList
            let replacingFiles =
                currentFlowFiles
                |> Map.toList
                |> List.map snd
                |> List.filter (fun file -> replacementScopeNames.Contains file.Settings.ScopeName)
            let replacedFileKeys = replacingFiles |> List.map (fun file -> flowTestFileKey file.OwnerId file.Reference) |> Set.ofList
            let replacedFileCaseKeys =
                replacingFiles
                |> List.collect (fun file -> file.Settings.Tests |> List.map (fun test -> flowAttachmentKey file.OwnerId test.CaseName))
                |> Set.ofList
            let incomingTests =
                document.Tests
                |> List.map (fun parsed ->
                    let sourceObject = Storage.sourceObject StorageObjectKind.TestDefinition parsed.SourceText
                    let sourceFile = $"<flow:{ownerName}/{ownerWord.Revision + 1}>/test:{sourceObject.Reference.Hash}"
                    let definition =
                        match FlowParser.parseTestWithVersion syntaxVersion sourceFile parsed.SourceText with
                        | Ok value when value.Word = ownerName -> value
                        | Ok value -> error "FLOW_ATTACHMENT_OWNER_MISMATCH" "A standalone Flow test source changed its declared owner during validation." (Some ownerName) (Some value.Span) [ ownerName ] [ value.Word ]
                        | Error diagnostic -> raise (LanguageException diagnostic)
                    let source: FlowLowering.FlowAttachmentSourceDocument =
                        { OwnerName = ownerName
                          SyntaxVersion = syntaxVersion
                          OwnerId = ownerId
                          OwnerRevision = ownerWord.Revision + 1
                          Kind = FlowLowering.FlowAttachmentKind.Test
                          CaseName = definition.CaseName
                          Reference = sourceObject.Reference
                          SourceFile = sourceFile
                          Content = parsed.SourceText }
                    flowAttachmentKey ownerId definition.CaseName, { Source = source; StoredBindings = None })
            let incomingExamples =
                document.Examples
                |> List.map (fun parsed ->
                    let sourceObject = Storage.sourceObject StorageObjectKind.ExampleDefinition parsed.SourceText
                    let sourceFile = $"<flow:{ownerName}/{ownerWord.Revision + 1}>/example:{sourceObject.Reference.Hash}"
                    let definition =
                        match FlowParser.parseExampleWithVersion syntaxVersion sourceFile parsed.SourceText with
                        | Ok value when value.Word = ownerName -> value
                        | Ok value -> error "FLOW_ATTACHMENT_OWNER_MISMATCH" "A standalone Flow example source changed its declared owner during validation." (Some ownerName) (Some value.Span) [ ownerName ] [ value.Word ]
                        | Error diagnostic -> raise (LanguageException diagnostic)
                    let source: FlowLowering.FlowAttachmentSourceDocument =
                        { OwnerName = ownerName
                          SyntaxVersion = syntaxVersion
                          OwnerId = ownerId
                          OwnerRevision = ownerWord.Revision + 1
                          Kind = FlowLowering.FlowAttachmentKind.Example
                          CaseName = definition.CaseName
                          Reference = sourceObject.Reference
                          SourceFile = sourceFile
                          Content = parsed.SourceText }
                    flowAttachmentKey ownerId definition.CaseName, { Source = source; StoredBindings = None })
            let incomingFileCases = incomingFileSettings |> List.collect (fun file -> file.Settings.Tests |> List.map (fun test -> flowAttachmentKey ownerId test.CaseName))
            let incomingTestKeys = (incomingTests |> List.map fst) @ incomingFileCases
            let incomingExampleKeys = incomingExamples |> List.map fst
            let allIncomingKeys = incomingTestKeys @ incomingExampleKeys
            if (allIncomingKeys |> List.distinct).Length <> allIncomingKeys.Length then
                error "FLOW_ATTACHMENT_DUPLICATE_CHANGE" "A Flow test-file document may declare each case key only once." (Some ownerName) None [] allIncomingKeys
            let removalsByKey = removals |> List.map (fun (kind, caseName, _) -> (kind, flowAttachmentKey ownerId caseName)) |> Set.ofList
            if incomingTests |> List.exists (fun (key, _) -> removalsByKey.Contains(FlowLowering.FlowAttachmentKind.Test, key))
               || incomingFileCases |> List.exists (fun key -> removalsByKey.Contains(FlowLowering.FlowAttachmentKind.Test, key))
               || incomingExamples |> List.exists (fun (key, _) -> removalsByKey.Contains(FlowLowering.FlowAttachmentKind.Example, key)) then
                error "FLOW_ATTACHMENT_DUPLICATE_CHANGE" "One Flow attachment key cannot be replaced and removed in the same test-file request." (Some ownerName) None [] []

            let changedCaseKeys =
                currentFlowTests
                |> Map.toList
                |> List.choose (fun (key, _) -> if replacedFileCaseKeys.Contains key then Some(FlowLowering.FlowAttachmentKind.Test, key) else None)
                |> fun values -> values @ (incomingTests |> List.map (fun (key, _) -> FlowLowering.FlowAttachmentKind.Test, key))
                |> fun values -> values @ (incomingFileCases |> List.map (fun key -> FlowLowering.FlowAttachmentKind.Test, key))
                |> fun values -> values @ (incomingExamples |> List.map (fun (key, _) -> FlowLowering.FlowAttachmentKind.Example, key))
            let removalChanges = removals |> List.map (fun (kind, caseName, _) -> kind, flowAttachmentKey ownerId caseName)
            if removals |> List.exists (fun (kind, caseName, _) -> List.contains (kind, flowAttachmentKey ownerId caseName) changedCaseKeys) then
                error "FLOW_ATTACHMENT_DUPLICATE_CHANGE" "One Flow attachment key cannot be replaced and removed in the same test-file request." (Some ownerName) None [] []

            let caseCollisionKeys =
                let tests =
                    currentFlowTests
                    |> Map.toList
                    |> List.choose (fun (key, item) ->
                        if item.Source.OwnerId = ownerId && not (replacedFileCaseKeys.Contains key) && not (removalChanges |> List.contains (FlowLowering.FlowAttachmentKind.Test, key)) then Some(FlowLowering.FlowAttachmentKind.Test, key)
                        else None)
                let examples =
                    currentFlowExamples
                    |> Map.toList
                    |> List.choose (fun (key, _) -> if not (removalChanges |> List.contains (FlowLowering.FlowAttachmentKind.Example, key)) then Some(FlowLowering.FlowAttachmentKind.Example, key) else None)
                tests @ examples
            let collisions =
                caseCollisionKeys
                |> List.choose (fun (kind, key) -> if List.contains (kind, key) changedCaseKeys then Some(sprintf "%A/%s" kind key) else None)
            let hasRemovals = not removals.IsEmpty
            let needsCas = not replacementScopeNames.IsEmpty || not collisions.IsEmpty || hasRemovals
            if needsCas && (not replace || not (arguments.ContainsKey "expectedRevision")) then
                error "FLOW_ATTACHMENT_CAS_REQUIRED" "Replacing a shared test-file scope, colliding case, or removing an attachment requires replace=true and the current expectedRevision." (Some ownerName) None [ "replace=true and expectedRevision" ] (collisions @ (if replacementScopeNames.IsEmpty then [] else replacementScopeNames |> Set.toList) @ (if hasRemovals then [ "attachment removal" ] else []))
            if replace && not (arguments.ContainsKey "expectedRevision") then
                error "FLOW_ATTACHMENT_CAS_REQUIRED" "An explicit test-file replacement requires the owner's current expectedRevision." (Some ownerName) None [ "expectedRevision" ] []
            if arguments.ContainsKey "expectedRevision" then
                let expected = flowExpectedRevision arguments
                if expected <> ownerWord.Revision then
                    error "FLOW_BATCH_STALE_REVISION" "Test-file edit expectedRevision does not match the current Flow owner revision." (Some ownerName) None [ string ownerWord.Revision ] [ string expected ]
            for kind, caseName, expectedHash in removals do
                let key = flowAttachmentKey ownerId caseName
                let prior =
                    (if kind = FlowLowering.FlowAttachmentKind.Test then currentFlowTests.TryFind key
                     else currentFlowExamples.TryFind key)
                    |> Option.defaultWith (fun () -> error "FLOW_ATTACHMENT_REMOVE_MISSING" "Attachment removal requires an existing Flow test or example source." (Some(ownerName + "/" + caseName)) None [ "existing Flow attachment" ] [])
                if prior.Source.Reference.Hash <> expectedHash then
                    error "FLOW_ATTACHMENT_STALE_SOURCE" "Attachment removal expectedSourceHash does not match the current immutable source reference." (Some(ownerName + "/" + caseName)) None [ prior.Source.Reference.Hash ] [ expectedHash ]

            let mutable nextFiles = currentFlowFiles |> Map.filter (fun key file -> not (replacedFileKeys.Contains key))
            let mutable nextTests = currentFlowTests |> Map.filter (fun key _ -> not (replacedFileCaseKeys.Contains key))
            let mutable nextExamples = currentFlowExamples
            let removeTestCase caseName =
                let key = flowAttachmentKey ownerId caseName
                match nextTests.TryFind key with
                | None -> error "FLOW_ATTACHMENT_REMOVE_MISSING" "Attachment removal requires an existing Flow test source." (Some(ownerName + "/" + caseName)) None [ "existing Flow test" ] []
                | Some attachment ->
                    let fileKey = flowTestFileKey ownerId attachment.Source.Reference
                    match nextFiles.TryFind fileKey with
                    | None -> nextTests <- Map.remove key nextTests
                    | Some file ->
                        let updated =
                            match FlowRewrite.removeTestFileCase caseName file.Settings with
                            | Ok result -> result
                            | Error diagnostic -> raise (LanguageException diagnostic)
                        nextTests <- nextTests |> Map.filter (fun _ item -> item.Source.OwnerId <> ownerId || item.Source.Reference <> file.Reference)
                        match updated with
                        | None -> nextFiles <- Map.remove fileKey nextFiles
                        | Some settings ->
                            let content = FlowSource.renderTestFileSettings settings
                            let sourceObject = Storage.sourceObject StorageObjectKind.TestDefinition content
                            let sourceFile = $"<flow:{ownerName}/{ownerWord.Revision + 1}>/test-file:{sourceObject.Reference.Hash}"
                            let settings =
                                match FlowParser.parseTestFileSettingsWithVersion syntaxVersion sourceFile content with
                                | Ok parsed -> parsed
                                | Error diagnostic -> raise (LanguageException diagnostic)
                            let updatedFile = { file with Reference = sourceObject.Reference; SourceFile = sourceFile; Settings = settings; StoredBindings = None }
                            let newFileKey = flowTestFileKey ownerId updatedFile.Reference
                            nextFiles <- nextFiles |> Map.remove fileKey |> Map.add newFileKey updatedFile
                            for test in settings.Tests do
                                let source: FlowLowering.FlowAttachmentSourceDocument =
                                    { OwnerName = ownerName
                                      SyntaxVersion = syntaxVersion
                                      OwnerId = ownerId
                                      OwnerRevision = ownerWord.Revision + 1
                                      Kind = FlowLowering.FlowAttachmentKind.Test
                                      CaseName = test.CaseName
                                      Reference = updatedFile.Reference
                                      SourceFile = sourceFile
                                      Content = content }
                                nextTests <- Map.add (flowAttachmentKey ownerId test.CaseName) { Source = source; StoredBindings = None } nextTests
            for kind, caseName, _ in removals do
                match kind with
                | FlowLowering.FlowAttachmentKind.Test -> removeTestCase caseName
                | FlowLowering.FlowAttachmentKind.Example -> nextExamples <- Map.remove (flowAttachmentKey ownerId caseName) nextExamples

            for key, item in incomingTests do
                match nextTests.TryFind key with
                | Some prior when currentFlowFiles.ContainsKey(flowTestFileKey ownerId prior.Source.Reference) ->
                    error "FLOW_TEST_FILE_EDIT_REQUIRED" "A case in a shared wrapper must be replaced by submitting its complete test-file source." (Some key) None [ "complete wrapper source" ] [ "standalone test source" ]
                | Some _ when replace -> ()
                | Some _ -> error "FLOW_TEST_CASE_ALREADY_EXISTS" "A Flow test case already exists; use replace=true with the current expectedRevision or submit the complete wrapper." (Some key) None [] [ key ]
                | None -> ()
                nextTests <- Map.add key item nextTests
            for key, item in incomingExamples do
                if nextExamples.ContainsKey key && not replace then
                    error "FLOW_EXAMPLE_CASE_ALREADY_EXISTS" "A Flow example case already exists; use replace=true with the current expectedRevision." (Some key) None [] [ key ]
                nextExamples <- Map.add key item nextExamples
            for file in incomingFileSettings do
                let fileCaseKeys = file.Settings.Tests |> List.map (fun test -> flowAttachmentKey ownerId test.CaseName)
                for key in fileCaseKeys do
                    match nextTests.TryFind key with
                    | Some prior when currentFlowFiles.ContainsKey(flowTestFileKey ownerId prior.Source.Reference) ->
                        error "FLOW_TEST_FILE_SCOPE_REQUIRED" "A case in another shared wrapper cannot be shadowed; replace or edit that wrapper by its label." (Some key) (Some file.Settings.Span) [ "unused test case key or matching wrapper label" ] [ key ]
                    | Some _ when replace -> nextTests <- Map.remove key nextTests
                    | Some _ -> error "FLOW_TEST_CASE_ALREADY_EXISTS" "A Flow test case already exists; use replace=true with the current expectedRevision." (Some key) (Some file.Settings.Span) [] [ key ]
                    | None -> ()
                let fileKey = flowTestFileKey ownerId file.Reference
                nextFiles <- Map.add fileKey file nextFiles
                for test in file.Settings.Tests do
                    let source: FlowLowering.FlowAttachmentSourceDocument =
                        { OwnerName = ownerName
                          SyntaxVersion = syntaxVersion
                          OwnerId = ownerId
                          OwnerRevision = ownerWord.Revision + 1
                          Kind = FlowLowering.FlowAttachmentKind.Test
                          CaseName = test.CaseName
                          Reference = file.Reference
                          SourceFile = file.SourceFile
                          Content = file.Settings.SourceText }
                    nextTests <- Map.add (flowAttachmentKey ownerId test.CaseName) { Source = source; StoredBindings = None } nextTests

            let revision = ownerWord.Revision + 1
            let status = if ownerWord.Status = Persistent then Candidate else ownerWord.Status
            let ownerSourceFile = $"<flow:{ownerName}/{revision}>"
            let ownerDefinition =
                match FlowParser.parseWordWithVersion currentFlowWord.Source.SyntaxVersion ownerSourceFile currentFlowWord.Source.Content with
                | Ok parsed when parsed.Name = ownerName -> parsed
                | Ok parsed -> error "FLOW_RUNTIME_OWNER_MISMATCH" "The current Flow owner source changed its name while updating test-file metadata." (Some ownerName) (Some parsed.Span) [ ownerName ] [ parsed.Name ]
                | Error diagnostic -> raise (LanguageException diagnostic)
            let nextFlowWord =
                { currentFlowWord with
                    Definition = ownerDefinition
                    Source = { currentFlowWord.Source with OwnerRevision = revision; SourceFile = ownerSourceFile } }
            let nextFiles =
                nextFiles
                |> Map.map (fun _ file ->
                    if file.OwnerRevision = ownerWord.Revision + 1 then file
                    else
                        { file with
                            OwnerRevision = revision
                            SourceFile = $"<flow:{ownerName}/{revision}>/test-file:{file.Reference.Hash}" })
            let nextTests =
                nextTests
                |> Map.map (fun _ item ->
                    let fileKey = flowTestFileKey ownerId item.Source.Reference
                    let isWrapper = nextFiles.ContainsKey fileKey
                    { item with
                        Source =
                            { item.Source with
                                OwnerRevision = revision
                                SourceFile =
                                    if isWrapper then nextFiles[fileKey].SourceFile
                                    else $"<flow:{ownerName}/{revision}>/test:{item.Source.Reference.Hash}" } })
            let nextExamples = nextExamples |> Map.map (fun _ item -> { item with Source = { item.Source with OwnerRevision = revision; SourceFile = $"<flow:{ownerName}/{revision}>/example:{item.Source.Reference.Hash}" } })
            let oldTests = old.Tests |> Map.filter (fun _ test -> test.Word = ownerName)
            let oldExamples = old.Examples |> Map.filter (fun _ example -> example.Word = ownerName)
            let replacementBackups =
                match ownerWord.Status, old.Replacements.TryFind ownerName with
                | Persistent, None ->
                    let backup =
                        { Word = ownerWord
                          Tests = oldTests
                          Examples = oldExamples
                          FlowWord = Some currentFlowWord
                          FlowTests = currentFlowTests
                          FlowTestFiles = currentFlowFiles
                          FlowExamples = currentFlowExamples }
                    Map.add ownerName backup old.Replacements
                | _ -> old.Replacements
            let finalTestKeys = nextTests |> Map.toList |> List.map fst |> Set.ofList
            let finalExampleKeys = nextExamples |> Map.toList |> List.map fst |> Set.ofList
            let projectedOwner =
                { ownerWord.Definition with
                    Revision = revision
                    SourceText = currentFlowWord.Source.Content
                    Span = ownerDefinition.Span }
            let words = Map.add ownerName { ownerWord with Definition = projectedOwner; Revision = revision; Status = status } old.Words
            let proposed =
                { old with
                    Words = words
                    FlowWords = Map.add ownerIdText nextFlowWord old.FlowWords
                    FlowTests = (old.FlowTests |> Map.filter (fun _ item -> item.Source.OwnerId <> ownerId)) |> fun current -> Map.fold (fun found key value -> Map.add key value found) current nextTests
                    FlowTestFiles = (old.FlowTestFiles |> Map.filter (fun _ file -> file.OwnerId <> ownerId)) |> fun current -> Map.fold (fun found key value -> Map.add key value found) current nextFiles
                    FlowExamples = (old.FlowExamples |> Map.filter (fun _ item -> item.Source.OwnerId <> ownerId)) |> fun current -> Map.fold (fun found key value -> Map.add key value found) current nextExamples
                    Tests = old.Tests |> Map.filter (fun _ test -> test.Word <> ownerName || finalTestKeys.Contains(test.Word + "/" + test.Name))
                    Examples = old.Examples |> Map.filter (fun _ example -> example.Word <> ownerName || finalExampleKeys.Contains(example.Word + "/" + example.Name))
                    Replacements = replacementBackups }
            let executable = compileRuntimeSnapshot proposed
            activateRuntimeSnapshot executable
            lastResults <- []
            log "create" ownerName
            let payload = JsonObject()
            payload["frontend"] <- jstr "flow"
            payload["name"] <- jstr ownerName
            payload["id"] <- jstr ownerIdText
            payload["revision"] <- jint revision
            payload["tests"] <- jsonNode (executable.State.FlowTests |> Map.toList |> List.map snd |> List.filter (fun item -> item.Source.OwnerId = ownerId) |> List.map (fun item -> item.Source.CaseName) |> List.sort)
            payload["testFiles"] <- jsonNode (executable.State.FlowTestFiles |> Map.toList |> List.map snd |> List.filter (fun file -> file.OwnerId = ownerId) |> List.map (fun file -> file.Settings.ScopeName) |> List.sort)
            success "defined" "Flow test-file source, cases, replacement targets, and bindings validated atomically." (Some payload)

        let registerFlowAttachmentsOnlyLegacy (arguments: JsonObject) syntaxVersion (document: FlowProjectDocument) =
            let old = data
            let attachmentSources =
                (document.Tests |> List.map (fun item -> item.Word))
                @ (document.Examples |> List.map (fun item -> item.Word))
            let owners = attachmentSources |> Set.ofList
            if owners.Count <> 1 then
                error "FLOW_ATTACHMENT_OWNER_MISMATCH" "A Flow attachment-only document must name exactly one owner across its tests and examples." None None [ "one Flow word owner" ] (owners |> Set.toList)
            let ownerName = Set.minElement owners
            if not (document.Records.IsEmpty && document.Scalars.IsEmpty && document.Enums.IsEmpty && document.Words.IsEmpty)
               || (document.Tests.IsEmpty && document.Examples.IsEmpty) then
                error "FLOW_ATTACHMENT_DOCUMENT_SHAPE" "An attachment-only Flow document must contain tests and/or examples for one existing Flow word and no declarations." (Some ownerName) None [ "test/example declarations only" ] []
            if not (flowSourceStrings arguments "tests").IsEmpty || not (flowSourceStrings arguments "examples").IsEmpty then
                error "FLOW_PROJECT_REQUEST_SHAPE" "An attachment-only Flow document must keep its test and example sources inline; external attachment arrays would make ownership ambiguous." (Some ownerName) None [ "inline Flow test/example sources" ] [ "external tests or examples" ]

            if (makeGenerated old).ContainsKey ownerName then
                error "FLOW_ATTACHMENT_OWNER_NOT_FLOW_WORD" $"Flow attachment owner '{ownerName}' is a generated type word and cannot own authored Flow cases." (Some ownerName) None [ "user-authored Flow word" ] [ "generated type word" ]
            let ownerWord =
                old.Words.TryFind ownerName
                |> Option.defaultWith (fun () -> error "FLOW_ATTACHMENT_OWNER_NOT_FOUND" $"Flow attachment owner '{ownerName}' is not a current user word." (Some ownerName) None [ "existing Flow user word" ] [])
            if ownerWord.Status = Primitive || ownerWord.Builtin.IsSome then
                error "FLOW_ATTACHMENT_OWNER_NOT_FLOW_WORD" $"Flow attachment owner '{ownerName}' is a primitive or generated word and cannot own authored Flow cases." (Some ownerName) None [ "user-authored Flow word" ] [ ownerName ]
            let ownerSyntaxVersion =
                old.WordIds.TryFind ownerName
                |> Option.bind (fun identity -> old.FlowWords.TryFind identity)
                |> Option.map (fun authored -> authored.Definition.SyntaxVersion)
                |> Option.defaultWith (fun () -> error "FLOW_ATTACHMENT_OWNER_NOT_FLOW_WORD" $"Flow attachment owner '{ownerName}' has no Flow source version." (Some ownerName) None [ "Flow-authored word" ] [])
            let ownerIdText =
                old.WordIds.TryFind ownerName
                |> Option.defaultWith (fun () -> error "FLOW_ATTACHMENT_OWNER_NOT_FLOW_WORD" $"Flow attachment owner '{ownerName}' has no stable user-word identity." (Some ownerName) None [ "existing Flow user word" ] [])
            let ownerId = WordId ownerIdText
            let authored =
                old.FlowWords.TryFind (wordIdText ownerId)
                |> Option.defaultWith (fun () -> error "FLOW_ATTACHMENT_OWNER_NOT_FLOW_WORD" $"Flow attachment owner '{ownerName}' is not authored in Flow." (Some ownerName) None [ "existing Flow user word" ] [ "Stack or generated word" ])
            if authored.Source.OwnerName <> ownerName || authored.Source.OwnerRevision <> ownerWord.Revision then
                error "FLOW_ATTACHMENT_OWNER_STALE" $"Flow attachment owner '{ownerName}' does not match its current immutable word revision." (Some ownerName) None [ $"{ownerName}@{ownerWord.Revision}" ] [ $"{authored.Source.OwnerName}@{authored.Source.OwnerRevision}" ]

            let replace = readOptionalStrictBool arguments "replace" false
            let removals = flowAttachmentRemovals arguments
            let existingTestNames =
                old.FlowTests
                |> Map.toList
                |> List.choose (fun (_, item) -> if item.Source.OwnerId = ownerId then Some item.Source.CaseName else None)
                |> Set.ofList
            let existingExampleNames =
                old.FlowExamples
                |> Map.toList
                |> List.choose (fun (_, item) -> if item.Source.OwnerId = ownerId then Some item.Source.CaseName else None)
                |> Set.ofList
            let testCollisions = document.Tests |> List.map (fun item -> item.CaseName) |> Set.ofList |> Set.intersect existingTestNames
            let exampleCollisions = document.Examples |> List.map (fun item -> item.CaseName) |> Set.ofList |> Set.intersect existingExampleNames
            let hasCaseCollision = not testCollisions.IsEmpty || not exampleCollisions.IsEmpty
            let hasRemovals = not removals.IsEmpty
            let hasExpectedRevision = arguments.ContainsKey "expectedRevision"
            if hasExpectedRevision && not replace then
                error "FLOW_ATTACHMENT_CAS_REQUIRED" "An explicit expectedRevision for attachment edits must be paired with replace=true." (Some ownerName) None [ "replace=true with expectedRevision" ] [ "expectedRevision without replace=true" ]
            if (hasCaseCollision || hasRemovals) && (not replace || not hasExpectedRevision) then
                error "FLOW_ATTACHMENT_CAS_REQUIRED" "Replacing or removing an existing Flow case requires replace=true and the current expectedRevision; add-only case documents may capture the current revision." (Some ownerName) None
                    [ "replace=true and expectedRevision" ]
                    ((testCollisions |> Set.toList |> List.map (fun name -> "test/" + name))
                     @ (exampleCollisions |> Set.toList |> List.map (fun name -> "example/" + name))
                     @ (if hasRemovals then [ "attachment removal" ] else []))

            let routed = JsonObject()
            for KeyValue(key, value) in arguments do
                if key <> "source" && key <> "tests" && key <> "examples" then
                    routed[key] <- if isNull value then null else JsonNode.Parse(value.ToJsonString())
            routed["source"] <- jstr authored.Source.Content
            routed["tests"] <- jsonNode (document.Tests |> List.map (fun item -> item.SourceText))
            routed["examples"] <- jsonNode (document.Examples |> List.map (fun item -> item.SourceText))
            if not replace && not hasExpectedRevision then
                routed["replace"] <- jbool true
                routed["expectedRevision"] <- jint ownerWord.Revision
            registerFlowParsedLegacyWithAttachmentVersion routed ownerSyntaxVersion syntaxVersion

        let registerFlowAttachmentsOnly (arguments: JsonObject) syntaxVersion (document: FlowProjectDocument) =
            if not document.TestFiles.IsEmpty then
                registerFlowTestFilesOnly arguments syntaxVersion document
            else
                registerFlowAttachmentsOnlyLegacy arguments syntaxVersion document

        let registerFlowParsed (arguments: JsonObject) syntaxVersion =
            let source = requiredFlowString arguments "source"
            let document =
                match FlowParser.parseDocumentWithVersion syntaxVersion "<flow-project>" source with
                | Ok value -> value
                | Error diagnostic -> raise (LanguageException diagnostic)
            let replaceWasRequested =
                match arguments["replace"] with
                | :? JsonValue as value ->
                    let mutable requested = false
                    value.TryGetValue<bool>(&requested) && requested
                | _ -> false
            if replaceWasRequested then rejectFlowTypeReplacement document
            if arguments.ContainsKey "expectedRevisions" && document.Words.Length < 2 then
                error "FLOW_PROJECT_REPLACEMENT_CAS_SHAPE" "expectedRevisions is only for an atomic replacement of at least two words declared in the same source document." None None [ "multiword source with one expectedRevisions entry per word" ] [ string document.Words.Length + " word declarations" ]
            if document.Records.IsEmpty && document.Scalars.IsEmpty && document.Enums.IsEmpty && document.Words.IsEmpty && (not document.Tests.IsEmpty || not document.Examples.IsEmpty || not document.TestFiles.IsEmpty) then
                registerFlowAttachmentsOnly arguments syntaxVersion document
            elif document.Records.IsEmpty && document.Scalars.IsEmpty && document.Enums.IsEmpty && document.Words.Length = 1 && document.TestFiles.IsEmpty then
                if document.Tests.IsEmpty && document.Examples.IsEmpty then
                    // Preserve the byte-for-byte legacy source object for the established
                    // one-word request shape, including comments and trailing whitespace.
                    registerFlowParsedLegacy arguments syntaxVersion
                else
                    // Inline cases are a convenient document form for a single owner. The
                    // existing owner CAS implementation remains the authority for updates.
                    let routed = JsonObject()
                    for KeyValue(key, value) in arguments do
                        if key <> "source" && key <> "tests" && key <> "examples" then
                            routed[key] <- if isNull value then null else JsonNode.Parse(value.ToJsonString())
                    routed["source"] <- jstr document.Words.Head.SourceText
                    routed["tests"] <- jsonNode ((flowSourceStrings arguments "tests") @ (document.Tests |> List.map (fun test -> test.SourceText)))
                    routed["examples"] <- jsonNode ((flowSourceStrings arguments "examples") @ (document.Examples |> List.map (fun example -> example.SourceText)))
                    registerFlowParsedLegacy routed syntaxVersion
            else
                registerFlowProjectParsed arguments syntaxVersion document

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
            words, Discovery.build words data.Records data.Scalars data.Enums

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

        let sourceFormatJson (sourceFormat: SourceFormat) =
            let payload = JsonObject()
            payload["frontend"] <- jstr (match sourceFormat.Frontend with | SourceFrontend.Stack -> "stack" | SourceFrontend.Flow -> "flow")
            payload["version"] <- jint sourceFormat.Version
            payload :> JsonNode

        let storedCallTargetJson (target: StoredCallTarget) =
            let payload = JsonObject()
            match target with
            | StoredCallTarget.UserWord identity ->
                payload["kind"] <- jstr "userWord"
                payload["identity"] <- jstr identity
            | StoredCallTarget.Primitive identity ->
                payload["kind"] <- jstr "primitive"
                payload["identity"] <- jstr identity
            | StoredCallTarget.GeneratedWord identity ->
                payload["kind"] <- jstr "generatedWord"
                payload["identity"] <- jstr identity
            payload :> JsonNode

        let describeTypeJson typeName =
            if not ((knownTypes data).Contains typeName) then
                error "DISCOVERY_UNKNOWN_TYPE" $"Type source '{typeName}' is not defined." (Some typeName) None [ "declared record, scalar, or enum type" ] []
            let typeStatus =
                match data.Records.TryFind typeName, data.Scalars.TryFind typeName, data.Enums.TryFind typeName with
                | Some item, _, _ -> Some item.Status
                | _, Some item, _ -> Some item.Status
                | _, _, Some item -> Some item.Status
                | _ -> None
            let authored = data.TypeSources.TryFind typeName |> Option.defaultValue (defaultTypeSource data typeName)
            let effectiveValidatorTarget =
                match data.Records.TryFind typeName, data.Scalars.TryFind typeName with
                | Some record, _ -> resolvedRecordValidatorTarget data typeName record.Definition
                | _, Some scalar -> resolvedValidatorTarget data typeName scalar.Definition
                | _ -> authored.ValidatorTarget
            let authored = { authored with ValidatorTarget = effectiveValidatorTarget }
            let exactSource = Storage.sourceObject StorageObjectKind.TypeDefinition authored.Content
            let expectedHead =
                { Name = typeName
                  Definition = exactSource.Reference
                  SourceFormat = authored.SourceFormat
                  ValidatorTarget = authored.ValidatorTarget }
            let persistedRevision =
                match typeStatus, currentManifest with
                | Some Persistent, Some manifest ->
                    manifest.Types
                    |> List.tryFind (fun item -> item.Name = typeName && item = expectedHead)
                    |> Option.map (fun _ ->
                        (manifest.TypeRevisions |> List.filter (fun item -> item.Name = typeName) |> List.length) + 1)
                | _ -> None
            log "inspect" typeName
            let payload = JsonObject()
            payload["name"] <- jstr typeName
            payload["kind"] <- jstr "type"
            payload["status"] <- jstr (match typeStatus with | Some Candidate -> "candidate" | Some Persistent -> "persistent" | Some Temporary -> "temporary" | Some Primitive -> "primitive" | None -> "unknown")
            payload["revision"] <- persistedRevision |> Option.map jint |> Option.defaultValue null
            payload["sourceHash"] <- jstr exactSource.Reference.Hash
            payload["sourceFormat"] <- sourceFormatJson authored.SourceFormat
            payload["validatorTarget"] <- authored.ValidatorTarget |> Option.map storedCallTargetJson |> Option.defaultValue null
            payload

        let resultList (snapshot: RuntimeSnapshot) (kind: string) (text: string) (results: TestCaseResult list) (target: string option) =
            let array = JsonArray()
            results |> List.iter (fun result -> array.Add(resultJson snapshot result))
            let dataNode = JsonObject()
            dataNode["results"] <- array
            match target with Some word when snapshot.Words.ContainsKey word -> dataNode["coverage"] <- coverageJson snapshot word results | _ -> ()
            success kind text (Some dataNode)

        member _.ProjectDirectory = projectRoot
        member _.Capabilities = capabilities
        member _.TestCapabilities = engineTestCapabilities
        member _.FileSystemMode = engineFileSystemMode

        member _.Dispatch(operation: string, args: JsonObject) =
            try
                match operation with
                | "eval" ->
                    let structured = readOptionalStrictBool args "structured" false
                    let code = readString args "code" ""
                    let frontend = selectedFrontend args
                    let syntaxVersion = selectedSyntaxVersion args frontend
                    match frontend with
                    | "flow" ->
                        match FlowParser.parseExpressionWithVersion syntaxVersion "<flow-eval>" code with
                        | Error diagnostic -> response false "error" (Diagnostics.render diagnostic) None (Some diagnostic)
                        | Ok expression ->
                            let snapshot = currentSnapshot ()
                            let flowContext = snapshot.FlowContext |> Option.defaultWith (fun () -> error "FLOW_RUNTIME_CONTEXT_MISSING" "The active runtime snapshot has no Flow context." None None [] [])
                            let compiled = FlowLowering.compileExpressionWithVersion syntaxVersion flowContext expression
                            let evalSnapshot = { snapshot with Program = compiled.Program }
                            let trace = createTrace None engineFileSystemMode false false virtualFiles
                            let result = executeIRBody evalSnapshot "<flow-eval>" trace compiled.Body
                            let structuredStack = if structured then Some(ValueInspection.toData compiled.Program result) else None
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
                    | _ ->
                        match Parser.parse "<eval>" code with
                        | Ok parsed when not (List.isEmpty parsed.Words && List.isEmpty parsed.Records && List.isEmpty parsed.Scalars && List.isEmpty parsed.Tests && List.isEmpty parsed.Examples) ->
                            registerParsed parsed (readBool args "temporary" false)
                            success "defined" "Definitions parsed, type checked, and staged as candidates." (Some(jsonNode (parsed.Words |> List.map (fun word -> word.Name))))
                        | _ ->
                            match Parser.parseExpression "<eval>" code with
                            | Error diagnostic -> response false "error" (Diagnostics.render diagnostic) None (Some diagnostic)
                            | Ok body ->
                                let snapshot = currentSnapshot ()
                                let result, trace = executeExpression snapshot None engineFileSystemMode virtualFiles body
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
                    let frontend = selectedFrontend args
                    let syntaxVersion = selectedSyntaxVersion args frontend
                    match frontend with
                    | "flow" ->
                        validateFlowDefineArguments args
                        registerFlowParsed args syntaxVersion
                    | _ ->
                        let source = readString args "source" (readString args "code" "")
                        match Parser.parse "<definition>" source with
                        | Error diagnostic -> response false "error" (Diagnostics.render diagnostic) None (Some diagnostic)
                        | Ok parsed ->
                            registerParsed parsed (readBool args "temporary" false)
                            success "defined" "Definitions parsed, type checked, and staged." (Some(jsonNode (parsed.Words |> List.map (fun word -> word.Name))))
                | "format" ->
                    let allowed = [ "source"; "frontend"; "syntaxVersion" ]
                    let allowedSet = Set.ofList allowed
                    let unknown =
                        args
                        |> Seq.map (fun (KeyValue(key, _)) -> key)
                        |> Seq.filter (allowedSet.Contains >> not)
                        |> Seq.toList
                    rejectUnknownFields "format" allowed unknown
                    let frontend = selectedFrontend args
                    let syntaxVersion = selectedSyntaxVersion args frontend
                    if frontend <> "flow" then
                        error "SOURCE_FORMAT_UNSUPPORTED" "The format operation currently accepts Flow source." None None [ "frontend flow" ] [ frontend ]
                    let source = requiredFlowString args "source"
                    let document =
                        match FlowParser.parseDocumentWithVersion syntaxVersion "<flow-format>" source with
                        | Ok value -> value
                        | Error diagnostic -> raise (LanguageException diagnostic)
                    let formatted = FlowSource.renderDocument document
                    let payload = JsonObject()
                    payload["frontend"] <- jstr frontend
                    payload["syntaxVersion"] <- jint syntaxVersion
                    payload["source"] <- jstr formatted
                    success "format" "Canonical Flow source; submit it with define to stage an edit." (Some payload)
                | "help" ->
                    match AuthoringHelp.parseRequest args with
                    | Ok request ->
                        let payload = helpPayload request
                        success "help" (payload["title"].GetValue<string>()) (Some(payload :> JsonNode))
                    | Error diagnostic -> raise (LanguageException diagnostic)
                | "words" ->
                    let compact = readOptionalStrictBool args "compact" false
                    let words = effectiveWords data
                    let entries = words |> Map.toList |> List.map snd |> List.filter (fun item -> item.Status <> Primitive || item.Builtin.IsSome)
                    if compact then
                        let orderedEntries = entries |> List.sortBy (fun item -> item.Definition.Name)
                        orderedEntries |> List.iter (fun item -> log "inspect" item.Definition.Name)
                        let payload = JsonObject()
                        payload["compact"] <- jbool true
                        payload["words"] <- jsonNode (orderedEntries |> List.map (fun item -> item.Definition.Name))
                        payload["constructs"] <- jsonNode (syntaxDescriptors |> List.map (fun descriptor -> descriptor.Name) |> List.sort)
                        success "words" $"{entries.Length} word(s)." (Some payload)
                    else
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
                    if args.ContainsKey "type" then
                        if args.ContainsKey "word" then
                            error "TYPE_QUERY_AMBIGUOUS_SELECTOR" "Describe accepts exactly one of 'word' or 'type'." None None [ "one selector" ] [ "word and type" ]
                        let typeName = requiredFlowString args "type"
                        success "describe" $"Description for type {typeName}." (Some(describeTypeJson typeName))
                    else
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
                                || (item.Flow2Syntax |> Option.exists (fun syntax -> syntax.Contains(query, StringComparison.OrdinalIgnoreCase)))
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
                    if args.ContainsKey "type" then
                        if args.ContainsKey "word" then
                            error "SOURCE_SELECTOR_AMBIGUOUS" "Source inspection accepts exactly one of 'word' or 'type'." None None [ "one selector" ] [ "word and type" ]
                        let name = requiredFlowString args "type"
                        if not ((knownTypes data).Contains name) then
                            error "DISCOVERY_UNKNOWN_TYPE" $"Type source '{name}' is not defined." (Some name) None [ "declared record or scalar type" ] []
                        let authored = data.TypeSources.TryFind name |> Option.defaultValue (defaultTypeSource data name)
                        log "inspect" name
                        success "source" authored.Content (Some(jstr authored.Content))
                    else
                        let name = readString args "word" ""
                        match syntaxDescriptors |> List.tryFind (fun descriptor -> descriptor.Name = name) with
                        | Some descriptor ->
                            log "inspect" name
                            let source = "syntax " + descriptor.Syntax + " : " + (String.concat " " descriptor.Inputs) + " -> " + (String.concat " " descriptor.Outputs)
                            success "source" source (Some(jstr source))
                        | None ->
                            let snapshot = currentSnapshot ()
                            let words = snapshot.Words
                            match words.TryFind name with
                            | None -> error "NAME_UNKNOWN_WORD" $"Word '{name}' is not defined." (Some name) None [] []
                            | Some item ->
                                log "inspect" name
                                let authoredFlowSource =
                                    snapshot.State.WordIds.TryFind name
                                    |> Option.bind (fun identity -> snapshot.State.FlowWords.TryFind identity)
                                    |> Option.map (fun authored -> authored.Source.Content)
                                let source =
                                    match authoredFlowSource, item.Builtin with
                                    | Some exactSource, _ -> exactSource
                                    | None, Some(BuiltinOp _) ->
                                        let inputText = String.concat " " (item.Definition.Inputs |> List.map Types.format)
                                        let outputText = String.concat " " (item.Definition.Outputs |> List.map Types.format)
                                        $"primitive {name} : {inputText} -> {outputText}"
                                    | None, Some _ -> sourceForAgent item.Definition.SourceText
                                    | None, None -> sourceForAgent item.Definition.SourceText
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
                    let selectorError name expected actual =
                        error "TESTS_INVALID_ARGUMENT" $"Tests argument '{name}' must be {expected}." None None [ expected ] [ actual ]
                    let requiredSelector name =
                        if not (args.ContainsKey name) then selectorError name "a nonempty string" "missing"
                        match args[name] with
                        | :? JsonValue as value ->
                            let mutable selected = ""
                            if value.TryGetValue<string>(&selected) && not (String.IsNullOrWhiteSpace selected) then selected
                            else selectorError name "a nonempty string" (flowJsonKind (value :> JsonNode))
                        | value -> selectorError name "a nonempty string" (flowJsonKind value)
                    let optionalSelector name =
                        if args.ContainsKey name then Some(requiredSelector name) else None
                    let includeSource =
                        if not (args.ContainsKey "includeSource") then false
                        else
                            match args["includeSource"] with
                            | :? JsonValue as value ->
                                let mutable selected = false
                                if value.TryGetValue<bool>(&selected) then selected
                                else selectorError "includeSource" "a boolean" (flowJsonKind (value :> JsonNode))
                            | value -> selectorError "includeSource" "a boolean" (flowJsonKind value)
                    let name = requiredSelector "word"
                    let caseName = optionalSelector "caseName"
                    let snapshot = currentSnapshot ()
                    if not (snapshot.Words.ContainsKey name) then
                        error "NAME_UNKNOWN_WORD" $"Word '{name}' is not defined." (Some name) None [] []
                    let tests =
                        snapshot.State.Tests
                        |> Map.toList
                        |> List.map snd
                        |> List.filter (fun test -> test.Word = name)
                        |> List.sortBy (fun test -> test.Name)
                    let tests =
                        match caseName with
                        | None -> tests
                        | Some selected ->
                            match tests |> List.tryFind (fun test -> test.Name = selected) with
                            | Some test -> [ test ]
                            | None ->
                                error "NAME_UNKNOWN_TEST" $"Test case '{name}/{selected}' is not attached to '{name}'." (Some name) None
                                    (tests |> List.map (fun test -> test.Name)) [ selected ]
                    let payload =
                        if includeSource then jsonNode (testSourceRows snapshot tests)
                        else jsonNode (tests |> List.map (fun test -> test.Name))
                    if includeSource then log "inspect" name
                    success "tests" $"{tests.Length} attached test(s)." (Some payload)
                | "examples" ->
                    let name = readString args "word" ""
                    let examples = data.Examples |> Map.toList |> List.map snd |> List.filter (fun example -> example.Word = name) |> List.sortBy (fun example -> example.Name)
                    success "examples" $"{examples.Length} example(s)." (Some(jsonNode (examples |> List.map (fun example -> example.Name))))
                | "example" ->
                    let name = requiredFlowString args "word"
                    let caseName = if args.ContainsKey "caseName" then Some(requiredFlowString args "caseName") else None
                    let snapshot = currentSnapshot ()
                    if not (snapshot.Words.ContainsKey name) then
                        error "NAME_UNKNOWN_WORD" $"Word '{name}' is not defined." (Some name) None [] []
                    let results = runExamplesFor snapshot (Some name) caseName
                    let payload = JsonObject()
                    let rows = JsonArray()
                    results |> List.iter rows.Add
                    payload["results"] <- rows
                    let passed = results |> List.filter (fun result -> result["passed"].GetValue<bool>()) |> List.length
                    success "example" $"{passed}/{results.Length} example(s) passed." (Some payload)
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
                        || (data.Enums |> Map.exists (fun _ value -> value.Status = Candidate))
                    if operation = "task.commit" && not (activeTask |> Option.exists (fun task -> task.Active)) then
                        error "TASK_NOT_ACTIVE" "No active task can be committed." None None [] []
                    elif actor <> "client" && actor <> "host" then
                        error "PROVENANCE_INVALID_ACTOR" "Commit actor must be 'client' or 'host'." None None [ "client"; "host" ] [ actor ]
                    elif operation = "replace-word" && (name = "" || not (data.Replacements.ContainsKey name)) then
                        error "REPLACE_NOT_STAGED" "No staged persistent replacement exists for this word; use define with replace=true and the current expectedRevision, then call replace-word after testing (see help topic 'replacement')." (if name = "" then None else Some name) None [] []
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
                         || (data.Scalars |> Map.exists (fun _ item -> item.Status = Candidate))
                         || (data.Enums |> Map.exists (fun _ item -> item.Status = Candidate)) then
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
                                error "TYPE_VALIDATOR_FROZEN" $"Cannot rename '{oldName}' because it belongs to a persistent nominal-type validator closure." (Some oldName) None [] [ oldName ]
                            let baselineState = data
                            let data = (compileRuntimeSnapshot baselineState).State
                            let identity = data.WordIds.TryFind oldName |> Option.defaultWith (fun () -> error "WORD_ID_MISSING" $"Word '{oldName}' has no stable identity." (Some oldName) None [] [])
                            let target = StoredCallTarget.UserWord identity
                            let flowWordNames =
                                data.FlowWords
                                |> Map.toSeq
                                |> Seq.map (fun (_, authored) -> authored.Source.OwnerName)
                                |> Set.ofSeq
                            let flowTestNames =
                                data.FlowTests
                                |> Map.toSeq
                                |> Seq.map (fun (_, authored) -> authored.Source.OwnerName + "/" + authored.Source.CaseName)
                                |> Set.ofSeq
                            let flowExampleNames =
                                data.FlowExamples
                                |> Map.toSeq
                                |> Seq.map (fun (_, authored) -> authored.Source.OwnerName + "/" + authored.Source.CaseName)
                                |> Set.ofSeq
                            let requireBindings description = function
                                | Some bindings -> bindings
                                | None -> error "FLOW_RUNTIME_BINDINGS_MISSING" $"Cannot rename '{oldName}' because {description} has no verified source binding inventory." (Some oldName) None [ "verified Flow call bindings" ] []
                            let rewriteResult description = function
                                | Ok result -> result
                                | Error diagnostic -> raise (LanguageException diagnostic)
                            let rewrittenFlowWords: Map<string, FlowAuthoredWord * FlowRewriteResult<FlowWordDefinition> * bool * string> =
                                data.FlowWords
                                |> Map.map (fun _ authored ->
                                    let prior = requireBindings ("Flow definition '" + authored.Source.OwnerName + "'") authored.StoredBindings
                                    let result =
                                        FlowRewrite.rewriteWord oldName newName target authored.Definition prior
                                        |> rewriteResult authored.Source.OwnerName
                                    let changed = result.Definition.Name <> authored.Definition.Name || result.Bindings <> prior
                                    let content = if changed then result.Definition.SourceText else authored.Source.Content
                                    authored, result, changed, content)
                            let rewrittenFlowTests: Map<string, FlowAuthoredAttachment * FlowRewriteResult<FlowTestDefinition> * bool * string> =
                                data.FlowTests
                                |> Map.filter (fun _ authored -> not (data.FlowTestFiles.ContainsKey(flowTestFileKey authored.Source.OwnerId authored.Source.Reference)))
                                |> Map.map (fun _ authored ->
                                    let parsed =
                                        match FlowParser.parseTestWithVersion authored.Source.SyntaxVersion authored.Source.SourceFile authored.Source.Content with
                                        | Ok definition -> definition
                                        | Error diagnostic -> raise (LanguageException diagnostic)
                                    let prior = requireBindings ("Flow test '" + authored.Source.OwnerName + "/" + authored.Source.CaseName + "'") authored.StoredBindings
                                    let result =
                                        FlowRewrite.rewriteTest oldName newName target parsed prior
                                        |> rewriteResult (authored.Source.OwnerName + "/" + authored.Source.CaseName)
                                    let changed = result.Definition.Word <> parsed.Word || result.Bindings <> prior
                                    let content = if changed then result.Definition.SourceText else authored.Source.Content
                                    authored, result, changed, content)
                            let rewrittenFlowTestFiles: Map<string, FlowAuthoredTestFile * FlowRewriteResult<FlowTestFileSettings> * bool * string> =
                                data.FlowTestFiles
                                |> Map.map (fun _ authored ->
                                    let parsed =
                                        match FlowParser.parseTestFileSettingsWithVersion authored.SyntaxVersion authored.SourceFile authored.Settings.SourceText with
                                        | Ok settings -> settings
                                        | Error diagnostic -> raise (LanguageException diagnostic)
                                    let caseBindings =
                                        data.FlowTests
                                        |> Map.toList
                                        |> List.collect (fun (_, attachment) ->
                                            if attachment.Source.OwnerId = authored.OwnerId && attachment.Source.Reference = authored.Reference then
                                                requireBindings ("Flow test-file case '" + authored.OwnerName + "/" + attachment.Source.CaseName + "'") attachment.StoredBindings
                                            else [])
                                    let priorOverrides = requireBindings ("Flow test-file '" + authored.OwnerName + "'") authored.StoredBindings
                                    let prior = priorOverrides @ caseBindings
                                    let result =
                                        FlowRewrite.rewriteTestFileSettings oldName newName target parsed prior
                                        |> rewriteResult (authored.OwnerName + "/test-file")
                                    let changed = result.Changed || result.Bindings <> prior
                                    let content = if changed then FlowSource.renderTestFileSettings result.Definition else authored.Settings.SourceText
                                    authored, result, changed, content)
                            let rewrittenFlowExamples: Map<string, FlowAuthoredAttachment * FlowRewriteResult<FlowExampleDefinition> * bool * string> =
                                data.FlowExamples
                                |> Map.map (fun _ authored ->
                                    let parsed =
                                        match FlowParser.parseExampleWithVersion authored.Source.SyntaxVersion authored.Source.SourceFile authored.Source.Content with
                                        | Ok definition -> definition
                                        | Error diagnostic -> raise (LanguageException diagnostic)
                                    let prior = requireBindings ("Flow example '" + authored.Source.OwnerName + "/" + authored.Source.CaseName + "'") authored.StoredBindings
                                    let result =
                                        FlowRewrite.rewriteExample oldName newName target parsed prior
                                        |> rewriteResult (authored.Source.OwnerName + "/" + authored.Source.CaseName)
                                    let changed = result.Definition.Word <> parsed.Word || result.Bindings <> prior
                                    let content = if changed then result.Definition.SourceText else authored.Source.Content
                                    authored, result, changed, content)
                            let changedFlowWordIds =
                                rewrittenFlowWords
                                |> Map.toSeq
                                |> Seq.choose (fun (ownerId, (_, _, changed, _)) -> if changed then Some ownerId else None)
                                |> Set.ofSeq
                            let changedFlowTestOwners =
                                rewrittenFlowTests
                                |> Map.toSeq
                                |> Seq.choose (fun (_, (authored, _, changed, _)) -> if changed then Some(wordIdText authored.Source.OwnerId) else None)
                                |> Set.ofSeq
                                |> fun owners ->
                                    rewrittenFlowTestFiles
                                    |> Map.toSeq
                                    |> Seq.choose (fun (_, (authored, _, changed, _)) -> if changed then Some(wordIdText authored.OwnerId) else None)
                                    |> Set.ofSeq
                                    |> Set.union owners
                            let changedFlowExampleOwners =
                                rewrittenFlowExamples
                                |> Map.toSeq
                                |> Seq.choose (fun (_, (authored, _, changed, _)) -> if changed then Some(wordIdText authored.Source.OwnerId) else None)
                                |> Set.ofSeq
                            let flowOwnerIds = data.FlowWords |> Map.toSeq |> Seq.map fst |> Set.ofSeq
                            let changedFlowOwnerIds =
                                Set.unionMany [ changedFlowWordIds; changedFlowTestOwners; changedFlowExampleOwners ]
                                |> fun changed -> if flowOwnerIds.Contains identity then Set.add identity changed else changed
                            let flowRevision ownerId =
                                let authored =
                                    data.FlowWords.TryFind ownerId
                                    |> Option.defaultWith (fun () -> error "FLOW_RUNTIME_ORPHAN_ATTACHMENT" "A Flow attachment owner has no Flow word while preparing a rename." (Some oldName) None [] [ ownerId ])
                                let owner =
                                    data.Words.TryFind authored.Source.OwnerName
                                    |> Option.defaultWith (fun () -> error "FLOW_RUNTIME_OWNER_MISSING" "A Flow source has no executable owner entry while preparing a rename." (Some authored.Source.OwnerName) None [] [])
                                owner.Revision + (if changedFlowOwnerIds.Contains ownerId then 1 else 0)
                            let flowSourceFile ownerName revision = $"<flow:{ownerName}/{revision}>"
                            let flowTestSourceFile ownerName revision reference = $"<flow:{ownerName}/{revision}>/test:{reference.Hash}"
                            let flowTestFileSourceFile ownerName revision reference = $"<flow:{ownerName}/{revision}>/test-file:{reference.Hash}"
                            let flowExampleSourceFile ownerName revision reference = $"<flow:{ownerName}/{revision}>/example:{reference.Hash}"
                            let finalFlowWords: Map<string, FlowAuthoredWord> =
                                rewrittenFlowWords
                                |> Map.map (fun ownerId (authored, result, _, content) ->
                                    let ownerName = result.Definition.Name
                                    let revision = flowRevision ownerId
                                    let sourceObject = Storage.sourceObject StorageObjectKind.WordDefinition content
                                    let sourceFile = flowSourceFile ownerName revision
                                    let definition =
                                        match FlowParser.parseWordWithVersion authored.Source.SyntaxVersion sourceFile content with
                                        | Ok parsed when parsed.Name = ownerName && parsed.SyntaxVersion = authored.Source.SyntaxVersion -> parsed
                                        | Ok parsed -> error "FLOW_REWRITE_OWNER_MISMATCH" "Rewritten Flow source did not preserve its stable owner name and syntax version." (Some ownerName) (Some parsed.Span) [ ownerName; $"Flow/{authored.Source.SyntaxVersion}" ] [ parsed.Name; string parsed.SyntaxVersion ]
                                        | Error diagnostic -> raise (LanguageException diagnostic)
                                    let source =
                                        { authored.Source with
                                            OwnerName = ownerName
                                            OwnerRevision = revision
                                            Reference = sourceObject.Reference
                                            SourceFile = sourceFile
                                            Content = content }
                                    let bindings = result.Bindings |> List.map (fun binding -> { binding with Source = source.Reference })
                                    { Definition = definition; Source = source; StoredBindings = Some bindings })
                            let finalFlowTestFileRows =
                                rewrittenFlowTestFiles
                                |> Map.toList
                                |> List.map (fun (oldKey, (authored, result, _, content)) ->
                                    let ownerName = result.Definition.Tests.Head.Word
                                    let revision = flowRevision (wordIdText authored.OwnerId)
                                    let sourceObject = Storage.sourceObject StorageObjectKind.TestDefinition content
                                    let sourceFile = flowTestFileSourceFile ownerName revision sourceObject.Reference
                                    let settings =
                                        match FlowParser.parseTestFileSettingsWithVersion authored.SyntaxVersion sourceFile content with
                                        | Ok parsed when parsed.SyntaxVersion = authored.SyntaxVersion -> parsed
                                        | Ok parsed -> error "FLOW_REWRITE_OWNER_MISMATCH" "Rewritten Flow test-file source changed its syntax version." (Some ownerName) (Some parsed.Span) [ string authored.SyntaxVersion ] [ string parsed.SyntaxVersion ]
                                        | Error diagnostic -> raise (LanguageException diagnostic)
                                    let bindings = result.Bindings |> List.map (fun binding -> { binding with Source = sourceObject.Reference })
                                    let file =
                                        { authored with
                                            OwnerName = ownerName
                                            OwnerRevision = revision
                                            Reference = sourceObject.Reference
                                            SourceFile = sourceFile
                                            Settings = settings
                                            StoredBindings = Some(bindings |> List.filter (fun binding -> binding.BodyRole = StoredCallBodyRole.TestOverride)) }
                                    oldKey, flowTestFileKey authored.OwnerId sourceObject.Reference, file, result)
                            let finalFlowTestFiles: Map<string, FlowAuthoredTestFile> =
                                finalFlowTestFileRows |> List.map (fun (_, key, file, _) -> key, file) |> Map.ofList
                            let finalStandaloneFlowTests: Map<string, FlowAuthoredAttachment> =
                                rewrittenFlowTests
                                |> Map.toList
                                |> List.map (fun (_, (authored, result, _, content)) ->
                                    let ownerId = wordIdText authored.Source.OwnerId
                                    let ownerName = result.Definition.Word
                                    let revision = flowRevision ownerId
                                    let sourceObject = Storage.sourceObject StorageObjectKind.TestDefinition content
                                    let sourceFile = flowTestSourceFile ownerName revision sourceObject.Reference
                                    let definition =
                                        match FlowParser.parseTestWithVersion authored.Source.SyntaxVersion sourceFile content with
                                        | Ok parsed when parsed.Word = ownerName && parsed.CaseName = authored.Source.CaseName && parsed.SyntaxVersion = authored.Source.SyntaxVersion -> parsed
                                        | Ok parsed -> error "FLOW_REWRITE_OWNER_MISMATCH" "Rewritten Flow test did not preserve its stable owner and case name." (Some(ownerName + "/" + authored.Source.CaseName)) (Some parsed.Span) [ ownerName; authored.Source.CaseName ] [ parsed.Word; parsed.CaseName ]
                                        | Error diagnostic -> raise (LanguageException diagnostic)
                                    let source =
                                        { authored.Source with
                                            OwnerName = ownerName
                                            OwnerRevision = revision
                                            Reference = sourceObject.Reference
                                            SourceFile = sourceFile
                                            Content = content }
                                    let bindings = result.Bindings |> List.map (fun binding -> { binding with Source = source.Reference })
                                    flowAttachmentKey authored.Source.OwnerId definition.CaseName,
                                    { Source = source; StoredBindings = Some bindings })
                                |> Map.ofList
                            let finalWrapperFlowTests: Map<string, FlowAuthoredAttachment> =
                                finalFlowTestFileRows
                                |> List.collect (fun (_, _, file, result) ->
                                    file.Settings.Tests
                                    |> List.map (fun test ->
                                        let key = flowAttachmentKey file.OwnerId test.CaseName
                                        let source: FlowLowering.FlowAttachmentSourceDocument =
                                            { OwnerName = file.OwnerName
                                              SyntaxVersion = file.SyntaxVersion
                                              OwnerId = file.OwnerId
                                              OwnerRevision = file.OwnerRevision
                                              Kind = FlowLowering.FlowAttachmentKind.Test
                                              CaseName = test.CaseName
                                              Reference = file.Reference
                                              SourceFile = file.SourceFile
                                              Content = file.Settings.SourceText }
                                        let bindings =
                                            result.Bindings
                                            |> List.filter (fun binding -> binding.CaseName = Some test.CaseName)
                                            |> List.map (fun binding -> { binding with Source = file.Reference })
                                        key, { Source = source; StoredBindings = Some bindings }))
                                |> Map.ofList
                            let finalFlowTests =
                                Map.fold (fun found key item -> Map.add key item found) finalStandaloneFlowTests finalWrapperFlowTests
                            let finalFlowExamples: Map<string, FlowAuthoredAttachment> =
                                rewrittenFlowExamples
                                |> Map.toList
                                |> List.map (fun (_, (authored, result, _, content)) ->
                                    let ownerId = wordIdText authored.Source.OwnerId
                                    let ownerName = result.Definition.Word
                                    let revision = flowRevision ownerId
                                    let sourceObject = Storage.sourceObject StorageObjectKind.ExampleDefinition content
                                    let sourceFile = flowExampleSourceFile ownerName revision sourceObject.Reference
                                    let definition =
                                        match FlowParser.parseExampleWithVersion authored.Source.SyntaxVersion sourceFile content with
                                        | Ok parsed when parsed.Word = ownerName && parsed.CaseName = authored.Source.CaseName && parsed.SyntaxVersion = authored.Source.SyntaxVersion -> parsed
                                        | Ok parsed -> error "FLOW_REWRITE_OWNER_MISMATCH" "Rewritten Flow example did not preserve its stable owner and case name." (Some(ownerName + "/" + authored.Source.CaseName)) (Some parsed.Span) [ ownerName; authored.Source.CaseName ] [ parsed.Word; parsed.CaseName ]
                                        | Error diagnostic -> raise (LanguageException diagnostic)
                                    let source =
                                        { authored.Source with
                                            OwnerName = ownerName
                                            OwnerRevision = revision
                                            Reference = sourceObject.Reference
                                            SourceFile = sourceFile
                                            Content = content }
                                    let bindings = result.Bindings |> List.map (fun binding -> { binding with Source = source.Reference })
                                    flowAttachmentKey authored.Source.OwnerId definition.CaseName,
                                    { Source = source; StoredBindings = Some bindings })
                                |> Map.ofList
                            let stackTestSources =
                                data.Tests
                                |> Map.toList
                                |> List.map snd
                                |> List.filter (fun test -> not (flowTestNames.Contains(test.Word + "/" + test.Name)))
                            let rewrittenStackTests = stackTestSources |> List.map (fun test -> test, Source.renameTestOwner oldName newName test)
                            let changedStackTestOwners =
                                rewrittenStackTests
                                |> List.choose (fun (before, after) -> if Source.renderTest before <> Source.renderTest after then Some after.Word else None)
                                |> Set.ofList
                            let stackExampleSources =
                                data.Examples
                                |> Map.toList
                                |> List.map snd
                                |> List.filter (fun example -> not (flowExampleNames.Contains(example.Word + "/" + example.Name)))
                            let rewrittenStackExamples = stackExampleSources |> List.map (fun example -> example, Source.renameExampleOwner oldName newName example)
                            let changedStackExampleOwners =
                                rewrittenStackExamples
                                |> List.choose (fun (before, after) -> if Source.renderExample before <> Source.renderExample after then Some after.Word else None)
                                |> Set.ofList
                            let changedStackAttachmentOwners = Set.union changedStackTestOwners changedStackExampleOwners
                            let stackDefinitionChanges =
                                data.Words
                                |> Map.toList
                                |> List.choose (fun (oldKey, item) ->
                                    if item.Builtin.IsSome || flowWordNames.Contains oldKey then None
                                    else
                                        let renamed = Source.renameWordDefinition oldName newName item.Definition
                                        let changed = renamed.Name <> item.Definition.Name || renamed.Body <> item.Definition.Body || changedStackAttachmentOwners.Contains renamed.Name
                                        if not changed then None
                                        else
                                            let revision = item.Revision + 1
                                            let definition = { renamed with Revision = revision; Maturity = item.Maturity }
                                            let rendered = Source.renderWord true definition
                                            let sourceObject = Storage.sourceObject StorageObjectKind.WordDefinition rendered
                                            let sourceFile = $"<revision:{definition.Name}/{revision}>/definition:{sourceObject.Reference.Hash}"
                                            match Parser.parse sourceFile rendered with
                                            | Error diagnostic -> raise (LanguageException diagnostic)
                                            | Ok parsed when parsed.Words.Length = 1 && parsed.Records.IsEmpty && parsed.Scalars.IsEmpty && parsed.Tests.IsEmpty && parsed.Examples.IsEmpty ->
                                                let parsedDefinition = parsed.Words.Head
                                                Some(oldKey, parsedDefinition.Name, { item with Definition = parsedDefinition; Revision = revision })
                                            | Ok _ -> error "RENAME_INVALID_SOURCE" "Renaming a Stack word did not produce exactly one valid word definition." (Some renamed.Name) (Some renamed.Span) [] [] )
                            let stackOwnerRevision ownerName =
                                match stackDefinitionChanges |> List.tryFind (fun (_, renamedName, _) -> renamedName = ownerName) with
                                | Some(_, _, entry) -> entry.Revision
                                | None -> data.Words.TryFind ownerName |> Option.map (fun entry -> entry.Revision) |> Option.defaultValue 0
                            let reparseChangedStackTest (before: TestDefinition, after: TestDefinition) =
                                if Source.renderTest before = Source.renderTest after then after
                                else
                                    let content = Source.renderTest after
                                    let sourceObject = Storage.sourceObject StorageObjectKind.TestDefinition content
                                    let sourceFile = $"<revision:{after.Word}/{stackOwnerRevision after.Word}>/test:{sourceObject.Reference.Hash}"
                                    let parsed = parseProjectSource sourceFile content
                                    match parsed.Tests, parsed.Words, parsed.Records, parsed.Scalars, parsed.Examples with
                                    | [ test ], [], [], [], [] when test.Word = after.Word && test.Name = after.Name -> test
                                    | _ -> error "RENAME_INVALID_SOURCE" "Renaming a Stack test did not produce exactly one valid test definition." (Some(after.Word + "/" + after.Name)) (Some after.Span) [] []
                            let reparseChangedStackExample (before: ExampleDefinition, after: ExampleDefinition) =
                                if Source.renderExample before = Source.renderExample after then after
                                else
                                    let content = Source.renderExample after
                                    let sourceObject = Storage.sourceObject StorageObjectKind.ExampleDefinition content
                                    let sourceFile = $"<revision:{after.Word}/{stackOwnerRevision after.Word}>/example:{sourceObject.Reference.Hash}"
                                    let parsed = parseProjectSource sourceFile content
                                    match parsed.Examples, parsed.Words, parsed.Records, parsed.Scalars, parsed.Tests with
                                    | [ example ], [], [], [], [] when example.Word = after.Word && example.Name = after.Name -> example
                                    | _ -> error "RENAME_INVALID_SOURCE" "Renaming a Stack example did not produce exactly one valid example definition." (Some(after.Word + "/" + after.Name)) (Some after.Span) [] []
                            let tests =
                                rewrittenStackTests
                                |> List.map reparseChangedStackTest
                                |> List.fold addTest Map.empty
                            let examples =
                                rewrittenStackExamples
                                |> List.map reparseChangedStackExample
                                |> List.fold addExample Map.empty
                            let changedStackOwnerNames =
                                stackDefinitionChanges
                                |> List.map (fun (_, name, _) -> name)
                                |> Set.ofList
                            let flowWordEntries =
                                finalFlowWords
                                |> Map.toList
                                |> List.map (fun (ownerId, authored) ->
                                    let originalAuthored = data.FlowWords[ownerId]
                                    let originalEntry = data.Words[originalAuthored.Source.OwnerName]
                                    let revision = flowRevision ownerId
                                    let definition =
                                        { originalEntry.Definition with
                                            Name = authored.Definition.Name
                                            Inputs = authored.Definition.Parameters |> List.map (fun parameter -> parameter.Type)
                                            Outputs = authored.Definition.Outputs
                                            Effects = authored.Definition.Effects
                                            Maturity = originalEntry.Maturity
                                            Revision = revision
                                            Documentation = authored.Definition.Documentation
                                            SourceText = authored.Source.Content
                                            Span = authored.Definition.Span }
                                    let entry = { originalEntry with Definition = definition; Revision = revision }
                                    authored.Source.OwnerName, entry)
                            let stackWords =
                                data.Words
                                |> Map.filter (fun name _ -> not (flowWordNames.Contains name) && name <> oldName)
                                |> fun found ->
                                    stackDefinitionChanges
                                    |> List.fold (fun current (_, name, item) -> Map.add name item current) found
                            let words =
                                flowWordEntries
                                |> List.fold (fun found (name, item) -> Map.add name item found) stackWords
                            let wordIds = data.WordIds |> Map.remove oldName |> Map.add newName identity
                            let scalars =
                                data.Scalars
                                |> Map.map (fun _ item ->
                                    let definition = Source.renameScalarValidator oldName newName item.Definition
                                    if definition.Validator = item.Definition.Validator then item
                                    else { item with Definition = definition })
                            let records =
                                data.Records
                                |> Map.map (fun _ item ->
                                    let definition = Source.renameRecordValidator oldName newName item.Definition
                                    if definition.Validator = item.Definition.Validator then item
                                    else { item with Definition = definition })
                            let deprecated = if data.Deprecated.Contains oldName then data.Deprecated |> Set.remove oldName |> Set.add newName else data.Deprecated
                            let movedHistory: Map<string, WordDefinition list> =
                                match data.History.TryFind oldName with
                                | Some oldHistory -> data.History |> Map.remove oldName |> Map.add newName oldHistory
                                | None -> data.History
                            let proposed =
                                { data with
                                    Words = words
                                    WordIds = wordIds
                                    Deprecated = deprecated
                                    Records = records
                                    Scalars = scalars
                                    Tests = tests
                                    Examples = examples
                                    FlowWords = finalFlowWords
                                    FlowTests = finalFlowTests
                                    FlowTestFiles = finalFlowTestFiles
                                    FlowExamples = finalFlowExamples
                                    Replacements = Map.empty }
                            let executable = compileRuntimeSnapshot proposed
                            let changedFlowOwnerNames =
                                changedFlowOwnerIds
                                |> Set.toList
                                |> List.choose (fun ownerId -> executable.State.FlowWords.TryFind ownerId |> Option.map (fun authored -> authored.Source.OwnerName))
                                |> Set.ofList
                            let changedWordNames = Set.union changedStackOwnerNames changedFlowOwnerNames
                            for name in changedWordNames do
                                rejectUnqualifiedLibraryDependencies executable.Words name
                            let testOwnersToRun = Set.union changedWordNames changedStackAttachmentOwners
                            let results =
                                testOwnersToRun
                                |> Set.toList
                                |> List.collect (fun name ->
                                    let attached = executable.State.Tests |> Map.toSeq |> Seq.map snd |> Seq.filter (fun test -> test.Word = name) |> Seq.toList
                                    if List.isEmpty attached then
                                        error "RENAME_TESTS_REQUIRED" $"Renaming or rewriting '{name}' requires at least one attached test." (Some name) None [ "attached passing test" ] []
                                    runTestsFor executable (Some name))
                            preflightStructuredTestResults executable.Program results
                            recordTestResults results
                            let failed = results |> List.filter (fun item -> not item.Passed)
                            if not (List.isEmpty failed) then
                                error "RENAME_TESTS_FAILED" "Affected tests must pass before the rename can be published." None None [] (failed |> List.map (fun item -> $"{item.Word}/{item.Name}"))
                            for name in changedWordNames do
                                match executable.Words.TryFind name with
                                | Some candidate when candidate.Maturity = LibraryWord ->
                                    let ownTests = results |> List.filter (fun test -> test.Word = name)
                                    requireLibraryCoverage executable name ownTests
                                | _ -> ()
                            let history =
                                changedWordNames
                                |> Set.fold (fun (found: Map<string, WordDefinition list>) name ->
                                    match executable.State.Words.TryFind name with
                                    | None -> found
                                    | Some item ->
                                        let previous = found.TryFind name |> Option.defaultValue []
                                        Map.add name (previous @ [ item.Definition ]) found) movedHistory
                            let flowHistory =
                                changedFlowOwnerIds
                                |> Set.fold (fun (found: Map<string, FlowAuthoredWord list>) ownerId ->
                                    match executable.State.FlowWords.TryFind ownerId with
                                    | None -> found
                                    | Some authored ->
                                        let previous = found.TryFind ownerId |> Option.defaultValue []
                                        if previous |> List.exists (fun item -> item.Source.OwnerRevision = authored.Source.OwnerRevision) then found
                                        else Map.add ownerId (previous @ [ authored ]) found) executable.State.FlowHistory
                            let finalExecutable =
                                { executable with State = { executable.State with History = history; FlowHistory = flowHistory } }
                            let durableSnapshot = compileRuntimeSnapshot (durableState finalExecutable.State)
                            publish baselineState durableSnapshot.State actor
                            activateRuntimeSnapshot finalExecutable
                            lastResults <- results
                            let payload = JsonObject()
                            payload["id"] <- jstr identity
                            payload["from"] <- jstr oldName
                            payload["to"] <- jstr newName
                            payload["rewrittenWords"] <- jsonNode (changedWordNames |> Set.toList)
                            success "rename" $"Renamed '{oldName}' to '{newName}' and validated {results.Length} affected test(s)." (Some payload)
                | "deprecate" ->
                    let name = readString args "word" ""
                    let actor = readString args "actor" "client"
                    if actor <> "client" && actor <> "host" then
                        error "PROVENANCE_INVALID_ACTOR" "Maintenance actor must be 'client' or 'host'." None None [ "client"; "host" ] [ actor ]
                    elif (data.Words |> Map.exists (fun _ item -> item.Status = Candidate || item.Status = Temporary))
                         || (data.Records |> Map.exists (fun _ item -> item.Status = Candidate))
                         || (data.Scalars |> Map.exists (fun _ item -> item.Status = Candidate))
                         || (data.Enums |> Map.exists (fun _ item -> item.Status = Candidate)) then
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
                            let identity =
                                data.WordIds.TryFind name
                                |> Option.defaultWith (fun () -> error "WORD_ID_MISSING" $"Word '{name}' has no stable identity." (Some name) None [] [])
                            let proposed =
                                match data.FlowWords.TryFind identity with
                                | Some authored ->
                                    let definitionObject = Storage.sourceObject StorageObjectKind.WordDefinition authored.Source.Content
                                    if definitionObject.Reference <> authored.Source.Reference then
                                        error "FLOW_RUNTIME_SOURCE_MISMATCH" "A Flow definition source no longer matches its immutable source reference." (Some name) None [ authored.Source.Reference.Hash ] [ definitionObject.Reference.Hash ]
                                    let sourceFile = $"<flow:{name}/{revision}>"
                                    let definition =
                                        match FlowParser.parseWordWithVersion authored.Source.SyntaxVersion sourceFile authored.Source.Content with
                                        | Ok parsed when parsed.Name = name && parsed.SyntaxVersion = authored.Definition.SyntaxVersion -> parsed
                                        | Ok parsed -> error "FLOW_RUNTIME_OWNER_MISMATCH" "Flow deprecation metadata does not match the authored owner and syntax version." (Some name) (Some parsed.Span) [ name; string authored.Definition.SyntaxVersion ] [ parsed.Name; string parsed.SyntaxVersion ]
                                        | Error diagnostic -> raise (LanguageException diagnostic)
                                    let nextWord =
                                        { authored with
                                            Definition = definition
                                            Source = { authored.Source with OwnerRevision = revision; SourceFile = sourceFile } }
                                    let flowTests =
                                        data.FlowTests
                                        |> Map.map (fun _ attachment ->
                                            if attachment.Source.OwnerId <> authored.Source.OwnerId then attachment
                                            else
                                                let sourceObject = Storage.sourceObject StorageObjectKind.TestDefinition attachment.Source.Content
                                                if sourceObject.Reference <> attachment.Source.Reference then
                                                    error "FLOW_RUNTIME_SOURCE_MISMATCH" "A Flow test source no longer matches its immutable source reference." (Some(name + "/" + attachment.Source.CaseName)) None [ attachment.Source.Reference.Hash ] [ sourceObject.Reference.Hash ]
                                                { attachment with
                                                    Source =
                                                        { attachment.Source with
                                                            OwnerRevision = revision
                                                            SourceFile =
                                                                if data.FlowTestFiles.ContainsKey(flowTestFileKey attachment.Source.OwnerId attachment.Source.Reference) then
                                                                    $"<flow:{name}/{revision}>/test-file:{attachment.Source.Reference.Hash}"
                                                                else $"<flow:{name}/{revision}>/test:{attachment.Source.Reference.Hash}" } })
                                    let flowTestFiles =
                                        data.FlowTestFiles
                                        |> Map.map (fun _ file ->
                                            if file.OwnerId <> authored.Source.OwnerId then file
                                            else
                                                let sourceFile = $"<flow:{name}/{revision}>/test-file:{file.Reference.Hash}"
                                                let settings =
                                                    match FlowParser.parseTestFileSettingsWithVersion file.SyntaxVersion sourceFile file.Settings.SourceText with
                                                    | Ok parsed -> parsed
                                                    | Error diagnostic -> raise (LanguageException diagnostic)
                                                { file with OwnerRevision = revision; SourceFile = sourceFile; Settings = settings })
                                    let flowExamples =
                                        data.FlowExamples
                                        |> Map.map (fun _ attachment ->
                                            if attachment.Source.OwnerId <> authored.Source.OwnerId then attachment
                                            else
                                                let sourceObject = Storage.sourceObject StorageObjectKind.ExampleDefinition attachment.Source.Content
                                                if sourceObject.Reference <> attachment.Source.Reference then
                                                    error "FLOW_RUNTIME_SOURCE_MISMATCH" "A Flow example source no longer matches its immutable source reference." (Some(name + "/" + attachment.Source.CaseName)) None [ attachment.Source.Reference.Hash ] [ sourceObject.Reference.Hash ]
                                                { attachment with
                                                    Source =
                                                        { attachment.Source with
                                                            OwnerRevision = revision
                                                            SourceFile = $"<flow:{name}/{revision}>/example:{attachment.Source.Reference.Hash}" } })
                                    let projectedDefinition =
                                        { item.Definition with
                                            Revision = revision
                                            SourceText = authored.Source.Content
                                            Span = definition.Span }
                                    { data with
                                        Words = Map.add name { item with Definition = projectedDefinition; Revision = revision } data.Words
                                        Deprecated = Set.add name data.Deprecated
                                        FlowWords = Map.add identity nextWord data.FlowWords
                                        FlowTests = flowTests
                                        FlowTestFiles = flowTestFiles
                                        FlowExamples = flowExamples }
                                | None ->
                                    let definition = { item.Definition with Revision = revision }
                                    let definition = { definition with SourceText = Source.renderWord true definition }
                                    let updated = { item with Definition = definition; Revision = revision }
                                    { data with
                                        Words = Map.add name updated data.Words
                                        Deprecated = Set.add name data.Deprecated }
                            let testSnapshot = compileRuntimeSnapshot proposed
                            let tests = runTestsFor testSnapshot (Some name)
                            if List.isEmpty tests then error "DEPRECATE_TESTS_REQUIRED" $"Word '{name}' needs an attached test before it can be deprecated." (Some name) None [ "attached passing test" ] []
                            preflightStructuredTestResults testSnapshot.Program tests
                            recordTestResults tests
                            let failed = tests |> List.filter (fun result -> not result.Passed)
                            if not (List.isEmpty failed) then error "DEPRECATE_TESTS_FAILED" "The word's attached tests must pass before deprecation." (Some name) None [] (failed |> List.map (fun result -> result.Name))
                            if item.Maturity = LibraryWord then
                                rejectUnqualifiedLibraryDependencies testSnapshot.Words name
                                requireLibraryCoverage testSnapshot name tests
                            let history =
                                let previous = data.History.TryFind name |> Option.defaultValue []
                                Map.add name (previous @ [ testSnapshot.State.Words[name].Definition ]) testSnapshot.State.History
                            let flowHistory =
                                match testSnapshot.State.WordIds.TryFind name with
                                | Some ownerId when testSnapshot.State.FlowWords.ContainsKey ownerId ->
                                    let authored = testSnapshot.State.FlowWords[ownerId]
                                    let previous = testSnapshot.State.FlowHistory.TryFind ownerId |> Option.defaultValue []
                                    if previous |> List.exists (fun prior -> prior.Source.OwnerRevision = authored.Source.OwnerRevision) then testSnapshot.State.FlowHistory
                                    else Map.add ownerId (previous @ [ authored ]) testSnapshot.State.FlowHistory
                                | _ -> testSnapshot.State.FlowHistory
                            let executable = { testSnapshot with State = { testSnapshot.State with History = history; FlowHistory = flowHistory } }
                            let durableSnapshot = compileRuntimeSnapshot (durableState executable.State)
                            publish data durableSnapshot.State actor
                            activateRuntimeSnapshot executable
                            lastResults <- tests
                            let payload = JsonObject()
                            payload["id"] <- jstr identity
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
                        let ownerId = data.WordIds.TryFind name |> Option.map WordId
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
                                let flowWords =
                                    data.FlowWords
                                    |> Map.filter (fun _ authored -> ownerId <> Some authored.Source.OwnerId)
                                    |> fun current ->
                                        match backup.FlowWord with
                                        | Some authored -> Map.add (wordIdText authored.Source.OwnerId) authored current
                                        | None -> current
                                let restoreFlowAttachments (current: Map<string, FlowAuthoredAttachment>) (saved: Map<string, FlowAuthoredAttachment>) =
                                    current
                                    |> Map.filter (fun _ attachment -> ownerId <> Some attachment.Source.OwnerId)
                                    |> fun remaining -> Map.fold (fun found key value -> Map.add key value found) remaining saved
                                let restoreFlowTestFiles (current: Map<string, FlowAuthoredTestFile>) (saved: Map<string, FlowAuthoredTestFile>) =
                                    current
                                    |> Map.filter (fun _ file -> ownerId <> Some file.OwnerId)
                                    |> fun remaining -> Map.fold (fun found key value -> Map.add key value found) remaining saved
                                { data with
                                    Words = Map.add name backup.Word data.Words
                                    Tests = tests
                                    Examples = examples
                                    FlowWords = flowWords
                                    FlowTests = restoreFlowAttachments data.FlowTests backup.FlowTests
                                    FlowTestFiles = restoreFlowTestFiles data.FlowTestFiles backup.FlowTestFiles
                                    FlowExamples = restoreFlowAttachments data.FlowExamples backup.FlowExamples
                                    Replacements = Map.remove name data.Replacements }
                            | None ->
                                let flowWords = data.FlowWords |> Map.filter (fun _ authored -> ownerId <> Some authored.Source.OwnerId)
                                let flowTests = data.FlowTests |> Map.filter (fun _ attachment -> ownerId <> Some attachment.Source.OwnerId)
                                let flowTestFiles = data.FlowTestFiles |> Map.filter (fun _ file -> ownerId <> Some file.OwnerId)
                                let flowExamples = data.FlowExamples |> Map.filter (fun _ attachment -> ownerId <> Some attachment.Source.OwnerId)
                                let flowHistory = ownerId |> Option.map (fun identity -> Map.remove (wordIdText identity) data.FlowHistory) |> Option.defaultValue data.FlowHistory
                                { data with
                                    Words = Map.remove name data.Words
                                    WordIds = Map.remove name data.WordIds
                                    Tests = data.Tests |> Map.filter (fun _ test -> test.Word <> name)
                                    Examples = data.Examples |> Map.filter (fun _ example -> example.Word <> name)
                                    FlowWords = flowWords
                                    FlowTests = flowTests
                                    FlowTestFiles = flowTestFiles
                                    FlowExamples = flowExamples
                                    FlowHistory = flowHistory }
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
                                TypeSources = Map.remove name data.TypeSources
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
                                TypeSources = Map.remove name data.TypeSources
                                Tests = data.Tests |> Map.filter (fun _ test -> not (owns test.Word))
                                Examples = data.Examples |> Map.filter (fun _ example -> not (owns example.Word)) }
                        let executable = compileRuntimeSnapshot proposed
                        activateRuntimeSnapshot executable
                        lastResults <- []
                        success "discard" $"Discarded candidate scalar type '{name}'." None
                    | None when data.Enums.TryFind name |> Option.exists (fun value -> value.Status = Candidate) ->
                        let prefix = name + "."
                        let proposed =
                            { data with
                                Enums = Map.remove name data.Enums
                                TypeSources = Map.remove name data.TypeSources
                                Tests = data.Tests |> Map.filter (fun _ test -> not (test.Word.StartsWith(prefix, StringComparison.Ordinal)))
                                Examples = data.Examples |> Map.filter (fun _ example -> not (example.Word.StartsWith(prefix, StringComparison.Ordinal))) }
                        let executable = compileRuntimeSnapshot proposed
                        activateRuntimeSnapshot executable
                        lastResults <- []
                        success "discard" $"Discarded candidate enum type '{name}'." None
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
                                let executable = validateStoredProject projectStore (Some snapshot.Manifest) (Some snapshot.ManifestHash) (Some projectSource)
                                let previousClock = fixedClock
                                fixedClock <- defaultArg snapshot.ClockValue "2000-01-01T00:00:00Z"
                                try
                                    requalifyDurableLibraries executable
                                finally
                                    fixedClock <- previousClock
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
                    if args.ContainsKey "type" && args.ContainsKey "word" then
                        error "TYPE_QUERY_AMBIGUOUS_SELECTOR" "History accepts exactly one of 'word' or 'type'." None None [ "one selector" ] [ "word and type" ]
                    elif args.ContainsKey "type" then
                        let typeName = requiredFlowString args "type"
                        if not ((knownTypes data).Contains typeName) then
                            error "HISTORY_TYPE_UNKNOWN" $"No durable history exists for type '{typeName}'." (Some typeName) None [] []
                        else
                            match store, currentManifest, currentManifestHash with
                            | Some projectStore, Some manifest, Some manifestHash when manifest.Types |> List.exists (fun item -> item.Name = typeName) ->
                                log "inspect" typeName
                                match Storage.readTypeHistory projectStore manifestHash typeName with
                                | Error storageError -> raiseStorageError storageError
                                | Ok revisions ->
                                    let rows = JsonArray()
                                    for content in revisions do
                                        let row = JsonObject()
                                        row["revision"] <- jint content.Revision.Revision
                                        row["source"] <- jstr content.Source
                                        row["sourceHash"] <- jstr content.Revision.Definition.Hash
                                        row["sourceFormat"] <- sourceFormatJson content.Revision.SourceFormat
                                        row["validatorTarget"] <- content.Revision.ValidatorTarget |> Option.map storedCallTargetJson |> Option.defaultValue null
                                        row["current"] <- jbool content.IsCurrent
                                        rows.Add row
                                    let payload = JsonObject()
                                    payload["type"] <- jstr typeName
                                    payload["revisions"] <- rows
                                    success "history" $"{revisions.Length} durable type-source revision(s) for {typeName}." (Some payload)
                            | _ ->
                                error "HISTORY_STORAGE_REQUIRED" $"Type history for '{typeName}' is available only after the type source is committed." (Some typeName) None [ "committed type source" ] []
                    else
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
                            let flowRevisions =
                                data.WordIds.TryFind name
                                |> Option.bind (fun identity -> data.FlowHistory.TryFind identity)
                                |> Option.defaultValue []
                            match flowRevisions with
                            | _ :: _ ->
                                let payload =
                                    flowRevisions
                                    |> List.map (fun authored ->
                                        {| revision = authored.Source.OwnerRevision
                                           source = authored.Source.Content
                                           maturity = if (data.Words.TryFind name |> Option.exists (fun word -> word.Maturity = LibraryWord)) then "library" else "project" |})
                                success "history" $"{flowRevisions.Length} revision(s) for {name}." (Some(jsonNode payload))
                            | [] ->
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
                            let flowSource =
                                data.WordIds.TryFind name
                                |> Option.bind (fun identity -> data.FlowHistory.TryFind identity)
                                |> Option.defaultValue []
                                |> List.tryFind (fun authored -> authored.Source.OwnerRevision = revision)
                                |> Option.map (fun authored -> authored.Source.Content)
                            flowSource
                            |> Option.orElseWith (fun () ->
                                data.History.TryFind name
                                |> Option.defaultValue []
                                |> List.tryFind (fun definition -> definition.Revision = revision)
                                |> Option.map (Source.renderWord true))
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
