namespace AgentLang

open System
open System.Collections.Generic
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

/// An immutable structural index over the runtime's effective type and word maps.
/// Its representation is private so callers cannot mutate its precomputed graph.
type DiscoveryIndex = private { Words: Map<string, WordEntry>; Records: Map<string, RecordEntry>; Scalars: Map<string, ScalarEntry>; Dependencies: Map<string, Set<string>>; Callers: Map<string, Set<string>> }

type DiscoveryGraphResult =
    { Text: string
      ExpandedWords: string list
      OmittedWords: int
      Truncated: bool }

type private DiscoveryGraphStep =
    | VisitWord of depth: int * indent: string * ancestors: Set<string> * name: string
    | VisitDependency of depth: int * indent: string * ancestors: Set<string> * caller: string * dependency: string

/// Content is a complete JSON document. Utf8Bytes measures the exact serialized
/// document, including its budget and omission metadata, rather than tokens.
type DiscoveryContextResult =
    { Content: string
      Utf8Bytes: int
      WordsIncluded: string list
      TypesIncluded: string list
      WordsOmitted: int
      TypesOmitted: int
      Truncated: bool
      TruncationReasons: string list }

[<RequireQualifiedAccess>]
module Discovery =
    let private utf8 = UTF8Encoding(false, true)
    let private jsonOptions = JsonSerializerOptions(WriteIndented = false)

    let private raiseDiscovery code message word expected actual =
        Diagnostics.raiseError code message word None expected actual

    let private typeNamesIn (typeValue: LangType) =
        let rec collect = function
            | TNamed name -> Set.singleton name
            | TList item | TOption item -> collect item
            | TResult(okType, errorType) -> Set.union (collect okType) (collect errorType)
            | TInt | TFloat | TBool | TString | TUnit | TVar _ -> Set.empty
        collect typeValue

    let private typeContains (target: LangType) (candidate: LangType) =
        let rec contains value =
            value = target
            || (match value with
                | TList item | TOption item -> contains item
                | TResult(okType, errorType) -> contains okType || contains errorType
                | TInt | TFloat | TBool | TString | TUnit | TNamed _ | TVar _ -> false)
        contains candidate

    let private namedTypeClosure (records: Map<string, RecordEntry>) (scalars: Map<string, ScalarEntry>) (initial: Set<string>) =
        let rec visit (pending: string list) (found: Set<string>) =
            match pending with
            | [] -> found
            | name :: rest when found.Contains name -> visit rest found
            | name :: rest ->
                let nested =
                    match records.TryFind name, scalars.TryFind name with
                    | Some record, _ ->
                        record.Definition.Fields
                        |> List.collect (fun field -> typeNamesIn field.Type |> Set.toList)
                    | None, Some scalar -> typeNamesIn scalar.Definition.BaseType |> Set.toList
                    | None, None -> []
                visit (rest @ List.sort nested) (Set.add name found)
        visit (initial |> Set.toList) Set.empty

    let private directDependencies (scalars: Map<string, ScalarEntry>) (entry: WordEntry) =
        match entry.Builtin with
        | None -> Compiler.dependencies entry.Definition.Body
        | Some(ScalarConstructor scalarName) ->
            scalars.TryFind scalarName
            |> Option.bind (fun scalar -> scalar.Definition.Validator)
            |> Option.map Set.singleton
            |> Option.defaultValue Set.empty
        | Some(BuiltinOp _ | RecordConstructor _ | RecordAccessor _ | ScalarAccessor _) -> Set.empty

    let private validateBuiltinReference (records: Map<string, RecordEntry>) (scalars: Map<string, ScalarEntry>) name builtin =
        match builtin with
        | None | Some(BuiltinOp _) -> ()
        | Some(RecordConstructor recordName) ->
            if not (records.ContainsKey recordName) then
                raiseDiscovery "DISCOVERY_UNKNOWN_RECORD" $"Generated word '{name}' references missing record '{recordName}'." (Some name) [ "known record" ] [ recordName ]
        | Some(RecordAccessor(recordName, fieldName)) ->
            match records.TryFind recordName with
            | None -> raiseDiscovery "DISCOVERY_UNKNOWN_RECORD" $"Generated word '{name}' references missing record '{recordName}'." (Some name) [ "known record" ] [ recordName ]
            | Some record when record.Definition.Fields |> List.exists (fun field -> field.Name = fieldName) -> ()
            | Some _ -> raiseDiscovery "DISCOVERY_UNKNOWN_FIELD" $"Generated word '{name}' references missing field '{recordName}.{fieldName}'." (Some name) [ "known record field" ] [ fieldName ]
        | Some(ScalarConstructor scalarName | ScalarAccessor scalarName) ->
            if not (scalars.ContainsKey scalarName) then
                raiseDiscovery "DISCOVERY_UNKNOWN_SCALAR" $"Generated word '{name}' references missing scalar '{scalarName}'." (Some name) [ "known scalar" ] [ scalarName ]

    let private validateNamedReferences (records: Map<string, RecordEntry>) (scalars: Map<string, ScalarEntry>) (owner: string) (typeValue: LangType) =
        for name in typeNamesIn typeValue do
            if not (records.ContainsKey name || scalars.ContainsKey name) then
                raiseDiscovery "DISCOVERY_UNKNOWN_TYPE" $"'{owner}' refers to undeclared nominal type '{name}'." (Some owner) [ "known record or scalar type" ] [ name ]

    /// Build a deterministic graph from immutable runtime snapshots. User-word
    /// edges include calls and static list callbacks. A generated scalar
    /// constructor has a direct edge to its declared validator, if any.
    let build
        (words: Map<string, WordEntry>)
        (records: Map<string, RecordEntry>)
        (scalars: Map<string, ScalarEntry>)
        : DiscoveryIndex =
        for KeyValue(key, entry) in words do
            if key <> entry.Definition.Name then
                raiseDiscovery "DISCOVERY_WORD_KEY_MISMATCH" $"Word map key '{key}' does not match definition name '{entry.Definition.Name}'." (Some key) [ key ] [ entry.Definition.Name ]
            validateBuiltinReference records scalars key entry.Builtin
            for typeValue in entry.Definition.Inputs @ entry.Definition.Outputs do
                validateNamedReferences records scalars key typeValue

        for KeyValue(key, entry) in records do
            if key <> entry.Definition.Name then
                raiseDiscovery "DISCOVERY_RECORD_KEY_MISMATCH" $"Record map key '{key}' does not match definition name '{entry.Definition.Name}'." (Some key) [ key ] [ entry.Definition.Name ]
            let duplicateField = entry.Definition.Fields |> List.groupBy (fun field -> field.Name) |> List.tryFind (fun (_, values) -> values.Length > 1)
            match duplicateField with
            | Some(name, _) -> raiseDiscovery "DISCOVERY_DUPLICATE_FIELD" $"Record '{key}' declares field '{name}' more than once." (Some key) [ "unique field names" ] [ name ]
            | None -> ()
            for field in entry.Definition.Fields do validateNamedReferences records scalars key field.Type

        for KeyValue(key, entry) in scalars do
            if key <> entry.Definition.Name then
                raiseDiscovery "DISCOVERY_SCALAR_KEY_MISMATCH" $"Scalar map key '{key}' does not match definition name '{entry.Definition.Name}'." (Some key) [ key ] [ entry.Definition.Name ]
            validateNamedReferences records scalars key entry.Definition.BaseType
            match entry.Definition.Validator with
            | None -> ()
            | Some validator when not (words.ContainsKey validator) ->
                raiseDiscovery "DISCOVERY_UNKNOWN_VALIDATOR" $"Scalar '{key}' names missing validator word '{validator}'." (Some key) [ "known validator word" ] [ validator ]
            | Some _ -> ()

        let duplicateType =
            records
            |> Map.toSeq
            |> Seq.map fst
            |> Set.ofSeq
            |> Set.intersect (scalars |> Map.toSeq |> Seq.map fst |> Set.ofSeq)
            |> Set.toList
            |> List.tryHead
        match duplicateType with
        | Some name -> raiseDiscovery "DISCOVERY_DUPLICATE_TYPE" $"'{name}' is both a record and a scalar type." (Some name) [ "one nominal type declaration" ] [ "record"; "scalar" ]
        | None -> ()

        for KeyValue(name, entry) in words do
            let direct = directDependencies scalars entry
            for dependency in direct do
                if not (words.ContainsKey dependency) then
                    raiseDiscovery "DISCOVERY_UNKNOWN_WORD" $"Word '{name}' depends on missing word '{dependency}'." (Some name) [ "known word dependency" ] [ dependency ]

        let dependencies = words |> Map.map (fun _ entry -> directDependencies scalars entry)
        let callers =
            words
            |> Map.toSeq
            |> Seq.fold (fun (graph: Map<string, Set<string>>) (caller, _) ->
                dependencies[caller]
                |> Set.fold (fun result dependency ->
                    let existing = result.TryFind dependency |> Option.defaultValue Set.empty
                    Map.add dependency (Set.add caller existing) result) graph) Map.empty
        { Words = words; Records = records; Scalars = scalars; Dependencies = dependencies; Callers = callers }

    let private requireKnownWord (index: DiscoveryIndex) (name: string) =
        if not (index.Words.ContainsKey name) then
            raiseDiscovery "DISCOVERY_UNKNOWN_ROOT" $"Unknown word '{name}'." (Some name) [ "known word" ] [ name ]

    let private requireNonempty (name: string) (description: string) =
        if String.IsNullOrWhiteSpace name then
            raiseDiscovery "DISCOVERY_INVALID_QUERY" $"{description} must be nonempty." None [ description ] [ name ]

    let private dependenciesFor (index: DiscoveryIndex) (name: string) =
        index.Dependencies.TryFind name |> Option.defaultValue Set.empty

    let private callersFor (index: DiscoveryIndex) (name: string) =
        index.Callers.TryFind name |> Option.defaultValue Set.empty

    /// Direct dependencies in stable ordinal order.
    let dependencies (index: DiscoveryIndex) (name: string) =
        requireKnownWord index name
        dependenciesFor index name |> Set.toList

    let private closure (neighbors: string -> Set<string>) (root: string) =
        let rec visit (pending: string list) (found: Set<string>) =
            match pending with
            | [] -> found
            | name :: rest when found.Contains name -> visit rest found
            | name :: rest -> visit (rest @ (neighbors name |> Set.toList)) (Set.add name found)
        visit (neighbors root |> Set.toList) Set.empty |> Set.remove root |> Set.toList

    /// All transitive dependencies in ordinal order, with the root excluded even
    /// if a reachable cycle points back to it.
    let transitiveDependencies (index: DiscoveryIndex) (root: string) =
        requireKnownWord index root
        closure (dependenciesFor index) root

    /// All transitive callers in ordinal order, with the root excluded even in
    /// recursive caller cycles.
    let transitiveCallers (index: DiscoveryIndex) (root: string) =
        requireKnownWord index root
        closure (callersFor index) root

    let private containsInSignature (target: LangType) (definition: WordDefinition) =
        definition.Inputs @ definition.Outputs |> List.exists (typeContains target)

    /// Exact structural search over any input or output subtree. Nominal names
    /// compare exactly: TNamed "Email" never matches TString.
    let searchType (index: DiscoveryIndex) (target: LangType) =
        index.Words
        |> Map.toList
        |> List.choose (fun (name, entry) -> if containsInSignature target entry.Definition then Some name else None)

    /// Exact structural search over output type subtrees only.
    let searchOutput (index: DiscoveryIndex) (target: LangType) =
        index.Words
        |> Map.toList
        |> List.choose (fun (name, entry) ->
            if entry.Definition.Outputs |> List.exists (typeContains target) then Some name else None)

    /// Search declared effects verbatim. Effect metadata remains conservative;
    /// Discovery does not try to infer a narrower effect set from a body.
    let searchEffect (index: DiscoveryIndex) (effect: string) =
        requireNonempty effect "Effect name"
        index.Words
        |> Map.toList
        |> List.choose (fun (name, entry) -> if entry.Definition.Effects.Contains effect then Some name else None)

    /// Return words with a direct edge to the named dependency.
    let searchDependency (index: DiscoveryIndex) (dependency: string) =
        requireNonempty dependency "Dependency name"
        index.Words
        |> Map.toList
        |> List.choose (fun (name, _) -> if (dependenciesFor index name).Contains dependency then Some name else None)

    let private breadthFirst (neighbors: string -> Set<string>) (root: string) (maxDepth: int) =
        let pending = Queue<string * int>()
        let seen = HashSet<string>(StringComparer.Ordinal)
        let found = ResizeArray<string * int>()
        pending.Enqueue(root, 0)
        while pending.Count > 0 do
            let name, depth = pending.Dequeue()
            if seen.Add name then
                found.Add(name, depth)
                if depth < maxDepth then
                    neighbors name |> Set.iter (fun child -> if not (seen.Contains child) then pending.Enqueue(child, depth + 1))
        found |> Seq.toList

    let private reachableWords index root = breadthFirst (dependenciesFor index) root Int32.MaxValue |> List.map fst

    let private validateBudgets maxDepth maxWords maxUtf8Bytes =
        if maxDepth < 0 || maxWords < 1 || maxUtf8Bytes < 1 then
            raiseDiscovery "DISCOVERY_INVALID_BUDGET" "Discovery budgets require maxDepth >= 0, maxWords >= 1, and maxUtf8Bytes >= 1." None [ "nonnegative depth, positive word count and positive byte count" ] [ string maxDepth; string maxWords; string maxUtf8Bytes ]

    /// Render a deterministic dependency tree. Cycles and previously rendered
    /// nodes are shown explicitly; depth and node caps produce a final summary.
    let graphText (index: DiscoveryIndex) root maxDepth maxNodes =
        requireKnownWord index root
        if maxDepth < 0 || maxNodes < 1 then
            raiseDiscovery "DISCOVERY_INVALID_BUDGET" "Graph budgets require maxDepth >= 0 and maxNodes >= 1." (Some root) [ "nonnegative depth and positive node count" ] [ string maxDepth; string maxNodes ]
        let allReachable = reachableWords index root |> Set.ofList
        let expanded = ResizeArray<string>()
        let seen = HashSet<string>(StringComparer.Ordinal)
        let output = ResizeArray<string>()
        let mutable reachedDepthLimit = false
        let mutable reachedNodeLimit = false

        let pending = Stack<DiscoveryGraphStep>()
        pending.Push(VisitWord(0, "", Set.empty, root))
        while pending.Count > 0 do
            match pending.Pop() with
            | VisitWord(depth, indent, ancestors, name) ->
                seen.Add name |> ignore
                expanded.Add name
                output.Add(indent + name)
                let direct = dependenciesFor index name |> Set.toList
                if not direct.IsEmpty then
                    if depth >= maxDepth then
                        for dependency in direct do
                            let line = indent + "  - " + dependency
                            if ancestors.Contains dependency then
                                output.Add(line + " [cycle]")
                            elif seen.Contains dependency then
                                output.Add(line + " [reused]")
                            else
                                reachedDepthLimit <- true
                                output.Add(line + " [depth limit]")
                    else
                        for dependency in List.rev direct do
                            pending.Push(VisitDependency(depth, indent, ancestors, name, dependency))
            | VisitDependency(depth, indent, ancestors, caller, dependency) ->
                let line = indent + "  - " + dependency
                if ancestors.Contains dependency then
                    output.Add(line + " [cycle]")
                elif seen.Contains dependency then
                    output.Add(line + " [reused]")
                elif expanded.Count >= maxNodes then
                    reachedNodeLimit <- true
                    output.Add(line + " [node limit]")
                else
                    pending.Push(VisitWord(depth + 1, indent + "  ", Set.add caller ancestors, dependency))
        let omitted = Set.difference allReachable (Set.ofSeq seen)
        if not omitted.IsEmpty then
            let reasons =
                [ if reachedDepthLimit then "depth"
                  if reachedNodeLimit then "nodes" ]
                |> String.concat ","
            output.Add($"truncated: {omitted.Count} unique word(s) omitted ({reasons})")
        { Text = String.concat "\n" output
          ExpandedWords = List.ofSeq expanded
          OmittedWords = omitted.Count
          Truncated = not omitted.IsEmpty }

    let private typeRootsForWords (index: DiscoveryIndex) (names: string list) =
        names
        |> List.collect (fun name ->
            let definition = index.Words[name].Definition
            (definition.Inputs @ definition.Outputs) |> List.collect (typeNamesIn >> Set.toList))
        |> Set.ofList

    let private contextRecordNode (name: string) (record: RecordDefinition) =
        let node = JsonObject()
        node["name"] <- JsonValue.Create name
        node["kind"] <- JsonValue.Create "record"
        let fields = JsonArray()
        for field in record.Fields do
            let fieldNode = JsonObject()
            fieldNode["name"] <- JsonValue.Create field.Name
            fieldNode["type"] <- JsonValue.Create(Types.format field.Type)
            fields.Add fieldNode
        node["fields"] <- fields
        node

    let private contextScalarNode (name: string) (scalar: ScalarTypeDefinition) =
        let node = JsonObject()
        node["name"] <- JsonValue.Create name
        node["kind"] <- JsonValue.Create "scalar"
        node["baseType"] <- JsonValue.Create(Types.format scalar.BaseType)
        node["validator"] <- scalar.Validator |> Option.map JsonValue.Create |> Option.defaultValue null
        node

    let private contextDocumentation (documentation: string) =
        let normalized = documentation.Replace("\r\n", "\n").Replace('\r', '\n')
        let lines = normalized.Split('\n')
        let firstParagraph =
            lines
            |> Array.takeWhile (String.IsNullOrWhiteSpace >> not)
            |> Array.map (fun line -> line.Trim())
            |> String.concat " "
        let paragraphEndsAt = firstParagraph.Length
        let afterFirstParagraph =
            lines
            |> Array.skipWhile (String.IsNullOrWhiteSpace >> not)
            |> Array.exists (String.IsNullOrWhiteSpace >> not)
        let builder = StringBuilder()
        let mutable index = 0
        let mutable scalarCount = 0
        while index < firstParagraph.Length && scalarCount < 256 do
            let rune = Rune.GetRuneAt(firstParagraph, index)
            builder.Append(rune.ToString()) |> ignore
            index <- index + rune.Utf16SequenceLength
            scalarCount <- scalarCount + 1
        let characterLimitReached = index < paragraphEndsAt
        builder.ToString(), characterLimitReached || afterFirstParagraph

    let private contextWordNode (index: DiscoveryIndex) (name: string) =
        let entry = index.Words[name]
        let definition = entry.Definition
        let node = JsonObject()
        node["name"] <- JsonValue.Create name
        let inputs = JsonArray()
        definition.Inputs |> List.iter (Types.format >> JsonValue.Create >> inputs.Add)
        node["inputs"] <- inputs
        let outputs = JsonArray()
        definition.Outputs |> List.iter (Types.format >> JsonValue.Create >> outputs.Add)
        node["outputs"] <- outputs
        let effects = JsonArray()
        definition.Effects |> Set.iter (JsonValue.Create >> effects.Add)
        node["effects"] <- effects
        node["dependencies"] <- dependenciesFor index name |> Set.toList |> (fun values -> JsonSerializer.SerializeToNode(values))
        let documentation, documentationTruncated = contextDocumentation definition.Documentation
        node["documentation"] <- JsonValue.Create documentation
        node["documentationTruncated"] <- JsonValue.Create documentationTruncated
        node["maturity"] <- JsonValue.Create(if entry.Maturity = LibraryWord then "library" else "project")
        node

    let private contextJson
        (root: string)
        (maxDepth: int)
        (maxWords: int)
        (maxUtf8Bytes: int)
        (words: JsonNode list)
        (types: JsonNode list)
        (omittedWords: int)
        (omittedTypes: int)
        (reasons: string list) =
        let document = JsonObject()
        document["root"] <- JsonValue.Create root
        document["maxDepth"] <- JsonValue.Create maxDepth
        document["maxWords"] <- JsonValue.Create maxWords
        document["maxUtf8Bytes"] <- JsonValue.Create maxUtf8Bytes
        let wordArray = JsonArray()
        words |> List.iter (fun node -> wordArray.Add node)
        document["words"] <- wordArray
        let typeArray = JsonArray()
        types |> List.iter (fun node -> typeArray.Add node)
        document["types"] <- typeArray
        document["wordsOmitted"] <- JsonValue.Create omittedWords
        document["typesOmitted"] <- JsonValue.Create omittedTypes
        document["truncated"] <- JsonValue.Create(omittedWords > 0 || omittedTypes > 0)
        let reasonArray = JsonArray()
        reasons |> List.iter (fun reason -> reasonArray.Add(JsonValue.Create reason))
        document["truncationReasons"] <- reasonArray

        // The byte count is part of the emitted JSON metadata. Iterate to the
        // fixed point so its own digits are included in the exact count.
        let mutable guess = 0
        let mutable complete = false
        let mutable json = ""
        let mutable byteCount = 0
        let mutable attempts = 0
        while not complete && attempts < 8 do
            attempts <- attempts + 1
            document["utf8Bytes"] <- JsonValue.Create guess
            json <- document.ToJsonString jsonOptions
            byteCount <- utf8.GetByteCount json
            if byteCount = guess then complete <- true
            else guess <- byteCount
        if not complete then
            raiseDiscovery "DISCOVERY_CONTEXT_SERIALIZATION" "Could not stabilize the context UTF-8 byte count." (Some root) [ "stable UTF-8 JSON byte count" ] [ string byteCount ]
        json, byteCount

    /// Generate a deterministic breadth-first compact JSON context. Every word
    /// and type declaration is emitted whole; byte truncation never slices JSON.
    let context (index: DiscoveryIndex) root maxDepth maxWords maxUtf8Bytes =
        requireKnownWord index root
        validateBudgets maxDepth maxWords maxUtf8Bytes
        let allWords = reachableWords index root
        let allWordSet = Set.ofList allWords
        let depthWords = breadthFirst (dependenciesFor index) root maxDepth |> List.map fst
        let allTypes = typeRootsForWords index allWords |> namedTypeClosure index.Records index.Scalars

        let typeNodes names =
            names
            |> Set.toList
            |> List.choose (fun name ->
                match index.Records.TryFind name, index.Scalars.TryFind name with
                | Some record, _ -> Some(contextRecordNode name record.Definition)
                | None, Some scalar -> Some(contextScalarNode name scalar.Definition)
                | None, None -> None)

        let makeResult included reasons =
            let includedSet = Set.ofList included
            let selectedTypes = typeRootsForWords index included |> namedTypeClosure index.Records index.Scalars
            let omittedWords = allWordSet.Count - includedSet.Count
            let omittedTypes = Set.difference allTypes selectedTypes |> Set.count
            let wordNodes = included |> List.map (contextWordNode index >> fun node -> node :> JsonNode)
            let declaredTypes = typeNodes selectedTypes |> List.map (fun node -> node :> JsonNode)
            let json, bytes = contextJson root maxDepth maxWords maxUtf8Bytes wordNodes declaredTypes omittedWords omittedTypes reasons
            json, bytes, selectedTypes, omittedWords, omittedTypes

        let initiallyIncluded = [ root ]
        let depthTruncated = depthWords.Length < allWords.Length
        let wordLimited = depthWords.Length > maxWords
        let initialReasons =
            [ if depthTruncated then "maxDepth"
              if wordLimited then "maxWords" ]
        let initial = makeResult initiallyIncluded initialReasons
        let _, initialBytes, _, _, _ = initial
        if initialBytes > maxUtf8Bytes then
            raiseDiscovery "DISCOVERY_CONTEXT_BUDGET_TOO_SMALL" "The UTF-8 byte budget cannot fit the root word and its complete reachable type declarations." (Some root) [ "root context within byte budget" ] [ string maxUtf8Bytes ]

        let candidates = depthWords |> List.skip 1 |> List.truncate (max 0 (maxWords - 1))
        let mutable included = initiallyIncluded
        let mutable current = initial
        let mutable byteTruncated = false
        let mutable stopped = false
        for candidate in candidates do
            if not stopped then
                let trialIncluded = included @ [ candidate ]
                let trial = makeResult trialIncluded initialReasons
                let _, trialBytes, _, _, _ = trial
                if trialBytes <= maxUtf8Bytes then
                    included <- trialIncluded
                    current <- trial
                else
                    byteTruncated <- true
                    stopped <- true

        let reasons = initialReasons @ (if byteTruncated then [ "maxUtf8Bytes" ] else [])
        current <- makeResult included reasons
        let mutable finalizedIncluded = included
        let mutable finalJson = ""
        let mutable finalBytes = 0
        let mutable selectedTypes = Set.empty
        let mutable omittedWords = 0
        let mutable omittedTypes = 0
        let updateFinal () =
            let json, bytes, selected, wordsOmitted, typesOmitted = current
            finalJson <- json
            finalBytes <- bytes
            selectedTypes <- selected
            omittedWords <- wordsOmitted
            omittedTypes <- typesOmitted
        updateFinal ()
        while byteTruncated && finalBytes > maxUtf8Bytes && finalizedIncluded.Length > 1 do
            finalizedIncluded <- finalizedIncluded |> List.take (finalizedIncluded.Length - 1)
            current <- makeResult finalizedIncluded reasons
            updateFinal ()
        if finalBytes > maxUtf8Bytes then
            raiseDiscovery "DISCOVERY_CONTEXT_BUDGET_TOO_SMALL" "The UTF-8 byte budget cannot fit a complete root context." (Some root) [ "complete root context within byte budget" ] [ string maxUtf8Bytes ]
        { Content = finalJson
          Utf8Bytes = finalBytes
          WordsIncluded = finalizedIncluded
          TypesIncluded = Set.toList selectedTypes
          WordsOmitted = omittedWords
          TypesOmitted = omittedTypes
          Truncated = omittedWords > 0 || omittedTypes > 0
          TruncationReasons = reasons }
