namespace AgentLang

open System
open System.Collections.Generic
open System.IO
open System.Numerics
open System.Security.Cryptography
open System.Text

/// Immutable analysis inputs and precomputed strict structural fingerprints.
type VocabularyAnalysisIndex = private { Words: Map<string, WordEntry>; Records: Map<string, RecordEntry>; Scalars: Map<string, ScalarEntry>; Enums: Map<string, EnumEntry>; WordIds: Map<string, string>; Fingerprints: Map<string, string> }

type DuplicateCandidate =
    { FirstWord: string
      SecondWord: string
      Fingerprint: string }

/// Potential static call-site counts, not runtime cost or dynamic invocation counts.
type StaticCallEstimate =
    { RootWord: string
      PrimitiveCallSites: BigInteger
      GeneratedCallSites: BigInteger
      AuthoredInvocationSites: BigInteger
      DynamicCallbackTargets: string list
      DynamicCallbackOccurrences: Map<string, BigInteger>
      DynamicCallbackRepetitionsUnknown: bool }

type private WordExpansion =
    { PrimitiveCallSites: BigInteger
      GeneratedCallSites: BigInteger
      AuthoredInvocationSites: BigInteger
      DynamicCallbackOccurrences: Map<string, BigInteger> }

[<RequireQualifiedAccess>]
module VocabularyAnalysis =
    let private utf8 = UTF8Encoding(false, true)

    let private raiseVocabulary code message word expected actual =
        Diagnostics.raiseError code message word None expected actual

    let private typeNamesIn (typeValue: LangType) =
        let rec collect = function
            | TNamed name -> Set.singleton name
            | TList item | TOption item -> collect item
            | TResult(okType, errorType) -> Set.union (collect okType) (collect errorType)
            | TInt | TFloat | TBool | TString | TUnit | TVar _ -> Set.empty
        collect typeValue

    let private dependenciesFor (scalars: Map<string, ScalarEntry>) (entry: WordEntry) =
        match entry.Builtin with
        | None -> Compiler.dependencies entry.Definition.Body
        | Some(ScalarConstructor scalarName) ->
            scalars.TryFind scalarName
            |> Option.bind (fun scalar -> scalar.Definition.Validator)
            |> Option.map Set.singleton
            |> Option.defaultValue Set.empty
        | Some(BuiltinOp _ | RecordConstructor _ | RecordAccessor _ | ScalarAccessor _ | EnumCaseConstructor _) -> Set.empty

    let private validateGeneratedReference (records: Map<string, RecordEntry>) (scalars: Map<string, ScalarEntry>) (enums: Map<string, EnumEntry>) wordName builtin =
        match builtin with
        | None | Some(BuiltinOp _) -> ()
        | Some(RecordConstructor recordName) ->
            if not (records.ContainsKey recordName) then
                raiseVocabulary "VOCABULARY_UNKNOWN_RECORD" $"Generated word '{wordName}' references missing record '{recordName}'." (Some wordName) [ "known record" ] [ recordName ]
        | Some(RecordAccessor(recordName, fieldName)) ->
            match records.TryFind recordName with
            | None -> raiseVocabulary "VOCABULARY_UNKNOWN_RECORD" $"Generated word '{wordName}' references missing record '{recordName}'." (Some wordName) [ "known record" ] [ recordName ]
            | Some record when record.Definition.Fields |> List.exists (fun field -> field.Name = fieldName) -> ()
            | Some _ -> raiseVocabulary "VOCABULARY_UNKNOWN_FIELD" $"Generated word '{wordName}' references missing field '{recordName}.{fieldName}'." (Some wordName) [ "known record field" ] [ fieldName ]
        | Some(ScalarConstructor scalarName | ScalarAccessor scalarName) ->
            if not (scalars.ContainsKey scalarName) then
                raiseVocabulary "VOCABULARY_UNKNOWN_SCALAR" $"Generated word '{wordName}' references missing scalar '{scalarName}'." (Some wordName) [ "known scalar" ] [ scalarName ]

        | Some(EnumCaseConstructor(enumName, caseName)) ->
            match enums.TryFind enumName with
            | None -> raiseVocabulary "VOCABULARY_UNKNOWN_ENUM" $"Generated word '{wordName}' references missing enum '{enumName}'." (Some wordName) [ "known enum" ] [ enumName ]
            | Some enumEntry when enumEntry.Definition.Cases |> List.contains caseName ->
                let expectedName = $"{enumName}.{caseName}"
                if wordName <> expectedName then
                    raiseVocabulary "VOCABULARY_ENUM_CONSTRUCTOR_KEY" $"Generated enum constructor '{wordName}' does not match its enum and case identity." (Some wordName) [ expectedName ] [ wordName ]
            | Some _ -> raiseVocabulary "VOCABULARY_UNKNOWN_ENUM_CASE" $"Generated word '{wordName}' references missing case '{enumName}.{caseName}'." (Some wordName) [ "known enum case" ] [ caseName ]

    let private validateNamedReferences (records: Map<string, RecordEntry>) (scalars: Map<string, ScalarEntry>) (enums: Map<string, EnumEntry>) owner typeValue =
        for typeName in typeNamesIn typeValue do
            if not (records.ContainsKey typeName || scalars.ContainsKey typeName || enums.ContainsKey typeName) then
                raiseVocabulary "VOCABULARY_UNKNOWN_TYPE" $"'{owner}' refers to undeclared nominal type '{typeName}'." (Some owner) [ "known record, scalar, or enum type" ] [ typeName ]

    let private writeInt (writer: BinaryWriter) (value: int) = writer.Write value

    let private writeString (writer: BinaryWriter) (value: string) =
        let bytes = utf8.GetBytes value
        writeInt writer bytes.Length
        writer.Write bytes

    let private writeType (writer: BinaryWriter) (typeValue: LangType) =
        let rec encode = function
            | TInt -> writer.Write 1uy
            | TFloat -> writer.Write 2uy
            | TBool -> writer.Write 3uy
            | TString -> writer.Write 4uy
            | TUnit -> writer.Write 5uy
            | TList item -> writer.Write 6uy; encode item
            | TOption item -> writer.Write 7uy; encode item
            | TResult(okType, errorType) -> writer.Write 8uy; encode okType; encode errorType
            | TNamed name -> writer.Write 9uy; writeString writer name
            | TVar name -> writer.Write 10uy; writeString writer name
        encode typeValue

    let private writeTypeList (writer: BinaryWriter) (types: LangType list) =
        writeInt writer types.Length
        types |> List.iter (writeType writer)

    let private writeLiteral (writer: BinaryWriter) = function
        | LInt value -> writer.Write 1uy; writer.Write value
        | LFloat value -> writer.Write 2uy; writer.Write(BitConverter.DoubleToInt64Bits value)
        | LBool value -> writer.Write 3uy; writer.Write value
        | LString value -> writer.Write 4uy; writeString writer value
        | LUnit -> writer.Write 5uy

    let private writeWordIdentity (index: VocabularyAnalysisIndex) (writer: BinaryWriter) (name: string) =
        let entry = index.Words[name]
        match entry.Builtin with
        | None ->
            writer.Write 1uy
            writeString writer index.WordIds[name]
        | Some(BuiltinOp operation) ->
            writer.Write 2uy
            writeString writer operation
        | Some(RecordConstructor recordName) ->
            writer.Write 3uy
            writeString writer recordName
        | Some(RecordAccessor(recordName, fieldName)) ->
            writer.Write 4uy
            writeString writer recordName
            writeString writer fieldName
        | Some(ScalarConstructor scalarName) ->
            writer.Write 5uy
            writeString writer scalarName
        | Some(ScalarAccessor scalarName) ->
            writer.Write 6uy
            writeString writer scalarName
        | Some(EnumCaseConstructor(enumName, caseName)) ->
            writer.Write 7uy
            writeString writer enumName
            writeString writer caseName

    let private writeExpressions (index: VocabularyAnalysisIndex) (writer: BinaryWriter) (expressions: Expr list) =
        let rec writeList (values: Expr list) : unit =
            writeInt writer values.Length
            values |> List.iter writeExpression
        and writeExpression (expression: Expr) : unit =
            match expression with
            | Push(literal, _) ->
                writer.Write 1uy
                writeLiteral writer literal
            | Call(name, _) ->
                writer.Write 2uy
                writeWordIdentity index writer name
            | ConstructContainer(kind, types, _) ->
                writer.Write 3uy
                match kind with
                | ListEmpty -> writer.Write 1uy
                | ListSingleton -> writer.Write 2uy
                | OptionNone -> writer.Write 3uy
                | OptionSome -> writer.Write 4uy
                | ResultOk -> writer.Write 5uy
                | ResultError -> writer.Write 6uy
                writeTypeList writer types
            | MapList(name, _) ->
                writer.Write 4uy
                writeWordIdentity index writer name
            | FilterList(name, _) ->
                writer.Write 5uy
                writeWordIdentity index writer name
            | EachList(name, _) ->
                writer.Write 6uy
                writeWordIdentity index writer name
            | FoldList(name, _) ->
                writer.Write 13uy
                writeWordIdentity index writer name
            | Let(name, _) ->
                writer.Write 7uy
                writeString writer name
            | Load(name, _) ->
                writer.Write 8uy
                writeString writer name
            | If(thenBranch, elseBranch, _) ->
                writer.Write 9uy
                writeList thenBranch
                writeList elseBranch
            | Scope(innerBody, _) ->
                writer.Write 12uy
                writeList innerBody
            | MatchOption(name, someBranch, noneBranch, _) ->
                writer.Write 10uy
                writeString writer name
                writeList someBranch
                writeList noneBranch
            | MatchResult(okName, errorName, okBranch, errorBranch, _) ->
                writer.Write 11uy
                writeString writer okName
                writeString writer errorName
                writeList okBranch
                writeList errorBranch
            | MatchEnum(cases, _) ->
                writer.Write 14uy
                writeInt writer cases.Length
                for caseName, body in cases do
                    writeString writer caseName
                    writeList body
        writeList expressions

    let private fingerprintDefinition (index: VocabularyAnalysisIndex) (definition: WordDefinition) =
        use stream = new MemoryStream()
        use writer = new BinaryWriter(stream, Encoding.UTF8, true)
        writeString writer "AgentLang.VocabularyFingerprint.v1"
        writeTypeList writer definition.Inputs
        writeTypeList writer definition.Outputs
        let effects = definition.Effects |> Set.toList
        writeInt writer effects.Length
        effects |> List.iter (writeString writer)
        writeExpressions index writer definition.Body
        writer.Flush()
        SHA256.HashData(stream.ToArray()) |> Convert.ToHexString |> fun hash -> hash.ToLowerInvariant()

    /// Create an immutable analysis index from a complete effective dictionary.
    /// Compiler type/effect checking remains the authority; this validates only
    /// map/identity/reference completeness needed by structural analysis.
    let build
        (words: Map<string, WordEntry>)
        (records: Map<string, RecordEntry>)
        (scalars: Map<string, ScalarEntry>)
        (enums: Map<string, EnumEntry>)
        (wordIds: Map<string, string>)
        : VocabularyAnalysisIndex =
        for KeyValue(key, entry) in words do
            if key <> entry.Definition.Name then
                raiseVocabulary "VOCABULARY_WORD_KEY_MISMATCH" $"Word map key '{key}' does not match definition name '{entry.Definition.Name}'." (Some key) [ key ] [ entry.Definition.Name ]
            validateGeneratedReference records scalars enums key entry.Builtin
            for typeValue in entry.Definition.Inputs @ entry.Definition.Outputs do
                validateNamedReferences records scalars enums key typeValue

        for KeyValue(key, entry) in records do
            if key <> entry.Definition.Name then
                raiseVocabulary "VOCABULARY_RECORD_KEY_MISMATCH" $"Record map key '{key}' does not match definition name '{entry.Definition.Name}'." (Some key) [ key ] [ entry.Definition.Name ]
            for field in entry.Definition.Fields do validateNamedReferences records scalars enums key field.Type

        for KeyValue(key, entry) in scalars do
            if key <> entry.Definition.Name then
                raiseVocabulary "VOCABULARY_SCALAR_KEY_MISMATCH" $"Scalar map key '{key}' does not match definition name '{entry.Definition.Name}'." (Some key) [ key ] [ entry.Definition.Name ]
            validateNamedReferences records scalars enums key entry.Definition.BaseType
            match entry.Definition.Validator with
            | Some validator when not (words.ContainsKey validator) ->
                raiseVocabulary "VOCABULARY_UNKNOWN_VALIDATOR" $"Scalar '{key}' names missing validator word '{validator}'." (Some key) [ "known validator word" ] [ validator ]
            | None | Some _ -> ()

        for KeyValue(key, entry) in enums do
            if key <> entry.Definition.Name then
                raiseVocabulary "VOCABULARY_ENUM_KEY_MISMATCH" $"Enum map key '{key}' does not match definition name '{entry.Definition.Name}'." (Some key) [ key ] [ entry.Definition.Name ]
            let duplicateCase = entry.Definition.Cases |> List.groupBy id |> List.tryFind (fun (_, cases) -> cases.Length > 1)
            match duplicateCase with
            | Some(caseName, _) -> raiseVocabulary "VOCABULARY_DUPLICATE_ENUM_CASE" $"Enum '{key}' declares case '{caseName}' more than once." (Some key) [ "unique enum case names" ] [ caseName ]
            | None -> ()

        let recordNames = records |> Map.toSeq |> Seq.map fst |> Set.ofSeq
        let scalarNames = scalars |> Map.toSeq |> Seq.map fst |> Set.ofSeq
        let enumNames = enums |> Map.toSeq |> Seq.map fst |> Set.ofSeq
        let duplicatedTypeNames =
            Set.unionMany [ recordNames; scalarNames; enumNames ]
            |> Set.filter (fun name -> [ recordNames.Contains name; scalarNames.Contains name; enumNames.Contains name ] |> List.filter id |> List.length > 1)
        match duplicatedTypeNames |> Set.toList with
        | name :: _ -> raiseVocabulary "VOCABULARY_DUPLICATE_TYPE" $"'{name}' is declared by more than one nominal type definition." (Some name) [ "unique nominal type name" ] [ "record"; "scalar"; "enum" ]
        | [] -> ()

        let authoredNames =
            words
            |> Map.toList
            |> List.choose (fun (name, entry) -> if entry.Builtin.IsNone then Some name else None)
            |> Set.ofList
        for KeyValue(name, identity) in wordIds do
            if not (words.ContainsKey name) then
                raiseVocabulary "VOCABULARY_UNKNOWN_ID_WORD" $"Stable identity is assigned to unknown word '{name}'." (Some name) [ "known word" ] [ name ]
            if String.IsNullOrWhiteSpace identity then
                raiseVocabulary "VOCABULARY_INVALID_WORD_ID" $"Word '{name}' has an empty stable identity." (Some name) [ "nonempty unique word ID" ] [ identity ]
        for name in authoredNames do
            if not (wordIds.ContainsKey name) then
                raiseVocabulary "VOCABULARY_MISSING_WORD_ID" $"Authored word '{name}' has no stable word ID." (Some name) [ "stable word ID" ] []
        let duplicateIds =
            authoredNames
            |> Set.toList
            |> List.groupBy (fun name -> wordIds[name])
            |> List.tryFind (fun (_, names) -> names.Length > 1)
        match duplicateIds with
        | Some(identity, names) -> raiseVocabulary "VOCABULARY_DUPLICATE_WORD_ID" $"Stable ID '{identity}' is assigned to more than one authored word." (List.head names |> Some) [ "unique word IDs" ] names
        | None -> ()

        for KeyValue(name, entry) in words do
            for dependency in dependenciesFor scalars entry do
                if not (words.ContainsKey dependency) then
                    raiseVocabulary "VOCABULARY_UNKNOWN_WORD" $"Word '{name}' refers to missing word '{dependency}'." (Some name) [ "known word reference" ] [ dependency ]

        let partial =
            { Words = words
              Records = records
              Scalars = scalars
              Enums = enums
              WordIds = wordIds
              Fingerprints = Map.empty }
        let fingerprints =
            words
            |> Map.toList
            |> List.choose (fun (name, entry) ->
                if entry.Builtin.IsNone then Some(name, fingerprintDefinition partial entry.Definition)
                else None)
            |> Map.ofList
        { partial with Fingerprints = fingerprints }

    /// Return the lowercase SHA-256 fingerprint for an authored word.
    let fingerprint (index: VocabularyAnalysisIndex) (name: string) =
        match index.Fingerprints.TryFind name with
        | Some hash -> hash
        | None when index.Words.ContainsKey name ->
            raiseVocabulary "VOCABULARY_NOT_AUTHORED_WORD" $"'{name}' is generated or primitive and has no authored-word fingerprint." (Some name) [ "authored word" ] [ name ]
        | None -> raiseVocabulary "VOCABULARY_UNKNOWN_ROOT" $"Unknown word '{name}'." (Some name) [ "known authored word" ] [ name ]

    let private pairs names =
        let rec loop = function
            | [] -> []
            | name :: rest -> (rest |> List.map (fun other -> name, other)) @ loop rest
        loop names

    /// List pairwise strict structural duplicate candidates in stable name order.
    let duplicateCandidates (index: VocabularyAnalysisIndex) =
        index.Fingerprints
        |> Map.toList
        |> List.groupBy snd
        |> List.collect (fun (hash, members) ->
            let names = members |> List.map fst |> List.sort
            pairs names
            |> List.map (fun (first, second) ->
                { FirstWord = first
                  SecondWord = second
                  Fingerprint = hash }))
        |> List.sortBy (fun candidate -> candidate.FirstWord, candidate.SecondWord)

    let private emptyExpansion =
        { PrimitiveCallSites = BigInteger.Zero
          GeneratedCallSites = BigInteger.Zero
          AuthoredInvocationSites = BigInteger.Zero
          DynamicCallbackOccurrences = Map.empty }

    let private addCounts (left: Map<string, BigInteger>) (right: Map<string, BigInteger>) =
        Map.fold (fun (counts: Map<string, BigInteger>) (name: string) (amount: BigInteger) ->
            let total =
                match counts.TryFind name with
                | Some existing -> existing + amount
                | None -> amount
            counts.Add(name, total)) left right

    let private addExpansion (left: WordExpansion) (right: WordExpansion) : WordExpansion =
        { PrimitiveCallSites = left.PrimitiveCallSites + right.PrimitiveCallSites
          GeneratedCallSites = left.GeneratedCallSites + right.GeneratedCallSites
          AuthoredInvocationSites = left.AuthoredInvocationSites + right.AuthoredInvocationSites
          DynamicCallbackOccurrences = addCounts left.DynamicCallbackOccurrences right.DynamicCallbackOccurrences }

    let private addAuthoredInvocation (expansion: WordExpansion) : WordExpansion =
        { expansion with AuthoredInvocationSites = expansion.AuthoredInvocationSites + BigInteger.One }

    let private addGeneratedInvocation (expansion: WordExpansion) : WordExpansion =
        { expansion with GeneratedCallSites = expansion.GeneratedCallSites + BigInteger.One }

    let private addCallbackTarget (target: string) (expansion: WordExpansion) : WordExpansion =
        let previous = expansion.DynamicCallbackOccurrences.TryFind target |> Option.defaultValue BigInteger.Zero
        { expansion with DynamicCallbackOccurrences = expansion.DynamicCallbackOccurrences.Add(target, previous + BigInteger.One) }

    /// Expand authored call sites without executing the dictionary. Repeated
    /// references retain multiplicity while each DAG node's totals are memoized.
    let staticCallEstimate (index: VocabularyAnalysisIndex) (root: string) =
        if not (index.Words.ContainsKey root) then
            raiseVocabulary "VOCABULARY_UNKNOWN_ROOT" $"Unknown word '{root}'." (Some root) [ "known word" ] [ root ]
        let cache = Dictionary<string, WordExpansion>(StringComparer.Ordinal)

        let rec expandWord (active: Set<string>) (name: string) =
            if active.Contains name then
                raiseVocabulary "VOCABULARY_RECURSIVE_EXPANSION" $"Cannot produce a finite static call estimate for recursive word '{name}'." (Some name) [ "acyclic static call graph" ] (active |> Set.toList)
            match cache.TryGetValue name with
            | true, expansion -> expansion
            | false, _ ->
                let entry = index.Words[name]
                let nextActive = Set.add name active
                let computed =
                    match entry.Builtin with
                    | Some(BuiltinOp _) -> { emptyExpansion with PrimitiveCallSites = BigInteger.One }
                    | Some(RecordConstructor _ | RecordAccessor _ | ScalarAccessor _ | EnumCaseConstructor _) -> addGeneratedInvocation emptyExpansion
                    | Some(ScalarConstructor scalarName) ->
                        let constructor = addGeneratedInvocation emptyExpansion
                        match index.Scalars[scalarName].Definition.Validator with
                        | None -> constructor
                        | Some validator ->
                            let validatorExpansion = expandWord nextActive validator
                            let validatorExpansion =
                                if index.Words[validator].Builtin.IsNone then addAuthoredInvocation validatorExpansion
                                else validatorExpansion
                            addExpansion constructor validatorExpansion
                    | None ->
                        let rec expandExpressions expressions =
                            expressions |> List.fold (fun total expression -> addExpansion total (expandExpression nextActive expression)) emptyExpansion
                        and expandExpression activeWords = function
                            | Push _ | ConstructContainer _ | Let _ | Load _ -> emptyExpansion
                            | Call(target, _) ->
                                let targetExpansion = expandWord activeWords target
                                if index.Words[target].Builtin.IsNone then addAuthoredInvocation targetExpansion else targetExpansion
                            | MapList(target, _) | FilterList(target, _) | EachList(target, _) | FoldList(target, _) ->
                                let targetExpansion = expandWord activeWords target
                                let targetExpansion = if index.Words[target].Builtin.IsNone then addAuthoredInvocation targetExpansion else targetExpansion
                                addCallbackTarget target targetExpansion
                            | If(thenBranch, elseBranch, _) ->
                                addExpansion (expandExpressions thenBranch) (expandExpressions elseBranch)
                            | Scope(innerBody, _) -> expandExpressions innerBody
                            | MatchOption(_, someBranch, noneBranch, _) ->
                                addExpansion (expandExpressions someBranch) (expandExpressions noneBranch)
                            | MatchResult(_, _, okBranch, errorBranch, _) ->
                                addExpansion (expandExpressions okBranch) (expandExpressions errorBranch)
                            | MatchEnum(cases, _) ->
                                cases |> List.map snd |> List.map expandExpressions |> List.fold addExpansion emptyExpansion
                        expandExpressions entry.Definition.Body
                cache[name] <- computed
                computed

        let expansion = expandWord Set.empty root
        { RootWord = root
          PrimitiveCallSites = expansion.PrimitiveCallSites
          GeneratedCallSites = expansion.GeneratedCallSites
          AuthoredInvocationSites = expansion.AuthoredInvocationSites
          DynamicCallbackTargets = expansion.DynamicCallbackOccurrences |> Map.toList |> List.map fst
          DynamicCallbackOccurrences = expansion.DynamicCallbackOccurrences
          DynamicCallbackRepetitionsUnknown = not expansion.DynamicCallbackOccurrences.IsEmpty }
