namespace AgentLang

open System
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

/// The format used for a content-addressed object. The kind is checked against
/// the reference site in a manifest; it is never used as a filesystem path.
[<RequireQualifiedAccess>]
type StorageObjectKind =
    | ProjectSource
    | LegacyDictionary
    | WordDefinition
    | TypeDefinition
    | TestDefinition
    | ExampleDefinition
    | VirtualFileState

/// A content-addressed object reference. Hash is a lowercase SHA-256 digest of
/// the exact UTF-8 bytes stored for the object.
type SourceRef =
    { Kind: StorageObjectKind
      Hash: string }

[<RequireQualifiedAccess; StructuralEquality; StructuralComparison>]
type SourceFrontend =
    | Stack
    | Flow

type SourceFormat =
    { Frontend: SourceFrontend
      Version: int }

[<RequireQualifiedAccess; StructuralEquality; StructuralComparison>]
type StoredCallBodyRole =
    | Definition
    | Actual
    | ExpectedExpression

[<RequireQualifiedAccess; StructuralEquality; StructuralComparison>]
type StoredCallForm =
    | Direct
    | AbsoluteRoot
    | DotStage of stage: string
    | StaticCallback of stage: string * qualification: FlowWordReferenceQualification

[<RequireQualifiedAccess; StructuralEquality; StructuralComparison>]
type StoredCallTarget =
    | UserWord of identity: string
    | Primitive of identity: string
    | GeneratedWord of identity: string

/// A storage-neutral resolved call identity bound to one authored source site.
/// Target revisions, source spans, and compiler traversal ordinals are omitted.
type StoredCallBinding =
    { Source: SourceRef
      CaseName: string option
      BodyRole: StoredCallBodyRole
      Path: FlowAstPath
      Form: StoredCallForm
      RequestedName: string
      Target: StoredCallTarget }

type SourceObject =
    { Reference: SourceRef
      Content: string }

type WordRevision =
    { WordId: string
      Name: string
      Revision: int
      Definition: SourceRef
      Tests: SourceRef list
      Examples: SourceRef list
      Maturity: WordMaturity
      Actor: string
      TaskId: string option
      TimestampUtc: DateTimeOffset
      Deprecated: bool
      SourceFormat: SourceFormat
      CallBindings: StoredCallBinding list }

type WordHead =
    { WordId: string
      CurrentName: string
      CurrentRevision: int
      Deprecated: bool }

type TypeSource =
    { Name: string
      Definition: SourceRef }

/// Immutable metadata for one committed project state. Storage deliberately
/// does not parse ProjectSource or validate its language-level meaning.
type ProjectManifest =
    { FormatVersion: int
      ProjectSource: SourceRef
      Types: TypeSource list
      Words: WordHead list
      Revisions: WordRevision list }

type StorageAuthority =
    | EmptyAuthority
    | LegacyAuthority of SourceRef
    | ManifestAuthority of string

/// Captured authoritative state for task rollback. ExportText preserves the
/// exact human-readable export seen when capture ran, including absence.
type StoreSnapshot =
    { Generation: int64
      Authority: StorageAuthority
      ExportText: string option }

/// A named committed snapshot. VirtualFiles and ClockValue are provider state;
/// host capabilities are intentionally outside this DTO.
type NamedSnapshot =
    { ManifestHash: string
      Manifest: ProjectManifest
      VirtualFiles: Map<string, string>
      ClockValue: string option }

type RevisionContent =
    { Revision: WordRevision
      DefinitionSource: string
      TestSources: string list
      ExampleSources: string list }

type StorageError =
    { Code: string
      Message: string
      Path: string option }

type StorageLoadResult =
    { Generation: int64
      Authority: StorageAuthority
      Manifest: ProjectManifest option
      ManifestHash: string option
      ProjectSource: string option
      ExportWarning: StorageError option }

type StorageCommitResult =
    { Generation: int64
      Authority: StorageAuthority
      ManifestHash: string option
      ExportWarning: StorageError option }

[<RequireQualifiedAccess>]
type StorageFailurePoint =
    | BeforePointerReplacement
    | AfterPointerReplacement
    | BeforeExportRefresh
    | AfterExportRefresh

/// One directory-backed storage instance. Tests may supply a deterministic
/// failure injector; production callers should use Storage.create.
[<Sealed>]
type Store internal (root: string, failureInjector: StorageFailurePoint -> unit) =
    member internal _.Root = root
    member internal _.Inject(point) = failureInjector point

[<RequireQualifiedAccess>]
module StorageLimits =
    /// Maximum exact UTF-8 source/provider object size.
    let MaxObjectBytes = 8 * 1024 * 1024
    /// Maximum encoded manifest or named snapshot size.
    let MaxMetadataBytes = 8 * 1024 * 1024
    /// Maximum number of source references in one manifest.
    let MaxReferences = 20_000
    /// Maximum number of virtual files in a named snapshot.
    let MaxVirtualFiles = 10_000
    /// Maximum lower-case ASCII snapshot-name length.
    let MaxSnapshotNameLength = 64
    /// Maximum total number of authored call bindings in one manifest.
    let MaxCallBindings = 20_000
    /// Maximum structural path depth for one call binding.
    let MaxCallBindingPathDepth = 128
    /// Maximum index value in an indexed structural path segment.
    let MaxCallBindingPathIndex = 100_000
    /// Maximum aggregate structural path segments in one manifest.
    let MaxCallBindingPathSegments = 100_000

module Storage =
    // CURRENT and named snapshots remain version 1 independently from manifests.
    let private pointerSnapshotFormatVersion = 1
    let private minimumManifestFormatVersion = 1
    let private maximumManifestFormatVersion = 2
    let private storeDirectoryName = ".agentlang"
    let private storeDirectory = "store"
    let private objectDirectory = "objects"
    let private manifestDirectory = "manifests"
    let private snapshotDirectory = "snapshots"
    let private currentFile = "CURRENT"
    let private lockFile = "WRITE.lock"
    let private exportFile = "dictionary.agent"
    let private taskHistoryDirectory = "history"

    type private Pointer =
        { Generation: int64
          Authority: StorageAuthority }

    type private SnapshotFile =
        { ManifestHash: string
          VirtualFiles: SourceRef option
          ClockValue: string option }

    exception private StorageFailure of StorageError

    let private failure code message path =
        raise (StorageFailure { Code = code; Message = message; Path = path })

    let private throwError error = raise (StorageFailure error)

    let private catchStorage action =
        try Ok(action ())
        with
        | StorageFailure error -> Error error
        | :? UnauthorizedAccessException as ex -> Error { Code = "STORAGE_ACCESS_DENIED"; Message = ex.Message; Path = None }
        | :? IOException as ex -> Error { Code = "STORAGE_IO"; Message = ex.Message; Path = None }
        | :? JsonException as ex -> Error { Code = "STORAGE_INVALID_JSON"; Message = ex.Message; Path = None }
        | ex -> Error { Code = "STORAGE_FAILURE"; Message = ex.Message; Path = None }

    let private utf8 = UTF8Encoding(false, true)

    let private sha256 (content: string) =
        content
        |> utf8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    let sourceObject kind content =
        { Reference = { Kind = kind; Hash = sha256 content }
          Content = content }

    let create root =
        if String.IsNullOrWhiteSpace root then invalidArg (nameof root) "A project directory is required."
        Store(Path.GetFullPath root, ignore)

    let createWithFailureInjector root injector =
        if String.IsNullOrWhiteSpace root then invalidArg (nameof root) "A project directory is required."
        if isNull (box injector) then invalidArg (nameof injector) "A failure injector is required."
        Store(Path.GetFullPath root, injector)

    let private jsonString (value: string) : JsonNode = JsonValue.Create(value) :> JsonNode
    let private jsonInt (value: int) : JsonNode = JsonValue.Create(value) :> JsonNode
    let private jsonInt64 (value: int64) : JsonNode = JsonValue.Create(value) :> JsonNode
    let private jsonBool (value: bool) : JsonNode = JsonValue.Create(value) :> JsonNode

    let private requireObject description (node: JsonNode) =
        match node with
        | :? JsonObject as value -> value
        | _ -> failure "STORAGE_INVALID_JSON" $"{description} must be a JSON object." None

    let private requireArray description (node: JsonNode) =
        match node with
        | :? JsonArray as value -> value
        | _ -> failure "STORAGE_INVALID_JSON" $"{description} must be a JSON array." None

    let private requireString description (node: JsonNode) =
        try
            if isNull node then failure "STORAGE_INVALID_JSON" $"{description} is required." None
            node.GetValue<string>()
        with
        | StorageFailure _ as ex -> raise ex
        | _ -> failure "STORAGE_INVALID_JSON" $"{description} must be a string." None

    let private requireInt description (node: JsonNode) =
        try
            if isNull node then failure "STORAGE_INVALID_JSON" $"{description} is required." None
            node.GetValue<int>()
        with
        | StorageFailure _ as ex -> raise ex
        | _ -> failure "STORAGE_INVALID_JSON" $"{description} must be an integer." None

    let private requireInt64 description (node: JsonNode) =
        try
            if isNull node then failure "STORAGE_INVALID_JSON" $"{description} is required." None
            node.GetValue<int64>()
        with
        | StorageFailure _ as ex -> raise ex
        | _ -> failure "STORAGE_INVALID_JSON" $"{description} must be an integer." None

    let private requireBool description (node: JsonNode) =
        try
            if isNull node then failure "STORAGE_INVALID_JSON" $"{description} is required." None
            node.GetValue<bool>()
        with
        | StorageFailure _ as ex -> raise ex
        | _ -> failure "STORAGE_INVALID_JSON" $"{description} must be a boolean." None

    let private optionalString description (node: JsonNode) =
        if isNull node then None else Some(requireString description node)

    let private kindName = function
        | StorageObjectKind.ProjectSource -> "project-source"
        | StorageObjectKind.LegacyDictionary -> "legacy-dictionary"
        | StorageObjectKind.WordDefinition -> "word-definition"
        | StorageObjectKind.TypeDefinition -> "type-definition"
        | StorageObjectKind.TestDefinition -> "test-definition"
        | StorageObjectKind.ExampleDefinition -> "example-definition"
        | StorageObjectKind.VirtualFileState -> "virtual-file-state"

    let private parseKind = function
        | "project-source" -> StorageObjectKind.ProjectSource
        | "legacy-dictionary" -> StorageObjectKind.LegacyDictionary
        | "word-definition" -> StorageObjectKind.WordDefinition
        | "type-definition" -> StorageObjectKind.TypeDefinition
        | "test-definition" -> StorageObjectKind.TestDefinition
        | "example-definition" -> StorageObjectKind.ExampleDefinition
        | "virtual-file-state" -> StorageObjectKind.VirtualFileState
        | value -> failure "STORAGE_INVALID_REFERENCE" $"Unknown source object kind '{value}'." None

    let private extension = function
        | StorageObjectKind.VirtualFileState -> ".json"
        | _ -> ".agent"

    let private isHexHash (value: string) =
        value.Length = 64
        && value |> Seq.forall (fun ch -> (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'))

    let private validateHash path hash =
        if isNull hash || not (isHexHash hash) then
            failure "STORAGE_INVALID_HASH" "A source reference must contain a lowercase 64-character SHA-256 hash." path

    let private sourceRefNode (reference: SourceRef) =
        let node = JsonObject()
        node["kind"] <- jsonString (kindName reference.Kind)
        node["hash"] <- jsonString reference.Hash
        node :> JsonNode

    let private parseSourceRef path node =
        let value = requireObject "source reference" node
        let reference =
            { Kind = parseKind (requireString "source reference kind" value["kind"])
              Hash = requireString "source reference hash" value["hash"] }
        validateHash path reference.Hash
        reference

    let private maturityName = function ProjectWord -> "project" | LibraryWord -> "library"

    let private parseMaturity = function
        | "project" -> ProjectWord
        | "library" -> LibraryWord
        | value -> failure "STORAGE_INVALID_MANIFEST" $"Unknown word maturity '{value}'." None

    let private defaultSourceFormat =
        { Frontend = SourceFrontend.Stack
          Version = 1 }

    let private sourceFrontendName = function
        | SourceFrontend.Stack -> "stack"
        | SourceFrontend.Flow -> "flow"

    let private parseSourceFrontend path = function
        | "stack" -> SourceFrontend.Stack
        | "flow" -> SourceFrontend.Flow
        | value -> failure "STORAGE_UNSUPPORTED_FRONTEND" $"Source frontend '{value}' is not supported." path

    let private sourceFormatNode (sourceFormat: SourceFormat) =
        let node = JsonObject()
        node["frontend"] <- jsonString (sourceFrontendName sourceFormat.Frontend)
        node["version"] <- jsonInt sourceFormat.Version
        node :> JsonNode

    let private parseSourceFormat path node =
        let value = requireObject "word revision source format" node
        let frontend = requireString "word revision source frontend" value["frontend"] |> parseSourceFrontend path
        let version = requireInt "word revision source format version" value["version"]
        if version <> 1 then
            failure "STORAGE_UNSUPPORTED_VERSION" $"Source syntax version {version} is not supported." path
        { Frontend = frontend
          Version = version }

    let private storedCallBodyRoleName = function
        | StoredCallBodyRole.Definition -> "definition"
        | StoredCallBodyRole.Actual -> "actual"
        | StoredCallBodyRole.ExpectedExpression -> "expectedExpression"

    let private parseStoredCallBodyRole path = function
        | "definition" -> StoredCallBodyRole.Definition
        | "actual" -> StoredCallBodyRole.Actual
        | "expectedExpression" -> StoredCallBodyRole.ExpectedExpression
        | value -> failure "STORAGE_INVALID_MANIFEST" $"Unknown call binding body role '{value}'." path

    let private callBindingPathSegmentNode segment =
        let node = JsonObject()
        let setIndexed name index =
            node["segment"] <- jsonString name
            node["index"] <- jsonInt index
        let setNamed name = node["segment"] <- jsonString name
        match segment with
        | FlowAstPathSegment.BlockStatement index -> setIndexed "blockStatement" index
        | FlowAstPathSegment.LetInitializer -> setNamed "letInitializer"
        | FlowAstPathSegment.DestructureInitializer -> setNamed "destructureInitializer"
        | FlowAstPathSegment.EvaluateExpression -> setNamed "evaluateExpression"
        | FlowAstPathSegment.ReturnOutput index -> setIndexed "returnOutput" index
        | FlowAstPathSegment.CallArgument index -> setIndexed "callArgument" index
        | FlowAstPathSegment.RootCallArgument index -> setIndexed "rootCallArgument" index
        | FlowAstPathSegment.DotReceiver -> setNamed "dotReceiver"
        | FlowAstPathSegment.DotArgument index -> setIndexed "dotArgument" index
        | FlowAstPathSegment.IfCondition -> setNamed "ifCondition"
        | FlowAstPathSegment.IfThenStatement index -> setIndexed "ifThenStatement" index
        | FlowAstPathSegment.IfElseStatement index -> setIndexed "ifElseStatement" index
        | FlowAstPathSegment.ContainerPayload -> setNamed "containerPayload"
        | FlowAstPathSegment.OptionScrutinee -> setNamed "optionScrutinee"
        | FlowAstPathSegment.OptionSomeStatement index -> setIndexed "optionSomeStatement" index
        | FlowAstPathSegment.OptionNoneStatement index -> setIndexed "optionNoneStatement" index
        | FlowAstPathSegment.ResultScrutinee -> setNamed "resultScrutinee"
        | FlowAstPathSegment.ResultOkStatement index -> setIndexed "resultOkStatement" index
        | FlowAstPathSegment.ResultErrorStatement index -> setIndexed "resultErrorStatement" index
        node :> JsonNode

    let private parseCallBindingPathSegment path node =
        let value = requireObject "call binding path segment" node
        let segment = requireString "call binding path segment kind" value["segment"]
        let indexed constructor =
            let index = requireInt "call binding path index" value["index"]
            if index < 0 then failure "STORAGE_INVALID_MANIFEST" "Call binding path indexes cannot be negative." path
            if index > StorageLimits.MaxCallBindingPathIndex then
                failure "STORAGE_LIMIT_EXCEEDED" $"Call binding path indexes cannot exceed {StorageLimits.MaxCallBindingPathIndex}." path
            constructor index
        match segment with
        | "blockStatement" -> indexed FlowAstPathSegment.BlockStatement
        | "letInitializer" -> FlowAstPathSegment.LetInitializer
        | "destructureInitializer" -> FlowAstPathSegment.DestructureInitializer
        | "evaluateExpression" -> FlowAstPathSegment.EvaluateExpression
        | "returnOutput" -> indexed FlowAstPathSegment.ReturnOutput
        | "callArgument" -> indexed FlowAstPathSegment.CallArgument
        | "rootCallArgument" -> indexed FlowAstPathSegment.RootCallArgument
        | "dotReceiver" -> FlowAstPathSegment.DotReceiver
        | "dotArgument" -> indexed FlowAstPathSegment.DotArgument
        | "ifCondition" -> FlowAstPathSegment.IfCondition
        | "ifThenStatement" -> indexed FlowAstPathSegment.IfThenStatement
        | "ifElseStatement" -> indexed FlowAstPathSegment.IfElseStatement
        | "containerPayload" -> FlowAstPathSegment.ContainerPayload
        | "optionScrutinee" -> FlowAstPathSegment.OptionScrutinee
        | "optionSomeStatement" -> indexed FlowAstPathSegment.OptionSomeStatement
        | "optionNoneStatement" -> indexed FlowAstPathSegment.OptionNoneStatement
        | "resultScrutinee" -> FlowAstPathSegment.ResultScrutinee
        | "resultOkStatement" -> indexed FlowAstPathSegment.ResultOkStatement
        | "resultErrorStatement" -> indexed FlowAstPathSegment.ResultErrorStatement
        | value -> failure "STORAGE_INVALID_MANIFEST" $"Unknown call binding path segment '{value}'." path

    let private storedCallFormNode = function
        | StoredCallForm.Direct ->
            let node = JsonObject()
            node["kind"] <- jsonString "direct"
            node :> JsonNode
        | StoredCallForm.AbsoluteRoot ->
            let node = JsonObject()
            node["kind"] <- jsonString "absoluteRoot"
            node :> JsonNode
        | StoredCallForm.DotStage stage ->
            let node = JsonObject()
            node["kind"] <- jsonString "dotStage"
            node["stage"] <- jsonString stage
            node :> JsonNode
        | StoredCallForm.StaticCallback(stage, qualification) ->
            let node = JsonObject()
            node["kind"] <- jsonString "staticCallback"
            node["stage"] <- jsonString stage
            node["qualification"] <-
                jsonString (
                    match qualification with
                    | FlowWordReferenceQualification.ExplicitShort -> "explicitShort"
                    | FlowWordReferenceQualification.NamespaceQualified -> "namespaceQualified"
                    | FlowWordReferenceQualification.AbsoluteRoot -> "absoluteRoot")
            node :> JsonNode

    let private parseStoredCallForm path node =
        let value = requireObject "call binding form" node
        match requireString "call binding form kind" value["kind"] with
        | "direct" -> StoredCallForm.Direct
        | "absoluteRoot" -> StoredCallForm.AbsoluteRoot
        | "dotStage" -> StoredCallForm.DotStage(requireString "call binding dot stage" value["stage"])
        | "staticCallback" ->
            let qualification =
                match requireString "call binding callback qualification" value["qualification"] with
                | "explicitShort" -> FlowWordReferenceQualification.ExplicitShort
                | "namespaceQualified" -> FlowWordReferenceQualification.NamespaceQualified
                | "absoluteRoot" -> FlowWordReferenceQualification.AbsoluteRoot
                | item -> failure "STORAGE_INVALID_MANIFEST" $"Unknown callback qualification '{item}'." path
            StoredCallForm.StaticCallback(requireString "call binding callback stage" value["stage"], qualification)
        | item -> failure "STORAGE_INVALID_MANIFEST" $"Unknown call binding form '{item}'." path

    let private storedCallTargetNode = function
        | (StoredCallTarget.UserWord identity
          | StoredCallTarget.Primitive identity
          | StoredCallTarget.GeneratedWord identity) as target ->
            let node = JsonObject()
            node["kind"] <-
                jsonString (
                    match target with
                    | StoredCallTarget.UserWord _ -> "userWord"
                    | StoredCallTarget.Primitive _ -> "primitive"
                    | StoredCallTarget.GeneratedWord _ -> "generatedWord")
            node["identity"] <- jsonString identity
            node :> JsonNode

    let private parseStoredCallTarget path node =
        let value = requireObject "call binding target" node
        let identity = requireString "call binding target identity" value["identity"]
        match requireString "call binding target kind" value["kind"] with
        | "userWord" -> StoredCallTarget.UserWord identity
        | "primitive" -> StoredCallTarget.Primitive identity
        | "generatedWord" -> StoredCallTarget.GeneratedWord identity
        | item -> failure "STORAGE_INVALID_MANIFEST" $"Unknown call binding target kind '{item}'." path

    let private storedCallBindingNode (binding: StoredCallBinding) =
        let node = JsonObject()
        node["source"] <- sourceRefNode binding.Source
        node["caseName"] <- binding.CaseName |> Option.map jsonString |> Option.defaultValue null
        node["bodyRole"] <- jsonString (storedCallBodyRoleName binding.BodyRole)
        let path = JsonArray()
        let (FlowAstPath.FlowAstPath segments) = binding.Path
        segments |> List.iter (callBindingPathSegmentNode >> path.Add)
        node["path"] <- path
        node["form"] <- storedCallFormNode binding.Form
        node["requestedName"] <- jsonString binding.RequestedName
        node["target"] <- storedCallTargetNode binding.Target
        node :> JsonNode

    let private storedCallBindingSortKey (binding: StoredCallBinding) =
        let (FlowAstPath.FlowAstPath segments) = binding.Path
        kindName binding.Source.Kind,
        binding.Source.Hash,
        Option.defaultValue "" binding.CaseName,
        storedCallBodyRoleName binding.BodyRole,
        segments

    let private parseStoredCallBinding path node =
        let value = requireObject "call binding" node
        if not (value.ContainsKey "caseName") then
            failure "STORAGE_INVALID_JSON" "Call binding caseName is required (use null for the definition body)." path
        let caseName = optionalString "call binding case name" value["caseName"]
        let pathNode = requireArray "call binding path" value["path"]
        if pathNode.Count > StorageLimits.MaxCallBindingPathDepth then
            failure "STORAGE_LIMIT_EXCEEDED" $"Call binding path depth exceeds {StorageLimits.MaxCallBindingPathDepth}." path
        let pathSegments = pathNode |> Seq.map (parseCallBindingPathSegment path) |> Seq.toList
        { Source = parseSourceRef path value["source"]
          CaseName = caseName
          BodyRole = requireString "call binding body role" value["bodyRole"] |> parseStoredCallBodyRole path
          Path = FlowAstPath.FlowAstPath pathSegments
          Form = parseStoredCallForm path value["form"]
          RequestedName = requireString "call binding requested name" value["requestedName"]
          Target = parseStoredCallTarget path value["target"] }

    let private indexedCallBindingPathValue = function
        | FlowAstPathSegment.BlockStatement index
        | FlowAstPathSegment.ReturnOutput index
        | FlowAstPathSegment.CallArgument index
        | FlowAstPathSegment.RootCallArgument index
        | FlowAstPathSegment.DotArgument index
        | FlowAstPathSegment.IfThenStatement index
        | FlowAstPathSegment.IfElseStatement index
        | FlowAstPathSegment.OptionSomeStatement index
        | FlowAstPathSegment.OptionNoneStatement index
        | FlowAstPathSegment.ResultOkStatement index
        | FlowAstPathSegment.ResultErrorStatement index -> Some index
        | FlowAstPathSegment.LetInitializer
        | FlowAstPathSegment.DestructureInitializer
        | FlowAstPathSegment.EvaluateExpression
        | FlowAstPathSegment.DotReceiver
        | FlowAstPathSegment.IfCondition
        | FlowAstPathSegment.ContainerPayload
        | FlowAstPathSegment.OptionScrutinee
        | FlowAstPathSegment.ResultScrutinee -> None

    let private validateCallBindingBounds path (revisions: WordRevision list) =
        let mutable bindingCount = 0
        let mutable pathSegmentCount = 0
        for revision in revisions do
            if revision.CallBindings.Length > StorageLimits.MaxCallBindings - bindingCount then
                failure "STORAGE_LIMIT_EXCEEDED" $"A manifest may contain at most {StorageLimits.MaxCallBindings} call bindings." path
            bindingCount <- bindingCount + revision.CallBindings.Length
            for binding in revision.CallBindings do
                let (FlowAstPath.FlowAstPath segments) = binding.Path
                if segments.Length > StorageLimits.MaxCallBindingPathDepth then
                    failure "STORAGE_LIMIT_EXCEEDED" $"Call binding path depth exceeds {StorageLimits.MaxCallBindingPathDepth}." path
                if segments.Length > StorageLimits.MaxCallBindingPathSegments - pathSegmentCount then
                    failure "STORAGE_LIMIT_EXCEEDED" $"A manifest may contain at most {StorageLimits.MaxCallBindingPathSegments} aggregate call binding path segments." path
                pathSegmentCount <- pathSegmentCount + segments.Length
                for segment in segments do
                    match indexedCallBindingPathValue segment with
                    | Some index when index < 0 ->
                        failure "STORAGE_INVALID_MANIFEST" "Call binding path indexes cannot be negative." path
                    | Some index when index > StorageLimits.MaxCallBindingPathIndex ->
                        failure "STORAGE_LIMIT_EXCEEDED" $"Call binding path indexes cannot exceed {StorageLimits.MaxCallBindingPathIndex}." path
                    | Some _ | None -> ()

    let private wordRevisionNode manifestVersion (revision: WordRevision) =
        let isDefaultSource = revision.SourceFormat = defaultSourceFormat && List.isEmpty revision.CallBindings
        if manifestVersion = 1 && not isDefaultSource then
            failure "STORAGE_INVALID_MANIFEST" "A version-1 manifest can only serialize Stack version 1 revisions with no call bindings." None
        if manifestVersion <> 1 && manifestVersion <> 2 then
            failure "STORAGE_UNSUPPORTED_VERSION" $"Manifest format {manifestVersion} is not supported." None
        let node = JsonObject()
        node["wordId"] <- jsonString revision.WordId
        node["name"] <- jsonString revision.Name
        node["revision"] <- jsonInt revision.Revision
        node["definition"] <- sourceRefNode revision.Definition
        let tests = JsonArray()
        revision.Tests |> List.sortBy (fun item -> item.Hash, kindName item.Kind) |> List.iter (sourceRefNode >> tests.Add)
        node["tests"] <- tests
        let examples = JsonArray()
        revision.Examples |> List.sortBy (fun item -> item.Hash, kindName item.Kind) |> List.iter (sourceRefNode >> examples.Add)
        node["examples"] <- examples
        node["maturity"] <- jsonString (maturityName revision.Maturity)
        node["actor"] <- jsonString revision.Actor
        node["taskId"] <- revision.TaskId |> Option.map jsonString |> Option.defaultValue null
        node["timestampUtc"] <- jsonString (revision.TimestampUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
        node["deprecated"] <- jsonBool revision.Deprecated
        if manifestVersion = 2 then
            node["sourceFormat"] <- sourceFormatNode revision.SourceFormat
            let bindings = JsonArray()
            revision.CallBindings |> List.sortBy storedCallBindingSortKey |> List.iter (storedCallBindingNode >> bindings.Add)
            node["callBindings"] <- bindings
        node :> JsonNode

    let private wordHeadNode (head: WordHead) =
        let node = JsonObject()
        node["wordId"] <- jsonString head.WordId
        node["currentName"] <- jsonString head.CurrentName
        node["currentRevision"] <- jsonInt head.CurrentRevision
        node["deprecated"] <- jsonBool head.Deprecated
        node :> JsonNode

    let private typeSourceNode (item: TypeSource) =
        let node = JsonObject()
        node["name"] <- jsonString item.Name
        node["definition"] <- sourceRefNode item.Definition
        node :> JsonNode

    let private manifestNode (manifest: ProjectManifest) =
        if manifest.FormatVersion < minimumManifestFormatVersion || manifest.FormatVersion > maximumManifestFormatVersion then
            failure "STORAGE_UNSUPPORTED_VERSION" $"Manifest format {manifest.FormatVersion} is not supported." None
        validateCallBindingBounds None manifest.Revisions
        let node = JsonObject()
        node["formatVersion"] <- jsonInt manifest.FormatVersion
        node["projectSource"] <- sourceRefNode manifest.ProjectSource
        let types = JsonArray()
        manifest.Types |> List.sortBy (fun item -> item.Name) |> List.iter (typeSourceNode >> types.Add)
        node["types"] <- types
        let words = JsonArray()
        manifest.Words |> List.sortBy (fun item -> item.WordId) |> List.iter (wordHeadNode >> words.Add)
        node["words"] <- words
        let revisions = JsonArray()
        manifest.Revisions
        |> List.sortBy (fun item -> item.WordId, item.Revision)
        |> List.iter (wordRevisionNode manifest.FormatVersion >> revisions.Add)
        node["revisions"] <- revisions
        node

    let private parseArray description parser (node: JsonNode) =
        requireArray description node
        |> Seq.map parser
        |> Seq.toList

    let private parseWordRevision path manifestVersion node =
        let value = requireObject "word revision" node
        let tests = parseArray "word revision tests" (parseSourceRef path) value["tests"]
        let examples = parseArray "word revision examples" (parseSourceRef path) value["examples"]
        let sourceFormat, callBindings =
            match manifestVersion with
            | 1 ->
                let sourceFormat =
                    if value.ContainsKey "sourceFormat" then parseSourceFormat path value["sourceFormat"]
                    else defaultSourceFormat
                let callBindings =
                    if value.ContainsKey "callBindings" then parseArray "word revision call bindings" (parseStoredCallBinding path) value["callBindings"]
                    else []
                sourceFormat, callBindings
            | 2 ->
                if not (value.ContainsKey "sourceFormat") then
                    failure "STORAGE_INVALID_JSON" "Version-2 word revisions require sourceFormat." path
                if not (value.ContainsKey "callBindings") then
                    failure "STORAGE_INVALID_JSON" "Version-2 word revisions require callBindings." path
                parseSourceFormat path value["sourceFormat"],
                parseArray "word revision call bindings" (parseStoredCallBinding path) value["callBindings"]
            | version ->
                failure "STORAGE_UNSUPPORTED_VERSION" $"Manifest format {version} is not supported." path
        let timestampText = requireString "word revision timestamp" value["timestampUtc"]
        let timestamp =
            match DateTimeOffset.TryParse(timestampText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
            | true, result -> result
            | _ -> failure "STORAGE_INVALID_MANIFEST" "Word revision timestamp must be a round-trip date-time." path
        { WordId = requireString "word revision ID" value["wordId"]
          Name = requireString "word revision name" value["name"]
          Revision = requireInt "word revision number" value["revision"]
          Definition = parseSourceRef path value["definition"]
          Tests = tests
          Examples = examples
          Maturity = parseMaturity (requireString "word maturity" value["maturity"])
          Actor = requireString "word revision actor" value["actor"]
          TaskId = optionalString "word revision task ID" value["taskId"]
          TimestampUtc = timestamp
          Deprecated = requireBool "word revision deprecated flag" value["deprecated"]
          SourceFormat = sourceFormat
          CallBindings = callBindings }

    let private parseWordHead node =
        let value = requireObject "word head" node
        { WordId = requireString "word head ID" value["wordId"]
          CurrentName = requireString "word head name" value["currentName"]
          CurrentRevision = requireInt "word head revision" value["currentRevision"]
          Deprecated = requireBool "word head deprecated flag" value["deprecated"] }

    let private parseTypeSource path node =
        let value = requireObject "type source" node
        { Name = requireString "type source name" value["name"]
          Definition = parseSourceRef path value["definition"] }

    let private preflightCallBindingWireBounds path (revisionNodes: JsonArray) =
        let mutable bindingCount = 0
        let mutable pathSegmentCount = 0
        for revisionNode in revisionNodes do
            let revision = requireObject "word revision" revisionNode
            if revision.ContainsKey "callBindings" then
                let bindings = requireArray "word revision call bindings" revision["callBindings"]
                if bindings.Count > StorageLimits.MaxCallBindings - bindingCount then
                    failure "STORAGE_LIMIT_EXCEEDED" $"A manifest may contain at most {StorageLimits.MaxCallBindings} call bindings." path
                bindingCount <- bindingCount + bindings.Count
                for bindingNode in bindings do
                    let binding = requireObject "call binding" bindingNode
                    let callPath = requireArray "call binding path" binding["path"]
                    if callPath.Count > StorageLimits.MaxCallBindingPathDepth then
                        failure "STORAGE_LIMIT_EXCEEDED" $"Call binding path depth exceeds {StorageLimits.MaxCallBindingPathDepth}." path
                    if callPath.Count > StorageLimits.MaxCallBindingPathSegments - pathSegmentCount then
                        failure "STORAGE_LIMIT_EXCEEDED" $"A manifest may contain at most {StorageLimits.MaxCallBindingPathSegments} aggregate call binding path segments." path
                    pathSegmentCount <- pathSegmentCount + callPath.Count

    let private parseManifest path node =
        let value = requireObject "manifest" node
        // Reject an unsupported manifest before parsing project references or
        // version-specific revision fields so errors are stable and structured.
        let manifestVersion = requireInt "manifest format version" value["formatVersion"]
        if manifestVersion < minimumManifestFormatVersion || manifestVersion > maximumManifestFormatVersion then
            failure "STORAGE_UNSUPPORTED_VERSION" $"Manifest format {manifestVersion} is not supported (expected 1 or 2)." path
        let revisionNodes = requireArray "manifest revisions" value["revisions"]
        preflightCallBindingWireBounds path revisionNodes
        { FormatVersion = manifestVersion
          ProjectSource = parseSourceRef path value["projectSource"]
          Types = parseArray "manifest types" (parseTypeSource path) value["types"]
          Words = parseArray "manifest words" parseWordHead value["words"]
          Revisions = parseArray "manifest revisions" (parseWordRevision path manifestVersion) (revisionNodes :> JsonNode) }

    let private sourceRefs (manifest: ProjectManifest) =
        seq {
            yield manifest.ProjectSource
            for item in manifest.Types do yield item.Definition
            for revision in manifest.Revisions do
                yield revision.Definition
                yield! revision.Tests
                yield! revision.Examples
        }
        |> Seq.toList

    let private allSourceRefs (manifest: ProjectManifest) =
        let refs = sourceRefs manifest
        if refs.Length > StorageLimits.MaxReferences then
            failure "STORAGE_LIMIT_EXCEEDED" $"A manifest may reference at most {StorageLimits.MaxReferences} source objects." None
        for reference in refs do
            validateHash None reference.Hash
        refs

    let private validMetadataText description maximum path (value: string) =
        if String.IsNullOrWhiteSpace value || value.Length > maximum then
            failure "STORAGE_INVALID_MANIFEST" $"{description} must be nonempty and at most {maximum} characters." path

    let private validateManifest (manifest: ProjectManifest) path =
        if manifest.FormatVersion < minimumManifestFormatVersion || manifest.FormatVersion > maximumManifestFormatVersion then
            failure "STORAGE_UNSUPPORTED_VERSION" $"Manifest format {manifest.FormatVersion} is not supported (expected 1 or 2)." path
        validateCallBindingBounds path manifest.Revisions
        if manifest.ProjectSource.Kind <> StorageObjectKind.ProjectSource then
            failure "STORAGE_INVALID_MANIFEST" "Manifest projectSource must reference a project-source object." path

        let uniqueBy description key (values: 'T list) =
            let duplicate = values |> List.groupBy key |> List.tryFind (fun (_, entries) -> entries.Length > 1)
            match duplicate with
            | Some(value, _) -> failure "STORAGE_DUPLICATE_IDENTITY" $"Manifest has duplicate {description} '{value}'." path
            | None -> ()

        uniqueBy "type name" (fun item -> item.Name) manifest.Types
        for item in manifest.Types do
            validMetadataText "Type name" 256 path item.Name
            if item.Definition.Kind <> StorageObjectKind.TypeDefinition then
                failure "STORAGE_INVALID_MANIFEST" $"Type '{item.Name}' must reference a type-definition object." path

        uniqueBy "word ID" (fun item -> item.WordId) manifest.Words
        uniqueBy "current word name" (fun item -> item.CurrentName) manifest.Words
        uniqueBy "revision identity" (fun (item: WordRevision) -> item.WordId, item.Revision) manifest.Revisions

        let revisionsByIdentity = manifest.Revisions |> List.map (fun item -> (item.WordId, item.Revision), item) |> Map.ofList
        for head in manifest.Words do
            validMetadataText "Word ID" 128 path head.WordId
            validMetadataText "Current word name" 256 path head.CurrentName
            if head.CurrentRevision < 1 then failure "STORAGE_INVALID_MANIFEST" "Word head revision must be positive." path
            match revisionsByIdentity.TryFind(head.WordId, head.CurrentRevision) with
            | None -> failure "STORAGE_INVALID_MANIFEST" $"Word head '{head.WordId}' references a missing current revision." path
            | Some revision when revision.Name <> head.CurrentName || revision.Deprecated <> head.Deprecated ->
                failure "STORAGE_INVALID_MANIFEST" $"Word head '{head.CurrentName}' does not match its current revision metadata." path
            | Some _ -> ()

        for revision in manifest.Revisions do
            validMetadataText "Revision word ID" 128 path revision.WordId
            validMetadataText "Revision word name" 256 path revision.Name
            validMetadataText "Revision actor" 128 path revision.Actor
            if revision.Revision < 1 then failure "STORAGE_INVALID_MANIFEST" "Word revision number must be positive." path
            if revision.SourceFormat.Version <> 1 then
                failure "STORAGE_UNSUPPORTED_VERSION" $"Source syntax version {revision.SourceFormat.Version} is not supported." path
            if manifest.FormatVersion = 1 && revision.SourceFormat <> defaultSourceFormat then
                failure "STORAGE_INVALID_MANIFEST" "Version-1 manifests only support Stack version 1 source metadata." path
            match manifest.FormatVersion, revision.SourceFormat.Frontend with
            | 1, SourceFrontend.Flow ->
                failure "STORAGE_INVALID_MANIFEST" "Version-1 manifests cannot declare Flow-authored revisions." path
            | _, SourceFrontend.Stack when not (List.isEmpty revision.CallBindings) ->
                failure "STORAGE_INVALID_MANIFEST" "Stack revisions cannot contain Flow call bindings." path
            | _ -> ()
            if revision.Definition.Kind <> StorageObjectKind.WordDefinition then
                failure "STORAGE_INVALID_MANIFEST" $"Word revision '{revision.Name}' must reference a word-definition object." path
            if revision.Tests |> List.exists (fun reference -> reference.Kind <> StorageObjectKind.TestDefinition) then
                failure "STORAGE_INVALID_MANIFEST" $"Word revision '{revision.Name}' has a non-test reference in tests." path
            if revision.Examples |> List.exists (fun reference -> reference.Kind <> StorageObjectKind.ExampleDefinition) then
                failure "STORAGE_INVALID_MANIFEST" $"Word revision '{revision.Name}' has a non-example reference in examples." path
            if revision.TimestampUtc.Offset <> TimeSpan.Zero then
                failure "STORAGE_INVALID_MANIFEST" "Word revision timestamps must be expressed in UTC." path
            let duplicateBinding =
                revision.CallBindings
                |> List.groupBy (fun binding -> binding.Source, binding.CaseName, binding.BodyRole, binding.Path)
                |> List.tryFind (fun (_, values) -> values.Length > 1)
            match duplicateBinding with
            | Some(key, _) -> failure "STORAGE_INVALID_MANIFEST" $"Word revision '{revision.Name}' has duplicate call binding key '{key}'." path
            | None -> ()
            for binding in revision.CallBindings do
                validMetadataText "Call binding requested name" 256 path binding.RequestedName
                validMetadataText "Call binding target identity" 128 path (match binding.Target with | StoredCallTarget.UserWord value | StoredCallTarget.Primitive value | StoredCallTarget.GeneratedWord value -> value)
                match binding.Form with
                | StoredCallForm.Direct | StoredCallForm.AbsoluteRoot -> ()
                | StoredCallForm.DotStage stage -> validMetadataText "Call binding dot stage" 128 path stage
                | StoredCallForm.StaticCallback(stage, _) ->
                    if stage <> "map" && stage <> "filter" && stage <> "each" then
                        failure "STORAGE_INVALID_MANIFEST" $"Static callback stage '{stage}' is not supported." path
                let belongsToDefinition = binding.Source = revision.Definition
                let belongsToTest = revision.Tests |> List.contains binding.Source
                let belongsToExample = revision.Examples |> List.contains binding.Source
                let roleMatchesSource =
                    match binding.BodyRole, binding.CaseName, binding.Source.Kind with
                    | StoredCallBodyRole.Definition, None, StorageObjectKind.WordDefinition -> belongsToDefinition
                    | StoredCallBodyRole.Actual, Some caseName, StorageObjectKind.TestDefinition ->
                        validMetadataText "Call binding case name" 256 path caseName
                        belongsToTest
                    | StoredCallBodyRole.Actual, Some caseName, StorageObjectKind.ExampleDefinition ->
                        validMetadataText "Call binding case name" 256 path caseName
                        belongsToExample
                    | StoredCallBodyRole.ExpectedExpression, Some caseName, StorageObjectKind.TestDefinition ->
                        validMetadataText "Call binding case name" 256 path caseName
                        belongsToTest
                    | _ -> false
                if not roleMatchesSource then
                    failure "STORAGE_INVALID_MANIFEST" $"Call binding source, body role, and case name are incompatible with word revision '{revision.Name}'." path

        allSourceRefs manifest |> ignore

    let private manifestBytes manifest =
        validateManifest manifest None
        manifestNode manifest |> fun node -> node.ToJsonString(JsonSerializerOptions(WriteIndented = false)) |> utf8.GetBytes

    let private kindPathExtension kind = extension kind

    let private isWithin (parent: string) (candidate: string) =
        let parent = Path.GetFullPath parent |> fun value -> value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        let candidate = Path.GetFullPath candidate
        let prefix = parent + string Path.DirectorySeparatorChar
        let comparison = if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal
        candidate.StartsWith(prefix, comparison)

    let private combineSafe parent child path =
        let candidate = Path.GetFullPath(Path.Combine(parent, child))
        if not (isWithin parent candidate) then failure "STORAGE_PATH_ESCAPE" "A derived storage path escaped its containing directory." path
        candidate

    let private storeRoot (store: Store) = Path.Combine(store.Root, storeDirectoryName, storeDirectory)
    let private objectRoot (store: Store) = Path.Combine(storeRoot store, objectDirectory)
    let private manifestRoot (store: Store) = Path.Combine(storeRoot store, manifestDirectory)
    let private snapshotRoot (store: Store) = Path.Combine(storeRoot store, snapshotDirectory)
    let private currentPath (store: Store) = Path.Combine(storeRoot store, currentFile)
    let private lockPath (store: Store) = Path.Combine(storeRoot store, lockFile)
    let private exportPath (store: Store) = Path.Combine(store.Root, exportFile)

    let private pathExists path = File.Exists path || Directory.Exists path

    let private rejectReparsePoint path =
        if pathExists path then
            let attributes = File.GetAttributes path
            if attributes.HasFlag FileAttributes.ReparsePoint then
                failure "STORAGE_REPARSE_POINT" "Storage refuses to read or write through a reparse point." (Some path)

    let private rejectReparseAncestors path =
        let rec visit (current: string) =
            rejectReparsePoint current
            let parent = Path.GetDirectoryName current
            if not (String.IsNullOrEmpty parent) && not (String.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) then
                visit parent
        visit (Path.GetFullPath path)

    let private ensureDirectory path =
        rejectReparsePoint path
        if File.Exists path then failure "STORAGE_INVALID_PATH" "Expected a directory but found a file." (Some path)
        if not (Directory.Exists path) then Directory.CreateDirectory path |> ignore
        rejectReparsePoint path

    let private ensureStoreLayout (store: Store) =
        rejectReparseAncestors store.Root
        if not (Directory.Exists store.Root) then Directory.CreateDirectory store.Root |> ignore
        rejectReparseAncestors store.Root
        let agentRoot = Path.Combine(store.Root, storeDirectoryName)
        ensureDirectory agentRoot
        ensureDirectory (storeRoot store)
        ensureDirectory (objectRoot store)
        ensureDirectory (manifestRoot store)
        ensureDirectory (snapshotRoot store)

    let private ensureReadLayout (store: Store) =
        rejectReparseAncestors store.Root
        let agentRoot = Path.Combine(store.Root, storeDirectoryName)
        rejectReparsePoint agentRoot
        rejectReparsePoint (storeRoot store)
        rejectReparsePoint (objectRoot store)
        rejectReparsePoint (manifestRoot store)
        rejectReparsePoint (snapshotRoot store)
        rejectReparsePoint (currentPath store)

    let private objectPath store reference =
        validateHash None reference.Hash
        let directory = objectRoot store
        let name = reference.Hash + kindPathExtension reference.Kind
        combineSafe directory name (Some directory)

    let private manifestPath store hash =
        validateHash None hash
        let directory = manifestRoot store
        combineSafe directory (hash + ".json") (Some directory)

    let private snapshotPath store name =
        if String.IsNullOrWhiteSpace name
           || name.Length > StorageLimits.MaxSnapshotNameLength
           || name = "."
           || name = ".."
           || name |> Seq.exists (fun ch -> not ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch = '-' || ch = '_' || ch = '.')) then
            failure "STORAGE_INVALID_SNAPSHOT_NAME" "Snapshot names must be 1-64 lowercase ASCII letters, digits, dots, underscores, or hyphens and cannot be '.' or '..'." None
        let directory = snapshotRoot store
        combineSafe directory (name + ".json") (Some directory)

    let private readBytes maximum path =
        rejectReparsePoint path
        if Directory.Exists path then failure "STORAGE_INVALID_PATH" "Expected a file but found a directory." (Some path)
        if not (File.Exists path) then failure "STORAGE_OBJECT_MISSING" "Referenced storage file does not exist." (Some path)
        let info = FileInfo path
        if info.Length > int64 maximum then failure "STORAGE_LIMIT_EXCEEDED" $"Storage file exceeds the {maximum}-byte limit." (Some path)
        File.ReadAllBytes path

    let private decodeUtf8 (path: string) (bytes: byte array) =
        try utf8.GetString bytes
        with :? DecoderFallbackException -> failure "STORAGE_INVALID_UTF8" "Stored source is not valid UTF-8." (Some path)

    let private ensureObjectLimits (content: string) =
        if isNull content then failure "STORAGE_INVALID_SOURCE" "Source object content cannot be null." None
        let byteCount = utf8.GetByteCount content
        if byteCount > StorageLimits.MaxObjectBytes then
            failure "STORAGE_LIMIT_EXCEEDED" $"Source object exceeds the {StorageLimits.MaxObjectBytes}-byte limit." None

    let private verifyObjectBytes (path: string) (reference: SourceRef) (bytes: byte array) =
        if bytes.Length > StorageLimits.MaxObjectBytes then
            failure "STORAGE_LIMIT_EXCEEDED" $"Source object exceeds the {StorageLimits.MaxObjectBytes}-byte limit." (Some path)
        let actual = bytes |> SHA256.HashData |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
        if not (String.Equals(actual, reference.Hash, StringComparison.Ordinal)) then
            failure "STORAGE_HASH_MISMATCH" "Stored source bytes do not match their content hash." (Some path)

    let private readSourceCore (store: Store) (reference: SourceRef) =
        validateHash None reference.Hash
        let path = objectPath store reference
        let bytes = readBytes StorageLimits.MaxObjectBytes path
        verifyObjectBytes path reference bytes
        decodeUtf8 path bytes

    let private jsonBytes (node: JsonNode) = node.ToJsonString(JsonSerializerOptions(WriteIndented = false)) |> utf8.GetBytes

    let private writeAtomic (path: string) (bytes: byte array) =
        rejectReparsePoint path
        let directory = Path.GetDirectoryName path
        ensureDirectory directory
        let temporary = Path.Combine(directory, $".{Path.GetFileName path}.{Guid.NewGuid():N}.tmp")
        try
            use stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)
            stream.Write(bytes, 0, bytes.Length)
            stream.Flush(true)
            stream.Dispose()
            rejectReparsePoint path
            File.Move(temporary, path, true)
        finally
            if File.Exists temporary then File.Delete temporary

    let private writeImmutable (path: string) (bytes: byte array) =
        rejectReparsePoint path
        if File.Exists path then
            let existing = readBytes (max StorageLimits.MaxObjectBytes StorageLimits.MaxMetadataBytes) path
            if not (existing.AsSpan().SequenceEqual(bytes.AsSpan())) then
                failure "STORAGE_HASH_COLLISION" "An existing content-addressed path contains different bytes." (Some path)
        else
            let directory = Path.GetDirectoryName path
            ensureDirectory directory
            let temporary = Path.Combine(directory, $".{Path.GetFileName path}.{Guid.NewGuid():N}.tmp")
            try
                use stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                stream.Write(bytes, 0, bytes.Length)
                stream.Flush(true)
                stream.Dispose()
                rejectReparsePoint path
                try File.Move(temporary, path, false)
                with :? IOException when File.Exists path ->
                    let existing = readBytes (max StorageLimits.MaxObjectBytes StorageLimits.MaxMetadataBytes) path
                    if not (existing.AsSpan().SequenceEqual(bytes.AsSpan())) then
                        failure "STORAGE_HASH_COLLISION" "An existing content-addressed path contains different bytes." (Some path)
            finally
                if File.Exists temporary then File.Delete temporary

    let private sourceObjectBytes (item: SourceObject) =
        ensureObjectLimits item.Content
        let expected = sourceObject item.Reference.Kind item.Content
        if expected.Reference.Hash <> item.Reference.Hash then
            failure "STORAGE_HASH_MISMATCH" "Provided source content does not match its declared source reference." None
        utf8.GetBytes item.Content

    let private addJsonProperty (node: JsonObject) (key: string) (value: JsonNode) = node[key] <- value

    let private pointerNode (pointer: Pointer) =
        let node = JsonObject()
        node["formatVersion"] <- jsonInt pointerSnapshotFormatVersion
        node["generation"] <- jsonInt64 pointer.Generation
        match pointer.Authority with
        | EmptyAuthority -> node["kind"] <- jsonString "empty"
        | LegacyAuthority source ->
            node["kind"] <- jsonString "legacy"
            node["legacySource"] <- sourceRefNode source
        | ManifestAuthority hash ->
            node["kind"] <- jsonString "manifest"
            node["manifestHash"] <- jsonString hash
        node

    let private parsePointer (path: string) (node: JsonNode) =
        let value = requireObject "current pointer" node
        let version = requireInt "current pointer format version" value["formatVersion"]
        if version <> pointerSnapshotFormatVersion then
            failure "STORAGE_UNSUPPORTED_VERSION" $"Current pointer format {version} is not supported (expected {pointerSnapshotFormatVersion})." (Some path)
        let generation = requireInt64 "current pointer generation" value["generation"]
        if generation < 1L then failure "STORAGE_INVALID_POINTER" "Current pointer generation must be positive." (Some path)
        let authority =
            match requireString "current pointer kind" value["kind"] with
            | "empty" -> EmptyAuthority
            | "legacy" ->
                let source = parseSourceRef (Some path) value["legacySource"]
                if source.Kind <> StorageObjectKind.LegacyDictionary then failure "STORAGE_INVALID_POINTER" "Legacy pointer must reference a legacy-dictionary object." (Some path)
                LegacyAuthority source
            | "manifest" ->
                let hash = requireString "current pointer manifest hash" value["manifestHash"]
                validateHash (Some path) hash
                ManifestAuthority hash
            | kind -> failure "STORAGE_INVALID_POINTER" $"Unknown current pointer kind '{kind}'." (Some path)
        { Generation = generation; Authority = authority }

    let private parseJsonBytes (path: string) (bytes: byte array) =
        try JsonNode.Parse(decodeUtf8 path bytes)
        with
        | :? JsonException as ex -> failure "STORAGE_INVALID_JSON" ex.Message (Some path)

    let private readJson (path: string) (maximum: int) =
        let bytes = readBytes maximum path
        parseJsonBytes path bytes

    let private currentPointer store =
        let path = currentPath store
        if not (File.Exists path) && not (Directory.Exists path) then None
        else
            let node = readJson path StorageLimits.MaxMetadataBytes
            Some(parsePointer path node)

    let private validateManifestReferences (store: Store) (provided: SourceObject list) (manifest: ProjectManifest) (path: string option) =
        validateManifest manifest path
        if provided.Length > StorageLimits.MaxReferences then
            failure "STORAGE_LIMIT_EXCEEDED" $"A commit may provide at most {StorageLimits.MaxReferences} source objects." path
        // Validate every supplied object, including unused entries, before any
        // of them can be written. Conflicting duplicate references are rejected
        // instead of silently choosing whichever appears first.
        for item in provided do sourceObjectBytes item |> ignore
        let providedByRef =
            provided
            |> List.map (fun item -> item.Reference, item)
            |> List.groupBy fst
            |> List.map (fun (reference, entries) ->
                let items = entries |> List.map snd
                let first = items.Head
                if items |> List.exists (fun item -> not (String.Equals(item.Content, first.Content, StringComparison.Ordinal))) then
                    failure "STORAGE_DUPLICATE_OBJECT" "The provided source list contains conflicting content for one source reference." None
                reference, first)
            |> Map.ofList
        for reference in allSourceRefs manifest do
            match providedByRef.TryFind reference with
            | Some item -> sourceObjectBytes item |> ignore
            | None -> readSourceCore store reference |> ignore
        providedByRef

    let private manifestHashPath (store: Store) (manifest: ProjectManifest) =
        let bytes = manifestBytes manifest
        if bytes.Length > StorageLimits.MaxMetadataBytes then
            failure "STORAGE_LIMIT_EXCEEDED" $"Manifest exceeds the {StorageLimits.MaxMetadataBytes}-byte limit." None
        let hash = SHA256.HashData bytes |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
        hash, bytes

    let private loadManifestCore store hash =
        validateHash None hash
        let path = manifestPath store hash
        let bytes = readBytes StorageLimits.MaxMetadataBytes path
        let actual = bytes |> SHA256.HashData |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
        if actual <> hash then failure "STORAGE_HASH_MISMATCH" "Manifest bytes do not match their content hash." (Some path)
        // Parse precisely the bytes whose hash was just verified. Reading the
        // file again here would permit a swap between verification and parsing.
        let manifest = parseJsonBytes path bytes |> parseManifest (Some path)
        validateManifest manifest (Some path)
        let refs = allSourceRefs manifest
        let projectSource = readSourceCore store manifest.ProjectSource
        for reference in refs do readSourceCore store reference |> ignore
        manifest, projectSource

    let private loadCore store =
        ensureReadLayout store
        match currentPointer store with
        | Some pointer ->
            match pointer.Authority with
            | EmptyAuthority ->
                { Generation = pointer.Generation; Authority = EmptyAuthority; Manifest = None; ManifestHash = None; ProjectSource = None; ExportWarning = None }
            | LegacyAuthority reference ->
                let source = readSourceCore store reference
                { Generation = pointer.Generation; Authority = pointer.Authority; Manifest = None; ManifestHash = None; ProjectSource = Some source; ExportWarning = None }
            | ManifestAuthority hash ->
                let manifest, source = loadManifestCore store hash
                { Generation = pointer.Generation; Authority = pointer.Authority; Manifest = Some manifest; ManifestHash = Some hash; ProjectSource = Some source; ExportWarning = None }
        | None ->
            let path = exportPath store
            if not (File.Exists path) && not (Directory.Exists path) then
                { Generation = 0L; Authority = EmptyAuthority; Manifest = None; ManifestHash = None; ProjectSource = None; ExportWarning = None }
            else
                let bytes = readBytes StorageLimits.MaxObjectBytes path
                let source = decodeUtf8 path bytes
                let reference = (sourceObject StorageObjectKind.LegacyDictionary source).Reference
                { Generation = 0L; Authority = LegacyAuthority reference; Manifest = None; ManifestHash = None; ProjectSource = Some source; ExportWarning = None }

    let private writeExport (store: Store) (source: string option) =
        let path = exportPath store
        match source with
        | Some contents ->
            ensureObjectLimits contents
            writeAtomic path (utf8.GetBytes contents)
        | None ->
            rejectReparsePoint path
            if Directory.Exists path then failure "STORAGE_INVALID_PATH" "Cannot remove the project export because the path is a directory." (Some path)
            if File.Exists path then File.Delete path

    let private inject (store: Store) point =
        try
            store.Inject point
            None
        with ex -> Some { Code = "STORAGE_INJECTED_FAILURE"; Message = ex.Message; Path = None }

    let private exportWarning store source =
        match inject store StorageFailurePoint.BeforeExportRefresh with
        | Some error -> Some error
        | None ->
            match catchStorage (fun () -> writeExport store source) with
            | Error error -> Some { error with Code = "STORAGE_EXPORT_FAILED" }
            | Ok () -> inject store StorageFailurePoint.AfterExportRefresh

    let private currentAuthorityAndGeneration store =
        let loaded = loadCore store
        loaded.Generation, loaded.Authority, loaded.ManifestHash, loaded.ProjectSource

    let private withWriterLock (store: Store) action =
        match catchStorage (fun () -> ensureStoreLayout store) with
        | Error error -> Error error
        | Ok () ->
            let path = lockPath store
            try
                rejectReparsePoint path
                use _lock = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
                catchStorage action
            with
            | StorageFailure error -> Error error
            | :? IOException as ex -> Error { Code = "STORAGE_WRITER_LOCKED"; Message = ex.Message; Path = Some path }
            | :? UnauthorizedAccessException as ex -> Error { Code = "STORAGE_ACCESS_DENIED"; Message = ex.Message; Path = Some path }
            | ex -> Error { Code = "STORAGE_FAILURE"; Message = ex.Message; Path = Some path }

    let private writePointer (store: Store) (pointer: Pointer) =
        let path = currentPath store
        let bytes = pointerNode pointer |> jsonBytes
        if bytes.Length > StorageLimits.MaxMetadataBytes then failure "STORAGE_LIMIT_EXCEEDED" "Current pointer exceeds its size limit." (Some path)
        writeAtomic path bytes

    let private commitPointer store oldGeneration authority exportText =
        if oldGeneration = Int64.MaxValue then failure "STORAGE_GENERATION_EXHAUSTED" "Storage generation cannot be incremented further." None
        let generation = oldGeneration + 1L
        match inject store StorageFailurePoint.BeforePointerReplacement with
        | Some error -> throwError error
        | None -> ()
        writePointer store { Generation = generation; Authority = authority }
        let afterPointerWarning = inject store StorageFailurePoint.AfterPointerReplacement
        let warning =
            match afterPointerWarning, exportWarning store exportText with
            | Some error, Some _ -> Some error
            | Some error, None -> Some error
            | None, warning -> warning
        { Generation = generation
          Authority = authority
          ManifestHash = match authority with ManifestAuthority hash -> Some hash | _ -> None
          ExportWarning = warning }

    let private validateVirtualPath (path: string) =
        if isNull path || String.IsNullOrWhiteSpace path || path.Length > 4096
           || path.StartsWith("/", StringComparison.Ordinal)
           || path.Contains('\\') || path.Contains(':') || path.Contains('\u0000')
           || (path.Split('/') |> Array.exists (fun segment -> String.IsNullOrEmpty segment || segment = "." || segment = "..")) then
            failure "STORAGE_INVALID_VIRTUAL_PATH" "Virtual file paths must be relative slash-separated paths without empty, '.' or '..' segments." None

    let private encodeVirtualFiles (files: Map<string, string>) =
        if files.Count > StorageLimits.MaxVirtualFiles then
            failure "STORAGE_LIMIT_EXCEEDED" $"A snapshot may contain at most {StorageLimits.MaxVirtualFiles} virtual files." None
        let node = JsonObject()
        for KeyValue(path, contents) in files do
            validateVirtualPath path
            if isNull contents then failure "STORAGE_INVALID_SNAPSHOT" "Virtual file contents must be non-null." None
            node[path] <- jsonString contents
        node.ToJsonString(JsonSerializerOptions(WriteIndented = false))

    let private decodeVirtualFiles (path: string) (source: string) =
        let node = JsonNode.Parse(source) |> requireObject "virtual file state"
        if node.Count > StorageLimits.MaxVirtualFiles then
            failure "STORAGE_LIMIT_EXCEEDED" $"Snapshot contains more than {StorageLimits.MaxVirtualFiles} virtual files." (Some path)
        node
        |> Seq.map (fun pair ->
            validateVirtualPath pair.Key
            pair.Key, requireString "virtual file contents" pair.Value)
        |> Map.ofSeq

    let private snapshotFileNode (snapshot: SnapshotFile) =
        let node = JsonObject()
        node["formatVersion"] <- jsonInt pointerSnapshotFormatVersion
        node["manifestHash"] <- jsonString snapshot.ManifestHash
        node["virtualFiles"] <- snapshot.VirtualFiles |> Option.map sourceRefNode |> Option.defaultValue null
        node["clockValue"] <- snapshot.ClockValue |> Option.map jsonString |> Option.defaultValue null
        node :> JsonNode

    let private parseSnapshotFile path node =
        let value = requireObject "named snapshot" node
        let version = requireInt "snapshot format version" value["formatVersion"]
        if version <> pointerSnapshotFormatVersion then
            failure "STORAGE_UNSUPPORTED_VERSION" $"Snapshot format {version} is not supported (expected {pointerSnapshotFormatVersion})." (Some path)
        let manifestHash = requireString "snapshot manifest hash" value["manifestHash"]
        validateHash (Some path) manifestHash
        let virtualFiles = if isNull value["virtualFiles"] then None else Some(parseSourceRef (Some path) value["virtualFiles"])
        match virtualFiles with
        | Some reference when reference.Kind <> StorageObjectKind.VirtualFileState -> failure "STORAGE_INVALID_SNAPSHOT" "Snapshot virtualFiles must reference a virtual-file-state object." (Some path)
        | _ -> ()
        let clockValue = optionalString "snapshot clock value" value["clockValue"]
        match clockValue with
        | Some clock when clock.Length > 256 -> failure "STORAGE_LIMIT_EXCEEDED" "Snapshot clock value exceeds 256 characters." (Some path)
        | _ -> ()
        { ManifestHash = manifestHash; VirtualFiles = virtualFiles; ClockValue = clockValue }

    let private ensureExpectedGeneration expectedGeneration actualGeneration =
        if expectedGeneration <> actualGeneration then
            failure "STORAGE_STALE_GENERATION" $"Expected generation {expectedGeneration}, but the current generation is {actualGeneration}." None

    let private refreshExport (store: Store) (loaded: StorageLoadResult) =
        let path = exportPath store
        let exportText = loaded.ProjectSource
        let alreadyCurrent =
            match exportText with
            | None -> not (File.Exists path || Directory.Exists path)
            | Some expected when File.Exists path ->
                try
                    let current = readBytes StorageLimits.MaxObjectBytes path |> decodeUtf8 path
                    String.Equals(current, expected, StringComparison.Ordinal)
                with _ -> false
            | Some _ -> false
        if alreadyCurrent then loaded
        else { loaded with ExportWarning = exportWarning store exportText }

    /// Load the currently authoritative project state. A missing CURRENT file
    /// permits a read-only legacy import of dictionary.agent; no migration file
    /// is written until a successful commit or restore.
    let load (store: Store) : Result<StorageLoadResult, StorageError> =
        withWriterLock store (fun () ->
            let loaded = loadCore store
            refreshExport store loaded)

    /// Capture the exact logical authority and current export bytes for task
    /// rollback. The saved generation is descriptive; restore advances the live
    /// generation monotonically.
    let capture (store: Store) : Result<StoreSnapshot, StorageError> =
        withWriterLock store (fun () ->
            let loaded = loadCore store
            let path = exportPath store
            let exportText =
                if File.Exists path then
                    Some(readBytes StorageLimits.MaxObjectBytes path |> decodeUtf8 path)
                elif Directory.Exists path then
                    failure "STORAGE_INVALID_PATH" "The project export path is a directory." (Some path)
                else None
            { Generation = loaded.Generation
              Authority = loaded.Authority
              ExportText = exportText })

    /// Validate and publish a new immutable manifest. The only authoritative
    /// commit point is the atomic replacement of CURRENT; export refresh occurs
    /// after that point and reports a warning rather than a false failed commit.
    let commit
        (store: Store)
        (expectedGeneration: int64)
        (manifest: ProjectManifest)
        (sources: SourceObject list)
        (exportText: string)
        : Result<StorageCommitResult, StorageError> =
        withWriterLock store (fun () ->
            let current = loadCore store
            ensureExpectedGeneration expectedGeneration current.Generation
            let provided = validateManifestReferences store sources manifest None
            let projectSource =
                match provided.TryFind manifest.ProjectSource with
                | Some item -> item.Content
                | None -> readSourceCore store manifest.ProjectSource
            if not (String.Equals(projectSource, exportText, StringComparison.Ordinal)) then
                failure "STORAGE_EXPORT_MISMATCH" "The export text must exactly match the manifest project source." None

            let referenced = allSourceRefs manifest |> Set.ofList
            for KeyValue(reference, item) in provided do
                if referenced.Contains reference then
                    let path = objectPath store reference
                    writeImmutable path (sourceObjectBytes item)

            let hash, bytes = manifestHashPath store manifest
            let manifestFile = manifestPath store hash
            writeImmutable manifestFile bytes
            commitPointer store current.Generation (ManifestAuthority hash) (Some exportText))

    /// Restore a captured authority without manufacturing revisions. This is a
    /// new pointer generation whose manifest/history identity remains unchanged.
    let restore
        (store: Store)
        (expectedGeneration: int64)
        (snapshot: StoreSnapshot)
        : Result<StorageCommitResult, StorageError> =
        withWriterLock store (fun () ->
            let current = loadCore store
            ensureExpectedGeneration expectedGeneration current.Generation
            let exportText = snapshot.ExportText
            let targetAuthority =
                match snapshot.Authority with
                | EmptyAuthority -> EmptyAuthority
                | LegacyAuthority reference ->
                    if reference.Kind <> StorageObjectKind.LegacyDictionary then
                        failure "STORAGE_INVALID_SNAPSHOT" "Legacy authority must reference a legacy-dictionary object." None
                    match exportText with
                    | Some content ->
                        let item = sourceObject StorageObjectKind.LegacyDictionary content
                        if item.Reference <> reference then
                            failure "STORAGE_INVALID_SNAPSHOT" "Captured legacy export does not match its content reference." None
                        writeImmutable (objectPath store reference) (sourceObjectBytes item)
                    | None -> readSourceCore store reference |> ignore
                    LegacyAuthority reference
                | ManifestAuthority hash ->
                    validateHash None hash
                    loadManifestCore store hash |> ignore
                    ManifestAuthority hash
            commitPointer store current.Generation targetAuthority exportText)

    /// Save provider state against the current manifest without changing project
    /// authority or generation. Host capabilities are intentionally excluded.
    let saveSnapshot
        (store: Store)
        (expectedGeneration: int64)
        (name: string)
        (virtualFiles: Map<string, string>)
        (clockValue: string option)
        : Result<unit, StorageError> =
        withWriterLock store (fun () ->
            let current = loadCore store
            ensureExpectedGeneration expectedGeneration current.Generation
            let manifestHash =
                match current.Authority with
                | ManifestAuthority hash -> hash
                | _ -> failure "STORAGE_SNAPSHOT_REQUIRES_MANIFEST" "Named snapshots require an active project manifest." None
            loadManifestCore store manifestHash |> ignore
            match clockValue with
            | Some value when isNull value || value.Length > 256 ->
                failure "STORAGE_INVALID_SNAPSHOT" "Clock value must be at most 256 characters." None
            | _ -> ()
            let virtualFilesReference =
                if virtualFiles.IsEmpty then None
                else
                    let content = encodeVirtualFiles virtualFiles
                    let item = sourceObject StorageObjectKind.VirtualFileState content
                    writeImmutable (objectPath store item.Reference) (sourceObjectBytes item)
                    Some item.Reference
            let snapshot =
                { ManifestHash = manifestHash
                  VirtualFiles = virtualFilesReference
                  ClockValue = clockValue }
            let bytes = snapshotFileNode snapshot |> jsonBytes
            if bytes.Length > StorageLimits.MaxMetadataBytes then
                failure "STORAGE_LIMIT_EXCEEDED" $"Named snapshot exceeds the {StorageLimits.MaxMetadataBytes}-byte limit." None
            writeAtomic (snapshotPath store name) bytes)

    /// Read and validate a named snapshot without activating its manifest or
    /// provider state.
    let readSnapshot (store: Store) (name: string) : Result<NamedSnapshot, StorageError> =
        catchStorage (fun () ->
            ensureReadLayout store
            let path = snapshotPath store name
            let snapshotFile = readJson path StorageLimits.MaxMetadataBytes |> parseSnapshotFile path
            let manifest, _ = loadManifestCore store snapshotFile.ManifestHash
            let virtualFiles =
                match snapshotFile.VirtualFiles with
                | None -> Map.empty
                | Some reference -> readSourceCore store reference |> decodeVirtualFiles (objectPath store reference)
            { ManifestHash = snapshotFile.ManifestHash
              Manifest = manifest
              VirtualFiles = virtualFiles
              ClockValue = snapshotFile.ClockValue })

    /// Activate a named snapshot using the same pointer transaction as a task
    /// restore. Runtime should apply returned provider state only after success.
    let restoreSnapshot
        (store: Store)
        (expectedGeneration: int64)
        (snapshot: NamedSnapshot)
        : Result<StorageCommitResult, StorageError> =
        withWriterLock store (fun () ->
            let current = loadCore store
            ensureExpectedGeneration expectedGeneration current.Generation
            encodeVirtualFiles snapshot.VirtualFiles |> ignore
            match snapshot.ClockValue with
            | Some clock when isNull clock || clock.Length > 256 ->
                failure "STORAGE_INVALID_SNAPSHOT" "Clock value must be at most 256 characters." None
            | _ -> ()
            let persistedManifest, source = loadManifestCore store snapshot.ManifestHash
            if manifestBytes persistedManifest <> manifestBytes snapshot.Manifest then
                failure "STORAGE_SNAPSHOT_MANIFEST_MISMATCH" "Snapshot manifest does not match the persisted manifest object." None
            let authority = ManifestAuthority snapshot.ManifestHash
            commitPointer store current.Generation authority (Some source))

    /// Read an exact UTF-8 source object after verifying its content hash.
    let readSource (store: Store) (reference: SourceRef) : Result<string, StorageError> =
        catchStorage (fun () -> ensureReadLayout store; readSourceCore store reference)

    /// Read one immutable historical word revision and its attached metadata.
    let readRevision
        (store: Store)
        (manifestHash: string)
        (wordId: string)
        (revision: int)
        : Result<RevisionContent, StorageError> =
        catchStorage (fun () ->
            ensureReadLayout store
            let manifest, _ = loadManifestCore store manifestHash
            match manifest.Revisions |> List.tryFind (fun (item: WordRevision) -> item.WordId = wordId && item.Revision = revision) with
            | None -> failure "STORAGE_REVISION_NOT_FOUND" $"Word revision '{wordId}/{revision}' was not found in manifest {manifestHash}." None
            | Some item ->
                { Revision = item
                  DefinitionSource = readSourceCore store item.Definition
                  TestSources = item.Tests |> List.map (readSourceCore store)
                  ExampleSources = item.Examples |> List.map (readSourceCore store) })

    /// Persist a machine-readable task log under the project history directory.
    /// Task IDs are restricted to a filename-safe ASCII alphabet.
    let saveTaskLog (store: Store) (taskId: string) (json: string) : Result<unit, StorageError> =
        withWriterLock store (fun () ->
            if String.IsNullOrWhiteSpace taskId
               || taskId.Length > 128
               || taskId |> Seq.exists (fun ch -> not ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch = '-' || ch = '_')) then
                failure "STORAGE_INVALID_TASK_ID" "Task IDs may contain only ASCII letters, digits, hyphens, and underscores." None
            if isNull json then failure "STORAGE_INVALID_TASK_LOG" "Task log JSON cannot be null." None
            let bytes = utf8.GetBytes json
            if bytes.Length > StorageLimits.MaxMetadataBytes then
                failure "STORAGE_LIMIT_EXCEEDED" $"Task log exceeds the {StorageLimits.MaxMetadataBytes}-byte limit." None
            try
                use _document = JsonDocument.Parse json
                let directory = Path.Combine(store.Root, taskHistoryDirectory)
                ensureDirectory directory
                let path = combineSafe directory ($"task-{taskId}.json") (Some directory)
                writeAtomic path bytes
            with
            | :? JsonException as ex -> failure "STORAGE_INVALID_TASK_LOG" ex.Message None)
