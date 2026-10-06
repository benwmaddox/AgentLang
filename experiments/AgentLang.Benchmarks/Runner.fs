namespace AgentLang.Benchmarks

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open AgentLang

type RunMode =
    | Flat
    | Growing

/// Baseline profile describes starting vocabulary, independently of retention mode.
[<RequireQualifiedAccess>]
type BaselineProfile =
    | DomainSeededControl
    | PrimitiveOnly

[<RequireQualifiedAccess>]
type HarnessTestFailurePoint =
    | BeforeInitialLineageMarkerWrite
    | AfterFinalLineageMarkerWrite

type OracleStep =
    { Operation: string
      Arguments: JsonObject
      ExpectedOk: bool
      ExpectedTextContains: string option
      ExpectedData: JsonNode option
      MinimumPassingTests: int option }

type ExperimentTask =
    { Id: string
      Goal: string
      SystemPrompt: string
      InitialContext: string
      Oracle: OracleStep list }

type RunConfig =
    { Task: ExperimentTask
      Mode: RunMode
      Frontend: SourceFrontend
      Model: string
      ProjectDirectory: string
      RunDirectory: string
      BaselineProfile: BaselineProfile
      SeedDictionarySource: string option
      TestFailurePoint: HarnessTestFailurePoint option
      MaxTurns: int
      MaxToolCalls: int
      MaxOutputTokens: int
      ContextBudgetBytes: int }

type OracleOutcome =
    { Operation: string
      Passed: bool
      Reason: string
      Response: JsonObject }

type RunReport =
    { RunId: string
      TaskId: string
      Mode: string
      Frontend: string
      Provider: string
      Model: string
      Success: bool
      Status: string
      FailureCode: string option
      FailureMessage: string option
      StartedAt: string
      FinishedAt: string
      DurationMilliseconds: int64
      Turns: int
      ToolCalls: int
      RequestCount: int
      TotalRequestBytes: int64
      PreparedRequestBytes: int64
      PeakRequestBytes: int64
      ContextBudgetBytes: int
      EstimatedRequestTokens: int64
      EstimateMethod: string
      InputTokens: int option
      OutputTokens: int option
      TotalTokens: int option
      Oracle: OracleOutcome list
      BaselineAudit: JsonObject
      ProjectDirectory: string }

type private BaselineLineage =
    { Profile: BaselineProfile
      OriginInventory: JsonObject
      OriginCanonicalStateHash: string
      LastCanonicalStateHash: string
      SeedSourceHash: string option
      SeedSourceApplied: bool }

exception private BaselineGuardFailure of string * string

module TaskFile =
    let private requiredString root key =
        Json.tryProperty root key
        |> Option.bind Json.tryString
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.defaultWith (fun () -> invalidOp $"Task file requires non-empty string '{key}'.")

    let private readOracleStep node =
        let operation = requiredString node "operation"
        let args =
            match Json.tryProperty node "args" with
            | None -> JsonObject()
            | Some value -> Json.asObject value |> Option.defaultWith (fun () -> invalidOp "Oracle args must be a JSON object.")
        let expectedOk =
            match Json.tryProperty node "expectOk" with
            | Some value -> try value.GetValue<bool>() with _ -> invalidOp "Oracle expectOk must be a boolean."
            | None -> true
        let expectedText = Json.tryProperty node "textContains" |> Option.bind Json.tryString
        let expectedData = Json.tryProperty node "data" |> Option.map (fun value -> value.DeepClone())
        let minimumPassing = Json.propertyInt node "minimumPassingTests"
        if minimumPassing |> Option.exists (fun value -> value < 0) then invalidOp "minimumPassingTests cannot be negative."
        { Operation = operation
          Arguments = args
          ExpectedOk = expectedOk
          ExpectedTextContains = expectedText
          ExpectedData = expectedData
          MinimumPassingTests = minimumPassing }

    let private languagePrimer (frontend: SourceFrontend) =
        let rules =
            "Inspect words before use and prefer composing existing vocabulary. The define tool requires lifetime `candidate` or `temporary`; temporary words disappear when the task ends. Test your changes. A project commit requires passing tests; a library commit additionally requires tests to execute every instruction and every control-flow outcome, including both `if` branches. The task tool's `quality` is `project` or `library`. Use only supplied runtime tools and finish with a concise result."
        let syntax =
            match frontend with
            | SourceFrontend.Flow ->
                "You work inside AgentLang's strongly typed Flow language. Define with `word name(input: Type) -> Output { ... }`; declare `effects none` or effects inside the body. Call words as `namespace::word(arguments)` or use receiver dot stages; bind immutable locals with `let name = expression;`. Branch with `if condition { ... } else { ... }`. A test is `test word/case { expression => expected; }`; attach tests in the definition source."
            | SourceFrontend.Stack ->
                "You work inside AgentLang's strongly typed concatenative Stack language. Define with `word name : Input -> Output`, then `effects none` or declared effects, a body, and `end`. Locals use `let name` to bind the top value and `$name` to read it. `if` has `else` and `end`. A test is `test word/case`, a body, `=> expected-literal`, `end`; attach tests in the definition source."
        syntax + " " + rules

    let parseWithFrontend (frontend: SourceFrontend) (text: string) =
        let root =
            match JsonNode.Parse(text) |> Json.asObject with
            | Some value -> value
            | None -> invalidOp "Task file must be a JSON object."
        let oracle =
            match Json.tryProperty root "oracle" |> Option.bind Json.asArray with
            | Some items -> items |> Seq.map readOracleStep |> Seq.toList
            | None -> invalidOp "Task file requires an oracle array."
        if List.isEmpty oracle then invalidOp "Task file oracle must contain at least one check."
        let primer = languagePrimer frontend
        let taskSpecificPrompt = Json.propertyString root "systemPrompt" ""
        { Id = requiredString root "id"
          Goal = requiredString root "goal"
          SystemPrompt = if String.IsNullOrWhiteSpace taskSpecificPrompt then primer else $"{primer}\nTask-specific guidance: {taskSpecificPrompt}"
          InitialContext = Json.propertyString root "initialContext" ""
          Oracle = oracle }

    let parse (text: string) = parseWithFrontend SourceFrontend.Flow text

    let loadWithFrontend frontend path = File.ReadAllText(path) |> parseWithFrontend frontend

    let load path = loadWithFrontend SourceFrontend.Flow path

module Runner =
    exception private ProjectSnapshotFailure of StorageError

    type private ProjectCheckpoint =
        { Storage: StoreSnapshot
          TaskHistory: (string * string) list }

    let private maximumTaskHistoryFiles = 10_000
    let private maximumTaskHistoryFileBytes = int64 StorageLimits.MaxMetadataBytes
    let private maximumTaskHistoryBytes = 32L * 1024L * 1024L

    let private serializer = JsonSerializerOptions(WriteIndented = true)

    let private nodeOption (value: JsonNode option) =
        match value with
        | Some node -> node.DeepClone()
        | None -> null

    let private storageErrorMessage (error: StorageError) =
        match error.Path with
        | Some path -> $"{error.Message} ({path})"
        | None -> error.Message

    let private taskHistoryFailure code message path =
        raise (ProjectSnapshotFailure { Code = code; Message = message; Path = Some path })

    let private ensureSafeHistoryPath (path: string) (expectedDirectory: bool option) (allowMissingFinal: bool) =
        let fullPath = Path.GetFullPath(path)
        let volumeRoot = Path.GetPathRoot(fullPath)
        if String.IsNullOrWhiteSpace volumeRoot then
            taskHistoryFailure "PROJECT_HISTORY_PATH_INVALID" "The project history path has no filesystem root." fullPath
        let relative = Path.GetRelativePath(volumeRoot, fullPath)
        let segments =
            if relative = "." then [||]
            else relative.Split([| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |], StringSplitOptions.RemoveEmptyEntries)
        let mutable current = volumeRoot
        for index = 0 to segments.Length do
            if index > 0 then current <- Path.Combine(current, segments[index - 1])
            let isFinal = index = segments.Length
            let attributes =
                try Some(File.GetAttributes(current))
                with
                | :? FileNotFoundException when isFinal && allowMissingFinal -> None
                | :? DirectoryNotFoundException when isFinal && allowMissingFinal -> None
                | ex ->
                    taskHistoryFailure
                        "PROJECT_HISTORY_PATH_INVALID"
                        $"Could not inspect the history path component ({ex.GetType().Name})."
                        current
            match attributes with
            | None -> ()
            | Some value ->
                if (value &&& FileAttributes.ReparsePoint) <> enum<FileAttributes> 0 then
                    taskHistoryFailure "PROJECT_HISTORY_REPARSE_POINT" "Project history cannot use a reparse point or symbolic link." current
                let isDirectory = (value &&& FileAttributes.Directory) <> enum<FileAttributes> 0
                if not isFinal && not isDirectory then
                    taskHistoryFailure "PROJECT_HISTORY_PATH_INVALID" "A project history path ancestor is not a directory." current
                if isFinal then
                    match expectedDirectory with
                    | Some true when not isDirectory ->
                        taskHistoryFailure "PROJECT_HISTORY_PATH_INVALID" "The project history path is not a directory." current
                    | Some false when isDirectory ->
                        taskHistoryFailure "PROJECT_HISTORY_PATH_INVALID" "A project task history entry is a directory." current
                    | _ -> ()

    let private taskHistoryFiles historyPath =
        ensureSafeHistoryPath historyPath (Some true) true
        let attributes =
            try Some(File.GetAttributes(historyPath))
            with
            | :? FileNotFoundException
            | :? DirectoryNotFoundException -> None
            | ex ->
                taskHistoryFailure
                    "PROJECT_HISTORY_IO"
                    $"Could not inspect project task history ({ex.GetType().Name})."
                    historyPath
        match attributes with
        | None -> []
        | Some value when (value &&& FileAttributes.Directory) = enum<FileAttributes> 0 ->
            taskHistoryFailure "PROJECT_HISTORY_PATH_INVALID" "The project history path is not a directory." historyPath
        | Some _ ->
            try
                let entries = ResizeArray<string>()
                use iterator = Directory.EnumerateFiles(historyPath, "task-*.json").GetEnumerator()
                let mutable hasMore = true
                while hasMore && entries.Count <= maximumTaskHistoryFiles do
                    if iterator.MoveNext() then entries.Add(iterator.Current) else hasMore <- false
                if entries.Count > maximumTaskHistoryFiles then
                    taskHistoryFailure "PROJECT_HISTORY_LIMIT" $"Project history exceeds the {maximumTaskHistoryFiles}-file limit." historyPath
                let files = entries.ToArray()
                Array.sortInPlaceWith (fun (left: string) (right: string) -> StringComparer.Ordinal.Compare(left, right)) files
                files
                |> Array.iter (fun file -> ensureSafeHistoryPath file (Some false) false)
                Array.toList files
            with
            | ProjectSnapshotFailure _ as ex -> raise ex
            | ex ->
                taskHistoryFailure
                    "PROJECT_HISTORY_IO"
                    $"Could not enumerate project task history ({ex.GetType().Name})."
                    historyPath

    let private readTaskHistoryFile path currentTotalBytes =
        ensureSafeHistoryPath path (Some false) false
        try
            use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.SequentialScan)
            if stream.Length > maximumTaskHistoryFileBytes then
                taskHistoryFailure "PROJECT_HISTORY_LIMIT" $"Task history files are limited to {maximumTaskHistoryFileBytes} bytes." path
            if currentTotalBytes + stream.Length > maximumTaskHistoryBytes then
                taskHistoryFailure "PROJECT_HISTORY_LIMIT" $"Project task history is limited to {maximumTaskHistoryBytes} UTF-8 bytes." path
            use buffer = new MemoryStream()
            let chunk = Array.zeroCreate<byte> 8192
            let mutable count = stream.Read(chunk, 0, chunk.Length)
            while count > 0 do
                if currentTotalBytes + buffer.Length + int64 count > maximumTaskHistoryBytes
                   || buffer.Length + int64 count > maximumTaskHistoryFileBytes then
                    taskHistoryFailure "PROJECT_HISTORY_LIMIT" "Project task history exceeded its bounded UTF-8 read limit." path
                buffer.Write(chunk, 0, count)
                count <- stream.Read(chunk, 0, chunk.Length)
            let bytes = buffer.ToArray()
            let contents = UTF8Encoding(false, true).GetString(bytes)
            contents, int64 bytes.Length
        with
        | ProjectSnapshotFailure _ as ex -> raise ex
        | :? DecoderFallbackException ->
            taskHistoryFailure "PROJECT_HISTORY_ENCODING" "Task history must contain valid UTF-8." path
        | ex ->
            taskHistoryFailure
                "PROJECT_HISTORY_IO"
                $"Could not read project task history ({ex.GetType().Name})."
                path

    let private responseOk (response: JsonObject) =
        Json.tryProperty response "ok"
        |> Option.bind (fun value -> try value.GetValue<bool>() |> Some with _ -> None)
        |> Option.defaultValue false

    let private deepSubset (expected: JsonNode) (actual: JsonNode) =
        let rec matches (left: JsonNode) (right: JsonNode) =
            match left, right with
            | null, null -> true
            | null, _ | _, null -> false
            | (:? JsonObject as leftObject), (:? JsonObject as rightObject) ->
                leftObject
                |> Seq.forall (fun pair ->
                    match Json.tryProperty rightObject pair.Key with
                    | Some value -> matches pair.Value value
                    | None -> false)
            | (:? JsonArray as leftArray), (:? JsonArray as rightArray) ->
                leftArray.Count = rightArray.Count
                && Seq.forall2 matches leftArray rightArray
            | _ -> Json.compact left = Json.compact right
        matches expected actual

    let private addKnown (current: int option) (next: int option) =
        match current, next with
        | Some left, Some right -> Some(left + right)
        | _ -> None

    let private optionNumber (value: int option) : JsonNode =
        match value with
        | Some number -> Json.integer number
        | None -> null

    let private frontendName = function
        | SourceFrontend.Stack -> "stack"
        | SourceFrontend.Flow -> "flow"

    let private setTaskCall (engine: Runtime.Engine) (frontend: SourceFrontend) (operation: string) (arguments: JsonObject) =
        let args = JsonObject()
        for KeyValue(key, value) in arguments do args[key] <- if isNull value then null else value.DeepClone()
        if (operation = "eval" || operation = "define") && not (args.ContainsKey "frontend") then
            args["frontend"] <- Json.text (frontendName frontend)
        engine.Dispatch(operation, args)

    let private taskHistory (projectDirectory: string) =
        let historyPath = Path.Combine(projectDirectory, "history")
        let files = taskHistoryFiles historyPath
        let mutable totalBytes = 0L
        files
        |> List.map (fun path ->
            let contents, byteCount = readTaskHistoryFile path totalBytes
            totalBytes <- totalBytes + byteCount
            $"history/{Path.GetFileName path}", contents)

    let private baselineProfileName = function
        | BaselineProfile.DomainSeededControl -> "domain-seeded-control"
        | BaselineProfile.PrimitiveOnly -> "primitive-only"

    let private baselineLabel = function
        | BaselineProfile.DomainSeededControl -> "Domain-seeded retention control"
        | BaselineProfile.PrimitiveOnly -> "Primitive-only starting vocabulary"

    let private hashBytes (bytes: byte array) =
        SHA256.HashData(bytes) |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

    let private hashText (value: string) =
        hashBytes (UTF8Encoding(false, true).GetBytes value)

    let private stringArray (values: string list) : JsonArray =
        let result = JsonArray()
        values |> List.iter (Json.text >> result.Add)
        result

    let private inventorySourceHash (loaded: StorageLoadResult) =
        match loaded.Manifest with
        | Some manifest -> Some manifest.ProjectSource.Hash
        | None ->
            match loaded.Authority with
            | LegacyAuthority reference -> Some reference.Hash
            | EmptyAuthority | ManifestAuthority _ -> None

    let private authorityName = function
        | EmptyAuthority -> "empty"
        | LegacyAuthority _ -> "legacy"
        | ManifestAuthority _ -> "manifest"

    type private InventoryWord =
        { Name: string
          StableId: string option
          Inputs: LangType list
          Outputs: LangType list
          Effects: Set<string>
          Maturity: WordMaturity
          Revision: int
          Deprecated: bool }

    type private InventoryDefinitions =
        { Words: InventoryWord list
          Records: RecordDefinition list
          Scalars: ScalarTypeDefinition list
          Tests: string list
          Examples: string list }

    let private inventoryFailure message =
        raise (BaselineGuardFailure("BASELINE_INVENTORY_UNAVAILABLE", message))

    let private parseStackInventorySource file source =
        match Parser.parse file source with
        | Ok parsed -> parsed
        | Error diagnostic -> inventoryFailure $"Could not parse Stack source for its starting inventory: {diagnostic.Code}: {diagnostic.Message}"

    let private parseFlowWordInventory file source =
        match FlowParser.parseWord file source with
        | Ok parsed -> parsed
        | Error diagnostic -> inventoryFailure $"Could not parse Flow word source for its starting inventory: {diagnostic.Code}: {diagnostic.Message}"

    let private parseFlowTestInventory file source =
        match FlowParser.parseTest file source with
        | Ok parsed -> parsed
        | Error diagnostic -> inventoryFailure $"Could not parse Flow test source for its starting inventory: {diagnostic.Code}: {diagnostic.Message}"

    let private parseFlowExampleInventory file source =
        match FlowParser.parseExample file source with
        | Ok parsed -> parsed
        | Error diagnostic -> inventoryFailure $"Could not parse Flow example source for its starting inventory: {diagnostic.Code}: {diagnostic.Message}"

    let private parseFlowTypeInventory file source =
        match FlowParser.parseDocument file source with
        | Ok parsed when parsed.Words.IsEmpty && parsed.Tests.IsEmpty && parsed.Examples.IsEmpty && (parsed.Records.Length + parsed.Scalars.Length = 1) ->
            parsed.Records, parsed.Scalars
        | Ok _ -> inventoryFailure "A Flow type source object must contain exactly one record or scalar declaration."
        | Error diagnostic -> inventoryFailure $"Could not parse Flow type source for its starting inventory: {diagnostic.Code}: {diagnostic.Message}"

    let private exactStackWord file source =
        let parsed = parseStackInventorySource file source
        match parsed.Words, parsed.Records, parsed.Scalars, parsed.Tests, parsed.Examples with
        | [ word ], [], [], [], [] -> word
        | _ -> inventoryFailure "A Stack word revision source object did not contain exactly one word."

    let private exactStackTest file source =
        let parsed = parseStackInventorySource file source
        match parsed.Tests, parsed.Words, parsed.Records, parsed.Scalars, parsed.Examples with
        | [ test ], [], [], [], [] -> test.Word, test.Name
        | _ -> inventoryFailure "A Stack test source object did not contain exactly one test."

    let private exactStackExample file source =
        let parsed = parseStackInventorySource file source
        match parsed.Examples, parsed.Words, parsed.Records, parsed.Scalars, parsed.Tests with
        | [ example ], [], [], [], [] -> example.Word, example.Name
        | _ -> inventoryFailure "A Stack example source object did not contain exactly one example."

    let private stackSectionsForLegacyManifest (source: string) =
        let sections = ResizeArray<string>()
        let current = ResizeArray<string>()
        let mutable frontend = "stack"
        let mutable sawMarker = false
        let flush () =
            while current.Count > 0 && String.IsNullOrWhiteSpace current[current.Count - 1] do
                current.RemoveAt(current.Count - 1)
            if frontend = "stack" && current.Count > 0 then sections.Add(String.Join("\n", current))
            current.Clear()
        for line in source.Replace("\r\n", "\n").Split('\n') do
            if line.StartsWith("// frontend: ", StringComparison.Ordinal) then
                flush ()
                sawMarker <- true
                frontend <-
                    match line with
                    | "// frontend: stack/1" -> "stack"
                    | "// frontend: flow/1" -> "flow"
                    | _ -> inventoryFailure $"Unsupported source section marker in the historical manifest: {line}"
            elif not sawMarker || frontend = "stack" then
                current.Add line
        flush ()
        String.concat "\n\n" sections

    let private baselineInventory (projectDirectory: string) (loaded: StorageLoadResult) =
        let store = Storage.create projectDirectory
        let readSource reference =
            match Storage.readSource store reference with
            | Ok source -> source
            | Error error -> inventoryFailure $"Could not verify source object {error.Code} for the starting inventory: {error.Message}"

        let inventoryDefinitions =
            match loaded.Manifest, loaded.Authority with
            | Some manifest, ManifestAuthority _ ->
                let manifestHash = loaded.ManifestHash |> Option.defaultWith (fun () -> inventoryFailure "The authoritative manifest has no hash for inventory verification.")
                let words = ResizeArray<InventoryWord>()
                let tests = ResizeArray<string>()
                let examples = ResizeArray<string>()
                for head in manifest.Words |> List.sortBy _.CurrentName do
                    let metadata =
                        manifest.Revisions
                        |> List.tryFind (fun revision -> revision.WordId = head.WordId && revision.Revision = head.CurrentRevision)
                        |> Option.defaultWith (fun () -> inventoryFailure $"Current word head '{head.CurrentName}' has no matching revision source metadata.")
                    let content =
                        match Storage.readRevision store manifestHash head.WordId head.CurrentRevision with
                        | Ok value -> value
                        | Error error -> inventoryFailure $"Could not verify current revision for '{head.CurrentName}' ({error.Code}): {error.Message}"
                    if metadata.Name <> head.CurrentName || content.Revision.Name <> head.CurrentName then
                        inventoryFailure $"Current word metadata for '{head.CurrentName}' refers to a different source name."
                    let inputs, outputs, effects =
                        match metadata.SourceFormat.Frontend, metadata.SourceFormat.Version with
                        | SourceFrontend.Stack, 1 ->
                            let definition = exactStackWord $"<baseline:{head.CurrentName}/{head.CurrentRevision}>" content.DefinitionSource
                            if definition.Name <> head.CurrentName then inventoryFailure $"Stack revision metadata for '{head.CurrentName}' does not match its exact source object."
                            definition.Inputs, definition.Outputs, definition.Effects
                        | SourceFrontend.Flow, 1 ->
                            let definition = parseFlowWordInventory $"<baseline:{head.CurrentName}/{head.CurrentRevision}>" content.DefinitionSource
                            if definition.Name <> head.CurrentName then inventoryFailure $"Flow revision metadata for '{head.CurrentName}' does not match its exact source object."
                            definition.Parameters |> List.map _.Type, definition.Outputs, definition.Effects
                        | frontend, version -> inventoryFailure $"Current word '{head.CurrentName}' uses unsupported {frontend}/{version} source metadata."
                    words.Add
                        { Name = head.CurrentName
                          StableId = Some head.WordId
                          Inputs = inputs
                          Outputs = outputs
                          Effects = effects
                          Maturity = metadata.Maturity
                          Revision = metadata.Revision
                          Deprecated = head.Deprecated }
                    for source in content.TestSources do
                        match metadata.SourceFormat.Frontend, metadata.SourceFormat.Version with
                        | SourceFrontend.Stack, 1 ->
                            let owner, caseName = exactStackTest $"<baseline:{head.CurrentName}/{head.CurrentRevision}/test>" source
                            if owner <> head.CurrentName then inventoryFailure $"Stack test for '{head.CurrentName}' is attached to a different source word."
                            tests.Add(owner + "/" + caseName)
                        | SourceFrontend.Flow, 1 ->
                            let test = parseFlowTestInventory $"<baseline:{head.CurrentName}/{head.CurrentRevision}/test>" source
                            if test.Word <> head.CurrentName then inventoryFailure $"Flow test for '{head.CurrentName}' is attached to a different source word."
                            tests.Add(test.Word + "/" + test.CaseName)
                        | frontend, version -> inventoryFailure $"Current test for '{head.CurrentName}' uses unsupported {frontend}/{version} source metadata."
                    for source in content.ExampleSources do
                        match metadata.SourceFormat.Frontend, metadata.SourceFormat.Version with
                        | SourceFrontend.Stack, 1 ->
                            let owner, caseName = exactStackExample $"<baseline:{head.CurrentName}/{head.CurrentRevision}/example>" source
                            if owner <> head.CurrentName then inventoryFailure $"Stack example for '{head.CurrentName}' is attached to a different source word."
                            examples.Add(owner + "/" + caseName)
                        | SourceFrontend.Flow, 1 ->
                            let example = parseFlowExampleInventory $"<baseline:{head.CurrentName}/{head.CurrentRevision}/example>" source
                            if example.Word <> head.CurrentName then inventoryFailure $"Flow example for '{head.CurrentName}' is attached to a different source word."
                            examples.Add(example.Word + "/" + example.CaseName)
                        | frontend, version -> inventoryFailure $"Current example for '{head.CurrentName}' uses unsupported {frontend}/{version} source metadata."

                let records = ResizeArray<RecordDefinition>()
                let scalars = ResizeArray<ScalarTypeDefinition>()
                for typeSource in manifest.Types |> List.sortBy _.Name do
                    let source = readSource typeSource.Definition
                    let typeRecords, typeScalars =
                        match typeSource.SourceFormat.Frontend, typeSource.SourceFormat.Version with
                        | SourceFrontend.Stack, 1 ->
                            let parsed = parseStackInventorySource $"<baseline:type:{typeSource.Name}/{typeSource.Definition.Hash}>" source
                            match parsed.Records, parsed.Scalars, parsed.Words, parsed.Tests, parsed.Examples with
                            | [ record ], [], [], [], [] when record.Name = typeSource.Name -> [ record ], []
                            | [], [ scalar ], [], [], [] when scalar.Name = typeSource.Name -> [], [ scalar ]
                            | _ -> inventoryFailure $"Stack type source object for '{typeSource.Name}' did not contain exactly that type."
                        | SourceFrontend.Flow, 1 ->
                            let flowRecords, flowScalars = parseFlowTypeInventory $"<baseline:type:{typeSource.Name}/{typeSource.Definition.Hash}>" source
                            match flowRecords, flowScalars with
                            | [ record ], [] when record.Name = typeSource.Name -> flowRecords, flowScalars
                            | [], [ scalar ] when scalar.Name = typeSource.Name -> flowRecords, flowScalars
                            | _ -> inventoryFailure $"Flow type source object for '{typeSource.Name}' did not contain exactly that type."
                        | frontend, version -> inventoryFailure $"Type '{typeSource.Name}' uses unsupported {frontend}/{version} source metadata."
                    records.AddRange typeRecords
                    scalars.AddRange typeScalars

                // Manifests predating type source objects stored Stack types only in their
                // canonical export. Parse only explicitly marked Stack sections; Flow text
                // is never passed through the Stack parser.
                if manifest.Types.IsEmpty && manifest.FormatVersion < 3 then
                    let source = loaded.ProjectSource |> Option.defaultValue ""
                    let stackSource = stackSectionsForLegacyManifest source
                    let parsed = parseStackInventorySource "<baseline:historical-stack-types>" stackSource
                    records.AddRange parsed.Records
                    scalars.AddRange parsed.Scalars

                { Words = List.ofSeq words
                  Records = List.ofSeq records
                  Scalars = List.ofSeq scalars
                  Tests = List.ofSeq tests
                  Examples = List.ofSeq examples }
            | None, EmptyAuthority
            | None, LegacyAuthority _ ->
                let source = loaded.ProjectSource |> Option.defaultValue ""
                let parsed = parseStackInventorySource "<baseline-legacy-stack>" source
                { Words =
                    parsed.Words
                    |> List.map (fun word ->
                        { Name = word.Name
                          StableId = None
                          Inputs = word.Inputs
                          Outputs = word.Outputs
                          Effects = word.Effects
                          Maturity = word.Maturity
                          Revision = word.Revision
                          Deprecated = false })
                  Records = parsed.Records
                  Scalars = parsed.Scalars
                  Tests = parsed.Tests |> List.map (fun test -> test.Word + "/" + test.Name)
                  Examples = parsed.Examples |> List.map (fun example -> example.Word + "/" + example.Name) }
            | _ -> inventoryFailure "Storage authority and manifest metadata disagree while reading the starting inventory."

        let words = JsonArray()
        inventoryDefinitions.Words
        |> List.sortBy (fun word -> word.Name)
        |> List.iter (fun definition ->
            let item = JsonObject()
            item["name"] <- Json.text definition.Name
            item["stableId"] <- definition.StableId |> Option.map Json.text |> nodeOption
            item["inputs"] <- stringArray (definition.Inputs |> List.map Types.format)
            item["outputs"] <- stringArray (definition.Outputs |> List.map Types.format)
            item["effects"] <- stringArray (definition.Effects |> Set.toList)
            item["maturity"] <- Json.text (if definition.Maturity = LibraryWord then "library" else "project")
            item["revision"] <- Json.integer definition.Revision
            item["deprecated"] <- Json.bool definition.Deprecated
            words.Add item)
        let records = JsonArray()
        inventoryDefinitions.Records
        |> List.sortBy (fun record -> record.Name)
        |> List.iter (fun definition ->
            let item = JsonObject()
            item["name"] <- Json.text definition.Name
            let fields = JsonArray()
            definition.Fields
            |> List.iter (fun field ->
                let fieldItem = JsonObject()
                fieldItem["name"] <- Json.text field.Name
                fieldItem["type"] <- Json.text (Types.format field.Type)
                fields.Add fieldItem)
            item["fields"] <- fields
            records.Add item)
        let scalars = JsonArray()
        inventoryDefinitions.Scalars
        |> List.sortBy (fun scalar -> scalar.Name)
        |> List.iter (fun definition ->
            let item = JsonObject()
            item["name"] <- Json.text definition.Name
            item["baseType"] <- Json.text (Types.format definition.BaseType)
            item["validator"] <- definition.Validator |> Option.map Json.text |> nodeOption
            scalars.Add item)
        let tests = inventoryDefinitions.Tests |> List.sort |> stringArray
        let examples = inventoryDefinitions.Examples |> List.sort |> stringArray
        let result = JsonObject()
        result["attachmentNameFormat"] <- Json.text "word-case/1"
        result["storageAuthority"] <- Json.text (authorityName loaded.Authority)
        result["manifestHash"] <- loaded.ManifestHash |> Option.map Json.text |> nodeOption
        result["projectSourceSha256"] <- inventorySourceHash loaded |> Option.map Json.text |> nodeOption
        result["authoredWords"] <- words
        result["records"] <- records
        result["scalars"] <- scalars
        result["tests"] <- tests
        result["examples"] <- examples
        result, inventoryDefinitions

    let private inventoryHasAuthoredAlgorithms (parsed: InventoryDefinitions) =
        not parsed.Words.IsEmpty || not parsed.Tests.IsEmpty || not parsed.Examples.IsEmpty

    let private inventoryViolations (parsed: InventoryDefinitions) =
        [ yield! parsed.Words |> List.map (fun word -> "word " + word.Name)
          yield! parsed.Tests |> List.map (fun test -> "test " + test)
          yield! parsed.Examples |> List.map (fun example -> "example " + example) ]

    let private readSeedSourceBytes (path: string option) =
        path
        |> Option.map (fun value ->
            if not (File.Exists value) then raise (BaselineGuardFailure("BASELINE_SEED_SOURCE_MISSING", $"Seed source does not exist: {value}"))
            File.ReadAllBytes value)

    let private decodeSeedSource (bytes: byte array) =
        use stream = new MemoryStream(bytes, false)
        use reader = new StreamReader(stream, UTF8Encoding(false, true), true)
        reader.ReadToEnd()

    let private injectTestFailure (config: RunConfig) (point: HarnessTestFailurePoint) =
        if config.TestFailurePoint = Some point then
            raise (InvalidOperationException($"Injected harness failure at {point}."))

    let private canonicalDurableStateHash (projectDirectory: string) =
        let loaded =
            match Storage.load (Storage.create projectDirectory) with
            | Ok result -> result
            | Error error -> raise (ProjectSnapshotFailure error)
        let authority = JsonObject()
        match loaded.Authority with
        | EmptyAuthority -> authority["kind"] <- Json.text "empty"
        | LegacyAuthority reference ->
            authority["kind"] <- Json.text "legacy"
            authority["sourceHash"] <- Json.text reference.Hash
        | ManifestAuthority manifestHash ->
            authority["kind"] <- Json.text "manifest"
            authority["manifestHash"] <- Json.text manifestHash
        let history = JsonArray()
        taskHistory projectDirectory
        |> List.iter (fun (path, contents) ->
            let item = JsonObject()
            item["path"] <- Json.text path
            item["content"] <- Json.text contents
            history.Add item)
        let state = JsonObject()
        state["format"] <- Json.text "AgentLang.BenchmarkDurableState.v1"
        state["authority"] <- authority
        state["projectSourceSha256"] <- inventorySourceHash loaded |> Option.map Json.text |> nodeOption
        state["taskHistory"] <- history
        hashText (Json.compact state)

    let private lineagePath (projectDirectory: string) =
        Path.Combine(projectDirectory, ".agentlang-benchmark-lineage.json")

    let private lineageNode (lineage: BaselineLineage) =
        let result = JsonObject()
        result["schemaVersion"] <- Json.integer 2
        result["profile"] <- Json.text (baselineProfileName lineage.Profile)
        result["originInventory"] <- lineage.OriginInventory.DeepClone()
        result["originCanonicalDurableStateSha256"] <- Json.text lineage.OriginCanonicalStateHash
        result["lastCanonicalDurableStateSha256"] <- Json.text lineage.LastCanonicalStateHash
        result["seedSourceSha256"] <- lineage.SeedSourceHash |> Option.map Json.text |> nodeOption
        result["seedSourceApplied"] <- Json.bool lineage.SeedSourceApplied
        result

    let private sameLineageOrigin (left: BaselineLineage) (right: BaselineLineage) =
        left.Profile = right.Profile
        && left.OriginCanonicalStateHash = right.OriginCanonicalStateHash
        && left.SeedSourceHash = right.SeedSourceHash
        && left.SeedSourceApplied = right.SeedSourceApplied
        && Json.compact (left.OriginInventory :> JsonNode) = Json.compact (right.OriginInventory :> JsonNode)

    let private parseBaselineProfile (value: string) =
        match value with
        | "domain-seeded-control" -> Some BaselineProfile.DomainSeededControl
        | "primitive-only" -> Some BaselineProfile.PrimitiveOnly
        | _ -> None

    let private isLowerSha256 (value: string) =
        value.Length = 64
        && (value
            |> Seq.forall (fun character ->
                (character >= '0' && character <= '9')
                || (character >= 'a' && character <= 'f')))

    let private nonBlankStringProperty (node: JsonNode) (name: string) =
        Json.tryProperty node name
        |> Option.bind Json.tryString
        |> Option.exists (String.IsNullOrWhiteSpace >> not)

    let private nullableStringProperty (node: JsonNode) (name: string) =
        match Json.tryProperty node name with
        | Some value when isNull value -> true
        | Some value -> Json.tryString value |> Option.exists (String.IsNullOrWhiteSpace >> not)
        | None -> false

    let private nullableHashProperty (node: JsonNode) (name: string) =
        match Json.tryProperty node name with
        | Some value when isNull value -> true
        | Some value -> Json.tryString value |> Option.exists isLowerSha256
        | None -> false

    let private optionalHashValue (node: JsonNode) (name: string) =
        Json.tryProperty node name
        |> Option.map (fun value -> if isNull value then None else Json.tryString value)

    let private boolProperty (node: JsonNode) (name: string) =
        Json.tryProperty node name
        |> Option.exists (fun value -> try value.GetValue<bool>() |> ignore; true with _ -> false)

    let private positiveIntProperty (node: JsonNode) (name: string) =
        Json.tryProperty node name
        |> Option.exists (fun value -> try value.GetValue<int>() > 0 with _ -> false)

    let private stringArrayProperty (node: JsonNode) (name: string) =
        Json.tryProperty node name
        |> Option.bind Json.asArray
        |> Option.exists (fun items ->
            items
            |> Seq.forall (fun item ->
                Json.tryString item
                |> Option.exists (String.IsNullOrWhiteSpace >> not)))

    let private validQualifiedAttachmentName (name: string) =
        let separator = name.IndexOf('/')
        separator > 0
        && separator < name.Length - 1
        && not (String.IsNullOrWhiteSpace(name.Substring(0, separator)))
        && not (String.IsNullOrWhiteSpace(name.Substring(separator + 1)))

    let private validQualifiedAttachmentArray (node: JsonObject) (name: string) =
        Json.tryProperty (node :> JsonNode) name
        |> Option.bind Json.asArray
        |> Option.exists (fun items ->
            items
            |> Seq.forall (fun item ->
                Json.tryString item
                |> Option.exists validQualifiedAttachmentName))

    let private validAttachmentNameFormat (inventory: JsonObject) =
        match Json.tryProperty (inventory :> JsonNode) "attachmentNameFormat" with
        | None -> true // Version-2 lineage markers created before this field used case-only names.
        | Some value when Json.tryString value = Some "word-case/1" ->
            validQualifiedAttachmentArray inventory "tests"
            && validQualifiedAttachmentArray inventory "examples"
        | _ -> false

    let private validWordInventoryItem (item: JsonNode) =
        let validStableId = nullableStringProperty item "stableId"
        let validMaturity =
            Json.tryProperty item "maturity"
            |> Option.bind Json.tryString
            |> Option.exists (fun value -> value = "project" || value = "library")
        nonBlankStringProperty item "name"
        && validStableId
        && stringArrayProperty item "inputs"
        && stringArrayProperty item "outputs"
        && stringArrayProperty item "effects"
        && validMaturity
        && positiveIntProperty item "revision"
        && boolProperty item "deprecated"

    let private validRecordFieldInventoryItem (item: JsonNode) =
        nonBlankStringProperty item "name" && nonBlankStringProperty item "type"

    let private validRecordInventoryItem (item: JsonNode) =
        nonBlankStringProperty item "name"
        && (Json.tryProperty item "fields"
            |> Option.bind Json.asArray
            |> Option.exists (Seq.forall validRecordFieldInventoryItem))

    let private validScalarInventoryItem (item: JsonNode) =
        nonBlankStringProperty item "name"
        && nonBlankStringProperty item "baseType"
        && nullableStringProperty item "validator"

    let private arrayItemsSatisfy (node: JsonObject) (name: string) (predicate: JsonNode -> bool) =
        Json.tryProperty (node :> JsonNode) name
        |> Option.bind Json.asArray
        |> Option.exists (Seq.forall predicate)

    let private arrayPropertyIsEmpty (node: JsonObject) (name: string) =
        Json.tryProperty (node :> JsonNode) name
        |> Option.bind Json.asArray
        |> Option.exists (fun items -> items.Count = 0)

    let private validOriginInventory (inventory: JsonObject) =
        let authorityIsValid =
            Json.tryProperty (inventory :> JsonNode) "storageAuthority"
            |> Option.bind Json.tryString
            |> Option.exists (fun value -> value = "empty" || value = "legacy" || value = "manifest")
        let authority = Json.propertyString (inventory :> JsonNode) "storageAuthority" ""
        let manifestHash = optionalHashValue (inventory :> JsonNode) "manifestHash"
        let sourceHash = optionalHashValue (inventory :> JsonNode) "projectSourceSha256"
        let authorityHashesMatch =
            match authority, manifestHash, sourceHash with
            | "empty", Some None, Some None ->
                arrayPropertyIsEmpty inventory "authoredWords"
                && arrayPropertyIsEmpty inventory "records"
                && arrayPropertyIsEmpty inventory "scalars"
                && arrayPropertyIsEmpty inventory "tests"
                && arrayPropertyIsEmpty inventory "examples"
            | "legacy", Some None, Some(Some _) -> true
            | "manifest", Some(Some _), Some(Some _) -> true
            | _ -> false
        authorityIsValid
        && nullableHashProperty (inventory :> JsonNode) "manifestHash"
        && nullableHashProperty (inventory :> JsonNode) "projectSourceSha256"
        && arrayItemsSatisfy inventory "authoredWords" validWordInventoryItem
        && arrayItemsSatisfy inventory "records" validRecordInventoryItem
        && arrayItemsSatisfy inventory "scalars" validScalarInventoryItem
        && stringArrayProperty (inventory :> JsonNode) "tests"
        && stringArrayProperty (inventory :> JsonNode) "examples"
        && validAttachmentNameFormat inventory
        && authorityHashesMatch

    let private readLineage (projectDirectory: string) =
        let path = lineagePath projectDirectory
        if Directory.Exists path then
            raise (BaselineGuardFailure("BASELINE_LINEAGE_INVALID", "The harness lineage marker path is a directory."))
        if not (File.Exists path) then None
        else
            try
                let node = JsonNode.Parse(File.ReadAllText(path, UTF8Encoding(false, true)))
                let version = Json.propertyInt node "schemaVersion" |> Option.defaultValue -1
                let profile = Json.propertyString node "profile" ""
                let origin = Json.tryProperty node "originInventory" |> Option.bind Json.asObject
                let originHash = Json.propertyString node "originCanonicalDurableStateSha256" ""
                let lastHash = Json.propertyString node "lastCanonicalDurableStateSha256" ""
                let seedHash =
                    match Json.tryProperty node "seedSourceSha256" with
                    | Some value when isNull value -> Some None
                    | Some value -> Json.tryString value |> Option.filter isLowerSha256 |> Option.map Some
                    | None -> None
                let seedApplied = Json.tryProperty node "seedSourceApplied" |> Option.bind (fun value -> try value.GetValue<bool>() |> Some with _ -> None)
                match version, parseBaselineProfile profile, origin with
                | 2, Some parsedProfile, Some inventory when
                    isLowerSha256 originHash
                    && isLowerSha256 lastHash
                    && seedHash.IsSome
                    && seedApplied.IsSome
                    && (not seedApplied.Value || seedHash.Value.IsSome)
                    && validOriginInventory inventory ->
                    Some
                        { Profile = parsedProfile
                          OriginInventory = inventory.DeepClone().AsObject()
                          OriginCanonicalStateHash = originHash
                          LastCanonicalStateHash = lastHash
                          SeedSourceHash = seedHash.Value
                          SeedSourceApplied = seedApplied.Value }
                | _ -> raise (BaselineGuardFailure("BASELINE_LINEAGE_INVALID", "The harness lineage marker is malformed or uses an unsupported version."))
            with
            | BaselineGuardFailure _ as error -> raise error
            | _ -> raise (BaselineGuardFailure("BASELINE_LINEAGE_INVALID", "The harness lineage marker could not be read."))

    let private writeLineage (projectDirectory: string) (lineage: BaselineLineage) =
        let path = lineagePath projectDirectory
        if File.Exists path && (File.GetAttributes(path) &&& FileAttributes.ReparsePoint) <> enum<FileAttributes> 0 then
            raise (BaselineGuardFailure("BASELINE_LINEAGE_INVALID", "The harness lineage marker cannot be a reparse point."))
        let temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp"
        try
            let bytes = Encoding.UTF8.GetBytes(Json.compact (lineageNode lineage))
            do
                use stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                stream.Write(bytes, 0, bytes.Length)
                stream.Flush(true)
            if File.Exists path then File.Move(temporaryPath, path, true)
            else File.Move(temporaryPath, path)
        with ex ->
            try if File.Exists temporaryPath then File.Delete temporaryPath with _ -> ()
            raise (BaselineGuardFailure("BASELINE_LINEAGE_WRITE_FAILED", $"Could not atomically write the harness lineage marker ({ex.GetType().Name})."))

    let private ensureGrowingLineage
        (projectDirectory: string)
        (profile: BaselineProfile)
        (inventory: JsonObject)
        (currentStateHash: string)
        (seedHash: string option)
        (seedApplied: bool)
        (hasAuthoredAlgorithms: bool)
        =
        match readLineage projectDirectory with
        | Some existing ->
            if existing.Profile <> profile then
                raise (BaselineGuardFailure("BASELINE_PROFILE_MISMATCH", "A Growing project cannot switch baseline profiles after its lineage is established."))
            if existing.LastCanonicalStateHash <> currentStateHash then
                raise (BaselineGuardFailure("BASELINE_LINEAGE_MISMATCH", "The authoritative durable vocabulary or task history does not match the previous Growing run's final state."))
            if profile = BaselineProfile.PrimitiveOnly then
                let originWords = Json.tryProperty existing.OriginInventory "authoredWords" |> Option.bind Json.asArray |> Option.defaultValue (JsonArray())
                let originTests = Json.tryProperty existing.OriginInventory "tests" |> Option.bind Json.asArray |> Option.defaultValue (JsonArray())
                let originExamples = Json.tryProperty existing.OriginInventory "examples" |> Option.bind Json.asArray |> Option.defaultValue (JsonArray())
                if originWords.Count > 0 || originTests.Count > 0 || originExamples.Count > 0 then
                    raise (BaselineGuardFailure("BASELINE_LINEAGE_INVALID", "Primitive-only lineage contains authored initial words, tests, or examples."))
            match existing.SeedSourceHash, seedHash with
            | Some expected, Some supplied when expected <> supplied ->
                raise (BaselineGuardFailure("BASELINE_SEED_SOURCE_MISMATCH", "A Growing continuation supplied a different seed source than its audited baseline."))
            | None, Some _ ->
                raise (BaselineGuardFailure("BASELINE_SEED_SOURCE_MISMATCH", "This Growing lineage started without a seed source; a new seed source cannot be added during continuation."))
            | _ -> ()
            existing, "continued", Some existing.LastCanonicalStateHash
        | None ->
            let hasPriorHistory = not (List.isEmpty (taskHistory projectDirectory))
            match profile with
            | BaselineProfile.PrimitiveOnly when hasPriorHistory ->
                raise (BaselineGuardFailure("BASELINE_LINEAGE_MISSING", "A Growing primitive-only project with task history but no harness lineage cannot be verified as a fresh primitive baseline."))
            | BaselineProfile.PrimitiveOnly when hasAuthoredAlgorithms ->
                raise (BaselineGuardFailure("BASELINE_INVENTORY_MISMATCH", "Primitive-only initialization contains authored words, tests, or examples."))
            | _ ->
                let status = if hasPriorHistory then "adopted-existing-control" else "fresh"
                let lineage =
                    { Profile = profile
                      OriginInventory = inventory.DeepClone().AsObject()
                      OriginCanonicalStateHash = currentStateHash
                      LastCanonicalStateHash = currentStateHash
                      SeedSourceHash = seedHash
                      SeedSourceApplied = seedApplied }
                lineage, status, None

    let private baselineAuditNode
        (profile: BaselineProfile)
        (inventory: JsonObject)
        (originInventory: JsonObject)
        (currentStateHash: string)
        (originStateHash: string)
        (lineageStatus: string)
        (previousStateHash: string option)
        (seedSourceHash: string option)
        =
        let audit = JsonObject()
        audit["profile"] <- Json.text (baselineProfileName profile)
        audit["label"] <- Json.text (baselineLabel profile)
        audit["schemaProvenance"] <-
            if (Json.tryProperty inventory "records" |> Option.bind Json.asArray |> Option.exists (fun values -> values.Count > 0))
               || (Json.tryProperty inventory "scalars" |> Option.bind Json.asArray |> Option.exists (fun values -> values.Count > 0)) then
                Json.text "user-supplied schema; source hash and exact declarations are recorded"
            else Json.text "empty schema"
        audit["contractEquivalence"] <- Json.text "not established; this audit records declarations and source identity only"
        audit["seedSourceSha256"] <- seedSourceHash |> Option.map Json.text |> nodeOption
        audit["taskStartInventory"] <- inventory.DeepClone()
        audit["originInventory"] <- originInventory.DeepClone()
        audit["originCanonicalDurableStateSha256"] <- Json.text originStateHash
        let lineage = JsonObject()
        lineage["status"] <- Json.text lineageStatus
        lineage["taskStartCanonicalDurableStateSha256"] <- Json.text currentStateHash
        lineage["previousCanonicalDurableStateSha256"] <- previousStateHash |> Option.map Json.text |> nodeOption
        lineage["hashScope"] <- Json.text "authoritative project source/manifest identity and harness task-history files; excludes virtual provider state, clock, and capabilities"
        audit["lineage"] <- lineage
        audit

    let private captureProjectState (projectDirectory: string) =
        let history = taskHistory projectDirectory
        match Storage.capture (Storage.create projectDirectory) with
        | Ok snapshot -> Ok { Storage = snapshot; TaskHistory = history }
        | Error error -> Error error

    let private snapshotOrRaise (result: Result<'snapshot, StorageError>) : 'snapshot =
        match result with
        | Ok snapshot -> snapshot
        | Error error -> raise (ProjectSnapshotFailure error)

    let private stateNode (checkpoint: ProjectCheckpoint) =
        let state = JsonObject()
        let authority = JsonObject()
        match checkpoint.Storage.Authority with
        | EmptyAuthority -> authority["kind"] <- Json.text "empty"
        | LegacyAuthority reference ->
            authority["kind"] <- Json.text "legacy"
            authority["sourceKind"] <- Json.text "legacy-dictionary"
            authority["sourceHash"] <- Json.text reference.Hash
        | ManifestAuthority manifestHash ->
            authority["kind"] <- Json.text "manifest"
            authority["manifestHash"] <- Json.text manifestHash
        state["storageAuthority"] <- authority
        state["generation"] <- JsonValue.Create(checkpoint.Storage.Generation)
        state["dictionary"] <- checkpoint.Storage.ExportText |> Option.map Json.text |> nodeOption
        let history = JsonArray()
        checkpoint.TaskHistory
        |> List.iter (fun (relativePath, content) ->
            let item = JsonObject()
            item["path"] <- Json.text relativePath
            item["content"] <- Json.text content
            history.Add item)
        state["history"] <- history
        state

    let private restoreTaskHistory (projectDirectory: string) (entries: (string * string) list) =
        let historyPath = Path.Combine(projectDirectory, "history")
        let files = taskHistoryFiles historyPath
        for path in files do
            ensureSafeHistoryPath path (Some false) false
        for path in files do
            ensureSafeHistoryPath path (Some false) true
            if File.Exists path then
                try File.Delete path
                with ex ->
                    taskHistoryFailure "PROJECT_HISTORY_IO" $"Could not remove project task history ({ex.GetType().Name})." path
        if not entries.IsEmpty then
            ensureSafeHistoryPath historyPath (Some true) true
            try Directory.CreateDirectory(historyPath) |> ignore
            with ex ->
                taskHistoryFailure "PROJECT_HISTORY_IO" $"Could not create the project history directory ({ex.GetType().Name})." historyPath
            ensureSafeHistoryPath historyPath (Some true) false
            for relative, content in entries do
                let fileName = Path.GetFileName(relative)
                if relative <> $"history/{fileName}"
                   || not (fileName.StartsWith("task-", StringComparison.Ordinal) && fileName.EndsWith(".json", StringComparison.Ordinal)) then
                    taskHistoryFailure "PROJECT_HISTORY_PATH_INVALID" "A captured project task history path is invalid." relative
                let target = Path.Combine(historyPath, fileName)
                ensureSafeHistoryPath target (Some false) true
                try File.WriteAllText(target, content, UTF8Encoding(false))
                with ex ->
                    taskHistoryFailure "PROJECT_HISTORY_IO" $"Could not restore project task history ({ex.GetType().Name})." target
                ensureSafeHistoryPath target (Some false) false

    let private restoreState (projectDirectory: string) (checkpoint: ProjectCheckpoint) =
        ensureSafeHistoryPath (Path.Combine(projectDirectory, "history")) (Some true) true
        let store = Storage.create projectDirectory
        let current = Storage.capture store |> snapshotOrRaise
        match Storage.restore store current.Generation checkpoint.Storage with
        | Error error -> raise (ProjectSnapshotFailure error)
        | Ok result ->
            restoreTaskHistory projectDirectory checkpoint.TaskHistory
            result

    let private writeJson (path: string) (node: JsonNode) =
        File.WriteAllText(path, node.ToJsonString(serializer), UTF8Encoding(false))

    let private errorResponse code message =
        let detail = JsonObject()
        detail["code"] <- Json.text code
        detail["message"] <- Json.text message
        let result = JsonObject()
        result["ok"] <- Json.bool false
        result["kind"] <- Json.text "error"
        result["text"] <- Json.text message
        result["error"] <- detail
        result

    let private commandError code message = Json.compact (errorResponse code message)

    let private toolCallId (item: JsonObject) = Json.propertyString item "call_id" ""

    let private toolCalls (output: JsonArray) =
        output
        |> Seq.choose (fun item ->
            match Json.asObject item with
            | Some value when Json.propertyString value "type" "" = "function_call" -> Some value
            | _ -> None)
        |> Seq.toList

    let private toolOutput callId output =
        let item = JsonObject()
        item["type"] <- Json.text "function_call_output"
        item["call_id"] <- Json.text callId
        item["output"] <- Json.text output
        item :> JsonNode

    let private inputItems initialContext goal =
        let user = JsonObject()
        user["role"] <- Json.text "user"
        let content =
            if String.IsNullOrWhiteSpace initialContext then goal
            else $"{initialContext.Trim()}\n\nTask:\n{goal}"
        user["content"] <- Json.text content
        let items = JsonArray()
        items.Add user
        items

    let private oracleOutcome (engine: Runtime.Engine) (frontend: SourceFrontend) (step: OracleStep) =
        let response = setTaskCall engine frontend step.Operation step.Arguments
        let mutable passed = responseOk response = step.ExpectedOk
        let reasons = ResizeArray<string>()
        if not passed then reasons.Add $"expected ok={step.ExpectedOk}"
        let text = Json.propertyString response "text" ""
        match step.ExpectedTextContains with
        | Some fragment when not (text.Contains(fragment, StringComparison.Ordinal)) ->
            passed <- false
            reasons.Add $"response text does not contain {fragment}"
        | _ -> ()
        match step.ExpectedData with
        | Some expected ->
            let actual = Json.tryProperty response "data" |> Option.defaultValue null
            if not (deepSubset expected actual) then
                passed <- false
                reasons.Add "response data does not contain the expected structure"
        | None -> ()

        let isTest = step.Operation = "test" || step.Operation = "test-all" || step.Operation = "failed-tests"
        let resultNodes =
            Json.tryProperty response "data"
            |> Option.bind (fun node -> Json.tryProperty node "results")
            |> Option.bind Json.asArray
            |> Option.defaultValue (JsonArray())
        let passingTests =
            resultNodes
            |> Seq.filter (fun result -> Json.tryProperty result "passed" |> Option.bind (fun value -> try value.GetValue<bool>() |> Some with _ -> None) = Some true)
            |> Seq.length
        if step.MinimumPassingTests |> Option.exists (fun minimum -> passingTests < minimum) then
            passed <- false
            reasons.Add $"expected at least {step.MinimumPassingTests.Value} passing tests, got {passingTests}"
        if isTest && step.Operation = "failed-tests" && resultNodes.Count <> 0 then
            passed <- false
            reasons.Add $"{resultNodes.Count} failing tests remain"
        elif isTest && step.Operation <> "failed-tests" then
            if resultNodes.Count = 0 then
                passed <- false
                reasons.Add "test oracle ran no tests"
            elif passingTests <> resultNodes.Count then
                passed <- false
                reasons.Add $"{resultNodes.Count - passingTests} test(s) failed"
        let reason = if reasons.Count = 0 then "oracle passed" else String.concat "; " reasons
        { Operation = step.Operation; Passed = passed; Reason = reason; Response = response }

    let private oracleJson (outcome: OracleOutcome) =
        let value = JsonObject()
        value["operation"] <- Json.text outcome.Operation
        value["passed"] <- Json.bool outcome.Passed
        value["reason"] <- Json.text outcome.Reason
        value["response"] <- outcome.Response.DeepClone()
        value

    let private makeReportNode report =
        let node = JsonObject()
        node["runId"] <- Json.text report.RunId
        node["taskId"] <- Json.text report.TaskId
        node["mode"] <- Json.text report.Mode
        node["frontend"] <- Json.text report.Frontend
        node["provider"] <- Json.text report.Provider
        node["model"] <- Json.text report.Model
        node["success"] <- Json.bool report.Success
        node["status"] <- Json.text report.Status
        node["failureCode"] <- report.FailureCode |> Option.map Json.text |> nodeOption
        node["failureMessage"] <- report.FailureMessage |> Option.map Json.text |> nodeOption
        node["startedAt"] <- Json.text report.StartedAt
        node["finishedAt"] <- Json.text report.FinishedAt
        node["durationMilliseconds"] <- JsonValue.Create(report.DurationMilliseconds)
        node["turns"] <- Json.integer report.Turns
        node["toolCalls"] <- Json.integer report.ToolCalls
        node["requestCount"] <- Json.integer report.RequestCount
        let context = JsonObject()
        context["totalRequestBytes"] <- JsonValue.Create(report.TotalRequestBytes)
        context["preparedRequestBytes"] <- JsonValue.Create(report.PreparedRequestBytes)
        context["peakRequestBytes"] <- JsonValue.Create(report.PeakRequestBytes)
        context["budgetBytesPerRequest"] <- Json.integer report.ContextBudgetBytes
        context["estimatedRequestTokens"] <- JsonValue.Create(report.EstimatedRequestTokens)
        context["estimateMethod"] <- Json.text report.EstimateMethod
        node["context"] <- context
        let tokens = JsonObject()
        tokens["inputTokens"] <- optionNumber report.InputTokens
        tokens["outputTokens"] <- optionNumber report.OutputTokens
        tokens["totalTokens"] <- optionNumber report.TotalTokens
        node["tokenUsage"] <- tokens
        let oracle = JsonArray()
        report.Oracle |> List.iter (oracleJson >> oracle.Add)
        node["oracle"] <- oracle
        node["baselineAudit"] <- report.BaselineAudit.DeepClone()
        node["projectDirectory"] <- Json.text report.ProjectDirectory
        node

    let private stateDigest (state: JsonObject) =
        let semanticState = state.DeepClone().AsObject()
        semanticState.Remove("generation") |> ignore
        let bytes = Encoding.UTF8.GetBytes(Json.compact semanticState)
        SHA256.HashData(bytes) |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

    let private stateManifest (state: JsonObject) =
        let node = JsonObject()
        node["sha256"] <- Json.text (stateDigest state)
        node["state"] <- state.DeepClone()
        node

    let private stateManifestWithAudit (state: JsonObject) (audit: JsonObject) =
        let node = stateManifest state
        node["baselineAudit"] <- audit.DeepClone()
        node

    let private logFile (writer: StreamWriter) (eventName: string) (fields: (string * JsonNode) list) =
        let item = JsonObject()
        item["at"] <- Json.text (DateTimeOffset.UtcNow.ToString("O"))
        item["event"] <- Json.text eventName
        for key, value in fields do item[key] <- if isNull value then null else value.DeepClone()
        writer.WriteLine(item.ToJsonString(JsonSerializerOptions(WriteIndented = false)))
        writer.Flush()

    let run (config: RunConfig) (provider: IAgentProvider) =
        task {
            if String.IsNullOrWhiteSpace config.Model then invalidArg "config.Model" "A model name is required, including for scripted runs."
            if config.MaxTurns <= 0 then invalidArg "config.MaxTurns" "maxTurns must be positive."
            if config.MaxToolCalls <= 0 then invalidArg "config.MaxToolCalls" "maxToolCalls must be positive."
            if config.MaxOutputTokens <= 0 then invalidArg "config.MaxOutputTokens" "maxOutputTokens must be positive."
            if config.ContextBudgetBytes <= 0 then invalidArg "config.ContextBudgetBytes" "contextBudgetBytes must be positive."
            if Directory.Exists config.RunDirectory then invalidOp $"Run directory already exists: {config.RunDirectory}"
            if config.Mode = Flat then
                let expectedProject = Path.GetFullPath(Path.Combine(config.RunDirectory, "project"))
                if not (String.Equals(Path.GetFullPath(config.ProjectDirectory), expectedProject, StringComparison.OrdinalIgnoreCase)) then
                    invalidArg "config.ProjectDirectory" "Flat mode requires a new project directory at <run-directory>/project."
            Directory.CreateDirectory(config.RunDirectory) |> ignore
            Directory.CreateDirectory(config.ProjectDirectory) |> ignore

            let started = DateTimeOffset.UtcNow
            let timer = Diagnostics.Stopwatch.StartNew()
            let runId = $"{config.Task.Id}-{started:yyyyMMddTHHmmssfffZ}"
            let tracePath = Path.Combine(config.RunDirectory, "trace.jsonl")
            let runtimeLogPath = Path.Combine(config.RunDirectory, "runtime.log")
            let reportPath = Path.Combine(config.RunDirectory, "report.json")
            let initialStatePath = Path.Combine(config.RunDirectory, "initial-state.json")
            let finalStatePath = Path.Combine(config.RunDirectory, "final-state.json")
            let promptPath = Path.Combine(config.RunDirectory, "prompt.txt")
            let fullPrompt =
                if String.IsNullOrWhiteSpace config.Task.InitialContext then config.Task.Goal
                else $"{config.Task.InitialContext.Trim()}\n\nTask:\n{config.Task.Goal}"
            File.WriteAllText(promptPath, $"SYSTEM\n{config.Task.SystemPrompt}\n\nUSER\n{fullPrompt}\n", UTF8Encoding(false))

            use traceWriter = new StreamWriter(tracePath, false, UTF8Encoding(false))
            let log event fields = logFile traceWriter event fields
            let mutable engineInstance: Runtime.Engine option = None
            let mutable failure: (string * string) option = None
            let mutable taskBegan = false
            let mutable initialStateSnapshot: ProjectCheckpoint option = None
            let mutable baselineAudit = JsonObject()
            baselineAudit["profile"] <- Json.text (baselineProfileName config.BaselineProfile)
            baselineAudit["label"] <- Json.text (baselineLabel config.BaselineProfile)
            baselineAudit["status"] <- Json.text "not-audited"
            baselineAudit["guardPurpose"] <- Json.text "classification and inventory audit; not a security boundary"
            let mutable growingLineage: BaselineLineage option = None
            let mutable lineageMarkerPersisted = false
            let mutable freshLineageMarkerCreated = false
            let mutable baselineCheckpointAccepted = false
            let mutable taskStartCanonicalStateHash: string option = None
            let mutable turns = 0
            let mutable calls = 0
            let mutable requestsSent = 0
            let mutable totalRequestBytes = 0L
            let mutable preparedRequestBytes = 0L
            let mutable peakRequestBytes = 0L
            let mutable inputTokens: int option = Some 0
            let mutable outputTokens: int option = Some 0
            let mutable totalTokens: int option = Some 0
            let mutable oracleOutcomes: OracleOutcome list = []
            let mutable runtimeLog: JsonNode = null

            try
                let preSeedState = captureProjectState config.ProjectDirectory |> snapshotOrRaise
                initialStateSnapshot <- Some preSeedState
                taskStartCanonicalStateHash <- Some(canonicalDurableStateHash config.ProjectDirectory)
                let priorLineage = if config.Mode = Growing then readLineage config.ProjectDirectory else None
                growingLineage <- priorLineage
                lineageMarkerPersisted <- priorLineage.IsSome
                let hasPriorTaskHistory = config.Mode = Growing && not (List.isEmpty (taskHistory config.ProjectDirectory))
                writeJson initialStatePath (stateManifestWithAudit (stateNode preSeedState) baselineAudit)
                let engine = Runtime.Engine(config.ProjectDirectory, Set.empty, "2000-01-01T00:00:00Z")
                engineInstance <- Some engine
                let seedBytes = readSeedSourceBytes config.SeedDictionarySource
                let seedHash = seedBytes |> Option.map hashBytes
                let authoritativeProjectIsEmpty =
                    match preSeedState.Storage.Authority with
                    | EmptyAuthority -> true
                    | LegacyAuthority _
                    | ManifestAuthority _ -> false
                let mayApplySeed =
                    authoritativeProjectIsEmpty
                    && (config.Mode = Flat || (priorLineage.IsNone && not hasPriorTaskHistory))
                let mutable seedApplied = false
                if mayApplySeed then
                    match seedBytes with
                    | Some bytes ->
                        let defineArgs = JsonObject()
                        defineArgs["frontend"] <- Json.text (frontendName config.Frontend)
                        defineArgs["source"] <- Json.text (decodeSeedSource bytes)
                        let defined = engine.Dispatch("define", defineArgs)
                        log "seed-define" [ "response", defined ]
                        let defineError = Json.propertyString defined "text" "unknown error"
                        if not (responseOk defined) then failwith $"Could not load seed dictionary: {defineError}"
                        let committed = engine.Dispatch("commit", JsonObject())
                        log "seed-commit" [ "response", committed ]
                        let commitError = Json.propertyString committed "text" "unknown error"
                        if not (responseOk committed) then failwith $"Could not commit seed dictionary: {commitError}"
                        seedApplied <- true
                    | None -> ()

                let loaded =
                    match Storage.load (Storage.create config.ProjectDirectory) with
                    | Ok result -> result
                    | Error error -> raise (ProjectSnapshotFailure error)
                let inventory, parsed = baselineInventory config.ProjectDirectory loaded
                let currentStateHash = canonicalDurableStateHash config.ProjectDirectory
                let hasAuthoredAlgorithms = inventoryHasAuthoredAlgorithms parsed
                let provisionalOriginInventory = priorLineage |> Option.map (fun lineage -> lineage.OriginInventory) |> Option.defaultValue inventory
                let provisionalOriginHash = priorLineage |> Option.map (fun lineage -> lineage.OriginCanonicalStateHash) |> Option.defaultValue currentStateHash
                let provisionalPreviousHash = priorLineage |> Option.map (fun lineage -> lineage.LastCanonicalStateHash)
                baselineAudit <-
                    baselineAuditNode
                        config.BaselineProfile
                        inventory
                        provisionalOriginInventory
                        currentStateHash
                        provisionalOriginHash
                        (if config.Mode = Growing then "pending" else "not-applicable-flat")
                        provisionalPreviousHash
                        (priorLineage |> Option.map (fun lineage -> lineage.SeedSourceHash) |> Option.defaultValue seedHash)
                baselineAudit["guardPurpose"] <- Json.text "classification and inventory audit; not a security boundary"
                baselineAudit["seedSourceProvidedSha256"] <- seedHash |> Option.map Json.text |> nodeOption
                baselineAudit["seedSourceAppliedThisRun"] <- Json.bool seedApplied
                baselineAudit["seedSourceAppliedAtOrigin"] <-
                    Json.bool (priorLineage |> Option.map (fun lineage -> lineage.SeedSourceApplied) |> Option.defaultValue (seedApplied && config.Mode <> Growing))

                if config.Mode = Flat && config.BaselineProfile = BaselineProfile.PrimitiveOnly && hasAuthoredAlgorithms then
                    let violations = inventoryViolations parsed
                    let details = JsonArray()
                    violations |> List.iter (Json.text >> details.Add)
                    baselineAudit["rejectionCode"] <- Json.text "BASELINE_INVENTORY_MISMATCH"
                    baselineAudit["rejectedAuthoredInventory"] <- details
                    let violationText = String.concat ", " violations
                    raise (BaselineGuardFailure("BASELINE_INVENTORY_MISMATCH", $"Primitive-only initialization contains authored algorithms: {violationText}."))

                if config.Mode = Growing then
                    let lineage, status, previousHash =
                        ensureGrowingLineage
                            config.ProjectDirectory
                            config.BaselineProfile
                            inventory
                            currentStateHash
                            seedHash
                            seedApplied
                            hasAuthoredAlgorithms
                    growingLineage <- Some lineage
                    baselineAudit <-
                        baselineAuditNode
                            config.BaselineProfile
                            inventory
                            lineage.OriginInventory
                            currentStateHash
                            lineage.OriginCanonicalStateHash
                            status
                            previousHash
                            lineage.SeedSourceHash
                    baselineAudit["guardPurpose"] <- Json.text "classification and inventory audit; not a security boundary"
                    baselineAudit["seedSourceProvidedSha256"] <- seedHash |> Option.map Json.text |> nodeOption
                    baselineAudit["seedSourceAppliedThisRun"] <- Json.bool seedApplied
                    baselineAudit["seedSourceAppliedAtOrigin"] <- Json.bool (priorLineage |> Option.map (fun prior -> prior.SeedSourceApplied) |> Option.defaultValue false)

                let freshGrowingOrigin = config.Mode = Growing && priorLineage.IsNone
                baselineAudit["status"] <- Json.text (if freshGrowingOrigin then "candidate" else "accepted")
                let initialState = captureProjectState config.ProjectDirectory |> snapshotOrRaise
                if config.Mode = Growing && priorLineage.IsNone then
                    match growingLineage with
                    | Some lineage ->
                        injectTestFailure config HarnessTestFailurePoint.BeforeInitialLineageMarkerWrite
                        writeLineage config.ProjectDirectory lineage
                        lineageMarkerPersisted <- true
                        freshLineageMarkerCreated <- true
                        baselineAudit["status"] <- Json.text "accepted"
                        baselineAudit["seedSourceAppliedAtOrigin"] <- Json.bool lineage.SeedSourceApplied
                    | None -> ()
                writeJson initialStatePath (stateManifestWithAudit (stateNode initialState) baselineAudit)
                initialStateSnapshot <- Some initialState
                taskStartCanonicalStateHash <- Some currentStateHash
                baselineCheckpointAccepted <- true
                let beginArgs = JsonObject()
                beginArgs["goal"] <- Json.text config.Task.Goal
                let begun = engine.Dispatch("task.begin", beginArgs)
                log "task-begin" [ "response", begun ]
                let beginError = Json.propertyString begun "text" "unknown error"
                if not (responseOk begun) then failwith $"Could not begin task: {beginError}"
                taskBegan <- true

                let tools = AgentTools.definitions ()
                let history = inputItems config.Task.InitialContext config.Task.Goal
                let mutable isFinished = false
                while not isFinished && failure.IsNone do
                    turns <- turns + 1
                    let request =
                        { Model = config.Model
                          Instructions = config.Task.SystemPrompt
                          Input = history
                          Tools = tools
                          MaxOutputTokens = config.MaxOutputTokens }
                    let wire = RequestWire.serialize request
                    let bytes = int64 (Encoding.UTF8.GetByteCount wire)
                    preparedRequestBytes <- preparedRequestBytes + bytes
                    peakRequestBytes <- max peakRequestBytes bytes
                    log "provider-request-prepared" [ "turn", Json.integer turns; "bytes", JsonValue.Create(bytes); "body", Json.text wire ]
                    if bytes > int64 config.ContextBudgetBytes then
                        failure <- Some("CONTEXT_BUDGET_EXCEEDED", $"Request is {bytes} UTF-8 bytes, above the {config.ContextBudgetBytes}-byte per-request budget.")
                    elif turns > config.MaxTurns then
                        failure <- Some("TURN_LIMIT", $"The run exceeded its {config.MaxTurns}-turn model-call limit.")
                    else
                        requestsSent <- requestsSent + 1
                        totalRequestBytes <- totalRequestBytes + bytes
                        log "provider-request" [ "turn", Json.integer turns; "bytes", JsonValue.Create(bytes); "body", Json.text wire ]
                        let! response = provider.Complete request
                        log "provider-response" [ "turn", Json.integer turns; "status", Json.text response.Status; "raw", Json.text response.RawResponse ]
                        inputTokens <- addKnown inputTokens (response.Usage |> Option.bind (fun usage -> usage.InputTokens))
                        outputTokens <- addKnown outputTokens (response.Usage |> Option.bind (fun usage -> usage.OutputTokens))
                        totalTokens <- addKnown totalTokens (response.Usage |> Option.bind (fun usage -> usage.TotalTokens))
                        if response.Status <> "completed" then
                            let message = if response.Status = "incomplete" then "Provider response was incomplete (often caused by an output-token limit)." else $"Provider response status was '{response.Status}'."
                            failure <- Some("PROVIDER_NOT_COMPLETED", message)
                        else
                            let returnedCalls = toolCalls response.OutputItems
                            if returnedCalls.IsEmpty then
                                if String.IsNullOrWhiteSpace response.OutputText then
                                    failure <- Some("AGENT_EMPTY_FINAL", "Provider completed without a final text answer or runtime tool call.")
                                else
                                    log "agent-final" [ "text", Json.text response.OutputText ]
                                    isFinished <- true
                            elif turns >= config.MaxTurns then
                                failure <- Some("TURN_LIMIT", $"The agent requested tools on its final allowed turn ({config.MaxTurns}); no calls were executed without room for a follow-up response.")
                            elif calls + returnedCalls.Length > config.MaxToolCalls then
                                failure <- Some("TOOL_LIMIT", $"The agent requested more than the {config.MaxToolCalls}-call tool limit; none of the calls in this response were executed.")
                            else
                                for outputItem in response.OutputItems do history.Add(outputItem.DeepClone())
                                calls <- calls + returnedCalls.Length
                                for call in returnedCalls do
                                    let callId = toolCallId call
                                    if String.IsNullOrWhiteSpace callId then
                                        failure <- Some("MALFORMED_TOOL_CALL", "Function call item is missing call_id; the run cannot be continued safely.")
                                    else
                                        let resultText =
                                            match AgentTools.decode call with
                                            | Error message -> commandError "AGENT_TOOL_ARGUMENTS" message
                                            | Ok command ->
                                                try engine |> fun value -> AgentTools.dispatch value config.Frontend command |> Json.compact
                                                with ex -> commandError "AGENT_TOOL_FAILURE" ex.Message
                                        let resultNode =
                                            try JsonNode.Parse(resultText) with _ -> errorResponse "AGENT_TOOL_FAILURE" resultText
                                        log "runtime-tool" [ "callId", Json.text callId; "call", call; "result", resultNode ]
                                        history.Add(toolOutput callId resultText)

                if failure.IsNone && isFinished then
                    for step in config.Task.Oracle do
                        if failure.IsNone then
                            let outcome = oracleOutcome engine config.Frontend step
                            oracleOutcomes <- oracleOutcomes @ [ outcome ]
                            log "oracle" [ "operation", Json.text outcome.Operation; "passed", Json.bool outcome.Passed; "reason", Json.text outcome.Reason; "response", outcome.Response ]
                            if not outcome.Passed then
                                failure <- Some("ORACLE_FAILED", outcome.Reason)

                    if failure.IsNone then
                        let status = engine.Dispatch("task.status", JsonObject())
                        let taskActive =
                            Json.tryProperty status "data"
                            |> Option.bind (fun node -> Json.tryProperty node "active")
                            |> Option.bind (fun value -> try value.GetValue<bool>() |> Some with _ -> None)
                            |> Option.defaultValue false
                        if taskActive then
                            let committed = engine.Dispatch("task.commit", JsonObject())
                            log "task-commit" [ "response", committed ]
                            if not (responseOk committed) then failure <- Some("TASK_COMMIT_FAILED", Json.propertyString committed "text" "Task commit failed.")
                if failure.IsNone then
                    taskBegan <- false
                    match growingLineage with
                    | Some lineage ->
                        try
                            let finalCanonicalHash = canonicalDurableStateHash config.ProjectDirectory
                            let updated = { lineage with LastCanonicalStateHash = finalCanonicalHash }
                            writeLineage config.ProjectDirectory updated
                            lineageMarkerPersisted <- true
                            growingLineage <- Some updated
                            baselineAudit["finalCanonicalDurableStateSha256"] <- Json.text finalCanonicalHash
                            match Json.tryProperty baselineAudit "lineage" |> Option.bind Json.asObject with
                            | Some lineageAudit ->
                                lineageAudit["finalCanonicalDurableStateSha256"] <- Json.text finalCanonicalHash
                                lineageAudit["continuityVerified"] <- Json.bool true
                            | None -> ()
                            injectTestFailure config HarnessTestFailurePoint.AfterFinalLineageMarkerWrite
                        with
                        | BaselineGuardFailure(code, message) -> failure <- Some(code, message)
                        | ProjectSnapshotFailure error -> failure <- Some(error.Code, storageErrorMessage error)
                    | None ->
                        try
                            let finalCanonicalHash = canonicalDurableStateHash config.ProjectDirectory
                            baselineAudit["finalCanonicalDurableStateSha256"] <- Json.text finalCanonicalHash
                        with ProjectSnapshotFailure error -> failure <- Some(error.Code, storageErrorMessage error)
                let finalStatus = engine.Dispatch("task.log", JsonObject())
                runtimeLog <- finalStatus.DeepClone()
                log "runtime-log" [ "response", finalStatus ]
            with
            | :? ProviderFailure as ex ->
                failure <- Some(ex.Code, ex.Message)
                inputTokens <- None
                outputTokens <- None
                totalTokens <- None
            | ProjectSnapshotFailure error ->
                if failure.IsNone then failure <- Some(error.Code, storageErrorMessage error)
                log "project-snapshot-failure"
                    [ "code", Json.text error.Code
                      "message", Json.text (storageErrorMessage error)
                      "path", error.Path |> Option.map Json.text |> nodeOption ]
            | BaselineGuardFailure(code, message) ->
                if failure.IsNone then failure <- Some(code, message)
                baselineAudit["status"] <- Json.text "rejected"
                baselineAudit["rejectionCode"] <- Json.text code
                baselineAudit["rejectionMessage"] <- Json.text message
                log "baseline-guard-failure" [ "code", Json.text code; "message", Json.text message ]
            | ex ->
                if failure.IsNone then failure <- Some("HARNESS_FAILURE", ex.Message)
                if config.Mode = Growing
                   && growingLineage.IsSome
                   && not baselineCheckpointAccepted
                   && (freshLineageMarkerCreated || not lineageMarkerPersisted) then
                    baselineAudit["status"] <- Json.text "initialization-failed"
                    baselineAudit["seedSourceAppliedAtOrigin"] <- Json.bool false
                if requestsSent > 0 then
                    inputTokens <- None
                    outputTokens <- None
                    totalTokens <- None

            if failure.IsSome then
                if taskBegan then
                    try
                        let aborted = engineInstance.Value.Dispatch("task.abort", JsonObject())
                        log "task-abort" [ "response", aborted ]
                        if not (responseOk aborted) then
                            let message = Json.propertyString aborted "text" "Task abort was rejected."
                            failure <- Some("ROLLBACK_FAILED", $"Task abort was rejected: {message}")
                        runtimeLog <- engineInstance.Value.Dispatch("task.log", JsonObject())
                    with ex ->
                        log "task-abort-failure" [ "message", Json.text ex.Message ]
                        failure <- Some("ROLLBACK_FAILED", $"Task abort failed: {ex.Message}")
                if initialStateSnapshot.IsSome then
                    try
                        match initialStateSnapshot with
                        | Some initial ->
                            let restoreResult = restoreState config.ProjectDirectory initial
                            let restored = captureProjectState config.ProjectDirectory |> snapshotOrRaise
                            if config.Mode = Growing && freshLineageMarkerCreated && not baselineCheckpointAccepted then
                                let markerPath = lineagePath config.ProjectDirectory
                                if File.Exists markerPath then File.Delete markerPath
                                freshLineageMarkerCreated <- false
                                lineageMarkerPersisted <- false
                                baselineAudit["status"] <- Json.text "initialization-failed"
                                baselineAudit["seedSourceAppliedAtOrigin"] <- Json.bool false
                            let initialNode = stateNode initial
                            let restoredNode = stateNode restored
                            let expectedDigest = stateDigest initialNode
                            let actualDigest = stateDigest restoredNode
                            let warning =
                                restoreResult.ExportWarning
                                |> Option.map (fun item -> $"{item.Code}: {storageErrorMessage item}")
                            log "project-storage-restore"
                                [ "capturedGeneration", JsonValue.Create(initial.Storage.Generation)
                                  "restoredGeneration", JsonValue.Create(restoreResult.Generation)
                                  "expectedSemanticDigest", Json.text expectedDigest
                                  "actualSemanticDigest", Json.text actualDigest
                                  "warning", warning |> Option.map Json.text |> nodeOption ]
                            if actualDigest <> expectedDigest then
                                let detail =
                                    warning
                                    |> Option.map (fun value -> $" Restore warning: {value}")
                                    |> Option.defaultValue ""
                                failure <- Some("ROLLBACK_FAILED", $"Project state digest did not match after storage restore.{detail}")
                            elif warning.IsSome then
                                log "project-storage-restore-warning" [ "warning", Json.text warning.Value ]
                            match taskStartCanonicalStateHash with
                            | Some expectedHash ->
                                let restoredHash = canonicalDurableStateHash config.ProjectDirectory
                                baselineAudit["rollbackCanonicalDurableStateSha256"] <- Json.text restoredHash
                                baselineAudit["rollbackContinuityVerified"] <- Json.bool (restoredHash = expectedHash)
                                if restoredHash <> expectedHash && (failure |> Option.exists (fun (code, _) -> code <> "ROLLBACK_FAILED")) then
                                    failure <- Some("ROLLBACK_FAILED", "Canonical durable project state did not match the task-start hash after rollback.")
                                if restoredHash = expectedHash && baselineCheckpointAccepted then
                                    match config.Mode, growingLineage, lineageMarkerPersisted with
                                    | Growing, Some expectedLineage, true ->
                                        let currentMarker = readLineage config.ProjectDirectory
                                        match currentMarker with
                                        | Some current when not (sameLineageOrigin current expectedLineage) ->
                                            raise (BaselineGuardFailure("BASELINE_LINEAGE_INVALID", "The lineage origin changed while reconciling rollback."))
                                        | _ -> ()
                                        let reconciled = { expectedLineage with LastCanonicalStateHash = restoredHash }
                                        writeLineage config.ProjectDirectory reconciled
                                        lineageMarkerPersisted <- true
                                        growingLineage <- Some reconciled
                                        baselineAudit["lineageReconciledAfterRollback"] <- Json.bool true
                                        baselineAudit["finalCanonicalDurableStateSha256"] <- Json.text restoredHash
                                        baselineAudit["rollbackLineageContinuityVerified"] <- Json.bool true
                                        match Json.tryProperty baselineAudit "lineage" |> Option.bind Json.asObject with
                                        | Some lineageAudit ->
                                            lineageAudit["finalCanonicalDurableStateSha256"] <- Json.text restoredHash
                                            lineageAudit["continuityVerified"] <- Json.bool true
                                            lineageAudit["reconciledAfterRollback"] <- Json.bool true
                                        | None -> ()
                                    | Growing, _, true ->
                                        raise (BaselineGuardFailure("BASELINE_LINEAGE_INVALID", "A persisted Growing lineage has no in-memory origin for rollback reconciliation."))
                                    | _ -> ()
                            | None -> ()
                        | None -> ()
                    with
                    | ProjectSnapshotFailure error ->
                        log "project-storage-restore-failure"
                            [ "code", Json.text error.Code
                              "message", Json.text (storageErrorMessage error)
                              "path", error.Path |> Option.map Json.text |> nodeOption ]
                        failure <- Some("ROLLBACK_FAILED", $"{error.Code}: {storageErrorMessage error}")
                    | ex ->
                        log "project-storage-restore-failure"
                            [ "code", Json.text "ROLLBACK_FAILED"
                              "message", Json.text ex.Message ]
                        failure <- Some("ROLLBACK_FAILED", $"Could not restore project state: {ex.Message}")

            if requestsSent = 0 then
                inputTokens <- None
                outputTokens <- None
                totalTokens <- None

            let finalState =
                try
                    match captureProjectState config.ProjectDirectory with
                    | Ok snapshot -> stateManifestWithAudit (stateNode snapshot) baselineAudit
                    | Error error ->
                        if failure.IsNone then failure <- Some("PROJECT_SNAPSHOT_FAILED", $"Could not capture final project state: {error.Code}: {storageErrorMessage error}")
                        let state = JsonObject()
                        let detail = JsonObject()
                        detail["code"] <- Json.text error.Code
                        detail["message"] <- Json.text (storageErrorMessage error)
                        detail["path"] <- error.Path |> Option.map Json.text |> nodeOption
                        state["captureError"] <- detail
                        stateManifestWithAudit state baselineAudit
                with ex ->
                    if failure.IsNone then failure <- Some("PROJECT_SNAPSHOT_FAILED", $"Could not capture final project state: {ex.Message}")
                    let state = JsonObject()
                    let detail = JsonObject()
                    detail["code"] <- Json.text "STATE_CAPTURE_FAILED"
                    detail["message"] <- Json.text ex.Message
                    state["captureError"] <- detail
                    stateManifestWithAudit state baselineAudit
            writeJson finalStatePath finalState
            match initialStateSnapshot with
            | Some initial -> writeJson initialStatePath (stateManifestWithAudit (stateNode initial) baselineAudit)
            | None -> ()
            let savedRuntimeLog: JsonNode = if isNull runtimeLog then JsonObject() :> JsonNode else runtimeLog
            writeJson runtimeLogPath savedRuntimeLog
            timer.Stop()
            let finished = DateTimeOffset.UtcNow
            let success = failure.IsNone && (oracleOutcomes |> List.forall (fun outcome -> outcome.Passed))
            let report =
                { RunId = runId
                  TaskId = config.Task.Id
                  Mode = if config.Mode = Flat then "flat" else "growing"
                  Frontend = frontendName config.Frontend
                  Provider = provider.Name
                  Model = config.Model
                  Success = success
                  Status = if success then "completed" else "failed"
                  FailureCode = failure |> Option.map fst
                  FailureMessage = failure |> Option.map snd
                  StartedAt = started.ToString("O")
                  FinishedAt = finished.ToString("O")
                  DurationMilliseconds = timer.ElapsedMilliseconds
                  Turns = turns
                  ToolCalls = calls
                  RequestCount = requestsSent
                  TotalRequestBytes = totalRequestBytes
                  PreparedRequestBytes = preparedRequestBytes
                  PeakRequestBytes = peakRequestBytes
                  ContextBudgetBytes = config.ContextBudgetBytes
                  EstimatedRequestTokens = (totalRequestBytes + 3L) / 4L
                  EstimateMethod = "Approximate ceil(UTF-8 request-body bytes / 4); not a tokenizer or exact token usage."
                  InputTokens = inputTokens
                  OutputTokens = outputTokens
                  TotalTokens = totalTokens
                  Oracle = oracleOutcomes
                  BaselineAudit = baselineAudit
                  ProjectDirectory = config.ProjectDirectory }
            writeJson reportPath (makeReportNode report)
            return report
        }
